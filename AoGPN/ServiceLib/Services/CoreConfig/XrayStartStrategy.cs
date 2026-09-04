namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Xray başlatma stratejisi: standart çekirdek — TUN uygulama tarafından
/// yönetilir, yerel SOCKS5 dinleyicisi + bağlantı sondalarıyla hazır olma
/// doğrulaması yapılır.
/// </summary>
internal sealed class XrayStartStrategy : CoreStartStrategyBase
{
    private static readonly Lazy<XrayStartStrategy> _instance = new(() => new());

    public static XrayStartStrategy Instance => _instance.Value;

    public override bool OwnsTun => false;

    public override bool IsNativeTunnelCore => false;
}