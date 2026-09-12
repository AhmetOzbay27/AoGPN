using System.Diagnostics;

namespace ServiceLib.Services;

/// <summary>
/// GPN bağlanma yolunun AŞAMA ÖLÇÜMÜ (Faz 0 — görünürlük).
///
/// Neden: canlı ölçümlerde "Bağlan"dan tünele ~10 saniye geçiyordu ama bu sürenin
/// ne kadarı sunucu ölçümüne (ICMP/UDP), ne kadarı çekirdek başlatmaya, ne kadarı
/// el sıkışmaya gittiği BİLİNMİYORDU. Optimizasyonun nereye yapılacağı tahmine
/// kalıyordu. Bu sınıf her aşamayı kaydedip tek bir <c>GPN_TIMING</c> satırı üretir.
///
/// Tasarım:
///   * Zaman kaynağı dışarıdan verilebilir (<c>monotonicMs</c>) — testler gerçek
///     bekleme yapmadan aşamaları doğrular.
///   * İşaretler İŞ PARÇACIGI GÜVENLİDİR: ICMP ve UDP ölçüm fazları paralel koşar
///     ve ikisi de kendi bitiş anını işaretler.
///   * Değerler "başlangıçtan bu yana geçen ms"tir (delta değil): paralel fazlarda
///     da okunabilir tek biçim budur — "ICMP 1.2 sn'de bitti, UDP 4.6 sn'de bitti,
///     çekirdek 9.1 sn'de hazır oldu" gibi.
///   * <see cref="Summarize"/> çıktısı diyagnoz panosuna ve günlüğe yazılır.
///
/// Örnek çıktı:
/// <code>
///   GPN_TIMING total=9649ms connect-start=0ms teardown=132ms icmp=1250ms udp=4680ms
///              select-done=4702ms launch=9180ms connected=9649ms
/// </code>
/// </summary>
public sealed class GpnConnectTimeline
{
    /// <summary>İşaret sayısı üst sınırı — beklenmedik bir döngü satırı şişirmesin.</summary>
    private const int MaxMarks = 48;

    private readonly Func<long> _now;
    private readonly Stopwatch? _stopwatch;
    private readonly List<StageMark> _marks = new(MaxMarks);
    private readonly Lock _gate = new();

    private readonly record struct StageMark(string Name, long ElapsedMs);

    public GpnConnectTimeline(Func<long>? monotonicMs = null)
    {
        if (monotonicMs is null)
        {
            _stopwatch = Stopwatch.StartNew();
            _now = () => _stopwatch.ElapsedMilliseconds;
        }
        else
        {
            _now = monotonicMs;
        }
    }

    /// <summary>Başlangıçtan bu yana geçen süre (ms).</summary>
    public long ElapsedMs => _now();

    /// <summary>İşaretlenen aşama adları (kayıt sırasıyla).</summary>
    public IReadOnlyList<string> StageNames
    {
        get
        {
            lock (_gate)
            {
                return _marks.Select(m => m.Name).ToArray();
            }
        }
    }

    /// <summary>
    /// Bir aşamanın tamamlandığını kaydeder. Aynı ad birden fazla kez
    /// işaretlenirse sonuncusu geçerlidir (ör. yeniden denemeler).
    /// </summary>
    public void Mark(string name)
    {
        if (name.IsNullOrEmpty())
        {
            return;
        }

        var elapsed = _now();
        lock (_gate)
        {
            // Aynı aşama yeniden işaretlenirse (ör. yeniden deneme) SONUNCUSU
            // geçerlidir — diyagnoz satırında aynı ad birden çok kez görünmez.
            var existing = _marks.FindIndex(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (existing >= 0)
            {
                _marks[existing] = new StageMark(name, elapsed);
                return;
            }

            if (_marks.Count >= MaxMarks)
            {
                return;
            }

            _marks.Add(new StageMark(name, elapsed));
        }
    }

    /// <summary>
    /// Diyagnoz satırı: <c>GPN_TIMING total=…ms &lt;aşama&gt;=…ms …</c>.
    /// Aşamalar geçen süreye göre sıralanır (paralel fazlarda da okunabilir).
    /// </summary>
    public string Summarize()
    {
        StageMark[] snapshot;
        lock (_gate)
        {
            snapshot = _marks.ToArray();
        }

        var total = _now();
        if (snapshot.Length == 0)
        {
            return $"GPN_TIMING total={total}ms";
        }

        // OrderBy kararlı (stable) sıralamadır: eşit süreli aşamalar kayıt sırasını korur.
        var stages = string.Join(' ', snapshot
            .OrderBy(m => m.ElapsedMs)
            .Select(m => $"{m.Name}={m.ElapsedMs}ms"));

        return $"GPN_TIMING total={total}ms {stages}";
    }

    /// <summary>Özeti diyagnoz akışına yazar.</summary>
    public void WriteToDiag() => DiagLog.Write(Summarize());
}
