using System.Net;
using System.Text.Json;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Helper;
using ServiceLib.Models;
using ServiceLib.Models.Configs;
using ServiceLib.Services;
using ServiceLib.ViewModels;

namespace AoGPN.Services;

// ─────────────────────────────────────────────────────────────────────────
// DashboardSettingsService — Ayarlar formu iş mantığı (P0 Faz 2, 2. Dalga)
//
// MainWindow.xaml.cs içindeki Ayarlar kümesi buraya taşındı: dashboard
// "Ayarlar" formunun okunması (PushSettingsAsync), doğrulanıp kaydedilmesi
// (SaveSettingsAsync → SaveSettingsCoreAsync), görünüm (effects/language) ve
// sistem-proxy durumu yayınları. UI kanalına yalnızca ctor'a enjekte edilen
// executeScript (WebView2 yürütme — MainWindow.ExecuteScriptSafelyAsync,
// dispatcher marshal'ı orada yapılır) ve isWebViewReady bayrağıyla dokunur;
// dialog/tray/yaşam-döngüsü MainWindow'da kalır. İş kuralları BİREBİR
// korunmuştur (taşınan gövdelerde hiçbir satır değişmedi).
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Ayarlar formu (read/persist/push) iş mantığı. MainWindow tarafından
/// kurulur; IDashboardBridge ayarlar üyeleri bu servise delege edilir.
/// </summary>
internal sealed class DashboardSettingsService
{
    private readonly Func<string, Task> _executeScript;
    private readonly Func<bool> _isWebViewReady;
    private readonly Func<string> _readTransport;
    private readonly SystemProxyOnlyService _proxyOnlyService;

    // Route-test results are pushed to the renderer with camelCase keys to match the
    // rest of the host→renderer payloads.
    private static readonly JsonSerializerOptions RouteTestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private string _lastSystemProxySignature = "";

    public DashboardSettingsService(
        Func<string, Task> executeScript,
        Func<bool> isWebViewReady,
        Func<string> readTransport,
        SystemProxyOnlyService proxyOnlyService)
    {
        _executeScript = executeScript ?? throw new ArgumentNullException(nameof(executeScript));
        _isWebViewReady = isWebViewReady ?? throw new ArgumentNullException(nameof(isWebViewReady));
        _readTransport = readTransport ?? throw new ArgumentNullException(nameof(readTransport));
        _proxyOnlyService = proxyOnlyService ?? throw new ArgumentNullException(nameof(proxyOnlyService));
    }

    private Task ExecuteScriptSafelyAsync(string script) => _executeScript(script);
    private bool WebViewReady => _isWebViewReady();

    /// <summary>
    /// Pushes the visual-effects tier (full / balanced / reduced) to the
    /// dashboard. Legacy configs (empty EffectsMode plus the old ReduceEffects
    /// switch) are normalized and persisted so the file self-migrates; the tier
    /// applies immediately on the renderer side, no restart needed. The host
    /// config is authoritative — the renderer never persists it.
    /// </summary>
    public async Task PushEffectsTierAsync()
    {
        var config = AppManager.Instance.Config;
        var tier = NormalizeEffectsMode(config.GuiItem.EffectsMode);
        config.GuiItem.ReduceEffects = tier == "reduced";
        if (!string.Equals(config.GuiItem.EffectsMode, tier, StringComparison.Ordinal))
        {
            config.GuiItem.EffectsMode = tier;
            ConfigSaveQueue.RequestSave(config);
        }
        await ExecuteScriptSafelyAsync(
            $"if(typeof setEffectsTier==='function'){{setEffectsTier({JsonSerializer.Serialize(tier)},false)}}");
    }

    /// <summary>
    /// Accepts "full", "balanced" or "reduced"; anything else falls back to
    /// "full" (or "reduced" when the legacy ReduceEffects switch was on and the
    /// mode was never set) — a null/empty mode from an older config file migrates
    /// without losing the user's previous choice.
    /// </summary>
    internal static string NormalizeEffectsMode(string? mode)
    {
        if (mode is "balanced" or "reduced")
        {
            return mode;
        }
        return AppManager.Instance.Config.GuiItem.ReduceEffects ? "reduced" : "full";
    }

