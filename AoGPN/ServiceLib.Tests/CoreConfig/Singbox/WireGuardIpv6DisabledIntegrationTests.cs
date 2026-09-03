using System.Diagnostics;
using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Singbox;

/// <summary>
/// Uçtan uca GPN WireGuard entegrasyon testi: config üretimi → gerçek sing-box
/// binary'sinde ÇALIŞTIRMA. Windows'ta IPv6 kapalıyken (adaptör binding veya
/// DisabledComponents) sing-box WireGuard endpoint'i dual-stack UDP socket
/// açamaz — "listen udp6 [::]: An invalid argument was supplied" + "address
/// family not supported by protocol" ile el sıkışma HİÇ gönderilemez
/// (SagerNet/sing-box#3571, upstream'te düzeltilmemiş).
///
/// Düzeltme (BuildWireGuardEndpoint): IPv6 kullanılamıyorsa inet4_bind_address
/// = 0.0.0.0 ve allowed_ips = yalnız 0.0.0.0/0 üretilir; sing-box yalnız IPv4
/// socket açar ve el sıkışmayı gönderir. Bu test, üretilen config'in gerçek
/// binary'de gerçekten bind olup el sıkışma gönderdiğini (eski kodda kırılan
/// nokta) uçtan uca doğrular.
///
/// Gerçek sunucu el sıkışması TAMAMLANMAZ — test profilinin istemci özel
/// anahtarı yer tutucudur (canlı anahtar test kaynaklarına yazılmaz). Ama
/// "sending handshake initiation" satırı gönderim ANINDA loglanır; sunucu
/// yanıtına bağlı değildir. Bind + gönderim çalışıyorsa veri yolu açıktır.
/// Binary yoksa test kendini atlar (CI çekirdek varlığı indirmeyebilir).
/// </summary>
[Collection("SharedDatabase")]
public class WireGuardIpv6DisabledIntegrationTests
{
    [Fact]
    public void GpnWireGuardEndpoint_BindsAndSendsHandshake_WhenIpv6Disabled()
    {
        var singBox = SingBoxBinaryConfigValidationTests.LocateSingBoxBinary();
        if (singBox is null)
        {
            Assert.Skip("sing-box binary not found in build output; skipping runtime integration test.");
            return;
        }

        // IPv6 kapalı (kullanıcı ayarı) — deterministik olarak IPv4-only endpoint
        // üretir (EnableIPv6Address == true && CanBindIpv6Udp() kısa devresi).
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        config.TunModeItem.EnableIPv6Address = false;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateItalyWireguardNode(ECoreType.sing_box, remarks: "AoGPN İtalya");
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual,
            new[] { new SplitTunnelAppItem { EntryType = "app", Value = "chrome.exe", Action = "vpn" } },
            invertManual: false);
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

        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var wgEndpoint = cfg.endpoints?.FirstOrDefault(e => e.type == "wireguard");
        wgEndpoint.Should().NotBeNull("GPN modunda WireGuard endpoint üretilmeli");
        wgEndpoint!.detour.Should().Be(Global.DirectTag,
            "TUN döngüsünü kırmak + dual-stack listener'ı önlemek için endpoint direct'e detour edilmeli");
        wgEndpoint.peers[0].allowed_ips.Should().NotContain("::/0");

        // Runtime: üretilen endpoint'i gerçek binary'de çalıştır. auto_detect_interface
        // gerçek GPN config'inde açıktır ve IPv6 kapalı makinede dual-stack bind'i
        // tetikleyen kombinasyondur; düzeltilmiş config bununla bile çalışmalı.
        var runtimeJson = BuildRuntimeConfigJson(wgEndpoint, out var port);
        var output = RunSingBoxForSeconds(singBox, runtimeJson, seconds: 6);

