namespace ServiceLib.Services;

/// <summary>
/// The process/TUN runtime contract consumed by the application-owned host.
/// Keeping this boundary separate from configuration generation allows lifecycle
/// behavior to be tested without launching a real core process.
/// </summary>
public interface ICoreRuntime
{
    IReadOnlyDictionary<CoreHealthRole, CoreHealthSnapshot> Health { get; }

    CoreHealthSnapshot GetHealth(CoreHealthRole role);

    Task InitializeAsync(Config config, Func<bool, string, Task> update);

    Task StartAsync(CoreConfigContext? mainContext, CoreConfigContext? preContext);

    Task StopAsync();
}

/// <summary>
/// Production adapter for the existing CoreManager implementation.
/// </summary>
public sealed class CoreManagerRuntime : ICoreRuntime
{
    private readonly CoreManager _coreManager;

    public CoreManagerRuntime(CoreManager? coreManager = null)
    {
        _coreManager = coreManager ?? CoreManager.Instance;
    }

    public IReadOnlyDictionary<CoreHealthRole, CoreHealthSnapshot> Health =>
        _coreManager.Health;

    public CoreHealthSnapshot GetHealth(CoreHealthRole role) =>
        _coreManager.GetHealth(role);

    public Task InitializeAsync(Config config, Func<bool, string, Task> update) =>
        _coreManager.Init(config, update);

    public Task StartAsync(CoreConfigContext? mainContext, CoreConfigContext? preContext) =>
        _coreManager.LoadCore(mainContext, preContext);

    public Task StopAsync() =>
        _coreManager.CoreStop();
}
