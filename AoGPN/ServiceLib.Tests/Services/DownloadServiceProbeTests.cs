using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// <see cref="DownloadService.ProbeUrlAsync"/> için testler — bir kaynak adresinin
/// HTTP canlılığını nasıl sınıflandırdığımızı doğrular (GpnSourceCheckTool'un
/// dayandığı primitive):
///   • 2xx        → canlı (Ok=true, HttpStatus=200)
///   • 4xx/5xx    → erişilebilir ama ölü (Ok=false, HttpStatus dolu)
///   • ağ hatası  → erişilemez (Ok=false, HttpStatus=null)
///
/// Gerçek ağa bağlanmamak için yerel bir stub HTTP sunucusu (HamHttpServer)
/// kullanılır; böylece test deterministik ve çevredeki ağdan bağımsızdır.
/// </summary>
public class DownloadServiceProbeTests
{
    [Fact]
    public async Task ProbeUrl_Http200_ReportsLive()
    {
        using var server = await StubHttpServer.StartAsync(200);

        var result = await new DownloadService()
            .ProbeUrlAsync($"http://127.0.0.1:{server.Port}/sub.txt", (IWebProxy?)null, timeoutSec: 5);

        result.Ok.Should().BeTrue($"2xx yanıt canlı sayılmalı (hata: {result.Message})");
        result.HttpStatus.Should().Be(200);
        result.Message.Should().Be("200");
    }

    [Fact]
    public async Task ProbeUrl_Http500_ReportsDeadButReachable()
    {
        using var server = await StubHttpServer.StartAsync(500);

        var result = await new DownloadService()
            .ProbeUrlAsync($"http://127.0.0.1:{server.Port}/", (IWebProxy?)null, timeoutSec: 5);

        result.Ok.Should().BeFalse("5xx erişilebilir ama kaynak ölü sayılmalı");
        result.HttpStatus.Should().Be(500, "HTTP durum kodu korunmalı (LIVE yerine HTTP N gösterimi)");
    }

    [Fact]
    public async Task TryDownloadString_ProxyUnreachable_FallsBackToDirect()
    {
        // GPN oturumundaki hata senaryosu: yerel SOCKS proxy'si (core restart,
        // tünel döngüsü) ulaşılamaz — indirme yine de doğrudan tamamlanmalı.
        using var server = await StubHttpServer.StartAsync(200);
        var deadProxy = new WebProxy($"socks5://127.0.0.1:{GetFreePort()}");

        var result = await new DownloadService()
            .TryDownloadString($"http://127.0.0.1:{server.Port}/sub.txt", (IWebProxy?)deadProxy, "test");

        result.Should().NotBeNullOrEmpty("proxy ölüyken doğrudan yola düşülmeli");
        result.Should().Contain("OK");
    }

    [Fact]
    public async Task UrlRedirectAsync_ProxyUnreachable_FallsBackToDirect()
    {
        // Güncelleme kontrolü (github releases/latest) proxy üzerinden gidemezse
        // doğrudan yönlendirme hedefini almalı — "StatusCode error" yığını biter.
        const string location = "https://github.com/AhmetOzbay27/AoGPN/releases/tag/v1.1.1";
        using var server = await StubHttpServer.StartRedirectAsync(location);
        var deadProxy = new WebProxy($"socks5://127.0.0.1:{GetFreePort()}");

        var result = await new DownloadService()
            .UrlRedirectAsync($"http://127.0.0.1:{server.Port}/latest", (IWebProxy?)deadProxy);

        result.Should().Be(location);
    }

