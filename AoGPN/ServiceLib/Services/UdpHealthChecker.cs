namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// UdpHealthChecker — hedef sunucunun UDP yolunu (51820/udp) sağlık testi
//
// "Bağlan" akışında Tier seçiminin kalbi: seçilen sunucunun UDP portuna
// asenkron bir UdpClient ile küçük bir test paketi gönderilir ve üç sinyal
// ayırt edilir:
//
//   Open        — karşı taraf yanıt verdi                  → WireGuardUDP
//   Blocked     — ICMP Port Unreachable (ConnectionReset)  → V2rayTCP
//   NoResponse  — zaman aşımı (UDP sessiz)                 → politika kararı
//   HandshakeNoResponse — geçerli WG el sıkışması yanıtsız → V2rayTCP (varsayılan)
//
// Önemli teknik not: socket "connected" (UdpClient.Connect) olarak açılır —
// Windows'ta bağlı UDP soketleri ICMP Port Unreachable'ı SocketException
// (ConnectionReset) olarak yüzeye çıkarır; bağlı olmayan soketlerde bu hata
// sessizce kaybolur ve "bloklu" algılanamaz.
//
// WireGuard gerçeği: 1 baytlık junk pakete WireGuard sunucusu YANIT VERMEZ
// (paket 148 bayttan kısa olduğu için sessizce düşürülür). Bu yüzden gerçek
// bir WireGuard sunucusunda "NoResponse" genellikle SAĞLIKLI UDP yolu demektir
// (paket ulaştı, sunucu sustu). "Blocked" ise paketin hiç ulaşmadığına dair
// kesin ICMP kanıtıdır. Karar politikası UdpHealthCheckOptions üzerinden
// ayarlanabilir (bkz. GpnServerSelectionService.DecideMode).
//
// HandshakeNoResponse FARKLI bir durumdur: geçerli bir WireGuard el sıkışması
// gönderildi ama yanıt gelmedi (ICMP kanıtı da yok). Sağlıklı bir sunucu geçerli
// el sıkışmaya mutlaka yanıt verir — sessizlik, junk-probe'daki "beklenen
// sessizlik" DEĞİLDİR; güçlü bir bozukluk işaretidir (güvenlik listesi sessiz
// düşürüyor, sunucu wg0'ı kapalı, dönüş yolu bozuk veya anahtar uyuşmuyor).
// ─────────────────────────────────────────────────────────────────────────

/// <summary>UDP sağlık testinin sonucu.</summary>
public enum UdpProbeStatus
{
    /// <summary>Yanıt alındı — UDP yolu açık ve çalışıyor.</summary>
    Open,

    /// <summary>ICMP Port Unreachable — port kapalı veya UDP paketi engellendi (kesin kanıt).</summary>
    Blocked,

    /// <summary>Zaman aşımı — karşı taraf sessiz (WireGuard junk pakete yanıt vermez).</summary>
    NoResponse,

    /// <summary>
    /// Geçerli WireGuard el sıkışması gönderildi ama yanıt gelmedi — ICMP kanıtı
    /// yok. "Blocked"tan farkı: bloklamayı KANITLAYAN ICMP hatası yok; sessizlik
    /// (sessiz düşürme / sunucu wg0'ı kapalı / dönüş yolu bozuk / anahtar
    /// uyuşmazlığı) yüzünden el sıkışma tamamlanamıyor. Sağlıklı sunucu geçerli
    /// el sıkışmaya yanıt vereceğinden bu durum NoResponse'tan (junk beklenen
    /// sessizlik) çok daha güçlü bir bozukluk işaretidir.
    /// </summary>
    HandshakeNoResponse,
}

/// <summary>Tek UDP sağlık testinin sonucu.</summary>
public sealed record UdpProbeResult(
    string ServerId,
    UdpProbeStatus Status,
    int RoundTripMs,
    bool IsReachable,     // Status == Open
    string? Detail = null);

/// <summary>
/// UDP sağlık testi ayarları.
/// <c>TreatNoResponseAsBlocked</c>: junk-probe NoResponse'unu fallback tetikleyici
/// say. Varsayılan false — gerçek WireGuard sunucuları junk pakete yanıt vermediği
/// için NoResponse normalde sağlıklı UDP yolu demektir.
/// <c>TreatHandshakeNoResponseAsBlocked</c>: geçerli el sıkışma yanıtsızsa fallback
/// tetikle. Varsayılan TRUE — sağlıklı sunucu geçerli el sıkışmaya yanıt vereceğinden
/// HandshakeNoResponse güçlü bozukluk işaretidir (NoResponse'tan ayrı tutulur).
/// </summary>
public sealed record UdpHealthCheckOptions(
    int WaitTimeoutMs = 3000,           // yanıt bekleme süresi
    int PayloadSize = 1,                // gönderilen test paketi boyutu (bayt)
    bool TreatNoResponseAsBlocked = false,
    bool TreatHandshakeNoResponseAsBlocked = true);

