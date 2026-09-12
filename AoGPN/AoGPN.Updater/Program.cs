using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace AoGPN.Updater;

/// <summary>
/// AoGPN yan güncelleyicisi (sidecar updater).
///
/// Görev tanımı — yalnızca bu üç iş:
///   1. Ana uygulamanın (AoGPN.exe) kapanmasını bekle,
///   2. Argümanla verilen yeni sürüm .zip dosyasını kurulum klasörüne,
///      ESKİ DOSYALARIN ÜZERİNE YAZARAK aç,
///   3. AoGPN.exe'yi yeniden başlat ve kendini kapat.
///
/// Tasarım kararları (neden böyle):
///
///  * **Kendini geçici klasöre taşıma (self-relocation).** Güncelleme paketi
///    içinde güncelleyicinin KENDİ exe'si de bulunur. Kurulum klasöründen
///    çalışırken bu dosya kilitli olurdu ve üzerine yazılamazdı. Bu yüzden
///    kurulum klasöründen başlatıldığında önce <c>%TEMP%</c> altına kopyalanıp
///    oradan yeniden başlatılır; böylece kurulum klasöründeki hiçbir dosya
///    kilitli kalmaz.
///
///  * **Üst düzey klasörü soyma.** Release zip'leri tek bir kök klasör
///    (ör. <c>AoGPN-windows-64/…</c>) içinde paketlenir. Zip düz (kök klasörsüz)
///    ise hiçbir şey soyulmaz — iki düzen de doğru açılır.
///
///  * **bin/ korunur.** Çekirdek ikilileri (mihomo/xray/wintun/WinDivert) hem
///    büyük hem de yerelde zaten indirilmiş olabilir; bu yüzden zip'te bulunan
///    ve hedefte hâlihazırda var olan bin/ dosyaları ATLANIR (yeniden indirme yok).
///
///  * **Kilit dayanıklılığı.** Dosyalar anti-virüs/indeksleyici tarafından kısa
///    süre kilitlenebilir; her dosya artan beklemeyle yeniden denenir.
///
///  * Bu program yönetici hakları İSTEMEZ ve kabuk üzerinden başlatmaz
///    (UseShellExecute=false): yeniden başlatılan AoGPN, kendisini başlatan
///    sürecin belirteciyle (token) aynı yetkide açılır — TUN/WinDivert yolu
///    yönetici gerektiriyorsa aynı yetkiyle devam eder.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Türkçe günlük satırları konsolda bozuk görünmesin (kod sayfası).
        // Başarısız olursa yalnızca görünüm etkilenir — akış devam eder.
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
        }

        var options = UpdaterOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(UpdaterOptions.Usage);
            return 2;
        }

        // Konsol çıktısı kaybolmasın: hem stdout'a hem geçici bir günlüğe yaz.
        var log = new UpdaterLog();

        try
        {
            log.Info($"AoGPN.Updater başladı. zip={options.ZipPath} target={options.TargetDirectory} "
                + $"restart={options.RestartExePath ?? "(yok)"} waitPid={options.WaitProcessId?.ToString() ?? "(yok)"}");

            // ── Adım 0: kurulum klasöründe çalışıyorsak geçici klasöre taşın ──
            if (!options.Relocated && SelfRelocator.IsInside(Environment.ProcessPath, options.TargetDirectory))
            {
                var exitCode = SelfRelocator.RelaunchFromTemp(options, log);
                // Başarılıysa geçici kopya devraldı; bu süreç çekilir.
                return exitCode;
            }

            // ── Adım 1: yeni sürüm paketi gerçekten var mı? ──
            if (!File.Exists(options.ZipPath))
            {
                log.Error($"Güncelleme paketi bulunamadı: {options.ZipPath}");
                return 3;
            }

            // ── Adım 2: ana uygulamanın kapanmasını bekle ──
            WaitForApplicationExit(options, log);

            // ── Adım 3: üzerine yazarak aç ──
            var extractor = new ZipInstaller(log);
            var written = extractor.Extract(options.ZipPath, options.TargetDirectory);
            log.Info($"Arşiv açıldı: {written} dosya yazıldı.");

            // ── Adım 4: uygulamanın yeniden başlatılabilir olduğunu doğrula ──
            var restartPath = options.RestartExePath ?? Path.Combine(options.TargetDirectory, "AoGPN.exe");
            if (!File.Exists(restartPath))
            {
                log.Error($"Güncelleme uygulandı ancak yeniden başlatılacak dosya yok: {restartPath} "
                    + "— uygulamayı elle başlatın.");
                return 4;
            }

            // ── Adım 5: indirilen paketi temizle ──
            TryDelete(options.ZipPath, log);

            // ── Adım 6: uygulamayı yeniden başlat ──
            if (options.Restart)
            {
                var workingDirectory = Path.GetDirectoryName(restartPath) ?? options.TargetDirectory;
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = restartPath,
                    WorkingDirectory = workingDirectory,
                    // Kabuk kullanılmaz: yeni süreç bu sürecin yetkisini/belirtecini
                    // devralır (yönetici olarak çalışan bir kurulum yönetici kalır).
                    UseShellExecute = false,
                });

                if (process is null)
                {
                    log.Error("AoGPN yeniden başlatılamadı (Process.Start null döndü).");
                    return 5;
                }

                log.Info($"AoGPN yeniden başlatıldı (pid={process.Id}).");
            }
            else
            {
                log.Info("--no-restart verildi: uygulama başlatılmadı.");
            }

            log.Info("Güncelleme tamamlandı.");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Güncelleme başarısız: " + ex);
            return 1;
        }
        finally
        {
            // Geçici kopyadan çalışıyorsak kendi dosyamızı arkamızda bırakma.
            // (Çalışan bir exe kendini silemez; ayrılmış bir cmd bekleyip siler.)
            SelfCleanup.ScheduleTempCleanup(options.Relocated, log);
        }
    }

    /// <summary>
    /// Ana uygulamanın kapanmasını bekler. Önce verilen PID, sonra kurulum
    /// klasöründe çalışan tüm AoGPN süreçleri izlenir; süre dolarsa YALNIZCA
    /// kurulum klasöründeki uygulamaya ait süreçler zorla kapatılır (üçüncü
    /// taraf süreçlere asla dokunulmaz).
    /// </summary>
    private static void WaitForApplicationExit(UpdaterOptions options, UpdaterLog log)
    {
        var deadline = DateTime.UtcNow.AddSeconds(options.WaitSeconds);

        if (options.WaitProcessId is { } pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero && !process.WaitForExit((int)remaining.TotalMilliseconds))
                {
                    log.Info($"Ana süreç (pid={pid}) {options.WaitSeconds} sn içinde kapanmadı; "
                        + "dosya kilidi zorlanmadan önce son bir tur beklenecek.");
                }
                else
                {
                    log.Info($"Ana süreç (pid={pid}) kapandı.");
                }
            }
            catch (ArgumentException)
            {
                log.Info($"Ana süreç (pid={pid}) zaten kapalı.");
            }
            catch (Exception ex)
            {
                log.Info($"Ana süreç beklenirken hata (yok sayıldı): {ex.Message}");
            }
        }

        // Kalan AoGPN süreçleri: kurulum klasöründen çalışan başka bir örnek
        // (ör. tepsi kalıntısı) varsa dosyaları kilitler — onu da bekleyip,
        // gerekirse kapat.
        var ownProcesses = FindAppProcesses(options.TargetDirectory);
        foreach (var process in ownProcesses)
        {
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero && process.WaitForExit((int)remaining.TotalMilliseconds))
                {
                    log.Info($"Kurulum klasöründeki AoGPN süreci kapandı (pid={process.Id}).");
                    continue;
                }

                log.Info($"Kurulum klasöründeki AoGPN süreci kapanmadı (pid={process.Id}); kapatılıyor.");
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                log.Info($"Süreç kapatılamadı (pid={process.Id}): {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Kurulum klasöründen çalışan AoGPN.exe süreçlerini bulur. Görüntü yolu
    /// okunamazsa (yetki) süreç ATLANIR — yanlış süreç asla kapatılmaz.
    /// </summary>
    private static List<Process> FindAppProcesses(string targetDirectory)
    {
        var result = new List<Process>();
        var normalizedTarget = Paths.NormalizeDirectory(targetDirectory);

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("AoGPN");
        }
        catch
        {
            return result;
        }

        foreach (var process in candidates)
        {
            try
            {
                var imagePath = process.MainModule?.FileName;
                if (imagePath is null)
                {
                    process.Dispose();
                    continue;
                }

                if (Paths.IsUnder(imagePath, normalizedTarget))
                {
                    result.Add(process);
                    continue;
                }
            }
            catch
            {
                // Erişim engellendi — bu süreç bize ait değil/okunamıyor, atla.
            }

            process.Dispose();
        }

        return result;
    }

    private static void TryDelete(string path, UpdaterLog log)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                log.Info($"İndirilen paket silindi: {path}");
            }
        }
        catch (Exception ex)
        {
            log.Info($"Paket silinemedi (yok sayıldı): {ex.Message}");
        }
    }
}

