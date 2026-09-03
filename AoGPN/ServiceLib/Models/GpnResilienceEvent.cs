namespace ServiceLib.Models;

/// <summary>GPN direnç (resilience) olayının türü — dashboard'ın gösterdiği karar.</summary>
public enum GpnResilienceAction
{
    /// <summary>WireGuard tüneli başka bir sunucuya taşındı (failover sunucu değişimi).</summary>
    ServerSwitch,

    /// <summary>Aktif tünelin UDP yolu ölü bulundu (ICMP Port Unreachable / katı politikada NoResponse).</summary>
    UdpDeath,

    /// <summary>Mod V2rayTCP'ye düştü (Tier 3 fallback) — tünel öldü, mevcut V2ray düğümüne geçildi.</summary>
    ModeFallback,

    /// <summary>V2rayTCP sonrası sağlıklı sunucu bulundu, Tier 2 (WireGuard) kurtarması.</summary>
    Recover,

    /// <summary>Bağlan sırasında seçilen başlangıç modu kararı (WireGuardUDP / V2rayTCP).</summary>
    ModeDecision,
}

/// <summary>
/// GpnServerSelectionService'in aldığı kararları dashboard'a taşıyan olay kapsülü.
/// AppEvents.GpnResilienceChanged üzerinden yayınlanır; kararın türü, kaynak/hedef
/// sunucu, mod geçişi ve hiçbir ağ çağrısını tekrarlamadan gerekçeyle.
/// </summary>
public sealed record GpnResilienceEvent
{
    public GpnResilienceAction Action { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? ServerId { get; init; }
    public string? ServerName { get; init; }
    public string? TargetServerId { get; init; }
    public string? TargetServerName { get; init; }
    public ConnectionMode FromMode { get; init; }
    public ConnectionMode ToMode { get; init; }
    public string? Reason { get; init; }
    public UdpProbeStatus? UdpStatus { get; init; }
    public int? DelayMs { get; init; }

    public GpnResilienceEvent(
        GpnResilienceAction action,
        ConnectionMode toMode,
        string? reason = null,
        string? serverId = null,
        string? serverName = null,
        string? targetServerId = null,
        string? targetServerName = null,
        ConnectionMode fromMode = ConnectionMode.WireGuardUDP,
        UdpProbeStatus? udpStatus = null,
        int? delayMs = null)
    {
        Action = action;
        ToMode = toMode;
        Reason = reason;
        ServerId = serverId;
        ServerName = serverName;
        TargetServerId = targetServerId;
        TargetServerName = targetServerName;
        FromMode = fromMode;
        UdpStatus = udpStatus;
        DelayMs = delayMs;
        OccurredAt = DateTimeOffset.UtcNow;
    }
}