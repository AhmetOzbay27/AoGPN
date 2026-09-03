using AwesomeAssertions;
using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

public class GpnJsonlConsumerTests
{
    private static DateTimeOffset Ingest => new(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(3));

    // GpnProbeTool'un --jsonl çıktısındaki gerçek server_probe satır biçimi.
    private const string ProbeLine =
        "{\"event\":\"server_probe\",\"serverId\":\"it\",\"name\":\"İtalya\",\"host\":\"92.4.220.236\"," +
        "\"port\":51820,\"icmpMs\":25,\"icmpAvgMs\":27,\"icmpLossPct\":0,\"udp\":\"Open\"," +
        "\"udpDetail\":\"\",\"handshake\":\"Open\",\"timestamp\":\"2026-08-29T11:59:40.1234567+03:00\"}";

    // ── Ayrıştırma ───────────────────────────────────────────────────────

    [Fact]
    public void TryParseServerProbe_ParsesRealShape()
    {
        var log = GpnJsonlConsumer.TryParseServerProbe(ProbeLine, Ingest);

        log.Should().NotBeNull();
        log!.ServerId.Should().Be("it");
        log.Name.Should().Be("İtalya");
        log.Host.Should().Be("92.4.220.236");
        log.Port.Should().Be(51820);
        log.IcmpMs.Should().Be(25);
        log.IcmpAvgMs.Should().Be(27);
        log.IcmpLossPct.Should().Be(0);
        log.Udp.Should().Be("Open");
        log.Handshake.Should().Be("Open");
        // Kesirli saniyelerle tam yuvarlak gidiş-dönüş (DateTimeOffset.Parse → ToString("O")).
        log.Timestamp.ToString("O").Should().Be("2026-08-29T11:59:40.1234567+03:00");
        log.IngestedAt.Should().Be(Ingest);
    }

    [Fact]
    public void TryParseServerProbe_NonServerProbeEvent_ReturnsNull()
    {
        const string selection =
            "{\"event\":\"selection\",\"best\":\"it\",\"mode\":\"WireGuardUDP\",\"timestamp\":\"2026-08-29T11:59:41+03:00\"}";

        GpnJsonlConsumer.TryParseServerProbe(selection, Ingest).Should().BeNull();
    }

    [Fact]
    public void TryParseServerProbe_GarbageAndEmpty_ReturnsNull()
    {
        GpnJsonlConsumer.TryParseServerProbe("not json at all", Ingest).Should().BeNull();
        GpnJsonlConsumer.TryParseServerProbe("   ", Ingest).Should().BeNull();
        GpnJsonlConsumer.TryParseServerProbe("{\"event\":42}", Ingest).Should().BeNull();
        GpnJsonlConsumer.TryParseServerProbe("{\"event\":\"server_probe\"}", Ingest).Should().BeNull();
    }

    [Fact]
    public void TryParseServerProbe_MissingTimestamp_FallsBackToIngestedAt()
    {
        const string line =
            "{\"event\":\"server_probe\",\"serverId\":\"de\",\"icmpMs\":-1,\"udp\":\"Blocked\"}";

        var log = GpnJsonlConsumer.TryParseServerProbe(line, Ingest);

        log.Should().NotBeNull();
        log!.ServerId.Should().Be("de");
        log.Timestamp.Should().Be(Ingest);
        log.IcmpMs.Should().Be(-1);
        log.Udp.Should().Be("Blocked");
    }

    // ── Zenginleştirme (zaman damgalı JSONL arşivi) ──────────────────────

    [Fact]
    public void EnrichLine_AddsIngestedAt_KeepsOriginalFields()
    {
        var enriched = GpnJsonlConsumer.EnrichLine(ProbeLine, Ingest);

        enriched.Should().NotBeNull();
        var doc = System.Text.Json.JsonDocument.Parse(enriched!);
        DateTimeOffset.Parse(doc.RootElement.GetProperty("ingestedAt").GetString()!).Should().Be(Ingest);
        doc.RootElement.GetProperty("event").GetString().Should().Be("server_probe");
        doc.RootElement.GetProperty("serverId").GetString().Should().Be("it");
        doc.RootElement.GetProperty("icmpMs").GetInt32().Should().Be(25);
    }

    [Fact]
    public void EnrichLine_NonObject_ReturnsNull()
    {
        GpnJsonlConsumer.EnrichLine("[1,2,3]", Ingest).Should().BeNull();
        GpnJsonlConsumer.EnrichLine("bogus", Ingest).Should().BeNull();
        GpnJsonlConsumer.EnrichLine("", Ingest).Should().BeNull();
    }

