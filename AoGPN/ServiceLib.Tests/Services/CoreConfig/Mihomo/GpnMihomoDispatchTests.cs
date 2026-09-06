using AwesomeAssertions;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Builder;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Services.CoreConfig.Mihomo;
using ServiceLib.Services.Gpn;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services.CoreConfig.Mihomo;

/// <summary>
/// GPN → mihomo dağıtımının uçtan uca sınanması: gerçek pipeline
/// (BuildWireGuardProfile → CoreConfigContextBuilder.BuildAll →
/// CoreConfigHandler.GenerateClientConfig) mihomo YAML'i üretir mi?
///
/// Beklenen akış (canlı doğrulama, Ağu 2026):
///  * BuildAll, RunCoreType = mihomo üretir (sing-box'a zorlanmaz),
///  * pre-socks üretilmez (mihomo own TUN),
///  * config dalı YAML üretir: warp kuralları → warp-socks (dialer-proxy),
///    vpn kuralları → wg-&lt;id&gt;, MATCH,DIRECT güvenlik ağı.
/// </summary>
[Collection("SharedDatabase")]
public class GpnMihomoDispatchTests
{
    [Fact]
    public async Task GenerateClientConfig_GpnWireGuardProfile_ProducesMihomoYaml()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        config.TunModeItem.EnableTun = true;
        // Uygulamanın DNS ayarları → mihomo config'ine aktarılmalı.
        config.SimpleDNSItem = new SimpleDNSItem
        {
            RemoteDNS = "1.1.1.1;8.8.8.8,https://cloudflare-dns.com/dns-query",
            BootstrapDNS = "9.9.9.9",
            FakeIPRange = "198.18.0.1/16",
        };

        // Paylaşılan SQLite'ta başka testlerin bıraktığı aktif routing'ler
        // GetDefaultRouting'in sonucunu kirletebilir — önce hepsini pasife al,
        // sonra kendi kural setini tek aktif satır olarak yaz.
        // RoutingItem tablosunu AppManager.InitApp yaratır; test host'unda eksik olabilir.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        await SQLiteHelper.Instance.ExecuteAsync("UPDATE RoutingItem SET IsActive = 0");

