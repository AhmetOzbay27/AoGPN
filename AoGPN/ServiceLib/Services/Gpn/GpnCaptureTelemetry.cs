namespace ServiceLib.Services;

/// <summary>Tek akış (peer endpoint) için paket/byte sayacı.</summary>
public sealed record GpnFlowStat(string PeerEndpoint, string Protocol, long Packets, long Bytes, DateTimeOffset LastSeen);

/// <summary>Tek PID için paket/byte sayacı (port tablosuyla atfedilmiş).</summary>
public sealed record GpnPidStat(uint Pid, long Packets, long Bytes, bool InPool);

/// <summary>
/// GpnCaptureLoop'un per-packet telemetri anlık görüntüsü. Dashboard
/// <c>setGpnCaptureStats</c> ile bu kaydın JSON'unu alır; "PID havuzu küçükken
/// hangi paketler kaçıyor" teşhisi: ByPid satırları hangi süreçlerin aktif
/// trafik ürettiğini, InPool=false satırları havuz DIŞI (yakalanmayan) trafiği
/// gösterir — paket başına UDP port→PID köprüsüyle atfedilir.
/// </summary>
public sealed record GpnCaptureStatsSnapshot(
    long TotalPackets,
    long TotalBytes,
    long OutboundPackets,
    long InboundPackets,
    long UdpPackets,
    long TcpPackets,
    long IcmpPackets,
    long OtherPackets,
    long UnknownPackets,
    IReadOnlyList<GpnFlowStat> TopFlows,
    IReadOnlyList<GpnPidStat> ByPid,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt);

/// <summary>
/// Paket başına sayaçlar: skalerler Interlocked ile, akış/PID haritaları kilitli.
/// Oyun trafiği yüksek frekanslıdır — Record yolu yalnızca bir switch + iki
/// Interlocked.Increment + kilitli sözlük güncellemesi içerir (parse GpnPacketStats'ta).
/// </summary>
public sealed class GpnCaptureTelemetry
{
    public const int DefaultMaxFlows = 8;

    private readonly int _maxFlows;
    private readonly object _lock = new();
    private readonly Dictionary<string, FlowCount> _flows = new();
    private readonly Dictionary<uint, PidCount> _byPid = new();

    private long _totalPackets;
    private long _totalBytes;
    private long _outboundPackets;
    private long _inboundPackets;
    private long _udpPackets;
    private long _tcpPackets;
    private long _icmpPackets;
    private long _otherPackets;
    private long _unknownPackets;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastActivityAt;

    public GpnCaptureTelemetry(int maxFlows = DefaultMaxFlows)
    {
        _maxFlows = Math.Max(1, maxFlows);
        _startedAt = DateTimeOffset.UtcNow;
        _lastActivityAt = _startedAt;
    }

    public long TotalPackets => Volatile.Read(ref _totalPackets);

    /// <summary>
    /// Bir paketi kaydeder. <paramref name="pid"/> UDP port tablosundan atfedilmiş
    /// sahip PID (yoksa null — havuz dışı/çözülemedi), <paramref name="pidInPool"/>
    /// o PID'in güncel yakalama havuzunda olup olmadığı.
    /// </summary>
    public void Record(in GpnPacketStats stats, uint? pid, bool pidInPool)
    {
        var now = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _totalPackets);
        Interlocked.Add(ref _totalBytes, stats.Length);
        if (stats.Outbound)
        {
            Interlocked.Increment(ref _outboundPackets);
        }
        else
        {
            Interlocked.Increment(ref _inboundPackets);
        }
        switch (stats.Protocol)
        {
            case GpnPacketProtocol.Udp: Interlocked.Increment(ref _udpPackets); break;
            case GpnPacketProtocol.Tcp: Interlocked.Increment(ref _tcpPackets); break;
            case GpnPacketProtocol.Icmp: Interlocked.Increment(ref _icmpPackets); break;
            case GpnPacketProtocol.Other: Interlocked.Increment(ref _otherPackets); break;
            default: Interlocked.Increment(ref _unknownPackets); break;
        }

