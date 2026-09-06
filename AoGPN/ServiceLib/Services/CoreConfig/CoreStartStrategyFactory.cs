namespace ServiceLib.Services.CoreConfig;

/// <summary>
/// Çekirdek tipine göre başlatma stratejisini seçer (Factory).
///
/// CoreManager, bağlamın <c>RunCoreType</c>'ına göre buradan strateji çözer:
/// mihomo → own-TUN + host rotası; sing-box / Xray → standart; openvpn →
/// native-tunnel varsayılanı; diğer tüm çekirdekler → standart varsayılan.
/// Bağlam duyarlı <see cref="For(CoreConfigContext)"/> aşırı yüklemesi, yerel
/// native GPN motoru (WinDivert + WireGuard + Wintun) isteyen WireGuard
/// bağlamlarını NativeGpnStartStrategy'ye yönlendirir (dormant yuva).
/// </summary>
public static class CoreStartStrategyFactory
{
    public static ICoreStartStrategy For(ECoreType coreType) => coreType switch
    {
        ECoreType.mihomo => MihomoStartStrategy.Instance,
        ECoreType.Xray => XrayStartStrategy.Instance,
        ECoreType.openvpn => DefaultCoreStartStrategy.NativeTunnelInstance,
        _ => DefaultCoreStartStrategy.Instance,
    };

    /// <summary>
    /// Bağlam duyarlı çözüm: bağlam YEREL native GPN motoru istiyorsa
    /// (UseNativeGpnEngine == true VE düğüm EConfigType.WireGuard) süreç tabanlı
    /// strateji yerine <see cref="NativeGpnStartStrategy"/> döner; aksi halde
    /// <see cref="For(ECoreType)"/> ile birebir aynı eşleme. Bayrak varsayılan
    /// false olduğundan davranış bugün değişmez — CoreManager'ın üç çözüm noktası
    /// (LoadCore / BeginLifecycle / RecoverMainCoreAsync) bu aşırı yüklemeyi kullanır.
    /// </summary>
    public static ICoreStartStrategy For(CoreConfigContext context)
        => context.UseNativeGpnEngine && context.Node.ConfigType == EConfigType.WireGuard
            ? NativeGpnStartStrategy.Instance
            : For(context.RunCoreType);
}