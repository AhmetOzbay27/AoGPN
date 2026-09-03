namespace ServiceLib.Handler.SysProxy;

public static class SysProxyHandler
{
    private static readonly string _tag = "SysProxyHandler";
    private static readonly SemaphoreSlim _updateGate = new(1, 1);
    private static ESysProxyType? _lastAppliedType;
    private const int MaxApplyAttempts = 4;
    private static readonly TimeSpan[] ApplyRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(700),
    ];

    public static Task<bool> UpdateSysProxy(Config config, bool forceDisable)
        => UpdateSysProxy(config, forceDisable, null);

    public static async Task<bool> UpdateSysProxy(Config config, bool forceDisable, ESysProxyType? requestedType)
    {
        var type = SystemProxyPolicy.ResolveEffectiveType(config, requestedType, forceDisable);
        // Bounded gate wait: a stuck proxy apply (e.g. a hung PacManager or an
        // OS call) previously held _updateGate forever, freezing every later
        // proxy update — including the exit path's forced clear. Time out and
        // skip instead of blocking shutdown.
        if (!await _updateGate.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Logging.SaveLog($"{_tag} proxy update gate busy for 10s — proxy update skipped.");
            return false;
        }
        try
        {
            DiagLog.Write($"PROXY UpdateSysProxy: forceDisable={forceDisable} requested={requestedType} resolved={type}");
            if (_lastAppliedType == type && type != ESysProxyType.Pac)
                return true;

            for (var attempt = 0; attempt < MaxApplyAttempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(ApplyRetryDelays[attempt]);

                var port = 0;
                if (type is ESysProxyType.ForcedChange or ESysProxyType.Pac)
                {
                    port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
                    if (port <= 0)
                    {
                        DiagLog.Write($"PROXY listener not ready; retry {attempt + 1}/{MaxApplyAttempts}");
                        continue;
                    }
                }

                try
                {
                    await ApplyProxySetting(config, type, port);
                    if (type != ESysProxyType.Pac && Utils.IsWindows())
                        PacManager.Instance.Stop();
                    _lastAppliedType = type;
                    return true;
                }
                catch (Exception ex)
                {
                    Logging.SaveLog($"{_tag} proxy apply attempt {attempt + 1} failed", ex);
                    if (attempt + 1 == MaxApplyAttempts)
                        return false;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private static async Task ApplyProxySetting(Config config, ESysProxyType type, int port)
    {
        switch (type)
        {
            case ESysProxyType.ForcedChange when Utils.IsWindows():
                var (strProxy, strExceptions) = GetWindowsProxyString(config, port);
                ProxySettingWindows.SetProxy(strProxy, strExceptions, 2);
                break;
            case ESysProxyType.ForcedChange when Utils.IsLinux():
                await ProxySettingLinux.SetProxy(Global.Loopback, port, SanitizeExceptions(config));
                break;
            case ESysProxyType.ForcedChange when Utils.IsMacOS():
                await ProxySettingOSX.SetProxy(Global.Loopback, port, SanitizeExceptions(config));
                break;
            case ESysProxyType.ForcedClear when Utils.IsWindows():
                ProxySettingWindows.UnsetProxy();
                break;
            case ESysProxyType.ForcedClear when Utils.IsLinux():
                await ProxySettingLinux.UnsetProxy();
                break;
            case ESysProxyType.ForcedClear when Utils.IsMacOS():
                await ProxySettingOSX.UnsetProxy();
                break;
            case ESysProxyType.Pac when Utils.IsWindows():
                await SetWindowsProxyPac(port);
                break;
        }
    }

    private static string SanitizeExceptions(Config config)
    {
        var exceptions = config.SystemProxyItem.SystemProxyExceptions;
        if (exceptions.IsNullOrEmpty()) return string.Empty;
        return string.Join(',', exceptions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Replace(" ", string.Empty)).Where(item => item.Length > 0));
    }

    private static (string strProxy, string strExceptions) GetWindowsProxyString(Config config, int port)
    {
        var strExceptions = config.SystemProxyItem.SystemProxyExceptions.Replace(" ", "");
        if (config.SystemProxyItem.NotProxyLocalAddress) strExceptions = $"<local>;{strExceptions}";
        var strProxy = config.SystemProxyItem.SystemProxyAdvancedProtocol.IsNullOrEmpty()
            ? $"{Global.Loopback}:{port}"
            : config.SystemProxyItem.SystemProxyAdvancedProtocol.Replace("{ip}", Global.Loopback)
                .Replace("{http_port}", port.ToString()).Replace("{socks_port}", port.ToString());
        return (strProxy, strExceptions);
    }

    [SupportedOSPlatform("windows")]
    private static async Task SetWindowsProxyPac(int port)
    {
        var portPac = AppManager.Instance.GetLocalPort(EInboundProtocol.pac);
        await PacManager.Instance.StartAsync(port, portPac);
        ProxySettingWindows.SetProxy($"{Global.HttpProtocol}{Global.Loopback}:{portPac}/pac?t={DateTime.Now.Ticks}", "", 4);
    }
}
