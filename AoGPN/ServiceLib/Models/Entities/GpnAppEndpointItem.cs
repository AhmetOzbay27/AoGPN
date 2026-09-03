namespace ServiceLib.Models.Entities;

/// <summary>
/// gpn_app_endpoints tablosu — GPN kullanımında tünellenen uygulamaların
/// GERÇEK sunucu uç noktaları (ör. oyun sunucusu IP:port).
///
/// Kaynak: uygulama GPN listesine eklendiğinde ve çalışıp genel adreslere
/// bağlandığında, bağlantı monitörünün gördüğü uzak adresler buraya yazılır
/// (SplitTunnelViewModel). Amaç "gerçek ping" ölçümüdür: program açılışında bu
/// uç noktalar doğrudan yoldan (önce), GPN bağlantısı kurulunca tünel yolundan
/// (sonra) ölçülür ve öncesi/sonrası karşılaştırması dashboard'da gösterilir.
///
/// (AppName, Endpoint) çifti benzersizdir (uq_gpn_app_endpoints indeksi) —
/// aynı sunucuya her oturumda yeniden bağlanmak yeni satır açmaz; HitCount
/// artar ve LastSeenAt tazelenir. Eski satırlar saklama süresi (varsayılan
/// 45 gün) dolunca GpnAppEndpointStore tarafından budanır.
/// </summary>
[Serializable]
[Table("gpn_app_endpoints")]
public class GpnAppEndpointItem
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Uygulama exe adı (ör. "EscapeFromTarkov.exe").</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Uzak uç nokta (ör. "1.2.3.4:51820", "1.2.3.4" veya "[v6]:443").</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Taşıma protokolü ("UDP" / "TCP") — ping ölçümünde TCP fallback'i için.</summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>İlk görülme zamanı (Unix milisaniye).</summary>
    public long FirstSeenAt { get; set; }

    /// <summary>Son görülme zamanı (Unix milisaniye) — budama ölçütü.</summary>
    public long LastSeenAt { get; set; }

    /// <summary>Kaç oturumda görüldüğü (ölçüm önceliği için — en sık kullanılan uç nokta önce ölçülür).</summary>
    public int HitCount { get; set; }
}
