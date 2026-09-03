using AwesomeAssertions;
using ServiceLib.Models.Dto;
using ServiceLib.Services.CoreConfig.Mihomo;
using ServiceLib.Services.Gpn;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnSoftPolicyApplier — kesintisiz rota uygulamasının karar + PUT mantığı.
/// Ağ erişimi delegelerle sahte mihomo durumu üzerinden değiştirilir (gerçek
/// çekirdeğe dokunulmaz): seçim delta'sı, kapı kontrolleri ve doğrulama sınanır.
/// </summary>
public class GpnSoftPolicyApplierTests
{
    // GPN-Nodes üyeleri — wg-düğüm seçimi bu testin konusu değil, sabit tutulur.
    private const string WgDe = "wg-de";
    private const string WgIt = "wg-it";

    private readonly Dictionary<string, ClashProxies.ProxiesItem> _state = new();
    private readonly List<(string Group, string Target)> _puts = new();

    // Ortak giriş listesi (parmak izi = çalışan config'in üretim listesi).
    private static readonly IReadOnlyList<GpnSoftRoutingEntry> Entries = new[]
    {
        new GpnSoftRoutingEntry("app", "chrome.exe", "", "vpn"),
        new GpnSoftRoutingEntry("app", "edge.exe", "", "direct"),
    };

    private static IReadOnlyList<string> Fingerprint()
        => Entries.Select(GpnSoftRouting.EntryKey).ToList();

    private static ClashProxies.ProxiesItem Sel(string now, params string[] members) => new()
    {
        type = "Selector",
        now = now,
        all = members.ToList(),
    };

    private void SeedSupersetState(
        string modeNow = GpnSoftRouting.ClashDirect,
        string ao0Now = GpnMihomoConfigService.NodesGroupName,
        string ao1Now = GpnSoftRouting.ClashDirect)
    {
        var all = new[]
        {
            GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashDirect,
            GpnSoftRouting.ClashReject,
            GpnMihomoConfigService.WarpProxyName,
        };
        _state.Clear();
        _puts.Clear();
        _state[GpnMihomoConfigService.NodesGroupName] = Sel(WgDe, WgDe, WgIt);
        _state[GpnSoftRouting.ModeGroupName] = Sel(modeNow, GpnSoftRouting.ClashDirect, GpnMihomoConfigService.NodesGroupName);
        _state[GpnSoftRouting.AppGroupName(0)] = Sel(ao0Now, all);
        _state[GpnSoftRouting.AppGroupName(1)] = Sel(ao1Now, all);
    }

    private GpnSoftRoutingPolicy Policy(int mode, bool invert, params (int Index, string Action)[] overrides)
    {
        var entries = Entries.Select(e => new GpnSoftRoutingEntry(e.EntryType, e.Value, e.Port, e.Action)).ToList();
        foreach (var (index, action) in overrides)
        {
            entries[index] = entries[index] with { Action = action };
        }
        return new GpnSoftRoutingPolicy(mode, invert, entries);
    }

    private Func<Task<ClashProxies?>> Fetch() => () => Task.FromResult<ClashProxies?>(new ClashProxies { proxies = _state });

    private Func<string, string, Task> SetSelection()
        => (group, target) =>
        {
            _puts.Add((group, target));
            if (_state.TryGetValue(group, out var item))
            {
                item.now = target; // gerçek mihomo gibi seçimi uygula
            }
            return Task.CompletedTask;
        };

    private Task<bool> Apply(GpnSoftRoutingPolicy desired, IReadOnlyList<string>? fingerprint = null)
        => GpnSoftPolicyApplier.TryApplyCoreAsync(desired, fingerprint ?? Fingerprint(), Fetch(), SetSelection());

    private Task<bool> ApplyWithoutSession(GpnSoftRoutingPolicy desired)
        => GpnSoftPolicyApplier.TryApplyCoreAsync(desired, fingerprint: null, Fetch(), SetSelection());

