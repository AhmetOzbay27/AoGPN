using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

public class GameTriggerStateMachineTests
{
    private static GameTriggerStateMachine NewMachine() => new();

    // --- startup / baseline seeding ---

    [Fact]
    public void FirstTick_SeedsBaseline_GameAlreadyRunningDoesNotHijack()
    {
        var sm = NewMachine();

        var decision = sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        decision.Action.Should().Be(GameTriggerAction.None);
        sm.TriggerActive.Should().BeFalse();
    }

    [Fact]
    public void FirstTick_NoGamesRunning_NoDecision()
    {
        var sm = NewMachine();

        sm.Tick(true, GameTriggerModes.Off, []).Action.Should().Be(GameTriggerAction.None);
        sm.TriggerActive.Should().BeFalse();
    }

    // --- connect ---

    [Fact]
    public void GameStarts_WhileOff_ReturnsConnectWithNewlyStartedNames()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);

        var decision = sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        decision.Action.Should().Be(GameTriggerAction.Connect);
        decision.NewlyStarted.Should().Contain("EscapeFromTarkov");
        sm.TriggerActive.Should().BeTrue();
    }

    [Fact]
    public void TwoGamesStartInSameTick_BothReportedAsNewlyStarted()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);

        var decision = sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov", "Valorant"]);

        decision.Action.Should().Be(GameTriggerAction.Connect);
        decision.NewlyStarted.Should().Contain("EscapeFromTarkov");
        decision.NewlyStarted.Should().Contain("Valorant");
    }

    [Fact]
    public void GameStarts_WhileDisabled_ReturnsNone()
    {
        var sm = NewMachine();
        sm.Tick(false, GameTriggerModes.Off, []);

        var decision = sm.Tick(false, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        decision.Action.Should().Be(GameTriggerAction.None);
        sm.TriggerActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(GameTriggerModes.Vpn)]
    [InlineData(GameTriggerModes.Manual)]
    public void GameStarts_NotOffMode_ReturnsNone(int mode)
    {
        var sm = NewMachine();
        sm.Tick(true, mode, []);

        sm.Tick(true, mode, ["EscapeFromTarkov"]).Action.Should().Be(GameTriggerAction.None);
        sm.TriggerActive.Should().BeFalse();
    }

    // --- restore ---

    [Fact]
    public void LastGameCloses_ReturnsRestore()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        var decision = sm.Tick(true, GameTriggerModes.Manual, []);

        decision.Action.Should().Be(GameTriggerAction.Restore);
        sm.TriggerActive.Should().BeFalse();
    }

    [Fact]
    public void GameStillRunning_NoRestore()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        var decision = sm.Tick(true, GameTriggerModes.Manual, ["EscapeFromTarkov"]);

        decision.Action.Should().Be(GameTriggerAction.None);
        sm.TriggerActive.Should().BeTrue();
    }

    [Fact]
    public void ModeChangedAwayFromManual_NoRestore()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        // The user switched to VPN mode while the game runs → nothing happens.
        sm.Tick(true, GameTriggerModes.Vpn, ["EscapeFromTarkov"]).Action.Should().Be(GameTriggerAction.None);
    }

    // --- multiple games ---

    [Fact]
    public void MultiGame_SecondGameStarts_NoSecondConnect()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        var decision = sm.Tick(true, GameTriggerModes.Manual, ["EscapeFromTarkov", "Valorant"]);

        decision.Action.Should().Be(GameTriggerAction.None);
        sm.TriggerActive.Should().BeTrue();
    }

    [Fact]
    public void MultiGame_OneCloses_NoRestore_UntilAllClosed()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);
        sm.Tick(true, GameTriggerModes.Manual, ["EscapeFromTarkov", "Valorant"]); // second game starts

        // A closes, B still runs → no restore.
        sm.Tick(true, GameTriggerModes.Manual, ["Valorant"]).Action.Should().Be(GameTriggerAction.None);
        // B closes → restore.
        sm.Tick(true, GameTriggerModes.Manual, []).Action.Should().Be(GameTriggerAction.Restore);
    }

    // --- user intervention / cancel ---

    [Fact]
    public void Reset_WhileGameRunning_CancelsPendingRestore()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        sm.Reset();

        sm.TriggerActive.Should().BeFalse();
        // The running game reseeds the baseline → no connect...
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]).Action.Should().Be(GameTriggerAction.None);
        // ...and no restore when it closes.
        sm.Tick(true, GameTriggerModes.Manual, []).Action.Should().Be(GameTriggerAction.None);
    }

    [Fact]
    public void UserTakesOver_GameKeepsRunning_NeverFightsBack()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]); // connect
        sm.Reset(); // user switched the mode manually

        // The game keeps running in Off mode → must not flip back to Manuel.
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]).Action.Should().Be(GameTriggerAction.None);
    }

    // --- case-insensitivity ---

    [Fact]
    public void RunningSet_IsCaseInsensitive()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["escapefromtarkov"]); // lower-case first

        // Upper-case in the same set is still tracked as the same game.
        sm.Tick(true, GameTriggerModes.Manual, ["EscapeFromTarkov"]).Action.Should().Be(GameTriggerAction.None);
        sm.Tick(true, GameTriggerModes.Manual, []).Action.Should().Be(GameTriggerAction.Restore);
    }

    // --- reconnect after close + reopen ---

    [Fact]
    public void GameClosesThenReopens_ConnectsAgain()
    {
        var sm = NewMachine();
        sm.Tick(true, GameTriggerModes.Off, []);
        sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]); // connect
        sm.Tick(true, GameTriggerModes.Manual, []); // restore

        var decision = sm.Tick(true, GameTriggerModes.Off, ["EscapeFromTarkov"]);

        decision.Action.Should().Be(GameTriggerAction.Connect);
        sm.TriggerActive.Should().BeTrue();
    }
}
