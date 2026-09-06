using ServiceLib.Services;

namespace ServiceLib.Models.Entities;

/// <summary>
/// Per-app WARP egress düğümü: kullanıcının düğüm listesinden (ProfileItem)
/// seçtiği ve bir uygulama satırının "warp" rotasının çıkışı olarak kullanılacak
/// düğümün çözülmüş halidir. GpnCoreLauncher, politikadaki satırların WarpNodeId
/// değerlerini bağlantı anında ProfileItem'a çözer ve CoreConfigContext üzerinden
/// mihomo üreticisine taşır (üretici saf kalır — DB erişimi yoktur).
///
/// WireGuard profilleri <see cref="WireGuard"/> ile birlikte gelir (mihomo'da
/// ayrı bir wg-&lt;id&gt; outbound'u olarak yazılır); diğer protokoller (VLESS,
/// VMess, Shadowsocks, ...) yalnızca <see cref="Profile"/> taşır ve genel mihomo
/// çeviricisiyle kendi adında bir proxy outbound'una dönüştürülür.
/// </summary>
public sealed record WarpNodeProfile(
    string IndexId,
    string Name,
    ProfileItem? Profile,
    GpnServerProfile? WireGuard)
{
    public bool IsWireGuard => WireGuard is not null;

    /// <summary>Düğüm çözülebildi mi (profil bulundu ve desteklenen tipe dönüştü).</summary>
    public bool IsResolved => WireGuard is not null || Profile is not null;
}