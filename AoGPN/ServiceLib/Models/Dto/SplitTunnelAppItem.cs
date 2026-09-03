namespace ServiceLib.Models.Dto;

public class SplitTunnelAppItem : ReactiveObject
{
    /// <summary>"app" or "domain".</summary>
    public string EntryType { get; set; } = "app";

    /// <summary>Process name (google.exe), domain or IP (without the port) for the entry.</summary>
    [Reactive]
    public string Value { get; set; } = "";

    /// <summary>Port when the entry specifies one (e.g. "443"). Empty otherwise.</summary>
    [Reactive]
    public string Port { get; set; } = "";

    /// <summary>Value plus the optional ":port", for display.</summary>
    public string ValueText => Port.IsNotEmpty() ? $"{Value}:{Port}" : Value;

    /// <summary>Process name (google.exe) for apps; empty for domains. Used for traffic matching.</summary>
    [Reactive]
    public string ProcessName { get; set; } = "";

    /// <summary>Human friendly name shown in the list.</summary>
    [Reactive]
    public string DisplayName { get; set; }

    /// <summary>Full path to the executable, used to extract the icon. May be empty.</summary>
    [Reactive]
    public string ExePath { get; set; }

    /// <summary>vpn | proxy | vpn+proxy | direct | block.</summary>
    [Reactive]
    public string Action { get; set; } = "proxy";

    /// <summary>Effective route (proxy / direct / block) this entry currently follows.</summary>
    [Reactive]
    public string RouteTag { get; set; } = "";

    /// <summary>Localized label for the effective route.</summary>
    [Reactive]
    public string RouteText { get; set; } = "";

    /// <summary>Live download traffic (bytes) while sing-box reports per-app traffic.</summary>
    [Reactive]
    public long Download { get; set; }

    /// <summary>Live upload traffic (bytes) while sing-box reports per-app traffic.</summary>
    [Reactive]
    public long Upload { get; set; }

    /// <summary>Human readable download traffic, e.g. "12.3 MB". Empty when idle.</summary>
    [Reactive]
    public string DownloadText { get; set; } = "";

    /// <summary>Human readable upload traffic, e.g. "4.1 MB". Empty when idle.</summary>
    [Reactive]
    public string UploadText { get; set; } = "";

    /// <summary>
    /// Live destination IPs of the app's connections (Mihomo /connections
    /// metadata), comma-separated, UDP first. Empty when the core reports none.
    /// </summary>
    [Reactive]
    public string ActiveIps { get; set; } = "";

    /// <summary>Localized entry type label (Uygulama / Domain / IP).</summary>
    public string TypeText => EntryType switch
    {
        "domain" => ResUI.ManualEntryDomain,
        "ip" => ResUI.ManualEntryIp,
        _ => ResUI.ManualEntryApp,
    };

    /// <summary>True while the process of an app entry is running.</summary>
    [Reactive]
    public bool IsRunning { get; set; }

    /// <summary>Localized running status (Çalışıyor / Çalışmıyor / —).</summary>
    [Reactive]
    public string RunStatusText { get; set; } = "";

    /// <summary>Route tag of the app's live connections (proxy / direct / block), empty when none.</summary>
    [Reactive]
    public string LiveRouteTag { get; set; } = "";

    /// <summary>Localized live connection label, e.g. "VPN · 3" or "—" when idle.</summary>
    [Reactive]
    public string LiveRouteText { get; set; } = "";

    /// <summary>Number of live connections for this entry.</summary>
    [Reactive]
    public int LiveConnectionCount { get; set; }

    /// <summary>
    /// True when the app is set to proxy-only, TUN is off and the app is connecting
    /// directly to public addresses (it ignores the system proxy).
    /// </summary>
    [Reactive]
    public bool NeedsTun { get; set; }

    /// <summary>True when the app entry's executable file no longer exists (deleted/moved).</summary>
    [Reactive]
    public bool ExeMissing { get; set; }

    /// <summary>
    /// Connection option that was suggested when the entry was added (known games → "vpn").
    /// Empty when no suggestion was made (regular apps, domains).
    /// </summary>
    public string SuggestedAction { get; set; } = "";

    /// <summary>True while the entry still uses its suggested option ("Önerildi" badge shown).</summary>
    [Reactive]
    public bool IsSuggested { get; set; }

    /// <summary>Recomputes the suggestion badge after the connection option changes.</summary>
    
    /// <summary>Last measured round-trip time in milliseconds, -1 when unknown.</summary>
    [Reactive]
    public int LatencyMs { get; set; } = -1;

    /// <summary>Human readable latency label, e.g. "42 ms" or "—".</summary>
    [Reactive]
    public string LatencyText { get; set; } = "—";

    /// <summary>
    /// Real game/server latency measured on the direct path at program startup
    /// (before the GPN tunnel), milliseconds; -1 when not measured yet.
    /// </summary>
    [Reactive]
    public int BeforePingMs { get; set; } = -1;

    /// <summary>Human readable before-latency label, e.g. "42 ms" or "—".</summary>
    [Reactive]
    public string BeforePingText { get; set; } = "—";

    /// <summary>
    /// Real game/server latency measured through the GPN tunnel after the
    /// connection is established, milliseconds; -1 when not measured yet.
    /// </summary>
    [Reactive]
    public int AfterPingMs { get; set; } = -1;

    /// <summary>Human readable after-latency label, e.g. "18 ms" or "—".</summary>
    [Reactive]
    public string AfterPingText { get; set; } = "—";

    /// <summary>Before → after delta, e.g. "-24 ms"; empty when not comparable.</summary>
    [Reactive]
    public string PingDeltaText { get; set; } = "";

    /// <summary>Recomputes the suggestion badge after the connection option changes.</summary>
    public void UpdateSuggested()
    {
        IsSuggested = SuggestedAction.IsNotEmpty()
            && SuggestedAction != "proxy"
            && Action.Equals(SuggestedAction, StringComparison.OrdinalIgnoreCase);
    }
}
