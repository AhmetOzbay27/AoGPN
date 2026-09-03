namespace ServiceLib.Common;

/// <summary>Connection mode values shared by the UI and the game trigger state machine.</summary>
public static class GameTriggerModes
{
    public const int Off = 0;
    public const int Vpn = 1;
    public const int Manual = 2;
}

/// <summary>What the game auto-connect trigger decided for one tick.</summary>
public enum GameTriggerAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>A VPN-routed game just started while the mode was Off → switch to Manuel.</summary>
    Connect,

    /// <summary>The last triggered game closed and the mode is still ours → restore the previous mode.</summary>
    Restore,
}

/// <summary>Outcome of one trigger evaluation.</summary>
public sealed class GameTriggerDecision
{
    public GameTriggerAction Action { get; }

    /// <summary>Games that newly started on this tick (only meaningful for <see cref="GameTriggerAction.Connect"/>).</summary>
    public IReadOnlyList<string> NewlyStarted { get; }

    private GameTriggerDecision(GameTriggerAction action, IReadOnlyList<string> newlyStarted)
    {
        Action = action;
        NewlyStarted = newlyStarted;
    }

    public static GameTriggerDecision None { get; } = new(GameTriggerAction.None, []);

    public static GameTriggerDecision Connect(IEnumerable<string> newlyStarted) =>
        new(GameTriggerAction.Connect, newlyStarted.ToList());

    public static GameTriggerDecision Restore() => new(GameTriggerAction.Restore, []);
}

/// <summary>
/// Pure state machine for the game auto-connect trigger. Feed it the set of currently
/// running VPN-routed games each tick; it decides when to switch to Manuel mode and
/// when to restore the previous mode. It never touches the UI, config or core — the
/// caller applies the decisions.
/// </summary>
public sealed class GameTriggerStateMachine
{
    private HashSet<string>? _prevRunning; // null = baseline not seeded yet

    /// <summary>True once the trigger switched to Manuel mode and the restore is pending.</summary>
    public bool TriggerActive { get; private set; }

    /// <summary>
    /// Evaluates one snapshot. The first call only seeds the baseline, so a game that
    /// is already running when the app opens never hijacks the mode.
    /// </summary>
    public GameTriggerDecision Tick(bool enabled, int currentMode, IReadOnlyCollection<string> runningVpnApps)
    {
        var running = new HashSet<string>(runningVpnApps, StringComparer.OrdinalIgnoreCase);

        // First snapshot seeds the baseline without acting on it.
        if (_prevRunning is null)
        {
            _prevRunning = running;
            return GameTriggerDecision.None;
        }

        var newlyStarted = running.Except(_prevRunning).ToList();

        if (enabled && !TriggerActive && currentMode == GameTriggerModes.Off && newlyStarted.Count > 0)
        {
            TriggerActive = true;
            _prevRunning = running;
            return GameTriggerDecision.Connect(newlyStarted);
        }

        if (TriggerActive && currentMode == GameTriggerModes.Manual && _prevRunning.Count > 0 && running.Count == 0)
        {
            TriggerActive = false;
            _prevRunning = running;
            return GameTriggerDecision.Restore();
        }

        _prevRunning = running;
        return GameTriggerDecision.None;
    }

    /// <summary>
    /// Forgets the pending restore (user changed the mode, disabled the trigger or
    /// pressed İptal). The next snapshot re-seeds the baseline.
    /// </summary>
    public void Reset()
    {
        TriggerActive = false;
        _prevRunning = null;
    }
}
