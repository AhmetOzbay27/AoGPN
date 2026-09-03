using System.Text;

namespace ServiceLib.Services;

/// <summary>
/// WinDivert filtre dizesi üreticisi — yalnızca seçilen süreçlerin (oyun exe'si
/// ve alt süreçleri) giden UDP paketlerini yakalayan filtreyi kurar. Salt (pure):
/// ağ/çekirdek bağımlılığı yok, birim testleri doğrudan dizgeyi doğrular.
///
/// Örnek çıktı (tek PID):
///   outbound and udp and ip and (processId == 1234)
/// Çoklu PID, IPv4+IPv6 veya tünel uç noktası dışlama isteğe bağlı eklenir.
/// </summary>
public static class WinDivertFilterBuilder
{
    /// <summary>Yalnızca IPv4 — L2/L3 oyun trafiğinin çoğu buradadır.</summary>
    public const string Ipv4Term = "ip";

    /// <summary>IPv6</summary>
    public const string Ipv6Term = "ip6";

    /// <summary>Yakalanmayacak "kendi tünelimizin" UDP çifti (canlı döngü önlemek için).</summary>
    public static readonly string NoFilter = "false";

    /// <summary>
    /// Verilen PID kümesi için outbound-UDP filtresi kurar.
    /// </summary>
    /// <param name="pids">Yakalanacak süreç kimlikleri — en az 1 olmalı.</param>
    /// <param name="ipv4">IPv4 trafiğini dahil et (varsayılan true).</param>
    /// <param name="ipv6">IPv6 trafiğini dahil et (varsayılan false).</param>
    /// <param name="excludedDstHost">
    /// Filtrenin dışında bırakılacak hedef IP/port (ör. WireGuard sunucu uç noktası).
    /// Bu, tünelin kendi şifreli paketlerinin geri yakalanmasını engeller (canlı döngü).
    /// </param>
    /// <param name="excludedDstPort">excludedDstHost ile birlikte kullanılır.</param>
    public static string BuildFilter(
        IReadOnlyCollection<uint> pids,
        bool ipv4 = true,
        bool ipv6 = false,
        string? excludedDstHost = null,
        int? excludedDstPort = null)
    {
        if (pids.Count == 0)
        {
            throw new ArgumentException("En az bir PID gereklidir.", nameof(pids));
        }
        if (!ipv4 && !ipv6)
        {
            throw new ArgumentException("Hedef IP sürümü en az biri açık olmalı.", nameof(ipv4));
        }

        var network = ipv4 && ipv6 ? $"{Ipv4Term} or {Ipv6Term}" : ipv4 ? Ipv4Term : Ipv6Term;

        var pidClause = string.Join(" or ", pids.Select(p => $"processId == {p}"));

        var sb = new StringBuilder();
        sb.Append("outbound and udp and (").Append(network).Append(")");
        sb.Append(" and (").Append(pidClause).Append(')');

        // Enjeksiyon sonrası kendi paketlerimizin geri yakalanmaması için tünel
        // uç noktasına özel dışlama (filtre sözdizimi: "and !(...)").
        if (excludedDstHost.IsNotEmpty() && excludedDstPort is > 0 and <= 65535)
        {
            sb.Append(" and !( (ip.DstAddr == ").Append(excludedDstHost)
              .Append(" or ip6.DstAddr == ").Append(excludedDstHost).Append(')')
              .Append(" and udp.DstPort == ").Append(excludedDstPort.Value).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>Tek bir hedef süreç PID'i için hızlı aşırı yükleme.</summary>
    public static string BuildFilterForProcess(uint pid) => BuildFilter(new[] { pid });
}