using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.Common;

/// <summary>
/// GpnRoutingRuleService — yönlendirme OTORİTESİ (Tier 2 — Rota Merkezileştirmesi).
/// Tek karar kaynağının çekirdek söz varlıklarıyla (sing-box/legacy OutboundTag
/// etiketleri, mihomo superset üyeleri, native yakalama kümesi) eşitliği burada
/// action × yön matrisinde kilitlenir — otorite saptarsa legacy üreticiler de
/// saptar (ve tersi), iki ayrı eylem tablosu sürüklenemez.
/// </summary>
public class GpnRoutingRuleServiceTests
{
    private static readonly string[] Actions = ["vpn", "proxy", "vpn+proxy", "direct", "block", "warp", "", "bilinmeyen"];

    // ── ResolveDestination ↔ legacy tek satır kural üreticisi ─────────────

    [Theory]
    [InlineData("vpn", false)]
    [InlineData("proxy", false)]
    [InlineData("vpn+proxy", false)]
    [InlineData("direct", false)]
    [InlineData("block", false)]
    [InlineData("warp", false)]
    [InlineData("", false)]
    [InlineData("bilinmeyen", false)]
    [InlineData("vpn", true)]
    [InlineData("proxy", true)]
    [InlineData("vpn+proxy", true)]
    [InlineData("direct", true)]
    [InlineData("block", true)]
    [InlineData("warp", true)]
    [InlineData("", true)]
    [InlineData("bilinmeyen", true)]
    public void ResolveDestination_MatchesLegacyEntryRuleTag(string action, bool invert)
    {
        // Legacy (ManualRoutingRules.BuildEntryRule) artık kararı otoriteden alır;
        // bu test delegasyonun çıktıyı DEĞİŞTİRMEDİĞİNİ kilitler (regresyon ağı).
        var legacyTag = ManualRoutingRules.BuildEntryRule("app", "x.exe", null, action, invertManual: invert).OutboundTag;
        var destination = GpnRoutingRuleService.ResolveDestination(action, invert);

        DestinationTagFor(destination).Should().Be(legacyTag, $"action={action} invert={invert}");
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("block", false)]
    [InlineData("warp", false)]
    [InlineData("proxy", false)]
    public void ResolveDestination_Whitelist_MatchesPublicOutboundMap(string action, bool invert)
    {
        // Beyaz listede otorite kararı ile eski eylem→etiket eşlemesi aynıdır
        // (MapActionToOutbound hâlâ görüntü katmanı için korunur).
        var tag = GpnRoutingRuleService.ResolveDestination(action, invert) switch
        {
            GpnRoutingDestination.Direct => Global.DirectTag,
            GpnRoutingDestination.Block => Global.BlockTag,
            GpnRoutingDestination.WarpEgress => Global.WarpTag,
            _ => Global.ProxyTag,
        };
        tag.Should().Be(ManualRoutingRules.MapActionToOutbound(action));
    }

    // ── ResolveCatchAll ↔ legacy yakalayıcı kuralı ────────────────────────

    [Theory]
    [InlineData(GameTriggerModes.Off, false)]
    [InlineData(GameTriggerModes.Off, true)]
    [InlineData(GameTriggerModes.Vpn, false)]
    [InlineData(GameTriggerModes.Vpn, true)]
    [InlineData(GameTriggerModes.Manual, false)]
    [InlineData(GameTriggerModes.Manual, true)]
    public void ResolveCatchAll_MatchesLastManagedRule(int mode, bool invert)
    {
        var rules = ManualRoutingRules.BuildManagedRules(mode, [], invertManual: invert);
        var catchAll = rules.Should().ContainSingle().Subject;

        catchAll.OutboundTag.Should().Be(
            DestinationTagFor(GpnRoutingRuleService.ResolveCatchAll(mode, invert)),
            $"mode={mode} invert={invert}");
        catchAll.Remarks.Should().Be(ManualRoutingRules.ManagedRemarksPrefix + " varsayılan");
    }

    // ── ResolveCatchAll ↔ mihomo superset mod hedefi ──────────────────────

    [Theory]
    [InlineData(GameTriggerModes.Off, false)]
    [InlineData(GameTriggerModes.Off, true)]
    [InlineData(GameTriggerModes.Vpn, false)]
    [InlineData(GameTriggerModes.Vpn, true)]
    [InlineData(GameTriggerModes.Manual, false)]
    [InlineData(GameTriggerModes.Manual, true)]
    public void ResolveCatchAll_MatchesModeTargetMember(int mode, bool invert)
    {
        // Yakalayıcı yalnızca DIRECT ya da tünel grubuna gidebilir.
        var expected = GpnRoutingRuleService.ResolveCatchAll(mode, invert) == GpnRoutingDestination.Tunnel
            ? GpnMihomoConfigService.NodesGroupName
            : GpnSoftRouting.ClashDirect;

        GpnSoftRouting.ModeTarget(mode, invert).Should().Be(expected, $"mode={mode} invert={invert}");
    }

    // ── Member eşitliği (mihomo superset) ─────────────────────────────────

    [Theory]
    [InlineData("vpn", false)]
    [InlineData("proxy", false)]
    [InlineData("warp", false)]
    [InlineData("direct", false)]
    [InlineData("block", false)]
    [InlineData("vpn", true)]
    [InlineData("warp", true)]
    [InlineData("direct", true)]
    [InlineData("block", true)]
    public void MapActionToMember_MatchesDestinationMember(string action, bool invert)
    {
        var destination = GpnRoutingRuleService.ResolveDestination(action, invert);
        var expected = destination switch
        {
            GpnRoutingDestination.Direct => GpnSoftRouting.ClashDirect,
            GpnRoutingDestination.Block => GpnSoftRouting.ClashReject,
            GpnRoutingDestination.WarpEgress => GpnMihomoConfigService.WarpProxyName,
            _ => GpnMihomoConfigService.NodesGroupName,
        };

        GpnSoftRouting.MapActionToMember(action, invert).Should().Be(expected, $"action={action} invert={invert}");
    }