public interface IUdpHealthChecker
{
    Task<UdpProbeResult> ProbeAsync(
        string serverId,
        string host,
        int port,
        UdpHealthCheckOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class UdpHealthChecker : IUdpHealthChecker
{
    private const string Tag = "UdpHealth";
    private readonly UdpProbeSocketFactory _socketFactory;

    /// <summary>Üretim kurucusu — gerçek UdpClient tabanlı soket kullanır.</summary>
    public UdpHealthChecker()
        => _socketFactory = static family => new UdpClientProbeSocket(family);

    /// <summary>
    /// Test enjeksiyon noktası: sahte ICMP kanıtı (ConnectionReset fırlatan sahte
    /// soket) verilerek kapalı-port davranışı Windows ICMP rate-limit'inden
    /// bağımsız doğrulanabilir.
    /// </summary>
    internal UdpHealthChecker(UdpProbeSocketFactory socketFactory)
        => _socketFactory = socketFactory;

    /// <summary>
    /// Hedef sunucunun UDP portuna 1 baytlık (yapılandırılabilir) test paketi
    /// gönderir ve yanıt / ICMP hatası / zaman aşımı sinyallerini sınıflandırır.
    /// </summary>
    public async Task<UdpProbeResult> ProbeAsync(
        string serverId,
        string host,
        int port,
        UdpHealthCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new UdpHealthCheckOptions();
        var sw = Stopwatch.StartNew();

        try
        {
            // Bağlı UDP soketi: ICMP hataları SocketException olarak yüzeye çıkar.
            using var udp = _socketFactory(ResolveAddressFamily(host));
            udp.Connect(host, port);

            var payload = new byte[Math.Clamp(options.PayloadSize, 1, 512)];

            // İki gönderim + alım döngüsü. Bağlı UDP soketlerinde ICMP Port
            // Unreachable bazen yalnızca bir SONRAKİ gönderimde yüzeye çıkar;
            // ikinci döngü bu geç hata yüzeylemesini yakalar (kapalı/engelli
            // portların kesin tespiti). Gerçek WireGuard sunucusu junk pakete
            // yanıt vermeyeceği için ikinci deneme zararsızdır.
            UdpProbeResult? result = null;
            for (var i = 0; i < 2; i++)
            {
                // Her döngü taze bir zaman penceresi alır — önceki döngünün
                // zaman aşımı ikinci gönderimi iptal etmemeli.
                using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cycleCts.CancelAfter(options.WaitTimeoutMs);

                await udp.SendAsync(payload, cycleCts.Token).ConfigureAwait(false);
                try
                {
                    await udp.ReceiveAsync(cycleCts.Token).ConfigureAwait(false);
                    result = new UdpProbeResult(serverId, UdpProbeStatus.Open, (int)sw.ElapsedMilliseconds, true);
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Dahili süre doldu — döngüyü bir kez daha dene.
                }
            }

            if (result is not null)
            {
                return result;
            }

            // Dış iptalse yukarı taşı; değilse WireGuard sessizliği = NoResponse
            // (politika kararı çağıran tarafta — DecideMode).
            cancellationToken.ThrowIfCancellationRequested();
            return new UdpProbeResult(serverId, UdpProbeStatus.NoResponse, -1, false,
                $"timeout after {options.WaitTimeoutMs}ms (2 probe cycle)");
        }
        catch (SocketException ex) when (IsIcmpUnreachable(ex.SocketErrorCode))
        {
            sw.Stop();
            // ICMP Port Unreachable / ağ erişilemez — paket hedefe ulaşmadı.
            Logging.SaveLog($"[{Tag}] {serverId} ({host}:{port}) UDP bloklu: {ex.SocketErrorCode}");
            return new UdpProbeResult(serverId, UdpProbeStatus.Blocked, (int)sw.ElapsedMilliseconds, false, ex.SocketErrorCode.ToString());
        }
        catch (OperationCanceledException)
        {
            // Dış iptal — çağıran tarafın iptali, sonuç değil.
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logging.SaveLog($"[{Tag}] {serverId} ({host}:{port}) probe hatası: {ex.Message}");
            return new UdpProbeResult(serverId, UdpProbeStatus.NoResponse, -1, false, ex.Message);
        }
    }

    private static AddressFamily ResolveAddressFamily(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address.AddressFamily;
        }
        return AddressFamily.InterNetwork;
    }

    /// <summary>
    /// UDP'de "port kapalı / paket engellendi" anlamına gelen ICMP türevi
    /// soket hataları. Windows bağlı UDP soketlerinde ICMP Port Unreachable
    /// ConnectionReset olarak yüzeye çıkar; Linux'ta ConnectionRefused olabilir.
    /// </summary>
    private static bool IsIcmpUnreachable(SocketError error) => error is
        SocketError.ConnectionReset or
        SocketError.ConnectionRefused or
        SocketError.NetworkUnreachable or
        SocketError.HostUnreachable;
}
