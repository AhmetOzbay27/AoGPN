// ─────────────────────────────────────────────────────────────────────────
// ConnectionCommandGate — bağlantı komutları için tek kapı + "en yeni niyet"
//
// Bağlan/kes/mod değişimi gibi komutların hepsi TEK semafor kapısından geçer.
// Kapı meşgulken gelen komut ATILMAZ: en yeni niyet tek yuvada saklanır ve
// kapı sahibi işini bitirince (hâlâ kapıyı ELDE tutarken) tam olarak bir kez
// işlenir. Böylece:
//   • hiçbir tıklama sessizce düşmez ("tıkladım ama cevap vermiyor"),
//   • ardışık tıklamalarda yalnızca SON karar kazanır (kullanıcı kararı sadık),
//   • işlem sırası tıklama sırasıyla birebir aynıdır; iki komut asla
//     eşzamanlı çalışmaz (yarış/kilitlenme yok).
//
// Kuyruklanan komut `_dispatch` delege'siyle koşturulur: üretimde UI thread
// (Dispatcher.InvokeAsync), testlerde doğrudan çağrı veya elle sürülen bir
// pompaya bağlanabilir. Delege verilmezse komut çağıran thread'de koşar.
// ─────────────────────────────────────────────────────────────────────────

namespace ServiceLib.Services;

/// <summary>
/// Bağlantı komutlarını tek kapıdan sıralayan ve kapı meşgulken en yeni niyeti
/// saklayıp kapı boşalınca tam bir kez işleyen senkronizasyon birimi.
/// </summary>
public sealed class ConnectionCommandGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Func<Func<Task>, Task> _dispatch;
    private PendingCommand? _pending;

    private sealed record PendingCommand(string Kind, Func<Task> Command);

    /// <param name="dispatch">
    /// Kuyruklanan komutun koşturulacağı delege (üretimde UI thread).
    /// Verilmezse komut çağıran thread'de koşar.
    /// </param>
    public ConnectionCommandGate(Func<Func<Task>, Task>? dispatch = null)
    {
        _dispatch = dispatch ?? (action => action());
    }

    /// <summary>
    /// Komutu kapıdan geçirir. Kapı meşgulse komut atılmaz — en yeni niyet
    /// yuvasında saklanır ve kapı boşalınca (hâlâ kapı elde tutulurken) tam
    /// olarak bir kez işlenir. Komut başarısız olursa kaydedilir ve yeniden
    /// fırlatılır; kuyruklanan komut başarısız olursa yalnızca kaydedilir
    /// (drenaj devam eder, sonraki niyetler işlenir).
    /// </summary>
    public async Task RunAsync(string kind, Func<Task> command)
    {
        if (command is null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            lock (_sync)
            {
                _pending = new PendingCommand(kind, command);
            }
            return;
        }

        try
        {
            await command().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"Connection command '{kind}' failed", ex);
            throw;
        }
        finally
        {
            try
            {
                // Kapı hâlâ ELDEYKEN kuyruktaki en yeni niyeti işle (drain):
                // kapı bırakılmadan önce işlendiği için araya eşzamanlı komut
                // giremez ve sıra tıklama sırasıyla birebir aynı kalır. Drenaj
                // sırasında gelen yeni tıklamalar yuvayı değiştirir — döngü
                // yalnızca SON niyeti işler (en son karar kazanır).
                while (true)
                {
                    PendingCommand? next;
                    lock (_sync)
                    {
                        next = _pending;
                        _pending = null;
                    }
                    if (next is null)
                    {
                        break;
                    }
                    try
                    {
                        await _dispatch(next.Command).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logging.SaveLog($"Queued connection command '{next.Kind}' failed", ex);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}