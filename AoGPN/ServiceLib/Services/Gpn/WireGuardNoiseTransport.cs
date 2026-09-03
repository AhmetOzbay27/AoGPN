using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WireGuardNoiseTransport — Faz 2c: gerçek WireGuard veri düzlemi
//
// WireGuardNoise ilkelleri üzerinde wireguard-go state machine'i:
//
//   WireGuardHandshakeClient  — initiator (Noise_IKpsk2): initiation üretimi,
//                               response tüketimi (MAC1 + transkript doğrulama),
//                               cookie reply (yük altındaki sunucu) + MAC2'li
//                               yeniden gönderim. Oturum anahtarlarını türetir.
//   WireGuardSession          — taşıma katmanı (tip-4 mesajlar, counter, replay).
//   WireGuardNoiseTransport   — IWireGuardTransport dikişi: ConnectAsync ile el
//                               sıkışmayı yapar, Encrypt/Decrypt oturumu kullanır.
//
// NoopWireGuardTransport'un yerine geçer — köprü/telemetri/test altyapısı değişmez.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// İnitiator (istemci) tarafı WireGuard el sıkışma durum makinesi.
/// wireguard-go device/noise-protocol.go CreateMessageInitiation +
/// ConsumeMessageResponse + cookie.go (CookieGenerator) portu.
/// </summary>
public sealed class WireGuardHandshakeClient
{
    private static readonly byte[] ZeroPsk = new byte[32];
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromSeconds(120); // CookieRefreshTime

    private readonly byte[] _clientPrivateKey;
    private readonly byte[] _serverPublicKey;
    private readonly byte[] _clientStaticPublic;
    private readonly byte[] _psk;
    private readonly byte[] _mac1Key;
    private readonly byte[] _cookieKey;

    // Initiation sonrası saklanan durum (response tüketimi için).
    private byte[]? _hash;
    private byte[]? _chainKey;
    private byte[]? _localEphemeralPrivate;
    private uint _localIndex;

    // Cookie durumu (yük altındaki sunucu).
    private byte[]? _lastMac1;
    private bool _hasLastMac1;
    private byte[]? _cookie;
    private DateTimeOffset _cookieSetAt = DateTimeOffset.MinValue;

    /// <summary>Anahtarlar WireGuard .conf formatında (base64, 32 bayt çözümlenir).</summary>
    public WireGuardHandshakeClient(byte[] clientPrivateKey, byte[] serverPublicKey, byte[]? psk = null)
    {
        _clientPrivateKey = clientPrivateKey ?? throw new ArgumentNullException(nameof(clientPrivateKey));
        _serverPublicKey = serverPublicKey ?? throw new ArgumentNullException(nameof(serverPublicKey));
        if (_clientPrivateKey.Length != 32 || _serverPublicKey.Length != 32)
        {
            throw new ArgumentException("WireGuard anahtarları 32 bayt olmalıdır.");
        }
        _clientStaticPublic = WireGuardNoise.PublicKey(_clientPrivateKey);
        _psk = psk ?? ZeroPsk;
        _mac1Key = WireGuardNoise.Mac1Key(_serverPublicKey);
        _cookieKey = WireGuardNoise.CookieKey(_serverPublicKey);
    }

    /// <summary>Sunucudan alınan cookie hâlâ geçerli mi (MAC2'li gönderim yapılabilir mi).</summary>
    public bool HasFreshCookie => _cookie is not null && DateTimeOffset.UtcNow - _cookieSetAt < CookieLifetime;

    /// <summary>Son üretilen initiation'ın sender index'i (response'ta receiver olur).</summary>
    public uint LocalIndex => _localIndex;

