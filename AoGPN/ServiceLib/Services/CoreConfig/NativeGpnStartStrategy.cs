namespace ServiceLib.Services.CoreConfig;

// ─────────────────────────────────────────────────────────────────────────
// NativeGpnStartStrategy — YEREL (in-process) native GPN motoru başlatma
// stratejisi (Tier 1 — Native Motoru Canlıya Alma).
//
// ServiceLib/Services/Gpn altındaki WinDivert yakalama + WireGuard veri
// düzlemi + Wintun adaptörü altyapısını CoreStartStrategyFactory'nin strateji
// soketine bağlar. Diğer stratejilerden FARKI: harici bir çekirdek .exe
// başlatmaz — CoreProcessLauncher KULLANILMAZ, süreç uygulamanın kendisidir.
// StartAsync, köprüyü (GpnCaptureBridge — GpnCoreLauncher'ın da sahibi
// olduğu kompozisyon) WireGuard sunucu profiliyle canlıya alır; AfterStopAsync
// WinDivert filtresini + yakalama kuyruklarını + Wintun oturumunu temiz
// kapatır (GpnCaptureBridge.StopAsync — idempotent).
//
// YAŞAM DÖNGÜSÜ KÖPRÜSÜ (in-process = process-yok): harici süreç olmadığı
// için Exited event'i doğal tetiklenmez. Köprü, oturum SIRASINDA beklenmedik
// hata verdiğinde (yakalama/alım görevi fault) GpnCaptureBridge.EngineFailed
// olayını fırlatır; strateji (StartAsync'te abone olur, AfterStopAsync'te
// bırakır) bunu CoreManager'ın onExited delegesine köprüler → mevcut çökme
// kurtarma döngüsü (Degraded → yeniden başlatma politikası) engine için de
// işler. BİLİNÇLİ duruş (AfterStopAsync) onExited'i TETİKLEMEZ.
//
// AKTİVASYON: CoreManager bu stratejiyi yalnızca CoreConfigContext.
// UseNativeGpnEngine == true VE düğüm EConfigType.WireGuard olduğunda seçer;
// bayrağı GpnCoreLauncher, NativeGpnEnginePolicy.IsEnabled anahtarıyla kurar
// (varsayılan KAPALI — mihomo yolu canlı doğrulanmıştır, sürücülü ortamda
// doğrulandıktan sonra tek satırla açılır). Köprü ctor'da verilebilir veya
// AppManager.Instance.CaptureBridge'ten çözülür (kompozisyon kökü koyar).
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Yerel GPN motoru başlatma stratejisi: düğümü GpnServerProfile'a çevirip
/// köprüyü (WinDivert → WireGuard → Wintun) canlıya alır; duruşta temiz
/// kapanış yapar; oturum-içi engine hatalarını CoreManager kurtarma döngüsüne
/// (onExited) köprüler. OwnsTun=true (motor kendi Wintun adaptörünü yönetir),
/// IsNativeTunnelCore=true (yerel SOCKS5 dinleyicisi yoktur),
/// IsInProcessEngine=true (harici süreç yoktur — null process olağandır).
/// </summary>
internal sealed class NativeGpnStartStrategy : ICoreStartStrategy
{
    private static readonly Lazy<NativeGpnStartStrategy> _instance = new(() => new());

    private readonly GpnCaptureBridge? _bridge;

    /// <summary>Köprüsüz varsayılan örnek — köprü AppManager'dan çözülür (dormant).</summary>
    public static NativeGpnStartStrategy Instance => _instance.Value;

    private GpnCaptureBridge? _sessionBridge;
    private Action? _currentOnExited;
    private bool _engineFailedHandled;

    /// <summary>Test süzgeci: oturum-içi fault köprüsü (EngineFailed aboneliği + onExited bağı) takılı mı?</summary>
    internal bool IsEngineFailureHookAttached => _sessionBridge is not null && _currentOnExited is not null;

    /// <param name="captureBridge">
    /// Yakalama tünel köprüsü (GpnCoreLauncher'ın captureBridge parametresiyle
    /// aynı desen). Verilmezse StartAsync AppManager.Instance.CaptureBridge'ten
    /// çözer — kompozisyon kökü (MainWindowViewModel) köprüyü oraya koyar.
    /// </param>
    public NativeGpnStartStrategy(GpnCaptureBridge? captureBridge = null)
    {
        _bridge = captureBridge;
    }

    /// <inheritdoc/>
    /// Motor kendi Wintun adaptörünü + rotalarını yönetir (mihomo gibi) —
    /// uygulamanın TunLifecycleManager'ı bu motor için ayrı çalışmalıdır.
    public bool OwnsTun => true;

    /// <inheritdoc/>
    /// Yerel SOCKS5 dinleyicisi yoktur — tünel süreçsizdir (in-process).
    public bool IsNativeTunnelCore => true;

    /// <inheritdoc/>
    public bool IsInProcessEngine => true;

    /// <inheritdoc/>
    public Task BeforeStartAsync(CoreConfigContext context) => Task.CompletedTask;

