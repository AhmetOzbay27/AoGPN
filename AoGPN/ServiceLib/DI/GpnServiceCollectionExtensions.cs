using Microsoft.Extensions.DependencyInjection;

namespace ServiceLib.DI;

/// <summary>
/// GPN Akıllı Düşüş (Smart Fallback) servislerinin DI kayıtları.
///
/// Uygulama şu an singleton tabanlı (Lazy&lt;T&gt;) kompozisyon kullanıyor; bu
/// uzantı, DI'ya geçildiğinde composition root'ta tek satırla bağlanmak üzere
/// hazırdır. Kayıtların gerçekten çalıştığı <c>ServiceLib.Tests</c> içindeki
/// DI çözümleme testiyle doğrulanır.
///
/// Kullanım (composition root):
/// <code>
/// var services = new ServiceCollection();
/// services.AddAoGpnGpnServices();
/// var provider = services.BuildServiceProvider();
/// var selector = provider.GetRequiredService&lt;IGpnServerSelectionService&gt;();
/// </code>
/// </summary>
public static class GpnServiceCollectionExtensions
{
    /// <summary>
    /// GPN seçim/fallback zincirini kaydeder. Tüm servisler singleton'dır:
    /// ölçüm durumu ve failover izleyicisi süreç boyunca tek örnek üzerinde çalışır.
    /// </summary>
    public static IServiceCollection AddAoGpnGpnServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // UdpHealthChecker — hedef sunucunun 51820/udp yolunu test eder
        // (Tier seçiminin kalbi). Interface üzerinden kaydedilir ki testler
        // sahte (fake) uygulama enjekte edebilsin.
        services.AddSingleton<IUdpHealthChecker, UdpHealthChecker>();

        // WireGuardHandshakeProbe — gerçek Noise_IKpsk2 el sıkışmasıyla kesin UDP
        // kanıtı (SelectBestServer'in Tier kararının güçlendirilmiş sürümü).
        services.AddSingleton<IWireGuardHandshakeProbe, WireGuardHandshakeProbe>();

        // GpnServerProber — ağ test motoru (ICMP/TCP/UDP + el sıkışma). P0 Faz 2
        // katmanlamasında ölçüm GpnServerSelectionService'ten ayrıldı; singleton
        // olarak paylaşılır (seçim + failover + dashboard aynı örneği kullanır).
        services.AddSingleton<GpnServerProber>();

        // GpnHairpinDetector — hairpin (kendi genel IP'si) teşhisi; own-IP önbelleği
        // bu singleton üzerinde yaşar (GpnServerSelectionService ile paylaşılır).
        services.AddSingleton<GpnHairpinDetector>();

        // GpnServerSelectionService — orkestratör: paralel ICMP + UDP sağlık testi
        // (GpnServerProber), hairpin teşhisi (GpnHairpinDetector) ve saf kararlar
        // (GpnDecision) üzerinden ConnectionMode kararı (WireGuardUDP / V2rayTCP
        // fallback). Ctor internal'dır (alt servis tipleri internal) — DI bu yüzden
        // factory ile kurar; DI dışı (new) kullanımda checker'lar ctor opsiyonelleriyle
        // verilir (örn. testler sahte IUdpHealthChecker enjekte eder).
        services.AddSingleton<IGpnServerSelectionService>(sp => new GpnServerSelectionService(
            udpHealthChecker: sp.GetRequiredService<IUdpHealthChecker>(),
            wireGuardProbe: sp.GetRequiredService<IWireGuardHandshakeProbe>(),
            ownPublicIpProvider: null,
            prober: sp.GetRequiredService<GpnServerProber>(),
            hairpinDetector: sp.GetRequiredService<GpnHairpinDetector>()));

        // Mevcut paralel ping koordinatörü — uygulamanın hız testi altyapısıyla
        // aynı örnek üzerinde paylaşılır (uygulama DI'ya geçtiğinde).
        services.AddSingleton<NodePingCoordinator>();

