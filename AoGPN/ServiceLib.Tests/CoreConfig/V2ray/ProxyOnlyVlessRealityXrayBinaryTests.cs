using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler.Builder;
using ServiceLib.Models;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.V2ray;

/// <summary>
/// Validates the <b>proxy-only</b> config path against the real bundled Xray binary.
///
/// The regression this covers: when connection is Off and the system proxy is set
/// (proxy-only mode, the v2rayN behaviour), AoGPN used to force the node's core type to
/// sing-box merely because <c>TunModeItem.EnableTun</c> was set in the app config — even
/// though TUN is never used in proxy-only mode. v2rayN, by contrast, runs the very same
/// VLESS + REALITY node on Xray, and the handshake carries a real browser uTLS fingerprint.
/// A sing-box build (or an empty fingerprint) emits a raw Go/uTLS hello that Cloudflare
/// flags as "Suspicious behavior".
///
/// This test produces the exact proxy-only context (<c>Build(config, node, proxyOnly: true)</c>
/// followed by <see cref="SystemProxyOnlyService.ToProxyOnlyContext"/> — the same two steps as
/// <c>StartCoreAsync</c>) with <c>EnableTun = true</c> present, then asserts the generated
/// Xray config keeps the <c>Xray</c> core type, preserves the REALITY identity v2rayN's
/// working config carries (publicKey / shortId / SNI / browser fingerprint), and is accepted
/// by <c>xray run -test</c>. Skips the real-binary check when no xray.exe is in the build
/// output (e.g. CI jobs that never downloaded core assets).
/// </summary>
[Collection("SharedDatabase")]
public sealed class ProxyOnlyVlessRealityXrayBinaryTests
{
    // The REALITY identity and browser fingerprint from v2rayN's working deployment
    // (v7.24.4, the "Africa" VLESS+Reality server the user runs successfully). The
    // proxy-only core must reproduce these exactly from the stored node profile.
    private const string ServerAddress = "92.4.137.125";
    private const int ServerPort = 443;
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private const string RealityPublicKey = "JTew2n4FMkcVfGrplUM7fAa-zqrNii0ZpH_kyNdiVjY";
    private const string RealityShortId = "0123456789abcdef";
    private const string RealitySni = "www.microsoft.com";
    private const string BrowserFingerprint = "edge";

    [Fact]
    public async Task ProxyOnlyVlessReality_EnableTunTrue_KeepsXrayAndMatchesV2rayNRealityIdentity()
    {
        var config = CreateConfigWithTunEnabled();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        CreateTables();

        var node = CreateVlessRealityNode();
        await SQLiteHelper.Instance.ReplaceAsync(node);
        config.IndexId = node.IndexId;

        // Step 1 — SystemProxyOnlyService.StartCoreAsync builds with proxyOnly: true.
        var build = await CoreConfigContextBuilder.Build(config, node, proxyOnly: true);
        build.Success.Should().BeTrue(string.Join("; ", build.ValidatorResult.Errors));
        build.Context.RunCoreType.Should().Be(ECoreType.Xray,
            "proxy-only must keep the node's Xray core type despite EnableTun=true");
        build.Context.IsTunEnabled.Should().BeFalse("proxy-only mode never enables TUN");

        // Step 2 — StartCoreAsync converts to the proxy-only context (strips managed
        // rules; TUN stays off). This is the same transformation the service applies.
        var proxyOnlyContext = SystemProxyOnlyService.ToProxyOnlyContext(build.Context);

        var result = new CoreConfigV2rayService(proxyOnlyContext).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"config generation failed: {result.Msg}");
        result.Data.Should().NotBeNull();

        var v2rayConfig = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var proxy = v2rayConfig.outbounds.Should()
            .ContainSingle(o => o.tag == Global.ProxyTag && o.protocol == "vless").Subject;

        proxy.streamSettings.Should().NotBeNull();
        proxy.streamSettings!.security.Should().Be("reality");
        proxy.streamSettings.realitySettings.Should().NotBeNull();

        var reality = proxy.streamSettings.realitySettings!;
        reality.publicKey.Should().Be(RealityPublicKey,
            "the server validates the public key — a wrong value breaks the handshake");
        reality.shortId.Should().Be(RealityShortId,
            "shortId must be in the server's list — a wrong value breaks the handshake");
        reality.serverName.Should().Be(RealitySni,
            "SNI must be a real server name or the REALITY fallback is exposed");
        reality.fingerprint.Should().Be(BrowserFingerprint,
            "must present the same browser uTLS fingerprint v2rayN uses; otherwise Cloudflare flags the handshake");

