namespace ServiceLib.Services.CoreConfig.Mihomo;

/// <summary>
/// Seçenekler: üretilen mihomo YAML'inin dışarıdan kontrol edilen noktaları.
/// Portlar / log seviyesi / TUN yığını gibi çalışma zamanı değerleri çağırana aittir;
/// bu sınıf saf (pure) kalır ve birim test edilebilir.
/// </summary>
public sealed record GpnMihomoOptions
{
    /// <summary>mihomo mixed (HTTP+SOCKS5) dinleme portu. 0 verilirse listener üretilmez.</summary>
    public int MixedPort { get; init; }

    /// <summary>RESTful external-controller portu (telemetri / ClashApiManager).</summary>
    public int ExternalControllerPort { get; init; } = 0;

    /// <summary>mihomo log seviyesi: silent|error|warning|info|debug.</summary>
    public string LogLevel { get; init; } = "info";

    /// <summary>
    /// Tüm çıkış dial'larının bağlanacağı fiziksel ağ adı (örn. "Ethernet").
    /// Boş bırakılırsa mihomo kendi varsayılanını kullanır (auto-detect).
    /// İtalya/WireGuard kapalı ortamda TUN döngüsünü önlemenin kanıtlanmış yoludur.
    /// </summary>
    public string? InterfaceName { get; init; }

    /// <summary>Wintun adaptör adı.</summary>
    public string TunDevice { get; init; } = "AoGPN";

    /// <summary>TUN IP yığını: gvisor (kanıtlandı) | system | mixed.</summary>
    public string TunStack { get; init; } = "gvisor";

    /// <summary>Varsayılan rotayı TUN'a çek (mihomo auto-route).</summary>
    public bool AutoRoute { get; init; } = true;

    /// <summary>MTU; null ise sunucu profilinden türetilir (GpnRecommendedMtu'ya kırpılır).</summary>
    public int? Mtu { get; init; }

    /// <summary>
    /// DNS modülü: açıkken mihomo TUN içinden geçen tüm DNS sorgularını yakalar
    /// (hijack) ve fake-ip ile yanıtlar; bağlantı kurulurken PROCESS/DOMAIN kuralı
    /// işletilir ve hedef gerçek hostname olarak proxy'ye (örn. warp-socks) aktarılır.
    /// Kapalıyken dns bloğu yalnızca "enable: false" olur (kanıtlanmış test config'i).
    /// </summary>
    public bool DnsEnabled { get; init; }

    /// <summary>enhanced-mode: fake-ip (önerilen) | redir-host.</summary>
    public string DnsEnhancedMode { get; init; } = "fake-ip";

    /// <summary>fake-ip aralığı (TUN ile çakışmayan özel aralık).</summary>
    public string FakeIpRange { get; init; } = "198.18.0.1/16";

    /// <summary>Çözümlemede kullanılacak DNS sunucuları (uygulamanın RemoteDNS/Ayarlar).</summary>
    public IReadOnlyList<string> DnsNameservers { get; init; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>
    /// default-nameserver önyükleme listesi (SADECE saf IP — mihomo DoH/tls
    /// sunucu adını bunlarla çözer). Boşsa üretici, nameserver listesindeki saf
    /// IP'leri kullanır; o da boşsa [1.1.1.1, 8.8.8.8] güvenlik ağına düşer.
    /// </summary>
    public IReadOnlyList<string> DnsDefaultNameservers { get; init; } = [];

    /// <summary>WG el sıkışmasını ayakta tutan keep-alive (sn); ≤0 ise yazılmaz.</summary>
    public int PersistentKeepalive { get; init; }

    /// <summary>
    /// mihomo log dosyası (log-file). Boşsa yazılmaz (mihomo stdout'a yazar).
    /// WarpDialHealthMonitor bu dosyayı kuyruğundan izler — GPN mihomo
    /// oturumlarında WARP dial hataları buradan yakalanır.
    /// </summary>
    public string LogFilePath { get; init; } = "";
}

/// <summary>
/// GPN (WireGuard + WARP egress) için mihomo (Clash.Meta) YAML üreticisi.
///
/// sing-box'ın WireGuard "endpoint" ayağı, IPv6'sız Windows makinelerde dual-stack
/// UDP soketi açamadığı için el sıkışmayı tamamlayamıyordu (canlı A/B doğrulaması).
/// mihomo ise aynı anahtarlarla ilk denemede el sıkışıyor ve TUN + PROCESS-NAME
/// süreç ayırımı + WARP SOCKS5 zincirini (dialer-proxy) bu makinede kanıtladı.
///
/// Bu sınıf, GpnServerProfile + rota kurallarını (RulesItem — ManualRoutingRules
/// çıktısı) alıp doğrudan çalıştırılabilir mihomo YAML'i üretir:
///
///   proxies:
///     - wg-&lt;serverId&gt;        → type: wireguard (sunucuya tünel, düşük ping)
///     - warp-socks (legacy — yalnızca en az bir "warp" kuralı varsa)
///                              → type: socks5 10.66.66.1:40000,
///                                dialer-proxy: wg-&lt;serverId&gt; (Cloudflare egress)
///     - vless-launcher (Çift Bağlantı — bypass düğümü verildiğinde)
///                              → type: vless + reality-opts (launcher/auth egress)
///   rules:
///     - DOMAIN-SUFFIX,escapefromtarkov.com,…            (BSG launcher/API alan adları —
///       DOMAIN-SUFFIX,battlestategames.com,…            her zaman launcher-egress,
///       DOMAIN-SUFFIX,tarkov.com,…                      en üstte — UI'dan bağımsız;
///       DOMAIN-SUFFIX,escapefromtarkov.ru,…             CefSharp arka plan web motoru
///       DOMAIN-SUFFIX,prod.escapefromtarkov.com,…       API çağrıları da buradan çıkar;
///       DOMAIN-SUFFIX,launcher.escapefromtarkov.com,…   WAF korumalı auth/API
///       DOMAIN-SUFFIX,launcher.escapefromtarkov.ru,…    hostlarının tamamı — tarkov.com
///       DOMAIN-SUFFIX,gw-pvp.escapefromtarkov.com,…     ve escapefromtarkov.ru
///       DOMAIN-SUFFIX,www.escapefromtarkov.com,…        aileleri dahil)
///       DOMAIN-SUFFIX,profile.tarkov.com,…
///     - PROCESS-NAME,BsGLauncher.exe,warp-socks      (launcher → WARP — legacy)
///     - PROCESS-NAME,BsGLauncher.exe,vless-launcher  (launcher → VLESS — Çift Bağlantı)
///     - PROCESS-NAME,EscapeFromTarkov.exe,wg-de      (oyun UDP/TCP → tünel)
///     - MATCH,DIRECT                                 (beyaz liste varsayılanı)
///
/// Rota eşlemesi sing-box etiketlerinden (Global.DirectTag/ProxyTag/WarpTag/
/// BlockTag) Clash hedeflerine çevrilir: direct→DIRECT, block→REJECT, proxy/vpn→
/// wg-&lt;serverId&gt;. warp→ Çift Bağlantıda vless-launcher, legacy'de warp-socks.
/// </summary>
public sealed class GpnMihomoConfigService
{
    private const string _clashDirect = "DIRECT";
    private const string _clashReject = "REJECT";

