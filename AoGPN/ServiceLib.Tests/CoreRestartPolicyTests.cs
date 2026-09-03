namespace ServiceLib.Tests;

public class CoreRestartPolicyTests
{
    [Fact]
    public void DelayUsesCappedExponentialBackoff()
    {
        var policy = new CoreRestartPolicy(3, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(25));

        Assert.Equal(TimeSpan.Zero, policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(20), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(25), policy.GetDelay(3));
    }

    [Fact]
    public void AttemptsAreLimited()
    {
        var policy = new CoreRestartPolicy(maxAttempts: 2);

        Assert.True(policy.CanRestart(1));
        Assert.True(policy.CanRestart(2));
        Assert.False(policy.CanRestart(3));
    }
}
