using System.Net.Sockets;

namespace ServiceLib.Services;

/// <summary>
/// Çekirdek kurtarma / soft-reload pencerelerinde oturumların hayatta kalıp
/// kalmadığını ölçen prob (Faz 1 — docs/session-continuity-design.md, L3-2).
///
/// Yerel mihomo SOCKS/mixed-port dinleyicisi üzerinden iki sürekli bacak tutar:
///   • TCP — SOCKS5 CONNECT + <c>GET /generate_204</c> (uçtan uca yanıt doğrular)
///   • UDP — SOCKS5 UDP ASSOCIATE + DNS sorgusu (uçtan uca yanıt doğrular)
///
/// Bacaklar ÇEKİRDEK DIŞINDA (bu serviste) yaşadığı için çekirdek yeniden
/// başlatılınca bacak durumu oturumların GERÇEKTEN kesilip kesilmediğini söyler:
/// çekirdek öldüyse dinleyiciyi tutan süreç gittiğinden bacak RST/FIN ile
/// kapanır; soft reload (aynı süreç) bacakları etkilemez. Çekirdek sağlığı
/// Ready → (Starting|Degraded|Failed) → Ready geçişinde değerlendirme yapılır,
/// sonuç DiagLog'a <c>SURVIVAL tcp=… udp=… gapMs=…</c> olarak yazılır ve
/// <see cref="LastEvaluation"/> ile okunur. Kullanıcının bilinçli koparması
/// (Stopped) değerlendirmeye girmez.
///
/// Not: TUN adaptörü yeniden kurulursa TUN üzerindeki oturumlar bu probun
/// kapsamı dışında kalır (prob SOCKS girişinden ölçer) — TUN katmanı ölçümü
/// ileride Wintun soketi ile eklenebilir; prob, süreç katmanı oturum
/// sürekliliğinin (asıl düzeltilebilir kısım) doğrulayıcısıdır.
/// </summary>
public sealed class SessionSurvivalProbeService : IDisposable
{
    /// <summary>Tek bir kurtarma penceresi değerlendirmesinin sonucu.</summary>
    public sealed record Evaluation(
        DateTimeOffset At,
        long GapMs,
        bool TcpTried,
        bool TcpSurvived,
        bool UdpTried,
        bool UdpSurvived);

    private static readonly Lazy<SessionSurvivalProbeService> _instance = new(() => new(
        socksPortProvider: () => AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
        tcpHost: "www.gstatic.com",
        tcpPort: 80,
        udpHost: "8.8.8.8",
        udpPort: 53));

    /// <summary>Uygulama geneli tek örnek (MainWindow açılışında Start()).</summary>
    public static SessionSurvivalProbeService Instance => _instance.Value;

    private const string Tag = "SURVIVAL";
    private static readonly byte[] DnsQueryPayload = BuildDnsQuery(0xA0A1, "www.example.com");

    private readonly Func<int> _socksPortProvider;
    private readonly string _tcpHost;
    private readonly int _tcpPort;
    private readonly string _udpHost;
    private readonly int _udpPort;
    private readonly int _timeoutMs;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private IDisposable? _subscription;
    private TcpClient? _tcpLeg;
    private NetworkStream? _tcpLegStream;
    private UdpClient? _udpLeg;
    private TcpClient? _udpControl;
    private IPEndPoint? _udpRelayEp;
    private bool _wasReady;
    private DateTimeOffset? _gapSince;
    private Evaluation? _lastEvaluation;

    public SessionSurvivalProbeService(
        Func<int> socksPortProvider,
        string tcpHost,
        int tcpPort,
        string udpHost,
        int udpPort,
        int timeoutMs = 3000)
    {
        _socksPortProvider = socksPortProvider;
        _tcpHost = tcpHost;
        _tcpPort = tcpPort;
        _udpHost = udpHost;
        _udpPort = udpPort;
        _timeoutMs = timeoutMs;
    }

