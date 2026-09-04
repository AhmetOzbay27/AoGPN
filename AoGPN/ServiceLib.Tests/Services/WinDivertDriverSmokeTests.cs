using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GERÇEK WinDivert sürücüsüne karşı smoke testi — WinDivertAddress'in yeni
/// 2.x layout'unun canlı paketlerde doğru doldurulduğunu doğrular.
///
/// Varsayılan olarak ATLANIR: sürücü kurulu + yönetici + WinDivert.dll gerekir.
/// Çalıştırmak için:
///   set AOGPN_SMOKE_DRIVER=1  (yönetici PowerShell'de)
///   dotnet test ServiceLib.Tests --filter "Category=DriverSmoke"
/// Sürücü yoksa/izinsizse test Assert.Skip ile atlanır — CI (yönetici olmayan)
/// bu testten etkilenmez.
/// </summary>
[Trait("Category", "DriverSmoke")]
public class WinDivertDriverSmokeTests
{
    [Fact]
    public async Task Sniff_LoopbackPacket_AddressFieldsAreSane()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WinDivert yalnızca Windows'ta çalışır.");
        }
        if (Environment.GetEnvironmentVariable("AOGPN_SMOKE_DRIVER") != "1")
        {
            Assert.Skip("Sürücü smoke'u kapalı — AOGPN_SMOKE_DRIVER=1 ile (yönetici) çalıştırın.");
        }

        using var engine = new WinDivertEngine();
        try
        {
            // Sniff modu: paketler kopyalanır, yığında ilerler — trafiği bozmaz.
            engine.OpenSniff("loopback", new WinDivertOpenParams(), layer: WinDivertNative.LayerNetwork);
        }
        catch (WinDivertException ex)
        {
            Assert.Skip($"WinDivert sürücüsü kullanılamıyor (hata {ex.NativeError}): {ex.Message}");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var worker = engine.StartCapture(cts.Token);

        // Gerçek loopback trafiği üret: 127.0.0.1'e UDP datagramı. Sniff filtresi
        // "loopback" bunu yakalar (kopyalar) — paket yine de hedefe ulaşır.
        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            await udp.SendAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, 4,
                new IPEndPoint(IPAddress.Loopback, 9)); // discard port
        }

        var enumerator = worker.GetPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                Assert.Fail("Loopback trafiği üretildi ama WinDivert hiç paket yakalamadı.");
            }

            var packet = enumerator.Current;

            packet.Length.Should().BeGreaterThan(0, "sniff edilen paket veri içerir");
            packet.Address.Timestamp.Should().NotBe(0, "timestamp QPC tabanlı — sıfır olamaz");
            packet.Address.Layer.Should().Be(WinDivertNative.LayerNetwork, "NETWORK katmanında yakalandı");
            packet.Address.IsOutbound.Should().BeTrue("loopback paketleri yalnızca outbound yakalanır");
            packet.Address.Loopback.Should().BeTrue("loopback bayrağı set edilir");
            packet.Address.IfIdx.Should().NotBe(0, "loopback arayüz indeksi dolu gelir");
            // İlk paket muhtemelen ICMP/UDP'den biri — en azından veri ve adres tutarlı.
            // Yön/arayüz enjeksiyon için yeterli; bayt içeriği sniff modunda değişmez.
        }
        finally
        {
            await enumerator.DisposeAsync();
            await worker.StopAsync();
            engine.Close();
        }
    }

    [Fact]
    public async Task CaptureToInject_LoopbackRoundTrip_DestinationReceivesPacket()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WinDivert yalnızca Windows'ta çalışır.");
        }
        if (Environment.GetEnvironmentVariable("AOGPN_SMOKE_DRIVER") != "1")
        {
            Assert.Skip("Sürücü smoke'u kapalı — AOGPN_SMOKE_DRIVER=1 ile (yönetici) çalıştırın.");
        }

        // Dinleyiciyi ÖNCE bağla — filtre yalnızca bu porta giden TEK paketi hedefler
        // (makinedeki diğer loopback trafiği asla yakalanmaz).
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var engine = new WinDivertEngine();
        try
        {
            // recv-only: paket yığından ÇIKARILIR (hedefe ulaşmaz) — gerçek yakalama.
            // DstPort eşleşmesi + outbound, yalnızca test paketini seçer.
            engine.OpenRecvOnly(
                $"loopback and udp and outbound and udp.DstPort == {port}",
                new WinDivertOpenParams(),
                layer: WinDivertNative.LayerNetwork);
        }
        catch (WinDivertException ex)
        {
            Assert.Skip($"WinDivert sürücüsü kullanılamıyor (hata {ex.NativeError}): {ex.Message}");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var worker = engine.StartCapture(cts.Token);

        // Paketi gönder — recv-only handle yığından çıkarır; listener HENÜZ almaz.
        var payload = new byte[] { 0x52, 0x54, 0x2D, 0x53, 0x4D, 0x4B }; // "RT-SMK"
        int clientLocalPort;
        using (var client = new UdpClient(AddressFamily.InterNetwork))
        {
            clientLocalPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
        }

        // Yakalanan paket + adres alanları (gerçek sürücüde 2.x layout doğruluğu).
        DivertedPacket captured;
        var enumerator = worker.GetPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                Assert.Fail("Loopback paketi gönderildi ama recv-only yakalama hiç paket vermedi.");
            }
            captured = enumerator.Current;
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        captured.Data.Length.Should().BeGreaterThan(0, "yakalanan paket tam IP datagramıdır");
        captured.Address.Timestamp.Should().NotBe(0, "timestamp QPC tabanlı — sıfır olamaz");
        captured.Address.Layer.Should().Be(WinDivertNative.LayerNetwork, "NETWORK katmanında yakalandı");
        captured.Address.IsOutbound.Should().BeTrue("gönderen sürecin paketi outbound'tur");
        captured.Address.Loopback.Should().BeTrue("loopback bayrağı set edilir");
        captured.Address.IfIdx.Should().NotBe(0, "loopback arayüz indeksi dolu gelir");

        // Yakalama handle'ını kapat → filtre sürücüden kalkar. Yeniden enjekte edilen
        // paket artık hiçbir filtraye takılmaz (recv-only handle'da Send zaten başarısız
        // olurdu; ayrıca aynı filtre kalsaydı sonsuz yakala→enjekte döngüsü oluşurdu).
        await worker.StopAsync();
        engine.Close();

        // Ayrı handle ("false" filtresi — hiçbir şey yakalamaz) ile AYNI adresi
        // (IfIdx + Outbound + Loopback) koruyarak geri enjekte et. Teslimat ancak
        // adres doğruysa gerçekleşir — hedef soketin paketi alması, IfIdx/Outbound'un
        // enjeksiyonda korunduğunun kesin kanıtıdır.
        engine.Open("false", layer: WinDivertNative.LayerNetwork);
        engine.Send(captured.Data, captured.Address);

        var received = await ReceiveWithTimeoutAsync(listener, TimeSpan.FromSeconds(3));
        received.Buffer.Should().Equal(payload, "yeniden enjekte edilen paket hedef UDP soketine ulaştı");
        received.RemoteEndPoint.Should().NotBeNull();
        ((IPEndPoint)received.RemoteEndPoint!).Port.Should().Be(clientLocalPort,
            "hedefe ulaşan paket AYNI paket — kaynak port korundu (yeni paket değil)");

        engine.Close();
    }

    /// <summary>Listener'a gelen datagramı zaman aşımıyla bekler (teslim kanıtı).</summary>
    private static async Task<UdpReceiveResult> ReceiveWithTimeoutAsync(UdpClient client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await client.ReceiveAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Yeniden enjekte edilen paket {timeout.TotalSeconds:0}s içinde listener'a ulaşmadı.");
            return default; // unreachable
        }
    }
}
