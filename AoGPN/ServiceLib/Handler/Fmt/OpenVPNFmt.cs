namespace ServiceLib.Handler.Fmt;

public class OpenVPNFmt : BaseFmt
{
    public static ProfileItem? Resolve(string str, out string msg)
    {
        msg = ResUI.ConfigurationFormatIncorrect;

        ProfileItem item = new()
        {
            ConfigType = EConfigType.OpenVPN,
            CoreType = ECoreType.openvpn
        };

        var url = Utils.TryUri(str);
        if (url == null)
        {
            return null;
        }

        item.Address = url.IdnHost;
        item.Port = url.Port > 0 ? url.Port : 1194;
        item.Remarks = url.GetComponents(UriComponents.Fragment, UriFormat.Unescaped);

        var userInfo = Utils.UrlDecode(url.UserInfo);
        if (userInfo.IsNotEmpty())
        {
            item.Username = userInfo;
        }

        var query = Utils.ParseQueryString(url.Query);

        item.Password = GetQueryDecoded(query, "password");
        item.StreamSecurity = GetQueryDecoded(query, "security") ?? string.Empty;

        var protoExtra = item.GetProtocolExtra();
        protoExtra = protoExtra with
        {
            Flow = GetQueryDecoded(query, "proto") ?? "udp",  // udp or tcp
            SsMethod = GetQueryDecoded(query, "cipher") ?? "AES-256-GCM",
            CongestionControl = GetQueryDecoded(query, "auth") ?? "SHA256",
            VlessEncryption = GetQueryDecoded(query, "compression") ?? "none",
        };

        var tlsAuth = GetQueryDecoded(query, "tls-auth");
        if (tlsAuth.IsNotEmpty())
        {
            protoExtra = protoExtra with
            {
                SalamanderPass = tlsAuth
            };
        }

        item.SetProtocolExtra(protoExtra);

        // Transport: TCP or UDP
        item.Network = protoExtra.Flow == "tcp" ? nameof(ETransport.raw) : nameof(ETransport.raw);

        // Proxy support through OpenVPN
        var proxyType = GetQueryDecoded(query, "proxy-type");
        if (proxyType.IsNotEmpty())
        {
            item.SetTransportExtra(item.GetTransportExtra() with
            {
                Host = GetQueryDecoded(query, "proxy-host"),
                Path = GetQueryDecoded(query, "proxy-port"),
            });
        }

        if (item.Remarks.IsNullOrEmpty())
        {
            item.Remarks = $"{nameof(EConfigType.OpenVPN)} {item.Address}:{item.Port}";
        }

        msg = string.Empty;
        return item;
    }

    public static string? ToUri(ProfileItem? item)
    {
        if (item == null)
        {
            return null;
        }

        var remark = string.Empty;
        if (item.Remarks.IsNotEmpty())
        {
            remark = "#" + Utils.UrlEncode(item.Remarks);
        }

        var protoExtra = item.GetProtocolExtra();
        var dicQuery = new Dictionary<string, string>
        {
            { "proto", protoExtra.Flow ?? "udp" },
            { "cipher", protoExtra.SsMethod ?? "AES-256-GCM" },
            { "auth", protoExtra.CongestionControl ?? "SHA256" }
        };

        if (item.Password.IsNotEmpty())
        {
            dicQuery["password"] = Utils.UrlEncode(item.Password);
        }
        if (protoExtra.SalamanderPass.IsNotEmpty())
        {
            dicQuery["tls-auth"] = Utils.UrlEncode(protoExtra.SalamanderPass);
        }
        if (protoExtra.VlessEncryption.IsNotEmpty() && protoExtra.VlessEncryption != "none")
        {
            dicQuery["compression"] = Utils.UrlEncode(protoExtra.VlessEncryption);
        }
        if (item.StreamSecurity.IsNotEmpty())
        {
            dicQuery["security"] = Utils.UrlEncode(item.StreamSecurity);
        }

        return ToUri(EConfigType.OpenVPN, item.Address, item.Port,
            item.Username.IsNotEmpty() ? item.Username : string.Empty,
            dicQuery, remark);
    }

