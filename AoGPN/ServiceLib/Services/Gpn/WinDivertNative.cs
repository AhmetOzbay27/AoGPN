using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WinDivertNative — WinDivert.dll P/Invoke bağlayıcıları + sabitler
//
// NOT (kritik): bu katman artık modern .NET source-generated P/Invoke
// ([LibraryImport]) kullanır — çalışma zamanı ILMarshaller yerine derleme
// zamanında türetilen, dengesiz (Marshaller spike'ı yok) ve hızlı natif
// çağrı dizileri üretir. Eski [DllImport] kodundan farklı noktalar:
//
//   * Sınıf ve metodlar `partial` olmalıdır (generator kod üretir).
//   * CallingConvention DllImport ctor'da değil [UnmanagedCallConv] ile
//     verilir. WinDivert __cdecl => CallConvCdecl.
//   * ANSI (LPStr) string'ler için StringMarshalling.Custom +
//     AnsiStringMarshaller gerekir (varsayılan UTF-8, WinDivert'i bozar).
//   * SetLastError=true desteklenir; hata okuma aynıdır
//     (Marshal.GetLastPInvokeError — GetLastWin32Error ile eşdeğer).
//
// Yönetilen wrapper (WinDivertEngine) bu natif katmanın üzerine oturur;
// testler IWinDivertApi ile natif katmanı hiç çağırmadan çalışır.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class WinDivertNative
{
    internal const string Library = "WinDivert.dll";

    /// <summary>WINDIVERT_LAYER_NETWORK — ağ katmanı (V4/V6, in/out).</summary>
    internal const int LayerNetwork = 0;
    internal const int LayerNetworkForward = 1;

    // WINDIVERT_PARAM_* (WinDivertSetParam/GetParam)
    internal const int QueueLen = 0;
    internal const int QueueTime = 1;
    internal const int QueueSize = 2;

    // WINDIVERT_LAYER_NETWORK adresindeki yön baytları
    // (WinDivertAddress.Direction özelliğine verilen değerler — bit 17).
    internal const byte DirectionInbound = 0;
    internal const byte DirectionOutbound = 1;

    // ── WINDIVERT_ADDRESS.Flags bitfield bit konumları (windivert.h, LSB'den) ──
    internal const uint AddressLayerMask = 0x000000FFu;   // bit 0-7  Layer
    internal const uint AddressEventMask = 0x0000FF00u;   // bit 8-15 Event
    internal const uint AddressSniffed = 0x00010000u;     // bit 16
    internal const uint AddressOutbound = 0x00020000u;    // bit 17
    internal const uint AddressLoopback = 0x00040000u;    // bit 18
    internal const uint AddressImpostor = 0x00080000u;    // bit 19
    internal const uint AddressIpv6 = 0x00100000u;        // bit 20
    internal const uint AddressIpChecksum = 0x00200000u;  // bit 21
    internal const uint AddressTcpChecksum = 0x00400000u; // bit 22
    internal const uint AddressUdpChecksum = 0x00800000u; // bit 23

    // WINDIVERT_OPEN_PARAMS_VERSION_0 — current bölüm için sürüm alanı.
    internal const int OpenParamsVersion0 = 0;

    // ── WINDIVERT_FLAG_* — WinDivertOpen/OpenEx flags bitleri ────────────
    // Değerler RESMÎ WinDivert 2.2.2 dağıtımının (basil00/WinDivert,
    // dağıtılan ikili) windivert.h'inden birebir. Klasik WinDivertOpen yalnızca
    // bu bitleri tanır — tanımadığı bir bit ERROR_INVALID_PARAMETER (87) üretir.
    internal const ulong FlagSniff = 0x0001;             // paketleri çak, bırakma
    internal const ulong FlagDrop = 0x0002;              // paketleri düşür
    internal const ulong FlagRecvOnly = 0x0004;          // yalnızca recv
    internal const ulong FlagSendOnly = 0x0008;          // yalnızca send
    internal const ulong FlagNoInstall = 0x0010;         // sürücüyü kurma (kuruluysa aç)
    internal const ulong FlagFragments = 0x0020;         // fragmanları da yakala

    // Resmî 2.2.2'de BULUNMAYAN, OpenEx'li (fork biçimli) sürümlere özgü bitler.
    // Klasik WinDivertOpen yolunda MASKELENMELİDİR (WinDivertEngine): bilinmeyen
    // bitler hata 87 döndürür. Kuyruk parametreleri klasik yolda WinDivertSetParam
    // ile uygulanır — bu bitler yalnızca OpenEx sürümünde anlamlıdır.
    internal const ulong FlagUseSequenceNumbers = 0x0080; // seq num doğrulaması
    internal const ulong FlagQueueLength = 0x0400;       // QueueLen kullan
    internal const ulong FlagQueueTime = 0x0800;         // QueueTime kullan
    internal const ulong FlagQueueSize = 0x1000;         // QueueSize kullan

    /// <summary>Resmî 2.2.2 WinDivertOpen'un tanıdığı bit kümesi — klasik yola giren flags her zaman bu maske ile süzülür.</summary>
    internal const ulong ClassicFlagMask = FlagSniff | FlagDrop | FlagRecvOnly | FlagSendOnly | FlagNoInstall | FlagFragments;

    // ── WinDivertOpen/OpenEx (cdecl, ANSI filter) ────────────────────────

    [LibraryImport(Library, EntryPoint = "WinDivertOpen", SetLastError = true,
        StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(AnsiStringMarshaller))]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr WinDivertOpen(
        string? filter,
        int layer,
        short priority,
        ulong flags);

    /// <summary>
    /// WinDivertOpenEx: classic çağrıya ek olarak <paramref name="openParams"/> ile
    /// katman/yön bazlı kuyruk parametreleri (QueueLen/Time/Size[4]) verilir.
    ///
    /// NOT: resmî WinDivert 2.2.2 dağıtımı bu export'u İÇERMEZ — WinDivertEngine
    /// export yoksa klasik WinDivertOpen'a düşer (kuyruk ayarları WinDivertSetParam
    /// ile uygulanır, fork/OpenEx'e özgü bitler maskelenir).
    /// </summary>
    [LibraryImport(Library, EntryPoint = "WinDivertOpenEx", SetLastError = true,
        StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(AnsiStringMarshaller))]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr WinDivertOpenEx(
        string? filter,
        int layer,
        short priority,
        ulong flags,
        in WinDivertOpenParams openParams,
        uint openParamsLength);

    // ── Paket alma/gönderme / kuyruk / kapat (cdecl) ──────────────────────

    [LibraryImport(Library, EntryPoint = "WinDivertRecv", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinDivertRecv(
        IntPtr handle,
        IntPtr packet,
        int packetLen,
        out int pRecvLen,
        ref WinDivertAddress pAddr);

    [LibraryImport(Library, EntryPoint = "WinDivertSend", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinDivertSend(
        IntPtr handle,
        IntPtr packet,
        int packetLen,
        out int pSendLen,
        ref WinDivertAddress pAddr);

    [LibraryImport(Library, EntryPoint = "WinDivertClose", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinDivertClose(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "WinDivertSetParam", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int WinDivertSetParam(IntPtr handle, int param, ulong value);

    [LibraryImport(Library, EntryPoint = "WinDivertGetParam")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int WinDivertGetParam(IntPtr handle, int param, out ulong value);

    /// <summary>Mevcut iş parçacığı için son Win32 hata kodunu döndürür.</summary>
    internal static int LastWin32Error => Marshal.GetLastPInvokeError();
}

// ─────────────────────────────────────────────────────────────────────────
// WinDivertOpenParams — WINDIVERT_OPEN_PARAMS (WinDivertOpenEx için)
//
// Tüm diziler katman*direction sırasıyla (0..3) indeksli 4 eleman taşır.
//   indeks = (layer * 2) + direction
//   layer 0 = NETWORK, layer 1 = NETWORK_FORWARD; direction 0 = in, 1 = out
// Natif boyut: UINT16+UINT8+UINT8 = 4 başlık + 3×4×UINT32 = 48 → 52 bayt.
// ─────────────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential)]
public struct WinDivertOpenParams
{
    /// <summary>WINDIVERT_OPEN_PARAMS_VERSION_0 (0) olmalı.</summary>
    public ushort Version;

    /// <summary>1 ise Queue* alanları yalnızca current layer için geçerlidir.</summary>
    public byte CurrentLayer;

    /// <summary>1 ise Queue* alanları yalnızca current direction için geçerlidir.</summary>
    public byte CurrentDirection;

#pragma warning disable CS1591 // açıklanmamış alanlar — slot anlamı ForSher yorumda
    public uint QueueLen0, QueueLen1, QueueLen2, QueueLen3;
    public uint QueueTime0, QueueTime1, QueueTime2, QueueTime3;
    public uint QueueSize0, QueueSize1, QueueSize2, QueueSize3;
#pragma warning restore CS1591

    /// <summary>Natif WINDIVERT_OPEN_PARAMS boyutu (openParamsLength için).</summary>
    public static int NativeSize => Marshal.SizeOf<WinDivertOpenParams>();

    /// <summary>Varsayılan (tümü 0) parametre bloğu — klasik açılışa denk.</summary>
    public static WinDivertOpenParams Default => new() { Version = WinDivertNative.OpenParamsVersion0 };

    /// <summary>Belirli (layer, direction) slotunun QueueLen değerini yazar. Slot 0..3 dışına taşar.</summary>
    public void SetQueueLen(int slot, uint value)
    {
        switch (slot)
        {
            case 0: QueueLen0 = value; break;
            case 1: QueueLen1 = value; break;
            case 2: QueueLen2 = value; break;
            default: QueueLen3 = value; break;
        }
    }

    /// <summary>Belirli (layer, direction) slotunun QueueTime değerini yazar. Slot 0..3 dışına taşar.</summary>
    public void SetQueueTime(int slot, uint value)
    {
        switch (slot)
        {
            case 0: QueueTime0 = value; break;
            case 1: QueueTime1 = value; break;
            case 2: QueueTime2 = value; break;
            default: QueueTime3 = value; break;
        }
    }

    /// <summary>Belirli (layer, direction) slotunun QueueSize değerini yazar. Slot 0..3 dışına taşar.</summary>
    public void SetQueueSize(int slot, uint value)
    {
        switch (slot)
        {
            case 0: QueueSize0 = value; break;
            case 1: QueueSize1 = value; break;
            case 2: QueueSize2 = value; break;
            default: QueueSize3 = value; break;
        }
    }
}