using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// RunFailoverMonitorAsync — Akıllı Düşüş failover mantığı: aktif sunucu ölünce
/// V2rayTCP'ye düşmeden önce diğer adayların UDP yolu kontrol edilir ve açıksa
/// sunucu değişimi yapılır. Sadece HİÇBİR adayda sağlıklı UDP yoksa Tier-3'e
/// düşülür.
/// </summary>
public class RunFailoverMonitorSmartFallbackTests
{
    // ── Ana test: aktif ölü + sağlıklı aday → geçiş, V2rayTCP düşmez ──────

    [Fact]
    public async Task ActiveDead_OtherHealthy_SwitchesInsteadOfV2ray()
    {
        var ct = TestContext.Current.CancellationToken;
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // Çevrim 1: it=Blocked (tünel öldü), de=NoResponse (sağlıklı — varsayılan politika).
        // DecideFailover SwitchServer(de) dönmeli; monitor onSwitch(de) çağırmalı.
        // V2rayTCP düşüşü HİÇ OLMAHAMALI.
        var checker = new StagedUdpHealthChecker(
            stage: [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.NoResponse)],
            recovery: [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Blocked)]);
        var service = new GpnServerSelectionService(udpHealthChecker: checker);

        var switchedTo = new TaskCompletionSource<GpnServerProfile?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fellToV2ray = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = Task.Run(() => service.RunFailoverMonitorAsync(
            active: it,
            candidates: [it, de],
            onSwitch: (server, _) =>
            {
                switchedTo.TrySetResult(server);
                return Task.CompletedTask;
            },
            onModeFallback: (mode, _) =>
            {
                fellToV2ray.TrySetResult(mode == ConnectionMode.V2rayTCP);
                return Task.CompletedTask;
            },
            options: new GpnProbeOptions
            {
                Samples = 1,
                PerSampleTimeoutMs = 300,
                UseWireGuardHandshakeProbe = false,
            },
            interval: TimeSpan.FromMilliseconds(50),
            cancellationToken: cts.Token), ct);

        // de'ye geçiş beklenir.
        var switched = await switchedTo.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        switched.Should().NotBeNull();
        switched!.ServerId.Should().Be("de");

        // V2rayTCP düşüşü HİÇ tetiklenmemeli — sağlıklı aday varken tier-3'e düşülmez.
        fellToV2ray.Task.IsCompleted.Should().BeFalse(
            "V2rayTCP'ye düşülmemeli — sağlıklı aday mevcut");

        cts.Cancel();
        await monitor;
    }

    // ── İki aday da ölü → V2rayTCP düşüşü (kontrol) ──────────────────────

    [Fact]
    public async Task ActiveDead_NoHealthyCandidate_FallsToV2ray()
    {
        var ct = TestContext.Current.CancellationToken;
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // Çevrim 1: her ikisi de Blocked → hiçbir sağlıklı aday yok → V2rayTCP.
        var checker = new StagedUdpHealthChecker(
            stage: [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Blocked)],
            recovery: [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Blocked)]);
        var service = new GpnServerSelectionService(udpHealthChecker: checker);

        var fellToV2ray = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var switchedTo = new TaskCompletionSource<GpnServerProfile?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = Task.Run(() => service.RunFailoverMonitorAsync(
            active: it,
            candidates: [it, de],
            onSwitch: (server, _) =>
            {
                switchedTo.TrySetResult(server);
                return Task.CompletedTask;
            },
            onModeFallback: (mode, _) =>
            {
                fellToV2ray.TrySetResult(mode == ConnectionMode.V2rayTCP);
                return Task.CompletedTask;
            },
            options: new GpnProbeOptions
            {
                Samples = 1,
                PerSampleTimeoutMs = 300,
                UseWireGuardHandshakeProbe = false,
            },
            interval: TimeSpan.FromMilliseconds(50),
            cancellationToken: cts.Token), ct);

        var fell = await fellToV2ray.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        fell.Should().BeTrue();

        // Geçiş yapılmamalı — hiçbir sağlıklı aday yok.
        switchedTo.Task.IsCompleted.Should().BeFalse(
            "Sağlıklı aday yokken sunucu değişimi olmamalı");

        cts.Cancel();
        await monitor;
    }

    // ── Aktif ölü + diğer aday Open → geçiş ──────────────────────────────

    [Fact]
    public async Task ActiveDead_OtherOpen_SwitchesToOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // it=Blocked (ölü), de=Open (sağlıklı) → geçiş.
        var checker = new StagedUdpHealthChecker(
            stage: [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open)],
            recovery: [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open)]);
        var service = new GpnServerSelectionService(udpHealthChecker: checker);

        var switchedTo = new TaskCompletionSource<GpnServerProfile?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = Task.Run(() => service.RunFailoverMonitorAsync(
            active: it,
            candidates: [it, de],
            onSwitch: (server, _) =>
            {
                switchedTo.TrySetResult(server);
                return Task.CompletedTask;
            },
            options: new GpnProbeOptions
            {
                Samples = 1,
                PerSampleTimeoutMs = 300,
                UseWireGuardHandshakeProbe = false,
            },
            interval: TimeSpan.FromMilliseconds(50),
            cancellationToken: cts.Token), ct);

        var switched = await switchedTo.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        switched.Should().NotBeNull();
        switched!.ServerId.Should().Be("de");

        cts.Cancel();
        await monitor;
    }

    // ── Katı politika: NoResponse ölü sayılır, Open ile geçiş ────────────

    [Fact]
    public async Task ActiveDead_StrictPolicy_SwitchesToOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // Katı politika: NoResponse=ölü. it=NoResponse → öldü sayılır.
        // de=Open → sağlıklı.
        var checker = new StagedUdpHealthChecker(
            stage: [Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.Open)],
            recovery: [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open)]);
        var service = new GpnServerSelectionService(udpHealthChecker: checker);

        var switchedTo = new TaskCompletionSource<GpnServerProfile?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = Task.Run(() => service.RunFailoverMonitorAsync(
            active: it,
            candidates: [it, de],
            onSwitch: (server, _) =>
            {
                switchedTo.TrySetResult(server);
                return Task.CompletedTask;
            },
            options: new GpnProbeOptions
            {
                Samples = 1,
                PerSampleTimeoutMs = 300,
                UseWireGuardHandshakeProbe = false,
                UdpCheck = new UdpHealthCheckOptions(TreatNoResponseAsBlocked: true),
            },
            interval: TimeSpan.FromMilliseconds(50),
            cancellationToken: cts.Token), ct);

        var switched = await switchedTo.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        switched.Should().NotBeNull();
        switched!.ServerId.Should().Be("de");

        cts.Cancel();
        await monitor;
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────

    private static GpnServerProfile Server(string id, string name) => new(
        ServerId: id,
        Name: name,
        EndpointHost: "127.0.0.1",
        EndpointPort: 51820,
        ServerPublicKey: "5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=",
        ClientPrivateKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        ClientAddress: "10.66.66.2/24");

    private static UdpProbeResult Udp(string id, UdpProbeStatus status) =>
        new(id, status, status == UdpProbeStatus.Open ? 5 : -1, status == UdpProbeStatus.Open);

    /// <summary>
    /// Sahneli sahte UDP health checker — choose
    /// <paramref name="stage"/> sonuçlarını döner, sonra
    /// <paramref name="recovery"/> sonuçlarını döner.
    /// </summary>
    private sealed class StagedUdpHealthChecker : IUdpHealthChecker
    {
        private readonly List<UdpProbeResult> _stage;
        private readonly List<UdpProbeResult> _recovery;
        private int _callCount;

        public StagedUdpHealthChecker(List<UdpProbeResult> stage, List<UdpProbeResult> recovery)
        {
            _stage = stage;
            _recovery = recovery;
        }

        public Task<UdpProbeResult> ProbeAsync(
            string serverId,
            string host,
            int port,
            UdpHealthCheckOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var batch = index < _stage.Count ? _stage : _recovery;
            var result = batch.FirstOrDefault(r => r.ServerId == serverId);
            return Task.FromResult(result);
        }
    }
}
