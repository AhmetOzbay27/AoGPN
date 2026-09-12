using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GERÇEK uçtan uca güncelleme testi: sahte bir "kurulu AoGPN" klasörü ile sahte
/// bir "yeni sürüm" zip'i üretir ve <c>AoGPN.Updater.exe</c>'yi AYRI BİR SÜREÇ
/// olarak çalıştırır.
///
/// Neden taklit (mock) değil: güncelleyici, uygulamayı YERİNDE değiştirebilen tek
/// bileşendir ve kendi süreci içinde çalışır. Argüman biçimi ve yol çözümlemesi
/// birim testleriyle (AppUpdateUpdaterContractTests, AppUpdaterLauncherTests)
/// zaten kilitli; burada sınanan şey o sözleşmenin GERÇEKTEN işlemesi: dosyaların
/// üzerine yazılması, çekirdek ikililerinin korunması, kullanıcı verisine
/// dokunulmaması, zip-slip reddi ve yeniden başlatma devri.
///
/// Sınır: yeniden başlatılan sürecin KENDİ davranışı gözlenmez (güncelleyici onu
/// ayrı bir süreç olarak bırakır); doğrulanan şey devrin gerçekleştiğidir —
/// çıkış kodu 0 ve günlükte gerçek bir pid.
/// </summary>
public sealed class AppUpdaterEndToEndTests
{
    private static readonly Lazy<string?> UpdaterExecutable = new(FindUpdaterExecutable);

    private static readonly (string Name, string Content)[] NewVersionEntries =
    [
        ("AoGPN.exe", "NEW-APP-1.1.2"),
        ("AoGPN.Updater.exe", "NEW-UPDATER"),
        ("Dil/en.json", "new-strings"),
        ("Temalar/app.js", "new-dashboard"),
        ("Temalar/new-file.js", "BRAND-NEW"),
        // Hedefte zaten var olan çekirdek ikilileri: ATLANMALI (yeniden indirme yok).
        ("bin/mihomo/mihomo.exe", "SHIPPED-CORE"),
        ("bin/geosite.dat", "SHIPPED-GEO"),
        // Hedefte olmayan çekirdek dosyası: yazılmalı.
        ("bin/newcore.txt", "NEW-CORE"),
    ];

    // ── 1. Asıl senaryo: gerçek release düzeni (tek kök klasörlü zip) ──

    [Fact]
    public void AppliesTheReleaseLayoutOverAnExistingInstallAndKeepsUserData()
    {
        var updater = RequireUpdater();
        using var fixture = new InstallFixture();
        fixture.SeedInstalledVersion();
        var package = fixture.CreatePackage("AoGPN-windows-64/", NewVersionEntries);

        var result = Run(updater, "--zip", package, "--target", fixture.InstallDirectory,
            "--no-restart", "--wait-seconds", "1");

        result.ExitCode.Should().Be(0, result.Output);

        // Sürüm dosyaları yenilendi
        fixture.Read("AoGPN.exe").Should().Be("NEW-APP-1.1.2");
        fixture.Read("AoGPN.Updater.exe").Should().Be("NEW-UPDATER");
        fixture.Read("Dil/en.json").Should().Be("new-strings");
        fixture.Read("Temalar/app.js").Should().Be("new-dashboard");
        // Pakette olmayan yeni dosya da yazıldı
        fixture.Read("Temalar/new-file.js").Should().Be("BRAND-NEW");
        // Çekirdekler: var olanlar korunur, yeni olan eklenir
        fixture.Read("bin/mihomo/mihomo.exe").Should().Be("PRE-EXISTING-CORE");
        fixture.Read("bin/geosite.dat").Should().Be("PRE-EXISTING-GEO");
        fixture.Read("bin/newcore.txt").Should().Be("NEW-CORE");
        // Kullanıcı verisi: paket bu dosyaları içermese de bozulmamalı
        fixture.Read("Logs/app.log").Should().Be("user-log");
        fixture.Read("guiConfigs/config.json").Should().Be("user-config");
        fixture.Read("AoGPN.db").Should().Be("user-database");
        // İndirilen paket, iş bittikten sonra temizlenir
        File.Exists(package).Should().BeFalse("güncelleme paketi uygulandıktan sonra silinir");
    }

