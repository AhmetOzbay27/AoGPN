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
/// </summary>
public sealed record GpnSoftRoutingPolicy(
    int Mode,
    bool InvertManualRouting,
    IReadOnlyList<GpnSoftRoutingEntry> Entries,
    string? WarpEgressProxy = null)
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
/// Superset config'te üç grup ailesi üretilir:
///   * "GPN-Nodes"  — tüm WG düğümleri (düğüm değişimi; Faz 1),
///   * "GPN-MODE"   — yakalayıcı hedef: DIRECT | GPN-Nodes (mod değişimi),
///   * "ao-&lt;i&gt;"     — i. girişin kural satırının hedefi (uygulama rota değişimi).
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
    /// <summary>Yakalayıcı (unlisted) hedef grubu: DIRECT | GPN-Nodes üyeleriyle.</summary>
    public const string ModeGroupName = "GPN-MODE";

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

    /// <summary>Yakalayıcı (unlisted) hedefi: mod + yön → DIRECT ya da tünel grubu.</summary>
    public static string ModeTarget(int mode, bool invertManual)
        => GpnRoutingRuleService.ResolveCatchAll(mode, invertManual) switch
        {
            // Yakalayıcı yalnızca DIRECT ya da tünele gider (WarpEgress/Block olamaz).
            GpnRoutingDestination.Tunnel => GpnMihomoConfigService.NodesGroupName,
            _ => ClashDirect,
        };

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
        return new GpnSoftRoutingPolicy(mode, invert, entries, ResolveWarpEgressProxy(config));
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
        return new GpnSoftRoutingPolicy(mode, invertManualRouting, entries, ResolveWarpEgressProxy(config));
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
