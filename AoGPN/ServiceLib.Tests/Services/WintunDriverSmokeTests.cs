using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GERÇEK Wintun sürücüsüne karşı Faz 2b smoke testi: WireGuardTunnelService'in
/// enjeksiyon köprüsünü (session ring → adaptör → OS) uçtan uca doğrular.
///
/// Varsayılan olarak ATLANIR: wintun.dll + sürücü + yönetici gerekir (adapter
/// oluşturma sürücüyü otomatik kurar). Çalıştırmak için:
///   set AOGPN_SMOKE_DRIVER=1   (yönetici PowerShell'de)
///   dotnet test ServiceLib.Tests --filter "Category=DriverSmoke"
///
/// Adım: benzersiz adapter oluştur → 10.88.0.2/24 ata (netsh) → session aç →
/// listener'ı adapter IP'sine bağla → 10.88.0.2'ye giden IPv4/UDP paketi
/// InjectPacket ile gönder → listener payload'ı alır (enjekte edilen paket OS'a
/// teslim edildi — tünel "inbound" yolu çalışıyor). Temizlik: session/adapter
/// kapat + netsh adresi sil.
/// </summary>
[Trait("Category", "DriverSmoke")]
public class WintunDriverSmokeTests
{
    private const string AdapterSubnet = "10.88.0.0/24";

