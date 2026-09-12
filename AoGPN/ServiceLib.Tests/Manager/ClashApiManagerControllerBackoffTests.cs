using AwesomeAssertions;
using ServiceLib.Manager;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Manager;

/// <summary>
/// ClashApiManager'ın denetleyici (mihomo dış API) erişilemediğindeki davranışı.
/// Regresyon: her okuma 3 deneme × 2 istek + 2 sn bekleme yapıyordu — tek çağrı
/// 6 başarısız bağlantı ve ~4 sn gecikme üretiyordu; GPN bağlanma yolu ve
/// saniyelik dashboard anketleri bunu katlıyordu.
///
/// Ağ erişimi dışarıdan verilir (internal overload'lar) — testler soket açmaz.
/// </summary>
public sealed class ClashApiManagerControllerBackoffTests
{
    private const string Api = "http://127.0.0.1:10814";

    /// <summary>İstenen adresleri kaydeden ve önceden verilen yanıtı dönen sahte denetleyici.</summary>
    private sealed class FakeController
    {
        public List<string> Calls { get; } = [];

        /// <summary>null = denetleyici yanıt vermiyor (bağlantı reddi).</summary>
        public string? Response { get; set; }

        public Task<string?> FetchAsync(string url)
        {
            Calls.Add(url);
            return Task.FromResult(Response);
        }
    }

    [Fact]
    public async Task Proxies_WhenControllerRefuses_SingleRequestAndNoProvidersCall()
    {
        var manager = new ClashApiManager();
        var controller = new FakeController { Response = null };

        var snapshot = await manager.GetClashProxiesAsync(Api, controller.FetchAsync);

        snapshot.Should().BeNull();
        controller.Calls.Should().HaveCount(1);
        controller.Calls[0].Should().Be($"{Api}/proxies",
            "denetleyici yanıtsızsa providers uç noktası hiç denenmez");
    }

    [Fact]
    public async Task AllReads_DuringCooldown_SendNoRequest()
    {
        // Uzun geri çekilme: pencere test boyunca açık kalır (bekleme yok).
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var manager = new ClashApiManager(policy);
        var controller = new FakeController { Response = null };

        await manager.GetClashProxiesAsync(Api, controller.FetchAsync);
        controller.Calls.Should().HaveCount(1);

        // Çekirdek kapalıyken gelen anketler: hem proxy hem bağlantı okumaları
        // geri çekilme penceresinde sessizce boş döner.
        for (var i = 0; i < 5; i++)
        {
            (await manager.GetClashProxiesAsync(Api, controller.FetchAsync)).Should().BeNull();
            (await manager.GetClashConnectionsAsync(Api, controller.FetchAsync)).Should().BeNull();
        }

        controller.Calls.Should().HaveCount(1, "geri çekilme penceresinde tek bir istek bile atılmaz");
        policy.ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public async Task WhenControllerComesUp_BothEndpointsReadAndBackoffResets()
    {
        // Çok kısa aralıklar: gerçek bekleme olmadan toparlanma doğrulanır.
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        var manager = new ClashApiManager(policy);
        var controller = new FakeController { Response = null };

        await manager.GetClashProxiesAsync(Api, controller.FetchAsync);
        controller.Calls.Should().HaveCount(1);

        await Task.Delay(25, TestContext.Current.CancellationToken); // geri çekilme penceresi geçti

        controller.Response = """{"proxies":{}}""";
        var snapshot = await manager.GetClashProxiesAsync(Api, controller.FetchAsync);

        snapshot.Should().NotBeNull();
        controller.Calls.Should().HaveCount(3);
        controller.Calls[2].Should().Be($"{Api}/providers/proxies");
        policy.ConsecutiveFailures.Should().Be(0, "denetleyici yanıt verdiği anda geri çekilme sıfırlanır");

        // Toparlanma sonrası çağrı beklemeden yapılır (kapı kapalı kalır).
        await manager.GetClashProxiesAsync(Api, controller.FetchAsync);
        controller.Calls.Should().HaveCount(5);
    }

    [Fact]
    public async Task ForceAttempt_IgnoresCooldownForDecisionCriticalReads()
    {
        // Uzun geri çekilme: kapı test boyunca açık.
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var manager = new ClashApiManager(policy);
        var controller = new FakeController { Response = null };

        await manager.GetClashProxiesAsync(Api, controller.FetchAsync);
        controller.Calls.Should().HaveCount(1);

        // Yumuşak düğüm geçişi / egress kararı: boş okuma restart fallback'ine yol
        // açtığı için geri çekilme penceresinde de denenir (tek istek).
        controller.Response = "{\"proxies\":{}}";
        var snapshot = await manager.GetClashProxiesAsync(Api, controller.FetchAsync, forceAttempt: true);

        snapshot.Should().NotBeNull();
        policy.ConsecutiveFailures.Should().Be(0, "karar-kritik okuma başarılıysa geri çekilme temizlenir");
    }

    [Fact]
    public async Task Connections_WhenControllerRefuses_ReturnsNullAndBacksOff()
    {
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var manager = new ClashApiManager(policy);
        var controller = new FakeController { Response = null };

        var first = await manager.GetClashConnectionsAsync(Api, controller.FetchAsync);
        var second = await manager.GetClashConnectionsAsync(Api, controller.FetchAsync);

        first.Should().BeNull();
        second.Should().BeNull();
        controller.Calls.Should().HaveCount(1);
        controller.Calls[0].Should().Be($"{Api}/connections");
    }
}
