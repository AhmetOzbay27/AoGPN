namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnCaptureLoop — PID filtresi + iki aşamalı yakalama döngüsü
//
//   GpnTargetResolver (5 sn) → PID kümesi
//         │ değiştiyse WinDivertFilterBuilder ile YENİ filtre derlenir, handle
//         ▼ değiştirilir (yakalama kesintisiz — yeni worker aynı kanaldan okur)
//
//   Faz 1 (VARSayılan — sniff): WinDivertEngine.OpenSniff (OpenEx + WINDIVERT_FLAG_SNIFF)
//     ile paketleri YAKALA VE BIRAK. Sniff modunda paketler kopyalanıp kullanıcıya
//     verilir AMA ağ yığınında ilerlemeye devam eder — trafik bozulmaz. Bu aşamada
//     asla enjeksiyon yapılmaz (sniff'e geri gönderim paket çoğaltır); yalnızca
//     gözlem sayacı tutulur. VerifyPacketCount paket görüldüğünde (veya SniffTimeout
//     dolduğunda) doğrulama tamamlanır.
//
//   Faz 2 (recv-only): WinDivertEngine.OpenRecvOnly (OpenEx + WINDIVERT_FLAG_RECV_ONLY)
//     ile gerçek yakalama — paketler yığından çıkarılır ve YALNIZCA okunur; geri
//     enjekte edilmez (recv-only handle'da WinDivertSend başarısız olur). Yakalanan
//     paketler _inject hattına pompalanır: varsayılan tüketim (consume) — tünel
//     şifrelemesi (Faz 2b WireGuardTunnelService) bu paketleri şifreleyip tünel
//     soketine verir; iskelet aşamasında paket sayaç olarak bırakılır.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>Yakalama aşaması: sniff (yakala-bırak) veya recv-only (tünele al).</summary>
public enum GpnCaptureMode
{
    /// <summary>Paketler kopyalanır ama yığında ilerler — güvenli doğrulama.</summary>
    Sniff,

    /// <summary>Paketler yığından çıkarılır ve yalnızca okunur — tünel egress'i.</summary>
    RecvOnly,
}

/// <summary>
/// GpnCaptureLoop davranış seçenekleri. Varsayılan: önce sniff ile paketleri
/// yakala-bırak ve doğrula, ardından recv-only'ye geç (OpenEx tabanlı).
/// </summary>
public sealed record GpnCaptureOptions
{
    /// <summary>true (varsayılan): her döngü sniff aşamasıyla başlar, doğrulama sonrası recv-only'ye geçer.</summary>
    public bool SniffFirst { get; init; } = true;

    /// <summary>Sniff aşamasında kaç paket görüldüğünde recv-only'ye geçileceği. 0 = yalnızca zaman aşımıyla geç.</summary>
    public int VerifyPacketCount { get; init; } = 1;

    /// <summary>Sniff aşamasının en fazla sürebileceği süre (paket gelmezse de recv-only'ye geçilir). &lt;=0 = sonsuz.</summary>
    public TimeSpan SniffTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>WinDivertOpenEx kuyruk parametreleri (katman/yön bazlı QueueLen/Time/Size).</summary>
    public WinDivertOpenParams OpenParams { get; init; } = WinDivertOpenParams.Default;

    /// <summary>Sniff/recv-only bayraklarına eklenecek ek WinDivert bayrakları (ör. FlagUseSequenceNumbers).</summary>
    public ulong ExtraFlags { get; init; }

    /// <summary>
    /// Filtreden hariç tutulacak hedef IP (ör. WireGuard sunucu uç noktası).
    /// Filtre NETWORK katmanında süreç tanımadığı için geniş (tüm outbound UDP)
    /// kurulur; kendi şifreli tünel egress'imizin geri yakalanıp sonsuz döngüye
    /// girmemesi için bu uç nokta ZORUNLU hariç tutulur (bkz. WinDivertFilterBuilder).
    /// </summary>
    public string? ExcludedDstHost { get; init; }

    /// <summary>ExcludedDstHost ile birlikte kullanılır (WireGuard portu, ör. 51820).</summary>
    public int? ExcludedDstPort { get; init; }