    [Fact]
    public async Task InjectPacket_RealAdapter_DeliversToOsStack()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Wintun yalnızca Windows'ta çalışır.");
        }
        if (Environment.GetEnvironmentVariable("AOGPN_SMOKE_DRIVER") != "1")
        {
            Assert.Skip("Sürücü smoke'u kapalı — AOGPN_SMOKE_DRIVER=1 ile (yönetici) çalıştırın.");
        }

        var adapterName = $"AoGPN-Smoke-{Guid.NewGuid():N}";
        var adapterIp = IPAddress.Parse("10.88.0.2");

        var service = new WireGuardTunnelService();
        try
        {
            try
            {
                service.Open(adapterName);
            }
            catch (WintunException ex)
            {
                Assert.Skip($"Wintun kullanılamıyor (hata {ex.NativeError}): {ex.Message}");
            }

            if (!await TryAssignAdapterIpAsync(adapterName, adapterIp))
            {
                Assert.Skip($"Adapter IP atanamadı (netsh) — yönetici ayrıcalığı gerekir: {adapterName}");
            }

            using var listener = await BindListenerWithRetryAsync(adapterIp);
            var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

            // IPv4 + UDP paketi üret: src=10.88.0.2 → dst=10.88.0.2:listenerPort.
            var payload = new byte[] { 0xA0, 0x47, 0x50, 0x4E, 0x2D, 0x53, 0x4D, 0x4B }; // "AoGPN-SMK"
            var packet = BuildIpv4Udp(adapterIp, adapterIp, (ushort)listenerPort, payload);

            // Faz 2d alım yolu köprüsü: sunucudan çözülen paket adaptöre enjekte edilir.
            service.InjectIntoAdapter(packet).Should().BeTrue("session açıkken enjeksiyon başarılı");

            // Paket adaptöre enjekte edildi → OS yerel yığına teslim etti → listener aldı.
            var received = await ReceiveWithTimeoutAsync(listener, TimeSpan.FromSeconds(3));

            received.Should().Equal(payload, "enjekte edilen UDP payload'ı OS yığınına ulaştı");
            service.Snapshot().Injected.Should().Be(1);
            service.Snapshot().Captured.Should().Be(0, "köprü çalıştırılmadı — yalnızca doğrudan alım-yolu enjeksiyonu");
        }
        finally
        {
            await service.DisposeAsync();
            await TryRemoveAdapterIpAsync(adapterName, adapterIp);
        }
    }

    /// <summary>
    /// Gelişmiş gerçek-sürücü smoke testi: geçersiz adaptör adını (':' içeren) —
    /// özellikle <see cref="WintunException"/> kodu 87 (ERROR_INVALID_PARAMETER) —
    /// NET şekilde raporlar ve regresyonu FAIL olarak yakalar.
    ///
    /// Neden ayrı bir test: <see cref="InjectPacket_RealAdapter_DeliversToOsStack"/>
    /// her WintunException'ı Assert.Skip ile geçirir; o yüzden 87-geçersiz-ad hatası
    /// orada sessizce yutulurdu. Bu varyant iki adımda çalışır:
    ///   1) Sürücünün GERÇEKTEN kullanılabilir olduğunu geçerli bir adaptör açıp
    ///      kapatarak kanıtlar (DLL/sürücü/yönetici yoksa <em>skip</em> — ortam sorunu),
    ///   2) ':' içeren geçersiz adın WintunCreateAdapter'da 87 üretmesini zorunlu kılar;
    ///      üretmezse (farklı kod veya başarı) bu bir <em>FAIL</em> olur, skip değil.
    ///   Böylece adaptör adını sanitleştiren kök-düzeltme (GpnCaptureBridge) regresyona
    ///   uğrarsa gerçek sürücüye karşı anında, açıklamalı bir hata görürsünüz.
    /// </summary>
    [Fact]
    public async Task Open_InvalidAdapterName_ReportsClearError87()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Wintun yalnızca Windows'ta çalışır.");
        }
        if (Environment.GetEnvironmentVariable("AOGPN_SMOKE_DRIVER") != "1")
        {
            Assert.Skip("Sürücü smoke'u kapalı — AOGPN_SMOKE_DRIVER=1 ile (yönetici) çalıştırın.");
        }

        var validName = $"AoGPN-Smoke-{Guid.NewGuid():N}";
        await using (var probe = new WireGuardTunnelService())
        {
            try
            {
                // Geçerli ad açılabiliyorsa sürücü gerçekten çalışıyor demektir (yönetici
                // + wintun.dll + sürücü). Dispose (await using) oluşturulan adaptörü siler.
                probe.Open(validName);
            }
            catch (WintunException ex)
            {
                // Ortam erişilemez: DLL/sürücü/yönetici sorunu — testin konusu değil (skip).
                Assert.Skip($"Sürücü kullanılamıyor — geçerli-ad Open dahi başarısız (hata {ex.NativeError}): {ex.Message}");
            }
        }

        // Gerçek sürücüye karşı geçersiz adaptör adı: ':' içeren ad → WintunCreateAdapter
        // ERROR_INVALID_PARAMETER (87) döndürmelidir. Bu, host:port ServerId'nin adaptör
        // adına sızması senaryosudur (GpnCaptureBridge bunu sanitleştirir); sanitleştirici
        // atlanırsa veya bozulursa, bu test 87 beklentisini FAIL olarak net raporlar.
        var invalidName = $"AoGPN-Smoke-{Guid.NewGuid():N}:bad";
        await using var service = new WireGuardTunnelService();
        var thrown = Assert.Throws<WintunException>(() => service.Open(invalidName));

        thrown.NativeError.Should().Be(
            87, $"geçersiz adaptör adı ({invalidName}) WintunCreateAdapter'da ERROR_INVALID_PARAMETER (87) üretmeli; alınan: {thrown.NativeError}");
        thrown.Message.Should().Contain("87", "hata kodu mesajda net görünmeli (raporlanabilirlik)");
    }

    // ── Yardımcılar ──────────────────────────────────────────────────────

    private static async Task<bool> TryAssignAdapterIpAsync(string adapterName, IPAddress ip)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ok = await RunNetshAsync(
                $"interface ip set address name=\"{adapterName}\" source=static addr={ip} mask=255.255.255.0");
            if (ok)
            {
                return true;
            }
            await Task.Delay(300);
        }
        return false;
    }

    private static async Task<bool> TryRemoveAdapterIpAsync(string adapterName, IPAddress ip)
    {
        await RunNetshAsync($"interface ip delete address name=\"{adapterName}\" addr={ip}");
        return true;
    }

    private static Task<bool> RunNetshAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit(5000);
            return Task.FromResult(process.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static async Task<UdpClient> BindListenerWithRetryAsync(IPAddress ip)
    {
        // Adapter IP'si atandıktan sonra arayüzün hazır olması zaman alabilir.
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                return new UdpClient(new IPEndPoint(ip, 0));
            }
            catch (SocketException)
            {
                await Task.Delay(200);
            }
        }
        throw new InvalidOperationException($"Adapter IP'sine ({ip}) bağlanılamadı — arayüz hazır değil.");
    }

    private static async Task<byte[]> ReceiveWithTimeoutAsync(UdpClient client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var result = await client.ReceiveAsync(cts.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Enjekte edilen paket {timeout.TotalSeconds:0}s içinde listener'a ulaşmadı.");
            return []; // unreachable
        }
    }

    /// <summary>Minimal IPv4+UDP paketi kurar (IP checksum hesaplı, UDP checksum 0 — IPv4'te geçerli).</summary>
    private static byte[] BuildIpv4Udp(IPAddress src, IPAddress dst, ushort dstPort, byte[] payload)
    {
        var srcBytes = src.GetAddressBytes();
        var dstBytes = dst.GetAddressBytes();
        var udpLen = 8 + payload.Length;
        var totalLen = 20 + udpLen;

        var packet = new byte[totalLen];
        packet[0] = 0x45;                         // IPv4, IHL 5
        packet[2] = (byte)(totalLen >> 8);
        packet[3] = (byte)totalLen;
        packet[4] = 0x12;                         // id
        packet[5] = 0x34;
        packet[8] = 64;                           // TTL
        packet[9] = 17;                           // UDP
        srcBytes.CopyTo(packet, 12);
        dstBytes.CopyTo(packet, 16);

        var checksum = ComputeIpChecksum(packet.AsSpan(0, 20));
        packet[10] = (byte)(checksum >> 8);
        packet[11] = (byte)checksum;

        packet[20] = 0x77;                        // src port 0x7777
        packet[21] = 0x77;
        packet[22] = (byte)(dstPort >> 8);
        packet[23] = (byte)dstPort;
        packet[24] = (byte)(udpLen >> 8);
        packet[25] = (byte)udpLen;
        // UDP checksum 0 — IPv4 için "hesaplanmadı" anlamına gelir, kabul edilir.
        payload.CopyTo(packet, 28);
        return packet;
    }

    private static ushort ComputeIpChecksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (var i = 0; i < header.Length; i += 2)
        {
            sum += (uint)((header[i] << 8) | header[i + 1]);
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }
}

