using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Services;

/// <summary>
/// SpeedtestService regression guards, locked to the behavior fixed across the
/// 7.26.x milestones:
///  - An idle service must not emit a spurious "stop" event (ExitLoop registry
///    used to be static: a stale key left behind by a finished run made the next
///    ExitLoop() cancel a fresh run before it started).
///  - A stopped/errored run still persists whatever was measured (SaveTo runs on
///    every exit path).
///  - Tcping batches are capped at 64 so a "test all" over a huge list cannot
///    exhaust ephemeral ports.
///  - Run keys are scoped per service instance and removed on completion, so
///    sequential runs never cross-cancel and HasActiveRun tracks real work.
/// All runs use Tcping against 127.0.0.1:1 (instant connection refusal) so no
/// external network or core process is involved. SharedDatabase collection:
/// ProfileExManager is a process-wide singleton and runs touch SQLite.
/// </summary>
[Collection("SharedDatabase")]
public class SpeedtestServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private readonly List<SpeedTestResult> _events = [];
    private readonly List<string> _createdIndexIds = [];
    private readonly TaskCompletionSource _firstItemSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _gate = OpenGate();
    private SpeedtestService _service = null!;

    public async ValueTask InitializeAsync()
    {
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ProfileExItem>();

        var config = CoreConfigTestFactory.CreateConfig();
        // Far above the 64-batch cap so the cap itself is what chunks the run.
        config.SpeedTestItem.SpeedTestPageSize = 1000;
        // No inter-batch wait unless a test opts into it (the cap test).
        config.SpeedTestItem.SpeedTestDelayInterval = 0;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        _gate = OpenGate();
        _service = new SpeedtestService(config, OnUpdateAsync);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Remove only the ProfileEx rows this class created; leave other
        // classes' rows (the singleton DB is shared within this collection).
        foreach (var indexId in _createdIndexIds)
        {
            var rows = await SQLiteHelper.Instance.TableAsync<ProfileExItem>()
                .Where(it => it.IndexId == indexId)
                .ToListAsync();
            foreach (var row in rows)
            {
                await SQLiteHelper.Instance.DeleteAsync(row);
            }
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private static TaskCompletionSource OpenGate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
        return tcs;
    }

    private Task OnUpdateAsync(SpeedTestResult result)
    {
        lock (_events)
        {
            _events.Add(result);
        }

        if (result.IndexId.IsNotEmpty())
        {
            _firstItemSeen.TrySetResult();
        }

        return _gate.Task;
    }

    private bool HasStatus(string message) =>
        _events.Any(it => string.IsNullOrEmpty(it.IndexId) && it.Delay == message);

    private int CompletedCount() =>
        _events.Count(it => string.IsNullOrEmpty(it.IndexId) && it.Delay == ResUI.SpeedtestingCompleted);


    private bool HasStopEvent() => HasStatus(ResUI.SpeedtestingStop);

    private int StopEventCount() =>
        _events.Count(it => string.IsNullOrEmpty(it.IndexId) && it.Delay == ResUI.SpeedtestingStop);

    private ProfileItem CreateNode(int seed)
    {
        var indexId = $"st-{Guid.NewGuid():N}";
        _createdIndexIds.Add(indexId);
        var node = new ProfileItem
        {
            IndexId = indexId,
            ConfigType = EConfigType.VMess,
            CoreType = null,
            Address = "127.0.0.1",
            Port = 1,
            Remarks = $"speedtest-node-{seed}",
        };
        node.SetProtocolExtra(node.GetProtocolExtra() with
        {
            Ports = string.Empty,
        });
        return node;
    }

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < WaitTimeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        var dump = string.Join("\n  ", _events.Select(e => $"[{e.IndexId}] delay='{e.Delay}' speed='{e.Speed}'"));
        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for {what}.\nEvents so far:\n  {dump}");
    }

    // ---- 1. Exit-loop silence (the spurious-stop regression) --------------

    [Fact]
    public void ExitLoop_OnIdleService_EmitsNoStopEvent()
    {
        _service.ExitLoop();

        Assert.Equal(0, StopEventCount());
        Assert.False(_service.HasActiveRun);
    }

    [Fact]
    public async Task ExitLoop_AfterFinishedRun_EmitsNoStopEvent()
    {
        // Empty selection: the run completes instantly without any network I/O.
        _service.RunLoop(ESpeedActionType.Tcping, [], TestContext.Current.CancellationToken);

        await WaitForAsync(() => HasStatus(ResUI.SpeedtestingCompleted), "run completion");
        Assert.Equal(0, StopEventCount());
        Assert.False(_service.HasActiveRun);

        // A stale exit-loop key (the historic bug) would make this emit a stop.
        _service.ExitLoop();

        Assert.Equal(0, StopEventCount());
    }

    [Fact]
    public async Task EmptyTcpingSelection_CompletesCleanly_InsteadOfErroring()
    {
        // Regression: with checked arithmetic enabled, an empty selection drove
        // the batch page size to 0 and GetTestBatchItem divided by it — the NaN
        // int cast threw OverflowException and the run reported "stopped" even
        // though nothing was being tested. It must finish with a normal
        // "completed" event and no stop event.
        _service.RunLoop(ESpeedActionType.Tcping, [], TestContext.Current.CancellationToken);

        await WaitForAsync(() => HasStatus(ResUI.SpeedtestingCompleted), "run completion");
        Assert.Equal(0, StopEventCount());
    }

    // ---- 2. HasActiveRun lifecycle (double-click / stale-flag guard) ------

    [Fact]
    public async Task HasActiveRun_TracksRunFromStartUntilSettled()
    {
        var node = CreateNode(1);
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _service.RunLoop(ESpeedActionType.Tcping, [node], TestContext.Current.CancellationToken);

        // The first per-item event fires inside RunAsync while the exit-loop
        // key is registered — the UI relies on HasActiveRun here to reject a
        // second start instead of cancelling the live one.
        await _firstItemSeen.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        Assert.True(_service.HasActiveRun);

        _gate.TrySetResult();
        await WaitForAsync(() => HasStatus(ResUI.SpeedtestingCompleted), "run completion");
        Assert.False(_service.HasActiveRun);
    }

    [Fact]
    public async Task SequentialRuns_OnSameService_DoNotCrossCancel()
    {
        _service.RunLoop(ESpeedActionType.Tcping, [], TestContext.Current.CancellationToken);
        await WaitForAsync(() => HasStatus(ResUI.SpeedtestingCompleted), "first run completion");

        var node = CreateNode(2);
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.RunLoop(ESpeedActionType.Tcping, [node], TestContext.Current.CancellationToken);

        await _firstItemSeen.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        Assert.True(_service.HasActiveRun);

        _gate.TrySetResult();
        // HasActiveRun flips false in RunAsync's finally, which runs BEFORE
        // SaveTo + the completed event — wait for the second completed event so
        // the first run's leftovers can never satisfy the predicate early.
        await WaitForAsync(() => CompletedCount() >= 2, "second run completion");

        // Second run must have finished normally, not been cancelled by the
        // first run's leftover state.
        Assert.Equal(0, StopEventCount());
        Assert.False(_service.HasActiveRun);
    }

    // ---- 3. Mid-run stop: single stop event + partial results persisted ----

    [Fact]
    public async Task ExitLoop_MidRun_EmitsSingleStop_AndPersistsPartialResults()
    {
        var node = CreateNode(3);
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _service.RunLoop(ESpeedActionType.Tcping, [node], TestContext.Current.CancellationToken);
        await _firstItemSeen.Task.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        Assert.True(_service.HasActiveRun);

        // Stop while the run is blocked on the first item event.
        _service.ExitLoop();
        await WaitForAsync(HasStopEvent, "stop event");

        _gate.TrySetResult();
        // The completed event only fires after the stop-path SaveTo has flushed,
        // so it is the safe barrier before asserting the persisted row.
        await WaitForAsync(() => HasStatus(ResUI.SpeedtestingCompleted), "run completion after stop");
        Assert.False(_service.HasActiveRun);

        // Exactly one stop event — no double-emission.
        Assert.Equal(1, StopEventCount());

        // The run was stopped, yet GetClearItem's delay reset (delay=0) must
        // have been flushed to the DB by the stop-path SaveTo call.
        var rows = await SQLiteHelper.Instance.TableAsync<ProfileExItem>()
            .Where(it => it.IndexId == node.IndexId)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(0, row.Delay);
    }

    // ---- 4. Tcping 64-batch cap (ephemeral-port exhaustion guard) ---------

    [Fact]
    public async Task TcpingRun_Over64Nodes_ChunksAcrossDelayedBatches()
    {
        // A run of 130 nodes must be chunked by the 64 cap into 3 batches. Each
        // batch is followed by the configured 1 s inter-batch delay, so a capped
        // run takes ~3 s while an uncapped (single-batch) run would take ~1 s.
        // The refusal on 127.0.0.1:1 is instant, so elapsed time is driven by
        // the delays alone — no timing flake window.
        var config = CoreConfigTestFactory.CreateConfig();
        config.SpeedTestItem.SpeedTestPageSize = 1000;
        config.SpeedTestItem.SpeedTestDelayInterval = 1;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        _gate = OpenGate();
        var service = new SpeedtestService(config, OnUpdateAsync);

        var nodes = Enumerable.Range(0, 130).Select(CreateNode).ToList();
        var sw = Stopwatch.StartNew();
        service.RunLoop(ESpeedActionType.Tcping, nodes, TestContext.Current.CancellationToken);
        await WaitForAsync(() => HasStatus(ResUI.SpeedtestingCompleted), "capped run completion");
        sw.Stop();

        // 3 batches => 3 x 1 s delays. An uncapped single batch would finish in
        // roughly 1 s; keep a wide margin so slow CI machines cannot flip it.
        Assert.True(sw.ElapsedMilliseconds >= 2500,
            $"expected >= 2500 ms for 3 delayed batches, took {sw.ElapsedMilliseconds} ms");
    }
}
