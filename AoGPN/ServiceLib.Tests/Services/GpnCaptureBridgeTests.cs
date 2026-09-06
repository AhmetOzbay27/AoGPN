using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnCaptureBridge — yakalama tünel köprüsünün bağlantı-anı entegrasyonu.
/// GpnCaptureLoop'un varsayılan tüketimi (ConsumeInjectAsync) yerine
/// WireGuardTunnelService.CreateInjectHandler kullanılır: recv-only'de yakalanan
/// oyun paketleri tünele/adaptöre enjekte edilir; ters yön (tünel çıkışı) çözülüp
/// tüketiciye verilir. Sürücü yok: WinDivert natif katmanı sahte API, Wintun
/// session sahte session, süreç ağacı sahte kaynakla değiştirilir.
/// </summary>
public class GpnCaptureBridgeTests
{
    // ── Köprü yaşam döngüsü ──────────────────────────────────────────────

    [Fact]
    public async Task Start_GameRunning_InjectsPacketsIntoTunnel_StopCleansUp()
    {
        // Oyuncu çalışıyor (Game.exe, PID 100). 2 geçerli UDP paketi (kaynak port
        // 5000 → port tablosu PID 100'e atfeder) yakalanıyor → köprü her ikisini de
        // tünel inject hattından geçirir (varsayılan ConsumeInjectAsync DEĞİL).
        var api = new FakeDivertApi(recvPackets:
        [
            GpnPacketTestData.V4Udp(5000, 27015),
            GpnPacketTestData.V4Udp(5000, 27015),
        ]);
        using var engine = new WinDivertEngine(api);
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(), // SniffFirst=false → deterministik
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => noopTransport,
            portPidTable: new FakePortPidTable(5000, 100));

        var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        started.Should().BeTrue();
        bridge.IsRunning.Should().BeTrue();
        bridge.LastIdleCause.Should().Be(GpnBridgeIdleCause.None, "canlıya alınan deneme bekleme nedeni taşımaz");
        await WaitUntilAsync(() => bridge.TunnelSnapshot.Sent == 2, TimeSpan.FromSeconds(5));
        bridge.TunnelSnapshot.Sent.Should().Be(2, "hedef sürece atfedilen her paket UDP veri yolundan sunucuya gönderilir (Faz 2d)");
        noopTransport.SentCount.Should().Be(2);
        session.Injected.Should().BeEmpty("giden yol Wintun'a değil gerçek sunucuya gider");
        tunnel.AdapterName.Should().Be("AoGPN-it", "tünel seçilen sunucuyla adlandırılır");
        api.OpenFilters.Should().HaveCount(1, "ilk anlık görüntü yeniden açılışa yol açmaz (paket düşürme yarışı yok)");

        await bridge.StopAsync();

