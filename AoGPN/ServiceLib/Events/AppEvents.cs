using ServiceLib.Services.Gpn;

namespace ServiceLib.Events;

public static class AppEvents
{
    public static readonly EventChannel<Unit> AddServerViaClipboardRequested = new();
    public static readonly EventChannel<bool> HasUpdateNotified = new();

    public static readonly EventChannel<ServerSpeedItem> DispatcherStatisticsRequested = new();

    public static readonly EventChannel<string> SendSnackMsgRequested = new();
    public static readonly EventChannel<ActionNotice> SendSnackActionRequested = new();
    public static readonly EventChannel<string> SendMsgViewRequested = new();

    public static readonly EventChannel<Unit> AppExitRequested = new();
    public static readonly EventChannel<bool> ShutdownRequested = new();

    public static readonly EventChannel<ESysProxyType> SysProxyChangeRequested = new();

    /// <summary>
    /// Published with the new font family name (may be empty for the default) when the UI font changes.
    /// </summary>
    public static readonly EventChannel<string> FontFamilyChanged = new();

    public static readonly EventChannel<string> ThemeChanged = new();

    /// <summary>
    /// Published with the new language code when the UI language changes so the
    /// WebView2 dashboard can re-localise instantly without a restart.
    /// </summary>
    public static readonly EventChannel<string> LanguageChanged = new();

    public static readonly EventChannel<CoreHealthSnapshot> CoreHealthChanged = new();
    public static readonly EventChannel<CoreStartupDiagnostic> CoreStartupDiagnosticChanged = new();

    /// <summary>
    /// Published when the rule-drift health check compares the active RoutingItem against
    /// the sing-box config the running core loaded and reaches a verdict.
    /// </summary>
    public static readonly EventChannel<RuleDriftReport> RuleDriftChanged = new();

    /// <summary>
    /// Published when the GPN pipeline makes a resilience decision: server switch, UDP
    /// death, mode fallback to V2rayTCP, or Tier-2 recovery. Carries the target server,
    /// mode transition and reason so the dashboard can surface it without re-probing.
    /// </summary>
    public static readonly EventChannel<GpnResilienceEvent> GpnResilienceChanged = new();

    /// <summary>
    /// Published on every GPN coordinator state transition (Disconnected / Connecting /
    /// Connected / Failed) so the main dashboard connect button can be synchronized with
    /// the coordinator without waiting for the 2 s telemetry poll. Carries the full
    /// <see cref="GpnConnectionSnapshot"/> (state, mode, selected server).
    /// </summary>
    public static readonly EventChannel<GpnConnectionSnapshot> GpnConnectionStateChanged = new();

    /// <summary>
    /// Kesintisiz (soft) düğüm geçişi sonrası eski düğümün boşalma ilerlemesi.
    /// Kaynak: <see cref="ServiceLib.Services.Gpn.GpnDrainWatcher"/> — mihomo
    /// <c>GET /connections</c> zincirlerinden eski wg-&lt;id&gt;'ye bağlı kalan oturum
    /// sayısını düzenli aralıklarla yayınlar; dashboard durum satırı "eski düğüm
    /// boşalıyor (N)" gösterimini bundan besler.
    /// </summary>
    public static readonly EventChannel<GpnDrainSnapshot> GpnDrainChanged = new();

    /// <summary>
    /// Published for every DiagLog "GPN_*" line (GPN_LOG / GPN_RECOVER /
    /// GPN_SELECT / GPN_FAILOVER / GPN_LAUNCH ...) so the WebView2 dashboard can
    /// render a live GPN diagnostics feed without re-reading ao_diag.txt.
    /// </summary>
    public static readonly EventChannel<GpnDiagEvent> GpnDiagChanged = new();

    /// <summary>
    /// Published ~1s arada GpnCaptureLoop çalışırken: yakalanan paket başına
    /// telemetri (protokol/yön/byte, top akışlar, UDP port→PID atfı). Dashboard
    /// canlı trafik kartını bu akıştan besler.
    /// </summary>
    public static readonly EventChannel<GpnCaptureStatsSnapshot> GpnCaptureStatsChanged = new();

    /// <summary>
    /// Published when the server availability check (ping + public IP via the local
    /// proxy) completes — StatusBarViewModel.TestServerAvailability. Carries the
    /// measured latency and IP so the WebView2 dashboard main panel can show them
    /// right after a connection instead of only in the WPF status bar / toast.
    /// </summary>
    public static readonly EventChannel<AvailabilityCheckResult> AvailabilityCheckCompleted = new();

    /// <summary>
    /// WARP outbound dial sağlığı değiştiğinde yayınlanır (her hata, eşiğe ulaşınca
    /// faulted, hatasız süre dolunca healthy). Kaynak: sing-box log kuyruğunu izleyen
    /// <see cref="Gpn.WarpDialHealthMonitor"/> — dashboard banner'ı ve diag çıktısı
    /// bu olay üzerinden beslenir.
    /// </summary>
    public static readonly EventChannel<WarpDialHealth> WarpDialHealthChanged = new();

    /// <summary>
    /// WinDivert yakalama köprüsü çevre durumu değiştiğinde yayınlanır (var olan
    /// her kontrol — uygulama açılışı, köprü fault'u, sürücü kurulumu). Kaynak:
    /// <see cref="Gpn.WinDivertHealthMonitor"/> — dashboard banner'ı ve diag
    /// çıktısı bu olay üzerinden beslenir (WinDivert.dll/WinDivert64.sys eksik,
    /// yönetici yok, sürücü kurulamadı vb.).
    /// </summary>
    public static readonly EventChannel<WinDivertHealth> WinDivertHealthChanged = new();
}

/// <summary>Sunucu kullanılabilirlik ölçümünün (hız testi) yapılandırılmış sonucu.</summary>
public sealed record AvailabilityCheckResult(
    int DelayMs,
    string? Ip,
    string? Country,
    string? ServerName);
