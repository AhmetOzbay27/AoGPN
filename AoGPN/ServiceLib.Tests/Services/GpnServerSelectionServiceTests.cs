using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ServiceLib.DI;
using ServiceLib.Enums;
using ServiceLib.Events;
using ServiceLib.Models;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Akıllı Düşüş (Smart Fallback) karar mekanizmasının testleri.
/// UdpHealthChecker testleri yerel (loopback) UDP sunucularıyla gerçek ağ
/// yığını üzerinde çalışır — İtalya/Almanya sunucularına dokunmaz.
/// </summary>
// TUN-etkin fallback testi ProbeEgressNic statik durumuna yazar (SetSnapshotForTest);
// o sınıfla aynı koleksiyonda serileşir (paralel çalışma durumu karıştırır).
[Collection("probe-egress")]
public class GpnServerSelectionServiceTests
{
    // ── DecideMode (saf karar mantığı) ────────────────────────────────────

    [Fact]
    public void DecideMode_Open_ReturnsWireGuardUdp()
    {
        var probe = new UdpProbeResult("it", UdpProbeStatus.Open, 12, true);
        GpnDecision.DecideMode(probe).Should().Be(ConnectionMode.WireGuardUDP);
    }

    [Fact]
    public void DecideMode_Blocked_ReturnsV2rayTcp()
    {
        var probe = new UdpProbeResult("it", UdpProbeStatus.Blocked, 3, false, "ConnectionReset");
        GpnDecision.DecideMode(probe).Should().Be(ConnectionMode.V2rayTCP);
    }

    [Fact]
    public void DecideMode_NoResponse_Default_ReturnsWireGuardUdp()
    {
        // Varsayılan politika: WireGuard junk pakete yanıt vermez, sessizlik
        // sağlıklı UDP yolu demektir → Tier 2 korunur.
        var probe = new UdpProbeResult("it", UdpProbeStatus.NoResponse, -1, false, "timeout");
        GpnDecision.DecideMode(probe).Should().Be(ConnectionMode.WireGuardUDP);
    }

    [Fact]
    public void DecideMode_NoResponse_StrictPolicy_ReturnsV2rayTcp()
    {
        // Katı politika (zaman aşımı → fallback) istenirse V2rayTCP'ye düşer.
        var probe = new UdpProbeResult("it", UdpProbeStatus.NoResponse, -1, false, "timeout");
        var options = new UdpHealthCheckOptions(TreatNoResponseAsBlocked: true);
        GpnDecision.DecideMode(probe, options).Should().Be(ConnectionMode.V2rayTCP);
    }

    // ── ToConf — resmi istemci için ASCII güvenli tünel adı ──────────────

    [Fact]
    public void ToConf_NonAsciiDisplayName_EmitsAsciiTunnelName()
    {
        // Resmi WireGuard istemcisi tünel adlarını ^[a-zA-Z0-9_=+.-]{1,32}$ ile
        // sınırlar (upstream conf/name.go); "İtalya" (noktalı İ, U+0130) bu kurala
        // uymaz — içe aktarılamaz ve bu adla kalmış bozuk yapılandırma "The system
        // cannot find the file specified" üretir. .conf dışa aktarımı ASCII güvenli
        // ad taşımalıdır (İ→I).
        var it = Server("it", "İtalya");

        var conf = it.ToConf();

        conf.Should().Contain("# Name = Italya");
        conf.Should().NotContain("İtalya", "Windows istemcisi Unicode tünel adını reddeder");
        it.ConfTunnelName.Should().Be("Italya");
        it.ConfTunnelName.Should().MatchRegex("^[a-zA-Z0-9_=+.-]{1,32}$");
    }

    [Fact]
    public void ToConf_AsciiName_Unchanged()
    {
        var de = Server("de", "Almanya");

        de.ToConf().Should().Contain("# Name = Almanya");
        de.ConfTunnelName.Should().Be("Almanya");
    }

    [Fact]
    public void ToConf_EmptyName_FallsBackToTunnel()
    {
        var anonymous = Server("it", string.Empty);

        anonymous.ConfTunnelName.Should().Be("tunnel");
        anonymous.ToConf().Should().Contain("# Name = tunnel");
    }

    // ── DecideMode — HandshakeNoResponse (canlı testte gözlenen senaryo) ──

    [Fact]
    public void DecideMode_HandshakeNoResponse_Default_ReturnsV2rayTcp()
    {
        // Geçerli el sıkışma yanıtsız: sağlıklı sunucu yanıt verirdi. ICMP kanıtı
        // yok (Blocked'tan farklı) ama yine de güçlü bozukluk işareti → varsayılan
        // politika (TreatHandshakeNoResponseAsBlocked=true) V2rayTCP düşüşü yapar.
        var probe = new UdpProbeResult("it", UdpProbeStatus.HandshakeNoResponse, -1, false, "timeout");
        GpnDecision.DecideMode(probe).Should().Be(ConnectionMode.V2rayTCP);
    }

    [Fact]
    public void DecideMode_HandshakeNoResponse_LenientPolicy_ReturnsWireGuardUdp()
    {
        // Yumuşak politika (riskli/el sıkışma atlanan senaryolar): yanıtsızlık
        // WireGuardUDP'yi korur.
        var probe = new UdpProbeResult("it", UdpProbeStatus.HandshakeNoResponse, -1, false, "timeout");
        var options = new UdpHealthCheckOptions(TreatHandshakeNoResponseAsBlocked: false);
        GpnDecision.DecideMode(probe, options).Should().Be(ConnectionMode.WireGuardUDP);
    }

    [Fact]
    public void DecideMode_HandshakeNoResponse_And_JunkNoResponse_HaveSeparatePolicies()
    {
        // İki teşhis AYRI politikalardır: junk NoResponse (WireGuard junk pakete
        // yanıt vermez — beklenen sessizlik) sağlıklı KALIR; el sıkışma yanıtsızlığı
        // ölü sayılır. Canlı gözlenen karışıklığın kaynağı buydu — ikisi aynı
        // kovada değerlendiriliyordu.
        var junk = new UdpProbeResult("it", UdpProbeStatus.NoResponse, -1, false, "timeout");
        var hs = new UdpProbeResult("it", UdpProbeStatus.HandshakeNoResponse, -1, false, "timeout");
        GpnDecision.DecideMode(junk).Should().Be(ConnectionMode.WireGuardUDP);
        GpnDecision.DecideMode(hs).Should().Be(ConnectionMode.V2rayTCP);
    }

    // ── DecideFailover (UDP destekli failover kararı) ────────────────────

