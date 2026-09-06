using ServiceLib.Models.Dto;
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

    private static SplitTunnelAppItem App(string value, string action = "vpn")
        => new() { EntryType = "app", Value = value, Action = action };

    private static ConnectionMonitorItem Conn(string process, int pid, string protocol = "UDP")
        => new() { ProcessName = process, Pid = pid, Protocol = protocol };

    // ── BuildCandidates (UDP-only candidate derivation) ─────────────────────

    [Fact]
    public void TcpOnlyLiveConnections_ProduceNoCandidates()
    {
        // Regresyon: LeagueClient/Discord gibi TCP ağırlıklı uygulamaların canlı
        // TCP bağlantıları vardır ama köprü yalnızca outbound UDP yakalar — TCP
        // için yakalanan paket beklentisi olmadığından aday üretilmemeli (eski
        // davranış her oturumda kalıcı yanlış "trafik doğrudan gidiyor" uyarısı
        // üretiyordu).
        var apps = new[] { App("LeagueClient.exe"), App("Discord.exe") };
        var connections = new[]
        {
            Conn("LeagueClient.exe", 100, "TCP"),
            Conn("Discord.exe", 200, "TCP"),
        };

        var candidates = GpnCaptureDriftChecker.BuildCandidates(apps, connections, invertRouting: false);

        Assert.Empty(candidates);
    }

    [Fact]
    public void UdpLiveConnections_ProduceCandidateWithUdpCountAndPids()
    {
        var apps = new[] { App("Game.exe") };
        var connections = new[]
        {
            Conn("Game.exe", 100),
            Conn("Game.exe", 100),
            Conn("Game.exe", 101),
        };

        var candidates = GpnCaptureDriftChecker.BuildCandidates(apps, connections, invertRouting: false);

        var candidate = Assert.Single(candidates);
        Assert.Equal("Game.exe", candidate.ProcessName);
        Assert.Equal(3, candidate.LiveConnections); // canlı UDP satırı sayısı
        Assert.Equal(new[] { 100, 101 }, candidate.Pids.OrderBy(p => p)); // benzersiz PID seti
    }

    [Fact]
    public void MixedTcpAndUdp_CountsOnlyUdpPids()
    {
        // TCP satırları sayıma VE PID setine girmemeli: TCP yakalanamaz, yalnızca
        // UDP sahipliği sürüklenme yargısına girer.
        var apps = new[] { App("Game.exe") };
        var connections = new[]
        {
            Conn("Game.exe", 100),
            Conn("Game.exe", 101, "TCP"),
            Conn("Game.exe", 102, "TCP"),
        };

        var candidates = GpnCaptureDriftChecker.BuildCandidates(apps, connections, invertRouting: false);

        var candidate = Assert.Single(candidates);
        Assert.Equal(1, candidate.LiveConnections);
        Assert.Equal(new[] { 100 }, candidate.Pids);
    }

    [Fact]
    public void NonTunneledApps_ProduceNoCandidates()
    {
        // Yalnızca efektif olarak tünellenen (vpn / blacklist'te direct) uygulamalar adaydır.
        var apps = new[] { App("ProxyApp.exe", "proxy"), App("Game.exe", "vpn") };
        var connections = new[] { Conn("ProxyApp.exe", 100), Conn("Game.exe", 200) };

        var candidates = GpnCaptureDriftChecker.BuildCandidates(apps, connections, invertRouting: false);

        var candidate = Assert.Single(candidates);
        Assert.Equal("Game.exe", candidate.ProcessName);
    }

    [Fact]
    public void InvertedRouting_UsesDirectAsTunneledAction()
    {
        // Blacklist yönünde tünellenen eylem "direct"tir — adaylar ona göre kurulur.
        var apps = new[] { App("Game.exe", "direct"), App("Skip.exe", "vpn") };
        var connections = new[] { Conn("Game.exe", 100), Conn("Skip.exe", 200) };

        var candidates = GpnCaptureDriftChecker.BuildCandidates(apps, connections, invertRouting: true);

        var candidate = Assert.Single(candidates);
        Assert.Equal("Game.exe", candidate.ProcessName);
    }

    [Fact]
    public void PidZeroAndNamelessConnections_AreIgnored()
    {
        var apps = new[] { App("Game.exe") };
        var connections = new[]
        {
            Conn("Game.exe", 0),              // kimliksiz sahip — yargılanamaz
            new ConnectionMonitorItem { ProcessName = "", Pid = 100, Protocol = "UDP" },
        };

        var candidates = GpnCaptureDriftChecker.BuildCandidates(apps, connections, invertRouting: false);

        Assert.Empty(candidates);
    }

    [Fact]
    public void NoUdpConnections_EndToEnd_NoDriftReported()
    {
        // Kullanıcı senaryosunun uçtan uca regresyonu: TCP ağırlıklı uygulamalar
        // (bun/Freebuff/League/Riot/Discord) canlı bağlantılara sahipken tünel
        // sağlıklı — sürüklenme raporlanmamalı.
        var apps = new[]
        {
            App("bun.exe"), App("Freebuff.exe"), App("LeagueClient.exe"),
            App("RiotClient.exe"), App("RiotClientServices.exe"), App("Discord.exe"),
        };
        var connections = new[]
        {
            Conn("bun.exe", 1, "TCP"), Conn("Freebuff.exe", 2, "TCP"),
            Conn("LeagueClient.exe", 3, "TCP"), Conn("RiotClient.exe", 4, "TCP"),
            Conn("RiotClientServices.exe", 5, "TCP"), Conn("Discord.exe", 6, "TCP"),
        };

        var candidates = GpnCaptureDriftChecker.BuildCandidates(apps, connections, invertRouting: false);
        var drifted = GpnCaptureDriftChecker.FindDrifted(
            new[] { Stat(10, 5) }, // köprü çalışıyor (başka süreç paketleri görülüyor)
            candidates);

        Assert.Empty(drifted);
    }

    [Fact]
    public void UdpConnectionWithoutCapturedPackets_EndToEnd_IsDrifted()
    {
        // Gerçek sürüklenme hâlâ yakalanıyor: oyunun canlı UDP bağlantısı var,
        // köprü çalışıyor (başka süreçlerin paketleri sayılıyor) ama oyunun
        // PID'lerinden hiç paket yok — trafik doğrudan gidiyordur.
        var apps = new[] { App("Game.exe") };
        var connections = new[] { Conn("Game.exe", 100) };

        var candidates = GpnCaptureDriftChecker.BuildCandidates(apps, connections, invertRouting: false);
        var drifted = GpnCaptureDriftChecker.FindDrifted(
            new[] { Stat(10, 5) },
            candidates);

        var entry = Assert.Single(drifted);
        Assert.Equal("Game.exe", entry.ProcessName);
        Assert.Equal(1, entry.LiveConnections);
    }

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