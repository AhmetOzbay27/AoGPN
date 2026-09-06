using System.Text.Json;
using ServiceLib.Enums;
using ServiceLib.Handler.Fmt;
using ServiceLib.Models.Entities;
using ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// AoGPN.GpnProbeTool — canlı İtalya/Almanya WireGuard sunucularını var olduğu
// gibi test eden CLI tanılama aracı.
//
// WireGuardServerCatalog / GpnServerSelectionService'in "Bağlan" akışında
// çalıştırdığı ölçümlerin aynısını (ICMP gecikme, junk-UDP sağlık, gerçek
// Noise_IKpsk2 el sıkışması ve otomatik seçim) komut satırından koşturur —
// üretim koduna dokunmadan canlı sunuculardaki sorunu teşhis etmek için.
//
// Kullanım:
//   AoGPN.GpnProbeTool <conf1> [conf2 ...] [options]
//
// Örnek:
//   AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf
//
// Seçenekler:
//   --timeout-ms N     UDP/el sıkışma yanıt penceresi   (varsayılan 3000)
//   --samples N        ICMP örnek sayısı                 (varsayılan 4)
//   --icmp mode        auto | icmp | tcp                 (varsayılan auto)
//   --no-handshake     gerçek WG el sıkışmasını atla (yalnızca junk-UDP)
//   --no-select        otomatik seçimi atla (yalnızca sunucu başına diyagnostik)
//   --failover         failover karar matrisini yazdır (her aday "aktif" varsayılarak
//                      DecideFailover senaryoları: aktif ölü / sağlıklı / hysteresis)
//   --strict-udp       NoResponse'u bloklu (ölü) say — katı politika için karar matrisi
//   --monitor [N]      uzun süreli ön izleme: her N sn (varsayılan 10) ölçüm + sunucu
//                      değişimi/mod düşüşü-kurtarma olaylarını zaman damgalı bas
//   --jsonl            makine-tüketilebilir JSON satırları (NDJSON) yaz — CI/notifier
//   --webhook URL      bitişte genel özeti URL'ye POST et (notifier; hata çalışmayı düşürmez)
//   --help             bu yardımı yaz
// ─────────────────────────────────────────────────────────────────────────

// Türkçe karakterlerin Windows kukla konsolunda bozulmasını önle.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch
{
    // Konsol kod sayfası değiştirilemiyorsa yok say — çıktı ASCII'ye düşer.
}

var (confPaths, opts) = ParseArgs(args);
if (!confPaths.Any())
{
    PrintUsage();
    return 1;
}
JsonlOut.Enabled = opts.Jsonl;

var profiles = LoadProfiles(confPaths);
if (profiles.Count == 0)
{
    Console.Error.WriteLine("[hata] Geçerli hiçbir WireGuard profili çözümlenemedi.");
    return 2;
}

if (!opts.Jsonl)
{
    PrintBanner(profiles);
    Console.WriteLine();
}

var selector = new GpnServerSelectionService();
var udpChecker = new UdpHealthChecker();
var handshakeProbe = opts.NoHandshake ? null : new WireGuardHandshakeProbe();
var probeOptions = new GpnProbeOptions
{
    Samples = opts.Samples,
    PerSampleTimeoutMs = opts.PerSampleTimeoutMs,
    Mode = opts.ProbeMode,
    UdpCheck = new UdpHealthCheckOptions(WaitTimeoutMs: opts.TimeoutMs, TreatNoResponseAsBlocked: opts.StrictUdp),
    HandshakeProbe = new WireGuardHandshakeProbeOptions(WaitTimeoutMs: opts.TimeoutMs, MaxAttempts: 1),
};

// ── Uzun süreli ön izleme modu (--monitor) ───────────────────────────────
if (opts.MonitorSeconds > 0)
{
    return await RunMonitorAsync(selector, udpChecker, handshakeProbe, profiles, probeOptions, opts);
}