    /// <summary>WG proxy adı — rota "vpn" hedefleri buraya gider.</summary>
    public static string WireGuardProxyName(GpnServerProfile server) => $"wg-{server.ServerId}";

    /// <summary>WARP SOCKS5 proxy adı — rota "warp" hedefleri buraya gider (legacy).</summary>
    public const string WarpProxyName = "warp-socks";

    /// <summary>
    /// BSG launcher/API alan adları — Tarkov kimlik doğrulama trafiği her zaman
    /// launcher-egress çıkışından gider (Çift Bağlantıda vless-launcher, legacy'de
    /// warp-socks). Apex domain aileleri (escapefromtarkov.com, battlestategames.com,
    /// tarkov.com, escapefromtarkov.ru) DOMAIN-SUFFIX eşleşmesiyle tüm alt
    /// domainlerini kapsar — .ru ailesi canlı oturum kaydında görülen RU launcher
    /// aynası (launcher.escapefromtarkov.ru) için eklendi. Launcher'ın CefSharp arka
    /// plan web motorunun kullandığı bilinen auth/API hostları (prod/launcher/gw-pvp/
    /// www.escapefromtarkov.com, launcher.escapefromtarkov.ru, profile.tarkov.com)
    /// da açıkça listelenir — WireGuard tüneline sızıp "Fatal Error" vermesinler.
    /// Bu satırlar kural listesinin EN ÜSTÜNE yazılır (first-match-wins), böylece UI
    /// düğmelerinden bağımsız olarak çekirdeğe doğrudan işlenirler.
    /// </summary>
    private static readonly string[] BsgLauncherDomains =
    [
        "escapefromtarkov.com",
        "battlestategames.com",
        "tarkov.com",
        "escapefromtarkov.ru", // RU launcher ailesi — canlı oturum kaydında launcher.escapefromtarkov.ru görüldü
        "prod.escapefromtarkov.com",
        "launcher.escapefromtarkov.com",
        "launcher.escapefromtarkov.ru",
        "gw-pvp.escapefromtarkov.com",
        "www.escapefromtarkov.com",
        "profile.tarkov.com",
    ];

    /// <summary>
    /// Çift Bağlantı (Bölünmüş Tünelleme) ikincil proxy adı — launcher/auth trafiği
    /// (BsGLauncher.exe) VLESS/Reality üzerinden bu outbound'dan çıkar. Üreticiye
    /// launcher-bypass düğümü (VlessProfileItem) verildiğinde bu outbound WireGuard
    /// tanımlarının hemen altına yazılır ve "warp" egress kuralları warp-socks yerine
    /// buraya işaret eder (WARP SOCKS5 zinciri üretilmez). Kaynak küresel ayardır
    /// (GuiItem.VlessBypassNodeJson) — GpnCoreLauncher → CoreConfigContext → üretici.
    /// </summary>
    public const string BypassProxyName = "vless-launcher";

