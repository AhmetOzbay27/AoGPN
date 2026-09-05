using AwesomeAssertions;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Services.CoreConfig.Mihomo;
using ServiceLib.Services.Gpn;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnBypassEgressController — WARP faulted iken launcher egress'inin canlı
/// (restart'sız) DIRECT'e çekilmesi ve sağlık gelince geri yüklenmesi.
/// Ağ erişimi delegelerle dışarıdan verilir (delegate-seam deseni): kapılar,
/// PUT vektörü ve doğrulama geri-okuması sahte Clash API üzerinde test edilir.
/// </summary>
public class GpnBypassEgressControllerTests
{
    private const string WarpSocks = GpnMihomoConfigService.WarpProxyName;

    private static Config ConfigWith(params (string Value, string Action)[] apps)
    {
        var config = new Config
        {
            ConnectionItem = new ConnectionSettingsItem
            {
                Mode = GameTriggerModes.Manual,
                ManualRoutes =
                [
                    .. apps.Select(a => new ManualRouteSetting
                    {
                        EntryType = "app",
                        Value = a.Value,
                        Action = a.Action,
                    }),
                ],
            },
        };
        return config;
    }

    private static GpnConnectionSnapshot ConnectedSnap()
        => new(GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, null, null, DateTimeOffset.UtcNow);

    private static WarpDialHealth Faulted() => new(true, 3, "no route to host", null, null);

    private sealed class FakeClash
    {
        public readonly Dictionary<string, ClashProxies.ProxiesItem> Proxies = new();
        public readonly List<(string Group, string Target)> Puts = new();
        public bool ApplyPuts = true; // false → PUT'lar canlı seçimi değiştirmez (doğrulama hatası)

        public FakeClash AddSelector(string name, params string[] members)
        {
            Proxies[name] = new ClashProxies.ProxiesItem
            {
                type = "Selector",
                all = members.ToList(),
                now = members[0],
            };
            return this;
        }

        public Task<Dictionary<string, ClashProxies.ProxiesItem>?> FetchAsync()
            => Task.FromResult<Dictionary<string, ClashProxies.ProxiesItem>?>(Proxies);

        public Task SetAsync(string group, string target)
        {
            Puts.Add((group, target));
            if (ApplyPuts && Proxies.TryGetValue(group, out var item))
            {
                item.now = target;
            }
            return Task.CompletedTask;
        }
    }

    private static (GpnBypassEgressController Controller, FakeClash Clash) Build(
        Config? config = null,
        bool mihomoRunning = true,
        bool connected = false)
    {
        var clash = new FakeClash();
        clash.AddSelector(GpnSoftRouting.BsgGroupName, WarpSocks, GpnSoftRouting.ClashDirect)
             .AddSelector(GpnSoftRouting.AppGroupName(0),
                 GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect,
                 GpnSoftRouting.ClashReject, WarpSocks)
             .AddSelector(GpnSoftRouting.AppGroupName(1),
                 GpnMihomoConfigService.NodesGroupName, GpnSoftRouting.ClashDirect,
                 GpnSoftRouting.ClashReject, WarpSocks);
        var controller = new GpnBypassEgressController(
            getConfig: () => config ?? ConfigWith(("EscapeFromTarkov.exe", "vpn"), ("BsGLauncher.exe", "warp")),
            isMihomoRunning: () => mihomoRunning,
            fetchProxies: clash.FetchAsync,
            setSelection: clash.SetAsync);
        if (connected)
        {
            controller.ProcessState(ConnectedSnap());
        }
        return (controller, clash);
    }

    [Fact]
    public async Task Fault_WhileConnected_DegradesBsgGroupAndWarpEntries()
    {
        var (controller, clash) = Build(connected: true);

        await controller.ProcessHealth(Faulted());

        // GPN-BSG + warp rotalı giriş (ao-1, BsGLauncher.exe) DIRECT'e çekilir;
        // vpn girişi (ao-0) dokunulmaz.
        clash.Puts.Should().Contain((GpnSoftRouting.BsgGroupName, GpnSoftRouting.ClashDirect));
        clash.Puts.Should().Contain((GpnSoftRouting.AppGroupName(1), GpnSoftRouting.ClashDirect));
        clash.Puts.Should().NotContain((GpnSoftRouting.AppGroupName(0), GpnSoftRouting.ClashDirect));
        controller.Degraded.Should().BeTrue();
    }