        // GpnCaptureBridge — yakalama tünel köprüsü: GpnCaptureLoop'un varsayılan
        // tüketimini WireGuardTunnelService.CreateInjectHandler ile değiştirir ve
        // WireGuard bağlantısında canlıya alır. Hedef ad delegesi composition
        // root'ta verilir; DI'da boş kalır (uygulama doğrudan MainWindowViewModel
        // üzerinden SplitTunnelViewModel uygulamalarıyla kurar).
        services.AddSingleton(sp => new GpnCaptureBridge(
            () => Array.Empty<string>(),
            sp.GetRequiredService<WinDivertEngine>(),
            sp.GetRequiredService<WireGuardTunnelService>(),
            sp.GetRequiredService<IGpnCaptureSettingsProvider>()));

        // GpnCoreLauncher — mod kararını gerçek çekirdek yaşam döngüsüne köprüler
        // (Config/CoreEngineHost'u AppManager'dan alır; parametreleri opsiyoneldir).
        // WireGuard modunda yakalama tünel köprüsünü de canlıya alır.
        services.AddSingleton<IGpnConnectionLauncher>(sp => new GpnCoreLauncher(
            captureBridge: sp.GetRequiredService<GpnCaptureBridge>()));

        // GpnConnectionCoordinator — "Bağlan" orkestratörü: seçim → mod kararı →
        // bağlantı + failover izleyici. Bağımlılıkları (selector + launcher) yukarıdan
        // otomatik çözülür.
        services.AddSingleton<IGpnConnectionCoordinator, GpnConnectionCoordinator>();

        // GpnTelemetryService — failover/kurtarma olaylarını sayaçlayan telemetri
        // servisi (AppEvents.GpnResilienceChanged akışını dinler).
        services.AddSingleton<GpnTelemetryService>();

        // GpnBypassEgressController — WARP faulted iken launcher egress'ini canlı
        // (restart'sız) DIRECT'e çeken degrade denetleyicisi (AppEvents.WarpDialHealthChanged
        // + GpnConnectionStateChanged dinler). Uygulama şu an MainWindow'da doğrudan
        // kurar; DI'ya geçildiğinde singleton olarak buradan bağlanır (işaret süreç
        // boyunca tek örnekte yaşar — dashboard rozeti Degraded bayrağını okur).
        services.AddSingleton<GpnBypassEgressController>();

        // GpnResilienceLog — son 50 GpnResilience kararını tutan döngüsel tampon;
        // dosyaya yazarak sorun giderme penceresi sunar.
        services.AddSingleton<GpnResilienceLog>();

        // P0 Faz 3 — Split-tunnel alt servisleri: oyun otomatik tetikleyici (durum
        // makinesi + süreç taraması) ve canlı telemetri izdüşümü. SplitTunnelViewModel
        // bunları şu an doğrudan kurar (Lazy/manual kompozisyon); DI'ya geçildiğinde
        // singleton olarak buradan bağlanırlar (durum süreç boyunca tek örnekte yaşar).
        services.AddSingleton<GameAutoTriggerService>();
        services.AddSingleton<GpnTelemetryMonitorService>();

        // P0 Nihai Faz — bağlantı + trafik veri motorları (DashboardConnectionEngine /
        // DashboardTrafficEngine): OS bağlantı tablosu + /connections trafiği ve
        // istatistik olayı → Mbps/kayıp/ping matematiği. ViewModel kabukları şu an
        // doğrudan kurar (Lazy/manual kompozisyon); DI'ya geçildiğinde buradan bağlanır.
        services.AddSingleton<DashboardConnectionEngine>();
        services.AddSingleton<DashboardTrafficEngine>();

