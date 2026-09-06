namespace ServiceLib.Services.CoreConfig.Mihomo;

/// <summary>
/// Bir uygulama rotası (manuel liste satırı) için üretilen "ao-&lt;i&gt;" seçim grubunun
/// hangi düğüme/yola işaret ettiğini taşıyan salt veri kaydı. Girişler (uygulama /
/// domain / IP) sıralıdır — kural satırları ve grup adları bu sıraya göre üretilir.
/// </summary>
public sealed record GpnSoftRoutingEntry(string EntryType, string Value, string Port, string Action);

/// <summary>
/// GPN mihomo "soft" (kesintisiz) rotalama politikası: rota modu + yön + sıralı giriş
/// listesi. Çalışan çekirdeğin YAML'i bu politikadan bir kez üretilir (tüm girişler
/// sabit kural satırı olarak yazılır, her biri kendi seçim grubuna işaret eder);
/// mod/uygulama rota değişiklikleri daha sonra çekirdek yeniden başlatılmadan yalnızca
/// grup seçimini (PUT /proxies) değiştirir.
///
/// <see cref="WarpEgressProxy"/>: Çift Bağlantı (Bölünmüş Tünelleme) durumunda küresel
/// launcher-bypass düğümünün seçim grubu üye adı (vless-launcher). Dolu olduğunda
/// "warp" egressli girişler (BsGLauncher.exe) bu düğüme işaret eder; null → legacy
/// WARP SOCKS5 zinciri (warp-socks). Politika, küresel ayardan (GuiItem
/// .VlessBypassNodeJson) <see cref="GpnSoftRouting.BuildPolicy(Config)"/> tarafından
/// doldurulur — üretici ve yumuşak uygulayıcı böylece her zaman aynı egress'i hedefler.
///
/// <see cref="IpCheckExtraDomains"/>: kullanıcının Ayarlar → IPAPIUrl ayarındaki host'un
/// ek IP-doğrulama domainidir (null/boş = yalnızca yerleşik hostlar). Üretici superset
/// config'te bu domainlere sabit GPN-CHECK kural satırları basar — uygulamanın kendi
/// doğrulama istekleri (AoGPN.exe → api.ip.sb) beyaz listede bile WG tünelinden çıkar.
/// </summary>
public sealed record GpnSoftRoutingPolicy(
    int Mode,
    bool InvertManualRouting,
    IReadOnlyList<GpnSoftRoutingEntry> Entries,
    string? WarpEgressProxy = null,
    IReadOnlyList<string>? IpCheckExtraDomains = null)
{
    /// <summary>
    /// Giriş kimlik anahtarları (sıralı) — çalışan config'in hangi giriş setiyle
    /// üretildiğinin parmak izi. Yalnızca yapı (tür+değer+port) kimliğini taşır;
    /// rota seçimleri (Action) bilinçli olarak DIŞARIDADIR çünkü onlar seçimle değişir.
    /// </summary>
    public IReadOnlyList<string> EntryKeys => Entries.Select(GpnSoftRouting.EntryKey).ToList();
}

/// <summary>
/// Kesintisiz (make-before-break) GPN rota geçişi için saf yardımcılar.
///
/// Superset config'te dört grup ailesi üretilir:
///   * "GPN-Nodes"  — tüm WG düğümleri (düğüm değişimi; Faz 1),
///   * "GPN-MODE"   — yakalayıcı hedef: DIRECT | GPN-Nodes (mod değişimi),
///   * "ao-&lt;i&gt;"     — i. girişin kural satırının hedefi (uygulama rota değişimi),
///   * "GPN-CHECK"  — IP doğrulama hostları (uygulamanın kendi api.ip.sb benzeri
///                    istekleri): bağlıyken tünele, Off'ta DIRECT'e gider (bkz.
///                    <see cref="CheckGroupTarget"/>).
///
/// Mod/yön değişimi TÜM grupların seçim vektörünü yeniden hesaplar (giriş kuralları
/// moddan bağımsız sabit satırlar olduğu için): Off → her şey DIRECT, Global VPN →
/// her şey GPN-Nodes, Oyun Tüneli (Manuel) → girişler kendi rotasına + yakalayıcı
/// yöne göre. Her vektör, legacy kural üreticisinin (ManualRoutingRules) o moddaki
/// davranışıyla BİREBİR aynı trafik sonucunu verir — sadece çekirdek restart'ı yerine
/// seçim PUT'ları kullanılır.
/// </summary>
public static class GpnSoftRouting
{
    /// <summary>
    /// Launcher egress grubu: DIRECT | warp egress üyeleriyle (yalnızca
    /// superset-legacy biçimde üretilir; warp egress'li launcher bypass satırları
    /// bu gruba bağlanır). Bu gruba bağlanan sabit DOMAIN-SUFFIX satırları,
    /// WarpDialHealthMonitor faulted olduğunda GpnBypassEgressController
    /// tarafından canlı (restart'sız) DIRECT'e çevrilir — WARP zinciri ölüyken
    /// launcher trafiği hata almak yerine doğrudan çıkar ve WAF engeli yerine
    /// bağlantı korunur; sağlıklıyken grup warp egress'e döner.
    /// </summary>
    public const string LauncherGroupName = "GPN-LAUNCHER";

