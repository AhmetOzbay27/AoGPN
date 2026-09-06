namespace ServiceLib.Models.Configs;

[Serializable]
public class CoreBasicItem
{
    public bool LogEnabled { get; set; }

    public string Loglevel { get; set; }

    public string DefFingerprint { get; set; }

    public string DefUserAgent { get; set; }

    public string? SendThrough { get; set; }

    public string? BindInterface { get; set; }

    public bool EnableFragment { get; set; }

    public bool EnableFinalFragment { get; set; }

    public bool EnableCacheFile4Sbox { get; set; } = true;
}

[Serializable]
public class InItem
{
    public int LocalPort { get; set; }
    public string Protocol { get; set; }
    public bool UdpEnabled { get; set; }
    public bool SniffingEnabled { get; set; } = true;
    public List<string>? DestOverride { get; set; } = ["http", "tls"];
    public bool RouteOnly { get; set; }
    public bool AllowLANConn { get; set; }
    public bool NewPort4LAN { get; set; }
    public string User { get; set; }
    public string Pass { get; set; }
    public bool SecondLocalPortEnabled { get; set; }
}

[Serializable]
public class KcpItem
{
    public int Mtu { get; set; }

    public int Tti { get; set; }

    public int UplinkCapacity { get; set; }

    public int DownlinkCapacity { get; set; }

    public int CwndMultiplier { get; set; }

    public int MaxSendingWindow { get; set; }
}

[Serializable]
public class GrpcItem
{
    public int? IdleTimeout { get; set; }
    public int? HealthCheckTimeout { get; set; }
    public bool? PermitWithoutStream { get; set; }
    public int? InitialWindowsSize { get; set; }
}

[Serializable]
public class GUIItem
{
    public bool AutoRun { get; set; }
    public bool EnableStatistics { get; set; }
    public bool DisplayRealTimeSpeed { get; set; }

    /// <summary>Internal live dashboard stream; kept enabled so the WebView can show real throughput.</summary>
    public bool EnableDashboardTelemetry { get; set; } = true;

    public bool KeepOlderDedupl { get; set; }
    public int AutoUpdateInterval { get; set; }
    public int TrayMenuServersLimit { get; set; } = 20;

    /// <summary>
    /// Hardware-accelerated WPF rendering, on by default so the app gains the
    /// GPU for free on normal machines. The startup guard (see
    /// HardwareAccelerationGuard) falls back to software rendering and flips
    /// this off automatically when no usable GPU path exists (RenderCapability
    /// Tier &lt; 2) or after three sessions that ended abnormally while armed —
    /// a broken driver can crash-loop instead of glitching forever. WebView2
    /// is not affected: it manages its own GPU fallback.
    /// </summary>
    public bool EnableHWA { get; set; } = true;

    /// <summary>
    /// Crash-guard strikes: incremented at startup when the previous session
    /// ran armed (HwaSessionArmed) but never cleared the flag on exit — i.e.
    /// it died abnormally. At three strikes the guard auto-disables HWA.
    /// </summary>
    public int HwaCrashStrikes { get; set; }

    /// <summary>
    /// True while a hardware-accelerated session is running; cleared by the
    /// graceful-exit path so a process that dies without reaching it counts as
    /// one abnormal session for the crash guard.
    /// </summary>
    public bool HwaSessionArmed { get; set; }

    /// <summary>
    /// Set when the guard auto-disabled HWA (no GPU path or repeated crashes)
    /// so the UI can tell the user why the toggle is off; cleared after the
    /// notice is shown.
    /// </summary>
    public bool HwaAutoDisabledNotice { get; set; }

    public bool EnableLog { get; set; } = true;

    /// <summary>
    /// When enabled, the logging system captures detailed module-level trace
    /// (connection lifecycle, proxy transitions, GPN routing decisions, subscription
    /// downloads, WebView2 bridge calls). Designed for debugging and diagnostics.
    /// </summary>
    public bool EnableVerboseLog { get; set; } = false;

    /// <summary>
    /// Dashboard visual-effects tier: "full" (every ambient animation),
    /// "balanced" (perpetual full-screen loops are frozen — aurora drift,
    /// theme signature scans, matrix rain — while reactive effects like cursor
    /// trails, confetti, the CONNECT ring and spinners stay) or "reduced"
    /// (everything off, the old ReduceEffects behaviour). Empty means unset:
    /// the host normalizes it on first read, migrating the legacy
    /// ReduceEffects switch (on → "reduced", otherwise "full") so older
    /// config files keep their choice.
    /// </summary>
    public string EffectsMode { get; set; } = "";

