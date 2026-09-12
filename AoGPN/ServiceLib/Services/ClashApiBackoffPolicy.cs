// ─────────────────────────────────────────────────────────────────────────
// ClashApiBackoffPolicy — mihomo dış denetleyicisi için devre kesici
//
// Denetleyici (127.0.0.1:{StatePort2}) çekirdek kapalı/başlarken bağlantıyı
// reddeder. Eskiden her okuma çağrısı 3 deneme × 2 istek + 2 sn bekleme
// yapıyordu: tek bir çağrı 6 başarısız bağlantı ve ~4 sn gecikme üretiyor,
// saniyelik dashboard anketleri ve GPN bağlanma yolu bunu katlıyordu.
// Bu politika art arda başarısızlıklarda deneme aralığını kademeli olarak
// açar ve ilk başarıda anında sıfırlar; böylece denetleyici gelir gelmez
// ilk deneme (en fazla bir geri çekilme aralığı sonra) başarılı olur.
//
// Saf ve deterministiktir: zaman dışarıdan verilir (bkz. testler), böylece
// gerçek beklemeler olmadan doğrulanabilir.
// ─────────────────────────────────────────────────────────────────────────

namespace ServiceLib.Services;

/// <summary>
/// Başarısız denetleyici çağrılarını kademeli geri çekilmeye çeviren devre
/// kesici. Örnek başına durum tutar; <see cref="ClashApiManager"/> tekil
/// örneğiyle paylaşılır.
/// </summary>
public sealed class ClashApiBackoffPolicy
{
    /// <summary>İlk geri çekilme: denetleyici henüz ayakta değilken hızlı toparlanma.</summary>
    public static readonly TimeSpan DefaultMinBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>Üst sınır: çekirdek gerçekten kapalıyken anketler tamamen durur.</summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _minBackoff;
    private readonly TimeSpan _maxBackoff;
    private TimeSpan _nextBackoff;

    /// <summary>Bu an'a kadar deneme yapılmaz (ticks). Volatile okunur — anketler çok iş parçacıklı.</summary>
    private long _retryNotBeforeTicks = long.MinValue;

    private int _consecutiveFailures;

    public ClashApiBackoffPolicy(TimeSpan? minBackoff = null, TimeSpan? maxBackoff = null)
    {
        _minBackoff = minBackoff is { Ticks: > 0 } min ? min : DefaultMinBackoff;
        _maxBackoff = maxBackoff is { } max && max > _minBackoff ? max : _minBackoff;
        _nextBackoff = _minBackoff;
    }

    /// <summary>Art arda kaç başarısızlık kaydedildi (başarıda sıfırlanır).</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>Geri çekilme penceresi açık mı (yani şu an denetleyiciye hiç gidilmemeli mi).</summary>
    public bool IsOpen => IsOpenAt(DateTime.UtcNow);

    /// <summary><see cref="IsOpen"/>'un test edilebilir hali.</summary>
    public bool IsOpenAt(DateTime utcNow)
        => utcNow.Ticks < Volatile.Read(ref _retryNotBeforeTicks);

    /// <summary>Deneme yapılabilir mi (kapı kapalıysa çağrı hiç kurulmaz).</summary>
    public bool ShouldAttemptAt(DateTime utcNow) => !IsOpenAt(utcNow);

    /// <summary>Bu anda kalan geri çekilme süresi (kapı açık değilse sıfır).</summary>
    public TimeSpan RemainingCooldownAt(DateTime utcNow)
    {
        var retryAt = new DateTime(Volatile.Read(ref _retryNotBeforeTicks), DateTimeKind.Utc);
        return retryAt > utcNow ? retryAt - utcNow : TimeSpan.Zero;
    }

    /// <summary>Başarısızlığı kaydeder ve sonraki denemeyi kademeli olarak erteler.</summary>
    public void RecordFailureAt(DateTime utcNow)
    {
        _consecutiveFailures++;
        Volatile.Write(ref _retryNotBeforeTicks, (utcNow + _nextBackoff).Ticks);
        _nextBackoff = TimeSpan.FromTicks(Math.Min(_maxBackoff.Ticks, _nextBackoff.Ticks * 2));
    }

    /// <summary>
    /// Başarıyı kaydeder: geri çekilme ve sayaç sıfırlanır — denetleyici bir kez
    /// yanıt verdiğinde sonraki her çağrı anında yeniden denenir.
    /// </summary>
    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _nextBackoff = _minBackoff;
        Volatile.Write(ref _retryNotBeforeTicks, long.MinValue);
    }
}
