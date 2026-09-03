namespace ServiceLib.Models;

/// <summary>
/// GPN diyagnoz satırı (DiagLog'un "GPN_*" çıktısı) — WebView2 dashboard'a
/// canlı akan olay. <see cref="Kind"/>, "GPN_" önekinden sonraki ilk sözcüktür
/// (LOG, RECOVER, SELECT, FAILOVER, LAUNCH...); <see cref="Message"/> ham
/// satırın tamamını taşır (zaman damgası olmadan — satır DiagLog tarafından
/// zaten zaman damgalı yazılır, buradaki TimestampMs akış zamanıdır).
/// </summary>
public sealed record GpnDiagEvent(
    string Kind,
    string Message,
    long TimestampMs);
