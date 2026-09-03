namespace ServiceLib.Models.Entities;

/// <summary>
/// gpn_servers tablosu — GPN WireGuard sunucu kümesi (Faz 3).
///
/// İstemci özel anahtarı bu tabloda DÜZ METİN saklanmaz: <see cref="ClientPrivateKeyEnc"/>
/// sütunu, Windows'ta DPAPI (crypt32 <c>CryptProtectData</c> / Geçerli Kullanıcı) ile
/// şifrelenmiş baytların base64 halini taşır ve yalnızca bağlantı anında çözülür.
/// Windows dışı platformlarda (debug/masaüstü GUI) şifreleme etkin değildir; bu sütun
/// o durumda düz metin base64 olarak saklanır (Logging: "DPAPI unavailable").
///
/// <see cref="ServerId"/> uç noktadan türetilmiş kararlı bir kimliktir ("host:port"),
/// böylece aynı .conf yeniden içe aktarıldığında yeni satır oluşmaz (upsert olur) ve
/// failover/otomatik seçim aday kimlikleri içe aktarımlar arasında sabit kalır.
/// </summary>
[Serializable]
public class GpnServerItem
{
    [PrimaryKey]
    public string ServerId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string EndpointHost { get; set; } = string.Empty;

    public int EndpointPort { get; set; }

    public string ServerPublicKey { get; set; } = string.Empty;

    /// <summary>DPAPI ile şifrelenmiş istemci özel anahtarı (base64). Windows dışında düz base64.</summary>
    public string ClientPrivateKeyEnc { get; set; } = string.Empty;

    public string ClientAddress { get; set; } = "10.66.66.2/24";

    public int Mtu { get; set; } = 1420;

    public string Dns { get; set; } = "1.1.1.1";

    public int Keepalive { get; set; } = 25;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Bu satırın türetildiği ProfileItem.IndexId (kaynak takibi).</summary>
    public string? SourceProfileId { get; set; }

    /// <summary>Son güncelleme zamanı (Unix milisaniye).</summary>
    public long UpdatedAt { get; set; }
}