using System.Collections.Concurrent;
using MaxMind.Db;

namespace ServiceLib.Common;

/// <summary>Result of a single IP lookup. Empty fields mean "unknown".</summary>
public sealed record GeoIpInfo(
    string CountryCode,
    string CountryName,
    long AsnNumber,
    string AsnOrg,
    bool IsPrivate)
{
    public static readonly GeoIpInfo Unknown = new(string.Empty, string.Empty, 0, string.Empty, false);
    public static readonly GeoIpInfo Local = new(string.Empty, string.Empty, 0, string.Empty, true);
}

/// <summary>
/// Reads the geo databases shipped next to the cores ("bin" folder):
///   Country.mmdb         - MaxMind format country database (Loyalsoldier release),
///                           optionally with ASN inside "traits" (genuine MaxMind binaries).
///   GeoLite2-ASN.mmdb    - optional ASN database (e.g. from the geo update flow).
/// Both files are optional; lookups degrade gracefully when a file is missing.
/// The readers are created once and reused (thread-safe), and results are cached per IP.
/// </summary>
public static class GeoIpLookupService
{
    private static readonly object _sync = new();
    private static readonly ConcurrentDictionary<string, GeoIpInfo> _cache = new(StringComparer.Ordinal);
    private const int MaxCacheEntries = 8192;
    private static Reader? _countryReader;
    private static Reader? _asnReader;
    private static bool _initialized;
    private static DateTime _nextFileCheck = DateTime.MinValue;

    public static GeoIpInfo Lookup(IPAddress? address)
    {
        if (address is null)
        {
            return GeoIpInfo.Unknown;
        }
        if (IsPrivateOrLocal(address))
        {
            return GeoIpInfo.Local;
        }

        var key = address.ToString();
        // Bound the cache: a long-lived monitor sees many distinct remote IPs, and the
        // hot ones get re-cached immediately after a clear.
        if (_cache.Count > MaxCacheEntries)
        {
            _cache.Clear();
        }
        return _cache.GetOrAdd(key, _ => Resolve(address));
    }

    private static GeoIpInfo Resolve(IPAddress address)
    {
        EnsureReaders();

        var info = new GeoIpInfo(string.Empty, string.Empty, 0, string.Empty, false);
        try
        {
            Dictionary<string, object>? record = null;
            if (_countryReader is { } countryReader)
            {
                record = countryReader.Find<Dictionary<string, object>>(address);
                if (record is not null)
                {
                    var country = GetMap(record, "country") ?? GetMap(record, "registered_country");
                    if (country is not null)
                    {
                        info = info with
                        {
                            CountryCode = GetString(country, "iso_code") ?? string.Empty,
                            CountryName = GetCountryName(country),
                        };
                    }
                }

                // Genuine MaxMind GeoLite2-Country binaries carry ASN inside "traits".
                if (info.AsnNumber == 0)
                {
                    var traits = GetMap(record, "traits");
                    if (traits is not null)
                    {
                        info = info with
                        {
                            AsnNumber = GetLong(traits, "autonomous_system_number"),
                            AsnOrg = GetString(traits, "autonomous_system_organization") ?? string.Empty,
                        };
                    }
                }
            }

            if (_asnReader is { } asnReader)
            {
                var asnRecord = asnReader.Find<Dictionary<string, object>>(address);
                if (asnRecord is not null)
                {
                    info = info with
                    {
                        AsnNumber = GetLong(asnRecord, "autonomous_system_number"),
                        AsnOrg = GetString(asnRecord, "autonomous_system_organization") ?? string.Empty,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("GeoIpLookupService", ex);
        }

        return info;
    }

    private static void EnsureReaders()
    {
        var now = DateTime.UtcNow;
        if (_initialized && now < _nextFileCheck)
        {
            return;
        }

        lock (_sync)
        {
            if (_initialized && now < _nextFileCheck)
            {
                return;
            }

            var countryPath = Utils.GetBinPath("Country.mmdb");
            var asnPath = Utils.GetBinPath("GeoLite2-ASN.mmdb");

            if (!_initialized)
            {
                _countryReader = TryOpenReader(countryPath);
                _asnReader = TryOpenReader(asnPath);
                _initialized = true;
            }
            else
            {
                // A geo update may have added a file while the app is running.
                _countryReader ??= TryOpenReader(countryPath);
                _asnReader ??= TryOpenReader(asnPath);
            }

            _nextFileCheck = now.AddSeconds(60);
        }
    }

    private static Reader? TryOpenReader(string path)
    {
        try
        {
            return File.Exists(path) ? new Reader(path) : null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"GeoIpLookupService open {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    /// <summary>Converts an ISO-3166 alpha-2 code to a regional-indicator flag emoji.</summary>
    public static string CountryFlag(string? countryCode)
    {
        if (countryCode is null or { Length: not 2 })
        {
            return string.Empty;
        }

        // Non-country codes that must never render as a flag.
        if (countryCode is "EU" or "AP" or "A1" or "A2" or "O1" or "ZZ" or "None")
        {
            return string.Empty;
        }

        var sb = new StringBuilder(4);
        foreach (var c in countryCode.ToUpperInvariant())
        {
            sb.Append(char.ConvertFromUtf32(0x1F1E6 + (c - 'A')));
        }
        return sb.ToString();
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6Teredo)
        {
            return true;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            var first = bytes[0];
            var second = bytes[1];
            return first == 0
                || first == 10
                || first == 127
                || (first == 100 && second is >= 64 and <= 127)
                || (first == 169 && second == 254)
                || (first == 172 && second is >= 16 and <= 31)
                || (first == 192 && second == 0)
                || (first == 192 && second == 168)
                || (first == 198 && second is 18 or 19)
                || (first == 198 && second == 51)
                || (first == 203 && second == 0);
        }

        // fc00::/7 (unique local addressing) is not covered by IsIPv6SiteLocal.
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static Dictionary<string, object>? GetMap(Dictionary<string, object>? record, string key)
    {
        if (record is null || !record.TryGetValue(key, out var value) || value is not Dictionary<string, object> map)
        {
            return null;
        }
        return map;
    }

    private static string? GetString(Dictionary<string, object>? map, string key)
    {
        if (map is null || !map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }
        return value is string s ? s : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long GetLong(Dictionary<string, object>? map, string key)
    {
        if (map is null || !map.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }
        try
        {
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static string GetCountryName(Dictionary<string, object> country)
    {
        if (GetMap(country, "names") is { } names)
        {
            foreach (var language in new[] { "en", "ru", "zh-CN" })
            {
                if (GetString(names, language) is { Length: > 0 } name)
                {
                    return name;
                }
            }

            foreach (var value in names.Values)
            {
                if (value is string s && s.Length > 0)
                {
                    return s;
                }
            }
        }
        return string.Empty;
    }
}
