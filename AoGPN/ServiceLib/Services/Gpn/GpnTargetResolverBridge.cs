using ServiceLib.Models.Dto;
using ServiceLib.ViewModels;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnTargetResolverBridge — SplitTunnelViewModel ↔ GpnTargetResolver köprüsü
//
// Dashboard'daki Game Boost listesinden TÜNELLENECEK (efektif olarak "vpn"
// yönlendirmeli) uygulamaların exe adlarını toplar, bunları GpnTargetResolver'a
// besler ve PID havuzunu CANLI (5 sn) çalıştırır. Yeni PID kümesi her türetildiğinde
// SnapshotChanged olayı yayınlanır — dashboard "PID havuzu" kartı bunu gösterir,
// WinDivert filtresi de aynı anlık görüntüyle yeniden derlenir.
//
// Hedef adları her turda tekrar okunur: kullanıcı oyun ekleyip/çıkarınca köprü en
// geç bir sonraki turda yeni resolver'ı kurar. GPN yönü (InvertManualRouting)
// hedef kümesini çevirir: beyaz listede "vpn" eylemli girişler tünellenir; kara
// listede (dışlama) "vpn" eylemli girişler doğrudan kalır (istisna), "direct"
// eylemli girişler ise kurallarda tünele çevrilir — yakalama köprüsü kurallarla
// AYNI kümeyi hedeflemelidir, yoksa dışlanan uygulamalar hâlâ tünellenir.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>PID havuzunun bir anlık görüntüsü (dashboard'a ve filtre derlemeye gider).</summary>
public sealed record GpnPidPoolSnapshot(
    IReadOnlyList<string> TargetNames,
    uint[] Pids,
    long Version,
    DateTimeOffset ResolvedAt,
    bool Watching,
    bool TargetRunning,
    ProcessTreeStatus SourceStatus = ProcessTreeStatus.Healthy);

public sealed class GpnTargetResolverBridge : IDisposable
{
    public static readonly TimeSpan DefaultRefreshInterval = GpnTargetResolver.DefaultRefreshInterval;

    private readonly Func<IEnumerable<SplitTunnelAppItem>> _apps;
    private readonly Func<bool> _invertManual;
    private readonly IProcessTreeSource? _source;
    private readonly object _gate = new();
    private GpnTargetResolver? _resolver;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private GpnPidPoolSnapshot? _last;

    /// <param name="splitTunnel">Oyun/GPN yönlendirme listesinin sahibi (Dashboard Game Boost).</param>
    /// <param name="source">Süreç ağacı kaynağı; belirtilmezse Toolhelp32 (yalnızca testler için).</param>
    public GpnTargetResolverBridge(SplitTunnelViewModel splitTunnel, IProcessTreeSource? source = null)
        : this(() => splitTunnel.Apps.ToArray(), source, () => splitTunnel.InvertManualRouting)
    {
    }

    /// <summary>
    /// Test desteği: uygulama listesi ve GPN yönü sağlayıcısı doğrudan verilir
    /// (ağır VM kurucusu gerekmez). Yön sağlayıcısı her turda canlı okunur — kullanıcı
    /// beyaz/kara liste arasında geçiş yapınca hedef kümesi en geç bir sonraki turda
    /// yeni yöne göre yeniden kurulur.
    /// </summary>
    internal GpnTargetResolverBridge(
        Func<IEnumerable<SplitTunnelAppItem>> apps,
        IProcessTreeSource? source = null,
        Func<bool>? invertManual = null)
    {
        _apps = apps ?? throw new ArgumentNullException(nameof(apps));
        _invertManual = invertManual ?? (() => false);
        _source = source;
    }

    /// <summary>Yeni bir anlık görüntü yayınlandığında tetiklenir (hedef adı/PID kümesi/durum değişince).</summary>
    public event Action<GpnPidPoolSnapshot>? SnapshotChanged;

    /// <summary>Son yayınlanan anlık görüntü (dashboard push'ları için).</summary>
    public GpnPidPoolSnapshot? LastSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>5 sn'lik izleme döngüsü çalışıyor mu.</summary>
    public bool IsWatching
    {
        get
        {
            lock (_gate)
            {
                return _cts is not null;
            }
        }
    }

