using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnJsonlConsumer — GpnProbeTool --jsonl çıktısının tüketici çekirdeği
//
// GpnProbeTool her olayı tek satır JSON (NDJSON) olarak basar; bu sınıf o
// akışı iki amaçla tüketir:
//   1. Zaman damgalı JSONL kaydı: her `server_probe` satırı ayrıştırılır,
//      `ingestedAt` (tüketici zamanı) eklenir ve arşivlenir (log dosyası).
//   2. Prometheus metrikleri: son ölçümler sunucu başına saklanır ve
//      `gpn_ping_ms` / `gpn_udp_status` metrikleriyle exposition üretilir
//      (textfile collector veya HTTP /metrics ucu).
//
// Ayrıştırma hataları asla fırlatmaz — bozuk/ilgisiz satırlar atlanır.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Bir `server_probe` JSONL satırının tüketilmiş hali — orijinal alanlara ek
/// olarak tüketici tarafından eklenen <see cref="IngestedAt"/> zaman damgasını taşır.
/// </summary>
public sealed record GpnServerProbeLog(
    string ServerId,
    string? Name,
    string? Host,
    int Port,
    int IcmpMs,
    int IcmpAvgMs,
    int IcmpLossPct,
    string? Udp,
    string? UdpDetail,
    string? Handshake,
    DateTimeOffset Timestamp,
    DateTimeOffset IngestedAt);

/// <summary>Tek sunucunun son ölçümü (Prometheus durum haritası değeri).</summary>
public sealed record GpnProbeMetric(
    string ServerId,
    string? Name,
    int PingMs,
    string? UdpStatus,
    string? Handshake,
    DateTimeOffset Timestamp);

/// <summary>
/// Sunucu başına SON ölçümü tutan thread-safe durum; Prometheus metin biçiminde
/// exposition üretir. Yalnızca <c>server_probe</c> olayları metrikleri besler.
/// </summary>
public sealed class GpnPrometheusProbeState
{
    /// <summary>UDP durumlarının sabit sırası — one-hot satırlar kararlı kalsın diye.</summary>
    private static readonly string[] UdpStatuses = ["Open", "Blocked", "NoResponse", "HandshakeNoResponse"];

    private readonly object _lock = new();
    private readonly Dictionary<string, GpnProbeMetric> _latest = new();

    /// <summary>Sunucunun son ölçümünü günceller (aynı sunucu için son satır kazanır).</summary>
    public void Update(GpnServerProbeLog log)
    {
        lock (_lock)
        {
            _latest[log.ServerId] = new GpnProbeMetric(
                log.ServerId, log.Name, log.IcmpMs, log.Udp, log.Handshake, log.Timestamp);
        }
    }

    /// <summary>Son ölçümlerin anlık kopyası (ölçüm sırası korunmaz).</summary>
    public IReadOnlyList<GpnProbeMetric> Snapshot()
    {
        lock (_lock)
        {
            return _latest.Values.ToArray();
        }
    }

    /// <summary>
    /// Prometheus metin biçiminde exposition (text/plain; version=0.0.4):
    /// <list type="bullet">
    ///   <item><c>gpn_ping_ms{server,name}</c> — en iyi ICMP gidiş-dönüş (ms; -1 = ölçüm başarısız).</item>
    ///   <item><c>gpn_udp_status{server,name,status}</c> — one-hot: gözlenen durum 1, diğerleri 0.</item>
    /// </list>
    /// </summary>
    public string BuildExposition()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# HELP gpn_ping_ms GPN server ICMP round-trip latency in milliseconds (best sample; -1 = probe failed).");
        sb.AppendLine("# TYPE gpn_ping_ms gauge");
        sb.AppendLine("# HELP gpn_udp_status GPN server UDP probe status (one-hot: 1 for the observed status, 0 otherwise).");
        sb.AppendLine("# TYPE gpn_udp_status gauge");

        foreach (var metric in Snapshot())
        {
            var server = EscapeLabel(metric.ServerId);
            var name = EscapeLabel(metric.Name ?? "");
            sb.AppendLine($"gpn_ping_ms{{server=\"{server}\",name=\"{name}\"}} {metric.PingMs}");
            foreach (var status in UdpStatuses)
            {
                var one = string.Equals(status, metric.UdpStatus, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                sb.AppendLine($"gpn_udp_status{{server=\"{server}\",name=\"{name}\",status=\"{status}\"}} {one}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Prometheus etiket değeri kaçışı: ters bölü, tırnak, satır sonu.</summary>
    internal static string EscapeLabel(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}

/// <summary>GpnProbeTool --jsonl akışının saf ayrıştırıcı/zenginleştirici yardımcıları.</summary>
public static class GpnJsonlConsumer
{
    /// <summary>
    /// Bir satırı <c>server_probe</c> olayı olarak ayrıştırır. Olay değilse veya
    /// ayrıştırılamıyorsa null (sessiz atlama). <paramref name="ingestedAt"/> kaynak
    /// satırda <c>timestamp</c> yoksa ölçüm zamanı olarak da kullanılır.
    /// </summary>
    public static GpnServerProbeLog? TryParseServerProbe(string line, DateTimeOffset ingestedAt)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryGetString(root, "event", out var evt) || evt != "server_probe")
            {
                return null;
            }

            if (!TryGetString(root, "serverId", out var serverId) || string.IsNullOrEmpty(serverId))
            {
                return null;
            }

            var timestamp = TryGetDateTime(root, "timestamp") ?? ingestedAt;
            return new GpnServerProbeLog(
                serverId,
                GetStringOrNull(root, "name"),
                GetStringOrNull(root, "host"),
                GetInt(root, "port", 51820),
                GetInt(root, "icmpMs", -1),
                GetInt(root, "icmpAvgMs", -1),
                GetInt(root, "icmpLossPct", 100),
                GetStringOrNull(root, "udp"),
                GetStringOrNull(root, "udpDetail"),
                GetStringOrNull(root, "handshake"),
                timestamp,
                ingestedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Herhangi bir JSON nesnesi satırına <c>ingestedAt</c> alanı ekler (zaman damgalı
    /// JSONL arşivi — yalnızca server_probe değil, selection/failover/monitor_event
    /// satırları da loglanır). JSON değilse veya nesne değilse null.
    /// </summary>
    public static string? EnrichLine(string line, DateTimeOffset ingestedAt)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(line);
            if (node is not JsonObject obj)
            {
                return null;
            }

            obj["ingestedAt"] = ingestedAt;
            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetString(JsonElement el, string name, out string value)
    {
        value = string.Empty;
        return el.TryGetProperty(name, out var prop)
               && prop.ValueKind == JsonValueKind.String
               && (value = prop.GetString()!) is not null;
    }

    private static string? GetStringOrNull(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static int GetInt(JsonElement el, string name, int fallback)
    {
        if (el.TryGetProperty(name, out var prop)
            && prop.ValueKind is JsonValueKind.Number or JsonValueKind.String
            && int.TryParse(prop.ToString(), out var value))
        {
            return value;
        }

        return fallback;
    }

    private static DateTimeOffset? TryGetDateTime(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(prop.GetString(), out var value))
        {
            return value;
        }

        return null;
    }
}
