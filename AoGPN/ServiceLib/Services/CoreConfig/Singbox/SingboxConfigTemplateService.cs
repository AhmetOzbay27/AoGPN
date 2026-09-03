namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private string ApplyFullConfigTemplate()
    {
        var fullConfigTemplate = context.FullConfigTemplate;
        if (fullConfigTemplate is not { Enabled: true })
        {
            return JsonUtils.Serialize(_coreConfig);
        }

        var fullConfigTemplateItem = context.IsTunEnabled ? fullConfigTemplate.TunConfig : fullConfigTemplate.Config;
        if (fullConfigTemplateItem.IsNullOrEmpty())
        {
            return JsonUtils.Serialize(_coreConfig);
        }

        var fullConfigTemplateNode = JsonNode.Parse(fullConfigTemplateItem);
        if (fullConfigTemplateNode == null)
        {
            return JsonUtils.Serialize(_coreConfig);
        }

        // Process outbounds
        var customOutboundsNode = fullConfigTemplateNode["outbounds"] as JsonArray ?? [];
        foreach (var outbound in _coreConfig.outbounds)
        {
            if (outbound.type.ToLower() is "direct" or "block")
            {
                if (fullConfigTemplate.AddProxyOnly == true)
                {
                    continue;
                }
            }
            else if (outbound.detour.IsNullOrEmpty() && !fullConfigTemplate.ProxyDetour.IsNullOrEmpty() && !Utils.IsPrivateNetwork(outbound.server ?? string.Empty))
            {
                outbound.detour = fullConfigTemplate.ProxyDetour;
            }
            customOutboundsNode.Add(JsonUtils.DeepCopy(outbound));
        }
        fullConfigTemplateNode["outbounds"] = customOutboundsNode;

        // Process endpoints
        if (_coreConfig.endpoints is { Count: > 0 })
        {
            var customEndpointsNode = fullConfigTemplateNode["endpoints"] as JsonArray ?? [];
            foreach (var endpoint in _coreConfig.endpoints)
            {
                if (endpoint.detour.IsNullOrEmpty() && !fullConfigTemplate.ProxyDetour.IsNullOrEmpty())
                {
                    endpoint.detour = fullConfigTemplate.ProxyDetour;
                }
                customEndpointsNode.Add(JsonUtils.DeepCopy(endpoint));
            }
            fullConfigTemplateNode["endpoints"] = customEndpointsNode;
        }

        return JsonUtils.Serialize(fullConfigTemplateNode);
    }

    /// <summary>
    /// TUN (auto_route + strict_route) aktifken sing-box'ın kendi soketlerinin TUN'a
    /// geri yakalanmasını (sonsuz döngü) önler. direct outbound'unu fiziksel NIC'e
    /// bağlar — böylece TUN'dan giren trafik direct'e düştüğünde paket wintun'a geri
    /// gitmez, doğrudan fiziksel ağdan çıkar (WSAEACCES "forbidden access" döngüsünü
    /// kırar). WireGuard endpoint'inin bağlaması BuildWireGuardEndpoint'te yapılır.
    /// </summary>
    private void ApplyTunLoopProtection()
    {
        if (!context.IsTunEnabled)
        {
            return;
        }
        var (physName, _) = Utils.GetPhysicalDefaultInterface();
        if (ApplyBindInterfaceToDirects(_coreConfig.outbounds, physName))
        {
            DiagLog.Write($"TUN_LOOP direct bind_interface={physName} (physical NIC bypass)");
        }
    }

    /// <summary>
    /// direct outbound'larına <paramref name="physName"/> arayüzünü bağlar (saf — ağ
    /// yok, test edilebilir). Yalnızca tip <c>direct</c> olan ve zaten bir
    /// bind_interface'i OLMAYAN outbound'ları değiştirir (kullanıcının bilinçli
    /// BindInterface seçimi ezilmez). Hiçbir değişiklik yapılmadıysa false döner
    /// (physName boş/outbounds null/uygun direct yok).
    /// </summary>
    internal static bool ApplyBindInterfaceToDirects(IEnumerable<Outbound4Sbox>? outbounds, string? physName)
    {
        if (physName.IsNullOrEmpty() || outbounds is null)
        {
            return false;
        }
        var changed = false;
        foreach (var outbound in outbounds)
        {
            if (outbound?.type == "direct" && outbound.bind_interface.IsNullOrEmpty())
            {
                outbound.bind_interface = physName;
                changed = true;
            }
        }
        return changed;
    }

    private void ApplyOutboundBindInterface()
    {
        var bindInterface = _config.CoreBasicItem.BindInterface?.TrimEx();
        if (bindInterface.IsNullOrEmpty())
        {
            return;
        }
        foreach (var outbound in _coreConfig.outbounds ?? [])
        {
            outbound.bind_interface = ShouldBindNet(outbound) ? bindInterface : null;
        }
    }

    private void ApplyOutboundSendThrough()
    {
        var sendThrough = _config.CoreBasicItem.SendThrough?.TrimEx();
        if (sendThrough.IsNullOrEmpty())
        {
            return;
        }

        foreach (var outbound in _coreConfig.outbounds ?? [])
        {
            outbound.inet4_bind_address = ShouldBindNet(outbound) ? sendThrough : null;
        }
    }

    private static bool ShouldBindNet(Outbound4Sbox outbound)
    {
        if (outbound.type is "direct" or "block" or "dns" or "selector" or "urltest")
        {
            return false;
        }

        if (!outbound.detour.IsNullOrEmpty())
        {
            return false;
        }

        var outboundAddress = outbound.server ?? string.Empty;

        if (outboundAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IPAddress.TryParse(outboundAddress, out var address) || !IPAddress.IsLoopback(address);
    }
}
