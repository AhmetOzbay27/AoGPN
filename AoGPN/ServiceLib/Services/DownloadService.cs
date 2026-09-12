using System.Net.Http.Headers;

namespace ServiceLib.Services;

/// <summary>
/// Download
/// </summary>
public class DownloadService
{
    public event EventHandler<UpdateResult>? UpdateCompleted;

    public event ErrorEventHandler? Error;

    private static readonly string _tag = "DownloadService";

    /// <summary>Tek bir yolda (proxy ya da doğrudan) toplam deneme sayısı — ilk + üstel geri çekilmeli tekrarlar.</summary>
    internal int MaxAttempts { get; set; } = 3;

    /// <summary>Geri çekilme taban gecikmesi; her başarısız denemede 2^n ile büyür (jitter'lı).</summary>
    internal TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Üstel geri çekilme gecikmesi: taban * 2^(n-1) ± %25 jitter (saf fonksiyon —
    /// testler sınırları doğrudan doğrular). Jitter, senkronize yeniden denemelerin
    /// (örn. güncelleme kontrolü + abonelik yenileme aynı anda) her seferinde
    /// çakışmasını önler.
    /// </summary>
    internal static TimeSpan ComputeRetryDelay(TimeSpan baseDelay, int failedAttempts)
    {
        var expMs = baseDelay.TotalMilliseconds * Math.Pow(2, failedAttempts - 1);
        var jitter = 0.75 + Random.Shared.NextDouble() * 0.5; // 0.75..1.25
        return TimeSpan.FromMilliseconds(expMs * jitter);
    }

    private TimeSpan RetryDelay(int failedAttempts)
        => ComputeRetryDelay(RetryBaseDelay, failedAttempts);

    /// <summary>
    /// Downloads data with the specified proxy and reports progress messages.
    /// </summary>
    public async Task<int> DownloadDataAsync(string url, IWebProxy webProxy, int downloadTimeout, Func<bool, string, Task> updateFunc)
    {
        try
        {
            var progress = new Progress<string>();
            progress.ProgressChanged += (sender, value) => updateFunc?.Invoke(false, $"{value}");

            await DownloaderHelper.Instance.DownloadDataAsync4Speed(webProxy,
                  url,
                  progress,
                  downloadTimeout);
        }
        catch (Exception ex)
        {
            await updateFunc?.Invoke(false, ex.Message);
            if (ex.InnerException != null)
            {
                await updateFunc?.Invoke(false, ex.InnerException.Message);
            }
        }
        return 0;
    }

    /// <summary>
    /// Downloads a file and reports progress through events.
    /// </summary>
    public async Task DownloadFileAsync(string url, string fileName, bool blProxy, int downloadTimeout)
    {
        try
        {
            UpdateCompleted?.Invoke(this, new UpdateResult(false, $"{ResUI.Downloading}   {url}"));

            var progress = new Progress<double>();
            progress.ProgressChanged += (sender, value) => UpdateCompleted?.Invoke(this, new UpdateResult(value > 100, $"...{value}%"));

            var webProxy = await GetWebProxy(blProxy);
            await DownloaderHelper.Instance.DownloadFileAsync(webProxy,
                url,
                fileName,
                progress,
                downloadTimeout);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);

            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }
    }

    /// <summary>Erişilebilirlik taraması sonucu.</summary>
    public sealed record UrlProbeResult(string Url, bool Ok, int? HttpStatus, long LatencyMs, string? Message);

    /// <summary>
    /// Bir abonelik/node kaynağının HTTP erişilebilirliğini ölçer — kaynak listesi
    /// çekilmeden önce hangi adreslerin canlı olduğunu taramak için kullanılır.
    /// Doğrudan deneme başarısız/erişilemez olursa SOCKS proxy üzerinden tekrar dener.
    /// </summary>
    public async Task<UrlProbeResult> ProbeUrlAsync(string url, bool blProxy, int timeoutSec = 5)
    {
        var webProxy = await GetWebProxy(blProxy);
        return await ProbeUrlAsync(url, webProxy, timeoutSec);
    }

