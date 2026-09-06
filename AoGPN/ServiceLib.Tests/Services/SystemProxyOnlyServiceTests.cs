using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class SystemProxyOnlyServiceTests
{
    // ---------------------------------------------------------------------
    // ShouldRun decisions
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(ESysProxyType.ForcedChange, true)]
    [InlineData(ESysProxyType.Pac, true)]
    [InlineData(ESysProxyType.ForcedClear, false)]
    [InlineData(ESysProxyType.Unchanged, false)]
    public void ShouldRun_WithConnectionOff_MirrorsProxyPreference(ESysProxyType proxyType, bool expected)
    {
        var config = CreateConfig(proxyType, GameTriggerModes.Off);

        new Harness().Service.ShouldRun(config).Should().Be(expected);
    }

    [Theory]
    [InlineData(GameTriggerModes.Vpn)]
    [InlineData(GameTriggerModes.Manual)]
    public void ShouldRun_WithActiveConnection_IsAlwaysFalse(int mode)
    {
        var config = CreateConfig(ESysProxyType.ForcedChange, mode);

        new Harness().Service.ShouldRun(config).Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_WithoutProxyItem_IsFalse()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.SystemProxyItem = null;
        config.ConnectionItem = new ConnectionSettingsItem { Mode = GameTriggerModes.Off };

        new Harness().Service.ShouldRun(config).Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_WithoutConnectionItem_StillHonoursProxyPreference()
    {
        var config = CreateConfig(ESysProxyType.ForcedChange, GameTriggerModes.Off);
        config.ConnectionItem = null;

        new Harness().Service.ShouldRun(config).Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // Routing cleanup (ToProxyOnlyContext)
    // ---------------------------------------------------------------------

    [Fact]
    public void ToProxyOnlyContext_DropsManagedRulesAndForcesProxyOnlyRouting()
    {
        var context = new CoreConfigContext
        {
            Node = CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo),
            RunCoreType = ECoreType.mihomo,
            IsTunEnabled = true,
            ProtectCoreTypeList = [ECoreType.Xray, ECoreType.mihomo],
            RoutingItem = new RoutingItem
            {
                Id = "r1",
                Remarks = "default",
                RuleSet = JsonUtils.Serialize(new List<RulesItem>
                {
                    new() { Id = "user", Remarks = "user domain", Domain = ["example.com"], OutboundTag = Global.ProxyTag, Enabled = true },
                    new() { Id = "managed-app", Remarks = "AoGPN Manuel game.exe", Process = ["game.exe"], OutboundTag = Global.ProxyTag },
                    new() { Id = "catch-all", Port = "0-65535", OutboundTag = Global.ProxyTag, Remarks = "AoGPN Manuel varsayılan" },
                    new() { Id = "udp-block", Port = "443", Network = "udp", OutboundTag = Global.BlockTag },
                }),
            },
        };

        var result = SystemProxyOnlyService.ToProxyOnlyContext(context);

        result.IsTunEnabled.Should().BeFalse();
        result.ProtectCoreTypeList.Should().BeEmpty();

        var rules = JsonUtils.Deserialize<List<RulesItem>>(result.RoutingItem.RuleSet);
        rules.Should().ContainSingle();
        rules[0].Id.Should().Be("user");
        rules[0].Domain.Should().ContainSingle("example.com");
        result.RoutingItem.RuleNum.Should().Be(1);
    }

    [Fact]
    public void ToProxyOnlyContext_KeepsRoutingMetadataAndDisablesTun()
    {
        var context = new CoreConfigContext
        {
            Node = CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo),
            RunCoreType = ECoreType.mihomo,
            IsTunEnabled = true,
            ProtectCoreTypeList = [ECoreType.Xray],
            RoutingItem = new RoutingItem
            {
                Id = "r9",
                Remarks = "custom",
                RuleSet = "[]",
                DomainStrategy = "IPIfNonMatch",
                DomainStrategy4Singbox = "ipv4_only",
                Enabled = false,
                Locked = true,
            },
        };

        var result = SystemProxyOnlyService.ToProxyOnlyContext(context);

        result.IsTunEnabled.Should().BeFalse();
        result.ProtectCoreTypeList.Should().BeEmpty();
        result.RoutingItem.Id.Should().Be("r9");
        result.RoutingItem.Remarks.Should().Be("custom");
        result.RoutingItem.DomainStrategy.Should().Be("IPIfNonMatch");
        result.RoutingItem.DomainStrategy4Singbox.Should().Be("ipv4_only");
        result.RoutingItem.Enabled.Should().BeFalse();
        result.RoutingItem.Locked.Should().BeTrue();
    }

    [Fact]
    public void ToProxyOnlyContext_WithoutRoutingItem_OnlyDisablesTunAndClearsProtectList()
    {
        var context = new CoreConfigContext
        {
            Node = CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo),
            RunCoreType = ECoreType.mihomo,
            IsTunEnabled = true,
            ProtectCoreTypeList = [ECoreType.Xray],
            RoutingItem = null,
        };

        var result = SystemProxyOnlyService.ToProxyOnlyContext(context);

        result.RoutingItem.Should().BeNull();
        result.IsTunEnabled.Should().BeFalse();
        result.ProtectCoreTypeList.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // Reconcile lifecycle
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ReconcileAsync_WithSetPreference_StartsCoreAndAppliesProxy()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));

        var applied = await harness.Service.ReconcileAsync(config);

        applied.Should().BeTrue();
        harness.Service.IsRunning.Should().BeTrue();
        harness.Runtime.Starts.Should().ContainSingle();
        harness.ProxyApplies.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileAsync_IsIdempotent_WhileRunning()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));

        await harness.Service.ReconcileAsync(config);
        var applied = await harness.Service.ReconcileAsync(config);

        applied.Should().BeTrue();
        harness.Runtime.Starts.Should().ContainSingle();
        harness.ProxyApplies.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileAsync_WithClearPreference_DoesNotStartCoreButAppliesProxyOnce()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedClear);

        var applied = await harness.Service.ReconcileAsync(config);

        applied.Should().BeTrue();
        harness.Runtime.Starts.Should().BeEmpty();
        harness.ProxyApplies.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileAsync_MissingNode_NotifiesAndDoesNotApplyProxy()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(null);

        var applied = await harness.Service.ReconcileAsync(config);

        applied.Should().BeFalse();
        harness.Runtime.Starts.Should().BeEmpty();
        harness.ProxyApplies.Should().Be(0);
        harness.Notifications.Should().ContainSingle(msg => msg == "proxyOnly.noNode");
        // The caller must be able to surface why it failed instead of a generic message.
        harness.Service.LastFailureReason.Should().NotBeNullOrEmpty();
        harness.Service.LastFailureReason.Should().Be("proxyOnly.noNode");
    }

    [Fact]
    public async Task ReconcileAsync_ApplyProxyFailure_ReturnsFalseButKeepsCoreRunning()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));
        harness.ApplyProxy = _ => { harness.ProxyApplies++; return Task.FromResult(false); };

        var applied = await harness.Service.ReconcileAsync(config);

        // The core started, but the OS proxy was NOT applied — Reconcile must not
        // claim success, otherwise the user is told "System proxy updated".
        applied.Should().BeFalse();
        harness.Service.IsRunning.Should().BeTrue();
        harness.Runtime.Starts.Should().ContainSingle();
        harness.ProxyApplies.Should().Be(1);
        // A transient apply failure is not a core failure — no misleading reason.
        harness.Service.LastFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_WhenAlreadyRunning_StillReportsApplied()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));

        await harness.Service.ReconcileAsync(config);
        var second = await harness.Service.ReconcileAsync(config);

        second.Should().BeTrue();
        harness.ProxyApplies.Should().Be(1); // no re-apply while running
    }

    [Fact]
    public async Task SuccessfulStart_ClearsLastFailureReason()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(null);
        await harness.Service.ReconcileAsync(config);
        harness.Service.LastFailureReason.Should().NotBeNullOrEmpty();

        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));
        var applied = await harness.Service.ReconcileAsync(config);

        applied.Should().BeTrue();
        harness.Service.LastFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_FailedValidation_NotifiesAndDoesNotStart()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));
        harness.ContextBuilder = (_, node) => Task.FromResult(new CoreConfigContextBuilderResult(
            new CoreConfigContext { Node = node, RunCoreType = ECoreType.mihomo },
            new NodeValidatorResult(["invalid address"], [])));

        var applied = await harness.Service.ReconcileAsync(config);

        applied.Should().BeFalse();
        harness.Runtime.Starts.Should().BeEmpty();
        harness.Notifications.Should().ContainSingle(msg => msg.Contains("invalid address"));
    }

    [Fact]
    public async Task ReconcileAsync_OpenVpnNode_IsRejected()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));
        harness.ContextBuilder = (_, node) => Task.FromResult(new CoreConfigContextBuilderResult(
            new CoreConfigContext { Node = node, RunCoreType = ECoreType.openvpn },
            NodeValidatorResult.Empty()));

        var applied = await harness.Service.ReconcileAsync(config);

        applied.Should().BeFalse();
        harness.Runtime.Starts.Should().BeEmpty();
        harness.Notifications.Should().ContainSingle(msg => msg == "proxyOnly.unsupportedNode");
    }

    [Fact]
    public async Task ReleaseOwnership_PreventsReconcileFromStoppingTheCore()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));

        await harness.Service.ReconcileAsync(config);
        harness.Service.ReleaseOwnership();

        var active = CreateConfig(ESysProxyType.ForcedChange, GameTriggerModes.Vpn);
        await harness.Service.ReconcileAsync(active);

        harness.Runtime.StopCount.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // Node changes
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RestartOnNodeChangeAsync_RestartsWithTheNewNode()
    {
        var harness = new Harness();
        var nodeA = CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo, indexId: "a");
        var nodeB = CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo, indexId: "b");
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(nodeA);
        harness.ContextBuilder = (_, node) => Task.FromResult(new CoreConfigContextBuilderResult(
            new CoreConfigContext { Node = node, RunCoreType = ECoreType.mihomo },
            NodeValidatorResult.Empty()));

        await harness.Service.ReconcileAsync(config);

        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(nodeB);
        await harness.Service.RestartOnNodeChangeAsync(config);

        harness.Runtime.Starts.Should().HaveCount(2);
        harness.Runtime.Starts[0].Main!.Node.IndexId.Should().Be("a");
        harness.Runtime.Starts[1].Main!.Node.IndexId.Should().Be("b");
        harness.Runtime.StopCount.Should().Be(1);
        // Stopping the old host force-clears the proxy once, then the restart re-applies it.
        harness.ProxyResets.Should().Be(1);
        harness.ProxyApplies.Should().Be(2);
    }

    [Fact]
    public async Task RestartOnNodeChangeAsync_WhenNotRunning_IsNoOp()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));

        await harness.Service.RestartOnNodeChangeAsync(config);

        harness.Runtime.Starts.Should().BeEmpty();
        harness.Runtime.StopCount.Should().Be(0);
        harness.ProxyApplies.Should().Be(0);
    }

    [Fact]
    public async Task RestartOnNodeChangeAsync_WhenConnectionBecameActive_IsNoOp()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));
        await harness.Service.ReconcileAsync(config);

        var active = CreateConfig(ESysProxyType.ForcedChange, GameTriggerModes.Vpn);
        await harness.Service.RestartOnNodeChangeAsync(active);

        harness.Runtime.Starts.Should().ContainSingle();
        harness.Runtime.StopCount.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_StopsTheRunningCore()
    {
        var harness = new Harness();
        var config = CreateConfig(ESysProxyType.ForcedChange);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(CoreConfigTestFactory.CreateVmessNode(ECoreType.mihomo));
        await harness.Service.ReconcileAsync(config);

        await harness.Service.StopAsync();

        harness.Service.IsRunning.Should().BeFalse();
        harness.Runtime.StopCount.Should().Be(1);
        harness.ProxyResets.Should().Be(1);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static Config CreateConfig(ESysProxyType proxyType, int mode = GameTriggerModes.Off)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.SystemProxyItem.SysProxyType = proxyType;
        config.ConnectionItem = new ConnectionSettingsItem { Mode = mode, Transport = "", ManualRoutes = [] };
        return config;
    }

    private sealed class Harness
    {
        private readonly string _root = CreateTempDirectory();

        public RecordingRuntime Runtime { get; } = new();
        public List<string> Notifications { get; } = [];
        public int ProxyApplies { get; set; }
        public int ProxyResets { get; private set; }
        public Func<Config, Task<ProfileItem?>> NodeResolver { get; set; } =
            _ => Task.FromResult<ProfileItem?>(null);
        public Func<Config, ProfileItem, Task<CoreConfigContextBuilderResult>> ContextBuilder { get; set; } =
            (_, node) => Task.FromResult(new CoreConfigContextBuilderResult(
                new CoreConfigContext { Node = node, RunCoreType = ECoreType.mihomo },
                NodeValidatorResult.Empty()));
        public Func<Config, Task<bool>> ApplyProxy { get; set; } = null!;

        public SystemProxyOnlyService Service { get; }

        public Harness()
        {
            Directory.CreateDirectory(Path.Combine(_root, "mihomo"));
            File.WriteAllText(Path.Combine(_root, "mihomo", "mihomo-windows-amd64-v1"), string.Empty);

            ApplyProxy = _ =>
            {
                ProxyApplies++;
                return Task.FromResult(true);
            };

            Service = new SystemProxyOnlyService(
                msg =>
                {
                    Notifications.Add(msg);
                    return Task.CompletedTask;
                },
                config => NodeResolver(config),
                (config, node) => ContextBuilder(config, node),
                (config, update) => new CoreEngineHost(
                    config,
                    update,
                    runtime: Runtime,
                    binaryRegistry: new CoreBinaryRegistry(_root, () => false),
                    resetProxy: () =>
                    {
                        ProxyResets++;
                        return Task.CompletedTask;
                    }),
                config => ApplyProxy(config));
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "aogpn-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private sealed class RecordingRuntime : ICoreRuntime
    {
        public List<(CoreConfigContext? Main, CoreConfigContext? Pre)> Starts { get; } = [];
        public int StopCount { get; private set; }

        private readonly Dictionary<CoreHealthRole, CoreHealthSnapshot> _health = new()
        {
            [CoreHealthRole.Main] = CoreHealthSnapshot.Stopped(CoreHealthRole.Main),
            [CoreHealthRole.PreSocks] = CoreHealthSnapshot.Stopped(CoreHealthRole.PreSocks),
        };

        public IReadOnlyDictionary<CoreHealthRole, CoreHealthSnapshot> Health => _health;

        public CoreHealthSnapshot GetHealth(CoreHealthRole role) => _health[role];

        public Task InitializeAsync(Config config, Func<bool, string, Task> update) => Task.CompletedTask;

        public async Task StartAsync(CoreConfigContext? mainContext, CoreConfigContext? preContext)
        {
            Starts.Add((mainContext, preContext));
            await Task.Yield();
            _health[CoreHealthRole.Main] = new CoreHealthSnapshot(
                CoreHealthRole.Main,
                CoreHealthState.Ready,
                ECoreType.mihomo,
                port: 10808);
        }

        public Task StopAsync()
        {
            StopCount++;
            _health[CoreHealthRole.Main] = CoreHealthSnapshot.Stopped(CoreHealthRole.Main);
            return Task.CompletedTask;
        }
    }
}
