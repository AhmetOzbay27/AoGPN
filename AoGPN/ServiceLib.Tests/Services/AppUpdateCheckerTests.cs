using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// AppUpdateChecker — GitHub Releases API tabanlı uygulama güncelleme denetimi.
/// Testler gerçek ağa ÇIKMAZ: HTTP istemcisi sahte bir işleyiciyle verilir,
/// saat dışarıdan sürülür.
/// </summary>
public sealed class AppUpdateCheckerTests
{
    private const string ReleaseJson =
        """
        {
          "tag_name": "v99.0.0",
          "name": "99.0.0 — test sürümü",
          "html_url": "https://github.com/owner/repo/releases/tag/v99.0.0",
          "body": "değişiklik notları",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "AoGPN-windows-64.zip", "browser_download_url": "https://example.test/win64.zip", "size": 123456 },
            { "name": "AoGPN-linux-64.zip",   "browser_download_url": "https://example.test/linux64.zip", "size": 654321 }
          ]
        }
        """;

    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    // ── Adres kurulumu ──────────────────────────────────────────────────

    [Fact]
    public void BuildLatestReleaseApiUrl_UsesTheGitHubApiEndpoint()
    {
        AppUpdateChecker.BuildLatestReleaseApiUrl("owner/repo")
            .Should().Be("https://api.github.com/repos/owner/repo/releases/latest");
    }

    [Fact]
    public void BuildReleasesApiUrl_ListsReleasesWithoutTheLatestSuffix()
    {
        AppUpdateChecker.BuildReleasesApiUrl("owner/repo")
            .Should().Be("https://api.github.com/repos/owner/repo/releases");
    }

    [Theory]
    [InlineData(" owner/repo ")]
    [InlineData("owner/repo/")]
    [InlineData("/owner/repo")]
    public void BuildLatestReleaseApiUrl_NormalisesTheRepositorySlug(string slug)
    {
        var url = AppUpdateChecker.BuildLatestReleaseApiUrl(slug);

        url.Should().Be("https://api.github.com/repos/owner/repo/releases/latest");
        // Path.Combine Windows'ta '…/releases\latest' üretiyordu ve istek 404
        // dönüyordu; adres her platformda '/' ile kurulmalı.
        url.Should().NotContain("\\");
    }

    [Fact]
    public void ResolveRepositorySlug_ComesFromTheSingleCoreUrlSource()
    {
        // Depo adı YALNIZCA Global.CoreUrls içinde tanımlıdır; bu test onun
        // doldurulmuş olduğunu garanti eder (boşsa güncelleme sessizce ölür).
        var slug = AppUpdateChecker.ResolveRepositorySlug();

        slug.Should().NotBeNullOrWhiteSpace();
        slug.Should().Contain("/");
        slug.Should().NotContain(" ");

        Global.CoreUrls[ECoreType.AoGPN].Trim('/').Should().Be(slug);
    }

    // ── Sürüm karşılaştırma ─────────────────────────────────────────────

    [Theory]
    [InlineData("v7.26.71", "7.26.70", true)]
    [InlineData("7.26.71", "7.26.70", true)]
    [InlineData("v8.0.0", "7.26.70", true)]
    [InlineData("v7.27.0", "7.26.70", true)]
    [InlineData("v7.26.70", "7.26.70", false)]
    [InlineData("v7.26.69", "7.26.70", false)]
    [InlineData("v7.26", "7.26.70", false)]
    [InlineData("v7.27", "7.26.70", true)]
    public void IsNewer_ComparesSemanticVersions(string remote, string local, bool expected)
    {
        AppUpdateChecker.IsNewer(remote, local).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void IsNewer_KeepsWorkingForUnparseableTags(string? remote)
    {
        // Bozuk bir etiket kullanıcıyı sonsuz güncelleme döngüsüne sokmamalı.
        AppUpdateChecker.IsNewer(remote, "7.26.70").Should().BeFalse();
    }

    // ── Varlık (asset) seçimi ───────────────────────────────────────────

    [Fact]
    public void SelectAsset_PrefersTheExactExpectedName()
    {
        var assets = new List<GitHubReleaseAsset>
        {
            new() { Name = "AoGPN-windows-arm64.zip", BrowserDownloadUrl = "https://example.test/arm.zip" },
            new() { Name = "AoGPN-windows-64.zip", BrowserDownloadUrl = "https://example.test/x64.zip" },
        };

        var selected = AppUpdateChecker.SelectAsset(assets);

        selected.Should().NotBeNull();
        selected!.BrowserDownloadUrl.Should().Be("https://example.test/x64.zip");
    }

    [Fact]
    public void SelectAsset_ToleratesCaseAndSuffixDifferences()
    {
        var assets = new List<GitHubReleaseAsset>
        {
            new() { Name = "AoGPN-WINDOWS-64.ZIP", BrowserDownloadUrl = "https://example.test/upper.zip" },
        };

        AppUpdateChecker.SelectAsset(assets)?.BrowserDownloadUrl.Should().Be("https://example.test/upper.zip");
    }

    [Fact]
    public void SelectAsset_ReturnsNullWhenNoPlatformAssetIsPublished()
    {
        var assets = new List<GitHubReleaseAsset>
        {
            new() { Name = "AoGPN-freebsd-64.zip" },
            new() { Name = null },
        };

        AppUpdateChecker.SelectAsset(assets).Should().BeNull();
        AppUpdateChecker.SelectAsset(null).Should().BeNull();
    }

    [Fact]
    public void ExpectedAssetName_MatchesThePlatformDownloadTemplate()
    {
        var expected = AppUpdateChecker.ExpectedAssetName();

        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            expected.Should().Be("AoGPN-windows-64.zip");
            return;
        }

        // Diğer platformlar: ya desteklenmiyordur ya da bir zip adı döner.
        if (expected is not null)
        {
            expected.Should().EndWith(".zip");
        }
    }

