namespace ServiceLib.Models;

public enum CoreHealthState
{
    Stopped,
    Starting,
    Ready,
    Degraded,
    Failed
}

public enum CoreHealthRole
{
    Main,
    PreSocks
}

public sealed record CoreHealthSnapshot
{
    public CoreHealthRole Role { get; init; }
    public CoreHealthState State { get; init; }
    public ECoreType? CoreType { get; init; }
    public int? Port { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>
    /// True when this snapshot belongs to an automatic core-restart (crash recovery)
    /// window started by <c>CoreManager.RecoverMainCoreAsync</c>. Consumers publish
    /// the recovery UI state ("reconnecting") from the Degraded snapshot that carries
    /// it instead of flipping straight to a full disconnect on every crash.
    /// </summary>
    public bool Recovering { get; init; }

    /// <summary>Exit code of the core process that exited unexpectedly (if known).</summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Tail of the crashed core's stdout/stderr (newline separated), captured at the
    /// moment of the unexpected exit — surfaced on the failure card so a silent
    /// drop never looks random.
    /// </summary>
    public string? OutputTail { get; init; }

    public bool IsReady => State == CoreHealthState.Ready;

    public CoreHealthSnapshot(
        CoreHealthRole role,
        CoreHealthState state,
        ECoreType? coreType,
        int? port,
        string? error = null,
        DateTimeOffset? changedAt = null,
        bool recovering = false,
        int? exitCode = null,
        string? outputTail = null)
    {
        Role = role;
        State = state;
        CoreType = coreType;
        Port = port;
        Error = error;
        ChangedAt = changedAt ?? DateTimeOffset.UtcNow;
        Recovering = recovering;
        ExitCode = exitCode;
        OutputTail = outputTail;
    }

    public static CoreHealthSnapshot Stopped(CoreHealthRole role) =>
        new(role, CoreHealthState.Stopped, null, null);
}
