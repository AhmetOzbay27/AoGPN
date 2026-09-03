using System.Threading;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// IWireGuardTransport — WireGuard veri düzlemi dikişi (Faz 2b → 2d)
//
// WireGuardTunnelService'in yakalama köprüsü her paketi şu hattan geçirir:
//
//   DivertWorker kanalı → Encrypt(paket) → SendAsync(wire) → UDP → WG sunucusu
//   WG sunucusundan gelen tip-4 → RunReceiveLoopAsync → Decrypt → consumer:
//       Wintun adaptörüne enjekte (OS → oyuna dönüş)
//
// Gerçek uygulama WireGuardNoiseTransport'tur — el sıkışma durum makinesi +
// oturum anahtarı türetme + ChaCha20-Poly1305 taşıma katmanı (Faz 2c) + KALICI
// UDP veri yolu (Faz 2d: ConnectAsync sonrası soket canlı kalır, tip-4 mesajlar
// sunucuya gider/gelir). NoopWireGuardTransport yalnızca DI varsayılanı ve
// sürücüsüz/sunucusuz testler için passthrough iskelettir.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// WireGuard taşıma dikişi: <see cref="Encrypt"/> tünel içine giden paketi,
/// <see cref="Decrypt"/> tünelden gelen paketi işler; Faz 2d'de <see cref="SendAsync"/>
/// şifrelenmiş tip-4 mesajı GERÇEK sunucuya gönderir, <see cref="RunReceiveLoopAsync"/>
/// sunucudan gelen tip-4'leri çözüp tüketiciye verir. null dönüşü = paket
/// işlenemedi (kaynak anahtar yok vb.) — köprü paketi atlar, sayaç tutar.
/// </summary>
public interface IWireGuardTransport
{
    /// <summary>Tünel içine gidecek paketi şifreler (kaynak paketi değiştirmez).</summary>
    byte[]? Encrypt(ReadOnlySpan<byte> packet);

    /// <summary>Tünelden gelen paketi çözer.</summary>
    byte[]? Decrypt(ReadOnlySpan<byte> packet);

    /// <summary>Encrypt çağrı sayısı (telemetri).</summary>
    long EncryptedCount { get; }

    /// <summary>Decrypt çağrı sayısı (telemetri).</summary>
    long DecryptedCount { get; }

    /// <summary>Çözülemeyen (bozuk/tekrar/anahtar yok) mesaj sayısı (telemetri).</summary>
    long DecryptFailedCount { get; }

    /// <summary>Faz 2d — oturum + UDP soketi canlı mı (veri yolu kullanılabilir mi).</summary>
    bool IsDataPathActive { get; }

    /// <summary>Sunucuya gönderilen tip-4 mesaj sayısı (telemetri).</summary>
    long SentCount { get; }

    /// <summary>Gönderilemeyen (oturum/soket yok, soket hatası) tip-4 mesaj sayısı (telemetri).</summary>
    long SendFailedCount { get; }

    /// <summary>
    /// Faz 2d — şifrelenmiş tip-4 mesajı gerçek sunucuya gönderir. Veri yolu
    /// kapalıysa (oturum/soket yok) veya gönderim başarısızsa false.
    /// </summary>
    ValueTask<bool> SendAsync(byte[] wirePacket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Faz 2d — sunucudan gelen tip-4 mesajları UDP üzerinden alır, çözer ve
    /// tüketiciye verir (çözülmüş IP paketi). İptalde veya soket kapanınca temiz biter.
    /// </summary>
    Task RunReceiveLoopAsync(
        Func<byte[], CancellationToken, ValueTask> consumer,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Varsayılan taşıma: paketi olduğu gibi geçirir (şifrelemesiz iskelet). Veri yolu
/// her zaman "aktif" kabul edilir (Send sayaçlanır, alım döngüsü boştur) — köprünün
/// uçtan uca akışını sürücüsüz/sunucusuz doğrulamayı mümkün kılar.
/// </summary>
public sealed class NoopWireGuardTransport : IWireGuardTransport
{
    private long _encrypted;
    private long _decrypted;
    private long _sent;

    public long EncryptedCount => Interlocked.Read(ref _encrypted);
    public long DecryptedCount => Interlocked.Read(ref _decrypted);
    public long DecryptFailedCount => 0;
    public long SentCount => Interlocked.Read(ref _sent);
    public long SendFailedCount => 0;
    public bool IsDataPathActive => true;

    public byte[]? Encrypt(ReadOnlySpan<byte> packet)
    {
        Interlocked.Increment(ref _encrypted);
        return packet.ToArray();
    }

    public byte[]? Decrypt(ReadOnlySpan<byte> packet)
    {
        Interlocked.Increment(ref _decrypted);
        return packet.ToArray();
    }

    public ValueTask<bool> SendAsync(byte[] wirePacket, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _sent);
        return ValueTask.FromResult(true);
    }

    public Task RunReceiveLoopAsync(
        Func<byte[], CancellationToken, ValueTask> consumer,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
        => Task.CompletedTask; // passthrough iskelette alım yolu yok
}
