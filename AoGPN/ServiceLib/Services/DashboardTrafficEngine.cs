using System.Reactive.Concurrency;

namespace ServiceLib.Services;

/// <summary>
/// Powers the AoGPN "Connection Center" telemetry strip: live ping, packet-loss
/// estimate and real proxy download/upload speeds with a 60-sample rolling history
/// for the sparklines. Ping is measured through the local SOCKS proxy on a throttled
/// loop; speeds come from the real-time statistics events the core already produces.
/// </summary>
// P0 Nihai Faz — ham ölçüm motoru: istatistik olayları (ServerSpeedItem), ping
// ölçümü, hız/gauge/kayıp matematiği ve örnek tamponları bu sınıfta yaşar.
// WPF bağımlılığı yoktur; her değişim <see cref="Changed"/> ile yayınlanır ve
// binding kabuğu (TelemetryDashboardViewModel) UI iş parçacığında kopyalar.
public sealed class DashboardTrafficEngine
{
    /// <summary>How many samples the sparklines keep (one per second ≈ 60 s).</summary>
    public const int MaxSamples = 60;

    /// <summary>Raised after every sample is recorded so the view can redraw its sparklines.</summary>
    public event Action? Changed;

    private readonly object _lock = new();
    private readonly List<double> _pingSamples = [];
    private readonly List<double> _downSamples = [];
    private readonly List<double> _upSamples = [];
    private readonly SemaphoreSlim _pingGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private bool _active;
    private const double KilobytesPerSecondToMegabitsPerSecond = 8.0 * 1024.0 / 1_000_000.0;

    private int _pingTotal;
    private int _pingMisses;

    public bool IsConnected { get; set; }

    public string StatusText { get; set; } = "ÇEVRİMDIŞI";

    /// <summary>Ping value in ms, -1 while unknown.</summary>
    public int PingValue { get; set; } = -1;

    public string PingText { get; set; } = "--";

    public string PingQuality { get; set; } = "ölçülüyor…";

    public string LossText { get; set; } = "--";

    /// <summary>Packet-loss percentage for the current rolling probe session, -1 while unknown.</summary>
    public double LossValue { get; set; } = -1;

    public string LossQuality { get; set; } = "kararlı";

    public string DownText { get; set; } = "0.0";

    /// <summary>Download throughput in Mbps from the current statistics delta.</summary>
    public double DownValue { get; set; }

    public string UpText { get; set; } = "0.0";

    /// <summary>Upload throughput in Mbps from the current statistics delta.</summary>
    public double UpValue { get; set; }

    /// <summary>Gauge fill 0..1 for the download ring.</summary>
    public double DownFraction { get; set; }

    /// <summary>Gauge fill 0..1 for the upload ring.</summary>
    public double UpFraction { get; set; }

    /// <summary>Full-scale Mbps value the download gauge currently maps to.</summary>
    public double DownGaugeMax { get; set; } = 25;

    /// <summary>Full-scale Mbps value the upload gauge currently maps to.</summary>
    public double UpGaugeMax { get; set; } = 12;

    /// <summary>
    /// Nice round full-scale values for the adaptive gauges. The scale grows
    /// instantly to the first value above the observed peak (15% headroom) so a
    /// fast link never pegs the dial, and decays slowly back toward the floor
    /// while the link is idle so a slow link stays legible.
    /// </summary>
    private static readonly double[] NiceGaugeScales = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    private static double NextNiceScale(double mbps, double floor)
    {
        foreach (var scale in NiceGaugeScales)
        {
            if (scale >= mbps * 1.15 && scale >= floor)
            {
                return scale;
            }
        }
        return NiceGaugeScales[^1];
    }

    private static double CalibrateGauge(double currentScale, double mbps, double floor)
    {
        if (mbps >= currentScale * 0.85)
        {
            return NextNiceScale(mbps, floor);
        }

        // Shrink in nice steps, only when the link is meaningfully below the
        // current full-scale — steady traffic keeps a stable scale instead of
        // decaying every sample.
        if (mbps < currentScale * 0.5 && currentScale > floor)
        {
            var candidate = floor;
            foreach (var scale in NiceGaugeScales)
            {
                if (scale < currentScale && scale >= floor)
                {
                    candidate = scale;
                }
            }
            return mbps < candidate * 0.85 ? candidate : currentScale;
        }
        return currentScale;
    }

