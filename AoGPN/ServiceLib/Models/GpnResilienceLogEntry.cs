namespace ServiceLib.Models;

/// <summary>
/// Döngüsel karar günlüğü kaydı (son 50 GpnResilienceEvent). Dashboard tarafından
/// sorun giderme amaçlı gösterilir; her kayıt ham olay alanlarıyla birlikte
/// önceden biçimlendirilmiş tek satırlık <see cref="Line"/> taşır.
/// </summary>
public sealed record GpnResilienceLogEntry(
    long TimestampMs,
    GpnResilienceAction Action,
    string? ServerId,
    string? ServerName,
    string? TargetServerId,
    string? TargetServerName,
    ConnectionMode FromMode,
    ConnectionMode ToMode,
    string? Reason,
    string Line);
