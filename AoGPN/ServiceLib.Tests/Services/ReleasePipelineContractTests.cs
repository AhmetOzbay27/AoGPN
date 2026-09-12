using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Yayın hattının (<c>.github/workflows/release.yml</c>) uygulamayla olan
/// sözleşmesini kilitler.
///
/// Bu kuralların hiçbiri derleyicinin görebileceği türden değildir: ihlal
/// edildiğinde çözüm derlenir, bütün testler yeşil kalır ve sürüm "başarıyla"
/// yayımlanır — ama kullanıcı yeni sürümü HİÇ görmez ya da indirdiği paketi
/// uygulayamaz. Bu yüzden hat metin olarak değil YAML olarak ayrıştırılır ve
/// yapı üzerinden sınanır.
///
/// Kilitlenen kurallar:
///   1. Yayınlanan varlık adları, uygulamanın platformuna göre indirmek için
///      aradığı adlarla (<see cref="AppUpdateChecker.ExpectedAssetName()"/>)
///      birebir aynıdır — ad tutmazsa güncelleme "zip bulunamadı" ile biter.
///   2. Sürüm ÖN SÜRÜM olarak işaretlenmez ve "latest" yapılır; aksi hâlde
///      <c>/releases/latest</c> ucu onu göstermez (ön sürümler atlanır).
///   3. Paket kökünde <c>AoGPN.exe</c>'nin yanında <c>AoGPN.Updater.exe</c>
///      bulunur; indirilen paketi yerine koyan tek bileşen odur.
///   4. <c>v*</c> etiketini yalnızca BU iş akışı yazar — aynı etikete iki hat
///      yazarsa GitHub Releases'te aynı varlık için yarış oluşur.
/// </summary>
public sealed class ReleasePipelineContractTests
{
    private const string ReleaseWorkflowFile = "release.yml";

    // ── 1. Varlık adları: hat ↔ uygulama ───────────────────────────────

    [Theory]
    [InlineData(Architecture.X64, "AoGPN-windows-64.zip")]
    [InlineData(Architecture.Arm64, "AoGPN-windows-arm64.zip")]
    public void ExpectedAssetName_MapsWindowsArchitecturesToTheShippedNames(Architecture architecture, string expected)
        // Platformdan bağımsız: eşleme saf fonksiyon olduğu için CI ubuntu'da
        // koşarken de Windows istemcisinin hangi adı isteyeceği doğrulanabilir.
        => AppUpdateChecker.ExpectedAssetName(OSPlatform.Windows, architecture).Should().Be(expected);

    [Fact]
    public void PackageMatrix_PublishesExactlyTheAssetNamesWindowsClientsRequest()
    {
        var published = MatrixInclude("package")
            .Select(entry => entry.GetValueOrDefault("asset"))
            .ToList();

        published.Should().NotContainNulls("her matris satırı bir varlık adı taşımalı");
        published.Should().NotContain(string.Empty);

        published.Should().BeEquivalentTo(
            [
                AppUpdateChecker.ExpectedAssetName(OSPlatform.Windows, Architecture.X64),
                AppUpdateChecker.ExpectedAssetName(OSPlatform.Windows, Architecture.Arm64),
            ],
            "yayınlanan varlık adları uygulamanın indirmek için aradığı adlarla birebir aynı olmalı");
    }

    // ── 2. Ön sürüm olmama ─────────────────────────────────────────────

    [Fact]
    public void ReleaseJob_ForcesTheReleaseToBeStableAndLatest()
    {
        var edit = StepRuns("release")
            .FirstOrDefault(run => run.Contains("gh release edit", StringComparison.Ordinal));

        edit.Should().NotBeNull(
            "sürümün ön sürüm işareti kaldırılmazsa /releases/latest ucu onu atlar ve güncelleme hiç sunulmaz");
        edit.Should().Contain("--prerelease=false");
        edit.Should().Contain("--latest");
    }

    [Fact]
    public void NoStepMarksTheReleaseAsAPrerelease()
    {
        var text = ReadWorkflow(ReleaseWorkflowFile);

        Regex.IsMatch(text, @"--prerelease\s*=\s*true")
            .Should().BeFalse("ön sürüm işaretli bir yayın güncelleme denetiminde hiç görünmez");

        // Actions girdisi olarak işaretlenmesi de aynı sonucu doğurur.
        Regex.IsMatch(text, @"^\s*prerelease:\s*true\s*$", RegexOptions.Multiline)
            .Should().BeFalse("bir adım sürümü 'prerelease: true' ile açmamalı");
    }

    // ── 3. Paket kökünde güncelleyici ──────────────────────────────────

    [Fact]
    public void PackageJob_BuildsTheUpdaterIntoTheSameStageAsTheApp()
    {
        var runs = StepRuns("package").ToList();

        var appPublish = runs.FirstOrDefault(run =>
            run.Contains("dotnet publish", StringComparison.Ordinal) &&
            run.Contains("AoGPN.csproj", StringComparison.Ordinal));

        var updaterPublish = runs.FirstOrDefault(run =>
            run.Contains("dotnet publish", StringComparison.Ordinal) &&
            run.Contains("AoGPN.Updater.csproj", StringComparison.Ordinal));

        appPublish.Should().NotBeNull("uygulama yayımlanmalı");
        updaterPublish.Should().NotBeNull("yan güncelleyici ayrı bir yürütülebilir olarak yayımlanmalı");

        appPublish.Should().Contain("stage/${{ matrix.rid }}");
        // Güncelleyici $stage'e yazar ve $stage aynı betikte uygulamanın klasörüne
        // eşitlenir; iki yayın ayrı klasörlere düşerse zip'te exe'ler yan yana olmaz.
        updaterPublish.Should().Contain("stage/${{ matrix.rid }}");
    }

