using System.Reflection;
using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Models;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Services.CoreConfig;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// Pure unit tests for the rule-drift comparison (<see cref="RoutingDriftHealthCheck.BuildReport"/>):
/// the verdict logic that decides whether the active RoutingItem still matches the rules the
/// running core loaded (mihomo YAML rule rows). No database or filesystem involved.
/// </summary>
public class RoutingDriftHealthCheckTests
{
    private static RoutingItem Routing(params RulesItem[] rules)
    {
        return new RoutingItem
        {
            Id = "r-drift",
            Remarks = "active-routing",
            RuleSet = JsonUtils.Serialize(rules.ToList(), false),
            IsActive = true,
        };
    }

    private static RulesItem DbRule(string process, string outbound, bool enabled = true)
    {
        return new RulesItem
        {
            Id = Utils.GetGuid(false),
            Process = [process],
            OutboundTag = outbound,
            Enabled = enabled,
            Remarks = $"{process} → {outbound}",
        };
    }

    [Fact]
    public void IdenticalRules_InSync_NoMissingOrExtra()
    {
        var expected = new List<string>
        {
            "PROCESS-NAME,chrome.exe,wg-de",
            "MATCH,DIRECT",
        };

        var report = RoutingDriftHealthCheck.BuildReport(
            Routing(DbRule("chrome.exe", Global.ProxyTag)),
            expected,
            expected);

        report.State.Should().Be(RuleDriftState.InSync);
        report.IsDrifted.Should().BeFalse();
        report.MissingRules.Should().BeEmpty();
        report.ExtraRules.Should().BeEmpty();
        report.Reordered.Should().BeFalse();
        report.ExpectedRuleCount.Should().Be(2);
        report.LiveRuleCount.Should().Be(2);
        report.ActiveRuleCount.Should().Be(1);
        report.ActiveRoutingId.Should().Be("r-drift");
    }

    [Fact]
    public void StaleRule_NewRuleInDatabaseNotInLiveConfig_IsMissing()
    {
        var expected = new List<string>
        {
            "PROCESS-NAME,chrome.exe,wg-de",
            "PROCESS-NAME,msedge.exe,DIRECT", // added after the core loaded
            "MATCH,DIRECT",
        };
        var live = new List<string>
        {
            "PROCESS-NAME,chrome.exe,wg-de",
            "MATCH,DIRECT",
        };

        var report = RoutingDriftHealthCheck.BuildReport(
            Routing(
                DbRule("chrome.exe", Global.ProxyTag),
                DbRule("msedge.exe", Global.DirectTag)),
            expected,
            live);

        report.State.Should().Be(RuleDriftState.Drifted);
        report.IsDrifted.Should().BeTrue();
        report.MissingRules.Should().ContainSingle(r => r.Contains("msedge.exe"));
        report.ExtraRules.Should().BeEmpty();
        report.Reordered.Should().BeFalse();
    }

    [Fact]
    public void RemovedRule_StillInLiveConfig_IsExtra()
    {
        var expected = new List<string>
        {
            "PROCESS-NAME,chrome.exe,wg-de",
            "MATCH,DIRECT",
        };
        var live = new List<string>
        {
            "PROCESS-NAME,chrome.exe,wg-de",
            "PROCESS-NAME,legacy.exe,wg-de", // rule the core still enforces
            "MATCH,DIRECT",
        };

        var report = RoutingDriftHealthCheck.BuildReport(
            Routing(DbRule("chrome.exe", Global.ProxyTag)),
            expected,
            live);

        report.State.Should().Be(RuleDriftState.Drifted);
        report.ExtraRules.Should().ContainSingle(r => r.Contains("legacy.exe"));
        report.MissingRules.Should().BeEmpty();
    }

    [Fact]
    public void SameRules_DifferentOrder_IsDriftBecauseFirstMatchWins()
    {
        var expected = new List<string>
        {
            "PROCESS-NAME,chrome.exe,wg-de",
            "MATCH,DIRECT",
        };
        var live = new List<string>
        {
            "MATCH,DIRECT",
            "PROCESS-NAME,chrome.exe,wg-de",
        };

        var report = RoutingDriftHealthCheck.BuildReport(
            Routing(DbRule("chrome.exe", Global.ProxyTag)),
            expected,
            live);

        report.State.Should().Be(RuleDriftState.Drifted,
            "rule order changes what matches first — first-match-wins routing");
        report.Reordered.Should().BeTrue();
        report.MissingRules.Should().BeEmpty();
        report.ExtraRules.Should().BeEmpty();
    }

