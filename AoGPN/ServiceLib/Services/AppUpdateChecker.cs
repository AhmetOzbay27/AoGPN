using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace ServiceLib.Services;

/// <summary>
/// Bulunan yeni sürümün tarifesi (indirilecek varlık dahil).
/// </summary>
/// <param name="Tag">GitHub etiketi (ör. <c>v7.26.70</c>).</param>
/// <param name="Version">Etiketten arındırılmış sürüm (ör. <c>7.26.70</c>).</param>
/// <param name="Title">Sürüm başlığı (release name; boş olabilir).</param>
/// <param name="Notes">Sürüm notları (release body; boş olabilir).</param>
/// <param name="HtmlUrl">İnsan tarafından okunabilir sürüm sayfası.</param>
/// <param name="AssetName">Seçilen platform varlığının adı.</param>
/// <param name="AssetUrl">İndirme adresi (yoksa null — çağıran yedek şablonu kullanır).</param>
/// <param name="AssetSize">Varlık boyutu (bayt; bilinmiyorsa 0).</param>
/// <param name="Prerelease">Ön sürüm mü?</param>
/// <param name="IsNewer">Yerel sürümden yeni mi?</param>
public sealed record AppUpdateInfo(
    string Tag,
    string Version,
    string? Title,
    string? Notes,
    string? HtmlUrl,
    string? AssetName,
    string? AssetUrl,
    long AssetSize,
    bool Prerelease,
    bool IsNewer);

/// <summary>
/// Ana uygulamanın (AoGPN) kendi güncellemesini GitHub Releases API'sinden
/// denetleyen servis.
///
/// GitHub API sözleşmesi (kimlik doğrulaması TAŞIMAYAN yol):
///   GET https://api.github.com/repos/{owner}/{repo}/releases/latest
///
/// DEPO KAMUYA AÇIK OLMAK ZORUNDADIR. GitHub, özel (private) depolara yapılan
/// anonim isteklere 403 değil **404** döndürür; aynı şey yayın varlığının indirme
/// adresi için de geçerlidir. Görünürlük yanlışsa güncelleme hattı hiçbir sürümde
/// çalışmaz ve geriye kalan tek belirti bir 404'tür — bu yüzden bu durum bir
/// istisna gibi değil, açık bir YAPILANDIRMA teşhisi olarak günlüğe yazılır
/// (bkz. <see cref="AppUpdateCheckFailure.RepositoryNotVisible"/>).
///
/// İkinci tuzak: <c>/releases/latest</c> YALNIZCA ön sürüm olmayan yayınları görür
/// ve hiç kararlı yayın yoksa depo var olsa bile 404 verir. Bu yüzden 404 tek
/// başına "depo yok" demek değildir; ayrımı <c>/releases</c> listesine düşerek
/// yapılır.
///
/// Neden bu uç: <c>…/releases/latest</c> yönlendirmesi etiketi yalnızca
/// Location başlığından okutur ve ön sürümleri atlar; API ucu ise platform
/// varlığını (asset) adı ve boyutuyla birlikte verir — bu yüzden indirme
/// adresi dosya adı uydurularak değil, listeden SEÇİLEREK bulunur.
///
/// Depo adı TEK yerde tanımlıdır: <see cref="Global.CoreUrls"/> içindeki
/// <c>ECoreType.AoGPN</c> girdisi. Buradaki hiçbir sabit depo adı taşımaz —
/// böylece çekirdek güncelleme yolu ile uygulama güncelleme yolu asla
/// birbirinden sapamaz.
///
/// GitHub, kimlik doğrulamasız isteklerde IP başına saatte 60 isteğe izin verir.
/// Bu servis bu yüzden:
///   * her çağrıya geçerli bir <c>User-Agent</c> ekler (GitHub'ın ZORUNLU
///     başlığıdır; eksikse 403 döner),
///   * süreç başına en fazla bir denetim yapar ve sonuçları
///     <see cref="MinCheckInterval"/> boyunca yeniden kullanır.
///
/// Yönlendirme motoruna, WinDivert katmanına ve Tier 4 optimizasyonlarına
/// DOKUNMAZ: yalnızca bir HTTP GET ve bir karşılaştırmadır.
/// </summary>
public enum AppUpdateCheckFailure
{
    /// <summary>Denetim tamamlandı (yeni sürüm olmasa da sonuç geçerlidir).</summary>
    None = 0,

