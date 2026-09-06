using System.Security.Cryptography;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WireGuardHandshakeProbe — gerçek WireGuard el sıkışmasıyla KESİN UDP kanıtı
//
// UdpHealthChecker'ın 1 baytlık "junk" paketinin aksine bu probe, istemci özel
// anahtarıyla GEÇERLİ bir Noise_IKpsk2 handshake initiation paketi üretir ve
// sunucuya gönderir. Sunucu MAC1'i doğrular, el sıkışmayı işler ve bir
// Handshake Response paketi (tip 2, 92 bayt) ile yanıt verir. Bu yanıt,
// UDP yolunun gerçekten çalıştığının ve sunucunun bu anahtarları tanıdığının
// kesin kanıtıdır.
//
//   Open        — geçerli Handshake Response alındı (MAC1 doğrulandı / cookie_
//   Blocked     — ICMP Port Unreachable (UDP engelli / port kapalı)
//   HandshakeNoResponse — zaman aşımı: geçerli el sıkışma gönderildi ama yanıt yok
//   NoResponse  — yalnızca probe hatası / anahtar eksik (ağ teşhisi yok)
//
// HandshakeNoResponse vs Blocked (canlı testte gözlenen senaryo):
//   * Blocked = ICMP kanıtı VAR — paket hedefe ulaşmadı (port kapalı / güvenlik
//     listesi açıkça reddediyor). Kesin.
//   * HandshakeNoResponse = ICMP kanıtı YOK ama sağlıklı sunucunun yanıtlaması
//     GEREKEN geçerli el sıkışmaya sessizlik. Nedenleri: güvenlik listesi sessizce
//     düşürüyor, sunucu wg0'ı kapalı/çökük, dönüş yolu bozuk veya sunucu MAC1'i
//     doğrulayamadı (yanlış sunucu anahtarı). Junk-probe'daki beklenen sessizliğin
//     aksine bu durum tünelin kurulamayacağına dair güçlü işarettir — GpnServerSelectionService
//     varsayılan olarak V2rayTCP'ye düşer (TreatHandshakeNoResponseAsBlocked).
//
// Sahteciliğe karşı: gelen yanıtın MAC1 alanı, bu istemcinin statik genel
// anahtarına dayanan anahtarla doğrulanır — MAC1 eşleşmezse Open SAYILMAZ.
// (Yük altındaki WireGuard sunucusu önce tip-3 cookie reply gönderir; bu da
// MAC1'in kabul edildiğinin iyi bir işaretidir, Open sayılır.)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>Gerçek WireGuard el sıkışmasıyla UDP sağlık testi.</summary>
public interface IWireGuardHandshakeProbe
{
    /// <summary>
    /// Geçerli bir handshake initiation gönderir ve kesin yanıtı bekler.
    /// Anahtarlar base64 (WireGuard .conf ile aynı format).
    /// </summary>
    Task<UdpProbeResult> ProbeAsync(
        string serverId,
        string host,
        int port,
        string? serverPublicKeyBase64,
        string? clientPrivateKeyBase64,
        WireGuardHandshakeProbeOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Handshake probe ayarları.</summary>
public sealed record WireGuardHandshakeProbeOptions(
    int WaitTimeoutMs = 4000,       // tek deneme penceresi
    int MaxAttempts = 2);           // toplam gönderim sayısı (geç ICMP için)

public sealed class WireGuardHandshakeProbe : IWireGuardHandshakeProbe
{
    private const string Tag = "WgHandshake";
    private readonly UdpProbeSocketFactory _socketFactory;

    // Hata logu rate-limit: dashboard'ın canlı küme kartı her ~15-30 sn'de tüm
    // sunucuları probe'lar; sunucu yanıtsızken her döngü özdeş "el sıkışma
    // yanıtsız" satırı basar (günde on binlerce). İlk oluşum + her 50.'si loglanır;
    // teşhis korunur, günlük şişmez. Karar mantığı etkilenmez — yalnızca loglama.
    private readonly ConcurrentDictionary<string, int> _failureCounters = new();

    private bool LogFailureRateLimited(string serverId, string message)
    {
        var n = _failureCounters.AddOrUpdate(serverId, 1, (_, c) => c + 1);
        if (n == 1 || n % 50 == 0)
        {
            Logging.SaveLog(message);
            return true;
        }
        return false;
    }

    /// <summary>Üretim kurucusu — gerçek UdpClient tabanlı soket kullanır.</summary>
    public WireGuardHandshakeProbe()
        => _socketFactory = static family => new UdpClientProbeSocket(family);

    /// <summary>
    /// Test enjeksiyon noktası: sahte ICMP kanıtı (ConnectionReset fırlatan sahte
    /// soket) verilerek kapalı-port davranışı Windows ICMP rate-limit'inden
    /// bağımsız doğrulanabilir.
    /// </summary>
    internal WireGuardHandshakeProbe(UdpProbeSocketFactory socketFactory)
        => _socketFactory = socketFactory;

    /// <inheritdoc />
    public async Task<UdpProbeResult> ProbeAsync(
        string serverId,
        string host,
        int port,
        string? serverPublicKeyBase64,
        string? clientPrivateKeyBase64,
        WireGuardHandshakeProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WireGuardHandshakeProbeOptions();

        // Anahtar yoksa handshake üretilemez — çağıran junk probe'a düşebilir.
        if (string.IsNullOrWhiteSpace(serverPublicKeyBase64) || string.IsNullOrWhiteSpace(clientPrivateKeyBase64))
        {
            return new UdpProbeResult(serverId, UdpProbeStatus.NoResponse, -1, false, "anahtar eksik");
        }

        byte[] serverPublicKey;
        byte[] clientPrivateKey;
        try
        {
            serverPublicKey = Convert.FromBase64String(serverPublicKeyBase64.Trim());
            clientPrivateKey = Convert.FromBase64String(clientPrivateKeyBase64.Trim());
        }
        catch (FormatException ex)
        {
            return new UdpProbeResult(serverId, UdpProbeStatus.NoResponse, -1, false, $"geçersiz anahtar: {ex.Message}");
        }

        // Rastgele sender index (elmış yönlendirme için; response'ta receiver olur).
        var idx = new byte[4];
        RandomNumberGenerator.Fill(idx);
        // Little-endian uint32 — (idx[3] << 24) int taşması overflow kontrolü altında
        // fırlar; unchecked sarmalayıcı aritmetiği two's-complement truncate eder.
        var senderIndex = unchecked((uint)(idx[0] | (idx[1] << 8) | (idx[2] << 16) | (idx[3] << 24)));

        var (packet, ourStaticPublic, _) = WireGuardNoise.BuildInitiation(
            clientPrivateKey, serverPublicKey, senderIndex);

        var sw = Stopwatch.StartNew();
        try
        {
            using var udp = _socketFactory(ResolveAddressFamily(host));
            udp.Connect(host, port);

            // Bağlı sokette ICMP Port Unreachable SocketException olarak yüzeye
            // çıkar — bazı durumlarda yalnızca bir SONRAKİ gönderimde. Bu yüzden
            // birden çok deneme yapılır.
            for (var attempt = 0; attempt < options.MaxAttempts; attempt++)
            {
                await udp.SendAsync(packet, cancellationToken).ConfigureAwait(false);
                var received = await AwaitResponseAsync(
                    udp, ourStaticPublic, serverId, options.WaitTimeoutMs, cancellationToken).ConfigureAwait(false);
                if (received is not null)
                {
                    sw.Stop();
                    return received with { RoundTripMs = (int)sw.ElapsedMilliseconds };
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            sw.Stop();
            // Geçerli el sıkışma yanıtsız — junk-probe'daki "beklenen sessizlik"
            // DEĞİL; sağlıklı sunucu yanıt verirdi. ICMP kanıtı yok → Blocked'tan
            // ayrı durum (HandshakeNoResponse); karar GpnServerSelectionService'te.
            LogFailureRateLimited(serverId,
                $"[{Tag}] {serverId} ({host}:{port}) el sıkışma yanıtsız ({options.MaxAttempts} gönderim).");
            return new UdpProbeResult(serverId, UdpProbeStatus.HandshakeNoResponse, -1, false,
                $"timeout after {options.MaxAttempts} attempts ({options.WaitTimeoutMs}ms) — geçerli el sıkışma yanıtsız");
        }
        catch (SocketException ex) when (IsIcmpUnreachable(ex.SocketErrorCode))
        {
            sw.Stop();
            LogFailureRateLimited(serverId,
                $"[{Tag}] {serverId} ({host}:{port}) UDP bloklu: {ex.SocketErrorCode}");
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
            LogFailureRateLimited(serverId,
                $"[{Tag}] {serverId} ({host}:{port}) probe hatası: {ex.Message}");
            return new UdpProbeResult(serverId, UdpProbeStatus.NoResponse, -1, false, ex.Message);
        }
    }

    /// <summary>Yanıtı bekle ve sınıflandır. null → bu denemede yanıt gelmedi.</summary>
    private static async Task<UdpProbeResult?> AwaitResponseAsync(
        IUdpProbeSocket udp,
        byte[] ourStaticPublic,
        string serverId,
        int windowMs,
        CancellationToken cancellationToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(windowMs);

        try
        {
            while (!window.IsCancellationRequested)
            {
                var datagram = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);

                var type = WireGuardNoise.PeekMessageType(datagram.Buffer);
                if (type == WireGuardNoise.MessageTypeResponse
                    && datagram.Buffer.Length == WireGuardNoise.ResponseSize
                    && WireGuardNoise.VerifyResponseMac1(datagram.Buffer, ourStaticPublic))
                {
                    return new UdpProbeResult(serverId, UdpProbeStatus.Open, -1, true, "handshake_response (MAC1 doğrulandı)");
                }

                // Cookie reply (yük altındaki sunucu MAC1'i kabul ettiğinde gönderir)
                // — UDP yolunun çalıştığının ve sunucunun paketi tanıdığının işareti.
                if (type == WireGuardNoise.MessageTypeCookieReply && datagram.Buffer.Length == 64)
                {
                    return new UdpProbeResult(serverId, UdpProbeStatus.Open, -1, true, "cookie_reply (yük altında)");
                }

                // Tanınmayan datagram — görmezden gel, bekle.
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // pencere doldu — bu deneme sonuçsuz
        }
        catch (SocketException ex) when (IsIcmpUnreachable(ex.SocketErrorCode))
        {
            return new UdpProbeResult(serverId, UdpProbeStatus.Blocked, -1, false, ex.SocketErrorCode.ToString());
        }

        return null;
    }

    private static AddressFamily ResolveAddressFamily(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address.AddressFamily;
        }
        return AddressFamily.InterNetwork;
    }

    private static bool IsIcmpUnreachable(SocketError error) => error is
        SocketError.ConnectionReset or
        SocketError.ConnectionRefused or
        SocketError.NetworkUnreachable or
        SocketError.HostUnreachable;
}