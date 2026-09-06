namespace ServiceLib.Handler;

using ServiceLib.Services.CoreConfig.Mihomo;
using ServiceLib.Services.Gpn;

/// <summary>
/// Core configuration file processing class
/// </summary>
public static class CoreConfigHandler
{
    private static readonly string _tag = "CoreConfigHandler";

    public static async Task<RetResult> GenerateClientConfig(CoreConfigContext context, string? fileName)
    {
        var config = AppManager.Instance.Config;
        var result = new RetResult();
        var node = context.Node;

        if (context.RunCoreType == ECoreType.openvpn && node.ConfigType != EConfigType.Custom)
        {
            // OpenVPN owns its TUN device and consumes an .ovpn-style config;
            // never pass this profile through the Xray/sing-box JSON builders.
            result.Success = true;
            result.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            result.Data = OpenVPNFmt.GenerateConfig(node);
        }
        else if (context.RunCoreType == ECoreType.mihomo && node.ConfigType == EConfigType.WireGuard)
        {
            // GPN WireGuard → mihomo YAML (mihomo TUN + PROCESS-NAME süreç
            // kuralları + WARP SOCKS zinciri). sing-box'ın WG endpoint'i IPv6'sız
            // makinede el sıkışamıyor; mihomo aynı donanımda kanıtlandı.
            result = GenerateGpnMihomoConfig(context);
        }
        else if (node.ConfigType == EConfigType.Custom)
        {
            result = node.CoreType switch
            {
                ECoreType.mihomo => await new CoreConfigClashService(config, context.IsTunEnabled).GenerateClientCustomConfig(node, fileName),
                _ => await GenerateClientCustomConfig(node, fileName)
            };
        }
        else if (context.RunCoreType == ECoreType.mihomo && Global.MihomoSupportConfigType.Contains(node.ConfigType))
        {
            // GPN Global VPN (TUN/Proxy) — mihomo YAML: tüm trafik seçili profile
            // gider (MATCH → proxy). Xray'in Windows TUN'u yoktur; mihomo hem TUN'u
            // hem mixed-port (Proxy) dinleyicisini kendisi yönetir.
            result = GenerateGlobalMihomoConfig(context);
        }
        else
        {
            result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        }
        if (result.Success != true)
        {
            return result;
        }
        if (fileName.IsNotEmpty() && result.Data != null)
        {
            await File.WriteAllTextAsync(fileName, result.Data.ToString());
        }

        return result;
    }

