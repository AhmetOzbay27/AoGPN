using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2SweepProbe;

internal enum SweepState
{
    /// <summary>Idle: no body.connected — the connected sweep is OFF (baseline).</summary>
    Disabled,

    /// <summary>body.connected — the slow radar sweep (ring 12s spin, orbits, halo breathe) is ON.</summary>
    Enabled,

    /// <summary>body.connected + body.reduce-effects — the app's kill switch freezes every animation (floor).</summary>
    Frozen,
}

internal sealed record ProbeOptions(
    string HtmlPath,
    int WindowMs,
    int SettleMs,
    int Rounds,
    int FrozenMs,
    bool Debug)
{
    public static ProbeOptions Parse(string[] args)
    {
        var html = GetArg(args, "--html")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "vpn-gpn-dashboard.html"));
        if (!File.Exists(html))
        {
            throw new FileNotFoundException("Dashboard HTML not found; pass --html <path>.", html);
        }

        return new ProbeOptions(
            html,
            GetIntArg(args, "--window-ms", 20_000),
            GetIntArg(args, "--settle-ms", 4_000),
            GetIntArg(args, "--rounds", 2),
            GetIntArg(args, "--frozen-ms", 15_000),
            args.Contains("--debug"));
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int GetIntArg(string[] args, string name, int fallback)
        => int.TryParse(GetArg(args, name), out var value) ? value : fallback;
}

internal sealed class ProbeForm : Form
{
    private const string HostName = "aogpn.local";

    private readonly ProbeOptions _opts;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly TaskCompletionSource<bool> _navigationTcs = new();
    private readonly List<WindowResult> _windows = [];

    private int _browserPid;
    private CpuSampler? _gpuSampler;
    private CpuSampler? _browserSampler;
    private ProcessGroupSampler? _rendererSampler;
    private ProcessGroupSampler? _utilitySampler;

