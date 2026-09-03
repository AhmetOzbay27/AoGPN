namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void GenOutbounds()
    {
        var proxyOutbounds = BuildAllProxyOutbounds();
        FillRangeProxy(proxyOutbounds, _coreConfig, true);
        GenWarpOutboundIfNeeded();
    }

    /// <summary>
    /// WARP egress outbound: yalnızca WireGuard düğümü + en az bir "warp" kuralı
    /// varken üretilir. Socks outbound, sunucudaki WARP SOCKS5'ine (örn.
    /// 10.66.66.1:40000) bağlanır ve çıkış IP'si Oracle yerine Cloudflare WARP
    /// olur (Cloudflare WAF'ın datacenter IP bloklarını engellemesini aşar; Tarkov
    /// launcher auth'u bu yüzden düşerdi).
    ///
    /// Dial her durumda WG endpoint'ine DETOUR ile yapılır (TUN açık/kapalı fark
    /// etmez). Endpoint adresleri host prefix'e (/32) indirgendiği için ağ geçidi
    /// adresi (10.66.66.1/32) endpoint'in route set'ine açıkça eklenir
    /// (<see cref="EnsureGatewayInWgEndpoint"/>) — aksi hâlde internal dial
    /// "connect tcp 10.66.66.1:40000: no route to host" ile düşer (yalıtılmış
    /// sing-box doğrulaması + canlı ölçümle kanıtlandı). Daha önceki TUN döngüsü
    /// tasarımı (OS dial → auto_route → ip_cidr 10.66.66.0/24 → endpoint) artık
    /// kullanılmaz: o yolda TCP verisi canlıda düşüyordu (curl: connection reset,
    /// sing-box dial: i/o timeout) — ip_cidr kuralı uyumluluk için yerinde kalır.
    ///
    /// WireGuard dışı düğümlerde (VLESS/REALITY vb.) tünel içinde ulaşılabilir bir
    /// WARP dinleyicisi olmadığından outbound üretilmez; o durumda kural üretimi
    /// warp kurallarını proxy'ye düşürür (GenRoutingUserRuleOutbound).
    /// </summary>
    private void GenWarpOutboundIfNeeded()
    {
        try
        {
            if (_node.ConfigType != EConfigType.WireGuard)
            {
                return;
            }
            if (!RoutingNeedsWarpOutbound())
            {
                return;
            }

            var gateway = DeriveWireGuardGateway(_node.GetProtocolExtra().WgInterfaceAddress);
            var warp = new Outbound4Sbox
            {
                type = "socks",
                tag = Global.WarpTag,
                server = gateway,
                server_port = Global.WarpSocksDefaultPort,
            };

            // Dial her durumda WG endpoint detour'undan gider (TUN döngüsü yok):
            // TUN + OS dial → ip_cidr döngüsü canlıda TCP verisini düşürüyordu
            // (curl: connection reset, sing-box: i/o timeout). Detour'un "no route
            // to host" hatası ise ağ geçidinin endpoint route set'inde olmamasından
            // kaynaklanıyordu — EnsureGatewayInWgEndpoint bunu giderir.
            warp.detour = Global.ProxyTag;
            EnsureGatewayInWgEndpoint(gateway);

            _coreConfig.outbounds.Add(warp);
            DiagLog.Write($"WARP outbound added: socks {gateway}:{Global.WarpSocksDefaultPort} via detour={Global.ProxyTag} + gateway route");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// WARP SOCKS ağ geçidine (10.66.66.1:40000) detour dial'i, WG endpoint'inin
    /// route set'inde olmalı: endpoint adresleri host prefix'e (/32,/128)
    /// indirgendiği için ağ geçidi açıkça eklenmezse internal dial "connect tcp
    /// 10.66.66.1:40000: no route to host" ile düşer (yalıtılmış sing-box
    /// doğrulaması: /32-only endpoint → no route to host; +ağ geçidi /32 → dial
    /// endpoint'ten gider). Yalnızca warp outbound üretilen oturumlarda uygulanır;
    /// /32 eklemesi subnet/ARP yan etkisi oluşturmaz (alt ağ yakalanmaz).
    /// </summary>
    private void EnsureGatewayInWgEndpoint(string gateway)
    {
        var wgEndpoint = _coreConfig.endpoints?.FirstOrDefault(e => e.tag == Global.ProxyTag);
        if (wgEndpoint?.address is null)
        {
            return;
        }
        var gwCidr = $"{gateway}/32";
        if (!wgEndpoint.address.Contains(gwCidr, StringComparer.OrdinalIgnoreCase))
        {
            wgEndpoint.address.Add(gwCidr);
            DiagLog.Write($"WG_ENDPOINT gateway route added: {gwCidr} (warp detour dial hedefi)");
        }
    }

    /// <summary>Yönetilen kurallardan biri warp outbound'una gidiyor mu?</summary>
    private bool RoutingNeedsWarpOutbound()
    {
        var routing = context.RoutingItem;
        if (routing is null || routing.RuleSet.IsNullOrEmpty())
        {
            return false;
        }
        var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? [];
        return rules.Any(r => r.Enabled
            && string.Equals(r.OutboundTag, Global.WarpTag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// İstemci WireGuard adresinden (ör. "10.66.66.2/24") ağ geçidini (10.66.66.1)
    /// türetir — WARP SOCKS5'i sunucunun wg0 arayüzünde dinler ve tünel içindeki
    /// ilk kullanılabilir adres sunucudur.
    /// </summary>
    /// <remarks>
    /// Endpoint arayüz adresinin ön eki ne olursa olsun (kullanıcı /24 de girse
    /// üretici /32'ye indirger) ağ geçidi mantığı aynıdır: tünel adresinin bulunduğu
    /// ağın ilk kullanılabilir adresi sunucu wg0'dır.
    /// </remarks>
    private static string DeriveWireGuardGateway(string? interfaceAddress)
    {
        try
        {
            var cidr = interfaceAddress?.Split(',')[0].Trim() ?? string.Empty;
            if (cidr.IsNullOrEmpty())
            {
                return "10.66.66.1";
            }
            var parts = cidr.Split('/');
            var ip = System.Net.IPAddress.Parse(parts[0]);
            var prefix = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 24;
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return "10.66.66.1"; // IPv6 adreslerde varsayılan (sunucular IPv4 /24)
            }
            var bytes = ip.GetAddressBytes();
            for (var i = 0; i < 4; i++)
            {
                var bits = Math.Clamp(prefix - i * 8, 0, 8);
                var mask = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF;
                bytes[i] = (byte)(bytes[i] & mask);
            }
            bytes[3] = 1; // ağın ilk kullanılabilir adresi = sunucu wg0
            return new System.Net.IPAddress(bytes).ToString();
        }
        catch
        {
            return "10.66.66.1";
        }
    }

    /// <summary>
    /// İstemci WireGuard adresinden (ör. "10.66.66.2/24") WG alt ağını türetir
    /// (10.66.66.0/24) — WARP SOCKS dial'inin (10.66.66.1:40000) TUN'dan geri
    /// alındığında proxy endpoint'ine yönlendirilmesi için ip_cidr kuralında
    /// kullanılır. Bozuk/boş girdi varsayılana (10.66.66.0/24) düşer.
    /// </summary>
    private static string DeriveWireGuardSubnet(string? interfaceAddress)
    {
        try
        {
            var cidr = interfaceAddress?.Split(',')[0].Trim() ?? string.Empty;
            if (cidr.IsNullOrEmpty())
            {
                return "10.66.66.0/24";
            }
            var parts = cidr.Split('/');
            var ip = System.Net.IPAddress.Parse(parts[0]);
            var prefix = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 24;
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return "10.66.66.0/24"; // IPv6 adreslerde varsayılan (sunucular IPv4 /24)
            }
            var bytes = ip.GetAddressBytes();
            for (var i = 0; i < 4; i++)
            {
                var bits = Math.Clamp(prefix - i * 8, 0, 8);
                var mask = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF;
                bytes[i] = (byte)(bytes[i] & mask);
            }
            return $"{new System.Net.IPAddress(bytes)}/{prefix}";
        }
        catch
        {
            return "10.66.66.0/24";
        }
    }

    private List<BaseServer4Sbox> BuildAllProxyOutbounds(string baseTagName = Global.ProxyTag, bool withSelector = true)
    {
        var proxyOutboundList = new List<BaseServer4Sbox>();
        if (!_node.ConfigType.IsComplexType())
        {
            var outbound = BuildProxyOutbound(baseTagName);
            proxyOutboundList.Add(outbound);
        }
        else
        {
            proxyOutboundList.AddRange(BuildGroupProxyOutbounds(baseTagName));
        }
        if (withSelector)
        {
            var proxyTags = proxyOutboundList.Where(n => n.tag.StartsWith(baseTagName)).Select(n => n.tag).ToList();
            if (proxyTags.Count > 1)
            {
                proxyOutboundList.InsertRange(0, BuildSelectorOutbounds(proxyTags, baseTagName));
            }
        }
        return proxyOutboundList;
    }

    private BaseServer4Sbox BuildProxyOutbound(string baseTagName = Global.ProxyTag)
    {
        var outbound = BuildProxyServer();
        outbound.tag = baseTagName;
        return outbound;
    }

    private List<BaseServer4Sbox> BuildGroupProxyOutbounds(string baseTagName = Global.ProxyTag)
    {
        var proxyOutboundList = new List<BaseServer4Sbox>();
        switch (_node.ConfigType)
        {
            case EConfigType.PolicyGroup:
                proxyOutboundList = BuildOutboundsList(baseTagName);
                break;

            case EConfigType.ProxyChain:
                proxyOutboundList = BuildChainOutboundsList(baseTagName);
                break;
        }
        return proxyOutboundList;
    }

    private BaseServer4Sbox BuildProxyServer()
    {
        try
        {
            if (_node.ConfigType == EConfigType.WireGuard)
            {
                return BuildWireGuardEndpoint();
            }
            var txtOutbound = EmbedUtils.GetEmbedText(Global.SingboxSampleOutbound);
            var outbound = JsonUtils.Deserialize<Outbound4Sbox>(txtOutbound);
            FillOutbound(outbound);
            return outbound;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        throw new InvalidOperationException();
    }

    private void FillOutbound(Outbound4Sbox outbound)
    {
        try
        {
            var protocolExtra = _node.GetProtocolExtra();
            var transportExtra = _node.GetTransportExtra();
            var network = _node.GetNetwork();
            outbound.server = _node.Address;
            outbound.server_port = _node.Port;
            outbound.type = Global.ProtocolTypes[_node.ConfigType];

            switch (_node.ConfigType)
            {
                case EConfigType.VMess:
                    {
                        outbound.uuid = _node.Password;
                        outbound.alter_id = int.TryParse(protocolExtra.AlterId, out var result) ? result : 0;
                        if (Global.VmessSecurities.Contains(protocolExtra.VmessSecurity))
                        {
                            outbound.security = protocolExtra.VmessSecurity;
                        }
                        else
                        {
                            outbound.security = Global.DefaultSecurity;
                        }

                        FillOutboundMux(outbound);
                        FillOutboundTransport(outbound);
                        break;
                    }
                case EConfigType.Shadowsocks:
                    {
                        outbound.method = AppManager.Instance.GetShadowsocksSecurities(_node).Contains(protocolExtra.SsMethod)
                            ? protocolExtra.SsMethod : Global.None;
                        outbound.password = _node.Password;
                        outbound.udp_over_tcp = protocolExtra.Uot == true ? true : null;

                        if (network == nameof(ETransport.raw) && transportExtra.RawHeaderType == Global.RawHeaderHttp)
                        {
                            outbound.plugin = "obfs-local";
                            outbound.plugin_opts = $"obfs=http;obfs-host={transportExtra.Host};";
                        }
                        else
                        {
                            var pluginArgs = string.Empty;
                            if (network == nameof(ETransport.ws))
                            {
                                pluginArgs += "mode=websocket;";
                                pluginArgs += $"host={transportExtra.Host};";
                                // https://github.com/shadowsocks/v2ray-plugin/blob/e9af1cdd2549d528deb20a4ab8d61c5fbe51f306/args.go#L172
                                // Equal signs and commas [and backslashes] must be escaped with a backslash.
                                var path = (transportExtra.Path ?? string.Empty).Replace("\\", "\\\\").Replace("=", "\\=").Replace(",", "\\,");
                                pluginArgs += $"path={path};";
                            }
                            if (_node.StreamSecurity == Global.StreamSecurity)
                            {
                                pluginArgs += "tls;";
                                var certs = CertPemManager.ParsePemChain(_node.Cert);
                                if (certs.Count > 0)
                                {
                                    var cert = certs.First();
                                    const string beginMarker = "-----BEGIN CERTIFICATE-----\n";
                                    const string endMarker = "\n-----END CERTIFICATE-----";

                                    var base64Content = cert.Replace(beginMarker, "").Replace(endMarker, "").Trim();

                                    base64Content = base64Content.Replace("=", "\\=");

                                    pluginArgs += $"certRaw={base64Content};";
                                }
                            }
                            if (pluginArgs.Length > 0)
                            {
                                outbound.plugin = "v2ray-plugin";
                                pluginArgs += "mux=0;";
                                // pluginStr remove last ';'
                                pluginArgs = pluginArgs[..^1];
                                outbound.plugin_opts = pluginArgs;
                            }
                        }

                        FillOutboundMux(outbound);
                        break;
                    }
                case EConfigType.SOCKS:
                    {
                        outbound.version = "5";
                        if (_node.Username.IsNotEmpty()
                            && _node.Password.IsNotEmpty())
                        {
                            outbound.username = _node.Username;
                            outbound.password = _node.Password;
                        }
                        break;
                    }
                case EConfigType.HTTP:
                    {
                        if (_node.Username.IsNotEmpty()
                            && _node.Password.IsNotEmpty())
                        {
                            outbound.username = _node.Username;
                            outbound.password = _node.Password;
                        }
                        break;
                    }
                case EConfigType.VLESS:
                    {
                        outbound.uuid = _node.Password;

                        outbound.packet_encoding = "xudp";

                        if (protocolExtra.Flow is "xtls-rprx-vision" or "xtls-rprx-vision-udp443")
                        {
                            outbound.flow = "xtls-rprx-vision";
                        }
                        else if (!protocolExtra.Flow.IsNullOrEmpty())
                        {
                            outbound.flow = protocolExtra.Flow;
                        }

                        FillOutboundMux(outbound);
                        FillOutboundTransport(outbound);
                        break;
                    }
                case EConfigType.Trojan:
                    {
                        outbound.password = _node.Password;

                        FillOutboundMux(outbound);
                        FillOutboundTransport(outbound);
                        break;
                    }
                case EConfigType.Hysteria2:
                    {
                        outbound.password = _node.Password;

                        if (!protocolExtra.SalamanderPass.IsNullOrEmpty())
                        {
                            var isGecko = !protocolExtra.GeckoMinPacketSize.IsNullOrEmpty() || !protocolExtra.GeckoMaxPacketSize.IsNullOrEmpty();
                            outbound.obfs = new()
                            {
                                type = isGecko ? "gecko" : "salamander",
                                password = protocolExtra.SalamanderPass.TrimEx(),
                            };
                            if (isGecko)
                            {
                                outbound.obfs.min_packet_size = protocolExtra.GeckoMinPacketSize.ToInt();
                                outbound.obfs.max_packet_size = protocolExtra.GeckoMaxPacketSize.ToInt();
                            }
                        }
                        int? upMbps = protocolExtra?.UpMbps is { } su and >= 0
                            ? su
                            : _config.HysteriaItem.UpMbps;
                        int? downMbps = protocolExtra?.DownMbps is { } sd and >= 0
                            ? sd
                            : _config.HysteriaItem.DownMbps;
                        outbound.up_mbps = upMbps > 0 ? upMbps : null;
                        outbound.down_mbps = downMbps > 0 ? downMbps : null;
                        var ports = protocolExtra?.Ports?.IsNullOrEmpty() == false ? protocolExtra.Ports : null;
                        if ((!ports.IsNullOrEmpty()) && (ports.Contains(':') || ports.Contains('-') || ports.Contains(',')))
                        {
                            outbound.server_port = null;
                            outbound.server_ports = ports.Split(',')
                                .Select(p => p.Trim())
                                .Where(p => p.IsNotEmpty())
                                .Select(p =>
                                {
                                    var port = p.Replace('-', ':');
                                    return port.Contains(':') ? port : $"{port}:{port}";
                                })
                                .ToList();
                            outbound.hop_interval = _config.HysteriaItem.HopInterval >= 5
                                ? $"{_config.HysteriaItem.HopInterval}s"
                                : $"{Global.Hysteria2DefaultHopInt}s";
                            if (int.TryParse(protocolExtra.HopInterval, out var hiResult))
                            {
                                outbound.hop_interval = hiResult >= 5 ? $"{hiResult}s" : outbound.hop_interval;
                            }
                            else if (protocolExtra.HopInterval?.Contains('-') ?? false)
                            {
                                // may be a range like 5-10
                                var parts = protocolExtra.HopInterval.Split('-');
                                if (parts.Length == 2 && int.TryParse(parts[0], out var hiL) &&
                                    int.TryParse(parts[0], out var hiH))
                                {
                                    var hi = (hiL + hiH) / 2;
                                    outbound.hop_interval = hi >= 5 ? $"{hi}s" : outbound.hop_interval;
                                }
                            }
                        }

                        if (HyRealm.TryParse(protocolExtra.Hy2RealmUrl, out var realm)
                            && realm is not null)
                        {
                            var realm4Sbox = new HyRealm4Sbox()
                            {
                                server_url = realm.ToServerUrl(),
                                token = realm.Token,
                                realm_id = realm.RealmName,
                                stun_servers = realm.StunList?.Count > 0 ? realm.StunList : null,
                            };
                            outbound.realm = realm4Sbox;
                            outbound.server = null;
                            outbound.server_port = null;
                            outbound.server_ports = null;
                        }

                        break;
                    }
                case EConfigType.TUIC:
                    {
                        outbound.uuid = _node.Username;
                        outbound.password = _node.Password;
                        outbound.congestion_control = protocolExtra.CongestionControl;
                        break;
                    }
                case EConfigType.Anytls:
                    {
                        outbound.password = _node.Password;
                        break;
                    }
                case EConfigType.Naive:
                    {
                        outbound.username = _node.Username;
                        outbound.password = _node.Password;
                        if (protocolExtra.NaiveQuic == true)
                        {
                            outbound.quic = true;
                            outbound.quic_congestion_control = protocolExtra.CongestionControl.NullIfEmpty();
                        }
                        if (protocolExtra.InsecureConcurrency > 0)
                        {
                            outbound.insecure_concurrency = protocolExtra.InsecureConcurrency;
                        }
                        outbound.udp_over_tcp = protocolExtra.Uot == true ? true : null;
                        break;
                    }
            }

            FillOutboundTls(outbound);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// Gerçek WireGuard outbound (sing-box 1.12+ "endpoints" şeması).
    ///
    /// Kritik tasarım kararı: <c>system = false</c>. Endpoint kullanıcı alanında
    /// (userspace) çalışır ve sistem arayüzü oluşturmaz. Böylece GPN modundaki
    /// singbox_tun (TUN inbound, auto_route) ile aynı config içinde çakışmadan
    /// birlikte çalışır: oyun trafiği TUN'dan girer, process_name kuralı proxy'ye
    /// yönlendirir, WireGuard kriptosu kullanıcı alanında yapılır, şifreli UDP
    /// fiziksel arayüzden çıkar (saf UDP — TCP meltdown yok).
    ///
    /// sing-box 1.13.19'da system alanı varsayılan olarak userspace'tir; açıkça
    /// false yazarak davranış sürümler arasında sabitlenir. Bu üretici, şablon
    /// JSON'dan deserialize etmek yerine endpoint'i programatik kurar — örnek
    /// şablondaki alanlara bağımlılık kalmaz.
    ///
    /// Arayüz adresi ayrıca host prefix'e indirgenir (<see cref="NormalizeEndpointAddresses"/>)
    /// — gVisor netstack'ine arayüz CIDR'si olarak alt ağ vermek (10.66.66.2/24 gibi)
    /// o alt ağı doğrudan bağlı sayarak komşu/ARP mantığını devreye sokabilir; host
    /// prefix bunu imkânsız kılar. Unutulmamalı: canlı WARP hatasının asıl kaynağı
    /// bu değildi (yalıtılmış testler /24 ile de in-tunnel dial'i yönlendiriyor),
    /// gerçek kök neden outbound → endpoint detour dial'iydi — bkz.
    /// <see cref="GenWarpOutboundIfNeeded"/> ve GenRoutingGpn warpSubnet kuralı.
    /// </summary>
    private Endpoints4Sbox BuildWireGuardEndpoint()
    {
        var protocolExtra = _node.GetProtocolExtra();

        // Resolve peer hostname to IPv4 only — Windows without IPv6 support
        // fails with "listen udp6 [::]: An invalid argument was supplied" when
        // sing-box tries to bind a dual-stack UDP socket for the WireGuard tunnel.
        var peerHost = _node.Address;
        var peerPort = _node.Port;
        try
        {
            if (!System.Net.IPAddress.TryParse(peerHost, out _))
            {
                var addrs = System.Net.Dns.GetHostAddresses(peerHost);
                var ipv4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ipv4 is not null)
                {
                    peerHost = ipv4.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"WG_ENDPOINT dns_resolve_failed host={_node.Address} error={ex.Message}");
        }

        // Windows'ta IPv6 protokolü adaptörde devre dışıysa (DisabledComponents veya
        // adaptör binding kapalı), sing-box WireGuard endpoint'i kendi UDP socket'ini
        // dual-stack açar ve "listen udp6 [::]: An invalid argument was supplied" +
        // "address family not supported by protocol" hatalarıyla el sıkışma hiç
        // gönderilemez (SagerNet/sing-box#3571 — düzeltilmemiş upstream bug).
        //
        // Çözüm: IPv6 kullanılamıyorsa (kullanıcı ayarı kapalı VEYA sistem gerçekten
        // IPv6 UDP bind desteklemiyor) endpoint'e inet4_bind_address=0.0.0.0 vererek
        // IPv4-only bind zorla ve peer'ın allowed_ips'inden ::/0'ı çıkar — sing-box
        // artık yalnızca IPv4 socket açar, el sıkışma gönderilir. IPv6 varsa davranış
        // değişmez (dual-stack korunur).
        var ipv6Available = _config.TunModeItem.EnableIPv6Address == true && CanBindIpv6Udp();
        var allowedIps = ipv6Available
            ? new List<string> { "0.0.0.0/0", "::/0" }
            : new List<string> { "0.0.0.0/0" };

        var endpoint = new Endpoints4Sbox
        {
            type = Global.ProtocolTypes[_node.ConfigType],
            tag = Global.ProxyTag,
            system = false,
            mtu = protocolExtra.WgMtu > 0 ? protocolExtra.WgMtu : Global.TunMtus.First(),
            address = NormalizeEndpointAddresses(protocolExtra.WgInterfaceAddress),
            private_key = _node.Password,
            peers =
            [
                new Peer4Sbox
                {
                    public_key = protocolExtra.WgPublicKey ?? string.Empty,
                    pre_shared_key = protocolExtra.WgPresharedKey,
                    reserved = ParseWgReserved(protocolExtra.WgReserved),
                    address = peerHost,
                    port = peerPort,
                    allowed_ips = allowedIps,
                    persistent_keepalive_interval = protocolExtra.WgPersistentKeepalive is > 0
                        ? protocolExtra.WgPersistentKeepalive
                        : null,
                },
            ],
        };

        // TUN döngüsü koruması: sing-box TUN (auto_route + strict_route) açıkken,
        // kendi WireGuard endpoint'inin el sıkışma UDP'si fiziksel NIC'e bağlanmazsa
        // TUN'a geri girer (sing-box kendi paketini yakalar) ve el sıkışma asla
        // tamamlanmaz (log: inbound/tun + endpoint/wireguard aynı hedefe — döngü).
        // route.auto_detect_interface yalnızca outbound'lara uygulanır, endpoint'e
        // değil; bu yüzden endpoint'i açıkça fiziksel arayüze bağlıyoruz.
        //
        // TUN döngüsü koruması — canlı olarak doğrulandı (SagerNet/sing-box#2900/
        // #3571): endpoint'i direct outbound'a detour et. direct, TUN modunda
        // bind_interface ile fiziksel NIC'e bağlanır (ApplyTunLoopProtection); böylece
        // el sıkışma UDP'si makineden fiziksel ağ üzerinden çıkar ve sing-box'ın kendi
        // TUN'u (auto_route+strict_route) paketi tekrar yakalayıp sonsuz döngüye
        // sokmaz. Ayrıca detour'dayken endpoint kendi dual-stack (udp6) listener'
        // ını açmadığından, IPv6 kapalı makinedeki "listen udp6 [::]" hatası da
        // ortadan kalkar. detour etkinken diğer dial alanları (inet4_bind_address /
        // bind_interface) yok sayılır — o yüzden bu ikisi endpoint'te kurulmaz.
        endpoint.detour = Global.DirectTag;
        DiagLog.Write(ipv6Available
            ? "WG_ENDPOINT tun-loop-protect detour=direct (IPv6 dual-stack, allowed_ips=0.0.0.0/0,::/0)"
            : "WG_ENDPOINT ipv6-unavailable → IPv4-only bind via detour=direct (allowed_ips=0.0.0.0/0)");
        DiagLog.Write($"WG_ENDPOINT peer={peerHost}:{peerPort} (original={_node.Address}:{_node.Port}) mtu={endpoint.mtu} address={string.Join(",", endpoint.address)}");
        DiagLog.Write($"WG_ENDPOINT address normalized to host prefix (/32,/128) — gVisor subnet route would break in-tunnel dials (WARP gateway) with 'no route to host'");
        return endpoint;
    }

    /// <summary>
    /// Sistemin IPv6 UDP socket'i gerçekten açıp açamadığını dener. Windows'ta IPv6
    /// protokolü adaptör binding'den kaldırılmışsa <c>Socket.OSSupportsIPv6</c> yanlış
    /// pozitif dönebilir; gerçek bind denemesi kesin sonuç verir (sing-box'ın yaptığı
    /// işlemin aynısı).
    /// </summary>
    private static bool CanBindIpv6Udp()
    {
        try
        {
            using var socket = new Socket(
                System.Net.Sockets.AddressFamily.InterNetworkV6,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.SetSocketOption(
                System.Net.Sockets.SocketOptionLevel.IPv6,
                System.Net.Sockets.SocketOptionName.IPv6Only,
                true);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Any, 0));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Endpoint arayüz adreslerini host prefix'ine indirger (IPv4 → /32, IPv6 → /128).
    ///
    /// Neden: GPN endpoint'i userspace gVisor netstack'inde çalışır (system=false).
    /// gVisor'a "10.66.66.2/24" gibi bir CIDR verildiğinde o alt ağ (10.66.66.0/24)
    /// DOĞRUDAN BAĞLI kabul edilir; alt ağ içindeki diğer adreslere (ör. sunucudaki
    /// WARP SOCKS5'i 10.66.66.1:40000) giden TCP dial'ler ARP/komşu çözümü ister.
    /// WireGuard linkinin link katmanı yoktur (ARPHardwareNone, MAC'siz) — çözüm
    /// asla tamamlanmaz ve bağlantı "connect tcp 10.66.66.1:40000: no route to
    /// host" ile düşer (canlı ölçüm: tünel sağlamken WARP outbound'una sevk edilen
    /// launcher auth trafiğinin tamamı bu hatayla düşüyor; proxy/direct rotaları
    /// etkilenmiyordu).
    ///
    /// Host prefix'te alt ağ rotası oluşmaz; 10.66.66.1, gVisor'un varsayılan
    /// (0.0.0.0/0) rotasıyla tünelin içinden hedeflenir ve sunucudaki dinleyiciye
    /// ulaşır. Bozuk girdi olduğu gibi korunur; boş girdi varsayılana düşer.
    /// </summary>
    private static List<string> NormalizeEndpointAddresses(string? interfaceAddress)
    {
        var result = new List<string>();
        foreach (var raw in Utils.String2List(interfaceAddress) ?? [])
        {
            var cidr = raw.Trim();
            if (cidr.IsNullOrEmpty())
            {
                continue;
            }
            try
            {
                var slash = cidr.IndexOf('/');
                var ipPart = slash >= 0 ? cidr[..slash] : cidr;
                var ip = System.Net.IPAddress.Parse(ipPart);
                var bits = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                result.Add($"{ip}/{bits}");
            }
            catch
            {
                result.Add(cidr);
            }
        }
        return result.Count > 0 ? result : ["172.16.0.2/32"];
    }

    /// <summary>"1,2,3" biçimindeki WireGuard Reserved listesini güvenle çözer; bozuk girdi null döner.</summary>
    private static List<int>? ParseWgReserved(string? wgReserved)
    {
        try
        {
            return Utils.String2List(wgReserved)?.Select(s => s.Trim()).Select(int.Parse).ToList();
        }
        catch
        {
            return null;
        }
    }

    private void FillOutboundMux(Outbound4Sbox outbound)
    {
        try
        {
            var muxEnabled = _node.MuxEnabled ?? false;
            if (muxEnabled && _config.Mux4SboxItem.Protocol.IsNotEmpty())
            {
                var mux = new Multiplex4Sbox()
                {
                    enabled = true,
                    protocol = _config.Mux4SboxItem.Protocol,
                    max_connections = _config.Mux4SboxItem.MaxConnections,
                    padding = _config.Mux4SboxItem.Padding,
                };
                outbound.multiplex = mux;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private void FillOutboundTls(Outbound4Sbox outbound)
    {
        try
        {
            if (_node.StreamSecurity is not (Global.StreamSecurityReality or Global.StreamSecurity))
            {
                return;
            }
            if (_node.ConfigType is EConfigType.Shadowsocks or EConfigType.SOCKS or EConfigType.WireGuard)
            {
                return;
            }
            var serverName = string.Empty;
            if (_node.Sni.IsNotEmpty())
            {
                serverName = _node.Sni;
            }
            else
            {
                var host = _node.GetNetwork() switch
                {
                    nameof(ETransport.raw) => _node.GetTransportExtra().Host,
                    nameof(ETransport.ws) => _node.GetTransportExtra().Host,
                    nameof(ETransport.httpupgrade) => _node.GetTransportExtra().Host,
                    nameof(ETransport.xhttp) => _node.GetTransportExtra().Host,
                    nameof(ETransport.grpc) => _node.GetTransportExtra().GrpcAuthority,
                    _ => null,
                };
                serverName = Utils.String2List(host)?.First();
            }
            var tls = new Tls4Sbox()
            {
                enabled = true,
                server_name = serverName,
                insecure = _node.GetAllowInsecure(),
                alpn = _node.GetAlpn(),
            };
            if (_config.CoreBasicItem.EnableFragment == true)
            {
                tls.fragment = true;
                tls.record_fragment = true;
            }
            // Apply a browser TLS fingerprint so the server (e.g. Cloudflare) cannot
            // recognise the raw Go/uTLS handshake as non-browser traffic. If neither the
            // node nor the configured default carries one, fall back to a Chrome client
            // hello - exactly what v2rayN relies on for Reality/VLESS over the system
            // proxy. Without this, empty-fingerprint nodes emit a default Go fingerprint
            // that Cloudflare flags as "Suspicious behavior".
            var nodeFingerprint = _node.Fingerprint.NullIfEmpty() ?? _config.CoreBasicItem.DefFingerprint;
            if (nodeFingerprint.IsNotEmpty())
            {
                tls.utls = new Utls4Sbox()
                {
                    enabled = true,
                    fingerprint = nodeFingerprint
                };
            }
            else if (_node.StreamSecurity == Global.StreamSecurityReality)
            {
                // Reality must always present a plausible browser fingerprint;
                // otherwise the handshake is trivially detectable.
                tls.utls = new Utls4Sbox()
                {
                    enabled = true,
                    fingerprint = "chrome"
                };
            }
            if (_node.StreamSecurity == Global.StreamSecurity)
            {
                var certs = CertPemManager.ParsePemChain(_node.Cert);
                if (certs.Count > 0)
                {
                    tls.certificate = certs;
                    tls.insecure = false;
                }
            }
            else if (_node.StreamSecurity == Global.StreamSecurityReality)
            {
                tls.reality = new Reality4Sbox()
                {
                    enabled = true,
                    public_key = _node.PublicKey,
                    short_id = _node.ShortId
                };
                tls.insecure = false;
            }
            var (ech, _) = ParseEchParam(_node.EchConfigList);
            if (ech is not null)
            {
                tls.ech = ech;
            }
            outbound.tls = tls;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private void FillOutboundTransport(Outbound4Sbox outbound)
    {
        try
        {
            var transport = new Transport4Sbox();
            var transportExtra = _node.GetTransportExtra();
            var useragent = _config.CoreBasicItem.DefUserAgent ?? string.Empty;
            var useragentValue = Global.RawHttpUserAgentTexts.GetValueOrDefault(useragent, useragent);

            switch (_node.GetNetwork())
            {
                case nameof(ETransport.raw):   //http
                    if (transportExtra.RawHeaderType == Global.RawHeaderHttp)
                    {
                        transport.type = nameof(ETransport.http);
                        transport.host = transportExtra.Host.IsNullOrEmpty()
                            ? null
                            : Utils.String2List(transportExtra.Host);
                        transport.path = transportExtra.Path.NullIfEmpty();
                        if (!useragentValue.IsNullOrEmpty())
                        {
                            transport.headers ??= new();
                            transport.headers.UserAgent = useragentValue;
                        }
                    }
                    break;

                case nameof(ETransport.ws):
                    transport.type = nameof(ETransport.ws);
                    var wsPath = transportExtra.Path;

                    // Parse eh and ed parameters from path using regex
                    if (!wsPath.IsNullOrEmpty())
                    {
                        var edRegex = new Regex(@"[?&]ed=(\d+)");
                        var edMatch = edRegex.Match(wsPath);
                        if (edMatch.Success && int.TryParse(edMatch.Groups[1].Value, out var edValue))
                        {
                            transport.max_early_data = edValue;
                            transport.early_data_header_name = "Sec-WebSocket-Protocol";

                            wsPath = edRegex.Replace(wsPath, "");
                            wsPath = wsPath.Replace("?&", "?");
                            if (wsPath.EndsWith('?'))
                            {
                                wsPath = wsPath.TrimEnd('?');
                            }
                        }

                        var ehRegex = new Regex(@"[?&]eh=([^&]+)");
                        var ehMatch = ehRegex.Match(wsPath);
                        if (ehMatch.Success)
                        {
                            transport.early_data_header_name = Uri.UnescapeDataString(ehMatch.Groups[1].Value);
                        }
                    }

                    transport.path = wsPath.NullIfEmpty();
                    if (transportExtra.Host.IsNotEmpty())
                    {
                        transport.headers = new()
                        {
                            Host = transportExtra.Host
                        };
                    }
                    if (!useragentValue.IsNullOrEmpty())
                    {
                        transport.headers ??= new();
                        transport.headers.UserAgent = useragentValue;
                    }
                    break;

                case nameof(ETransport.httpupgrade):
                    transport.type = nameof(ETransport.httpupgrade);
                    transport.path = transportExtra.Path.NullIfEmpty();
                    transport.host = transportExtra.Host.NullIfEmpty();
                    if (!useragentValue.IsNullOrEmpty())
                    {
                        transport.headers ??= new();
                        transport.headers.UserAgent = useragentValue;
                    }

                    break;

                case nameof(ETransport.grpc):
                    transport.type = nameof(ETransport.grpc);
                    transport.service_name = transportExtra.GrpcServiceName;
                    transport.idle_timeout = _config.GrpcItem.IdleTimeout?.ToString("##s");
                    transport.ping_timeout = _config.GrpcItem.HealthCheckTimeout?.ToString("##s");
                    transport.permit_without_stream = _config.GrpcItem.PermitWithoutStream;
                    break;

                default:
                    break;
            }
            if (transport.type != null)
            {
                outbound.transport = transport;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private List<Outbound4Sbox> BuildSelectorOutbounds(List<string> proxyTags, string baseTagName = Global.ProxyTag)
    {
        var multipleLoad = _node.GetProtocolExtra().MultipleLoad ?? EMultipleLoad.LeastPing;
        var outUrltest = new Outbound4Sbox
        {
            type = "urltest",
            tag = $"{baseTagName}-auto",
            outbounds = proxyTags,
            interrupt_exist_connections = false,
        };

        if (multipleLoad == EMultipleLoad.Fallback)
        {
            outUrltest.tolerance = 5000;
        }

        // Add selector outbound (manual selection)
        var outSelector = new Outbound4Sbox
        {
            type = "selector",
            tag = baseTagName,
            outbounds = JsonUtils.DeepCopy(proxyTags),
            interrupt_exist_connections = false,
        };
        outSelector.outbounds.Insert(0, outUrltest.tag);

        return [outSelector, outUrltest];
    }

    private List<BaseServer4Sbox> BuildOutboundsList(string baseTagName = Global.ProxyTag)
    {
        var nodes = new List<ProfileItem>();
        foreach (var nodeId in Utils.String2List(_node.GetProtocolExtra().ChildItems) ?? [])
        {
            if (context.AllProxiesMap.TryGetValue(nodeId, out var node))
            {
                nodes.Add(node);
            }
        }
        var resultOutbounds = new List<BaseServer4Sbox>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var currentTag = $"{baseTagName}-{i + 1}-{node.Remarks}";

            if (nodes.Count == 1)
            {
                currentTag = baseTagName;
            }

            if (node.ConfigType.IsGroupType())
            {
                var childProfiles = new CoreConfigSingboxService(context with { Node = node, }).BuildGroupProxyOutbounds(currentTag);
                resultOutbounds.AddRange(childProfiles);
                continue;
            }
            var outbound = new CoreConfigSingboxService(context with { Node = node, }).BuildProxyOutbound();
            outbound.tag = currentTag;
            resultOutbounds.Add(outbound);
        }
        return resultOutbounds;
    }

    private List<BaseServer4Sbox> BuildChainOutboundsList(string baseTagName = Global.ProxyTag)
    {
        var nodes = new List<ProfileItem>();
        foreach (var nodeId in Utils.String2List(_node.GetProtocolExtra().ChildItems) ?? [])
        {
            if (context.AllProxiesMap.TryGetValue(nodeId, out var node))
            {
                nodes.Add(node);
            }
        }
        // Based on actual network flow instead of data packets
        var nodesReverse = nodes.AsEnumerable().Reverse().ToList();
        var resultOutbounds = new List<BaseServer4Sbox>();
        for (var i = 0; i < nodesReverse.Count; i++)
        {
            var node = nodesReverse[i];
            var currentTag = i == 0 ? baseTagName : $"chain-{baseTagName}-{i}-{node.Remarks}";
            var dialerProxyTag = i != nodesReverse.Count - 1 ? $"chain-{baseTagName}-{i + 1}-{nodesReverse[i + 1].Remarks}" : null;
            if (node.ConfigType.IsGroupType())
            {
                var childProfiles = new CoreConfigSingboxService(context with { Node = node, }).BuildGroupProxyOutbounds(currentTag);
                if (!dialerProxyTag.IsNullOrEmpty())
                {
                    var chainEndNodes =
                        childProfiles.Where(n => n?.detour.IsNullOrEmpty() ?? true);
                    foreach (var chainEndNode in chainEndNodes)
                    {
                        chainEndNode.detour = dialerProxyTag;
                    }
                }
                if (i != 0)
                {
                    var chainStartNodes = childProfiles.Where(n => n.tag.StartsWith(currentTag)).ToList();
                    if (chainStartNodes.Count == 1)
                    {
                        foreach (var existedChainEndNode in resultOutbounds.Where(n => n.detour == currentTag))
                        {
                            existedChainEndNode.detour = chainStartNodes.First().tag;
                        }
                    }
                    else if (chainStartNodes.Count > 1)
                    {
                        var existedChainNodes = CloneOutbounds(resultOutbounds);
                        resultOutbounds.Clear();
                        var j = 0;
                        foreach (var chainStartNode in chainStartNodes)
                        {
                            var existedChainNodesClone = CloneOutbounds(existedChainNodes);
                            foreach (var existedChainNode in existedChainNodesClone)
                            {
                                var cloneTag = $"{existedChainNode.tag}-clone-{j + 1}";
                                existedChainNode.tag = cloneTag;
                            }
                            for (var k = 0; k < existedChainNodesClone.Count; k++)
                            {
                                var existedChainNode = existedChainNodesClone[k];
                                var previousDialerProxyTag = existedChainNode.detour;
                                var nextTag = k + 1 < existedChainNodesClone.Count
                                    ? existedChainNodesClone[k + 1].tag
                                    : chainStartNode.tag;
                                existedChainNode.detour = (previousDialerProxyTag == currentTag)
                                    ? chainStartNode.tag
                                    : nextTag;
                                resultOutbounds.Add(existedChainNode);
                            }
                            j++;
                        }
                    }
                }
                resultOutbounds.AddRange(childProfiles);
                continue;
            }
            var outbound = new CoreConfigSingboxService(context with { Node = node, }).BuildProxyOutbound();

            outbound.tag = currentTag;

            if (!dialerProxyTag.IsNullOrEmpty())
            {
                outbound.detour = dialerProxyTag;
            }

            resultOutbounds.Add(outbound);
        }
        return resultOutbounds;
    }

    private static List<BaseServer4Sbox> CloneOutbounds(List<BaseServer4Sbox> source)
    {
        if (source is null || source.Count == 0)
        {
            return [];
        }

        var result = new List<BaseServer4Sbox>(source.Count);
        foreach (var item in source)
        {
            BaseServer4Sbox? clone = null;
            if (item is Outbound4Sbox outbound)
            {
                clone = JsonUtils.DeepCopy(outbound);
            }
            else if (item is Endpoints4Sbox endpoint)
            {
                clone = JsonUtils.DeepCopy(endpoint);
            }
            if (clone is not null)
            {
                result.Add(clone);
            }
        }
        return result;
    }

    private static void FillRangeProxy(List<BaseServer4Sbox> servers, SingboxConfig singboxConfig, bool prepend = true)
    {
        try
        {
            if (servers is null || servers.Count <= 0)
            {
                return;
            }
            var outbounds = servers.OfType<Outbound4Sbox>().ToList();
            var endpoints = servers.OfType<Endpoints4Sbox>().ToList();
            singboxConfig.endpoints ??= [];
            if (prepend)
            {
                singboxConfig.outbounds.InsertRange(0, outbounds);
                singboxConfig.endpoints.InsertRange(0, endpoints);
            }
            else
            {
                singboxConfig.outbounds.AddRange(outbounds);
                singboxConfig.endpoints.AddRange(endpoints);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private static (Ech4Sbox? ech, Server4Sbox? dnsServer) ParseEchParam(string? echConfig)
    {
        if (echConfig.IsNullOrEmpty())
        {
            return (null, null);
        }
        if (!echConfig.Contains("://"))
        {
            return (new Ech4Sbox()
            {
                enabled = true,
                config = [$"-----BEGIN ECH CONFIGS-----\n" +
                          $"{echConfig}\n" +
                          $"-----END ECH CONFIGS-----"],
            }, null);
        }
        var idx = echConfig.IndexOf('+');
        var queryServerName = idx > 0 ? echConfig[..idx] : null;
        var echDnsServer = idx > 0 ? echConfig[(idx + 1)..] : echConfig;
        return (new Ech4Sbox()
        {
            enabled = true,
            query_server_name = queryServerName,
        }, ParseDnsAddress(echDnsServer));
    }
}
