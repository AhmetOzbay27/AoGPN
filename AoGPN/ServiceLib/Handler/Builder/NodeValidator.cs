namespace ServiceLib.Handler.Builder;

public record NodeValidatorResult(List<string> Errors, List<string> Warnings)
{
    public bool Success => Errors.Count == 0;

    public static NodeValidatorResult Empty()
    {
        return new NodeValidatorResult([], []);
    }
}

public class NodeValidator
{
    // Static validator rules (mihomo — Global VPN çekirdeği)
    private static readonly HashSet<string> MihomoUnsupportedTransports =
        [nameof(ETransport.kcp), nameof(ETransport.xhttp), nameof(ETransport.httpupgrade)];

    private static readonly HashSet<EConfigType> MihomoTransportSupportedProtocols =
        [EConfigType.VMess, EConfigType.VLESS, EConfigType.Trojan];

    public static NodeValidatorResult Validate(ProfileItem item, ECoreType coreType)
    {
        var v = new ValidationContext();
        ValidateNodeAndCoreSupport(item, coreType, v);
        return v.ToResult();
    }

    private static void ValidateNodeAndCoreSupport(ProfileItem item, ECoreType coreType, ValidationContext v)
    {
        if (item.ConfigType is EConfigType.Custom)
        {
            return;
        }

        if (item.ConfigType.IsGroupType())
        {
            // Group logic is handled in ValidateGroupNode
            return;
        }

        // Basic Property Validation
        v.Assert(!item.Address.IsNullOrEmpty(), string.Format(ResUI.MsgInvalidProperty, ResUI.TbAddress));
        v.Assert(item.Port is > 0 and <= 65535, string.Format(ResUI.MsgInvalidProperty, ResUI.TbPort));

        // Network & Core Logic
        var net = item.GetNetwork();
        if (coreType == ECoreType.mihomo)
        {
            var transportError = ValidateMihomoTransport(item.ConfigType, net);
            if (transportError != null)
            {
                v.Error(transportError);
            }

            if (!Global.MihomoSupportConfigType.Contains(item.ConfigType))
            {
                v.Error(string.Format(ResUI.MsgCoreNotSupportProtocol, nameof(ECoreType.mihomo), item.ConfigType));
            }
        }
        else if (coreType is ECoreType.Xray)
        {
            if (!Global.XraySupportConfigType.Contains(item.ConfigType))
            {
                v.Error(string.Format(ResUI.MsgCoreNotSupportProtocol, nameof(ECoreType.Xray), item.ConfigType));
            }
        }

        // Protocol Specifics
        var protocolExtra = item.GetProtocolExtra();
        switch (item.ConfigType)
        {
            case EConfigType.VMess:
                v.Assert(!item.Password.IsNullOrEmpty() && Utils.IsGuidByParse(item.Password),
                    string.Format(ResUI.MsgInvalidProperty, ResUI.TbId));
                break;

            case EConfigType.VLESS:
                v.Assert(
                    !item.Password.IsNullOrEmpty()
                    && (Utils.IsGuidByParse(item.Password) || item.Password.Length <= 30),
                    string.Format(ResUI.MsgInvalidProperty, ResUI.TbId5)
                );
                v.Assert(Global.Flows.Contains(protocolExtra.Flow ?? string.Empty),
                    string.Format(ResUI.MsgInvalidProperty, ResUI.TbFlow5));
                break;

            case EConfigType.Shadowsocks:
                v.Assert(!item.Password.IsNullOrEmpty(), string.Format(ResUI.MsgInvalidProperty, ResUI.TbId3));
                v.Assert(
                    !string.IsNullOrEmpty(protocolExtra.SsMethod) &&
                    Global.SsSecuritiesInMihomo.Contains(protocolExtra.SsMethod),
                    string.Format(ResUI.MsgInvalidProperty, ResUI.TbSecurity3));
                break;
        }

        if (coreType is ECoreType.Xray
            && (protocolExtra.Flow ?? string.Empty).StartsWith("xtls", StringComparison.OrdinalIgnoreCase)
            && item.MuxEnabled == true)
        {
            v.Warning(string.Format(ResUI.MsgOptionsConflict, "XTLS", "Mux.Cool"));
        }

        if (item.GetNetwork() is nameof(ETransport.ws)
            && item.EchConfigList.IsNullOrEmpty()
            && item.GetAlpn()?.FirstOrDefault() == "h3")
        {
            v.Warning(
                "WebSocket but ALPN is set to h3, the core may ignore the ALPN setting or cause unexpected issues.");
        }

        // TLS & Security
        if (item.StreamSecurity == Global.StreamSecurity)
        {
            var isCertProvided = !item.Cert.IsNullOrEmpty();
            if (!item.Cert.IsNullOrEmpty() && CertPemManager.ParsePemChain(item.Cert).Count == 0)
            {
                v.Error(string.Format(ResUI.MsgInvalidProperty, ResUI.TbFullCertTips));
                isCertProvided = false;
            }

            // Check for deprecated allowInsecure property when TLS is enabled
            if (item.GetAllowInsecure()
                && item.Cert.IsNullOrEmpty()
                && item.CertSha.IsNullOrEmpty())
            {
                v.Warning(ResUI.MsgAllowInsecureDeprecated);
            }

            if (coreType is ECoreType.Xray or ECoreType.mihomo
                && item.GetAllowInsecure()
                && !isCertProvided)
            {
                v.Warning(ResUI.MsgInsecureConfiguration);
            }
        }

        if (item.StreamSecurity == Global.StreamSecurityReality)
        {
            v.Assert(!item.PublicKey.IsNullOrEmpty(), string.Format(ResUI.MsgInvalidProperty, ResUI.TbPublicKey));
        }

        var transport = item.GetTransportExtra();
        if (item.Network == nameof(ETransport.xhttp) && !transport.XhttpExtra.IsNullOrEmpty())
        {
            if (JsonUtils.ParseJson(transport.XhttpExtra) is not JsonObject)
            {
                v.Error(string.Format(ResUI.MsgInvalidProperty, ResUI.TransportExtra));
            }
        }

        if (!item.Finalmask.IsNullOrEmpty())
        {
            if (JsonUtils.ParseJson(item.Finalmask) is not JsonObject)
            {
                v.Error(string.Format(ResUI.MsgInvalidProperty, ResUI.TbFinalmask));
            }
        }
    }

    private static string? ValidateMihomoTransport(EConfigType configType, string net)
    {
        // mihomo does not support kcp / xhttp / httpupgrade transports
        if (MihomoUnsupportedTransports.Contains(net))
        {
            return string.Format(ResUI.MsgCoreNotSupportNetwork, nameof(ECoreType.mihomo), net);
        }

        // mihomo supports non-tcp transports only for vmess/trojan/vless
        if (!MihomoTransportSupportedProtocols.Contains(configType) && net != nameof(ETransport.raw))
        {
            return string.Format(ResUI.MsgCoreNotSupportProtocolTransport,
                nameof(ECoreType.mihomo), configType.ToString(), net);
        }

        return null;
    }

    private class ValidationContext
    {
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Error(string message)
        {
            Errors.Add(message);
        }

        public void Warning(string message)
        {
            Warnings.Add(message);
        }

        public void Assert(bool condition, string errorMsg)
        {
            if (!condition)
            {
                Error(errorMsg);
            }
        }

        public NodeValidatorResult ToResult()
        {
            return new NodeValidatorResult(Errors, Warnings);
        }
    }
}
