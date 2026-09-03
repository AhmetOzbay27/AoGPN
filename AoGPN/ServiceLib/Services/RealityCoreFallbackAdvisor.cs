using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models;

namespace ServiceLib.Services;

public enum RealityFallbackAdvice
{
    /// <summary>
    /// No suggestion: the node is not REALITY, the failed main core was not
    /// sing-box, the connection is up, or the routing mode is off.
    /// </summary>
    None,

    /// <summary>
    /// The active REALITY node failed while running on the sing-box core, but TUN
    /// is enabled so Xray cannot take over the main core — informational notice
    /// only (Xray has no TUN support on Windows in this app).
    /// </summary>
    SuggestXray,

    /// <summary>
    /// The active REALITY node failed while running on the sing-box core and TUN is
    /// off — switch the node's core type to Xray and retry the connection.
    /// </summary>
    SwitchToXray
}

/// <summary>
/// Automates core selection for REALITY nodes. The sing-box core hardcodes the
/// client version it reports in the REALITY handshake (1.8.1), so modern 3x-ui/Xray
/// servers that enforce a <c>minClientVer</c> gate reject it while the Xray core
/// (which reports its real version) succeeds. When a REALITY node fails on
/// sing-box, this advisor decides whether the app can switch it to the Xray core.
/// </summary>
public static class RealityCoreFallbackAdvisor
{
    public static bool IsRealityNode(ProfileItem? node)
        => node is not null && node.StreamSecurity == Global.StreamSecurityReality;

    /// <summary>
    /// Decides the fallback action for a failed REALITY connection. Pure decision
    /// logic — the caller supplies the runtime facts (failed core type from the
    /// health snapshot, TUN flag, live connection state, routing mode).
    /// </summary>
    public static RealityFallbackAdvice Evaluate(
        ProfileItem? node,
        ECoreType? failedCoreType,
        bool tunEnabled,
        bool connectionUp,
        bool modeOff)
    {
        if (node is null || modeOff || connectionUp)
        {
            return RealityFallbackAdvice.None;
        }

        if (!IsRealityNode(node) || failedCoreType != ECoreType.sing_box)
        {
            return RealityFallbackAdvice.None;
        }

        // With TUN enabled the main core is forced to sing-box (Xray has no TUN
        // support on Windows here), so switching the node is pointless — inform
        // the user instead.
        return tunEnabled ? RealityFallbackAdvice.SuggestXray : RealityFallbackAdvice.SwitchToXray;
    }

    /// <summary>
    /// Switches the node to the Xray core. Only call this when
    /// <see cref="Evaluate"/> returned <see cref="RealityFallbackAdvice.SwitchToXray"/>
    /// (i.e. TUN is off), otherwise the switch is ineffective.
    /// </summary>
    public static void ApplyXraySwitch(ProfileItem node)
    {
        node.CoreType = ECoreType.Xray;
    }
}
