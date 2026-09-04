using System.Security.Cryptography;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WireGuardDataPlane — el sıkışma sonrası taşıma katmanı (Faz 2c)
//
// wireguard-go device/send.go + receive.go portu:
//
//   Transport mesajı (tip 4): [u32 type][u32 receiver][u64 counter] (16 bayt
//   başlık) + ChaCha20-Poly1305 ile şifrelenmiş içerik (+ 16 bayt tag).
//   Nonce: 12 bayt — ilk 4 bayt sıfır, son 8 bayt counter (little-endian).
//   İçerik gönderirken 16'nın katına doldurulur (PaddingMultiple); alırken IP
//   başlığındaki toplam uzunluk alanına göre kırpılır (wireguard-go
//   RoutineSequentialReceiver ile aynı davranış).
//
//   Anahtarlar (BeginSymmetricSession): KDF2(chainKey, "") → (t0, t1);
//   initiator send=t0 / recv=t1, responder tam tersi. Her yönün kendi counter'ı
//   (RejectAfterMessages üst sınırı) + alıcı tarafında kayan pencere replay
//   filtresi (2048 bit) vardır.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>WireGuard taşıma katmanı sabitleri (wireguard-go sabitleriyle aynı).</summary>
public static class WireGuardWire
{
    /// <summary>Transport mesajı başlık boyutu (type + receiver + counter).</summary>
    public const int TransportHeaderSize = 16;

    /// <summary>İçerik padding hizası (wireguard-go PaddingMultiple).</summary>
    public const int PaddingMultiple = 16;

    /// <summary>Counter üst sınırı: 2^64 - 2^13 - 1 (RejectAfterMessages).</summary>
    public const ulong RejectAfterMessages = ulong.MaxValue - (1UL << 13);
}

/// <summary>
/// Kayan pencere replay filtresi — wireguard-go replay/replay.go (RFC 6479) portu.
/// 128 blokluk (128×64 bit) halka tampon: counter'ı son görülenden yalnızca ileriye
/// kabul eder, penceredeki tekrarları bit kontrolüyle reddeder (old != new).
/// Yalnızca başarılı şifre çözme SONRASI çağrılmalıdır (wg alım sırasıyla aynı).
/// </summary>
public sealed class WireGuardReplayFilter
{
    private const int RingBlocks = 1 << 7;          // 128 blok (halka)
    private const int BlockBits = 64;               // 1 << 6
    private const int WindowSize = (RingBlocks - 1) * BlockBits; // 8128 bit
    private const int BlockMask = RingBlocks - 1;
    private const int BitMask = BlockBits - 1;

    private readonly ulong[] _ring = new ulong[RingBlocks];
    private ulong _last; // zero-value: ilk counter (0) kabul edilir

    /// <summary>Counter'ı filtreye işler: kabul edilirse true (iç durum ilerletilir).</summary>
    public bool ValidateCounter(ulong counter)
    {
        lock (_ring)
        {
            var indexBlock = (int)(counter >> 6);
            if (counter > _last)
            {
                // Pencereyi ileri taşı: yeni girilen blokları temizle.
                var current = (int)(_last >> 6);
                var diff = indexBlock - current;
                if (diff > RingBlocks)
                {
                    diff = RingBlocks; // tüm halkayı temizleyecek şekilde sınırla
                }
                for (var i = current + 1; i <= current + diff; i++)
                {
                    _ring[i & BlockMask] = 0;
                }
                _last = counter;
            }
            else if (_last - counter > WindowSize)
            {
                return false; // pencerenin gerisinde
            }

            // Bit kontrolü + işaretle: zaten işaretliyse tekrar → reddet.
            indexBlock &= BlockMask;
            var indexBit = (int)(counter & BitMask);
            var old = _ring[indexBlock];
            var fresh = old | (1UL << indexBit);
            _ring[indexBlock] = fresh;
            return old != fresh;
        }
    }
}

/// <summary>
/// El sıkışma sonrası taşıma oturumu: yön bazlı anahtarlar + counter'lar + replay
/// filtresi. <see cref="Encrypt"/> tünel içine giden IP paketini, <see cref="Decrypt"/>
/// tünelden gelen transport mesajını işler (wire format — gerçek sunucuyla uyumlu).
/// </summary>
public sealed class WireGuardSession : IDisposable
{
    private readonly byte[] _sendKey;
    private readonly byte[] _recvKey;
    private readonly WireGuardReplayFilter _replay = new();
    private ulong _sendCounter;
    private int _disposed;

    // Tier 4 — sıfır tahsisat: ChaCha20Poly1305 örneği (natif anahtar tutamacı)
    // PAKET BAŞINA yaratılmaz; yön başına BİR KEZ tembelce kurulur ve oturum
    // boyunca yeniden kullanılır (Encrypt yalnızca yakalama hattından, Decrypt
    // yalnızca alım döngüsünden çağrılır — yönler ayrı olduğu için örnekler
    // eşzamanlı kullanılmaz). Nonce tamponları da yön başına tek kez ayrılır.
    private ChaCha20Poly1305? _sendAead;
    private ChaCha20Poly1305? _recvAead;
    private readonly byte[] _sendNonce = new byte[12]; // ilk 4 bayt sıfır, son 8 bayt counter
    private readonly byte[] _recvNonce = new byte[12];

    /// <summary>Bu oturumun kendi index'i (incoming mesajların receiver alanında beklenir).</summary>
    public uint LocalIndex { get; }

