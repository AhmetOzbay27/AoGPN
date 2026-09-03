using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Singbox;

/// <summary>
/// Validates configs produced by <see cref="CoreConfigSingboxService.GenerateClientConfigContent"/>
/// against the real sing-box binary via <c>sing-box check</c>. Schema mismatches (for
/// example the sing-box 1.13 removal of the legacy inbound <c>sniff</c> fields, which
/// previously killed the core at startup with a FATAL error) are caught here instead of
/// at app runtime. The test skips itself when no binary is present in the build output
/// (e.g. CI jobs that never downloaded the core assets).
/// </summary>
[Collection("SharedDatabase")]
public class SingBoxBinaryConfigValidationTests
{
    [Fact]
    public void BasicProxyConfig_IsAcceptedByRealSingBox()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);

        AssertAcceptedByRealSingBox(context, "basic-proxy");
    }

    [Fact]
    public void TunGpnConfig_IsAcceptedByRealSingBox()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box, remarks: "demo-node");
        var apps = new[]
        {
            new SplitTunnelAppItem { EntryType = "app", Value = "chrome.exe", Action = "vpn" },
            new SplitTunnelAppItem { EntryType = "app", Value = "msedge.exe", Action = "direct" },
        };
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: false);

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            RoutingItem = new RoutingItem
            {
                Id = "gpn-routing",
                Remarks = "gpn-managed-routing",
                RuleSet = JsonUtils.Serialize(managed, false),
                RuleNum = managed.Count,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };

        AssertAcceptedByRealSingBox(context, "tun-gpn");
    }

    [Fact]
    public void WireguardGpnConfig_WithAoGpnItalyServer_IsAcceptedByRealSingBox()
    {
        // Faz 1 hedef senaryosu: gerçek İtalya WireGuard sunucusu (endpoint +
        // sunucu genel anahtarı gerçek; istemci özel anahtarı şema doğrulaması
        // için yer tutucu) mevcut GPN process_name routing ile birlikte
        // sing-box config üretmeli ve üretilen config gerçek binary'den geçmeli.
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateItalyWireguardNode(ECoreType.sing_box, remarks: "AoGPN İtalya");
        var apps = new[]
        {
            new SplitTunnelAppItem { EntryType = "app", Value = "chrome.exe", Action = "vpn" },
            new SplitTunnelAppItem { EntryType = "app", Value = "msedge.exe", Action = "direct" },
        };
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: false);

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            RoutingItem = new RoutingItem
            {
                Id = "gpn-wg-routing",
                Remarks = "gpn-wireguard-routing",
                RuleSet = JsonUtils.Serialize(managed, false),
                RuleNum = managed.Count,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"config generation failed: {result.Msg}");
        result.Data.Should().NotBeNull();

        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var wgEndpoint = cfg.endpoints?.FirstOrDefault(e => e.type == "wireguard");
        wgEndpoint.Should().NotBeNull("GPN modunda WireGuard profili bir endpoint olarak üretilmeli");
        wgEndpoint!.system.Should().BeFalse("userspace endpoint, singbox_tun ile çakışmaz");
        wgEndpoint.mtu.Should().Be(1420);
        wgEndpoint.private_key.Should().NotBeNullOrEmpty();
        wgEndpoint.peers.Should().HaveCount(1);
        var peer = wgEndpoint.peers[0];
        peer.address.Should().Be("92.4.220.236");
        peer.port.Should().Be(51820);
        peer.public_key.Should().Be("5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=");
        peer.persistent_keepalive_interval.Should().Be(25, "NAT traversal için keepalive korunmalı");
        peer.allowed_ips.Should().Contain("0.0.0.0/0");

        AssertAcceptedByRealSingBox(result.Data?.ToString(), "wireguard-gpn-italy");
    }

    [Fact]
    public void WireguardGpnConfig_NormalizesEndpointAddressToHostPrefix()
    {
        // WARP regresyonu: gVisor netstack'i "10.66.66.2/24" arayüz CIDR'sini
        // doğrudan bağlı alt ağ sayar; 10.66.66.1 (sunucudaki WARP SOCKS5'i) gibi
        // alt ağ içi hedeflere dial ARP/komşu çözümü ister ve MAC'siz/ARP'siz WG
        // linkinde "no route to host" ile düşer (canlı ölçüm: tünel sağlamken tüm
        // warp dial'leri bu hatayla düşüyordu). Üretici arayüz adresini host
        // prefix'e (/32, /128) indirgemeli — ağ geçidi o zaman allowed_ips
        // 0.0.0.0/0 rotasıyla tünelin içinden hedeflenir.
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateItalyWireguardNode(ECoreType.sing_box, remarks: "AoGPN İtalya");
        // WgInterfaceAddress init-only'dir; JSON yoluyla yeni örnek kurulur.
        var extraJson = JsonUtils.Serialize(node.GetProtocolExtra(), false);
        extraJson = Regex.Replace(extraJson, "\"WgInterfaceAddress\"\\s*:\\s*\"[^\"]*\"", "\"WgInterfaceAddress\":\"10.66.66.2/24, fd00::2/64\"");
        node.SetProtocolExtra(JsonUtils.Deserialize<ProtocolExtraItem>(extraJson)!);

        var apps = new[]
        {
            new SplitTunnelAppItem { EntryType = "app", Value = "chrome.exe", Action = "vpn" },
        };
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: false);

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            RoutingItem = new RoutingItem
            {
                Id = "gpn-wg-hostprefix-routing",
                Remarks = "gpn-wg-hostprefix-routing",
                RuleSet = JsonUtils.Serialize(managed, false),
                RuleNum = managed.Count,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"config generation failed: {result.Msg}");
        result.Data.Should().NotBeNull();

        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var wgEndpoint = cfg.endpoints?.FirstOrDefault(e => e.type == "wireguard");
        wgEndpoint.Should().NotBeNull("GPN modunda WireGuard profili bir endpoint olarak üretilmeli");
        wgEndpoint!.address.Should().BeEquivalentTo(
            ["10.66.66.2/32", "fd00::2/128"],
            "gVisor alt ağ rotası WARP ağ geçidine (10.66.66.1) dial'i 'no route to host' ile düşürür — host prefix şart");

        AssertAcceptedByRealSingBox(context, "wireguard-gpn-hostprefix");
    }

    [Fact]
    public void TarkovGpnConfig_TunnelsLauncherGameAndBattlEye_ThroughProxy()
    {
        // Tarkov süreç seti (launcher + oyun + BattlEye) GPN beyaz listesinde per-app
        // aksiyonlarıyla: launcher (BsGLauncher.exe) WARP egress outbound'una,
        // oyun + BattlEye proxy (düşük ping) outbound'una gitmeli. WireGuard düğümünde
        // zincirli WARP socks outbound'u da üretilmeli (detour = proxy endpoint).
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateItalyWireguardNode(ECoreType.sing_box, remarks: "AoGPN İtalya");

        var apps = new[]
        {
            new SplitTunnelAppItem { EntryType = "app", Value = "BsGLauncher.exe", Action = "warp" },
            new SplitTunnelAppItem { EntryType = "app", Value = "EscapeFromTarkov.exe", Action = "vpn" },
            new SplitTunnelAppItem { EntryType = "app", Value = "EscapeFromTarkov_BE.exe", Action = "vpn" },
        };
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: false);
        managed.Should().HaveCount(4,
            "her süreç için birer kural + catch-all");

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            RoutingItem = new RoutingItem
            {
                Id = "gpn-tarkov-routing",
                Remarks = "gpn-tarkov-routing",
                RuleSet = JsonUtils.Serialize(managed, false),
                RuleNum = managed.Count,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"config generation failed: {result.Msg}");

        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        // Launcher → warp outbound (temiz egress), oyun + BattlEye → proxy (düşük ping).
        // (QUIC önleme kuralı — outbound'suz reject — yanlış eşleşmesin diye
        // outbound şartı da aranır.)
        foreach (var expected in new[] { "EscapeFromTarkov.exe", "EscapeFromTarkov_BE.exe" })
        {
            var rule = cfg.route.rules.FirstOrDefault(r =>
                r.outbound == Global.ProxyTag
                && r.process_name != null
                && r.process_name.Contains(expected));
            rule.Should().NotBeNull($"process_name proxy kuralı eksik: {expected}");
            rule!.process_name.Should().ContainSingle(expected,
                "her süreç kendi kuralında tek kez eşleşir");
        }

        var launcherRule = cfg.route.rules.FirstOrDefault(r =>
            r.outbound == Global.WarpTag
            && r.process_name != null
            && r.process_name.Contains("BsGLauncher.exe"));
        launcherRule.Should().NotBeNull("launcher warp outbound kuralı eksik");
        launcherRule!.process_name.Should().ContainSingle("BsGLauncher.exe");

        // WARP socks outbound: detour her zaman WG endpoint'ine gider (TUN
        // döngüsü değil — o yol canlıda TCP verisini düşürüyordu: connection
        // reset / i/o timeout). Endpoint'in route set'inde ağ geçidi /32 adresi
        // eklenmiş olmalı, aksi hâlde internal dial "no route to host" ile düşer
        // (yalıtılmış sing-box doğrulamasıyla kanıtlanan regresyon).
        var warp = cfg.outbounds.FirstOrDefault(o => o.tag == Global.WarpTag);
        warp.Should().NotBeNull("WireGuard düğümünde warp outbound üretilmeli");
        warp!.type.Should().Be("socks");
        warp.server.Should().Be("10.66.66.1", "WgInterfaceAddress 10.66.66.2/24'ten ağ geçidi türetilir");
        warp.server_port.Should().Be(Global.WarpSocksDefaultPort);
        warp.detour.Should().Be(Global.ProxyTag,
            "warp dial'i WG endpoint detour'undan geçmeli (TUN/OS döngüsü kullanılmaz)");

        var wgEndpoint = cfg.endpoints?.FirstOrDefault(e => e.tag == Global.ProxyTag && e.type == "wireguard");
        wgEndpoint.Should().NotBeNull("WG endpoint üretilmeli");
        wgEndpoint!.address.Should().Contain("10.66.66.1/32",
            "warp SOCKS ağ geçidi endpoint route set'inde olmalı — yoksa detour dial 'no route to host' ile düşer");

        // WARP ağ geçidinin TUN'dan geri alınan dial'i endpoint'e gitmeli ve kural
        // core-process protect (sing-box.exe → direct) kurallarından ÖNCE gelmeli.
        var warpSubnetRule = cfg.route.rules.FirstOrDefault(r =>
            r.ip_cidr != null && r.ip_cidr.Contains("10.66.66.0/24"));
        warpSubnetRule.Should().NotBeNull("10.66.66.0/24 → proxy ip_cidr kuralı eksik");
        warpSubnetRule!.outbound.Should().Be(Global.ProxyTag);
        var warpSubnetIndex = cfg.route.rules.IndexOf(warpSubnetRule);
        // Test ortamında çekirdek yolları bulunamadığında protect kuralı üretilmeyebilir;
        // üretildiğinde warpSubnet kuralı ondan ÖNCE eşleşmeli (bkz. GenRoutingGpn).
        var firstProcessDirectIndex = cfg.route.rules.FindIndex(r =>
            r.outbound == Global.DirectTag && r.process_path != null && r.process_path.Count > 0);
        if (firstProcessDirectIndex >= 0)
        {
            warpSubnetIndex.Should().BeLessThan(firstProcessDirectIndex,
                "warpSubnet kuralı core-process protect'tan önce eşleşmeli");
        }

        // Beyaz listede olmayan bir süreç tünellenmemeli (catch-all direct kalır).
        var unrelated = cfg.route.rules.FirstOrDefault(r =>
            r.process_name != null && r.process_name.Contains("chrome.exe"));
        unrelated.Should().BeNull(
            "listedeki Tarkov süreçleri dışında hiçbir süreç proxy kuralı almamalı");

        AssertAcceptedByRealSingBox(result.Data?.ToString(), "tarkov-gpn");
    }

    [Fact]
    public void TarkovGpnConfig_WarpEntriesOnNonWireguardNode_FallBackToProxy()
    {
        // WARP egress yalnızca WireGuard düğümünde mümkündür (sunucudaki WARP
        // dinleyicisine tünel içinden ulaşılır). WireGuard dışı bir düğümde warp
        // kuralları proxy'ye düşmeli ve warp outbound'u üretilmemeli — aksi hâlde
        // kural var olmayan outbound'a işaret eder ve sing-box başlamaz.
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box, remarks: "demo-node");
        var apps = new[]
        {
            new SplitTunnelAppItem { EntryType = "app", Value = "BsGLauncher.exe", Action = "warp" },
        };
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: false);

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            RoutingItem = new RoutingItem
            {
                Id = "gpn-warp-fallback",
                Remarks = "gpn-warp-fallback",
                RuleSet = JsonUtils.Serialize(managed, false),
                RuleNum = managed.Count,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"config generation failed: {result.Msg}");

        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        cfg.outbounds.Should().NotContain(o => o.tag == Global.WarpTag,
            "WireGuard dışı düğümde warp outbound üretilmez");
        // QUIC önleme kuralı (outbound'suz reject) process kuralından önce eşleşmesin diye
        // outbound != null şartı aranır.
        var rule = cfg.route.rules.FirstOrDefault(r =>
            r.outbound != null && r.process_name != null && r.process_name.Contains("BsGLauncher.exe"));
        rule.Should().NotBeNull();
        rule!.outbound.Should().Be(Global.ProxyTag,
            "warp kuralı var olmayan outbound yerine proxy'ye düşer");

        AssertAcceptedByRealSingBox(result.Data?.ToString(), "tarkov-gpn-warp-fallback");
    }

    [Fact]
    public void TarkovGpnConfig_BlacklistDirection_KeepsTarkovProcessesOutsideTunnel()
    {
        // Kara liste (invertManual=true) yönünde vpn atanan Tarkov süreçleri tünel
        // DIŞINDA kalır (direct exception) ve geri kalan her şey proxy'ye gider —
        // sing-box kural üretiminin yön çevrimi önayar süreçleriyle de tutarlıdır.
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateItalyWireguardNode(ECoreType.sing_box, remarks: "AoGPN İtalya");

        var apps = new[]
        {
            new SplitTunnelAppItem { EntryType = "app", Value = "BsGLauncher.exe", Action = "warp" },
            new SplitTunnelAppItem { EntryType = "app", Value = "EscapeFromTarkov.exe", Action = "vpn" },
            new SplitTunnelAppItem { EntryType = "app", Value = "EscapeFromTarkov_BE.exe", Action = "vpn" },
        };
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: true);

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            RoutingItem = new RoutingItem
            {
                Id = "gpn-tarkov-blacklist",
                Remarks = "gpn-tarkov-blacklist",
                RuleSet = JsonUtils.Serialize(managed, false),
                RuleNum = managed.Count,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"config generation failed: {result.Msg}");

        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        foreach (var expected in new[] { "BsGLauncher.exe", "EscapeFromTarkov.exe", "EscapeFromTarkov_BE.exe" })
        {
            var rule = cfg.route.rules.FirstOrDefault(r =>
                r.outbound == Global.DirectTag
                && r.process_name != null
                && r.process_name.Contains(expected));
            rule.Should().NotBeNull($"kara listede {expected} direct istisna olmalı");
        }

        // Kara liste catch-all: 0-65535 port aralığı proxy'ye gider (sing-box'ta
        // port_range "0:65535" olarak üretilir).
        var catchAll = cfg.route.rules.FirstOrDefault(r =>
            r.port_range != null && r.port_range.Contains("0:65535"));
        catchAll.Should().NotBeNull("kara liste catch-all proxy'ye gitmeli");
        catchAll!.outbound.Should().Be(Global.ProxyTag,
            "kara listede listelenmeyen her şey tünelden geçer");

        AssertAcceptedByRealSingBox(result.Data?.ToString(), "tarkov-gpn-blacklist");
    }

    [Fact]
    public void WireguardGpnConfig_Ipv6Disabled_UsesIpv4OnlyBind()
    {
        // Windows'ta IPv6 protokolü adaptörde devre dışıysa sing-box WireGuard
        // endpoint'i "listen udp6 [::]: An invalid argument was supplied" ile
        // el sıkışmayı hiç gönderemez (SagerNet/sing-box#3571). Kullanıcı IPv6'yı
        // kapatmışken endpoint IPv4-only üretilmeli: inet4_bind_address=0.0.0.0
        // ve allowed_ips'ten ::/0 çıkarılmalı.
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        config.TunModeItem.EnableIPv6Address = false; // kullanıcı IPv6'yı kapatmış
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateItalyWireguardNode(ECoreType.sing_box, remarks: "AoGPN İtalya");
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            RoutingItem = new RoutingItem
            {
                Id = "gpn-wg-routing",
                Remarks = "gpn-wireguard-routing",
                RuleSet = "[]",
                RuleNum = 0,
                DomainStrategy = Global.AsIs,
                DomainStrategy4Singbox = string.Empty,
            },
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"config generation failed: {result.Msg}");

        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var wgEndpoint = cfg.endpoints?.FirstOrDefault(e => e.type == "wireguard");
        wgEndpoint.Should().NotBeNull();
        wgEndpoint!.detour.Should().Be(Global.DirectTag,
            "TUN döngüsünü kırmak için endpoint direct outbound'a detour edilir; detour etkinken inet4_bind_address yerine bu kullanılır");
        wgEndpoint.inet4_bind_address.Should().BeNull(
            "detour etkinken diğer dial alanları yok sayılır — dual-stack listener'ı açacak inet4_bind_address kurulmaz");
        wgEndpoint.peers.Should().HaveCount(1);
        wgEndpoint.peers[0].allowed_ips.Should().NotContain("::/0",
            "IPv6 kapalıyken peer'a ::/0 vermek anlamsız; IPv6 socket'i açılmamalı");
        wgEndpoint.peers[0].allowed_ips.Should().Contain("0.0.0.0/0");

        AssertAcceptedByRealSingBox(result.Data?.ToString(), "wireguard-gpn-ipv6-off");
    }

    private static void AssertAcceptedByRealSingBox(CoreConfigContext context, string scenario)
    {
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"[{scenario}] config generation failed: {result.Msg}");
        AssertAcceptedByRealSingBox(result.Data?.ToString(), scenario);
    }

    private static void AssertAcceptedByRealSingBox(string? configJson, string scenario)
    {
        var singBoxBinary = LocateSingBoxBinary();
        if (singBoxBinary is null)
        {
            Assert.Skip("sing-box binary not found in build output; skipping real-binary config validation.");
            return;
        }

        configJson.Should().NotBeNull($"[{scenario}] generated config was null");

        var tmpDir = Path.Combine(Path.GetTempPath(), "aogpn-singbox-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var cfg = JsonUtils.Deserialize<SingboxConfig>(configJson)!;
            RewriteRuntimePaths(cfg, tmpDir, singBoxBinary);

            var cfgPath = Path.Combine(tmpDir, "config.json");
            File.WriteAllText(cfgPath, JsonUtils.Serialize(cfg, false));

            var (exitCode, output) = Run(singBoxBinary, $"check -c \"{cfgPath}\"");
            exitCode.Should().Be(0,
                $"[{scenario}] sing-box check rejected the generated config:{Environment.NewLine}{output}");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// The generated config points log/cache/rule-set files at paths under the test
    /// output directory. Rewrite them so <c>check</c> does not fail on missing files:
    /// log and cache go into the temp dir, and rule-set declarations point at the real
    /// assets shipped next to the binary. When no matching asset exists, the rule-set
    /// declarations and all references to them are dropped so the structural validation
    /// still runs.
    /// </summary>
    private static void RewriteRuntimePaths(SingboxConfig cfg, string tmpDir, string singBoxBinary)
    {
        if (cfg.log is { output: not null })
        {
            cfg.log.output = Path.Combine(tmpDir, "singbox.log");
        }

        if (cfg.experimental?.cache_file is { path: not null })
        {
            cfg.experimental.cache_file.path = Path.Combine(tmpDir, "cache.db");
        }

        if (cfg.route?.rule_set is not { Count: > 0 })
        {
            return;
        }

        var srssDir = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(singBoxBinary))!, "srss");
        var allResolved = true;
        foreach (var ruleSet in cfg.route.rule_set)
        {
            if (ruleSet.path is null)
            {
                continue;
            }

            var realPath = Path.Combine(srssDir, Path.GetFileName(ruleSet.path));
            if (File.Exists(realPath))
            {
                ruleSet.path = realPath;
            }
            else
            {
                allResolved = false;
            }
        }

        if (allResolved)
        {
            return;
        }

        cfg.route.rule_set = [];
        cfg.route.rules?.ForEach(rule => rule.rule_set = null);
        cfg.dns?.rules?.ForEach(rule => rule.rule_set = null);
    }

    internal static string? LocateSingBoxBinary()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        // The repo root found above is the directory that contains AoGPN.slnx;
        // the WPF app project lives one level below it.
        var appBinRoot = Path.Combine(root, "AoGPN", "bin");
        if (!Directory.Exists(appBinRoot))
        {
            return null;
        }

        var binaryName = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var configurationDir = Path.Combine(appBinRoot, configuration);
            if (!Directory.Exists(configurationDir))
            {
                continue;
            }

            foreach (var targetFramework in Directory.GetDirectories(configurationDir))
            {
                var candidate = Path.Combine(
                    targetFramework, "bin", "sing_box", binaryName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AoGPN.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (process.ExitCode, output);
    }
}