    /// <summary>
    /// GPN WireGuard profilini + rota kurallarını mihomo YAML'ine çevirir.
    /// Üretici (GpnMihomoConfigService) saf ve birim testlidir; burada yalnızca
    /// context'ten girişler toplanır: profile (IndexId gpn-&lt;id&gt; + protocol
    /// extra alanları), routing rules (RulesItem listesi — ManualRoutingRules
    /// çıktısı) ve çalışma zamanı seçenekleri (mixed port = yerel SOCKS portu,
    /// fiziksel NIC adı, DNS).
    /// </summary>
    private static RetResult GenerateGpnMihomoConfig(CoreConfigContext context)
    {
        var ret = new RetResult();
        try
        {
            var node = context.Node;
            var rules = JsonUtils.Deserialize<List<RulesItem>>(context.RoutingItem?.RuleSet) ?? [];
            if (rules.Count == 0)
            {
                DiagLog.Write("GPN_MIHOMO config: rota kuralı yok — yalnızca MATCH,DIRECT üretilecek");
            }

            var server = ToGpnServerProfile(node);
            // Çift Bağlantı (Bölünmüş Tünelleme): küresel launcher-bypass (VLESS/Reality)
            // düğümü koordinatör başlatmasında context üzerinden taşınır (GpnCoreLauncher,
            // GuiItem.VlessBypassNodeJson'dan okur). Burada doğrudan üreticiye verilir;
            // üretici parametre doluysa ikincil "vless-launcher" outbound'unu üretir.
            var bypassNode = context.GpnVlessBypass;
            var controllerPort = AppManager.Instance.StatePort2;
            // DNS nameserver'ları uygulamanın kendi ayarlarından (SimpleDNSItem).
            // RemoteDNS → mihomo nameserver (tünel-içi çözüm — launcher domain'leri
            // ISP'ye sızmaz); BootstrapDNS → default-nameserver (saf IP önyükleme).
            // İkisi de boşsa ve kullanıcı custom DNSItem aktif etmişse DomainDNSAddress
            // son çare olarak kullanılır; hiçbiri yoksa üreticinin güvenlik ağı geçer.
            var simpleDns = context.SimpleDnsItem ?? new SimpleDNSItem();
            var remoteRaw = simpleDns.RemoteDNS;
            var nameservers = GpnMihomoConfigService.ParseDnsServers(
                remoteRaw, [Global.DomainRemoteDNSAddress.First()]);
            var bootstrap = GpnMihomoConfigService.ParseDnsServers(
                simpleDns.BootstrapDNS, []);
            // Kullanıcı RemoteDNS ayarlamamışsa ve custom DNSItem (per-core) aktif
            // ise son çare olarak onun DomainDNSAddress'i kullanılır.
            if (remoteRaw.IsNullOrEmpty()
                && context.RawDnsItem is { Enabled: true } customDns
                && customDns.DomainDNSAddress.IsNotEmpty())
            {
                nameservers = GpnMihomoConfigService.ParseDnsServers(customDns.DomainDNSAddress, nameservers);
            }
            var options = new GpnMihomoOptions
            {
                // Readiness probe bu porta bakar (AppManager.GetLocalPort(socks));
                // mihomo mixed-port aynı portta dinler → hem SOCKS hem HTTP.
                MixedPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
                // App'teki tüm Clash ailesi konvansiyonu: external-controller
                // StatePort2'de dinler (CoreConfigClashService/SingboxStatisticService
                // aynı portu kullanır). mihomo GPN oturumunda da oraya konur →
                // ClashApiManager (/connections, /proxies) ve StatisticsSingboxService
                // (/traffic WS) aynı yerden canlı telemetri alır; dashboard'un
                // sing-box yolları mihomo'ya YUMUŞAK geçer (IsRunningCore mihomo'yu
                // zaten sing_box uyumlu sayar).
                ExternalControllerPort = controllerPort,
                // TUN döngüsünü kıran kanıtlanmış kombinasyon: dial soketleri
                // fiziksel NIC'e kilitlenir (Faz C/DNS doğrulaması).
                InterfaceName = MihomoTunSupport.DetectPhysicalInterfaceName(server.EndpointHost),
                // Domain kuralları (ör. cdn.escapefromtarkov.com) için fake-ip DNS
                // şart: TUN sorguları mihomo içinde çözülür, ISP'ye sızıntı olmaz.
                // enhanced-mode KANITLANMIŞ fake-ip'te sabittir; FakeIPRange yalnız
                // geçerli bir CIDR ise kullanıcı ayarından alınır.
                DnsEnabled = true,
                DnsNameservers = nameservers,
                DnsDefaultNameservers = bootstrap,
                DnsEnhancedMode = "fake-ip",
                FakeIpRange = IsValidCidr(simpleDns.FakeIPRange)
                    ? simpleDns.FakeIPRange!
                    : "198.18.0.1/16",
                PersistentKeepalive = server.PersistentKeepalive,
                // WarpDialHealthMonitor bu dosyayı kuyruğundan izler — mihomo
                // stdout'a yazar, kalıcı dosya yoksa WARP dial hataları yakalanmaz.
                // (İleri bölü çizgiler: mihomo Windows'ta / yolunu da kabul eder,
                // YAML tırnak kaçış riskini sıfıra indirir.)
                LogFilePath = Path.Combine(Utils.GetLogPath(),
                    $"ao_mihomo_{DateTime.Now:yyyy-MM-dd}.log").Replace('\\', '/'),
            };
            DiagLog.Write($"GPN_MIHOMO options mixed={options.MixedPort} controller={controllerPort} nic={options.InterfaceName ?? "(auto)"} dns=[{string.Join(", ", nameservers)}] default=[{string.Join(", ", bootstrap)}] mode={options.DnsEnhancedMode}");

            // Çoklu-düğüm YAML'i: context'te aday listesi varsa (GpnCoreLauncher,
            // seçim yapılmış WireGuard bağlantısında koordinatörün adaylarını iletir)
            // tüm adaylar + "GPN-Nodes" select grubu üretilir → düğüm değişimi
            // restart'sız tek PUT /proxies çağrısı olur. Aday yoksa legacy tek-düğüm.
            var candidateNodes = context.GpnCandidates;
            var generator = new GpnMihomoConfigService();
            var policy = context.GpnSoftPolicy;
            string yaml;
            if (policy is not null)
            {
                // Superset (kesintisiz rota): routing item'daki uygulama-yönetimli
                // kurallar yok sayılır — satırlar politikanın girişlerinden üretilir
                // (her giriş sabit satır + kendi ao-<i> grubuna işaret eder), yalnızca
                // kullanıcının kendi (yönetilmeyen) kuralları korunur. Mod/rota
                // değişiklikleri artık restart'sız grup seçimi olur.
                var preserved = rules.Where(r => !ManualRoutingRules.IsManagedRule(r)).ToList();
                yaml = generator.GenerateYaml(candidateNodes, server, policy, preserved, options, bypassNode,
                    warpNodes: context.GpnWarpNodes);
                // Çalışan config artık superset oturumun parmak izini taşır: yumuşak
                // uygulayıcı (GpnSoftPolicyApplier) giriş listesinde yapısal değişiklik
                // olup olmadığını bu parmak iziyle doğrular ve gerektiğinde restart'lı
                // fallback'e düşer. Pre-socks (yardımcı) çekirdek oturumu temsil etmez.
                if (!context.IsPreSocks)
                {
                    GpnSoftSession.Begin(policy);
                }
                DiagLog.Write($"GPN_MIHOMO superset mode={policy.Mode} invert={policy.InvertManualRouting} entries={policy.Entries.Count} nodes={(candidateNodes is null ? 0 : candidateNodes.Count)}");
            }
            else
            {
                yaml = generator.GenerateYaml(candidateNodes, server, rules, options, bypassNode);
                DiagLog.Write($"GPN_MIHOMO nodes={(candidateNodes is null ? 0 : candidateNodes.Count)} multi={(candidateNodes?.Count > 1 ? "group" : "legacy")}");
            }
            LoggerWrite(yaml);
            ret.Data = yaml;
            ret.Success = true;
            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
        }
        return ret;
    }