    // ── 2. Kök klasörsüz (düz) zip de doğru açılmalı ───────────────────

    [Fact]
    public void AlsoAppliesAPackageWithoutARootFolder()
    {
        var updater = RequireUpdater();
        using var fixture = new InstallFixture();
        fixture.SeedInstalledVersion();
        var package = fixture.CreatePackage(rootPrefix: string.Empty, NewVersionEntries);

        var result = Run(updater, "--zip", package, "--target", fixture.InstallDirectory,
            "--no-restart", "--wait-seconds", "1");

        result.ExitCode.Should().Be(0, result.Output);
        fixture.Read("AoGPN.exe").Should().Be("NEW-APP-1.1.2");
        fixture.Read("Temalar/new-file.js").Should().Be("BRAND-NEW");
        fixture.Read("bin/mihomo/mihomo.exe").Should().Be("PRE-EXISTING-CORE");
    }

    // ── 3. Zip-slip: hedefin dışına yazan girdi reddedilmeli ───────────

    [Fact]
    public void RejectsArchiveEntriesThatEscapeTheTargetDirectory()
    {
        var updater = RequireUpdater();
        using var fixture = new InstallFixture();
        fixture.SeedInstalledVersion();
        var package = fixture.CreatePackage(
            rootPrefix: string.Empty,
            ("AoGPN.exe", "NEW-APP-1.1.2"),
            ("../escaped.txt", "SHOULD-NEVER-EXIST"));

        var result = Run(updater, "--zip", package, "--target", fixture.InstallDirectory,
            "--no-restart", "--wait-seconds", "1");

        // Tehlikeli girdi atlanır ama iş ölmez: normal girdi uygulanmış olmalı.
        result.ExitCode.Should().Be(0, result.Output);
        fixture.Read("AoGPN.exe").Should().Be("NEW-APP-1.1.2");

        var escaped = Path.Combine(Path.GetDirectoryName(fixture.InstallDirectory)!, "escaped.txt");
        File.Exists(escaped).Should().BeFalse("hedef dışına yazan arşiv girdisi yazılmamalı");
        result.Output.Should().Contain("hedef dışına çıkan arşiv girdisi atlandı");
    }

    // ── 4. Hata yolları: kurulum bozulmadan net çıkış kodları ──────────

    [Fact]
    public void ReportsAMissingPackageWithoutTouchingTheInstall()
    {
        var updater = RequireUpdater();
        using var fixture = new InstallFixture();
        fixture.SeedInstalledVersion();

        var result = Run(updater, "--zip", Path.Combine(fixture.Root, "yok.zip"),
            "--target", fixture.InstallDirectory, "--no-restart", "--wait-seconds", "1");

        result.ExitCode.Should().Be(3, result.Output);
        result.Output.Should().Contain("Güncelleme paketi bulunamadı");
        fixture.Read("AoGPN.exe").Should().Be("OLD-APP-1.1.1", "paket yokken kurulum değişmemeli");
    }

    [Fact]
    public void ReportsAMissingRestartTargetAfterApplyingTheFiles()
    {
        var updater = RequireUpdater();
        using var fixture = new InstallFixture();
        // AoGPN.exe yok — ve paket de onu içermiyor, aksi hâlde açma işlemi
        // dosyayı yerine koyar ve hedef "bulunmuş" olurdu.
        Directory.CreateDirectory(Path.Combine(fixture.InstallDirectory, "bin"));
        var package = fixture.CreatePackage(rootPrefix: string.Empty, ("Temalar/app.js", "new-dashboard"));

        var result = Run(updater, "--zip", package, "--target", fixture.InstallDirectory,
            "--no-restart", "--wait-seconds", "1");

        result.ExitCode.Should().Be(4, result.Output);
        result.Output.Should().Contain("yeniden başlatılacak dosya yok");
    }

    // ── 5. Yeniden başlatma devri ──────────────────────────────────────

