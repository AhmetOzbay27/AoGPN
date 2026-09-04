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
        SystemProxyOnlyService proxyOnlyService,
        Func<MainWindowViewModel?> getViewModel)
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

        return services;
    }
}