        // CoreManager strateji kayıtları (P0: Başlatma Yöneticisinin Parçalanması):
        // çekirdek başına başlatma politikası (mihomo own-TUN + WG host rotası,
        // sing-box/Xray standart, openvpn native-tunnel varsayılanı). CoreManager
        // şu an Lazy singleton kompozisyonla CoreStartStrategyFactory üzerinden
        // çözer; DI'ya geçildiğinde buradan bağlanırlar (stateless — örnek paylaşımı
        // güvenlidir). Varsayılan strateji DI'da standart politikayla çözülür;
        // openvpn eşlemesi için CoreStartStrategyFactory kullanılmalıdır.
        services.AddSingleton<MihomoStartStrategy>(_ => MihomoStartStrategy.Instance);
        services.AddSingleton<SingboxStartStrategy>(_ => SingboxStartStrategy.Instance);
        services.AddSingleton<XrayStartStrategy>(_ => XrayStartStrategy.Instance);
        services.AddSingleton<DefaultCoreStartStrategy>(_ => DefaultCoreStartStrategy.Instance);

        // P0 — Native GPN Motoru: yerel (in-process) WinDivert + WireGuard + Wintun
        // motorunun strateji soketi. Köprüsüz dormant örnek — StartAsync ancak
        // köprü delege çiftiyle kurulmuş bir örnekte motoru canlıya alır
        // (NativeGpnStartStrategy ctor; Tier 1 flip'i GpnCoreLauncher'ın
        // captureBridge deseninin aynısıyla bağlanır). Fabrika bu örneği yalnızca
        // UseNativeGpnEngine bayrağı kuran WireGuard bağlamlarında seçer.
        services.AddSingleton<NativeGpnStartStrategy>(_ => NativeGpnStartStrategy.Instance);

        // GpnCaptureSettingsProvider — WinDivertOpenParams yapılandırmasını
        // (queue len/time/size + katman/yön) kullanıcı config'inden (GpnCaptureItem)
        // üretir. Ayarlar GUI'de düzenlenince Config.SaveConfig sonrası tazelenir.
        services.AddSingleton<IGpnCaptureSettingsProvider, GpnCaptureSettingsProvider>();

        // GpnCaptureOptions — ayarlardan türetilmiş yakalama seçenekleri (OpenParams +
        // FlagQueue* bitleri). GpnCaptureLoop her bağlantıda bu options'ı DI'dan çözer
        // ve OpenEx tabanlı sniff → recv-only akışını ayarlarla açar.
        // TRANSIENT: provider'ın CaptureOptions özelliği her erişimde canlı config'den
        // yeniden türetir (AppManager.Instance.Config.GpnCaptureItem). Singleton yapılırsa
        // ilk çözümden sonra kullanıcının set_gpn_capture_settings patch'i asla yeni açılışa
        // yansımaz — kuyruk değişiklikleri bir sonraki bağlantıda kaybolur.
        services.AddTransient(sp => sp.GetRequiredService<IGpnCaptureSettingsProvider>().CaptureOptions);

        // WinDivertEngine — OpenEx tabanlı (sniff/recv-only) yakalama motoru.
        // GpnTargetResolver/GpnCaptureLoop ise bağlantı anında PID hedefiyle kurulur
        // (seçili oyun exe'sine göre); options'ları DI'dan alırlar.
        services.AddSingleton<WinDivertEngine>();

        // Faz 2b/2c — WireGuard tünel köprüsü: DivertWorker kanalı → şifreleme →
        // Wintun adaptörü. DI varsayılanı Noop'tur; köprü (GpnCaptureBridge)
        // BAĞLANTI ANINDA profilin anahtarlarıyla gerçek WireGuard veri düzlemini
        // (WireGuardNoiseTransport — el sıkışma + oturum anahtarı + ChaCha20-Poly1305)
        // kurup UseTransport ile tünele bağlar. Arayüz değişmez — köprü/telemetri/test
        // altyapısı gerçek şifrelemeyle de aynı kalır.
        services.AddSingleton<IWireGuardTransport, NoopWireGuardTransport>();
        services.AddSingleton<WireGuardTunnelService>();

        return services;
    }
}
