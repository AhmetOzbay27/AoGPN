using AwesomeAssertions;
using ServiceLib;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

public class ManualRoutingRulesTests
{
    // --- MapActionToOutbound ---

    [Theory]
    [InlineData("vpn")]
    [InlineData("proxy")]
    [InlineData("vpn+proxy")]
    [InlineData("unknown")]
    public void MapActionToOutbound_ProxyFamily_ReturnsProxyTag(string action)
    {
        ManualRoutingRules.MapActionToOutbound(action).Should().Be(Global.ProxyTag);
    }

    [Fact]
    public void MapActionToOutbound_Direct_ReturnsDirectTag()
    {
        ManualRoutingRules.MapActionToOutbound("direct").Should().Be(Global.DirectTag);
    }

    [Fact]
    public void MapActionToOutbound_Block_ReturnsBlockTag()
    {
        ManualRoutingRules.MapActionToOutbound("block").Should().Be(Global.BlockTag);
    }

    [Fact]
    public void MapActionToOutbound_Warp_ReturnsWarpTag()
    {
        ManualRoutingRules.MapActionToOutbound("warp").Should().Be(Global.WarpTag);
    }

    // --- BuildManagedRules: mode behaviour ---

    [Fact]
    public void BuildManagedRules_OffMode_OnlyCatchAllDirect_EvenWithEntries()
    {
        var apps = new[] { App("chrome.exe"), App("discord.gg", entryType: "domain") };

        var rules = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Off, apps);