    /// <summary>Paket başına telemetri sayaçları + AppEvents.GpnCaptureStatsChanged akışı (varsayılan açık).</summary>
    public bool EnableTelemetry { get; init; } = true;

    /// <summary>Telemetri anlık görüntüsünün yayınlanma aralığı (port tablosu da burada tazelenir).</summary>
    public TimeSpan TelemetryTickInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed class GpnCaptureLoop : IAsyncDisposable
{
    private readonly GpnTargetResolver _resolver;
    private readonly WinDivertEngine _engine;
    private readonly Func<DivertedPacket, CancellationToken, ValueTask> _inject;
    private readonly GpnCaptureOptions _options;
    private readonly IGpnPortPidTable _portPidTable;
    private readonly EventChannel<GpnCaptureStatsSnapshot> _statsChannel;
    private readonly GpnCaptureTelemetry _telemetry = new();
    private readonly object _gate = new();
    private DivertWorker? _worker;
    private TargetPidSnapshot? _openedSnapshot;
    private GpnCaptureMode _currentMode = GpnCaptureMode.RecvOnly;
    private int _sniffedPackets;
    private int _consumedPackets;
    private long _reinjectedPackets;
    private volatile IReadOnlyDictionary<ushort, uint> _portPidMap = EmptyPortPidMap;
    private volatile IReadOnlySet<uint> _poolPids = EmptyPool;

    private static readonly IReadOnlyDictionary<ushort, uint> EmptyPortPidMap = new Dictionary<ushort, uint>();
    private static readonly IReadOnlySet<uint> EmptyPool = new HashSet<uint>();

    /// <param name="inject">
    /// Yakalanan paketin enjeksiyon hattı (yalnızca recv-only aşamasında çağrılır).
    /// Verilmezse <see cref="ConsumeInjectAsync"/> — paket tüketilir (tünel egress'i
    /// Faz 2b'de bu hattı şifreleyip tünel soketine bağlar; iskelet sayaç tutar).
    /// </param>
    /// <param name="portPidTable">
    /// UDP port→PID köprüsü (per-PID telemetri atfı). Verilmezse IP Helper tablosu.
    /// </param>
    /// <param name="statsChannel">
    /// Telemetri yayın kanalı. Verilmezse <see cref="AppEvents.GpnCaptureStatsChanged"/>;
    /// testler izole kanal enjekte eder.
    /// </param>
    public GpnCaptureLoop(
        GpnTargetResolver resolver,
        WinDivertEngine engine,
        Func<DivertedPacket, CancellationToken, ValueTask>? inject = null,
        GpnCaptureOptions? options = null,
        IGpnPortPidTable? portPidTable = null,
        EventChannel<GpnCaptureStatsSnapshot>? statsChannel = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _inject = inject ?? ConsumeInjectAsync;
        _options = options ?? new GpnCaptureOptions();
        _portPidTable = portPidTable ?? new GpnPortPidTable();
        _statsChannel = statsChannel ?? ServiceLib.Events.AppEvents.GpnCaptureStatsChanged;
    }

    /// <summary>Paket başına telemetri sayaçları (döngü çalışırken canlı okunur).</summary>
    public GpnCaptureTelemetry Telemetry => _telemetry;

    /// <summary>Sniff aşamasında gözlemlenen (yakala-bırak) paket sayısı.</summary>
    public int SniffedPacketCount => Volatile.Read(ref _sniffedPackets);

    /// <summary>Recv-only aşamasında enjeksiyon hattına verilen (hedef süreç) paket sayısı.</summary>
    public int ConsumedPacketCount => Volatile.Read(ref _consumedPackets);

    /// <summary>Recv-only aşamasında hedef DIŞI sayılıp yığına geri enjekte edilen paket sayısı.</summary>
    public long ReinjectedPacketCount => Interlocked.Read(ref _reinjectedPackets);

    /// <summary>
    /// 5 sn'lik PID tazeleme + filtre yeniden derleme + enjeksiyon döngüsü.
    /// Varsayılan akış: önce sniff (yakala-bırak + doğrulama), sonra recv-only.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var initial = _resolver.Resolve(cancellationToken);
        if (initial is null)
        {
            throw new InvalidOperationException("Hedef süreç çalışmıyor — PID kümesi boş.");
        }

        // Faz 1 (varsayılan): sniff — paketleri yakala ve bırak, doğrula.
        // Trafiği bozmadan filtrenin doğru PID'leri yakaladığı teyit edilir.
        if (_options.SniffFirst)
        {
            await SniffAndVerifyAsync(initial, cancellationToken);
        }

        // Faz 2: recv-only — gerçek yakalama; paketler tünel egress'ine pompalanır.
        OpenAndStart(initial, GpnCaptureMode.RecvOnly, cancellationToken);

        _telemetry.Reset();
        // Oturum-içi yardımcı görevler (PID tazeleme + telemetri) iptale kadar
        // SONSUZDUR. Pump fault'unda onları beklemek, görevin fault'unu maskeler
        // (görev asla fault olmaz → ObserveFaultAsync → EngineFailed bildirimi
        // yalnızca kapanışta tetiklenirdi). Ayrı bağlantılı iptal taşırlar: pump
        // hatası loopCts'yi iptal eder → yardımcı görevler hızla biter → gerçek
        // istisna yüzeye anında çıkar (Tier 1 — yaşam döngüsü köprüsü).
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var refreshTask = RefreshLoopAsync(loopCts.Token);
        Task? telemetryTick = _options.EnableTelemetry ? TelemetryTickAsync(loopCts.Token) : null;
        try
        {
            await PumpAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
        catch (Exception)
        {
            // Pump fault: yardımcı döngüleri bitir — finally onları beklerken
            // fault maskelenmesin (EngineFailed kapanışa kalmasın).
            loopCts.Cancel();
            throw;
        }
        finally
        {
            await refreshTask;
            if (telemetryTick is not null)
            {
                await telemetryTick; // içeride OCE zaten yutulur
            }
            await CloseAsync();
        }
    }

    /// <summary>
    /// Sniff aşaması: OpenSniff ile yakala-bırak. Enjeksiyon YAPILMAZ (sniff'e geri
    /// gönderim paket çoğaltır); yalnızca gözlem sayacı artar. <see cref="GpnCaptureOptions.VerifyPacketCount"/>
    /// paket görülünce veya <see cref="GpnCaptureOptions.SniffTimeout"/> dolunca aşama biter.
    /// </summary>
    private async Task SniffAndVerifyAsync(TargetPidSnapshot snapshot, CancellationToken cancellationToken)
    {
        OpenAndStart(snapshot, GpnCaptureMode.Sniff, cancellationToken);
        using var sniffCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.SniffTimeout > TimeSpan.Zero)
        {
            sniffCts.CancelAfter(_options.SniffTimeout);
        }

        try
        {
            var worker = GetCurrentWorker();
            if (worker is null)
            {
                return;
            }
            await foreach (var packet in worker.GetPacketsAsync(sniffCts.Token))
            {
                try
                {
                    Interlocked.Increment(ref _sniffedPackets);
                    if (_options.VerifyPacketCount > 0
                        && Volatile.Read(ref _sniffedPackets) >= _options.VerifyPacketCount)
                    {
                        break; // yeterli kanıt — recv-only'ye geç
                    }
                }
                finally
                {
                    packet.Release(); // sniff yalnızca sayaç tutar — tampon hemen havuzda
                }
            }
        }
        catch (OperationCanceledException)
        {
            // zaman aşımı (paket gelmedi) veya dış iptal — yine de recv-only'ye geçilir
        }
        finally
        {
            await CloseAsync();
        }
    }

