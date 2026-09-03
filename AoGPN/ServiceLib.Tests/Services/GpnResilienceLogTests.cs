using AwesomeAssertions;
using ServiceLib.Events;
using ServiceLib.Models;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnResilienceLog — son 50 GPN kararını tutan döngüsel tampon + dosya.
/// İzole <see cref="EventChannel{T}"/> + geçici dosya ile test edilir.
/// </summary>
public class GpnResilienceLogTests : IDisposable
{
    private readonly string _tempFile;

    public GpnResilienceLogTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"gpn_reslog_{Guid.NewGuid():N}.log");
    }

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { /* best-effort */ }
    }

    private static GpnResilienceEvent Evt(GpnResilienceAction action,
        string? serverName = null, string? reason = null) =>
        new(action, ConnectionMode.WireGuardUDP,
            reason: reason, serverId: "it", serverName: serverName ?? "İtalya");

    [Fact]
    public void PublishesAppearInRecentAndWrittenToFile()
    {
        var source = new EventChannel<GpnResilienceEvent>();
        using var log = new GpnResilienceLog(source, _tempFile);

        source.Publish(Evt(GpnResilienceAction.ServerSwitch, serverName: "İtalya", reason: "de daha iyi"));
        source.Publish(Evt(GpnResilienceAction.Recover, serverName: "Almanya"));

        log.Count.Should().Be(2);
        log.Recent.Should().HaveCount(2);
        log.Recent[0].Action.Should().Be(GpnResilienceAction.ServerSwitch);
        log.Recent[1].Action.Should().Be(GpnResilienceAction.Recover);

        File.Exists(_tempFile).Should().BeTrue();
        var lines = File.ReadAllLines(_tempFile);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("ServerSwitch").And.Contain("İtalya");
    }

    [Fact]
    public void OverflowCapDropOldestAndRewriteFile()
    {
        var source = new EventChannel<GpnResilienceEvent>();
        using var log = new GpnResilienceLog(source, _tempFile);

        for (var i = 0; i < 65; i++)
        {
            source.Publish(Evt(GpnResilienceAction.ModeDecision, reason: $"karar-{i}"));
        }

        log.Count.Should().Be(GpnResilienceLog.Capacity);
        log.Recent.Should().HaveCount(GpnResilienceLog.Capacity);
        // İlk karar artık tamponda değil — korunan en eskisi 65-50 = 15.
        log.Recent.First().Reason.Should().Be("karar-15");

        var lines = File.ReadAllLines(_tempFile);
        lines.Should().HaveCount(GpnResilienceLog.Capacity);
        lines[0].Should().Contain("karar-15");
        lines[^1].Should().Contain("karar-64");
    }

    [Fact]
    public void ClearResetsBufferAndEmptiesFile()
    {
        var source = new EventChannel<GpnResilienceEvent>();
        using var log = new GpnResilienceLog(source, _tempFile);
        source.Publish(Evt(GpnResilienceAction.UdpDeath));

        log.Clear();

        log.Count.Should().Be(0);
        log.Recent.Should().BeEmpty();
        File.ReadAllLines(_tempFile).Should().BeEmpty();
    }

    [Fact]
    public void LineFormat_ContainsTimeAndAction()
    {
        var source = new EventChannel<GpnResilienceEvent>();
        using var log = new GpnResilienceLog(source, _tempFile);
        source.Publish(Evt(GpnResilienceAction.ModeFallback, reason: "UDP ölü"));

        var line = log.Recent[0].Line;
        line.Should().Contain("ModeFallback");
        line.Should().Contain("UDP ölü");
        // [HH:mm:ss.fff] formatı — köşeli parantez ve zaman damgası.
        line.Should().StartWith("[");
    }
}
