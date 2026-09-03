namespace ServiceLib.Models;

/// <summary>
/// Failover karar eylemi (dashboard görünümü için kamuya açık sözlük —
/// iç <c>GpnServerSelectionService.FailoverActionType</c>'ın eşlemesi).
/// </summary>
public enum GpnFailoverAction
{
    /// <summary>Değişiklik yok — aktif tünel korunur.</summary>
    None,

    /// <summary>Başka sunucuya geç (TargetServerId).</summary>
    SwitchServer,

    /// <summary>Hiçbir sunucuda sağlıklı UDP yok — V2rayTCP'ye düş.</summary>
    FallbackToV2ray,
}

/// <summary>Failover matrisinin tek sunucu satırı (ölçüm + iki politika altında sağlık).</summary>
public sealed record GpnFailoverMatrixRow(
    string ServerId,
    string Name,
    int PingMs,
    int LossPercent,
    UdpProbeStatus? UdpStatus,
    bool IsActive,
    bool HealthyDefault,
    bool HealthyStrict);

/// <summary>Tek politika altındaki failover kararı (varsayılan / katı yan yana gösterim).</summary>
public sealed record GpnFailoverMatrixPolicy(
    bool NoResponseAsBlocked,
    bool HandshakeNoResponseAsBlocked,
    bool UdpHealthEnabled,
    GpnFailoverAction Action,
    string? TargetServerId,
    string Reason);

/// <summary>
/// Failover karar matrisi: aynı canlı ölçümün (ping + UDP durumları) iki politika
/// altında nasıl yorumlanacağını yan yana gösterir. Karar, GpnServerSelectionService'in
/// saf <c>DecideFailover</c> fonksiyonuyla hesaplanır — dashboard yalnızca görüntüler,
/// karar mantığı çoğaltılmaz.
/// </summary>
public sealed record GpnFailoverMatrix(
    IReadOnlyList<GpnFailoverMatrixRow> Rows,
    GpnFailoverMatrixPolicy DefaultPolicy,
    GpnFailoverMatrixPolicy StrictPolicy,
    DateTimeOffset MeasuredAt);
