using System.Runtime.InteropServices;
using AwesomeAssertions;
using ServiceLib.Models.Configs;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnWintunSettings — Wintun adaptörü ayarları (adapter ad ön eki + halka
/// tampon kapasitesi) testleri. Saf eşleyici/patch: sürücü yok, yalnızca
/// sınırlama/sanitleştirme/2'nin katına yuvarlama + köprü açılışına taşınma.
/// </summary>
public class GpnWintunSettingsTests
{
    // ── Eşleyici ─────────────────────────────────────────────────────────

    [Fact]
    public void ToOptions_Defaults_MatchOfficialExample()
    {
        var options = GpnWintunSettingsMapper.ToOptions(new GpnWintunItem());

        options.AdapterName.Should().Be("AoGPN");
        options.RingCapacity.Should().Be(0x400000, "resmi wintun örneğiyle aynı (4 MiB)");
    }

    [Fact]
    public void ToOptions_CustomValues_PassedThroughNormalized()
    {
        var options = GpnWintunSettingsMapper.ToOptions(new GpnWintunItem
        {
            AdapterName = "MyTunnel",
            RingCapacity = 0x200000,
        });

        options.AdapterName.Should().Be("MyTunnel");
        options.RingCapacity.Should().Be(0x200000);
    }

    [Theory]
    [InlineData("Ao GPN!", "AoGPN")]           // boşluk + noktalama atılır
    [InlineData("tünel-123", "tnel-123")]      // Türkçe karakterler atılır
    [InlineData("", "AoGPN")]                  // boş → varsayılan
    [InlineData("  ", "AoGPN")]                // boşluk → varsayılan
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789ABCDEF", "abcdefghijklmnopqrstuvwxyz012345")] // 42 → ilk 32
    public void SanitizeAdapterName_StripsUnsafeChars(string input, string expected)
    {
        GpnWintunSettingsMapper.SanitizeAdapterName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("AoGPN", "it", "AoGPN-it")]                          // kısa sunucu kodu — değişmez
    [InlineData("AoGPN", "de", "AoGPN-de")]                          // kısa sunucu kodu — değişmez
    [InlineData("AoGPN", "130.61.223.36:51820", "AoGPN-130612233651820")] // host:port iki noktası atılır
    [InlineData("AoGPN", "92.4.220.236:51820", "AoGPN-92422023651820")] // host:port iki noktası atılır
    public void SanitizeAdapterName_ComposedWithServerId_IsWintunSafe(string prefix, string serverId, string expected)
    {
        // GpnCaptureBridge, adaptör adını "{ön ek}-{ServerId}" olarak kurar. ServerId
        // host:port biçimindeyken (iki nokta içerir) WintunCreateAdapter bunu
        // ERROR_INVALID_PARAMETER (87) ile reddederdi — kompozisyon sanitleştirilmeli.
        GpnWintunSettingsMapper.SanitizeAdapterName($"{prefix}-{serverId}").Should().Be(expected);
    }

    [Theory]
    [InlineData(0x10000u, 0x20000u)]          // alt sınırın altı → 128 KiB
    [InlineData(0x4000000u, 0x4000000u)]      // üst sınır → 64 MiB
    [InlineData(0x8000000u, 0x4000000u)]      // üst sınırın üstü → 64 MiB
    [InlineData(0x30000u, 0x40000u)]          // 2'nin katına yuvarla
    [InlineData(100_000u, 0x20000u)]          // 2'nin katına yuvarla (aşağı)
    [InlineData(0x400000u, 0x400000u)]        // tam 4 MiB korunur
    public void NormalizeRingCapacity_ClampsAndRoundsToPowerOfTwo(uint input, uint expected)
    {
        GpnWintunSettingsMapper.NormalizeRingCapacity(input).Should().Be(expected);
    }

    // ── Patch ────────────────────────────────────────────────────────────

    [Fact]
    public void Patch_AppliesProvidedFields_KeepsRest()
    {
        var current = new GpnWintunItem { AdapterName = "AoGPN", RingCapacity = 0x400000 };

        var result = new GpnWintunSettingsPatch(AdapterName: "Fast", RingCapacity: 0x800000).Apply(current);

        result.AdapterName.Should().Be("Fast");
        result.RingCapacity.Should().Be(0x800000);
    }

    [Fact]
    public void Patch_NullFields_KeepCurrent()
    {
        var current = new GpnWintunItem { AdapterName = "Keep", RingCapacity = 0x100000 };

        var result = new GpnWintunSettingsPatch(AdapterName: null, RingCapacity: null).Apply(current);

        result.AdapterName.Should().Be("Keep");
        result.RingCapacity.Should().Be(0x100000);
    }

    [Fact]
    public void Patch_ClampsAndSanitizes()
    {
        var result = new GpnWintunSettingsPatch(AdapterName: "bozuk ad", RingCapacity: 0x10000).Apply(null);

        result.AdapterName.Should().Be("bozukad", "güvensiz karakterler atılır");
        result.RingCapacity.Should().Be(0x20000, "alt sınıra sınırlanır");
    }

    [Fact]
    public void Patch_NullCurrent_UsesDefaults()
    {
        var result = new GpnWintunSettingsPatch(AdapterName: null, RingCapacity: null).Apply(null);

        result.AdapterName.Should().Be("AoGPN");
        result.RingCapacity.Should().Be(0x400000);
    }

    // ── Köprü açılışına taşınma (GpnCaptureBridge) ──────────────────────

    [Fact]
    public async Task Bridge_OpensTunnel_WithConfiguredAdapterPrefixAndRingCapacity()
    {
        // Kullanıcı ayarları: adapter "FastTun", kapasite 8 MiB → köprü açılışı
        // adapter = "FastTun-it" (sunucu kimliği eklenir), ring = 0x800000.
        var api = new BridgeFakes.FakeDivertApi(recvPackets: [new byte[] { 0x45, 0x00, 0x00, 0x1c, 0x00, 0x01 }]);
        using var engine = new WinDivertEngine(api);
        using var session = new BridgeFakes.RecordingSession();
        var tunnel = new WireGuardTunnelService(session: session);
        var bridge = new GpnCaptureBridge(
            () => ["Game.exe"],
            engine,
            tunnel,
            settings: BridgeFakes.DirectSettings(),
            wintunSettings: BridgeFakes.WintunSettings(new GpnWintunItem { AdapterName = "FastTun", RingCapacity = 0x800000 }),
            source: new BridgeFakes.FakeProcessTreeSource(new ProcessInfo(100, 0, "Game.exe")));

        var started = await bridge.StartAsync(BridgeFakes.Server(), TestContext.Current.CancellationToken);

        started.Should().BeTrue();
        tunnel.AdapterName.Should().Be("FastTun-it", "ayarlardaki ön ek + sunucu kimliği");
        tunnel.LastRingCapacity.Should().Be(0x800000, "ayarlardaki kapasite normalize edilerek iletilir");

        await bridge.StopAsync();
    }
}

/// <summary>GpnCaptureBridge ile ilgili testlerin ortak sürücüsüz donanımı.</summary>
internal static class BridgeFakes
{
    /// <summary>Doğrudan recv-only (sniff ön aşaması yok) — deterministik testler.</summary>
    public static IGpnCaptureSettingsProvider DirectSettings()
        => new StubSettingsProvider();

    public static IGpnWintunSettingsProvider WintunSettings(GpnWintunItem item)
        => new StubWintunSettingsProvider(item);

    public static GpnServerProfile Server() => new(
        ServerId: "it",
        Name: "İtalya",
        EndpointHost: "127.0.0.1",
        EndpointPort: 51820,
        ServerPublicKey: "5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=",
        ClientPrivateKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        ClientAddress: "10.66.66.2/24");

    private sealed class StubSettingsProvider : IGpnCaptureSettingsProvider
    {
        public GpnCaptureItem Current => new();

        public GpnCaptureOptions CaptureOptions => new() { SniffFirst = false };
    }

    private sealed class StubWintunSettingsProvider : IGpnWintunSettingsProvider
    {
        private readonly GpnWintunItem _item;

        public StubWintunSettingsProvider(GpnWintunItem item) => _item = item;

        public GpnWintunItem Current => _item;

        public GpnWintunOptions Options => GpnWintunSettingsMapper.ToOptions(_item);
    }

    public sealed class FakeProcessTreeSource : IProcessTreeSource
    {
        private readonly IEnumerable<ProcessInfo> _processes;

        public FakeProcessTreeSource(params ProcessInfo[] processes) => _processes = processes;

        public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
            => _processes.ToArray();
    }

    public sealed class FakeDivertApi : IWinDivertApi
    {
        private readonly byte[][] _recvPackets;
        private readonly bool _limited;
        private int _recvIndex;

        public FakeDivertApi(byte[][]? recvPackets = null)
        {
            _recvPackets = recvPackets ?? [];
            _limited = recvPackets is not null;
        }

        public IntPtr Open(string? filter, int layer, short priority, ulong flags) => new(0xBEEF);

        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams)
            => new(0xBEEF);

        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
        {
            recvLen = 0;
            if (_limited && _recvIndex < _recvPackets.Length)
            {
                var data = _recvPackets[_recvIndex++];
                Marshal.Copy(data, 0, packet, data.Length);
                recvLen = data.Length;
                address.Direction = WinDivertNative.DirectionOutbound;
                return true;
            }
            return false;
        }

        public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address)
        {
            sendLen = length;
            return true;
        }

        public bool Close(IntPtr handle) => true;
        public bool SetParam(IntPtr handle, int param, ulong value) => true;

        public int GetLastError() => 0;
    }

    public sealed class RecordingSession : IWintunSession
    {
        public List<byte[]> Injected { get; } = new();
        public bool Open { get; set; } = true;

        public bool InjectPacket(ReadOnlySpan<byte> packet)
        {
            if (!Open)
            {
                return false;
            }
            Injected.Add(packet.ToArray());
            return true;
        }

        public bool TryReceivePacket(out byte[] packet, int timeoutMs)
        {
            packet = [];
            return false;
        }

        public bool IsOpen => Open;

        public void Dispose() => Open = false;
    }
}
