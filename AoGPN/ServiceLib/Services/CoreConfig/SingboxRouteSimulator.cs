namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Synthetic connection used to evaluate sing-box route rules offline. Every field is
/// optional: only the fields the caller provides participate in matching, so a probe
/// that only knows the process name and a destination domain can still be evaluated
/// without DNS resolution or opening any socket.
/// </summary>
public sealed class RouteProbe
{
    /// <summary>Process name with extension, e.g. "chrome.exe".</summary>
    public string ProcessName { get; init; } = "";

    /// <summary>Full process path (optional; needed for process_path rules).</summary>
    public string ProcessPath { get; init; } = "";

    /// <summary>Destination domain (used when the destination is not an IP).</summary>
    public string Domain { get; init; } = "";

    /// <summary>Destination IP (used when the caller provided one; never resolved from a domain).</summary>
    public IPAddress? IpAddress { get; init; }

    /// <summary>Destination port, when known.</summary>
    public int? Port { get; init; }

    /// <summary>Network type, e.g. "tcp" or "udp".</summary>
    public string Network { get; init; } = "";

    /// <summary>Protocol, e.g. "http", "tls", "quic", "dns".</summary>
    public string Protocol { get; init; } = "";

    /// <summary>Inbound tag (e.g. "tun", "socks"). Empty means "unknown", which never matches inbound rules.</summary>
    public string Inbound { get; init; } = "";

    /// <summary>Clash mode; sing-box's default is "Rule".</summary>
    public string ClashMode { get; init; } = "Rule";
}

/// <summary>A rule that matched a probe, or a modifier (sniff/resolve/route-options) that applied.</summary>
public sealed class RouteTestMatch
{
    /// <summary>1-based index into route.rules.</summary>
    public int RuleIndex { get; init; }

    /// <summary>Human-readable summary of the rule (fields + outcome).</summary>
    public string Description { get; init; } = "";

    /// <summary>The match criteria that actually matched (e.g. "process_name: chrome.exe").</summary>
    public List<string> Criteria { get; init; } = [];

    /// <summary>Friendly outcome label, e.g. "Proxy", "Direct", "Block", "Reject".</summary>
    public string Outcome { get; init; } = "";

    /// <summary>"outbound" or "action".</summary>
    public string OutcomeKind { get; init; } = "";

    /// <summary>Raw outbound tag or action value from the rule.</summary>
    public string OutcomeRaw { get; init; } = "";
}

/// <summary>Result of a route test. Serialized to the dashboard with camelCase keys.</summary>
public sealed class RouteTestResult
{
    public bool Success { get; init; }

    /// <summary>User-facing error when the test could not run.</summary>
    public string? Error { get; init; }

    /// <summary>"gpn" or "global" — the routing path the config was generated for.</summary>
    public string ConfigMode { get; init; } = "";

    /// <summary>True when a terminal rule matched.</summary>
    public bool Matched { get; init; }

    /// <summary>The first matched rule that decides the route (outbound / reject / hijack-dns / ...).</summary>
    public RouteTestMatch? Match { get; init; }

    /// <summary>
    /// Rules that matched before the terminal one but do not terminate routing
    /// (sniff / resolve / route-options). They run, then evaluation continues.
    /// </summary>
    public List<RouteTestMatch> Modifiers { get; init; } = [];

    /// <summary>route.final — what happens when no rule matches.</summary>
    public string? FinalFallback { get; init; }

    /// <summary>Warnings, e.g. rule-set data not available locally.</summary>
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Pure, offline simulator for sing-box <c>route.rules</c>. Evaluates rules in order
/// (first match wins) against a <see cref="RouteProbe"/>, mirroring sing-box semantics:
/// fields inside a rule AND together, <c>type = logical</c> combines sub-rules with
/// <c>mode</c> (or/and), <c>invert</c> flips the result, and a rule with no match fields
/// matches everything. Rules whose action is a non-terminal modifier (sniff, resolve,
/// route-options) match and then let evaluation continue to the next rule.
///
/// No network I/O happens here: domain probes never resolve DNS, so IP-only rule items
/// are skipped for them. Rule-set membership is delegated to an injected matcher so
/// callers can back it with the bundled sing-box binary (or tests with a fake).
/// </summary>
public static class SingboxRouteSimulator
{
    /// <summary>Tri-state result of evaluating a rule-set: whether it matched, and whether the data was available.</summary>
    public readonly record struct RuleSetEvaluation(bool Matched, bool Available);

