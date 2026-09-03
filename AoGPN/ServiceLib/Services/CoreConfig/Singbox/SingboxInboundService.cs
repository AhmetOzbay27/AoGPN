namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void GenInbounds()
    {
        try
        {
            var listen = "0.0.0.0";
            var listenPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            // When this sing-box instance's node IS the local SOCKS bridge
            // (the pre-socks helper: 127.0.0.1:10808), do NOT add a local mixed
            // listener on that same port — it would conflict with the main
            // core's listener and create a loop. The helper is a pure
            // TUN → socks-outbound bridge and needs no inbound of its own.
            //
            // For a real server node (pure sing-box TUN / GPN main config) the
            // mixed inbound IS kept alongside TUN: it gives the SOCKS5 health
            // probe a listener and provides a fallback path if the TUN adapter
            // ever fails to come up.
            var isUsingLocalMixedPort = _node.Address == Global.Loopback && _node.Port == listenPort;
            _coreConfig.inbounds = [];

            if (!isUsingLocalMixedPort)
            {
                var inbound = new Inbound4Sbox()
                {
                    type = nameof(EInboundProtocol.mixed),
                    tag = nameof(EInboundProtocol.socks),
                    listen = Global.Loopback,
                };
                _coreConfig.inbounds.Add(inbound);

                inbound.listen_port = listenPort;

                if (_config.Inbound.First().SecondLocalPortEnabled)
                {
                    var inbound2 = BuildInbound(inbound, EInboundProtocol.socks2, true);
                    _coreConfig.inbounds.Add(inbound2);
                }

                if (_config.Inbound.First().AllowLANConn)
                {
                    if (_config.Inbound.First().NewPort4LAN)
                    {
                        var inbound3 = BuildInbound(inbound, EInboundProtocol.socks3, true);
                        inbound3.listen = listen;
                        _coreConfig.inbounds.Add(inbound3);

                        //auth
                        if (_config.Inbound.First().User.IsNotEmpty() && _config.Inbound.First().Pass.IsNotEmpty())
                        {
                            inbound3.users = new() { new() { username = _config.Inbound.First().User, password = _config.Inbound.First().Pass } };
                        }
                    }
                    else
                    {
                        inbound.listen = listen;
                    }
                }
            }

            if (context.IsTunEnabled)
            {
                if (_config.TunModeItem.Mtu <= 0)
                {
                    _config.TunModeItem.Mtu = Global.TunMtus.First();
                }
                if (_config.TunModeItem.Stack.IsNullOrEmpty())
                {
                    _config.TunModeItem.Stack = Global.TunStacks.First();
                }

                var tunInbound = JsonUtils.Deserialize<Inbound4Sbox>(EmbedUtils.GetEmbedText(Global.TunSingboxInboundFileName)) ?? new Inbound4Sbox { };
                tunInbound.interface_name = context.IsMacOS ? $"utun{new Random().Next(99)}" : Global.SingboxTunInterfaceName;
                tunInbound.mtu = _config.TunModeItem.Mtu;
                tunInbound.auto_route = _config.TunModeItem.AutoRoute;
                tunInbound.strict_route = _config.TunModeItem.StrictRoute;
                // VMware and some Hyper-V guests cannot create gvisor-based TUN
                // adapters (stack=system or mixed). Force the Windows Filtering
                // Platform (WFP) stack which works everywhere, including VMs.
                tunInbound.stack = Utils.IsWindows() ? "system" : (_config.TunModeItem.Stack.IsNullOrEmpty() ? Global.TunStacks.First() : _config.TunModeItem.Stack);
                DiagLog.Write($"TUN_STACK resolved: requested={_config.TunModeItem.Stack} actual={tunInbound.stack}");

                var address = _config.TunModeItem.IPv4Address.NullIfEmpty() ?? Global.TunIPv4Address.First();
                tunInbound.address = [address];
                // Always assign an IPv6 address to the TUN interface regardless of
                // the user's EnableIPv6Address toggle. Without IPv6 on the TUN adapter,
                // Chrome/Edge receive real AAAA records, connect via the native physical
                // IPv6 interface, and completely bypass the TUN — process_name rules
                // never see the traffic and it leaks to the real ISP.
                var address6 = _config.TunModeItem.IPv6Address.NullIfEmpty() ?? Global.TunIPv6Address.First();
                tunInbound.address.Add(address6);
                tunInbound.route_exclude_address = _config.TunModeItem.RouteExcludeAddress;

                // Aggressive sniffing — hijacks DoH / hardcoded DNS at the TUN level.
                // sing-box 1.11.0 deprecated inbound sniff fields and 1.13.0 removed
                // them (FATAL: "legacy inbound fields are deprecated in sing-box 1.11.0
                // and removed in sing-box 1.13.0"). Sniffing now lives in the route
                // action emitted by SingboxRoutingService: { action = "sniff" }.
                // (The 1.13 sniff route action has no override_destination option.)

                Logging.SaveLog("[SingboxInbound] TUN sniff moved to route action" +
                    (_config.SimpleDNSItem?.FakeIP == true ? " (FakeIP active)" : ""));
                DiagLog.Write($"TUN_SNIFF configured: route action enabled=true stack={tunInbound.stack} mtu={tunInbound.mtu} auto_route={tunInbound.auto_route} strict_route={tunInbound.strict_route} ipv6={_config.TunModeItem.EnableIPv6Address}");

                _coreConfig.inbounds.Add(tunInbound);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private Inbound4Sbox BuildInbound(Inbound4Sbox inItem, EInboundProtocol protocol, bool bSocks)
    {
        var inbound = JsonUtils.DeepCopy(inItem);
        inbound.tag = protocol.ToString();
        inbound.listen_port = inItem.listen_port + (int)protocol;
        inbound.type = nameof(EInboundProtocol.mixed);
        return inbound;
    }
}
