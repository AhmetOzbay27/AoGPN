using ServiceLib.Common;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Entities;

namespace ServiceLib.Services.CoreConfig.Mihomo;

/// <summary>
/// Launcher-bypass yapılandırmasının tek okuma noktası: GuiItem.LauncherBypassesJson
/// içindeki kullanıcı listesini çözer. Ayar boş/tanımsızsa yerleşik BSG
/// varsayılanına düşer — eski sabit BsgLauncherDomains listesinin birebir
/// taşıyıcısıdır, böylece mevcut kurulumlarda kural üretimi hiç değişmez.
/// JSON bozuksa yine varsayılan kullanılır (kural üretimi asla patlamaz);
/// bilerek kaydedilmiş boş "[]" liste tüm launcher satırlarını kapatır.
/// </summary>
public static class GpnLauncherBypass
{
    /// <summary>Egress seçenekleri — LauncherBypassItem.Egress değerleri.</summary>
    public const string EgressWarp = "warp";
    public const string EgressVless = "vless";
    public const string EgressDirect = "direct";

    /// <summary>
    /// Yerleşik BSG/Tarkov domain ailesi — eski sabit listenin taşıyıcısı.
    /// escapefromtarkov.com / battlestategames.com / tarkov.com /
    /// escapefromtarkov.ru aileleri DOMAIN-SUFFIX ile tüm alt domainleri kapsar
    /// (RU launcher aynası launcher.escapefromtarkov.ru dahil); bilinen auth/API
    /// hostları (prod/launcher/gw-pvp/www, profile.tarkov.com) açıkça listelenir.
    /// </summary>
    public static LauncherBypassItem BsgDefault { get; } = new()
    {
        Name = "BSG",
        Egress = EgressWarp,
        Enabled = true,
        Domains =
        [
            "escapefromtarkov.com",
            "battlestategames.com",
            "tarkov.com",
            "escapefromtarkov.ru", // RU launcher ailesi — launcher.escapefromtarkov.ru
            "prod.escapefromtarkov.com",
            "launcher.escapefromtarkov.com",
            "launcher.escapefromtarkov.ru",
            "gw-pvp.escapefromtarkov.com",
            "www.escapefromtarkov.com",
            "profile.tarkov.com",
        ],
    };

    /// <summary>
    /// Dashboard "Add Launcher" menüsünde önerilen hazır önayarlar. Yalnızca
    /// kullanıcının seçmesiyle devreye girer — varsayılan listeye yazılmazlar
    /// (mevcut davranışı değiştirmemek için).
    /// </summary>
    public static IReadOnlyList<LauncherBypassItem> Presets { get; } =
    [
        BsgDefault,
        new() { Name = "Epic Games", Egress = EgressWarp, Domains = ["epicgames.com", "unrealengine.com"] },
        new() { Name = "Steam", Egress = EgressWarp, Domains = ["steampowered.com", "steamgames.com", "steamstatic.com"] },
        new() { Name = "Riot", Egress = EgressWarp, Domains = ["riotgames.com", "riotcdn.net"] },
    ];

    /// <summary>
    /// Kullanıcı launcher-bypass listesini çözer. Boş/tanımsız ayar veya bozuk
    /// JSON → varsayılan [BSG]; "[]" → boş liste (tüm satırlar kapalı). Null
    /// girişler ve null domain öğeleri elenir.
    /// </summary>
    public static IReadOnlyList<LauncherBypassItem> ReadAll(Config? config)
    {
        var json = config?.GuiItem?.LauncherBypassesJson;
        if (json.IsNullOrEmpty())
        {
            return [BsgDefault];
        }
        var list = JsonUtils.Deserialize<List<LauncherBypassItem>>(json);
        if (list is null)
        {
            // Bozuk JSON — sessizce varsayılana düş (kural üretimi asla patlamaz).
            return [BsgDefault];
        }
        return list.Where(item => item is not null).ToList();
    }
}