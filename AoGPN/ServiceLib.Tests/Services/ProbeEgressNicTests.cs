using AwesomeAssertions;
using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

/// <summary>
/// ProbeEgressNic testleri — tünel-benzeri ad sınıflandırması, anlık görüntü
/// önbelleği ve egress bağlamanın gerçek soket üzerinde güvenli (no-op) davranışı.
/// Gerçek NIC durumuna (CI'da tünel yok) bağımlılık yoktur — saf parçalar test edilir.
/// </summary>
// ProbeEgressNic statik paylaşılan durum taşır; koordinatör testleri de aynı
// duruma yazdığı için (SetKnownTunnelNames) iki sınıf AYNI koleksiyonda serileşir
// — paralel çalışma testleri birbirine karıştırır.
[Collection("probe-egress")]
public class ProbeEgressNicTests
{
    // ── IsTunLikeName — tünel-benzeri ad sınıflandırması ────────────────

    [Theory]
    [InlineData("singbox_tun")]        // uygulamanın kendi sing-box TUN adaptörü
    [InlineData("wintunsingbox_tun")]  // legacy sing-box TUN adaptörü
    [InlineData("xray_tun")]           // xray TUN adaptörü
    [InlineData("AoGPN-it")]           // GPN wintun yakalama adaptörü (WintunNative)
    [InlineData("WireGuard Tunnel")]   // orijinal WireGuard for Windows adaptörü
    [InlineData("utun3")]              // macOS tünel adaptörü
    [InlineData("OpenVPN TAP")]        // OpenVPN TAP adaptörü
    [InlineData("Tailscale")]          // Tailscale sanal adaptörü
    [InlineData("v2rayN")]             // v2rayN TUN adaptörü
    public void IsTunLikeName_TunnelNames_ReturnsTrue(string name)
        => ProbeEgressNic.IsTunLikeName(name).Should().BeTrue();

    [Theory]
    [InlineData("Ethernet")]
    [InlineData("Ethernet 2")]
    [InlineData("Wi-Fi")]
    [InlineData("Local Area Connection")]
    [InlineData("Realtek PCIe GbE Family Controller")]
    [InlineData("Intel(R) Ethernet Connection (7) I219-V")]
    public void IsTunLikeName_PhysicalNames_ReturnsFalse(string name)
        => ProbeEgressNic.IsTunLikeName(name).Should().BeFalse();

    // ── GetSnapshot — gerçek ağ yığını üzerinde çökmez, tutarlıdır ──────

    [Fact]
    public void GetSnapshot_DoesNotThrow_AndIsCachedWithinTtl()
    {
        // Gerçek ağ durumu ne olursa olsun snapshot üretilir; TTL penceresi
        // İÇİNDEKİ ikinci çağrı önbellekten AYNI örneği döndürür (her probe'da NIC
        // numaralandırması yapılmaz).
        //
        // Pencere DIŞINA çıkan ikinci çağrı ise haklı olarak taze numaralandırır.
        // Bu, yüklü bir paralel koşuda (25 adaptörlü makinelerde BuildSnapshot
        // saniyeler sürebilir) gerçekten oluyordu ve eski hâliyle test, ölçmediği
        // bir zamanlama varsayımına dayandığı için kararsızca kırılıyordu.
        // İddia artık pencerenin HER İKİ tarafını da doğrular.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = ProbeEgressNic.GetSnapshot();
        var second = ProbeEgressNic.GetSnapshot();
        sw.Stop();

        if (sw.Elapsed < ProbeEgressNic.CacheTtl)
        {
            second.Should().BeSameAs(first, "TTL içindeki ikinci çağrı önbellekten döner");
        }
        else
        {
            second.Should().NotBeSameAs(first, "TTL dolduğunda taze numaralandırma beklenir");
        }
    }

    [Fact]
    public void Refresh_InvalidatesCache()
    {
        var first = ProbeEgressNic.GetSnapshot();
        ProbeEgressNic.Refresh();
        var second = ProbeEgressNic.GetSnapshot();

        second.Should().NotBeSameAs(first, "Refresh sonrası taze numaralandırma yapılır");
    }

    // ── SetKnownTunnelNames — koordinatörden gelen bilinen tünel adı ────

    [Fact]
    public void SetKnownTunnelNames_StoresNamesAndInvalidatesCache()
    {
        ProbeEgressNic.SetKnownTunnelNames(null); // temiz başlangıç
        var first = ProbeEgressNic.GetSnapshot();

        ProbeEgressNic.SetKnownTunnelNames(["singbox_tun", "singbox_tun", " xray_tun "]);

        // Büyük/küçük harf duyarsız, boşluk temizlenmez ama farklı adlar korunur;
        // tekrarlar elenir.
        ProbeEgressNic.GetKnownTunnelNames().Should().Contain("singbox_tun");
        ProbeEgressNic.GetKnownTunnelNames().Should().Contain("xray_tun");
        // Yeni bilgi önbelleği geçersiz kılar — sonraki sorgu taze numaralandırır.
        ProbeEgressNic.GetSnapshot().Should().NotBeSameAs(first);
    }

    [Fact]
    public void SetKnownTunnelNames_Null_ClearsAndFallsBackToHeuristic()
    {
        ProbeEgressNic.SetKnownTunnelNames(["singbox_tun"]);
        ProbeEgressNic.GetKnownTunnelNames().Should().NotBeNull();

        ProbeEgressNic.SetKnownTunnelNames(null);

        ProbeEgressNic.GetKnownTunnelNames().Should().BeNull("null = sezgisel (heuristic) moda dönüş");
    }

    // ── BindSocketEgress — gerçek soket üzerinde güvenli (no-op) ────────

    [Fact]
    public void BindSocketEgress_OnFreshSocket_DoesNotThrow()
    {
        // Tünel yokken (CI) no-op olmalı; tünel varsa bile yasal soket seçeneği
        // uygulanır — hiçbir durumda fırlatmaz, soketi bozmaz.
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        ProbeEgressNic.BindSocketEgress(udp.Client, AddressFamily.InterNetwork);

        udp.Client.Connected.Should().BeFalse();
        udp.Client.IsBound.Should().BeFalse();
    }

    [Fact]
    public void BindSocketEgress_OnFreshTcpSocket_DoesNotThrow()
    {
        using var tcp = new TcpClient();
        ProbeEgressNic.BindSocketEgress(tcp.Client, AddressFamily.InterNetwork);

        tcp.Client.Connected.Should().BeFalse();
    }
}
