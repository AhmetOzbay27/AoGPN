using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Faz 2c — gerçek WireGuard veri düzlemi testleri:
///
///  1. Loopback UDP üzerinde iki taraflı (initiator ↔ responder) el sıkışma —
///     ConnectAsync gerçek bir karşı tarafla oturumu kurar.
///  2. Oturum anahtarı eşleşmesi — initiator send == responder recv (ve tersi).
///  3. Transport round-trip — tip-4 mesajlar gerçek wire format'la gider/gelir.
///  4. Tamper + replay — bozuk/tekrarlanan mesajlar reddedilir, sayaç işler.
///  5. Cookie retry — yük altındaki sunucu (cookie reply) → MAC2'li yeniden
///     gönderim → oturum kurulur.
///
/// Hiçbiri İtalya/Almanya sunucularına dokunmaz.
/// </summary>
public class WireGuardNoiseTransportTests
{
    // ── 1. Loopback UDP iki taraflı el sıkışma ────────────────────────────

    [Fact]
    public async Task ConnectAsync_GenuineResponderOnLoopback_EstablishesSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        await using var host = ResponderHost.Start(serverPrivate, serverIndex: 0x1111u);
        var port = host.Port;

        using var transport = new WireGuardNoiseTransport(clientPrivate, serverPublic);
        var ok = await transport.ConnectAsync("127.0.0.1", port, ct);

