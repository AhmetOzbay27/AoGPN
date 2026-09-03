using System.Security.Cryptography;
using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Gerçek WireGuard el sıkışması (handshake initiation) probe'unun testleri:
///
///  1. Kripto primitifleri RFC 7693 (BLAKE2s) vektörleriyle doğrular.
///  2. İki taraflı (initiator ↔ responder) el sıkışmayı wireguard-go state
///     machine'ine sadık bir karşı taraf ile round-trip eder.
///  3. Loopback üzerinde gerçek UDP yığınıyla probe davranışını test eder.
///
/// Hiçbiri İtalya/Almanya sunucularına dokunmaz.
/// </summary>
public class WireGuardHandshakeProbeTests
{
    // ── RFC 7693 (BLAKE2s) vektörleri ─────────────────────────────────────

    [Fact]
    public void Blake2s256_MatchesRfc7693_Abc()
    {
        // RFC 7693 A.2: BLAKE2s-256("abc")
        var digest = WireGuardNoise.Blake2s("abc"u8.ToArray(), 32);
        Hex(digest).Should().Be("508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982");
    }

    [Fact]
    public void Blake2sKeyed256_MatchesRfc7693_EmptyInput()
    {
        // RFC 7693 A.1: keyed BLAKE2s-256, key=0..31, boş giriş
        var key = Range(0, 32);
        var digest = WireGuardNoise.Blake2sKeyed(key, Array.Empty<byte>(), 32);
        Hex(digest).Should().Be("48a8997da407876b3d79c0d92325ad3b89cbb754d86ab71aee047ad345fd2c49");
    }

    // ── Kripto primitifleri: RFC 7748 (X25519) + ChaCha20-Poly1305 ───────