    /// <summary>Evaluates a rule-set tag for a probe. Returns whether the data was available.</summary>
    public delegate RuleSetEvaluation RuleSetMatcher(string ruleSetTag, RouteProbe probe);

    /// <summary>Route actions that match but do not terminate routing; evaluation continues.</summary>
    private static readonly HashSet<string> NonTerminalActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sniff",
        "resolve",
        "route-options",
    };

    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Walks <paramref name="rules"/> in order and returns the first terminal match plus
    /// any non-terminal modifiers that applied first. When nothing matches, the result
    /// reports <paramref name="finalFallback"/> (route.final) as the outcome.
    /// </summary>
    public static RouteTestResult FindFirstMatch(
        IReadOnlyList<Rule4Sbox> rules,
        RouteProbe probe,
        RuleSetMatcher? ruleSetMatcher = null,
        string? finalFallback = null,
        string? configMode = null,
        List<string>? warnings = null)
    {
        var modifiers = new List<RouteTestMatch>();
        var index = 0;
        foreach (var rule in rules)
        {
            index++;
            if (rule is null)
            {
                continue;
            }

            var criteria = new List<string>();
            if (!MatchRule(rule, probe, ruleSetMatcher, criteria, warnings))
            {
                continue;
            }

            var match = BuildMatch(rule, index, criteria);
            if (IsTerminal(rule))
            {
                return new RouteTestResult
                {
                    Success = true,
                    ConfigMode = configMode ?? "",
                    Matched = true,
                    Match = match,
                    Modifiers = modifiers,
                    FinalFallback = finalFallback,
                    Warnings = warnings ?? [],
                };
            }

            // sniff / resolve / route-options apply to the connection but do not
            // decide the route — keep evaluating like sing-box does.
            modifiers.Add(match);
        }

        return new RouteTestResult
        {
            Success = true,
            ConfigMode = configMode ?? "",
            Matched = false,
            Modifiers = modifiers,
            FinalFallback = finalFallback,
            Warnings = warnings ?? [],
        };
    }