        ok.Should().BeTrue();
        transport.SessionEstablished.Should().BeTrue();
        transport.HandshakeAttempts.Should().Be(1);
        transport.IsDataPathActive.Should().BeTrue("Faz 2d: el sıkışma sonrası UDP soketi canlı kalır");
    }

    [Fact]
    public async Task ConnectAsync_SilentListener_ReturnsFalse_NoSession()
    {
        // Canlıda gözlenen handshake-no-response senaryosu: geçerli initiation
        // gider, yanıt yok → bağlantı kurulamaz, oturum yok (Encrypt null döner).
        var ct = TestContext.Current.CancellationToken;
        var serverPublic = WireGuardNoise.PublicKey(RandomBytes(32));
        var clientPrivate = RandomBytes(32);

        await using var silent = ResponderHost.StartSilent();
        var port = silent.Port;

        using var transport = new WireGuardNoiseTransport(clientPrivate, serverPublic);
        var ok = await transport.ConnectAsync("127.0.0.1", port, ct);

        ok.Should().BeFalse();
        transport.SessionEstablished.Should().BeFalse();
        transport.IsDataPathActive.Should().BeFalse();
        transport.Encrypt(new byte[] { 0x45, 0, 0, 20 }).Should().BeNull();
    }

    // ── 2. Oturum anahtarı eşleşmesi (saf kripto) ─────────────────────────

    [Fact]
    public void FullHandshake_SessionKeys_CrossDecryptWorks()
    {
        var clientPrivate = RandomBytes(32);
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var client = new WireGuardHandshakeClient(clientPrivate, serverPublic);

        var initiation = client.CreateInitiation();
        var (response, responderSession, failure) = ResponderCore.Respond(initiation, serverPrivate, 0x2222u);
        response.Should().NotBeNull(failure);
        responderSession.Should().NotBeNull(failure);

        var clientSession = client.ConsumeResponse(response!);
        clientSession.Should().NotBeNull();

        var ipPacket = BuildTestIpPacket();

        // İnitiator gönderir → responder çözer (send_i == recv_r).
        var wire = clientSession!.Encrypt(ipPacket);
        wire.Should().NotBeNull();
        var roundTrip = responderSession!.Decrypt(wire!);
        roundTrip.Should().NotBeNull();
        roundTrip!.AsSpan().SequenceEqual(ipPacket).Should().BeTrue();

        // Responder gönderir → initiator çözer (send_r == recv_i).
        var back = responderSession.Encrypt(ipPacket);
        back.Should().NotBeNull();
        var decrypted = clientSession.Decrypt(back!);
        decrypted.Should().NotBeNull();
        decrypted!.AsSpan().SequenceEqual(ipPacket).Should().BeTrue();
    }

    // ── Tier 4 — önbellekli AEAD + nonce yeniden kullanımı ──────────────

    [Fact]
    public void Session_CachedAeads_ManyVariedSizePackets_RoundTrip()
    {
        // ChaCha20Poly1305 örneği artık yön başına BİR KEZ kurulur (paket başına
        // natif anahtar tutamacı yok) ve nonce tamponları yeniden kullanılır;
        // şifreleme yerinde (in-place) yapılır — ara padding kopyası yok. Bu test
        // aynı oturumda 16 hizası altı/üstü + sınır boyutlarında çok sayıda
        // paketin şifrelenip çözülmesiyle önbellekli AEAD + counter ilerlemesi +
        // padding trim'inin doğruluğunu kilitler (0xFFFF WG üst sınırı).
        var clientPrivate = RandomBytes(32);
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var client = new WireGuardHandshakeClient(clientPrivate, serverPublic);
        var (response, responderSession, failure) = ResponderCore.Respond(client.CreateInitiation(), serverPrivate, 0x2222u);
        response.Should().NotBeNull(failure);
        var clientSession = client.ConsumeResponse(response!);
        clientSession.Should().NotBeNull();

        var sizes = new[] { 20, 28, 47, 64, 255, 1500, 0xFFFF };
        foreach (var size in sizes)
        {
            var payload = RandomBytes(size);
            payload[0] = 0x45;                                   // IPv4 başlığı
            payload[2] = (byte)((size >> 8) & 0xFF);             // toplam uzunluk →
            payload[3] = (byte)(size & 0xFF);                    // (checked ctx — maske şart)
            // not: 0xFFFF boyutunda toplam uzunluk alanı 16-bit'e sığmaz; bu sınır
            // boyutta trim yine de güvenlidir (65535 <= padded 65536)

            var wire = clientSession!.Encrypt(payload);
            wire.Should().NotBeNull($"{size} bayt paket şifrelenebilmeli");
            var roundTrip = responderSession!.Decrypt(wire!);
            roundTrip.Should().NotBeNull($"{size} bayt paket çözülebilmeli");
            roundTrip!.AsSpan().SequenceEqual(payload).Should().BeTrue();
        }
    }

    [Fact]
    public void Session_EncryptBeyondCounter255_NoOverflow_RoundTrip()
    {
        // WriteUint64 checked-context kusuru: counter >= 256 iken (byte) daraltma
        // cast'i OverflowException fırlatıyordu (CheckForOverflowUnderflow=true,
        // Directory.Build.props). Canlı bir oyun oturumu paket #257'de çökerdi;
        // mevcut testler hiçbir oturumda 256 paketi geçmediği için yakalanamamıştı.
        // BenchmarkDotNet ölçümü (200k paket/iterasyon) bu kusuru ortaya çıkardı.
        var clientPrivate = RandomBytes(32);
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var client = new WireGuardHandshakeClient(clientPrivate, serverPublic);
        var (response, responderSession, failure) = ResponderCore.Respond(client.CreateInitiation(), serverPrivate, 0x2222u);
        response.Should().NotBeNull(failure);
        var clientSession = client.ConsumeResponse(response!);
        clientSession.Should().NotBeNull();

        var payload = BuildTestIpPacket(); // 28 bayt IPv4
        byte[]? last = null;
        for (var i = 0; i < 300; i++) // counter 0..299 — 255 sınırının ötesinde
        {
            last = clientSession!.Encrypt(payload);
            last.Should().NotBeNull();
        }
        // Son paket (counter 299) hâlâ doğru çözülmeli.
        var roundTrip = responderSession!.Decrypt(last!);
        roundTrip.Should().NotBeNull();
        roundTrip!.AsSpan().SequenceEqual(payload).Should().BeTrue();
    }

    // ── 3. Transport round-trip (loopback UDP, wire format) ───────────────

    [Fact]
    public async Task TransportMessages_RoundTripOverLoopbackUdp()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        await using var host = ResponderHost.Start(serverPrivate, serverIndex: 0x3333u);
        var port = host.Port;

        using var transport = new WireGuardNoiseTransport(clientPrivate, serverPublic);
        (await transport.ConnectAsync("127.0.0.1", port, ct)).Should().BeTrue();

        var ipPacket = BuildTestIpPacket();
        var wire = transport.Encrypt(ipPacket);
        wire.Should().NotBeNull();

        // Wire format doğrulaması: tip 4 + receiver + counter 0.
        WireGuardNoise.ReadUint32(wire!, 0).Should().Be(WireGuardNoise.MessageTypeTransport);
        WireGuardNoise.ReadUint32(wire!, 4).Should().Be(host.ServerIndex);
        WireGuardNoise.ReadUint64(wire!, 8).Should().Be(0);

        // Sunucuya gönder → sunucu çözer + kendi anahtarıyla şifreleyip yankılar.
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(IPAddress.Loopback, port);
        await udp.SendAsync(wire!, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var echo = await udp.ReceiveAsync(timeout.Token);
        var decrypted = transport.Decrypt(echo.Buffer);
        decrypted.Should().NotBeNull();
        decrypted!.AsSpan().SequenceEqual(ipPacket).Should().BeTrue();

        // Sayaçlar: 1 şifreleme (giden) + 1 çözme (gelen yankı).
        transport.EncryptedCount.Should().Be(1);
        transport.DecryptedCount.Should().Be(1);
    }

    // ── 4. Tamper + replay ────────────────────────────────────────────────

    [Fact]
    public void Decrypt_TamperedMessage_ReturnsNullAndCountsFailure()
    {
        var (client, responderSession) = EstablishedPair();
        var wire = client.Encrypt(BuildTestIpPacket())!;

        var tampered = (byte[])wire.Clone();
        tampered[^1] ^= 0x01; // tag'ın son baytını boz

        responderSession.Decrypt(tampered).Should().BeNull();
        // Orijinal hâlâ geçerli (bozma paketi değil oturumu etkilemedi).
        responderSession.Decrypt(wire).Should().NotBeNull();
    }

    [Fact]
    public void Decrypt_ReplayedMessage_SecondAttemptRejected()
    {
        var (client, responderSession) = EstablishedPair();
        var wire = client.Encrypt(BuildTestIpPacket())!;

        responderSession.Decrypt(wire).Should().NotBeNull();
        responderSession.Decrypt(wire).Should().BeNull(); // replay filtresi
        responderSession.Decrypt(wire).Should().BeNull();
    }

    [Fact]
    public void Decrypt_WrongTypeOrReceiver_Rejected()
    {
        var (client, responderSession) = EstablishedPair();
        var wire = client.Encrypt(BuildTestIpPacket())!;

        var wrongType = (byte[])wire.Clone();
        WireGuardNoise.WriteUint32(wrongType, 0, 99);
        responderSession.Decrypt(wrongType).Should().BeNull();

        var wrongReceiver = (byte[])wire.Clone();
        WireGuardNoise.WriteUint32(wrongReceiver, 4, 0xDEADBEEF);
        responderSession.Decrypt(wrongReceiver).Should().BeNull();
    }

    // ── 5. Cookie retry (yük altındaki sunucu) ────────────────────────────

    [Fact]
    public void ConsumeCookieReply_ThenInitiationCarriesMac2()
    {
        var clientPrivate = RandomBytes(32);
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var client = new WireGuardHandshakeClient(clientPrivate, serverPublic);

        var initiation1 = client.CreateInitiation();
        client.HasFreshCookie.Should().BeFalse();
        // MAC2 (132..148) sıfır — çerez yok.
        initiation1.Skip(132).Take(16).Should().OnlyContain(b => b == 0);

        var cookieReply = ResponderCore.BuildCookieReply(initiation1, serverPublic);
        client.ConsumeCookieReply(cookieReply).Should().BeTrue();
        client.HasFreshCookie.Should().BeTrue();

        var initiation2 = client.CreateInitiation();
        // Yeni initiation MAC2 taşır (cookie anahtarıyla imzalı).
        initiation2.Skip(132).Take(16).Should().Contain(b => b != 0);
    }

    [Fact]
    public async Task ConnectAsync_CookieFirstResponder_RetriesWithMac2_EstablishesSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        // Host: ilk initiation'a yanıt yerine cookie reply gönderir; MAC2'li
        // yeniden gönderimi kabul edip el sıkışmayı tamamlar.
        await using var host = ResponderHost.Start(serverPrivate, serverIndex: 0x4444u, cookieFirst: true);
        var port = host.Port;

        using var transport = new WireGuardNoiseTransport(clientPrivate, serverPublic);
        var ok = await transport.ConnectAsync("127.0.0.1", port, ct);

        ok.Should().BeTrue();
        transport.SessionEstablished.Should().BeTrue();
        transport.HandshakeAttempts.Should().Be(2); // 1. cookie, 2. MAC2'li
    }

    // ── 6. Faz 2d: kalıcı UDP veri yolu ──────────────────────────────────

    [Fact]
    public async Task DataPath_SendAsyncAndReceiveLoop_RoundTripOverLoopbackUdp()
    {
        // SendAsync (tip-4 → sunucu) + RunReceiveLoopAsync (sunucu yankısı → çöz →
        // consumer) — kalıcı veri yolu uçtan uca (gerçek UDP yığını).
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        await using var host = ResponderHost.Start(serverPrivate, serverIndex: 0x6666u);
        var port = host.Port;

        using var transport = new WireGuardNoiseTransport(clientPrivate, serverPublic);
        (await transport.ConnectAsync("127.0.0.1", port, ct)).Should().BeTrue();

        var ipPacket = BuildTestIpPacket();
        var wire = transport.Encrypt(ipPacket);
        wire.Should().NotBeNull();

        var delivered = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var loop = transport.RunReceiveLoopAsync(
            (packet, _) =>
            {
                delivered.Add(packet);
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(100),
            cts.Token);

        (await transport.SendAsync(wire!, TestContext.Current.CancellationToken)).Should().BeTrue("tip-4 sunucuya gönderilir");

        await WaitUntilAsync(() => delivered.Count == 1, TimeSpan.FromSeconds(3));
        delivered.Should().HaveCount(1);
        delivered[0].AsSpan().SequenceEqual(ipPacket).Should().BeTrue("sunucu yankısı çözülüp tüketiciye ulaşır");

        transport.SentCount.Should().Be(1);
        transport.DecryptedCount.Should().Be(1);
        transport.SendFailedCount.Should().Be(0);

        cts.Cancel();
        await loop; // temiz biter
    }

    [Fact]
    public async Task DataPath_NoSession_SendAsyncReturnsFalse()
    {
        // El sıkışma yapılmadan veri yolu yok — gönderim reddedilir ve sayaçlanır.
        var serverPublic = WireGuardNoise.PublicKey(RandomBytes(32));
        using var transport = new WireGuardNoiseTransport(RandomBytes(32), serverPublic);

        (await transport.SendAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken)).Should().BeFalse();
        transport.SendFailedCount.Should().Be(1);
        transport.IsDataPathActive.Should().BeFalse();
    }

    [Fact]
    public async Task DataPath_ReceiveLoop_WaitsForHandshake_ThenConsumes()
    {
        // Köprü akışı: alım döngüsü el sıkışmadan ÖNCE başlar, oturum gelince canlıya
        // geçer (oturum yokken Task.Delay ile bekler, hata vermez).
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        await using var host = ResponderHost.Start(serverPrivate, serverIndex: 0x7777u);
        var port = host.Port;

        using var transport = new WireGuardNoiseTransport(clientPrivate, serverPublic);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var delivered = new List<byte[]>();
        var loop = transport.RunReceiveLoopAsync(
            (packet, _) =>
            {
                delivered.Add(packet);
                return ValueTask.CompletedTask;
            },
            TimeSpan.FromMilliseconds(100),
            cts.Token);

        // Oturum yokken döngü beklemede — sonra el sıkışma + veri akışı.
        (await transport.ConnectAsync("127.0.0.1", port, ct)).Should().BeTrue();
        var wire = transport.Encrypt(BuildTestIpPacket());
        (await transport.SendAsync(wire!, ct)).Should().BeTrue();

        await WaitUntilAsync(() => delivered.Count == 1, TimeSpan.FromSeconds(3));
        delivered.Should().HaveCount(1);

        cts.Cancel();
        await loop;
    }

    // ── 7. Kapanış sertleştirmesi (rotа değişimi çökme regresyonu) ──────
    //
    // Canlıda görülen çökme: rota değişimi → çekirdek reload → köprü kapanışı →
    // bekleyen UdpClient.ReceiveAsync istisnayla ayrılır → AppDomain'e kaçarsa
    // uygulama anında kapanır (CurrentDomain_UnhandledException). Bu üç test,
    // alıcı döngüsünün HİÇBİR kapanış senaryosunda istisna kaçırmadığını sabitler.

    [Fact]
    public async Task ReceiveLoop_CancelledTokenBeforeStart_CompletesWithoutThrowing()
    {
        var serverPublic = WireGuardNoise.PublicKey(RandomBytes(32));
        using var transport = new WireGuardNoiseTransport(RandomBytes(32), serverPublic);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var loop = transport.RunReceiveLoopAsync(
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromMilliseconds(100),
            cts.Token);

        // İptal önceden verilmiş — döngü hata fırlatamaz, temiz biter.
        await loop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReceiveLoop_DisposeRaceDuringReceive_CompletesWithoutThrowing()
    {
        // Canlı çökme senaryosunun birebir provası: oturum kurulu, döngü bekleyen
        // ReceiveAsync içinde iken soket arkasından dispose edilir (kapanış yarışı).
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        await using var host = ResponderHost.Start(serverPrivate, serverIndex: 0x8888u);
        using var transport = new WireGuardNoiseTransport(clientPrivate, serverPublic);
        (await transport.ConnectAsync("127.0.0.1", host.Port, ct)).Should().BeTrue();

        using var loopCts = new CancellationTokenSource();
        var loop = transport.RunReceiveLoopAsync(
            (_, _) => ValueTask.CompletedTask,
            TimeSpan.FromMilliseconds(100),
            loopCts.Token);

        // Döngünün ReceiveAsync'e girmesine fırsat ver, SONRA soketi arkadan kapat.
        await Task.Delay(150, ct);
        transport.Dispose(); // döngünün elindeki UDP soketini kapat — yarışı üret
        loopCts.Cancel();

        // Hangi istisna fırlarsa fırlasın görev FAULT olmamalı (çökme = regression).
        await loop.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task ReceiveLoop_ConsumerThrowsCancellation_CompletesWithoutThrowing()
    {
        // Tüketici (telemetri/enjeksiyon hattı) kapanışta OCE fırlatabilir — döngü
        // bunu dışarı kaçırmaz, temiz biter (öncesinde AppDomain'e kaçıyordu).
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        await using var host = ResponderHost.Start(serverPrivate, serverIndex: 0x9999u);
        using var transport = new WireGuardNoiseTransport(clientPrivate, serverPublic);
        (await transport.ConnectAsync("127.0.0.1", host.Port, ct)).Should().BeTrue();

        using var loopCts = new CancellationTokenSource();
        var loop = transport.RunReceiveLoopAsync(
            (_, _) => throw new OperationCanceledException(), // token iptal DEĞİL — yarış simülasyonu
            TimeSpan.FromMilliseconds(100),
            loopCts.Token);

        // Bir tip-4 mesaj gönder — sunucu yankılar, çözülür ve tüketici patlar.
        var wire = transport.Encrypt(BuildTestIpPacket());
        wire.Should().NotBeNull();
        (await transport.SendAsync(wire!, ct)).Should().BeTrue();

        await loop.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────

    private static (WireGuardSession Client, WireGuardSession Responder) EstablishedPair()
    {
        var clientPrivate = RandomBytes(32);
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var client = new WireGuardHandshakeClient(clientPrivate, serverPublic);

        var initiation = client.CreateInitiation();
        var (response, responderSession, failure) = ResponderCore.Respond(initiation, serverPrivate, 0x5555u);
        if (response is null || responderSession is null)
        {
            throw new InvalidOperationException($"Responder el sıkışmayı reddetti: {failure}");
        }
        var clientSession = client.ConsumeResponse(response);
        if (clientSession is null)
        {
            throw new InvalidOperationException("İstemci yanıtı tüketemedi.");
        }
        return (clientSession, responderSession);
    }

    /// <summary>Geçerli IPv4 başlıklı küçük bir UDP paketi (padding kırpması için uzunluk alanı dolu).</summary>
    internal static byte[] BuildTestIpPacket()
    {
        // 20 bayt IPv4 başlığı (toplam uzunluk = 28) + 8 bayt UDP yükü.
        var packet = new byte[28];
        packet[0] = 0x45;              // v4, IHL=5
        packet[2] = 0x00; packet[3] = 0x1C; // toplam uzunluk 28
        packet[8] = 64;                // TTL
        packet[9] = 17;                // UDP
        packet[12] = 10; packet[13] = 0; packet[14] = 0; packet[15] = 2;  // 10.0.0.2
        packet[16] = 10; packet[17] = 0; packet[18] = 0; packet[19] = 1;  // 10.0.0.1
        packet[20] = 0x12; packet[21] = 0x34; packet[22] = 0x56; packet[23] = 0x78;
        packet[24] = 0; packet[25] = 8; packet[26] = 0; packet[27] = 0;
        return packet;
    }

    private static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                return false;
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        return true;
    }
}

/// <summary>
/// wireguard-go ConsumeMessageInitiation + CreateMessageResponse + BeginSymmetricSession
/// portu — istemci veri düzlemini bağımsız bir karşı tarafla round-trip etmek için.
/// Gerçek istemciyle AYNI primitifleri (WireGuardNoise) kullanır; amaç savunma
/// tarafının state machine'ini + oturum anahtarlarını doğrulamaktır.
/// </summary>
internal static class ResponderCore
{
    internal static (byte[]? Response, WireGuardSession? Session, string? Failure) Respond(
        byte[] initiation, byte[] serverPrivate, uint serverIndex)
    {
        if (initiation.Length != WireGuardNoise.InitiationSize)
        {
            return (null, null, "boyut");
        }

        var hash = (byte[])WireGuardNoise.InitialHash.Clone();
        var chainKey = (byte[])WireGuardNoise.InitialChainKey.Clone();
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);

        hash = WireGuardNoise.MixHash(hash, serverPublic);
        var ephemeral = initiation[8..40];
        hash = WireGuardNoise.MixHash(hash, ephemeral);
        chainKey = WireGuardNoise.Kdf1(chainKey, ephemeral);

        if (!WireGuardNoise.SharedSecret(serverPrivate, ephemeral, out var ss1))
        {
            return (null, null, "ss1 sıfır");
        }
        var (ck1, key1) = WireGuardNoise.Kdf2(chainKey, ss1);
        chainKey = ck1;
        if (!WireGuardNoise.AeadOpen(key1, initiation[40..88], hash, out var clientStatic))
        {
            return (null, null, "static AAD");
        }
        hash = WireGuardNoise.MixHash(hash, initiation[40..88]);

        if (!WireGuardNoise.SharedSecret(serverPrivate, clientStatic, out var ss2))
        {
            return (null, null, "ss2 sıfır");
        }
        var (ck2, key2) = WireGuardNoise.Kdf2(chainKey, ss2);
        chainKey = ck2;
        if (!WireGuardNoise.AeadOpen(key2, initiation[88..116], hash, out _))
        {
            return (null, null, "timestamp AAD");
        }
        hash = WireGuardNoise.MixHash(hash, initiation[88..116]);

        // ── create response ──
        var clientSender = WireGuardNoise.ReadUint32(initiation, 4);
        var e2Private = RandomNumberGenerator.GetBytes(32);
        var e2 = WireGuardNoise.PublicKey(e2Private);
        hash = WireGuardNoise.MixHash(hash, e2);
        chainKey = WireGuardNoise.Kdf1(chainKey, e2);

        if (WireGuardNoise.SharedSecret(e2Private, ephemeral, out var ssA))
        {
            chainKey = WireGuardNoise.Kdf1(chainKey, ssA);
        }
        if (WireGuardNoise.SharedSecret(e2Private, clientStatic, out var ssB))
        {
            chainKey = WireGuardNoise.Kdf1(chainKey, ssB);
        }

        var psk = new byte[32];
        var (ck3, tau, key3) = WireGuardNoise.Kdf3(chainKey, psk);
        chainKey = ck3; // oturum anahtarları için gerekli
        hash = WireGuardNoise.MixHash(hash, tau);
        var empty = WireGuardNoise.AeadSeal(key3, Array.Empty<byte>(), hash);
        hash = WireGuardNoise.MixHash(hash, empty);

        var resp = new byte[WireGuardNoise.ResponseSize];
        WireGuardNoise.WriteUint32(resp, 0, WireGuardNoise.MessageTypeResponse);
        WireGuardNoise.WriteUint32(resp, 4, serverIndex);
        WireGuardNoise.WriteUint32(resp, 8, clientSender);
        Buffer.BlockCopy(e2, 0, resp, 12, 32);
        Buffer.BlockCopy(empty, 0, resp, 44, empty.Length);
        var mac1 = WireGuardNoise.Blake2sKeyed(WireGuardNoise.Mac1Key(clientStatic), resp[..60], 16);
        Buffer.BlockCopy(mac1, 0, resp, 60, 16);

        // BeginSymmetricSession (responder): initiator'un tersi — send=key1, recv=key0.
        // (wireguard-go: initiator send=key0/recv=key1; responder send=key1/recv=key0.)
        var (responderSend, responderRecv) = WireGuardNoise.Kdf2(chainKey, Array.Empty<byte>());
        var session = new WireGuardSession(responderRecv, responderSend, serverIndex, clientSender, isInitiator: false);
        return (resp, session, null);
    }

    /// <summary>
    /// Cookie reply (64 bayt) üretir: XChaCha20-Poly1305(cookieKey, nonce, cookie,
    /// AAD=initiation'ın MAC1'i). wireguard-go CookieGenerator portu.
    /// </summary>
    internal static byte[] BuildCookieReply(byte[] initiation, byte[] serverPublic)
    {
        var clientSender = WireGuardNoise.ReadUint32(initiation, 4);
        var mac1 = initiation[116..132];
        var nonce = RandomNumberGenerator.GetBytes(24);
        var cookie = RandomNumberGenerator.GetBytes(16);

        var cookieCt = WireGuardNoise.XAeadSeal(
            WireGuardNoise.CookieKey(serverPublic), nonce, cookie, mac1);

        var reply = new byte[WireGuardNoise.CookieReplySize];
        WireGuardNoise.WriteUint32(reply, 0, WireGuardNoise.MessageTypeCookieReply);
        WireGuardNoise.WriteUint32(reply, 4, clientSender);
        Buffer.BlockCopy(nonce, 0, reply, 8, 24);
        Buffer.BlockCopy(cookieCt, 0, reply, 32, cookieCt.Length);
        return reply;
    }
}

