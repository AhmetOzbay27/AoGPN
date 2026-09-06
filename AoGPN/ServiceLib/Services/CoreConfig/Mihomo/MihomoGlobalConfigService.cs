namespace ServiceLib.Services.CoreConfig.Mihomo;

using System.Net;
using System.Net.Sockets;
using ServiceLib.Common;
using ServiceLib.Models.Entities;

/// <summary>
/// Global VPN (TUN veya Proxy) için mihomo YAML üreticisi.
///
/// Sing-box kaldırıldıktan sonra Global modun tek çekirdeği mihomo'dur: seçili
/// profil (VLESS/VMess/Trojan/Shadowsocks/Hysteria2/TUIC/SOCKS/HTTP) tek bir
/// mihomo proxy'sine çevrilir ve split-tunnel kuralları YERİNE tüm trafiği o
/// proxy'ye yönlendiren tek <c>MATCH,&lt;proxy&gt;</c> kuralı üretilir. TUN açıkken
/// mihomo kendi Wintun adaptörünü kurar (auto-route); kapalıyken yalnızca
/// mixed-port (HTTP+SOCKS5 — Proxy transportu) dinlenir. Bu sınıf saf (pure)
/// kalır — çalışma zamanı değerleri <see cref="GpnMihomoOptions"/> ile verilir.
/// </summary>
public static class MihomoGlobalConfigService
{
    /// <summary>Global moddaki tek proxy adı — kural hedefleri buraya işaret eder.</summary>
    public const string GlobalProxyName = "global-proxy";

    public static string GenerateGlobalYaml(ProfileItem node, GpnMihomoOptions options, bool tunEnabled)
    {
        var proxy = BuildProxy(node);
        var root = new Dictionary<string, object?>
        {
            ["mode"] = "rule",
            ["ipv6"] = false,
            ["allow-lan"] = false,
            ["log-level"] = options.LogLevel,
        };

        if (options.LogFilePath.IsNotEmpty())
        {
            root["log-file"] = options.LogFilePath;
        }
        if (options.MixedPort > 0)
        {
            root["mixed-port"] = options.MixedPort;
        }
        if (options.ExternalControllerPort > 0)
        {
            root["external-controller"] = $"{Global.Loopback}:{options.ExternalControllerPort}";
        }
        if (options.InterfaceName.IsNotEmpty())
        {
            root["interface-name"] = options.InterfaceName;
        }

        if (tunEnabled)
        {
            root["tun"] = new Dictionary<string, object?>
            {
                ["enable"] = true,
                ["stack"] = options.TunStack,
                ["device"] = options.TunDevice,
                ["auto-route"] = options.AutoRoute,
                ["auto-detect-interface"] = false,
                ["mtu"] = options.Mtu is > 0 ? options.Mtu.Value : Global.GpnRecommendedMtu,
            };

            // TUN içinden geçen DNS sorgularını yakala (hijack) — fake-ip ile
            // yanıtlanır, ISP'ye domain sızıntısı olmaz. Proxy-only modda DNS
            // modülü kapalıdır (sistem çözümleyicisi + uzak çözüm yeterli).
            var ns = options.DnsNameservers.Count > 0 ? options.DnsNameservers : new[] { "1.1.1.1", "8.8.8.8" };
            var defaultNs = options.DnsDefaultNameservers.Count > 0
                ? options.DnsDefaultNameservers
                : ns.Where(IsPlainIpAddress).ToList();
            if (defaultNs.Count == 0)
            {
                defaultNs = ["1.1.1.1", "8.8.8.8"];
            }
            root["dns"] = new Dictionary<string, object?>
            {
                ["enable"] = true,
                ["ipv6"] = false,
                ["enhanced-mode"] = options.DnsEnhancedMode,
                ["fake-ip-range"] = options.FakeIpRange,
                ["default-nameserver"] = new List<string>(defaultNs),
                ["nameserver"] = new List<string>(ns),
            };
        }
        else
        {
            root["dns"] = new Dictionary<string, object?> { ["enable"] = false };
            // Koruma: mihomo config'i TUN kapalıyken (Proxy transportu / TUN-yok
            // fallback) da tun bloğunu taşır — yalnızca enable:false ile. Böylece
            // çekirdek her iki modda da aynı şemayı görür ve config'te "tun:"
            // her zaman mevcuttur (akış testlerinin sözleşmesi).
            root["tun"] = new Dictionary<string, object?>
            {
                ["enable"] = false,
                ["device"] = options.TunDevice,
            };
        }

        root["proxies"] = new List<object> { proxy };
        // Global VPN: split-tunnel kuralı YOKTUR — tek MATCH satırı tüm trafiği
        // seçili profile yönlendirir (kullanıcının istediği "basit global kural").
        root["rules"] = new List<string> { $"MATCH,{GlobalProxyName}" };

        var yaml = YamlUtils.ToYaml(root);
        if (yaml.IsNullOrEmpty())
        {
            return string.Empty;
        }
        var header = new List<string>
        {
            "# AoGPN Global VPN — mihomo yapılandırması (otomatik üretildi)",
            $"# Sunucu: {node.Remarks} ({node.Address}:{node.Port})",
            "# Kural: MATCH -> proxy (tüm trafik seçili profile gider)",
            string.Empty,
        };
        return string.Join(Environment.NewLine, header) + yaml;
    }

