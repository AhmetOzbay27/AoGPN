using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

public class ManualRouteParserTests
{
    // --- TrySplitPort ---

    [Theory]
    [InlineData("discord.gg:443", "discord.gg", "443")]
    [InlineData("1.2.3.4:8080", "1.2.3.4", "8080")]
    [InlineData("example.com:53", "example.com", "53")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1", "443")]
    public void TrySplitPort_WithPort_SplitsHostAndPort(string input, string expectedHost, string expectedPort)
    {
        ManualRouteParser.TrySplitPort(input, out var host, out var port).Should().BeTrue();
        host.Should().Be(expectedHost);
        port.Should().Be(expectedPort);
    }

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("discord.gg")]
    [InlineData("2001:db8::1")]        // plain IPv6 — colons are not a port
    [InlineData("10.0.0.0/24")]        // CIDR — slash is not a port
    [InlineData("example.com")]
    public void TrySplitPort_WithoutPort_KeepsHostAndEmptyPort(string input)
    {
        ManualRouteParser.TrySplitPort(input, out var host, out var port).Should().BeTrue();
        host.Should().Be(input);
        port.Should().Be("");
    }

    [Theory]
    [InlineData("host:0")]      // port out of range
    [InlineData("host:99999")]  // port out of range
    public void TrySplitPort_InvalidPort_DoesNotSplit(string input)
    {
        ManualRouteParser.TrySplitPort(input, out var host, out var port).Should().BeTrue();
        host.Should().Be(input);
        port.Should().Be("");
    }

    [Fact]
    public void TrySplitPort_Empty_ReturnsFalse()
    {
        ManualRouteParser.TrySplitPort("", out _, out _).Should().BeFalse();
    }

    // --- ClassifyEntryType ---

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("2001:db8::1")]
    [InlineData("10.0.0.0/24")]
    [InlineData("192.168.1.0/30")]
    public void ClassifyEntryType_Addresses_ReturnsIp(string host)
    {
        ManualRouteParser.ClassifyEntryType(host).Should().Be("ip");
    }

    [Theory]
    [InlineData("discord.gg")]
    [InlineData("example.com")]
    [InlineData("sub.domain.org")]
    public void ClassifyEntryType_Domains_ReturnsDomain(string host)
    {
        ManualRouteParser.ClassifyEntryType(host).Should().Be("domain");
    }

    // --- IsValidHost ---

    [Theory]
    [InlineData("discord.gg", "domain")]
    [InlineData("sub.example.com", "domain")]
    public void IsValidHost_Domain_Valid(string host, string type)
    {
        ManualRouteParser.IsValidHost(host, type).Should().BeTrue();
    }

    [Theory]
    [InlineData("bad host", "domain")]
    [InlineData("nodots", "domain")]
    [InlineData("a/b.com", "domain")]
    [InlineData("a\\b.com", "domain")]
    [InlineData("", "domain")]
    public void IsValidHost_Domain_Invalid(string host, string type)
    {
        ManualRouteParser.IsValidHost(host, type).Should().BeFalse();
    }

    [Theory]
    [InlineData("1.2.3.4", "ip")]
    [InlineData("2001:db8::1", "ip")]
    [InlineData("10.0.0.0/24", "ip")]
    [InlineData("2001:db8::/64", "ip")]
    public void IsValidHost_Ip_Valid(string host, string type)
    {
        ManualRouteParser.IsValidHost(host, type).Should().BeTrue();
    }

    [Theory]
    [InlineData("999.1.1.1", "ip")]
    [InlineData("10.0.0.0/33", "ip")]     // IPv4 prefix too large
    [InlineData("not-an-ip/24", "ip")]
    [InlineData("10.0.0.0/", "ip")]
    [InlineData("", "ip")]
    public void IsValidHost_Ip_Invalid(string host, string type)
    {
        ManualRouteParser.IsValidHost(host, type).Should().BeFalse();
    }
}
