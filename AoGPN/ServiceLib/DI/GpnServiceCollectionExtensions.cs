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

        // GpnServerSelectionService — paralel ICMP + UDP sağlık testi +
        // ConnectionMode kararı (WireGuardUDP / V2rayTCP fallback).
        // UdpHealthChecker'ı ctor'dan alır; DI zincir otomatik kurar.
        services.AddSingleton<IGpnServerSelectionService, GpnServerSelectionService>();

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

        // GpnResilienceLog — son 50 GpnResilience kararını tutan döngüsel tampon;
        // dosyaya yazarak sorun giderme penceresi sunar.
        services.AddSingleton<GpnResilienceLog>();

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
