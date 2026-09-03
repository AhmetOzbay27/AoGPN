using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// DivertWorker — WinDivertRecv bloklama döngüsü
//
// WinDivertRecv natif olarak bloklar ve tek bir iş parçacığıyla okunmalıdır;
// bu yüzden çalışan ayrı bir arka plan iş parçacığında yaşar. Yakalanan her
// saf IP paketi, tünele enjeksiyondan önce tüketicinin dh hızında işleyebilmesi
// için SABİT SINIRLI (bounded) bir Channel'e yazılır — geri basınç (backpressure)
// sayesinde yüksek hızda araya giren oyun trafiği belleği patlatmaz.
//
// Durdurma (Deactivate/uygulama kapanışı): StopAsync → iptal + handle kapatılınca
// (WinDivertEngine.Close) bekleyen WinDivertRecv false döner → döngü sonlanır.
//─────────────────────────────────────────────────────────────────────────
public sealed class DivertWorker
{
    /// <summary>WinDivert/oyun UDP paketlerinin en büyük varsayılan boyutu.</summary>
    internal const int MaxPacketLength = 0xFFFF;

    private static int _idSeed;

    private readonly IWinDivertApi _api;
    private readonly IntPtr _handle;
    private readonly Channel<DivertedPacket> _channel;
    private readonly CancellationTokenSource _cts;
    private Thread? _thread;

    /// <summary>Yakalanan paket kanalında kaç paket birikebilir (geri basınç eşiği).</summary>
    public int QueueCapacity { get; }

    public DivertWorker(IWinDivertApi api, IntPtr handle, CancellationToken extractionToken, int queueCapacity = 1024)
    {
        _api = api;
        _handle = handle;
        QueueCapacity = queueCapacity;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(extractionToken);
        _channel = Channel.CreateBounded<DivertedPacket>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public bool IsRunning => _thread?.IsAlive == true;

    /// <summary>
    /// Yakalanan paketlerin CANLI akışı — tünel enjeksiyon aşaması bunu tüketir.
    /// </summary>
    public IAsyncEnumerable<DivertedPacket> GetPacketsAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Sürücüden okuma döngüsünü ayrı bir arka plan iş parçacığında yürütür.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }
        var id = Interlocked.Increment(ref _idSeed);
        _thread = new Thread(Loop)
        {
            IsBackground = true,   // uygulama kapanışını asla engellemez
            Name = $"WinDivertWorker-{id}",
        };
        _thread.Start();
    }

    /// <summary>İptal eder, handler kapatmaz; döngü ikinci/bekleyen Recv'de false görüp çıkar.</summary>
    public async Task StopAsync()
    {
        _cts.Cancel();
        var workerThread = _thread;
        _thread = null;

        if (workerThread is { IsAlive: true })
        {
            // Sürücü kapandığında bekleyen Recv unblock olup false döner; kısa bir süre
            // sabırla bekleriz, ardından arka plan iş parçacığı kendini bırakır.
            _ = workerThread.Join(TimeSpan.FromMilliseconds(1000));
        }

        _channel.Writer.TryComplete();
        await Task.CompletedTask;
    }

    private void Loop()
    {
        var buffer = Marshal.AllocHGlobal(MaxPacketLength);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var address = new WinDivertAddress();
                bool ok = false;
                int recvLen = 0;
                try
                {
                    ok = _api.Recv(_handle, buffer, MaxPacketLength, out recvLen, ref address);
                }
                catch (OperationCanceledException)
                {
                    // İptal VEYA handle kapanış yarışı — ikisi de normal duruştur.
                    // `when` filtresi KONULMAZ: sürücü kapanırken dahili token ile
                    // fırlayan bir OCE filtreden kaçıp ham iş parçacığını öldürür
                    // (CurrentDomain_UnhandledException — rota değişiminde çökme
                    // sınıfı). Filtresiz yakalama tek istisnanın bile AppDomain'e
                    // ulaşmasını imkânsız kılar.
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break; // handle kapandı
                }
                catch (Exception ex)
                {
                    // Sürücü kaldırılması/kapanış yarışı natif katmanda her türlü
                    // istisna üretebilir (SocketException, sarmalayıcı hatalar vb.).
                    // Bu iş parçacığı hamdır — tek bir istisna dışarı kaçarsa
                    // uygulama anında kapanır. Döngü güvenle biter.
                    Logging.SaveLog($"DivertWorker.Recv: {ex}");
                    break;
                }

                if (ok && recvLen > 0)
                {
                    var data = new byte[recvLen];
                    Marshal.Copy(buffer, data, 0, recvLen);

                    // Tüketici hızına saygı — kanal dolarsa yazılabilir olana kadar bekle (geri basınç).
                    if (_channel.Writer.TryWrite(new DivertedPacket(data, address, DateTimeOffset.UtcNow)))
                    {
                        continue;
                    }
                    if (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                    try
                    {
                        _ = _channel.Writer.WaitToWriteAsync(_cts.Token).AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        break; // iptal — filter'sız (kapanış yarışında da güvenli)
                    }
                    catch (Exception ex)
                    {
                        // StopAsync ile yarışta kanal tamamlanmış olabilir — yazma
                        // hataları döngüyü öldürmez, temiz biter.
                        Logging.SaveLog($"DivertWorker.Channel: {ex}");
                        break;
                    }
                }
                else if (!_cts.IsCancellationRequested)
                {
                    // ok==false → bekleyen handle kapandı veya gerçek hata; kısa ara (fırtına yaratma).
                    Thread.Sleep(10);
                }
            }
        }
        catch (Exception ex)
        {
            // Emniyet kemeri: yukarıdaki hiçbir dal beklenmedik bir istisna kaçırırsa
            // burada son bulur — ham iş parçacığı AppDomain'e asla kaçırmaz.
            Logging.SaveLog($"DivertWorker.Loop: {ex}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _channel.Writer.TryComplete();
        }
    }
}