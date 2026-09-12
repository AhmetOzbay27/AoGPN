using System.Collections;
using System.Globalization;

namespace ServiceLib.Helper;

public sealed class SQLiteHelper
{
    private static readonly Lazy<SQLiteHelper> _instance = new(() => new());
    public static SQLiteHelper Instance => _instance.Value;
    private readonly string _connstr;
    private SQLiteConnection _db;
    private SQLiteAsyncConnection _dbAsync;
    private readonly string _configDB = "guiNDB.db";

    /// <summary>
    /// Serializes every asynchronous write through the shared async connection.
    /// Overlapping transactional writes (e.g. the startup profile migration racing
    /// a refresh/scheduled-task write) can corrupt sqlite-net's per-connection
    /// savepoint stack and surface as
    /// "savePoint is not valid, and should be the result of a call to SaveTransactionPoint".
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // "gate busy" logging is rate-limited: a wedged writer once produced ~4.3M
    // identical lines (≈375 MB) in ten minutes while every write waited 10 s and
    // skipped. The first occurrence and then every 1000th keep the symptom visible
    // without filling the disk.
    private int _busyLogCounter;
    private int _timeoutLogged;
    private int _slowLogCounter;

    // Kapıyı elinde tutan yazar + tutma başlangıcı — gate-busy satırına yazılır.
    // 2026-09-10 oturumunda ~2,9M yazma atlandı ama "write timed out" HİÇ
    // basmadı: tutucu 10-15 sn arası yazıyor ve kapıyı bırakıyordu. Tutucuyu
    // adlandırmadan bu yazar bulunamazdı.
    private readonly object _gateStateLock = new();
    private string? _gateHolder;
    private long _gateHeldSince;

