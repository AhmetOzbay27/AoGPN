namespace ServiceLib.Services;

/// <summary>
/// Split-tunnel sayfasının canlı telemetri izdüşümü (P0 Faz 3): bağlantı monitörünün
/// anlık görüntülerini (OS bağlantı tablosu + sing-box /connections trafiği) satır
/// başına "çalışıyor / canlı rota / trafik / uç nokta gözlemi" durumuna işler.
///
/// Saf analiz yapan bir arka plan servisidir: girişleri parametre olarak alır
/// (uygulama listesi + canlı bağlantı/trafik anlık görüntüleri), satır modellerini
/// (SplitTunnelAppItem) günceller ve GPN "gerçek ping" uç nokta mağazasına
/// (GpnAppEndpointStore) gözlem yazar. WPF durumu tutmaz; çağrılar — ViewModel'deki
/// mevcut akışla aynı şekilde — UI (reaktif ana) iş parçacığında yapılır.
/// </summary>
public sealed class GpnTelemetryMonitorService
{
    private readonly GpnAppEndpointStore _endpointStore = GpnAppEndpointStore.Instance;

    public GpnTelemetryMonitorService()
    {
        // Periyodik flush döngüsünü başlat (idempotent) — gözlemler diske düzenli
        // yazılır ve eski uç noktalar budanır.
        _endpointStore.Start();
    }

    public bool UpdateLiveStatus(
        IEnumerable<SplitTunnelAppItem> apps,
        IEnumerable<ConnectionMonitorItem> connections,
        HashSet<string>? running,
        bool invertRouting)
    {
        var byProcess = new Dictionary<string, List<ConnectionMonitorItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in connections)
        {
            if (c.ProcessName.IsNullOrEmpty())
            {
                continue;
            }
            if (!byProcess.TryGetValue(c.ProcessName, out var list))
            {
                byProcess[c.ProcessName] = list = new List<ConnectionMonitorItem>();
            }
            list.Add(c);
        }

