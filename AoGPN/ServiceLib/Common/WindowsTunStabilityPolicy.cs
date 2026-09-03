namespace ServiceLib.Common;

/// <summary>
/// Safe defaults for Windows Wintun/sing-box and Xray TUN operation. Jumbo MTUs are
/// intentionally rejected because they are a common source of fragmentation and game
/// disconnects on real WAN paths. User values inside the safe range are preserved.
/// </summary>
public static class WindowsTunStabilityPolicy
{
    public const int DefaultMtu = 1408;
    public const int MinimumSafeMtu = 1280;
    public const int MaximumSafeMtu = 1500;
    public const string DefaultStack = "mixed";

    public static bool IsSafeMtu(int mtu) => mtu is >= MinimumSafeMtu and <= MaximumSafeMtu;

    public static int NormalizeMtu(int mtu)
    {
        return IsSafeMtu(mtu) ? mtu : DefaultMtu;
    }

    public static string NormalizeStack(string? stack)
    {
        return Global.TunStacks.Contains(stack ?? string.Empty) ? stack! : DefaultStack;
    }

    public static void Apply(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.TunModeItem ??= new TunModeItem();
        config.TunModeItem.Mtu = NormalizeMtu(config.TunModeItem.Mtu);
        config.TunModeItem.Stack = NormalizeStack(config.TunModeItem.Stack);

        // These are the fail-safe route settings. They prevent traffic from escaping
        // the TUN when the core is active; the independent system proxy remains a
        // separate preference and is not modified here.
        config.TunModeItem.AutoRoute = true;
        config.TunModeItem.StrictRoute = true;
        config.TunModeItem.EnableLegacyProtect = true;
    }
}
