using ServiceLib.Common;

namespace ServiceLib.Services;

/// <summary>
/// UDP yerel port → sahip PID köprüsü. GpnCaptureLoop, ağ katmanı paketlerinin
/// PID'ini port tablosuyla atfeder (WinDivert ağ katmanı paket başına PID vermez).
/// Varsayılan kaynak Windows IP Helper (GetExtendedUdpTable); testler sabit harita
/// enjekte eder.
/// </summary>
public interface IGpnPortPidTable
{
    /// <summary>Mevcut UDP yerel port → PID haritası (v4 + v6). Erişilemezse boş.</summary>
    IReadOnlyDictionary<ushort, uint> GetUdpPortOwners();
}

/// <summary><see cref="IGpnPortPidTable"/> varsayılan uygulaması (IP Helper).</summary>
public sealed class GpnPortPidTable : IGpnPortPidTable
{
    private readonly Func<IReadOnlyDictionary<ushort, uint>> _source;

    public GpnPortPidTable(Func<IReadOnlyDictionary<ushort, uint>>? source = null)
    {
        _source = source ?? WindowsNetworkTable.GetUdpPortOwners;
    }

    public IReadOnlyDictionary<ushort, uint> GetUdpPortOwners()
    {
        try
        {
            return _source();
        }
        catch
        {
            // Tablo erişilemez (platform/driver) — çağıran eski haritayı korur.
            return new Dictionary<ushort, uint>();
        }
    }
}
