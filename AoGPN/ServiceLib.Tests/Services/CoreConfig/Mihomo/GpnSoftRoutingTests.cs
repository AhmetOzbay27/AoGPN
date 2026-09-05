using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.Services.CoreConfig.Mihomo;

/// <summary>
/// GpnSoftRouting — kesintisiz (make-before-break) rota geçişinin saf mantığı:
/// mod/yön/giriş rotası → seçim grubu vektörü. Üretilen vektör, legacy kural
/// üreticisinin (ManualRoutingRules) o moddaki davranışıyla aynı trafik sonucunu
/// verir; testler bu eşitliği mod × yön × action matrisinde doğrular.
/// </summary>
public class GpnSoftRoutingTests
{
    private static GpnSoftRoutingPolicy Policy(
        int mode,
        bool invert,
        params (string Type, string Value, string Port, string Action)[] entries)
        => new(mode, invert, entries
            .Select(e => new GpnSoftRoutingEntry(e.Type, e.Value, e.Port, e.Action))
            .ToList());

    private static (string Type, string Value, string Port, string Action) App(string value, string action)
        => ("app", value, "", action);

    [Fact]
    public void Off_EverythingDirect_RegardlessOfInvertAndRoutes()
    {
        foreach (var invert in new[] { false, true })
        {
            var policy = Policy(GameTriggerModes.Off, invert,
                App("chrome.exe", "vpn"), App("edge.exe", "direct"), App("blocked.exe", "block"));
            var (modeTarget, appTargets) = GpnSoftRouting.ComputeSelectionVector(policy);

            modeTarget.Should().Be(GpnSoftRouting.ClashDirect);
            appTargets.Should().Equal(
                GpnSoftRouting.ClashDirect, GpnSoftRouting.ClashDirect, GpnSoftRouting.ClashDirect);
        }
    }

    [Fact]
    public void GlobalVpn_EverythingTunnels_EntriesIgnored()
    {
        foreach (var invert in new[] { false, true })
        {
            var policy = Policy(GameTriggerModes.Vpn, invert,
                App("chrome.exe", "vpn"), App("edge.exe", "direct"), App("blocked.exe", "block"));
            var (modeTarget, appTargets) = GpnSoftRouting.ComputeSelectionVector(policy);

            modeTarget.Should().Be(GpnMihomoConfigService.NodesGroupName);
            appTargets.Should().Equal(
                GpnMihomoConfigService.NodesGroupName,
                GpnMihomoConfigService.NodesGroupName,
                GpnMihomoConfigService.NodesGroupName);
        }
    }

    [Fact]
    public void ManualWhitelist_CatchAllDirect_EntriesKeepTheirRoute()
    {
        var policy = Policy(GameTriggerModes.Manual, invert: false,
            App("chrome.exe", "vpn"),
            App("edge.exe", "direct"),
            App("blocked.exe", "block"),
            App("warped.exe", "warp"));
        var (modeTarget, appTargets) = GpnSoftRouting.ComputeSelectionVector(policy);

        modeTarget.Should().Be(GpnSoftRouting.ClashDirect);
        appTargets.Should().Equal(
            GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashDirect,
            GpnSoftRouting.ClashReject,
            GpnMihomoConfigService.WarpProxyName);
    }

    [Fact]
    public void ManualBlacklist_CatchAllTunnels_EntriesInverted()
    {
        // Kara liste (dışlama): vpn/warp eylemli girişler istisna = direct kalır,
        // açık "direct" giriş tünele çevrilir, block block kalır — ManualRoutingRules
        // BuildEntryRule(invert: true) davranışının birebir karşılığı.
        var policy = Policy(GameTriggerModes.Manual, invert: true,
            App("chrome.exe", "vpn"),
            App("edge.exe", "direct"),
            App("blocked.exe", "block"),
            App("warped.exe", "warp"));
        var (modeTarget, appTargets) = GpnSoftRouting.ComputeSelectionVector(policy);

        modeTarget.Should().Be(GpnMihomoConfigService.NodesGroupName);
        appTargets.Should().Equal(
            GpnSoftRouting.ClashDirect,       // vpn listede istisna → direct
            GpnMihomoConfigService.NodesGroupName, // açık direct → tünel
            GpnSoftRouting.ClashReject,       // block → block
            GpnSoftRouting.ClashDirect);      // warp listede istisna → direct
    }

    [Fact]
    public void ManualWhitelist_DualConnection_WarpTargetsVlessLauncher()
    {
        // Çift Bağlantı (Bölünmüş Tünelleme): bypass düğümü yapılandırılmış politikada
        // "warp" egress (BsGLauncher.exe) warp-socks yerine vless-launcher'a gider —
        // üretici bu modda WARP SOCKS5 zincirini hiç üretmez.
        var policy = Policy(GameTriggerModes.Manual, invert: false,
            App("BsGLauncher.exe", "warp"),
            App("EscapeFromTarkov.exe", "vpn"))
            with { WarpEgressProxy = GpnMihomoConfigService.BypassProxyName };
        var (modeTarget, appTargets) = GpnSoftRouting.ComputeSelectionVector(policy);

        modeTarget.Should().Be(GpnSoftRouting.ClashDirect);
        appTargets.Should().Equal(
            GpnMihomoConfigService.BypassProxyName, // launcher → VLESS/Reality bypass
            GpnMihomoConfigService.NodesGroupName); // oyun → WG tüneli
    }

