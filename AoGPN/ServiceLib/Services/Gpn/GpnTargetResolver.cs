namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnTargetResolver — "hangi süreçler yakalanacak" (PID havuzu)
//
// UI'dan seçilen oyun exe'sinin PID'lerini bulur ve ALT SÜREÇLERİNİ de katar
// (oyunlar genellikle launcher→oyun→antı zinciri kurar). Her 5 saniyede bir
// tazelenir; yeni bir PID kümesi türetilirse WinDivert filtresi yeniden
// derlenir (oyun alt süreç doğurduğunda yakalama aynı kalır).
// ─────────────────────────────────────────────────────────────────────────
public class GpnTargetResolver
{
    /// <summary>WinDivert filtresinin yeniden derlenme aralığı.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly string[] _targetNames;
    private readonly IProcessTreeSource _source;
    private long _version;

    /// <param name="targetProcessNames">İzlenecek exe adları (ör. "EscapeFromTarkov.exe").</param>
    /// <param name="source">Süreç ağacı kaynağı; belirtilmezse Toolhelp32.</param>
    public GpnTargetResolver(IEnumerable<string> targetProcessNames, IProcessTreeSource? source = null)
    {
        _targetNames = targetProcessNames
            .Select(ProcessCatalogService.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_targetNames.Length == 0)
        {
            throw new ArgumentException("En az bir hedef süreç adı gereklidir.", nameof(targetProcessNames));
        }
        _source = source ?? new NativeProcessTreeSource();
    }

    /// <summary>Hedef adı yalnızca bu süreçlere mi (alt süreç dahil) isabet eder?</summary>
    public IReadOnlyCollection<string> TargetNames => _targetNames;

    /// <summary>
    /// Süreç ağacı kaynağının son tarama durumu. FATAL ise hiçbir karar güvenilir
    /// değildir — <see cref="Resolve"/> null döner ama bu "hedef kapalı" demek
    /// değildir (çağıran bu bayrağı kontrol edip farklı davranmalıdır).
    /// </summary>
    public ProcessTreeStatus LastSourceStatus { get; private set; } = ProcessTreeStatus.Healthy;

    /// <summary>
    /// Hedef süreçlerin ve tüm alt ağaçlarının PID kümesini üretir. Eksik bir
    /// hedef (süreç kapalıysa) veya FATAL kaynak durumu null döner — filtrenin
    /// "hiçbir şeyi yakalama" moduna girmesini engellemek için çağıran None
    /// dönerse önceki seti korur. null'un nedenini <see cref="LastSourceStatus"/> ile ayırt edin.
    /// </summary>
    public virtual TargetPidSnapshot? Resolve(CancellationToken cancellationToken = default)
    {
        ProcessInfo[] processes;
        try
        {
            processes = _source.Enumerate(cancellationToken).ToArray();
            LastSourceStatus = _source.Status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Kaynak kendi hatasını yakalamadıysa da FATAL say — ağaç erişilemez.
            LastSourceStatus = ProcessTreeStatus.Fatal;
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (LastSourceStatus == ProcessTreeStatus.Fatal)
        {
            return null; // ağaç erişilemez — "hedef kapalı" sanma
        }

        var nameSet = new HashSet<string>(_targetNames, StringComparer.OrdinalIgnoreCase);
        var byPid = processes.Where(p => p.Pid != 0).ToDictionary(p => p.Pid);
        var roots = processes.Where(p => nameSet.Contains(p.Name)).Select(p => p.Pid).ToHashSet();
        if (roots.Count == 0)
        {
            return null; // hedef süreç çalışmıyor
        }

        // Alt ağaçlar: kökler → çocuklar → torunlar (BFS).
        var childrenByParent = new Dictionary<uint, List<uint>>();
        foreach (var p in processes)
        {
            if (p.ParentPid != 0 && p.ParentPid != p.Pid)
            {
                if (!childrenByParent.TryGetValue(p.ParentPid, out var list))
                {
                    list = childrenByParent[p.ParentPid] = new List<uint>();
                }
                list.Add(p.Pid);
            }
        }

        var pids = new HashSet<uint>(roots);
        var queue = new Queue<uint>(roots);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parent, out var kids))
            {
                continue;
            }
            foreach (var kid in kids)
            {
                if (pids.Add(kid))
                {
                    queue.Enqueue(kid);
                }
            }
        }

        var id = System.Threading.Interlocked.Increment(ref _version);
        return new TargetPidSnapshot(pids.OrderBy(x => x).ToArray(), id, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Periyodik tazeleme döngüsü: ilk anlık görüntüyü hemen verir, ardından
    /// <paramref name="interval"/> (varsayılan 5 sn) aralıklarla PID kümeleri
    /// DEĞİŞİNCE yeni anlık görüntü sağlar. Çağıran, yeni görüntüde filtreyi
    /// yeniden derler.
    /// </summary>
    public virtual async IAsyncEnumerable<TargetPidSnapshot> RefreshLoopAsync(
        TimeSpan? interval = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        interval ??= DefaultRefreshInterval;
        var previous = Resolve(cancellationToken);
        if (previous is not null)
        {
            yield return previous;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval.Value, cancellationToken).ConfigureAwait(false);
            var current = Resolve(cancellationToken);
            if (current is null)
            {
                continue; // hedef kapandı — önceki seti koru (filtre boşa düşmesin)
            }
            if (!current.HasSamePids(previous))
            {
                previous = current;
                yield return current;
            }
        }
    }
}

/// <summary>Belirli bir anda yakalanacak PID kümesinin anlık görüntüsü.</summary>
public sealed record TargetPidSnapshot(uint[] Pids, long Version, DateTimeOffset ResolvedAt)
{
    /// <summary>Önceki görüntüyle PID kümesi aynı mı (filtre yeniden derlemeye gerek yok mu)?</summary>
    public bool HasSamePids(TargetPidSnapshot? other)
        => other is not null
           && Pids.Length == other.Pids.Length
           && Pids.AsSpan().SequenceEqual(other.Pids);

    /// <summary>WinDivert filtresi için PID kümesi (processId == x or …).</summary>
    public IReadOnlyCollection<uint> PidSet => Pids;
}