    /// <summary>
    /// Handshake initiation (148 bayt) üretir ve state'i response tüketimine hazırlar.
    /// Çerez varsa MAC2 de eklenir (yük altındaki sunucu MAC2'siz paketi cookie ile
    /// yanıtlar). wireguard-go CreateMessageInitiation + cookieGenerator.AddMacs.
    /// </summary>
    public byte[] CreateInitiation()
    {
        var hash = (byte[])WireGuardNoise.InitialHash.Clone();
        var chainKey = (byte[])WireGuardNoise.InitialChainKey.Clone(); // wireguard-go InitialChainKey

        // pre-message: sunucunun statik genel anahtarı
        hash = WireGuardNoise.MixHash(hash, _serverPublicKey);

        // ephemeral e_i
        var ephemeralPrivate = RandomNumberGenerator.GetBytes(32);
        var ephemeralPublic = WireGuardNoise.PublicKey(ephemeralPrivate);
        chainKey = WireGuardNoise.Kdf1(chainKey, ephemeralPublic);
        hash = WireGuardNoise.MixHash(hash, ephemeralPublic);

        // ENCRYPT s_i: DH(e_i, s_R)
        if (!WireGuardNoise.SharedSecret(ephemeralPrivate, _serverPublicKey, out var ss1))
        {
            throw new InvalidOperationException("Sunucu anahtarı geçersiz (DH sıfır).");
        }
        var (ck1, key1) = WireGuardNoise.Kdf2(chainKey, ss1);
        chainKey = ck1;
        var encryptedStatic = WireGuardNoise.AeadSeal(key1, _clientStaticPublic, hash);
        hash = WireGuardNoise.MixHash(hash, encryptedStatic);

        // ENCRYPT timestamp: DH(s_i, s_R)
        if (!WireGuardNoise.SharedSecret(_clientPrivateKey, _serverPublicKey, out var ss2))
        {
            throw new InvalidOperationException("İstemci/sunucu anahtar çifti geçersiz (DH sıfır).");
        }
        var (ck2, key2) = WireGuardNoise.Kdf2(chainKey, ss2);
        chainKey = ck2;
        var encryptedTimestamp = WireGuardNoise.AeadSeal(key2, WireGuardNoise.Tai64nNow(), hash);
        hash = WireGuardNoise.MixHash(hash, encryptedTimestamp);

        _localIndex = RandomIndex();
        var msg = new byte[WireGuardNoise.InitiationSize];
        WireGuardNoise.WriteUint32(msg, 0, WireGuardNoise.MessageTypeInitiation);
        WireGuardNoise.WriteUint32(msg, 4, _localIndex);
        Buffer.BlockCopy(ephemeralPublic, 0, msg, 8, 32);
        Buffer.BlockCopy(encryptedStatic, 0, msg, 40, encryptedStatic.Length);
        Buffer.BlockCopy(encryptedTimestamp, 0, msg, 88, encryptedTimestamp.Length);

        // MAC1 — her zaman; MAC2 — çerez varken.
        var mac1 = WireGuardNoise.ComputeMac1(_mac1Key, msg, 116);
        Buffer.BlockCopy(mac1, 0, msg, 116, 16);
        _lastMac1 = mac1;
        _hasLastMac1 = true;
        if (HasFreshCookie && _cookie is not null)
        {
            var mac2 = WireGuardNoise.ComputeMac1(_cookie, msg, 132);
            Buffer.BlockCopy(mac2, 0, msg, 132, 16);
        }

        _hash = hash;
        _chainKey = chainKey;
        _localEphemeralPrivate = ephemeralPrivate;
        return msg;
    }