    public ProbeForm(ProbeOptions opts)
    {
        _opts = opts;

        Text = "AoGPN sweep probe — leave visible while measuring GPU load";
        BackColor = Color.FromArgb(0x0B, 0x0F, 0x19);
        ClientSize = new Size(1024, 720);
        StartPosition = FormStartPosition.Manual;
        var workArea = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(workArea.Right - Width - 12, workArea.Bottom - Height - 12);
        TopMost = true;

        Controls.Add(_webView);
        _webView.DefaultBackgroundColor = Color.FromArgb(0x0B, 0x0F, 0x19);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            Console.WriteLine($"Loading {_opts.HtmlPath}");

            // Mirror the app's WebView2 environment (DashboardHost.cs): same
            // anti-throttling flags, own user-data folder so the app's profile
            // is untouched. The SDK is the same version the app pins.
            var envOptions = new CoreWebView2EnvironmentOptions(
                "--disable-backgrounding-occluded-windows "
                + "--disable-renderer-backgrounding "
                + "--disable-background-timer-throttling");
            var userDataFolder = Path.Combine(Path.GetTempPath(), "AoGPN-SweepProbe-WV2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);

            await _webView.EnsureCoreWebView2Async(environment);
            var core = _webView.CoreWebView2
                ?? throw new InvalidOperationException("CoreWebView2 was not created.");
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationCompleted += (_, e) => _navigationTcs.TrySetResult(e.IsSuccess);
            core.SetVirtualHostNameToFolderMapping(
                HostName,
                Path.GetDirectoryName(_opts.HtmlPath)!,
                CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate($"https://{HostName}/{Path.GetFileName(_opts.HtmlPath)}");

            if (!await _navigationTcs.Task.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("Dashboard navigation failed.");
            }

            _browserPid = (int)core.BrowserProcessId;
            await Task.Delay(5_000); // let the page boot and settle at idle

            PrintEnvironmentAsync(environment);

            // Classify this browser group's children (GPU / renderer / utility).
            BrowserProcessSet? processSet = null;
            for (var attempt = 0; attempt < 3 && processSet is null or { Any: false }; attempt++)
            {
                processSet = await Task.Run(() => GpuProcessLocator.Find(_browserPid, _opts.Debug));
                if (processSet.Any || attempt < 2)
                {
                    break;
                }

                await Task.Delay(2_000);
            }

            _gpuSampler = CpuSampler.Open(processSet?.GpuPid ?? 0);
            _browserSampler = CpuSampler.Open(_browserPid);
            _rendererSampler = ProcessGroupSampler.Open(processSet?.RendererPids ?? []);
            _utilitySampler = ProcessGroupSampler.Open(processSet?.UtilityPids ?? []);
            Console.WriteLine(processSet?.GpuPid > 0
                ? $"GPU process: pid {processSet.GpuPid} | renderers: {processSet!.RendererPids.Count} | utilities: {processSet.UtilityPids.Count} (sampled every 2 s)"
                : "WARNING: could not locate the GPU process of this browser group.");

            await core.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");

            // Warm both states once so shader compilation / first-frame costs
            // are paid before any measured window.
            await SetStateAsync(SweepState.Disabled);
            await Task.Delay(6_000);
            await SetStateAsync(SweepState.Enabled);
            await Task.Delay(6_000);
            await SetStateAsync(SweepState.Disabled);
            await Task.Delay(4_000);

            Console.WriteLine();
            Console.WriteLine("Measuring (interleaved to cancel drift)…");
            for (var round = 0; round < _opts.Rounds; round++)
            {
                await MeasureWindowAsync(SweepState.Disabled);
                await MeasureWindowAsync(SweepState.Enabled);
            }

            await MeasureWindowAsync(SweepState.Frozen);
            PrintSummary();
            Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: {ex}");
            Close();
        }
    }

    private async Task MeasureWindowAsync(SweepState state)
    {
        await SetStateAsync(state);
        await Task.Delay(_opts.SettleMs); // let the compositor settle into the state

        var anim = await GetAnimationStateAsync();
        var perfStart = await PerfMetricsAsync();

        _gpuSampler?.Sample();          // prime the samplers
        _browserSampler?.Sample();
        _rendererSampler?.Sample();
        _utilitySampler?.Sample();

        var gpuSamples = new List<double>();
        var browserSamples = new List<double>();
        var rendererSamples = new List<double>();
        var utilitySamples = new List<double>();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < _opts.WindowMs)
        {
            // 2 s cadence: GetProcessTimes tick quantization (~15.6 ms) then
            // averages out to well under half a percent per window.
            await Task.Delay(2_000);
            if (_gpuSampler?.Sample() is { } g)
            {
                gpuSamples.Add(g);
            }

            if (_browserSampler?.Sample() is { } b)
            {
                browserSamples.Add(b);
            }

            if (_rendererSampler?.Sample() is { } r)
            {
                rendererSamples.Add(r);
            }

            if (_utilitySampler?.Sample() is { } u)
            {
                utilitySamples.Add(u);
            }
        }

        var wallSeconds = stopwatch.Elapsed.TotalSeconds;
        var perfEnd = await PerfMetricsAsync();

        var result = new WindowResult(
            _windows.Count + 1,
            state,
            wallSeconds,
            Avg(gpuSamples),
            Max(gpuSamples),
            Avg(browserSamples),
            Avg(rendererSamples),
            Avg(utilitySamples),
            MetricDelta(perfStart, perfEnd, "FramesRendered"),
            MetricDelta(perfStart, perfEnd, "FramesDropped"),
            anim);
        _windows.Add(result);
        Console.WriteLine(result.ToRow());
    }

    private static double Avg(List<double> samples) => samples.Count > 0 ? samples.Average() : double.NaN;

    private static double Max(List<double> samples) => samples.Count > 0 ? samples.Max() : double.NaN;