    [Fact]
    public async Task ModeToGlobal_SwitchesModeGroupAndDirectEntry_OnlyChangedGroups()
    {
        SeedSupersetState(modeNow: GpnSoftRouting.ClashDirect, ao1Now: GpnSoftRouting.ClashDirect);
        var desired = Policy(GameTriggerModes.Vpn, invert: false);

        var applied = await Apply(desired);

        applied.Should().BeTrue();
        // ao-0 (chrome vpn) zaten GPN-Nodes'da — delta'ya girmez; GPN-MODE ve ao-1 değişir.
        _puts.Should().Equal(
            (GpnSoftRouting.ModeGroupName, GpnMihomoConfigService.NodesGroupName),
            (GpnSoftRouting.AppGroupName(1), GpnMihomoConfigService.NodesGroupName));
        _state[GpnSoftRouting.ModeGroupName].now.Should().Be(GpnMihomoConfigService.NodesGroupName);
    }

    [Fact]
    public async Task SingleRouteChange_PutsOnlyThatEntryGroup()
    {
        SeedSupersetState();
        var desired = Policy(GameTriggerModes.Manual, invert: false, (1, "block"));

        var applied = await Apply(desired);

        applied.Should().BeTrue();
        _puts.Should().Equal((GpnSoftRouting.AppGroupName(1), GpnSoftRouting.ClashReject));
    }

    [Fact]
    public async Task Off_PutsModeAndEveryEntryToDirect()
    {
        // Canlı durum kara liste/Global vektöründe (her şey tünelde) — Off hepsini
        // direct'e çeker: mod grubu + her giriş grubu değişir.
        SeedSupersetState(
            modeNow: GpnMihomoConfigService.NodesGroupName,
            ao0Now: GpnMihomoConfigService.NodesGroupName,
            ao1Now: GpnMihomoConfigService.NodesGroupName);
        var desired = Policy(GameTriggerModes.Off, invert: false);

        var applied = await Apply(desired);

        applied.Should().BeTrue();
        _puts.Should().Contain((GpnSoftRouting.ModeGroupName, GpnSoftRouting.ClashDirect));
        _puts.Should().Contain((GpnSoftRouting.AppGroupName(0), GpnSoftRouting.ClashDirect));
        _puts.Should().Contain((GpnSoftRouting.AppGroupName(1), GpnSoftRouting.ClashDirect));
        _puts.Should().HaveCount(3);
    }

