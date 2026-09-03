namespace ServiceLib.Tests;

public class SystemProxyPolicyTests
{
    [Fact]
    public void IndependentSetPreferenceIsKeptWhenDisconnected()
    {
        var config = CreateConfig(ESysProxyType.ForcedChange, GameTriggerModes.Off, "");

        Assert.Equal(ESysProxyType.ForcedChange, SystemProxyPolicy.ResolveEffectiveType(config));
        Assert.False(SystemProxyPolicy.ConnectionNeedsSystemProxy(config));
    }

    [Fact]
    public void TunConnectionDoesNotOverrideIndependentClearPreference()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear, GameTriggerModes.Vpn, "tun");

        Assert.False(SystemProxyPolicy.ConnectionNeedsSystemProxy(config));
        Assert.Equal(ESysProxyType.ForcedClear, SystemProxyPolicy.ResolveEffectiveType(config));
    }

    [Fact]
    public void ProxyTransportTemporarilyOwnsSystemProxyWithoutChangingPreference()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear, GameTriggerModes.Vpn, "proxy");

        Assert.True(SystemProxyPolicy.ConnectionNeedsSystemProxy(config));
        Assert.Equal(ESysProxyType.ForcedChange, SystemProxyPolicy.ResolveEffectiveType(config));
        Assert.Equal(ESysProxyType.ForcedClear, config.SystemProxyItem.SysProxyType);
    }

    [Fact]
    public void DisconnectRestoresIndependentSetPreferenceAfterProxyTransport()
    {
        var config = CreateConfig(ESysProxyType.ForcedChange, GameTriggerModes.Off, "");

        Assert.Equal(ESysProxyType.ForcedChange, SystemProxyPolicy.ResolveEffectiveType(config));
    }

    [Fact]
    public void LegacyManualProxyRoutesStillRequireProxyCapture()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear, GameTriggerModes.Manual, "");
        config.ConnectionItem.ManualRoutes =
        [
            new ManualRouteSetting { EntryType = "app", Value = "game.exe", Action = "proxy" }
        ];

        Assert.True(SystemProxyPolicy.ConnectionNeedsSystemProxy(config));
        Assert.Equal(ESysProxyType.ForcedChange, SystemProxyPolicy.ResolveEffectiveType(config));
    }

    [Fact]
    public void LegacyManualProxyRoutesDoNotOverrideTunCapture()
    {
        var config = CreateConfig(ESysProxyType.ForcedClear, GameTriggerModes.Manual, "");
        config.TunModeItem.EnableTun = true;
        config.ConnectionItem.ManualRoutes =
        [
            new ManualRouteSetting { EntryType = "app", Value = "game.exe", Action = "proxy" }
        ];

        Assert.False(SystemProxyPolicy.ConnectionNeedsSystemProxy(config));
        Assert.Equal(ESysProxyType.ForcedClear, SystemProxyPolicy.ResolveEffectiveType(config));
    }

    [Fact]
    public void ForceDisableReleasesProxyOwnedByConnectionButLeavesUnchangedPreferenceAlone()
    {
        var owned = CreateConfig(ESysProxyType.Unchanged, GameTriggerModes.Vpn, "proxy");
        var unrelated = CreateConfig(ESysProxyType.Unchanged, GameTriggerModes.Off, "");

        Assert.Equal(ESysProxyType.ForcedClear, SystemProxyPolicy.ResolveEffectiveType(owned, forceDisable: true));
        Assert.Equal(ESysProxyType.Unchanged, SystemProxyPolicy.ResolveEffectiveType(unrelated, forceDisable: true));
    }

    [Theory]
    [InlineData(ESysProxyType.ForcedClear, ESysProxyType.ForcedChange)]
    [InlineData(ESysProxyType.Unchanged, ESysProxyType.ForcedChange)]
    [InlineData(ESysProxyType.Pac, ESysProxyType.ForcedClear)]
    public void QuickToggleUsesAoGPNSetAndClearModes(ESysProxyType current, ESysProxyType expected)
    {
        Assert.Equal(expected, SystemProxyPolicy.Toggle(current));
    }

    private static Config CreateConfig(ESysProxyType proxyType, int mode, string transport)
    {
        return new Config
        {
            SystemProxyItem = new SystemProxyItem { SysProxyType = proxyType },
            TunModeItem = new TunModeItem(),
            ConnectionItem = new ConnectionSettingsItem { Mode = mode, Transport = transport, ManualRoutes = [] },
        };
    }
}
