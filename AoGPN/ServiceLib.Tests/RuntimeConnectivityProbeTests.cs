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
        // ISP DNS yönlendirmesi ".invalid" alan adlarını bile çözebilir — bu yüzden
        // sistem çözümleyicisi yerine NXDOMAIN dönen yerel bir test DNS sunucusuna
        // sondaj yapılır; test makinenin ağ ortamından bağımsız hale gelir.
        using var dnsServer = new FakeDnsServer();

        var result = await RuntimeConnectivityProbe.ProbeDnsAsync(
            "invalid-host-that-should-not-resolve.invalid",
            TimeSpan.FromSeconds(1),
            dnsServer.Endpoint,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(result.ErrorCode, new[] { RuntimeProbeErrorCode.DnsFailed, RuntimeProbeErrorCode.Timeout });
    }

    [Fact]
    public async Task DnsProbeAcceptsLocalResolverAnswer()
    {
        using var dnsServer = new FakeDnsServer(answerWithRecord: true);

        var result = await RuntimeConnectivityProbe.ProbeDnsAsync(
            "resolvable-host.test",
            TimeSpan.FromSeconds(1),
            dnsServer.Endpoint,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(RuntimeProbeKind.Dns, result.Kind);
    }

    /// <summary>
    /// Yerel, deterministik bir UDP DNS test sunucusu: gelen her sorguya ya NXDOMAIN
    /// (varsayılan) ya da 127.0.0.1 A kaydıyla yanıt verir — makinenin gerçek
    /// çözümleyicisinden (ISP yönlendirmesi dahil) tamamen bağımsızdır.
    /// </summary>
    private sealed class FakeDnsServer : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _answerWithRecord;

        public FakeDnsServer(bool answerWithRecord = false)
        {
            _answerWithRecord = answerWithRecord;
            _ = Task.Run(ServerLoopAsync, _cts.Token);
        }

        public IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

        private async Task ServerLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var request = await _udp.ReceiveAsync(_cts.Token);
                    var response = BuildResponse(request.Buffer);
                    if (response is not null)
                    {
                        await _udp.SendAsync(response, request.RemoteEndPoint, _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private byte[]? BuildResponse(byte[] request)
        {
            if (request.Length < 12)
            {
                return null;
            }
            var response = request.ToArray();
            response[2] = 0x81;
            response[3] = _answerWithRecord ? (byte)0x80 : (byte)0x83;
            Array.Clear(response, 6, 6); // AN/NS/AR sayıları = 0
            if (!_answerWithRecord)
            {
                return response;
            }
            response[6] = 0;
            response[7] = 1; // ANCOUNT = 1
            byte[] answer =
            [
                0xC0, 0x0C,                 // ad işaretçisi → QNAME
                0x00, 0x01,                 // QTYPE = A
                0x00, 0x01,                 // QCLASS = IN
                0x00, 0x00, 0x00, 0x3C,     // TTL = 60
                0x00, 0x04,                 // RDLENGTH = 4
                127, 0, 0, 1,               // 127.0.0.1
            ];
            return [.. response, .. answer];
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
            _cts.Dispose();
        }
    }
}
