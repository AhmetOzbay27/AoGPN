using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.V2ray;

/// <summary>
/// Validates configs produced by <see cref="CoreConfigV2rayService.GenerateClientConfigContent"/>
/// against the real bundled Xray binary via <c>xray run -test</c>. The scenario is the real
/// VLESS + REALITY node (92.5.108.102 / swdist.apple.com) that failed to connect against a
/// modern 3x-ui/Xray server while v2rayN (newer bundled core) succeeded: the generated config
/// must carry the exact shortId/publicKey/SNI the server validates, and the bundled Xray must
/// accept the generated schema. Skips when no xray.exe is present in the build output (e.g. CI
/// jobs that never downloaded the core assets).
/// </summary>
[Collection("SharedDatabase")]
public class XrayBinaryConfigValidationTests
{
    [Fact]
    public void VlessRealityNode_IsAcceptedByBundledXray()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateAlmanyaRealityNode();

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);
        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"config generation failed: {result.Msg}");
        result.Data.Should().NotBeNull();

        // The REALITY identity the server validates (shortId must be in the server's list,
        // publicKey must match the server's private key, SNI must be a server name) has to
        // survive config generation unchanged — a stale/wrong value shows up here first.
        var v2rayConfig = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        var proxy = v2rayConfig.outbounds.Should().ContainSingle(o => o.tag == Global.ProxyTag && o.protocol == "vless").Subject;
        proxy.streamSettings.Should().NotBeNull();
        proxy.streamSettings!.security.Should().Be("reality");
        proxy.streamSettings.realitySettings.Should().NotBeNull();
        proxy.streamSettings.realitySettings!.publicKey.Should().Be("8Whf79j5Dsv9xXeE8Z845PPp9xdjEuSjH69QmoSADFg");
        proxy.streamSettings.realitySettings.shortId.Should().Be("0123456789abcdef");
        proxy.streamSettings.realitySettings.serverName.Should().Be("swdist.apple.com");
        proxy.streamSettings.realitySettings.fingerprint.Should().Be("chrome");
        proxy.streamSettings.realitySettings.spiderX.Should().Be("/ff854c8280b2137");

        AssertAcceptedByBundledXray(context, "vless-reality-almanya");
    }

    /// <summary>
    /// The real Almanya profile as stored in the app database: a VLESS + REALITY node
    /// served by 3x-ui / Xray 26.7.28 at 92.5.108.102:443.
    /// </summary>
    private static ProfileItem CreateAlmanyaRealityNode()
    {
        var node = new ProfileItem
        {
            ConfigType = EConfigType.VLESS,
            Address = "92.5.108.102",
            Port = 443,
            Password = "b831381d-6324-4d53-ad4f-8cda48b30811",
            Remarks = "Almanya",
            Network = "raw",
            StreamSecurity = "reality",
            Sni = "swdist.apple.com",
            Fingerprint = "chrome",
            PublicKey = "8Whf79j5Dsv9xXeE8Z845PPp9xdjEuSjH69QmoSADFg",
            ShortId = "0123456789abcdef",
            SpiderX = "/ff854c8280b2137",
            AllowInsecure = string.Empty,
        };
        node.SetProtocolExtra(node.GetProtocolExtra() with
        {
            Flow = "xtls-rprx-vision",
            VlessEncryption = Global.None,
        });
        node.SetTransportExtra(node.GetTransportExtra() with
        {
            RawHeaderType = "none",
        });
        return node;
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

        var tmpDir = Path.Combine(Path.GetTempPath(), "aogpn-xray-check", Guid.NewGuid().ToString("N"));
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

        // The repo root found above is the directory that contains AoGPN.slnx;
        // the WPF app project lives one level below it.
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
