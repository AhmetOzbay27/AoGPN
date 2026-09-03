using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WintunNative — wintun.dll P/Invoke bağlayıcıları + sabitler
//
// Wintun, WireGuard ekosisteminin layer-3 TUN sürücüsüdür (winipip'dan farklı
// olarak yalnızca saf IP paketleri taşır — ethernet başlığı yok). API resmi
// api/wintun.h ile birebir: adapter oluştur → session (halka tampon) başlat →
// WintunAllocateSendPacket/WintunSendPacket ile gönder, WintunReceivePacket/
// WintunReleaseReceivePacket ile al. Tüm fonksiyonlar WINAPI (stdcall) ve
// LibraryImport source-generated P/Invoke ile bağlanır (WinDivertNative ile
// aynı desen).
//
// Gönderim sırası: AllocateSendPacket çağrı SIRASI paket gönderim sırasını
// belirler (SendPacket'in kendisi sıra garantisi vermez). Alımda ERROR_NO_MORE_ITEMS
// → WintunGetReadWaitEvent ile bekle (burada token denetimli kısa uyku yeterli).
//
// ÖNEMLİ: wintun.dll, WinDivert.dll gibi çalışma zamanı bağımlılığıdır — repo'da
// yok, uygulamanın yanına kopyalanır. İlk natif çağrıda DLL yoksa DllNotFoundException
// fırlar; WireGuardTunnelService bunu dostça WintunException'a çevirir.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class WintunNative
{
    internal const string Library = "wintun.dll";

    // Wintun ring kapasite sınırları (api/wintun.h)
    internal const uint MinRingCapacity = 0x20000;   // 128 KiB
    internal const uint MaxRingCapacity = 0x4000000; // 64 MiB
    internal const uint MaxIpPacketSize = 0xFFFF;

    // GetLastError kodları
    internal const int ErrorNoMoreItems = 259;        // ERROR_NO_MORE_ITEMS — alım tamponu boş
    internal const int ErrorBufferOverflow = 111;     // ERROR_BUFFER_OVERFLOW — gönderim tamponu dolu
    internal const int ErrorHandleEof = 38;           // ERROR_HANDLE_EOF — adapter kapatılıyor

    // ── Adapter yaşam döngüsü ────────────────────────────────────────────

    /// <summary>
    /// Yeni Wintun adapter oluşturur (sürücü yoksa otomatik kurar — yönetici
    /// gerekir). Başarısızlıkta IntPtr.Zero; WintunCloseAdapter ile serbest bırakılır.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "WintunCreateAdapter", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial IntPtr WintunCreateAdapter(string name, string tunnelType, IntPtr requestedGuid);

    /// <summary>Mevcut bir Wintun adapter'ı açar (oluşturulan adaptörü kapatmakla sürücüden kaldırır).</summary>
    [LibraryImport(Library, EntryPoint = "WintunOpenAdapter", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial IntPtr WintunOpenAdapter(string name);

    /// <summary>Adapter kaynaklarını serbest bırakır (oluşturulan adaptörü siler).</summary>
    [LibraryImport(Library, EntryPoint = "WintunCloseAdapter")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial void WintunCloseAdapter(IntPtr adapter);

    /// <summary>Adapter'ın LUID'ini doldurur (NET_LUID — 64 bit, ağ yönetiminde tanımlayıcı).</summary>
    // DİKKAT: export adı büyük harfle "WintunGetAdapterLUID" (wintun.h'deki gibi) —
    // GetProcAddress büyük/küçük harfe duyarlıdır; küçük harfli "WintunGetAdapterLuid"
    // araması EntryPointNotFoundException verir ve köprü her oturumda skipped kalırdı.
    [LibraryImport(Library, EntryPoint = "WintunGetAdapterLUID")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial void WintunGetAdapterLuid(IntPtr adapter, out long luid);

    /// <summary>Yüklü Wintun sürücüsünün sürüm numarası (0 = sürücü yok).</summary>
    [LibraryImport(Library, EntryPoint = "WintunGetRunningDriverVersion", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial uint WintunGetRunningDriverVersion();

    // ── Session (halka tampon) ───────────────────────────────────────────

    /// <summary>
    /// Session başlatır. Capacity Min..Max ring arasında ve 2'nin katı olmalı.
    /// Başarısızlıkta IntPtr.Zero; WintunEndSession ile serbest bırakılır.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "WintunStartSession", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial IntPtr WintunStartSession(IntPtr adapter, uint capacity);

    /// <summary>Session'ı sonlandırır (halka tamponları serbest bırakır).</summary>
    [LibraryImport(Library, EntryPoint = "WintunEndSession")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial void WintunEndSession(IntPtr session);

    /// <summary>Alım için beklenecek olay tutamacı (session'a aittir — CloseHandle YAPILMAZ).</summary>
    [LibraryImport(Library, EntryPoint = "WintunGetReadWaitEvent")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial IntPtr WintunGetReadWaitEvent(IntPtr session);

    // ── Paket alımı ──────────────────────────────────────────────────────

    /// <summary>
    /// Session'dan bir paket alır; PacketSize'a boyut, dönüşe paket işaretçisi yazılır.
    /// Başarısızlıkta IntPtr.Zero (ERROR_NO_MORE_ITEMS = tampon boş, ERROR_HANDLE_EOF = kapanıyor).
    /// Alınan tampon WintunReleaseReceivePacket ile serbest bırakılmalıdır.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "WintunReceivePacket", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

    /// <summary>Alınan paket tamponunu serbest bırakır.</summary>
    [LibraryImport(Library, EntryPoint = "WintunReleaseReceivePacket")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

    // ── Paket gönderimi ──────────────────────────────────────────────────

    /// <summary>
    /// Gönderilecek paket için halka tamponda yer ayırır (tam PacketSize).
    /// Dönüş işaretçisine veri yazıldıktan sonra WintunSendPacket çağrılmalıdır.
    /// Başarısızlıkta IntPtr.Zero (ERROR_BUFFER_OVERFLOW = tampon dolu).
    /// </summary>
    [LibraryImport(Library, EntryPoint = "WintunAllocateSendPacket", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

    /// <summary>AllocateSendPacket ile ayrılan paketi gönderir ve tamponu serbest bırakır.</summary>
    [LibraryImport(Library, EntryPoint = "WintunSendPacket")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static partial void WintunSendPacket(IntPtr session, IntPtr packet);

    /// <summary>Mevcut iş parçacığı için son Win32 hata kodunu döndürür.</summary>
    internal static int LastWin32Error => Marshal.GetLastPInvokeError();
}

// ─────────────────────────────────────────────────────────────────────────
// WintunException — wintun.dll/sürücü yoksa, yönetici yoksa vb. dostça hata
// ─────────────────────────────────────────────────────────────────────────
public sealed class WintunException : Exception
{
    public WintunException(string message, int nativeError)
        : base($"{message} (Win32 hata kodu: {nativeError})")
    {
        NativeError = nativeError;
    }

    public WintunException(string message, int nativeError, Exception inner)
        : base($"{message} (Win32 hata kodu: {nativeError})", inner)
    {
        NativeError = nativeError;
    }

    /// <summary>Son başarısız Win32 çağrısının hata kodu.</summary>
    public int NativeError { get; }
}
