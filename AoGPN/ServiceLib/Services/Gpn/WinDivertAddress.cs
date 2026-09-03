using System.Runtime.InteropServices;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WinDivertAddress — WINDIVERT_ADDRESS'in yönetilen eşdeğeri (WinDivert 2.x)
//
// WinDivertRecv bir paketle birlikte hedefin zaman damgasını, yönünü, katmanını
// ve ağ arayüzünü doldurur; WinDivertSend'e AYNI struct geri verilerek paket
// aynı arayüzden enjekte edilir. Layout, resmi include/windivert.h ile birebir
// eşleşmeli:
//
//   INT64  Timestamp;                       offset  0   (QPC tabanlı)
//   UINT32 Layer:8, Event:8, Sniffed:1, Outbound:1, Loopback:1, Impostor:1,
//          IPv6:1, IPChecksum:1, TCPChecksum:1, UDPChecksum:1, Reserved1:8
//                                           offset  8   (tek UINT32 bitfield depolama)
//   UINT32 Reserved2;                       offset 12
//   union { Network | Flow | Socket | Reflect | Reserved3[64] }
//                                           offset 16   (64 bayt)
//
// Toplam 80 bayt (x86 ve x64 aynı). MSVC bitfield'ları depolama biriminde
// LSB'den başlar: Layer = bit 0-7, Event = bit 8-15, Sniffed = bit 16,
// Outbound = bit 17, Loopback = bit 18, Impostor = bit 19, IPv6 = bit 20,
// IPChecksum = bit 21, TCPChecksum = bit 22, UDPChecksum = bit 23,
// Reserved1 = bit 24-31.
//
// Bit alanlarına C# tarafında erişim için kolaylık özellikleri (Direction,
// Layer, Event, Sniffed, ...) Flags UINT32'sini okuyup yazar; ham Flags alanı
// da doğrudan erişilebilir (native ile byte'lar birebir).
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// WinDivert ağ adresi/yönü + katman/olay bayrakları + union verisi
/// (native WINDIVERT_ADDRESS, 80 bayt).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = NativeSize)]
public unsafe struct WinDivertAddress
{
    /// <summary>Natif WINDIVERT_ADDRESS boyutu (8 + 4 + 4 + 64).</summary>
    public const int NativeSize = 80;

    // ── Başlık ───────────────────────────────────────────────────────────

    /// <summary>Paket zaman damgası (QueryPerformanceCounter ile aynı saat).</summary>
    [FieldOffset(0)] public long Timestamp;

    /// <summary>
    /// Bitfield depolaması: Layer (bit 0-7), Event (bit 8-15), Sniffed (16),
    /// Outbound (17), Loopback (18), Impostor (19), IPv6 (20), IPChecksum (21),
    /// TCPChecksum (22), UDPChecksum (23), Reserved1 (24-31).
    /// Doğrudan okumak/yazmak için <see cref="WinDivertNative"/> adres maskeleri.
    /// </summary>
    [FieldOffset(8)] public uint Flags;

    /// <summary>Reserved (her zaman sıfır).</summary>
    [FieldOffset(12)] public uint Reserved2;

    // ── Union (offset 16, 64 bayt) — NETWORK görünümü ────────────────────

    /// <summary>Paketin arayüz indeksi (inbound için anlamlı; outbound'da yok sayılır).</summary>
    [FieldOffset(16)] public uint IfIdx;

    /// <summary>Paketin alt-arayüz indeksi.</summary>
    [FieldOffset(20)] public uint SubIfIdx;

    // ── Union — FLOW görünümü (WINDIVERT_DATA_FLOW) ──────────────────────

    [FieldOffset(16)] public ulong FlowEndpointId;
    [FieldOffset(24)] public ulong FlowParentEndpointId;
    [FieldOffset(32)] public uint FlowProcessId;
    [FieldOffset(36)] public uint FlowLocalAddr0;
    [FieldOffset(40)] public uint FlowLocalAddr1;
    [FieldOffset(44)] public uint FlowLocalAddr2;
    [FieldOffset(48)] public uint FlowLocalAddr3;
    [FieldOffset(52)] public uint FlowRemoteAddr0;
    [FieldOffset(56)] public uint FlowRemoteAddr1;
    [FieldOffset(60)] public uint FlowRemoteAddr2;
    [FieldOffset(64)] public uint FlowRemoteAddr3;
    [FieldOffset(68)] public ushort FlowLocalPort;
    [FieldOffset(70)] public ushort FlowRemotePort;
    [FieldOffset(72)] public byte FlowProtocol;

    // ── Union — SOCKET görünümü (WINDIVERT_DATA_SOCKET, FLOW ile aynı biçim) ──

    [FieldOffset(16)] public ulong SocketEndpointId;
    [FieldOffset(24)] public ulong SocketParentEndpointId;
    [FieldOffset(32)] public uint SocketProcessId;
    [FieldOffset(36)] public uint SocketLocalAddr0;
    [FieldOffset(40)] public uint SocketLocalAddr1;
    [FieldOffset(44)] public uint SocketLocalAddr2;
    [FieldOffset(48)] public uint SocketLocalAddr3;
    [FieldOffset(52)] public uint SocketRemoteAddr0;
    [FieldOffset(56)] public uint SocketRemoteAddr1;
    [FieldOffset(60)] public uint SocketRemoteAddr2;
    [FieldOffset(64)] public uint SocketRemoteAddr3;
    [FieldOffset(68)] public ushort SocketLocalPort;
    [FieldOffset(70)] public ushort SocketRemotePort;
    [FieldOffset(72)] public byte SocketProtocol;