    /// <summary>
    /// Legacy mirror of EffectsMode == "reduced"; kept so older consumers
    /// (config files, status-bar paths) keep working unchanged.
    /// </summary>
    public bool ReduceEffects { get; set; } = false;

    public string? RootCertProvider { get; set; }

    /// <summary>
    /// GPN kurtarma izleyicisini aç/kapat (EnableRecoveryWatch). Açıkken V2rayTCP
    /// düşüşünün ardından sistem sağlıklı bir WireGuard sunucusu bulunca otomatik
    /// Tier-2'ye döner; kapatıldığında V2rayTCP düşüşü terminaldir (geri kurtarma yok).
    /// </summary>
    public bool GpnEnableRecoveryWatch { get; set; } = true;

    /// <summary>
    /// WARP egress otomatik kurtarmasını aç/kapat (WarpAutoRecoverService).
    /// Açıkken (varsayılan) WARP dial sağlığı faulted olunca (eşik hatalar) aktif
    /// WireGuard tüneli otomatik yeniden başlatılır — WARP SOCKS5 sunucuya yalnızca
    /// tünel üzerinden erişilebildiği için yol böyle onarılır. Kapalıyken yalnızca
    /// dashboard uyarı bandı + diag çıktısı kalır (otomatik müdahale yok).
    /// </summary>
    public bool GpnEnableWarpAutoRecover { get; set; } = true;

    /// <summary>
    /// GPN otomatik sunucu değiştirmesini (failover/ping-pong) aç/kapat. KAPALIYKEN
    /// (varsayılan) seçilen İtalya/Almanya sunucusuna bağlandıktan sonra otomatik
    /// sunucu değişimi, V2rayTCP düşüşü ve Tier-2 kurtarma TÜMÜ devre dışıdır;
    /// bağlantı seçili sunucuya takılı kalır (en stabil/kesintisiz deneyim). Açıkken
    /// izleyici her 15 sn'de adapter'ı ölçer ve tünel ölürse sunucu değiştirir/düşer.
    /// </summary>
    public bool GpnEnableFailover { get; set; } = false;

    /// <summary>
    /// GPN otomatik-seçim/failover ölçüm zaman aşımları + yüksek-gecikme toleransı.
    /// Uzak/yoğun bir WireGuard sunucusunda yanlış "tünel öldü" algısını (bağlantının
    /// ara ara kopması) azaltmak için <see cref="GpnProbeTuning.HandshakeWaitTimeoutMs"/>
    /// ve <see cref="GpnProbeTuning.SlowServerToleranceMs"/> yukarı ayarlanabilir.
    /// </summary>
    public GpnProbeTuning GpnProbe { get; set; } = new();

    /// <summary>
    /// GPN seed tohumlaması için İtalya istemci özel anahtarı (base64). Gömülü
    /// şablondaki ANAHTAR BİLİNÇLİ OLARAK YOKTUR — özel anahtarlar sürüm kontrolüne
    /// yazılmaz. Bu ayar (veya GPN_ITALY_PRIVATE_KEY env değişkeni) ilk kurulumda
    /// gpn_servers tohumlamasına anahtar sağlar; diske DPAPI ile şifrelenerek yazılır.
    /// </summary>
    public string? GpnSeedItalyPrivateKey { get; set; }

    /// <summary>Almanya istemci özel anahtarı (base64) — <see cref="GpnSeedItalyPrivateKey"/> ile aynı akış.</summary>
    public string? GpnSeedGermanyPrivateKey { get; set; }

    /// <summary>
    /// Çift Bağlantı (Bölünmüş Tünelleme) — küresel launcher-bypass düğümü
    /// (VLESS/Reality): <see cref="VlessProfileItem"/>'ın JSON serileştirilmiş hali.
    /// Dolu geldiğinde GPN WireGuard bağlantısı (GpnCoreLauncher) bunu core
    /// context'ine taşır; mihomo YAML'i ana WG outbound'larının yanına ikincil
    /// "vless-launcher" proxy'si ekler ve "warp" egress kuralları (BsGLauncher.exe)
    /// WARP SOCKS5 zinciri yerine o düğüme gider. Boş/null → legacy WARP davranışı.
    /// Tekil (küresel) ayardır — hangi WG düğümü seçilirse seçilsin aynı düğüm kullanılır.
    /// </summary>
    public string? VlessBypassNodeJson { get; set; }