    [Fact]
    public async Task TryDownloadString_TransientFailure_Succeeds()
    {
        // Geçici arıza: ilk bağlantı yanıtsız kapatılır (ağ hatası). HttpClient
        // düşer, DownloaderHelper kendi iç yeniden denemesiyle 2. bağlantıda 200
        // alır — indirme geçici arızaya rağmen tamamlanır.
        using var server = await StubHttpServer.StartAsync(200, failFirst: 1);

        var result = await new DownloadService()
            .TryDownloadString($"http://127.0.0.1:{server.Port}/sub.txt", (IWebProxy?)null, "test");

        result.Should().NotBeNullOrEmpty("geçici arıza sonrası indirme tamamlanmalı");
        result.Should().Contain("OK");
        server.RequestCount.Should().Be(2, "1 yanıtsız + 1 başarılı bağlantı");
    }

    [Fact]
    public async Task TryDownloadString_PersistentFailure_ExhaustsAllAttempts()
    {
        // Sunucu her bağlantıya 500 döner (anında başarısız) — dış retry döngüsü
        // 3 denemenin TAMAMINI tüketir ve null döner. Deneme başına en az 2 bağlantı
        // (HttpClient + DownloaderHelper) harcandığından toplam bağlantı sayısı
        // tek-denemelik uygulamadan belirgin biçimde yüksektir.
        using var server = await StubHttpServer.StartAsync(500);
        var svc = new DownloadService
        {
            MaxAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        };

        var sw = Stopwatch.StartNew();
        var result = await svc.TryDownloadString($"http://127.0.0.1:{server.Port}/sub.txt", (IWebProxy?)null, "test");
        sw.Stop();

        result.Should().BeNull("tüm denemeler başarısızsa null döner");
        server.RequestCount.Should().BeGreaterThanOrEqualTo(6,
            "3 denemenin her biri en az 2 bağlantı harcamalı (retry döngüsü çalıştı)");
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(90,
            "geri çekilme gecikmeleri uygulanmalı (2 × ~50 ms, min jitter)");
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    [Fact]
    public void ComputeRetryDelay_GrowsExponentiallyWithinJitterBounds()
    {
        var baseDelay = TimeSpan.FromMilliseconds(100);
        var samples = Enumerable.Range(1, 4).Select(n => DownloadService.ComputeRetryDelay(baseDelay, n)).ToArray();

        // 1. deneme: [75, 125]; 2.: [150, 250]; 3.: [300, 500]; 4.: [600, 1000]
        for (var i = 0; i < samples.Length; i++)
        {
            var min = 0.75 * Math.Pow(2, i) * baseDelay.TotalMilliseconds;
            var max = 1.25 * Math.Pow(2, i) * baseDelay.TotalMilliseconds;
            samples[i].TotalMilliseconds.Should().BeInRange(min - 0.001, max + 0.001, $"deneme {i + 1} jitter sınırları içinde");
        }

        // Ardışık gecikmeler katı biçimde büyür: alt sınır(sonraki) > üst sınır(önceki)
        // (0.75·2 / 1.25 = 1.2) — jitter büyümeyi asla geri çeviremez.
        for (var i = 1; i < samples.Length; i++)
        {
            samples[i].Should().BeGreaterThan(samples[i - 1], "üstel geri çekilme katı biçimde büyümeli");
        }
    }

    [Fact]
    public async Task UrlRedirectAsync_TransientFailure_RetriesAndSucceeds()
    {
        // 302 sunucusu ilk bağlantıyı yanıtsız kapatır; retry sonrası Location döner.
        const string location = "https://github.com/AhmetOzbay27/AoGPN/releases/tag/v1.1.1";
        using var server = await StubHttpServer.StartRedirectAsync(location, failFirst: 1);
        var svc = new DownloadService
        {
            MaxAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        };

        var result = await svc.UrlRedirectAsync($"http://127.0.0.1:{server.Port}/latest", (IWebProxy?)null);

        result.Should().Be(location, "geçici arıza sonrası yönlendirme hedefi alınmalı");
        server.RequestCount.Should().Be(2, "1 hatalı + 1 başarılı bağlantı");
    }

    /// <summary>Dinlenmeyen boş bir port döndürür (ölü SOCKS proxy senaryosu).</summary>
    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task ProbeUrl_NoListener_ReportsOffline()
    {
        // Boş bir port bul, sonra onu dinleme — bağlantı reddedilir (ağ hatası).
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var result = await new DownloadService()
                .ProbeUrlAsync($"http://127.0.0.1:{port}/", (IWebProxy?)null, timeoutSec: 3);

            result.Ok.Should().BeFalse("erişilemeyen adres canlı sayılmamalı");
            result.HttpStatus.Should().BeNull("ağ hatasında HTTP durum kodu yoktur — OFFLINE sınıfı");
        }
    }