    /// <summary>Recv-only aşamasındaki yakalanan paketleri enjeksiyon hattına sürekli pompalar.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var worker = GetCurrentWorker();
            if (worker is null)
            {
                return; // kapatıldı
            }

            // Worker değiştiğinde (filtre yeniden derleme) eski kanal tamamlanır →
            // döngü _current'i yeniden okur ve yeni worker'dan devam eder.
            await foreach (var packet in worker.GetPacketsAsync(cancellationToken))
            {
                try
                {
                    // Net katmanı filtre içinde süreç tanımadığı için (processId yok —
                    // hata 87) paket burada ROTALANIR: hedef süreç → tünel egress,
                    // hedef dışı → yığına geri enjeksiyon. Telemetri her iki yol için
                    // kaydedilir (havuz içi / kaçan trafik işareti).
                    await RoutePacketAsync(packet, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    packet.Release();
                }
            }
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshLoopCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // normal kapanış (iptal — pump fault'unda loopCts iptali dahil)
        }
    }

    // Açılış penceresi: bağlantı kurulurken oyun henüz başlamamış olabilir — ilk
    // tazeleme tik'i kısa aralıkla atılır (5 sn yerine) ki launcher'ın açtığı oyun
    // süreci ilk paketlerini kaçırmadan filtreye girsin. PID kümesi değişmediyse
    // tik sessizdir (worker yeniden derlenmez).
    private static readonly TimeSpan FirstRefreshInterval = TimeSpan.FromMilliseconds(750);

    private async Task RefreshLoopCoreAsync(CancellationToken cancellationToken)
    {
        await foreach (var snapshot in _resolver.RefreshLoopAsync(
            firstTickInterval: FirstRefreshInterval,
            cancellationToken: cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Gerçek GpnTargetResolver, RefreshLoopAsync'te İLK anlık görüntüyü de
            // üretir (açılışta kullanılanla AYNI PID kümesi). Burada handle'ı kapatıp
            // yeniden açmak, az önce açılan worker'ın kanalına yazılmış paketleri
            // düşürür — köprü bağlantı anında ilk paketleri yarışta kaybeder. Yalnızca
            // PID kümesi GERÇEKTEN değiştiyse filtre yeniden derlenir ve worker taşınır.
            TargetPidSnapshot? opened;
            lock (_gate)
            {
                opened = _openedSnapshot;
            }
            if (opened is not null && snapshot.HasSamePids(opened))
            {
                continue;
            }

            await CloseAsync();
            OpenAndStart(snapshot, _currentMode, cancellationToken);
        }
    }

    private void OpenAndStart(TargetPidSnapshot snapshot, GpnCaptureMode mode, CancellationToken cancellationToken)
    {
        _currentMode = mode;
        _poolPids = snapshot.PidSet.ToHashSet();
        lock (_gate)
        {
            _openedSnapshot = snapshot;
        }
        // Filtrede süreç yok (NETWORK katmanı processId tanımaz — hata 87): geniş
        // outbound-UDP + zorunlu kendi-tünel-egress dışlaması. PID ayrımı kullanıcı
        // modunda RoutePacketAsync'te yapılır (port→PID tablosu).
        var filter = WinDivertFilterBuilder.BuildFilter(
            excludedDstHost: _options.ExcludedDstHost,
            excludedDstPort: _options.ExcludedDstPort);
        switch (mode)
        {
            case GpnCaptureMode.Sniff:
                _engine.OpenSniff(filter, _options.OpenParams, extraFlags: _options.ExtraFlags);
                break;
            default:
                // Düz divert (flags=0): Send ETKİN — hedef dışı paketler geri enjekte
                // edilebilir (RecvOnly WinDivertSend'i devre dışı bırakırdı).
                _engine.OpenDivert(filter, _options.OpenParams, extraFlags: _options.ExtraFlags);
                break;
        }
        var worker = _engine.StartCapture(cancellationToken);
        // İlk paket için bile port→PID haritası hazır olsun — telemetri kapalıyken
        // (veya ilk tik gelene kadar) hedef paketler yanlışlıkla geri enjekte edilmesin.
        RefreshPortMap();
        lock (_gate)
        {
            _worker = worker;
        }
    }

    private DivertWorker? GetCurrentWorker()
    {
        lock (_gate)
        {
            return _worker;
        }
    }

    /// <summary>Worker'ı durdurur ve handle'ı kapatır (driver filtresini serbest bırakır).</summary>
    private async Task CloseAsync()
    {
        DivertWorker? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
        }
        if (worker is not null)
        {
            await worker.StopAsync().ConfigureAwait(false);
        }
        if (_engine.IsOpen)
        {
            _engine.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var worker = GetCurrentWorker();
        if (worker is not null)
        {
            await CloseAsync();
        }
        else if (_engine.IsOpen)
        {
            _engine.Close();
        }
    }

    /// <summary>
    /// Varsayılan enjeksiyon (recv-only): paket tüketilir — WinDivertSend bu handle'da
    /// başarısız olur; tünel egress'i (Faz 2b) bu hattı şifreleyip tünel soketine bağlar.
    /// </summary>
    private ValueTask ConsumeInjectAsync(DivertedPacket packet, CancellationToken ct)
    {
        Interlocked.Increment(ref _consumedPackets);
        return ValueTask.CompletedTask;
    }

    // ── per-packet rota + telemetri ───────────────────────────────────────

    /// <summary>
    /// Paketi ROTALAR: IP başlığı çözülür, dışa giden UDP paketinin kaynak portu
    /// UDP port→PID tablosuyla sahibine atfedilir; sahibi yakalama havuzundaysa
    /// (hedef oyun/uygulama) paket tünel enjeksiyon hattına verilir, değilse ya da
    /// sahibi çözülemiyorsa yığına GERİ ENJEKTE edilir (kaçan trafik yutulmaz).
    /// Ağ katmanında WinDivert PID vermez — port köprüsü "hangi PID'in trafiği
    /// yakalanıyor / havuz dışı trafik var mı" teşhisinin TEK kaynağıdır.
    /// Telemetri her iki yol için kaydedilir (havuz içi / kaçan işareti).
    /// Tier 4: havuzlanmış tamponun YALNIZCA mantıksal uzunluğu işlenir (Length),
    /// kapasitesi değil — dilim boşluğu asla telemetriye/enjeksiyona girmez.
    /// </summary>
    private async ValueTask RoutePacketAsync(DivertedPacket packet, CancellationToken cancellationToken)
    {
        var stats = GpnPacketStats.FromPacket(packet.Data.AsSpan(0, packet.Length), packet.Address.IsOutbound);
        uint? pid = null;
        var inPool = false;
        if (stats.Protocol == GpnPacketProtocol.Udp && stats.LocalPort != 0
            && _portPidMap.TryGetValue(stats.LocalPort, out var owner))
        {
            pid = owner;
            inPool = _poolPids.Contains(owner);
        }

        if (_options.EnableTelemetry)
        {
            _telemetry.Record(stats, pid, inPool);
        }

        if (inPool)
        {
            // Hedef süreç paketi → tünel egress. _inject Data'yı await edilen çağrı
            // içinde senkron tüketir (Encrypt aşaması) — dönüşte tampon havuza döner.
            await _inject(packet, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ReinjectPacket(packet);
        }
    }

    /// <summary>
    /// Hedef dışı (veya sahibi çözülemeyen) paketi aynı handle üzerinden yığına
    /// geri enjekte eder — geniş filtre yalnızca GÖZLEM için değil, yakalama için
    /// kurulduğundan, geri enjeksiyon olmadan diğer uygulamaların UDP trafiği
    /// (DNS, tarayıcı QUIC, sesli sohbet) yutulurdu. Enjeksiyon hatası paketi
    /// düşürür ama döngüyü fault ettirmez (kapanış yarışı vb. — best-effort).
    /// </summary>
    private void ReinjectPacket(DivertedPacket packet)
    {
        if (!_engine.IsOpen)
        {
            return; // kapanış anı — paket zaten yığından çıkarıldı, düşür
        }
        try
        {
            _engine.Send(packet.Data.AsSpan(0, packet.Length), packet.Address);
            Interlocked.Increment(ref _reinjectedPackets);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[GpnCaptureLoop] Geri enjeksiyon hatası: {ex.Message}");
        }
    }

    /// <summary>UDP port→PID haritasını tazeler (açılışta + telemetri tikinde; erişilemezse eski harita korunur).</summary>
    private void RefreshPortMap()
    {
        try
        {
            _portPidMap = _portPidTable.GetUdpPortOwners();
        }
        catch
        {
            // erişilemezse eski harita korunur
        }
    }

    /// <summary>
    /// Telemetri tik döngüsü: her aralıkta UDP port→PID haritasını tazeler ve
    /// mevcut anlık görüntüyü dashboard kanalına yayınlar (paket gelmese de
    /// "henüz trafik yok" durumu yansır). İptalde sessizce biter.
    /// </summary>
    private async Task TelemetryTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                RefreshPortMap();
                _statsChannel.Publish(_telemetry.Snapshot);
                await Task.Delay(_options.TelemetryTickInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
    }
}