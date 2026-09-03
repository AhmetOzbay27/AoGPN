using ServiceLib.Services.CoreConfig.Mihomo;

namespace ServiceLib.Models.CoreConfigs;

public record CoreConfigContext
{
    public required ProfileItem Node { get; init; }
    public required ECoreType RunCoreType { get; init; }
    public RoutingItem? RoutingItem { get; init; }
    public DNSItem? RawDnsItem { get; init; }
    public SimpleDNSItem SimpleDnsItem { get; init; } = new();
    public Dictionary<string, ProfileItem> AllProxiesMap { get; init; } = new();
    public Config AppConfig { get; init; } = new();
    public FullConfigTemplateItem? FullConfigTemplate { get; init; } = new();

    // Test ServerTestItem Map
    public Dictionary<string, string> ServerTestItemMap { get; init; } = new();

    // TUN Compatibility
    public bool IsTunEnabled { get; init; } = false;

    /// <summary>
    /// True for the auxiliary pre-SOCKS core. It must not expose the main API or
    /// share the main sing-box cache file, otherwise a custom mihomo/sing-box main
    /// core can collide with the helper on the same ports/files.
    /// </summary>
    public bool IsPreSocks { get; init; } = false;

    /// <summary>
    /// GPN (mihomo) çoklu-düğüm YAML'i için aday sunucu listesi. Dolu geldiğinde
    /// üretici tüm adayları <c>type: wireguard</c> outbound + "GPN-Nodes" select
    /// grubu olarak yazar; kurallar gruba işaret eder ve düğüm değişimi çekirdek
    /// yeniden başlatılmadan tek API çağrısıyla yapılabilir. Null/tek öğe → legacy
    /// tek-düğüm biçimi (mevcut davranış).
    /// </summary>
    public IReadOnlyList<GpnServerProfile>? GpnCandidates { get; init; }

    /// <summary>
    /// GPN mihomo kesintisiz rota (superset) politikası. Dolu geldiğinde üretici
    /// tüm girişleri sabit kural satırı + "ao-&lt;i&gt;" seçim grubu olarak, yakalayıcıyı
    /// "GPN-MODE" grubuna yazar; mod/yön/uygulama rota değişiklikleri daha sonra
    /// çekirdek yeniden başlatılmadan tek tek PUT /proxies çağrısıyla yapılır.
    /// Null → legacy biçim (mevcut davranış). Yalnızca GPN WireGuard koordinatör
    /// başlatması bu politikayı taşır (GpnCoreLauncher).
    /// </summary>
    public GpnSoftRoutingPolicy? GpnSoftPolicy { get; init; }

    /// <summary>
    /// GPN mihomo "Çift Bağlantı (Bölünmüş Tünelleme)" için KÜRESEL launcher-bypass
    /// düğümü (VLESS/Reality). Dolu geldiğinde üretici ana WireGuard outbound'larının
    /// hemen altına ikincil "vless-launcher" proxy'si ekler ve "warp" (temiz/auth
    /// egress) kurallarını o düğüme yönlendirir (warp-socks üretilmez). Null → legacy
    /// davranış. Tekil ayardır: GpnCoreLauncher, GuiItem.VlessBypassNodeJson'dan okur
    /// ve hangi WG düğümü seçilirse seçilsin aynı düğümü context'e taşır.
    /// </summary>
    public VlessProfileItem? GpnVlessBypass { get; init; }

    public HashSet<string> ProtectDomainList { get; init; } = [];
    // Typically, it is the core of the outbound chain
    public HashSet<ECoreType> ProtectCoreTypeList { get; init; } = [];

    public bool IsWindows { get; init; }
    public bool IsMacOS { get; init; }
}
