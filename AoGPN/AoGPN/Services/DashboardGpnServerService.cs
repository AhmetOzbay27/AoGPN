using System.Text.Json;
using ServiceLib.Enums;
using ServiceLib.Handler.Fmt;
using ServiceLib.Helper;
using ServiceLib.Models;
using ServiceLib.Services;
using ServiceLib.ViewModels;

namespace AoGPN.Services;

// ─────────────────────────────────────────────────────────────────────────
// DashboardGpnServerService — GPN Sunucu Yönetimi iş mantığı (P0 Faz 2, 2. Dalga)
//
// MainWindow.xaml.cs içindeki gpn_servers kataloğu kümesi buraya taşındı:
// liste/durum yayını (PushGpnServersAsync / PushGpnDefaultsStatusAsync), canlı
// ölçüm (ProbeGpnServersAsync → failover matrisi + en iyi aday kartı) ve
// katalog CRUD (içe aktarma, silme, etkinleştirme, varsayılan geri yükleme).
// WPF ekleme/düzenleme penceresi (ShowGpnServerEditDialogAsync) DIALOG
// olduğu için MainWindow'da kaldı; bu servis yalnızca script yürütme
// (executeScript), hazır bayrağı ve ViewModel erişimiyle kurulur. Taşınan
// gövdelerde hiçbir satır değişmedi — davranış birebir korunur.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// GPN sunucu kataloğu (okuma/ölçüm/CRUD) iş mantığı. MainWindow tarafından
/// kurulur; IDashboardBridge sunucu üyeleri bu servise delege edilir.
/// </summary>
internal sealed class DashboardGpnServerService
{
    private readonly Func<string, Task> _executeScript;
    private readonly Func<bool> _isWebViewReady;
    private readonly Func<MainWindowViewModel?> _getViewModel;

    // Route-test results are pushed to the renderer with camelCase keys to match the
    // rest of the host→renderer payloads.
    private static readonly JsonSerializerOptions RouteTestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DashboardGpnServerService(
        Func<string, Task> executeScript,
        Func<bool> isWebViewReady,
        Func<MainWindowViewModel?> getViewModel)
    {
        _executeScript = executeScript ?? throw new ArgumentNullException(nameof(executeScript));
        _isWebViewReady = isWebViewReady ?? throw new ArgumentNullException(nameof(isWebViewReady));
        _getViewModel = getViewModel ?? throw new ArgumentNullException(nameof(getViewModel));
    }

    private Task ExecuteScriptSafelyAsync(string script) => _executeScript(script);
    private bool WebViewReady => _isWebViewReady();
    private MainWindowViewModel? ViewModel => _getViewModel();

    /// <summary>
    /// gpn_servers tablosunun meta verisini (özel anahtar HARİÇ — şifreli blob
    /// dahi renderer'a gönderilmez) dashboard Sunucu Yönetimi ekranına gönderir.
    /// </summary>
    public async Task PushGpnServersAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        var items = await WireGuardServerCatalog.GetItemsAsync();
        var view = (items ?? []).Select(item => new
        {
            ServerId = item.ServerId,
            Name = item.Name,
            EndpointHost = item.EndpointHost,
            EndpointPort = item.EndpointPort,
            ClientAddress = item.ClientAddress,
            Mtu = item.Mtu,
            Dns = item.Dns,
            Keepalive = item.Keepalive,
            IsEnabled = item.IsEnabled,
            KeyProtected = item.ClientPrivateKeyEnc.IsNotEmpty(),
            UpdatedAt = item.UpdatedAt,
        }).ToList();

        try
        {
            var json = JsonSerializer.Serialize(view, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnServers?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-servers push failed", ex);
        }
    }

    /// <summary>
    /// gpn_servers'taki etkin sunuculara canlı ölçüm yapar ve sonucu dashboard'a
    /// gönderir: <see cref="GpnServerSelectionService.ProbeAllAsync"/> (paralel ICMP
    /// ping → gecikme/kayıp) + <see cref="UdpHealthChecker"/> (51820/udp yolu).
    /// Ölçüm yalnızca okuma amaçlıdır — tünel başlatılmaz. Özel anahtar renderer'a
    /// hiç gönderilmez.
    /// </summary>
    public async Task ProbeGpnServersAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var servers = await WireGuardServerCatalog.LoadAsync();
            var enabled = servers.Where(s => s.IsEnabled).ToList();

