using System.Net;
using System.Net.Sockets;
using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Oturum hayatta kalma probu testleri. Gerçek ağ üzerinden değil, yerel bir
/// sahte mihomo çekirdeği (SOCKS5 dinleyicisi + HTTP-204 yankısı + SOCKS5 UDP
/// relay) üzerinden doğrulanır:
///   • Survived: sahte çekirdek yumuşak yeniden yüklemeyi taklit eder (bağlantılar
///     AÇIK kalır) → prob bacakların ayakta kaldığını raporlamalı.
///   • Cut: sahte çekirdek ÇÖKER (tüm bağlantılar + UDP relay kapanır) ve yeniden
///     başlar → prob bacakların koptuğunu raporlamalı.
///   • Kullanıcı koparması (Stopped) değerlendirme üretmemeli.
/// </summary>
public class SessionSurvivalProbeServiceTests : IDisposable
{
    private readonly FakeMihomoCore _fake = new();
    private readonly SessionSurvivalProbeService _probe;

    public SessionSurvivalProbeServiceTests()
    {
        _probe = new SessionSurvivalProbeService(
            socksPortProvider: () => _fake.SocksPort,
            tcpHost: "fake.test",
            tcpPort: 80,
            udpHost: "8.8.8.8",
            udpPort: 53,
            timeoutMs: 1500);
        _probe.Start();
    }

    public void Dispose()
    {
        _probe.Dispose();
        _fake.Dispose();
    }

    private static void Publish(CoreHealthState state, bool recovering = false) =>
        AppEvents.CoreHealthChanged.Publish(new CoreHealthSnapshot(
            CoreHealthRole.Main, state, ECoreType.mihomo, 7890, recovering: recovering));

