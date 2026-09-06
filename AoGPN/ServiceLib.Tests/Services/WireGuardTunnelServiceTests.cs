using System.Runtime.CompilerServices;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// WireGuardTunnelService (Faz 2b→2d) sürücüsüz testleri: IWintunSession + taşıma
/// dikişi sahteleriyle köprü akışı, şifreleme sırası, kalıcı UDP veri yolu
/// (Encrypt→SendAsync; UDP→Decrypt→Wintun), telemetri ve hata yolları.
/// Gerçek wintun.dll/sürücü/sunucu gerekmez (WintunDriverSmokeTests'e bakın).
/// </summary>
public class WireGuardTunnelServiceTests
{
    // ── Açılış / temel ───────────────────────────────────────────────────

    [Fact]
    public void Open_WithTestSession_SetsOpenAndAdapterName()
    {
        using var session = new RecordingSession();
        var service = new WireGuardTunnelService(session: session);

        service.Open("AoGPN-Test");

        service.IsOpen.Should().BeTrue();
        service.AdapterName.Should().Be("AoGPN-Test");
        var act = () => service.Open("ikinci");
        act.Should().Throw<InvalidOperationException>("zaten açıkken tekrar açılamaz");
    }

    [Fact]
    public async Task InjectPacketAsync_DataPathDown_ReturnsFalse()
    {
        // Veri yolu kapalı (oturum/soket yok) → gönderim başarısız, sayaç işler.
        var transport = new QueueTransport { DataPathActive = false };
        var service = new WireGuardTunnelService(transport, new RecordingSession());
        service.Open("AoGPN-Test");

        (await service.InjectPacketAsync(new byte[] { 1 }, TestContext.Current.CancellationToken)).Should().BeFalse();
        service.Snapshot().InjectFailed.Should().Be(1);
    }

    // ── Şifreleme dikişi + gönderim (giden yol) ──────────────────────────

