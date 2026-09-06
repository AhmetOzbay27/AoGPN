namespace ServiceLib.Services;

/// <summary>
/// Bağlantı monitörünün veri motoru (P0 Nihai Faz): işletim sistemi bağlantı
/// tablosunu (WindowsNetworkTable), aktif yönlendirme kurallarını (30 sn önbellekli)
/// ve sing-box /connections trafiğini (ClashApiManager) toplar; satır / ülke /
/// trafik izdüşümlerini tek bir anlık görüntüde (<see cref="ConnectionMonitorSnapshot"/>)
/// üretir. WPF durumu tutmaz — anlık görüntüler olay/delege ile çağıran ViewModel'e
/// verilir ve koleksiyonlar orada (UI iş parçacığında) güncellenir.
/// </summary>
public sealed class DashboardConnectionEngine
{
    private readonly Config _config;

    public DashboardConnectionEngine()
    {
        _config = AppManager.Instance.Config;
    }
    public async Task<ConnectionMonitorSnapshot> RefreshAsync(MonitorRequestOptions options)
    {
        var rows = await Task.Run(WindowsNetworkTable.GetActiveConnections);
        var routing = await LoadRoutingAsync();

        var filter = options.Filter?.Trim() ?? "";
        var hideListeners = options.HideListeners;
        var items = new List<ConnectionMonitorItem>();
        foreach (var row in rows)
        {
            if (hideListeners && row.Protocol == "TCP" && row.State == "Listen")
            {
                continue;
            }

            if (!TryGetAppInfo(row.Pid, out var info))
            {
                continue;
            }

            var processName = info.name;
            if (_excludeProcesses.Contains(Path.GetFileNameWithoutExtension(processName)))
            {
                continue;
            }

            var item = new ConnectionMonitorItem
            {
                ProcessName = processName,
                DisplayName = info.display,
                ExePath = info.path,
                Pid = row.Pid,
                Protocol = row.Protocol,
                LocalAddress = row.LocalAddress,
                RemoteAddress = row.RemoteAddress,
                State = row.Protocol == "UDP" ? "" : StateText(row.State),
                RouteTag = ResolveRouteTag(routing, processName),
            };
            item.RouteText = RouteText(item.RouteTag);
            ApplyGeoInfo(item);

            if (filter.IsNotEmpty()
                && !item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !item.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !item.RemoteAddress.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !item.CountryText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !item.AsnText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(item);
        }

        items.Sort((a, b) =>
        {
            var byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.RemoteAddress, b.RemoteAddress, StringComparison.OrdinalIgnoreCase);
        });

        var traffic = await LoadTrafficAsync(options.TrafficFilter);
        var countries = BuildCountryItems(items, traffic.byCountry);
        return new ConnectionMonitorSnapshot(items, countries, traffic.byType, traffic.byApp, traffic.download, traffic.upload, traffic.status);
    }
    /// <summary>
    /// 2 saniyelik izleme döngüsü (orijinal ConnectionMonitorViewModel.RunLoopAsync
    /// semantiği): her tikte kapı (<paramref name="shouldRefresh"/>) açıksa veriyi
    /// tazeler ve anlık görüntüyü <paramref name="onSnapshot"/> ile yayınlar. Kapıyı
    /// ve opsiyonları çağıran (ViewModel) kendi UI durumundan besler; tüm hatalar
    /// orijinaldeki gibi sessizce yutulur.
    /// </summary>
    public async Task RunLoopAsync(Func<MonitorRequestOptions> optionsProvider, Func<bool> shouldRefresh, Action<ConnectionMonitorSnapshot> onSnapshot)
    {
        while (true)
        {
            try
            {
                if (shouldRefresh())
                {
                    var snapshot = await RefreshAsync(optionsProvider());
                    onSnapshot?.Invoke(snapshot);
                }
            }
            catch
            {
            }
            await Task.Delay(2000);
        }
    }
    private async Task<(List<TrafficMonitorItem> byType, List<TrafficMonitorItem> byApp, List<CountryTrafficItem> byCountry, long download, long upload, string status)> LoadTrafficAsync(string? trafficFilter)
    {
        var empty = (new List<TrafficMonitorItem>(), new List<TrafficMonitorItem>(), new List<CountryTrafficItem>(), 0L, 0L, ResUI.MonitorTrafficUnavailable);
        if (!AppManager.Instance.IsRunningCore(ECoreType.mihomo))
        {
            return empty;
        }

        try
        {
            var result = await ClashApiManager.Instance.GetClashConnectionsAsync();
            if (result?.connections is not { Count: > 0 } connections)
            {
                return empty;
            }

            var filter = trafficFilter?.Trim() ?? "";
            var records = connections
                .Where(x => x.metadata is not null)
                .Select(x =>
                {
                    var metadata = x.metadata!;
                    var processPath = metadata.processPath ?? "";
                    var process = metadata.process.IsNotEmpty()
                        ? metadata.process!
                        : processPath.IsNotEmpty() ? Path.GetFileName(processPath) : ResUI.MonitorOtherApp;
                    var appName = Path.GetFileNameWithoutExtension(process);
                    if (appName.IsNullOrEmpty())
                    {
                        appName = process;
                    }
                    return new TrafficRecord(
                        TrafficType(metadata.type, metadata.destinationPort),
                        appName,
                        processPath,
                        Saturate(x.download),
                        Saturate(x.upload),
                        DestinationOf(metadata),
                        IsUdpNetwork(metadata));
                })
                .Where(x => filter.IsNullOrEmpty()
                    || x.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || x.AppName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var download = records.Sum(x => x.Download);
            var upload = records.Sum(x => x.Upload);
            var byType = records
                .GroupBy(x => new { x.Type, x.AppName, x.ExePath })
                .Select(x => CreateTrafficItem(x.Key.Type, x.Key.AppName, x.Key.ExePath, x.ToList()))
                .OrderByDescending(x => x.Download + x.Upload)
                .ToList();
            var byApp = records
                .GroupBy(x => new { x.AppName, x.ExePath })
                .Select(x => CreateTrafficItem(ResUI.MonitorTrafficTotal, x.Key.AppName, x.Key.ExePath, x.ToList()))
                .OrderByDescending(x => x.Download + x.Upload)
                .ToList();

            byType.Insert(0, CreateTrafficItem(ResUI.MonitorTrafficAll, ResUI.MonitorAllApps, "", records));
            var byCountry = BuildCountryTraffic(connections);
            return (byType, byApp, byCountry, download, upload, ResUI.MonitorTrafficLive);
        }
        catch
        {
            return empty;
        }
    }
    private static List<CountryTrafficItem> BuildCountryTraffic(List<ConnectionItem>? connections)
    {
        var result = new List<CountryTrafficItem>();
        foreach (var item in connections ?? [])
        {
            var destination = item.metadata?.destinationIP;
            if (destination is not { Length: > 0 } || !IPAddress.TryParse(destination, out var ip))
            {
                continue;
            }

            var geo = GeoIpLookupService.Lookup(ip);
            result.Add(new CountryTrafficItem(
                geo.IsPrivate ? string.Empty : geo.CountryCode,
                geo.CountryName,
                geo.IsPrivate,
                Saturate(item.download),
                Saturate(item.upload)));
        }

        return result;
    }
    private static long SumTraffic(IEnumerable<long> values)
    {
        var sum = 0L;
        foreach (var value in values)
        {
            if (value <= 0)
            {
                continue;
            }

            sum = value > long.MaxValue - sum ? long.MaxValue : sum + value;
        }
        return sum;
    }
    private static TrafficMonitorItem CreateTrafficItem(string type, string appName, string exePath, IEnumerable<TrafficRecord> records)
    {
        var list = records.ToList();
        var download = list.Sum(x => (long)x.Download);
        var upload = list.Sum(x => (long)x.Upload);
        return new TrafficMonitorItem
        {
            Type = type,
            AppName = appName,
            AppCountText = list.Count > 1 ? $"+{list.Count}" : "",
            ExePath = exePath,
            ConnectionCount = list.Count,
            Download = download,
            Upload = upload,
            ActiveIps = AggregateDestinations(list),
        };
    }
    private static string AggregateDestinations(List<TrafficRecord> records)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new List<string>();
        foreach (var group in new[] { true, false })
        {
            foreach (var record in records)
            {
                if (record.IsUdp == group
                    && record.Destination.IsNotEmpty()
                    && seen.Add(record.Destination))
                {
                    destinations.Add(record.Destination);
                }
            }
        }
        return string.Join(", ", destinations);
    }
    private static string DestinationOf(MetadataItem metadata)
    {
        var ip = metadata.destinationIP;
        if (ip.IsNotEmpty() && IPAddress.TryParse(ip, out _))
        {
            return ip!;
        }
        var host = metadata.host;
        return host.IsNotEmpty() ? host! : "";
    }
    private static bool IsUdpNetwork(MetadataItem metadata)
    {
        return "udp".Equals(metadata.network, StringComparison.OrdinalIgnoreCase);
    }
    private static string TrafficType(string? type, string? port)
    {
        var normalized = type?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "https" or "tls" => "Hypertext Transfer Protocol over SSL/TLS",
            "http" => "Hypertext Transfer Protocol (HTTP)",
            "dns" => "Domain Name System (DNS)",
            "mdns" => "Multicast DNS (mDNS)",
            "pop3" => "Post Office Protocol 3 (POP3)",
            "smtp" => "Simple Mail Transfer Protocol (SMTP)",
            _ when port == "443" => "Hypertext Transfer Protocol over SSL/TLS",
            _ when port == "80" => "Hypertext Transfer Protocol (HTTP)",
            _ when port == "53" => "Domain Name System (DNS)",
            _ => type.IsNotEmpty() ? type! : ResUI.MonitorOtherTraffic,
        };
    }
    private static long Saturate(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
    private sealed record TrafficRecord(string Type, string AppName, string ExePath, long Download, long Upload, string Destination, bool IsUdp);
    private sealed record CountryTrafficItem(string CountryCode, string CountryName, bool IsPrivate, long Download, long Upload);
    private static List<CountryAggregateItem> BuildCountryItems(
        List<ConnectionMonitorItem> items,
        List<CountryTrafficItem> traffic)
    {
        var connectionGroups = items
            .GroupBy(x => (Code: x.IsPrivate ? string.Empty : x.CountryCode ?? string.Empty, x.IsPrivate))
            .ToDictionary(g => g.Key, g => g.ToList());
        var trafficGroups = traffic
            .GroupBy(x => (Code: x.IsPrivate ? string.Empty : x.CountryCode ?? string.Empty, x.IsPrivate))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<CountryAggregateItem>();
        foreach (var key in connectionGroups.Keys.Concat(trafficGroups.Keys).Distinct())
        {
            var conns = connectionGroups.GetValueOrDefault(key) ?? [];
            var trafs = trafficGroups.GetValueOrDefault(key) ?? [];
            var first = conns.FirstOrDefault();
            var code = key.Code;
            var isPrivate = key.IsPrivate;
            var countryName = first?.CountryName.IsNotEmpty() == true
                ? first.CountryName
                : trafs.FirstOrDefault()?.CountryName ?? string.Empty;

            var item = new CountryAggregateItem
            {
                CountryCode = code,
                CountryName = countryName,
                IsPrivate = isPrivate,
                ConnectionCount = conns.Count,
                AppCount = conns.Select(x => x.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                AsnText = string.Join(", ", conns.Select(x => x.AsnText).Where(x => x.IsNotEmpty()).Distinct(StringComparer.Ordinal).Take(2)),
                Download = SumTraffic(trafs.Select(x => x.Download)),
                Upload = SumTraffic(trafs.Select(x => x.Upload)),
            };
            item.DisplayName = isPrivate
                ? ResUI.MonitorCountryLocal
                : code.IsNotEmpty()
                    ? countryName.IsNotEmpty() ? $"{item.Flag} {code} · {countryName}" : $"{item.Flag} {code}"
                    : ResUI.MonitorCountryUnknown;
            result.Add(item);
        }

        var useTraffic = result.Any(x => x.TrafficBytes > 0);
        return result
            .OrderByDescending(x => useTraffic ? x.TrafficBytes : x.ConnectionCount)
            .ToList();
    }
    private static void ApplyGeoInfo(ConnectionMonitorItem item)
    {
        var host = ExtractHost(item.RemoteAddress);
        if (host is null || !IPAddress.TryParse(host, out var ip))
        {
            return;
        }

        var geo = GeoIpLookupService.Lookup(ip);
        item.CountryCode = geo.CountryCode;
        item.CountryName = geo.CountryName;
        item.AsnNumber = geo.AsnNumber;
        item.AsnOrg = geo.AsnOrg;
        item.IsPrivate = geo.IsPrivate;
        item.CountryText = geo.IsPrivate
            ? ResUI.MonitorCountryLocal
            : geo.CountryCode.IsNotEmpty() ? $"{item.Flag} {geo.CountryCode}" : string.Empty;
        item.AsnText = geo.AsnNumber > 0
            ? geo.AsnOrg.IsNotEmpty() ? $"AS{geo.AsnNumber} {geo.AsnOrg}" : $"AS{geo.AsnNumber}"
            : string.Empty;
    }
    private static string? ExtractHost(string? address)
    {
        if (address.IsNullOrEmpty() || address == "*")
        {
            return null;
        }

        if (address![0] == '[')
        {
            var end = address.IndexOf(']');
            return end > 1 ? address[1..end] : null;
        }

        var idx = address.LastIndexOf(':');
        return idx > 0 ? address[..idx] : address;
    }
    private async Task<(Dictionary<string, string> process, string catchAll)> LoadRoutingAsync()
    {
        var now = DateTime.UtcNow;
        if (_routingCache is { } cached && _routingCacheExpires > now)
        {
            return cached;
        }

        var result = await LoadRoutingCoreAsync();
        _routingCache = result;
        _routingCacheExpires = now.AddSeconds(RoutingCacheSeconds);
        return result;
    }
    private const int RoutingCacheSeconds = 30;
    private (Dictionary<string, string> Process, string CatchAll)? _routingCache;
    private DateTime _routingCacheExpires = DateTime.MinValue;
    private async Task<(Dictionary<string, string> process, string catchAll)> LoadRoutingCoreAsync()
    {
        var process = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var catchAll = Global.ProxyTag;

        try
        {
            var routingItem = await ConfigHandler.GetDefaultRouting(_config);
            var rules = routingItem?.RuleSet.IsNullOrEmpty() != false
                ? new List<RulesItem>()
                : JsonUtils.Deserialize<List<RulesItem>>(routingItem.RuleSet) ?? new List<RulesItem>();

            foreach (var r in rules)
            {
                if (r.Process is { Count: > 0 })
                {
                    foreach (var p in r.Process)
                    {
                        if (p.IsNotEmpty())
                        {
                            process[p] = r.OutboundTag ?? Global.ProxyTag;
                        }
                    }
                }

                if (IsCatchAllRule(r))
                {
                    catchAll = r.OutboundTag == Global.DirectTag ? Global.DirectTag : Global.ProxyTag;
                }

            }
        }
        catch
        {
        }

        return (process, catchAll);
    }
    private static string ResolveRouteTag((Dictionary<string, string> process, string catchAll) routing, string processName)
    {
        return routing.process.TryGetValue(processName, out var tag) ? tag : routing.catchAll;
    }
    private static bool IsCatchAllRule(RulesItem r)
    {
        return r.Port == "0-65535"
            && (r.Process is null || r.Process.Count == 0)
            && (r.Domain is null || r.Domain.Count == 0)
            && (r.Ip is null || r.Ip.Count == 0);
    }
    private static string RouteText(string tag)
    {
        return tag switch
        {
            Global.ProxyTag => ResUI.SplitTunnelActionProxy,
            Global.DirectTag => ResUI.SplitTunnelActionDirect,
            Global.BlockTag => ResUI.SplitTunnelActionBlock,
            Global.WarpTag => ResUI.ManualActionWarp,
            _ => ResUI.SplitTunnelActionDefault,
        };
    }
    private static string StateText(string state)
    {
        return state switch
        {
            "Established" => ResUI.MonitorStateEstablished,
            "Listen" => ResUI.MonitorStateListening,
            "TimeWait" => ResUI.MonitorStateTimeWait,
            "CloseWait" => ResUI.MonitorStateCloseWait,
            "FinWait1" or "FinWait2" => ResUI.MonitorStateFinWait,
            "SynSent" or "SynReceived" => ResUI.MonitorStateConnecting,
            "Closing" => ResUI.MonitorStateClosing,
            "LastAck" => ResUI.MonitorStateLastAck,
            "Closed" => ResUI.MonitorStateClosed,
            _ => state,
        };
    }
    public void InvalidateRoutingCache()
    {
        _routingCache = null;
    }
    private static readonly HashSet<string> _excludeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "AoGPN", "AoGPN", "xray", "mihomo", "AmazTool", "EnableLoopback"
    };
    private static bool TryGetAppInfo(int pid, out (string name, string path, string display) info)
    {
        info = default;
        var now = DateTime.UtcNow;

        lock (_appInfoCacheLock)
        {
            PruneAppInfoCache(now);
            if (_appInfoCache.TryGetValue(pid, out var cached) && cached.ExpiresAt > now)
            {
                info = (cached.Name, cached.Path, cached.Display);
                return true;
            }
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            if (name.IsNullOrEmpty())
            {
                return false;
            }

            var path = "";
            try
            {
                path = p.MainModule?.FileName ?? "";
            }
            catch
            {
            }

            var display = name;
            if (path.IsNotEmpty())
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    if (vi.FileDescription.IsNotEmpty())
                    {
                        display = vi.FileDescription;
                    }
                    else if (vi.ProductName.IsNotEmpty())
                    {
                        display = vi.ProductName;
                    }
                }
                catch
                {
                }
            }

            var fullName = name + ".exe";
            lock (_appInfoCacheLock)
            {
                _appInfoCache[pid] = new AppInfoCacheEntry
                {
                    Name = fullName,
                    Path = path,
                    Display = display,
                    ExpiresAt = now.AddSeconds(AppInfoCacheSeconds),
                };
            }

            info = (fullName, path, display);
            return true;
        }
        catch
        {
            // The process exited between the net-table snapshot and the lookup.
            lock (_appInfoCacheLock)
            {
                _appInfoCache.Remove(pid);
            }
            return false;
        }
    }
    private const int AppInfoCacheSeconds = 30;
    private const int AppInfoCacheMaxEntries = 2048;
    private static readonly Dictionary<int, AppInfoCacheEntry> _appInfoCache = new();
    private static readonly object _appInfoCacheLock = new();
    private sealed class AppInfoCacheEntry
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string Display { get; init; }
        public required DateTime ExpiresAt { get; init; }
    }
    private static void PruneAppInfoCache(DateTime now)
    {
        if (_appInfoCache.Count <= AppInfoCacheMaxEntries)
        {
            return;
        }
        var expired = _appInfoCache.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
        {
            _appInfoCache.Remove(key);
        }
    }
}

/// <summary>Bir izleme tikinin giriş seçenekleri (UI durumundan anlık okunur).</summary>
public sealed record MonitorRequestOptions(string Filter, string TrafficFilter, bool HideListeners);

/// <summary>Bir izleme tikinin tam çıktısı — satırlar, ülkeler, trafik kovaları ve sayaçlar.</summary>
public sealed record ConnectionMonitorSnapshot(
    List<ConnectionMonitorItem> Items,
    List<CountryAggregateItem> Countries,
    List<TrafficMonitorItem> ByType,
    List<TrafficMonitorItem> ByApp,
    long Download,
    long Upload,
    string Status);
