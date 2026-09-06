using System.Net;
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

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Route-tester coverage: the pure mihomo rule simulator
/// (<see cref="MihomoRouteSimulator"/>) and end-to-end simulation against the
/// mihomo YAML the GPN generator emits for managed routing rules.
/// </summary>
[Collection("SharedDatabase")]
public class RouteTesterTests
{
    // ---- helpers ----

    private static RouteProbe Probe(
        string processName = "chrome.exe",
        string? domain = null,
        string? ip = null,
        int? port = null)
    {
        return new RouteProbe
        {
            ProcessName = processName,
            Domain = domain ?? string.Empty,
            IpAddress = ip is null ? null : IPAddress.Parse(ip),
            Port = port,
        };
    }

    private static RouteTestMatch? Simulate(IReadOnlyList<string> rules, RouteProbe probe)
        => MihomoRouteSimulator.FindFirstMatch(rules, probe);

    private static string ProxyTarget() => GpnMihomoConfigService.WireGuardProxyName(Server());

    private static GpnServerProfile Server() => new(
        ServerId: "de",
        Name: "Almanya",
        EndpointHost: "130.61.223.36",
        EndpointPort: 51820,
        ServerPublicKey: "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
        ClientPrivateKey: "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
        ClientAddress: "10.66.66.2/24",
        Mtu: 1420,
        PersistentKeepalive: 25);

    // ---- process matching ----

    [Fact]
    public void ProcessName_MatchesFirstTerminalRule()
    {
        var rules = new[] { "PROCESS-NAME,chrome.exe,proxy", "MATCH,DIRECT" };
        var match = Simulate(rules, Probe("chrome.exe"));

        match.Should().NotBeNull();
        match!.RuleIndex.Should().Be(1);
        match.Outcome.Should().Be("proxy");
    }

    [Fact]
    public void ProcessName_IsCaseInsensitive()
    {
        var rules = new[] { "PROCESS-NAME,CHROME.EXE,proxy", "MATCH,DIRECT" };
        Simulate(rules, Probe("chrome.exe"))!.Outcome.Should().Be("proxy");
    }

    [Fact]
    public void ProcessName_UnknownProcess_FallsThroughToNextRule()
    {
        var rules = new[] { "PROCESS-NAME,chrome.exe,proxy", "MATCH,DIRECT" };
        var match = Simulate(rules, Probe("notepad.exe"));

        match.Should().NotBeNull();
        match!.RuleIndex.Should().Be(2);
        match.Outcome.Should().Be("DIRECT");
    }

    [Fact]
    public void NoMatch_ReportsFinalFallback()
    {
        // MATCH is terminal and always present in generated configs — the fallback
        // is the MATCH row itself.
        var rules = new[] { "MATCH,DIRECT" };
        var match = Simulate(rules, Probe("anything.exe", domain: "example.com"));

        match.Should().NotBeNull();
        match!.Outcome.Should().Be("DIRECT");
    }

    // ---- domain matching ----

    [Fact]
    public void DomainSuffix_MatchesSubdomainsAndExact()
    {
        var rules = new[] { "DOMAIN-SUFFIX,discord.gg,proxy", "MATCH,DIRECT" };

        Simulate(rules, Probe(domain: "discord.gg"))!.Outcome.Should().Be("proxy");
        Simulate(rules, Probe(domain: "cdn.discord.gg"))!.Outcome.Should().Be("proxy");
        Simulate(rules, Probe(domain: "evil-discord.gg.attacker.com"))!.Outcome.Should().Be("DIRECT");
    }

    [Fact]
    public void DomainKeyword_MatchesSubstring()
    {
        var rules = new[] { "DOMAIN-KEYWORD,tarkov,proxy", "MATCH,DIRECT" };

        Simulate(rules, Probe(domain: "launcher.escapefromtarkov.com"))!.Outcome.Should().Be("proxy");
        Simulate(rules, Probe(domain: "example.com"))!.Outcome.Should().Be("DIRECT");
    }

