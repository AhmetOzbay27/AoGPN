using System.Windows.Media;

namespace AoGPN.Common;

/// <summary>
/// Keeps hardware acceleration on by default while watching for machines where
/// WPF's GPU path misbehaves. Three layered protections:
///   1. RenderCapability.Tier &lt; 2 at startup — no usable GPU path (basic
///      display adapter, some VMs): run software for the session and persist
///      the toggle off so the setting stays honest.
///   2. Crash guard — a session that ran armed (HwaSessionArmed) but ended
///      abnormally (the process died before OnExit cleared the flag) adds one
///      strike; after three strikes HWA is auto-disabled so a broken driver
///      cannot crash-loop the app. Re-enabling HWA in Settings resets the
///      counter and gives the driver a fresh chance.
///   3. The default render mode is left in place (never forced to
///      HardwareOnly), so WPF's own per-frame software fallback stays active
///      for transient GPU failures.
/// WebView2 is intentionally outside this guard: it manages its own GPU
/// fallback, and pushing it to software would burn CPU on the dashboard
/// effects work (blur removal, steps() quantization, effects tiers).
/// </summary>
public static class HardwareAccelerationGuard
{
    /// <summary>Abnormal sessions tolerated before HWA is auto-disabled.</summary>
    public const int HwaCrashStrikeLimit = 3;

    /// <summary>
    /// Must run on the UI thread before any window is shown, with the config
    /// already loaded (App.OnStartup). Decides the WPF render mode for the
    /// whole session and maintains the persisted crash-guard state.
    /// </summary>
    public static void ApplyOnStartup()
    {
        var gui = AppManager.Instance.Config.GuiItem;

        if (!gui.EnableHWA)
        {
            // User (or a previous auto-fallback) turned HWA off: stay software
            // and don't arm the guard for a session that never used the GPU.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            gui.HwaSessionArmed = false;
            return;
        }

        // Crash guard: the previous session was armed but never cleared the
        // flag on exit — count it and decide whether to keep trusting the GPU.
        if (gui.HwaSessionArmed)
        {
            gui.HwaCrashStrikes++;
            Logging.SaveLog(
                $"HardwareAccelerationGuard: previous HWA session ended abnormally " +
                $"(strike {gui.HwaCrashStrikes}/{HwaCrashStrikeLimit})");
            if (gui.HwaCrashStrikes >= HwaCrashStrikeLimit)
            {
                gui.EnableHWA = false;
                gui.HwaCrashStrikes = 0;
                gui.HwaAutoDisabledNotice = true;
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                Logging.SaveLog(
                    "HardwareAccelerationGuard: auto-disabled hardware acceleration " +
                    "after repeated abnormal sessions");
                return;
            }
        }

        // No hardware path at all (basic display adapter / broken driver / VM):
        // software now, persist, and don't arm the guard for a session that
        // never used the GPU anyway.
        if (RenderCapability.Tier < 2)
        {
            gui.EnableHWA = false;
            gui.HwaCrashStrikes = 0;
            gui.HwaAutoDisabledNotice = true;
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
            Logging.SaveLog(
                "HardwareAccelerationGuard: no hardware render path " +
                $"(Tier {RenderCapability.Tier}), fell back to software rendering");
            return;
        }

        // All clear: leave the default render mode (hardware with WPF's own
        // per-frame fallback still active) and arm the flag so an abnormal
        // death counts against the strike budget.
        gui.HwaSessionArmed = true;
        ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
    }

    /// <summary>
    /// Called on graceful shutdown so the armed flag is cleared before the
    /// process exits — otherwise every normal exit would look like a crash.
    /// Flushes synchronously because Environment.Exit follows immediately.
    /// </summary>
    public static void OnGracefulExit()
    {
        AppManager.Instance.Config.GuiItem.HwaSessionArmed = false;
        // Run the flush on a context-free worker thread. Sync-over-async on the
        // UI thread (GetResult) deadlocks whenever the write chain yields and its
        // continuation needs the UI dispatcher — observed live on 2026-09-03:
        // OnExit entered, the config flush never finished (no throw, no timeout
        // log), and only the exit watchdog's 70 s force-exit ended the process.
        // Task.Run gives the whole chain a null SynchronizationContext, so no
        // continuation can ever be stranded on the blocked UI thread.
        Task.Run(() => ConfigSaveQueue.SaveAndWaitAsync(AppManager.Instance.Config))
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Called when the user explicitly re-enables HWA in Settings: reset the
    /// crash counter so the driver gets a fresh chance instead of inheriting
    /// the auto-disabled state forever.
    /// </summary>
    public static void OnUserReEnabled()
    {
        AppManager.Instance.Config.GuiItem.HwaCrashStrikes = 0;
        AppManager.Instance.Config.GuiItem.HwaSessionArmed = false;
    }
}