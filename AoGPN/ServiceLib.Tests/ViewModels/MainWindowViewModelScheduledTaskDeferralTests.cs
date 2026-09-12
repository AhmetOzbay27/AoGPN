using AwesomeAssertions;
using ServiceLib.Services;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

/// <summary>
/// Zamanlanmış bakım tiklerinin GPN bağlantısı sırasında/yeni kurulduğunda
/// ertelenmesi (Faz 1).
///
/// Regresyon: <c>TaskManager.UpdateTaskRunSubscription</c> her dakika koşuyor ve
/// güncellenen bir abonelik <c>Reload()</c> → <c>ConnectAsync</c> zincirini
/// tetikliyordu. İdempotentlik kapısı eklenmeden önce bu, maç ortasında tünelin
/// yıkılıp yeniden kurulması demekti. Erteleme bu zinciri en baştan keser.
/// </summary>
public class MainWindowViewModelScheduledTaskDeferralTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConnectingDefers()
        => MainWindowViewModel.ShouldDeferScheduledTasks(
                GpnConnectionState.Connecting, Now, Now)
            .Should().BeTrue("bağlanma uçuşta: ölçüm ve el sıkışma yavaşlatılmamalı");

    [Fact]
    public void FreshlyConnectedDefers()
        => MainWindowViewModel.ShouldDeferScheduledTasks(
                GpnConnectionState.Connected, Now - TimeSpan.FromSeconds(5), Now)
            .Should().BeTrue("ilk saniyeler en kırılgan an: adaptör/rota ve DNS yeni yerleşiyor");

    [Fact]
    public void ConnectedJustOutsideTheWindowDoesNotDefer()
        => MainWindowViewModel.ShouldDeferScheduledTasks(
                GpnConnectionState.Connected,
                Now - MainWindowViewModel.GpnConnectFreshWindow - TimeSpan.FromSeconds(1),
                Now)
            .Should().BeFalse();

    [Theory]
    [InlineData(GpnConnectionState.Disconnected)]
    [InlineData(GpnConnectionState.Failed)]
    public void NotConnectedDoesNotDefer(GpnConnectionState state)
        => MainWindowViewModel.ShouldDeferScheduledTasks(state, Now, Now).Should().BeFalse();

    [Fact]
    public void ConnectedWithoutTimestampDoesNotDeferForever()
        // Zaman damgası yoksa erteleme sonsuza uzamaz: bakım yine de çalışır.
        => MainWindowViewModel.ShouldDeferScheduledTasks(GpnConnectionState.Connected, null, Now)
            .Should().BeFalse();

    [Fact]
    public void NoCoordinatorDoesNotDefer()
        => MainWindowViewModel.ShouldDeferScheduledTasks(null, null, Now).Should().BeFalse();

    [Fact]
    public void FreshWindowIsOneMinute()
        // Pencere bilinçli olarak kısa: erteleme kayıp değildir, bir sonraki
        // dakikada yeniden denenir; ama maç ortasında sürekli ertelenmemeli.
        => MainWindowViewModel.GpnConnectFreshWindow.Should().Be(TimeSpan.FromSeconds(60));
}
