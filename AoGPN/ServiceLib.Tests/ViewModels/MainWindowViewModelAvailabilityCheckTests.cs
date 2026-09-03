using AwesomeAssertions;
using ServiceLib.Models;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

/// <summary>
/// MainWindowViewModel.WaitForCoreReadyAsync — otomatik sunucu kullanılabilirlik
/// ölçümünün (ping/hız testi) bağlantı kurulduktan sonraya ertelenmesini sağlayan
/// bekleme mantığı. Sahte sağlık durumu sağlayıcısıyla test edilir (AppManager /
/// gerçek çekirdek başlatılmaz).
/// </summary>
public class MainWindowViewModelAvailabilityCheckTests
{
    private static CoreHealthSnapshot Health(CoreHealthState state)
        => new(CoreHealthRole.Main, state, null, null);

    [Fact]
    public async Task WaitForCoreReady_Ready_ReturnsTrue()
    {
        // Bağlantı zaten kurulu (Ready) → ölçüm hemen çalışabilir.
        var result = await MainWindowViewModel.WaitForCoreReadyAsync(
            () => Health(CoreHealthState.Ready),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForCoreReady_Failed_ReturnsFalse()
    {
        // Bağlantı kurulamadı → otomatik ölçüm atlanır.
        var result = await MainWindowViewModel.WaitForCoreReadyAsync(
            () => Health(CoreHealthState.Failed),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForCoreReady_Stopped_ReturnsFalse()
    {
        // Bağlantı kapatıldı / hiç kurulmadı → ölçüm atlanır (eskiden kesikken bile
        // hız testi çalışıp yanıltıcı sonuç gösterebiliyordu).
        var result = await MainWindowViewModel.WaitForCoreReadyAsync(
            () => Health(CoreHealthState.Stopped),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForCoreReady_NullHealth_ReturnsFalse()
    {
        // Çekirdek yok (hiç başlatılmadı) → bekleyecek bağlantı yok.
        var result = await MainWindowViewModel.WaitForCoreReadyAsync(
            () => null,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForCoreReady_StartingThenReady_ReturnsTrue()
    {
        // Bağlantı kuruluyor (Starting) → bir süre sonra Ready (kuruldu) → true;
        // ölçüm bağlantı kurulduktan SONRA çalışır.
        var polls = 0;
        CoreHealthSnapshot? Provider()
        {
            polls++;
            return Health(polls >= 2 ? CoreHealthState.Ready : CoreHealthState.Starting);
        }

        var result = await MainWindowViewModel.WaitForCoreReadyAsync(
            Provider,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        polls.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task WaitForCoreReady_AlwaysStarting_TimesOut_ReturnsFalse()
    {
        // Bağlantı hiç kurulamıyor (hep Starting) → zaman aşımında ölçüm atlanır.
        var result = await MainWindowViewModel.WaitForCoreReadyAsync(
            () => Health(CoreHealthState.Starting),
            TimeSpan.FromMilliseconds(150),
            TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }
}