    /// <summary>Depo adı tanımsız/boş — <see cref="Global.CoreUrls"/> doldurulmalı.</summary>
    Misconfigured,

    /// <summary>Depo anonim isteklerde görünmüyor (404): özel (private) ya da silinmiş.</summary>
    RepositoryNotVisible,

    /// <summary>Depo görünüyor ama hiç kararlı (ön sürüm olmayan) yayın yok.</summary>
    NoPublishedRelease,

    /// <summary>Kimliksiz istek limiti aşıldı (403/429).</summary>
    RateLimited,

    /// <summary>Sunucu beklenmeyen bir durum kodu döndürdü (5xx vb.).</summary>
    HttpError,

    /// <summary>Ağ/TLS/zaman aşımı — geçici olabilir.</summary>
    Network,
}

public sealed class AppUpdateChecker
{
    private const string Tag = "AppUpdate";
    private const string UserAgentProduct = "AoGPN";

    /// <summary>İki denetim arasındaki en kısa süre (API limiti koruması).</summary>
    public static readonly TimeSpan MinCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// BAŞARISIZ bir denetimden sonra yeniden denemeden önce beklenecek süre.
    /// <see cref="MinCheckInterval"/> yalnızca başarılı sonuç için geçerlidir:
    /// geçici bir ağ kesintisi güncellemeleri altı saat kapatmamalıdır, ama aynı
    /// saniye içinde tekrar tekrar da sorulmamalıdır.
    /// </summary>
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(1);

    private static readonly Lazy<AppUpdateChecker> _instance = new(() => new AppUpdateChecker());

    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private DateTimeOffset? _lastAttemptAt;
    private bool _lastAttemptFailed;
    private AppUpdateInfo? _lastResult;
    private readonly HashSet<AppUpdateCheckFailure> _loglananlar = [];

    public static AppUpdateChecker Instance => _instance.Value;

    /// <summary>
    /// Son denetimin başarısızlık nedeni; denetim yapılabildiyse
    /// <see cref="AppUpdateCheckFailure.None"/>. <c>null</c> dönmek tek başına
    /// yeterli değildir: "yeni sürüm yok" ile "denetim yapılamadı" aynı şeye
    /// benzememelidir — ilki normaldir, ikincisi yapılandırma ya da ağ sorunudur.
    /// </summary>
    public AppUpdateCheckFailure LastFailure { get; private set; }

