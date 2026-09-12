// ─────────────────────────────────────────────────────────────────────────
// ConnectionLifecycleSupervisor — 2 sn'lik bağlantı-yaşam döngüsü poll'u (W4-A)
//
// MainWindow.xaml.cs içindeki ConnectionLifecycleLoopAsync + StartTelemetryLoop
// buraya taşındı (TrayWindowCoordinator deseni): tek ctor, tüm çıkışlar delege.
// Poll, WebView2 köprüsünden gelmeyen mod değişikliklerini (oyun tetikleyici,
// yerli akışlar) yakalar ve canlı yüzeylere fan-out yapar:
//   • durum senkronu (her tik) + kural kayması sağlık kontrolü (~30 sn /
//     bağlantı anında hemen — atlanan reload'u gizleyemez)
//   • bağlanma anında bayat IP ölçümünün hemen yeniden ölçülmesi (hızlı aralık),
//     doğrulama kesinleşince 30 sn'ye seyrelme
//   • REALITY çekirdek düşüşü önerisi, tepsi durumu, sistem proxy, monitor
//     anlık görüntüsü, düğüm bilgisi, pencere durumu senkronu
// Hız ayarları (tik aralığı, drift/IP tik sayıları) ctor parametresidir → test
// edilebilir; WaitForNextTickAsync delege'si testlerde elle sürülen deterministik
// bir tik kaynağıyla değiştirilebilir.
// ─────────────────────────────────────────────────────────────────────────

namespace ServiceLib.Services;

/// <summary>
/// Bağlantı-yaşam döngüsü poll'unun sahibi. Gövde MainWindow'dan byte-exact
/// taşındı; Dispatcher sarmalayıcıları <see cref="_runOnUiThread"/> delege'sine,
/// durum okumaları <see cref="_readConnected"/> / <see cref="_readLastTunnelVerified"/>
/// delege'lerine mekanik olarak yönlendirildi. Karar kuralları (drift tik sayacı,
/// IP hızlı/yavaş aralığı, bağlantı geçişinde anında ölçüm) değişmedi.
/// </summary>
public sealed class ConnectionLifecycleSupervisor
{
    private readonly TimeSpan _tickInterval;
    private readonly int _driftEveryTicks;
    private readonly int _ipFastTicks;
    private readonly int _ipSlowTicks;
    private readonly Func<Func<Task>, Task> _runOnUiThread;
    private readonly Func<bool> _readConnected;
    private readonly Func<bool> _readLastTunnelVerified;
    private readonly Func<Task> _synchronizeConnectionState;
    private readonly Func<Task> _pushRuleDrift;
    private readonly Func<Task> _checkIp;
    private readonly Func<Task> _suggestRealityCoreFallback;
    private readonly Func<Task> _updateTrayStatus;
    private readonly Func<Task> _pushSystemProxyState;
    private readonly Func<Task> _pushMonitorSnapshot;
    private readonly Func<Task> _pushNodeInfo;
    private readonly Func<Task> _synchronizeWindowState;
    private readonly Func<CancellationToken, Task<bool>>? _waitForNextTick;

    private Task? _lifecycleTask;

    public ConnectionLifecycleSupervisor(
        Func<Func<Task>, Task> runOnUiThread,
        Func<bool> readConnected,
        Func<bool> readLastTunnelVerified,
        Func<Task> synchronizeConnectionState,
        Func<Task> pushRuleDrift,
        Func<Task> checkIp,
        Func<Task> suggestRealityCoreFallback,
        Func<Task> updateTrayStatus,
        Func<Task> pushSystemProxyState,
        Func<Task> pushMonitorSnapshot,
        Func<Task> pushNodeInfo,
        Func<Task> synchronizeWindowState,
        TimeSpan? tickInterval = null,
        int driftEveryTicks = 15,
        int ipFastTicks = 3,
        int ipSlowTicks = 15,
        Func<CancellationToken, Task<bool>>? waitForNextTick = null)
    {
        _runOnUiThread = runOnUiThread ?? throw new ArgumentNullException(nameof(runOnUiThread));
        _readConnected = readConnected ?? throw new ArgumentNullException(nameof(readConnected));
        _readLastTunnelVerified = readLastTunnelVerified ?? throw new ArgumentNullException(nameof(readLastTunnelVerified));
        _synchronizeConnectionState = synchronizeConnectionState ?? throw new ArgumentNullException(nameof(synchronizeConnectionState));
        _pushRuleDrift = pushRuleDrift ?? throw new ArgumentNullException(nameof(pushRuleDrift));
        _checkIp = checkIp ?? throw new ArgumentNullException(nameof(checkIp));
        _suggestRealityCoreFallback = suggestRealityCoreFallback ?? throw new ArgumentNullException(nameof(suggestRealityCoreFallback));
        _updateTrayStatus = updateTrayStatus ?? throw new ArgumentNullException(nameof(updateTrayStatus));
        _pushSystemProxyState = pushSystemProxyState ?? throw new ArgumentNullException(nameof(pushSystemProxyState));
        _pushMonitorSnapshot = pushMonitorSnapshot ?? throw new ArgumentNullException(nameof(pushMonitorSnapshot));
        _pushNodeInfo = pushNodeInfo ?? throw new ArgumentNullException(nameof(pushNodeInfo));
        _synchronizeWindowState = synchronizeWindowState ?? throw new ArgumentNullException(nameof(synchronizeWindowState));
        _tickInterval = tickInterval ?? TimeSpan.FromSeconds(2);
        _driftEveryTicks = driftEveryTicks;
        _ipFastTicks = ipFastTicks;
        _ipSlowTicks = ipSlowTicks;
        _waitForNextTick = waitForNextTick;
    }