    /// <summary>
    /// Tüm WG düğümlerini içeren select grubu. Kurallar (ve WARP zinciri) bu gruba
    /// işaret ettiğinde düğüm değişimi tek bir API çağrısıyla (PUT /proxies) yapılır:
    /// çekirdek/TUN yerinde kalır, mevcut bağlantılar eski düğümde doğal olarak
    /// bitene kadar sürer, yeni bağlantılar anında yeni düğümden kurulur.
    /// </summary>
    public const string NodesGroupName = "GPN-Nodes";

    /// <summary>
    /// Çoklu düğüm YAML'inde kural hedeflerinin ve WARP dialer-proxy'sinin bağlandığı
    /// select grubu adı (<see cref="NodesGroupName"/>). Tek düğümde grup üretilmez
    /// (legacy doğrudan hedef korunur).
    /// </summary>
    public static string TunnelTargetName(bool multiNode, GpnServerProfile active)
        => multiNode ? NodesGroupName : WireGuardProxyName(active);

    /// <summary>
    /// Tek sunucu profili + aktif rota kurallarından eksiksiz mihomo YAML üretir
    /// (tek düğüm — legacy biçim; proxy-group üretilmez, kurallar doğrudan wg-&lt;id&gt;'ye
    /// işaret eder). Çoklu düğüm / kesintisiz rota için diğer aşırı yüklemelere bakın.
    /// Kurallar geldikleri sırayla yazılır; sıra anlamsaldır (önce özel, sonra genel).
    /// </summary>
    public string GenerateYaml(GpnServerProfile server, IReadOnlyList<RulesItem> rules, GpnMihomoOptions options,
        VlessProfileItem? bypass = null)
        => GenerateCore(nodes: null, active: server, policy: null, rules, options, bypass);

    /// <summary>
    /// Aday düğüm listesi + aktif düğüm + rota kurallarından eksiksiz mihomo YAML üretir.
    ///
    /// Aday sayısı 1'i geçtiğinde kesintisiz (make-before-break) düğüm geçişini
    /// mümkün kılan yapı üretilir:
    ///   proxies  → her aday için bir type: wireguard outbound (wg-&lt;id&gt;)
    ///   proxy-groups → "GPN-Nodes" select grubu (üyeler tüm wg-*; ilk üye = aktif)
    ///   rules    → vpn/proxy hedefleri doğrudan wg-&lt;id&gt; yerine "GPN-Nodes" grubuna
    ///   warp-socks → dialer-proxy: "GPN-Nodes" (zincir de seçilen düğümü izler)
    ///
    /// Böylece düğüm değişimi çekirdeği yeniden başlatmadan tek API çağrısıyla
    /// (PUT /proxies/GPN-Nodes) yapılır; mevcut bağlantılar eski düğümde boşalır.
    /// <paramref name="nodes"/> null/tek öğeli ise legacy tek-düğüm biçimi korunur.
    /// Kurallar geldikleri sırayla yazılır; sıra anlamsaldır (önce özel, sonra genel).
    /// </summary>
    public string GenerateYaml(
        IReadOnlyList<GpnServerProfile>? nodes,
        GpnServerProfile active,
        IReadOnlyList<RulesItem> rules,
        GpnMihomoOptions options,
        VlessProfileItem? bypass = null)
        => GenerateCore(nodes, active, policy: null, rules, options, bypass);

    /// <summary>
    /// Kesintisiz rota (superset) biçimi: politika (mod + yön + tüm girişler) + korunan
    /// kullanıcı kurallarından eksiksiz mihomo YAML üretir.
    ///
    /// Bu biçimde HER giriş (uygulama/domain/IP — moddan bağımsız) sabit bir kural
    /// satırı olarak yazılır ve kendi "ao-&lt;i&gt;" seçim grubuna işaret eder; yakalayıcı
    /// "GPN-MODE" grubuna gider. Grupların üye sırası, politikanın istediği seçimle
    /// (mod hedefi / giriş rotası) başlayacak şekilde kurulur — config ilk açılışta
    /// doğru rotada başlar. Sonraki mod/rota/yön değişiklikleri çekirdek restart'ı
    /// olmadan tek tek PUT /proxies çağrısıyla yapılır (make-before-break):
    ///
    ///   Off         → GPN-MODE: DIRECT + tüm ao-&lt;i&gt;: DIRECT   (tünel canlı, her şey direct)
    ///   Global VPN  → GPN-MODE: GPN-Nodes + tüm ao-&lt;i&gt;: GPN-Nodes
    ///   Game Tunnel → GPN-MODE: yöne göre (beyaz: DIRECT / kara: GPN-Nodes)
    ///                 + ao-&lt;i&gt;: girişin rotası (yön uygulanmış)
    ///
    /// <paramref name="preservedRules"/>: kullanıcının kendi kuralları (uygulama
    /// yönetimindeki kurallar DIŞINDA — onlar superset satırlarıyla değiştirilir),
    /// legacy sıraya uygun olarak yakalayıcıdan SONRA yazılır.
    /// </summary>
    public string GenerateYaml(
        IReadOnlyList<GpnServerProfile>? nodes,
        GpnServerProfile active,
        GpnSoftRoutingPolicy policy,
        IReadOnlyList<RulesItem> preservedRules,
        GpnMihomoOptions options,
        VlessProfileItem? bypass = null)
        => GenerateCore(nodes, active, policy, preservedRules ?? [], options, bypass);

