using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WireGuardTunnelService — Faz 2b: DivertWorker kanalı → WireGuard tüneli →
// Wintun adaptörü enjeksiyon köprüsü
//
//   GpnCaptureLoop (WinDivert recv-only)     yakalanan oyun UDP paketleri
//        │ GetPacketsAsync()
//        ▼
//   InjectPacketAsync ──► IWireGuardTransport.Encrypt ──► SendAsync ──► UDP
//                                                                        │
//                                                        WireGuard sunucusuna (tip-4)
//
//   Ters yön (sunucu → oyun):
//   WireGuardNoiseTransport.RunReceiveLoopAsync (UDP'den tip-4)
//        ──► Decrypt ──► WintunSession.InjectPacket ──► Wintun adaptörü
//             (çözülen IP paketi)                      → OS → oyun soketi
//
// Şifreleme dikişi IWireGuardTransport'tur; varsayılan NoopWireGuardTransport
// (iskelet). Gerçek WireGuard veri düzlemi WireGuardNoise ilkelleriyle Faz 2c'de
// aynı arayüze bağlanır — köprü, telemetri ve test altyapısı değişmez.
//
// Sürücü durumu: wintun.dll + Wintun sürücüsü gerektirir (adapter oluşturma
// sürücüyü otomatik kurar — yönetici). Eksikse dostça WintunException; testler
// IWintunSession sahte uygulamasıyla sürücüsüz çalışır.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>WireGuard tünel köprüsünün paket başına telemetri anlık görüntüsü.</summary>
public sealed record GpnTunnelTelemetrySnapshot(
    long Captured,      // kanaldan okunan paket
    long Injected,      // adaptöre başarıyla yazılan (alım yolu — çözülen paketler)
    long InjectFailed,  // şifreleme/gönderim hatası nedeniyle atlanan (giden) + adaptör reddi (alım)
    long Encrypted,     // taşımanın şifrelediği
    long Received,      // UDP'den alınıp çözülen paket
    long DecryptFailed, // taşımanın çözemediği
    long Sent,          // sunucuya gönderilen tip-4 mesaj (Faz 2d veri yolu)
    long SendFailed);   // gönderilemeyen tip-4 mesaj

public sealed class WireGuardTunnelService : IAsyncDisposable
{
    private const uint DefaultRingCapacity = 0x400000; // 4 MiB — resmi örnekle aynı

    private IWireGuardTransport _transport;
    private readonly IWintunSession? _testSession;
    private readonly Func<IWintunSession>? _testSessionFactory;

    // Natif WintunCreateAdapter çağrısı için test dikişi. Gerçek yolda
    // WintunNative.WintunCreateAdapter'a gider; testler DLL-bulunamadı /
    // export-yok istisnalarını deterministik üretip hata eşlemesini (2 / 127)
    // sürücüye dokunmadan doğrular.
    private readonly Func<string, string, IntPtr, IntPtr> _createAdapter;

    // Kalan natif yaşam döngüsü çağrıları için dikişler (hepsi varsayılan olarak
    // WintunNative'e gider). Tek sahiplik kuralı: ADAPTÖRÜ yalnızca servis kapatır
    // (WintunCloseAdapter) — WintunSession.Dispose yalnızca session'ı bitirir.
    // Çift kapatma çift-free'dir → ntdll heap corruption (0xc0000374).
    private readonly Action<IntPtr> _closeAdapter;
    private readonly Action<IntPtr>? _endSession;
    private readonly Func<IntPtr, long> _getAdapterLuid;
    private readonly Func<IntPtr, uint, IntPtr> _startSession;

    private readonly object _gate = new();
    private IntPtr _adapter;
    private IWintunSession? _session;
    private bool _disposed;

    // Telemetri sayaçları (decrypt hataları taşıma tarafında sayılır — DecryptFailedCount)
    private long _captured;
    private long _injected;
    private long _injectFailed;
    private long _received;

