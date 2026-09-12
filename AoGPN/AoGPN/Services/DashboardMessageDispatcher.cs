using System.Net;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using AoGPN.Views;
using Microsoft.Web.WebView2.Core;
using ServiceLib.Services;
using ServiceLib.ViewModels;

namespace AoGPN.Services;

/// <summary>
/// Owns the dashboard ↔ host message conversation for the WebView2 dashboard.
///
/// Ingress: <see cref="HandleWebMessageReceived"/> parses the renderer postMessage
/// JSON (validated by <see cref="DashboardMessageParser"/>), decodes the action
/// payload with the TryGet* helpers below and dispatches each supported action.
/// The heavy lifting for every action runs on the window, which implements
/// <see cref="IDashboardBridge"/>; this class deliberately holds no WPF state so
/// the code-behind stays a thin shell around UI/tray/window-lifecycle concerns.
///
/// The WebView2 raises WebMessageReceived on the UI thread, so no extra dispatcher
/// hop is needed on the ingress path (the same guarantee the old code-behind had).
/// </summary>
public sealed class DashboardMessageDispatcher
{
    private readonly IDashboardBridge _bridge;

    // Boot diagnostic: set when the first renderer message of the session is
    // logged, so a dead renderer→host channel is visible in the diag log.
    private bool _firstRendererMessageLogged;

    // ---- Single-level undo for app removals / route changes ----
    // The dashboard keeps ONE undo slot: every remove_app, set_app_route (route
    // change or quick-add by name) replaces it; other app mutations clear it so
    // the toast never offers a stale restore. Undo is consumed by
    // undo_last_app_op and re-pushes the authoritative snapshot.
    private enum AppUndoKind { Remove, Route, Add }

    private sealed record AppUndoRecord(
        AppUndoKind Kind,
        string ProcessName,
        string? DisplayName,
        string Action,
        string? WarpNodeIndexId,
        string? WarpNodeName);

    private AppUndoRecord? _lastAppUndo;

