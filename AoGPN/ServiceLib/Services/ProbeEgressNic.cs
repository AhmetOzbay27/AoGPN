namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// ProbeEgressNic — failover/ölçüm probe'larının TUN içine yakalanmasını önler
//
// sing-box TUN (auto_route + strict_route) altında tüm trafik — probe paketleri
// dahil — tünele çekilir: failover izleyicisinin aday sunuculara attığı UDP
// el sıkışma/junk paketleri kendi tünelinin İÇİNE girer, hedefe asla ulaşmaz ve
// yanıltıcı "HandshakeNoResponse" üretir → gereksiz sunucu değişimi / bağlantı
// kopması (canlı gözlenen "bağlantı kendi kendini koparıyor" A↔B döngüsü).
//
// Çözüm: tünel etkinken probe soketlerini FİZİKSEL uplink NIC'ine bağla —
//   • Windows: IP_UNICAST_IF (31)      — arayüz indeksi
//   • Linux  : SO_BINDTODEVICE (25)    — arayüz adı
//   • macOS  : IP_BOUND_IF (25) / IPV6_BOUND_IF (125) — arayüz indeksi
// Böylece paketler TUN'a girmeden doğrudan fiziksel ağdan hedefe gider ve ölçüm
// gerçek sunucu erişilebilirliğini gösterir. Tünel etkin DEĞİLSE bu işlem
// no-op'tur (paketler zaten fiziksel yoldan gider) — loopback testleri ve
// bağlantı-öncesi ölçümler etkilenmez.
//
// Ağ durumu kısa süre önbelleklenir (her probe'da NIC numaralandırması yapılmaz);
// <see cref="Refresh"/> ile tazeleme noktası failover izleyicisi başlangıcıdır
// (tünel genelde o anda zaten kuruludur — ilk çevrimden itibaren doğru ölçülür).
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Probe egress NIC yardımcısı: tünel etkinliğini algılar ve probe soketlerini
/// fiziksel uplink NIC'ine bağlar (TUN içine yakalanmayı önler).
/// </summary>
internal static class ProbeEgressNic
{
    private const string Tag = "ProbeEgress";
    private const int CacheSeconds = 15;

    /// <summary>
    /// Anlık görüntü önbelleğinin ömrü — TEK kaynak. Testler bu değeri okuyup
    /// iddialarını pencereye göre kurar; eskiden test, iki ardışık çağrının her
    /// koşulda bu pencere içinde kalacağını VARSAYIYORDU ve yüklü bir paralel
    /// koşuda pencere aşıldığında (ikinci çağrı haklı olarak taze numaralandırır)
    /// kararsız biçimde kırılıyordu.
    /// </summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(CacheSeconds);
    private static readonly object Sync = new();
    private static Snapshot? _cached;
    private static DateTime _cachedUtc;

    // Koordinatörün doğrudan ilettiği bilinen tünel adaptör adları (ör. GPN failover
    // izleyicisi başlarken "singbox_tun"). Ad sezgisel eşleşmesine (IsTunLikeName)
    // güvenmeden tünel durumunu deterministik kılar — ilk ölçüm bile doğru bilir.
    private static string[]? _knownTunnelNames;

    // Test dikişi: null değilse gerçek NIC numaralandırması yerine bu anlık görüntü
    // kullanılır (gerçek tünel olmadan "tünel etkin" senaryosu kurmak için).
    private static Snapshot? _forcedSnapshot;

    /// <summary>Bir ölçüm anındaki ağ durumu.</summary>
    internal sealed record Snapshot(
        bool TunnelActive,
        string? PhysicalName,
        int? PhysicalIpv4Index,
        int? PhysicalIpv6Index)
    {
        internal static Snapshot None { get; } = new(false, null, null, null);
    }

    /// <summary>Tünel (TUN/wintun) şu an etkin mi?</summary>
    internal static bool IsTunnelActive() => GetSnapshot().TunnelActive;

