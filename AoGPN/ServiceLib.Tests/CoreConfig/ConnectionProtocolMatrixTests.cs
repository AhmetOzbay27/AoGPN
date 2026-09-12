using AwesomeAssertions;
using ServiceLib.Services.CoreConfig;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

public class ConnectionProtocolMatrixTests
{
    [Fact]
    public void OpenVpnUri_ParsesAndGeneratesNativeClientConfig()
    {
        var item = OpenVPNFmt.Resolve(
            "openvpn://game-user@vpn.example.com:1194?password=game-pass&proto=tcp#EU%20Game",
            out var message);

        item.Should().NotBeNull();
        message.Should().BeEmpty();
        item!.ConfigType.Should().Be(EConfigType.OpenVPN);
        item.CoreType.Should().Be(ECoreType.openvpn);
        item.Address.Should().Be("vpn.example.com");
        item.Port.Should().Be(1194);
        item.GetProtocolExtra().Flow.Should().Be("tcp");

        var generated = OpenVPNFmt.GenerateConfig(item);

        generated.Should().Contain("proto tcp");
        generated.Should().Contain("remote vpn.example.com 1194");
        generated.Should().Contain("<auth-user-pass>");
        generated.Should().Contain("game-user");
        generated.Should().Contain("game-pass");
    }

