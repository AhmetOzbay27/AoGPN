namespace ServiceLib.Services;

public sealed record CoreValidationResult(bool Success, string Message)
{
    public static CoreValidationResult Valid() => new(true, string.Empty);
}

public static class CoreConfigValidator
{
    public static string? GetArguments(ECoreType coreType, string configPath)
    {
        var quotedPath = Utils.GetBinConfigPath(configPath).AppendQuotes();
        return coreType switch
        {
            ECoreType.Xray => $"run -test -config {quotedPath}",
            ECoreType.v2fly_v5 => $"run -test -c {quotedPath} -format jsonv5",
            ECoreType.mihomo => $"-t -f {quotedPath}",
            _ => null
        };
    }

    public static async Task<CoreValidationResult> ValidateAsync(
        ECoreType coreType,
        string executable,
        string configPath,
        string workingDirectory,
        IDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = GetArguments(coreType, configPath);
        if (arguments is null)
        {
            return CoreValidationResult.Valid();
        }

        // A hanging core check (e.g. a stuck `sing-box check` on an edge-case
        // speedtest config) must never block the app forever: bound the
        // validation subprocess and kill it on expiry. A check that does not
        // answer within the window is a failed check — the caller falls back to
        // its normal failure path instead of hanging the caller (observed live:
        // the dashboard "TCP + UDP" node test stuck on "Test ediliyor…" because
        // the UDP phase's core validation never returned).
        const int ValidationTimeoutSeconds = 15;

        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (environment != null)
            {
                foreach (var item in environment)
                {
                    if (item.Value != null)
                    {
                        process.StartInfo.Environment[item.Key] = item.Value;
                    }
                }
            }

            process.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ValidationTimeoutSeconds));
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = (await stdout) + Environment.NewLine + (await stderr);
            if (process.ExitCode == 0)
            {
                return CoreValidationResult.Valid();
            }

            return new CoreValidationResult(false, output.Trim());
        }
        catch (OperationCanceledException)
        {
            // The check subprocess hung — kill it (with its tree) so it cannot
            // linger, then report the timeout as a failed validation.
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process already exited between the timeout and the kill.
            }
            return new CoreValidationResult(false, "Core configuration validation timed out.");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CoreConfigValidator", ex);
            return new CoreValidationResult(false, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }
}
