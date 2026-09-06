using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Otomatik çekirdek indirme (Adım 3): CoreInstaller.InstallMissingCoreAsync'ın
/// karar mantığı + CoreEngineHost'un eksik mihomo karşısında indirip yeniden
/// doğrulama akışı. Gerçek ağ çağrısı yok — indirme dikişleri test sahteleriyle
/// değiştirilir; registry tarafı ise GERÇEK CoreBinaryRegistry ile sınanır.
/// </summary>
[Collection("SharedDatabase")]
public class CoreAutoInstallTests
{
    // ── CoreInstaller.InstallMissingCoreAsync ─────────────────────────────

    [Fact]
    public async Task InstallMissingCore_CoreMissing_InvokesDownloaderWithMihomo()
    {
        var updates = new List<string>();
        var invoked = false;

        var ok = await CoreInstaller.InstallMissingCoreAsync(
            ECoreType.mihomo,
            (_, msg) => { updates.Add(msg); return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken,
            isInstalledCheck: _ => false,
            downloadInstall: (coreType, update, _) =>
            {
                invoked = true;
                coreType.Should().Be(ECoreType.mihomo);
                update(false, "indiriliyor…").GetAwaiter().GetResult();
                return Task.FromResult(true);
            });

        ok.Should().BeTrue();
        invoked.Should().BeTrue("eksik çekirdek için indirme tetiklenmeli");
    }

    [Fact]
    public async Task InstallMissingCore_CorePresent_IsNoOp()
    {
        var invoked = false;

        var ok = await CoreInstaller.InstallMissingCoreAsync(
            ECoreType.mihomo,
            (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken,
            isInstalledCheck: _ => true,
            downloadInstall: (_, _, _) =>
            {
                invoked = true;
                return Task.FromResult(true);
            });

        ok.Should().BeTrue("kurulu çekirdek için no-op başarı sayılır");
        invoked.Should().BeFalse("kurulu çekirdek asla indirilmez");
    }

    [Fact]
    public async Task InstallMissingCore_DownloadFails_ReturnsFalse()
    {
        var ok = await CoreInstaller.InstallMissingCoreAsync(
            ECoreType.mihomo,
            (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken,
            isInstalledCheck: _ => false,
            downloadInstall: (_, _, _) => Task.FromResult(false));

        ok.Should().BeFalse("indirme başarısızsa kurulum başarısız sayılır");
    }

    // ── CoreEngineHost.StartAsync → otomatik kurulum kancası ──────────────

    [Fact]
    public async Task StartAsync_MihomoMissing_AutoInstallsAndProceeds()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var updates = new List<string>();
        var installerCalls = new List<ECoreType>();
        var runtimeStarted = false;

        var tempRoot = NewTempRoot();
        try
        {
            var registry = new CoreBinaryRegistry(tempRoot, () => true);
            var runtime = new RecordingRuntime(() => runtimeStarted = true);
            var host = new CoreEngineHost(
                config,
                (notify, msg) => { lock (updates) { updates.Add($"{(notify ? "N" : "n")}:{msg}"); } return Task.CompletedTask; },
                runtime: runtime,
                binaryRegistry: registry,
                autoCoreInstaller: (coreType, _, _) =>
                {
                    lock (installerCalls) { installerCalls.Add(coreType); }
                    // "İndirme" başarılıysa binary'yi gerçek konuma yaz → yeniden
                    // doğrulama GERÇEK registry ile geçsin.
                    Directory.CreateDirectory(Path.Combine(tempRoot, "mihomo"));
                    File.WriteAllBytes(Path.Combine(tempRoot, "mihomo", "mihomo-windows-amd64-v1.exe"), [0x4D, 0x5A]);
                    return Task.FromResult(true);
                });

            var context = new CoreConfigContext
            {
                Node = new ProfileItem
                {
                    IndexId = "gpn-test",
                    ConfigType = EConfigType.WireGuard,
                    CoreType = ECoreType.mihomo,
                    Address = "127.0.0.1",
                    Port = 51820,
                },
                RunCoreType = ECoreType.mihomo,
                AppConfig = config,
                IsTunEnabled = false,
            };

            await host.StartAsync(context, null, TestContext.Current.CancellationToken);

            installerCalls.Should().ContainSingle(x => x == ECoreType.mihomo,
                "eksik mihomo indirilir ve yalnızca bir kez denenir");
            updates.Should().Contain(m => m.Contains("indiriliyor", StringComparison.OrdinalIgnoreCase));
            updates.Should().Contain(m => m.Contains("kuruldu", StringComparison.OrdinalIgnoreCase));
            runtimeStarted.Should().BeTrue("indirme sonrası çekirdek başlatılır");

            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_MihomoMissing_AutoInstallFails_ThrowsOriginalError()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);

        using var tempRoot = new TempDirCore();
        var registry = new CoreBinaryRegistry(tempRoot.Path, () => true);
        var host = new CoreEngineHost(
            config,
            (_, _) => Task.CompletedTask,
            runtime: new RecordingRuntime(() => { }),
            binaryRegistry: registry,
            autoCoreInstaller: (_, _, _) => Task.FromResult(false));

        var context = new CoreConfigContext
        {
            Node = new ProfileItem
            {
                IndexId = "gpn-test",
                ConfigType = EConfigType.WireGuard,
                CoreType = ECoreType.mihomo,
                Address = "127.0.0.1",
                Port = 51820,
            },
            RunCoreType = ECoreType.mihomo,
            AppConfig = config,
            IsTunEnabled = false,
        };

        var act = async () => await host.StartAsync(context, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*required mihomo*");
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aogpn-coreinst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingRuntime : ICoreRuntime
    {
        private readonly System.Action _onStart;

        public RecordingRuntime(System.Action onStart) => _onStart = onStart;

        public IReadOnlyDictionary<CoreHealthRole, CoreHealthSnapshot> Health { get; } =
            new Dictionary<CoreHealthRole, CoreHealthSnapshot>();

        public CoreHealthSnapshot GetHealth(CoreHealthRole role)
            => new(CoreHealthRole.Main, CoreHealthState.Ready, ECoreType.mihomo, 10808);

        public Task InitializeAsync(Config config, Func<bool, string, Task> update) => Task.CompletedTask;

        public Task StartAsync(CoreConfigContext? mainContext, CoreConfigContext? preContext)
        {
            _onStart();
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class TempDirCore : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aogpn-coreinst-{Guid.NewGuid():N}");

        public TempDirCore() => Directory.CreateDirectory(System.IO.Path.Combine(Path, "mihomo"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}