namespace ServiceLib.Tests;

public class CoreConfigValidatorTests
{
    [Theory]
    [InlineData(ECoreType.Xray, "run -test -config")]
    [InlineData(ECoreType.v2fly_v5, "run -test -c")]
    [InlineData(ECoreType.mihomo, "-t -f")]
    public void SupportedCoresHaveValidationArguments(ECoreType coreType, string expectedPrefix)
    {
        var arguments = CoreConfigValidator.GetArguments(coreType, "config.json");

        Assert.NotNull(arguments);
        Assert.StartsWith(expectedPrefix, arguments);
    }

    [Theory]
    [InlineData(ECoreType.v2fly)]
    [InlineData(ECoreType.hysteria2)]
    [InlineData(ECoreType.naiveproxy)]
    public void UnsupportedCoresKeepExistingLaunchBehavior(ECoreType coreType)
    {
        Assert.Null(CoreConfigValidator.GetArguments(coreType, "config.json"));
    }
}