    /// <param name="transport">WireGuard şifreleme dikişi (varsayılan Noop).</param>
    /// <param name="session">
    /// Test amaçlı sahte session. Verilirse <see cref="Open"/> natif katmanı
    /// çağırmaz — sürücüsüz doğrulama mümkün olur.
    /// </param>
    /// <param name="sessionFactory">
    /// Test dikişi (re-arm/sunucu değişimi senaryoları): gerçek yol her Open'da TAZE
    /// bir natif session üretir; factory de her Open'da taze sahte session verir —    ///     tek-örnek sahte Close sonrası kalıcı kapandığından Close→Open çevrimi
    ///     (GpnCaptureBridge dinamik re-arm) sahteyle test edilemezdi.
    /// </param>
    /// <param name="closeAdapter">Adaptörü kapatan natif çağrı (varsayılan WintunCloseAdapter).</param>
    /// <param name="endSession">Session'ı bitiren natif çağrı (varsayılan WintunEndSession).</param>
    /// <param name="getAdapterLuid">Adapter LUID okuyucu (varsayılan WintunGetAdapterLUID).</param>
    /// <param name="startSession">Session başlatıcı (varsayılan WintunStartSession).</param>
    public WireGuardTunnelService(
        IWireGuardTransport? transport = null,
        IWintunSession? session = null,
        Func<string, string, IntPtr, IntPtr>? createAdapter = null,
        Func<IWintunSession>? sessionFactory = null,
        Action<IntPtr>? closeAdapter = null,
        Action<IntPtr>? endSession = null,
        Func<IntPtr, long>? getAdapterLuid = null,
        Func<IntPtr, uint, IntPtr>? startSession = null)
    {
        _transport = transport ?? new NoopWireGuardTransport();
        _testSession = session;
        _createAdapter = createAdapter ?? WintunNative.WintunCreateAdapter;
        _testSessionFactory = sessionFactory;
        _closeAdapter = closeAdapter ?? WintunNative.WintunCloseAdapter;
        _endSession = endSession;
        _getAdapterLuid = getAdapterLuid ?? (h =>
        {
            WintunNative.WintunGetAdapterLuid(h, out var luid);
            return luid;
        });
        _startSession = startSession ?? WintunNative.WintunStartSession;
    }