    /// <summary>Kapıyı bekleyen yazarın atlanmadan önce beklediği süre (test dikişi: kısaltılabilir).</summary>
    internal TimeSpan GateAcquireTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Kapıyı tutan yazmaya verilen tavan süre — aşarsa yazma terk edilir, bağlantı yenilenir.</summary>
    internal TimeSpan GateHoldCap { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Bu süreyi aşan kilit tutuşları contention olmasa bile raporlanır (yavaş yazar teşhisi).</summary>
    internal TimeSpan SlowHoldReportThreshold { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tavan aşımında async bağlantının yeniden kurulup kurulmayacağı. Test dikişi:
    /// sqlite-net'in bağlantı HAVUZU bağlantı dizesine göre statiktir — testte havuz
    /// bağlantısını kapatmak tekil örneğin sonraki yazmalarını bozar; tavan aşımı
    /// davranışı (terk + kapı serbest) bağlantı yenilemesi OLMADAN da doğrulanır.
    /// </summary>
    internal bool RecreateConnectionOnTimeout { get; set; } = true;

    internal int BusySkipCount => Volatile.Read(ref _busyLogCounter);
    internal int TimeoutAbandonCount => Volatile.Read(ref _timeoutLogged);
    internal int SlowHoldLogCount => Volatile.Read(ref _slowLogCounter);

    internal bool IsGateHeld
    {
        get
        {
            lock (_gateStateLock)
            {
                return _gateHolder is not null;
            }
        }
    }

    internal void ResetGateDiagnosticsForTest()
    {
        Interlocked.Exchange(ref _busyLogCounter, 0);
        Interlocked.Exchange(ref _timeoutLogged, 0);
        Interlocked.Exchange(ref _slowLogCounter, 0);
    }

    /// <summary>Test dikişi: gerçek SQLite işlemi olmadan gate davranışını sürer.</summary>
    internal Task<int> RunSerializedWriteForTest(Func<Task<int>> action)
        => SerializedWriteAsync(action);

    public SQLiteHelper()
    {
        _connstr = Utils.GetConfigPath(_configDB);
        _db = new SQLiteConnection(_connstr, false);
        _dbAsync = new SQLiteAsyncConnection(_connstr, false);
    }

    public CreateTableResult CreateTable<T>()
    {
        return _db.CreateTable<T>();
    }

    private static string CallerName(Func<Task<int>> action)
        => $"{action.Method.DeclaringType?.Name}.{action.Method.Name}";

    /// <summary>gate-busy mesajı — bekleme sırasında hem bekleyen hem TUTAN çağıran görünür.</summary>
    internal static string BuildBusyMessage(string caller, string? holder, double heldForSeconds)
        => string.Format(CultureInfo.InvariantCulture,
            "SQLiteHelper write gate busy for 10s — write skipped " +
            "(caller: {0}; gate holder: {1}; held for: {2:0.0}s).",
            caller, holder ?? "?", heldForSeconds);

    private async Task<int> SerializedWriteAsync(Func<Task<int>> action)
    {
        var caller = CallerName(action);

        // Bounded gate: a stuck writer (e.g. a hung transaction during exit's
        // state flush) previously held _writeGate forever and froze the whole
        // shutdown. Time out and skip instead of blocking.
        if (!await _writeGate.WaitAsync(GateAcquireTimeout))
        {
            var n = Interlocked.Increment(ref _busyLogCounter);
            if (n == 1 || n % 1000 == 0)
            {
                // Teşhis anahtarı: kapıyı KİM tutuyor + ne zamandır — 2026-09-06'daki
                // 4,3M özdeş satırın yazıcısı ancak elle bulunabildi; bu satır onu
                // otomatik adlandırır.
                var holder = GetGateHolder(out var heldForSeconds);
                Logging.SaveLog(BuildBusyMessage(caller, holder, heldForSeconds));
            }
            return -1;
        }
        var heldSince = Stopwatch.GetTimestamp();
        try
        {
            lock (_gateStateLock)
            {
                _gateHolder = caller;
                _gateHeldSince = heldSince;
            }
            // Task.Run + Unwrap: sqlite-net's async API can block synchronously
            // inside its connection queue when the queue is wedged. If the action
            // ran on the caller's thread, the gate holder itself would be stuck
            // forever (observed live: the gate stayed held for the whole session,
            // every other write skipped for 10 minutes, and the process froze).
            // Hopping to a pool thread keeps the cap effective: the holder always
            // times out and releases the gate.
            // Task.Run unwraps Func<Task<T>> automatically — the cap below then
            // applies to the real operation.
            var result = await Task.Run(action).WaitAsync(GateHoldCap).ConfigureAwait(false);
            ReportSlowHoldIfNeeded(caller, heldSince);
            return result;
        }
        catch (TimeoutException)
        {
            // First timeout carries the caller identity so the stuck write can be
            // found in the logs; the async connection is recreated because a wedged
            // sqlite-net queue would otherwise poison every later write too.
            if (Interlocked.Exchange(ref _timeoutLogged, 1) == 0)
            {
                Logging.SaveLog(
                    $"SQLiteHelper write timed out after {GateHoldCap.TotalSeconds:0}s — write abandoned " +
                    $"(caller: {caller}); async connection recreated.");
            }
            if (RecreateConnectionOnTimeout)
            {
                RecreateAsyncConnection();
            }
            return -1;
        }
        finally
        {
            lock (_gateStateLock)
            {
                _gateHolder = null;
                _gateHeldSince = 0;
            }
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Kilit <see cref="SlowHoldReportThreshold"/>'u aştıysa yavaş yazarı adlandırır —
    /// kimse beklemiyor olsa bile. 10-15 sn'lik tutuşlar busy spam'ı olmadan da sessizce
    /// yaşanabilir (loglarda "write timed out" yokken ~2,9M atlanan yazma); bu satır
    /// onları görünür yapar.
    /// </summary>
    private void ReportSlowHoldIfNeeded(string caller, long heldSince)
    {
        var heldFor = Stopwatch.GetElapsedTime(heldSince);
        if (heldFor < SlowHoldReportThreshold)
        {
            return;
        }
        var n = Interlocked.Increment(ref _slowLogCounter);
        if (n == 1 || n % 100 == 0)
        {
            Logging.SaveLog(string.Format(CultureInfo.InvariantCulture,
                "SQLiteHelper write gate held {0:0.0}s by {1} (slow write).", heldFor.TotalSeconds, caller));
        }
    }

    private string? GetGateHolder(out double heldForSeconds)
    {
        lock (_gateStateLock)
        {
            heldForSeconds = _gateHeldSince == 0
                ? 0
                : Stopwatch.GetElapsedTime(_gateHeldSince).TotalSeconds;
            return _gateHolder;
        }
    }

    /// <summary>
    /// Replaces the async connection after a wedged write. sqlite-net operations
    /// that never complete queue up on the connection's internal queue; a fresh
    /// connection starts clean (reads use the same instance, so this also
    /// unblocks read paths that were stuck behind the wedged queue).
    /// </summary>
    private void RecreateAsyncConnection()
    {
        try
        {
            _dbAsync?.GetConnection()?.Close();
        }
        catch
        {
            // zaten bozuk — yeni bağlantı zaten kurulacak
        }
        try
        {
            _dbAsync?.GetConnection()?.Dispose();
        }
        catch
        {
            // yukarıdaki gibi
        }
        try
        {
            _dbAsync = new SQLiteAsyncConnection(_connstr, false);
        }
        catch (Exception ex)
        {
            // Yeniden kurulum başarısızsa bile kapı serbest kalır — sonraki yazma
            // yeniden dener; tekil yazma kaybı tüm akışı kilitlemez.
            Logging.SaveLog($"SQLiteHelper async connection recreate failed: {ex.Message}");
        }
    }

    public Task<int> InsertAllAsync(IEnumerable models)
    {
        return SerializedWriteAsync(() => _dbAsync.InsertAllAsync(models, runInTransaction: true));
    }

    public Task<int> InsertAsync(object model)
    {
        return SerializedWriteAsync(() => _dbAsync.InsertAsync(model));
    }

    public Task<int> ReplaceAsync(object model)
    {
        return SerializedWriteAsync(() => _dbAsync.InsertOrReplaceAsync(model));
    }

    public Task<int> UpdateAsync(object model)
    {
        return SerializedWriteAsync(() => _dbAsync.UpdateAsync(model));
    }

    public Task<int> UpdateAllAsync(IEnumerable models)
    {
        return SerializedWriteAsync(() => _dbAsync.UpdateAllAsync(models, runInTransaction: true));
    }

    public Task<int> DeleteAsync(object model)
    {
        return SerializedWriteAsync(() => _dbAsync.DeleteAsync(model));
    }

    public Task<int> DeleteAllAsync<T>()
    {
        return SerializedWriteAsync(() => _dbAsync.DeleteAllAsync<T>());
    }

    public Task<int> ExecuteAsync(string sql)
    {
        return SerializedWriteAsync(() => _dbAsync.ExecuteAsync(sql));
    }

    public async Task<List<T>> QueryAsync<T>(string sql) where T : new()
    {
        return await _dbAsync.QueryAsync<T>(sql);
    }

    public AsyncTableQuery<T> TableAsync<T>() where T : new()
    {
        return _dbAsync.Table<T>();
    }

    public async Task DisposeDbConnectionAsync()
    {
        await Task.Factory.StartNew(() =>
        {
            _db?.Close();
            _db?.Dispose();
            _db = null;

            _dbAsync?.GetConnection()?.Close();
            _dbAsync?.GetConnection()?.Dispose();
            _dbAsync = null;
        });
    }
}
