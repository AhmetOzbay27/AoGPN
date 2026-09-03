namespace ServiceLib.Common;

/// <summary>
/// User-facing protocol strategies. "Mimic" is intentionally a strategy label for
/// Xray/sing-box Reality/TLS profiles; it is not presented as a new wire protocol.
/// </summary>
public static class ConnectionProtocolPreference
{
    public const string Automatic = "auto";
    public const string WireGuard = "wireguard";
    public const string Mimic = "mimic";
    public const string Hysteria2 = "hysteria2";
    public const string OpenVpn = "openvpn";

    public static readonly IReadOnlyList<string> All =
    [Automatic, WireGuard, Mimic, Hysteria2, OpenVpn];

    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            WireGuard or "wg" => WireGuard,
            Mimic or "reality" or "xray-reality" => Mimic,
            Hysteria2 or "hysteria" or "tuic" or "quic" => Hysteria2,
            OpenVpn or "open-vpn" or "ovpn" => OpenVpn,
            _ => Automatic,
        };
    }
}

public sealed record ConnectionProtocolDecision(
    string Preference,
    EConfigType? SelectedConfigType,
    ECoreType? SelectedCoreType,
    bool IsAvailable,
    bool UsesUdp,
    string Reason,
    ProfileItem? SelectedProfile = null);

/// <summary>
/// Chooses only from protocols that the selected profile can actually use. A protocol
/// preference never rewrites credentials or converts one wire protocol into another.
/// </summary>
public static class ConnectionProtocolPolicy
{
    public static bool Matches(ProfileItem profile, string? preference)
    {
        var normalized = ConnectionProtocolPreference.Normalize(preference);
        return normalized switch
        {
            ConnectionProtocolPreference.Automatic => true,
            ConnectionProtocolPreference.WireGuard => profile.ConfigType == EConfigType.WireGuard,
            ConnectionProtocolPreference.OpenVpn => profile.ConfigType == EConfigType.OpenVPN
                || (profile.ConfigType == EConfigType.Custom && profile.CoreType == ECoreType.openvpn),
            ConnectionProtocolPreference.Hysteria2 => profile.ConfigType is EConfigType.Hysteria2 or EConfigType.TUIC,
            ConnectionProtocolPreference.Mimic => IsMimicProfile(profile),
            _ => false,
        };
    }

    public static bool IsMimicProfile(ProfileItem profile)
    {
        if (profile.ConfigType is not (EConfigType.VLESS or EConfigType.Trojan or EConfigType.VMess))
        {
            return false;
        }

        var security = profile.StreamSecurity?.TrimEx().ToLowerInvariant();
        var transport = profile.GetTransportExtra();
        var protocol = profile.GetProtocolExtra();

        // Reality/TLS with a browser fingerprint is the supported obfuscation
        // strategy. "Mimic" itself is not a separate Xray or sing-box core.
        return security is Global.StreamSecurityReality or Global.StreamSecurity
            && (profile.Fingerprint.IsNotEmpty()
                || profile.Sni.IsNotEmpty()
                || protocol.Flow?.Contains("vision", StringComparison.OrdinalIgnoreCase) == true
                || transport.Host.IsNotEmpty());
    }

    /// <summary>
    /// Returns a deterministic score for automatic selection. Lower latency is applied
    /// by <see cref="SelectBest"/> when a delay map is supplied; protocol capability is
    /// always the primary signal.
    /// </summary>
    public static int Score(ProfileItem profile)
    {
        if (profile.ConfigType == EConfigType.WireGuard)
        {
            return 100;
        }

        if (profile.ConfigType is EConfigType.Hysteria2 or EConfigType.TUIC)
        {
            return 96;
        }

        if (IsMimicProfile(profile))
        {
            return 92;
        }

        if (profile.ConfigType == EConfigType.OpenVPN)
        {
            return profile.GetProtocolExtra().Flow?.Equals("udp", StringComparison.OrdinalIgnoreCase) == true ? 84 : 70;
        }

        return profile.ConfigType switch
        {
            EConfigType.Trojan or EConfigType.VLESS => 80,
            EConfigType.VMess => 68,
            EConfigType.Shadowsocks => 64,
            EConfigType.Anytls or EConfigType.Naive => 62,
            EConfigType.SOCKS or EConfigType.HTTP => 35,
            _ => 20,
        };
    }

    public static ProfileItem? SelectBest(
        IEnumerable<ProfileItem> profiles,
        string? preference = null,
        IReadOnlyDictionary<string, int>? delays = null)
    {
        var normalized = ConnectionProtocolPreference.Normalize(preference);
        return profiles
            .Where(profile => profile != null && Matches(profile, normalized))
            .OrderByDescending(Score)
            .ThenBy(profile => GetDelay(delays, profile.IndexId))
            .ThenBy(profile => profile.Remarks, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static ConnectionProtocolDecision Evaluate(
        ProfileItem? selectedProfile,
        string? preference,
        bool transportUsesTun)
    {
        var normalized = ConnectionProtocolPreference.Normalize(preference);
        if (selectedProfile is null)
        {
            return new(
                normalized,
                null,
                null,
                false,
                false,
                "No VPN profile is selected.");
        }

        if (!Matches(selectedProfile, normalized))
        {
            return new(
                normalized,
                selectedProfile.ConfigType,
                selectedProfile.CoreType,
                false,
                UsesUdp(selectedProfile),
                $"The selected profile ({selectedProfile.ConfigType}) does not support the '{normalized}' strategy.",
                selectedProfile);
        }

        var reason = normalized switch
        {
            ConnectionProtocolPreference.Automatic => "Automatic mode keeps the selected profile and uses its native core.",
            ConnectionProtocolPreference.WireGuard => "WireGuard is selected for low overhead and native UDP performance.",
            ConnectionProtocolPreference.Mimic => "Mimic uses the profile's supported Xray/sing-box Reality or TLS fingerprinting.",
            ConnectionProtocolPreference.Hysteria2 => "Hysteria2/TUIC is selected for lossy networks and UDP congestion control.",
            ConnectionProtocolPreference.OpenVpn => "OpenVPN uses its native TUN client and preserves the provider profile.",
            _ => "The selected protocol is available.",
        };

        return new(
            normalized,
            selectedProfile.ConfigType,
            selectedProfile.CoreType,
            true,
            UsesUdp(selectedProfile),
            transportUsesTun && selectedProfile.ConfigType == EConfigType.OpenVPN
                ? reason + " OpenVPN owns the tunnel capture."
                : reason,
            selectedProfile);
    }

    public static bool UsesUdp(ProfileItem profile)
    {
        if (profile.ConfigType is EConfigType.Hysteria2 or EConfigType.TUIC or EConfigType.WireGuard)
        {
            return true;
        }

        return profile.GetProtocolExtra().Flow?.Equals("udp", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int GetDelay(IReadOnlyDictionary<string, int>? delays, string indexId)
    {
        return delays?.TryGetValue(indexId, out var delay) == true && delay >= 0 ? delay : int.MaxValue;
    }
}
