using ServiceLib.Models.Configs;

namespace ServiceLib.Services;

/// <summary>Bir açılış süpürme turunun sonucu (dashboard/log için sayaçlar + adlar).</summary>
public sealed record WintunOrphanSweepResult(
    int Removed,
    int Skipped,
    IReadOnlyList<string> RemovedNames,
    IReadOnlyList<string> SkippedNames,
    string? AbortReason)
{
    internal static WintunOrphanSweepResult Empty { get; } = new(0, 0, [], [], null);
}

/// <summary>
/// Başlatmada sahipsiz (orphan) Wintun adaptörlerini süpürür.
///
/// GPN oturumları (hem native WinDivert köprüsü hem mihomo TUN) Wintun
/// adaptörü yaratır ve normal kapanışta siler; süreç kaza/kill ile öldüğünde
/// adaptör geride kalabilir — örneğin önceki native oturumlardan kalan,
/// üzerine IP atanmamış (yalnızca 169.254.x.x APIPA) "AoGPN-&lt;sunucu&gt;"
/// adaptörleri. Kalıntılar bir sonraki açılışta aynı adla adaptör yaratmayı
/// engelleyebilir ya da "hayalet" ağ arayüzü olarak görünür.
///
/// Uygulama TEK ÖRNEK garantisiyle çalışır (App.OnStartup — EventWaitHandle);
/// açılışta, bu örneğin kendi oturumu daha kurulmadan koştuğu için ad ön ekimize
/// uyan HER adaptör önceki (ölü) süreçten kalmıştır ve güvenle silinebilir.
/// Silme, wintun.dll'nin resmi API'siyle yapılır: WintunOpenAdapter + 
/// WintunCloseAdapter — modern wintun'da (0.14+, SwDevice tabanlı) CloseAdapter,
/// handle'ın nasıl alındığından bağımsız olarak PnP cihaz örneğini kaldırır
/// (DIF_REMOVE; wintun api/adapter.c — WintunCloseAdapter → AdapterRemoveInstance).
///
/// Yeniden başlatma (rebootas) modunda çağrılmaz: eski örnek kendi adaptörünü
/// kapatırken yarışmamak gerekir. Yönetici değilken silme başarısız olur —
/// kayıt düşülür, oturum bozulmaz (best-effort).
/// </summary>
public static class WintunOrphanSweeper
{
    private const string Tag = "WintunSweep";

    /// <summary>Her zaman süpürülen miras ön ek (kullanıcı ön eki ne olursa olsun).</summary>
    private const string LegacyPrefix = "AoGPN";

    /// <summary>
    /// Üretim girişi: ayarlardan gelen ad ön eki (+ miras "AoGPN") ile arayüz
    /// numaralandırması + wintun silme gerçek yollarını kullanır. App.OnStartup
    /// bağlantıdan ÖNCE çağırır (rebootas hariç); best-effort'tur.
    /// </summary>
    public static Task<WintunOrphanSweepResult> SweepOrphanedAsync(
        GpnWintunItem? settings,
        CancellationToken cancellationToken = default)
        => SweepOrphanedCoreAsync(
            BuildOwnedPrefixes(settings?.AdapterName),
            enumerateNames: EnumerateInterfaceNames,
            removeByName: RemoveAdapterByNameAsync,
            cancellationToken);

    /// <summary>
    /// Uygulamanın sahibi olduğu (süpürülebilir) ad ön ekleri: kullanıcının
    /// yapılandırdığı Wintun ad ön eki (sanitleştirilmiş; boşsa "AoGPN") + miras
    /// "AoGPN" — kullanıcı ön eki değiştirse bile ESKİ kalıntılar da yakalanır.
    /// </summary>
    internal static IReadOnlyList<string> BuildOwnedPrefixes(string? configuredAdapterName)
    {
        var configured = GpnWintunSettingsMapper.SanitizeAdapterName(configuredAdapterName);
        var prefixes = new List<string>(2);
        foreach (var candidate in new[] { configured, LegacyPrefix })
        {
            if (!prefixes.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                prefixes.Add(candidate);
            }
        }
        return prefixes;
    }

