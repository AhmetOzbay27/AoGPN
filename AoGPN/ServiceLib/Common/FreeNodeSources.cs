namespace ServiceLib.Common;

/// <summary>
/// Curated catalog of trusted, publicly available free v2ray/Xray node
/// subscription URLs and configuration repositories.  Each entry is a URL
/// that can be fed directly into the subscription handler.
/// </summary>
public static class FreeNodeSources
{
    public sealed record Source(string Name, string Url, string Category)
    {
        public string Flag => GeoIpLookupService.CountryFlag(CountryCode);

        private string? _countryCode;

        public string CountryCode
        {
            get
            {
                if (_countryCode is null)
                {
                    _countryCode = TryExtractCountryCode(Name);
                }
                return _countryCode;
            }
        }

        private static string TryExtractCountryCode(string name)
        {
            // Common patterns in source names
            var lower = name.ToLowerInvariant();
            return lower switch
            {
                var s when s.Contains("iran") || s.Contains("🇮🇷") || s.Contains("tehran") => "IR",
                var s when s.Contains("germany") || s.Contains("🇩🇪") || s.Contains("deutsch") => "DE",
                var s when s.Contains("netherlands") || s.Contains("🇳🇱") || s.Contains("amsterdam") => "NL",
                var s when s.Contains("usa") || s.Contains("united states") || s.Contains("🇺🇸") => "US",
                var s when s.Contains("uk") || s.Contains("united kingdom") || s.Contains("🇬🇧") || s.Contains("london") => "GB",
                var s when s.Contains("france") || s.Contains("🇫🇷") || s.Contains("paris") => "FR",
                var s when s.Contains("japan") || s.Contains("🇯🇵") || s.Contains("tokyo") => "JP",
                var s when s.Contains("singapore") || s.Contains("🇸🇬") => "SG",
                var s when s.Contains("canada") || s.Contains("🇨🇦") => "CA",
                var s when s.Contains("turkey") || s.Contains("🇹🇷") || s.Contains("türkiye") || s.Contains("istanbul") => "TR",
                var s when s.Contains("russia") || s.Contains("🇷🇺") || s.Contains("moscow") => "RU",
                var s when s.Contains("hong kong") || s.Contains("🇭🇰") => "HK",
                var s when s.Contains("taiwan") || s.Contains("🇹🇼") => "TW",
                var s when s.Contains("korea") || s.Contains("🇰🇷") || s.Contains("seoul") => "KR",
                var s when s.Contains("india") || s.Contains("🇮🇳") => "IN",
                var s when s.Contains("brazil") || s.Contains("🇧🇷") => "BR",
                var s when s.Contains("australia") || s.Contains("🇦🇺") => "AU",
                var s when s.Contains("vietnam") || s.Contains("🇻🇳") => "VN",
                var s when s.Contains("indonesia") || s.Contains("🇮🇩") => "ID",
                var s when s.Contains("sweden") || s.Contains("🇸🇪") => "SE",
                var s when s.Contains("switzerland") || s.Contains("🇨🇭") => "CH",
                var s when s.Contains("finland") || s.Contains("🇫🇮") => "FI",
                _ => string.Empty,
            };
        }
    }

