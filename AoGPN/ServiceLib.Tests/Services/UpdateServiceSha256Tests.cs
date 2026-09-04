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

    [Fact]
    public void TryParseDgstSha256_OpenSslFormat_ReturnsHash()
    {
        var dgst = string.Join(
            Environment.NewLine,
            "MD5= 402f65a8cccdf123a6c0d5c176ef5252",
            $"SHA2-256= {Hash}",
            "SHA2-512= 5b1356f07a91cbd4fb538fd7eccc494967ea045e770fb60887a9bec0412f0b3bce6148a6514ce7c661824daefe83929b7ffa8b059c22abfc1f1299d9d228712c");

        UpdateService.TryParseDgstSha256(dgst, out var parsed).Should().BeTrue();
        parsed.Should().Be(Hash);
    }

    [Fact]
    public void TryParseDgstSha256_Sha256Alias_IsTolerated()
    {
        var dgst = $"SHA256= {Hash}";

        UpdateService.TryParseDgstSha256(dgst, out var parsed).Should().BeTrue();
        parsed.Should().Be(Hash);
    }

    [Fact]
    public void TryParseDgstSha256_UppercaseHash_IsNormalised()
    {
        var dgst = $"SHA2-256= {Hash.ToUpperInvariant()}";

        UpdateService.TryParseDgstSha256(dgst, out var parsed).Should().BeTrue();
        parsed.Should().Be(Hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseDgstSha256_EmptyInput_ReturnsFalse(string? dgst)
    {
        UpdateService.TryParseDgstSha256(dgst, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseDgstSha256_MissingSha256Line_ReturnsFalse()
    {
        var dgst = string.Join(
            Environment.NewLine,
            "MD5= 402f65a8cccdf123a6c0d5c176ef5252",
            "SHA1= 42e5e9a66b970b5499c34e99d8da6c986c67c2cc");

        UpdateService.TryParseDgstSha256(dgst, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseDgstSha256_WrongLengthHash_ReturnsFalse()
    {
        UpdateService.TryParseDgstSha256($"SHA2-256= {Hash[..^2]}", out _).Should().BeFalse();
    }
}
