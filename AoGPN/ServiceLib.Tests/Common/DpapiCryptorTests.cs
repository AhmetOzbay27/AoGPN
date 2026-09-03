using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

public class DpapiCryptorTests
{
    private const string SampleKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public void Encrypt_Then_Decrypt_RoundTrips_ExactValue()
    {
        var cipher = DpapiCryptor.Encrypt(SampleKey);
        cipher.Should().NotBeNullOrEmpty();
        cipher.Should().NotBe(SampleKey);

        DpapiCryptor.Decrypt(cipher).Should().Be(SampleKey);
    }

    [Fact]
    public void Encrypt_Twice_Is_Not_Deterministic()
    {
        var c1 = DpapiCryptor.Encrypt(SampleKey);
        var c2 = DpapiCryptor.Encrypt(SampleKey);
        c1.Should().NotBe(c2);
    }

    [Fact]
    public void Encrypt_EmptyInput_Returns_Empty()
    {
        DpapiCryptor.Encrypt(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Decrypt_EmptyOrGarbage_DoesNotThrow()
    {
        DpapiCryptor.Decrypt(string.Empty).Should().BeEmpty();
        DpapiCryptor.Decrypt("  not base64 !!!  ").Should().BeEmpty();
    }
}