    /// <summary>Ağ durumu önbelleğini sıfırlar (sonraki sorgu taze numaralandırır).</summary>
    internal static void Refresh()
    {
        lock (Sync)
        {
            _cached = null;
            // IP_UNICAST_IF yeteneği de sıfırlanır: tek bir bozuk/geçersiz NIC
            // anlık görüntüsü (ör. yarı kurulu Wi-Fi adaptörü — "Adaptör okunamadı"
            // sonrası bayat indeks) yüzünden ilk pin denemesi WSAEADDRNOTAVAIL ile
            // düşüp yetenek "desteklenmiyor" işaretlenirse, oturum boyunca TÜM
            // probe'lar egress pinsiz kalır ve TUN içine yakalanır (canlı gözlenen:
            // tek hata sonrası sürekli "IP_UNICAST_IF desteklenmiyor" → el sıkışma
            // probe'ları hedefe asla ulaşmaz). Ağ durumu değişince yetenek yeniden
            // yoklanır — doğru fiziksel NIC'le pin başarılı olur.
            Volatile.Write(ref _unicastIfCapability, 0);
        }
    }

    /// <summary>
    /// Bilinen tünel adaptör adlarını teşhise iletir (null/boş = temizle). GPN
    /// koordinatörü failover izleyicisini başlatırken aktif TUN'un adını bu yolla
    /// doğrudan verir — tünel durumu ad sezgisel eşleşmesine güvenmeden, garantili
    /// bilinir. Önbellek geçersiz kılınır (sonraki sorgu yeni bilgiyle taze
    /// numaralandırır). Tünel kapandığında tekrar null verilerek sezgisel eşleşmeye
    /// dönülür.
    /// </summary>
    internal static void SetKnownTunnelNames(IEnumerable<string>? names)
    {
        lock (Sync)
        {
            _knownTunnelNames = names?
                .Select(n => n?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _cached = null;
        }
    }

    /// <summary>Bilinen tünel adaptör adları (test/teşhis için — null = sezgisel mod).</summary>
    internal static string[]? GetKnownTunnelNames()
    {
        lock (Sync)
        {
            return _knownTunnelNames;
        }
    }

    /// <summary>
    /// Test dikişi: anlık görüntüyü zorla (null = normal algılama). Gerçek NIC
    /// durumundan bağımsız "tünel etkin" senaryosu kurmak için kullanılır — örn.
    /// TUN etkinken ICMP yerine UDP el sıkışma gecikmesi fallback'inin testi.
    /// Üretim kodundan ÇAĞRILMAZ.
    /// </summary>
    internal static void SetSnapshotForTest(Snapshot? snapshot)
    {
        lock (Sync)
        {
            _forcedSnapshot = snapshot;
            _cached = null;
        }
    }

    /// <summary>
    /// Tünel etkinse soketi fiziksel NIC'e bağlar (egress pinleme); etkin değilse
    /// hiçbir şey yapmaz. Başarısızlık ölümcül değildir — loglanır ve normal
    /// davranış sürer (TUN kapalıyken probe zaten fiziksel yoldan gider).
    /// </summary>
    // Windows IP_UNICAST_IF yeteneği: bazı sistemlerde (sürücü yığını, sanal
    // NIC'ler, WinDivert etkileşimi) IPv4 indeksli pin WSAEADDRNOTAVAIL (10049)
    // üretir ve HER probe'da gürültülü hata basardı. İlk başarısız denemede
    // "desteklenmiyor" olarak işaretlenir ve sonraki pin denemeleri sessizce
    // atlanır — probe zaten pinsiz fiziksel yoldan gider (native modda tünel
    // default rotayı değiştirmez; yalnızca auto_route TUN'lar pin gerektirir).
    private static int _unicastIfCapability; // 0 = bilinmiyor, 1 = çalışıyor, -1 = desteklenmiyor

    /// <summary>Test dikişi: pinleme yeteneğini sıfırlar (yetenek yeniden yoklanır).</summary>
    internal static void ResetEgressCapabilityForTest()
    {
        Volatile.Write(ref _unicastIfCapability, 0);
    }

    internal static void BindSocketEgress(Socket socket, AddressFamily family)
    {
        var snapshot = GetSnapshot();
        if (!snapshot.TunnelActive)
        {
            return;
        }

        try
        {
            if (Utils.IsWindows())
            {
                // IP_UNICAST_IF (31) — Windows IPv4/IPv6 çıkış arayüzü pinleme.
                // Aile/indeks uyumu ZORUNLU: IPv6 sokete IPv4 indeksi uygulanırsa
                // WSAEINVAL, IPv4 sokete geçersiz indeks uygulanırsa WSAEADDRNOTAVAIL.
                // IPv6 kapalıyken (kullanıcı ayarı) fiziksel NIC'in IPv6 indeksi
                // YOKTUR — o zaman IPv4 indeksine düşmek yerine pin atlanır.
                if (Volatile.Read(ref _unicastIfCapability) < 0)
                {
                    return; // bu sistemde IP_UNICAST_IF desteklenmiyor — sessiz atla
                }
                var index = family == AddressFamily.InterNetworkV6
                    ? snapshot.PhysicalIpv6Index
                    : snapshot.PhysicalIpv4Index;
                if (index is not > 0)
                {
                    return;
                }
                var level = family == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
                try
                {
                    socket.SetSocketOption(level, (SocketOptionName)31, index.Value);
                    Volatile.Write(ref _unicastIfCapability, 1);
                }
                catch (Exception ex)
                {
                    // İlk hata = yetenek yoklaması: bu sistemde pin çalışmıyor.
                    // Sonraki denemeler sessizce atlanır (tek seferlik bilgi günlüğü).
                    if (Interlocked.Exchange(ref _unicastIfCapability, -1) == 0)
                    {
                        Logging.SaveLog($"[{Tag}] IP_UNICAST_IF bu sistemde desteklenmiyor — egress pinleme atlandı (probe fiziksel yoldan gider): {ex.Message}");
                    }
                }
                return;
            }
            if (Utils.IsLinux())
            {
                // SO_BINDTODEVICE (25) — arayüz adıyla egress bağlama (Linux).
                if (!string.IsNullOrEmpty(snapshot.PhysicalName))
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)25, snapshot.PhysicalName);
                }
                return;
            }
            if (Utils.IsMacOS())
            {
                // IP_BOUND_IF (25) / IPV6_BOUND_IF (125) — arayüz indeksi (macOS).
                // Not: macOS yönetilen API'si GetIPv4Properties().Index'i döndürmez;
                // indeks bulunamazsa no-op (macOS utun auto_route yakalaması Windows
                // kadar agresif değildir).
                var index = family == AddressFamily.InterNetworkV6
                    ? snapshot.PhysicalIpv6Index
                    : snapshot.PhysicalIpv4Index;
                if (index is not > 0)
                {
                    return;
                }
                var option = family == AddressFamily.InterNetworkV6 ? 125 : 25;
                var level = family == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
                socket.SetSocketOption(level, (SocketOptionName)option, index.Value);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Fiziksel NIC'e egress bağlama başarısız: {ex.Message}");
        }
    }

    internal static Snapshot GetSnapshot()
    {
        lock (Sync)
        {
            if (_forcedSnapshot is not null)
            {
                return _forcedSnapshot;
            }

            var now = DateTime.UtcNow;
            if (_cached is not null && now - _cachedUtc < CacheTtl)
            {
                return _cached;
            }

            Snapshot snapshot;
            try
            {
                snapshot = BuildSnapshot();
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"[{Tag}] Ağ arayüzü numaralandırma hatası: {ex.Message}");
                snapshot = Snapshot.None;
            }

            _cached = snapshot;
            _cachedUtc = now;
            return snapshot;
        }
    }

    /// <summary>
    /// Tek geçişte tünel etkinliği + fiziksel uplink NIC'i çözer.
    /// Tünel: Up durumda, tünel tipinde (NetworkInterfaceType.Tunnel — wintun/
    /// WireGuard adaptörleri) veya adı tünel-benzeri anahtar kelime içeren,
    /// loopback olmayan arayüz.
    /// Fiziksel: Ethernet/Wireless80211, tünel-benzeri ad YOK, APIPA-olmayan
    /// IPv4 ve gerçek (non-APIPA) IPv4 ağ geçidi — Utils.GetPhysicalDefaultInterface
    /// ile aynı seçim ölçütü. IPv4/IPv6 arayüz indeksleri de taşınır.
    /// </summary>
    private static Snapshot BuildSnapshot()
    {
        // Tek bir bozuk adaptörün (GetIPProperties/GetAllNetworkInterfaces bazı
        // Windows sürücülerinde fırlatabilir) TÜM teşhisi devre dışı bırakmasını
        // önle: adaptör bazında hata yutar, diğer adaptörler yine değerlendirilir.
        NetworkInterface[]? interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Arayüz listesi alınamadı: {ex.Message}");
            return Snapshot.None;
        }

        var tunnelActive = false;
        string? physicalName = null;
        int? ipv4Index = null;
        int? ipv6Index = null;

        foreach (var ni in interfaces)
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            try
            {
                var name = ni.Name ?? string.Empty;
                var isLoopback = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                // Koordinatörün ilettiği bilinen tünel adı — sezgisel eşleşmeden
                // bağımsız deterministik tespit (ör. "singbox_tun" yeniden adlandırılsa
                // veya sezgisel kalıba uymasa bile doğru tanınır).
                var isKnownTunnel = _knownTunnelNames is { } known
                    && known.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

                // ── Tünel etkinliği ──
                if (!isLoopback
                    && (isKnownTunnel || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel || IsTunLikeName(name)))
                {
                    tunnelActive = true;
                }

                // ── Fiziksel uplink adayı ──
                if (isLoopback
                    || ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                    || IsTunLikeName(name)
                    || isKnownTunnel)
                {
                    continue;
                }

                var props = ni.GetIPProperties();
                var hasIpv4 = props.UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(a.Address)
                    && !IsLinkLocalIpv4(a.Address));
                var hasRealGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocalIpv4(g.Address));
                if (!hasIpv4 || !hasRealGateway)
                {
                    continue;
                }

                physicalName = name;
                ipv4Index = props.GetIPv4Properties()?.Index;
                ipv6Index = props.GetIPv6Properties()?.Index;
                break;
            }
            catch (Exception ex)
            {
                // Bozuk adaptör — atla, diğerlerini değerlendir.
                Logging.SaveLog($"[{Tag}] Adaptör okunamadı ({ni.Name}): {ex.Message}");
            }
        }

        return new Snapshot(tunnelActive, physicalName, ipv4Index, ipv6Index);
    }

    /// <summary>
    /// Tünel-benzeri arayüz adları: wintun adaptörleri (singbox_tun, xray_tun,
    /// AoGPN-*), WireGuard, sanal/tap arayüzleri. Fiziksel NIC adları (Ethernet,
    /// Wi-Fi, satıcı modeli) bu anahtar kelimeleri içermez.
    /// </summary>
    internal static bool IsTunLikeName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("tun", StringComparison.Ordinal)
               || lower.Contains("wintun", StringComparison.Ordinal)
               || lower.Contains("wireguard", StringComparison.Ordinal)
               || lower.Contains("sing", StringComparison.Ordinal)
               || lower.Contains("xray", StringComparison.Ordinal)
               || lower.Contains("v2ray", StringComparison.Ordinal)
               || lower.Contains("mihomo", StringComparison.Ordinal)
               || lower.Contains("clash", StringComparison.Ordinal)
               || lower.Contains("tap", StringComparison.Ordinal)
               || lower.Contains("tailscale", StringComparison.Ordinal)
               || lower.Contains("zerotier", StringComparison.Ordinal)
               || lower.Contains("openvpn", StringComparison.Ordinal)
               || lower.Contains("nordvpn", StringComparison.Ordinal)
               || lower.Contains("protonvpn", StringComparison.Ordinal)
               || lower.Contains("windscribe", StringComparison.Ordinal)
               || lower.Contains("vmnet", StringComparison.Ordinal)
               || lower.Contains("vmware", StringComparison.Ordinal)
               || lower.Contains("virtual", StringComparison.Ordinal)
               || lower.Contains("aogpn", StringComparison.Ordinal);
    }

    private static bool IsLinkLocalIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
