using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Models;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Services.CoreConfig;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// End-to-end GPN split-tunnel flow: the managed routing rules
/// (<see cref="ManualRoutingRules.BuildManagedRules"/>) are generated into a real
/// mihomo YAML config (via <see cref="CoreConfigHandler.GenerateClientConfig"/>),
/// and the process names the config routes to the tunnel (wg-&lt;id&gt; target) must match
/// exactly the process set the Phase-2 capture bridge targets
/// (<see cref="GpnTargetResolverBridge.ExtractTargetNames"/>). If the two ever diverge,
/// an app that the rules send direct is still captured and tunneled by the bridge — the
/// "excluded apps still go through the VPN" regression.
/// </summary>
[Collection("SharedDatabase")]
public class GpnCaptureFlowIntegrationTests
{
    private const string ServerId = "de";

    private static async Task CleanRoutingItemsAsync()
    {
        // Tabloları AppManager.InitApp yaratır ama test host'u onu hiç çalıştırmaz;
        // temiz bir bin'de guiNDB.db tablosuzdur ve bu sorgu "no such table" ile düşer.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        var all = await SQLiteHelper.Instance.TableAsync<RoutingItem>().ToListAsync();
        foreach (var r in all)
        {
            await SQLiteHelper.Instance.DeleteAsync(r);
        }
    }

    private static Config CreateConfig()
    {
        return new Config
        {
            CoreBasicItem = new CoreBasicItem { Loglevel = "warning" },
            TunModeItem = new TunModeItem { EnableTun = true, IcmpRouting = "default" },
            RoutingBasicItem = new RoutingBasicItem
            {
                DomainStrategy = Global.AsIs,
                RoutingIndexId = string.Empty,
            },
            GuiItem = new GUIItem { EnableStatistics = false },
            UiItem = new UIItem
            {
                CurrentLanguage = "en",
                CurrentFontFamily = "sans",
                MainColumnItem = [],
                WindowSizeItem = [],
            },
            ConstItem = new ConstItem(),
            SimpleDNSItem = new SimpleDNSItem
            {
                BootstrapDNS = Global.DomainPureIPDNSAddress.FirstOrDefault(),
                ServeStale = false,
                ParallelQuery = false,
                Strategy4Freedom = Global.AsIs,
                Strategy4Proxy = Global.AsIs,
                Strategy4ProxyDial = Global.AsIs,
            },
            KcpItem = new KcpItem(),
            GrpcItem = new GrpcItem(),
            HysteriaItem = new HysteriaItem(),
            Mux4RayItem = new Mux4RayItem(),
            Mux4SboxItem = new Mux4SboxItem(),
            Inbound = [new InItem
            {
                Protocol = nameof(EInboundProtocol.socks),
                LocalPort = 10808,
                UdpEnabled = true,
                SniffingEnabled = true,
            }],
        };
    }