    /// <summary>
    /// Kullanıcı düzenlenebilir launcher-bypass listesi —
    /// <see cref="ServiceLib.Models.Entities.LauncherBypassItem"/> kayıtlarının
    /// JSON serileştirilmiş hali (ad + domain aileleri + egress seçimi). Boş/null
    /// → yerleşik BSG varsayılanı (eski sabit BsgLauncherDomains davranışı); "[]"
    /// → launcher satırları kapatılır. GpnMihomoConfigService kural üretiminde
    /// GpnLauncherBypass.ReadAll ile çözülür; dashboard Ayarlar → GPN panelinden
    /// düzenlenir (sıradaki bağlantıda uygulanır, çalışan oturumu kesmez).
    /// </summary>
    public string? LauncherBypassesJson { get; set; }
}

/// <summary>
/// GPN seçim/failover ölçüm ayarları (probe zaman aşımları + yüksek-gecikme toleransı).
/// GPN Bağlan seçiminde <see cref="GpnServerSelectionService"/> ile <see cref="GpnProbeOptions"/>
/// ı üretir; her alan o seçenek içindeki karşılığına 1:1 maplanır.
/// </summary>
[Serializable]
public class GpnProbeTuning
{
    /// <summary>Tek ICMP örnek zaman aşımı (ms) → <c>PerSampleTimeoutMs</c>. Varsayılan 1000.</summary>
    public int IcmpSampleTimeoutMs { get; set; } = 1000;

    /// <summary>UDP/junk yanıt bekleme süresi (ms) → <c>UdpCheck.WaitTimeoutMs</c>. Varsayılan 3000.</summary>
    public int UdpWaitTimeoutMs { get; set; } = 3000;

    /// <summary>El sıkışma (handshake) tek-deneme yanıt penceresi (ms) → <c>HandshakeProbe.WaitTimeoutMs</c>. Varsayılan 4000.</summary>
    public int HandshakeWaitTimeoutMs { get; set; } = 4000;

    /// <summary>Failover'da salınımı önleyen marj (ms) → <c>SwitchHysteresisMs</c>. Varsayılan 15.</summary>
    public int SwitchHysteresisMs { get; set; } = 15;

    /// <summary>Yüksek-gecikme toleransı (ms) → <c>SlowServerToleranceMs</c>. Varsayılan 400.</summary>
    public int SlowServerToleranceMs { get; set; } = 400;

    /// <summary>
    /// Failover ping-pong sıçrama koruması (sn) → <c>FailoverSwitchCooldownSeconds</c>.
    /// Sunucu değişiminden sonra bu süre içinde az önce terk edilen sunucuya geri
    /// dönülmez — bir saniyede A↔B döngüsüyle tüneli/socket'leri koparan churn'ü durdurur.
    /// Varsayılan 45.
    /// </summary>
    public int FailoverSwitchCooldownSeconds { get; set; } = 45;
}

/// <summary>
/// WinDivert yakalama ayarları — <see cref="ServiceLib.Services.WinDivertOpenParams"/>
/// yapılandırmasını kullanıcı ayarlarından yönetilebilir kılar (OpenEx kuyruk
/// parametreleri + katman/yön). Varsayılanlar klasik WinDivert açılış davranışına
/// denktir (QueueLen 8192, QueueTime 1000 ms, QueueSize sınırsız, NETWORK/outbound).
/// </summary>
[Serializable]
public class GpnCaptureItem
{
    /// <summary>QueueLen değeri WINDIVERT_PARAM_QUEUE_LENGTH olarak iletilir.</summary>
    public bool EnableQueueLen { get; set; } = true;

    /// <summary>QueueTime değeri WINDIVERT_PARAM_QUEUE_TIME olarak iletilir.</summary>
    public bool EnableQueueTime { get; set; } = true;

    /// <summary>QueueSize değeri WINDIVERT_PARAM_QUEUE_SIZE olarak iletilir (0 = sınırsız).</summary>
    public bool EnableQueueSize { get; set; } = false;

    /// <summary>Kullanıcı kuyruğundaki maksimum paket sayısı (WINDIVERT_PARAM_QUEUE_LENGTH).</summary>
    public uint QueueLen { get; set; } = 8192;

