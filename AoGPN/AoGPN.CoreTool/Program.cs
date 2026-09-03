using ServiceLib;
using ServiceLib.Manager;
using ServiceLib.Services;

namespace AoGPN.CoreTool;

/// <summary>
/// Headless companion to the WPF app's "--update-cores" startup mode. It runs on
/// any platform (net10.0, no WPF dependency) and drives the same
/// <see cref="CoreInstaller"/> pipeline, so Linux/macOS packaging hosts can fetch
/// the host-appropriate sing-box/Xray cores and geo assets before packaging.
/// Cores land in a "bin" folder next to the tool; the packaging scripts rsync
/// that folder into the package staging area.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (!args.Contains(Global.UpdateCoresMode))
        {
            Console.Error.WriteLine($"Usage: AoGPN.CoreTool {Global.UpdateCoresMode}");
            return 2;
        }

        Console.WriteLine("AoGPN.CoreTool: initializing (config, logging, database)...");
        if (!AppManager.Instance.InitApp())
        {
            Console.Error.WriteLine("AoGPN.CoreTool: failed to load or create the GUI configuration.");
            return 1;
        }

        var exitCode = CoreInstaller.UpdateCoresAsync().GetAwaiter().GetResult();
        Console.WriteLine(exitCode == 0
            ? "AoGPN.CoreTool: cores and geo assets are ready under 'bin'."
            : "AoGPN.CoreTool: one or more assets could not be installed (see Logs for details).");
        return exitCode;
    }
}
