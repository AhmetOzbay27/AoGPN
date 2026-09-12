using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnProbeCache — ölçüm sonuçlarının kısa ömürlü önbelleği (Faz 2).
///
/// Neden test var: önbellek iki yönde de tehlikelidir. Çok agresif olursa ölü bir
/// sunucuyu "diri" gösterip failover'ı körleştirir; çok dar olursa kazancı sıfırlar.
/// Anahtarın ölçümün ANLAMINI değiştiren her şeyi taşıması (özellikle tünel-aktif
/// bayrağı) bu yüzden sözleşmedir.
/// </summary>
public class GpnProbeCacheTests
{
    [Fact]
    public void SetThenGet_WithinTtl_ReturnsValue()
    {
        var now = 0L;
        var cache = new GpnProbeCache(TimeSpan.FromSeconds(60), () => now);

        cache.Set("ping|it|x", 42);
        now = 59_000;

        cache.TryGet<int>("ping|it|x", out var value).Should().BeTrue();
        value.Should().Be(42);
    }

    [Fact]
    public void ExpiredEntry_IsNotReturned()
    {
        var now = 0L;
        var cache = new GpnProbeCache(TimeSpan.FromSeconds(60), () => now);

        cache.Set("ping|it|x", 42);
        now = 60_001;

        cache.TryGet<int>("ping|it|x", out _).Should().BeFalse();
        cache.Count.Should().Be(0, "süresi dolmuş kayıt temizlenmeli");
    }

    [Fact]
    public void TunnelActiveChangesTheKey()
    {
        // Tünel yokken ICMP kullanılır; tünel etkinken fiziksel NIC üzerinden UDP el
        // sıkışma ölçülür. Aynı anahtar kullanılsaydı "0 ms" gibi anlamsız bir ICMP
        // sonucu bağlanma kararını zehirlerdi.
        var options = new GpnProbeOptions();

        var noTunnel = GpnProbeCache.BuildKey("ping", "it", options, tunnelActive: false);
        var tunnel = GpnProbeCache.BuildKey("ping", "it", options, tunnelActive: true);

        tunnel.Should().NotBe(noTunnel);
    }

    [Fact]
    public void ProbeMeaning_IsFullyPartOfTheKey()
    {
        var baseline = new GpnProbeOptions(Samples: 4, PerSampleTimeoutMs: 1000);
        var moreSamples = baseline with { Samples = 8 };
        var longerTimeout = baseline with { PerSampleTimeoutMs = 2000 };
        var otherMode = baseline with { Mode = GpnProbeMode.Tcp };

        static string Key(GpnProbeOptions o) => GpnProbeCache.BuildKey("udp", "de", o, false);

        Key(moreSamples).Should().NotBe(Key(baseline));
        Key(longerTimeout).Should().NotBe(Key(baseline));
        Key(otherMode).Should().NotBe(Key(baseline));
    }

    [Fact]
    public void InvalidateServer_DropsOnlyThatServer()
    {
        var cache = new GpnProbeCache(TimeSpan.FromSeconds(60), () => 0L);
        cache.Set(GpnProbeCache.BuildKey("ping", "it", new GpnProbeOptions(), false), 1);
        cache.Set(GpnProbeCache.BuildKey("ping", "de", new GpnProbeOptions(), false), 2);

        cache.InvalidateServer("it");

        cache.TryGet<int>(GpnProbeCache.BuildKey("ping", "it", new GpnProbeOptions(), false), out _).Should().BeFalse();
        cache.TryGet<int>(GpnProbeCache.BuildKey("ping", "de", new GpnProbeOptions(), false), out var stillThere).Should().BeTrue();
        stillThere.Should().Be(2);
    }

    [Fact]
    public void TypeMismatch_IsAMissNotAnException()
    {
        var cache = new GpnProbeCache(TimeSpan.FromSeconds(60), () => 0L);
        cache.Set("k", 42);

        cache.TryGet<string>("k", out _).Should().BeFalse();
    }

    [Fact]
    public void CapacityIsBounded()
    {
        var now = 0L;
        var cache = new GpnProbeCache(TimeSpan.FromSeconds(600), () => now, capacity: 8);

        for (var i = 0; i < 50; i++)
        {
            now = i;
            cache.Set($"k{i}", i);
        }

        cache.Count.Should().BeLessThanOrEqualTo(8, "önbellek süreç ömrü boyunca büyümemeli");
    }

    [Fact]
    public void NullOrEmptyKeyAndNullValueAreIgnored()
    {
        var cache = new GpnProbeCache(TimeSpan.FromSeconds(60), () => 0L);

        cache.Set("", 1);
        cache.Set("k", (string)null!);

        cache.Count.Should().Be(0);
    }
}
