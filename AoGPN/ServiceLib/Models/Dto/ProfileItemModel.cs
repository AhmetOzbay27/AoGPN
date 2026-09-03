namespace ServiceLib.Models.Dto;

[Serializable]
public class ProfileItemModel : ReactiveObject
{
    public bool IsActive { get; set; }
    public string IndexId { get; set; }
    public EConfigType ConfigType { get; set; }
    public string Remarks { get; set; }
    public string Address { get; set; }
    public int Port { get; set; }
    public string Network { get; set; }
    public string StreamSecurity { get; set; }
    public string Subid { get; set; }
    public string SubRemarks { get; set; }
    public int Sort { get; set; }

    /// <summary>Country flag emoji derived from Address via GeoIP lookup, or empty if unknown.</summary>
    public string CountryFlag => ResolveCountryFlag();

    /// <summary>Resolved ISO 3166 alpha-2 country code (lazy, from Address via GeoIP).</summary>
    public string CountryCode => ResolveCountryCode();

    private string? _cachedCountryCode;
    private bool _countryCodeResolved;

    private string ResolveCountryCode()
    {
        if (_countryCodeResolved)
        {
            return _cachedCountryCode ?? string.Empty;
        }
        _countryCodeResolved = true;

        if (string.IsNullOrEmpty(Address))
        {
            return string.Empty;
        }

        if (System.Net.IPAddress.TryParse(Address, out var ip))
        {
            var geo = GeoIpLookupService.Lookup(ip);
            _cachedCountryCode = geo.CountryCode;
            return geo.CountryCode;
        }

        return string.Empty;
    }

    private string ResolveCountryFlag()
    {
        return GeoIpLookupService.CountryFlag(ResolveCountryCode());
    }

    [Reactive]
    public int Delay { get; set; }

    public decimal Speed { get; set; }

    [Reactive]
    public string DelayVal { get; set; }

    [Reactive]
    public string SpeedVal { get; set; }

    [Reactive]
    public string IpInfo { get; set; }

    [Reactive]
    public string TodayUp { get; set; }

    [Reactive]
    public string TodayDown { get; set; }

    [Reactive]
    public string TotalUp { get; set; }

    [Reactive]
    public string TotalDown { get; set; }

    public string GetSummary()
    {
        var summary = $"[{ConfigType}] {Remarks}";
        if (!ConfigType.IsComplexType())
        {
            summary += $"({Address}:{Port})";
        }

        return summary;
    }
}
