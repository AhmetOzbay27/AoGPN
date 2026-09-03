using ServiceLib.Manager;
using ServiceLib.Models.Configs;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnCaptureSettings — kullanıcı ayarlarından WinDivertOpenParams üretimi
//
// GpnCaptureItem (Config) → WinDivertOpenParams + WINDIVERT_FLAG_* bitleri:
//   * QueueLen/Time/Size değerleri, seçilen (Layer, Direction) slotuna yazılır
//     (slot = layer * 2 + direction) ve CurrentLayer/CurrentDirection = 1 ile
//     yalnızca o slotun geçerli olması sağlanır (diğer katman/yönler klasik
//     davranışta kalır).
//   * Enable* bayrakları, karşılık gelen WINDIVERT_FLAG_QUEUE_LENGTH/TIME/SIZE
//     bitlerini açar — WinDivert bu bit yoksa ilgili parametreyi uygulamaz.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// GpnCaptureItem ayar bloğunu WinDivertOpenParams + açılış bayraklarına ve
/// doğrudan <see cref="GpnCaptureOptions"/>'a çeviren saf eşleyici (pure).
/// </summary>
public static class GpnCaptureSettingsMapper
{
    /// <summary>Katman/yön değerlerini geçerli WinDivert slot aralığına sabitler.</summary>
    public static int NormalizeSlot(int layer, int direction)
    {
        var l = Math.Clamp(layer, 0, 1);
        var d = Math.Clamp(direction, 0, 1);
        return l * 2 + d;
    }

    /// <summary>
    /// Ayarlardan WinDivertOpenParams üretir. Kuyruk değerleri seçilen slotta
    /// yazılır; devre dışı (Enable* = false) alanlar 0 kalır ve ilgili flag
    /// açılmaz → WinDivert o parametreyi uygulamaz.
    /// </summary>
    public static WinDivertOpenParams ToOpenParams(GpnCaptureItem settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ps = WinDivertOpenParams.Default;
        ps.CurrentLayer = 1;   // kuyruk alanları yalnızca current layer için
        ps.CurrentDirection = 1; // ve yalnızca current direction için geçerli
        var slot = NormalizeSlot(settings.Layer, settings.Direction);
        ps.SetQueueLen(slot, settings.EnableQueueLen ? settings.QueueLen : 0);
        ps.SetQueueTime(slot, settings.EnableQueueTime ? settings.QueueTime : 0);
        ps.SetQueueSize(slot, settings.EnableQueueSize ? settings.QueueSize : 0);
        return ps;
    }

    /// <summary>Etkin kuyruk alanları için WINDIVERT_FLAG_QUEUE_* bitlerini üretir.</summary>
    public static ulong ToOpenFlags(GpnCaptureItem settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ulong flags = 0;
        if (settings.EnableQueueLen)
        {
            flags |= WinDivertNative.FlagQueueLength;
        }
        if (settings.EnableQueueTime)
        {
            flags |= WinDivertNative.FlagQueueTime;
        }
        if (settings.EnableQueueSize)
        {
            flags |= WinDivertNative.FlagQueueSize;
        }
        return flags;
    }

    /// <summary>GpnCaptureLoop'a verilecek options'ı ayarlardan üretir (OpenParams + ExtraFlags).</summary>
    public static GpnCaptureOptions ToCaptureOptions(GpnCaptureItem settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GpnCaptureOptions
        {
            OpenParams = ToOpenParams(settings),
            ExtraFlags = ToOpenFlags(settings),
        };
    }
}

/// <summary>
/// Dashboard "WinDivert kuyruk" kartından gelen set_gpn_capture_settings yükü —
/// değerler isteğe bağlıdır (yalnızca gönderilen alanlar değiştirilir) ve geçerli
/// aralıklara sınırlanır. Saf: ağ/DI yok, test edilebilir.
/// </summary>
public sealed record GpnCaptureSettingsPatch(
    uint? QueueLen,
    uint? QueueTime,
    uint? QueueSize,
    bool? EnableQueueLen,
    bool? EnableQueueTime,
    bool? EnableQueueSize,
    int? Layer,
    int? Direction)
{
    /// <summary>
    /// Bu yamayı mevcut ayarlara uygular. Gönderilmeyen alanlar korunur; sayısal
    /// değerler WinDivert anlamlı aralıklarına sınırlanır (QueueLen 1..1M,
    /// QueueTime 1..600k ms, QueueSize 0..64M bayt, katman/yön 0..1).
    /// </summary>
    public GpnCaptureItem Apply(GpnCaptureItem? current)
    {
        current ??= new GpnCaptureItem();
        return new GpnCaptureItem
        {
            EnableQueueLen = EnableQueueLen ?? current.EnableQueueLen,
            EnableQueueTime = EnableQueueTime ?? current.EnableQueueTime,
            EnableQueueSize = EnableQueueSize ?? current.EnableQueueSize,
            QueueLen = QueueLen is { } queueLen ? Math.Clamp(queueLen, 1u, 1_000_000u) : current.QueueLen,
            QueueTime = QueueTime is { } queueTime ? Math.Clamp(queueTime, 1u, 600_000u) : current.QueueTime,
            QueueSize = QueueSize is { } queueSize ? Math.Clamp(queueSize, 0u, 64_000_000u) : current.QueueSize,
            Layer = Layer is { } layer ? Math.Clamp(layer, 0, 1) : current.Layer,
            Direction = Direction is { } direction ? Math.Clamp(direction, 0, 1) : current.Direction,
            Priority = current.Priority,
        };
    }
}

/// <summary>
/// Kullanıcının GpnCaptureItem ayarlarını (ve türetilmiş capture options'ını)
/// sağlar. AppManager singleton config'inden okur; DI'dan tek satırla çözülür.
/// </summary>
public interface IGpnCaptureSettingsProvider
{
    /// <summary>Mevcut kullanıcı ayar bloğu (config'de yoksa varsayılanlar).</summary>
    GpnCaptureItem Current { get; }

    /// <summary>Ayarlardan türetilmiş, GpnCaptureLoop'a verilebilir options.</summary>
    GpnCaptureOptions CaptureOptions { get; }
}

public sealed class GpnCaptureSettingsProvider : IGpnCaptureSettingsProvider
{
    public GpnCaptureItem Current => AppManager.Instance.Config?.GpnCaptureItem ?? new GpnCaptureItem();

    public GpnCaptureOptions CaptureOptions => GpnCaptureSettingsMapper.ToCaptureOptions(Current);
}