    [Fact]
    public void HandsTheRestartOverToARealProcess()
    {
        var updater = RequireUpdater();
        using var fixture = new InstallFixture();
        fixture.SeedInstalledVersion();
        var package = fixture.CreatePackageWithRunnableApp("AoGPN-windows-64/", updater);
        var restartTarget = Path.Combine(fixture.InstallDirectory, "AoGPN.exe");

        var result = Run(updater, "--zip", package, "--target", fixture.InstallDirectory,
            "--restart", restartTarget, "--wait-seconds", "1");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().MatchRegex(@"AoGPN yeniden başlatıldı \(pid=[1-9][0-9]*\)");
        // Yeniden başlatılan dosya, paketin yerine koyduğu gerçek uygulamadır.
        new FileInfo(restartTarget).Length.Should().BeGreaterThan(1024);
    }

    [Fact]
    public void HonorsNoRestartByNotStartingAnything()
    {
        var updater = RequireUpdater();
        using var fixture = new InstallFixture();
        fixture.SeedInstalledVersion();
        var package = fixture.CreatePackage("AoGPN-windows-64/", NewVersionEntries);

        var result = Run(updater, "--zip", package, "--target", fixture.InstallDirectory,
            "--no-restart", "--wait-seconds", "1");

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().Contain("--no-restart verildi");
    }

    // ── 6. Boşluk içeren kurulum yolu ─────────────────────────────────

    [Fact]
    public void HandlesAnInstallPathContainingSpaces()
    {
        var updater = RequireUpdater();
        using var fixture = new InstallFixture(subDirectory: "AoGPN Pro Kurulum");
        fixture.SeedInstalledVersion();
        var package = fixture.CreatePackage("AoGPN-windows-64/", NewVersionEntries);

        var result = Run(updater, "--zip", package, "--target", fixture.InstallDirectory,
            "--no-restart", "--wait-seconds", "1");

        result.ExitCode.Should().Be(0, result.Output);
        fixture.Read("AoGPN.exe").Should().Be("NEW-APP-1.1.2");
    }

    // ── Altyapı ────────────────────────────────────────────────────────

