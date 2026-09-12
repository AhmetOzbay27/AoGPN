using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnConnectTimeline — bağlanma aşamalarının süre ölçümü (Faz 0).
///
/// Neden test var: bu sınıfın çıktısı ("GPN_TIMING total=… icmp=… udp=… launch=…")
/// optimizasyon kararlarının KANITI. Yanlış sıralama, kaybolan aşama veya paralel
/// fazlarda yarış, "10 saniye nereye gitti" sorusunu tekrar tahmine çevirir.
/// Zaman kaynağı dışarıdan verildiği için gerçek bekleme yapılmaz.
/// </summary>
public class GpnConnectTimelineTests
{
    [Fact]
    public void MarksAreReportedAsElapsedSinceStart()
    {
        var now = 0L;
        var timeline = new GpnConnectTimeline(() => now);

        now = 120;
        timeline.Mark("icmp");
        now = 480;
        timeline.Mark("udp");
        now = 900;
        timeline.Mark("connected");

        timeline.Summarize().Should().Be("GPN_TIMING total=900ms icmp=120ms udp=480ms connected=900ms");
    }

    [Fact]
    public void ParallelStages_DoNotClobberEachOther()
    {
        // ICMP ve UDP ölçüm fazları PARALEL koşar ve ikisi de kendi bitiş anını
        // işaretler. Ölçümün iş parçacığı güvenli olması bu yüzden şart.
        var now = 0L;
        var timeline = new GpnConnectTimeline(() => now);

        Parallel.For(0, 200, i =>
        {
            timeline.Mark(i % 2 == 0 ? "icmp" : "udp");
        });

        timeline.StageNames.Should().Contain("icmp").And.Contain("udp");
    }

    [Fact]
    public void SummarizeWithoutMarks_StillReportsTotal()
        => new GpnConnectTimeline(() => 42L).Summarize().Should().Be("GPN_TIMING total=42ms");

    [Fact]
    public void EmptyMarkNamesAreIgnored()
    {
        var timeline = new GpnConnectTimeline(() => 1L);
        timeline.Mark("");
        timeline.Mark(null!);

        timeline.StageNames.Should().BeEmpty();
    }

    [Fact]
    public void MarkCountIsBounded()
    {
        // Beklenmedik bir döngü diyagnoz satırını saniyelerce uzatmasın.
        var timeline = new GpnConnectTimeline(() => 0L);
        for (var i = 0; i < 200; i++)
        {
            timeline.Mark($"stage{i}");
        }

        timeline.StageNames.Count.Should().BeLessThanOrEqualTo(48);
    }

    [Fact]
    public void RepeatedStageName_KeepsLastOccurrence()
    {
        // Yeniden denemeler aynı aşamayı birden çok kez işaretler; sonuncusu
        // gerçek bitiş anıdır.
        var now = 0L;
        var timeline = new GpnConnectTimeline(() => now);

        now = 100;
        timeline.Mark("launch");
        now = 700;
        timeline.Mark("launch");
        now = 800;

        timeline.Summarize().Should().Contain("launch=700ms")
            .And.NotContain("launch=100ms");
    }
}
