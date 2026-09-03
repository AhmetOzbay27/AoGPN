namespace ServiceLib.Services;

public static class CoreStartupDiagnostics
{
    public static CoreStartupErrorCode Classify(
        CoreStartupStage stage,
        Exception? exception = null,
        string? output = null,
        bool processExited = false)
    {
        var text = $"{exception?.Message} {output}".ToLowerInvariant();

        if (stage == CoreStartupStage.ResolveBinary)
        {
            return CoreStartupErrorCode.BinaryNotFound;
        }
        if (stage == CoreStartupStage.ValidateConfig)
        {
            return CoreStartupErrorCode.ConfigInvalid;
        }
        if (stage == CoreStartupStage.Elevation)
        {
            return text.Contains("password") || text.Contains("permission") || text.Contains("sudo")
                ? CoreStartupErrorCode.ElevationFailed
                : CoreStartupErrorCode.ElevationRequired;
        }
        if (stage == CoreStartupStage.WaitForProxy)
        {
            return text.Contains("port") ? CoreStartupErrorCode.PortUnavailable : CoreStartupErrorCode.SocksReadinessTimeout;
        }
        if (stage == CoreStartupStage.StartHelper)
        {
            return CoreStartupErrorCode.HelperStartFailed;
        }
        if (processExited)
        {
            return CoreStartupErrorCode.ProcessExited;
        }
        if (exception != null)
        {
            return exception switch
            {
                SocketException => CoreStartupErrorCode.PortUnavailable,
                UnauthorizedAccessException => CoreStartupErrorCode.ElevationRequired,
                System.ComponentModel.Win32Exception => CoreStartupErrorCode.ProcessStartFailed,
                _ => CoreStartupErrorCode.ProcessStartFailed
            };
        }

        return CoreStartupErrorCode.Unknown;
    }

    public static CoreStartupDiagnostic Create(
        CoreHealthRole role,
        ECoreType? coreType,
        CoreStartupStage stage,
        string message,
        Exception? exception = null,
        string? output = null,
        int? port = null,
        bool processExited = false,
        bool canRecover = false)
    {
        return new CoreStartupDiagnostic(
            role,
            coreType,
            stage,
            Classify(stage, exception, output, processExited),
            message,
            exception?.ToString() ?? output,
            port,
            canRecover);
    }
}