    /// <summary>
    /// Parse an .ovpn config file and extract profile items
    /// </summary>
    public static List<ProfileItem>? ResolveConfig(string strData)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = strData.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string? currentSection = null;
        var sectionData = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.IsNullOrEmpty() || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            // Section headers like [client]
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1];
                if (!sectionData.ContainsKey(currentSection))
                    sectionData[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eqIdx = line.IndexOf(' ');
            if (eqIdx <= 0) continue;

            var key = line[..eqIdx].Trim();
            var value = line[(eqIdx + 1)..].Trim().Trim('"');

            if (currentSection != null && sectionData.TryGetValue(currentSection, out var sec))
            {
                sec[key] = value;
            }
            else
            {
                config[key] = value;
            }
        }

        if (!config.TryGetValue("remote", out var remote) || remote.IsNullOrEmpty())
            return null;

        // Parse remote: "host port [proto]"
        var remoteParts = remote.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (remoteParts.Length < 2) return null;

        var address = remoteParts[0];
        var port = int.TryParse(remoteParts[1], out var p) ? p : 1194;
        var proto = remoteParts.Length > 2 ? remoteParts[2].ToLower() : "udp";

        var protoExtra = new ProtocolExtraItem
        {
            Flow = proto,
            SsMethod = config.TryGetValue("cipher", out var cipher) ? cipher : "AES-256-GCM",
            CongestionControl = config.TryGetValue("auth", out var auth) ? auth : "SHA256",
            VlessEncryption = config.TryGetValue("compress", out var comp) ? comp : null,
            SalamanderPass = config.TryGetValue("tls-auth", out var tlsAuth) ? tlsAuth.Split(' ')[0] : null,
        };

        var item = new ProfileItem
        {
            ConfigType = EConfigType.OpenVPN,
            CoreType = ECoreType.openvpn,
            Remarks = config.TryGetValue("setenv", out var setenv) && setenv.Contains("friendly_name")
                ? setenv.Split(' ').LastOrDefault() ?? $"OpenVPN {address}"
                : $"OpenVPN {address}",
            Address = address,
            Port = port,
            Username = config.TryGetValue("auth-user-pass", out var _) ? string.Empty : string.Empty,
            Password = config.TryGetValue("key-direction", out var _) ? string.Empty : string.Empty,
        };

        if (config.TryGetValue("ca", out var ca) && ca.IsNotEmpty())
        {
            item.Cert = ca;
        }

        // Look for inline certificates
        if (sectionData.TryGetValue("inline", out var inlineSec))
        {
            // Store entire inline section as cert data
            var inlineParts = new List<string>();
            foreach (var kv in inlineSec)
            {
                inlineParts.Add($"<{kv.Key}>");
                inlineParts.Add(kv.Value);
                inlineParts.Add($"</{kv.Key}>");
            }
            item.Cert = string.Join("\n", inlineParts);
        }

        item.SetProtocolExtra(protoExtra);
        return [item];
    }

    /// <summary>
    /// Generate an .ovpn config file content from a ProfileItem
    /// </summary>
    public static string GenerateConfig(ProfileItem item)
    {
        var protoExtra = item.GetProtocolExtra();
        var transportExtra = item.GetTransportExtra();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# AoGPN OpenVPN Configuration");
        sb.AppendLine("# Generated automatically - do not edit");
        sb.AppendLine();
        sb.AppendLine("client");
        sb.AppendLine($"dev tun");
        sb.AppendLine($"proto {protoExtra.Flow ?? "udp"}");
        sb.AppendLine($"remote {item.Address} {item.Port}");
        sb.AppendLine("resolv-retry infinite");
        sb.AppendLine("nobind");
        sb.AppendLine("persist-key");
        sb.AppendLine("persist-tun");
        sb.AppendLine();

        if (protoExtra.SsMethod.IsNotEmpty())
            sb.AppendLine($"cipher {protoExtra.SsMethod}");

        if (protoExtra.CongestionControl.IsNotEmpty() && protoExtra.CongestionControl != "none")
            sb.AppendLine($"auth {protoExtra.CongestionControl}");

        if (protoExtra.VlessEncryption.IsNotEmpty() && protoExtra.VlessEncryption != "none")
            sb.AppendLine($"compress {protoExtra.VlessEncryption}");

        sb.AppendLine();

        // Authentication. An inline credential block keeps ProcessService from
        // waiting for an interactive stdin prompt when a URI supplied credentials.
        if (item.Username.IsNotEmpty() && item.Password.IsNotEmpty())
        {
            sb.AppendLine("<auth-user-pass>");
            sb.AppendLine(item.Username);
            sb.AppendLine(item.Password);
            sb.AppendLine("</auth-user-pass>");
        }

        // TLS auth
        if (protoExtra.SalamanderPass.IsNotEmpty())
        {
            sb.AppendLine($"tls-auth {protoExtra.SalamanderPass} 1");
            sb.AppendLine("key-direction 1");
        }

        // Certificate inline
        if (item.Cert.IsNotEmpty())
        {
            sb.AppendLine();
            sb.AppendLine(item.Cert);
        }

        sb.AppendLine();
        sb.AppendLine("verb 3");
        sb.AppendLine("mute 20");
        sb.AppendLine();

        // Gaming optimizations
        sb.AppendLine("# AoGPN Gaming Optimizations");
        sb.AppendLine("fast-io");
        sb.AppendLine("sndbuf 393216");
        sb.AppendLine("rcvbuf 393216");
        sb.AppendLine("push \"sndbuf 393216\"");
        sb.AppendLine("push \"rcvbuf 393216\"");

        return sb.ToString();
    }
}