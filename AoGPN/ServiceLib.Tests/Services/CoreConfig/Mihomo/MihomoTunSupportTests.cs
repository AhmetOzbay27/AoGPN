using System.Net;
using AwesomeAssertions;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.Services.CoreConfig.Mihomo;

/// <summary>
/// MihomoTunSupport — fiziksel NIC / host rota yardımcıları. Buradaki kritik
/// regresyon: son okteti ≥ 128 olan sunucu IP'si (ör. Italya 92.4.220.236)
/// `byte << 24` imzalı int taşmasıyla OverflowException fırlatıyordu
/// (proje CheckForOverflowUnderflow=true) — GPN mihomo config üretimi
/// \"Varsayılan yapılandırma dosyası oluşturulamadı\" ile düşüyordu.
/// </summary>
public class MihomoTunSupportTests
{
    [Theory]
    [InlineData("92.4.220.236", 0x5C04DCECu)]   // Italya — son oktet ≥ 128 (eski kod taşardı)
    [InlineData("130.61.223.36", 0x823DDF24u)]  // Almanya — bağlanan sunucu
    [InlineData("127.0.0.1", 0x7F000001u)]
    [InlineData("10.66.66.2", 0x0A424202u)]
    [InlineData("255.255.255.255", 0xFFFFFFFFu)] // tüm oktetler üst sınır
    [InlineData("0.0.0.0", 0x00000000u)]
    public void ToNetworkOrderDword_ConvertsBigEndian(string ip, uint expected)
    {
        MihomoTunSupport.ToNetworkOrderDword(IPAddress.Parse(ip)).Should().Be(expected);
    }

    [Fact]
    public void DetectPhysicalInterface_ItalyIp_DoesNotThrow()
    {
        // Italya (92.4.220.236): eski kod burada OverflowException fırlatıyordu.
        // Gerçek iphlpapi çağrısı salt-okunurdur; asıl amaç taşma regresyonu.
        var info = MihomoTunSupport.DetectPhysicalInterface("92.4.220.236");

        // Fiziksel NIC bulunamazsa null dönebilir (ağ yok) — ama ASLA fırlatmamalı.
        if (info is not null)
        {
            info.Name.Should().NotBeNullOrWhiteSpace();
            info.Index.Should().BeGreaterThan(0);
        }
    }

    // --------------------------------------------------------------------------
    // Saf seçim fonksiyonu (SelectPhysicalInterface) — canlı NetworkInterface
    // verisi gerektirmez; loglardaki gerçek senaryoları birebir modeller.
    // --------------------------------------------------------------------------

    private static MihomoTunSupport.NicCandidate C(
        string name, int index,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        OperationalStatus status = OperationalStatus.Up,
        bool hasGateway = true,
        bool hasDefaultRoute = false,
        string? gateway = null,
        string? description = null)
        => new(name, description ?? name, type, status, hasGateway, hasDefaultRoute,
            hasGateway ? gateway ?? $"192.168.1.{(index % 254) + 1}" : null, index);

    [Fact]
    public void Select_DefaultRouteOwnerWins_OverVmnetListedFirst()
    {
        // 20.09.2026 canlı senaryo: mihomo "VMware Network Adapter VMnet1"e
        // bağlandı (gw 192.168.0.1, internet yok) → WG "unreachable network".
        // Wi-Fi varsayılan rotanın sahibi ve aktif yol — seçilmesi gereken o.
        var candidates = new[]
        {
            C("VMware Network Adapter VMnet1", 11, gateway: "192.168.0.1"),
            C("Wi-Fi", 14, type: NetworkInterfaceType.Wireless80211, hasDefaultRoute: true),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: []);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Wi-Fi");
        result.Gateway.Should().Be("192.168.1.15"); // 14 % 254 + 1
    }

    [Fact]
    public void Select_PreferredIndexPointingAtVmnet_FallsBackToDefaultRouteOwner()
    {
        // GetBestInterface(hedef) VMnet1'i söylerse bile sanal adaptör elenir;
        // varsayılan rotanın sahibine düşülür.
        var candidates = new[]
        {
            C("VMware Network Adapter VMnet1", 11, gateway: "192.168.0.1"),
            C("Ethernet", 3, hasDefaultRoute: true),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: [11]);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Ethernet");
    }

