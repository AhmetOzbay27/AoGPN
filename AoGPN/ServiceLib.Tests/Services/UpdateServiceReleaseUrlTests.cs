using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Çekirdek güncelleme kontrolünün "latest release" adresi regresyonu: adres
/// <c>Path.Combine</c> ile kurulduğunda Windows'ta ayırıcı '\' olduğu için
/// "…/releases\latest" üretiliyor ve güncelleme kontrolü her seferinde
/// başarısız oluyordu (canlı gözlenen: "StatusCode error: …/releases\latest").
/// Testler platformdan bağımsızdır — dizenin kendisi doğrulanır.
/// </summary>
public sealed class UpdateServiceReleaseUrlTests
{
    [Fact]
    public void BuildLatestReleaseUrl_UsesForwardSlash()
    {
        var url = UpdateService.BuildLatestReleaseUrl("https://github.com/AhmetOzbay27/AoGPN/releases");

        url.Should().Be("https://github.com/AhmetOzbay27/AoGPN/releases/latest");
        url.Should().NotContain("\\", "URL ayırıcısı asla ters bölü olmamalı (Windows'ta Path.Combine tuzağı)");
    }

    [Fact]
    public void BuildLatestReleaseUrl_NormalizesTrailingSlash()
    {
        var url = UpdateService.BuildLatestReleaseUrl("https://github.com/MetaCubeX/mihomo/releases/");

        url.Should().Be("https://github.com/MetaCubeX/mihomo/releases/latest");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildLatestReleaseUrl_WithoutCoreUrl_ReturnsNull(string? coreUrl)
        // Çekirdek bilgisi yoksa istek kurulmaz (eskiden coreInfo.Url üzerinden NRE
        // fırlıyor ve jenerik bir hata mesajına dönüşüyordu).
        => UpdateService.BuildLatestReleaseUrl(coreUrl).Should().BeNull();
}
