using System.Runtime.CompilerServices;
using ServiceLib.Events;
using ServiceLib.Models;

namespace ServiceLib.Common;

/// <summary>
/// Standalone diagnostic file logger.  Always active — does not depend on
/// NLog configuration, the <c>EnableLog</c> checkbox, or verbose-log mode.
/// Designed for socket-flush traces, QUIC-block decisions, and tunnel-startup
/// diagnostics that must survive in the field regardless of user settings.
///
/// Writes to <c>&lt;startup&gt;\Logs\ao_diag.txt</c> (same folder as every
/// other AoGPN log).  Calls are cheap when no subscriber calls
/// <see cref="Write"/> (the message is discarded).
/// </summary>
public static class DiagLog
{
    private static readonly string _logPath;
    private static readonly object _lock = new();

    static DiagLog()
    {
        try
        {
            // Colocate with the NLog/session logs under the Logs folder.
            _logPath = Utils.GetLogPath("ao_diag.txt");
        }
        catch
        {
            _logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AoGPN", "Logs", "ao_diag.txt");
        }
    }

    /// <summary>
    /// Appends a timestamped line to the diagnostic log.
    /// Thread-safe; silently swallows I/O errors so the caller never crashes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // Diagnostic log is best-effort; never crash the caller.
        }

        // GPN_* satırlarını dashboard'a akan olay yayınına bağla. Dosya yazımı
        // başarısız olsa bile yayın yapılır; yayın da best-effort'tur.
        PublishGpnLine(message);

        // Bağlantı/test sürecinin TAMAMINI oturum denetim günlüğüne yansıt
        // (VPN kopunca canlı iletişim kesilse de log diske yazılır; sonraki
        // oturumda okunup tanı konulur). GPN ve VPN (normal) akışları AYRI
        // dosyalara düşer — neyin ne olduğu net görülsün:
        //   GPN_*  → gpn-session.log
        //   diğer  → vpn-session.log  (CORE_/TUN_/ROUTE/FLUSH/QUIC ...)
        // WEBVIEW_PERF örnekleri yüksek frekanslıdır ve oturum tanısına katkısız
        // olur — her iki dosyaya da yazılmaz.
        if (message.StartsWith("GPN_", StringComparison.Ordinal))
        {
            GpnSessionLog.Record(message);
        }
        else if (!message.StartsWith("WEBVIEW_PERF", StringComparison.Ordinal))
        {
            VpnSessionLog.Record(message);
        }
    }

    /// <summary>
    /// "GPN_" önekli satırları <see cref="AppEvents.GpnDiagChanged"/> üzerinden
    /// yayınlar. Kind, önekten sonraki ilk sözcüktür (LOG / RECOVER / SELECT /
    /// FAILOVER / LAUNCH ...). Diğer diyagnoz kategorileri (FLUSH, TUN_*, ROUTE,
    /// CORE_*, QUIC...) yayınlanmaz — dashboard yalnızca GPN akışını izler.
    /// </summary>
    private static void PublishGpnLine(string message)
    {
        if (message is null || !message.StartsWith("GPN_", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var kind = "LOG";
            var space = message.IndexOf(' ');
            kind = space > 4
                ? message[4..space]
                : message.Length > 4 ? message[4..] : "LOG";

            AppEvents.GpnDiagChanged.Publish(new GpnDiagEvent(
                kind,
                message,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        catch
        {
            // Best-effort; as with the file write, never crash the caller.
        }
    }

    /// <summary>
    /// Full path to the diagnostic log file so the user knows where to look.
    /// </summary>
    public static string FilePath => _logPath;
}