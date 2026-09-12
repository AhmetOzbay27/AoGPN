using System.Reactive;
using System.Runtime.ExceptionServices;
using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

/// <summary>
/// <see cref="InteractionExtensions"/> — bildirim amaçlı interaction çağrılarının
/// dinleyici yokken İSTİSNA ATMAMASINI kilitler.
///
/// Bu kural, bildirilen hata ayıklama günlüğünün en büyük ikinci gürültü kaynağıydı:
/// 22 MB'lık günlükte 33.933 "UnhandledInteractionException / Failed to find a
/// registration for an Interaction." satırı vardı. Uygulama bu istisnaları
/// yakalıyordu, ama Visual Studio her atış için ilk şans istisnası satırı yazar.
/// </summary>
public sealed class InteractionExtensionsTests
{
    [Fact]
    public void HasHandler_IsFalseBeforeAnyViewRegistersAndTrueAfter()
    {
        var interaction = new Interaction<Unit, Unit>();

        // Kayıt yokken false döner. Bu iddia aynı zamanda API-KAYMASI KİLİDİDİR:
        // ReactiveUI handler listesinin alanını yeniden adlandırırsa
        // HasHandler ihtiyatlı tarafta kalıp true döner ve bu test kırılır —
        // yani "algılayamıyorum" durumu sessizce geçemez.
        interaction.HasHandler().Should().BeFalse();

        interaction.RegisterHandler(ctx => ctx.SetOutput(Unit.Default));

        interaction.HasHandler().Should().BeTrue();
    }

    [Fact]
    public async Task TryHandleAsync_DoesNotThrowAndReturnsFalseWithoutAHandler()
    {
        var interaction = new Interaction<string, Unit>();
        var atilan = 0;
        void Say(object? _, FirstChanceExceptionEventArgs e)
        {
            // Tür jeneriktir (UnhandledInteractionException<TIn,TOut>); ad üzerinden
            // kontrol hem sürümden hem tür argümanlarından bağımsız kalır.
            if (e.Exception.GetType().Name.Contains("UnhandledInteraction", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref atilan);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += Say;
        bool handled;
        try
        {
            handled = await interaction.TryHandleAsync("mesaj");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Say;
        }

        handled.Should().BeFalse();
        // Asıl kilit: dinleyici yokken hiç istisna ATILMAMALI. Eski yol (Handle +
        // yutma) burada bir istisna atardı.
        atilan.Should().Be(0);
    }

    [Fact]
    public async Task TryHandleAsync_RunsTheRegisteredHandlerAndReturnsTrue()
    {
        var interaction = new Interaction<string, Unit>();
        string? seen = null;
        interaction.RegisterHandler(ctx =>
        {
            seen = ctx.Input;
            ctx.SetOutput(Unit.Default);
        });

        var handled = await interaction.TryHandleAsync("gövde");

        handled.Should().BeTrue();
        seen.Should().Be("gövde");
    }

    [Fact]
    public async Task TryHandleResultAsync_ReturnsTheOutputWhenRegistered()
    {
        var interaction = new Interaction<Unit, string?>();
        interaction.RegisterHandler(ctx => ctx.SetOutput("girilen-sifre"));

        var (handled, output) = await interaction.TryHandleResultAsync(Unit.Default);

        handled.Should().BeTrue();
        output.Should().Be("girilen-sifre");
    }

    [Fact]
    public async Task TryHandleResultAsync_ReturnsFalseAndNullWithoutAHandlerAndKeepsCallersAlive()
    {
        // Şifre istemi gerçek kullanım: dinleyici yoksa (WebView2 yerleşiminde
        // StatusBarView aktive olmuyor) çağıran geçişi iptal edebilmelidir —
        // eskiden bu yol bir istisnayla kesiliyordu.
        var interaction = new Interaction<Unit, string?>();

        var (handled, output) = await interaction.TryHandleResultAsync(Unit.Default);

        handled.Should().BeFalse();
        output.Should().BeNull();
    }
}
