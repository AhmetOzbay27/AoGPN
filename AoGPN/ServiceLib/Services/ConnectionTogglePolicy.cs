// ─────────────────────────────────────────────────────────────────────────
// ConnectionTogglePolicy — "toggle" isteğini kullanıcı NİYETİNE göre yorumlar
//
// Dashboard'ın tek bir bağlantı butonu vardır: hem bağlanır hem keser. Yönü
// yalnızca canlı durumdan türetmek, arka planda kurulan bir tünel arayüzde hâlâ
// "Bağlan" görünürken gelen tıklamayı "kes"e çeviriyordu (canlı gözlenen
// "bağlandı → Bağlan'a döndü → tekrar basınca bağlandı" titremesi). Bu politika,
// isteği gönderen arayüzün EKRANDA GÖSTERDİĞİ durumu (payload'daki `connected`)
// yetkili kabul eder; canlı çekirdek durumu yalnızca niyet bildirilmemişse
// (eski gönderen, tepsi, iç yeniden bağlanma) kullanılan geriye dönük yoldur.
// ─────────────────────────────────────────────────────────────────────────

namespace ServiceLib.Services;

/// <summary>Kullanıcının bağlantı toggle'ındaki niyeti.</summary>
public enum ConnectionToggleIntent
{
    /// <summary>Kullanıcı "Bağlan" gördü ve bastı.</summary>
    Connect,

    /// <summary>Kullanıcı "Bağlantıyı Kes" gördü ve bastı.</summary>
    Disconnect,
}

/// <summary>Toggle isteğinin dönüşeceği eylem.</summary>
public enum ConnectionToggleAction
{
    /// <summary>Bağlanma akışı çalıştırılır.</summary>
    Connect,

    /// <summary>Kesme (teardown) akışı çalıştırılır.</summary>
    Disconnect,

    /// <summary>Niyet zaten karşılanmış — hiçbir geçiş başlatılmaz.</summary>
    NoOp,
}

/// <summary>
/// Saf (UI'sız, deterministik test edilebilir) toggle kararı.
/// <see cref="ConnectionCommandGate"/> sıralamayı üstlenir; bu politika yorumu.
/// </summary>
public static class ConnectionTogglePolicy
{
    /// <summary>
    /// Gönderen arayüzün ekranda gösterdiği bağlantı durumunu niyete çevirir.
    /// <c>null</c> → gönderen durum bildirmedi: çağıran, canlı duruma göre
    /// yorumlayan geriye dönük yola düşer.
    /// </summary>
    /// <param name="displayedConnected">
    /// Kullanıcı bastığı anda arayüzün gösterdiği durum (bağlı = "Bağlantıyı Kes").
    /// </param>
    public static ConnectionToggleIntent? IntentFromDisplayedState(bool? displayedConnected)
        => displayedConnected switch
        {
            true => ConnectionToggleIntent.Disconnect,
            false => ConnectionToggleIntent.Connect,
            _ => null,
        };

    /// <summary>
    /// Niyeti, bağlantının canlı durumu eşliğinde eyleme çevirir. Temel ilke:
    /// kullanıcının istediği yön ASLA tersine çevrilmez — istek zaten
    /// karşılanmışsa hiçbir şey yapılmaz (bağlan isteği kesmeye, kesme isteği
    /// yeniden bağlanmaya dönüşmez).
    /// </summary>
    /// <param name="intent">Kullanıcının niyeti.</param>
    /// <param name="effectiveConnected">
    /// Canlı durum: çekirdek Ready / GPN koordinatörü bağlı / "bağlı" yayınlanmış
    /// (bkz. MainWindow.ReadEffectiveConnectionState).
    /// </param>
    /// <param name="connectionStarting">
    /// Bir geçiş (bağlanma ya da kesme) hâlâ sürüyor mu.
    /// </param>
    public static ConnectionToggleAction Decide(
        ConnectionToggleIntent intent,
        bool effectiveConnected,
        bool connectionStarting)
        => intent switch
        {
            // "Bağlan": tünel zaten kurulu ya da kurulmakta → dokunma. Uçuştaki
            // bağlanmayı iptal etmek de yanlış olurdu — kullanıcı bağlanmak istedi.
            ConnectionToggleIntent.Connect => effectiveConnected || connectionStarting
                ? ConnectionToggleAction.NoOp
                : ConnectionToggleAction.Connect,

            // "Kes": kurulu ya da kurulmakta olan tünel kesilir (yarıda kalan
            // bağlanma isteği de iptal edilir — çağıranın kesme dalı bunu kapsar).
            ConnectionToggleIntent.Disconnect => effectiveConnected || connectionStarting
                ? ConnectionToggleAction.Disconnect
                : ConnectionToggleAction.NoOp,

            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Bilinmeyen toggle niyeti"),
        };
}
