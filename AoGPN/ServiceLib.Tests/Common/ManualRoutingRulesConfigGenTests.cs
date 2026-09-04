using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Helper;
using ServiceLib.Models;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Common;

/// <summary>
/// Integration tests that verify the end-to-end flow from managed routing rules
/// through the sing-box config generator. Rather than stopping at the database,
/// these tests round-trip through
/// <see cref="CoreConfigSingboxService.GenerateClientConfigContent"/> and inspect
/// the emitted <c>route.rules</c> for per-app <c>process_name</c> entries and
/// correct catch-all routing.
/// </summary>
[Collection("SharedDatabase")]
public class ManualRoutingRulesConfigGenTests
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

    private static SplitTunnelAppItem App(
        string value,
        string action = "proxy",
        string entryType = "app",
        string port = "")
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

    // ----- Tests -----

    [Fact]
    public async Task GpnMode_WithTwoApps_GeneratesProcessNameRulesAndDirectCatchAll()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn"), App("msedge.exe", action: "direct") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);

        managed.Should().HaveCount(3);

        var cfg = GenerateAndAssert(managed, config);
        var rules = cfg.route.rules;

        // The QUIC preemption emits an outbound-less reject rule for the same
        // process (UDP/443), so pick the actual routing rule (outbound != null).
        var chromeRule = rules.FirstOrDefault(r =>
            r.process_name != null && r.process_name.Contains("chrome.exe") && r.outbound != null);
        chromeRule.Should().NotBeNull("GPN mode must emit a process_name rule for chrome.exe");
        chromeRule!.outbound.Should().Be(Global.ProxyTag);

        // QUIC preemption must accompany the proxy route: an outbound-less reject
        // rule blocks UDP/443 for the proxy-routed process, forcing it to fall
        // back to TCP which the TUN captures with process attribution.
        var quicRule = rules.FirstOrDefault(r =>
            r.process_name != null
            && r.process_name.Contains("chrome.exe")
            && r.outbound == null
            && r.action == "reject");
        quicRule.Should().NotBeNull("QUIC preemption must emit an outbound-less reject rule for proxy-routed chrome.exe");
        quicRule!.network.Should().Contain("udp");
        quicRule.port.Should().Contain(443);

        // Direct-routed processes must NOT get the QUIC reject rule: their
        // traffic already flows to direct, so nothing needs forcing back to TCP.
        var edgeQuic = rules.FirstOrDefault(r =>
            r.process_name != null
            && r.process_name.Contains("msedge.exe")
            && r.outbound == null
            && r.action == "reject");
        edgeQuic.Should().BeNull("direct-routed msedge.exe must not get a QUIC preemption reject rule");

        var edgeRule = rules.FirstOrDefault(r =>
            r.process_name != null && r.process_name.Contains("msedge.exe"));
        edgeRule.Should().NotBeNull("GPN mode must emit a process_name rule for msedge.exe");
        edgeRule!.outbound.Should().Be(Global.DirectTag);

        var catchAll = rules.FirstOrDefault(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.DirectTag);
        catchAll.Should().NotBeNull("GPN whitelist: catch-all must be direct");

        var tunnelAll = rules.Where(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.ProxyTag);
        tunnelAll.Should().BeEmpty("whitelist catch-all must not tunnel unlisted apps");
    }

    [Fact]
    public async Task GlobalVpnMode_NoProcessNameRules_CatchAllProxy()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn"), App("msedge.exe", action: "direct") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Vpn, apps);

        managed.Should().HaveCount(1);
        managed[0].OutboundTag.Should().Be(Global.ProxyTag);

        var cfg = GenerateAndAssert(managed, config);
        var rules = cfg.route.rules;

        var processRules = rules.Where(r => r.process_name != null && r.process_name.Count > 0);
        processRules.Should().BeEmpty("Global VPN must not emit per-app process_name rules");

        var catchAll = rules.FirstOrDefault(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.ProxyTag);
        catchAll.Should().NotBeNull("Global VPN must emit catch-all proxy");
    }

    [Fact]
    public async Task GpnMode_WithBlockApp_GeneratesRejectRule()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[] { App("adware.exe", action: "block") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);

        managed.Should().HaveCount(2);

        var cfg = GenerateAndAssert(managed, config);
        var rules = cfg.route.rules;

        var blockRule = rules.FirstOrDefault(r =>
            r.process_name != null && r.process_name.Contains("adware.exe"));
        blockRule.Should().NotBeNull("GPN mode must emit a process_name rule for adware.exe");
        blockRule!.action.Should().Be("reject", "Block action must map to sing-box reject");
        blockRule.outbound.Should().BeNull("Block rules use action=reject, not outbound");
    }

    [Fact]
    public async Task GpnMode_WithDomainEntry_GeneratesDomainRuleWithNoProcessName()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[] { App("discord.gg", entryType: "domain", action: "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);

        managed.Should().HaveCount(2);
        managed[0].Domain.Should().ContainSingle("discord.gg");
        managed[0].Process.Should().BeNull();

        var cfg = GenerateAndAssert(managed, config);
        var rules = cfg.route.rules;

        var domainRule = rules.FirstOrDefault(r =>
            r.domain_keyword != null && r.domain_keyword.Contains("discord.gg"));
        domainRule.Should().NotBeNull("A domain entry must produce a domain_keyword rule");
        domainRule!.outbound.Should().Be(Global.ProxyTag);
        domainRule.process_name.Should().BeNull("Domain rules must not carry a process_name list");
    }

    [Fact]
    public async Task GpnMode_InvertManualBlacklist_FlipsGeneratedOutbounds()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[] { App("steam.exe", action: "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: true);

        managed.Should().HaveCount(2);
        managed[0].OutboundTag.Should().Be(Global.DirectTag);
        managed[1].OutboundTag.Should().Be(Global.ProxyTag);

        var cfg = GenerateAndAssert(managed, config);
        var rules = cfg.route.rules;

        var steamRule = rules.FirstOrDefault(r =>
            r.process_name != null && r.process_name.Contains("steam.exe"));
        steamRule.Should().NotBeNull();
        steamRule!.outbound.Should().Be(Global.DirectTag, "blacklist: VPN entry becomes direct exception");

        var catchAll = rules.FirstOrDefault(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.ProxyTag);
        catchAll.Should().NotBeNull("blacklist: catch-all must be proxy");
    }

    [Fact]
    public async Task StaleDirectCatchAllInActiveProfile_IsManagedAndRewrittenToProxy_ForGlobalVpn()
    {
        // Regression: the live user DB carried "AoGPN Manuel varsayılan 0-65535 →
        // direct" in the ACTIVE (Global) routing profile — left over from a GPN
        // whitelist apply. Global VPN then routed everything direct and the IP
        // verification panel reported a leak. SaveRulesAsync must treat that rule
        // as managed and rewrite the catch-all to proxy for ModeVpn.
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        // Simulate the stale profile: managed direct catch-all + a stray preserved rule.
        var stale = new List<RulesItem>
        {
            ManualRoutingRules.BuildCatchAllRule(Global.DirectTag),
            new RulesItem
            {
                Id = Utils.GetGuid(false),
                Remarks = "stray",
                OutboundTag = Global.DirectTag,
                Domain = ["geosite:private"],
                Enabled = true,
            },
        };

        // SaveRulesAsync keeps non-managed rules and regenerates managed ones for
        // the active mode. Replicate its exact filter + generation.
        var preserved = stale.Where(r => !ManualRoutingRules.IsManagedRule(r)).ToList();
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Vpn, []);
        var final = managed.Concat(preserved).ToList();

        final.Should().HaveCount(2, "managed catch-all (proxy) + preserved stray rule");
        final[0].OutboundTag.Should().Be(Global.ProxyTag, "Global VPN catch-all must be rewritten to proxy");
        final[1].Remarks.Should().Be("stray", "user-defined rule must be preserved");
        final[1].OutboundTag.Should().Be(Global.DirectTag);

        var cfg = GenerateAndAssert(final, config);
        var rules = cfg.route.rules;

        rules.Should().Contain(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.ProxyTag);
        rules.Should().NotContain(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.DirectTag,
            "stale direct catch-all must be gone in Global VPN");
    }

    [Fact]
    public async Task SwitchFromGlobalVpnToGpn_ManagedRulesToggledCorrectly()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn"), App("msedge.exe", action: "direct") };

        // Step 1: Global VPN
        var vpnManaged = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Vpn, apps);
        vpnManaged.Should().HaveCount(1);

        var vpnCfg = GenerateAndAssert(vpnManaged, config);
        var vpnRules = vpnCfg.route.rules;

        vpnRules.Where(r => r.process_name != null && r.process_name.Count > 0)
            .Should().BeEmpty();
        vpnRules.Should().Contain(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.ProxyTag);

        // Step 2: Switch to GPN
        var gpnManaged = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        gpnManaged.Should().HaveCount(3);

        var gpnCfg = GenerateAndAssert(gpnManaged, config);
        var gpnRules = gpnCfg.route.rules;

        // Skip the QUIC preemption reject rule (outbound-less) for the same process.
        var chromeRule = gpnRules.FirstOrDefault(r =>
            r.process_name != null && r.process_name.Contains("chrome.exe") && r.outbound != null);
        chromeRule.Should().NotBeNull();
        chromeRule!.outbound.Should().Be(Global.ProxyTag);

        // GPN mode must also carry the QUIC preemption reject rule (UDP/443).
        var quicRule = gpnRules.FirstOrDefault(r =>
            r.process_name != null
            && r.process_name.Contains("chrome.exe")
            && r.outbound == null
            && r.action == "reject");
        quicRule.Should().NotBeNull("QUIC preemption reject rule must survive the VPN→GPN switch");
        quicRule!.network.Should().Contain("udp");
        quicRule.port.Should().Contain(443);

        // Direct-routed processes must not carry the QUIC reject rule.
        var edgeQuic = gpnRules.FirstOrDefault(r =>
            r.process_name != null
            && r.process_name.Contains("msedge.exe")
            && r.outbound == null
            && r.action == "reject");
        edgeQuic.Should().BeNull("direct-routed msedge.exe must not get a QUIC preemption reject rule after the switch");

        var edgeRule = gpnRules.FirstOrDefault(r =>
            r.process_name != null && r.process_name.Contains("msedge.exe"));
        edgeRule.Should().NotBeNull();
        edgeRule!.outbound.Should().Be(Global.DirectTag);

        gpnRules.Should().Contain(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.DirectTag);

        // Step 3: Off
        var offManaged = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Off, apps);
        offManaged.Should().HaveCount(1);

        var offCfg = GenerateAndAssert(offManaged, config);
        var offRules = offCfg.route.rules;

        offRules.Where(r => r.process_name != null && r.process_name.Count > 0)
            .Should().BeEmpty();
        offRules.Should().Contain(r =>
            r.port_range != null && r.port_range.Contains("0:65535") && r.outbound == Global.DirectTag);
    }
}