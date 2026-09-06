using System.Collections;

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

    private async Task<int> SerializedWriteAsync(Func<Task<int>> action)
    {
        // Bounded gate: a stuck writer (e.g. a hung transaction during exit's
        // state flush) previously held _writeGate forever and froze the whole
        // shutdown. Time out and skip instead of blocking.
        if (!await _writeGate.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            var n = Interlocked.Increment(ref _busyLogCounter);
            if (n == 1 || n % 1000 == 0)
            {
                Logging.SaveLog("SQLiteHelper write gate busy for 10s — write skipped.");
            }
            return -1;
        }
        try
        {
            // Task.Run + Unwrap: sqlite-net's async API can block synchronously
            // inside its connection queue when the queue is wedged. If the action
            // ran on the caller's thread, the gate holder itself would be stuck
            // forever (observed live: the gate stayed held for the whole session,
            // every other write skipped for 10 minutes, and the process froze).
            // Hopping to a pool thread keeps the 15 s cap effective: the holder
            // always times out and releases the gate.
            // Task.Run unwraps Func<Task<T>> automatically — the 15 s cap below
            // then applies to the real operation.
            return await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // First timeout carries the caller identity so the stuck write can be
            // found in the logs; the async connection is recreated because a wedged
            // sqlite-net queue would otherwise poison every later write too.
            if (Interlocked.Exchange(ref _timeoutLogged, 1) == 0)
            {
                Logging.SaveLog(
                    $"SQLiteHelper write timed out after 15s — write abandoned " +
                    $"(caller: {action.Method.DeclaringType?.Name}.{action.Method.Name}); async connection recreated.");
            }
            RecreateAsyncConnection();
            return -1;
        }
        finally
        {
            _writeGate.Release();
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
        _dbAsync = new SQLiteAsyncConnection(_connstr, false);
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