    [Fact]
    public async Task Healthy_AfterDegrade_RestoresWarpMember()
    {
        var (controller, clash) = Build(connected: true);
        await controller.ProcessHealth(Faulted());
        controller.Degraded.Should().BeTrue();

        await controller.ProcessHealth(WarpDialHealth.Healthy);

        clash.Puts.Should().Contain((GpnSoftRouting.BsgGroupName, WarpSocks));
        clash.Puts.Should().Contain((GpnSoftRouting.AppGroupName(1), WarpSocks));
        controller.Degraded.Should().BeFalse();
    }

    [Fact]
    public async Task Fault_WhileDisconnected_DoesNothing()
    {
        var (controller, clash) = Build(connected: false);

        await controller.ProcessHealth(Faulted());

        clash.Puts.Should().BeEmpty();
        controller.Degraded.Should().BeFalse();
    }

    [Fact]
    public async Task Fault_WhenCoreIsNotMihomo_DoesNothing()
    {
        var (controller, clash) = Build(connected: true, mihomoRunning: false);

        await controller.ProcessHealth(Faulted());

        clash.Puts.Should().BeEmpty();
        controller.Degraded.Should().BeFalse();
    }

    [Fact]
    public async Task Fault_WhenBsgGroupMissing_DoesNothing()
    {
        var (controller, clash) = Build(connected: true);
        clash.Proxies.Remove(GpnSoftRouting.BsgGroupName);

        await controller.ProcessHealth(Faulted());

        clash.Puts.Should().BeEmpty();
        controller.Degraded.Should().BeFalse();
    }

    [Fact]
    public async Task VerificationMismatch_KeepsNotDegraded()
    {
        var (controller, clash) = Build(connected: true);
        clash.ApplyPuts = false; // PUT'lar canlı seçimi değiştirmez → geri-okuma başarısız

        await controller.ProcessHealth(Faulted());

        // PUT'lar denendi ama doğrulanamadı — işaret dönmez, sonraki olay dener.
        clash.Puts.Should().Contain((GpnSoftRouting.BsgGroupName, GpnSoftRouting.ClashDirect));
        controller.Degraded.Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_ResetsDegradedState()
    {
        var (controller, _) = Build(connected: true);
        await controller.ProcessHealth(Faulted());
        controller.Degraded.Should().BeTrue();

        controller.ProcessState(new GpnConnectionSnapshot(
            GpnConnectionState.Disconnected, ConnectionMode.WireGuardUDP, null, null, DateTimeOffset.UtcNow));

        controller.Degraded.Should().BeFalse();
    }

    [Fact]
    public async Task NoWarpEntries_OnlyBsgGroupDegrades()
    {
        var config = ConfigWith(("EscapeFromTarkov.exe", "vpn"), ("chrome.exe", "direct"));
        var (controller, clash) = Build(config: config, connected: true);

        await controller.ProcessHealth(Faulted());

        // Yalnızca sabit BSG egress grubu DIRECT'e çekilir; giriş gruplarına dokunulmaz.
        clash.Puts.Should().Equal(
            (GpnSoftRouting.BsgGroupName, GpnSoftRouting.ClashDirect));
        controller.Degraded.Should().BeTrue();
    }

    [Fact]
    public async Task Fault_Twice_IsIdempotent()
    {
        var (controller, clash) = Build(connected: true);
        await controller.ProcessHealth(Faulted());
        var putsAfterFirst = clash.Puts.Count;

        await controller.ProcessHealth(Faulted());

        clash.Puts.Count.Should().Be(putsAfterFirst);
        controller.Degraded.Should().BeTrue();
    }
}