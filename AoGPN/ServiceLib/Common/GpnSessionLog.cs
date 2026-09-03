using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using ServiceLib.Manager;

namespace ServiceLib.Common;

/// <summary>
/// AOGPN oturum denetim günlükleri — GPN ve VPN (normal) akışları için AYRI:
///
///   GPN: &lt;startup&gt;\Logs\gpn-session.log   (seçim, WG köprü, el sıkışma,
///                                         failover, GPN IP doğrulama, uygulamalar)
///   VPN: &lt;startup&gt;\Logs\vpn-session.log   (normal bağlantı, çekirdek/TUN, proxy)
///
/// NEDEN: Kullanıcı resmi WireGuard uygulamasıyla bağlıyken VPN'i kestiğinde
/// uzaktaki ajanla canlı iletişim kopuyor. Bağlantı/test sürecinin TAMAMI ilgili
/// dosyaya yazılır ki kullanıcı testleri bitirip geri döndüğünde loglar okunup
/// tanı konulabilsin — hangi akışın (GPN mi VPN mi) ne yaptığı net görülsün.
///
/// Yapı (her dosya): oturum blokları — START trigger → ortam anlık görüntüsü →
/// zaman damgalı adımlar → END reason + elapsed. Son 5 oturum korunur (kırpma).
/// Tüm yazımlar best-effort: I/O hatası asla çağıranı düşürmez.
/// </summary>
public static class GpnSessionLog
{
    private static readonly SessionLogCore _core = new("gpn-session.log");

    public static string FilePath => _core.FilePath;
    public static bool IsSessionActive => _core.IsSessionActive;

    public static void BeginSession(string trigger) => _core.BeginSession(trigger);
    public static void Record(string message) => _core.Record(message);
    public static void EndSession(string reason) => _core.EndSession(reason);

    /// <summary>Test dikişi: günlük yolunu geçici dosyaya yönlendirir (üretimde kullanılmaz).</summary>
    internal static void SetPathForTesting(string path) => _core.SetPathForTesting(path);
}

/// <summary>VPN (normal bağlantı) akışının oturum günlüğü — <see cref="GpnSessionLog"/> ikizi.</summary>
public static class VpnSessionLog
{
    private static readonly SessionLogCore _core = new("vpn-session.log");

    public static string FilePath => _core.FilePath;
    public static bool IsSessionActive => _core.IsSessionActive;

    public static void BeginSession(string trigger) => _core.BeginSession(trigger);
    public static void Record(string message) => _core.Record(message);
    public static void EndSession(string reason) => _core.EndSession(reason);

    /// <summary>Test dikişi.</summary>
    internal static void SetPathForTesting(string path) => _core.SetPathForTesting(path);
}

/// <summary>
/// Tek dosya/alan için oturum günlüğü çekirdeği. Her <see cref="GpnSessionLog"/> /
/// <see cref="VpnSessionLog"/> kendi örneğini tutar; küresel statik durum yarışı
/// olmaması için testler ayrı sıralı koleksiyonda koşar.
/// </summary>
internal sealed class SessionLogCore
{
    private const int KeepSessions = 5;
    private const string StartMarker = "=== SESSION START ";
    private const string EndMarker = "=== SESSION END ";

    private string _logPath;
    private readonly object _lock = new();

    private DateTime _sessionStartedUtc;
    private bool _sessionActive;

    public SessionLogCore(string fileName)
    {
        try
        {
            // Colocate with every other AoGPN log under the Logs folder.
            _logPath = Utils.GetLogPath(fileName);
        }
        catch
        {
            // Son çare: exe dizini
            try
            {
                _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            }
            catch
            {
                _logPath = fileName;
            }
        }
    }

    public string FilePath => _logPath;

    public bool IsSessionActive => _sessionActive;

    public void BeginSession(string trigger)
    {
        lock (_lock)
        {
            if (_sessionActive)
            {
                return; // idempotent — oturum zaten açık
            }

            _sessionStartedUtc = DateTime.UtcNow;
            _sessionActive = true;

            AppendLine(StartMarker + NowStamp() + " | " + Sanitize(trigger));
            foreach (var line in CaptureEnvironmentSnapshot())
            {
                AppendLine("   " + line);
            }
        }
    }

