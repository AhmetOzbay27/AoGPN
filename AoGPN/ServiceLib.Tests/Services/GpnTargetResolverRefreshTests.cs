using ServiceLib.Services;
using ServiceLib.Services.Gpn;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnTargetResolver.RefreshLoopAsync açılış-penceresi (fast first tick) testleri.
///
/// A2 açığı: köprü bağlantı anında hedef oyunu çözümler; oyun bağlantı KURULURKEN
/// doğarsa (launcher oyunu açar açmaz) ilk tazeleme varsayılan 5 sn'de gelir ve
/// oyunun ilk paketleri yarışta yakalanmadan geçer. firstTickInterval verilince
/// ilk tik kısa aralıkla atılır (PID kümesi değişmediyse sessiz) — oyun süreci
/// ~1 sn içinde filtreye girer.
/// </summary>
public class GpnTargetResolverRefreshTests
{
    private sealed class MutableProcessSource : IProcessTreeSource
    {
        private volatile ProcessInfo[] _processes = [];

        public ProcessTreeStatus Status => ProcessTreeStatus.Healthy;

        public void Set(params ProcessInfo[] processes) => _processes = processes;

        public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _processes;
        }
    }

    [Fact]
    public async Task GameStartingDuringConnect_IsPickedUpByFastFirstTick()
    {
        var source = new MutableProcessSource();
        var resolver = new GpnTargetResolver(new[] { "Game.exe" }, source);

        // Normal kadans 5 sn — hızlı ilk tik olmasaydı bu test 5 sn beklerdi.
        var interval = TimeSpan.FromSeconds(5);
        var firstTick = TimeSpan.FromMilliseconds(250);

        // Bağlantı anında hedef henüz çalışmıyor (launcher oyunu başlatacak).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sw = Stopwatch.StartNew();

        await using var enumerator = resolver
            .RefreshLoopAsync(interval, TestContext.Current.CancellationToken, firstTick)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // İlk Resolve null (hedef yok) — hiçbir anlık görüntü yayınlanmadı.
        // Oyun bağlantı kurulduktan kısa süre sonra doğar:
        await Task.Delay(50);
        source.Set(new ProcessInfo(100, 0, "Game.exe"));

        var hasSnapshot = await enumerator.MoveNextAsync().AsTask().WaitAsync(cts.Token);
        Assert.True(hasSnapshot, "hızlı ilk tik, bağlantı anında doğan oyunun PID'lerini yakalamalı");
        Assert.Equal(new[] { 100u }, enumerator.Current.Pids);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2.5),
            $"ilk tazeleme 5 sn'lik normal kadansı beklemeden gelmeli (geçen: {sw.Elapsed})");
    }

    [Fact]
    public async Task UnchangedPids_FastFirstTickStaysSilent()
    {
        var source = new MutableProcessSource();
        source.Set(new ProcessInfo(100, 0, "Game.exe"));
        var resolver = new GpnTargetResolver(new[] { "Game.exe" }, source);

        var interval = TimeSpan.FromSeconds(5);
        var firstTick = TimeSpan.FromMilliseconds(150);
        using var lifetime = new CancellationTokenSource();

        await using var enumerator = resolver
            .RefreshLoopAsync(interval, lifetime.Token, firstTick)
            .GetAsyncEnumerator(lifetime.Token);

        // İlk anlık görüntü hemen gelir.
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(new[] { 100u }, enumerator.Current.Pids);

        // PID kümesi değişmedi — hızlı tik (150 ms) sessizdir; döngü 5 sn'lik
        // kadansa kadar yeni anlık görüntü YAYINLAMAZ (küme aynı → filtre yeniden
        // derlenmez). Bekleme penceresi içinde hiçbir öğe gelmemesi beklenir
        // (MoveNext sonsuz döngüde asla false dönmez — zaman aşımı doğru işarettir).
        using var window = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        var task = enumerator.MoveNextAsync().AsTask();
        var ex = await Record.ExceptionAsync(() => task.WaitAsync(window.Token));
        Assert.NotNull(ex); // TimeoutException/TaskCanceledException — öğe yok

        // Bekleyen MoveNextAsync'i iptal ederek temiz kapan (dispose'a bırakma).
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}