using AwesomeAssertions;
using ServiceLib.Services.Gpn;
using Xunit;

namespace ServiceLib.Tests.Services.Gpn;

/// <summary>
/// WARP dial sağlık monitörünün durum makinesi testleri — dosya kuyruğu
/// yerine <see cref="WarpDialHealthMonitor.ProcessLine"/> doğrudan beslenir.
/// </summary>
public class WarpDialHealthMonitorTests
{
    private static WarpDialHealthMonitor CreateMonitor() => new(
        faultThreshold: 3,
        faultWindow: TimeSpan.FromSeconds(60),
        recoveryCooldown: TimeSpan.FromSeconds(45),
        pollInterval: TimeSpan.FromSeconds(2));

    private const string NoRouteLine =
        "ERROR [1 150ms] connection: open connection to launcher-seq.eft-project.com:2095 " +
        "using outbound/socks[warp]: connect tcp 10.66.66.1:40000: no route to host";

    [Fact]
    public void IsWarpFailureLine_MatchesKnownDialErrors()
    {
        WarpDialHealthMonitor.IsWarpFailureLine(NoRouteLine).Should().BeTrue();
        WarpDialHealthMonitor.IsWarpFailureLine(
            "ERROR [2 151ms] connection: open connection to x:443 using outbound/socks[warp]: connect tcp 10.66.66.1:40000: connection was refused")
            .Should().BeTrue();
        WarpDialHealthMonitor.IsWarpFailureLine(
            "ERROR [3 1.2s] connection: open connection to x:443 using outbound/socks[warp]: connect tcp 10.66.66.1:40000: i/o timeout")
            .Should().BeTrue();
        // UDP associate — aynı socks[warp] dial'i (listen packet connection).
        WarpDialHealthMonitor.IsWarpFailureLine(
            "ERROR [4 3.2s] connection: listen packet connection using  using outbound/socks[warp]: connect tcp 10.66.66.1:40000: no route to host")
            .Should().BeTrue();
    }

    [Fact]
    public void IsWarpFailureLine_MatchesMihomoDialErrors()
    {
        // mihomo çekirdek formatı — warp-socks outbound adı + dial hatası.
        WarpDialHealthMonitor.IsWarpFailureLine(
            "time=\"2026-09-02T20:00:01+03:00\" level=error msg=\"[TCP] dial proxy 2(warp-socks) error: dial tcp 10.66.66.1:40000: connect: no route to host\"")
            .Should().BeTrue();
        WarpDialHealthMonitor.IsWarpFailureLine(
            "time=\"2026-09-02T20:00:02+03:00\" level=error msg=\"[TCP] dial proxy 2(warp-socks) error: dial tcp 10.66.66.1:40000: connectex: No connection could be made because the target machine actively refused it.\"")
            .Should().BeTrue();
        WarpDialHealthMonitor.IsWarpFailureLine(
            "time=\"2026-09-02T20:00:03+03:00\" level=error msg=\"[UDP] dial proxy 2(warp-socks) error: dial tcp 10.66.66.1:40000: i/o timeout\"")
            .Should().BeTrue();
        WarpDialHealthMonitor.IsWarpFailureLine(
            "time=\"2026-09-02T20:00:04+03:00\" level=error msg=\"[TCP] dial proxy 2(warp-socks) error: dial tcp 10.66.66.1:40000: network is unreachable\"")
            .Should().BeTrue();
    }

    [Fact]
    public void IsWarpFailureLine_IgnoresMihomoNonFailureLines()
    {
        // warp-socks geçen ama HATA OLMAYAN satırlar sayılmamalı.
        WarpDialHealthMonitor.IsWarpFailureLine(
            "time=\"2026-09-02T20:00:00+03:00\" level=info msg=\"[TCP] dial proxy 2(warp-socks) in connection to 10.66.66.1:40000\"")
            .Should().BeFalse();
        WarpDialHealthMonitor.IsWarpFailureLine(
            "time=\"2026-09-02T20:00:05+03:00\" level=info msg=\"start initial runner\"")
            .Should().BeFalse();
    }

    [Fact]
    public void ExtractError_ReturnsSuffix_ForMihomoLine()
    {
        WarpDialHealthMonitor.ExtractError(
            "time=\"2026-09-02T20:00:01+03:00\" level=error msg=\"[TCP] dial proxy 2(warp-socks) error: dial tcp 10.66.66.1:40000: connect: no route to host\"")
            .Should().Contain("10.66.66.1:40000")
            .And.Contain("no route to host");
    }

