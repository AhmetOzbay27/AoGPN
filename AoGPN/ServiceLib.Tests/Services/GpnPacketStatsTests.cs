using AwesomeAssertions;
using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

/// <summary>GpnPacketStats IP başlık çözücü testleri (saf yönetilen parse).</summary>
public class GpnPacketStatsTests
{
    [Fact]
    public void FromPacket_V4Udp_Outbound_ReadsPeerAndLocalPort()
    {
        var stats = GpnPacketStats.FromPacket(GpnPacketTestData.V4Udp(5000, 27015), outbound: true);

        stats.Protocol.Should().Be(GpnPacketProtocol.Udp);
        stats.PeerEndpoint.Should().Be("8.8.4.4:27015");
        stats.LocalPort.Should().Be(5000);
        stats.Outbound.Should().BeTrue();
        stats.IsIpv6.Should().BeFalse();
        stats.Length.Should().Be(28);
    }

    [Fact]
    public void FromPacket_V4Udp_Inbound_FlipsPeerAndLocalPort()
    {
        var stats = GpnPacketStats.FromPacket(GpnPacketTestData.V4Udp(5000, 27015), outbound: false);

        stats.PeerEndpoint.Should().Be("10.1.2.3:5000");
        stats.LocalPort.Should().Be(27015);
        stats.Outbound.Should().BeFalse();
    }

    [Fact]
    public void FromPacket_V4Tcp_ClassifiesTcp()
    {
        var stats = GpnPacketStats.FromPacket(GpnPacketTestData.V4Tcp(5000, 443), outbound: true);

        stats.Protocol.Should().Be(GpnPacketProtocol.Tcp);
        stats.PeerEndpoint.Should().Be("8.8.4.4:443");
        stats.LocalPort.Should().Be(5000);
    }

    [Fact]
    public void FromPacket_V4Icmp_HasNoPorts()
    {
        var stats = GpnPacketStats.FromPacket(GpnPacketTestData.V4Icmp(), outbound: true);

        stats.Protocol.Should().Be(GpnPacketProtocol.Icmp);
        stats.PeerEndpoint.Should().Be("8.8.4.4");
        stats.LocalPort.Should().Be(0);
    }

    [Fact]
    public void FromPacket_V4WithOptions_ReadsPortsAtIhlOffset()
    {
        var stats = GpnPacketStats.FromPacket(GpnPacketTestData.V4UdpWithOptions(6000, 27015), outbound: true);

        stats.Protocol.Should().Be(GpnPacketProtocol.Udp);
        stats.PeerEndpoint.Should().Be("8.8.4.4:27015");
        stats.LocalPort.Should().Be(6000);
    }

    [Fact]
    public void FromPacket_V6Udp_Outbound_FormatsBracketedPeer()
    {
        var stats = GpnPacketStats.FromPacket(GpnPacketTestData.V6Udp(5000, 27015), outbound: true);

        stats.Protocol.Should().Be(GpnPacketProtocol.Udp);
        stats.PeerEndpoint.Should().Be("[2001:db8::2]:27015");
        stats.LocalPort.Should().Be(5000);
        stats.IsIpv6.Should().BeTrue();
    }

    [Fact]
    public void FromPacket_Truncated_ReturnsUnknown()
    {
        var stats = GpnPacketStats.FromPacket(new byte[] { 0x45, 0x00, 0x00 }, outbound: true);

        stats.Protocol.Should().Be(GpnPacketProtocol.Unknown);
        stats.PeerEndpoint.Should().BeEmpty();
        stats.Length.Should().Be(3);
    }

    [Fact]
    public void FromPacket_NonIpVersion_ReturnsUnknown()
    {
        var data = new byte[20];
        data[0] = 0x30; // version 3 — desteklenmiyor
        var stats = GpnPacketStats.FromPacket(data, outbound: true);

        stats.Protocol.Should().Be(GpnPacketProtocol.Unknown);
    }

    [Fact]
    public void FromPacket_Empty_ReturnsUnknown_WithoutThrowing()
    {
        var stats = GpnPacketStats.FromPacket(Array.Empty<byte>(), outbound: true);

        stats.Protocol.Should().Be(GpnPacketProtocol.Unknown);
        stats.Length.Should().Be(0);
    }
}
