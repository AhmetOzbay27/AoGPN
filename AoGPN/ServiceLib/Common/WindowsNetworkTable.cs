namespace ServiceLib.Common;

public record NetConnectionRow(
    int Pid,
    string Protocol,
    string LocalAddress,
    string RemoteAddress,
    string State);

/// <summary>
/// Reads the Windows TCP/UDP connection table (owner PID) without spawning external processes.
/// </summary>
public static class WindowsNetworkTable
{
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;

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
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        public uint State;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, uint ipVersion, int tblClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort, uint ipVersion, int tblClass, uint reserved);

    public static List<NetConnectionRow> GetActiveConnections()
    {
        var result = new List<NetConnectionRow>();
        try { GetTcp(result); } catch { }
        try { GetUdp(result); } catch { }
        try { GetTcp6(result); } catch { }
        try { GetUdp6(result); } catch { }
        return result;
    }

    /// <summary>
    /// UDP yerel portu → sahip süreç PID haritası (v4 + v6). GpnCaptureLoop'un
    /// per-PID telemetrisi, yakalanan paketin kaynak portunu bu tabloyla eşleştirir
    /// — WinDivert ağ katmanı paket başına PID vermez, port tablosu köprüsüdür.
    /// </summary>
    public static IReadOnlyDictionary<ushort, uint> GetUdpPortOwners()
    {
        var map = new Dictionary<ushort, uint>();
        try { CollectUdpOwners(map, AfInet, v6: false); } catch { /* tablo erişilemezse boş harita */ }
        try { CollectUdpOwners(map, AfInet6, v6: true); } catch { /* v6 tablosu opsiyonel */ }
        return map;
    }

    private static void CollectUdpOwners(Dictionary<ushort, uint> map, uint afInet, bool v6)
    {
        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, afInet, UdpTableOwnerPid, 0);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, afInet, UdpTableOwnerPid, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = v6 ? Marshal.SizeOf<MibUdp6RowOwnerPid>() : Marshal.SizeOf<MibUdpRowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            for (var i = 0; i < count; i++)
            {
                var port = v6
                    ? Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPtr).LocalPort
                    : Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPtr).LocalPort;
                var pid = v6
                    ? Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPtr).OwningPid
                    : Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPtr).OwningPid;
                if (pid > 0)
                {
                    map[(ushort)Port(port)] = pid; // aynı port birden çok satırda görünürse son kazanan geçerli
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void GetTcp(List<NetConnectionRow> result)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                if (row.OwningPid > 0)
                {
                    result.Add(new NetConnectionRow(
                        (int)row.OwningPid,
                        "TCP",
                        $"{IpToString(row.LocalAddr)}:{Port(row.LocalPort)}",
                        $"{IpToString(row.RemoteAddr)}:{Port(row.RemotePort)}",
                        TcpStateName(row.State)));
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void GetUdp(List<NetConnectionRow> result)
    {
        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, UdpTableOwnerPid, 0);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, AfInet, UdpTableOwnerPid, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPtr);
                if (row.OwningPid > 0)
                {
                    result.Add(new NetConnectionRow(
                        (int)row.OwningPid,
                        "UDP",
                        $"{IpToString(row.LocalAddr)}:{Port(row.LocalPort)}",
                        "*",
                        ""));
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void GetTcp6(List<NetConnectionRow> result)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet6, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet6, TcpTableOwnerPidAll, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                if (row.OwningPid > 0)
                {
                    result.Add(new NetConnectionRow(
                        (int)row.OwningPid,
                        "TCP",
                        $"[{Ip6ToString(row.LocalAddr, row.LocalScopeId)}]:{Port(row.LocalPort)}",
                        $"[{Ip6ToString(row.RemoteAddr, row.RemoteScopeId)}]:{Port(row.RemotePort)}",
                        TcpStateName(row.State)));
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void GetUdp6(List<NetConnectionRow> result)
    {
        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet6, UdpTableOwnerPid, 0);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, AfInet6, UdpTableOwnerPid, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
            var rowPtr = IntPtr.Add(buffer, sizeof(int));
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPtr);
                if (row.OwningPid > 0)
                {
                    result.Add(new NetConnectionRow(
                        (int)row.OwningPid,
                        "UDP",
                        $"[{Ip6ToString(row.LocalAddr, row.LocalScopeId)}]:{Port(row.LocalPort)}",
                        "*",
                        ""));
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string IpToString(uint addr)
    {
        var bytes = BitConverter.GetBytes(addr);
        Array.Reverse(bytes);
        return new IPAddress(bytes).ToString();
    }

    private static string Ip6ToString(byte[] addr, uint scopeId)
    {
        var ip = new IPAddress(addr);
        var text = ip.ToString();
        return scopeId == 0 ? text : $"{text}%{scopeId}";
    }

    private static int Port(uint port)
    {
        return ((int)(port & 0xFF) << 8) | (int)((port >> 8) & 0xFF);
    }

    private static string TcpStateName(uint state)
    {
        return state switch
        {
            1 => "Closed",
            2 => "Listen",
            3 => "SynSent",
            4 => "SynReceived",
            5 => "Established",
            6 => "FinWait1",
            7 => "FinWait2",
            8 => "CloseWait",
            9 => "Closing",
            10 => "LastAck",
            11 => "TimeWait",
            _ => "Unknown"
        };
    }
}
