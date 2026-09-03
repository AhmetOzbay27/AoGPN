namespace ServiceLib.Common;

/// <summary>
/// Parses and classifies manual route values: domains, IP addresses / CIDR blocks and
/// optional ":port" suffixes. Shared by every add flow and the edit dialog so they all
/// behave identically.
/// </summary>
public static class ManualRouteParser
{
    /// <summary>
    /// Splits a trailing ":port" off a host ("discord.gg:443" → host + "443").
    /// IPv6 literals, with or without brackets, are recognized and never split.
    /// </summary>
    public static bool TrySplitPort(string value, out string host, out string port)
    {
        host = value;
        port = "";
        if (value.IsNullOrEmpty())
        {
            return false;
        }

        // Bracketed IPv6 with port: "[2001:db8::1]:443".
        if (value[0] == '[')
        {
            var end = value.IndexOf(']');
            if (end > 1 && IPAddress.TryParse(value[1..end], out _))
            {
                host = value[1..end];
                var rest = value[(end + 1)..];
                if (rest.StartsWith(':') && IsPort(rest[1..], out port))
                {
                    // port parsed
                }
                return true;
            }
        }

        // Plain IPv6 — contains colons but parses as an address, so no port split.
        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }

        var idx = value.LastIndexOf(':');
        if (idx > 0 && idx < value.Length - 1 && IsPort(value[(idx + 1)..], out port))
        {
            host = value[..idx];
            return true;
        }

        port = "";
        return true;
    }

    /// <summary>Classifies a bare host as an IP entry ("ip") or a domain entry ("domain").</summary>
    public static string ClassifyEntryType(string host)
    {
        if (host.Contains('/')) // CIDR, e.g. 10.0.0.0/24
        {
            return "ip";
        }
        if (IPAddress.TryParse(host, out _))
        {
            return "ip";
        }
        return "domain";
    }

    /// <summary>Validates a host for the given entry type ("app" | "domain" | "ip").</summary>
    public static bool IsValidHost(string host, string entryType)
    {
        if (host.IsNullOrEmpty() || host.Contains(' '))
        {
            return false;
        }
        return entryType switch
        {
            "domain" => host.Contains('.') && !host.Contains('/') && !host.Contains('\\') && !host.Contains(':'),
            "ip" => host.Contains('/') ? IsCidr(host) : IPAddress.TryParse(host, out _),
            _ => true, // apps are validated by the caller (no path separators)
        };
    }

    private static bool IsPort(string text, out string port)
    {
        port = "";
        if (int.TryParse(text, out var p) && p is >= 1 and <= 65535)
        {
            port = p.ToString();
            return true;
        }
        return false;
    }

    private static bool IsCidr(string value)
    {
        var idx = value.IndexOf('/');
        if (idx <= 0 || idx == value.Length - 1)
        {
            return false;
        }
        if (!IPAddress.TryParse(value[..idx], out var addr)
            || !int.TryParse(value[(idx + 1)..], out var prefix))
        {
            return false;
        }
        var maxPrefix = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        return prefix >= 0 && prefix <= maxPrefix;
    }
}
