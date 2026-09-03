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
    private bool _startupCompleted;
    private AoGPN.Views.SplashWindow? _splash;

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

        // Decide the WPF render mode for this session before the splash or any
        // other window is created: hardware by default, with automatic software
        // fallbacks (no GPU path, or a crash budget exceeded — see
        // HardwareAccelerationGuard). WebView2 is not affected.
        HardwareAccelerationGuard.ApplyOnStartup();

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

        // Show the boot splash over the (cold) config/reactive initialization below.
        // It deliberately skips headless/one-shot modes, and is faded out just
        // before the main window renders in the success path lower in this method.
        if (!rebootas)
        {
            _splash = new AoGPN.Views.SplashWindow();
            _splash.Show();
            _splash.SetStage("Başlatılıyor…", 10);
        }

        // Apply the configured UI font (config is loaded now; a static x:Static would
        // run too early, before the config exists, so the font is applied via a resource).
        Resources[MaterialDesignFonts.FontResourceKey] = MaterialDesignFonts.GetFont(AppManager.Instance.Config.UiItem.CurrentFontFamily);

        _splash?.SetStage("Arayüz yükleniyor…", 30);

        // Keep the font live-updating from any settings window (no restart needed).
        AppEvents.FontFamilyChanged.AsObservable().Subscribe(fontFamily =>
            Resources[MaterialDesignFonts.FontResourceKey] = MaterialDesignFonts.GetFont(fontFamily));

        AppManager.Instance.WindowDialog = new WindowDialog();

        _splash?.SetStage("Bileşenler başlatılıyor…", 45);

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
        }

        AppManager.Instance.InitComponents();

        _splash?.SetStage("Arayüz başlatılıyor…", 60);

        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWpf()
            .BuildApp();

        base.OnStartup(e);

        _splash?.SetStage("Hazırlanıyor…", 85);

        var mainWindowViewModel = new MainWindowViewModel();
        var viewFor = SimpleViewLocator.Instance.ResolveView(mainWindowViewModel);
        viewFor!.ViewModel = mainWindowViewModel;

        var mainWindow = (MainWindow)viewFor;

        // Complete the bar, fade the splash out while the window comes up, then close it.
        _splash?.SetStage("Hazır", 100);
        _splash?.FadeOut();
        mainWindow.Show();
        MainWindow = mainWindow;
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
            // Startup failed before the main window appeared. Drop the splash so it
            // can't cover the error dialog, then exit with a visible error instead of
            // running headless with no window (silent failure mode).
            if (_splash != null)
            {
                _splash.Close();
                _splash = null;
            }
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