    /// <summary>
    /// Poll'u başlatır (idempotent — ikinci çağrı hiçbir şey yapmaz). Task.Run
    /// üzerinde çalışır; tüm UI dokunuşları <see cref="_runOnUiThread"/>'den
    /// geçtiği için pencere iş parçacığına hiç dokunmaz.
    /// </summary>
    public void Start(CancellationToken cancellationToken)
    {
        if (_lifecycleTask is not null)
        {
            return;
        }

        _lifecycleTask = Task.Run(
            () => RunAsync(cancellationToken),
            cancellationToken);
    }

    /// <summary>Test kapsamı: döngünün sonlandığını gözlemlemek için.</summary>
    internal Task? LifecycleTask => _lifecycleTask;

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_tickInterval);
        var ipCheckCounter = 0;
        var driftCheckCounter = 0;
        var lastPublishedConnectionState = false;

        try
        {
            while (await WaitForNextTickAsync(timer, cancellationToken).ConfigureAwait(false))
            {
                // Polling also catches mode changes made by the game-trigger/native
                // flows, which do not originate in the WebView2 message bridge.
                await _runOnUiThread(_synchronizeConnectionState).ConfigureAwait(false);

                // Stale-rule health check: run immediately when the tunnel comes up
                // (so a connect that skipped the reload cannot hide drift) and then
                // every ~30 s (15 × 2 s ticks) while the app is alive, so rules edited
                // in the routing settings without a reload are surfaced before they
                // cause failures.
                var connectedNow = _readConnected();
                if (connectedNow != lastPublishedConnectionState || ++driftCheckCounter >= _driftEveryTicks)
                {
                    driftCheckCounter = 0;
                    await _runOnUiThread(_pushRuleDrift).ConfigureAwait(false);
                }

                // Bağlantı anı: tünel daha hazır değilken alınmış bayat IP ölçümünü
                // ("sızıntı" yanlış uyarısı) bir sonraki tick'te (≈2 sn) yeniden ölç.
                if (connectedNow && !lastPublishedConnectionState)
                {
                    ipCheckCounter = _ipFastTicks; // hızlı aralığı tetikle
                    await _runOnUiThread(_checkIp).ConfigureAwait(false);
                }
                lastPublishedConnectionState = connectedNow;

                // REALITY nodes that fail on the sing-box core (its hardcoded 1.8.1
                // handshake claim is rejected by modern 3x-ui servers) fall back to
                // the Xray core automatically when TUN is off, or get an explanatory
                // notice when TUN blocks the switch.
                await _runOnUiThread(_suggestRealityCoreFallback).ConfigureAwait(false);

                // Keep the tray status line (connection / proxy-only / idle) live even
                // when the window is hidden to the tray.
                await _runOnUiThread(_updateTrayStatus).ConfigureAwait(false);

                // Surface server switches and proxy changes made outside the WebView2
                // bridge (native lists, tray flows, hotkeys) while the app is alive.
                await _runOnUiThread(_pushSystemProxyState).ConfigureAwait(false);

                await _runOnUiThread(_pushMonitorSnapshot).ConfigureAwait(false);

                if (_readConnected())
                {
                    await _runOnUiThread(_pushNodeInfo).ConfigureAwait(false);

                    // IP panelini periyodik YENİDEN ölç: doğrulanana dek hızlı
                    // (3 tick ≈ 6 sn — bağlanma anındaki bayat ölçümün "sızıntı"
                    // uyarısı saniyeler içinde düzeltilir), doğrulama kesinleşince
                    // 30 sn'ye seyrel. Orta oturum sızıntılarını da yakalar: çöken
                    // TUN sürücüsü, sessizce yeniden başlayan proxy, core çıkışı.
                    ipCheckCounter++;
                    var ipFastTicks = _readLastTunnelVerified() ? _ipSlowTicks : _ipFastTicks;
                    if (ipCheckCounter >= ipFastTicks)
                    {
                        ipCheckCounter = 0;
                        await _runOnUiThread(_checkIp).ConfigureAwait(false);
                    }
                }
                else
                {
                    ipCheckCounter = 0;
                }

                // Keep the title bar maximize/restore icon in sync with the real
                // window state (Win+Up/Down, snap, drag-to-top maximize).
                await _runOnUiThread(_synchronizeWindowState).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN connection lifecycle poll stopped unexpectedly", ex);
        }
    }

    private Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        => _waitForNextTick is null
            ? timer.WaitForNextTickAsync(cancellationToken).AsTask()
            : _waitForNextTick(cancellationToken);
}