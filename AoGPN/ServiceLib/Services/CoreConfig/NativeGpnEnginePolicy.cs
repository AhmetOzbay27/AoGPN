namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Tier 1 — The Live Flip: native motor (WinDivert + WireGuard + Wintun)
/// aktivasyon politikası.
///
/// GpnCoreLauncher, WireGuard bağlantısında bu anahtar AÇIKKEN
/// CoreConfigContext.UseNativeGpnEngine kurar → CoreManager (strateji fabrikası)
/// yerel motor stratejisini (NativeGpnStartStrategy) seçer: harici mihomo süreci
/// yerine uygulama-içi in-process motor (WinDivert + WireGuard + Wintun).
///
/// TIER 1 FLIP (2026-09-04): varsayılan AÇIK — native motor birinci sınıf
/// vatandaştır. Anahtar KAPALIYSA mevcut mihomo yolu birebir aynen kalır
/// (dormant geri dönüş; testler tek tek pinler). Gerçek saha doğrulaması
/// sürücülü (WinDivert .sys + Wintun) bir ortamda, uygulama Yönetici olarak
/// çalıştırılarak yapılır — dotnet testi sürücüsüz koşar ve native veri
/// düzlemini doğrulayamaz.
/// </summary>
public static class NativeGpnEnginePolicy
{
    /// <summary>
    /// True (varsayılan — Tier 1 flip): GPN WireGuard bağlantıları yerel
    /// in-process motora yönlendirilir.
    /// False: mihomo yolu aynen kalır (dormant geri dönüş).
    /// </summary>
    public static bool IsEnabled { get; set; } = true;
}