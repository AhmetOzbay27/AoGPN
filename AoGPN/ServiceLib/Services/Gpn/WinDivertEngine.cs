using System.Runtime.InteropServices;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WinDivertEngine — WinDivert wrapper'ın üst katmanı
//
//   Open(filter)  → sürücü filtresini kurar, handle açar
//   Send(paket)   → yakalanan/enjekte edilen IP paketini aynı arayüzden gönderir
//   Close/Dispose → handle'ı kapatır (filtreyi sürücüden kaldırır), sürücüyü serbest bırakır
//
// Bellek yönetimi: natif çağrılar IntPtr arabellekler kullanır ve her çağrıda
// Marshal.AllocHGlobal/FreeHGlobal ile dengelidir (unsafe blok yok). Handle
// sahibi tek katman burasıdır; DivertWorker yalnızca sürücüden okur.
// ─────────────────────────────────────────────────────────────────────────
public sealed class WinDivertEngine : IDisposable, IAsyncDisposable
{
    private static readonly IntPtr InvalidHandle = new(-1);

    private readonly IWinDivertApi _api;
    private readonly object _gate = new();
    private IntPtr _handle;
    private DivertWorker? _worker;
    private bool _disposed;

    /// <summary>Şu an açık olan filtre dizesi (yalnızca informational).</summary>
    public string? Filter { get; private set; }

    /// <summary>Sürücü kurulu/erişilebilir mi — Open başarılı olmuş mu.</summary>
    public bool IsOpen => _handle != IntPtr.Zero && _handle != InvalidHandle;

    /// <summary>Yalnızca Windows'ta anlamlı; başka platformda Open imkânsızdır.</summary>
    public static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public WinDivertEngine(IWinDivertApi? api = null)
    {
        // DI/test dostu: belirtilmezse natif katman kullanılır.
        _api = api ?? new WinDivertApi();
    }

    /// <summary>
    /// WinDivert sürücü filtresini açar. <paramref name="filter"/> null ise
    /// "yakala" modu için anlamlı bir varsayılan kurulmaz — mutlaka verilmelidir
    /// (genellikle WinDivertFilterBuilder.BuildFilter çıktısı).
    /// </summary>
    public void Open(string? filter, int layer = WinDivertNative.LayerNetwork, short priority = 0, ulong flags = 0)
    {
        // Resmî WinDivert, tanımadığı flag bitlerini hata 87 ile reddeder —
        // klasik yola giren flags her zaman resmî bit kümesine süzülür.
        var handle = SafeOpenInvocations(() => _api.Open(filter, layer, priority, flags & WinDivertNative.ClassicFlagMask));
        // Hata kodunu çağrının HEMEN ardından yakala: aradaki herhangi bir
        // P/Invoke (günlük dosya yazma, SetParam vb.) iş parçacığının son hata
        // değerini sıfırlar ve "Win32 hata kodu: 0" ile kök neden gizlenir.
        var nativeError = _api.GetLastError();
        CommitHandle(handle, filter, nativeError);
    }

    /// <summary>
    /// WinDivertOpenEx (v2.2+): klasik <see cref="Open"/> ile aynı işi yapar ama
    /// kuyruk parametrelerini (<see cref="WinDivertOpenParams"/> — katman/yön bazlı
    /// QueueLen/Time/Size) ve isteğe bağlı <paramref name="flags"/> değerini iletir.
    /// WinDivertOpenEx sürücüde yoksa (eski WinDivert) alınır; açılmazsa güncel
    /// <see cref="WinDivertException"/> fırlatılır.
    /// </summary>
    public void OpenEx(string? filter, in WinDivertOpenParams openParams, int layer = WinDivertNative.LayerNetwork, short priority = 0, ulong flags = 0)
    {
        // in-parametre lambda içinde yakalanamaz (CS1628) — önce kopyala.
        var ps = openParams;
        IntPtr handle;
        var nativeError = 0;
        try
        {
            handle = _api.OpenEx(filter, layer, priority, flags, ps);
            nativeError = _api.GetLastError(); // hemen yakala — aradaki çağrılar kodu sıfırlamasın
        }
        catch (EntryPointNotFoundException)
        {
            // Resmî WinDivert 2.2.2 dağıtımı WinDivertOpenEx'i dışa aktarmaz.
            // Klasik Open'a düşülür: fork/OpenEx'e özgü bitler maskelenir; kuyruk
            // parametreleri OpenParams slotlarından tek anlamlı değer seçilip
            // WinDivertSetParam ile uygulanır (resmî parametreler globaldir).
            DiagLog.Write("WINDIVERT OpenEx export yok → klasik WinDivertOpen (resmî 2.2.2; kuyruk paramları SetParam ile)");
            handle = SafeOpenInvocations(() => _api.Open(filter, layer, priority, flags & WinDivertNative.ClassicFlagMask));
            nativeError = _api.GetLastError(); // hemen yakala (aynı neden)
            if (handle != IntPtr.Zero && handle != InvalidHandle)
            {
                ApplyQueueParamsViaSetParam(handle, ps);
            }
        }
        catch (DllNotFoundException ex)
        {
            throw WrapDllMissing(ex);
        }
        CommitHandle(handle, filter, nativeError);
    }

