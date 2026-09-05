using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// GpnCaptureDriftChecker — \"canlı bağlantı var ama 0 paket yakalanıyor\"
/// denetçisinin saf birim testleri. A2 açığının regresyon ağı: tünellenmesi
/// gereken bir uygulamanın canlı bağlantıları varken yakalama köprüsü onun
/// PID'lerinden hiç paket saymadıysa uygulama sürüklenmiş sayılır (trafik
/// doğrudan gidiyordur — sessiz hata görünür olur).
/// </summary>
public class GpnCaptureDriftCheckerTests
{
    private static GpnPidStat Stat(uint pid, long packets) => new(pid, packets, 0, true);

    private static CaptureDriftCandidate Candidate(
        string name = "Game.exe",
        long live = 3,
        params int[] pids) => new(name, live, pids);

    private static IReadOnlyList<CaptureDriftEntry> Find(
        IReadOnlyList<GpnPidStat>? stats,
        params CaptureDriftCandidate[] candidates)
        => GpnCaptureDriftChecker.FindDrifted(stats, candidates);

    [Fact]
    public void AppWithCapturedPackets_IsNotDrifted()
    {
        var stats = new[] { Stat(100, 42) };
        var drifted = Find(stats, Candidate("Game.exe", 3, 100));

        Assert.Empty(drifted);
    }

    [Fact]
    public void LiveConnectionsWithNoCapturedPackets_IsDrifted()
    {
        var stats = new[] { Stat(200, 10) }; // başka süreç yakalanıyor
        var drifted = Find(stats, Candidate("Game.exe", 5, 100, 101));

        var entry = Assert.Single(drifted);
        Assert.Equal("Game.exe", entry.ProcessName);
        Assert.Equal(5, entry.LiveConnections);
    }

    [Fact]
    public void NoStatsAtAll_AllLiveCandidatesAreDrifted()
    {
        // Köprü hiç paket saymadı (stats null) — canlı adayların hepsi sürüklenmiş.
        var drifted = Find(
            null,
            Candidate("GameA.exe", live: 2, pids: 100),
            Candidate("GameB.exe", live: 1, pids: 200));

        Assert.Collection(
            drifted,
            e => Assert.Equal("GameA.exe", e.ProcessName),
            e => Assert.Equal("GameB.exe", e.ProcessName));
    }

    [Fact]
    public void ZeroPacketStat_DoesNotCountAsCaptured()
    {
        // ByPid satırı var ama Paket 0 — yakalanmış sayılmaz (sürüklenme devam).
        var stats = new[] { Stat(100, 0) };
        var drifted = Find(stats, Candidate(live: 1, pids: 100));

        Assert.NotEmpty(drifted);
    }

    [Fact]
    public void ZeroLiveConnections_NotReported()
    {
        var drifted = Find(null, Candidate(live: 0, pids: 100));

        Assert.Empty(drifted);
    }

    [Fact]
    public void EmptyPids_NotJudged()
    {
        // PID atanamayan (kimliksiz sahip) bağlantılar yargılanmaz — yanlış
        // pozitif üretmemek için sessizce atlanır.
        var drifted = Find(null, Candidate(live: 4, pids: Array.Empty<int>()));

        Assert.Empty(drifted);
    }

    [Fact]
    public void CapturedAndDriftedApps_ReportedTogether()
    {
        var stats = new[]
        {
            Stat(100, 7),   // GameA yakalanıyor
            Stat(300, 0),   // GameC satırı var ama paket yok
        };
        var drifted = Find(
            stats,
            Candidate("GameA.exe", live: 2, pids: 100),
            Candidate("GameB.exe", live: 3, pids: 200),
            Candidate("GameC.exe", live: 1, pids: 300));

        Assert.Collection(
            drifted,
            e => Assert.Equal("GameB.exe", e.ProcessName),
            e => Assert.Equal("GameC.exe", e.ProcessName));
    }

    [Fact]
    public void NullCandidates_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            GpnCaptureDriftChecker.FindDrifted(null, null!));
    }
}