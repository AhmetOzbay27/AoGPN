using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

public class KnownAppCatalogTests
{
    [Theory]
    [InlineData("EscapeFromTarkov.exe", "Escape From Tarkov")]
    [InlineData("EscapeFromTarkov_BE.exe", "Escape From Tarkov")] // BattlEye client
    [InlineData("Valorant.exe", "VALORANT")]
    [InlineData("cs2.exe", "Counter-Strike 2")]
    [InlineData("FortniteClient-Win64-Shipping.exe", "Fortnite")]
    [InlineData("steam.exe", "Steam")]
    [InlineData("LeagueClient.exe", "League of Legends")]
    public void SuggestAction_KnownGame_ReturnsVpn(string processName, string displayName)
    {
        KnownAppCatalog.SuggestAction(processName, displayName).Should().Be("vpn");
    }

    [Fact]
    public void SuggestAction_Launcher_ReturnsWarp()
    {
        // BSG Launcher auth trafiği temiz egress (WARP) ister; VPN/Oracle çıkışı
        // Cloudflare WAF tarafından engellenir.
        KnownAppCatalog.SuggestAction("BsGLauncher.exe", "BSG Launcher").Should().Be("warp");
    }

    [Theory]
    [InlineData("r5apex.exe", "Apex Legends")]          // exact process match
    [InlineData("customgame.exe", "Escape From Tarkov")] // display-name keyword match
    public void SuggestAction_KeywordMatch_ReturnsVpn(string processName, string displayName)
    {
        KnownAppCatalog.SuggestAction(processName, displayName).Should().Be("vpn");
    }

    [Theory]
    [InlineData("chrome.exe", "Google Chrome")]
    [InlineData("msedge.exe", "Microsoft Edge")]
    [InlineData("firefox.exe", "Firefox")]
    [InlineData("notepad.exe", "Notepad")]
    [InlineData("RustDesk.exe", "RustDesk")] // "rust" must not trigger the game heuristic
    [InlineData("excel.exe", "Microsoft Excel")]
    public void SuggestAction_RegularApp_ReturnsProxy(string processName, string displayName)
    {
        KnownAppCatalog.SuggestAction(processName, displayName).Should().Be("proxy");
    }

    [Fact]
    public void SuggestAction_CaseInsensitive_MatchesProcessName()
    {
        KnownAppCatalog.SuggestAction("escapefromtarkov.exe", "Something Else").Should().Be("vpn");
    }
}
