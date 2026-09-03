namespace ServiceLib.Models;

public enum ConnectionState
{
    Disconnected,
    Preparing,
    StartingCore,
    ApplyingRoutes,
    ApplyingProxy,
    Verifying,
    Connected,
    Stopping,
    Failed,
}

public sealed record RuntimeSnapshot
{
    public long Sequence { get; init; }
    public ConnectionState Connection { get; init; } = ConnectionState.Disconnected;
    public string? ActiveNodeId { get; init; }
    public string Mode { get; init; } = "off";
    public string Transport { get; init; } = "proxy";
    public bool SystemProxyEnabled { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
