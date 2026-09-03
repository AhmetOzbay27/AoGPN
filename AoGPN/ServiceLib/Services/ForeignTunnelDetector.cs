using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using ServiceLib.Manager;

namespace ServiceLib.Services;

public sealed record ForeignTunnelConflictResult
{
    public IReadOnlyList<string> TunAdapterNames { get; init; } = [];
    public IReadOnlyList<string> ForeignProcessNames { get; init; } = [];
    public bool PortOccupiedByForeignProcess { get; init; }

    public bool HasTunConflict => TunAdapterNames.Count > 0;
    public bool HasPortConflict => PortOccupiedByForeignProcess;
    public bool HasProcessConflict => ForeignProcessNames.Count > 0;
    public bool HasConflicts => HasTunConflict || HasPortConflict || HasProcessConflict;
}

/// <summary>
/// Detects foreign VPN state before AoGPN starts its own tunnel: a TUN adapter that
/// belongs to another VPN client (v2rayN's <c>xray_tun</c>, wintun-based clients, ...),
/// a well-known third-party VPN client process (WireGuard for Windows, v2rayN, ...), or
/// a foreign process already listening on AoGPN's local proxy port. Two TUN stacks
/// at once capture each other's traffic and kill the internet, and a busy proxy port
/// means the core cannot bind — so the caller can warn the user before connecting.
/// Third-party VPN clients are never terminated; closing them is the user's decision.
/// </summary>
public sealed class ForeignTunnelDetector
{
    /// <summary>The TUN adapter names owned by AoGPN itself (sing-box creates it).</summary>
    public static readonly string[] AppOwnedTunNames =
        ["singbox_tun", "wintunsingbox_tun"];

    /// <summary>
    /// Windows'un gömülü IPv6 geçiş / sözde (pseudo) arayüz ad parçaları — gerçek
    /// VPN tünelleri DEĞİLDİRLER, o yüzden asla yabancı-tünel çakışması sayılmamalıdır.
    /// Teredo, isatap, 6to4 ve loopback pseudo-interface'leri Win10/11'de her zaman
    /// var olur ve adları "tun" içerdiği için, bu filtre olmadan her GPN bağlantısında
    /// "kapatılamaz yabancı tünel" (calıntı) olarak loglanırlardı.
    /// </summary>
    public static readonly string[] BuiltInTunLikeNameHints =
    [
        "teredo",
        "isatap",
        "6to4",
        "pseudo",
        "loopback",
    ];

    /// <summary>
    /// Adında Windows gömülü IPv6 geçiş/sözde arayüz ipucu var mı (Teredo vb.).
    /// Gerçek (yabancı) VPN adaptörleri — wintun, WireGuard Tunnel, xray_tun —
    /// bu ipuçlarını içermediği için yine de yakalanır.
    /// </summary>
    public static bool IsBuiltInTunLikeInterfaceName(string? name)
        => !name.IsNullOrEmpty()
           && BuiltInTunLikeNameHints.Any(hint =>
               name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Well-known third-party VPN client executables that carry their own tunnel.
    /// Used for DETECTION/REPORTING only — these processes are never terminated;
    /// AoGPN surfaces them so the user can decide. AoGPN's own processes are
    /// deliberately absent: its cores are sing-box / xray / mihomo and the app
    /// itself (AoGPN) are never treated as foreign. Matched case-insensitively
    /// against the process name (with or without .exe).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownForeignVpnProcessNames =
    [
        "wireguard.exe",   // WireGuard for Windows (GUI + service tunnel both run this binary)
        "wg.exe",          // WireGuard CLI
        "v2rayn.exe",      // v2rayN
        "nekoray.exe",
        "nekobox.exe",
        "hiddify.exe",
        "mullvad.exe",
        "mullvadvpn.exe",
        "openvpn.exe",
        "openvpn-gui.exe",
        "protonvpn.exe",
        "nordvpn.exe",
        "surfshark.exe",
        "windscribe.exe",
        "tailscale.exe",
        "outlinevpn.exe",
    ];

    private readonly Func<IReadOnlyList<string>> _tunAdapterNames;
    private readonly Func<int, bool> _isPortListening;
    private readonly Func<bool> _isAppCoreRunning;
    private readonly Func<IReadOnlyList<string>> _foreignProcessNames;

    public ForeignTunnelDetector(
        Func<IReadOnlyList<string>>? tunAdapterNames = null,
        Func<int, bool>? isPortListening = null,
        Func<bool>? isAppCoreRunning = null,
        Func<IReadOnlyList<string>>? foreignProcessNames = null)
    {
        _tunAdapterNames = tunAdapterNames ?? EnumerateForeignTunAdapters;
        _isPortListening = isPortListening ?? IsPortListening;
        _isAppCoreRunning = isAppCoreRunning ?? IsAppCoreRunning;
        _foreignProcessNames = foreignProcessNames ?? EnumerateForeignVpnProcesses;
    }

    public ForeignTunnelConflictResult Detect(int localPort)
    {
        var tunNames = _tunAdapterNames();
        var processNames = _foreignProcessNames();
        // When AoGPN's own core is already running (connection or proxy-only) it is
        // the legitimate owner of the local proxy port; only flag the port when
        // something else grabbed it.
        var portOccupied = _isPortListening(localPort) && !_isAppCoreRunning();
        return new ForeignTunnelConflictResult
        {
            TunAdapterNames = tunNames,
            ForeignProcessNames = processNames,
            PortOccupiedByForeignProcess = portOccupied
        };
    }

    public static bool IsAppOwnedTunName(string name)
    {
        return AppOwnedTunNames.Any(
            owned => owned.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> EnumerateForeignTunAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                    && ni.Name.Contains("tun", StringComparison.OrdinalIgnoreCase)
                    && !IsAppOwnedTunName(ni.Name)
                    // Windows gömülü IPv6 geçiş/sözde arayüzleri (Teredo, isatap,
                    // 6to4, loopback pseudo) gerçek VPN tüneli değildir — çakışma
                    // sayılmamalıdır (yoksa her bağlantıda boşuna uyarı basılırdı).
                    && !IsBuiltInTunLikeInterfaceName(ni.Name))
                .Select(ni => ni.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ForeignTunnelDetector adapter enumeration failed", ex);
            return [];
        }
    }

    /// <summary>Yalnızca tespit: çalışan yabancı VPN istemcilerini listeler, asla öldürmez.</summary>
    private static IReadOnlyList<string> EnumerateForeignVpnProcesses()
    {
        var found = new List<string>();
        foreach (var name in KnownForeignVpnProcessNames)
        {
            var baseName = Path.GetFileNameWithoutExtension(name);
            try
            {
                if (Process.GetProcessesByName(baseName).Length > 0)
                {
                    found.Add(name);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"ForeignTunnelDetector process lookup failed for {name}", ex);
            }
        }

        return found;
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listeners.Any(endpoint => endpoint.Port == port);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ForeignTunnelDetector listener enumeration failed", ex);
            return false;
        }
    }

    private static bool IsAppCoreRunning()
    {
        var health = AppManager.Instance.CoreEngineHost?.GetHealth(CoreHealthRole.Main);
        return health?.State is CoreHealthState.Starting
            or CoreHealthState.Ready
            or CoreHealthState.Degraded;
    }
}
