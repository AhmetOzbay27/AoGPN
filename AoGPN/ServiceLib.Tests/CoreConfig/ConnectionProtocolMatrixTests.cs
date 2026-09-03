using AwesomeAssertions;
using ServiceLib.Services.CoreConfig;
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
    [InlineData(EConfigType.Shadowsocks, "shadowsocks")]
    [InlineData(EConfigType.Trojan, "trojan")]
    [InlineData(EConfigType.Hysteria2, "hysteria2")]
    [InlineData(EConfigType.TUIC, "tuic")]
    [InlineData(EConfigType.Anytls, "anytls")]
    [InlineData(EConfigType.Naive, "naive")]
    [InlineData(EConfigType.WireGuard, "wireguard")]
    [InlineData(EConfigType.SOCKS, "socks")]
    [InlineData(EConfigType.HTTP, "http")]
    public void Singbox_GeneratesOutboundForEverySupportedProtocol(EConfigType configType, string expectedType)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateNode(configType, ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"{configType}: {result.Msg}");
        var generated = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString());
        generated.Should().NotBeNull();
        if (configType == EConfigType.WireGuard)
        {
            generated!.endpoints.Should().Contain(endpoint =>
                endpoint.tag == Global.ProxyTag && endpoint.type == expectedType);
        }
        else
        {
            generated!.outbounds.Should().Contain(outbound =>
                outbound.tag == Global.ProxyTag && outbound.type == expectedType);
        }
    }

    [Fact]
    public void Singbox_PreSocksHelper_DoesNotClaimMainApiOrCache()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateNode(EConfigType.SOCKS, ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            IsPreSocks = true,
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue($"{result.Msg}");
        var generated = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString());
        generated.Should().NotBeNull();
        generated!.experimental.Should().BeNull();
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
                node.CoreType = ECoreType.sing_box;
                break;

            case EConfigType.Anytls:
                node.Network = string.Empty;
                node.StreamSecurity = Global.StreamSecurity;
                node.CoreType = ECoreType.sing_box;
                break;

            case EConfigType.Naive:
                node.Username = "naive-user";
                node.Network = string.Empty;
                node.StreamSecurity = Global.StreamSecurity;
                node.CoreType = ECoreType.sing_box;
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
