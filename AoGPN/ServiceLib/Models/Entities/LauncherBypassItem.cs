namespace ServiceLib.Models.Entities;

/// <summary>
/// Kullanıcı düzenlenebilir "launcher bypass" girişi: bir oyun başlatıcısının
/// (BSG, Epic, Steam, Riot …) kimlik doğrulama/API domain ailesi ve bunların
/// kullanacağı egress seçimi. Eski sabit <c>BsgLauncherDomains</c> listesinin
/// yerini alır — her launcher kendi domain listesiyle (DOMAIN-SUFFIX eşleşmesi)
/// ve kendi çıkış hedefiyle tanımlanır:
///
///   warp   → launcher-egress (Çift Bağlantıda vless-launcher, legacy'de
///            warp-socks; superset-legacy'de GPN-LAUNCHER seçim grubundan
///            geçer — GpnBypassEgressController WARP faulted iken canlı
///            (restart'sız) DIRECT'e çeker, sağlık gelince geri döner),
///   vless  → yalnızca Çift Bağlantıda anlamlıdır (vless-launcher outbound);
///            düğüm tanımlı değilse legacy warp zincirine düşer,
///   direct → her biçimde doğrudan çıkar (launcher trafiği tünele girmez).
///
/// Ayar <c>GuiItem.LauncherBypassesJson</c> içinde JSON liste olarak saklanır;
/// boş/tanımsızsa yerleşik BSG varsayılanı kullanılır (eski sabit liste —
/// mevcut davranış korunur). Dashboard Ayarlar → GPN panelinden düzenlenir.
/// </summary>
[Serializable]
public sealed class LauncherBypassItem
{
    /// <summary>Görünen ad ("BSG", "Epic Games", …) — yalnızca gösterim/okunabilirlik.</summary>
    public string Name { get; set; } = "BSG";

    /// <summary>DOMAIN-SUFFIX eşleşecek domain aileleri (alt domainler dahil).</summary>
    public string[] Domains { get; set; } = [];

    /// <summary>Egress seçimi: "warp" | "vless" | "direct" (bkz. GpnLauncherBypass sabitleri).</summary>
    public string Egress { get; set; } = "warp";

    /// <summary>Satır kural üretimine katılır mı? (varsayılan true)</summary>
    public bool Enabled { get; set; } = true;
}