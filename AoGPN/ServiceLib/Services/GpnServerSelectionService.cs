namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GPN Server Selection Service — iskelet (Faz 4: Otomasyon + Ping Modülü)
//
// Kullanıcı "Bağlan" dediğinde İtalya ve Almanya WireGuard sunucularını
// paralel ölçer, en düşük gecikmeli sunucuyu otomatik seçer ve bağlıyken
// degrade olan bağlantıyı daha iyi adaya devreder (failover).
//
// Tasarım notları:
//  * ICMP ölçümü System.Net.NetworkInformation.Ping ile yapılır; ISP
//    ICMP'yi engelliyorsa TCP connect sondasına düşülür (GpnProbeMode.Auto).
//  * Paralellik mevcut NodePingCoordinator üzerinden sağlanır — uygulamada
//    zaten var olan hız testi altyapısı yeniden kullanılır.
//  * Bu sınıf AppManager'a bağımlı DEĞİLDİR: sunucu listesi dışarıdan
//    verilir (Faz 3'teki WireGuardServerCatalog'tan), böylece unit-test
//    edilebilir kalır.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// WireGuard sunucu profili. Faz 3'te bu kayıt, uygulamanın SQLite veri
/// tabanındaki ProfileItem satırlarından (EConfigType.WireGuard) doldurulur;
/// private anahtar diske DPAPI ile şifrelenerek yazılır.
/// </summary>
public sealed record GpnServerProfile(
    string ServerId,          // "it" (İtalya), "de" (Almanya)
    string Name,              // Görünen ad, örn. "İtalya"
    string EndpointHost,      // 92.4.220.236 / 130.61.223.36
    int EndpointPort,         // 51820
    string ServerPublicKey,   // wg0 sunucu genel anahtarı (base64)
    string ClientPrivateKey,  // istemci özel anahtarı (base64)
    string ClientAddress,     // 10.66.66.2/24
    int Mtu = 1420,
    string Dns = "1.1.1.1",
    int PersistentKeepalive = 25,
    bool IsEnabled = true)
{
    /// <summary>WireGuard .conf formatı (WireGuardTunnel.dll / wg-quick için).</summary>
    public string ToConf() => string.Join('\n',
        "[Interface]",
        $"# Name = {Name}",
        $"Address = {ClientAddress}",
        $"PrivateKey = {ClientPrivateKey}",
        $"MTU = {Mtu}",
        $"DNS = {Dns}",
        "",
        "[Peer]",
        $"PublicKey = {ServerPublicKey}",
        $"Endpoint = {EndpointHost}:{EndpointPort}",
        "AllowedIPs = 0.0.0.0/0, ::/0",
        $"PersistentKeepalive = {PersistentKeepalive}");
}

/// <summary>Tek sunucu için ölçüm özeti.</summary>
public sealed record GpnServerProbeResult(
    string ServerId,
    int DelayMs,          // en iyi (min) gidiş-dönüş süresi; -1 = başarısız
    int AvgDelayMs,
    int MaxDelayMs,
    int LossPercent,      // 0-100
    bool IsSuccess,
    Exception? Error = null,
    /// <summary>
    /// Ölçüm TUN etkinken fiziksel NIC üzerinden yapıldı mı (ICMP atlandı; gecikme
    /// UDP el sıkışma RTT'sinden). Dashboard, tünel-içi ölçümle karıştırılmaması
    /// için bu bayrağı kullanıcıya ipucu olarak gösterir.
    /// </summary>
    bool MeasuredOverPhysicalNic = false);

/// <summary>Ölçüm modu: ICMP engelliyse TCP connect'e düş.</summary>
public enum GpnProbeMode
{
    Auto,   // önce ICMP, başarısızsa TCP
    Icmp,
    Tcp,
}

/// <summary>Probe ayarları. Varsayılanlar oyun senaryosuna göre yeterli.</summary>
public sealed record GpnProbeOptions(
    int Samples = 4,               // sunucu başına örnek sayısı
    int PerSampleTimeoutMs = 1000, // tek örnek zaman aşımı
    int MaxConcurrency = 8,
    GpnProbeMode Mode = GpnProbeMode.Auto,
    int SwitchHysteresisMs = 15)   // failover'da salınımı önleyen marj
{
    /// <summary>Seçilen sunucudaki UDP sağlık testi ayarları (Smart Fallback).</summary>
    public UdpHealthCheckOptions UdpCheck { get; init; } = new();

    /// <summary>Failover izleyicisinde ping yanında periyodik UDP sağlık testi çalıştır.</summary>
    public bool EnableUdpHealth { get; init; } = true;

    /// <summary>
    /// Otomatik sunucu değiştirmesini (failover) aç/kapat. FALSE olduğunda failover
    /// izleyicisi başlatılmaz: seçilen sunucuya bağlandıktan sonra sunucu değişimi,
    /// V2rayTCP düşüşü ve Tier-2 kurtarma TÜMÜ devre dışı kalır — bağlantı seçili
    /// sunucuya takılı kalır (en stabil/kesintisiz). Varsayılan TRUE programatik
    /// davranışı korur; GPN kullanıcı ayarı (GpnEnableFailover=false) bu değeri
    /// kapatarak stabil modu açar.
    /// </summary>
    public bool EnableFailover { get; init; } = true;

    /// <summary>
    /// V2rayTCP düşüşü sonrası WireGuard'ın geri kurtarılmasını izle: Top Tier-2
    /// dönüşü için adayları periyodik UDP ile probe et, sağlıklı sunucu bulununca
    /// <c>onRecover</c> çağır. false ise V2rayTCP düşüşü terminaldir (eski davranış).
    /// </summary>
    public bool EnableRecoveryWatch { get; init; } = true;

    /// <summary>
    /// Gerçek WireGuard el sıkışmasıyla (handshake initiation) kesin UDP kanıtı kullan.
    /// Anahtarlar (GpnServerProfile.ClientPrivateKey/ServerPublicKey) mevcutsa
    /// 1 baytlık junk probe'a tercih edilir.
    /// </summary>
    public bool UseWireGuardHandshakeProbe { get; init; } = true;

    /// <summary>Handshake probe ayarları (UseWireGuardHandshakeProbe=true iken).</summary>
    public WireGuardHandshakeProbeOptions HandshakeProbe { get; init; } = new();

    /// <summary>
    /// Yüksek-gecikme toleransı (ms): bir sunucunun ölçülen ping'i bu değere EŞİT/ÜST
    /// ise, o sunucunun <c>HandshakeNoResponse</c> sonucu "ölü" sayılmaz. Kısa probe
    /// penceresinde uzak/yoğun bir WireGuard sunucusunun gidiş-dönüş süresi yanlış
    /// 'tünel öldü' algısı yaratıp failover/kopanmayı tetiklemesin (canlı gözlenen
    /// "arada bir bağlantı kaçıyor" kaynaklarından biri). 0 = tolerans kapalı (eski
    /// davranış).
    /// </summary>
    public int SlowServerToleranceMs { get; init; } = 400;

    /// <summary>
    /// Failover ping-pong/soğutma süresi (saniye): bir sunucu değişiminden SONRA bu
    /// süre dolmadan, AZ ÖNCE TERK EDİLEN sunucuya GERİ dönülmez. TUN (auto_route +
    /// strict_route) altında failover'ın UDP sağlık probe'ları fiziksel NIC'e bağlı
    /// olmadığı için kendi tünelinin içine yakalanıp yanıltıcı "HandshakeNoResponse"
    /// üretebilir; bu da iki sunucu arasında ~30 sn'de bir gidip-gelme (her geçişte
    /// sing-box restart + mevcut TCP kesimi = "bağlantı kendi kendini koparıyor")
    /// yaratır — canlı gözlenen sorun. Bu süre sıçramayı kırar: probe bir kez yanılırsa
    /// tünel yerinde kalır, bir sonraki çevrimde doğrulanınca geçiş yapılır. 0 = koruma
    /// kapalı (eski davranış).
    /// </summary>
    public int FailoverSwitchCooldownSeconds { get; init; } = 45;

    /// <summary>
    /// Probe ölçümlerini tünelden kurtar: TUN (auto_route + strict_route) etkinken
    /// aday sunuculara atılan ICMP/TCP/UDP probe'ları kendi tünelinin İÇİNE yakalanır
    /// ve yanıltıcı sonuç üretir (failover'ın UDP el sıkışma probe'ları → sahte
    /// "HandshakeNoResponse" → gereksiz sunucu değişimi/kopma). TRUE iken ve bir tünel
    /// algılandığında:
    ///   * UDP/TCP probe soketleri fiziksel uplink NIC'ine bağlanır (ProbeEgressNic),
    ///   * ICMP atlanır — System.Net.NetworkInformation.Ping arayüz bağlayamaz ve
    ///     tünel-içi ping anlamsızdır; gecikme yerine fiziksel NIC üzerinden UDP el
    ///     sıkışma round-trip'i ölçülür (UdpDelayMsAsync — handshake yanıtı RTT
    ///     verir; yanıt yoksa -1), karar UDP sağlık kanıtına kalır.
    /// Failover izleyicisi bu bayrağı koordinatör üzerinden açar; bağlantı-öncesi
    /// seçim (SelectBestServerAsync) ve dashboard ölçümü (ProbeAllAsync) kapalı
    /// bırakır — tünel yokken zaten fiziksel yoldan ölçülür, ICMP gösterimi korunur.
    /// </summary>
    public bool EscapeTunnelForProbes { get; init; } = false;
}

/// <summary>
/// Seçim sonucu: en iyi aday + bağlantı modu kararı + UDP sağlık testi + ölçümler.
/// <c>Best</c> null ise hiçbir WireGuard sunucusu seçilemedi demektir; bu durumda
/// <c>Mode</c> V2rayTCP'dir ve uygulama mevcut V2ray düğümlerine düşer.
/// </summary>
public sealed record GpnSelectionResult(
    GpnServerProfile? Best,
    ConnectionMode Mode,
    UdpProbeResult? UdpProbe,
    IReadOnlyList<GpnServerProbeResult> Results,
    /// <summary>
    /// Seçilen <see cref="Best"/> sunucusu, kendi genel IP'sine işaret eden (hairpin)
    /// bir sunucu OLDUĞU HALDE seçilmek ZORUNDA KALINDI (etkin tüm sunucular hairpin
    /// olduğu için son çare). NAT hairpin desteklemeyen yönlendiricide el sıkışma asla
    /// yanıt almaz — koordinatör bunu kullanıcıya açıklayıcı bir uyarı olarak gösterir.
    /// </summary>
    bool HairpinForced = false);

