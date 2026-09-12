using AwesomeAssertions;
using ServiceLib.Models.Dto;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

/// <summary>
/// SplitTunnelViewModel.FindManualRouteIndex — dashboard'daki kural (domain/IP)
/// satırlarının kaldırma/sıralama adresleme sözleşmesi. Daha önce yalnızca app
/// girişleri kaldırılabiliyordu; domain/IP satırlarının silme butonu sessizce
/// yok sayılıyordu. Bu eşleştirme, RemoveManualRoute (dashboard remove_route)
/// ve MoveManualRoute (move_route) tarafından ortak kullanılır.
///
/// VM'in kendisi AppManager.Config'e bağımlı olduğu için test host'unda
/// kurulmaz (InitApp yalnızca WPF başlangıcında çalışır); saf eşleştirme
/// mantığı burada doğrudan test edilir.
/// </summary>
public class SplitTunnelViewModelRemoveRouteTests
{
    private static SplitTunnelAppItem Entry(string value, string entryType = "app")
        => new()
        {
            EntryType = entryType,
            Value = value,
            ProcessName = entryType == "app" ? value : string.Empty,
            DisplayName = value,
            Action = "vpn",
        };

    [Fact]
    public void Find_DomainEntry_ReturnsIndex()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            Entry("cs2.exe"),
            Entry("discord.gg", "domain"),
            Entry("1.2.3.4/32", "ip"),
        };

        SplitTunnelViewModel.FindManualRouteIndex(apps, "domain", "discord.gg").Should().Be(1);
        SplitTunnelViewModel.FindManualRouteIndex(apps, "ip", "1.2.3.4/32").Should().Be(2);
        SplitTunnelViewModel.FindManualRouteIndex(apps, "app", "cs2.exe").Should().Be(0);
    }

    [Fact]
    public void Find_MatchesCaseInsensitively()
    {
        var apps = new List<SplitTunnelAppItem> { Entry("discord.gg", "domain") };

        SplitTunnelViewModel.FindManualRouteIndex(apps, "domain", "DISCORD.GG").Should().Be(0);
        SplitTunnelViewModel.FindManualRouteIndex(apps, "domain", "discord.gg").Should().Be(0);
    }

    [Fact]
    public void Find_InvalidTypeOrUnknownValue_ReturnsMinusOne()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            Entry("discord.gg", "domain"),
            Entry("cs2.exe"),
        };

        SplitTunnelViewModel.FindManualRouteIndex(apps, "host", "discord.gg").Should().Be(-1, "geçersiz tür reddedilir");
        SplitTunnelViewModel.FindManualRouteIndex(apps, "domain", "riot.com").Should().Be(-1, "bilinmeyen değer reddedilir");
        SplitTunnelViewModel.FindManualRouteIndex(apps, "domain", "").Should().Be(-1, "boş değer reddedilir");
        SplitTunnelViewModel.FindManualRouteIndex(apps, "domain", null!).Should().Be(-1, "null değer reddedilir");
        // Tür eşleşmeli ama değer başka türdeki satırda olabilir.
        SplitTunnelViewModel.FindManualRouteIndex(apps, "domain", "cs2.exe").Should().Be(-1);
    }

    [Fact]
    public void Find_EmptyList_ReturnsMinusOne()
    {
        SplitTunnelViewModel.FindManualRouteIndex([], "app", "cs2.exe").Should().Be(-1);
    }
}