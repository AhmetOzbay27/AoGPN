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

        public GpnConnectionSnapshot Snapshot => GpnConnectionSnapshot.Idle();

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
}