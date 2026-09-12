using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnAppEndpointStore — gerçek sunucu uç noktası kaydı (observe → flush → upsert),
/// uygulama bazlı yükleme ve "gerçek ping" ölçümünün toplama mantığı. SQLiteHelper'in
/// küresel veritabanını kullandığı için SqliteCatalog koleksiyonunda sıralı koşar.
/// </summary>
[Collection(ServiceLib.Tests.SqliteCatalogCollection.Name)]
public class GpnAppEndpointStoreTests : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        SQLiteHelper.Instance.CreateTable<GpnAppEndpointItem>();
        await SQLiteHelper.Instance.DeleteAllAsync<GpnAppEndpointItem>();
    }

    public async ValueTask DisposeAsync()
    {
        await SQLiteHelper.Instance.DeleteAllAsync<GpnAppEndpointItem>();
    }

    [Fact]
    public async Task Observe_AndFlush_UpsertsUniqueEndpoint_AndAccumulatesHitCount()
    {
        var store = new GpnAppEndpointStore();
        await store.EnsureSchemaAsync();

        // Aynı uç nokta iki kez görülür → tek satır, HitCount 2.
        store.Observe("EscapeFromTarkov.exe", "1.2.3.4:51820", "UDP");
        store.Observe("EscapeFromTarkov.exe", "1.2.3.4:51820", "UDP");
        store.Observe("EscapeFromTarkov.exe", "5.6.7.8:443", "TCP");

        var affected = await store.FlushAsync(TestContext.Current.CancellationToken);
        Assert.True(affected > 0);

        var rows = await store.LoadForAppsAsync(["EscapeFromTarkov.exe"], TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Count);

        var udp = rows.Single(r => r.Endpoint == "1.2.3.4:51820");
        Assert.Equal(2, udp.HitCount);
        Assert.Equal("UDP", udp.Protocol);
        Assert.True(udp.FirstSeenAt <= udp.LastSeenAt);

        // İkinci flush aynı satırı GÜNCELLER (yeni satır açmaz) — HitCount artar.
        store.Observe("EscapeFromTarkov.exe", "1.2.3.4:51820", "UDP");
        await store.FlushAsync(TestContext.Current.CancellationToken);
        var again = await store.LoadForAppsAsync(["EscapeFromTarkov.exe"], TestContext.Current.CancellationToken);
        Assert.Equal(2, again.Count);
        Assert.Equal(3, again.Single(r => r.Endpoint == "1.2.3.4:51820").HitCount);
    }

    [Fact]
    public async Task Flush_WithoutObservations_DoesNothing()
    {
        var store = new GpnAppEndpointStore();
        Assert.Equal(0, await store.FlushAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Observe_IgnoresEmptyValues()
    {
        var store = new GpnAppEndpointStore();
        store.Observe("", "1.2.3.4:1", "UDP"); // fırlatmaz
        store.Observe("app.exe", "", "UDP");   // fırlatmaz
        Assert.Equal(0, await store.FlushAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadForApps_FiltersByAppNames_CaseInsensitive()
    {
        var store = new GpnAppEndpointStore();
        await store.EnsureSchemaAsync();
        store.Observe("GameOne.exe", "1.1.1.1:1000", "UDP");
        store.Observe("GameTwo.exe", "2.2.2.2:2000", "UDP");
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var rows = await store.LoadForAppsAsync(["gameone.exe"], TestContext.Current.CancellationToken);
        var row = Assert.Single(rows);
        Assert.Equal("GameOne.exe", row.AppName);
    }

    [Fact]
    public async Task MeasureRealPingAsync_UsesProbe_AndAggregatesBestPerApp()
    {
        var probeCalls = 0;
        var store = new GpnAppEndpointStore(probe: (row, _) =>
        {
            probeCalls++;
            var ms = row.Endpoint switch
            {
                "10.0.0.1:1000" => 40,
                "10.0.0.2:2000" => 90,
                _ => -1,
            };
            return Task.FromResult(ms);
        });
        await store.EnsureSchemaAsync();
        store.Observe("Game.exe", "10.0.0.1:1000", "UDP");
        store.Observe("Game.exe", "10.0.0.2:2000", "UDP");
        store.Observe("Other.exe", "10.0.0.3:3000", "TCP");
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var results = await store.MeasureRealPingAsync(["Game.exe", "Other.exe"], maxEndpointsPerApp: 4, ct: TestContext.Current.CancellationToken);

        var game = Assert.Single(results, r => r.AppName == "Game.exe");
        Assert.Equal(40, game.BestMs); // iki sondan en düşüğü (en iyi)
        Assert.Equal(2, game.OkEndpoints);
        Assert.Equal(2, game.TotalEndpoints);

        var other = Assert.Single(results, r => r.AppName == "Other.exe");
        Assert.Equal(-1, other.BestMs);
        Assert.Equal(0, other.OkEndpoints);
        Assert.Equal(1, other.TotalEndpoints);

        Assert.Equal(3, probeCalls);
    }

    [Fact]
    public async Task MeasureRealPingAsync_UnspecifiedEndpoint_DoesNotThrow()
    {
        // Gerçek (varsayılan) probe yoluyla 0.0.0.0 kayıtlı uç nokta: SendPingAsync
        // hedef olarak belirsiz adres kabul etmez (ArgumentException) — guard bu
        // durumu fırlatmadan "ölçülemedi" (-1) olarak işaretler.
        var store = new GpnAppEndpointStore();
        await store.EnsureSchemaAsync();
        store.Observe("Game.exe", "0.0.0.0:51820", "UDP");
        await store.FlushAsync(TestContext.Current.CancellationToken);

        var results = await store.MeasureRealPingAsync(
            ["Game.exe"], maxEndpointsPerApp: 4, ct: TestContext.Current.CancellationToken);

        var game = Assert.Single(results, r => r.AppName == "Game.exe");
        Assert.Equal(-1, game.BestMs);
        Assert.Equal(0, game.OkEndpoints);
        Assert.Equal(1, game.TotalEndpoints);
    }

    [Fact]
    public async Task MeasureRealPingAsync_RespectsMaxEndpointsPerApp()
    {
        var probeCalls = 0;
        var store = new GpnAppEndpointStore(probe: (_, _) =>
        {
            probeCalls++;
            return Task.FromResult(10);
        });
        await store.EnsureSchemaAsync();
        store.Observe("Game.exe", "10.0.0.1:1", "UDP");
        store.Observe("Game.exe", "10.0.0.2:2", "UDP");
        store.Observe("Game.exe", "10.0.0.3:3", "UDP");
        store.Observe("Game.exe", "10.0.0.4:4", "UDP");
        await store.FlushAsync(TestContext.Current.CancellationToken);

        await store.MeasureRealPingAsync(["Game.exe"], maxEndpointsPerApp: 2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, probeCalls); // uygulama başına en fazla 2 uç nokta ölçülür
    }

    [Fact]
    public async Task MeasureRealPingAsync_EmptyAppsOrNoRows_ReturnsEmpty()
    {
        var store = new GpnAppEndpointStore(probe: (_, _) => Task.FromResult(10));
        Assert.Empty(await store.MeasureRealPingAsync([], ct: TestContext.Current.CancellationToken));
        Assert.Empty(await store.MeasureRealPingAsync(["NeverSeen.exe"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExtractHost_And_TryGetPort_ParseEndpointForms()
    {
        Assert.Equal("1.2.3.4", GpnAppEndpointStore.ExtractHost("1.2.3.4:51820"));
        Assert.Equal("::1", GpnAppEndpointStore.ExtractHost("[::1]:443"));
        Assert.Equal("host", GpnAppEndpointStore.ExtractHost("host"));
        Assert.Null(GpnAppEndpointStore.ExtractHost("*"));
        Assert.Null(GpnAppEndpointStore.ExtractHost(""));

        Assert.True(GpnAppEndpointStore.TryGetPort("1.2.3.4:51820", out var p1) && p1 == 51820);
        Assert.True(GpnAppEndpointStore.TryGetPort("[::1]:443", out var p2) && p2 == 443);
        Assert.False(GpnAppEndpointStore.TryGetPort("1.2.3.4", out _));
        Assert.False(GpnAppEndpointStore.TryGetPort("", out _));
        Assert.False(GpnAppEndpointStore.TryGetPort("*", out _));
    }

    [Fact]
    public void SqlEscape_DoublesSingleQuotes()
    {
        Assert.Equal("it''s", GpnAppEndpointStore.SqlEscape("it's"));
    }
}