    [Fact]
    public void DisabledRules_AreNotCountedAsActive()
    {
        var routing = Routing(
            DbRule("chrome.exe", Global.ProxyTag, enabled: true),
            DbRule("msedge.exe", Global.DirectTag, enabled: false));

        var report = RoutingDriftHealthCheck.BuildReport(
            routing,
            ["PROCESS-NAME,chrome.exe,wg-de"],
            ["PROCESS-NAME,chrome.exe,wg-de"]);

        report.State.Should().Be(RuleDriftState.InSync);
        report.ActiveRuleCount.Should().Be(1);
    }

    [Fact]
    public void ExternalWarnings_ArePreserved()
    {
        var expected = new List<string> { "PROCESS-NAME,chrome.exe,wg-de" };
        var report = RoutingDriftHealthCheck.BuildReport(
            Routing(DbRule("chrome.exe", Global.ProxyTag)),
            expected,
            expected,
            externalWarnings: ["rule-set .srs file missing locally"]);

        report.State.Should().Be(RuleDriftState.InSync);
        report.Warnings.Should().ContainSingle("rule-set .srs file missing locally");
    }
}

/// <summary>
/// End-to-end tests for <see cref="RoutingDriftHealthCheck.CheckAsync"/>: seeds the active
/// RoutingItem + node in the database, writes a mihomo YAML file to the location the core
/// would load, and verifies the check compares them correctly.
/// </summary>
[Collection("SharedDatabase")]
public class RoutingDriftHealthCheckIntegrationTests
{
    private const string ServerId = "de";

    private static async Task CleanAsync()
    {
        // Tabloları AppManager.InitApp yaratır ama test host'u onu hiç çalıştırmaz;
        // temiz bir bin'de guiNDB.db tablosuzdur — temizlikten önce eksikleri yarat.
        CreateTables();
        foreach (var r in await SQLiteHelper.Instance.TableAsync<RoutingItem>().ToListAsync())
        {
            await SQLiteHelper.Instance.DeleteAsync(r);
        }
        foreach (var p in await SQLiteHelper.Instance.TableAsync<ProfileItem>().ToListAsync())
        {
            await SQLiteHelper.Instance.DeleteAsync(p);
        }
    }

    /// <summary>
    /// Mirrors the tables AppManager creates at startup so the context builder
    /// (which reads the full-config template and DNS tables through AppManager)
    /// can run against an empty database.
    /// </summary>
    private static void CreateTables()
    {
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
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

    private static ProfileItem CreateNode()
        => GpnCoreLauncher.BuildWireGuardProfile(Server());

    private static RoutingItem CreateRouting(params RulesItem[] rules)
    {
        return new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "active-routing",
            RuleSet = JsonUtils.Serialize(rules.ToList(), false),
            RuleNum = rules.Length,
            IsActive = true,
            Sort = 0,
        };
    }

    private static RulesItem UserRule(string process, string outbound)
    {
        return new RulesItem
        {
            Id = Utils.GetGuid(false),
            Process = [process],
            OutboundTag = outbound,
            Enabled = true,
            Remarks = $"{process} → {outbound}",
        };
    }

    private static string ConfigFilePath() => Utils.GetBinConfigPath(Global.CoreConfigFileName);

    private static async Task<string> GenerateNowAsync(Config config, ProfileItem node)
    {
        var builderResult = await CoreConfigContextBuilder.Build(config, node);
        builderResult.Success.Should().BeTrue(string.Join("; ", builderResult.ValidatorResult.Errors));
        var result = await CoreConfigHandler.GenerateClientConfig(builderResult.Context, null);
        result.Success.Should().BeTrue("mihomo config generation must succeed: " + result.Msg);
        // Çekirdeğin diske yazdığı GERÇEK YAML döner — "live" dosya bu metinle
        // birebir aynı olmalı ki CheckAsync kuralları çözebilsin.
        return result.Data!.ToString()!;
    }

