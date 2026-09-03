using System.Runtime.CompilerServices;
using ServiceLib.Common;

namespace ServiceLib.Tests.Common;

/// <summary>
/// Test derlemesi yüklenirken <see cref="GpnSessionLog"/>'u geçici bir dosyaya
/// yönlendirir — DiagLog aynası üzerinden testlerin yazdığı GPN_* satırları
/// kullanıcının gerçek &lt;startup&gt;\Logs\gpn-session.log dosyasını kirletmesin.
/// </summary>
internal static class GpnSessionLogModule
{
    public static readonly string TestPath =
        Path.Combine(Path.GetTempPath(), "gpn-session-tests.log");

    [ModuleInitializer]
    public static void Init()
    {
        GpnSessionLog.SetPathForTesting(TestPath);
    }
}
