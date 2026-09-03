using ServiceLib.UdpTest;

namespace ServiceLib.Services;

public static class RuntimeConnectivityProbe
{
    public static async Task<RuntimeConnectivityResult> ProbeTcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Tcp, $"{host}:{port}", RuntimeProbeErrorCode.InvalidTarget);
        }

        var target = $"{host}:{port}";
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return RuntimeConnectivityResult.Passed(RuntimeProbeKind.Tcp, target, timer.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Tcp, target, RuntimeProbeErrorCode.Timeout, duration: timer.Elapsed);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Tcp, target, RuntimeProbeErrorCode.ConnectionRefused, ex.Message, timer.Elapsed);
        }
        catch (Exception ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Tcp, target, RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
    }

    public static async Task<RuntimeConnectivityResult> ProbeUdpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Udp, $"{host}:{port}", RuntimeProbeErrorCode.InvalidTarget);
        }

        var target = $"{host}:{port}";
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = new UdpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var payload = Encoding.UTF8.GetBytes("aogpn-runtime-probe");
            var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);
            var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault();
            if (address == null)
            {
                return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Udp, target, RuntimeProbeErrorCode.DnsFailed, duration: timer.Elapsed);
            }
            await client.SendAsync(payload, new IPEndPoint(address, port), timeoutCts.Token);
            var result = await client.ReceiveAsync(timeoutCts.Token);
            return result.Buffer.SequenceEqual(payload)
                ? RuntimeConnectivityResult.Passed(RuntimeProbeKind.Udp, target, timer.Elapsed)
                : RuntimeConnectivityResult.Failed(RuntimeProbeKind.Udp, target, RuntimeProbeErrorCode.ProtocolMismatch, duration: timer.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Udp, target, RuntimeProbeErrorCode.Timeout, duration: timer.Elapsed);
        }
        catch (SocketException ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Udp, target, RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
        catch (Exception ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Udp, target, RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
    }

    public static async Task<RuntimeConnectivityResult> ProbeSocks5UdpAsync(
        string host,
        int socks5Port,
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var service = UdpTestService.CreateFromTarget(target, out var targetHost);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var elapsed = await service.SendUdpRequestAsync(targetHost, socks5Port, timeout);
            return RuntimeConnectivityResult.Passed(RuntimeProbeKind.Socks5Udp, $"{host}:{socks5Port}->{target}", elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5Udp, $"{host}:{socks5Port}->{target}", RuntimeProbeErrorCode.Timeout, duration: timer.Elapsed);
        }
        catch (Exception ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5Udp, $"{host}:{socks5Port}->{target}", RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
    }

    public static async Task<RuntimeConnectivityResult> ProbeAddressFamilyAsync(
        AddressFamily family,
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var kind = family == AddressFamily.InterNetwork ? RuntimeProbeKind.Ipv4 : RuntimeProbeKind.Ipv6;
        var target = $"{(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")}:{host}:{port}";
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(family);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return RuntimeConnectivityResult.Passed(kind, target, timer.Elapsed);
        }
        catch (ArgumentOutOfRangeException)
        {
            return RuntimeConnectivityResult.Failed(kind, target, RuntimeProbeErrorCode.InvalidTarget, duration: timer.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeConnectivityResult.Failed(kind, target, RuntimeProbeErrorCode.Timeout, duration: timer.Elapsed);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported)
        {
            return RuntimeConnectivityResult.Failed(kind, target, RuntimeProbeErrorCode.Unsupported, ex.Message, timer.Elapsed);
        }
        catch (Exception ex)
        {
            return RuntimeConnectivityResult.Failed(kind, target, RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
    }

    public static async Task<RuntimeConnectivityResult> ProbeSocks5Async(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var target = $"{host}:{port}";
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var token = timeoutCts.Token;
            await client.ConnectAsync(host, port, token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
            var response = new byte[2];
            await stream.ReadExactlyAsync(response, token);
            if (response[0] != 5)
            {
                return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5, target, RuntimeProbeErrorCode.ProtocolMismatch, duration: timer.Elapsed);
            }
            if (response[1] != 0)
            {
                return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5, target, RuntimeProbeErrorCode.AuthenticationFailed, duration: timer.Elapsed);
            }
            return RuntimeConnectivityResult.Passed(RuntimeProbeKind.Socks5, target, timer.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5, target, RuntimeProbeErrorCode.Timeout, duration: timer.Elapsed);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5, target, RuntimeProbeErrorCode.ConnectionRefused, ex.Message, timer.Elapsed);
        }
        catch (Exception ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5, target, RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
    }

    /// <summary>
    /// Performs a real SOCKS5 CONNECT through the local core. A listener-only probe
    /// cannot detect an upstream tunnel that is alive locally but has lost its remote
    /// path, so the recovery workflow uses this handshake as its health signal.
    /// </summary>
    public static async Task<RuntimeConnectivityResult> ProbeSocks5ConnectAsync(
        string host,
        int port,
        string targetHost,
        int targetPort,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var target = $"{host}:{port}->{targetHost}:{targetPort}";
        var timer = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(host)
            || port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(targetHost)
            || targetPort is < 1 or > 65535)
        {
            return RuntimeConnectivityResult.Failed(
                RuntimeProbeKind.Socks5,
                target,
                RuntimeProbeErrorCode.InvalidTarget,
                duration: timer.Elapsed);
        }

        var encodedHost = Encoding.ASCII.GetBytes(targetHost);
        if (encodedHost.Length is < 1 or > 255)
        {
            return RuntimeConnectivityResult.Failed(
                RuntimeProbeKind.Socks5,
                target,
                RuntimeProbeErrorCode.InvalidTarget,
                duration: timer.Elapsed);
        }

        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var token = timeoutCts.Token;
            await client.ConnectAsync(host, port, token);
            await using var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, token);
            if (greeting[0] != 5 || greeting[1] != 0)
            {
                return RuntimeConnectivityResult.Failed(
                    RuntimeProbeKind.Socks5,
                    target,
                    RuntimeProbeErrorCode.AuthenticationFailed,
                    "The local SOCKS5 listener did not accept unauthenticated health probes.",
                    timer.Elapsed);
            }

            var request = new byte[7 + encodedHost.Length];
            request[0] = 5;
            request[1] = 1; // CONNECT
            request[2] = 0;
            request[3] = 3; // domain-name address type
            request[4] = (byte)encodedHost.Length;
            encodedHost.CopyTo(request, 5);
            request[^2] = (byte)(targetPort >> 8);
            request[^1] = (byte)(targetPort & 0xff);
            await stream.WriteAsync(request, token);

            var response = new byte[4];
            await stream.ReadExactlyAsync(response, token);
            if (response[0] != 5)
            {
                return RuntimeConnectivityResult.Failed(
                    RuntimeProbeKind.Socks5,
                    target,
                    RuntimeProbeErrorCode.ProtocolMismatch,
                    duration: timer.Elapsed);
            }

            await ConsumeSocks5AddressAsync(stream, response[3], token);
            var responsePort = new byte[2];
            await stream.ReadExactlyAsync(responsePort, token);
            if (response[1] != 0)
            {
                return RuntimeConnectivityResult.Failed(
                    RuntimeProbeKind.Socks5,
                    target,
                    RuntimeProbeErrorCode.ConnectionRefused,
                    $"SOCKS5 CONNECT reply: {response[1]}",
                    timer.Elapsed);
            }

            return RuntimeConnectivityResult.Passed(RuntimeProbeKind.Socks5, target, timer.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5, target, RuntimeProbeErrorCode.Timeout, duration: timer.Elapsed);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5, target, RuntimeProbeErrorCode.ConnectionRefused, ex.Message, timer.Elapsed);
        }
        catch (Exception ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Socks5, target, RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
    }

    private static async Task ConsumeSocks5AddressAsync(NetworkStream stream, byte addressType, CancellationToken cancellationToken)
    {
        var length = addressType switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadByteAsync(stream, cancellationToken)),
            _ => throw new InvalidDataException($"Unknown SOCKS5 address type: {addressType}"),
        };

        var address = new byte[length];
        await stream.ReadExactlyAsync(address, cancellationToken);
    }

    private static async Task<byte> ReadByteAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await stream.ReadExactlyAsync(value, cancellationToken);
        return value[0];
    }

    public static async Task<RuntimeConnectivityResult> ProbeHttpProxyAsync(Uri proxy, Uri target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy(proxy),
                UseProxy = true,
                ConnectTimeout = timeout
            });
            using var request = new HttpRequestMessage(HttpMethod.Head, target);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            return RuntimeConnectivityResult.Passed(RuntimeProbeKind.HttpProxy, target.ToString(), timer.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.HttpProxy, target.ToString(), RuntimeProbeErrorCode.Timeout, duration: timer.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.HttpProxy, target.ToString(), RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
    }

    public static async Task<RuntimeConnectivityResult> ProbeDnsAsync(string host, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await Dns.GetHostAddressesAsync(host, timeoutCts.Token);
            return RuntimeConnectivityResult.Passed(RuntimeProbeKind.Dns, host, timer.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Dns, host, RuntimeProbeErrorCode.Timeout, duration: timer.Elapsed);
        }
        catch (SocketException ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Dns, host, RuntimeProbeErrorCode.DnsFailed, ex.Message, timer.Elapsed);
        }
        catch (Exception ex)
        {
            return RuntimeConnectivityResult.Failed(RuntimeProbeKind.Dns, host, RuntimeProbeErrorCode.Unknown, ex.Message, timer.Elapsed);
        }
    }
}