// ── Sunucu başına diyagnostik ────────────────────────────────────────────
if (!opts.Jsonl)
{
    Console.WriteLine($"== Sunucu başına ölçüm (ICMP örnekleri: {probeOptions.Samples}) ==");
}
var junkResults = new List<UdpProbeResult>();
var pingResults = new List<GpnServerProbeResult>();
foreach (var profile in profiles)
{
    var ping = (await selector.ProbeAllAsync([profile], probeOptions)).First();
    pingResults.Add(ping);
    var junk = await udpChecker.ProbeAsync(
        profile.ServerId, profile.EndpointHost, profile.EndpointPort,
        probeOptions.UdpCheck);
    junkResults.Add(junk);

    UdpProbeResult? hs = null;
    if (handshakeProbe is not null)
    {
        hs = await handshakeProbe.ProbeAsync(
            profile.ServerId, profile.EndpointHost, profile.EndpointPort,
            profile.ServerPublicKey, profile.ClientPrivateKey,
            probeOptions.HandshakeProbe);
    }

    JsonlOut.Write(new
    {
        @event = "server_probe",
        serverId = profile.ServerId,
        name = profile.Name,
        host = profile.EndpointHost,
        port = profile.EndpointPort,
        icmpMs = ping.IsSuccess ? ping.DelayMs : -1,
        icmpAvgMs = ping.IsSuccess ? ping.AvgDelayMs : -1,
        icmpLossPct = ping.LossPercent,
        udp = junk.Status.ToString(),
        udpDetail = junk.Detail,
        handshake = hs?.Status.ToString(),
        timestamp = DateTimeOffset.Now,
    });

    if (!opts.Jsonl)
    {
        PrintServerRow(profile, ping, junk, hs);
        Console.WriteLine();
    }
}

