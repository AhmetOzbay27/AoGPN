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
        // If a theme stylesheet is edited but the embedded copy is not regenerated,
        // the two diverge — this test catches that drift for every imported file.
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "vpn-gpn-dashboard.html"));
        var manifest = File.ReadAllText(Path.Combine(root, "Temalar", "dashboard.css"));

        var start = html.IndexOf("<style id=\"embedded-dashboard-css\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "embedded-dashboard-css block is missing from the dashboard.");
        start = html.IndexOf('>', start) + 1;
        var end = html.IndexOf("</style>", start, StringComparison.Ordinal);
        Assert.True(end > start, "embedded-dashboard-css closing tag is missing.");
        var embedded = html.Substring(start, end - start);

        var imported = System.Text.RegularExpressions.Regex.Matches(manifest, @"(?m)^\s*@import url\(""([^""]+)""\)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(imported.Count >= 18, "Expected base.css + components.css + at least 16 themes in the manifest.");

        foreach (var rel in imported)
        {
            var fileText = File.ReadAllText(Path.Combine(root, "Temalar", rel));
            Assert.True(
                NormalizeNewlines(embedded).Contains(NormalizeNewlines(fileText), StringComparison.Ordinal),
                $"Embedded dashboard CSS is out of sync with {rel}. Regenerate the embedded copy after editing theme stylesheets.");
        }
    }

    private static string NormalizeNewlines(string s)
        => s.Replace("\r\n", "\n").Replace('\r', '\n');

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
