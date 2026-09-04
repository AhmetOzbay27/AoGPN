using System.Net.Http.Headers;
using System.Security.Authentication;
using Downloader;

namespace ServiceLib.Helper;

public class DownloaderHelper
{
    private static readonly Lazy<DownloaderHelper> _instance = new(() => new());
    public static DownloaderHelper Instance => _instance.Value;

    public async Task<string?> DownloadStringAsync(IWebProxy? webProxy, string url, string? userAgent, int timeout)
    {
        if (url.IsNullOrEmpty())
        {
            return null;
        }

        var connectTimeout = Math.Clamp(timeout / 5, 2, 5);

        Uri uri = new(url);
        //Authorization Header
        var headers = new WebHeaderCollection();
        if (uri.UserInfo.IsNotEmpty())
        {
            headers.Add(HttpRequestHeader.Authorization, "Basic " + Utils.Base64Encode(uri.UserInfo));
        }

        var requestConfiguration = new RequestConfiguration()
        {
            Headers = headers,
            UserAgent = userAgent,
            ConnectTimeout = connectTimeout * 1000,
            Proxy = webProxy
        };
        var downloadOpt = new DownloadConfiguration()
        {
            BlockTimeout = timeout * 1000,
            MaxTryAgainOnFailure = 2,
            RequestConfiguration = requestConfiguration,
            CustomHttpMessageHandlerFactory = () => GetSocketsHttpHandler(requestConfiguration),
        };

        await using var downloader = new Downloader.DownloadService(downloadOpt);
        downloader.DownloadFileCompleted += (sender, value) =>
        {
            if (value.Error != null)
            {
                throw value.Error;
            }
        };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        await using var stream = await downloader.DownloadFileTaskAsync(address: url, cts.Token);
        using StreamReader reader = new(stream);

        downloadOpt = null;

        return await reader.ReadToEndAsync(cts.Token);
    }

    public async Task DownloadDataAsync4Speed(IWebProxy webProxy, string url, IProgress<string> progress, int timeout)
    {
        if (url.IsNullOrEmpty())
        {
            throw new ArgumentNullException(nameof(url));
        }

        var connectTimeout = Math.Clamp(timeout / 5, 2, 5);
        var requestConfiguration = new RequestConfiguration()
        {
            ConnectTimeout = connectTimeout * 1000,
            Proxy = webProxy
        };
        var downloadOpt = new DownloadConfiguration()
        {
            BlockTimeout = timeout * 1000,
            MaxTryAgainOnFailure = 2,
            RequestConfiguration = requestConfiguration,
            CustomHttpMessageHandlerFactory = () => GetSocketsHttpHandler(requestConfiguration),
        };

        var lastUpdateTime = DateTime.Now;
        var hasValue = false;
        double maxSpeed = 0;
        await using var downloader = new Downloader.DownloadService(downloadOpt);

        downloader.DownloadProgressChanged += (sender, value) =>
        {
            if (progress != null && value.BytesPerSecondSpeed > 0)
            {
                hasValue = true;
                if (value.BytesPerSecondSpeed > maxSpeed)
                {
                    maxSpeed = value.BytesPerSecondSpeed;
                }

                var ts = DateTime.Now - lastUpdateTime;
                if (ts.TotalMilliseconds >= 1000)
                {
                    lastUpdateTime = DateTime.Now;
                    var speed = (maxSpeed / 1000 / 1000).ToString("#0.0");
                    progress.Report(speed);
                }
            }
        };
        downloader.DownloadFileCompleted += (sender, value) =>
        {
            if (progress != null)
            {
                if (hasValue && maxSpeed > 0)
                {
                    var finalSpeed = (maxSpeed / 1000 / 1000).ToString("#0.0");
                    progress.Report(finalSpeed);
                }
                else if (value.Error != null)
                {
                    progress.Report(value.Error?.Message);
                }
                else
                {
                    progress.Report("0");
                }
            }
        };
        //progress.Report("......");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        await using var stream = await downloader.DownloadFileTaskAsync(address: url, cts.Token);

        downloadOpt = null;
    }

    /// <summary>
    /// Max attempts through the Downloader library before the single-stream
    /// fallback takes over. The first attempt downloads fresh; every later
    /// attempt RESUMES the same package (the library keeps the received bytes
    /// and chunk positions in "&lt;fileName&gt;.download"), so a transfer that dies
    /// mid-way continues from the last byte instead of restarting from zero.
    /// </summary>
    private const int MaxLibraryAttempts = 4;

