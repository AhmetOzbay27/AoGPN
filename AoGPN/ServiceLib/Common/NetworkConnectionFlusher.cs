using System.Runtime.Versioning;

namespace ServiceLib.Common;

// ReSharper disable InconsistentNaming

/// <summary>
/// Forcefully terminates TCP connections belonging to specified process names.
/// Used to ensure target applications reconnect through the newly-started TUN interface
/// rather than leaking traffic over pre-existing sockets.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NetworkConnectionFlusher
{
    private const string LogModule = "SocketFlusher";
    // ── P/Invoke constants ───────────────────────────────────────────────
    private const int TcpTableOwnerPidAll = 5;
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;

    /// <summary>
    /// MIB_TCP_STATE_DELETE_TCB (12) — instructs the TCP/IP stack to
    /// close the connection immediately.
    /// </summary>
    private const uint DeleteTcb = 12;

    // ── Native structs ───────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        public uint State;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    // ── Native imports ───────────────────────────────────────────────────
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        uint ipVersion,
        int tblClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MibTcpRowOwnerPid pTcpRow);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MibTcp6RowOwnerPid pTcpRow);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int dwOutBufLen,
        bool sort,
        uint ipVersion,
        int tblClass,
        uint reserved);

    [DllImport("dnsapi.dll", SetLastError = true)]
    private static extern bool DnsFlushResolverCache();

    [DllImport("dnsapi.dll", SetLastError = true)]
    private static extern bool DnsFlushResolverCacheEntry_W(IntPtr entryName);

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses the <paramref name="routingItem"/> rule set, collects every
    /// process name that routes to a proxy outbound, and kills their
    /// established TCP connections so they reconnect through the TUN.
    /// </summary>
    /// <param name="routingItem">
    /// The active routing item containing process-based split-tunnel rules.
    /// </param>
    /// <returns>The total number of TCP connections that were killed.</returns>
    public static int FlushProxyProcessConnections(RoutingItem? routingItem)
    {
        if (routingItem?.RuleSet.IsNullOrEmpty() == true)
        {
            return 0;
        }

        List<RulesItem>? rules;
        try
        {
            rules = JsonUtils.Deserialize<List<RulesItem>>(routingItem.RuleSet);
        }
        catch
        {
            return 0;
        }

        if (rules is not { Count: > 0 })
        {
            return 0;
        }

        // Only kill connections for processes that route to a proxy
        // (not direct or block), since those are the ones that would
        // leak through pre-existing sockets.
        var proxyProcesses = new List<string>();
        var skippedDirect = 0;
        var skippedDisabled = 0;
        foreach (var rule in rules)
        {
            if (rule is not { Enabled: true })
            {
                skippedDisabled++;
                continue;
            }

            if (rule.Process is not { Count: > 0 })
            {
                continue;
            }

            var outbound = rule.OutboundTag ?? Global.ProxyTag;
            if (outbound is Global.DirectTag or Global.BlockTag)
            {
                skippedDirect += rule.Process.Count;
                continue;
            }

            proxyProcesses.AddRange(rule.Process);
        }

        Logging.SaveLog($"[SocketFlusher] Extracted {proxyProcesses.Count} proxy-process name(s) from routing: [{string.Join(", ", proxyProcesses.Distinct(StringComparer.OrdinalIgnoreCase).Take(20))}]"
            + (proxyProcesses.Count > 20 ? $" ... (+{proxyProcesses.Count - 20} more)" : "")
            + (skippedDirect > 0 ? $" | Skipped {skippedDirect} direct/block process(es)" : "")
            + (skippedDisabled > 0 ? $" | Skipped {skippedDisabled} disabled rule(s)" : ""));
        DiagLog.Write($"FLUSH extract: {proxyProcesses.Count} proxy processes (skippedDirect={skippedDirect}, skippedDisabled={skippedDisabled})");

        // Use FlushAll (TCP kill + UDP scan + DNS cache flush) for maximum
        // coverage against QUIC/UDP leaks that KillActiveConnectionsForProcesses
        // alone cannot close.
        return FlushAll(proxyProcesses);
    }

    /// <summary>
    /// Combined flush: kills TCP connections, scans UDP endpoints, and flushes
    /// the DNS resolver cache. Call this before the TUN starts to force every
    /// proxy-routed process to reconnect (and resolve DNS) through the TUN.
    /// </summary>
    public static int FlushAll(List<string> processNames)
    {
        var tcpKilled = KillActiveConnectionsForProcesses(processNames);
        var udpDetected = ScanUdpEndpoints(processNames);
        var dnsFlushed = false;
        try
        {
            dnsFlushed = DnsFlushResolverCache();
            Logging.SaveLog($"[SocketFlusher] DNS resolver cache flush: {(dnsFlushed ? "succeeded" : "failed")}.");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[SocketFlusher] DNS cache flush failed", ex);
        }

        Logging.SaveLog($"[SocketFlusher] FlushAll complete: tcpKilled={tcpKilled}, udpDetected={udpDetected}, dnsFlushed={dnsFlushed}.");
        DiagLog.Write($"FLUSH all: tcpKilled={tcpKilled}, udpDetected={udpDetected}, dnsFlushed={dnsFlushed}");
        return tcpKilled + udpDetected;
    }

    /// <summary>
    /// Enumerates all active TCP connections and forcefully closes every
    /// connection owned by any process whose executable name matches one
    /// of <paramref name="processNames"/> (case-insensitive comparison).
    /// </summary>
    /// <param name="processNames">
    /// Executable names such as "chrome.exe", "LeagueClient.exe".
    /// </param>
    /// <returns>The total number of TCP connections that were killed.</returns>
    public static int KillActiveConnectionsForProcesses(List<string> processNames)
    {
        if (processNames == null || processNames.Count == 0)
        {
            Logging.SaveLog("[SocketFlusher] No target process names supplied — nothing to flush.");
            return 0;
        }

        Logging.SaveLog($"[SocketFlusher] Resolving PIDs for {processNames.Count} target process name(s)...");
        var targetPids = ResolveTargetPids(processNames);
        if (targetPids.Count == 0)
        {
            Logging.SaveLog("[SocketFlusher] No running processes matched the target names — nothing to flush.");
            return 0;
        }

        Logging.SaveLog($"[SocketFlusher] Found {targetPids.Count} PID(s) to scan: [{string.Join(", ", targetPids.OrderBy(p => p))}]");

        var killed4 = 0;
        var killed6 = 0;
        try { killed4 = KillTcp4(targetPids); Logging.SaveLog($"[SocketFlusher] TCPv4 table: killed {killed4} connection(s)."); }
        catch (Exception ex) { Logging.SaveLog("[SocketFlusher] TCPv4 scan failed", ex); }
        try { killed6 = KillTcp6(targetPids); Logging.SaveLog($"[SocketFlusher] TCPv6 table: killed {killed6} connection(s)."); }
        catch (Exception ex) { Logging.SaveLog("[SocketFlusher] TCPv6 scan failed", ex); }

        var totalKilled = killed4 + killed6;
        Logging.SaveLog($"[SocketFlusher] Done. Total killed: {totalKilled} (v4={killed4}, v6={killed6}). Target PIDs: [{string.Join(", ", targetPids.OrderBy(p => p))}]");
        DiagLog.Write($"FLUSH tcpKilled={totalKilled} (v4={killed4}, v6={killed6})  PIDs=[{string.Join(",", targetPids.OrderBy(p => p))}]");
        return totalKilled;
    }

    // ── UDP endpoint scanning ──────────────────────────────────────────

    private static readonly HashSet<string> _udpWarningPids = [];

    /// <summary>
    /// Scans the UDP endpoint table for target processes and logs them.
    /// UDP sockets cannot be forcefully closed like TCP TCBs, but detecting
    /// them lets the caller decide whether QUIC fallback is needed.
    /// Returns the number of UDP endpoints found.
    /// </summary>
    private static int ScanUdpEndpoints(List<string> processNames)
    {
        var targetPids = ResolveTargetPids(processNames);
        if (targetPids.Count == 0)
        {
            return 0;
        }

        var detected = 0;
        try { detected += ScanUdp4(targetPids); } catch (Exception ex) { Logging.SaveLog("[SocketFlusher] UDPv4 scan failed", ex); }
        try { detected += ScanUdp6(targetPids); } catch (Exception ex) { Logging.SaveLog("[SocketFlusher] UDPv6 scan failed", ex); }
        return detected;
    }

    private static int ScanUdp4(HashSet<int> targetPids)
    {
        const int udpTableOwnerPid = 1;
        var detected = 0;
        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, udpTableOwnerPid, 0);
        if (size <= 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, AfInet, udpTableOwnerPid, 0) != 0)
            {
                return 0;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (!targetPids.Contains((int)row.OwningPid))
                {
                    continue;
                }

                Logging.SaveLog($"[SocketFlusher] UDPv4 endpoint: PID={row.OwningPid} localPort={row.LocalPort} — QUIC session may bypass TUN.");
                lock (_udpWarningPids)
                {
                    _udpWarningPids.Add(row.OwningPid.ToString());
                }
                detected++;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (detected > 0)
        {
            Logging.SaveLog($"[SocketFlusher] WARNING: {detected} UDPv4 endpoint(s) detected — these may leak real IP until DNS cache expires or QUIC times out.");
        }
        return detected;
    }

    private static int ScanUdp6(HashSet<int> targetPids)
    {
        const int udpTableOwnerPid = 1;
        const uint afInet6 = 23;
        var detected = 0;
        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, afInet6, udpTableOwnerPid, 0);
        if (size <= 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, afInet6, udpTableOwnerPid, 0) != 0)
            {
                return 0;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (!targetPids.Contains((int)row.OwningPid))
                {
                    continue;
                }

                Logging.SaveLog($"[SocketFlusher] UDPv6 endpoint: PID={row.OwningPid} localPort={row.LocalPort} — QUIC session may bypass TUN.");
                detected++;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (detected > 0)
        {
            Logging.SaveLog($"[SocketFlusher] WARNING: {detected} UDPv6 endpoint(s) detected.");
        }
        return detected;
    }

    // ── PID resolution ───────────────────────────────────────────────────

    private static HashSet<int> ResolveTargetPids(List<string> processNames)
    {
        var nameSet = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var pids = new HashSet<int>();

        try
        {
            var allProcs = Process.GetProcesses();
            Logging.SaveLog($"[SocketFlusher] Scanning {allProcs.Length} running process(es) for matches...");
            var matched = 0;
            var errors = 0;
            foreach (var proc in allProcs)
            {
                var procName = SafeGetProcessName(proc);
                if (procName == null)
                {
                    errors++;
                    continue;
                }
                if (nameSet.Contains(procName))
                {
                    pids.Add(proc.Id);
                    matched++;
                    Logging.Verbose(LogModule, "pid_resolved", ("proc", procName), ("pid", proc.Id));
                }
            }
            Logging.SaveLog($"[SocketFlusher] PID scan complete: {matched} match(es), {errors} error(s).");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[SocketFlusher] Process enumeration failed", ex);
        }

        return pids;
    }

    private static string? SafeGetProcessName(Process proc)
    {
        try { return proc.ProcessName + ".exe"; }
        catch { return null; }
    }

    // ── TCPv4 table walk + kill ──────────────────────────────────────────

    private static int KillTcp4(HashSet<int> targetPids)
    {
        var killed = 0;
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return 0;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (!targetPids.Contains((int)row.OwningPid))
                {
                    continue;
                }

                // Only kill connections that are actually established;
                // listening sockets are left alone.
                if (row.State is not (>= 5 and <= 11))
                {
                    continue;
                }

                var killRow = row;
                killRow.State = DeleteTcb;

                if (SetTcpEntry(ref killRow) == 0)
                {
                    killed++;
                }
                else
                {
                    // May fail with ERROR_ACCESS_DENIED for system-owned
                    // connections — that is expected and safe.
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return killed;
    }

    // ── TCPv6 table walk + kill ──────────────────────────────────────────

    private static int KillTcp6(HashSet<int> targetPids)
    {
        var killed = 0;
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet6, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet6, TcpTableOwnerPidAll, 0) != 0)
            {
                return 0;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                rowPtr = IntPtr.Add(rowPtr, rowSize);

                if (!targetPids.Contains((int)row.OwningPid))
                {
                    continue;
                }

                // Only kill established connections.
                if (row.State is not (>= 5 and <= 11))
                {
                    continue;
                }

                var killRow = row;
                killRow.State = DeleteTcb;

                if (SetTcpEntry(ref killRow) == 0)
                {
                    killed++;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return killed;
    }
}