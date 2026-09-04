namespace ServiceLib.Manager;

using System.Threading;

/// <summary>
/// Core process processing class
/// </summary>
public class CoreManager
{
    private static readonly Lazy<CoreManager> _instance = new(() => new());
    public static CoreManager Instance => _instance.Value;
    private Config _config;

    [SupportedOSPlatform("windows")]
    private WindowsJobService? _processJob;

    private ProcessService? _processService;
    private ProcessService? _processPreService;
    private bool _linuxSudo = false;
    private Func<bool, string, Task>? _updateFunc;
    private const string _tag = "CoreHandler";

    /// <summary>How long after launch the core's stdout/stderr is mirrored to ao_diag.txt.</summary>
    private static readonly TimeSpan CoreStartupCaptureWindow = TimeSpan.FromSeconds(30);

    private readonly System.Diagnostics.Stopwatch _coreStartupCapture = new();
    private readonly CoreRestartPolicy _restartPolicy = new();
    private TunCleanupTransaction? _tunTransaction;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource _lifecycleCts = new();
    private int _lifecycleGeneration;
    private CoreConfigContext? _activeMainContext;
    private CoreConfigContext? _activePreContext;
    private int _mainCrashAttempts;
    private ICoreStartStrategy? _activeMainStrategy;

    private readonly object _healthLock = new();
    private readonly Dictionary<CoreHealthRole, CoreHealthSnapshot> _health = new()
    {
        [CoreHealthRole.Main] = CoreHealthSnapshot.Stopped(CoreHealthRole.Main),
        [CoreHealthRole.PreSocks] = CoreHealthSnapshot.Stopped(CoreHealthRole.PreSocks)
    };

    public IReadOnlyDictionary<CoreHealthRole, CoreHealthSnapshot> Health
    {
        get
        {
            lock (_healthLock)
            {
                return new Dictionary<CoreHealthRole, CoreHealthSnapshot>(_health);
            }
        }
    }

    public CoreHealthSnapshot GetHealth(CoreHealthRole role)
    {
        lock (_healthLock)
        {
            return _health[role];
        }
    }

    private void PublishDiagnostic(CoreStartupDiagnostic diagnostic)
    {
        AppEvents.CoreStartupDiagnosticChanged.Publish(diagnostic);
        Logging.SaveLog($"[{diagnostic.Code}] {diagnostic.Message} {diagnostic.TechnicalDetails}");
    }

    private void PublishHealth(CoreHealthRole role, CoreHealthState state, ECoreType? coreType = null, int? port = null, string? error = null)
    {
        var snapshot = new CoreHealthSnapshot(role, state, coreType, port, error);
        lock (_healthLock)
        {
            _health[role] = snapshot;
        }
        AppEvents.CoreHealthChanged.Publish(snapshot);
    }

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;

        //Copy the bin folder to the storage location (for init)
        if (Environment.GetEnvironmentVariable(Global.LocalAppData) == "1")
        {
            var fromPath = Utils.GetBaseDirectory("bin");
            var toPath = Utils.GetBinPath("");
            if (fromPath != toPath)
            {
                FileUtils.CopyDirectory(fromPath, toPath, true, false);
            }
        }

