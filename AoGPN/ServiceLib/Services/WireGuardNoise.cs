using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WireGuardNoise — Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s istemci tarafı
// (handshake initiation) kripto üreticisi.
//
// wireguard-go'dan (MIT) birebir port edildi:
//   * device/noise-protocol.go   → CreateMessageInitiation
//   * device/noise-helpers.go    → KDF1/KDF2, HMAC-BLAKE2s
//   * device/cookie.go           → mac1 anahtarı + MAC1 hesaplama
//
// Tüm primitifler denetlenmiş kütüphanelerden gelir — el yazısı kripto yok:
//   * X25519  : BouncyCastle (Org.BouncyCastle.Math.EC.Rfc7748.X25519)
//   * BLAKE2s : BouncyCastle (keyed MAC ve HMAC dahil)
//   * ChaCha20-Poly1305 : .NET System.Security.Cryptography.ChaCha20Poly1305
//
// Bu sınıfın ABD/AT testi <c>ServiceLib.Tests</c>'te RFC 7693 (BLAKE2s) vektörleri
// ve yerel iki-taraflı (initiator↔responder) el sıkışma round-trip'i ile doğrulanır.
// ─────────────────────────────────────────────────────────────────────────
internal static class WireGuardNoise
{
    internal const int MessageTypeInitiation = 1;
    internal const int MessageTypeResponse = 2;
    internal const int MessageTypeCookieReply = 3;
    internal const int MessageTypeTransport = 4;

    internal const int InitiationSize = 148;
    internal const int ResponseSize = 92;
    internal const int CookieReplySize = 64;
    internal const int TransportHeaderSize = 16;

    private const int PublicKeySize = 32;
    private const int StaticCiphertextSize = PublicKeySize + 16; // 48
    private const int TimestampSize = 12;
    private const int TimestampCiphertextSize = TimestampSize + 16; // 28
    private const int MacSize = 16;

    private static readonly byte[] LabelMac1 = "mac1----"u8.ToArray();
    private static readonly byte[] LabelCookie = "cookie--"u8.ToArray();

    /// <summary>
    /// Zincir anahtarının BAŞLANGIÇ değeri: BLAKE2s-256(CONSTRUCTION) — wireguard-go
    /// InitialChainKey (el sıkışmada chainKey buradan başlar).
    /// </summary>
    internal static readonly byte[] InitialChainKey;

    /// <summary>Transkript karmasının başlangıç değeri: BLAKE2s-256(InitialChainKey || IDENTIFIER).</summary>
    internal static readonly byte[] InitialHash;

    static WireGuardNoise()
    {
        // init_chain_key = BLAKE2s-256(CONSTRUCTION)
        InitialChainKey = Blake2s("Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s"u8.ToArray(), 32);
        // init_hash = BLAKE2s-256( init_chain_key || IDENTIFIER )
        var identifier = "WireGuard v1 zx2c4 Jason@zx2c4.com"u8.ToArray();
        var input = new byte[InitialChainKey.Length + identifier.Length];
        Buffer.BlockCopy(InitialChainKey, 0, input, 0, InitialChainKey.Length);
        Buffer.BlockCopy(identifier, 0, input, InitialChainKey.Length, identifier.Length);
        InitialHash = Blake2s(input, 32);
    }

    // ── BLAKE2s primitifleri ──────────────────────────────────────────────

    /// <summary>Unkeyed BLAKE2s — <paramref name="digestBytes"/> (1..32) çıktı boyutu.</summary>
    internal static byte[] Blake2s(byte[] data, int digestBytes)
    {
        var d = new Blake2sDigest(digestBytes * 8);
        d.BlockUpdate(data, 0, data.Length);
        var outp = new byte[d.GetDigestSize()];
        d.DoFinal(outp, 0);
        return outp;
    }

    /// <summary>Keyed BLAKE2s (Prefix-MAC) — WireGuard MAC1/MAC2'nin kullandığı mod.</summary>
    internal static byte[] Blake2sKeyed(byte[] key, byte[] data, int digestBytes)
    {
        var d = new Blake2sDigest(key, digestBytes, null, null);
        d.BlockUpdate(data, 0, data.Length);
        var outp = new byte[d.GetDigestSize()];
        d.DoFinal(outp, 0);
        return outp;
    }

    /// <summary>HMAC-BLAKE2s-256 — WireGuard KDF'nin temel bloğu.</summary>
    internal static byte[] HmacBlake2s(byte[] key, params byte[][] parts)
    {
        var mac = new HMac(new Blake2sDigest());
        mac.Init(new KeyParameter(key));
        foreach (var part in parts)
        {
            mac.BlockUpdate(part, 0, part.Length);
        }
        var outp = new byte[mac.GetMacSize()];
        mac.DoFinal(outp, 0);
        return outp;
    }