/// <summary>
/// Önceden tahmin edilen otomatik seçim kararı (dashboard "en iyi aday" kartı).
/// <see cref="DecideSelection"/> ile hesaplanır — <see cref="SelectBestServerAsync"/>
/// ile BİREBİR aynı mantık; dashboard "Bağlan"a basmadan hangi sunucunun/modun
/// seçileceğini gösterir. <c>Best</c> null ise hiçbir WireGuard sunucusu seçilemez
/// → V2rayTCP düşüşü öngörülür.
/// </summary>
public sealed record GpnSelectionPrediction(
    GpnServerProfile? Best,
    ConnectionMode Mode,
    UdpProbeResult? UdpProbe,
    string Reason);

public interface IGpnServerSelectionService
{
    /// <summary>
    /// "Bağlan" girişi: kullanıcı belirli bir sunucu seçtiyse (<paramref name="preferred"/>
    /// — dashboard node seçiciden gelir) önce YALNIZCA onu ölçer ve sağlıklıysa doğrudan
    /// seçer; seçili sunucu WireGuardUDP için uygun değilse Akıllı Düşüş tüm adayları
    /// ölçerek devreye girer. <paramref name="preferred"/> null ise davranış eskisi gibi
    /// tam otomatik ölçümdür.
    /// </summary>
    Task<GpnSelectionResult> SelectBestServerAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions? options = null,
        GpnServerProfile? preferred = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GpnServerProbeResult>> ProbeAllAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aynı ölçümü varsayılan + katı politika altında değerlendiren failover karar
    /// matrisi (dashboard görselleştirmesi — saf, ağ yok).
    /// </summary>
    GpnFailoverMatrix EvaluateFailoverMatrix(
        GpnServerProfile active,
        IReadOnlyList<GpnServerProfile> candidates,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null);

    /// <summary>
    /// GPN Bağlan'ın yapacağı otomatik seçim kararını önceden gösterir (dashboard
    /// "en iyi aday" kartı — saf, ağ yok). <see cref="SelectBestServerAsync"/> ile
    /// birebir aynı mantık: ping adayı varsa yalnızca onun UDP'si karar verir, ping
    /// yoksa ilk UDP-açık sunucu seçilir, o da yoksa V2rayTCP.
    /// </summary>
    GpnSelectionPrediction EvaluateSelection(
        IReadOnlyList<GpnServerProfile> servers,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null,
        IReadOnlySet<string>? hairpinServerIds = null);

    /// <summary>
    /// Bağlı WireGuard tünelini izle; ölünce otomatik sunucu değişimi/failover yap.
    /// V2rayTCP'ye düşüldükten sonra <paramref name="onRecover"/> verildiyse ve
    /// <see cref="GpnProbeOptions.EnableRecoveryWatch"/> açıksa, adayları periyodik
    /// UDP ile probe edip sağlıklı sunucu bulununca otomatik Tier-2 (WireGuard)
    /// kurtarması tetiklenir (<c>onRecover(targetServer, ct)</c> çağrılır).
    /// </summary>
    Task RunFailoverMonitorAsync(
        GpnServerProfile active,
        IReadOnlyList<GpnServerProfile> candidates,
        Func<GpnServerProfile, CancellationToken, Task> onSwitch,
        Func<ConnectionMode, CancellationToken, Task>? onModeFallback = null,
        Func<GpnServerProfile, CancellationToken, Task>? onRecover = null,
        GpnProbeOptions? options = null,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default);
}

public sealed class GpnServerSelectionService : IGpnServerSelectionService
{
    private const string Tag = "GpnSelect";
    private const int OwnIpCacheSeconds = 60;
    private readonly IUdpHealthChecker _udpHealthChecker;
    private readonly IWireGuardHandshakeProbe _wireGuardProbe;
    private readonly Func<string?>? _ownPublicIpProvider;

    // Kendi genel IP'si için kısa zaman aşımlı HTTP çözücü; seçim ağını engellememesi
    // için 60 sn önbelleklenir (object-memcache benzeri). Bağlan akışında bir kez yavas
    // net HTTP çağrısı yeterli — her seçimde hafif tutulur.
    private static readonly HttpClient _ownIpHttp = new() { Timeout = TimeSpan.FromSeconds(3) };
    private string? _cachedOwnIp;
    private DateTime _ownIpUtc;

    // Hairpin kimlikleri önbelleği: aynı aday kümesiyle tekrar çağrıldığında (ör. her
    // failover izleyicisi başlangıcı) kendi-IP TTL'si (OwnIpCacheSeconds) içinde seti
    // YENİDEN hesaplamaz — HTTP çözümü VE candidate taraması önlenir. Sıfır küme de
    // saklanır ("hairpin yok" sonucu da TTL boyunca sabittir). Aday kimliği değişirse
    // (farklı sunucu listesi) anahtar eşleşmediği için otomatik yenilenir.
    private string? _cachedHairpinKey;
    private HashSet<string>? _cachedHairpinIds;
    private DateTime _cachedHairpinUtc;

    public GpnServerSelectionService(
        IUdpHealthChecker? udpHealthChecker = null,
        IWireGuardHandshakeProbe? wireGuardProbe = null,
        Func<string?>? ownPublicIpProvider = null)
    {
        // DI uyumlu: checker'lar dışarıdan enjekte edilir; testlerde sahte (fake)
        // uygulamalar verilebilir. Belirtilmezse gerçek uygulamalar kullanılır.
        // ownPublicIpProvider hairpin teşhisi için makinenin kendi genel IP'sini
        // verir — testte sabit döner, üretimde önbellekli HTTP çözücü kullanılır.
        _udpHealthChecker = udpHealthChecker ?? new UdpHealthChecker();
        _wireGuardProbe = wireGuardProbe ?? new WireGuardHandshakeProbe();
        _ownPublicIpProvider = ownPublicIpProvider;
    }

    /// <summary>
    /// Hairpin (öz-erişim): hedef sunucunun genel IP'si makinenin KENDİ genel IP'siyle
    /// eşleşiyorsa, istemci tünelin içinden kendi sunucusuna erişmeye çalışıyor demektir
    /// (tünel-içi hairpin NAT). Böyle bir sunucuya el sıkışma genellikle yanıt almaz;
    /// bu teşhis bağlanmaya çalışmadan önce uyarır ve sunucuyu aday sırasının sonuna atar.
    /// </summary>
    internal static bool IsHairpin(GpnServerProfile server, string? ownPublicIp)
    {
        if (string.IsNullOrWhiteSpace(ownPublicIp)
            || !IPAddress.TryParse(server.EndpointHost, out var endpoint)
            || !IPAddress.TryParse(ownPublicIp.Trim(), out var own))
        {
            return false;
        }
        return endpoint.Equals(own);
    }

    /// <summary>
    /// Aday sıralaması (saf — test edilebilir): hairpin adaylar EN SONA (tünel-içi
    /// öz-erişim el sıkışma almaz), ardından başarılı pingler önce (düşük gecikme),
    /// başarısız pingler sonra. <see cref="SelectBestServerAsync"/> ve dashboard
    /// <see cref="DecideSelection"/> bu fonksiyonu BİREBİR kullanır — tahmin ile
    /// gerçek seçim hairpin durumunda da çelişemez.
    /// </summary>
    internal static IEnumerable<GpnServerProfile> OrderCandidates(
        IEnumerable<GpnServerProfile> servers,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlySet<string> hairpinIds)
        => servers
            .OrderBy(s => hairpinIds.Contains(s.ServerId) ? 1 : 0)
            .ThenByDescending(s => pingResults.First(r => r.ServerId == s.ServerId).IsSuccess)
            .ThenBy(s => pingResults.First(r => r.ServerId == s.ServerId).DelayMs);

    /// <summary>
    /// Etkin bir NON-hairpin sunucu varken hairpin (kendi genel IP'si, öz-erişim)
    /// adaylar seçimden TAMAMEN elenmelidir. Hairpin sunucunun UDP ölçümü yanıltıcı
    /// şekilde "Open" görünebilir (tünel-içi rota) ama NAT hairpin desteklenmeyen
    /// yönlendiricilerde el sıkışma asla yanıt almaz — canlı gözlendi (makine İtalya
    /// hairpin'ine failover'da geçti, el sıkışma öldü, yeniden churn). Yalnızca TÜM
    /// etkin sunucular hairpin ise son çare olarak hairpin yine de seçilebilir.
    /// </summary>
    private static bool HasNonHairpinAlternative(
        IReadOnlyList<GpnServerProfile> enabledServers,
        IReadOnlySet<string> hairpinIds)
        => hairpinIds.Count > 0 && enabledServers.Any(s => !hairpinIds.Contains(s.ServerId));

    /// <summary>
    /// "Bağlan" akışının girişi — Akıllı Düşüş (Smart Fallback) karar mekanizması:
    ///
    /// 1. İtalya ve Almanya'ya paralel ICMP ping atılır.
    /// 2. Tüm adayların 51820/udp yolu paralel test edilir (el sıkışma + junk).
    /// 3. Ping sırasına göre İLK WireGuardUDP-uygun aday seçilir (Akıllı Düşüş):
    ///    - en düşük ping'li adayın UDP'si ölüyse (bloklu / handshake-no-response)
    ///      ikinci aday denenir — yalnızca HİÇBİR aday uygun değilse V2rayTCP (Tier 3).
    ///
    /// ICMP tamamen engelliyse (bazı ISP'ler) ping adayı çıkmaz; bu durumda ilk
    /// UDP-açık (kesin kanıt) sunucu seçilir.
    /// </summary>
    public async Task<GpnSelectionResult> SelectBestServerAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions? options = null,
        GpnServerProfile? preferred = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GpnProbeOptions();
        var enabled = servers.Where(s => s.IsEnabled).ToArray();
        if (enabled.Length == 0)
        {
            Logging.SaveLog($"[{Tag}] SelectBestServer: aktif sunucu yok.");
            PublishResilience(new GpnResilienceEvent(
                GpnResilienceAction.ModeDecision, ConnectionMode.V2rayTCP,
                reason: "aktif sunucu yok", fromMode: ConnectionMode.WireGuardUDP));
            return new GpnSelectionResult(null, ConnectionMode.V2rayTCP, null, Array.Empty<GpnServerProbeResult>());
        }

