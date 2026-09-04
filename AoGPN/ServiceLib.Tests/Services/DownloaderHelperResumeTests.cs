using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using ServiceLib.Helper;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// <see cref="DownloaderHelper"/> indirmelerinin ortasında kopan transferlere karşı
/// dayanıklılığı: kısmi veriyle yarıda kesilen bir indirme, sıfırdan başlamak yerine
/// kaldığı yerden devam etmeli (Range resume), tekrar tekrar denemeli ve gerekirse
/// tek-akışlı fallback'e düşmelidir. Gerçek ağa bağlanmaz — yerel bir TcpListener
/// tabanlı stub HTTP sunucusu kullanılır (URL ACL gerektirmez; HttpListener yerine ham
/// TCP, DownloadServiceProbeTests ile aynı gerekçeyle).
/// </summary>
public class DownloaderHelperResumeTests
{
    private const int PayloadSize = 1_000_000;

    private static byte[] MakePayload(int size)
    {
        // 256'ya hizalanmış desenler gizli bozuklukları maskeleyebilir — 251'e göre
        // dönen, ofsete bağlı bir desen kullanılır.
        var data = new byte[size];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31 + 7) % 251);
        }
        return data;
    }

    private static string TempTarget()
        => Path.Combine(Path.GetTempPath(), $"aogpn-dl-{Guid.NewGuid():N}.bin");

    /// <summary>
    /// Kütüphane yolu: sunucu gövdenin %60'ından sonra bağlantıyı 3 kez (ilk deneme +
    /// MaxTryAgainOnFailure=2) koparır; 4. istek bir Range-resume isteğidir ve başarılı
    /// olur. İndirme tamamlanmalı VE sunucu en az bir pozitif ofsetten resume görmeli —
    /// sıfırdan yeniden başlama bu testi başarısız eder.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task DownloadFileAsync_MidStreamDrop_ResumesInsteadOfRestarting()
    {
        var payload = MakePayload(PayloadSize);
        using var server = new ResumableStubServer(payload, failFirstBodyRequests: 3);
        var target = TempTarget();
        try
        {
            await DownloaderHelper.Instance.DownloadFileAsync(
                null, $"http://127.0.0.1:{server.Port}/core.zip", target, new Progress<double>(), timeout: 4);

            (await File.ReadAllBytesAsync(target)).Should().Equal(payload, "final content must be intact");
            server.ResumeRanges.Should().NotBeEmpty(
                "a mid-stream drop must be resumed from the saved position, not restarted from 0");
            server.ResumeRanges.Should().OnlyContain(r => r > 0 && r < payload.Length);
            server.AbortedBodyRequests.Should().BeGreaterThanOrEqualTo(1, "the drop must actually have happened");
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>
    /// Kütüphane yolu, sağlıklı sunucu: dosya yokken temiz indirme tamamlanır ve önceden
    /// var olan (çöp) hedef dosya indirme öncesi silinir.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DownloadFileAsync_PlainSuccess_OverwritesExistingFile()
    {
        var payload = MakePayload(PayloadSize);
        using var server = new ResumableStubServer(payload, failFirstBodyRequests: 0);
        var target = TempTarget();
        try
        {
            await File.WriteAllBytesAsync(target, new byte[1234]);

            await DownloaderHelper.Instance.DownloadFileAsync(
                null, $"http://127.0.0.1:{server.Port}/core.bin", target, new Progress<double>(), timeout: 4);

            (await File.ReadAllBytesAsync(target)).Should().Equal(payload);
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>
    /// Tek-akışlı fallback: diskte kısmi dosya varken Range isteği kaldığı yerden devam
    /// eder — yalnızca eksik kısım transfer edilir, sonuç bütünle eşleşir.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DownloadSingleStreamResumableAsync_ResumesFromExistingPartial()
    {
        var payload = MakePayload(PayloadSize);
        using var server = new ResumableStubServer(payload, failFirstBodyRequests: 0);
        var target = TempTarget();
        try
        {
            var partial = payload.AsSpan(0, 400_000).ToArray();
            await File.WriteAllBytesAsync(target, partial);

            await DownloaderHelper.DownloadSingleStreamResumableAsync(
                null, $"http://127.0.0.1:{server.Port}/core.bin", target, new Progress<double>(), timeout: 4);

            (await File.ReadAllBytesAsync(target)).Should().Equal(payload);
            server.ResumeRanges.Should().Contain(400_000, "the Range request must start at the partial length");
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>
    /// Tek-akışlı fallback, Range'i umursamayan sunucu (200 + tam gövde): bozuk kısmi
    /// dosya sıfırdan yeniden yazılır ve sonuç bütünle eşleşir.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DownloadSingleStreamResumableAsync_ServerIgnoringRange_RewritesFromZero()
    {
        var payload = MakePayload(PayloadSize);
        using var server = new ResumableStubServer(payload, failFirstBodyRequests: 0, ignoreRange: true);
        var target = TempTarget();
        try
        {
            await File.WriteAllBytesAsync(target, Enumerable.Repeat((byte)0xFF, 400_000).ToArray());

            await DownloaderHelper.DownloadSingleStreamResumableAsync(
                null, $"http://127.0.0.1:{server.Port}/core.bin", target, new Progress<double>(), timeout: 4);

            (await File.ReadAllBytesAsync(target)).Should().Equal(payload,
                "a server ignoring Range must cause a full rewrite, not an append over garbage");
            server.LastRangeRequested.Should().BeTrue("the fallback must still ask for a resume range");
        }
        finally
        {
            File.Delete(target);
        }
    }

    /// <summary>
    /// Stub HTTP sunucusu: isteğe bağlı olarak belirli sayıda "0'dan gövde isteğini"
    /// (probe değil — probe bytes=0-0'dır) ortasında koparır, Range isteklerine 206 ile
    /// yanıt verir ve gördüğü resume-ofsetlerini kaydeder. Her yanıtta Connection: close
    /// kullanılır — her istek kendi bağlantısında, sıralı ve deterministik.
    /// </summary>
    private sealed class ResumableStubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;
        private readonly int _failFirstBodyRequests;
        private readonly bool _ignoreRange;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;
        private readonly List<long> _resumeRanges = new();
        private readonly object _lock = new();
        private int _abortedBodyRequests;

        public int Port { get; }
        public int AbortedBodyRequests => Volatile.Read(ref _abortedBodyRequests);

        public IReadOnlyList<long> ResumeRanges
        {
            get
            {
                lock (_lock)
                {
                    return _resumeRanges.ToArray();
                }
            }
        }

        public bool LastRangeRequested { get; private set; }

        public ResumableStubServer(byte[] payload, int failFirstBodyRequests, bool ignoreRange = false)
        {
            _payload = payload;
            _failFirstBodyRequests = failFirstBodyRequests;
            _ignoreRange = ignoreRange;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = Task.Run(ServeLoopAsync);
        }

        private async Task ServeLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => HandleConnectionAsync(client));
            }
        }

        private async Task HandleConnectionAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var head = await ReadHeadAsync(stream).ConfigureAwait(false);
                    if (head is null)
                    {
                        return;
                    }

                    var range = ParseRange(head);
                    LastRangeRequested = head.Contains("Range:", StringComparison.OrdinalIgnoreCase);
                    if (range.start == 0 && range.end != 0 && !_ignoreRange)
                    {
                        // Gerçek bir "0'dan gövde" isteği (probe bytes=0-0'dır ve sayılmaz).
                        var n = Interlocked.Increment(ref _abortedBodyRequests);
                        if (n <= _failFirstBodyRequests)
                        {
                            await WriteAbortedAsync(stream).ConfigureAwait(false);
                            return;
                        }
                    }

                    if (range.start > 0 && !_ignoreRange)
                    {
                        lock (_lock)
                        {
                            _resumeRanges.Add(range.start);
                        }
                    }

                    await WriteRangeAsync(stream, range, _ignoreRange).ConfigureAwait(false);
                }
            }
            catch
            {
                // İstemci bağlantıyı erken kapattı — sessiz geç.
            }
        }

        private static async Task<string?> ReadHeadAsync(Stream stream)
        {
            var buffer = new byte[4096];
            var head = new MemoryStream();
            while (head.Length < 16384)
            {
                var n = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (n == 0)
                {
                    return head.Length == 0 ? null : Encoding.ASCII.GetString(head.ToArray());
                }
                head.Write(buffer, 0, n);
                if (Encoding.ASCII.GetString(head.ToArray()).Contains("\r\n\r\n"))
                {
                    break;
                }
            }
            return Encoding.ASCII.GetString(head.ToArray());
        }

        private static (long start, long end) ParseRange(string head)
        {
            foreach (var line in head.Split("\r\n"))
            {
                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var spec = line[(line.IndexOf(':') + 1)..].Trim();
                var value = spec.Contains('=') ? spec[(spec.IndexOf('=') + 1)..].Trim() : spec;
                var parts = value.Split('-');
                if (!long.TryParse(parts[0], out var start))
                {
                    return (0, -1);
                }
                var end = parts.Length > 1 && long.TryParse(parts[1], out var e) ? e : -1;
                return (start, end);
            }
            return (0, -1);
        }

        private async Task WriteAbortedAsync(Stream stream)
        {
            // Content-Length TAM bildirilir ama gövde yalnızca %60 yazılıp bağlantı
            // kapanır → istemci beklenmeyen akış sonu görür (orta-kopma simülasyonu).
            var head = "HTTP/1.1 206 Partial Content\r\n" +
                       "Accept-Ranges: bytes\r\n" +
                       $"Content-Range: bytes 0-{_payload.Length - 1}/{_payload.Length}\r\n" +
                       $"Content-Length: {_payload.Length}\r\n" +
                       "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head)).ConfigureAwait(false);
            await stream.WriteAsync(_payload.AsMemory(0, (int)(_payload.Length * 0.6))).ConfigureAwait(false);
            // Dön → akış kapanır, kalan gövde asla gönderilmez.
        }

        private async Task WriteRangeAsync(Stream stream, (long start, long end) range, bool ignoreRange)
        {
            var len = _payload.Length;
            var start = ignoreRange ? 0 : Math.Max(0, range.start);
            var end = range.end < 0 || range.end >= len ? len - 1 : range.end;

            if (start >= len)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(
                    "HTTP/1.1 416 Requested Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).ConfigureAwait(false);
                return;
            }

            var count = end - start + 1;
            var isFull = start == 0 && count == len;
            var head = (isFull ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 206 Partial Content\r\n") +
                       "Accept-Ranges: bytes\r\n" +
                       (!isFull ? $"Content-Range: bytes {start}-{end}/{len}\r\n" : string.Empty) +
                       $"Content-Length: {count}\r\n" +
                       "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head)).ConfigureAwait(false);
            await stream.WriteAsync(_payload.AsMemory((int)start, (int)count)).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _cts.Cancel();
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
                // dinleyici durduruldu
            }
        }
    }
}