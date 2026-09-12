using System.Diagnostics;

namespace ServiceLib.Services;

/// <summary>
/// Yan güncelleyiciyi (<c>AoGPN.Updater</c>) bulup başlatan TEK yer.
///
/// Neden tek yer: uygulamayı yerinde değiştirebilen tek bileşen yan güncelleyicidir.
/// Onu çalıştıran bütün çağrı yolları (açılışta bulunan yeni sürüm, ileride
/// eklenecek tetikleyiciler) aynı yol çözümlemesini ve aynı argüman sözleşmesini
/// kullanmalıdır — aksi halde bir yol güncellenir, diğeri sessizce başarısız olur.
///
/// Argüman biçimi tek doğruluk kaynağında tutulur
/// (<see cref="AppUpdateUpdaterContract"/>); burası yalnızca yolu bulur, süreç
/// başlatır ve hatayı çağırana döndürür.
///
/// Ağ yönlendirme motoruna, WinDivert katmanına ve Tier 4 optimizasyonlarına
/// dokunmaz: yalnızca bir dosya araması ve bir süreç başlatmasıdır.
/// </summary>
public static class AppUpdaterLauncher
{
    /// <summary>Yan güncelleyicinin dosya adı (uzantısız; <see cref="Utils.GetExeName"/> ekler).</summary>
    public const string UpdaterExeName = AppUpdateUpdaterContract.UpdaterExeName;

    /// <summary>Güncelleyicinin ana süreci beklemesi için önerilen üst sınır (saniye).</summary>
    public const int DefaultWaitSeconds = AppUpdateUpdaterContract.DefaultWaitSeconds;

    /// <summary>
    /// Güncellemenin uygulanacağı klasör (çalışan AoGPN.exe'nin klasörü).
    /// </summary>
    public static string ResolveTargetDirectory()
    {
        var exePath = Utils.GetExePath();
        var directory = Path.GetFileName(exePath).IsNullOrEmpty() ? null : Path.GetDirectoryName(exePath);
        return (directory.IsNullOrEmpty() ? Utils.GetBaseDirectory() : directory).TrimEnd('\\', '/');
    }

    /// <summary>
    /// Kurulum klasöründeki yan güncelleyicinin yolu; yoksa null. Yokluğu bir hata
    /// değildir: uygulama güncelleme olmadan normal çalışır.
    /// </summary>
    public static string? ResolveUpdaterPath()
    {
        var path = ResolveUpdaterAbsolutePath();
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Güncelleyicinin yolu (var olsun olmasın, tam yol) ve var mı bilgisi.
    /// </summary>
    public static bool UpdaterExists(out string updaterPath)
    {
        updaterPath = ResolveUpdaterAbsolutePath();
        return File.Exists(updaterPath);
    }

    private static string ResolveUpdaterAbsolutePath()
        => Path.Combine(Utils.GetBaseDirectory(), Utils.GetExeName(UpdaterExeName));

    /// <summary>
    /// İndirilen paketi güncelleyiciye devreder. Başarılıysa çağıran, ana uygulamayı
    /// DERHAL kapatmalıdır: güncelleyici <paramref name="waitProcessId"/> süreci
    /// bitene kadar bekler, sonra dosyaları değiştirip uygulamayı yeniden başlatır.
    /// </summary>
    /// <returns>Başlatıldıysa true; hata mesajı <paramref name="error"/> içinde döner.</returns>
    public static bool TryStart(string zipPath, int waitProcessId, out string? error)
        => TryStart(
            zipPath,
            waitProcessId,
            ResolveUpdaterPath(),
            ResolveTargetDirectory(),
            Utils.GetExePath(),
            StartProcess,
            out error);

    /// <summary>
    /// Testlerin süreç başlatmadan doğrulayabilmesi için yol/süreç başlatıcı
    /// dışarıdan verilebilir (bkz. AppUpdaterLauncherTests).
    /// </summary>
    internal static bool TryStart(
        string? zipPath,
        int waitProcessId,
        string? updaterPath,
        string targetDirectory,
        string restartExePath,
        Func<ProcessStartInfo, bool> startProcess,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(startProcess);

        error = null;

        if (updaterPath.IsNullOrEmpty() || !File.Exists(updaterPath))
        {
            error = $"{Utils.GetExeName(UpdaterExeName)} kurulum klasöründe bulunamadı; güncelleme uygulanamıyor.";
            return false;
        }

        if (zipPath.IsNullOrEmpty() || !File.Exists(zipPath))
        {
            error = $"İndirilen güncelleme paketi bulunamadı: {zipPath}";
            return false;
        }

        var arguments = AppUpdateUpdaterContract.BuildArguments(
            zipPath, targetDirectory, restartExePath, waitProcessId, DefaultWaitSeconds);

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = arguments,
            WorkingDirectory = targetDirectory,
            // Kabuk kullanılmaz: yeni süreç bu sürecin yetkisini/belirtecini devralır
            // (yönetici çalışan bir kurulum yönetici kalır) ve argümanlar olduğu gibi
            // geçer. ProcUtils.ProcessStart ise tüm argüman dizesini toptan tırnaklayıp
            // sözleşmeyi tek bir argümana çevirirdi.
            UseShellExecute = false,
        };

        try
        {
            if (!startProcess(startInfo))
            {
                error = "Güncelleyici başlatılamadı.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[AppUpdaterLauncher] Güncelleyici başlatma hatası", ex);
            error = ex.Message;
            return false;
        }
    }

    private static bool StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }
}
