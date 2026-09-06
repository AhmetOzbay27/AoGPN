using System.Runtime.InteropServices;

namespace ServiceLib.Services;

/// <summary>
/// Wintun session soyutlaması — WireGuardTunnelService'in test edilebilmesi için.
/// Gerçek uygulama (WintunSession) natif halka tamponla konuşur; testler kayıt
/// yapan (recording) sahte uygulamalar enjekte eder.
/// </summary>
public interface IWintunSession : IDisposable
{
    /// <summary>
    /// IP paketini session üzerinden adaptöre enjekte eder (tünel "inbound" yolu).
    /// Tampon dolu veya session kapanıyorsa false döner (ölümcül değil — köprü
    /// paketi atlar, sayaç tutar).
    /// </summary>
    bool InjectPacket(ReadOnlySpan<byte> packet);

    /// <summary>
    /// Session'dan bir paket alır (OS → adaptör → tünel "outbound" yolu).
    /// Tampon boşsa timeout kadar bekler; paket yoksa false döner.
    /// </summary>
    bool TryReceivePacket(out byte[] packet, int timeoutMs);

    /// <summary>Session açık mı (EndSession çağrılmadı mı).</summary>
    bool IsOpen { get; }
}

/// <summary>
/// Natif Wintun session sarmalayıcısı. AllocateSendPacket → kopyala → SendPacket
/// (gönderim sırası allocate sırasıyla belirlenir); ReceivePacket → kopyala →
/// ReleaseReceivePacket. ERROR_BUFFER_OVERFLOW (gönderim tamponu dolu) ve
/// ERROR_NO_MORE_ITEMS (alım tamponu boş) normal akış olarak yönetilir — paket
/// kaybı olmaz, yalnızca beklenir.
/// </summary>
public sealed class WintunSession : IWintunSession
{
    private readonly IntPtr _session;
    private readonly Action<IntPtr> _endSession;
    private bool _disposed;

    /// <summary>
    /// Yalnızca SESSION'ı sarar — adaptörün yaşam döngüsü WireGuardTunnelService'e
    /// aittir (onu o yaratır ve WintunCloseAdapter ile kapatır). Dispose yalnızca
    /// WintunEndSession çağırır; adaptör burada kapatılmaz. (Geçmişte burada da
    /// WintunCloseAdapter çağrılıyordu → servisin Close'u aynı adaptörü İKİNCİ kez
    /// kapatıyordu → çift-free → ntdll heap corruption 0xc0000374.)
    /// </summary>
    internal WintunSession(IntPtr session, Action<IntPtr>? endSession = null)
    {
        _session = session;
        _endSession = endSession ?? WintunNative.WintunEndSession;
    }

    public bool IsOpen => !_disposed;

    public bool InjectPacket(ReadOnlySpan<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.Length == 0 || packet.Length > WintunNative.MaxIpPacketSize)
        {
            return false;
        }

        var buffer = WintunNative.WintunAllocateSendPacket(_session, (uint)packet.Length);
        if (buffer == IntPtr.Zero)
        {
            return false; // ERROR_BUFFER_OVERFLOW — köprü sayaçta işler
        }

        unsafe
        {
            fixed (byte* src = packet)
            {
                Buffer.MemoryCopy(src, (void*)buffer, packet.Length, packet.Length);
            }
        }
        WintunNative.WintunSendPacket(_session, buffer);
        return true;
    }

    public bool TryReceivePacket(out byte[] packet, int timeoutMs)
    {
        packet = [];
        ObjectDisposedException.ThrowIf(_disposed, this);

        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            var ptr = WintunNative.WintunReceivePacket(_session, out var size);
            if (ptr != IntPtr.Zero)
            {
                var data = new byte[size];
                Marshal.Copy(ptr, data, 0, (int)size);
                WintunNative.WintunReleaseReceivePacket(_session, ptr);
                packet = data;
                return true;
            }

            var err = WintunNative.LastWin32Error;
            if (err == WintunNative.ErrorHandleEof)
            {
                return false; // adapter kapanıyor
            }
            if (err != WintunNative.ErrorNoMoreItems)
            {
                return false; // beklenmeyen hata — döngü sonlanır
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false; // zaman aşımı
            }
            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _endSession(_session);
    }
}
