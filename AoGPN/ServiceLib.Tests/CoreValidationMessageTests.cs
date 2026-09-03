using AwesomeAssertions;

namespace ServiceLib.Tests;

public class CoreValidationMessageTests
{
    [Theory]
    // The exact sing-box 1.13 FATAL that killed the core at startup (with ANSI codes).
    [InlineData("\u001B[31mFATAL\u001B[0m[0000] initialize inbound[1]: legacy inbound fields are deprecated in sing-box 1.11.0 and removed in sing-box 1.13.0, checkout migration")]
    [InlineData("FATAL[0000] initialize inbound[0]: legacy inbound fields are deprecated in sing-box 1.11.0")]
    public void SchemaMismatch_ExplainsCoreVersionConflict(string raw)
    {
        var msg = CoreValidationMessage.ToUserMessage(raw);

        msg.Should().NotContain("FATAL");
        msg.Should().NotContain("sing-box 1.13.0");
        msg.Should().Contain("config format");
        msg.Should().Contain("Check for Updates");
    }

    [Fact]
    public void UnknownField_ExplainsUnrecognizedOption()
    {
        var raw = "FATAL[0000] decode config at config.json: route.rules[3].override_destination: json: unknown field \"override_destination\"";

        var msg = CoreValidationMessage.ToUserMessage(raw);

        msg.Should().Contain("does not recognize");
        msg.Should().Contain("Check for Updates");
    }

    [Fact]
    public void InvalidValueType_ExplainsMalformedOption()
    {
        var raw = "FATAL[0000] inbounds[0].sniff: json: cannot unmarshal object into Go struct field";

        var msg = CoreValidationMessage.ToUserMessage(raw);

        msg.Should().Contain("invalid value");
    }

    [Fact]
    public void MissingFile_ExplainsMissingAsset()
    {
        var raw = "FATAL[0000] initialize router: parse rule-set[0]: open Z:\\\\missing\\\\geosite-private.srs: The system cannot find the path specified.";

        var msg = CoreValidationMessage.ToUserMessage(raw);

        msg.Should().Contain("required file");
        msg.Should().Contain("Check for Updates");
    }

    [Fact]
    public void EmptyInput_FallsBackToGeneric()
    {
        CoreValidationMessage.ToUserMessage("").Should().Be(
            "The core rejected the generated configuration.");
    }

    [Fact]
    public void UnrecognizedError_FallsBackToGenericWithAction()
    {
        var msg = CoreValidationMessage.ToUserMessage("FATAL[0000] some unexpected parser error");

        msg.Should().Contain("rejected the generated configuration");
        msg.Should().Contain("Check for Updates");
    }

    [Fact]
    public void StripAnsi_RemovesColourEscapeSequences()
    {
        CoreValidationMessage.StripAnsi("\u001B[31mFATAL\u001B[0m[0000] boom")
            .Should().Be("FATAL[0000] boom");
    }
}
