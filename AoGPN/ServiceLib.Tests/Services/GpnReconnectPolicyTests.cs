using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnReconnectPolicy — "bu bağlanma isteği mevcut tünel tarafından zaten
/// karşılanıyor mu?" (Faz 1, en kritik düzeltme).
///
/// Regresyon: <c>GpnConnectionCoordinator.ConnectAsync</c> her çağrıda tüneli
/// durdurup tüm adayları yeniden ölçüyordu; <c>Reload()</c> (abonelik güncellemesi,
/// profil yenileme, F5) her tetiklendiğinde çalışan tünel YIKILIYORDU. Canlı
/// gözlenen "bağlan → kop → yeniden bağlan" ve ~10 saniyelik kesintinin kaynağı.
/// </summary>
public class GpnReconnectPolicyTests
{
    private static GpnServerProfile Server(string id)
        => new(id, id, "host", 51820, "pub", "priv", "10.66.66.2/24");

    [Theory]
    [InlineData(GpnConnectionState.Disconnected)]
    [InlineData(GpnConnectionState.Failed)]
    public void NotConnected_ReusesNothing(GpnConnectionState state)
        => GpnReconnectPolicy.ShouldReuseExistingTunnel(state, ConnectionMode.WireGuardUDP, "it", null)
            .Should().BeFalse();

    [Theory]
    [InlineData(GpnConnectionState.Connected)]
    [InlineData(GpnConnectionState.Connecting)]
    public void WireGuardTunnel_WithoutPreferredTarget_IsReused(GpnConnectionState state)
    {
        // preferred == null: otomatik akış, Reload, tepsi yeniden bağlanması.
        // Mevcut WG tüneli hedefi zaten karşılar — ölçme, yıkma, yeniden kurma.
        GpnReconnectPolicy.ShouldReuseExistingTunnel(state, ConnectionMode.WireGuardUDP, "it", null)
            .Should().BeTrue();
    }

    [Fact]
    public void SamePreferredServer_IsReused()
    {
        // Kullanıcı zaten bağlı olduğu sunucuya "Bağlan" dedi: tünel yıkılmamalı.
        GpnReconnectPolicy.ShouldReuseExistingTunnel(
                GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, "it", Server("it"))
            .Should().BeTrue();
    }

    [Fact]
    public void DifferentPreferredServer_FallsThroughToSoftSwitch()
    {
        // Farklı düğüm isteği yumuşak geçiş yoluna bırakılır (make-before-break).
        GpnReconnectPolicy.ShouldReuseExistingTunnel(
                GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, "it", Server("de"))
            .Should().BeFalse();
    }

    [Fact]
    public void V2rayFallback_WithWireGuardRequest_IsNotReused()
    {
        // V2rayTCP yedeğindeki bir bağlantı, WireGuard isteğini karşılamaz:
        // yükseltme denemesi anlamlıdır.
        GpnReconnectPolicy.ShouldReuseExistingTunnel(
                GpnConnectionState.Connected, ConnectionMode.V2rayTCP, "it", null)
            .Should().BeFalse();
    }

    [Fact]
    public void ConnectedButCurrentServerUnknown_WithPreferred_IsNotReused()
    {
        // Sunucu kimliği bilinmiyorsa "aynı sunucu" iddiasında bulunulamaz.
        GpnReconnectPolicy.ShouldReuseExistingTunnel(
                GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, null, Server("it"))
            .Should().BeFalse();
    }

    [Fact]
    public void ReloadScenario_DoesNotTearDownLiveTunnel()
    {
        // Canlı senaryo: tünel İtalya'ya bağlı; arka planda abonelik güncellemesi
        // Reload() tetikledi ve bağlanma yoluna preferred olmadan geldi.
        var reuse = GpnReconnectPolicy.ShouldReuseExistingTunnel(
            GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, "it", preferred: null);

        reuse.Should().BeTrue(
            "Reload, çalışan tüneli yıkıp yeniden kurmamalı — maç ortasında kesinti olur");
    }
}
