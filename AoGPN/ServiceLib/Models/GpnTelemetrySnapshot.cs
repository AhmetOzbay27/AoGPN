namespace ServiceLib.Models;

/// <summary>
/// GPN failover/kurtarma telemetri sayaçlarının anlık görüntüsü.
/// <see cref="GpnTelemetryService"/> tarafından AppEvents.GpnResilienceChanged
/// akışından derlenir; dashboard'a canlı basılır.
/// </summary>
public sealed record GpnTelemetrySnapshot(
    /// <summary>WireGuard tünelinin başka bir sunucuya taşınma sayısı (failover switch).</summary>
    int ServerSwitches,

    /// <summary>Aktif tünelin UDP yolunun ölü bulunma sayısı (ICMP Port Unreachable).</summary>
    int UdpDeaths,

    /// <summary>Tier 3'e (V2rayTCP) düşme sayısı (mode fallback).</summary>
    int ModeFallbacks,

    /// <summary>V2rayTCP sonrası sağlıklı sunucu bulunarak Tier 2'ye (WireGuard) dönüş sayısı.</summary>
    int Recoveries,

    /// <summary>Bağlan akışında yapılan başlangıç mod kararı sayısı (ör. otomatik sunucu seçimi).</summary>
    int ModeDecisions,

    /// <summary>Toplam kayıtlı olay sayısı (yukarıdaki beş kategorinin toplamı).</summary>
    int TotalEvents,

    /// <summary>Sayaçların başladığı an (son sıfırlamadan bu yana ölçülür).</summary>
    DateTimeOffset StartedAt);