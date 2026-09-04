namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Stratejilerin ortak davranışı: StartAsync tüm çekirdekler için aynıdır
/// (CoreManager.CoreStart'ın karar bloğu — BİREBİR taşınmıştır). Çekirdeğe
/// özgü olan yalnızca OwnsTun / IsNativeTunnelCore politikaları ve yaşam
/// döngüsü hook'larıdır; ikisi de varsayılan no-op'tur.
/// </summary>
public abstract class CoreStartStrategyBase : ICoreStartStrategy
{
    /// <inheritdoc/>
    public abstract bool OwnsTun { get; }

    /// <inheritdoc/>
    public abstract bool IsNativeTunnelCore { get; }

    /// <inheritdoc/>
    /// Tüm süreç tabanlı çekirdekler haricidir — null process hâlâ hata sayılır.
    public virtual bool IsInProcessEngine => false;

    /// <inheritdoc/>
    public virtual Task BeforeStartAsync(CoreConfigContext context) => Task.CompletedTask;

    /// <inheritdoc/>
    public virtual Task AfterStopAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<ProcessService?> StartAsync(CoreConfigContext context, CoreProcessLauncher launcher, Action onExited)
    {
        // Use the same immutable core selection that produced the config.
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(context.RunCoreType);

        var displayLog = context.Node.ConfigType != EConfigType.Custom || context.Node.DisplayLog;
        return await launcher(
            coreInfo,
            Global.CoreConfigFileName,
            displayLog,
            true,
            context.IsTunEnabled || IsNativeTunnelCore,
            onExited);
    }
}