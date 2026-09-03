namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnPacketStats / GpnCaptureLoop telemetri testleri için sentetik IP paketleri.
/// (Ham byte üretir — gerçek sürücü gerektirmez.)
/// </summary>
internal static class GpnPacketTestData
{
    private static readonly byte[] V4Src = { 10, 1, 2, 3 };
    private static readonly byte[] V4Dst = { 8, 8, 4, 4 };
    private static readonly byte[] V6Src = { 0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
    private static readonly byte[] V6Dst = { 0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 };

    /// <summary>IPv4 UDP: 20 bayt başlık + 8 bayt UDP (portlar dahil).</summary>
    public static byte[] V4Udp(int srcPort, int dstPort)
        => V4Transport(17, 20, srcPort, dstPort);

    /// <summary>IPv4 TCP: 20 bayt başlık + 8 bayt TCP başlık öncesi (portlar).</summary>
    public static byte[] V4Tcp(int srcPort, int dstPort)
        => V4Transport(6, 20, srcPort, dstPort);

    /// <summary>IPv4 ICMP (port yok): 20 bayt başlık + 8 bayt gövde.</summary>
    public static byte[] V4Icmp()
    {
        var pkt = new byte[28];
        FillV4Header(pkt, 1, 20);
        pkt[20] = 8; // echo request
        return pkt;
    }

    /// <summary>IPv4 seçenekli UDP (IHL=6): taşıma başlığı 24. baytta.</summary>
    public static byte[] V4UdpWithOptions(int srcPort, int dstPort)
        => V4Transport(17, 24, srcPort, dstPort);

    /// <summary>IPv6 UDP (uzantı başlığı yok): 40 bayt + 8 bayt UDP.</summary>
    public static byte[] V6Udp(int srcPort, int dstPort)
    {
        var pkt = new byte[48];
        pkt[0] = 0x60; // version 6
        WriteU16(pkt, 4, 8); // payload length
        pkt[6] = 17; // next header: UDP
        pkt[7] = 64; // hop limit
        V6Src.CopyTo(pkt, 8);
        V6Dst.CopyTo(pkt, 24);
        WriteU16(pkt, 40, srcPort);
        WriteU16(pkt, 42, dstPort);
        WriteU16(pkt, 44, 8); // UDP length
        return pkt;
    }

    private static byte[] V4Transport(byte protocol, int headerLen, int srcPort, int dstPort)
    {
        var pkt = new byte[headerLen + 8];
        pkt[0] = (byte)(0x40 | (headerLen / 4)); // version 4 + IHL
        WriteU16(pkt, 2, (ushort)pkt.Length); // total length
        pkt[8] = 64; // TTL
        pkt[9] = protocol;
        V4Src.CopyTo(pkt, 12);
        V4Dst.CopyTo(pkt, 16);
        WriteU16(pkt, headerLen, srcPort);
        WriteU16(pkt, headerLen + 2, dstPort);
        WriteU16(pkt, headerLen + 4, 8); // UDP/TCP length
        return pkt;
    }

    private static void FillV4Header(byte[] pkt, byte protocol, int headerLen)
    {
        pkt[0] = (byte)(0x40 | (headerLen / 4));
        WriteU16(pkt, 2, (ushort)pkt.Length);
        pkt[8] = 64;
        pkt[9] = protocol;
        V4Src.CopyTo(pkt, 12);
        V4Dst.CopyTo(pkt, 16);
    }

    private static void WriteU16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }
}
