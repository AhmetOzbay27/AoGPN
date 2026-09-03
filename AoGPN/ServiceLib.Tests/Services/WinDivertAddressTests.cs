using System.Runtime.InteropServices;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// WinDivertAddress'in native WINDIVERT_ADDRESS (WinDivert 2.x, include/windivert.h)
/// ile birebir eşleştiğini doğrulayan layout testleri — gerçek sürücü gerekmez.
/// </summary>
public class WinDivertAddressTests
{
    // ── Boyut ve offsetler ───────────────────────────────────────────────

    [Fact]
    public void NativeSize_MatchesWindivert2xHeader_80Bytes()
    {
        WinDivertAddress.NativeSize.Should().Be(80);
        Marshal.SizeOf<WinDivertAddress>().Should().Be(80, "explicit layout Size=80 — x86/x64 aynı");
    }

    [Fact]
    public void FieldOffsets_MatchCHeader()
    {
        OffsetOf("Timestamp").Should().Be(0);
        OffsetOf("Flags").Should().Be(8, "UINT32 bitfield depolama (Layer/Event/bayraklar)");
        OffsetOf("Reserved2").Should().Be(12);
        OffsetOf("IfIdx").Should().Be(16, "union başlangıcı");
        OffsetOf("SubIfIdx").Should().Be(20);
        // FLOW görünümü
        OffsetOf("FlowEndpointId").Should().Be(16);
        OffsetOf("FlowProcessId").Should().Be(32);
        OffsetOf("FlowLocalPort").Should().Be(68);
        OffsetOf("FlowRemotePort").Should().Be(70);
        OffsetOf("FlowProtocol").Should().Be(72);
        // REFLECT görünümü
        OffsetOf("ReflectTimestamp").Should().Be(16);
        OffsetOf("ReflectFlags").Should().Be(32);
        OffsetOf("ReflectPriority").Should().Be(40);
    }

    [Fact]
    public void Union_NetworkAndFlowShareRegion()
    {
        var addr = new WinDivertAddress
        {
            IfIdx = 0x11223344,
            SubIfIdx = 0x55667788,
        };

        // FlowEndpointId aynı ofsette başlar — düşük 32 bit IfIdx, yüksek 32 bit SubIfIdx.
        addr.FlowEndpointId.Should().Be(0x5566778811223344UL, "union: Network.IfIdx/SubIfIdx == Flow.EndpointId ilk 8 bayt");
    }

    // ── Bitfield paketleme (MSVC sırası, LSB'den) ────────────────────────

    [Fact]
    public void FlagsBitfield_PropertiesPack_IntoExactUintLayout()
    {
        var addr = new WinDivertAddress
        {
            Layer = 1,                       // bit 0-7  = 0x01
            Event = 2,                       // bit 8-15 = 0x02 << 8
            Sniffed = true,                  // bit 16
            Direction = 1,                   // bit 17
            Loopback = true,                 // bit 18
            Impostor = true,                 // bit 19
            Ipv6 = true,                     // bit 20
            IpChecksum = true,               // bit 21
            TcpChecksum = true,              // bit 22
            UdpChecksum = true,              // bit 23
        };

        var expected = 0x00000001u          // Layer
                       | (0x02u << 8)       // Event
                       | WinDivertNative.AddressSniffed
                       | WinDivertNative.AddressOutbound
                       | WinDivertNative.AddressLoopback
                       | WinDivertNative.AddressImpostor
                       | WinDivertNative.AddressIpv6
                       | WinDivertNative.AddressIpChecksum
                       | WinDivertNative.AddressTcpChecksum
                       | WinDivertNative.AddressUdpChecksum;

        addr.Flags.Should().Be(expected);
        expected.Should().Be(0x00FF0201u, "sabit bit deseni: 23..16 bayrak bitleri + Event(2) + Layer(1)");
    }