/// <summary>Zip kurulumu: üzerine yazma, üst düzey klasör soyma, kilit yeniden deneme.</summary>
internal sealed class ZipInstaller(UpdaterLog log)
{
    private const int ExtractAttempts = 5;
    private const int RetryDelayMs = 400;

    public int Extract(string zipPath, string targetDirectory)
    {
        var target = Paths.NormalizeDirectory(targetDirectory);
        Directory.CreateDirectory(target);

        using var archive = ZipFile.OpenRead(zipPath);
        var prefix = DetermineStrippedPrefix(archive);

        var written = 0;
        foreach (var entry in archive.Entries)
        {
            // Dizin girdileri (adı '/' ile biten) ayrıca açılmaz.
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = entry.FullName.Replace('\\', '/');
            if (prefix is not null && relative.StartsWith(prefix, StringComparison.Ordinal))
            {
                relative = relative[prefix.Length..];
            }

            relative = relative.TrimStart('/');
            if (relative.Length == 0)
            {
                continue;
            }

            // Yol kaçışı (zip slip) koruması: hedefin dışına yazan girdi reddedilir.
            var outputPath = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!Paths.IsUnder(outputPath, target))
            {
                log.Info($"Güvenlik: hedef dışına çıkan arşiv girdisi atlandı → {entry.FullName}");
                continue;
            }

            // bin/ altındaki mevcut çekirdek ikilileri korunur (yeniden indirme yok).
            if (IsUnderBin(relative) && File.Exists(outputPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (ExtractEntry(entry, outputPath))
            {
                written++;
            }
            else
            {
                log.Error($"Dosya yazılamadı (kilitli): {outputPath}");
            }
        }

        return written;
    }

    private static bool IsUnderBin(string relative)
        => relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Zip tek bir kök klasör içeriyorsa (ör. <c>AoGPN-windows-64/…</c>) o
    /// klasörün "<c>ad/</c>" önekini döndürür; zip düz ise null.
    /// </summary>
    private static string? DetermineStrippedPrefix(ZipArchive archive)
    {
        string? root = null;
        var hasNested = false;

        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0)
            {
                continue;
            }

            var separatorIndex = relative.IndexOf('/');
            if (separatorIndex < 0)
            {
                // Kök seviyede bir dosya var → zip düz, hiçbir şey soyulmaz.
                return null;
            }

            var candidate = relative[..(separatorIndex + 1)];
            if (root is null)
            {
                root = candidate;
            }
            else if (!string.Equals(root, candidate, StringComparison.Ordinal))
            {
                return null; // Birden fazla kök → düz kabul et.
            }

            hasNested = true;
        }