    /// <summary>
    /// Ad bu uygulamanın adaptör kalıbına uyuyor mu: ön eke EŞİT (mihomo TUN
    /// cihazı — varsayılan "AoGPN") ya da ön ek + "-" ile başlayan (native köprü
    /// — "AoGPN-&lt;sunucu kimliği&gt;"). "AoGPNX" gibi komşu adlar eşleşmez.
    /// </summary>
    internal static bool IsOwnedAdapterName(string? name, IReadOnlyList<string> prefixes)
    {
        if (name.IsNullOrEmpty() || prefixes is null || prefixes.Count == 0)
        {
            return false;
        }
        foreach (var prefix in prefixes)
        {
            if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase)
                || name!.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Saf karar + uygulama çekirdeği (ağ/deletion dikişleri dışarıdan verilir):
    /// arayüz adlarını toplar, sahipli adları süzer ve her birini sildirir.
    /// Gerçek dikişler WintunNative'e/NetworkInterface'e gider; testler sahte
    /// delegeyle sürücüsüz doğrular. wintun.dll yoksa ilk silme denemesinde
    /// DllNotFoundException fırlar → tüm tur durur, sonuç AbortReason taşır.
    /// </summary>
    internal static async Task<WintunOrphanSweepResult> SweepOrphanedCoreAsync(
        IReadOnlyList<string> prefixes,
        Func<IReadOnlyList<string>> enumerateNames,
        Func<string, CancellationToken, Task<(bool Handled, string? Note)>> removeByName,
        CancellationToken cancellationToken)
    {
        if (prefixes.Count == 0)
        {
            return WintunOrphanSweepResult.Empty;
        }

        IReadOnlyList<string> names;
        try
        {
            names = enumerateNames();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Arayüz listesi okunamadı — süpürme atlandı: {ex.Message}");
            return WintunOrphanSweepResult.Empty;
        }

        var removed = new List<string>();
        var skipped = new List<string>();
        foreach (var name in names)
        {
            if (!IsOwnedAdapterName(name, prefixes))
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (handled, note) = await removeByName(name!, cancellationToken).ConfigureAwait(false);
                if (handled)
                {
                    removed.Add(name!);
                    DiagLog.Write($"{Tag} kaldırıldı: {name}{(note is null ? "" : $" ({note})")}");
                }
                else
                {
                    skipped.Add(name!);
                    DiagLog.Write($"{Tag} atlandı: {name} ({(note ?? "silinemedi")})");
                }
            }
            catch (DllNotFoundException ex)
            {
                // wintun.dll yok — sonraki adlar da aynı hatayı verir; turu durdur.
                Logging.SaveLog($"[{Tag}] wintun.dll bulunamadı — sahipsiz adaptör süpürmesi atlandı: {ex.Message}");
                return new WintunOrphanSweepResult(removed.Count, skipped.Count, removed, skipped, "wintun.dll yok");
            }
            catch (EntryPointNotFoundException ex)
            {
                Logging.SaveLog($"[{Tag}] wintun.dll sürümü eski (WintunOpenAdapter export'u yok) — süpürme atlandı: {ex.Message}");
                return new WintunOrphanSweepResult(removed.Count, skipped.Count, removed, skipped, "wintun.dll sürümü eski");
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"[{Tag}] '{name}' temizlenirken hata: {ex.Message}");
                skipped.Add(name!);
            }
        }

        if (removed.Count > 0 || skipped.Count > 0)
        {
            Logging.SaveLog($"[{Tag}] tamam: removed={removed.Count} skipped={skipped.Count} "
                + $"removed=[{string.Join(",", removed)}] skipped=[{string.Join(",", skipped)}]");
        }
        return new WintunOrphanSweepResult(removed.Count, skipped.Count, removed, skipped, null);
    }

    /// <summary>Mevcut ağ arayüzü adları (yalnızca Windows; hata → boş liste).</summary>
    private static IReadOnlyList<string> EnumerateInterfaceNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Select(ni => ni.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Arayüz numaralandırma hatası: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Tek bir adaptörü wintun API'siyle kaldırır: WintunOpenAdapter (ad tam
    /// eşleşmesi) + WintunCloseAdapter. Modern wintun'da CloseAdapter PnP cihaz
    /// örneğini kaldırır (handle'ın açılmış ya da yaratılmış olması fark etmez);
    /// başarı, yeniden açmanın ERROR_NOT_FOUND vermesiyle teyit edilir.
    /// </summary>
    private static Task<(bool Handled, string? Note)> RemoveAdapterByNameAsync(
        string name, CancellationToken cancellationToken)
    {
        var handle = WintunNative.WintunOpenAdapter(name);
        if (handle == IntPtr.Zero)
        {
            var err = WintunNative.LastWin32Error;
            // Arayüz listesinde göründü ama wintun artık bulamıyor → zaten gitmiş.
            return Task.FromResult(err == WintunNative.ErrorNotFound
                ? (true, "zaten yok")
                : (false, $"açılamadı (hata {err})"));
        }

        // CloseAdapter — kaldırma çağrısı (DIF_REMOVE). Hata wintun logger'ına
        // düşer; burada dönüş void olduğundan başarı yeniden açma ile doğrulanır.
        WintunNative.WintunCloseAdapter(handle);

        var verify = WintunNative.WintunOpenAdapter(name);
        if (verify == IntPtr.Zero)
        {
            var verifyErr = WintunNative.LastWin32Error;
            return Task.FromResult(verifyErr == WintunNative.ErrorNotFound
                ? (true, "kaldırıldı")
                : (false, $"doğrulama hatası {verifyErr}"));
        }
        // Hâlâ açılabiliyor — kaldırma başarısız (örn. yönetici değiliz). Açtığımız
        // tutamacı da kapat (tekrar kaldırmayı dener; zararsızdır) ve atlandı işaretle.
        WintunNative.WintunCloseAdapter(verify);
        return Task.FromResult<(bool, string?)>((false, "kaldırılamadı — hâlâ mevcut (yönetici gerekebilir)"));
    }
}
