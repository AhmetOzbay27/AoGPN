namespace ServiceLib.Services;

public static class CoreHealthProbe
{
    public static bool IsProcessReady(ProcessService? process)
    {
        return process is { HasExited: false };
    }

    public static async Task<bool> WaitForSocks5Async(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;
        var greeting = new byte[] { 0x05, 0x01, 0x00 };
        var response = new byte[2];

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, token);
                await using var stream = client.GetStream();
                await stream.WriteAsync(greeting, token);
                var read = await stream.ReadAsync(response.AsMemory(0, response.Length), token);
                if (read == 2 && response[0] == 0x05 && response[1] == 0x00)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // The listener may still be starting; retry until the deadline.
            }
            catch (IOException)
            {
                // A listener that is not speaking SOCKS5 is not ready.
            }

            try
            {
                await Task.Delay(50, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }

        return false;
    }
}
