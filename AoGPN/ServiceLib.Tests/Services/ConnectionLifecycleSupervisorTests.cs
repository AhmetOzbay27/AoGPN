using System.Threading;
using ServiceLib.Services;

namespace ServiceLib.Tests.Services;

/// <summary>
/// W4-A: ConnectionLifecycleSupervisor karar kuralları — tik sayaçları ve
/// koşullar (drift 15 tikte / bağlantı geçişinde, IP hızlı 3 / yavaş 15 tik,
/// bağlantısızken sayaç sıfırlanır, beklenmedik hata döngüyü bitirir).
/// Gerçek PeriodicTimer yerine elle sürülen tik kaynağı kullanılır, bu yüzden
/// her test deterministiktir (zamanlama yok).
/// </summary>
public class ConnectionLifecycleSupervisorTests
{
    /// <summary>Manuel tik sürücüsü: döngü her beklemede bir TCS kaydeder; test tikleri tek tek serbest bırakır.</summary>
    private sealed class TickDriver
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<bool>> _pending = new();

        public Func<CancellationToken, Task<bool>> Waiter { get; }

        public TickDriver()
        {
            Waiter = async ct =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var reg = ct.Register(() => tcs.TrySetResult(false));
                lock (_gate)
                {
                    _pending.Add(tcs);
                }
                return await tcs.Task.ConfigureAwait(false);
            };
        }

        /// <summary>Bir tiki serbest bırakır ve döngünün o iterasyonu bitirip yenisini istemesini bekler.</summary>
        public async Task RunTickAsync()
        {
            await ReleasePendingAsync();
            await WaitForPendingAsync();
        }

        /// <summary>Bekleyen tik isteğini yalnızca serbest bırakır (döngünün bitmesini beklemeyen senaryolar).</summary>
        public async Task ReleasePendingAsync()
        {
            var tcs = await TakeNextPendingAsync();
            tcs.TrySetResult(true);
        }

        private async Task<TaskCompletionSource<bool>> TakeNextPendingAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        var tcs = _pending[0];
                        _pending.RemoveAt(0);
                        return tcs;
                    }
                }
                await Task.Delay(5);
            }
            throw new TimeoutException("Döngü tik isteğinde bulunmadı");
        }

        private async Task WaitForPendingAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        return;
                    }
                }
                await Task.Delay(5);
            }
            throw new TimeoutException("Döngü bir sonraki tik isteğinde bulunmadı");
        }
    }

    private sealed class Harness
    {
        public bool Connected { get; set; }
        public bool LastTunnelVerified { get; set; }
        public bool ThrowOnRuleDrift { get; set; }
        public int SyncState { get; private set; }
        public int RuleDrift { get; private set; }
        public int CheckIp { get; private set; }
        public int RealityFallback { get; private set; }
        public int TrayStatus { get; private set; }
        public int SystemProxy { get; private set; }
        public int MonitorSnapshot { get; private set; }
        public int NodeInfo { get; private set; }
        public int WindowState { get; private set; }

        public TickDriver Driver { get; } = new();
        private readonly CancellationTokenSource _cts = new();

        public ConnectionLifecycleSupervisor Supervisor { get; }

        public Harness()
        {
            Supervisor = new ConnectionLifecycleSupervisor(
                runOnUiThread: action => action(),
                readConnected: () => Connected,
                readLastTunnelVerified: () => LastTunnelVerified,
                synchronizeConnectionState: () => { SyncState++; return Task.CompletedTask; },
                pushRuleDrift: () =>
                {
                    RuleDrift++;
                    if (ThrowOnRuleDrift)
                    {
                        throw new InvalidOperationException("test hatası");
                    }
                    return Task.CompletedTask;
                },
                checkIp: () => { CheckIp++; return Task.CompletedTask; },
                suggestRealityCoreFallback: () => { RealityFallback++; return Task.CompletedTask; },
                updateTrayStatus: () => { TrayStatus++; return Task.CompletedTask; },
                pushSystemProxyState: () => { SystemProxy++; return Task.CompletedTask; },
                pushMonitorSnapshot: () => { MonitorSnapshot++; return Task.CompletedTask; },
                pushNodeInfo: () => { NodeInfo++; return Task.CompletedTask; },
                synchronizeWindowState: () => { WindowState++; return Task.CompletedTask; },
                waitForNextTick: Driver.Waiter);
        }

        public void Start() => Supervisor.Start(_cts.Token);

        public void Cancel() => _cts.Cancel();

        public async Task AssertStoppedCleanlyAsync()
        {
            await (Supervisor.LifecycleTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DisconnectedIdle_RunsHousekeepingEveryTick_DriftEvery15Ticks_NoIpOrNodePushes()
    {
        var h = new Harness();
        h.Start();

        for (var i = 0; i < 16; i++)
        {
            await h.Driver.RunTickAsync();
        }

        Assert.Equal(16, h.SyncState);
        Assert.Equal(1, h.RuleDrift); // yalnızca 15. tikte
        Assert.Equal(0, h.CheckIp);
        Assert.Equal(0, h.NodeInfo);
        Assert.Equal(16, h.RealityFallback);
        Assert.Equal(16, h.TrayStatus);
        Assert.Equal(16, h.SystemProxy);
        Assert.Equal(16, h.MonitorSnapshot);
        Assert.Equal(16, h.WindowState);

        h.Cancel();
        await h.AssertStoppedCleanlyAsync();
    }

    [Fact]
    public async Task ConnectTransition_FiresDriftAndImmediateIpCheck_ThenFastCadenceEvery3Ticks()
    {
        var h = new Harness();
        h.Start();
        await h.Driver.RunTickAsync(); // tik 1 — bağlı değil
        await h.Driver.RunTickAsync(); // tik 2

        h.Connected = true;
        await h.Driver.RunTickAsync(); // tik 3 — geçiş: drift + anında IP + hızlı aralık
        await h.Driver.RunTickAsync(); // tik 4
        await h.Driver.RunTickAsync(); // tik 5

        // Geçiş tiki: drift hemen, checkIp anında (1) + hızlı aralık tetiklenir (2),
        // düğüm bilgisi başlar. 6. tikte hızlı aralık yeniden ölçer (3).
        Assert.Equal(1, h.RuleDrift);
        Assert.Equal(2, h.CheckIp);
        Assert.Equal(3, h.NodeInfo);

        await h.Driver.RunTickAsync(); // tik 6 — hızlı aralık (ipCheckCounter 2→3)
        Assert.Equal(3, h.CheckIp);
        Assert.Equal(4, h.NodeInfo);

        h.Cancel();
        await h.AssertStoppedCleanlyAsync();
    }

    [Fact]
    public async Task TunnelVerified_SlowsIpChecksFrom3TicksTo15()
    {
        var h = new Harness();
        h.Start();
        h.Connected = true;
        await h.Driver.RunTickAsync(); // geçiş: checkIp ×2, sayaç 0
        await h.Driver.RunTickAsync(); // sayaç 1
        await h.Driver.RunTickAsync(); // sayaç 2

        h.LastTunnelVerified = true;
        await h.Driver.RunTickAsync(); // sayaç 3 → yavaş aralık (15), henüz ölçüm yok
        Assert.Equal(2, h.CheckIp); // geçiş tikindeki iki ölçüm (anında + hızlı aralık)

        // Doğrulama kesinleştikten sonra 15 tike kadar ölçüm yok.
        var afterVerify = h.CheckIp;
        for (var i = 0; i < 8; i++)
        {
            await h.Driver.RunTickAsync();
        }

        Assert.Equal(afterVerify, h.CheckIp);
        Assert.True(h.NodeInfo >= 10, "düğüm bilgisi bağlıyken her tikte gider");

        h.Cancel();
        await h.AssertStoppedCleanlyAsync();
    }

    [Fact]
    public async Task Disconnect_FiresDrift_ResetsIpCounter_StopsNodePushes()
    {
        var h = new Harness();
        h.Start();
        h.Connected = true;
        await h.Driver.RunTickAsync(); // geçiş: drift #1, checkIp ×2, nodeInfo #1
        await h.Driver.RunTickAsync(); // nodeInfo #2, sayaç 1

        h.Connected = false;
        await h.Driver.RunTickAsync(); // geçiş: drift #2, sayaç sıfırlanır

        var checkIpBefore = h.CheckIp;
        for (var i = 0; i < 3; i++)
        {
            await h.Driver.RunTickAsync();
        }

        Assert.Equal(2, h.RuleDrift);
        Assert.Equal(checkIpBefore, h.CheckIp); // bağlantısızken ölçüm yok
        Assert.Equal(2, h.NodeInfo);            // bağlantı kesilince düğüm itmesi durur

        h.Cancel();
        await h.AssertStoppedCleanlyAsync();
    }

    [Fact]
    public async Task UnexpectedFanOutException_StopsTheLoop_AfterLogging()
    {
        var h = new Harness();
        h.Start();
        await h.Driver.RunTickAsync(); // tik 1 sağlam

        h.ThrowOnRuleDrift = true;
        h.Connected = true; // geçiş → drift çağrısı fırlatır
        await h.Driver.ReleasePendingAsync();

        // Döngü bitmeli; yeni tik isteği gelmemeli (LifecycleTask tamamlanır).
        await (h.Supervisor.LifecycleTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, h.SyncState); // durum senkronu çalıştı, drift sonrası döngü durdu
        Assert.Equal(1, h.RuleDrift);
    }

    [Fact]
    public async Task Start_IsIdempotent_SingleLoopRuns()
    {
        var h = new Harness();
        h.Start();
        h.Start();
        var task = h.Supervisor.LifecycleTask;
        h.Start();
        Assert.Same(task, h.Supervisor.LifecycleTask);

        await h.Driver.RunTickAsync();
        Assert.Equal(1, h.SyncState); // tek döngü, tek iterasyon

        h.Cancel();
        await h.AssertStoppedCleanlyAsync();
    }

    [Fact]
    public async Task CancelToken_StopsCleanly_NoFurtherTicks()
    {
        var h = new Harness();
        h.Start();
        await h.Driver.RunTickAsync();
        h.Cancel();

        await (h.Supervisor.LifecycleTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, h.SyncState); // iptal tik tüketir; yeni iterasyon hiç başlamaz
    }
}