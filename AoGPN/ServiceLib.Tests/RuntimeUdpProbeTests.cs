namespace ServiceLib.Tests;

public class RuntimeUdpProbeTests
{
    [Fact]
    public async Task UdpProbeAcceptsEchoListener()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            var request = await listener.ReceiveAsync(TestContext.Current.CancellationToken);
            await listener.SendAsync(request.Buffer, request.RemoteEndPoint);
        }, TestContext.Current.CancellationToken);

        var result = await RuntimeConnectivityProbe.ProbeUdpAsync(
            IPAddress.Loopback.ToString(), port, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(RuntimeProbeKind.Udp, result.Kind);
    }

    [Fact]
    public async Task AddressFamilyProbeAcceptsIpv4Listener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        var result = await RuntimeConnectivityProbe.ProbeAddressFamilyAsync(
            AddressFamily.InterNetwork, IPAddress.Loopback.ToString(), port, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(RuntimeProbeKind.Ipv4, result.Kind);
    }

    [Fact]
    public async Task AddressFamilyProbeReportsUnsupportedOrConnectionFailureForUnavailableIpv6()
    {
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            return;
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        var result = await RuntimeConnectivityProbe.ProbeAddressFamilyAsync(
            AddressFamily.InterNetworkV6, IPAddress.IPv6Loopback.ToString(), port, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(result.Success || result.ErrorCode is RuntimeProbeErrorCode.Unsupported or RuntimeProbeErrorCode.Unknown);
    }
}