        // Confirm the outbound dials the exact server v2rayN uses.
        var vnext = proxy.settings?.vnext?.Should().ContainSingle().Subject;
        vnext!.address.Should().Be(ServerAddress);
        vnext.port.Should().Be(ServerPort);
        var user = vnext.users.Should().ContainSingle().Subject;
        user.id.Should().Be(Uuid);
        user.flow.Should().Be("xtls-rprx-vision");

        // The proxy-only inbound routes system-proxy traffic to the local mixed port.
        v2rayConfig.inbounds.Should().Contain(i =>
            i.protocol == nameof(EInboundProtocol.mixed)
            && i.listen == Global.Loopback
            && i.port == AppManager.Instance.GetLocalPort(EInboundProtocol.socks));

        AssertAcceptedByBundledXray(proxyOnlyContext, "proxy-only-vless-reality");
    }

    private static Config CreateConfigWithTunEnabled()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        config.TunModeItem.EnableTun = true; // the trap — must NOT force sing-box in proxy-only
        config.CoreBasicItem.DefFingerprint = "chrome";
        config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedChange;
        config.ConnectionItem = new ConnectionSettingsItem { Mode = GameTriggerModes.Off };
        return config;
    }

    /// <summary>
    /// The "Africa" VLESS + REALITY profile as stored in the app database — the same
    /// node v2rayN runs successfully over the system proxy.
    /// </summary>
    private static ProfileItem CreateVlessRealityNode()
    {
        var node = new ProfileItem
        {
            IndexId = Guid.NewGuid().ToString("N"),
            ConfigType = EConfigType.VLESS,
            CoreType = ECoreType.Xray,
            Remarks = "Africa",
            Address = ServerAddress,
            Port = ServerPort,
            Password = Uuid,
            Network = nameof(ETransport.raw),
            StreamSecurity = Global.StreamSecurityReality,
            Sni = RealitySni,
            Fingerprint = BrowserFingerprint,
            PublicKey = RealityPublicKey,
            ShortId = RealityShortId,
            SpiderX = string.Empty,
            AllowInsecure = string.Empty,
            Subid = string.Empty,
        };
        node.SetProtocolExtra(node.GetProtocolExtra() with
        {
            Flow = "xtls-rprx-vision",
            VlessEncryption = Global.None,
        });
        node.SetTransportExtra(node.GetTransportExtra() with { RawHeaderType = "none" });
        return node;
    }

    private static void CreateTables()
    {
        // AppManager.InitApp RoutingItem'ı da yaratır; context builder (GetDefaultRouting)
        // bu tabloyu okur — test host'unda açıkça yaratılmazsa sorgu düşer.
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
    }

    private static void AssertAcceptedByBundledXray(CoreConfigContext context, string scenario)
    {
        var xrayBinary = LocateXrayBinary();
        if (xrayBinary is null)
        {
            Assert.Skip("xray binary not found in build output; skipping real-binary config validation.");
            return;
        }

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"[{scenario}] config generation failed: {result.Msg}");

        var tmpDir = Path.Combine(Path.GetTempPath(), "aogpn-proxyonly-xray", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var cfgPath = Path.Combine(tmpDir, "config.json");
            File.WriteAllText(cfgPath, result.Data!.ToString());

            var (exitCode, output) = Run(xrayBinary, $"run -test -c \"{cfgPath}\"");
            exitCode.Should().Be(0,
                $"[{scenario}] xray run -test rejected the generated config:{Environment.NewLine}{output}");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static string? LocateXrayBinary()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        var appBinRoot = Path.Combine(root, "AoGPN", "bin");
        if (!Directory.Exists(appBinRoot))
        {
            return null;
        }

        var binaryName = OperatingSystem.IsWindows() ? "xray.exe" : "xray";
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var configurationDir = Path.Combine(appBinRoot, configuration);
            if (!Directory.Exists(configurationDir))
            {
                continue;
            }

            foreach (var targetFramework in Directory.GetDirectories(configurationDir))
            {
                var candidate = Path.Combine(targetFramework, "bin", "xray", binaryName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AoGPN.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (process.ExitCode, output);
    }
}