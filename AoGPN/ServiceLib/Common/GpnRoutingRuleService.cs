namespace ServiceLib.Common;

/// <summary>
/// Yönlendirme OTORİTESİ (Tier 2 — Rota Merkezileştirmesi): manuel listenin bir
/// girişinin / yakalayıcının NE yapacağına karar veren TEK kaynak.
///
/// Çekirdekler aynı kararı kendi söz varlığına çevirir — karar mantığı asla
/// çoğaltılmaz:
///   * sing-box/legacy kurallar  → <see cref="ManualRoutingRules"/> (OutboundTag:
///     Direct/Proxy/Warp/Block etiketleri),
///   * mihomo superset seçimleri  → <see cref="GpnSoftRouting"/> (DIRECT / REJECT /
///     warp-socks-vless-launcher / GPN-Nodes üyeleri),
///   * native yakalama motoru     → <see cref="GpnTargetResolverBridge"/> (Tunnel/
///     WarpEgress varışlı "app" girişleri WinDivert filtresine girer).
///
/// İki eksen vardır:
///   1. <see cref="ResolveDestination"/> — bir girişin ACTION + yön (whitelist/
///      blacklist) → nötr varış yeri.
///   2. <see cref="ResolveCatchAll"/>    — mod + yön → yakalayıcı (unlisted) varışı.
/// <see cref="BuildPlan"/> ikisini birleştirip sıralı, normalize edilmiş planı üretir.
/// </summary>
public enum GpnRoutingDestination
{
    /// <summary>Listelenen trafik tünele GİRMEZ (doğrudan internete).</summary>
    Direct,

    /// <summary>Trafik GPN WireGuard tüneline girer (oyun / vpn egress).</summary>
    Tunnel,

    /// <summary>Trafik tünelden geçip sunucu tarafı WARP egress'inden çıkar (Çift Bağlantıda vless-launcher).</summary>
    WarpEgress,

    /// <summary>Trafik engellenir.</summary>
    Block,
}

/// <summary>
/// Otoritenin tükettiği yapısal (karar-öncesi) giriş biçimi: tür + değer + port +
/// eylem. Görünüm modeli (SplitTunnelAppItem), kayıtlı ayar (ManualRouteSetting) ve
/// mihomo politika kaydı (GpnSoftRoutingEntry) bu biçime izdüşürülür; normalize
/// edilmiş liste <see cref="GpnRoutingRuleService.NormalizeEntries"/> üretir.
/// </summary>
public readonly record struct GpnRouteEntry(string EntryType, string Value, string Port, string Action)
{
    /// <summary>Görünüm modelindeki uygulama listesi satırından izdüşüm.</summary>
    public static GpnRouteEntry From(SplitTunnelAppItem app)
        => new(app.EntryType ?? string.Empty, app.Value ?? string.Empty, app.Port ?? string.Empty, app.Action ?? "vpn");

    /// <summary>Kayıtlı (config) manuel rota ayarından izdüşüm.</summary>
    public static GpnRouteEntry From(ManualRouteSetting route)
        => new(route.EntryType ?? string.Empty, route.Value ?? string.Empty, route.Port ?? string.Empty, route.Action ?? "vpn");
}

/// <summary>Planın tek bir girişi: normalize edilmiş giriş + o moddaki varış yeri.</summary>
public sealed record GpnRoutingPlanEntry(GpnRouteEntry Entry, GpnRoutingDestination Destination);

/// <summary>
/// Mod + yön + sıralı girişler için tam nötr rota planı: yakalayıcı varışı ve her
/// girişin varışı. Off / Global VPN modlarında girişler modun gölgesindedir
/// (destination = yakalayıcı); Manuel modda her giriş kendi kararını taşır.
/// Native motor canlıya alındığında (Tier 2 — Live) bu plandan beslenir.
/// </summary>
public sealed record GpnRoutingPlan(
    int Mode,
    bool InvertManualRouting,
    GpnRoutingDestination CatchAll,
    IReadOnlyList<GpnRoutingPlanEntry> Entries);

