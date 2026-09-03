using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Events;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// End-to-end regression test for the WPF-side system-proxy toggle.
///
/// The AoGPN dashboard wire-up ('set_system_proxy_mode' → SetSystemProxyModeAsync)
/// reconciles SystemProxyOnlyService so the OS proxy works while the connection is
/// Off (v2rayN behaviour). The WPF paths (status-bar combobox, sidebar combobox and
/// global hotkeys) go through StatusBarViewModel.SetListenerType, which publishes on
/// SystemProxyModeRequested; MainWindow forwards that channel to the same
/// ReconcileAsync. Before that routing existed, those paths only called
/// SysProxyHandler.UpdateSysProxy — applying the OS proxy with no core listening
/// behind it while disconnected (a dead proxy).
///
/// This test reproduces that exact wiring at the ServiceLib level: an
/// EventChannel&lt;ESysProxyType&gt; stands in for SystemProxyModeRequested, a subscriber
/// stands in for the MainWindow handler, and each "request" first persists the
/// independent preference (what SetListenerType does), then publishes to the channel
/// (also what SetListenerType does). The asserted outcome is the regression the fix
/// locks in: a Set/PAC request with the connection Off must leave the proxy-only core
/// RUNNING and the OS proxy applied.
/// </summary>
public sealed class SystemProxyWpfFlowIntegrationTests
{
    [Fact]
    public async Task SetRequest_WhileDisconnected_StartsProxyOnlyCoreAndAppliesProxy()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear);
        var harness = new Harness(config);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(
            CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box));

        // Status bar: "Sistem proxy → Ayarla" with the connection off.
        await harness.RequestAsync(config, ESysProxyType.ForcedChange);

        harness.Service.IsRunning.Should().BeTrue();
        harness.Runtime.Starts.Should().ContainSingle();
        harness.ProxyApplies.Should().Be(1);
        config.SystemProxyItem.SysProxyType.Should().Be(ESysProxyType.ForcedChange);
    }

    [Fact]
    public async Task PacRequest_WhileDisconnected_StartsProxyOnlyCore()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear);
        var harness = new Harness(config);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(
            CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box));

        await harness.RequestAsync(config, ESysProxyType.Pac);

        harness.Service.IsRunning.Should().BeTrue();
        harness.Runtime.Starts.Should().ContainSingle();
        config.SystemProxyItem.SysProxyType.Should().Be(ESysProxyType.Pac);
    }

    [Fact]
    public async Task ClearRequest_AfterSet_StopsProxyOnlyCoreAndClearsProxy()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear);
        var harness = new Harness(config);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(
            CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box));

        await harness.RequestAsync(config, ESysProxyType.ForcedChange);
        harness.Service.IsRunning.Should().BeTrue();

        await harness.RequestAsync(config, ESysProxyType.ForcedClear);

        harness.Service.IsRunning.Should().BeFalse();
        harness.Runtime.Starts.Should().ContainSingle();
        harness.Runtime.StopCount.Should().Be(1);
        // StopAsync force-clears the OS proxy (resetProxy), then the Clear reconcile
        // applies the resolved (off) state once more.
        harness.ProxyResets.Should().Be(1);
        harness.ProxyApplies.Should().Be(2);
    }

    [Fact]
    public async Task UnchangedRequest_WhileDisconnected_DoesNotRunProxyOnlyCore()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear);
        var harness = new Harness(config);

        await harness.RequestAsync(config, ESysProxyType.Unchanged);

        harness.Service.IsRunning.Should().BeFalse();
        harness.Runtime.Starts.Should().BeEmpty();
    }

    [Fact]
    public async Task SetRequest_WhileConnectionActive_DoesNotStartProxyOnlyCore()
    {
        var config = CreateConfig(ESysProxyType.ForcedChange, GameTriggerModes.Vpn);
        var harness = new Harness(config);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(
            CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box));

        await harness.RequestAsync(config, ESysProxyType.ForcedChange);

        // A real connection owns the core; the proxy-only service must not double-run.
        harness.Service.IsRunning.Should().BeFalse();
        harness.Runtime.Starts.Should().BeEmpty();
    }

    [Fact]
    public async Task SetRequest_MissingNode_NotifiesAndDoesNotApplyProxy()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear);
        var harness = new Harness(config);
        harness.NodeResolver = _ => Task.FromResult<ProfileItem?>(null);

        await harness.RequestAsync(config, ESysProxyType.ForcedChange);

        harness.Service.IsRunning.Should().BeFalse();
        harness.Runtime.Starts.Should().BeEmpty();
        harness.ProxyApplies.Should().Be(0);
        harness.Notifications.Should().ContainSingle(msg => msg == "proxyOnly.noNode");
    }

    // ------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------

    private static Config CreateConfig(ESysProxyType proxyType, int mode = GameTriggerModes.Off)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.SystemProxyItem.SysProxyType = proxyType;
        config.ConnectionItem = new ConnectionSettingsItem { Mode = mode, Transport = "", ManualRoutes = [] };
        return config;
    }

    /// <summary>
    /// Models the WPF-side system-proxy request bus: an EventChannel&lt;ESysProxyType&gt;
    /// (SystemProxyModeRequested) whose subscriber calls
    /// SystemProxyOnlyService.ReconcileAsync against the shared config — exactly the
    /// MainWindow glue. RequestAsync performs SetListenerType's two steps (persist the
    /// independent preference, then publish) and awaits the subscriber's reconcile.
    /// </summary>
    private sealed class Harness
    {
        private readonly string _root = CreateTempDirectory();
        private readonly Config _config;
        private TaskCompletionSource? _complete;

        public EventChannel<ESysProxyType> Requests { get; } = new();
        public RecordingRuntime Runtime { get; } = new();
        public List<string> Notifications { get; } = [];
        public int ProxyApplies { get; private set; }
        public int ProxyResets { get; private set; }
        public Func<Config, Task<ProfileItem?>> NodeResolver { get; set; } =
            _ => Task.FromResult<ProfileItem?>(null);

        public SystemProxyOnlyService Service { get; }

        public Harness(Config config)
        {
            _config = config;
            Directory.CreateDirectory(Path.Combine(_root, "sing_box"));
            File.WriteAllText(Path.Combine(_root, "sing_box", "sing-box-client"), string.Empty);

            Service = new SystemProxyOnlyService(
                msg =>
                {
                    Notifications.Add(msg);
                    return Task.CompletedTask;
                },
                config => NodeResolver(config),
                (config, node) => Task.FromResult(new CoreConfigContextBuilderResult(
                    new CoreConfigContext { Node = node, RunCoreType = ECoreType.sing_box },
                    NodeValidatorResult.Empty())),
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
                _ =>
                {
                    ProxyApplies++;
                    return Task.FromResult(true);
                });

            Requests.AsObservable().Subscribe(type => _ = HandleRequestAsync(type));
        }

        /// <summary>SetListenerType's contract: persist the preference, then publish.</summary>
        public async Task RequestAsync(Config config, ESysProxyType type)
        {
            // SetListenerType step 1: persist the independent preference before the
            // request is delivered. Must be the same instance the handler reconciles.
            config.SystemProxyItem.SysProxyType = type;
            if (!ReferenceEquals(config, _config))
            {
                throw new InvalidOperationException("RequestAsync config must be the harness's shared config.");
            }

            // SetListenerType step 2: publish — the subscriber reconciles the core.
            _complete = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Publish(type);
            await _complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private async Task HandleRequestAsync(ESysProxyType type)
        {
            try
            {
                // MainWindow's SystemProxyModeRequested handler.
                await Service.ReconcileAsync(_config);
            }
            finally
            {
                _complete?.TrySetResult();
            }
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
                ECoreType.sing_box,
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