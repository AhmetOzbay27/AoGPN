using System.IO;
using ServiceLib.Common;
using ServiceLib.Events;

namespace ServiceLib.Services.Gpn;

// ─────────────────────────────────────────────────────────────────────────
// WinDivertHealthMonitor — yakalama köprüsünün (WinDivert) çevre durumu
//
// Köprü ölümsüz bilinen bir nedenle düşüyor: exe yanında WinDivert.dll yok →
// P/Invoke "Unable to load DLL 'WinDivert.dll'" → "GPN_BRIDGE yakalama
// döngüsü fault". Bu monitör durumu ÖNCEDEN ve gerçek zamanlı ortaya koyar:
//
//   Check() sırası:
//     1. WinDivert.dll var mı?            → yoksa DllMissing
//     2. WinDivert64.sys var mı?          → yoksa DriverFileMissing
//     3. \\.\WinDivert cihazı açılıyor mu? → evetse Ready
//        cihaz yok + yönetici değil       → NeedsAdmin
//        cihaz yok + yönetici + izinliyse → SCM kurulumu (EnsureDriverStarted)
//                                           → tekrar probe → Ready/InstallFailed
//        diğer açılış hataları            → InstallFailed (imza/BFE/engel...)
//
// Her Check sonucu AppEvents.WinDivertHealthChanged üzerinden dashboard'a
// akar (banner + diag satırı). Sürücü kuralı WinDivert 2.2.2'nin kendisiyle
// aynıdır: WinDivert64.sys, WinDivert.dll'nin YANINDA olmalıdır ve kurulum
// yönetici ister (GPN TUN yolu zaten yükseltilmiş çalışır).
// ─────────────────────────────────────────────────────────────────────────
public enum WinDivertHealthState
{
    /// <summary>Henüz kontrol edilmedi.</summary>
    Unknown,

    /// <summary>Sürücü çalışıyor — köprü açılabilir.</summary>
    Ready,

    /// <summary>WinDivert.dll exe yanında yok (build dağıtımı eksik/gecikmiş).</summary>
    DllMissing,

    /// <summary>WinDivert64.sys exe yanında yok.</summary>
    DriverFileMissing,

    /// <summary>Sürücü çalışmıyor ve kurulum için yönetici yetkisi yok.</summary>
    NeedsAdmin,

    /// <summary>Sürücü kurulamadı veya açılamadı (NativeError'a bakın).</summary>
    InstallFailed,
}

/// <summary>
/// WinDivert çevre durumu anlık görüntüsü.
/// <see cref="AppEvents.WinDivertHealthChanged"/> üzerinden dashboard'a akar;
/// mesaj kullanıcıya gösterilmek üzere yerelleştirilmiş diyagnostik metindir.
/// </summary>
public sealed record WinDivertHealth(
    WinDivertHealthState State,
    string? Message,
    int NativeError = 0)
{
    public bool IsHealthy => State == WinDivertHealthState.Ready;

    public static WinDivertHealth Unknown => new(WinDivertHealthState.Unknown, null);
}

/// <summary>
/// WinDivert DLL/sürücü durumunu kontrol eden, gerektiğinde SCM üzerinden
/// sürücüyü kuran ve sonucu diag + dashboard kanalına yazan monitör.
/// Tek örnek (<see cref="Instance"/>) MainWindow açılışında + köprü fault'unda
/// çağrılır; testler fakes ile doğrudan kurar.
/// </summary>
public sealed class WinDivertHealthMonitor
{
    private static readonly Lazy<WinDivertHealthMonitor> _lazy = new(static () => new WinDivertHealthMonitor(
        Utils.GetPath("WinDivert.dll"),
        Utils.GetPath("WinDivert64.sys"),
        WinDivertDriverSupport.ProbeDevice,
        WinDivertDriverSupport.EnsureDriverStarted,
        Utils.IsAdministrator()));

    /// <summary>Uygulama geneli tek örnek (MainWindow açılışı + köprü fault'u).</summary>
    public static WinDivertHealthMonitor Instance => _lazy.Value;

    private readonly string _dllPath;
    private readonly string _sysPath;
    private readonly Func<(bool Exists, int Error)> _probe;
    private readonly Func<string, int>? _installDriver;
    private readonly bool _elevated;

    /// <summary>Son <see cref="Check"/> sonucu (çizim/itme için).</summary>
    public WinDivertHealth Snapshot { get; private set; } = WinDivertHealth.Unknown;