// ── Otomatik seçim (tıpkı Bağlan akışı gibi) ─────────────────────────────
string? chosenServerId = null;
ConnectionMode? chosenMode = null;
UdpProbeStatus? chosenUdp = null;
if (profiles.Count > 1 && !opts.NoSelect)
{
    if (!opts.Jsonl)
    {
        Console.WriteLine("== Otomatik seçim (SelectBestServerAsync — Bağlan akışı) ==");
    }
    try
    {
        var selection = await selector.SelectBestServerAsync(profiles, probeOptions);
        chosenServerId = selection.Best?.ServerId;
        chosenMode = selection.Mode;
        chosenUdp = selection.UdpProbe?.Status;

        JsonlOut.Write(new
        {
            @event = "selection",
            best = selection.Best?.ServerId,
            mode = selection.Mode.ToString(),
            udp = selection.UdpProbe?.Status.ToString(),
            udpDetail = selection.UdpProbe?.Detail,
            servers = selection.Results.Select(r => new
            {
                serverId = r.ServerId,
                icmpMs = r.IsSuccess ? r.DelayMs : -1,
                icmpLossPct = r.LossPercent,
            }).ToArray(),
            timestamp = DateTimeOffset.Now,
        });

        if (!opts.Jsonl)
        {
            Console.WriteLine($"   En iyi sunucu : {selection.Best?.Name ?? "(yok)"}");
            Console.WriteLine($"   Mod (Tier)    : {selection.Mode}");
            Console.WriteLine($"   UDP probe     : {selection.UdpProbe?.Status} — {selection.UdpProbe?.Detail}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"   [hata] seçim sırasında: {ex.Message}");
    }
}

// ── Failover karar matrisi (DecideFailover) ──────────────────────────────
if (profiles.Count > 1 && opts.ShowFailover)
{
    if (!opts.Jsonl)
    {
        Console.WriteLine("== Failover karar matrisi (DecideFailover) ==");
    }
    PrintDecisionMatrix(profiles, pingResults, junkResults.ToDictionary(r => r.ServerId), opts);
}

// Dönüş kodu: en az bir sunucuda sağlıklı UDP yolu varsa 0, yoksa 1.
// WireGuard junk pakete yanıt vermediği için NoResponse sağlıklı yoldur;
// Open yanıtı vardır (yanıt veren bir UDP hizmeti) ya da ICMP gelmediyse (NoResponse)
// yolun açık olduğunu varsayıyoruz. Bloklu (ICMP Port Unreachable) ise kesin kapalı.
bool anyReachable = junkResults.Any(r => r.Status is UdpProbeStatus.Open or UdpProbeStatus.NoResponse);
var exitCode = anyReachable ? 0 : 1;
if (!opts.Jsonl)
{
    Console.WriteLine(new string('-', 72));
    Console.WriteLine(anyReachable
        ? "Sonuç: en az bir sunucuya UDP yolu açık görünüyor."
        : "Sonuç: hiçbir sunucuya UDP yolu doğrulanamadı (ICMP Port Unreachable).");
}

var summary = new
{
    @event = "summary",
    ok = anyReachable,
    exitCode,
    best = chosenServerId,
    mode = chosenMode?.ToString(),
    udp = chosenUdp?.ToString(),
    servers = profiles.Select(p => new
    {
        serverId = p.ServerId,
        name = p.Name,
        endpoint = $"{p.EndpointHost}:{p.EndpointPort}",
    }).ToArray(),
    timestamp = DateTimeOffset.Now,
};
JsonlOut.Write(summary);
if (!string.IsNullOrEmpty(opts.Webhook))
{
    await TryNotifyAsync(opts.Webhook, summary, anyReachable);
}
return exitCode;

// ── yardımcılar ──────────────────────────────────────────────────────────

/// <summary>
/// Uzun süreli ön izleme: her <see cref="ProbeToolOptions.MonitorSeconds"/> saniyede
/// otomatik seçimi yeniden ölçer ve tek satır/göünirimlik basar. Önceki döngüye göre
/// sunucu değiştiyse veya mod düştüğünde/kurtarıldığında (WireGuardUDP ↔ V2rayTCP)
/// zaman damgalı olay satırı ekler — canlı failover davranışını seyretmek için.
/// Ctrl+C ile durur.
/// </summary>
static async Task<int> RunMonitorAsync(
    GpnServerSelectionService selector,
    UdpHealthChecker udpChecker,
    WireGuardHandshakeProbe? handshakeProbe,
    IReadOnlyList<GpnServerProfile> profiles,
    GpnProbeOptions probeOptions,
    ProbeToolOptions opts)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var interval = TimeSpan.FromSeconds(opts.MonitorSeconds);
    string? activeId = null;
    ConnectionMode? lastMode = null;

    Console.WriteLine();
    if (!JsonlOut.Enabled)
    {
        Console.WriteLine($"== ÖN İZLEME MODU (her {opts.MonitorSeconds} sn ölçüm — Ctrl+C ile durdur) ==");
    }

    while (!cts.IsCancellationRequested)
    {
        GpnSelectionResult? selection;
        try
        {
            selection = await selector.SelectBestServerAsync(profiles, probeOptions, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Stamp()} [HATA] ölçüm: {ex.Message}");
            try { await Task.Delay(interval, cts.Token); } catch (OperationCanceledException) { break; }
            continue;
        }

        var best = selection.Best;
        var mode = selection.Mode;

        // Sunucu başına ping özeti + seçim satırı.
        var summary = string.Join("  ", profiles.Select(p =>
        {
            var r = selection.Results.FirstOrDefault(x => x.ServerId == p.ServerId);
            var ping = r?.IsSuccess == true ? $"{r.DelayMs}ms" : "−";
            return $"{p.Name}={ping}";
        }));

        JsonlOut.Write(new
        {
            @event = "monitor_sample",
            best = best?.ServerId,
            mode = mode.ToString(),
            udp = selection.UdpProbe?.Status.ToString(),
            summary,
            timestamp = DateTimeOffset.Now,
        });
        if (!JsonlOut.Enabled)
        {
            Console.WriteLine($"{Stamp()}  seçim={best?.ServerId ?? "(yok)"} mod={mode} udp={selection.UdpProbe?.Status}   [{summary}]");
        }

        // Olay tespiti: sunucu değişimi + mod düşüşü/kurtarma.
        if (activeId is not null)
        {
            if (best is not null && best.ServerId != activeId)
            {
                JsonlOut.Write(new { @event = "monitor_event", kind = "server_change", from = activeId, to = best.ServerId, mode = mode.ToString(), timestamp = DateTimeOffset.Now });
                if (!JsonlOut.Enabled)
                {
                    Console.WriteLine($"{Stamp()}  → OLAY: SUNUCU DEĞİŞİMİ  {activeId} → {best.ServerId}  (mode={mode})");
                }
            }
            else if (best is null && mode == ConnectionMode.V2rayTCP)
            {
                JsonlOut.Write(new { @event = "monitor_event", kind = "fallback", from = activeId, to = "V2rayTCP", reason = "hiçbir sunucu seçilemedi", timestamp = DateTimeOffset.Now });
                if (!JsonlOut.Enabled)
                {
                    Console.WriteLine($"{Stamp()}  → OLAY: DÜŞÜŞ  {activeId} → V2rayTCP  (hiçbir sunucu seçilemedi)");
                }
            }
        }
        if (lastMode is not null && mode != lastMode)
        {
            var kind = mode == ConnectionMode.WireGuardUDP ? "mode_recover" : "mode_fallback";
            JsonlOut.Write(new { @event = "monitor_event", kind, from = lastMode.ToString(), to = mode.ToString(), timestamp = DateTimeOffset.Now });
            if (!JsonlOut.Enabled)
            {
                var label = mode == ConnectionMode.WireGuardUDP ? "MOD KURTARMA (Tier 2)" : "MOD DÜŞÜŞÜ (Tier 3)";
                Console.WriteLine($"{Stamp()}  → OLAY: {label}  {lastMode} → {mode}");
            }
        }

        activeId = best?.ServerId;
        lastMode = mode;

        try { await Task.Delay(interval, cts.Token); }
        catch (OperationCanceledException) { break; }
    }

    JsonlOut.Write(new { @event = "monitor_end", reason = "cancelled", timestamp = DateTimeOffset.Now });
    if (!JsonlOut.Enabled)
    {
        Console.WriteLine($"{Stamp()}  İzleme durdu.");
    }
    return 0;
}

static string Stamp() => DateTimeOffset.Now.ToString("HH:mm:ss");

static void PrintBanner(IReadOnlyList<GpnServerProfile> profiles)
{
    Console.WriteLine("AoGPN.GpnProbeTool — canlı sunucu teşhisi");
    Console.WriteLine(new string('-', 72));
    foreach (var p in profiles)
    {
        var maskedKey = Mask(p.ClientPrivateKey);
        Console.WriteLine($"  {p.Name,-16} {p.EndpointHost}:{p.EndpointPort}  priv={maskedKey}  pub={p.ServerPublicKey?[..8]}…");
    }
    Console.WriteLine(new string('-', 72));
}

static void PrintServerRow(GpnServerProfile profile, GpnServerProbeResult ping, UdpProbeResult junk, UdpProbeResult? handshake)
{
    Console.WriteLine($"  [{profile.Name} ({profile.ServerId})] {profile.EndpointHost}:{profile.EndpointPort}");
    Console.WriteLine($"    ICMP gecikme   : {FormatPing(ping)}");
    Console.WriteLine($"    UDP (junk)     : {Status(junk)}  ({junk.Detail})");
    if (handshake is null)
    {
        Console.WriteLine($"    WG el sıkışma  : (atlandı --no-handshake)");
    }
    else
    {
        Console.WriteLine($"    WG el sıkışma  : {Status(handshake)}  ({handshake.Detail})");
    }
}

static string FormatPing(GpnServerProbeResult r)
    => r.IsSuccess
        ? $"min={r.DelayMs}ms avg={r.AvgDelayMs}ms max={r.MaxDelayMs}ms kayıp%={r.LossPercent}"
        : "yanıt yok (−1)";

/// <summary>
/// Failover karar matrisi: her adayı sırayla "aktif tünel" varsayıp
/// GpnDecision.DecideFailover'ı çalıştırır ve hangi eylemi
/// (None/SwitchServer/FallbackToV2ray) önerdiğini yazdırır. Böylece canlı
/// ping+UDP sonuçlarıyla aktif/ölü/hysteresis senaryolarının kararı görünür.
/// </summary>
static void PrintDecisionMatrix(
    IReadOnlyList<GpnServerProfile> profiles,
    IReadOnlyList<GpnServerProbeResult> pingResults,
    IReadOnlyDictionary<string, UdpProbeResult> udpResults,
    ProbeToolOptions opts)
{
    var matrixOptions = new GpnProbeOptions
    {
        EnableUdpHealth = true,
        UdpCheck = new UdpHealthCheckOptions(TreatNoResponseAsBlocked: opts.StrictUdp),
        // Kalın hysteresis: canlı (gürültülü) ölçümlerde salınım göstermesin;
        // aktif tünel sağlıklıyken yalnızca belirgin (25ms) farkla geçiş önersin.
        SwitchHysteresisMs = 25,
    };

    Console.WriteLine($"   Katı politika (NoResponse=ölü): {opts.StrictUdp}");
    Console.WriteLine();

    // Önce tüm aktif senaryoları bir kez çöz (DecideFailover net sonuç üretir);
    // sonra her satırda kararı hedef sütununa işaretle.
    var decisions = profiles.ToDictionary(
        active => active.ServerId,
        active => GpnDecision.DecideFailover(active, profiles, pingResults, udpResults, matrixOptions));

    Console.WriteLine(new string('-', 74));
    Console.WriteLine($"{"Aktif",-10} | {"UDP",-10} | {"Ping",-8} | Karar");
    Console.WriteLine(new string('-', 74));
    foreach (var active in profiles)
    {
        var d = decisions[active.ServerId];
        var udp = udpResults.GetValueOrDefault(active.ServerId)?.Status.ToString() ?? "-";
        var ping = pingResults.FirstOrDefault(r => r.ServerId == active.ServerId)?.IsSuccess == true
            ? pingResults.First(r => r.ServerId == active.ServerId).DelayMs + "ms"
            : "-";

        var actionText = d.Action switch
        {
            GpnDecision.FailoverActionType.SwitchServer => $"SUNUCU DEĞİŞ → {d.Target?.Name}",
            GpnDecision.FailoverActionType.FallbackToV2ray => "V2RAYTCP DÜŞÜŞÜ",
            _ => "DEĞİŞİM YOK",
        };

        JsonlOut.Write(new
        {
            @event = "failover",
            type = d.Action.ToString(),
            active = active.ServerId,
            udp = udp,
            pingMs = ping,
            action = actionText,
            reason = d.Reason,
            target = d.Target?.ServerId,
            strictUdp = opts.StrictUdp,
            timestamp = DateTimeOffset.Now,
        });

        if (!JsonlOut.Enabled)
        {
            Console.WriteLine($"{Center(active.Name, 10)} | {Center(udp, 10)} | {Center(ping, 8)} | {actionText, -26} ({d.Reason})");
        }
    }
    Console.WriteLine(new string('-', 74));
}

/// <summary>
/// Webhook notifier: genel özeti JSon olarak URL'ye POST eder. Sürüm sonucu
/// ayrıca `--jsonl` ise summary olayı olarak da satırlanır; burada ağ hatası
/// çalışmayı DÜŞÜRMEZ (notifier yan etkidir) ama stderr'e uyarı basar.
/// OK yalnızca HTTP 2xx aldığında doğrulanır.
/// </summary>
static async Task<bool> TryNotifyAsync(string webhook, object summary, bool ok)
{
    try
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var json = JsonSerializer.Serialize(summary, JsonlOut.Options);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(webhook, content);
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[notifier] webhook {resp.StatusCode} — {webhook}");
            return false;
        }
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[notifier] webhook gönderilemedi: {ex.Message}");
        return false;
    }
}

