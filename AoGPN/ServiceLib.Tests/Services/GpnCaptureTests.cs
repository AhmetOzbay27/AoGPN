using System.Runtime.InteropServices;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ServiceLib.DI;
using ServiceLib.Models.Configs;
using ServiceLib.Services;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnTargetResolver (PID havuzu + 5 sn tazeleme) ve GpnCaptureLoop (filtre
/// derleme + yakalama/enjeksiyon döngüsü) testleri. Süreç kaynağı sahte,
/// WinDivert natif katmanı sahte API ile değiştirilir — gerçek sürücü gerekmez.
/// Entegrasyon testi AppManager.Instance._config'i yansıma ile değiştirdiği için
/// bu sınıf AppManager config'ini değiştiren diğer sınıflarla aynı SIRALI
/// koleksiyonda koşar (xUnit paralel koleksiyonlarında rebind yarışı olmaz).
/// </summary>
[Collection("GpnSharedAppConfigSerial")]
public class GpnCaptureTests
{
    // ── GpnTargetResolver ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_IncludesChildAndGrandchild()
    {
        var source = new FakeProcessTreeSource(
            new ProcessInfo(100, 0, "EscapeFromTarkov.exe"),
            new ProcessInfo(200, 100, "EscapeFromTarkov.exe"),
            new ProcessInfo(300, 200, "BeService.exe"),
            new ProcessInfo(400, 999, "notepad.exe"));

        var resolver = new GpnTargetResolver(["EscapeFromTarkov.exe"], source);

        var snap = resolver.Resolve(TestContext.Current.CancellationToken);
        snap.Should().NotBeNull();
        snap!.Pids.Should().BeEquivalentTo(new[] { 100u, 200u, 300u });
    }

    [Fact]
    public void Resolve_MultipleTargetNames_Unions()
    {
        var source = new FakeProcessTreeSource(
            new ProcessInfo(100, 0, "lol.exe"),
            new ProcessInfo(200, 100, "lol.exe"),
            new ProcessInfo(300, 0, "discord.exe"));

        var resolver = new GpnTargetResolver(new[] { "lol.exe", "discord.exe" }, source);

        resolver.Resolve(TestContext.Current.CancellationToken)!.Pids.Should().BeEquivalentTo(new[] { 100u, 200u, 300u });
    }

    [Fact]
    public void Resolve_TargetNotRunning_ReturnsNull()
    {
        var source = new FakeProcessTreeSource(new ProcessInfo(100, 0, "notepad.exe"));
        var resolver = new GpnTargetResolver(["EscapeFromTarkov.exe"], source);

        resolver.Resolve(TestContext.Current.CancellationToken).Should().BeNull();
    }

    [Fact]
    public void HasSamePids_FalseWhenChanged_TrueWhenEqual()
    {
        var a = new TargetPidSnapshot(new[] { 1u, 2u }, 1, DateTimeOffset.UtcNow);
        var b = new TargetPidSnapshot(new[] { 1u, 2u }, 2, DateTimeOffset.UtcNow);
        var c = new TargetPidSnapshot(new[] { 1u, 2u, 3u }, 3, DateTimeOffset.UtcNow);

        a.HasSamePids(b).Should().BeTrue(); // sıra fark etmeksizin aynı küme (sıralı array)
        a.HasSamePids(c).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshLoop_YieldsOnlyOnPidChange_AndIncludesNewChild()
    {
        var gate = new object();
        var procs = new List<ProcessInfo> { new(100, 0, "Game.exe"), new(200, 100, "Game.exe") };
        var source = new FakeProcessTreeSource(procs, gate);
        var resolver = new GpnTargetResolver(["Game.exe"], source);

        var seen = new List<TargetPidSnapshot>();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            lock (gate)
            {
                procs.Add(new ProcessInfo(300, 200, "GameChild.exe"));
            }
        }, TestContext.Current.CancellationToken);

        await foreach (var snap in resolver.RefreshLoopAsync(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken))
        {
            seen.Add(snap);
            if (seen.Count >= 2)
            {
                break; // ikinci değişim alındı — döngüyü sonlandır
            }
        }

        seen.Count.Should().BeGreaterThanOrEqualTo(2);
        seen[0].Pids.Should().BeEquivalentTo(new[] { 100u, 200u });
        seen[^1].Pids.Should().Contain(300u); // alt süreç doğunca yeni set
    }

    // ── GpnCaptureLoop — enjeksiyon pompası ───────────────────────────────

    [Fact]
    public async Task CaptureLoop_RecvOnly_PumpsPackets_ThroughInjectHandler()
    {
        // SniffFirst=false → doğrudan recv-only: paketler yığından çıkarılır ve
        // enjeksiyon hattına pompalanır (tünel egress'i).
        var packets = new[] { new byte[] { 1, 2, 3 }, new byte[] { 9, 9, 9, 9 } };
        var api = new FakeDivertApi(recvPackets: packets);
        using var engine = new WinDivertEngine(api);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var injected = new List<byte[]>();
        var loop = new GpnCaptureLoop(
            new StubResolver(initial: Snap(42u), refresh: null),
            engine,
            inject: (packet, ct) =>
            {
                // Tier 4: havuzlanmış tampon işlendikten sonra havuza DÖNER ve
                // yeniden kiralanabilir — tüketici yalnızca mantıksal uzunluğu
                // (Length) kopyalamalı, ham referansı saklamamalı.
                injected.Add(packet.Data.AsSpan(0, packet.Length).ToArray());
                if (injected.Count >= 2)
                {
                    cts.Cancel();
                }
                return ValueTask.CompletedTask;
            },
            options: new GpnCaptureOptions { SniffFirst = false });

        await loop.RunAsync(cts.Token);

        injected.Should().HaveCount(2);
        injected[0].Should().Equal(packets[0]);
        injected[1].Should().Equal(packets[1]);
        api.OpenFlags.Should().OnlyContain(f => (f & WinDivertNative.FlagRecvOnly) != 0);
        engine.IsOpen.Should().BeFalse(); // kapanış temiz
    }

    // ── GpnCaptureLoop — PID değişince filtre yeniden derlenir ────────────

    [Fact]
    public async Task CaptureLoop_PidsChange_RecompilesFilter()
    {
        var api = new FakeDivertApi();
        using var engine = new WinDivertEngine(api);

        var resolver = new StubResolver(initial: Snap(100u), refresh: Snap(100u, 200u));
        // SniffFirst=false → tek mod (recv-only): açılış sayısı deterministik.
        var loop = new GpnCaptureLoop(resolver, engine, options: new GpnCaptureOptions { SniffFirst = false });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await loop.RunAsync(cts.Token);

        // İlk açılış (PID 100) + tazeleme sonrası yeniden derleme (PID 100 ve 200).
        api.OpenFilters.Should().HaveCount(2);
        api.OpenFilters[0].Should().Contain("processId == 100");
        api.OpenFilters[1].Should().Contain("processId == 200");
        api.OpenFilters[1].Should().NotBe(api.OpenFilters[0]);
        api.OpenFlags.Should().OnlyContain(f => (f & WinDivertNative.FlagRecvOnly) != 0);
        engine.IsOpen.Should().BeFalse();
    }

    // ── GpnCaptureLoop — varsayılan sniff-önce akışı ─────────────────────

    [Fact]
    public async Task CaptureLoop_SniffFirst_ObservesThenSwitchesToRecvOnly()
    {
        // Sınırsız paket üreten sahte sürücü: sniff aşaması VerifyPacketCount paketi
        // gözlemler (yakala-bırak), sonra OpenEx recv-only'ye geçilir ve paketler
        // enjeksiyon hattına pompalanır.
        var api = new FakeDivertApi(infinitePackets: true);
        using var engine = new WinDivertEngine(api);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var consumed = new List<byte[]>();
        var loop = new GpnCaptureLoop(
            new StubResolver(initial: Snap(42u), refresh: null),
            engine,
            inject: (packet, ct) =>
            {
                // Tier 4: havuzlanmış tamponun mantıksal uzunluğu kopyalanır (Length).
                consumed.Add(packet.Data.AsSpan(0, packet.Length).ToArray());
                if (consumed.Count >= 2)
                {
                    cts.Cancel();
                }
                return ValueTask.CompletedTask;
            },
            options: new GpnCaptureOptions
            {
                SniffFirst = true,
                VerifyPacketCount = 3,
                SniffTimeout = TimeSpan.FromSeconds(30),
            });

        await loop.RunAsync(cts.Token);

        api.OpenFilters.Should().HaveCount(2, "sniff + recv-only açılışı");
        (api.OpenFlags[0] & WinDivertNative.FlagSniff).Should().NotBe(0ul);
        (api.OpenFlags[1] & WinDivertNative.FlagRecvOnly).Should().NotBe(0ul);
        loop.SniffedPacketCount.Should().BeGreaterThanOrEqualTo(3, "sniff doğrulama eşiği");
        consumed.Count.Should().BeGreaterThanOrEqualTo(2, "recv-only paketleri inject'e pompalanır");
        engine.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task CaptureLoop_SniffMode_DoesNotInject()
    {
        // Sniff aşamasında paketler yalnızca gözlemlenir — enjeksiyon hattına asla
        // verilmez (sniff'e geri gönderim paket çoğaltır).
        var packets = new[] { new byte[] { 1 }, new byte[] { 2 } };
        var api = new FakeDivertApi(recvPackets: packets);
        using var engine = new WinDivertEngine(api);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var injected = new List<byte[]>();
        var loop = new GpnCaptureLoop(
            new StubResolver(initial: Snap(42u), refresh: null),
            engine,
            inject: (packet, ct) =>
            {
                injected.Add(packet.Data.AsSpan(0, packet.Length).ToArray());
                return ValueTask.CompletedTask;
            },
            options: new GpnCaptureOptions
            {
                SniffFirst = true,
                VerifyPacketCount = 2,
                SniffTimeout = TimeSpan.FromSeconds(30),
            });

        await loop.RunAsync(cts.Token);

        (api.OpenFlags[0] & WinDivertNative.FlagSniff).Should().NotBe(0ul);
        loop.SniffedPacketCount.Should().Be(2, "sniff eşiğindeki paketler gözlemlendi");
        injected.Should().BeEmpty("sniff aşamasında enjeksiyon yapılmaz");
        engine.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task CaptureLoop_SniffTimeout_StillSwitchesToRecvOnly()
    {
        // Paket eşiğine ulaşılamasa bile SniffTimeout dolunca recv-only'ye geçilir
        // (paket gelmeyen sunucu/filtre döngüyü kilitlemez).
        var api = new FakeDivertApi(recvPackets: new[] { new byte[] { 7 } });
        using var engine = new WinDivertEngine(api);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var loop = new GpnCaptureLoop(
            new StubResolver(initial: Snap(42u), refresh: null),
            engine,
            options: new GpnCaptureOptions
            {
                SniffFirst = true,
                VerifyPacketCount = 10, // asla ulaşılmaz
                SniffTimeout = TimeSpan.FromMilliseconds(100),
            });

        await loop.RunAsync(cts.Token);

        api.OpenFilters.Should().HaveCount(2);
        (api.OpenFlags[0] & WinDivertNative.FlagSniff).Should().NotBe(0ul);
        (api.OpenFlags[1] & WinDivertNative.FlagRecvOnly).Should().NotBe(0ul);
        loop.SniffedPacketCount.Should().Be(1, "tek paket görüldü ama eşik aşılmadı");
        engine.IsOpen.Should().BeFalse();
    }

    // ── GpnCaptureLoop — paket başına telemetri ──────────────────────────

    [Fact]
    public async Task CaptureLoop_Telemetry_RecordsPackets_AndPublishesTicks()
    {
        // Dışa giden 2 UDP paketi (kaynak port 5000) → port tablosu PID 100'e
        // atfeder (havuzda) → telemetri sayaçları + top akış + per-PID satırı;
        // ~100 ms'lik tik döngüsü anlık görüntüleri kanala yayınlar.
        var api = new FakeDivertApi(recvPackets:
        [
            GpnPacketTestData.V4Udp(5000, 27015),
            GpnPacketTestData.V4Udp(5000, 27015),
        ]);
        using var engine = new WinDivertEngine(api);
        var channel = new EventChannel<GpnCaptureStatsSnapshot>();
        var snapshots = new List<GpnCaptureStatsSnapshot>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var sub = channel.AsObservable().Subscribe(s =>
        {
            snapshots.Add(s);
            if (s.TotalPackets >= 2)
            {
                cts.Cancel(); // son durum yayınlandı — döngüyü kapat
            }
        });

        var loop = new GpnCaptureLoop(
            new StubResolver(initial: Snap(100u), refresh: null),
            engine,
            portPidTable: new FakePortPidTable(5000, 100),
            statsChannel: channel,
            options: new GpnCaptureOptions
            {
                SniffFirst = false,
                EnableTelemetry = true,
                TelemetryTickInterval = TimeSpan.FromMilliseconds(50),
            });

        await loop.RunAsync(cts.Token);

        loop.Telemetry.TotalPackets.Should().Be(2, "her paket telemetriye kaydedilir");
        loop.Telemetry.Snapshot.UdpPackets.Should().Be(2);
        loop.Telemetry.Snapshot.OutboundPackets.Should().Be(2);
        loop.ConsumedPacketCount.Should().Be(2, "varsayılan inject tüketir");

        snapshots.Should().NotBeEmpty("tik döngüsü anlık görüntü yayınlar");
        var last = snapshots[^1];
        last.TotalPackets.Should().Be(2);
        last.TopFlows.Should().ContainSingle(f => f.PeerEndpoint == "8.8.4.4:27015" && f.Packets == 2);
        last.ByPid.Should().ContainSingle(p => p.Pid == 100 && p.InPool && p.Packets == 2);
    }

    [Fact]
    public async Task CaptureLoop_Telemetry_OutOfPoolPid_FlaggedNotInPool()
    {
        // Port 5000 → PID 200, ama yakalama havuzu {100} — paket havuz DIŞI bir
        // süreçten geliyor (kaçan trafik). Dashboard bu satırı kırmızı işaretler.
        var api = new FakeDivertApi(recvPackets: [GpnPacketTestData.V4Udp(5000, 27015)]);
        using var engine = new WinDivertEngine(api);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var loop = new GpnCaptureLoop(
            new StubResolver(initial: Snap(100u), refresh: null),
            engine,
            portPidTable: new FakePortPidTable(5000, 200),
            options: new GpnCaptureOptions
            {
                SniffFirst = false,
                EnableTelemetry = true,
                TelemetryTickInterval = TimeSpan.FromMilliseconds(30),
            });

        await loop.RunAsync(cts.Token);

        loop.Telemetry.TotalPackets.Should().Be(1);
        loop.Telemetry.Snapshot.ByPid.Should().ContainSingle(p => p.Pid == 200 && !p.InPool);
    }

    [Fact]
    public async Task CaptureLoop_TelemetryDisabled_NoPublish()
    {
        var api = new FakeDivertApi(recvPackets: [GpnPacketTestData.V4Udp(5000, 27015)]);
        using var engine = new WinDivertEngine(api);
        var channel = new EventChannel<GpnCaptureStatsSnapshot>();
        var snapshots = new List<GpnCaptureStatsSnapshot>();
        using var sub = channel.AsObservable().Subscribe(snapshots.Add);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var loop = new GpnCaptureLoop(
            new StubResolver(initial: Snap(100u), refresh: null),
            engine,
            portPidTable: new FakePortPidTable(5000, 100),
            statsChannel: channel,
            options: new GpnCaptureOptions
            {
                SniffFirst = false,
                EnableTelemetry = false,
            });

        await loop.RunAsync(cts.Token);

        loop.Telemetry.TotalPackets.Should().Be(0, "telemetri kapalıyken kayıt yapılmaz");
        snapshots.Should().BeEmpty("kapalıyken kanala yayın yapılmaz");
    }

    // ── GpnCaptureLoop — bağlantı anında DI'dan options çözümü (entegrasyon) ──

    [Fact]
    public async Task Di_ConnectTime_ResolvesOptions_AndSettingsChange_NewOpenUsesNewSlotAndFlags()
    {
        // "Uygulama başlangıcı": config A — katman 1 (NETWORK_FORWARD) / yön 0 (inbound)
        // → slot 2; QueueSize açık → FlagQueueSize de açık olmalı.
        var config = CoreConfigTestFactory.CreateConfig();
        config.GpnCaptureItem = new GpnCaptureItem
        {
            QueueLen = 4096,
            QueueTime = 500,
            QueueSize = 1048576,
            EnableQueueSize = true,
            Layer = 1,
            Direction = 0,
        };
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var services = new ServiceCollection();
        services.AddAoGpnGpnServices();
        using var provider = services.BuildServiceProvider();

        // "Bağlantı 1": loop, options'ı bağlantı anında DI'dan çözer (transient → canlı config).
        var options1 = provider.GetRequiredService<GpnCaptureOptions>();
        options1.OpenParams.QueueLen2.Should().Be(4096, "slot = layer(1)*2 + direction(0) = 2");
        (options1.ExtraFlags & WinDivertNative.FlagQueueSize).Should().NotBe(0ul);

        var api1 = new FakeDivertApi(infinitePackets: true);
        using (var engine1 = new WinDivertEngine(api1))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var loop1 = new GpnCaptureLoop(
                new StubResolver(initial: Snap(42u), refresh: null),
                engine1,
                options: options1);
            await loop1.RunAsync(cts.Token);
        }

        // Varsayılan sniff-önce akışı: sniff + recv-only açılışı — ikisi de aynı options.
        api1.OpenParamsList.Should().HaveCount(2, "sniff + recv-only açılışı");
        api1.OpenParamsList.Should().OnlyContain(p => p.QueueLen2 == 4096 && p.QueueTime2 == 500 && p.QueueSize2 == 1048576);
        (api1.OpenFlags[0] & WinDivertNative.FlagSniff).Should().NotBe(0ul);
        (api1.OpenFlags[1] & WinDivertNative.FlagRecvOnly).Should().NotBe(0ul);
        api1.OpenFlags.Should().OnlyContain(f => (f & WinDivertNative.FlagQueueSize) != 0);
        api1.OpenFlags.Should().OnlyContain(f => (f & WinDivertNative.FlagQueueLength) != 0);

        // "Ayar değişti" — dashboard set_gpn_capture_settings yükü (patch) config'e yazılır
        // (MainWindow.SetGpnCaptureSettingsAsync akışının eşdeğeri: patch → config → save).
        config.GpnCaptureItem = new GpnCaptureSettingsPatch(
            QueueLen: 32768, QueueTime: 2000, QueueSize: null,
            EnableQueueLen: null, EnableQueueTime: null, EnableQueueSize: false,
            Layer: 0, Direction: 1).Apply(config.GpnCaptureItem);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        // "Bağlantı 2": options bağlantı anında TAZE çözülür — singleton değil, yoksa
        // kullanıcının kuyruk değişikliği bir sonraki açılışa asla yansımazdı.
        var options2 = provider.GetRequiredService<GpnCaptureOptions>();
        options2.Should().NotBeSameAs(options1, "options her bağlantıda canlı config'den türetilir");
        options2.OpenParams.QueueLen1.Should().Be(32768, "slot = layer(0)*2 + direction(1) = 1");

        var api2 = new FakeDivertApi(infinitePackets: true);
        using (var engine2 = new WinDivertEngine(api2))
        {
            using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var loop2 = new GpnCaptureLoop(
                new StubResolver(initial: Snap(42u), refresh: null),
                engine2,
                options: options2);
            await loop2.RunAsync(cts2.Token);
        }

        // Yeni açılış YENİ slot/flag'lerle: slot 1 (0*2+1), QueueSize bayrağı kaldırıldı.
        api2.OpenParamsList.Should().HaveCount(2, "sniff + recv-only açılışı");
        api2.OpenParamsList.Should().OnlyContain(p => p.QueueLen1 == 32768 && p.QueueTime1 == 2000);
        api2.OpenFlags.Should().OnlyContain(f => (f & WinDivertNative.FlagQueueSize) == 0, "EnableQueueSize kapatıldı → bayrak yok");
        api2.OpenFlags.Should().OnlyContain(f => (f & WinDivertNative.FlagQueueLength) != 0, "EnableQueueLen varsayılan açık");
        api2.OpenFlags.Should().OnlyContain(f => (f & WinDivertNative.FlagRecvOnly) != 0 || (f & WinDivertNative.FlagSniff) != 0);
    }

    // ── Test donanımı ────────────────────────────────────────────────────

    private static TargetPidSnapshot Snap(params uint[] pids)
        => new(pids.OrderBy(x => x).ToArray(), 0, DateTimeOffset.UtcNow);

    private sealed class FakeProcessTreeSource : IProcessTreeSource
    {
        private readonly IEnumerable<ProcessInfo> _processes;
        private readonly object? _lock;

        public FakeProcessTreeSource(params ProcessInfo[] processes) : this(processes, null) { }

        public FakeProcessTreeSource(IEnumerable<ProcessInfo> processes, object? gate)
        {
            _processes = processes;
            _lock = gate;
        }

        public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
        {
            if (_lock is null)
            {
                return _processes.ToArray();
            }
            lock (_lock)
            {
                return _processes.ToArray();
            }
        }
    }

    /// <summary>Resolve/RefreshLoopOverrides alt sınıfta denetlenir — timer/test determinizmi.</summary>
    private sealed class StubResolver : GpnTargetResolver
    {
        private readonly TargetPidSnapshot? _initial;
        private readonly TargetPidSnapshot? _refresh;

        public StubResolver(TargetPidSnapshot? initial, TargetPidSnapshot? refresh)
            : base(new[] { "Game.exe" }, new FakeProcessTreeSource())
        {
            _initial = initial;
            _refresh = refresh;
        }

        public override TargetPidSnapshot? Resolve(CancellationToken cancellationToken = default) => _initial;

        public override async IAsyncEnumerable<TargetPidSnapshot> RefreshLoopAsync(
            TimeSpan? interval = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (_refresh is not null)
            {
                yield return _refresh;
            }
        }
    }

    private sealed class FakePortPidTable : IGpnPortPidTable
    {
        private readonly IReadOnlyDictionary<ushort, uint> _map;

        public FakePortPidTable(ushort port, uint pid)
        {
            _map = new Dictionary<ushort, uint> { [port] = pid };
        }

        public IReadOnlyDictionary<ushort, uint> GetUdpPortOwners() => _map;
    }

    private sealed class FakeDivertApi : IWinDivertApi
    {
        private readonly byte[][] _recvPackets;
        private readonly bool _infinite;
        private int _recvIndex;
        private bool _limited;
        public List<string> OpenFilters { get; } = new();
        public List<ulong> OpenFlags { get; } = new();

        /// <summary>Her OpenEx çağrısına verilen OpenParams (slot/kuyruk değerleri) — hangi
        /// ayarlarla açıldığını doğrulamak için sırayla kaydedilir.</summary>
        public List<WinDivertOpenParams> OpenParamsList { get; } = new();

        public FakeDivertApi(byte[][]? recvPackets = null, bool infinitePackets = false)
        {
            _recvPackets = recvPackets ?? Array.Empty<byte[]>();
            _limited = recvPackets is not null;
            _infinite = infinitePackets;
        }

        public IntPtr Open(string? filter, int layer, short priority, ulong flags)
        {
            OpenFilters.Add(filter ?? string.Empty);
            OpenFlags.Add(flags);
            OpenParamsList.Add(default);
            return new IntPtr(0xBEEF);
        }

        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams)
        {
            OpenFilters.Add(filter ?? string.Empty);
            OpenFlags.Add(flags);
            OpenParamsList.Add(openParams); // struct — kopyalanır
            return new IntPtr(0xBEEF);
        }

        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
        {
            recvLen = 0;
            if (_infinite)
            {
                // Sonsuz paket akışı — sniff eşiğine ve recv-only pompasına yetecek kadar üretir.
                var data = new byte[] { 0x45, 0x00, 0x00, 0x1c, 0x00, 0x01 };
                Marshal.Copy(data, 0, packet, data.Length);
                recvLen = data.Length;
                address.Direction = WinDivertNative.DirectionOutbound;
                return true;
            }
            if (_limited && _recvIndex < _recvPackets.Length)
            {
                var data = _recvPackets[_recvIndex++];
                Marshal.Copy(data, 0, packet, data.Length);
                recvLen = data.Length;
                address.Direction = WinDivertNative.DirectionOutbound;
                return true;
            }
            return false;
        }

        public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address)
        {
            sendLen = length;
            return true;
        }

        public bool Close(IntPtr handle) => true;

        public bool SetParam(IntPtr handle, int param, ulong value) => true;

        public int GetLastError() => 0;
    }
}