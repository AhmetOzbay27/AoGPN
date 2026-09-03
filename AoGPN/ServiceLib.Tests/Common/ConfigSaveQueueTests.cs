using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

public class ConfigSaveQueueTests
{
    private readonly object _lock = new();
    private readonly List<int> _saved = new();
    private int _inFlight;
    private int _maxInFlight;

    [Fact]
    public async Task FlushAsync_NoRequests_CompletesImmediately()
    {
        ConfigSaveQueue.SaveAction = Stub();

        await ConfigSaveQueue.FlushAsync();

        _saved.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestSave_Burst_CoalescesToLatest_NoConcurrency()
    {
        ConfigSaveQueue.SaveAction = Stub();

        ConfigSaveQueue.RequestSave(CreateConfig(1));
        ConfigSaveQueue.RequestSave(CreateConfig(2));
        ConfigSaveQueue.RequestSave(CreateConfig(3));
        await ConfigSaveQueue.FlushAsync();

        // Intermediate state (2) is dropped; the latest request is the last write.
        _saved.Last().Should().Be(3);
        _saved.Count.Should().BeLessThan(3);
        _maxInFlight.Should().Be(1); // writes never overlap
    }

    [Fact]
    public async Task RequestSave_UnderLoad_SerialAndCoalesced()
    {
        ConfigSaveQueue.SaveAction = Stub(delayMs: 1);

        for (var i = 0; i < 50; i++)
        {
            ConfigSaveQueue.RequestSave(CreateConfig(i));
        }
        await ConfigSaveQueue.FlushAsync();

        _maxInFlight.Should().Be(1);
        _saved.Last().Should().Be(49);
        _saved.Count.Should().BeLessThan(50);
    }

    [Fact]
    public async Task SaveAndWaitAsync_CompletesOnlyAfterWrite()
    {
        ConfigSaveQueue.SaveAction = Stub(delayMs: 30);

        await ConfigSaveQueue.SaveAndWaitAsync(CreateConfig(7));

        _saved.Should().Contain(7);
        _maxInFlight.Should().Be(1);
    }

    [Fact]
    public async Task FlushAsync_WaitsForInFlightWrite()
    {
        ConfigSaveQueue.SaveAction = Stub(delayMs: 50);

        ConfigSaveQueue.RequestSave(CreateConfig(1));
        var flush = ConfigSaveQueue.FlushAsync();

        flush.IsCompleted.Should().BeFalse(); // a write is in flight
        await flush;
        _saved.Should().Contain(1);
    }

    [Fact]
    public async Task FlushAsync_StuckWrite_TimesOutInsteadOfBlocking()
    {
        // A write that never completes (e.g. config file locked by another
        // instance) must not block FlushAsync forever — the exit path's state
        // flush depends on this. The flush gives up after FlushTimeout.
        var stuck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigSaveQueue.SaveAction = _ => stuck.Task;
        var old = ConfigSaveQueue.FlushTimeout;
        ConfigSaveQueue.FlushTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            ConfigSaveQueue.RequestSave(CreateConfig(1));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ConfigSaveQueue.FlushAsync();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ConfigSaveQueue.FlushTimeout = old;
            stuck.TrySetResult(0); // let the queued write finish so the loop drains
            ConfigSaveQueue.SaveAction = ConfigHandler.SaveConfig;
        }
    }

    [Fact]
    public void SaveAndWaitAsync_SyncWaitFromNonPumpingContext_DoesNotDeadlock()
    {
        // Regression (observed live 2026-09-03): App.OnExit calls
        // HardwareAccelerationGuard.OnGracefulExit(), which flushes the config
        // with a synchronous GetResult from the UI thread. When the in-flight
        // write yielded, a continuation posted back to the UI dispatcher — blocked
        // in GetResult — stranded forever, and shutdown deadlocked silently until
        // the 70 s exit watchdog force-killed the process. The contract: the
        // queue's own continuations (flush signal, timeout handler, write loop)
        // and the write action's continuations (ConfigHandler.SaveConfig uses
        // ConfigureAwait(false), and OnGracefulExit additionally wraps the whole
        // flush in Task.Run) must never depend on the caller's
        // SynchronizationContext. Simulated here with a context that never pumps.
        ConfigSaveQueue.SaveAction = async config =>
        {
            // A well-behaved write action, mirroring SaveConfig's
            // ConfigureAwait(false): progress must not need the caller's context.
            await Task.Delay(30).ConfigureAwait(false);
            lock (_lock)
            {
                _saved.Add(9);
            }
            return 0;
        };

        var completed = false;
        var syncWaiter = new Thread(() =>
        {
            // A UI thread that is blocked in GetResult does not pump posted
            // callbacks — stranded continuations would never run.
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            ConfigSaveQueue.SaveAndWaitAsync(CreateConfig(9)).GetAwaiter().GetResult();
            completed = true;
        })
        { IsBackground = true };

        syncWaiter.Start();
        // If any await in the queue path regains ConfigureAwait(true), the sync
        // wait deadlocks and this join times out — a clean failure, not a hang.
        syncWaiter.Join(TimeSpan.FromSeconds(5));

        completed.Should().BeTrue("the synchronous flush wait must complete even when the caller's context never pumps");
        _saved.Should().Contain(9);
        ConfigSaveQueue.SaveAction = ConfigHandler.SaveConfig;
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Swallow: simulates a dispatcher queue that is never pumped because
            // the UI thread is blocked in a synchronous wait.
        }
    }

    [Fact]
    public async Task RequestSave_NewRequestWhileWriting_IsPickedUpBySameLoop()
    {
        ConfigSaveQueue.SaveAction = Stub(delayMs: 20);

        ConfigSaveQueue.RequestSave(CreateConfig(1));
        await Task.Delay(5, TestContext.Current.CancellationToken); // let the first write start
        ConfigSaveQueue.RequestSave(CreateConfig(2));
        await ConfigSaveQueue.FlushAsync();

        _saved.Should().Contain(1);
        _saved.Last().Should().Be(2);
        _maxInFlight.Should().Be(1);
    }

    private Func<Config, Task<int>> Stub(int delayMs = 15)
    {
        return async config =>
        {
            var id = int.TryParse(config.MsgUIItem.MainMsgFilter, out var value) ? value : -1;
            var current = Interlocked.Increment(ref _inFlight);
            lock (_lock)
            {
                if (current > _maxInFlight)
                {
                    _maxInFlight = current;
                }
            }

            await Task.Delay(delayMs);

            Interlocked.Decrement(ref _inFlight);
            lock (_lock)
            {
                _saved.Add(id);
            }
            return 0;
        };
    }

    private static Config CreateConfig(int id)
    {
        return new Config
        {
            MsgUIItem = new MsgUIItem { MainMsgFilter = id.ToString() },
        };
    }
}