/// <summary>
/// wintun.dll ortam-durumu hata eşlemesi — sürücü gerekMEZ, bu yüzden
/// <see cref="WintunDriverSmokeTests"/>'in DriverSmoke kapısının DIŞINDA ayrı
/// bir sınıfta normal test koşularında çalışır. <see cref="WireGuardTunnelService.Open"/>
/// natif yükleme durumlarını doğru koda eşler:
///   • DLL bulunamadı (DllNotFoundException)     → WintunException.NativeError = 2
///   • eski DLL / export yok (EntryPointNotFound)→ WintunException.NativeError = 127
/// Gerçek wintun.dll'i çıkarıp geri koymak paylaşılan test dizininde yarışlı/yıkıcı
/// olurdu; bunun yerine Open'ın natif dikişine (createAdapter) bu istisnaları
/// fırlatan bir uygulama verilir — gerçek ortamın tetiklediği aynı kod yolu budur.
/// </summary>
public sealed class WintunNativeLoadErrorTests
{
    [Fact]
    public void Open_MissingWintunDll_ReportsError2()
    {
        // wintun.dll yokken WintunCreateAdapter DllNotFoundException verir ve
        // WireGuardTunnelService bunu ERROR_FILE_NOT_FOUND (2) olarak map eder.
        var service = new WireGuardTunnelService(
            createAdapter: (_, _, _) => throw new DllNotFoundException("wintun.dll"));

        var ex = Assert.Throws<WintunException>(() => service.Open("AoGPN-Test"));

        ex.NativeError.Should().Be(2, "wintun.dll bulunamadı → DllNotFoundException → ERROR_FILE_NOT_FOUND (2)");
        ex.Message.Should().Contain("wintun.dll bulunamadı", "teşhis için hata mesajı hata kodunu açıklamalı");
    }

    [Fact]
    public void Open_OldWintunDll_MissingCreateAdapterExport_ReportsError127()
    {
        // Eskimiş wintun.dll WintunCreateAdapter export'unu içermez → yükleme
        // EntryPointNotFoundException verir, map CODE ERROR_PROC_NOT_FOUND (127).
        var service = new WireGuardTunnelService(
            createAdapter: (_, _, _) => throw new EntryPointNotFoundException("WintunCreateAdapter"));

        var ex = Assert.Throws<WintunException>(() => service.Open("AoGPN-Test"));

        ex.NativeError.Should().Be(127, "eski wintun.dll → EntryPointNotFound → ERROR_PROC_NOT_FOUND (127)");
        ex.Message.Should().Contain("sürümü eski", "teşhis için hata mesajı hata kodunu açıklamalı");
    }
}
