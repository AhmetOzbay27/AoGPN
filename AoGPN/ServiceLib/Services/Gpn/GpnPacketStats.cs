using System.Buffers.Binary;
using System.Net;

namespace ServiceLib.Services;

/// <summary>Yakalanan paketin taşıma protokolü (IP başlığından türetilir).</summary>
public enum GpnPacketProtocol
{
    /// <summary>Başlık çözülemedi / bozuk paket.</summary>
    Unknown,

    Udp,
    Tcp,
    Icmp,

    /// <summary>GRE, ESP vb. — port bilgisi yok.</summary>
    Other,
}

/// <summary>
/// GpnCaptureLoop telemetrisi için paket özeti. WinDivert ağ katmanı tam IP
/// paketi verir; başlık SAF yönetilen kodla çözülür (natif yardımcı çağrısı yok,
/// test edilebilir). Yön (outbound) adresten, protokol/portlar paketten gelir.
/// IPv6'da uzantı başlıkları (extension headers) takip EDİLMEZ — next header
/// doğrudan taşıma protokolü sayılır (oyun UDP'si için yeterli; karmaşık
/// yönlendirme başlıklı paketler Other olarak sınıflanır).
/// </summary>
public sealed record GpnPacketStats(
    GpnPacketProtocol Protocol,
    string PeerEndpoint,
    ushort LocalPort,
    int Length,
    bool IsIpv6,
    bool Outbound)
{
    /// <summary>Bozuk/çözülemeyen paket için güvenli boş değer (asla fırlatmaz).</summary>
    public static GpnPacketStats Unknown(ReadOnlySpan<byte> data)
        => new(GpnPacketProtocol.Unknown, string.Empty, 0, data.Length, false, false);

    /// <summary>
    /// Ham IP paketini çözer. <paramref name="outbound"/> doğruysa eş (peer)
    /// hedef IP:port, yerel port kaynak porttur; gelen pakette tersi.
    /// </summary>
    public static GpnPacketStats FromPacket(ReadOnlySpan<byte> data, bool outbound)
    {
        if (data.Length < 20)
        {
            return Unknown(data);
        }

        return (data[0] >> 4) switch
        {
            4 => ParseV4(data, outbound),
            6 => ParseV6(data, outbound),
            _ => Unknown(data),
        };
    }

    private static GpnPacketStats ParseV4(ReadOnlySpan<byte> data, bool outbound)
    {
        var ihl = (data[0] & 0x0F) * 4;
        if (ihl < 20 || data.Length < ihl + 4)
        {
            return Unknown(data);
        }

        var protocol = data[9];
        var src = new IPAddress(data.Slice(12, 4));
        var dst = new IPAddress(data.Slice(16, 4));
        var peer = outbound ? dst : src;
        var localPort = 0;
        var isTcpUdp = protocol is 6 or 17;
        if (isTcpUdp)
        {
            localPort = outbound
                ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(ihl, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(ihl + 2, 2));
        }

        return new GpnPacketStats(
            Classify(protocol),
            FormatEndpoint(peer, isTcpUdp ? (outbound ? data.Slice(ihl + 2, 2) : data.Slice(ihl, 2)) : default, isTcpUdp),
            (ushort)localPort,
            data.Length,
            IsIpv6: false,
            Outbound: outbound);
    }

    private static GpnPacketStats ParseV6(ReadOnlySpan<byte> data, bool outbound)
    {
        if (data.Length < 40)
        {
            return Unknown(data);
        }

        var nextHeader = data[6];
        var src = new IPAddress(data.Slice(8, 16));
        var dst = new IPAddress(data.Slice(24, 16));
        var peer = outbound ? dst : src;
        var isTcpUdp = nextHeader is 6 or 17;
        var localPort = 0;
        if (isTcpUdp)
        {
            localPort = outbound
                ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(40, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(42, 2));
        }

        return new GpnPacketStats(
            Classify(nextHeader),
            FormatEndpoint(peer, isTcpUdp ? (outbound ? data.Slice(42, 2) : data.Slice(40, 2)) : default, isTcpUdp),
            (ushort)localPort,
            data.Length,
            IsIpv6: true,
            Outbound: outbound);
    }

    private static GpnPacketProtocol Classify(byte protocol) => protocol switch
    {
        17 => GpnPacketProtocol.Udp,
        6 => GpnPacketProtocol.Tcp,
        1 or 58 => GpnPacketProtocol.Icmp,
        0 => GpnPacketProtocol.Unknown,
        _ => GpnPacketProtocol.Other,
    };

    private static string FormatEndpoint(IPAddress peer, ReadOnlySpan<byte> portBytes, bool withPort)
    {
        if (!withPort || portBytes.Length < 2)
        {
            return peer.ToString();
        }
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        return peer.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{peer}]:{port}"
            : $"{peer}:{port}";
    }
}
