using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests;

public class ConnectionPolicyTests
{
    [Theory]
    [InlineData("wg", ConnectionProtocolPreference.WireGuard)]
    [InlineData("reality", ConnectionProtocolPreference.Mimic)]
    [InlineData("tuic", ConnectionProtocolPreference.Hysteria2)]
    [InlineData("ovpn", ConnectionProtocolPreference.OpenVpn)]
    [InlineData("not-a-real-strategy", ConnectionProtocolPreference.Automatic)]
    public void ProtocolPreference_NormalizesSafeAliases(string input, string expected)
    {
        ConnectionProtocolPreference.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void AutomaticSelection_PrefersNativeLowOverheadProfile()
    {
        var vless = new ProfileItem { IndexId = "vless", ConfigType = EConfigType.VLESS };
        var hysteria = new ProfileItem { IndexId = "hy2", ConfigType = EConfigType.Hysteria2 };
        var wireguard = new ProfileItem { IndexId = "wg", ConfigType = EConfigType.WireGuard };

        var selected = ConnectionProtocolPolicy.SelectBest([vless, hysteria, wireguard]);

        selected.Should().BeSameAs(wireguard);
    }

    [Fact]
    public void ExplicitPreference_DoesNotPretendAnotherProfileSupportsIt()
    {
        var profile = new ProfileItem
        {
            IndexId = "plain-vless",
            ConfigType = EConfigType.VLESS,
            StreamSecurity = string.Empty,
        };

        var decision = ConnectionProtocolPolicy.Evaluate(
            profile,
            ConnectionProtocolPreference.WireGuard,
            transportUsesTun: true);

        decision.IsAvailable.Should().BeFalse();
        decision.SelectedConfigType.Should().Be(EConfigType.VLESS);
    }

    [Fact]
    public void WindowsTunDefaults_RejectJumboMtuAndUnknownStack()
    {
        WindowsTunStabilityPolicy.NormalizeMtu(9000).Should().Be(WindowsTunStabilityPolicy.DefaultMtu);
        WindowsTunStabilityPolicy.NormalizeMtu(1400).Should().Be(1400);
        WindowsTunStabilityPolicy.NormalizeStack("unknown").Should().Be(WindowsTunStabilityPolicy.DefaultStack);
        WindowsTunStabilityPolicy.NormalizeStack("mixed").Should().Be("mixed");
    }
}