    [Fact]
    public void DomainRegex_MatchesPattern()
    {
        var rules = new[] { "DOMAIN-REGEX,^.*\\.tarkov\\.com$,proxy", "MATCH,DIRECT" };

        Simulate(rules, Probe(domain: "www.tarkov.com"))!.Outcome.Should().Be("proxy");
        Simulate(rules, Probe(domain: "tarkov.org"))!.Outcome.Should().Be("DIRECT");
    }

    [Fact]
    public void DomainProbe_NeverMatchesIpOnlyRules()
    {
        var rules = new[] { "IP-CIDR,10.0.0.0/8,DIRECT", "MATCH,proxy" };
        Simulate(rules, Probe(domain: "example.com"))!.Outcome.Should().Be("proxy");
    }

    // ---- IP matching ----

    [Fact]
    public void IpCidr_MatchesWithinRange()
    {
        var rules = new[] { "IP-CIDR,10.0.0.0/8,DIRECT", "MATCH,proxy" };

        Simulate(rules, Probe(ip: "10.1.2.3"))!.Outcome.Should().Be("DIRECT");
        Simulate(rules, Probe(ip: "11.1.2.3"))!.Outcome.Should().Be("proxy");
    }

    [Fact]
    public void IpCidr6_MatchesV6AndNotV4()
    {
        var rules = new[] { "IP-CIDR6,fd00::/8,DIRECT", "MATCH,proxy" };

        Simulate(rules, Probe(ip: "fd12::1"))!.Outcome.Should().Be("DIRECT");
        Simulate(rules, Probe(ip: "10.0.0.1"))!.Outcome.Should().Be("proxy");
    }

    [Fact]
    public void IpCidr_PlainIpMatchesItself()
    {
        var rules = new[] { "IP-CIDR,1.2.3.4/32,DIRECT", "MATCH,proxy" };

        Simulate(rules, Probe(ip: "1.2.3.4"))!.Outcome.Should().Be("DIRECT");
        Simulate(rules, Probe(ip: "1.2.3.5"))!.Outcome.Should().Be("proxy");
    }

    [Fact]
    public void IpCidr_NoResolveSuffix_IsTolerated()
    {
        var rules = new[] { "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve", "MATCH,proxy" };
        Simulate(rules, Probe(ip: "10.9.9.9"))!.Outcome.Should().Be("DIRECT");
    }

    // ---- port matching ----

    [Fact]
    public void DstPort_MatchesPortAndRange()
    {
        var rules = new[] { "DST-PORT,80,443,proxy", "DST-PORT,1000-2000,DIRECT", "MATCH,block" };

        Simulate(rules, Probe(port: 80))!.Outcome.Should().Be("proxy");
        Simulate(rules, Probe(port: 443))!.Outcome.Should().Be("proxy");
        Simulate(rules, Probe(port: 1500))!.Outcome.Should().Be("DIRECT");
        Simulate(rules, Probe(port: 8080))!.Outcome.Should().Be("block");
    }

    // ---- rule semantics ----

    [Fact]
    public void CatchAll_Match_MatchesEverything()
    {
        var rules = new[] { "MATCH,DIRECT" };
        Simulate(rules, Probe("chrome.exe", domain: "a.com", ip: "1.2.3.4", port: 443))!.Outcome.Should().Be("DIRECT");
    }

    [Fact]
    public void RuleLine_Parse_TargetFromLastToken()
    {
        var rule = MihomoRuleLine.Parse("DOMAIN-SUFFIX,cdn.example.com,wg-de");
        rule.Type.Should().Be("DOMAIN-SUFFIX");
        rule.Payload.Should().Be("cdn.example.com");
        rule.Target.Should().Be("wg-de");
    }

    [Fact]
    public void RuleLine_Parse_DstPortPayload_KeepsCommaSeparatedPorts()
    {
        var rule = MihomoRuleLine.Parse("DST-PORT,80,443,proxy");
        rule.Type.Should().Be("DST-PORT");
        rule.Payload.Should().Be("80,443");
        rule.Target.Should().Be("proxy");
    }

    // ---- integration: generated mihomo config ----

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

