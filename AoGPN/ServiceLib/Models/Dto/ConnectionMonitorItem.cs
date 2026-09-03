namespace ServiceLib.Models.Dto;

public class ConnectionMonitorItem
{
    /// <summary>Executable name, e.g. "chrome.exe".</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Human friendly name shown in the list.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Full path to the executable, used to extract the icon. May be empty.</summary>
    public string ExePath { get; set; } = "";

    public int Pid { get; set; }
    public string Protocol { get; set; } = "TCP";
    public string LocalAddress { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string State { get; set; } = "";

    /// <summary>proxy / direct / block / "" (unknown).</summary>
    public string RouteTag { get; set; } = "";

    /// <summary>Localized route label.</summary>
    public string RouteText { get; set; } = "";

    /// <summary>ISO-3166 alpha-2 country code of the remote address, e.g. "TR".</summary>
    public string CountryCode { get; set; } = "";

    /// <summary>Country name, e.g. "Turkey". Used as tooltip.</summary>
    public string CountryName { get; set; } = "";

    /// <summary>Autonomous system number of the remote address, 0 when unknown.</summary>
    public long AsnNumber { get; set; }

    /// <summary>Autonomous system organization, e.g. "Cloudflare, Inc.".</summary>
    public string AsnOrg { get; set; } = "";

    /// <summary>True when the remote address is a private/local range.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Flag emoji for <see cref="CountryCode"/>, empty when unknown.</summary>
    public string Flag => GeoIpLookupService.CountryFlag(CountryCode);

    /// <summary>Display text for the country column (flag + code, or localized "Local").</summary>
    public string CountryText { get; set; } = "";

    /// <summary>Display text for the ASN column, e.g. "AS13335 Cloudflare, Inc.".</summary>
    public string AsnText { get; set; } = "";
}