    private async Task SetStateAsync(SweepState state)
    {
        var script = state switch
        {
            SweepState.Disabled =>
                "document.body.classList.remove('connected','effects-balanced','reduce-effects')",
            SweepState.Enabled =>
                "document.body.classList.add('connected'); document.body.classList.remove('effects-balanced','reduce-effects')",
            SweepState.Frozen =>
                "document.body.classList.add('connected','reduce-effects'); document.body.classList.remove('effects-balanced')",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

        await _webView.CoreWebView2!.ExecuteScriptAsync(script);
    }

    private async Task<string> GetAnimationStateAsync()
    {
        const string script = """
            (() => {
              const a = sel => {
                const el = document.querySelector(sel);
                if (!el) return 'missing';
                const cs = getComputedStyle(el);
                const n = cs.animationName;
                return n === 'none' ? 'none' : n + ' ' + cs.animationDuration + ' x' + cs.animationIterationCount;
              };
              return {
                connected: document.body.classList.contains('connected'),
                frozen: document.body.classList.contains('reduce-effects'),
                reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
                ringSvg: a('.ring-wrap .ring-svg'),
                orbit: a('.ring-orbit'),
                halo: a('.ring-halo')
              };
            })()
            """;
        var raw = await _webView.CoreWebView2!.ExecuteScriptAsync(script);
        try
        {
            var snapshot = JsonSerializer.Deserialize<AnimationSnapshot>(raw, JsonOpts);
            return snapshot?.ToString() ?? "anim state unreadable";
        }
        catch
        {
            return "anim state unreadable";
        }
    }

    private async Task<Dictionary<string, double>> PerfMetricsAsync()
    {
        try
        {
            var json = await _webView.CoreWebView2!.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}");
            var result = new Dictionary<string, double>();
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.GetProperty("metrics").EnumerateArray())
            {
                result[entry.GetProperty("name").GetString()!] = entry.GetProperty("value").GetDouble();
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, double>();
        }
    }

    private static double MetricDelta(Dictionary<string, double> start, Dictionary<string, double> end, string name)
    {
        if (!start.ContainsKey(name) || !end.ContainsKey(name))
        {
            return double.NaN;
        }

        return end[name] - start[name];
    }

    private void PrintEnvironmentAsync(CoreWebView2Environment environment)
    {
        Console.WriteLine($"WebView2 runtime : {environment.BrowserVersionString}");
        Console.WriteLine($"Logical cores    : {Environment.ProcessorCount}");
        Console.WriteLine($"OS               : {Environment.OSVersion.VersionString}");
        Console.WriteLine();
        Console.WriteLine("CPU % is percent of ONE logical core. FramesRendered is a renderer-side");
        Console.WriteLine("counter (transform-only compositor work may not increment it — GPU CPU is");
        Console.WriteLine("the load measure; frames are supporting evidence).");
        Console.WriteLine();
    }

