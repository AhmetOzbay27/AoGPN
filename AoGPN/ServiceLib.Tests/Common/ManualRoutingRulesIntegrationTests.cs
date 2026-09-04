using AwesomeAssertions;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Handler;
using ServiceLib.Helper;
using ServiceLib.Models;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using Xunit;

namespace ServiceLib.Tests.Common;

/// <summary>
/// Integration tests that verify the managed routing rules flow from
/// <see cref="ManualRoutingRules.BuildManagedRules"/> through
/// <see cref="ConfigHandler.SaveRoutingItem"/> and back — the same pipeline
/// that <c>SplitTunnelViewModel.SaveRulesAsync</c> calls. These tests
/// complement the pure unit tests in <see cref="ManualRoutingRulesTests"/>
/// by exercising the database round-trip.
/// </summary>
[Collection("SharedDatabase")]
public class ManualRoutingRulesIntegrationTests
{
    // ----- Infrastructure -----

    /// <summary>Ensures no leftover routing items from other test suites interfere.</summary>
    private static async Task CleanRoutingItemsAsync()
    {
        // Tabloları AppManager.InitApp yaratır ama test host'u onu hiç çalıştırmaz;
        // temiz bir bin'de guiNDB.db tablosuzdur ve bu sorgu "no such table" ile düşer.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        var all = await SQLiteHelper.Instance.TableAsync<RoutingItem>().ToListAsync();
        foreach (var r in all)
        {
            await SQLiteHelper.Instance.DeleteAsync(r);
        }
    }

    private static Config CreateTestConfig()
    {
        return new Config
        {
            CoreBasicItem = new CoreBasicItem { Loglevel = "warning" },
            TunModeItem = new TunModeItem { EnableTun = false },
            RoutingBasicItem = new RoutingBasicItem
            {
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
                RoutingIndexId = string.Empty,
            },
            GuiItem = new GUIItem { EnableStatistics = false },
            UiItem = new UIItem
            {
                CurrentLanguage = "en",
                CurrentFontFamily = "sans",
                MainColumnItem = [],
                WindowSizeItem = [],
            },
            ConstItem = new ConstItem(),
            SimpleDNSItem = new SimpleDNSItem
            {
                BootstrapDNS = Global.DomainPureIPDNSAddress.FirstOrDefault(),
                ServeStale = false,
                ParallelQuery = false,
                Strategy4Freedom = Global.AsIs,
                Strategy4Proxy = Global.AsIs,
                Strategy4ProxyDial = Global.AsIs,
            },
            KcpItem = new KcpItem(),
            GrpcItem = new GrpcItem(),
            HysteriaItem = new HysteriaItem(),
            Mux4RayItem = new Mux4RayItem(),
            Mux4SboxItem = new Mux4SboxItem(),
            Inbound = [new InItem { Protocol = nameof(EInboundProtocol.socks), LocalPort = 10808 }],
            CoreTypeItem = [new CoreTypeItem { ConfigType = EConfigType.VMess, CoreType = ECoreType.Xray }],
        };
    }