static string Center(string text, int width)
{
    if (text.Length >= width)
    {
        return text[..width];
    }
    var pad = width - text.Length;
    var left = pad / 2;
    return new string(' ', left) + text + new string(' ', pad - left);
}

static string Status(UdpProbeResult r) => r.Status switch
{
    UdpProbeStatus.Open => "AÇIK (open)",
    UdpProbeStatus.Blocked => "BLOKLI (blocked)",
    UdpProbeStatus.HandshakeNoResponse => "EL SIKIŞMA YANITSIZ (handshake no-response)",
    _ => "YANIT YOK (no-response)",
};

static string Mask(string key)
    => key.Length <= 8 ? "***" : key[..4] + "…" + key[^4..];

static List<GpnServerProfile> LoadProfiles(IReadOnlyList<string> confPaths)
{
    var profiles = new List<GpnServerProfile>();
    foreach (var path in confPaths)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[uyarı] dosya yok: {path}");
            continue;
        }

        var text = File.ReadAllText(path);
        var peers = WireguardFmt.ResolveConfig(text);
        if (peers is null || peers.Count == 0)
        {
            Console.Error.WriteLine($"[uyarı] çözümlenemedi: {path}");
            continue;
        }

        foreach (var peer in peers)
        {
            if (WireGuardServerCatalog.TryMap(peer, out var profile))
            {
                profiles.Add(profile);
            }
        }
    }

    // Aynı endpoint'i iki kez probe'lama (ör. iki conf aynı sunucuyu taşıyorsa).
    return profiles
        .GroupBy(p => (p.EndpointHost, p.EndpointPort))
        .Select(g => g.First())
        .ToList();
}

