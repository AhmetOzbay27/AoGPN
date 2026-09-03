using System.Net.NetworkInformation;
using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Singbox;

/// <summary>
/// <c>ApplyTunLoopProtection</c>'ın direct outbound <c>bind_interface</c> mantığı + fiziksel
/// NIC seçimini birim testleriyle kapsar. Farklı NIC senaryoları (yok / tek / çoklu /
/// tünel) <see cref="Utils.PickDefaultPhysicalInterface"/> üzerinden ağ olmadan doğrulanır;
/// direct'e bind uygulaması <see cref="CoreConfigSingboxService.ApplyBindInterfaceToDirects"/>
/// üzerinden kapsanır.
/// </summary>
public class TunLoopProtectionTests
{
    private static Utils.InterfaceProbe P(string name, NetworkInterfaceType type,
        string? ipv4 = "192.168.1.4", bool gw = true, bool tun = false)
        => new(name, type, ipv4, gw, tun);

    // ── Seçici: NIC yok ─────────────────────────────────────────────────

    [Fact]
    public void PickDefault_NoPhysicalNics_ReturnsNullNull()
    {
        // Yalnızca loopback / tünel / ağ geçitsiz veya IPv4'süz — fiziksel uplink yok.
        var (name, ipv4) = Utils.PickDefaultPhysicalInterface(
        [
            P("Loopback Pseudo-Interface 1", NetworkInterfaceType.Loopback),
            P("WireGuard Tunnel", NetworkInterfaceType.Tunnel, "10.66.66.2"),
            P("Ethernet 2", NetworkInterfaceType.Ethernet, null, gw: false), // IPv4 yok
            P("Yerel Ağ", NetworkInterfaceType.Wireless80211, "169.254.1.5", gw: false), // yalnız link-local, gw yok
        ]);
        name.Should().BeNull("fiziksel NIC yoksa bind_interface bağlanmamalı");
        ipv4.Should().BeNull();
    }

    // ── Seçici: tek fiziksel NIC ─────────────────────────────────────────

    [Fact]
    public void PickDefault_SinglePhysical_ReturnsNameAndIpv4()
    {
        var (name, ipv4) = Utils.PickDefaultPhysicalInterface(
        [
            P("Wi-Fi", NetworkInterfaceType.Wireless80211, "192.168.1.4"),
        ]);
        name.Should().Be("Wi-Fi");
        ipv4.Should().Be("192.168.1.4");
    }

    // ── Seçici: tünel önce gelip fiziksel tipte olamaz ──────────────────

    [Fact]
    public void PickDefault_TunnelWithGateway_IsNotChosen()
    {
        // Tünel (wintun/wireguard) ağ geçidi + IPv4 taşıyorsa bile fiziksel tipte
        // olmadığından seçilemez — gerçek uplink tercih edilir.
        var (name, _) = Utils.PickDefaultPhysicalInterface(
        [
            P("WireGuard Tunnel", NetworkInterfaceType.Tunnel, "10.66.66.2"), // önce ama tünel
            P("Ethernet", NetworkInterfaceType.Ethernet, "192.168.0.10"),
        ]);
        name.Should().Be("Ethernet");
    }

