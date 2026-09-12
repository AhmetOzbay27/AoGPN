using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.Services.CoreConfig.Mihomo;

namespace ServiceLib.Services.Gpn;

/// <summary>
/// Çalışan GPN mihomo çekirdeğinin hangi giriş setiyle (superset config) üretildiğini
/// tutan oturum kaydı. Üretici (CoreConfigHandler — GpnSoftPolicy superset dalı) config
/// üretirken <see cref="Begin"/> çağırır; GpnCoreLauncher duruşta <see cref="End"/> ile
/// temizler. Yumuşak uygulayıcı, güncel uygulama listesinin çalışan config'in satırlarıyla
/// birebir aynı olduğunu bu parmak iziyle doğrular — giriş eklendi/silindi/taşındıysa
/// (yapısal değişiklik) uygulama legacy restart yoluna düşer.
/// </summary>
public static class GpnSoftSession
{
    private static readonly object _gate = new();
    private static IReadOnlyList<string>? _entryKeys;
    private static ProfileItem? _node;

    /// <summary>Çalışan config superset biçiminde üretildi mi?</summary>
    public static bool IsActive
    {
        get { lock (_gate) { return _entryKeys is not null; } }
    }

    /// <summary>Sıralı giriş kimlik anahtarları (null = superset oturum yok).</summary>
    public static IReadOnlyList<string>? Fingerprint
    {
        get { lock (_gate) { return _entryKeys; } }
    }

    /// <summary>
    /// Superset config'in üretildiği WireGuard düğümü (GpnCoreLauncher'ın
    /// <c>BuildWireGuardProfile</c> çıktısı — çekirdeğin gerçekten yüklediği
    /// config'in kaynağı). Oturum sırasında uygulamanın varsayılan düğümü BU
    /// DEĞİLDİR (launcher geçici bir profil kurar, kullanıcının varsayılan
    /// seçimini değiştirmez); RoutingDriftHealthCheck beklenen config'i canlı
    /// superset config'le birebir üretebilmek için bu düğümü kullanır.
    /// </summary>
    public static ProfileItem? Node
    {
        get { lock (_gate) { return _node; } }
    }

    /// <summary>Superset config üretildiğinde çağrılır (giriş listesi oturum boyunca sabittir).</summary>
    public static void Begin(GpnSoftRoutingPolicy policy, ProfileItem? node = null)
    {
        var keys = policy?.EntryKeys ?? [];
        lock (_gate)
        {
            _entryKeys = keys;
            _node = node;
        }
    }

    /// <summary>Çekirdek durduğunda / superset olmayan biçime geçildiğinde çağrılır.</summary>
    public static void End()
    {
        lock (_gate)
        {
            _entryKeys = null;
            _node = null;
        }
    }

    internal static void ResetForTests() => End();
}

/// <summary>
/// Çalışan GPN mihomo çekirdeğine kesintisiz rota politikası uygular: mod / yön / uygulama
/// rota değişikliklerini çekirdeği yeniden başlatmadan tek tek seçim PUT'larına çevirir.
///
///   PUT /proxies/GPN-MODE   {"name": "DIRECT" | "GPN-Nodes"}   → yakalayıcı (mod)
///   PUT /proxies/GPN-CHECK  {"name": "DIRECT" | "GPN-Nodes"}   → IP doğrulama hostları
///                                (bağlıyken tünel, Off'ta DIRECT — bkz. GpnSoftRouting.CheckGroupTarget)
///   PUT /proxies/ao-&lt;i&gt;    {"name": "GPN-Nodes"|"DIRECT"|"REJECT"|"warp-socks"|"vless-launcher"} → i. giriş
///   (warp egress üyesi: Çift Bağlantıda vless-launcher, legacy'de warp-socks)
///
/// mihomo süreci, TUN ve dinleyiciler yerinde kalır; mevcut bağlantılar kesilmez.
/// Aşağıdaki koşullardan biri sağlanmazsa <c>false</c> döner ve çağıran (SplitTunnel
/// ApplyAsync) eski kural-yaz + reload yoluna düşer — davranış asla bozulmaz:
///  1) çalışan çekirdek mihomo'dur ve canlı config superset biçimindedir
///     (GPN-MODE + GPN-CHECK + GPN-Nodes + ao-* grupları mevcut),
///  2) güncel giriş listesi, config'in üretildiği parmak iziyle birebir aynıdır
///     (yapısal değişiklik yok),
///  3) istenen hedef, grubun üye listesindedir ve PUT sonrası seçim doğrulanır.
/// </summary>
public static class GpnSoftPolicyApplier
{
    private const string Tag = "GpnSoftPolicy";

