using System.Text.RegularExpressions;

namespace ServiceLib.Services;

/// <summary>
/// Turns raw core validation output (e.g. <c>sing-box check -c</c>) into a concise,
/// user-facing message. The raw FATAL text is developer-oriented; this keeps the
/// notification readable while the original output stays available in the message
/// panel, ao_diag.txt and the startup diagnostics.
/// </summary>
public static class CoreValidationMessage
{
    private static readonly Regex AnsiEscapeRegex = new(
        "\u001B\\[[0-9;]*m",
        RegexOptions.Compiled);

    /// <summary>Removes ANSI colour escape sequences (e.g. sing-box FATAL colouring).</summary>
    public static string StripAnsi(string text) => AnsiEscapeRegex.Replace(text, string.Empty);

    public static string ToUserMessage(string rawMessage)
    {
        var text = StripAnsi(rawMessage).Trim();
        if (text.IsNullOrEmpty())
        {
            return "The core rejected the generated configuration.";
        }

        var lower = text.ToLowerInvariant();

        // sing-box 1.13 removed the legacy inbound sniff fields ("legacy inbound
        // fields are deprecated in sing-box 1.11.0 and removed in sing-box 1.13.0").
        if (lower.Contains("legacy inbound fields")
            || lower.Contains("removed in sing-box")
            || lower.Contains("deprecated in sing-box"))
        {
            return "The core rejected the generated configuration: it uses a config format this core version no longer supports. Please update the core via Check for Updates and try again.";
        }

        if (lower.Contains("unknown field"))
        {
            return "The core rejected the generated configuration: it contains an option this core version does not recognize. Please update the core via Check for Updates and try again.";
        }

        if (lower.Contains("cannot unmarshal") || lower.Contains("failed to decode"))
        {
            return "The core rejected the generated configuration: an option has an invalid value. Please update the core via Check for Updates or try a different server.";
        }

        if (lower.Contains("no such file")
            || lower.Contains("cannot find the path")
            || lower.Contains("cannot find the file")
            || lower.Contains("file not found"))
        {
            return "The core could not find a required file. Please reinstall the core via Check for Updates and try again.";
        }

        return "The core rejected the generated configuration. Please update the core via Check for Updates or try a different server.";
    }
}