    [Theory]
    [InlineData(EConfigType.VMess, "vmess")]
    [InlineData(EConfigType.VLESS, "vless")]
    [InlineData(EConfigType.Shadowsocks, "shadowsocks")]
    [InlineData(EConfigType.Trojan, "trojan")]
    [InlineData(EConfigType.Hysteria2, "hysteria")]
    [InlineData(EConfigType.WireGuard, "wireguard")]
    [InlineData(EConfigType.SOCKS, "socks")]
    [InlineData(EConfigType.HTTP, "http")]
    public void Xray_GeneratesOutboundForEverySupportedProtocol(EConfigType configType, string expectedProtocol)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateNode(configType, ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"{configType}: {result.Msg}");
        var generated = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString());
        generated.Should().NotBeNull();
        generated!.outbounds.Should().Contain(outbound =>
            outbound.tag == Global.ProxyTag && outbound.protocol == expectedProtocol);
    }

    [Theory]
    [InlineData(EConfigType.VMess, "vmess")]
    [InlineData(EConfigType.VLESS, "vless")]
    [InlineData(EConfigType.Shadowsocks, "ss")]
    [InlineData(EConfigType.Trojan, "trojan")]
    [InlineData(EConfigType.Hysteria2, "hysteria2")]
    [InlineData(EConfigType.TUIC, "tuic")]
    [InlineData(EConfigType.SOCKS, "socks5")]
    [InlineData(EConfigType.HTTP, "http")]
    public void MihomoGlobal_GeneratesSingleProxyForEverySupportedProtocol(EConfigType configType, string expectedType)
    {
        // Global VPN (mihomo): seçili profil tek proxy'ye çevrilir — WireGuard
        // GPN'e özgüdür (GpnMihomoConfigService kapsar), Anytls/Naive mihomo'da
        // yoktur ve MihomoSupportConfigType dışındadır.
        var node = CreateNode(configType, ECoreType.mihomo);
        var yaml = MihomoGlobalConfigService.GenerateGlobalYaml(node,
            new GpnMihomoOptions { MixedPort = 10808 }, tunEnabled: true);

        yaml.Should().NotBeNullOrEmpty();
        var generated = YamlUtils.FromYaml<Dictionary<string, object?>>(yaml);
        generated.Should().NotBeNull();

        var proxies = generated!["proxies"] as List<object?>;
        proxies.Should().NotBeNull();
        var proxy = proxies!.Single() as Dictionary<object, object?>;
        proxy.Should().NotBeNull();
        proxy!["name"]!.ToString().Should().Be(MihomoGlobalConfigService.GlobalProxyName);
        proxy["type"]!.ToString().Should().Be(expectedType);

        // Global mod: tek MATCH → proxy kuralı, split-tunnel kuralı YOK.
        var rules = (generated["rules"] as List<object?>)!;
        rules.Should().ContainSingle();
        rules[0]!.ToString().Should().Be($"MATCH,{MihomoGlobalConfigService.GlobalProxyName}");
    }

    [Fact]
    public void MihomoGlobal_TunEnabled_ExcludesLoopbackFromRoute()
    {
        // Global TUN: uygulamanın kendi SOCKS'una (127.0.0.1:10808) giden trafik
        // tünele düşmemeli — aksi halde auto-route /1 rotaları loopback'i de
        // yakalar ve kendi proxy'sine bağlantı tünel içinde döner.
        var node = CreateNode(EConfigType.VLESS, ECoreType.mihomo);
        var yaml = MihomoGlobalConfigService.GenerateGlobalYaml(node,
            new GpnMihomoOptions { MixedPort = 10808 }, tunEnabled: true);

        yaml.Should().Contain("route-exclude-address");
        yaml.Should().Contain("127.0.0.0/8");
    }

    [Fact]
    public void MihomoGlobal_ProxyTransport_TunDisabled_MixedPortOnly()
    {
        // Global + Proxy: TUN kapalı — mixed-port (HTTP+SOCKS5) dinler, tun.enable=false.
        var node = CreateNode(EConfigType.VLESS, ECoreType.mihomo);
        var yaml = MihomoGlobalConfigService.GenerateGlobalYaml(node,
            new GpnMihomoOptions { MixedPort = 10808 }, tunEnabled: false);

        yaml.Should().NotBeNullOrEmpty();
        // YamlDotNet, object hedefli ayrıştırmada skalerleri string olarak döndürür
        // (JSON tipi zorlaması yapmaz) — değerler ham metin sözleşmesiyle doğrulanır.
        var generated = YamlUtils.FromYaml<Dictionary<string, object?>>(yaml);
        generated!.Should().ContainKey("mixed-port");
        generated["mixed-port"]!.ToString().Should().Be("10808");
        generated.Should().ContainKey("tun");
        var tun = (Dictionary<object, object?>)generated["tun"]!;
        tun["enable"]!.ToString().Should().Be("false"); // YAML skaleri — küçük harf
        tun["device"]!.ToString().Should().Be(Global.MihomoTunInterfaceName);
    }

    [Fact]
    public async Task MihomoCustomConfig_GeneratesMixedPortControllerAndTun()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"aogpn-mihomo-{Guid.NewGuid():N}.yaml");
        var outputPath = Path.Combine(Path.GetTempPath(), $"aogpn-mihomo-output-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(sourcePath, "mode: rule\nproxies: []\n", TestContext.Current.CancellationToken);

            var config = CoreConfigTestFactory.CreateConfig(ECoreType.mihomo);
            config.TunModeItem.EnableTun = true;
            CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = new ProfileItem
            {
                IndexId = "mihomo-custom",
                ConfigType = EConfigType.Custom,
                CoreType = ECoreType.mihomo,
                Remarks = "mihomo custom",
                Address = sourcePath,
            };

            var result = await new CoreConfigClashService(config, isTunEnabled: true)
                .GenerateClientCustomConfig(node, outputPath);

            result.Success.Should().BeTrue($"{result.Msg}");
            var generated = YamlUtils.FromYaml<Dictionary<string, object>>(
                await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            generated.Should().NotBeNull();
            generated!.Should().ContainKey("mixed-port");
            generated.Should().ContainKey("external-controller");
            generated.Should().ContainKey("tun");
        }
        finally
        {
            if (File.Exists(sourcePath))
            {
                File.Delete(sourcePath);
            }
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void Xray_TlsOutbound_DoesNotEmitAllowInsecure_Xray26Compatibility()
    {
        // Xray 26 removed the allowInsecure option entirely (migrated to
        // pinnedPeerCertSha256 / verifyPeerCertByName). Writing it — even as
        // false — makes config validation fail, which is exactly the
        // "CORE_CHECK success=False" the session log showed for TLS nodes.
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateNode(EConfigType.Trojan, ECoreType.Xray);
        node.StreamSecurity = Global.StreamSecurity;
        node.Sni = "example.com";
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"{result.Msg}");
        var generated = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString());
        generated.Should().NotBeNull();

        var proxy = generated!.outbounds.First(o => o.tag == Global.ProxyTag);
        var tls = proxy.streamSettings?.tlsSettings;
        tls.Should().NotBeNull("Trojan with StreamSecurity must carry TLS settings");
        tls!.allowInsecure.Should().BeNull("Xray 26 rejects allowInsecure — it must stay null/omitted");

        // And the raw JSON must not contain the key at all.
        var rawJson = result.Data!.ToString()!;
        rawJson.Should().NotContain("allowInsecure", "Xray 26 config validation fails when allowInsecure is present");
    }

    [Fact]
    public void Xray_AlwaysAppendsFinalProxyRule_EvenWithoutBalancer()
    {
        // Regression: BuildFinalRule was only appended when a balancer existed.
        // A missing catch-all (or a stale "0-65535 direct" rule in the active
        // routing profile) then left unmatched traffic leaking direct in
        // Global VPN mode. The final rule must always be present.
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateNode(EConfigType.VLESS, ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"{result.Msg}");
        var generated = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString());
        generated.Should().NotBeNull();

        var finalRules = generated!.routing.rules
            .Where(r => r.outboundTag == Global.ProxyTag)
            .ToList();
        finalRules.Should().NotBeEmpty("a final proxy rule must always be appended");

        // The final rule is the last one: first-match-wins routing means any
        // earlier user rule (including a stale direct catch-all) is honoured,
        // but unmatched traffic still terminates at the proxy, never direct.
        var last = generated.routing.rules[^1];
        last.outboundTag.Should().Be(Global.ProxyTag, "the last routing rule must be the proxy catch-all");
    }

    private static ProfileItem CreateNode(EConfigType configType, ECoreType coreType)
    {
        var node = new ProfileItem
        {
            IndexId = $"matrix-{configType}-{coreType}",
            ConfigType = configType,
            CoreType = coreType,
            Remarks = $"matrix-{configType}",
            Address = "example.com",
            Port = 443,
            Password = "password",
            Username = "user",
            Network = nameof(ETransport.raw),
            StreamSecurity = string.Empty,
            Subid = string.Empty,
        };

        switch (configType)
        {
            case EConfigType.VMess:
                node.Password = Guid.NewGuid().ToString();
                node.SetProtocolExtra(node.GetProtocolExtra() with
                {
                    AlterId = "0",
                    VmessSecurity = Global.DefaultSecurity,
                });
                break;

            case EConfigType.VLESS:
                node.Password = Guid.NewGuid().ToString();
                node.SetProtocolExtra(node.GetProtocolExtra() with
                {
                    Flow = string.Empty,
                    VlessEncryption = Global.None,
                });
                break;

            case EConfigType.Shadowsocks:
                node.SetProtocolExtra(node.GetProtocolExtra() with
                {
                    SsMethod = "aes-256-gcm",
                });
                break;

            case EConfigType.Trojan:
                node.StreamSecurity = Global.StreamSecurity;
                node.Sni = "example.com";
                break;

            case EConfigType.Hysteria2:
                node.Network = string.Empty;
                node.StreamSecurity = string.Empty;
                break;

            case EConfigType.TUIC:
                node.Username = Guid.NewGuid().ToString();
                node.Network = string.Empty;
                node.StreamSecurity = Global.StreamSecurity;
                node.Alpn = "h3";
                node.CoreType = ECoreType.mihomo;
                break;

            case EConfigType.Anytls:
                node.Network = string.Empty;
                node.StreamSecurity = Global.StreamSecurity;
                node.CoreType = ECoreType.mihomo;
                break;

            case EConfigType.Naive:
                node.Username = "naive-user";
                node.Network = string.Empty;
                node.StreamSecurity = Global.StreamSecurity;
                node.CoreType = ECoreType.mihomo;
                break;

            case EConfigType.WireGuard:
                node.Address = "198.51.100.10";
                node.Port = 51820;
                node.Password = "private-key";
                node.Network = string.Empty;
                node.SetProtocolExtra(node.GetProtocolExtra() with
                {
                    WgPublicKey = "public-key",
                    WgInterfaceAddress = "172.16.0.2/32",
                    WgMtu = 1280,
                });
                break;

            case EConfigType.SOCKS:
                node.Username = "socks-user";
                node.Password = "socks-password";
                break;

            case EConfigType.HTTP:
                node.Username = "http-user";
                node.Password = "http-password";
                break;
        }

        return node;
    }
}
