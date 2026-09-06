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

namespace ServiceLib.Tests.Common;

/// <summary>
/// Integration tests that verify the end-to-end flow from managed routing rules
/// through the mihomo (GPN) config generator. Rather than stopping at the database,
/// these tests round-trip through
/// <see cref="CoreConfigHandler.GenerateClientConfig"/> and inspect the emitted
/// mihomo <c>rules</c> rows for per-app PROCESS-NAME entries and correct catch-all
/// routing (MATCH → wg-&lt;id&gt; / DIRECT).
/// </summary>
[Collection("SharedDatabase")]
public class ManualRoutingRulesConfigGenTests
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

    private static CoreConfigContext BuildContext(
        Config config,
        List<RulesItem> managedRules)
    {
        var node = GpnCoreLauncher.BuildWireGuardProfile(Server());
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

        var rules = await GenerateAndAssert(managed, config);

        rules.Should().Contain($"PROCESS-NAME,chrome.exe,{TunnelTarget()}");
        rules.Should().Contain("PROCESS-NAME,msedge.exe,DIRECT");
        rules.Should().Contain("MATCH,DIRECT");
        rules.Should().NotContain("MATCH," + TunnelTarget());
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

        var rules = await GenerateAndAssert(managed, config);

        rules.Where(r => r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("Global VPN must not emit per-app PROCESS-NAME rules");
        rules.Should().Contain($"MATCH,{TunnelTarget()}");
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

        var rules = await GenerateAndAssert(managed, config);

        rules.Should().Contain("PROCESS-NAME,adware.exe,REJECT");
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

        var rules = await GenerateAndAssert(managed, config);

        rules.Should().Contain($"DOMAIN-SUFFIX,discord.gg,{TunnelTarget()}");
        rules.Should().NotContain(r => r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase));
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

        var rules = await GenerateAndAssert(managed, config);

        rules.Should().Contain("PROCESS-NAME,steam.exe,DIRECT");
        rules.Should().Contain($"MATCH,{TunnelTarget()}");
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

        var rules = await GenerateAndAssert(final, config);

        rules.Should().Contain($"MATCH,{TunnelTarget()}");
        rules.Should().NotContain("MATCH,DIRECT", "stale direct catch-all must be gone in Global VPN");
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

        var vpnRules = await GenerateAndAssert(vpnManaged, config);
        vpnRules.Where(r => r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
        vpnRules.Should().Contain($"MATCH,{TunnelTarget()}");

        // Step 2: Switch to GPN
        var gpnManaged = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        gpnManaged.Should().HaveCount(3);

        var gpnRules = await GenerateAndAssert(gpnManaged, config);
        gpnRules.Should().Contain($"PROCESS-NAME,chrome.exe,{TunnelTarget()}");
        gpnRules.Should().Contain("PROCESS-NAME,msedge.exe,DIRECT");
        gpnRules.Should().Contain("MATCH,DIRECT");

        // Step 3: Off
        var offManaged = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Off, apps);
        offManaged.Should().HaveCount(1);

        var offRules = await GenerateAndAssert(offManaged, config);
        offRules.Where(r => r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
        offRules.Should().Contain("MATCH,DIRECT");
    }
}