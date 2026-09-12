using AoGPN.Services;
using ServiceLib.Services;

namespace AoGPN.Views;

/// <summary>
/// Yeni sürüm bildirimi ve onay penceresi.
///
/// Akış (kullanıcı sözleşmesi):
///   1. Açılışta yeni sürüm bulunur → indirme ARKA PLANDA bu pencere açıkken başlar,
///   2. İndirme BİTİNCE kullanıcıya onay sorulur (düğmeler bu anda etkinleşir),
///   3. Onay verilirse <see cref="InstallRequested"/> true olur ve indirilen paket
///      yolu <see cref="DownloadedZipPath"/> ile çağırana verilir; güncelleyiciyi
///      başlatıp uygulamayı kapatmak MainWindow'un işidir.
///
/// Pencere hiçbir süreç öldürmez, hiçbir dosyanın üzerine yazmaz — yalnızca indirir.
/// Dosyaları yerine koyan tek bileşen <c>AoGPN.Updater</c>'dır.
/// </summary>
public partial class UpdateAvailableWindow : Window
{
    /// <summary>Sürüm notları çok uzun olabilir; pencereyi şişirmemek için kırpılır.</summary>
    private const int MaxNotesLength = 4000;

    private readonly AppUpdateInfo _update;
    private readonly AppUpdateInstaller _installer;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _downloadStarted;
    private bool _downloadFinished;

    public UpdateAvailableWindow(AppUpdateInfo update, AppUpdateInstaller installer)
    {
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));

        InitializeComponent();

        Title = $"AoGPN — {update.Version}";
        txtTitle.Text = update.Title.IsNotEmpty() ? update.Title! : "Yeni sürüm hazır";
        txtVersion.Text = BuildVersionLine(update);
        txtNotes.Text = BuildNotes(update);
        btnInstall.Content = "Şimdi Güncelle";

        Closed += (_, _) => _cancellation.Cancel();
    }

    /// <summary>Kullanıcı "Şimdi Güncelle" dedi mi?</summary>
    public bool InstallRequested { get; private set; }

    /// <summary>İndirilen güncelleme paketinin yolu (yalnızca indirme başarılıysa).</summary>
    public string? DownloadedZipPath { get; private set; }

    /// <summary>İndirmeyi arka planda başlatır. Pencere açıldıktan sonra çağrılır.</summary>
    public void StartDownload()
    {
        if (_downloadStarted)
        {
            return;
        }

        _downloadStarted = true;
        _ = RunDownloadAsync();
    }

    private async Task RunDownloadAsync()
    {
        try
        {
            // Progress<T> geri çağrısı oluşturulduğu iş parçacığının bağlamına
            // (burada UI dispatcher'ına) geri posta edilir — bar güvenle güncellenir.
            // Ad bilinçli olarak farklı: XAML'deki ProgressBar alanı "progress".
            var progressReporter = new Progress<double>(ReportProgress);

            DownloadedZipPath = await _installer.DownloadAsync(_update, progressReporter, _cancellation.Token)
                .ConfigureAwait(true);

            _downloadFinished = true;
            progress.IsIndeterminate = false;
            progress.Value = 100;
            txtStatus.Text = "İndirme tamamlandı. Güncelleme şimdi uygulanabilir.";
            btnInstall.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Kullanıcı pencereyi kapattı — sessizce çık.
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[AppUpdate] Güncelleme indirilemedi", ex);
            progress.IsIndeterminate = false;
            progress.Value = 0;
            txtStatus.Text = $"İndirme başarısız: {ex.Message} — daha sonra tekrar deneyin.";
            btnInstall.IsEnabled = false;
        }
    }

    private void ReportProgress(double value)
    {
        if (value >= 1)
        {
            return; // Tamamlanma durumu indirme bittiğinde tek yerden yazılır.
        }

        progress.IsIndeterminate = false;
        progress.Value = Math.Clamp(value * 100, 0, 100);
        txtStatus.Text = $"İndiriliyor… %{progress.Value:0}";
    }

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (!_downloadFinished || DownloadedZipPath.IsNullOrEmpty())
        {
            return;
        }

        InstallRequested = true;
        Close();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        _cancellation.Cancel();
        Close();
    }

    private static string BuildVersionLine(AppUpdateInfo update)
    {
        var local = Utils.GetVersionInfo();
        var size = update.AssetSize > 0 ? $" · {FormatSize(update.AssetSize)}" : "";
        var pre = update.Prerelease ? " · ön sürüm" : "";
        return $"Yüklü sürüm {local}  →  yeni sürüm {update.Version}{pre}{size}";
    }

    private static string BuildNotes(AppUpdateInfo update)
    {
        if (update.Notes.IsNullOrEmpty())
        {
            return "Bu sürüm için sürüm notu yayınlanmamış.";
        }

        var notes = update.Notes!;
        return notes.Length <= MaxNotesLength
            ? notes
            : notes[..MaxNotesLength] + Environment.NewLine + "…";
    }

    private static string FormatSize(long bytes)
    {
        const double Kilobyte = 1024;
        const double Megabyte = Kilobyte * 1024;
        const double Gigabyte = Megabyte * 1024;

        return bytes switch
        {
            >= (long)Gigabyte => $"{bytes / Gigabyte:0.0} GB",
            >= (long)Megabyte => $"{bytes / Megabyte:0.0} MB",
            >= (long)Kilobyte => $"{bytes / Kilobyte:0} KB",
            _ => $"{bytes} B",
        };
    }
}
