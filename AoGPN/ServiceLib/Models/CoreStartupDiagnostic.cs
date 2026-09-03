namespace ServiceLib.Models;

public enum CoreStartupStage
{
    ResolveBinary,
    ValidateConfig,
    StartProcess,
    WaitForProxy,
    StartHelper,
    Elevation,
    Unknown
}

public enum CoreStartupErrorCode
{
    None,
    BinaryNotFound,
    ConfigInvalid,
    ProcessStartFailed,
    ProcessExited,
    PortUnavailable,
    SocksReadinessTimeout,
    HelperStartFailed,
    ElevationRequired,
    ElevationFailed,
    Unknown
}

public sealed record CoreStartupDiagnostic
{
    public CoreHealthRole Role { get; init; }
    public ECoreType? CoreType { get; init; }
    public CoreStartupStage Stage { get; init; }
    public CoreStartupErrorCode Code { get; init; }
    public string Message { get; init; }
    public string? TechnicalDetails { get; init; }
    public int? Port { get; init; }
    public bool CanRecover { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public CoreStartupDiagnostic(
        CoreHealthRole role,
        ECoreType? coreType,
        CoreStartupStage stage,
        CoreStartupErrorCode code,
        string message,
        string? technicalDetails = null,
        int? port = null,
        bool canRecover = false,
        DateTimeOffset? createdAt = null)
    {
        Role = role;
        CoreType = coreType;
        Stage = stage;
        Code = code;
        Message = message;
        TechnicalDetails = technicalDetails;
        Port = port;
        CanRecover = canRecover;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }
}
