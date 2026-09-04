using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Models;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Context;

[Collection("SharedDatabase")]
public class CoreConfigContextBuilderTests
{
    [Fact]
    public async Task ResolveNodeAsync_DirectCycleDependency_ShouldFailWithCycleError()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var groupAId = NewId("group-a");
        var groupBId = NewId("group-b");
        var groupA = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupAId, "group-a", [groupBId]);
        var groupB = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupBId, "group-b", [groupAId]);

        await UpsertProfilesAsync(groupA, groupB);

        var context = CoreConfigTestFactory.CreateContext(config, groupA, ECoreType.Xray);
        context.AllProxiesMap.Clear();

        var (_, validatorResult) = await CoreConfigContextBuilder.ResolveNodeAsync(context, groupA, false);

        validatorResult.Success.Should().BeFalse();
        validatorResult.Errors.Should().Contain(msg => ContainsCycleDependencyMessage(msg));
        context.AllProxiesMap.Should().NotContainKey(groupA.IndexId);
        context.AllProxiesMap.Should().NotContainKey(groupB.IndexId);
    }

    [Fact]
    public async Task ResolveNodeAsync_IndirectCycleDependency_ShouldFailWithCycleError()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var groupAId = NewId("group-a");
        var groupBId = NewId("group-b");
        var groupCId = NewId("group-c");
        var groupA = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupAId, "group-a", [groupBId]);
        var groupB = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupBId, "group-b", [groupCId]);
        var groupC = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupCId, "group-c", [groupAId]);

        await UpsertProfilesAsync(groupA, groupB, groupC);

        var context = CoreConfigTestFactory.CreateContext(config, groupA, ECoreType.Xray);
        context.AllProxiesMap.Clear();

        var (_, validatorResult) = await CoreConfigContextBuilder.ResolveNodeAsync(context, groupA, false);

        validatorResult.Success.Should().BeFalse();
        validatorResult.Errors.Should().Contain(msg => ContainsCycleDependencyMessage(msg));
        context.AllProxiesMap.Should().NotContainKey(groupA.IndexId);
        context.AllProxiesMap.Should().NotContainKey(groupB.IndexId);
        context.AllProxiesMap.Should().NotContainKey(groupC.IndexId);
    }

    [Fact]
    public async Task ResolveNodeAsync_CycleWithValidBranch_ShouldSkipCycleAndKeepValidChild()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var groupAId = NewId("group-a");
        var groupBId = NewId("group-b");
        var leafId = NewId("leaf");
        var groupA = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupAId, "group-a", [groupBId, leafId]);
        var groupB = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, groupBId, "group-b", [groupAId]);
        var leaf = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, leafId, "leaf");

        await UpsertProfilesAsync(groupA, groupB, leaf);

        var context = CoreConfigTestFactory.CreateContext(config, groupA, ECoreType.Xray);
        context.AllProxiesMap.Clear();

        var (_, validatorResult) = await CoreConfigContextBuilder.ResolveNodeAsync(context, groupA, false);

        validatorResult.Success.Should().BeTrue();
        validatorResult.Errors.Should().BeEmpty();
        validatorResult.Warnings.Should().Contain(msg => ContainsCycleDependencyMessage(msg));

        context.AllProxiesMap.Should().ContainKey(leaf.IndexId);
        context.AllProxiesMap.Should().ContainKey(groupA.IndexId);
        context.AllProxiesMap.Should().NotContainKey(groupB.IndexId);
        groupA.GetProtocolExtra().ChildItems.Should().Be(leaf.IndexId);
    }

    [Fact]
    public async Task Build_ProxyOnly_WithTunEnabled_KeepsNodeCoreType()
    {
        // Regression for "Suspicious behavior" from Cloudflare under the system proxy.
        // A VLESS+REALITY node (the one configured for Xray) must keep Xray when the
        // connection is Off and the proxy-only core is running — exactly what v2rayN
        // runs — instead of being forced to sing-box just because TunModeItem.EnableTun
        // happens to be set in the app config (TUN is never used in proxy-only mode).
        var config = CreateConfigWithTunEnabled();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        CreateTables();

        var node = CreateVlessRealityNode();
        await UpsertProfilesAsync(node);
        config.IndexId = node.IndexId;

        var proxyOnly = await CoreConfigContextBuilder.Build(config, node, proxyOnly: true);
        proxyOnly.Success.Should().BeTrue(string.Join("; ", proxyOnly.ValidatorResult.Errors));
        proxyOnly.Context.IsTunEnabled.Should().BeFalse();
        proxyOnly.Context.RunCoreType.Should().Be(ECoreType.Xray,
            "proxy-only must keep the node's Xray core type despite EnableTun=true");

        // Contrast: the ordinary (TUN) build path still forces sing-box.
        var tun = await CoreConfigContextBuilder.Build(config, node, proxyOnly: false);
        tun.Context.RunCoreType.Should().Be(ECoreType.sing_box,
            "TUN build must still force sing-box for a non-sing-box core type");
        tun.Context.IsTunEnabled.Should().BeTrue();
    }

    private static Config CreateConfigWithTunEnabled()
    {
        var config = new Config
        {
            CoreBasicItem = new CoreBasicItem { Loglevel = "warning" },
            TunModeItem =
                new TunModeItem
                {
                    EnableTun = true,
                    IcmpRouting = "default",
                    Stack = "system",
                    Mtu = 1408,
                },
            KcpItem = new KcpItem(),
            GrpcItem = new GrpcItem(),
            RoutingBasicItem =
                new RoutingBasicItem
                {
                    DomainStrategy = Global.AsIs,
                    DomainStrategy4Singbox = string.Empty,
                    RoutingIndexId = string.Empty,
                },
            GuiItem = new GUIItem { EnableStatistics = false, DisplayRealTimeSpeed = false, EnableLog = false },
            MsgUIItem = new MsgUIItem(),
            UiItem =
                new UIItem
                {
                    CurrentLanguage = "en",
                    CurrentFontFamily = "sans",
                    MainColumnItem = [],
                    WindowSizeItem = [],
                },
            ConstItem = new ConstItem(),
            SpeedTestItem = new SpeedTestItem
            {
                SpeedPingTestUrl = Global.SpeedPingTestUrls.First(),
                SpeedTestUrl = Global.SpeedTestUrls.First(),
                SpeedTestTimeout = 10,
                MixedConcurrencyCount = 1,
                IPAPIUrl = string.Empty,
            },
            Mux4RayItem = new Mux4RayItem { Concurrency = 8, XudpConcurrency = 16, XudpProxyUDP443 = "reject" },
            Mux4SboxItem = new Mux4SboxItem { Protocol = Global.SingboxMuxs.First(), MaxConnections = 8 },
            HysteriaItem = new HysteriaItem { UpMbps = 100, DownMbps = 100 },
            ClashUIItem = new ClashUIItem { ConnectionsColumnItem = [] },
            SystemProxyItem = new SystemProxyItem { SystemProxyExceptions = string.Empty },
            WebDavItem = new WebDavItem(),
            CheckUpdateItem = new CheckUpdateItem(),
            Fragment4RayItem = new Fragment4RayItem { Packets = "tlshello", Lengths = ["100-200"], Delays = ["10-20"] },
            Inbound =
            [
                new InItem
                {
                    Protocol = nameof(EInboundProtocol.socks),
                    LocalPort = 10808,
                    UdpEnabled = true,
                    SniffingEnabled = true,
                    RouteOnly = false,
                    DestOverride = ["http", "tls"],
                }
            ],
            GlobalHotkeys = [],
            CoreTypeItem =
            [
                new CoreTypeItem { ConfigType = EConfigType.VLESS, CoreType = ECoreType.Xray }
            ],
            SimpleDNSItem = new SimpleDNSItem
            {
                BootstrapDNS = Global.DomainPureIPDNSAddress.FirstOrDefault(),
                ServeStale = false,
                ParallelQuery = false,
                Strategy4Freedom = Global.AsIs,
                Strategy4Proxy = Global.AsIs,
                Strategy4ProxyDial = Global.AsIs,
            },
            GpnCaptureItem = new GpnCaptureItem(),
            GpnWintunItem = new GpnWintunItem(),
        };
        return config;
    }

    private static ProfileItem CreateVlessRealityNode()
    {
        var node = new ProfileItem
        {
            IndexId = NewId("vless"),
            ConfigType = EConfigType.VLESS,
            CoreType = ECoreType.Xray,
            Remarks = "vless-reality",
            Address = "92.5.108.102",
            Port = 443,
            Password = "b831381d-6324-4d53-ad4f-8cda48b30811",
            Network = nameof(ETransport.raw),
            StreamSecurity = Global.StreamSecurityReality,
            Sni = "swdist.apple.com",
            Fingerprint = "chrome",
            PublicKey = "8Whf79j5Dsv9xXeE8Z845PPp9xdjEuSjH69QmoSADFg",
            ShortId = "0123456789abcdef",
            SpiderX = "/ff854c8280b2137",
            AllowInsecure = string.Empty,
            Subid = string.Empty,
        };
        node.SetProtocolExtra(node.GetProtocolExtra() with
        {
            Flow = "xtls-rprx-vision",
            VlessEncryption = Global.None,
        });
        node.SetTransportExtra(node.GetTransportExtra() with { RawHeaderType = "none" });
        return node;
    }

    private static void CreateTables()
    {
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
    }

    private static string NewId(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }

    private static bool ContainsCycleDependencyMessage(string message)
    {
        return message.Contains("cycle dependency", StringComparison.OrdinalIgnoreCase)
               || message.Contains("循环依赖", StringComparison.Ordinal)
               || message.Contains("循環依賴", StringComparison.Ordinal)
               || message.Contains("циклическую зависимость", StringComparison.OrdinalIgnoreCase)
               || message.Contains("döngüsel bağımlılık", StringComparison.OrdinalIgnoreCase)
               || message.Contains("döngüsel bağımlılığa", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task UpsertProfilesAsync(params ProfileItem[] profiles)
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        foreach (var profile in profiles)
        {
            await SQLiteHelper.Instance.ReplaceAsync(profile);
        }
    }
}