    // ── Prometheus durumu + exposition ───────────────────────────────────

    [Fact]
    public void State_LatestPerServerWins()
    {
        var state = new GpnPrometheusProbeState();
        state.Update(GpnJsonlConsumer.TryParseServerProbe(ProbeLine, Ingest)!);

        // Aynı sunucu için yeni ölçüm — son satır kazanır.
        const string newer =
            "{\"event\":\"server_probe\",\"serverId\":\"it\",\"name\":\"İtalya\",\"icmpMs\":40," +
            "\"udp\":\"NoResponse\",\"timestamp\":\"2026-08-29T12:05:00+03:00\"}";
        state.Update(GpnJsonlConsumer.TryParseServerProbe(newer, Ingest)!);

        var snap = state.Snapshot();
        snap.Should().ContainSingle();
        snap[0].PingMs.Should().Be(40);
        snap[0].UdpStatus.Should().Be("NoResponse");
    }

    [Fact]
    public void BuildExposition_EscapesLabelsAndPings()
    {
        var state = new GpnPrometheusProbeState();
        state.Update(GpnJsonlConsumer.TryParseServerProbe(ProbeLine, Ingest)!);

        var text = state.BuildExposition();

        text.Should().Contain("# HELP gpn_ping_ms");
        text.Should().Contain("# TYPE gpn_ping_ms gauge");
        text.Should().Contain("# TYPE gpn_udp_status gauge");
        text.Should().Contain("gpn_ping_ms{server=\"it\",name=\"İtalya\"} 25");
    }

    [Fact]
    public void BuildExposition_UdpStatus_OneHot()
    {
        var state = new GpnPrometheusProbeState();
        state.Update(GpnJsonlConsumer.TryParseServerProbe(ProbeLine, Ingest)!); // udp=Open

        var text = state.BuildExposition();

        text.Should().Contain("gpn_udp_status{server=\"it\",name=\"İtalya\",status=\"Open\"} 1");
        text.Should().Contain("gpn_udp_status{server=\"it\",name=\"İtalya\",status=\"Blocked\"} 0");
        text.Should().Contain("gpn_udp_status{server=\"it\",name=\"İtalya\",status=\"NoResponse\"} 0");
        text.Should().Contain("gpn_udp_status{server=\"it\",name=\"İtalya\",status=\"HandshakeNoResponse\"} 0");
    }

    [Fact]
    public void BuildExposition_LabelEscape()
    {
        var state = new GpnPrometheusProbeState();
        const string evil =
            "{\"event\":\"server_probe\",\"serverId\":\"a\\\"b\",\"name\":\"x\\\\y\",\"icmpMs\":10," +
            "\"udp\":\"Blocked\",\"timestamp\":\"2026-08-29T12:05:00+03:00\"}";
        state.Update(GpnJsonlConsumer.TryParseServerProbe(evil, Ingest)!);

        var text = state.BuildExposition();

        text.Should().Contain("gpn_ping_ms{server=\"a\\\"b\",name=\"x\\\\y\"} 10");
        // Kaçış sonrası ham tırnak etikete sızamaz.
        text.Should().NotContain("server=\"a\"b\"");
    }

    [Fact]
    public void BuildExposition_MultipleServers_TwoPingSeries()
    {
        var state = new GpnPrometheusProbeState();
        state.Update(GpnJsonlConsumer.TryParseServerProbe(ProbeLine, Ingest)!);
        state.Update(GpnJsonlConsumer.TryParseServerProbe(
            "{\"event\":\"server_probe\",\"serverId\":\"de\",\"name\":\"Almanya\",\"icmpMs\":31," +
            "\"udp\":\"Open\",\"timestamp\":\"2026-08-29T11:59:41+03:00\"}", Ingest)!);

        var text = state.BuildExposition();

        text.Should().Contain("gpn_ping_ms{server=\"it\",name=\"İtalya\"} 25");
        text.Should().Contain("gpn_ping_ms{server=\"de\",name=\"Almanya\"} 31");
    }

    [Fact]
    public void BuildExposition_EmptyState_OnlyHelpAndTypeLines()
    {
        var text = new GpnPrometheusProbeState().BuildExposition();

        text.Should().Contain("# TYPE gpn_ping_ms gauge");
        text.Should().NotContain("gpn_ping_ms{");
        text.Should().NotContain("gpn_udp_status{");
    }
}
