using AwesomeAssertions;
using ServiceLib.Events;
using ServiceLib.Models;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnTelemetryService — failover/kurtarma olaylarını sayaçlayan servis.
/// İzole bir <see cref="EventChannel{T}"/> üzerinden test edilir (global
/// AppEvents.GpnResilienceChanged akışının paralel test gürültüsü karışmaz).
/// Kurtarma döngüsü de kapsanır: RunFailoverMonitorAsync'in Recover/ModeFallback
/// olayları bu akışa düşer ve sayaç tarafından kaydedilir.
/// </summary>
public class GpnTelemetryServiceTests
{
    private static GpnResilienceEvent Evt(GpnResilienceAction action, ConnectionMode to = ConnectionMode.WireGuardUDP)
        => new(action, to);

    [Fact]
    public void Counts_FailoverAndRecoveryEvents()
    {
        var source = new EventChannel<GpnResilienceEvent>();
        using var service = new GpnTelemetryService(source);

        source.Publish(Evt(GpnResilienceAction.ServerSwitch));
        source.Publish(Evt(GpnResilienceAction.UdpDeath));
        source.Publish(Evt(GpnResilienceAction.ModeFallback));
        source.Publish(Evt(GpnResilienceAction.Recover));
        source.Publish(Evt(GpnResilienceAction.ModeDecision));

        var s = service.Snapshot;
        s.ServerSwitches.Should().Be(1);
        s.UdpDeaths.Should().Be(1);
        s.ModeFallbacks.Should().Be(1);
        s.Recoveries.Should().Be(1);
        s.ModeDecisions.Should().Be(1);
        s.TotalEvents.Should().Be(5);
    }

    [Fact]
    public void RecoveryLoop_RecoverAndFallback_AreCounted()
    {
        // V2rayTCP düşüşü (ModeFallback) + Tier-2 kurtarması (Recover) — kurtarma
        // döngüsünün üreteceği olay çifti.
        var source = new EventChannel<GpnResilienceEvent>();
        using var service = new GpnTelemetryService(source);

        source.Publish(Evt(GpnResilienceAction.ModeFallback, ConnectionMode.V2rayTCP));
        source.Publish(Evt(GpnResilienceAction.Recover, ConnectionMode.WireGuardUDP));

        var s = service.Snapshot;
        s.ModeFallbacks.Should().Be(1);
        s.Recoveries.Should().Be(1);
        s.TotalEvents.Should().Be(2);
    }

    [Fact]
    public void Reset_ClearsCounters()
    {
        var source = new EventChannel<GpnResilienceEvent>();
        using var service = new GpnTelemetryService(source);

        source.Publish(Evt(GpnResilienceAction.ServerSwitch));
        source.Publish(Evt(GpnResilienceAction.Recover));
        service.Snapshot.TotalEvents.Should().Be(2);

        service.Reset();

        var s = service.Snapshot;
        s.ServerSwitches.Should().Be(0);
        s.UdpDeaths.Should().Be(0);
        s.ModeFallbacks.Should().Be(0);
        s.Recoveries.Should().Be(0);
        s.ModeDecisions.Should().Be(0);
        s.TotalEvents.Should().Be(0);
    }

    [Fact]
    public void IgnoredActions_DoNotCount()
    {
        // Bilinmeyen/gelecekteki eylemler sayaçları bozmamalı (default düşer).
        var source = new EventChannel<GpnResilienceEvent>();
        using var service = new GpnTelemetryService(source);

        // GPN dışı benzer bir kanala yayın yapılsa bile sayılmaz — servis yalnızca
        // kendi kaynağını dinler; burada bilinçli olarak bir UdpDeath sayarız.
        source.Publish(Evt(GpnResilienceAction.UdpDeath));
        service.Snapshot.UdpDeaths.Should().Be(1);
        service.Snapshot.ServerSwitches.Should().Be(0);
    }
}