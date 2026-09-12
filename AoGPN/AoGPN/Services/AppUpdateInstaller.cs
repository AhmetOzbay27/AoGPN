using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using ServiceLib.Services;

namespace AoGPN.Services;

/// <summary>
/// Uygulama güncellemesinin makine tarafı: yeni sürüm paketini indirir,
/// ardından <c>AoGPN.Updater.exe</c> yan uygulamasını başlatır ve ana sürecin
/// kapanmasına izin verir.
///
/// Sorumluluk ayrımı (kasıtlı):
///   * <see cref="AppUpdateChecker"/> — "yeni sürüm var mı?" (saf HTTP + karşılaştırma)
///   * <b>bu sınıf</b>          — "indir ve devret" (dosya + süreç yönetimi)
///   * <c>AoGPN.Updater</c>     — "dosyaların yerine koy ve yeniden başlat"
///
/// Ağ yönlendirme motoruna, WinDivert katmanına ve Tier 4 optimizasyonlarına
/// DOKUNMAZ; yalnızca bir HTTPS indirmesi ve bir süreç başlatmasıdır.
/// </summary>
public sealed class AppUpdateInstaller
{
    private const string Tag = "AppUpdate";

    /// <summary>Yan güncelleyicinin dosya adı (kurulum klasöründe AoGPN.exe'nin yanında).</summary>
    public const string UpdaterExeName = AppUpdateUpdaterContract.UpdaterExeName;

    /// <summary>Güncelleyicinin ana süreci beklemesi için tanınan üst sınır.</summary>
    public const int DefaultWaitSeconds = AppUpdateUpdaterContract.DefaultWaitSeconds;

    private readonly HttpClient _httpClient;

    public AppUpdateInstaller(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // İndirme sırasında otomatik yönlendirme izlenir; GitHub varlık
            // adresleri objects.githubusercontent.com'a yönlendirir.
            AllowAutoRedirect = true,
        };

        var client = new HttpClient(handler)
        {
            // Paket birkaç yüz MB olabilir; toplam süre değil, hareketsizlik sınırı
            // anlamlıdır. Bu yüzden geniş bir üst sınır verilir.
            Timeout = TimeSpan.FromMinutes(30),
        };

        // Bazı CDN'ler/DEP'ler User-Agent'sız istekleri reddeder.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AoGPN", "1.0"));
        return client;
    }

    /// <summary>
    /// Kurulum klasöründeki yan güncelleyicinin yolu; yoksa null.
    /// (Yol çözümlemesi tek yerde: <see cref="AppUpdaterLauncher"/>.)
    /// </summary>
    public static string? ResolveUpdaterPath() => AppUpdaterLauncher.ResolveUpdaterPath();

    /// <summary>
    /// Güncellemenin uygulanacağı klasör (çalışan AoGPN.exe'nin klasörü).
    /// </summary>
    public static string ResolveTargetDirectory() => AppUpdaterLauncher.ResolveTargetDirectory();

    /// <summary>
    /// Yeni sürüm paketini geçici klasöre indirir. İndirme sırasında
    /// <paramref name="progress"/> 0..1 aralığında yüzde bildirir (toplam boyut
    /// bilinmiyorsa hiç bildirilmez). Dönen değer indirilen dosyanın yoludur.
    /// </summary>
    public async Task<string> DownloadAsync(
        AppUpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var url = update.AssetUrl;
        if (url.IsNullOrEmpty())
        {
            throw new InvalidOperationException("Güncelleme paketinin indirme adresi yok.");
        }

        var fileName = BuildDownloadFileName(update);
        var targetPath = Utils.GetTempPath(fileName);

        Logging.SaveLog($"[{Tag}] Güncelleme indiriliyor: {update.Tag} → {targetPath}");

        using var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 404 burada neredeyse her zaman iki şeyden biridir: yayın varlığı
            // kaldırılmıştır ya da depo anonim isteklere kapalıdır (GitHub özel
            // depolara 403 değil 404 döner). Kullanıcıya çıplak bir durum kodu
            // göstermek yerine nedeni söylenir — sürüm denetimi ile aynı teşhis.
            var neden = response.StatusCode == HttpStatusCode.NotFound
                ? " Paket adresi bulunamadı (404): yayın varlığı kaldırılmış olabilir ya da depo herkese açık değil."
                : string.Empty;
            Logging.SaveLog($"[{Tag}] İndirme başarısız: {(int)response.StatusCode} {response.ReasonPhrase} → {url}{neden}");
            throw new HttpRequestException(
                $"Güncelleme paketi indirilemedi ({(int)response.StatusCode} {response.ReasonPhrase}).{neden}");
        }

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;

            if (totalBytes is > 0)
            {
                progress?.Report(Math.Clamp((double)copied / totalBytes.Value, 0, 1));
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

        Logging.SaveLog($"[{Tag}] İndirme tamamlandı: {copied} bayt");
        progress?.Report(1);
        return targetPath;
    }

    /// <summary>
    /// Yan güncelleyiciyi başlatır. Bu çağrıdan sonra ana uygulama DERHAL
    /// kapanmalıdır (<c>MainWindow.ExitApplicationSafelyAsync</c>): güncelleyici
    /// <paramref name="waitProcessId"/> süreci bitene kadar bekler, sonra dosyaları
    /// değiştirip uygulamayı yeniden başlatır.
    /// </summary>
    /// <returns>Başlatıldıysa true; hata mesajı <paramref name="error"/> içinde döner.</returns>
    public bool TryLaunchUpdater(string zipPath, int waitProcessId, out string? error)
    {
        // Yol çözümlemesi + argüman sözleşmesi + süreç başlatma tek bir yerde
        // (AppUpdaterLauncher) tutulur; buradaki tek iş günlüğe yazmaktır.
        if (!AppUpdaterLauncher.TryStart(zipPath, waitProcessId, out error))
        {
            return false;
        }

        Logging.SaveLog($"[{Tag}] Güncelleyici başlatıldı; ana uygulama kapanıyor.");
        return true;
    }

    // ── Saf (test edilebilir) yardımcılar ────────────────────────────────

    /// <summary>
    /// Geçici indirme dosyasının adı. Sürüm etiketi adın içine gömülür ki yarıda
    /// kalan bir indirme sonraki denemede ayırt edilebilsin.
    /// </summary>
    internal static string BuildDownloadFileName(AppUpdateInfo update)
    {
        var tag = update.Tag.IsNotEmpty() ? update.Tag : update.Version;
        var sanitized = new string((tag ?? "guncelleme")
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-')
            .ToArray());

        return $"AoGPN-update-{sanitized}.zip";
    }

}