    [Fact]
    public void ManualBlacklist_DualConnection_WarpEntriesStillInvertedToDirect()
    {
        // Kara liste yönü Çift Bağlantıdan bağımsızdır: warp egressli girişler listelenen
        // istisnalardır ve egress hangi düğüm olursa olsun direct kalır.
        var policy = Policy(GameTriggerModes.Manual, invert: true, App("BsGLauncher.exe", "warp"))
            with { WarpEgressProxy = GpnMihomoConfigService.BypassProxyName };
        var (modeTarget, appTargets) = GpnSoftRouting.ComputeSelectionVector(policy);

        modeTarget.Should().Be(GpnMihomoConfigService.NodesGroupName);
        appTargets.Should().Equal(GpnSoftRouting.ClashDirect);
    }

    [Fact]
    public void ResolveLauncherEgressTarget_HealthyUsesWarpEgress_DegradedDirect()
    {
        // Sağlıklı: legacy warp-socks / çift bağlantıda vless-launcher (politika egress'i).
        GpnSoftRouting.ResolveLauncherEgressTarget(false).Should().Be(GpnMihomoConfigService.WarpProxyName);
        GpnSoftRouting.ResolveLauncherEgressTarget(false, GpnMihomoConfigService.BypassProxyName)
            .Should().Be(GpnMihomoConfigService.BypassProxyName);
        // Degrade (WARP faulted): her iki biçimde de DIRECT.
        GpnSoftRouting.ResolveLauncherEgressTarget(true).Should().Be(GpnSoftRouting.ClashDirect);
        GpnSoftRouting.ResolveLauncherEgressTarget(true, GpnMihomoConfigService.BypassProxyName)
            .Should().Be(GpnSoftRouting.ClashDirect);
        // Kanonik üye sırası: warp egress önce (varsayılan seçim), DIRECT sonra.
        GpnSoftRouting.LauncherMemberOrder().Should().Equal(
            GpnMihomoConfigService.WarpProxyName, GpnSoftRouting.ClashDirect);
        GpnSoftRouting.LauncherMemberOrder(GpnMihomoConfigService.BypassProxyName).Should().Equal(
            GpnMihomoConfigService.BypassProxyName, GpnSoftRouting.ClashDirect);
        GpnSoftRouting.LauncherGroupName.Should().Be("GPN-LAUNCHER");
    }

    [Fact]
    public void IsStructuralEntryChange_DetectsOnlyEntrySetChanges()
    {
        var policy = Policy(GameTriggerModes.Manual, false,
            App("EscapeFromTarkov.exe", "vpn"), App("BsGLauncher.exe", "warp"));

        // Superset oturum yoksa yapısal değişiklik sayılmaz → çağıran reload yolunu kullanır.
        GpnSoftRouting.IsStructuralEntryChange(null, policy).Should().BeFalse();

        var live = policy.EntryKeys.ToList();
        // Birebir aynı giriş seti → yapısal değişiklik yok (rota değişimi EntryKeys dışıdır).
        GpnSoftRouting.IsStructuralEntryChange(live, policy).Should().BeFalse();

        // Yalnızca ROTA değişimi (action) yapısal DEĞİLDİR — yumuşak yol hâlâ uygulayabilir.
        var routeChanged = Policy(GameTriggerModes.Manual, false,
            App("EscapeFromTarkov.exe", "vpn"), App("BsGLauncher.exe", "direct"));
        GpnSoftRouting.IsStructuralEntryChange(live, routeChanged).Should().BeFalse();

        // Giriş EKLENDİ → yapısal.
        var added = Policy(GameTriggerModes.Manual, false,
            App("EscapeFromTarkov.exe", "vpn"), App("BsGLauncher.exe", "warp"), App("discord.exe", "direct"));
        GpnSoftRouting.IsStructuralEntryChange(live, added).Should().BeTrue();

        // Giriş SİLİNDİ → yapısal.
        var removed = Policy(GameTriggerModes.Manual, false, App("EscapeFromTarkov.exe", "vpn"));
        GpnSoftRouting.IsStructuralEntryChange(live, removed).Should().BeTrue();

        // SIRALAMA değişti → yapısal (kural satırları sıraya göre üretilir).
        var reordered = Policy(GameTriggerModes.Manual, false,
            App("BsGLauncher.exe", "warp"), App("EscapeFromTarkov.exe", "vpn"));
        GpnSoftRouting.IsStructuralEntryChange(live, reordered).Should().BeTrue();
    }