    /// <summary>
    /// Testler için: istemci ve saat dışarıdan verilebilir (gerçek ağ yok).
    /// </summary>
    internal AppUpdateChecker(HttpClient? httpClient = null, Func<DateTimeOffset>? clock = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            // Kısa zaman aşımı: açılış akışı bir güncelleme denetimini beklemez.
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// GitHub sözleşmesine uygun istek üretir. Başlıklar bilerek HER İSTEĞE ayrı
    /// ayrı eklenir (istemci varsayılanlarına değil): böylece dışarıdan enjekte
    /// edilen bir istemciyle de doğru başlıklar gider ve sözleşme test edilebilir
    /// kalır. User-Agent GitHub'ın ZORUNLU başlığıdır — eksikse istek 403 döner.
    /// </summary>
    private static HttpRequestMessage BuildRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProduct, SafeVersionForUserAgent()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    /// <summary>
    /// Kullanıcı aracısı için güvenli sürüm: HTTP başlığı yalnızca yazdırılabilir
    /// ASCII taşıyabilir, bu yüzden boşluk/ayrık karakterler kırpılır.
    /// </summary>
    private static string SafeVersionForUserAgent()
    {
        var version = Utils.GetVersionInfo();
        if (version.IsNullOrEmpty())
        {
            return "0.0";
        }

        var sanitized = new string(version.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return sanitized.IsNullOrEmpty() ? "0.0" : sanitized;
    }

    /// <summary>
    /// Sürümü denetler. <see cref="MinCheckInterval"/> içindeki tekrarlar API'ye
    /// hiç gitmez (sonuç yeniden kullanılır) — açılışta bir kez çağrılmak üzere
    /// tasarlanmıştır.
    /// </summary>
    /// <returns>Yeni sürüm yoksa veya denetim yapılamazsa null.</returns>
    public async Task<AppUpdateInfo?> CheckAsync(bool includePrerelease = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = _clock();
            if (_lastAttemptAt is { } last)
            {
                var bekleme = _lastAttemptFailed ? FailureRetryInterval : MinCheckInterval;
                if (now - last < bekleme)
                {
                    // Başarısızlık "sonuç" gibi önbelleğe alınmaz: yalnızca kısa
                    // bir süre yeniden denenmez. Eskiden başarısız denetim de
                    // MinCheckInterval boyunca geçerli sayılıyordu, yani geçici
                    // bir kesinti güncellemeleri altı saat kapatıyordu.
                    return _lastAttemptFailed ? null : _lastResult;
                }
            }

            _lastAttemptAt = now;
        }

        var result = await CheckNowAsync(includePrerelease, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _lastAttemptFailed = LastFailure != AppUpdateCheckFailure.None;
            _lastAttemptAt = _clock();
            _lastResult = result;
        }

        return result;
    }

    /// <summary>
    /// Kısıtlama (throttle) uygulamadan denetler; testlerin ve elle
    /// "güncellemeyi denetle" girişinin kullandığı yol.
    /// </summary>
    public Task<AppUpdateInfo?> CheckNowAsync(bool includePrerelease = false, CancellationToken cancellationToken = default)
        => includePrerelease
            ? CheckReleasesAsync(includePrerelease: true, cancellationToken)
            : CheckLatestAsync(cancellationToken);

    private async Task<AppUpdateInfo?> CheckLatestAsync(CancellationToken cancellationToken)
    {
        LastFailure = AppUpdateCheckFailure.None;

        if (!TryResolveSlug(out var slug))
        {
            return null;
        }

        try
        {
            // 1) Kararlı sürüm ucu. NOT: hiç kararlı yayın yoksa (depoda yalnızca
            //    ön sürümler varsa) GitHub bu uçta depo var olsa bile 404 döner —
            //    bu yüzden 404'ün anlamı tek başına belirsizdir.
            var latestUrl = BuildLatestReleaseApiUrl(slug);
            var (latestDurum, latestYayin) = await GetLatestReleaseAsync(latestUrl, cancellationToken).ConfigureAwait(false);

            if (IsSuccess(latestDurum))
            {
                if (latestYayin is null)
                {
                    ReportFailure(AppUpdateCheckFailure.HttpError, latestUrl, "GitHub sürüm yanıtı çözümlenemedi.");
                    return null;
                }

                return ToUpdateInfo(latestYayin, allowPrerelease: false);
            }

            if (latestDurum != HttpStatusCode.NotFound)
            {
                ReportFailure(Sinifla(latestDurum), latestUrl, null);
                return null;
            }

            // 2) Liste ucuna düşülür: "depo görünmüyor" ile "kararlı yayın yok"
            //    ayrımını kesinleştiren tek şey budur. Ayrıca yeni bir depoda
            //    /releases/latest 404 verirken kararlı bir yayın bulunabilir.
            var listUrl = BuildReleasesApiUrl(slug);
            var (listeDurum, liste) = await GetReleasesAsync(listUrl, cancellationToken).ConfigureAwait(false);

            if (listeDurum == HttpStatusCode.NotFound)
            {
                ReportFailure(AppUpdateCheckFailure.RepositoryNotVisible, listUrl, RepoGorunurlukCozumu(slug));
                return null;
            }

            if (!IsSuccess(listeDurum))
            {
                ReportFailure(Sinifla(listeDurum), listUrl, null);
                return null;
            }

            var kararli = SelectNewest(liste, includePrerelease: false);
            if (kararli is not null)
            {
                return ToUpdateInfo(kararli, allowPrerelease: false);
            }

            var onSurum = SelectNewest(liste, includePrerelease: true);
            ReportFailure(
                AppUpdateCheckFailure.NoPublishedRelease,
                latestUrl,
                onSurum?.TagName is { Length: > 0 } etiket
                    ? $"Depoda kararlı yayın yok; yalnızca ön sürümler var (en yeni: {etiket}). "
                      + "Ön sürümler otomatik güncelleme olarak sunulmaz."
                    : "Depoda henüz yayınlanmış bir sürüm yok.");
            return null;
        }
        catch (Exception ex)
        {
            // Buraya yalnızca gerçek taşıma hataları (DNS/TLS/zaman aşımı) düşer:
            // beklenen HTTP sonuçları artık istisna üretmiyor.
            ReportFailure(AppUpdateCheckFailure.Network, null, ex.Message, ex);
            return null;
        }
    }

    private async Task<AppUpdateInfo?> CheckReleasesAsync(bool includePrerelease, CancellationToken cancellationToken)
    {
        LastFailure = AppUpdateCheckFailure.None;

        if (!TryResolveSlug(out var slug))
        {
            return null;
        }

        var url = BuildReleasesApiUrl(slug);
        try
        {
            var (durum, liste) = await GetReleasesAsync(url, cancellationToken).ConfigureAwait(false);

            if (durum == HttpStatusCode.NotFound)
            {
                ReportFailure(AppUpdateCheckFailure.RepositoryNotVisible, url, RepoGorunurlukCozumu(slug));
                return null;
            }

            if (!IsSuccess(durum))
            {
                ReportFailure(Sinifla(durum), url, null);
                return null;
            }

            var yayin = SelectNewest(liste, includePrerelease);
            if (yayin is null)
            {
                ReportFailure(AppUpdateCheckFailure.NoPublishedRelease, url, null);
                return null;
            }

            return ToUpdateInfo(yayin, allowPrerelease: true);
        }
        catch (Exception ex)
        {
            ReportFailure(AppUpdateCheckFailure.Network, url, ex.Message, ex);
            return null;
        }
    }

    /// <summary>
    /// Kararlı sürüm ucunu okur (<c>/releases/latest</c>): tek bir yayın nesnesi döner.
    ///
    /// Beklenen HTTP sonuçları (404/403/5xx) için İSTİSNA ATILMAZ. Eskiden 404 burada
    /// <c>HttpRequestException</c> olarak atılıyordu; üst katmanda yakalansa bile Visual
    /// Studio her atış için bir "ilk şans istisnası" satırı yazıyordu ve raporlanan
    /// hata ayıklama günlüğündeki gürültünün kaynağı tam olarak buydu. Taşıma hataları
    /// (DNS/TLS/zaman aşımı) yine istisnadır ve çağıranda tek bir yerde karşılanır.
    /// </summary>
    private async Task<(HttpStatusCode Durum, GitHubRelease? Yayin)> GetLatestReleaseAsync(string url, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(url);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, JsonUtils.Deserialize<GitHubRelease>(body));
    }

