using ServiceLib.Common;
using ServiceLib.Events;

namespace ServiceLib.Services.Gpn;

/// <summary>
/// WARP egress yolunu otomatik onaran hizmet. WarpDialHealthMonitor faulted
/// duruma geçince (eşik hatalar) aktif WireGuard tünelini yeniden başlatır —
/// WARP SOCKS5 dinleyicisi (sunucu wireproxy, 10.66.66.1:40000) yalnızca tünel
/// üzerinden erişilebildiği için tüneli yeniden kurmak yolu onarır.
///
/// Kurallar:
///  - cooldown (varsayılan 90 sn) — sık yeniden başlatmayı önler;
///  - oturum başına deneme limiti (varsayılan 3) — kalıcı arızada döngü yok;
///  - bağlantı değişimi (Connected/Disconnected/Failed) sayaçları sıfırlar;
///  - kullanıcı ayarı (GuiItem.GpnEnableWarpAutoRecover, varsayılan true)
///    kapalıysa no-op.
///
/// Kararlar <see cref="AppEvents.WarpDialHealthChanged"/> ve
/// <see cref="AppEvents.GpnConnectionStateChanged"/> kanallarından beslenir;
/// diag çıktısı GPN_WARP_RECOVER satırlarıdır.
/// </summary>
public sealed class WarpAutoRecoverService : IDisposable
{
    public const int DefaultMaxAttempts = 3;

    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(90);

    private readonly Func<CancellationToken, Task<bool>> _reconnect;
    private readonly Func<bool> _isEnabled;
    private readonly TimeSpan _cooldown;
    private readonly int _maxAttempts;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();

    private IDisposable? _healthSub;
    private IDisposable? _stateSub;
    private int _attempts;
    private DateTimeOffset? _lastAttemptUtc;
    private bool _recovering;
    private bool _disposed;

    public WarpAutoRecoverService(
        Func<CancellationToken, Task<bool>> reconnect,
        Func<bool>? isEnabled = null,
        TimeSpan? cooldown = null,
        int? maxAttempts = null)
    {
        _reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
        _isEnabled = isEnabled ?? (() => true);
        _cooldown = cooldown ?? DefaultCooldown;
        _maxAttempts = maxAttempts ?? DefaultMaxAttempts;
    }

    /// <summary>Test kancası: hizmetin kullandığı şimdiki zaman.</summary>
    internal DateTimeOffset NowUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Bu oturumda yapılan yeniden başlatma denemesi sayısı.</summary>
    internal int Attempts
    {
        get
        {
            lock (_sync)
            {
                return _attempts;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_healthSub is not null)
            {
                return;
            }
            _healthSub = AppEvents.WarpDialHealthChanged.AsObservable()
                .Subscribe(h => { _ = ProcessHealth(h); });
            _stateSub = AppEvents.GpnConnectionStateChanged.AsObservable()
                .Subscribe(ProcessState);
        }
    }

    /// <summary>
    /// WARP sağlık olayını işler; faulted ise kurtarma dener. Testler için
    /// doğrudan çağrılabilir; dönen Task kurtarma denemesi bitince tamamlanır.
    /// </summary>
    internal Task ProcessHealth(WarpDialHealth health)
    {
        lock (_sync)
        {
            if (_disposed || !health.Faulted || _recovering || !_isEnabled())
            {
                return Task.CompletedTask;
            }
            if (_attempts >= _maxAttempts)
            {
                return Task.CompletedTask;
            }
            var now = NowUtc;
            if (_lastAttemptUtc is not null && now - _lastAttemptUtc.Value < _cooldown)
            {
                return Task.CompletedTask;
            }

            _attempts++;
            _lastAttemptUtc = now;
            _recovering = true;
            return RunRecoveryAsync(_cts.Token);
        }
    }

    /// <summary>Bağlantı durumu değişiminde sayaçları sıfırla — eski oturumun denemeleri yenisine taşınmaz.</summary>
    internal void ProcessState(GpnConnectionSnapshot snap)
    {
        lock (_sync)
        {
            if (snap.State is GpnConnectionState.Connected
                or GpnConnectionState.Disconnected
                or GpnConnectionState.Failed)
            {
                _attempts = 0;
                _lastAttemptUtc = null;
            }
        }
    }

    private async Task RunRecoveryAsync(CancellationToken ct)
    {
        var attempt = 0;
        try
        {
            lock (_sync)
            {
                attempt = _attempts;
            }
            var ok = await _reconnect(ct).ConfigureAwait(false);
            DiagLog.Write(ok
                ? $"GPN_WARP_RECOVER attempt={attempt}/{_maxAttempts} ok (tünel yeniden başlatıldı)"
                : $"GPN_WARP_RECOVER attempt={attempt}/{_maxAttempts} rejected (mod/tünel uygun değil)");
            if (ok)
            {
                NoticeManager.Instance.Enqueue(ResUI.WarpDialRecoverNotice);
            }
        }
        catch (OperationCanceledException)
        {
            // Kapanış/iptal — sessizce bırak.
        }
        catch (Exception ex)
        {
            DiagLog.Write($"GPN_WARP_RECOVER attempt={attempt}/{_maxAttempts} error: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _recovering = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _healthSub?.Dispose();
            _healthSub = null;
            _stateSub?.Dispose();
            _stateSub = null;
        }
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}