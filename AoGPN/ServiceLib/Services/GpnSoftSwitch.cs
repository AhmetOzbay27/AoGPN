using ServiceLib.Manager;
using ServiceLib.Services.CoreConfig.Mihomo;

namespace ServiceLib.Services;

/// <summary>
/// GPN mihomo tüneli için kesintisiz (make-before-break) düğüm geçişi.
///
/// Çalışan çekirdeğin YAML'i çoklu-düğüm biçiminde üretildiyse (tüm adaylar
/// <c>type: wireguard</c> outbound + "GPN-Nodes" select grubu) düğüm değişimi
/// çekirdeği/TUN'u durdurmadan tek API çağrısıyla yapılır:
///
///   PUT /proxies/GPN-Nodes   {"name": "wg-&lt;hedefId&gt;"}
///
/// mihomo süreci, TUN adaptörü ve yerel dinleyiciler yerinde kalır; mevcut
/// bağlantılar eski düğümde doğal olarak bitene kadar sürer (drenaj), yeni
/// bağlantılar anında yeni düğümden kurulur. Başarısız olan her senaryoda
/// <c>false</c> döner — çağıran (koordinatör) eski durdur/başlat yoluna düşer,
/// böylece çekirdeği çoklu-düğüm desteklemeyen sürümlerde davranış değişmez.
/// </summary>
public static class GpnSoftSwitch
{
    private const string Tag = "GpnSoft";

    /// <summary>
    /// Çalışan mihomo çekirdeğinde seçili düğümü <paramref name="target"/>'e taşır.
    /// Yalnızca üç koşul birden sağlanınca geçiş yapar ve <c>true</c> döner:
    ///  1) çalışan çekirdek mihomo'dur (GPN akışının kanıtlanmış çekirdeği),
    ///  2) canlı config'te "GPN-Nodes" select grubu var ve hedef wg-&lt;id&gt; üye,
    ///  3) PUT sonrası grup seçimi doğrulanabilir şekilde hedefe dönmüştür.
    /// Diğer tüm durumlarda (başka çekirdek, eski tek-düğüm config, API kapalı)
    /// <c>false</c> — çağıran durdur/başlat fallback'ini kullanır.
    /// </summary>
    public static async Task<bool> TrySwitchNodeAsync(
        GpnServerProfile target,
        CancellationToken cancellationToken = default)
    {
        if (target is null || target.ServerId.IsNullOrEmpty())
        {
            return false;
        }
        try
        {
            // GPN düğüm değişimi yalnızca mihomo çekirdeği üzerinde anlamlıdır.
            // Diğer çekirdeklerde (xray/sing-box legacy vb.) bu API yoktur veya
            // farklı config üretir — süreci 6sn'lik retry ile boğmamak için önce
            // hızlı süreç kapısından geç.
            if (!AppManager.Instance.IsRunningCore(ECoreType.mihomo))
            {
                DiagLog.Write($"{Tag} skip: çekirdek mihomo değil");
                return false;
            }

            var api = ClashApiManager.Instance;
            var targetName = GpnMihomoConfigService.WireGuardProxyName(target);

            // 1) Canlı config çoklu-düğüm biçiminde mi + hedef grupta üye mi?
            var snapshot = await api.GetClashProxiesAsync().ConfigureAwait(false);
            var proxies = snapshot?.Item1?.proxies;
            if (proxies is null
                || !proxies.TryGetValue(GpnMihomoConfigService.NodesGroupName, out var group)
                || !string.Equals(group.type, "Selector", StringComparison.OrdinalIgnoreCase)
                || group.all?.Contains(targetName, StringComparer.OrdinalIgnoreCase) != true)
            {
                DiagLog.Write($"{Tag} skip: çalışan config'te çoklu-düğüm grubu/hedef yok ({targetName})");
                return false;
            }
            if (string.Equals(group.now, targetName, StringComparison.OrdinalIgnoreCase))
            {
                DiagLog.Write($"{Tag} skip: hedef zaten seçili ({targetName})");
                return true;
            }

            // 2) Seçimi değiştir (make-before-break — mevcut bağlantılar kesilmez).
            await api.ClashSetActiveProxy(GpnMihomoConfigService.NodesGroupName, targetName)
                .ConfigureAwait(false);

            // 3) Doğrula: ClashApiManager PUT'un HTTP durumunu raporlamaz, seçim
            //    gerçekten döndü mü ikinci okumayla teyit et.
            var verifySnap = (await api.GetClashProxiesAsync().ConfigureAwait(false))?.Item1?.proxies;
            var now = verifySnap is not null
                && verifySnap.TryGetValue(GpnMihomoConfigService.NodesGroupName, out var after)
                    ? after?.now
                    : null;
            var ok = string.Equals(now, targetName, StringComparison.OrdinalIgnoreCase);
            if (ok)
            {
                DiagLog.Write($"{Tag} geçiş OK → {targetName} (restart'sız)");
            }
            else
            {
                DiagLog.Write($"{Tag} doğrulama başarısız: hedef={targetName} now={now ?? "(yok)"}");
            }
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] yumuşak düğüm geçişi hatası: {ex.Message}");
            DiagLog.Write($"{Tag} hata: {ex.Message}");
            return false;
        }
    }
}
