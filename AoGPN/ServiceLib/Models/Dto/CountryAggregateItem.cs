namespace ServiceLib.Models.Dto;

/// <summary>Aggregated connections for one country, consumed by the world map and the country list.</summary>
public class CountryAggregateItem
{
    /// <summary>ISO-3166 alpha-2 code; empty for private/local or unknown destinations.</summary>
    public string CountryCode { get; set; } = "";

    /// <summary>Country name, e.g. "Turkey". Used as tooltip.</summary>
    public string CountryName { get; set; } = "";

    public int ConnectionCount { get; set; }

    public int AppCount { get; set; }

    /// <summary>Comma separated ASN summary, e.g. "AS13335 Cloudflare, Inc.".</summary>
    public string AsnText { get; set; } = "";

    /// <summary>True when the destinations are private/local ranges.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Total bytes downloaded to destinations in this country, from the clash API.</summary>
    public long Download { get; set; }

    /// <summary>Total bytes uploaded to destinations in this country, from the clash API.</summary>
    public long Upload { get; set; }

    public string DownloadText => Utils.HumanFy(Download);

    public string UploadText => Utils.HumanFy(Upload);

    /// <summary>Combined traffic used to size and rank map dots; saturates at long.MaxValue.</summary>
    public long TrafficBytes
    {
        get
        {
            var download = Download < 0 ? 0 : Download;
            var upload = Upload < 0 ? 0 : Upload;
            return download > long.MaxValue - upload ? long.MaxValue : download + upload;
        }
    }

    /// <summary>Flag emoji for <see cref="CountryCode"/>, empty when unknown.</summary>
    public string Flag => GeoIpLookupService.CountryFlag(CountryCode);

    /// <summary>Localized display name shown in the country list and map labels.</summary>
    public string DisplayName { get; set; } = "";
}