    /// <summary>
    /// Tek ortak üretici: <paramref name="policy"/> null ise legacy (tek/çoklu düğüm,
    /// doğrudan hedefli kural satırları), dolu ise kesintisiz rota (superset) biçimi
    /// üretilir. Legacy dal davranışı korunur; superset dalı yalnızca grup seçimine
    /// dayalı geçişi mümkün kılan ek yapı ekler.
    /// </summary>
    private string GenerateCore(
        IReadOnlyList<GpnServerProfile>? nodes,
        GpnServerProfile active,
        GpnSoftRoutingPolicy? policy,
        IReadOnlyList<RulesItem> rules,
        GpnMihomoOptions options,
        VlessProfileItem? bypass = null)
    {
        var server = active;
        var superset = policy is not null;
        // Adayları kümeyle (ServerId) tekilleyip aktif düğümü başa al — grup seçimi
        // (ilk üye = varsayılan seçim) her zaman aktif düğümde başlasın.
        var ordered = NormalizeNodes(nodes, server);
        var multiNode = ordered.Count > 1;
        // Superset biçiminde GPN-Nodes grubu HER ZAMAN üretilir (tek düğümde bile)
        // — mod/rota seçimleri bu gruba işaret eder.
        var createTunnelGroup = multiNode || superset;
        var wgName = WireGuardProxyName(server);
        // Kural hedefleri çoklu düğümde gruba, tek düğümde doğrudan wg-<id>'ye gider.
        var tunnelTarget = createTunnelGroup ? NodesGroupName : wgName;
        // Çift Bağlantı (Bölünmüş Tünelleme): küresel VLESS/Reality launcher-bypass
        // düğümü (bypass parametresi — GuiItem.VlessBypassNodeJson'dan gelir) doluysa
        // mihomo YAML'i iki çıkış taşır — ana WG tüneli (oyun, düşük ping) + ikincil
        // vless-launcher (launcher/auth egress). Bu modda "warp" kuralları
        // vless-launcher'a gider; WARP SOCKS5 zinciri (warp-socks + dialer-proxy)
        // ÜRETİLMEZ. Bypass yoksa davranış legacy (aynı).
        var dual = bypass is not null;
        // Superset: warp-socks her zaman tanımlanır (ao grupları üye olarak
        // gösterebilir). Legacy: yalnızca en az bir warp kuralı varsa.
        var needWarp = !dual && (superset || rules.Any(r => r.Enabled && r.OutboundTag == Global.WarpTag));
        // BSG launcher/API domain kuralları yalnızca hedefleri TANIMLIYSA üretilir:
        // Çift Bağlantıda vless-launcher her zaman vardır; legacy'de warp-socks
        // yalnızca needWarp ile üretilir — tanımsız outbound'a kural yazılmaz
        // (mihomo config'i reddeder).
        var emitBsgDomains = dual || needWarp;
        var gateway = DeriveWireGuardGateway(server.ClientAddress);
        // "warp" egress'in çözüleceği proxy adı: Çift Bağlantıda ikincil VLESS
        // düğümü, legacy'de WARP SOCKS zinciri.
        var warpTarget = dual ? BypassProxyName : WarpProxyName;

        var root = new Dictionary<string, object?>
        {
            ["mode"] = "rule",
            ["ipv6"] = false,
            ["allow-lan"] = false,
            ["find-process-mode"] = "always",
            ["log-level"] = options.LogLevel,
        };

        if (options.LogFilePath.IsNotEmpty())
        {
            root["log-file"] = options.LogFilePath;
        }

        if (options.MixedPort > 0)
        {
            root["mixed-port"] = options.MixedPort;
        }
        if (options.ExternalControllerPort > 0)
        {
            root["external-controller"] = $"{Global.Loopback}:{options.ExternalControllerPort}";
        }
        if (options.InterfaceName.IsNotEmpty())
        {
            root["interface-name"] = options.InterfaceName;
        }

        // TUN — kanıtlanmış üretim topolojisi: auto-route + gvisor + fiziksel NIC dial pin'i.
        root["tun"] = new Dictionary<string, object?>
        {
            ["enable"] = true,
            ["stack"] = options.TunStack,
            ["device"] = options.TunDevice,
            ["auto-route"] = options.AutoRoute,
            ["auto-detect-interface"] = false,
            ["mtu"] = ResolveMtu(server, options),
        };

        // DNS — fake-ip + hijack: TUN içinden geçen sorgular yakalanır, uygulamalar
        // ISP'ye domain sızdırmaz; PROCESS/DOMAIN kuralları bağlantı kurarken işler.
        if (options.DnsEnabled)
        {
            var ns = options.DnsNameservers.Count > 0 ? options.DnsNameservers : new[] { "1.1.1.1", "8.8.8.8" };
            // default-nameserver: kullanıcı bootstrap verdi mi onu, vermediyse
            // nameserver listesindeki saf IP'leri, o da yoksa güvenlik ağı. mihomo
            // default-nameserver'sız DNS modülünü başlatmaz — boş bırakılmaz.
            var defaultNs = options.DnsDefaultNameservers.Count > 0
                ? options.DnsDefaultNameservers
                : ns.Where(IsPlainIpAddress).ToList();
            if (defaultNs.Count == 0)
            {
                defaultNs = ["1.1.1.1", "8.8.8.8"];
            }
            root["dns"] = new Dictionary<string, object?>
            {
                ["enable"] = true,
                ["ipv6"] = false,
                ["enhanced-mode"] = options.DnsEnhancedMode,
                ["fake-ip-range"] = options.FakeIpRange,
                ["default-nameserver"] = new List<string>(defaultNs),
                ["nameserver"] = new List<string>(ns),
            };
        }
        else
        {
            root["dns"] = new Dictionary<string, object?> { ["enable"] = false };
        }

        // ── proxies ──
        var proxies = new List<object>();
        foreach (var node in ordered)
        {
            var nodeName = WireGuardProxyName(node);
            var nodeIp = node.ClientAddress?.Split(',')[0].Trim().Split('/')[0].Trim() ?? "10.66.66.2";
            proxies.Add(BuildWireGuardProxy(node, nodeName, nodeIp, ResolveMtu(node, options), options));
        }
        if (dual)
        {
            // Çift Bağlantı: ikincil VLESS/Reality düğümü WireGuard tanımlarının
            // hemen altına yazılır (proxies sırası wg-* → vless-launcher).
            proxies.Add(BuildVlessRealityProxy(bypass!));
        }
        else if (needWarp)
        {
            // Çoklu düğümde (ve superset'te) zincir GPN-Nodes grubundan geçer → düğüm
            // değişince WARP rotaları da otomatik olarak yeni düğümü izler
            // (dialer-proxy grup adı kabul eder — mihomo ortak alan şeması).
            // Tek düğümde legacy: wg-<id>.
            proxies.Add(BuildWarpSocksProxy(gateway, tunnelTarget));
        }
        root["proxies"] = proxies;

        // ── proxy-groups ──
        var groups = new List<object>();
        if (createTunnelGroup)
        {
            groups.Add(new Dictionary<string, object?>
            {
                ["name"] = NodesGroupName,
                ["type"] = "select",
                // mihomo select grubu ilk üyeyi varsayılan seçer — aktif düğüm başa
                // konduğu için config ilk açılışta da doğru düğümde başlar.
                ["proxies"] = ordered.Select(n => WireGuardProxyName(n)).ToList(),
            });
        }

        IReadOnlyList<string>? appTargets = null;
        if (superset)
        {
            var (modeTarget, targets) = GpnSoftRouting.ComputeSelectionVector(policy!);
            // Çift Bağlantı: "warp" seçimi grup üyelerinde vless-launcher ile temsil
            // edilir (warp-socks üretilmez) — seçim vektörü ve kanonik üye sırası
            // bypass moduna göre kurulur. Legacy'de kanonik sıra aynen korunur.
            IReadOnlyList<string> canonicalAppOrder = dual
                ? new[] { NodesGroupName, GpnSoftRouting.ClashDirect, GpnSoftRouting.ClashReject, BypassProxyName }
                : GpnSoftRouting.AppMemberOrder;
            if (dual)
            {
                modeTarget = NormalizeDualMember(modeTarget);
                targets = targets.Select(NormalizeDualMember).ToList();
            }
            appTargets = targets;
            // Yakalayıcı grubu: hedef seçim ilk üye (config açılışta doğru modda başlar).
            groups.Add(new Dictionary<string, object?>
            {
                ["name"] = GpnSoftRouting.ModeGroupName,
                ["type"] = "select",
                ["proxies"] = OrderMembers(modeTarget, GpnSoftRouting.ModeMemberOrder),
            });
            // Giriş grupları: her girişin kural satırı kendi ao-<i> grubuna işaret eder.
            for (var i = 0; i < policy!.Entries.Count; i++)
            {
                groups.Add(new Dictionary<string, object?>
                {
                    ["name"] = GpnSoftRouting.AppGroupName(i),
                    ["type"] = "select",
                    ["proxies"] = OrderMembers(targets[i], canonicalAppOrder),
                });
            }
        }
        if (groups.Count > 0)
        {
            root["proxy-groups"] = groups;
        }

        // ── rules ──
        var clashRules = new List<string>();
        // BSG launcher/API domainleri her şeyden ÖNCE eşleşir (first-match-wins):
        // Tarkov auth trafiği süreç kuralından bağımsız olarak launcher-egress
        // çıkışından gider — MATCH / GPN-Nodes / FINAL'dan önce listelenir.
        if (emitBsgDomains)
        {
            foreach (var domain in BsgLauncherDomains)
            {
                clashRules.Add($"DOMAIN-SUFFIX,{domain},{warpTarget}");
            }
        }
        if (superset)
        {
            // 1) Giriş satırları (sıra = politika sırası): hedef her zaman kendi ao-<i>.
            for (var i = 0; i < policy!.Entries.Count; i++)
            {
                var entry = policy.Entries[i];
                var rule = ManualRoutingRules.BuildEntryRule(
                    entry.EntryType, entry.Value, entry.Port, entry.Action, invertManual: false);
                AppendRuleRows(rule, GpnSoftRouting.AppGroupName(i), clashRules);
            }
            // 2) Yakalayıcı — legacy'deki yönetilen catch-all satırının konumu: giriş
            //    satırlarından sonra, kullanıcı kurallarından ÖNCE (MATCH her şeyi
            //    yakaladığı için legacy'de kullanıcı satırları da aynen bu konumdadır).
            clashRules.Add($"MATCH,{GpnSoftRouting.ModeGroupName}");
            // 3) Kullanıcının korunan kuralları (legacy sıra: yönetilen satırlardan sonra).
            foreach (var rule in rules.Where(r => r.Enabled))
            {
                BuildRuleStrings(rule, NodesGroupName, warpTarget, clashRules);
            }
        }
        else
        {
            foreach (var rule in rules.Where(r => r.Enabled))
            {
                BuildRuleStrings(rule, tunnelTarget, warpTarget, clashRules);
            }
            if (clashRules.All(r => !r.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase)))
            {
                // Güvenlik ağı: her zaman sona MATCH — beyaz listede DIRECT (sızıntı yok).
                clashRules.Add($"MATCH,{_clashDirect}");
            }
        }
        root["rules"] = clashRules;

