using AwesomeAssertions;
using ServiceLib.Models.Configs;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

public sealed class WindowLifecycleServiceTests
{
    [Theory]
    [InlineData(false, WindowLifecycleAction.Exit)]
    [InlineData(true, WindowLifecycleAction.HideToTray)]
    public void CloseDecisionFollowsSetting(bool hideToTray, WindowLifecycleAction expected)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.UiItem.Hide2TrayWhenClose = hideToTray;

        new WindowLifecycleDecisionService().DecideClose(config, false).Should().Be(expected);
    }

    [Fact]
    public void ExplicitClosePermissionAlwaysAllowsClose()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.UiItem.Hide2TrayWhenClose = true;

        new WindowLifecycleDecisionService().DecideClose(config, true).Should().Be(WindowLifecycleAction.AllowClose);
    }

    [Theory]
    [InlineData(false, false, WindowLifecycleAction.None)]
    [InlineData(true, true, WindowLifecycleAction.None)]
    [InlineData(true, false, WindowLifecycleAction.HideToTray)]
    public void MinimizeDecisionRespectsStartupAndSetting(bool minimizeToTray, bool startupPending, WindowLifecycleAction expected)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.UiItem.Minimize2Tray = minimizeToTray;

        new WindowLifecycleDecisionService().DecideStateChange(config, true, startupPending).Should().Be(expected);
    }

    [Fact]
    public void NonMinimizedStateDoesNotTriggerTrayHide()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.UiItem.Minimize2Tray = true;

        new WindowLifecycleDecisionService().DecideStateChange(config, false, false).Should().Be(WindowLifecycleAction.None);
    }

    [Fact]
    public void StartupMinimizeDoesNotTriggerTrayHide()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.UiItem.Minimize2Tray = true;

        new WindowLifecycleDecisionService().DecideStateChange(config, true, true).Should().Be(WindowLifecycleAction.None);
    }
}
