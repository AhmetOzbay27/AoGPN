namespace ServiceLib.Services.CoreConfig;

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

/// <summary>
/// Saf (ağ yok, DNS yok) mihomo kural simülatörü: üretilen mihomo YAML'inden
/// alınan kural satırlarını ("PROCESS-NAME,x,DIRECT" gibi) ilk-eşleşme-kazanır
/// semantiğiyle sırayla işler. Sing-box kaldırıldığından geosite/geoip kural-seti
/// değerlendirmesi yoktur — mihomo üreticisi (GpnMihomoConfigService) bu tipleri
/// zaten üretmez (geosite:/geoip: öneklerini atlar).
/// </summary>
public sealed class RouteProbe
{
    public string ProcessName { get; init; } = "";
    public string ProcessPath { get; init; } = "";
    public string Domain { get; init; } = "";
    public IPAddress? IpAddress { get; init; }
    public int? Port { get; init; }
    public string Network { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Inbound { get; init; } = "";
    public string ClashMode { get; init; } = "Rule";
}

public sealed class RouteTestMatch
{
    public int RuleIndex { get; init; }
    public string Description { get; init; } = "";
    public List<string> Criteria { get; init; } = [];
    public string Outcome { get; init; } = "";
    public string OutcomeKind { get; init; } = "";
    public string OutcomeRaw { get; init; } = "";
}

public sealed class RouteTestResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string ConfigMode { get; init; } = "";
    public bool Matched { get; init; }
    public RouteTestMatch? Match { get; init; }
    public List<RouteTestMatch> Modifiers { get; init; } = [];
    public string? FinalFallback { get; init; }
    public List<string> Warnings { get; init; } = [];
}

/// <summary>Mihomo kural satırı: tip + yük(ler) + hedef.</summary>
public readonly record struct MihomoRuleLine(string Type, string Payload, string Target, string Raw)
{
    public static MihomoRuleLine Parse(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 2)
        {
            return new MihomoRuleLine(parts[0], "", "", line);
        }
        var type = parts[0].Trim();
        if (type.Equals("IP-CIDR", StringComparison.OrdinalIgnoreCase)
            || type.Equals("IP-CIDR6", StringComparison.OrdinalIgnoreCase))
        {
            // IP-CIDR,<cidr>,<target>[,no-resolve]
            return new MihomoRuleLine(type, parts[1].Trim(), parts.Length > 2 ? parts[2].Trim() : "", line);
        }
        // PROCESS-NAME,payload,target — payload birden çok virgül içerebilir (DST-PORT,80,443,target)
        var target = parts[^1].Trim();
        var payload = string.Join(",", parts[1..^1]).Trim();
        return new MihomoRuleLine(type, payload, target, line);
    }
}

