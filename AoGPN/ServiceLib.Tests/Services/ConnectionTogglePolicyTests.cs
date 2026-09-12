using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// ConnectionTogglePolicy — "toggle" isteğinin kullanıcı NİYETİNE göre
/// yorumlandığını doğrular. Regresyonun kaynağı: dashboard'ın tek butonu
/// bağlan/kes ayrımını canlı çekirdek durumundan türetiyordu, bu yüzden arka
/// planda kurulan bir tünel arayüzde hâlâ "Bağlan" görünürken gelen tıklama
/// "kes"e dönüşüp taze tüneli yıkıyordu.
/// </summary>
public class ConnectionTogglePolicyTests
{
    [Theory]
    [InlineData(true, ConnectionToggleIntent.Disconnect)]
    [InlineData(false, ConnectionToggleIntent.Connect)]
    public void DisplayedState_MapsToIntent(bool displayedConnected, ConnectionToggleIntent expected)
        => ConnectionTogglePolicy.IntentFromDisplayedState(displayedConnected).Should().Be(expected);

    [Fact]
    public void MissingDisplayedState_YieldsNoIntent()
        // Durum bildirmeyen gönderen (eski tema, tepsi) → canlı duruma göre
        // yorumlayan geriye dönük yola düşülür.
        => ConnectionTogglePolicy.IntentFromDisplayedState(null).Should().BeNull();

    [Theory]
    // "Bağlan" niyeti: tünel kurulu değilse bağlanır; kurulu ya da kurulmakta ise
    // HİÇBİR ŞEY yapılmaz (istek kesmeye dönüşmez).
    [InlineData(ConnectionToggleIntent.Connect, false, false, ConnectionToggleAction.Connect)]
    [InlineData(ConnectionToggleIntent.Connect, true, false, ConnectionToggleAction.NoOp)]
    [InlineData(ConnectionToggleIntent.Connect, false, true, ConnectionToggleAction.NoOp)]
    [InlineData(ConnectionToggleIntent.Connect, true, true, ConnectionToggleAction.NoOp)]
    // "Kes" niyeti: kurulu ya da kurulmakta olan tünel kesilir; zaten kopuksa
    // hiçbir şey yapılmaz (istek yeniden bağlanmaya dönüşmez).
    [InlineData(ConnectionToggleIntent.Disconnect, true, false, ConnectionToggleAction.Disconnect)]
    [InlineData(ConnectionToggleIntent.Disconnect, false, true, ConnectionToggleAction.Disconnect)]
    [InlineData(ConnectionToggleIntent.Disconnect, true, true, ConnectionToggleAction.Disconnect)]
    [InlineData(ConnectionToggleIntent.Disconnect, false, false, ConnectionToggleAction.NoOp)]
    public void Decide_NeverFlipsTheUserIntent(
        ConnectionToggleIntent intent,
        bool effectiveConnected,
        bool connectionStarting,
        ConnectionToggleAction expected)
        => ConnectionTogglePolicy.Decide(intent, effectiveConnected, connectionStarting).Should().Be(expected);

    [Fact]
    public void InvisibleBackgroundConnect_ThenClick_IsNoOp()
    {
        // Canlı senaryo: açılışta tünel arka planda kuruldu (canlı durum BAĞLI) ama
        // arayüz hâlâ "Bağlan" gösteriyordu; kullanıcı bastı.
        // Niyet "bağlan", canlı durum "bağlı" → dokunulmaz; eskiden taze tünel yıkılırdı.
        var intent = ConnectionTogglePolicy.IntentFromDisplayedState(displayedConnected: false);

        var action = ConnectionTogglePolicy.Decide(intent!.Value, effectiveConnected: true, connectionStarting: false);

        action.Should().Be(ConnectionToggleAction.NoOp,
            "kullanıcı bağlanmak istedi; canlı durum bu isteği kesmeye çevirmemeli");
    }
}