    private static async Task<SessionSurvivalProbeService.Evaluation> WaitEvaluationAsync(
        SessionSurvivalProbeService probe)
    {
        for (var i = 0; i < 100 && probe.LastEvaluation is null; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return probe.LastEvaluation
            ?? throw new InvalidOperationException("Probe evaluation timed out.");
    }

    private static async Task WaitLegsAsync(SessionSurvivalProbeService probe)
    {
        for (var i = 0; i < 100 && !probe.HasLegs; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        if (!probe.HasLegs)
        {
            throw new InvalidOperationException("Probe legs were not established.");
        }
    }

    [Fact]
    public async Task SoftReload_SameProcessKept_ReportsSurvived()
    {
        Publish(CoreHealthState.Ready);
        await WaitLegsAsync(_probe);

        // Yumuşak yeniden yükleme: süreç (sahte çekirdek) YERİNDE — bacaklar açık kalır.
        Publish(CoreHealthState.Starting);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Publish(CoreHealthState.Ready);

        var evaluation = await WaitEvaluationAsync(_probe);
        Assert.True(evaluation.TcpTried, "TCP bacağı denendi.");
        Assert.True(evaluation.TcpSurvived, "TCP oturumu soft reload'da yaşamalı.");
        Assert.True(evaluation.UdpTried, "UDP bacağı denendi.");
        Assert.True(evaluation.UdpSurvived, "UDP oturumu soft reload'da yaşamalı.");
        Assert.True(evaluation.GapMs >= 0);
    }

    [Fact]
    public async Task CrashRecovery_ProcessDied_ReportsCut()
    {
        Publish(CoreHealthState.Ready);
        await WaitLegsAsync(_probe);

        // Çekirdek çöker: tüm bağlantılar + UDP relay kapanır, dinleyici gider.
        Publish(CoreHealthState.Degraded, recovering: true);
        _fake.SimulateCrash();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        // Otomatik kurtarma: yeni süreç aynı portta ayağa kalkar.
        _fake.SimulateStart();
        Publish(CoreHealthState.Ready);

        var evaluation = await WaitEvaluationAsync(_probe);
        Assert.True(evaluation.TcpTried, "TCP bacağı denendi.");
        Assert.False(evaluation.TcpSurvived, "Çekirdek öldüyse TCP oturumu kopmuş olmalı.");
        Assert.True(evaluation.UdpTried, "UDP bacağı denendi.");
        Assert.False(evaluation.UdpSurvived, "Çekirdek öldüyse UDP oturumu kopmuş olmalı.");
    }

    [Fact]
    public async Task UserDisconnect_Stopped_NeverEvaluates()
    {
        Publish(CoreHealthState.Ready);
        await WaitLegsAsync(_probe);

        // Kullanıcı bilinçli koparma: değerlendirme ÜRETİLMEMELİ (kurtarma değil).
        Publish(CoreHealthState.Stopped);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(_probe.LastEvaluation);

        // Yeniden bağlanma sonrası da geçmiş bacak değerlendirilmez — temiz başlangıç.
        Publish(CoreHealthState.Starting);
        Publish(CoreHealthState.Ready);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(_probe.LastEvaluation);
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// mihomo mixed-port davranışını taklit eden yerel sahte çekirdek:
    ///   • TCP CONNECT → yerel HTTP-204 yankı sunucusuna köprü,
    ///   • UDP ASSOCIATE → her ilişkilendirme için AYRI bir relay soketi (gerçek
    ///     mihomo davranışı) — relay, DNS yanıtı kılığında yankı yapar.
    ///   • SimulateCrash: dinleyici + tüm bağlantılar/relay'ler kapanır (süreç ölümü),
    ///   • SimulateStart: aynı portta yeniden dinlemeye başlar (kurtarma).
    /// </summary>
    private sealed class FakeMihomoCore : IDisposable
    {
        private readonly TcpListener _echo;
        private readonly object _lock = new();
        private readonly List<Socket> _sockets = [];
        private TcpListener? _socks;
        private CancellationTokenSource _cts = new();

        public FakeMihomoCore()
        {
            _echo = new TcpListener(IPAddress.Loopback, 0);
            _echo.Start();
            _ = Task.Run(EchoLoopAsync);

            _socks = new TcpListener(IPAddress.Loopback, 0);
            _socks.Start();
            SocksPort = ((IPEndPoint)_socks.LocalEndpoint).Port;
            StartAccepting();
        }

        public int SocksPort { get; }

        public void SimulateCrash()
        {
            lock (_lock)
            {
                _cts.Cancel();
                _socks?.Stop();
                _socks = null;
                foreach (var socket in _sockets)
                {
                    try
                    {
                        socket.Close();
                    }
                    catch
                    {
                    }
                }
                _sockets.Clear();
            }
        }

        public void SimulateStart()
        {
            lock (_lock)
            {
                if (_socks is not null)
                {
                    return;
                }
                _socks = new TcpListener(IPAddress.Loopback, SocksPort);
                _socks.Start();
                StartAccepting();
            }
        }

        public void Dispose()
        {
            SimulateCrash();
            _echo.Stop();
        }

        private void StartAccepting()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    var listener = _socks;
                    if (listener is null)
                    {
                        return;
                    }
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch
                {
                    return;
                }
                var accepted = client;
                _ = Task.Run(() => HandleClientAsync(accepted));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    RegisterSocket(client.Client);
                    var stream = client.GetStream();

                    // Greeting: SOCKS5 no-auth.
                    var greet = new byte[3];
                    if (!await ReadExactlyAsync(stream, greet) || greet[0] != 0x05)
                    {
                        return;
                    }
                    await stream.WriteAsync(new byte[] { 0x05, 0x00 });

                    var header = new byte[4];
                    if (!await ReadExactlyAsync(stream, header) || header[0] != 0x05)
                    {
                        return;
                    }
                    var addrLen = header[3] switch
                    {
                        0x01 => 4,
                        0x04 => 16,
                        0x03 => await ReadOneByteAsync(stream),
                        _ => -1,
                    };
                    if (addrLen < 0 || !await ReadExactlyAsync(stream, new byte[addrLen + 2]))
                    {
                        return;
                    }

                    switch (header[1])
                    {
                        case 0x01: // CONNECT → HTTP-204 yankı köprüsü
                            await HandleConnectAsync(client, stream);
                            break;
                        case 0x03: // UDP ASSOCIATE → per-session relay
                            await HandleUdpAssociateAsync(client, stream);
                            break;
                    }
                }
            }
            catch
            {
                // Bağlantı çöktü/kapatıldı — temizlik dispose'ta.
            }
            finally
            {
                RemoveSocket(client.Client);
            }
        }

        private async Task HandleConnectAsync(TcpClient client, NetworkStream stream)
        {
            using var echo = new TcpClient();
            await echo.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_echo.LocalEndpoint).Port);
            var echoStream = echo.GetStream();
            RegisterSocket(echo.Client);