    /// <summary>İzleme döngüsünü başlatır (varsa yeniden başlatmaz) ve hemen ilk ölçümü yayınlar.</summary>
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
        var owner = cts;
        _loop = Task.Run(() => LoopAsync(owner, owner.Token));
        _ = RefreshNow();
    }

    /// <summary>İzleme döngüsünü durdurur ve "izlenmiyor" durumunu yayınlar (son PID kümesi korunur).</summary>
    public void Stop()
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

        GpnPidPoolSnapshot? last;
        lock (_gate)
        {
            last = _last;
        }
        var stopped = new GpnPidPoolSnapshot(
            last?.TargetNames ?? [],
            last?.Pids ?? [],
            last?.Version ?? 0,
            DateTimeOffset.UtcNow,
            Watching: false,
            last?.TargetRunning ?? false,
            last?.SourceStatus ?? ProcessTreeStatus.Healthy);
        Publish(stopped);
    }

    /// <summary>
    /// Tek seferlik ölçüm (dashboard "Yenile" butonu): hedef adlarını yeniden okur,
    /// gerekirse resolver'ı yeniden kurar ve anlık görüntüyü (değiştiyse) yayınlar.
    /// </summary>
    public GpnPidPoolSnapshot RefreshNow()
    {
        GpnPidPoolSnapshot snap;
        lock (_gate)
        {
            snap = RunOnceLocked(CancellationToken.None);
        }
        Publish(snap);
        return snap;
    }

    public void Dispose()
    {
        Stop();
    }

    // ── iç ───────────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationTokenSource owner, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Action<GpnPidPoolSnapshot>? handler;
            GpnPidPoolSnapshot snap;
            lock (_gate)
            {
                // Stop() çağrıldıysa (sahip değişti) bu turda yayın yapma — Stop'un
                // "izlenmiyor" anlık görüntüsü her zaman son sözü söyler.
                if (!ReferenceEquals(_cts, owner))
                {
                    break;
                }
                snap = RunOnceLocked(cancellationToken);
                var changed = !SnapshotsEqual(_last, snap);
                _last = snap;
                handler = changed ? SnapshotChanged : null;
            }
            handler?.Invoke(snap);

            try
            {
                await Task.Delay(DefaultRefreshInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Anlık görüntüyü değiştiyse yayınlar (kilit dışında event invoke eder).</summary>
    private void Publish(GpnPidPoolSnapshot snap)
    {
        Action<GpnPidPoolSnapshot>? handler;
        lock (_gate)
        {
            var changed = !SnapshotsEqual(_last, snap);
            _last = snap;
            handler = changed ? SnapshotChanged : null;
        }
        handler?.Invoke(snap);
    }

    private GpnPidPoolSnapshot RunOnceLocked(CancellationToken cancellationToken)
    {
        string[] names;
        try
        {
            names = ComputeTargetNames();
        }
        catch
        {
            // Apps koleksiyonu eşzamanlı değişirse bu turu atla — bir sonraki tur yeniden dener.
            names = [];
        }

        if (names.Length == 0)
        {
            _resolver = null;
            return new GpnPidPoolSnapshot([], [], 0, DateTimeOffset.UtcNow, Watching: true, TargetRunning: false);
        }

        var resolver = _resolver;
        if (resolver is null || !SameNameSet(resolver.TargetNames, names))
        {
            resolver = new GpnTargetResolver(names, _source);
            _resolver = resolver;
        }

        var snapshot = resolver.Resolve(cancellationToken);
        return new GpnPidPoolSnapshot(
            names,
            snapshot?.Pids ?? [],
            snapshot?.Version ?? 0,
            snapshot?.ResolvedAt ?? DateTimeOffset.UtcNow,
            Watching: true,
            TargetRunning: snapshot is not null,
            SourceStatus: resolver.LastSourceStatus);
    }

    /// <summary>
    /// Tünellenecek (efektif olarak "vpn" yönlendirmeli) "app" girişlerinin exe
    /// adlarını toplar. GpnCaptureBridge (bağlantı anı) ile köprünün (5 sn izleme)
    /// AYNI hedef kümesini kullanmasını garantiler — aynı kaynak kod, ayrılmaz.
    /// </summary>
    /// <param name="invertManual">
    /// GPN kara liste (dışlama) yönü: true iken "vpn" eylemli girişler istisnadır
    /// (kurallarda direct'e çevrilir) ve YAKALANMAZ; "direct" eylemli girişler
    /// kurallarda tünele çevrilir ve yakalanır. false (beyaz liste) iken yalnızca
    /// "vpn" eylemli girişler yakalanır.
    /// </param>
    public static string[] ExtractTargetNames(IEnumerable<SplitTunnelAppItem> apps, bool invertManual = false)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var app in apps)
        {
            if (app is null
                || !string.Equals(app.EntryType, "app", StringComparison.OrdinalIgnoreCase)
                || app.Action.IsNullOrEmpty())
            {
                continue;
            }
            // Yakalama kararı yönlendirme OTORİTESİNDEN gelir (Tier 2 — rota
            // merkezileştirmesi): varışı tünel ya da WARP egress olan "app" girişleri
            // yakalanır (beyaz listede vpn/warp; kara listede kuralların tünele
            // çevirdiği "direct"). Kurallar ve yakalama köprüsü böylece AYNI karar
            // kaynağını kullanır — iki ayrı eylem tablosu sürüklenemez. (Legacy
            // "proxy"/"vpn+proxy" satırları da kuralların tünellediği gibi yakalanır.)
            var destination = GpnRoutingRuleService.ResolveDestination(app.Action, invertManual);
            if (!GpnRoutingRuleService.IsCapturedByNativeTunnel(destination))
            {
                continue;
            }
            var name = app.ProcessName.IsNotEmpty() ? app.ProcessName : app.Value;
            if (name.IsNotEmpty() && seen.Add(name))
            {
                names.Add(name);
            }
        }
        return names.ToArray();
    }

    /// <summary>Canlı yön ile tünellenecek (efektif "vpn") girişlerin exe adlarını toplar.</summary>
    private string[] ComputeTargetNames() => ExtractTargetNames(_apps(), _invertManual());

    private static bool SameNameSet(IReadOnlyCollection<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        var set = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        return b.All(set.Contains);
    }

    private static bool SnapshotsEqual(GpnPidPoolSnapshot? a, GpnPidPoolSnapshot b)
    {
        if (a is null)
        {
            return false;
        }
        if (a.Watching != b.Watching || a.TargetRunning != b.TargetRunning || a.SourceStatus != b.SourceStatus)
        {
            return false;
        }
        if (!SameNameSet(a.TargetNames, b.TargetNames))
        {
            return false;
        }
        return a.Pids.AsSpan().SequenceEqual(b.Pids);
    }
}