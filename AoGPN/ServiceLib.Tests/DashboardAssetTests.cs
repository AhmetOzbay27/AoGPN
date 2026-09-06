namespace ServiceLib.Tests;

public sealed class DashboardAssetTests
{
    [Fact]
    public void DashboardReferencesItsLocalStylesheetChain()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "vpn-gpn-dashboard.html"));

        Assert.True(
            html.Contains("Temalar/dashboard.css", StringComparison.Ordinal)
            || html.Contains("id=\"embedded-dashboard-css\"", StringComparison.Ordinal),
            "Dashboard must reference or embed its local stylesheet.");
        Assert.True(
            html.Contains("id=\"embedded-dashboard-css\"", StringComparison.Ordinal),
            "Dashboard must embed the stylesheet chain so it renders identically even where @import targets cannot be resolved.");
    }

    [Fact]
    public void DashboardRendersOfflineWithoutTailwindCdn()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "vpn-gpn-dashboard.html"));

        // The utility layer must be fully local: no Tailwind CDN script allowed.
        Assert.False(
            html.Contains("https://cdn.tailwindcss.com", StringComparison.Ordinal),
            "Dashboard must not depend on the Tailwind CDN.");

        // The generated utility CSS is embedded so the layout renders identically offline.
        Assert.True(
            html.Contains("id=\"embedded-tailwind-utils\"", StringComparison.Ordinal),
            "Dashboard must embed the generated Tailwind utility CSS for offline rendering.");

        // The local runtime ships alongside for dynamically-added classes.
        Assert.True(
            html.Contains("Temalar/tailwind.js", StringComparison.Ordinal),
            "Dashboard must reference the local Tailwind runtime for dynamically-added classes.");
    }

    [Fact]
    public void DashboardLocalStylesheetsExist()
    {
        var root = FindRepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "Temalar", "dashboard.css")));
        Assert.True(File.Exists(Path.Combine(root, "Temalar", "base.css")));
        Assert.True(File.Exists(Path.Combine(root, "Temalar", "components.css")));
        Assert.True(File.Exists(Path.Combine(root, "Temalar", "tailwind.js")));
    }

    [Fact]
    public void DashboardThemeChainIsComplete()
    {
        // Every theme file referenced by the manifest must exist so the theme
        // picker never offers a theme that has no styles.
        var root = FindRepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "Temalar", "dashboard.css"));
        // Line-anchored: the manifest header documents the pattern inside a
        // comment, which must not be treated as a real import.
        var imported = System.Text.RegularExpressions.Regex.Matches(manifest, @"(?m)^\s*@import url\(""([^""]+)""\)")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Contains("base.css", imported);
        Assert.Contains("components.css", imported);
        Assert.True(imported.Count(t => t.StartsWith("themes/", StringComparison.Ordinal)) >= 16,
            "The stylesheet manifest must import every shipped theme.");
        foreach (var rel in imported)
        {
            Assert.True(File.Exists(Path.Combine(root, "Temalar", rel)), $"Missing imported stylesheet: {rel}");
        }
    }

    [Fact]
    public void EmbeddedDashboardCssMatchesImportedChain()
    {
        // The inline <style id="embedded-dashboard-css"> copy is what renders the
        // dashboard when @import targets cannot be resolved (offline / preview).
        // It is regenerated from the Temalar/dashboard.css import chain by
        // Temalar/build-embedded-dashboard-css.js on every AoGPN build; the C#
        // side deliberately does NOT re-implement the concatenation so there is
        // exactly one source of truth for what the embedded copy must contain.
        // This test binds to that producer: it runs the generator in --check
        // mode and fails whenever the embedded copy would be rewritten, i.e.
        // whenever a stylesheet was edited without regenerating the HTML.
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "Temalar", "build-embedded-dashboard-css.js");
        Assert.True(File.Exists(script), "Embedded-CSS generator is missing: Temalar/build-embedded-dashboard-css.js");

        if (!TryRunNode("--version", out _, out _))
        {
            Assert.Skip("node.js not found on PATH; skipping embedded-CSS sync check.");
        }

        var (exitCode, output) = RunNode(script, "--check", root);
        Assert.True(
            exitCode == 0,
            $"Embedded dashboard CSS is out of sync with the import chain. Regenerate it by running " +
            $"\"node Temalar/build-embedded-dashboard-css.js\" (or rebuild AoGPN).\n{output}");
    }

    private static bool TryRunNode(string arguments, out int exitCode, out string output)
    {
        try
        {
            (exitCode, output) = RunNode(null, arguments, null);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            exitCode = -1;
            output = string.Empty;
            return false;
        }
    }

    private static (int ExitCode, string Output) RunNode(string? script, string arguments, string? workingDirectory)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "node";
        process.StartInfo.Arguments = script is null ? arguments : $"\"{script}\" {arguments}";
        process.StartInfo.WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.Start();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "vpn-gpn-dashboard.html")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Dashboard source root was not found.");
    }
}
