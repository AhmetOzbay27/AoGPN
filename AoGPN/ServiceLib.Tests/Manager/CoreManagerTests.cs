using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Manager;
using Xunit;

namespace ServiceLib.Tests.Manager;

public class CoreManagerTests
{
    [Theory]
    [InlineData(ECoreType.mihomo)]
    [InlineData(ECoreType.Xray)]
    public void ShouldRunAsSudo_TunLaunchOnNonWindows_RequiresElevation(ECoreType coreType)
    {
        CoreManager.ShouldRunAsSudo(isTunLaunch: true, coreType, isNonWindows: true).Should().BeTrue();
    }

    [Fact]
    public void ShouldRunAsSudo_NonTunLaunch_ShouldNotElevate()
    {
        // Regression guard for the macOS TUN failure: the elevation decision must follow
        // the context snapshot that generated the config. A launch whose snapshot has TUN
        // disabled must never elevate, and a launch whose snapshot has TUN enabled must
        // elevate regardless of later changes to the live config.
        CoreManager.ShouldRunAsSudo(isTunLaunch: false, ECoreType.mihomo, isNonWindows: true).Should().BeFalse();
        CoreManager.ShouldRunAsSudo(isTunLaunch: false, ECoreType.Xray, isNonWindows: true).Should().BeFalse();
    }

    [Fact]
    public void ShouldRunAsSudo_OnWindows_ShouldNotElevate()
    {
        CoreManager.ShouldRunAsSudo(isTunLaunch: true, ECoreType.mihomo, isNonWindows: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(ECoreType.v2fly)]
    [InlineData(ECoreType.hysteria)]
    [InlineData(null)]
    public void ShouldRunAsSudo_UnsupportedCoreType_ShouldNotElevate(ECoreType? coreType)
    {
        CoreManager.ShouldRunAsSudo(isTunLaunch: true, coreType, isNonWindows: true).Should().BeFalse();
    }

    [Fact]
    public void IsPathUnderDirectories_AppOwnedPath_IsOwned()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "AoGPN_owned");
        var exe = Path.Combine(appDir, "bin", "xray", "xray.exe");

        CoreManager.IsPathUnderDirectories(exe, [appDir]).Should().BeTrue();
    }

    [Fact]
    public void IsPathUnderDirectories_ExactDirectoryMatch_IsOwned()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "AoGPN_owned_exact");
        var exe = Path.Combine(appDir, "sing-box.exe");

        CoreManager.IsPathUnderDirectories(exe, [appDir]).Should().BeTrue();
    }

    [Fact]
    public void IsPathUnderDirectories_ForeignPath_IsNotOwned()
    {
        // A foreign client's core in its own folder must never be attributed
        // to AoGPN (the whole point of the orphan-kill ownership guard).
        var appDir = Path.Combine(Path.GetTempPath(), "AoGPN_owned_foreign");
        var foreignExe = Path.Combine(Path.GetTempPath(), "v2rayN", "xray.exe");

        CoreManager.IsPathUnderDirectories(foreignExe, [appDir]).Should().BeFalse();
    }

    [Fact]
    public void IsPathUnderDirectories_SiblingPrefix_IsNotOwned()
    {
        // Directory-boundary guard: C:\app\bin2\xray.exe must not match
        // the owned directory C:\app\bin.
        var appDir = Path.Combine(Path.GetTempPath(), "AoGPN_boundary");
        var sibling = Path.Combine(Path.GetTempPath(), "AoGPN_boundary_evil", "xray.exe");

        CoreManager.IsPathUnderDirectories(sibling, [appDir]).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPathUnderDirectories_UnreadablePath_IsNotOwned(string? exePath)
    {
        // A process whose executable path cannot be read is never killed.
        CoreManager.IsPathUnderDirectories(exePath, [Path.GetTempPath()]).Should().BeFalse();
    }

    [Fact]
    public void IsPathUnderDirectories_CaseInsensitive_IsOwned()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "AoGPN_CaseMix");
        var exe = Path.Combine(appDir.ToLowerInvariant(), "bin", "xray", "xray.exe");

        CoreManager.IsPathUnderDirectories(exe, [appDir]).Should().BeTrue();
    }
}
