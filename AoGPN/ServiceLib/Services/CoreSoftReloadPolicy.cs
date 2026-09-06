namespace ServiceLib.Services;

/// <summary>
/// <c>CoreManager.LoadCore</c> yumuşak yeniden yükleme (soft reload) hızlı yolu
/// için koşullar. Sağlandığında yeni config, çekirdek süreci DURDURULMADAN
/// <c>PUT /configs?force=true</c> ile yüklenir ve mevcut TCP/UDP oturumları
/// korunur (oturum sürekliliği — bkz. docs/session-continuity-design.md, Faz 1).
///
/// Koşullar:
///   1) önceki ve sonraki bağlam ikisi de mihomo (başka çekirdeğin API'si farklı),
///   2) çekirdek süreci hâlâ canlı,
///   3) çalışan çekirdek gerçekten mihomo,
///   4) TUN durumu DEĞİŞMEDİ — değiştiyse (GPN ↔ Global geçişi) reload adaptörü/
///      rotaları yeniden kurar; bu geçiş restart yolunda kalır (güvenli taraf).
///   (pre-SOCKS zinciri varlığı ayrıca CoreManager tarafında kontrol edilir:
///   yardımcı çekirdek reload ile yeniden başlatılamaz.)
/// </summary>
public static class CoreSoftReloadPolicy
{
    public static bool CanReload(
        CoreConfigContext? previous,
        CoreConfigContext next,
        bool mainProcessAlive,
        bool runningCoreIsMihomo)
    {
        if (previous is null)
        {
            return false;
        }
        if (previous.RunCoreType != ECoreType.mihomo || next.RunCoreType != ECoreType.mihomo)
        {
            return false;
        }
        if (!mainProcessAlive || !runningCoreIsMihomo)
        {
            return false;
        }
        if (previous.IsTunEnabled != next.IsTunEnabled)
        {
            return false;
        }
        return true;
    }
}