        rules.Should().HaveCount(1);
        rules[0].OutboundTag.Should().Be(Global.DirectTag);
        rules[0].Port.Should().Be("0-65535");
    }

    [Fact]
    public void BuildManagedRules_VpnMode_OnlyCatchAllProxy()
    {
        var apps = new[] { App("chrome.exe") };

        var rules = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Vpn, apps);

        rules.Should().HaveCount(1);
        rules[0].OutboundTag.Should().Be(Global.ProxyTag);
    }

    [Fact]
    public void BuildManagedRules_ManualMode_EndsWithDirectCatchAll()
    {
        var rules = ManualRoutingRules.BuildManagedRules(GameTriggerModes.Manual, [App("chrome.exe")]);

        var catchAll = rules[^1];
        catchAll.OutboundTag.Should().Be(Global.DirectTag); // unlisted apps stay direct
        catchAll.Port.Should().Be("0-65535");
        rules.Should().HaveCount(2);
    }

    // --- BuildManagedRules: entry types ---

    [Fact]
    public void BuildManagedRules_WarpEntry_WritesWarpRule()
    {
        var rule = ManualRoutingRules.BuildEntryRule(App("BsGLauncher.exe", action: "warp"));

        rule.Process.Should().ContainSingle("BsGLauncher.exe");
        rule.OutboundTag.Should().Be(Global.WarpTag);
        rule.Network.Should().Be("tcp,udp");
    }

    [Fact]
    public void BuildManagedRules_WarpEntry_BlacklistDirection_BecomesDirect()
    {
        // Kara liste: warp (tünel-benzeri) atama istisnadır — kurallarda direct olur.
        var rule = ManualRoutingRules.BuildEntryRule(App("BsGLauncher.exe", action: "warp"), invertManual: true);

        rule.OutboundTag.Should().Be(Global.DirectTag);
    }

    [Fact]
    public void BuildManagedRules_AppEntry_WritesProcessRule()
    {
        var rule = ManualRoutingRules.BuildEntryRule(App("chrome.exe", action: "block"));

        rule.Process.Should().ContainSingle("chrome.exe");
        rule.Domain.Should().BeNull();
        rule.Ip.Should().BeNull();
        rule.Port.Should().BeNull();
        rule.OutboundTag.Should().Be(Global.BlockTag);
        rule.Network.Should().Be("tcp,udp");
        rule.Enabled.Should().BeTrue();
        rule.Remarks.Should().Be($"{ManualRoutingRules.ManagedRemarksPrefix} chrome.exe");
    }

    [Fact]
    public void BuildManagedRules_DomainEntry_WritesDomainRule()
    {
        var rule = ManualRoutingRules.BuildEntryRule(App("discord.gg", entryType: "domain"));

        rule.Domain.Should().ContainSingle("discord.gg");
        rule.Process.Should().BeNull();
        rule.Ip.Should().BeNull();
        rule.Port.Should().BeNull();
    }

    [Fact]
    public void BuildManagedRules_IpEntry_WritesIpRule()
    {
        var rule = ManualRoutingRules.BuildEntryRule(App("1.2.3.4", entryType: "ip"));

        rule.Ip.Should().ContainSingle("1.2.3.4");
        rule.Domain.Should().BeNull();
        rule.Process.Should().BeNull();
    }

    [Fact]
    public void BuildManagedRules_IpWithPort_WritesPort()
    {
        var rule = ManualRoutingRules.BuildEntryRule(App("1.2.3.4", entryType: "ip", port: "443"));

        rule.Ip.Should().ContainSingle("1.2.3.4");
        rule.Port.Should().Be("443");
    }

    [Fact]
    public void BuildManagedRules_DomainWithPort_WritesPort()
    {
        var rule = ManualRoutingRules.BuildEntryRule(App("discord.gg", entryType: "domain", port: "443"));

        rule.Domain.Should().ContainSingle("discord.gg");
        rule.Port.Should().Be("443");
    }

    [Fact]
    public void BuildManagedRules_EmptyValue_Skipped()
    {
        var rules = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual,
            [App(""), App("chrome.exe")]);

        rules.Should().HaveCount(2); // one entry + catch-all
        rules[0].Process.Should().ContainSingle("chrome.exe");
    }

    // --- StripManagedRules ---

    [Fact]
    public void StripManagedRules_RemovesManagedRules_KeepsUserRules()
    {
        var managed = new List<RulesItem>
        {
            ManualRoutingRules.BuildEntryRule(App("chrome.exe", action: "vpn")),
            ManualRoutingRules.BuildCatchAllRule(Global.ProxyTag),
            new RulesItem
            {
                Id = Utils.GetGuid(false),
                Remarks = "user-rule",
                OutboundTag = Global.ProxyTag,
                Domain = ["custom.example.com"],
                Enabled = true,
            },
            new RulesItem
            {
                Port = "443",
                Network = "udp",
                OutboundTag = Global.BlockTag,
            },
        };
        var routing = new RoutingItem
        {
            Id = "r1",
            Remarks = "default",
            RuleSet = JsonUtils.Serialize(managed, false),
            RuleNum = managed.Count,
        };

        var stripped = ManualRoutingRules.StripManagedRules(routing);

        stripped.Should().NotBeSameAs(routing);
        var rules = JsonUtils.Deserialize<List<RulesItem>>(stripped!.RuleSet)!;
        rules.Should().ContainSingle("only the user-defined rule survives");
        rules[0].Remarks.Should().Be("user-rule");
        stripped.RuleNum.Should().Be(1);
    }

    [Fact]
    public void StripManagedRules_NoManagedRules_ReturnsSameInstance()
    {
        var userRules = new List<RulesItem>
        {
            new()
            {
                Id = Utils.GetGuid(false),
                Remarks = "user",
                OutboundTag = Global.DirectTag,
                Domain = ["x.example"],
                Enabled = true,
            },
        };
        var routing = new RoutingItem
        {
            Id = "r1",
            RuleSet = JsonUtils.Serialize(userRules, false),
            RuleNum = 1,
        };

        ManualRoutingRules.StripManagedRules(routing).Should().BeSameAs(routing);
    }

    [Fact]
    public void StripManagedRules_NullOrEmpty_ReturnsSameInstance()
    {
        ManualRoutingRules.StripManagedRules(null).Should().BeNull();
        var routing = new RoutingItem { Id = "r1", RuleSet = "" };
        ManualRoutingRules.StripManagedRules(routing).Should().BeSameAs(routing);
    }

    // --- IsManagedRule ---

    [Fact]
    public void IsManagedRule_ManagedRemarks_ReturnsTrue()
    {
        var rule = ManualRoutingRules.BuildEntryRule(App("chrome.exe"));
        ManualRoutingRules.IsManagedRule(rule).Should().BeTrue();
    }

    [Fact]
    public void IsManagedRule_CatchAll_ReturnsTrue()
    {
        var rule = ManualRoutingRules.BuildCatchAllRule(Global.DirectTag);
        ManualRoutingRules.IsManagedRule(rule).Should().BeTrue();
    }

    [Fact]
    public void IsManagedRule_Udp443Block_ReturnsTrue()
    {
        var rule = new RulesItem
        {
            Port = "443",
            Network = "udp",
            OutboundTag = Global.BlockTag,
        };
        ManualRoutingRules.IsManagedRule(rule).Should().BeTrue();
    }

    [Fact]
    public void IsManagedRule_CustomRule_ReturnsFalse()
    {
        var rule = new RulesItem
        {
            Domain = ["custom.example.com"],
            OutboundTag = Global.ProxyTag,
        };
        ManualRoutingRules.IsManagedRule(rule).Should().BeFalse();
    }

    private static SplitTunnelAppItem App(string value, string action = "proxy", string entryType = "app", string port = "")
    {
        return new SplitTunnelAppItem
        {
            EntryType = entryType,
            Value = value,
            Port = port,
            Action = action,
        };
    }
}
