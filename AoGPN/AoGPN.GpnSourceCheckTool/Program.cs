using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ServiceLib.Common;
using ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnSourceCheckTool — abonelik/node kaynaklarının HTTP canlılık taraması
//
// FreeNodeSources kataloğundaki (veya komut satırında verilen) kaynakların her
// birine HTTP isteği atar, yanıt kodunu + gecikmeyi ölçer ve canlı/ölü olarak
// sınıflandırır. Amaç: node havuzunu çekmeden ÖNCE hangi adreslerin açık olduğunu
// görmek; ölü adresler tespit edilirse exit kodu 0 dışına döner (CI/script uyumlu).
//
// Örnekler:
//   GpnSourceCheckTool                                   → FreeNodeSources kataloğunu tara
//   GpnSourceCheckTool https://raw.githubusercontent.com/.../sub.txt
//   GpnSourceCheckTool --file node-pool-links.txt --jsonl
//   GpnSourceCheckTool --parallel 12 --timeout 8
// ─────────────────────────────────────────────────────────────────────────

var opts = ParseArgs(args);
if (opts.Help)
{
    PrintHelp();
    return 0;
}

var sources = ResolveSources(opts);

if (sources.Count == 0)
{
    Console.Error.WriteLine("[GpnSourceCheckTool] Taranacak kaynak yok.");
    return 2;
}

Console.Error.WriteLine($"[GpnSourceCheckTool] {sources.Count} kaynak taranıyor (paralellik={opts.Parallel}, timeout={opts.Timeout}s)…");

var results = new ConcurrentBag<(string Name, string Url, DownloadService.UrlProbeResult Probe)>();
var service = new DownloadService();

await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = opts.Parallel }, async (source, ct) =>
    {
        // Önce doğrudan; erişilemez (timeout/ağ hatası) ve --proxy verildiyse onun
        // üzerinden tekrar dene. (Uygulama içi SOCKS proxy'ine bağımlılık yok —
        // standalone araç yalnızca açıkça verilen proxy'i kullanır.)
        var proxy = opts.Proxy is not null ? new WebProxy(opts.Proxy) : null;
        var probe = await service.ProbeUrlAsync(source.Url, proxy, opts.Timeout);
        if (!probe.Ok && probe.HttpStatus is null && proxy != null)
        {
            var viaProxy = await service.ProbeUrlAsync(source.Url, proxy, opts.Timeout);
            if (viaProxy.Ok || viaProxy.HttpStatus is not null)
            {
                probe = viaProxy;
            }
        }
        results.Add((source.Name, source.Url, probe));
    });

var ordered = results.OrderBy(r => r.Probe.Ok ? 0 : 1).ThenBy(r => r.Name).ToList();
foreach (var (name, url, probe) in ordered)
{
    if (opts.Jsonl)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            name,
            url,
            ok = probe.Ok,
            httpStatus = probe.HttpStatus,
            latencyMs = probe.LatencyMs,
            message = probe.Message,
        }));
    }
    else
    {
        var status = probe.Ok ? "LIVE" : (probe.HttpStatus is not null ? $"HTTP {probe.HttpStatus}" : "OFFLINE");
        Console.WriteLine($"{Pad(status, 8)} {probe.LatencyMs,6} ms  {name,-42} {url}");
    }
}

var live = ordered.Count(r => r.Probe.Ok);
var reachable = ordered.Count(r => r.Probe.HttpStatus is not null);
if (!opts.Jsonl)
{
    Console.WriteLine();
    Console.WriteLine($"[GpnSourceCheckTool] {live}/{ordered.Count} canlı, {reachable}/{ordered.Count} erişilebilir (HTTP) — tarandı: {sources.Count}");
}

return ordered.Count(r => r.Probe.Ok) == ordered.Count ? 0 : 1;

// ── Kaynak çözümleme ─────────────────────────────────────────────────────

