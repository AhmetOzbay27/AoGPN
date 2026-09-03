namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void GenRouting()
    {
        try
        {
            _coreConfig.route.final = Global.ProxyTag;
            var simpleDnsItem = context.SimpleDnsItem;

            var defaultDomainResolverTag = Global.SingboxDirectDNSTag;
            var dialDnsStrategy = Utils.DomainStrategy4Sbox(simpleDnsItem.Strategy4ProxyDial);

            var rawDNSItem = context.RawDnsItem;
            if (rawDNSItem is { Enabled: true })
            {
                defaultDomainResolverTag = Global.SingboxLocalDNSTag;
                dialDnsStrategy = rawDNSItem.DomainStrategy4Freedom.IsNullOrEmpty() ? null : rawDNSItem.DomainStrategy4Freedom;
            }
            else if (!simpleDnsItem.Strategy4Freedom.IsNullOrEmpty())
            {
                var directOutbound = _coreConfig.outbounds.FirstOrDefault(o => o.tag == Global.DirectTag);
                directOutbound?.domain_resolver = new()
                {
                    server = defaultDomainResolverTag,
                    strategy = Utils.DomainStrategy4Sbox(simpleDnsItem.Strategy4Freedom),
                };
            }
            _coreConfig.route.default_domain_resolver = new()
            {
                server = defaultDomainResolverTag,
                strategy = dialDnsStrategy
            };

            // ────────────────────────────────────────────────────────────────
            // GPN / TUN path — strict process-based split tunnel.
            //
            // Rules are evaluated top-to-bottom. GPN mode strips all v2rayN
            // legacy catch-alls so process_name rules execute first.
            //
            // When TUN works: assigned processes match → proxy; unassigned
            // fall through to route.final = direct.
            //
            // When TUN fails (VMware, etc.): CoreManager detects the missing
            // adapter and restarts the core with IsTunEnabled=false, which
            // enters the Global VPN path below where route.final = proxy.
            // ────────────────────────────────────────────────────────────────
            if (context.IsTunEnabled)
            {
                _coreConfig.route.auto_detect_interface = true;
                _coreConfig.route.final = Global.DirectTag;
                GenRoutingGpn();
                return;
            }

            // ────────────────────────────────────────────────────────────────
            // Global VPN / non-TUN path — full v2rayN legacy routing.
            // ────────────────────────────────────────────────────────────────
            GenRoutingGlobal();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// GPN/TUN routing — process-based split tunnel.
    /// Only injects TUN hygiene, user process/domain rules, and a catch-all direct.
    /// v2rayN's clash_mode, hosts, ICMP policy, and domain-strategy resolve rules
    /// are intentionally excluded so they cannot capture traffic before process_name
    /// rules are evaluated.
    /// </summary>
    private void GenRoutingGpn()
    {
        DiagLog.Write($"ROUTE GPN mode: stripping v2rayN legacy rules, building process-based chain");

        // ── 1. TUN base rules (LLMNR, multicast, etc.) ──
        var tunRules = JsonUtils.Deserialize<List<Rule4Sbox>>(EmbedUtils.GetEmbedText(Global.TunSingboxRulesFileName));
        if (tunRules != null)
        {
            _coreConfig.route.rules.AddRange(tunRules);
        }

        // ── 2. IPv6 gate ──
        if (_config.TunModeItem.EnableIPv6Address != true)
        {
            _coreConfig.route.rules.Add(new Rule4Sbox
            {
                ip_cidr = ["::/0"],
                action = "reject",
            });
            DiagLog.Write("ROUTE ipv6Block: ::/0 → reject (user disabled IPv6 through proxy)");
        }
        else
        {
            DiagLog.Write("ROUTE ipv6Allow: IPv6 traffic passes through to process_name rules");
        }

        // ── 2b. WARP ağ geçidi → tünel içi rota ──
        // GPN/TUN modunda warp socks outbound'u detour'suz üretilir (bkz.
        // GenWarpOutboundIfNeeded): sing-box kendi dial'ini OS yığınından yapar,
        // auto_route paketi kendi TUN'undan geri alır ve bu kural trafiği proxy
        // endpoint'ine (WireGuard netstack) yönlendirir. Kural, core-process
        // protect kurallarından (sing-box.exe → direct) ÖNCE gelmelidir — aksi
        // hâlde sing-box'ın kendi dial'i direct'e gider, fiziksel NIC'te ağ geçidi
        // rotası olmadığından "no route to host" ile düşer.
        if (_node.ConfigType == EConfigType.WireGuard && RoutingNeedsWarpOutbound())
        {
            var wgSubnet = DeriveWireGuardSubnet(_node.GetProtocolExtra().WgInterfaceAddress);
            _coreConfig.route.rules.Add(new Rule4Sbox
            {
                ip_cidr = [wgSubnet],
                outbound = Global.ProxyTag,
            });
            DiagLog.Write($"ROUTE warpSubnet: {wgSubnet} → {Global.ProxyTag} (WARP SOCKS dial'i TUN'dan endpoint'e)");
        }

        // ── 3. Core-process protect (sing-box.exe itself → direct) ──
        var lstDirectExe = BuildRoutingDirectExe();
        if (lstDirectExe.Count > 0)
        {
            _coreConfig.route.rules.Add(new()
            {
                port = [53],
                action = "hijack-dns",
                process_path = lstDirectExe,
            });
            _coreConfig.route.rules.Add(new()
            {
                outbound = Global.DirectTag,
                process_path = lstDirectExe,
            });
        }

        // ── 4. DNS hijack + sniff (always-on in TUN mode) ──
        // Sniffing is a route action since sing-box 1.13.0 — the legacy TUN inbound
        // sniff/sniff_override_destination fields were removed (FATAL on startup).
        // The 1.13 sniff action accepts only sniffer/timeout; the old
        // sniff_override_destination option has no route-action equivalent, so the
        // sniffed metadata is used for rule matching (DNS hijack, protocol rules)
        // without rewriting the connection destination.
        _coreConfig.route.rules.Add(new() { action = "sniff" });
        _coreConfig.route.rules.Add(new()
        {
            type = "logical",
            mode = "or",
            action = "hijack-dns",
            rules =
            [
                new() { port = [53] },
                new() { protocol = ["dns"] },
            ],
        });
        if (_config.CoreBasicItem.EnableFinalFragment)
        {
            _coreConfig.route.rules.Add(new()
            {
                protocol = ["tls"],
                action = "route-options",
                tls_record_fragment = true,
            });
        }

        // ── 5. User process_name / domain / IP rules ──
        var routing = context.RoutingItem;
        if (routing != null)
        {
            var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet);
            DiagLog.Write($"ROUTE genUserRules: routingId={routing.Id} ruleCount={rules?.Count ?? 0} tunEnabled=True routeFinal={_coreConfig.route.final}");
            // First-match-wins routing: any catch-all rule (port 0-65535 with no
            // process/domain/IP) must be emitted AFTER every specific entry rule,
            // or it would shadow them and send all traffic direct/proxy. The
            // persisted RuleSet can carry the catch-all at an arbitrary position
            // (older writes put it first), so defer it to the end defensively.
            var catchAllRules = new List<RulesItem>();
            foreach (var item in rules ?? [])
            {
                if (!item.Enabled || item.RuleType == ERuleType.DNS)
                    continue;

                if (IsCatchAllRule(item))
                {
                    catchAllRules.Add(item);
                    continue;
                }

                GenRoutingUserRule(item);
            }
            foreach (var catchAll in catchAllRules)
            {
                GenRoutingUserRule(catchAll);
            }
        }

        // ── 6. No explicit catch-all ──
        // In GPN mode unassigned traffic falls through to route.final which
        // GenRouting set to "direct". When TUN fails and the core restarts
        // in Global VPN mode, the Global path sets route.final = "proxy".

        DiagLog.Write($"ROUTE totalRulesAfterGen={_coreConfig.route.rules.Count} (GPN mode, final={_coreConfig.route.final})");
    }

    /// <summary>
    /// True when <paramref name="item"/> is a routing catch-all: it matches every
    /// destination (port 0-65535) and has no narrow match criteria (process/domain/IP).
    /// In first-match-wins routing such a rule must be evaluated last, else it shadows
    /// every specific entry rule that follows it.
    /// </summary>
    private static bool IsCatchAllRule(RulesItem item)
    {
        if (item.Port != "0-65535" || string.IsNullOrEmpty(item.Port))
        {
            return false;
        }
        return item.Process?.Count is null or 0
            && item.Domain?.Count is null or 0
            && item.Ip?.Count is null or 0;
    }

    /// <summary>
    /// Global VPN / non-TUN path — retains the full v2rayN legacy routing chain
    /// (ICMP, hosts, clash_mode catch-alls, domain-strategy resolve). This path
    /// is untouched to preserve the original behaviour when the TUN is not active.
    /// </summary>
    private void GenRoutingGlobal()
    {
        var simpleDnsItem = context.SimpleDnsItem;
        var rawDNSItem = context.RawDnsItem;
        // ICMP Routing
        var icmpRouting = _config.TunModeItem.IcmpRouting ?? "";
        if (!Global.TunIcmpRoutingPolicies.Contains(icmpRouting))
        {
            icmpRouting = Global.TunIcmpRoutingPolicies.First();
        }
        if (icmpRouting == "direct")
        {
            _coreConfig.route.rules.Add(new()
            {
                network = ["icmp"],
                outbound = Global.DirectTag,
            });
        }
        else if (icmpRouting != "rule")
        {
            var rejectMethod = icmpRouting switch
            {
                "unreachable" => "default",
                "drop" => "drop",
                _ => "reply",
            };
            _coreConfig.route.rules.Add(new()
            {
                network = ["icmp"],
                action = "reject",
                method = rejectMethod,
            });
        }

        if (_config.Inbound.First().SniffingEnabled)
        {
            _coreConfig.route.rules.Add(new() { action = "sniff" });
            _coreConfig.route.rules.Add(new()
            {
                type = "logical",
                mode = "or",
                action = "hijack-dns",
                rules =
                [
                    new() { port = [53] },
                    new() { protocol = ["dns"] },
                ],
            });
            if (_config.CoreBasicItem.EnableFinalFragment)
            {
                _coreConfig.route.rules.Add(new()
                {
                    protocol = ["tls"],
                    action = "route-options",
                    tls_record_fragment = true,
                });
            }
        }
        else
        {
            _coreConfig.route.rules.Add(new()
            {
                port = [53],
                action = "hijack-dns",
            });
            if (_config.CoreBasicItem.EnableFinalFragment)
            {
                _coreConfig.route.rules.Add(new()
                {
                    action = "route-options",
                    tls_record_fragment = true,
                });
            }
        }

        var hostsDomains = new List<string>();
        if (rawDNSItem is not { Enabled: true })
        {
            var userHostsMap = Utils.ParseHostsToDictionary(simpleDnsItem.Hosts);
            hostsDomains.AddRange(userHostsMap.Select(kvp => kvp.Key));
            if (simpleDnsItem.UseSystemHosts == true)
            {
                var systemHostsMap = Utils.GetSystemHosts();
                hostsDomains.AddRange(systemHostsMap.Select(kvp => kvp.Key));
            }
        }
        if (hostsDomains.Count > 0)
        {
            var hostsResolveRule = new Rule4Sbox { action = "resolve" };
            var hostsCounter = 0;
            foreach (var host in hostsDomains)
            {
                var domainRule = new Rule4Sbox();
                if (!ParseV2Domain(host, domainRule)) continue;
                if (domainRule.domain_keyword?.Count > 0 && !host.Contains(':'))
                {
                    domainRule.domain = domainRule.domain_keyword;
                    domainRule.domain_keyword = null;
                }
                if (domainRule.domain?.Count > 0)           { hostsResolveRule.domain ??= []; hostsResolveRule.domain.AddRange(domainRule.domain); hostsCounter++; }
                else if (domainRule.domain_keyword?.Count > 0) { hostsResolveRule.domain_keyword ??= []; hostsResolveRule.domain_keyword.AddRange(domainRule.domain_keyword); hostsCounter++; }
                else if (domainRule.domain_suffix?.Count > 0)  { hostsResolveRule.domain_suffix ??= []; hostsResolveRule.domain_suffix.AddRange(domainRule.domain_suffix); hostsCounter++; }
                else if (domainRule.domain_regex?.Count > 0)   { hostsResolveRule.domain_regex ??= []; hostsResolveRule.domain_regex.AddRange(domainRule.domain_regex); hostsCounter++; }
                else if (domainRule.geosite?.Count > 0)        { hostsResolveRule.geosite ??= []; hostsResolveRule.geosite.AddRange(domainRule.geosite); hostsCounter++; }
            }
            if (hostsCounter > 0) _coreConfig.route.rules.Add(hostsResolveRule);
        }

        _coreConfig.route.rules.Add(new() { outbound = Global.DirectTag, clash_mode = nameof(ERuleMode.Direct) });
        _coreConfig.route.rules.Add(new() { outbound = Global.ProxyTag, clash_mode = nameof(ERuleMode.Global) });

        var domainStrategy = _config.RoutingBasicItem.DomainStrategy4Singbox.NullIfEmpty();
        var routing = context.RoutingItem;
        if (routing.DomainStrategy4Singbox.IsNotEmpty())
            domainStrategy = routing.DomainStrategy4Singbox;
        var resolveRule = new Rule4Sbox { action = "resolve", strategy = domainStrategy };
        if (_config.RoutingBasicItem.DomainStrategy == Global.IPOnDemand)
            _coreConfig.route.rules.Add(resolveRule);

        var ipRules = new List<RulesItem>();
        if (routing != null)
        {
            var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet);
            DiagLog.Write($"ROUTE genUserRules: routingId={routing.Id} ruleCount={rules?.Count ?? 0} tunEnabled=False routeFinal={_coreConfig.route.final}");
            foreach (var item1 in rules ?? [])
            {
                if (!item1.Enabled || item1.RuleType == ERuleType.DNS) continue;
                GenRoutingUserRule(item1);
                if (item1.Ip?.Count > 0) ipRules.Add(item1);
            }
        }
        if (_config.RoutingBasicItem.DomainStrategy == Global.IPIfNonMatch)
        {
            _coreConfig.route.rules.Add(resolveRule);
            foreach (var item2 in ipRules) GenRoutingUserRule(item2);
        }

        DiagLog.Write($"ROUTE totalRulesAfterGen={_coreConfig.route.rules.Count} (Global mode)");
    }

    private List<string> BuildRoutingDirectExe()
    {
        var directExeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allCoreInfo = CoreInfoManager.Instance.GetCoreInfo();

        foreach (var coreConfig in allCoreInfo)
        {
            if (!context.ProtectCoreTypeList.Contains(coreConfig.CoreType))
            {
                continue;
            }
            if (coreConfig.CoreType == ECoreType.AoGPN)
            {
                continue;
            }
            if (coreConfig.CoreExes == null)
            {
                continue;
            }
            foreach (var baseExeName in coreConfig.CoreExes)
            {
                //directExeSet.Add(Utils.GetExeName(baseExeName));
                var exePath = CoreInfoManager.Instance.GetCoreExecFile(coreConfig, out _);
                if (!exePath.IsNullOrEmpty())
                {
                    directExeSet.Add(exePath);
                }
            }
        }

        return directExeSet.ToList();
    }

    private void GenRoutingUserRule(RulesItem? item)
    {
        try
        {
            if (item == null)
            {
                return;
            }
            item.OutboundTag = GenRoutingUserRuleOutbound(item.OutboundTag ?? Global.ProxyTag);
            var rules = _coreConfig.route.rules;

            var rule = new Rule4Sbox();
            if (item.OutboundTag == "block")
            {
                rule.action = "reject";
            }
            else
            {
                rule.outbound = item.OutboundTag;
            }

            if (item.Port.IsNotEmpty())
            {
                var portRanges = item.Port.Split(',').Where(it => it.Contains('-')).Select(it => it.Replace("-", ":")).ToList();
                var ports = item.Port.Split(',').Where(it => !it.Contains('-')).Select(it => it.ToInt()).ToList();

                rule.port_range = portRanges.Count > 0 ? portRanges : null;
                rule.port = ports.Count > 0 ? ports : null;
            }
            if (item.Network.IsNotEmpty())
            {
                rule.network = Utils.String2List(item.Network);
            }
            if (item.Protocol?.Count > 0)
            {
                rule.protocol = item.Protocol;
            }
            if (item.InboundTag?.Count >= 0)
            {
                rule.inbound = item.InboundTag;
            }
            var rule1 = JsonUtils.DeepCopy(rule);
            var rule2 = JsonUtils.DeepCopy(rule);
            var rule3 = JsonUtils.DeepCopy(rule);

            var hasDomainIp = false;
            if (item.Domain?.Count > 0)
            {
                var countDomain = 0;
                foreach (var it in item.Domain)
                {
                    if (ParseV2Domain(it, rule1))
                    {
                        countDomain++;
                    }
                }
                if (countDomain > 0)
                {
                    rules.Add(rule1);
                    hasDomainIp = true;
                }
            }

            if (item.Ip?.Count > 0)
            {
                var countIp = 0;
                var negativeIpList = item.Ip.Where(it => it.StartsWith('!')).ToList();
                if (negativeIpList.Count > 0)
                {
                    var positiveIpList = item.Ip.Except(negativeIpList).ToList();
                    var positiveRule = rule2;
                    positiveRule = JsonUtils.DeepCopy(rule2);
                    positiveRule.outbound = null;
                    positiveRule.action = null;
                    foreach (var it in positiveIpList)
                    {
                        if (ParseV2Address(it, positiveRule))
                        {
                            countIp++;
                        }
                    }
                    var negativeRule = new Rule4Sbox();
                    foreach (var it in negativeIpList)
                    {
                        // Remove first '!' and trim spaces
                        var ip = it[1..].Trim();
                        if (ParseV2Address(ip, negativeRule))
                        {
                            countIp++;
                        }
                    }
                    negativeRule.invert = true;
                    rule2 = new Rule4Sbox()
                    {
                        outbound = rule2.outbound,
                        action = rule2.action,
                        type = "logical",
                        mode = "or",
                        rules = [
                            positiveRule,
                            negativeRule
                        ]
                    };
                }
                else
                {
                    foreach (var it in item.Ip)
                    {
                        if (ParseV2Address(it, rule2))
                        {
                            countIp++;
                        }
                    }
                }
                if (countIp > 0)
                {
                    rules.Add(rule2);
                    hasDomainIp = true;
                }
            }

            if (item.Process?.Count > 0)
            {
                var ruleProcName = JsonUtils.DeepCopy(rule3);
                ruleProcName.process_name ??= [];
                var ruleProcPath = JsonUtils.DeepCopy(rule3);
                ruleProcPath.process_path ??= [];
                foreach (var process in item.Process)
                {
                    // sing-box doesn't support this, fall back to process name match
                    if (process is "self/" or "xray/")
                    {
                        ruleProcName.process_name.Add(Utils.GetExeName("sing-box"));
                        continue;
                    }

                    if (process.Contains('/') || process.Contains('\\'))
                    {
                        var procPath = process;
                        if (Utils.IsWindows())
                        {
                            procPath = procPath.Replace('/', '\\');
                        }
                        ruleProcPath.process_path.Add(procPath);
                        continue;
                    }

                    // sing-box strictly matches the exe suffix on Windows
                    var procName = Utils.GetExeName(process);

                    ruleProcName.process_name.Add(procName);
                }

                if (ruleProcName.process_name.Count > 0)
                {
                    // QUIC preemption: block UDP/443 for proxy-routed processes
                    // to force Chrome, modern browsers, and games to fall back
                    // to TCP, which the TUN captures correctly.
                    // Processes listed in Global.QuicBlockExemptProcesses (VPN
                    // clients, network tools) are excluded — their UDP/443 is
                    // legitimate tunnel traffic that must not be blocked.
                    if (rule.outbound != null
                        && rule.outbound != Global.DirectTag
                        && rule.outbound != Global.BlockTag)
                    {
                        var exemptNames = ruleProcName.process_name
                            .Where(p => Global.QuicBlockExemptProcesses.Contains(p, StringComparer.OrdinalIgnoreCase))
                            .ToList();
                        var nonExemptNames = ruleProcName.process_name.Except(exemptNames, StringComparer.OrdinalIgnoreCase).ToList();
                        if (exemptNames.Count > 0)
                        {
                            Logging.SaveLog($"[SingboxRouting] QUIC-block whitelist exempted: [{string.Join(", ", exemptNames)}]");
                            DiagLog.Write($"QUIC whitelist EXEMPT: {string.Join(", ", exemptNames)}");
                        }
                        if (nonExemptNames.Count > 0)
                        {
                            rules.Add(new Rule4Sbox
                            {
                                process_name = nonExemptNames,
                                network = ["udp"],
                                port = [443],
                                action = "reject",
                            });
                            Logging.SaveLog($"[SingboxRouting] Injected QUIC-block for: [{string.Join(", ", nonExemptNames)}] (reject)");
                            DiagLog.Write($"QUIC BLOCK injected for: {string.Join(", ", nonExemptNames)}");
                        }
                    }

                    rules.Add(ruleProcName);
                    hasDomainIp = true;
                }

                if (ruleProcPath.process_path.Count > 0)
                {
                    // QUIC preemption for process_path rules as well.
                    if (rule.outbound != null
                        && rule.outbound != Global.DirectTag
                        && rule.outbound != Global.BlockTag)
                    {
                        var exemptPaths = ruleProcPath.process_path
                            .Where(p => Global.QuicBlockExemptProcesses.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                            .ToList();
                        var nonExemptPaths = ruleProcPath.process_path.Except(exemptPaths, StringComparer.OrdinalIgnoreCase).ToList();
                        if (exemptPaths.Count > 0)
                        {
                            Logging.SaveLog($"[SingboxRouting] QUIC-block whitelist exempted paths: [{string.Join(", ", exemptPaths.Select(Path.GetFileName))}]");
                            DiagLog.Write($"QUIC whitelist EXEMPT paths: {string.Join(", ", exemptPaths.Select(Path.GetFileName))}");
                        }
                        if (nonExemptPaths.Count > 0)
                        {
                            rules.Add(new Rule4Sbox
                            {
                                process_path = nonExemptPaths,
                                network = ["udp"],
                                port = [443],
                                action = "reject",
                            });
                            Logging.SaveLog($"[SingboxRouting] Injected QUIC-block for paths: [{string.Join(", ", nonExemptPaths.Select(Path.GetFileName))}] (reject)");
                            DiagLog.Write($"QUIC BLOCK injected paths: {string.Join(", ", nonExemptPaths.Select(Path.GetFileName))}");
                        }
                    }

                    rules.Add(ruleProcPath);
                    hasDomainIp = true;
                }
            }

            if (!hasDomainIp
                && (rule.port != null || rule.port_range != null || rule.protocol != null || rule.inbound != null || rule.network != null))
            {
                rules.Add(rule);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private static bool ParseV2Domain(string domain, Rule4Sbox rule)
    {
        if (domain.StartsWith('#') || domain.StartsWith("ext:") || domain.StartsWith("ext-domain:"))
        {
            return false;
        }
        else if (domain.StartsWith(Global.GeoSitePrefix))
        {
            rule.geosite ??= [];
            rule.geosite?.Add(domain[Global.GeoSitePrefix.Length..]);
        }
        else if (domain.StartsWith("regexp:"))
        {
            rule.domain_regex ??= [];
            rule.domain_regex?.Add(domain.Replace(Global.RoutingRuleComma, ",").Substring(7));
        }
        else if (domain.StartsWith("domain:"))
        {
            rule.domain_suffix ??= [];
            rule.domain_suffix?.Add(domain.Substring(7));
        }
        else if (domain.StartsWith("full:"))
        {
            rule.domain ??= [];
            rule.domain?.Add(domain.Substring(5));
        }
        else if (domain.StartsWith("keyword:"))
        {
            rule.domain_keyword ??= [];
            rule.domain_keyword?.Add(domain.Substring(8));
        }
        else if (domain.StartsWith("dotless:"))
        {
            rule.domain_keyword ??= [];
            rule.domain_keyword?.Add(domain.Substring(8));
        }
        else
        {
            rule.domain_keyword ??= [];
            rule.domain_keyword?.Add(domain);
        }
        return true;
    }

    private static bool ParseV2Address(string address, Rule4Sbox rule)
    {
        if (address.StartsWith("ext:") || address.StartsWith("ext-ip:"))
        {
            return false;
        }
        else if (address.Equals($"{Global.GeoIPPrefix}private"))
        {
            rule.ip_is_private = true;
        }
        else if (address.StartsWith(Global.GeoIPPrefix))
        {
            rule.geoip ??= [];
            rule.geoip?.Add(address[Global.GeoIPPrefix.Length..]);
        }
        else
        {
            rule.ip_cidr ??= [];
            rule.ip_cidr?.Add(address);
        }
        return true;
    }

    private string GenRoutingUserRuleOutbound(string outboundTag)
    {
        if (Global.OutboundTags.Contains(outboundTag))
        {
            // warp yalnızca WireGuard düğümü + WARP outbound'u üretildiğinde geçerli;
            // aksi hâlde (VLESS/REALITY düğümü vb.) kurallar proxy'ye düşer — kural
            // var olmayan bir outbound'a işaret ederse sing-box başlamaz.
            if (outboundTag == Global.WarpTag
                && !_coreConfig.outbounds.Any(o => o.tag == Global.WarpTag))
            {
                return Global.ProxyTag;
            }
            return outboundTag;
        }

        var node = context.AllProxiesMap.GetValueOrDefault($"remark:{outboundTag}");

        if (node == null
            || (!Global.SingboxSupportConfigType.Contains(node.ConfigType)
            && !node.ConfigType.IsGroupType()))
        {
            return Global.ProxyTag;
        }

        var tag = $"{node.IndexId}-{Global.ProxyTag}-{node.Remarks}";
        if (_coreConfig.outbounds.Any(o => o.tag.StartsWith(tag))
            || (_coreConfig.endpoints != null && _coreConfig.endpoints.Any(e => e.tag.StartsWith(tag))))
        {
            return tag;
        }

        var proxyOutbounds = new CoreConfigSingboxService(context with { Node = node, }).BuildAllProxyOutbounds(tag);
        FillRangeProxy(proxyOutbounds, _coreConfig, false);

        return tag;
    }
}