        lock (_lock)
        {
            if (stats.Protocol == GpnPacketProtocol.Udp || stats.Protocol == GpnPacketProtocol.Tcp)
            {
                var key = stats.Protocol + "|" + stats.PeerEndpoint;
                if (_flows.TryGetValue(key, out var flow))
                {
                    flow.Packets++;
                    flow.Bytes += stats.Length;
                    flow.LastSeen = now;
                }
                else
                {
                    _flows[key] = new FlowCount(stats.PeerEndpoint, stats.Protocol.ToString(), stats.Length, now);
                }
            }

            if (pid is { } owner)
            {
                if (_byPid.TryGetValue(owner, out var row))
                {
                    row.Packets++;
                    row.Bytes += stats.Length;
                    row.InPool = pidInPool; // havuz değişirse bayrağı güncelle
                }
                else
                {
                    _byPid[owner] = new PidCount(owner, stats.Length, pidInPool);
                }
            }

            _lastActivityAt = now;
        }
    }

    public GpnCaptureStatsSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                var flows = _flows.Values
                    .OrderByDescending(f => f.Packets)
                    .Take(_maxFlows)
                    .Select(f => new GpnFlowStat(f.PeerEndpoint, f.Protocol, f.Packets, f.Bytes, f.LastSeen))
                    .ToArray();
                var pids = _byPid.Values
                    .OrderByDescending(p => p.Packets)
                    .Select(p => new GpnPidStat(p.Pid, p.Packets, p.Bytes, p.InPool))
                    .ToArray();

                return new GpnCaptureStatsSnapshot(
                    TotalPackets: Volatile.Read(ref _totalPackets),
                    TotalBytes: Volatile.Read(ref _totalBytes),
                    OutboundPackets: Volatile.Read(ref _outboundPackets),
                    InboundPackets: Volatile.Read(ref _inboundPackets),
                    UdpPackets: Volatile.Read(ref _udpPackets),
                    TcpPackets: Volatile.Read(ref _tcpPackets),
                    IcmpPackets: Volatile.Read(ref _icmpPackets),
                    OtherPackets: Volatile.Read(ref _otherPackets),
                    UnknownPackets: Volatile.Read(ref _unknownPackets),
                    TopFlows: flows,
                    ByPid: pids,
                    StartedAt: _startedAt,
                    LastActivityAt: _lastActivityAt);
            }
        }
    }

    /// <summary>Sayaçları ve akış/PID haritalarını sıfırlar (oturum başlangıcı damgası yeniden kurulur).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _flows.Clear();
            _byPid.Clear();
            var now = DateTimeOffset.UtcNow;
            _startedAt = now;
            _lastActivityAt = now;
        }
        Interlocked.Exchange(ref _totalPackets, 0);
        Interlocked.Exchange(ref _totalBytes, 0);
        Interlocked.Exchange(ref _outboundPackets, 0);
        Interlocked.Exchange(ref _inboundPackets, 0);
        Interlocked.Exchange(ref _udpPackets, 0);
        Interlocked.Exchange(ref _tcpPackets, 0);
        Interlocked.Exchange(ref _icmpPackets, 0);
        Interlocked.Exchange(ref _otherPackets, 0);
        Interlocked.Exchange(ref _unknownPackets, 0);
    }

    private sealed class FlowCount
    {
        public FlowCount(string peerEndpoint, string protocol, int bytes, DateTimeOffset lastSeen)
        {
            PeerEndpoint = peerEndpoint;
            Protocol = protocol;
            Bytes = bytes;
            LastSeen = lastSeen;
            Packets = 1; // ilk kayıt bu girdiyi oluşturur — sayı 1'den başlar
        }

        public string PeerEndpoint { get; }
        public string Protocol { get; }
        public long Packets;
        public long Bytes;
        public DateTimeOffset LastSeen;
    }

    private sealed class PidCount
    {
        public PidCount(uint pid, int bytes, bool inPool)
        {
            Pid = pid;
            Bytes = bytes;
            InPool = inPool;
            Packets = 1; // ilk kayıt bu girdiyi oluşturur — sayı 1'den başlar
        }

        public uint Pid { get; }
        public long Packets;
        public long Bytes;
        public bool InPool;
    }
}