    // ── NormalizeEntries / BuildPlan ──────────────────────────────────────

    [Fact]
    public void BuildPlan_OffMode_ShadowsEveryEntryToDirect()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            App("chrome.exe", "vpn"),
            App("edge.exe", "direct"),
            App("blocked.exe", "block"),
        };
        var plan = GpnRoutingRuleService.BuildPlan(GameTriggerModes.Off, invertManual: false, apps);

        plan.CatchAll.Should().Be(GpnRoutingDestination.Direct);
        plan.Entries.Should().HaveCount(3);
        plan.Entries.Should().OnlyContain(e => e.Destination == GpnRoutingDestination.Direct,
            "Off modunda girişler modun gölgesindedir — hepsi direct");
    }

    [Fact]
    public void BuildPlan_GlobalVpn_ShadowsEveryEntryToTunnel()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            App("chrome.exe", "direct"),
            App("blocked.exe", "block"),
        };
        var plan = GpnRoutingRuleService.BuildPlan(GameTriggerModes.Vpn, invertManual: true, apps);

        plan.CatchAll.Should().Be(GpnRoutingDestination.Tunnel);
        plan.Entries.Should().OnlyContain(e => e.Destination == GpnRoutingDestination.Tunnel);
    }

    [Fact]
    public void BuildPlan_ManualWhitelist_EachEntryKeepsItsRoute()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            App("game.exe", "vpn"),
            App("direct.exe", "direct"),
            App("blocked.exe", "block"),
            App("launcher.exe", "warp"),
        };
        var plan = GpnRoutingRuleService.BuildPlan(GameTriggerModes.Manual, invertManual: false, apps);

        plan.CatchAll.Should().Be(GpnRoutingDestination.Direct);
        plan.Entries.Select(e => e.Destination).Should().Equal(
            GpnRoutingDestination.Tunnel,
            GpnRoutingDestination.Direct,
            GpnRoutingDestination.Block,
            GpnRoutingDestination.WarpEgress);
    }

    [Fact]
    public void BuildPlan_ManualBlacklist_InvertsEntries()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            App("game.exe", "vpn"),
            App("direct.exe", "direct"),
            App("blocked.exe", "block"),
            App("launcher.exe", "warp"),
        };
        var plan = GpnRoutingRuleService.BuildPlan(GameTriggerModes.Manual, invertManual: true, apps);

        plan.CatchAll.Should().Be(GpnRoutingDestination.Tunnel);
        plan.Entries.Select(e => e.Destination).Should().Equal(
            GpnRoutingDestination.Direct,   // vpn listede istisna → direct
            GpnRoutingDestination.Tunnel,   // açık direct → tünel
            GpnRoutingDestination.Block,    // block → block
            GpnRoutingDestination.Direct);  // warp listede istisna → direct
    }

    [Fact]
    public void BuildPlan_NormalizesEmptyValuesAndDefaults()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            App("game.exe", "vpn"),
            new SplitTunnelAppItem { EntryType = "app", Value = "", Action = "vpn" },
            new SplitTunnelAppItem { EntryType = "app", Value = "nullport.exe", Port = null, Action = null },
        };
        var plan = GpnRoutingRuleService.BuildPlan(GameTriggerModes.Manual, invertManual: false, apps);

        plan.Entries.Should().HaveCount(2, "boş değerli satır atlanır");
        plan.Entries[1].Entry.Port.Should().Be("", "port varsayılanı boş");
        plan.Entries[1].Entry.Action.Should().Be("vpn", "eylem varsayılanı vpn");
    }

    [Fact]
    public void NativeCapturePredicate_MatchesTargetNameExtraction()
    {
        // Native yakalama kümesi otoritenin varış kararından türetilir — kurallar ve
        // köprü aynı dili konuşur. Beyaz listede vpn/warp yakalanır, direct/block
        // yakalanmaz; kara listede yalnızca kuralların tünele çevirdiği "direct" girişler.
        var apps = new List<SplitTunnelAppItem>
        {
            App("EscapeFromTarkov.exe", "vpn"),
            App("BsGLauncher.exe", "warp"),
            App("notepad.exe", "direct"),
            App("adware.exe", "block"),
        };

        var whitelist = GpnTargetResolverBridge.ExtractTargetNames(apps, invertManual: false);
        whitelist.Should().BeEquivalentTo(["EscapeFromTarkov.exe", "BsGLauncher.exe"],
            "beyaz listede tünel/WARP egress girişleri yakalanır");

        var blacklist = GpnTargetResolverBridge.ExtractTargetNames(apps, invertManual: true);
        blacklist.Should().BeEquivalentTo(["notepad.exe"],
            "kara listede açık direct girişler tünele çevrilir ve yakalanır");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────

    private static SplitTunnelAppItem App(string value, string action) => new()
    {
        EntryType = "app",
        Value = value,
        Port = null,
        Action = action,
        DisplayName = value,
    };

    private static string DestinationTagFor(GpnRoutingDestination destination) => destination switch
    {
        GpnRoutingDestination.Direct => Global.DirectTag,
        GpnRoutingDestination.Block => Global.BlockTag,
        GpnRoutingDestination.WarpEgress => Global.WarpTag,
        _ => Global.ProxyTag,
    };
}
