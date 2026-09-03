using System.Text.Json;
using AwesomeAssertions;
using ServiceLib.Common;

namespace ServiceLib.Tests;

public sealed class DashboardMessageContractTests
{
    [Fact]
    public void ParsesAllowedActionAndClonesPayload()
    {
        DashboardMessageParser.TryParse("{\"action\":\"select_node\",\"indexId\":\"node-1\"}", out var message).Should().BeTrue();

        message.Should().NotBeNull();
        message!.Action.Should().Be("select_node");
        message.TryGetString("indexId", out var indexId).Should().BeTrue();
        indexId.Should().Be("node-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"action\":\"execute_shell\"}")]
    [InlineData("[]")]
    [InlineData("{\"action\":12}")]
    public void RejectsMalformedUnknownOrWrongShape(string raw)
    {
        DashboardMessageParser.TryParse(raw, out _).Should().BeFalse();
    }

    [Fact]
    public void ParsesAddDomainRoutePayloadFromGameBoostButtons()
    {
        // Exact wire payload of the "BSG API → WARP" quick button and the "+ Domain"
        // form (Temalar/app.js + skin bridges) — must survive the policy gate and
        // reach the MainWindow add_domain_route case.
        var raw = "{\"action\":\"add_domain_route\",\"value\":\"escapefromtarkov.com\","
            + "\"route\":\"warp\",\"displayName\":\"BSG API (escapefromtarkov.com)\"}";
        DashboardMessageParser.TryParse(raw, out var message).Should().BeTrue();

        message.Should().NotBeNull();
        message!.Action.Should().Be("add_domain_route");
        message.TryGetString("value", out var value).Should().BeTrue();
        value.Should().Be("escapefromtarkov.com");
        message.TryGetString("route", out var route).Should().BeTrue();
        route.Should().Be("warp");
        message.TryGetString("displayName", out var displayName).Should().BeTrue();
        displayName.Should().Be("BSG API (escapefromtarkov.com)");
    }

    [Fact]
    public void ParsesMoveRoutePayloadFromRowReorderControls()
    {
        // Wire payload of the Game Boost row up/down arrows (entryType + value +
        // direction identify a domain/IP row exactly like the app rows).
        var raw = "{\"action\":\"move_route\",\"entryType\":\"domain\","
            + "\"value\":\"profile.tarkov.com\",\"direction\":\"up\"}";
        DashboardMessageParser.TryParse(raw, out var message).Should().BeTrue();

        message.Should().NotBeNull();
        message!.Action.Should().Be("move_route");
        message.TryGetString("entryType", out var entryType).Should().BeTrue();
        entryType.Should().Be("domain");
        message.TryGetString("value", out var value).Should().BeTrue();
        value.Should().Be("profile.tarkov.com");
        message.TryGetString("direction", out var direction).Should().BeTrue();
        direction.Should().Be("up");
    }

    [Fact]
    public void StringArrayAccessorRejectsNonStringsAndOversizedValues()
    {
        var raw = "{\"action\":\"test_nodes\",\"indexIds\":[\"one\",12,\"" + new string('x', 65) + "\"]}";
        DashboardMessageParser.TryParse(raw, out var message).Should().BeTrue();

        message!.TryGetStringArray("indexIds", out var values).Should().BeTrue();
        values.Should().Equal("one");
    }

    [Fact]
    public void BooleanAccessorRequiresBooleanJsonValue()
    {
        DashboardMessageParser.TryParse("{\"action\":\"set_verbose_log\",\"enabled\":true}", out var message).Should().BeTrue();

        message!.TryGetBoolean("enabled", out var enabled).Should().BeTrue();
        enabled.Should().BeTrue();
        message.TryGetBoolean("missing", out _).Should().BeFalse();
    }
}