public static class MihomoRouteSimulator
{
    /// <summary>
    /// İlk eşleşen kuralı bulur. <paramref name="rules"/> üretilen mihomo YAML'inin
    /// rules bloğudur (sıra anlamlıdır — önce özel, sonra MATCH). Eşleşme yoksa
    /// (yapısal olarak imkânsız — MATCH her zaman vardır) null döner.
    /// </summary>
    public static RouteTestMatch? FindFirstMatch(IReadOnlyList<string> rules, RouteProbe probe)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = MihomoRuleLine.Parse(rules[i]);
            if (!Matches(rule, probe))
            {
                continue;
            }
            return new RouteTestMatch
            {
                RuleIndex = i + 1,
                Description = rules[i],
                Criteria = [rule.Type, rule.Payload],
                Outcome = rule.Target,
                OutcomeKind = "target",
                OutcomeRaw = rule.Target,
            };
        }
        return null;
    }

    public static bool Matches(MihomoRuleLine rule, RouteProbe probe)
    {
        switch (rule.Type.ToUpperInvariant())
        {
            case "MATCH":
                return true;

            case "PROCESS-NAME":
                return probe.ProcessName.IsNotEmpty()
                    && rule.Payload.IsNotEmpty()
                    && string.Equals(
                        NormalizeProcess(rule.Payload),
                        NormalizeProcess(probe.ProcessName),
                        StringComparison.OrdinalIgnoreCase);

            case "PROCESS-PATH":
                return probe.ProcessPath.IsNotEmpty()
                    && rule.Payload.IsNotEmpty()
                    && probe.ProcessPath.Contains(rule.Payload, StringComparison.OrdinalIgnoreCase);

            case "DOMAIN":
                return probe.Domain.IsNotEmpty()
                    && string.Equals(probe.Domain, rule.Payload, StringComparison.OrdinalIgnoreCase);

            case "DOMAIN-SUFFIX":
                return probe.Domain.IsNotEmpty()
                    && rule.Payload.IsNotEmpty()
                    && DomainSuffixMatch(probe.Domain, rule.Payload);

            case "DOMAIN-KEYWORD":
                return probe.Domain.IsNotEmpty()
                    && rule.Payload.IsNotEmpty()
                    && probe.Domain.Contains(rule.Payload, StringComparison.OrdinalIgnoreCase);

            case "DOMAIN-REGEX":
                return probe.Domain.IsNotEmpty()
                    && rule.Payload.IsNotEmpty()
                    && Regex.IsMatch(probe.Domain, rule.Payload, RegexOptions.IgnoreCase);

            case "IP-CIDR":
            case "IP-CIDR6":
                if (probe.IpAddress is null || !TryParseCidr(rule.Payload, out var network, out var prefix))
                {
                    return false;
                }
                if (rule.Type.Equals("IP-CIDR6", StringComparison.OrdinalIgnoreCase)
                    && probe.IpAddress.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    return false;
                }
                if (rule.Type.Equals("IP-CIDR", StringComparison.OrdinalIgnoreCase)
                    && probe.IpAddress.AddressFamily != AddressFamily.InterNetwork)
                {
                    return false;
                }
                return IpInRange(probe.IpAddress, network, prefix);

            case "DST-PORT":
                return probe.Port is { } port && PortMatches(rule.Payload, port);

            default:
                // GEOSITE/GEOIP/... mihomo üreticisi tarafından üretilmez — eşleşme yok.
                return false;
        }
    }

    /// <summary>".exe" son ekini normalleştirir (PROCESS-NAME karşılaştırması).</summary>
    internal static string NormalizeProcess(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + ".exe";
    }

    internal static bool DomainSuffixMatch(string domain, string suffix)
    {
        var d = domain.TrimEnd('.');
        var s = suffix.TrimStart('.').TrimEnd('.');
        if (s.IsNullOrEmpty())
        {
            return false;
        }
        return string.Equals(d, s, StringComparison.OrdinalIgnoreCase)
            || d.EndsWith("." + s, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseCidr(string cidr, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var raw = cidr.Trim();
        var slash = raw.IndexOf('/');
        if (slash < 0)
        {
            return false;
        }
        if (!IPAddress.TryParse(raw[..slash], out var ip) || !int.TryParse(raw[(slash + 1)..], out prefix))
        {
            return false;
        }
        network = ip;
        return true;
    }

    internal static bool IpInRange(IPAddress ip, IPAddress network, int prefix)
    {
        var ipBytes = ip.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (ipBytes.Length != netBytes.Length)
        {
            return false;
        }
        var totalBits = ipBytes.Length * 8;
        var clamped = Math.Clamp(prefix, 0, totalBits);
        var fullBytes = clamped / 8;
        var remainingBits = clamped % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (ipBytes[i] != netBytes[i])
            {
                return false;
            }
        }
        if (remainingBits > 0 && fullBytes < ipBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((ipBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool PortMatches(string spec, int port)
    {
        foreach (var part in spec.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.IsNullOrEmpty())
            {
                continue;
            }
            var dash = trimmed.IndexOf('-');
            if (dash > 0
                && int.TryParse(trimmed[..dash], out var lo)
                && int.TryParse(trimmed[(dash + 1)..], out var hi))
            {
                if (port >= lo && port <= hi)
                {
                    return true;
                }
            }
            else if (int.TryParse(trimmed, out var single) && port == single)
            {
                return true;
            }
        }
        return false;
    }
}