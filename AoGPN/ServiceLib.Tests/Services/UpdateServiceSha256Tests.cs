using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class UpdateServiceSha256Tests
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void TryParseSha256Checksum_GitHubFormat_ReturnsHash()
    {
        var checksum = $"{Hash}  Xray-windows-64.zip";

        UpdateService.TryParseSha256Checksum(checksum, out var parsed).Should().BeTrue();
        parsed.Should().Be(Hash);
    }

    [Fact]
    public void TryParseSha256Checksum_MultipleEntries_ReturnsFirstHash()
    {
        var checksum = string.Join(
            Environment.NewLine,
            $"{Hash}  sing-box-1.11.0-windows-amd64.zip",
            "0000000000000000000000000000000000000000000000000000000000000000  other.zip");

        UpdateService.TryParseSha256Checksum(checksum, out var parsed).Should().BeTrue();
        parsed.Should().Be(Hash);
    }

    [Fact]
    public void TryParseSha256Checksum_LeadingStarPrefix_IsTolerated()
    {
        var checksum = $"{Hash}  *Xray-windows-64.zip";

        UpdateService.TryParseSha256Checksum(checksum, out var parsed).Should().BeTrue();
        parsed.Should().Be(Hash);
    }

    [Fact]
    public void TryParseSha256Checksum_CrLfLineEndings_IsTolerated()
    {
        var checksum = $"{Hash}  Xray-windows-64.zip\r\n";

        UpdateService.TryParseSha256Checksum(checksum, out var parsed).Should().BeTrue();
        parsed.Should().Be(Hash);
    }

    [Fact]
    public void TryParseSha256Checksum_UppercaseHash_IsNormalised()
    {
        var checksum = $"{Hash.ToUpperInvariant()}  Xray-windows-64.zip";

        UpdateService.TryParseSha256Checksum(checksum, out var parsed).Should().BeTrue();
        parsed.Should().Be(Hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseSha256Checksum_EmptyInput_ReturnsFalse(string? checksum)
    {
        UpdateService.TryParseSha256Checksum(checksum, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseSha256Checksum_NoFilename_ReturnsFalse()
    {
        UpdateService.TryParseSha256Checksum(Hash, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseSha256Checksum_WrongLengthHash_ReturnsFalse()
    {
        UpdateService.TryParseSha256Checksum($"{Hash[..^2]}  file.zip", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseSha256Checksum_Garbage_ReturnsFalse()
    {
        UpdateService.TryParseSha256Checksum("not a checksum file at all", out _).Should().BeFalse();
    }
}