        // Aktif rota kural seti: launcher → warp, oyun → vpn, yakalayıcı → direct.
        var activeRouting = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "active-routing",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new()
                {
                    Id = Utils.GetGuid(false),
                    Process = ["BsGLauncher.exe"],
                    OutboundTag = Global.WarpTag,
                    Enabled = true,
                    Remarks = "launcher → WARP",
                },
                new()
                {
                    Id = Utils.GetGuid(false),
                    Process = ["EscapeFromTarkov.exe", "BattlEye_BE.exe"],
                    OutboundTag = Global.ProxyTag,
                    Enabled = true,
                    Remarks = "oyun → tünel",
                },
                new()
                {
                    Id = Utils.GetGuid(false),
                    Port = "0-65535",
                    OutboundTag = Global.DirectTag,
                    Enabled = true,
                    Remarks = "yakalayıcı → direct",
                },
            }, false),
            RuleNum = 3,
            IsActive = true,
            Sort = 0,
        };
        await SQLiteHelper.Instance.ReplaceAsync(activeRouting);

        var server = new GpnServerProfile(
            ServerId: "de",
            Name: "Almanya",
            EndpointHost: "130.61.223.36",
            EndpointPort: 51820,
            ServerPublicKey: "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
            ClientPrivateKey: "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
            ClientAddress: "10.66.66.2/24",
            Mtu: 1420,
            PersistentKeepalive: 25);
        var otherServer = new GpnServerProfile(
            ServerId: "it",
            Name: "İtalya",
            EndpointHost: "92.4.220.236",
            EndpointPort: 51820,
            ServerPublicKey: "iK25iMzwDmLjq2PzX7w6yYdLk9nTgQ4vRcF1sB8hUaA=",
            ClientPrivateKey: "nL4xOq2WcVb9sRt7YhGf1pDk8uJm0zXeN6SaIcQ5wE=",
            ClientAddress: "10.66.66.2/24",
            Mtu: 1420,
            PersistentKeepalive: 25);

        var node = GpnCoreLauncher.BuildWireGuardProfile(server);

        // 1) Launcher → profil mihomo çekirdeği taşımalı.
        node.CoreType.Should().Be(ECoreType.mihomo);

        // 2) Gerçek BuildAll akışı: mihomo'ya zorlama yok, pre-socks yok.
        var allResult = await CoreConfigContextBuilder.BuildAll(config, node);
        allResult.Success.Should().BeTrue(string.Join("; ",
            allResult.CombinedValidatorResult.Errors));
        allResult.MainResult.Context.RunCoreType.Should().Be(ECoreType.mihomo);
        allResult.PreSocksResult.Should().BeNull("mihomo own TUN — legacy protect helper üretilmez");

        // 3) Config üretimi: mihomo YAML.
        var result = await CoreConfigHandler.GenerateClientConfig(allResult.MainResult.Context, fileName: null);
        result.Success.Should().BeTrue(result.Msg);
        var yaml = result.Data?.ToString();
        yaml.Should().NotBeNullOrEmpty();

        yaml.Should().Contain("type: wireguard");
        yaml.Should().Contain("server: 130.61.223.36");
        yaml.Should().Contain("name: wg-de");
        yaml.Should().Contain("name: warp-socks");
        yaml.Should().Contain("dialer-proxy: wg-de");
        yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,warp-socks");
        yaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,wg-de");
        yaml.Should().Contain("MATCH,DIRECT");
        // mixed-port, yerel SOCKS portuna bağlı (readiness probe portu).
        yaml.Should().Contain($"mixed-port: {AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}");
        // external-controller, uygulamanın StatePort2'sinde — ClashApiManager ve
        // StatisticsSingboxService (dashboard telemetrisi) aynı portu okur;
        // mihomo orada dinler → /connections + /proxies + /traffic canlı gelir.
        yaml.Should().Contain($"external-controller: {Global.Loopback}:{AppManager.Instance.StatePort2}");
        // fake-ip DNS üretilir (domain kuralları için).
        yaml.Should().Contain("enhanced-mode: fake-ip");
        // DNS nameserver'ları uygulamanın SimpleDNSItem ayarlarından gelir:
        // RemoteDNS → nameserver, BootstrapDNS → default-nameserver.
        yaml.Should().Contain("https://cloudflare-dns.com/dns-query");
        yaml.Should().Contain("9.9.9.9");
        yaml.Should().Contain("1.1.1.1");
        yaml.Should().Contain("8.8.8.8");

        // ── Çoklu-düğüm yolu: koordinatör aday listesini context'e taşırsa üretici
        // tüm adayları + GPN-Nodes select grubunu basar; kurallar ve WARP zinciri
        // gruba işaret eder (kesintisiz düğüm değişimi için tek PUT /proxies yeterli).
        var multiContext = allResult.MainResult.Context with
        {
            GpnCandidates = new[] { server, otherServer },
        };
        var multiResult = await CoreConfigHandler.GenerateClientConfig(multiContext, fileName: null);
        multiResult.Success.Should().BeTrue(multiResult.Msg);
        var multiYaml = multiResult.Data?.ToString();
        multiYaml.Should().NotBeNullOrEmpty();
        multiYaml.Should().Contain("name: wg-de");
        multiYaml.Should().Contain("name: wg-it");
        multiYaml.Should().Contain("name: GPN-Nodes");
        multiYaml.Should().Contain("type: select");
        multiYaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,GPN-Nodes");
        multiYaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,warp-socks");
        multiYaml.Should().Contain("dialer-proxy: GPN-Nodes");
        multiYaml.Should().NotContain("PROCESS-NAME,EscapeFromTarkov.exe,wg-de");
        multiYaml.Should().Contain("MATCH,DIRECT");
    }

    /// <summary>
    /// Kesintisiz rota (superset) dağıtımı: context GpnSoftPolicy taşırsa üretici
    /// GPN-MODE + ao-&lt;i&gt; gruplarını ve sabit giriş satırlarını basar; routing
    /// item'daki uygulama-yönetimli kurallar yok sayılır (superset satırlarıyla
    /// değiştirilir), kullanıcının kendi kuralları korunur. Çağrı aynı zamanda
    /// GpnSoftSession parmak izini başlatır (yumuşak uygulayıcının yapısal değişiklik
    /// kapısı) — test sonunda temizlenir.
    /// </summary>
    [Fact]
    public async Task GenerateClientConfig_WithSoftPolicy_EmitsSupersetGroupsAndStartsSession()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        config.TunModeItem.EnableTun = true;

        // Paylaşılan SQLite'ta başka testlerin bıraktığı aktif routing'ler
        // GetDefaultRouting'in sonucunu kirletebilir — önce hepsini pasife al.
        // RoutingItem tablosunu AppManager.InitApp yaratır; test host'unda eksik olabilir.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        await SQLiteHelper.Instance.ExecuteAsync("UPDATE RoutingItem SET IsActive = 0");

        // Kural seti: uygulama-yönetimli satır (superset tarafından ATILIR) +
        // kullanıcının kendi domain kuralı (KORUNUR).
        var activeRouting = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "active-routing",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new()
                {
                    Id = Utils.GetGuid(false),
                    Process = ["BsGLauncher.exe"],
                    OutboundTag = Global.ProxyTag,
                    Enabled = true,
                    Remarks = $"{ManualRoutingRules.ManagedRemarksPrefix} BsGLauncher.exe",
                },
                new()
                {
                    Id = Utils.GetGuid(false),
                    Domain = ["user-site.example"],
                    OutboundTag = Global.DirectTag,
                    Enabled = true,
                    Remarks = "kullanıcının kendi kuralı",
                },
            }, false),
            RuleNum = 2,
            IsActive = true,
            Sort = 0,
        };
        await SQLiteHelper.Instance.ReplaceAsync(activeRouting);

        var server = new GpnServerProfile(
            ServerId: "de", Name: "Almanya", EndpointHost: "130.61.223.36", EndpointPort: 51820,
            ServerPublicKey: "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
            ClientPrivateKey: "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
            ClientAddress: "10.66.66.2/24", Mtu: 1420, PersistentKeepalive: 25);
        var otherServer = new GpnServerProfile(
            ServerId: "it", Name: "İtalya", EndpointHost: "92.4.220.236", EndpointPort: 51820,
            ServerPublicKey: "iK25iMzwDmLjq2PzX7w6yYdLk9nTgQ4vRcF1sB8hUaA=",
            ClientPrivateKey: "nL4xOq2WcVb9sRt7YhGf1pDk8uJm0zXeN6SaIcQ5wE=",
            ClientAddress: "10.66.66.2/24", Mtu: 1420, PersistentKeepalive: 25);

        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: false,
            new[] { new GpnSoftRoutingEntry("app", "chrome.exe", "", "vpn") });
        try
        {
            var node = GpnCoreLauncher.BuildWireGuardProfile(server);
            var allResult = await CoreConfigContextBuilder.BuildAll(config, node);
            allResult.Success.Should().BeTrue();

            var context = allResult.MainResult.Context with
            {
                GpnSoftPolicy = policy,
                GpnCandidates = new[] { server, otherServer },
            };
            var result = await CoreConfigHandler.GenerateClientConfig(context, fileName: null);
            result.Success.Should().BeTrue(result.Msg);
            var yaml = result.Data?.ToString();
            yaml.Should().NotBeNullOrEmpty();

            // Superset yapısı: düğüm grubu + mod grubu + giriş grubu + sabit satır.
            yaml.Should().Contain("name: GPN-Nodes");
            yaml.Should().Contain("name: GPN-MODE");
            yaml.Should().Contain("name: ao-0");
            yaml.Should().Contain("name: wg-de");
            yaml.Should().Contain("name: wg-it");
            yaml.Should().Contain("PROCESS-NAME,chrome.exe,ao-0");
            yaml.Should().Contain("MATCH,GPN-MODE");
            yaml.Should().Contain("dialer-proxy: GPN-Nodes");

            // Yönetimli routing satırı superset tarafından değiştirildi (atıldı),
            // kullanıcının kendi kuralı korundu.
            yaml.Should().NotContain("BsGLauncher");
            yaml.Should().Contain("DOMAIN-SUFFIX,user-site.example,DIRECT");

            // Oturum parmak izi başlatıldı — yumuşak uygulayıcı bu listeyle eşleşir.
            GpnSoftSession.IsActive.Should().BeTrue();
            GpnSoftSession.Fingerprint.Should().Equal("app|chrome.exe|");
        }
        finally
        {
            GpnSoftSession.ResetForTests();
        }
    }

    /// <summary>
    /// Çift Bağlantı (Bölünmüş Tünelleme) dağıtımı: context küresel VLESS/Reality
    /// launcher-bypass düğümü (GpnVlessBypass) taşırsa CoreConfigHandler üreticiye
    /// iletir → YAML ikincil "vless-launcher" outbound'unu üretir, warp egress
    /// (BsGLauncher.exe) o düğüme gider ve WARP SOCKS5 zinciri (warp-socks +
    /// dialer-proxy) üretilmez. GpnCoreLauncher bu düğümü GuiItem.VlessBypassNodeJson
    /// ayarından context'e koyar (bu test, context → üretici ayağını doğrular).
    /// </summary>
    [Fact]
    public async Task GenerateClientConfig_WithGpnVlessBypassOnContext_EmitsDualConnectionYaml()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        config.TunModeItem.EnableTun = true;

        // RoutingItem tablosunu AppManager.InitApp yaratır; test host'unda eksik olabilir.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        await SQLiteHelper.Instance.ExecuteAsync("UPDATE RoutingItem SET IsActive = 0");
        var activeRouting = new RoutingItem
        {
            Id = Utils.GetGuid(false),
            Remarks = "active-routing",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new()
                {
                    Id = Utils.GetGuid(false),
                    Process = ["BsGLauncher.exe"],
                    OutboundTag = Global.WarpTag,
                    Enabled = true,
                    Remarks = "launcher → bypass",
                },
                new()
                {
                    Id = Utils.GetGuid(false),
                    Process = ["EscapeFromTarkov.exe"],
                    OutboundTag = Global.ProxyTag,
                    Enabled = true,
                    Remarks = "oyun → tünel",
                },
            }, false),
            RuleNum = 2,
            IsActive = true,
            Sort = 0,
        };
        await SQLiteHelper.Instance.ReplaceAsync(activeRouting);

        var server = new GpnServerProfile(
            ServerId: "de", Name: "Almanya", EndpointHost: "130.61.223.36", EndpointPort: 51820,
            ServerPublicKey: "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
            ClientPrivateKey: "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
            ClientAddress: "10.66.66.2/24", Mtu: 1420, PersistentKeepalive: 25);
        var bypass = new VlessProfileItem(
            Name: "Afrika VLESS",
            ServerAddress: "197.210.54.32",
            ServerPort: 443,
            Uuid: "7b6a8e31-8f2a-4b3c-9d4e-5f60718293a4",
            PublicKey: "REALITY_PUBLIC_KEY_PLACEHOLDER",
            ShortId: "1a2b3c4d",
            ServerName: "www.microsoft.com");

        var node = GpnCoreLauncher.BuildWireGuardProfile(server);
        var allResult = await CoreConfigContextBuilder.BuildAll(config, node);
        allResult.Success.Should().BeTrue();

        var context = allResult.MainResult.Context with { GpnVlessBypass = bypass };
        var result = await CoreConfigHandler.GenerateClientConfig(context, fileName: null);
        result.Success.Should().BeTrue(result.Msg);
        var yaml = result.Data?.ToString();
        yaml.Should().NotBeNullOrEmpty();

        // İkincil VLESS/Reality outbound + warp egress'in o düğüme gitmesi.
        yaml.Should().Contain("name: vless-launcher");
        yaml.Should().Contain("type: vless");
        yaml.Should().Contain("reality-opts:");
        yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,vless-launcher");
        // Oyun hâlâ ana WG tünelinde.
        yaml.Should().Contain("PROCESS-NAME,EscapeFromTarkov.exe,wg-de");
        // WARP SOCKS5 zinciri bu modda üretilmez.
        yaml.Should().NotContain("warp-socks");
        yaml.Should().NotContain("dialer-proxy");
        yaml.Should().Contain("MATCH,DIRECT");
    }

    /// <summary>
    /// Per-app WARP egress düğümleri (Ayarlar → GPN'deki küresel VLESS bypass'ın
    /// yerini alan yeni akış): politika girişlerinin WarpNodeId değerleri
    /// context.GpnWarpNodes üzerinden çözülür. WG düğümü → ayrı wg-&lt;id&gt; tüneli,
    /// VLESS düğümü → warp-&lt;id&gt; proxy outbound'u; ilgili satırların seçim
    /// grupları bu üyelere işaret eder. Çözülemeyen düğüm varsayılan WARP
    /// egress'e (warp-socks) düşer — kural geçersiz outbound'a gitmez.
    /// </summary>
    [Fact]
    public async Task GenerateClientConfig_WithPerAppWarpNodes_EmitsDedicatedEgressOutbounds()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        config.TunModeItem.EnableTun = true;

        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        await SQLiteHelper.Instance.ExecuteAsync("UPDATE RoutingItem SET IsActive = 0");

        var server = new GpnServerProfile(
            ServerId: "de", Name: "Almanya", EndpointHost: "130.61.223.36", EndpointPort: 51820,
            ServerPublicKey: "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
            ClientPrivateKey: "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
            ClientAddress: "10.66.66.2/24", Mtu: 1420, PersistentKeepalive: 25);
        var warpWgNode = new GpnServerProfile(
            ServerId: "gpn-za", Name: "Güney Afrika", EndpointHost: "196.2.4.9", EndpointPort: 51820,
            ServerPublicKey: "iK25iMzwDmLjq2PzX7w6yYdLk9nTgQ4vRcF1sB8hUaA=",
            ClientPrivateKey: "nL4xOq2WcVb9sRt7YhGf1pDk8uJm0zXeN6SaIcQ5wE=",
            ClientAddress: "10.66.66.3/24", Mtu: 1420, PersistentKeepalive: 25);
        var vlessProfile = new ProfileItem
        {
            IndexId = "vless-1",
            Remarks = "VLESS Düğüm",
            ConfigType = EConfigType.VLESS,
            Password = "7b6a8e31-8f2a-4b3c-9d4e-5f60718293a4",
            Address = "197.210.54.32",
            Port = 443,
            Network = nameof(ETransport.raw),
            StreamSecurity = string.Empty,
        };

        var policy = new GpnSoftRoutingPolicy(
            GameTriggerModes.Manual,
            InvertManualRouting: false,
            new[]
            {
                new GpnSoftRoutingEntry("app", "BsGLauncher.exe", "", "warp", "gpn-za"),
                new GpnSoftRoutingEntry("app", "LauncherApi.exe", "", "warp", "vless-1"),
                new GpnSoftRoutingEntry("app", "SilinenDugum.exe", "", "warp", "kayip-id"),
                new GpnSoftRoutingEntry("app", "chrome.exe", "", "vpn"),
            });

        try
        {
            var node = GpnCoreLauncher.BuildWireGuardProfile(server);
            var allResult = await CoreConfigContextBuilder.BuildAll(config, node);
            allResult.Success.Should().BeTrue();

            var context = allResult.MainResult.Context with
            {
                GpnSoftPolicy = policy,
                GpnCandidates = new[] { server },
                GpnWarpNodes = new Dictionary<string, WarpNodeProfile>
                {
                    ["gpn-za"] = new("gpn-za", "Güney Afrika", null, warpWgNode),
                    ["vless-1"] = new("vless-1", "VLESS Düğüm", vlessProfile, null),
                },
            };
            var result = await CoreConfigHandler.GenerateClientConfig(context, fileName: null);
            result.Success.Should().BeTrue(result.Msg);
            var yaml = result.Data?.ToString();
            yaml.Should().NotBeNullOrEmpty();

            // WG warp düğümü → ayrı wg-<id> tüneli (aday listesine üye değil).
            yaml.Should().Contain("name: wg-gpn-za");
            yaml.Should().Contain("server: 196.2.4.9");
            // VLESS warp düğümü → kendi adında proxy outbound'u.
            yaml.Should().Contain("name: warp-vless-1");
            yaml.Should().Contain("type: vless");
            // Çözülemeyen düğüm + varsayılan için WARP SOCKS zinciri hâlâ üretilir.
            yaml.Should().Contain("name: warp-socks");
            // Kural satırları kendi ao-<i> grubuna işaret eder.
            yaml.Should().Contain("PROCESS-NAME,BsGLauncher.exe,ao-0");
            yaml.Should().Contain("PROCESS-NAME,LauncherApi.exe,ao-1");
            yaml.Should().Contain("PROCESS-NAME,SilinenDugum.exe,ao-2");
            // Seçim grupları egress üyelerini taşır: warp düğümleri + warp-socks
            // (çözülemeyen satırın ve varsayılan warp'ın hedefi).
            yaml.Should().Contain("- wg-gpn-za");
            yaml.Should().Contain("- warp-vless-1");
            yaml.Should().Contain("- warp-socks");
        }
        finally
        {
            GpnSoftSession.ResetForTests();
        }
    }
}