    public DashboardTrafficEngine()
    {
        AppEvents.DispatcherStatisticsRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnStatistics);
    }

    /// <summary>Snapshot of the ping history (newest last). Doubles may be NaN = lost sample.</summary>
    public double[] GetPingSamples()
    {
        lock (_lock)
        {
            return _pingSamples.ToArray();
        }
    }

    /// <summary>Snapshot of the download history in Mbps.</summary>
    public double[] GetDownSamples()
    {
        lock (_lock)
        {
            return _downSamples.ToArray();
        }
    }

    /// <summary>Snapshot of the upload history in Mbps.</summary>
    public double[] GetUpSamples()
    {
        lock (_lock)
        {
            return _upSamples.ToArray();
        }
    }

    /// <summary>
    /// Starts / stops the sampling loop. The view calls this from Loaded/Unloaded so
    /// the app only measures ping while the dashboard is actually on screen.
    /// </summary>
    public void SetActive(bool active)
    {
        if (active == _active)
        {
            return;
        }
        _active = active;
        if (active)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = LoopAsync(_cts.Token);
        }
        else
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var connected = AppManager.Instance.IsRunningCore(ECoreType.sing_box)
                    || AppManager.Instance.IsRunningCore(ECoreType.Xray);
                RxSchedulers.MainThreadScheduler.Schedule(() => ApplyConnectionState(connected));

                if (connected)
                {
                    await MeasurePingAsync(token);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TelemetryDashboard.Loop", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MeasurePingAsync(CancellationToken token)
    {
        // The dashboard is only on screen while the main window is visible; skip
        // measuring (and the network chatter) when the app sits in the tray.
        if (!AppManager.Instance.ShowInTaskbar)
        {
            return;
        }

        // Never overlap pings — if the previous one is still running, skip this tick.
        if (!await _pingGate.WaitAsync(0, token))
        {
            return;
        }
        try
        {
            var ping = -1;
            try
            {
                var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
                var proxy = new WebProxy($"socks5://{Global.Loopback}:{port}");
                // Generous per-sample budget: a distant/slow tunnel round-trip
                // routinely exceeds the tight 4s used elsewhere, and a miss here
                // made the dashboard ping flicker to "--". The _pingGate already
                // prevents overlapping probes, so extra headroom is cheap.
                ping = await ConnectionHandler.GetRealPingTime(proxy, 8);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TelemetryDashboard.Ping", ex);
                ping = -1;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            var result = ping;
            RxSchedulers.MainThreadScheduler.Schedule(() => RecordPing(result));
        }
        finally
        {
            _pingGate.Release();
        }
    }

    private void RecordPing(int ping)
    {
        _pingTotal++;
        if (ping <= 0)
        {
            _pingMisses++;
            PushSample(_pingSamples, double.NaN);
            PingValue = -1;
            PingText = "--";
            PingQuality = "zaman aşımı";
        }
        else
        {
            PingValue = ping;
            PingText = ping.ToString();
            PingQuality = ping switch
            {
                <= 10 => "mükemmel",
                <= 30 => "iyi",
                <= 60 => "orta",
                _ => "yavaş",
            };
            PushSample(_pingSamples, ping);
        }

        UpdateLoss();
        RaiseChanged();
    }

    private void OnStatistics(ServerSpeedItem update)
    {
        if (update is null)
        {
            return;
        }

        // StatisticsXray/Singbox normalize their byte counters to KB/s before
        // publishing ServerSpeedItem; convert that shared unit to Mbps for the UI.
        var downMbps = update.ProxyDown * KilobytesPerSecondToMegabitsPerSecond;
        var upMbps = update.ProxyUp * KilobytesPerSecondToMegabitsPerSecond;

        DownValue = Math.Max(0, downMbps);
        UpValue = Math.Max(0, upMbps);
        DownText = DownValue.ToString("0.0");
        UpText = UpValue.ToString("0.0");
        DownGaugeMax = CalibrateGauge(DownGaugeMax, DownValue, 25);
        UpGaugeMax = CalibrateGauge(UpGaugeMax, UpValue, 12);
        DownFraction = Math.Min(1.0, DownValue / DownGaugeMax);
        UpFraction = Math.Min(1.0, UpValue / UpGaugeMax);
        PushSample(_downSamples, DownValue);
        PushSample(_upSamples, UpValue);
        RaiseChanged();
    }

    private void UpdateLoss()
    {
        if (_pingTotal <= 0)
        {
            LossValue = -1;
            LossText = "--";
            LossQuality = "kararlı";
            return;
        }
        var loss = _pingMisses * 100.0 / _pingTotal;
        LossValue = loss;
        LossText = loss.ToString("0.0");
        LossQuality = loss switch
        {
            0 => "kararlı",
            < 5 => "düşük",
            < 20 => "dikkat",
            _ => "yüksek",
        };
    }

    private void ApplyConnectionState(bool connected)
    {
        if (connected == IsConnected)
        {
            return;
        }
        IsConnected = connected;
        StatusText = connected ? "BAĞLI" : "ÇEVRİMDIŞI";

        if (!connected)
        {
            // Fresh session look: reset the strip to the design's idle state.
            PingValue = -1;
            PingText = "--";
            PingQuality = "ölçülüyor…";
            LossValue = -1;
            LossText = "--";
            LossQuality = "kararlı";
            DownValue = 0;
            UpValue = 0;
            DownText = "0.0";
            UpText = "0.0";
            DownFraction = 0;
            UpFraction = 0;
            DownGaugeMax = 25;
            UpGaugeMax = 12;
            _pingTotal = 0;
            _pingMisses = 0;
            lock (_lock)
            {
                _pingSamples.Clear();
                _downSamples.Clear();
                _upSamples.Clear();
            }
            RaiseChanged();
        }
    }

    private void PushSample(List<double> samples, double value)
    {
        lock (_lock)
        {
            samples.Add(value);
            if (samples.Count > MaxSamples)
            {
                samples.RemoveAt(0);
            }
        }
    }

    private void RaiseChanged()
    {
        Changed?.Invoke();
    }
}