    private static Config CreateConfig(bool enableTun)
    {
        return new Config
        {
            CoreBasicItem = new CoreBasicItem { Loglevel = "warning" },
            TunModeItem = new TunModeItem { EnableTun = enableTun, IcmpRouting = "default" },
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

    private static SplitTunnelAppItem App(string value, string action = "proxy", string entryType = "app")
    {
        return new SplitTunnelAppItem
        {
            EntryType = entryType,
            Value = value,
            Action = action,
        };
    }

    private static async Task<List<string>> Generate(IReadOnlyList<RulesItem> managed, Config config)
    {
        var node = GpnCoreLauncher.BuildWireGuardProfile(Server());
        var context = new CoreConfigContext
        {
            Node = node,
            RunCoreType = ECoreType.mihomo,
            AppConfig = config,
            RoutingItem = new RoutingItem
            {
                Id = Utils.GetGuid(false),
                Remarks = "gpn-managed-routing",
                RuleSet = JsonUtils.Serialize(managed, false),
                DomainStrategy = Global.AsIs,
            },
            RawDnsItem = null,
            SimpleDnsItem = config.SimpleDNSItem,
            AllProxiesMap = new Dictionary<string, ProfileItem> { [node.IndexId] = node },
            FullConfigTemplate = null,
            IsTunEnabled = config.TunModeItem.EnableTun,
            ProtectDomainList = [],
        };

        var result = await CoreConfigHandler.GenerateClientConfig(context, null);
        result.Success.Should().BeTrue("mihomo config generation must succeed: " + result.Msg);
        return RouteTesterService.ExtractRules(result.Data!.ToString()!);
    }

    [Fact]
    public async Task GpnMode_ChromeToDiscord_MatchesProcessRuleProxy()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var rules = await Generate(managed, config);

        var match = Simulate(rules, Probe("chrome.exe", domain: "discord.gg"));
        match.Should().NotBeNull();
        match!.Outcome.Should().Be(ProxyTarget());
    }

    [Fact]
    public async Task GpnMode_DirectRoutedEdge_MatchesDirectRule()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("msedge.exe", action: "direct") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var rules = await Generate(managed, config);

        var match = Simulate(rules, Probe("msedge.exe", domain: "bing.com"));
        match.Should().NotBeNull();
        match!.Outcome.Should().Be("DIRECT");
    }

    [Fact]
    public async Task GpnMode_UnlistedApp_FallsToDirectCatchAll()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var rules = await Generate(managed, config);

        var match = Simulate(rules, Probe("notepad.exe", domain: "example.com"));
        match.Should().NotBeNull();
        match!.Outcome.Should().Be("DIRECT", "whitelist catch-all keeps unlisted apps direct");
    }

    [Fact]
    public async Task GpnMode_BlockApp_MatchesRejectAction()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("adware.exe", action: "block") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var rules = await Generate(managed, config);

        var match = Simulate(rules, Probe("adware.exe", domain: "ads.example.com"));
        match.Should().NotBeNull();
        match!.Outcome.Should().Be("REJECT");
    }

    [Fact]
    public async Task GpnMode_IpDestination_MatchesIpEntryRule()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("203.0.113.0/24", entryType: "ip", action: "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, apps, invertManual: false);
        var rules = await Generate(managed, config);

        var match = Simulate(rules, Probe("chrome.exe", ip: "203.0.113.55", port: 443));
        match.Should().NotBeNull();
        match!.Outcome.Should().Be(ProxyTarget());
    }

    [Fact]
    public async Task GlobalVpnMode_AllTraffic_MatchesProxyCatchAll()
    {
        await CleanRoutingItemsAsync();
        var config = CreateConfig(enableTun: true);
        BindConfig(config);

        var apps = new[] { App("chrome.exe", action: "vpn") };
        var managed = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Vpn, apps);
        var rules = await Generate(managed, config);

        rules.Where(r => r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();

        var match = Simulate(rules, Probe("notepad.exe", domain: "example.com"));
        match.Should().NotBeNull();
        match!.Outcome.Should().Be(ProxyTarget(), "Global VPN catch-all routes everything to the tunnel");
    }
}