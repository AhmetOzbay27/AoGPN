using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.Services.CoreConfig.Mihomo;

public class GpnMihomoConfigServiceTests
{
    private static readonly GpnServerProfile Almanya = new(
        "de", "Almanya", "130.61.223.36", 51820,
        "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
        "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
        "10.66.66.2/24", 1420, "1.1.1.1", 25, true);

    private static RulesItem Rule(string id, string outbound, List<string>? process = null,
        List<string>? domain = null, List<string>? ip = null, string? port = null) => new()
    {
        Id = id,
        OutboundTag = outbound,
        Process = process,
        Domain = domain,
        Ip = ip,
        Port = port,
        Enabled = true,
    };

    private static readonly GpnServerProfile Italya = new(
        "it", "İtalya", "92.4.220.236", 51820,
        "iK25iMzwDmLjq2PzX7w6yYdLk9nTgQ4vRcF1sB8hUaA=",
        "nL4xOq2WcVb9sRt7YhGf1pDk8uJm0zXeN6SaIcQ5wE=",
        "10.66.66.2/24", 1420, "1.1.1.1", 25, true);

    private static string Generate(GpnServerProfile server, IReadOnlyList<RulesItem> rules,
        GpnMihomoOptions? options = null, VlessProfileItem? bypass = null,
        IReadOnlyList<LauncherBypassItem>? launcherBypasses = null)
        => new GpnMihomoConfigService().GenerateYaml(server, rules, options ?? new GpnMihomoOptions(), bypass, launcherBypasses);

    private static string GenerateMulti(
        IReadOnlyList<GpnServerProfile>? nodes,
        GpnServerProfile active,
        IReadOnlyList<RulesItem> rules,
        GpnMihomoOptions? options = null,
        VlessProfileItem? bypass = null,
        IReadOnlyList<LauncherBypassItem>? launcherBypasses = null)
        => new GpnMihomoConfigService().GenerateYaml(nodes, active, rules, options ?? new GpnMihomoOptions(), bypass, launcherBypasses);

    [Fact]
    public void MultiNode_EmitsEveryNodePlusSelectGroup_AndRulesTargetGroup()
    {
        var nodes = new[] { Italya, Almanya };
        var yaml = GenerateMulti(nodes, Almanya, new[]
        {
            Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]),
            Rule("2", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
        });

        // Her aday ayrı bir wireguard outbound olur.
        yaml.Should().Contain("name: wg-it");
        yaml.Should().Contain("name: wg-de");
        yaml.Should().Contain("server: 92.4.220.236");
        yaml.Should().Contain("server: 130.61.223.36");

        // Kesintisiz geçişin kalbi: tüm düğümleri içeren select grubu.
        yaml.Should().Contain("name: GPN-Nodes");
        yaml.Should().Contain("type: select");
        // Aktif düğüm ilk üyeyse mihomo config açılışında onu varsayılan seçer.
        var groupIdx = yaml.IndexOf("name: GPN-Nodes", StringComparison.Ordinal);
        var wgDeIdx = yaml.IndexOf("name: wg-de", StringComparison.Ordinal);
        var wgItIdx = yaml.IndexOf("name: wg-it", StringComparison.Ordinal);
        groupIdx.Should().BeGreaterThan(0);
        // proxy-groups listesinde üyeler aktif (wg-de) önce gelir.
        var membersIdx = yaml.IndexOf("proxies:", wgDeIdx > 0 ? wgDeIdx : 0, StringComparison.Ordinal);
        yaml.Substring(groupIdx).Should().Contain("wg-de");
        yaml.Substring(groupIdx).Should().Contain("wg-it");
        _ = wgItIdx; _ = membersIdx;