/// <summary>
/// Loopback UDP üzerinde çalışan gerçek WireGuard sunucu sahtesi:
/// initiation → (cookieFirst ise cookie reply, değilse response + oturum);
/// tip-4 mesaj → oturumla çöz + yankıla. Tek oturumlu (testler sıralı).
/// </summary>
internal sealed class ResponderHost : IAsyncDisposable
{
    private readonly byte[] _serverPrivate;
    private readonly byte[] _serverPublic;
    private readonly bool _cookieFirst;
    private readonly bool _silent;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private WireGuardSession? _session;
    private uint _clientSender;
    private bool _cookieSent;

    public UdpClient Client { get; }
    public uint ServerIndex { get; }

    /// <summary>Bağlı loopback portu (istemci tarafından hedef).</summary>
    public int Port => ((IPEndPoint)Client.Client.LocalEndPoint).Port;

    private ResponderHost(byte[] serverPrivate, uint serverIndex, bool cookieFirst, bool silent = false)
    {
        _serverPrivate = serverPrivate;
        _serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        ServerIndex = serverIndex;
        _cookieFirst = cookieFirst;
        _silent = silent;
        Client = new UdpClient(AddressFamily.InterNetwork);
        Client.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _loop = Task.Run(LoopAsync);
    }

    public static ResponderHost Start(byte[] serverPrivate, uint serverIndex, bool cookieFirst = false)
        => new(serverPrivate, serverIndex, cookieFirst);

