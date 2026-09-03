using System;
using System.Windows;
using System.Windows.Media.Animation;
using ServiceLib.Common;

namespace AoGPN.Views;

/// <summary>
/// Start-up splash window that shows the AO GPN logo while the app boots.
/// The parent (App.OnStartup) reports boot progress via <see cref="SetStage"/> and
/// calls <see cref="FadeOut"/> when the main window is about to render so the
/// splash dissolves smoothly instead of vanishing.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>
    /// Boot is so fast that without a floor the splash would only flash for a few
    /// milliseconds. We hold it for at least this long so the logo, version and
    /// loading bar are actually seen before the fade-out begins.
    /// </summary>
    private static readonly TimeSpan MinDisplayDuration = TimeSpan.FromSeconds(2.2);

    private DateTime _shownAt;
    private bool _closing;

    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"V{Utils.GetVersionInfo()}";
        Loaded += (_, _) => _shownAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates the boot status message and loading bar. Called while the UI thread is
    /// busy with start-up work, so it pumps a background-priority dispatcher frame to
    /// let the newly set values actually repaint before the next synchronous step.
    /// </summary>
    public void SetStage(string? status, double progress)
    {
        if (status is not null)
        {
            StatusText.Text = status;
        }

        BootProgress.Value = Math.Clamp(progress, BootProgress.Minimum, BootProgress.Maximum);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Fades the splash away and closes it. Safe to call twice — the second call
    /// (e.g. an extra guard on a subsequent code path) is ignored. If the minimum
    /// display time has not yet elapsed, the splash holds (bar at 100%) for the
    /// remaining time so it isn't visibly skipped, then fades out.
    /// </summary>
    public void FadeOut()
    {
        if (_closing)
        {
            return;
        }
        _closing = true;

        var elapsed = DateTime.UtcNow - _shownAt;
        var hold = MinDisplayDuration - elapsed;

        if (hold <= TimeSpan.Zero)
        {
            AnimateFadeOut();
            return;
        }

        // Hold the completed bar in view, then start the fade on the UI thread.
        var wait = new DispatcherTimer { Interval = hold };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (IsLoaded)
            {
                AnimateFadeOut();
            }
            else
            {
                Close();
            }
        };
        wait.Start();
    }

    private void AnimateFadeOut()
    {
        if (!IsLoaded)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}