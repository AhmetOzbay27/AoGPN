using ServiceLib.Models.Configs;

namespace ServiceLib.Services;

/// <summary>
/// Coordinates the window's tray-hide vs real-exit behaviour so the proxy-only
/// invariant cannot regress: hiding the window (minimize-to-tray or close-to-tray)
/// must never stop the running core or clear the OS proxy — only a real exit may.
///
/// MainWindow routes every hide decision through this guard. The lifecycle seams
/// (stop core, clear proxy, flush, shutdown) are injected so the exit path can be
/// exercised in unit tests, and so a test can prove the hide paths never touch them.
/// </summary>
public sealed class TrayWindowCoordinator
{
    private readonly Action _hideWindow;
    private readonly Action _showTrayHint;
    private readonly Func<Task> _stopCoreAsync;
    private readonly Func<Task> _clearProxyAsync;
    private readonly Func<Task> _flushAsync;
    private readonly Action _shutdown;
    private readonly Action _forceExit;
    private bool _minimizeHintShown;
    private int _exitStarted;

    /// <summary>
    /// Per-step timeout for the exit path. A stuck lifecycle step (proxy gate,
    /// config-flush signal, core-stop gate) previously blocked shutdown forever
    /// — observed live as a ~2 minute frozen exit. Each step is now bounded so
    /// shutdown always reaches the finally block. Test seam: lowered in unit
    /// tests to prove a hanging step still shuts down.
    /// </summary>
    internal static TimeSpan ExitStepTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whole-exit deadline for the watchdog. Each step is bounded individually
    /// (<see cref="ExitStepTimeout"/>), but a step — or the window teardown that
    /// follows the close — that blocks the UI thread *synchronously* never
    /// reaches its bounded await, so `Application.Shutdown` may never run and
    /// the process would linger invisible in Task Manager forever. After this
    /// overall deadline a worker-thread watchdog sweeps app-owned cores and
    /// force-terminates the process. Must exceed three hanging steps
    /// (<c>ExitStepTimeout * 3</c>) so async-only hangs finish normally. Test
    /// seam: shrunk in unit tests.
    /// </summary>
    internal static TimeSpan OverallExitTimeout { get; set; } = TimeSpan.FromSeconds(70);

    public TrayWindowCoordinator(
        Action hideWindow,
        Action showTrayHint,
        Func<Task> stopCoreAsync,
        Func<Task> clearProxyAsync,
        Func<Task> flushAsync,
        Action shutdown,
        Action? forceExit = null)
    {
        _hideWindow = hideWindow ?? throw new ArgumentNullException(nameof(hideWindow));
        _showTrayHint = showTrayHint ?? throw new ArgumentNullException(nameof(showTrayHint));
        _stopCoreAsync = stopCoreAsync ?? throw new ArgumentNullException(nameof(stopCoreAsync));
        _clearProxyAsync = clearProxyAsync ?? throw new ArgumentNullException(nameof(clearProxyAsync));
        _flushAsync = flushAsync ?? throw new ArgumentNullException(nameof(flushAsync));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _forceExit = forceExit ?? ForceExitProcess;
    }

    /// <summary>True once the real exit path has started (used to reject new connects).</summary>
    public bool IsExitInProgress => Volatile.Read(ref _exitStarted) != 0;

    /// <summary>True when a minimized window should hide to the tray.</summary>
    public bool ShouldHideOnMinimize(Config config) => config.UiItem.Minimize2Tray;

    /// <summary>True when a close request should hide to the tray instead of exiting.</summary>
    public bool ShouldHideOnClose(Config config) => config.UiItem.Hide2TrayWhenClose;

    /// <summary>
    /// Decides what a "toggle" request (tray left click, ShowForm hotkey — i.e. no
    /// explicit show/hide intent) should do, from the window's LIVE state rather
    /// than a cached "is visible" flag. The flag can desync from reality: when
    /// minimize-to-tray is off, minimizing leaves the window minimized but still in
    /// the taskbar with the flag still true, so a tray click toggled on the flag
    /// would HIDE it again instead of restoring it — the click appears dead and the
    /// window "never comes back". Deriving the toggle from the live state makes it
    /// self-healing: any minimized window (taskbar or tray) is restored, any fully
    /// visible window is hidden.
    /// </summary>
    /// <param name="isInTaskbar">Whether the window currently has a taskbar button.</param>
    /// <param name="isMinimized">Whether the window is currently minimized.</param>
    public static bool ShouldShowOnToggle(bool isInTaskbar, bool isMinimized)
        => !(isInTaskbar && !isMinimized);

