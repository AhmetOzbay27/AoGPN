using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ServiceLib.Tests.Services;

/// <summary>
/// CI dosyalarının GEÇERLİ olduğunu kilitler. Bu sınıftaki hatalar derleyicinin
/// görebileceği türden değildir: dosya yerelde sorunsuz görünür, YAML ayrışır,
/// test paketi yeşil kalır — ama GitHub iş akışını çalıştırmayı reddeder ve koşum
/// HİÇ İŞ OLUŞTURMADAN kırılır. Yani hata "kırmızı bir CI" olarak bile anlaşılmaz;
/// geriye yalnızca iş listesi boş, anlamsız bir koşum kalır.
///
/// Kilitlenen kural ölçülmüş bir olaydan geliyor:
/// <c>gpn-servers-health.yml</c> iki adımda <c>if: ${{ secrets.X != '' }}</c>
/// kullanıyordu. <c>secrets</c> bağlamı <c>if</c> koşullarında kullanılamaz; iş
/// akışı "Unrecognized named-value: 'secrets'" ile geçersiz sayıldı ve o iş
/// akışının 7 koşumunun 7'si boş kırıldı. Doğru yol: secret'ı <c>env</c>'e alıp
/// koşulu <c>env.X != ''</c> ile kurmak.
/// </summary>
public sealed class WorkflowValidityTests
{
    [Fact]
    public void EveryWorkflowAndActionFileIsParsableYaml()
    {
        var broken = new List<string>();

        foreach (var file in CiFiles())
        {
            try
            {
                _ = Load(file);
            }
            catch (YamlException ex)
            {
                broken.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        broken.Should().BeEmpty("bozuk bir iş akışı dosyası Actions sekmesinde hiç çalışmaz");
    }

    [Fact]
    public void NoIfConditionReferencesTheSecretsContext()
    {
        var offenders = new List<string>();

        foreach (var file in CiFiles())
        {
            foreach (var (key, value) in ScalarPairs(Load(file)))
            {
                if (string.Equals(key, "if", StringComparison.Ordinal) &&
                    Regex.IsMatch(value, @"\bsecrets\."))
                {
                    offenders.Add($"{Path.GetFileName(file)} → if: {value}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "`secrets` bağlamı `if` koşullarında kullanılamaz: GitHub iş akışını geçersiz sayar "
            + "ve koşum hiç iş oluşturmadan kırılır. Secret'ı env'e alıp `env.X != ''` ile kontrol edin");
    }

    [Fact]
    public void TheHealthWorkflowGatesOnEnvironmentVariablesRatherThanSecrets()
    {
        // Kuralların kendisi değil, düzeltmenin ŞEKLİ kilitlenir: anahtarlar
        // yoksa iş yeşil kalmalı ve neden atlandığını söyleyen bir notice olmalı.
        var path = Path.Combine(CiDirectory, "workflows", "gpn-servers-health.yml");
        var document = File.ReadAllText(path);

        document.Should().Contain("::notice title=", "atlanma nedeni bildirilmeli");
        document.Should().Contain("ITALY_CONF_B64: ${{ secrets.ITALY_CONF_B64 }}",
            "koşullarda okunabilmesi için secret env'e taşınmalı");
        document.Should().Contain("env.GPN_WEBHOOK_URL != ''", "webhook koşulu env üzerinden kurulmalı");
    }

    // ── yardımcılar ────────────────────────────────────────────────────

    private static YamlNode Load(string path)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(path));
        stream.Load(reader);
        return stream.Documents[0].RootNode;
    }

    /// <summary>Dosyadaki tüm skaler anahtar/değer çiftlerini (iç içe dahil) gezer.</summary>
    private static IEnumerable<(string Key, string Value)> ScalarPairs(YamlNode? node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var pair in map.Children)
                {
                    if (pair.Key is YamlScalarNode key && pair.Value is YamlScalarNode value)
                    {
                        yield return (key.Value ?? string.Empty, value.Value ?? string.Empty);
                    }

                    foreach (var nested in ScalarPairs(pair.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case YamlSequenceNode sequence:
                foreach (var item in sequence.Children)
                {
                    foreach (var nested in ScalarPairs(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static IEnumerable<string> CiFiles()
    {
        var workflows = Directory.Exists(Path.Combine(CiDirectory, "workflows"))
            ? Directory.EnumerateFiles(Path.Combine(CiDirectory, "workflows"), "*.y*ml")
            : [];

        var actionsDirectory = Path.Combine(CiDirectory, "actions");
        var actions = Directory.Exists(actionsDirectory)
            ? Directory.EnumerateDirectories(actionsDirectory)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "action.y*ml"))
            : [];

        return workflows.Concat(actions).OrderBy(file => file, StringComparer.Ordinal);
    }

    /// <summary>
    /// Testler depo ağacından koşar; <c>.github</c>'i yukarı doğru arayarak bulur.
    /// </summary>
    private static string CiDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, ".github");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(".github dizini bulunamadi");
        }
    }
}
