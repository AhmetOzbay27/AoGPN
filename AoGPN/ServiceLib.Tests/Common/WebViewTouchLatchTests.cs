using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

/// <summary>
/// <see cref="WebViewTouchLatch"/> — kapanmış WebView2 denetleyicisine tekrar tekrar
/// dokunmayı (ve her seferinde istisna atmayı) durduran kalıcı kilit.
///
/// Bildirilen hata ayıklama günlüğünde "CoreWebView2 members cannot be accessed
/// after the WebView2 controller was closed." iletisi 44 ayrı blok hâlinde 80 kez
/// geçiyordu: 2 sn'lik yaşam döngüsü poll'u, denetleyici kapandıktan sonra da
/// CoreWebView2 üyelerine dokunmaya devam ediyordu.
/// </summary>
public sealed class WebViewTouchLatchTests
{
    [Fact]
    public void ShouldTouch_RequiresEveryPrecondition()
    {
        var latch = new WebViewTouchLatch();

        latch.ShouldTouch(isClosing: false, isHostReady: true, hasController: true).Should().BeTrue();

        // Herhangi biri düşerse dokunulmaz.
        latch.ShouldTouch(isClosing: true, isHostReady: true, hasController: true).Should().BeFalse();
        latch.ShouldTouch(isClosing: false, isHostReady: false, hasController: true).Should().BeFalse();
        latch.ShouldTouch(isClosing: false, isHostReady: true, hasController: false).Should().BeFalse();
    }

    [Fact]
    public void CloseOnDestruction_ShutsTheLatchPermanently()
    {
        var latch = new WebViewTouchLatch();
        latch.IsOpen.Should().BeTrue();

        latch.CloseOnDestruction().Should().BeTrue("yıkım ilk kez gözleniyor");

        latch.IsOpen.Should().BeFalse();
        // Diğer koşullar yeniden sağlansa bile kilit açılmaz: denetleyici nesnesi
        // null olmadığı için "hazır" görünmeye devam ederdi.
        latch.ShouldTouch(isClosing: false, isHostReady: true, hasController: true).Should().BeFalse();
    }

    [Fact]
    public void CloseOnDestruction_ReportsOnlyTheFirstObservation()
    {
        // Bu, "hatayı yalnızca bir kez bildir" davranışının temelidir: ikinci ve
        // sonraki gözlemler false döner, çağıran tekrar günlük yazmaz.
        var latch = new WebViewTouchLatch();

        latch.CloseOnDestruction().Should().BeTrue();

        for (var i = 0; i < 5; i++)
        {
            latch.CloseOnDestruction().Should().BeFalse();
        }
    }

    [Fact]
    public void ShouldTouch_StaysClosedUnderConcurrentObservations()
    {
        // Sıcak poll döngüsü ile UI iş parçacığı aynı anda gözlemleyebilir: kilit
        // yalnızca bir kez kapanmalı, Count > 1 olmamalı.
        var latch = new WebViewTouchLatch();
        var ilkKapanislar = 0;

        Parallel.For(0, 64, _ =>
        {
            if (latch.CloseOnDestruction())
            {
                Interlocked.Increment(ref ilkKapanislar);
            }
        });

        ilkKapanislar.Should().Be(1);
        latch.IsOpen.Should().BeFalse();
    }
}
