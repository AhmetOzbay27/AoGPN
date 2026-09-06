using System.Net;
using System.Text;

namespace ServiceLib.Services;

/// <summary>
/// WinDivert filtre dizesi üreticisi — WinDivert NETWORK katmanında yakalanacak
/// dışa giden UDP paketlerini seçen filtre. Salt (pure): ağ/çekirdek bağımlılığı
/// yok, birim testleri doğrudan dizgeyi doğrular.
///
/// Örnek çıktı:
///   outbound and udp and (ip)
///   outbound and udp and (ip) and (ip.DstAddr != 92.4.220.236 or udp.DstPort != 51820)
///
/// ⚠ KRİTİK (saha doğrulaması — WinDivert 2.2.2, WinDivertHelperCompileFilter):
/// 1. `processId` alanı NETWORK katmanında GEÇERSİZDİR (WinDivertOpen hata 87
///    döndürür). PID ayrımı yalnızca FLOW/SOCKET katmanlarında vardır; bu araç
///    NETWORK katmanında açtığı için süreç filtresi FILTREYE GİRMEZ — paket
///    sahipliği kullanıcı modunda UDP port→PID tablosuyla çözülür ve
///    GpnCaptureLoop hedef olmayan paketleri tekrar yığına enjekte eder.
/// 2. IPv6 alan adları `ip6`/`icmp6` DEĞİL `ipv6`/`icmpv6`'dır (resmî 2.2.2
///    grameri; `ip6` kullanımı hata 87 üretir).
/// 3. WinDivert filtre dilinde MANTIKSAL DEĞİL (NOT/!) YOKTUR — dışlama ancak
///    `!=` operatörüyle (De Morgan: "A değil ise" → `A != X or B != Y`) yazılır.
///
/// Filtre yakaladığı her paketi yığından çıkarır; tünel motoru bu paketleri ya
/// tünele sokar ya da (hedef dışıysa) geri enjekte eder — süzme boşluğu yoktur.
/// </summary>
public static class WinDivertFilterBuilder
{
    /// <summary>Yalnızca IPv4 — L2/L3 oyun trafiğinin çoğu buradadır.</summary>
    public const string Ipv4Term = "ip";

    /// <summary>IPv6 (resmî WinDivert 2.2.2 alan adı — `ip6` değil).</summary>
    public const string Ipv6Term = "ipv6";

    /// <summary>Yakalanmayacak \"kendi tünelimizin\" UDP çifti (canlı döngü önlemek için).</summary>
    public static readonly string NoFilter = "false";

    /// <summary>
    /// Outbound-UDP filtresi kurar. Süreç filtresi NETWORK katmanında OLMADIĞI
    /// için filtre yalnızca protokol/ağ sürümü + (isteğe bağlı) tünel uç noktası
    /// dışlaması taşır; hedef süreç ayrımı tüketiciye (GpnCaptureLoop) aittir.
    /// </summary>
    /// <param name="ipv4">IPv4 trafiğini dahil et (varsayılan true).</param>
    /// <param name="ipv6">IPv6 trafiğini dahil et (varsayılan false).</param>
    /// <param name="excludedDstHost">
    /// Filtrenin dışında bırakılacak hedef IP (ör. WireGuard sunucu uç noktası).
    /// ZORUNLU — kendi şifreli tünel paketlerimiz (uygulama → sunucu) dışarı
    /// çıkarken tekrar yakalanıp sonsuz döngüye girmesin. Bu, tünelin kendi
    /// egress'ini canlı döngüden hariç tutar; `!=` ile De Morgan biçiminde yazılır
    /// (WinDivert gramerinde NOT yoktur).
    /// </param>
    /// <param name="excludedDstPort">excludedDstHost ile birlikte kullanılır.</param>
    public static string BuildFilter(
        bool ipv4 = true,
        bool ipv6 = false,
        string? excludedDstHost = null,
        int? excludedDstPort = null)
    {
        if (!ipv4 && !ipv6)
        {
            throw new ArgumentException("Hedef IP sürümü en az biri açık olmalı.", nameof(ipv4));
        }

        var network = ipv4 && ipv6 ? $"{Ipv4Term} or {Ipv6Term}" : ipv4 ? Ipv4Term : Ipv6Term;

        var sb = new StringBuilder();
        sb.Append("outbound and udp and (").Append(network).Append(')');

        // Loopback dışlama — WinDivert 2.2 loopback (localhost) trafiğini NETWORK
        // katmanında YAKALAR (yalnızca outbound sayar); yakalanan loopback UDP'si
        // geri enjekte edilemez/yeniden yığına dönmez (canlı doğrulama: bağlıyken
        // 127.0.0.1 UDP echo 1 sn timeout'a düşüyor, kapalıyken 55 ms'de dönüyor).
        // Yerel hizmetler (proxy/health-check/test) kırılmasın diye 127.0.0.0/8 ve
        // ::1 yakalama DIŞINDA tutulur. WinDivert grameri CIDR/`not` desteklemediği
        // için IPv4 aralık (>,<) + IPv6 `!= ::1` biçiminde yazılır (probe ile
        // derleme doğrulandı). Aile başına ayrı cümle — yanlış aile alanı tüm
        // paketleri filtre dışı bırakır (or ile birleştirilir).
        var ipv4LoopbackExclusion = "(ip.DstAddr < 127.0.0.0 or ip.DstAddr > 127.255.255.255)";
        var ipv6LoopbackExclusion = "(ipv6.DstAddr != ::1)";
        var loopbackClause = (ipv4, ipv6) switch
        {
            (true, false) => ipv4LoopbackExclusion,
            (false, true) => ipv6LoopbackExclusion,
            _ => $"((ip and {ipv4LoopbackExclusion}) or (ipv6 and {ipv6LoopbackExclusion}))",
        };
        sb.Append(" and ").Append(loopbackClause);

        // Tünel uç noktası dışlaması (De Morgan, `!=`):
        //   !(ip.DstAddr == X and udp.DstPort == P)
        //   ≡ ip.DstAddr != X or udp.DstPort != P
        // Alan adı, adresin ailesine göre seçilir — yanlış aile "hata 87" üretir
        // (probe ile doğrulandı: ipv4 literal vs ipv6 alanı reddedilir).
        if (excludedDstHost.IsNotEmpty() && excludedDstPort is > 0 and <= 65535
            && IPAddress.TryParse(excludedDstHost, out var parsed))
        {
            var addrField = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "ipv6.DstAddr"
                : "ip.DstAddr";
            sb.Append(" and (").Append(addrField).Append(" != ").Append(excludedDstHost)
              .Append(" or udp.DstPort != ").Append(excludedDstPort.Value).Append(')');
        }

        return sb.ToString();
    }
}