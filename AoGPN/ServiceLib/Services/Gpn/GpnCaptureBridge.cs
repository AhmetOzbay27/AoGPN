namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnCaptureBridge — yakalama tünel köprüsünün bağlantı-anı kompozisyonu
//
// GpnCaptureLoop'un VARSayılan enjeksiyon hattını (ConsumeInjectAsync — paketi
// sayaç olarak bırakır) WireGuardTunnelService.CreateInjectHandler ile
// değiştirir: recv-only aşamasında yakalanan oyun UDP paketleri tünele
// şifrelenip Wintun adaptörüne enjekte edilir. Ters yön (adaptörden gelen
// tünel çıkışı) çözülüp tüketiciye verilir.
//
//   GpnCaptureLoop (WinDivert recv-only)
//        │ inject = _tunnel.CreateInjectHandler()
//        ▼
//   WireGuardTunnelService.InjectPacket → IWireGuardTransport.Encrypt →
//   Wintun adaptörü
//
//   Wintun adaptörü → RunReceiveLoopAsync → Decrypt → tüketici (oyuna dönüş)
//
// Lifecycle, GpnCoreLauncher üzerinden koordinatörle birlikte yürür:
//   LaunchAsync(WireGuardUDP, server) → StartAsync(server)
//   StopAsync / düşüş / sunucu değişimi → StopAsync (idempotent)
//
// Hedef oyun çalışmıyorsa veya hedef ad listesi boşsa köprü BAŞLATILMAZ
// (false döner) — çekirdek bağlantısı (sing-box TUN) bozulmaz; oyun açılınca
// bir sonraki bağlantıda köprü canlıya alınır.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// WinDivert yakalama + WireGuard/Wintun tünel köprüsünü bağlantı anında kuran
/// orkestratör. Yakalanan her paket <see cref="WireGuardTunnelService.CreateInjectHandler"/>
/// hattından geçer; durdurma temiz (iptal + worker kapanışı + tünel close).
/// </summary>
public sealed class GpnCaptureBridge : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<string>> _targetNames;
    private readonly WinDivertEngine _engine;
    private readonly WireGuardTunnelService _tunnel;
    private readonly IGpnCaptureSettingsProvider _settings;
    private readonly IGpnWintunSettingsProvider _wintunSettings;
    private readonly IProcessTreeSource? _source;
    private readonly Func<byte[], CancellationToken, ValueTask>? _receiveConsumer;
    private readonly Func<GpnServerProfile, IWireGuardTransport> _transportFactory;
    private readonly ForeignTunnelDetector _foreignDetector;
    private readonly Func<IReadOnlyList<string>, int> _connectionFlusher;

    private readonly Func<string, Task>? _onFailure;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private GpnCaptureLoop? _loop;
    private Task? _loopTask;
    private Task? _receiveTask;
    private long _deliveredToConsumer;

    /// <param name="targetNames">
    /// Hedef oyun exe adlarını veren delege (ör. SplitTunnelViewModel'deki
    /// "vpn" eylemli uygulamalar). Boş dönerse köprü başlamaz.
    /// </param>
    /// <param name="engine">WinDivert yakalama motoru (testlerde sahte API).</param>
    /// <param name="tunnel">WireGuard/Wintun tünel servisi (inject hattının sahibi).</param>
    /// <param name="settings">
    /// Yakalama seçenekleri (kuyruk/katman/yön). Verilmezse AppManager
    /// config'inden okur (GpnCaptureSettingsProvider varsayılanı).
    /// </param>
    /// <param name="wintunSettings">
    /// Wintun adaptörü seçenekleri (adapter ad ön eki + halka tampon kapasitesi).
    /// Verilmezse AppManager config'inden okur (GpnWintunSettingsProvider varsayılanı).
    /// </param>
    /// <param name="source">
    /// Süreç ağacı kaynağı (varsayılan Toolhelp32) — testlerde sahte kaynak.
    /// </param>
    /// <param name="receiveConsumer">
    /// Tünelden dönen (çözülen) paketlerin tüketicisi — ör. oyun soketine geri
    /// enjeksiyon. Verilmezse yalnızca sayaç tutulur (telemetri).
    /// </param>
    /// <param name="transportFactory">
    /// Taşıma dikişi üreticisi — Faz 2c: varsayılan, profilin anahtarlarıyla gerçek
    /// WireGuard veri düzlemini (WireGuardNoiseTransport) kurar. Testler sahte
    /// taşıma vererek sürücüsüz/sunucusuz doğrulayabilir (Noop passthrough).
    /// </param>
    /// <param name="foreignDetector">
    /// Yabancı VPN durumu dedektörü — GPN akışı kendi tünelini açmadan ÖNCE dışarıdaki
    /// VPN durumunu (ör. resmi WireGuard uygulamasının wireguard.exe) tespit edip
    /// günlüğe düşer; üçüncü taraf istemciler ASLA kapatılmaz. Verilmezse gerçek
    /// dedektör kullanılır; testler sahte taramayla tespit akışını doğrular.
    /// </param>
    /// <param name="connectionFlusher">
    /// Bayat bağlantı temizleyici — tünel canlıya alındıktan SONRA hedef süreçlerin mevcut
    /// TCP bağlantılarını keser (yeniden tünel üzerinden kurulmaları için). Varsayılan,
    /// <see cref="NetworkConnectionFlusher.KillActiveConnectionsForProcesses"/> ile gerçek
    /// ağ yığınını temizler; testler sahte delege ile kayıt tutar.
    /// </param>
    /// <param name="onFailure">
    /// Köprü GERÇEK bir hatayla başlatılamadığında çağrılan bildirim geri çağrısı
    /// (Wintun açılışı, geçersiz WireGuard anahtarları, süreç/tarama hatası). Neden
    /// parametre olarak gelir; sessizce V2rayTCP'ye düşmek yerine kullanıcıya gösterilmek
    /// üzere verilir (ör. bir toast). "Hedef oyun seçilmedi/çalışmıyor" gibi tasarım gereği
    /// atlanan durumlarda çağrılmaz — yalnızca gerçek hatalarda. Best-effort'tur: geri
    /// çağrı hata verse bile köprü akışı bozulmaz.
    /// </param>
    public GpnCaptureBridge(
        Func<IReadOnlyList<string>> targetNames,
        WinDivertEngine engine,
        WireGuardTunnelService tunnel,
        IGpnCaptureSettingsProvider? settings = null,
        IGpnWintunSettingsProvider? wintunSettings = null,
        IProcessTreeSource? source = null,
        Func<byte[], CancellationToken, ValueTask>? receiveConsumer = null,
        Func<GpnServerProfile, IWireGuardTransport>? transportFactory = null,
        ForeignTunnelDetector? foreignDetector = null,
        Func<IReadOnlyList<string>, int>? connectionFlusher = null,
        Func<string, Task>? onFailure = null)
    {
        _targetNames = targetNames ?? throw new ArgumentNullException(nameof(targetNames));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
        _settings = settings ?? new GpnCaptureSettingsProvider();
        _wintunSettings = wintunSettings ?? new GpnWintunSettingsProvider();
        _source = source;
        _receiveConsumer = receiveConsumer;
        _transportFactory = transportFactory ?? BuildNoiseTransport;
        _foreignDetector = foreignDetector ?? new ForeignTunnelDetector();
        _onFailure = onFailure;
        // Varsayılan flusher Windows'a özgü bir API kullanır (WinSock/GetExtendedTcpTable);
        // CA1416'yı bastırmak için çağrı OperatingSystem.IsWindows() ile korunur. Windows
        // dışında ağ bağlantısı temizliği yoktur — 0 (hiçbir bağlantı kapatılmadı) döner.
        _connectionFlusher = connectionFlusher
            ?? (names => OperatingSystem.IsWindows()
                ? NetworkConnectionFlusher.KillActiveConnectionsForProcesses(names.ToList())
                : 0);
    }

    /// <summary>Yakalama köprüsü şu an çalışıyor mu (loop + tünel açık).</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _cts is not null;
            }
        }
    }

    /// <summary>Tünelden dönüp tüketiciye verilen (çözülen) paket sayısı.</summary>
    public long DeliveredToConsumer => Interlocked.Read(ref _deliveredToConsumer);

    /// <summary>Tünel telemetri anlık görüntüsü (dashboard köprüsü için).</summary>
    public GpnTunnelTelemetrySnapshot TunnelSnapshot => _tunnel.Snapshot();

    /// <summary>
    /// Köprüyü canlıya alır: hedef oyun çalışıyorsa tüneli açar ve yakalama
    /// döngüsünü tünelin inject hattıyla çalıştırır. Başlatılamadıysa false —
    /// çekirdek bağlantısı devam eder (köprü isteğe bağlıdır, hata ölümcül değildir).
    /// </summary>
    public async Task<bool> StartAsync(GpnServerProfile? server, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return true; // zaten çalışıyor — idempotent
            }
        }

        if (server is null)
        {
            DiagLog.Write("GPN_BRIDGE skip: sunucu yok (V2rayTCP modu)");
            return false;
        }

        IReadOnlyList<string> names;
        try
        {
            names = _targetNames();
        }
        catch (Exception ex)
        {
            var reason = $"Hedef ad listesi okunamadı: {ex.Message}";
            await ReportFailureAsync(reason).ConfigureAwait(false);
            return false;
        }

        if (names.Count == 0)
        {
            DiagLog.Write("GPN_BRIDGE skip: hedef oyun seçili değil (vpn eylemli uygulama yok)");
            return false;
        }

        var resolver = new GpnTargetResolver(names, _source);
        TargetPidSnapshot? initial;
        try
        {
            initial = resolver.Resolve(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ReportFailureAsync($"Süreç taraması başarısız: {ex.Message}").ConfigureAwait(false);
            return false;
        }

        if (initial is null)
        {
            DiagLog.Write("GPN_BRIDGE skip: hedef oyun çalışmıyor — oyunu başlatınca yeniden bağlanın");
            return false;
        }

        try
        {
            // Hangi hedef uygulamaların bulunduğunu oturum günlüğüne yaz — hangi
            // oyun exe'si bağlandı (PID havuzu), hangisi çalışmıyor, net görülsün.
            DiagLog.Write($"GPN_APP resolve targets=[{string.Join(",", names)}] pids=[{string.Join(",", initial.Pids)}]");

            // GPN akışı foreign-tunnel koruması: AOGPN kendi WireGuard tünelini açmadan
            // ÖNCE dışarıdaki yabancı VPN durumunu tespit edip günlüğe düşer. Üçüncü
            // taraf istemciler (resmi WireGuard uygulaması = wireguard.exe vb.) ASLA
            // kapatılmaz — iki Wintun/yol yığını çakışıp interneti öldürebileceği
            // raporlanır; tünel açılışı yine engellenmez (best-effort).
            DetectForeignTunnelBeforeConnect();

            // Faz 2c — gerçek WireGuard veri düzlemi: profildeki istemci özel +
            // sunucu genel anahtarlarıyla taşıma kurulur (el sıkışma durum makinesi +
            // oturum anahtarı türetme + ChaCha20-Poly1305 taşıma katmanı). Anahtarlar
            // bozuksa köprü başlamaz (çekirdek bağlantısı etkilenmez). Testler
            // transportFactory ile sahte taşıma verir (Noop passthrough).
            IWireGuardTransport transport;
            try
            {
                transport = _transportFactory(server);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                await ReportFailureAsync($"WireGuard anahtarları geçersiz: {ex.Message}").ConfigureAwait(false);
                return false;
            }
            _tunnel.UseTransport(transport);

            // Tünel + yakalama döngüsü (inject = tünel hattı). Adapter adı ve halka
            // tampon kapasitesi kullanıcı ayarlarından gelir (GpnWintunItem); ad
            // önekine sunucu kimliği eklenir (benzersiz adapter — sunucu değişiminde
            // çakışma olmaz).
            var wintun = _wintunSettings.Options;
            // Adaptör adı Wintun'un kurallarına uymalıdır: [A-Za-z0-9_-] dışına
            // çıkan karakterler (örn. host:port biçimindeki ServerId'deki iki nokta)
            // WintunCreateAdapter'da ERROR_INVALID_PARAMETER (87) ile reddedilir.
            // Kompozisyonu bir bütün olarak sanitleştir — hem sunucu kimliği hem
            // uzunluk sınırı güvende olur.
            var rawAdapterName = $"{wintun.AdapterName}-{server.ServerId}";
            var adapterName = GpnWintunSettingsMapper.SanitizeAdapterName(rawAdapterName);
            // Köprü açılış diyagnozu: üretilen ham ad + sanitleştirici sonucu ayrı
            // ayrı gpn-session.log'a düşer (GPN_BRIDGE → DiagLog → GpnSessionLog).
            // Ad Open'ta başarısız olsa bile bu satır yazılır — Wintun 87-regresyonunu
            // izlerken ham ':' içeren adla sanitleştirilmiş adı yan yana görürsünüz.
            DiagLog.Write($"GPN_BRIDGE adapter raw=\"{rawAdapterName}\" sanitized=\"{adapterName}\" ring={wintun.RingCapacity}");
            _tunnel.Open(adapterName, "AoGPN", wintun.RingCapacity);
            var loop = new GpnCaptureLoop(
                resolver,
                _engine,
                inject: _tunnel.CreateInjectHandler(),
                options: _settings.CaptureOptions);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_gate)
            {
                if (_cts is not null)
                {
                    // Yarış: başka bir iş parçacığı başlattı — açtığımızı temizle.
                    cts.Dispose();
                    _tunnel.Close();
                    return true;
                }
                _cts = cts;
                _loop = loop;
            }

            var token = cts.Token;
            _loopTask = Task.Run(() => loop.RunAsync(token), token);
            _receiveTask = Task.Run(() => _tunnel.RunReceiveLoopAsync(
                OnTunnelPacketAsync,
                TimeSpan.FromMilliseconds(250),
                token), token);

            // El sıkışmayı arka planda başlat — Bağlan akışını bloklamaz. Oturum
            // kurulana kadar Encrypt null döner (paketler injectFailed sayılır);
            // handshake bitince taşıma canlıya geçer. Görev hataları gözlenir.
            // Sahte taşıma (test) bu aşamayı atlar.
            if (transport is WireGuardNoiseTransport noiseTransport)
            {
                _ = ObserveTransportConnectAsync(noiseTransport, server, token);
            }

            // Görev hatalarını gözle (erken fault StopAsync'i beklemeden günlüğe düşer;
            // köprü zaten görevleri StopAsync'te de bekler).
            _ = ObserveFaultAsync(_loopTask, "yakalama döngüsü");
            _ = ObserveFaultAsync(_receiveTask, "alım döngüsü");

            // Bayat bağlantı temizliği: tünel canlıya alındıktan sonra hedef süreçlerin
            // (seçili oyun/"vpn" eylemli uygulamalar) tünel açılmadan ÖNCE kurulmuş mevcut
            // TCP bağlantılarını kes — yeni bağlantılar kendi tünelimiz üzerinden kurulsun.
            // Arka planda koşar (Bağlan akışını bloklamaz); kapanışta iptal edilir.
            _ = ObserveFaultAsync(
                FlushTargetConnectionsAsync(names, token),
                "bağlantı temizleyici");

            DiagLog.Write($"GPN_BRIDGE live server={server.ServerId} pids={initial.Pids.Length} adapter={_tunnel.AdapterName} ring={_tunnel.LastRingCapacity}");
            return true;
        }
        catch (Exception ex)
        {
            // Tünel/loop kurulumu başarısız (wintun.dll yok, yönetici yok vb.) —
            // köprü atlanır, çekirdek bağlantısı bozulmaz; neden kullanıcıya bildirilir.
            await ReportFailureAsync(ex.Message).ConfigureAwait(false);
            await StopCoreAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Köprü gerçek bir hatayla başlatılamadığında nedeni oturum günlüğüne yazar ve
    /// (<see cref="_onFailure"/>) geri çağrısına iletir — sessizce V2rayTCP'ye düşmek
    /// yerine kullanıcıya gösterilir. Best-effort: geri çağrı hata verse bile köprü akışı
    /// bozulmaz (kullanıcı yine de fallback'e gider).
    /// </summary>
    private async Task ReportFailureAsync(string reason)
    {
        Logging.SaveLog($"[GpnBridge] Köprü başlatılamadı: {reason}");
        if (_onFailure is null)
        {
            return;
        }
        try
        {
            await _onFailure(reason).ConfigureAwait(false);
        }
        catch
        {
            // Bildirim hatası köprü akışını bozmasın.
        }
    }

    /// <summary>
    /// GPN akışı foreign-tunnel koruması: kendi tünelini açmadan önce yabancı VPN
    /// durumunu (yabancı istemci süreçleri, unknown TUN adaptörü, dolu proxy portu)
    /// tespit edip diyagnoza/günlüğe düşürür. Üçüncü taraf VPN istemcileri ASLA
    /// kapatılmaz; tünel açılışı engellenmez (koruma best-effort, hata köprüyü düşürmez).
    /// </summary>
    private void DetectForeignTunnelBeforeConnect()
    {
        try
        {
            int localPort;
            try
            {
                localPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            }
            catch
            {
                localPort = 0; // proxy port çözülemezse yalnızca süreç/adaptör taraması yap
            }

            var detected = _foreignDetector.Detect(localPort);
            if (!detected.HasConflicts)
            {
                return; // temiz — kendi tünelini aç
            }

            DiagLog.Write($"GPN_BRIDGE foreign state: processes=[{string.Join(",", detected.ForeignProcessNames)}] adapters=[{string.Join(",", detected.TunAdapterNames)}] port={detected.PortOccupiedByForeignProcess}");
            Logging.SaveLog($"[GpnBridge] Yabancı VPN durumu tespit edildi (kapatılmadı): processes=[{string.Join(",", detected.ForeignProcessNames)}] adapters=[{string.Join(",", detected.TunAdapterNames)}] port={detected.PortOccupiedByForeignProcess}");

            // Yabancı adaptör kalıntısı açıkça diyagnoza düşer (yönlendirme riski gözden
            // kaçmasın) ama tünel açılışı devam eder — koruma best-effort'tur.
            if (detected.HasTunConflict)
            {
                DiagLog.Write($"GPN_BRIDGE foreign-adapter rapor: {string.Join(",", detected.TunAdapterNames)}");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[GpnBridge] Foreign-tunnel taraması hatası: {ex.Message}");
            // best-effort: tarama hatası köprüyü düşürmesin
        }
    }

    /// <summary>
    /// Köprüyü durdurur (idempotent): iptal → döngü/alım görevleri → loop
    /// dispose → tünel close. Sürücü handle'ları serbest bırakılır.
    /// </summary>
    public async Task StopAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
    }

    /// <summary>Varsayılan taşıma: profilin anahtarlarıyla gerçek WireGuard veri düzlemi.</summary>
    private static IWireGuardTransport BuildNoiseTransport(GpnServerProfile server)
    {
        var clientPrivate = Convert.FromBase64String(server.ClientPrivateKey.Trim());
        var serverPublic = Convert.FromBase64String(server.ServerPublicKey.Trim());
        return new WireGuardNoiseTransport(clientPrivate, serverPublic);
    }

    /// <summary>
    /// Hedef süreçlerin bayat TCP bağlantılarını keser (arka plan) — tünel canlıya
    /// alındıktan sonra mevcut soketlerin yeniden kurulması için. Gerçek flusher
    /// (<see cref="NetworkConnectionFlusher.KillActiveConnectionsForProcesses"/>) ağ
    /// yığınında DELETE_TCB atar; ağ erişimi tünele geçerken bayat bağlantılar eski
    /// yoldan sızıp IP'yi eski gösterir (tarayıcı yenilemesiz görülmez). Hatalar köprüyü
    /// düşürmez — temizlik best-effort'tur.
    /// </summary>
    private async Task FlushTargetConnectionsAsync(IReadOnlyList<string> targetNames, CancellationToken cancellationToken)
    {
        if (targetNames.Count == 0)
        {
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int killed;
        try
        {
            // Thread pool'e paşaların — ağ yığını taraması senkron ve hızlıdır.
            killed = await Task.Run(() => _connectionFlusher(targetNames), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // kapanış — tamamlanmadan iptal
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[GpnBridge] Bağlantı temizliği hatası: {ex.Message}");
            return;
        }
        sw.Stop();
        Logging.SaveLog($"[GpnBridge] Bayat bağlantı temizliği: {killed} bağlantı kesildi ({sw.ElapsedMilliseconds}ms) targets=[{string.Join(",", targetNames)}]");
        DiagLog.Write($"GPN_BRIDGE flush killed={killed} ms={sw.ElapsedMilliseconds} targets=[{string.Join(",", targetNames)}]");
    }

    /// <summary>
    /// El sıkışmayı sunucuya karşı tamamlar (arka plan). Başarısızlık günlüğe düşer
    /// — köprü ayakta kalır, oturum gelmeyince paketler injectFailed sayılır
    /// (handshake-no-response teşhisiyle aynı dil).
    /// </summary>
    private static async Task ObserveTransportConnectAsync(
        WireGuardNoiseTransport transport, GpnServerProfile server, CancellationToken ct)
    {
        try
        {
            var ok = await transport.ConnectAsync(server.EndpointHost, server.EndpointPort, ct).ConfigureAwait(false);
            DiagLog.Write(ok
                ? $"GPN_TUNNEL handshake ok server={server.ServerId}"
                : $"GPN_TUNNEL handshake failed server={server.ServerId} (oturum yok — paketler injectFailed sayılır)");
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[GpnBridge] El sıkışma hatası: {ex.Message}");
        }
    }

    /// <summary>Tünelden gelen paket: çözülmüş halde tüketiciye (sayaç + isteğe bağlı consumer).</summary>
    private async ValueTask OnTunnelPacketAsync(byte[] packet, CancellationToken ct)
    {
        Interlocked.Increment(ref _deliveredToConsumer);
        if (_receiveConsumer is not null)
        {
            await _receiveConsumer(packet, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Köprü görevlerindeki erken hataları yakalar (sessiz fault önlenir).</summary>
    private static async Task ObserveFaultAsync(Task task, string what)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[GpnBridge] {what} görevi hata verdi: {ex}");
            DiagLog.Write($"GPN_BRIDGE {what} fault: {ex.Message}");
        }
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cts;
        GpnCaptureLoop? loop;
        Task? loopTask;
        Task? receiveTask;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            loop = _loop;
            _loop = null;
            loopTask = _loopTask;
            receiveTask = _receiveTask;
            _loopTask = null;
            _receiveTask = null;
        }

        if (cts is null)
        {
            return; // zaten durmuş
        }

        cts.Cancel();
        try
        {
            await Task.WhenAll(loopTask ?? Task.CompletedTask, receiveTask ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[GpnBridge] Kapanışta hata: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
        }

        // Uygulama bazlı oturum özeti: hangi uygulamalar trafik taşıdı (başarılı),
        // hangileri boş kaldı (başarısız) + tünelin toplam başarı/başarısızlık
        // sayaçları. Loop telemetrisi hâlâ erişilebilirken (dispose öncesi) okunur.
        LogAppSummary();

        if (loop is not null)
        {
            await loop.DisposeAsync().ConfigureAwait(false);
        }
        _tunnel.Close();
        DiagLog.Write("GPN_BRIDGE stopped");
    }

    /// <summary>
    /// Bağlantı sonunda uygulama bazlı özeti oturum günlüğüne yazar: yakalanan her
    /// PID için süreç adı + paket/bayt (paket > 0 = tünelden trafik akıyordu = başarılı,
    /// 0 = o uygulama bağlanmadı) ve tünelin toplam sent/sendFailed/injectFailed/
    /// decryptFailed sayaçları (başarısızlık = hangi paketler tünele giremedi).
    /// </summary>
    private void LogAppSummary()
    {
        try
        {
            GpnCaptureLoop? loop;
            lock (_gate)
            {
                loop = _loop;
            }
            if (loop is null)
            {
                return;
            }

            var stats = loop.Telemetry.Snapshot;
            var tunnel = _tunnel.Snapshot();

            foreach (var p in stats.ByPid)
            {
                var name = ResolveProcessName(p.Pid);
                DiagLog.Write($"GPN_APP stats pid={p.Pid} name={name} packets={p.Packets} bytes={p.Bytes} "
                    + $"inPool={p.InPool} status={(p.Packets > 0 ? "ok" : "no-traffic")}");
            }

            DiagLog.Write($"GPN_APP tunnel captured={tunnel.Captured} injected={tunnel.Injected} "
                + $"injectFailed={tunnel.InjectFailed} sent={tunnel.Sent} sendFailed={tunnel.SendFailed} "
                + $"decryptFailed={tunnel.DecryptFailed} status={(tunnel.SendFailed == 0 && tunnel.InjectFailed == 0 ? "ok" : "failed")}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[GpnBridge] Uygulama özeti hatası: {ex.Message}");
        }
    }

    /// <summary>Süreç kimliğini exe adına çevirir (süreç kapanmışsa pid olarak kalır).</summary>
    private static string ResolveProcessName(uint pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            return name.Length > 0 ? name + ".exe" : $"pid-{pid}";
        }
        catch
        {
            return $"pid-{pid}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
        await Task.CompletedTask;
    }
}