    private static readonly JsonSerializerOptions UndoJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // The bridge accepts only small, strict JSON objects from the WebView2 renderer.
    private static readonly JsonDocumentOptions WebMessageJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    public DashboardMessageDispatcher(IDashboardBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <summary>
    /// Parses only JSON string messages and dispatches the supported frontend actions.
    /// Unknown actions and malformed payloads are ignored by design.
    /// </summary>
    internal async void HandleWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        string rawMessage;
        try
        {
            // The HTML bridge deliberately calls postMessage with JSON.stringify(...).
            // TryGetWebMessageAsString rejects object messages rather than coercing them.
            rawMessage = e.TryGetWebMessageAsString();
        }
        catch (COMException)
        {
            return;
        }

        // Settings payloads (values + option lists) are far larger than the small
        // node/connection commands, so the cap is raised well above them. The strict
        // JSON options below still reject malformed or deeply nested renderer input.
        if (string.IsNullOrWhiteSpace(rawMessage) || rawMessage.Length > 128 * 1024)
        {
            return;
        }

        if (!DashboardMessageParser.TryParse(rawMessage, out var dashboardMessage)
            || dashboardMessage is null)
        {
            return;
        }

        try
        {
            using var rootDocument = JsonDocument.Parse(rawMessage, WebMessageJsonOptions);
            var root = rootDocument.RootElement;
            var action = dashboardMessage.Action;

            // Boot diagnostic: log the first renderer message of the session so a
            // dead renderer→host channel is visible in the diag log instead of
            // silently disabling every dashboard action.
            if (!_firstRendererMessageLogged)
            {
                _firstRendererMessageLogged = true;
                DiagLog.Write($"WEBVIEW_MSG first-message action={action}");
            }

            switch (action)
            {
                case "toggle_connection":
                    // Delegate to the existing split-tunnel ViewModel so the click
                    // updates routing rules, persisted settings, TUN requirements and
                    // the normal AoGPN core reload path instead of changing a UI flag.
                    var requestedMode = "gpn";
                    if (TryGetStringProperty(root, "mode", out var requestedModeValue)
                        && requestedModeValue is "vpn" or "gpn")
                    {
                        requestedMode = requestedModeValue;
                    }
                    var requestedTransport = "proxy";
                    if (TryGetStringProperty(root, "transport", out var requestedTransportValue)
                        && requestedTransportValue is "tun" or "proxy")
                    {
                        requestedTransport = requestedTransportValue;
                    }
                    // Kullanıcının EKRANDA GÖRDÜĞÜ durum (varsa) istekle birlikte taşınır:
                    // yön bu niyetten türetilir, canlı çekirdek durumundan değil. Aksi
                    // halde arka planda kurulan bir tünel arayüzde hâlâ "Bağlan"
                    // görünürken gelen tıklama "kes"e dönüşüp taze tüneli yıkıyordu.
                    bool? displayedConnected = TryGetBooleanProperty(root, "connected", out var displayedConnectedValue)
                        ? displayedConnectedValue
                        : null;
                    await ToggleConnectionAsync(requestedMode, requestedTransport, displayedConnected);
                    break;

                case "gpn_connect":
                    // Belirgin "GPN Bağlan" butonu: seçili profil ne olursa olsun
                    // (WireGuard dışı olsa bile) GPN modu açıkça seçildiğinde
                    // İtalya/Almanya otomatik seçimini zorla tetikle.
                    await RunGpnConnectAsync();
                    break;

                case "gpn_servers_list":
                    // Sunucu Yönetimi ekranı: gpn_servers tablosunun meta verisini
                    // (özel anahtar hariç) + gömülü varsayılan durumunu dashboard'a gönder.
                    await PushGpnServersAsync();
                    await PushGpnDefaultsStatusAsync();
                    break;

                case "gpn_defaults_status":
                    // Gömülü varsayılan şablonların katalog kayıtlarıyla durumu
                    // (tohumlandı mı / anahtar var mı / güncel mi) — saf okuma.
                    await PushGpnDefaultsStatusAsync();
                    break;

                case "gpn_defaults_restore":
                    // "Varsayılanları geri yükle": gömülü anahtarsız şablon alanlarını
                    // mevcut kayıtlara yeniden uygula (DPAPI anahtarı korunur), durumu
                    // ve listeyi tazele.
                    await RestoreGpnDefaultsAsync();
                    break;

                case "gpn_servers_probe":
                case "gpn_cluster_probe":
                    // Canlı ölçüm: gpn_servers'taki etkin sunuculara ICMP ping +
                    // UDP sağlık testi çalıştır ve rozetleri dashboard'a gönder.
                    // gpn_cluster_probe aynı ölçümü GPN panelindeki "sunucu kümesi"
                    // kartı için tetikler — iki akış aynı setGpnServerProbes verisini besler.
                    await ProbeGpnServersAsync();
                    break;

                case "gpn_pid_pool_start":
                    GetGpnPidBridge().Start();
                    await PushGpnPidPoolAsync();
                    break;

                case "gpn_pid_pool_stop":
                    GetGpnPidBridge().Stop();
                    await PushGpnPidPoolAsync();
                    break;

                case "gpn_pid_pool_refresh":
                    // Tek seferlik ölçüm (hedef adlarını da yeniden okur).
                    GetGpnPidBridge().RefreshNow();
                    await PushGpnPidPoolAsync();
                    break;

                case "gpn_server_add":
                    // .conf metnini kataloğa içe aktar (DPAPI ile şifrelenerek saklanır).
                    if (TryGetLongStringProperty(root, "confText", out var gpnConfText))
                    {
                        await ImportGpnServersAsync(gpnConfText);
                    }
                    break;

                case "gpn_server_delete":
                    if (TryGetStringProperty(root, "serverId", out var gpnDeleteId))
                    {
                        await DeleteGpnServerAsync(gpnDeleteId);
                    }
                    break;

                case "gpn_server_toggle":
                    if (TryGetStringProperty(root, "serverId", out var gpnToggleId)
                        && TryGetBooleanProperty(root, "enabled", out var gpnToggleEnabled))
                    {
                        await ToggleGpnServerAsync(gpnToggleId, gpnToggleEnabled);
                    }
                    break;

                case "gpn_server_add_dialog":
                    // WPF ekleme penceresi: DPAPI'li kataloğa manuel sunucu ekle.
                    await ShowGpnServerEditDialogAsync(existingServerId: null);
                    break;

                case "gpn_server_edit_dialog":
                    if (TryGetStringProperty(root, "serverId", out var gpnEditId))
                    {
                        await ShowGpnServerEditDialogAsync(gpnEditId);
                    }
                    break;

                case "select_node":
                    if (!TryGetStringProperty(root, "indexId", out var nodeIndexId))
                    {
                        return;
                    }

                    await SelectNodeAsync(nodeIndexId);
                    break;

                case "copy_nodes":
                    TryGetStringArrayProperty(root, "indexIds", out var copyIds);
                    await CopyNodesAsync(copyIds);
                    break;

                case "paste_nodes":
                    await PasteNodesAsync();
                    break;

                case "edit_node":
                    // Opens the native server-edit dialog for one node (right-click →
                    // Düzenle in the dashboard) and republishes the list on OK.
                    if (TryGetStringProperty(root, "indexId", out var editNodeIndexId))
                    {
                        await EditNodeAsync(editNodeIndexId);
                    }
                    break;

                case "import_wireguard_conf":
                    // WireGuard .conf files drag-and-dropped onto the dashboard;
                    // the renderer reads them and sends name + raw text.
                    if (TryGetWireGuardConfFiles(root, out var wgConfFiles))
                    {
                        await ImportWireGuardConfsAsync(wgConfFiles);
                    }
                    break;

                case "delete_nodes":
                    if (!TryGetStringArrayProperty(root, "indexIds", out var deleteIds))
                    {
                        return;
                    }

                    await DeleteNodesAsync(deleteIds);
                    break;

                case "move_node":
                    // Drag-to-reorder in the dashboard's Default order view: move
                    // indexId to the position of targetIndexId (native semantics).
                    if (TryGetStringProperty(root, "indexId", out var moveFromId)
                        && TryGetStringProperty(root, "targetIndexId", out var moveToId))
                    {
                        await MoveNodeAsync(moveFromId, moveToId);
                    }
                    break;

                case "test_nodes":
                    TryGetStringArrayProperty(root, "indexIds", out var testIds);
                    TryGetStringProperty(root, "testType", out var testType);
                    var requestedRunId = TryGetInt64Property(root, "runId", out var parsedRunId)
                        ? parsedRunId
                        : 0;
                    await StartNodeSpeedtestAsync(testIds, testType, requestedRunId);
                    break;

                case "stop_test":
                    if (TryGetInt64Property(root, "runId", out var stopRunId)
                        && stopRunId != _bridge.NodeTestRunId)
                    {
                        return;
                    }
                    StopNodeSpeedtest();
                    break;

                case "disable_nodes":
                    if (!TryGetStringArrayProperty(root, "indexIds", out var disableIds))
                    {
                        return;
                    }

                    await DisableNodesAsync(disableIds);
                    break;

                case "restore_nodes":
                    if (!TryGetStringArrayProperty(root, "indexIds", out var restoreIds))
                    {
                        return;
                    }

                    await RestoreNodesAsync(restoreIds);
                    break;

                case "cleanup_failed":
                    TryGetStringProperty(root, "target", out var cleanupTarget);
                    await CleanupFailedNodesAsync(cleanupTarget == "delete" ? "delete" : "disable");
                    break;

                case "dedup_nodes":
                    await DedupNodesAsync();
                    break;

                case "get_node_pool":
                    await PushNodePoolAsync();
                    break;

                case "add_node_pool_link":
                    if (!TryGetStringProperty(root, "url", out var poolAddUrl) || poolAddUrl.Length < 8)
                    {
                        return;
                    }

                    await AddNodePoolLinkAsync(poolAddUrl);
                    break;

                case "edit_node_pool_link":
                    if (!TryGetStringProperty(root, "url", out var poolEditUrl)
                        || !TryGetStringProperty(root, "newUrl", out var poolNewUrl))
                    {
                        return;
                    }

                    await EditNodePoolLinkAsync(poolEditUrl, poolNewUrl);
                    break;

                case "remove_node_pool_link":
                    if (!TryGetStringProperty(root, "url", out var poolRemoveUrl))
                    {
                        return;
                    }

                    await RemoveNodePoolLinkAsync(poolRemoveUrl);
                    break;

                case "fetch_node_pool":
                    await FetchNodePoolAsync();
                    break;

                case "toggle_node_fav":
                    if (!TryGetStringProperty(root, "indexId", out var favIndexId))
                    {
                        return;
                    }

                    await ToggleNodeFavAsync(favIndexId);
                    break;

                case "set_connection_mode":
                    if (!TryGetStringProperty(root, "mode", out var connectionMode)
                        || connectionMode is not "vpn" and not "gpn")
                    {
                        return;
                    }

                    await SetConnectionModeAsync(connectionMode);
                    break;

                case "set_transport":
                    if (!TryGetStringProperty(root, "transport", out var transportValue)
                        || transportValue is not "tun" and not "proxy")
                    {
                        return;
                    }

                    await SetTransportAsync(transportValue);
                    break;

                case "set_protocol_preference":
                    if (!TryGetStringProperty(root, "protocol", out var protocolPreferenceValue))
                    {
                        return;
                    }

                    await SetProtocolPreferenceAsync(protocolPreferenceValue);
                    break;

                case "set_tun_stack":
                    if (!TryGetStringProperty(root, "stack", out var tunStackValue)
                        || !Global.TunStacks.Contains(tunStackValue))
                    {
                        return;
                    }

                    await SetTunStackAsync(tunStackValue);
                    break;

                case "set_auto_reconnect":
                    await SetAutoReconnectAsync(GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.ConnectionItem?.AutoReconnectEnabled ?? true));
                    break;

                case "set_gpn_recovery_watch":
                    await SetGpnRecoveryWatchAsync(GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.GuiItem?.GpnEnableRecoveryWatch ?? true));
                    break;

                case "set_gpn_failover":
                    await SetGpnFailoverAsync(GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.GuiItem?.GpnEnableFailover ?? false));
                    break;

