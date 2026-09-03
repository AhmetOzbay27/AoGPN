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
            Logging.SaveLog("SQLiteHelper write gate busy for 10s — write skipped.");
            return -1;
        }
        try
        {
            return await action().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logging.SaveLog("SQLiteHelper write timed out after 15s — write abandoned.");
            return -1;
        }
        finally
        {
            _writeGate.Release();
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