static (List<string> ConfPaths, ProbeToolOptions Options) ParseArgs(string[] args)
{
    var confs = new List<string>();
    var opts = new ProbeToolOptions();

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--help" or "-h":
                PrintUsage();
                Environment.Exit(0);
                break;
            case "--timeout-ms" when i + 1 < args.Length:
                opts.TimeoutMs = int.Parse(args[++i]);
                break;
            case "--samples" when i + 1 < args.Length:
                opts.Samples = Math.Max(1, int.Parse(args[++i]));
                break;
            case "--icmp" when i + 1 < args.Length:
                opts.ProbeMode = args[++i].ToLowerInvariant() switch
                {
                    "icmp" => GpnProbeMode.Icmp,
                    "tcp" => GpnProbeMode.Tcp,
                    _ => GpnProbeMode.Auto,
                };
                break;
            case "--no-handshake":
                opts.NoHandshake = true;
                break;
            case "--no-select":
                opts.NoSelect = true;
                break;
            case "--failover":
                opts.ShowFailover = true;
                break;
            case "--strict-udp":
                opts.StrictUdp = true;
                break;
            case "--monitor" when i + 1 < args.Length && int.TryParse(args[i + 1], out var monVal):
                opts.MonitorSeconds = Math.Max(1, monVal);
                i++;
                break;
            case "--monitor":
                opts.MonitorSeconds = 10; // varsayılan aralık
                break;
            case "--jsonl":
                opts.Jsonl = true;
                break;
            case "--webhook" when i + 1 < args.Length:
                opts.Webhook = args[++i];
                break;
            default:
                // Konfigürasyon dosyası olduğu varsayılır (konfigürasyon tarzı değilse).
                confs.Add(args[i]);
                break;
        }
    }

    return (confs, opts);
}

