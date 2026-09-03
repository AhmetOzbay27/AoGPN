using AwesomeAssertions;
using ServiceLib.Common;
using Xunit;

namespace ServiceLib.Tests.Common;

/// <summary>
/// GpnSessionLog — bağlantı/test sürecini diske yazan oturum denetim günlüğü.
/// VPN kopunca canlı iletişim kesildiğinden loglar sonradan okunur; yapının
/// (oturum bloğu + ortam anlık görüntüsü + kırpma) doğruluğu burada test edilir.
/// Tüm testler geçici dosya üzerinde çalışır (gerçek LocalAppData'ya dokunmaz).
/// </summary>
[Collection("GpnSessionLogSerial")]
public class GpnSessionLogTests : IDisposable
{
    private readonly string _tempPath;

    public GpnSessionLogTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"gpn-session-{Guid.NewGuid():N}.log");
        GpnSessionLog.SetPathForTesting(_tempPath);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempPath))
            {
                File.Delete(_tempPath);
            }
        }
        catch
        {
            // best-effort
        }
        // Modül yönlendirmesine geri dön (sonraki testlerin DiagLog aynası da
        // gerçek LocalAppData günlüğünü kirletmesin).
        GpnSessionLog.SetPathForTesting(GpnSessionLogModule.TestPath);
    }

    [Fact]
    public void BeginRecordEnd_WritesStructuredSessionBlock()
    {
        GpnSessionLog.BeginSession("GPN connect candidates=2");
        GpnSessionLog.Record("GPN_SELECT best=de mode=WireGuardUDP");
        GpnSessionLog.Record("GPN_BRIDGE live server=de");
        GpnSessionLog.EndSession("GPN disconnect");

        var text = File.ReadAllText(_tempPath);
        text.Should().Contain("=== SESSION START ");
        text.Should().Contain("| GPN connect candidates=2");
        text.Should().Contain("env os=", "ortam anlık görüntüsü yazılır");
        text.Should().Contain("GPN_SELECT best=de mode=WireGuardUDP");
        text.Should().Contain("GPN_BRIDGE live server=de");
        text.Should().Contain("=== SESSION END ");
        text.Should().Contain("| GPN disconnect | elapsed=");
    }

    [Fact]
    public void Record_WithoutBegin_AutoOpensFirstLogSession()
    {
        // GPN akışı BeginSession çağırmadan ilk satırı yazarsa otomatik açılır —
        // hiçbir diyagnoz satırı kaybolmaz.
        GpnSessionLog.Record("GPN_LOG connect start candidates=1");

        var text = File.ReadAllText(_tempPath);
        text.Should().Contain("=== SESSION START ");
        text.Should().Contain("| first-log");
        text.Should().Contain("GPN_LOG connect start candidates=1");
    }

    [Fact]
    public void BeginSession_WhileActive_IsIdempotent()
    {
        GpnSessionLog.BeginSession("ilk");
        GpnSessionLog.BeginSession("ikinci");
        GpnSessionLog.EndSession("bitiş");

        var text = File.ReadAllText(_tempPath);
        text.Should().Contain("| ilk");
        text.Should().NotContain("| ikinci", "aktif oturum varken ikinci begin no-op");
    }

    [Fact]
    public void EndSession_WithoutActiveSession_IsNoOp()
    {
        GpnSessionLog.EndSession("yok oturum");

        File.Exists(_tempPath).Should().BeFalse("oturum yokken dosya oluşturulmaz");
    }

    [Fact]
    public void Trim_KeepsOnlyLastFiveSessions()
    {
        for (var i = 1; i <= 7; i++)
        {
            GpnSessionLog.BeginSession($"oturum-{i}");
            GpnSessionLog.Record($"GPN_LOG adim-{i}");
            GpnSessionLog.EndSession("bitti");
        }

        var text = File.ReadAllText(_tempPath);
        text.Should().NotContain("oturum-1");
        text.Should().NotContain("oturum-2");
        text.Should().Contain("oturum-3");
        text.Should().Contain("oturum-7");
        CountOccurrences(text, "=== SESSION START ").Should().Be(5);
    }

    [Fact]
    public void DiagLogWrite_MirrorsIntoSessionFile()
    {
        // DiagLog aynası: GPN_* diyagnoz satırı oturum dosyasına da düşer.
        GpnSessionLog.BeginSession("diyagnoz");
        DiagLog.Write("GPN_TEST mirror-check");
        GpnSessionLog.EndSession("bitti");

        var text = File.ReadAllText(_tempPath);
        text.Should().Contain("GPN_TEST mirror-check");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
