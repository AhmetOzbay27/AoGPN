namespace ServiceLib.Services.CoreConfig;

using ServiceLib.Services.CoreConfig.Mihomo;

/// <summary>
/// mihomo başlatma stratejisi: mihomo kendi Wintun adaptörünü + rotalarını
/// yönetir (own TUN) — uygulamanın TunLifecycleManager'ı ve host rotası
/// mihomo için ayrı çalışır. Başlatma öncesi WG sunucu IP'si için /32 host
/// rotası kurulur (wg-quick deseni); kapanışta geri alınır.
/// </summary>
internal sealed class MihomoStartStrategy : CoreStartStrategyBase
{
    private static readonly Lazy<MihomoStartStrategy> _instance = new(() => new());

    public static MihomoStartStrategy Instance => _instance.Value;

    public override bool OwnsTun => true;

    public override bool IsNativeTunnelCore => false;

    public override Task BeforeStartAsync(CoreConfigContext context)
    {
        // mihomo: WG sunucu IP'si için /32 host rotası (wg-quick deseni) —
        // el sıkışma paketleri mihomo'nun /1 TUN rotasına asla dönmez.
        // Rota, kapanışta AfterStopAsync içinde kaldırılır.
        MihomoTunSupport.EnsureHostRoute(context.Node.Address);
        return Task.CompletedTask;
    }

    public override Task AfterStopAsync()
    {
        // mihomo WG host rotasını geri al (idempotent — kurulmamışsa no-op).
        MihomoTunSupport.RemoveHostRoute();
        return Task.CompletedTask;
    }
}