    [Fact]
    public async Task AlreadyDesired_NoOp_ReturnsTrueWithoutPuts()
    {
        // Varsayılan canlı durum = Manual beyaz liste vektörü (mode DIRECT, ao-0 tünel,
        // ao-1 DIRECT) — istenen politikayla aynı → reload'a gerek yok, PUT yok.
        SeedSupersetState(modeNow: GpnSoftRouting.ClashDirect, ao0Now: GpnMihomoConfigService.NodesGroupName, ao1Now: GpnSoftRouting.ClashDirect);
        var desired = Policy(GameTriggerModes.Manual, invert: false);

        var applied = await Apply(desired);

        applied.Should().BeTrue("seçimler zaten istenen durumda");
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task NoSupersetSession_ReturnsFalse()
    {
        SeedSupersetState();

        var applied = await ApplyWithoutSession(Policy(GameTriggerModes.Vpn, invert: false));

        applied.Should().BeFalse("oturum parmak izi yok — superset config çalışmıyor");
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task EntryListChanged_StructuralChange_ReturnsFalse()
    {
        SeedSupersetState();
        // Çalışan config 2 girişle üretildi (parmak izi 2); istenen listede 3. giriş var.
        var extra = new GpnSoftRoutingPolicy(
            GameTriggerModes.Vpn,
            false,
            Entries.Append(new GpnSoftRoutingEntry("app", "firefox.exe", "", "vpn")).ToList());

        var applied = await Apply(extra);

        applied.Should().BeFalse("eklenen giriş kural seti değiştirir — restart'lı fallback gerekir");
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task EntryReordered_StructuralChange_ReturnsFalse()
    {
        SeedSupersetState();
        // Aynı girişler farklı sırada → satır/grup eşleşmesi bozulur (kural sırası).
        var reordered = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            false,
            new[] { Entries[1], Entries[0] });

        var applied = await Apply(reordered, fingerprint: Fingerprint());

        applied.Should().BeFalse();
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task RunningConfigNotSuperset_ReturnsFalse()
    {
        // Canlı config'te GPN-MODE grubu yok (eski tek-düğüm config) → kapı reddeder.
        _state.Clear();
        _puts.Clear();
        _state[GpnMihomoConfigService.NodesGroupName] = Sel(WgDe, WgDe);
        _state["wg-de"] = Sel(WgDe);

        var applied = await Apply(Policy(GameTriggerModes.Vpn, invert: false));

        applied.Should().BeFalse();
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task TargetNotMemberOfGroup_ReturnsFalse()
    {
        // ao-0 üye listesi REJECT içermiyor (eski/sürüm farkı config) — hedef geçersiz.
        SeedSupersetState();
        _state[GpnSoftRouting.AppGroupName(0)].all = new List<string>
        {
            GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashDirect,
            GpnMihomoConfigService.WarpProxyName,
        };
        var desired = Policy(GameTriggerModes.Manual, invert: false, (0, "block"));

        var applied = await Apply(desired);

        applied.Should().BeFalse("REJECT hedefi grubun üyesi değil — PUT tehlikeli olur");
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task DualSession_WarpEntry_PutsVlessLauncherWithoutRestart()
    {
        // Çift Bağlantı oturumu: canlı ao grupları warp-socks yerine vless-launcher
        // üyesi taşır; istenen politika da küresel bypass ayarından vless-launcher'ı
        // hedefler → BsGLauncher gibi warp girişleri restart'sız tek PUT ile uygulanır.
        var dualMembers = new[]
        {
            GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashDirect,
            GpnSoftRouting.ClashReject,
            GpnMihomoConfigService.BypassProxyName,
        };
        SeedSupersetState();
        _state[GpnSoftRouting.AppGroupName(0)] = Sel(GpnMihomoConfigService.NodesGroupName, dualMembers);
        _state[GpnSoftRouting.AppGroupName(1)] = Sel(GpnSoftRouting.ClashDirect, dualMembers);
        var desired = Policy(GameTriggerModes.Manual, invert: false, (0, "warp"))
            with { WarpEgressProxy = GpnMihomoConfigService.BypassProxyName };

        var applied = await Apply(desired);

        applied.Should().BeTrue("çift bağlantı oturumunda warp hedefi üye listesindedir");
        _puts.Should().Equal((GpnSoftRouting.AppGroupName(0), GpnMihomoConfigService.BypassProxyName));
    }

    [Fact]
    public async Task LegacyWarpDesired_AgainstDualSession_ReturnsFalseForRestartFallback()
    {
        // Uyumsuzluk kapısı: istenen politika legacy (warp → warp-socks) ama canlı
        // oturum çift bağlantı (üyelerde vless-launcher var, warp-socks yok) → hedef
        // üye değil → false; çağıran restart'lı fallback ile config'i yeniden üretir
        // (asla yanlış gruba PUT atılmaz).
        var dualMembers = new[]
        {
            GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashDirect,
            GpnSoftRouting.ClashReject,
            GpnMihomoConfigService.BypassProxyName,
        };
        SeedSupersetState();
        _state[GpnSoftRouting.AppGroupName(0)] = Sel(GpnMihomoConfigService.NodesGroupName, dualMembers);
        var desired = Policy(GameTriggerModes.Manual, invert: false, (0, "warp"));

        var applied = await Apply(desired);

        applied.Should().BeFalse("warp-socks canlı üye listesinde değil — restart'lı fallback");
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task PutVerifyFails_ReturnsFalse_ForLegacyFallback()
    {
        SeedSupersetState();
        // Sahte mihomo seçimi uygulamıyor (PUT sessizce kayboluyor) → doğrulama tutmaz.
        var desired = Policy(GameTriggerModes.Vpn, invert: false);
        Func<string, string, Task> brokenSet = (group, target) =>
        {
            _puts.Add((group, target));
            return Task.CompletedTask; // state güncellenmez
        };

        var applied = await GpnSoftPolicyApplier.TryApplyCoreAsync(desired, Fingerprint(), Fetch(), brokenSet);

        applied.Should().BeFalse("seçimler gerçekleşmedi — restart'lı fallback config'i yeniden üretir");
        _puts.Should().NotBeEmpty();
    }
}