    /// <summary>
    /// Global VPN (mihomo) YAML üreticisi: seçili profil tek proxy'ye çevrilir,
    /// TUN durumu context'ten gelir (IsTunEnabled — Proxy transportunda kapalı,
    /// mixed-port devrede), kural tek <c>MATCH,global-proxy</c>'dir. Spli-tunnel
    /// kuralı ÜRETİLMEZ — Global modda tüm trafik profile gider.
    /// </summary>
    private static RetResult GenerateGlobalMihomoConfig(CoreConfigContext context)
    {
        var ret = new RetResult();
        try
        {
            var simpleDns = context.SimpleDnsItem ?? new SimpleDNSItem();
            var nameservers = GpnMihomoConfigService.ParseDnsServers(
                simpleDns.RemoteDNS, [Global.DomainRemoteDNSAddress.First()]);
            var bootstrap = GpnMihomoConfigService.ParseDnsServers(simpleDns.BootstrapDNS, []);
            var options = new GpnMihomoOptions
            {
                MixedPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
                ExternalControllerPort = AppManager.Instance.StatePort2,
                InterfaceName = context.IsTunEnabled
                    ? MihomoTunSupport.DetectPhysicalInterfaceName(context.Node.Address)
                    : null,
                DnsEnabled = context.IsTunEnabled,
                DnsNameservers = nameservers,
                DnsDefaultNameservers = bootstrap,
                DnsEnhancedMode = "fake-ip",
                FakeIpRange = IsValidCidr(simpleDns.FakeIPRange) ? simpleDns.FakeIPRange! : "198.18.0.1/16",
                LogFilePath = Path.Combine(Utils.GetLogPath(),
                    $"ao_mihomo_{DateTime.Now:yyyy-MM-dd}.log").Replace('\\', '/'),
            };
            var yaml = MihomoGlobalConfigService.GenerateGlobalYaml(context.Node, options, context.IsTunEnabled);
            if (yaml.IsNullOrEmpty())
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }
            DiagLog.Write($"GPN_GLOBAL mihomo tun={context.IsTunEnabled} mixed={options.MixedPort} node={context.Node.Address}:{context.Node.Port} rule=MATCH,{MihomoGlobalConfigService.GlobalProxyName}");
            ret.Data = yaml;
            ret.Success = true;
            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
        }
        return ret;
    }

    /// <summary>Geçerli IPv4/IPv6 CIDR mi? (FakeIPRange kullanıcı ayarı koruması)</summary>
    private static bool IsValidCidr(string? cidr)
    {
        if (cidr.IsNullOrEmpty())
        {
            return false;
        }
        try
        {
            return IPNetwork2.TryParse(cidr, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Üretilen YAML özetini ao_diag.txt'ye yazar (hata ayıklama kanıtı).</summary>
    private static void LoggerWrite(string yaml)
    {
        var brief = string.Join(" | ", yaml.Split(Environment.NewLine)
            .Where(l => l.StartsWith("mixed-port") || l.StartsWith("interface-name")
                || l.StartsWith("- PROCESS-NAME") || l.StartsWith("- DOMAIN") || l.StartsWith("- IP-CIDR"))
            .Take(12));
        DiagLog.Write($"GPN_MIHOMO config generated: {brief}");
    }

    /// <summary>
    /// ProfileItem'ı (GPN launcher'ın BuildWireGuardProfile çıktısı) üreticinin
    /// beklediği GpnServerProfile kaydına geri çevirir. Tüm alanlar protocol
    /// extra'da taşınır (WgPublicKey, WgInterfaceAddress, WgMtu, keepalive).
    /// internal: NativeGpnStartStrategy (yerel motor köprü başlatması) aynı
    /// çeviriyi kullanır — tek kaynak, sürüklenme yok.
    /// </summary>
    internal static GpnServerProfile ToGpnServerProfile(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var id = node.IndexId.StartsWith("gpn-", StringComparison.Ordinal)
            ? node.IndexId["gpn-".Length..]
            : node.IndexId;
        if (id.IsNullOrEmpty())
        {
            id = "gpn";
        }
        return new GpnServerProfile(
            ServerId: id,
            Name: node.Remarks.IsNullOrEmpty() ? id : node.Remarks,
            EndpointHost: node.Address,
            EndpointPort: node.Port,
            ServerPublicKey: extra.WgPublicKey ?? string.Empty,
            ClientPrivateKey: node.Password ?? string.Empty,
            ClientAddress: extra.WgInterfaceAddress ?? "10.66.66.2/24",
            Mtu: extra.WgMtu ?? 0,
            PersistentKeepalive: extra.WgPersistentKeepalive ?? 0);
    }

    private static async Task<RetResult> GenerateClientCustomConfig(ProfileItem node, string? fileName)
    {
        var ret = new RetResult();
        try
        {
            if (node == null || fileName is null)
            {
                ret.Msg = ResUI.CheckServerSettings;
                return ret;
            }

            if (File.Exists(fileName))
            {
                File.SetAttributes(fileName, FileAttributes.Normal); //If the file has a read-only attribute, direct deletion will fail
                File.Delete(fileName);
            }

            var addressFileName = node.Address;
            if (!File.Exists(addressFileName))
            {
                addressFileName = Utils.GetConfigPath(addressFileName);
            }
            if (!File.Exists(addressFileName))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }
            File.Copy(addressFileName, fileName);
            File.SetAttributes(fileName, FileAttributes.Normal); //Copy will keep the attributes of addressFileName, so we need to add write permissions to fileName just in case of addressFileName is a read-only file.

            //check again
            if (!File.Exists(fileName))
            {
                ret.Msg = ResUI.FailedGenDefaultConfiguration;
                return ret;
            }

            ret.Msg = string.Format(ResUI.SuccessfulConfiguration, "");
            ret.Success = true;
            return await Task.FromResult(ret);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            ret.Msg = ResUI.FailedGenDefaultConfiguration;
            return ret;
        }
    }

    public static async Task<RetResult> GenerateClientSpeedtestConfig(Config config, string fileName, List<ServerTestItem> selecteds, ECoreType coreType)
    {
        var result = new RetResult();
        var dummyNode = new ProfileItem
        {
            CoreType = coreType
        };
        var builderResult = await CoreConfigContextBuilder.Build(config, dummyNode);
        var context = builderResult.Context;
        foreach (var testItem in selecteds)
        {
            var node = testItem.Profile;
            var (actNode, _) = await CoreConfigContextBuilder.ResolveNodeAsync(context, node, true);
            if (node.IndexId == actNode.IndexId)
            {
                continue;
            }
            context.ServerTestItemMap[node.IndexId] = actNode.IndexId;
        }
        if (coreType == ECoreType.Xray)
        {
            result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(selecteds);
        }
        if (result.Success != true)
        {
            return result;
        }
        await File.WriteAllTextAsync(fileName, result.Data.ToString());
        return result;
    }

    public static async Task<RetResult> GenerateClientSpeedtestConfig(Config config, CoreConfigContext context, ServerTestItem testItem, string fileName)
    {
        var result = new RetResult();
        var initPort = AppManager.Instance.GetLocalPort(EInboundProtocol.speedtest);
        var port = Utils.GetFreePort(initPort + testItem.QueueNum);
        testItem.Port = port;

        if (context.RunCoreType == ECoreType.openvpn)
        {
            result.Msg = "OpenVPN profiles do not expose a local SOCKS speed-test listener.";
            return result;
        }

        result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(port);
        if (result.Success != true)
        {
            return result;
        }

        await File.WriteAllTextAsync(fileName, result.Data.ToString());
        return result;
    }
}