        // ── Kullanıcı seçimi önceliği (preferred server) ───────────────────
        // Kullanıcı dashboard node seçiciden belirli bir sunucu seçtiyse, otomatik
        // ölçüm yerine önce YALNIZCA o sunucu denenir: UDP yolu sağlıklıysa (Akıllı
        // Düşüş'ün ölçütüyle) doğrudan seçilir. Yalnızca seçili sunucu uygun değilse
        // (bloklu / el sıkışma yanıtsız) tüm adaylar ölçülerek Akıllı Düşüş devreye
        // girer. Seçili sunucu hairpin ise ve non-hairpin alternatif varsa atlanır.
        if (preferred is not null)
        {
            var prefEnabled = enabled.FirstOrDefault(s =>
                string.Equals(s.ServerId, preferred.ServerId, StringComparison.OrdinalIgnoreCase));
            if (prefEnabled is null)
            {
                Logging.SaveLog($"[{Tag}] Tercih edilen sunucu {preferred.Name} etkin adaylarda değil — Akıllı Düşüş kullanılıyor.");
            }
            else
            {
                var prefOwnIp = GuardOwnIpArtifact(
                    await ResolveOwnPublicIpAsync(cancellationToken).ConfigureAwait(false),
                    enabled);
                var prefHairpinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (IsHairpin(prefEnabled, prefOwnIp))
                {
                    prefHairpinIds.Add(prefEnabled.ServerId);
                }
                if (prefHairpinIds.Count > 0 && HasNonHairpinAlternative(enabled, prefHairpinIds))
                {
                    Logging.SaveLog($"[{Tag}] Tercih edilen {prefEnabled.Name} hairpin (kendi genel IP'si) — non-hairpin varken atlanıyor, Akıllı Düşüş.");
                }
                else
                {
                    var prefSelection = await TrySelectPreferredAsync(prefEnabled, options, cancellationToken)
                        .ConfigureAwait(false);
                    if (prefSelection is not null)
                    {
                        return prefSelection;
                    }
                    Logging.SaveLog($"[{Tag}] Seçili sunucu {prefEnabled.Name} WireGuardUDP için uygun değil — Akıllı Düşüş tüm adayları ölçüyor.");
                }
            }
        }

        // Hairpin teşhisi: kendi genel IP'sini ölçümlerle PARALEL çöz (önbellekli HTTP —
        // için el sıkışma/ICMP süresini bloklamaz). Sonuç aday sıralamasından önce beklenir.
        var ownIpTask = ResolveOwnPublicIpAsync(cancellationToken);

        // ── Adım 1: Paralel ICMP ping (mevcut NodePingCoordinator altyapısı) ──
        var requests = enabled
            .Select(s => new NodePingRequest(s.ServerId, token => ProbeDelayMsAsync(s, options, token)))
            .ToArray();
        var timeout = TimeSpan.FromMilliseconds(options.Samples * options.PerSampleTimeoutMs + 500);
        var coordinator = new NodePingCoordinator(options.MaxConcurrency);
        var pingResults = await coordinator.RunAsync(requests, timeout, cancellationToken: cancellationToken);

        var results = new List<GpnServerProbeResult>(enabled.Length);
        foreach (var s in enabled)
        {
            var hit = pingResults.FirstOrDefault(r => r.Id == s.ServerId);
            results.Add(new GpnServerProbeResult(
                s.ServerId,
                hit?.Delay ?? -1,
                hit?.Delay ?? -1,
                hit?.Delay ?? -1,
                hit is { IsSuccess: true } ? 0 : 100,
                hit is { IsSuccess: true }));
        }

        var bestPing = results.Where(r => r.IsSuccess).OrderBy(r => r.DelayMs).FirstOrDefault();

        // ── Adım 2: UDP sağlık testi — tüm adaylarda paralel (Akıllı Düşüş) ──
        //
        // BUGFIX (canlı doğrulama, 29 Ağu 2026): eski akış yalnızca EN DÜŞÜK ping'li
        // TEK adayı test ediyordu; o adayın UDP yolu ölüyse (bloklu / handshake-no-
        // response — ör. tünel içinden kendi sunucusuna hairpin) ikinci en iyi sunucu
        // HİÇ denenmeden doğrudan V2rayTCP'ye düşüyordu. Gerçekte Almanya'nın el
        // sıkışması AÇIKKEN İtalya'nınki başarısızdı ve seçici V2rayTCP'ye düştü.
        // Artık tüm adayların UDP yolu ölçülür ve ping sırasına göre İLK
        // WireGuardUDP-uygun aday seçilir.
        var udpAll = await ProbeUdpAllAsync(enabled, options, cancellationToken);
        var udpByServer = udpAll.ToDictionary(r => r.ServerId);

        GpnServerProfile? candidate = null;
        UdpProbeResult? udpProbe = null;

