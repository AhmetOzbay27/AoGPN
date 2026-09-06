namespace ServiceLib.Tests;

public class CoreRestartPolicyTests
{
    [Fact]
    public void DelayUsesCappedExponentialBackoff_WithImmediateFirstAttempt()
    {
        var policy = new CoreRestartPolicy(3, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(25));

        // İLK kurtarma denemesi gecikmesizdir (oturum sürekliliği — pencere
        // TCP retransmission zamanlayıcılarından önce kapanır); üstel geri çekilme
        // yalnızca TEKRARLANAN çökmelerde işler.
        Assert.Equal(TimeSpan.Zero, policy.GetDelay(0));
        Assert.Equal(TimeSpan.Zero, policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(20), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromMilliseconds(25), policy.GetDelay(4));
    }

    [Fact]
    public void AttemptsAreLimited()
    {
        var policy = new CoreRestartPolicy(maxAttempts: 2);

        Assert.True(policy.CanRestart(1));
        Assert.True(policy.CanRestart(2));
        Assert.False(policy.CanRestart(3));
    }

    [Fact]
    public void DisabledPolicyNeverRestarts()
    {
        var policy = CoreRestartPolicy.Disabled();

        Assert.True(policy.IsDisabled);
        Assert.Equal(0, policy.MaxAttempts);
        Assert.False(policy.CanRestart(1));
        Assert.False(policy.CanRestart(int.MaxValue));
    }

    [Fact]
    public void FromConfig_Enabled_UsesConfiguredAttemptCountClamped()
    {
        Assert.Equal(3, CoreRestartPolicy.FromConfig(enabled: true, configuredMaxAttempts: 3).MaxAttempts);
        // ConfigHandler zaten 1..10'a kırpar; yine de savunmacı clamp olsun.
        Assert.Equal(10, CoreRestartPolicy.FromConfig(enabled: true, configuredMaxAttempts: 999).MaxAttempts);
        Assert.Equal(1, CoreRestartPolicy.FromConfig(enabled: true, configuredMaxAttempts: 0).MaxAttempts);
    }

    [Fact]
    public void FromConfig_Disabled_AlwaysDisables()
    {
        var policy = CoreRestartPolicy.FromConfig(enabled: false, configuredMaxAttempts: 5);

        Assert.True(policy.IsDisabled);
        Assert.False(policy.CanRestart(1));
    }
}