    /// <summary>
    /// Stub HTTP sunucusu: ayarlanabilir durum koduyla yanıt verir; <c>failFirst</c>
    /// ile ilk N bağlantıya YANITSIZ kapatılarak geçici ağ arızası simüle edilir
    /// (retry davranışını test etmek için). HttpListener yerine ham TcpListener
    /// kullanılır — Windows'ta URL ACL / yönetici ayrıcalığı gerektirmez.
    /// </summary>
    private sealed class StubHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _statusCode;
        private readonly string? _location;
        private readonly int _failFirst;
        private readonly Task _serveTask;
        private int _served;

        public int Port { get; }

        /// <summary>Kabul edilen toplam bağlantı sayısı (retry sayısını doğrulamak için).</summary>
        public int RequestCount => Volatile.Read(ref _served);

        private StubHttpServer(int statusCode, string? location = null, int failFirst = 0)
        {
            _statusCode = statusCode;
            _location = location;
            _failFirst = failFirst;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = Task.Run(ServeLoopAsync);
        }

        public static Task<StubHttpServer> StartAsync(int statusCode, int failFirst = 0)
            => Task.FromResult(new StubHttpServer(statusCode, null, failFirst));

        /// <summary>302 yönlendirme modu — Location başlığıyla yanıt verir.</summary>
        public static Task<StubHttpServer> StartRedirectAsync(string location, int failFirst = 0)
            => Task.FromResult(new StubHttpServer(302, location, failFirst));

        private async Task ServeLoopAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch
                    {
                        break; // dinleyici kapatıldı
                    }
                    _ = Task.Run(() => ServeOneAsync(client));
                }
            }
            catch
            {
                // dinleyici kapatıldı
            }
        }

        private async Task ServeOneAsync(TcpClient client)
        {
            try
            {
                using var _ = client;
                using var stream = client.GetStream();

                // İstek başlığını oku (\r\n\r\n'ye kadar) — küçük GET başlıkları için yeterli.
                var buffer = new byte[4096];
                var head = new MemoryStream();
                while (head.Length < 8192)
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0)
                    {
                        break;
                    }
                    head.Write(buffer, 0, n);
                    if (Encoding.ASCII.GetString(head.ToArray()).Contains("\r\n\r\n"))
                    {
                        break;
                    }
                }

                // İlk N bağlantı: isteği al, YANITSIZ kapat → geçici ağ arızası.
                var served = Interlocked.Increment(ref _served);
                if (_failFirst > 0 && served <= _failFirst)
                {
                    return;
                }

                var reason = _statusCode switch
                {
                    200 => "OK",
                    302 => "Found",
                    500 => "Internal Server Error",
                    _ => "Error",
                };
                var body = "OK\n"u8.ToArray();
                var locationHeader = _location is null ? string.Empty : $"Location: {_location}\r\n";
                var response = $"HTTP/1.1 {_statusCode} {reason}\r\n" +
                               locationHeader +
                               "Content-Type: text/plain\r\n" +
                               $"Content-Length: {body.Length}\r\n" +
                               "Connection: close\r\n\r\n";
                var bytes = Encoding.UTF8.GetBytes(response).Concat(body).ToArray();
                await stream.WriteAsync(bytes);
            }
            catch
            {
                // İstemci bağlantıyı erken kapattı — sessiz geç.
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
                // zaten kapalı
            }
            try
            {
                _serveTask.Wait(500);
            }
            catch
            {
                // yanıt gönderilmeden bırakıldı
            }
        }
    }
}