    private static string RequireUpdater()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Yan güncelleyici yalnızca Windows'ta çalışan bir exe'dir.");
        }

        var path = UpdaterExecutable.Value;
        if (path is null)
        {
            Assert.Skip("AoGPN.Updater.exe derlenmemiş; " +
                "'dotnet build AoGPN.Updater/AoGPN.Updater.csproj' çalıştırın.");
        }

        return path!;
    }

    /// <summary>
    /// Derlenmiş güncelleyiciyi bulur (<c>AoGPN.Updater/bin/**</c> altında, en yeni).
    /// Yayın biçimi önemsizdir: sınanan şey davranıştır, paketleme değil.
    /// </summary>
    private static string? FindUpdaterExecutable()
    {
        var binDirectory = Path.Combine(ProjectDirectory, "AoGPN.Updater", "bin");
        if (!Directory.Exists(binDirectory))
        {
            return null;
        }

        var fileName = Utils.GetExeName("AoGPN.Updater");
        return Directory.EnumerateFiles(binDirectory, fileName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Testler depo ağacından koşar; <c>AoGPN.slnx</c> ile proje kökünü bulur.</summary>
    private static string ProjectDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AoGPN.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("'AoGPN.slnx' bulunamadı: bu test depo ağacından çalıştırılmalı.");
        }
    }

    private static (int ExitCode, string Output) Run(string updater, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = updater,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList kullanılır: boşluk içeren yollar tek tek ve doğru tırnaklanır
        // (elle tırnaklanmış bir dize sözleşmeyi maskelerdi). Argümanlar süreç
        // BAŞLATILMADAN önce eklenmelidir.
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.ExitCode, output);
    }

    /// <summary>Sahte kurulum klasörü + paket üreticisi; test bitince siler.</summary>
    private sealed class InstallFixture : IDisposable
    {
        public InstallFixture(string subDirectory = "install")
        {
            Root = Path.Combine(Path.GetTempPath(), $"aogpn-update-e2e-{Guid.NewGuid():N}");
            InstallDirectory = Path.Combine(Root, subDirectory);
            Directory.CreateDirectory(InstallDirectory);
        }

        public string Root { get; }

        public string InstallDirectory { get; }

        /// <summary>Eski sürüm + kullanıcı verisi + önceden indirilmiş çekirdekler.</summary>
        public void SeedInstalledVersion()
        {
            Directory.CreateDirectory(Path.Combine(InstallDirectory, "bin", "mihomo"));
            Directory.CreateDirectory(Path.Combine(InstallDirectory, "Dil"));
            Directory.CreateDirectory(Path.Combine(InstallDirectory, "Temalar"));
            Directory.CreateDirectory(Path.Combine(InstallDirectory, "Logs"));
            Directory.CreateDirectory(Path.Combine(InstallDirectory, "guiConfigs"));

            Write("AoGPN.exe", "OLD-APP-1.1.1");
            Write("AoGPN.Updater.exe", "OLD-UPDATER");
            Write("Dil/en.json", "old-strings");
            Write("Temalar/app.js", "old-dashboard");
            Write(Path.Combine("bin", "mihomo", "mihomo.exe"), "PRE-EXISTING-CORE");
            Write(Path.Combine("bin", "geosite.dat"), "PRE-EXISTING-GEO");
            // Kullanıcı verisi: hiçbir paket bunları içermez, güncelleme de silmemeli.
            Write(Path.Combine("Logs", "app.log"), "user-log");
            Write(Path.Combine("guiConfigs", "config.json"), "user-config");
            Write("AoGPN.db", "user-database");
        }

        public string CreatePackage(string rootPrefix, params (string Name, string Content)[] entries)
        {
            var path = Path.Combine(Root, $"package-{Guid.NewGuid():N}.zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(rootPrefix + name).Open());
                writer.Write(content);
            }

            return path;
        }

        /// <summary>
        /// Paketin <c>AoGPN.exe</c>'si GERÇEK bir yürütülebilir olsun: yeniden
        /// başlatma hedefi tam olarak o dosyadır, dolayısıyla yer tutucu bir metin
        /// bu senaryoyu sınayamaz. Güncelleyicinin çıktısı <c>AoGPN.*</c> adlarına
        /// gömülür (uygulama ana makinesi kendi adıyla eşleşen dll/runtimeconfig
        /// arar, bu yüzden yalnızca exe yetmez).
        /// </summary>
        public string CreatePackageWithRunnableApp(string rootPrefix, string updaterPath)
        {
            var path = Path.Combine(Root, $"package-{Guid.NewGuid():N}.zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            foreach (var (name, content) in NewVersionEntries.Where(e => e.Name != "AoGPN.exe"))
            {
                AddText(archive, rootPrefix + name, content);
            }

            foreach (var file in CompanionFiles(updaterPath))
            {
                AddBytes(archive, rootPrefix + RenamedAppEntry(file), File.ReadAllBytes(file));
            }

            return path;
        }

        /// <summary>Güncelleyicinin yanındaki, uygulamayı çalıştırmak için gereken dosyalar.</summary>
        private static IEnumerable<string> CompanionFiles(string updaterPath)
        {
            var outputDirectory = Path.GetDirectoryName(updaterPath)!;
            var baseName = Path.GetFileNameWithoutExtension(updaterPath);

            return Directory.EnumerateFiles(outputDirectory, baseName + ".*")
                .Where(file => Path.GetExtension(file) is ".exe" or ".dll" or ".json");
        }

        /// <summary>AoGPN.Updater.deps.json -> AoGPN.deps.json, .exe -> AoGPN.exe, …</summary>
        private static string RenamedAppEntry(string file)
            => "AoGPN" + Path.GetFileName(file)["AoGPN.Updater".Length..];

        private static void AddText(ZipArchive archive, string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        private static void AddBytes(ZipArchive archive, string name, byte[] content)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(content);
        }

        public void Write(string relativePath, string content)
        {
            var full = Path.Combine(InstallDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public string Read(string relativePath)
            => File.ReadAllText(Path.Combine(InstallDirectory, relativePath));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Geçici klasör silinemezse test sonucu etkilenmez.
            }
        }
    }
}