                case "get_vless_bypass_node":
                    // Çift Bağlantı (Bölünmüş Tünelleme) küresel launcher-bypass düğümünü
                    // dashboard'a gönder (window.setVlessBypassNode).
                    await PushVlessBypassNodeAsync();
                    break;

                case "set_vless_bypass_node":
                    // Dashboard'dan gelen VLESS/Reality launcher-bypass düğümünü doğrula ve
                    // GuiItem.VlessBypassNodeJson'a kaydet — sonraki GPN bağlantısında
                    // (GpnCoreLauncher) mihomo YAML'ine ikincil "vless-launcher" olarak eklenir.
                    await SetVlessBypassNodeAsync(root);
                    break;

                case "set_vless_bypass_from_uri":
                    // Aynı düğüm, ama ham vless:// Reality paylaşım bağlantısı olarak:
                    // FmtHandler.ResolveConfig (kanonik URI ayrıştırıcı) ile çözülür,
                    // VlessProfileItem'a eşlenir ve GuiItem.VlessBypassNodeJson'a kaydedilir.
                    await SetVlessBypassFromUriAsync(root);
                    break;

                case "get_gpn_capture_settings":
                    // WinDivert kuyruk kartı: mevcut GpnCaptureItem ayarlarını dashboard'a gönder.
                    await PushGpnCaptureSettingsAsync();
                    break;

                case "set_gpn_capture_settings":
                    // WinDivert kuyruk kartından gelen ayarları doğrula (saf GpnCaptureSettingsPatch
                    // ile sınırla) ve config'e yaz — sonraki yakalama başlangıcında uygulanır.
                    await SetGpnCaptureSettingsAsync(root);
                    break;

                case "get_gpn_wintun_settings":
                    // Wintun adapter kartı: mevcut GpnWintunItem ayarlarını dashboard'a gönder.
                    await PushGpnWintunSettingsAsync();
                    break;

                case "set_gpn_wintun_settings":
                    // Wintun adapter kartından gelen ayarları doğrula (saf GpnWintunSettingsPatch
                    // ile sanitleştir/sınırla) ve config'e yaz — sonraki bağlantıda uygulanır.
                    await SetGpnWintunSettingsAsync(root);
                    break;

                case "reset_gpn_telemetry":
                    _bridge.ResetGpnTelemetry();
                    await PushGpnTelemetryAsync();
                    break;

                case "get_gpn_telemetry":
                    await PushGpnTelemetryAsync();
                    await PushGpnCaptureStatsAsync();
                    break;

                case "get_gpn_resilience_log":
                    await PushGpnResilienceLogAsync();
                    break;

                case "clear_gpn_resilience_log":
                    _bridge.ClearGpnResilienceLog();
                    await PushGpnResilienceLogAsync();
                    break;

                case "set_active_view":
                    if (TryGetStringProperty(root, "view", out var activeView)
                        && activeView is "dashboard" or "nodes" or "perf" or "boost" or "settings" or "coming")
                    {
                        _bridge.ActiveView = activeView;
                    }
                    break;

                case "request_monitor_snapshot":
                    await PushMonitorSnapshotAsync(force: true);
                    break;

                case "list_running_processes":
                    await PushProcessCatalogAsync();
                    break;

                case "get_app_icons":
                    // Renderer asks for the shell icons of the executables its tables
                    // display (Game Boost / Connection Monitor / running-apps picker).
                    // Paths are capped to bound extraction work; the host answers
                    // with window.setAppIcons({ icons: { path: dataUri } }).
                    if (TryGetPathArrayProperty(root, "paths", out var iconPaths))
                    {
                        await PushAppIconsAsync(iconPaths);
                    }
                    break;

                case "refresh_monitor":
                    if (ViewModel?.ConnectionViewModel is { } monitorViewModel)
                    {
                        await monitorViewModel.Monitor.RefreshAsync();
                        await PushMonitorSnapshotAsync(force: true);
                    }
                    break;

                case "set_app_route":
                    if (!TryGetStringProperty(root, "processName", out var routeProcess)
                        || !TryGetStringProperty(root, "route", out var routeAction)
                        || routeAction is not ("vpn" or "proxy" or "vpn+proxy" or "direct" or "block" or "warp"))
                    {
                        return;
                    }

                    TryGetStringProperty(root, "displayName", out var routeDisplayName);
                    // Per-app WARP egress düğümü (Ayarlar → GPN bypass'ın yerine):
                    // "warp" rotasında isteğe bağlı warpNodeIndexId iletilir — satır
                    // o düğümün egress'inden çıkar. Yalnızca warp rotasında saklanır.
                    TryGetStringProperty(root, "warpNodeIndexId", out var routeWarpNode);
                    TryGetStringProperty(root, "warpNodeName", out var routeWarpNodeName);
                    if (ViewModel?.ConnectionViewModel is { } routeViewModel)
                    {
                        // Pre-op state snapshot for the undo slot: an existing entry
                        // records its previous route (incl. WARP egress node); a new
                        // entry (quick-add by name) records that it was added.
                        var existingRoute = routeViewModel.Apps.FirstOrDefault(a => a.EntryType == "app"
                            && a.Value.Equals(routeProcess, StringComparison.OrdinalIgnoreCase));
                        var applied = await routeViewModel.SetDashboardAppRouteAsync(
                            routeProcess, routeDisplayName, routeAction, routeWarpNode, routeWarpNodeName);
                        if (applied)
                        {
                            var effectiveWarp = routeAction == "warp" ? (routeWarpNode ?? string.Empty).Trim() : string.Empty;
                            var changed = existingRoute is null
                                || existingRoute.Action != routeAction
                                || !string.Equals(existingRoute.WarpNodeIndexId ?? string.Empty, effectiveWarp, StringComparison.OrdinalIgnoreCase);
                            if (changed)
                            {
                                _lastAppUndo = existingRoute is null
                                    ? new AppUndoRecord(AppUndoKind.Add, routeProcess, routeDisplayName, routeAction, routeWarpNode, routeWarpNodeName)
                                    : new AppUndoRecord(AppUndoKind.Route, existingRoute.Value, existingRoute.DisplayName,
                                        existingRoute.Action, existingRoute.WarpNodeIndexId, null);
                                await PushUndoStateAsync();
                            }
                        }
                        await PushMonitorSnapshotAsync(force: true);
                        await NotifyNodesOpAsync(applied ? "Application route saved" : "Application route was rejected");
                    }
                    break;

