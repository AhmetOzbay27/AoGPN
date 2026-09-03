using System.Runtime.InteropServices;

namespace ServiceLib.Services.Gpn;

// ─────────────────────────────────────────────────────────────────────────
// WinDivertDriverSupport — sürücü cihaz probe'u + SCM kurulum/başlatma
//
// WinDivert 2.2.2'nin kendi akışı (dll/windivert.c → WinDivertDriverInstall):
//   ilk WinDivertOpen() `\\.\WinDivert` cihazını açamazsa SCM üzerinden
//   "WinDivert" adında geçici kernel servisi oluşturur, WinDivert64.sys yolunu
//   WinDivert.dll'nin YANINDAN çözer ve servisi başlattıktan sonra siler
//   (transient — son handle kapanınca sürücü boşalır). Yönetici yetkisi şarttır.
//
// Burada aynı akış uygulamanın kendi sağlık kontrolü (WinDivertHealthMonitor)
// için yeniden üretilir, TEK farkla: kurulan servis silinmez — kararlı
// "sürücü kurulu" durumu SCM'de görünür kalır ve sonraki WinDivertOpen
// zaten başlatıp (kendi transient modelinde) silebilir.
//
// Probe (ProbeDevice) hiçbir yan etki içermez: `\\.\WinDivert` cihazını açar
// ve hemen kapatır — filter kurmaz, paket yakalamaz.
// ─────────────────────────────────────────────────────────────────────────
internal static partial class WinDivertDriverSupport
{
    internal const string DevicePath = @"\\.\WinDivert";
    internal const string ServiceName = "WinDivert";

    // CreateFileW (cihaz probe'u)
    private const uint FileAttributeNormal = 0x80;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint GenericReadWrite = 0xC0000000;

    // SCM
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceKernelDriver = 0x1;
    private const uint ServiceDemandStart = 0x3;
    private const uint ServiceErrorNormal = 0x1;

    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceAlreadyRunning = 1056;

    /// <summary>
    /// Sürücü cihazına erişilebilir mi? <c>true</c> ise sürücü çalışıyor demektir
    /// (servis adına güvenilmez — WinDivert servisi transient olabilir; cihaz
    /// tek gerçek kanıttır).
    /// </summary>
    internal static (bool Exists, int Error) ProbeDevice()
    {
        var handle = CreateFileW(
            DevicePath, GenericReadWrite, 0, IntPtr.Zero, OpenExisting,
            FileAttributeNormal | FileFlagOverlapped, IntPtr.Zero);
        if (handle == INVALID_HANDLE_VALUE)
        {
            return (false, Marshal.GetLastPInvokeError());
        }
        CloseHandle(handle);
        return (true, 0);
    }

    /// <summary>
    /// "WinDivert" kernel servisini oluşturur (yoksa) ve başlatır.
    /// <paramref name="sysFullPath"/> sürücü dosyasının TAM yolu olmalı
    /// (uygulama klasöründeki WinDivert64.sys). 0 başarı; aksi halde Win32 hatası
    /// (genellikle 5 = yönetici yetkisi yok).
    /// </summary>
    internal static int EnsureDriverStarted(string sysFullPath)
    {
        var manager = OpenSCManagerW(null, null, ScManagerAllAccess);
        if (manager == IntPtr.Zero)
        {
            return Marshal.GetLastPInvokeError();
        }
        try
        {
            var service = OpenServiceW(manager, ServiceName, ServiceAllAccess);
            var lastError = service != IntPtr.Zero ? 0 : Marshal.GetLastPInvokeError();
            if (service == IntPtr.Zero)
            {
                if (lastError != ErrorServiceDoesNotExist)
                {
                    return lastError;
                }

                service = CreateServiceW(
                    manager, ServiceName, ServiceName, ServiceAllAccess,
                    ServiceKernelDriver, ServiceDemandStart, ServiceErrorNormal,
                    sysFullPath, null, 0, null, null, null);
                if (service == IntPtr.Zero)
                {
                    lastError = Marshal.GetLastPInvokeError();
                    if (lastError == ErrorServiceExists)
                    {
                        // Başka bir süreç araya girip kurmuş — onu başlatmayı dene.
                        service = OpenServiceW(manager, ServiceName, ServiceAllAccess);
                        if (service == IntPtr.Zero)
                        {
                            return Marshal.GetLastPInvokeError();
                        }
                    }
                    else
                    {
                        return lastError;
                    }
                }
            }

            if (!StartServiceW(service, 0, null))
            {
                lastError = Marshal.GetLastPInvokeError();
                return lastError == ErrorServiceAlreadyRunning ? 0 : lastError;
            }
            return 0;
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    [LibraryImport("kernel32", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("advapi32", EntryPoint = "OpenSCManagerW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32", EntryPoint = "OpenServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32", EntryPoint = "CreateServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        uint dwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [LibraryImport("advapi32", EntryPoint = "StartServiceW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, string? lpServiceArgVectors);

    [LibraryImport("advapi32", EntryPoint = "CloseServiceHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr hSCObject);

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
}