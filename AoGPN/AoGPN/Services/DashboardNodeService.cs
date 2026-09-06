using System.Text;
using System.Text.Json;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Fmt;
using ServiceLib.Helper;
using ServiceLib.Services;
using ServiceLib.ViewModels;

namespace AoGPN.Services;

/// <summary>A WireGuard .conf file dropped onto the dashboard: display name (file name) and raw text.</summary>
public sealed record WireGuardConfFile(string Name, string Content);

// ─────────────────────────────────────────────────────────────────────────
// DashboardNodeService — Düğüm/profil yönetimi iş mantığı (P0 Faz 2, 3. Dalga)
//
// MainWindow.xaml.cs içindeki düğüm kümesi buraya taşındı: düğüm seçimi
// (SelectNodeAsync), kopyala/yapıştır/sil (Copy/Paste/DeleteNodesAsync),
// devre dışı bırakma/geri getirme (Disable/RestoreNodesAsync), başarısız
// temizliği + tekilleştirme (CleanupFailedNodesAsync / DedupNodesAsync),
// favori yıldızı (ToggleNodeFavAsync), düğüm havuzu link işlemleri
// (Add/Edit/Remove/FetchNodePoolLinkAsync) ve liste yayınları
// (PushNodeListAsync / PushNodeInfoAsync / PushNodePoolAsync) + gerçek ping
// köprüsü (StartNodeSpeedtestAsync / StopNodeSpeedtest — kendi
// _nodeSpeedtestService / _nodeTestRunId durumuyla).
//
// UI kanalına yalnızca ctor'a enjekte edilen delegelerle dokunur:
// executeScript (WebView2 yürütme — MainWindow.ExecuteScriptSafelyAsync),
// WebViewReady bayrağı, kapanma bayrağı, toast kanalı (NotifyNodesOpAsync —
// MainWindow'da kaldı çünkü SystemProxyOnlyService ctor'u ona bağlı),
// ProfilesViewModel erişimi, UI iş parçacığına atlama (Dispatcher.InvokeAsync),
// proxy-only servisi, sistem-proxy durum yayını ve tepsi durum güncellemesi.
// Dialog/tray/yaşam-döngüsü MainWindow'da kalır. Taşınan gövdelerde hiçbir
// satır değişmedi — yalnızca yukarıdaki pencere üyelerine yapılan çağrılar
// ilgili delegeye mekanik olarak yönlendirildi.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Düğüm/profil yönetimi (seçim, CRUD, havuz, favori, ping testi, liste yayını)
/// iş mantığı. MainWindow tarafından kurulur; IDashboardBridge düğüm üyeleri bu
/// servise delege edilir.
/// </summary>
internal sealed class DashboardNodeService
{
    private readonly Func<string, Task> _executeScript;
    private readonly Func<bool> _isWebViewReady;
    private readonly Func<bool> _isClosing;
    private readonly Func<string, Task> _notifyNodesOp;
    private readonly Func<ProfilesViewModel?> _getProfilesViewModel;
    private readonly Func<MainWindowViewModel?> _getMainViewModel;
    private readonly Action<Action> _invokeOnUiThread;
    private readonly SystemProxyOnlyService _proxyOnlyService;
    private readonly Func<bool, Task> _pushSystemProxyState;
    private readonly Action _updateTrayStatus;

    // Route-test results are pushed to the renderer with camelCase keys to match the
    // rest of the host→renderer payloads.
    private static readonly JsonSerializerOptions RouteTestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Signature of the last pushed node card, so the 2 s poll stays silent while
    // the name/address/protocol are unchanged.
    private string _lastNodeSignature = "";

    // Real ping-test bridge: runs the native SpeedtestService over the selected
    // profiles and streams per-node results into the WebView2 DOM.
    private SpeedtestService? _nodeSpeedtestService;
    private CancellationTokenSource? _nodePingCancellation;
    private bool _nodeTestRunning;
    private long _nodeTestRunId;

    // A single run can probe through two paths at once (WireGuard probe chain +
    // regular SpeedtestService). Both paths report completion; the run must be
    // finalized exactly once — when the LAST path finishes. Finalizing while the
    // other path is still in flight clears the renderer's testing state and every
    // later result is dropped by its stale-callback guard (observed live: the WG
    // probe finished first, so the VLESS node's TCP result never reached the card).
    private int _nodeTestActivePaths;

    // runId of the run that last dispatched the shared SpeedtestService, so the
    // service's terminal update finalizes ITS OWN run — never a newer one (a
    // stopped run's late callback must not finalize the run that replaced it).
    private long _nodeSpeedtestRunId;

    // Last-resort run watchdog: if a node test never finalizes (a probe, DNS
    // lookup or core launch hanging), the renderer would stay on "Test ediliyor…"
    // forever. The watchdog force-finishes the run after a generous grace period
    // so the cards always come back to a settled state.
    private CancellationTokenSource? _nodeTestWatchdogCts;
    private const int NodeTestWatchdogSeconds = 120;

    public DashboardNodeService(
        Func<string, Task> executeScript,
        Func<bool> isWebViewReady,
        Func<bool> isClosing,
        Func<string, Task> notifyNodesOp,
        Func<ProfilesViewModel?> getProfilesViewModel,
        Func<MainWindowViewModel?> getMainViewModel,
        Action<Action> invokeOnUiThread,
        SystemProxyOnlyService proxyOnlyService,
        Func<bool, Task> pushSystemProxyState,
        Action updateTrayStatus)
    {
        _executeScript = executeScript ?? throw new ArgumentNullException(nameof(executeScript));
        _isWebViewReady = isWebViewReady ?? throw new ArgumentNullException(nameof(isWebViewReady));
        _isClosing = isClosing ?? throw new ArgumentNullException(nameof(isClosing));
        _notifyNodesOp = notifyNodesOp ?? throw new ArgumentNullException(nameof(notifyNodesOp));
        _getProfilesViewModel = getProfilesViewModel ?? throw new ArgumentNullException(nameof(getProfilesViewModel));
        _getMainViewModel = getMainViewModel ?? throw new ArgumentNullException(nameof(getMainViewModel));
        _invokeOnUiThread = invokeOnUiThread ?? throw new ArgumentNullException(nameof(invokeOnUiThread));
        _proxyOnlyService = proxyOnlyService ?? throw new ArgumentNullException(nameof(proxyOnlyService));
        _pushSystemProxyState = pushSystemProxyState ?? throw new ArgumentNullException(nameof(pushSystemProxyState));
        _updateTrayStatus = updateTrayStatus ?? throw new ArgumentNullException(nameof(updateTrayStatus));
    }