    /// <summary>Paketin kuyrukta bekleme süresi — milisaniye (WINDIVERT_PARAM_QUEUE_TIME).</summary>
    public uint QueueTime { get; set; } = 1000;

    /// <summary>Kuyruk tampon boyutu — bayt (WINDIVERT_PARAM_QUEUE_SIZE; 0 = sınırsız).</summary>
    public uint QueueSize { get; set; } = 0;

    /// <summary>WinDivert katmanı: 0 = WINDIVERT_LAYER_NETWORK, 1 = WINDIVERT_LAYER_NETWORK_FORWARD.</summary>
    public int Layer { get; set; } = 0;

    /// <summary>Yön: 0 = inbound, 1 = outbound (oyun yakalama filtresi outbound'dur).</summary>
    public int Direction { get; set; } = 1;

    /// <summary>WinDivertOpenEx priority değeri.</summary>
    public short Priority { get; set; } = 0;
}

/// <summary>
/// Wintun adaptörü ayarları — <see cref="ServiceLib.Services.WireGuardTunnelService.Open"/>
/// parametrelerini (adapter ad ön eki + halka tampon kapasitesi) kullanıcı
/// ayarlarından yönetilebilir kılar. Değerler bir sonraki WireGuard bağlantısında
/// (GpnCaptureBridge köprü açılışı) uygulanır.
/// </summary>
[Serializable]
public class GpnWintunItem
{
    /// <summary>
    /// Wintun adapter adı ÖN EKİ — bağlantıda sunucu kimliği eklenir
    /// (örn. "AoGPN" + "it" → "AoGPN-it"). Yalnızca [A-Za-z0-9_-] izinli.
    /// </summary>
    public string AdapterName { get; set; } = "AoGPN";

    /// <summary>
    /// Wintun halka tampon kapasitesi (bayt) — 2'nin katına yuvarlanır,
    /// 128 KiB..64 MiB aralığına sınırlanır (resmi örnek varsayılanı 4 MiB).
    /// </summary>
    public uint RingCapacity { get; set; } = 0x400000; // 4 MiB
}

[Serializable]
public class MsgUIItem
{
    public string? MainMsgFilter { get; set; }
    public bool? AutoRefresh { get; set; }
}

[Serializable]
public class ConnectionSettingsItem
{
    // 0 = Off, 1 = VPN, 2 = Manuel (app/domain based list)
    public int Mode { get; set; } = 2;
    public List<ManualRouteSetting> ManualRoutes { get; set; } = [];

    /// <summary>
    /// Capture transport for the connection workflow: "tun", "proxy", or empty for
    /// the legacy mode-derived default. It does not change the independent system-
    /// proxy preference stored in <see cref="SystemProxyItem"/>.
    /// </summary>
    public string Transport { get; set; } = "";

    /// <summary>When a listed VPN-routed game starts, auto-switch to Manuel mode and apply the rules.</summary>
    public bool AutoConnectOnGameStart { get; set; }

    /// <summary>
    /// Manual (GPN) routing direction. False = whitelist (only listed apps are
    /// tunneled, everything else stays direct). True = blacklist (listed apps stay
    /// direct/blocked and everything else is tunneled).
    /// </summary>
    public bool InvertManualRouting { get; set; }

    /// <summary>
    /// Automatic, wireguard, mimic (Reality/TLS strategy), hysteria2, or openvpn.
    /// This is a preference only; credentials and the actual profile protocol are
    /// never rewritten by the selector.
    /// </summary>
    public string ProtocolPreference { get; set; } = ConnectionProtocolPreference.Automatic;

    /// <summary>Enables the bounded core/tunnel recovery workflow.</summary>
    public bool AutoReconnectEnabled { get; set; } = true;

    /// <summary>Maximum restart attempts for one failure burst.</summary>
    public int AutoReconnectMaxAttempts { get; set; } = 5;
}

[Serializable]
public class ManualRouteSetting
{
    /// <summary>"app" or "domain".</summary>
    public string EntryType { get; set; } = "app";

    /// <summary>Process name (google.exe), domain or IP value (without the port).</summary>
    public string Value { get; set; } = "";

    /// <summary>Port (e.g. "443") when the entry specifies one. Empty otherwise.</summary>
    public string Port { get; set; } = "";

    /// <summary>Friendly display name, e.g. "Google Chrome".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Full executable path (apps only), used for the icon.</summary>
    public string ExePath { get; set; } = "";