    [Fact]
    public void DecideFailover_ActiveHealthy_NoBetterCandidate_ReturnsNone()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");

        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 40), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.NoResponse)),
            new GpnProbeOptions());

        decision.Action.Should().Be(GpnDecision.FailoverActionType.None);
    }

    [Fact]
    public void DecideFailover_ActiveHealthy_BetterCandidate_ReturnsSwitch()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");

        // de 30ms vs it 80ms — fark 50ms > hysteresis 15ms
        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 80), Ping("de", 30)],
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.NoResponse)),
            new GpnProbeOptions());

        decision.Action.Should().Be(GpnDecision.FailoverActionType.SwitchServer);
        decision.Target!.ServerId.Should().Be("de");
    }

    [Fact]
    public void DecideFailover_ActiveHealthy_BetterButWithinHysteresis_ReturnsNone()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");

        // de 70ms vs it 80ms — fark 10ms < hysteresis 15ms → salınımı önle
        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 80), Ping("de", 70)],
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.NoResponse)),
            new GpnProbeOptions());

        decision.Action.Should().Be(GpnDecision.FailoverActionType.None);
    }

    [Fact]
    public void DecideFailover_ActiveUdpBlocked_OtherHealthy_ReturnsSwitch()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");

        // Aktif tünel öldü (Blocked) → UDP'si sağlıklı de'ye geç
        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 40), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.NoResponse)),
            new GpnProbeOptions());

        decision.Action.Should().Be(GpnDecision.FailoverActionType.SwitchServer);
        decision.Target!.ServerId.Should().Be("de");
        decision.Reason.Should().Contain("UDP ölü");
    }

    [Fact]
    public void DecideFailover_ActiveUdpBlocked_NoHealthyCandidate_ReturnsFallbackToV2ray()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");

        // Her iki sunucuda da UDP ölü → Tier 3 düşüşü
        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 40), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Blocked)),
            new GpnProbeOptions());

        decision.Action.Should().Be(GpnDecision.FailoverActionType.FallbackToV2ray);
    }

    [Fact]
    public void DecideFailover_NoResponse_StrictPolicy_ActiveDead_OtherOpen_ReturnsSwitch()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");
        var options = new GpnProbeOptions { UdpCheck = new UdpHealthCheckOptions(TreatNoResponseAsBlocked: true) };

        // Katı politika: NoResponse = ölü → de'nin Open yanıtına geç
        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 40), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.Open)),
            options);

        decision.Action.Should().Be(GpnDecision.FailoverActionType.SwitchServer);
        decision.Target!.ServerId.Should().Be("de");
    }

    [Fact]
    public void DecideFailover_CandidateWithDeadUdp_NotSwitchTarget_EvenIfPingBetter()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");

        // de ping olarak çok daha iyi AMA UDP'si Blocked → ölü tünele geçilmez
        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 80), Ping("de", 20)],
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.Blocked)),
            new GpnProbeOptions());

        decision.Action.Should().Be(GpnDecision.FailoverActionType.None);
    }

    [Fact]
    public void DecideFailover_ActiveHealthy_NoPingBaseline_ReturnsNone()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");

        // Aktif UDP sağlıklı ama ICMP ölçülemiyor (ISP engeli) — ping tabanı yok,
        // geçiş kararı verilemez (salınım riski).
        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("de", 30)], // it için ping sonucu yok
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.NoResponse)),
            new GpnProbeOptions());

        decision.Action.Should().Be(GpnDecision.FailoverActionType.None);
    }

    // ── DecideRecovery (V2rayTCP sonrası Tier-2 kurtarma kararı) ───────────

    [Fact]
    public void DecideRecovery_HealthyServerOpen_ReturnsIt()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // de'nin UDP'si Open → kurtarma hedefi de.
        var target = GpnDecision.DecideRecovery(
            [it, de],
            [Ping("it", 40), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open)),
            new GpnProbeOptions());

        target.Should().NotBeNull();
        target!.ServerId.Should().Be("de");
    }

    [Fact]
    public void DecideRecovery_NoHealthyCandidate_ReturnsNull()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // Her ikisi de Blocked → kurtarma hedefi yok, Tier-3'te kal.
        var target = GpnDecision.DecideRecovery(
            [it, de],
            [Ping("it", 40), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Blocked)),
            new GpnProbeOptions());

        target.Should().BeNull();
    }

    [Fact]
    public void DecideRecovery_NoResponse_DefaultPolicy_TreatedHealthy()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // Varsayılan politika: WireGuard junk pakete yanıt vermez → NoResponse
        // sağlıklı yoldur; it NoResponse ise kurtarma hedefi it olur.
        var target = GpnDecision.DecideRecovery(
            [it, de],
            [Ping("it", 30), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.Blocked)),
            new GpnProbeOptions());

        target.Should().NotBeNull();
        target!.ServerId.Should().Be("it"); // düşük ping + sağlıklı kabul
    }

    [Fact]
    public void DecideRecovery_DisabledServer_NotChosen()
    {
        var it = Server("it", "İtalya") with { IsEnabled = false };
        var de = Server("de", "Almanya");

        var target = GpnDecision.DecideRecovery(
            [it, de],
            [Ping("it", 20), Ping("de", 60)],
            UdpMap(Udp("it", UdpProbeStatus.Open), Udp("de", UdpProbeStatus.Open)),
            new GpnProbeOptions());

        target.Should().NotBeNull();
        target!.ServerId.Should().Be("de");
    }

    [Fact]
    public void DecideRecovery_UdpDisabled_FallbackToPingOnly()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var options = new GpnProbeOptions { EnableUdpHealth = false };

        // UDP kapalı → tüm sunucular sağlıklı; en düşük ping'li seçilir.
        var target = GpnDecision.DecideRecovery(
            [it, de],
            [Ping("it", 30), Ping("de", 45)],
            new Dictionary<string, UdpProbeResult>(),
            options);

        target.Should().NotBeNull();
        target!.ServerId.Should().Be("it");
    }

    // ── IsPingPongSwitchSuppressed (ping-pong sıçrama koruması) ──────────

    [Fact]
    public void PingPongSuppressed_WithinCooldown_BackToAbandonedServer_True()
    {
        // A→B geçtik 10 sn önce (abandoned=A); karar B→A istiyor → cooldown içinde
        // A'ya geri dönüş ENGELENMELİ (probe fluke'u — tüneli yırtma).
        var now = new DateTime(2026, 8, 30, 12, 0, 0);
        var lastSwitchUtc = now.AddSeconds(-10); // FailoverSwitchCooldownSeconds=45 içinde
        GpnDecision.IsPingPongSwitchSuppressed(
                "it", "it", lastSwitchUtc, now, cooldownSeconds: 45)
            .Should().BeTrue();
    }

    [Fact]
    public void PingPongSuppressed_AfterCooldown_AllowsSwitchToAbandonedServer()
    {
        // Cooldown (45 sn) geçti → terk edilen sunucuya geri dönüş ARTIK serbest
        // (gerçek failover/ölüm senaryosu).
        var now = new DateTime(2026, 8, 30, 12, 0, 0);
        var lastSwitchUtc = now.AddSeconds(-60);
        GpnDecision.IsPingPongSwitchSuppressed(
                "it", "it", lastSwitchUtc, now, cooldownSeconds: 45)
            .Should().BeFalse();
    }

    [Fact]
    public void PingPongSuppressed_DifferentTarget_NotSuppressed()
    {
        // Hedef terk edilen sunucu DEĞİL (taze/üçüncü sunucu) → cooldown içinde bile
        // geçiş engellenmez — sağlıklı yeni adaya geçmek istenir.
        var now = new DateTime(2026, 8, 30, 12, 0, 0);
        var lastSwitchUtc = now.AddSeconds(-5);
        GpnDecision.IsPingPongSwitchSuppressed(
                "it", "de", lastSwitchUtc, now, cooldownSeconds: 45)
            .Should().BeFalse();
    }

    [Fact]
    public void PingPongSuppressed_CooldownDisabled_NotSuppressed()
    {
        // FailoverSwitchCooldownSeconds=0 → koruma kapalı (eski davranış).
        var now = new DateTime(2026, 8, 30, 12, 0, 0);
        var lastSwitchUtc = now.AddSeconds(-5);
        GpnDecision.IsPingPongSwitchSuppressed(
                "it", "it", lastSwitchUtc, now, cooldownSeconds: 0)
            .Should().BeFalse();
    }

    [Fact]
    public void PingPongSuppressed_NoPriorSwitch_NotSuppressed()
    {
        // Henüz değişim yok (abandoned=null) → ilk geçiş serbest.
        var now = new DateTime(2026, 8, 30, 12, 0, 0);
        GpnDecision.IsPingPongSwitchSuppressed(
                null, "it", DateTime.MinValue, now, cooldownSeconds: 45)
            .Should().BeFalse();
    }

    // ── Gerçek failover döngüsü: V2rayTCP düşüşü sonrası Tier-2 kurtarma ──

    [Fact]
    public async Task RunFailoverMonitor_FallsToV2ray_ThenRecoversToWireGuard()
    {
        var ct = TestContext.Current.CancellationToken;
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // İlk çevrimde her iki sunucu da Blocked (tünel ölü) → V2rayTCP düşüşü.
        // Ardından de Open oluyor → onRecover(de) ile Tier-2 kurtarma.
        var checker = new StagedUdpHealthChecker(
            stage: AllBlocked,   // çevrim 1: tümü ölü → V2rayTCP düşüşü
            recovery: DeGreen);  // kurtarma: de sağlıklı → Tier-2 dönüşü
        var service = new GpnServerSelectionService(udpHealthChecker: checker);

        var fellToV2ray = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredServer = new TaskCompletionSource<GpnServerProfile?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = Task.Run(() => service.RunFailoverMonitorAsync(
            active: it,
            candidates: [it, de],
            onSwitch: (_, _) => Task.CompletedTask,
            onModeFallback: (mode, _) =>
            {
                fellToV2ray.TrySetResult(mode == ConnectionMode.V2rayTCP);
                return Task.CompletedTask;
            },
            onRecover: (server, _) =>
            {
                recoveredServer.TrySetResult(server);
                return Task.CompletedTask;
            },
            options: new GpnProbeOptions
            {
                Samples = 1,
                PerSampleTimeoutMs = 300,
                UseWireGuardHandshakeProbe = false, // saf junk-UDP yolu (senaryo basitliği)
            },
            interval: TimeSpan.FromMilliseconds(50),
            cancellationToken: cts.Token), ct);

        // Önce V2rayTCP düşüşü beklenir, ardından Tier-2 kurtarma.
        (await fellToV2ray.Task).Should().BeTrue();
        var recovered = await recoveredServer.Task;
        recovered.Should().NotBeNull();
        recovered!.ServerId.Should().Be("de");
        cts.Cancel();
        await monitor;
    }

    [Fact]
    public async Task RunFailoverMonitor_PublishesResilienceEvents_OnFallbackAndRecovery()
    {
        var ct = TestContext.Current.CancellationToken;
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // İlk çevrim: tümü ölü → UdpDeath + ModeFallback olayları; sonra de Open
        // → Recover olayı. Tümü AppEvents üzerinden yayınlanmalı.
        var checker = new StagedUdpHealthChecker(stage: AllBlocked, recovery: DeGreen);
        var service = new GpnServerSelectionService(udpHealthChecker: checker);

        var received = new List<GpnResilienceEvent>();
        using var sub = AppEvents.GpnResilienceChanged.AsObservable().Subscribe(received.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var monitor = Task.Run(() => service.RunFailoverMonitorAsync(
            active: it,
            candidates: [it, de],
            onSwitch: (_, _) => Task.CompletedTask,
            onModeFallback: (_, _) => Task.CompletedTask,
            onRecover: (_, _) => Task.CompletedTask,
            options: new GpnProbeOptions
            {
                Samples = 1,
                PerSampleTimeoutMs = 300,
                UseWireGuardHandshakeProbe = false,
            },
            interval: TimeSpan.FromMilliseconds(50),
            cancellationToken: cts.Token), ct);

        // UdpDeath + ModeFallback + Recover üç olayın gelmesini bekle.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (received.Count(r => r.Action is GpnResilienceAction.UdpDeath or GpnResilienceAction.ModeFallback or GpnResilienceAction.Recover) < 3
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }

        received.Should().ContainSingle(e => e.Action == GpnResilienceAction.UdpDeath);
        received.Should().ContainSingle(e => e.Action == GpnResilienceAction.ModeFallback);
        received.Should().ContainSingle(e => e.Action == GpnResilienceAction.Recover);
        received.Should().Contain(e => e.Action == GpnResilienceAction.ModeFallback && e.ToMode == ConnectionMode.V2rayTCP);
        received.Should().Contain(e => e.Action == GpnResilienceAction.Recover && e.ServerId == "de");
        cts.Cancel();
        await monitor;
    }

    [Fact]
    public async Task RunFailoverMonitor_RecoveryWatchDisabled_DoesNotRecover()
    {
        var ct = TestContext.Current.CancellationToken;
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        // Adaylar ölü (Blocked) → V2rayTCP düşüşü; ardından de Open olsa bile
        // kurtarma izleyicisi kapalı olduğundan onRecover ÇAĞRILMAZ — düşüş terminaldir.
        var checker = new StagedUdpHealthChecker(stage: AllBlocked, recovery: DeGreen);
        var service = new GpnServerSelectionService(udpHealthChecker: checker);

        var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var monitor = Task.Run(() => service.RunFailoverMonitorAsync(
            active: it,
            candidates: [it, de],
            onSwitch: (_, _) => Task.CompletedTask,
            onModeFallback: (_, _) => Task.CompletedTask,
            onRecover: (server, _) =>
            {
                recovered.TrySetResult(true);
                return Task.CompletedTask;
            },
            options: new GpnProbeOptions
            {
                Samples = 1,
                PerSampleTimeoutMs = 300,
                UseWireGuardHandshakeProbe = false,
                EnableRecoveryWatch = false, // kullanıcı kurtarma izleyicisini kapattı
            },
            interval: TimeSpan.FromMilliseconds(50),
            cancellationToken: cts.Token), ct);

        // EnableRecoveryWatch=false → düşüş terminaldir; monitor döner, kurtarma olmaz.
        await monitor.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        recovered.Task.IsCompleted.Should().BeFalse("EnableRecoveryWatch=false iken onRecover çağrılmamalı");
    }

    // ── DecideFailover / DecideRecovery — el sıkışma yanıtsızlığı ─────────

    [Fact]
    public void DecideFailover_HandshakeNoResponse_Default_ActiveDead_SwitchesToOpenCandidate()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");
        var options = new GpnProbeOptions(); // TreatHandshakeNoResponseAsBlocked=true varsayılan

        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 30), Ping("de", 31)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.Open)),
            options);

        // Aktif el sıkışma yanıtsız → ölü; Open aday var → geç.
        decision.Action.Should().Be(GpnDecision.FailoverActionType.SwitchServer);
        decision.Target!.ServerId.Should().Be("de");
    }

    [Fact]
    public void DecideFailover_HandshakeNoResponse_Lenient_ActiveHealthy_NoSwitch()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");
        var options = new GpnProbeOptions
        {
            UdpCheck = new UdpHealthCheckOptions(TreatHandshakeNoResponseAsBlocked: false),
        };

        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 30), Ping("de", 31)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.Open)),
            options);

        // Yumuşak politika: el sıkışma yanıtsızlığı sağlıklı → aktif korunur
        // (de 31ms, hysteresis 15ms: 31 < 30-15=15 değil).
        decision.Action.Should().Be(GpnDecision.FailoverActionType.None);
    }

    [Fact]
    public void DecideFailover_HandshakeNoResponse_Default_NoHealthyCandidate_FallsBack()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");
        var options = new GpnProbeOptions();

        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 30), Ping("de", 31)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.HandshakeNoResponse)),
            options);

        decision.Action.Should().Be(GpnDecision.FailoverActionType.FallbackToV2ray);
    }

    [Fact]
    public void DecideRecovery_HandshakeNoResponse_Default_TreatedDead()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var options = new GpnProbeOptions();

        // it el sıkışma yanıtsız (ölü) + de Blocked → sağlıklı aday yok → null.
        var target = GpnDecision.DecideRecovery(
            [it, de],
            [Ping("it", 30), Ping("de", 40)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.Blocked)),
            options);
        target.Should().BeNull();

        // de Open olsaydı kurtarma hedefi de olurdu.
        var target2 = GpnDecision.DecideRecovery(
            [it, de],
            [Ping("it", 30), Ping("de", 40)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.Open)),
            options);
        target2!.ServerId.Should().Be("de");
    }

    [Fact]
    public void DecideFailover_UdpDisabled_FallsBackToPingOnlyBehavior()
    {
        var active = Server("it", "İtalya");
        var other = Server("de", "Almanya");
        var options = new GpnProbeOptions { EnableUdpHealth = false };

        // UDP kapalı → eski ping-tabanlı davranış: fark hysteresis'i aşarsa geç
        var decision = GpnDecision.DecideFailover(
            active, [active, other],
            [Ping("it", 80), Ping("de", 30)],
            new Dictionary<string, UdpProbeResult>(),
            options);

        decision.Action.Should().Be(GpnDecision.FailoverActionType.SwitchServer);
        decision.Target!.ServerId.Should().Be("de");
    }

    // ── ProbeUdpAsync — el sıkışma yanıtsızlığı teşhis zinciri ────────────

    [Fact]
    public async Task ProbeUdpAsync_HandshakeNoResponse_BothSilent_ReturnsHandshakeDiagnosis()
    {
        // Canlı gözlenen senaryo: geçerli el sıkışma yanıtsız + junk probe de sessiz
        // (ICMP kanıtı yok). Sonuç NoResponse DEĞİL HandshakeNoResponse olmalı ve
        // varsayılan katı handshake politikası V2rayTCP düşüşü yapmalı.
        var it = Server("it", "İtalya");
        var service = new GpnServerSelectionService(
            udpHealthChecker: new FixedUdpHealthChecker(Udp("it", UdpProbeStatus.NoResponse)),
            wireGuardProbe: new FixedHandshakeProbe(
                new UdpProbeResult("it", UdpProbeStatus.HandshakeNoResponse, -1, false,
                    "timeout after 2 attempts (700ms) — geçerli el sıkışma yanıtsız")));

        var probe = await service.ProbeUdpAsync(it, new GpnProbeOptions(), TestContext.Current.CancellationToken);

        probe.Status.Should().Be(UdpProbeStatus.HandshakeNoResponse);
        probe.Detail.Should().Contain("ICMP kanıtı yok");
        probe.Detail.Should().Contain("el sıkışma yanıtsız");
        GpnDecision.DecideMode(probe).Should().Be(ConnectionMode.V2rayTCP);
    }

    [Fact]
    public async Task ProbeUdpAsync_HandshakeNoResponse_JunkBlocked_ReturnsBlockedWithChainDetail()
    {
        // Junk probe ICMP bloklu kanıtı verirse kesin Blocked'a yükseltilir ve
        // el sıkışma yanıtsızlığı da zincire not düşülür.
        var it = Server("it", "İtalya");
        var service = new GpnServerSelectionService(
            udpHealthChecker: new FixedUdpHealthChecker(
                new UdpProbeResult("it", UdpProbeStatus.Blocked, 3, false, "ConnectionReset")),
            wireGuardProbe: new FixedHandshakeProbe(
                new UdpProbeResult("it", UdpProbeStatus.HandshakeNoResponse, -1, false, "timeout")));

        var probe = await service.ProbeUdpAsync(it, new GpnProbeOptions(), TestContext.Current.CancellationToken);

        probe.Status.Should().Be(UdpProbeStatus.Blocked);
        probe.Detail.Should().Contain("ConnectionReset");
        probe.Detail.Should().Contain("el sıkışma da yanıtsızdı");
        GpnDecision.DecideMode(probe).Should().Be(ConnectionMode.V2rayTCP);
    }

    [Fact]
    public async Task ProbeUdpAsync_HandshakeNoResponse_JunkOpen_ReturnsOpen()
    {
        // Junk probe bir yanıtlayıcı bulursa (Open) o kanıt döner — portta konuşan
        // bir hizmet var demektir.
        var it = Server("it", "İtalya");
        var service = new GpnServerSelectionService(
            udpHealthChecker: new FixedUdpHealthChecker(Udp("it", UdpProbeStatus.Open)),
            wireGuardProbe: new FixedHandshakeProbe(
                new UdpProbeResult("it", UdpProbeStatus.HandshakeNoResponse, -1, false, "timeout")));

        var probe = await service.ProbeUdpAsync(it, new GpnProbeOptions(), TestContext.Current.CancellationToken);

        probe.Status.Should().Be(UdpProbeStatus.Open);
        GpnDecision.DecideMode(probe).Should().Be(ConnectionMode.WireGuardUDP);
    }

    [Fact]
    public async Task ProbeUdpAsync_HandshakeOpen_ShortCircuitsWithoutJunk()
    {
        // El sıkışma Open (kesin kanıt) → junk probe HİÇ çalıştırılmaz.
        var it = Server("it", "İtalya");
        var junkCalls = 0;
        var service = new GpnServerSelectionService(
            udpHealthChecker: new CountingUdpHealthChecker(() => junkCalls++, Udp("it", UdpProbeStatus.NoResponse)),
            wireGuardProbe: new FixedHandshakeProbe(Udp("it", UdpProbeStatus.Open)));

        var probe = await service.ProbeUdpAsync(it, new GpnProbeOptions(), TestContext.Current.CancellationToken);

        probe.Status.Should().Be(UdpProbeStatus.Open);
        junkCalls.Should().Be(0);
    }

    // ── ProbeAllAsync — TUN etkinken ICMP yerine UDP el sıkışma gecikmesi ──

    [Fact]
    public async Task ProbeAllAsync_TunnelActive_UdpHandshakeDelayFallback()
    {
        // TUN etkinken ICMP atlanır (Ping arayüz bağlayamaz) ve TCP anlamsızdır —
        // gecikme, fiziksel NIC üzerinden geçerli el sıkışmaya verilen yanıtın RTT'si
        // ile ölçülür. Dashboard "—" yerine gerçek değer görür.
        var it = Server("it", "İtalya");
        var service = new GpnServerSelectionService(
            wireGuardProbe: new FixedHandshakeProbe(
                new UdpProbeResult("it", UdpProbeStatus.Open, 42, true, "handshake_response")));

        ProbeEgressNic.SetSnapshotForTest(new ProbeEgressNic.Snapshot(true, "Ethernet", 1, 1));
        try
        {
            var results = await service.ProbeAllAsync(
                [it],
                new GpnProbeOptions { Samples = 3, PerSampleTimeoutMs = 100, EscapeTunnelForProbes = true },
                TestContext.Current.CancellationToken);

            var probe = results.Single();
            probe.IsSuccess.Should().BeTrue();
            probe.DelayMs.Should().Be(42, "her örnek UDP el sıkışma RTT'sini ölçer");
            probe.AvgDelayMs.Should().Be(42);
            probe.MaxDelayMs.Should().Be(42);
            probe.LossPercent.Should().Be(0);
        }
        finally
        {
            ProbeEgressNic.SetSnapshotForTest(null);
        }
    }

    [Fact]
    public async Task ProbeAllAsync_TunnelActive_UdpHandshakeNoResponse_ReturnsFailure()
    {
        // El sıkışmaya yanıt yoksa (HandshakeNoResponse) gecikme ölçülemez — sonuç
        // başarısız (kayıp %100); yanıltıcı değer üretilmez.
        var it = Server("it", "İtalya");
        var service = new GpnServerSelectionService(
            wireGuardProbe: new FixedHandshakeProbe(
                new UdpProbeResult("it", UdpProbeStatus.HandshakeNoResponse, -1, false, "timeout")));

        ProbeEgressNic.SetSnapshotForTest(new ProbeEgressNic.Snapshot(true, "Ethernet", 1, 1));
        try
        {
            var results = await service.ProbeAllAsync(
                [it],
                new GpnProbeOptions { Samples = 3, PerSampleTimeoutMs = 100, EscapeTunnelForProbes = true },
                TestContext.Current.CancellationToken);

            var probe = results.Single();
            probe.IsSuccess.Should().BeFalse();
            probe.DelayMs.Should().Be(-1);
            probe.LossPercent.Should().Be(100);
        }
        finally
        {
            ProbeEgressNic.SetSnapshotForTest(null);
        }
    }

    [Fact]
    public async Task ProbeAllAsync_NoTunnel_IgnoresUdpFallback()
    {
        // Tünel YOKKEN (bayrak açık olsa bile) davranış eskisi gibi: ICMP ölçülür.
        // Loopback adresine ICMP başarılı olursa gerçek gecikme döner — fallback
        // tetiklenmez.
        var it = Server("it", "İtalya");
        var service = new GpnServerSelectionService(
            wireGuardProbe: new FixedHandshakeProbe(
                new UdpProbeResult("it", UdpProbeStatus.Open, 42, true, "handshake_response")));

        ProbeEgressNic.SetSnapshotForTest(new ProbeEgressNic.Snapshot(false, null, null, null));
        try
        {
            var results = await service.ProbeAllAsync(
                [it],
                new GpnProbeOptions { Samples = 1, PerSampleTimeoutMs = 100, EscapeTunnelForProbes = true },
                TestContext.Current.CancellationToken);

            var probe = results.Single();
            // Tünel yok → ICMP yolu (loopback) denenir; fallback (UDP RTT) kullanılmaz.
            probe.IsSuccess.Should().BeTrue();
            probe.DelayMs.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            ProbeEgressNic.SetSnapshotForTest(null);
        }
    }

    // ── SelectBestServerAsync — kullanıcı tercihi (preferred) önceliği ──

    [Fact]
    public async Task SelectBestServerAsync_PreferredHealthy_SelectsItWithoutMeasuringOthers()
    {
        // Kullanıcı İtalya'yı seçtiyse otomatik ölçüm YAPILMAZ: yalnızca İtalya
        // ölçülür (junk probe 1 kez) ve sağlıklıysa doğrudan seçilir — Almanya
        // hiç ölçülmez.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var junkCalls = 0;
        var service = new GpnServerSelectionService(
            udpHealthChecker: new CountingUdpHealthChecker(() => junkCalls++, Udp("it", UdpProbeStatus.NoResponse)),
            wireGuardProbe: new FixedHandshakeProbe(Udp("it", UdpProbeStatus.NoResponse)),
            ownPublicIpProvider: () => null);

        var result = await service.SelectBestServerAsync(
            [it, de],
            preferred: it,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Best.Should().Be(it);
        result.Mode.Should().Be(ConnectionMode.WireGuardUDP);
        junkCalls.Should().Be(1, "yalnızca tercih edilen sunucu ölçülür; diğer adaylar ölçülmez");
    }

    [Fact]
    public async Task SelectBestServerAsync_PreferredBlocked_FallsBackToSmartSelection()
    {
        // Seçili sunucu (İtalya) bloklu → uygun değil; Akıllı Düşüş tüm adayları
        // ölçer ve UDP'si açık olan Almanya'yı seçer.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var batch = new List<UdpProbeResult> { Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open) };
        var service = new GpnServerSelectionService(
            udpHealthChecker: new StagedUdpHealthChecker(batch, batch),
            wireGuardProbe: new FixedHandshakeProbe(Udp("x", UdpProbeStatus.NoResponse)),
            ownPublicIpProvider: () => null);

        var result = await service.SelectBestServerAsync(
            [it, de],
            preferred: it,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Best.Should().Be(de, "tercih edilen bloklu — Akıllı Düşüş UDP-açık adayı seçer");
        result.Mode.Should().Be(ConnectionMode.WireGuardUDP);
    }

    [Fact]
    public async Task SelectBestServerAsync_PreferredNotInCandidates_FallsBackToSmartSelection()
    {
        // Tercih edilen sunucu aday listesinde yoksa (ör. devre dışı ya da eşleşme
        // yok) tercih yok sayılır ve tam otomatik ölçüm çalışır.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var ghost = Server("xx", "Hayalet");
        var batch = new List<UdpProbeResult> { Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open) };
        var service = new GpnServerSelectionService(
            udpHealthChecker: new StagedUdpHealthChecker(batch, batch),
            wireGuardProbe: new FixedHandshakeProbe(Udp("x", UdpProbeStatus.NoResponse)),
            ownPublicIpProvider: () => null);

        var result = await service.SelectBestServerAsync(
            [it, de],
            preferred: ghost,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Best.Should().Be(de, "tercih adaylarda değil — otomatik ölçüm çalışır");
        result.Mode.Should().Be(ConnectionMode.WireGuardUDP);
    }

    // ── GuardOwnIpArtifact — tünel kalıntısı hairpin teşhisini bozmasın ──
    //
    // Canlı olay (31 Ağu 2026): "kendi genel IP" önceki GPN oturumunda tünel
    // İÇİNDEN çözülünce sunucunun kendi IP'si (92.4.220.236) önbelleğe yazıldı.
    // Bağlantı sonrası o IP ile eşleşen sunucu yanlışlıkla "hairpin" sanılıp
    // atlanıyor, seçim V2rayTCP fallback'e düşüyordu. GuardOwnIpArtifact bu
    // tünel kalıntısını ayıklar: çözülen IP etkin bir sunucunun uç noktasıyla
    // eşleşiyorsa güvenilir değildir → hairpin teşhisi o turda devre dışı.

    [Fact]
    public async Task SelectBestServerAsync_OwnIpEqualsServerEndpoint_TunnelArtifact_PreferredStillTried()
    {
        // Tercih edilen sunucunun uç noktası, önbellekteki "kendi genel IP"ye
        // eşit olsa bile (tünel kalıntısı) hairpin sanılıp ATLANMAZ — guard IP'yi
        // ayıklar, tercih edilen sunucu gerçekten ölçülür ve seçilir.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var ownIp = "127.0.0.1"; // Server() uç noktasıyla eşleşen "kendi IP" (tünel kalıntısı)

        // Ham hairpin kontrolü bu IP'yi hairpin OLARAK işaretlerdi — guard olmadan
        // tercih edilen sunucu atlanırdı. Guard'ın devreye girdiğini kanıtlar.
        GpnHairpinDetector.IsHairpin(it, ownIp).Should().BeTrue();

        var junkCalls = 0;
        var service = new GpnServerSelectionService(
            udpHealthChecker: new CountingUdpHealthChecker(() => junkCalls++, Udp("it", UdpProbeStatus.NoResponse)),
            wireGuardProbe: new FixedHandshakeProbe(Udp("it", UdpProbeStatus.NoResponse)),
            ownPublicIpProvider: () => ownIp);

        var result = await service.SelectBestServerAsync(
            [it, de],
            preferred: it,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Best.Should().Be(it, "tünel kalıntısı hairpin olarak yorumlanmamalı — tercih edilen sunucu denenir");
        result.Mode.Should().Be(ConnectionMode.WireGuardUDP);
        junkCalls.Should().Be(1, "tercih edilen sunucu ölçülür (hairpin yüzünden atlanmaz)");
    }

    [Fact]
    public async Task SelectBestServerAsync_OwnIpEqualsServerEndpoint_TunnelArtifact_SmartFallbackNotSkipped()
    {
        // Akıllı Düşüş yolunda da aynı koruma: "kendi IP" bir sunucunun uç
        // noktasına eşitse o sunucu hairpin sanılıp aday sırasından atılmaz.
        // Guard yokken de (ownIp == de uç noktası) hairpin sayılıp de ATLANIR,
        // it de bloklu olduğundan V2rayTCP'ye düşülürdü. Guard ile de seçilir.
        var it = Server("it", "İtalya") with { EndpointHost = "127.0.0.1" };
        var de = Server("de", "Almanya") with { EndpointHost = "127.0.0.2" }; // farklı loopback — sadece de eşleşir
        var ownIp = "127.0.0.2";

        // Ham kontrol de'yi hairpin işaretlerdi — guard olmadan de atlanırdı.
        GpnHairpinDetector.IsHairpin(de, ownIp).Should().BeTrue();
        GpnHairpinDetector.IsHairpin(it, ownIp).Should().BeFalse();

        var batch = new List<UdpProbeResult> { Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open) };
        var service = new GpnServerSelectionService(
            udpHealthChecker: new StagedUdpHealthChecker(batch, batch),
            wireGuardProbe: new FixedHandshakeProbe(Udp("x", UdpProbeStatus.NoResponse)),
            ownPublicIpProvider: () => ownIp);

        var result = await service.SelectBestServerAsync(
            [it, de],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Best.Should().Be(de, "tünel kalıntısı hairpin olarak yorumlanmamalı — UDP-açık aday seçilir");
        result.Mode.Should().Be(ConnectionMode.WireGuardUDP);
    }

    // ── EvaluateFailoverMatrix — varsayılan vs katı yan yana ─────────────

    [Fact]
    public void EvaluateFailoverMatrix_NoResponse_DefaultKeeps_StrictFallsBack()
    {
        // Aynı ölçüm (iki sunucu da NoResponse): varsayılan politika aktif tüneli
        // korur (WireGuard junk sessizliği sağlıklı), katı politika V2rayTCP'ye düşer.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var service = new GpnServerSelectionService();

        var matrix = service.EvaluateFailoverMatrix(
            it, [it, de],
            [Ping("it", 30), Ping("de", 40)],
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.NoResponse)));

        matrix.DefaultPolicy.Action.Should().Be(GpnFailoverAction.None);
        matrix.DefaultPolicy.NoResponseAsBlocked.Should().BeFalse();
        matrix.StrictPolicy.Action.Should().Be(GpnFailoverAction.FallbackToV2ray);
        matrix.StrictPolicy.NoResponseAsBlocked.Should().BeTrue();

        matrix.Rows.Should().HaveCount(2);
        matrix.Rows.Should().OnlyContain(r => r.HealthyDefault && !r.HealthyStrict,
            "NoResponse varsayılanda sağlıklı, katıda ölü");
    }

    [Fact]
    public void EvaluateFailoverMatrix_HandshakeNoResponse_ActiveDead_SwitchesToOpenCandidate_BothPolicies()
    {
        // El sıkışma yanıtsızlığı İKİ politikada da ölü sayılır (katı varsayılan) —
        // aktif ölü, de Open → her iki politikada da SwitchServer→de.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var service = new GpnServerSelectionService();

        var matrix = service.EvaluateFailoverMatrix(
            it, [it, de],
            [Ping("it", 30), Ping("de", 40)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.Open)));

        matrix.DefaultPolicy.Action.Should().Be(GpnFailoverAction.SwitchServer);
        matrix.DefaultPolicy.TargetServerId.Should().Be("de");
        matrix.StrictPolicy.Action.Should().Be(GpnFailoverAction.SwitchServer);
        matrix.StrictPolicy.TargetServerId.Should().Be("de");

        matrix.Rows.Should().ContainSingle(r => r.ServerId == "it" && r.IsActive && !r.HealthyDefault);
        matrix.Rows.Should().ContainSingle(r => r.ServerId == "de" && !r.IsActive && r.HealthyDefault && r.HealthyStrict);
    }

    [Fact]
    public void EvaluateFailoverMatrix_ActiveFlagAndOrdering()
    {
        // Aktif satır önce, ardından ping'e göre; UdpStatus satıra taşınır.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var service = new GpnServerSelectionService();

        var matrix = service.EvaluateFailoverMatrix(
            it, [de, it],
            [Ping("it", 80), Ping("de", 20)],
            UdpMap(Udp("it", UdpProbeStatus.Open), Udp("de", UdpProbeStatus.Open)));

        matrix.Rows[0].ServerId.Should().Be("it"); // aktif önce
        matrix.Rows[0].IsActive.Should().BeTrue();
        matrix.Rows[0].UdpStatus.Should().Be(UdpProbeStatus.Open);
        matrix.Rows[0].PingMs.Should().Be(80);
        matrix.Rows[1].ServerId.Should().Be("de");
    }

    [Fact]
    public void EvaluateFailoverMatrix_UdpDisabled_RowsHealthyBoth_PingOnlyDecision()
    {
        // EnableUdpHealth=false → sağlık bayrakları iki politikada da true; karar
        // ping tabanlı (de 40 > 30-15 hysteresis → geçiş yok).
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var service = new GpnServerSelectionService();

        var matrix = service.EvaluateFailoverMatrix(
            it, [it, de],
            [Ping("it", 30), Ping("de", 40)],
            new Dictionary<string, UdpProbeResult>(),
            new GpnProbeOptions { EnableUdpHealth = false });

        matrix.DefaultPolicy.UdpHealthEnabled.Should().BeFalse();
        matrix.DefaultPolicy.Action.Should().Be(GpnFailoverAction.None);
        matrix.Rows.Should().OnlyContain(r => r.HealthyDefault && r.HealthyStrict);
    }

    // ── DecideSelection — "en iyi aday" kartı (SelectBestServerAsync ile birebir) ──

    [Fact]
    public void DecideSelection_BestPingOpenUdp_WireGuardUdp()
    {
        // GPN Bağlan'ın seçimi: en düşük ping'li aday (it) + UDP Open → Tier 2.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var prediction = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 25), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.Open), Udp("de", UdpProbeStatus.Open)));

        prediction.Best.Should().Be(it);
        prediction.Mode.Should().Be(ConnectionMode.WireGuardUDP);
        prediction.UdpProbe!.Status.Should().Be(UdpProbeStatus.Open);
        prediction.Reason.Should().Contain("ping 25ms");
    }

    [Fact]
    public void DecideSelection_BestPingBlockedUdp_SecondServerOpen_SelectsSecond_WireGuardUdp()
    {
        // Akıllı Düşüş BUGFIX'i (canlı doğrulama): en düşük ping'li adayın (it) UDP'si
        // bloklu ama ikinci adayın (de) UDP'si açık → seçim de'ye çevrilir, WireGuardUDP
        // korunur. Eski davranış yalnızca it'i deneyip doğrudan V2rayTCP'ye düşüyordu.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var prediction = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 25), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open)));

        prediction.Best.Should().Be(de);
        prediction.Mode.Should().Be(ConnectionMode.WireGuardUDP);
        prediction.UdpProbe!.Status.Should().Be(UdpProbeStatus.Open);
        prediction.Reason.Should().Contain("ping 45ms");
    }

    [Fact]
    public void DecideSelection_AllUdpDead_FallsBackToV2ray_NullBest()
    {
        // Tüm adayların UDP'si ölüyse (bloklu) hiçbir aday seçilemez → Best=null,
        // V2rayTCP düşüşü (eski davranış: bloklu aday Best olarak kalıyordu).
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var prediction = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 25), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Blocked)));

        prediction.Best.Should().BeNull();
        prediction.Mode.Should().Be(ConnectionMode.V2rayTCP);
        prediction.Reason.Should().Contain("UDP yolu");
    }

    [Fact]
    public void DecideSelection_NoPingButUdpOpen_SelectsFirstOpenServer()
    {
        // ICMP tamamen engelli (ISP politikası) — gerçek akışın Adım 2b'si:
        // ilk UDP-açık sunucu seçilir (ping'e bakılmaz).
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var prediction = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", -1), Ping("de", -1)],
            UdpMap(Udp("it", UdpProbeStatus.NoResponse), Udp("de", UdpProbeStatus.Open)));

        prediction.Best.Should().Be(de);
        prediction.Mode.Should().Be(ConnectionMode.WireGuardUDP);
        prediction.UdpProbe!.Status.Should().Be(UdpProbeStatus.Open);
    }

    [Fact]
    public void DecideSelection_NoPingNoUdpOpen_NullBest_V2ray()
    {
        // Hiçbir sunucu seçilemez → Best=null, V2rayTCP düşüşü öngörülür.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var prediction = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", -1), Ping("de", -1)],
            UdpMap(Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Blocked)));

        prediction.Best.Should().BeNull();
        prediction.Mode.Should().Be(ConnectionMode.V2rayTCP);
        prediction.Reason.Should().Contain("UDP yolu");
    }

    [Fact]
    public void DecideSelection_HandshakeNoResponse_StrictDefaultTriesNext_LenientKeepsBestPing()
    {
        // Canlı senaryo (29 Ağu 2026): en düşük ping'li adayın el sıkışması yanıtsız
        // ama ikinci adayın UDP'si AÇIK → katı varsayılan politikada seçim de'ye
        // kayar (V2rayTCP'ye düşmez!); gevşek politikada it korunur.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var strict = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 25), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.Open)));
        strict.Best.Should().Be(de);
        strict.Mode.Should().Be(ConnectionMode.WireGuardUDP);

        var lenient = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 25), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.Open)),
            new GpnProbeOptions
            {
                UdpCheck = new UdpHealthCheckOptions { TreatHandshakeNoResponseAsBlocked = false },
            });
        lenient.Best.Should().Be(it);
        lenient.Mode.Should().Be(ConnectionMode.WireGuardUDP);
    }

    [Fact]
    public void DecideSelection_BestPingDeadSecondDead_StrictFallsBack_LenientKeepsBestPing()
    {
        // İki aday da el sıkışma yanıtsız: katı → V2rayTCP düşüşü (Best=null);
        // gevşek → en düşük ping'li aday korunur (riskli politika).
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var strict = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 25), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.HandshakeNoResponse)));
        strict.Best.Should().BeNull();
        strict.Mode.Should().Be(ConnectionMode.V2rayTCP);

        var lenient = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 25), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse), Udp("de", UdpProbeStatus.HandshakeNoResponse)),
            new GpnProbeOptions
            {
                UdpCheck = new UdpHealthCheckOptions { TreatHandshakeNoResponseAsBlocked = false },
            });
        lenient.Best.Should().Be(it);
        lenient.Mode.Should().Be(ConnectionMode.WireGuardUDP);
    }

    [Fact]
    public void DecideSelection_DisabledServer_NeverSelected()
    {
        // Devre dışı sunucu en iyi ping'e sahip olsa bile asla aday olamaz.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya") with { IsEnabled = false };

        var prediction = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 80), Ping("de", 10)],
            UdpMap(Udp("it", UdpProbeStatus.Open), Udp("de", UdpProbeStatus.Open)));

        prediction.Best.Should().Be(it);
        prediction.Mode.Should().Be(ConnectionMode.WireGuardUDP);
    }

    // ── Hairpin (tünel-içi öz-erişim) teşhisi ────────────────────────────

    [Fact]
    public void IsHairpin_SameEndpointAndOwnIp_ReturnsTrue()
    {
        // Tünel içinden kendi sunucusuna erişim: hedef sunucunun genel IP'si makinenin
        // kendi genel IP'siyle aynı → hairpin. (Canlı gözlenen senaryo: makine İtalya
        // tünelinin içindeyken İtalya sunucusuna ulaşmaya çalışmak.)
        var server = Server("it", "İtalya") with { EndpointHost = "92.4.220.236" };
        GpnHairpinDetector.IsHairpin(server, "92.4.220.236").Should().BeTrue();
    }

    [Theory]
    [InlineData("92.4.220.236", "130.61.223.36")]  // farklı sunucu → hayır
    [InlineData("92.4.220.236", null)]               // kendi IP bilinmiyor → hayır
    [InlineData("92.4.220.236", "")]                // boş → hayır
    [InlineData("92.4.220.236", "  ")]              // boşluk → hayır
    [InlineData("not-an-ip", "92.4.220.236")]       // hedef IP ayrıştırılamıyor → hayır
    [InlineData("92.4.220.236", "not-an-ip")]       // kendi IP ayrıştırılamıyor → hayır
    public void IsHairpin_NoMatch_ReturnsFalse(string endpoint, string? ownIp)
    {
        var server = Server("it", "İtalya") with { EndpointHost = endpoint };
        GpnHairpinDetector.IsHairpin(server, ownIp).Should().BeFalse();
    }

    [Fact]
    public void OrderCandidates_HairpinMovesLast_DespiteBestPing()
    {
        // Hairpin aday EN DÜŞÜK ping'e sahip olsa bile (tünel-içi rota yanıltıcıdır)
        // aday sırasının SONUNA alınır — sağlıklı diğer adaylar önce denenir.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var ordered = GpnDecision.OrderCandidates(
            [it, de],
            [Ping("it", 10), Ping("de", 45)],
            new HashSet<string> { "it" }).ToArray();

        ordered[0].ServerId.Should().Be("de"); // hairpin olmayan önce
        ordered[1].ServerId.Should().Be("it"); // hairpin sona atıldı
    }

    [Fact]
    public void OrderCandidates_NoHairpin_PingOrderPreserved()
    {
        // Hairpin yoksa sıralama ping-tabanlı kalır (düşük gecikme önce).
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var ordered = GpnDecision.OrderCandidates(
            [it, de],
            [Ping("it", 45), Ping("de", 10)],
            new HashSet<string>()).ToArray();

        ordered[0].ServerId.Should().Be("de");
        ordered[1].ServerId.Should().Be("it");
    }

    [Fact]
    public void DecideSelection_HairpinServer_Deprioritized()
    {
        // Canlı senaryo (29 Ağu 2026): makine İtalya tünelinin İÇİNDEYKEN İtalya
        // sunucusu hairpin (öz-erişim) — düşük ping'e rağmen aday sırasının sonunda.
        // Almanya'nın UDP'si de açık olduğundan Almanya seçilir, İtalya değil.
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");

        var prediction = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 10), Ping("de", 45)],
            UdpMap(Udp("it", UdpProbeStatus.Open), Udp("de", UdpProbeStatus.Open)),
            hairpinServerIds: new HashSet<string> { "it" });

        prediction.Best.Should().Be(de);
        prediction.Mode.Should().Be(ConnectionMode.WireGuardUDP);
        prediction.UdpProbe!.Status.Should().Be(UdpProbeStatus.Open);
    }

    [Fact]
    public void DecideSelection_HairpinOnlyServer_StillFallbackCandidate()
    {
        // Yalnızca hairpin aday varsa seçim yine de onun UDP durumuna göre yapılır
        // (son umut) — hairpin sıralamayı etkiler, seçimi imkânsızlaştırmaz.
        var it = Server("it", "İtalya");

        var prediction = GpnDecision.DecideSelection(
            [it],
            [Ping("it", 10)],
            UdpMap(Udp("it", UdpProbeStatus.Open)),
            hairpinServerIds: new HashSet<string> { "it" });

        prediction.Best.Should().Be(it);
        prediction.Mode.Should().Be(ConnectionMode.WireGuardUDP);
    }

    [Fact]
    public void DecideSelection_HairpinLooksHealthy_NonHairpinAlternative_DoesNotPickHairpin()
    {
        // Canlı düşüş kaynağı (30 Ağu 2026): hairpin (öz-erişim, kendi genel IP'si)
        // sunucunun UDP ölçümü yanıltıcı "Open" döner ama NAT hairpin olmayan
        // yönlendiricide el sıkışma asla tamamlanmaz. Non-hairpin alternatif (Almanya)
        // mevcutken hairpin (İtalya) HİÇ seçilmemeli — Almanya ölüyse bile V2rayTCP
        // düşüşü, yanıltıcı hairpin'e tercih edilir.
        var it = Server("it", "İtalya"); // hairpin — UDP "Open" görünüyor (yanıltıcı)
        var de = Server("de", "Almanya"); // non-hairpin — UDP ölü

        var prediction = GpnDecision.DecideSelection(
            [it, de],
            [Ping("it", 5), Ping("de", 40)],
            UdpMap(Udp("it", UdpProbeStatus.Open), Udp("de", UdpProbeStatus.Blocked)),
            hairpinServerIds: new HashSet<string> { "it" });

        prediction.Mode.Should().Be(ConnectionMode.V2rayTCP,
            "non-hairpin alternatif varken hairpin seçilmez — yanıltıcı Open'a rağmen V2rayTCP düşüşü doğru");
        prediction.Best.Should().BeNull();
    }

    [Fact]
    public void DecideFailover_HairpinLooksHealthy_NonHairpinAlternative_FallsBackInsteadOfHairpin()
    {
        // Aktif (Almanya, non-hairpin) tünel öldü; hairpin (İtalya) UDP "Open" görünüyor.
        // Non-hairpin alternatifi (aktif) mevcut olduğundan hairpin geçiş hedefi olamaz
        // → V2rayTCP düşüşü (hairpin'e geçip el sıkışmanın ölmesine izin verilmez).
        var de = Server("de", "Almanya"); // aktif, non-hairpin, UDP ölü
        var it = Server("it", "İtalya");    // hairpin, UDP "Open" (yanıltıcı)

        var options = new GpnProbeOptions
        {
            EnableUdpHealth = true,
            UdpCheck = new UdpHealthCheckOptions
            {
                TreatNoResponseAsBlocked = true,
                TreatHandshakeNoResponseAsBlocked = true,
            },
        };
        var decision = GpnDecision.DecideFailover(
            de, [de, it],
            [Ping("de", 40), Ping("it", 5)],
            UdpMap(Udp("de", UdpProbeStatus.Blocked), Udp("it", UdpProbeStatus.Open)),
            options,
            hairpinServerIds: new HashSet<string> { "it" });

        decision.Action.Should().Be(GpnDecision.FailoverActionType.FallbackToV2ray,
            "active ölü + yalnızca hairpin sağlıklı → hairpin'e geçilmez, V2rayTCP'ye düşülür");
        decision.Target.Should().BeNull();
    }

    [Fact]
    public void DecideFailover_HairpinHealthy_NonHairpinAltHealthy_PrefersNonHairpin()
    {
        // Aktif (Fransa, non-hairpin) öldü; hem non-hairpin (Almanya) hem hairpin
        // (İtalya) UDP sağlıklı. Non-hairpin alternatif varken hairpin hedef olamaz
        // → Almanya'ya (non-hairpin) geçilir, İtalya (hairpin) seçilmez.
        var fr = Server("fr", "Fransa"); // aktif, ölü, non-hairpin
        var de = Server("de", "Almanya"); // sağlıklı, non-hairpin
        var it = Server("it", "İtalya");    // sağlıklı, hairpin (kendi genel IP'si)

        var options = new GpnProbeOptions
        {
            EnableUdpHealth = true,
            UdpCheck = new UdpHealthCheckOptions
            {
                TreatNoResponseAsBlocked = true,
                TreatHandshakeNoResponseAsBlocked = true,
            },
        };
        var decision = GpnDecision.DecideFailover(
            fr, [fr, de, it],
            [Ping("fr", 30), Ping("de", 10), Ping("it", 5)],
            UdpMap(Udp("fr", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open), Udp("it", UdpProbeStatus.Open)),
            options,
            hairpinServerIds: new HashSet<string> { "it" });

        decision.Action.Should().Be(GpnDecision.FailoverActionType.SwitchServer);
        decision.Target!.ServerId.Should().Be("de", "non-hairpin (Almanya) hairpin'e (İtalya) tercih edilir");
    }

    [Fact]
    public void DecideRecovery_HairpinLooksHealthy_NonHairpinAlternative_ReturnsNull()
    {
        // V2rayTCP'den kurtarılırken yalnızca hairpin sağlıklıysa ve non-hairpin
        // alternatif varsa Tier-2 dönüşü YAPILMAZ (hairpin el sıkışma almaz).
        var de = Server("de", "Almanya");
        var it = Server("it", "İtalya");

        var options = new GpnProbeOptions
        {
            EnableUdpHealth = true,
            UdpCheck = new UdpHealthCheckOptions
            {
                TreatNoResponseAsBlocked = true,
                TreatHandshakeNoResponseAsBlocked = true,
            },
        };
        var target = GpnDecision.DecideRecovery(
            [de, it],
            [Ping("de", 40), Ping("it", 5)],
            UdpMap(Udp("de", UdpProbeStatus.Blocked), Udp("it", UdpProbeStatus.Open)),
            options,
            hairpinServerIds: new HashSet<string> { "it" });

        target.Should().BeNull("non-hairpin alternatif varken hairpin kurtarma hedefi olmaz");
    }

    // ── Yüksek-gecikme toleransı: yanlış 'ölü' algısını azalt ───────────

    [Fact]
    public void DecideFailover_HighPingHandshakeNoResponse_NotTreatedDead()
    {
        // Aktif sunucunun ping'i SlowServerToleranceMs (varsayılan 400) ÜSTÜNDE ve UDP
        // sonucu HandshakeNoResponse — kısa probe penceresi uzak/yoğun sunucu için yetersiz
        // kalabilir; tünel 'ölü' sayılmaz, gereksiz failover/kopma tetiklenmez.
        var it = Server("it", "İtalya");
        var options = new GpnProbeOptions { SlowServerToleranceMs = 400 };

        var decision = GpnDecision.DecideFailover(
            it, [it],
            [Ping("it", 600)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse)),
            options);

        decision.Action.Should().Be(GpnDecision.FailoverActionType.None,
            "yüksek ping + HandshakeNoResponse 'ölü' sayılmamalı — false-failover engellenir");
    }

    [Fact]
    public void DecideFailover_LowPingHandshakeNoResponse_StillTreatedDead()
    {
        // Ping toleransın ALTINDA (100 < 400) → HandshakeNoResponse YİNE ölü sayılır
        // (tolerans yalnızca gerçekten yüksek gecikme için; varsayılan katı davranış korunur).
        var it = Server("it", "İtalya");
        var decision = GpnDecision.DecideFailover(
            it, [it],
            [Ping("it", 100)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse)),
            new GpnProbeOptions { SlowServerToleranceMs = 400 });

        decision.Action.Should().Be(GpnDecision.FailoverActionType.FallbackToV2ray,
            "düşük ping + HandshakeNoResponse hâlâ ölü → V2rayTCP düşüşü");
    }

    [Fact]
    public void DecideSelection_HighPingHandshakeNoResponse_SelectedWireGuard_NotV2ray()
    {
        // Tek aday yüksek ping'li ve HandshakeNoResponse — tolerans devrede olduğundan
        // seçim yanlış V2rayTCP'ye düşmez, WireGuardUDP kalır.
        var it = Server("it", "İtalya");
        var prediction = GpnDecision.DecideSelection(
            [it],
            [Ping("it", 600)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse)),
            new GpnProbeOptions { SlowServerToleranceMs = 400 });

        prediction.Mode.Should().Be(ConnectionMode.WireGuardUDP,
            "yüksek ping + HandshakeNoResponse toleransla ölü sayılmaz → WireGuardUDP kalır");
        prediction.Best!.ServerId.Should().Be("it");
    }

    [Fact]
    public void DecideSelection_LowPingHandshakeNoResponse_StillDropsToV2ray()
    {
        var it = Server("it", "İtalya");
        var prediction = GpnDecision.DecideSelection(
            [it],
            [Ping("it", 100)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse)),
            new GpnProbeOptions { SlowServerToleranceMs = 400 });

        prediction.Mode.Should().Be(ConnectionMode.V2rayTCP,
            "düşük ping + HandshakeNoResponse toleranssız ölü → V2rayTCP düşer");
    }

    [Fact]
    public void DecideRecovery_HighPingHandshakeNoResponse_TargetReturned_NotSkipped()
    {
        // Kurtarma: yüksek ping'li adayın HandshakeNoResponse'u toleransla sağlıklı sayılarak
        // Tier-2 (WireGuard) dönüşü kaçırılmaz.
        var it = Server("it", "İtalya");
        var target = GpnDecision.DecideRecovery(
            [it],
            [Ping("it", 600)],
            UdpMap(Udp("it", UdpProbeStatus.HandshakeNoResponse)),
            new GpnProbeOptions { SlowServerToleranceMs = 400 });

        target.Should().NotBeNull("yüksek ping + HandshakeNoResponse toleransla sağlıklı → kurtarma hedefi olur");
        target!.ServerId.Should().Be("it");
    }

    // ── ResolveHairpinServerIdsAsync — önbellek (TTL içinde yeniden kullanım) ──

    [Fact]
    public async Task ResolveHairpinServerIds_CachesResult_WithinTtl()
    {
        // Aynı aday kümesi + TTL içinde ikinci çağrı, kendi-genel-IP çözümünü/øn taramayı
        // TEKRARLAMAZ — provider yalnızca bir kez çağrılır.
        var calls = 0;
        string? Provider() { calls++; return "92.4.220.236"; }
        var svc = new GpnServerSelectionService(ownPublicIpProvider: Provider);
        var it = Server("it", "İtalya") with { EndpointHost = "92.4.220.236" };
        var de = Server("de", "Almanya");

        var first = await svc.ResolveHairpinServerIdsAsync([it, de], TestContext.Current.CancellationToken);
        first.Should().Contain("it");
        first.Should().NotContain("de");

        var second = await svc.ResolveHairpinServerIdsAsync([it, de], TestContext.Current.CancellationToken);
        second.Should().Equal(first);

        calls.Should().Be(1, "aynı aday kümesi + TTL içinde hairpin kimlikleri önbellekten yeniden kullanılmalı");
    }

    [Fact]
    public async Task ResolveHairpinServerIds_OwnIpUnresolved_ReturnsEmpty_AndCaches()
    {
        // Kendi genel IP'si çözülemezse → boş küme döner ve BAŞARISIZ sonuç da TTL
        // boyunca önbelleklenir (IP servisine döngüsel yük olmaz).
        var calls = 0;
        string? Provider() { calls++; return null; }
        var svc = new GpnServerSelectionService(ownPublicIpProvider: Provider);
        var it = Server("it", "İtalya") with { EndpointHost = "92.4.220.236" };

        var result = await svc.ResolveHairpinServerIdsAsync([it], TestContext.Current.CancellationToken);
        result.Should().BeEmpty("kendi IP çözülemezse hairpin tespiti devre dışı (boş küme)");

        await svc.ResolveHairpinServerIdsAsync([it], TestContext.Current.CancellationToken);
        calls.Should().Be(1, "başarısız çözüm de TTL içinde yeniden denenmez");
    }

    [Fact]
    public async Task ResolveHairpinServerIds_CandidateSetChange_InvalidatesCache()
    {
        // Aday kimliği (sunucu listesi) değiştiğinde anahtar farklılaşır → yeniden çözülür.
        var calls = 0;
        string? Provider() { calls++; return "92.4.220.236"; }
        var svc = new GpnServerSelectionService(ownPublicIpProvider: Provider);
        var it = Server("it", "İtalya") with { EndpointHost = "92.4.220.236" };
        var de = Server("de", "Almanya");

        await svc.ResolveHairpinServerIdsAsync([it, de], TestContext.Current.CancellationToken);
        calls.Should().Be(1);

        var re = await svc.ResolveHairpinServerIdsAsync([it], TestContext.Current.CancellationToken);
        calls.Should().Be(2, "aday listesi değişince önbellek geçersiz kılınmalı");
        re.Should().Contain("it");
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

    private static GpnServerProbeResult Ping(string id, int delayMs) => new(
        id, delayMs, delayMs, delayMs, 0, delayMs > 0);

    private static UdpProbeResult Udp(string id, UdpProbeStatus status) => new(
        id, status, status == UdpProbeStatus.Open ? 5 : -1, status == UdpProbeStatus.Open);

    private static IReadOnlyDictionary<string, UdpProbeResult> UdpMap(params UdpProbeResult[] results)
        => results.ToDictionary(r => r.ServerId);

    // Hepsi ölü (Blocked) senaryosu — V2rayTCP düşüşünü tetiklemek için.
    private static List<UdpProbeResult> AllBlocked => [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Blocked)];

    // Kurtarma senaryosu — Almanya sağlıklı (Open).
    private static List<UdpProbeResult> DeGreen => [Udp("it", UdpProbeStatus.Blocked), Udp("de", UdpProbeStatus.Open)];

    /// <summary>
    /// Sahneli sahte UDP health checker: belirli sayıda çağrıda <paramref name="stage"/>
    /// sonuçlarını döner, ardından tüm kalan çağrılarda <paramref name="recovery"/>
    /// sonuçlarını döner. V2rayTCP düşüşü (tümü ölü) sonrası Tier-2 kurtarmayı
    /// (bir sunucu sağlıklı) test etmek için kullanılır.
    /// </summary>
    /// <summary>Sabit sonuç döndüren sahte UDP health checker (teşhis zinciri testleri).</summary>
    private sealed class FixedUdpHealthChecker : IUdpHealthChecker
    {
        private readonly UdpProbeResult _result;

        public FixedUdpHealthChecker(UdpProbeResult result) => _result = result;

        public Task<UdpProbeResult> ProbeAsync(
            string serverId, string host, int port,
            UdpHealthCheckOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    /// <summary>Sabit sonuç döndüren sahte el sıkışma probe'u.</summary>
    private sealed class FixedHandshakeProbe : IWireGuardHandshakeProbe
    {
        private readonly UdpProbeResult _result;

        public FixedHandshakeProbe(UdpProbeResult result) => _result = result;

        public Task<UdpProbeResult> ProbeAsync(
            string serverId, string host, int port,
            string? serverPublicKeyBase64, string? clientPrivateKeyBase64,
            WireGuardHandshakeProbeOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    /// <summary>Çağrı sayısı sayan sarmalayıcı (junk probe'un atlandığını doğrulamak için).</summary>
    private sealed class CountingUdpHealthChecker : IUdpHealthChecker
    {
        private readonly Action _onCall;
        private readonly UdpProbeResult _result;

        public CountingUdpHealthChecker(Action onCall, UdpProbeResult result)
        {
            _onCall = onCall;
            _result = result;
        }

        public Task<UdpProbeResult> ProbeAsync(
            string serverId, string host, int port,
            UdpHealthCheckOptions? options = null, CancellationToken cancellationToken = default)
        {
            _onCall();
            return Task.FromResult(_result);
        }
    }

    private sealed class StagedUdpHealthChecker : IUdpHealthChecker
    {
        private readonly int _stageLength;
        private readonly List<UdpProbeResult> _stage;
        private readonly List<UdpProbeResult> _recovery;
        private int _callCount;

        public StagedUdpHealthChecker(List<UdpProbeResult> stage, List<UdpProbeResult> recovery)
        {
            _stage = stage;
            _recovery = recovery;
            _stageLength = stage.Count;
        }

        public Task<UdpProbeResult> ProbeAsync(
            string serverId,
            string host,
            int port,
            UdpHealthCheckOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var batch = index < _stageLength ? _stage : _recovery;
            var result = batch.FirstOrDefault(r => r.ServerId == serverId);
            return Task.FromResult(result);
        }
    }

    // ── UdpHealthChecker — gerçek loopback UDP yığını ─────────────────────

    [Fact]
    public async Task UdpHealthChecker_OpenPort_ReturnsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var echo = await StartEchoServerAsync(ct);
        var port = ((IPEndPoint)echo.Client.LocalEndPoint).Port;

        var checker = new UdpHealthChecker();
        var result = await checker.ProbeAsync("it", "127.0.0.1", port, new UdpHealthCheckOptions(WaitTimeoutMs: 2000), ct);

        result.Status.Should().Be(UdpProbeStatus.Open);
        result.IsReachable.Should().BeTrue();
        result.RoundTripMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task UdpHealthChecker_SilentPort_ReturnsNoResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        using var silent = await StartSilentServerAsync(ct);
        var port = ((IPEndPoint)silent.Client.LocalEndPoint).Port;

        var checker = new UdpHealthChecker();
        var result = await checker.ProbeAsync("de", "127.0.0.1", port, new UdpHealthCheckOptions(WaitTimeoutMs: 800), ct);

        // Port açık (dinleyici var) ama yanıt vermiyor → WireGuard davranışı gibi sessiz.
        result.Status.Should().Be(UdpProbeStatus.NoResponse);
        result.IsReachable.Should().BeFalse();
    }

    [Fact]
    public async Task UdpHealthChecker_ClosedPort_FakeIcmpEvidence_ReturnsBlocked()
    {
        // Windows ICMP rate-limit'ine bağımlılık YOK: gerçek port-unreachable
        // üretimi yerine sahte ICMP kanıtı — bağlı UDP soketinin ConnectionReset
        // yüzeyi (ICMP Port Unreachable'ın Windows karşılığı) enjekte edilir ve
        // Blocked deterministik üretilir. Ağ, IP, gerçek port hiç kullanılmaz.
        var ct = TestContext.Current.CancellationToken;
        var checker = new UdpHealthChecker(_ => new FakeIcmpUnreachableSocket(SocketError.ConnectionReset));

        var result = await checker.ProbeAsync("it", "10.0.0.1", 51820,
            new UdpHealthCheckOptions(WaitTimeoutMs: 500), ct);

        result.Status.Should().Be(UdpProbeStatus.Blocked);
        result.IsReachable.Should().BeFalse();
        result.Detail.Should().Contain("ConnectionReset");
    }

    // ── DI kayıtları ──────────────────────────────────────────────────────

    [Fact]
    public void GpnServices_ResolveFromServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddAoGpnGpnServices();
        using var provider = services.BuildServiceProvider();

        var selector = provider.GetRequiredService<IGpnServerSelectionService>();
        selector.Should().BeOfType<GpnServerSelectionService>();

        var udp = provider.GetRequiredService<IUdpHealthChecker>();
        udp.Should().BeOfType<UdpHealthChecker>();

        // Singleton kayıtlar: aynı örnek döner.
        provider.GetRequiredService<IGpnServerSelectionService>().Should().BeSameAs(selector);
        provider.GetRequiredService<IUdpHealthChecker>().Should().BeSameAs(udp);
        provider.GetRequiredService<NodePingCoordinator>()
            .Should().BeSameAs(provider.GetRequiredService<NodePingCoordinator>());
    }

    // ── ProbeAllAsync — dashboard ölçüm panelinin temeli ────────────────────

    [Fact]
    public async Task ProbeAllAsync_ReturnsResultPerServer_InInputOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var it = Server("it", "İtalya") with { EndpointHost = "127.0.0.1" };
        var de = Server("de", "Almanya") with { EndpointHost = "127.0.0.1" };

        // Host tarafı (ProbeGpnServersAsync) gibi: Samples=3, loopback ICMP.
        var results = await new GpnServerSelectionService().ProbeAllAsync(
            [it, de],
            new GpnProbeOptions
            {
                Samples = 3,
                PerSampleTimeoutMs = 1000,
            },
            ct);

        // Her sunucu için bir sonuç, giriş sırası korunur, gecikme ölçüldü.
        results.Should().HaveCount(2);
        results[0].ServerId.Should().Be("it");
        results[1].ServerId.Should().Be("de");
        results[0].IsSuccess.Should().BeTrue($"it result: {results[0]}; de result: {results[1]}");
        results[0].DelayMs.Should().BeGreaterThanOrEqualTo(0, $"it result: {results[0]}");
        results[0].LossPercent.Should().Be(0);
        results[1].IsSuccess.Should().BeTrue($"de result: {results[1]}");
    }

    [Fact]
    public async Task PingLoopback_InsideTestHost_Works()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(IPAddress.Loopback, 1000,
            Encoding.ASCII.GetBytes("aogpn-gpn-probe-0123456789ab"), new PingOptions(64, true));
        reply.Status.Should().Be(IPStatus.Success, $"loopback ping: {reply.Status} ({reply.RoundtripTime}ms)");
    }

    [Fact]
    public async Task ProbeAllAsync_UnreachableHost_ReportsFailure_NotThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var bogus = Server("bogus", "Yok") with { EndpointHost = "203.0.113.9" }; // TEST-NET-3, erişilemez

        // Ölçülemeyen sunucu sonucu başarısız işaretler; ProbeAllAsync çökmez.
        var results = await new GpnServerSelectionService().ProbeAllAsync(
            [bogus],
            new GpnProbeOptions
            {
                Samples = 1,
                PerSampleTimeoutMs = 400,
                Mode = GpnProbeMode.Icmp,
            },
            ct);

        results.Should().HaveCount(1);
        results[0].IsSuccess.Should().BeFalse();
        results[0].DelayMs.Should().Be(-1);
        results[0].LossPercent.Should().Be(100);
    }

    // ── Yerel test sunucuları ─────────────────────────────────────────────

    private static async Task<UdpClient> StartEchoServerAsync(CancellationToken ct)
    {
        var server = new UdpClient(AddressFamily.InterNetwork);
        server.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var res = await server.ReceiveAsync(ct);
                    await server.SendAsync(res.Buffer, res.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // normal kapanış
            }
            catch (ObjectDisposedException)
            {
                // sunucu dispose edildi
            }
            catch
            {
                // diğer hatalar testi çökertmesin
            }
        });
        return server;
    }

    private static async Task<UdpClient> StartSilentServerAsync(CancellationToken ct)
    {
        var server = new UdpClient(AddressFamily.InterNetwork);
        server.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    _ = await server.ReceiveAsync(ct); // al, yanıt verme
                }
            }
            catch
            {
                // normal kapanış / iptal
            }
        });
        return server;
    }

}
