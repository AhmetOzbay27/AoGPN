using System.Reactive.Subjects;
using ServiceLib.Common;
using ServiceLib.Events;
using ServiceLib.Manager;
using ServiceLib.Services.Gpn;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnConnectionCoordinator — "Bağlan" akışının orkestratörü
//
// SelectBestServerAsync'in döndürdüğü ConnectionMode kararına göre bağlantıyı
// kurar:
//
//   WireGuardUDP  → seçilen İtalya/Almanya sunucusunun WireGuard tünelini başlat
//                   + failover izleyicisini başlat (sunucu değişimi / V2rayTCP düşüşü)
//   V2rayTCP      → mevcut V2ray düğümünü başlat (Tier 3 fallback)
//
// Koordinatör AppManager'a bağımlı DEĞİLDİR: seçim (IGpnServerSelectionService)
// ve çekirdek başlatma (IGpnConnectionLauncher) dışarıdan enjekte edilir —
// bu sayede birim testlerde her ikisi de sahte (fake) uygulamayla değiştirilir.
// ─────────────────────────────────────────────────────────────────────────

public enum GpnConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

/// <summary>Koordinatörün anlık durumu (UI/dashboard tüketebilir).</summary>
public sealed record GpnConnectionSnapshot(
    GpnConnectionState State,
    ConnectionMode Mode,
    GpnServerProfile? Server,
    string? ProfileSummary,
    DateTimeOffset UpdatedAt,
    string? Error = null)
{
    public static GpnConnectionSnapshot Idle() => new(
        GpnConnectionState.Disconnected, ConnectionMode.WireGuardUDP, null, null, DateTimeOffset.UtcNow);
}

/// <summary>
/// Çekirdek başlatma köprüsü (adapter): koordinatör ile gerçek çekirdek yaşam
/// döngüsü arasındaki soyutlama. Gerçek uygulama (GpnCoreLauncher)
/// CoreEngineHost + CoreConfigContextBuilder kullanır; testler sahte uygulama
/// enjekte eder.
/// </summary>
public interface IGpnConnectionLauncher
{
    /// <summary>
    /// Moda göre bağlantıyı kurar. WireGuardUDP'de <paramref name="server"/> zorunlu;
    /// V2rayTCP'de uygulamanın varsayılan V2ray düğümü kullanılır.
    /// <paramref name="gpnCandidates"/> verilirse (WireGuard bağlantısı) config
    /// tüm adayları + "GPN-Nodes" select grubuyla üretilir — böylece sonraki düğüm
    /// değişimleri çekirdek yeniden başlatılmadan yapılabilir.
    /// </summary>
    Task LaunchAsync(ConnectionMode mode, GpnServerProfile? server, CancellationToken ct,
        IReadOnlyList<GpnServerProfile>? gpnCandidates = null);

    /// <summary>Çalışan çekirdeği durdurur (idempotent).</summary>
    Task StopAsync(CancellationToken ct);
}

public interface IGpnConnectionCoordinator
{
    /// <summary>Anlık durum.</summary>
    GpnConnectionSnapshot Snapshot { get; }

    /// <summary>Durum değişiklikleri akışı (ReactiveUI/WebView2 dashboard'a bağlanabilir).</summary>
    IObservable<GpnConnectionSnapshot> Snapshots { get; }

    /// <summary>
    /// "Bağlan" girişi: akıllı seçim (ICMP → UDP → mod kararı) sonrası moda göre
    /// bağlantıyı kurar; WireGuard modunda failover izleyicisini başlatır.
    /// <paramref name="preferred"/> verilirse (kullanıcının seçtiği sunucu) önce
    /// yalnızca o sunucu denenir; başarısızsa otomatik ölçüme düşülür.
    /// </summary>
    Task<GpnConnectionSnapshot> ConnectAsync(
        IReadOnlyList<GpnServerProfile> candidates,
        GpnProbeOptions? options = null,
        GpnServerProfile? preferred = null,
        CancellationToken ct = default);

    /// <summary>Failover izleyicisini iptal eder ve bağlantıyı durdurur.</summary>
    Task DisconnectAsync(CancellationToken ct);