    // ── WireGuard KDF ─────────────────────────────────────────────────────

    /// <summary>KDF1: karıştırma anahtarı (mixKey) güncellemesi.</summary>
    internal static byte[] Kdf1(byte[] chainKey, byte[] input)
    {
        var prk = HmacBlake2s(chainKey, input);
        return HmacBlake2s(prk, new byte[] { 0x01 });
    }

    /// <summary>KDF2: yeni karıştırma anahtarı + simetrik anahtar (ss türetmede).</summary>
    internal static (byte[] ChainKey, byte[] Key) Kdf2(byte[] chainKey, byte[] input)
    {
        var prk = HmacBlake2s(chainKey, input);
        var t0 = HmacBlake2s(prk, new byte[] { 0x01 });
        var t1 = HmacBlake2s(prk, t0, new byte[] { 0x02 });
        return (t0, t1);
    }

    /// <summary>KDF3: (chainKey, tau, key) — response aşamasında PSK karıştırmada.</summary>
    internal static (byte[] ChainKey, byte[] Tau, byte[] Key) Kdf3(byte[] chainKey, byte[] input)
    {
        var prk = HmacBlake2s(chainKey, input);
        var t0 = HmacBlake2s(prk, new byte[] { 0x01 });
        var t1 = HmacBlake2s(prk, t0, new byte[] { 0x02 });
        var t2 = HmacBlake2s(prk, t1, new byte[] { 0x03 });
        return (t0, t1, t2);
    }

    /// <summary>mixHash: h' = BLAKE2s-256(h || data) (transcript karma).</summary>
    internal static byte[] MixHash(byte[] hash, byte[] data)
    {
        var input = new byte[hash.Length + data.Length];
        Buffer.BlockCopy(hash, 0, input, 0, hash.Length);
        Buffer.BlockCopy(data, 0, input, hash.Length, data.Length);
        return Blake2s(input, 32);
    }

    /// <summary>MAC1 anahtarı: HASH("mac1----" || diğer tarafın statik genel anahtarı).</summary>
    internal static byte[] Mac1Key(byte[] responderStaticPublic)
    {
        var input = new byte[LabelMac1.Length + responderStaticPublic.Length];
        Buffer.BlockCopy(LabelMac1, 0, input, 0, LabelMac1.Length);
        Buffer.BlockCopy(responderStaticPublic, 0, input, LabelMac1.Length, responderStaticPublic.Length);
        return Blake2s(input, 32);
    }

    /// <summary>
    /// Cookie şifreleme anahtarı: HASH("cookie--" || sunucunun statik genel anahtarı).
    /// Sunucu cookie'yi bu anahtarla XChaCha20-Poly1305 ile şifreler (AAD = son MAC1);
    /// istemci aynı anahtarla çözer.
    /// </summary>
    internal static byte[] CookieKey(byte[] responderStaticPublic)
    {
        var input = new byte[LabelCookie.Length + responderStaticPublic.Length];
        Buffer.BlockCopy(LabelCookie, 0, input, 0, LabelCookie.Length);
        Buffer.BlockCopy(responderStaticPublic, 0, input, LabelCookie.Length, responderStaticPublic.Length);
        return Blake2s(input, 32);
    }

    /// <summary>
    /// MAC1 hesaplar: keyed-BLAKE2s-128(key, mesajın MAC alanına kadarki öneki).
    /// initiation'da mesaj [0..smac1), response'ta [0..smac1).
    /// </summary>
    internal static byte[] ComputeMac1(byte[] key, byte[] packet, int smac1)
    {
        var prefix = new byte[smac1];
        Buffer.BlockCopy(packet, 0, prefix, 0, smac1);
        return Blake2sKeyed(key, prefix, 16);
    }

    // ── X25519 ────────────────────────────────────────────────────────────

    /// <summary>Gizli anahtardan genel anahtar (basepoint çarpımı, clampla).</summary>
    internal static byte[] PublicKey(byte[] secret)
    {
        var pk = new byte[PublicKeySize];
        X25519.ScalarMultBase(secret, 0, pk, 0);
        return pk;
    }

    /// <summary>DH(özel, genel). Sonuç tüm-sıfırsa (geçersiz) false döner.</summary>
    internal static bool SharedSecret(byte[] privateKey, byte[] publicKey, out byte[] ss)
    {
        ss = new byte[PublicKeySize];
        return X25519.CalculateAgreement(privateKey, 0, publicKey, 0, ss, 0);
    }

    // ── ChaCha20-Poly1305 ─────────────────────────────────────────────────