            // Ölçüm, tek/önbellekli GpnServerSelectionService örneği üzerinden
            // MainWindowViewModel.ProbeServersAsync ile yürütülür.
            var probeResults = await ViewModel!.ProbeServersAsync(
                enabled,
                new GpnProbeOptions
                {
                    Samples = 3,
                    PerSampleTimeoutMs = 1000,

                    // Tünel (TUN auto_route + strict_route) etkinken ölçümü FİZİKSEL
                    // NIC üzerinden yap: ICMP atlanır (Ping arayüz bağlayamaz — tünel-içi
                    // ping anlamsız), UDP/TCP probe soketleri ProbeEgressNic ile fiziksel
                    // uplink'e bağlanır → kendi tünelinin içine yakalanmaz, gerçek sunucu
                    // erişilebilirliği ölçülür. Tünel yoksa no-op (normal ICMP + ölçüm).
                    EscapeTunnelForProbes = true,
                });

            // UDP sağlık testi — seçim/failover ile AYNI kanıt zinciri (el sıkışma +
            // junk): WireGuard sunucusu junk pakete yanıt vermediği için yalnızca
            // geçerli el sıkışma Open + RTT üretir; panel gecikme fallback'ini ve
            // gerçek UDP durumunu buradan besler.
            var udpList = await ViewModel!.ProbeUdpAllAsync(
                enabled,
                new GpnProbeOptions
                {
                    // El sıkışma UDP'si hafif zaman aşımıyla — ölçüm paneli hızlı kalsın.
                    HandshakeProbe = new WireGuardHandshakeProbeOptions(WaitTimeoutMs: 1500, MaxAttempts: 1),
                    UdpCheck = new UdpHealthCheckOptions(WaitTimeoutMs: 1500),
                    EscapeTunnelForProbes = true,
                });
            var udpResults = udpList.Select(u => (u.ServerId, Result: u)).ToList();

