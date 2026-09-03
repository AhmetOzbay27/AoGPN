namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnAppEndpointStore — GPN uygulamalarının gerçek sunucu uç noktaları +
// "gerçek ping" ölçümü
//
// GPN kullanımında tünellenen uygulamaların bağlandığı uzak adresler (oyun
// sunucusu IP:port) gpn_app_endpoints tablosuna kaydedilir. Kayıt, bağlantı
// monitöründen beslenir (SplitTunnelViewModel): uygulama çalışıp genel bir
// adrese bağlandığında Observe çağrılır. Gözlemler bellekte biriktirilir ve
// periyodik (varsayılan 10 sn) veya kapasite dolunca tek SQL upsert ile diske
// yazılır — paket başına DB yazımı yok, kayıt yolu yalnızca sözlük güncellemesi.
//
// Ölçüm (MeasureRealPingAsync) iki zaman noktasında koşar:
//   1. Program açılışı → doğrudan yol ("önce"): uygulama sunucusuna VPN'siz ping
//   2. GPN bağlantısı kurulunca → tünel yolu ("sonra"): aynı uç noktalara ping
// Aynı uç nokta kümesi olduğu için fark gerçek iyileşmeyi gösterir. Ölçümde
// önce ICMP denenir (DNS çözümüyle); ICMP engelli veya TUN etkinken yanıtsız
// TCP uç noktalarında TCP connect gecikmesi fallback olarak kullanılır.
// (GpnServerSelectionService'in sunucu ölçüm deseninin aynısı — o tüneli
// ATLAMAYA çalışır, bu servis tünel YOLUNU ölçmek ister; ICMP tünel etkinken
// TUN üzerinden gider, TCP connect de öyle.)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>Tek uygulamanın gerçek ping ölçümü sonucu.</summary>
public sealed record GpnAppRealPingResult(
    string AppName,
    int BestMs,
    int OkEndpoints,
    int TotalEndpoints)
{
    /// <summary>Ölçüm yapılabildi mi (en az bir uç nokta yanıt verdi).</summary>
    public bool IsMeasured => BestMs >= 0;
}

/// <summary>
/// GPN uygulamalarının sunucu uç noktalarını kaydeder (bellek + SQLite) ve
/// gerçek ping ölçümünü yürütür. Tek örnek (Instance) uygulama boyunca paylaşılır;
/// gözlem yolu pahalı değildir, flush periyodiktir.
/// </summary>
public sealed class GpnAppEndpointStore : IDisposable
{
    public const int DefaultMaxEndpointsPerApp = 6;

    /// <summary>Saklama süresi — bu yaştan eski uç noktalar budanır.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(45);

    /// <summary>Gözlemlerin diske yazılma aralığı.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(10);

    /// <summary>Ölçümde tek uç noktaya verilen azami süre (ICMP/TCP).</summary>
    public static readonly TimeSpan PingSampleTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>Tek uç noktanın azami toplam ölçüm süresi (koordinatör zaman aşımı).</summary>
    public static readonly TimeSpan PingCoordinatorTimeout = TimeSpan.FromMilliseconds(2000);

    /// <summary>Flush kuyruğu bu boyuta ulaşınca periyot beklenmeden diske yazılır.</summary>
    private const int PendingFlushThreshold = 100;

    private static readonly Lazy<GpnAppEndpointStore> _instance = new(() => new());
    public static GpnAppEndpointStore Instance => _instance.Value;

    private readonly NodePingCoordinator _coordinator = new(8);
    private readonly TimeSpan _retention;
    private readonly TimeSpan _flushInterval;
    private readonly Func<GpnAppEndpointItem, CancellationToken, Task<int>> _probe;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingEndpoint> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Uygulama tek örneği: varsayılan saklama/aralık + gerçek ağ sondası.</summary>
    public GpnAppEndpointStore()
        : this(null, null, null)
    {
    }

    /// <summary>Test desteği: aralıklar ve ölçüm sondası dışarıdan verilir.</summary>
    internal GpnAppEndpointStore(
        TimeSpan? retention = null,
        TimeSpan? flushInterval = null,
        Func<GpnAppEndpointItem, CancellationToken, Task<int>>? probe = null)
    {
        _retention = retention ?? DefaultRetention;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _probe = probe ?? ProbeEndpointAsync;
    }

