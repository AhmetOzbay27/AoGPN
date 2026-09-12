using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// AppUpdateUpdaterContract — ana uygulama ile yan güncelleyici
/// (<c>AoGPN.Updater.exe</c>) arasındaki komut satırı sözleşmesini sabitler.
///
/// Güncelleyici ana derlemeye bağlı olmadığı için iki taraf ayrı yerde yaşar;
/// bu testler biçimi kilitler. Güncelleyicideki çözümleyici
/// (<c>UpdaterOptions.Parse</c>) tam olarak bu bayrakları okur.
/// </summary>
public sealed class AppUpdateUpdaterContractTests
{
    [Fact]
    public void BuildArguments_EmitsEveryFlagTheUpdaterParses()
    {
        var arguments = AppUpdateUpdaterContract.BuildArguments(
            @"C:\Temp\AoGPN-update-v1.2.3.zip",
            @"C:\Program Files\AoGPN",
            @"C:\Program Files\AoGPN\AoGPN.exe",
            waitProcessId: 4242);

        arguments.Should().Contain("--zip \"C:\\Temp\\AoGPN-update-v1.2.3.zip\"");
        arguments.Should().Contain("--target \"C:\\Program Files\\AoGPN\"");
        arguments.Should().Contain("--restart \"C:\\Program Files\\AoGPN\\AoGPN.exe\"");
        arguments.Should().Contain("--wait-pid 4242");
        arguments.Should().Contain("--wait-seconds 30");
    }

    [Fact]
    public void BuildArguments_QuotesPathsWithSpaces()
    {
        // Boşluk içeren yol tırnaklanmazsa güncelleyici argümanı böler ve
        // güncelleme yanlış klasöre açılır.
        var arguments = AppUpdateUpdaterContract.BuildArguments(
            @"C:\Users\Ad Soyad\AppData\Local\Temp\a.zip", @"C:\App Dir", @"C:\App Dir\AoGPN.exe", 1);

        arguments.Should().Contain("--zip \"C:\\Users\\Ad Soyad\\AppData\\Local\\Temp\\a.zip\"");
        arguments.Should().Contain("--target \"C:\\App Dir\"");
        arguments.Should().Contain("--restart \"C:\\App Dir\\AoGPN.exe\"");
    }

    [Fact]
    public void BuildArguments_HonoursACustomWaitBudget()
    {
        var arguments = AppUpdateUpdaterContract.BuildArguments("a.zip", "t", "e.exe", 7, waitSeconds: 90);

        arguments.Should().Contain("--wait-pid 7");
        arguments.Should().Contain("--wait-seconds 90");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArguments_RejectsAnEmptyArchivePath(string? zipPath)
    {
        var act = () => AppUpdateUpdaterContract.BuildArguments(zipPath!, "t", "e.exe", 1);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Defaults_AreSharedBetweenTheAppAndTheUpdater()
    {
        AppUpdateUpdaterContract.UpdaterExeName.Should().Be("AoGPN.Updater");
        AppUpdateUpdaterContract.DefaultWaitSeconds.Should().Be(30);
    }
}