static void PrintUsage()
{
    Console.WriteLine("""
        AoGPN.GpnProbeTool — canlı İtalya/Almanya WireGuard sunucu teşhisi

        Kullanım:
          AoGPN.GpnProbeTool <conf1> [conf2 ...] [options]

        Örnek (iki sunucu rakip):
          AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf

        Seçenekler:
          --timeout-ms N    UDP/el sıkışma yanıt penceresi   (varsayılan 3000)
          --samples N       ICMP örnek sayısı                (varsayılan 4)
          --icmp mode       auto | icmp | tcp                (varsayılan auto)
          --no-handshake    gerçek WG el sıkışmasını atla (yalnızca junk-UDP)
          --no-select       otomatik seçimi atla (yalnızca sunucu başına diyagnostik)
          --failover        failover karar matrisini yazdır
          --strict-udp      NoResponse'u ölü say (katı politika)
          --monitor [N]     uzun süreli ön izleme — her N sn (varsayılan 10) ölçüm +
                            sunucu değişimi/mod düşüşü-kurtarma olaylarını zaman danklı bas
          --jsonl            makine-tüketilebilir JSON satırları (NDJSON) yaz — CI/notifier
          --webhook URL      bitişte genel özeti URL'ye POST et (notifier)
          --help            bu yardımı yaz

        Windows'ta WireGuard açıkken ForeignTunnelDetector testleri çakışabilir;
        bu araç yalnızca UDP el sıkışma + ICMP ölçümü yapar, tünel başlatmaz.
        """);
}        internal sealed class ProbeToolOptions
    {
        public int TimeoutMs { get; set; } = 3000;   // UDP / el-sıkışma yanıt penceresi
        public int PerSampleTimeoutMs { get; set; } = 1000; // tek ICMP örneği zaman aşımı
        public int Samples { get; set; } = 4;
        public GpnProbeMode ProbeMode { get; set; } = GpnProbeMode.Auto;
        public bool NoHandshake { get; set; }
        public bool NoSelect { get; set; }
        public bool ShowFailover { get; set; }
        public bool StrictUdp { get; set; }
        public int MonitorSeconds { get; set; }  // > 0 → uzun süreli ön izleme modu
        public bool Jsonl { get; set; }          // makine-tüketilebilir JSON satırları (NDJSON)
        public string? Webhook { get; set; }     // bitişte genel özeti POST edecek notifier URL'si
    }

    /// <summary>
    /// Makine-tüketilebilir JSON satırı (NDJSON) yazıcısı. <see cref="Enabled"/> açıkken
    /// her olay (server_probe / selection / failover / monitor_sample / monitor_event /
    /// summary) tek satır JSON olarak yazılır — CI, notifier veya `jq -s` tüketebilir.
    /// Nasıl açılır: `--jsonl`. Kapalıyken hiçbir JSON basılmaz (varsayılan insan metni).
    /// </summary>
    internal static class JsonlOut
    {
        public static bool Enabled;
        private static readonly JsonSerializerOptions Opts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

        /// <summary>Webhook notifier'ın aynı serileştirme kurallarını kullanması için.</summary>
        public static JsonSerializerOptions Options => Opts;

        public static void Write<T>(T payload)
        {
            if (Enabled)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, Opts));
            }
        }
    }