            var reply = new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 80 };
            await stream.WriteAsync(reply);

            // Çift yönlü köprü — çekirdek ölünce (SimulateCrash) bu bacak kapanır.
            var upstream = Task.Run(() => CopyAsync(stream, echoStream));
            var downstream = Task.Run(() => CopyAsync(echoStream, stream));
            await Task.WhenAny(upstream, downstream);
            try
            {
                echoStream.Close();
                client.Close();
            }
            catch
            {
            }
            await Task.WhenAll(upstream, downstream).WaitAsync(TimeSpan.FromSeconds(5));
        }

        private async Task HandleUdpAssociateAsync(TcpClient client, NetworkStream stream)
        {
            // Gerçek mihomo gibi her ilişkilendirme için AYRI relay soketi:
            // çekirdek ölünce bu soket kapanır → eski bacak yanıt alamaz (cut).
            using var relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            RegisterSocket(relay.Client);
            var relayPort = ((IPEndPoint)relay.Client.LocalEndPoint!).Port;

            var reply = new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1 };
            // & 0xFF: CheckForOverflowUnderflow açık — 255 üstü int→byte dökümü fırlatır.
            reply = [.. reply, (byte)((relayPort >> 8) & 0xFF), (byte)(relayPort & 0xFF)];
            await stream.WriteAsync(reply);

            _ = Task.Run(() => ServeUdpRelayAsync(relay));

            // Kontrol bağlantısını çekirdek ömrü boyunca AÇIK tut — okuma tarafı
            // yalnızca SimulateCrash'te kapanır (o da HandleClientAsync'ı sonlandırır).
            var buffer = new byte[1];
            try
            {
                // CA2022: ReadAsync kısmi okuma döndürebilir — bağlantı kapanana dek
                // bloke olmak için ReadExactlyAsync (kapanışta EndOfStreamException
                // fırlatır, aşağıdaki catch'e düşer).
                await stream.ReadExactlyAsync(buffer);
            }
            catch
            {
            }
        }

        private static async Task ServeUdpRelayAsync(UdpClient relay)
        {
            try
            {
                while (true)
                {
                    var result = await relay.ReceiveAsync();
                    var frame = result.Buffer;
                    var offset = 4;
                    switch (frame[3])
                    {
                        case 0x01:
                            offset += 4;
                            break;
                        case 0x04:
                            offset += 16;
                            break;
                        case 0x03:
                            offset += 1 + frame[4];
                            break;
                        default:
                            continue;
                    }
                    offset += 2;
                    if (frame.Length - offset < 12)
                    {
                        continue;
                    }
                    // Aynı ID + QR set + ANCOUNT=1 — probun doğrulaması bunlara bakar.
                    var reply = new byte[12];
                    Array.Copy(frame, offset, reply, 0, 2);
                    reply[2] = 0x81;
                    reply[3] = 0x80;
                    reply[5] = 0x01;
                    reply[7] = 0x01;

                    var response = new byte[offset + reply.Length];
                    Array.Copy(frame, response, offset);
                    Array.Copy(reply, 0, response, offset, reply.Length);
                    await relay.SendAsync(response, result.RemoteEndPoint);
                }
            }
            catch
            {
                // Relay kapatıldı (çekirdek çökmesi / dispose).
            }
        }

        private async Task EchoLoopAsync()
        {
            while (true)
            {
                TcpClient? client = null;
                try
                {
                    client = await _echo.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }
                var accepted = client;
                _ = Task.Run(() => ServeHttp204Async(accepted));
            }
        }

        private static async Task ServeHttp204Async(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    // İsteği sonuna kadar oku (probe GET /generate_204 gönderir).
                    var buffer = new byte[2048];
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read <= 0)
                        {
                            return;
                        }
                        var text = Encoding.ASCII.GetString(buffer, 0, read);
                        if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                    const string response = "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                    // Bağlantıyı çekirdek ölünceye dek AÇIK tut (probe bacağı buraya köprülü).
                    await Task.Delay(Timeout.Infinite);
                }
            }
            catch
            {
            }
        }

        private static async Task CopyAsync(NetworkStream from, NetworkStream to)
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await from.ReadAsync(buffer);
                if (read <= 0)
                {
                    return;
                }
                await to.WriteAsync(buffer.AsMemory(0, read));
                await to.FlushAsync();
            }
        }

        private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read));
                if (n <= 0)
                {
                    return false;
                }
                read += n;
            }
            return true;
        }

        private static async Task<int> ReadOneByteAsync(NetworkStream stream)
        {
            var buffer = new byte[1];
            return await ReadExactlyAsync(stream, buffer) ? buffer[0] : -1;
        }

        private void RegisterSocket(Socket socket)
        {
            lock (_lock)
            {
                _sockets.Add(socket);
            }
        }

        private void RemoveSocket(Socket socket)
        {
            lock (_lock)
            {
                _sockets.Remove(socket);
            }
        }
    }
}