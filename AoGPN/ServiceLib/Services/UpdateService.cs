namespace ServiceLib.Services;

public class UpdateService(Config config, Func<bool, string, Task> updateFunc)
{
    private readonly Config? _config = config;
    private readonly Func<bool, string, Task>? _updateFunc = updateFunc;
    private readonly int _timeout = 30;
    private static readonly string _tag = "UpdateService";

    public async Task CheckUpdateCore(ECoreType type, bool preRelease)
    {
        var url = string.Empty;
        var fileName = string.Empty;

        DownloadService downloadHandle = new();
        downloadHandle.UpdateCompleted += (sender2, args) =>
        {
            if (args.Success)
            {
                _ = UpdateFunc(false, ResUI.MsgDownloadV2rayCoreSuccessfully);
            }
            else
            {
                _ = UpdateFunc(false, args.Msg);
            }
        };
        downloadHandle.Error += (sender2, args) =>
        {
            _ = UpdateFunc(false, args.GetException().Message);
        };

        await UpdateFunc(false, string.Format(ResUI.MsgStartUpdating, type));
        var result = await CheckUpdateAsync(downloadHandle, type, preRelease);
        if (result.Success)
        {
            await UpdateFunc(false, string.Format(ResUI.MsgParsingSuccessfully, type));
            await UpdateFunc(false, result.Msg);

            url = result.Url.ToString();
            var ext = url.Contains(".tar.gz") ? ".tar.gz" : Path.GetExtension(url);
            // Stable per-type name so a transfer that dies mid-way can be RESUMED
            // by the next attempt (DownloaderHelper keeps the resume state in the
            // matching "<name>.download" file) instead of restarting from zero
            // every retry cycle.
            fileName = Utils.GetTempPath(type.ToString().ToLowerInvariant() + ext);
            await downloadHandle.DownloadFileAsync(url, fileName, true, _timeout);

            // Verify before signalling the unpacker; a core binary that fails its
            // SHA-256 check is discarded and never installed.
            if (File.Exists(fileName))
            {
                var verifyError = await VerifyDownloadedFileSha256Async(
                    downloadHandle, url, fileName, $"{type} core", requireChecksum: true);
                if (verifyError is not null)
                {
                    TryDeleteDownloadedFile(fileName);
                    await UpdateFunc(true, verifyError);
                }
                else
                {
                    await UpdateFunc(false, ResUI.MsgUnpacking);
                    try
                    {
                        await UpdateFunc(true, fileName);
                    }
                    catch (Exception ex)
                    {
                        await UpdateFunc(false, ex.Message);
                    }
                }
            }
        }
        else
        {
            if (!result.Msg.IsNullOrEmpty())
            {
                await UpdateFunc(false, result.Msg);
            }
        }
    }

    public async Task<UpdateResult> CheckHasUpdateOnly(ECoreType type, bool preRelease)
    {
        if (!CoreInfoManager.Instance.IsCheckUpdateSupported(type))
        {
            return new UpdateResult(false, ResUI.MsgNotSupport);
        }

        var downloadHandle = new DownloadService();
        var checkPreRelease = CoreInfoManager.Instance.GetCheckPreRelease(type, preRelease);
        return await CheckUpdateAsync(downloadHandle, type, checkPreRelease);
    }

    public async Task<List<string>> CheckHasUpdateOnlyAll(bool preRelease)
    {
        var msgs = new List<string>();
        foreach (var type in CoreInfoManager.Instance.GetCheckUpdateCoreTypes())
        {
            if (!(_config.CheckUpdateItem.SelectedCoreTypes?.Contains(type.ToString()) ?? true))
            {
                continue;
            }

            var result = await CheckHasUpdateOnly(type, preRelease);
            if (result.Success && result.Version != null)
            {
                var msg = string.Format(ResUI.MsgCheckUpdateHasNewVersion, type, result.Version);
                msgs.Add(msg);
                AppManager.Instance.SetLastCheckUpdateResult(type, msg);
            }
            else
            {
                AppManager.Instance.SetLastCheckUpdateResult(type, result.Msg);
            }
        }
        return msgs;
    }

    public async Task UpdateGeoFileAll()
    {
        await UpdateGeoFiles();
        await UpdateOtherFiles();
        await UpdateFunc(true, string.Format(ResUI.MsgDownloadGeoFileSuccessfully, "geo"));
    }

    #region CheckUpdate private

    private async Task<UpdateResult> CheckUpdateAsync(DownloadService downloadHandle, ECoreType type, bool preRelease)
    {
        try
        {
            var result = await GetRemoteVersion(downloadHandle, type, preRelease);
            if (!result.Success || result.Version is null)
            {
                return result;
            }
            return await ParseDownloadUrl(type, result);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new UpdateResult(false, ex.Message);
        }
    }

