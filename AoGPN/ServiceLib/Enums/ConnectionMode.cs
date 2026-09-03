namespace ServiceLib.Enums;

/// <summary>
/// GPN bağlantı modu (Tier seçimi). Akıllı Düşüş (Smart Fallback) karar
/// mekanizmasının çıktısıdır.
///
/// Tier 2 = WireGuardUDP — öncelikli mod: saf UDP iletimi, TCP meltdown yok.
/// Tier 3 = V2rayTCP  — yedek mod: hedef sunucunun UDP yolu engelli veya
///                       erişilemez olduğunda mevcut V2ray/TCP düğümlerine düşülür.
/// </summary>
public enum ConnectionMode
{
    /// <summary>Öncelikli: İtalya/Almanya WireGuard sunucusuna UDP tünel.</summary>
    WireGuardUDP = 2,

    /// <summary>Yedek: UDP yolu çalışmıyorsa V2ray TCP (REALITY/VLESS) üzerinden.</summary>
    V2rayTCP = 3,
}