    private Task ExecuteScriptSafelyAsync(string script) => _executeScript(script);
    private bool WebViewReady => _isWebViewReady();

    /// <summary>Current node speed-test run id (volatile read); stale stop requests are ignored.</summary>
    public long NodeTestRunId => Volatile.Read(ref _nodeTestRunId);

    /// <summary>
    /// Sends the currently selected AoGPN profile to the dashboard node card.
    /// The signature check suppresses repeats so the 2 s poll only touches the
    /// browser when the name, address or protocol actually changed.
    /// </summary>
    public async Task PushNodeInfoAsync(bool force = false)
    {
        if (!WebViewReady)
        {
            return;
        }

        ProfileItem? profile;
        try
        {
            profile = await AppManager.Instance.GetProfileItem(AppManager.Instance.Config.IndexId);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node info lookup failed", ex);
            return;
        }

        if (profile is null)
        {
            return;
        }

        var address = profile.Port > 0 ? $"{profile.Address}:{profile.Port}" : profile.Address;
        var signature = $"{profile.Remarks}|{address}|{profile.ConfigType}";
        if (!force && signature == _lastNodeSignature)
        {
            return;
        }

        _lastNodeSignature = signature;
        var nameJson = JsonSerializer.Serialize(profile.Remarks);
        var addressJson = JsonSerializer.Serialize(address);
        var protocolJson = JsonSerializer.Serialize(profile.ConfigType.ToString());
        await ExecuteScriptSafelyAsync(
            $"window.updateNodeInfo({nameJson}, {addressJson}, {protocolJson});");
    }

