using System.IO.Compression;

namespace ServiceLib.Services;

/// <summary>
/// Headless core installer backing the "--update-cores" startup mode. It fetches
/// the latest sing-box and Xray releases (plus geo assets) through
/// <see cref="UpdateService"/> and unpacks them into the application's bin folder,
/// so a fresh build output always contains runnable cores. Exit code 0 when
/// everything is installed or already current; 1 when something could not be
/// installed.
/// </summary>
public static class CoreInstaller
{
    /// <summary>
    /// Headless install of one missing core over the SAME pipeline the app's
    /// update flow uses (UpdateService → CoreInfoManager download URLs →
    /// SHA-256 verification → unpack into bin\&lt;core&gt;). The GPN mihomo
    /// integration calls this when the core binary is absent so "GPN Bağlan"
    /// never dies on a missing executable. Returns true when the core is (or
    /// became) available; false when install was skipped or failed.
    /// </summary>
    /// <param name="coreType">Çekirdek (mihomo desteklenen ana akış).</param>
    /// <param name="updateFunc">İlerleme mesajları (notify=false).</param>
    /// <param name="cancellationToken">İptal.</param>
    /// <param name="isInstalledCheck">Test dikişi — kurulu mu? (varsayılan: CoreInfoManager yolları)</param>
    /// <param name="downloadInstall">Test dikişi — indir+kur. (varsayılan: gerçek UpdateService akışı)</param>
    public static async Task<bool> InstallMissingCoreAsync(
        ECoreType coreType,
        Func<bool, string, Task> updateFunc,
        CancellationToken cancellationToken = default,
        Func<ECoreType, bool>? isInstalledCheck = null,
        Func<ECoreType, Func<bool, string, Task>, CancellationToken, Task<bool>>? downloadInstall = null)
    {
        if (isInstalledCheck?.Invoke(coreType) ?? CoreExists(coreType))
        {
            return true; // zaten kurulu — no-op
        }

        if (downloadInstall is not null)
        {
            return await downloadInstall(coreType, updateFunc, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var config = AppManager.Instance.Config;
            // DownloadService pins the TLS chain through CertPemManager.
            await CertPemManager.Instance.Init(config);

            string? archive = null;
            var service = new UpdateService(config, (notify, msg) =>
            {
                if (notify && msg is { Length: > 0 } && File.Exists(msg))
                {
                    archive = msg; // indirilen arşiv yolu
                }
                else if (notify == false && msg.IsNotEmpty())
                {
                    _ = updateFunc(false, msg); // indirme ilerlemesi kullanıcıya
                }
                return Task.CompletedTask;
            });

            await updateFunc(false, $"{coreType} çekirdeği bulunamadı — en son sürüm indiriliyor…").ConfigureAwait(false);
            await service.CheckUpdateCore(coreType, false).ConfigureAwait(false);

            for (var i = 0; i < 50 && archive is null && !cancellationToken.IsCancellationRequested; i++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (archive is null || !File.Exists(archive))
            {
                await updateFunc(false, $"{coreType} indirilemedi (güncelleme kontrolü veya indirme başarısız).").ConfigureAwait(false);
                return false;
            }

            try
            {
                InstallCoreArchive(coreType, archive);
                await EnsureCoreWintunAsync(coreType).ConfigureAwait(false);
                await updateFunc(false, $"{coreType} çekirdeği kuruldu ✓").ConfigureAwait(false);
                return true;
            }
            finally
            {
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[CoreInstaller] {coreType} auto-install failed", ex);
            await updateFunc(false, $"{coreType} kurulamadı: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Çekirdek exe'lerinden biri mevcut mu? (CoreInfoManager yol düzeni)</summary>
    internal static bool CoreExists(ECoreType coreType)
    {
        try
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
            return coreInfo?.CoreExes is { } exes
                && exes.Any(name => File.Exists(Utils.GetBinPath(Utils.GetExeName(name), coreInfo.CoreType.ToString())));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[CoreInstaller] CoreExists failed", ex);
            return false;
        }
    }

    /// <summary>
    /// mihomo, Wintun sürücüsünü kendi klasöründen yükler (RunProcessNormal cwd'si
    /// exe dizinidir). Resmi zip wintun.dll içermez — uygulamanın kök kopyasından
    /// veya xray/sing-box paketinden kopyala; yoksa resmi Wintun dağıtımından indir.
    /// </summary>
    private static async Task EnsureCoreWintunAsync(ECoreType coreType)
    {
        if (coreType != ECoreType.mihomo)
        {
            return;
        }

        var target = Utils.GetBinPath("wintun.dll", "mihomo");
        if (File.Exists(target))
        {
            return;
        }

        var source = new[]
            {
                Utils.GetPath("wintun.dll"),
                Utils.GetBinPath("wintun.dll", ECoreType.Xray.ToString()),
                Utils.GetBinPath("wintun.dll", ECoreType.mihomo.ToString()),
            }
            .FirstOrDefault(File.Exists);
        if (source is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            Logging.SaveLog($"[CoreInstaller] wintun.dll copied to bin\\mihomo from {source}");
            return;
        }

        await DownloadWintunDllAsync(target);
        Logging.SaveLog("[CoreInstaller] wintun.dll downloaded for bin\\mihomo");
    }

    public static async Task<int> UpdateCoresAsync()
    {
        var config = AppManager.Instance.Config;
        // DownloadService pins the TLS chain through CertPemManager; mirror the
        // normal startup order (MainWindowViewModel.Init) so its config is set.
        await CertPemManager.Instance.Init(config);

        var failures = new List<string>();

        // A fresh output has neither cores nor geo data, and the packaging scripts
        // expect the same bin layout (cores + geoip/geosite + rule-sets), so fetch
        // the geo assets too when they are absent.
        if (!File.Exists(Utils.GetBinPath("geosite.dat"))
            || !File.Exists(Utils.GetBinPath("geoip.dat"))
            || !File.Exists(Utils.GetBinPath("Country.mmdb")))
        {
            try
            {
                var geoService = new UpdateService(config, (_, _) => Task.CompletedTask);
                await geoService.UpdateGeoFileAll();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("[CoreInstaller] geo assets update failed", ex);
                failures.Add($"geo: {ex.Message}");
            }
        }

        // mihomo: GPN WireGuard/Global VPN çekirdeği — taze build'lerde hazır olmalı.
        foreach (var coreType in new[] { ECoreType.Xray, ECoreType.mihomo })
        {
            string? archive = null;
            var service = new UpdateService(config, (notify, msg) =>
            {
                // UpdateService hands the downloaded archive path to updateFunc
                // with notify == true once the download completes.
                if (notify && msg is { Length: > 0 } && File.Exists(msg))
                {
                    archive = msg;
                }
                return Task.CompletedTask;
            });

            await service.CheckUpdateCore(coreType, false);

            // The download-completed callback is fire-and-forget inside
            // UpdateService; give it a moment to deliver the archive path.
            for (var i = 0; i < 50 && archive is null; i++)
            {
                await Task.Delay(100);
            }

            if (archive is null)
            {
                // Already current, unsupported, or check/download failed; the
                // failure was already reported through updateFunc/Logging.
                // A core that is still missing afterwards must be reported so
                // --update-cores (and the MSBuild targets that run it) do not
                // exit 0 as if everything had been installed.
                if (!CoreExists(coreType))
                {
                    failures.Add($"{coreType}: no archive was produced and the core is still missing");
                }
                continue;
            }

            try
            {
                InstallCoreArchive(coreType, archive);
                await EnsureCoreWintunAsync(coreType);
                Logging.SaveLog($"[CoreInstaller] {coreType} installed from {archive}");
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"[CoreInstaller] {coreType} install failed", ex);
                failures.Add($"{coreType}: {ex.Message}");
            }
            finally
            {
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }
            }
        }

        // The GPN capture bridge (WintunNative P/Invoke) loads wintun.dll from the
        // application directory using the default DLL search order. Xray's Windows
        // release bundles wintun.dll inside its own folder (bin\xray\), which the
        // bridge cannot see; promote that copy (or sing-box's) to the output root.
        // If neither core shipped one, fetch it from the official Wintun
        // distribution so a fresh install still gets a working capture bridge.
        await EnsureWintunDllAsync(failures);

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Puts a runnable <c>wintun.dll</c> next to the executable (the location the
    /// GPN capture bridge's P/Invoke probes first). Preferred sources, in order:
    /// an existing root copy, a copy bundled with the Xray/sing-box cores, and
    /// finally the official Wintun distribution (zip, amd64). Best-effort: any
    /// failure is recorded in <paramref name="failures"/> and the install continues.
    /// </summary>
    private static async Task EnsureWintunDllAsync(List<string> failures)
    {
        try
        {
            // P/Invoke exe'nin YANINDA (StartupPath) arar — bin\ altında değil.
            var rootDll = Utils.GetPath("wintun.dll");
            if (!File.Exists(rootDll))
            {
                // Xray Windows sürümü kendi klasöründe wintun.dll paketler;
                // mihomo da paketleyebilir. Hangisi varsa onu köke taşı.
                var coreDll = new[]
                    {
                        Utils.GetBinPath("wintun.dll", ECoreType.Xray.ToString()),
                        Utils.GetBinPath("wintun.dll", ECoreType.mihomo.ToString()),
                    }
                    .FirstOrDefault(File.Exists);
                if (coreDll is not null)
                {
                    File.Copy(coreDll, rootDll, overwrite: true);
                    Logging.SaveLog($"[CoreInstaller] wintun.dll promoted from {coreDll}");
                }
            }

            if (!File.Exists(rootDll))
            {
                await DownloadWintunDllAsync(rootDll);
                Logging.SaveLog("[CoreInstaller] wintun.dll downloaded from the official Wintun distribution");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[CoreInstaller] wintun.dll install failed", ex);
            failures.Add($"wintun: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches the official Wintun release (zip), extracts the amd64 build and
    /// places it at <paramref name="rootDll"/>. Uses the same DownloadService
    /// pipeline as the cores (TLS pinning via CertPemManager, proxy support).
    /// </summary>
    private static async Task DownloadWintunDllAsync(string rootDll)
    {
        var url = Global.WintunDownloadUrl;
        var archive = Utils.GetTempPath(Utils.GetGuid());
        try
        {
            var download = new DownloadService();
            await download.DownloadFileAsync(url, archive, true, 60);
            if (!File.Exists(archive))
            {
                throw new InvalidOperationException($"Wintun indirilemedi: {url}");
            }

            var extractDir = Utils.GetTempPath(Utils.GetGuid());
            Directory.CreateDirectory(extractDir);
            try
            {
                using var zip = ZipFile.OpenRead(archive);
                var amd64Entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Contains("amd64", StringComparison.OrdinalIgnoreCase)
                    && e.Name.Equals("wintun.dll", StringComparison.OrdinalIgnoreCase));
                if (amd64Entry is null)
                {
                    throw new InvalidOperationException("Wintun zip'inde amd64/wintun.dll bulunamadı.");
                }

                var extracted = Path.Combine(extractDir, "wintun.dll");
                amd64Entry.ExtractToFile(extracted, overwrite: true);
                Directory.CreateDirectory(Path.GetDirectoryName(rootDll)!);
                File.Copy(extracted, rootDll, overwrite: true);
            }
            finally
            {
                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, recursive: true);
                }
            }
        }
        finally
        {
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }
        }
    }

    /// <summary>
    /// Mirrors CheckUpdateViewModel.UpgradeCore's unpacking: tar.gz archives are
    /// flattened (releases often nest the binaries one directory deep), plain gz
    /// files are decompressed, everything else is treated as a zip.
    /// </summary>
    private static void InstallCoreArchive(ECoreType coreType, string archivePath)
    {
        var coreTypeName = coreType.ToString().ToLowerInvariant();
        var toPath = Utils.GetBinPath("", coreTypeName);

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            FileUtils.DecompressTarFile(archivePath, toPath);
            if (Directory.Exists(toPath))
            {
                foreach (var subDir in new DirectoryInfo(toPath).GetDirectories())
                {
                    FileUtils.CopyDirectory(subDir.FullName, toPath, true, true);
                    subDir.Delete(true);
                }
            }
        }
        else if (archivePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            FileUtils.DecompressFile(archivePath, toPath, coreTypeName);
        }
        else
        {
            FileUtils.ZipExtractToFile(archivePath, toPath, "geo");
        }
    }
}