    /// <summary>AEAD şifrele (nonce=sıfır, AAD=hash). Çıktı: ciphertext || tag.</summary>
    internal static byte[] AeadSeal(byte[] key, byte[] plaintext, byte[] aad)
    {
        using var aead = new ChaCha20Poly1305(key);
        var nonce = new byte[12]; // ZeroNonce
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        aead.Encrypt(nonce, plaintext, ct, tag, aad);
        var outp = new byte[ct.Length + tag.Length];
        Buffer.BlockCopy(ct, 0, outp, 0, ct.Length);
        Buffer.BlockCopy(tag, 0, outp, ct.Length, tag.Length);
        return outp;
    }

    // ── El sıkışma kurulumu (handshake initiation) ───────────────────────

    /// <summary>
    /// Initiation mesajını oluşturur (wireguard-go CreateMessageInitiation +
    /// AddMacs portu). <paramref name="clientPrivateKey"/> (istemci statik özel),
    /// <paramref name="serverPublicKey"/> (sunucu statik genel).
    /// MAC1'den sonra MAC2 alanı sıfır bırakılır (çerez yok — normal başlangıçtır).
    /// </summary>
    internal static (byte[] Packet, byte[] OurStaticPublic, byte[] SenderIndexBuffer) BuildInitiation(
        byte[] clientPrivateKey,
        byte[] serverPublicKey,
        uint senderIndex)
    {
        var hash = (byte[])InitialHash.Clone();
        var chainKey = (byte[])InitialChainKey.Clone(); // wireguard-go InitialChainKey — BUGFIX: eski sürüm InitialHash kullanıyordu

        // pre-message: responder (sunucu) statik genel anahtarı transkripte karıştırılır
        hash = MixHash(hash, serverPublicKey);

        // e (ephemeral) anahtar çifti
        var ephemeralPrivate = RandomNumberGenerator.GetBytes(PublicKeySize);
        var ephemeralPublic = PublicKey(ephemeralPrivate);
        chainKey = Kdf1(chainKey, ephemeralPublic); // mixKey(e)
        hash = MixHash(hash, ephemeralPublic);      // mixHash(e)

        // istemcinin statik genel anahtarı (kimliği) — encrypted_static şifrelenecek
        var ourStaticPublic = PublicKey(clientPrivateKey);

        // ENCRYPT s_i (statik genel anahtar): anahtar = DH(e_i, s_R) üzerinden KDF2
        if (!SharedSecret(ephemeralPrivate, serverPublicKey, out var ss1))
        {
            throw new ArgumentException("Sunucu (responder) anahtarı geçersiz (DH sıfır).");
        }
        var (ck1, key1) = Kdf2(chainKey, ss1);
        chainKey = ck1;
        var encryptedStatic = AeadSeal(key1, ourStaticPublic, hash);
        hash = MixHash(hash, encryptedStatic);

        // ENCRYPT timestamp: anahtar = s_i . s_R (precomputed static-static DH) üzerinden KDF2
        if (!SharedSecret(clientPrivateKey, serverPublicKey, out var ss2))
        {
            throw new ArgumentException("İstemci/sunucu anahtar çifti geçersiz (DH sıfır).");
        }
        var (ck2, key2) = Kdf2(chainKey, ss2);
        chainKey = ck2;
        var timestamp = Tai64nNow();
        var encryptedTimestamp = AeadSeal(key2, timestamp, hash);
        hash = MixHash(hash, encryptedTimestamp); // son transkript karışımı

        // mesaj montajı
        var msg = new byte[InitiationSize];
        WriteUint32(msg, 0, MessageTypeInitiation);
        WriteUint32(msg, 4, senderIndex);
        Buffer.BlockCopy(ephemeralPublic, 0, msg, 8, PublicKeySize);                    // 8..40
        Buffer.BlockCopy(encryptedStatic, 0, msg, 40, StaticCiphertextSize);           // 40..88
        Buffer.BlockCopy(encryptedTimestamp, 0, msg, 88, TimestampCiphertextSize);     // 88..116
        // mac1 116..132, mac2 132..148
        var mac1Key = Mac1Key(serverPublicKey);
        var mac1 = ComputeMac1(mac1Key, msg, 116);
        Buffer.BlockCopy(mac1, 0, msg, 116, MacSize);

        return (msg, ourStaticPublic, WriteUint32Buf(senderIndex));
    }

    /// <summary>
    /// Sunucudan gelen el sıkışma yanıtının (handshake response) MAC1'ini doğrular.
    /// Response MAC1 anahtarı, istemcinin (bize dönen tarafın) statik GENEL anahtarına
    /// dayanır. MAC1 eşleşiyorsa paket kesinlikle bu el sıkışmayı işleyen gerçek WG
    /// sunucusundandır — sahte paketle taklit edilemez.
    /// </summary>
    internal static bool VerifyResponseMac1(byte[] response, byte[] ourStaticPublic)
    {
        if (response.Length != ResponseSize)
        {
            return false;
        }
        var mac1Key = Mac1Key(ourStaticPublic);
        var expected = ComputeMac1(mac1Key, response, 60); // mac1 alanı 60..76
        return CryptographicOperations.FixedTimeEquals(expected, new ReadOnlySpan<byte>(response, 60, MacSize));
    }

