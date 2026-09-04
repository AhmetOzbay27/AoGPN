namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Özel stratejisi olmayan çekirdekler için varsayılan başlatma stratejisi
/// (v2fly, hysteria, tuic, naiveproxy, mieru, ...): TUN uygulama tarafından
/// yönetilir, SOCKS5 hazır olma doğrulaması yapılır.
///
/// openvpn tek istisnadır: OS tüneline sahiptir ve yerel SOCKS5 dinleyicisi
/// yoktur — fabrika onu <see cref="NativeTunnelInstance"/> ile eşler.
/// </summary>
internal sealed class DefaultCoreStartStrategy : CoreStartStrategyBase
{
    private readonly bool _isNativeTunnelCore;

    /// <summary>Standart politika (openvpn dışındaki tüm çekirdekler).</summary>
    public static DefaultCoreStartStrategy Instance { get; } = new(isNativeTunnelCore: false);

    /// <summary>Native-tunnel politikası (openvpn).</summary>
    public static DefaultCoreStartStrategy NativeTunnelInstance { get; } = new(isNativeTunnelCore: true);

    public DefaultCoreStartStrategy()
        : this(isNativeTunnelCore: false)
    {
    }

    private DefaultCoreStartStrategy(bool isNativeTunnelCore)
    {
        _isNativeTunnelCore = isNativeTunnelCore;
    }

    public override bool OwnsTun => false;

    public override bool IsNativeTunnelCore => _isNativeTunnelCore;
}