    [Fact]
    public async Task InjectPacketAsync_EncryptsFirst_ThenSendsViaTransport()
    {
        using var session = new RecordingSession();
        var transport = new QueueTransport(0x77);
        var service = new WireGuardTunnelService(transport, session);
        service.Open("AoGPN-Test");

        var ok = await service.InjectPacketAsync(new byte[] { 1, 2, 3, 4 }, TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        transport.SentWire.Should().HaveCount(1);
        transport.SentWire[0].Should().Equal(new byte[] { 0x77, 1, 2, 3, 4 }, "taşıma önce şifreler (önek), köprü tip-4'ü UDP veri yolundan gönderir");
        session.Injected.Should().BeEmpty("giden yol Wintun'a değil, gerçek sunucuya gider (Faz 2d)");
        transport.Encrypted.Should().HaveCount(1);
        service.Snapshot().Encrypted.Should().Be(1);
        service.Snapshot().Sent.Should().Be(1);
        service.Snapshot().InjectFailed.Should().Be(0);
    }

    [Fact]
    public async Task InjectPacketAsync_TransportRejects_CountsAsFailed()
    {
        using var session = new RecordingSession();
        var transport = new RejectingTransport();
        var service = new WireGuardTunnelService(transport, session);
        service.Open("AoGPN-Test");

        (await service.InjectPacketAsync(new byte[] { 9 }, TestContext.Current.CancellationToken)).Should().BeFalse();
        transport.SentWire.Should().BeEmpty();
        service.Snapshot().InjectFailed.Should().Be(1);
    }

    // ── Yakalama köprüsü (DivertWorker kanalı) ───────────────────────────

    [Fact]
    public async Task Bridge_ConsumesChannel_SendsInOrder()
    {
        using var session = new RecordingSession();
        var transport = new QueueTransport();
        var service = new WireGuardTunnelService(transport, session);
        service.Open("AoGPN-Test");

        await service.RunCaptureBridgeAsync(
            Stream(new byte[] { 1 }, new byte[] { 2, 2 }, new byte[] { 3, 3, 3 }),
            TestContext.Current.CancellationToken);

        transport.SentWire.Select(p => p.Length).Should().Equal(new[] { 1, 2, 3 }, "kanal sırası korunur");
        service.Snapshot().Captured.Should().Be(3);
        service.Snapshot().Sent.Should().Be(3);
        service.Snapshot().InjectFailed.Should().Be(0);
    }

    [Fact]
    public async Task Bridge_TransportRejects_SkipsAndContinues()
    {
        using var session = new RecordingSession();
        var transport = new QueueTransport { SendPredicate = p => p.Length != 2 }; // 2 baytlık gönderim reddi
        var service = new WireGuardTunnelService(transport, session);
        service.Open("AoGPN-Test");

        await service.RunCaptureBridgeAsync(
            Stream(new byte[] { 1 }, new byte[] { 2, 2 }, new byte[] { 3 }),
            TestContext.Current.CancellationToken);

        transport.SentWire.Should().HaveCount(2, "reddedilen paket atlanır, döngü ölmez");
        service.Snapshot().Captured.Should().Be(3);
        service.Snapshot().Sent.Should().Be(2);
        service.Snapshot().InjectFailed.Should().Be(1);
    }

    [Fact]
    public async Task Bridge_Cancellation_StopsCleanly()
    {
        using var session = new RecordingSession();
        var service = new WireGuardTunnelService(session: session);
        service.Open("AoGPN-Test");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var bridge = service.RunCaptureBridgeAsync(EndlessStream(cts.Token), cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cts.Cancel();

        await bridge; // temiz biter — exception yok
        service.Snapshot().Captured.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── Alım döngüsü (sunucu → UDP → çöz → Wintun + tüketici) ────────────

    [Fact]
    public async Task ReceiveLoop_DecryptsDeliversToConsumer_AndInjectsIntoAdapter()
    {
        using var session = new RecordingSession();
        var transport = new QueueTransport(0x77);
        transport.ReceiveQueue.Enqueue(new byte[] { 0x77, 0xAA });
        transport.ReceiveQueue.Enqueue(new byte[] { 0x77, 0xBB, 0xBB });

        var service = new WireGuardTunnelService(transport, session);
        service.Open("AoGPN-Test");

        var delivered = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.RunReceiveLoopAsync(
            (packet, _) =>
            {
                delivered.Add(packet);
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(50),
            cts.Token);

        delivered.Select(p => p.Length).Should().Equal(new[] { 1, 2 }, "çözülen paketler tüketiciye sırayla gider");
        session.Injected.Select(p => p.Length).Should().Equal(new[] { 1, 2 }, "çözülen paketler Wintun adaptörüne enjekte edilir (OS → oyuna)");
        service.Snapshot().Received.Should().Be(2);
        service.Snapshot().Injected.Should().Be(2);
        service.Snapshot().DecryptFailed.Should().Be(0);
    }

    [Fact]
    public async Task ReceiveLoop_DecryptFailure_CountsAndContinues()
    {
        using var session = new RecordingSession();
        var transport = new QueueTransport(0x77) { FailDecryptFirst = true };
        transport.ReceiveQueue.Enqueue(new byte[] { 0x77, 0xAA }); // bozuk → decrypt başarısız
        transport.ReceiveQueue.Enqueue(new byte[] { 0x77, 0xBB }); // sağlam

        var service = new WireGuardTunnelService(transport, session);
        service.Open("AoGPN-Test");

        var delivered = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.RunReceiveLoopAsync(
            (packet, _) =>
            {
                delivered.Add(packet);
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(50),
            cts.Token);

        delivered.Should().HaveCount(1, "bozuk paket atlanır, sağlam devam eder");
        session.Injected.Should().HaveCount(1);
        service.Snapshot().Received.Should().Be(1, "yalnızca başarılı çözülenler sayılır");
        service.Snapshot().DecryptFailed.Should().Be(1);
    }

    [Fact]
    public async Task ReceiveLoop_EmptyPacket_Keepalive_NotInjected()
    {
        // Keepalive (boş içerikli tip-4): adaptöre enjekte edilmez, tüketiciye geçer.
        using var session = new RecordingSession();
        var transport = new QueueTransport(0x77);
        transport.ReceiveQueue.Enqueue(new byte[] { 0x77 }); // 1 bayt önek → çözülünce boş

        var service = new WireGuardTunnelService(transport, session);
        service.Open("AoGPN-Test");

        var delivered = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.RunReceiveLoopAsync(
            (packet, _) =>
            {
                delivered.Add(packet);
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(50),
            cts.Token);

        delivered.Should().HaveCount(1);
        delivered[0].Should().BeEmpty();
        session.Injected.Should().BeEmpty("keepalive adaptöre enjekte edilmez");
        service.Snapshot().Received.Should().Be(1);
        service.Snapshot().Injected.Should().Be(0);
    }

    // ── Temizlik ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_ClosesSession()
    {
        var session = new RecordingSession();
        var service = new WireGuardTunnelService(session: session);
        service.Open("AoGPN-Test");

        await service.DisposeAsync();

        session.Open.Should().BeFalse("Dispose session'ı kapatır");
        service.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_ThenOpen_ThrowsObjectDisposedException()
    {
        // Kalıcı kapanış: DisposeAsync sonrası örnek yeniden kullanılamaz — Open,
        // _disposed guard'ına takılır (ObjectDisposedException).
        var session = new RecordingSession();
        var service = new WireGuardTunnelService(session: session);
        service.Open("AoGPN-Test");
        service.IsOpen.Should().BeTrue();

        await service.DisposeAsync();
        service.IsOpen.Should().BeFalse();

        var act = () => service.Open("AoGPN-Test");
        act.Should().Throw<ObjectDisposedException>("Dispose sonrası Open kalıcı kapanış guard'ına takılır");
    }

    [Fact]
    public void Close_DoesNotDispose_OpenRemainsUsable()
    {
        // Close yalnızca akışı durdurur, örneği kalıcı dispose ETMEZ: guard
        // (_disposed) yalnızca DisposeAsync'te set edilir. GpnCaptureBridge sunucu
        // değişimi/failover'da Close → Open döngüsünü bu davranışa borçludur.
        var session = new RecordingSession();
        var service = new WireGuardTunnelService(session: session);
        service.Open("AoGPN-Test");
        service.IsOpen.Should().BeTrue();

        service.Close();
        service.IsOpen.Should().BeFalse();

        // Close sonrası Open, Dispose guard'ına takılmaz — guard aşılıp adapter
        // kurulumuna geçilir (AdapterName güncellenir), tünel yeniden açılabilir.
        var act = () => service.Open("AoGPN-Test-2");
        act.Should().NotThrow("Close, _disposed'ı set etmemeli — yeniden açılabilir kalır");
        service.AdapterName.Should().Be("AoGPN-Test-2", "Open guard'ı aşılıp adapter kurulumuna geçilir");
    }

    // ── Test donanımı ────────────────────────────────────────────────────

    private static async IAsyncEnumerable<DivertedPacket> Stream(params byte[][] packets)
    {
        foreach (var p in packets)
        {
            yield return new DivertedPacket(p, default, DateTimeOffset.UtcNow);
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<DivertedPacket> EndlessStream([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            yield return new DivertedPacket(new byte[] { 1 }, default, DateTimeOffset.UtcNow);
            await Task.Yield(); // await noktası olmadan tüketici iş parçacığını asla bırakmaz
        }
    }

    private sealed class RecordingSession : IWintunSession
    {
        public List<byte[]> Injected { get; } = new();
        public Queue<byte[]> ReceiveQueue { get; } = new();
        public Func<byte[], bool>? InjectPredicate { get; set; }
        public bool Open { get; set; } = true;

        public bool InjectPacket(ReadOnlySpan<byte> packet)
        {
            if (!Open)
            {
                return false;
            }
            if (InjectPredicate is not null && !InjectPredicate(packet.ToArray()))
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
    /// Faz 2d fake taşıması: Encrypt önek koyar (şifreleme kanıtı), SendAsync'i\n
    /// kaydeder, RunReceiveLoopAsync'i kuyruktan tüketiciye akıtır. Veri yolu\n
    /// kapalı (DataPathActive=false) veya SendPredicate reddi simüle edilebilir.
    /// </summary>
    private sealed class QueueTransport : IWireGuardTransport
    {
        private readonly byte _prefix;
        private bool _failedDecryptOnce;

        public QueueTransport(byte prefix = 0) => _prefix = prefix;

        public List<byte[]> SentWire { get; } = new();
        public Queue<byte[]> ReceiveQueue { get; } = new();
        public Func<byte[], bool>? SendPredicate { get; set; }
        public bool DataPathActive { get; set; } = true;
        public bool FailDecryptFirst { get; set; }
        public List<byte[]> Encrypted { get; } = new();
        public List<byte[]> Decrypted { get; } = new();

        public long EncryptedCount => Encrypted.Count;
        public long DecryptedCount => Decrypted.Count;
        public long DecryptFailedCount => FailDecryptFirst && _failedDecryptOnce ? 1 : 0;
        public long SentCount => SentWire.Count;
        public long SendFailedCount => 0;
        public bool IsDataPathActive => DataPathActive;

        public byte[]? Encrypt(ReadOnlySpan<byte> packet)
        {
            if (!DataPathActive)
            {
                return null;
            }
            var result = _prefix == 0
                ? packet.ToArray()
                : Prefixed(packet);
            Encrypted.Add(result);
            return result;
        }

        public byte[]? Decrypt(ReadOnlySpan<byte> packet)
        {
            if (FailDecryptFirst && !_failedDecryptOnce)
            {
                _failedDecryptOnce = true;
                return null;
            }
            var result = _prefix == 0
                ? packet.ToArray()
                : packet[1..].ToArray();
            Decrypted.Add(result);
            return result;
        }

        public ValueTask<bool> SendAsync(byte[] wirePacket, CancellationToken cancellationToken = default)
        {
            if (!DataPathActive || (SendPredicate is not null && !SendPredicate(wirePacket)))
            {
                return ValueTask.FromResult(false);
            }
            SentWire.Add(wirePacket);
            return ValueTask.FromResult(true);
        }

        public async Task RunReceiveLoopAsync(
            Func<byte[], CancellationToken, ValueTask> consumer,
            TimeSpan idleTimeout,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && ReceiveQueue.Count > 0)
            {
                var wire = ReceiveQueue.Dequeue();
                var decrypted = Decrypt(wire);
                if (decrypted is null)
                {
                    continue;
                }
                await consumer(decrypted, cancellationToken).ConfigureAwait(false);
            }
        }

        private byte[] Prefixed(ReadOnlySpan<byte> packet)
        {
            var result = new byte[packet.Length + 1];
            result[0] = _prefix;
            packet.CopyTo(result.AsSpan(1));
            return result;
        }
    }

    /// <summary>Encrypt her zaman başarısız — şifreleme hatası yolunu doğrular.</summary>
    // ── Native yaşam döngüsü (seam'li — sürücüsüz) ──────────────────────

    [Fact]
    public async Task Close_RealNativePath_ClosesAdapterExactlyOnce()
    {
        // Regresyon (saha çökmesi 0xc0000374 — ntdll heap corruption):
        // WintunSession.Dispose adaptörü de WintunCloseAdapter ile kapatıyordu;
        // WireGuardTunnelService.Close aynı adaptörü İKİNCİ kez kapatıyordu →
        // çift-free. Adaptör sahipliği YALNIZCA servistedir; Dispose yalnızca
        // session'ı bitirir (WintunEndSession).
        var closes = 0;
        var ends = 0;
        await using var service = new WireGuardTunnelService(
            createAdapter: (_, _, _) => new IntPtr(0x1000),
            closeAdapter: _ => closes++,
            endSession: _ => ends++,
            getAdapterLuid: _ => 12345,
            startSession: (_, _) => new IntPtr(0x2000));

        service.Open("AoGPN-Test");
        service.IsOpen.Should().BeTrue();

        service.Close();

        closes.Should().Be(1, "adaptörün tek sahibi servistir — tek WintunCloseAdapter (çift-free yok)");
        ends.Should().Be(1, "session Dispose'ta bir kez WintunEndSession ile sonlandırılır");
        service.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Open_StartSessionFails_ClosesAdapterOnce_AndLaterCloseIsNoOp()
    {
        // Açılış hatası yolunda adaptör bir kez kapatılır ve _adapter sıfırlanır;
        // ardından Close çağrısı ek kapatma YAPMAMALIDIR (aynı çift-free regresyonu).
        var closes = 0;
        await using var service = new WireGuardTunnelService(
            createAdapter: (_, _, _) => new IntPtr(0x1000),
            getAdapterLuid: _ => 12345,
            startSession: (_, _) => IntPtr.Zero,
            closeAdapter: _ => closes++);

        service.Invoking(s => s.Open("AoGPN-Test")).Should().Throw<WintunException>();

        closes.Should().Be(1, "başarısız Open adaptörü bir kez temizler");
        service.Close();
        closes.Should().Be(1, "hata sonrası Close ek kapatma yapmaz");
        service.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_RealNativePath_ClosesAdapterExactlyOnce()
    {
        // DisposeAsync → Close aynı tek-kapatma sözleşmesini korur (smoke testleri
        // gerçek sürücüyle adaptör oluşturup Dispose eder — bu sözleşme onları da korur).
        var closes = 0;
        var ends = 0;
        var service = new WireGuardTunnelService(
            createAdapter: (_, _, _) => new IntPtr(0x1000),
            closeAdapter: _ => closes++,
            endSession: _ => ends++,
            getAdapterLuid: _ => 12345,
            startSession: (_, _) => new IntPtr(0x2000));

        service.Open("AoGPN-Test");
        await service.DisposeAsync();

        closes.Should().Be(1);
        ends.Should().Be(1);
    }

    private sealed class RejectingTransport : IWireGuardTransport
    {
        public List<byte[]> SentWire { get; } = new();
        public byte[]? Encrypt(ReadOnlySpan<byte> packet) => null;
        public byte[]? Decrypt(ReadOnlySpan<byte> packet) => packet.ToArray();
        public long EncryptedCount => 0;
        public long DecryptedCount => 0;
        public long DecryptFailedCount => 0;
        public long SentCount => SentWire.Count;
        public long SendFailedCount => 0;
        public bool IsDataPathActive => false;

        public ValueTask<bool> SendAsync(byte[] wirePacket, CancellationToken cancellationToken = default)
        {
            SentWire.Add(wirePacket);
            return ValueTask.FromResult(false);
        }

        public Task RunReceiveLoopAsync(
            Func<byte[], CancellationToken, ValueTask> consumer,
            TimeSpan idleTimeout,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
