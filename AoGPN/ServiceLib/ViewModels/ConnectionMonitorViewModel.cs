using System.Reactive.Concurrency;

namespace ServiceLib.ViewModels;

public class ConnectionMonitorViewModel : MyReactiveObject
{
    public IObservableCollection<ConnectionMonitorItem> Connections { get; } = new ObservableCollectionExtended<ConnectionMonitorItem>();
    public IObservableCollection<TrafficMonitorItem> TrafficItems { get; } = new ObservableCollectionExtended<TrafficMonitorItem>();
    public IObservableCollection<TrafficMonitorItem> AppTrafficItems { get; } = new ObservableCollectionExtended<TrafficMonitorItem>();
    public IObservableCollection<CountryAggregateItem> CountryItems { get; } = new ObservableCollectionExtended<CountryAggregateItem>();

    [Reactive]
    public ConnectionMonitorItem? SelectedItem { get; set; }

    [Reactive]
    public int TrafficTabIndex { get; set; } = 0;

    [Reactive]
    public string TrafficStatus { get; set; } = "";

    [Reactive]
    public int ActiveConnectionCount { get; set; }

    [Reactive]
    public int ActiveAppCount { get; set; }

    [Reactive]
    public int ActiveCountryCount { get; set; }

    [Reactive]
    public string TotalDownloadText { get; set; } = "0.0 B";

    [Reactive]
    public string TotalUploadText { get; set; } = "0.0 B";

    [Reactive]
    public TrafficMonitorItem? SelectedTrafficItem { get; set; }

    [Reactive]
    public TrafficMonitorItem? SelectedAppTrafficItem { get; set; }

    [Reactive]
    public string TrafficFilter { get; set; } = "";

    public ReactiveCommand<Unit, Unit> ClearTrafficCmd { get; }

    [Reactive]
    public string Filter { get; set; } = "";

    [Reactive]
    public bool AutoRefresh { get; set; } = true;

    [Reactive]
    public bool HideListeners { get; set; } = true;

    public ReactiveCommand<Unit, Unit> RefreshCmd { get; }

    // P0 Nihai Faz — veri motoru (OS tablo + yönlendirme + /connections trafiği)
    // DashboardConnectionEngine'de yaşar; bu sınıf yalnızca binding koleksiyonlarına
    // kopyalar. Anlık görüntüler her zaman UI iş parçacığında uygulanır.
    private readonly DashboardConnectionEngine _engine = new();

    public ConnectionMonitorViewModel()
    {
        RefreshCmd = ReactiveCommand.CreateFromTask(async () => await RefreshAsync());
        ClearTrafficCmd = ReactiveCommand.Create(() =>
        {
            TrafficItems.Clear();
            AppTrafficItems.Clear();
            ActiveConnectionCount = 0;
            ActiveAppCount = 0;
            TotalDownloadText = "0.0 B";
            TotalUploadText = "0.0 B";
            TrafficStatus = ResUI.MonitorTrafficCleared;
        });
        _ = RefreshAsync();
        _ = _engine.RunLoopAsync(GetOptions, ShouldRefresh, ApplySnapshot);
    }

    /// <summary>UI durumundan bir izleme isteği üretir (tarama/loop iş parçacığında okunur).</summary>
    private MonitorRequestOptions GetOptions()
    {
        return new MonitorRequestOptions(Filter ?? "", TrafficFilter ?? "", HideListeners);
    }

    private bool ShouldRefresh() => AutoRefresh && AppManager.Instance.ShowInTaskbar;

    /// <summary>Motor anlık görüntüsünü binding koleksiyonlarına kopyalar (UI iş parçacığı).</summary>
    private void ApplySnapshot(ConnectionMonitorSnapshot snapshot)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            Connections.Clear();
            Connections.AddRange(snapshot.Items);
            ActiveConnectionCount = snapshot.Items.Count;
            ActiveAppCount = snapshot.Items.Select(x => x.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            CountryItems.Clear();
            CountryItems.AddRange(snapshot.Countries);
            ActiveCountryCount = snapshot.Countries.Count;
            TrafficItems.Clear();
            TrafficItems.AddRange(snapshot.ByType);
            AppTrafficItems.Clear();
            AppTrafficItems.AddRange(snapshot.ByApp);
            TotalDownloadText = Utils.HumanFy(snapshot.Download);
            TotalUploadText = Utils.HumanFy(snapshot.Upload);
            TrafficStatus = snapshot.Status;
        });
    }

    /// <summary>Bir izleme tikini hemen çalıştırır (loop'tan bağımsız — manuel yenileme/RefreshCmd).</summary>
    public async Task RefreshAsync()
    {
        var snapshot = await _engine.RefreshAsync(GetOptions());
        ApplySnapshot(snapshot);
    }

    /// <summary>Forces the next refresh to reload the routing rules (called after an apply).</summary>
    public void InvalidateRoutingCache()
    {
        _engine.InvalidateRoutingCache();
    }
}
