namespace ServiceLib.Services.CoreConfig.Mihomo;

/// <summary>
/// mihomo TUN entegrasyonu için Windows yardımcıları.
///
/// Canlı doğrulamada (bu makinede, Ağu 2026) kanıtlanan kombinasyon:
///   1. "interface-name": fiziksel NIC adı — mihomo'nun tüm çıkış dial
///      soketlerini fiziksel ağa kilitler; TUN'un /1 varsayılan rotasına
///      döngü yapmaz.
///   2. WG sunucu IP'si için host rotası (/32, klasik wg-quick deseni) —
///      el sıkışma paketleri asla TUN'a girmez.
/// Her ikisi de GPN "Bağlan"da otomatik uygulanır; kapanışta rota geri alınır.
/// </summary>
public static class MihomoTunSupport
{
    private const string Tag = "MihomoTun";
    private static readonly object RouteLock = new();
    private static string? _hostRouteIp;

    /// <summary>Fiziksel ağ tespit sonucu (mihomo interface-name + host rotası için).</summary>
    public sealed record PhysicalInterfaceInfo(string Name, int Index, string? Gateway);

    private static readonly string[] TunLikeHints =
        ["wintun", "wireguard", "xray", "singbox", "sing-box", "tun", "tap", "aogpn", "clash"];

    /// <summary>Ad/açıklama TUN benzeri (mihomo'nun kendi adaptörü vb.) mi?</summary>
    public static bool IsTunLikeInterface(string? name, string? description)
    {
        var haystack = $"{name ?? string.Empty} {description ?? string.Empty}";
        return TunLikeHints.Any(h => haystack.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// WG sunucusuna ulaşan fiziksel arayüzü bulur (varsayılan rota/gateway sahibi).
    /// Önce GetBestInterface (IPHelper) ile kesin seçim; olmazsa Up + IPv4 gateway
    /// sahibi ilk Ethernet/WiFi arayüze düşer. TUN benzeri adaptörler asla seçilmez.
    /// </summary>
    public static PhysicalInterfaceInfo? DetectPhysicalInterface(string? destinationIp = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // 1) Kesin yol: GetBestInterface — hedef IP'ye giden arayüzü Windows söyler.
        if (IPAddress.TryParse(destinationIp, out var dest)
            && dest.AddressFamily == AddressFamily.InterNetwork)
        {
            if (GetBestInterface(ToNetworkOrderDword(dest), out var bestIndex) == 0)
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    var props = ni.GetIPProperties();
                    if (props.GetIPv4Properties()?.Index == (int)bestIndex)
                    {
                        return new PhysicalInterfaceInfo(ni.Name, (int)bestIndex,
                            props.GatewayAddresses.FirstOrDefault(g =>
                                g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString());
                    }
                }
            }
        }

        // 2) Yedek: Up + IPv4 gateway sahibi ilk fiziksel arayüz.
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                     .OrderByDescending(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                         or NetworkInterfaceType.Wireless80211))
        {
            if (ni.OperationalStatus != OperationalStatus.Up
                || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            var props = ni.GetIPProperties();
            var gw = props.GatewayAddresses.FirstOrDefault(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gw is null)
            {
                continue;
            }
            if (IsTunLikeInterface(ni.Name, ni.Description))
            {
                continue;
            }
            return new PhysicalInterfaceInfo(
                ni.Name,
                props.GetIPv4Properties()?.Index ?? 0,
                gw.Address.ToString());
        }

        return null;
    }

    /// <summary>mihomo config "interface-name" değeri (boş olabilir → mihomo auto-detect).</summary>
    public static string? DetectPhysicalInterfaceName(string? destinationIp = null)
        => DetectPhysicalInterface(destinationIp)?.Name;

    /// <summary>
    /// IPv4'ü GetBestInterface'in beklediği ağ bayt sıralı (big-endian) DWORD'a
    /// çevirir. UNSIGNED kaydırma kullanılır: son oktet ≥ 128 olan adreslerde
    /// (ör. 92.4.220.236) `byte << 24` imzalı int taşması OverflowException fırlatır
    /// (proje CheckForOverflowUnderflow=true) — Italya bağlantısı bu yüzden
    /// "Varsayılan yapılandırma dosyası oluşturulamadı" ile düşüyordu.
    /// </summary>
    internal static uint ToNetworkOrderDword(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>
    /// WG sunucu IP'si için /32 host rotası ekler (TUN açılmadan önce çağrılır).
    /// Aynı IP için ikinci çağrı no-op'tur. Hedef domain ise atlanır (GPN IP kullanır).
    /// </summary>
    public static bool EnsureHostRoute(string? serverHost)
    {
        if (!OperatingSystem.IsWindows() || serverHost.IsNullOrEmpty() || !IsIpv4(serverHost))
        {
            return false;
        }

        lock (RouteLock)
        {
            if (_hostRouteIp == serverHost)
            {
                return true; // zaten kurulu
            }
            var nic = DetectPhysicalInterface(serverHost);
            if (nic is null || nic.Gateway.IsNullOrEmpty() || nic.Index <= 0)
            {
                DiagLog.Write($"{Tag} host-route skip: fiziksel arayüz bulunamadı ({serverHost})");
                return false;
            }
            // Çökme/force-kill sonrası aynı hedefte STALE rota kalabilir (önceki
            // oturumun teardown'ı hiç çalışmadıysa); `route ADD` o zaman "already
            // exists" ile düşer. Önce best-effort sil — rota yoksa sessizce atlanır.
            RunRouteCommand($"DELETE {serverHost} mask 255.255.255.255", logErrors: false);
            if (RunRouteCommand($"ADD {serverHost} mask 255.255.255.255 {nic.Gateway} IF {nic.Index} METRIC 5"))
            {
                _hostRouteIp = serverHost!;
                DiagLog.Write($"{Tag} host-route OK {serverHost} via {nic.Gateway} IF {nic.Index} (wg-quick deseni)");
                return true;
            }
            DiagLog.Write($"{Tag} host-route FAILED {serverHost} (route ADD hatası)");
            return false;
        }
    }

    /// <summary>Kurulmuş host rotasını kaldırır (idempotent — çağrılmadıysa no-op).</summary>
    public static void RemoveHostRoute()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        lock (RouteLock)
        {
            if (_hostRouteIp.IsNullOrEmpty())
            {
                return;
            }
            RunRouteCommand($"DELETE {_hostRouteIp} mask 255.255.255.255");
            DiagLog.Write($"{Tag} host-route removed {_hostRouteIp}");
            _hostRouteIp = null;
        }
    }

    private static bool IsIpv4(string host)
        => IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    private static bool RunRouteCommand(string args, bool logErrors = true)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "route.exe"),
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }
            proc.WaitForExit(10000);
            if (proc.ExitCode != 0)
            {
                if (logErrors)
                {
                    DiagLog.Write($"{Tag} route {args} exit={proc.ExitCode} {proc.StandardError.ReadToEnd().Trim()}");
                }
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"{Tag} route exception: {ex.Message}");
            return false;
        }
    }

    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetBestInterface(uint destAddr, out uint bestIfIndex);
}