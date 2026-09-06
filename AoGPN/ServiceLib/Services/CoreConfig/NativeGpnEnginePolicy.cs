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
/// SAHA GERİ ÇEKİLMESİ (2026-09-04, gece): Tier 1 flip canlı testlerde
/// kullanıcı akışını karşılamadı — native motor yalnızca hedef oyunların UDP
/// paketlerini yakalar (WinDivert NETWORK katmanı); "VPN rotalı" tarayıcıların
/// (Chrome/Edge) TCP trafiği TUN'a girmez ve doğrudan ISP üzerinden çıkar
/// (whatismyip/speedtest yerel IP gösterir — kullanıcı raporu). Eski çalışan
/// sürümün ("2.0 Final") doğrulanmış motoru mihomo TUN + PROCESS-NAME
/// kurallarıdır: beyaz listedeki uygulamanın TÜM trafiği (TCP dahil) tünele
/// girer. Bu nedenle varsayılan KAPALI'ya döndü — GPN WireGuard bağlantıları
/// yine mihomo TUN yolunu kullanır (byte-identical dormant geri dönüş;
/// testler tek tek pinler). Native motor entegrasyonu (strateji + köprü +
/// Tier 2/3/4) olduğu gibi korunur: sürücülü ortamda Wintun adaptörünün IP
/// yapılandırması + TCP taşıma eksikleri giderilip saha doğrulandıktan sonra
/// tek satırla yeniden açılabilir.
/// </summary>
public static class NativeGpnEnginePolicy
{
    /// <summary>
    /// True: GPN WireGuard bağlantıları yerel in-process motora yönlendirilir
    /// (yalnızca oyun UDP yakalama — tarayıcı TCP taşımaz).
    /// False (varsayılan — saha doğrulaması): mihomo TUN yolu aynen kalır;
    /// VPN rotalı uygulamaların TCP dahil tüm trafiği tünelden geçer.
    /// </summary>
    public static bool IsEnabled { get; set; } = false;
}