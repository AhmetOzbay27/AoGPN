using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WebView2SweepProbe;

/// <summary>
/// Minimal native plumbing for reading process CPU times, parent PIDs and
/// command lines of the WebView2 child processes (msedgewebview2.exe tree)
/// spawned by this probe. Used to find and sample the GPU process directly.
/// </summary>
internal static class NativeProc
{
    private const uint ProcessQueryLimited = 0x1000;
    private const uint ProcessQuery = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInfoClass = 0;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(
        IntPtr hProcess, out long lpCreationTime, out long lpExitTime,
        out long lpKernelTime, out long lpUserTime);



    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, out long buffer, int size, out int bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] buffer, int size, out int bytesRead);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryInformationProcessBasic(
        IntPtr processHandle, int processInformationClass,
        out ProcessBasicInformation processInformation, int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryInformationProcessCommandLine(
        IntPtr processHandle, int processInformationClass,
        IntPtr processInformation, int processInformationLength,
        out int returnLength);

    /// <summary>Cumulative kernel+user CPU time in milliseconds, or -1 on failure.</summary>
    public static long GetCpuMilliseconds(IntPtr handle)
    {
        if (handle == IntPtr.Zero ||
            !GetProcessTimes(handle, out _, out _, out var kernel, out var user))
        {
            return -1;
        }

        return (kernel / 10_000) + (user / 10_000);
    }



    public static IntPtr OpenQueryHandle(int pid)
        => OpenProcess(ProcessQueryLimited | ProcessQuery, false, pid);

    public static void CloseHandleSafely(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    public static int? GetParentPid(int pid)
    {
        var handle = OpenQueryHandle(pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (NtQueryInformationProcessBasic(
                    handle, ProcessBasicInfoClass, out var pbi,
                    Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
            {
                return null;
            }

            var parent = pbi.InheritedFromUniqueProcessId.ToInt64();
            return parent is > 0 and <= int.MaxValue ? (int)parent : null;
        }
        finally
        {
            CloseHandleSafely(handle);
        }
    }

    public static string? GetCommandLine(int pid)
    {
        // NtQueryInformationProcess(ProcessCommandLineInformation) needs a
        // buffer big enough for the UNICODE_STRING header PLUS the string it
        // points into (passing just the header size fails with
        // STATUS_INFO_LENGTH_MISMATCH). The returned Buffer points into the
        // caller's buffer; older builds report it as a relative offset.
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            var handle = OpenQueryHandle(pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    if (NtQueryInformationProcessCommandLine(
                            handle, ProcessCommandLineInformation, buffer, 4096, out _) == 0)
                    {
                        var us = Marshal.PtrToStructure<UnicodeString>(buffer);
                        var baseAddr = buffer.ToInt64();
                        var raw = us.Buffer.ToInt64();

                        if (raw >= baseAddr && raw < baseAddr + 4096)
                        {
                            var len = Math.Min(us.Length, (int)(baseAddr + 4096 - raw));
                            return Marshal.PtrToStringUni(new IntPtr(raw), len / 2);
                        }

                        if (raw > 0 && raw < 4096)
                        {
                            var addr = baseAddr + raw;
                            var len = Math.Min(us.Length, (int)(baseAddr + 4096 - addr));
                            return Marshal.PtrToStringUni(new IntPtr(addr), len / 2);
                        }

                        if (raw != 0)
                        {
                            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                        }
                    }
                }
                finally
                {
                    CloseHandleSafely(handle);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // Fallback: read the command line out of the remote PEB directly
        // (x64 layout: PEB.ProcessParameters at +0x20, RTL_USER_PROCESS_
        // PARAMETERS.CommandLine UNICODE_STRING at +0x70).
        return IntPtr.Size == 8 ? ReadCommandLineViaPeb(pid) : null;
    }

    private static string? ReadCommandLineViaPeb(int pid)
    {
        var handle = OpenProcess(ProcessQuery | ProcessVmRead, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (NtQueryInformationProcessBasic(
                    handle, ProcessBasicInfoClass, out var pbi,
                    Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
            {
                return null;
            }

            var peb = pbi.PebBaseAddress.ToInt64();
            if (peb == 0 ||
                !ReadProcessMemory(handle, new IntPtr(peb + 0x20), out var paramsAddr, 8, out _) ||
                paramsAddr == 0 ||
                !ReadProcessMemory(handle, new IntPtr(paramsAddr + 0x70), out var cmdline, 16, out _))
            {
                return null;
            }

            var length = (int)(cmdline & 0xFFFF);
            var bufferPtr = (long)((ulong)cmdline >> 32);
            if (length <= 0 || length > 32768 || bufferPtr == 0)
            {
                return null;
            }

            var bytes = new byte[length];
            if (!ReadProcessMemory(handle, new IntPtr(bufferPtr), bytes, length, out _))
            {
                return null;
            }

            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CloseHandleSafely(handle);
        }
    }
}

/// <summary>The WebView2 child processes of one browser group, classified by --type.</summary>
internal sealed record BrowserProcessSet(
    int GpuPid,
    IReadOnlyList<int> RendererPids,
    IReadOnlyList<int> UtilityPids,
    IReadOnlyList<int> OtherPids)
{
    public bool Any => GpuPid > 0 || RendererPids.Count > 0 || UtilityPids.Count > 0 || OtherPids.Count > 0;
}

/// <summary>
/// Classifies the msedgewebview2.exe children of THIS probe's browser process
/// (CoreWebView2Environment.BrowserProcessId) by their --type command-line
/// switch. Never touches other WebView2 apps' process trees.
/// </summary>
internal static class GpuProcessLocator
{
    public static BrowserProcessSet Find(int browserProcessId, bool debug = false)
    {
        var renderers = new List<int>();
        var utilities = new List<int>();
        var others = new List<int>();
        var gpu = 0;

        if (browserProcessId <= 0)
        {
            return new BrowserProcessSet(0, renderers, utilities, others);
        }

        if (debug)
        {
            Console.WriteLine($"[locator] browser pid={browserProcessId}");
        }

        foreach (var process in System.Diagnostics.Process.GetProcessesByName("msedgewebview2"))
        {
            try
            {
                if (process.Id == browserProcessId)
                {
                    continue;
                }

                var parent = NativeProc.GetParentPid(process.Id);
                if (parent != browserProcessId)
                {
                    continue;
                }

                var commandLine = NativeProc.GetCommandLine(process.Id) ?? string.Empty;
                var kind = commandLine.Contains("--type=gpu-process", StringComparison.Ordinal) ? "gpu"
                    : commandLine.Contains("--type=renderer", StringComparison.Ordinal) ? "renderer"
                    : commandLine.Contains("--type=utility", StringComparison.Ordinal) ? "utility"
                    : "other";

                if (debug)
                {
                    Console.WriteLine(
                        $"[locator] pid={process.Id} kind={kind} cmd={Truncate(commandLine)}");
                }

                switch (kind)
                {
                    case "gpu" when gpu == 0:
                        gpu = process.Id;
                        break;
                    case "renderer":
                        renderers.Add(process.Id);
                        break;
                    case "utility":
                        utilities.Add(process.Id);
                        break;
                    default:
                        others.Add(process.Id);
                        break;
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return new BrowserProcessSet(gpu, renderers, utilities, others);
    }

    private static string Truncate(string commandLine)
        => commandLine.Length <= 120 ? commandLine : commandLine[..120] + "…";
}

/// <summary>
/// Incremental CPU sampler over a fixed process: every Sample() call reports
/// the CPU percentage of ONE logical core used since the previous call.
/// Uses GetProcessTimes cumulative CPU seconds (the same accounting Task
/// Manager shows). Windows updates the counters in ~15.6 ms ticks, so the
/// caller should sample at 1-2 s cadence, where the quantization averages
/// out to a fraction of a percent.
/// </summary>
internal sealed class CpuSampler : IDisposable
{
    private readonly IntPtr _handle;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _lastCpuMs = -1;
    private double _lastAt;

    private CpuSampler(IntPtr handle, int pid)
    {
        _handle = handle;
        Pid = pid;
    }

    public int Pid { get; }

    public static CpuSampler? Open(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        var handle = NativeProc.OpenQueryHandle(pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        return new CpuSampler(handle, pid);
    }

    /// <summary>Percent of one logical core since the previous call, or null before the first call / on failure.</summary>
    public double? Sample()
    {
        var cpuMs = NativeProc.GetCpuMilliseconds(_handle);
        if (cpuMs < 0)
        {
            return null;
        }

        var now = _stopwatch.Elapsed.TotalSeconds;
        if (_lastCpuMs < 0)
        {
            _lastCpuMs = cpuMs;
            _lastAt = now;
            return null;
        }

        var deltaMs = cpuMs - _lastCpuMs;
        var wallSeconds = now - _lastAt;
        _lastCpuMs = cpuMs;
        _lastAt = now;

        if (wallSeconds <= 0)
        {
            return null;
        }

        var percent = deltaMs / (wallSeconds * 1000.0) * 100.0;
        return percent >= 0 ? percent : 0;
    }

    public void Dispose() => NativeProc.CloseHandleSafely(_handle);
}

/// <summary>
/// Samples a group of processes together; Sample() returns the SUM of the
/// per-process percent-of-one-core readings (so "renderer total" is the sum
/// across all renderer processes).
/// </summary>
internal sealed class ProcessGroupSampler : IDisposable
{
    private readonly List<CpuSampler> _samplers = [];

    public static ProcessGroupSampler Open(IEnumerable<int> pids)
    {
        var group = new ProcessGroupSampler();
        foreach (var pid in pids)
        {
            if (CpuSampler.Open(pid) is { } sampler)
            {
                group._samplers.Add(sampler);
            }
        }

        return group;
    }

    public int Count => _samplers.Count;

    public double? Sample()
    {
        double? total = null;
        foreach (var sampler in _samplers)
        {
            if (sampler.Sample() is { } value)
            {
                total = (total ?? 0) + value;
            }
        }

        return total;
    }

    public void Dispose()
    {
        foreach (var sampler in _samplers)
        {
            sampler.Dispose();
        }
    }
}