using AwesomeAssertions;

namespace ServiceLib.Tests.Services;

/// <summary>
/// End-to-end GPN split-tunnel flow: the managed routing rules
/// (<see cref="ManualRoutingRules.BuildManagedRules"/>) are generated into a real
/// sing-box config (via <see cref="CoreConfigSingboxService.GenerateClientConfigContent"/>),
/// and the process names the config routes to the tunnel (proxy outbound) must match
/// exactly the process set the Phase-2 capture bridge targets
/// (<see cref="GpnTargetResolverBridge.ExtractTargetNames"/>). If the two ever diverge,
/// an app that the rules send direct is still captured and tunneled by the bridge — the
/// "excluded apps still go through the VPN" regression.
/// </summary>
[Collection("SharedDatabase")]
public class GpnCaptureFlowIntegrationTests
{
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
                DomainStrategy4Singbox = string.Empty,
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
            CoreTypeItem = [new CoreTypeItem { ConfigType = EConfigType.VMess, CoreType = ECoreType.sing_box }],
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

    private static CoreConfigContext BuildContext(
        Config config,
        List<RulesItem> managedRules)
    {
        var node = new ProfileItem
        {
            IndexId = "node-1",
            ConfigType = EConfigType.VMess,
            CoreType = ECoreType.sing_box,
            Remarks = "test-node",
            Address = "example.com",
            Port = 443,
            Password = Guid.NewGuid().ToString(),
            Network = nameof(ETransport.raw),
            StreamSecurity = string.Empty,
            Subid = string.Empty,
        };
        node.SetProtocolExtra(node.GetProtocolExtra() with
        {
            AlterId = "0",
            VmessSecurity = Global.DefaultSecurity,
        });

        return new CoreConfigContext
        {
            Node = node,
            RunCoreType = ECoreType.sing_box,
            AppConfig = config,
            RoutingItem = new RoutingItem
            {
                Id = Utils.GetGuid(false),
                Remarks = "gpn-managed-routing",
                RuleSet = JsonUtils.Serialize(managedRules, false),
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
            RawDnsItem = null,
            SimpleDnsItem = config.SimpleDNSItem,
            AllProxiesMap = new Dictionary<string, ProfileItem> { [node.IndexId] = node },
            FullConfigTemplate = null,
            IsTunEnabled = true,
            ProtectDomainList = [],
        };
    }

    private static SingboxConfig GenerateAndAssert(IReadOnlyList<RulesItem> managed, Config config)
    {
        var context = BuildContext(config, [.. managed]);
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue("sing-box config generation must succeed: " + result.Msg);
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        cfg.Should().NotBeNull();
        return cfg;
    }

    /// <summary>
    /// Process names the GENERATED sing-box config routes to the tunnel (proxy outbound).
    /// The QUIC preemption emits outbound-less reject rules for the same processes, so
    /// only rules carrying an actual proxy outbound count.
    /// </summary>
    private static string[] TunneledProcessNames(IReadOnlyList<RulesItem> managed, Config config)
    {
        var cfg = GenerateAndAssert(managed, config);
        return cfg.route.rules
            .Where(r => r.process_name is { Count: > 0 } && r.outbound == Global.ProxyTag)
            .SelectMany(r => r.process_name!)
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
        var cfg = GenerateAndAssert(managed, config);
        var rules = cfg.route.rules;

        // Rules side: vpn apps get proxy process rules, direct app gets direct, block app gets reject.
        var proxyRules = rules.Where(r => r.process_name is { Count: > 0 } && r.outbound == Global.ProxyTag).ToList();
        proxyRules.SelectMany(r => r.process_name!).Should().Equal(["EscapeFromTarkov.exe", "lol.exe"]);

        rules.Should().Contain(r => r.process_name != null
            && r.process_name.Contains("notepad.exe") && r.outbound == Global.DirectTag);
        rules.Should().Contain(r => r.process_name != null
            && r.process_name.Contains("adware.exe") && r.outbound == null && r.action == "reject");
        rules.Should().Contain(r => r.port_range != null
            && r.port_range.Contains("0:65535") && r.outbound == Global.DirectTag,
            "whitelist catch-all must be direct");

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
        var cfg = GenerateAndAssert(managed, config);
        var rules = cfg.route.rules;

        // Rules side: excluded vpn apps → direct; cs2 (direct entry) → proxy; catch-all → proxy.
        rules.Should().Contain(r => r.process_name != null
            && r.process_name.Contains("EscapeFromTarkov.exe") && r.outbound == Global.DirectTag,
            "blacklist: vpn entry becomes direct exception");
        rules.Should().Contain(r => r.process_name != null
            && r.process_name.Contains("cs2.exe") && r.outbound == Global.ProxyTag,
            "blacklist: direct entry is inverted to the tunnel");
        rules.Should().Contain(r => r.port_range != null
            && r.port_range.Contains("0:65535") && r.outbound == Global.ProxyTag,
            "blacklist catch-all must tunnel unlisted apps");

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
        var fallbackContext = SystemProxyOnlyService.ToProxyOnlyContext(
            BuildContext(config, managed) with { IsTunEnabled = false });

        var strippedRules = JsonUtils.Deserialize<List<RulesItem>>(fallbackContext.RoutingItem!.RuleSet)!;
        strippedRules.Should().BeEmpty("managed split rules are meaningless without TUN and must be dropped");

        // Generate the real config: Global VPN path — route.final = proxy governs, and
        // no per-process rules remain to silently misroute the excluded apps.
        var result = new CoreConfigSingboxService(fallbackContext).GenerateClientConfigContent();
        result.Success.Should().BeTrue("fallback config generation must succeed: " + result.Msg);
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.route.final.Should().Be(Global.ProxyTag, "TUN-less fallback runs as Global VPN");
        cfg.route.rules.Where(r => r.process_name is { Count: > 0 })
            .Should().BeEmpty("no dead process_name rules in the fallback config");
        cfg.route.rules.Where(r => r.port_range != null && r.port_range.Contains("0:65535"))
            .Should().BeEmpty("no managed catch-all shadowing route.final");
    }

    [Fact]
    public async Task TunFallback_WhitelistDirectCatchAll_AlsoStripped()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        // Whitelist without TUN is the worst silent failure: the managed direct
        // catch-all would shadow route.final = proxy and leak EVERY unlisted
        // connection direct while the user believes they are tunneled.
        var apps = new[] { App("lol.exe", "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        managed.Should().Contain(r => r.OutboundTag == Global.DirectTag && r.Port == "0-65535",
            "whitelist catch-all is direct");

        var fallbackContext = SystemProxyOnlyService.ToProxyOnlyContext(
            BuildContext(config, managed) with { IsTunEnabled = false });

        var strippedRules = JsonUtils.Deserialize<List<RulesItem>>(fallbackContext.RoutingItem!.RuleSet)!;
        strippedRules.Should().BeEmpty("the whitelist direct catch-all must not survive the fallback");

        var result = new CoreConfigSingboxService(fallbackContext).GenerateClientConfigContent();
        result.Success.Should().BeTrue("fallback config generation must succeed: " + result.Msg);
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        cfg.route.final.Should().Be(Global.ProxyTag, "route.final = proxy must govern (no leak to direct)");
        cfg.route.rules.Where(r => r.port_range != null && r.port_range.Contains("0:65535"))
            .Should().BeEmpty("no managed catch-all in the fallback config");
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

            // 1) Rules → real sing-box config → the process names routed to the tunnel.
            var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: invert);
            var tunneled = TunneledProcessNames(managed, config);

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
