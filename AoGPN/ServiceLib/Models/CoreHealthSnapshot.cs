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
    public bool IsReady => State == CoreHealthState.Ready;

    public CoreHealthSnapshot(
        CoreHealthRole role,
        CoreHealthState state,
        ECoreType? coreType,
        int? port,
        string? error = null,
        DateTimeOffset? changedAt = null)
    {
        Role = role;
        State = state;
        CoreType = coreType;
        Port = port;
        Error = error;
        ChangedAt = changedAt ?? DateTimeOffset.UtcNow;
    }

    public static CoreHealthSnapshot Stopped(CoreHealthRole role) =>
        new(role, CoreHealthState.Stopped, null, null);
}