    /// <summary>
    /// Sunucudan gelen handshake response'u (92 bayt) tüketir ve oturum anahtarlarını
    /// türetir. MAC1 (gerçek sunucu kanıtı) + Empty alanı (transkript doğrulaması)
    /// başarısızsa null. wireguard-go ConsumeMessageResponse + BeginSymmetricSession.
    /// </summary>
    public WireGuardSession? ConsumeResponse(byte[] packet)
    {
        if (packet.Length != WireGuardNoise.ResponseSize)
        {
            return null;
        }
        if (WireGuardNoise.ReadUint32(packet, 0) != WireGuardNoise.MessageTypeResponse)
        {
            return null;
        }
        if (WireGuardNoise.ReadUint32(packet, 8) != _localIndex)
        {
            return null; // bu el sıkışmaya yanıt değil
        }
        if (_hash is null || _chainKey is null || _localEphemeralPrivate is null)
        {
            return null; // önce initiation üretilmiş olmalı
        }

        // MAC1: yanıt, bizim statik genel anahtarımıza dayalı anahtarla imzalanır —
        // gerçek sunucudan geldiğinin kesin kanıtı (sahte paketle taklit edilemez).
        if (!WireGuardNoise.VerifyResponseMac1(packet, _clientStaticPublic))
        {
            return null;
        }

        var hash = (byte[])_hash.Clone();
        var chainKey = (byte[])_chainKey.Clone();
        var remoteEphemeral = packet[12..44];

        hash = WireGuardNoise.MixHash(hash, remoteEphemeral);
        chainKey = WireGuardNoise.Kdf1(chainKey, remoteEphemeral);

        // ee: DH(e_i, e_R)
        if (!WireGuardNoise.SharedSecret(_localEphemeralPrivate, remoteEphemeral, out var ssA))
        {
            return null;
        }
        chainKey = WireGuardNoise.Kdf1(chainKey, ssA);

        // se: DH(s_i, e_R)
        if (!WireGuardNoise.SharedSecret(_clientPrivateKey, remoteEphemeral, out var ssB))
        {
            return null;
        }
        chainKey = WireGuardNoise.Kdf1(chainKey, ssB);

        // psk: KDF3(chainKey, psk) → (chainKey, tau, key)
        var (ck, tau, key) = WireGuardNoise.Kdf3(chainKey, _psk);
        chainKey = ck;
        hash = WireGuardNoise.MixHash(hash, tau);

        // Empty alanı — transkriptin sunucu tarafından doğrulandığını kanıtlar.
        if (!WireGuardNoise.AeadOpen(key, packet[44..60], hash, out _))
        {
            return null;
        }
        hash = WireGuardNoise.MixHash(hash, packet[44..60]);

        // BeginSymmetricSession (initiator): KDF2(chainKey, "") → (send, recv).
        var (sendKey, recvKey) = WireGuardNoise.Kdf2(chainKey, Array.Empty<byte>());

        // El sıkışma durumunu temizle — oturum kuruldu.
        _hash = null;
        _chainKey = null;
        _localEphemeralPrivate = null;

        return new WireGuardSession(sendKey, recvKey, _localIndex, WireGuardNoise.ReadUint32(packet, 4), isInitiator: true);
    }

    /// <summary>
    /// Cookie reply'ı (64 bayt) çözer: cookie = XChaCha20-Poly1305(key=cookieKey,
    /// AAD=son MAC1). Başarılıysa cookie saklanır ve sonraki initiation MAC2'li gider.
    /// </summary>
    public bool ConsumeCookieReply(byte[] packet)
    {
        if (packet.Length != WireGuardNoise.CookieReplySize)
        {
            return false;
        }
        if (WireGuardNoise.ReadUint32(packet, 0) != WireGuardNoise.MessageTypeCookieReply)
        {
            return false;
        }
        if (WireGuardNoise.ReadUint32(packet, 4) != _localIndex)
        {
            return false;
        }
        if (!_hasLastMac1 || _lastMac1 is null)
        {
            return false;
        }

        var nonce = packet[8..32];       // 24 bayt XNonce
        var cookieCt = packet[32..64];   // cookie + tag
        if (!WireGuardNoise.XAeadOpen(_cookieKey, nonce, cookieCt, _lastMac1, out var cookie))
        {
            return false;
        }
        if (cookie.Length != 16)
        {
            return false;
        }

        _cookie = cookie;
        _cookieSetAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>LocalIndex için rasgele sender index (little-endian uint32).</summary>
    private static uint RandomIndex()
    {
        var buf = new byte[4];
        RandomNumberGenerator.Fill(buf);
        return WireGuardNoise.ReadUint32(buf, 0);
    }
}

/// <summary>
/// IWireGuardTransport'un gerçek veri düzlemi uygulaması: ConnectAsync ile
/// İtalya/Almanya sunucusuna karşı el sıkışmayı tamamlar (cookie retry dahil),
/// ardından Encrypt/Decrypt WireGuardSession üzerinden tip-4 mesajları işler.
/// Oturum yokken (bağlantı kurulmamış) Encrypt/Decrypt null döner — köprü
/// paketi atlar, sayaç tutar.
/// </summary>
public sealed class WireGuardNoiseTransport : IWireGuardTransport, IDisposable
{
    private readonly WireGuardHandshakeClient _handshake;
    private readonly object _gate = new();
    private WireGuardSession? _session;
    private UdpClient? _udp; // Faz 2d: el sıkışma sonrası CANLI kalan UDP soketi
    private long _encrypted;
    private long _decrypted;
    private long _decryptFailed;
    private long _sent;
    private long _sendFailed;
    private int _attempts;