    [Fact]
    public void PackageJob_VerifiesBothExecutablesBeforeZipping()
    {
        var verify = StepRuns("package")
            .FirstOrDefault(run => run.Contains("Test-Path", StringComparison.Ordinal) &&
                                   run.Contains("AoGPN.Updater.exe", StringComparison.Ordinal));

        verify.Should().NotBeNull("paket içeriği sıkıştırmadan ÖNCE doğrulanmalı");
        verify.Should().Contain("throw", "eksik dosya sürümü kırmalı, uyarı olarak geçmemeli");

        // Dosya adlarının adımın herhangi bir yerinde geçmesi yetmez: VAR OLMASI
        // GEREKENLER listesinde olmalı — aksi halde güncelleyici eksik çıksa da
        // sürüm yeşil geçerdi. Liste yapısı aranır, tırnak/biçim değil.
        var requiredList = Regex.Match(verify, @"foreach\s*\(\s*\$required\s+in\s+(?<list>[^)]*)\)");
        requiredList.Success.Should().BeTrue("paket, zorunlu dosyaları bir listeden döngüyle doğrulamalı");
        requiredList.Groups["list"].Value.Should().Contain("AoGPN.exe");
        requiredList.Groups["list"].Value.Should().Contain("AoGPN.Updater.exe");
    }

    [Fact]
    public void ZipPackagesTheStageContentsSoBothExecutablesSitAtTheArchiveRoot()
    {
        var zip = StepRuns("package")
            .FirstOrDefault(run => run.Contains("Compress-Archive", StringComparison.Ordinal));

        zip.Should().NotBeNull("paket bir zip üretmeli");

        // Kritik nokta yolun sonundaki '/*': sahne klasörünün İÇERİĞİ sıkıştırılır.
        // Bir üst klasör sıkıştırılırsa AoGPN.Updater.exe zip kökünde değil bir alt
        // klasörde kalır ve güncelleyici hiç bulunamaz.
        Regex.IsMatch(zip, @"Compress-Archive\s+-Path\s+""[^""]*/\*""")
            .Should().BeTrue("zip kökü sahne klasörünün içeriği olmalı (…/<stage>/* ile sıkıştırılmalı)");

        zip.Should().Contain("matrix.asset");
    }

    // ── 4. Etiket tetikleyicisinin tek sahibi ──────────────────────────

    [Fact]
    public void ReleaseWorkflow_TriggersOnVersionTags()
        => TagPatterns(Path.Combine(WorkflowsDirectory, ReleaseWorkflowFile)).Should().Contain("v*");

    [Fact]
    public void OnlyTheReleaseWorkflowOwnsTheVersionTagTrigger()
    {
        var owners = WorkflowFiles()
            .Where(file => TagPatterns(file).Any(pattern => pattern.StartsWith('v')))
            .Select(Path.GetFileName)
            .ToList();

        owners.Should().BeEquivalentTo(
            [ReleaseWorkflowFile],
            "aynı v* etiketine iki hat yazarsa GitHub Releases'te aynı varlık için yarış oluşur");
    }

    // ── Depo/hat erişimi ───────────────────────────────────────────────

    /// <summary>
    /// Testler depo ağacından koşar; <c>.github/workflows</c>'u yukarı doğru
    /// arayarak bulur (testin bin/ klasöründen göreli yol varsayılmaz).
    /// </summary>
    private static string WorkflowsDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, ".github", "workflows");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "'.github/workflows' bulunamadı: bu test depo ağacından çalıştırılmalı.");
        }
    }

    private static IEnumerable<string> WorkflowFiles() =>
        Directory.EnumerateFiles(WorkflowsDirectory, "*.y*ml")
            .Where(file => file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                           file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal);

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(WorkflowsDirectory, fileName));

    private static YamlMappingNode ReleaseRoot() => LoadYaml(Path.Combine(WorkflowsDirectory, ReleaseWorkflowFile));

    private static YamlMappingNode LoadYaml(string path)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(path));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    /// <summary>
    /// Anahtar araması bilerek dize karşılaştırmasıyla yapılır: YAML'da
    /// <c>on</c> gibi anahtarlar şema bazlı çözümlemede bool'a dönebilir, ham
    /// gösterim modeli ise özgün skaleri korur.
    /// </summary>
    private static YamlNode? Child(YamlNode? node, string key)
    {
        if (node is not YamlMappingNode map)
        {
            return null;
        }

        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string? Text(YamlNode? node) => (node as YamlScalarNode)?.Value;

    private static IEnumerable<YamlMappingNode> JobSteps(string job)
        => (Child(Child(ReleaseRoot(), "jobs"), job) is { } jobNode
                ? Child(jobNode, "steps") as YamlSequenceNode
                : null)
            ?.OfType<YamlMappingNode>() ?? [];

    private static IEnumerable<string> StepRuns(string job)
        => JobSteps(job).Select(step => Text(Child(step, "run"))).OfType<string>();

    private static List<Dictionary<string, string?>> MatrixInclude(string job)
    {
        var include = Child(Child(Child(Child(ReleaseRoot(), "jobs"), job), "strategy"), "matrix") is { } matrix
            ? Child(matrix, "include") as YamlSequenceNode
            : null;

        return (include ?? [])
            .OfType<YamlMappingNode>()
            .Select(entry => entry.Children
                .Where(pair => pair.Key is YamlScalarNode)
                .ToDictionary(
                    pair => ((YamlScalarNode)pair.Key).Value ?? string.Empty,
                    pair => Text(pair.Value)))
            .ToList();
    }

    private static IEnumerable<string> TagPatterns(string path)
        => (Child(Child(Child(LoadYaml(path), "on"), "push"), "tags") as YamlSequenceNode ?? [])
            .Select(Text)
            .OfType<string>();
}