    /// <summary>
    /// True when the rule matches the probe. Present field groups must all match (AND);
    /// <c>invert</c> flips the overall result. Matched criteria are appended to
    /// <paramref name="matchedCriteria"/>.
    /// </summary>
    public static bool MatchRule(
        Rule4Sbox rule,
        RouteProbe probe,
        RuleSetMatcher? ruleSetMatcher,
        List<string>? matchedCriteria = null,
        List<string>? warnings = null)
    {
        if (rule is null)
        {
            return false;
        }

        // Logical rule: combine sub-rules with mode (or/and), then apply invert.
        if (rule.type == "logical" && rule.rules is { Count: > 0 })
        {
            var subCriteria = new List<string>();
            var mode = rule.mode.IsNullOrEmpty() ? "or" : rule.mode;
            var results = new List<bool>();
            foreach (var sub in rule.rules)
            {
                results.Add(MatchRule(sub, probe, ruleSetMatcher, subCriteria, warnings));
            }

            var logicalMatched = mode.Equals("and", StringComparison.OrdinalIgnoreCase)
                ? results.All(x => x)
                : results.Any(x => x);

            var matched = rule.invert == true ? !logicalMatched : logicalMatched;
            if (matched)
            {
                matchedCriteria?.Add($"logical ({mode})");
                matchedCriteria?.AddRange(subCriteria);
            }
            return matched;
        }

        var checks = new List<(bool Ok, string? Criteria)>();

        if (rule.inbound is { Count: > 0 })
        {
            var ok = probe.Inbound.IsNotEmpty()
                && rule.inbound.Contains(probe.Inbound, StringComparer.Ordinal);
            checks.Add((ok, ok ? $"inbound: {probe.Inbound}" : null));
        }

        if (rule.protocol is { Count: > 0 })
        {
            var ok = probe.Protocol.IsNotEmpty()
                && rule.protocol.Contains(probe.Protocol, StringComparer.OrdinalIgnoreCase);
            checks.Add((ok, ok ? $"protocol: {probe.Protocol}" : null));
        }

        if (rule.network is { Count: > 0 })
        {
            var ok = probe.Network.IsNotEmpty()
                && rule.network.Contains(probe.Network, StringComparer.OrdinalIgnoreCase);
            checks.Add((ok, ok ? $"network: {probe.Network}" : null));
        }

        if (rule.port is { Count: > 0 })
        {
            var ok = probe.Port is int p && rule.port.Contains(p);
            checks.Add((ok, ok ? $"port: {probe.Port}" : null));
        }

        if (rule.port_range is { Count: > 0 })
        {
            // A probe without a port means "any port"; it matches only ranges that
            // span the entire port space (e.g. the 0-65535 catch-all).
            var ok = probe.Port is int pr
                ? rule.port_range.Any(r => PortRangeContains(r, pr))
                : rule.port_range.Any(IsFullPortRange);
            checks.Add((ok, ok ? $"port_range: [{string.Join(",", rule.port_range)}]" : null));
        }

        if (rule.ip_cidr is { Count: > 0 })
        {
            var ok = probe.IpAddress is { } ip && rule.ip_cidr.Any(c => IpInCidr(ip, c));
            checks.Add((ok, ok ? $"ip_cidr: {probe.IpAddress}" : null));
        }

        if (rule.ip_is_private == true)
        {
            var ok = probe.IpAddress is { } privateIp && Utils.IsPrivateNetwork(privateIp.ToString());
            checks.Add((ok, ok ? "ip_is_private" : null));
        }

        if (rule.domain is { Count: > 0 })
        {
            var ok = probe.Domain.IsNotEmpty()
                && rule.domain.Contains(probe.Domain, StringComparer.OrdinalIgnoreCase);
            checks.Add((ok, ok ? $"domain: {probe.Domain}" : null));
        }

        if (rule.domain_suffix is { Count: > 0 })
        {
            var ok = probe.Domain.IsNotEmpty()
                && rule.domain_suffix.Any(s => DomainSuffixMatch(probe.Domain, s));
            checks.Add((ok, ok ? $"domain_suffix: [{string.Join(",", rule.domain_suffix)}]" : null));
        }

        if (rule.domain_keyword is { Count: > 0 })
        {
            var ok = probe.Domain.IsNotEmpty()
                && rule.domain_keyword.Any(k => probe.Domain.Contains(k, StringComparison.OrdinalIgnoreCase));
            checks.Add((ok, ok ? $"domain_keyword: [{string.Join(",", rule.domain_keyword)}]" : null));
        }

        if (rule.domain_regex is { Count: > 0 })
        {
            var ok = probe.Domain.IsNotEmpty()
                && rule.domain_regex.Any(rx => DomainRegexMatch(probe.Domain, rx));
            checks.Add((ok, ok ? $"domain_regex: [{string.Join(",", rule.domain_regex)}]" : null));
        }

        if (rule.process_name is { Count: > 0 })
        {
            var ok = probe.ProcessName.IsNotEmpty()
                && rule.process_name.Contains(probe.ProcessName, StringComparer.OrdinalIgnoreCase);
            checks.Add((ok, ok ? $"process_name: {probe.ProcessName}" : null));
        }

        if (rule.process_path is { Count: > 0 })
        {
            var ok = probe.ProcessPath.IsNotEmpty()
                && rule.process_path.Contains(probe.ProcessPath, StringComparer.OrdinalIgnoreCase);
            checks.Add((ok, ok ? $"process_path: {probe.ProcessPath}" : null));
        }

        if (rule.rule_set is { Count: > 0 })
        {
            if (ruleSetMatcher is null)
            {
                checks.Add((false, null));
            }
            else
            {
                var anyMatched = false;
                var anyAvailable = false;
                foreach (var tag in rule.rule_set)
                {
                    var evaluation = ruleSetMatcher(tag, probe);
                    if (evaluation.Available)
                    {
                        anyAvailable = true;
                    }
                    if (evaluation.Matched)
                    {
                        anyMatched = true;
                        break;
                    }
                }
                checks.Add((anyMatched, anyMatched ? $"rule_set: [{string.Join(",", rule.rule_set)}]" : null));
                if (!anyAvailable)
                {
                    warnings?.Add($"Rule-set not available locally: [{string.Join(",", rule.rule_set)}] — geosite/geoip membership was not evaluated");
                }
            }
        }

        if (rule.clash_mode.IsNotEmpty())
        {
            var ok = probe.ClashMode.Equals(rule.clash_mode, StringComparison.OrdinalIgnoreCase);
            checks.Add((ok, ok ? $"clash_mode: {rule.clash_mode}" : null));
        }

        var allOk = checks.All(c => c.Ok);
        var matchedRule = rule.invert == true ? !allOk : allOk;
        if (matchedRule)
        {
            if (rule.invert == true)
            {
                matchedCriteria?.Add("inverted");
            }
            else
            {
                matchedCriteria?.AddRange(checks.Where(c => c.Criteria != null).Select(c => c.Criteria!));
            }
        }
        return matchedRule;
    }

