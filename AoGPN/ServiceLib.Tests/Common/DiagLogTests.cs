using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Events;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests.Common;

/// <summary>
/// DiagLog'un \"GPN_*\" satırlarını <see cref=\"AppEvents.GpnDiagChanged\"/>
/// üzerinden yayınlayan kancasının testleri. İşaretçi (marker) mesajlar kullanılır
/// — GPN seçim/failover testleri paralel çalışıp kendi GPN_* satırlarını da
/// yayınlayabilir, bu yüzden yalnızca benzersiz mesajlar üzerinden doğrulama yapılır.
/// </summary>
public class DiagLogTests
{
    [Fact]
    public void Write_GpnLine_PublishesDiagEvent_WithKindAndMessage()
    {
        const string marker = "GPN_RECOVER test-marker-xyz wireguard → de";
        var received = new List<GpnDiagEvent>();
        using var sub = AppEvents.GpnDiagChanged.AsObservable().Subscribe(received.Add);

        DiagLog.Write(marker);

        received.Should().Contain(e => e.Message == marker);
        var evt = received.First(e => e.Message == marker);
        evt.Kind.Should().Be("RECOVER");
        evt.TimestampMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Write_NonGpnLine_DoesNotPublish()
    {
        // FLUSH / TUN_* / ROUTE / CORE_* gibi diğer diyagnoz kategorileri dashboard
        // akışına karışmamalı — yalnızca GPN_* yayınlanır.
        const string marker = "FLUSH test-marker-xyz tcpKilled=0";
        var received = new List<GpnDiagEvent>();
        using var sub = AppEvents.GpnDiagChanged.AsObservable().Subscribe(received.Add);

        DiagLog.Write(marker);

        received.Should().NotContain(e => e.Message == marker);
    }

    [Fact]
    public void Write_GpnLineWithoutSpace_KindIsFullSuffix()
    {
        const string marker = "GPN_LAUNCH";
        var received = new List<GpnDiagEvent>();
        using var sub = AppEvents.GpnDiagChanged.AsObservable().Subscribe(received.Add);

        DiagLog.Write(marker);

        received.Should().Contain(e => e.Message == marker);
        received.First(e => e.Message == marker).Kind.Should().Be("LAUNCH");
    }
}
