using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ServiceLib.Common;
using ServiceLib.Events;
using ServiceLib.Manager;
using ServiceLib.Services;

namespace ServiceLib.Services.Gpn;

/// <summary>
/// WARP outbound dial hatalarını canlı yakalayan sağlık monitörü. Çekirdek log
/// kuyruklarını okur: sing-box <c>ao_singbox_&lt;tarih&gt;.log</c> (GPN sing-box
/// oturumları) ve mihomo <c>ao_mihomo_&lt;tarih&gt;.log</c> (GPN mihomo oturumları
/// — üretici log-file yazar). Warp socks outbound'una giden TCP dial hatalarını
/// (örn. <c>no route to host</c>) sayar ve eşiği aşınca durumu Faulted yapar.
///
/// Durum değişimleri:
///  - her hata için <c>WARP_DIAL error detected</c> diag satırı (bölüm başına bir),
///  - eşik aşılınca <c>WARP_DIAL health=faulted</c> + snackbar bildirimi,
///  - hatasız geçen süre (RecoveryCooldown) dolunca <c>WARP_DIAL health=healthy</c>.
///
/// Durum, <see cref="AppEvents.WarpDialHealthChanged"/> kanalından dashboard'a
/// akar; GPN bağlantı durumu değişimlerinde sayaçlar sıfırlanır (eski oturumun
/// hataları yeni oturuma taşınmaz).
/// </summary>
public sealed class WarpDialHealthMonitor : IDisposable
{
    /// <summary>Faulted sayılması için gerekli pencere içindeki hata sayısı.</summary>
    public const int DefaultFaultThreshold = 3;

    /// <summary>Hataların sayıldığı kayan pencere.</summary>
    public static readonly TimeSpan DefaultFaultWindow = TimeSpan.FromSeconds(60);

    /// <summary>Bu süre boyunca yeni hata gelmezse durum Healthy'e döner.</summary>
    public static readonly TimeSpan DefaultRecoveryCooldown = TimeSpan.FromSeconds(45);

    /// <summary>Log kuyruğu yoklama aralığı.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private static readonly Lazy<WarpDialHealthMonitor> _instance = new(() => new());
    public static WarpDialHealthMonitor Instance => _instance.Value;

    private readonly object _sync = new();
    private readonly int _faultThreshold;
    private readonly TimeSpan _faultWindow;
    private readonly TimeSpan _recoveryCooldown;
    private readonly TimeSpan _pollInterval;

    private CancellationTokenSource? _cts;
    private Task? _tailTask;
    private IDisposable? _connectionSub;
    private bool _disposed;

    // İzleyici durumu.
    private readonly Queue<DateTimeOffset> _errorTimes = new();
    private DateTimeOffset? _lastErrorUtc;
    private string _latestError = string.Empty;
    private bool _faulted;
    private bool _episodeDiagged;
    private DateTimeOffset? _episodeSnapshot;

    // Log kuyruğu için dosya izleme durumu (yol → konum/bağlanma).
    private readonly Dictionary<string, (long Position, bool Anchored)> _tracked = new(StringComparer.OrdinalIgnoreCase);

