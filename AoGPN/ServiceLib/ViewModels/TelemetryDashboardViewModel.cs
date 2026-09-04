using System.Reactive.Concurrency;

namespace ServiceLib.ViewModels;

/// <summary>
/// AoGPN "Connection Center" telemetri şeridinin binding kabuğu (P0 Nihai Faz):
/// tüm ölçüm ve hesaplama DashboardTrafficEngine'de yaşar; bu sınıf Changed
/// olayını UI iş parçacığında reaktif özelliklere kopyalar ve sparkline'ları
/// (SamplesChanged) tazeler. Durumun tek sahibi engine'dir.
/// </summary>
public class TelemetryDashboardViewModel : MyReactiveObject
{
    /// <summary>Raised after every sample is recorded so the view can redraw its sparklines.</summary>
    public event Action? SamplesChanged;

    private readonly DashboardTrafficEngine _engine = new();

    public TelemetryDashboardViewModel()
    {
        _engine.Changed += OnEngineChanged;
    }

    private void OnEngineChanged()
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            IsConnected = _engine.IsConnected;
            StatusText = _engine.StatusText;
            PingValue = _engine.PingValue;
            PingText = _engine.PingText;
            PingQuality = _engine.PingQuality;
            LossValue = _engine.LossValue;
            LossText = _engine.LossText;
            LossQuality = _engine.LossQuality;
            DownValue = _engine.DownValue;
            DownText = _engine.DownText;
            UpValue = _engine.UpValue;
            UpText = _engine.UpText;
            DownFraction = _engine.DownFraction;
            UpFraction = _engine.UpFraction;
            DownGaugeMax = _engine.DownGaugeMax;
            UpGaugeMax = _engine.UpGaugeMax;
            SamplesChanged?.Invoke();
        });
    }

    [Reactive] public bool IsConnected { get; set; }

    [Reactive] public string StatusText { get; set; } = "ÇEVRİMDIŞI";

    /// <summary>Ping value in ms, -1 while unknown.</summary>
    [Reactive] public int PingValue { get; set; } = -1;

    [Reactive] public string PingText { get; set; } = "--";

    [Reactive] public string PingQuality { get; set; } = "ölçülüyor…";

    [Reactive] public string LossText { get; set; } = "--";

    /// <summary>Packet-loss percentage for the current rolling probe session, -1 while unknown.</summary>
    [Reactive] public double LossValue { get; set; } = -1;

    [Reactive] public string LossQuality { get; set; } = "kararlı";

    [Reactive] public string DownText { get; set; } = "0.0";

    /// <summary>Download throughput in Mbps from the current statistics delta.</summary>
    [Reactive] public double DownValue { get; set; }

    [Reactive] public string UpText { get; set; } = "0.0";

    /// <summary>Upload throughput in Mbps from the current statistics delta.</summary>
    [Reactive] public double UpValue { get; set; }

    /// <summary>Gauge fill 0..1 for the download ring.</summary>
    [Reactive] public double DownFraction { get; set; }

    /// <summary>Gauge fill 0..1 for the upload ring.</summary>
    [Reactive] public double UpFraction { get; set; }

    /// <summary>Full-scale Mbps value the download gauge currently maps to.</summary>
    [Reactive] public double DownGaugeMax { get; set; } = 25;

    /// <summary>Full-scale Mbps value the upload gauge currently maps to.</summary>
    [Reactive] public double UpGaugeMax { get; set; } = 12;

    /// <summary>Snapshot of the ping history (newest last). Doubles may be NaN = lost sample.</summary>
    public double[] GetPingSamples()
    {
        return _engine.GetPingSamples();
    }

    /// <summary>Snapshot of the download history in Mbps.</summary>
    public double[] GetDownSamples()
    {
        return _engine.GetDownSamples();
    }

    /// <summary>Snapshot of the upload history in Mbps.</summary>
    public double[] GetUpSamples()
    {
        return _engine.GetUpSamples();
    }

    /// <summary>
    /// Starts / stops the sampling loop. The view calls this from Loaded/Unloaded so
    /// the app only measures ping while the dashboard is actually on screen.
    /// </summary>
    public void SetActive(bool active)
    {
        _engine.SetActive(active);
    }
}