    /// <summary>Yakalayıcı (unlisted) hedef grubu: DIRECT | GPN-Nodes üyeleriyle.</summary>
    public const string ModeGroupName = "GPN-MODE";

    /// <summary>
    /// IP doğrulama hedef grubu: DIRECT | GPN-Nodes üyeleriyle. Üretici superset
    /// config'te uygulamanın kendi IP-doğrulama hostlarına (api.ip.sb vb.) sabit
    /// DOMAIN-SUFFIX satırları basar; bu satırların hedefi bu gruptur. Grup seçimi
    /// modla birlikte <see cref="CheckGroupTarget"/> ile hesaplanır ve yumuşak
    /// uygulayıcı (GpnSoftPolicyApplier) mod değişimlerinde GPN-MODE ile birlikte
    /// PUT'lar — böylece doğrulama isteği bağlıyken aktif WG tünelinden çıkar
    /// ("IP değişmedi" yanlış alarmı üretilmez), Off'ta DIRECT'e dönerek ISP
    /// baz çizgisi ölçümü bozulmaz.
    /// </summary>
    public const string CheckGroupName = "GPN-CHECK";

    /// <summary>i. girişin seçim grubu adı (kural satırı bu gruba işaret eder).</summary>
    public static string AppGroupName(int index) => $"ao-{index}";

    /// <summary>mihomo özel hedefleri — seçim grubu üyeleri olarak da kullanılır.</summary>
    public const string ClashDirect = "DIRECT";
    public const string ClashReject = "REJECT";

    /// <summary>Seçim grubu üyelerinin kanonik (varsayılan) sırası.</summary>
    public static readonly IReadOnlyList<string> AppMemberOrder = new[]
    {
        GpnMihomoConfigService.NodesGroupName,
        ClashDirect,
        ClashReject,
        GpnMihomoConfigService.WarpProxyName,
    };

    /// <summary>Mod grubunun olası üyeleri (yakalayıcı yalnızca DIRECT ya da tünele gider).</summary>
    public static readonly IReadOnlyList<string> ModeMemberOrder = new[]
    {
        GpnMihomoConfigService.NodesGroupName,
        ClashDirect,
    };

    /// <summary>
    /// Bir girişin "action" değerini seçim grubu üyesine çevirir — ManualRoutingRules'ın
    /// (whitelist/blacklist yönü dahil) davranışını birebir yansıtır. "warp" egress
    /// <paramref name="warpEgressProxy"/> ile çözülür: Çift Bağlantı (Bölünmüş
    /// Tünelleme) oturumunda vless-launcher (küresel bypass düğümü), legacy'de
    /// warp-socks. Çağıranlar politikadaki <see cref="GpnSoftRoutingPolicy.WarpEgressProxy"/>
    /// değerini iletir; null/boş ise legacy davranış korunur.
    /// </summary>
    public static string MapActionToMember(string? action, bool invertManual, string? warpEgressProxy = null)
    {
        // Eylem + yön kararı tek otoritede (GpnRoutingRuleService.ResolveDestination —
        // kara liste çevirisi dahil); burada yalnızca Clash üye adına çevrilir.
        var destination = GpnRoutingRuleService.ResolveDestination(action, invertManual);
        return destination switch
        {
            GpnRoutingDestination.Direct => ClashDirect,
            GpnRoutingDestination.Block => ClashReject,
            // Çift Bağlantıda "warp" (temiz/auth egress) vless-launcher'a gider;
            // bypass yoksa legacy WARP SOCKS zinciri (warp-socks) kullanılır.
            GpnRoutingDestination.WarpEgress => !string.IsNullOrEmpty(warpEgressProxy) ? warpEgressProxy! : GpnMihomoConfigService.WarpProxyName,
            _ => GpnMihomoConfigService.NodesGroupName, // Tunnel → düğüm grubu
        };
    }