                case "move_route":
                    // Game Boost row reordering: rule order = list order (first match
                    // wins), so a domain rule must be movable above the process rule it
                    // takes precedence over. Identifies the entry by type + value so
                    // domain/IP rows are addressable exactly like app rows.
                    if (TryGetStringProperty(root, "entryType", out var moveEntryType)
                        && TryGetStringProperty(root, "value", out var moveEntryValue)
                        && TryGetStringProperty(root, "direction", out var moveDirection)
                        && moveEntryValue.IsNotEmpty()
                        && moveDirection is "up" or "down"
                        && ViewModel?.ConnectionViewModel is { } moveViewModel)
                    {
                        var moved = moveViewModel.MoveManualRoute(moveEntryType, moveEntryValue, moveDirection == "up");
                        await PushMonitorSnapshotAsync(force: true);
                        await NotifyNodesOpAsync(moved
                            ? "Route order updated"
                            : "Route order could not be changed");
                    }
                    break;

                case "add_domain_route":
                    // Manual domain/IP rule from Game Boost (e.g. the one-click
                    // "BSG API → WARP" entry). Duplicate/invalid values are refused
                    // by the view model; the row order stays fully manual.
                    if (TryGetStringProperty(root, "value", out var domainValue)
                        && domainValue.IsNotEmpty()
                        && ViewModel?.ConnectionViewModel is { } domainViewModel)
                    {
                        TryGetStringProperty(root, "route", out var domainAction);
                        TryGetStringProperty(root, "displayName", out var domainDisplayName);
                        var added = domainViewModel.AddDomainRoute(
                            domainValue, domainAction.IsNotEmpty() ? domainAction : "proxy", domainDisplayName);
                        await PushMonitorSnapshotAsync(force: true);
                        await NotifyNodesOpAsync(added
                            ? "Domain route added"
                            : "Domain route could not be added");
                    }
                    break;

                case "add_app":
                    if (ViewModel?.ConnectionViewModel is { } addAppViewModel
                        && UI.OpenFileDialog(out var appPath, "Applications|*.exe|All files|*.*") == true)
                    {
                        // New app mutations replace (not stack) the undo slot.
                        await ClearAppUndoAsync();
                        await addAppViewModel.AddDashboardAppAsync(appPath);
                        await PushMonitorSnapshotAsync(force: true);
                    }
                    break;

                case "add_running_process":
                    if (root.TryGetProperty("pid", out var pidElement)
                        && pidElement.ValueKind == JsonValueKind.Number
                        && pidElement.TryGetInt32(out var pid)
                        && ViewModel?.ConnectionViewModel is { } runningAppViewModel)
                    {
                        if (_bridge.TryResolveExecutablePath(pid, out var runningPath)
                            && !IsProtectedProcessPath(runningPath))
                        {
                            // New app mutations replace (not stack) the undo slot.
                            await ClearAppUndoAsync();
                            await runningAppViewModel.AddDashboardAppAsync(runningPath);
                            await PushMonitorSnapshotAsync(force: true);
                            await NotifyNodesOpAsync("Running application added");
                            return;
                        }

                        // Anti-cheat guarded processes (e.g. BattlEye games) deny
                        // executable-path queries entirely. Routing matches by
                        // process name only, so add by name from the picker payload
                        // when native resolution is blocked.
                        TryGetStringProperty(root, "processName", out var pickerProcessName);
                        TryGetStringProperty(root, "displayName", out var pickerDisplayName);
                        pickerProcessName = ProcessCatalogService.NormalizeProcessName(pickerProcessName);
                        if (pickerProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            && !IsProtectedProcessPath(pickerProcessName))
                        {
                            var suggested = KnownAppCatalog.SuggestAction(pickerProcessName, pickerDisplayName ?? string.Empty);
                            var addedByName = await runningAppViewModel.SetDashboardAppRouteAsync(
                                pickerProcessName, pickerDisplayName, suggested);
                            await PushMonitorSnapshotAsync(force: true);
                            await NotifyNodesOpAsync(addedByName
                                ? "Running application added"
                                : "Running application was rejected");
                            return;
                        }

                        await NotifyNodesOpAsync("Running application could not be resolved");
                    }
                    break;

                case "remove_app":
                    if (TryGetStringProperty(root, "processName", out var removeProcessName)
                        && ViewModel?.ConnectionViewModel is { } removeAppViewModel)
                    {
                        var target = removeAppViewModel.Apps
                            .FirstOrDefault(a => a.EntryType == "app" &&
                                a.Value.Equals(removeProcessName, StringComparison.OrdinalIgnoreCase));
                        // Eski render'larda domain/IP satırları da remove_app gönderiyordu
                        // (silme butonu sessizce çalışmıyordu). App eşleşmesi yoksa rule
                        // girişlerinde de ara — hiçbir satırın silme butonu no-op kalmasın.
                        if (target is null)
                        {
                            TryGetStringProperty(root, "entryType", out var removeEntryType);
                            target = removeEntryType is "domain" or "ip"
                                ? removeAppViewModel.Apps.FirstOrDefault(a =>
                                    a.EntryType == removeEntryType &&
                                    a.Value.Equals(removeProcessName, StringComparison.OrdinalIgnoreCase))
                                : removeAppViewModel.Apps.FirstOrDefault(a => a.EntryType != "app" &&
                                    a.Value.Equals(removeProcessName, StringComparison.OrdinalIgnoreCase));
                        }
                        if (target is not null)
                        {
                            // Record the removed entry (value + route + WARP egress)
                            // so undo can re-add it exactly as it was.
                            _lastAppUndo = new AppUndoRecord(AppUndoKind.Remove, target.Value, target.DisplayName,
                                target.Action, target.WarpNodeIndexId, null);
                            await PushUndoStateAsync();
                            removeAppViewModel.SelectedApp = target;
                            removeAppViewModel.RemoveAppCmd.Execute().Subscribe();
                            await PushMonitorSnapshotAsync(force: true);
                        }
                    }
                    break;

