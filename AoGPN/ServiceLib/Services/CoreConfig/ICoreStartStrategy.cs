namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Ortak çekirdek süreç başlatma hattı (CoreManager.RunProcess'e bağlanır):
/// ikili çözümleme, başlatma öncesi config doğrulaması, yükseltme (sudo) ve
/// ProcessService başlatması. Çekirdekler arası ORTAK mekanizmadır — stratejiler
/// yalnızca karar parametrelerini (displayLog, isTunLaunch, ...) verir.
/// </summary>
public delegate Task<ProcessService?> CoreProcessLauncher(
    CoreInfo? coreInfo, string configFileName, bool displayLog, bool mayNeedSudo, bool isTunLaunch, Action? exitedCallback);

/// <summary>
/// Çekirdek başına başlatma/durdurma stratejisi (Strategy Pattern).
///
/// CoreManager'ın (P0: Başlatma Yöneticisinin Parçalanması) çekirdeğe özgü
/// kararları: TUN sahipliği (mihomo own-TUN), native-tunnel politikası
/// (openvpn — OS tüneli + yerel SOCKS5 yok) ve yaşam döngüsü yan etkileri
/// (mihomo WG /32 host rotası). Config üretimi CoreManager'da değil, zaten
/// çekirdek bazında dağıtılmış CoreConfigHandler katmanındadır
/// (CoreConfigSingboxService / CoreConfigV2rayService / GpnMihomoConfigService).
/// </summary>
public interface ICoreStartStrategy
{
    /// <summary>
    /// True: çekirdek kendi TUN adaptörünü + rotalarını yönetir (mihomo).
    /// Uygulamanın TunLifecycleManager'ı (Begin/Cleanup + ikinci-geçiş flush)
    /// bu çekirdek için ayrı çalışır.
    /// </summary>
    bool OwnsTun { get; }

    /// <summary>
    /// True: çekirdek OS tüneline sahiptir ve yerel SOCKS5 dinleyicisi yoktur
    /// (openvpn). Readiness doğrulaması yalnızca süreç bazında yapılır —
    /// SOCKS5 beklenmez.
    /// </summary>
    bool IsNativeTunnelCore { get; }

    /// <summary>
    /// True: çekirdek HARİCİ bir süreç DEĞİLDİR — uygulamanın İÇİNDE (in-process)
    /// çalışır (native GPN motoru: WinDivert + WireGuard + Wintun). CoreManager
    /// bu strateji için null ProcessService'i OLAĞAN sayar: "Core executable
    /// missing" hatası atılmaz, hazır olma doğrulaması süreç/SOCKS5 yoklaması
    /// yapmaz (StartAsync gerçek hatalarda fırlatır). Harici çekirdekler
    /// (mihomo/sing-box/Xray/openvpn/...) için kural eskisi gibi KATIDIR.
    /// </summary>
    bool IsInProcessEngine { get; }

    /// <summary>
    /// Başlatma öncesi çekirdeğe özgü yan etkiler (mihomo: WG sunucu IP'si için
    /// /32 host rotası — el sıkışma paketleri TUN rotasına asla dönmez).
    /// </summary>
    Task BeforeStartAsync(CoreConfigContext context);

    /// <summary>
    /// Durdurma sonrası çekirdeğe özgü yan etkiler (mihomo: host rotasını geri
    /// al). Idempotent olmalıdır — strateji aktif değilken çağrı no-op'tur.
    /// </summary>
    Task AfterStopAsync();

    /// <summary>
    /// Çekirdeği ortak başlatma hattı (<paramref name="launcher"/>) üzerinden
    /// başlatır. Strateji yalnızca çekirdeğe özgü kararları verir: ikili
    /// seçimi, displayLog ve isTunLaunch (yükseltme kararını besler).
    /// </summary>
    Task<ProcessService?> StartAsync(CoreConfigContext context, CoreProcessLauncher launcher, Action onExited);
}