static List<(string Name, string Url)> ResolveSources(CheckOptions opts)
{
    var sources = new List<(string Name, string Url)>();
    if (opts.Urls.Count > 0)
    {
        foreach (var url in opts.Urls)
        {
            var trimmed = url.Trim();
            if (trimmed.Length > 0)
            {
                sources.Add(($"arg[{sources.Count + 1}]", trimmed));
            }
        }
        return sources;
    }

    if (opts.File is not null)
    {
        foreach (var line in File.ReadAllLines(opts.File))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }
            sources.Add((trimmed, trimmed));
        }
        return sources;
    }

    return FreeNodeSources.All.Select(s => (s.Name, s.Url)).ToList();
}

// ── Argümanlar ───────────────────────────────────────────────────────────

static CheckOptions ParseArgs(string[] args)
{
    var opts = new CheckOptions();
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        var (name, inlineValue) = SplitArg(arg);
        switch (name)
        {
            case "--file" when TakeValue(args, ref i, inlineValue, out var v):
                opts.File = v;
                break;
            case "--parallel" when TakeValue(args, ref i, inlineValue, out var v) && int.TryParse(v, out var p):
                opts.Parallel = Math.Clamp(p, 1, 64);
                break;
            case "--timeout" when TakeValue(args, ref i, inlineValue, out var v) && int.TryParse(v, out var t):
                opts.Timeout = Math.Clamp(t, 2, 30);
                break;
            case "--proxy" when TakeValue(args, ref i, inlineValue, out var v):
                opts.Proxy = v;
                break;
            case "--jsonl":
                opts.Jsonl = true;
                break;
            case "--help":
            case "-h":
                opts.Help = true;
                break;
            default:
                if (!arg.StartsWith('-'))
                {
                    opts.Urls.Add(arg);
                }
                else
                {
                    Console.Error.WriteLine($"[GpnSourceCheckTool] Bilinmeyen argüman: {arg}");
                }
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

static string Pad(string value, int width)
{
    return value.Length >= width ? value : value + new string(' ', width - value.Length);
}

static void PrintHelp()
{
    Console.WriteLine("""
        GpnSourceCheckTool — abonelik/node kaynaklarının HTTP canlılık taraması

        KULLANIM:
          GpnSourceCheckTool [seçenekler] [url1 url2 ...]

        Kaynak seçimi (öncelik sırasıyla):
          <url...>                Komut satırında verilen adresleri tara
          --file <path>           Birer satır URL içeren dosyayı tara ('#' yorum satırı)
          (varsayılan)            FreeNodeSources kataloğundaki tüm kaynakları tara

        SEÇENEKLER:
          --parallel <n>          Eşzamanlı probe sayısı (varsayılan: 8)
          --timeout <sn>          Kaynak başına zaman aşımı (varsayılan: 5)
          --proxy <socks5://…>   Erişilemez kaynaklar için açık proxy adresi
                                 (verilirse bir kez proxy üzerinden tekrar dener)
          --jsonl                 Okunabilir tablo yerine NDJSON satırları bas
          -h, --help              Bu yardım

        ÇIKIŞ KODU:
          0  tüm kaynaklar canlı
          1  en az bir kaynak ölü/erişilemez (2xx/3xx dışı ya da timeout)
          2  taranacak kaynak yok

        AÇIKLAMA:
          LIVE   → 2xx/3xx (yanıt alındı, kaynak canlı)
          HTTP N → 4xx/5xx (erişilebilir ama içerik/abonelik ölü sayılır)
          OFFLINE→ zaman aşımı/ağ hatası (erişilemez)
        """);
}

/// <summary>Komut satırı seçenekleri.</summary>
sealed class CheckOptions
{
    public List<string> Urls { get; } = [];
    public string? File { get; set; }
    public int Parallel { get; set; } = 8;
    public int Timeout { get; set; } = 5;
    public string? Proxy { get; set; }
    public bool Jsonl { get; set; }
    public bool Help { get; set; }
}