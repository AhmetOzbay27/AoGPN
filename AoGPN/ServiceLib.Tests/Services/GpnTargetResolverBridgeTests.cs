using AwesomeAssertions;
using ServiceLib.Models.Dto;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnTargetResolverBridge — SplitTunnelViewModel'den (vpn eylemli oyunlar)
/// GpnTargetResolver PID havuzunu canlı çalıştıran köprünün testleri.
/// VM kurucusu yerine iç test kurucusu (apps sağlayıcısı) kullanılır.
/// </summary>
public class GpnTargetResolverBridgeTests
{
    private static SplitTunnelAppItem App(string name, string action, string? processName = null)
        => new()
        {
            EntryType = "app",
            Action = action,
            Value = name,
            ProcessName = processName ?? name,
        };

    private static GpnTargetResolverBridge CreateBridge(
        IList<SplitTunnelAppItem> apps,
        IProcessTreeSource? source = null,
        Func<bool>? invertManual = null)
    {
        // IList kopyası: her çağrıda güncel listeyi döndürür (testler listeyi değiştirebilir).
        return new GpnTargetResolverBridge(() => apps.ToArray(), source, invertManual);
    }

    [Fact]
    public void RefreshNow_VpnAppsOnly_ResolvesPoolIncludingChildren()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            App("EscapeFromTarkov.exe", "vpn"),
            App("lol.exe", "vpn"),
            App("notepad.exe", "direct"), // vpn değil → havuz dışı
        };
        var source = new FakeProcessTreeSource(
            new ProcessInfo(100, 0, "EscapeFromTarkov.exe"),
            new ProcessInfo(200, 100, "EscapeFromTarkov.exe"), // alt süreç
            new ProcessInfo(300, 0, "lol.exe"),
            new ProcessInfo(400, 0, "notepad.exe"));
        var bridge = CreateBridge(apps, source);

        var snap = bridge.RefreshNow();

        snap.TargetNames.Should().Equal(["EscapeFromTarkov.exe", "lol.exe"]);
        snap.Pids.Should().Contain(new uint[] { 100, 200, 300 });
        snap.Pids.Should().NotContain(400);
        snap.TargetRunning.Should().BeTrue();
        snap.Watching.Should().BeTrue();
    }

    [Fact]
    public void RefreshNow_WarpApps_IncludedInWhitelistPool()
    {
        // WARP egress de tünelden geçer — beyaz listede warp eylemli uygulamalar
        // (örn. Tarkov launcher) vpn gibi yakalama havuzuna girmeli.
        var apps = new List<SplitTunnelAppItem>
        {
            App("BsGLauncher.exe", "warp"),
            App("EscapeFromTarkov.exe", "vpn"),
            App("notepad.exe", "direct"),
        };
        var source = new FakeProcessTreeSource(
            new ProcessInfo(100, 0, "BsGLauncher.exe"),
            new ProcessInfo(200, 0, "EscapeFromTarkov.exe"),
            new ProcessInfo(300, 0, "notepad.exe"));
        var bridge = CreateBridge(apps, source);

        var snap = bridge.RefreshNow();

        snap.TargetNames.Should().Equal(["BsGLauncher.exe", "EscapeFromTarkov.exe"]);
        snap.Pids.Should().Contain(new uint[] { 100, 200 });
        snap.Pids.Should().NotContain(300);
    }

    [Fact]
    public void RefreshNow_WarpApp_BlacklistDirection_NotCaptured()
    {
        // Kara listede warp ataması istisnadır (kurallarda direct olur) — yakalanmaz.
        var apps = new List<SplitTunnelAppItem>
        {
            App("BsGLauncher.exe", "warp"),
        };
        var source = new FakeProcessTreeSource(new ProcessInfo(100, 0, "BsGLauncher.exe"));
        var bridge = CreateBridge(apps, source, () => true);

        var snap = bridge.RefreshNow();

        snap.TargetNames.Should().BeEmpty();
        snap.Pids.Should().BeEmpty();
    }

    [Fact]
    public void RefreshNow_NoVpnApps_EmptyPool()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            App("discord.exe", "direct"),
            App("notepad.exe", "block"),
        };
        var bridge = CreateBridge(apps, new FakeProcessTreeSource());

        var snap = bridge.RefreshNow();

        snap.TargetNames.Should().BeEmpty();
        snap.Pids.Should().BeEmpty();
        snap.TargetRunning.Should().BeFalse();
    }

    [Fact]
    public void RefreshNow_BlacklistDirection_TargetsDirectEntriesNotVpnExceptions()
    {
        // Kara liste (dışlama) yönü: "vpn" eylemli girişler istisnadır ve kurallarda
        // direct'e çevrilir — yakalama köprüsü onları TÜNELLEMEMELİDİR. "direct"
        // eylemli girişler ise kurallarda tünele çevrilir ve hedeflenir.
        var apps = new List<SplitTunnelAppItem>
        {
            App("EscapeFromTarkov.exe", "vpn"),   // dışlanan oyun → yakalanmamalı
            App("lol.exe", "vpn"),                // dışlanan oyun → yakalanmamalı
            App("cs2.exe", "direct"),             // kara listede tünellenir → hedef
            App("notepad.exe", "block"),          // engellenir → hedef dışı
        };
        var source = new FakeProcessTreeSource(
            new ProcessInfo(100, 0, "EscapeFromTarkov.exe"),
            new ProcessInfo(200, 0, "lol.exe"),
            new ProcessInfo(300, 0, "cs2.exe"),
            new ProcessInfo(400, 0, "notepad.exe"));
        var bridge = CreateBridge(apps, source, () => true);

        var snap = bridge.RefreshNow();

        snap.TargetNames.Should().ContainSingle("cs2.exe", "kara listede yalnızca tünellenen girişler hedeflenir");
        snap.Pids.Should().Equal(300u);
        snap.Pids.Should().NotContain(100u);
        snap.Pids.Should().NotContain(200u);
        snap.TargetRunning.Should().BeTrue();
    }

    [Fact]
    public void RefreshNow_BlacklistDirection_OnlyExcludedEntries_EmptyPool()
    {
        // Yalnızca dışlanan (vpn) girişler varsa yakalama köprüsü hiç başlamamalı;
        // tünel kararı tamamen sing-box TUN kurallarına kalır.
        var apps = new List<SplitTunnelAppItem>
        {
            App("EscapeFromTarkov.exe", "vpn"),
            App("lol.exe", "vpn"),
        };
        var source = new FakeProcessTreeSource(
            new ProcessInfo(100, 0, "EscapeFromTarkov.exe"),
            new ProcessInfo(200, 0, "lol.exe"));
        var bridge = CreateBridge(apps, source, () => true);

        var snap = bridge.RefreshNow();

        snap.TargetNames.Should().BeEmpty();
        snap.Pids.Should().BeEmpty();
        snap.TargetRunning.Should().BeFalse();
    }

    [Fact]
    public void RefreshNow_DirectionSwitch_RecomputesTargetSetLive()
    {
        // Yön her turda canlı okunur: beyaz listede "vpn" hedeflenir, kara listeye
        // geçilince aynı liste tünel dışı kalır ve hedef kümesi boşalır.
        var apps = new List<SplitTunnelAppItem> { App("EscapeFromTarkov.exe", "vpn") };
        var source = new FakeProcessTreeSource(new ProcessInfo(100, 0, "EscapeFromTarkov.exe"));
        var invert = false;
        var bridge = CreateBridge(apps, source, () => invert);

        var whitelist = bridge.RefreshNow();
        whitelist.TargetNames.Should().ContainSingle("EscapeFromTarkov.exe");
        whitelist.Pids.Should().Equal(100u);

        invert = true; // kullanıcı kara listeye geçti
        var blacklist = bridge.RefreshNow();
        blacklist.TargetNames.Should().BeEmpty("dışlanan (vpn) oyun artık yakalanmaz");
        blacklist.Pids.Should().BeEmpty();
    }

    [Fact]
    public void RefreshNow_TargetNamesChange_RebuildsResolver()
    {
        var apps = new List<SplitTunnelAppItem>
        {
            App("EscapeFromTarkov.exe", "vpn"),
        };
        var source = new FakeProcessTreeSource(
            new ProcessInfo(100, 0, "EscapeFromTarkov.exe"),
            new ProcessInfo(300, 0, "lol.exe"));
        var bridge = CreateBridge(apps, source);

        var first = bridge.RefreshNow();
        first.TargetNames.Should().Equal("EscapeFromTarkov.exe");
        first.Pids.Should().Equal(100u);

        // Kullanıcı oyunu değiştirdi → köprü yeni resolver'ı kurar.
        apps.Clear();
        apps.Add(App("lol.exe", "vpn"));

        var second = bridge.RefreshNow();
        second.TargetNames.Should().ContainSingle("lol.exe");
        second.Pids.Should().Equal(300u);
        second.Version.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task StartStop_FlipsWatching_AndPublishesStoppedSnapshot()
    {
        var apps = new List<SplitTunnelAppItem> { App("EscapeFromTarkov.exe", "vpn") };
        var source = new FakeProcessTreeSource(new ProcessInfo(100, 0, "EscapeFromTarkov.exe"));
        using var bridge = CreateBridge(apps, source);

        var events = new List<GpnPidPoolSnapshot>();
        bridge.SnapshotChanged += events.Add;

        bridge.RefreshNow(); // ilk ölçüm (event 1)
        events.Clear();

        bridge.Start();
        bridge.IsWatching.Should().BeTrue();

        bridge.Stop();
        bridge.IsWatching.Should().BeFalse();
        bridge.LastSnapshot.Should().NotBeNull();
        bridge.LastSnapshot!.Watching.Should().BeFalse();
        bridge.LastSnapshot.Pids.Should().Equal(new uint[] { 100u }, "son PID kümesi durdurulunca korunur");
        events.Any(e => !e.Watching).Should().BeTrue("Stop durumu yayınlanır");

        await Task.Delay(30, TestContext.Current.CancellationToken); // iptal edilen döngünün durumu bozmadığını doğrula
        bridge.LastSnapshot.Watching.Should().BeFalse("Stop son sözü söyler");
    }

    [Fact]
    public void SnapshotEvent_FiresOnlyOnChange()
    {
        var apps = new List<SplitTunnelAppItem> { App("EscapeFromTarkov.exe", "vpn") };
        var source = new FakeProcessTreeSource(new ProcessInfo(100, 0, "EscapeFromTarkov.exe"));
        var bridge = CreateBridge(apps, source);

        var fired = 0;
        bridge.SnapshotChanged += _ => fired++;

        bridge.RefreshNow();
        bridge.RefreshNow(); // aynı PID kümesi → yayın yok

        fired.Should().Be(1, "yalnızca değişen anlık görüntüler yayınlanır");
    }

    // ── Test donanımı ────────────────────────────────────────────────────

    private sealed class FakeProcessTreeSource : IProcessTreeSource
    {
        private readonly ProcessInfo[] _processes;

        public FakeProcessTreeSource(params ProcessInfo[] processes)
        {
            _processes = processes;
        }

        public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
            => _processes.ToArray();
    }
}