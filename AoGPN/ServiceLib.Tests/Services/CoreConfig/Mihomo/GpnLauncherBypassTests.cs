using AwesomeAssertions;
using ServiceLib.Models.Configs;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.Services.CoreConfig.Mihomo;

public class GpnLauncherBypassTests
{
    private static Config ConfigWith(string? json) => new() { GuiItem = new() { LauncherBypassesJson = json } };

    [Fact]
    public void ReadAll_NullOrEmptySetting_FallsBackToBsgDefault()
    {
        // Test ortamında config yok / GuiItem yok — hepsi varsayılana düşer.
        GpnLauncherBypass.ReadAll(null).Should().ContainSingle().Which.Name.Should().Be("BSG");
        GpnLauncherBypass.ReadAll(new Config()).Should().ContainSingle().Which.Name.Should().Be("BSG");
        GpnLauncherBypass.ReadAll(ConfigWith(null)).Should().ContainSingle().Which.Name.Should().Be("BSG");
        GpnLauncherBypass.ReadAll(ConfigWith("   ")).Should().ContainSingle().Which.Name.Should().Be("BSG");

        // Varsayılan BSG girişi eski sabit listeyi birebir taşır: 10 domain, warp egress.
        GpnLauncherBypass.BsgDefault.Domains.Should().HaveCount(10);
        GpnLauncherBypass.BsgDefault.Domains.Should().Contain(
            "escapefromtarkov.com", "battlestategames.com", "tarkov.com",
            "escapefromtarkov.ru", "prod.escapefromtarkov.com", "launcher.escapefromtarkov.com",
            "launcher.escapefromtarkov.ru", "gw-pvp.escapefromtarkov.com",
            "www.escapefromtarkov.com", "profile.tarkov.com");
        GpnLauncherBypass.BsgDefault.Egress.Should().Be(GpnLauncherBypass.EgressWarp);
        GpnLauncherBypass.BsgDefault.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ReadAll_CorruptJson_FallsBackToBsgDefault()
    {
        GpnLauncherBypass.ReadAll(ConfigWith("{ not json")).Should().ContainSingle().Which.Name.Should().Be("BSG");
    }

    [Fact]
    public void ReadAll_ParsesUserList_AndDropsNullEntries()
    {
        var items = GpnLauncherBypass.ReadAll(ConfigWith(
            """[{"name":"Epic","domains":["epicgames.com","unrealengine.com"],"egress":"direct","enabled":true},{"name":"Steam","domains":["steampowered.com"],"egress":"warp","enabled":true},null]"""));

        items.Should().HaveCount(2);
        items[0].Name.Should().Be("Epic");
        items[0].Egress.Should().Be(GpnLauncherBypass.EgressDirect);
        items[0].Domains.Should().Equal("epicgames.com", "unrealengine.com");
        items[1].Name.Should().Be("Steam");
        items[1].Egress.Should().Be(GpnLauncherBypass.EgressWarp);
    }

    [Fact]
    public void ReadAll_EmptyArray_DisablesAllLauncherRules()
    {
        GpnLauncherBypass.ReadAll(ConfigWith("[]")).Should().BeEmpty();
    }

    [Fact]
    public void Presets_CoverCommonLaunchers_AndDefaultToWarp()
    {
        GpnLauncherBypass.Presets.Select(p => p.Name).Should().Contain(
            "BSG", "Epic Games", "Steam", "Riot");
        GpnLauncherBypass.Presets
            .Where(p => p.Name != "BSG")
            .Should().OnlyContain(p =>
                p.Egress == GpnLauncherBypass.EgressWarp && p.Enabled && p.Domains.Length > 0);
    }
}