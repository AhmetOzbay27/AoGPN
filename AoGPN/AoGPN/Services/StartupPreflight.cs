using ServiceLib.Services;

namespace AoGPN.Services;

/// <summary>
/// Açılış ÖN YÜKLEMESİ (Faz 3).
///
/// İlke: kullanıcı açılışı zaten bekliyor — pahalı ama bağlanmadan bağımsız işler bu
/// pencerede yapılmalı; bağlanma anı ise yalnızca bağlantı için gerekli işlerden
/// oluşan temiz bir hat kalmalı.
///
/// Yapılan işler:
///   1. <b>GPN ölçüm ısıtma</b>: sunucu adaylarının ICMP ve UDP el sıkışma ölçümleri
///      (ve hairpin teşhisi için kendi genel IP'si) önceden yapılıp kısa ömürlü
///      önbelleğe yazılır; sonraki "Bağlan" ölçümü büyük ölçüde hazır veriden
///      karşılanır. Yalnızca kullanıcı GPN akışını yapılandırdıysa çalışır
///      (GPN kapalıyken sunuculara boşa paket gönderilmez).
///   2. <b>Çekirdek ikilisi kontrolü</b>: mihomo/xray yürütülebilirleri yerinde mi?
///      Eksikse kullanıcı bağlanmayı deneyip başarısız olmadan ÖNCE bilgilendirilir
///      (kusuru bağlanma anında öğrenmek en kötü deneyimdir).
///
/// Tasarım kuralları:
///   * Aşamalar PARALEL ve bağımsızdır; biri hata verse diğeri etkilenmez.
///   * Hiçbir aşama hata FIRLATMAZ — açılış ön yükleme yüzünden asla bozulmaz.
///   * İdempotenttir; ikinci çağrı hiçbir şey yapmaz.
///   * Ağ yönlendirme motoruna, WinDivert katmanına ve Tier 4 optimizasyonlarına
///     dokunmaz: yalnızca ölçüm ve dosya kontrolü yapar.
/// </summary>
public sealed class StartupPreflight
{
    private const string Tag = "StartupPreflight";

    private readonly Func<bool> _shouldWarmGpnProbes;
    private readonly Func<CancellationToken, Task> _warmGpnProbeCache;
    private readonly Action<string> _log;
    private readonly Action<string> _notify;
    private readonly Func<IReadOnlyList<string>> _findMissingCores;
    private int _started;

    public StartupPreflight(
        Func<bool> shouldWarmGpnProbes,
        Func<CancellationToken, Task> warmGpnProbeCache,
        Action<string>? log = null,
        Action<string>? notify = null,
        Func<IReadOnlyList<string>>? findMissingCores = null)
    {
        _shouldWarmGpnProbes = shouldWarmGpnProbes ?? throw new ArgumentNullException(nameof(shouldWarmGpnProbes));
        _warmGpnProbeCache = warmGpnProbeCache ?? throw new ArgumentNullException(nameof(warmGpnProbeCache));
        _log = log ?? (_ => { });
        _notify = notify ?? (_ => { });
        _findMissingCores = findMissingCores
            ?? new Func<IReadOnlyList<string>>(() => CoreBinaryPreflight.FindMissingCoreBinaries(
                CoreInfoManager.Instance.GetCoreInfo(), Utils.GetBinPath));
    }

    /// <summary>Ön yüklemeyi çalıştırır (idempotent). Asla hata fırlatmaz.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _log("GPN_PREFLIGHT start");

        // İki aşama bağımsızdır: ölçüm ısıtma ağa çıkar, ikili kontrolü yereldir.
        await Task.WhenAll(
            WarmGpnProbesAsync(cancellationToken),
            CheckCoreBinariesAsync()).ConfigureAwait(false);

        _log("GPN_PREFLIGHT done");
    }

    private async Task WarmGpnProbesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_shouldWarmGpnProbes())
            {
                _log("GPN_PREFLIGHT gpn-warm skipped (GPN akışı yapılandırılmamış)");
                return;
            }

            await _warmGpnProbeCache(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Açılış iptal edildi (kapanış) — sessizce çık.
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] GPN ölçüm ısıtma başarısız", ex);
        }
    }

    private async Task CheckCoreBinariesAsync()
    {
        try
        {
            var missing = _findMissingCores();
            if (missing.Count == 0)
            {
                _log("GPN_PREFLIGHT core-binaries ok");
                return;
            }

            _log($"GPN_PREFLIGHT core-binaries MISSING {string.Join(",", missing)}");
            _notify($"Eksik çekirdek dosyaları: {string.Join(", ", missing)} — bağlantı kurulamaz. "
                + "Ayarlar → Güncelleme'den çekirdekleri indirin.");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Çekirdek ikilisi kontrolü başarısız", ex);
        }

        await Task.CompletedTask;
    }
}