    public Evaluation? LastEvaluation
    {
        get
        {
            lock (_gate)
            {
                return _lastEvaluation;
            }
        }
    }

    /// <summary>Test gözlemi: TCP + UDP bacakları şu an kurulu mu?</summary>
    internal bool HasLegs
    {
        get
        {
            lock (_gate)
            {
                return _tcpLeg is { Connected: true }
                    && _tcpLegStream is not null
                    && _udpLeg is not null
                    && _udpControl is { Connected: true }
                    && _udpRelayEp is not null;
            }
        }
    }

    /// <summary>Sağlık olaylarına abone olur; zaten Ready olan çekirdek için bacakları kurar.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_subscription != null)
            {
                return;
            }
            _subscription = AppEvents.CoreHealthChanged.AsObservable().Subscribe(OnHealthChanged);
        }
        var current = CoreManager.Instance.GetHealth(CoreHealthRole.Main);
        if (current.IsReady)
        {
            OnHealthChanged(current);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
            DisposeLegsLocked();
            _wasReady = false;
            _gapSince = null;
        }
    }

    public void Dispose() => Stop();

    // ── sağlık olayı → durum makinesi ──────────────────────────────────────

    private void OnHealthChanged(CoreHealthSnapshot health)
    {
        if (health.Role != CoreHealthRole.Main)
        {
            return;
        }

        long? gapMs = null;
        var needsLegs = false;
        lock (_gate)
        {
            if (health.IsReady)
            {
                if (_gapSince is { } gap)
                {
                    gapMs = (long)(DateTimeOffset.UtcNow - gap).TotalMilliseconds;
                    _gapSince = null;
                }
                needsLegs = true;
                _wasReady = true;
            }
            else if (health.State == CoreHealthState.Stopped)
            {
                // Kullanıcının bilinçli koparması: değerlendirme YAPILMAZ,
                // bacaklar atılır — bir sonraki bağlantı temiz başlar.
                DisposeLegsLocked();
                _wasReady = false;
                _gapSince = null;
                return;
            }
            else if (_wasReady && _gapSince == null)
            {
                // Kurtarma penceresi başladı (Starting/Degraded/Failed).
                _gapSince = DateTimeOffset.UtcNow;
            }
        }

        if (gapMs != null)
        {
            _ = Task.Run(() => EvaluateAndRefreshAsync(gapMs.Value));
        }
        else if (needsLegs)
        {
            _ = EnsureLegsAsync();
        }
    }

    // ── değerlendirme + bacak bakımı ───────────────────────────────────────

    private async Task EvaluateAndRefreshAsync(long gapMs)
    {
        await _work.WaitAsync();
        try
        {
            var tcpTried = false;
            var tcpSurvived = false;
            var udpTried = false;
            var udpSurvived = false;

            // Önce GAP ÖNCESİ bacakları değerlendir — soru "eski oturum koptu mu?"
            var (tcpLeg, tcpStream) = TakeTcpLeg();
            if (tcpLeg is not null && tcpStream is not null)
            {
                tcpTried = true;
                tcpSurvived = await TcpRoundTripAsync(tcpStream, _tcpHost, _tcpPort, _timeoutMs);
            }
            var (udpLeg, udpControl, udpRelayEp) = TakeUdpLeg();
            if (udpLeg is not null && udpControl is not null && udpRelayEp is not null)
            {
                udpTried = true;
                udpSurvived = await UdpRoundTripAsync(udpLeg, udpRelayEp, _udpHost, _udpPort, _timeoutMs);
            }
            try
            {
                tcpStream?.Dispose();
                tcpLeg?.Dispose();
                udpControl?.Dispose();
                udpLeg?.Dispose();
            }
            catch
            {
                // Bacak zaten ölmüş olabilir (çekirdek çökmesi) — temizlik isteğe bağlı.
            }

            var evaluation = new Evaluation(
                At: DateTimeOffset.UtcNow,
                GapMs: gapMs,
                TcpTried: tcpTried,
                TcpSurvived: tcpSurvived,
                UdpTried: udpTried,
                UdpSurvived: udpSurvived);
            lock (_gate)
            {
                _lastEvaluation = evaluation;
            }
            DiagLog.Write(
                $"{Tag} tcp={(tcpTried ? (tcpSurvived ? "survived" : "cut") : "n/a")} " +
                $"udp={(udpTried ? (udpSurvived ? "survived" : "cut") : "n/a")} gapMs={gapMs}");

            // Değerlendirme bitti: eski bacaklar artık geçersiz, yenilerini kur.
            await EnsureLegsCoreAsync();
        }
        finally
        {
            _work.Release();
        }
    }

    private Task EnsureLegsAsync() => Task.Run(async () =>
    {
        await _work.WaitAsync();
        try
        {
            await EnsureLegsCoreAsync();
        }
        finally
        {
            _work.Release();
        }
    });

    private async Task EnsureLegsCoreAsync()
    {
        var socksPort = _socksPortProvider();
        if (socksPort <= 0)
        {
            return;
        }

        var tcpOk = false;
        lock (_gate)
        {
            tcpOk = _tcpLeg is { Connected: true };
        }
        if (!tcpOk)
        {
            var (client, stream) = await TrySocksConnectAsync(socksPort, _tcpHost, _tcpPort, _timeoutMs);
            if (client is not null)
            {
                lock (_gate)
                {
                    DisposeTcpLegLocked();
                    _tcpLeg = client;
                    _tcpLegStream = stream;
                }
            }
            else
            {
                DiagLog.Write($"{Tag} tcp leg kurulamadı (socks={socksPort})");
            }
        }

        var udpOk = false;
        lock (_gate)
        {
            udpOk = _udpLeg is not null && _udpControl is { Connected: true };
        }
        if (!udpOk)
        {
            var (relay, relayEp, control) = await TrySocksUdpAssociateAsync(socksPort, _timeoutMs);
            if (relay is not null)
            {
                lock (_gate)
                {
                    DisposeUdpLegLocked();
                    _udpLeg = relay;
                    _udpRelayEp = relayEp;
                    _udpControl = control;
                }
            }
            else
            {
                DiagLog.Write($"{Tag} udp leg kurulamadı (socks={socksPort})");
            }
        }
    }

    // ── bacak alma/atma ────────────────────────────────────────────────────

    private (TcpClient? Client, NetworkStream? Stream) TakeTcpLeg()
    {
        lock (_gate)
        {
            var client = _tcpLeg;
            var stream = _tcpLegStream;
            _tcpLeg = null;
            _tcpLegStream = null;
            return (client, stream);
        }
    }

    private (UdpClient? Relay, TcpClient? Control, IPEndPoint? RelayEp) TakeUdpLeg()
    {
        lock (_gate)
        {
            var relay = _udpLeg;
            var control = _udpControl;
            var relayEp = _udpRelayEp;
            _udpLeg = null;
            _udpControl = null;
            _udpRelayEp = null;
            return (relay, control, relayEp);
        }
    }

    private void DisposeLegsLocked()
    {
        DisposeTcpLegLocked();
        DisposeUdpLegLocked();
    }

    private void DisposeTcpLegLocked()
    {
        try
        {
            _tcpLegStream?.Dispose();
        }
        catch
        {
        }
        try
        {
            _tcpLeg?.Dispose();
        }
        catch
        {
        }
        _tcpLeg = null;
        _tcpLegStream = null;
    }

    private void DisposeUdpLegLocked()
    {
        try
        {
            _udpLeg?.Dispose();
        }
        catch
        {
        }
        try
        {
            _udpControl?.Dispose();
        }
        catch
        {
        }
        _udpLeg = null;
        _udpControl = null;
        _udpRelayEp = null;
    }

    // ── SOCKS5 istemcisi ───────────────────────────────────────────────────

    private static async Task<(TcpClient? Client, NetworkStream? Stream)> TrySocksConnectAsync(
        int socksPort, string host, int port, int timeoutMs)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, socksPort)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            var stream = client.GetStream();

            // Greeting: SOCKS5, no-auth.
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 })
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            var greetReply = new byte[2];
            if (!await ReadExactlyAsync(stream, greetReply, timeoutMs)
                || greetReply[0] != 0x05 || greetReply[1] != 0x00)
            {
                client.Dispose();
                return (null, null);
            }

            // CONNECT <host>:<port> (ATYP=domain).
            var hostBytes = Encoding.ASCII.GetBytes(host);
            var request = new List<byte> { 0x05, 0x01, 0x00, 0x03, (byte)hostBytes.Length };
            request.AddRange(hostBytes);
            // & 0xFF: CheckForOverflowUnderflow açık — checked bağlamda 255 üstü
            // int→byte dökümü OverflowException fırlatır.
            request.AddRange(new byte[] { (byte)((port >> 8) & 0xFF), (byte)(port & 0xFF) });
            await stream.WriteAsync(request.ToArray())
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

            var header = new byte[4];
            if (!await ReadExactlyAsync(stream, header, timeoutMs)
                || header[0] != 0x05 || header[1] != 0x00)
            {
                client.Dispose();
                return (null, null);
            }
            var addrLen = header[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => await ReadOneByteAsync(stream, timeoutMs),
                _ => -1,
            };
            if (addrLen < 0 || !await ReadExactlyAsync(stream, new byte[addrLen + 2], timeoutMs))
            {
                client.Dispose();
                return (null, null);
            }
            return (client, stream);
        }
        catch
        {
            try
            {
                client?.Dispose();
            }
            catch
            {
            }
            return (null, null);
        }
    }

    private static async Task<(UdpClient? Relay, IPEndPoint? RelayEp, TcpClient? Control)>
        TrySocksUdpAssociateAsync(int socksPort, int timeoutMs)
    {
        TcpClient? control = null;
        UdpClient? relay = null;
        try
        {
            control = new TcpClient { NoDelay = true };
            await control.ConnectAsync(IPAddress.Loopback, socksPort)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            var stream = control.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 })
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            var greetReply = new byte[2];
            if (!await ReadExactlyAsync(stream, greetReply, timeoutMs)
                || greetReply[0] != 0x05 || greetReply[1] != 0x00)
            {
                control.Dispose();
                return (null, null, null);
            }

            // UDP ASSOCIATE: hedef "0.0.0.0:0" → çekirdek kendi relay adresini döner.
            await stream.WriteAsync(new byte[] { 0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0 })
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            var header = new byte[4];
            if (!await ReadExactlyAsync(stream, header, timeoutMs)
                || header[0] != 0x05 || header[1] != 0x00)
            {
                control.Dispose();
                return (null, null, null);
            }

            IPAddress boundAddr;
            switch (header[3])
            {
                case 0x01:
                {
                    var addrBytes = new byte[4];
                    if (!await ReadExactlyAsync(stream, addrBytes, timeoutMs))
                    {
                        control.Dispose();
                        return (null, null, null);
                    }
                    boundAddr = new IPAddress(addrBytes);
                    break;
                }
                case 0x04:
                {
                    var addrBytes = new byte[16];
                    if (!await ReadExactlyAsync(stream, addrBytes, timeoutMs))
                    {
                        control.Dispose();
                        return (null, null, null);
                    }
                    boundAddr = new IPAddress(addrBytes);
                    break;
                }
                default:
                    control.Dispose();
                    return (null, null, null);
            }
            var portBytes = new byte[2];
            if (!await ReadExactlyAsync(stream, portBytes, timeoutMs))
            {
                control.Dispose();
                return (null, null, null);
            }
            var relayEp = new IPEndPoint(boundAddr, (portBytes[0] << 8) | portBytes[1]);
            relay = new UdpClient();
            return (relay, relayEp, control);
        }
        catch
        {
            try
            {
                relay?.Dispose();
                control?.Dispose();
            }
            catch
            {
            }
            return (null, null, null);
        }
    }

    // ── uçtan uca yanıt doğrulama ──────────────────────────────────────────

    private static async Task<bool> TcpRoundTripAsync(NetworkStream stream, string host, int port, int timeoutMs)
    {
        try
        {
            var request = $"GET /generate_204 HTTP/1.1\r\n" +
                          $"Host: {host}\r\n" +
                          "User-Agent: AoGPN-SurvivalProbe/1.0\r\n" +
                          "Connection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(bytes).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer.AsMemory())
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            if (read <= 0)
            {
                return false;
            }
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            return text.StartsWith("HTTP/", StringComparison.Ordinal)
                && text.Contains(" 204 ", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> UdpRoundTripAsync(
        UdpClient relay, IPEndPoint relayEp, string host, int port, int timeoutMs)
    {
        try
        {
            var frame = FrameUdpDatagram(DnsQueryPayload, host, port);
            await relay.SendAsync(frame, relayEp).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await relay.ReceiveAsync(cts.Token);
            return IsDnsResponse(response.Buffer, DnsQueryPayload);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDnsResponse(byte[] packet, byte[] query)
    {
        // SOCKS5 UDP çerçevesini ayıkla (RSV FRAG ATYP …).
        var offset = 0;
        if (packet.Length < 10 || packet[0] != 0x00 || packet[1] != 0x00 || packet[2] != 0x00)
        {
            return false;
        }
        offset = 4;
        switch (packet[3])
        {
            case 0x01:
                offset += 4;
                break;
            case 0x04:
                offset += 16;
                break;
            case 0x03:
                offset += 1 + packet[4];
                break;
            default:
                return false;
        }
        offset += 2; // port

        var payloadLen = packet.Length - offset;
        if (payloadLen < 12)
        {
            return false;
        }
        // ID eşleşmeli + yanıt biti (QR) set olmalı.
        return packet[offset] == query[0]
            && packet[offset + 1] == query[1]
            && (packet[offset + 2] & 0x80) != 0;
    }

    private static byte[] FrameUdpDatagram(byte[] payload, string host, int port)
    {
        var frame = new List<byte> { 0x00, 0x00, 0x00 };
        if (IPAddress.TryParse(host, out var ipv4) && ipv4.AddressFamily == AddressFamily.InterNetwork)
        {
            frame.Add(0x01);
            frame.AddRange(ipv4.GetAddressBytes());
        }
        else
        {
            var hostBytes = Encoding.ASCII.GetBytes(host);
            frame.Add(0x03);
            frame.Add((byte)hostBytes.Length);
            frame.AddRange(hostBytes);
        }
        frame.Add((byte)((port >> 8) & 0xFF));
        frame.Add((byte)(port & 0xFF));
        frame.AddRange(payload);
        return frame.ToArray();
    }

    private static byte[] BuildDnsQuery(ushort id, string domain)
    {
        var query = new List<byte>
        {
            // & 0xFF: checked bağlamda int→byte dökümü yalnızca ≤255 değerlerde geçerli.
            (byte)((id >> 8) & 0xFF), (byte)(id & 0xFF), // ID
            0x01, 0x00, // flags: RD
            0x00, 0x01, // QDCOUNT
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        foreach (var label in domain.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            query.Add((byte)bytes.Length);
            query.AddRange(bytes);
        }
        query.Add(0x00); // root
        query.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 }); // A, IN
        return query.ToArray();
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
                if (n <= 0)
                {
                    return false;
                }
                read += n;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> ReadOneByteAsync(NetworkStream stream, int timeoutMs)
    {
        var buffer = new byte[1];
        return await ReadExactlyAsync(stream, buffer, timeoutMs) ? buffer[0] : -1;
    }
}