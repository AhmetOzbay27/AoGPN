using System.Runtime.InteropServices;

namespace ServiceLib.Services;

/// <summary>Bir işlemin kimliği + üst işlemi (süreç ağacı kurmak için).</summary>
public readonly record struct ProcessInfo(uint Pid, uint ParentPid, string Name);

/// <summary>
/// Süreç ağacı kaynağının son tarama durumu. FATAL "hedef kapalı" ile
/// karıştırılmamalıdır: ağaca hiç erişilemediğini, yani hiçbir kararın
/// güvenilir olmadığını söyler.
/// </summary>
public enum ProcessTreeStatus
{
    /// <summary>Toolhelp32 anlık görüntüsü başarılı — tam ağaç (parent PID'ler dahil).</summary>
    Healthy,

    /// <summary>Toolhelp32 başarısız; yedek (Process.GetProcesses) kullanıldı — parent bilgisi kayıp.</summary>
    Degraded,

    /// <summary>Süreç ağacına erişilemedi — hem Toolhelp32 hem yedek başarısız/boş.</summary>
    Fatal,
}

/// <summary>
/// Çalışan işlemlerin (PID, üst PID, exe adı) kaynağı. Tarayı rengi yok — saf
/// okuma; ideali Toolhelp32, başka platformda Process.GetProcesses() düşer.
/// </summary>
public interface IProcessTreeSource
{
    IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default);

    /// <summary>Son <see cref="Enumerate"/> çağrısının sonucu (varsayılan sağlıklı).</summary>
    ProcessTreeStatus Status => ProcessTreeStatus.Healthy;
}

/// <summary>
/// Windows Toolhelp32 (CreateToolhelp32Snapshot) tabanlı varsayılan kaynak —
/// üst PID'leri okur ki alt süreç (oyun→antı) ağacı türetilebilsin. Sistem
/// sınıflarına/izinlerine takılınca ucuz bir yedek kullanır.
///
/// Durum makinesi (son Enumerate çağrısına göre):
///   Toolhelp32 OK                        → <see cref="ProcessTreeStatus.Healthy"/>
///   Toolhelp32 FAIL + yedek dolu         → <see cref="ProcessTreeStatus.Degraded"/> (parent'sız)
///   Toolhelp32 FAIL + yedek FAIL/boş     → <see cref="ProcessTreeStatus.Fatal"/> (ağaç erişilemez)
/// </summary>
public class NativeProcessTreeSource : IProcessTreeSource
{
    private ProcessTreeStatus _status = ProcessTreeStatus.Healthy;

    public ProcessTreeStatus Status => _status;

    public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
    {
        if (TrySnapshotNative(out var entries))
        {
            _status = ProcessTreeStatus.Healthy;
            foreach (var e in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ProcessInfo(e.Pid, e.ParentPid, e.Name);
            }
            yield break;
        }

        // Yedek: parent bilinmez, yalnızca isim/PID (alt ağaç desteği kaybolur).
        List<ProcessInfo> fallback;
        try
        {
            fallback = CollectFallbackNative(cancellationToken).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Yedek bile çalışmıyor (izin/sistem kısıtı) — FATAL: ağaç erişilemez.
            _status = ProcessTreeStatus.Fatal;
            yield break;
        }

        if (fallback.Count == 0)
        {
            // Normal bir Windows makinesinde asla sıfır süreç olmaz → erişim kısıtlı.
            _status = ProcessTreeStatus.Fatal;
            yield break;
        }

        _status = ProcessTreeStatus.Degraded;
        foreach (var info in fallback)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return info;
        }
    }

    /// <summary>Toolhelp32 backend'i — testler sahte senaryo (başarısızlık/boş) için override eder.</summary>
    protected virtual bool TrySnapshotNative(out List<NativeEntry> entries)
        => Native.TrySnapshot(out entries);

    /// <summary>Yedek (parent'sız) backend — testler fırlatma/boşluk senaryoları için override eder.</summary>
    protected virtual IEnumerable<ProcessInfo> CollectFallbackNative(CancellationToken cancellationToken)
        => CollectFallback(cancellationToken);

    private static IEnumerable<ProcessInfo> CollectFallback(CancellationToken cancellationToken)
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name;
                try
                {
                    name = BuildExeName(p.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    continue; // süreç arada sonlandı
                }
                catch (ArgumentException)
                {
                    continue; // süreç arada sonlandı
                }
                yield return new ProcessInfo((uint)p.Id, 0, name);
            }
        }
    }

    private static string BuildExeName(string processName)
        => processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : processName + ".exe";

    // ── Toolhelp32 P/Invoke ──────────────────────────────────────────────

    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint DWSize;
        public uint CntUsage;
        public uint Th32ProcessID;
        public UIntPtr Th32DefaultHeapID;
        public uint Th32ModuleID;
        public uint CntThreads;
        public uint Th32ParentProcessID;
        public int PcPriClassBase;
        public uint DWFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }

    /// <summary>Toolhelp32 satırı (test seam'lerinin imza türü).</summary>
    public sealed record NativeEntry(uint Pid, uint ParentPid, string Name);

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        internal static bool TrySnapshot(out List<NativeEntry> entries)
        {
            entries = new List<NativeEntry>();
            var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return false;
            }

            try
            {
                var entry = new ProcessEntry32 { DWSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (!Process32FirstW(snapshot, ref entry))
                {
                    return false;
                }

                do
                {
                    if (entry.Th32ProcessID != 0)
                    {
                        entries.Add(new NativeEntry(entry.Th32ProcessID, entry.Th32ParentProcessID, entry.SzExeFile));
                    }
                }
                while (Process32NextW(snapshot, ref entry));
                return entries.Count > 0;
            }
            finally
            {
                _ = CloseHandle(snapshot);
            }
        }
    }
}