    /// <param name="dllPath">WinDivert.dll tam yolu (genellikle exe yanı).</param>
    /// <param name="sysPath">WinDivert64.sys tam yolu (DLL yanı — sürücü kuralı).</param>
    /// <param name="probe">Sürücü cihazı probe'u (varsayılan <see cref="WinDivertDriverSupport.ProbeDevice"/>).</param>
    /// <param name="installDriver">
    /// SCM kurulum/başlatma; 0 başarı. null ise kurulum denenmez (yalnızca rapor).
    /// Testlerde sahte kurucu verilir.
    /// </param>
    /// <param name="elevated">İşlem yönetici yetkisiyle mi çalışıyor.</param>
    public WinDivertHealthMonitor(
        string dllPath,
        string sysPath,
        Func<(bool Exists, int Error)>? probe = null,
        Func<string, int>? installDriver = null,
        bool elevated = false)
    {
        _dllPath = dllPath;
        _sysPath = sysPath;
        _probe = probe ?? WinDivertDriverSupport.ProbeDevice;
        _installDriver = installDriver;
        _elevated = elevated;
    }

    /// <summary>
    /// Çevre durumunu kontrol eder, sonucu yayınlar ve döndürür.
    /// <paramref name="attemptInstall"/> yalnızca yönetici yetkisi varken ve
    /// cihaz yokken SCM kurulumunu tetikler (açılış: true; köprü fault'u: false
    /// — kurulum açılışta zaten denenmiştir, fault anında sürpriz kurulum yok).
    /// </summary>
    public WinDivertHealth Check(bool attemptInstall = false)
    {
        var health = Evaluate(attemptInstall);
        Snapshot = health;
        DiagLog.Write(
            $"WINDIVERT env state={health.State} err={health.NativeError} {(health.Message ?? "ok")}");
        AppEvents.WinDivertHealthChanged.Publish(health);
        return health;
    }

    private WinDivertHealth Evaluate(bool attemptInstall)
    {
        if (!File.Exists(_dllPath))
        {
            return new WinDivertHealth(
                WinDivertHealthState.DllMissing,
                $"WinDivert.dll bulunamadı ({_dllPath}) — yakalama köprüsü kapalı. Uygulamayı yeniden derleyin: build, Libs\\WinDivert'teki resmî 2.2.2 dağıtımını exe yanına kopyalar.",
                Native.ERROR_FILE_NOT_FOUND);
        }

        if (!File.Exists(_sysPath))
        {
            return new WinDivertHealth(
                WinDivertHealthState.DriverFileMissing,
                $"WinDivert64.sys bulunamadı ({_sysPath}) — sürücü dosyası WinDivert.dll ile aynı klasörde olmalı.",
                Native.ERROR_FILE_NOT_FOUND);
        }

        var (exists, err) = _probe();
        if (exists)
        {
            return new WinDivertHealth(WinDivertHealthState.Ready, null);
        }

        // Sürücü cihazı yok veya açılamıyor.
        if (err == Native.ERROR_ACCESS_DENIED)
        {
            return new WinDivertHealth(
                WinDivertHealthState.NeedsAdmin,
                "Yakalama sürücüsüne erişilemiyor (hata 5) — uygulama yönetici olarak çalışmalı.",
                err);
        }

        if (err == Native.ERROR_FILE_NOT_FOUND || err == Native.ERROR_PATH_NOT_FOUND)
        {
            if (!_elevated)
            {
                return new WinDivertHealth(
                    WinDivertHealthState.NeedsAdmin,
                    "Yakalama sürücüsü (WinDivert) çalışmıyor ve kurulum için yönetici yetkisi gerekir — programı yönetici olarak çalıştırıp yeniden bağlanın.",
                    err);
            }

            if (!attemptInstall || _installDriver is null)
            {
                return new WinDivertHealth(
                    WinDivertHealthState.NeedsAdmin,
                    "Yakalama sürücüsü (WinDivert) henüz başlatılmadı — GPN bağlantısı köprüyü başlatınca otomatik kurulur.",
                    err);
            }

            var installErr = _installDriver(Path.GetFullPath(_sysPath));
            if (installErr != 0)
            {
                return new WinDivertHealth(
                    WinDivertHealthState.InstallFailed,
                    $"WinDivert sürücüsü kurulamadı (hata {installErr}): {WinDivertEngine.MissingDriverHint(installErr)}",
                    installErr);
            }

            var (existsAfter, errAfter) = _probe();
            if (existsAfter)
            {
                return new WinDivertHealth(WinDivertHealthState.Ready, null);
            }
            return new WinDivertHealth(
                WinDivertHealthState.InstallFailed,
                $"WinDivert sürücüsü kuruldu ama cihaz açılamadı (hata {errAfter}): {WinDivertEngine.MissingDriverHint(errAfter)}",
                errAfter);
        }

        // Diğer açılış hataları — imza (577), engelleme (1275), BFE (1753) vb.
        return new WinDivertHealth(
            WinDivertHealthState.InstallFailed,
            WinDivertEngine.MissingDriverHint(err),
            err);
    }

    private static class Native
    {
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_FILE_NOT_FOUND = 2;
        internal const int ERROR_PATH_NOT_FOUND = 3;
    }
}