using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Services;
using ServiceLib.Services.Gpn;
using Xunit;

namespace ServiceLib.Tests.Services.Gpn;

/// <summary>
/// WarpAutoRecoverService — WARP dial sağlığı faulted olunca aktif tüneli
/// yeniden başlatma kararları. Sağlık/bağlantı olayları kanallar yerine
/// doğrudan ProcessHealth/ProcessState ile beslenir (kanalsız, deterministik).
/// </summary>
public class WarpAutoRecoverServiceTests
{
    private static WarpDialHealth Faulted(int errors = 3)
        => new(true, errors, "connect tcp 10.66.66.1:40000: no route to host",
            DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow);

    [Fact]
    public async Task FaultedHealth_TriggersReconnect()
    {
        var calls = 0;
        var service = new WarpAutoRecoverService(_ =>
        {
            calls++;
            return Task.FromResult(true);
        });

        await service.ProcessHealth(Faulted());

        calls.Should().Be(1);
        service.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task HealthyHealth_DoesNotReconnect()
    {
        var calls = 0;
        var service = new WarpAutoRecoverService(_ =>
        {
            calls++;
            return Task.FromResult(true);
        });

        await service.ProcessHealth(WarpDialHealth.Healthy);

        calls.Should().Be(0);
        service.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Cooldown_BlocksImmediateSecondAttempt()
    {
        var calls = 0;
        var service = new WarpAutoRecoverService(
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            },
            cooldown: TimeSpan.FromSeconds(90));

        await service.ProcessHealth(Faulted());
        await service.ProcessHealth(Faulted());

        calls.Should().Be(1);
    }

    [Fact]
    public async Task CooldownExpiry_AllowsNextAttempt()
    {
        var calls = 0;
        var service = new WarpAutoRecoverService(
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            },
            cooldown: TimeSpan.FromSeconds(30));
        service.NowUtc = DateTimeOffset.UtcNow;

        await service.ProcessHealth(Faulted());
        service.NowUtc = service.NowUtc.AddSeconds(31);
        await service.ProcessHealth(Faulted());

        calls.Should().Be(2);
    }

    [Fact]
    public async Task MaxAttempts_StopsFurtherRecovery()
    {
        var calls = 0;
        var service = new WarpAutoRecoverService(
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            },
            cooldown: TimeSpan.Zero,
            maxAttempts: 2);

        await service.ProcessHealth(Faulted());
        await service.ProcessHealth(Faulted());
        await service.ProcessHealth(Faulted());

        calls.Should().Be(2);
        service.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task ConnectionChange_ResetsAttemptCounter()
    {
        var calls = 0;
        var service = new WarpAutoRecoverService(
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            },
            cooldown: TimeSpan.Zero,
            maxAttempts: 1);

        await service.ProcessHealth(Faulted());
        calls.Should().Be(1);

        service.ProcessState(new GpnConnectionSnapshot(
            GpnConnectionState.Connected, ConnectionMode.WireGuardUDP,
            null, null, DateTimeOffset.UtcNow, null));

        await service.ProcessHealth(Faulted());
        calls.Should().Be(2, "bağlantı değişimi sayaçları sıfırlar — yeni oturum yeniden deneyebilir");
    }

    [Fact]
    public async Task DisabledSetting_DoesNotReconnect()
    {
        var calls = 0;
        var service = new WarpAutoRecoverService(
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            },
            isEnabled: () => false);

        await service.ProcessHealth(Faulted());

        calls.Should().Be(0);
    }

    [Fact]
    public async Task FailedReconnect_IsLoggedAndDoesNotThrow()
    {
        var service = new WarpAutoRecoverService(_ => Task.FromResult(false));

        await service.ProcessHealth(Faulted());

        service.Attempts.Should().Be(1);
    }
}