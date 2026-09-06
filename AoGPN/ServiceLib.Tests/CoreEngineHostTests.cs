using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests;

public sealed class CoreBinaryRegistryTests
{
    [Fact]
    public void Resolve_FindsWindowsClientNameAndOptionalWintun()
    {
        var root = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "xray"));
            File.WriteAllText(Path.Combine(root, "xray", "xray.exe"), string.Empty);
            File.WriteAllText(Path.Combine(root, "wintun.dll"), string.Empty);

            var registry = new CoreBinaryRegistry(root, () => true);
            var result = registry.Validate(requireXray: true, requireTun: true);

            result.IsValid.Should().BeTrue();
            registry.Resolve(ECoreType.Xray).Should().Be(
                Path.Combine(root, "xray", "xray.exe"));
            result.Warnings.Should().BeEmpty();
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void Validate_ReportsRequiredCoreWithoutRejectingOptionalAssets()
    {
        var root = CreateTempDirectory();
        try
        {
            var registry = new CoreBinaryRegistry(root, () => true);
            var result = registry.Validate(requireXray: true);

            result.IsValid.Should().BeFalse();
            result.Errors.Should().ContainSingle(error => error.Contains("Xray", StringComparison.OrdinalIgnoreCase));
            result.Assets.Should().Contain(asset => asset.Name == ECoreType.Xray.ToString());
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "aogpn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

public sealed class ProcessCatalogServiceTests
{
    [Fact]
    public void Catalog_KeepsDifferentPathsWithTheSameProcessName()
    {
        var source = new InMemoryProcessSource(
            new ProcessCatalogCandidate(10, "game.exe", "Game A", @"C:\Games\A\game.exe", false),
            new ProcessCatalogCandidate(11, "game.exe", "Game B", @"C:\Games\B\game.exe", false),
            new ProcessCatalogCandidate(12, "svchost.exe", "Service Host", @"C:\Windows\System32\svchost.exe", false));

        var result = new ProcessCatalogService(source: source).GetRunningProcesses(TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Select(item => item.ExePath).Should().Contain(@"C:\Games\A\game.exe");
        result.Select(item => item.ExePath).Should().Contain(@"C:\Games\B\game.exe");
        result.Select(item => item.ProcessName).Should().NotContain("svchost.exe");
    }

    [Fact]
    public void Catalog_DeduplicatesRepeatedPathAndKeepsPathlessNames()
    {
        var source = new InMemoryProcessSource(
            new ProcessCatalogCandidate(10, "game", "Game", @"C:\Games\game.exe", false),
            new ProcessCatalogCandidate(10, "game.exe", "Game", @"C:\Games\game.exe", false),
            new ProcessCatalogCandidate(11, "unknown.exe", "Unknown", string.Empty, false),
            new ProcessCatalogCandidate(12, "unknown.exe", "Unknown duplicate", string.Empty, false));

        var result = new ProcessCatalogService(source: source).GetRunningProcesses(TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Count(item => item.ExePath.Equals(@"C:\Games\game.exe", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        result.Count(item => item.ProcessName == "unknown.exe").Should().Be(1);
    }

    [Theory]
    [InlineData("game", "game.exe")]
    [InlineData(@"C:\Games\GAME.EXE", "GAME.EXE")]
    [InlineData("", "")]
    public void NormalizeProcessName_AddsExeOnlyWhenNeeded(string value, string expected)
    {
        ProcessCatalogService.NormalizeProcessName(value).Should().Be(expected);
    }

    [Fact]
    public void TryResolveExecutablePath_CurrentProcess_ResolvesFullPath()
    {
        var service = new ProcessCatalogService(source: new InMemoryProcessSource());

        var resolved = service.TryResolveExecutablePath(Environment.ProcessId, out var path);

        resolved.Should().BeTrue();
        path.Should().NotBeNullOrWhiteSpace();
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void TryResolveExecutablePath_InvalidPid_ReturnsFalse()
    {
        var service = new ProcessCatalogService(source: new InMemoryProcessSource());

        service.TryResolveExecutablePath(0, out var path).Should().BeFalse();
        service.TryResolveExecutablePath(-5, out path).Should().BeFalse();
        path.Should().BeEmpty();
    }

    [Fact]
    public void ResolvePathLimitedQuery_CurrentProcess_ResolvesWithoutMainModule()
    {
        // Exercises the anti-cheat fallback: a limited-information query that
        // protected processes (BattlEye-guarded games) still allow, unlike the
        // MainModule path (PROCESS_QUERY_INFORMATION | PROCESS_VM_READ).
        var path = ProcessCatalogService.ResolvePathLimitedQuery(Environment.ProcessId);

        path.Should().NotBeNullOrWhiteSpace();
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void ResolvePathLimitedQuery_InvalidPid_ReturnsNull()
    {
        ProcessCatalogService.ResolvePathLimitedQuery(0).Should().BeNull();
        ProcessCatalogService.ResolvePathLimitedQuery(-1).Should().BeNull();
    }

    private sealed class InMemoryProcessSource : IProcessCatalogSource
    {
        private readonly IReadOnlyList<ProcessCatalogCandidate> _items;

        public InMemoryProcessSource(params ProcessCatalogCandidate[] items)
        {
            _items = items;
        }

        public IEnumerable<ProcessCatalogCandidate> Enumerate(CancellationToken cancellationToken = default)
        {
            foreach (var item in _items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }
}

public sealed class DashboardMessagePolicyTests
{
    [Theory]
    [InlineData("toggle_connection")]
    [InlineData("list_running_processes")]
    [InlineData("add_running_process")]
    [InlineData("save_settings")]
    [InlineData("test_route")]
    [InlineData("add_domain_route")]
    [InlineData("move_route")]
    [InlineData("move_node")]
    [InlineData("edit_node")]
    [InlineData("import_wireguard_conf")]
    [InlineData("gpn_cluster_probe")]
    [InlineData("set_effects_tier")]
    [InlineData("set_window_behavior")]
    public void KnownActions_AreAllowed(string action)
    {
        DashboardMessagePolicy.IsAllowedAction(action).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("execute_shell")]
    [InlineData("toggle_connection ")]
    public void UnknownOrMalformedActions_AreRejected(string action)
    {
        DashboardMessagePolicy.IsAllowedAction(action).Should().BeFalse();
    }
}

public sealed class CoreEngineHostTests
{
    [Fact]
    public async Task ConcurrentStarts_AreSerializedAndStopRunsAfterThem()
    {
        var runtime = new RecordingRuntime();
        var proxyResetCount = 0;
        var root = CreateTempDirectory();
        try
        {
            var xrayDirectory = Path.Combine(root, "xray");
            Directory.CreateDirectory(xrayDirectory);
            File.WriteAllText(Path.Combine(xrayDirectory, "xray.exe"), string.Empty);

            var registry = new CoreBinaryRegistry(root, () => true);
            var host = new CoreEngineHost(
                new Config(),
                (_, _) => Task.CompletedTask,
                runtime: runtime,
                binaryRegistry: registry,
                resetProxy: () =>
                {
                    Interlocked.Increment(ref proxyResetCount);
                    return Task.CompletedTask;
                });

            var context = new CoreConfigContext
            {
                Node = new ProfileItem { IndexId = "test", Address = "127.0.0.1", Port = 443 },
                RunCoreType = ECoreType.Xray,
            };

            await Task.WhenAll(
                host.StartAsync(context, null, TestContext.Current.CancellationToken),
                host.StartAsync(context, null, TestContext.Current.CancellationToken));
            await host.StopAsync(TestContext.Current.CancellationToken);

            runtime.MaxConcurrentOperations.Should().Be(1);
            runtime.InitializeCount.Should().Be(1);
            runtime.StartCount.Should().Be(2);
            runtime.StopCount.Should().Be(1);
            proxyResetCount.Should().Be(1);

            await host.DisposeAsync();
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task StartAsync_NativeInProcessEngine_SkipsBinaryValidationAndInstaller()
    {
        // Tier 2 — native motor izolasyonu: seçilen strateji in-process ise
        // (UseNativeGpnEngine + WireGuard düğümü → NativeGpnStartStrategy) harici
        // mihomo.exe yokluğu başlatmayı ENGELLEMEZ — motor saf C#'tır. Boş binary
        // kökü + başarısız kurucu bile olsa StartAsync runtime'ı başlatır.
        var runtime = new RecordingRuntime();
        var installerCalls = new List<ECoreType>();
        var tempRoot = CreateTempDirectory();
        try
        {
            var registry = new CoreBinaryRegistry(tempRoot, () => true); // boş — mihomo exe yok
            var host = new CoreEngineHost(
                new Config(),
                (_, _) => Task.CompletedTask,
                runtime: runtime,
                binaryRegistry: registry,
                resetProxy: () => Task.CompletedTask,
                autoCoreInstaller: (coreType, _, _) =>
                {
                    installerCalls.Add(coreType);
                    return Task.FromResult(false);
                });

            var context = new CoreConfigContext
            {
                Node = new ProfileItem
                {
                    IndexId = "gpn-native",
                    ConfigType = EConfigType.WireGuard,
                    CoreType = ECoreType.mihomo,
                    Address = "127.0.0.1",
                    Port = 51820,
                },
                RunCoreType = ECoreType.mihomo,
                UseNativeGpnEngine = true,
            };

            await host.StartAsync(context, null, TestContext.Current.CancellationToken);

            runtime.StartCount.Should().Be(1, "native bağlam doğrulama bekletmeden başlar");
            installerCalls.Should().BeEmpty("in-process motor mihomo.exe indirmez");

            await host.StopAsync(TestContext.Current.CancellationToken);
            await host.DisposeAsync();
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task StartAsync_DormantContext_StillRequiresMihomoBinary()
    {
        // Koruma kuralı: şalter KAPALIYKEN (UseNativeGpnEngine=false — üretimdeki
        // mihomo yolu) binary süreci zerre değişmez: boş kök + başarısız kurucu →
        // runtime BAŞLAMAZ, "required mihomo" hatası fırlar (CoreAutoInstallTests
        // ile aynı davranış).
        var runtime = new RecordingRuntime();
        var installerCalls = new List<ECoreType>();
        var tempRoot = CreateTempDirectory();
        try
        {
            var registry = new CoreBinaryRegistry(tempRoot, () => true);
            var host = new CoreEngineHost(
                new Config(),
                (_, _) => Task.CompletedTask,
                runtime: runtime,
                binaryRegistry: registry,
                resetProxy: () => Task.CompletedTask,
                autoCoreInstaller: (coreType, _, _) =>
                {
                    installerCalls.Add(coreType);
                    return Task.FromResult(false);
                });

            var context = new CoreConfigContext
            {
                Node = new ProfileItem
                {
                    IndexId = "gpn-dormant",
                    ConfigType = EConfigType.WireGuard,
                    CoreType = ECoreType.mihomo,
                    Address = "127.0.0.1",
                    Port = 51820,
                },
                RunCoreType = ECoreType.mihomo,
                UseNativeGpnEngine = false,
            };

            var act = async () => await host.StartAsync(context, null, TestContext.Current.CancellationToken);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*required mihomo*");
            runtime.StartCount.Should().Be(0, "binary doğrulanamadan runtime başlatılmaz");
            installerCalls.Should().ContainSingle(x => x == ECoreType.mihomo,
                "eksik mihomo yine de indirme denenir (davranış değişmez)");
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    private sealed class RecordingRuntime : ICoreRuntime
    {
        private int _activeOperations;
        private int _maxConcurrentOperations;

        public int InitializeCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int MaxConcurrentOperations => _maxConcurrentOperations;

        // Simulates the real runtime's health lifecycle: Stopped until a start
        // brings the core up, then Ready with the listening port, and back to
        // Stopped on stop. CoreEngineHost.StartAsync verifies Main is Ready.
        private readonly Dictionary<CoreHealthRole, CoreHealthSnapshot> _health = new()
        {
            [CoreHealthRole.Main] = CoreHealthSnapshot.Stopped(CoreHealthRole.Main),
            [CoreHealthRole.PreSocks] = CoreHealthSnapshot.Stopped(CoreHealthRole.PreSocks),
        };

        public IReadOnlyDictionary<CoreHealthRole, CoreHealthSnapshot> Health => _health;

        public CoreHealthSnapshot GetHealth(CoreHealthRole role) => _health[role];

        public async Task InitializeAsync(Config config, Func<bool, string, Task> update)
        {
            Enter();
            try
            {
                InitializeCount++;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            finally
            {
                Exit();
            }
        }

        public async Task StartAsync(CoreConfigContext? mainContext, CoreConfigContext? preContext)
        {
            Enter();
            try
            {
                StartCount++;
                await Task.Delay(20, TestContext.Current.CancellationToken);
                _health[CoreHealthRole.Main] = new CoreHealthSnapshot(
                    CoreHealthRole.Main,
                    CoreHealthState.Ready,
                    ECoreType.mihomo,
                    port: 10808);
            }
            finally
            {
                Exit();
            }
        }

        public async Task StopAsync()
        {
            Enter();
            try
            {
                StopCount++;
                await Task.Delay(20, TestContext.Current.CancellationToken);
                _health[CoreHealthRole.Main] = CoreHealthSnapshot.Stopped(CoreHealthRole.Main);
            }
            finally
            {
                Exit();
            }
        }

        private void Enter()
        {
            var active = Interlocked.Increment(ref _activeOperations);
            while (true)
            {
                var current = Volatile.Read(ref _maxConcurrentOperations);
                if (active <= current
                    || Interlocked.CompareExchange(ref _maxConcurrentOperations, active, current) == current)
                {
                    return;
                }
            }
        }

        private void Exit() => Interlocked.Decrement(ref _activeOperations);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "aogpn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
