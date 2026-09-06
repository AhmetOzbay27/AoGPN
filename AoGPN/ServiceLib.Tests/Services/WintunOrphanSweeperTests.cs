using AwesomeAssertions;
using ServiceLib.Models.Configs;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// WintunOrphanSweeper — açılış sahipsiz-Wintun süpürmesinin saf mantığı.
/// Gerçek wintun/NetworkInterface çağrıları sürücü/yönetici ister; süpürme
/// çekirdeği (SweepOrphanedCoreAsync) arayüz numaralandırma + silme dikişlerini
/// dışarıdan aldığı için ad eşleşmesi, süzme ve hata yolları sürücüsüz doğrulanır.
/// </summary>
public class WintunOrphanSweeperTests
{
    [Fact]
    public void BuildOwnedPrefixes_AlwaysIncludesLegacyAoGPN()
    {
        // Kullanıcı ön eki ne olursa olsun miras "AoGPN" kalıntıları da yakalanır.
        WintunOrphanSweeper.BuildOwnedPrefixes("FastTun").Should().Equal("FastTun", "AoGPN");
        WintunOrphanSweeper.BuildOwnedPrefixes("AoGPN").Should().Equal("AoGPN");
        WintunOrphanSweeper.BuildOwnedPrefixes(null).Should().Equal("AoGPN");
        WintunOrphanSweeper.BuildOwnedPrefixes("").Should().Equal("AoGPN");
        // Sanitleştirme: geçersiz karakterler atılır; kullanıcının harf durumu korunur
        // (eşleşme zaten büyük/küçük harf duyarsızdır) ve miras eklenmez — aynı ön ek.
        WintunOrphanSweeper.BuildOwnedPrefixes("aogpn").Should().Equal("aogpn");
        WintunOrphanSweeper.BuildOwnedPrefixes("A:B C").Should().Equal("ABC", "AoGPN");
    }

    [Fact]
    public void IsOwnedAdapterName_MatchesExactPrefixAndPrefixDashSuffix()
    {
        var prefixes = new[] { "AoGPN" };
        // Sahibi biz: mihomo TUN ("AoGPN") + native köprü ("AoGPN-<sunucu kimliği>").
        WintunOrphanSweeper.IsOwnedAdapterName("AoGPN", prefixes).Should().BeTrue();
        WintunOrphanSweeper.IsOwnedAdapterName("AoGPN-130612233651820", prefixes).Should().BeTrue();
        WintunOrphanSweeper.IsOwnedAdapterName("AoGPN-it", prefixes).Should().BeTrue();
        WintunOrphanSweeper.IsOwnedAdapterName("aogpn-de", prefixes).Should().BeTrue("büyük/küçük harf duyarsız");
        // Bizim değil: komşu adlar, farklı aileler, boş değerler.
        WintunOrphanSweeper.IsOwnedAdapterName("AoGPNExtra", prefixes).Should().BeFalse("önek + '-' sınırı");
        WintunOrphanSweeper.IsOwnedAdapterName("AoGPN2", prefixes).Should().BeFalse();
        WintunOrphanSweeper.IsOwnedAdapterName("X-AoGPN", prefixes).Should().BeFalse();
        WintunOrphanSweeper.IsOwnedAdapterName("Ethernet", prefixes).Should().BeFalse();
        WintunOrphanSweeper.IsOwnedAdapterName(null, prefixes).Should().BeFalse();
        WintunOrphanSweeper.IsOwnedAdapterName("", prefixes).Should().BeFalse();
        WintunOrphanSweeper.IsOwnedAdapterName("AoGPN-de", []).Should().BeFalse("ön ek yok — süpürme kapalı");
    }

    [Fact]
    public async Task Sweep_RemovesOnlyOwnedAdapterNames()
    {
        // Arayüz listesinde yabancı NIC'ler de var — yalnızca sahipli adlar sildirilir.
        var enumerated = new List<string>
        {
            "Ethernet",
            "AoGPN",
            "AoGPN-130612233651820",
            "Wireless-2",
            "aogpn-it",
        };
        var removedCalls = new List<string>();
        var result = await WintunOrphanSweeper.SweepOrphanedCoreAsync(
            WintunOrphanSweeper.BuildOwnedPrefixes("AoGPN"),
            enumerateNames: () => enumerated,
            removeByName: (name, _) =>
            {
                removedCalls.Add(name);
                return Task.FromResult<(bool, string?)>((true, "kaldırıldı"));
            },
            CancellationToken.None);

        result.Removed.Should().Be(3);
        result.Skipped.Should().Be(0);
        removedCalls.Should().Equal("AoGPN", "AoGPN-130612233651820", "aogpn-it");
        result.RemovedNames.Should().Equal("AoGPN", "AoGPN-130612233651820", "aogpn-it");
        result.AbortReason.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_ReportsRemovalFailuresAsSkipped_WithoutAbortingOthers()
    {
        var enumerated = new List<string> { "AoGPN-it", "AoGPN-130612233651820" };
        var result = await WintunOrphanSweeper.SweepOrphanedCoreAsync(
            WintunOrphanSweeper.BuildOwnedPrefixes("AoGPN"),
            enumerateNames: () => enumerated,
            removeByName: (name, _) => Task.FromResult<(bool, string?)>(
                name == "AoGPN-it"
                    ? (false, "yönetici gerekebilir")
                    : (true, "kaldırıldı")),
            CancellationToken.None);

        result.Removed.Should().Be(1);
        result.Skipped.Should().Be(1);
        result.RemovedNames.Should().Equal("AoGPN-130612233651820");
        result.SkippedNames.Should().Equal("AoGPN-it");
    }

    [Fact]
    public async Task Sweep_MissingWintunDll_AbortsWholeRunOnce()
    {
        var enumerated = new List<string> { "AoGPN-it", "AoGPN-de" };
        var attempts = 0;
        var result = await WintunOrphanSweeper.SweepOrphanedCoreAsync(
            WintunOrphanSweeper.BuildOwnedPrefixes("AoGPN"),
            enumerateNames: () => enumerated,
            removeByName: (name, _) =>
            {
                attempts++;
                throw new DllNotFoundException("wintun.dll");
            },
            CancellationToken.None);

        result.AbortReason.Should().Be("wintun.dll yok");
        result.Removed.Should().Be(0);
        // İlk denemeden sonra tur durur — kalan adlar denenmez (gürültü yok).
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Sweep_EnumerationFailure_ReturnsEmptyWithoutRemoving()
    {
        var result = await WintunOrphanSweeper.SweepOrphanedCoreAsync(
            WintunOrphanSweeper.BuildOwnedPrefixes("AoGPN"),
            enumerateNames: () => throw new InvalidOperationException("bozuk NIC"),
            removeByName: (_, _) => throw new Xunit.Sdk.XunitException("çağrılmamalı"),
            CancellationToken.None);

        result.Removed.Should().Be(0);
        result.AbortReason.Should().BeNull();
    }
}
