using AwesomeAssertions;
using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

/// <summary>GpnCaptureTelemetry sayaç + anlık görüntü testleri.</summary>
public class GpnCaptureTelemetryTests
{
    private static GpnPacketStats Udp(string peer, ushort localPort, int len, bool outbound = true)
        => new(GpnPacketProtocol.Udp, peer, localPort, len, IsIpv6: false, Outbound: outbound);

    [Fact]
    public void Record_AggregatesTotalsAndProtocols()
    {
        var telemetry = new GpnCaptureTelemetry();

        telemetry.Record(Udp("8.8.4.4:27015", 5000, 28), pid: null, pidInPool: false);
        telemetry.Record(Udp("8.8.4.4:27015", 5000, 28), pid: null, pidInPool: false);
        telemetry.Record(Udp("8.8.8.8:53", 53000, 40), pid: null, pidInPool: false);
        telemetry.Record(new GpnPacketStats(GpnPacketProtocol.Tcp, "1.2.3.4:443", 4000, 60, false, true), pid: null, pidInPool: false);

        var snap = telemetry.Snapshot;

        snap.TotalPackets.Should().Be(4);
        snap.TotalBytes.Should().Be(156);
        snap.UdpPackets.Should().Be(3);
        snap.TcpPackets.Should().Be(1);
        snap.OutboundPackets.Should().Be(4);
        snap.InboundPackets.Should().Be(0);
    }

    [Fact]
    public void Record_TracksInboundDirection()
    {
        var telemetry = new GpnCaptureTelemetry();
        telemetry.Record(Udp("10.1.2.3:5000", 27015, 28, outbound: false), pid: null, pidInPool: false);

        var snap = telemetry.Snapshot;
        snap.InboundPackets.Should().Be(1);
        snap.OutboundPackets.Should().Be(0);
    }

    [Fact]
    public void Snapshot_TopFlows_OrderedByPackets_AndCapped()
    {
        var telemetry = new GpnCaptureTelemetry(maxFlows: 2);

        telemetry.Record(Udp("a:1", 1, 10), pid: null, pidInPool: false);
        telemetry.Record(Udp("a:1", 1, 10), pid: null, pidInPool: false);
        telemetry.Record(Udp("b:2", 2, 10), pid: null, pidInPool: false);
        telemetry.Record(Udp("c:3", 3, 10), pid: null, pidInPool: false);

        var snap = telemetry.Snapshot;

        snap.TopFlows.Should().HaveCount(2);
        snap.TopFlows[0].PeerEndpoint.Should().Be("a:1");
        snap.TopFlows[0].Packets.Should().Be(2);
        snap.TopFlows[0].Bytes.Should().Be(20);
        snap.TopFlows[1].Packets.Should().Be(1);
    }

    [Fact]
    public void Record_AttributedPid_AggregatesPerPid_WithPoolFlag()
    {
        var telemetry = new GpnCaptureTelemetry();

        telemetry.Record(Udp("8.8.4.4:27015", 5000, 28), pid: 100, pidInPool: true);
        telemetry.Record(Udp("8.8.4.4:27015", 5000, 28), pid: 100, pidInPool: true);
        telemetry.Record(Udp("8.8.8.8:53", 53000, 40), pid: 200, pidInPool: false);

        var snap = telemetry.Snapshot;

        snap.ByPid.Should().HaveCount(2);
        snap.ByPid[0].Pid.Should().Be(100); // en çok paket üstte
        snap.ByPid[0].Packets.Should().Be(2);
        snap.ByPid[0].InPool.Should().BeTrue();
        snap.ByPid[1].Pid.Should().Be(200);
        snap.ByPid[1].InPool.Should().BeFalse();
    }

    [Fact]
    public void Record_WithoutPid_ProducesNoPidRows()
    {
        var telemetry = new GpnCaptureTelemetry();
        telemetry.Record(Udp("8.8.4.4:27015", 5000, 28), pid: null, pidInPool: false);

        telemetry.Snapshot.ByPid.Should().BeEmpty();
        telemetry.Snapshot.TopFlows.Should().HaveCount(1);
    }

    [Fact]
    public void Reset_ClearsCountersAndMaps()
    {
        var telemetry = new GpnCaptureTelemetry();
        telemetry.Record(Udp("8.8.4.4:27015", 5000, 28), pid: 100, pidInPool: true);

        telemetry.Reset();
        var snap = telemetry.Snapshot;

        snap.TotalPackets.Should().Be(0);
        snap.TopFlows.Should().BeEmpty();
        snap.ByPid.Should().BeEmpty();
    }
}

/// <summary>GpnPortPidTable köprüsü testleri (IP Helper yerine enjekte edilmiş kaynak).</summary>
public class GpnPortPidTableTests
{
    [Fact]
    public void GetUdpPortOwners_ReturnsInjectedMap()
    {
        var table = new GpnPortPidTable(() => new Dictionary<ushort, uint> { [5000] = 100 });

        table.GetUdpPortOwners()[5000].Should().Be(100);
    }

    [Fact]
    public void GetUdpPortOwners_SourceThrows_ReturnsEmpty()
    {
        var table = new GpnPortPidTable(() => throw new InvalidOperationException("tablo yok"));

        table.GetUdpPortOwners().Should().BeEmpty();
    }
}