    /// <summary>Anahtarlar WireGuard .conf formatında (base64).</summary>
    public WireGuardNoiseTransport(byte[] clientPrivateKey, byte[] serverPublicKey, byte[]? psk = null)
        : this(new WireGuardHandshakeClient(clientPrivateKey, serverPublicKey, psk))
    {
    }

    /// <summary>Test enjeksiyonu: hazır handshake durum makinesi verilir.</summary>
    internal WireGuardNoiseTransport(WireGuardHandshakeClient handshake)
    {
        _handshake = handshake;
    }

    /// <summary>El sıkışma tamamlandı mı (oturum anahtarları hazır mı).</summary>
    public bool SessionEstablished
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>Bağlantı kurmak için yapılan toplam el sıkışma denemesi sayısı.</summary>
    public int HandshakeAttempts => _attempts;

    public long EncryptedCount => Interlocked.Read(ref _encrypted);
    public long DecryptedCount => Interlocked.Read(ref _decrypted);

    /// <summary>Çözülemeyen (bozuk/tekrar) mesaj sayısı — telemetri.</summary>
    public long DecryptFailedCount => Interlocked.Read(ref _decryptFailed);

    /// <summary>Sunucuya gönderilen tip-4 mesaj sayısı — telemetri.</summary>
    public long SentCount => Interlocked.Read(ref _sent);

    /// <summary>Gönderilemeyen (oturum yok / soket hatası) mesaj sayısı — telemetri.</summary>
    public long SendFailedCount => Interlocked.Read(ref _sendFailed);

    /// <summary>Oturum + UDP soketi canlı mı (veri yolu kullanılabilir mi).</summary>
    public bool IsDataPathActive
    {
        get
        {
            lock (_gate)
            {
                return _session is not null && _udp is not null;
            }
        }
    }

    /// <summary>
    /// Sunucuya karşı el sıkışmayı tamamlar: initiation gönder → yanıt (response
    /// veya cookie reply) → gerekirse MAC2'li yeniden gönderim. Başarılıysa oturum
    /// kurulur ve Encrypt/Decrypt canlıya geçer.
    /// </summary>
    public async Task<bool> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        const int maxAttempts = 3;

        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(host, port);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _attempts);

