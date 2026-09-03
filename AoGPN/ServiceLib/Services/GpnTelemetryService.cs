using ServiceLib.Events;
using ServiceLib.Models;

namespace ServiceLib.Services;

/// <summary>
/// GPN failover/kurtarma olaylarını sayan telemetri sayacı.
///
/// <see cref="AppEvents.GpnResilienceChanged"/> akışını dinler ve sunucu değişimi
/// (ServerSwitch), UDP ölümü (UdpDeath), Tier-3 düşüşü (ModeFallback) ve Tier-2
/// kurtarmasını (Recover) sayar. Kurtarma döngüsü dahildir: RunFailoverMonitorAsync
/// V2rayTCP düşüşünün ardından sağlıklı sunucu bulunca Recover olayı yayınlar ve bu
/// servis onu kaydeder. Başlangıç seçimleri (ModeDecision) de ayrı sayaç olarak tutulur.
///
/// Thread-safe; olaylar düşük frekansta geldiği için lock tabanlıdır. Dashboard
/// <see cref="Snapshot"/> okuyup her olaydan sonra güncel değerleri yansıtır.
/// </summary>
public sealed class GpnTelemetryService : IDisposable
{
    private readonly object _lock = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly IDisposable _subscription;

    private int _serverSwitches;
    private int _udpDeaths;
    private int _modeFallbacks;
    private int _recoveries;
    private int _modeDecisions;

    public GpnTelemetryService()
        : this(AppEvents.GpnResilienceChanged)
    {
    }

    /// <summary>
    /// Test desteği: sayaçları izole bir kanala bağlar (global AppEvents akışından
    /// paralel test gürültüsü almaz). Üretim her zaman <see cref="AppEvents.GpnResilienceChanged"/> kullanır.
    /// </summary>
    internal GpnTelemetryService(EventChannel<GpnResilienceEvent> source)
    {
        _subscription = source.AsObservable()
            .Subscribe(OnResilienceEvent);
    }

    private void OnResilienceEvent(GpnResilienceEvent evt)
    {
        lock (_lock)
        {
            switch (evt.Action)
            {
                case GpnResilienceAction.ServerSwitch:
                    _serverSwitches++;
                    break;
                case GpnResilienceAction.UdpDeath:
                    _udpDeaths++;
                    break;
                case GpnResilienceAction.ModeFallback:
                    _modeFallbacks++;
                    break;
                case GpnResilienceAction.Recover:
                    _recoveries++;
                    break;
                case GpnResilienceAction.ModeDecision:
                    _modeDecisions++;
                    break;
                default:
                    break;
            }
        }
    }

    public GpnTelemetrySnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                var total = _serverSwitches + _udpDeaths + _modeFallbacks + _recoveries + _modeDecisions;
                return new GpnTelemetrySnapshot(
                    ServerSwitches: _serverSwitches,
                    UdpDeaths: _udpDeaths,
                    ModeFallbacks: _modeFallbacks,
                    Recoveries: _recoveries,
                    ModeDecisions: _modeDecisions,
                    TotalEvents: total,
                    StartedAt: _startedAt);
            }
        }
    }

    /// <summary>Tüm sayaçları sıfırlar (oturum başlangıcı damgasını yeniden kurar).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _serverSwitches = 0;
            _udpDeaths = 0;
            _modeFallbacks = 0;
            _recoveries = 0;
            _modeDecisions = 0;
        }
    }

    public void Dispose() => _subscription.Dispose();
}