namespace ServiceLib.Models;

/// <summary>
/// Kesintisiz (make-before-break) düğüm geçişi sonrası eski düğümün boşalma
/// (drain) ilerlemesi.
///
/// Geçişte çekirdek durmaz: mevcut bağlantılar eski düğümün outbound'unda
/// (wg-&lt;eskiId&gt;) doğal olarak bitene kadar sürer, yeniler anında yeni
/// düğümden kurulur. Bu anlık görüntü, mihomo <c>GET /connections</c> zincir
/// (chains) alanlarından eski wg-&lt;id&gt;'ye bağlı kalan oturum sayısını taşır;
/// dashboard durum satırı "eski düğüm boşalıyor" bilgisini bundan çizer.
/// </summary>
public sealed record GpnDrainSnapshot(
    /// <summary>True = boşalma sürüyor; false = tamamlandı / zaman aşımıyla sonlandı.</summary>
    bool IsDraining,
    /// <summary>Eski düğüme bağlı kalan bağlantı sayısı; -1 = henüz ölçülemedi.</summary>
    int RemainingConnections,
    string? FromServerId = null,
    string? FromServerName = null,
    string? ToServerId = null,
    string? ToServerName = null,
    /// <summary>True = süre doldu ve eski düğümde bağlantı kaldı (kalan doğal olarak bitecek).</summary>
    bool TimedOut = false)
{
    /// <summary>Anlık görüntünün alındığı zaman.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