    /// <summary>Adapter/session açık mı.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _session is { IsOpen: true };
            }
        }
    }

    /// <summary>Oluşturulan adapter adı (yalnızca informational).</summary>
    public string? AdapterName { get; private set; }

    /// <summary>Adapter'ın NET_LUID değeri (netsh/IP Helper entegrasyonu için).</summary>
    public long? AdapterLuid { get; private set; }

    /// <summary>Son <see cref="Open"/> çağrısında kullanılan (normalleştirilmiş) halka tampon kapasitesi.</summary>
    public uint? LastRingCapacity { get; private set; }

    /// <summary>Şifreleme dikişi (Noop ise sayaçları telemetriye akar).</summary>
    public IWireGuardTransport Transport => Volatile.Read(ref _transport);

    /// <summary>
    /// Taşıma dikişini değiştirir — Faz 2c: bağlantı anında profilin anahtarlarıyla
    /// gerçek WireGuard veri düzlemi (WireGuardNoiseTransport) kurulur. Session
    /// KAPALIYKEN çağrılmalıdır (Open öncesi); yoksa InvalidOperationException.
    /// </summary>
    public void UseTransport(IWireGuardTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (_gate)
        {
            if (_session is { IsOpen: true })
            {
                throw new InvalidOperationException("Transport değişimi yalnızca session kapalıyken yapılabilir.");
            }
            Volatile.Write(ref _transport, transport);
        }
    }

    /// <summary>
    /// Wintun adapter + session açar. Natif yolda adapter oluşturma sürücüyü
    /// otomatik kurar (yönetici gerekir); wintun.dll yoksa WintunException.
    /// </summary>
    public void Open(string adapterName, string tunnelType = "AoGPN", uint ringCapacity = DefaultRingCapacity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_session is { IsOpen: true })
            {
                throw new InvalidOperationException("WireGuard tünel session'ı zaten açık.");
            }

            AdapterName = adapterName;
            LastRingCapacity = NormalizeCapacity(ringCapacity);

            if (_testSessionFactory is not null)
            {
                _session = _testSessionFactory(); // her Open taze sahte session (gerçek yol gibi)
                return;
            }
            if (_testSession is not null)
            {
                _session = _testSession; // test: natif katman yok
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new WintunException("Wintun yalnızca Windows'ta çalışır.", 0);
            }

            IntPtr adapter;
            try
            {
                adapter = _createAdapter(adapterName, tunnelType, IntPtr.Zero);
            }
            catch (DllNotFoundException ex)
            {
                throw new WintunException("wintun.dll bulunamadı — uygulamanın yanına kopyalayın (sürücüyle birlikte).", 2 /* ERROR_FILE_NOT_FOUND */, ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new WintunException("wintun.dll sürümü eski — güncel wintun.dll gerekir (WintunCreateAdapter export'u yok).", 127, ex);
            }

            if (adapter == IntPtr.Zero)
            {
                var err = WintunNative.LastWin32Error;
                throw new WintunException(MissingDriverHint(err), err);
            }

            _adapter = adapter;
            try
            {
                AdapterLuid = _getAdapterLuid(adapter);

                var session = _startSession(adapter, NormalizeCapacity(ringCapacity));
                if (session == IntPtr.Zero)
                {
                    var err = WintunNative.LastWin32Error;
                    throw new WintunException("Wintun session başlatılamadı.", err);
                }
                _session = new WintunSession(session, _endSession);
            }
            catch
            {
                _closeAdapter(adapter);
                _adapter = IntPtr.Zero;
                _session = null;
                throw;
            }
        }
    }

    /// <summary>
    /// Paketi tünel üzerinden GERÇEK sunucuya gönderir (Faz 2d veri yolu): önce
    /// <see cref="IWireGuardTransport.Encrypt"/> (tip-4), ardından
    /// <see cref="IWireGuardTransport.SendAsync"/> (UDP). Veri yolu kapalıysa veya
    /// şifreleme/gönderim başarısızsa false — köprü sayaçta işler, döngü ölmez.
    /// </summary>
    public async ValueTask<bool> InjectPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        // Encrypt senkron ve ilk await'ten ÖNCE çalışır — havuzlanmış tampon
        // (AsMemory) yalnızca bu aşamada okunur; sonrası şifrelenmiş kopyadır.
        var encrypted = Volatile.Read(ref _transport).Encrypt(packet.Span);
        if (encrypted is null)
        {
            Interlocked.Increment(ref _injectFailed);
            return false;
        }

        var ok = await Volatile.Read(ref _transport).SendAsync(encrypted, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            Interlocked.Increment(ref _injectFailed);
        }
        return ok;
    }

    /// <summary>
    /// Çözülmüş IP paketini Wintun adaptörüne enjekte eder (alım yolu — OS oyuna
    /// teslim eder). Session kapalıysa/reddederse false; sayaç işler.
    /// </summary>
    public bool InjectIntoAdapter(ReadOnlySpan<byte> decrypted)
    {
        var session = GetOpenSession();
        if (session is null)
        {
            return false;
        }

        var ok = session.InjectPacket(decrypted);
        if (ok)
        {
            Interlocked.Increment(ref _injected);
        }
        else
        {
            Interlocked.Increment(ref _injectFailed);
        }
        return ok;
    }

    /// <summary>
    /// Yakalama köprüsü: DivertWorker kanalını (GetPacketsAsync) tüketir ve her
    /// paketi InjectPacket ile adaptöre taşır. İptal/kapanışta temiz biter.
    /// GpnCaptureLoop'a inject hattı olarak da verilebilir
    /// (<see cref="CreateInjectHandler"/>).
    /// </summary>
    public async Task RunCaptureBridgeAsync(IAsyncEnumerable<DivertedPacket> packets, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in packets.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                Interlocked.Increment(ref _captured);
                try
                {
                    // InjectPacketAsync, paket verisini YALNIZCA senkron Encrypt aşamasında
                    // okur (sonrası şifrelenmiş kopya üzerinden await eder) — döndüğünde
                    // kiralanan tampon serbestçe havuza dönebilir (Tier 4). Yalnızca
                    // mantıksal uzunluk işlenir — havuz kapasitesi değil (Length).
                    await InjectPacketAsync(packet.Data.AsMemory(0, packet.Length), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    packet.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
    }

    /// <summary>
    /// Alım döngüsü (Faz 2d): taşımanın UDP veri yolunu tüketir — sunucudan gelen
    /// tip-4 mesajlar transport tarafında çözülür; çözülen IP paketi Wintun
    /// adaptörüne enjekte edilir (OS oyuna teslim eder) VE tüketiciye verilir
    /// (telemetri/köprü). 0-bayt paket (keepalive) adaptöre enjekte edilmez.
    /// İptalde veya soket kapanınca temiz biter.
    /// </summary>
    public async Task RunReceiveLoopAsync(
        Func<byte[], CancellationToken, ValueTask> consumer,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Volatile.Read(ref _transport).RunReceiveLoopAsync(
                OnDecryptedPacketAsync,
                idleTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal kapanış
        }
        catch (Exception ex)
        {
            // Alt döngüden sızabilecek herhangi bir hata (taşıma null dahil)
            // burada son bulur — AppDomain'e kaçıp uygulamayı öldüremez.
            Logging.SaveLog($"WireGuardTunnelService.ReceiveLoop: {ex}");
        }

        async ValueTask OnDecryptedPacketAsync(byte[] decrypted, CancellationToken ct)
        {
            Interlocked.Increment(ref _received);
            if (decrypted.Length > 0)
            {
                InjectIntoAdapter(decrypted); // Wintun → OS → oyun soketi
            }
            await consumer(decrypted, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// GpnCaptureLoop'un inject hattı olarak kullanılacak delege — yakalanan her
    /// paket tünel veri yoluna gider (recv-only yakalama → Encrypt → UDP → sunucu).
    /// </summary>
    public Func<DivertedPacket, CancellationToken, ValueTask> CreateInjectHandler()
        => async (packet, ct) =>
        {
            await InjectPacketAsync(packet.Data.AsMemory(0, packet.Length), ct).ConfigureAwait(false);
        };

    /// <summary>Paket başına telemetri anlık görüntüsü (dashboard akışı için).</summary>
    public GpnTunnelTelemetrySnapshot Snapshot()
    {
        var transport = Volatile.Read(ref _transport);
        return new GpnTunnelTelemetrySnapshot(
            Captured: Interlocked.Read(ref _captured),
            Injected: Interlocked.Read(ref _injected),
            InjectFailed: Interlocked.Read(ref _injectFailed),
            Encrypted: transport.EncryptedCount,
            Received: Interlocked.Read(ref _received),
            DecryptFailed: transport.DecryptFailedCount,
            Sent: transport.SentCount,
            SendFailed: transport.SendFailedCount);
    }

    public void Close()
    {
        IWintunSession? session;
        lock (_gate)
        {
            // NOT: _disposed burada set edilmez — Close yalnızca akışı durdurur ve tünel
            // yeniden açılabilir (GpnCaptureBridge sunucu değişimi/failover'da Open eder).
            // Kalıcı dispose (ObjectDisposedException guard'ı) yalnızca DisposeAsync'te.
            session = _session;
            _session = null;
        }
        session?.Dispose();

        lock (_gate)
        {
            if (_adapter != IntPtr.Zero)
            {
                // Adaptörün TEK sahibi servistir — WintunSession.Dispose artık adaptörü
                // kapatmaz (çift-free → heap corruption 0xc0000374 regresyonu).
                _closeAdapter(_adapter); // _testSession yolunda sıfır
                _adapter = IntPtr.Zero;
            }
        }

        // Faz 2d: taşımanın UDP veri yolu soketini de kapat (WireGuardNoiseTransport).
        if (Volatile.Read(ref _transport) is IDisposable transport)
        {
            transport.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true; // kalıcı kapanış: sonraki Open/GetOpenSession guard'ı devreye girer
        }
        Close();
        await Task.CompletedTask;
    }

    private IWintunSession? GetOpenSession()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }
            return _session is { IsOpen: true } ? _session : null;
        }
    }

    private static uint NormalizeCapacity(uint capacity)
    {
        capacity = Math.Clamp(capacity, WintunNative.MinRingCapacity, WintunNative.MaxRingCapacity);
        // 2'nin katına yuvarla (halka tampon gereksinimi)
        var power = 1u;
        while (power < capacity)
        {
            power <<= 1;
        }
        return power;
    }

    private static string MissingDriverHint(int error)
        => error switch
        {
            2 => "Wintun sürücüsü bulunamadı — wintun.dll + sürücüyü uygulamanın yanına kopyalayın.",
            5 => "Yönetici ayrıcalığı gerekir — uygulamayı yönetici olarak çalıştırın.",
            87 => "Geçersiz parametre — adaptör adında Wintun'un izin vermediği bir karakter (iki nokta vb.) veya isim çok uzun; ad 32 karaktere sınırlanmalı.",
            1275 => "Wintun sürücüsü engellendi (güvenlik yazılımı veya sanallaştırma).",
            _ => "Wintun adapter oluşturulamadı.",
        };
}