    /// <summary>Yayın listesi ucunu okur (<c>/releases</c>): dizi döner. Aynı istisna kuralı geçerlidir.</summary>
    private async Task<(HttpStatusCode Durum, List<GitHubRelease>? Liste)> GetReleasesAsync(string url, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(url);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, JsonUtils.Deserialize<List<GitHubRelease>>(body));
    }

    /// <summary>
    /// Depo kimliğini çözer; tanımsızsa teşhisi yazıp false döner (istisna atmaz —
    /// yanlış yapılandırma bir çökme değil, bir teşhis satırıdır).
    /// </summary>
    private bool TryResolveSlug(out string slug)
    {
        try
        {
            slug = ResolveRepositorySlug();
            return true;
        }
        catch (Exception ex)
        {
            slug = string.Empty;
            ReportFailure(AppUpdateCheckFailure.Misconfigured, null, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Başarısızlığı kaydeder ve günlüğe her neden için YALNIZCA BİR KEZ yazar —
    /// süreç ömrü boyunca tekrarlanan denetimler aynı satırı çoğaltmamalıdır.
    /// </summary>
    private void ReportFailure(AppUpdateCheckFailure neden, string? url, string? ayrinti, Exception? ex = null)
    {
        LastFailure = neden;

        bool ilkMi;
        lock (_gate)
        {
            ilkMi = _loglananlar.Add(neden);
        }

        if (!ilkMi)
        {
            return;
        }

        var ek = ayrinti.IsNotEmpty() ? $" {ayrinti}" : string.Empty;
        var adres = url.IsNotEmpty() ? $" → {url}" : string.Empty;
        if (ex is null)
        {
            Logging.SaveLog($"[{Tag}] Güncelleme denetimi yapılamadı ({neden}).{ek}{adres}");
        }
        else
        {
            Logging.SaveLog($"[{Tag}] Güncelleme denetimi yapılamadı ({neden}).{ek}{adres}", ex);
        }
    }

    private AppUpdateInfo ToUpdateInfo(GitHubRelease release, bool allowPrerelease)
    {
        var tag = release.TagName ?? string.Empty;
        var version = tag.RemovePrefix('v');
        var asset = SelectAsset(release.Assets);

        return new AppUpdateInfo(
            tag,
            version,
            release.Name,
            release.Body,
            release.HtmlUrl,
            asset?.Name,
            asset?.BrowserDownloadUrl,
            asset?.Size ?? 0,
            release.Prerelease,
            IsNewer(tag, Utils.GetVersionInfo()));
    }

    // ── Saf (test edilebilir) yardımcılar ────────────────────────────────

    /// <summary>
    /// Listedeki en yeni yayını seçer. Taslaklar hiçbir zaman sunulmaz; ön sürümler
    /// yalnızca açıkça istenirse kabul edilir, yani otomatik açılış denetimi asla
    /// kullanıcıyı bir ön sürüme yükseltmez.
    /// </summary>
    internal static GitHubRelease? SelectNewest(IEnumerable<GitHubRelease>? releases, bool includePrerelease)
        => releases?
            .Where(r => !r.Draft)
            .FirstOrDefault(r => includePrerelease || !r.Prerelease);

    /// <summary>HTTP durum kodunu teşhis nedenine eşler.</summary>
    internal static AppUpdateCheckFailure Sinifla(HttpStatusCode durum)
        => durum is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? AppUpdateCheckFailure.RateLimited
            : AppUpdateCheckFailure.HttpError;

    /// <summary>2xx = başarılı (tek yerde tanımlı, çünkü birden çok uç kullanılıyor).</summary>
    internal static bool IsSuccess(HttpStatusCode durum) => (int)durum is >= 200 and < 300;

    /// <summary>
    /// Görünürlük hatasının ne olduğunu ve nasıl düzeltileceğini tek satırda söyler.
    /// Bu satırın varlık nedeni: özel bir depoda tek belirti bir 404'tür ve
    /// "sürüm yok" sanılıp aylarca fark edilmeden kalabilir.
    /// </summary>
    internal static string RepoGorunurlukCozumu(string slug)
        => $"'{slug}' deposu anonim isteklere görünmüyor (404). GitHub özel depolara 403 değil 404 döner; "
         + "depo özel kaldıkça sürüm denetimi de paket indirme de çalışmaz. "
         + "Çözüm: depoyu herkese açık yapın ya da yayın paketlerini herkese açık bir adresten sunun.";

    /// <summary>
    /// Depo kimliğini (<c>owner/repo</c>) tek doğruluk kaynağından okur:
    /// <see cref="Global.CoreUrls"/> → <c>ECoreType.AoGPN</c>.
    /// </summary>
    internal static string ResolveRepositorySlug()
    {
        if (!Global.CoreUrls.TryGetValue(ECoreType.AoGPN, out var slug) || slug.IsNullOrEmpty())
        {
            throw new InvalidOperationException(
                "Güncelleme deposu tanımlı değil: Global.CoreUrls içindeki ECoreType.AoGPN girdisini doldurun.");
        }

        return slug.Trim().Trim('/');
    }

    /// <summary>Kararlı sürüm ucu: <c>{api}/repos/{owner}/{repo}/releases/latest</c>.</summary>
    internal static string BuildLatestReleaseApiUrl(string? repositorySlug)
        => BuildReleaseApiUrl(repositorySlug, "latest");

    /// <summary>Sürüm listesi ucu: <c>{api}/repos/{owner}/{repo}/releases</c>.</summary>
    internal static string BuildReleasesApiUrl(string? repositorySlug)
        => BuildReleaseApiUrl(repositorySlug, suffix: null);

    private static string BuildReleaseApiUrl(string? repositorySlug, string? suffix)
    {
        if (repositorySlug.IsNullOrEmpty())
        {
            throw new ArgumentException("Depo kimliği (owner/repo) boş olamaz.", nameof(repositorySlug));
        }

        // Adres BİLEREK dize birleştirmeyle kurulur: Path.Combine Windows'ta
        // ters bölü koyar ve "…/releases\latest" gibi geçersiz bir adres üretir
        // (bu hata çekirdek güncelleme yolunda canlı gözlenmişti).
        var baseUrl = $"{Global.GithubApiUrl}/{repositorySlug.Trim().Trim('/')}/releases";
        return suffix.IsNullOrEmpty() ? baseUrl : $"{baseUrl}/{suffix}";
    }

    /// <summary>
    /// Bu makinenin platformuna karşılık gelen varlığı seçer. Adlar
    /// <see cref="CoreInfoManager"/> içindeki indirme şablonlarıyla aynıdır
    /// (ör. <c>AoGPN-windows-64.zip</c>); önce tam ad, sonra büyük/küçük harf
    /// duyarsız eşleşme denenir.
    /// </summary>
    internal static GitHubReleaseAsset? SelectAsset(IEnumerable<GitHubReleaseAsset>? assets)
    {
        var expected = ExpectedAssetName();
        if (expected is null || assets is null)
        {
            return null;
        }

        var list = assets.Where(a => a.Name.IsNotEmpty()).ToList();
        return list.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.Ordinal))
            ?? list.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(a => a.Name!.EndsWith(expected, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Platforma göre beklenen yayın varlığı adı (desteklenmiyorsa null).
    /// Ortak kullanımdadır: sürüm yayınlanırken dosyanın bu adla yüklenmesi
    /// gerekir, hata ayıklarken de bu ad günlüğe yazılır.
    /// </summary>
    public static string? ExpectedAssetName()
        => ExpectedAssetName(CurrentPlatform(), RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// Platformdan bağımsız eşleme: (işletim sistemi, mimari) → yayın varlığı adı.
    ///
    /// Saf fonksiyon olarak AYRI tutulur çünkü <see cref="ExpectedAssetName()"/>
    /// yalnızca koşucunun kendi platformunu sorar; CI ubuntu'da koştuğu için
    /// "Windows istemcisi tam olarak hangi adı ister" sorusu orada
    /// cevaplanamazdı. Yayın hattının varlık adları sözleşmesi
    /// (<c>ReleasePipelineContractTests</c>) bu eşlemeyi tüm platformlar için
    /// tek tek sınar.
    /// </summary>
    internal static string? ExpectedAssetName(OSPlatform platform, Architecture architecture)
    {
        if (platform == OSPlatform.Windows)
        {
            return architecture switch
            {
                Architecture.X64 => "AoGPN-windows-64.zip",
                Architecture.Arm64 => "AoGPN-windows-arm64.zip",
                _ => null,
            };
        }

        if (platform == OSPlatform.Linux)
        {
            return architecture switch
            {
                Architecture.X64 => "AoGPN-linux-64.zip",
                Architecture.Arm64 => "AoGPN-linux-arm64.zip",
                Architecture.RiscV64 => "AoGPN-linux-riscv64.zip",
                Architecture.LoongArch64 => "AoGPN-linux-loong64.zip",
                _ => null,
            };
        }

        if (platform == OSPlatform.OSX)
        {
            return architecture switch
            {
                Architecture.X64 => "AoGPN-macos-64.zip",
                Architecture.Arm64 => "AoGPN-macos-arm64.zip",
                _ => null,
            };
        }

        return null;
    }

    /// <summary>Koşucunun işletim sistemini <see cref="OSPlatform"/> değerine çevirir.</summary>
    private static OSPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return OSPlatform.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return OSPlatform.OSX;
        }

        return OSPlatform.Create("UNKNOWN");
    }

    /// <summary>
    /// <paramref name="remoteTag"/> yerel sürümden yeni mi? İki taraf da
    /// çözümlenemezse (0.0.0) "yeni değil" kabul edilir — bozuk bir etiket
    /// kullanıcıyı sonsuz bir güncelleme döngüsüne sokmamalıdır.
    /// </summary>
    internal static bool IsNewer(string? remoteTag, string? localVersion)
    {
        if (remoteTag.IsNullOrEmpty())
        {
            return false;
        }

        var remote = new SemanticVersion(remoteTag);
        var local = new SemanticVersion(localVersion);

        if (remote == new SemanticVersion(0, 0, 0) || local == new SemanticVersion(0, 0, 0))
        {
            return false;
        }

        // SemanticVersion yalnızca >= / <= / == / != sağlar; "daha yeni" =
        // (uzak >= yerel) VE (uzak != yerel).
        return remote >= local && remote != local;
    }
}