    [Fact]
    public async Task Check_MatchingLiveConfig_IsInSync()
    {
        await CleanAsync();
        var config = CreateConfig();
        BindConfig(config);
        CreateTables();

        var node = CreateNode();
        await SQLiteHelper.Instance.ReplaceAsync(node);
        config.IndexId = node.IndexId;
        var routing = CreateRouting(UserRule("chrome.exe", Global.ProxyTag));
        await SQLiteHelper.Instance.ReplaceAsync(routing);

        var yaml = await GenerateNowAsync(config, node);
        var configPath = ConfigFilePath();
        await File.WriteAllTextAsync(configPath, yaml, TestContext.Current.CancellationToken);

        try
        {
            var report = await new RoutingDriftHealthCheck(config).CheckAsync();

            report.State.Should().Be(RuleDriftState.InSync);
            report.IsDrifted.Should().BeFalse();
            report.ActiveRoutingId.Should().Be(routing.Id);
            report.ActiveRuleCount.Should().Be(1);
            report.ExpectedRuleCount.Should().Be(report.LiveRuleCount);
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task Check_StaleLiveConfig_IsDrifted()
    {
        await CleanAsync();
        var config = CreateConfig();
        BindConfig(config);
        CreateTables();

        var node = CreateNode();
        await SQLiteHelper.Instance.ReplaceAsync(node);
        config.IndexId = node.IndexId;

        // The database says chrome.exe is routed via the proxy…
        var routing = CreateRouting(UserRule("chrome.exe", Global.ProxyTag));
        await SQLiteHelper.Instance.ReplaceAsync(routing);

        // …but the config the core loaded was generated when msedge.exe was the
        // only rule (e.g. the user edited rules in the settings without reloading).
        var builderResult = await CoreConfigContextBuilder.Build(config, node);
        builderResult.Success.Should().BeTrue();
        var staleContext = builderResult.Context with
        {
            RoutingItem = CreateRouting(UserRule("msedge.exe", Global.DirectTag)),
        };
        var staleResult = await CoreConfigHandler.GenerateClientConfig(staleContext, null);
        staleResult.Success.Should().BeTrue();

        var configPath = ConfigFilePath();
        await File.WriteAllTextAsync(configPath, staleResult.Data!.ToString()!, TestContext.Current.CancellationToken);

        try
        {
            var report = await new RoutingDriftHealthCheck(config).CheckAsync();

            report.State.Should().Be(RuleDriftState.Drifted);
            report.IsDrifted.Should().BeTrue();
            report.MissingRules.Should().Contain(r => r.Contains("chrome.exe"));
            report.ExtraRules.Should().Contain(r => r.Contains("msedge.exe"));
            report.ActiveRoutingId.Should().Be(routing.Id);
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task Check_NoLiveConfigFile_IsUnknownWithWarning()
    {
        await CleanAsync();
        var config = CreateConfig();
        BindConfig(config);
        CreateTables();

        var node = CreateNode();
        await SQLiteHelper.Instance.ReplaceAsync(node);
        config.IndexId = node.IndexId;
        await SQLiteHelper.Instance.ReplaceAsync(CreateRouting(UserRule("chrome.exe", Global.ProxyTag)));

        var configPath = ConfigFilePath();
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }

        var report = await new RoutingDriftHealthCheck(config).CheckAsync();

        report.State.Should().Be(RuleDriftState.Unknown);
        report.Warnings.Should().Contain(w => w.Contains("No config file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Check_NonMihomoCore_IsNotApplicable()
    {
        await CleanAsync();
        var config = CreateConfig();
        // With TUN enabled the context builder forces an Xray node onto the
        // mihomo core; disable TUN so the Xray core is selected as-is.
        config.TunModeItem.EnableTun = false;
        BindConfig(config);
        CreateTables();

        var node = new ProfileItem
        {
            IndexId = "node-xray",
            ConfigType = EConfigType.VLESS,
            CoreType = ECoreType.Xray,
            Remarks = "xray-node",
            Address = "example.com",
            Port = 443,
            Password = Guid.NewGuid().ToString(), // UUID — NodeValidator VLESS zorunluluğu
            Network = nameof(ETransport.raw),
            StreamSecurity = Global.StreamSecurityReality,
            Sni = "example.com",
            PublicKey = "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
            ShortId = "abc123",
            Subid = string.Empty,
        };
        await SQLiteHelper.Instance.ReplaceAsync(node);
        config.IndexId = node.IndexId;
        await SQLiteHelper.Instance.ReplaceAsync(CreateRouting(UserRule("chrome.exe", Global.ProxyTag)));

        var report = await new RoutingDriftHealthCheck(config).CheckAsync();

        report.State.Should().Be(RuleDriftState.NotApplicable);
        report.IsDrifted.Should().BeFalse();
        report.Error.Should().NotBeNull();
        report.Error!.ToLowerInvariant().Should().Contain("mihomo");
    }
}