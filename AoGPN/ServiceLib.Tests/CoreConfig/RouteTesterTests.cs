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

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Pure unit tests for the offline sing-box route simulator. No database or process
/// is involved: rules are constructed directly and matched against synthetic probes.
/// </summary>
public class SingboxRouteSimulatorTests
{
    // ---- helpers ----

    private static Rule4Sbox SimpleRule(string? outbound = null, string? action = null)
    {
        return new Rule4Sbox { outbound = outbound, action = action };
    }

    private static RouteProbe Probe(
        string processName = "",
        string domain = "",
        string? ip = null,
        int? port = null,
        string network = "",
        string protocol = "",
        string clashMode = "Rule")
    {
        return new RouteProbe
        {
            ProcessName = processName,
            Domain = domain,
            IpAddress = ip is null ? null : IPAddress.Parse(ip),
            Port = port,
            Network = network,
            Protocol = protocol,
            ClashMode = clashMode,
        };
    }

    private static RouteTestResult Simulate(
        List<Rule4Sbox> rules,
        RouteProbe probe,
        SingboxRouteSimulator.RuleSetMatcher? matcher = null,
        string? final = null)
    {
        // FindFirstMatch stores the provided list in the result, so the warnings
        // surface on the returned RouteTestResult directly.
        return SingboxRouteSimulator.FindFirstMatch(rules, probe, matcher, final, warnings: new List<string>());
    }

    // ---- process matching ----