    private static void BindConfig(Config config)
    {
        var field = typeof(AppManager).GetField(
            "_config", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(AppManager.Instance, config);
    }

    private static SplitTunnelAppItem App(string value, string action, string entryType = "app", string port = "")
    {
        return new SplitTunnelAppItem
        {
            EntryType = entryType,
            Value = value,
            Port = port,
            Action = action,
        };
    }

    private static GpnServerProfile Server() => new(
        ServerId: ServerId,
        Name: "Almanya",
        EndpointHost: "130.61.223.36",
        EndpointPort: 51820,
        ServerPublicKey: "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
        ClientPrivateKey: "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
        ClientAddress: "10.66.66.2/24",
        Mtu: 1420,
        PersistentKeepalive: 25);

    /// <summary>Global VPN (fallback) için VLESS profil — mihomo global dalını tetikler.</summary>
    private static ProfileItem VlessNode()
    {
        var node = new ProfileItem
        {
            IndexId = "vless-1",
            ConfigType = EConfigType.VLESS,
            CoreType = ECoreType.mihomo,
            Remarks = "global-node",
            Address = "92.4.137.125",
            Port = 443,
            Id = Guid.NewGuid().ToString(),
            Network = nameof(ETransport.raw),
            StreamSecurity = Global.StreamSecurityReality,
            Sni = "example.com",
            PublicKey = "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
            ShortId = "abc123",
            Fingerprint = "chrome",
            Subid = string.Empty,
        };
        node.SetProtocolExtra(node.GetProtocolExtra() with { Flow = string.Empty });
        return node;
    }

    private static CoreConfigContext BuildContext(
        Config config,
        List<RulesItem> managedRules,
        ProfileItem? node = null)
    {
        node ??= GpnCoreLauncher.BuildWireGuardProfile(Server());
        node.Remarks = "test-node";

        return new CoreConfigContext
        {
            Node = node,
            RunCoreType = ECoreType.mihomo,
            AppConfig = config,
            RoutingItem = new RoutingItem
            {
                Id = Utils.GetGuid(false),
                Remarks = "gpn-managed-routing",
                RuleSet = JsonUtils.Serialize(managedRules, false),
                DomainStrategy = Global.AsIs,
            },
            RawDnsItem = null,
            SimpleDnsItem = config.SimpleDNSItem,
            AllProxiesMap = new Dictionary<string, ProfileItem> { [node.IndexId] = node },
            FullConfigTemplate = null,
            IsTunEnabled = true,
            ProtectDomainList = [],
        };
    }

    private static async Task<List<string>> GenerateAndAssert(IReadOnlyList<RulesItem> managed, Config config)
    {
        var context = BuildContext(config, [.. managed]);
        var result = await CoreConfigHandler.GenerateClientConfig(context, null);

        result.Success.Should().BeTrue("mihomo config generation must succeed: " + result.Msg);
        var rules = RouteTesterService.ExtractRules(result.Data!.ToString()!);
        rules.Should().NotBeEmpty("generated mihomo config must carry route rules");
        return rules;
    }

    private static string TunnelTarget() => GpnMihomoConfigService.WireGuardProxyName(Server());

    /// <summary>
    /// Process names the GENERATED mihomo config routes to the tunnel
    /// (PROCESS-NAME rows whose target is the wg-&lt;id&gt; proxy).
    /// </summary>
    private static async Task<string[]> TunneledProcessNames(IReadOnlyList<RulesItem> managed, Config config)
    {
        var rules = await GenerateAndAssert(managed, config);
        return rules
            .Where(r => r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Split(','))
            .Where(parts => parts.Length == 3 && parts[2] == TunnelTarget())
            .Select(parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] Sorted(IEnumerable<string> names)
        => names.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // ----- Whitelist direction -----

    [Fact]
    public async Task GpnWhitelist_CaptureBridgeTargets_AreTheTunneledVpnApps()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        // Whitelist: only "vpn"-actioned apps are tunneled; direct/block entries and
        // unlisted apps stay direct (catch-all direct).
        var apps = new[]
        {
            App("EscapeFromTarkov.exe", "vpn"),
            App("lol.exe", "vpn"),
            App("notepad.exe", "direct"),
            App("adware.exe", "block"),
        };

        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var rules = await GenerateAndAssert(managed, config);

        // Rules side: vpn apps get proxy process rules, direct app gets direct, block app gets reject.
        var proxyRules = rules.Where(r => r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase)
            && r.EndsWith("," + TunnelTarget(), StringComparison.OrdinalIgnoreCase)).ToList();
        proxyRules.SelectMany(r => new[] { r.Split(',')[1] }).Should().Equal(["EscapeFromTarkov.exe", "lol.exe"]);

        rules.Should().Contain("PROCESS-NAME,notepad.exe,DIRECT");
        rules.Should().Contain("PROCESS-NAME,adware.exe,REJECT");
        rules.Should().Contain("MATCH,DIRECT", "whitelist catch-all must be direct");

        // Bridge side: exactly the tunneled apps.
        GpnTargetResolverBridge.ExtractTargetNames(apps, invertManual: false)
            .Should().Equal(["EscapeFromTarkov.exe", "lol.exe"],
                "whitelist capture bridge targets = the vpn-actioned apps");
    }

    // ----- Blacklist direction -----