/// <summary>
/// Rota kararlarının tek sahibi (bkz. <see cref="GpnRoutingDestination"/>). Saf ve
/// bağımsızdır — çekirdek/UI bağımlılığı yoktur, her çekirdek kendi söz varlığına
/// buradan çevirir. Davranış, legacy kural üreticisinin (ManualRoutingRules) ve
/// mihomo superset seçim hesabının (GpnSoftRouting) BİREBİR aynısıdır — testler
/// eşitliği action × yön matrisinde kilitler.
/// </summary>
public static class GpnRoutingRuleService
{
    /// <summary>
    /// Bir girişin "action" değerini yön (whitelist/blacklist) ile birlikte nötr
    /// varış yerine çevirir. Kara listede (invert) listelenen girişler İSTİSNADIR:
    /// tünel-benzeri seçimler (vpn/proxy/warp) direct kalır, açık "direct" tünele
    /// çevrilir, block block'a. Beyaz listede direct→direct, block→block,
    /// warp→WARP egress, gerisi (vpn/proxy/vpn+proxy/bilinmeyen)→tünel.
    /// </summary>
    public static GpnRoutingDestination ResolveDestination(string? action, bool invertManual)
    {
        if (invertManual)
        {
            return action switch
            {
                "direct" => GpnRoutingDestination.Tunnel,
                "block" => GpnRoutingDestination.Block,
                _ => GpnRoutingDestination.Direct, // vpn / proxy / warp tünel-benzeri → listede istisna = direct
            };
        }
        return action switch
        {
            "direct" => GpnRoutingDestination.Direct,
            "block" => GpnRoutingDestination.Block,
            "warp" => GpnRoutingDestination.WarpEgress,
            _ => GpnRoutingDestination.Tunnel, // vpn / proxy / vpn+proxy / bilinmeyen → tünel
        };
    }

    /// <summary>
    /// Yakalayıcı (unlisted) varışı: mod + yön → nötr sonuç. Global VPN → tünel;
    /// Manuel + kara liste → tünel (listelenenler istisna olduğundan gerisi tünellenir);
    /// diğer tümü (Off, Manuel + beyaz liste) → direct.
    /// </summary>
    public static GpnRoutingDestination ResolveCatchAll(int mode, bool invertManual)
    {
        return mode switch
        {
            GameTriggerModes.Vpn => GpnRoutingDestination.Tunnel,
            GameTriggerModes.Manual when invertManual => GpnRoutingDestination.Tunnel,
            _ => GpnRoutingDestination.Direct,
        };
    }

    /// <summary>
    /// Girişleri normalize eder: boş değerli satırlar atlanır, port varsayılanı "",
    /// eylem varsayılanı "vpn". Sıra korunur (çekirdekler üstten alta eşleşir).
    /// </summary>
    public static IReadOnlyList<GpnRouteEntry> NormalizeEntries(IEnumerable<GpnRouteEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(e => e.Value.IsNotEmpty())
            .Select(e => e with { Port = e.Port ?? string.Empty, Action = e.Action ?? "vpn" })
            .ToList();
    }

    /// <summary>
    /// Tam nötr rota planı: normalize edilmiş girişler + yakalayıcı varışı + her
    /// girişin o moddaki varışı. Off / Global VPN'de girişler modun gölgesindedir
    /// (destination = yakalayıcı — legacy davranış); Manuel'de her giriş kendi
    /// kararını (yön uygulanmış) taşır.
    /// </summary>
    public static GpnRoutingPlan BuildPlan(int mode, bool invertManual, IEnumerable<GpnRouteEntry> entries)
    {
        var normalized = NormalizeEntries(entries);
        var catchAll = ResolveCatchAll(mode, invertManual);
        var resolved = normalized
            .Select(e => new GpnRoutingPlanEntry(
                e,
                mode == GameTriggerModes.Manual
                    ? ResolveDestination(e.Action, invertManual)
                    : catchAll))
            .ToList();
        return new GpnRoutingPlan(mode, invertManual, catchAll, resolved);
    }

    /// <summary>Görünüm modeli listesinden doğrudan plan kurar (uygulama rotaları).</summary>
    public static GpnRoutingPlan BuildPlan(int mode, bool invertManual, IEnumerable<SplitTunnelAppItem> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        return BuildPlan(mode, invertManual, apps.Select(GpnRouteEntry.From));
    }

    /// <summary>
    /// Native yakalama eşiği: varışı tünel ya da WARP egress olan girişler WinDivert
    /// filtresine girer (trafikleri tünelden geçer); Direct/Block kalanlar yakalanmaz.
    /// Kurallarla AYNI karar kaynağı — kara listede "direct" girişler tünele
    /// çevrildiği için onlar yakalanır, "vpn"/"warp" eylemli istisnalar yakalanmaz.
    /// </summary>
    public static bool IsCapturedByNativeTunnel(GpnRoutingDestination destination)
        => destination is GpnRoutingDestination.Tunnel or GpnRoutingDestination.WarpEgress;
}
