using ServiceLib.Manager;
using ServiceLib.Services.CoreConfig.Mihomo;

namespace ServiceLib.Services.Gpn;

/// <summary>
/// Kesintisiz düğüm geçişi sonrası eski düğümün boşalma (drain) izleyicisi.
///
/// Geçiş (GPN-Nodes grubu seçiminin PUT /proxies ile değiştirilmesi) çekirdeği
/// durdurmadığı için geçiş anında zaten açık olan oturumlar eski düğümün
/// outbound'unda (wg-&lt;eskiId&gt;) doğal olarak bitene kadar sürer. Bu sınıf,
/// mihomo <c>GET /connections</c> yanıtındaki her bağlantının <c>chains</c>
/// (zincir) alanında eski wg-&lt;id&gt; adını arayarak kalan oturum sayısını
/// düzenli aralıklarla okur ve <see cref="GpnDrainSnapshot"/> olarak yayınlar:
///
///   progress → { IsDraining: true,  RemainingConnections: N }
///   son (0 veya zaman aşımı) → { IsDraining: false, TimedOut, RemainingConnections: kalan }
///
/// Bağlantılar kendiliğinden bitmediğinde <paramref name="maxDuration"/> sonunda
/// TimedOut anlık görüntüsüyle biter (bağlantılar zorla kapatılmaz — oyun UDP
/// akışları gibi kısa ömürlü oturumlar çoktan bitmiştir; uzun TCP oturumları
/// eski düğümde doğal olarak devam eder).
/// </summary>
public sealed class GpnDrainWatcher
{
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxDuration;

    public GpnDrainWatcher(TimeSpan? pollInterval = null, TimeSpan? maxDuration = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1500);
        _maxDuration = maxDuration ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// /connections yanıtında eski düğümün wg outbound'undan geçen oturum sayısı.
    /// Zincir, grubun adını da içerebilir (örn. ["GPN-Nodes", "wg-it"]); eşleşme
    /// wg-&lt;id&gt; adı üzerinden yapılır.
    /// </summary>
    public static int CountRemainingConnections(ClashConnections? connections, string wgProxyName)
    {
        if (connections?.connections is not { Count: > 0 } list || wgProxyName.IsNullOrEmpty())
        {
            return 0;
        }
        return list.Count(c => c.chains?.Contains(wgProxyName, StringComparer.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Boşalma ilerlemesini yayınlayarak izler. Her yoklamada ve sonlandığında
    /// <paramref name="publish"/> çağrılır; iptal edilirse sessizce fırlar (son
    /// anlık görüntü yayınlanmaz — yeni bir geçiş/eski izleyici iptali söz konusudur).
    /// </summary>
    public async Task<GpnDrainSnapshot> RunAsync(
        GpnServerProfile fromServer,
        GpnServerProfile toServer,
        Func<Task<ClashConnections?>> fetchConnections,
        Action<GpnDrainSnapshot> publish,
        CancellationToken cancellationToken = default)
    {
        var wgProxyName = GpnMihomoConfigService.WireGuardProxyName(fromServer);
        var startedAt = DateTimeOffset.UtcNow;
        var remaining = -1;
        var timedOut = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var connections = await fetchConnections().ConfigureAwait(false);
                remaining = CountRemainingConnections(connections, wgProxyName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // /connections tek bir yoklamada okunamadı — kalan bilinmiyor; bir
                // sonraki turda tekrar dene. (Drenaj gözlemi kritik değil, geçiş zaten
                // tamamlandı.)
                DiagLog.Write($"[GpnDrain] /connections okunamadı: {ex.Message}");
                remaining = -1;
            }

            publish(new GpnDrainSnapshot(
                IsDraining: true,
                remaining,
                fromServer.ServerId, fromServer.Name,
                toServer.ServerId, toServer.Name));

            if (remaining == 0)
            {
                break;
            }
            if (DateTimeOffset.UtcNow - startedAt >= _maxDuration)
            {
                timedOut = remaining != 0;
                break;
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        var final = new GpnDrainSnapshot(
            IsDraining: false,
            remaining,
            fromServer.ServerId, fromServer.Name,
            toServer.ServerId, toServer.Name,
            timedOut);
        publish(final);
        DiagLog.Write($"GPN_DRAIN {fromServer.ServerId}→{toServer.ServerId} kalan={remaining} timedOut={timedOut}");
        return final;
    }
}