    /// <summary>Attempts of the plain single-stream fallback (Range-resumable).</summary>
    private const int MaxSingleStreamAttempts = 2;

    public async Task DownloadFileAsync(IWebProxy? webProxy, string url, string fileName, IProgress<double> progress, int timeout)
    {
        if (url.IsNullOrEmpty())
        {
            throw new ArgumentNullException(nameof(url));
        }
        if (fileName.IsNullOrEmpty())
        {
            throw new ArgumentNullException(nameof(fileName));
        }
        if (File.Exists(fileName))
        {
            File.Delete(fileName);
        }

        var connectTimeout = Math.Clamp(timeout / 5, 2, 5);
        var requestConfiguration = new RequestConfiguration()
        {
            ConnectTimeout = connectTimeout * 1000,
            Proxy = webProxy
        };
        var downloadOpt = new DownloadConfiguration()
        {
            BlockTimeout = timeout * 1000,
            MaxTryAgainOnFailure = 2,
            RequestConfiguration = requestConfiguration,
            CustomHttpMessageHandlerFactory = () => GetSocketsHttpHandler(requestConfiguration),
            // Resume across process restarts too: the library embeds resume
            // metadata inside "<fileName>.download" and continues from there when
            // a later run uses the same file name (callers pass stable names).
            EnableAutoResumeDownload = true,
        };

        var progressPercentage = 0;
        var hasValue = false;
        Exception? lastError = null;
        await using var downloader = new Downloader.DownloadService(downloadOpt);
        downloader.DownloadStarted += (sender, value) => progress?.Report(0);
        downloader.DownloadProgressChanged += (sender, value) =>
        {
            hasValue = true;
            var percent = (int)value.ProgressPercentage;//   Convert.ToInt32((totalRead * 1d) / (total * 1d) * 100);
            if (progressPercentage != percent && percent % 10 == 0)
            {
                progressPercentage = percent;
                progress.Report(percent);
            }
        };
        // The library reports chunk/transfer failures through this event (the
        // awaited task itself completes). Record the error instead of throwing
        // inside the event dispatch — the retry loop below decides what to do.
        downloader.DownloadFileCompleted += (sender, value) =>
        {
            if (value.Error != null)
            {
                lastError = value.Error;
            }
            else if (hasValue)
            {
                progress.Report(101);
            }
        };

        var backoff = TimeSpan.FromSeconds(2);
        for (var attempt = 1; attempt <= MaxLibraryAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(timeout));
                if (attempt == 1)
                {
                    await downloader.DownloadFileTaskAsync(url, fileName, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    // Resume: the package still holds the received bytes and the
                    // chunk positions, so only the missing ranges are re-requested.
                    await downloader.DownloadFileTaskAsync(downloader.Package, url, cts.Token).ConfigureAwait(false);
                }

                if (File.Exists(fileName))
                {
                    progress.Report(101);
                    downloadOpt = null;
                    return;
                }

                // The transfer "completed" without producing the final file (a
                // partial may still sit in "<fileName>.download") — treat it as a
                // failure so the next attempt can finalize/resume it.
                lastError = new IOException(
                    $"Download ended without producing '{fileName}' (status: {downloader.Package.Status}).");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt < MaxLibraryAttempts)
            {
                Logging.SaveLog($"[DownloaderHelper] attempt {attempt}/{MaxLibraryAttempts} for {url} failed: {lastError?.Message}; retrying in {backoff.TotalSeconds:0}s");
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
            }
        }

        // Last resort: a plain single-stream HTTP download with Range resume.
        // The library pipeline failed repeatedly; a single sequential connection
        // (curl-style) often still works where multi-chunk transfers keep dying.
        for (var attempt = 1; attempt <= MaxSingleStreamAttempts; attempt++)
        {
            try
            {
                await DownloadSingleStreamResumableAsync(webProxy, url, fileName, progress, timeout).ConfigureAwait(false);
                downloadOpt = null;
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Logging.SaveLog($"[DownloaderHelper] single-stream fallback attempt {attempt}/{MaxSingleStreamAttempts} for {url} failed: {ex.Message}");
            }

            if (attempt < MaxSingleStreamAttempts)
            {
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
            }
        }