        var tunOnForList = apps.Any(a => a.Action is "vpn" or "vpn+proxy" or "warp");
        foreach (var app in apps)
        {
            if (app.EntryType != "app")
            {
                app.IsRunning = false;
                app.RunStatusText = "—";
                app.LiveRouteTag = "";
                app.LiveRouteText = "—";
                app.LiveConnectionCount = 0;
                app.NeedsTun = false;
                app.ExeMissing = false;
                continue;
            }

            // Running status and missing-exe detection need the process scan; when the
            // scan is gated off (hidden window / non-manual mode) keep the last values.
            if (running is not null)
            {
                var processName = Path.GetFileNameWithoutExtension(app.Value);
                var isRunning = processName.IsNotEmpty() && running.Contains(processName);
                app.IsRunning = isRunning;
                app.RunStatusText = isRunning ? ResUI.ManualRunning : ResUI.ManualNotRunning;
                app.ExeMissing = IsExeMissing(app);
            }

            var live = byProcess.TryGetValue(app.Value, out var list) ? list : null;
            if (live is { Count: > 0 })
            {
                var tag = live
                    .GroupBy(x => x.RouteTag)
                    .OrderByDescending(g => g.Count())
                    .First()
                    .Key;
                app.LiveRouteTag = tag.IsNullOrEmpty() ? Global.DirectTag : tag;
                app.LiveRouteText = live.Count > 1
                    ? $"{RouteText(app.LiveRouteTag)} · {live.Count}"
                    : RouteText(app.LiveRouteTag);
                app.LiveConnectionCount = live.Count;
            }
            else
            {
                app.LiveRouteTag = "";
                app.LiveRouteText = "—";
                app.LiveConnectionCount = 0;
            }

            // When the process scan is gated off (hidden window / Global VPN / Off
            // mode) the live connection list is the source of truth: a row with active
            // connections is running — never show "Not running" next to live traffic.
            if (running is null && live is { Count: > 0 })
            {
                app.IsRunning = true;
                app.RunStatusText = ResUI.ManualRunning;
            }

            // GPN kullanımında eklenen uygulamanın GERÇEK sunucu uç noktalarını
            // kaydet: uygulama (efektif olarak tünellenen kümede) çalışıp genel bir
            // adrese bağlandığında uzak uç nokta gpn_app_endpoints'e gözlem olarak
            // düşer. Kayıt bellekte yapılır; periyodik flush diske yazar. Aynı uç
            // nokta seti program açılışında (doğrudan yol = önce) ve GPN bağlantısı
            // kurulunca (tünel yolu = sonra) ölçülerek öncesi/sonrası üretilir.
            if (IsTunneledApp(app, invertRouting) && live is { Count: > 0 })
            {
                RecordAppEndpoints(app.Value, live);
            }

            // Proxy-only apps whose live connections go to public addresses are bypassing
            // the system proxy; they need the TUN (VPN) option to be captured.
            app.NeedsTun = app.Action == "proxy"
                && !tunOnForList
                && live is { Count: > 0 }
                && live.Any(c => RemoteIsPublicDirect(c.RemoteAddress));
        }
        return apps.Any(a => a.NeedsTun);
    }

    private void RecordAppEndpoints(string processName, List<ConnectionMonitorItem> live)
    {
        foreach (var connection in live)
        {
            if (connection.IsPrivate
                || connection.RemoteAddress.IsNullOrEmpty()
                || connection.RemoteAddress is "*" or "*:0" or "0.0.0.0")
            {
                continue;
            }
            _endpointStore.Observe(processName, connection.RemoteAddress, connection.Protocol);
        }
    }

    private bool IsTunneledApp(SplitTunnelAppItem app, bool invertRouting)
    {
        if (app.EntryType != "app")
        {
            return false;
        }
        var tunneledAction = invertRouting ? "direct" : "vpn";
        return string.Equals(app.Action, tunneledAction, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExeMissing(SplitTunnelAppItem app)
    {
        return app.EntryType == "app" && app.ExePath.IsNotEmpty() && !File.Exists(app.ExePath);
    }

    private static bool RemoteIsPublicDirect(string? remoteAddress)
    {
        var host = ExtractHost(remoteAddress);
        if (host is null)
        {
            return false;
        }
        if (IPAddress.TryParse(host, out _))
        {
            return !Utils.IsPrivateNetwork(host);
        }
        return true; // hostname remote → a direct connection, not the local proxy
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

    public static string MapActionToOutbound(string action) => ManualRoutingRules.MapActionToOutbound(action);

    public static string RouteText(string tag)
    {
        return tag switch
        {
            Global.ProxyTag => ResUI.ManualActionProxy,
            Global.DirectTag => ResUI.ManualActionDirect,
            Global.BlockTag => ResUI.ManualActionBlock,
            Global.WarpTag => ResUI.ManualActionWarp,
            _ => ResUI.ManualActionProxy,
        };
    }

    public void ApplyTrafficToApps(IEnumerable<SplitTunnelAppItem> apps, IEnumerable<TrafficMonitorItem> trafficItems)
    {
        var byName = new Dictionary<string, (long Download, long Upload, string Ips)>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, (long Download, long Upload, string Ips)>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in trafficItems)
        {
            if (t.AppName.IsNotEmpty())
            {
                AddTraffic(byName, t.AppName + ".exe", t.Download, t.Upload, t.ActiveIps);
            }
            if (t.ExePath.IsNotEmpty())
            {
                AddTraffic(byPath, t.ExePath, t.Download, t.Upload, t.ActiveIps);
            }
        }

        foreach (var app in apps)
        {
            long download = 0;
            long upload = 0;
            var ips = "";

            if (app.ProcessName.IsNotEmpty() && byName.TryGetValue(app.ProcessName, out var nameHit))
            {
                download = nameHit.Download;
                upload = nameHit.Upload;
                ips = nameHit.Ips;
            }
            else if (app.ExePath.IsNotEmpty() && byPath.TryGetValue(app.ExePath, out var pathHit))
            {
                download = pathHit.Download;
                upload = pathHit.Upload;
                ips = pathHit.Ips;
            }

            app.Download = download;
            app.Upload = upload;
            app.ActiveIps = ips;
            app.DownloadText = download > 0 ? Utils.HumanFy(download) : "";
            app.UploadText = upload > 0 ? Utils.HumanFy(upload) : "";
        }
    }

    private static void AddTraffic(
        Dictionary<string, (long Download, long Upload, string Ips)> map,
        string key,
        long download,
        long upload,
        string ips)
    {
        if (map.TryGetValue(key, out var existing))
        {
            map[key] = (
                existing.Download > long.MaxValue - download ? long.MaxValue : existing.Download + download,
                existing.Upload > long.MaxValue - upload ? long.MaxValue : existing.Upload + upload,
                MergeIps(existing.Ips, ips));
        }
        else
        {
            map[key] = (download, upload, ips);
        }
    }

    private static string MergeIps(string existing, string add)
    {
        if (add.IsNullOrEmpty())
        {
            return existing;
        }
        if (existing.IsNullOrEmpty())
        {
            return add;
        }
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in existing.Split(',').Concat(add.Split(',')))
        {
            var ip = part.Trim();
            if (ip.IsNotEmpty())
            {
                merged.Add(ip);
            }
        }
        return string.Join(", ", merged);
    }
}