    /// <summary>
    /// Periyodik flush döngüsünü başlatır (idempotent) ve şemayı hazırlar
    /// (benzersiz indeks + eski satır budaması). SplitTunnelViewModel başlarken çağrılır.
    /// </summary>
    public void Start()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }
            cts = _cts = new CancellationTokenSource();
        }
        _ = EnsureSchemaAsync();
        _loop = Task.Run(() => FlushLoopAsync(cts.Token));
    }

    /// <summary>
    /// Bir gözlem kaydeder (yalnızca bellekte). Aynı (uygulama, uç nokta) çifti
    /// tekrar görülürse HitCount artar, LastSeenAt tazelenir — diske her pakette
    /// değil, periyodik flush'ta yazılır.
    /// </summary>
    public void Observe(string appName, string endpoint, string protocol)
    {
        if (appName.IsNullOrEmpty() || endpoint.IsNullOrEmpty())
        {
            return;
        }

        var key = appName + "|" + endpoint;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var pending))
            {
                pending.HitCount++;
                pending.LastSeenAt = now;
            }
            else
            {
                _pending[key] = new PendingEndpoint(appName, endpoint, protocol ?? string.Empty, now, now);
            }

            if (_pending.Count >= PendingFlushThreshold)
            {
                // Kapasite doldu — periyot beklenmeden boşalt (arka planda, hata gözlenir).
                _ = FlushSafeAsync();
            }
        }
    }

    /// <summary>Eşik tetikli flush — hatalar gözlenir (unobserved task exception olmaz).</summary>
    private async Task FlushSafeAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("GpnAppEndpointStore flush (threshold) failed", ex);
        }
    }

    /// <summary>Bekleyen tüm gözlemleri diske yazar (upsert). Yeni/satır sayısını döndürür.</summary>
    public Task<int> FlushAsync(CancellationToken ct = default)
    {
        List<PendingEndpoint> batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return Task.FromResult(0);
            }
            batch = _pending.Values.ToList();
            _pending.Clear();
        }
        return FlushBatchAsync(batch, ct);
    }

    /// <summary>
    /// Belirtilen uygulamaların kayıtlı uç noktalarını yükler (HitCount ve
    /// son görülme önceliğiyle sıralı) — ölçüm ve teşhis için.
    /// </summary>
    public async Task<List<GpnAppEndpointItem>> LoadForAppsAsync(
        IEnumerable<string> appNames,
        CancellationToken ct = default)
    {
        var names = appNames
            .Where(n => n.IsNotEmpty())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return [];
        }

        ct.ThrowIfCancellationRequested();
        // sqlite-net'in ifade çevirisi dizi Contains'i desteklemez — değerler
        // (uygulama adları) SQL literal olarak kaçışlı gömülür (tek tırnak ikiye
        // katlanır) ve IN listesiyle sorgulanır. LOWER her iki tarafta da — uygulama
        // adı oturumlar arasında büyük/küçük harf farkıyla değişse bile eşleşir.
        var quoted = string.Join(", ", names.Select(n => $"LOWER('{SqlEscape(n)}')"));
        var sql = $"SELECT * FROM gpn_app_endpoints WHERE LOWER(AppName) IN ({quoted}) " +
                  "ORDER BY AppName, HitCount DESC, LastSeenAt DESC";
        var rows = await SQLiteHelper.Instance.QueryAsync<GpnAppEndpointItem>(sql).ConfigureAwait(false);
        return rows ?? [];
    }

    /// <summary>
    /// Uygulamaların gerçek sunucu ping'ini ölçer: her uygulamanın kayıtlı
    /// uç noktalarının en iyi (en düşük) gidiş-dönüş süresi. ICMP yanıt
    /// vermeyen TCP uç noktalarında TCP connect gecikmesi kullanılır; UDP
    /// uç noktalarında yalnızca ICMP (oyun sunucusu ana bilgisayarına).
    /// Sonuçlar uygulama başına toplanır.
    /// </summary>
    public async Task<IReadOnlyList<GpnAppRealPingResult>> MeasureRealPingAsync(
        IReadOnlyCollection<string> appNames,
        int maxEndpointsPerApp = DefaultMaxEndpointsPerApp,
        CancellationToken ct = default)
    {
        if (appNames is null || appNames.Count == 0)
        {
            return [];
        }

        var rows = await LoadForAppsAsync(appNames, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var appByRequestId = new Dictionary<string, string>(StringComparer.Ordinal);
        var requests = new List<NodePingRequest>();
        foreach (var group in rows.GroupBy(r => r.AppName, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var row in group.Take(maxEndpointsPerApp))
            {
                var requestId = $"{group.Key}|{row.Endpoint}";
                appByRequestId[requestId] = group.Key;
                requests.Add(new NodePingRequest(requestId, token => _probe(row, token)));
            }
        }
        if (requests.Count == 0)
        {
            return [];
        }

        var results = await _coordinator
            .RunAsync(requests, PingCoordinatorTimeout, cancellationToken: ct)
            .ConfigureAwait(false);

        var bestMs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var okCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            var app = appByRequestId[result.Id];
            totalCount[app] = totalCount.GetValueOrDefault(app) + 1;
            if (result.IsSuccess)
            {
                okCount[app] = okCount.GetValueOrDefault(app) + 1;
                if (!bestMs.TryGetValue(app, out var current) || result.Delay < current)
                {
                    bestMs[app] = result.Delay;
                }
            }
        }

        return appNames
            .Where(n => n.IsNotEmpty())
            .Select(app => new GpnAppRealPingResult(
                app,
                bestMs.GetValueOrDefault(app, -1),
                okCount.GetValueOrDefault(app),
                totalCount.GetValueOrDefault(app)))
            .ToArray();
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }
        if (cts is null)
        {
            return;
        }
        cts.Cancel();
        cts.Dispose();
        try
        {
            _loop?.GetAwaiter().GetResult();
        }
        catch
        {
            // kapanış — flush döngüsü iptalle biter
        }
    }

    // ── iç ───────────────────────────────────────────────────────────────

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_flushInterval, ct).ConfigureAwait(false);
                await FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
        catch (Exception ex)
        {
            Logging.SaveLog("GpnAppEndpointStore flush loop failed", ex);
        }
    }

    /// <summary>
    /// Şemayı hazırlar: (AppName, Endpoint) benzersiz indeksi (upsert'in
    /// ON CONFLICT hedefi) + saklama süresi dolan satırların budaması.
    /// </summary>
    internal async Task EnsureSchemaAsync()
    {
        try
        {
            await SQLiteHelper.Instance.ExecuteAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_gpn_app_endpoints ON gpn_app_endpoints(AppName, Endpoint)")
                .ConfigureAwait(false);
            var cutoff = DateTimeOffset.UtcNow.Add(-_retention).ToUnixTimeMilliseconds();
            await SQLiteHelper.Instance.ExecuteAsync(
                $"DELETE FROM gpn_app_endpoints WHERE LastSeenAt < {cutoff}")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("GpnAppEndpointStore schema/prune failed", ex);
        }
    }

    private async Task<int> FlushBatchAsync(List<PendingEndpoint> batch, CancellationToken ct)
    {
        var affected = 0;
        foreach (var pending in batch)
        {
            ct.ThrowIfCancellationRequested();
            var sql =
                "INSERT INTO gpn_app_endpoints (AppName, Endpoint, Protocol, FirstSeenAt, LastSeenAt, HitCount) " +
                $"VALUES ('{SqlEscape(pending.AppName)}', '{SqlEscape(pending.Endpoint)}', '{SqlEscape(pending.Protocol)}', " +
                $"{pending.FirstSeenAt}, {pending.LastSeenAt}, {pending.HitCount}) " +
                "ON CONFLICT(AppName, Endpoint) DO UPDATE SET " +
                "Protocol = excluded.Protocol, " +
                "LastSeenAt = excluded.LastSeenAt, " +
                "HitCount = gpn_app_endpoints.HitCount + excluded.HitCount";
            affected += await SQLiteHelper.Instance.ExecuteAsync(sql).ConfigureAwait(false);
        }
        return affected;
    }

    /// <summary>SQL string literal kaçışı (tek tırnak ikiye katlanır).</summary>
    internal static string SqlEscape(string value) => value.Replace("'", "''");

    /// <summary>
    /// Tek uç nokta için gecikme sondası: önce ICMP (DNS çözümüyle), ICMP
    /// yanıtsızsa TCP uç noktalarında TCP connect gecikmesi. -1 = ölçülemedi.
    /// </summary>
    private static async Task<int> ProbeEndpointAsync(GpnAppEndpointItem row, CancellationToken ct)
    {
        var host = ExtractHost(row.Endpoint);
        if (host is null)
        {
            return -1;
        }

        var address = await ResolveHostAsync(host, ct).ConfigureAwait(false);
        if (address is not null)
        {
            var icmp = await IcmpDelayMsAsync(address, ct).ConfigureAwait(false);
            if (icmp >= 0)
            {
                return icmp;
            }
        }

        if (string.Equals(row.Protocol, "TCP", StringComparison.OrdinalIgnoreCase)
            && TryGetPort(row.Endpoint, out var port))
        {
            var tcp = await TcpConnectDelayMsAsync(host, port, ct).ConfigureAwait(false);
            if (tcp >= 0)
            {
                return tcp;
            }
        }

        return -1;
    }

    /// <summary>
    /// ICMP Echo ile gidiş-dönüş ölçümü (GpnServerSelectionService ile aynı
    /// desen): DontFragment + küçük paket. TUN etkinken ping tünel üzerinden
    /// gider — "sonra" ölçümü tam olarak istenen yoldur.
    /// </summary>
    private static async Task<int> IcmpDelayMsAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var buffer = Encoding.ASCII.GetBytes("aogpn-real-ping-0123456789ab");
            var reply = await ping.SendPingAsync(
                address,
                (int)PingSampleTimeout.TotalMilliseconds,
                buffer,
                new PingOptions(64, true)).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>TCP connect gecikmesi (ICMP fallback'i — TCP uygulamaları için gerçek sunucu gecikmesi).</summary>
    private static async Task<int> TcpConnectDelayMsAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PingSampleTimeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Hostname'i IP'ye çözer (zaten IP ise aynen döner); çözülemezse null.</summary>
    private static async Task<IPAddress?> ResolveHostAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address;
        }
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"host:port", "[v6]:port" veya "host" biçiminden ana bilgisayar kısmını ayırır.</summary>
    internal static string? ExtractHost(string? endpoint)
    {
        if (endpoint.IsNullOrEmpty() || endpoint == "*")
        {
            return null;
        }
        if (endpoint![0] == '[')
        {
            var end = endpoint.IndexOf(']');
            return end > 1 ? endpoint[1..end] : null;
        }
        var idx = endpoint.LastIndexOf(':');
        return idx > 0 ? endpoint[..idx] : endpoint;
    }

    /// <summary>"host:port" / "[v6]:port" biçiminden portu ayırır; yoksa false.</summary>
    internal static bool TryGetPort(string? endpoint, out int port)
    {
        port = 0;
        if (endpoint.IsNullOrEmpty())
        {
            return false;
        }
        if (endpoint![0] == '[')
        {
            var end = endpoint.IndexOf(']');
            if (end <= 0 || end + 1 >= endpoint.Length || endpoint[end + 1] != ':')
            {
                return false;
            }
            return int.TryParse(endpoint[(end + 2)..], out port) && port > 0;
        }
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0)
        {
            return false;
        }
        return int.TryParse(endpoint[(idx + 1)..], out port) && port > 0;
    }

    /// <summary>Bellekte bekleyen tek (uygulama, uç nokta) gözlemi.</summary>
    private sealed class PendingEndpoint
    {
        public PendingEndpoint(string appName, string endpoint, string protocol, long firstSeenAt, long lastSeenAt)
        {
            AppName = appName;
            Endpoint = endpoint;
            Protocol = protocol;
            FirstSeenAt = firstSeenAt;
            LastSeenAt = lastSeenAt;
            HitCount = 1;
        }

        public string AppName { get; }
        public string Endpoint { get; }
        public string Protocol { get; }
        public long FirstSeenAt { get; }
        public long LastSeenAt;
        public int HitCount;
    }
}
