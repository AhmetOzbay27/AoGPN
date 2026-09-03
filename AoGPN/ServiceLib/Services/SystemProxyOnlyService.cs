using ServiceLib.Common;
using ServiceLib.Handler.Builder;
using ServiceLib.Handler.SysProxy;

namespace ServiceLib.Services;

/// <summary>
/// Runs a minimal "proxy-only" core (local SOCKS/HTTP inbounds with the selected node
/// as the outbound) so the system proxy works even when no GPN/Global tunnel is active —
/// the v2rayN behaviour. ConnectionItem.Mode stays Off while it runs, so the dashboard
/// keeps reporting "disconnected"; only the OS proxy badge reflects it.
///
/// Lifecycle ownership:
///  • ReconcileAsync starts the core when the proxy preference is Set/PAC and the
///    connection is Off, and stops it (clearing the OS proxy) otherwise.
///  • A real connection takes over the core through the normal reload path
///    (LoadCore stops the old process first); ReleaseOwnership is called before that
///    so this service never stops the connection's core.
///  • RestartOnNodeChangeAsync re-runs the core on the newly selected node while the
///    proxy-only state is active.
/// </summary>
public sealed class SystemProxyOnlyService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, Task> _notify;
    private readonly Func<Config, Task<ProfileItem?>> _nodeResolver;
    private readonly Func<Config, ProfileItem, Task<CoreConfigContextBuilderResult>> _contextBuilder;
    private readonly Func<Config, Func<bool, string, Task>, CoreEngineHost> _hostFactory;
    private readonly Func<Config, Task<bool>> _applyProxy;
    private CoreEngineHost? _host;
    private bool _running;

    public bool IsRunning => Volatile.Read(ref _running);

    /// <summary>
    /// The user-facing reason the last <see cref="ReconcileAsync"/> could not
    /// start the proxy-only core (no node selected, invalid config, unsupported
    /// node type, exception). Null when the core started or was already running,
    /// or when the proxy could not be applied for a transient reason (e.g. the
    /// local listener is not registered yet — that case applies automatically on
    /// the next core/proxy event).
    /// </summary>
    public string? LastFailureReason { get; private set; }

    public SystemProxyOnlyService(Func<string, Task> notify)
        : this(
            notify,
            config => AppManager.Instance.GetProfileItem(config.IndexId),
            // proxyOnly:true keeps the node's natural core type (Xray for VLESS/Reality,
            // the same core v2rayN runs) instead of forcing sing-box for the TUN path.
            static (config, node) => CoreConfigContextBuilder.Build(config, node, proxyOnly: true),
            static (config, update) => GetOrCreateHost(config, update),
            static config => SysProxyHandler.UpdateSysProxy(config, false))
    {
    }

    /// <summary>
    /// Test seam: injects the node lookup, context builder, core host factory and OS
    /// proxy applier so the orchestration can be exercised without SQLite, a live core
    /// process or OS registry writes. The public constructor wires the production
    /// implementations.
    /// </summary>
    internal SystemProxyOnlyService(
        Func<string, Task> notify,
        Func<Config, Task<ProfileItem?>> nodeResolver,
        Func<Config, ProfileItem, Task<CoreConfigContextBuilderResult>> contextBuilder,
        Func<Config, Func<bool, string, Task>, CoreEngineHost> hostFactory,
        Func<Config, Task<bool>> applyProxy)
    {
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _nodeResolver = nodeResolver ?? throw new ArgumentNullException(nameof(nodeResolver));
        _contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        _applyProxy = applyProxy ?? throw new ArgumentNullException(nameof(applyProxy));
    }

    /// <summary>
    /// True when the current config calls for a proxy-only core: the saved proxy
    /// preference is Set/PAC AND no connection is active.
    /// </summary>
    public bool ShouldRun(Config config)
    {
        var connectionActive = (config.ConnectionItem?.Mode ?? GameTriggerModes.Off) != GameTriggerModes.Off;
        if (connectionActive)
        {
            return false;
        }
        return SystemProxyPolicy.IsProxyEnabled(
            config.SystemProxyItem?.SysProxyType ?? ESysProxyType.ForcedClear);
    }

    /// <summary>
    /// Reconciles the proxy-only state with the current config. Returns true when the
    /// OS proxy is (or stays) applied, false when it could not be started.
    /// </summary>
    public async Task<bool> ReconcileAsync(Config config)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ShouldRun(config))
            {
                // Clear/Unchanged preference or a connection took over: release the
                // core and apply the effective (off) proxy state. The result is
                // honest: "applied" means the OS proxy really was set/cleared.
                await StopCoreAsync();
                return await _applyProxy(config);
            }

            if (_running)
            {
                return true;
            }

            var started = await StartCoreAsync(config);
            if (started)
            {
                var applied = await _applyProxy(config);
                if (!applied)
                {
                    // Core is up but the OS proxy could not be applied yet (usually
                    // the local listener is not registered at this instant). This is
                    // transient — the next core/proxy event applies it — so it is
                    // reported as "not applied yet", not as a hard failure.
                    return false;
                }
            }
            return started;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the proxy-only core and clears the OS proxy (shutdown, takeover).</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the core as owned by a real connection. Called before the connection
    /// reload so this service never stops the connection's core afterwards.
    /// </summary>
    public void ReleaseOwnership()
    {
        Volatile.Write(ref _running, false);
        _host = null;
    }

    /// <summary>Restarts the proxy-only core so it routes through the newly selected node.</summary>
    public async Task RestartOnNodeChangeAsync(Config config)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_running || !ShouldRun(config))
            {
                return;
            }
            await StopCoreAsync();
            if (await StartCoreAsync(config))
            {
                await _applyProxy(config);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (!_running)
        {
            return;
        }
        _running = false;
        var host = _host;
        _host = null;
        if (host is not null)
        {
            // StopAsync also force-clears the OS proxy (CoreEngineHost._resetProxy).
            await host.StopAsync();
        }
    }

    private async Task<bool> StartCoreAsync(Config config)
    {
        try
        {
            var node = await _nodeResolver(config);
            if (node is null)
            {
                LastFailureReason = "proxyOnly.noNode";
                await _notify(LastFailureReason);
                return false;
            }

            var build = await _contextBuilder(config, node);
            if (!build.Success)
            {
                LastFailureReason = build.ValidatorResult.Errors.FirstOrDefault()
                             ?? build.ValidatorResult.Warnings.FirstOrDefault()
                             ?? "proxyOnly.configFailed";
                await _notify(LastFailureReason);
                return false;
            }

            // OpenVPN and custom configs own their network stack and expose no local
            // SOCKS/HTTP inbound, so the system proxy cannot be routed through them.
            if (build.Context.RunCoreType == ECoreType.openvpn
                || node.ConfigType == EConfigType.Custom)
            {
                LastFailureReason = "proxyOnly.unsupportedNode";
                await _notify(LastFailureReason);
                return false;
            }

            var context = ToProxyOnlyContext(build.Context);
            var host = _hostFactory(config, async (notify, msg) =>
            {
                if (notify)
                {
                    await _notify(msg);
                }
            });

            await host.StartAsync(context, null);
            _host = host;
            _running = true;
            LastFailureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN proxy-only core failed to start", ex);
            _running = false;
            _host = null;
            LastFailureReason = "proxyOnly.startFailed";
            await _notify(LastFailureReason);
            return false;
        }
    }

    private static CoreEngineHost GetOrCreateHost(Config config, Func<bool, string, Task> update)
    {
        var host = AppManager.Instance.CoreEngineHost;
        if (host is not null)
        {
            return host;
        }

        host = new CoreEngineHost(config, update);
        AppManager.Instance.CoreEngineHost = host;
        return host;
    }

    /// <summary>
    /// Turns a normal connection context into a proxy-only one: TUN disabled, no core
    /// protection list, and the routing item stripped of the app-managed rules (per-app
    /// entries + mode catch-alls) so nothing shadows the route.final = proxy fall-through
    /// that GenRoutingGlobal produces for non-TUN configs. Also used by the CoreManager
    /// TUN-missing fallback so the split rules cannot silently misroute without PID
    /// attribution.
    /// </summary>
    internal static CoreConfigContext ToProxyOnlyContext(CoreConfigContext context)
    {
        var routing = ManualRoutingRules.StripManagedRules(context.RoutingItem);
        return context with { IsTunEnabled = false, ProtectCoreTypeList = [], RoutingItem = routing };
    }
}