            var initiation = _handshake.CreateInitiation();
            await udp.SendAsync(initiation, cancellationToken).ConfigureAwait(false);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                while (!window.IsCancellationRequested)
                {
                    var datagram = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                    var type = WireGuardNoise.PeekMessageType(datagram.Buffer);

                    if (type == WireGuardNoise.MessageTypeResponse)
                    {
                        var session = _handshake.ConsumeResponse(datagram.Buffer);
                        if (session is null)
                        {
                            udp.Dispose();
                            return false; // MAC1/transkript hatası — gerçek sunucu değil
                        }
                        lock (_gate)
                        {
                            _session = session;
                            _udp = udp; // Faz 2d: soket CANLI kalır — veri yolu bu soketten akar
                        }
                        return true;
                    }

                    if (type == WireGuardNoise.MessageTypeCookieReply)
                    {
                        // Yük altındaki sunucu: MAC1'i kabul etti ama MAC2 bekliyor.
                        // Cookie'yi çöz, bir sonraki initiation'ı MAC2 ile gönder.
                        _handshake.ConsumeCookieReply(datagram.Buffer);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Kapanış sinyali: dismiss signal — handshaking'i bitir, yukarıdaki while döngüsüne
                    // ``cancellationToken.IsCancellationRequested == true`` ile girilir ve döngü sonlanır.
                    udp.Dispose();
                    return false;
                }
                // Pencere doldu (yanıt yok) — yeni initiation ile yeniden dene.
            }
            catch (SocketException)
            {
                udp.Dispose();
                return false; // ICMP unreachable veya ağ hatası
            }
        }

        udp.Dispose();
        return false;
    }

    // ── Faz 2d: kalıcı UDP veri yolu ─────────────────────────────────────

    /// <summary>
    /// Şifrelenmiş tip-4 mesajı gerçek sunucuya gönderir. Veri yolu kapalıysa
    /// (oturum/soket yok) veya soket hatası olursa false — köprü sayaçta işler.
    /// </summary>
    public async ValueTask<bool> SendAsync(byte[] wirePacket, CancellationToken cancellationToken = default)
    {
        var udp = GetDataSocket();
        if (udp is null)
        {
            Interlocked.Increment(ref _sendFailed);
            return false;
        }

        try
        {
            await udp.SendAsync(wirePacket, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _sent);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Interlocked.Increment(ref _sendFailed);
            return false;
        }
    }

    /// <summary>
    /// Sunucudan gelen tip-4 mesajları UDP üzerinden alır, çözer ve tüketiciye
    /// verir. Oturum/soket henüz yoksa (el sıkışma sürüyor) bekler; iptalde veya
    /// soket kapanınca temiz biter. Boş içerik (keepalive) da tüketiciye gider —
    /// çağıran (WireGuardTunnelService) 0-bayt paketi adaptöre enjekte etmez.
    /// </summary>
    public async Task RunReceiveLoopAsync(
        Func<byte[], CancellationToken, ValueTask> consumer,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var udp = GetDataSocket();
                if (udp is null || GetSession() is null)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false); // el sıkışma bitene dek bekle
                    continue;
                }

                UdpReceiveResult datagram;
                try
                {
                    datagram = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // İptal VE kapanış yarışı (soket dispose edilirken bekleyen
                    // ReceiveAsync dahili token ile OCE fırlatabilir) — ikisi de
                    // normal kapanıştır. `when` filtresi KONULMAZ: tek bir istisna
                    // bile AppDomain'e kaçarsa uygulama çöker
                    // (CurrentDomain_UnhandledException — rota değişiminde görüldü).
                    break;
                }
                catch (SocketException)
                {
                    break; // soket kapandı
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var type = WireGuardNoise.PeekMessageType(datagram.Buffer);
                if (type != WireGuardNoise.MessageTypeTransport)
                {
                    continue; // el sıkışma sonrası gelmemeli ama güvenli atla
                }

                var decrypted = Decrypt(datagram.Buffer);
                if (decrypted is null)
                {
                    continue; // decrypt hatası zaten DecryptFailedCount'a işlendi
                }
                try
                {
                    await consumer(decrypted, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Tüketici iptali (kapanış sırasında beklenir) — döngü temiz biter.
                    break;
                }
                catch (Exception ex)
                {
                    // Tüketici hatası döngüyü öldürmemeli: günlüğe düş, bir sonraki
                    // paketle devam et (tünel ayakta kalır).
                    Logging.SaveLog($"WireGuardNoiseTransport.Consumer: {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
        catch (Exception ex)
        {
            // Beklenmeyen hata asla dışarı kaçmaz — AppDomain seviyesinde çökme
            // olmaz; neden günlüğe düşer (kök nedeni takip edilebilir kalır).
            Logging.SaveLog($"WireGuardNoiseTransport.ReceiveLoop: {ex}");
        }
    }

    /// <summary>Oturum kurulmuşsa UDP soketini döndürür (veri yolu).</summary>
    private UdpClient? GetDataSocket()
    {
        lock (_gate)
        {
            if (_session is null || _udp is null)
            {
                return null;
            }
            return _udp;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _udp?.Dispose();
            _udp = null;
            _session = null;
        }
    }

    public byte[]? Encrypt(ReadOnlySpan<byte> packet)
    {
        var session = GetSession();
        var result = session?.Encrypt(packet);
        if (result is not null)
        {
            Interlocked.Increment(ref _encrypted);
        }
        return result;
    }

    public byte[]? Decrypt(ReadOnlySpan<byte> packet)
    {
        var session = GetSession();
        var result = session?.Decrypt(packet);
        if (result is not null)
        {
            Interlocked.Increment(ref _decrypted);
        }
        else
        {
            Interlocked.Increment(ref _decryptFailed);
        }
        return result;
    }

    private WireGuardSession? GetSession()
    {
        lock (_gate)
        {
            return _session;
        }
    }
}