                case "remove_route":
                    // Rule (domain/IP) removal from Game Boost: identifies the entry
                    // by type + value, exactly like move_route. Apps keep the richer
                    // remove_app path (undo slot); rules are removed directly and
                    // cleanly through the same collection-change pipeline.
                    if (TryGetStringProperty(root, "entryType", out var removeRouteType)
                        && TryGetStringProperty(root, "value", out var removeRouteValue)
                        && removeRouteValue.IsNotEmpty()
                        && ViewModel?.ConnectionViewModel is { } removeRouteViewModel)
                    {
                        var removed = removeRouteViewModel.RemoveManualRoute(removeRouteType, removeRouteValue);
                        await PushMonitorSnapshotAsync(force: true);
                        await NotifyNodesOpAsync(removed
                            ? "Route removed"
                            : "Route could not be removed");
                    }
                    break;

                case "undo_last_app_op":
                    await UndoLastAppOpAsync();
                    break;

                case "add_files":
                    if (ViewModel?.ConnectionViewModel is { } dropViewModel
                        && root.TryGetProperty("files", out var filesEl)
                        && filesEl.ValueKind == JsonValueKind.Array)
                    {
                        var fileNames = filesEl.EnumerateArray()
                            .Where(f => f.ValueKind == JsonValueKind.String)
                            .Select(f => f.GetString()!)
                            .Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        if (fileNames.Length > 0)
                        {
                            // New app mutations replace (not stack) the undo slot.
                            await ClearAppUndoAsync();
                            var resolved = await ResolveDropFilePathsAsync(fileNames);
                            if (resolved.Length > 0)
                            {
                                await dropViewModel.AddDroppedFilesAsync(resolved);
                                await PushMonitorSnapshotAsync(force: true);
                            }
                        }
                    }
                    break;

                case "set_auto_game_connect":
                    if (ViewModel?.ConnectionViewModel is { } autoGameViewModel)
                    {
                        autoGameViewModel.AutoConnectOnGameStart = GetSettingsBool(
                            root,
                            "enabled",
                            autoGameViewModel.AutoConnectOnGameStart);
                        await PushMonitorSnapshotAsync(force: true);
                    }
                    break;

                case "set_split_mode":
                    if (TryGetStringProperty(root, "mode", out var splitMode))
                    {
                        await SetDashboardModeAsync(splitMode);
                    }
                    break;

                case "set_split_direction":
                    if (TryGetStringProperty(root, "invert", out var invertManualValue)
                        && ViewModel?.ConnectionViewModel is { } directionVm)
                    {
                        var invert = invertManualValue.Equals("true", StringComparison.OrdinalIgnoreCase);
                        if (directionVm.InvertManualRouting != invert)
                        {
                            directionVm.InvertManualRouting = invert;
                            await directionVm.ApplyCmd.Execute().ToTask();
                            await PushMonitorSnapshotAsync(force: true);
                        }
                    }
                    break;

                case "test_route":
                    if (!TryGetStringProperty(root, "exeName", out var testExe)
                        || !TryGetStringProperty(root, "destination", out var testDestination))
                    {
                        return;
                    }

                    TryGetStringProperty(root, "port", out var testPort);
                    TryGetStringProperty(root, "network", out var testNetwork);
                    TryGetStringProperty(root, "exePath", out var testExePath);
                    await RunRouteTestAsync(testExe, testDestination, testPort, testNetwork, testExePath);
                    break;

