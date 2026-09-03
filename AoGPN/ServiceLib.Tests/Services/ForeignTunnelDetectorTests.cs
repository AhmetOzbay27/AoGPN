using System.Reflection;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class ForeignTunnelDetectorTests
{
    [Fact]
    public void Detect_NoConflicts_ReturnsClean()
    {
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: _ => false,
            isAppCoreRunning: () => false,
            foreignProcessNames: () => []);

        var result = detector.Detect(10808);

        result.HasConflicts.Should().BeFalse();
        result.HasTunConflict.Should().BeFalse();
        result.HasPortConflict.Should().BeFalse();
    }

    [Fact]
    public void Detect_ForeignTun_FlagsTunConflict()
    {
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => ["xray_tun"],
            isPortListening: _ => false,
            isAppCoreRunning: () => false);

        var result = detector.Detect(10808);

        result.HasTunConflict.Should().BeTrue();
        result.HasConflicts.Should().BeTrue();
        result.TunAdapterNames.Should().Contain("xray_tun");
    }

    [Fact]
    public void Detect_PortOccupiedWithoutAppCore_FlagsPortConflict()
    {
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: port => port == 10808,
            isAppCoreRunning: () => false);

        var result = detector.Detect(10808);

        result.HasPortConflict.Should().BeTrue();
        result.HasConflicts.Should().BeTrue();
    }

    [Fact]
    public void Detect_PortOccupiedWithAppCoreRunning_NoPortConflict()
    {
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: _ => true,
            isAppCoreRunning: () => true,
            foreignProcessNames: () => []);

        var result = detector.Detect(10808);

        result.HasPortConflict.Should().BeFalse();
        result.HasConflicts.Should().BeFalse();
    }

    [Fact]
    public void Detect_BothConflicts_FlagsBoth()
    {
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => ["xray_tun"],
            isPortListening: _ => true,
            isAppCoreRunning: () => false);

        var result = detector.Detect(10808);

        result.HasTunConflict.Should().BeTrue();
        result.HasPortConflict.Should().BeTrue();
        result.HasConflicts.Should().BeTrue();
    }

    [Fact]
    public void Detect_ForeignProcess_FlagsProcessConflict()
    {
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: _ => false,
            isAppCoreRunning: () => false,
            foreignProcessNames: () => ["wireguard.exe"]);

        var result = detector.Detect(10808);

        result.HasProcessConflict.Should().BeTrue();
        result.HasConflicts.Should().BeTrue();
        result.ForeignProcessNames.Should().Contain("wireguard.exe");
    }

    [Fact]
    public void Detect_NoForeignProcesses_NoProcessConflict()
    {
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: _ => false,
            isAppCoreRunning: () => false,
            foreignProcessNames: () => []);

        var result = detector.Detect(10808);

        result.HasProcessConflict.Should().BeFalse();
        result.HasConflicts.Should().BeFalse();
    }

    [Fact]
    public void IsAppOwnedTunName_RecognizesAppTunNames()
    {
        ForeignTunnelDetector.IsAppOwnedTunName("singbox_tun").Should().BeTrue();
        ForeignTunnelDetector.IsAppOwnedTunName("wintunsingbox_tun").Should().BeTrue();
        ForeignTunnelDetector.IsAppOwnedTunName("SINGBOX_TUN").Should().BeTrue();
    }

    [Fact]
    public void IsAppOwnedTunName_RejectsForeignTunNames()
    {
        ForeignTunnelDetector.IsAppOwnedTunName("xray_tun").Should().BeFalse();
        ForeignTunnelDetector.IsAppOwnedTunName("singbox").Should().BeFalse();
        ForeignTunnelDetector.IsAppOwnedTunName("Ethernet").Should().BeFalse();
    }

    // ── Windows gömülü IPv6 geçiş/sözde arayüzleri (Teredo vb.) ───────────

    [Theory]
    [InlineData("Teredo Tunneling Pseudo-Interface", true)]  // rapor edilen asıl bozucu
    [InlineData("Microsoft Teredo Tunneling Adapter", true)]
    [InlineData("isatap.{8B0A0E9A-...}", true)]
    [InlineData("6to4 Adapter", true)]
    [InlineData("Loopback Pseudo-Interface 1", true)]
    // Gerçek yabancı VPN / kendi tünel adları asla göz ardı edilmez
    [InlineData("xray_tun", false)]
    [InlineData("WireGuard Tunnel", false)]
    [InlineData("Wintun", false)]
    [InlineData("Almanya-client", false)]
    [InlineData("Ethernet", false)]
    [InlineData("AoGPN-It", false)]
    public void IsBuiltInTunLikeInterfaceName_IgnoresWindowsBuiltInTunnelPseudoInterface(string name, bool expected)
    {
        // Teredo ve kardeşi IPv6 geçiş/sözde arayüzleri Windows'ta her zaman var olur;
        // "tun" içeren adları yüzünden yabancı-tünel çakışması sayılmamalıdır.
        ForeignTunnelDetector.IsBuiltInTunLikeInterfaceName(name).Should().Be(expected);
    }

    [Fact]
    public void IsBuiltInTunLikeInterfaceName_NullOrEmpty_ReturnsFalse()
    {
        ForeignTunnelDetector.IsBuiltInTunLikeInterfaceName(null).Should().BeFalse();
        ForeignTunnelDetector.IsBuiltInTunLikeInterfaceName(string.Empty).Should().BeFalse();
    }

    // ── Kill API'si yok — tespit yalnızca raporlar ────────────────────────

    [Fact]
    public void Detector_ExposesNoKillOrResolveApi()
    {
        // Auto-kill kalıcı olarak kaldırıldı (bkz. docs/foreign-vpn-kill-removal-roadmap.md):
        // ForeignTunnelDetector yalnızca tespit/uyarı yapar, hiçbir üçüncü taraf VPN
        // sürecini sonlandıramaz. Bu regresyon testi, kill API'si (Kill*/Resolve*
        // yöntem adı) geri eklenirse yapısal olarak yakalanmasını sağlar.
        var methodNames = typeof(ForeignTunnelDetector)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => m.Name);

        methodNames.Should().NotContain(m => m.Contains("Kill", StringComparison.OrdinalIgnoreCase));
        methodNames.Should().NotContain(m => m.Contains("Resolve", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Detect_ForeignProcess_ReportsItWithoutKilling()
    {
        // Yabancı VPN istemcisi tespit edilir ve raporlanır; öldürme yolu yoktur —
        // karar kullanıcınındır (uyarı gösterilir, süreç yaşar).
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: _ => false,
            isAppCoreRunning: () => false,
            foreignProcessNames: () => ["wireguard.exe"]);

        var result = detector.Detect(10808);

        result.HasProcessConflict.Should().BeTrue();
        result.ForeignProcessNames.Should().Contain("wireguard.exe");
    }
}