    public void EndSession(string reason)
    {
        lock (_lock)
        {
            if (!_sessionActive)
            {
                return;
            }

            var elapsed = DateTime.UtcNow - _sessionStartedUtc;
            AppendLine(EndMarker + NowStamp() + " | " + Sanitize(reason)
                + $" | elapsed={(long)elapsed.TotalMilliseconds}ms");
            _sessionActive = false;

            TrimOldSessions();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_lock)
        {
            if (!_sessionActive)
            {
                BeginSessionCore("first-log");
            }
            AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}");
        }
    }

    internal void SetPathForTesting(string path)
    {
        lock (_lock)
        {
            _logPath = path;
            _sessionActive = false;
        }
    }

    // ── iç ───────────────────────────────────────────────────────────────

    private void BeginSessionCore(string trigger)
    {
        _sessionStartedUtc = DateTime.UtcNow;
        _sessionActive = true;
        AppendLine(StartMarker + NowStamp() + " | " + Sanitize(trigger));
    }

    private static string NowStamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    private static string Sanitize(string s)
        => string.IsNullOrEmpty(s) ? "-" : s.Replace('\r', ' ').Replace('\n', ' ');

    private void AppendLine(string line)
    {
        // Çekirdek çıktısından (CORE_OUT) gelen satırlar kontrol karakterleri
        // içerebilir (NUL vb.) — günlüğü kirletmesinler. Yalnızca NUL/control
        // içeren satırlarda maliyetli yola girilir; normal satırlarda hızlı yol.
        if (!string.IsNullOrEmpty(line) && HasControlChars(line))
        {
            line = StripControlChars(line);
        }
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // best-effort — asla düşürme
        }
    }

    private static bool HasControlChars(string s)
    {
        foreach (var c in s)
        {
            if (c < ' ' && c is not '\t' and not '\r' and not '\n')
            {
                return true;
            }
        }
        return false;
    }

    private static string StripControlChars(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= ' ' || c is '\t' or '\r' or '\n')
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Ortam anlık görüntüsü — her oturum başında (tanı için kritik).</summary>
    private static IEnumerable<string> CaptureEnvironmentSnapshot()
    {
        var lines = new List<string>
        {
            $"env os={Environment.OSVersion}",
            $"env framework={Environment.Version}",
            $"env admin={Utils.IsAdministrator()}",
            $"env arch={RuntimeInformation.OSArchitecture}",
        };

        try
        {
            var port = AppManager.Instance.GetLocalPort(Enums.EInboundProtocol.socks);
            lines.Add($"env socks-port={port}");
        }
        catch
        {
            lines.Add("env socks-port=?");
        }

        // Ağ adaptörleri (ad + IPv4) — yabancı tünel (ör. resmi WG 'Almanya-client')
        // veya AOGPN adaptörünün varlığını logdan tek başına görebilmek için.
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                var ipv4 = ni.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToArray();
                lines.Add($"env adapter name={ni.Name} type={ni.NetworkInterfaceType} ipv4=[{string.Join(",", ipv4)}]");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"env adapters-error={ex.Message}");
        }

        // Varsayılan rotalar (ağ geçidi + metric) — trafik tünelden mi gidiyor.
        try
        {
            var gateways = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                && a.IPv4Mask is not null)
                    .Select(a => new { Iface = ni.Name, Addr = a.Address.ToString(), Mask = a.IPv4Mask.ToString() }))
                .SelectMany(x =>
                {
                    var rows = new List<string>();
                    try
                    {
                        var gw = NetworkInterface.GetAllNetworkInterfaces()
                            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                            .Where(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            .Select(g => g.Address.ToString())
                            .ToArray();
                        rows.Add($"env route iface={x.Iface} addr={x.Addr} mask={x.Mask} gw=[{string.Join(",", gw)}]");
                    }
                    catch
                    {
                        rows.Add($"env route iface={x.Iface} addr={x.Addr} mask={x.Mask}");
                    }
                    return rows;
                })
                .Distinct()
                .Take(20);
            lines.AddRange(gateways);
        }
        catch (Exception ex)
        {
            lines.Add($"env routes-error={ex.Message}");
        }

        return lines;
    }

    /// <summary>Dosyayı son <see cref="KeepSessions"/> oturumla sınırlar (eski bloklar kırpılır).</summary>
    private void TrimOldSessions()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return;
            }

            var text = File.ReadAllText(_logPath);
            var starts = new List<int>();
            var idx = 0;
            while ((idx = text.IndexOf(StartMarker, idx, StringComparison.Ordinal)) >= 0)
            {
                starts.Add(idx);
                idx += StartMarker.Length;
            }

            if (starts.Count <= KeepSessions)
            {
                return;
            }

            var cut = starts[^KeepSessions];
            File.WriteAllText(_logPath, text[cut..]);
        }
        catch
        {
            // best-effort
        }
    }
}
