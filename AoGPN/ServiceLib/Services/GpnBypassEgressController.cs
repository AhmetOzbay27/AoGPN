using ServiceLib.Models.Dto;
using ServiceLib.Services.CoreConfig.Mihomo;
using ServiceLib.Services.Gpn;

namespace ServiceLib.Services;

/// <summary>
/// WARP egress "degrade" yönetimi — launcher-engeli aşma hedefinin tek nokta
/// arızasını kapatır. WarpDialHealthMonitor (log kuyruğundan warp-socks dial
/// hatalarını sayar) faulted duruma geçince, çalışan mihomo superset oturumunda
/// launcher/API egress'ini ÇEKİRDEĞİ YENİDEN BAŞLATMADAN DIRECT'e çevirir:
///
///   PUT /proxies/GPN-LAUNCHER → DIRECT   (warp egress'li launcher DOMAIN-SUFFIX satırları)
///   PUT /proxies/ao-&lt;i&gt;       → DIRECT   ("warp" rotalı girişler — ör. BsGLauncher.exe)
///
/// Böylece WARP zinciri (sunucu wireproxy 10.66.66.1:40000 — yalnızca WG tüneli
/// üzerinden erişilebilir) ölüyken launcher/auth trafiği dial hatası alıp WAF
/// engeline geri düşmek yerine doğrudan çıkar; bağlantı korunur. Sağlık geri
/// gelince (recovery cooldown sonrası monitor healthy yayınlar) seçimler warp
/// egress üyesine geri döner.
///
/// Kapılar (hepsi sağlanmazsa hiçbir şey uygulanmaz — davranış asla bozulmaz):
///   1) çalışan çekirdek mihomo'dur (soft oturum),
///   2) bağlantı Connected durumdadır,
///   3) GPN-LAUNCHER ve hedeflenen ao-&lt;i&gt; grupları canlı config'te Selector'dür ve
///      istenen hedef üye listesindedir,
///   4) PUT sonrası geri-okuma ile seçimler doğrulanır (kısmi uygulama kabul edilmez).
///
/// Kapsam notu: izleyici (WarpDialHealthMonitor) yalnızca legacy warp-socks
/// zincirini izler — Çift Bağlantı (vless-launcher) modunda bu servis no-op'tur
/// (GPN-LAUNCHER grubu da o biçimde üretilmez; satırlar doğrudan vless-launcher'a
/// gider). Yumuşak uygulayıcıdan (GpnSoftPolicyApplier) bağımsızdır: mod/yön/
/// giriş vektörünü değiştirmez, yalnızca launcher egress seçimini yönetir.
/// Yeni bir fault olayı degrade durumunu yeniden doğrular (self-healing).
/// </summary>
public sealed class GpnBypassEgressController : IDisposable
{
    private const string Tag = "GpnBypassEgress";

    private readonly Func<Config> _getConfig;
    private readonly Func<bool> _isMihomoRunning;
    private readonly Func<Task<Dictionary<string, ClashProxies.ProxiesItem>?>> _fetchProxies;
    private readonly Func<string, string, Task> _setSelection;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();

    private IDisposable? _healthSub;
    private IDisposable? _stateSub;
    private bool _connected;
    private bool _degraded;
    private bool _disposed;

    public GpnBypassEgressController(
        Func<Config>? getConfig = null,
        Func<bool>? isMihomoRunning = null,
        Func<Task<Dictionary<string, ClashProxies.ProxiesItem>?>>? fetchProxies = null,
        Func<string, string, Task>? setSelection = null)
    {
        _getConfig = getConfig ?? (() => AppManager.Instance.Config);
        _isMihomoRunning = isMihomoRunning ?? (() => AppManager.Instance.IsRunningCore(ECoreType.mihomo));
        _fetchProxies = fetchProxies
            ?? (async () => (await ClashApiManager.Instance.GetClashProxiesAsync().ConfigureAwait(false))?.Item1?.proxies);
        _setSelection = setSelection
            ?? ((group, target) => ClashApiManager.Instance.ClashSetActiveProxy(group, target));
    }

    /// <summary>Launcher egress şu anda DIRECT'e degrade edilmiş mi? (dashboard rozeti)</summary>
    public bool Degraded
    {
        get
        {
            lock (_sync)
            {
                return _degraded;
            }
        }
    }