    [Fact]
    public void FlagsBitfield_RawFlags_DecodeThroughProperties()
    {
        // 0x00FF0201: Layer=1, Event=2, Sniffed..UdpChecksum hepsi 1 (bit 16-23), Reserved1 (24-31)=0xFF
        var addr = new WinDivertAddress { Flags = 0x00FF0201u };

        addr.Layer.Should().Be(1);
        addr.Event.Should().Be(2);
        addr.Sniffed.Should().BeTrue();
        addr.Direction.Should().Be(1);
        addr.IsOutbound.Should().BeTrue();
        addr.IsInbound.Should().BeFalse();
        addr.Loopback.Should().BeTrue();
        addr.Impostor.Should().BeTrue();
        addr.Ipv6.Should().BeTrue();
        addr.IpChecksum.Should().BeTrue();
        addr.TcpChecksum.Should().BeTrue();
        addr.UdpChecksum.Should().BeTrue();

        // Reserved1 bitleri (24-31) özellikleri etkilemez — Layer/Event maskeleri dışında.
        addr.Layer.Should().Be(1, "Reserved1 Flags içinde kalsa da Layer maskesi 0xFF'dir");
    }

    [Fact]
    public void Direction_SetAndClear_IsSingleBit()
    {
        var addr = new WinDivertAddress { Direction = 1 };
        addr.Flags.Should().Be(WinDivertNative.AddressOutbound);
        addr.Direction.Should().Be(1);
        addr.IsOutbound.Should().BeTrue();

        addr.Direction = 0;
        addr.Flags.Should().Be(0);
        addr.IsInbound.Should().BeTrue();
    }

    [Fact]
    public void Timestamp_RoundTrips()
    {
        var addr = new WinDivertAddress { Timestamp = 0x0102030405060708L };
        addr.Timestamp.Should().Be(0x0102030405060708L);
    }

    // ── Byte-byte marshal golden (C layout'una birebir) ──────────────────

    [Fact]
    public void Marshal_ProducesExactWindivert2xByteLayout()
    {
        var addr = new WinDivertAddress
        {
            Timestamp = 0x0102030405060708L,
            Flags = 0x80000001u,             // Layer=1 + UDPChecksum (bit 23)
            Reserved2 = 0x0A0B0C0Du,
            IfIdx = 0x11223344,
            SubIfIdx = 0x55667788,
        };

        var actual = new byte[WinDivertAddress.NativeSize];
        var ptr = Marshal.AllocHGlobal(actual.Length);
        try
        {
            Marshal.StructureToPtr(addr, ptr, false);
            Marshal.Copy(ptr, actual, 0, actual.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        // Beklenen: alan değerleri FieldOffset'larına, union kalanı sıfır.
        var expected = new byte[WinDivertAddress.NativeSize];
        BitConverter.GetBytes(0x0102030405060708L).CopyTo(expected, 0);  // Timestamp
        BitConverter.GetBytes(0x80000001u).CopyTo(expected, 8);          // Flags
        BitConverter.GetBytes(0x0A0B0C0Du).CopyTo(expected, 12);         // Reserved2
        BitConverter.GetBytes(0x11223344u).CopyTo(expected, 16);         // IfIdx
        BitConverter.GetBytes(0x55667788u).CopyTo(expected, 20);         // SubIfIdx
        // 24..79 sıfır (union kalanı + NetworkForward alanı yok)

        actual.Should().Equal(expected);
    }

    [Fact]
    public void Default_IsAllZero()
    {
        var addr = default(WinDivertAddress);

        addr.Timestamp.Should().Be(0);
        addr.Flags.Should().Be(0);
        addr.IfIdx.Should().Be(0);
        addr.SubIfIdx.Should().Be(0);
        addr.IsInbound.Should().BeTrue();
        addr.Layer.Should().Be(0);
    }

    private static int OffsetOf(string fieldName)
        => Marshal.OffsetOf<WinDivertAddress>(fieldName).ToInt32();
}