    [Fact]
    public void IsWarpFailureLine_IgnoresOtherTraffic()
    {
        // WG endpoint dial'i (warp değil) ve erişim satırları sayılmamalı.
        WarpDialHealthMonitor.IsWarpFailureLine(
            "ERROR [5 1ms] connection: open connection to 80.147.36.57:7680 using outbound/wireguard[proxy]: connect tcp 80.147.36.57:7680: no route to host")
            .Should().BeFalse();
        WarpDialHealthMonitor.IsWarpFailureLine(
            "INFO [6 0ms] outbound/socks[warp]: outbound connection to 10.66.66.1:40000")
            .Should().BeFalse();
        WarpDialHealthMonitor.IsWarpFailureLine(
            "ERROR [7 1ms] connection: open connection to x:443 using outbound/socks[proxy]: connect tcp 127.0.0.1:10808: no route to host")
            .Should().BeFalse();
        WarpDialHealthMonitor.IsWarpFailureLine(string.Empty).Should().BeFalse();
        WarpDialHealthMonitor.IsWarpFailureLine("WireGuard is not ready yet").Should().BeFalse();
    }

    [Fact]
    public void ExtractError_ReturnsHumanReadableSuffix()
    {
        WarpDialHealthMonitor.ExtractError(NoRouteLine)
            .Should().Contain("10.66.66.1:40000")
            .And.Contain("no route to host");
    }

    [Fact]
    public void BelowThreshold_StaysHealthy()
    {
        var monitor = CreateMonitor();
        monitor.NowUtc = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

        var snap1 = monitor.ProcessLine(NoRouteLine);
        snap1.Faulted.Should().BeFalse();
        snap1.ErrorCount.Should().Be(1);

        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        var snap2 = monitor.ProcessLine(NoRouteLine);
        snap2.Faulted.Should().BeFalse();
        snap2.ErrorCount.Should().Be(2);
    }

    [Fact]
    public void ThresholdReached_BecomesFaulted()
    {
        var monitor = CreateMonitor();
        monitor.NowUtc = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

        monitor.ProcessLine(NoRouteLine);
        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        monitor.ProcessLine(NoRouteLine);
        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        var snap = monitor.ProcessLine(NoRouteLine);

        snap.Faulted.Should().BeTrue("eşik (3) pencerede aşıldı");
        snap.ErrorCount.Should().Be(3);
        snap.LatestError.Should().Contain("no route to host");
    }

    [Fact]
    public void OldErrorsOutsideWindow_DoNotCount()
    {
        var monitor = CreateMonitor();
        monitor.NowUtc = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
        monitor.ProcessLine(NoRouteLine);

        // 60 s pencere doldu: ilk hata düşer, sadece ikincisi sayılır.
        monitor.NowUtc = monitor.NowUtc.AddSeconds(61);
        var snap = monitor.ProcessLine(NoRouteLine);

        snap.Faulted.Should().BeFalse("penceredışı hatalar sayılmaz");
        snap.ErrorCount.Should().Be(1);
    }

    [Fact]
    public void RecoveryAfterCooldown_ClearsFault()
    {
        var monitor = CreateMonitor();
        monitor.NowUtc = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
        monitor.ProcessLine(NoRouteLine);
        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        monitor.ProcessLine(NoRouteLine);
        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        monitor.ProcessLine(NoRouteLine).Faulted.Should().BeTrue();

        // Hatasız 45 s (cooldown) geçti — normal satırlar da iyileşmeyi tetikler.
        monitor.NowUtc = monitor.NowUtc.AddSeconds(46);
        var snap = monitor.ProcessLine(
            "INFO [8 0ms] inbound/mixed[socks]: inbound connection to profile.tarkov.com:443");

        snap.Faulted.Should().BeFalse("cooldown sonrası healthy'e döner");
    }

    [Fact]
    public void CheckRecovery_ClearsFaultAfterCooldownWithoutNewLines()
    {
        var monitor = CreateMonitor();
        monitor.NowUtc = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
        monitor.ProcessLine(NoRouteLine);
        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        monitor.ProcessLine(NoRouteLine);
        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        monitor.ProcessLine(NoRouteLine).Faulted.Should().BeTrue();

        // Yeni satır gelmese bile yoklama döngüsü iyileşmeyi değerlendirmeli.
        monitor.NowUtc = monitor.NowUtc.AddSeconds(60);
        monitor.CheckRecovery();

        monitor.Snapshot.Faulted.Should().BeFalse("cooldown dolunca satır beklemeden healthy");
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var monitor = CreateMonitor();
        monitor.NowUtc = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
        monitor.ProcessLine(NoRouteLine);
        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        monitor.ProcessLine(NoRouteLine);
        monitor.NowUtc = monitor.NowUtc.AddSeconds(1);
        monitor.ProcessLine(NoRouteLine).Faulted.Should().BeTrue();

        monitor.Reset();

        monitor.Snapshot.Faulted.Should().BeFalse();
        monitor.Snapshot.ErrorCount.Should().Be(0);
        monitor.Snapshot.LatestError.Should().BeNull();
    }
}