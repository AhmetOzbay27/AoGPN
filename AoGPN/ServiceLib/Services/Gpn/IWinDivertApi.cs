namespace ServiceLib.Services;

/// <summary>
/// WinDivert.dll'nin soyutlaması. Gerçek uygulama (WinDivertApi) natif'le
/// konuşur; testler isteğe bağlı bir uygulama enjekte ederek önceden hazır
/// paket kanalını sürer veya sürücü yokken yaşam döngüsünü doğrular.
/// Tüm metodlar çağıran iş parçacığında (senkron, yalnızca çekirdek sınıflar
/// tarafından) çalıştırılır — WinDivert kuyruk semantiğini korumak için.
/// </summary>
public interface IWinDivertApi
{
    /// <summary>WinDivertOpen; başarısızlıkta IntPtr.Zero / -1 (INVALID_HANDLE_VALUE).</summary>
    IntPtr Open(string? filter, int layer, short priority, ulong flags);

    /// <summary>WinDivertOpenEx (v2.2+): krise parametreleri ([`WinDivertOpenParams`]) + flags.</summary>
    IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams);

    /// <summary>WinDivertRecv — paket tamponuna kopyalar.</summary>
    bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address);

    /// <summary>WinDivertSend — paketi seçilen arayüzden enjekte eder.</summary>
    bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address);

    bool Close(IntPtr handle);

    /// <summary>
    /// WinDivertSetParam — kuyruk parametresi uygular (QUEUE_LENGTH/QUEUE_TIME/
    /// QUEUE_SIZE). OpenEx export'u olmayan resmî 2.2.2 dağıtımında kuyruk
    /// ayarları bu yolla uygulanır. Başarı BOOL döner.
    /// </summary>
    bool SetParam(IntPtr handle, int param, ulong value);

    /// <summary>Son başarısız WinDivert çağrısının Win32 hata kodu (tanılama).</summary>
    int GetLastError();
}

/// <summary>Natif WinDivert.dll üzerinde çalışan varsayılan uygulama.</summary>
internal sealed class WinDivertApi : IWinDivertApi
{
    public IntPtr Open(string? filter, int layer, short priority, ulong flags)
        => WinDivertNative.WinDivertOpen(filter, layer, (short)priority, flags);

    public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams)
        => WinDivertNative.WinDivertOpenEx(filter, layer, (short)priority, flags, in openParams, (uint)WinDivertOpenParams.NativeSize);

    public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
        => WinDivertNative.WinDivertRecv(handle, packet, length, out recvLen, ref address);

    public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address)
        => WinDivertNative.WinDivertSend(handle, packet, length, out sendLen, ref address);

    public bool Close(IntPtr handle) => WinDivertNative.WinDivertClose(handle);

    public bool SetParam(IntPtr handle, int param, ulong value)
        => WinDivertNative.WinDivertSetParam(handle, param, value) != 0;

    public int GetLastError() => WinDivertNative.LastWin32Error;
}