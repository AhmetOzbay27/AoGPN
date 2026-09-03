namespace ServiceLib.Handler;

public static class ConnectionHandler
{
    private static readonly string _tag = "ConnectionHandler";
    // Remember the last endpoint that returned a valid IP so subsequent checks
    // try it first instead of paying timeouts on a blocked configured URL.
    private static string? _lastWorkingIPUrl;

    /// <summary>
    /// Runs ping and IP checks and returns a formatted result string.
    /// </summary>
    public static async Task<string> RunAvailabilityCheck()
    {
        var (time, ipInfo) = await RunAvailabilityCheckData();
        var ip = ipInfo?.ToString() ?? Global.None;

        return string.Format(ResUI.TestMeOutput, time, ip);
    }

    /// <summary>
    /// Yapılandırılmış kullanılabilirlik ölçümü: HTTP ping (proxy üzerinden) + genel
    /// IP bilgisi. Biçimlendirilmiş mesajın yanı sıra ham değerleri döndürür — status
    /// bar metni ve dashboard (gecikme/IP kartı) aynı ölçümü ayrı ayrı tüketebilir.
    /// IP, ping başarısızsa null'dur (IPAPI çağrısı anlamsız — bağlantı ölçülmüyor).
    /// </summary>
    public static async Task<(int TimeMs, IpInfoResult? IpInfo)> RunAvailabilityCheckData()
    {
        var time = await GetRealPingTimeInfo();
        IpInfoResult? ipInfo = null;
        if (time > 0)
        {
            var webProxy = await GetWebProxy();
            ipInfo = await GetIPInfo(webProxy);
        }

        return (time, ipInfo);
    }

    /// <summary>
    /// Gets IP information using the default local proxy.
    /// </summary>
    private static async Task<string?> GetIPInfo()
    {
        var webProxy = await GetWebProxy();

        var ipInfo = await GetIPInfo(webProxy);
        return ipInfo?.ToString() ?? Global.None;
    }

    /// <summary>
    /// Measures real ping time using configured test URL.
    /// </summary>
    private static async Task<int> GetRealPingTimeInfo()
    {
        var responseTime = -1;
        try
        {
            var webProxy = await GetWebProxy();

            for (var i = 0; i < 2; i++)
            {
                responseTime = await GetRealPingTime(webProxy);
                if (responseTime > 0)
                {
                    break;
                }
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return -1;
        }
        return responseTime;
    }

    /// <summary>
    /// Creates local SOCKS proxy instance.
    /// </summary>
    private static async Task<WebProxy?> GetWebProxy()
    {
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        return new WebProxy($"socks5://{Global.Loopback}:{port}");
    }

    /// <summary>
    /// Measures response time by sending HTTP requests through proxy.
    /// </summary>
    public static async Task<int> GetRealPingTime(
        IWebProxy? webProxy,
        int downloadTimeout = 9,
        CancellationToken cancellationToken = default)
    {
        var url = AppManager.Instance.Config.SpeedTestItem.SpeedPingTestUrl;
        var responseTime = -1;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(downloadTimeout));
            using var client = new HttpClient(new SocketsHttpHandler()
            {
                Proxy = webProxy,
                UseProxy = webProxy != null,
                ConnectTimeout = TimeSpan.FromSeconds(3)
            });

            List<int> oneTime = [];
            for (var i = 0; i < 2; i++)
            {
                var timer = Stopwatch.StartNew();
                await client.GetAsync(url, cts.Token).ConfigureAwait(false);
                timer.Stop();
                oneTime.Add((int)timer.Elapsed.TotalMilliseconds);
                await Task.Delay(100, cts.Token);
            }
            responseTime = oneTime.Where(x => x > 0).OrderBy(x => x).FirstOrDefault();
        }
        catch
        {
        }
        return responseTime;
    }

    /// <summary>
    /// Gets IP and country information through specified proxy.
    /// </summary>
    public static async Task<IpInfoResult?> GetIPInfo(IWebProxy? webProxy)
    {
        // Try the configured endpoint first, then fall back across the built-in
        // list and a few well-known free endpoints so a blocked or stale single
        // URL can never hide the public IP from the dashboard.
        var candidates = new List<string>();
        if (_lastWorkingIPUrl.IsNotEmpty())
        {
            candidates.Add(_lastWorkingIPUrl!);
        }
        var configured = AppManager.Instance.Config.SpeedTestItem.IPAPIUrl;
        if (configured.IsNotEmpty() && !candidates.Contains(configured))
        {
            candidates.Add(configured);
        }
        foreach (var url in Global.IPAPIUrls)
        {
            if (url.IsNotEmpty() && !candidates.Contains(url))
            {
                candidates.Add(url);
            }
        }
        foreach (var url in new[]
                 {
                     "https://ipinfo.io/json",
                     "https://ip-api.com/json/",
                     "https://api.ipify.org?format=json"
                 })
        {
            if (!candidates.Contains(url))
            {
                candidates.Add(url);
            }
        }

        foreach (var url in candidates)
        {
            try
            {
                var downloadHandle = new DownloadService();
                var result = await downloadHandle.TryDownloadString(url, webProxy, "");
                if (result == null)
                {
                    continue;
                }

                var ipInfo = JsonUtils.Deserialize<IPAPIInfo>(result);
                if (ipInfo == null)
                {
                    continue;
                }

                var ip = ipInfo.ip ?? ipInfo.clientIp ?? ipInfo.ip_addr ?? ipInfo.query;
                var country = ipInfo.country_code ?? ipInfo.country ?? ipInfo.countryCode ?? ipInfo.location?.country_code ?? "unknown";
                if (ip.IsNotEmpty())
                {
                    _lastWorkingIPUrl = url;
                    return new IpInfoResult(country, ip);
                }
            }
            catch
            {
                // Try the next endpoint.
            }
        }

        return null;
    }
}