    /// <summary>
    /// GitHub "latest release" adresini üretir. Burada <c>Path.Combine</c> BİLEREK
    /// kullanılmaz: Windows'ta ayırıcı '\' olduğu için "…/releases\latest" gibi
    /// geçersiz bir adres üretiyordu ve çekirdek güncelleme kontrolü her seferinde
    /// başarısız oluyordu (canlı gözlenen: "StatusCode error: https://github.com/…/releases\latest").
    /// </summary>
    /// <returns>Adres; <paramref name="coreUrl"/> boş/null ise null.</returns>
    internal static string? BuildLatestReleaseUrl(string? coreUrl)
        => coreUrl.IsNullOrEmpty() ? null : $"{coreUrl.TrimEnd('/')}/latest";

    private async Task<UpdateResult> GetRemoteVersion(DownloadService downloadHandle, ECoreType type, bool preRelease)
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
        var tagName = string.Empty;
        if (preRelease)
        {
            var url = coreInfo?.ReleaseApiUrl;
            var result = await downloadHandle.TryDownloadString(url, true, Global.AppName);
            if (result.IsNullOrEmpty())
            {
                return new UpdateResult(false, "");
            }

            var gitHubReleases = JsonUtils.Deserialize<List<GitHubRelease>>(result);
            var gitHubRelease = preRelease ? gitHubReleases?.First() : gitHubReleases?.First(r => r.Prerelease == false);
            tagName = gitHubRelease?.TagName;
            //var body = gitHubRelease?.Body;
        }
        else
        {
            var url = BuildLatestReleaseUrl(coreInfo?.Url);
            if (url is null)
            {
                return new UpdateResult(false, "");
            }
            var lastUrl = await downloadHandle.UrlRedirectAsync(url, true);
            if (lastUrl == null)
            {
                return new UpdateResult(false, "");
            }

            tagName = lastUrl?.Split("/tag/").LastOrDefault();
        }
        return new UpdateResult(true, new SemanticVersion(tagName));
    }

    private async Task<SemanticVersion> GetCoreVersion(ECoreType type)
    {
        try
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
            var filePath = string.Empty;
            foreach (var name in coreInfo.CoreExes)
            {
                var vName = Utils.GetBinPath(Utils.GetExeName(name), coreInfo.CoreType.ToString());
                if (File.Exists(vName))
                {
                    filePath = vName;
                    break;
                }
            }

            if (!File.Exists(filePath))
            {
                var msg = string.Format(ResUI.NotFoundCore, @"", "", "");
                //ShowMsg(true, msg);
                return new SemanticVersion("");
            }

            var result = await Utils.GetCliWrapOutput(filePath, coreInfo.VersionArg);
            var echo = result ?? "";
            var version = string.Empty;
            switch (type)
            {
                case ECoreType.v2fly:
                case ECoreType.Xray:
                case ECoreType.v2fly_v5:
                    version = Regex.Match(echo, $"{coreInfo.Match} ([0-9.]+) \\(").Groups[1].Value;
                    break;

                case ECoreType.mihomo:
                    version = Regex.Match(echo, $"v[0-9.]+").Groups[0].Value;
                    break;

            }
            return new SemanticVersion(version);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new SemanticVersion("");
        }
    }

    private async Task<UpdateResult> ParseDownloadUrl(ECoreType type, UpdateResult result)
    {
        try
        {
            var version = result.Version ?? new SemanticVersion(0, 0, 0);
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
            var coreUrl = await GetUrlFromCore(coreInfo) ?? string.Empty;
            SemanticVersion curVersion;
            string message;
            string? url;
            switch (type)
            {
                case ECoreType.v2fly:
                case ECoreType.Xray:
                case ECoreType.v2fly_v5:
                    {
                        curVersion = await GetCoreVersion(type);
                        message = string.Format(ResUI.IsLatestCore, type, curVersion.ToVersionString("v"));
                        url = string.Format(coreUrl, version.ToVersionString("v"));
                        break;
                    }
                case ECoreType.mihomo:
                    {
                        curVersion = await GetCoreVersion(type);
                        message = string.Format(ResUI.IsLatestCore, type, curVersion);
                        url = string.Format(coreUrl, version.ToVersionString("v"));
                        break;
                    }
                case ECoreType.AoGPN:
                    {
                        curVersion = new SemanticVersion(Utils.GetVersionInfo());
                        message = string.Format(ResUI.IsLatestN, type, curVersion);
                        url = string.Format(coreUrl, version);
                        break;
                    }
                default:
                    throw new ArgumentException("Type");
            }

            if (curVersion >= version && version != new SemanticVersion(0, 0, 0))
            {
                return new UpdateResult(false, message);
            }

            result.Url = url;
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new UpdateResult(false, ex.Message);
        }
    }

    private async Task<string?> GetUrlFromCore(CoreInfo? coreInfo)
    {
        if (Utils.IsWindows())
        {
            var url = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlWinArm64,
                Architecture.X64 => coreInfo?.DownloadUrlWin64,
                _ => null,
            };

            if (coreInfo?.CoreType != ECoreType.AoGPN)
            {
                return url;
            }

            //Check for avalonia desktop windows version
            if (File.Exists(Path.Combine(Utils.GetBaseDirectory(), "libHarfBuzzSharp.dll")))
            {
                return url?.Replace(".zip", "-desktop.zip");
            }

            return url;
        }
        else if (Utils.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlLinuxArm64,
                Architecture.RiscV64 => coreInfo?.DownloadUrlLinuxRiscV64,
                Architecture.LoongArch64 => coreInfo?.DownloadUrlLinuxLoong64,
                Architecture.X64 => coreInfo?.DownloadUrlLinux64,
                _ => null,
            };
        }
        else if (Utils.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlOSXArm64,
                Architecture.X64 => coreInfo?.DownloadUrlOSX64,
                _ => null,
            };
        }
        return await Task.FromResult("");
    }

    #endregion CheckUpdate private

    #region Geo private

    private async Task UpdateGeoFiles()
    {
        var geoUrl = string.IsNullOrEmpty(_config?.ConstItem.GeoSourceUrl)
            ? Global.GeoUrl
            : _config.ConstItem.GeoSourceUrl;

        List<string> files = ["geosite", "geoip"];
        foreach (var geoName in files)
        {
            var fileName = $"{geoName}.dat";
            var targetPath = Utils.GetBinPath($"{fileName}");
            var url = string.Format(geoUrl, geoName);

            await DownloadGeoFile(url, fileName, targetPath);
        }
    }

    private async Task UpdateOtherFiles()
    {
        //If it is not in China area, no update is required
        if (_config.ConstItem.GeoSourceUrl.IsNotEmpty())
        {
            return;
        }

        foreach (var url in Global.OtherGeoUrls)
        {
            var fileName = Path.GetFileName(url);
            var targetPath = Utils.GetBinPath($"{fileName}");

            await DownloadGeoFile(url, fileName, targetPath);
        }
    }

    private async Task DownloadGeoFile(string url, string fileName, string targetPath)
    {
        var tmpFileName = Utils.GetTempPath(Utils.GetGuid());

        DownloadService downloadHandle = new();
        downloadHandle.UpdateCompleted += (sender2, args) =>
        {
            if (args.Success)
            {
                _ = UpdateFunc(false, string.Format(ResUI.MsgDownloadGeoFileSuccessfully, fileName));
            }
            else
            {
                _ = UpdateFunc(false, args.Msg);
            }
        };
        downloadHandle.Error += (sender2, args) =>
        {
            _ = UpdateFunc(false, args.GetException().Message);
        };

        await downloadHandle.DownloadFileAsync(url, tmpFileName, true, _timeout);

        if (!File.Exists(tmpFileName))
        {
            return;
        }

        // Data files (geo/rule-sets) are verified whenever GitHub publishes a
        // checksum, but a raw URL without one is accepted with a warning.
        var verifyError = await VerifyDownloadedFileSha256Async(
            downloadHandle, url, tmpFileName, fileName, requireChecksum: false);
        if (verifyError is not null)
        {
            TryDeleteDownloadedFile(tmpFileName);
            await UpdateFunc(true, verifyError);
            return;
        }

        try
        {
            File.Copy(tmpFileName, targetPath, true);
            File.Delete(tmpFileName);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
        }
    }

    private static void TryDeleteDownloadedFile(string fileName)
    {
        try
        {
            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// Verifies a downloaded file against the SHA-256 checksum GitHub publishes
    /// for release assets. The checksum source is resolved in order:
    /// <c>&lt;asset-url&gt;.sha256sum</c> (GitHub convention, used by some repos),
    /// then <c>&lt;asset-url&gt;.dgst</c> (OpenSSL digest files, published by
    /// XTLS/Xray-core). MetaCubeX/mihomo publishes no
    /// checksum asset at all, so when neither source exists the download is
    /// accepted unverified with a warning (same policy as the geo/rule-set
    /// path) — a hard failure would make every core update impossible.
    /// The checksum travels over the same pinned TLS as the asset, so this
    /// catches corrupted or tampered downloads (proxy/CDN interference, broken
    /// mirrors). It does not protect against the upstream repository itself
    /// being compromised — that would require a GPG signature.
    /// </summary>
    /// <param name="requireChecksum">
    /// True for executable payloads (cores, app updates): a checksum that is
    /// published but unreadable or mismatching fails the update. False for data
    /// files (geo assets). When NO checksum source exists for the asset, both
    /// paths accept the download with a warning.
    /// </param>
    /// <returns>Null when verified; otherwise a human-readable failure reason.</returns>
    private static async Task<string?> VerifyDownloadedFileSha256Async(
        DownloadService downloadHandle,
        string assetUrl,
        string downloadedFile,
        string displayName,
        bool requireChecksum)
    {
        string? checksumText = null;
        try
        {
            checksumText = await downloadHandle.TryDownloadString(assetUrl + ".sha256sum", true, Global.AppName);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        // XTLS/Xray-core publishes its checksum as "<asset>.dgst" (OpenSSL
        // digest format) instead of "<asset>.sha256sum" — try that convention
        // before concluding no checksum exists.
        if (checksumText.IsNullOrEmpty())
        {
            try
            {
                var dgstText = await downloadHandle.TryDownloadString(assetUrl + ".dgst", true, Global.AppName);
                if (TryParseDgstSha256(dgstText, out var dgstHash))
                {
                    checksumText = $"{dgstHash}  asset";
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
        }

        if (checksumText.IsNullOrEmpty())
        {
            if (requireChecksum)
            {
                // MetaCubeX/mihomo publishes no checksum
                // asset at all. A hard failure here would make every core
                // download unusable, so degrade to the same unverified
                // acceptance the geo/rule-set path uses, with a warning.
                Logging.SaveLog($"[UpdateService] No SHA-256 checksum is published for {displayName}; accepting unverified download.");
                return null;
            }
            Logging.SaveLog($"[UpdateService] No SHA-256 checksum available for {displayName}; accepting unverified download.");
            return null;
        }

        // GitHub's generated checksum files look like "<64 hex>  <asset-filename>"
        // (one entry per line, a leading '*' before the name is tolerated).
        if (!TryParseSha256Checksum(checksumText, out var expected))
        {
            if (requireChecksum)
            {
                return $"The checksum file for {displayName} has an unexpected format; the download was not applied.";
            }
            Logging.SaveLog($"[UpdateService] Checksum file for {displayName} has an unexpected format; accepting unverified download.");
            return null;
        }
        string actual;
        try
        {
            await using var stream = File.OpenRead(downloadedFile);
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return $"Could not compute the SHA-256 of {displayName}.";
        }

        if (!actual.Equals(expected, StringComparison.Ordinal))
        {
            Logging.SaveLog($"[UpdateService] SHA-256 mismatch for {displayName}: expected {expected}, got {actual}.");
            return $"SHA-256 mismatch for {displayName} (expected {expected}, got {actual}); the download was discarded.";
        }

        Logging.SaveLog($"[UpdateService] SHA-256 verified for {displayName}.");
        return null;
    }

    /// <summary>
    /// Parses GitHub's auto-generated release checksum file: one or more lines of
    /// "&lt;64 lowercase hex&gt;  &lt;asset-filename&gt;" (an optional leading '*' before the
    /// name is tolerated). Returns the first (normalised) hash found.
    /// </summary>
    internal static bool TryParseSha256Checksum(string? checksumText, out string expectedHash)
    {
        expectedHash = string.Empty;
        if (checksumText.IsNullOrEmpty())
        {
            return false;
        }

        var match = Regex.Match(checksumText, @"(?im)^\s*([0-9a-fA-F]{64})\s+(?:\*)?\S+\s*$");
        if (!match.Success)
        {
            return false;
        }

        expectedHash = match.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Parses an OpenSSL digest file ("&lt;asset&gt;.dgst", published by
    /// XTLS/Xray-core): lines in the form "SHA2-256= &lt;64 hex&gt;" (also tolerates
    /// "SHA256="). Returns the normalised hash when found.
    /// </summary>
    internal static bool TryParseDgstSha256(string? dgstText, out string expectedHash)
    {
        expectedHash = string.Empty;
        if (dgstText.IsNullOrEmpty())
        {
            return false;
        }

        var match = Regex.Match(dgstText, @"(?im)^\s*SHA2?-?256\s*=\s*([0-9a-fA-F]{64})\s*$");
        if (!match.Success)
        {
            return false;
        }

        expectedHash = match.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    #endregion Geo private

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }
}
