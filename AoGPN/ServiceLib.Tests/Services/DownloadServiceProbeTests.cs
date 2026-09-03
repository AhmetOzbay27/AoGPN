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
    /// Stub HTTP sunucusu: ayarlanabilir durum koduyla TEK bir isteğe yanıt verir ve
    /// kapanır. HttpListener yerine ham TcpListener kullanılır — Windows'ta URL ACL /
    /// yönetici ayrıcalığı gerektirmez, her ortamda deterministik çalışır.
    /// </summary>
    private sealed class StubHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _statusCode;
        private readonly Task _serveTask;

        public int Port { get; }

        private StubHttpServer(int statusCode)
        {
            _statusCode = statusCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = Task.Run(ServeAsync);
        }

        public static Task<StubHttpServer> StartAsync(int statusCode)
            => Task.FromResult(new StubHttpServer(statusCode));

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();

                // İstek başlığını oku (\\r\\n\\r\\n'ye kadar) — küçük GET başlıkları için yeterli.
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

                var reason = _statusCode switch
                {
                    200 => "OK",
                    500 => "Internal Server Error",
                    _ => "Error",
                };
                var body = "OK\n"u8.ToArray();
                var response = $"HTTP/1.1 {_statusCode} {reason}\r\n" +
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
            finally
            {
                try
                {
                    _listener.Stop();
                }
                catch
                {
                    // zaten kapalı
                }
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