    /// <summary>
    /// OpenEx export'u olmayan DLL'de kuyruk ayarlarını klasik WinDivertSetParam
    /// ile uygular. Resmî WinDivert parametreleri globaldir (katman/yön slotu
    /// yok); OpenParams slotlarındaki TEK anlamlı (nonzero ve çakışmasız) değer
    /// seçilir — GpnCaptureSettingsMapper tek slota yazar, diğerleri 0 kalır.
    /// </summary>
    private void ApplyQueueParamsViaSetParam(IntPtr handle, in WinDivertOpenParams ps)
    {
        ApplyDistinctQueueParam(handle, WinDivertNative.QueueLen, ps.QueueLen0, ps.QueueLen1, ps.QueueLen2, ps.QueueLen3);
        ApplyDistinctQueueParam(handle, WinDivertNative.QueueTime, ps.QueueTime0, ps.QueueTime1, ps.QueueTime2, ps.QueueTime3);
        ApplyDistinctQueueParam(handle, WinDivertNative.QueueSize, ps.QueueSize0, ps.QueueSize1, ps.QueueSize2, ps.QueueSize3);
    }

    private void ApplyDistinctQueueParam(IntPtr handle, int param, params uint[] slots)
    {
        var distinct = slots.Where(v => v != 0).Distinct().ToArray();
        if (distinct.Length != 1)
        {
            return; // hiç ayar yok ya da slotlar çelişiyor (gerçek OpenEx) — dokunma
        }
        if (!_api.SetParam(handle, param, distinct[0]))
        {
            DiagLog.Write($"WINDIVERT SetParam({param}) başarısız — hata {_api.GetLastError()}");
        }
    }