    [Fact]
    public void Select_PreferredIndexPointingAtOwnTun_NeverSelectsTun()
    {
        // GetBestInterface kendi TUN'unu söylese bile (host rotası eksikken olabilir)
        // asla seçilmez — mihomo kendi TUN'una döngü yapmamalı.
        var candidates = new[]
        {
            C("AoGPN", 20, description: "Wintun Userspace Tunnel", hasDefaultRoute: true),
            C("Wi-Fi", 14, type: NetworkInterfaceType.Wireless80211, hasDefaultRoute: true),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: [20]);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void Select_DestinationPreferredIndex_WinsOverDefaultRouteOwner()
    {
        // GetBestInterface(hedef IP) kesin yol söylüyorsa o kazanır.
        var candidates = new[]
        {
            C("Ethernet", 3, hasDefaultRoute: true),
            C("Wi-Fi", 14, type: NetworkInterfaceType.Wireless80211),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: [14, 3]);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void Select_NoDefaultRoute_PhysicalEthernetBeatsVmnet()
    {
        // Varsayılan rota sahibi yoksa: gerçek Ethernet, sanal adaptörden önce.
        var candidates = new[]
        {
            C("VMware Network Adapter VMnet8", 12, gateway: "192.168.1.1"),
            C("Ethernet", 3),
            C("Wi-Fi", 14, type: NetworkInterfaceType.Wireless80211),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: []);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Ethernet"); // tip sırası: Ethernet > Wireless80211 > diğer
    }

    [Fact]
    public void Select_PhysicalTypePreferredOverUnknownType()
    {
        var candidates = new[]
        {
            C("Yerel Ağ Bağlantısı", 9, type: NetworkInterfaceType.Unknown, hasDefaultRoute: true),
            C("Wi-Fi", 14, type: NetworkInterfaceType.Wireless80211, hasDefaultRoute: true),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: []);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void Select_OnlyVirtualAdapters_ReturnsBestVirtualAsLastResort()
    {
        // Makinenin kendisi bir VM: yalnızca sanal adaptörler var. null yerine
        // en iyi aday dönmeli — host rotası kurulabilsin (mihomo auto-detect'e
        // bırakmaktan iyidir).
        var candidates = new[]
        {
            C("VMware Network Adapter VMnet1", 11, gateway: "192.168.0.1"),
            C("VMware Network Adapter VMnet8", 12, gateway: "192.168.1.1", hasDefaultRoute: true),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: []);

        result.Should().NotBeNull();
        result!.Name.Should().Be("VMware Network Adapter VMnet8");
    }

    [Fact]
    public void Select_TunNeverSelected_EvenAsLastResort()
    {
        // TUN benzeri hiçbir koşulda dönülmez; sanal adaptör bile ondan önce gelir.
        var candidates = new[]
        {
            C("AoGPN", 20, description: "Wintun Userspace Tunnel", hasDefaultRoute: true),
            C("VMware Network Adapter VMnet1", 11, gateway: "192.168.0.1", hasDefaultRoute: true),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: []);

        result.Should().NotBeNull();
        result!.Name.Should().Be("VMware Network Adapter VMnet1");
    }

    [Fact]
    public void Select_NoUpInterfaceWithGateway_ReturnsNull()
    {
        var candidates = new[]
        {
            C("Wi-Fi", 14, type: NetworkInterfaceType.Wireless80211, status: OperationalStatus.Down, hasGateway: false),
            C("Ethernet", 3, status: OperationalStatus.Unknown, hasGateway: false),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: []);

        result.Should().BeNull();
    }

    [Fact]
    public void Select_PreferredIndexUnknown_IgnoresIt()
    {
        // GetBestInterface'in söylediği indeks adaylarda yoksa sessizce atlanır.
        var candidates = new[]
        {
            C("Ethernet", 3, hasDefaultRoute: true),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: [99]);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Ethernet");
    }

    [Fact]
    public void Select_PreservesNameIndexAndGateway()
    {
        var candidates = new[]
        {
            C("Ethernet", 3, gateway: "192.168.1.1", hasDefaultRoute: true),
        };

        var result = MihomoTunSupport.SelectPhysicalInterface(candidates, preferredIndexes: []);

        result.Should().BeEquivalentTo(
            new MihomoTunSupport.PhysicalInterfaceInfo("Ethernet", 3, "192.168.1.1"));
    }

    [Theory]
    [InlineData("VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter for VMnet1", true)]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", true)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", true)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", false)]
    [InlineData("AoGPN", "Wintun Userspace Tunnel", false)] // TUN ayrı kategoride
    public void IsVirtualAdapter_DetectsVirtualMachines(string name, string description, bool expected)
    {
        MihomoTunSupport.IsVirtualAdapter(name, description).Should().Be(expected);
    }

    [Theory]
    [InlineData("AoGPN", "Wintun Userspace Tunnel", true)]
    [InlineData("Local Area Connection", "WireGuard Tunnel Adapter", true)]
    [InlineData("Ethernet", "Realtek PCIe GbE Family Controller", false)]
    public void IsTunLikeInterface_DetectsTunnels(string name, string description, bool expected)
    {
        MihomoTunSupport.IsTunLikeInterface(name, description).Should().Be(expected);
    }
}