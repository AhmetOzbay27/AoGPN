using NLog;
using NLog.Config;
using NLog.Targets;

namespace ServiceLib.Common;

/// <summary>
/// Central logging façade backed by NLog.  Two levels:
///   SaveLog     – standard operational logging (always on when EnableLog is true).
///   Verbose     – detailed diagnostic trace (only when EnableVerboseLog is true).
///
/// Usage pattern:  Logging.Verbose("ModuleName", "event", details);
/// </summary>
public class Logging
{
    private static readonly Logger _logger1 = LogManager.GetLogger("Log1");
    private static readonly Logger _logger2 = LogManager.GetLogger("Log2");
    private static readonly Logger _verbose = LogManager.GetLogger("Verbose");

    private static bool _verboseEnabled;

    public static void Setup()
    {
        LoggingConfiguration config = new();
        FileTarget fileTarget = new();
        config.AddTarget("file", fileTarget);
        fileTarget.Layout = "${longdate}-${level:uppercase=true} ${message}";
        fileTarget.FileName = Utils.GetLogPath("${shortdate}.txt");
        config.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, fileTarget));
        LogManager.Configuration = config;
    }

    public static void LoggingEnabled(bool enable)
    {
        if (!enable)
        {
            LogManager.SuspendLogging();
        }
    }

    public static void VerboseLoggingEnabled(bool enable)
    {
        _verboseEnabled = enable;
    }

    // ── standard operational ───────────────────────────────────────────

    public static void SaveLog(string strContent)
    {
        if (!LogManager.IsLoggingEnabled())
        {
            return;
        }

        _logger1.Info(strContent);
    }

    public static void SaveLog(string strTitle, Exception ex)
    {
        if (!LogManager.IsLoggingEnabled())
        {
            return;
        }

        // Write the whole exception (message + inner chain + stack trace) as ONE
        // compact line. Previously the raw multi-line StackTrace was logged, so a
        // single async exception produced a separate log row per stack frame and a
        // day of network timeouts flooded the log with hundreds of identical
        // "at System.Threading..." rows. The full trace is still greppable — just
        // flattened onto one line.
        var flat = ex?.ToString().ReplaceLineBreaks(" | ") ?? "null";
        _logger2.Error($"{strTitle},{flat}");
    }

    // ── verbose diagnostic trace ───────────────────────────────────────

    /// <summary>
    /// Write a structured diagnostic entry.  Thread-safe.
    /// </summary>
    /// <param name="module">Calling module name (e.g. "GPN", "Proxy", "Core").</param>
    /// <param name="event">Short event tag (e.g. "mode_changed", "connect_start").</param>
    /// <param name="detail">Human-readable detail string.</param>
    public static void Verbose(string module, string @event, string detail)
    {
        if (!_verboseEnabled || !LogManager.IsLoggingEnabled())
        {
            return;
        }

        _verbose.Info($"[{module}] {@event} | {detail}");
    }

    /// <summary>Verbose trace with structured key-value pairs for easy grepping.</summary>
    public static void Verbose(string module, string @event, params (string Key, object? Value)[] fields)
    {
        if (!_verboseEnabled || !LogManager.IsLoggingEnabled())
        {
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append('[').Append(module).Append("] ").Append(@event);
        foreach (var (k, v) in fields)
        {
            sb.Append(" | ").Append(k).Append('=').Append(v ?? "(null)");
        }
        _verbose.Info(sb.ToString());
    }

    /// <summary>Write a verbose entry only when the condition is true (avoids allocation).</summary>
    public static void VerboseIf(bool condition, string module, string @event, string detail)
    {
        if (condition)
        {
            Verbose(module, @event, detail);
        }
    }
}