using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class RealityCoreFallbackAdvisorTests
{
    private static ProfileItem CreateRealityNode()
    {
        return new ProfileItem
        {
            IndexId = "node-1",
            ConfigType = EConfigType.VLESS,
            StreamSecurity = Global.StreamSecurityReality,
            Sni = "swdist.apple.com",
            PublicKey = "public-key",
            ShortId = "0123456789abcdef",
        };
    }

    [Fact]
    public void Evaluate_NonRealityNode_ReturnsNone()
    {
        var node = CreateRealityNode();
        node.StreamSecurity = "tls";

        var advice = RealityCoreFallbackAdvisor.Evaluate(
            node, failedCoreType: ECoreType.sing_box,
            tunEnabled: false, connectionUp: false, modeOff: false);

        advice.Should().Be(RealityFallbackAdvice.None);
    }

    [Fact]
    public void Evaluate_RealityNodeFailedOnXray_ReturnsNone()
    {
        var node = CreateRealityNode();

        var advice = RealityCoreFallbackAdvisor.Evaluate(
            node, failedCoreType: ECoreType.Xray,
            tunEnabled: false, connectionUp: false, modeOff: false);

        advice.Should().Be(RealityFallbackAdvice.None);
    }

    [Fact]
    public void Evaluate_RealityNodeSingBoxFailedTunOn_ReturnsSuggestXray()
    {
        var node = CreateRealityNode();

        var advice = RealityCoreFallbackAdvisor.Evaluate(
            node, failedCoreType: ECoreType.sing_box,
            tunEnabled: true, connectionUp: false, modeOff: false);

        advice.Should().Be(RealityFallbackAdvice.SuggestXray);
    }

    [Fact]
    public void Evaluate_RealityNodeSingBoxFailedTunOff_ReturnsSwitchToXray()
    {
        var node = CreateRealityNode();

        var advice = RealityCoreFallbackAdvisor.Evaluate(
            node, failedCoreType: ECoreType.sing_box,
            tunEnabled: false, connectionUp: false, modeOff: false);

        advice.Should().Be(RealityFallbackAdvice.SwitchToXray);
    }

    [Fact]
    public void Evaluate_ConnectionUp_ReturnsNone()
    {
        var node = CreateRealityNode();

        var advice = RealityCoreFallbackAdvisor.Evaluate(
            node, failedCoreType: ECoreType.sing_box,
            tunEnabled: false, connectionUp: true, modeOff: false);

        advice.Should().Be(RealityFallbackAdvice.None);
    }

    [Fact]
    public void Evaluate_ModeOff_ReturnsNone()
    {
        var node = CreateRealityNode();

        var advice = RealityCoreFallbackAdvisor.Evaluate(
            node, failedCoreType: ECoreType.sing_box,
            tunEnabled: false, connectionUp: false, modeOff: true);

        advice.Should().Be(RealityFallbackAdvice.None);
    }

    [Fact]
    public void Evaluate_NullNode_ReturnsNone()
    {
        var advice = RealityCoreFallbackAdvisor.Evaluate(
            null, failedCoreType: ECoreType.sing_box,
            tunEnabled: false, connectionUp: false, modeOff: false);

        advice.Should().Be(RealityFallbackAdvice.None);
    }

    [Fact]
    public void Evaluate_FailedCoreNull_ReturnsNone()
    {
        var node = CreateRealityNode();

        var advice = RealityCoreFallbackAdvisor.Evaluate(
            node, failedCoreType: null,
            tunEnabled: false, connectionUp: false, modeOff: false);

        advice.Should().Be(RealityFallbackAdvice.None);
    }

    [Fact]
    public void IsRealityNode_ChecksStreamSecurity()
    {
        RealityCoreFallbackAdvisor.IsRealityNode(CreateRealityNode()).Should().BeTrue();
        RealityCoreFallbackAdvisor.IsRealityNode(null).Should().BeFalse();

        var tlsNode = CreateRealityNode();
        tlsNode.StreamSecurity = "tls";
        RealityCoreFallbackAdvisor.IsRealityNode(tlsNode).Should().BeFalse();
    }

    [Fact]
    public void ApplyXraySwitch_SetsNodeCoreTypeToXray()
    {
        var node = CreateRealityNode();
        node.CoreType = ECoreType.sing_box;

        RealityCoreFallbackAdvisor.ApplyXraySwitch(node);

        node.CoreType.Should().Be(ECoreType.Xray);
    }
}