    public static readonly IReadOnlyList<Source> All =
    [
        // ------------------------------------------------------------------
        //  GitHub repositories — curated, frequently updated
        // ------------------------------------------------------------------
        new("MatinGhanbari · All Configs",
            "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/all_sub.txt",
            "GitHub"),

        new("MatinGhanbari · Super Sub",
            "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/super-sub.txt",
            "GitHub"),

        new("MatinGhanbari · Splitted (mixed)",
            "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/splitted.txt",
            "GitHub"),

        new("MatinGhanbari · Trojan",
            "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/trojan.txt",
            "GitHub"),

        new("MatinGhanbari · Shadowsocks",
            "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/ss.txt",
            "GitHub"),

        new("MatinGhanbari · VLESS Reality",
            "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/reality.txt",
            "GitHub"),

        new("MatinGhanbari · Hysteria2",
            "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/hysteria2.txt",
            "GitHub"),

        new("MatinGhanbari · TUIC",
            "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/tuic.txt",
            "GitHub"),

        new("mahdibland · V2Ray Collector",
            "https://raw.githubusercontent.com/mahdibland/V2RayCollector/master/sub/sub_merge_base64.txt",
            "GitHub"),

        new("mahdibland · Shadowsocks",
            "https://raw.githubusercontent.com/mahdibland/ShadowsocksAggregator/master/sub/splitted/ss.txt",
            "GitHub"),

        new("mahdibland · Trojan",
            "https://raw.githubusercontent.com/mahdibland/ShadowsocksAggregator/master/sub/splitted/trojan.txt",
            "GitHub"),

        new("mahdibland · VLESS Reality",
            "https://raw.githubusercontent.com/mahdibland/ShadowsocksAggregator/master/sub/splitted/vless.txt",
            "GitHub"),

        new("barry-far · V2ray Sub",
            "https://raw.githubusercontent.com/barry-far/V2ray-Configs/main/Sub1.txt",
            "GitHub"),

        new("barry-far · Splitted",
            "https://raw.githubusercontent.com/barry-far/V2ray-Configs/main/Splitted-By-Protocol/vless.txt",
            "GitHub"),

        new("yebekhe · Telegram Aggregator",
            "https://raw.githubusercontent.com/yebekhe/TelegramV2rayCollector/main/sub/base64/mix",
            "GitHub"),

        new("yebekhe · Reality",
            "https://raw.githubusercontent.com/yebekhe/TelegramV2rayCollector/main/sub/splitted/reality",
            "GitHub"),

        new("yebekhe · Hysteria2",
            "https://raw.githubusercontent.com/yebekhe/TelegramV2rayCollector/main/sub/splitted/hysteria2",
            "GitHub"),

        new("yebekhe · TUIC",
            "https://raw.githubusercontent.com/yebekhe/TelegramV2rayCollector/main/sub/splitted/tuic",
            "GitHub"),

        new("yebekhe · VLESS",
            "https://raw.githubusercontent.com/yebekhe/TelegramV2rayCollector/main/sub/splitted/vless",
            "GitHub"),

        new("yebekhe · VMess",
            "https://raw.githubusercontent.com/yebekhe/TelegramV2rayCollector/main/sub/splitted/vmess",
            "GitHub"),

        new("yebekhe · Trojan",
            "https://raw.githubusercontent.com/yebekhe/TelegramV2rayCollector/main/sub/splitted/trojan",
            "GitHub"),

        new("yebekhe · Shadowsocks",
            "https://raw.githubusercontent.com/yebekhe/TelegramV2rayCollector/main/sub/splitted/ss",
            "GitHub"),

        new("Epodonios · Bulk VLESS",
            "https://raw.githubusercontent.com/Epodonios/v2ray-configs/main/Config%20list4.txt",
            "GitHub"),

        new("sashalsk · V2Ray Config",
            "https://raw.githubusercontent.com/sashalsk/V2Ray/main/V2Config",
            "GitHub"),

        new("sashalsk · V2Ray Sub",
            "https://raw.githubusercontent.com/sashalsk/V2Ray/main/V2Sub",
            "GitHub"),

        new("Pawdroid · Free V2Ray",
            "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub",
            "GitHub"),

        new("ts-sf · Flying Fish",
            "https://raw.githubusercontent.com/ts-sf/flying-fish/master/sub/clash.yaml",
            "GitHub"),

        new("rtwo2 · FastNodes Top 1000",
            "https://raw.githubusercontent.com/rtwo2/FastNodes/main/sub/top.txt",
            "GitHub"),

        new("rtwo2 · FastNodes Xray Verified",
            "https://raw.githubusercontent.com/rtwo2/FastNodes/main/sub/verified.txt",
            "GitHub"),

        new("rtwo2 · FastNodes WireGuard",
            "https://raw.githubusercontent.com/rtwo2/FastNodes/main/sub/protocols/wireguard.txt",
            "GitHub"),

        new("Au1rxx · Free VPN Subscriptions (base64)",
            "https://github.com/Au1rxx/free-vpn-subscriptions/raw/main/output/v2ray-base64.txt",
            "GitHub"),

        new("Au1rxx · Free VPN Subscriptions (Clash)",
            "https://github.com/Au1rxx/free-vpn-subscriptions/raw/main/output/clash.yaml",
            "GitHub"),

        // ------------------------------------------------------------------
        //  Telegram-channel aggregated sources
        // ------------------------------------------------------------------
        new("AZNIX · All Servers",
            "https://raw.githubusercontent.com/AzadNetCH/Clash/main/AzNixCH.yaml",
            "Telegram"),

        new("MrPooya · V2Ray Clash",
            "https://raw.githubusercontent.com/MrPooyaX/V2rayClients/main/clash.yml",
            "Telegram"),

        new("MrPooya · V2Ray Base64",
            "https://raw.githubusercontent.com/MrPooyaX/V2rayClients/main/base64.txt",
            "Telegram"),

        new("Iam54 · Configs",
            "https://raw.githubusercontent.com/IranianCypherpunks/sub/main/configs.txt",
            "Telegram"),

        new("BiYue · V2Ray Sub",
            "https://raw.githubusercontent.com/biyue/OneDrive/master/v2ray/sub.txt",
            "Telegram"),

        new("Snoyl1st · Sub",
            "https://raw.githubusercontent.com/snoyl1st/sub/main/neko",
            "Telegram"),

        new("LemonCandy · V2Ray Proxies",
            "https://raw.githubusercontent.com/learnetproxies/proxies/main/proxies.txt",
            "Telegram"),

        // ------------------------------------------------------------------
        //  Direct-served subscription endpoints (self-hosted, free)
        // ------------------------------------------------------------------
        new("v2rayfree · US",
            "https://proxies.bihamta.com/sub?type=vmess&country=US",
            "Web"),

        new("v2rayfree · DE",
            "https://proxies.bihamta.com/sub?type=vmess&country=DE",
            "Web"),

        new("v2rayfree · Mix",
            "https://proxies.bihamta.com/sub?type=vmess",
            "Web"),
    ];
}