using System.Text.Json;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services;
using ServiceLib.ViewModels;

namespace AoGPN.Services;

/// <summary>
/// The window-owned surface the <see cref="DashboardMessageDispatcher"/> calls into.
/// Every dashboard action ultimately lands on an operation that lives with the
/// window (connection lifecycle, node/server CRUD, dialogs, settings, tray-chrome
/// behaviour). Keeping this behind an interface lets the dispatcher stay a plain
/// Services-layer class while MainWindow remains the UI/lifecycle owner.
/// </summary>
public interface IDashboardBridge
{
    /// <summary>The typed window view-model (dashboard data context).</summary>
    MainWindowViewModel? ViewModel { get; }

    /// <summary>True while the window is tearing down; suppresses late pushes/logs.</summary>
    bool IsClosing { get; }

    /// <summary>The last dashboard view the renderer selected (dashboard/nodes/…).</summary>
    string ActiveView { get; set; }

    /// <summary>Current node speed-test run id (volatile read); stale stop requests are ignored.</summary>
    long NodeTestRunId { get; }

    /// <summary>Lazily-created GPN PID pool bridge (subscribe/snapshot source for the dashboard).</summary>
    GpnTargetResolverBridge GpnPidBridge { get; }

    // ---- connection / mode operations ----

    Task ToggleConnectionAsync(string requestedMode, string transport);
    Task RunGpnConnectAsync();
    Task SetConnectionModeAsync(string mode);
    Task SetDashboardModeAsync(string mode);
    Task SetTransportAsync(string transport);
    Task SetProtocolPreferenceAsync(string preference);
    Task SetTunStackAsync(string stack);
    Task SetAutoReconnectAsync(bool enabled);
    Task SetGpnRecoveryWatchAsync(bool enabled);
    Task SetGpnFailoverAsync(bool enabled);
    Task SetSystemProxyModeAsync(ESysProxyType requestedType);
    Task ToggleSystemProxyAsync();
    Task TestProxyAsync();
    Task CheckIpAsync();

    // ---- GPN server catalogue ----

    Task ProbeGpnServersAsync();
    Task ImportGpnServersAsync(string confText);
    Task DeleteGpnServerAsync(string serverId);
    Task ToggleGpnServerAsync(string serverId, bool enabled);
    Task ShowGpnServerEditDialogAsync(string? existingServerId);
    Task RestoreGpnDefaultsAsync();

    // ---- node / profile operations ----

    Task SelectNodeAsync(string indexId);
    Task CopyNodesAsync(string[] indexIds);
    Task PasteNodesAsync();
    Task EditNodeAsync(string indexId);
    Task ImportWireGuardConfsAsync(List<WireGuardConfFile> files);
    Task DeleteNodesAsync(string[] indexIds);
    Task MoveNodeAsync(string indexId, string targetIndexId);
    Task StartNodeSpeedtestAsync(string[] indexIds, string? testType = null, long requestedRunId = 0);
    void StopNodeSpeedtest();
    Task DisableNodesAsync(string[] indexIds);
    Task RestoreNodesAsync(string[] indexIds);
    Task CleanupFailedNodesAsync(string target);
    Task DedupNodesAsync();
    Task ToggleNodeFavAsync(string indexId);
    Task AddNodePoolLinkAsync(string url);
    Task EditNodePoolLinkAsync(string url, string newUrl);
    Task RemoveNodePoolLinkAsync(string url);
    Task FetchNodePoolAsync();

    // ---- split-tunnel / VLESS bypass ----

    Task SetVlessBypassNodeAsync(JsonElement root);
    Task SetVlessBypassFromUriAsync(JsonElement root);
    Task SetGpnCaptureSettingsAsync(JsonElement root);
    Task SetGpnWintunSettingsAsync(JsonElement root);

    // ---- diagnostics / route test ----

    Task RunRouteTestAsync(string exeName, string destination, string port, string network, string exePath);
    void ResetGpnTelemetry();
    void ClearGpnResilienceLog();
    bool TryResolveExecutablePath(int pid, out string runningPath);

    // ---- window chrome actions coming from the HTML title bar / shell ----

    void HandleAppControl(string command);

    // ---- theme ----

    /// <summary>Applies the theme through the native sidebar ViewModel when present.</summary>
    bool TryApplySidebarTheme(string wpfTheme);

    // ---- egress pushes (window-owned until they migrate into the dispatcher) ----

    Task PushGpnServersAsync();
    Task PushGpnDefaultsStatusAsync(WireGuardServerCatalog.GpnDefaultsRestoreResult? restoreResult = null);
    Task PushGpnPidPoolAsync();
    Task PushGpnTelemetryAsync();
    Task PushGpnCaptureStatsAsync();
    Task PushGpnResilienceLogAsync();
    Task PushGpnWintunSettingsAsync();
    Task PushGpnCaptureSettingsAsync();
    Task PushVlessBypassNodeAsync();
    Task PushEffectsTierAsync();
    Task PushLanguageAsync();
    Task PushMonitorSnapshotAsync(bool force = false);
    Task PushProcessCatalogAsync();
    Task PushNodePoolAsync();
    Task PushSettingsAsync();

    /// <summary>Persists dashboard "save_settings" payloads through the settings view-model.</summary>
    Task SaveSettingsAsync(JsonElement root);
    Task NotifyNodesOpAsync(string message);
}
