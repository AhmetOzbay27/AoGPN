using ServiceLib.Events;
using ServiceLib.Models;

namespace ServiceLib.Services;

/// <summary>
/// Son 50 GpnResilience kararını tutan döngüsel günlük tamponu.
/// <see cref="AppEvents.GpnResilienceChanged"/> akışını dinler; her kararı
/// tampona ekler (kapasite taştığında en eski karar düşer) ve tamponu
/// <c>&lt;startup&gt;\Logs\gpn_resilience.log</c> dosyasına yazarak sorun
/// giderme için dosyada güncel bir \"son 50 karar\" penceresi tutar.
///
/// Kurtarma döngüsü (Recover / ModeFallback), sunucu değişimi (ServerSwitch),
/// UDP ölümü (UdpDeath) ve başlangıç kararları (ModeDecision) dahildir.
///
/// Thread-safe (low-frequency olaylar için lock); testler için izole kanal ctor'u.
/// </summary>
public sealed class GpnResilienceLog : IDisposable
{
    public const int Capacity = 50;

    private readonly object _lock = new();
    private readonly List<GpnResilienceLogEntry> _entries = new(Capacity);
    private readonly IDisposable _subscription;
    private readonly string _logPath;

    public GpnResilienceLog()
        : this(AppEvents.GpnResilienceChanged, DefaultLogPath())
    {
    }

    /// <summary>Test desteği: izole kanal + özel dosya yolu.</summary>
    internal GpnResilienceLog(EventChannel<GpnResilienceEvent> source, string logPath)
    {
        _logPath = logPath;
        _subscription = source.AsObservable().Subscribe(OnEvent);
    }

    private static string DefaultLogPath() =>
        Utils.GetLogPath("gpn_resilience.log");

    public string LogPath => _logPath;

    private void OnEvent(GpnResilienceEvent evt)
    {
        var entry = ToEntry(evt);
        string[] linesSnapshot;
        lock (_lock)
        {
            _entries.Add(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }
            linesSnapshot = _entries.Select(e => e.Line).ToArray();
        }

        WriteLogFile(linesSnapshot);
    }

    /// <summary>Son N (varsayılan 50) kararı en eskiden en yeniye doğru döndürür.</summary>
    public IReadOnlyList<GpnResilienceLogEntry> Recent
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Tampondaki mevcut kayıt sayısı.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Tamponu ve dosyayı sıfırlar.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
        try
        {
            System.IO.File.WriteAllText(_logPath, string.Empty);
        }
        catch
        {
            // best-effort
        }
    }

    private static GpnResilienceLogEntry ToEntry(GpnResilienceEvent evt)
    {
        var ts = evt.OccurredAt;
        var time = ts.ToLocalTime().ToString("HH:mm:ss.fff");
        var action = evt.Action.ToString();
        var mode = evt.ToMode == ConnectionMode.V2rayTCP ? "V2rayTCP" : "WireGuard";
        var server = evt.ServerName ?? evt.ServerId ?? "";
        var target = evt.TargetServerName ?? evt.TargetServerId ?? "";

        var sb = new System.Text.StringBuilder();
        sb.Append('[').Append(time).Append("] ").Append(action).Append(" → ").Append(mode);
        if (!string.IsNullOrEmpty(server)) sb.Append("  ").Append(server);
        if (!string.IsNullOrEmpty(target)) sb.Append(" → ").Append(target);
        if (!string.IsNullOrEmpty(evt.Reason)) sb.Append("  reason=").Append(evt.Reason);

        return new GpnResilienceLogEntry(
            TimestampMs: ts.ToUnixTimeMilliseconds(),
            Action: evt.Action,
            ServerId: evt.ServerId,
            ServerName: evt.ServerName,
            TargetServerId: evt.TargetServerId,
            TargetServerName: evt.TargetServerName,
            FromMode: evt.FromMode,
            ToMode: evt.ToMode,
            Reason: evt.Reason,
            Line: sb.ToString());
    }

    private void WriteLogFile(string[] lines)
    {
        try
        {
            var content = string.Join(Environment.NewLine, lines);
            if (lines.Length > 0) content += Environment.NewLine;
            System.IO.File.WriteAllText(_logPath, content);
        }
        catch
        {
            // Dosya yazımı best-effort — karar sayacı (gösterge) dosyadan bağımsızdır.
        }
    }

    public void Dispose() => _subscription.Dispose();
}