    /// <summary>Yanıt vermeyen sessiz sunucu (handshake-no-response senaryosu).</summary>
    public static ResponderHost StartSilent()
        => new(RandomNumberGenerator.GetBytes(32), 1u, false, silent: true);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await Client.ReceiveAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            if (_silent)
            {
                continue; // al, yanıt verme — handshake-no-response senaryosu
            }

            var type = WireGuardNoise.PeekMessageType(datagram.Buffer);
            if (type == WireGuardNoise.MessageTypeInitiation)
            {
                _clientSender = WireGuardNoise.ReadUint32(datagram.Buffer, 4);

                if (_cookieFirst && !_cookieSent)
                {
                    // İlk initiation'a yanıt yerine cookie — istemci MAC2 ile döner.
                    _cookieSent = true;
                    var cookieReply = ResponderCore.BuildCookieReply(datagram.Buffer, _serverPublic);
                    await Client.SendAsync(cookieReply, datagram.RemoteEndPoint, _cts.Token);
                    continue;
                }

                var (response, session, failure) = ResponderCore.Respond(
                    datagram.Buffer, _serverPrivate, ServerIndex);
                if (response is null || session is null)
                {
                    continue; // MAC1/transkript reddi — sessiz
                }
                _session = session;
                await Client.SendAsync(response, datagram.RemoteEndPoint, _cts.Token);
            }
            else if (type == WireGuardNoise.MessageTypeTransport && _session is not null)
            {
                // Tünel mesajı: çöz + kendi anahtarımızla yankıla (round-trip).
                var plain = _session.Decrypt(datagram.Buffer);
                if (plain is null)
                {
                    continue;
                }
                var echo = _session.Encrypt(plain);
                if (echo is not null)
                {
                    await Client.SendAsync(echo, datagram.RemoteEndPoint, _cts.Token);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        _cts.Dispose();
        Client.Dispose();
    }
}