    /// <inheritdoc/>
    /// Temiz kapanış: WinDivert filtresi + yakalama kuyrukları + Wintun oturumu
    /// (GpnCaptureBridge.StopAsync — ağda sızıntı bırakmaz). BİLİNÇLİ duruş:
    /// onExited bağı ve EngineFailed aboneliği KOPARILIR — kurtarma tetiklenmez.
    /// Idempotent: oturum yoksa no-op'tur.
    public Task AfterStopAsync()
    {
        var bridge = _sessionBridge;
        _sessionBridge = null;
        _currentOnExited = null;
        _engineFailedHandled = false;
        if (bridge is not null)
        {
            bridge.EngineFailed -= OnEngineFailed;
            // Tier 3 — dinamik otonomi yalnızca native oturumda: kapanışta köprü
            // Ready-idle denetçisini bırakır (StopAsync denetçiyi de iptal eder).
            bridge.DynamicReArmEnabled = false;
        }
        return bridge?.StopAsync() ?? Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// HARİCİ bir çekirdek süreci başlatmaz — launcher ve onExited burada
    /// süreç başlatmak için kullanılmaz; motor uygulamanın içinde (aynı
    /// süreçte) çalışır ve DÖNEN null ProcessService OLAĞANDIR (CoreManager
    /// IsInProcessEngine için null'u hata saymaz). Gerçek hatalar (köprü
    /// bağlanmamış, geçersiz düğüm) fırlatılır — sessiz no-op YOKTUR.
    public async Task<ProcessService?> StartAsync(CoreConfigContext context, CoreProcessLauncher launcher, Action onExited)
    {
        // Yerel motor yalnızca WireGuard düğümleriyle anlamlıdır — fabrika aynı
        // kapıyı zaten kontrol eder; yine de savunmacı doğrulama (doğrudan çağrı).
        if (context.Node.ConfigType != EConfigType.WireGuard)
        {
            DiagLog.Write($"GPN_NATIVE native motor yalnızca WireGuard düğümleri içindir (ConfigType={context.Node.ConfigType}) — başlatılmıyor.");
            return null;
        }

        var bridge = _bridge ?? AppManager.Instance.CaptureBridge;
        if (bridge is null)
        {
            // Sessiz "Connected" tuzağı olmasın: motor istendi ama köprü yok —
            // YÜKSEK SESLE hata (koordinatör bağlantıyı başarısız sayar).
            throw new InvalidOperationException(
                "GPN native motor köprüsü bağlanmamış — AppManager.Instance.CaptureBridge kompozisyon kökünde kurulmalı (NativeGpnEnginePolicy.IsEnabled açıkken).");
        }

        // Oturum bağı: engine hatalarını CoreManager'ın onExited'ine köprüle
        // (çökme kurtarma döngüsü — Degraded → yeniden başlatma politikası).
        _sessionBridge = bridge;
        _currentOnExited = onExited;
        _engineFailedHandled = false;
        // Tier 3 — Dinamik yeniden kurma: köprü Ready-idle'dayken hedef oyunun
        // başlamasını bekler ve kendiliğinden canlanır (yalnızca native oturumda;
        // harici çekirdekler köprüyü hiç kullanmaz). AfterStopAsync kapatır.
        bridge.DynamicReArmEnabled = true;
        bridge.EngineFailed += OnEngineFailed;
        try
        {
            // Düğüm (BuildWireGuardProfile çıktısı) → köprünün beklediği sunucu
            // profili. Tek kaynak CoreConfigHandler.ToGpnServerProfile'tur —
            // mihomo üreticisiyle aynı çeviri, sürüklenme yok.
            var server = CoreConfigHandler.ToGpnServerProfile(context.Node);
            var started = await bridge.StartAsync(server, CancellationToken.None).ConfigureAwait(false);
            // Tier 2 — sessiz, kontrollü Ready-idle: köprü hedef oyun olmadığı için
            // atladıysa (LastIdleCause) bu bir hata değildir — oturum Ready kalır,
            // oyun beklenir. Köprü artık gürültülü uyarı basmaz; burada TEK sessiz
            // işaret yazılır (nedenle birlikte). Gerçek başlatma hatası (cause=None)
            // köprünün onFailure bildirimiyle zaten kullanıcıya gitmiştir.
            if (started)
            {
                DiagLog.Write($"GPN_NATIVE engine live server={server.Name} ({server.EndpointHost}:{server.EndpointPort})");
            }
            else if (bridge.LastIdleCause != GpnBridgeIdleCause.None)
            {
                DiagLog.Write($"GPN_NATIVE engine ready-idle server={server.Name} cause={bridge.LastIdleCause} (oturum Ready — hedef oyun bekleniyor)");
            }
            else
            {
                DiagLog.Write($"GPN_NATIVE engine start-skipped server={server.Name} ({server.EndpointHost}:{server.EndpointPort}) — köprü onFailure raporuna bakın");
            }
            return null;
        }
        catch
        {
            // Başlatma hatası → oturum temizliği, sonra yukarı taşı (koordinatör görür).
            bridge.EngineFailed -= OnEngineFailed;
            bridge.DynamicReArmEnabled = false;
            _sessionBridge = null;
            _currentOnExited = null;
            throw;
        }
    }

    /// <summary>
    /// Oturum-içi engine hatası bildirimi (köprü EngineFailed olayından gelir).
    /// onExited bağını bir kez kaldırır (best-effort) — CoreManager'ın
    /// OnMainProcessExited(generation) mekanizması eskimiş nesli zaten eler.
    /// </summary>
    private async Task OnEngineFailed(string reason)
    {
        DiagLog.Write($"GPN_NATIVE engine fault: {reason}");
        if (_engineFailedHandled)
        {
            return;
        }
        _engineFailedHandled = true;
        var onExited = _currentOnExited;
        if (onExited is null)
        {
            return;
        }
        try
        {
            onExited();
        }
        catch
        {
            // best-effort: bildirim hatası kurtarma akışını bozmasın
        }
        await Task.CompletedTask;
    }
}