    /// <summary>
    /// Handles the minimize-to-tray path. Hide-only by contract: it may hide the
    /// window (and show the one-time tray hint) but must never stop the core, clear
    /// the OS proxy, flush state or shut down.
    /// </summary>
    public void HandleMinimize(Config config)
    {
        if (!ShouldHideOnMinimize(config))
        {
            return;
        }

        // Hide FIRST: the tray hint is best-effort and must never prevent the hide.
        // Previously a throwing hint (e.g. "TrayIcon is not created" when the native
        // icon is not ready yet) propagated out of HandleMinimize and left the window
        // stranded minimized in the taskbar instead of hiding to the tray.
        _hideWindow();

        if (_minimizeHintShown)
        {
            return;
        }

        try
        {
            _showTrayHint();
            // Only mark the hint as shown after it actually succeeded, so a later
            // minimize can retry when the tray icon becomes available.
            _minimizeHintShown = true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Tray minimize hint failed", ex);
        }
    }

    /// <summary>
    /// Hides the window to the tray when close-to-tray is active. Hide-only by
    /// contract: the running core and the OS proxy are left untouched.
    /// </summary>
    public void CloseToTray()
    {
        _hideWindow();
    }

    /// <summary>
    /// The real exit path: clears the OS proxy, flushes pending state, stops the
    /// core and shuts the application down. This is the only place the tray flows
    /// may invoke the core/proxy lifecycle. Shutdown is guaranteed even when a
    /// lifecycle step throws, mirroring AppExitAsync's finally semantics.
    ///
    /// A watchdog arms the whole exit: if the process is still alive after
    /// <see cref="OverallExitTimeout"/> it sweeps app-owned core processes and
    /// force-terminates it. That covers a shutdown step or the window teardown
    /// wedging the UI thread synchronously (per-step timeouts cannot cover that)
    /// AND an `Application.Shutdown` that returns without the process actually
    /// terminating — observed live: `OnExit` ran, an Application.Exit handler
    /// threw, `Environment.Exit(0)` was skipped and the process lingered invisible
    /// with its timers still running. The watchdog is therefore deliberately
    /// NEVER canceled once armed: in the happy path the process is gone (via
    /// OnExit's guaranteed Environment.Exit) long before the deadline, and if it
    /// is not, the watchdog is the last line of defence.
    /// </summary>
    public async Task ExitApplicationAsync()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            return;
        }

        var watchdog = Task.Run(async () =>
        {
            await Task.Delay(OverallExitTimeout);
            Logging.SaveLog($"TrayWindowCoordinator exit watchdog fired after "
                + $"{OverallExitTimeout.TotalSeconds}s — forcing process termination");
            try
            {
                _forceExit();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TrayWindowCoordinator exit watchdog force-exit failed", ex);
            }
        });

        try
        {
            Logging.SaveLog("TrayWindowCoordinator ExitApplicationAsync Begin");
            await RunExitStepAsync("proxy cleanup", _clearProxyAsync);
            await RunExitStepAsync("state flush", _flushAsync);
            await RunExitStepAsync("core stop", _stopCoreAsync);
            Logging.SaveLog("TrayWindowCoordinator ExitApplicationAsync End");
        }
        finally
        {
            try
            {
                _shutdown();
            }
            catch (Exception ex)
            {
                // A throwing shutdown must never leave an invisible process behind.
                Logging.SaveLog("TrayWindowCoordinator shutdown failed", ex);
                ForceExitSafely();
            }
        }
    }

    /// <summary>Last-resort termination: sweep app-owned cores, then hard-exit.</summary>
    private static void ForceExitProcess()
    {
        try
        {
            // App-owned core processes (path-verified — foreign clients are never
            // touched) could otherwise outlive a wedged exit and keep holding the
            // local ports or the TUN adapter.
            CoreManager.KillOrphanCoreProcesses();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TrayWindowCoordinator force-exit core sweep failed", ex);
        }

        Logging.SaveLog("TrayWindowCoordinator forcing process exit");
        Environment.Exit(1);
    }

    private void ForceExitSafely()
    {
        try
        {
            _forceExit();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TrayWindowCoordinator force-exit failed", ex);
        }
    }

    private static async Task RunExitStepAsync(string name, Func<Task> step)
    {
        var sw = Stopwatch.StartNew();
        Logging.SaveLog($"Exit step '{name}' start");
        try
        {
            await step().WaitAsync(ExitStepTimeout);
            Logging.SaveLog($"Exit step '{name}' done in {sw.ElapsedMilliseconds} ms");
        }
        catch (TimeoutException)
        {
            Logging.SaveLog($"Exit step '{name}' TIMED OUT after {ExitStepTimeout.TotalSeconds}s — "
                + "shutdown continues without it.");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TrayWindowCoordinator {name} failed", ex);
        }
    }
}