        if (Utils.IsNonWindows())
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo();
            foreach (var it in coreInfo)
            {
                if (it.CoreType == ECoreType.AoGPN)
                {
                    if (Utils.UpgradeAppExists(out var upgradeFileName))
                    {
                        await Utils.SetLinuxChmod(upgradeFileName);
                    }
                    continue;
                }

                foreach (var name in it.CoreExes)
                {
                    var exe = Utils.GetBinPath(Utils.GetExeName(name), it.CoreType.ToString());
                    if (File.Exists(exe))
                    {
                        await Utils.SetLinuxChmod(exe);
                    }
                }
            }
        }
    }

    /// <param name="mainContext">Resolved main context (with pre-socks ports already merged if applicable).</param>
    /// <param name="preContext">Optional pre-socks context passed to <see cref="CoreStartPreService"/>.</param>
    public async Task LoadCore(CoreConfigContext? mainContext, CoreConfigContext? preContext)
    {
        if (mainContext == null)
        {
            await UpdateFunc(false, ResUI.CheckServerSettings);
            return;
        }

        var node = mainContext.Node;
        // Başlatma stratejisi: TUN sahipliği / native-tunnel politikası çekirdek
        // tipine göre CoreStartStrategyFactory'den gelir (mihomo own-TUN + host
        // rotası, openvpn native tunnel, diğerleri varsayılan). Bağlam duyarlı
        // çözüm: UseNativeGpnEngine bayrağı yerel motoru (NativeGpnStartStrategy)
        // seçer — bayrak şu an hiçbir çağıranda yok, davranış birebir aynı.
        var startStrategy = CoreStartStrategyFactory.For(mainContext);
        // mihomo kendi Wintun adaptörünü + rotalarını yönetir (own TUN): uygulamanın
        // TunLifecycleManager'ı ve host rotası mihomo için ayrı çalışır.
        var mihomoOwnsTun = startStrategy.OwnsTun;
        var isNativeTunnelCore = startStrategy.IsNativeTunnelCore;
        var generation = BeginLifecycle(mainContext, preContext);
        PublishHealth(CoreHealthRole.Main, CoreHealthState.Starting, mainContext.RunCoreType, AppManager.Instance.GetLocalPort(EInboundProtocol.socks));
        if (preContext != null)
        {
            PublishHealth(CoreHealthRole.PreSocks, CoreHealthState.Starting, preContext.RunCoreType, preContext.Node.Port);
        }
        var fileName = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        var result = await CoreConfigHandler.GenerateClientConfig(mainContext, fileName);
        if (result.Success != true)
        {
            PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, mainContext.RunCoreType,
                AppManager.Instance.GetLocalPort(EInboundProtocol.socks), result.Msg);
            await UpdateFunc(true, result.Msg);
            return;
        }

        await UpdateFunc(false, $"{node.GetSummary()}");
        await UpdateFunc(false, $"{Utils.GetRuntimeInfo()}");
        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await StopProcessesOnly();
        await Task.Delay(100);

        if ((mainContext.IsTunEnabled || preContext?.IsTunEnabled == true) && !isNativeTunnelCore && !mihomoOwnsTun)
        {
            _tunTransaction = await TunLifecycleManager.Instance.BeginAsync(true, mainContext.RoutingItem);
            await TunLifecycleManager.Instance.CleanupAsync(true);
        }

        // mihomo: WG sunucu IP'si için /32 host rotası (wg-quick deseni) —
        // el sıkışma paketleri mihomo'nun /1 TUN rotasına asla dönmez.
        // Rota, kapanışta StopProcessesOnly içinde kaldırılır.
        if (mihomoOwnsTun)
        {
            await startStrategy.BeforeStartAsync(mainContext);
        }

        await CoreStart(mainContext, generation, startStrategy);
        if (_processService is null)
        {
            // Tier 1 — in-process motor (native GPN): HARİCİ süreç YOKTUR, null
            // OLAĞANDIR (StartAsync gerçek hatalarda fırlatır — sessiz no-op yok).
            // Harici çekirdekler (mihomo/sing-box/Xray/openvpn/...) için kural
            // eskisi gibi KATIDIR: exe eksik → hata + dönüş.
            if (!startStrategy.IsInProcessEngine)
            {
                PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, mainContext.RunCoreType,
                    AppManager.Instance.GetLocalPort(EInboundProtocol.socks), "Core executable missing or process failed to start.");
                return;
            }
            DiagLog.Write("CORE_INPROCESS native engine active — no external process (by design).");
        }
        // OpenVPN owns the OS tunnel and intentionally has no local SOCKS5
        // listener. Xray/sing-box keep the stricter listener + connectivity probes.
        // In-process motor (IsInProcessEngine): süreç/SOCKS5 yoklaması YAPILMAZ —
        // StartAsync başarıyla döndüyse motor komutu aldı; oturum-içi hatalar
        // EngineFailed → onExited kurtarma döngüsünden gelir.
        var mainReady = startStrategy.IsInProcessEngine
            ? true
            : isNativeTunnelCore
                ? _processService != null && CoreHealthProbe.IsProcessReady(_processService)
                : _processService != null
                    && CoreHealthProbe.IsProcessReady(_processService)
                    && await CoreHealthProbe.WaitForSocks5Async(Global.Loopback, AppManager.Instance.GetLocalPort(EInboundProtocol.socks), TimeSpan.FromSeconds(5));
        if (!isNativeTunnelCore && !startStrategy.IsInProcessEngine)
        {
            var mainProbe = await RuntimeConnectivityProbe.ProbeSocks5Async(
                Global.Loopback,
                AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
                TimeSpan.FromSeconds(1));
            if (!mainProbe.Success)
            {
                PublishDiagnostic(CoreStartupDiagnostics.Create(
                    CoreHealthRole.Main, mainContext.RunCoreType, CoreStartupStage.WaitForProxy,
                    "Main core runtime SOCKS5 probe failed.",
                    output: $"{mainProbe.ErrorCode}: {mainProbe.Details}",
                    port: AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
                    canRecover: true));
            }
            mainReady &= mainProbe.Success;
        }
        if (!mainReady)
        {
            PublishDiagnostic(CoreStartupDiagnostics.Create(
                CoreHealthRole.Main, mainContext.RunCoreType, CoreStartupStage.WaitForProxy,
                "Core started but its local SOCKS5 endpoint did not become ready.",
                output: "SOCKS5 readiness timeout.",
                port: AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
                canRecover: true));
            PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, mainContext.RunCoreType, AppManager.Instance.GetLocalPort(EInboundProtocol.socks), "Core failed readiness check.");
            await StopProcessesOnly();
            await UpdateFunc(true, "Core failed to open the local SOCKS5 port.");
            return;
        }
        else
        {
            PublishHealth(CoreHealthRole.Main, CoreHealthState.Ready, mainContext.RunCoreType, AppManager.Instance.GetLocalPort(EInboundProtocol.socks));

            // Second-pass socket flush: the TUN is now live, so kill any
            // connections that leaked through during the startup gap. This
            // is especially important for QUIC/UDP sessions that survived
            // the pre-start TCP-only flush.
            // mihomo hariç: mihomo kendi TUN'unu kurar, TunInterfaceConfirmed/
            // fallback mekanizması sing-box'a özgüdür (Global VPN fallback'i
            // mihomo config'ini yeniden üretemez).
            if ((mainContext.IsTunEnabled || preContext?.IsTunEnabled == true) && !mihomoOwnsTun)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TunLifecycleManager.FlushAfterTunStartAsync(mainContext.RoutingItem);

                        // TUN fallback: if the Wintun adapter failed to create
                        // (common in VMware and some Hyper-V guests), restart the
                        // core in Global VPN mode.  The GPN routing with
                        // process_name rules cannot work through the SOCKS inbound
                        // (no PID attribution), so we must regenerate the config
                        // with IsTunEnabled=false and restart sing-box.
                        //
                        // Step 1: mutate the app config so SystemProxyPolicy
                        //         permits the proxy.
                        // Step 2: regenerate the sing-box config with TUN disabled
                        //         → Global VPN path → route.final = "proxy".
                        // Step 3: restart the core process.
                        // Step 4: enable the OS system proxy.
                        if (TunLifecycleManager.TunInterfaceConfirmed == false)
                        {
                            Logging.SaveLog("[CoreManager] TUN adapter not found — restarting in Global VPN mode.");
                            DiagLog.Write("CORE_FALLBACK TUN missing → restarting core in Global VPN fallback");
                            try
                            {
                                var config = AppManager.Instance.Config;
                                config.ConnectionItem.Transport = "proxy";
                                if (config.TunModeItem != null)
                                {
                                    config.TunModeItem.EnableTun = false;
                                }

                                // Rebuild the config context with TUN disabled so
                                // GenRouting() takes the Global VPN code path — and strip
                                // the app-managed split rules (per-entry process rules,
                                // mode catch-alls, QUIC block) at the same time. Without
                                // the TUN inbound there is no PID attribution through the
                                // SOCKS/proxy path, so those rules cannot match: keeping
                                // them would silently misroute traffic (a GPN blacklist's
                                // excluded apps would still be tunneled by the proxy
                                // catch-all; a whitelist's direct catch-all would leak
                                // every unlisted connection). The fallback is an honest
                                // Global VPN — route.final = proxy governs, user-defined
                                // rules are preserved.
                                var fallbackContext = SystemProxyOnlyService.ToProxyOnlyContext(
                                    mainContext with { IsTunEnabled = false });
                                var cfgPath = Utils.GetBinConfigPath(Global.CoreConfigFileName);
                                var genResult = await CoreConfigHandler.GenerateClientConfig(fallbackContext, cfgPath);
                                DiagLog.Write($"CORE_FALLBACK config regenerated (TUN off): success={genResult.Success}");

                                if (genResult.Success == true)
                                {
                                    // Kill the old core that has the GPN config.
                                    await StopProcessesOnly();
                                    // Restart with the new Global VPN config.
                                    await CoreStart(fallbackContext, generation, startStrategy);
                                    DiagLog.Write("CORE_FALLBACK core restarted with Global VPN config");

                                    // The normal reload completion path applies the
                                    // effective proxy exactly once after readiness.
                                    await SysProxyHandler.UpdateSysProxy(config, false, ESysProxyType.ForcedChange);
                                    DiagLog.Write("CORE_FALLBACK system proxy enabled for SOCKS path");

                                    // The GPN split tunnel cannot work without the TUN
                                    // inbound — tell the user the connection is running as
                                    // Global VPN instead of silently ignoring their split
                                    // rules (excluded apps would otherwise look like they
                                    // still go through the tunnel).
                                    NoticeManager.Instance.SendMessageEx(
                                        "TUN adaptörü kurulamadı — GPN ayrıştırma (split tunnel) devre dışı; "
                                        + "bağlantı Global VPN (proxy) olarak sürüyor. TUN çalışan bir ortamda yeniden bağlanın.");
                                    DiagLog.Write("CORE_FALLBACK notice: split tunneling disabled (Global VPN fallback)");
                                }
                            }
                            catch (Exception ex)
                            {
                                DiagLog.Write($"CORE_FALLBACK restart failed: {ex.Message}");
                            }
                        }
                    }
                    catch
                    {
                        // Never let a flush failure cascade into core health.
                    }
                });
            }
        }
        await WaitForProxyPort(preContext);
        await CoreStartPreService(preContext, generation);
        if (preContext != null)
        {
            var helperReady = _processPreService != null && CoreHealthProbe.IsProcessReady(_processPreService);
            if (helperReady)
            {
                var helperProbe = await RuntimeConnectivityProbe.ProbeSocks5Async(
                    Global.Loopback, preContext.Node.Port, TimeSpan.FromSeconds(1));
                helperReady = helperProbe.Success;
                if (!helperProbe.Success)
                {
                    PublishDiagnostic(CoreStartupDiagnostics.Create(
                        CoreHealthRole.PreSocks, preContext.RunCoreType, CoreStartupStage.StartHelper,
                        "Pre-SOCKS helper runtime probe failed.",
                        output: $"{helperProbe.ErrorCode}: {helperProbe.Details}",
                        port: preContext.Node.Port, canRecover: true));
                }
            }
            if (!helperReady)
            {
                PublishDiagnostic(CoreStartupDiagnostics.Create(
                    CoreHealthRole.PreSocks, preContext.RunCoreType, CoreStartupStage.StartHelper,
                    "Pre-SOCKS helper core failed to start.",
                    port: preContext.Node.Port, canRecover: true));
            }
            PublishHealth(CoreHealthRole.PreSocks, helperReady ? CoreHealthState.Ready : CoreHealthState.Failed, preContext.RunCoreType, preContext.Node.Port,
                helperReady ? null : ResUI.FailedToRunCore);
        }

        // The helper is an implementation detail; UI, statistics and API consumers
        // must follow the main core that owns the user's connection profile.
        AppManager.Instance.RunningCoreType = mainContext.RunCoreType;

        // This is the only connect-side system-proxy update. It runs after the
        // selected core has started and its local endpoint has been verified.
        if (_processService != null)
        {
            await SysProxyHandler.UpdateSysProxy(_config, false);
            await UpdateFunc(true, $"{node.GetSummary()}");
        }
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(List<ServerTestItem> selecteds)
    {
        var coreType = selecteds.FirstOrDefault()?.CoreType == ECoreType.sing_box ? ECoreType.sing_box : ECoreType.Xray;
        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, configPath, selecteds, coreType);
        await UpdateFunc(false, result.Msg);
        if (result.Success != true)
        {
            return null;
        }

        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await UpdateFunc(false, configPath);

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(ServerTestItem testItem)
    {
        var node = await AppManager.Instance.GetProfileItem(testItem.IndexId);
        if (node is null)
        {
            return null;
        }

        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var (context, _) = await CoreConfigContextBuilder.Build(_config, node);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, context, testItem, configPath);
        if (result.Success != true)
        {
            return null;
        }

        var coreType = context.RunCoreType;
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task CoreStop()
    {
        CancelLifecycle();
        PublishHealth(CoreHealthRole.Main, CoreHealthState.Stopped);
        PublishHealth(CoreHealthRole.PreSocks, CoreHealthState.Stopped);
        await StopProcessesOnly();
    }

    private async Task StopProcessesOnly()
    {
        try
        {
            if (_linuxSudo)
            {
                await CoreAdminManager.Instance.KillProcessAsLinuxSudo();
                _linuxSudo = false;
            }

            // Snapshot the field before awaiting: StopProcessesOnly can be entered
            // concurrently (UI disconnect, exit watchdog, crash recovery, the
            // fire-and-forget CoreStop), and another invocation may null the
            // field while this one awaits StopAsync — disposing the re-read field
            // would then throw NullReferenceException. The CompareExchange clears
            // the slot only if it still holds the instance we just stopped, so a
            // concurrent CoreStart that already installed a newer process is never
            // clobbered.
            var processService = _processService;
            if (processService != null)
            {
                await processService.StopAsync();
                processService.Dispose();
                Interlocked.CompareExchange(ref _processService, null, processService);
            }

            var processPreService = _processPreService;
            if (processPreService != null)
            {
                await processPreService.StopAsync();
                processPreService.Dispose();
                Interlocked.CompareExchange(ref _processPreService, null, processPreService);
            }

            // Clean up orphaned core processes left behind by a previous
            // run that crashed or was force-killed. They hold the SOCKS
            // port and prevent the new instance from binding.
            KillOrphanCoreProcesses();

            if (_tunTransaction != null)
            {
                await _tunTransaction.CompleteAsync();
                _tunTransaction = null;
            }

            // Aktif ana çekirdeğin stratejisi kapanış yan etkilerini geri alır
            // (mihomo WG host rotası — idempotent, kurulmamışsa no-op).
            if (_activeMainStrategy != null)
            {
                await _activeMainStrategy.AfterStopAsync();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// Kill any core processes (xray, sing-box, mihomo) left running from a
    /// previous AoGPN session. Only processes whose executable lives under this
    /// application's own directories are considered: foreign clients that happen
    /// to run an identically-named binary (v2rayN, Nekoray, standalone
    /// sing-box, ...) are never touched, and a process whose executable path
    /// cannot be read is left alone too — never kill what we cannot attribute
    /// to this app.
    ///
    /// Runs on three occasions:
    ///  1. during every core stop, so a graceful disconnect/exit kills its own;
    ///  2. at normal GUI startup, so cores left by a previous run that crashed
    ///     or was force-killed (Task Manager, hard shutdown) cannot keep holding
    ///     the local ports or TUN from the dead session;
    ///  3. from the exit watchdog as the last step before force-terminating a
    ///     wedged exit.
    /// Safe to call at any time: the path check never touches a running
    /// instance's processes and foreign clients are excluded by construction.
    /// </summary>
    public static void KillOrphanCoreProcesses()
    {
        var coreNames = new[] { "xray", "sing-box", "mihomo" };
        var ownedDirectories = new[]
        {
            Utils.StartupPath(),
            Utils.GetBaseDirectory(),
        };
        foreach (var name in coreNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!IsAppOwnedCoreProcess(proc, ownedDirectories))
                        {
                            continue;
                        }
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                        Logging.SaveLog($"Kill orphan {name} PID {proc.Id}");
                    }
                    catch
                    {
                        // Process may have already exited
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
                // Process enumeration may fail for some names on some platforms
            }
        }
    }

    /// <summary>
    /// True when the given process's executable lives under one of the
    /// application-owned directories (StartupPath or the base directory).
    /// Path comparison is ordinal case-insensitive; any failure to read the
    /// path (access denied, process exited meanwhile) returns false so an
    /// unverifiable process is never killed.
    /// </summary>
    private static bool IsAppOwnedCoreProcess(Process proc, IEnumerable<string> ownedDirectories)
    {
        try
        {
            // MainModule throws for other-architecture or access-protected
            // processes; that is fine — those cannot be positively attributed
            // to this app and are therefore never killed.
            return IsPathUnderDirectories(proc.MainModule?.FileName, ownedDirectories);
        }
        catch
        {
            // Access denied / process exited: without positive proof of ownership
            // the process is left untouched.
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="exePath"/> lives under one of the given
    /// directories (ordinal case-insensitive prefix match with a directory
    /// boundary — <c>C:\app\bin</c> does not own <c>C:\app\bin2\xray.exe</c>).
    /// Null, empty or otherwise unreadable paths are never considered owned.
    /// </summary>
    internal static bool IsPathUnderDirectories(string? exePath, IEnumerable<string> ownedDirectories)
    {
        if (exePath.IsNullOrEmpty())
        {
            return false;
        }

        var fullPath = Path.GetFullPath(exePath);
        foreach (var dir in ownedDirectories)
        {
            var fullDir = Path.GetFullPath(dir);
            if (fullPath.Equals(fullDir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase)
                && fullPath.Length > fullDir.Length
                && (fullPath[fullDir.Length] == Path.DirectorySeparatorChar
                    || fullPath[fullDir.Length] == Path.AltDirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    #region Private

    private int BeginLifecycle(CoreConfigContext mainContext, CoreConfigContext? preContext)
    {
        lock (_lifecycleLock)
        {
            _lifecycleCts.Cancel();
            _lifecycleCts.Dispose();
            _lifecycleCts = new CancellationTokenSource();
            _activeMainContext = mainContext;
            _activePreContext = preContext;
            _activeMainStrategy = CoreStartStrategyFactory.For(mainContext);
            _mainCrashAttempts = 0;
            return ++_lifecycleGeneration;
        }
    }

    private void CancelLifecycle()
    {
        lock (_lifecycleLock)
        {
            _lifecycleCts.Cancel();
            _activeMainContext = null;
            _activePreContext = null;
            ++_lifecycleGeneration;
        }
    }

    private bool IsCurrentGeneration(int generation)
    {
        lock (_lifecycleLock)
        {
            return generation == _lifecycleGeneration && !_lifecycleCts.IsCancellationRequested;
        }
    }

    private void OnMainProcessExited(int generation)
    {
        // A process exit during an intentional stop/reload must never be treated
        // as a crash. BeginLifecycle/CancelLifecycle invalidates this generation
        // before the old process is disposed.
        if (!IsCurrentGeneration(generation))
        {
            return;
        }
        _ = RecoverMainCoreAsync(generation);
    }

    private void OnPreProcessExited(int generation)
    {
        if (!IsCurrentGeneration(generation))
        {
            return;
        }
        var context = _activePreContext;
        if (context != null)
        {
            PublishHealth(CoreHealthRole.PreSocks, CoreHealthState.Failed, context.RunCoreType, context.Node.Port, "Pre-SOCKS core exited unexpectedly.");
        }
    }

    private async Task RecoverMainCoreAsync(int generation)
    {
        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        CoreConfigContext? mainContext;
        CoreConfigContext? preContext;
        int attempt;
        CancellationToken token;
        lock (_lifecycleLock)
        {
            mainContext = _activeMainContext;
            preContext = _activePreContext;
            attempt = ++_mainCrashAttempts;
            token = _lifecycleCts.Token;
        }

        if (mainContext == null || !_restartPolicy.CanRestart(attempt))
        {
            if (mainContext != null)
            {
                PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, mainContext.RunCoreType,
                    AppManager.Instance.GetLocalPort(EInboundProtocol.socks), "Core stopped repeatedly and recovery was disabled.");
            }
            return;
        }

        PublishHealth(CoreHealthRole.Main, CoreHealthState.Degraded, mainContext.RunCoreType,
            AppManager.Instance.GetLocalPort(EInboundProtocol.socks), "Core exited unexpectedly; restarting.");

        try
        {
            await Task.Delay(_restartPolicy.GetDelay(attempt), token);
            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            await StopProcessesOnly();
            var startStrategy = CoreStartStrategyFactory.For(mainContext);
            await CoreStart(mainContext, generation, startStrategy);
            var isNativeTunnelCore = startStrategy.IsNativeTunnelCore;
            // In-process motor: süreç yoklaması yapılmaz (StartAsync fırlatmadıysa
            // motor ayakta; oturum-içi hatalar EngineFailed → onExited ile gelir).
            var ready = startStrategy.IsInProcessEngine
                ? true
                : isNativeTunnelCore
                    ? _processService != null && CoreHealthProbe.IsProcessReady(_processService)
                    : _processService != null
                        && CoreHealthProbe.IsProcessReady(_processService)
                        && await CoreHealthProbe.WaitForSocks5Async(Global.Loopback,
                            AppManager.Instance.GetLocalPort(EInboundProtocol.socks), TimeSpan.FromSeconds(5), token);
            if (ready && !isNativeTunnelCore && !startStrategy.IsInProcessEngine)
            {
                var recoveryProbe = await RuntimeConnectivityProbe.ProbeSocks5Async(
                    Global.Loopback, AppManager.Instance.GetLocalPort(EInboundProtocol.socks), TimeSpan.FromSeconds(1), token);
                ready = recoveryProbe.Success;
            }
            if (!ready)
            {
                PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, mainContext.RunCoreType,
                    AppManager.Instance.GetLocalPort(EInboundProtocol.socks), "Core recovery failed readiness check.");
                return;
            }

            PublishHealth(CoreHealthRole.Main, CoreHealthState.Ready, mainContext.RunCoreType,
                AppManager.Instance.GetLocalPort(EInboundProtocol.socks));
            await CoreStartPreService(preContext, generation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, mainContext.RunCoreType,
                AppManager.Instance.GetLocalPort(EInboundProtocol.socks), ex.Message);
        }
    }

    private async Task CoreStart(CoreConfigContext context, int generation, ICoreStartStrategy strategy)
    {
        // Çekirdeğe özgü başlatma kararları (ikili seçimi, displayLog, isTunLaunch)
        // stratejide yaşar; ortak başlatma hattı (RunProcess) burada kalır.
        var proc = await strategy.StartAsync(context, RunProcess, () => OnMainProcessExited(generation));
        if (proc is null)
        {
            return;
        }
        _processService = proc;
    }

    private async Task CoreStartPreService(CoreConfigContext? preContext, int generation)
    {
        if (_processService is { HasExited: false } && preContext != null)
        {
            var preCoreType = preContext.RunCoreType;
            var fileName = Utils.GetBinConfigPath(Global.CorePreConfigFileName);
            var result = await CoreConfigHandler.GenerateClientConfig(preContext, fileName);
            if (result.Success)
            {
                var coreInfo = CoreInfoManager.Instance.GetCoreInfo(preCoreType);
                var proc = await RunProcess(coreInfo, Global.CorePreConfigFileName, true, true, preContext.IsTunEnabled, () => OnPreProcessExited(generation));
                if (proc is null)
                {
                    return;
                }
                _processPreService = proc;
            }
        }
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }

    private static async Task WaitForProxyPort(CoreConfigContext? preContext, int timeoutMs = 5000)
    {
        if (preContext is null)
        {
            return;
        }
        if (!preContext.IsTunEnabled)
        {
            return;
        }

        using var rootCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        var rootToken = rootCts.Token;

        var port = preContext.Node.Port;
        // SOCKS5 client greeting: VER=5, NMETHODS=1, METHOD=0x00 (no auth)
        ReadOnlyMemory<byte> greeting = new byte[] { 0x05, 0x01, 0x00 };
        var buf = new byte[2];

        while (!rootToken.IsCancellationRequested)
        {
            using var tcp = new TcpClient();
            using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(rootToken, attemptCts.Token);
            var linkedToken = linkedCts.Token;
            try
            {
                await tcp.ConnectAsync(Global.Loopback, port, linkedToken);
                var stream = tcp.GetStream();

                await stream.WriteAsync(greeting, linkedToken);

                var read = await stream.ReadAsync(buf.AsMemory(0, 2), linkedToken);

                // Server selection: VER=5, METHOD=0x00 (no auth) — proxy is fully ready.
                // Checking the method as well avoids treating an unrelated listener as ready.
                if (read == 2 && buf[0] == 0x05 && buf[1] == 0x00)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                if (!rootToken.IsCancellationRequested)
                {
                    continue;
                }
                Logging.SaveLog($"WaitForProxyPort Timeout waiting for proxy port {port} to be ready.");
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Connection refused, proxy not ready yet, wait 50ms before retrying
                try
                {
                    await Task.Delay(50, rootToken);
                }
                catch (OperationCanceledException)
                {
                    Logging.SaveLog($"WaitForProxyPort Timeout waiting for proxy port {port} to be ready.");
                    return;
                }
            }
            catch
            {
                // Ignore other exceptions and continue
            }
        }
    }

    #endregion Private

    #region Process

    /// <summary>
    ///     Decides whether a core launch must be elevated on non-Windows platforms.
    ///     The TUN state comes from the immutable <see cref="CoreConfigContext" /> snapshot that
    ///     generated the config, never from the live mutable config: the generated config and the
    ///     launch mode must always agree, even if TUN is toggled while a reload is in flight.
    /// </summary>
    public static bool ShouldRunAsSudo(bool isTunLaunch, ECoreType? coreType, bool isNonWindows)
    {
        return isTunLaunch
            && coreType is ECoreType.sing_box or ECoreType.mihomo or ECoreType.Xray or ECoreType.openvpn
            && isNonWindows;
    }

    private async Task<ProcessService?> RunProcess(CoreInfo? coreInfo, string configPath, bool displayLog, bool mayNeedSudo, bool isTunLaunch = false, Action? exitedCallback = null)
    {
        var fileName = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var msg);
        if (fileName.IsNullOrEmpty())
        {
            var missingMessage = $"Core executable missing: {msg}";
            DiagLog.Write($"CORE_MISSING {missingMessage}");
            PublishDiagnostic(CoreStartupDiagnostics.Create(
                CoreHealthRole.Main, coreInfo?.CoreType, CoreStartupStage.ResolveBinary,
                missingMessage, port: AppManager.Instance.GetLocalPort(EInboundProtocol.socks)));
            PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, coreInfo?.CoreType,
                AppManager.Instance.GetLocalPort(EInboundProtocol.socks), missingMessage);
            await UpdateFunc(true, missingMessage);
            return null;
        }

        try
        {
            var validation = await CoreConfigValidator.ValidateAsync(
                coreInfo.CoreType,
                fileName,
                configPath,
                Utils.GetBinConfigPath(),
                coreInfo.Environment);
            // Always surface the pre-launch core check (e.g. `sing-box check -c`)
            // in ao_diag.txt. A failing check is the single most informative
            // line for diagnosing "TUN never created" / instant core exits.
            DiagLog.Write($"CORE_CHECK {coreInfo.CoreType} config={configPath} success={validation.Success}");
            if (!validation.Success)
            {
                var validationMessage = validation.Message.IsNullOrEmpty()
                    ? "Core configuration validation failed."
                    : validation.Message;
                DiagLog.Write($"CORE_CHECK OUTPUT: {validationMessage.ReplaceLineBreaks(" | ")}");
                // The raw core output is developer-oriented (FATAL + schema jargon).
                // The user gets a plain explanation; the technical detail stays in
                // the startup diagnostics, ao_diag.txt and the message panel below.
                var userMessage = CoreValidationMessage.ToUserMessage(validationMessage);
                var technicalDetail = CoreValidationMessage.StripAnsi(validationMessage);
                PublishDiagnostic(CoreStartupDiagnostics.Create(
                    CoreHealthRole.Main, coreInfo.CoreType, CoreStartupStage.ValidateConfig,
                    validationMessage, output: technicalDetail,
                    port: AppManager.Instance.GetLocalPort(EInboundProtocol.socks)));
                PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, coreInfo.CoreType,
                    AppManager.Instance.GetLocalPort(EInboundProtocol.socks), userMessage);
                await UpdateFunc(true, userMessage);
                if (!validationMessage.Equals(userMessage, StringComparison.Ordinal))
                {
                    NoticeManager.Instance.SendMessage(technicalDetail);
                }
                return null;
            }

            if (mayNeedSudo
                && ShouldRunAsSudo(isTunLaunch, coreInfo.CoreType, Utils.IsNonWindows()))
            {
                _linuxSudo = true;
                await CoreAdminManager.Instance.Init(_config, _updateFunc);
                return await CoreAdminManager.Instance.RunProcessAsLinuxSudo(fileName, coreInfo, configPath, exitedCallback);
            }

            return await RunProcessNormal(fileName, coreInfo, configPath, displayLog, exitedCallback);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"CORE_START_ERROR {ex}");
            Logging.SaveLog(_tag, ex);
            var stage = mayNeedSudo && ShouldRunAsSudo(isTunLaunch, coreInfo?.CoreType, Utils.IsNonWindows())
                ? CoreStartupStage.Elevation
                : CoreStartupStage.StartProcess;
            var startupMessage = ex.Message.IsNullOrEmpty()
                ? "Core process could not be started."
                : ex.Message;
            PublishDiagnostic(CoreStartupDiagnostics.Create(
                CoreHealthRole.Main, coreInfo?.CoreType, stage,
                startupMessage, ex,
                port: AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
                canRecover: true));
            PublishHealth(CoreHealthRole.Main, CoreHealthState.Failed, coreInfo?.CoreType,
                AppManager.Instance.GetLocalPort(EInboundProtocol.socks), startupMessage);
            await UpdateFunc(true, startupMessage);
            return null;
        }
    }

    private async Task<ProcessService?> RunProcessNormal(string fileName, CoreInfo? coreInfo, string configPath, bool displayLog, Action? exitedCallback = null)
    {
        // Always pass the config as an absolute, quoted path so the core can
        // find it no matter which working directory we launch from. Some cores
        // (sing-box) resolve wintun.dll / geo assets relative to their own
        // executable, so we set the working directory to the binary's folder
        // instead of the binConfigs folder.
        var absoluteConfigPath = Utils.GetBinConfigPath(configPath).AppendQuotes();
        var workingDirectory = Path.GetDirectoryName(fileName) ?? Utils.GetBinConfigPath();
        if (!File.Exists(fileName))
        {
            var missingMessage = $"Core executable missing: '{fileName}' does not exist.";
            DiagLog.Write($"CORE_MISSING {missingMessage}");
            throw new FileNotFoundException(missingMessage, fileName);
        }

        var environmentVars = new Dictionary<string, string>();
        foreach (var kv in coreInfo.Environment)
        {
            // Environment values must stay unquoted (e.g. mieru's
            // MIERU_CONFIG_JSON_FILE reads the raw value).
            environmentVars[kv.Key] = string.Format(kv.Value, Utils.GetBinConfigPath(configPath));
        }

        var arguments = string.Format(coreInfo.Arguments, absoluteConfigPath);
        DiagLog.Write($"CORE_LAUNCH {fileName} args={arguments} cwd={workingDirectory} displayLog={displayLog}");

        // Startup window for CORE_OUT mirroring (see outputSink below).
        _coreStartupCapture.Restart();

        var procService = new ProcessService(
            fileName: fileName,
            arguments: arguments,
            workingDirectory: workingDirectory,
            displayLog: displayLog,
            redirectInput: false,
            environmentVars: environmentVars,
            updateFunc: _updateFunc,
            exitedCallback: exitedCallback,
            // Pipe every stdout/stderr line into ao_diag.txt ONLY during the
            // startup window so a core that crashes during startup (bad config,
            // missing wintun.dll, etc.) leaves an exact trace next to the EXE.
            // Steady-state core output is already written by the core itself to
            // its own log file (ao_singbox_*.log / Verror_*.txt / Vaccess_*.txt);
            // mirroring it forever duplicated every line into ao_diag.txt and
            // vpn-session.log for the whole session.
            outputSink: line =>
            {
                if (_coreStartupCapture.Elapsed <= CoreStartupCaptureWindow)
                {
                    DiagLog.Write("CORE_OUT " + line);
                }
            }
        );

        await procService.StartAsync();

        await Task.Delay(100);

        if (procService is null or { HasExited: true })
        {
            PublishDiagnostic(CoreStartupDiagnostics.Create(
                CoreHealthRole.Main, coreInfo?.CoreType, CoreStartupStage.StartProcess,
                "Core process exited during startup.", processExited: true,
                port: AppManager.Instance.GetLocalPort(EInboundProtocol.socks), canRecover: true));
            throw new Exception(ResUI.FailedToRunCore);
        }
        AddProcessJob(procService.Handle);

        return procService;
    }

    private void AddProcessJob(nint processHandle)
    {
        if (Utils.IsWindows())
        {
            _processJob ??= new();
            try
            {
                _processJob?.AddProcess(processHandle);
            }
            catch { }
        }
    }

    #endregion Process
}