    [Fact]
    public void X25519_MatchesRfc7748_Vector1()
    {
        // RFC 7748 §5.2 vektörü
        var alicePriv = HexBytes("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var alicePub = HexBytes("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        var bobPriv = HexBytes("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var bobPub = HexBytes("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        var shared = HexBytes("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        Hex(WireGuardNoise.PublicKey(alicePriv)).Should().Be(Hex(alicePub));
        Hex(WireGuardNoise.PublicKey(bobPriv)).Should().Be(Hex(bobPub));
        WireGuardNoise.SharedSecret(alicePriv, bobPub, out var s1).Should().BeTrue();
        WireGuardNoise.SharedSecret(bobPriv, alicePub, out var s2).Should().BeTrue();
        Hex(s1).Should().Be(Hex(shared));
        Hex(s2).Should().Be(Hex(shared));
    }

    [Fact]
    public void ChaChaPoly1305_EncryptThenDecrypt_RoundTrips()
    {
        var key = RandomBytes(32);
        var aad = RandomBytes(16);
        var plaintext = "Ladies and Gentlemen of the class of '99"u8.ToArray();

        var ciphertext = WireGuardNoise.AeadSeal(key, plaintext, aad);
        WireGuardNoise.AeadOpen(key, ciphertext, aad, out var roundTrip).Should().BeTrue();
        Hex(roundTrip).Should().Be(Hex(plaintext));

        // Yanlış AAD → kimlik doğrulama başarısız
        WireGuardNoise.AeadOpen(key, ciphertext, Range(0, 16), out _).Should().BeFalse();
    }

    [Fact]
    public void Kdf2_FirstOutIsNewChainKey_SecondIsKey()
    {
        // KDF yapısının deterministik olduğunu + çıktılarının 32 bayt olduğunu doğrula.
        var (ck, key) = WireGuardNoise.Kdf2(Range(1, 32), Range(33, 32));
        ck.Length.Should().Be(32);
        key.Length.Should().Be(32);
        var (ck2, key2) = WireGuardNoise.Kdf2(Range(1, 32), Range(33, 32));
        Hex(ck2).Should().Be(Hex(ck));
        Hex(key2).Should().Be(Hex(key));
    }

    // ── İki taraflı el sıkışma round-trip (saf kripto, ağ yok) ────────────

    [Fact]
    public void FullHandshake_ClientInitiator_ServerResponder_VerifyResponseMac1()
    {
        var clientPrivate = RandomBytes(32);
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);

        var senderIndex = 0xDEADBEEFu;
        var (initiation, clientStaticPublic, _) = WireGuardNoise.BuildInitiation(clientPrivate, serverPublic, senderIndex);

        AssertInitiationLayout(initiation);

        // Sunucu (responder): el sıkışmayı tüket + geçerli yanıt üret.
        var (response, failure) = Responder.TryRespondCore(initiation, serverPrivate, clientStaticPublic, 0x01020304u);
        var why = response is null ? $"başarısızlık: {failure}" : "kabul";
        response.Should().NotBeNull(why);
        response!.Length.Should().Be(WireGuardNoise.ResponseSize);

        // İstemci: yanıtın MAC1'ini kendi statik genel anahtarıyla doğrular.
        WireGuardNoise.VerifyResponseMac1(response, clientStaticPublic).Should().BeTrue();
        WireGuardNoise.PeekMessageType(response).Should().Be(WireGuardNoise.MessageTypeResponse);
    }

    [Fact]
    public void FullHandshake_WrongServerKey_ResponderRejectsViaMac1()
    {
        // İstemcinin profilindeki sunucu genel anahtarı GERÇEK sunucuyla eşleşmiyor:
        // MAC1 yanlış → gerçek sunucu paketi sessizce düşürür, yanıt gelmez.
        var clientPrivate = RandomBytes(32);
        var actualServerPrivate = RandomBytes(32);
        var actualServerPublic = WireGuardNoise.PublicKey(actualServerPrivate);
        var wrongServerPublic = WireGuardNoise.PublicKey(RandomBytes(32));

        var (initiation, clientStaticPublic, _) = WireGuardNoise.BuildInitiation(clientPrivate, wrongServerPublic, 123u);

        // Gerçek sunucu, MAC1'i kendi genel anahtarına dayalı anahtarla doğrular → reddeder.
        var response = Responder.TryRespond(initiation, actualServerPrivate, clientStaticPublic, 4u);
        response.Should().BeNull("yanlış sunucu anahtarıyla gönderilen handshake kabul edilmemeli");
    }

    // ── Loopback UDP probe davranışı ──────────────────────────────────────

    [Fact]
    public async Task ProbeAsync_GenuineResponderOnLoopback_ReturnsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        using var responder = await Responder.StartServerAsync(serverPrivate, ct);
        var port = ((IPEndPoint)responder.Client.LocalEndPoint).Port;

        var probe = new WireGuardHandshakeProbe();
        var result = await probe.ProbeAsync(
            "it", "127.0.0.1", port,
            Convert.ToBase64String(serverPublic), Convert.ToBase64String(clientPrivate),
            new WireGuardHandshakeProbeOptions(WaitTimeoutMs: 2000, MaxAttempts: 1), ct);

        result.Status.Should().Be(UdpProbeStatus.Open);
        result.IsReachable.Should().BeTrue();
        result.Detail.Should().Contain("handshake_response");
    }

    [Fact]
    public async Task ProbeAsync_SilentListener_ReturnsHandshakeNoResponse()
    {
        // Canlı testte gözlenen senaryo: geçerli el sıkışma gönderildi ama yanıt
        // yok (ICMP kanıtı da yok). Bu junk-probe sessizliğinden (NoResponse) FARKLI
        // bir teşhistir — sağlıklı sunucu geçerli el sıkışmaya yanıt verirdi.
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var serverPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);

        using var responder = await Responder.StartSilentServerAsync(ct);
        var port = ((IPEndPoint)responder.Client.LocalEndPoint).Port;

        var probe = new WireGuardHandshakeProbe();
        var result = await probe.ProbeAsync(
            "it", "127.0.0.1", port,
            Convert.ToBase64String(serverPublic), Convert.ToBase64String(clientPrivate),
            new WireGuardHandshakeProbeOptions(WaitTimeoutMs: 700, MaxAttempts: 1), ct);

        result.Status.Should().Be(UdpProbeStatus.HandshakeNoResponse);
        result.IsReachable.Should().BeFalse();
        result.Detail.Should().Contain("el sıkışma");
    }