    /// <summary>
    /// Launcher egress grubunun istenen seçimi: degrade (WARP faulted) →
    /// DIRECT, sağlıklı → warp egress üyesi (Çift Bağlantıda vless-launcher,
    /// legacy'de warp-socks). Bu değer GpnBypassEgressController tarafından
    /// canlı seçim PUT'larına çevrilir; üretici grubu varsayılan olarak sağlıklı
    /// hedefle açar.
    /// </summary>
    public static string ResolveLauncherEgressTarget(bool degraded, string? warpEgressProxy = null)
        => degraded
            ? ClashDirect
            : string.IsNullOrEmpty(warpEgressProxy) ? GpnMihomoConfigService.WarpProxyName : warpEgressProxy!;

    /// <summary>Launcher egress grubunun kanonik üye sırası: warp egress önce (varsayılan seçim), DIRECT sonra.</summary>
    public static IReadOnlyList<string> LauncherMemberOrder(string? warpEgressProxy = null)
        => new[] { ResolveLauncherEgressTarget(false, warpEgressProxy), ClashDirect };

    /// <summary>Yakalayıcı (unlisted) hedefi: mod + yön → DIRECT ya da tünel grubu.</summary>
    public static string ModeTarget(int mode, bool invertManual)
        => GpnRoutingRuleService.ResolveCatchAll(mode, invertManual) switch
        {
            // Yakalayıcı yalnızca DIRECT ya da tünele gider (WarpEgress/Block olamaz).
            GpnRoutingDestination.Tunnel => GpnMihomoConfigService.NodesGroupName,
            _ => ClashDirect,
        };

    /// <summary>
    /// IP doğrulama (GPN-CHECK) grubunun moda göre istenen seçimi: Off → DIRECT,
    /// bağlı her mod (Global VPN / Manuel — yön fark etmeksizin) → tünel grubu.
    /// Bağlıyken uygulamanın kendi doğrulama istekleri aktif WG tünelinden çıkar ve
    /// panel gerçek tünel çıkışını ölçer; Off'ta DIRECT'e döner ki ISP baz çizgisi
    /// (disconnected ölçüm) tünel IP'siyle kirlenmesin.
    /// </summary>
    public static string CheckGroupTarget(int mode)
        => mode == GameTriggerModes.Off
            ? ClashDirect
            : GpnMihomoConfigService.NodesGroupName;

    /// <summary>
    /// İstenen politika için tam seçim vektörü (mod hedefi + her girişin hedefi).
    /// Off ve Global VPN modlarında giriş kuralları yok sayılır (legacy davranış) —
    /// bu yüzden bu modlarda tüm giriş grupları sırasıyla DIRECT/GPN-Nodes'a çekilir.
    /// "warp" egress girişleri politikanın <see cref="GpnSoftRoutingPolicy.WarpEgressProxy"/>
    /// değeriyle çözülür (Çift Bağlantıda vless-launcher, legacy'de warp-socks).
    /// </summary>
    public static (string ModeTarget, IReadOnlyList<string> AppTargets) ComputeSelectionVector(
        GpnSoftRoutingPolicy policy)
    {
        var modeTarget = ModeTarget(policy.Mode, policy.InvertManualRouting);
        var appTargets = new List<string>(policy.Entries.Count);
        foreach (var entry in policy.Entries)
        {
            if (policy.Mode == GameTriggerModes.Manual)
            {
                appTargets.Add(MapActionToMember(entry.Action, policy.InvertManualRouting, policy.WarpEgressProxy));
            }
            else
            {
                // Off / Global VPN: girişler modun gölgesinde — tümü DIRECT ya da tünel.
                appTargets.Add(modeTarget);
            }
        }
        return (modeTarget, appTargets);
    }

    /// <summary>Bir girişin yapısal kimlik anahtarı (parmak izi / grup eşleştirme).</summary>
    public static string EntryKey(GpnSoftRoutingEntry entry)
        => $"{entry.EntryType}|{entry.Value}|{entry.Port}";

    /// <summary>
    /// Canlı superset oturumu varken giriş listesi YAPISAL olarak değişti mi?
    /// (giriş eklendi/silindi/sıralandı — rota seçimleri değil, EntryKeys rota
    /// dışıdır). Doğruysa çekirdek restart'ı yalnızca bu yapısal değişiklikler için
    /// gereklidir ve çağıran (SplitTunnelViewModel) maç ortası kopmayı önlemek için
    /// uygulamayı sonraki doğal yeniden bağlantıya erteleyebilir; kurallar zaten
    /// kaydedildiği için sonraki bağlantı config'i yeni kurallarla üretir.
    /// </summary>
    public static bool IsStructuralEntryChange(IReadOnlyList<string>? liveFingerprint, GpnSoftRoutingPolicy desired)
    {
        if (liveFingerprint is null || desired is null)
        {
            return false;
        }
        var desiredKeys = desired.EntryKeys;
        return desiredKeys.Count != liveFingerprint.Count || !desiredKeys.SequenceEqual(liveFingerprint);
    }