        // Hairpin (öz-erişim) teşhisi — ölçümle paralel çözülen kendi genel IP'si:
        // hedef sunucunun genel IP'si makinenin KENDİ genel IP'siyle eşleşiyorsa (tünel
        // içinden kendi sunucusuna hairpin NAT), o sunucu aday sırasının SONUNA atılır ve
        // diğer adaylar önceliklendirilir. Düşük ping'i yanıltıcıdır (tünel-içi rota).
        var ownIp = GuardOwnIpArtifact(await ownIpTask.ConfigureAwait(false), enabled);
        var hairpinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in enabled)
        {
            if (!IsHairpin(s, ownIp))
            {
                continue;
            }
            hairpinIds.Add(s.ServerId);
            Logging.SaveLog($"[{Tag}] HAIRPIN: {s.Name} endpoint {s.EndpointHost} == kendi genel IP ({ownIp}) — öz-erişim; seçilmez.");
            DiagLog.Write($"GPN_SELECT hairpin server={s.ServerId} endpoint={s.EndpointHost} ownIp={ownIp}");
        }

        // Hairpin (öz-erişim) politikası: etkin bir NON-hairpin sunucu varken hairpin
        // adaylar HİÇBİR yolda seçilmez — UDP ölçümü yanıltıcı "Open" dönebilir (tünel-içi
        // rota), NAT hairpin desteklenmeyen yönlendiricide el sıkışma asla tamamlanmaz
        // (canlı gözlenen drop kaynağı). Yalnızca TÜM sunucular hairpin ise son çare.
        var skipHairpin = HasNonHairpinAlternative(enabled, hairpinIds);

        if (bestPing is not null)
        {
            // Ping sırası (ortak OrderCandidates): hairpin adaylar EN SONA (tünel-içi
            // öz-erişim el sıkışma almaz), sonra başarılı pingler önce, başarısız pingler
            // sonra — her aday için mod kararı; ilk WireGuardUDP-uygun (Tier 2) aday kazanır.
            var ordered = OrderCandidates(enabled, results, hairpinIds);

            foreach (var s in ordered)
            {
                if (skipHairpin && hairpinIds.Contains(s.ServerId))
                {
                    Logging.SaveLog($"[{Tag}] {s.Name} hairpin (kendi genel IP'si) — non-hairpin varken atlanıyor.");
                    continue;
                }
                var udp = udpByServer[s.ServerId];
                // Yüksek-gecikme toleransı (DecideSelection ile birebir): ping toleranstan
                // büyük/yeşıtken HandshakeNoResponse ölü sayılmaz — uzak/yoğun sunucuda yanlış
                // V2rayTCP düşüşü engellenir.
                var candidatePingMs = results.First(r => r.ServerId == s.ServerId).DelayMs;
                var check = options.UdpCheck;
                if (options.SlowServerToleranceMs > 0
                    && check.TreatHandshakeNoResponseAsBlocked
                    && candidatePingMs >= options.SlowServerToleranceMs)
                {
                    check = check with { TreatHandshakeNoResponseAsBlocked = false };
                }
                var m = DecideMode(udp, check);
                Logging.SaveLog($"[{Tag}] Aday: {s.Name} ping={candidatePingMs}ms udp={udp.Status} → {m}");
                if (m == ConnectionMode.WireGuardUDP)
                {
                    candidate = s;
                    udpProbe = udp;
                    break;
                }
            }
        }
        else
        {
            // ICMP tamamen engelli olabilir (ISP politikası) — pes etmeden önce
            // UDP'yi doğrudan deneyelim: ilk AÇIK (Open) sunucu seçilir (ping sinyali
            // yokken kesin kanıt gerekir — NoResponse/HandshakeNoResponse aday olmaz).
            Logging.SaveLog($"[{Tag}] ICMP yanıtı yok — ilk UDP-açık sunucu aranıyor");
            udpProbe = udpAll.FirstOrDefault(r => r.Status == UdpProbeStatus.Open
                                                  && !(skipHairpin && hairpinIds.Contains(r.ServerId)));
            if (udpProbe is not null)
            {
                candidate = enabled.First(s => s.ServerId == udpProbe.ServerId);
                Logging.SaveLog($"[{Tag}] UDP testi adayı: {candidate.Name} ({udpProbe.Status})");
            }
        }

        if (candidate is null || udpProbe is null)
        {
            Logging.SaveLog($"[{Tag}] Hiçbir sunucuda UDP yolu doğrulanamadı — V2rayTCP moduna düşülüyor.");
            PublishResilience(new GpnResilienceEvent(
                GpnResilienceAction.ModeDecision, ConnectionMode.V2rayTCP,
                reason: "UDP yolu doğrulanamadı", fromMode: ConnectionMode.WireGuardUDP));
            return new GpnSelectionResult(null, ConnectionMode.V2rayTCP, udpAll.FirstOrDefault(), results);
        }

        // ── Adım 3: Karar — adayın UDP yolu çalışıyorsa WireGuard (Tier 2), değilse V2rayTCP (Tier 3) ──
        // Yüksek-gecikme toleransı (seçim döngüsüyle BIREBIR): seçilen adayın ping'i
        // toleranstan büyük/yeşıtken HandshakeNoResponse yanlış V2rayTCP'ye indirmez.
        var pingMs = results.First(r => r.ServerId == candidate.ServerId).DelayMs;
        var modeCheck = options.UdpCheck;
        if (options.SlowServerToleranceMs > 0
            && modeCheck.TreatHandshakeNoResponseAsBlocked
            && pingMs >= options.SlowServerToleranceMs)
        {
            modeCheck = modeCheck with { TreatHandshakeNoResponseAsBlocked = false };
        }
        var mode = DecideMode(udpProbe, modeCheck);
        Logging.SaveLog($"[{Tag}] Karar: {candidate.Name} → {mode} (ping={pingMs}ms, udp={udpProbe.Status})");
        DiagLog.Write($"GPN_SELECT server={candidate.ServerId} ping={pingMs} udp={udpProbe.Status} mode={mode}");
        PublishResilience(new GpnResilienceEvent(
            GpnResilienceAction.ModeDecision,
            mode,
            reason: candidate.Name,
            serverId: candidate.ServerId,
            serverName: candidate.Name,
            udpStatus: udpProbe.Status,
            delayMs: pingMs));

        // Hairpin zorlaması: hedef sunucu kendi genel IP'si (hairpin) VE etkin tüm
        // sunucular hairpin olduğu için son çare seçildiyse işaretle — koordinatör
        // NAT hairpin gerektiğini kullanıcıya açıklayan bir uyarı gösterir.
        var hairpinForced = hairpinIds.Contains(candidate.ServerId);
        if (hairpinForced)
        {
            Logging.SaveLog($"[{Tag}] HAIRPIN-FORCED: {candidate.Name} hairpin (kendi genel IP'si) — son çare seçildi; NAT hairpin gerekir.");
            DiagLog.Write($"GPN_SELECT hairpin-forced server={candidate.ServerId} — NAT hairpin gerekir");
        }
        return new GpnSelectionResult(candidate, mode, udpProbe, results, hairpinForced);
    }

    /// <summary>
    /// UDP sağlık testi sonucundan bağlantı moduna karar verir (saf — test edilebilir):
    ///  * Open       → WireGuardUDP
    ///  * Blocked    → V2rayTCP (ICMP Port Unreachable kanıtı — kesin)
    ///  * HandshakeNoResponse → V2rayTCP (varsayılan): sağlıklı sunucu geçerli el
    ///    sıkışmaya yanıt verir; sessizlik güçlü bozukluk işaretidir. Blocked'tan
    ///    farkı ICMP kanıtının OLMAMASI; yine de tünel kurulamayacağı için düşüş
    ///    güvenli taraftır. TreatHandshakeNoResponseAsBlocked=false ile WireGuardUDP
    ///    korunabilir (riskli senaryolar için).
    ///  * NoResponse → politika: varsayılan WireGuardUDP (WireGuard junk pakete
    ///    yanıt vermez, sessizlik sağlıklı yoldur); TreatNoResponseAsBlocked=true
    ///    ise V2rayTCP'ye düş.
    /// </summary>
    internal static ConnectionMode DecideMode(UdpProbeResult probe, UdpHealthCheckOptions? options = null)
    {
        options ??= new UdpHealthCheckOptions();
        return probe.Status switch
        {
            UdpProbeStatus.Open => ConnectionMode.WireGuardUDP,
            UdpProbeStatus.Blocked => ConnectionMode.V2rayTCP,
            UdpProbeStatus.HandshakeNoResponse =>
                options.TreatHandshakeNoResponseAsBlocked ? ConnectionMode.V2rayTCP : ConnectionMode.WireGuardUDP,
            _ => options.TreatNoResponseAsBlocked ? ConnectionMode.V2rayTCP : ConnectionMode.WireGuardUDP,
        };
    }

    /// <summary>
    /// Otomatik seçim kararının saf (ağ yok, test edilebilir) hali —
    /// <see cref="SelectBestServerAsync"/>'in Adım 2-3'ü ile BİREBİR aynı mantık:
    ///
    ///  1. Ping adayları ping sırasına göre (başarılı ping önce, düşük gecikme
    ///     önce) taranır; ilk WireGuardUDP-uygun UDP sonucu (DecideMode) kazanır —
    ///     en düşük ping'li adayın UDP'si ölüyse ikinci aday denenir (Akıllı Düşüş),
    ///  2. Ping adayı yoksa (ICMP engelli) ilk UDP-açık (Open) sunucu seçilir,
    ///  3. Aday yok veya UDP sonucu yoksa → V2rayTCP (Best=null).
    ///
    /// Dashboard "en iyi aday" kartı bu fonksiyonu kullanır; karar mantığı
    /// çoğaltılmaz, tahmin her zaman Bağlan'ın kararıyla aynıdır.
    /// </summary>
    internal static GpnSelectionPrediction DecideSelection(
        IReadOnlyList<GpnServerProfile> servers,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null,
        IReadOnlySet<string>? hairpinServerIds = null)
    {
        options ??= new GpnProbeOptions();
        hairpinServerIds ??= new HashSet<string>();
        var enabled = servers.Where(s => s.IsEnabled).ToArray();
        if (enabled.Length == 0)
        {
            return new GpnSelectionPrediction(null, ConnectionMode.V2rayTCP, null, "aktif sunucu yok");
        }

        // Yalnızca etkin sunucular aday olabilir (gerçek akışta ölçüm zaten yalnızca
        // etkin sunuculara atılır — eski/uzaylı ping girdilerine karşı savunma).
        var enabledIds = enabled.Select(s => s.ServerId).ToHashSet();

        // Adım 2 (SelectBestServerAsync ile aynı): en düşük gecikmeli ping adayı.
        var bestPing = pingResults
            .Where(r => r.IsSuccess && enabledIds.Contains(r.ServerId))
            .OrderBy(r => r.DelayMs)
            .FirstOrDefault();

        GpnServerProfile? candidate = null;
        UdpProbeResult? udpProbe = null;
        // Hairpin (öz-erişim) politikası: etkin bir NON-hairpin sunucu varken hairpin
        // adaylar HİÇBİR koşulda seçilmez (UDP ölçümü yanıltıcı "Open" verebilir; NAT
        // hairpin desteklenmeyen yönlendiricide el sıkışma asla almaz). Yalnızca TÜM
        // sunucular hairpin ise son çare olarak seçilebilir.
        var skipHairpin = HasNonHairpinAlternative(enabled, hairpinServerIds);

        if (bestPing is not null)
        {
            // Akıllı Düşüş (SelectBestServerAsync BUGFIX'iyle birebir): en düşük
            // ping'li adayın UDP'si ölüyse (bloklu / handshake-no-response) ikinci
            // en iyi aday denenir; hairpin adaylar ise OrderCandidates ile EN SONA
            // alınır. İlk WireGuardUDP-uygun aday kazanır; yalnızca HİÇBİR aday uygun
            // değilse V2rayTCP düşüşü.
            var ordered = OrderCandidates(enabled, pingResults, hairpinServerIds);

            foreach (var s in ordered)
            {
                if (skipHairpin && hairpinServerIds.Contains(s.ServerId))
                {
                    continue; // non-hairpin alternatif varken hairpin'i atla
                }
                if (!udpResults.TryGetValue(s.ServerId, out var udp))
                {
                    continue; // bu adayın UDP sonucu yok — atla
                }
                // Yüksek-gecikme toleransı: bu adayın ping'i SlowServerToleranceMs'e eşit/üst
                // ise HandshakeNoResponse ölü sayılmaz (uzak/yoğun sunucuda kısa probe penceresi
                // yetersiz olabilir) — seçim yanlış V2rayTCP'ye düşmesin.
                var candidatePingMs = pingResults.FirstOrDefault(r => r.ServerId == s.ServerId)?.DelayMs ?? -1;
                var check = options.UdpCheck;
                if (options.SlowServerToleranceMs > 0
                    && check.TreatHandshakeNoResponseAsBlocked
                    && candidatePingMs >= options.SlowServerToleranceMs)
                {
                    check = check with { TreatHandshakeNoResponseAsBlocked = false };
                }
                if (DecideMode(udp, check) == ConnectionMode.WireGuardUDP)
                {
                    candidate = s;
                    udpProbe = udp;
                    break;
                }
            }
        }
        else
        {
            // ICMP yanıtı yok — pes etmeden önce ilk UDP-açık sunucu (Adım 2b):
            // ping sinyali yokken kesin kanıt (Open) gerekir. Non-hairpin alternatif
            // varken hairpin Open sunucu seçilmez.
            udpProbe = udpResults.Values.FirstOrDefault(
                r => r.Status == UdpProbeStatus.Open
                     && enabledIds.Contains(r.ServerId)
                     && !(skipHairpin && hairpinServerIds.Contains(r.ServerId)));
            if (udpProbe is not null)
            {
                candidate = enabled.First(s => s.ServerId == udpProbe.ServerId);
            }
        }

        if (candidate is null || udpProbe is null)
        {
            return new GpnSelectionPrediction(null, ConnectionMode.V2rayTCP, udpProbe,
                "UDP yolu doğrulanamadı — V2rayTCP düşüşü");
        }

        // Adım 3: UDP durumundan mod kararı — seçilen adayın ping'iyle tolerans uygulanır
        // (döngüdeki seçimle BİREBİR tutarlı; yoksa yüksek-ping HandshakeNoResponse aday
        // seçilip yine de V2rayTCP gösterilerek çelişirdi).
        var selectedPing = pingResults.FirstOrDefault(r => r.ServerId == candidate.ServerId)?.DelayMs ?? -1;
        var modeCheck = options.UdpCheck;
        if (options.SlowServerToleranceMs > 0
            && modeCheck.TreatHandshakeNoResponseAsBlocked
            && selectedPing >= options.SlowServerToleranceMs)
        {
            modeCheck = modeCheck with { TreatHandshakeNoResponseAsBlocked = false };
        }
        var mode = DecideMode(udpProbe, modeCheck);
        var pingMs = pingResults.First(r => r.ServerId == candidate.ServerId).DelayMs;
        var reason = mode == ConnectionMode.WireGuardUDP
            ? $"{candidate.Name} — ping {pingMs}ms, UDP {udpProbe.Status}"
            : $"{candidate.Name} — UDP {udpProbe.Status} → V2rayTCP düşüşü";
        return new GpnSelectionPrediction(candidate, mode, udpProbe, reason);
    }

    /// <inheritdoc cref="IGpnServerSelectionService.EvaluateSelection"/>
    public GpnSelectionPrediction EvaluateSelection(
        IReadOnlyList<GpnServerProfile> servers,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null,
        IReadOnlySet<string>? hairpinServerIds = null)
        => DecideSelection(servers, pingResults, udpResults, options, hairpinServerIds);

    /// <summary>Failover kararının eylemi.</summary>
    internal enum FailoverActionType
    {
        /// <summary>Değişiklik yok — aktif tünel sağlıklı, daha iyi aday yok.</summary>
        None,

        /// <summary>UDP'si sağlıklı başka bir sunucuya geç.</summary>
        SwitchServer,

        /// <summary>Hiçbir sunucuda sağlıklı UDP yok — WireGuard tüneli öldü, V2rayTCP'ye düş.</summary>
        FallbackToV2ray,
    }

    /// <summary>Failover kararının sonucu (saf, test edilebilir).</summary>
    internal sealed record FailoverDecision(FailoverActionType Action, GpnServerProfile? Target, string Reason);

    /// <summary>
    /// Failover karar mantığı (saf — ağ yok, test edilebilir):
    ///
    ///  * Aktif tünel sağlıklıysa (UDP Open, veya katı olmayan politikada NoResponse):
    ///    yalnızca aktiften SwitchHysteresisMs kadar daha iyi ping'li ve UDP'si sağlıklı
    ///    bir aday varsa SwitchServer. Salınım bu marjla engellenir.
    ///  * Aktif tünel öldüyse (UDP Blocked, veya katı politikada NoResponse):
    ///    önce UDP'si sağlıklı bir aday aranır → SwitchServer.
    ///  * Hiçbir adayda sağlıklı UDP yoksa → FallbackToV2ray (Tier 3).
    ///
    /// UDP sağlık testi kapalıysa (EnableUdpHealth=false) tüm sunucular "sağlıklı"
    /// sayılır ve davranış eski ping-tabanlı failover'a döner.
    /// </summary>
    internal static FailoverDecision DecideFailover(
        GpnServerProfile active,
        IReadOnlyList<GpnServerProfile> candidates,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions options,
        IReadOnlySet<string>? hairpinServerIds = null)
    {
        hairpinServerIds ??= new HashSet<string>();
        var strict = options.UdpCheck.TreatNoResponseAsBlocked;
        var strictHandshake = options.UdpCheck.TreatHandshakeNoResponseAsBlocked;
        var udpEnabled = options.EnableUdpHealth;
        var tolerance = options.SlowServerToleranceMs;

        bool IsUdpHealthy(GpnServerProfile server, int pingMs)
        {
            if (!udpEnabled || !udpResults.TryGetValue(server.ServerId, out var udp))
            {
                return true; // UDP devre dışı veya sonuç yok → varsayılan sağlıklı
            }
            if (IsUdpHealthyStatus(udp.Status, strict, strictHandshake))
            {
                return true;
            }
            // Yüksek-gecikme toleransı: ping'i toleranstan büyük/yeşıt olan bir sunucunun
            // HandshakeNoResponse'u 'ölü' sayılmaz — kısa probe penceresi uzak/yoğun bir
            // WireGuard sunucusu için yetersiz kalabilir (false-dead → gereksiz failover).
            if (udp.Status == UdpProbeStatus.HandshakeNoResponse
                && tolerance > 0 && pingMs >= tolerance)
            {
                return true;
            }
            return false;
        }

        var activeUdp = udpEnabled ? udpResults.GetValueOrDefault(active.ServerId) : null;
        var activePing = pingResults
            .FirstOrDefault(r => r.ServerId == active.ServerId && r.IsSuccess)?.DelayMs ?? int.MaxValue;
        var activeUdpDead = activeUdp is not null
            && IsUdpDeadStatus(activeUdp.Status, strict, strictHandshake)
            // Tolerans: aktif tünel yüksek ping'deyse HandshakeNoResponse ölü sayılmaz.
            && !(activeUdp.Status == UdpProbeStatus.HandshakeNoResponse
                 && tolerance > 0 && activePing != int.MaxValue && activePing >= tolerance);

        // Hairpin (öz-erişim, kendi genel IP'si) politikası: etkin bir non-hairpin
        // sunucu varken hairpin'ler geçiş hedefi OLAMAZ — UDP ölçümü yanıltıcı "Open"
        // dönebilir ama NAT hairpin desteklenmeyen yönlendiricide el sıkışma almaz
        // (canlı gözlenen düşüş kaynağı). Yalnızca TÜM sunucular hairpin ise son çare.
        var skipHairpin = HasNonHairpinAlternative(
            candidates.Where(c => c.IsEnabled).ToArray(), hairpinServerIds);

        // Switch adayları: aktif hariç, UDP'si sağlıklı (+ tolerans bilinçli), non-hairpin
        // varken hairpin değil. Ping başarısı olanlar önce, sonra düşük gecikmeye göre
        // sıralanır (ICMP engelli ama UDP sağlıklı adaylar da geçerli hedeftir).
        var switchTargets = candidates
            .Where(c => c.ServerId != active.ServerId
                        && !(skipHairpin && hairpinServerIds.Contains(c.ServerId)))
            .Select(c => (Server: c, Ping: pingResults.FirstOrDefault(r => r.ServerId == c.ServerId)?.DelayMs ?? -1))
            .Where(x => IsUdpHealthy(x.Server, x.Ping)
                        && (x.Ping >= 0 || (udpEnabled && udpResults.ContainsKey(x.Server.ServerId))))
            .OrderByDescending(x => x.Ping >= 0)
            .ThenBy(x => x.Ping)
            .ToList();

        if (!activeUdpDead)
        {
            if (activePing == int.MaxValue)
            {
                // Aktif tünel UDP sağlıklı ama ICMP ölçülemiyor (ISP engeli olabilir) —
                // ping tabanı yok, hysteresis karşılaştırması yapılamaz; geçiş salınımı
                // yaratmamak için dokunma.
                return new FailoverDecision(FailoverActionType.None, null, "aktif tünel sağlıklı, ping tabanı yok");
            }

            // Aktif tünel sağlıklı — yalnızca belirgin şekilde daha iyi adaya geç.
            var better = switchTargets
                .FirstOrDefault(x => x.Ping >= 0 && x.Ping < activePing - options.SwitchHysteresisMs);
            if (better.Server is not null)
            {
                return new FailoverDecision(FailoverActionType.SwitchServer, better.Server,
                    $"ping {activePing}→{better.Ping}ms (hysteresis {options.SwitchHysteresisMs}ms)");
            }
            return new FailoverDecision(FailoverActionType.None, null, "aktif tünel sağlıklı, daha iyi aday yok");
        }

        // Aktif tünel öldü — önce sağlıklı UDP'li başka sunucu ara.
        if (switchTargets.Count > 0)
        {
            var target = switchTargets.First().Server;
            return new FailoverDecision(FailoverActionType.SwitchServer, target,
                $"aktif UDP ölü ({activeUdp!.Status}) → {target.Name} (UDP sağlıklı)");
        }

        return new FailoverDecision(FailoverActionType.FallbackToV2ray, null,
            "hiçbir sunucuda sağlıklı UDP yok — tünel öldü");
    }

    /// <summary>
    /// Tüm sunucularda paralel UDP sağlık testi (el sıkışma + junk teşhis zinciri).
    /// Seçim/failover içinde kullanılır; dashboard ⚡ Test paneli de (MainWindow
    /// ProbeGpnServersAsync) aynı kanıtı göstermek için buradan beslenir.
    /// </summary>
    public async Task<IReadOnlyList<UdpProbeResult>> ProbeUdpAllAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        var results = new UdpProbeResult[servers.Count];
        using var semaphore = new SemaphoreSlim(options.MaxConcurrency);
        async Task ProbeOneAsync(GpnServerProfile server, int index)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await ProbeUdpAsync(server, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        await Task.WhenAll(servers.Select(ProbeOneAsync)).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Bir sunucunun UDP yolunu test eder. Gerçek WireGuard el sıkışması (kesin
    /// kanıt) etkinse ve anahtarlar mevcutsa onu kullanır; el sıkışma yanıtsızsa
    /// bloklama sinyalini doğrulamak için 1 baytlık junk probe'a düşer:
    ///
    ///  * El sıkışma Open      → kesin kanıt (sunucu anahtarları tanıdı ve yanıtladı)
    ///  * El sıkışma Blocked   → ICMP kanıtı, kesin
    ///  * El sıkışma HandshakeNoResponse → junk probe ile teşhis zinciri:
    ///      - junk Blocked → Blocked (ICMP kanıtı, el sıkışma da yanıtsızdı)
    ///      - junk Open    → Open (portta yanıtlayan bir hizmet var)
    ///      - junk NoResponse → HandshakeNoResponse (daha güçlü sinyal — sağlıklı
    ///        sunucu geçerli el sıkışmaya yanıt verirdi; ICMP kanıtı yok)
    ///  * El sıkışma kapalı/anahtar yok → yalnızca junk probe
    /// </summary>
    internal async Task<UdpProbeResult> ProbeUdpAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        UdpProbeResult? handshake = null;
        if (options.UseWireGuardHandshakeProbe)
        {
            handshake = await _wireGuardProbe.ProbeAsync(
                server.ServerId, server.EndpointHost, server.EndpointPort,
                server.ServerPublicKey, server.ClientPrivateKey,
                options.HandshakeProbe, cancellationToken).ConfigureAwait(false);

            // Kesin sonuç (Open: geçerli el sıkışma; Blocked: ICMP) → hemen dön.
            if (handshake.Status is UdpProbeStatus.Open or UdpProbeStatus.Blocked)
            {
                return handshake;
            }
        }

        // El sıkışma yanıtsız (veya handshake kapalı/anahtar yok) → junk probe ile
        // ICMP-bloklu sinyalini teyit et; teşhis zinciri aşağıda birleştirilir.
        var junk = await _udpHealthChecker.ProbeAsync(
            server.ServerId, server.EndpointHost, server.EndpointPort,
            options.UdpCheck, cancellationToken).ConfigureAwait(false);

        // Junk: kesin sinyal (ICMP bloklu veya yanıtlayan hizmet) — el sıkışma
        // yanıtsızlığını da not düşerek döndür.
        if (junk.Status is UdpProbeStatus.Blocked or UdpProbeStatus.Open)
        {
            var enrich = handshake is { Status: UdpProbeStatus.HandshakeNoResponse };
            return enrich
                ? junk with { Detail = $"{junk.Detail}; el sıkışma da yanıtsızdı (geçerli initiation)" }
                : junk;
        }

        // İkisi de sessiz → el sıkışma teşhisi daha güçlü (sağlıklı sunucu yanıt
        // verirdi; ICMP kanıtı yok — Blocked'tan ayrı tutulur).
        if (handshake is not null)
        {
            return handshake with { Detail = $"{handshake.Detail}; junk probe de NoResponse (ICMP kanıtı yok)" };
        }

        return junk; // handshake kapalı — yalnızca junk sonucu
    }

    /// <summary>
    /// Kullanıcının seçtiği sunucuyu yalnızca KENDİSİNİ ölçerek dener: ping + UDP
    /// sağlık (el sıkışma + junk zinciri). <see cref="DecideMode"/> WireGuardUDP
    /// derse sonuç döner, değilse null — çağıran Akıllı Düşüş'e düşer. Diğer adaylar
    /// HİÇ ölçülmez: seçim kullanıcı tercihidir; otomatik ölçüm yalnızca seçilen
    /// sunucu başarısız olduğunda devreye girer.
    /// </summary>
    private async Task<GpnSelectionResult?> TrySelectPreferredAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        var ping = await ProbeServerAsync(server, options, cancellationToken).ConfigureAwait(false);
        var udp = await ProbeUdpAsync(server, options, cancellationToken).ConfigureAwait(false);

        var pingMs = ping.IsSuccess ? ping.DelayMs : -1;
        var check = options.UdpCheck;
        if (options.SlowServerToleranceMs > 0
            && check.TreatHandshakeNoResponseAsBlocked
            && pingMs >= options.SlowServerToleranceMs)
        {
            check = check with { TreatHandshakeNoResponseAsBlocked = false };
        }
        var mode = DecideMode(udp, check);
        Logging.SaveLog($"[{Tag}] Tercih: {server.Name} ping={pingMs}ms udp={udp.Status} → {mode}");
        DiagLog.Write($"GPN_SELECT server={server.ServerId} ping={pingMs} udp={udp.Status} mode={mode} preferred=1");
        if (mode != ConnectionMode.WireGuardUDP)
        {
            return null;
        }

        PublishResilience(new GpnResilienceEvent(
            GpnResilienceAction.ModeDecision, mode,
            reason: server.Name,
            serverId: server.ServerId,
            serverName: server.Name,
            udpStatus: udp.Status,
            delayMs: pingMs));
        return new GpnSelectionResult(server, mode, udp, new[] { ping });
    }

    /// <summary>
    /// Tüm sunucuları tam istatistikle (min/avg/max/kayıp) ölç. Dashboard
    /// telemetrisi ve manuel "test et" butonu için.
    /// </summary>
    public async Task<IReadOnlyList<GpnServerProbeResult>> ProbeAllAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GpnProbeOptions();

        // EscapeTunnelForProbes açıkken her ölçüm grubu taze tünel durumuyla başlar:
        // ⚡ Test manuel basıldığında veya failover çevriminde tünel yeni kurulmuş
        // olabilir (15 sn önbellek bayat kalır → tünel algılanmaz → ICMP atlanmaz,
        // UDP fiziksel NIC'e bağlanmaz). Tünel yoksa Refresh no-op'tur (normal ölçüm).
        if (options.EscapeTunnelForProbes)
        {
            ProbeEgressNic.Refresh();
        }

        var results = new GpnServerProbeResult[servers.Count];

        using var semaphore = new SemaphoreSlim(options.MaxConcurrency);
        async Task ProbeOneAsync(GpnServerProfile server, int index)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await ProbeServerAsync(server, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        await Task.WhenAll(servers.Select(ProbeOneAsync)).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Failover karar matrisini hesaplar: AYNI ölçümü (ping + UDP durumları) iki
    /// politika altında değerlendirir — varsayılan (NoResponse sağlıklı, el sıkışma
    /// yanıtsızlığı ölü) ve katı (NoResponse da ölü). Kararlar saf
    /// <see cref="DecideFailover"/> ile üretilir; dashboard yalnızca görüntüler.
    /// Satır sağlık bayrakları <see cref="IsUdpHealthyStatus"/> ile hesaplanır.
    /// </summary>
    public GpnFailoverMatrix EvaluateFailoverMatrix(
        GpnServerProfile active,
        IReadOnlyList<GpnServerProfile> candidates,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null)
    {
        options ??= new GpnProbeOptions();
        var baseCheck = options.UdpCheck;

        // Varsayılan: junk NoResponse = sağlıklı; el sıkışma yanıtsızlığı = ölü.
        var defaultOptions = options with
        {
            UdpCheck = baseCheck with { TreatNoResponseAsBlocked = false, TreatHandshakeNoResponseAsBlocked = true },
        };
        // Katı: ikisi de ölü.
        var strictOptions = options with
        {
            UdpCheck = baseCheck with { TreatNoResponseAsBlocked = true, TreatHandshakeNoResponseAsBlocked = true },
        };

        var defaultDecision = DecideFailover(active, candidates, pingResults, udpResults, defaultOptions);
        var strictDecision = DecideFailover(active, candidates, pingResults, udpResults, strictOptions);

        var rows = candidates
            .Where(c => c.IsEnabled)
            .Select(c =>
            {
                udpResults.TryGetValue(c.ServerId, out var udp);
                var ping = pingResults.FirstOrDefault(r => r.ServerId == c.ServerId);
                return new GpnFailoverMatrixRow(
                    c.ServerId,
                    c.Name,
                    ping?.DelayMs ?? -1,
                    ping?.LossPercent ?? 100,
                    udp?.Status,
                    c.ServerId == active.ServerId,
                    // "Varsayılan" sütunu: NoResponse sağlıklı, el sıkışma yanıtsız ölü.
                    udp is null || IsUdpHealthyStatus(udp.Status, strictNoResponse: false, strictHandshake: true),
                    // "Katı" sütunu: ikisi de ölü.
                    udp is null || IsUdpHealthyStatus(udp.Status, strictNoResponse: true, strictHandshake: true));
            })
            .OrderBy(r => r.IsActive ? 0 : 1)
            .ThenBy(r => r.PingMs)
            .ToArray();

        return new GpnFailoverMatrix(
            rows,
            ToPolicy(defaultOptions, defaultDecision),
            ToPolicy(strictOptions, strictDecision),
            DateTimeOffset.UtcNow);
    }

    private static GpnFailoverMatrixPolicy ToPolicy(GpnProbeOptions options, FailoverDecision decision)
        => new(
            options.UdpCheck.TreatNoResponseAsBlocked,
            options.UdpCheck.TreatHandshakeNoResponseAsBlocked,
            options.EnableUdpHealth,
            decision.Action switch
            {
                FailoverActionType.SwitchServer => GpnFailoverAction.SwitchServer,
                FailoverActionType.FallbackToV2ray => GpnFailoverAction.FallbackToV2ray,
                _ => GpnFailoverAction.None,
            },
            decision.Target?.ServerId,
            decision.Reason);

    /// <summary>
    /// Bağlı kalınan WireGuard tünelinin sürekli izlenmesi — UDP destekli failover.
    ///
    /// Her çevrimde (varsayılan 15 sn):
    ///  1. Tüm adaylara paralel ping atılır (gecikme trendi).
    ///  2. Tüm adayların 51820/udp yolu paralel UdpHealthChecker ile test edilir
    ///     — tünelin gerçekten ölüp ölmediğinin kanıtı (ICMP Port Unreachable).
    ///  3. Karar (saf DecideFailover):
    ///     - Aktif tünel sağlıklıysa: yalnızca belirgin şekilde daha iyi (hysteresis
    ///       marjı) ve UDP'si sağlıklı bir aday varsa onSwitch ile geçilir.
    ///     - Aktif tünel öldüyse (Blocked, veya katı politikada NoResponse): önce
    ///       UDP'si sağlıklı başka sunucu aranır → onSwitch.
    ///     - Hiçbir sunucuda sağlıklı UDP yoksa: onModeFallback(ConnectionMode.V2rayTCP)
    ///       tetiklenir ve izleyici durur — Tier 3'e düşüş terminaldir.
    /// </summary>
    public async Task RunFailoverMonitorAsync(
        GpnServerProfile active,
        IReadOnlyList<GpnServerProfile> candidates,
        Func<GpnServerProfile, CancellationToken, Task> onSwitch,
        Func<ConnectionMode, CancellationToken, Task>? onModeFallback = null,
        Func<GpnServerProfile, CancellationToken, Task>? onRecover = null,
        GpnProbeOptions? options = null,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GpnProbeOptions();
        interval ??= TimeSpan.FromSeconds(15);
        onModeFallback ??= (_, _) => Task.CompletedTask;

        var current = active;
        var recovering = false; // V2rayTCP düşüşünden sonra Tier-2 (WireGuard) kurtarması bekleniyor

        // Ping-pong sıçrama koruması: son değişimde terk edilen sunucu + zaman damgası.
        // <see cref="GpnProbeOptions.FailoverSwitchCooldownSeconds"/> boyunca o sunucuya
        // geri dönülmez (TUN altındaki yanıltıcı UDP probe'u yüzünden sürekli A↔B döngüsü).
        string? abandonedServerId = null;
        var lastSwitchUtc = DateTime.MinValue;

        // Tünel durumu önbelleğini tazele: izleyici başlarken tünel genelde zaten
        // kuruludur — ilk çevrimden itibaren probe'lar fiziksel NIC üzerinden ölçsün
        // (kendi tünelinin içine yakalanmasın). Tünel yoksa no-op (normal ölçüm).
        ProbeEgressNic.Refresh();

        // Hairpin (kendi genel IP'si, öz-erişim) kimlikleri — izleyici başlarken bir
        // kez çözülür (önbellekli, 60 sn). Non-hairpin alternatif varken hairpin ne
        // geçiş ne kurtarma hedefi olur: UDP ölçümü yanıltıcı "Open" dönebilir ama NAT
        // hairpin desteklenmeyen yönlendiricide el sıkışma asla tamamlanmaz → döngü/drop.
        var hairpinIds = await ResolveHairpinServerIdsAsync(candidates, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval.Value, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                // 1) Ping ölçümleri (tüm adaylar)
                var pingResults = await ProbeAllAsync(candidates, options, cancellationToken);

                // 2) UDP sağlık testleri (tüm adaylar, paralel) — tünel ölümünün kanıtı
                IReadOnlyDictionary<string, UdpProbeResult> udpResults;
                if (options.EnableUdpHealth)
                {
                    var udpList = await ProbeUdpAllAsync(candidates, options, cancellationToken);
                    udpResults = udpList.ToDictionary(r => r.ServerId);
                }
                else
                {
                    udpResults = new Dictionary<string, UdpProbeResult>();
                }

                // ── Kurtarma modu: V2rayTCP'ye düştük, sağlıklı sunucu dönünce Tier-2 ──
                if (recovering)
                {
                    var recoveryTarget = DecideRecovery(candidates, pingResults, udpResults, options, hairpinIds);
                    if (recoveryTarget is not null)
                    {
                        Logging.SaveLog($"[{Tag}] Kurtarma: {recoveryTarget.Name} UDP sağlıklı — Tier-2 (WireGuard) dönüşü");
                        DiagLog.Write($"GPN_RECOVER wireguard → {recoveryTarget.ServerId}");
                        PublishResilience(new GpnResilienceEvent(
                            GpnResilienceAction.Recover,
                            ConnectionMode.WireGuardUDP,
                            reason: recoveryTarget.Name,
                            serverId: recoveryTarget.ServerId,
                            serverName: recoveryTarget.Name));
                        await onRecover!.Invoke(recoveryTarget, cancellationToken);
                        current = recoveryTarget;
                        recovering = false;
                    }
                    continue; // henüz sağlıklı sunucu yoksa bekle
                }

                // 3) Saf karar fonksiyonu (normal mod)
                var decision = DecideFailover(current, candidates, pingResults, udpResults, options, hairpinIds);
                switch (decision.Action)
                {
                    case FailoverActionType.SwitchServer when decision.Target is not null:
                        // Ping-pong sıçrama koruması: henüz terk ettiğimiz sunucuya cooldown
                        // içinde geri dönmek istiyorsak, probe fluke'u say (tüneli yırtma) —
                        // bir sonraki çevrimde doğrulanınca geçiş yapılır. Bu, "bağlantı
                        // kendi kendini koparıyor" olarak görünen A↔B döngüsünü kırar.
                        if (IsPingPongSwitchSuppressed(
                            abandonedServerId,
                            decision.Target.ServerId,
                            lastSwitchUtc,
                            DateTime.UtcNow,
                            options.FailoverSwitchCooldownSeconds))
                        {
                            Logging.SaveLog($"[{Tag}] Ping-pong koruması: {decision.Target.Name}" +
                                $" az önce terk edildi — cooldown içinde geri dönülmüyor (şüpheli probe sonucu).");
                            DiagLog.Write($"GPN_FAILOVER pingpong-suppressed target={decision.Target.ServerId}");
                            continue;
                        }

                        Logging.SaveLog($"[{Tag}] Failover sunucu değişimi: {current.Name} → {decision.Target.Name} ({decision.Reason})");
                        DiagLog.Write($"GPN_FAILOVER switch {current.ServerId}→{decision.Target.ServerId} reason={decision.Reason}");
                        PublishResilience(new GpnResilienceEvent(
                            GpnResilienceAction.ServerSwitch,
                            ConnectionMode.WireGuardUDP,
                            reason: decision.Reason,
                            serverId: current.ServerId,
                            serverName: current.Name,
                            targetServerId: decision.Target.ServerId,
                            targetServerName: decision.Target.Name,
                            udpStatus: ResolveActiveUdpStatus(udpResults, current.ServerId, options)));
                        await onSwitch(decision.Target, cancellationToken);
                        abandonedServerId = current.ServerId;
                        lastSwitchUtc = DateTime.UtcNow;
                        current = decision.Target;
                        break;

                    case FailoverActionType.FallbackToV2ray:
                        Logging.SaveLog($"[{Tag}] Failover V2rayTCP düşüşü: {decision.Reason}");
                        DiagLog.Write($"GPN_FAILOVER fallback V2rayTCP reason={decision.Reason}");
                        PublishResilience(new GpnResilienceEvent(
                            GpnResilienceAction.UdpDeath,
                            ConnectionMode.V2rayTCP,
                            reason: decision.Reason,
                            serverId: current.ServerId,
                            serverName: current.Name,
                            fromMode: ConnectionMode.WireGuardUDP,
                            udpStatus: ResolveActiveUdpStatus(udpResults, current.ServerId, options)));
                        PublishResilience(new GpnResilienceEvent(
                            GpnResilienceAction.ModeFallback,
                            ConnectionMode.V2rayTCP,
                            reason: decision.Reason,
                            serverId: current.ServerId,
                            serverName: current.Name,
                            fromMode: ConnectionMode.WireGuardUDP));
                        await onModeFallback(ConnectionMode.V2rayTCP, cancellationToken);

                        // Kurtarma beklenmiyorsa Tier 3 düşüşü terminaldir (eski davranış).
                        if (!options.EnableRecoveryWatch || onRecover is null)
                        {
                            return;
                        }

                        // Aksi halde Tier-2 kurtarmasını izle: sağlıklı sunucu dönünce
                        // onRecover ile otomatik WireGuard'a geri dön.
                        recovering = true;
                        current = null!;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"[{Tag}] Failover çevrimi hatası: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// V2rayTCP düşüşü sonrası WireGuard kurtarma kararı (saf — ağ yok, test edilebilir):
    /// tüm adayların UDP sağlığına bakar, sağlıklı (Open; katı olmayan politikada
    /// NoResponse da sağlıklı) olanlar arasından DÜŞÜK-ping'li adayı döndürür.
    /// Sağlıklı aday yoksa null. Seçilen aday Tier-2'ye dönüşün hedefidir.
    /// </summary>
    internal static GpnServerProfile? DecideRecovery(
        IReadOnlyList<GpnServerProfile> candidates,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions options,
        IReadOnlySet<string>? hairpinServerIds = null)
    {
        hairpinServerIds ??= new HashSet<string>();
        var strict = options.UdpCheck.TreatNoResponseAsBlocked;
        var strictHandshake = options.UdpCheck.TreatHandshakeNoResponseAsBlocked;
        var udpEnabled = options.EnableUdpHealth;
        var tolerance = options.SlowServerToleranceMs;

        bool IsUdpHealthy(GpnServerProfile server, int pingMs)
        {
            if (!udpEnabled || !udpResults.TryGetValue(server.ServerId, out var udp))
            {
                return true; // UDP devre dışı veya sonuç yok → varsayılan sağlıklı
            }
            if (IsUdpHealthyStatus(udp.Status, strict, strictHandshake))
            {
                return true;
            }
            // Yüksek-gecikme toleransı (DecideFailover ile aynı): yüksek ping'li bir
            // sunucunun HandshakeNoResponse'u yanlış 'ölü' sayılıp kurtarma kaçırılmaz.
            if (udp.Status == UdpProbeStatus.HandshakeNoResponse
                && tolerance > 0 && pingMs >= tolerance)
            {
                return true;
            }
            return false;
        }

        // Hairpin (öz-erişim) politikası: non-hairpin alternatif varken hairpin'e
        // Tier-2 kurtarması yapılmaz — el sıkışma almaz (NAT hairpin yoksa).
        var skipHairpin = HasNonHairpinAlternative(
            candidates.Where(c => c.IsEnabled).ToArray(), hairpinServerIds);

        var target = candidates
            .Where(c => c.IsEnabled
                        && !(skipHairpin && hairpinServerIds.Contains(c.ServerId)))
            .Select(c => (Server: c, Ping: pingResults.FirstOrDefault(r => r.ServerId == c.ServerId)?.DelayMs ?? -1))
            .Where(x => IsUdpHealthy(x.Server, x.Ping))
            .OrderByDescending(x => x.Ping >= 0)
            .ThenBy(x => x.Ping)
            .FirstOrDefault().Server;

        return target;
    }

    /// <summary>
    /// Ping-pong sıçrama koruması (saf — test edilebilir): bir sunucu değişiminden
    /// <c>cooldownSeconds</c> içinde, AZ ÖNCE TERK EDİLEN sunucuya (target == abandoned)
    /// geri dönmeyi engeller. TUN altındaki yanıltıcı UDP probe sonuçları iki sunucu
    /// arasında sürekli gidip-gelme yaratır (her geçiş sing-box restart + TCP kesimi =
    /// "bağlantı kendi kendini koparıyor"); bu koruma probe bir kez yanılırsa tünelin
    /// yerinde kalmasını sağlar. cooldown &lt;= 0 ise koruma kapalı (eski davranış).
    /// </summary>
    internal static bool IsPingPongSwitchSuppressed(
        string? abandonedServerId,
        string? targetServerId,
        DateTime lastSwitchUtc,
        DateTime now,
        int cooldownSeconds)
        => abandonedServerId is not null
           && targetServerId is not null
           && string.Equals(abandonedServerId, targetServerId, StringComparison.OrdinalIgnoreCase)
           && cooldownSeconds > 0
           && (now - lastSwitchUtc).TotalSeconds < cooldownSeconds;

    /// <summary>
    /// UDP durumunun "sağlıklı" sayılıp sayılmayacağı (failover/kurtarma adayı).
    /// Katı bayraklar: NoResponse (junk) ve HandshakeNoResponse için ayrı politikalar
    /// — varsayılan katı handshake politikası, el sıkışma yanıtsızlığını "ölü" sayar
    /// (sağlıklı sunucu geçerli el sıkışmaya yanıt verir), NoResponse ise WireGuard
    /// junk-sessizliği olduğundan sağlıklı kalır.
    /// </summary>
    internal static bool IsUdpHealthyStatus(UdpProbeStatus status, bool strictNoResponse, bool strictHandshake)
        => status == UdpProbeStatus.Open
           || (status == UdpProbeStatus.NoResponse && !strictNoResponse)
           || (status == UdpProbeStatus.HandshakeNoResponse && !strictHandshake);

    /// <summary>UDP durumunun "ölü" (tünel çalışmaz) sayılıp sayılmayacağı — <see cref="IsUdpHealthyStatus"/> tersi.</summary>
    internal static bool IsUdpDeadStatus(UdpProbeStatus status, bool strictNoResponse, bool strictHandshake)
        => status == UdpProbeStatus.Blocked
           || (status == UdpProbeStatus.NoResponse && strictNoResponse)
           || (status == UdpProbeStatus.HandshakeNoResponse && strictHandshake);

    // ── Olay yayını ───────────────────────────────────────────────────────

    /// <summary>GPN kararını AppEvents üzerinden dashboard'a yayınlar (ızgara dışı).</summary>
    private static void PublishResilience(GpnResilienceEvent evt)
    {
        try
        {
            AppEvents.GpnResilienceChanged.Publish(evt);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Resilience yayını hatası: {ex.Message}");
        }
    }

    /// <summary>Aktif sunucunun UDP durumunu döndürür (udpResults'ta yoksa null).</summary>
    private static UdpProbeStatus? ResolveActiveUdpStatus(
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        string serverId,
        GpnProbeOptions options)
    {
        if (!options.EnableUdpHealth || !udpResults.TryGetValue(serverId, out var udp))
        {
            return null;
        }
        return udp.Status;
    }

    // ── Hairpin teşhisi ──────────────────────────────────────────────────

    /// <summary>
    /// Makinenin kendi genel IP'sini döndürür (hairpin teşhisi için). Öncelik:
    ///  1. Constructor'a enjekte edilen <c>ownPublicIpProvider</c> (testler sabit döner),
    ///  2. önbellekli HTTP çözücü (api.ipify.org → ifconfig.me → icanhazip), 60 sn TTL.
    /// Hiçbir uç nokta yanıt vermezse null — bu durumda hairpin tespiti sessizce devre dışı
    /// kalır (seçim asla ağ hatasıyla düşmez).
    /// </summary>
    private async Task<string?> ResolveOwnPublicIpAsync(CancellationToken cancellationToken)
    {
        if (_ownPublicIpProvider is not null)
        {
            return _ownPublicIpProvider();
        }

        var now = DateTime.UtcNow;
        if (_cachedOwnIp is not null && now - _ownIpUtc < TimeSpan.FromSeconds(OwnIpCacheSeconds))
        {
            return _cachedOwnIp;
        }

        foreach (var url in new[] { "https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com" })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(1500);
                var body = (await _ownIpHttp.GetStringAsync(url, cts.Token).ConfigureAwait(false)).Trim();
                if (IPAddress.TryParse(body, out _))
                {
                    _cachedOwnIp = body;
                    _ownIpUtc = DateTime.UtcNow;
                    return body;
                }
            }
            catch (OperationCanceledException)
            {
                // zaman aşımı — sonraki uç noktayı dene
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"[{Tag}] Kendi genel IP çözülemedi ({url}): {ex.Message}");
            }
        }
        Logging.SaveLog($"[{Tag}] Kendi genel IP'si hiçbir uç noktadan çözülemedi "
            + "(ipify→ifconfig.me→icanhazip) — hairpin teşhisi bu turda devre dışı.");
        return null;
    }

    /// <summary>
    /// Tünel kalıntısı olan "kendi genel IP"sini ayıklar: kendi IP'si önceki GPN
    /// oturumunda tünel İÇİNDEN çözülmüşse sunucunun kendi genel IP'si (örn.
    /// 92.4.220.236) önbelleğe yazılır; bağlantı sonrası o sunucu yanlışlıkla
    /// "hairpin" sanılıp atlanır (canlı gözlenen yanlış-seçim kaynağı). Çözülen IP
    /// etkin bir sunucunun uç noktasıyla birebir eşleşiyorsa güvenilir değildir —
    /// null döner, hairpin teşhisi o turda sessizce devre dışı kalır.
    /// </summary>
    private string? GuardOwnIpArtifact(string? ownIp, IReadOnlyList<GpnServerProfile> enabled)
    {
        if (ownIp is null)
        {
            return null;
        }

        var matchesServerEndpoint = enabled.Any(s =>
            string.Equals(s.EndpointHost, ownIp.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!matchesServerEndpoint)
        {
            return ownIp;
        }

        Logging.SaveLog($"[{Tag}] ownIp={ownIp} bir GPN sunucusunun uç noktasıyla eşleşiyor "
            + "(tünel kalıntısı) — hairpin teşhisi bu turda devre dışı.");
        return null;
    }

    /// <summary>
    /// Verilen adaylar arasındaki hairpin (öz-erişim, kendi genel IP'si) sunucuların
    /// kimliklerini döndürür. failover izleyicisi başlarken bir kez çağrılır.
    ///
    /// Önbellekleme: sonuç (sıfır küme dahil) aday-kümesi imzasıyla birlikte kendi-IP
    /// TTL'si (<see cref="OwnIpCacheSeconds"/> = 60 sn) boyunca saklanır. Aynı sunucu
    /// listesiyle gelen her izleyici başlangıcı önbelleği YENİDEN kullanır — ne HTTP
    /// çözümü ne candidate taraması tekrarlanır. Aday listesi değişirse anahtar değişir ve
    /// otomatik yenilenir.
    ///
    /// Hata davranışı: kendi genel IP'si hiçbir uç noktadan çözülemezse hairpin tespiti
    /// devre dışı kalır — boş küme döner VE bu durum TTL boyunca önbelleklenir (IP
    /// servisine döngüsel yük olmaz). Karar, hairpin-elenmeden normal seçim akışına
    /// döner: kendi IP'sine sahip sunucu aday/sunucu değişim hedefi olarak seçilebilir
    /// (öz-erişim el sıkışması yanıtı donanıma/yönlendiriciye bağlıdır). Açıkça loglanır.
    /// </summary>
    internal async Task<IReadOnlySet<string>> ResolveHairpinServerIdsAsync(
        IReadOnlyList<GpnServerProfile> candidates,
        CancellationToken cancellationToken)
    {
        var key = string.Join(",", candidates.Where(c => c.IsEnabled)
                                              .Select(c => c.ServerId)
                                              .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        if (_cachedHairpinIds is not null
            && _cachedHairpinKey == key
            && DateTime.UtcNow - _cachedHairpinUtc < TimeSpan.FromSeconds(OwnIpCacheSeconds))
        {
            return _cachedHairpinIds;
        }

        var ownIp = await ResolveOwnPublicIpAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(ownIp))
        {
            Logging.SaveLog($"[{Tag}] Kendi genel IP çözülemedi — hairpin (öz-erişim) tespiti devre dışı. "
                + "Aday/sunucu-değişimine kendi IP'li sunucu girebilir (el sıkışma yanıtı yönlendiriciye bağlıdır).");
            DiagLog.Write($"GPN_FAILOVER ownIp-unresolved → hairpin detection disabled (empty set — cached {OwnIpCacheSeconds}s)");

            // Sıfır küme de saklanır: başarısız çözüm TTL boyunca tekrarlanmaz.
            _cachedHairpinIds = ids;
            _cachedHairpinKey = key;
            _cachedHairpinUtc = DateTime.UtcNow;
            return ids;
        }

        foreach (var c in candidates)
        {
            if (c.IsEnabled && IsHairpin(c, ownIp))
            {
                ids.Add(c.ServerId);
                DiagLog.Write($"GPN_FAILOVER hairpin server={c.ServerId} endpoint={c.EndpointHost} ownIp={ownIp} — non-hairpin varken hedef değildir");
            }
        }

        _cachedHairpinIds = ids;
        _cachedHairpinKey = key;
        _cachedHairpinUtc = DateTime.UtcNow;
        return ids;
    }

    // ── Ölçüm çekirdeği ──────────────────────────────────────────────────

    /// <summary>Seçili modda tek sunucunun gidiş-dönüş süresini döndürür (ms; -1 = hata).</summary>
    private async Task<int> ProbeDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        var result = await ProbeServerAsync(server, options, cancellationToken).ConfigureAwait(false);
        return result.DelayMs;
    }

    /// <summary>Tek sunucu için tam ölçüm: N örnek, min/avg/max, kayıp oranı.</summary>
    private async Task<GpnServerProbeResult> ProbeServerAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        // TUN etkinken (EscapeTunnelForProbes) ölçüm fiziksel NIC üzerinden yapılır
        // (ICMP atlanır, gecikme UDP el sıkışma RTT'si) — dashboard bu bayrağı
        // kullanıcıya "tünel-içi değil, fiziksel ölçüm" ipucu olarak gösterir.
        var measuredOverPhysicalNic = options.EscapeTunnelForProbes && ProbeEgressNic.IsTunnelActive();
        try
        {
            var samples = new List<int>(options.Samples);
            for (var i = 0; i < options.Samples; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var delay = options.Mode switch
                {
                    GpnProbeMode.Icmp => await IcmpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false),
                    GpnProbeMode.Tcp => await TcpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false),
                    _ => await AutoDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false),
                };

                // 0 ms (loopback / çok hızlı LAN) geçerli bir ölçümdür; yalnızca
                // -1 (ölçüm başarısız) örnek dışı bırakılır.
                if (delay >= 0)
                {
                    samples.Add(delay);
                }

                // Örnekler arası kısa ara — ICMP rate-limit'ine takılmayı azaltır.
                if (i < options.Samples - 1)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }

            if (samples.Count == 0)
            {
                return new GpnServerProbeResult(server.ServerId, -1, -1, -1, 100, false, null, measuredOverPhysicalNic);
            }

            var loss = (int)Math.Round((options.Samples - samples.Count) * 100.0 / options.Samples);
            return new GpnServerProbeResult(
                server.ServerId,
                samples.Min(),
                (int)samples.Average(),
                samples.Max(),
                loss,
                true,
                null,
                measuredOverPhysicalNic);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Probe hatası: {server.Name}: {ex.Message}");
            return new GpnServerProbeResult(server.ServerId, -1, -1, -1, 100, false, ex, measuredOverPhysicalNic);
        }
    }

    private async Task<int> AutoDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        var icmp = await IcmpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false);
        // 0 ms geçerli bir ICMP ölçümüdür (loopback); yalnızca -1 (başarısız)
        // fallback'i tetikler.
        if (icmp >= 0)
        {
            return icmp;
        }

        // TUN etkinken ICMP atlandı (Ping arayüz bağlayamaz) ve TCP anlamsızdır
        // (WireGuard 51820 UDP-only) — ping yerine fiziksel NIC üzerinden UDP el
        // sıkışma round-trip'i ölçülür: geçerli bir handshake initiation'a yanıt
        // alan sunucu gerçek gecikme verir (dashboard "—" yerine değer görür).
        // Tünel yoksa eski TCP fallback'i sürer.
        if (options.EscapeTunnelForProbes && ProbeEgressNic.IsTunnelActive())
        {
            return await UdpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false);
        }

        return await TcpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ICMP yokken (TUN etkin — Ping arayüz bağlayamaz) gecikmeyi FİZİKSEL NIC
    /// üzerinden UDP el sıkışmasıyla ölçer. WireGuard sunucusu junk pakete yanıt
    /// vermediği için RTT yalnızca geçerli handshake initiation'a verilen yanıttan
    /// (handshake response / cookie reply) elde edilir; soket ProbeEgressNic ile
    /// fiziksel uplink'e bağlıdır — tünelin içine yakalanmaz, gerçek sunucu
    /// gecikmesi ölçülür. Anahtar yoksa veya yanıt yoksa -1 (ölçülemedi).
    /// </summary>
    private async Task<int> UdpDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server.ServerPublicKey) || string.IsNullOrWhiteSpace(server.ClientPrivateKey))
        {
            return -1;
        }

        try
        {
            var result = await _wireGuardProbe.ProbeAsync(
                server.ServerId, server.EndpointHost, server.EndpointPort,
                server.ServerPublicKey, server.ClientPrivateKey,
                new WireGuardHandshakeProbeOptions(
                    WaitTimeoutMs: Math.Max(300, options.PerSampleTimeoutMs),
                    MaxAttempts: 1),
                cancellationToken).ConfigureAwait(false);
            return result.Status == UdpProbeStatus.Open && result.RoundTripMs >= 0 ? result.RoundTripMs : -1;
        }
        catch (OperationCanceledException)
        {
            throw; // dış iptal — sonuç değil, yukarı taşınır
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] {server.Name} UDP gecikme ölçümü hatası: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// ICMP Echo isteği ile gidiş-dönüş ölçümü. DontFragment=true: WireGuard
    /// MTU 1420 planını yansıtan küçük paketle ölçüm yapılır (fragmantasyon
    /// gecikmesi ölçüme karışmaz).
    /// </summary>
    private static async Task<int> IcmpDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        // TUN etkinken ICMP tünele yakalanır; System.Net.NetworkInformation.Ping
        // arayüz bağlayamaz — fiziksel ölçüm imkânsız. EscapeTunnelForProbes açıksa
        // ICMP'yi atla; failover kararı UDP sağlık kanıtına (fiziksel NIC'ten) kalır.
        if (options.EscapeTunnelForProbes && ProbeEgressNic.IsTunnelActive())
        {
            return -1;
        }

        if (!IPAddress.TryParse(server.EndpointHost, out var address))
        {
            return -1;
        }

        using var ping = new Ping();
        var buffer = Encoding.ASCII.GetBytes("aogpn-gpn-probe-0123456789ab");
        var pingOptions = new PingOptions(64, true); // TTL 64, DontFragment
        var timeout = options.PerSampleTimeoutMs;

        var reply = await ping.SendPingAsync(
            address, timeout, buffer, pingOptions).ConfigureAwait(false);

        return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
    }

    /// <summary>
    /// TCP connect sondası: ICMP engellendiğinde devreye girer. Sunucunun
    /// 51820/udp portuna TCP connect başarılı olmayabilir (udp-only) — bu
    /// durumda -1 döner ve sunucu "erişilemez" sayılır; bu bir yanlış
    /// negatiftir, bu yüzden Auto modda yalnızca ICMP başarısız olduğunda
    /// denenecek son çaredir.
    /// </summary>
    private static async Task<int> TcpDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        // TUN etkinken TCP probe de tünele yakalanır; fiziksel NIC'e bağlansa bile
        // WireGuard 51820/udp portunda TCP dinleyici YOKTUR — hızlı vazgeç (ölçüm
        // UDP sağlık kanıtına kalır).
        if (options.EscapeTunnelForProbes && ProbeEgressNic.IsTunnelActive())
        {
            return -1;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.PerSampleTimeoutMs);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            // TUN etkinse soketi fiziksel NIC'e bağla — ICMP gibi tünele yakalanmasın
            // (tünel yoksa no-op).
            ProbeEgressNic.BindSocketEgress(client.Client, AddressFamily.InterNetwork);
            await client.ConnectAsync(server.EndpointHost, server.EndpointPort, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }
}
