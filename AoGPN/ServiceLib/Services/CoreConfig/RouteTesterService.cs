namespace ServiceLib.Services.CoreConfig;

using ServiceLib.Handler;
using ServiceLib.Handler.Builder;

/// <summary>
/// Offline mihomo route tester. Rebuilds the exact mihomo YAML the active core
/// would use (same context builder + generator as a live connect — GPN WireGuard
/// veya Global VPN), then simulates first-match-wins routing for a synthetic
/// connection. No sockets are opened and no DNS is resolved; geosite/geoip
/// rule-set evaluation is unnecessary because the mihomo generator never emits
/// those rule types (it skips geosite:/geoip: prefixes).
/// </summary>
public sealed class RouteTesterService
{
    private static readonly string _tag = "RouteTesterService";

    private readonly Config _config;

    public RouteTesterService(Config config)
    {
        _config = config;
    }

    /// <summary>
    /// Tests which mihomo route rule would match traffic from <paramref name="exeName"/>
    /// to <paramref name="destination"/> (domain or IP, optional <c>:port</c>).
    /// </summary>
    public async Task<RouteTestResult> TestAsync(
        string? exeName,
        string? destination,
        string? port = null,
        string? network = null,
        string? exePath = null)
    {
        try
        {
            var exe = (exeName ?? string.Empty).Trim();
            var dest = (destination ?? string.Empty).Trim();
            if (exe.IsNullOrEmpty())
            {
                return Error("Enter an executable name, e.g. chrome.exe.");
            }
            if (dest.IsNullOrEmpty())
            {
                return Error("Enter a destination domain or IP, e.g. discord.gg or 1.2.3.4:443.");
            }

            // The process name mihomo matches carries the ".exe" suffix on Windows.
            var processName = Utils.GetExeName(Path.GetFileName(exe));
            var processPath = (exePath ?? string.Empty).Trim();

            if (!ManualRouteParser.TrySplitPort(dest, out var host, out var destPort) || host.IsNullOrEmpty())
            {
                return Error("Invalid destination.");
            }
            var entryType = ManualRouteParser.ClassifyEntryType(host);
            if (!ManualRouteParser.IsValidHost(host, entryType))
            {
                return Error("Invalid destination. Use a domain (discord.gg), an IP (1.2.3.4) or host:port (1.2.3.4:443).");
            }
            if (host.Contains('/'))
            {
                return Error("A destination must be a single host, not a CIDR block.");
            }

            IPAddress? ip = null;
            var domain = string.Empty;
            if (IPAddress.TryParse(host, out var parsedIp))
            {
                ip = parsedIp;
            }
            else
            {
                domain = host;
            }

            var probePort = ParsePort(port) ?? ParsePort(destPort);
            var probeNetwork = (network ?? string.Empty).Trim().ToLowerInvariant();
            if (probeNetwork is not ("" or "tcp" or "udp"))
            {
                probeNetwork = string.Empty;
            }

            var probe = new RouteProbe
            {
                ProcessName = processName,
                ProcessPath = processPath,
                Domain = domain,
                IpAddress = ip,
                Port = probePort,
                Network = probeNetwork,
            };

            var node = await ConfigHandler.GetDefaultServer(_config);
            if (node is null)
            {
                return Error("No active node. Add and select a profile before testing routes.");
            }

            var builderResult = await CoreConfigContextBuilder.Build(_config, node);
            if (!builderResult.Success)
            {
                return Error("Cannot build the active routing config: "
                    + string.Join("; ", builderResult.ValidatorResult.Errors));
            }

            if (builderResult.Context.RunCoreType != ECoreType.mihomo)
            {
                return Error($"Route testing is mihomo only (current node core: {builderResult.Context.RunCoreType}).");
            }

            var generated = await CoreConfigHandler.GenerateClientConfig(builderResult.Context, null);
            if (!generated.Success || generated.Data is null)
            {
                return Error("Cannot generate the mihomo config: " + generated.Msg);
            }

            var rules = ExtractRules(generated.Data.ToString());
            if (rules.Count == 0)
            {
                return Error("Generated mihomo config has no route rules.");
            }

            var match = MihomoRouteSimulator.FindFirstMatch(rules, probe);
            var finalFallback = match is null ? null : match.Outcome;
            return new RouteTestResult
            {
                Success = true,
                ConfigMode = builderResult.Context.IsTunEnabled ? "gpn" : "global",
                Matched = match is not null,
                Match = match,
                FinalFallback = finalFallback,
                Warnings = [],
            };
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return Error("Route test failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Üretilen YAML'den rules bloğunu çıkarır (YamlDotNet dictionary). Sıra
    /// anlamlıdır ve olduğu gibi korunur.
    /// </summary>
    internal static List<string> ExtractRules(string yaml)
    {
        try
        {
            var doc = YamlUtils.FromYaml<Dictionary<string, object?>>(yaml);
            if (doc is null || !doc.TryGetValue("rules", out var rulesObj) || rulesObj is not IEnumerable<object> list)
            {
                return [];
            }
            return list.OfType<string>().Where(r => r.IsNotEmpty()).ToList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RouteTesterService", ex);
            return [];
        }
    }

    private static RouteTestResult Error(string message)
    {
        return new RouteTestResult
        {
            Success = false,
            Error = message,
        };
    }

    private static int? ParsePort(string? value)
    {
        if (value.IsNullOrEmpty() || !int.TryParse(value.Trim(), out var port))
        {
            return null;
        }
        return port is >= 1 and <= 65535 ? port : null;
    }
}