    /// <summary>
    /// Sağlık + bağlantı kanallarına abone olur ve anlık izleyici durumuyla
    /// senkronize olur (bağlantı öncesi oluşmuş bir fault'u kaçırmamak için).
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_healthSub is not null)
            {
                return;
            }
            _healthSub = AppEvents.WarpDialHealthChanged.AsObservable().Subscribe(h => { _ = ProcessHealth(h); });
            _stateSub = AppEvents.GpnConnectionStateChanged.AsObservable().Subscribe(ProcessState);
        }
        // Anlık durum: izleyici yalnızca değişimlerde yayın yapar; abonelik öncesi
        // faulted olunmuş olabilir. (Okuma yalnızca — korumalı katmana yazılmaz.)
        _ = ProcessHealth(WarpDialHealthMonitor.Instance.Snapshot);
    }

    /// <summary>
    /// WARP sağlık olayını işler: faulted → degrade (DIRECT'e çek), healthy →
    /// geri yükle (warp egress üyesine). Testler doğrudan çağırabilir; dönen Task
    /// uygulama bitince tamamlanır.
    /// </summary>
    internal Task ProcessHealth(WarpDialHealth health)
    {
        var degrade = false;
        lock (_sync)
        {
            if (_disposed || _cts.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }
            if (health.Faulted)
            {
                if (!_connected || _degraded || !_isMihomoRunning())
                {
                    return Task.CompletedTask;
                }
                degrade = true;
            }
            else
            {
                if (!_degraded)
                {
                    return Task.CompletedTask;
                }
            }
        }
        return degrade
            ? RunAsync(DegradeChanges, desiredDegraded: true)
            : RunAsync(RestoreChanges, desiredDegraded: false);
    }

    /// <summary>Bağlantı durumu değişiminde işareti sıfırla (oturum bitti → çekirdek duracak).</summary>
    internal void ProcessState(GpnConnectionSnapshot snap)
    {
        lock (_sync)
        {
            if (snap.State is GpnConnectionState.Connecting
                or GpnConnectionState.Disconnected
                or GpnConnectionState.Failed)
            {
                _connected = false;
                _degraded = false;
            }
            else if (snap.State == GpnConnectionState.Connected)
            {
                _connected = true;
            }
        }
    }

    private async Task RunAsync(
        Func<GpnSoftRoutingPolicy, string, IReadOnlyList<(string Group, string Target)>> buildChanges,
        bool desiredDegraded)
    {
        try
        {
            var policy = GpnSoftRouting.BuildPolicy(_getConfig());
            var warpMember = policy.WarpEgressProxy ?? GpnMihomoConfigService.WarpProxyName;
            var changes = buildChanges(policy, warpMember);
            if (changes.Count == 0)
            {
                return;
            }
            var ok = await ApplyAsync(changes).ConfigureAwait(false);
            lock (_sync)
            {
                // Yalnızca DOĞRULANMIŞ uygulama işareti değiştirir: degrade başarılıysa
                // true, geri yükleme başarılıysa false — kapı/doğrulama hatasında
                // mevcut durum korunur, sonraki olay yeniden dener.
                if (ok)
                {
                    _degraded = desiredDegraded;
                }
            }
            DiagLog.Write(ok
                ? $"{Tag} uygulandı: launcher egress → {changes[0].Target} ({changes.Count} grup)"
                : $"{Tag} kapı/doğrulama başarısız — uygulanmadı");
        }
        catch (OperationCanceledException)
        {
            // Kapanış/iptal — sessizce bırak.
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] hata: {ex.Message}");
            DiagLog.Write($"{Tag} hata: {ex.Message}");
        }
    }

    /// <summary>
/// Degrade değişim vektörü: GPN-LAUNCHER grubu (warp egress'li launcher
/// satırları) + "warp" egress rotalı girişlerin ao-&lt;i&gt; grupları → DIRECT.
/// Yalnızca launcher egress'i etkiler; mod/yön/giriş vektörü
/// (GpnSoftPolicyApplier) değişmez.
    /// </summary>
    private static IReadOnlyList<(string Group, string Target)> DegradeChanges(
        GpnSoftRoutingPolicy policy, string warpMember)
        => BuildBypassChanges(policy, warpMember, target: GpnSoftRouting.ClashDirect);

    /// <summary>Sağlıklı geri yükleme vektörü: aynı gruplar → warp egress üyesine.</summary>
    private static IReadOnlyList<(string Group, string Target)> RestoreChanges(
        GpnSoftRoutingPolicy policy, string warpMember)
        => BuildBypassChanges(policy, warpMember, target: warpMember);

    private static IReadOnlyList<(string Group, string Target)> BuildBypassChanges(
        GpnSoftRoutingPolicy policy, string warpMember, string target)
    {
        var changes = new List<(string Group, string Target)>
        {
            (GpnSoftRouting.LauncherGroupName, target),
        };
        for (var i = 0; i < policy.Entries.Count; i++)
        {
            var healthy = GpnSoftRouting.MapActionToMember(
                policy.Entries[i].Action, policy.InvertManualRouting, policy.WarpEgressProxy);
            if (string.Equals(healthy, warpMember, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add((GpnSoftRouting.AppGroupName(i), target));
            }
        }
        return changes;
    }

    /// <summary>
    /// Kapı + uygulama + doğrulama: her grup Selector olmalı ve hedef üye
    /// listesinde olmalı; PUT'lar sonrası geri-okuma tüm seçimleri teyit eder.
    /// </summary>
    private async Task<bool> ApplyAsync(IReadOnlyList<(string Group, string Target)> changes)
    {
        var proxies = await _fetchProxies().ConfigureAwait(false);
        if (proxies is null || proxies.Count == 0)
        {
            DiagLog.Write($"{Tag} kapı: /proxies okunamadı");
            return false;
        }
        foreach (var (group, target) in changes)
        {
            if (!proxies.TryGetValue(group, out var item)
                || !string.Equals(item.type, "Selector", StringComparison.OrdinalIgnoreCase)
                || item.all?.Contains(target, StringComparer.OrdinalIgnoreCase) != true)
            {
                DiagLog.Write($"{Tag} kapı: {group} Selector değil veya {target} üye değil");
                return false;
            }
        }
        foreach (var (group, target) in changes)
        {
            DiagLog.Write($"{Tag} PUT {group} → {target}");
            await _setSelection(group, target).ConfigureAwait(false);
        }
        var verify = await _fetchProxies().ConfigureAwait(false);
        if (verify is null)
        {
            return false;
        }
        foreach (var (group, target) in changes)
        {
            if (!verify.TryGetValue(group, out var item)
                || !string.Equals(item.now, target, StringComparison.OrdinalIgnoreCase))
            {
                DiagLog.Write($"{Tag} doğrulama başarısız: {group} now={item?.now ?? "(yok)"}");
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _healthSub?.Dispose();
            _healthSub = null;
            _stateSub?.Dispose();
            _stateSub = null;
        }
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}