    /// <summary>
    /// Aktif WireGuard tünelini yerinde yeniden başlatır (WARP egress otomatik
    /// kurtarması). Yalnızca WireGuardUDP modunda ve bağlı/bağlanıyor durumdayken
    /// çalışır; aksi halde false döner.
    /// </summary>
    Task<bool> ReconnectCurrentTunnelAsync(CancellationToken ct);
}

public sealed class GpnConnectionCoordinator : IGpnConnectionCoordinator
{
    private const string Tag = "GpnConn";
    private readonly IGpnServerSelectionService _selector;
    private readonly IGpnConnectionLauncher _launcher;
    private readonly BehaviorSubject<GpnConnectionSnapshot> _snapshots;
    private readonly object _gate = new();
    private readonly Func<GpnServerProfile, CancellationToken, Task<bool>>? _softSwitchOverride;
    private CancellationTokenSource? _monitorCts;
    private CancellationTokenSource? _drainCts;
    private Task? _monitorTask;
    private bool _reconnectInFlight;
    private IReadOnlyList<GpnServerProfile> _candidates = [];

    public GpnConnectionCoordinator(
        IGpnServerSelectionService selector,
        IGpnConnectionLauncher launcher,
        Func<GpnServerProfile, CancellationToken, Task<bool>>? softSwitch = null)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _softSwitchOverride = softSwitch;
        _snapshots = new BehaviorSubject<GpnConnectionSnapshot>(GpnConnectionSnapshot.Idle());
    }

    public GpnConnectionSnapshot Snapshot => _snapshots.Value;

    public IObservable<GpnConnectionSnapshot> Snapshots => _snapshots.AsObservable();

    /// <summary>Testlerin izleyici görevini beklemesi için iç erişim.</summary>
    internal Task? ActiveMonitorTask => _monitorTask;

    public async Task<GpnConnectionSnapshot> ConnectAsync(
        IReadOnlyList<GpnServerProfile> candidates,
        GpnProbeOptions? options = null,
        GpnServerProfile? preferred = null,
        CancellationToken ct = default)
    {
        options ??= new GpnProbeOptions();
        _candidates = candidates;

        // Kesintisiz düğüm değişimi (make-before-break): bağlantı zaten kurulu bir
        // WireGuard tünelindeyken kullanıcı başka bir düğümle "Bağlan" derse tüneli
        // yıkıp yeniden kurmak yerine çalışan mihomo'nun GPN-Nodes grubunun seçimini
        // API ile değiştirmeyi dener. Başarılıysa mevcut bağlantılar eski düğümde
        // doğal olarak biter, yeniler anında yeni düğümden gider — kopma olmaz.
        // Başarısız olursa (çekirdek mihomo değil / config çoklu-düğüm desteklemiyor /
        // hedef sağlıksız) akış aşağıda eskisi gibi durdur→başlat'a düşer.
        if (preferred is not null
            && Snapshot.State is GpnConnectionState.Connected or GpnConnectionState.Connecting
            && Snapshot.Mode == ConnectionMode.WireGuardUDP
            && Snapshot.Server is { } currentServer
            && !string.Equals(currentServer.ServerId, preferred.ServerId, StringComparison.Ordinal)
            && await TargetSeemsHealthyAsync(preferred, options, ct).ConfigureAwait(false)
            && await SoftSwitchServerAsync(preferred, ct).ConfigureAwait(false))
        {
            SetState(GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, preferred,
                $"WireGuard → {preferred.Name} (kesintisiz geçiş)");
            DiagLog.Write($"GPN_LOG soft-switch → {preferred.ServerId}");
            Logging.SaveLog($"[{Tag}] Kesintisiz düğüm değişimi: {currentServer.Name} → {preferred.Name}");
            // Eski düğümde kalan oturumları izle (mevcut bağlantılar kesilmez, eski
            // düğümde boşalır) — dashboard "eski düğüm boşalıyor" durumunu gösterir.
            BeginDrain(currentServer, preferred);
            // Eski failover izleyicisi eski düğümü izliyor — iptal edip yenisiyle
            // değiştir (yeni düğüm izlenir, sağlıksızlaşırsa yine yumuşak geçilir).
            StopMonitor();
            if (options.EnableFailover)
            {
                StartFailoverMonitor(preferred, candidates, options, ct);
            }
            return Snapshot;
        }

        // Tam (restart'lı) bağlantı yolu eski tüneli kapatır — süregiden drenaj
        // gözlemi anlamsızlaşır, iptal et.
        CancelDrain();

        // Oturum denetim günlüğü: bağlantı sürecinin tamamı (seçim → mod → tünel →
        // el sıkışma → flush → failover → IP doğrulama) gpn-session.log'a yazılır;
        // VPN kopunca canlı iletişim kesilse bile log diske düşer, sonra okunur.
        GpnSessionLog.BeginSession($"GPN connect candidates={candidates.Count}");

        // Temiz başlangıç: eski bağlantı ve izleyici varsa durdur (idempotent).
        StopMonitor();
        await _launcher.StopAsync(ct).ConfigureAwait(false);

        DiagLog.Write($"GPN_LOG connect start candidates={candidates.Count}");
        SetState(GpnConnectionState.Connecting, ConnectionMode.WireGuardUDP, null, "sunucu ölçülüyor");

        try
        {
            // 1) Akıllı seçim: kullanıcı tercihi varsa önce o sunucu denenir (yalnızca
            //    kendisi ölçülür); başarısızsa paralel ICMP → UDP sağlık → mod kararı.
            if (preferred is not null)
            {
                Logging.SaveLog($"[{Tag}] Kullanıcı tercihi iletildi: {preferred.Name} ({preferred.ServerId}) — önce o denenir.");
                DiagLog.Write($"GPN_LOG preferred={preferred.ServerId}");
            }
            var selection = await _selector.SelectBestServerAsync(candidates, options, preferred, ct).ConfigureAwait(false);

            // 2) Mod kararına göre bağlantıyı kur
            if (selection.Mode == ConnectionMode.WireGuardUDP && selection.Best is not null)
            {
                var server = selection.Best;

                // Hairpin zorlaması: etkin TÜM sunucular kendi genel IP'sine (hairpin)
                // işaret ettiği için son çare olarak böyle bir sunucu seçildi. NAT hairpin
                // desteklemeyen yönlendiricide el sıkışma asla yanıt almaz — kullanıcıya
                // "neden bağlanmıyor" sorusunu panelde yanıtlayan açıklayıcı bir uyarı göster.
                if (selection.HairpinForced)
                {
                    NoticeManager.Instance.SendMessageEx(
                        $"{server.Name} kendi genel IP'niz (hairpin) olduğu için seçildi — bağlantı için " +
                        "yönlendiricinizin NAT hairpin (dönüş desteği) gerektirir; desteklemezse el sıkışma yanıt alamaz.");
                }

                SetState(GpnConnectionState.Connecting, ConnectionMode.WireGuardUDP, server, $"WireGuard → {server.Name}");
                // Adaylar iletildi → mihomo config'i tüm düğümler + GPN-Nodes grubuyla
                // üretilir; böylece daha sonraki düğüm değişimleri kesintisiz yapılabilir.
                await _launcher.LaunchAsync(ConnectionMode.WireGuardUDP, server, ct, _candidates).ConfigureAwait(false);

                SetState(GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, server, $"WireGuard → {server.Name}");
                DiagLog.Write($"GPN_LOG connected mode=WireGuardUDP server={server.ServerId}");
                if (options.EnableFailover)
                {
                    StartFailoverMonitor(server, candidates, options, ct);
                }
                else
                {
                    Logging.SaveLog($"[{Tag}] EnableFailover=false — failover izleyicisi başlatılmadı; "
                        + $"{server.Name} üstünde kalınıyor (stabil/Kesintisiz mod).");
                }
            }
            else
            {
                // UDP yolu doğrulanamadı → Tier 3: mevcut V2ray düğümü
                SetState(GpnConnectionState.Connecting, ConnectionMode.V2rayTCP, null, "V2ray TCP (fallback)");
                await _launcher.LaunchAsync(ConnectionMode.V2rayTCP, null, ct).ConfigureAwait(false);

                SetState(GpnConnectionState.Connected, ConnectionMode.V2rayTCP, null, "V2ray TCP (fallback)");
                DiagLog.Write("GPN_LOG connected mode=V2rayTCP (fallback)");
            }

            return Snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Bağlantı hatası: {ex.Message}");
            DiagLog.Write($"GPN_LOG failed: {ex.Message}");
            SetState(GpnConnectionState.Failed, Snapshot.Mode, Snapshot.Server, null, ex.Message);
            return Snapshot;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        StopMonitor();
        CancelDrain();
        // Tünel kapandı — bilinen tünel adını temizle; teşhis sezgisel eşleşmeye
        // (yabancı tüneller dahil) döner. Adaptör zaten Down olduğu için snapshot
        // zaten "tünel yok" der; temizlik sonraki bağlantılar için doğru başlangıç
        // sağlar.
        ProbeEgressNic.SetKnownTunnelNames(null);
        DiagLog.Write("GPN_LOG disconnect");
        GpnSessionLog.EndSession("GPN disconnect");
        try
        {
            await _launcher.StopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Durdurma hatası: {ex.Message}");
        }
        SetState(GpnConnectionState.Disconnected, Snapshot.Mode, null, null);
    }

    // ── Failover izleyici yönetimi ───────────────────────────────────────

    private void StartFailoverMonitor(
        GpnServerProfile active,
        IReadOnlyList<GpnServerProfile> candidates,
        GpnProbeOptions options,
        CancellationToken ct)
    {
        lock (_gate)
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        var token = _monitorCts.Token;

        // Failover izleyicisi bağlıyken çalışır — TUN etkinse probe'ları kendi
        // tünelinin içine yakalanmasın diye fiziksel NIC üzerinden ölçtür
        // (EscapeTunnelForProbes: UDP/TCP fiziksel NIC'e bağlanır, ICMP atlanır).
        // Seçim (SelectBestServerAsync) bu bayrağı ALMAZ — bağlantı-öncesi ölçüm
        // tünel yokken zaten fiziksel yoldan yapılır ve ICMP gösterimi korunur.
        var monitorOptions = options with { EscapeTunnelForProbes = true };

        // TUN'un etkin olduğunu koordinatör garantiler (çekirdek Ready = TUN kuruldu):
        // aktif tünel adaptörünün adını ProbeEgressNic teşhisine DOĞRUDAN ilet — ilk
        // failover ölçümü bile ad sezgisel eşleşmesine (IsTunLikeName) güvenmeden
        // tünel durumunu bilir ve probe'lar kendi tünelinin içine yakalanmaz.
        // (macOS utun{N} rastgele olduğu için sezgisel eşleşme orada devrededir.)
        ProbeEgressNic.SetKnownTunnelNames([Global.SingboxTunInterfaceName]);

        _monitorTask = Task.Run(() => _selector.RunFailoverMonitorAsync(
            active,
            candidates,
            onSwitch: (server, t) => SwitchServerAsync(server, t),
            onModeFallback: (_, t) => FallbackToV2rayAsync(t),
            onRecover: (server, t) => RecoverToWireGuardAsync(server, t),
            monitorOptions,
            cancellationToken: token), token);
    }

    private void StopMonitor()
    {
        lock (_gate)
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
        }
    }

    /// <summary>İzleyicinin onSwitch geri çağrısı: tüneli yeni sunucuya taşı.</summary>
    private async Task SwitchServerAsync(GpnServerProfile server, CancellationToken ct)
    {
        Logging.SaveLog($"[{Tag}] Sunucu değişimi: {server.Name}");
        DiagLog.Write($"GPN_LOG switch → {server.ServerId}");

        // Önce kesintisiz geçişi dene (çekirdek mihomo + çoklu-düğüm config ise tek
        // PUT /proxies — mevcut oturumlar eski düğümde boşalır, yeniler anında yeni
        // düğümde açılır). Çekirdek/config desteklemiyorsa eski durdur→başlat yoluna
        // düş (failover senaryosu zaten tüneli ölü saydığı için güvenlidir).
        var previousServer = Snapshot.Server;
        if (await SoftSwitchServerAsync(server, ct).ConfigureAwait(false))
        {
            SetState(GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, server,
                $"WireGuard → {server.Name} (kesintisiz geçiş)");
            DiagLog.Write($"GPN_LOG soft-switch → {server.ServerId}");
            // Eski düğümde kalan oturumlar boşalırken izlemeyi sürdür — kullanıcı
            // durumu dashboard'da görür (eski düğüm bağlantıları kesilmez).
            if (previousServer is not null)
            {
                BeginDrain(previousServer, server);
            }
            return;
        }

        SetState(GpnConnectionState.Connecting, ConnectionMode.WireGuardUDP, server, $"WireGuard → {server.Name}");
        await _launcher.StopAsync(ct).ConfigureAwait(false);
        await _launcher.LaunchAsync(ConnectionMode.WireGuardUDP, server, ct, _candidates).ConfigureAwait(false);
        SetState(GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, server, $"WireGuard → {server.Name}");
    }

    /// <summary>
    /// Yumuşak (restart'sız) düğüm geçişi: çalışan mihomo'nun GPN-Nodes grubunun
    /// seçimini API ile değiştirir. Testler enjekte edilmiş sahte uygulama verir;
    /// üretimde <see cref="GpnSoftSwitch.TrySwitchNodeAsync"/> kullanılır.
    /// </summary>
    private Task<bool> SoftSwitchServerAsync(GpnServerProfile target, CancellationToken ct)
        => (_softSwitchOverride ?? GpnSoftSwitch.TrySwitchNodeAsync)(target, ct);

    /// <summary>
    /// Yumuşak geçiş sonrası eski düğümün boşalma (drain) gözlemini başlatır:
    /// <see cref="GpnDrainWatcher"/> /connections zincirlerinden eski wg-&lt;id&gt;'ye
    /// bağlı kalan oturumları sayar ve <see cref="AppEvents.GpnDrainChanged"/> ile
    /// yayınlar (dashboard durum satırı). Fire-and-forget; yeni geçiş/bağlantı
    /// kesme iptal eder. Testlerde enjekte edilmiş sahte geçiş gerçek ağ içermez —
    /// orada izleyici başlatılmaz.
    /// </summary>
    private void BeginDrain(GpnServerProfile fromServer, GpnServerProfile toServer)
    {
        if (_softSwitchOverride is not null)
        {
            return;
        }

        CancellationToken token;
        lock (_gate)
        {
            _drainCts?.Cancel();
            _drainCts?.Dispose();
            var cts = new CancellationTokenSource();
            _drainCts = cts;
            token = cts.Token;
        }

        var from = fromServer;
        var to = toServer;
        _ = Task.Run(async () =>
        {
            try
            {
                var watcher = new GpnDrainWatcher();
                await watcher.RunAsync(
                    from,
                    to,
                    fetchConnections: () => ClashApiManager.Instance.GetClashConnectionsAsync(),
                    publish: snap => AppEvents.GpnDrainChanged.Publish(snap),
                    cancellationToken: token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Yeni geçiş/bağlantı kesme eski izleyiciyi iptal etti — sessizce bit.
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"[{Tag}] Drenaj izleyicisi hatası: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private void CancelDrain()
    {
        lock (_gate)
        {
            _drainCts?.Cancel();
            _drainCts?.Dispose();
            _drainCts = null;
        }
    }

    /// <summary>
    /// Kullanıcının seçtiği hedef düğüm, failover kapalıyken (stabil mod) körlemesine
    /// seçilirse ölü bir düğüme takılı kalınabilir — seçimden önce hedefin sağlığını
    /// hızlıca yokla. Failover açıksa izleyici zaten sürekli doğrular, yoklama atlanır.
    /// Yoklama sonucu boş gelirse (ölçülemeyen ortam) tercihe güvenilir.
    /// </summary>
    private async Task<bool> TargetSeemsHealthyAsync(
        GpnServerProfile target,
        GpnProbeOptions options,
        CancellationToken ct)
    {
        if (options.EnableFailover)
        {
            return true;
        }
        try
        {
            var probe = await _selector.ProbeAllAsync(
                [target],
                new GpnProbeOptions { EscapeTunnelForProbes = true },
                ct).ConfigureAwait(false);
            return probe.Count == 0 || probe.All(p => p.IsSuccess);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Hedef sağlık yoklaması başarısız ({target.Name}): {ex.Message}");
            return false;
        }
    }

    /// <summary>İzleyicinin onModeFallback geri çağrısı: tünel öldü, V2rayTCP'ye düş.</summary>
    private async Task FallbackToV2rayAsync(CancellationToken ct)
    {
        Logging.SaveLog($"[{Tag}] V2rayTCP düşüşü (tünel öldü)");
        SetState(GpnConnectionState.Connecting, ConnectionMode.V2rayTCP, null, "V2ray TCP (fallback)");
        await _launcher.StopAsync(ct).ConfigureAwait(false);
        await _launcher.LaunchAsync(ConnectionMode.V2rayTCP, null, ct).ConfigureAwait(false);
        SetState(GpnConnectionState.Connected, ConnectionMode.V2rayTCP, null, "V2ray TCP (fallback)");
    }

    /// <summary>
    /// İzleyicinin onRecover geri çağrısı: V2rayTCP düşüşü sonrası, periyodik UDP
    /// probe sağlıklı bir WireGuard sunucusu bulunca otomatik Tier-2'ye dön.
    /// </summary>
    private async Task RecoverToWireGuardAsync(GpnServerProfile server, CancellationToken ct)
    {
        Logging.SaveLog($"[{Tag}] Tier-2 kurtarma: WireGuard → {server.Name}");
        SetState(GpnConnectionState.Connecting, ConnectionMode.WireGuardUDP, server, $"WireGuard kurtarma → {server.Name}");
        await _launcher.StopAsync(ct).ConfigureAwait(false);
        await _launcher.LaunchAsync(ConnectionMode.WireGuardUDP, server, ct, _candidates).ConfigureAwait(false);
        SetState(GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, server, $"WireGuard → {server.Name}");
    }

    /// <summary>
    /// Aktif WireGuard tünelini yerinde yeniden başlatır (WARP egress yolunu
    /// onarmak için — WARP SOCKS5 dinleyicisi sunucuya yalnızca tünel üzerinden
    /// erişilebildiğinden tüneli yeniden kurmak yolu onarır). Yalnızca
    /// WireGuardUDP modunda ve bağlı/bağlanıyor durumdayken çalışır; V2rayTCP
    /// fallback'inde veya zaten bir yeniden başlatma sürerken false döner.
    /// Başarı: tünel Stop+Launch edilir ve durum Connected'a döner.
    /// </summary>
    public async Task<bool> ReconnectCurrentTunnelAsync(CancellationToken ct)
    {
        GpnServerProfile? server;
        lock (_gate)
        {
            if (_reconnectInFlight)
            {
                return false;
            }
            var snap = Snapshot;
            if (snap.Mode != ConnectionMode.WireGuardUDP
                || snap.State is not (GpnConnectionState.Connected or GpnConnectionState.Connecting)
                || snap.Server is null)
            {
                return false;
            }
            server = snap.Server;
            _reconnectInFlight = true;
        }

        try
        {
            Logging.SaveLog($"[{Tag}] Tünel yeniden başlatılıyor: {server.Name}");
            DiagLog.Write($"GPN_LOG reconnect → {server.ServerId}");
            SetState(GpnConnectionState.Connecting, ConnectionMode.WireGuardUDP, server, $"WireGuard yeniden başlatma → {server.Name}");
            await _launcher.StopAsync(ct).ConfigureAwait(false);
            await _launcher.LaunchAsync(ConnectionMode.WireGuardUDP, server, ct, _candidates).ConfigureAwait(false);
            SetState(GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, server, $"WireGuard → {server.Name}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Yeniden başlatma hatası: {ex.Message}");
            DiagLog.Write($"GPN_LOG reconnect failed: {ex.Message}");
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _reconnectInFlight = false;
            }
        }
    }

    private void SetState(GpnConnectionState state, ConnectionMode mode, GpnServerProfile? server, string? summary, string? error = null)
    {
        var snapshot = new GpnConnectionSnapshot(state, mode, server, summary, DateTimeOffset.UtcNow, error);
        _snapshots.OnNext(snapshot);
        // Ana dashboard bağlantı butonunu koordinatörle eşitler: Connected → buton
        // "Bağlantıyı Kes", Connecting/Disconnected → ara/kesik durum. 2 sn'lik
        // telemetri döngüsü beklenmez — GpnCoreLauncher çekirdeği başlatır başlatmaz
        // UI gerçek durumu görür.
        AppEvents.GpnConnectionStateChanged.Publish(snapshot);
        var serverName = server?.Name ?? "-";
        var errorPart = error is null ? string.Empty : $" | hata: {error}";
        Logging.SaveLog($"[{Tag}] {state} | mod={mode} | sunucu={serverName} | {summary}{errorPart}");
    }
}