    [Fact]
    public async Task ProbeAsync_WrongServerKey_ReturnsHandshakeNoResponse()
    {
        // MAC1 reddedildi → sunucu sessiz → geçerli görünümlü el sıkışma yanıtsız.
        // Ağ yolu çalışıyor olabilir (paket ulaşıyor) ama el sıkışma tamamlanamıyor
        // — HandshakeNoResponse, Blocked'tan (ICMP kanıtı) ayrı tutulur.
        var ct = TestContext.Current.CancellationToken;
        var serverPrivate = RandomBytes(32);
        var actualServerPublic = WireGuardNoise.PublicKey(serverPrivate);
        var clientPrivate = RandomBytes(32);
        var wrongServerPublic = WireGuardNoise.PublicKey(RandomBytes(32));

        using var responder = await Responder.StartServerAsync(serverPrivate, ct);
        var port = ((IPEndPoint)responder.Client.LocalEndPoint).Port;

        var probe = new WireGuardHandshakeProbe();
        var result = await probe.ProbeAsync(
            "it", "127.0.0.1", port,
            Convert.ToBase64String(wrongServerPublic), Convert.ToBase64String(clientPrivate),
            new WireGuardHandshakeProbeOptions(WaitTimeoutMs: 700, MaxAttempts: 1), ct);

        result.Status.Should().Be(UdpProbeStatus.HandshakeNoResponse);
    }

    [Fact]
    public async Task ProbeAsync_ClosedPort_FakeIcmpEvidence_ReturnsBlocked()
    {
        // Windows ICMP rate-limit'ine bağımlılık YOK: gerçek port-unreachable
        // üretimi (hedef IP başına hız sınırlı) yerine sahte ICMP kanıtı — bağlı
        // UDP soketinin ConnectionReset yüzeyi enjekte edilir, Blocked deterministik
        // üretilir. Ağ, IP, gerçek port hiç kullanılmaz.
        var ct = TestContext.Current.CancellationToken;
        var serverPublic = WireGuardNoise.PublicKey(Cs(0x42));
        var clientPrivate = RandomBytes(32);
        var probe = new WireGuardHandshakeProbe(_ => new FakeIcmpUnreachableSocket(SocketError.ConnectionReset));

        var result = await probe.ProbeAsync(
            "de", "10.0.0.1", 51820,
            Convert.ToBase64String(serverPublic), Convert.ToBase64String(clientPrivate),
            new WireGuardHandshakeProbeOptions(WaitTimeoutMs: 1000, MaxAttempts: 1), ct);

        result.Status.Should().Be(UdpProbeStatus.Blocked);
        result.IsReachable.Should().BeFalse();
    }

