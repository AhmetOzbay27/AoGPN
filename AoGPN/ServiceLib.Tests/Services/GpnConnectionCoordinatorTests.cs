using ServiceLib;
using ServiceLib.Events;
using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnConnectionCoordinator — mod kararına göre bağlantı kurma ve failover
/// geri çağrılarının orkestrasyonu. Seçim ve launcher sahte (fake) uygulamalarla
/// değiştirilir; gerçek ağ/çekirdek dokunulmaz.
/// </summary>
// ProbeEgressNic statik paylaşılan durum taşır (SetKnownTunnelNames); bu sınıf
// oraya yazdığı için ProbeEgressNicTests ile AYNI koleksiyonda serileşir.
[Collection("probe-egress")]
public class GpnConnectionCoordinatorTests
{
    [Fact]
    public async Task ConnectAsync_WireGuardUdp_LaunchesWireGuardTunnel()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        var snapshot = await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);

        launcher.Launches.Should().ContainSingle();
        launcher.Launches[0].Mode.Should().Be(ConnectionMode.WireGuardUDP);
        launcher.Launches[0].Server.Should().BeSameAs(it);
        snapshot.State.Should().Be(GpnConnectionState.Connected);
        snapshot.Mode.Should().Be(ConnectionMode.WireGuardUDP);
        snapshot.Server.Should().BeSameAs(it);
    }

    [Fact]
    public async Task ConnectAsync_WireGuardForcedHairpin_WarnsAboutNatHairpin()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            // Etkin TÜM sunucular hairpin (kendi genel IP'si) — son çare seçildi.
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, [], HairpinForced: true),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        // Paylaşılan mesaj kanalına (panelin "Mesajlar" sekmesi) yayınlanan uyarıyı yakala.
        var messages = new List<string>();
        using var sub = AppEvents.SendMsgViewRequested.AsObservable().Subscribe(messages.Add);

        var snapshot = await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);

        snapshot.State.Should().Be(GpnConnectionState.Connected);
        launcher.Launches.Should().ContainSingle();
        launcher.Launches[0].Server.Should().BeSameAs(it);
        // Kullanıcıya NAT hairpin gerektiği açıklanmalı — "neden bağlanmıyor" panelde yanıtlanır.
        messages.Should().Contain(m => m.Contains("hairpin", StringComparison.OrdinalIgnoreCase));
        messages.Should().Contain(m => m.Contains("NAT"));
    }

    [Fact]
    public async Task ConnectAsync_WireGuardNotHairpin_NoWarning()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, [], HairpinForced: false),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        var messages = new List<string>();
        using var sub = AppEvents.SendMsgViewRequested.AsObservable().Subscribe(messages.Add);

        var snapshot = await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);

        snapshot.State.Should().Be(GpnConnectionState.Connected);
        messages.Should().NotContain(m => m.Contains("hairpin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConnectAsync_V2rayTcp_LaunchesDefaultV2rayNode()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(null, ConnectionMode.V2rayTCP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        var snapshot = await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);

        launcher.Launches.Should().ContainSingle();
        launcher.Launches[0].Mode.Should().Be(ConnectionMode.V2rayTCP);
        launcher.Launches[0].Server.Should().BeNull();
        snapshot.State.Should().Be(GpnConnectionState.Connected);
        snapshot.Mode.Should().Be(ConnectionMode.V2rayTCP);
    }

    [Fact]
    public async Task ConnectAsync_LaunchFails_StateFailed()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher { LaunchError = new InvalidOperationException("core boom") };
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        var snapshot = await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);

        snapshot.State.Should().Be(GpnConnectionState.Failed);
        snapshot.Error.Should().Contain("core boom");
        launcher.Launches.Should().BeEmpty(); // hata fırlatıldığı için kayıt eklenmedi
    }

    [Fact]
    public async Task FailoverMonitorStart_SetsKnownTunnelName_AndDisconnectClears()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        ProbeEgressNic.SetKnownTunnelNames(null); // temiz başlangıç
        try
        {
            await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);

            // Failover izleyicisi başlarken koordinatör aktif TUN adını teşhise iletir
            // — ilk ölçüm bile ad sezgisel eşleşmesine güvenmeden tüneli tanır.
            ProbeEgressNic.GetKnownTunnelNames().Should().Contain(Global.SingboxTunInterfaceName);

            await coordinator.DisconnectAsync(TestContext.Current.CancellationToken);

            // Tünel kapandı — bilinen ad temizlenir (sezgisel eşleşmeye dönüş).
            ProbeEgressNic.GetKnownTunnelNames().Should().BeNull();
        }
        finally
        {
            ProbeEgressNic.SetKnownTunnelNames(null);
        }
    }

    [Fact]
    public async Task DisconnectAsync_StopsLauncherAndResetsState()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);
        launcher.StopCount.Should().Be(1); // ConnectAsync başındaki temiz başlangıç

        await coordinator.DisconnectAsync(TestContext.Current.CancellationToken);

        launcher.StopCount.Should().Be(2);
        coordinator.Snapshot.State.Should().Be(GpnConnectionState.Disconnected);
    }

    [Fact]
    public async Task FailoverMonitor_SwitchServer_RelaysOnSwitch()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
            // İzleyici ilk çevrimde sunucu değişimi tetikler (onSwitch).
            MonitorBehavior = async (_, _, onSwitch, _, _, ct) => await onSwitch(de, ct),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);
        await coordinator.ActiveMonitorTask;

        launcher.Launches.Should().HaveCount(2);
        launcher.Launches[1].Mode.Should().Be(ConnectionMode.WireGuardUDP);
        launcher.Launches[1].Server.Should().BeSameAs(de);
        coordinator.Snapshot.State.Should().Be(GpnConnectionState.Connected);
        coordinator.Snapshot.Server.Should().BeSameAs(de);
    }

    [Fact]
    public async Task FailoverMonitor_SwitchServer_SoftSwitchKeepsTunnelAlive()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
            // İzleyici ilk çevrimde sunucu değişimi tetikler (onSwitch → de).
            MonitorBehavior = async (_, _, onSwitch, _, _, ct) => await onSwitch(de, ct),
        };
        // Yumuşak geçiş (mihomo GPN-Nodes grubu seçimi) başarılı: çekirdek durmaz.
        var coordinator = new GpnConnectionCoordinator(selector, launcher,
            softSwitch: (_, _) => Task.FromResult(true));

        await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);
        await coordinator.ActiveMonitorTask;

        launcher.Launches.Should().ContainSingle("yumuşak geçiş çekirdeği yeniden başlatmamalı");
        launcher.StopCount.Should().Be(1, "yalnızca ilk bağlantının temiz başlangıcı");
        coordinator.Snapshot.State.Should().Be(GpnConnectionState.Connected);
        coordinator.Snapshot.Server.Should().BeSameAs(de);
    }

    [Fact]
    public async Task FailoverMonitor_SwitchServer_SoftSwitchUnavailable_FallsBackToRestart()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
            MonitorBehavior = async (_, _, onSwitch, _, _, ct) => await onSwitch(de, ct),
        };
        // Yumuşak geçiş desteklenmiyor (ör. çekirdek mihomo değil / eski tek-düğüm
        // config) → eski durdur→başlat davranışı korunmalı.
        var coordinator = new GpnConnectionCoordinator(selector, launcher,
            softSwitch: (_, _) => Task.FromResult(false));

        await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);
        await coordinator.ActiveMonitorTask;

        launcher.Launches.Should().HaveCount(2, "fallback: durdur → başlat");
        launcher.Launches[1].Server.Should().BeSameAs(de);
        coordinator.Snapshot.State.Should().Be(GpnConnectionState.Connected);
        coordinator.Snapshot.Server.Should().BeSameAs(de);
    }

    [Fact]
    public async Task ConnectAsync_WhileConnected_SoftSwitchesPreferredNode_WithoutRestart()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher,
            softSwitch: (_, _) => Task.FromResult(true));

        await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);
        launcher.StopCount.Should().Be(1);
        launcher.Launches.Should().ContainSingle();

        // Bağlıyken kullanıcı başka düğümle Bağlan derse tünel yıkılmaz — seçim değişir.
        var snapshot = await coordinator.ConnectAsync([it, de],
            preferred: de, ct: TestContext.Current.CancellationToken);

        launcher.Launches.Should().ContainSingle("yumuşak geçiş launcher'a dokunmamalı");
        launcher.StopCount.Should().Be(1, "yumuşak geçişte durdurma yok");
        snapshot.State.Should().Be(GpnConnectionState.Connected);
        snapshot.Server.Should().Be(de); // kayıt eşitliği — ikinci aday dizisi yeni örnek taşır
    }

    [Fact]
    public async Task ConnectAsync_WhileConnected_SoftSwitchFails_FallsBackToFullRestart()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher,
            softSwitch: (_, _) => Task.FromResult(false));

        await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);
        launcher.StopCount.Should().Be(1);

        // Yumuşak geçiş başarısız → seçim akışı devam eder (de seçilir), tam yeniden
        // başlatma yapılır — eski davranış birebir korunur.
        selector.Selection = new GpnSelectionResult(de, ConnectionMode.WireGuardUDP, null, []);
        var snapshot = await coordinator.ConnectAsync([it, de],
            preferred: de, ct: TestContext.Current.CancellationToken);

        launcher.StopCount.Should().Be(2);
        launcher.Launches.Should().HaveCount(2);
        launcher.Launches[1].Server.Should().BeSameAs(de);
        snapshot.State.Should().Be(GpnConnectionState.Connected);
        snapshot.Server.Should().BeSameAs(de);
    }

    [Fact]
    public async Task ConnectAsync_WhileDisconnected_PreferredNode_LaunchesNormally()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(de, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher,
            softSwitch: (_, _) => Task.FromResult(true));

        // Bağlı değilken (ilk bağlantı) yumuşak geçiş yolu devreye girmez — normal başlatma.
        var snapshot = await coordinator.ConnectAsync([it, de],
            preferred: de, ct: TestContext.Current.CancellationToken);

        launcher.Launches.Should().ContainSingle();
        launcher.Launches[0].Server.Should().BeSameAs(de);
        launcher.Launches[0].Mode.Should().Be(ConnectionMode.WireGuardUDP);
        snapshot.State.Should().Be(GpnConnectionState.Connected);
    }

    [Fact]
    public async Task FailoverMonitor_FallbackToV2ray_RelaysOnModeFallback()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
            // İzleyici tünel ölümünü algılar → V2rayTCP düşüşü (onModeFallback).
            MonitorBehavior = async (_, _, _, onModeFallback, _, ct) => await onModeFallback(ConnectionMode.V2rayTCP, ct),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);
        await coordinator.ActiveMonitorTask;

        launcher.Launches.Should().HaveCount(2);
        launcher.Launches[1].Mode.Should().Be(ConnectionMode.V2rayTCP);
        launcher.Launches[1].Server.Should().BeNull();
        coordinator.Snapshot.State.Should().Be(GpnConnectionState.Connected);
        coordinator.Snapshot.Mode.Should().Be(ConnectionMode.V2rayTCP);
    }

    [Fact]
    public async Task FailoverMonitor_Recovery_RelaysOnRecover_BackToWireGuard()
    {
        var it = Server("it", "İtalya");
        var de = Server("de", "Almanya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
            // İzleyici V2rayTCP'ye düştükten sonra kurtarma moduna geçer ve
            // sağlıklı sunucu bulununca onRecover(server) ile Tier-2'ye döner.
            MonitorBehavior = async (_, _, _, _, onRecover, ct) => await onRecover(de, ct),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        await coordinator.ConnectAsync([it, de], ct: TestContext.Current.CancellationToken);
        await coordinator.ActiveMonitorTask;

        launcher.Launches.Should().HaveCount(2);
        launcher.Launches[1].Mode.Should().Be(ConnectionMode.WireGuardUDP);
        launcher.Launches[1].Server.Should().BeSameAs(de);
        coordinator.Snapshot.State.Should().Be(GpnConnectionState.Connected);
        coordinator.Snapshot.Mode.Should().Be(ConnectionMode.WireGuardUDP);
        coordinator.Snapshot.Server.Should().BeSameAs(de);
    }

    // ── GpnCoreLauncher.BuildWireGuardProfile ─────────────────────────────

    [Fact]
    public void BuildWireGuardProfile_MapsServerFieldsAndPassesValidation()
    {
        var server = Server("it", "İtalya");

        var node = GpnCoreLauncher.BuildWireGuardProfile(server);

        node.ConfigType.Should().Be(EConfigType.WireGuard);
        // GPN WireGuard mihomo çekirdeğiyle üretilir (sing-box'ın WG endpoint'i
        // IPv6'sız makinede el sıkışamıyor; mihomo canlı doğrulandı — Ağu 2026).
        node.CoreType.Should().Be(ECoreType.mihomo);
        node.Address.Should().Be("127.0.0.1");
        node.Port.Should().Be(51820);
        node.Password.Should().Be(server.ClientPrivateKey);
        node.Network.Should().Be(nameof(ETransport.raw));
        node.GetProtocolExtra().WgPublicKey.Should().Be(server.ServerPublicKey);
        node.GetProtocolExtra().WgInterfaceAddress.Should().Be("10.66.66.2/24");
        // MTU iyileştirmesi (Ağu 2026): 1420'lik eski varsayılan yol sınırını (~1400)
        // aşıp parçalanmaya yol açıyordu; launcher artık Global.GpnRecommendedMtu (1360)
        // ile sınırlar. Daha düşük bir MTU ayarlanırsa ona saygı duyulur.
        node.GetProtocolExtra().WgMtu.Should().Be(Global.GpnRecommendedMtu);
        node.GetProtocolExtra().WgPersistentKeepalive.Should().Be(25);

        // Üretilen profil uygulamanın düğüm doğrulamasından geçmeli
        // (CoreConfigContextBuilder.BuildAll bunu çalıştırır).
        var validation = NodeValidator.Validate(node, ECoreType.mihomo);
        validation.Success.Should().BeTrue(
            string.Join("; ", validation.Errors));
    }

    [Theory]
    [InlineData(0, 1360)]                 // belirsiz → önerilen MTU
    [InlineData(1420, 1360)]              // eski varsayılan → önerilen MTU'ya kırpılır
    [InlineData(1280, 1280)]              // kullanıcı daha düşük ayarlarsa saygı duyulur
    [InlineData(1360, 1360)]              // önerilen değer aynen korunur
    public void BuildWireGuardProfile_ClampsMtuToRecommended(int storedMtu, int expectedMtu)
    {
        var server = Server("it", "İtalya") with { Mtu = storedMtu };

        var node = GpnCoreLauncher.BuildWireGuardProfile(server);

        node.GetProtocolExtra().WgMtu.Should().Be(expectedMtu);
    }

    [Fact]
    public async Task ConnectAsync_ForwardsPreferredServerToSelector()
    {
        var it = Server("it", "İtalya");
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, new FakeLauncher());

        await coordinator.ConnectAsync([it], preferred: it);

        selector.LastPreferred.Should().BeSameAs(it);
    }

    [Fact]
    public async Task ConnectAsync_FailoverMonitorEscapesTunnel_SelectionDoesNot()
    {
        // TUN etkinken failover izleyicisinin probe'ları kendi tünelinin içine
        // yakalanmasın: koordinatör izleyiciye EscapeTunnelForProbes=true geçirir
        // (UDP/TCP fiziksel NIC'e bağlanır, ICMP atlanır). Bağlantı-öncesi seçim
        // ise bayrağı ALMAZ — tünel yokken zaten fiziksel yoldan ölçülür ve
        // dashboard ICMP gösterimi korunur.
        var it = Server("it", "İtalya");
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, new FakeLauncher());

        await coordinator.ConnectAsync([it], options: new GpnProbeOptions());
        await coordinator.ActiveMonitorTask;

        selector.LastSelectionOptions.Should().NotBeNull();
        selector.LastSelectionOptions!.EscapeTunnelForProbes.Should().BeFalse(
            "bağlantı-öncesi seçim tünelden bağımsız ölçer — ICMP gösterimi korunur");
        selector.LastMonitorOptions.Should().NotBeNull();
        selector.LastMonitorOptions!.EscapeTunnelForProbes.Should().BeTrue(
            "failover izleyicisi TUN içine yakalanmamak için fiziksel NIC üzerinden ölçer");
    }

    [Fact]
    public async Task ConnectAsync_WithoutPreferred_ForwardsNull()
    {
        var it = Server("it", "İtalya");
        var selector = new FakeSelector();
        var coordinator = new GpnConnectionCoordinator(selector, new FakeLauncher());

        await coordinator.ConnectAsync([it]);

        selector.LastPreferred.Should().BeNull();
    }

    [Fact]
    public async Task ReconnectCurrentTunnel_OnWireGuardSession_RelaunchesTunnel()
    {
        var it = Server("it", "İtalya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        await coordinator.ConnectAsync([it], ct: TestContext.Current.CancellationToken);
        launcher.Launches.Clear();
        var stopBefore = launcher.StopCount;

        var ok = await coordinator.ReconnectCurrentTunnelAsync(TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        launcher.StopCount.Should().Be(stopBefore + 1, "yeniden başlatma tüneli Stop+Launch eder");
        launcher.Launches.Should().ContainSingle();
        launcher.Launches[0].Mode.Should().Be(ConnectionMode.WireGuardUDP);
        launcher.Launches[0].Server.Should().BeSameAs(it);
        coordinator.Snapshot.State.Should().Be(GpnConnectionState.Connected);
    }

    [Fact]
    public async Task ReconnectCurrentTunnel_OnV2raySession_ReturnsFalse()
    {
        var it = Server("it", "İtalya");
        var launcher = new FakeLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(null, ConnectionMode.V2rayTCP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        await coordinator.ConnectAsync([it], ct: TestContext.Current.CancellationToken);
        launcher.Launches.Clear();
        var stopBefore = launcher.StopCount;

        var ok = await coordinator.ReconnectCurrentTunnelAsync(TestContext.Current.CancellationToken);

        ok.Should().BeFalse();
        launcher.StopCount.Should().Be(stopBefore, "V2rayTCP oturumunda tünele dokunulmaz");
        launcher.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconnectCurrentTunnel_WhileInFlight_ReturnsFalse()
    {
        var it = Server("it", "İtalya");
        var launcher = new GatedLauncher();
        var selector = new FakeSelector
        {
            Selection = new GpnSelectionResult(it, ConnectionMode.WireGuardUDP, null, []),
        };
        var coordinator = new GpnConnectionCoordinator(selector, launcher);

        await coordinator.ConnectAsync([it], ct: TestContext.Current.CancellationToken);
        launcher.GateClosed = true;

        // İlk çağrı Gate'e takılı kalırken (in-flight) ikincisi reddedilmeli.
        var first = coordinator.ReconnectCurrentTunnelAsync(TestContext.Current.CancellationToken);
        var second = await coordinator.ReconnectCurrentTunnelAsync(TestContext.Current.CancellationToken);
        second.Should().BeFalse();

        launcher.Gate.SetResult();
        (await first).Should().BeTrue();
        launcher.LaunchCount.Should().Be(2, "ConnectAsync + tek yeniden başlatma");
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

    private sealed class FakeSelector : IGpnServerSelectionService
    {
        public GpnSelectionResult Selection { get; set; } =
            new(null, ConnectionMode.V2rayTCP, null, []);

        /// <summary>Son SelectBestServerAsync çağrısına iletilen tercih (null = tercih yok).</summary>
        public GpnServerProfile? LastPreferred { get; private set; }

        /// <summary>Son SelectBestServerAsync çağrısına iletilen seçenekler.</summary>
        public GpnProbeOptions? LastSelectionOptions { get; private set; }

        /// <summary>Son RunFailoverMonitorAsync çağrısına iletilen seçenekler.</summary>
        public GpnProbeOptions? LastMonitorOptions { get; private set; }

        public Func<GpnServerProfile, IReadOnlyList<GpnServerProfile>,
            Func<GpnServerProfile, CancellationToken, Task>,
            Func<ConnectionMode, CancellationToken, Task>,
            Func<GpnServerProfile, CancellationToken, Task>,
            CancellationToken, Task>? MonitorBehavior { get; set; }

        public Task<GpnSelectionResult> SelectBestServerAsync(
            IReadOnlyList<GpnServerProfile> servers,
            GpnProbeOptions? options = null,
            GpnServerProfile? preferred = null,
            CancellationToken cancellationToken = default)
        {
            LastPreferred = preferred;
            LastSelectionOptions = options;
            return Task.FromResult(Selection);
        }

        public Task<IReadOnlyList<GpnServerProbeResult>> ProbeAllAsync(
            IReadOnlyList<GpnServerProfile> servers,
            GpnProbeOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GpnServerProbeResult>>([]);

        public GpnFailoverMatrix EvaluateFailoverMatrix(
            GpnServerProfile active,
            IReadOnlyList<GpnServerProfile> candidates,
            IReadOnlyList<GpnServerProbeResult> pingResults,
            IReadOnlyDictionary<string, UdpProbeResult> udpResults,
            GpnProbeOptions? options = null)
            => new([],
                new GpnFailoverMatrixPolicy(false, true, true, GpnFailoverAction.None, null, "fake"),
                new GpnFailoverMatrixPolicy(true, true, true, GpnFailoverAction.None, null, "fake"),
                DateTimeOffset.UtcNow);

        public GpnSelectionPrediction EvaluateSelection(
            IReadOnlyList<GpnServerProfile> servers,
            IReadOnlyList<GpnServerProbeResult> pingResults,
            IReadOnlyDictionary<string, UdpProbeResult> udpResults,
            GpnProbeOptions? options = null,
            IReadOnlySet<string>? hairpinServerIds = null)
            => new(servers.FirstOrDefault(), ConnectionMode.WireGuardUDP, null, "fake");

        public async Task RunFailoverMonitorAsync(
            GpnServerProfile active,
            IReadOnlyList<GpnServerProfile> candidates,
            Func<GpnServerProfile, CancellationToken, Task> onSwitch,
            Func<ConnectionMode, CancellationToken, Task>? onModeFallback = null,
            Func<GpnServerProfile, CancellationToken, Task>? onRecover = null,
            GpnProbeOptions? options = null,
            TimeSpan? interval = null,
            CancellationToken cancellationToken = default)
        {
            LastMonitorOptions = options;
            if (MonitorBehavior is not null)
            {
                await MonitorBehavior(active, candidates, onSwitch,
                    onModeFallback ?? ((_, _) => Task.CompletedTask),
                    onRecover ?? ((_, _) => Task.CompletedTask),
                    cancellationToken);
            }
        }
    }

    private sealed class FakeLauncher : IGpnConnectionLauncher
    {
        public List<(ConnectionMode Mode, GpnServerProfile? Server)> Launches { get; } = [];
        public int StopCount { get; private set; }
        public Exception? LaunchError { get; set; }

        public Task LaunchAsync(ConnectionMode mode, GpnServerProfile? server, CancellationToken ct,
            IReadOnlyList<GpnServerProfile>? gpnCandidates = null)
        {
            if (LaunchError is not null)
            {
                throw LaunchError;
            }
            Launches.Add((mode, server));
            LastCandidates = gpnCandidates;
            return Task.CompletedTask;
        }

        public IReadOnlyList<GpnServerProfile>? LastCandidates { get; private set; }

        public Task StopAsync(CancellationToken ct)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>LaunchAsync'i bir kapıya kadar bekletir — eşzamanlılık senaryoları için.</summary>
    private sealed class GatedLauncher : IGpnConnectionLauncher
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GateClosed { get; set; }
        public int LaunchCount { get; private set; }

        public Task LaunchAsync(ConnectionMode mode, GpnServerProfile? server, CancellationToken ct,
            IReadOnlyList<GpnServerProfile>? gpnCandidates = null)
        {
            LaunchCount++;
            return GateClosed ? Gate.Task : Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
