using System.Net;
using System.Net.Sockets;
using System.Text;
using ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnJsonlConsumer — GpnProbeTool --jsonl çıktısının tüketicisi
//
// GpnProbeTool'un ürettiği NDJSON akışını okur (stdin veya --file):
//   1. Her satırı `ingestedAt` ekleyerek zaman damgalı JSONL loguna ekler (--log),
//   2. `server_probe` satırlarını Prometheus metriklerine çevirir:
//        --prometheus-out FILE   → her güncellemede exposition'ı dosyaya yazar
//                                  (node_exporter textfile collector deseni)
//        --prometheus-listen P   → GET /metrics üzerinden exposition'ı HTTP ile sunar
//                                  (sürekli izleyen bir exporter — stdin açık kalmalı)
//
// Örnekler:
//   GpnProbeTool --jsonl --monitor 3600 <confs> | GpnJsonlConsumer --log probes.log
//   GpnProbeTool --jsonl <confs> | GpnJsonlConsumer --prometheus-out /var/lib/node_exporter/gpn.prom
//   GpnProbeTool --jsonl --monitor 3600 <confs> | GpnJsonlConsumer --prometheus-listen 9101
// ─────────────────────────────────────────────────────────────────────────

var opts = ParseArgs(args);
if (opts.Help)
{
    PrintHelp();
    return 0;
}

var state = new GpnPrometheusProbeState();
using var http = opts.ListenPort is not null
    ? new MetricsHttpServer(opts.ListenPort.Value, state.BuildExposition)
    : null;

if (http is not null)
{
    Console.Error.WriteLine($"[GpnJsonlConsumer] Prometheus /metrics {opts.ListenPort} portunda dinleniyor — http://localhost:{opts.ListenPort}/metrics");
}

using TextReader reader = opts.InputFile is not null
    ? File.OpenText(opts.InputFile)
    : Console.In;

