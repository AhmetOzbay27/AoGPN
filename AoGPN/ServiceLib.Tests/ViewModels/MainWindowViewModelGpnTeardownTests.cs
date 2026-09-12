using AwesomeAssertions;
using ServiceLib.Services;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

/// <summary>
/// MainWindowViewModel'in GPN modundan normal (VLESS/SS gibi WireGuard dışı) bir
/// profile geçişte GPN koordinatörünü teardown ettiğini doğrular.
///
/// Karar, ağır UI bağımlılıkları / çekirdek başlatma olmadan deterministik olarak
/// test edilebilmesi için <see cref="MainWindowViewModel.StopGpnCoordinatorWhenNotConnectedAsync"/>
/// içine alınmıştır; Reload akışı gerçekte bu metodu çağırır.
/// </summary>
public class MainWindowViewModelGpnTeardownTests
{
    private sealed class FakeGpnCoordinator : IGpnConnectionCoordinator
    {
        public int DisconnectCount;
        public int ConnectCount;
        private readonly GpnConnectionSnapshot _snapshot;

        public FakeGpnCoordinator(GpnConnectionSnapshot? snapshot = null)
        {
            _snapshot = snapshot ?? GpnConnectionSnapshot.Idle();
        }

        public GpnConnectionSnapshot Snapshot => _snapshot;

        public IObservable<GpnConnectionSnapshot> Snapshots =>
            Observable.Never<GpnConnectionSnapshot>();

        public Task<GpnConnectionSnapshot> ConnectAsync(
            IReadOnlyList<GpnServerProfile> candidates,
            GpnProbeOptions? options = null,
            GpnServerProfile? preferred = null,
            CancellationToken ct = default)
        {
            ConnectCount++;
            return Task.FromResult(GpnConnectionSnapshot.Idle());
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            DisconnectCount++;
            return Task.CompletedTask;
        }

        public Task<bool> ReconnectCurrentTunnelAsync(CancellationToken ct)
            => Task.FromResult(false);
    }

    [Fact]
    public async Task GpnToVlessSwitch_StopsActiveCoordinator()
    {
        // Önce GPN'deyken koordinatör kuruldu (ConnectCount). Ardından seçili profil
        // VLESS/SS'e değişti → gpnConnected = false → koordinatör durdurulmalıdır.
        var coordinator = new FakeGpnCoordinator();
        await coordinator.ConnectAsync(Array.Empty<GpnServerProfile>(), ct: TestContext.Current.CancellationToken);

        await MainWindowViewModel.StopGpnCoordinatorWhenNotConnectedAsync(
            gpnConnected: false,
            coordinator);

        coordinator.ConnectCount.Should().Be(1, "önce GPN bağlantısı kurulmuştu");
        coordinator.DisconnectCount.Should().Be(1, "VLESS/SS'e geçişte koordinatör durur");
    }

    [Fact]
    public async Task StillOnGpn_KeepsCoordinatorRunning()
    {
        // gpnConnected = true (seçili profil WireGuard + TUN açık) → teardown yok.
        var coordinator = new FakeGpnCoordinator();

        await MainWindowViewModel.StopGpnCoordinatorWhenNotConnectedAsync(
            gpnConnected: true,
            coordinator);

        coordinator.DisconnectCount.Should().Be(0, "GPN aktifken koordinatör durmaz");
    }

    [Fact]
    public async Task NoCoordinator_IsNoOp()
    {
        // Hiç koordinatör kurulmamış (null) → güvenli no-op.
        await MainWindowViewModel.StopGpnCoordinatorWhenNotConnectedAsync(
            gpnConnected: false,
            coordinator: null);
    }

    // ── Aktif tünel teardown'u (StopGpnTunnelIfActiveAsync) ──

    [Fact]
    public async Task StopGpnTunnel_WhenCoordinatorConnected_Disconnects()
    {
        var coordinator = new FakeGpnCoordinator(new GpnConnectionSnapshot(
            GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, null, "WireGuard", DateTimeOffset.UtcNow));

        await MainWindowViewModel.StopGpnTunnelIfActiveAsync(coordinator);

        coordinator.DisconnectCount.Should().Be(1, "bağlı tünel kesmede durdurulur");
    }

    [Fact]
    public async Task StopGpnTunnel_WhenCoordinatorConnecting_Disconnects()
    {
        var coordinator = new FakeGpnCoordinator(new GpnConnectionSnapshot(
            GpnConnectionState.Connecting, ConnectionMode.WireGuardUDP, null, "ölçülüyor", DateTimeOffset.UtcNow));

        await MainWindowViewModel.StopGpnTunnelIfActiveAsync(coordinator);

        coordinator.DisconnectCount.Should().Be(1, "bağlanıyor durumundaki tünel de durdurulur");
    }

    [Fact]
    public async Task StopGpnTunnel_WhenCoordinatorIdle_IsNoOp()
    {
        var coordinator = new FakeGpnCoordinator(); // Disconnected (Idle)

        await MainWindowViewModel.StopGpnTunnelIfActiveAsync(coordinator);

        coordinator.DisconnectCount.Should().Be(0, "kopuk koordinatörde teardown gerekmez");
    }

    [Fact]
    public async Task StopGpnTunnel_WhenCoordinatorNull_IsNoOp()
    {
        await MainWindowViewModel.StopGpnTunnelIfActiveAsync(coordinator: null);
    }

    // ── GPN akış kararı (ShouldRunGpnFlow) — kullanıcı seçimi yetkilidir ──

    [Theory]
    [InlineData(SplitTunnelViewModel.ModeManual, true, true, "GPN modu + TUN → GPN akışı")]
    [InlineData(SplitTunnelViewModel.ModeManual, false, false, "GPN modu ama TUN kapalı → normal akış (TUN GPN tüneli için zorunlu)")]
    [InlineData(SplitTunnelViewModel.ModeVpn, true, false, "VPN modu + TUN → NORMAL akış — WireGuard profili seçili olsa bile VPN seçimi GPN akışını çalıştırmaz")]
    [InlineData(SplitTunnelViewModel.ModeVpn, false, false, "VPN modu + TUN kapalı → normal akış")]
    [InlineData(SplitTunnelViewModel.ModeOff, true, false, "Mod kapalı → GPN akışı yok")]
    public void ShouldRunGpnFlow_FollowsUserModeNotProfileType(int userMode, bool tunEnabled, bool expected, string reason)
    {
        MainWindowViewModel.ShouldRunGpnFlow(userMode, tunEnabled)
            .Should().Be(expected, reason);
    }
}