namespace ServiceLib.Services;

/// <summary>
/// Yan güncelleyici (<c>AoGPN.Updater.exe</c>) ile ana uygulama arasındaki
/// KOMUT SATIRI SÖZLEŞMESİ.
///
/// Güncelleyici bilerek ana derlemeye bağlı DEĞİLDİR (kendi dosyaları kilitliyken
/// çalışması gerekir, bu yüzden ServiceLib'e referans veremez). Bu yüzden sözleşme
/// burada — ana uygulama tarafında, test edilebilir tek bir yerde — tutulur ve
/// güncelleyicideki çözümleyici (<c>AoGPN.Updater.Program/UpdaterOptions.Parse</c>)
/// bu biçimi birebir kabul eder. İki taraftan biri değişirse
/// <c>AppUpdateUpdaterContractTests</c> kırılır.
///
/// Kararlaştırılan biçim:
/// <code>
///   --zip "&lt;indirilen zip&gt;"
///   --target "&lt;kurulum klasörü&gt;"
///   --restart "&lt;AoGPN.exe yolu&gt;"
///   --wait-pid &lt;ana sürecin kimliği&gt;
///   --wait-seconds &lt;bekleme üst sınırı&gt;
/// </code>
/// </summary>
public static class AppUpdateUpdaterContract
{
    /// <summary>Yan güncelleyicinin dosya adı (uzantısız; <see cref="Common.Utils.GetExeName"/> ekler).</summary>
    public const string UpdaterExeName = "AoGPN.Updater";

    /// <summary>Güncelleyicinin ana süreci beklemesi için önerilen üst sınır (saniye).</summary>
    public const int DefaultWaitSeconds = 30;

    /// <summary>
    /// Güncelleme paketini açacağı klasörü ve yeniden başlatılacak dosyayı
    /// tarif eden argüman dizesini üretir. Yollar boşluk içerebildiği için
    /// tırnaklanır.
    /// </summary>
    public static string BuildArguments(
        string zipPath,
        string targetDirectory,
        string restartExePath,
        int waitProcessId,
        int waitSeconds = DefaultWaitSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        return $"--zip {Quote(zipPath)} "
            + $"--target {Quote(targetDirectory)} "
            + $"--restart {Quote(restartExePath)} "
            + $"--wait-pid {waitProcessId} "
            + $"--wait-seconds {waitSeconds}";
    }

    private static string Quote(string value) => $"\"{value}\"";
}
