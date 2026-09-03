using AwesomeAssertions;
using ServiceLib.Models.Configs;
using ServiceLib.Services;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Guards the tray-hide invariant: minimizing/close-to-tray must never stop the
/// running core or clear the OS proxy — only a real exit may. If a future refactor
/// routes a hide path through the core/proxy lifecycle, these tests fail.
/// </summary>
public sealed class TrayWindowCoordinatorTests
{
    // ---------------------------------------------------------------------
    // The proxy-only invariant: hide paths never touch the core or the proxy
    // ---------------------------------------------------------------------

    [Fact]
    public void MinimizeToTray_NeverStopsCoreOrClearsProxy()
    {
        var harness = new Harness();
        harness.Config.UiItem.Minimize2Tray = true;

        harness.Coordinator.HandleMinimize(harness.Config);

        harness.HideCalls.Should().Be(1);
        harness.HintCalls.Should().Be(1);
        harness.StopCalls.Should().Be(0, "minimizing to the tray must not stop the core");
        harness.ClearProxyCalls.Should().Be(0, "minimizing to the tray must not clear the OS proxy");
        harness.FlushCalls.Should().Be(0);
        harness.ShutdownCalls.Should().Be(0);
    }

    [Fact]
    public void MinimizeToTray_ShowsHintOnlyOncePerSession()
    {
        var harness = new Harness();
        harness.Config.UiItem.Minimize2Tray = true;

        harness.Coordinator.HandleMinimize(harness.Config);
        harness.Coordinator.HandleMinimize(harness.Config);

        harness.HideCalls.Should().Be(2);
        harness.HintCalls.Should().Be(1, "the tray hint is one-time per session");
        harness.StopCalls.Should().Be(0);
        harness.ClearProxyCalls.Should().Be(0);
    }

    [Fact]
    public void MinimizeToTray_WhenDisabled_DoesNothing()
    {
        var harness = new Harness();
        harness.Config.UiItem.Minimize2Tray = false;

        harness.Coordinator.HandleMinimize(harness.Config);

        harness.HideCalls.Should().Be(0);
        harness.HintCalls.Should().Be(0);
        harness.StopCalls.Should().Be(0);
        harness.ClearProxyCalls.Should().Be(0);
        harness.ShutdownCalls.Should().Be(0);
    }

    [Fact]
    public void MinimizeToTray_HintThrows_HideStillRunsAndHintRetries()
    {
        // Regression guard for "the app disappears but keeps running": a throwing
        // tray hint (H.NotifyIcon's "TrayIcon is not created" when the native icon
        // is not ready yet) used to propagate out of HandleMinimize BEFORE the
        // hide, leaving the window stranded minimized in the taskbar. The hide
        // must always run, and the one-time hint must be retried on a later
        // minimize once the icon exists (then marked shown).
        var harness = new Harness(hintFailures: 1);
        harness.Config.UiItem.Minimize2Tray = true;

        harness.Coordinator.HandleMinimize(harness.Config); // hint attempt 1: throws
        harness.Coordinator.HandleMinimize(harness.Config); // hint attempt 2: succeeds
        harness.Coordinator.HandleMinimize(harness.Config); // hint already shown

        harness.HideCalls.Should().Be(3, "the hide must run on every minimize regardless of the hint");
        harness.HintCalls.Should().Be(1, "a failed hint must be retried later, then marked shown");
        harness.StopCalls.Should().Be(0);
        harness.ClearProxyCalls.Should().Be(0);
        harness.ShutdownCalls.Should().Be(0);
    }

