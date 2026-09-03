namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Offline sing-box route tester. Rebuilds the exact sing-box config the active core
/// would use (same context builder + generator as a live connect), then simulates
/// first-match-wins routing for a synthetic connection. No sockets are opened and no
/// DNS is resolved. geosite/geoip rule-set membership is evaluated through the bundled
/// sing-box binary's offline <c>rule-set match</c> subcommand against local .srs files
/// when the data is available.
/// </summary>
public sealed class RouteTesterService
{
    private static readonly string _tag = "RouteTesterService";

    private readonly Config _config;
    private readonly Dictionary<string, SingboxRouteSimulator.RuleSetEvaluation> _ruleSetCache =
        new(StringComparer.Ordinal);

    public RouteTesterService(Config config)
    {
        _config = config;
    }

    /// <summary>
    /// Tests which sing-box route rule would match traffic from <paramref name="exeName"/>
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

            // The process name sing-box matches carries the ".exe" suffix on Windows.
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

            if (builderResult.Context.RunCoreType != ECoreType.sing_box)
            {
                return Error($"Route testing is sing-box only (current node core: {builderResult.Context.RunCoreType}).");
            }

            var generated = new CoreConfigSingboxService(builderResult.Context).GenerateClientConfigContent();
            if (!generated.Success || generated.Data is null)
            {
                return Error("Cannot generate the sing-box config: " + generated.Msg);
            }

            var singboxConfig = JsonUtils.Deserialize<SingboxConfig>(generated.Data.ToString());
            if (singboxConfig?.route?.rules is not { Count: > 0 } rules)
            {
                return Error("Generated sing-box config has no route rules.");
            }

            // Pre-evaluate every referenced rule-set (async, offline) so the pure
            // simulator can consume the results synchronously.
            var tags = new HashSet<string>(StringComparer.Ordinal);
            CollectRuleSetTags(rules, tags);
            foreach (var tag in tags)
            {
                _ruleSetCache[tag] = await EvaluateRuleSetAsync(tag, probe);
            }

            var warnings = new List<string>();
            return SingboxRouteSimulator.FindFirstMatch(
                rules,
                probe,
                ruleSetMatcher: (tag, p) => _ruleSetCache.TryGetValue(tag, out var cached) ? cached : new(false, false),
                finalFallback: singboxConfig.route.final,
                configMode: builderResult.Context.IsTunEnabled ? "gpn" : "global",
                warnings: warnings);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return Error("Route test failed: " + ex.Message);
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

    private static void CollectRuleSetTags(IEnumerable<Rule4Sbox> rules, HashSet<string> tags)
    {
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                continue;
            }
            if (rule.rule_set is { Count: > 0 })
            {
                foreach (var tag in rule.rule_set)
                {
                    if (tag.IsNotEmpty())
                    {
                        tags.Add(tag);
                    }
                }
            }
            if (rule.rules is { Count: > 0 })
            {
                CollectRuleSetTags(rule.rules, tags);
            }
        }
    }

    /// <summary>
    /// Runs <c>sing-box rule-set match &lt;srs&gt; &lt;domain-or-ip&gt;</c> for one rule-set
    /// tag. Reads a local .srs file only — no network. Returns Available=false when the
    /// file, the binary, or the subcommand is unavailable.
    /// </summary>
    private async Task<SingboxRouteSimulator.RuleSetEvaluation> EvaluateRuleSetAsync(string tag, RouteProbe probe)
    {
        var srsPath = Path.Combine(Utils.GetBinPath("srss"), $"{tag}.srs");
        if (!File.Exists(srsPath))
        {
            return new(false, false);
        }

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(ECoreType.sing_box);
        var singBoxExe = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out _);
        if (singBoxExe.IsNullOrEmpty())
        {
            return new(false, false);
        }

        var domainOrIp = probe.Domain.IsNotEmpty() ? probe.Domain : probe.IpAddress?.ToString();
        if (domainOrIp.IsNullOrEmpty())
        {
            return new(false, false);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = singBoxExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("rule-set");
            psi.ArgumentList.Add("match");
            psi.ArgumentList.Add(srsPath);
            psi.ArgumentList.Add(domainOrIp);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new(false, false);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new(false, false);
            }

            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0)
            {
                // Unreadable file or an older sing-box without "rule-set match".
                return new(false, false);
            }

            var matched = stdout.Contains("match rules.", StringComparison.OrdinalIgnoreCase);
            Logging.Verbose("GPN", "rule_set_match",
                ("tag", tag), ("target", domainOrIp), ("matched", matched), ("exit", process.ExitCode));
            return new(matched, true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return new(false, false);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