    /// <summary>True when a matched rule decides the route (outbound or a terminal action).</summary>
    public static bool IsTerminal(Rule4Sbox rule)
    {
        if (rule is null)
        {
            return false;
        }
        if (rule.outbound.IsNotEmpty())
        {
            return true;
        }
        if (rule.action.IsNotEmpty())
        {
            return !NonTerminalActions.Contains(rule.action);
        }
        // A rule with neither — treated as terminal so it is reported instead of skipped.
        return true;
    }

    /// <summary>Builds a human-readable description of the rule, e.g. "#5 process_name:[chrome.exe] → proxy".</summary>
    public static string DescribeRule(Rule4Sbox rule, int? index = null)
    {
        if (rule is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (rule.type == "logical")
        {
            parts.Add($"logical({(rule.mode.IsNullOrEmpty() ? "or" : rule.mode)})");
        }
        AddList(parts, "inbound", rule.inbound);
        AddList(parts, "protocol", rule.protocol);
        AddList(parts, "network", rule.network);
        if (rule.port is { Count: > 0 })
        {
            parts.Add($"port:[{string.Join(",", rule.port)}]");
        }
        if (rule.port_range is { Count: > 0 })
        {
            parts.Add($"port_range:[{string.Join(",", rule.port_range)}]");
        }
        AddList(parts, "domain", rule.domain);
        AddList(parts, "domain_suffix", rule.domain_suffix);
        AddList(parts, "domain_keyword", rule.domain_keyword);
        AddList(parts, "domain_regex", rule.domain_regex);
        AddList(parts, "ip_cidr", rule.ip_cidr);
        if (rule.ip_is_private == true)
        {
            parts.Add("ip_is_private");
        }
        AddList(parts, "process_name", rule.process_name);
        AddList(parts, "process_path", rule.process_path);
        AddList(parts, "rule_set", rule.rule_set);
        if (rule.clash_mode.IsNotEmpty())
        {
            parts.Add($"clash_mode:{rule.clash_mode}");
        }
        if (rule.invert == true)
        {
            parts.Add("invert");
        }

        var (outcome, _, _) = DescribeOutcome(rule);
        var text = parts.Count == 0 ? "match-all" : string.Join(" ", parts);
        var prefix = index is { } i ? $"#{i} " : "";
        return $"{prefix}{text} → {outcome}".Trim();
    }

    private static RouteTestMatch BuildMatch(Rule4Sbox rule, int index, List<string> criteria)
    {
        var (outcome, kind, raw) = DescribeOutcome(rule);
        return new RouteTestMatch
        {
            RuleIndex = index,
            Description = DescribeRule(rule, index),
            Criteria = criteria,
            Outcome = outcome,
            OutcomeKind = kind,
            OutcomeRaw = raw,
        };
    }

    private static (string Outcome, string Kind, string Raw) DescribeOutcome(Rule4Sbox rule)
    {
        if (rule.outbound.IsNotEmpty())
        {
            return (FriendlyOutbound(rule.outbound), "outbound", rule.outbound);
        }
        if (rule.action.IsNotEmpty())
        {
            return (FriendlyAction(rule.action), "action", rule.action);
        }
        return ("Match", "match", "");
    }

    private static string FriendlyOutbound(string tag)
    {
        return tag switch
        {
            Global.ProxyTag => "Proxy",
            Global.DirectTag => "Direct",
            Global.BlockTag => "Block",
            _ => tag, // node-specific outbound tags keep their readable tag
        };
    }

    private static string FriendlyAction(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "reject" => "Reject (block)",
            "hijack-dns" => "DNS hijack",
            "sniff" => "Sniff",
            "resolve" => "Resolve",
            "route-options" => "Route options",
            "bypass" => "Bypass (direct)",
            _ => action,
        };
    }

