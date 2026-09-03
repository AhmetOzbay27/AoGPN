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

    public TimeSpan GetDelay(int attempt)
    {
        if (attempt <= 0)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Min(attempt - 1, 30));
        var milliseconds = Math.Min(BaseDelay.TotalMilliseconds * multiplier, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public bool CanRestart(int attempt) => attempt <= MaxAttempts;
}