        // Düzeltmenin kanıtı: bind başarılı + el sıkışma gönderildi + udp6 hatası yok.
        output.Should().Contain("udp bind has been updated",
            "IPv4-only bind ile WireGuard socket'i gerçekten açılmalı (eski kodda kırılan nokta)");
        output.Should().Contain("sending handshake initiation",
            "Bind sonrası el sıkışma gönderilir; sunucu yanıtı olmasa da gönderim loglanır");
        output.Should().NotContain("listen udp6", "IPv6 socket'i açılmamalı");
        output.Should().NotContain("address family not supported", "El sıkışma gönderimi engellenmemeli");
        output.Should().NotContain("unable to update bind");
    }

    /// <summary>
    /// Regresyon karşılaştırması — düzeltme ÖNCESİ davranışı (allowed_ips'te
    /// ::/0 + auto_detect_interface, inet4_bind_address'siz) aynı binary'de
    /// çalıştırır. Windows'ta default arayüzde (ör. Wi-Fi) IPv6 binding kapalıysa
    /// sing-box "listen udp6 [::]: An invalid argument was supplied" üretir ve
    /// el sıkışma hiç gönderilmez — düzeltmenin neden gerekli olduğunu kanıtlar.
    ///
    /// Not: Koşul .NET socket probe'una değil sing-box'ın GERÇEK çıktısına
    /// dayanır — .NET `[::]` bind'i kısıtlı IPv6'lı makinede bile başarılı olur
    /// (Teredo/loopback arayüzleri vardır) ama sing-box auto_detect_interface ile
    /// Wi-Fi'a bağlanınca IPv6'sız arayüzde dual-stack bind başarısız olur.
    /// IPv6 tam destekli makinede legacy config de çalışır; o zaman senaryo
    /// anlamsız olduğundan test atlanır.
    /// </summary>
    [Fact]
    public void LegacyDualStackEndpoint_ShowsUpstreamBindFailure_WhenDefaultInterfaceHasNoIpv6()
    {
        var singBox = SingBoxBinaryConfigValidationTests.LocateSingBoxBinary();
        if (singBox is null)
        {
            Assert.Skip("sing-box binary not found in build output; skipping runtime integration test.");
            return;
        }

        // Eski davranış: allowed_ips'te ::/0 var, inet4_bind_address YOK.
        var legacy = new Endpoints4Sbox
        {
            type = "wireguard",
            tag = Global.ProxyTag,
            system = false,
            mtu = 1420,
            address = ["10.66.66.2/24"],
            private_key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            peers =
            [
                new Peer4Sbox
                {
                    address = "92.4.220.236",
                    port = 51820,
                    public_key = "5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=",
                    allowed_ips = ["0.0.0.0/0", "::/0"],
                    persistent_keepalive_interval = 25,
                },
            ],
        };

        var output = RunSingBoxForSeconds(singBox, BuildRuntimeConfigJson(legacy, out var legacyPort), seconds: 5);

        // IPv6 tam destekliyse legacy config de bind olur — o makinede bu regresyon
        // geçerli değil, test anlamsız olduğu için atlanır.
        if (!output.Contains("listen udp6", StringComparison.Ordinal))
        {
            Assert.Skip("This machine provides full IPv6 on the default interface; the legacy dual-stack config binds fine, so the IPv6-disabled regression is not applicable here.");
            return;
        }

        // IPv6 kısıtlı makinede (kullanıcının senaryosu): upstream bug ürer —
        // düzeltmenin neden gerekli olduğunu uçtan uca kanıtlar.
        output.Should().Contain("address family not supported",
            "udp6 bind hatası el sıkışma gönderimini de engeller — düzeltme bu yüzden gerekli");
        output.Should().NotContain("udp bind has been updated",
            "eski davranışta bind hiç başarılı olmaz");
    }

    /// <summary>
    /// TUN inbound'LU gerçek config üretip, gerçek sing-box ile WireGuard
    /// endpoint'inin el sıkışmasının KENDİ TUN'una geri dönmediğini doğrular.
    ///
    /// Arka plan: sing-box TUN (auto_route + strict_route) çalışırken endpoint'in
    /// el sıkışma UDP'si fiziksel NIC'e bağlanmazsa sing-box KENDİ paketini yakalar
    /// (günlükteki ön görünüm: <c>inbound/tun ... to &lt;sunucu&gt;:51820</c> + hemen
    /// <c>endpoint/wireguard ... outbound ...</c> — sonsuz döngü, el sıkışma asla
    /// tamamlanmaz). Düzeltme: endpoint <c>detour=direct</c> edilir ve direct
    /// outbound <c>bind_interface</c> ile fiziksel NIC'e bağlanır → el sıkışma
    /// fiziksel ağdan çıkar, TUN'a girmez.
    ///
    /// Bu test gerçek TUN inbound'u kurar. Yönetici/wintun gerekir; ikisi de yoksa
    /// TUN oluşturulamaz ve test kendini atlar (bu makinede beklenen davranış).
    /// TUN açılabiliyorsa anti-loop koşulunu uygular: hedef sunucu IP:port'u hiçbir
    /// <c>inbound/tun</c> satırında görünmemelidir.
    /// </summary>
    [Fact]
    public void GpnWireGuardEndpoint_WithTunInbound_HandshakeDoesNotLoopIntoTun()
    {
        var singBox = SingBoxBinaryConfigValidationTests.LocateSingBoxBinary();
        if (singBox is null)
        {
            Assert.Skip("sing-box binary not found in build output; skipping runtime integration test.");
            return;
        }

        // Aynı üretici akışından (IPv6 kapalı) düzeltilmiş endpoint: detour=direct +
        // allowed_ips=0.0.0.0/0. TUN inbound'lu runtime config bunun etrafına kurulur.
        var wgEndpoint = BuildFixedGpnWireguardEndpoint();
        wgEndpoint.detour.Should().Be(Global.DirectTag,
            "döngü koruması detour=direct ile sağlanır (bind_interface dual-stack'i bozduğu için endpoint'te kullanılmaz)");

        var runtimeJson = BuildTunRuntimeConfigJson(wgEndpoint, out _);
        var output = RunSingBoxForSeconds(singBox, runtimeJson, seconds: 6);

        // TUN oluşturulamazsa (yönetici/wintun eksik) gerçek route-yakalama senaryosu
        // bu makinede çalıştırılamaz — test dürüstçe atlanır.
        if (ContainsAny(output,
            ["wintun", "Access is denied", "access is denied", "administrator",
             "Administrator", "permission denied", "Operation not permitted",
             "failed to create tun", "create tun", "create TUN", "interface name", "getadapters"]))
        {
            var reason = output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            Assert.Skip($"TUN inbound requires admin/wintun — anti-loop testi burada çalışamaz. ({(reason.Length > 120 ? reason[..120] : reason)})");
            return;
        }

        // Önce el sıkışmanın gerçekten gönderildiğini doğrula (endpoint bağlandı).
        output.Should().Contain("udp bind has been updated", "endpoint soketi açılmalı");
        output.Should().Contain("sending handshake initiation",
            "TUN varken endpoint el sıkışmayı göndermeli (veri yolu açık)");
        output.Should().NotContain("listen udp6", "IPv6 socket açılmamalı");
        output.Should().NotContain("address family not supported", "el sıkışma gönderimi engellenmemeli");

        // ── ANTI-LOOP ── hedef sunucu IP:port'u hiçbir inbound/tun satırında olmamalı.
        var peerHost = wgEndpoint.peers[0].address;
        var tunCaptures = output.Split('\n')
            .Where(l => l.Contains("inbound/tun", StringComparison.Ordinal)
                        && l.Contains($"{peerHost}:", StringComparison.Ordinal))
            .ToArray();
        tunCaptures.Should().BeEmpty(
            $"el sıkışma {peerHost}:{wgEndpoint.peers[0].port} kendi TUN'una geri girmemeli — döngü yakalandı: {string.Join(" | ", tunCaptures.Take(3))}");
    }

    private static bool ContainsAny(string haystack, IEnumerable<string> needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static Endpoints4Sbox BuildFixedGpnWireguardEndpoint()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        config.TunModeItem.EnableIPv6Address = false;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateItalyWireguardNode(ECoreType.sing_box, remarks: "AoGPN İtalya");
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual,
            new[] { new SplitTunnelAppItem { EntryType = "app", Value = "chrome.exe", Action = "vpn" } },
            invertManual: false);
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
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        return cfg.endpoints!.First(e => e.type == "wireguard");
    }

    /// <summary>WireGuard endpoint + gerçek TUN inbound (auto_route+strict_route) + direct (fiziksel NIC'e bağlı) içerir.</summary>
    private static string BuildTunRuntimeConfigJson(Endpoints4Sbox wgEndpoint, out int port)
    {
        port = GetFreeLoopbackPort();
        var (physName, _) = Utils.GetPhysicalDefaultInterface();
        var direct = new Outbound4Sbox
        {
            type = "direct",
            tag = Global.DirectTag,
            bind_interface = physName, // el sıkışmayı TUN yakalamasından kurtaran kilit alan
        };
        var runtime = new SingboxConfig
        {
            log = new Log4Sbox { level = "debug", timestamp = true },
            inbounds =
            [
                new Inbound4Sbox
                {
                    type = "tun",
                    tag = "tun",
                    mtu = 1408,
                    stack = "system",
                    address = ["172.18.0.1/30"],
                    auto_route = true,
                    strict_route = true,
                    interface_name = "aogpn-loop-test",
                },
                new Inbound4Sbox
                {
                    type = "mixed",
                    tag = "mixed-in",
                    listen = "127.0.0.1",
                    listen_port = port,
                },
            ],
            endpoints = [wgEndpoint],
            outbounds =
            [
                direct,
                new Outbound4Sbox { type = "block", tag = Global.BlockTag },
            ],
            route = new Route4Sbox
            {
                auto_detect_interface = true,
                rules = [],
                final = Global.DirectTag,
            },
        };
        return JsonUtils.Serialize(runtime, false);
    }

    // ── yardımcılar ───────────────────────────────────────────────────────

    /// <summary>
    /// WireGuard endpoint + mixed inbound + auto_detect_interface içeren minimal
    /// runtime config'i üretir (TUN inbound YOK — yönetici/wintun gerekmez;
    /// yalnızca WireGuard'ın UDP socket'i açılır). Üretilen endpoint'ten
    /// inet4_bind_address/allowed_ips korunur.
    /// </summary>
    private static string BuildRuntimeConfigJson(Endpoints4Sbox wgEndpoint, out int port)
    {
        // Her çalıştırmada yeni bir boş port seç — testler arası çakışmayı önler
        // (paralel koşan diğer testlerin loopback UDP/TCP soketleriyle).
        port = GetFreeLoopbackPort();
        var runtime = new SingboxConfig
        {
            log = new Log4Sbox { level = "debug", timestamp = true },
            inbounds =
            [
                new Inbound4Sbox
                {
                    type = "mixed",
                    tag = "mixed-in",
                    listen = "127.0.0.1",
                    listen_port = port,
                },
            ],
            endpoints = [wgEndpoint],
            outbounds =
            [
                new Outbound4Sbox { type = "direct", tag = Global.DirectTag },
                new Outbound4Sbox { type = "block", tag = Global.BlockTag },
            ],
            route = new Route4Sbox
            {
                auto_detect_interface = true,
                rules = [],
                final = Global.DirectTag,
            },
        };
        return JsonUtils.Serialize(runtime, false);
    }

    /// <summary>Loopback üzerinde boş bir UDP+TCP port bulur (soketi hemen kapatır — sing-box yeniden bağlanır).</summary>
    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// sing-box'i verilen config ile başlatır, <paramref name="seconds"/> saniye
    /// çalıştırıp kapatır ve stdout+stderr'i döndürür. Process sonsuza kadar
    /// çalışacağından süre sınırlıdır; çıktı tamamlanmamış olsa bile okunan
    /// kısım yeterlidir (el sıkışma ilk saniyelerde gönderilir).
    /// </summary>
    private static string RunSingBoxForSeconds(string singBoxBinary, string configJson, int seconds)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "aogpn-wg-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var cfgPath = Path.Combine(tmpDir, "config.json");
            File.WriteAllText(cfgPath, configJson);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBoxBinary,
                Arguments = $"run -c \"{cfgPath}\" --disable-color",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            process.WaitForExit(5_000);

            return outputTask.Result + errorTask.Result;
        }
        finally
        {
            try
            {
                Directory.Delete(tmpDir, recursive: true);
            }
            catch
            {
                // best-effort — dosyalar kilitliyse silinemeyebilir
            }
        }
    }

}
