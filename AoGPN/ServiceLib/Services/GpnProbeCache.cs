namespace ServiceLib.Services;

/// <summary>
/// Ölçüm sonuçları için KISA ÖMÜRLÜ önbellek (Faz 2).
///
/// Neden: bağlanma yolu her seferinde tüm adayları sıfırdan ölçüyordu. Aynı
/// sunucuya dakikalar içinde yeniden bağlanıldığında (kullanıcı "kes → bağlan",
/// mod değişimi, Reload) aynı ICMP örnekleri ve aynı WireGuard el sıkışma
/// probe'ları tekrar tekrar yapılıyordu — ölçülen ~10 saniyenin büyük kısmı bu.
///
/// Tasarım kararları:
///   * Varsayılan olarak KAPALIDIR: kullanım <see cref="GpnProbeOptions.UseCache"/>
///     ile açıkça istenir. Dashboard'ın ⚡ Test düğmesi ve failover izleyicisi her
///     zaman TAZE ölçüm ister (ölü sunucuyu önbellekten diri göstermek tehlikeli
///     olurdu) — bu yolar bayrağı hiç açmaz.
///   * Anahtar, ölçümün anlamını değiştiren her şeyi taşır (ölçüm modu, örnek
///     sayısı, tünel-aktif bayrağı). Özellikle <c>tunnelActive</c> KRİTİKTİR:
///     tünelsiz ölçüm ICMP kullanır, tünel etkinken fiziksel NIC üzerinden UDP
///     el sıkışma kullanılır — ikisi aynı sayıyı vermez.
///   * Zaman kaynağı monotondur ve dışarıdan verilebilir (testler beklemesin).
///   * Kapasite sınırlıdır: en eski kayıt düşürülür (süreç ömrü boyunca büyümez).
/// </summary>
public sealed class GpnProbeCache
{
    private readonly TimeSpan _ttl;
    private readonly Func<long> _now;
    private readonly int _capacity;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private readonly record struct Entry(object Value, long ExpiresAtMs);

    /// <summary>Ölçüm sonuçlarının kabul edilen en fazla yaşı (varsayılan 60 sn).</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    public GpnProbeCache(TimeSpan? ttl = null, Func<long>? monotonicMs = null, int capacity = 128)
    {
        _ttl = ttl ?? DefaultTtl;
        _now = monotonicMs ?? (() => Environment.TickCount64);
        _capacity = Math.Max(8, capacity);
    }

    /// <summary>Önbellekteki geçerli (süresi dolmamış) kayıt sayısı.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired();
                return _entries.Count;
            }
        }
    }

    public TimeSpan Ttl => _ttl;

    public bool TryGet<T>(string key, out T? value)
    {
        value = default;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresAtMs <= _now())
            {
                _entries.Remove(key);
                return false;
            }

            if (entry.Value is T typed)
            {
                value = typed;
                return true;
            }

            return false;
        }
    }

    public void Set<T>(string key, T value)
    {
        if (key.IsNullOrEmpty() || value is null)
        {
            return;
        }

        lock (_gate)
        {
            PurgeExpired();
            if (_entries.Count >= _capacity)
            {
                // Kapasite doldu: en yakın süresi dolacak kaydı düşür (LRU yerine
                // ucuz ve tahmin edilebilir — önbellek zaten kısa ömürlüdür).
                var oldest = _entries.MinBy(kv => kv.Value.ExpiresAtMs);
                _entries.Remove(oldest.Key);
            }

            _entries[key] = new Entry(value, _now() + (long)_ttl.TotalMilliseconds);
        }
    }

    /// <summary>Belirli bir sunucuya ait tüm kayıtları düşürür.</summary>
    public void InvalidateServer(string serverId)
    {
        if (serverId.IsNullOrEmpty())
        {
            return;
        }

        var suffix = $"|{serverId}|";
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => k.Contains(suffix, StringComparison.Ordinal)).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void PurgeExpired()
    {
        var now = _now();
        foreach (var key in _entries.Where(kv => kv.Value.ExpiresAtMs <= now).Select(kv => kv.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    /// <summary>
    /// Ölçüm anahtarı. Ölçümün sonucunu etkileyen her şey anahtara girer; böylece
    /// "aynı soru" gerçekten aynı sorudur.
    /// </summary>
    public static string BuildKey(string kind, string serverId, GpnProbeOptions options, bool tunnelActive)
        => $"{kind}|{serverId}|{options.Mode}|s{options.Samples}|t{options.PerSampleTimeoutMs}"
            + $"|h{options.HandshakeProbe.WaitTimeoutMs}x{options.HandshakeProbe.MaxAttempts}"
            + $"|tun{(tunnelActive ? 1 : 0)}";
}