    /// <summary>
    /// Switches the active AoGPN server from the dashboard node list. Reuses the
    /// same persistence + core reload path as the native servers view, then pushes
    /// the switched profile and refreshed selection back to the renderer.
    /// </summary>
    public async Task SelectNodeAsync(string indexId)
    {
        var profilesViewModel = _getProfilesViewModel();
        if (profilesViewModel is null)
        {
            await AcknowledgeNodeSwitchAsync(succeeded: false, indexId);
            return;
        }

        var succeeded = false;
        try
        {
            await profilesViewModel.SetDefaultServer(indexId);
            // SetDefaultServer can return silently (unknown id, same server); the
            // persisted IndexId is the authoritative proof the switch took effect.
            succeeded = AppManager.Instance.Config.IndexId == indexId;
            if (succeeded)
            {
                // Feed the "Recently used" node sort.
                ProfileExManager.Instance.TouchLastUsed(indexId);
                await ProfileExManager.Instance.SaveTo();

                // While the proxy-only core is up (no tunnel), re-run it on the newly
                // selected node so the system proxy follows the node picker.
                if (_proxyOnlyService.IsRunning)
                {
                    try
                    {
                        await _proxyOnlyService.RestartOnNodeChangeAsync(AppManager.Instance.Config);
                        await _pushSystemProxyState(true);
                    }
                    catch (Exception ex)
                    {
                        Logging.SaveLog("AoGPN proxy-only node switch failed", ex);
                    }
                }

                _updateTrayStatus();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("WebView2 server switch failed", ex);
        }

        // Always publish the effective profile and selection, then acknowledge so
        // the renderer can clear its "switching…" state or roll the switch back.
        await PushNodeInfoAsync(force: true);
        await PushNodeListAsync();
        await AcknowledgeNodeSwitchAsync(succeeded, indexId);
    }

    private async Task AcknowledgeNodeSwitchAsync(bool succeeded, string indexId)
    {
        await ExecuteScriptSafelyAsync(
            $"window.setNodeSwitchResult({JsonSerializer.Serialize(succeeded)}, {JsonSerializer.Serialize(indexId)});");
    }

    /// <summary>
    /// Loads the profile entities for the given ids; unknown or unreadable ids are
    /// skipped so a stale renderer selection can never crash the operation.
    /// </summary>
    private async Task<List<ProfileItem>> LoadProfilesByIdsAsync(IReadOnlyCollection<string> indexIds)
    {
        var profiles = new List<ProfileItem>();
        foreach (var id in indexIds)
        {
            if (id.IsNullOrEmpty())
            {
                continue;
            }

            try
            {
                var item = await AppManager.Instance.GetProfileItem(id);
                if (item is not null)
                {
                    profiles.Add(item);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN node lookup failed", ex);
            }
        }
        return profiles;
    }

    /// <summary>
    /// Copies the share links (vless://, vmess://, ...) of the selected nodes to the
    /// clipboard, mirroring the native servers view's Ctrl+C (Export2ShareUrl). An
    /// empty selection copies the active profile.
    /// </summary>
    public async Task CopyNodesAsync(string[] indexIds)
    {
        var ids = indexIds.Length > 0 ? indexIds : new[] { AppManager.Instance.Config.IndexId };
        var profiles = await LoadProfilesByIdsAsync(ids);
        if (profiles.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (var item in profiles)
        {
            var url = FmtHandler.GetShareUri(item);
            if (url.IsNullOrEmpty())
            {
                continue;
            }
            sb.AppendLine(url);
        }

        if (sb.Length == 0)
        {
            await _notifyNodesOp("Selected nodes have no share link");
            return;
        }

        WindowsUtils.SetClipboardData(sb.ToString());
        await _notifyNodesOp($"Copied {profiles.Count} node share link(s) to clipboard");
    }

    /// <summary>
    /// Imports nodes from the clipboard into the current group, mirroring the native
    /// Ctrl+V (AddServerViaClipboard), then republishes the refreshed node list.
    /// </summary>
    public async Task PasteNodesAsync()
    {
        var clipboardData = WindowsUtils.GetClipboardData();
        if (clipboardData.IsNullOrEmpty())
        {
            await _notifyNodesOp("Clipboard is empty");
            return;
        }

        try
        {
            var ret = await ConfigHandler.AddBatchServers(
                AppManager.Instance.Config, clipboardData, AppManager.Instance.Config.SubIndexId, false);
            Logging.SaveLog($"AoGPN clipboard paste imported {ret} node(s)");
            await PushNodeListAsync();
            await _notifyNodesOp(ret > 0
                ? $"Imported {ret} node(s) from clipboard"
                : "No valid nodes found in clipboard");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN clipboard import failed", ex);
            await _notifyNodesOp("Clipboard import failed");
        }
    }

    /// <summary>
    /// Opens the native server-edit dialog for one node — the same flow as the
    /// legacy ProfilesView right-click → Edit (AddServer/AddServer2/AddGroupServer
    /// by config type) — then republishes the refreshed list on OK and reloads the
    /// core when the edited node is the active profile.
    /// </summary>
    public async Task EditNodeAsync(string indexId)
    {
        if (indexId.IsNullOrEmpty())
        {
            return;
        }

        ProfileItem? item;
        try
        {
            item = await AppManager.Instance.GetProfileItem(indexId);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node edit lookup failed", ex);
            await _notifyNodesOp("Node could not be loaded");
            return;
        }
        if (item is null)
        {
            await _notifyNodesOp("Node not found");
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            bool? ret = false;
            var eConfigType = item.ConfigType;
            if (eConfigType == EConfigType.Custom)
            {
                ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(new AddServer2ViewModel(item));
            }
            else if (eConfigType.IsGroupType())
            {
                ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(new AddGroupServerViewModel(item));
            }
            else
            {
                ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(new AddServerViewModel(item));
            }

            if (ret != true)
            {
                return;
            }

            var profilesViewModel = _getProfilesViewModel();
            if (profilesViewModel is not null)
            {
                await profilesViewModel.RefreshServers();
                if (item.IndexId == config.IndexId)
                {
                    profilesViewModel.ReloadRequested.Publish();
                }
            }

            await PushNodeInfoAsync(force: true);
            await PushNodeListAsync();
            await _notifyNodesOp("Node updated");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node edit failed", ex);
            await _notifyNodesOp("Node edit failed");
        }
    }

    /// <summary>
    /// Imports dropped WireGuard .conf files as server profiles named after the
    /// file (the host half of drag-and-drop onto the dashboard). Every
    /// [Interface]/[Peer] pair becomes a profile in the current group, persisted
    /// through the same path as paste-imported WireGuard configs; the GPN server
    /// catalog upsert is kept so both entry points behave identically.
    /// </summary>
    public async Task ImportWireGuardConfsAsync(IReadOnlyCollection<WireGuardConfFile> files)
    {
        if (files is null || files.Count == 0)
        {
            return;
        }

        var config = AppManager.Instance.Config;
        var imported = 0;
        try
        {
            foreach (var file in files)
            {
                if (file.Content.IsNullOrEmpty())
                {
                    continue;
                }

                var servers = WireguardFmt.ResolveConfig(file.Content);
                if (servers is null || servers.Count == 0)
                {
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(file.Name ?? string.Empty).Trim();
                if (baseName.IsNullOrEmpty())
                {
                    baseName = "WireGuard";
                }

                var added = 0;
                foreach (var item in servers)
                {
                    item.Subid = config.SubIndexId;
                    item.IsSub = false;
                    // The file name becomes the server name; extra peers inside
                    // the same file get a numeric suffix.
                    item.Remarks = servers.Count > 1 && added > 0 ? $"{baseName} ({added + 1})" : baseName;
                    if (await ConfigHandler.AddWireguardServer(config, item) == 0)
                    {
                        await WireGuardServerCatalog.UpsertFromProfileAsync(item);
                        added++;
                    }
                }
                imported += added;
            }

            if (imported > 0)
            {
                await ConfigHandler.SaveConfig(config);

                var profilesViewModel = _getProfilesViewModel();
                if (profilesViewModel is not null)
                {
                    await profilesViewModel.RefreshServers();
                }

                await PushNodeListAsync();
                await _notifyNodesOp($"{imported} WireGuard server(s) imported");
            }
            else
            {
                await _notifyNodesOp("No valid WireGuard .conf found");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN wireguard conf import failed", ex);
            await _notifyNodesOp("WireGuard import failed");
        }
    }

    /// <summary>
    /// Reorders one node to the position of another (drag-to-reorder in the
    /// dashboard's Default order), persisting through the exact path the native
    /// servers view's drag-drop uses (ConfigHandler.MoveServer + EMove.Position),
    /// then republishes the list.
    /// </summary>
    public async Task MoveNodeAsync(string indexId, string targetIndexId)
    {
        if (indexId.IsNullOrEmpty() || targetIndexId.IsNullOrEmpty() || indexId == targetIndexId)
        {
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            var profiles = await AppManager.Instance.ProfileItems(config.SubIndexId) ?? [];
            if (profiles.Count < 2)
            {
                return;
            }

            // The indexes must come from the Sort-ordered list (the same order
            // PushNodeListAsync and the native view use), not raw table order.
            var exs = await ProfileExManager.Instance.GetProfileExs();
            var sortByIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            if (exs is not null)
            {
                foreach (var ex in exs)
                {
                    if (ex?.IndexId is { Length: > 0 } && !sortByIndex.ContainsKey(ex.IndexId))
                    {
                        sortByIndex[ex.IndexId] = ex.Sort;
                    }
                }
            }
            var ordered = profiles
                .OrderBy(p => sortByIndex.TryGetValue(p.IndexId, out var sort) ? sort : 0)
                .ToList();

            var fromIndex = ordered.FindIndex(p => p.IndexId == indexId);
            var toIndex = ordered.FindIndex(p => p.IndexId == targetIndexId);
            if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex)
            {
                return;
            }

            if (await ConfigHandler.MoveServer(config, ordered, fromIndex, EMove.Position, toIndex) != 0)
            {
                return;
            }
            await ProfileExManager.Instance.SaveTo();

            var profilesViewModel = _getProfilesViewModel();
            if (profilesViewModel is not null)
            {
                await profilesViewModel.RefreshServers();
            }

            await PushNodeListAsync();
            await _notifyNodesOp("Node moved");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node move failed", ex);
            await _notifyNodesOp("Node reorder failed");
        }
    }

    /// <summary>
    /// Deletes the given nodes through the same persistence path as the native view
    /// (ConfigHandler.RemoveServers), reloads the core if the active profile was
    /// among the removed ones, then republishes the list.
    /// </summary>
    public async Task DeleteNodesAsync(string[] indexIds)
    {
        var profiles = await LoadProfilesByIdsAsync(indexIds);
        if (profiles.Count == 0)
        {
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            var removedActive = profiles.Exists(t => t.IndexId == config.IndexId);
            await ConfigHandler.RemoveServers(config, profiles);

            var profilesViewModel = _getProfilesViewModel();
            if (profilesViewModel is not null)
            {
                await profilesViewModel.RefreshServers();
                if (removedActive)
                {
                    profilesViewModel.ReloadRequested.Publish();
                }
            }

            await PushNodeInfoAsync(force: true);
            await PushNodeListAsync();
            await _notifyNodesOp($"Deleted {profiles.Count} node(s)");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node delete failed", ex);
            await _notifyNodesOp("Delete failed");
        }
    }

    /// <summary>
    /// Runs the native real-ping test over the given nodes (empty array = the whole
    /// current group) and streams per-node results to the renderer. The same
    /// SpeedtestService the native servers view uses, so delays persist in
    /// ProfileEx and the "remove failed" cleanup matches native semantics.
    /// </summary>
    public async Task StartNodeSpeedtestAsync(string[] indexIds, string? testType = null, long requestedRunId = 0)
    {
        // The renderer marks the cards as testing BEFORE the host processes the
        // request, so EVERY refusal path below must roll the renderer state
        // back — otherwise the cards stay on "Test ediliyor…" forever (observed
        // live: a refused start left the whole node list stuck).
        var runId = requestedRunId > 0 ? requestedRunId : Interlocked.Increment(ref _nodeTestRunId);
        Interlocked.Exchange(ref _nodeTestRunId, runId);

        try
        {
            if (_nodeTestRunning)
            {
                // The flag can only be trusted while the service really has a run in
                // flight (e.g. a dashboard page reload can strand it as true). A
                // stuck flag must not permanently block new tests.
                if (_nodeSpeedtestService?.HasActiveRun == true)
                {
                    await _notifyNodesOp("A ping test is already running — press Stop to cancel it");
                    await RollbackNodeTestUiAsync(runId);
                    return;
                }
                _nodeTestRunning = false;
            }

            List<ProfileItem> profiles;
            try
            {
                profiles = indexIds.Length > 0
                    ? await LoadProfilesByIdsAsync(indexIds)
                    : await AppManager.Instance.ProfileItems(AppManager.Instance.Config.SubIndexId) ?? [];
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN ping test profile load failed", ex);
                await _notifyNodesOp("Ping test could not load the nodes");
                await RollbackNodeTestUiAsync(runId);
                return;
            }

            // Skip non-testable profiles (custom configs, portless groups) the same
            // way SpeedtestService.GetClearItem does.
            var testable = profiles.Where(p => p.ConfigType != EConfigType.Custom
                && (p.ConfigType.IsComplexType() || p.Port > 0)).ToList();
            if (testable.Count == 0)
            {
                await _notifyNodesOp("No testable nodes in the selection");
                await RollbackNodeTestUiAsync(runId);
                return;
            }

            var normalizedTestType = testType is "udp" or "both" ? testType : "tcp";
            var actionType = normalizedTestType switch
            {
                "udp" => ESpeedActionType.UdpTest,
                "both" => ESpeedActionType.Mixedtest,
                _ => ESpeedActionType.Tcping,
            };

            _nodeTestRunning = true;
            await ExecuteScriptSafelyAsync($"window.setNodeTestRunning(true, {runId});");
            Logging.SaveLog($"AoGPN ping test starting with {testable.Count} node(s), type={normalizedTestType}");

            // WireGuard endpoints speak UDP only, so the TCP-based SpeedtestService
            // would mark every WireGuard node "failed". Route those through the same
            // GPN probe the GPN servers panel uses (real WireGuard handshake + ICMP)
            // so the dashboard node list shows a truthful delay.
            var wireGuardNodes = testable.Where(p => p.ConfigType == EConfigType.WireGuard).ToList();
            var regularNodes = testable.Where(p => p.ConfigType != EConfigType.WireGuard).ToList();

            // This run has one active path per probing source. Each path decrements
            // the counter when it finishes; the path that reaches zero finalizes the
            // run. See FinalizeNodeTestPathAsync for why this must be shared.
            Interlocked.Exchange(ref _nodeTestActivePaths,
                (wireGuardNodes.Count > 0 ? 1 : 0) + (regularNodes.Count > 0 ? 1 : 0));

            // A superseded run must stop probing right away: cancel the previous
            // token before dispatching, so both paths of THIS run share the fresh one.
            _nodePingCancellation?.Cancel();
            _nodePingCancellation = new CancellationTokenSource();
            if (wireGuardNodes.Count > 0)
            {
                _ = RunWireGuardProbeAsync(wireGuardNodes, runId, _nodePingCancellation.Token);
            }
            if (regularNodes.Count == 0)
            {
                return;
            }

            Interlocked.Exchange(ref _nodeSpeedtestRunId, runId);
            _nodeSpeedtestService ??= new SpeedtestService(AppManager.Instance.Config, result =>
            {
                // Never await the renderer from the speed-test worker thread:
                // CoreWebView2.ExecuteScriptAsync can deadlock when awaited off the UI
                // thread, which would stall the whole test run. Dispatch the DOM push
                // onto the UI thread and return immediately, mirroring how the native
                // servers view schedules speed-test updates on the main scheduler.
                if (result is null || _isClosing() || !WebViewReady)
                {
                    return Task.CompletedTask;
                }

                try
                {
                    _invokeOnUiThread(async () => await PushNodeTestResultAsync(result));
                }
                catch
                {
                    // The dispatcher is shutting down; there is nothing left to push.
                }

                return Task.CompletedTask;
            });

            // A previous run that was interrupted is stopped before starting fresh.
            _nodeSpeedtestService.ExitLoop();
            _nodeSpeedtestService.RunLoop(actionType, regularNodes, _nodePingCancellation.Token);

            // The run is fully dispatched — arm the last-resort watchdog so the
            // renderer can never stay on "Test ediliyor…" if a probe, DNS lookup
            // or core launch ever hangs.
            ArmNodeTestWatchdog(runId);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node test start failed", ex);
            await RollbackNodeTestUiAsync(runId);
        }
    }

    /// <summary>
    /// Pings WireGuard nodes with the GPN probe chain (real WireGuard handshake +
    /// ICMP fallback) instead of TCP, since WireGuard endpoints only answer UDP.
    /// Results are pushed through the same updateNodeTest channel and persisted
    /// into ProfileEx (delay) so the "remove failed" cleanup matches native
    /// semantics.
    /// </summary>
    private async Task RunWireGuardProbeAsync(
        List<ProfileItem> nodes,
        long runId,
        CancellationToken cancellationToken)
    {
        try
        {
            var mainViewModel = _getMainViewModel();
            if (mainViewModel is null)
            {
                await FinalizeNodeTestPathAsync(runId);
                return;
            }

            // Reuse the catalog mapping so the probe chain has the real client
            // private key + server public key required for the handshake.
            var profiles = new List<GpnServerProfile>();
            var indexByServerId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (WireGuardServerCatalog.TryMap(node, out var profile))
                {
                    profiles.Add(profile);
                    indexByServerId[profile.ServerId] = node.IndexId;
                }
            }

            if (profiles.Count == 0)
            {
                await FinalizeNodeTestPathAsync(runId);
                return;
            }

            var options = new GpnProbeOptions
            {
                // Fewer samples than the GPN panel: the node list test should feel
                // snappy while still averaging enough to filter one-off spikes.
                Samples = 3,
                PerSampleTimeoutMs = 1000,

                // Direct-path measurement, identical to the connect-time selection
                // (SelectBestServerAsync) that reports real values in the field.
                // EscapeTunnelForProbes is designed for the failover MONITOR, which
                // runs while AoGPN's own TUN is up; when the dashboard node test
                // sets it, ANY up tunnel — including a foreign one like the user's
                // own WireGuard client (wireguard.exe) — makes ProbeEgressNic report
                // "tunnel active", ICMP gets skipped and the delay hinges on a
                // single short handshake window. Reachable servers then show
                // "✗ başarısız" while the same chain at connect time measures them
                // fine. Without the flag, ICMP is measured on the direct path; if
                // AoGPN's own TUN is up, the probes travel the tunnel and report
                // the tunneled latency — a value either way.
                EscapeTunnelForProbes = false,

                // One dropped packet must not fail the measurement: two attempts
                // with a 2 s window each, instead of the default single short try.
                HandshakeProbe = new WireGuardHandshakeProbeOptions(WaitTimeoutMs: 2000, MaxAttempts: 2),
            };

            var icmpResults = await mainViewModel.ProbeServersAsync(profiles, options, cancellationToken);
            var udpResults = await mainViewModel.ProbeUdpAllAsync(profiles, options, cancellationToken);
            var udpByServer = udpResults.ToDictionary(u => u.ServerId, StringComparer.Ordinal);

            foreach (var probe in icmpResults)
            {
                if (!indexByServerId.TryGetValue(probe.ServerId, out var indexId))
                {
                    continue;
                }

                // Prefer the handshake RTT (definitive proof the server accepted
                // our keys); fall back to ICMP delay when the handshake channel is
                // disabled or unreachable.
                var delayMs = -1;
                if (udpByServer.TryGetValue(probe.ServerId, out var udp)
                    && udp.Status == UdpProbeStatus.Open
                    && udp.RoundTripMs >= 0)
                {
                    delayMs = udp.RoundTripMs;
                }
                else if (probe.IsSuccess && probe.DelayMs >= 0)
                {
                    delayMs = probe.DelayMs;
                }

                ProfileExManager.Instance.SetTestDelay(indexId, delayMs);
                await PushNodeTestResultAsync(indexId, delayMs, runId);
            }

            await FinalizeNodeTestPathAsync(runId);
        }
        catch (OperationCanceledException)
        {
            // Superseded/stopped run — the new run owns the renderer state now.
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN wireguard node probe failed", ex);
            await FinalizeNodeTestPathAsync(runId);
        }
    }

    /// <summary>
    /// Marks one probing path (WireGuard chain or regular speed-test run) as
    /// finished. The run is finalized — renderer state cleared, "completed" pushed
    /// — exactly once, when the LAST active path finishes. Stale paths from a
    /// superseded or stopped run are ignored (runId no longer matches), so a late
    /// callback can never finalize the run that replaced it.
    /// </summary>
    private async Task FinalizeNodeTestPathAsync(long runId)
    {
        if (runId != Volatile.Read(ref _nodeTestRunId))
        {
            return;
        }

        // >0: another path is still probing — it finalizes. <0: a duplicate
        // terminal update (e.g. "Skip" followed by "Completed"); already done.
        if (Interlocked.Decrement(ref _nodeTestActivePaths) != 0)
        {
            return;
        }

        _nodeTestRunning = false;
        _nodeTestWatchdogCts?.Cancel();
        await ExecuteScriptSafelyAsync(
            $"window.updateNodeTest({JsonSerializer.Serialize(string.Empty)}, {JsonSerializer.Serialize("completed")}, {runId});");
    }

    /// <summary>
    /// Rolls the renderer's optimistic "testing" state back when the host refuses
    /// a test (already running / no testable nodes / load failure) or the start
    /// itself throws. Without this the cards would stay on "Test ediliyor…"
    /// forever — the renderer marks them before the host answers.
    /// </summary>
    private async Task RollbackNodeTestUiAsync(long runId)
    {
        // Only the run that owns the renderer state may clear it; a stale
        // refusal must never stomp a newer run's state.
        if (runId != Volatile.Read(ref _nodeTestRunId))
        {
            return;
        }

        _nodeTestRunning = false;
        await ExecuteScriptSafelyAsync($"window.setNodeTestRunning(false, {runId});");
    }

    /// <summary>
    /// Last-resort run watchdog: when a node test is dispatched, this timer is
    /// armed for <see cref="NodeTestWatchdogSeconds"/>. If the run has not
    /// finalized by then (a probe, DNS lookup or core launch hanging), the
    /// probing is cancelled and the renderer state is cleared so the cards can
    /// never stay on "Test ediliyor…" indefinitely. The runId guard ensures a
    /// superseded or newer run is never touched.
    /// </summary>
    private void ArmNodeTestWatchdog(long runId)
    {
        _nodeTestWatchdogCts?.Cancel();
        var cts = new CancellationTokenSource();
        _nodeTestWatchdogCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(NodeTestWatchdogSeconds), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // the run finalized (or was superseded) before the deadline
            }

            if (runId != Volatile.Read(ref _nodeTestRunId))
            {
                return; // a newer run owns the renderer state now
            }

            Logging.SaveLog($"AoGPN node test watchdog fired — run {runId} forced to finish");
            _nodePingCancellation?.Cancel();
            _nodeSpeedtestService?.ExitLoop();

            // Reset the path counter so late completions of the stuck paths are
            // treated as stale (decrementing from 0 never reaches the finalize
            // branch) and the renderer is cleared exactly once, on the UI thread.
            Interlocked.Exchange(ref _nodeTestActivePaths, 0);
            _nodeTestRunning = false;
            try
            {
                _invokeOnUiThread(async () =>
                    await ExecuteScriptSafelyAsync($"window.setNodeTestRunning(false, {runId});"));
            }
            catch
            {
                // The dispatcher is shutting down; there is nothing left to clear.
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Pushes one speed-test result to the renderer on the UI thread. An empty
    /// IndexId marks the run as finished/stopped and clears the running state.
    /// </summary>
    /// <summary>Pushes one probe result (index id + delay) for the WireGuard node probe path.</summary>
    private async Task PushNodeTestResultAsync(string indexId, int delay, long runId)
    {
        // The renderer contract (window.updateNodeTest) expects the delay as a
        // STRING, exactly like the native SpeedtestService path (SpeedTestResult.
        // Delay is a string). A raw JSON number is silently dropped there — the
        // WireGuard node probe used to send numbers, so every WG result vanished
        // and the cards never showed a delay ("—") nor "✗ başarısız" for -1.
        await ExecuteScriptSafelyAsync(
            $"window.updateNodeTest({JsonSerializer.Serialize(indexId)}, {JsonSerializer.Serialize(delay.ToString())}, {runId});");
    }

    private async Task PushNodeTestResultAsync(SpeedTestResult result)
    {
        if (result is null)
        {
            return;
        }

        // Empty IndexId = terminal update of the shared SpeedtestService run.
        // Route it through the shared path counter: it finalizes the run only
        // when the WireGuard probe path (if any) is also finished, and only for
        // the run that actually dispatched the service — never a newer one.
        if (result.IndexId.IsNullOrEmpty())
        {
            await FinalizeNodeTestPathAsync(Volatile.Read(ref _nodeSpeedtestRunId));
            return;
        }

        var runId = Volatile.Read(ref _nodeTestRunId);
        await ExecuteScriptSafelyAsync(
            $"window.updateNodeTest({JsonSerializer.Serialize(result.IndexId)}, {JsonSerializer.Serialize(result.Delay)}, {runId});");
    }

    /// <summary>Stops a running ping test; the service reports the stop back to the renderer.</summary>
    public void StopNodeSpeedtest()
    {
        Interlocked.Increment(ref _nodeTestRunId);
        _nodePingCancellation?.Cancel();
        _nodeSpeedtestService?.ExitLoop();
        _nodeTestWatchdogCts?.Cancel();
        _nodeTestRunning = false;
    }

    /// <summary>
    /// Moves the given nodes into the dashboard's Disabled section (a config-level
    /// flag, not a DB move), hides them from the main list, and persists the change.
    /// </summary>
    public async Task DisableNodesAsync(string[] indexIds)
    {
        var profiles = await LoadProfilesByIdsAsync(indexIds);
        if (profiles.Count == 0)
        {
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            config.DisabledIndexIds ??= [];
            var added = 0;
            foreach (var profile in profiles)
            {
                if (!config.DisabledIndexIds.Contains(profile.IndexId))
                {
                    config.DisabledIndexIds.Add(profile.IndexId);
                    added++;
                }
            }
            if (added > 0)
            {
                await ConfigHandler.SaveConfig(config);
            }

            await PushNodeListAsync();
            await _notifyNodesOp($"{added} node(s) moved to Disabled");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node disable failed", ex);
            await _notifyNodesOp("Disable failed");
        }
    }

    /// <summary>Moves the given nodes back from the Disabled section into the main list.</summary>
    public async Task RestoreNodesAsync(string[] indexIds)
    {
        try
        {
            var config = AppManager.Instance.Config;
            if (config.DisabledIndexIds is null || config.DisabledIndexIds.Count == 0)
            {
                return;
            }

            var restored = 0;
            foreach (var id in indexIds)
            {
                if (config.DisabledIndexIds.Remove(id))
                {
                    restored++;
                }
            }
            if (restored == 0)
            {
                return;
            }

            await ConfigHandler.SaveConfig(config);
            await PushNodeListAsync();
            await _notifyNodesOp($"{restored} node(s) restored");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node restore failed", ex);
            await _notifyNodesOp("Restore failed");
        }
    }

    /// <summary>
    /// Finds the nodes in the current group whose last real-ping result was a
    /// failure (ProfileEx delay == -1) and either deletes them or moves them to
    /// the Disabled section, mirroring native RemoveInvalidServerResult.
    /// </summary>
    public async Task CleanupFailedNodesAsync(string target)
    {
        try
        {
            var config = AppManager.Instance.Config;
            var lstModel = await AppManager.Instance.ProfileModels(config.SubIndexId, "");
            if (lstModel is null || lstModel.Count == 0)
            {
                return;
            }

            var lstProfileExs = await ProfileExManager.Instance.GetProfileExs();
            var failedIds = lstModel
                .Where(t => !t.ConfigType.IsComplexType()
                    && lstProfileExs.Any(e => e.IndexId == t.IndexId && e.Delay == -1))
                .Select(t => t.IndexId)
                .ToHashSet();
            if (failedIds.Count == 0)
            {
                await _notifyNodesOp("No failed nodes found — run a ping test first");
                return;
            }

            var lstProfile = await AppManager.Instance.ProfileItems(config.SubIndexId) ?? [];
            var failed = lstProfile.Where(p => failedIds.Contains(p.IndexId)).ToList();
            if (failed.Count == 0)
            {
                return;
            }

            if (target == "disable")
            {
                config.DisabledIndexIds ??= [];
                var added = 0;
                foreach (var item in failed)
                {
                    if (!config.DisabledIndexIds.Contains(item.IndexId))
                    {
                        config.DisabledIndexIds.Add(item.IndexId);
                        added++;
                    }
                }
                if (added > 0)
                {
                    await ConfigHandler.SaveConfig(config);
                }
                await PushNodeListAsync();
                await _notifyNodesOp($"{added} failed node(s) moved to Disabled");
                return;
            }

            var removedActive = failed.Exists(t => t.IndexId == config.IndexId);
            await ConfigHandler.RemoveServers(config, failed);

            var profilesViewModel = _getProfilesViewModel();
            if (profilesViewModel is not null)
            {
                await profilesViewModel.RefreshServers();
                if (removedActive)
                {
                    profilesViewModel.ReloadRequested.Publish();
                }
            }

            await PushNodeInfoAsync(force: true);
            await PushNodeListAsync();
            await _notifyNodesOp($"{failed.Count} failed node(s) deleted");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN failed-node cleanup failed", ex);
            await _notifyNodesOp("Cleanup failed");
        }
    }

    /// <summary>
    /// Removes duplicate profiles from the current group using the same property-
    /// based comparison as the native servers view, keeping only one of each.
    /// </summary>
    public async Task DedupNodesAsync()
    {
        try
        {
            var config = AppManager.Instance.Config;
            var tuple = await ConfigHandler.DedupServerList(config, config.SubIndexId);
            if (tuple.Item1 > 0)
            {
                await PushNodeInfoAsync(force: true);
                await PushNodeListAsync();
                await _notifyNodesOp(
                    $"Duplicates removed: {tuple.Item1 - tuple.Item2} of {tuple.Item1} kept {tuple.Item2}");
            }
            else
            {
                await _notifyNodesOp("No duplicate nodes found");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node dedup failed", ex);
            await _notifyNodesOp("Deduplicate failed");
        }
    }

    /// <summary>Pushes the node-pool link list to the dashboard Nodes view.</summary>
    public async Task PushNodePoolAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        var links = AppManager.Instance.Config.NodePoolLinks ?? [];
        await ExecuteScriptSafelyAsync(
            $"window.updateNodePool({JsonSerializer.Serialize(links)});");
    }

    /// <summary>Adds a raw .txt / subscription URL to the node pool.</summary>
    public async Task AddNodePoolLinkAsync(string url)
    {
        url = url.Trim();
        if (!url.StartsWith(Global.HttpsProtocol, StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith(Global.HttpProtocol, StringComparison.OrdinalIgnoreCase))
        {
            await _notifyNodesOp("Invalid link — must start with http:// or https://");
            return;
        }

        var config = AppManager.Instance.Config;
        config.NodePoolLinks ??= [];
        if (!config.NodePoolLinks.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            config.NodePoolLinks.Add(url);
            await ConfigSaveQueue.SaveAndWaitAsync(config);
        }
        await PushNodePoolAsync();
        await _notifyNodesOp("Link added to pool");
    }

    /// <summary>Replaces a pooled URL with an edited one (same validation as add).</summary>
    public async Task EditNodePoolLinkAsync(string url, string newUrl)
    {
        url = url.Trim();
        newUrl = newUrl.Trim();
        if (!newUrl.StartsWith(Global.HttpsProtocol, StringComparison.OrdinalIgnoreCase)
            && !newUrl.StartsWith(Global.HttpProtocol, StringComparison.OrdinalIgnoreCase))
        {
            await _notifyNodesOp("Invalid link — must start with http:// or https://");
            return;
        }

        var config = AppManager.Instance.Config;
        config.NodePoolLinks ??= [];
        if (!config.NodePoolLinks.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            await _notifyNodesOp("Link not found in pool");
            return;
        }
        if (config.NodePoolLinks.Any(l => !l.Equals(url, StringComparison.OrdinalIgnoreCase)
                                          && l.Equals(newUrl, StringComparison.OrdinalIgnoreCase)))
        {
            await _notifyNodesOp("Link already in pool");
            return;
        }

        var idx = config.NodePoolLinks.FindIndex(l => l.Equals(url, StringComparison.OrdinalIgnoreCase));
        config.NodePoolLinks[idx] = newUrl;
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushNodePoolAsync();
        await _notifyNodesOp("Link updated");
    }

    /// <summary>Removes a URL from the node pool.</summary>
    public async Task RemoveNodePoolLinkAsync(string url)
    {
        var config = AppManager.Instance.Config;
        config.NodePoolLinks ??= [];
        config.NodePoolLinks.RemoveAll(l => l.Equals(url.Trim(), StringComparison.OrdinalIgnoreCase));
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushNodePoolAsync();
        await _notifyNodesOp("Link removed from pool");
    }

    /// <summary>
    /// Downloads every link in the node pool (plain .txt, base64 or subscription
    /// endpoints) and imports the shared nodes into the current group without
    /// touching existing entries. Reports per-link progress through the Nodes-view
    /// toast, then republishes the node list.
    /// </summary>
    public async Task FetchNodePoolAsync()
    {
        var config = AppManager.Instance.Config;
        var links = config.NodePoolLinks ?? [];
        if (links.Count == 0)
        {
            await _notifyNodesOp("Pool is empty — add links first");
            return;
        }

        await _notifyNodesOp($"Downloading nodes from {links.Count} pool link(s)…");
        var total = 0;
        var totalLinks = 0;
        foreach (var link in links)
        {
            var url = Utils.GetPunycode(link.Trim());
            if (url.IsNullOrEmpty())
            {
                continue;
            }

            var download = new DownloadService();
            try
            {
                var content = await download.TryDownloadString(url, false, "");
                // Retry through the proxy when a direct fetch comes back empty.
                if (content.IsNullOrEmpty())
                {
                    content = await download.TryDownloadString(url, true, "");
                }
                if (content.IsNullOrEmpty())
                {
                    await _notifyNodesOp($"{link} — no content");
                    continue;
                }

                content = NormalizeNodePoolContent(content);
                var ret = await ConfigHandler.AddBatchServers(config, content, "", false);
                if (ret > 0)
                {
                    total += ret;
                    totalLinks++;
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"NodePool fetch failed: {link}", ex);
                await _notifyNodesOp($"{link} — failed");
            }
        }

        await PushNodeInfoAsync(force: true);
        await PushNodeListAsync();
        await PushNodePoolAsync();
        await _notifyNodesOp(
            total > 0
                ? $"{total} new nodes imported from {totalLinks} pool link(s)"
                : "No new nodes found in the pool");
    }

    private static string NormalizeNodePoolContent(string content)
    {
        var value = content.Trim();
        if (value.Length == 0) return value;

        // Subscription feeds are commonly base64-wrapped. Decode only when the
        // decoded text looks like a node URI or a JSON profile document.
        var compact = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        try
        {
            var padded = compact.PadRight(compact.Length + (4 - compact.Length % 4) % 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (decoded.Contains("://", StringComparison.Ordinal)
                || decoded.Contains("{\"", StringComparison.Ordinal)
                || decoded.Contains("outbounds", StringComparison.OrdinalIgnoreCase))
            {
                value = decoded;
            }
        }
        catch (FormatException)
        {
            // Plain text feeds are expected and need no decoding.
        }

        // Accept JSON arrays/objects emitted by several public aggregators by
        // extracting common URI lines; AddBatchServers handles the URI formats.
        if (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                var uris = new List<string>();
                CollectNodeUris(document.RootElement, uris);
                value = string.Join(Environment.NewLine, uris);
            }
            catch (JsonException)
            {
                // Let the existing batch parser report unsupported content.
            }
        }

        return string.Join(Environment.NewLine, value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => IsNodeUri(line) || line.Contains("vless://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("vmess://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ss://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("trojan://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("hysteria", StringComparison.OrdinalIgnoreCase)
                || line.Contains("tuic://", StringComparison.OrdinalIgnoreCase)));
    }

    private static void CollectNodeUris(JsonElement element, List<string> uris)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (IsNodeUri(value)) uris.Add(value!);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) CollectNodeUris(child, uris);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) CollectNodeUris(property.Value, uris);
        }
    }

    private static bool IsNodeUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var line = value.Trim();
        return line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("hysteria://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Flips the favorite star for a node and republishes the list.</summary>
    public async Task ToggleNodeFavAsync(string indexId)
    {
        var current = ProfileExManager.Instance.GetFav(indexId);
        ProfileExManager.Instance.SetFav(indexId, !current);
        await ProfileExManager.Instance.SaveTo();
        await PushNodeListAsync();
        await _notifyNodesOp(!current ? "Added to favorites" : "Removed from favorites");
    }

    /// <summary>
    /// Streams the active subscription group's real profiles to the renderer in
    /// chunks (large subscriptions would exceed a single script payload), then
    /// finalizes with the active profile id so the Nodes view can render.
    /// </summary>
    public async Task PushNodeListAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        List<ProfileItemModel>? profiles;
        try
        {
            profiles = await AppManager.Instance.ProfileModels(AppManager.Instance.Config.SubIndexId, "");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node list lookup failed", ex);
            return;
        }

        if (profiles is null || profiles.Count == 0)
        {
            return;
        }

        var config = AppManager.Instance.Config;
        var activeIndexId = config.IndexId;
        var disabledIds = new HashSet<string>(config.DisabledIndexIds ?? []);

        // Favorites and last-used stamps come from the profile extension table so
        // the dashboard can offer country / favorite / recently-used sorting.
        var profileExs = await ProfileExManager.Instance.GetProfileExs();
        var exByIndex = new Dictionary<string, ProfileExItem>(StringComparer.Ordinal);
        if (profileExs is not null)
        {
            foreach (var ex in profileExs)
            {
                if (ex?.IndexId is { Length: > 0 } && !exByIndex.ContainsKey(ex.IndexId))
                {
                    exByIndex[ex.IndexId] = ex;
                }
            }
        }

        var projectNode = (ProfileItemModel p) =>
        {
            var ex = exByIndex.TryGetValue(p.IndexId, out var foundEx) ? foundEx : null;
            return new
            {
                indexId = p.IndexId,
                name = p.Remarks ?? string.Empty,
                address = p.Address ?? string.Empty,
                port = p.Port,
                protocol = p.ConfigType.ToString(),
                sub = p.SubRemarks ?? string.Empty,
                delay = p.Delay > 0 ? p.Delay : 0,
                active = p.IndexId == activeIndexId,
                // Prefer the explicit remark code, then resolve the address IP via
                // GeoIP so plain-IP nodes still sort by country.
                country = DashboardMessageDispatcher.ResolveNodeCountry(p.Remarks, p.Address),
                fav = ex?.IsFav ?? false,
                lastUsed = ex?.LastUsed ?? 0,
            };
        };

        // Mirror the native servers list order (ProfilesViewModel.GetProfileItemsEx
        // orders by ProfileEx.Sort, missing entries first). The dashboard's
        // "Default order" view and drag-to-reorder both rely on this order.
        var orderedProfiles = profiles
            .OrderBy(p => exByIndex.TryGetValue(p.IndexId, out var sortEx) ? sortEx.Sort : 0)
            .ToList();

        // Disabled nodes are hidden from the main list and pushed separately so
        // the renderer can show them in their own section with restore/delete.
        var nodes = orderedProfiles.Where(p => !disabledIds.Contains(p.IndexId)).Select(projectNode).ToList();
        var disabledNodes = orderedProfiles.Where(p => disabledIds.Contains(p.IndexId)).Select(projectNode).ToList();

        const int chunkSize = 150;
        for (var offset = 0; offset < nodes.Count; offset += chunkSize)
        {
            var chunk = nodes.Skip(offset).Take(chunkSize).ToList();
            var chunkJson = JsonSerializer.Serialize(chunk);
            await ExecuteScriptSafelyAsync($"window.updateNodeListAppend({chunkJson});");
        }

        var disabledJson = JsonSerializer.Serialize(disabledNodes);
        await ExecuteScriptSafelyAsync($"window.updateDisabledNodes({disabledJson});");

        var activeJson = JsonSerializer.Serialize(activeIndexId);
        await ExecuteScriptSafelyAsync($"window.updateNodeListDone({activeJson});");
    }
}