        // vpn kuralları artık tek düğüme değil gruba işaret eder — seçim değişince
        // tüm kurallar yeni düğümü izler.
        yaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,GPN-Nodes");
        // warp zinciri de grubun üzerinden geçer (düğüm değişimi zinciri de taşır).
        yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,warp-socks");
        yaml.Should().Contain("dialer-proxy: GPN-Nodes");
        // Tek-düğüm legacy hedefi üretilmemeli.
        yaml.Should().NotContain("PROCESS-NAME,EscapeFromTarkov.exe,wg-de");
    }

    [Fact]
    public void MultiNode_NoWarp_StillEmitsGroupAndDirectCatchAll()
    {
        var nodes = new[] { Italya, Almanya };
        var yaml = GenerateMulti(nodes, Italya, new[]
        {
            Rule("1", Global.DirectTag, port: "0-65535"),
        });

        yaml.Should().Contain("name: GPN-Nodes");
        yaml.Should().Contain("name: wg-it");
        yaml.Should().Contain("name: wg-de");
        yaml.Should().NotContain("warp-socks");
        yaml.Should().Contain("MATCH,DIRECT");
    }

    [Fact]
    public void MultiNode_ActiveFirst_EvenWhenListedLater()
    {
        // Aktif düğüm listede sonda olsa bile grup üyeleri aktif düğümle başlar
        // (mihomo select varsayılanı = ilk üye → açılışta doğru düğüm seçili olur).
        var nodes = new[] { Italya, Almanya };
        var yaml = GenerateMulti(nodes, Italya, new[] { Rule("1", Global.ProxyTag, port: "0-65535") });

        var membersIdx = yaml.IndexOf("proxies:", StringComparison.Ordinal);
        var afterMembers = yaml[membersIdx..];
        var firstWg = afterMembers.IndexOf("wg-", StringComparison.Ordinal);
        afterMembers.Substring(firstWg, 5).Should().Be("wg-it");
    }

    [Fact]
    public void MultiNode_DeduplicatesByServerId()
    {
        var nodes = new[] { Italya, Almanya, Italya };
        var yaml = GenerateMulti(nodes, Almanya, new[] { Rule("1", Global.ProxyTag, port: "0-65535") });

        // wg-it outbound iki kez üretilmemeli (proxies bölümü tekrar sayılır).
        var proxiesSection = yaml[..yaml.IndexOf("proxy-groups", StringComparison.Ordinal)];
        proxiesSection.Split("name: wg-it", StringSplitOptions.None).Length.Should().Be(2);
    }

    [Fact]
    public void SingleNodeList_KeepsLegacyDirectTargets()
    {
        // Tek aday + aktif aynıysa legacy biçim korunur: grup yok, kurallar wg-<id>'ye.
        var yaml = GenerateMulti(new[] { Almanya }, Almanya, new[]
        {
            Rule("1", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
            Rule("2", Global.WarpTag, process: ["BsGLauncher.exe"]),
        });

        yaml.Should().NotContain("proxy-groups");
        yaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,wg-de");
        yaml.Should().Contain("dialer-proxy: wg-de");
    }

    [Fact]
    public void LogFilePath_WhenSet_EmitsLogFileKey()
    {
        var yaml = Generate(Almanya, [], new GpnMihomoOptions
        {
            LogFilePath = "C:/Programlar/VPN/AoGPN/guiLogs/ao_mihomo_2026-09-02.log",
        });

        yaml.Should().Contain("log-file:");
        yaml.Should().Contain("ao_mihomo_2026-09-02.log");
    }

    [Fact]
    public void LogFilePath_Empty_OmitsLogFileKey()
    {
        var yaml = Generate(Almanya, []);

        yaml.Should().NotContain("log-file:");
    }

    [Fact]
    public void WarpRule_EmitsWarpProxyWithDialerProxyToWg()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]),
            Rule("2", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
        });

        yaml.Should().Contain("name: warp-socks");
        yaml.Should().Contain("server: 10.66.66.1");
        yaml.Should().Contain("port: 40000");
        yaml.Should().Contain("dialer-proxy: wg-de");
        yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,warp-socks");
        yaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,wg-de");
        yaml.Should().Contain("name: wg-de");
        yaml.Should().Contain("server: 130.61.223.36");
    }

    [Fact]
    public void NoWarpRule_OmitsWarpProxy()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
        });

        yaml.Should().NotContain("warp-socks");
        yaml.Should().NotContain("dialer-proxy");
        yaml.Should().Contain("wg-de");
    }

    [Fact]
    public void BlockAndDirectTags_MapToRejectAndDirect()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.BlockTag, process: ["bad.exe"]),
            Rule("2", Global.DirectTag, ip: ["1.1.1.1"]),
            Rule("3", Global.DirectTag, port: "0-65535"),
        });

        yaml.Should().Contain("PROCESS-NAME,bad.exe,REJECT");
        yaml.Should().Contain("IP-CIDR,1.1.1.1/32,DIRECT");
        yaml.Should().Contain("MATCH,DIRECT");
    }

    [Fact]
    public void DomainAndIpv6_UseSuffixAndCidr6()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.ProxyTag, domain: ["profile.tarkov.com"]),
            Rule("2", Global.ProxyTag, ip: ["2606:4700::1"]),
        });

        yaml.Should().Contain("DOMAIN-SUFFIX,profile.tarkov.com,wg-de");
        yaml.Should().Contain("IP-CIDR6,2606:4700::1/128,wg-de");
    }

    [Fact]
    public void PortOnlyEntry_EmitsDstPortRule()
    {
        var yaml = Generate(Almanya, new[] { Rule("1", Global.ProxyTag, port: "27015-27050") });

        yaml.Should().Contain("DST-PORT,27015-27050,wg-de");
    }

    [Fact]
    public void DnsEnabled_EmitsFakeIpHijackBlock()
    {
        var yaml = Generate(Almanya,
            new[] { Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]) },
            new GpnMihomoOptions { DnsEnabled = true });

        yaml.Should().Contain("enable: true");
        yaml.Should().Contain("enhanced-mode: fake-ip");
        yaml.Should().Contain("fake-ip-range: 198.18.0.1/16");
        yaml.Should().Contain("default-nameserver:");
        yaml.Should().Contain("nameserver:");
        yaml.Should().Contain("1.1.1.1");
        yaml.Should().Contain("8.8.8.8");
    }

    [Fact]
    public void Dns_UserRemoteAndBootstrapNameservers_ArePreserved()
    {
        var yaml = Generate(Almanya,
            new[] { Rule("1", Global.ProxyTag, port: "0-65535") },
            new GpnMihomoOptions
            {
                DnsEnabled = true,
                DnsNameservers = ["https://cloudflare-dns.com/dns-query", "9.9.9.9"],
                DnsDefaultNameservers = ["9.9.9.9"],
            });

        yaml.Should().Contain("https://cloudflare-dns.com/dns-query");
        yaml.Should().Contain("9.9.9.9");
        // default-nameserver, nameserver listesinden AYRILIR (saf IP bootstrap'u)…
        yaml.Should().Contain("default-nameserver");
    }

    [Fact]
    public void Dns_DoHOnlyNameservers_FallBackDefaultToPlainIps()
    {
        // nameserver'da ÖZEL (saf IP) bootstrap yoksa üretici güvenlik ağına düşer:
        // mihomo default-nameserver'sız DNS modülünü başlatmaz.
        var yaml = Generate(Almanya,
            new[] { Rule("1", Global.ProxyTag, port: "0-65535") },
            new GpnMihomoOptions
            {
                DnsEnabled = true,
                DnsNameservers = ["https://quic.dns.nextdns.io"],
                DnsDefaultNameservers = [],
            });

        yaml.Should().Contain("https://quic.dns.nextdns.io");
        yaml.Should().Contain("default-nameserver:");
        yaml.Should().Contain("1.1.1.1");
        yaml.Should().Contain("8.8.8.8");
    }

    [Fact]
    public void ParseDnsServers_SplitsAndFiltersBySupportedSchemes()
    {
        var parsed = GpnMihomoConfigService.ParseDnsServers(
            "1.1.1.1; https://cloudflare-dns.com/dns-query, localhost, tls://1.1.1.1, 8.8.8.8",
            ["9.9.9.9"]);

        parsed.Should().BeEquivalentTo(
            ["1.1.1.1", "https://cloudflare-dns.com/dns-query", "tls://1.1.1.1", "8.8.8.8"]);

        // localhost/local (sing-box'a özgü) atlanır; boş girdi fallback'i döner.
        GpnMihomoConfigService.ParseDnsServers("localhost, local", ["9.9.9.9"]).Should().BeEquivalentTo(["9.9.9.9"]);
    }

    [Fact]
    public void IsPlainIpAddress_DetectsV4AndV6()
    {
        GpnMihomoConfigService.IsPlainIpAddress("1.1.1.1").Should().BeTrue();
        GpnMihomoConfigService.IsPlainIpAddress("2606:4700:4700::1111").Should().BeTrue();
        GpnMihomoConfigService.IsPlainIpAddress("https://1.1.1.1/dns-query").Should().BeFalse();
    }

    [Fact]
    public void DnsDisabled_EmitsMinimalBlock()
    {
        var yaml = Generate(Almanya,
            new[] { Rule("1", Global.ProxyTag, port: "0-65535") },
            new GpnMihomoOptions());

        yaml.Should().Contain("enable: false");
        yaml.Should().NotContain("fake-ip-range");
        yaml.Should().NotContain("enhanced-mode");
    }

    [Fact]
    public void DomainSuffixRule_UnderWarp_TargetsWarpSocks()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, domain: ["tarkov.com"]),
        });

        yaml.Should().Contain("DOMAIN-SUFFIX,tarkov.com,warp-socks");
    }

    // ── Çift Bağlantı (Bölünmüş Tünelleme) — WG + VLESS/Reality bypass ───────

    // Public key / short-id düz metin tutulur (YAML tırnak kaçışı yok) — üretim
    // değerleri base64 olabilir; Mihomo reality-opts'u burada değil, canlı config'te
    // doğrulanır (saf üretici testi string çıktısıyla sınırlıdır).
    private static readonly VlessProfileItem LauncherBypass = new(
        Name: "Afrika VLESS",
        ServerAddress: "197.210.54.32",
        ServerPort: 443,
        Uuid: "7b6a8e31-8f2a-4b3c-9d4e-5f60718293a4",
        PublicKey: "REALITY_PUBLIC_KEY_PLACEHOLDER",
        ShortId: "1a2b3c4d",
        ServerName: "www.microsoft.com",
        Flow: "xtls-rprx-vision",
        Fingerprint: "chrome");

    [Fact]
    public void DualMode_EmitsVlessRealityProxyRightBelowWireGuard_AndMapsWarpToVless()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]),
            Rule("2", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
        }, bypass: LauncherBypass);

        // İkincil VLESS/Reality düğümü WireGuard tanımından SONRA yazılır.
        var wgIdx = yaml.IndexOf("name: wg-de", StringComparison.Ordinal);
        var vlessIdx = yaml.IndexOf("name: vless-launcher", StringComparison.Ordinal);
        wgIdx.Should().BeGreaterThanOrEqualTo(0);
        vlessIdx.Should().BeGreaterThan(wgIdx, "vless-launcher, wg-de tanımının hemen altına eklenmeli");

        // Mihomo VLESS/Reality bloğu standart alanlarla üretilir.
        yaml.Should().Contain("type: vless");
        yaml.Should().Contain("server: 197.210.54.32");
        yaml.Should().Contain("port: 443");
        yaml.Should().Contain("uuid: 7b6a8e31-8f2a-4b3c-9d4e-5f60718293a4");
        yaml.Should().Contain("network: tcp");
        yaml.Should().Contain("udp: false");
        yaml.Should().Contain("tls: true");
        yaml.Should().Contain("flow: xtls-rprx-vision");
        yaml.Should().Contain("servername: www.microsoft.com");
        yaml.Should().Contain("client-fingerprint: chrome");
        yaml.Should().Contain("reality-opts:");
        yaml.Should().Contain("public-key: REALITY_PUBLIC_KEY_PLACEHOLDER");
        yaml.Should().Contain("short-id: 1a2b3c4d");

        // "warp" egress vless-launcher'a gider; WARP SOCKS5 zinciri üretilmez.
        yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,vless-launcher");
        yaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,wg-de");
        yaml.Should().NotContain("warp-socks");
        yaml.Should().NotContain("dialer-proxy");
    }

    [Fact]
    public void DualMode_MultiNode_KeepsGroupAndAddsVless()
    {
        var nodes = new[] { Italya, Almanya };
        var yaml = GenerateMulti(nodes, Almanya, new[]
        {
            Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]),
            Rule("2", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
        }, bypass: LauncherBypass);

        // Ana WG düğümleri + ikincil VLESS birlikte; oyun kuralları gruba gider.
        yaml.Should().Contain("name: wg-it");
        yaml.Should().Contain("name: wg-de");
        yaml.Should().Contain("name: GPN-Nodes");
        yaml.Should().Contain("name: vless-launcher");
        yaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,GPN-Nodes");
        yaml.Should().NotContain("PROCESS-NAME,EscapeFromTarkov.exe,wg-de");
        yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,vless-launcher");
        yaml.Should().NotContain("warp-socks");
        yaml.Should().NotContain("dialer-proxy");
    }

    [Fact]
    public void BsgDomains_AlwaysInjectAtTop_ToLauncherEgress()
    {
        // Çift Bağlantı: iki BSG domain kuralı da kural listesinin en üstünde
        // vless-launcher'a gider — process kurallarından ve MATCH'tan önce eşleşir.
        var dualYaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]),
            Rule("2", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
        }, bypass: LauncherBypass);

        dualYaml.Should().Contain("DOMAIN-SUFFIX,escapefromtarkov.com,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,battlestategames.com,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,prod.escapefromtarkov.com,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,launcher.escapefromtarkov.com,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,gw-pvp.escapefromtarkov.com,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,www.escapefromtarkov.com,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,tarkov.com,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,escapefromtarkov.ru,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,launcher.escapefromtarkov.ru,vless-launcher");
        dualYaml.Should().Contain("DOMAIN-SUFFIX,profile.tarkov.com,vless-launcher");
        var dEftIdx = dualYaml.IndexOf("DOMAIN-SUFFIX,escapefromtarkov.com,vless-launcher", StringComparison.Ordinal);
        var dBsgIdx = dualYaml.IndexOf("DOMAIN-SUFFIX,battlestategames.com,vless-launcher", StringComparison.Ordinal);
        var dTarkovIdx = dualYaml.IndexOf("DOMAIN-SUFFIX,tarkov.com,vless-launcher", StringComparison.Ordinal);
        var dRuIdx = dualYaml.IndexOf("DOMAIN-SUFFIX,escapefromtarkov.ru,vless-launcher", StringComparison.Ordinal);
        var dProdIdx = dualYaml.IndexOf("DOMAIN-SUFFIX,prod.escapefromtarkov.com,vless-launcher", StringComparison.Ordinal);
        var dLauncherRuIdx = dualYaml.IndexOf("DOMAIN-SUFFIX,launcher.escapefromtarkov.ru,vless-launcher", StringComparison.Ordinal);
        var dWwwIdx = dualYaml.IndexOf("DOMAIN-SUFFIX,www.escapefromtarkov.com,vless-launcher", StringComparison.Ordinal);
        var dProfileIdx = dualYaml.IndexOf("DOMAIN-SUFFIX,profile.tarkov.com,vless-launcher", StringComparison.Ordinal);
        var dGameIdx = dualYaml.IndexOf("PROCESS-NAME,EscapeFromTarkov.exe,wg-de", StringComparison.Ordinal);
        var dMatchIdx = dualYaml.IndexOf("MATCH,", StringComparison.Ordinal);
        dEftIdx.Should().BeGreaterThanOrEqualTo(0, "escapefromtarkov.com kuralı üretilmeli");
        dBsgIdx.Should().BeGreaterThan(dEftIdx);
        dTarkovIdx.Should().BeGreaterThan(dBsgIdx, "tarkov.com ailesi apexi de en üstte");
        dRuIdx.Should().BeGreaterThan(dTarkovIdx, "RU launcher ailesi apexi (escapefromtarkov.ru) da en üstte");
        dProdIdx.Should().BeGreaterThan(dRuIdx, "CefSharp alt domainleri de en üstte, sırayla");
        dLauncherRuIdx.Should().BeGreaterThan(dProdIdx, "launcher.escapefromtarkov.ru (canlı kayıtta görülen RU aynası) listede");
        dWwwIdx.Should().BeGreaterThan(dLauncherRuIdx, "www de dahil tüm alt domainler listelenmeli");
        dProfileIdx.Should().BeGreaterThan(dWwwIdx, "profile.tarkov.com (WAF korumalı auth host) da listede");
        dGameIdx.Should().BeGreaterThan(dProfileIdx, "domain kuralları process kurallarından ÖNCE eşleşir");
        dMatchIdx.Should().BeGreaterThan(dGameIdx, "MATCH en sonda kalır");

        // Legacy (warp kuralı var): aynı domain kuralları warp-socks'a gider, en üstte.
        var legacyYaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]),
            Rule("2", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
        });
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,escapefromtarkov.com,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,battlestategames.com,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,prod.escapefromtarkov.com,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,launcher.escapefromtarkov.com,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,gw-pvp.escapefromtarkov.com,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,www.escapefromtarkov.com,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,tarkov.com,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,escapefromtarkov.ru,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,launcher.escapefromtarkov.ru,warp-socks");
        legacyYaml.Should().Contain("DOMAIN-SUFFIX,profile.tarkov.com,warp-socks");
        var lEftIdx = legacyYaml.IndexOf("DOMAIN-SUFFIX,escapefromtarkov.com,warp-socks", StringComparison.Ordinal);
        var lGameIdx = legacyYaml.IndexOf("PROCESS-NAME,EscapeFromTarkov.exe,wg-de", StringComparison.Ordinal);
        lGameIdx.Should().BeGreaterThan(lEftIdx, "legacy'de de domain kuralları en üstte");

        // Legacy'de warp kuralı YOKSA hedef (warp-socks) da üretilmez → domain
        // kuralları yazılmaz (tanımsız outbound'a kural mihomo'yu reddeder).
        var plainYaml = Generate(Almanya, new[]
        {
            Rule("1", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
        });
        plainYaml.Should().NotContain("DOMAIN-SUFFIX,escapefromtarkov.com");
        plainYaml.Should().NotContain("DOMAIN-SUFFIX,battlestategames.com");
        plainYaml.Should().NotContain("warp-socks");
    }

    [Fact]
    public void DualMode_EmptyShortId_OmitsShortIdKey()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]),
        }, bypass: LauncherBypass with { ShortId = "" });

        yaml.Should().Contain("reality-opts:");
        yaml.Should().Contain("public-key: REALITY_PUBLIC_KEY_PLACEHOLDER");
        yaml.Should().NotContain("short-id:");
    }

    [Fact]
    public void DualMode_DomainWarpRule_TargetsVless()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, domain: ["profile.tarkov.com"]),
        }, bypass: LauncherBypass);

        yaml.Should().Contain("DOMAIN-SUFFIX,profile.tarkov.com,vless-launcher");
        yaml.Should().NotContain("warp-socks");
    }

    [Fact]
    public void DualMode_Superset_WarpEntriesAndPreservedRules_TargetVless()
    {
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: false,
            new[]
            {
                AppEntry("EscapeFromTarkov.exe", "vpn"),
                AppEntry("BsGLauncher.exe", "warp"),
            });
        var preserved = new[]
        {
            Rule("u1", Global.WarpTag, process: ["userTool.exe"]),
        };
        var yaml = GenerateSuperset(Almanya, policy, preserved, bypass: LauncherBypass);

        // Superset'te warp-socks yerine vless-launcher tanımlanır ve üye olarak girer.
        yaml.Should().Contain("name: vless-launcher");
        yaml.Should().NotContain("warp-socks");
        yaml.Should().NotContain("dialer-proxy");

        // vpn girişi tünel grubuyla başlar; warp girişi vless-launcher ile başlar.
        GroupMembers(yaml, "ao-0").Should().Equal(
            GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect,
            GpnSoftRouting.ClashReject, GpnMihomoConfigService.BypassProxyName);
        GroupMembers(yaml, "ao-1").Should().Equal(
            GpnMihomoConfigService.BypassProxyName, GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashDirect, GpnSoftRouting.ClashReject);
        yaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,ao-0");
        yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,ao-1");

        // Kullanıcının korunan warp kuralı da vless-launcher'a gider.
        yaml.Should().Contain("PROCESS-NAME,userTool.exe,vless-launcher");
    }

    [Fact]
    public void SupersetLegacy_BsgDomainsRouteThroughLauncherGroup_WithDirectFallback()
    {
        // Superset-legacy (warp-socks): varsayılan BSG domain satırları GPN-LAUNCHER
        // seçim grubuna gider — GpnBypassEgressController faulted iken bu grubu canlı
        // (restart'sız) DIRECT'e çeker, sağlıklıyken warp-socks'a döner.
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: false,
            new[]
            {
                AppEntry("EscapeFromTarkov.exe", "vpn"),
                AppEntry("BsGLauncher.exe", "warp"),
            });
        var yaml = GenerateSuperset(Almanya, policy);

        // Domain satırları GPN-LAUNCHER grubuna gider (eski warp-socks doğrudan hedefi değil).
        yaml.Should().Contain("DOMAIN-SUFFIX,escapefromtarkov.com,GPN-LAUNCHER");
        yaml.Should().Contain("DOMAIN-SUFFIX,battlestategames.com,GPN-LAUNCHER");
        yaml.Should().Contain("DOMAIN-SUFFIX,tarkov.com,GPN-LAUNCHER");
        yaml.Should().Contain("DOMAIN-SUFFIX,escapefromtarkov.ru,GPN-LAUNCHER");
        yaml.Should().Contain("DOMAIN-SUFFIX,profile.tarkov.com,GPN-LAUNCHER");
        yaml.Should().NotContain("DOMAIN-SUFFIX,escapefromtarkov.com,warp-socks");

        // GPN-LAUNCHER grubu: warp-socks varsayılan seçim, DIRECT degrade yedeği.
        yaml.Should().Contain("name: GPN-LAUNCHER");
        GroupMembers(yaml, "GPN-LAUNCHER").Should().Equal(
            GpnMihomoConfigService.WarpProxyName, GpnSoftRouting.ClashDirect);

        // warp rotalı giriş hâlâ ao-1 grubuna gider (değişmedi); zincir duruyor.
        yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,ao-1");
        yaml.Should().Contain("name: warp-socks");

        // First-match-wins: BSG domain satırları process satırlarından ve MATCH'tan önce.
        var bsgIdx = yaml.IndexOf("DOMAIN-SUFFIX,escapefromtarkov.com,GPN-LAUNCHER", StringComparison.Ordinal);
        var gameIdx = yaml.IndexOf("PROCESS-NAME,EscapeFromTarkov.exe,ao-0", StringComparison.Ordinal);
        bsgIdx.Should().BeGreaterThanOrEqualTo(0);
        gameIdx.Should().BeGreaterThan(bsgIdx);
    }

    [Fact]
    public void CatchAllIsAlwaysAppended_AsSafetyNet()
    {
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, process: ["BsGLauncher.exe"]),
        });

        yaml.Should().Contain("MATCH,DIRECT");
    }

    [Fact]
    public void Mtu_ClampedToGpnRecommended_WhenProfileLarger()
    {
        var yaml = Generate(Almanya, new[] { Rule("1", Global.ProxyTag, port: "0-65535") });

        yaml.Should().Contain($"mtu: {Global.GpnRecommendedMtu}");
    }

    [Fact]
    public void DeriveGateway_FromClientAddress()
    {
        GpnMihomoConfigService.DeriveWireGuardGateway("10.66.66.2/24").Should().Be("10.66.66.1");
        GpnMihomoConfigService.DeriveWireGuardGateway("172.16.5.9/16").Should().Be("172.16.0.1");
        GpnMihomoConfigService.DeriveWireGuardGateway("").Should().Be("10.66.66.1");
    }

    [Fact]
    public void GeoipAndGeosite_Prefixes_AreFilteredOut()
    {
        // sing-box geoip:/geosite: prefix'leri mihomo tarafindan taninmaz;
        // bunlar RuleSet icine sirayla girer ve Onceki faza OZEL debug hatasi uretir.
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.DirectTag, ip: ["geoip:private"]),
            Rule("2", Global.DirectTag, domain: ["geosite:private"]),
            Rule("3", Global.ProxyTag, process: ["curl.exe"]),
            Rule("4", Global.DirectTag, port: "0-65535"),
        });

        // sing-box specific ifadeler YAML icine hic girmemeli.
        yaml.Should().NotContain("geoip:");
        yaml.Should().NotContain("geosite:");
        // Normal kurallar hala mevcut olmali.
        yaml.Should().Contain("PROCESS-NAME,curl.exe,wg-de");
        yaml.Should().Contain("MATCH,DIRECT");
    }

    [Fact]
    public void DomainRule_ListedBeforeProcessRule_WinsInYaml()
    {
        // Manuel olarak domain (WARP) kuralı bir süreç (VPN) kuralından önce
        // listelenirse mihomo YAML'inde de aynı sırayı korur — first-match-wins.
        // (Preset yok; kullanıcı kendi rotalarını manuel ekler.)
        var yaml = Generate(Almanya, new[]
        {
            Rule("1", Global.WarpTag, domain: ["escapefromtarkov.com"]),
            Rule("2", Global.ProxyTag, process: ["EscapeFromTarkov.exe"]),
            Rule("3", Global.WarpTag, process: ["BsGLauncher.exe"]),
        });

        var domainIdx = yaml.IndexOf("DOMAIN-SUFFIX,escapefromtarkov.com,warp-socks", StringComparison.Ordinal);
        var gameIdx = yaml.IndexOf("PROCESS-NAME,EscapeFromTarkov.exe,wg-de", StringComparison.Ordinal);
        domainIdx.Should().BeGreaterThanOrEqualTo(0, "domain kuralı üretilmeli");
        gameIdx.Should().BeGreaterThanOrEqualTo(0, "oyun process kuralı üretilmeli");
        domainIdx.Should().BeLessThan(gameIdx,
            "önce listelenen domain kuralı process kuralından önce eşleşmeli");
    }

    // ── Superset (kesintisiz rota — GPN-MODE + ao-<i> grupları) biçimi ──────

    private static GpnSoftRoutingEntry AppEntry(string value, string action)
        => new("app", value, "", action);

    private static GpnSoftRoutingEntry DomainEntry(string value, string action)
        => new("domain", value, "", action);

    private static string GenerateSuperset(
        GpnServerProfile active,
        GpnSoftRoutingPolicy policy,
        IReadOnlyList<RulesItem>? preserved = null,
        IReadOnlyList<GpnServerProfile>? nodes = null,
        VlessProfileItem? bypass = null,
        IReadOnlyList<LauncherBypassItem>? launcherBypasses = null)
        => new GpnMihomoConfigService().GenerateYaml(nodes, active, policy, preserved ?? [], new GpnMihomoOptions(), bypass, launcherBypasses);

    /// <summary>YAML'deki bir select grubunun üye sırasını okur (blok stil).</summary>
    private static List<string> GroupMembers(string yaml, string groupName)
    {
        var members = new List<string>();
        var started = false;
        var inMembers = false;
        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0)
            {
                continue;
            }
            if (!started)
            {
                // Gruplar proxy-groups listesinde "- name: X" olarak başlar.
                started = t == $"name: {groupName}"
                    || t == $"- name: {groupName}";
                continue;
            }
            if (t.StartsWith("- name:", StringComparison.Ordinal))
            {
                return members; // sonraki grup/outbound başladı
            }
            if (inMembers)
            {
                if (t.StartsWith("- ", StringComparison.Ordinal))
                {
                    members.Add(t[2..].Trim());
                }
                else
                {
                    inMembers = false; // grubun sonraki anahtarı (ör. type)
                }
            }
            else if (t == "proxies:")
            {
                inMembers = true;
            }
        }
        return members;
    }

    [Fact]
    public void Superset_GameTunnel_EmitsModeAndPerAppGroups_WithPerRouteInitialSelection()
    {
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: false,
            new[]
            {
                AppEntry("chrome.exe", "vpn"),
                AppEntry("edge.exe", "direct"),
                AppEntry("blocked.exe", "block"),
                AppEntry("warped.exe", "warp"),
                DomainEntry("cdn.game.com", "vpn"),
            });
        var preserved = new[]
        {
            Rule("u1", Global.DirectTag, process: ["userTool.exe"]),
        };
        var yaml = GenerateSuperset(Almanya, policy, preserved);

        // Grup ailesi: düğüm grubu + mod (yakalayıcı) grubu + her giriş için ao-<i>.
        yaml.Should().Contain("name: GPN-Nodes");
        yaml.Should().Contain("name: GPN-MODE");
        yaml.Should().Contain("name: ao-0");
        yaml.Should().Contain("name: ao-4");
        // warp-socks her zaman tanımlanır (ao grupları üye olarak gösterebilir).
        yaml.Should().Contain("name: warp-socks");
        yaml.Should().Contain("dialer-proxy: GPN-Nodes");

        // Yakalayıcı (beyaz liste): DIRECT seçili başlar — unlisted doğrudan kalır.
        GroupMembers(yaml, GpnMihomoConfigService.NodesGroupName).Should().Equal("wg-de");
        GroupMembers(yaml, GpnSoftRouting.ModeGroupName).Should().Equal(
            GpnSoftRouting.ClashDirect, GpnMihomoConfigService.NodesGroupName);
        // Her giriş grubu, girişin rotasıyla başlar (config açılışta doğru rotada).
        GroupMembers(yaml, "ao-0").Should().Equal(
            GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect,
            GpnSoftRouting.ClashReject, GpnMihomoConfigService.WarpProxyName);
        GroupMembers(yaml, "ao-1").Should().Equal(
            GpnSoftRouting.ClashDirect, GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashReject, GpnMihomoConfigService.WarpProxyName);
        GroupMembers(yaml, "ao-2").Should().Equal(
            GpnSoftRouting.ClashReject, GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashDirect, GpnMihomoConfigService.WarpProxyName);
        GroupMembers(yaml, "ao-3").Should().Equal(
            GpnMihomoConfigService.WarpProxyName, GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ClashDirect, GpnSoftRouting.ClashReject);
        GroupMembers(yaml, "ao-4").Should().Equal(
            GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect,
            GpnSoftRouting.ClashReject, GpnMihomoConfigService.WarpProxyName);

        // Sabit kural satırları: her giriş kendi ao-<i> grubuna işaret eder.
        yaml.Should().Contain("PROCESS-NAME,chrome.exe,ao-0");
        yaml.Should().Contain("PROCESS-NAME,edge.exe,ao-1");
        yaml.Should().Contain("PROCESS-NAME,blocked.exe,ao-2");
        yaml.Should().Contain("PROCESS-NAME,warped.exe,ao-3");
        yaml.Should().Contain("DOMAIN-SUFFIX,cdn.game.com,ao-4");
        // Kurallar doğrudan tünele/değere değil gruplara gider (seçim değişince izler).
        yaml.Should().NotContain("PROCESS-NAME,chrome.exe,GPN-Nodes");

        // Sıra legacy ile aynı: giriş satırları → yakalayıcı (MATCH,GPN-MODE) →
        // kullanıcının korunan kuralları. Böylece her mod aynı trafik sonucunu verir.
        var lastEntryIdx = yaml.IndexOf("DOMAIN-SUFFIX,cdn.game.com,ao-4", StringComparison.Ordinal);
        var matchIdx = yaml.IndexOf("MATCH,GPN-MODE", StringComparison.Ordinal);
        var preservedIdx = yaml.IndexOf("PROCESS-NAME,userTool.exe,DIRECT", StringComparison.Ordinal);
        lastEntryIdx.Should().BeGreaterThanOrEqualTo(0);
        matchIdx.Should().BeGreaterThan(lastEntryIdx, "yakalayıcı giriş satırlarından sonra gelir");
        preservedIdx.Should().BeGreaterThan(matchIdx, "kullanıcı kuralları yakalayıcıdan sonra korunur");
    }

    [Fact]
    public void Superset_GlobalVpn_StartsWithEverythingTunneled()
    {
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Vpn,
            InvertManualRouting: false,
            new[] { AppEntry("chrome.exe", "vpn"), AppEntry("edge.exe", "direct") });
        var yaml = GenerateSuperset(Almanya, policy);

        // Yakalayıcı ve giriş gruplarının hepsi tünel grubuyla başlar (legacy Global:
        // girişler yok sayılır, her şey tünelden geçer).
        GroupMembers(yaml, GpnSoftRouting.ModeGroupName).Should().Equal(
            GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect);
        GroupMembers(yaml, "ao-0")[0].Should().Be(GpnMihomoConfigService.NodesGroupName);
        GroupMembers(yaml, "ao-1")[0].Should().Be(GpnMihomoConfigService.NodesGroupName);
        yaml.Should().Contain("MATCH,GPN-MODE");
        // Superset'te güvenlik ağı MATCH,DIRECT yerine gruba gider (asla legacy fallback).
        yaml.Should().NotContain("MATCH,DIRECT");
    }

    [Fact]
    public void Superset_Off_EverythingStartsDirect_TunnelIdleButAlive()
    {
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Off,
            InvertManualRouting: true, // Off'ta yönün önemi yok — her şey direct
            new[] { AppEntry("chrome.exe", "vpn"), AppEntry("edge.exe", "block") });
        var yaml = GenerateSuperset(Almanya, policy);

        GroupMembers(yaml, GpnSoftRouting.ModeGroupName).Should().Equal(
            GpnSoftRouting.ClashDirect, GpnMihomoConfigService.NodesGroupName);
        GroupMembers(yaml, "ao-0")[0].Should().Be(GpnSoftRouting.ClashDirect);
        GroupMembers(yaml, "ao-1")[0].Should().Be(GpnSoftRouting.ClashDirect);
        yaml.Should().Contain("MATCH,GPN-MODE");
    }

    [Fact]
    public void Superset_ManualBlacklist_InvertsInitialSelections()
    {
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: true,
            new[] { AppEntry("chrome.exe", "vpn"), AppEntry("edge.exe", "direct") });
        var yaml = GenerateSuperset(Almanya, policy);

        // Kara liste: yakalayıcı tünele gider; vpn girişi istisna = direct,
        // açık direct giriş tünele çevrilir.
        GroupMembers(yaml, GpnSoftRouting.ModeGroupName).Should().Equal(
            GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect);
        GroupMembers(yaml, "ao-0")[0].Should().Be(GpnSoftRouting.ClashDirect);
        GroupMembers(yaml, "ao-1")[0].Should().Be(GpnMihomoConfigService.NodesGroupName);
    }

    [Fact]
    public void Superset_WithNodeCandidates_StillTargetsGroups()
    {
        var nodes = new[] { Italya, Almanya };
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: false,
            new[] { AppEntry("chrome.exe", "vpn") });
        var yaml = GenerateSuperset(Almanya, policy, nodes: nodes);

        yaml.Should().Contain("name: wg-de");
        yaml.Should().Contain("name: wg-it");
        GroupMembers(yaml, GpnMihomoConfigService.NodesGroupName).Should().Equal("wg-de", "wg-it");
        yaml.Should().Contain("PROCESS-NAME,chrome.exe,ao-0");
        yaml.Should().Contain("MATCH,GPN-MODE");
        yaml.Should().Contain("dialer-proxy: GPN-Nodes");
    }

    [Fact]
    public void Superset_NoEntries_StillEmitsCatchAllGroupStructure()
    {
        var policy = new GpnSoftRoutingPolicy(GameTriggerModes.Manual, false, []);
        var yaml = GenerateSuperset(Almanya, policy);

        yaml.Should().Contain("name: GPN-MODE");
        yaml.Should().Contain("name: GPN-Nodes");
        yaml.Should().Contain("name: GPN-CHECK");
        yaml.Should().NotContain("name: ao-");
        yaml.Should().Contain("MATCH,GPN-MODE");
        yaml.Should().NotContain("MATCH,DIRECT");
    }

    // ── IP doğrulama (GPN-CHECK) satırları ──────────────────────────────────

    [Fact]
    public void Superset_EmitsIpCheckRulesPinnedToCheckGroup_BeforeCatchAll()
    {
        // Beyaz liste (yakalayıcı DIRECT): uygulamanın kendi api.ip.sb doğrulama
        // isteği giriş satırlarına/MATCH'e düşmeden GPN-CHECK'e gider — bağlıyken
        // tünel çıkışı ölçülür, "IP değişmedi" yanlış alarmı üretilmez.
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: false,
            new[] { AppEntry("chrome.exe", "vpn") });
        var yaml = GenerateSuperset(Almanya, policy);

        // Yerleşik host aileleri (ip.sb → api.ip.sb / api-ipv4/6.ip.sb; fallback'ler).
        yaml.Should().Contain("DOMAIN-SUFFIX,ip.sb,GPN-CHECK");
        yaml.Should().Contain("DOMAIN-SUFFIX,ipapi.is,GPN-CHECK");
        yaml.Should().Contain("DOMAIN-SUFFIX,ipinfo.io,GPN-CHECK");
        yaml.Should().Contain("DOMAIN-SUFFIX,ip-api.com,GPN-CHECK");
        yaml.Should().Contain("DOMAIN-SUFFIX,ipify.org,GPN-CHECK");
        // Satırlar yakalayıcıdan ÖNCE eşleşir (first-match-wins).
        yaml.IndexOf("DOMAIN-SUFFIX,ip.sb,GPN-CHECK", StringComparison.Ordinal)
            .Should().BeLessThan(yaml.IndexOf("MATCH,GPN-MODE", StringComparison.Ordinal),
                "IP doğrulama satırı yakalayıcıdan önce gelir");
        // GPN-CHECK grubu bağlı modda tünel grubuyla başlar (başlangıç seçimi).
        GroupMembers(yaml, GpnSoftRouting.CheckGroupName).Should().Equal(
            GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect);
        yaml.Should().NotContain("MATCH,DIRECT");
    }

    [Fact]
    public void Superset_UserConfiguredIpApiUrl_AddsExtraPinnedDomain()
    {
        // Ayarlar → IPAPIUrl kullanıcının kendi ucuysa o host da GPN-CHECK'e bağlanır.
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: false,
            new[] { AppEntry("chrome.exe", "vpn") },
            IpCheckExtraDomains: ["my-ip.example"]);
        var yaml = GenerateSuperset(Almanya, policy);

        yaml.Should().Contain("DOMAIN-SUFFIX,my-ip.example,GPN-CHECK");
        yaml.IndexOf("DOMAIN-SUFFIX,my-ip.example,GPN-CHECK", StringComparison.Ordinal)
            .Should().BeLessThan(yaml.IndexOf("MATCH,GPN-MODE", StringComparison.Ordinal));
    }

    [Fact]
    public void Superset_Off_CheckGroupDefaultsToDirect_IspBaselineStaysHonest()
    {
        // Off'ta üretim (kesintisiz tünel canlı, her şey direct): GPN-CHECK DIRECT ile
        // başlar — disconnected ölçümler tünel IP'sini ISP baz çizgisi olarak kaydetmez.
        var policy = new GpnSoftRoutingPolicy(GameTriggerModes.Off, false, []);
        var yaml = GenerateSuperset(Almanya, policy);

        yaml.Should().Contain("DOMAIN-SUFFIX,ip.sb,GPN-CHECK");
        GroupMembers(yaml, GpnSoftRouting.CheckGroupName).Should().Equal(
            GpnSoftRouting.ClashDirect, GpnMihomoConfigService.NodesGroupName);
    }

    [Fact]
    public void Superset_GlobalVpn_CheckGroupDefaultsToTunnel()
    {
        var policy = new GpnSoftRoutingPolicy(GameTriggerModes.Vpn, false, []);
        var yaml = GenerateSuperset(Almanya, policy);

        GroupMembers(yaml, GpnSoftRouting.CheckGroupName).Should().Equal(
            GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect);
    }

    // ── Launcher bypass genelleştirme (kullanıcı düzenlenebilir domain + egress) ──

    private static LauncherBypassItem Launcher(string name, string egress, params string[] domains)
        => new() { Name = name, Egress = egress, Domains = domains, Enabled = true };

    [Fact]
    public void LauncherBypasses_ResolvePerEgressTargets_InSupersetLegacy()
    {
        // Superset-legacy: warp → GPN-LAUNCHER grubu, direct → DIRECT, vless (düğüm
        // yok) → warp-socks zincirine düşer (needWarp superset'te her zaman açık).
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual, InvertManualRouting: false,
            new[] { AppEntry("EscapeFromTarkov.exe", "vpn") });
        var yaml = GenerateSuperset(Almanya, policy, launcherBypasses:
        [
            Launcher("BSG", GpnLauncherBypass.EgressWarp,
                "escapefromtarkov.com", "profile.tarkov.com"),
            Launcher("Epic", GpnLauncherBypass.EgressDirect, "epicgames.com", "unrealengine.com"),
            Launcher("Steam", GpnLauncherBypass.EgressVless, "steampowered.com"),
        ]);

        yaml.Should().Contain("DOMAIN-SUFFIX,escapefromtarkov.com,GPN-LAUNCHER");
        yaml.Should().Contain("DOMAIN-SUFFIX,profile.tarkov.com,GPN-LAUNCHER");
        yaml.Should().Contain("DOMAIN-SUFFIX,epicgames.com,DIRECT");
        yaml.Should().Contain("DOMAIN-SUFFIX,unrealengine.com,DIRECT");
        yaml.Should().Contain("DOMAIN-SUFFIX,steampowered.com,warp-socks");
        // Varsayılan BSG yerine özel liste verildi — başka BSG domaini üretilmez.
        yaml.Should().NotContain("DOMAIN-SUFFIX,battlestategames.com,");
        // Degrade grubu yalnızca warp egress'li launcher için üretilir.
        GroupMembers(yaml, GpnSoftRouting.LauncherGroupName).Should().Equal(
            GpnMihomoConfigService.WarpProxyName, GpnSoftRouting.ClashDirect);
    }

    [Fact]
    public void LauncherBypasses_DirectRowsEmitWithoutWarpBackend_WarpRowsSkipped()
    {
        // Legacy tek düğüm, warp kuralı yok, çift bağlantı yok: direct egress satırları
        // güvenle üretilir (DIRECT her zaman tanımlı); warp egress satırları tanımsız
        // hedef yüzünden atlanır (mihomo config'i tanımsız outbound'a kuralı reddeder).
        var yaml = Generate(Almanya, [], launcherBypasses:
        [
            Launcher("BSG", GpnLauncherBypass.EgressWarp, "escapefromtarkov.com"),
            Launcher("Epic", GpnLauncherBypass.EgressDirect, "epicgames.com"),
        ]);

        yaml.Should().Contain("DOMAIN-SUFFIX,epicgames.com,DIRECT");
        yaml.Should().NotContain("DOMAIN-SUFFIX,escapefromtarkov.com,");
    }

    [Fact]
    public void LauncherBypasses_VlessInDualMode_TargetsBypassProxy()
    {
        // Çift Bağlantı: warp ve vless egress'li launcher'ların tamamı vless-launcher'a
        // gider; GPN-LAUNCHER grubu üretilmez (izleyici o egress'i kapsamaz).
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual, InvertManualRouting: false,
            new[] { AppEntry("EscapeFromTarkov.exe", "vpn") });
        var yaml = GenerateSuperset(Almanya, policy, bypass: LauncherBypass, launcherBypasses:
        [
            Launcher("BSG", GpnLauncherBypass.EgressWarp, "escapefromtarkov.com"),
            Launcher("Epic", GpnLauncherBypass.EgressVless, "epicgames.com"),
        ]);

        yaml.Should().Contain("DOMAIN-SUFFIX,escapefromtarkov.com,vless-launcher");
        yaml.Should().Contain("DOMAIN-SUFFIX,epicgames.com,vless-launcher");
        yaml.Should().NotContain("name: GPN-LAUNCHER");
    }

    [Fact]
    public void LauncherBypasses_DisabledOrEmptyDomainLaunchers_EmitNothing()
    {
        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual, InvertManualRouting: false,
            new[] { AppEntry("EscapeFromTarkov.exe", "vpn") });
        var yaml = GenerateSuperset(Almanya, policy, launcherBypasses:
        [
            new LauncherBypassItem { Name = "Kapalı", Egress = GpnLauncherBypass.EgressDirect, Domains = ["kapali.com"], Enabled = false },
            new LauncherBypassItem { Name = "Boş", Egress = GpnLauncherBypass.EgressDirect, Domains = [], Enabled = true },
        ]);

        yaml.Should().NotContain("kapali.com");
        // Varsayılan BSG listesi de devre dışı — hiçbir launcher satırı üretilmez
        // (IP-check satırları gpn-check grubuna gider, launcher değildir).
        yaml.Should().NotContain("DOMAIN-SUFFIX,escapefromtarkov.com,");
        yaml.Should().NotContain("name: GPN-LAUNCHER");
    }
}