    /// <summary>Kayıtlı bağlantı ayarlarından (ConnectionItem) politika üretir.</summary>
    public static GpnSoftRoutingPolicy BuildPolicy(Config config)
    {
        var ci = config.ConnectionItem;
        var mode = ci?.Mode ?? GameTriggerModes.Off;
        var invert = ci?.InvertManualRouting ?? false;
        // Girişlerin normalize edilmesi tek otoritededir (boş değer atlama + port/eylem
        // varsayılanları) — uygulama listesi aşırı yüklemesiyle aynı kod yolu.
        var entries = GpnRoutingRuleService.NormalizeEntries((ci?.ManualRoutes ?? []).Select(GpnRouteEntry.From))
            .Select(e => new GpnSoftRoutingEntry(e.EntryType, e.Value, e.Port, e.Action))
            .ToList();
        return new GpnSoftRoutingPolicy(mode, invert, entries, ResolveWarpEgressProxy(config),
            ResolveIpCheckExtraDomains(config?.SpeedTestItem?.IPAPIUrl));
    }

    /// <summary>
    /// Görünüm modelindeki güncel uygulama listesinden politika üretir. Çift Bağlantı
    /// egress'i <paramref name="config"/> üzerinden çözülür (verilirse); null ise
    /// legacy varsayılanı korunur (warp → warp-socks).
    /// </summary>
    public static GpnSoftRoutingPolicy BuildPolicy(
        int mode,
        bool invertManualRouting,
        IEnumerable<SplitTunnelAppItem> apps,
        Config? config = null)
    {
        var entries = GpnRoutingRuleService.NormalizeEntries(apps.Select(GpnRouteEntry.From))
            .Select(e => new GpnSoftRoutingEntry(e.EntryType, e.Value, e.Port, e.Action))
            .ToList();
        return new GpnSoftRoutingPolicy(mode, invertManualRouting, entries, ResolveWarpEgressProxy(config),
            ResolveIpCheckExtraDomains(config?.SpeedTestItem?.IPAPIUrl));
    }

    /// <summary>
    /// Kullanıcının Ayarlar → IPAPIUrl ayarındaki host'u ek IP-doğrulama domaini
    /// olarak çözer (üreticinin GPN-CHECK satırları yerleşik hostlara bunu da ekler).
    /// URL çözümlenemezse, host boşsa ya da saf IP ise null döner — GPN-CHECK
    /// satırları yalnızca yerleşik hostlarla (ip.sb, ipinfo.io, ip-api.com, ...)
    /// basılır.
    /// </summary>
    public static IReadOnlyList<string>? ResolveIpCheckExtraDomains(string? ipApiUrl)
    {
        if (ipApiUrl.IsNullOrEmpty())
        {
            return null;
        }
        if (!Uri.TryCreate(ipApiUrl, UriKind.Absolute, out var uri) || uri.Host.IsNullOrEmpty())
        {
            return null;
        }
        // Saf IP hedef DOMAIN-SUFFIX ile eşleşmez; yalnızca gerçek hostname'ler anlamlıdır.
        if (IPAddress.TryParse(uri.Host, out _))
        {
            return null;
        }
        return [uri.Host.ToLowerInvariant()];
    }

    /// <summary>
    /// Küresel launcher-bypass ayarından (GuiItem.VlessBypassNodeJson) Çift Bağlantı
    /// "warp" egress üye adını çözer: ayar dolu VE geçerli bir VlessProfileItem'a
    /// açılıyorsa <see cref="GpnMihomoConfigService.BypassProxyName"/> (vless-launcher),
    /// değilse null → legacy WARP SOCKS5 davranışı (warp-socks). Kullanılan eşik
    /// GpnCoreLauncher'ın (GpnVlessBypass context kararı) ve mihomo üreticisinin
    /// (bypass parametresi) eşiğiyle birebir aynıdır — böylece politikanın seçim
    /// vektörü ile üretilen YAML her zaman aynı egress düğümünü hedefler.
    /// </summary>
    public static string? ResolveWarpEgressProxy(Config? config)
    {
        var json = config?.GuiItem?.VlessBypassNodeJson;
        if (json.IsNullOrEmpty())
        {
            return null;
        }
        return JsonUtils.Deserialize<VlessProfileItem>(json) is null
            ? null
            : GpnMihomoConfigService.BypassProxyName;
    }
}