    public WarpDialHealthMonitor(
        int? faultThreshold = null,
        TimeSpan? faultWindow = null,
        TimeSpan? recoveryCooldown = null,
        TimeSpan? pollInterval = null)
    {
        _faultThreshold = faultThreshold ?? DefaultFaultThreshold;
        _faultWindow = faultWindow ?? DefaultFaultWindow;
        _recoveryCooldown = recoveryCooldown ?? DefaultRecoveryCooldown;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>Test kancası: izleyicinin kullandığı şimdiki zaman.</summary>
    internal DateTimeOffset NowUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Anlık sağlık durumu.</summary>
    public WarpDialHealth Snapshot
    {
        get
        {
            lock (_sync)
            {
                return BuildSnapshot();
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tailTask is { IsCompleted: false })
            {
                return;
            }
            _cts = new CancellationTokenSource();
            _tailTask = Task.Run(() => TailLoopAsync(_cts.Token));
            _connectionSub ??= AppEvents.GpnConnectionStateChanged.AsObservable().Subscribe(snap =>
            {
                // Bağlantı değişimlerinde durumu sıfırla: eski oturumun warp
                // hataları yeni oturuma taşınmaz, banner temizlenir.
                if (snap.State is GpnConnectionState.Connecting
                    or GpnConnectionState.Connected
                    or GpnConnectionState.Disconnected)
                {
                    Reset();
                }
            });
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        IDisposable? connectionSub;
        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            _tailTask = null;
            connectionSub = _connectionSub;
            _connectionSub = null;
        }
        connectionSub?.Dispose();
        cts?.Cancel();
    }

    /// <summary>
    /// Sayaçları sıfırlar ve kuyruğu dosyanın sonuna hizalar. Bağlantı
    /// değişimlerinde çağrılır — eski oturumun hataları yeni oturuma taşınmaz.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            var wasFaulted = _faulted;
            _errorTimes.Clear();
            _lastErrorUtc = null;
            _latestError = string.Empty;
            _faulted = false;
            _episodeDiagged = false;
            _episodeSnapshot = null;
            _tracked.Clear();

            if (wasFaulted)
            {
                DiagLog.Write("WARP_DIAL health=healthy (oturum sıfırlandı — bağlantı değişti)");
                AppEvents.WarpDialHealthChanged.Publish(BuildSnapshot());
            }
        }
    }

    /// <summary>
    /// Tek satırı işler; warp outbound hatasıysa durum makinesini ilerletir.
    /// Testler için doğrudan çağrılabilir.
    /// </summary>
    internal WarpDialHealth ProcessLine(string line)
    {
        lock (_sync)
        {
            if (!IsWarpFailureLine(line))
            {
                MaybeRecover();
                return BuildSnapshot();
            }

            var error = ExtractError(line);
            var now = NowUtc;

            // Kayan pencere: pencereden eski hataları düş, yenisini ekle.
            while (_errorTimes.Count > 0 && now - _errorTimes.Peek() > _faultWindow)
            {
                _errorTimes.Dequeue();
            }
            _errorTimes.Enqueue(now);

            _lastErrorUtc = now;
            _latestError = error;

            if (!_episodeDiagged)
            {
                _episodeDiagged = true;
                DiagLog.Write($"WARP_DIAL error detected: {error}");
            }

            if (!_faulted && _errorTimes.Count >= _faultThreshold)
            {
                _faulted = true;
                _episodeSnapshot = now;
                DiagLog.Write(
                    $"WARP_DIAL health=faulted errors={_errorTimes.Count}/{_faultThreshold} " +
                    $"window={_faultWindow.TotalSeconds:0}s first={_errorTimes.Peek():HH:mm:ss} last={now:HH:mm:ss} error={error}");
                NoticeManager.Instance.Enqueue(ResUI.WarpDialFaultNotice);
            }

            // Her hatada snapshot yayınla — canlı sayaç dashboard'da güncel kalır.
            AppEvents.WarpDialHealthChanged.Publish(BuildSnapshot());
            return BuildSnapshot();
        }
    }

    /// <summary>
    /// Her yoklamada çağrılır: yeni satır gelmese de cooldown dolunca iyileşme
    /// değerlendirilir (launcher boştayken log satırı üretilmeyebilir).
    /// </summary>
    internal void CheckRecovery()
    {
        lock (_sync)
        {
            MaybeRecover();
        }
    }

    private void MaybeRecover()
    {
        if (!_faulted || _lastErrorUtc is null)
        {
            return;
        }
        if (NowUtc - _lastErrorUtc.Value < _recoveryCooldown)
        {
            return;
        }

        _faulted = false;
        var cleanSecs = (NowUtc - _lastErrorUtc.Value).TotalSeconds;
        DiagLog.Write($"WARP_DIAL health=healthy (errors={_errorTimes.Count} — {cleanSecs:0}s boyunca hata yok)");
        AppEvents.WarpDialHealthChanged.Publish(BuildSnapshot());
    }

    private WarpDialHealth BuildSnapshot() => new(
        _faulted,
        _errorTimes.Count,
        _latestError.IsNullOrEmpty() ? null : _latestError,
        _errorTimes.Count > 0 ? _errorTimes.Peek() : null,
        _lastErrorUtc);

    /// <summary>
    /// Warp socks outbound'unun dial hatası mı? sing-box şu biçimleri üretir:
    ///   <c>... using outbound/socks[warp]: connect tcp 10.66.66.1:40000: no route to host</c>
    ///   <c>... listen packet connection using  using outbound/socks[warp]: connect tcp ...</c>
    ///   mihomo: <c>level=error msg="[TCP] dial proxy 2(warp-socks) error: dial tcp 10.66.66.1:40000: ..."</c>
    /// Hata sayılması için outbound warp olmalı (sing-box socks[warp] veya mihomo
    /// warp-socks adı) VE bağlantı hatası içermeli.
    /// </summary>
    internal static bool IsWarpFailureLine(string line)
    {
        if (line.IsNullOrEmpty())
        {
            return false;
        }
        if (!line.Contains("socks[warp]", StringComparison.Ordinal)
            && !line.Contains("warp-socks", StringComparison.Ordinal))
        {
            return false;
        }
        // Go tarzı (sing-box/mihomo) + Windows WinSock (mihomo connectex) mesajları.
        return line.Contains("no route to host", StringComparison.Ordinal)
            || line.Contains("connection was refused", StringComparison.Ordinal)
            || line.Contains("connection refused", StringComparison.Ordinal)
            || line.Contains("actively refused", StringComparison.Ordinal)
            || line.Contains("did not properly respond", StringComparison.Ordinal)
            || line.Contains("forcibly closed by the remote host", StringComparison.Ordinal)
            || line.Contains("i/o timeout", StringComparison.Ordinal)
            || line.Contains("context deadline exceeded", StringComparison.Ordinal)
            || line.Contains("network is unreachable", StringComparison.Ordinal)
            || line.Contains("unreachable network", StringComparison.Ordinal)
            || line.Contains("unreachable host", StringComparison.Ordinal);
    }

    /// <summary>Hata satırından okunabilir kısa hatayı çıkarır (warp etiketi sonrası; sing-box ve mihomo).</summary>
    internal static string ExtractError(string line)
    {
        var match = Regex.Match(line, @"(?:socks\[warp\]|warp-socks)[^:]*:\s*(.+)$");
        var err = match.Success ? match.Groups[1].Value.Trim() : line.Trim();
        return err.Length > 200 ? err[..200] : err;
    }

    private async Task TailLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                TailOnce(ct);
                CheckRecovery();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logging.SaveLog("WarpDialHealthMonitor tail loop failed", ex);
            }
        }
    }

    private void TailOnce(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        var logDir = Utils.GetLogPath();
        // Hem sing-box hem mihomo oturumlarını izle: GPN artık mihomo çekirdeğiyle
        // de çalışır ve üretici ao_mihomo_*.log'a yazar (GpnMihomoConfigService log-file).
        TailFile(Path.Combine(logDir, $"ao_singbox_{day}.log"), ct);
        TailFile(Path.Combine(logDir, $"ao_mihomo_{day}.log"), ct);
    }

    private void TailFile(string path, CancellationToken ct)
    {
        lock (_sync)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                var length = stream.Length;
                if (!_tracked.TryGetValue(path, out var state))
                {
                    // İlk bağlanışta tarihsel hataları yineleme — kuyruğun ucuna git.
                    stream.Seek(0, SeekOrigin.End);
                    _tracked[path] = (stream.Position, true);
                    return;
                }

                if (length < state.Position)
                {
                    // Dosya küçüldü (truncate/rollover) — baştan okuma yerine ucuna hizala.
                    stream.Seek(0, SeekOrigin.End);
                    _tracked[path] = (stream.Position, true);
                    return;
                }

                stream.Seek(state.Position, SeekOrigin.Begin);
                if (length - state.Position <= 0)
                {
                    return;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
                string? line;
                while (!ct.IsCancellationRequested && (line = reader.ReadLine()) is not null)
                {
                    _ = ProcessLine(line);
                }
                _tracked[path] = (stream.Position, true);
            }
            catch (IOException)
            {
                // Dosya şu an yazılıyor/kilitli olabilir; sonraki poll dener.
            }
            catch (UnauthorizedAccessException)
            {
                // Kuyruk okunamıyorsa sessizce atla — diag logu zaten NLog'a düşer.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// WARP dial sağlığının anlık görüntüsü. <see cref="AppEvents.WarpDialHealthChanged"/>
/// üzerinden dashboard'a genişletilebilir JSON olarak akar (camelCase).
/// </summary>
public sealed record WarpDialHealth(
    bool Faulted,
    int ErrorCount,
    string? LatestError,
    DateTimeOffset? FirstErrorUtc,
    DateTimeOffset? LastErrorUtc)
{
    public static WarpDialHealth Healthy => new(false, 0, null, null, null);
}