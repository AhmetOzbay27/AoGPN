namespace ServiceLib.Tests;

public class CoreHealthTests
{
    [Fact]
    public void StoppedSnapshotUsesExpectedDefaults()
    {
        var snapshot = CoreHealthSnapshot.Stopped(CoreHealthRole.Main);

        Assert.Equal(CoreHealthRole.Main, snapshot.Role);
        Assert.Equal(CoreHealthState.Stopped, snapshot.State);
        Assert.Null(snapshot.CoreType);
        Assert.Null(snapshot.Port);
        Assert.Null(snapshot.Error);
        Assert.False(snapshot.Recovering);
        Assert.Null(snapshot.ExitCode);
        Assert.Null(snapshot.OutputTail);
    }

    [Fact]
    public void AutoRecoverySnapshotCarriesRecoveringFlagAndCrashDetails()
    {
        // CoreManager otomatik kurtarma başlarken Degraded + recovering=true yayınlar;
        // terminal Failed ise çıkış kodunu ve stdout/stderr kuyruğunu taşır — UI bu
        // alanlardan "yeniden bağlanıyor" durumunu ve hata kartı ayrıntısını besler.
        var degraded = new CoreHealthSnapshot(
            CoreHealthRole.Main, CoreHealthState.Degraded, ECoreType.mihomo, 1080,
            "Core exited unexpectedly; restarting.", recovering: true);

        Assert.True(degraded.Recovering);
        Assert.False(degraded.IsReady);

        var failed = new CoreHealthSnapshot(
            CoreHealthRole.Main, CoreHealthState.Failed, ECoreType.mihomo, 1080,
            "recovery exhausted", exitCode: 5, outputTail: "fatal error line 1\nfatal error line 2");

        Assert.False(failed.Recovering);
        Assert.Equal(5, failed.ExitCode);
        Assert.Contains("fatal error line 2", failed.OutputTail);
    }

    [Fact]
    public async Task Socks5ProbeRejectsNonSocksListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = client.GetStream();
            var buffer = new byte[3];
            await stream.ReadExactlyAsync(buffer);
            await stream.WriteAsync(new byte[] { 1, 1 });
        }, TestContext.Current.CancellationToken);

        var ready = await CoreHealthProbe.WaitForSocks5Async(
            IPAddress.Loopback.ToString(), port, TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        Assert.False(ready);
    }

    [Fact]
    public async Task Socks5ProbeAcceptsNoAuthNegotiation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = client.GetStream();
            var buffer = new byte[3];
            await stream.ReadExactlyAsync(buffer);
            await stream.WriteAsync(new byte[] { 5, 0 });
        }, TestContext.Current.CancellationToken);

        var ready = await CoreHealthProbe.WaitForSocks5Async(
            IPAddress.Loopback.ToString(), port, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(ready);
    }
}
