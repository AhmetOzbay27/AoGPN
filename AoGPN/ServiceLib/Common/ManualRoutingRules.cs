namespace ServiceLib.Common;

/// <summary>
/// Pure rule-generation helpers for the manual connection list. Kept free of UI, config
/// and core dependencies so the rule output can be unit-tested for every mode and entry
/// type (app / domain / IP, optional port).
/// </summary>
public static class ManualRoutingRules
{
    /// <summary>Prefix marking rules that the app manages (rewritten on every apply).</summary>
    public const string ManagedRemarksPrefix = "AoGPN Manuel";

    /// <summary>Maps a manual-list connection option to its routing outbound tag.</summary>
    public static string MapActionToOutbound(string action)
    {
        return action switch
        {
            "direct" => Global.DirectTag,
            "block" => Global.BlockTag,
            "warp" => Global.WarpTag, // WARP egress — WireGuard tüneli üzerinden sunucudaki WARP SOCKS5
            _ => Global.ProxyTag, // vpn / vpn+proxy / proxy / unknown
        };
    }

    /// <summary>
    /// Builds the managed routing rules for the active mode: one rule per manual-list
    /// entry in Manuel mode, plus the catch-all rule (everything through the proxy in
    /// VPN mode; everything direct otherwise).
    /// </summary>
    /// <param name="invertManual">
    /// GPN blacklist direction: when true, the listed entries are the exceptions
    /// (tunnel options become direct, direct becomes tunnel) and the catch-all
    /// tunnels everything else. When false (whitelist) only listed entries are
    /// tunneled and everything else stays direct.
    /// </param>
    public static List<RulesItem> BuildManagedRules(
        int mode,
        IEnumerable<SplitTunnelAppItem> apps,
        bool invertManual = false)
    {
        var rules = new List<RulesItem>();

        if (mode == GameTriggerModes.Manual)
        {
            foreach (var app in apps)
            {
                if (app.Value.IsNullOrEmpty())
                {
                    continue;
                }
                rules.Add(BuildEntryRule(app, invertManual));
            }
        }

        // Yakalayıcı hedefi tek otoriteden gelir (GpnRoutingRuleService.ResolveCatchAll
        // — mod + yön → nötr varış); buradan yalnızca söz varlığına (OutboundTag)
        // çevrilir. Karar mantığı çekirdekler arasında çoğaltılmaz.
        rules.Add(BuildCatchAllRule(DestinationTag(GpnRoutingRuleService.ResolveCatchAll(mode, invertManual))));
        return rules;
    }

    /// <summary>Rule routing a single manual-list entry (app / domain / IP, optional port).</summary>
    public static RulesItem BuildEntryRule(SplitTunnelAppItem app, bool invertManual = false)
    {
        return BuildEntryRule(app.EntryType, app.Value, app.Port, app.Action, invertManual);
    }

    /// <summary>
    /// Alan bazlı eşdeğeri — görünüm modelinden bağımsız saf kurallar üretmek isteyen
    /// tüketiciler (örn. mihomo superset satırları) bu imzayı kullanır. Davranış
    /// <see cref="BuildEntryRule(SplitTunnelAppItem, bool)"/> ile birebir aynıdır.
    /// </summary>
    public static RulesItem BuildEntryRule(
        string entryType,
        string value,
        string? port,
        string action,
        bool invertManual = false)
    {
        return new RulesItem
        {
            Id = Utils.GetGuid(false),
            // Eylem + yön → nötr varış yeri tek otoritede çözülür (kara liste
            // çevirisi dahil); burada yalnızca OutboundTag söz varlığına çevrilir.
            OutboundTag = DestinationTag(GpnRoutingRuleService.ResolveDestination(action, invertManual)),
            Network = "tcp,udp",
            Enabled = true,
            Remarks = $"{ManagedRemarksPrefix} {value}",
            Process = entryType == "app" ? [value] : null,
            Domain = entryType == "domain" ? [value] : null,
            Ip = entryType == "ip" ? [value] : null,
            Port = port.IsNotEmpty() ? port : null,
        };
    }

    /// <summary>
    /// Nötr varış yerini sing-box/legacy OutboundTag etiketine çevirir (tek yönlü
    /// söz varlığı eşlemesi — karar <see cref="GpnRoutingRuleService"/>'dadır).
    /// </summary>
    private static string DestinationTag(GpnRoutingDestination destination) => destination switch
    {
        GpnRoutingDestination.Direct => Global.DirectTag,
        GpnRoutingDestination.Block => Global.BlockTag,
        GpnRoutingDestination.WarpEgress => Global.WarpTag,
        _ => Global.ProxyTag, // Tunnel → proxy/vpn çıkışı
    };

    /// <summary>Catch-all rule: every unlisted destination follows <paramref name="outboundTag"/>.</summary>
    public static RulesItem BuildCatchAllRule(string outboundTag)
    {
        return new RulesItem
        {
            Id = Utils.GetGuid(false),
            Port = "0-65535",
            OutboundTag = outboundTag,
            Enabled = true,
            Remarks = $"{ManagedRemarksPrefix} varsayılan",
        };
    }

    /// <summary>True when the rule is managed by the app and safe to rewrite/drop.</summary>
    public static bool IsManagedRule(RulesItem rule)
    {
        if (rule.Remarks?.StartsWith(ManagedRemarksPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }
        if (IsCatchAllRule(rule))
        {
            return true;
        }
        return rule.Port == "443" && rule.Network == "udp" && rule.OutboundTag == Global.BlockTag;
    }

    /// <summary>
    /// Returns a routing item whose RuleSet has every app-managed rule (per-entry
    /// rules, mode catch-alls and the QUIC block) removed while user-defined rules
    /// are preserved. Returns the same instance when there is nothing to strip.
    ///
    /// Used when the core must run without the TUN inbound (proxy-only mode, and
    /// the TUN-missing fallback in CoreManager): process_name rules cannot match
    /// through the SOCKS/proxy inbound (no PID attribution), so keeping the managed
    /// rules would silently misroute traffic — a GPN blacklist's excluded apps would
    /// still be tunneled by the proxy catch-all, and a whitelist's direct catch-all
    /// would leak every unlisted connection. Stripping them makes the fallback an
    /// honest Global VPN where route.final = proxy governs.
    /// </summary>
    public static RoutingItem? StripManagedRules(RoutingItem? routing)
    {
        if (routing is null || routing.RuleSet.IsNullOrEmpty())
        {
            return routing;
        }
        var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? [];
        var preserved = rules.Where(r => !IsManagedRule(r)).ToList();
        if (preserved.Count == rules.Count)
        {
            return routing;
        }
        return new RoutingItem
        {
            Id = routing.Id,
            Remarks = routing.Remarks,
            Url = routing.Url,
            RuleSet = JsonUtils.Serialize(preserved),
            RuleNum = preserved.Count,
            Enabled = routing.Enabled,
            Locked = routing.Locked,
            CustomIcon = routing.CustomIcon,
            CustomRulesetPath4Singbox = routing.CustomRulesetPath4Singbox,
            DomainStrategy = routing.DomainStrategy,
            DomainStrategy4Singbox = routing.DomainStrategy4Singbox,
            Sort = routing.Sort,
            IsActive = routing.IsActive,
        };
    }

    private static bool IsCatchAllRule(RulesItem rule)
    {
        return rule.Port == "0-65535"
            && (rule.Process is null || rule.Process.Count == 0)
            && (rule.Domain is null || rule.Domain.Count == 0)
            && (rule.Ip is null || rule.Ip.Count == 0);
    }
}
