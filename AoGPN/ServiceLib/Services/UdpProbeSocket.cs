using System.Net.Sockets;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// UDP probe soket katmanı soyutlaması
//
// UdpHealthChecker ve WireGuardHandshakeProbe, "kapalı port" durumunu gerçek
// ICMP Port Unreachable'ın soket yüzeyine (Windows bağlı UDP soketlerinde
// SocketException.ConnectionReset) dayanarak algılar. Windows, ICMP üretimini
// hedef IP başına hız sınırlar (rate-limit) — bu yüzden gerçek ağ üzerindeki
// kapalı-port testleri ortama bağımlı ve kırılgandır.
//
// Bu soyutlama, probe sınıflarına bir enjeksiyon noktası verir: testler sahte
// ICMP kanıtı (ConnectionReset fırlatan sahte soket) enjekte ederek kapalı-port
// davranışını Windows ICMP rate-limit'inden tamamen bağımsız doğrular. Üretimde
// her zaman gerçek UdpClient uygulaması kullanılır (varsayılan fabrika).
// ─────────────────────────────────────────────────────────────────────────

/// <summary>Probe sınıflarının ihtiyaç duyduğu minimum UDP soket yüzeyi.</summary>
internal interface IUdpProbeSocket : IDisposable
{
    /// <summary>Bağlı UDP soketi açar — ICMP hatalarını SocketException olarak yüzeye çıkarır.</summary>
    void Connect(string host, int port);

    ValueTask<int> SendAsync(byte[] payload, CancellationToken cancellationToken);

    ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>IUdpProbeSocket'in gerçek UdpClient uygulaması (üretim varsayılanı).</summary>
internal sealed class UdpClientProbeSocket : IUdpProbeSocket
{
    private readonly UdpClient _udp;

    public UdpClientProbeSocket(AddressFamily family)
    {
        _udp = new UdpClient(family);
        // TUN (auto_route + strict_route) etkinken probe paketleri kendi tünelinin
        // İÇİNE yakalanır ve yanıltıcı "HandshakeNoResponse" üretir (failover'da
        // gereksiz sunucu değişimi). Soketi fiziksel uplink NIC'ine bağla (egress
        // pinleme) — paketler doğrudan fiziksel ağdan hedefe gider. Tünel etkin
        // değilse no-op (normal davranış, loopback testleri etkilenmez).
        ProbeEgressNic.BindSocketEgress(_udp.Client, family);
    }

    public void Connect(string host, int port) => _udp.Connect(host, port);

    public ValueTask<int> SendAsync(byte[] payload, CancellationToken cancellationToken)
        => _udp.SendAsync(payload, cancellationToken);

    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        => _udp.ReceiveAsync(cancellationToken);

    public void Dispose() => _udp.Dispose();
}

/// <summary>
/// Probe sınıflarının soket fabrikası — DI/test enjeksiyon noktası. Üretimde
/// <see cref="UdpClientProbeSocket"/> döner; testler sahte ICMP kanıtı üreten
/// sahte soketler verir.
/// </summary>
internal delegate IUdpProbeSocket UdpProbeSocketFactory(AddressFamily family);
