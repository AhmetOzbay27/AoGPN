namespace ServiceLib.Services;

/// <summary>
/// Açılış ön yüklemesinin çekirdek ikilisi kontrolü (Faz 3).
///
/// Neden ServiceLib'de ve saf: kullanıcının kusuru BAĞLANMA ANINDA öğrenmesi en
/// kötü deneyimdir. Ama bu kontrolün kendisi de yanlış pozitif üretirse kullanıcı
/// boş yere uyarılır. Bu yüzden dosya sistemi erişimi dışarıdan verilir
/// (<c>resolveBinPath</c>) ve mantık birim testlerle birebir doğrulanır.
///
/// Yalnızca dosya varlığına bakar: ağ yönlendirme motoruna, WinDivert katmanına
/// ve Tier 4 optimizasyonlarına dokunmaz.
/// </summary>
public static class CoreBinaryPreflight
{
    /// <summary>Ön yüklemenin zorunlu gördüğü çekirdekler.</summary>
    public static readonly IReadOnlyList<ECoreType> RequiredCores = [ECoreType.mihomo, ECoreType.Xray];

    /// <summary>
    /// Zorunlu çekirdeklerden yürütülebilir dosyası BULUNMAYANLARIN adlarını döndürür
    /// (ör. "mihomo"). Zaten kurulu olanlar ve zorunlu listede olmayanlar sonuca girmez.
    /// </summary>
    /// <param name="coreInfos">Çekirdek tanımları (ör. CoreInfoManager.Instance.GetCoreInfo()).</param>
    /// <param name="resolveBinPath">(dosya adı, alt klasör) → tam yol.</param>
    /// <param name="required">Zorunlu çekirdekler; null ise mihomo + xray.</param>
    public static IReadOnlyList<string> FindMissingCoreBinaries(
        IEnumerable<CoreInfo>? coreInfos,
        Func<string, string, string> resolveBinPath,
        IEnumerable<ECoreType>? required = null)
    {
        ArgumentNullException.ThrowIfNull(resolveBinPath);

        var needed = new HashSet<ECoreType>(required ?? RequiredCores);
        var missing = new List<string>();

        foreach (var coreInfo in coreInfos ?? [])
        {
            if (!needed.Contains(coreInfo.CoreType) || coreInfo.CoreExes is not { Count: > 0 })
            {
                continue;
            }

            var found = coreInfo.CoreExes.Any(name =>
                File.Exists(resolveBinPath(Utils.GetExeName(name), coreInfo.CoreType.ToString())));

            if (!found)
            {
                missing.Add(coreInfo.CoreType.ToString());
            }
        }

        return missing;
    }
}