    [Fact]
    public void CloseToTray_HidesWindow_WithoutTouchingLifecycle()
    {
        var harness = new Harness();

        harness.Coordinator.CloseToTray();

        harness.HideCalls.Should().Be(1);
        harness.StopCalls.Should().Be(0, "close-to-tray must not stop the core");
        harness.ClearProxyCalls.Should().Be(0, "close-to-tray must not clear the OS proxy");
        harness.FlushCalls.Should().Be(0);
        harness.ShutdownCalls.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // The exit path is the only place the lifecycle may be touched
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExitApplication_StopsCoreAndClearsProxy_ThenShutsDown()
    {
        var harness = new Harness();

        await harness.Coordinator.ExitApplicationAsync();

        harness.ClearProxyCalls.Should().Be(1);
        harness.FlushCalls.Should().Be(1);
        harness.StopCalls.Should().Be(1);
        harness.ShutdownCalls.Should().Be(1);
        harness.HideCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExitApplication_IsIdempotent_WhenRequestedTwice()
    {
        var harness = new Harness();

        await Task.WhenAll(
            harness.Coordinator.ExitApplicationAsync(),
            harness.Coordinator.ExitApplicationAsync());

        harness.ClearProxyCalls.Should().Be(1);
        harness.FlushCalls.Should().Be(1);
        harness.StopCalls.Should().Be(1);
        harness.ShutdownCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExitApplication_ShutsDownEvenWhenLifecycleStepFails()
    {
        var harness = new Harness(failClearProxy: true);

        await harness.Coordinator.ExitApplicationAsync();

        harness.ShutdownCalls.Should().Be(1);
        harness.FlushCalls.Should().Be(1);
        harness.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExitApplication_HangingStep_TimesOutAndStillShutsDown()
    {
        // A stuck lifecycle step (proxy gate, flush signal, core-stop gate) must
        // not block shutdown forever — observed live as a ~2 minute frozen exit.
        // Each step is bounded by ExitStepTimeout and shutdown still runs.
        var stopCalls = 0;
        var harness = new Harness(stopCoreAsync: () =>
        {
            stopCalls++;
            return Task.Delay(TimeSpan.FromSeconds(60)); // never completes on its own
        });
        var old = TrayWindowCoordinator.ExitStepTimeout;
        TrayWindowCoordinator.ExitStepTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await harness.Coordinator.ExitApplicationAsync();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            TrayWindowCoordinator.ExitStepTimeout = old;
        }

        harness.ClearProxyCalls.Should().Be(1);
        harness.FlushCalls.Should().Be(1);
        stopCalls.Should().Be(1);
        harness.ShutdownCalls.Should().Be(1);
        harness.ForceExitCalls.Should().Be(0, "async-hanging steps are bounded per step, so the watchdog must not fire");
    }

    [Fact]
    public async Task ExitApplication_ShutdownThrows_StillForceExits()
    {
        // A throwing shutdown (Application.Shutdown failing on a wedged dispatcher)
        // must never leave an invisible process behind: the coordinator escalates
        // to the force-exit path and still terminates.
        var harness = new Harness(shutdown: () => throw new InvalidOperationException("shutdown failed"));

        await harness.Coordinator.ExitApplicationAsync();

        harness.ClearProxyCalls.Should().Be(1);
        harness.StopCalls.Should().Be(1);
        harness.ForceExitCalls.Should().Be(1, "a throwing shutdown must escalate to force-exit");
    }

    [Fact]
    public async Task ExitApplication_OverallDeadline_ForcesExitWhenExitStillRunning()
    {
        // Whole-exit watchdog: per-step timeouts cannot cover a step that wedges
        // the UI thread synchronously (it never reaches its bounded await), so the
        // coordinator also arms an overall deadline. When the graceful path is
        // still running after OverallExitTimeout the watchdog force-exits.
        var harness = new Harness(clearProxyAsync: () => Task.Delay(TimeSpan.FromSeconds(60)));
        var oldStep = TrayWindowCoordinator.ExitStepTimeout;
        var oldOverall = TrayWindowCoordinator.OverallExitTimeout;
        TrayWindowCoordinator.ExitStepTimeout = TimeSpan.FromMilliseconds(500);
        TrayWindowCoordinator.OverallExitTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await harness.Coordinator.ExitApplicationAsync();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            TrayWindowCoordinator.ExitStepTimeout = oldStep;
            TrayWindowCoordinator.OverallExitTimeout = oldOverall;
        }

        harness.ForceExitCalls.Should().Be(1, "exceeding the overall exit deadline must force-terminate");
        harness.ShutdownCalls.Should().Be(1, "shutdown still runs once the hanging steps time out");
    }

    [Fact]
    public async Task ExitApplication_WatchdogStaysArmed_WhenProcessSurvivesShutdown()
    {
        // Regression guard for the failure observed live on 2026-09-03: the exit
        // steps and OnExit all ran, but an Application.Exit event handler threw and
        // skipped OnExit's Environment.Exit(0), so the process kept running
        // invisibly with its timers alive. Application.Shutdown returned normally
        // (nothing for the coordinator to catch), and because the watchdog used to
        // be canceled right after _shutdown(), no backstop remained. The watchdog
        // must stay armed: if the process is somehow still alive at the deadline it
        // force-exits.
        var harness = new Harness();
        var old = TrayWindowCoordinator.OverallExitTimeout;
        TrayWindowCoordinator.OverallExitTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            await harness.Coordinator.ExitApplicationAsync();
            // Application.Shutdown returned without terminating (as in the bug) —
            // wait past the watchdog deadline and assert it still fires.
            await Task.Delay(700);
        }
        finally
        {
            TrayWindowCoordinator.OverallExitTimeout = old;
        }

        harness.ForceExitCalls.Should().Be(1, "the watchdog must remain armed after shutdown returns");
        harness.ShutdownCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------------
    // Decision helpers mirror the persisted settings
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldHideOnMinimize_MirrorsUiItem(bool setting, bool expected)
    {
        var harness = new Harness();
        harness.Config.UiItem.Minimize2Tray = setting;

        harness.Coordinator.ShouldHideOnMinimize(harness.Config).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldHideOnClose_MirrorsUiItem(bool setting, bool expected)
    {
        var harness = new Harness();
        harness.Config.UiItem.Hide2TrayWhenClose = setting;

        harness.Coordinator.ShouldHideOnClose(harness.Config).Should().Be(expected);
    }

    // ---------------------------------------------------------------------
    // Tray left-click toggle decides from the LIVE window state
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, false, "a fully visible window toggles to hidden")]
    [InlineData(false, true, true, "a tray-hidden window (minimized, no taskbar button) toggles to shown")]
    [InlineData(true, true, true, "a plain taskbar minimize (Minimize2Tray off) toggles to shown, never hides again")]
    public void ShouldShowOnToggle_UsesLiveWindowState(bool isInTaskbar, bool isMinimized, bool expected, string because)
    {
        TrayWindowCoordinator.ShouldShowOnToggle(isInTaskbar, isMinimized).Should().Be(expected, because);
    }

    // ---------------------------------------------------------------------
    // Tray double clicks must toggle exactly once
    // ---------------------------------------------------------------------

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private sealed class Harness
    {
        public Config Config { get; } = CoreConfigTestFactory.CreateConfig();

        public int HideCalls { get; private set; }
        public int HintCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int ClearProxyCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public int ShutdownCalls { get; private set; }
        public int ForceExitCalls { get; private set; }

        public TrayWindowCoordinator Coordinator { get; }

        public Harness(
            int hintFailures = 0,
            bool failClearProxy = false,
            Func<Task>? stopCoreAsync = null,
            Func<Task>? clearProxyAsync = null,
            Action? shutdown = null)
        {
            var hintAttempts = 0;
            Coordinator = new TrayWindowCoordinator(
                hideWindow: () => HideCalls++,
                showTrayHint: () =>
                {
                    hintAttempts++;
                    if (hintAttempts <= hintFailures)
                    {
                        throw new InvalidOperationException("TrayIcon is not created.");
                    }
                    HintCalls++;
                },
                stopCoreAsync: stopCoreAsync ?? (() =>
                {
                    StopCalls++;
                    return Task.CompletedTask;
                }),
                clearProxyAsync: clearProxyAsync ?? (() =>
                {
                    ClearProxyCalls++;
                    if (failClearProxy)
                    {
                        throw new InvalidOperationException("proxy cleanup failed");
                    }
                    return Task.CompletedTask;
                }),
                flushAsync: () =>
                {
                    FlushCalls++;
                    return Task.CompletedTask;
                },
                shutdown: shutdown ?? (() => ShutdownCalls++),
                forceExit: () => ForceExitCalls++);
        }
    }
}