    // ── Union — REFLECT görünümü (WINDIVERT_DATA_REFLECT) ────────────────

    [FieldOffset(16)] public long ReflectTimestamp;
    [FieldOffset(24)] public uint ReflectProcessId;
    [FieldOffset(28)] public uint ReflectLayer;
    [FieldOffset(32)] public ulong ReflectFlags;
    [FieldOffset(40)] public short ReflectPriority;

    // ── Union — ham bayt görünümü (union genişliği 64 bayt) ───────────────

    [FieldOffset(16)] public fixed byte Reserved3[64];

    // ── Bitfield kolaylık özellikleri (Flags üzerinden) ──────────────────

    /// <summary>Paket katmanı (WINDIVERT_LAYER_*, bit 0-7).</summary>
    public byte Layer
    {
        readonly get => (byte)(Flags & WinDivertNative.AddressLayerMask);
        set => Flags = (Flags & ~WinDivertNative.AddressLayerMask) | (uint)(value & 0xFF);
    }

    /// <summary>Katman olayı (WINDIVERT_EVENT_*, bit 8-15).</summary>
    public byte Event
    {
        readonly get => (byte)((Flags >> 8) & 0xFF);
        set => Flags = (Flags & ~WinDivertNative.AddressEventMask) | ((uint)(value & 0xFF) << 8);
    }

    /// <summary>0 = inbound, 1 = outbound (bit 17).</summary>
    public byte Direction
    {
        readonly get => (byte)((Flags >> 17) & 1);
        set => Flags = (Flags & ~WinDivertNative.AddressOutbound) | ((uint)(value & 1) << 17);
    }

    /// <summary>Paket sniff mi (kopyalanıp bırakıldı mı), bit 16.</summary>
    public bool Sniffed
    {
        readonly get => (Flags & WinDivertNative.AddressSniffed) != 0;
        set => Flags = value ? Flags | WinDivertNative.AddressSniffed : Flags & ~WinDivertNative.AddressSniffed;
    }

    /// <summary>Paket loopback mi, bit 18. Windows loopback'i yalnızca outbound yakalar.</summary>
    public bool Loopback
    {
        readonly get => (Flags & WinDivertNative.AddressLoopback) != 0;
        set => Flags = value ? Flags | WinDivertNative.AddressLoopback : Flags & ~WinDivertNative.AddressLoopback;
    }

    /// <summary>Impostor paket (başka sürücü tarafından enjekte edilmiş), bit 19.</summary>
    public bool Impostor
    {
        readonly get => (Flags & WinDivertNative.AddressImpostor) != 0;
        set => Flags = value ? Flags | WinDivertNative.AddressImpostor : Flags & ~WinDivertNative.AddressImpostor;
    }

    /// <summary>Paket IPv6 mı (IPv4 için temiz), bit 20.</summary>
    public bool Ipv6
    {
        readonly get => (Flags & WinDivertNative.AddressIpv6) != 0;
        set => Flags = value ? Flags | WinDivertNative.AddressIpv6 : Flags & ~WinDivertNative.AddressIpv6;
    }

    /// <summary>IPv4 checksum geçerli mi, bit 21.</summary>
    public bool IpChecksum
    {
        readonly get => (Flags & WinDivertNative.AddressIpChecksum) != 0;
        set => Flags = value ? Flags | WinDivertNative.AddressIpChecksum : Flags & ~WinDivertNative.AddressIpChecksum;
    }

    /// <summary>TCP checksum geçerli mi, bit 22.</summary>
    public bool TcpChecksum
    {
        readonly get => (Flags & WinDivertNative.AddressTcpChecksum) != 0;
        set => Flags = value ? Flags | WinDivertNative.AddressTcpChecksum : Flags & ~WinDivertNative.AddressTcpChecksum;
    }

    /// <summary>UDP checksum geçerli mi, bit 23.</summary>
    public bool UdpChecksum
    {
        readonly get => (Flags & WinDivertNative.AddressUdpChecksum) != 0;
        set => Flags = value ? Flags | WinDivertNative.AddressUdpChecksum : Flags & ~WinDivertNative.AddressUdpChecksum;
    }

    public readonly bool IsInbound => Direction == 0;
    public readonly bool IsOutbound => Direction == 1;

    /// <summary>Bu struct'ın yönetilen tarafındaki bayt boyutu (native ile aynı: 80).</summary>
    public static int Size => Marshal.SizeOf<WinDivertAddress>();
}

/// <summary>
/// WinDivert tarafından yakalanmış tek paket. <see cref="Data"/> genişletilmiş
/// AES-GCM sonrası tünelin içine enjekte edilmek üzere tüketiciye teslim edilir;
/// <see cref="Address"/> enjeksiyonda WinDivertSend'e aynen geri verilir.
/// </summary>
public sealed record DivertedPacket(byte[] Data, WinDivertAddress Address, DateTimeOffset CapturedAt);
