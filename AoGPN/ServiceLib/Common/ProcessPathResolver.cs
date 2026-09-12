namespace ServiceLib.Common;

/// <summary>
/// Süreç exe yolunu, gereken EN AZ yetkiyle çözer.
///
/// <para><b>Sorun.</b> <c>Process.MainModule</c> (ve <c>Process.Modules</c>)
/// <c>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ</c> ister ve tam modül
/// numaralandırması yapar. Yükseltilmemiş bir süreçten korumalı süreçlere
/// yapılan her çağrı bir <see cref="System.ComponentModel.Win32Exception"/>
/// fırlatır; Visual Studio bunları <b>yakalansa bile</b> "ilk şans istisnası"
/// olarak yazar. Günlükte bu iki ileti bu yüzden tik başına yüzlerce satır
/// üretiyordu:
/// <list type="bullet">
///   <item><c>"Erişim engellendi."</c> — yükseltilmiş/SYSTEM süreçleri;</item>
///   <item><c>"Unable to enumerate the process modules."</c> — pid 0 gibi
///   gerçek modülü olmayan sahte süreçler.</item>
/// </list></para>
///
/// <para><b>Alternatif.</b> <c>PROCESS_QUERY_LIMITED_INFORMATION</c> +
/// <c>QueryFullProcessImageName</c>: tek ve ucuz bir çekirdek çağrısı, daha az
/// hak ister ve ölçüldüğü üzere <b>daha çok</b> süreci çözer. Canlı 447 süreçli
/// bir makinede (yükseltilmemiş): MainModule 219, limited sorgu 259 — MainModule'ün
/// reddettiği <b>40</b> süreci kurtarır (devenv, audiodg, anti-cheat korumalı
/// oyunlar, AnyDesk, ASUS servisleri…).</para>
///
/// <para><b>Bu yüzden sıra tersine çevrilir:</b> önce limited sorgu, yalnızca
/// gerekirse MainModule. Ölçüm (aynı 447 süreç): eski sıra <b>223</b> istisna
/// atışı / 240 çözüm; yeni sıra <b>0</b> istisna atışı / 240 çözüm — aynı kapsam,
/// sıfır gürültü.</para>
///
/// <para><b>Neden "erişim engellendi"de MainModule hiç denenmez.</b> Erişim
/// denetimi istenen haklar kümesinde monotondur: limited hakları reddeden bir
/// süreç, onun üst kümesini (VM_READ dahil) de reddeder. Denemek yalnızca
/// maliyetli bir tam modül numaralandırma + istisna atışı üretir, hiçbir yol
/// kazandırmaz. Ölçüm bunu doğruluyor: kısa devre hiçbir süreci kaybettirmedi
/// (240 = 240).</para>
/// </summary>
public static class ProcessPathResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorAccessDenied = 5;

    /// <summary>Limited sorgunun sonucu: yol ve (başarısızsa) son Win32 hata kodu.</summary>
    internal readonly record struct LimitedQueryOutcome(string? Path, int LastError);

    /// <summary>Erişim reddi hata kodu (ERROR_ACCESS_DENIED) — kısa devre ölçütü.</summary>
    internal const int AccessDeniedError = ErrorAccessDenied;

    /// <summary>
    /// Sürecin exe yolu; çözülemezse null. <b>Hiçbir koşulda fırlatmaz</b> ve
    /// erişimin reddedildiği bilinen durumda pahalı yolu hiç denemez.
    /// </summary>
    public static string? Resolve(int pid) => Resolve(pid, null, null);

    /// <summary>
    /// Testlerin gerçek Win32 çağrısı yapmadan sıra mantığını sınaması için
    /// enjekte edilebilir iç aşırı yükleme. Statik durum YOK — paralel testler
    /// birbirinin dikişini görmez.
    /// </summary>
    internal static string? Resolve(
        int pid,
        Func<int, LimitedQueryOutcome>? limitedQuery,
        Func<int, string?>? mainModule)
    {
        if (pid <= 0)
        {
            // pid 0 = "System Idle Process" — gerçek bir görüntü dosyası yok;
            // modül numaralandırması "Unable to enumerate the process modules."
            // fırlatır. Hiç dokunmuyoruz.
            return null;
        }

        var limited = (limitedQuery ?? QueryLimitedPath)(pid);
        if (IsUsableExePath(limited.Path))
        {
            return SafeFullPath(limited.Path!);
        }

        if (limited.LastError == ErrorAccessDenied)
        {
            // Limited hakları reddedildi → MainModule de reddedilir (monotonluk).
            // Denemek yalnızca ilk şans istisnası üretir.
            return null;
        }

        var mainModulePath = (mainModule ?? ReadMainModulePath)(pid);
        return IsUsableExePath(mainModulePath) ? SafeFullPath(mainModulePath!) : null;
    }

    /// <summary>
    /// Yalnızca yükseltme gerektirmeyen yolu kullanır (MainModule'e asla düşmez).
    /// Tanılama/test için: "limitli sorgu tek başına neyi çözebiliyor" sorusunu
    /// MainModule gürültüsü karışmadan yanıtlar.
    /// </summary>
    internal static string? ResolveLimitedOnly(int pid)
        => ResolveLimitedOnly(pid, null);

    internal static string? ResolveLimitedOnly(int pid, Func<int, LimitedQueryOutcome>? limitedQuery)
    {
        if (pid <= 0)
        {
            return null;
        }

        var outcome = (limitedQuery ?? QueryLimitedPath)(pid);
        return IsUsableExePath(outcome.Path) ? SafeFullPath(outcome.Path!) : null;
    }

    /// <summary>
    /// Yol kullanılabilir mi: gerçekten var olan bir <c>.exe</c> olmalı.
    /// Dosya adı boş ya da dosya silinmişse (süreç güncellenmiş) yedek yola düşülür.
    /// </summary>
    internal static bool IsUsableExePath(string? path)
        => path is not null
            && !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path);

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION ile görüntü yolunu okur.</summary>
    private static LimitedQueryOutcome QueryLimitedPath(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Windows dışında (repo macOS yollarını da barındırıyor) bu yol yok;
            // hata kodunu "erişim engellendi" YAPMIYORUZ ki MainModule yedeği
            // çalışsın.
            return new LimitedQueryOutcome(null, 0);
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return new LimitedQueryOutcome(null, Marshal.GetLastWin32Error());
        }

        try
        {
            var capacity = 32 * 1024u;
            var buffer = new StringBuilder((int)capacity);
            if (!QueryFullProcessImageName(handle, 0, buffer, ref capacity))
            {
                return new LimitedQueryOutcome(null, Marshal.GetLastWin32Error());
            }

            return new LimitedQueryOutcome(buffer.ToString(), 0);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Yedek yol: <c>Process.MainModule</c>. Yalnızca limited sorgu erişim
    /// reddinden BAŞKA bir sebeple başarısız olduğunda çağrılır. Fırlatabilecek
    /// istisnalar burada yutulur (çağıranlar zaten "yol yok" bekliyor).
    /// </summary>
    private static string? ReadMainModulePath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (ArgumentException)
        {
            // Böyle bir pid yok.
            return null;
        }
        catch (InvalidOperationException)
        {
            // Süreç arada çıktı.
            return null;
        }
        catch (Win32Exception)
        {
            // Erişim engelli / modüller numaralandırılamadı.
            return null;
        }
        catch (NotSupportedException)
        {
            // Başka mimari veya uzak süreç.
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
