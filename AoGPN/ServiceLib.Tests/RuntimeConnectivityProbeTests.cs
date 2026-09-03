namespace ServiceLib.Tests;

public class RuntimeConnectivityProbeTests
{
    [Fact]
    public async Task TcpProbeAcceptsLocalListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        var result = await RuntimeConnectivityProbe.ProbeTcpAsync(
            IPAddress.Loopback.ToString(), port, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(RuntimeProbeKind.Tcp, result.Kind);
    }

    [Fact]
    public async Task SocksProbeRejectsProtocolMismatch()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = client.GetStream();
            var input = new byte[3];
            await stream.ReadExactlyAsync(input);
            await stream.WriteAsync(new byte[] { 4, 0 });
        }, TestContext.Current.CancellationToken);

        var result = await RuntimeConnectivityProbe.ProbeSocks5Async(
            IPAddress.Loopback.ToString(), port, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(RuntimeProbeErrorCode.ProtocolMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task DnsProbeRejectsInvalidHost()
    {
        var result = await RuntimeConnectivityProbe.ProbeDnsAsync(
            "invalid-host-that-should-not-resolve.invalid", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(result.ErrorCode, new[] { RuntimeProbeErrorCode.DnsFailed, RuntimeProbeErrorCode.Timeout });
    }
}