        var yaml = YamlUtils.ToYaml(root);
        if (yaml.IsNullOrEmpty())
        {
            return yaml;
        }
        // Üst bilgi: Çift Bağlantıda ikincil çıkış vless-launcher'dır; legacy'de WARP
        // SOCKS5 zinciri (10.66.66.1:40000) raporlanır — gateway satırı moda göre seçilir.
        var header = new List<string>
        {
            "# AoGPN GPN — mihomo yapılandırması (otomatik üretildi)",
            $"# Sunucu: {server.Name} ({server.EndpointHost}:{server.EndpointPort})",
        };
        header.Add(dual
            ? $"# Bypass (VLESS/Reality): {BypassProxyName} @ {bypass!.ServerAddress}:{bypass.ServerPort}"
            : $"# Gateway (WARP SOCKS): {gateway}:{Global.WarpSocksDefaultPort}");
        header.Add(string.Empty);
        return string.Join(Environment.NewLine, header) + yaml;
    }

    /// <summary>
    /// Çift Bağlantı seçim vektörünü bypass adına normalleştirir: superset seçim
    /// hesabı (GpnSoftRouting) "warp" için warp-socks adını döndürür; bu modda o
    /// outbound üretilmediğinden eşdeğeri olan vless-launcher'a çevrilir.
    /// </summary>
    private static string NormalizeDualMember(string member)
        => member == WarpProxyName ? BypassProxyName : member;