    /// <summary>
    /// WinDivert.dll çalışma zamanı bağımlılığıdır — uygulama klasöründe yoksa
    /// source-generated P/Invoke DllNotFoundException fırlatır. Köprü açılışında
    /// bu ham .NET hatası yerine anlamlı bir <see cref="WinDivertException"/>
    /// üretilir (kullanıcıya neyin eksik olduğu söylenir: WinDivert.dll +
    /// WinDivert64.sys birlikte dağıtılır ve sürücü ilk Open'ta otomatik kurulur).
    /// </summary>
    private static IntPtr SafeOpenInvocations(Func<IntPtr> open)
    {
        try
        {
            return open();
        }
        catch (DllNotFoundException ex)
        {
            throw WrapDllMissing(ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new WinDivertException(
                $"WinDivert.dll sürümü uyumsuz — güncel WinDivert 2.2+ gerekir ({ex.Message}).",
                Native.ERROR_PROC_NOT_FOUND, ex);
        }
    }

    private static WinDivertException WrapDllMissing(DllNotFoundException ex)
        => new(
            $"WinDivert.dll bulunamadı — sürücü dosyaları (WinDivert.dll + WinDivert64.sys) uygulama klasöründe olmalı ({ex.Message}).",
            Native.ERROR_MODULE_NOT_FOUND, ex);

    /// <summary>
    /// Sniff modunda (WINDIVERT_FLAG_SNIFF) OpenEx açar: paketler kopyalanıp
    /// kullanıcıya verilir AMA ağ yığınında ilerlemeye devam eder (yakala-bırak).
    /// Bu en güvenli doğrulama modudur — filtre doğru PID'leri yakalıyor mu diye
    /// trafiği bozmadan teyit edilir. Sniff handle'ında asla WinDivertSend
    /// çağrılmamalıdır (paket çoğaltır).
    /// </summary>
    public void OpenSniff(string? filter, in WinDivertOpenParams openParams, int layer = WinDivertNative.LayerNetwork, short priority = 0, ulong extraFlags = 0)
    {
        OpenEx(filter, openParams, layer, priority, WinDivertNative.FlagSniff | extraFlags);
    }

    /// <summary>
    /// Recv-only modunda (WINDIVERT_FLAG_RECV_ONLY) OpenEx açar: paketler yığından
    /// çıkarılır ve YALNIZCA okunur — geri enjekte edilmez (WinDivertSend bu
    /// handle'da başarısız olur). GPN tüneli paketlerin yeni çıkış noktasıdır;
    /// yakalanan paketler şifrelenip tünel soketine verilir (Faz 2b).
    /// </summary>
    public void OpenRecvOnly(string? filter, in WinDivertOpenParams openParams, int layer = WinDivertNative.LayerNetwork, short priority = 0, ulong extraFlags = 0)
    {
        OpenEx(filter, openParams, layer, priority, WinDivertNative.FlagRecvOnly | extraFlags);
    }

    /// <summary>
    /// Düz (plain) divert modunda OpenEx açar: paketler yığından çıkarılıp
    /// kullanıcıya verilir AMA WinDivertSend etkin kalır — yakalanan ancak hedef
    /// sürece ait olmayan paketler aynı handle üzerinden tekrar yığına enjekte
    /// edilebilir (kullanıcı-modu PID ayırımı — NETWORK katmanı filtre içinde
    /// süreç tanımadığı için zorunludur, bkz. WinDivertFilterBuilder).
    /// Sniff/RecvOnly bayrağı taşımaz (flags = 0).
    /// </summary>
    public void OpenDivert(string? filter, in WinDivertOpenParams openParams, int layer = WinDivertNative.LayerNetwork, short priority = 0, ulong extraFlags = 0)
    {
        // Sniff/RecvOnly bayrağı taşımaz (flags = 0) — ancak kullanıcının seçtiği
        // OpenEx kuyruk bayrakları (QueueLength/QueueSize) AYNEN iletilir. Klasik
        // yolun ClassicFlagMask süzgeci burada YANLIŞ olurdu: OpenEx bitleri (0x400/
        // 0x1000) o maskeyle silinir ve kullanıcının kuyruk ayarı kaybolurdu.
        OpenEx(filter, openParams, layer, priority, extraFlags);
    }

    private void CommitHandle(IntPtr handle, string? filter, int nativeError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (IsOpen)
            {
                throw new InvalidOperationException("WinDivert handle'ı zaten açık.");
            }

            if (!IsSupportedPlatform)
            {
                throw new WinDivertException("WinDivert yalnızca Windows'ta çalışır.", Native.ERROR_PLATFORM_NOT_SUPPORTED);
            }

            if (handle == IntPtr.Zero || handle == InvalidHandle)
            {
                // nativeError, başarısız çağrının HEMEN ardından yakalanmış gerçek
                // Win32 kodudur — burada GetLastError okumak bayat değer verir.
                throw new WinDivertException(MissingDriverHint(nativeError), nativeError);
            }

            _handle = handle;
            Filter = filter;
        }
    }

