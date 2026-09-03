namespace ServiceLib.Services;

/// <summary>
/// Single application-owned entry point for core initialization and lifecycle.
/// Config generation remains in the existing ViewModels/services; this class
/// validates assets and serializes process/TUN ownership transitions.
/// </summary>
public sealed class CoreEngineHost : IAsyncDisposable
{
    private readonly Config _config;
    private readonly ICoreRuntime _runtime;
    private readonly CoreBinaryRegistry _binaryRegistry;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly Func<bool, string, Task> _update;
    private readonly Func<Task> _resetProxy;
    private readonly Func<ECoreType, Func<bool, string, Task>, CancellationToken, Task<bool>> _autoCoreInstaller;
    private bool _initialized;
    private bool _stopped;
    private bool _disposed;

    public CoreEngineHost(
        Config config,
        Func<bool, string, Task> update,
        CoreManager? coreManager = null,
        CoreBinaryRegistry? binaryRegistry = null,
        ICoreRuntime? runtime = null,
        Func<Task>? resetProxy = null,
        Func<ECoreType, Func<bool, string, Task>, CancellationToken, Task<bool>>? autoCoreInstaller = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _update = update ?? throw new ArgumentNullException(nameof(update));
        if (runtime is not null && coreManager is not null)
        {
            throw new ArgumentException("Specify either runtime or coreManager, not both.", nameof(runtime));
        }

        _runtime = runtime ?? new CoreManagerRuntime(coreManager);
        _binaryRegistry = binaryRegistry ?? new CoreBinaryRegistry();
        _resetProxy = resetProxy ?? (async () =>
        {
            await SysProxyHandler.UpdateSysProxy(_config, true);
        });
        // GPN mihomo çekirdeği eksikse bağlantı öncesi otomatik indir (UpdateService
        // → CoreInfoManager URL'leri); testlerde sahte kurucu verilir.
        _autoCoreInstaller = autoCoreInstaller
            ?? ((coreType, update, ct) => CoreInstaller.InstallMissingCoreAsync(coreType, update, ct));
    }

    public CoreBinaryValidationResult? LastValidation { get; private set; }

    public IReadOnlyDictionary<CoreHealthRole, CoreHealthSnapshot> Health =>
        _runtime.Health;

    public CoreHealthSnapshot GetHealth(CoreHealthRole role) =>
        _runtime.GetHealth(role);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var validation = _binaryRegistry.Validate();
            LastValidation = validation;

            foreach (var warning in validation.Warnings)
            {
                Logging.SaveLog($"[CoreAssets] {warning}");
            }

            if (!validation.IsValid)
            {
                foreach (var error in validation.Errors)
                {
                    Logging.SaveLog($"[CoreAssets] {error}");
                }

                throw new InvalidOperationException(validation.ErrorSummary);
            }

            await _runtime.InitializeAsync(_config, _update);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task StartAsync(
        CoreConfigContext mainContext,
        CoreConfigContext? preContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mainContext);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var requiredCore = _binaryRegistry.Validate(
                requireSingBox: mainContext.RunCoreType == ECoreType.sing_box,
                requireXray: mainContext.RunCoreType == ECoreType.Xray,
                requireTun: mainContext.IsTunEnabled || preContext?.IsTunEnabled == true,
                requiredCore: mainContext.RunCoreType);
            LastValidation = requiredCore;

            // mihomo eksik → kullanıcıdan hiçbir adım istemeden indir ("GPN Bağlan"
            // ölü binary yüzünden ölmesin). Başarılıysa doğrulama yeniden çalışır;
            // başarısızsa mevcut hata yolu aynen korunur.
            if (!requiredCore.IsValid && mainContext.RunCoreType == ECoreType.mihomo)
            {
                await _update(false, "mihomo çekirdeği bulunamadı — indiriliyor…").ConfigureAwait(false);
                var installed = false;
                try
                {
                    installed = await _autoCoreInstaller(ECoreType.mihomo, _update, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("[CoreEngineHost] mihomo auto-install failed", ex);
                }

                if (installed)
                {
                    await _update(false, "mihomo çekirdeği kuruldu ✓").ConfigureAwait(false);
                    requiredCore = _binaryRegistry.Validate(
                        requireSingBox: mainContext.RunCoreType == ECoreType.sing_box,
                        requireXray: mainContext.RunCoreType == ECoreType.Xray,
                        requireTun: mainContext.IsTunEnabled || preContext?.IsTunEnabled == true,
                        requiredCore: mainContext.RunCoreType);
                    LastValidation = requiredCore;
                }
            }

            if (!requiredCore.IsValid)
            {
                var message = requiredCore.ErrorSummary.IsNullOrEmpty()
                    ? "Core executable missing"
                    : requiredCore.ErrorSummary;
                await _update(true, message);
                throw new InvalidOperationException(message);
            }

            await _update(false, "Connecting...");
            await _runtime.StartAsync(mainContext, preContext);
            var health = _runtime.GetHealth(CoreHealthRole.Main);
            if (health.State != CoreHealthState.Ready)
            {
                var message = health.Error.IsNullOrEmpty()
                    ? "Core failed to become ready."
                    : health.Error;
                await _update(true, message);
                throw new InvalidOperationException(message);
            }
            _stopped = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            await _runtime.StopAsync();
            await _resetProxy();
        }
        catch
        {
            _stopped = false;
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
        _initializationGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }
}