    private static void AddList(List<string> parts, string name, List<string>? values)
    {
        if (values is { Count: > 0 })
        {
            parts.Add($"{name}:[{string.Join(",", values)}]");
        }
    }

    private static bool PortRangeContains(string range, int port)
    {
        var idx = range.IndexOf(':');
        if (idx <= 0)
        {
            return false;
        }
        return int.TryParse(range[..idx], out var from)
            && int.TryParse(range[(idx + 1)..], out var to)
            && port >= from && port <= to;
    }

    /// <summary>True when the range covers the entire valid port space (a catch-all).</summary>
    private static bool IsFullPortRange(string range)
    {
        var idx = range.IndexOf(':');
        if (idx <= 0)
        {
            return false;
        }
        return int.TryParse(range[..idx], out var from)
            && int.TryParse(range[(idx + 1)..], out var to)
            && from <= 1 && to >= 65535;
    }

    private static bool DomainSuffixMatch(string domain, string suffix)
    {
        if (suffix.StartsWith('.'))
        {
            suffix = suffix[1..];
        }
        if (domain.Equals(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return domain.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DomainRegexMatch(string domain, string pattern)
    {
        var regex = RegexCache.GetOrAdd(pattern,
            p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        try
        {
            return regex.IsMatch(domain);
        }
        catch
        {
            return false;
        }
    }

    private static bool IpInCidr(IPAddress ip, string cidr)
    {
        try
        {
            if (cidr.Contains('/'))
            {
                var idx = cidr.IndexOf('/');
                if (!IPAddress.TryParse(cidr[..idx], out var network)
                    || !int.TryParse(cidr[(idx + 1)..], out var prefix))
                {
                    return false;
                }
                return CidrContains(network, prefix, ip);
            }

            // A bare IP is a single-host network (sing-box accepts plain IPs in ip_cidr).
            if (!IPAddress.TryParse(cidr, out var single))
            {
                return false;
            }
            return CidrContains(single, single.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32, ip);
        }
        catch
        {
            return false;
        }
    }

    private static bool CidrContains(IPAddress network, int prefix, IPAddress ip)
    {
        if (network.AddressFamily != ip.AddressFamily)
        {
            return false;
        }
        var networkBytes = network.GetAddressBytes();
        var ipBytes = ip.GetAddressBytes();
        var maxBits = networkBytes.Length * 8;
        if (prefix < 0 || prefix > maxBits)
        {
            return false;
        }

        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != ipBytes[i])
            {
                return false;
            }
        }
        if (remainingBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((networkBytes[fullBytes] & mask) != (ipBytes[fullBytes] & mask))
            {
                return false;
            }
        }
        return true;
    }
}