    /// <summary>
    /// Paketi seçilen (yakalanan) arayüzden enjekte eder. <paramref name="address"/>
    /// WinDivertRecv'dan gelen değerin aynısı olmalı (yön/arayüz kimliği korunur).
    /// </summary>
    public void Send(ReadOnlySpan<byte> packet, in WinDivertAddress address)
    {
        var (handle, _) = RequireOpen();
        if (packet.Length == 0)
        {
            return;
        }

        // Unsafe blok olmadan güvenli yönetilen → natif kopyalama.
        var buffer = Marshal.AllocHGlobal(packet.Length);
        try
        {
            Marshal.Copy(packet.ToArray(), 0, buffer, packet.Length);
            var addrCopy = address;
            if (!_api.Send(handle, buffer, packet.Length, out _, ref addrCopy))
            {
                throw new WinDivertException($"WinDivertSend başarısız ({packet.Length}B paket)", _api.GetLastError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Handle zaten açıksa bir yakalama çalışanı (DivertWorker) başlatır.
    /// Yakalanan paketler dönen çalışanın <see cref="DivertWorker.GetPacketsAsync"/>
    /// kanalından tüketilir (bir sonraki aşama: tünele enjeksiyon).
    /// </summary>
    public DivertWorker StartCapture(CancellationToken cancellationToken)
    {
        RequireOpen();
        lock (_gate)
        {
            if (_worker is { IsRunning: true })
            {
                return _worker;
            }
            _worker = new DivertWorker(_api, _handle, cancellationToken);
            _worker.Start();
            return _worker;
        }
    }

    /// <summary>Handle'ı kapatır: sürücü filtresini kaldırır, driver referansını serbest bırakır.</summary>
    public void Close()
    {
        lock (_gate)
        {
            var h = _handle;
            _handle = IntPtr.Zero;
            Filter = null;
            if (h != IntPtr.Zero && h != InvalidHandle)
            {
                _api.Close(h);
            }
        }
    }

    /// <summary>Aktif çalışanı durdurur ve handle'ı kapatır.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        DivertWorker? worker;
        lock (_gate)
        {
            _disposed = true;
            worker = _worker;
            _worker = null;
        }

        if (worker is not null)
        {
            await worker.StopAsync().ConfigureAwait(false);
        }
        Close();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // ── natif katman erişimi (DivertWorker okur) ─────────────────────────

    internal IWinDivertApi Api => _api;

    internal IntPtr Handle
    {
        get
        {
            lock (_gate)
            {
                return _handle;
            }
        }
    }

    private (IntPtr Handle, IWinDivertApi Api) RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("WinDivert handle'ı açık değil — önce Open(...) çağırın.");
            }
            return (_handle, _api);
        }
    }

    internal static string MissingDriverHint(int error)
    {
        return error switch
        {
            // WinDivert dokümantasyonundaki yaygın hata kodları:
            //   2  — WinDivert32.sys/WinDivert64.sys bulunamadı
            //   126 — WinDivert.dll veya bağımlılığı yüklenemedi (ör. MSVCRT)
            //   5  — yönetici yetkisi yok (sürücü ilk Open'ta sessizce kurulur)
            //   577 — driver imzası geçersiz (eski/engellenmiş build)
            //   1275— güvenlik yazılımı veya sanallaştırma ortamı engelliyor
            //   1753— Windows Temel Filtreleme Altyapısı (BFE) hizmeti kapalı
            Native.ERROR_FILE_NOT_FOUND => "WinDivert.dll bulunamadı — sürücü dosyaları (WinDivert.dll + WinDivert64.sys) uygulama klasöründe olmalı.",
            Native.ERROR_MODULE_NOT_FOUND => "WinDivert.dll bulunamadı veya bağımlılığı yüklenemedi — uygulama klasöründe WinDivert.dll + WinDivert64.sys olmalı.",
            Native.ERROR_ACCESS_DENIED => "WinDivert sürücüsü kurulamadı — uygulama yönetici olarak çalışmalı (sürücü ilk WinDivertOpen'ta otomatik kurulur).",
            Native.ERROR_INVALID_IMAGE_HASH => "WinDivert64.sys geçerli imzaya sahip değil — güncel WinDivert 2.2.2 dağıtımı gerekir.",
            Native.ERROR_DRIVER_BLOCKED => "WinDivert sürücüsü güvenlik yazılımı veya sanallaştırma ortamı tarafından engellendi.",
            Native.ERROR_BFE_DISABLED => "WinDivert açılamadı — Windows Temel Filtreleme Altyapısı (BFE) hizmeti kapalı.",
            _ => $"WinDivertOpen başarısız",
        };
    }

    // WinDivert/Win32 yaygın hata kodları (tanıma yardımcı; WinDivertHealthMonitor da kullanır).
    internal static class Native
    {
        internal const int ERROR_PLATFORM_NOT_SUPPORTED = 120;  // ERROR_CALL_NOT_IMPLEMENTED
        internal const int ERROR_FILE_NOT_FOUND = 2;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_MODULE_NOT_FOUND = 126;        // DLL veya bağımlılığı yok
        internal const int ERROR_PROC_NOT_FOUND = 127;          // DLL sürümü uyumsuz (export yok)
        internal const int ERROR_INVALID_IMAGE_HASH = 577;      // driver imzası geçersiz
        internal const int ERROR_DRIVER_BLOCKED = 1275;         // güvenlik yazılımı/virtüel ortam
        internal const int ERROR_BFE_DISABLED = 1753;           // BFE hizmeti kapalı
    }
}