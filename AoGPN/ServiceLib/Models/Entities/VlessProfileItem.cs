namespace ServiceLib.Models.Entities;

/// <summary>
/// "Çift Bağlantı (Bölünmüş Tünelleme)" mimarisinde ana WireGuard düğümünün YANINA
/// eklenen opsiyonel ikincil proxy — "Launcher Bypass Düğümü" (VLESS/Reality).
///
/// Escape from Tarkov senaryosunda <c>BsGLauncher.exe</c> (kimlik doğrulama) bu
/// düğümden çıkar — BSG Cloudflare WAF'ının engellemediği, coğrafi olarak izin
/// verdiği temiz bir egress; oyun süreçleri (<c>EscapeFromTarkov.exe</c> +
/// BattlEye) ise düşük ping için ana WireGuard tünelinde kalır. Mihomo YAML
/// üreticisi (Services/CoreConfig/Mihomo'daki GpnMihomoConfigService) bu kaydı
/// <c>type: vless</c> + <c>reality-opts</c> proxy bloğuna çevirir.
///
/// Alanlar, Reality el sıkışması için gereken minimum seti taşır (UUID, sunucu
/// IP/port, public-key, short-id, servername/SNI). <see cref="Flow"/> ve
/// <see cref="Fingerprint"/> isteğe bağlıdır; varsayılanlar (xtls-rprx-vision /
/// chrome) yaygın Reality sunucu yapılandırmasıyla uyumludur.
/// </summary>
[Serializable]
public sealed record VlessProfileItem(
    string Name,            // Görünen ad ("Launcher Bypass" gibi)
    string ServerAddress,   // VLESS sunucu adresi (IP veya domain)
    int ServerPort,         // VLESS dinleme portu
    string Uuid,            // VLESS kullanıcı UUID'si
    string PublicKey,       // Reality public key (base64 "pbk")
    string ShortId = "",    // Reality short id; boşsa YAML'de anahtar yazılmaz
    string ServerName = "", // SNI / servername (Reality hedefi)
    string Flow = "xtls-rprx-vision",
    string Fingerprint = "chrome");
