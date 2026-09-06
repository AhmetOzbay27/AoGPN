using AwesomeAssertions;

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// NativeGpnStartStrategy (Tier 1 — Native Motoru Canlıya Alma): yerel
/// WinDivert + WireGuard + Wintun motorunun strateji soketi. Fabrika yalnızca
/// UseNativeGpnEngine bayrağı + WireGuard düğümüyle seçer; StartAsync köprüyü
/// düğümden çevrilmiş GpnServerProfile ile canlıya alır (harici süreç yok),
/// AfterStopAsync temiz kapanışı köprüye delege eder. Köprü tabanlı entegrasyon
/// (EngineFailed → onExited köprüsü) GpnCaptureBridgeTests'te sahte sürücü
/// rig'iyle test edilir; burada saf fabrika/politika/refusal yüzeyi.
/// </summary>
public class NativeGpnStartStrategyTests
{
    // ── Fabrika yönlendirmesi (CoreStartStrategyFactory.For(context)) ─────

    [Fact]
    public void Factory_RoutesNativeOnlyWhenFlagAndWireGuardNode()
    {
        var wgNode = GpnCoreLauncher.BuildWireGuardProfile(Server("it", "İtalya"));
        var vlessNode = new ProfileItem { ConfigType = EConfigType.VLESS, CoreType = ECoreType.Xray };

        // Bayrak + WireGuard düğümü → yerel motor (seçim yolu açık).
        var nativeContext = Context(wgNode, ECoreType.mihomo, useNative: true);
        CoreStartStrategyFactory.For(nativeContext).Should().BeSameAs(NativeGpnStartStrategy.Instance);

        // Bayrak true ama düğüm WireGuard değil → native SEÇİLMEZ (savunma kuralı).
        var nonWgContext = Context(vlessNode, ECoreType.Xray, useNative: true);
        CoreStartStrategyFactory.For(nonWgContext).Should().BeSameAs(XrayStartStrategy.Instance);

        // DORMANT: varsayılan bağlam (bayrak false) her zaman süreç tabanlı eşlemeye
        // düşer — bugün hiçbir çağıran bayrağı kurmadığından davranış değişmez.
        var dormantContext = Context(wgNode, ECoreType.mihomo, useNative: false);
        CoreStartStrategyFactory.For(dormantContext).Should().BeSameAs(MihomoStartStrategy.Instance);

        // Diğer eşlemeler bağlam aşırı yüklemesinde de aynen korunur.
        CoreStartStrategyFactory.For(Context(vlessNode, ECoreType.Xray, useNative: false))
            .Should().BeSameAs(XrayStartStrategy.Instance);
        CoreStartStrategyFactory.For(Context(vlessNode, ECoreType.openvpn, useNative: false))
            .Should().BeSameAs(DefaultCoreStartStrategy.NativeTunnelInstance);
    }

    // ── In-process sözleşmesi: yalnızca native motor null process'e izin verir ─

    [Fact]
    public void InProcessEngine_OnlyNativeStrategyAllowsNullProcess()
    {
        var wgNode = GpnCoreLauncher.BuildWireGuardProfile(Server("it", "İtalya"));

        // Yerel motor: HARİCİ süreç yok — null ProcessService olağandır.
        CoreStartStrategyFactory.For(Context(wgNode, ECoreType.mihomo, useNative: true))
            .IsInProcessEngine.Should().BeTrue();

        // Harici çekirdeklerin tamamı katı kalır (null → "Core executable missing").
        CoreStartStrategyFactory.For(ECoreType.mihomo).IsInProcessEngine.Should().BeFalse();
        CoreStartStrategyFactory.For(ECoreType.Xray).IsInProcessEngine.Should().BeFalse();
        CoreStartStrategyFactory.For(ECoreType.openvpn).IsInProcessEngine.Should().BeFalse();
        CoreStartStrategyFactory.For(ECoreType.v2fly).IsInProcessEngine.Should().BeFalse();
        CoreStartStrategyFactory.For(ECoreType.hysteria2).IsInProcessEngine.Should().BeFalse();
    }

    // ── StartAsync: sessiz no-op YOKTUR ───────────────────────────────────

    [Fact]
    public async Task StartAsync_UnwiredInstance_ThrowsLoudly()
    {
        // Bayrak kuran bir çağıran köprü bağlamayı unutursa "Connected" tuzağına
        // düşülmesin: köprüsüz StartAsync YÜKSEK SESLE hata verir (koordinatör
        // bağlantıyı başarısız sayar). Dormant yolda hiçbir çağıran bayrağı
        // kurmadığından bu yol üretimde tetiklenmez.
        var node = GpnCoreLauncher.BuildWireGuardProfile(Server("de", "Almanya"));
        var context = Context(node, ECoreType.mihomo, useNative: true);

        var act = async () => await NativeGpnStartStrategy.Instance.StartAsync(context,
            launcher: (_, _, _, _, _, _) => Task.FromResult<ProcessService?>(null),
            onExited: () => { });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CaptureBridge*");
    }

    [Fact]
    public async Task StartAsync_NonWireGuardNode_RefusesWithoutBridge()
    {
        // Düğüm kontrolü köprü çözümünden ÖNCE gelir — köprüsüz örnek bile
        // WireGuard dışı düğümü reddeder (savunma katmanı, sessiz ret).
        var context = Context(new ProfileItem { ConfigType = EConfigType.VLESS, CoreType = ECoreType.Xray },
            ECoreType.Xray, useNative: true);

        var process = await NativeGpnStartStrategy.Instance.StartAsync(context,
            launcher: (_, _, _, _, _, _) => Task.FromResult<ProcessService?>(null),
            onExited: () => { });

        process.Should().BeNull();
    }

    [Fact]
    public void Policies_EngineOwnsItsTunNoSocksAndIsInProcess()
    {
        NativeGpnStartStrategy.Instance.OwnsTun.Should().BeTrue();
        NativeGpnStartStrategy.Instance.IsNativeTunnelCore.Should().BeTrue();
        NativeGpnStartStrategy.Instance.IsInProcessEngine.Should().BeTrue();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────

    private static CoreConfigContext Context(ProfileItem node, ECoreType runCoreType, bool useNative)
        => new()
        {
            Node = node,
            RunCoreType = runCoreType,
            UseNativeGpnEngine = useNative,
        };

    private static GpnServerProfile Server(string id, string name) => new(
        ServerId: id,
        Name: name,
        EndpointHost: "127.0.0.1",
        EndpointPort: 51820,
        ServerPublicKey: "5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=",
        ClientPrivateKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        ClientAddress: "10.66.66.2/24");
}