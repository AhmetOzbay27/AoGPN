using AoGPN.Common;
using AoGPN.Converters;
using AoGPN.Manager;
using AoGPN.Views;
using ServiceLib.Services;

namespace AoGPN;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    public static EventWaitHandle ProgramStarted;

    /// <summary>
    /// The boot splash, shown before any heavy initialization and dismissed by
    /// <see cref="MainWindow.RevealStartupWindow"/> at the exact moment the
    /// dashboard has painted (see <see cref="SplashWindow"/>). Null after a
    /// successful startup handoff.
    /// </summary>
    public static SplashWindow? Splash { get; private set; }

    /// <summary>
    /// Process-boot anchor for the WEBVIEW_BOOT end-to-end timing (splash →
    /// first dashboard frame, see MainWindow.ProbeFirstFrameAsync). Initialized
    /// when the App type is first touched, i.e. at process start.
    /// </summary>
    public static readonly DateTime BootStartedAt = DateTime.UtcNow;

    private bool _startupCompleted;

    /// <summary>
    /// How long the OnExit dispatch (HWA disarm + Application.Exit event
    /// subscribers) may take before the process is force-terminated. Log traces
    /// (2026-09-03) showed OnExit being entered and then NOTHING happening for
    /// 70 s: an Application.Exit subscriber hung silently (no exception, no
    /// timeout) and the Environment.Exit(0) after it was never reached. The
    /// dispatch is therefore bounded — see <see cref="OnExit"/>.
    /// </summary>
    private static readonly TimeSpan OnExitDispatchTimeout = TimeSpan.FromSeconds(6);

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    /// <summary>
    /// Open only one process
    /// </summary>
    /// <param name="e"></param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        var exePathKey = Utils.GetMd5(Utils.GetExePath());

        var rebootas = e.Args.Any(t => t == Global.RebootAs);
        var updateCoresMode = e.Args.Any(t => t == Global.UpdateCoresMode);
        ProgramStarted = new EventWaitHandle(false, EventResetMode.AutoReset, exePathKey, out var bCreatedNew);
        if (!updateCoresMode && !rebootas && !bCreatedNew)
        {
            ProgramStarted.Set();
            Environment.Exit(0);
            return;
        }

        if (!AppManager.Instance.InitApp())
        {
            UI.Show($"Loading GUI configuration file is abnormal,please restart the application{Environment.NewLine}加载GUI配置文件异常,请重启应用");
            Environment.Exit(0);
            return;
        }

        // Decide the WPF render mode for this session before any window is
        // created — the guard's documented contract is "before any window is
        // shown", so it must run before the splash appears. Hardware by default,
        // with automatic software fallbacks (no GPU path, or a crash budget
        // exceeded — see HardwareAccelerationGuard). WebView2 is not affected.
        HardwareAccelerationGuard.ApplyOnStartup();

        // Apply the configured UI font (config is loaded now; a static x:Static would
        // run too early, before the config exists, so the font is applied via a resource).
        // Applied BEFORE the splash is created: the splash's window region is cut from
        // a snapshot of its tree, so the environment must be final before it renders.
        Resources[MaterialDesignFonts.FontResourceKey] = MaterialDesignFonts.GetFont(AppManager.Instance.Config.UiItem.CurrentFontFamily);

        // Startup splash: appears before InitComponents / WebView2 boot so a launch
        // never looks dead, and stays until MainWindow.RevealStartupWindow dismisses
        // it at the exact moment the dashboard has loaded and been seeded. The
        // loading bar is driven by the real stages below (SetProgress); the status
        // line itself is fixed (see SplashWindow).
        Splash = new SplashWindow();
        Splash.Show();
        Splash.SetProgress(10);
        DiagLog.Write("WEBVIEW_BOOT splash-shown");

        // Headless build-time mode: download/refresh the sing-box and Xray cores
        // into the bin folder next to this executable, then exit. Used by the
        // MSBuild DownloadCoreBinariesIntoReleaseOutput target so a fresh build
        // output always has runnable cores.
        if (updateCoresMode)
        {
            var exitCode = await RunUpdateCoresAsync();
            Environment.Exit(exitCode);
            return;
        }

        // Keep the font live-updating from any settings window (no restart needed).
        AppEvents.FontFamilyChanged.AsObservable().Subscribe(fontFamily =>
            Resources[MaterialDesignFonts.FontResourceKey] = MaterialDesignFonts.GetFont(fontFamily));

        AppManager.Instance.WindowDialog = new WindowDialog();

        // A previous run that was killed before its exit cleanup ran (crash,
        // Task Manager, hard shutdown) can leave its core processes behind
        // holding the local ports/TUN from the dead session. Sweep app-owned
        // xray/sing-box/mihomo on startup — the executable-path check guarantees
        // third-party VPN clients are never touched (see
        // CoreManager.KillOrphanCoreProcesses). Runs on a background thread so
        // boot is not delayed. Skipped in reboot-as-admin mode: the old instance
        // is still tearing down its own cores at that point and must not race it.
        if (!rebootas)
        {
            _ = Task.Run(CoreManager.KillOrphanCoreProcesses);

            // The same dead-session leftovers can leave Wintun adapters behind
            // (e.g. an IP-less "AoGPN-<server>" native adapter on 169.254.x.x).
            // The app is single-instance (see the EventWaitHandle above), so at
            // startup — before this instance opens its own tunnel — every
            // adapter under our name prefix belongs to a dead process and can be
            // removed safely (WintunOpenAdapter + CloseAdapter removes the PnP
            // device). Best-effort: without admin rights removal is skipped with
            // a log entry. Skipped in reboot-as-admin for the same reason as the
            // orphan-core sweep above.
            _ = Task.Run(async () =>
                await WintunOrphanSweeper.SweepOrphanedAsync(AppManager.Instance.Config?.GpnWintunItem));
        }

        AppManager.Instance.InitComponents();
        Splash?.SetProgress(30);
        DiagLog.Write("WEBVIEW_BOOT components-done");

        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWpf()
            .BuildApp();

        base.OnStartup(e);

        var mainWindowViewModel = new MainWindowViewModel();
        var viewFor = SimpleViewLocator.Instance.ResolveView(mainWindowViewModel);
        viewFor!.ViewModel = mainWindowViewModel;

        var mainWindow = (MainWindow)viewFor;

        // Show the window PARKED OFF-SCREEN instead of invisible: WebView2 needs
        // the window created (its Loaded handler initializes the dashboard), but
        // painting the raw frame first would flash an empty black window while the
        // dashboard boots. The old "Opacity 0" trick must NOT be used: WPF window
        // opacity below 1 requires a layered window, and layered windows paint as
        // a solid black RECTANGLE on machines where compositing is broken (software
        // rendering, some drivers, remote sessions — the same failure the splash's
        // SetWindowRgn rewrite exists for). The maximized main window therefore
        // blackened the ENTIRE screen behind the splash for the whole boot. The
        // parked window still loads normally (Loaded fires, WebView2 boots) but no
        // pixel is ever painted; RevealStartupWindow moves it to its saved spot (or
        // maximizes it) in the same tick the dashboard becomes visible.
        mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        mainWindow.Left = -32000;
        mainWindow.Top = -32000;
        mainWindow.Show();
        MainWindow = mainWindow;
        Splash?.SetProgress(45);
        // Prove the park worked: if the window is NOT far off-screen / has been
        // pushed back onto a monitor (some drivers clamp), the boot would paint it
        // over the desktop behind the splash — the exact black screen this parking
        // exists to prevent. The log line below makes the next run self-verifying.
        DiagLog.Write($"WEBVIEW_BOOT window-shown state={mainWindow.WindowState} "
            + $"left={mainWindow.Left:0} top={mainWindow.Top:0} w={mainWindow.ActualWidth:0} h={mainWindow.ActualHeight:0}");
        _startupCompleted = true;
    }

    private static async Task<int> RunUpdateCoresAsync()
    {
        try
        {
            return await CoreInstaller.UpdateCoresAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("UpdateCores", ex);
            return 1;
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logging.SaveLog("App_DispatcherUnhandledException", e.Exception);
        if (!_startupCompleted)
        {
            // Startup failed before the main window appeared. Exit with a visible
            // error instead of running headless with no window (silent failure mode).
            // The topmost splash must not cover the error dialog, so close it first.
            Splash?.CloseNow();
            UI.Show($"Startup failed: {e.Exception.Message}{Environment.NewLine}Başlangıç başarısız: {e.Exception.Message}");
            Environment.Exit(1);
            return;
        }
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject != null)
        {
            Logging.SaveLog("CurrentDomain_UnhandledException", (Exception)e.ExceptionObject);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logging.SaveLog("TaskScheduler_UnobservedTaskException", e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logging.SaveLog("OnExit");

        // Insurance for a teardown that starts while the splash is still up (e.g.
        // the user closes the app mid-boot): never let the topmost splash outlive
        // the main window.
        try
        {
            Splash?.CloseNow();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SplashWindow.CloseNow failed during exit", ex);
        }

        // The whole dispatch below is bounded by an escalator. A subscriber that
        // THROWS is caught and logged, but log traces (2026-09-03) showed a
        // subscriber that HANGS silently — no exception, no timeout — blocking
        // the Environment.Exit(0) below forever while the process kept running
        // headless (its timers even fired for another 70 s until the exit
        // watchdog killed it). If the dispatch has not finished within
        // OnExitDispatchTimeout, a worker thread force-terminates the process.
        using var hangCts = new CancellationTokenSource();
        var hangEscalator = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(OnExitDispatchTimeout, hangCts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // dispatch finished in time
            }

            Logging.SaveLog($"OnExit dispatch did not finish in "
                + $"{OnExitDispatchTimeout.TotalSeconds}s — forcing process exit");
            Environment.Exit(1);
        });

        try
        {
            try
            {
                // Disarm the HWA crash guard so a normal exit never counts as an
                // abnormal (crashed) session; flushed synchronously because the
                // Environment.Exit below tears the process down right away.
                HardwareAccelerationGuard.OnGracefulExit();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("HardwareAccelerationGuard.OnGracefulExit failed", ex);
            }

            try
            {
                base.OnExit(e);
            }
            catch (Exception ex)
            {
                // A throwing Application.Exit subscriber (e.g. the tray library's own
                // exit hook) must never skip the guaranteed termination below —
                // observed live: OnExit logged, an Exit handler threw, the
                // Environment.Exit(0) was never reached and the process lingered
                // invisible in Task Manager with its timers still running.
                Logging.SaveLog("Application.Exit event handler threw", ex);
            }
        }
        finally
        {
            hangCts.Cancel();
        }

        // Exit normally with code 0. Everything owned is already torn down by the
        // time we get here (AppExitAsync stops the core and flushes config/DB,
        // MainWindow_Closed disposes the WebView2 dashboard, the tray icon is
        // disposed in its exit handler). Process.Kill() made every shutdown look
        // like a crash in the debugger (exit code -1 / 0xffffffff) and could
        // leave the tray icon ghosted. Environment.Exit(0) keeps the guaranteed
        // termination (so a stray non-background library thread can never hang
        // shutdown) while reporting a clean exit code.
        Environment.Exit(0);
    }
}