    [Fact]
    public void BuildPolicy_ConfigWithBypass_ProducesDualWarpVector()
    {
        // Küresel ayar (GuiItem.VlessBypassNodeJson) dolu bir Config'ten üretilen
        // politika, warp egressli satırları vless-launcher'a hedefler — GpnCoreLauncher'ın
        // context kararıyla aynı eşik (geçerli VlessProfileItem) kullanılır.
        var config = new Config
        {
            GuiItem = new GUIItem
            {
                VlessBypassNodeJson = JsonUtils.Serialize(new VlessProfileItem(
                    "Launcher Bypass", "1.2.3.4", 443, "uuid-1", "pubkey-1"), indented: false),
            },
            ConnectionItem = new ConnectionSettingsItem
            {
                Mode = GameTriggerModes.Manual,
                ManualRoutes =
                [
                    new ManualRouteSetting { EntryType = "app", Value = "BsGLauncher.exe", Action = "warp" },
                    new ManualRouteSetting { EntryType = "app", Value = "EscapeFromTarkov.exe", Action = "vpn" },
                ],
            },
        };

        var policy = GpnSoftRouting.BuildPolicy(config);
        var (modeTarget, appTargets) = GpnSoftRouting.ComputeSelectionVector(policy);

        modeTarget.Should().Be(GpnSoftRouting.ClashDirect);
        appTargets.Should().Equal(
            GpnMihomoConfigService.BypassProxyName, // BsGLauncher.exe → VLESS launcher egress
            GpnMihomoConfigService.NodesGroupName); // EscapeFromTarkov.exe → WG tüneli
        policy.WarpEgressProxy.Should().Be(GpnMihomoConfigService.BypassProxyName);
    }

    [Fact]
    public void ResolveWarpEgressProxy_ConfiguredBypass_ReturnsBypassProxy()
    {
        var config = new Config
        {
            GuiItem = new GUIItem
            {
                VlessBypassNodeJson = JsonUtils.Serialize(
                    new VlessProfileItem("b", "1.2.3.4", 443, "u", "k"), indented: false),
            },
        };

        GpnSoftRouting.ResolveWarpEgressProxy(config).Should().Be(GpnMihomoConfigService.BypassProxyName);
    }

    [Fact]
    public void ResolveWarpEgressProxy_MissingOrInvalidSetting_ReturnsNullForLegacyWarp()
    {
        // Boş/null ayar ve bozuk JSON legacy davranışı korur (warp → warp-socks).
        GpnSoftRouting.ResolveWarpEgressProxy(null).Should().BeNull();
        GpnSoftRouting.ResolveWarpEgressProxy(new Config()).Should().BeNull();
        GpnSoftRouting.ResolveWarpEgressProxy(new Config { GuiItem = new GUIItem() }).Should().BeNull();
        GpnSoftRouting.ResolveWarpEgressProxy(new Config { GuiItem = new GUIItem { VlessBypassNodeJson = "{ not json" } })
            .Should().BeNull();
    }

    [Fact]
    public void ManualMapping_MatchesLegacyRuleGeneration_PerActionAndDirection()
    {
        // MapActionToMember çıktısı, ManualRoutingRules.BuildEntryRule'in invert'li
        // ve invert'siz ürettiği hedeflerle aynı olmalı (satırlar aynı trafiği çizer).
        foreach (var action in new[] { "vpn", "proxy", "vpn+proxy", "direct", "block", "warp" })
        {
            var legacy = ManualRoutingRules.MapActionToOutbound(action);
            var member = GpnSoftRouting.MapActionToMember(action, invertManual: false);
            member.Should().Be(MemberForOutbound(legacy), $"action={action} invert=false");

            var legacyInv = ManualRoutingRules.BuildEntryRule("app", "x.exe", null, action, invertManual: true).OutboundTag;
            var memberInv = GpnSoftRouting.MapActionToMember(action, invertManual: true);
            memberInv.Should().Be(MemberForOutbound(legacyInv), $"action={action} invert=true");
        }
    }

    private static string MemberForOutbound(string outboundTag)
    {
        return outboundTag switch
        {
            _ when outboundTag == Global.DirectTag => GpnSoftRouting.ClashDirect,
            _ when outboundTag == Global.BlockTag => GpnSoftRouting.ClashReject,
            _ when outboundTag == Global.WarpTag => GpnMihomoConfigService.WarpProxyName,
            _ => GpnMihomoConfigService.NodesGroupName, // proxy/vpn → tünel grubu
        };
    }

    [Fact]
    public void EntryKeys_AreStructuralOnly_RouteChangesKeepIdentity()
    {
        var entry = new GpnSoftRoutingEntry("app", "chrome.exe", "", "vpn");
        var sameRouteChanged = entry with { Action = "direct" };
        var differentValue = entry with { Value = "edge.exe" };

        GpnSoftRouting.EntryKey(entry).Should().Be(GpnSoftRouting.EntryKey(sameRouteChanged),
            "rota değişimi seçimdir — yapısal parmak izini değiştirmez");
        GpnSoftRouting.EntryKey(entry).Should().NotBe(GpnSoftRouting.EntryKey(differentValue));
    }

    [Fact]
    public void AppGroupNames_AreIndexBasedAndStable()
    {
        GpnSoftRouting.AppGroupName(0).Should().Be("ao-0");
        GpnSoftRouting.AppGroupName(12).Should().Be("ao-12");
        GpnSoftRouting.AppGroupName(0).Should().NotBe(GpnSoftRouting.AppGroupName(1));
    }
}