    /// <summary>
    /// Pushes the current WPF UI language to the dashboard so the language dropdown
    /// and any future localised strings in the WebView stay in sync.
    /// </summary>
    public async Task PushLanguageAsync()
    {
        var lang = AppManager.Instance.Config.UiItem.CurrentLanguage ?? "en";
        await ExecuteScriptSafelyAsync(
            $"if(typeof applyLanguage==='function'){{applyLanguage('{lang}')}}");
    }

    /// <summary>
    /// Pushes the running build/version to the dashboard so the About &amp; Help
    /// page always reports the real application version (it lifts it straight out
    /// of the assembly, exactly like the window title and splash screen).
    /// </summary>
    internal async Task PushAppInfoAsync()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = Utils.GetVersionInfo(),
            appName = "AO GPN Desktop",
            arch = RuntimeInformation.ProcessArchitecture.ToString()
        });
        await ExecuteScriptSafelyAsync($"window.setAppInfo?.({payload});");
    }

    /// <summary>
    /// Pushes the full AoGPN option set (mirroring OptionSettingViewModel) plus the
    /// Global.* combo lists to the dashboard Settings view. Called on navigation
    /// completion and on demand via get_settings.
    /// </summary>
    public async Task PushSettingsAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        var config = AppManager.Instance.Config;
        var inbound = config.Inbound.First();
        var coreTypes = new Dictionary<int, string>();
        foreach (var item in config.CoreTypeItem ?? [])
        {
            coreTypes[(int)item.ConfigType] = item.CoreType.ToString();
        }

        var payload = new
        {
            core = new
            {
                localPort = inbound.LocalPort,
                secondLocalPortEnabled = inbound.SecondLocalPortEnabled,
                udpEnabled = inbound.UdpEnabled,
                sniffingEnabled = inbound.SniffingEnabled,
                destOverride = inbound.DestOverride ?? [],
                routeOnly = inbound.RouteOnly,
                allowLANConn = inbound.AllowLANConn,
                newPort4LAN = inbound.NewPort4LAN,
                user = inbound.User ?? string.Empty,
                pass = inbound.Pass ?? string.Empty,
                logEnabled = config.CoreBasicItem.LogEnabled,
                loglevel = config.CoreBasicItem.Loglevel ?? string.Empty,
                defFingerprint = config.CoreBasicItem.DefFingerprint ?? string.Empty,
                defUserAgent = config.CoreBasicItem.DefUserAgent ?? string.Empty,
                sendThrough = config.CoreBasicItem.SendThrough ?? string.Empty,
                bindInterface = config.CoreBasicItem.BindInterface ?? string.Empty,
                mux4SboxProtocol = config.Mux4SboxItem.Protocol ?? string.Empty,
                enableCacheFile4Sbox = config.CoreBasicItem.EnableCacheFile4Sbox,
                hyUpMbps = config.HysteriaItem.UpMbps,
                hyDownMbps = config.HysteriaItem.DownMbps,
                enableFragment = config.CoreBasicItem.EnableFragment,
                enableFinalFragment = config.CoreBasicItem.EnableFinalFragment,
                fragmentPackets = config.Fragment4RayItem?.Packets ?? string.Empty,
                fragmentLengths = Utils.List2String(config.Fragment4RayItem?.Lengths),
                fragmentDelays = Utils.List2String(config.Fragment4RayItem?.Delays),
                fragmentMaxSplit = config.Fragment4RayItem?.MaxSplit ?? string.Empty,
            },
            general = new
            {
                autoRun = config.GuiItem.AutoRun,
                enableStatistics = config.GuiItem.EnableStatistics,
                displayRealTimeSpeed = config.GuiItem.DisplayRealTimeSpeed,
                keepOlderDedupl = config.GuiItem.KeepOlderDedupl,
                enableAutoAdjustMainLvColWidth = config.UiItem.EnableAutoAdjustMainLvColWidth,
                autoHideStartup = config.UiItem.AutoHideStartup,
                hide2TrayWhenClose = config.UiItem.Hide2TrayWhenClose,
                minimize2Tray = config.UiItem.Minimize2Tray,
                enableDragDropSort = config.UiItem.EnableDragDropSort,
                doubleClick2Activate = config.UiItem.DoubleClick2Activate,
                enableHWA = config.GuiItem.EnableHWA,
                verboseLogEnabled = config.GuiItem.EnableVerboseLog,
                effectsMode = NormalizeEffectsMode(config.GuiItem.EffectsMode),
                reduceEffects = config.GuiItem.ReduceEffects,
                rootCertProvider = config.GuiItem.RootCertProvider ?? string.Empty,
                autoUpdateInterval = config.GuiItem.AutoUpdateInterval,
                trayMenuServersLimit = config.GuiItem.TrayMenuServersLimit,
                currentFontFamily = config.UiItem.CurrentFontFamily ?? string.Empty,
                mixedConcurrencyCount = config.SpeedTestItem.MixedConcurrencyCount,
                speedTestTimeout = config.SpeedTestItem.SpeedTestTimeout,
                speedTestUrl = config.SpeedTestItem.SpeedTestUrl ?? string.Empty,
                speedPingTestUrl = config.SpeedTestItem.SpeedPingTestUrl ?? string.Empty,
                udpTestTarget = config.SpeedTestItem.UdpTestTarget ?? string.Empty,
                ipapiUrl = config.SpeedTestItem.IPAPIUrl ?? string.Empty,
                subConvertUrl = config.ConstItem.SubConvertUrl ?? string.Empty,
                geoFileSourceUrl = config.ConstItem.GeoSourceUrl ?? string.Empty,
                srsFileSourceUrl = config.ConstItem.SrsSourceUrl ?? string.Empty,
                routingRulesSourceUrl = config.ConstItem.RouteRulesTemplateSourceUrl ?? string.Empty,
            },
            connection = new
            {
                mode = config.ConnectionItem.Mode switch
                {
                    SplitTunnelViewModel.ModeManual => "manual",
                    SplitTunnelViewModel.ModeVpn => "vpn",
                    _ => "off",
                },
                transport = _readTransport(),
                protocolPreference = ConnectionProtocolPreference.Normalize(config.ConnectionItem.ProtocolPreference),
                invertManualRouting = config.ConnectionItem.InvertManualRouting,
                autoConnectOnGameStart = config.ConnectionItem.AutoConnectOnGameStart,
                autoReconnectEnabled = config.ConnectionItem.AutoReconnectEnabled,
                autoReconnectMaxAttempts = config.ConnectionItem.AutoReconnectMaxAttempts,
                gpnEnableRecoveryWatch = config.GuiItem.GpnEnableRecoveryWatch,
                gpnEnableWarpAutoRecover = config.GuiItem.GpnEnableWarpAutoRecover,
                gpnEnableFailover = config.GuiItem.GpnEnableFailover,
            },
            systemProxy = new
            {
                sysProxyType = (int)config.SystemProxyItem.SysProxyType,
                effectiveSysProxyType = (int)SystemProxyPolicy.ResolveEffectiveType(config),
                connectionOwnsProxy = SystemProxyPolicy.ConnectionNeedsSystemProxy(config),
                notProxyLocalAddress = config.SystemProxyItem.NotProxyLocalAddress,
                systemProxyAdvancedProtocol = config.SystemProxyItem.SystemProxyAdvancedProtocol ?? string.Empty,
                systemProxyExceptions = config.SystemProxyItem.SystemProxyExceptions ?? string.Empty,
                customSystemProxyPacPath = config.SystemProxyItem.CustomSystemProxyPacPath ?? string.Empty,
                customSystemProxyScriptPath = config.SystemProxyItem.CustomSystemProxyScriptPath ?? string.Empty,
            },
            tun = new
            {
                enableTun = config.TunModeItem.EnableTun,
                tunAutoRoute = config.TunModeItem.AutoRoute,
                tunStrictRoute = config.TunModeItem.StrictRoute,
                tunStack = config.TunModeItem.Stack ?? string.Empty,
                tunMtu = config.TunModeItem.Mtu,
                tunEnableIPv6Address = config.TunModeItem.EnableIPv6Address,
                tunIcmpRouting = config.TunModeItem.IcmpRouting ?? string.Empty,
                tunEnableLegacyProtect = config.TunModeItem.EnableLegacyProtect,
                tunRouteExcludeAddress = Utils.List2String(config.TunModeItem.RouteExcludeAddress, true),
                tunIPv4Address = config.TunModeItem.IPv4Address ?? string.Empty,
                tunIPv6Address = config.TunModeItem.IPv6Address ?? string.Empty,
            },
            coreType = new
            {
                coreType1 = coreTypes.GetValueOrDefault(1, string.Empty),
                coreType2 = coreTypes.GetValueOrDefault(2, string.Empty),
                coreType3 = coreTypes.GetValueOrDefault(3, string.Empty),
                coreType4 = coreTypes.GetValueOrDefault(4, string.Empty),
                coreType5 = coreTypes.GetValueOrDefault(5, string.Empty),
                coreType6 = coreTypes.GetValueOrDefault(6, string.Empty),
                coreType7 = coreTypes.GetValueOrDefault(7, string.Empty),
                coreType9 = coreTypes.GetValueOrDefault(9, string.Empty),
            },
            options = new
            {
                destOverrideProtocols = Global.destOverrideProtocols,
                logLevels = Global.LogLevels,
                fingerprints = Global.Fingerprints,
                userAgents = Global.UserAgent,
                singboxMuxs = Global.SingboxMuxs,
                tunMtus = Global.TunMtus.Select(t => t.ToString()).ToList(),
                tunStacks = Global.TunStacks,
                tunIcmpRoutingPolicies = Global.TunIcmpRoutingPolicies,
                tunIPv4Addresses = Global.TunIPv4Address,
                tunIPv6Addresses = Global.TunIPv6Address,
                fragmentPacketsOptions = Global.FragmentPacketsOptions,
                coreTypes = Global.CoreTypes,
                speedTestUrls = Global.SpeedTestUrls,
                speedPingTestUrls = Global.SpeedPingTestUrls,
                udpTestTargets = Global.UdpTestTargets,
                subConvertUrls = Global.SubConvertUrls,
                geoFilesSources = Global.GeoFilesSources,
                singboxRulesetSources = Global.SingboxRulesetSources,
                routingRulesSources = Global.RoutingRulesSources,
                ipapiUrls = Global.IPAPIUrls,
                rootCertProviders = Global.RootCertProviders,
                ieProxyProtocols = Global.IEProxyProtocols,
                mixedConcurrencyCounts = Enumerable.Range(2, 7).Select(i => i.ToString()).ToList(),
                speedTestTimeouts = Enumerable.Range(2, 5).Select(i => (i * 5).ToString()).ToList(),
            },
            platform = new
            {
                isWindows = Utils.IsWindows(),
                isLinux = Utils.IsLinux(),
                isMacOS = Utils.IsMacOS(),
                isAdmin = Utils.IsAdministrator(),
            },
        };

        var json = JsonSerializer.Serialize(payload);
        await ExecuteScriptSafelyAsync($"window.applySettings({json});");
        await PushSystemProxyStateAsync(force: true);
    }

    /// <summary>
    /// Applies the dashboard Settings form to the config using the same validation and
    /// persistence flow as the native OptionSettingViewModel, then performs the system
    /// proxy / TUN side effects the native status bar would do on change.
    /// </summary>
    public async Task SaveSettingsAsync(JsonElement root)
    {
        try
        {
            await SaveSettingsCoreAsync(root);
        }
        catch (Exception ex)
        {
            // Ack the failure so the renderer can re-enable its Save button; a save
            // that silently dies would leave the form stuck in the saving state.
            Logging.SaveLog("AoGPN settings save failed", ex);
            await NotifySettingsSaveAsync(false, "Failed to save settings");
        }
    }

    private async Task SaveSettingsCoreAsync(JsonElement root)
    {
        if (!root.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
        {
            await NotifySettingsSaveAsync(false, "Invalid settings payload");
            return;
        }

        var config = AppManager.Instance.Config;

        // Local SOCKS port validation mirrors OptionSettingViewModel.SaveSettingAsync.
        var localPort = GetSettingsInt(settings, "localPort", 0);
        if (localPort <= 0 || localPort >= Global.MaxPort)
        {
            await NotifySettingsSaveAsync(false, "Fill in the local listening port");
            return;
        }

        var fragmentLengths = Utils.String2List(GetSettingsString(settings, "fragmentLengths")) ?? [];
        var fragmentDelays = Utils.String2List(GetSettingsString(settings, "fragmentDelays")) ?? [];
        var fragmentMaxSplit = GetSettingsString(settings, "fragmentMaxSplit");
        if (fragmentLengths.Any(item => !Utils.TryParseRange(item, 0, int.MaxValue, out _, out _))
            || fragmentDelays.Any(item => !Utils.TryParseRange(item, 0, int.MaxValue, out _, out _))
            || (fragmentMaxSplit.IsNotEmpty() && !Utils.TryParseMaxSplit(fragmentMaxSplit, 0, 10000, out _, out _)))
        {
            await NotifySettingsSaveAsync(false, "Fragment parameter error");
            return;
        }

        var oldEnableStatistics = config.GuiItem.EnableStatistics;
        var oldDisplayRealTimeSpeed = config.GuiItem.DisplayRealTimeSpeed;
        var oldEnableDragDropSort = config.UiItem.EnableDragDropSort;
        var oldEnableHWA = config.GuiItem.EnableHWA;

        var requestedEnableTun = GetSettingsBool(settings, "enableTun", config.TunModeItem.EnableTun);
        var tunDenied = requestedEnableTun && !AllowEnableTun();
        var tunChanged = requestedEnableTun != config.TunModeItem.EnableTun;

        // Core
        var inbound = config.Inbound.First();
        inbound.LocalPort = localPort;
        inbound.SecondLocalPortEnabled = GetSettingsBool(settings, "secondLocalPortEnabled", inbound.SecondLocalPortEnabled);
        inbound.UdpEnabled = GetSettingsBool(settings, "udpEnabled", inbound.UdpEnabled);
        inbound.SniffingEnabled = GetSettingsBool(settings, "sniffingEnabled", inbound.SniffingEnabled);
        inbound.DestOverride = GetSettingsStringArray(settings, "destOverride");
        inbound.RouteOnly = GetSettingsBool(settings, "routeOnly", inbound.RouteOnly);
        inbound.AllowLANConn = GetSettingsBool(settings, "allowLANConn", inbound.AllowLANConn);
        inbound.NewPort4LAN = GetSettingsBool(settings, "newPort4LAN", inbound.NewPort4LAN);
        inbound.User = GetSettingsString(settings, "user", inbound.User);
        inbound.Pass = GetSettingsString(settings, "pass", inbound.Pass);
        if (config.Inbound.Count > 1)
        {
            config.Inbound.RemoveAt(1);
        }
        config.CoreBasicItem.LogEnabled = GetSettingsBool(settings, "logEnabled", config.CoreBasicItem.LogEnabled);
        config.CoreBasicItem.Loglevel = GetSettingsString(settings, "loglevel", config.CoreBasicItem.Loglevel);
        config.CoreBasicItem.DefFingerprint = GetSettingsString(settings, "defFingerprint", config.CoreBasicItem.DefFingerprint);
        config.CoreBasicItem.DefUserAgent = GetSettingsString(settings, "defUserAgent", config.CoreBasicItem.DefUserAgent);
        config.CoreBasicItem.SendThrough = GetSettingsString(settings, "sendThrough", config.CoreBasicItem.SendThrough ?? string.Empty).TrimEx();
        config.CoreBasicItem.BindInterface = GetSettingsString(settings, "bindInterface", config.CoreBasicItem.BindInterface ?? string.Empty).TrimEx();
        config.Mux4SboxItem.Protocol = GetSettingsString(settings, "mux4SboxProtocol", config.Mux4SboxItem.Protocol);
        config.CoreBasicItem.EnableCacheFile4Sbox = GetSettingsBool(settings, "enableCacheFile4Sbox", config.CoreBasicItem.EnableCacheFile4Sbox);
        config.HysteriaItem.UpMbps = GetSettingsInt(settings, "hyUpMbps", config.HysteriaItem.UpMbps);
        config.HysteriaItem.DownMbps = GetSettingsInt(settings, "hyDownMbps", config.HysteriaItem.DownMbps);
        config.CoreBasicItem.EnableFragment = GetSettingsBool(settings, "enableFragment", config.CoreBasicItem.EnableFragment);
        config.CoreBasicItem.EnableFinalFragment = GetSettingsBool(settings, "enableFinalFragment", config.CoreBasicItem.EnableFinalFragment);
        config.Fragment4RayItem ??= new();
        config.Fragment4RayItem.Packets = GetSettingsString(settings, "fragmentPackets", config.Fragment4RayItem.Packets);
        config.Fragment4RayItem.Lengths = fragmentLengths;
        config.Fragment4RayItem.Delays = fragmentDelays;
        config.Fragment4RayItem.MaxSplit = fragmentMaxSplit;

        // General
        config.GuiItem.AutoRun = GetSettingsBool(settings, "autoRun", config.GuiItem.AutoRun);
        config.GuiItem.EnableStatistics = GetSettingsBool(settings, "enableStatistics", config.GuiItem.EnableStatistics);
        config.GuiItem.DisplayRealTimeSpeed = GetSettingsBool(settings, "displayRealTimeSpeed", config.GuiItem.DisplayRealTimeSpeed);
        config.GuiItem.KeepOlderDedupl = GetSettingsBool(settings, "keepOlderDedupl", config.GuiItem.KeepOlderDedupl);
        config.UiItem.EnableAutoAdjustMainLvColWidth = GetSettingsBool(settings, "enableAutoAdjustMainLvColWidth", config.UiItem.EnableAutoAdjustMainLvColWidth);
        config.UiItem.AutoHideStartup = GetSettingsBool(settings, "autoHideStartup", config.UiItem.AutoHideStartup);
        config.UiItem.Hide2TrayWhenClose = GetSettingsBool(settings, "hide2TrayWhenClose", config.UiItem.Hide2TrayWhenClose);
        config.UiItem.Minimize2Tray = GetSettingsBool(settings, "minimize2Tray", config.UiItem.Minimize2Tray);
        config.UiItem.EnableDragDropSort = GetSettingsBool(settings, "enableDragDropSort", config.UiItem.EnableDragDropSort);
        config.UiItem.DoubleClick2Activate = GetSettingsBool(settings, "doubleClick2Activate", config.UiItem.DoubleClick2Activate);
        config.GuiItem.AutoUpdateInterval = GetSettingsInt(settings, "autoUpdateInterval", config.GuiItem.AutoUpdateInterval);
        config.GuiItem.TrayMenuServersLimit = GetSettingsInt(settings, "trayMenuServersLimit", config.GuiItem.TrayMenuServersLimit);
        config.UiItem.CurrentFontFamily = GetSettingsString(settings, "currentFontFamily", config.UiItem.CurrentFontFamily);
        config.SpeedTestItem.SpeedTestTimeout = GetSettingsInt(settings, "speedTestTimeout", config.SpeedTestItem.SpeedTestTimeout);
        config.SpeedTestItem.MixedConcurrencyCount = GetSettingsInt(settings, "mixedConcurrencyCount", config.SpeedTestItem.MixedConcurrencyCount);
        config.SpeedTestItem.SpeedTestUrl = GetSettingsString(settings, "speedTestUrl", config.SpeedTestItem.SpeedTestUrl);
        config.SpeedTestItem.SpeedPingTestUrl = GetSettingsString(settings, "speedPingTestUrl", config.SpeedTestItem.SpeedPingTestUrl);
        config.SpeedTestItem.UdpTestTarget = GetSettingsString(settings, "udpTestTarget", config.SpeedTestItem.UdpTestTarget);
        config.SpeedTestItem.IPAPIUrl = GetSettingsString(settings, "ipapiUrl", config.SpeedTestItem.IPAPIUrl);
        var requestedHwa = GetSettingsBool(settings, "enableHWA", config.GuiItem.EnableHWA);
        if (requestedHwa && !oldEnableHWA)
        {
            // Explicitly re-enabled after an auto-disable: give the GPU a
            // fresh chance by resetting the crash budget instead of inheriting
            // the auto-disabled state forever.
            HardwareAccelerationGuard.OnUserReEnabled();
        }
        config.GuiItem.EnableHWA = requestedHwa;
        config.GuiItem.EffectsMode = NormalizeEffectsMode(GetSettingsString(settings, "effectsMode", config.GuiItem.EffectsMode));
        config.GuiItem.ReduceEffects = config.GuiItem.EffectsMode == "reduced";
        config.ConstItem.SubConvertUrl = GetSettingsString(settings, "subConvertUrl", config.ConstItem.SubConvertUrl);
        config.ConstItem.GeoSourceUrl = GetSettingsString(settings, "geoFileSourceUrl", config.ConstItem.GeoSourceUrl);
        config.ConstItem.SrsSourceUrl = GetSettingsString(settings, "srsFileSourceUrl", config.ConstItem.SrsSourceUrl);
        config.ConstItem.RouteRulesTemplateSourceUrl = GetSettingsString(settings, "routingRulesSourceUrl", config.ConstItem.RouteRulesTemplateSourceUrl);
        config.GuiItem.RootCertProvider = GetSettingsString(settings, "rootCertProvider", config.GuiItem.RootCertProvider);

        // System proxy
        var requestedSysProxyType = (ESysProxyType)Math.Clamp(
            GetSettingsInt(settings, "sysProxyType", (int)config.SystemProxyItem.SysProxyType), 0, 3);
        config.SystemProxyItem.SystemProxyExceptions = GetSettingsString(settings, "systemProxyExceptions", config.SystemProxyItem.SystemProxyExceptions);
        config.SystemProxyItem.NotProxyLocalAddress = GetSettingsBool(settings, "notProxyLocalAddress", config.SystemProxyItem.NotProxyLocalAddress);
        config.SystemProxyItem.SystemProxyAdvancedProtocol = GetSettingsString(settings, "systemProxyAdvancedProtocol", config.SystemProxyItem.SystemProxyAdvancedProtocol);
        config.SystemProxyItem.CustomSystemProxyPacPath = GetSettingsString(settings, "customSystemProxyPacPath", config.SystemProxyItem.CustomSystemProxyPacPath);
        config.SystemProxyItem.CustomSystemProxyScriptPath = GetSettingsString(settings, "customSystemProxyScriptPath", config.SystemProxyItem.CustomSystemProxyScriptPath);
        config.SystemProxyItem.SysProxyType = requestedSysProxyType;

        // TUN mode
        config.TunModeItem.AutoRoute = GetSettingsBool(settings, "tunAutoRoute", config.TunModeItem.AutoRoute);
        config.TunModeItem.StrictRoute = GetSettingsBool(settings, "tunStrictRoute", config.TunModeItem.StrictRoute);
        config.TunModeItem.Stack = GetSettingsString(settings, "tunStack", config.TunModeItem.Stack);
        config.TunModeItem.Mtu = GetSettingsInt(settings, "tunMtu", config.TunModeItem.Mtu);
        config.TunModeItem.EnableIPv6Address = GetSettingsBool(settings, "tunEnableIPv6Address", config.TunModeItem.EnableIPv6Address);
        config.TunModeItem.IcmpRouting = GetSettingsString(settings, "tunIcmpRouting", config.TunModeItem.IcmpRouting);
        config.TunModeItem.EnableLegacyProtect = GetSettingsBool(settings, "tunEnableLegacyProtect", config.TunModeItem.EnableLegacyProtect);
        config.TunModeItem.RouteExcludeAddress = Utils.String2List(GetSettingsString(settings, "tunRouteExcludeAddress"));
        config.TunModeItem.IPv4Address = GetSettingsString(settings, "tunIPv4Address", config.TunModeItem.IPv4Address);
        config.TunModeItem.IPv6Address = GetSettingsString(settings, "tunIPv6Address", config.TunModeItem.IPv6Address);
        config.TunModeItem.EnableTun = tunDenied ? false : requestedEnableTun;

        // Re-assert the fail-safe route defaults after the settings form overwrites
        // the TUN block. The stability policy is applied on every config load; this
        // keeps AutoRoute / StrictRoute / EnableLegacyProtect pinned on even when
        // the user saves a custom MTU or stack, so TUN stays leak-free.
        if (Utils.IsWindows())
        {
            WindowsTunStabilityPolicy.Apply(config);
        }
        else
        {
            config.TunModeItem.Mtu = WindowsTunStabilityPolicy.NormalizeMtu(config.TunModeItem.Mtu);
            config.TunModeItem.Stack = WindowsTunStabilityPolicy.NormalizeStack(config.TunModeItem.Stack);
        }

        // Core types
        SaveSettingsCoreTypes(settings);

        var needReboot = oldEnableStatistics != config.GuiItem.EnableStatistics
            || oldDisplayRealTimeSpeed != config.GuiItem.DisplayRealTimeSpeed
            || oldEnableDragDropSort != config.UiItem.EnableDragDropSort
            || oldEnableHWA != config.GuiItem.EnableHWA;

        var saved = await ConfigHandler.SaveConfig(config) == 0;
        if (saved)
        {
            await AutoStartupHandler.UpdateTask(config);
            AppManager.Instance.Reset();

            // Apply the complete AoGPN system-proxy setting immediately, including
            // exceptions and PAC/script changes even when the mode number is unchanged.
            await SysProxyHandler.UpdateSysProxy(config, false, requestedSysProxyType);
            // If the saved preference became Set/PAC while the connection is off,
            // start the proxy-only core so the OS proxy points at a live listener
            // (the same reconcile the dashboard and status-bar paths perform).
            await _proxyOnlyService.ReconcileAsync(config);
            StatusBarViewModel.Instance.SystemProxySelected = (int)requestedSysProxyType;
            await PushSystemProxyStateAsync(force: true);

            // TUN toggle changed: persist and reload the core (native status bar behaviour).
            if (tunChanged && !tunDenied)
            {
                StatusBarViewModel.Instance.ReloadRequested.Publish();
            }

            // Resync the whole form with the persisted truth so the shown switches
            // always match the config the close/minimize paths read (e.g. after the
            // TUN admin denial reverted enableTun above).
            await PushSettingsAsync();
        }

        // The effects tier applies immediately — push it even when the save
        // itself failed so the renderer never drifts from what the user picked.
        await PushEffectsTierAsync();

        var message = tunDenied
            ? "TUN mode requires administrator privileges — it was not enabled"
            : needReboot ? "Settings saved — a restart is required for some changes" : "Settings saved";
        await NotifySettingsSaveAsync(saved, message, needReboot, tunDenied);
    }

    private void SaveSettingsCoreTypes(JsonElement settings)
    {
        foreach (var item in AppManager.Instance.Config.CoreTypeItem ?? [])
        {
            var value = GetSettingsString(settings, $"coreType{(int)item.ConfigType}", item.CoreType.ToString());
            if (Enum.TryParse<ECoreType>(value, true, out var parsed))
            {
                item.CoreType = parsed;
            }
        }
    }

    /// <summary>Sends the Settings save acknowledgement back to the renderer.</summary>
    private async Task NotifySettingsSaveAsync(
        bool ok,
        string message,
        bool needReboot = false,
        bool tunDenied = false)
    {
        if (!WebViewReady)
        {
            return;
        }
        var json = JsonSerializer.Serialize(new { ok, message, needReboot, tunDenied });
        await ExecuteScriptSafelyAsync($"window.setSettingsSaveResult({json});");
    }

    internal static bool GetSettingsBool(JsonElement settings, string name, bool fallback)
    {
        if (!settings.TryGetProperty(name, out var property))
        {
            return fallback;
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) ? parsed : fallback,
            JsonValueKind.Number => property.GetInt32() != 0,
            _ => fallback,
        };
    }

    internal static int GetSettingsInt(JsonElement settings, string name, int fallback)
    {
        if (!settings.TryGetProperty(name, out var property))
        {
            return fallback;
        }
        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var num))
        {
            return num;
        }
        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }
        return fallback;
    }

    private static string GetSettingsString(JsonElement settings, string name, string? fallback = "")
    {
        if (settings.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? fallback ?? string.Empty;
        }
        return fallback ?? string.Empty;
    }

    private static List<string> GetSettingsStringArray(JsonElement settings, string name)
    {
        var result = new List<string>();
        if (!settings.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var item in property.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (value.IsNotEmpty())
            {
                result.Add(value);
            }
        }
        return result;
    }

    /// <summary>Mirrors StatusBarViewModel.AllowEnableTun: TUN needs elevation everywhere.</summary>
    internal static bool AllowEnableTun()
    {
        if (Utils.IsWindows())
        {
            return Utils.IsAdministrator();
        }
        else if (Utils.IsLinux() || Utils.IsMacOS())
        {
            return AppManager.Instance.LinuxSudoPwd.IsNotEmpty();
        }
        return false;
    }

    /// <summary>
    /// Publishes both the persisted independent preference and the effective OS mode.
    /// A connection using proxy transport can temporarily force the effective mode;
    /// showing both values prevents the quick-toggle from appearing to lose its choice.
    /// </summary>
    internal async Task PushSystemProxyStateAsync(bool force = false)
    {
        if (!WebViewReady)
        {
            return;
        }

        var config = AppManager.Instance.Config;
        var desired = config.SystemProxyItem?.SysProxyType ?? ESysProxyType.ForcedClear;
        var effective = SystemProxyPolicy.ResolveEffectiveType(config);
        var connectionOwns = SystemProxyPolicy.ConnectionNeedsSystemProxy(config);
        var signature = $"{(int)desired}:{(int)effective}:{connectionOwns}";
        if (!force && signature == _lastSystemProxySignature)
        {
            return;
        }

        _lastSystemProxySignature = signature;
        await ExecuteScriptSafelyAsync(
            $"window.setSystemProxyState({JsonSerializer.Serialize((int)desired)}, {JsonSerializer.Serialize((int)effective)}, {JsonSerializer.Serialize(connectionOwns)});");
    }

}
