using ServiceLib.Common;

// ─────────────────────────────────────────────────────────────────────────
// AoGPN.GpnSessionTool — GPN oturum denetim günlüğü okuyucu
//
// VPN bağlantısı koptuğunda canlı iletişim kesildiğinden, bağlantı/test süreci
// gpn-session.log'a yazılır ve bu araçla sonradan okunur:
//
//   dotnet run --project AoGPN.GpnSessionTool -- --last
//   AoGPN.GpnSessionTool.exe --steps      (yalnızca GPN_* adım satırları)
//   AoGPN.GpnSessionTool.exe --sessions   (oturum özetleri)
//   AoGPN.GpnSessionTool.exe --path
// ─────────────────────────────────────────────────────────────────────────

var mode = args.Length > 0 ? args[0] : "--last";

// Hangi akış: GPN (varsayılan) veya VPN. `--file <yol>` ile doğrudan dosya.
var domain = "gpn";
if (args.Length >= 2 && args[0] is "--vpn")
{
    domain = "vpn";
    mode = args.Length > 1 ? args[1] : "--last";
}
else if (args.Length >= 2 && args[0] is "--gpn")
{
    mode = args.Length > 1 ? args[1] : "--last";
}

var path = domain == "vpn" ? VpnSessionLog.FilePath : GpnSessionLog.FilePath;
if (mode is "--file" or "-f")
{
    path = args.Length > (domain == "vpn" ? 2 : 1) ? args[(domain == "vpn" ? 2 : 1)] : path;
    mode = args.Length > (domain == "vpn" ? 3 : 2) ? args[(domain == "vpn" ? 3 : 2)] : "--last";
}
if (mode is "--path" or "-p")
{
    Console.WriteLine(path);
    return 0;
}

if (!File.Exists(path))
{
    Console.Error.WriteLine($"Oturum günlüğü yok: {path}");
    Console.Error.WriteLine("Önce uygulamayı çalıştırıp GPN Bağlan ile bir test yapın.");
    return 1;
}

var text = File.ReadAllText(path);

switch (mode)
{
    case "--last":
    case "-l":
        Console.WriteLine(LastSession(text));
        return 0;

    case "--steps":
    case "-s":
        foreach (var line in StepLines(text))
        {
            Console.WriteLine(line);
        }
        return 0;

    case "--sessions":
        Console.WriteLine(Summaries(text));
        return 0;

    case "--path":
        Console.WriteLine(path);
        return 0;

    default:
        Console.Error.WriteLine($"Bilinmeyen mod: {mode}  (--path | --last | --steps | --sessions)");
        return 2;
}

// ── okuma yardımcıları ──────────────────────────────────────────────────

static List<string> SessionBlocks(string text)
{
    const string start = "=== SESSION START ";
    const string end = "=== SESSION END ";
    var blocks = new List<string>();
    var i = 0;
    while ((i = text.IndexOf(start, i, StringComparison.Ordinal)) >= 0)
    {
        var j = text.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        var k = text.IndexOf(start, i + start.Length, StringComparison.Ordinal);
        var stop = (j >= 0 && (k < 0 || j < k)) ? j + end.Length : (k >= 0 ? k : text.Length);
        blocks.Add(text[i..stop].TrimEnd());
        i = (j >= 0 && (k < 0 || j < k)) ? stop : k;
    }
    return blocks;
}

static string LastSession(string text)
{
    var blocks = SessionBlocks(text);
    return blocks.Count > 0 ? blocks[^1] : "(oturum bloğu yok)";
}

static IEnumerable<string> StepLines(string text)
{
    var last = LastSession(text);
    foreach (var line in last.Split('\n'))
    {
        var trimmed = line.Trim();
        // Zaman damgalı GPN_* satırları "2026-...  GPN_SELECT ..." biçimindedir;
        // damgasız env/oturum işaretleri doğrudan başlar.
        if (trimmed.Contains(" GPN_", StringComparison.Ordinal)
            || trimmed.StartsWith("GPN_", StringComparison.Ordinal)
            || trimmed.StartsWith("=== SESSION", StringComparison.Ordinal)
            || trimmed.StartsWith("env ", StringComparison.Ordinal))
        {
            yield return trimmed;
        }
    }
}

static string Summaries(string text)
{
    var blocks = SessionBlocks(text);
    if (blocks.Count == 0)
    {
        return "(oturum bloğu yok)";
    }

    var outLines = new List<string> { $"{blocks.Count} oturum:" };
    foreach (var block in blocks)
    {
        var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var first = lines.FirstOrDefault() ?? "";
        var last = lines.LastOrDefault() ?? "";
        outLines.Add($"  {first}  →  {last}  ({lines.Length} satır)");
    }
    return string.Join(Environment.NewLine, outLines);
}
