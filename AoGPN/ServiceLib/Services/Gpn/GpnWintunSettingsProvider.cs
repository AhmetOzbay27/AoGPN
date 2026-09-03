using System.Text;
using ServiceLib.Manager;
using ServiceLib.Models.Configs;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnWintunSettings — Wintun adaptörü ayarlarından WireGuardTunnelService.Open
// parametrelerinin üretimi
//
// GpnWintunItem (Config) → GpnWintunOptions:
//   * AdapterName  → adapter ad ÖN EKİ (bağlantıda sunucu kimliği eklenir:
//                    "AoGPN" + "it" → "AoGPN-it"). Yalnızca [A-Za-z0-9_-]
//                    izinli; boş/güvensiz girdi varsayılana döner.
//   * RingCapacity → Wintun halka tampon kapasitesi; 128 KiB..64 MiB aralığına
//                    sınırlanır ve 2'nin katına yuvarlanır (halka tampon
//                    gereksinimi — WireGuardTunnelService.Open aynı normalleşmeyi
//                    açılışta da yapar).
//
// Değerler bir sonraki WireGuard bağlantısında uygulanır (GpnCaptureBridge köprü
// açılışı bu options'ı tünele verir); çalışan köprü mevcut parametrelerini korur.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>Wintun köprü açılışı için türetilmiş parametreler (WireGuardTunnelService.Open'a gider).</summary>
public sealed record GpnWintunOptions(
    string AdapterName,
    uint RingCapacity)
{
    /// <summary>Varsayılan seçenekler — resmi wintun örneğiyle aynı (4 MiB halka).</summary>
    public static GpnWintunOptions Default { get; } = new("AoGPN", 0x400000);
}

/// <summary>
/// GpnWintunItem ayar bloğunu <see cref="GpnWintunOptions"/>'a çeviren saf eşleyici.
/// </summary>
public static class GpnWintunSettingsMapper
{
    /// <summary>Wintun halka tampon kapasitesi aralığı (sınırlama + JS ipucu için).</summary>
    public const uint MinRingCapacity = 0x20000;   // 128 KiB
    public const uint MaxRingCapacity = 0x4000000; // 64 MiB

    /// <summary>
    /// Adapter ad ön ekini güvenli hale getirir: [A-Za-z0-9_-] dışındaki karakterler
    /// atılır, 32 karaktere kısaltılır; sonuç boşsa varsayılan "AoGPN" döner.
    /// </summary>
    public static string SanitizeAdapterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "AoGPN";
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            {
                sb.Append(c);
                if (sb.Length >= 32)
                {
                    break;
                }
            }
        }
        return sb.Length > 0 ? sb.ToString() : "AoGPN";
    }

    /// <summary>Kapasiteyi geçerli aralığa sınırlar ve 2'nin katına yuvarlar.</summary>
    public static uint NormalizeRingCapacity(uint capacity)
    {
        capacity = Math.Clamp(capacity, MinRingCapacity, MaxRingCapacity);
        var power = MinRingCapacity;
        while (power < capacity && power < MaxRingCapacity)
        {
            power <<= 1;
        }
        return power;
    }

    /// <summary>Ayarlardan WireGuardTunnelService.Open parametrelerini üretir.</summary>
    public static GpnWintunOptions ToOptions(GpnWintunItem settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GpnWintunOptions(
            SanitizeAdapterName(settings.AdapterName),
            NormalizeRingCapacity(settings.RingCapacity));
    }
}

/// <summary>
/// Dashboard "Wintun adapter" kartından gelen set_gpn_wintun_settings yükü —
/// alanlar isteğe bağlıdır (yalnızca gönderilenler değiştirilir); adapter adı
/// sanitleştirilir, kapasite sınırlanır. Saf: ağ/DI yok, test edilebilir.
/// </summary>
public sealed record GpnWintunSettingsPatch(
    string? AdapterName,
    uint? RingCapacity)
{
    /// <summary>Bu yamayı mevcut ayarlara uygular. Gönderilmeyen alanlar korunur.</summary>
    public GpnWintunItem Apply(GpnWintunItem? current)
    {
        current ??= new GpnWintunItem();
        return new GpnWintunItem
        {
            AdapterName = AdapterName is not null
                ? GpnWintunSettingsMapper.SanitizeAdapterName(AdapterName)
                : current.AdapterName,
            RingCapacity = RingCapacity is { } capacity
                ? GpnWintunSettingsMapper.NormalizeRingCapacity(capacity)
                : current.RingCapacity,
        };
    }
}

/// <summary>
/// Kullanıcının GpnWintunItem ayarlarını (ve türetilmiş wintun options'ını)
/// sağlar. AppManager singleton config'inden okur; DI'dan tek satırla çözülür.
/// </summary>
public interface IGpnWintunSettingsProvider
{
    /// <summary>Mevcut kullanıcı ayar bloğu (config'de yoksa varsayılanlar).</summary>
    GpnWintunItem Current { get; }

    /// <summary>Ayarlardan türetilmiş, WireGuardTunnelService.Open'a verilebilir options.</summary>
    GpnWintunOptions Options { get; }
}

public sealed class GpnWintunSettingsProvider : IGpnWintunSettingsProvider
{
    public GpnWintunItem Current => AppManager.Instance.Config?.GpnWintunItem ?? new GpnWintunItem();

    public GpnWintunOptions Options => GpnWintunSettingsMapper.ToOptions(Current);
}