        return hasNested ? root : null;
    }

    /// <summary>
    /// Tek bir girdiyi yazar. Kilitli dosyalar (anti-virüs, arama dizini, geç
    /// kapanan çekirdek süreci) için artan beklemeyle yeniden dener.
    /// </summary>
    private static bool ExtractEntry(ZipArchiveEntry entry, string outputPath)
    {
        for (var attempt = 1; attempt <= ExtractAttempts; attempt++)
        {
            try
            {
                entry.ExtractToFile(outputPath, true);
                return true;
            }
            catch (IOException) when (attempt < ExtractAttempts)
            {
                Thread.Sleep(RetryDelayMs * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < ExtractAttempts)
            {
                Thread.Sleep(RetryDelayMs * attempt);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>Kendini geçici klasöre kopyalayıp oradan yeniden başlatır.</summary>
internal static class SelfRelocator
{
    public static bool IsInside(string? exePath, string targetDirectory)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        return Paths.IsUnder(exePath, Paths.NormalizeDirectory(targetDirectory));
    }

    public static int RelaunchFromTemp(UpdaterOptions options, UpdaterLog log)
    {
        var sourcePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(sourcePath))
        {
            log.Error("Çalışan exe yolu okunamadı; geçici klasöre taşınamıyor.");
            return 6;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"AoGPN.Updater-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var destinationPath = Path.Combine(tempRoot, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath, true);
        log.Info($"Güncelleyici geçici klasöre taşındı: {destinationPath}");

        // Framework'e bağımlı derlemede yanındaki yardımcı dosyalar gerekir
        // (AoGPN.Updater.dll / .deps.json / .runtimeconfig.json). Tek dosya
        // (self-contained single-file) yayınında hiçbiri bulunmaz — zararsız.
        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        var assemblyName = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (var companion in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var extra = Path.Combine(sourceDirectory, assemblyName + companion);
            try
            {
                if (File.Exists(extra))
                {
                    File.Copy(extra, Path.Combine(tempRoot, Path.GetFileName(extra)), true);
                }
            }
            catch (Exception ex)
            {
                log.Info($"Yardımcı dosya kopyalanamadı ({extra}): {ex.Message}");
            }
        }

        var arguments = options.ToRelaunchArguments();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = destinationPath,
            Arguments = arguments,
            WorkingDirectory = tempRoot,
            UseShellExecute = false,
        });

        if (process is null)
        {
            log.Error("Güncelleyici geçici klasörden başlatılamadı.");
            return 6;
        }

        log.Info($"Geçici kopya devraldı (pid={process.Id}); bu süreç çekiliyor.");
        return 0;
    }
}

/// <summary>Geçici klasörde kalan güncelleyici kopyasını gecikmeli temizler.</summary>
internal static class SelfCleanup
{
    public static void ScheduleTempCleanup(bool relocated, UpdaterLog log)
    {
        if (!relocated)
        {
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(directory)
            || !Path.GetFileName(directory).StartsWith("AoGPN.Updater-", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            // Çalışan bir exe kendini silemez: ayrılmış (detached) bir cmd,
            // güncelleyici çıktıktan sonra klasörü siler.
            using var cleanup = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{directory}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            log.Info($"Geçici klasör temizliği planlandı: {directory}");
        }
        catch (Exception ex)
        {
            log.Info($"Geçici klasör temizliği planlanamadı (yok sayıldı): {ex.Message}");
        }
    }
}

/// <summary>Komut satırı sözleşmesi.</summary>
internal sealed class UpdaterOptions
{
    public const string Usage =
        """
        AoGPN.Updater — AoGPN yan güncelleyicisi

        Kullanım:
          AoGPN.Updater.exe --zip <yeni-sürüm.zip> [--target <kurulum klasörü>]
                            [--restart <AoGPN.exe>] [--wait-pid <pid>]
                            [--wait-seconds <sn>] [--no-restart]

        Seçenekler:
          --zip           Açılacak yeni sürüm arşivi (zorunlu).
          --target        Kurulum klasörü. Varsayılan: bu exe'nin klasörü.
          --restart       Yeniden başlatılacak yürütülebilir dosya.
                          Varsayılan: <target>\AoGPN.exe
          --wait-pid      Önce kapanması beklenecek süreç kimliği.
          --wait-seconds  Bekleme üst sınırı (varsayılan 30).
          --no-restart    Arşivi aç ama uygulamayı başlatma.
        """;

    public required string ZipPath { get; init; }

    public required string TargetDirectory { get; init; }

    public string? RestartExePath { get; init; }

    public int? WaitProcessId { get; init; }

    public int WaitSeconds { get; init; } = DefaultWaitSeconds;

    public bool Restart { get; init; } = true;

    public bool Relocated { get; init; }

    private const int DefaultWaitSeconds = 30;

    /// <summary>Geçici kopyaya devredildiğini işaretleyen iç bayrak.</summary>
    internal const string RelocatedFlag = "--relocated";

    public static UpdaterOptions? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        string? zip = null;
        string? target = null;
        string? restart = null;
        int? waitPid = null;
        var waitSeconds = DefaultWaitSeconds;
        var restartEnabled = true;
        var relocated = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--zip":
                case "--target":
                case "--restart":
                case "--wait-pid":
                case "--wait-seconds":
                    if (i + 1 >= args.Length)
                    {
                        return null;
                    }

                    var value = args[++i];
                    switch (arg.ToLowerInvariant())
                    {
                        case "--zip":
                            zip = value;
                            break;
                        case "--target":
                            target = value;
                            break;
                        case "--restart":
                            restart = value;
                            break;
                        case "--wait-pid":
                            waitPid = int.TryParse(value, out var parsedPid) && parsedPid > 0 ? parsedPid : null;
                            break;
                        case "--wait-seconds":
                            if (int.TryParse(value, out var seconds) && seconds > 0)
                            {
                                waitSeconds = seconds;
                            }
                            break;
                    }

                    break;

                case "--no-restart":
                    restartEnabled = false;
                    break;

                case RelocatedFlag:
                    relocated = true;
                    break;

                default:
                    // Bilinmeyen argüman: yol içeren tek bir değer verilmişse zip
                    // olarak kabul et (elle kullanım kolaylığı), aksi halde hata.
                    if (zip is null && File.Exists(arg))
                    {
                        zip = arg;
                        break;
                    }

                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(zip))
        {
            return null;
        }

        target ??= Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        return new UpdaterOptions
        {
            ZipPath = Path.GetFullPath(zip),
            TargetDirectory = Path.GetFullPath(target),
            RestartExePath = restart is null ? null : Path.GetFullPath(restart),
            WaitProcessId = waitPid,
            WaitSeconds = waitSeconds,
            Restart = restartEnabled,
            Relocated = relocated,
        };
    }

    /// <summary>Geçici kopyaya devrederken aynı işi tarif eden argümanlar.</summary>
    public string ToRelaunchArguments()
    {
        var builder = new StringBuilder();
        builder.Append("--zip ").Append(Quote(ZipPath));
        builder.Append(" --target ").Append(Quote(TargetDirectory));
        if (RestartExePath is not null)
        {
            builder.Append(" --restart ").Append(Quote(RestartExePath));
        }

        if (WaitProcessId is { } pid)
        {
            builder.Append(" --wait-pid ").Append(pid);
        }

        builder.Append(" --wait-seconds ").Append(WaitSeconds);
        if (!Restart)
        {
            builder.Append(" --no-restart");
        }

        builder.Append(' ').Append(RelocatedFlag);
        return builder.ToString();
    }

    private static string Quote(string value) => $"\"{value}\"";
}

/// <summary>Yol karşılaştırmaları (tek yerde, Windows'a duyarsız).</summary>
internal static class Paths
{
    public static string NormalizeDirectory(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary><paramref name="candidate"/> verilen klasörün ALTINDA mı?</summary>
    public static bool IsUnder(string candidate, string normalizedDirectory)
    {
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return full.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, comparison)
            || string.Equals(full, normalizedDirectory, comparison);
    }
}

/// <summary>
/// Basit günlük: konsola ve <c>%TEMP%\AoGPN-updater.log</c> dosyasına yazar.
/// (Kurulum klasörüne yazmaz — güncellenen dizini kirletmez.)
/// </summary>
internal sealed class UpdaterLog
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "AoGPN-updater.log");
    private readonly Lock _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        Console.WriteLine(line);

        try
        {
            lock (_gate)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Günlük yazılamazsa güncelleme yine de yürümeli.
        }
    }
}
