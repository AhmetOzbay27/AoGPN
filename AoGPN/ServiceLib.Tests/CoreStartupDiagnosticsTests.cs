namespace ServiceLib.Tests;

public class CoreStartupDiagnosticsTests
{
    [Fact]
    public void ValidationFailureIsClassifiedAsInvalidConfig()
    {
        var diagnostic = CoreStartupDiagnostics.Create(
            CoreHealthRole.Main,
            ECoreType.Xray,
            CoreStartupStage.ValidateConfig,
            "invalid",
            output: "unexpected field");

        Assert.Equal(CoreStartupErrorCode.ConfigInvalid, diagnostic.Code);
        Assert.Equal(CoreHealthRole.Main, diagnostic.Role);
        Assert.False(diagnostic.CanRecover);
    }

    [Fact]
    public void PermissionFailureIsClassifiedAsElevationFailure()
    {
        var code = CoreStartupDiagnostics.Classify(
            CoreStartupStage.Elevation,
            new UnauthorizedAccessException("permission denied"));

        Assert.Equal(CoreStartupErrorCode.ElevationFailed, code);
    }

    [Fact]
    public void ReadinessPortFailureIsClassifiedAsPortUnavailable()
    {
        var code = CoreStartupDiagnostics.Classify(
            CoreStartupStage.WaitForProxy,
            output: "address already in use: port 10808");

        Assert.Equal(CoreStartupErrorCode.PortUnavailable, code);
    }
}