    /// <summary>
    /// Üretim girişi: mihomo kapısından geçirip canlı mihomo API'siyle uygular.
    /// Testler delegeleri doğrudan <see cref="TryApplyCoreAsync"/>'a verir.
    /// </summary>
    public static async Task<bool> TryApplyAsync(
        GpnSoftRoutingPolicy desired,
        CancellationToken cancellationToken = default)
    {
        // GPN rota seçimleri yalnızca mihomo çekirdeği üzerinde anlamlıdır (Clash API).
        if (!AppManager.Instance.IsRunningCore(ECoreType.mihomo))
        {
            DiagLog.Write($"{Tag} skip: çekirdek mihomo değil");
            return false;
        }
        return await TryApplyCoreAsync(
            desired,
            fingerprint: GpnSoftSession.Fingerprint,
            // forceAttempt: politika uygulaması karar-kritik — geri çekilme
            // penceresi yüzünden boş okuma alıp yanlış karar vermemeli.
            fetchProxies: async () => (await ClashApiManager.Instance.GetClashProxiesAsync(forceAttempt: true).ConfigureAwait(false))?.Item1,
            setSelection: (group, target) => ClashApiManager.Instance.ClashSetActiveProxy(group, target),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saf karar + uygulama çekirdeği (ağ erişimi delegelerle dışarıdan verilir):
    /// kapı kontrolü, seçim delta'sı, PUT ve doğrulama. <c>true</c> = uygulandı (veya
    /// seçimler zaten istenen durumdaydı) — çağıran reload yayınlamaz; <c>false</c> =
    /// restart'lı fallback kullanılmalı.
    /// </summary>
    internal static async Task<bool> TryApplyCoreAsync(
        GpnSoftRoutingPolicy desired,
        IReadOnlyList<string>? fingerprint,
        Func<Task<ClashProxies?>> fetchProxies,
        Func<string, string, Task> setSelection,
        CancellationToken cancellationToken = default)
    {
        if (desired is null)
        {
            return false;
        }

        if (fingerprint is null)
        {
            DiagLog.Write($"{Tag} skip: superset oturum yok");
            return false;
        }
        var expectedKeys = desired.EntryKeys;
        if (!expectedKeys.SequenceEqual(fingerprint))
        {
            DiagLog.Write($"{Tag} skip: giriş listesi çalışan config'ten farklı " +
                $"(yapısal değişiklik?) expected={expectedKeys.Count} live={fingerprint.Count}");
            return false;
        }

        try
        {
            var proxies = (await fetchProxies().ConfigureAwait(false))?.proxies;
            if (proxies is null || proxies.Count == 0)
            {
                DiagLog.Write($"{Tag} skip: /proxies okunamadı");
                return false;
            }

            // 1) Canlı config superset biçiminde mi (grup ailesi mevcut)? GPN-CHECK
            //    eski superset config'lerde yoksa false → restart'lı fallback config'i
            //    yeniden üretir (grup ve GPN-CHECK satırları o zaman gelir).
            if (!IsSelectorGroup(proxies, GpnMihomoConfigService.NodesGroupName)
                || !IsSelectorGroup(proxies, GpnSoftRouting.ModeGroupName)
                || !IsSelectorGroup(proxies, GpnSoftRouting.CheckGroupName))
            {
                DiagLog.Write($"{Tag} skip: çalışan config superset biçiminde değil");
                return false;
            }
            for (var i = 0; i < desired.Entries.Count; i++)
            {
                if (!IsSelectorGroup(proxies, GpnSoftRouting.AppGroupName(i)))
                {
                    DiagLog.Write($"{Tag} skip: {GpnSoftRouting.AppGroupName(i)} grubu yok");
                    return false;
                }
            }

            // 2) İstenen seçim vektörü + değişmesi gereken gruplar (delta). Hedef grubun
            //    üye listesinde değilse bu bir kapı hatasıdır → false (restart'lı fallback).
            var (modeTarget, appTargets) = GpnSoftRouting.ComputeSelectionVector(desired);
            var checkTarget = GpnSoftRouting.CheckGroupTarget(desired.Mode);
            var changes = new List<(string Group, string Target)>();
            if (!AppendIfChanged(proxies, GpnSoftRouting.ModeGroupName, modeTarget, changes))
            {
                DiagLog.Write($"{Tag} skip: {GpnSoftRouting.ModeGroupName} hedefi üye değil ({modeTarget})");
                return false;
            }
            if (!AppendIfChanged(proxies, GpnSoftRouting.CheckGroupName, checkTarget, changes))
            {
                DiagLog.Write($"{Tag} skip: {GpnSoftRouting.CheckGroupName} hedefi üye değil ({checkTarget})");
                return false;
            }
            for (var i = 0; i < appTargets.Count; i++)
            {
                var group = GpnSoftRouting.AppGroupName(i);
                if (!AppendIfChanged(proxies, group, appTargets[i], changes))
                {
                    DiagLog.Write($"{Tag} skip: {group} hedefi üye değil ({appTargets[i]})");
                    return false;
                }
            }

            if (changes.Count == 0)
            {
                DiagLog.Write($"{Tag} no-op: seçimler istenen durumda (reload gerekmez)");
                return true;
            }

            // 3) Seçimleri değiştir (make-before-break — mevcut bağlantılar kesilmez).
            foreach (var (group, target) in changes)
            {
                DiagLog.Write($"{Tag} PUT {group} → {target}");
                await setSelection(group, target).ConfigureAwait(false);
            }

            // 4) Doğrula: ClashApiManager PUT'un HTTP durumunu raporlamaz, seçimler
            //    gerçekten döndü mü ikinci okumayla teyit et. Başarısız grup varsa
            //    false → çağıran restart'lı fallback ile config'i yeniden üretir
            //    (kısmi PUT'lar da böylece tutarlı hâle gelir).
            var verify = (await fetchProxies().ConfigureAwait(false))?.proxies;
            if (verify is null)
            {
                return false;
            }
            var ok = verify.TryGetValue(GpnSoftRouting.ModeGroupName, out var modeGroup)
                && string.Equals(modeGroup.now, modeTarget, StringComparison.OrdinalIgnoreCase)
                && verify.TryGetValue(GpnSoftRouting.CheckGroupName, out var checkGroup)
                && string.Equals(checkGroup.now, checkTarget, StringComparison.OrdinalIgnoreCase)
                && AllApplied(verify, appTargets);
            if (ok)
            {
                DiagLog.Write($"{Tag} uygulandı (restart'sız): mode={modeTarget} " +
                    $"entries={appTargets.Count} changes={changes.Count}");
            }
            else
            {
                DiagLog.Write($"{Tag} doğrulama başarısız: mode now={modeGroup?.now ?? "(yok)"}");
            }
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] yumuşak rota uygulaması hatası: {ex.Message}");
            DiagLog.Write($"{Tag} hata: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Canlı seçim istenenden farklıysa ve hedef üye listesindeyse delta'ya ekler.
    /// Grup eksikse veya hedef üye değilse <c>false</c> (sert kapı hatası). Seçim zaten
    /// istenen durumdaysa delta'ya bir şey eklemeden <c>true</c> döner.
    /// </summary>
    private static bool AppendIfChanged(
        Dictionary<string, ClashProxies.ProxiesItem> proxies,
        string group,
        string target,
        List<(string Group, string Target)> changes)
    {
        if (!proxies.TryGetValue(group, out var item))
        {
            return false;
        }
        if (string.Equals(item.now, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Hedef, grubun üye listesinde olmalı — yoksa PUT anlamsız/tehlikeli olur.
        if (item.all?.Contains(target, StringComparer.OrdinalIgnoreCase) != true)
        {
            return false;
        }
        changes.Add((group, target));
        return true;
    }

    private static bool AllApplied(Dictionary<string, ClashProxies.ProxiesItem> proxies, IReadOnlyList<string> appTargets)
    {
        for (var i = 0; i < appTargets.Count; i++)
        {
            var group = GpnSoftRouting.AppGroupName(i);
            if (!proxies.TryGetValue(group, out var item)
                || !string.Equals(item.now, appTargets[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSelectorGroup(
        Dictionary<string, ClashProxies.ProxiesItem> proxies,
        string name)
    {
        return proxies.TryGetValue(name, out var group)
            && string.Equals(group.type, "Selector", StringComparison.OrdinalIgnoreCase);
    }
}
