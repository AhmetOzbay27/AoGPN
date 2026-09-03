namespace ServiceLib.Common;

/// <summary>
/// Resolves the effective operating-system proxy mode without overwriting the user's
/// independent AoGPN-style system-proxy selection. A connection using the proxy
/// transport may temporarily require the proxy even when the saved preference is Clear.
/// </summary>
public static class SystemProxyPolicy
{
    /// <summary>
    /// Returns the mode that should be applied to the operating system right now.
    /// <paramref name="requestedType"/> is used by an explicit proxy-mode command and
    /// is not persisted by this helper.
    /// </summary>
    public static ESysProxyType ResolveEffectiveType(
        Config config,
        ESysProxyType? requestedType = null,
        bool forceDisable = false)
    {
        var desired = requestedType
            ?? config.SystemProxyItem?.SysProxyType
            ?? ESysProxyType.ForcedClear;

        if (forceDisable)
        {
            // Unchanged means the app must leave an unrelated system-proxy setting
            // alone. If AoGPN temporarily owned the proxy for a connection, however,
            // it must release it during shutdown.
            return desired == ESysProxyType.Unchanged && !ConnectionNeedsSystemProxy(config)
                ? ESysProxyType.Unchanged
                : ESysProxyType.ForcedClear;
        }

        // CRITICAL: When an active TUN connection is capturing traffic, the
        // Windows system proxy must be forced OFF regardless of the user's saved
        // independent preference. If the OS proxy stays enabled during TUN mode,
        // browsers connect through the local SOCKS port — sing-box sees the
        // traffic as coming from the proxy listener, not the browser, so
        // process_name rules fail to match and traffic leaks to direct.
        //
        // EXCEPTION: When the TUN adapter was confirmed missing (VMware, Hyper-V,
        // some VPNs), we are in fallback mode and the system proxy IS needed even
        // though TUN was originally requested.  TunLifecycleManager sets
        // TunInterfaceConfirmed=false after the post-start flush check.
        var connectionIsActive = config.ConnectionItem?.Mode is not null
            && config.ConnectionItem.Mode != GameTriggerModes.Off;
        if (connectionIsActive && !ConnectionNeedsSystemProxy(config)
            && TunLifecycleManager.TunInterfaceConfirmed != false)
        {
            return ESysProxyType.ForcedClear;
        }

        // When the connection is off, preserve the user's independent proxy
        // preference unchanged.
        if (!connectionIsActive)
        {
            return desired;
        }

        // The connection needs the system proxy. Honour the user's independent
        // proxy preference when it is already enabled; otherwise force it on.
        var userWantsProxy = IsProxyEnabled(desired);
        if (userWantsProxy)
        {
            return desired;
        }

        return ESysProxyType.ForcedChange;
    }

    /// <summary>
    /// True when the active AoGPN connection needs the Windows/Linux/macOS system proxy
    /// as its traffic capture mechanism. TUN capture intentionally does not require it.
    /// </summary>
    public static bool ConnectionNeedsSystemProxy(Config config)
    {
        var connection = config.ConnectionItem;
        if (connection is null || connection.Mode == GameTriggerModes.Off)
        {
            return false;
        }

        // An explicit transport wins over the legacy fields. In particular, do not
        // enable the OS proxy merely because a manual list contains proxy-tagged
        // entries when the active capture is already TUN.
        if (connection.Transport == "tun")
        {
            return false;
        }

        if (connection.Transport == "proxy")
        {
            return true;
        }

        // The persisted TUN flag is the next-best signal for configurations written
        // before ConnectionItem.Transport existed. TUN captures proxy-tagged manual
        // routes itself, so it must not be combined with a system-proxy override.
        if (config.TunModeItem?.EnableTun == true)
        {
            return false;
        }

        // Backward-compatible fallback for configs written before Transport was
        // persisted: manual proxy routes need the OS proxy when active. "vpn" is
        // included because legacy "proxy"/"vpn+proxy" entries are normalised to it.
        return connection.Mode == GameTriggerModes.Manual
            && (connection.ManualRoutes ?? [])
                .Any(route => route.Action is "vpn" or "proxy" or "vpn+proxy");
    }

    public static bool IsProxyEnabled(ESysProxyType type) =>
        type is ESysProxyType.ForcedChange or ESysProxyType.Pac;

    /// <summary>Cycles the independent quick-toggle between AoGPN Set and Clear.</summary>
    public static ESysProxyType Toggle(ESysProxyType current) =>
        IsProxyEnabled(current) ? ESysProxyType.ForcedClear : ESysProxyType.ForcedChange;
}