    /// <summary>
    /// Seçim grubu üye listesini kurar: istenen hedef İLK üye (select grubu ilk üyeyi
    /// varsayılan seçer — config açılışta doğru rotada başlar), kalanlar kanonik
    /// sırayla ve tekrarsız.
    /// </summary>
    private static List<string> OrderMembers(string target, IReadOnlyList<string> canonicalOrder)
    {
        var members = new List<string> { target };
        foreach (var member in canonicalOrder)
        {
            if (!members.Contains(member, StringComparer.Ordinal))
            {
                members.Add(member);
            }
        }
        return members;
    }

    /// <summary>
    /// Aday listesini tekilleyip aktif düğümü başa alır. Aday listesi null/boş ise
    /// yalnızca aktif düğümle döner — tek düğümde üretici legacy biçimi korur.
    /// </summary>
    internal static List<GpnServerProfile> NormalizeNodes(
        IReadOnlyList<GpnServerProfile>? nodes,
        GpnServerProfile active)
    {
        var result = new List<GpnServerProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(GpnServerProfile s)
        {
            if (s is null || string.IsNullOrEmpty(s.ServerId) || !seen.Add(s.ServerId))
            {
                return;
            }
            result.Add(s);
        }

        Add(active);
        if (nodes is not null)
        {
            foreach (var n in nodes)
            {
                Add(n);
            }
        }
        return result;
    }

    private static Dictionary<string, object?> BuildWireGuardProxy(
        GpnServerProfile server, string wgName, string wgIp, int mtu, GpnMihomoOptions options)
    {
        var proxy = new Dictionary<string, object?>
        {
            ["name"] = wgName,
            ["type"] = "wireguard",
            ["server"] = server.EndpointHost,
            ["port"] = server.EndpointPort,
            ["ip"] = wgIp,
            ["private-key"] = server.ClientPrivateKey,
            ["public-key"] = server.ServerPublicKey,
            ["udp"] = true,
            ["mtu"] = mtu,
        };
        var keepAlive = options.PersistentKeepalive > 0 ? options.PersistentKeepalive : server.PersistentKeepalive;
        if (keepAlive > 0)
        {
            proxy["keep-alive"] = keepAlive;
        }
        return proxy;
    }