        downloadOpt = null;
        throw lastError ?? new IOException($"Failed to download {url}");
    }

    /// <summary>
    /// Plain single-stream download with HTTP Range resume: bytes already on disk
    /// (from a previous partial attempt) are not re-fetched — the request starts
    /// at the existing file length and appends. A server that ignores the Range
    /// header (answers 200 with the whole body) is handled by truncating and
    /// rewriting from zero. Uses the same pinned-TLS handler as the library path.
    /// </summary>
    internal static async Task DownloadSingleStreamResumableAsync(
        IWebProxy? webProxy,
        string url,
        string fileName,
        IProgress<double> progress,
        int timeout)
    {
        var connectTimeout = Math.Clamp(timeout / 5, 2, 5);
        var requestConfiguration = new RequestConfiguration()
        {
            ConnectTimeout = connectTimeout * 1000,
            Proxy = webProxy
        };
        using var handler = GetSocketsHttpHandler(requestConfiguration);
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var resumeFrom = File.Exists(fileName) ? new FileInfo(fileName).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        }

        // Generous overall deadline: the caller's per-attempt timeout is meant for
        // the library path; a single-stream transfer of a big core needs room.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(timeout * 3, 120)));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            progress.Report(101); // the whole file is already on disk
            return;
        }
        response.EnsureSuccessStatusCode();

        // 206 = the server honoured our Range → append to the partial.
        // 200  = the server ignored the Range (or we had nothing to resume) →
        //        it is sending the entire body, so rewrite from an empty file.
        var append = response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            resumeFrom = 0;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using var destination = new FileStream(
            fileName, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        // On a 206 the Content-Length is the REMAINING bytes; the overall size
        // for the progress denominator is what was already on disk + remaining.
        var remaining = response.Content.Headers.ContentLength ?? 0;
        var total = resumeFrom + remaining;
        var buffer = new byte[81920];
        var received = resumeFrom;
        var progressStep = -1;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            received += read;
            if (total > 0)
            {
                var percent = (int)(received * 100d / total);
                var step = percent / 10;
                if (step > progressStep)
                {
                    progressStep = step;
                    progress.Report(percent);
                }
            }
        }

        if (total > 0 && received < total)
        {
            throw new IOException($"Incomplete transfer: got {received} of {total} bytes for {url}.");
        }
        progress.Report(101);
    }

    // https://github.com/bezzad/Downloader/blob/a75a6e431acd6cbba6293f7afdcf676544a09174/src/Downloader/SocketClient.cs#L45
    // There is a risk of MITM attacks
    // https://github.com/bezzad/Downloader/blob/a75a6e431acd6cbba6293f7afdcf676544a09174/src/Downloader/Extensions/ExceptionHelper.cs#L111
    private static SocketsHttpHandler GetSocketsHttpHandler(RequestConfiguration config)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = config.AllowAutoRedirect,
            MaxAutomaticRedirections = config.MaximumAutomaticRedirections,
            AutomaticDecompression = config.AutomaticDecompression,
            PreAuthenticate = config.PreAuthenticate,
            UseCookies = config.CookieContainer != null,
            UseProxy = config.Proxy != null,
            MaxConnectionsPerServer = 1000,
            PooledConnectionIdleTimeout = config.KeepAliveTimeout,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromMilliseconds(config.ConnectTimeout)
        };

        // Set up the SslClientAuthenticationOptions for custom certificate validation
        if (config.ClientCertificates?.Count > 0)
        {
            handler.SslOptions.ClientCertificates = config.ClientCertificates;
        }

        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;
        //handler.SslOptions.RemoteCertificateValidationCallback = ExceptionHelper.CertificateValidationCallBack;

        var certificateChainPolicy = CertPemManager.Instance.BuildCertificateChainPolicy();
        if (certificateChainPolicy != null)
        {
            handler.SslOptions.CertificateChainPolicy = certificateChainPolicy;
            handler.SslOptions.RemoteCertificateValidationCallback = null;
        }

        // Configure keep-alive
        if (config.KeepAlive)
        {
            handler.KeepAlivePingTimeout = config.KeepAliveTimeout;
            handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests;
        }

        // Configure credentials
        if (config.Credentials != null)
        {
            handler.Credentials = config.Credentials;
            handler.PreAuthenticate = config.PreAuthenticate;
        }

        // Configure cookies
        if (handler.UseCookies && config.CookieContainer != null)
        {
            handler.CookieContainer = config.CookieContainer;
        }

        // Configure proxy
        if (handler.UseProxy && config.Proxy != null)
        {
            handler.Proxy = config.Proxy;
        }

        // Add expect header
        if (!string.IsNullOrWhiteSpace(config.Expect))
        {
            handler.Expect100ContinueTimeout = TimeSpan.FromSeconds(1);
        }

        return handler;
    }
}