            var view = probeResults.Select(p => new
            {
                ServerId = p.ServerId,
                DelayMs = p.DelayMs,
                AvgDelayMs = p.AvgDelayMs,
                MaxDelayMs = p.MaxDelayMs,
                LossPercent = p.LossPercent,
                IsSuccess = p.IsSuccess,
                // Tünel etkinken ölçüm fiziksel NIC üzerinden yapıldı (ICMP atlandı) —
                // dashboard küme kartı bunu kullanıcıya ipucu olarak gösterir.
                MeasuredOverPhysicalNic = p.MeasuredOverPhysicalNic,
                UdpStatus = udpResults.FirstOrDefault(u => u.ServerId == p.ServerId).Result?.Status.ToString() ?? "unknown",
                UdpRoundTripMs = udpResults.FirstOrDefault(u => u.ServerId == p.ServerId).Result?.RoundTripMs,
                MeasuredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ToList();

            var json = JsonSerializer.Serialize(view, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnServerProbes?.({json});");

            // ── Failover matrisi: aynı ölçümü varsayılan + katı politika altında ──
            // Aynı GpnServerSelectionService (saf DecideFailover) ile hesaplanır;
            // dashboard yalnızca görüntüler. Aktif sunucu: bağlıysa o, değilse aday.
            await PushGpnFailoverMatrixAsync(enabled, probeResults, udpResults);

            // ── En iyi aday: GPN Bağlan'ın yapacağı otomatik seçim kararını önceden
            // göster (saf DecideSelection — SelectBestServerAsync ile birebir).
            await PushGpnSelectionPredictionAsync(enabled, probeResults, udpResults);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-servers probe failed", ex);
        }
    }

    /// <summary>
    /// Failover karar matrisini dashboard'a taşır: ping + UDP ölçümleri iki politika
    /// altında (varsayılan / katı) GpnServerSelectionService.EvaluateFailoverMatrix ile
    /// değerlendirilir ve setGpnFailoverMatrix olarak yayınlanır.
    /// </summary>
    private async Task PushGpnFailoverMatrixAsync(
        IReadOnlyList<GpnServerProfile> enabled,
        IReadOnlyList<GpnServerProbeResult> probeResults,
        List<(string ServerId, UdpProbeResult Result)> udpResults)
    {
        if (!WebViewReady || enabled.Count == 0)
        {
            return;
        }

        try
        {
            var udpMap = udpResults.ToDictionary(u => u.ServerId, u => u.Result);

            // Aktif: bağlı sunucu; bağlı değilse en düşük ping'li aday (matris "eğer
            // şu an bağlı olsaydık" senaryosunu gösterir).
            var active = ViewModel!.CurrentGpnServer
                ?? enabled.OrderBy(s => probeResults.FirstOrDefault(r => r.ServerId == s.ServerId)?.DelayMs ?? int.MaxValue)
                    .First();

            var matrix = ViewModel!.EvaluateFailoverMatrix(
                active, enabled, probeResults, udpMap,
                new GpnProbeOptions { Samples = 3, PerSampleTimeoutMs = 1000 });

            var json = JsonSerializer.Serialize(matrix, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnFailoverMatrix?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-failover-matrix push failed", ex);
        }
    }

    /// <summary>
    /// "En iyi aday" kartını dashboard'a taşır: aynı canlı ölçüm (ping + UDP) üzerinden
    /// GpnServerSelectionService.EvaluateSelection (saf DecideSelection) ile GPN
    /// Bağlan'ın yapacağı otomatik seçim kararı önceden gösterilir. Durum/mod adları
    /// string olarak serileştirilir (enum numarası değil) — renderer bunları karşılaştırır.
    /// </summary>
    private async Task PushGpnSelectionPredictionAsync(
        IReadOnlyList<GpnServerProfile> enabled,
        IReadOnlyList<GpnServerProbeResult> probeResults,
        List<(string ServerId, UdpProbeResult Result)> udpResults)
    {
        if (!WebViewReady || enabled.Count == 0)
        {
            return;
        }

        try
        {
            var udpMap = udpResults.ToDictionary(u => u.ServerId, u => u.Result);
            var prediction = ViewModel!.EvaluateSelection(
                enabled, probeResults, udpMap,
                new GpnProbeOptions { Samples = 3, PerSampleTimeoutMs = 1000 });

            var json = JsonSerializer.Serialize(new
            {
                prediction.Best?.ServerId,
                prediction.Best?.Name,
                PingMs = probeResults.FirstOrDefault(r => r.ServerId == prediction.Best?.ServerId)?.DelayMs ?? -1,
                LossPercent = probeResults.FirstOrDefault(r => r.ServerId == prediction.Best?.ServerId)?.LossPercent ?? 100,
                UdpStatus = prediction.UdpProbe?.Status.ToString() ?? "unknown",
                Mode = prediction.Mode.ToString(),
                Reason = prediction.Reason,
                Servers = enabled.Select(s => new
                {
                    s.ServerId,
                    s.Name,
                    DelayMs = probeResults.FirstOrDefault(r => r.ServerId == s.ServerId)?.DelayMs ?? -1,
                    LossPercent = probeResults.FirstOrDefault(r => r.ServerId == s.ServerId)?.LossPercent ?? 100,
                    UdpStatus = udpResults.FirstOrDefault(u => u.ServerId == s.ServerId).Result?.Status.ToString() ?? "unknown",
                    Selected = s.ServerId == prediction.Best?.ServerId,
                }).ToList(),
                MeasuredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnSelectionPrediction?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-selection-prediction push failed", ex);
        }
    }

    /// <summary>
    /// "Varsayılanları geri yükle" aksiyonu: gömülü anahtarsız şablon alanlarını
    /// (sunucu genel anahtarı, adres, MTU, DNS, keepalive) mevcut gpn_servers
    /// kayıtlarına yeniden uygular — DPAPI'li istemci anahtarı, ad ve etkinlik
    /// korunur. Sonrasında durum + sunucu listesi + kullanıcı bildirimi gönderilir.
    /// </summary>
    public async Task RestoreGpnDefaultsAsync()
    {
        var result = await WireGuardServerCatalog.RestoreDefaultsAsync();
        await PushGpnDefaultsStatusAsync(result);
        await PushGpnServersAsync();

        var message = result.RestoredCount > 0
            ? $"Varsayılanlar geri yüklendi ({result.RestoredCount} sunucu güncellendi)."
            : "Varsayılanlar zaten güncel — güncelleme gerekmedi.";
        await ExecuteScriptSafelyAsync($"window.gpnServersNotify?.({JsonSerializer.Serialize(message, RouteTestJsonOptions)});");
    }

    /// <summary>
    /// Gömülü varsayılan şablonların durumunu dashboard'a gönderir: her şablon için
    /// tohumlandı mı / anahtar var mı / güncel mi + farklı alan adları. Özel anahtar
    /// içeriği asla gönderilmez (yalnızca KeyPresent bayrağı).
    /// </summary>
    public async Task PushGpnDefaultsStatusAsync(WireGuardServerCatalog.GpnDefaultsRestoreResult? restoreResult = null)
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            IReadOnlyList<WireGuardServerCatalog.GpnDefaultsStatus> statuses = restoreResult is not null
                ? restoreResult.Statuses
                : await WireGuardServerCatalog.GetDefaultsStatusAsync();

            var json = JsonSerializer.Serialize(new
            {
                Servers = statuses.Select(s => new
                {
                    s.ServerId,
                    s.EndpointHost,
                    s.EndpointPort,
                    s.ServerPublicKey,
                    s.Seeded,
                    s.KeyPresent,
                    s.UpToDate,
                    s.Differences,
                }),
                RestoredCount = restoreResult?.RestoredCount,
                MeasuredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnDefaultsStatus?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-defaults-status push failed", ex);
        }
    }

    /// <summary>
    /// Kullanıcının Sunucu Yönetimi ekranına yapıştırdığı WireGuard .conf metnini
    /// parse edip gpn_servers'a ekler (private key DPAPI ile şifrelenerek saklanır).
    /// Her [Peer] bloğu ayrı bir sunucu satırı olur; aynı uç nokta yeniden içe
    /// aktarılırsa satır çoğalmaz (upsert).
    /// </summary>
    public async Task ImportGpnServersAsync(string confText)
    {
        var peers = WireguardFmt.ResolveConfig(confText);
        if (peers is null || peers.Count == 0)
        {
            await NotifyGpnServersOpAsync("GPN .conf çözümlenemedi — [Interface]/[Peer] bloğu bekleniyordu.");
            return;
        }

        var imported = 0;
        foreach (var peer in peers)
        {
            if (peer.ConfigType != EConfigType.WireGuard)
            {
                continue;
            }
            imported += await WireGuardServerCatalog.UpsertFromProfileAsync(peer);
        }

        await PushGpnServersAsync();
        await NotifyGpnServersOpAsync(imported > 0
            ? $"{imported} GPN sunucusu içe aktarıldı."
            : "GPN .conf içe aktarılamadı — uç nokta/anahtar eksik.");
    }

    /// <summary>Sunucu Yönetimi ekranından tek bir GPN sunucusunu siler.</summary>
    public async Task DeleteGpnServerAsync(string serverId)
    {
        var removed = await WireGuardServerCatalog.RemoveAsync(serverId);
        await PushGpnServersAsync();
        await NotifyGpnServersOpAsync(removed > 0
            ? "GPN sunucusu silindi."
            : "GPN sunucusu silinemedi.");
    }

    /// <summary>Sunucunun IsEnabled bayrağını değiştirir (otomatik seçimde adaylığı kapatır/açar).</summary>
    public async Task ToggleGpnServerAsync(string serverId, bool enabled)
    {
        var items = await WireGuardServerCatalog.GetItemsAsync();
        var item = (items ?? []).FirstOrDefault(i => i.ServerId == serverId);
        if (item is null)
        {
            await NotifyGpnServersOpAsync("GPN sunucusu bulunamadı.");
            return;
        }

        item.IsEnabled = enabled;
        item.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await WireGuardServerCatalog.UpsertAsync(item);
        await PushGpnServersAsync();
        await NotifyGpnServersOpAsync(enabled ? "GPN sunucusu etkinleştirildi." : "GPN sunucusu devre dışı bırakıldı.");
    }

    internal async Task NotifyGpnServersOpAsync(string message)
    {
        try
        {
            await ExecuteScriptSafelyAsync($"window.gpnServersNotify?.({JsonSerializer.Serialize(message)});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-servers notify failed", ex);
        }
    }

}
