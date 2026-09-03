namespace ServiceLib.Common;

/// <summary>
/// Keeps the WebView renderer contract explicit. The HTML may request only
/// operations implemented by the native host; unknown actions are ignored.
/// </summary>
public static class DashboardMessagePolicy
{
    private static readonly IReadOnlySet<string> AllowedActions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "toggle_connection",
            "gpn_connect",
            "gpn_servers_list",
            "gpn_servers_probe",
            "gpn_cluster_probe",
            "gpn_pid_pool_start",
            "gpn_pid_pool_stop",
            "gpn_pid_pool_refresh",
            "gpn_server_add",
            "gpn_server_delete",
            "gpn_server_toggle",
            "gpn_server_add_dialog",
            "gpn_server_edit_dialog",
            "gpn_defaults_status",
            "gpn_defaults_restore",
            "select_node",
            "copy_nodes",
            "paste_nodes",
            "delete_nodes",
            "test_nodes",
            "stop_test",
            "disable_nodes",
            "restore_nodes",
            "cleanup_failed",
            "dedup_nodes",
            "get_node_pool",
            "add_node_pool_link",
            "edit_node_pool_link",
            "remove_node_pool_link",
            "fetch_node_pool",
            "toggle_node_fav",
            "set_connection_mode",
            "set_transport",
            "set_protocol_preference",
            "set_tun_stack",
            "set_auto_reconnect",
            "set_gpn_recovery_watch",
            "set_gpn_failover",
            "get_vless_bypass_node",
            "set_vless_bypass_node",
            "set_vless_bypass_from_uri",
            "get_gpn_capture_settings",
            "set_gpn_capture_settings",
            "get_gpn_wintun_settings",
            "set_gpn_wintun_settings",
            "reset_gpn_telemetry",
            "get_gpn_telemetry",
            "get_gpn_resilience_log",
            "clear_gpn_resilience_log",
            "set_active_view",
            "set_window_behavior",
            "request_monitor_snapshot",
            "list_running_processes",
            "refresh_monitor",
            "set_app_route",
            "add_app",
            "add_running_process",
            "remove_app",
            "add_files",
            "add_domain_route",
            "move_route",
            "set_auto_game_connect",
            "set_split_mode",
            "set_split_direction",
            "test_route",
            "set_verbose_log",
            "set_reduce_effects",
            "set_effects_tier",
            "set_language",
            "set_theme",
            "set_system_proxy_mode",
            "toggle_system_proxy",
            "test_proxy",
            "performance_sample",
            "check_ip",
            "app_control",
            "get_settings",
            "save_settings",
        };

    public static bool IsAllowedAction(string? action) =>
        !string.IsNullOrWhiteSpace(action)
        && action.Length <= 64
        && AllowedActions.Contains(action);

    public static IReadOnlyCollection<string> GetAllowedActions() =>
        AllowedActions.ToArray();
}
