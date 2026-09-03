using System.Net.Sockets;
using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Sahte UDP probe soketi: GERÇEK ICMP Port Unreachable üretimine (Windows'ta hedef
/// IP başına hız sınırlı — rate-limit) bağımlılığı kaldırır. Windows bağlı UDP
/// soketinde ICMP Port Unreachable, SocketException.ConnectionReset olarak yüzeye
/// çıkar; bu sahte soket o yüzeyi doğrudan fırlatarak kapalı-port davranışını
/// (UdpProbeStatus.Blocked) ortamdan tamamen bağımsız, deterministik test eder.
/// </summary>
internal sealed class FakeIcmpUnreachableSocket : IUdpProbeSocket
{
    private readonly SocketError _error;

    public FakeIcmpUnreachableSocket(SocketError error) => _error = error;

    public void Connect(string host, int port)
    {
        // Bağlı soket kuruldu — ICMP yüzeylemesi ReceiveAsync'ta gelir.
    }

    public ValueTask<int> SendAsync(byte[] payload, CancellationToken cancellationToken)
        => ValueTask.FromResult(payload.Length);

    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        => throw new SocketException((int)_error);

    public void Dispose()
    {
    }
}