    [Fact]
    public void PickDefault_TunLikeNameWithPhysicalType_Skipped()
    {
        // Bazı ortamlar tünel adaptörlerini fiziksel tiple raporlayabilir (wintun'ın
        // Ethernet görünmesi). Adı tun/vmnet/virtual içeren böyle bir NIC yine elenir.
        var (name, _) = Utils.PickDefaultPhysicalInterface(
        [
            P("singbox_tun", NetworkInterfaceType.Ethernet, "172.18.0.1", tun: true), // fiziksel tip ama tünel adı
            P("Wi-Fi", NetworkInterfaceType.Wireless80211, "192.168.1.4"),
        ]);
        name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void PickDefault_VirtualVmwareNames_AreSkipped()
    {
        var (name, _) = Utils.PickDefaultPhysicalInterface(
        [
            P("VMware Network Adapter VMnet8", NetworkInterfaceType.Ethernet, "192.168.1.1", tun: true),
            P("VirtualBox Host-Only", NetworkInterfaceType.Ethernet, "192.168.56.1", tun: true),
            P("Wi-Fi", NetworkInterfaceType.Wireless80211, "192.168.1.4"),
        ]);
        name.Should().Be("Wi-Fi");
    }

    // ── Seçici: çoklu fiziksel NIC ───────────────────────────────────────

    [Fact]
    public void PickDefault_MultiplePhysical_RequiresRealGateway()
    {
        // Ethernet APIPA + ağ geçitsiz (kablosuz yedek) — ağ geçidi olan seçilir;
        // sırada ilk uygun (gerçek uplink) kazanır.
        var (name, _) = Utils.PickDefaultPhysicalInterface(
        [
            P("Ethernet", NetworkInterfaceType.Ethernet, "169.254.0.28", gw: false), // APIPA, gw yok
            P("Wi-Fi", NetworkInterfaceType.Wireless80211, "192.168.1.4"),           // gerçek gw
        ]);
        name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void PickDefault_FirstGatewayPhysical_Wins()
    {
        // İkisi de gerçek ağ geçitli iki fiziksel NIC → sırada ilk olan seçilir.
        var (name, _) = Utils.PickDefaultPhysicalInterface(
        [
            P("Ethernet", NetworkInterfaceType.Ethernet, "192.168.2.10"),
            P("Wi-Fi", NetworkInterfaceType.Wireless80211, "192.168.1.4"),
        ]);
        name.Should().Be("Ethernet");
    }

    // ── Direct'e bind uygulama ───────────────────────────────────────────

    [Fact]
    public void ApplyBind_EmptyBindOnDirect_GetsName_ReturnsTrue()
    {
        var direct = Direct();
        var changed = CoreConfigSingboxService.ApplyBindInterfaceToDirects([direct], "Wi-Fi");
        changed.Should().BeTrue();
        direct.bind_interface.Should().Be("Wi-Fi");
    }

    [Fact]
    public void ApplyBind_AlreadyBound_NotOverwritten_ReturnsFalse()
    {
        var direct = Direct("eth0");
        var changed = CoreConfigSingboxService.ApplyBindInterfaceToDirects([direct], "Wi-Fi");
        changed.Should().BeFalse("kullanıcının bilinçli BindInterface seçimi ezilmemeli");
        direct.bind_interface.Should().Be("eth0");
    }

    [Fact]
    public void ApplyBind_NullOrEmptyName_NoOp()
    {
        var direct = Direct();
        CoreConfigSingboxService.ApplyBindInterfaceToDirects([direct], null).Should().BeFalse();
        CoreConfigSingboxService.ApplyBindInterfaceToDirects([direct], "  ").Should().BeFalse();
        direct.bind_interface.Should().BeNull();
    }

    [Fact]
    public void ApplyBind_NonDirectOutbounds_Untouched()
    {
        var proxy = new Outbound4Sbox { type = "wireguard", tag = "proxy" };
        var dns = new Outbound4Sbox { type = "dns", tag = "dns-out" };
        CoreConfigSingboxService.ApplyBindInterfaceToDirects([proxy, dns], "Wi-Fi").Should().BeFalse();
        proxy.bind_interface.Should().BeNull();
        dns.bind_interface.Should().BeNull();
    }

    [Fact]
    public void ApplyBind_NullOrEmptyList_NoOp()
    {
        CoreConfigSingboxService.ApplyBindInterfaceToDirects(null, "Wi-Fi").Should().BeFalse();
        CoreConfigSingboxService.ApplyBindInterfaceToDirects(new List<Outbound4Sbox>(), "Wi-Fi").Should().BeFalse();
    }

    [Fact]
    public void ApplyBind_MultipleDirects_MixesCorrectly()
    {
        var a = Direct();        // boş → set edilir
        var b = Direct("ethX");  // mevcut → korunur
        var c = Direct();        // boş → set edilir
        CoreConfigSingboxService.ApplyBindInterfaceToDirects([a, b, c], "Wi-Fi").Should().BeTrue();
        a.bind_interface.Should().Be("Wi-Fi");
        b.bind_interface.Should().Be("ethX");
        c.bind_interface.Should().Be("Wi-Fi");
    }

    private static Outbound4Sbox Direct(string? bind = null) => new()
    {
        type = "direct",
        tag = "direct",
        bind_interface = bind,
    };
}