    private void PrintSummary()
    {
        static string StateName(SweepState s) => s switch
        {
            SweepState.Disabled => "sweep-disabled",
            SweepState.Enabled => "sweep-enabled",
            SweepState.Frozen => "sweep-frozen",
            _ => "?",
        };

        Console.WriteLine();
        Console.WriteLine("=== SUMMARY ===");
        Console.WriteLine($"{"state",-16} {"n",2} {"gpu-avg%",8} {"gpu-peak%",9} {"browser%",8} {"renderer%",9} {"utility%",8} {"frames/s",8} {"dropped",7}");
        foreach (var state in new[] { SweepState.Disabled, SweepState.Enabled, SweepState.Frozen })
        {
            var ws = _windows.Where(w => w.State == state).ToList();
            if (ws.Count == 0)
            {
                continue;
            }

            var framesPerSec = ws.Where(w => !double.IsNaN(w.FramesRendered))
                .Select(w => w.FramesRendered / w.WallSeconds).DefaultIfEmpty(double.NaN).Average();
            Console.WriteLine(
                $"{StateName(state),-16} {ws.Count,2} " +
                $"{ws.Average(w => w.GpuAvg),8:F2} {ws.Max(w => w.GpuPeak),9:F2} " +
                $"{ws.Average(w => w.BrowserAvg),8:F2} {ws.Average(w => w.RendererAvg),9:F2} " +
                $"{ws.Average(w => w.UtilityAvg),8:F2} {framesPerSec,8:F1} " +
                $"{ws.Sum(w => Num(w.FramesDropped)),7:F0}");
        }

        var disabled = _windows.Where(w => w.State == SweepState.Disabled).Select(w => w.GpuAvg).ToList();
        var enabled = _windows.Where(w => w.State == SweepState.Enabled).Select(w => w.GpuAvg).ToList();
        var frozen = _windows.Where(w => w.State == SweepState.Frozen).Select(w => w.GpuAvg).ToList();

        if (disabled.Count > 0 && enabled.Count > 0)
        {
            var d = disabled.Average();
            var e = enabled.Average();
            var f = frozen.Count > 0 ? frozen.Average() : double.NaN;
            Console.WriteLine();
            Console.WriteLine($"GPU load: sweep OFF {d:F2}% -> ON {e:F2}% of one core (delta {e - d:+0.00;-0.00} pp).");
            if (!double.IsNaN(f))
            {
                Console.WriteLine($"Full freeze (reduce-effects) floor: {f:F2}% of one core.");
            }

            var cheap = e < 5.0 && e - d < 4.0;
            Console.WriteLine();
            Console.WriteLine(cheap
                ? "VERDICT: sweep-enabled GPU load stays in the single-digit percent of one core — the slow"
                  + " transform-only animations are cheap; they do NOT peg the WebView2 GPU process."
                : "VERDICT: sweep-enabled GPU load is NOT in the cheap single-digit range — investigate.");
        }

        Console.WriteLine();
        Console.WriteLine("=== JSON ===");
        var summary = new
        {
            machine = new
            {
                cores = Environment.ProcessorCount,
                os = Environment.OSVersion.VersionString,
                webview2 = _webView.CoreWebView2?.Environment?.BrowserVersionString,
            },
            states = new
            {
                disabled = StateSummary(disabled),
                enabled = StateSummary(enabled),
                frozen = StateSummary(frozen),
            },
            windows = _windows.Select(w => new
            {
                w.Index,
                state = StateName(w.State),
                wallSeconds = Math.Round(w.WallSeconds, 1),
                gpuAvgPct = Num(w.GpuAvg),
                gpuPeakPct = Num(w.GpuPeak),
                browserPct = Num(w.BrowserAvg),
                rendererPct = Num(w.RendererAvg),
                utilityPct = Num(w.UtilityAvg),
                framesRendered = Num(w.FramesRendered),
                framesDropped = Num(w.FramesDropped),
                w.AnimState,
            }),
        };
        Console.WriteLine(JsonSerializer.Serialize(summary));
        Console.WriteLine("=== END JSON ===");
    }

    private static double? Num(double value) => double.IsNaN(value) ? null : Math.Round(value, 2);

    private static object? StateSummary(List<double> gpuAvgs)
        => gpuAvgs.Count > 0
            ? new { n = gpuAvgs.Count, gpuAvgPct = Math.Round(gpuAvgs.Average(), 2) }
            : null;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record AnimationSnapshot(
        bool Connected,
        bool Frozen,
        bool ReducedMotion,
        string RingSvg,
        string Orbit,
        string Halo)
    {
        public override string ToString()
            => $"connected={Connected} frozen={Frozen} reducedMotion={ReducedMotion} | "
               + $"ringSvg={RingSvg} orbit={Orbit} halo={Halo}";
    }
}

internal sealed record WindowResult(
    int Index,
    SweepState State,
    double WallSeconds,
    double GpuAvg,
    double GpuPeak,
    double BrowserAvg,
    double RendererAvg,
    double UtilityAvg,
    double FramesRendered,
    double FramesDropped,
    string AnimState)
{
    public string ToRow()
    {
        var stateName = State switch
        {
            SweepState.Disabled => "sweep-disabled",
            SweepState.Enabled => "sweep-enabled",
            SweepState.Frozen => "sweep-frozen",
            _ => "?",
        };

        var frames = double.IsNaN(FramesRendered)
            ? "frames n/a    "
            : $"frames {FramesRendered / WallSeconds,4:F1}/s dropped {FramesDropped,3:F0}";

        return
            $"[{Index}] {stateName,-15} {WallSeconds,5:F1}s  " +
            $"gpu {GpuAvg,5:F2}% (peak {GpuPeak,5:F2}%)  " +
            $"browser {BrowserAvg,5:F2}% renderer {RendererAvg,5:F2}% utility {UtilityAvg,5:F2}%  " +
            $"{frames}  {AnimState}";
    }
}