    /// <summary>vpn | proxy | vpn+proxy | direct | block | warp</summary>
    public string Action { get; set; } = "proxy";

    /// <summary>Connection option suggested when the app was added (games → "vpn"). Empty when none.</summary>
    public string SuggestedAction { get; set; } = "";

    /// <summary>
    /// WARP egress düğümü: bu satırın "warp" rotasının hangi mevcut düğüm
    /// (ProfileItem.IndexId) üzerinden çıkacağını tutar. Boş/null → varsayılan
    /// WARP egress (aktif düğümün WARP SOCKS5 zinciri / legacy davranış). Dolu
    /// olduğunda mihomo config üreticisi o düğüm için ayrı bir egress outbound'u
    /// üretir ve bu satırın kuralı ona işaret eder (eski Ayarlar → GPN VLESS
    /// bypass düğümünün yerini alır). Yalnızca Action == "warp" iken anlamlıdır.
    /// </summary>
    public string? WarpNodeIndexId { get; set; }
}

[Serializable]
public class UIItem
{
    public bool EnableAutoAdjustMainLvColWidth { get; set; }
    public int MainGirdHeight1 { get; set; }
    public int MainGirdHeight2 { get; set; }
    public EGirdOrientation MainGirdOrientation { get; set; } = EGirdOrientation.Tab;
    public string? ColorPrimaryName { get; set; }
    public string? CurrentTheme { get; set; }

    /// <summary>
    /// Raw dashboard (WebView2) theme id persisted from the Appearance deck, e.g.
    /// "aurora" / "candy" / "neon-cyber". Unlike <see cref="CurrentTheme"/>
    /// (an <see cref="ETheme"/> value), this stores every theme the dashboard can
    /// apply, including the ones with no native WPF palette. Null/empty on old
    /// configs — the startup path then derives it from <see cref="CurrentTheme"/>.
    /// </summary>
    public string? DashboardTheme { get; set; }
    public string CurrentLanguage { get; set; }
    public string CurrentFontFamily { get; set; }
    public int CurrentFontSize { get; set; }
    public bool EnableDragDropSort { get; set; }
    public bool DoubleClick2Activate { get; set; }
    public bool AutoHideStartup { get; set; }

    /// <summary>Opt-in: minimizing the window hides it to the tray instead of the taskbar. Off by default.</summary>
    public bool Minimize2Tray { get; set; }

    /// <summary>
    /// Opt-in: when ON, closing the window (X) keeps the app running in the
    /// background — the window hides to the tray and the connection/proxy stay
    /// active; only the tray menu's Exit fully quits it. Off by default for fresh
    /// configs, in which case X fully exits the app. Never enabled implicitly.
    /// </summary>
    public bool Hide2TrayWhenClose { get; set; }
    public bool MacOSShowInDock { get; set; }
    public List<ColumnItem> MainColumnItem { get; set; }
    public List<WindowSizeItem> WindowSizeItem { get; set; }
    public bool HideColumnIpInfo { get; set; }
    public int SidebarWidth { get; set; }
}

[Serializable]
public class ConstItem
{
    public string? SubConvertUrl { get; set; }
    public string? GeoSourceUrl { get; set; }
    public string? SrsSourceUrl { get; set; }
    public string? RouteRulesTemplateSourceUrl { get; set; }
}

[Serializable]
public class KeyEventItem
{
    public EGlobalHotkey EGlobalHotkey { get; set; }

    public bool Alt { get; set; }

    public bool Control { get; set; }

    public bool Shift { get; set; }

    public int? KeyCode { get; set; }
}

[Serializable]
public class CoreTypeItem
{
    public EConfigType ConfigType { get; set; }

    public ECoreType CoreType { get; set; }
}

[Serializable]
public class TunModeItem
{
    public bool EnableTun { get; set; }
    public bool AutoRoute { get; set; } = true;
    public bool StrictRoute { get; set; } = true;
    public string Stack { get; set; } = WindowsTunStabilityPolicy.DefaultStack;
    public int Mtu { get; set; }
    public bool EnableIPv6Address { get; set; }
    public string IcmpRouting { get; set; }
    public bool EnableLegacyProtect { get; set; } = true;
    public List<string>? RouteExcludeAddress { get; set; }
    public string IPv4Address { get; set; }
    public string IPv6Address { get; set; }
}

