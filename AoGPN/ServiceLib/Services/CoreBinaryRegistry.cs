namespace ServiceLib.Services;

/// <summary>
/// Resolves and validates the executable assets AoGPN is allowed to launch.
/// The registry is deliberately independent of <see cref="CoreManager"/> so
/// packaging and startup checks can be tested without starting a core.
/// </summary>
public sealed class CoreBinaryRegistry
{
    private readonly string _binaryRoot;
    private readonly Func<bool> _isWindows;

    public CoreBinaryRegistry(
        string? binaryRoot = null,
        Func<bool>? isWindows = null)
    {
        _binaryRoot = Path.GetFullPath(binaryRoot ?? Utils.GetBinPath(string.Empty));
        _isWindows = isWindows ?? Utils.IsWindows;
    }

    public string BinaryRoot => _binaryRoot;

    public CoreBinaryValidationResult Validate(
        bool requireSingBox = false,
        bool requireXray = false,
        bool requireTun = false,
        ECoreType? requiredCore = null)
    {
        var assets = new List<CoreBinaryAsset>();
        var errors = new List<string>();
        var warnings = new List<string>();

        var singBox = ResolveCoreBinary(
            ECoreType.sing_box,
            ["sing-box-client", "sing-box"]);
        var xray = ResolveCoreBinary(ECoreType.Xray, ["xray"]);
        var wintun = ResolveOptionalAsset("wintun.dll");

        assets.Add(singBox);
        assets.Add(xray);
        assets.Add(wintun);

        if (requireSingBox && !singBox.Exists)
        {
            errors.Add($"Core executable missing: sing-box executable not found under '{GetCoreDirectory(ECoreType.sing_box)}'.");
        }

        if (requireXray && !xray.Exists)
        {
            errors.Add($"Core executable missing: Xray executable not found under '{GetCoreDirectory(ECoreType.Xray)}'.");
        }

        if (requiredCore is { } coreType && Resolve(coreType) is null)
        {
            errors.Add($"Core executable missing: required {coreType} executable is not installed under '{GetCoreDirectory(coreType)}'.");
        }

        // Keep the driver check diagnostic-only. Depending on the release archive,
        // sing-box may load the driver from its own directory or use a bundled build.
        if (requireTun && _isWindows() && !wintun.Exists)
        {
            warnings.Add("No standalone wintun.dll was found; the selected core must provide its Windows TUN dependency.");
        }

        if (requireTun && !_isWindows())
        {
            warnings.Add("Windows Wintun validation was skipped on this platform.");
        }

        return new CoreBinaryValidationResult(
            _binaryRoot,
            assets,
            errors,
            warnings);
    }

    public string? Resolve(ECoreType coreType)
    {
        var names = coreType switch
        {
            ECoreType.sing_box => new[] { "sing-box-client", "sing-box" },
            ECoreType.Xray => new[] { "xray" },
            _ => CoreInfoManager.Instance.GetCoreInfo(coreType)?.CoreExes?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray() ?? [],
        };

        if (names.Length == 0)
        {
            return null;
        }

        var asset = ResolveCoreBinary(coreType, names);
        return asset.Exists ? asset.Path : null;
    }

    public static void ThrowIfInvalid(CoreBinaryValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.IsValid)
        {
            return;
        }

        throw new InvalidOperationException(validation.ErrorSummary);
    }

    private CoreBinaryAsset ResolveCoreBinary(
        ECoreType coreType,
        IReadOnlyList<string> names)
    {
        var directory = GetCoreDirectory(coreType);
        foreach (var name in names)
        {
            var candidateName = NormalizeExecutableName(name);
            var candidate = Path.GetFullPath(Path.Combine(directory, candidateName));
            if (!IsInsideRoot(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            return new CoreBinaryAsset(coreType.ToString(), candidate, true, false);
        }

        var missingName = NormalizeExecutableName(names[0]);
        return new CoreBinaryAsset(
            coreType.ToString(),
            Path.Combine(directory, missingName),
            false,
            false);
    }

    private CoreBinaryAsset ResolveOptionalAsset(string name)
    {
        var candidates = new[]
        {
            Path.Combine(GetCoreDirectory(ECoreType.sing_box), name),
            Path.Combine(GetCoreDirectory(ECoreType.Xray), name),
            Path.Combine(_binaryRoot, name),
        };

        foreach (var candidate in candidates.Select(Path.GetFullPath))
        {
            if (IsInsideRoot(candidate) && File.Exists(candidate))
            {
                return new CoreBinaryAsset(name, candidate, true, true);
            }
        }

        return new CoreBinaryAsset(name, candidates[0], false, true);
    }

    private string NormalizeExecutableName(string name)
    {
        if (!_isWindows() || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return name + ".exe";
    }

    private string GetCoreDirectory(ECoreType coreType) =>
        Path.Combine(_binaryRoot, CoreDirectoryName(coreType));

    private bool IsInsideRoot(string path)
    {
        var root = _binaryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _binaryRoot
            : _binaryRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string CoreDirectoryName(ECoreType coreType) =>
        coreType switch
        {
            ECoreType.sing_box => "sing_box",
            ECoreType.Xray => "xray",
            _ => coreType.ToString().ToLowerInvariant(),
        };
}

public sealed record CoreBinaryAsset(
    string Name,
    string Path,
    bool Exists,
    bool Optional);

public sealed class CoreBinaryValidationResult
{
    public CoreBinaryValidationResult(
        string binaryRoot,
        IReadOnlyList<CoreBinaryAsset> assets,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings)
    {
        BinaryRoot = binaryRoot;
        Assets = assets;
        Errors = errors;
        Warnings = warnings;
    }

    public string BinaryRoot { get; }
    public IReadOnlyList<CoreBinaryAsset> Assets { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsValid => Errors.Count == 0;
    public string ErrorSummary => string.Join(Environment.NewLine, Errors);
}
