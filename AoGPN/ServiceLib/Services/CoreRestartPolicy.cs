namespace ServiceLib.Services;

public sealed class CoreRestartPolicy
{
    public int MaxAttempts { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxDelay { get; }

    public CoreRestartPolicy(int maxAttempts = 3, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        MaxAttempts = Math.Max(0, maxAttempts);
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>True when automatic restarts are completely disabled (0 attempts).</summary>
    public bool IsDisabled => MaxAttempts <= 0;

    /// <summary>A policy that never restarts the core (auto-reconnect switched off).</summary>
    public static CoreRestartPolicy Disabled() => new(maxAttempts: 0);

    /// <summary>
    /// Builds the policy from the persisted user settings: enabled → the configured
    /// attempt count (clamped 1..10, ConfigHandler keeps it in that range already);
    /// disabled → <see cref="Disabled"/> so a core exit never spins an automatic
    /// restart loop the user explicitly turned off.
    /// </summary>
    public static CoreRestartPolicy FromConfig(bool enabled, int configuredMaxAttempts)
        => enabled ? new CoreRestartPolicy(Math.Clamp(configuredMaxAttempts, 1, 10)) : Disabled();

    /// <summary>
    /// Denemeler arası bekleme. İLK kurtarma denemesi (attempt 1) gecikmesizdir —
    /// oturum sürekliliği için pencere minimumda tutulur (TCP retransmission
    /// zamanlayıcıları tetiklenmeden yeniden başlama hedefi &lt;500 ms). Üstel
    /// geri çekilme yalnızca TEKRARLANAN çökmelerde işler (1× → 2× → 4× … base).
    /// </summary>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt <= 1)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Min(attempt - 2, 30));
        var milliseconds = Math.Min(BaseDelay.TotalMilliseconds * multiplier, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public bool CanRestart(int attempt) => attempt <= MaxAttempts;
}
