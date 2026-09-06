using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services;

namespace ServiceLib.Tests;

public class CoreSoftReloadPolicyTests
{
    private static CoreConfigContext Context(ECoreType coreType, bool tunEnabled) => new()
    {
        Node = new ProfileItem { Remarks = "test" },
        RunCoreType = coreType,
        IsTunEnabled = tunEnabled,
    };

    [Fact]
    public void CanReload_MihomoToMihomo_SameTun_Alive_True()
    {
        var prev = Context(ECoreType.mihomo, tunEnabled: true);
        var next = Context(ECoreType.mihomo, tunEnabled: true);

        Assert.True(CoreSoftReloadPolicy.CanReload(prev, next,
            mainProcessAlive: true, runningCoreIsMihomo: true));
    }

    [Fact]
    public void CanReload_NoPreviousContext_False()
    {
        var next = Context(ECoreType.mihomo, tunEnabled: true);

        Assert.False(CoreSoftReloadPolicy.CanReload(null, next,
            mainProcessAlive: true, runningCoreIsMihomo: true));
    }

    [Fact]
    public void CanReload_NonMihomoCore_False()
    {
        var prev = Context(ECoreType.Xray, tunEnabled: true);
        var next = Context(ECoreType.Xray, tunEnabled: true);

        // Xray API reload aynı sözleşmeyi taşımaz — restart yolunda kal.
        Assert.False(CoreSoftReloadPolicy.CanReload(prev, next,
            mainProcessAlive: true, runningCoreIsMihomo: false));
    }

    [Fact]
    public void CanReload_CoreTypeChange_False()
    {
        var prev = Context(ECoreType.mihomo, tunEnabled: true);
        var next = Context(ECoreType.Xray, tunEnabled: true);

        Assert.False(CoreSoftReloadPolicy.CanReload(prev, next,
            mainProcessAlive: true, runningCoreIsMihomo: false));
    }

    [Fact]
    public void CanReload_DeadProcess_False()
    {
        var prev = Context(ECoreType.mihomo, tunEnabled: true);
        var next = Context(ECoreType.mihomo, tunEnabled: true);

        // Süreç öldüyse reload edilecek bir şey yok — restart kurtarması devreye girer.
        Assert.False(CoreSoftReloadPolicy.CanReload(prev, next,
            mainProcessAlive: false, runningCoreIsMihomo: true));
    }

    [Fact]
    public void CanReload_TunStateChanged_False()
    {
        // GPN ↔ Global geçişi: reload adaptörü/rotaları yeniden kurar; bu geçiş
        // restart yolunda kalır (güvenli taraf — Faz 1 kararı).
        var prev = Context(ECoreType.mihomo, tunEnabled: false);
        var next = Context(ECoreType.mihomo, tunEnabled: true);

        Assert.False(CoreSoftReloadPolicy.CanReload(prev, next,
            mainProcessAlive: true, runningCoreIsMihomo: true));
    }
}