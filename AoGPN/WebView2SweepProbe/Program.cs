using System.Diagnostics;
using System.IO;

namespace WebView2SweepProbe;

// WebView2 GPU-process load probe for the AoGPN dashboard's connected sweep.
//
// Loads the shipped dashboard (vpn-gpn-dashboard.html — the exact document the
// app emits as index.html) in a real WebView2 (same SDK version and browser
// arguments as DashboardHost.cs), then measures the msedgewebview2 GPU process
// CPU while the connected radar sweep is toggled on/off:
//
//   sweep-disabled : idle — no body.connected        (baseline)
//   sweep-enabled  : body.connected                  (12 s ring spin + orbits + halo breathe)
//   sweep-frozen   : body.connected + reduce-effects (app kill switch — floor)
//
// Windows are interleaved (disabled, enabled, …) to cancel thermal/clock drift.
// GPU CPU is sampled natively every 500 ms (percent of one logical core); the
// DevTools SystemInfo.getProcessInfo cumulative CPU is reported as a
// cross-check; Performance.getMetrics frame counters are best-effort evidence.
//
// Usage: dotnet run --project AoGPN/WebView2SweepProbe [--html <path>]
//        [--window-ms 20000] [--settle-ms 4000] [--rounds 2] [--frozen-ms 15000]
//
// A visible window appears for the duration — it must stay on screen (the
// compositor idles when the window is fully occluded).

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            SelfTest();
            return;
        }

        if (args.Contains("--bordertest"))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new BorderTestForm());
            return;
        }

        if (args.Contains("--jscheck"))
        {
            var html = GetArg(args, "--html")
                ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "AoGPN", "bin", "Debug", "net10.0-windows10.0.19041.0", "index.html"));
            if (!File.Exists(html))
            {
                Console.Error.WriteLine($"index.html not found: {html} — pass --html <path>.");
                Environment.Exit(2);
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new JsCheckForm(html, args.Contains("--hidden-boot")));
            return;
        }

        var options = ProbeOptions.Parse(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new ProbeForm(options));
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

    /// <summary>
    /// Validates the cycle-based CpuSampler against the process's own
    /// TotalProcessorTime (cumulative CPU seconds — tick quantization cancels
    /// over a multi-second window). Both measure the same busy process, so the
    /// ambient machine load does not matter; the two readings must agree.
    /// </summary>
    private static void SelfTest()
    {
        Console.WriteLine($"self-test: comparing CpuSampler vs TotalProcessorTime on own pid {Environment.ProcessId}…");
        using var sampler = CpuSampler.Open(Environment.ProcessId);
        if (sampler is null)
        {
            Console.WriteLine("FAIL: could not open own process for sampling.");
            return;
        }

        using var workers = new ManualResetEventSlim(false);
        var spins = new[] { 0, 1 }.Select(_ => Task.Run(() =>
        {
            workers.Wait();
            var end = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < end)
            {
                // busy spin
            }
        })).ToArray();
        workers.Set();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var startCpu = Process.GetCurrentProcess().TotalProcessorTime;
        var readings = new List<double>();
        while (spins.Any(t => !t.IsCompleted))
        {
            Thread.Sleep(200);
            if (sampler.Sample() is { } value)
            {
                readings.Add(value);
            }
        }

        Task.WaitAll(spins);
        var endCpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wallSeconds = stopwatch.Elapsed.TotalSeconds;

        var samplerAvg = readings.Count > 0 ? readings.Average() : double.NaN;
        var referencePct = wallSeconds > 0 ? (endCpu - startCpu).TotalSeconds / wallSeconds * 100.0 : double.NaN;
        Console.WriteLine($"sampler avg      : {samplerAvg,6:F1}% of one core");
        Console.WriteLine($"reference (CPU s): {referencePct,6:F1}%  (2 spin threads; whatever fraction of cores");
        Console.WriteLine("                    they got under ambient load — both methods measure the same thing)");
        Console.WriteLine(
            double.IsNaN(samplerAvg) || double.IsNaN(referencePct) || Math.Abs(samplerAvg - referencePct) / referencePct > 0.15
                ? "MISMATCH — sampler vs reference differ by >15%; investigate."
                : "sampler agrees with the OS CPU-seconds reference (within 15%) — OK.");
    }
}