    private static void BindConfig(Config config)
    {
        var field = typeof(AppManager).GetField(
            "_config", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(AppManager.Instance, config);
    }

    private static SplitTunnelAppItem App(
        string value,
        string action = "proxy",
        string entryType = "app",
        string port = "")
    {
        return new SplitTunnelAppItem
        {
            EntryType = entryType,
            Value = value,
            Port = port,
            Action = action,
        };
    }

    // ----- Tests -----

    [Fact]
    public async Task SaveRulesRoundTrip_ManuelMode_PerAppRulesPersistWithProcessName()
    {
        // --- Arrange ---
        await CleanRoutingItemsAsync();
        var config = CreateTestConfig();
        BindConfig(config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();

        var staleRouting = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "stale-routing — should never receive managed rules",
            RuleSet = "[]",
            RuleNum = 0,
            IsActive = false,
            Sort = 1,
        };

        var activeRouting = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "active-routing",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new()
                {
                    Id = Utils.GetGuid(false),
                    Domain = ["not-managed.example.com"],
                    OutboundTag = Global.ProxyTag,
                    Enabled = true,
                    Remarks = "user rule — should survive",
                },
            }, false),
            RuleNum = 1,
            IsActive = true,
            Sort = 0,
        };

        await SQLiteHelper.Instance.ReplaceAsync(staleRouting);
        await SQLiteHelper.Instance.ReplaceAsync(activeRouting);

        var apps = new[]
        {
            App("chrome.exe", action: "vpn"),
            App("msedge.exe", action: "direct"),
        };

        // --- Act: simulate SaveRulesAsync's core logic ---
        var routingFromDb = await ConfigHandler.GetDefaultRouting(config);
        routingFromDb.Should().NotBeNull();
        routingFromDb!.IsActive.Should().BeTrue();

        var existingRules = routingFromDb.RuleSet.IsNullOrEmpty()
            ? new List<RulesItem>()
            : JsonUtils.Deserialize<List<RulesItem>>(routingFromDb.RuleSet)
              ?? new List<RulesItem>();

        var preserved = existingRules
            .Where(r => !ManualRoutingRules.IsManagedRule(r))
            .ToList();
        preserved.Should().HaveCount(1, "the user rule is not managed");

        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: false);

        // Per-app rules + catch-all
        managed.Should().HaveCount(3);
        managed[0].Process.Should().ContainSingle("chrome.exe");
        managed[0].OutboundTag.Should().Be(Global.ProxyTag);
        managed[1].Process.Should().ContainSingle("msedge.exe");
        managed[1].OutboundTag.Should().Be(Global.DirectTag);

        var catchAll = managed[^1];
        catchAll.Port.Should().Be("0-65535");
        catchAll.OutboundTag.Should().Be(Global.DirectTag);

        // Persist the combined rule set.
        var final = managed.Concat(preserved).ToList();
        routingFromDb.RuleSet = JsonUtils.Serialize(final, false);
        routingFromDb.RuleNum = final.Count;

        var saveResult = await ConfigHandler.SaveRoutingItem(config, routingFromDb);
        saveResult.Should().Be(0, "SaveRoutingItem must succeed");

        // --- Assert: re-read and verify round-trip ---
        var reread = await ConfigHandler.GetDefaultRouting(config);
        reread.Should().NotBeNull();

        var rereadRules = JsonUtils.Deserialize<List<RulesItem>>(reread!.RuleSet) ?? [];
        rereadRules.Should().HaveCount(4);

        // Per-app rules survived the round-trip.
        var chromeRule = rereadRules.FirstOrDefault(
            r => r.Process != null && r.Process.Contains("chrome.exe"));
        chromeRule.Should().NotBeNull("chrome.exe VPN rule must survive");
        chromeRule!.Process.Should().ContainSingle("chrome.exe");
        chromeRule.OutboundTag.Should().Be(Global.ProxyTag);
        chromeRule.Remarks.Should().Be(
            ManualRoutingRules.ManagedRemarksPrefix + " chrome.exe");

        var edgeRule = rereadRules.FirstOrDefault(
            r => r.Process != null && r.Process.Contains("msedge.exe"));
        edgeRule.Should().NotBeNull("msedge.exe direct rule must survive");
        edgeRule!.OutboundTag.Should().Be(Global.DirectTag);

        // The preserved user rule must still be there.
        rereadRules.Should().Contain(r =>
            r.Domain != null && r.Domain.Contains("not-managed.example.com"));

        // The stale routing item must NOT have been touched.
        var rereadStale = await SQLiteHelper.Instance
            .TableAsync<RoutingItem>()
            .FirstOrDefaultAsync(it => it.Id == staleRouting.Id);
        rereadStale.Should().NotBeNull();
        rereadStale!.RuleSet.Should().Be("[]",
            "the stale (inactive) routing item must not receive managed rules");
    }

    [Fact]
    public async Task SaveRulesRoundTrip_InvertManualBlacklist_FlipsAppDirection()
    {
        await CleanRoutingItemsAsync();
        var config = CreateTestConfig();
        BindConfig(config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();

        var activeRouting = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "blacklist-routing",
            RuleSet = "[]",
            RuleNum = 0,
            IsActive = true,
        };

        await SQLiteHelper.Instance.ReplaceAsync(activeRouting);

        var apps = new[]
        {
            App("steam.exe", action: "vpn"),
            App("myapp.exe", action: "direct"),
        };

        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: true);

        managed.Should().HaveCount(3);

        var steamRule = managed[0];
        steamRule.Process.Should().ContainSingle("steam.exe");
        steamRule.OutboundTag.Should().Be(Global.DirectTag,
            "blacklist: VPN entry becomes direct exception");

        var myappRule = managed[1];
        myappRule.Process.Should().ContainSingle("myapp.exe");
        myappRule.OutboundTag.Should().Be(Global.ProxyTag,
            "blacklist: direct entry becomes VPN exception");

        var catchAll = managed[^1];
        catchAll.OutboundTag.Should().Be(Global.ProxyTag);

        // Persist and verify round-trip.
        var routingFromDb = await ConfigHandler.GetDefaultRouting(config);
        routingFromDb.Should().NotBeNull();
        routingFromDb!.RuleSet = JsonUtils.Serialize(managed, false);
        routingFromDb.RuleNum = managed.Count;
        await ConfigHandler.SaveRoutingItem(config, routingFromDb);

        var reread = await ConfigHandler.GetDefaultRouting(config);
        var rereadRules = JsonUtils.Deserialize<List<RulesItem>>(
            reread?.RuleSet ?? "[]") ?? [];
        rereadRules.Should().HaveCount(3);

        rereadRules[0].OutboundTag.Should().Be(Global.DirectTag);
        rereadRules[1].OutboundTag.Should().Be(Global.ProxyTag);
        rereadRules[2].OutboundTag.Should().Be(Global.ProxyTag);
    }

    [Fact]
    public async Task SaveRulesRoundTrip_VpnMode_OnlyCatchAllProxy_NoPerAppRules()
    {
        await CleanRoutingItemsAsync();
        var config = CreateTestConfig();
        BindConfig(config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();

        var activeRouting = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "vpn-routing",
            RuleSet = "[]",
            RuleNum = 0,
            IsActive = true,
        };

        await SQLiteHelper.Instance.ReplaceAsync(activeRouting);

        var apps = new[]
        {
            App("chrome.exe", action: "vpn"),
            App("discord.gg", entryType: "domain"),
        };

        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Vpn, apps);

        managed.Should().HaveCount(1, "Global VPN never emits per-app rules");
        managed[0].Port.Should().Be("0-65535");
        managed[0].OutboundTag.Should().Be(Global.ProxyTag);
        managed[0].Process.Should().BeNull();

        var routingFromDb = await ConfigHandler.GetDefaultRouting(config);
        routingFromDb.Should().NotBeNull();
        routingFromDb!.RuleSet = JsonUtils.Serialize(managed, false);
        routingFromDb.RuleNum = 1;
        await ConfigHandler.SaveRoutingItem(config, routingFromDb);

        var reread = await ConfigHandler.GetDefaultRouting(config);
        var rereadRules = JsonUtils.Deserialize<List<RulesItem>>(
            reread?.RuleSet ?? "[]") ?? [];
        rereadRules.Should().HaveCount(1);
        rereadRules[0].Process.Should().BeNull();
        rereadRules[0].OutboundTag.Should().Be(Global.ProxyTag);
    }

    [Fact]
    public async Task SaveRulesRoundTrip_OffMode_CatchAllDirect_EmptiesManaged()
    {
        await CleanRoutingItemsAsync();
        var config = CreateTestConfig();
        BindConfig(config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();

        var activeRouting = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "off-routing",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new()
                {
                    Id = Utils.GetGuid(false),
                    Remarks = ManualRoutingRules.ManagedRemarksPrefix + " chrome.exe",
                    Process = ["chrome.exe"],
                    OutboundTag = Global.ProxyTag,
                    Enabled = true,
                },
                new()
                {
                    Id = Utils.GetGuid(false),
                    Remarks = ManualRoutingRules.ManagedRemarksPrefix + " varsayılan",
                    Port = "0-65535",
                    OutboundTag = Global.ProxyTag,
                    Enabled = true,
                },
            }, false),
            RuleNum = 2,
            IsActive = true,
        };

        await SQLiteHelper.Instance.ReplaceAsync(activeRouting);

        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Off, []);

        managed.Should().HaveCount(1);
        managed[0].OutboundTag.Should().Be(Global.DirectTag);

        // The old managed rules are discarded by the IsManagedRule check.
        var routingFromDb = await ConfigHandler.GetDefaultRouting(config);
        routingFromDb.Should().NotBeNull();

        var existingRules = routingFromDb!.RuleSet.IsNullOrEmpty()
            ? []
            : JsonUtils.Deserialize<List<RulesItem>>(routingFromDb.RuleSet) ?? [];

        var preserved = existingRules
            .Where(r => !ManualRoutingRules.IsManagedRule(r))
            .ToList();
        preserved.Should().BeEmpty("both old rules are managed and must be dropped");

        var final = managed.Concat(preserved).ToList();
        routingFromDb.RuleSet = JsonUtils.Serialize(final, false);
        routingFromDb.RuleNum = final.Count;
        await ConfigHandler.SaveRoutingItem(config, routingFromDb);

        var reread = await ConfigHandler.GetDefaultRouting(config);
        var rereadRules = JsonUtils.Deserialize<List<RulesItem>>(
            reread?.RuleSet ?? "[]") ?? [];
        rereadRules.Should().HaveCount(1, "only the catch-all direct rule should remain");
        rereadRules[0].OutboundTag.Should().Be(Global.DirectTag);
    }

    [Fact]
    public async Task GetDefaultRouting_AfterInsert_ReturnsTheActiveItem()
    {
        await CleanRoutingItemsAsync();
        var config = CreateTestConfig();
        BindConfig(config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();

        var inactive = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "inactive",
            RuleSet = "[]",
            IsActive = false,
        };
        var active = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "active",
            RuleSet = "[]",
            IsActive = true,
        };

        await SQLiteHelper.Instance.ReplaceAsync(inactive);
        await SQLiteHelper.Instance.ReplaceAsync(active);

        var result = await ConfigHandler.GetDefaultRouting(config);

        result.Should().NotBeNull();
        result!.Id.Should().Be(active.Id,
            "GetDefaultRouting must return the item with IsActive=true");
        result.Remarks.Should().Be("active");
    }
}