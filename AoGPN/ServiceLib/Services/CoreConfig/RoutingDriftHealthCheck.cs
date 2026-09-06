namespace ServiceLib.Services.CoreConfig;

using ServiceLib.Handler;
using ServiceLib.Handler.Builder;

public enum RuleDriftState
{
    /// <summary>The check does not apply (no active routing/node, or the core is not mihomo).</summary>
    NotApplicable,

    /// <summary>The freshly generated rules match the rules the running core loaded.</summary>
    InSync,

    /// <summary>
    /// The active RoutingItem in the database no longer matches the mihomo config the
    /// running core is using — stale-rule drift. Reconnecting/reloading would regenerate
    /// the config and close the gap.
    /// </summary>
    Drifted,

    /// <summary>The comparison could not be made (no live config, unreadable file, exception).</summary>
    Unknown,
}

/// <summary>
/// Result of comparing the active <see cref="RoutingItem"/> (database) against the
/// mihomo config the core actually loaded (on-disk config file). Serialized to the
/// dashboard with camelCase keys when pushed through the WebView2 bridge.
/// </summary>
public sealed class RuleDriftReport
{
    public RuleDriftState State { get; init; }
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>User-facing explanation when the check could not run.</summary>
    public string? Error { get; init; }

    /// <summary>Id of the active routing item in the database.</summary>
    public string? ActiveRoutingId { get; init; }

    /// <summary>Remarks of the active routing item in the database.</summary>
    public string? ActiveRoutingName { get; init; }

    /// <summary>Enabled non-DNS rules stored on the active RoutingItem.</summary>
    public int ActiveRuleCount { get; init; }

    /// <summary>Number of route rules the config would have if regenerated right now.</summary>
    public int ExpectedRuleCount { get; init; }

    /// <summary>Number of route rules in the config the running core loaded.</summary>
    public int LiveRuleCount { get; init; }

    /// <summary>Rules the fresh config contains that the live config does not (stale rules).</summary>
    public List<string> MissingRules { get; init; } = [];

    /// <summary>Rules the live config contains that the fresh config no longer has.</summary>
    public List<string> ExtraRules { get; init; } = [];

    /// <summary>True when the rule sets match but their order differs (first-match-wins changes).</summary>
    public bool Reordered { get; init; }

    /// <summary>Non-fatal observations.</summary>
    public List<string> Warnings { get; init; } = [];

    public bool IsDrifted => State == RuleDriftState.Drifted;
}

/// <summary>
/// Health check that detects stale-rule drift before it causes failures: it rebuilds the
/// exact mihomo config the active core would use right now (same context builder +
/// generator as a live connect, using the active <see cref="RoutingItem"/> from the
/// database) and compares its <c>rules</c> against the config file on disk that the
/// running core loaded. A mismatch means the tunnel is enforcing rules the user no longer
/// configured (e.g. rules edited in the routing settings without a reload), or vice versa.
///
/// No traffic is sent and no process is spawned; the comparison is pure config parsing.
/// </summary>
public sealed class RoutingDriftHealthCheck
{
    private static readonly string _tag = "RoutingDriftHealthCheck";

    private readonly Config _config;

    public RoutingDriftHealthCheck(Config config)
    {
        _config = config;
    }