    /// <summary>Bir UDP datagramının WireGuard mesaj tipini (varsa) döndürür.</summary>
    internal static int? PeekMessageType(byte[] packet)
    {
        if (packet.Length < 4)
        {
            return null;
        }
        var type = ReadUint32(packet, 0);
        return type is >= 1 and <= 4 ? (int)type : null;
    }

    // ── küçük yardımcılar ─────────────────────────────────────────────────

    /// <summary>
    /// XChaCha20-Poly1305 şifreleme — WireGuard cookie reply (24 bayt nonce).
    /// Çıktı: ciphertext || tag (Poly1305). BouncyCastle XChaCha20Poly1305 (BC 2.0+).
    /// </summary>
    internal static byte[] XAeadSeal(byte[] key, byte[] nonce24, byte[] plaintext, byte[] aad)
    {
        var cipher = new Org.BouncyCastle.Crypto.Modes.XChaCha20Poly1305();
        cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
            new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), 128, nonce24, aad));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        len += cipher.DoFinal(output, len);
        return output.AsSpan(0, len).ToArray();
    }

    /// <summary>XChaCha20-Poly1305 şifre çözme (doğrulama başarısızsa false).</summary>
    internal static bool XAeadOpen(byte[] key, byte[] nonce24, byte[] ciphertextWithTag, byte[] aad, out byte[] plaintext)
    {
        plaintext = [];
        try
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.XChaCha20Poly1305();
            cipher.Init(false, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
                new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), 128, nonce24, aad));
            var output = new byte[cipher.GetOutputSize(ciphertextWithTag.Length)];
            var len = cipher.ProcessBytes(ciphertextWithTag, 0, ciphertextWithTag.Length, output, 0);
            len += cipher.DoFinal(output, len);
            plaintext = output.AsSpan(0, len).ToArray();
            return true;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return false;
        }
    }

    /// <summary>ChaCha20-Poly1305 şifre çözme (doğrulama başarısızsa false).</summary>
    internal static bool AeadOpen(byte[] key, byte[] ciphertextWithTag, byte[] aad, out byte[] plaintext)
    {
        plaintext = new byte[ciphertextWithTag.Length - 16];
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        Buffer.BlockCopy(ciphertextWithTag, 0, ct, 0, ct.Length);
        Buffer.BlockCopy(ciphertextWithTag, ct.Length, tag, 0, 16);
        try
        {
            using var aead = new ChaCha20Poly1305(key);
            var nonce = new byte[12];
            aead.Decrypt(nonce, ct, tag, plaintext, aad);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = Array.Empty<byte>();
            return false;
        }
    }

    internal static uint ReadUint32(ReadOnlySpan<byte> buf, int offset) =>
        unchecked((uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24)));

    internal static void WriteUint32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    internal static ulong ReadUint64(ReadOnlySpan<byte> buf, int offset) =>
        unchecked((ulong)buf[offset]
                 | ((ulong)buf[offset + 1] << 8)
                 | ((ulong)buf[offset + 2] << 16)
                 | ((ulong)buf[offset + 3] << 24)
                 | ((ulong)buf[offset + 4] << 32)
                 | ((ulong)buf[offset + 5] << 40)
                 | ((ulong)buf[offset + 6] << 48)
                 | ((ulong)buf[offset + 7] << 56));

    internal static void WriteUint64(byte[] buf, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            buf[offset + i] = unchecked((byte)(value >> (i * 8)));
        }
    }

    private static byte[] WriteUint32Buf(uint value)
    {
        var buf = new byte[4];
        WriteUint32(buf, 0, value);
        return buf;
    }

    /// <summary>
    /// TAI64N zaman damgası (12 bayt): 8 bayt big-endian saniye (2^62 + 10 + UNIX saniye)
    /// + 4 bayt big-endian nanosaniye. Sunucu yalnızca replay kontrolü yapar (sondan sonraki
    /// zaman) — güncel sistem saati onu geçtiği için kabul edilir.
    /// </summary>
    internal static byte[] Tai64nNow()
    {
        var ts = new byte[TimestampSize];
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        unchecked
        {
            ulong seconds = 0x400000000000000AUL + (ulong)unixSeconds;
            for (var i = 0; i < 8; i++)
            {
                ts[i] = (byte)(seconds >> (56 - i * 8));
            }
            var nanos = (uint)(DateTimeOffset.UtcNow.UtcTicks % TimeSpan.TicksPerSecond) * 100; // tick→ns
            for (var i = 0; i < 4; i++)
            {
                ts[8 + i] = (byte)(nanos >> (24 - i * 8));
            }
        }
        return ts;
    }
}