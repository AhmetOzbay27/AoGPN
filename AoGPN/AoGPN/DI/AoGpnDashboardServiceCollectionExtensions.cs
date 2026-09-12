using AoGPN.Services;
using Microsoft.Extensions.DependencyInjection;
using ServiceLib.Services;

namespace AoGPN.DI;

/// <summary>
/// P0 Faz 2 (2. Dalga) dashboard iş mantığı servislerinin DI kayıtları.
///
/// MainWindow.xaml.cs'ten ayrılan DashboardSettingsService ve
/// DashboardGpnServerService singleton olarak kaydedilir. Her iki servis de
/// UI kanalına yalnızca ctor delegeleriyle dokunur (script yürütme, hazır
/// bayrağı, transport/ViewModel okuyucuları) — bu delegeler composition
/// root'ta MainWindow'un kendi primitiflerine bağlanır.
///
/// Uygulama şu an singleton tabanlı (Lazy&lt;T&gt;) manuel kompozisyon kullanıyor;
/// MainWindow servisleri doğrudan kurar (ServiceLib.DI'daki GPN kayıtlarıyla
/// aynı desen). Bu uzantı, gerçek DI composition root'una geçildiğinde tek
/// satırla bağlanmak üzere hazırdır ve ServiceLib.Tests'teki DI çözümleme
/// testleriyle aynı şekilde doğrulanabilir.
/// </summary>
public static class AoGpnDashboardServiceCollectionExtensions
{
    /// <summary>
    /// Dashboard servislerini kaydeder. Delegeler MainWindow'un yaşam döngüsüne
    /// bağlı olduğu için (ExecuteScriptSafelyAsync — dispatcher marshal'ı,
    /// _webViewReady, ReadTransport, _proxyOnlyService, ViewModel) burada
    /// composition root tarafından verilir.
    /// </summary>
    public static IServiceCollection AddAoGpnDashboardServices(
        this IServiceCollection services,
        Func<string, Task> executeScript,
        Func<bool> isWebViewReady,
        Func<string> readTransport,
        Func<bool> isClosing,
        Func<string, Task> notifyNodesOp,
        Action<Action> invokeOnUiThread,
        Func<bool, Task> pushSystemProxyState,
        Action updateTrayStatus,
        Func<bool> readActualConnectionState,
        Func<string> getActiveView,
        Func<CancellationToken> getWebViewToken,
        Func<GpnBypassEgressController?> getBypassEgressController,
        SystemProxyOnlyService proxyOnlyService,
        Func<MainWindowViewModel?> getViewModel,
        Func<Func<Task>, Task> runOnUiThread,
        Func<bool> readConnected,
        Func<bool> readLastTunnelVerified,
        Func<Task> synchronizeConnectionState,
        Func<Task> checkIp,
        Func<Task> suggestRealityCoreFallback,
        Func<Task> updateTrayStatusAsync,
        Func<Task> synchronizeWindowState)
    {
        ArgumentNullException.ThrowIfNull(services);

        // DashboardSettingsService — Ayarlar formu (read/persist/push) iş mantığı.
        // Singleton: config yazma akışı süreç boyunca tek örnek üzerinde çalışır.
        services.AddSingleton(sp => new DashboardSettingsService(
            executeScript,
            isWebViewReady,
            readTransport,
            proxyOnlyService));

        // DashboardGpnServerService — gpn_servers kataloğu (liste/ölçüm/CRUD).
        // Singleton: dashboard sunucu listesi tek örnek üzerinden beslenir.
        services.AddSingleton(sp => new DashboardGpnServerService(
            executeScript,
            isWebViewReady,
            getViewModel));

        // DashboardNodeService — düğüm/profil yönetimi (seçim, CRUD, havuz,
        // favori, ping testi, liste yayını). Singleton: düğüm kümesi durumu
        // (_nodeSpeedtestService, _nodeTestRunId, _lastNodeSignature) süreç
        // boyunca tek örnek üzerinde yaşar. Ctor delegeleri MainWindow'un
        // kendi primitiflerine bağlanır (toast kanalı NotifyNodesOpAsync,
        // Dispatcher.InvokeAsync, tepsi durumu vb.).
        services.AddSingleton(sp => new DashboardNodeService(
            executeScript,
            isWebViewReady,
            isClosing,
            notifyNodesOp,
            () => getViewModel()?.ProfilesViewModel,
            getViewModel,
            invokeOnUiThread,
            proxyOnlyService,
            pushSystemProxyState,
            updateTrayStatus));

        // DashboardPushService — egress push kümesi (telemetri/direnç/izleyici/
        // kayma itmeleri). Singleton: anlık görüntü + karar durumu (_lastCaptureStats,
        // _lastRuleDriftVerdict, _gpnPidBridge) tek örnek üzerinde yaşar. Delegeler
        // MainWindow'un kendi primitiflerine bağlanır (ConnectionViewModel okuma,
        // transport/aktif görünüm okuyucuları, WebView2 ömür token'ı, WARP degrade
        // denetleyicisi — WhenActivated'ta kurulduğu için tembel okunur).
        services.AddSingleton(sp => new DashboardPushService(
            executeScript,
            isWebViewReady,
            () => getViewModel()?.ConnectionViewModel,
            readTransport,
            readActualConnectionState,
            getActiveView,
            getWebViewToken,
            getBypassEgressController));

        // ConnectionLifecycleSupervisor — 2 sn'lik bağlantı-yaşam döngüsü poll'u
        // (W4-A). Fan-out hedeflerinin dördü bu koleksiyonda kayıtlı dashboard
        // servislerine gider; kalan çıktılar ctor delege parametreleridir. Karar
        // kuralları (drift/IP tik sayacı) servisin kendi ctor parametreleriyle
        // test edilir (ServiceLib.Tests).
        services.AddSingleton(sp =>
        {
            var push = sp.GetRequiredService<DashboardPushService>();
            var node = sp.GetRequiredService<DashboardNodeService>();
            var settings = sp.GetRequiredService<DashboardSettingsService>();
            return new ConnectionLifecycleSupervisor(
                runOnUiThread,
                readConnected,
                readLastTunnelVerified,
                synchronizeConnectionState,
                () => push.PushRuleDriftAsync(),
                checkIp,
                suggestRealityCoreFallback,
                updateTrayStatusAsync,
                () => settings.PushSystemProxyStateAsync(),
                () => push.PushMonitorSnapshotAsync(),
                () => node.PushNodeInfoAsync(),
                synchronizeWindowState);
        });

        return services;
    }
}