TextWriter logWriter;
if (opts.NoLog)
{
    logWriter = TextWriter.Null;
}
else
{
    // BOM'suz UTF-8 — JSONL arşivini jq -s / benzeri araçlar doğrudan tüketebilsin.
    var stream = new StreamWriter(opts.LogPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    logWriter = stream;
    Console.Error.WriteLine($"[GpnJsonlConsumer] JSONL log: {Path.GetFullPath(opts.LogPath)}");
}

string? line;
while ((line = reader.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    var ingestedAt = DateTimeOffset.Now;

    var probe = GpnJsonlConsumer.TryParseServerProbe(line, ingestedAt);
    if (probe is not null)
    {
        state.Update(probe);
    }

    if (!opts.NoLog)
    {
        var enriched = GpnJsonlConsumer.EnrichLine(line, ingestedAt);
        if (enriched is not null)
        {
            logWriter.WriteLine(enriched);
        }
    }

    if (opts.PrometheusOut is not null)
    {
        File.WriteAllText(opts.PrometheusOut, state.BuildExposition());
        Console.Error.WriteLine($"[GpnJsonlConsumer] Prometheus exposition güncellendi: {Path.GetFullPath(opts.PrometheusOut)}");
    }
}

// Dosya girdisinde EOF'a ulaşıldı — son durumu yazdır (varsa).
if (opts.PrometheusOut is not null)
{
    File.WriteAllText(opts.PrometheusOut, state.BuildExposition());
}

return 0;

// ── Argümanlar ───────────────────────────────────────────────────────────

static ConsumerOptions ParseArgs(string[] args)
{
    var opts = new ConsumerOptions();
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        var (name, inlineValue) = SplitArg(arg);
        switch (name)
        {
            case "--file" when TakeValue(args, ref i, inlineValue, out var v):
                opts.InputFile = v;
                break;
            case "--log" when TakeValue(args, ref i, inlineValue, out var v):
                opts.LogPath = v;
                break;
            case "--prometheus-out" when TakeValue(args, ref i, inlineValue, out var v):
                opts.PrometheusOut = v;
                break;
            case "--prometheus-listen" when TakeValue(args, ref i, inlineValue, out var v) && int.TryParse(v, out var port):
                opts.ListenPort = port;
                break;
            case "--no-log":
                opts.NoLog = true;
                break;
            case "--help":
            case "-h":
                opts.Help = true;
                break;
            default:
                Console.Error.WriteLine($"[GpnJsonlConsumer] Bilinmeyen argüman: {arg}");
                break;
        }
    }

    return opts;
}

static (string Name, string? Inline) SplitArg(string arg)
{
    var eq = arg.IndexOf('=');
    return eq > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
}

static bool TakeValue(string[] args, ref int i, string? inline, out string value)
{
    if (inline is not null)
    {
        value = inline;
        return true;
    }

    if (i + 1 < args.Length)
    {
        value = args[++i];
        return true;
    }

    value = string.Empty;
    return false;
}

static void PrintHelp()
{
    Console.WriteLine("""
        GpnJsonlConsumer — GpnProbeTool --jsonl çıktısının tüketicisi

        KULLANIM:
          GpnProbeTool --jsonl [--monitor N] <confs> | GpnJsonlConsumer [seçenekler]

        SEÇENEKLER:
          --file <path>            Girdiyi stdin yerine dosyadan oku (log tekrar oynatma)
          --log <path>             Zaman damgalı JSONL log (varsayılan: gpn-probe-log.jsonl)
          --no-log                 Log dosyası yazma (yalnızca metrikler)
          --prometheus-out <path>  Her güncellemede Prometheus exposition'ı dosyaya yaz
                                   (textfile collector: node_exporter --collector.textfile)
          --prometheus-listen <p>  GET /metrics üzerinden exposition'ı HTTP ile sun
          -h, --help               Bu yardım

        METRİKLER:
          gpn_ping_ms{server,name}          ICMP gidiş-dönüş (ms; -1 = başarısız)
          gpn_udp_status{server,name,status}  one-hot: gözlenen durum 1, diğerleri 0
        """);
}

/// <summary>Komut satırı seçenekleri.</summary>
sealed class ConsumerOptions
{
    public string? InputFile { get; set; }
    public string LogPath { get; set; } = "gpn-probe-log.jsonl";
    public string? PrometheusOut { get; set; }
    public int? ListenPort { get; set; }
    public bool NoLog { get; set; }
    public bool Help { get; set; }
}

/// <summary>
/// En küçük HTTP/1.1 ucu: her GET /metrics isteğine Prometheus exposition'ı
/// (text/plain; version=0.0.4) ile yanıt verir. Bağımlılıksız — TcpListener
/// üzerinde elle yazılmıştır; sürekli izleyen bir exporter için yeterlidir.
/// </summary>
internal sealed class MetricsHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string> _exposition;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public MetricsHttpServer(int port, Func<string> exposition)
    {
        _exposition = exposition;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 2000;

                // İstek başlığını oku (en fazla 8 KB, \r\n\r\n veya \n\n'ye kadar).
                var buffer = new byte[4096];
                var head = new StringBuilder();
                while (head.Length < 8192)
                {
                    var n = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    if (head.ToString().Contains("\r\n\r\n") || head.ToString().Contains("\n\n"))
                    {
                        break;
                    }
                }

                var requestLine = head.ToString().Split('\n').FirstOrDefault() ?? string.Empty;
                var path = requestLine.Split(' ').ElementAtOrDefault(1)?.Trim() ?? "/";
                var body = path is "/metrics" or "/" ? _exposition() : null;

                var statusLine = body is null ? "HTTP/1.1 404 Not Found\r\n" : "HTTP/1.1 200 OK\r\n";
                var payload = body ?? "not found\n";
                var response = statusLine +
                               "Content-Type: text/plain; version=0.0.4; charset=utf-8\r\n" +
                               $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n" +
                               "Connection: close\r\n\r\n" +
                               payload;

                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Bağlantı hataları sessiz — sonraki istekler gelmeye devam eder.
        }
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
    }
}