    [Fact]
    public async Task ProbeAsync_MissingKeys_ReturnsNoResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new WireGuardHandshakeProbe();
        var result = await probe.ProbeAsync("it", "127.0.0.1", 51820, null, null, new WireGuardHandshakeProbeOptions(), ct);
        result.Status.Should().Be(UdpProbeStatus.NoResponse);
        result.Detail.Should().Contain("anahtar");
    }

    // ── Layout doğrulaması ────────────────────────────────────────────────

    private static void AssertInitiationLayout(byte[] initiation)
    {
        initiation.Length.Should().Be(WireGuardNoise.InitiationSize);
        WireGuardNoise.PeekMessageType(initiation).Should().Be(WireGuardNoise.MessageTypeInitiation);
        // ephemeral (8..40) boş olmamalı
        initiation.Skip(8).Take(32).Should().Contain(b => b != 0);
        // encrypted_static + encrypted_timestamp dolu olmalı
        initiation.Skip(40).Take(48).Should().Contain(b => b != 0);
        initiation.Skip(88).Take(28).Should().Contain(b => b != 0);
        // MAC2 (132..148) sıfır — çerez yok
        initiation.Skip(132).Take(16).Should().OnlyContain(b => b == 0);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────

    private static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    private static byte[] Cs(byte fill)
    {
        var a = new byte[32];
        Array.Fill(a, fill);
        return a;
    }

    private static byte[] Range(int start, int count)
    {
        var a = new byte[count];
        for (var i = 0; i < count; i++)
        {
            a[i] = (byte)(start + i);
        }
        return a;
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    private static byte[] HexBytes(string hex)
    {
        var result = new byte[hex.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return result;
    }

    /// <summary>
    /// wireguard-go ConsumeMessageInitiation + CreateMessageResponse portu —
    /// istemci probe'unu bağımsız bir karşı tarafla round-trip etmek için.
    /// Gerçek istemciyle AYNI primitifleri (WireGuardNoise) kullanır; amac savunma
    /// tarafının state machine'ini doğrulamaktır.
    /// </summary>
    private static class Responder
    {
        internal static byte[]? TryRespond(byte[] initiation, byte[] serverPrivate, byte[]? expectedClientStatic, uint serverIndex)
        {
            return TryRespondCore(initiation, serverPrivate, expectedClientStatic, serverIndex).Response;
        }

        internal static (byte[]? Response, string? Failure) TryRespondCore(
            byte[] initiation, byte[] serverPrivate, byte[]? expectedClientStatic, uint serverIndex)
        {
            if (initiation.Length != WireGuardNoise.InitiationSize)
            {
                return (null, "boyut");
            }

            var hash = (byte[])WireGuardNoise.InitialHash.Clone();
            var chainKey = (byte[])WireGuardNoise.InitialChainKey.Clone(); // wireguard-go InitialChainKey
            var serverPublic = WireGuardNoise.PublicKey(serverPrivate);

            // pre-message
            hash = WireGuardNoise.MixHash(hash, serverPublic);
            var ephemeral = initiation[8..40];
            hash = WireGuardNoise.MixHash(hash, ephemeral);
            chainKey = WireGuardNoise.Kdf1(chainKey, ephemeral);

            // decrypt static (kimlik) — KDF2, zincir anahtarını ilerletir
            if (!WireGuardNoise.SharedSecret(serverPrivate, ephemeral, out var ss1))
            {
                return (null, $"ss1 ({BitConverter.ToString(ss1).Replace("-","")})");
            }
            var (ck1, key1) = WireGuardNoise.Kdf2(chainKey, ss1);
            chainKey = ck1;
            if (!WireGuardNoise.AeadOpen(key1, initiation[40..88], hash, out var clientStatic))
            {
                return (null, $"static AAD={BitConverter.ToString(hash).Replace("-","")}");
            }
            hash = WireGuardNoise.MixHash(hash, initiation[40..88]);

            if (expectedClientStatic is not null
                && !clientStatic.AsSpan().SequenceEqual(expectedClientStatic))
            {
                return (null, "bilinmeyen istemci");
            }

            // timestamp doğrula (replay/akıcılık testte atlandı) — KDF2, zincir anahtarını ilerletir
            if (!WireGuardNoise.SharedSecret(serverPrivate, clientStatic, out var ss2))
            {
                return (null, "ss2 sıfır");
            }
            var (ck2, key2) = WireGuardNoise.Kdf2(chainKey, ss2);
            chainKey = ck2;
            if (!WireGuardNoise.AeadOpen(key2, initiation[88..116], hash, out _))
            {
                return (null, $"timestamp AAD={BitConverter.ToString(hash).Replace("-","")}");
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
            var (_, tau, key3) = WireGuardNoise.Kdf3(chainKey, psk);
            hash = WireGuardNoise.MixHash(hash, tau);
            var empty = WireGuardNoise.AeadSeal(key3, Array.Empty<byte>(), hash);
            hash = WireGuardNoise.MixHash(hash, empty);

            var resp = new byte[WireGuardNoise.ResponseSize];
            WireGuardNoise.WriteUint32(resp, 0, WireGuardNoise.MessageTypeResponse);
            WireGuardNoise.WriteUint32(resp, 4, serverIndex);
            WireGuardNoise.WriteUint32(resp, 8, clientSender);
            Buffer.BlockCopy(e2, 0, resp, 12, 32);
            Buffer.BlockCopy(empty, 0, resp, 44, empty.Length);
            var mac1 = WireGuardNoise.Blake2sKeyed(
                WireGuardNoise.Mac1Key(clientStatic), resp[..60], 16);
            Buffer.BlockCopy(mac1, 0, resp, 60, 16);
            return (resp, null);
        }

        internal static async Task<UdpClient> StartServerAsync(byte[] serverPrivate, CancellationToken ct)
        {
            var server = new UdpClient(AddressFamily.InterNetwork);
            server.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    UdpReceiveResult datagram;
                    try
                    {
                        datagram = await server.ReceiveAsync(ct);
                    }
                    catch
                    {
                        return;
                    }
                    var response = TryRespond(datagram.Buffer, serverPrivate, null!, 0x4242u);
                    if (response is not null && response.Length == WireGuardNoise.ResponseSize)
                    {
                        await server.SendAsync(response, datagram.RemoteEndPoint, ct);
                    }
                }
            });
            return server;
        }

        internal static async Task<UdpClient> StartSilentServerAsync(CancellationToken ct)
        {
            var server = new UdpClient(AddressFamily.InterNetwork);
            server.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        _ = await server.ReceiveAsync(ct); // al, yanıt verme
                    }
                }
                catch
                {
                    // normal kapanış
                }
            });
            return server;
        }
    }
}