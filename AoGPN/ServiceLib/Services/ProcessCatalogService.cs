namespace ServiceLib.Services;

/// <summary>
/// Discovers running user applications for the split-routing picker. Process
/// discovery is advisory; persisted routing rules remain authoritative.
/// </summary>
public sealed class ProcessCatalogService
{
    private static readonly HashSet<string> DefaultExcludedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "aogpn.exe",
            "xray.exe",
            "sing-box.exe",
            "sing-box-client.exe",
            "mihomo.exe",
            "v2ray.exe",
            "openvpn.exe",
            "enableloopback.exe",
            "conhost.exe",
            "csrss.exe",
            "dwm.exe",
            "explorer.exe",
            "lsass.exe",
            "services.exe",
            "smss.exe",
            "spoolsv.exe",
            "svchost.exe",
            "system.exe",
            "system idle process.exe",
            "wininit.exe",
            "winlogon.exe",
        };

    private readonly int _maxItems;
    private readonly IProcessCatalogSource _source;
    private readonly ISet<string> _excludedNames;

    public ProcessCatalogService(
        int maxItems = 300,
        IProcessCatalogSource? source = null,
        IEnumerable<string>? excludedNames = null)
    {
        if (maxItems is < 1 or > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems));
        }

        _maxItems = maxItems;
        _source = source ?? new WindowsProcessCatalogSource();
        _excludedNames = new HashSet<string>(DefaultExcludedNames, StringComparer.OrdinalIgnoreCase);
        if (excludedNames != null)
        {
            foreach (var name in excludedNames)
            {
                var normalized = NormalizeProcessName(name);
                if (normalized.Length > 0)
                {
                    _excludedNames.Add(normalized);
                }
            }
        }
    }

    public IReadOnlyList<ProcessCatalogItem> GetRunningProcesses(
        CancellationToken cancellationToken = default)
    {
        var itemsByIdentity = new Dictionary<string, ProcessCatalogItem>(StringComparer.OrdinalIgnoreCase);
        var identitiesByPid = new Dictionary<int, string>();

        foreach (var candidate in _source.Enumerate(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Pid <= 0)
            {
                continue;
            }

            var processName = NormalizeProcessName(candidate.ProcessName);
            if (processName.Length == 0 || _excludedNames.Contains(processName))
            {
                continue;
            }

            var exePath = NormalizePath(candidate.ExePath);
            var identity = exePath.Length > 0
                ? $"path:{exePath}"
                : $"name:{processName}";
            var item = ToItem(candidate, processName, exePath);

            // A process can appear more than once if path inspection races with
            // enumeration. Prefer the path-bearing record for the same PID.
            if (identitiesByPid.TryGetValue(candidate.Pid, out var oldIdentity))
            {
                if (itemsByIdentity.TryGetValue(oldIdentity, out var oldItem)
                    && oldItem.ExePath.Length > 0)
                {
                    continue;
                }

                if (!string.Equals(oldIdentity, identity, StringComparison.OrdinalIgnoreCase))
                {
                    itemsByIdentity.Remove(oldIdentity);
                }
            }

            if (itemsByIdentity.ContainsKey(identity))
            {
                continue;
            }

            itemsByIdentity[identity] = item;
            identitiesByPid[candidate.Pid] = identity;

            if (itemsByIdentity.Count >= _maxItems)
            {
                break;
            }
        }

        return itemsByIdentity.Values
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ExePath, StringComparer.OrdinalIgnoreCase)
            .Take(_maxItems)
            .ToArray();
    }

    public bool TryResolveExecutablePath(int pid, out string executablePath)
    {
        executablePath = string.Empty;
        if (pid <= 0)
        {
            return false;
        }

        // Yol çözümü artık TEK bir yerde: ProcessPathResolver. Sıra bilerek
        // "önce en az yetki" — MainModule (PROCESS_QUERY_INFORMATION|VM_READ)
        // anti-cheat ve yükseltilmiş süreçlerde reddedilir ve her red bir
        // Win32Exception atışı (VS ilk şans gürültüsü) demektir; limited sorgu
        // hem daha ucuz hem ölçüldüğü üzere daha çok süreci çözer.
        var path = ProcessPathResolver.Resolve(pid);
        if (path is null)
        {
            return false;
        }

        executablePath = path;
        return true;
    }

    /// <summary>
    /// Resolves an executable path with PROCESS_QUERY_LIMITED_INFORMATION only.
    /// Works for many elevated/protected processes (anti-cheat guarded games)
    /// that deny MainModule access. Null when the process is gone or access is
    /// denied at this level too.
    /// </summary>
    internal static string? ResolvePathLimitedQuery(int pid)
        => ProcessPathResolver.ResolveLimitedOnly(pid);

    public static string NormalizeProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalizedValue = value.Trim().Replace('\\', '/');
        var name = Path.GetFileName(normalizedValue);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".exe";
    }

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var windowsPath = trimmed.Replace('/', '\\');
        if (windowsPath.Length >= 3
            && char.IsLetter(windowsPath[0])
            && windowsPath[1] == ':'
            && windowsPath[2] == '\\')
        {
            return windowsPath;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ProcessCatalogItem ToItem(
        ProcessCatalogCandidate candidate,
        string processName,
        string exePath) =>
        new(
            candidate.Pid,
            processName,
            string.IsNullOrWhiteSpace(candidate.DisplayName)
                ? Path.GetFileNameWithoutExtension(processName)
                : candidate.DisplayName.Trim(),
            exePath,
            candidate.IsElevatedProcess);
}

public sealed record ProcessCatalogCandidate(
    int Pid,
    string ProcessName,
    string DisplayName,
    string ExePath,
    bool IsElevatedProcess);

public interface IProcessCatalogSource
{
    IEnumerable<ProcessCatalogCandidate> Enumerate(CancellationToken cancellationToken = default);
}

public sealed class WindowsProcessCatalogSource : IProcessCatalogSource
{
    public IEnumerable<ProcessCatalogCandidate> Enumerate(
        CancellationToken cancellationToken = default)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessCatalogCandidate? candidate = null;
                try
                {
                    var processName = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(processName))
                    {
                        continue;
                    }

                    // Pid 0 ("System Idle Process") gibi sahte süreçlere hiç
                    // dokunmuyoruz: modül numaralandırması onlarda
                    // "Unable to enumerate the process modules." fırlatır ve
                    // zaten seçilebilir bir uygulama değiller.
                    var processId = process.Id;
                    if (processId <= 0)
                    {
                        continue;
                    }

                    // Tek çözümleme yolu: en az yetki önce (ProcessPathResolver).
                    var path = ProcessPathResolver.Resolve(processId) ?? string.Empty;

                    candidate = new ProcessCatalogCandidate(
                        process.Id,
                        processName,
                        processName,
                        path,
                        false);
                }
                catch (ArgumentException)
                {
                    // The process disappeared during inspection.
                }
                catch (InvalidOperationException)
                {
                    // The process exited or is no longer queryable.
                }

                if (candidate != null)
                {
                    yield return candidate;
                }
            }
        }
    }
}
