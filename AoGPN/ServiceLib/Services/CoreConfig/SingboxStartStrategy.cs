namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// sing-box başlatma stratejisi: standart çekirdek — TUN uygulama tarafından
/// yönetilir (TunLifecycleManager), yerel SOCKS5 dinleyicisi + bağlantı
/// sondalarıyla hazır olma doğrulaması yapılır.
/// </summary>
internal sealed class SingboxStartStrategy : CoreStartStrategyBase
{
    private static readonly Lazy<SingboxStartStrategy> _instance = new(() => new());

    public static SingboxStartStrategy Instance => _instance.Value;

    public override bool OwnsTun => false;

    public override bool IsNativeTunnelCore => false;
}