                case "set_verbose_log":
                    var verboseEnabled = GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.GuiItem.EnableVerboseLog);
                    AppManager.Instance.Config.GuiItem.EnableVerboseLog = verboseEnabled;
                    Logging.VerboseLoggingEnabled(verboseEnabled);
                    ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                    Logging.VerboseIf(verboseEnabled, "GPN", "verbose_log", verboseEnabled ? "enabled" : "disabled");
                    break;

                case "set_effects_tier":
                    // Visual-effects tier from the dashboard segmented control:
                    // "full" (everything), "balanced" (ambient loops frozen,
                    // reactive effects stay) or "reduced" (all off). Applies
                    // immediately on the renderer side, no restart needed.
                    var effectsTier = TryGetStringProperty(root, "tier", out var tierValue)
                        ? NormalizeEffectsMode(tierValue)
                        : "full";
                    AppManager.Instance.Config.GuiItem.EffectsMode = effectsTier;
                    AppManager.Instance.Config.GuiItem.ReduceEffects = effectsTier == "reduced";
                    ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                    await PushEffectsTierAsync();
                    break;

                case "set_reduce_effects":
                    // Legacy single switch from older dashboard builds: map onto
                    // the effects tier (reduced kills everything, full restores).
                    var reduceEffectsEnabled = GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.GuiItem.ReduceEffects);
                    AppManager.Instance.Config.GuiItem.EffectsMode = reduceEffectsEnabled ? "reduced" : "full";
                    AppManager.Instance.Config.GuiItem.ReduceEffects = reduceEffectsEnabled;
                    ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                    await PushEffectsTierAsync();
                    break;

                case "set_language":
                    if (TryGetStringProperty(root, "lang", out var newLang)
                        && Global.Languages.Contains(newLang)
                        && AppManager.Instance.Config.UiItem.CurrentLanguage != newLang)
                    {
                        AppManager.Instance.Config.UiItem.CurrentLanguage = newLang;
                        Thread.CurrentThread.CurrentUICulture = new(newLang);
                        ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                        await PushLanguageAsync();
                    }
                    break;

                case "set_theme":
                    // Persist the raw dashboard theme id (all 25, not just the 11
                    // with a WPF palette) so the startup push restores whatever the
                    // user picked from the Appearance deck — including aurora/candy/
                    // obsidian/sandstorm and the nexus palettes, which have no native
                    // ETheme equivalent. Mapped themes additionally update the native
                    // side through the sidebar ViewModel.
                    if (!TryGetStringProperty(root, "theme", out var webTheme)
                        || !MainWindow.IsKnownDashboardTheme(webTheme))
                    {
                        break;
                    }

                    AppManager.Instance.Config.UiItem.DashboardTheme = webTheme;
                    if (TryMapWebThemeToWpf(webTheme, out var wpfTheme)
                        && AppManager.Instance.Config.UiItem.CurrentTheme != wpfTheme)
                    {
                        // Reuse the native ViewModel so Material Design resources,
                        // the title-bar border and the WebView event channel all update
                        // through the same path as the native theme selector. When the
                        // sidebar ViewModel is not alive yet, persist the raw theme so
                        // the startup palette is correct.
                        if (!_bridge.TryApplySidebarTheme(wpfTheme))
                        {
                            AppManager.Instance.Config.UiItem.CurrentTheme = wpfTheme;
                        }
                    }
                    ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                    break;

                case "set_system_proxy_mode":
                    var requestedProxyMode = (ESysProxyType)Math.Clamp(
                        GetSettingsInt(root, "mode", (int)(AppManager.Instance.Config.SystemProxyItem?.SysProxyType ?? ESysProxyType.ForcedClear)),
                        0,
                        3);
                    await SetSystemProxyModeAsync(requestedProxyMode);
                    break;

                case "toggle_system_proxy":
                    await ToggleSystemProxyAsync();
                    break;

                case "test_proxy":
                    await TestProxyAsync();
                    break;

                case "performance_sample":
                    if (root.TryGetProperty("payload", out var performancePayload)
                        && performancePayload.ValueKind == JsonValueKind.Object)
                    {
                        var perfJson = performancePayload.GetRawText();
                        DiagLog.Write($"WEBVIEW_PERF {perfJson}");
                    }
                    break;

                case "check_ip":
                    await CheckIpAsync();
                    break;

                case "app_control":
                    if (!TryGetStringProperty(root, "command", out var command))
                    {
                        return;
                    }

                    HandleAppControl(command);
                    break;

                case "set_window_behavior":
                    // Dashboard window-behaviour toggles (minimize-to-tray /
                    // hide-to-tray-on-close) apply immediately on change, so the
                    // shown switch always matches what X / minimize actually do —
                    // the close and minimize paths read this same live config
                    // object, and the queued save persists the new value to disk.
                    var trayConfig = AppManager.Instance.Config;
                    var requestedHideOnClose = GetSettingsBool(root, "hide2TrayWhenClose", trayConfig.UiItem.Hide2TrayWhenClose);
                    var requestedMinimize2Tray = GetSettingsBool(root, "minimize2Tray", trayConfig.UiItem.Minimize2Tray);
                    if (requestedHideOnClose != trayConfig.UiItem.Hide2TrayWhenClose
                        || requestedMinimize2Tray != trayConfig.UiItem.Minimize2Tray)
                    {
                        trayConfig.UiItem.Hide2TrayWhenClose = requestedHideOnClose;
                        trayConfig.UiItem.Minimize2Tray = requestedMinimize2Tray;
                        ConfigSaveQueue.RequestSave(trayConfig);
                        Logging.VerboseIf(
                            requestedHideOnClose || requestedMinimize2Tray,
                            "UI", "window_behavior",
                            $"hideOnClose={requestedHideOnClose} minimize2Tray={requestedMinimize2Tray}");
                        // Reflect the persisted truth back so the form cannot drift
                        // from the real window behaviour.
                        await PushSettingsAsync();
                    }
                    break;

                case "get_settings":
                    await PushSettingsAsync();
                    break;

                case "save_settings":
                    await SaveSettingsAsync(root);
                    break;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON is untrusted renderer input; do not let it reach application
            // logic and do not turn repeated malformed messages into log noise.
        }
        catch (Exception ex)
        {
            // Keep renderer failures contained even if WebView2 is closing concurrently.
            if (!_bridge.IsClosing)
            {
                Logging.SaveLog("AoGPN WebView2 message handling failed", ex);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Message payload decoders (moved from MainWindow; pure & static).
    // ---------------------------------------------------------------------

    internal static bool TryGetStringProperty(
        JsonElement objectElement,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!objectElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 64)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    internal static bool TryGetInt64Property(JsonElement objectElement, string propertyName, out long value)
    {
        value = 0;
        return objectElement.TryGetProperty(propertyName, out var property)
            && property.TryGetInt64(out value)
            && value > 0;
    }

    /// <summary>
    /// Reads the renderer's drag-and-drop payload: an array of { name, content }
    /// objects for dropped WireGuard .conf files. Entries without content are
    /// skipped; false is returned only when nothing usable remains.
    /// </summary>
    internal static bool TryGetWireGuardConfFiles(JsonElement root, out List<WireGuardConfFile> files)
    {
        files = [];
        if (!root.TryGetProperty("files", out var filesEl)
            || filesEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var fileEl in filesEl.EnumerateArray())
        {
            if (fileEl.ValueKind != JsonValueKind.Object
                || !fileEl.TryGetProperty("content", out var contentEl)
                || string.IsNullOrEmpty(contentEl.GetString()))
            {
                continue;
            }
            var name = fileEl.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;
            files.Add(new WireGuardConfFile(name, contentEl.GetString()!));
        }
        return files.Count > 0;
    }

    internal static bool TryGetBooleanProperty(JsonElement objectElement, string propertyName, out bool value)
    {
        value = false;
        if (!objectElement.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// .conf metni gibi uzun payload'lar için: 64 karakterlik kısa-string limiti
    /// conf bloğunu keserdi, bu yüzden ayrı bir okuma yolu (128 KB renderer limiti
    /// içinde). Yalnızca güvenilen eylemler (gpn_server_add) kullanır.
    /// </summary>
    internal static bool TryGetLongStringProperty(
        JsonElement objectElement,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!objectElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 128 * 1024)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    /// <summary>
    /// Reads the renderer's app-icon request: an array of executable paths.
    /// Paths may exceed the 64-char short-string limit, so this decoder is
    /// separate from TryGetStringArrayProperty; the count is capped at 48 per
    /// request so a pathological renderer cannot trigger unbounded icon work.
    /// </summary>
    internal static bool TryGetPathArrayProperty(
        JsonElement objectElement,
        string propertyName,
        out string[] paths)
    {
        paths = [];
        if (!objectElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = item.GetString();
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 320)
            {
                result.Add(candidate.Trim());
            }

            if (result.Count >= 48)
            {
                break;
            }
        }

        paths = result.ToArray();
        return paths.Length > 0;
    }

    internal static bool TryGetStringArrayProperty(
        JsonElement objectElement,
        string propertyName,
        out string[] values)
    {
        values = [];
        if (!objectElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = item.GetString();
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 64)
            {
                result.Add(candidate);
            }
        }

        values = result.ToArray();
        return values.Length > 0;
    }

    /// <summary>
    /// Extracts a two-letter country code from common v2ray node remark patterns.
    /// Matches known codes surrounded by separators like [TR], TR-, (TR), TR·.
    /// Returns empty string when no country hint is found.
    /// </summary>
    internal static string ExtractCountryFromRemarks(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks))
            return string.Empty;

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TR", "US", "DE", "FR", "GB", "NL", "JP", "KR", "SG", "HK",
            "CA", "AU", "RU", "IR", "AE", "BR", "IN", "CN", "TW", "IT",
            "ES", "SE", "CH", "PL", "CZ", "UA", "KZ", "VN", "TH", "ID",
            "MY", "PH", "AR", "CL", "CO", "MX", "ZA", "NG", "EG",
        };

        var remarkUpper = remarks.ToUpperInvariant();
        foreach (var code in codes)
        {
            var idx = remarkUpper.IndexOf(code, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var before = idx > 0 ? remarkUpper[idx - 1] : '.';
                var after = idx + 2 < remarkUpper.Length ? remarkUpper[idx + 2] : '.';
                if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after))
                    return code;
                idx = remarkUpper.IndexOf(code, idx + 1, StringComparison.Ordinal);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Best-effort country code for a node: a two-letter code in the remarks wins;
    /// otherwise the node address is resolved through GeoIP when it is a plain IP.
    /// </summary>
    internal static string ResolveNodeCountry(string? remarks, string? address)
    {
        var fromRemarks = ExtractCountryFromRemarks(remarks);
        if (fromRemarks.IsNotEmpty())
        {
            return fromRemarks;
        }

        var normalizedAddress = NormalizeNodeAddress(address);
        if (normalizedAddress.IsNotEmpty()
            && IPAddress.TryParse(normalizedAddress, out var ip)
            && !IsNonPublicAddress(ip))
        {
            var geo = GeoIpLookupService.Lookup(ip);
            return geo.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        }

        return string.Empty;
    }

    internal static string NormalizeNodeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var value = address.Trim();
        if (value.StartsWith('[') && value.IndexOf(']') is var end && end > 1)
            return value[1..end];
        if (value.Count(c => c == ':') == 1 && value.LastIndexOf(':') is var colon && IPAddress.TryParse(value[..colon], out _))
            return value[..colon];
        return value.TrimEnd('.');
    }

    internal static bool IsNonPublicAddress(IPAddress ip)
    {
        return GeoIpLookupService.Lookup(ip).IsPrivate;
    }

    // ---------------------------------------------------------------------
    // Window-side static helpers (still defined on MainWindow; these thin
    // forwarders keep the moved switch text unchanged).
    // ---------------------------------------------------------------------

    private static bool GetSettingsBool(JsonElement settings, string name, bool fallback)
        => DashboardSettingsService.GetSettingsBool(settings, name, fallback);

    private static int GetSettingsInt(JsonElement settings, string name, int fallback)
        => DashboardSettingsService.GetSettingsInt(settings, name, fallback);

    private static bool TryMapWebThemeToWpf(string webTheme, out string wpfTheme)
        => MainWindow.TryMapWebThemeToWpf(webTheme, out wpfTheme);

    private static string NormalizeEffectsMode(string? mode)
        => DashboardSettingsService.NormalizeEffectsMode(mode);

    private static bool IsProtectedProcessPath(string path)
        => MainWindow.IsProtectedProcessPath(path);

    private static Task<string[]> ResolveDropFilePathsAsync(string[] exeNames)
        => MainWindow.ResolveDropFilePathsAsync(exeNames);

    // ---------------------------------------------------------------------
    // Bridge forwarders: the moved switch text calls these names exactly as
    // it did on the code-behind; each forwards to the window-owned operation.
    // ---------------------------------------------------------------------

    private MainWindowViewModel? ViewModel => _bridge.ViewModel;

    private GpnTargetResolverBridge GetGpnPidBridge() => _bridge.GpnPidBridge;

    private void StopNodeSpeedtest() => _bridge.StopNodeSpeedtest();

    private void HandleAppControl(string command) => _bridge.HandleAppControl(command);

    private Task ToggleConnectionAsync(string requestedMode, string transport, bool? displayedConnected)
        => _bridge.ToggleConnectionAsync(requestedMode, transport, displayedConnected);

    private Task RunGpnConnectAsync()
        => _bridge.RunGpnConnectAsync();

    private Task ProbeGpnServersAsync()
        => _bridge.ProbeGpnServersAsync();

    private Task ImportGpnServersAsync(string confText)
        => _bridge.ImportGpnServersAsync(confText);

    private Task DeleteGpnServerAsync(string serverId)
        => _bridge.DeleteGpnServerAsync(serverId);

    private Task ToggleGpnServerAsync(string serverId, bool enabled)
        => _bridge.ToggleGpnServerAsync(serverId, enabled);

    private Task ShowGpnServerEditDialogAsync(string? existingServerId)
        => _bridge.ShowGpnServerEditDialogAsync(existingServerId);

    private Task SelectNodeAsync(string indexId)
        => _bridge.SelectNodeAsync(indexId);

    private Task CopyNodesAsync(string[] indexIds)
        => _bridge.CopyNodesAsync(indexIds);

    private Task PasteNodesAsync()
        => _bridge.PasteNodesAsync();

    private Task EditNodeAsync(string indexId)
        => _bridge.EditNodeAsync(indexId);

    private Task ImportWireGuardConfsAsync(List<WireGuardConfFile> files)
        => _bridge.ImportWireGuardConfsAsync(files);

    private Task DeleteNodesAsync(string[] indexIds)
        => _bridge.DeleteNodesAsync(indexIds);

    private Task MoveNodeAsync(string indexId, string targetIndexId)
        => _bridge.MoveNodeAsync(indexId, targetIndexId);

    private Task StartNodeSpeedtestAsync(string[] indexIds, string? testType = null, long requestedRunId = 0)
        => _bridge.StartNodeSpeedtestAsync(indexIds, testType, requestedRunId);

    private Task DisableNodesAsync(string[] indexIds)
        => _bridge.DisableNodesAsync(indexIds);

    private Task RestoreNodesAsync(string[] indexIds)
        => _bridge.RestoreNodesAsync(indexIds);

    private Task CleanupFailedNodesAsync(string target)
        => _bridge.CleanupFailedNodesAsync(target);

    private Task DedupNodesAsync()
        => _bridge.DedupNodesAsync();

    private Task AddNodePoolLinkAsync(string url)
        => _bridge.AddNodePoolLinkAsync(url);

    private Task EditNodePoolLinkAsync(string url, string newUrl)
        => _bridge.EditNodePoolLinkAsync(url, newUrl);

    private Task RemoveNodePoolLinkAsync(string url)
        => _bridge.RemoveNodePoolLinkAsync(url);

    private Task FetchNodePoolAsync()
        => _bridge.FetchNodePoolAsync();

    private Task ToggleNodeFavAsync(string indexId)
        => _bridge.ToggleNodeFavAsync(indexId);

    private Task SetConnectionModeAsync(string mode)
        => _bridge.SetConnectionModeAsync(mode);

    private Task SetTransportAsync(string transport)
        => _bridge.SetTransportAsync(transport);

    private Task SetProtocolPreferenceAsync(string preference)
        => _bridge.SetProtocolPreferenceAsync(preference);

    private Task SetTunStackAsync(string stack)
        => _bridge.SetTunStackAsync(stack);

    private Task SetAutoReconnectAsync(bool enabled)
        => _bridge.SetAutoReconnectAsync(enabled);

    private Task SetGpnRecoveryWatchAsync(bool enabled)
        => _bridge.SetGpnRecoveryWatchAsync(enabled);

    private Task SetGpnFailoverAsync(bool enabled)
        => _bridge.SetGpnFailoverAsync(enabled);

    private Task SetVlessBypassNodeAsync(JsonElement root)
        => _bridge.SetVlessBypassNodeAsync(root);

    private Task SetVlessBypassFromUriAsync(JsonElement root)
        => _bridge.SetVlessBypassFromUriAsync(root);

    private Task SetGpnCaptureSettingsAsync(JsonElement root)
        => _bridge.SetGpnCaptureSettingsAsync(root);

    private Task SetGpnWintunSettingsAsync(JsonElement root)
        => _bridge.SetGpnWintunSettingsAsync(root);

    private Task SetDashboardModeAsync(string mode)
        => _bridge.SetDashboardModeAsync(mode);

    private Task TestProxyAsync()
        => _bridge.TestProxyAsync();

    private Task CheckIpAsync()
        => _bridge.CheckIpAsync();

    private Task RestoreGpnDefaultsAsync()
        => _bridge.RestoreGpnDefaultsAsync();

    private Task SaveSettingsAsync(JsonElement root)
        => _bridge.SaveSettingsAsync(root);

    private Task RunRouteTestAsync(
        string exeName,
        string destination,
        string port,
        string network,
        string exePath)
        => _bridge.RunRouteTestAsync(exeName, destination, port, network, exePath);

    private Task ToggleSystemProxyAsync()
        => _bridge.ToggleSystemProxyAsync();

    private Task SetSystemProxyModeAsync(ESysProxyType requestedType)
        => _bridge.SetSystemProxyModeAsync(requestedType);

    private Task PushGpnServersAsync()
        => _bridge.PushGpnServersAsync();

    private Task PushGpnDefaultsStatusAsync(WireGuardServerCatalog.GpnDefaultsRestoreResult? restoreResult = null)
        => _bridge.PushGpnDefaultsStatusAsync(restoreResult);

    private Task PushGpnPidPoolAsync()
        => _bridge.PushGpnPidPoolAsync();

    private Task PushGpnTelemetryAsync()
        => _bridge.PushGpnTelemetryAsync();

    private Task PushGpnCaptureStatsAsync()
        => _bridge.PushGpnCaptureStatsAsync();

    private Task PushGpnResilienceLogAsync()
        => _bridge.PushGpnResilienceLogAsync();

    private Task PushGpnWintunSettingsAsync()
        => _bridge.PushGpnWintunSettingsAsync();

    private Task PushGpnCaptureSettingsAsync()
        => _bridge.PushGpnCaptureSettingsAsync();

    private Task PushVlessBypassNodeAsync()
        => _bridge.PushVlessBypassNodeAsync();

    private Task PushEffectsTierAsync()
        => _bridge.PushEffectsTierAsync();

    private Task PushLanguageAsync()
        => _bridge.PushLanguageAsync();

    private Task PushMonitorSnapshotAsync(bool force = false)
        => _bridge.PushMonitorSnapshotAsync(force);

    /// <summary>
    /// Geri alma push'u: son kaldırma/rota değişikliği varsa toast için
    /// window.setUndoAvailable({ kind, processName, displayName }) gönderir,
    /// yoksa null (toast gizlenir).
    /// </summary>
    private Task PushUndoStateAsync()
    {
        var payload = _lastAppUndo is { } undo
            ? JsonSerializer.Serialize(new
            {
                kind = undo.Kind.ToString().ToLowerInvariant(),
                processName = undo.ProcessName,
                displayName = undo.DisplayName ?? undo.ProcessName,
            }, UndoJsonOptions)
            : "null";
        return _bridge.PushUndoStateAsync($"window.setUndoAvailable?.({payload});");
    }

    /// <summary>Undo slot'u temizler (yeni bir uygulama ekleme kaydı eski işlemi geçersiz kılar).</summary>
    private Task ClearAppUndoAsync()
    {
        _lastAppUndo = null;
        return PushUndoStateAsync();
    }

    /// <summary>
    /// Son uygulama işlemini geri alır (tek seviye): kaldırılan uygulama eski
    /// rotası/egress düğümüyle yeniden eklenir, rota değişikliği eski rotaya döner,
    /// hızlı eklenen uygulama listeden çıkarılır. Ardından otoritatif snapshot
    /// yeniden itilir ve undo slotu boşaltılır.
    /// </summary>
    private async Task UndoLastAppOpAsync()
    {
        if (ViewModel?.ConnectionViewModel is not { } undoViewModel || _lastAppUndo is not { } undo)
        {
            await NotifyNodesOpAsync("Nothing to undo");
            return;
        }

        var applied = false;
        switch (undo.Kind)
        {
            case AppUndoKind.Remove:
            case AppUndoKind.Route:
                // Geri almak = uygulamayı (kayıtlı rota + WARP egress düğümüyle)
                // yeniden ekle ya da eski rotaya döndür — ikisi de dashboard'un
                // tek giriş noktasından (SetDashboardAppRouteAsync) geçer.
                applied = await undoViewModel.SetDashboardAppRouteAsync(
                    undo.ProcessName, undo.DisplayName, undo.Action,
                    undo.WarpNodeIndexId, undo.WarpNodeName);
                break;

            case AppUndoKind.Add:
                var addedApp = undoViewModel.Apps.FirstOrDefault(a => a.EntryType == "app"
                    && a.Value.Equals(undo.ProcessName, StringComparison.OrdinalIgnoreCase));
                if (addedApp is not null)
                {
                    undoViewModel.SelectedApp = addedApp;
                    undoViewModel.RemoveAppCmd.Execute().Subscribe();
                    applied = true;
                }
                break;
        }

        _lastAppUndo = null;
        await PushMonitorSnapshotAsync(force: true);
        await PushUndoStateAsync();
        await NotifyNodesOpAsync(applied ? "Last change undone" : "Nothing to undo");
    }

    private Task PushProcessCatalogAsync()
        => _bridge.PushProcessCatalogAsync();

    private Task PushAppIconsAsync(string[] paths)
        => _bridge.PushAppIconsAsync(paths);

    private Task PushNodePoolAsync()
        => _bridge.PushNodePoolAsync();

    private Task PushSettingsAsync()
        => _bridge.PushSettingsAsync();

    private Task NotifyNodesOpAsync(string message)
        => _bridge.NotifyNodesOpAsync(message);
}
