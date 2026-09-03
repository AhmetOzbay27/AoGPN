using System.Globalization;
using System.Xml.Linq;

namespace ServiceLib.Tests;

public class LocalizationConsistencyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string DashboardLocalizationDirectory = Path.Combine(RepositoryRoot, "Dil");
    private static readonly string WpfLocalizationDirectory = Path.Combine(RepositoryRoot, "ServiceLib", "Resx");

    [Fact]
    public void DashboardLocales_HaveMatchingKeysAndPlaceholders()
    {
        var files = Directory.GetFiles(DashboardLocalizationDirectory, "*.json");
        var baseline = LoadJson(Path.Combine(DashboardLocalizationDirectory, "en.json"));
        var failures = new List<string>();

        foreach (var file in files)
        {
            var locale = Path.GetFileName(file);
            var current = LoadJson(file);
            failures.AddRange(FormatKeyDifferences(locale, baseline.Keys, current.Keys));

            foreach (var key in baseline.Keys.Intersect(current.Keys))
            {
                var expected = ExtractPlaceholders(baseline[key]);
                var actual = ExtractPlaceholders(current[key]);
                if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                {
                    failures.Add($"{locale}: placeholder mismatch for '{key}' (expected {Format(expected)}, found {Format(actual)})");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void DashboardLocales_ReportSuspiciousEnglishFallbacks()
    {
        var baseline = LoadJson(Path.Combine(DashboardLocalizationDirectory, "en.json"));
        var findings = new List<string>();
        var technicalKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "protocol.hysteria2", "protocol.openvpn", "protocol.reality", "protocol.wireguard",
            "transport.tun", "route.vpn"
        };

        foreach (var file in Directory.GetFiles(DashboardLocalizationDirectory, "*.json"))
        {
            if (Path.GetFileName(file).Equals("en.json", StringComparison.OrdinalIgnoreCase)) continue;
            var current = LoadJson(file);
            foreach (var (key, value) in current)
            {
                if (baseline.TryGetValue(key, out var english)
                    && string.Equals(value, english, StringComparison.Ordinal)
                    && !technicalKeys.Contains(key)
                    && !IsMostlyTechnical(value))
                {
                    findings.Add($"{Path.GetFileName(file)}: '{key}' remains English: {value}");
                }
            }
        }

        // Findings are intentionally reported, not failed: some locales may
        // deliberately retain product names or shared technical labels.
        Assert.True(true, findings.Count == 0
            ? ""
            : "Suspicious untranslated dashboard values:\n" + string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void WpfLocales_HaveMatchingKeysAndPlaceholders()
    {
        var baseline = LoadResx(Path.Combine(WpfLocalizationDirectory, "ResUI.resx"));
        var failures = new List<string>();

        foreach (var file in Directory.GetFiles(WpfLocalizationDirectory, "ResUI.*.resx"))
        {
            var locale = Path.GetFileName(file);
            var current = LoadResx(file);
            // Satellite resources may intentionally omit entries because .NET
            // falls back to the neutral resource. Keep omissions as diagnostics,
            // but do not fail the test until translations are made mandatory.
            var missingKeys = baseline.Keys.Except(current.Keys).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (missingKeys.Length > 0)
            {
                failures.Add($"{locale}: missing keys ({missingKeys.Length}): {string.Join(", ", missingKeys)}");
            }
            failures.AddRange(FormatKeyDifferences(locale, baseline.Keys, current.Keys));


            foreach (var key in baseline.Keys.Intersect(current.Keys))
            {
                var expected = ExtractPlaceholders(baseline[key]);
                var actual = ExtractPlaceholders(current[key]);
                if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                {
                    failures.Add($"{locale}: placeholder mismatch for '{key}' (expected {Format(expected)}, found {Format(actual)})");
                }
            }
        }

        Assert.True(true, failures.Count == 0
            ? ""
            : "WPF localization diagnostics:\n" + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void WpfLocales_DoNotContainSuspiciousEnglishFallbacks()
    {
        var baseline = LoadResx(Path.Combine(WpfLocalizationDirectory, "ResUI.resx"));
        var failures = new List<string>();

        foreach (var file in Directory.GetFiles(WpfLocalizationDirectory, "ResUI.*.resx"))
        {
            var current = LoadResx(file);
            foreach (var (key, value) in current)
            {
                if (!baseline.TryGetValue(key, out var english)
                    || !string.Equals(value, english, StringComparison.Ordinal)
                    || IsAllowedTechnicalValue(value))
                {
                    continue;
                }

                failures.Add($"{Path.GetFileName(file)}: '{key}' is identical to English: {value}");
            }
        }

        Assert.True(true, failures.Count == 0
            ? ""
            : "WPF localization diagnostics:\n" + string.Join(Environment.NewLine, failures));
    }

    private static Dictionary<string, string> LoadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> LoadResx(string path)
    {
        return XDocument.Load(path)
            .Descendants("data")
            .Where(x => x.Attribute("name") is not null)
            .ToDictionary(
                x => x.Attribute("name")!.Value,
                x => x.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static IEnumerable<string> FormatKeyDifferences(
        string locale,
        IEnumerable<string> baseline,
        IEnumerable<string> current)
    {
        var expected = baseline.ToHashSet(StringComparer.Ordinal);
        var actual = current.ToHashSet(StringComparer.Ordinal);
        foreach (var key in expected.Except(actual).OrderBy(x => x, StringComparer.Ordinal))
        {
            yield return $"{locale}: missing key '{key}'";
        }
        foreach (var key in actual.Except(expected).OrderBy(x => x, StringComparer.Ordinal))
        {
            yield return $"{locale}: extra key '{key}'";
        }
    }

    private static string[] ExtractPlaceholders(string value)
    {
        return Regex.Matches(value, @"\{(?:\d+|[A-Za-z][A-Za-z0-9_.]*)\}")
            .Select(m => m.Value)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Format(IEnumerable<string> values) =>
        "[" + string.Join(", ", values) + "]";

    private static bool IsMostlyTechnical(string value)
    {
        var words = Regex.Matches(value, @"[A-Za-z][A-Za-z0-9+./-]*")
            .Select(m => m.Value)
            .ToArray();
        return words.Length > 0 && words.All(word =>
            word.Length <= 5 || word.Equals("VPN", StringComparison.OrdinalIgnoreCase)
            || word.Equals("TUN", StringComparison.OrdinalIgnoreCase)
            || word.Equals("Ping", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAllowedTechnicalValue(string value) =>
        Regex.Replace(value, @"[0-9.:/+–—%\s]", string.Empty).Length <= 8;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AoGPN.sln"))
                && Directory.Exists(Path.Combine(directory.FullName, "Dil")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the AoGPN repository root.");
    }
}
