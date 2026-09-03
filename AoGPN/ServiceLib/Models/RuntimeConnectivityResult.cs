namespace ServiceLib.Models;

public enum RuntimeProbeKind
{
    Tcp,
    Udp,
    Socks5,
    Socks5Udp,
    HttpProxy,
    Dns,
    Ipv4,
    Ipv6
}

public enum RuntimeProbeErrorCode
{
    None,
    InvalidTarget,
    ConnectionRefused,
    Timeout,
    ProtocolMismatch,
    AuthenticationFailed,
    DnsFailed,
    Unsupported,
    Unknown
}

public sealed record RuntimeConnectivityResult(
    RuntimeProbeKind Kind,
    bool Success,
    string Target,
    TimeSpan Duration,
    RuntimeProbeErrorCode ErrorCode = RuntimeProbeErrorCode.None,
    string? Details = null)
{
    public static RuntimeConnectivityResult Passed(RuntimeProbeKind kind, string target, TimeSpan duration) =>
        new(kind, true, target, duration);

    public static RuntimeConnectivityResult Failed(RuntimeProbeKind kind, string target, RuntimeProbeErrorCode code, string? details = null, TimeSpan? duration = null) =>
        new(kind, false, target, duration ?? TimeSpan.Zero, code, details);
}
