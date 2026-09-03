using System.Net;
using AwesomeAssertions;
using ServiceLib.Services.CoreConfig.Mihomo;
using Xunit;

namespace ServiceLib.Tests.Services.CoreConfig.Mihomo;

/// <summary>
/// MihomoTunSupport — fiziksel NIC / host rota yardımcıları. Buradaki kritik
/// regresyon: son okteti ≥ 128 olan sunucu IP'si (ör. Italya 92.4.220.236)
/// `byte << 24` imzalı int taşmasıyla OverflowException fırlatıyordu
/// (proje CheckForOverflowUnderflow=true) — GPN mihomo config üretimi
/// \"Varsayılan yapılandırma dosyası oluşturulamadı\" ile düşüyordu.
/// </summary>
public class MihomoTunSupportTests
{
    [Theory]
    [InlineData("92.4.220.236", 0x5C04DCECu)]   // Italya — son oktet ≥ 128 (eski kod taşardı)
    [InlineData("130.61.223.36", 0x823DDF24u)]  // Almanya — bağlanan sunucu
    [InlineData("127.0.0.1", 0x7F000001u)]
    [InlineData("10.66.66.2", 0x0A424202u)]
    [InlineData("255.255.255.255", 0xFFFFFFFFu)] // tüm oktetler üst sınır
    [InlineData("0.0.0.0", 0x00000000u)]
    public void ToNetworkOrderDword_ConvertsBigEndian(string ip, uint expected)
    {
        MihomoTunSupport.ToNetworkOrderDword(IPAddress.Parse(ip)).Should().Be(expected);
    }

    [Fact]
    public void DetectPhysicalInterface_ItalyIp_DoesNotThrow()
    {
        // Italya (92.4.220.236): eski kod burada OverflowException fırlatıyordu.
        // Gerçek iphlpapi çağrısı salt-okunurdur; asıl amaç taşma regresyonu.
        var info = MihomoTunSupport.DetectPhysicalInterface("92.4.220.236");

        // Fiziksel NIC bulunamazsa null dönebilir (ağ yok) — ama ASLA fırlatmamalı.
        if (info is not null)
        {
            info.Name.Should().NotBeNullOrWhiteSpace();
            info.Index.Should().BeGreaterThan(0);
        }
    }
}