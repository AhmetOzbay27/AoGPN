using AwesomeAssertions;
using ServiceLib.Models.Dto;
using ServiceLib.Services;
using ServiceLib.Services.Gpn;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnDrainWatcher — kesintisiz düğüm geçişi sonrası eski düğümün boşalma
/// (drain) ilerlemesini /connections zincirlerinden sayıp yayınlar. Ağ/API
/// sahte uygulamalarla değiştirilir; gerçek mihomo'ya dokunulmaz.
/// </summary>
public class GpnDrainWatcherTests
{
    private static readonly GpnServerProfile Italya = new(
        "it", "İtalya", "92.4.220.236", 51820,
        "iK25iMzwDmLjq2PzX7w6yYdLk9nTgQ4vRcF1sB8hUaA=",
        "nL4xOq2WcVb9sRt7YhGf1pDk8uJm0zXeN6SaIcQ5wE=",
        "10.66.66.2/24");

    private static readonly GpnServerProfile Almanya = new(
        "de", "Almanya", "130.61.223.36", 51820,
        "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
        "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
        "10.66.66.2/24");

    private static ClashConnections Conns(params string[][] chains) => new()
    {
        connections = chains.Select(c => new ConnectionItem { chains = [.. c] }).ToList(),
    };

    private static ClashConnections EmptyConns() => new() { connections = [] };

    [Fact]
    public async Task DrainsToZero_PublishesProgressThenFinished()
    {
        var fetches = new Queue<ClashConnections>(
        [
            Conns(["wg-it"], ["wg-it"], ["wg-it"]), // 3 kaldı
            Conns(["wg-it"]),                        // 1 kaldı
            EmptyConns(),                            // 0 → bitti
        ]);
        var published = new List<GpnDrainSnapshot>();

        var watcher = new GpnDrainWatcher(pollInterval: TimeSpan.FromMilliseconds(1));
        var result = await watcher.RunAsync(
            Italya, Almanya,
            fetchConnections: () => Task.FromResult(fetches.Count > 0 ? fetches.Dequeue() : EmptyConns()),
            publish: published.Add);

        // İlerleme anlık görüntüleri: 3 → 1 → 0, hepsi IsDraining.
        published.Where(s => s.IsDraining).Select(s => s.RemainingConnections)
            .Should().Equal(3, 1, 0);
        // Kapanış: IsDraining=false, kalan 0, zaman aşımı yok.
        result.IsDraining.Should().BeFalse();
        result.RemainingConnections.Should().Be(0);
        result.TimedOut.Should().BeFalse();
        result.FromServerId.Should().Be("it");
        result.ToServerId.Should().Be("de");
        published[^1].IsDraining.Should().BeFalse();
        published[^1].TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task TimeoutWithLeftovers_EndsTimedOutWithoutClosing()
    {
        var published = new List<GpnDrainSnapshot>();
        var watcher = new GpnDrainWatcher(
            pollInterval: TimeSpan.FromMilliseconds(1),
            maxDuration: TimeSpan.FromMilliseconds(15));

        var result = await watcher.RunAsync(
            Italya, Almanya,
            fetchConnections: () => Task.FromResult(Conns(["wg-it"], ["wg-it"])), // sürekli 2 kaldı
            publish: published.Add);

        result.IsDraining.Should().BeFalse();
        result.RemainingConnections.Should().Be(2);
        result.TimedOut.Should().BeTrue("süre doldu ve eski düğümde bağlantı kaldı — zorla kapatılmaz");
        published.Count(s => s.IsDraining).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Canceled_SilentlyThrows_WithoutFinalSnapshot()
    {
        var published = new List<GpnDrainSnapshot>();
        var watcher = new GpnDrainWatcher(
            pollInterval: TimeSpan.FromMilliseconds(50),
            maxDuration: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // daha ilk yoklamadan iptal — yeni geçiş/bağlantı kesme senaryosu

        await watcher.Invoking(w => w.RunAsync(
                Italya, Almanya,
                fetchConnections: () => Task.FromResult(Conns(["wg-it"])),
                publish: published.Add,
                cancellationToken: cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        published.Should().BeEmpty("iptal edilen izleyici son anlık görüntüsünü yayınlamaz");
    }

    [Fact]
    public async Task FetchFails_RetriesWithUnknownRemaining()
    {
        var attempts = 0;
        var published = new List<GpnDrainSnapshot>();
        // Zaman aşımı bu senaryonun konusu değil — yalnızca hata→yeniden deneme→0
        // akışı sınanır; makine yükü zamanlama tabanlı eşiği bozmasın diye bol süre.
        var watcher = new GpnDrainWatcher(
            pollInterval: TimeSpan.FromMilliseconds(1),
            maxDuration: TimeSpan.FromSeconds(5));

        var result = await watcher.RunAsync(
            Italya, Almanya,
            fetchConnections: () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("controller yanıt vermedi");
                }
                return Task.FromResult(EmptyConns()); // ikinci turda 0 kaldı
            },
            publish: published.Add);

        // İlk yoklama hata verdi → -1 (bilinmiyor), sonra 0 → bitti.
        published.Select(s => s.RemainingConnections).Should().Contain(-1);
        result.RemainingConnections.Should().Be(0);
        result.TimedOut.Should().BeFalse();
    }

    [Theory]
    [InlineData("wg-it")]
    [InlineData("WG-IT")]
    public void CountRemaining_MatchesChainByWgProxyName_CaseInsensitive(string wgName)
    {
        // Zincirde grubun adı da bulunabilir ("GPN-Nodes" → wg-it); eşleşme wg-<id>'den.
        var conns = Conns(
            ["GPN-Nodes", "wg-it"],
            ["warp-socks", "wg-it"],
            ["wg-de"],          // yeni düğüm — sayılmaz
            ["DIRECT"]);        // doğrudan — sayılmaz

        GpnDrainWatcher.CountRemainingConnections(conns, wgName).Should().Be(2);
        GpnDrainWatcher.CountRemainingConnections(conns, "wg-de").Should().Be(1);
    }

    [Fact]
    public void CountRemaining_NullOrEmpty_ReturnsZero()
    {
        GpnDrainWatcher.CountRemainingConnections(null, "wg-it").Should().Be(0);
        GpnDrainWatcher.CountRemainingConnections(EmptyConns(), "wg-it").Should().Be(0);
        GpnDrainWatcher.CountRemainingConnections(Conns(["wg-it"]), "").Should().Be(0);
    }
}