        bridge.IsRunning.Should().BeFalse();
        session.Open.Should().BeFalse("Stop tünel session'ını kapatır");
        engine.IsOpen.Should().BeFalse("Stop WinDivert handle'ını kapatır");
        api.OpenFlags.Should().OnlyContain(f => (f & (WinDivertNative.FlagRecvOnly | WinDivertNative.FlagSniff)) == 0,
            "yakalama aşaması düz divert — Send etkin (hedef dışı paketler geri enjekte edilir)");
    }

    [Fact]
    public async Task Start_TargetNotRunning_ReturnsFalse_NoTunnelOpen()
    {
        // Oyun kapalı → köprü başlamaz (çekirdek bağlantısı bozulmaz), tünel açılmaz.
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var tunnel = new WireGuardTunnelService(session: new RecordingSession());
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(200, 0, "notepad.exe")));

        var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        started.Should().BeFalse();
        bridge.IsRunning.Should().BeFalse();
        tunnel.IsOpen.Should().BeFalse();
        engine.IsOpen.Should().BeFalse();
        // Tier 2 — kontrollü Ready-idle: neden kayıtlıdır (uyarı basılmaz), çekirdek
        // bağlantısı etkilenmez.
        bridge.LastIdleCause.Should().Be(GpnBridgeIdleCause.TargetNotRunning);
    }

    [Fact]
    public async Task Start_EmptyTargetNames_ReturnsFalse()
    {
        // Game Boost listesinde "vpn" eylemli uygulama yok → köprü başlamaz.
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var tunnel = new WireGuardTunnelService(session: new RecordingSession());
        var bridge = new GpnCaptureBridge(
            () => [],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")));

        var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        started.Should().BeFalse();
        bridge.IsRunning.Should().BeFalse();
        tunnel.IsOpen.Should().BeFalse();
        bridge.LastIdleCause.Should().Be(GpnBridgeIdleCause.NoTargetsConfigured);
    }

    [Fact]
    public async Task Start_NullServer_ReturnsFalse()
    {
        // V2rayTCP modu (sunucu yok) → köprü başlatılmaz.
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var tunnel = new WireGuardTunnelService(session: new RecordingSession());
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")));

        var started = await bridge.StartAsync(null, TestContext.Current.CancellationToken);

        started.Should().BeFalse();
        bridge.IsRunning.Should().BeFalse();
        tunnel.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Start_ForeignWireGuardRunning_NotKilled_TunnelStillOpens()
    {
        // GPN akışı foreign-tunnel koruması artık yalnızca TESPİT eder, öldürmez:
        // resmi WireGuard uygulaması (wireguard.exe) çalışıyorken köprü kendi
        // tünelini (Wintun) yine açar — üçüncü taraf istemciye dokunulmaz
        // (kapatma kararı kullanıcınındır).
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: _ => false,
            isAppCoreRunning: () => false,
            foreignProcessNames: () => ["wireguard.exe"]);

        detector.Detect(10808).ForeignProcessNames.Should().Contain("wireguard.exe");

        using var engine = new WinDivertEngine(new FakeDivertApi());
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => noopTransport,
            foreignDetector: detector);

        var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        started.Should().BeTrue("yabancı WG varken de tünel açılır (kill yok)");
        bridge.IsRunning.Should().BeTrue();
        tunnel.IsOpen.Should().BeTrue();
        session.Open.Should().BeTrue();

        await bridge.StopAsync();
    }

    [Fact]
    public async Task Start_NoForeignTunnel_Clean_StillOpens()
    {
        // Yabancı durum yok → tünel normal açılır.
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: _ => false,
            isAppCoreRunning: () => false,
            foreignProcessNames: () => []);

        using var engine = new WinDivertEngine(new FakeDivertApi());
        using var session = new RecordingSession();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => new NoopWireGuardTransport(),
            foreignDetector: detector);

        var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        started.Should().BeTrue();
        tunnel.IsOpen.Should().BeTrue();
        await bridge.StopAsync();
    }

    [Fact]
    public async Task Start_DoesNotFlushTcpConnections_NativeCaptureOnlyTunnelsUdp()
    {
        // Regresyon (canlı gözlenen): bağlantı anında hedef uygulamaların TCP soketleri
        // KESİLMEZ. Köprü yalnızca outbound UDP yakalar (NETWORK katmanı processId
        // tanımaz, filtre "udp"); TCP asla tünele alınmaz — yeniden kurulsa bile doğrudan
        // gider. Eski davranış tarayıcı/Discord bağlantılarını gereksiz yere öldürüp
        // ERR_NETWORK_CHANGED üretiyordu (kullanıcıda görülen "bağlantı kesildi").
        var flushCount = 0;
        var detector = new ForeignTunnelDetector(
            tunAdapterNames: () => [],
            isPortListening: _ => false,
            isAppCoreRunning: () => false,
            foreignProcessNames: () => []);

        using var engine = new WinDivertEngine(new FakeDivertApi());
        using var session = new RecordingSession();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe", "GameChild.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => new NoopWireGuardTransport(),
            foreignDetector: detector,
            connectionFlusher: _ =>
            {
                flushCount++;
                return 42;
            });

        var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        started.Should().BeTrue();
        tunnel.IsOpen.Should().BeTrue("tünel normal açılır");
        // Flush çalıştırılmamalı — TCP kesintisi yok (uygulamanın bağlantıları korunur).
        await Task.Delay(300, TestContext.Current.CancellationToken);
        flushCount.Should().Be(0, "native yakalama TCP tünellemez — bağlantı kesmek saf aksaklıktır");

        await bridge.StopAsync();
    }

    [Fact]
    public async Task Start_FlushFails_DoesNotPreventConnect()
    {
        // Bağlantı temizleyici hata verse bile köprü asla düşmez — temizlik best-effort'tur.
        using var engine = new WinDivertEngine(new FakeDivertApi());
        using var session = new RecordingSession();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => new NoopWireGuardTransport(),
            foreignDetector: new ForeignTunnelDetector(
                tunAdapterNames: () => [],
                isPortListening: _ => false,
                isAppCoreRunning: () => false),
            connectionFlusher: _ => throw new InvalidOperationException("flush başarısız"));

        var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        started.Should().BeTrue("flush hatası köprüyü düşürmez");
        bridge.IsRunning.Should().BeTrue();
        await bridge.StopAsync();
        bridge.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Start_AlreadyRunning_ReturnsTrue_DoesNotDoubleOpen()
    {
        using var engine = new WinDivertEngine(new FakeDivertApi(recvPackets: [new byte[] { 1, 2, 3 }]));
        var session = new RecordingSession();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")));

        await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);
        var second = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        second.Should().BeTrue("zaten çalışırken Start idempotent");
        session.Open.Should().BeTrue();
        await bridge.StopAsync();
    }

    [Fact]
    public async Task Stop_WhenNotRunning_NoThrow()
    {
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var tunnel = new WireGuardTunnelService(session: new RecordingSession());
        var bridge = new GpnCaptureBridge(() => ["Game.exe"], engine, tunnel);

        var act = async () => await bridge.StopAsync();

        await act.Should().NotThrowAsync();
        engine.IsOpen.Should().BeFalse();
    }

    // ── Faz 2c: taşıma dikişi (gerçek WireGuard veri düzlemi) ────────────

    [Fact]
    public async Task Start_DefaultFactory_InstallsNoiseTransportOnTunnel()
    {
        // Varsayılan taşıma üreticisi, profilin anahtarlarıyla gerçek WireGuard
        // veri düzlemini (WireGuardNoiseTransport) kurar ve tünele bağlar.
        using var engine = new WinDivertEngine(new FakeDivertApi(recvPackets: [new byte[] { 1, 2, 3 }]));
        var tunnel = new WireGuardTunnelService(session: new RecordingSession());
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")));

        await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);

        tunnel.Transport.Should().BeOfType<WireGuardNoiseTransport>("gerçek veri düzlemi köprüye bağlanır");
        await bridge.StopAsync();
    }

    [Fact]
    public async Task Start_InvalidKeys_ReturnsFalse_NoTunnelOpen()
    {
        // Profildeki anahtarlar geçersizse köprü başlamaz (çekirdek bağlantısı
        // bozulmaz) — sessiz passthrough yerine açık ret.
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var tunnel = new WireGuardTunnelService(session: new RecordingSession());
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")));

        var badServer = Server() with { ClientPrivateKey = "%%%geçersiz%%%" };
        var started = await bridge.StartAsync(badServer, TestContext.Current.CancellationToken);

        started.Should().BeFalse();
        tunnel.IsOpen.Should().BeFalse();
        bridge.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Start_Failure_InvokesOnFailureWithReason()
    {
        // Gerçek bir hata (geçersiz anahtarlar) köprüyü atlar ve nedeni onFailure
        // geri çağrısına iletir — sessizce V2rayTCP'ye düşmek yerine kullanıcıya
        // gösterilmesi için.
        var reasons = new List<string>();
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var tunnel = new WireGuardTunnelService(session: new RecordingSession());
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            onFailure: reason =>
            {
                lock (reasons)
                {
                    reasons.Add(reason);
                }
                return Task.CompletedTask;
            });

        var badServer = Server() with { ClientPrivateKey = "%%%geçersiz%%%" };
        var started = await bridge.StartAsync(badServer, TestContext.Current.CancellationToken);

        started.Should().BeFalse();
        reasons.Should().HaveCount(1, "tek hata, tek bildirim");
        reasons[0].Should().Contain("WireGuard anahtarları geçersiz");
    }

    [Fact]
    public async Task Start_DesignSkips_DoNotInvokeOnFailure()
    {
        // Tasarım gereği atlanan durumlar (sunucu yok = V2rayTCP modu) geri çağrıyı
        // tetiklemez — yalnızca GERÇEK hatalar kullanıcıya bildirilir (gürültü yok).
        var reasons = new List<string>();
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            new WireGuardTunnelService(session: new RecordingSession()),
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            onFailure: reason =>
            {
                reasons.Add(reason);
                return Task.CompletedTask;
            });

        var started = await bridge.StartAsync(null, TestContext.Current.CancellationToken);

        started.Should().BeFalse();
        reasons.Should().BeEmpty("sunucu yok (V2rayTCP) geri çağrıyı tetiklemez");
    }

    // ── Ters yön (tünel çıkışı → tüketici) ──────────────────────────────

    [Fact]
    public async Task ReceivePath_TunnelPacket_DeliveredToConsumer_AndInjectedIntoAdapter()
    {
        using var engine = new WinDivertEngine(new FakeDivertApi(recvPackets: [new byte[] { 1, 2 }]));
        var session = new RecordingSession();
        var transport = new BridgeQueueTransport();
        transport.ReceiveQueue.Enqueue(new byte[] { 0xAA, 0xBB }); // sunucudan dönen şifreli tip-4
        var tunnel = new WireGuardTunnelService(transport, session);

        var delivered = new List<byte[]>();
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            receiveConsumer: (packet, _) =>
            {
                delivered.Add(packet);
                return ValueTask.CompletedTask;
            },
            transportFactory: _ => transport);

        await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken); // alım döngüsünün paketi çözüp tüketiciye vermesi için

        delivered.Should().HaveCount(1);
        delivered[0].Should().Equal(new byte[] { 0xAA, 0xBB });
        session.Injected.Should().HaveCount(1);
        session.Injected[0].Should().Equal(new byte[] { 0xAA, 0xBB }, "çözülen paket Wintun adaptörüne enjekte edilir");
        bridge.DeliveredToConsumer.Should().Be(1);
        await bridge.StopAsync();
    }

    // ── Launcher entegrasyonu: mihomo çekirdeği köprüyü DEVREDE BIRAKMAZ ──

    [Fact]
    public async Task Launcher_WireGuardLaunch_MihomoCore_DoesNotStartCaptureBridge()
    {
        // "Bağlan (WireGuardUDP)" → GpnCoreLauncher artık GPN profilini mihomo
        // çekirdeğiyle başlatır; süreç bazlı ayırımı (BsGLauncher.exe → warp-socks,
        // oyun → wg-<id>) mihomo kendi TUN'unda yapar. WinDivert yakalama köprüsü
        // (ikinci Wintun + filter) mihomo yolunda BİLEREK başlatılmaz — çakışır.
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        config.TunModeItem.EnableTun = false; // pre-socks yolu yok — BuildAll tek sonuç
        BindConfig(config);
        // RoutingItem tablosu test host'unda InitApp tarafından oluşturulmaz; launcher
        // GetDefaultRouting üzerinden okur — sorgudan önce yarat (idempotent).
        // AppManager'ın başlangıçta yarattığı tabloları yarat (idempotent): temiz bir
        // bin'de guiNDB.db tablosuzdur ve launcher/context-builder FullConfigTemplate
        // vb. tabloları okur — eksikse "no such table" ile çöker.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();

        var api = new FakeDivertApi(recvPackets: [new byte[] { 0x45, 0x00, 0x00, 0x1c, 0x00, 0x01 }]);
        using var engine = new WinDivertEngine(api);
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => noopTransport);

        using var tempRoot = new TempDir();
        // CoreEngineHost.StartAsync çekirdeğin var olduğunu doğrular (FakeRuntime
        // hiçbir exe'yi çalıştırmaz, yalnızca dosya varlığı kontrol edilir).
        var mihomoDir = Path.Combine(tempRoot.Path, "mihomo");
        Directory.CreateDirectory(mihomoDir);
        File.WriteAllText(Path.Combine(mihomoDir, "mihomo-windows-amd64-v1.exe"), "dummy");
        var host = new CoreEngineHost(
            config,
            (_, _) => Task.CompletedTask,
            runtime: new FakeRuntime(),
            binaryRegistry: new CoreBinaryRegistry(tempRoot.Path, () => true));
        var launcher = new GpnCoreLauncher(config, (_, _) => Task.CompletedTask, host, bridge);

        // Bu test mihomo ürün kararını kodlar: politika kapalıyken köprü DEVREDE
        // DEĞİLDİR. Derlenmiş varsayılandan (NativeGpnEnginePolicy.IsEnabled =
        // true — Tier 1 flip) bağımsız olmak için anahtar açıkça pinlenir.
        NativeGpnEnginePolicy.IsEnabled = false;
        try
        {
            await launcher.LaunchAsync(ConnectionMode.WireGuardUDP, Server(), TestContext.Current.CancellationToken);
        }
        finally
        {
            NativeGpnEnginePolicy.IsEnabled = false;
        }

        bridge.IsRunning.Should().BeFalse("mihomo kendi PROCESS-NAME ayırımını yapar — köprü kullanılmaz");
        tunnel.IsOpen.Should().BeFalse();
        engine.IsOpen.Should().BeFalse();

        await launcher.StopAsync(TestContext.Current.CancellationToken);

        bridge.IsRunning.Should().BeFalse("zaten kapalı — Stop idempotent");
        tunnel.IsOpen.Should().BeFalse();
        engine.IsOpen.Should().BeFalse();
    }

    // ── Tier 1 — NativeGpnStartStrategy: in-process motor entegrasyonu ───

    private static CoreConfigContext NativeContext() => new()
    {
        Node = GpnCoreLauncher.BuildWireGuardProfile(Server()),
        RunCoreType = ECoreType.mihomo,
        UseNativeGpnEngine = true,
    };

    [Fact]
    public async Task Strategy_StartAsync_StartsEngineInProcess_ReturnsNullProcess()
    {
        var api = new FakeDivertApi(recvPackets: [new byte[] { 0x45, 0x00, 0x00, 0x1c, 0x00, 0x01 }]);
        using var engine = new WinDivertEngine(api);
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => noopTransport);
        var strategy = new NativeGpnStartStrategy(bridge);

        var process = await strategy.StartAsync(NativeContext(),
            launcher: (_, _, _, _, _, _) => Task.FromResult<ProcessService?>(null),
            onExited: () => { });

        process.Should().BeNull("in-process motor harici süreç üretmez — null OLAĞANDIR");
        bridge.IsRunning.Should().BeTrue("köprü strateji üzerinden canlıya alındı (harici exe değil)");
        tunnel.IsOpen.Should().BeTrue();
        engine.IsOpen.Should().BeTrue();
        bridge.DynamicReArmEnabled.Should().BeTrue("native oturum dinamik re-arm otonomisi kazanır");

        await strategy.AfterStopAsync();

        bridge.IsRunning.Should().BeFalse("temiz kapanış — WinDivert + kuyruklar + Wintun");
        bridge.DynamicReArmEnabled.Should().BeFalse("oturum kapanınca dinamik otonomi bırakılır");
        tunnel.IsOpen.Should().BeFalse();
        engine.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Strategy_MidSessionFault_NotifiesCoreManagerViaOnExited()
    {
        // Geçerli UDP paketi (kaynak port 5000 → PID 100 = Game.exe) — kullanıcı-modu
        // rotalama yalnızca hedef sürece atfedilen paketleri tünel enjeksiyonuna
        // sokar; FaultingEncryptTransport ancak o zaman Encrypt fault'unu üretir.
        var api = new FakeDivertApi(recvPackets: [GpnPacketTestData.V4Udp(5000, 27015)]);
        using var engine = new WinDivertEngine(api);
        using var session = new RecordingSession();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => new FaultingEncryptTransport(),
            portPidTable: new FakePortPidTable(5000, 100));
        var onExitedCount = 0;
        var strategy = new NativeGpnStartStrategy(bridge);
        await strategy.StartAsync(NativeContext(),
            launcher: (_, _, _, _, _, _) => Task.FromResult<ProcessService?>(null),
            onExited: () => Interlocked.Increment(ref onExitedCount));

        // Oturum-içi fault: yakalanan ilk paketin tünel enjeksiyonu (Encrypt) hata
        // verir → yakalama döngüsü görevi fault → GpnCaptureBridge.EngineFailed →
        // strateji → CoreManager onExited (çökme kurtarma döngüsü tetiklenir).
        var notified = await WaitUntilAsync(() => Volatile.Read(ref onExitedCount) > 0, TimeSpan.FromSeconds(5));

        notified.Should().BeTrue("engine fault, harici process-exit olmadan CoreManager kurtarmasını tetiklemeli");
        await strategy.AfterStopAsync();
    }

    [Fact]
    public async Task Strategy_AfterStop_ClearHook_DoesNotTriggerRecovery()
    {
        var api = new FakeDivertApi(recvPackets: [new byte[] { 0x45, 0x00, 0x00, 0x1c, 0x00, 0x01 }]);
        using var engine = new WinDivertEngine(api);
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => noopTransport);
        var onExitedCount = 0;
        var strategy = new NativeGpnStartStrategy(bridge);
        await strategy.StartAsync(NativeContext(),
            launcher: (_, _, _, _, _, _) => Task.FromResult<ProcessService?>(null),
            onExited: () => Interlocked.Increment(ref onExitedCount));
        strategy.IsEngineFailureHookAttached.Should().BeTrue("oturum canlıyken fault köprüsü takılı olmalı");
        await strategy.AfterStopAsync();

        // BİLİNÇLİ duruş: EngineFailed aboneliği ve onExited bağı koparıldı —
        // oturum-içi fault mekanizması artık kurtarma TETİKLEYEMEZ.
        strategy.IsEngineFailureHookAttached.Should().BeFalse("bilinçli duruş sonrası fault köprüsü kopuk olmalı");
        onExitedCount.Should().Be(0);
    }

    // ── Tier 1 — The Live Flip: NativeGpnEnginePolicy → bağlam bayrağı ───

    [Fact]
    public async Task Launcher_WireGuardLaunch_PolicyOff_KeepsMihomoContext()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        config.TunModeItem.EnableTun = false;
        BindConfig(config);
        // AppManager'ın başlangıçta yarattığı tabloları yarat (idempotent): temiz bir
        // bin'de guiNDB.db tablosuzdur ve launcher/context-builder FullConfigTemplate
        // vb. tabloları okur — eksikse "no such table" ile çöker.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();

        var api = new FakeDivertApi(recvPackets: [new byte[] { 0x45, 0x00, 0x00, 0x1c, 0x00, 0x01 }]);
        using var engine = new WinDivertEngine(api);
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => noopTransport);
        using var tempRoot = new TempDir();
        var mihomoDir = Path.Combine(tempRoot.Path, "mihomo");
        Directory.CreateDirectory(mihomoDir);
        File.WriteAllText(Path.Combine(mihomoDir, "mihomo-windows-amd64-v1.exe"), "dummy");
        var runtime = new FakeRuntime();
        var host = new CoreEngineHost(
            config,
            (_, _) => Task.CompletedTask,
            runtime: runtime,
            binaryRegistry: new CoreBinaryRegistry(tempRoot.Path, () => true));
        var launcher = new GpnCoreLauncher(config, (_, _) => Task.CompletedTask, host, bridge);

        NativeGpnEnginePolicy.IsEnabled = false; // varsayılan — dormant
        try
        {
            await launcher.LaunchAsync(ConnectionMode.WireGuardUDP, Server(), TestContext.Current.CancellationToken);
        }
        finally
        {
            NativeGpnEnginePolicy.IsEnabled = false;
        }

        runtime.LastMainContext.Should().NotBeNull();
        runtime.LastMainContext!.UseNativeGpnEngine.Should().BeFalse("politika kapalıyken mihomo yolu birebir aynen kalır");
        bridge.IsRunning.Should().BeFalse("mihomo yolunda köprü kullanılmaz (çakışma koruması)");
    }

    [Fact]
    public async Task Launcher_WireGuardLaunch_PolicyOn_FlagsNativeEngine_LauncherDoesNotStartBridge()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
        config.TunModeItem.EnableTun = false;
        BindConfig(config);
        // AppManager'ın başlangıçta yarattığı tabloları yarat (idempotent): temiz bir
        // bin'de guiNDB.db tablosuzdur ve launcher/context-builder FullConfigTemplate
        // vb. tabloları okur — eksikse "no such table" ile çöker.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();

        var api = new FakeDivertApi(recvPackets: [new byte[] { 0x45, 0x00, 0x00, 0x1c, 0x00, 0x01 }]);
        using var engine = new WinDivertEngine(api);
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")),
            transportFactory: _ => noopTransport);
        using var tempRoot = new TempDir();
        var mihomoDir = Path.Combine(tempRoot.Path, "mihomo");
        Directory.CreateDirectory(mihomoDir);
        File.WriteAllText(Path.Combine(mihomoDir, "mihomo-windows-amd64-v1.exe"), "dummy");
        var runtime = new FakeRuntime();
        var host = new CoreEngineHost(
            config,
            (_, _) => Task.CompletedTask,
            runtime: runtime,
            binaryRegistry: new CoreBinaryRegistry(tempRoot.Path, () => true));
        var launcher = new GpnCoreLauncher(config, (_, _) => Task.CompletedTask, host, bridge);

        NativeGpnEnginePolicy.IsEnabled = true;
        try
        {
            await launcher.LaunchAsync(ConnectionMode.WireGuardUDP, Server(), TestContext.Current.CancellationToken);
        }
        finally
        {
            NativeGpnEnginePolicy.IsEnabled = false;
        }

        runtime.LastMainContext.Should().NotBeNull();
        runtime.LastMainContext!.UseNativeGpnEngine.Should().BeTrue("flip: bağlam yerel motoru ister");
        bridge.IsRunning.Should().BeFalse("köprüyü launcher DEĞİL, strateji (CoreManager) başlatır — çift başlatma yok");
        tunnel.IsOpen.Should().BeFalse();
    }

    // ── Tier 3 — Dinamik yeniden kurma (dynamic re-arm) ─────────────────

    [Fact]
    public void DynamicReArm_DefaultsOff_OnlyNativeEngineGainsAutonomy()
    {
        // Koruma kuralı: dinamik otonomi KAPALI doğar — legacy yol / harici çekirdekler
        // köprüyü kullanıyorsa davranış zerre değişmez. Açan tek taraf NativeGpnStartStrategy.
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            new WireGuardTunnelService(session: new RecordingSession()),
            settings: StubSettingsProvider.Direct(),
            source: new FakeProcessTreeSource());

        bridge.DynamicReArmEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task DynamicReArm_Disabled_GameStartingLater_DoesNotStartBridge()
    {
        // Anahtar KAPALIYSA (varsayılan) Ready-idle kalıcıdır: oyun sonradan başlasa
        // bile köprü kendiliğinden canlanmaz (legacy davranış birebir korunur).
        var source = new MutableProcessTreeSource();
        using var engine = new WinDivertEngine(new FakeDivertApi());
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: source,
            transportFactory: _ => noopTransport);

        var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);
        started.Should().BeFalse("oyun kapalı — Ready-idle");
        bridge.LastIdleCause.Should().Be(GpnBridgeIdleCause.TargetNotRunning);

        source.Set(new ProcessInfo(100, 0, "Game.exe"));
        await Task.Delay(400, TestContext.Current.CancellationToken);

        bridge.IsRunning.Should().BeFalse("re-arm kapalıyken oyun başlasa da köprü başlamaz");
        tunnel.IsOpen.Should().BeFalse();
        engine.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task DynamicReArm_ReadyIdle_GameStartsLater_BridgeComesUpLive()
    {
        // Tier 3 çekirdek senaryosu: bağlantı anında oyun kapalı (Ready-idle) → oyun
        // sonradan başlayınca Ready-idle denetçisi köprüyü kendiliğinden canlıya alır.
        var source = new MutableProcessTreeSource();
        using var engine = new WinDivertEngine(new FakeDivertApi());
        using var session = new RecordingSession();
        var noopTransport = new NoopWireGuardTransport();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: source,
            transportFactory: _ => noopTransport);
        bridge.DynamicReArmEnabled = true; // native oturum — yalnızca bu anahtarla
        var oldPoll = GpnCaptureBridge.ReadyIdlePollInterval;
        GpnCaptureBridge.ReadyIdlePollInterval = TimeSpan.FromMilliseconds(120);
        try
        {
            var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);
            started.Should().BeFalse("oyun kapalı — Ready-idle");
            bridge.LastIdleCause.Should().Be(GpnBridgeIdleCause.TargetNotRunning);

            // Oyun başlar → denetçi yakalar ve köprüyü canlıya alır (yeniden bağlanma YOK).
            source.Set(new ProcessInfo(100, 0, "Game.exe"));
            var live = await WaitUntilAsync(() => bridge.IsRunning && tunnel.IsOpen && engine.IsOpen, TimeSpan.FromSeconds(10));

            live.Should().BeTrue("oyun başlayınca köprü kendiliğinden canlanır");
            bridge.IsRunning.Should().BeTrue();
            tunnel.IsOpen.Should().BeTrue();
            engine.IsOpen.Should().BeTrue();
        }
        finally
        {
            GpnCaptureBridge.ReadyIdlePollInterval = oldPoll;
            await bridge.StopAsync();
        }
        bridge.IsRunning.Should().BeFalse("Stop denetçiyi de kapatır — köprü durur");
        tunnel.IsOpen.Should().BeFalse();
        engine.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task DynamicReArm_Live_GameCloses_BridgeReturnsToIdle_AndReArmsOnRelaunch()
    {
        // Tam uyku/uyanma döngüsü: canlı oyun kapanır → köprü Ready-idle'a döner
        // (veri düzlemi kapanır, oturum korunur) → oyun yeniden başlar → köprü canlanır.
        var source = new MutableProcessTreeSource(new ProcessInfo(100, 0, "Game.exe"));
        using var engine = new WinDivertEngine(new FakeDivertApi());
        var noopTransport = new NoopWireGuardTransport();
        // Session FACTORY: gerçek yol her Open'da taze natif session üretir — re-arm
        // (Close→Open) döngüsünü sahteyle doğrulamak için her Open taze açık sahte
        // session vermelidir (tek-örnek sahte Close sonrası kalıcı kapanırdı).
        var tunnel = new WireGuardTunnelService(sessionFactory: () => new RecordingSession());
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: StubSettingsProvider.Direct(),
            source: source,
            transportFactory: _ => noopTransport);
        bridge.DynamicReArmEnabled = true;
        var oldPoll = GpnCaptureBridge.ReadyIdlePollInterval;
        GpnCaptureBridge.ReadyIdlePollInterval = TimeSpan.FromMilliseconds(120);
        try
        {
            var started = await bridge.StartAsync(Server(), TestContext.Current.CancellationToken);
            started.Should().BeTrue("oyun çalışıyor — canlı");

            // Oyun kapanır → denetçi veri düzlemini kapatır (Ready-idle). "İzleme" durumu
            // (_cts null) önce düşer; kapama (_tunnel/_engine) onu izler — son hale bak.
            source.Set();
            var idle = await WaitUntilAsync(() => !bridge.IsRunning && !tunnel.IsOpen && !engine.IsOpen, TimeSpan.FromSeconds(10));
            idle.Should().BeTrue("oyun kapanınca köprü Ready-idle'a döner (veri düzlemi kapanır)");

            // Oyun yeniden başlar → köprü canlanır (re-arm).
            source.Set(new ProcessInfo(200, 0, "Game.exe"));
            var live = await WaitUntilAsync(() => bridge.IsRunning && tunnel.IsOpen && engine.IsOpen, TimeSpan.FromSeconds(10));
            live.Should().BeTrue("oyun yeniden başlayınca köprü yeniden canlanır");
            bridge.IsRunning.Should().BeTrue();
            tunnel.IsOpen.Should().BeTrue();
            engine.IsOpen.Should().BeTrue();
        }
        finally
        {
            GpnCaptureBridge.ReadyIdlePollInterval = oldPoll;
            await bridge.StopAsync();
        }
        bridge.IsRunning.Should().BeFalse();
        tunnel.IsOpen.Should().BeFalse();
        engine.IsOpen.Should().BeFalse();
    }

    // ── Yardımcılar ─────────────────────────────────────────────────────

    /// <summary>Köprü görevlerinin (Task.Run) paketleri işlemesi için kısa süreli yoklama.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                return false;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return true;
    }

    private static GpnServerProfile Server() => new(
        ServerId: "it",
        Name: "İtalya",
        EndpointHost: "127.0.0.1",
        EndpointPort: 51820,
        ServerPublicKey: "5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=",
        ClientPrivateKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        ClientAddress: "10.66.66.2/24");

    private static void BindConfig(Config config)
        => CoreConfigTestFactory.BindAppManagerConfig(config);

    private sealed class StubSettingsProvider : IGpnCaptureSettingsProvider
    {
        private readonly GpnCaptureItem _item;

        private StubSettingsProvider(GpnCaptureItem item) => _item = item;

        /// <summary>Doğrudan recv-only (sniff ön aşaması yok) — deterministik testler.</summary>
        public static StubSettingsProvider Direct() => new(new GpnCaptureItem());

        public GpnCaptureItem Current => _item;

        public GpnCaptureOptions CaptureOptions => new() { SniffFirst = false };
    }

    private sealed class FakeProcessTreeSource : IProcessTreeSource
    {
        private readonly IEnumerable<ProcessInfo> _processes;

        public FakeProcessTreeSource(params ProcessInfo[] processes) => _processes = processes;

        public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
            => _processes.ToArray();
    }

    /// <summary>Oyun açılıp/kapanma senaryoları için canlı değiştirilebilir süreç kaynağı.</summary>
    private sealed class MutableProcessTreeSource : IProcessTreeSource
    {
        private volatile ProcessInfo[] _current;

        public MutableProcessTreeSource(params ProcessInfo[] processes) => _current = processes;

        public void Set(params ProcessInfo[] processes) => _current = processes;

        public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
            => _current;
    }

    /// <summary>UDP port→PID köprüsü (kullanıcı-modu rotalama testi) — sabit harita.</summary>
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
        private readonly bool _limited;
        private int _recvIndex;
        public int RecvCount { get; private set; }
        public List<string> OpenFilters { get; } = new();
        public List<ulong> OpenFlags { get; } = new();

        public FakeDivertApi(byte[][]? recvPackets = null)
        {
            _recvPackets = recvPackets ?? [];
            _limited = recvPackets is not null;
        }

        public IntPtr Open(string? filter, int layer, short priority, ulong flags)
        {
            OpenFilters.Add(filter ?? string.Empty);
            OpenFlags.Add(flags);
            return new IntPtr(0xBEEF);
        }

        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams)
        {
            OpenFilters.Add(filter ?? string.Empty);
            OpenFlags.Add(flags);
            return new IntPtr(0xBEEF);
        }

        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
        {
            RecvCount++;
            recvLen = 0;
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

    private sealed class RecordingSession : IWintunSession
    {
        public List<byte[]> Injected { get; } = new();
        public Queue<byte[]> ReceiveQueue { get; } = new();
        public bool Open { get; set; } = true;

        public bool InjectPacket(ReadOnlySpan<byte> packet)
        {
            if (!Open)
            {
                return false;
            }
            Injected.Add(packet.ToArray());
            return true;
        }

        public bool TryReceivePacket(out byte[] packet, int timeoutMs)
        {
            packet = [];
            if (!Open || ReceiveQueue.Count == 0)
            {
                return false;
            }
            packet = ReceiveQueue.Dequeue();
            return true;
        }

        public bool IsOpen => Open;

        public void Dispose() => Open = false;
    }

    /// <summary>
    /// Faz 2d köprü fake taşıması: passthrough şifreleme + kuyruktan tüketiciye
    /// akan alım döngüsü (sunucudan dönen tip-4 simülasyonu).
    /// </summary>
    private sealed class BridgeQueueTransport : IWireGuardTransport
    {
        public Queue<byte[]> ReceiveQueue { get; } = new();
        public long EncryptedCount => 0;
        public long DecryptedCount => 0;
        public long DecryptFailedCount => 0;
        public long SentCount => 0;
        public long SendFailedCount => 0;
        public bool IsDataPathActive => true;

        public byte[]? Encrypt(ReadOnlySpan<byte> packet) => packet.ToArray();
        public byte[]? Decrypt(ReadOnlySpan<byte> packet) => packet.ToArray();

        public ValueTask<bool> SendAsync(byte[] wirePacket, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public async Task RunReceiveLoopAsync(
            Func<byte[], CancellationToken, ValueTask> consumer,
            TimeSpan idleTimeout,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && ReceiveQueue.Count > 0)
            {
                await consumer(ReceiveQueue.Dequeue(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Oturum-içi fault senaryosu: Encrypt her çağrıda fırlatır — yakalama
    /// döngüsünün tünel enjeksiyon hattı (PumpAsync → _inject) hata verir ve
    /// görev fault olur (EngineFailed → onExited köprüsünü tetikler).
    /// </summary>
    private sealed class FaultingEncryptTransport : IWireGuardTransport
    {
        public long EncryptedCount => 0;
        public long DecryptedCount => 0;
        public long DecryptFailedCount => 0;
        public long SentCount => 0;
        public long SendFailedCount => 0;
        public bool IsDataPathActive => true;

        public byte[]? Encrypt(ReadOnlySpan<byte> packet)
            => throw new InvalidOperationException("transport fault (Encrypt)");

        public byte[]? Decrypt(ReadOnlySpan<byte> packet) => packet.ToArray();

        public ValueTask<bool> SendAsync(byte[] wirePacket, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public Task RunReceiveLoopAsync(
            Func<byte[], CancellationToken, ValueTask> consumer,
            TimeSpan idleTimeout,
            CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>CoreEngineHost'u gerçek çekirdek süreci başlatmadan sürebilen sahte runtime.</summary>
    private sealed class FakeRuntime : ICoreRuntime
    {
        public IReadOnlyDictionary<CoreHealthRole, CoreHealthSnapshot> Health { get; } =
            new Dictionary<CoreHealthRole, CoreHealthSnapshot>();

        public CoreHealthSnapshot GetHealth(CoreHealthRole role)
            => new(role, CoreHealthState.Ready, ECoreType.mihomo, 10808);

        public Task InitializeAsync(Config config, Func<bool, string, Task> update) => Task.CompletedTask;

        public CoreConfigContext? LastMainContext { get; private set; }

        public Task StartAsync(CoreConfigContext? mainContext, CoreConfigContext? preContext)
        {
            LastMainContext = mainContext;
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
    }

    /// <summary>CoreBinaryRegistry'nin sing-box exe aramasını geçmesi için geçici boş binary kökü.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aogpn-test-{Guid.NewGuid():N}");

        public TempDir()
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "sing_box"));
            File.WriteAllBytes(System.IO.Path.Combine(Path, "sing_box", "sing-box-client.exe"), [0x4D, 0x5A]);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // temizlik isteğe bağlı
            }
        }
    }
}