    /// <summary>
    /// Belirtilen proxy ile bir kaynağın HTTP erişilebilirliğini ölçer.
    /// <c>Ok</c> yalnızca 2xx/3xx yanıtlarda; 4xx/5xx erişilebilir ama ölü (HttpStatus
    /// dolu, Ok=false), zaman aşımı/ağ hatası ise erişilemez (HttpStatus null) sayılır.
    /// </summary>
    public async Task<UrlProbeResult> ProbeUrlAsync(string url, IWebProxy? webProxy, int timeoutSec = 5)
    {
        var started = Stopwatch.StartNew();
        var timeout = Math.Max(2, timeoutSec);
        try
        {
            var connectTimeout = Math.Clamp(timeout / 2, 2, 5);
            var handler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = webProxy != null,
                AllowAutoRedirect = true,
                ConnectTimeout = TimeSpan.FromSeconds(connectTimeout)
            };
            // Yalnızca HTTP erişilebilirliği kontrol edilir — özel kök sertifika hunisi
            // (CertPemManager) gerekmez; sistem güven zinciri yeterlidir.

            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(Utils.GetVersion(false));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            return new UrlProbeResult(url, IsLiveStatus(status), status, started.ElapsedMilliseconds, status.ToString());
        }
        catch (OperationCanceledException)
        {
            return new UrlProbeResult(url, false, null, started.ElapsedMilliseconds, $"timeout ({timeout}s)");
        }
        catch (Exception ex)
        {
            return new UrlProbeResult(url, false, null, started.ElapsedMilliseconds, ex.Message.Split(['\r', '\n'])[0]);
        }
    }

    private static bool IsLiveStatus(int status) => status is >= 200 and < 400;

    /// <summary>
    /// Gets redirect target URL without following redirects automatically.
    /// </summary>
    public async Task<string?> UrlRedirectAsync(string url, bool blProxy)
        => await UrlRedirectAsync(url, await GetWebProxy(blProxy));

    /// <summary>
    /// Gets redirect target URL without following redirects automatically.
    /// Her yol (proxy / doğrudan) üstel geri çekilmeyle yeniden denenir; yerel SOCKS
    /// proxy'si üzerinden sonuç alınamazsa (tünel bozuk, core yeniden başlarken port
    /// dinlenmiyor, SSL EOF vb.) doğrudan (proxysiz) yola düşülür — güncelleme
    /// kontrolü tünel durumundan bağımsız çalışır. Her iki yol da başarısızsa
    /// StatusCode hatası bir kez raporlanır.
    /// </summary>
    public async Task<string?> UrlRedirectAsync(string url, IWebProxy? webProxy)
    {
        var location = await TryUrlRedirectWithRetryAsync(url, webProxy);
        if (location is null && webProxy is not null)
        {
            location = await TryUrlRedirectWithRetryAsync(url, null);
        }
        if (location is null)
        {
            Error?.Invoke(this, new ErrorEventArgs(new Exception("StatusCode error: " + url)));
            Logging.SaveLog("StatusCode error: " + url);
        }
        return location;
    }

    private async Task<string?> TryUrlRedirectWithRetryAsync(string url, IWebProxy? webProxy)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var location = await TryUrlRedirectAsync(url, webProxy);
            if (location is not null)
            {
                return location;
            }
            if (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay(attempt));
            }
        }
        return null;
    }

    private static async Task<string?> TryUrlRedirectAsync(string url, IWebProxy? webProxy)
    {
        try
        {
            var webRequestHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                Proxy = webProxy,
            };
            var certificateChainPolicy = CertPemManager.Instance.BuildCertificateChainPolicy();
            if (certificateChainPolicy != null)
            {
                webRequestHandler.SslOptions.CertificateChainPolicy = certificateChainPolicy;
                webRequestHandler.SslOptions.RemoteCertificateValidationCallback = null;
            }
            using var client = new HttpClient(webRequestHandler);

            using var response = await client.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location is not null)
            {
                return response.Headers.Location.ToString();
            }
        }
        catch (Exception)
        {
            // ağ/proxy hatası — çağıran doğrudan yolu dener
        }
        return null;
    }

    /// <summary>
    /// Tries to download string content using proxy switch setting.
    /// </summary>
    public async Task<string?> TryDownloadString(string url, bool blProxy, string userAgent)
    {
        var webProxy = await GetWebProxy(blProxy);
        return await TryDownloadString(url, webProxy, userAgent);
    }

    /// <summary>
    /// Tries to download string content with a specified proxy.
    /// Proxy yolu üstel geri çekilmeyle <see cref="MaxAttempts"/> kez denenir;
    /// sonuç alınamazsa doğrudan (proxysiz) yol aynı politika ile çalışır.
    /// Geçici arızalar (tünel döngüsü, core restart, SSL EOF) böylece kendiliğinden
    /// iyileşir; iki yol da tükenirse null döner.
    /// </summary>
    public async Task<string?> TryDownloadString(string url, IWebProxy? webProxy, string userAgent)
    {
        var result = await TryDownloadStringWithRetryAsync(url, webProxy, userAgent);

        // Yerel SOCKS proxy'si üzerinden ulaşılamadıysa (tünel döngüsü, core
        // restart, SSL EOF) doğrudan yolu dene — indirme kullanıcının kendi
        // internet yolundan tamamlanır, abonelik/güncelleme akışı tıkanmaz.
        if (result.IsNullOrEmpty() && webProxy is not null)
        {
            result = await TryDownloadStringWithRetryAsync(url, null, userAgent);
        }
        return result;
    }

    /// <summary>Tek bir yolu (proxy veya doğrudan) üstel geri çekilmeyle dener.</summary>
    private async Task<string?> TryDownloadStringWithRetryAsync(string url, IWebProxy? webProxy, string userAgent)
    {
        // Ölü yerel SOCKS proxy'si DETERMINISTIK bir arızadır (bağlantı reddi) —
        // yeniden denemek anlamsızdır ve DownloaderHelper'ın iç denemeleriyle
        // ~20 sn israf edip doğrudan fallback'i geciktirir. Port kapalıysa retry
        // yapmadan null dön; çağıran (TryDownloadString) doğrudan yola düşer.
        if (webProxy is not null && !await IsProxyEndpointReachableAsync(webProxy))
        {
            Logging.Verbose(_tag, "download_proxy_dead", ("url", url));
            return null;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Hata olayları yalnızca SON denemede yayınlanır — geçici bir arıza
            // ardından gelen başarı, kullanıcıya yanlış "indirme başarısız" tostu
            // bastırmaz. Ara denemeler yalnızca Verbose günlüğe düşer.
            var result = await TryDownloadStringCore(url, webProxy, userAgent, raiseErrorEvents: attempt == MaxAttempts);
            if (result.IsNotEmpty())
            {
                return result;
            }
            if (attempt < MaxAttempts)
            {
                Logging.Verbose(_tag, "download_retry", ("url", url), ("attempt", attempt), ("max", MaxAttempts));
                await Task.Delay(RetryDelay(attempt));
            }
        }
        return null;
    }

    /// <summary>
    /// Proxy uç noktasına TCP bağlantısını dener. Yerel SOCKS portu kapalıysa
    /// (core çalışmıyor, tünel yok) false — retry'ı atlayıp doğrudan yola düşmek
    /// için. Belirsiz durumda (çözümlenemeyen adres vb.) true döner; retry politikası
    /// yine de uygulanır.
    /// </summary>
    private static async Task<bool> IsProxyEndpointReachableAsync(IWebProxy webProxy)
    {
        try
        {
            var uri = webProxy.GetProxy(new Uri("http://example.com"));
            if (uri is null || uri.Port <= 0 || uri.Host.IsNullOrEmpty())
            {
                return true;
            }
            return await SocketCheck(uri.Host, uri.Port);
        }
        catch
        {
            return true; // belirsiz — retry yine de çalışsın
        }
    }

    private async Task<string?> TryDownloadStringCore(string url, IWebProxy? webProxy, string userAgent, bool raiseErrorEvents = true)
    {
        var timeout = 15;
        try
        {
            var result1 = await DownloadStringAsync(url, webProxy, userAgent, timeout, raiseErrorEvents);
            if (result1.IsNotEmpty())
            {
                return result1;
            }
        }
        catch (Exception ex)
        {
            if (raiseErrorEvents)
            {
                Logging.SaveLog(_tag, ex);
                Error?.Invoke(this, new ErrorEventArgs(ex));
                if (ex.InnerException != null)
                {
                    Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
                }
            }
        }

        try
        {
            var result2 = await DownloadStringViaDownloader(url, webProxy, userAgent, timeout, raiseErrorEvents);
            if (result2.IsNotEmpty())
            {
                return result2;
            }
        }
        catch (Exception ex)
        {
            if (raiseErrorEvents)
            {
                Logging.SaveLog(_tag, ex);
                Error?.Invoke(this, new ErrorEventArgs(ex));
                if (ex.InnerException != null)
                {
                    Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads string content via HttpClient.
    /// </summary>
    private async Task<string?> DownloadStringAsync(string url, IWebProxy? webProxy, string userAgent, int timeout, bool raiseErrorEvents = true)
    {
        try
        {
            var connectTimeout = Math.Clamp(timeout / 5, 2, 5);
            var handler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = webProxy != null,
                ConnectTimeout = TimeSpan.FromSeconds(connectTimeout)
            };
            var certificateChainPolicy = CertPemManager.Instance.BuildCertificateChainPolicy();
            if (certificateChainPolicy != null)
            {
                handler.SslOptions.CertificateChainPolicy = certificateChainPolicy;
                handler.SslOptions.RemoteCertificateValidationCallback = null;
            }

            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            if (userAgent.IsNullOrEmpty())
            {
                userAgent = Utils.GetVersion(false);
            }
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent);

            Uri uri = new(url);
            //Authorization Header
            if (uri.UserInfo.IsNotEmpty())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Utils.Base64Encode(uri.UserInfo));
            }

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));

            return await client.GetStringAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            if (raiseErrorEvents)
            {
                Logging.SaveLog(_tag, ex);
                Error?.Invoke(this, new ErrorEventArgs(ex));
                if (ex.InnerException != null)
                {
                    Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads string content via DownloaderHelper.
    /// </summary>
    private async Task<string?> DownloadStringViaDownloader(string url, IWebProxy? webProxy, string userAgent, int timeout, bool raiseErrorEvents = true)
    {
        try
        {
            if (userAgent.IsNullOrEmpty())
            {
                userAgent = Utils.GetVersion(false);
            }
            var result = await DownloaderHelper.Instance.DownloadStringAsync(webProxy, url, userAgent, timeout);
            return result;
        }
        catch (Exception ex)
        {
            if (raiseErrorEvents)
            {
                Logging.SaveLog(_tag, ex);
                Error?.Invoke(this, new ErrorEventArgs(ex));
                if (ex.InnerException != null)
                {
                    Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Creates local SOCKS proxy when proxy switch is enabled.
    /// </summary>
    private async Task<WebProxy?> GetWebProxy(bool blProxy)
    {
        if (!blProxy)
        {
            return null;
        }
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (await SocketCheck(Global.Loopback, port) == false)
        {
            return null;
        }

        return new WebProxy($"socks5://{Global.Loopback}:{port}");
    }

    /// <summary>
    /// Checks whether the specified TCP endpoint is reachable.
    /// </summary>
    private static async Task<bool> SocketCheck(string ip, int port)
    {
        try
        {
            IPEndPoint point = new(IPAddress.Parse(ip), port);
            using Socket? sock = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(point);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