    // ── Uçtan uca denetim + API limiti koruması ─────────────────────────

    [Fact]
    public async Task CheckNowAsync_ReturnsTheNewerReleaseAndSendsAValidUserAgent()
    {
        var handler = new StubHandler(_ => Json(ReleaseJson));
        var checker = new AppUpdateChecker(new HttpClient(handler), () => T0);

        var update = await checker.CheckNowAsync(cancellationToken: TestContext.Current.CancellationToken);

        update.Should().NotBeNull();
        update!.Tag.Should().Be("v99.0.0");
        update.Version.Should().Be("99.0.0");
        update.IsNewer.Should().BeTrue();
        update.AssetSize.Should().Be(123456);

        // GitHub kimlik doğrulamasız isteklerde User-Agent'ı ZORUNLU tutar.
        handler.LastRequest!.Headers.UserAgent.Should().NotBeEmpty();
        handler.LastRequest.Headers.UserAgent.ToString().Should().Contain("AoGPN");
        handler.LastRequest.Headers.Accept.ToString().Should().Contain("application/vnd.github+json");

        // Platformumuza uyan varlık seçilmiş olmalı (yoksa null kalır ve indirme yapılamaz).
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            update.AssetUrl.Should().Be("https://example.test/win64.zip");
        }
    }

    [Fact]
    public async Task CheckAsync_ThrottlesRepeatedChecksToProtectTheApiLimit()
    {
        var handler = new StubHandler(_ => Json(ReleaseJson));
        var clock = T0;
        var checker = new AppUpdateChecker(new HttpClient(handler), () => clock);

        var first = await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        var second = await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        // İkinci çağrı aralık (6 saat) dolmadığı için API'ye HİÇ gitmemeli.
        handler.Calls.Should().Be(1);
        second.Should().BeSameAs(first);

        // Aralık dolduğunda yeniden denetlenir.
        clock = T0 + AppUpdateChecker.MinCheckInterval + TimeSpan.FromMinutes(1);
        await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CheckNowAsync_ReturnsNullOnErrorResponsesInsteadOfThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = new AppUpdateChecker(new HttpClient(handler), () => T0);

        var update = await checker.CheckNowAsync(cancellationToken: TestContext.Current.CancellationToken);

        update.Should().BeNull();
    }

    [Fact]
    public async Task CheckNowAsync_CreatesNoFirstChanceExceptionForAnExpected404()
    {
        // Bu test bildirilen bir belirtiyi kilitler: 404 beklenen bir SONUÇTUR
        // (kararlı yayın olmayabilir). Eskiden burada HttpRequestException
        // atılıyordu; üst katman yakalasa bile Visual Studio ilk şans istisnası
        // olarak yazıyordu ve hata ayıklama günlüğü bu yüzden 22 MB'a şişmişti.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = new AppUpdateChecker(new HttpClient(handler), () => T0);

        var atilan = 0;
        void Say(object? _, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is HttpRequestException)
            {
                Interlocked.Increment(ref atilan);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += Say;
        try
        {
            (await checker.CheckNowAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeNull();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Say;
        }

        atilan.Should().Be(0);
    }

    [Fact]
    public async Task CheckNowAsync_DiagnosesAMissingRepositoryAsNotVisible()
    {
        // Depo özel (private) ya da silinmiş: /releases/latest de /releases de 404.
        // Ayırt edici bilgi budur — "yayın yok" ile karıştırılmamalıdır.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = new AppUpdateChecker(new HttpClient(handler), () => T0);

        (await checker.CheckNowAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeNull();

        checker.LastFailure.Should().Be(AppUpdateCheckFailure.RepositoryNotVisible);
        // İki uç da sorulur: ayrım ancak liste ucundan sonra kesinleşir.
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CheckNowAsync_FallsBackToTheReleasesListWhenLatestIsMissing()
    {
        // /releases/latest 404 verir (depoda kararlı yayın yok) ama /releases 200:
        // kararlı bir sürüm varsa BULUNMALIDIR — "yayın yok" sanıp vazgeçmek,
        // güncellemeyi sessizce kapatırdı.
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json($"[{ReleaseJson}]"));
        var checker = new AppUpdateChecker(new HttpClient(handler), () => T0);

        var update = await checker.CheckNowAsync(cancellationToken: TestContext.Current.CancellationToken);

        update.Should().NotBeNull();
        update!.Tag.Should().Be("v99.0.0");
        checker.LastFailure.Should().Be(AppUpdateCheckFailure.None);
    }

    [Fact]
    public async Task CheckNowAsync_ReportsNoStableReleaseWhenOnlyPrereleasesExist()
    {
        // Depo görünüyor ama yalnızca ön sürümler yayınlanmış. Otomatik denetim bir
        // ön sürüme YÜKSELTMEZ; yine de nedenini doğru bildirir (aksi halde
        // "depo yok" ile karışırdı).
        var onSurum = ReleaseJson.Replace("\"prerelease\": false", "\"prerelease\": true");
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json($"[{onSurum}]"));
        var checker = new AppUpdateChecker(new HttpClient(handler), () => T0);

        (await checker.CheckNowAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeNull();

        checker.LastFailure.Should().Be(AppUpdateCheckFailure.NoPublishedRelease);
    }

    [Fact]
    public async Task CheckNowAsync_ReportsRateLimitingSeparatelyFromOtherHttpErrors()
    {
        // Kimliksiz GitHub API limiti 403 ile gelir; bu geçicidir ve 500'den ayrı
        // teşhis edilmelidir (ilk çare beklemek, çare değil yapılandırmayı kurcalamak).
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var checker = new AppUpdateChecker(new HttpClient(handler), () => T0);

        (await checker.CheckNowAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeNull();

        checker.LastFailure.Should().Be(AppUpdateCheckFailure.RateLimited);
    }

    [Fact]
    public async Task CheckAsync_DoesNotLockOutUpdatesForSixHoursAfterATransientFailure()
    {
        // Başarısız denetim, başarılı bir sonuç gibi önbelleğe alınmamalıdır.
        var durum = HttpStatusCode.InternalServerError;
        var handler = new StubHandler(_ => durum == HttpStatusCode.OK
            ? Json(ReleaseJson)
            : new HttpResponseMessage(durum));
        var clock = T0;
        var checker = new AppUpdateChecker(new HttpClient(handler), () => clock);

        (await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeNull();
        (await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().BeNull();

        // Aynı saniye içinde yeniden sorulmaz (limiti koruyan kısa fren).
        handler.Calls.Should().Be(1);
        checker.LastFailure.Should().Be(AppUpdateCheckFailure.HttpError);

        durum = HttpStatusCode.OK;
        clock = T0 + AppUpdateChecker.FailureRetryInterval + TimeSpan.FromSeconds(1);

        // Altı saat BEKLENMEZ: geçici kesinti güncellemeleri kapatmamalıdır.
        (await checker.CheckAsync(cancellationToken: TestContext.Current.CancellationToken)).Should().NotBeNull();
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CheckNowAsync_ParsesTheReleasesListWhenPrereleasesAreRequested()
    {
        var handler = new StubHandler(_ => Json($"[{ReleaseJson}]"));
        var checker = new AppUpdateChecker(new HttpClient(handler), () => T0);

        var update = await checker.CheckNowAsync(includePrerelease: true, cancellationToken: TestContext.Current.CancellationToken);

        update.Should().NotBeNull();
        update!.Tag.Should().Be("v99.0.0");
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("/releases");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) // 200 + JSON gövdesi
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }
}
