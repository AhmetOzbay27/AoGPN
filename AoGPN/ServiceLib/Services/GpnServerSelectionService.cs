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

    // P0 Faz 2 (Karar Mekanizmasının Katmanlanması): bu sınıf artık yalnızca
    // orkestratördür — ölçüm GpnServerProber'a, hairpin teşhisi GpnHairpinDetector'a,
    // saf kararlar GpnDecision'a taşınmıştır. Durum (önbellek vb.) alt servislerde
    // yaşar; bu sınıf onları birbirine bağlar ve dışarıya sonuç döndürür.
    private readonly GpnServerProber _prober;
    private readonly GpnHairpinDetector _hairpin;

    // internal: alt servis tipleri (GpnServerProber/GpnHairpinDetector) internal
    // olduğu için ctor da internal'dır — kurulum DI (aynı assembly) veya testler
    // (InternalsVisibleTo) üzerinden yapılır.
    internal GpnServerSelectionService(
        IUdpHealthChecker? udpHealthChecker = null,
        IWireGuardHandshakeProbe? wireGuardProbe = null,
        Func<string?>? ownPublicIpProvider = null,
        GpnServerProber? prober = null,
        GpnHairpinDetector? hairpinDetector = null)
    {
        // DI uyumlu: checker'lar dışarıdan enjekte edilir; testlerde sahte (fake)
        // uygulamalar verilebilir. Belirtilmezse gerçek uygulamalar kullanılır.
        // ownPublicIpProvider hairpin teşhisi için makinenin kendi genel IP'sini
        // verir — testte sabit döner, üretimde önbellekli HTTP çözücü kullanılır.
        // prober/hairpinDetector DI'dan gelirse (AddAoGpnGpnServices) singleton
        // paylaşılır; verilmezse ctor argümanlarıyla burada kurulur.
        _prober = prober ?? new GpnServerProber(udpHealthChecker, wireGuardProbe);
        _hairpin = hairpinDetector ?? new GpnHairpinDetector(ownPublicIpProvider);
    }

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
                var prefOwnIp = _hairpin.GuardOwnIpArtifact(
                    await _hairpin.ResolveOwnPublicIpAsync(cancellationToken).ConfigureAwait(false),
                    enabled);
                var prefHairpinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (GpnHairpinDetector.IsHairpin(prefEnabled, prefOwnIp))
                {
                    prefHairpinIds.Add(prefEnabled.ServerId);
                }
                if (prefHairpinIds.Count > 0 && GpnDecision.HasNonHairpinAlternative(enabled, prefHairpinIds))
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
        var ownIpTask = _hairpin.ResolveOwnPublicIpAsync(cancellationToken);

        // ── Adım 1: Paralel ICMP ping (mevcut NodePingCoordinator altyapısı) ──
        var requests = enabled
            .Select(s => new NodePingRequest(s.ServerId, token => _prober.ProbeDelayMsAsync(s, options, token)))
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
        var udpAll = await _prober.ProbeUdpAllAsync(enabled, options, cancellationToken);
        var udpByServer = udpAll.ToDictionary(r => r.ServerId);

        GpnServerProfile? candidate = null;
        UdpProbeResult? udpProbe = null;

        // Hairpin (öz-erişim) teşhisi — ölçümle paralel çözülen kendi genel IP'si:
        // hedef sunucunun genel IP'si makinenin KENDİ genel IP'siyle eşleşiyorsa (tünel
        // içinden kendi sunucusuna hairpin NAT), o sunucu aday sırasının SONUNA atılır ve
        // diğer adaylar önceliklendirilir. Düşük ping'i yanıltıcıdır (tünel-içi rota).
        var ownIp = _hairpin.GuardOwnIpArtifact(await ownIpTask.ConfigureAwait(false), enabled);
        var hairpinIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in enabled)
        {
            if (!GpnHairpinDetector.IsHairpin(s, ownIp))
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
        var skipHairpin = GpnDecision.HasNonHairpinAlternative(enabled, hairpinIds);

        if (bestPing is not null)
        {
            // Ping sırası (ortak OrderCandidates): hairpin adaylar EN SONA (tünel-içi
            // öz-erişim el sıkışma almaz), sonra başarılı pingler önce, başarısız pingler
            // sonra — her aday için mod kararı; ilk WireGuardUDP-uygun (Tier 2) aday kazanır.
            var ordered = GpnDecision.OrderCandidates(enabled, results, hairpinIds);

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
                var m = GpnDecision.DecideMode(udp, check);
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
        var mode = GpnDecision.DecideMode(udpProbe, modeCheck);
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

    /// <inheritdoc cref="IGpnServerSelectionService.EvaluateSelection"/>
    public GpnSelectionPrediction EvaluateSelection(
        IReadOnlyList<GpnServerProfile> servers,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null,
        IReadOnlySet<string>? hairpinServerIds = null)
        => GpnDecision.DecideSelection(servers, pingResults, udpResults, options, hairpinServerIds);

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
        var ping = await _prober.ProbeServerAsync(server, options, cancellationToken).ConfigureAwait(false);
        var udp = await _prober.ProbeUdpAsync(server, options, cancellationToken).ConfigureAwait(false);

        var pingMs = ping.IsSuccess ? ping.DelayMs : -1;
        var check = options.UdpCheck;
        if (options.SlowServerToleranceMs > 0
            && check.TreatHandshakeNoResponseAsBlocked
            && pingMs >= options.SlowServerToleranceMs)
        {
            check = check with { TreatHandshakeNoResponseAsBlocked = false };
        }
        var mode = GpnDecision.DecideMode(udp, check);
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

        var defaultDecision = GpnDecision.DecideFailover(active, candidates, pingResults, udpResults, defaultOptions);
        var strictDecision = GpnDecision.DecideFailover(active, candidates, pingResults, udpResults, strictOptions);

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
                    udp is null || GpnDecision.IsUdpHealthyStatus(udp.Status, strictNoResponse: false, strictHandshake: true),
                    // "Katı" sütunu: ikisi de ölü.
                    udp is null || GpnDecision.IsUdpHealthyStatus(udp.Status, strictNoResponse: true, strictHandshake: true));
            })
            .OrderBy(r => r.IsActive ? 0 : 1)
            .ThenBy(r => r.PingMs)
            .ToArray();

        return new GpnFailoverMatrix(
            rows,
            GpnDecision.ToPolicy(defaultOptions, defaultDecision),
            GpnDecision.ToPolicy(strictOptions, strictDecision),
            DateTimeOffset.UtcNow);
    }

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
        var hairpinIds = await _hairpin.ResolveHairpinServerIdsAsync(candidates, cancellationToken).ConfigureAwait(false);

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
                var pingResults = await _prober.ProbeAllAsync(candidates, options, cancellationToken);

                // 2) UDP sağlık testleri (tüm adaylar, paralel) — tünel ölümünün kanıtı
                IReadOnlyDictionary<string, UdpProbeResult> udpResults;
                if (options.EnableUdpHealth)
                {
                    var udpList = await _prober.ProbeUdpAllAsync(candidates, options, cancellationToken);
                    udpResults = udpList.ToDictionary(r => r.ServerId);
                }
                else
                {
                    udpResults = new Dictionary<string, UdpProbeResult>();
                }

                // ── Kurtarma modu: V2rayTCP'ye düştük, sağlıklı sunucu dönünce Tier-2 ──
                if (recovering)
                {
                    var recoveryTarget = GpnDecision.DecideRecovery(candidates, pingResults, udpResults, options, hairpinIds);
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
                var decision = GpnDecision.DecideFailover(current, candidates, pingResults, udpResults, options, hairpinIds);
                switch (decision.Action)
                {
                    case GpnDecision.FailoverActionType.SwitchServer when decision.Target is not null:
                        // Ping-pong sıçrama koruması: henüz terk ettiğimiz sunucuya cooldown
                        // içinde geri dönmek istiyorsak, probe fluke'u say (tüneli yırtma) —
                        // bir sonraki çevrimde doğrulanınca geçiş yapılır. Bu, "bağlantı
                        // kendi kendini koparıyor" olarak görünen A↔B döngüsünü kırar.
                        if (GpnDecision.IsPingPongSwitchSuppressed(
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

                    case GpnDecision.FailoverActionType.FallbackToV2ray:
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

    // ── Ölçüm çekirdeği ──────────────────────────────────────────────────

    // ── Alt servis köprüleri (dış API'yi korur) ──────────────────────────

    public Task<IReadOnlyList<GpnServerProbeResult>> ProbeAllAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions? options = null,
        CancellationToken cancellationToken = default)
        => _prober.ProbeAllAsync(servers, options, cancellationToken);

    public Task<IReadOnlyList<UdpProbeResult>> ProbeUdpAllAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
        => _prober.ProbeUdpAllAsync(servers, options, cancellationToken);

    internal Task<UdpProbeResult> ProbeUdpAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
        => _prober.ProbeUdpAsync(server, options, cancellationToken);

    internal Task<IReadOnlySet<string>> ResolveHairpinServerIdsAsync(
        IReadOnlyList<GpnServerProfile> candidates,
        CancellationToken cancellationToken)
        => _hairpin.ResolveHairpinServerIdsAsync(candidates, cancellationToken);
}
