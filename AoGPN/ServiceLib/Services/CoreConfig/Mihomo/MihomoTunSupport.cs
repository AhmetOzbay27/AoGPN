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

    /// <summary>
    /// Sanal makine ağı adaptörleri (seçimde asla öncelik ALMAZ — ölü VMnet'e
    /// bağlanmak WG el sıkışmasını "unreachable network" ile kırar). Makinenin
    /// kendisi bir VM ise son çare olarak yine de kullanılabilir.
    /// </summary>
    private static readonly string[] VirtualAdapterHints =
        ["vmnet", "vmware", "vmxnet", "virtualbox", "vbox", "host-only", "hostonly",
            "veethernet", "hyper-v", "default switch", "wsl", "docker", "parallels", "qemu"];

    private static bool IsMatchAny(string? name, string? description, string[] hints)
    {
        var haystack = $"{name ?? string.Empty} {description ?? string.Empty}";
        return hints.Any(h => haystack.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ad/açıklama TUN benzeri (mihomo'nun kendi adaptörü vb.) mi?</summary>
    public static bool IsTunLikeInterface(string? name, string? description)
        => IsMatchAny(name, description, TunLikeHints);

    /// <summary>Ad/açıklama sanal makine ağı adaptörü (VMware/VMnet, VirtualBox, Hyper-V…) mi?</summary>
    public static bool IsVirtualAdapter(string? name, string? description)
        => IsMatchAny(name, description, VirtualAdapterHints);

    /// <summary>
    /// Saf seçim adayı — gerçek <see cref="NetworkInterface"/> verisinin
    /// soyutlanmış hali (testlerde kolayca üretilebilir).
    /// </summary>
    internal sealed record NicCandidate(
        string Name,
        string? Description,
        NetworkInterfaceType Type,
        OperationalStatus Status,
        bool HasIPv4Gateway,
        /// <summary>Windows GetBestInterface(0): bu adaptör varsayılan rotanın (0.0.0.0/0) sahibi.</summary>
        bool HasDefaultGateway,
        string? Gateway,
        int Index);

    /// <summary>
    /// Adaylar arasından WG sunucusuna ulaşan en iyi fiziksel arayüzü seçer.
    /// Sıralama:
    ///   1. <paramref name="preferredIndexes"/> (GetBestInterface sonuçları):
    ///      önce hedef IP'nin rotası, sonra varsayılan rotanın (0.0.0.0/0) sahibi.
    ///      TUN benzeri ve sanal makine adaptörleri bu yolda ASLA dönülmez — kendi
    ///      TUN'una ya da ölü VMnet adaptörüne bağlanmak WG el sıkışmasını kırar.
    ///   2. Aktif internet yolu: Up + IPv4 gateway sahibi, TUN olmayan adaylar;
    ///      varsayılan rota sahibi (HasDefaultGateway) önce gelir, sonra gerçek
    ///      Ethernet/Wi-Fi, sonra diğer tipler; sanal adaptörler en sona itilir.
    ///   3. Son çare: yalnızca sanal adaptörler varsa (makinenin kendisi bir VM)
    ///      TUN olmayan en iyi aday döner — null'dan iyidir, host rotası kurulabilir.
    /// TUN benzeri adaptörler hiçbir koşulda seçilmez.
    /// </summary>
    internal static PhysicalInterfaceInfo? SelectPhysicalInterface(
        IReadOnlyList<NicCandidate> candidates,
        IReadOnlyList<int> preferredIndexes)
    {
        // 1) Windows'un söylediği en iyi arayüzler (hedef IP → varsayılan rota).
        foreach (var idx in preferredIndexes)
        {
            var ni = candidates.FirstOrDefault(c => c.Index == idx);
            if (ni is null || ni.Status != OperationalStatus.Up)
            {
                continue;
            }
            if (IsTunLikeInterface(ni.Name, ni.Description)
                || IsVirtualAdapter(ni.Name, ni.Description))
            {
                continue;
            }
            return ToInfo(ni);
        }

        // 2) Aktif internet yolu: varsayılan rotanın sahibi, gateway'li, TUN olmayan
        //    fiziksel arayüz; sanal adaptörler en sona itilir (son çare olarak).
        var viable = candidates
            .Where(c => c.Status == OperationalStatus.Up && c.HasIPv4Gateway)
            .Where(c => !IsTunLikeInterface(c.Name, c.Description))
            .OrderByDescending(c => c.HasDefaultGateway)
            .ThenBy(c => IsVirtualAdapter(c.Name, c.Description))
            .ThenByDescending(c => IsPhysicalType(c.Type))
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        if (viable.Count > 0)
        {
            return ToInfo(viable[0]);
        }

        // 3) Son çare: yalnızca sanal adaptörler varsa (örn. makinenin kendisi bir
        //    VM) hiçbiri null dönmesin — mihomo auto-detect'ten iyidir, host rotası
        //    kurulabilir. TUN benzeri yine de asla dönülmez.
        var lastResort = candidates
            .Where(c => c.Status == OperationalStatus.Up && c.HasIPv4Gateway)
            .Where(c => !IsTunLikeInterface(c.Name, c.Description))
            .OrderByDescending(c => c.HasDefaultGateway)
            .ThenByDescending(c => IsPhysicalType(c.Type))
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return lastResort is null ? null : ToInfo(lastResort);
    }

    private static bool IsPhysicalType(NetworkInterfaceType type)
        => type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;

    private static PhysicalInterfaceInfo ToInfo(NicCandidate c)
        => new(c.Name, c.Index, c.Gateway);

    /// <summary>
    /// WG sunucusuna ulaşan fiziksel arayüzü bulur (varsayılan rota/gateway sahibi).
    /// Önce GetBestInterface (IPHelper) ile hedef IP'nin ve varsayılan rotanın
    /// sahibini sorar; sonuç TUN/sanal adaptörse elenir. Ardından Up + IPv4 gateway
    /// sahibi adaylar arasında varsayılan rota sahibi (aktif internet yolu) öncelikli
    /// seçim yapılır. VMware/VMnet gibi sanal makine adaptörleri asla öncelik almaz;
    /// TUN benzeri adaptörler hiçbir koşulda seçilmez.
    /// </summary>
    public static PhysicalInterfaceInfo? DetectPhysicalInterface(string? destinationIp = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // 1) Kesin yol: GetBestInterface — hedef IP'ye giden arayüzü Windows söyler.
        var preferred = new List<int>();
        var defaultRouteIndex = -1;
        if (IPAddress.TryParse(destinationIp, out var dest)
            && dest.AddressFamily == AddressFamily.InterNetwork
            && GetBestInterface(ToNetworkOrderDword(dest), out var bestIndex) == 0)
        {
            preferred.Add((int)bestIndex);
        }
        // 2) Varsayılan rotanın (0.0.0.0/0) sahibi — aktif internet yolu.
        if (GetBestInterface(0, out var defaultIfIndex) == 0)
        {
            defaultRouteIndex = (int)defaultIfIndex;
            preferred.Add(defaultRouteIndex);
        }

        var candidates = new List<NicCandidate>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            try
            {
                // WFP filtre / sanal sözde adaptörlerde (örn. "Yerel Ağ Bağlantısı* 8-WFP")
                // GetIPProperties/GetIPv4Properties "İstenen iletişim kuralı sistemde
                // yapılandırılmamış" ile fırlatabilir (ProbeEgress'te de görülen senaryo) —
                // böyle adaptörler zaten gateway sahibi olamaz, sessizce atlanır.
                var props = ni.GetIPProperties();
                var ipv4 = props.GetIPv4Properties();
                var gw = props.GatewayAddresses.FirstOrDefault(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork);
                candidates.Add(new NicCandidate(
                    ni.Name,
                    ni.Description,
                    ni.NetworkInterfaceType,
                    ni.OperationalStatus,
                    HasIPv4Gateway: gw is not null,
                    HasDefaultGateway: ipv4 is not null && ipv4.Index == defaultRouteIndex,
                    Gateway: gw?.Address.ToString(),
                    Index: ipv4?.Index ?? 0));
            }
            catch (Exception ex)
            {
                DiagLog.Write($"{Tag} adapter read skipped ({ni.Name}): {ex.Message}");
            }
        }

        return SelectPhysicalInterface(candidates, preferred);
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