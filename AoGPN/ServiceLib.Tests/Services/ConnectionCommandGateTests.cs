using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

/// <summary>
/// ConnectionCommandGate davranışı — kapı sıralaması, en-yeni-niyet yuvası ve
/// tam-bir-kez drenaj. Komutlar elle sürülen TCS'lerle kontrol edilir, bu
/// yüzden her test deterministiktir (zamanlama yok).
/// </summary>
public class ConnectionCommandGateTests
{
    private sealed class CommandRecorder
    {
        public List<string> Started { get; } = [];
        public List<string> Completed { get; } = [];
        private readonly Dictionary<string, TaskCompletionSource> _gates = new();
        private readonly object _sync = new();

        /// <summary>Başlatıldığında bekleyen ve testin elle serbest bıraktığı bir komut tanımlar.</summary>
        public Func<Task> BlockingCommand(string name)
        {
            return async () =>
            {
                lock (_sync)
                {
                    Started.Add(name);
                }
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_sync)
                {
                    _gates[name] = tcs;
                }
                await tcs.Task.ConfigureAwait(false);
                lock (_sync)
                {
                    Completed.Add(name);
                }
            };
        }

        public Func<Task> InstantCommand(string name)
        {
            return () =>
            {
                lock (_sync)
                {
                    Started.Add(name);
                    Completed.Add(name);
                }
                return Task.CompletedTask;
            };
        }

        /// <summary>Başlayınca bekleyen ve serbest bırakılınca HATA veren komut (drenaj senaryoları için).</summary>
        public Func<Task> BlockingThrowingCommand(string name)
        {
            return async () =>
            {
                lock (_sync)
                {
                    Started.Add(name);
                }
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_sync)
                {
                    _gates[name] = tcs;
                }
                await tcs.Task.ConfigureAwait(false);
                throw new InvalidOperationException("boom");
            };
        }

        public void Release(string name)
        {
            TaskCompletionSource? tcs;
            lock (_sync)
            {
                _gates.TryGetValue(name, out tcs);
            }
            tcs?.TrySetResult();
        }

        /// <summary>Komutun kapıyı gerçekten aldığını (başladığını) bekler — kuyruklama testleri için.</summary>
        public void WaitForStarted(string name)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_sync)
                {
                    if (Started.Contains(name))
                    {
                        return;
                    }
                }
                Thread.Sleep(5);
            }
            throw new TimeoutException($"Komut başlamadı: {name}");
        }
    }

    [Fact]
    public async Task Runs_Sequentially_When_Idle()
    {
        var gate = new ConnectionCommandGate();
        var rec = new CommandRecorder();

        await gate.RunAsync("a", rec.InstantCommand("a"));
        await gate.RunAsync("b", rec.InstantCommand("b"));

        Assert.Equal(["a", "b"], rec.Started);
        Assert.Equal(["a", "b"], rec.Completed);
    }

    [Fact]
    public async Task Busy_Gate_Queues_Latest_Intent_And_Replays_It_Once()
    {
        var gate = new ConnectionCommandGate();
        var rec = new CommandRecorder();

        var first = gate.RunAsync("first", rec.BlockingCommand("first"));
        rec.WaitForStarted("first"); // kapı sahibi kesinleşti
        // İlk komut kapıyı tutuyor; gelen üç tıklama yuvayı sırayla değiştirir.
        var queued1 = gate.RunAsync("q1", rec.InstantCommand("q1"));
        var queued2 = gate.RunAsync("q2", rec.InstantCommand("q2"));
        var queued3 = gate.RunAsync("q3", rec.InstantCommand("q3"));
        await Task.WhenAll(queued1, queued2, queued3);

        // Kapı sahibi bitene kadar hiçbiri başlamadı.
        Assert.Equal(["first"], rec.Started);

        rec.Release("first");
        await first;

        // Yalnızca SON niyet işlendi; q1/q2 atıldı (en son karar kazanır).
        Assert.Equal(["first", "q3"], rec.Started);
        Assert.Equal(["first", "q3"], rec.Completed);
    }

    [Fact]
    public async Task Command_Arriving_During_Drain_Is_Also_Served()
    {
        var gate = new ConnectionCommandGate();
        var rec = new CommandRecorder();

        // İlk komut bloke; ikincisi başladığında testi bilgilendirip kendi TCS'sinde bekler.
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.RunAsync("first", rec.BlockingCommand("first"));
        rec.WaitForStarted("first"); // kapı sahibi kesinleşti
        var second = gate.RunAsync("second", async () =>
        {
            rec.Started.Add("second");
            secondStarted.TrySetResult();
            await secondGate.Task.ConfigureAwait(false);
            rec.Completed.Add("second");
        });

        rec.Release("first");
        await secondStarted.Task;

        // Drenaj "second"i işlerken gelen yeni tıklama da işlenmelidir.
        var third = gate.RunAsync("third", rec.InstantCommand("third"));

        // "second"i serbest bırakınca drenaj "third"ü de işler ve hepsi biter.
        secondGate.TrySetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(["first", "second", "third"], rec.Started);
        Assert.Equal(["first", "second", "third"], rec.Completed);
    }

    [Fact]
    public async Task Exception_In_Command_Still_Lets_Queued_Intent_Run()
    {
        var gate = new ConnectionCommandGate();
        var rec = new CommandRecorder();

        var first = gate.RunAsync("failing", () =>
        {
            rec.Started.Add("failing");
            throw new InvalidOperationException("boom");
        });
        var queued = gate.RunAsync("queued", rec.InstantCommand("queued"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await queued;

        // Hata veren komut tamamlanamaz (Started'a girer); kuyruklu niyet yine işlenir.
        Assert.Equal(["failing", "queued"], rec.Started);
        Assert.Equal(["queued"], rec.Completed);
    }

    [Fact]
    public async Task Exception_In_Queued_Command_Does_Not_Kill_The_Gate()
    {
        var gate = new ConnectionCommandGate();
        var rec = new CommandRecorder();

        var first = gate.RunAsync("first", rec.BlockingCommand("first"));
        rec.WaitForStarted("first"); // kapı sahibi kesinleşti
        // Drenajda çalışırken hata verecek (serbest bırakılınca throw eden) kuyruklu komut.
        var failing = gate.RunAsync("failing", rec.BlockingThrowingCommand("failing"));
        rec.Release("first");
        rec.WaitForStarted("failing"); // drenaj hata veren komutu işlemeye başladı

        // Drenaj sırasında gelen yeni niyet de işlenmelidir — hata drenajı öldürmez.
        var after = gate.RunAsync("after", rec.InstantCommand("after"));
        rec.Release("failing");
        await Task.WhenAll(first, failing, after);

        // Kuyruklu komut hata verse de drenaj sonraki niyeti işlemeye devam eder
        // (hata veren komut yalnızca Started'a girer, tamamlanamaz).
        Assert.Equal(["first", "failing", "after"], rec.Started);
        Assert.Equal(["first", "after"], rec.Completed);
    }

    [Fact]
    public async Task Dispatch_Delegate_Is_Used_For_Queued_Commands()
    {
        var dispatches = 0;
        var gate = new ConnectionCommandGate(action =>
        {
            dispatches++;
            return action();
        });
        var rec = new CommandRecorder();

        var first = gate.RunAsync("first", rec.BlockingCommand("first"));
        rec.WaitForStarted("first"); // kapı sahibi kesinleşti
        var queued = gate.RunAsync("queued", rec.InstantCommand("queued"));

        rec.Release("first");
        await Task.WhenAll(first, queued);

        Assert.Equal(1, dispatches);
        Assert.Equal(["first", "queued"], rec.Completed);
    }
}