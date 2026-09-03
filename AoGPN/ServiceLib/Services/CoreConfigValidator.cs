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
            ECoreType.sing_box => $"check -c {quotedPath}",
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

        try
        {
            using var process = new Process
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
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await stdout) + Environment.NewLine + (await stderr);
            if (process.ExitCode == 0)
            {
                return CoreValidationResult.Valid();
            }

            return new CoreValidationResult(false, output.Trim());
        }
        catch (OperationCanceledException)
        {
            return new CoreValidationResult(false, "Core configuration validation timed out.");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CoreConfigValidator", ex);
            return new CoreValidationResult(false, ex.Message);
        }
    }
}