    [Fact]
    public void ProcessName_MatchesFirstTerminalRule()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { process_name = ["chrome.exe"], outbound = Global.ProxyTag },
            new() { port_range = ["0:65535"], outbound = Global.DirectTag },
        };

        var result = Simulate(rules, Probe(processName: "chrome.exe"));

        result.Matched.Should().BeTrue();
        result.Match!.RuleIndex.Should().Be(1);
        result.Match.OutcomeRaw.Should().Be(Global.ProxyTag);
        result.Match.Outcome.Should().Be("Proxy");
        result.Match.Criteria.Should().Contain("process_name: chrome.exe");
    }

    [Fact]
    public void ProcessName_IsCaseInsensitive()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { process_name = ["Chrome.EXE"], outbound = Global.ProxyTag },
        };

        var result = Simulate(rules, Probe(processName: "chrome.exe"));
        result.Matched.Should().BeTrue();
    }

    [Fact]
    public void ProcessName_UnknownProcess_FallsThroughToNextRule()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { process_name = ["chrome.exe"], outbound = Global.ProxyTag },
            new() { port_range = ["0:65535"], outbound = Global.DirectTag },
        };

        var result = Simulate(rules, Probe(processName: "msedge.exe"), final: Global.DirectTag);

        result.Matched.Should().BeTrue();
        result.Match!.RuleIndex.Should().Be(2);
        result.Match.OutcomeRaw.Should().Be(Global.DirectTag);
    }

    [Fact]
    public void NoMatch_ReportsFinalFallback()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { process_name = ["chrome.exe"], outbound = Global.ProxyTag },
        };

        var result = Simulate(rules, Probe(processName: "msedge.exe"), final: Global.DirectTag);

        result.Matched.Should().BeFalse();
        result.Match.Should().BeNull();
        result.FinalFallback.Should().Be(Global.DirectTag);
    }

    // ---- domain matching ----

    [Fact]
    public void DomainSuffix_MatchesSubdomainsAndExact()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { domain_suffix = ["google.com"], outbound = Global.ProxyTag },
        };

        Simulate(rules, Probe(domain: "www.google.com")).Matched.Should().BeTrue();
        Simulate(rules, Probe(domain: "google.com")).Matched.Should().BeTrue();
        Simulate(rules, Probe(domain: "notgoogle.com")).Matched.Should().BeFalse();
    }

    [Fact]
    public void DomainKeyword_MatchesSubstring()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { domain_keyword = ["discord"], outbound = Global.ProxyTag },
        };

        var result = Simulate(rules, Probe(domain: "cdn.discordapp.com"));
        result.Matched.Should().BeTrue();
        result.Match!.Criteria.Should().Contain("domain_keyword: [discord]");
    }

    [Fact]
    public void DomainRegex_MatchesPattern()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { domain_regex = ["(^|\\.)example\\.com$"], outbound = Global.ProxyTag },
        };

        Simulate(rules, Probe(domain: "sub.example.com")).Matched.Should().BeTrue();
        Simulate(rules, Probe(domain: "example.org")).Matched.Should().BeFalse();
    }

    [Fact]
    public void DomainProbe_NeverMatchesIpOnlyRules()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { ip_cidr = ["10.0.0.0/8"], outbound = Global.ProxyTag },
        };

        var result = Simulate(rules, Probe(domain: "example.com"), final: Global.DirectTag);
        result.Matched.Should().BeFalse("a domain probe must not be matched against IP rules without DNS resolution");
    }

    // ---- IP matching ----

    [Fact]
    public void IpCidr_MatchesWithinRange()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { ip_cidr = ["10.0.0.0/8"], outbound = Global.DirectTag },
        };

        Simulate(rules, Probe(ip: "10.1.2.3")).Matched.Should().BeTrue();
        Simulate(rules, Probe(ip: "11.1.2.3")).Matched.Should().BeFalse();
    }

    [Fact]
    public void IpCidr_PlainIpMatchesItself()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { ip_cidr = ["1.2.3.4"], outbound = Global.DirectTag },
        };

        Simulate(rules, Probe(ip: "1.2.3.4")).Matched.Should().BeTrue();
        Simulate(rules, Probe(ip: "1.2.3.5")).Matched.Should().BeFalse();
    }

    [Fact]
    public void IpIsPrivate_MatchesPrivateAddresses()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { ip_is_private = true, outbound = Global.DirectTag },
        };

        Simulate(rules, Probe(ip: "192.168.1.1")).Matched.Should().BeTrue();
        Simulate(rules, Probe(ip: "10.0.0.5")).Matched.Should().BeTrue();
        Simulate(rules, Probe(ip: "8.8.8.8")).Matched.Should().BeFalse();
    }

    // ---- port / network / protocol ----

    [Fact]
    public void PortAndPortRange_MatchProbePort()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { port = [443], outbound = Global.ProxyTag },
            new() { port_range = ["8000:9000"], outbound = Global.DirectTag },
        };

        Simulate(rules, Probe(port: 443)).Matched.Should().BeTrue();
        Simulate(rules, Probe(port: 80)).Matched.Should().BeFalse("no rule matches port 80");

        var rangeResult = Simulate(
            [new Rule4Sbox { port_range = ["8000:9000"], outbound = Global.DirectTag }],
            Probe(port: 8500));
        rangeResult.Matched.Should().BeTrue();
    }

    [Fact]
    public void Network_MustMatch()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { network = ["udp"], outbound = Global.DirectTag },
        };

        Simulate(rules, Probe(network: "udp")).Matched.Should().BeTrue();
        Simulate(rules, Probe(network: "tcp")).Matched.Should().BeFalse();
    }

    [Fact]
    public void Protocol_MustMatch()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { protocol = ["dns"], outbound = Global.DirectTag },
        };

        Simulate(rules, Probe(protocol: "dns")).Matched.Should().BeTrue();
        Simulate(rules, Probe(protocol: "tls")).Matched.Should().BeFalse();
    }

    // ---- rule semantics ----

    [Fact]
    public void CatchAllRule_WithNoFields_MatchesEverything()
    {
        var rules = new List<Rule4Sbox>
        {
            SimpleRule(outbound: Global.DirectTag),
        };

        var result = Simulate(rules, Probe(processName: "anything.exe", domain: "whatever.com"));
        result.Matched.Should().BeTrue();
        result.Match!.Description.Should().Contain("match-all");
    }

    [Fact]
    public void LogicalOr_MatchesAnySubRule()
    {
        var rules = new List<Rule4Sbox>
        {
            new()
            {
                type = "logical",
                mode = "or",
                outbound = Global.ProxyTag,
                rules =
                [
                    new Rule4Sbox { domain_keyword = ["alpha"] },
                    new Rule4Sbox { domain_keyword = ["beta"] },
                ],
            },
        };

        Simulate(rules, Probe(domain: "beta.example.com")).Matched.Should().BeTrue();
        Simulate(rules, Probe(domain: "gamma.example.com")).Matched.Should().BeFalse();
    }

    [Fact]
    public void Invert_FlipsTheMatch()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { domain = ["google.com"], invert = true, outbound = Global.DirectTag },
        };

        Simulate(rules, Probe(domain: "example.com")).Matched.Should().BeTrue();
        Simulate(rules, Probe(domain: "google.com")).Matched.Should().BeFalse();
    }

    [Fact]
    public void SniffModifier_AppliesThenEvaluationContinues()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { action = "sniff" },
            new() { process_name = ["chrome.exe"], outbound = Global.ProxyTag },
        };

        var result = Simulate(rules, Probe(processName: "chrome.exe"));

        result.Matched.Should().BeTrue();
        result.Modifiers.Should().ContainSingle(m => m.OutcomeRaw == "sniff");
        result.Match!.RuleIndex.Should().Be(2);
        result.Match.OutcomeRaw.Should().Be(Global.ProxyTag);
    }

    [Fact]
    public void RejectAction_IsTerminalAndReportsAction()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { process_name = ["adware.exe"], action = "reject" },
        };

        var result = Simulate(rules, Probe(processName: "adware.exe"));

        result.Matched.Should().BeTrue();
        result.Match!.OutcomeKind.Should().Be("action");
        result.Match.OutcomeRaw.Should().Be("reject");
        result.Match.Outcome.Should().Be("Reject (block)");
    }

    [Fact]
    public void ClashMode_RuleByDefault_DoesNotMatchGlobalOrDirectRules()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { outbound = Global.DirectTag, clash_mode = nameof(ERuleMode.Direct) },
            new() { outbound = Global.ProxyTag, clash_mode = nameof(ERuleMode.Global) },
        };

        var result = Simulate(rules, Probe(), final: Global.ProxyTag);
        result.Matched.Should().BeFalse();
        result.FinalFallback.Should().Be(Global.ProxyTag);
    }

    // ---- rule-sets ----

    [Fact]
    public void RuleSet_MatchedViaInjectedEvaluator()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { rule_set = ["geosite-cn"], outbound = Global.DirectTag },
        };

        var result = Simulate(
            rules,
            Probe(domain: "baidu.com"),
            matcher: (tag, p) => new SingboxRouteSimulator.RuleSetEvaluation(true, true));

        result.Matched.Should().BeTrue();
        result.Match!.OutcomeRaw.Should().Be(Global.DirectTag);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void RuleSet_UnavailableLocalData_AddsWarningAndDoesNotMatch()
    {
        var rules = new List<Rule4Sbox>
        {
            new() { rule_set = ["geosite-cn"], outbound = Global.DirectTag },
        };

        var result = Simulate(
            rules,
            Probe(domain: "baidu.com"),
            matcher: (tag, p) => new SingboxRouteSimulator.RuleSetEvaluation(false, false),
            final: Global.ProxyTag);

        result.Matched.Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("geosite-cn", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Integration tests: managed per-app rules flow through the real sing-box config
/// generator and the simulator picks the exact rule the core would apply.
/// </summary>
[Collection("SharedDatabase")]
public class RouteTesterConfigGenTests
{
    private static async Task CleanRoutingItemsAsync()
    {
        var all = await SQLiteHelper.Instance.TableAsync<RoutingItem>().ToListAsync();
        foreach (var r in all)
        {
            await SQLiteHelper.Instance.DeleteAsync(r);
        }
    }

    private static Config CreateConfig(bool enableTun)
    {
        return new Config
        {
            CoreBasicItem = new CoreBasicItem { Loglevel = "warning" },
            TunModeItem = new TunModeItem { EnableTun = enableTun, IcmpRouting = "default" },
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

    private static SplitTunnelAppItem App(string value, string action = "proxy", string entryType = "app")
    {
        return new SplitTunnelAppItem
        {
            EntryType = entryType,
            Value = value,
            Action = action,
        };
    }

    private static CoreConfigContext BuildContext(Config config, List<RulesItem> managedRules)
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
            IsTunEnabled = config.TunModeItem.EnableTun,
            ProtectDomainList = [],
        };
    }

    private static SingboxConfig Generate(IReadOnlyList<RulesItem> managed, Config config)
    {
        var context = BuildContext(config, [.. managed]);
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue("sing-box config generation must succeed: " + result.Msg);
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
    }

    [Fact]
    public async Task GpnMode_ChromeToDiscord_MatchesProcessRuleProxy()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn"), App("msedge.exe", action: "direct") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var cfg = Generate(managed, config);

        var probe = new RouteProbe
        {
            ProcessName = "chrome.exe",
            Domain = "discord.gg",
            Port = 443,
            Network = "tcp",
        };
        var result = SingboxRouteSimulator.FindFirstMatch(
            cfg.route.rules, probe, finalFallback: cfg.route.final, configMode: "gpn");

        result.Matched.Should().BeTrue();
        result.Match!.OutcomeRaw.Should().Be(Global.ProxyTag);
        result.Match.Criteria.Should().Contain("process_name: chrome.exe");
        // The TUN path always sniffs first; it must be reported as an applied modifier.
        result.Modifiers.Should().Contain(m => m.OutcomeRaw == "sniff");
    }

    [Fact]
    public async Task GpnMode_DirectRoutedEdge_MatchesDirectRule()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn"), App("msedge.exe", action: "direct") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var cfg = Generate(managed, config);

        var probe = new RouteProbe
        {
            ProcessName = "msedge.exe",
            Domain = "discord.gg",
            Port = 443,
            Network = "tcp",
        };
        var result = SingboxRouteSimulator.FindFirstMatch(
            cfg.route.rules, probe, finalFallback: cfg.route.final, configMode: "gpn");

        result.Matched.Should().BeTrue();
        result.Match!.OutcomeRaw.Should().Be(Global.DirectTag);
    }

    [Fact]
    public async Task GpnMode_UnlistedApp_FallsToDirectCatchAll()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var cfg = Generate(managed, config);

        var probe = new RouteProbe
        {
            ProcessName = "firefox.exe",
            Domain = "example.com",
            Port = 443,
            Network = "tcp",
        };
        var result = SingboxRouteSimulator.FindFirstMatch(
            cfg.route.rules, probe, finalFallback: cfg.route.final, configMode: "gpn");

        result.Matched.Should().BeTrue("GPN whitelist mode ends with a direct catch-all rule");
        result.Match!.OutcomeRaw.Should().Be(Global.DirectTag);
        result.Match.Description.Should().Contain("port_range");
    }

    [Fact]
    public async Task GpnMode_BlockApp_MatchesRejectAction()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("adware.exe", action: "block") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var cfg = Generate(managed, config);

        var probe = new RouteProbe
        {
            ProcessName = "adware.exe",
            Domain = "ads.example.com",
            Port = 80,
            Network = "tcp",
        };
        var result = SingboxRouteSimulator.FindFirstMatch(
            cfg.route.rules, probe, finalFallback: cfg.route.final, configMode: "gpn");

        result.Matched.Should().BeTrue();
        result.Match!.OutcomeKind.Should().Be("action");
        result.Match.OutcomeRaw.Should().Be("reject");
    }

    [Fact]
    public async Task GpnMode_IpDestination_MatchesIpEntryRule()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[]
        {
            App("chrome.exe", action: "vpn"),
            App("10.0.0.7", action: "direct", entryType: "ip"),
        };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var cfg = Generate(managed, config);

        var probe = new RouteProbe
        {
            ProcessName = "firefox.exe",
            IpAddress = IPAddress.Parse("10.0.0.7"),
            Port = 443,
            Network = "tcp",
        };
        var result = SingboxRouteSimulator.FindFirstMatch(
            cfg.route.rules, probe, finalFallback: cfg.route.final, configMode: "gpn");

        result.Matched.Should().BeTrue();
        result.Match!.OutcomeRaw.Should().Be(Global.DirectTag);
        result.Match.Criteria.Should().Contain("ip_cidr: 10.0.0.7");
    }

    [Fact]
    public async Task GlobalVpnMode_AllTraffic_MatchesProxyCatchAll()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: false);
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Vpn, apps);
        var cfg = Generate(managed, config);

        var probe = new RouteProbe
        {
            ProcessName = "chrome.exe",
            Domain = "discord.gg",
            Port = 443,
            Network = "tcp",
        };
        var result = SingboxRouteSimulator.FindFirstMatch(
            cfg.route.rules, probe, finalFallback: cfg.route.final, configMode: "global");

        result.Matched.Should().BeTrue("Global VPN mode ends with a proxy catch-all rule");
        result.Match!.OutcomeRaw.Should().Be(Global.ProxyTag);
        result.ConfigMode.Should().Be("global");
    }
}