[Serializable]
public class SpeedTestItem
{
    public int SpeedTestTimeout { get; set; }
    public string SpeedTestUrl { get; set; }
    public string SpeedPingTestUrl { get; set; }
    public int MixedConcurrencyCount { get; set; }
    public string IPAPIUrl { get; set; }
    public string UdpTestTarget { get; set; }
    public int? SpeedTestPageSize { get; set; }
    public int? SpeedTestDelayInterval { get; set; }
}

[Serializable]
public class RoutingBasicItem
{
    public string DomainStrategy { get; set; }
    public string DomainStrategy4Singbox { get; set; }
    public string RoutingIndexId { get; set; }
}

[Serializable]
public class ColumnItem
{
    public string Name { get; set; }
    public int Width { get; set; }
    public int Index { get; set; }
}

[Serializable]
public class Mux4RayItem
{
    public int? Concurrency { get; set; }
    public int? XudpConcurrency { get; set; }
    public string? XudpProxyUDP443 { get; set; }
}

[Serializable]
public class Mux4SboxItem
{
    public string Protocol { get; set; }
    public int MaxConnections { get; set; }
    public bool? Padding { get; set; }
}

[Serializable]
public class HysteriaItem
{
    public int UpMbps { get; set; }
    public int DownMbps { get; set; }
    public int HopInterval { get; set; } = Global.Hysteria2DefaultHopInt;
}

[Serializable]
public class ClashUIItem
{
    public ERuleMode RuleMode { get; set; }
    public bool EnableIPv6 { get; set; }
    public bool EnableMixinContent { get; set; }
    public int ProxiesSorting { get; set; }
    public bool ProxiesAutoRefresh { get; set; }
    public int ProxiesAutoDelayTestInterval { get; set; } = 10;
    public bool ConnectionsAutoRefresh { get; set; }
    public int ConnectionsRefreshInterval { get; set; } = 2;
    public List<ColumnItem> ConnectionsColumnItem { get; set; }
}

[Serializable]
public class SystemProxyItem
{
    public ESysProxyType SysProxyType { get; set; }
    public string SystemProxyExceptions { get; set; }
    public bool NotProxyLocalAddress { get; set; } = true;
    public string SystemProxyAdvancedProtocol { get; set; }
    public string? CustomSystemProxyPacPath { get; set; }
    public string? CustomSystemProxyScriptPath { get; set; }
}

[Serializable]
public class WebDavItem
{
    public string? Url { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? DirName { get; set; }
}

[Serializable]
public class CheckUpdateItem
{
    public bool CheckPreReleaseUpdate { get; set; }
    public List<string>? SelectedCoreTypes { get; set; }
}

[Serializable]
public class Fragment4RayItem
{
    public string? Packets { get; set; }
    public List<string>? Lengths { get; set; }
    public List<string>? Delays { get; set; }
    public string? MaxSplit { get; set; }

    // For migration from old version, remove those properties in the future
    public string? Length { get; set; }

    public string? Interval { get; set; }
    // migration end
}

[Serializable]
public class WindowSizeItem
{
    public string TypeName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMaximized { get; set; }

    // Saved window position in WPF units. Nullable so configs written by older
    // builds (or windows that never stored a position) fall back to centering.
    public int? Left { get; set; }
    public int? Top { get; set; }
}

[Serializable]
public class SimpleDNSItem
{
    public bool? UseSystemHosts { get; set; }
    public bool? AddCommonHosts { get; set; }
    public bool? FakeIP { get; set; }
    public bool? GlobalFakeIp { get; set; }
    public string? FakeIPRange { get; set; }
    public bool? BlockBindingQuery { get; set; }
    public string? DirectDNS { get; set; }
    public string? RemoteDNS { get; set; }
    public string? BootstrapDNS { get; set; }
    public string? Strategy4Freedom { get; set; }
    public string? Strategy4Proxy { get; set; }
    public string? Strategy4ProxyDial { get; set; }
    public bool? ServeStale { get; set; }
    public bool? ParallelQuery { get; set; }
    public string? Hosts { get; set; }
    public string? DirectExpectedIPs { get; set; }
    public bool? EnableHappyEyeballs { get; set; }
}

[Serializable]
public class HappyEyeballs4RayItem
{
    public int? TryDelayMs { get; set; }
    public bool? PrioritizeIPv6 { get; set; }
    public int? Interleave { get; set; }
    public int? MaxConcurrentTry { get; set; }
}