    private static Dictionary<string, object?> BuildWarpSocksProxy(string gateway, string wgName)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = WarpProxyName,
            ["type"] = "socks5",
            ["server"] = gateway,
            ["port"] = Global.WarpSocksDefaultPort,
            ["dialer-proxy"] = wgName,
            ["udp"] = false,
        };
    }

    /// <summary>
    /// Çift Bağlantı (Bölünmüş Tünelleme) ikincil düğümünü mihomo VLESS/Reality
    /// proxy bloğuna çevirir — launcher/auth trafiği (BsGLauncher.exe) bu çıkıştan
    /// gider. Mihomo sözleşmesi: <c>type: vless</c> + <c>network: tcp</c> +
    /// <c>tls: true</c> + <c>reality-opts</c> (public-key / short-id). short-id
    /// boşsa anahtar yazılmaz (sunucu tarafı boş short-id kullanıyorsa gereksizdir).
    /// </summary>
    private static Dictionary<string, object?> BuildVlessRealityProxy(VlessProfileItem node)
    {
        var proxy = new Dictionary<string, object?>
        {
            ["name"] = BypassProxyName,
            ["type"] = "vless",
            ["server"] = node.ServerAddress,
            ["port"] = node.ServerPort,
            ["uuid"] = node.Uuid,
            ["network"] = "tcp",
            ["udp"] = false,
            ["tls"] = true,
            ["flow"] = node.Flow,
            ["servername"] = node.ServerName,
            ["client-fingerprint"] = node.Fingerprint,
        };
        if (node.PublicKey.IsNotEmpty())
        {
            var realityOpts = new Dictionary<string, object?>
            {
                ["public-key"] = node.PublicKey,
            };
            if (node.ShortId.IsNotEmpty())
            {
                realityOpts["short-id"] = node.ShortId;
            }
            proxy["reality-opts"] = realityOpts;
        }
        return proxy;
    }

    /// <summary>
    /// Tek bir kural satırını Clash kural dizisine çevirir (hedef, kuralın OutboundTag
    /// etiketinden <see cref="MapRuleTarget"/> ile türetilir). Superset üreticisi hedefi
    /// doğrudan verdiği için <see cref="AppendRuleRows"/> kullanır.
    /// </summary>
    private static void BuildRuleStrings(RulesItem rule, string fallbackTarget, string warpTarget, List<string> clashRules)
        => AppendRuleRows(rule, MapRuleTarget(rule, fallbackTarget, warpTarget), clashRules);

    /// <summary>
    /// Tek bir kuralı verilen hedefle Clash kural dizisine basar.
    ///  * process → PROCESS-NAME (her exe ayrı satır)
    ///  * domain  → DOMAIN-SUFFIX (her domain ayrı satır)
    ///  * ip      → IP-CIDR / IP-CIDR6 (bare host'a /32 veya /128 eklenir)
    ///  * port-only giriş → DST-PORT
    ///  * yakalayıcı (Port 0-65535, filtresiz) → MATCH
    /// </summary>
    private static void AppendRuleRows(RulesItem rule, string target, List<string> clashRules)
    {
        var hasProcess = rule.Process?.Any(p => p.IsNotEmpty()) == true;
        var hasDomain = rule.Domain?.Any(d => d.IsNotEmpty()) == true;
        var hasIp = rule.Ip?.Any(i => i.IsNotEmpty()) == true;

        if (!hasProcess && !hasDomain && !hasIp)
        {
            // Port 0-65535 + filtresiz = yakalayıcı (ManualRoutingRules.BuildCatchAllRule).
            if (rule.Port == "0-65535")
            {
                clashRules.Add($"MATCH,{target}");
                return;
            }
            if (rule.Port.IsNotEmpty())
            {
                clashRules.Add($"DST-PORT,{rule.Port},{target}");
            }
            return;
        }

        if (hasProcess)
        {
            foreach (var p in rule.Process!.Where(p => p.IsNotEmpty()))
            {
                clashRules.Add($"PROCESS-NAME,{p.Trim()},{target}");
            }
        }
        if (hasDomain)
        {
            foreach (var raw in rule.Domain!.Where(d => d.IsNotEmpty()))
            {
                // sing-box geosite: prefix — mihomo bunu tanimaz, atla.
                if (raw.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var domain = SanitizeDomain(raw);
                if (domain.IsNotEmpty())
                {
                    clashRules.Add($"DOMAIN-SUFFIX,{domain},{target}");
                }
            }
        }
        if (hasIp)
        {
            foreach (var raw in rule.Ip!.Where(i => i.IsNotEmpty()))
            {
                // sing-box geoip: prefix — mihomo bunu tanimaz, atla.
                if (raw.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                clashRules.Add(BuildIpCidrRule(raw.Trim(), target));
            }
        }
        // Process/domain/ip + port birlikteliği: Clash tek satırda birleştiremez;
        // v1'de hedef kuralı (process/domain/ip) basılır, port ayrımı faz-2'de eklenir.
    }

    /// <summary>
    /// Kuralın OutboundTag etiketini Clash hedefine çevirir: direct → DIRECT,
    /// block → REJECT, warp → <paramref name="warpTarget"/> (Çift Bağlantıda
    /// vless-launcher, legacy'de warp-socks), gerisi → <paramref name="fallbackTarget"/>
    /// (tünel grubu ya da wg-&lt;id&gt;).
    /// </summary>
    private static string MapRuleTarget(RulesItem rule, string fallbackTarget, string warpTarget)
    {
        return rule.OutboundTag switch
        {
            _ when rule.OutboundTag == Global.DirectTag => _clashDirect,
            _ when rule.OutboundTag == Global.BlockTag => _clashReject,
            _ when rule.OutboundTag == Global.WarpTag => warpTarget,
            _ => fallbackTarget, // vpn / proxy / bilinmeyen → tünel
        };
    }

    private static string BuildIpCidrRule(string raw, string target)
    {
        // Bare host ("1.2.3.4") → /32; IPv6 → /128 ve IP-CIDR6.
        if (raw.Contains('/'))
        {
            var prefix = raw.Split('/')[0];
            return IsIpv6(prefix) ? $"IP-CIDR6,{raw},{target}" : $"IP-CIDR,{raw},{target}";
        }
        return IsIpv6(raw) ? $"IP-CIDR6,{raw}/128,{target}" : $"IP-CIDR,{raw}/32,{target}";
    }

    private static bool IsIpv6(string ip)
        => IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>Saf IPv4/IPv6 mı? (default-nameserver'a aday)</summary>
    internal static bool IsPlainIpAddress(string server)
        => IPAddress.TryParse(server.Trim(), out _);

    /// <summary>
    /// Uygulamanın DNS alanlarını (RemoteDNS vb.) mihomo nameserver listesine
    /// çevirir: sing-box ile aynı ayırıcılar (',' veya ';'), mihomo tarafından
    /// desteklenen biçimler korunur (saf IP, udp/tcp/tls/quic/https/dhcp URL,
    /// system), diğerleri atlanır (ör. sing-box'a özgü "local"/"localhost").
    /// Boş sonuçta fallback döner.
    /// </summary>
    internal static List<string> ParseDnsServers(string? csv, IReadOnlyList<string> fallback)
    {
        var result = new List<string>();
        if (csv.IsNotEmpty())
        {
            foreach (var raw in csv!.Split(',', ';'))
            {
                var entry = raw.Trim();
                if (entry.IsNullOrEmpty())
                {
                    continue;
                }
                if (IsSupportedDnsEntry(entry))
                {
                    result.Add(entry);
                }
            }
        }
        return result.Count > 0 ? result : [.. fallback];
    }

    private static bool IsSupportedDnsEntry(string entry)
    {
        if (IsPlainIpAddress(entry))
        {
            return true;
        }
        var lower = entry.ToLowerInvariant();
        return lower is "system"
            || lower.StartsWith("udp://")
            || lower.StartsWith("tcp://")
            || lower.StartsWith("tls://")
            || lower.StartsWith("quic://")
            || lower.StartsWith("https://")
            || lower.StartsWith("dhcp://");
    }

    private static string SanitizeDomain(string raw)
    {
        var domain = raw.Trim().TrimStart('*').TrimStart('.');
        return domain.TrimEnd('.');
    }

    private static int ResolveMtu(GpnServerProfile server, GpnMihomoOptions options)
    {
        if (options.Mtu is > 0)
        {
            return options.Mtu.Value;
        }
        return server.Mtu > 0 && server.Mtu <= Global.GpnRecommendedMtu
            ? server.Mtu
            : Global.GpnRecommendedMtu;
    }

    /// <summary>
    /// "10.66.66.2/24" tipi istemci adresinden sunucu wg0 ağ geçidini türetir
    /// (ağ adresi + son oktet 1). SingboxOutboundService.DeriveWireGuardGateway
    /// ile aynı mantık — bu sınıfın bağımsız/pure kalması için kopyalandı.
    /// </summary>
    internal static string DeriveWireGuardGateway(string? interfaceAddress)
    {
        try
        {
            var cidr = interfaceAddress?.Split(',')[0].Trim() ?? string.Empty;
            if (cidr.IsNullOrEmpty())
            {
                return "10.66.66.1";
            }
            var parts = cidr.Split('/');
            var ip = IPAddress.Parse(parts[0]);
            var prefix = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 24;
            if (ip.AddressFamily != AddressFamily.InterNetwork)
            {
                return "10.66.66.1";
            }
            var bytes = ip.GetAddressBytes();
            for (var i = 0; i < 4; i++)
            {
                var bits = Math.Clamp(prefix - i * 8, 0, 8);
                var mask = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF;
                bytes[i] = (byte)(bytes[i] & mask);
            }
            bytes[3] = 1; // ağın ilk adresi = sunucu wg0
            return new IPAddress(bytes).ToString();
        }
        catch
        {
            return "10.66.66.1";
        }
    }
}