    /// <summary>
    /// ProfileItem'ı tek bir mihomo proxy bloğuna çevirir. Desteklenen tipler
    /// <see cref="Global.MihomoSupportConfigType"/> ile aynıdır; desteklenmeyen
    /// tip (ör. Anytls/Naive — mihomo'da yok) geçersiz type ile sonuçlanmaz,
    /// çağıran zaten MihomoSupportConfigType ile filtrelemiştir.
    /// </summary>
    private static Dictionary<string, object?> BuildProxy(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var transport = node.GetTransportExtra();
        var security = node.StreamSecurity ?? string.Empty;
        var isTls = string.Equals(security, Global.StreamSecurity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(security, Global.StreamSecurityReality, StringComparison.OrdinalIgnoreCase);
        var isReality = string.Equals(security, Global.StreamSecurityReality, StringComparison.OrdinalIgnoreCase);

        var proxy = new Dictionary<string, object?>
        {
            ["name"] = GlobalProxyName,
            ["server"] = node.Address,
            ["port"] = node.Port,
        };

        switch (node.ConfigType)
        {
            case EConfigType.VLESS:
                proxy["type"] = "vless";
                proxy["uuid"] = node.Id;
                proxy["udp"] = true;
                proxy["network"] = ResolveMihomoNetwork(node.Network, transport);
                proxy["flow"] = (extra.Flow ?? string.Empty).IsNotEmpty() ? extra.Flow : null;
                if (isReality)
                {
                    proxy["tls"] = true;
                    proxy["servername"] = node.Sni;
                    proxy["client-fingerprint"] = node.Fingerprint;
                    var realityOpts = new Dictionary<string, object?> { ["public-key"] = node.PublicKey };
                    if (node.ShortId.IsNotEmpty())
                    {
                        realityOpts["short-id"] = node.ShortId;
                    }
                    proxy["reality-opts"] = realityOpts;
                }
                else if (isTls)
                {
                    proxy["tls"] = true;
                    proxy["servername"] = node.Sni;
                    proxy["client-fingerprint"] = node.Fingerprint;
                    proxy["skip-cert-verify"] = node.GetAllowInsecure();
                    if (node.Alpn.IsNotEmpty())
                    {
                        proxy["alpn"] = node.Alpn.Split(',').Select(a => a.Trim()).Where(a => a.IsNotEmpty()).ToList();
                    }
                }
                break;

            case EConfigType.VMess:
                proxy["type"] = "vmess";
                proxy["uuid"] = node.Id;
                proxy["alterId"] = int.TryParse(extra.AlterId, out var alterId) ? alterId : 0;
                proxy["cipher"] = extra.VmessSecurity ?? "auto";
                proxy["udp"] = true;
                proxy["network"] = ResolveMihomoNetwork(node.Network, transport);
                ApplyTls(proxy, node, isTls);
                break;

            case EConfigType.Trojan:
                proxy["type"] = "trojan";
                proxy["password"] = node.Password;
                proxy["udp"] = true;
                proxy["network"] = ResolveMihomoNetwork(node.Network, transport);
                ApplyTls(proxy, node, isTls);
                break;

            case EConfigType.Shadowsocks:
                proxy["type"] = "ss";
                proxy["cipher"] = extra.SsMethod ?? node.Security ?? "aes-256-gcm";
                proxy["password"] = node.Password;
                proxy["udp"] = true;
                break;

            case EConfigType.Hysteria2:
                proxy["type"] = "hysteria2";
                proxy["password"] = node.Password;
                proxy["sni"] = node.Sni;
                proxy["skip-cert-verify"] = node.GetAllowInsecure();
                if (extra.UpMbps is > 0)
                {
                    proxy["up"] = extra.UpMbps.Value;
                }
                if (extra.DownMbps is > 0)
                {
                    proxy["down"] = extra.DownMbps.Value;
                }
                if (extra.Ports.IsNotEmpty())
                {
                    proxy["ports"] = extra.Ports;
                }
                if (extra.HopInterval.IsNotEmpty())
                {
                    proxy["hop-interval"] = extra.HopInterval;
                }
                if (extra.SalamanderPass.IsNotEmpty())
                {
                    proxy["obfs"] = "salamander";
                    proxy["obfs-password"] = extra.SalamanderPass;
                }
                break;

            case EConfigType.TUIC:
                proxy["type"] = "tuic";
                proxy["uuid"] = node.Id;
                proxy["password"] = node.Password;
                proxy["congestion-controller"] = extra.CongestionControl ?? "bbr";
                proxy["udp-relay-mode"] = "native";
                if (node.Alpn.IsNotEmpty())
                {
                    proxy["alpn"] = node.Alpn.Split(',').Select(a => a.Trim()).Where(a => a.IsNotEmpty()).ToList();
                }
                break;

            case EConfigType.SOCKS:
                proxy["type"] = "socks5";
                if (node.Username.IsNotEmpty())
                {
                    proxy["username"] = node.Username;
                    proxy["password"] = node.Password;
                }
                proxy["udp"] = true;
                break;

            case EConfigType.HTTP:
                proxy["type"] = "http";
                if (node.Username.IsNotEmpty())
                {
                    proxy["username"] = node.Username;
                    proxy["password"] = node.Password;
                }
                break;

            default:
                // Bilinmeyen tip — mihomo config'i geçersiz proxy ile reddeder;
                // çağıran MihomoSupportConfigType filtresinden geçemeyen tipleri
                // zaten buraya göndermez.
                proxy["type"] = "vless";
                proxy["uuid"] = node.Id;
                proxy["network"] = "tcp";
                break;
        }

        ApplyTransportOpts(proxy, node.Network, transport);
        return proxy;
    }

    private static void ApplyTls(Dictionary<string, object?> proxy, ProfileItem node, bool isTls)
    {
        if (!isTls)
        {
            return;
        }
        proxy["tls"] = true;
        proxy["servername"] = node.Sni;
        proxy["skip-cert-verify"] = node.GetAllowInsecure();
        if (node.Fingerprint.IsNotEmpty())
        {
            proxy["client-fingerprint"] = node.Fingerprint;
        }
    }

    private static string ResolveMihomoNetwork(string? network, TransportExtraItem transport)
    {
        var normalized = (network ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "ws" or "websocket" => "ws",
            "grpc" => "grpc",
            "h2" or "http" => "http",
            _ => "tcp",
        };
    }

    /// <summary>ws/grpc taşıma bloklarını ekler (mihomo ws-opts / grpc-opts sözleşmesi).</summary>
    private static void ApplyTransportOpts(Dictionary<string, object?> proxy, string? network, TransportExtraItem transport)
    {
        var normalized = ResolveMihomoNetwork(network, transport);
        if (normalized == "ws")
        {
            var wsOpts = new Dictionary<string, object?>();
            if (transport.Path.IsNotEmpty())
            {
                wsOpts["path"] = transport.Path;
            }
            if (transport.Host.IsNotEmpty())
            {
                wsOpts["headers"] = new Dictionary<string, object?> { ["Host"] = transport.Host };
            }
            proxy["ws-opts"] = wsOpts;
        }
        else if (normalized == "grpc" && transport.GrpcServiceName.IsNotEmpty())
        {
            proxy["grpc-opts"] = new Dictionary<string, object?>
            {
                ["grpc-service-name"] = transport.GrpcServiceName,
            };
        }
    }

    private static bool IsPlainIpAddress(string server)
        => IPAddress.TryParse(server.Trim(), out _);
}