    /// <summary>Karşı tarafın index'i (outgoing mesajların receiver alanına yazılır).</summary>
    public uint RemoteIndex { get; }

    /// <summary>İlk el sıkışmayı başlatan taraf mıyız (yön anahtarlarının yorumu).</summary>
    public bool IsInitiator { get; }

    /// <param name="sendKey">Bu taraftan gönderim anahtarı.</param>
    /// <param name="recvKey">Bu tarafa gelenleri çözme anahtarı.</param>
    public WireGuardSession(byte[] sendKey, byte[] recvKey, uint localIndex, uint remoteIndex, bool isInitiator)
    {
        _sendKey = sendKey;
        _recvKey = recvKey;
        LocalIndex = localIndex;
        RemoteIndex = remoteIndex;
        IsInitiator = isInitiator;
    }

    /// <summary>
    /// IP paketini WireGuard transport mesajına şifreler (wire format, gerçek
    /// sunucuya gönderilebilir). Counter aşarsa veya boş pakette null.
    /// </summary>
    public byte[]? Encrypt(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0 || packet.Length > 0xFFFF)
        {
            return null;
        }

        var counter = Interlocked.Increment(ref _sendCounter) - 1;
        if (counter >= WireGuardWire.RejectAfterMessages)
        {
            return null;
        }

        // Tier 4: tek tahsisat — hedef wire tamponu; padding ara kopyası YOK.
        // Paket doğrudan wire'ın plaintext bölgesine kopyalanır, geri kalan
        // padding sıfırdır (yeni dizi) ve AEAD yerinde (in-place) şifreler.
        var padded = (packet.Length + WireGuardWire.PaddingMultiple - 1) & ~(WireGuardWire.PaddingMultiple - 1);
        var wire = new byte[WireGuardWire.TransportHeaderSize + padded + 16];
        WireGuardNoise.WriteUint32(wire, 0, WireGuardNoise.MessageTypeTransport);
        WireGuardNoise.WriteUint32(wire, 4, RemoteIndex);
        WireGuardNoise.WriteUint64(wire, 8, counter);

        packet.CopyTo(wire.AsSpan(WireGuardWire.TransportHeaderSize));
        WriteNonce(_sendNonce, counter);
        var aead = _sendAead ??= new ChaCha20Poly1305(_sendKey);
        var region = wire.AsSpan(WireGuardWire.TransportHeaderSize, padded);
        aead.Encrypt(_sendNonce, region, region, wire.AsSpan(WireGuardWire.TransportHeaderSize + padded, 16));
        return wire;
    }

    /// <summary>
    /// Transport mesajını çözer ve orijinal IP paketini döndürür (padding IP
    /// uzunluk alanına göre kırpılır). Tip/receiver/counter/replay/doğrulama
    /// başarısızsa null — çağıran sayaçta işler.
    /// </summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < WireGuardWire.TransportHeaderSize + 16)
        {
            return null;
        }
        if (WireGuardNoise.ReadUint32(wire, 0) != WireGuardNoise.MessageTypeTransport)
        {
            return null;
        }
        if (WireGuardNoise.ReadUint32(wire, 4) != LocalIndex)
        {
            return null;
        }

        var counter = WireGuardNoise.ReadUint64(wire, 8);
        if (counter >= WireGuardWire.RejectAfterMessages)
        {
            return null;
        }

        var content = wire[WireGuardWire.TransportHeaderSize..^16];
        var tag = wire[^16..];
        WriteNonce(_recvNonce, counter);
        var plaintext = new byte[content.Length];
        try
        {
            // Tier 4: cached AEAD (natif anahtar tutamacı paket başına kurulmaz).
            var aead = _recvAead ??= new ChaCha20Poly1305(_recvKey);
            aead.Decrypt(_recvNonce, content, tag, plaintext);
        }
        catch (CryptographicException)
        {
            return null; // kimlik doğrulama başarısız
        }

        // wireguard-go sırası: başarılı çözme SONRASI replay filtresi.
        if (!_replay.ValidateCounter(counter))
        {
            return null;
        }

        return StripIpPadding(plaintext);
    }

    // ── iç ───────────────────────────────────────────────────────────────

    /// <summary>Nonce tamponuna counter'ı yazar (ilk 4 bayt zaten sıfır — tekrar sıfırlanmaz).</summary>
    private static void WriteNonce(byte[] nonce, ulong counter)
    {
        WireGuardNoise.WriteUint64(nonce, 4, counter);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _sendAead?.Dispose();
        _recvAead?.Dispose();
        _sendAead = null;
        _recvAead = null;
    }

    /// <summary>
    /// Çözülen içerikteki padding'i IP başlığının toplam uzunluk alanına göre
    /// kırpar (wireguard-go RoutineSequentialReceiver davranışı). IPv4: bayt 2-3;
    /// IPv6: bayt 4-5 + 40 bayt sabit başlık. Tanınamazsa olduğu gibi döner.
    /// </summary>
    private static byte[] StripIpPadding(byte[] content)
    {
        if (content.Length >= 20 && (content[0] >> 4) == 4)
        {
            var length = (content[2] << 8) | content[3];
            if (length is >= 20 && length <= content.Length)
            {
                return content.AsSpan(0, length).ToArray();
            }
        }
        else if (content.Length >= 40 && (content[0] >> 4) == 6)
        {
            var length = ((content[4] << 8) | content[5]) + 40;
            if (length is >= 40 && length <= content.Length)
            {
                return content.AsSpan(0, length).ToArray();
            }
        }
        return content;
    }
}
