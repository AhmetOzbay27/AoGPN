namespace ServiceLib.Services;

/// <summary>
/// "Bu bağlanma isteği mevcut tünel tarafından zaten karşılanıyor mu?" kararı.
///
/// Neden gerekli (canlı ölçüm, Eylül 2026): <see cref="GpnConnectionCoordinator.ConnectAsync"/>
/// çağrısı her seferinde tüneli durdurup (StopAsync) tüm adayları yeniden ölçüp
/// yeniden kuruyordu. Tek istisna "kesintisiz geçiş" bloğuydu ve o blok yalnızca
/// FARKLI bir sunucu tercih edildiğinde çalışıyordu. Sonuç:
///
///   * <c>Reload()</c> her tetiklendiğinde (abonelik güncellemesi tamamlandı,
///     profil listesi yenilendi, F5, mod değişimi) çalışan tünel YIKILIYOR ve
///     sıfırdan kuruluyordu — kullanıcının "bağlan → kop → yeniden bağlan"
///     algısının en güçlü kaynağı,
///   * <c>preferred == null</c> (seçili profil WireGuard değilken otomatik GPN
///     akışı) durumunda yumuşak geçiş bloğu tamamen atlanıyordu.
///
/// Bu politika, kullanıcının niyetini karşılayan bir tünel varken hiçbir şey
/// yapmamayı söyler — <see cref="ConnectionTogglePolicy"/> ile aynı felsefe:
/// niyet zaten karşılanmışsa dokunma.
///
/// Saftır: ağ, süreç veya yan etki yok — birim testlerle birebir doğrulanır.
/// </summary>
public static class GpnReconnectPolicy
{
    /// <summary>
    /// Mevcut tünel, istenen hedefi ZATEN karşılıyor mu? True ise çağıran ölçüm,
    /// teardown ve yeniden kurulum YAPMAMALIDIR.
    /// </summary>
    /// <param name="state">Koordinatörün anlık durumu.</param>
    /// <param name="mode">Koordinatörün anlık modu.</param>
    /// <param name="currentServerId">Bağlı sunucunun kimliği (yoksa null).</param>
    /// <param name="preferred">İstekte bildirilen tercih edilen sunucu (yoksa null).</param>
    public static bool ShouldReuseExistingTunnel(
        GpnConnectionState state,
        ConnectionMode mode,
        string? currentServerId,
        GpnServerProfile? preferred)
    {
        // Bağlı değilsek (Disconnected/Failed) mevcut tünel bir şeyi karşılamaz.
        // Connecting de kapsanır: uçuştaki bir bağlanma isteği ikinci bir
        // bağlanmayı başlatmamalıdır (çift tünel/yarış).
        if (state is not (GpnConnectionState.Connected or GpnConnectionState.Connecting))
        {
            return false;
        }

        // V2rayTCP yedeğinde "sunucu" kavramı yoktur; istek WireGuard hedefliyorsa
        // yükseltme denemesi anlamlıdır (mevcut davranış korunur).
        if (mode != ConnectionMode.WireGuardUDP)
        {
            return false;
        }

        // Tercih BİLDİRİLMEMİŞSE (Reload, tepsi, otomatik akış) mevcut WG tüneli
        // hedefi zaten karşılar: ölçme, yıkma, yeniden kurma.
        if (preferred is null)
        {
            return true;
        }

        // Tercih bildirilmişse yalnızca AYNI sunucu için yeniden kullanılır.
        // Farklı sunucu → kesintisiz geçiş (soft-switch) yoluna bırakılır.
        return !currentServerId.IsNullOrEmpty()
            && string.Equals(currentServerId, preferred.ServerId, StringComparison.Ordinal);
    }
}
