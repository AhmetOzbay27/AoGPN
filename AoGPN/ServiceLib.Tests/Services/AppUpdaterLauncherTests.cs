using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// AppUpdaterLauncher — yan güncelleyiciyi bulan ve başlatan TEK yol.
///
/// Sözleşme: yol çözümlemesi ile argüman biçimi burada birleşir; hiçbir çağrı
/// yolu kendi ProcessStartInfo'sunu kurmaz. Bu testler gerçek süreç başlatmaz —
/// başlatıcı dışarıdan verilir, böylece doğrulama deterministiktir.
/// </summary>
public sealed class AppUpdaterLauncherTests
{
    [Fact]
    public void UpdaterExeName_ComesFromTheSharedContract()
    {
        AppUpdaterLauncher.UpdaterExeName.Should().Be(AppUpdateUpdaterContract.UpdaterExeName);
        AppUpdaterLauncher.DefaultWaitSeconds.Should().Be(AppUpdateUpdaterContract.DefaultWaitSeconds);
    }

    [Fact]
    public void UpdaterExists_AgreesWithResolveUpdaterPath()
    {
        var exists = AppUpdaterLauncher.UpdaterExists(out var path);

        AppUpdaterLauncher.ResolveUpdaterPath().Should().Be(exists ? path : null);
        Path.IsPathRooted(path).Should().BeTrue();
        Path.GetFileName(path).Should().Be(Utils.GetExeName(AppUpdaterLauncher.UpdaterExeName));
    }

    [Fact]
    public void TryStart_RefusesToStartWithoutAnInstalledUpdater()
    {
        var started = false;

        var ok = AppUpdaterLauncher.TryStart(
            zipPath: "whatever.zip",
            waitProcessId: 1,
            updaterPath: null,
            targetDirectory: Path.GetTempPath(),
            restartExePath: "AoGPN.exe",
            startProcess: _ =>
            {
                started = true;
                return true;
            },
            out var error);

        ok.Should().BeFalse();
        started.Should().BeFalse();
        error.Should().Contain("bulunamadı");
    }

    [Fact]
    public void TryStart_RefusesToStartWhenTheArchiveIsMissing()
    {
        using var fixture = new TempFixture();
        var updater = fixture.CreateFile("fake-updater.exe");
        var started = false;

        var ok = AppUpdaterLauncher.TryStart(
            zipPath: Path.Combine(fixture.Directory, "missing.zip"),
            waitProcessId: 1,
            updaterPath: updater,
            targetDirectory: fixture.Directory,
            restartExePath: updater,
            startProcess: _ =>
            {
                started = true;
                return true;
            },
            out var error);

        ok.Should().BeFalse();
        started.Should().BeFalse();
        error.Should().Contain("İndirilen güncelleme paketi bulunamadı");
    }

    [Fact]
    public void TryStart_HandsOffToTheSidecarWithTheContractArguments()
    {
        using var fixture = new TempFixture();
        var updater = fixture.CreateFile("AoGPN.Updater.exe");
        var zip = fixture.CreateFile("AoGPN-update-v9.9.9.zip");
        var restart = fixture.CreateFile("AoGPN.exe");
        ProcessStartInfo? captured = null;

        var ok = AppUpdaterLauncher.TryStart(
            zipPath: zip,
            waitProcessId: 4242,
            updaterPath: updater,
            targetDirectory: fixture.Directory,
            restartExePath: restart,
            startProcess: info =>
            {
                captured = info;
                return true;
            },
            out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();

        captured.Should().NotBeNull();
        captured!.FileName.Should().Be(updater);
        captured.WorkingDirectory.Should().Be(fixture.Directory);
        // Kabuk kullanılmaz: yönetici belirteci korunur ve argümanlar bölünmez.
        captured.UseShellExecute.Should().BeFalse();

        captured.Arguments.Should().Contain($"--zip \"{zip}\"");
        captured.Arguments.Should().Contain($"--target \"{fixture.Directory}\"");
        captured.Arguments.Should().Contain($"--restart \"{restart}\"");
        captured.Arguments.Should().Contain("--wait-pid 4242");
        captured.Arguments.Should().Contain($"--wait-seconds {AppUpdaterLauncher.DefaultWaitSeconds}");
    }

    [Fact]
    public void TryStart_ReportsFailureWhenTheProcessCannotBeStarted()
    {
        using var fixture = new TempFixture();
        var updater = fixture.CreateFile("AoGPN.Updater.exe");
        var zip = fixture.CreateFile("update.zip");

        var ok = AppUpdaterLauncher.TryStart(
            zip, 1, updater, fixture.Directory, updater, _ => false, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("başlatılamadı");
    }

    [Fact]
    public void TryStart_SurfacesTheStartupExceptionMessage()
    {
        using var fixture = new TempFixture();
        var updater = fixture.CreateFile("AoGPN.Updater.exe");
        var zip = fixture.CreateFile("update.zip");

        var ok = AppUpdaterLauncher.TryStart(
            zip, 1, updater, fixture.Directory, updater,
            _ => throw new InvalidOperationException("boom"), out var error);

        ok.Should().BeFalse();
        error.Should().Be("boom");
    }

    private sealed class TempFixture : IDisposable
    {
        public TempFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"aogpn-launcher-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }

        public string CreateFile(string name)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, "test");
            return path;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, true);
            }
            catch
            {
                // Geçici klasör silinemezse test sonucu etkilenmez.
            }
        }
    }
}
