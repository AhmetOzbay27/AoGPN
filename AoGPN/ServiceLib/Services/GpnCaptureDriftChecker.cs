using ServiceLib.Models.Dto;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnCaptureDriftChecker — "canlı bağlantı var ama 0 paket yakalanıyor"
//
// GPN split-tunnel'ın en sessiz hata modunu görünür yapar: tünellenmesi
// gereken bir oyunun (GPN modunda vpn eylemli) canlı bağlantıları var ama
// yakalama köprüsü o süreçlerden hiç paket saymadıysa trafik doğrudan
// gidiyordur — ping düşmez, kullanıcı "tünel çalışıyor" sanır.
//
// Saf bir fonksiyondur: yakalanan paket istatistiklerini (GpnCaptureStatsSnapshot.
// ByPid — süreç başına paket/byte sayacı) ve aday uygulamaları (ad + canlı
// bağlantı sayısı + canlı bağlantılarının PID'leri) parametre olarak alır,
// sürüklenen uygulamaları döndürür. WPF/UI durumu tutmaz — test edilebilirlik
// için ayrı duruyor.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>Drift denetimi için aday: tünellenmesi gereken bir uygulamanın canlı anlık durumu.</summary>
public sealed record CaptureDriftCandidate(
    string ProcessName,
    long LiveConnections,
    IReadOnlyCollection<int> Pids);

/// <summary>Sürüklenen bir uygulamanın rapor satırı (canlı bağlantısı var, yakalanan paketi yok).</summary>
public sealed record CaptureDriftEntry(
    string ProcessName,
    long LiveConnections);

/// <summary>
/// Yakalama sürüklenme denetçisi. <see cref="FindDrifted"/> yalnızca şu
/// koşullarda sürüklenme raporlar: adayın canlı bağlantısı var (count &gt; 0),
/// canlı bağlantılarına ait PID'ler biliniyor (boş PID kümesi yargılanamaz —
/// çekirdek/kimliksiz sahipler uyarı üretmez) ve o PID'lerden HİÇBİRİ için
/// yakalanmış paket sayılmamış. Yalnızca &gt;0 paket sayan istatistikler
/// "yakalanmış" sayılır; 0 paketli bir satır yakalanmamış demektir.
/// </summary>
public static class GpnCaptureDriftChecker
{
    /// <summary>
    /// Sürüklenme adaylarını CANLI bağlantı tablosundan kurar. Yalnızca UDP
    /// bağlantıları aday sayılır: yakalama köprüsünün WinDivert filtresi NETWORK
    /// katmanında yalnızca outbound UDP yakalar (bkz. WinDivertFilterBuilder) —
    /// TCP bağlantıları tasarım gereği doğrudan gider, onlar için yakalanan paket
    /// beklentisi YOKTUR ve aday sayılmamaları gerekir (aksi hâlde LeagueClient /
    /// Discord gibi TCP ağırlıklı uygulamalar her oturumda kalıcı olarak "trafik
    /// doğrudan gidiyor" uyarısı üretirdi). PID kümesi ve canlı bağlantı sayısı da
    /// UDP satırlarından türetilir — sayı "yakalanabilir" bağlantı sayısını yansıtır.
    /// </summary>
    public static IReadOnlyList<CaptureDriftCandidate> BuildCandidates(
        IEnumerable<SplitTunnelAppItem> apps,
        IEnumerable<ConnectionMonitorItem> connections,
        bool invertRouting)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(connections);

        // Süreç başına UDP canlı bağlantı sayısı + benzersiz PID seti. Pid <= 0
        // (kimliksiz sahip) ve boş süreç adı satırları yargılanamaz — atlanır.
        var udpByProcess = new Dictionary<string, (int Count, List<int> Pids)>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in connections)
        {
            if (!string.Equals(connection.Protocol, "UDP", StringComparison.OrdinalIgnoreCase)
                || connection.Pid <= 0
                || string.IsNullOrEmpty(connection.ProcessName))
            {
                continue;
            }

            if (!udpByProcess.TryGetValue(connection.ProcessName, out var entry))
            {
                entry = (0, new List<int>());
                udpByProcess[connection.ProcessName] = entry;
            }
            entry.Count++;
            if (!entry.Pids.Contains(connection.Pid))
            {
                entry.Pids.Add(connection.Pid);
            }
            // Count bir değer alanıdır — sözlüğe geri yazılmazsa artış yerelde kaybolur.
            udpByProcess[connection.ProcessName] = entry;
        }

        var tunneledAction = invertRouting ? "direct" : "vpn";
        var candidates = new List<CaptureDriftCandidate>();
        foreach (var app in apps)
        {
            if (app.EntryType != "app"
                || !string.Equals(app.Action, tunneledAction, StringComparison.OrdinalIgnoreCase)
                || !udpByProcess.TryGetValue(app.Value, out var live))
            {
                continue;
            }

            candidates.Add(new CaptureDriftCandidate(app.Value, live.Count, live.Pids));
        }
        return candidates;
    }

    public static IReadOnlyList<CaptureDriftEntry> FindDrifted(
        IReadOnlyList<GpnPidStat>? capturedByPid,
        IEnumerable<CaptureDriftCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Süreç başına YALNIZCA paket sayan satırları topla (Packets > 0): köprü
        // hiç çalışmadıysa (null) veya yalnızca 0 paketli satırlar varsa küme boş
        // kalır ve tüm canlı adaylar sürüklenmiş sayılır.
        var capturedPids = new HashSet<int>();
        if (capturedByPid is not null)
        {
            foreach (var stat in capturedByPid)
            {
                if (stat.Packets > 0 && stat.Pid > 0)
                {
                    capturedPids.Add((int)stat.Pid);
                }
            }
        }

        var drifted = new List<CaptureDriftEntry>();
        foreach (var candidate in candidates)
        {
            if (candidate.LiveConnections <= 0 || candidate.Pids.Count == 0)
            {
                continue; // canlı bağlantı yok ya da PID atanamıyor — yargı yok
            }

            var captured = false;
            foreach (var pid in candidate.Pids)
            {
                if (capturedPids.Contains(pid))
                {
                    captured = true;
                    break;
                }
            }

            if (!captured)
            {
                drifted.Add(new CaptureDriftEntry(
                    candidate.ProcessName,
                    candidate.LiveConnections));
            }
        }

        return drifted;
    }
}