    [Fact]
    public async Task GpnBlacklist_ExcludedVpnApps_AreNotCapturedByBridge()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        // Blacklist (exclude): "vpn"-actioned entries are the exceptions that stay
        // direct; "direct"-actioned entries are inverted to the tunnel. The capture
        // bridge must therefore target the "direct" entries and NEVER the excluded ones.
        var apps = new[]
        {
            App("EscapeFromTarkov.exe", "vpn"), // dışlanan → yakalanmamalı
            App("lol.exe", "vpn"),              // dışlanan → yakalanmamalı
            App("cs2.exe", "direct"),           // kara listede tünellenir → hedef
            App("adware.exe", "block"),         // engellenir → hedef dışı
        };

        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: true);
        var rules = await GenerateAndAssert(managed, config);

        // Rules side: excluded vpn apps → direct; cs2 (direct entry) → proxy; catch-all → proxy.
        rules.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,DIRECT",
            "blacklist: vpn entry becomes direct exception");
        rules.Should().Contain($"PROCESS-NAME,cs2.exe,{TunnelTarget()}",
            "blacklist: direct entry is inverted to the tunnel");
        rules.Should().Contain($"MATCH,{TunnelTarget()}", "blacklist catch-all must tunnel unlisted apps");

        // Bridge side: only the tunneled cs2.exe — the excluded vpn apps must be absent
        // (this is the regression the fix targets: they used to be captured anyway).
        var targets = GpnTargetResolverBridge.ExtractTargetNames(apps, invertManual: true);
        targets.Should().ContainSingle("cs2.exe");
        targets.Should().NotContain("EscapeFromTarkov.exe");
        targets.Should().NotContain("lol.exe");
    }

    // ----- TUN-missing fallback (Global VPN via system proxy) -----

    [Fact]
    public async Task TunFallback_ManagedSplitRulesStripped_BecomesHonestGlobalVpn()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        // Simulate the TUN-missing fallback: a GPN blacklist config whose managed
        // process_name rules would be dead — without the TUN inbound the SOCKS/proxy
        // path has no PID attribution, so the excluded (vpn) apps would silently be
        // tunneled by the catch-all and the user would see Global VPN behaviour.
        var apps = new[]
        {
            App("EscapeFromTarkov.exe", "vpn"), // dışlanan → direct istisnası
            App("cs2.exe", "direct"),           // tünellenen giriş
        };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: true);

        // The fallback strips the app-managed rules and regenerates with TUN disabled.
        // mihomo Global VPN dalı (MihomoGlobalConfigService) devreye girer: TUN kapalı,
        // mixed-port açık, kural tek MATCH → global-proxy.
        var fallbackContext = SystemProxyOnlyService.ToProxyOnlyContext(
            BuildContext(config, managed, VlessNode()) with { IsTunEnabled = false });

        var strippedRules = JsonUtils.Deserialize<List<RulesItem>>(fallbackContext.RoutingItem!.RuleSet)!;
        strippedRules.Should().BeEmpty("managed split rules are meaningless without TUN and must be dropped");

        var result = await CoreConfigHandler.GenerateClientConfig(fallbackContext, null);
        result.Success.Should().BeTrue("fallback config generation must succeed: " + result.Msg);
        var yaml = result.Data!.ToString()!;

        yaml.Should().Contain($"MATCH,{MihomoGlobalConfigService.GlobalProxyName}",
            "TUN-less fallback runs as Global VPN — single MATCH → proxy rule");
        yaml.Should().Contain("tun:", "mihomo config always carries the tun block");
        yaml.Should().Contain("enable: false", "fallback disables the TUN");
        RouteTesterService.ExtractRules(yaml).Where(r => r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("no dead PROCESS-NAME rules in the fallback config");
    }

    [Fact]
    public async Task TunFallback_WhitelistDirectCatchAll_AlsoStripped()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        // Whitelist without TUN is the worst silent failure: the managed direct
        // catch-all would shadow the proxy fall-through and leak EVERY unlisted
        // connection direct while the user believes they are tunneled.
        var apps = new[] { App("lol.exe", "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        managed.Should().Contain(r => r.OutboundTag == Global.DirectTag && r.Port == "0-65535",
            "whitelist catch-all is direct");

        var fallbackContext = SystemProxyOnlyService.ToProxyOnlyContext(
            BuildContext(config, managed, VlessNode()) with { IsTunEnabled = false });

        var strippedRules = JsonUtils.Deserialize<List<RulesItem>>(fallbackContext.RoutingItem!.RuleSet)!;
        strippedRules.Should().BeEmpty("the whitelist direct catch-all must not survive the fallback");

        var result = await CoreConfigHandler.GenerateClientConfig(fallbackContext, null);
        result.Success.Should().BeTrue("fallback config generation must succeed: " + result.Msg);
        var rules = RouteTesterService.ExtractRules(result.Data!.ToString()!);

        rules.Should().Contain($"MATCH,{MihomoGlobalConfigService.GlobalProxyName}",
            "MATCH → proxy must govern (no leak to direct)");
        rules.Should().NotContain("MATCH,DIRECT", "stale direct catch-all must not survive the fallback");
    }

    // ----- Whole GPN flow: rules ⇔ bridge agreement for both directions -----

    [Fact]
    public async Task GpnFlow_RulesAndCaptureBridge_AgreeForBothDirections()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[]
        {
            App("EscapeFromTarkov.exe", "vpn"),
            App("lol.exe", "vpn"),
            App("cs2.exe", "direct"),
            App("notepad.exe", "direct"),
            App("adware.exe", "block"),
        };

        foreach (var invert in new[] { false, true })
        {
            var direction = invert ? "blacklist" : "whitelist";

            // 1) Rules → real mihomo config → the process names routed to the tunnel.
            var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: invert);
            var tunneled = await TunneledProcessNames(managed, config);

            // 2) The capture bridge's target set.
            var bridgeTargets = Sorted(GpnTargetResolverBridge.ExtractTargetNames(apps, invertManual: invert));

            // 3) The invariant: the bridge captures exactly what the rules tunnel.
            bridgeTargets.Should().Equal(tunneled,
                $"{direction}: capture-bridge targets must match the config's tunneled process set");

            if (invert)
            {
                // Excluded (vpn) apps are direct in the rules — they must never be
                // in the bridge target set (the fixed regression).
                bridgeTargets.Should().NotContain("EscapeFromTarkov.exe");
                bridgeTargets.Should().NotContain("lol.exe");
                bridgeTargets.Should().Equal(["cs2.exe", "notepad.exe"], "blacklist tunnels only direct entries");
            }
            else
            {
                bridgeTargets.Should().Equal(["EscapeFromTarkov.exe", "lol.exe"], "whitelist tunnels only vpn entries");
            }
        }
    }
}