    public async Task<RuleDriftReport> CheckAsync()
    {
        try
        {
            var routing = await ConfigHandler.GetDefaultRouting(_config);
            if (routing is null)
            {
                return Report(RuleDriftState.NotApplicable, error: "No active routing item.");
            }

            var node = await ConfigHandler.GetDefaultServer(_config);
            if (node is null)
            {
                return Report(RuleDriftState.NotApplicable, error: "No active node.");
            }

            var builderResult = await CoreConfigContextBuilder.Build(_config, node);
            if (!builderResult.Success)
            {
                return Report(RuleDriftState.Unknown,
                    error: "Cannot build the routing config: "
                        + string.Join("; ", builderResult.ValidatorResult.Errors));
            }

            if (builderResult.Context.RunCoreType != ECoreType.mihomo)
            {
                return Report(RuleDriftState.NotApplicable,
                    error: $"Rule-drift check is mihomo only (current core: {builderResult.Context.RunCoreType}).");
            }

            var generated = await CoreConfigHandler.GenerateClientConfig(builderResult.Context, null);
            if (!generated.Success || generated.Data is null)
            {
                return Report(RuleDriftState.Unknown, error: "Cannot generate the mihomo config: " + generated.Msg);
            }

            var expectedRules = RouteTesterService.ExtractRules(generated.Data.ToString());

            var livePath = Utils.GetBinConfigPath(Global.CoreConfigFileName);
            if (!File.Exists(livePath))
            {
                return Report(RuleDriftState.Unknown,
                    error: "No live mihomo config found on disk.",
                    routing: routing,
                    warnings: [$"No config file at {livePath} — the core may not have started, or it was never regenerated."],
                    expectedRules: expectedRules);
            }

            var liveText = await File.ReadAllTextAsync(livePath);
            var liveRules = RouteTesterService.ExtractRules(liveText);
            if (liveRules.Count == 0 && !liveText.Contains("rules:", StringComparison.OrdinalIgnoreCase))
            {
                return Report(RuleDriftState.Unknown,
                    error: "The live mihomo config could not be parsed.",
                    routing: routing,
                    warnings: [$"File at {livePath} is not a readable mihomo config."],
                    expectedRules: expectedRules);
            }

            return BuildReport(routing, expectedRules, liveRules);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return Report(RuleDriftState.Unknown, error: "Rule-drift check failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Pure comparison of the freshly generated rules against the rules the running core
    /// loaded. Exposed internally so unit tests can exercise the drift verdict without
    /// the database or the filesystem.
    /// </summary>
    internal static RuleDriftReport BuildReport(
        RoutingItem routing,
        IReadOnlyList<string> expectedRules,
        IReadOnlyList<string> liveRules,
        IReadOnlyList<string>? externalWarnings = null)
    {
        var missing = new List<string>();
        var liveSet = new HashSet<string>(liveRules, StringComparer.Ordinal);
        for (var i = 0; i < expectedRules.Count; i++)
        {
            if (!liveSet.Contains(expectedRules[i]))
            {
                missing.Add($"{i + 1}. {expectedRules[i]}");
            }
        }

        var extra = new List<string>();
        var expectedSet = new HashSet<string>(expectedRules, StringComparer.Ordinal);
        for (var i = 0; i < liveRules.Count; i++)
        {
            if (!expectedSet.Contains(liveRules[i]))
            {
                extra.Add($"{i + 1}. {liveRules[i]}");
            }
        }

        var reordered = missing.Count == 0
            && extra.Count == 0
            && !expectedRules.SequenceEqual(liveRules);

        var warnings = new List<string>(externalWarnings ?? []);
        var drifted = missing.Count > 0 || extra.Count > 0 || reordered;
        if (drifted)
        {
            Logging.SaveLog($"[{_tag}] DRIFT detected: routing='{routing.Remarks}' (id={routing.Id}) "
                + $"expected={expectedRules.Count} rules, live={liveRules.Count} rules, "
                + $"missing={missing.Count}, extra={extra.Count}, reordered={reordered}.");
            DiagLog.Write($"RULE_DRIFT routing={routing.Id} expected={expectedRules.Count} live={liveRules.Count} "
                + $"missing={missing.Count} extra={extra.Count} reordered={reordered}");
        }
        else
        {
            Logging.Verbose("GPN", "rule_drift_check",
                ("routing", routing.Id), ("expected", expectedRules.Count), ("live", liveRules.Count));
        }

        var dbRules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? [];
        var activeRuleCount = dbRules.Count(r => r.Enabled && r.RuleType != ERuleType.DNS);

        return new RuleDriftReport
        {
            State = drifted ? RuleDriftState.Drifted : RuleDriftState.InSync,
            CheckedAt = DateTimeOffset.UtcNow,
            ActiveRoutingId = routing.Id,
            ActiveRoutingName = routing.Remarks,
            ActiveRuleCount = activeRuleCount,
            ExpectedRuleCount = expectedRules.Count,
            LiveRuleCount = liveRules.Count,
            MissingRules = missing,
            ExtraRules = extra,
            Reordered = reordered,
            Warnings = warnings,
        };
    }

    private static RuleDriftReport Report(
        RuleDriftState state,
        string? error = null,
        RoutingItem? routing = null,
        List<string>? warnings = null,
        List<string>? expectedRules = null)
    {
        return new RuleDriftReport
        {
            State = state,
            CheckedAt = DateTimeOffset.UtcNow,
            Error = error,
            ActiveRoutingId = routing?.Id,
            ActiveRoutingName = routing?.Remarks,
            ExpectedRuleCount = expectedRules?.Count ?? 0,
            Warnings = warnings ?? [],
        };
    }
}