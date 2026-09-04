namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnDecision — saf karar katmanı (P0 Faz 2: Karar Mekanizmasının Katmanlanması)
//
// GpnServerSelectionService'in "beyni": ağ ölçümü yapmayan, durum tutmayan,
// yalnızca girdi (probe sonuçları + politikalar) → çıktı (mod/sunucu kararı)
// dönüştüren saf fonksiyonlar. Burada hiçbir iş kuralı değişmez — Akıllı Düşüş
// (Smart Fallback), 15 ms salınım koruması (hysteresis) ve UDP/ICMP karar
// matrisi GpnServerSelectionService'ten BİREBİR taşınmıştır. Katman, karar
// mantığının ağ/ölçüm altyapısından bağımsız olarak birim test edilmesini
// sağlar; orkestratör (GpnServerSelectionService) yalnızca bu fonksiyonları
// ölçüm sonuçlarıyla besler.
// ─────────────────────────────────────────────────────────────────────────

internal static class GpnDecision
{
    /// <summary>Failover kararının eylemi.</summary>
    internal enum FailoverActionType
    {
        /// <summary>Değişiklik yok — aktif tünel sağlıklı, daha iyi aday yok.</summary>
        None,

        /// <summary>UDP'si sağlıklı başka bir sunucuya geç.</summary>
        SwitchServer,

        /// <summary>Hiçbir sunucuda sağlıklı UDP yok — WireGuard tüneli öldü, V2rayTCP'ye düş.</summary>
        FallbackToV2ray,
    }

    /// <summary>Failover kararının sonucu (saf, test edilebilir).</summary>
    internal sealed record FailoverDecision(FailoverActionType Action, GpnServerProfile? Target, string Reason);

    /// <summary>
    /// UDP sağlık testi sonucundan bağlantı moduna karar verir (saf — test edilebilir):
    ///  * Open       → WireGuardUDP
    ///  * Blocked    → V2rayTCP (ICMP Port Unreachable kanıtı — kesin)
    ///  * HandshakeNoResponse → V2rayTCP (varsayılan): sağlıklı sunucu geçerli el
    ///    sıkışmaya yanıt verir; sessizlik güçlü bozukluk işaretidir. Blocked'tan
    ///    farkı ICMP kanıtının OLMAMASI; yine de tünel kurulamayacağı için düşüş
    ///    güvenli taraftır. TreatHandshakeNoResponseAsBlocked=false ile WireGuardUDP
    ///    korunabilir (riskli senaryolar için).
    ///  * NoResponse → politika: varsayılan WireGuardUDP (WireGuard junk pakete
    ///    yanıt vermez, sessizlik sağlıklı yoldur); TreatNoResponseAsBlocked=true
    ///    ise V2rayTCP'ye düş.
    /// </summary>
    internal static ConnectionMode DecideMode(UdpProbeResult probe, UdpHealthCheckOptions? options = null)
    {
        options ??= new UdpHealthCheckOptions();
        return probe.Status switch
        {
            UdpProbeStatus.Open => ConnectionMode.WireGuardUDP,
            UdpProbeStatus.Blocked => ConnectionMode.V2rayTCP,
            UdpProbeStatus.HandshakeNoResponse =>
                options.TreatHandshakeNoResponseAsBlocked ? ConnectionMode.V2rayTCP : ConnectionMode.WireGuardUDP,
            _ => options.TreatNoResponseAsBlocked ? ConnectionMode.V2rayTCP : ConnectionMode.WireGuardUDP,
        };
    }

    /// <summary>
    /// Aday sıralaması (saf — test edilebilir): hairpin adaylar EN SONA (tünel-içi
    /// öz-erişim el sıkışma almaz), ardından başarılı pingler önce (düşük gecikme),
    /// başarısız pingler sonra. <see cref="GpnServerSelectionService.SelectBestServerAsync"/>
    /// ve dashboard <see cref="DecideSelection"/> bu fonksiyonu BİREBİR kullanır —
    /// tahmin ile gerçek seçim hairpin durumunda da çelişemez.
    /// </summary>
    internal static IEnumerable<GpnServerProfile> OrderCandidates(
        IEnumerable<GpnServerProfile> servers,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlySet<string> hairpinIds)
        => servers
            .OrderBy(s => hairpinIds.Contains(s.ServerId) ? 1 : 0)
            .ThenByDescending(s => pingResults.First(r => r.ServerId == s.ServerId).IsSuccess)
            .ThenBy(s => pingResults.First(r => r.ServerId == s.ServerId).DelayMs);

    /// <summary>
    /// Etkin bir NON-hairpin sunucu varken hairpin (kendi genel IP'si, öz-erişim)
    /// adaylar seçimden TAMAMEN elenmelidir. Hairpin sunucunun UDP ölçümü yanıltıcı
    /// şekilde "Open" görünebilir (tünel-içi rota) ama NAT hairpin desteklenmeyen
    /// yönlendiricilerde el sıkışma asla yanıt almaz — canlı gözlendi (makine İtalya
    /// hairpin'ine failover'da geçti, el sıkışma öldü, yeniden churn). Yalnızca TÜM
    /// etkin sunucular hairpin ise son çare olarak hairpin yine de seçilebilir.
    /// </summary>
    internal static bool HasNonHairpinAlternative(
        IReadOnlyList<GpnServerProfile> enabledServers,
        IReadOnlySet<string> hairpinIds)
        => hairpinIds.Count > 0 && enabledServers.Any(s => !hairpinIds.Contains(s.ServerId));

    /// <summary>
    /// Otomatik seçim kararının saf (ağ yok, test edilebilir) hali —
    /// <see cref="GpnServerSelectionService.SelectBestServerAsync"/>'in Adım 2-3'ü ile BİREBİR aynı mantık:
    ///
    ///  1. Ping adayları ping sırasına göre (başarılı ping önce, düşük gecikme
    ///     önce) taranır; ilk WireGuardUDP-uygun UDP sonucu (DecideMode) kazanır —
    ///     en düşük ping'li adayın UDP'si ölüyse ikinci aday denenir (Akıllı Düşüş),
    ///  2. Ping adayı yoksa (ICMP engelli) ilk UDP-açık (Open) sunucu seçilir,
    ///  3. Aday yok veya UDP sonucu yoksa → V2rayTCP (Best=null).
    ///
    /// Dashboard "en iyi aday" kartı bu fonksiyonu kullanır; karar mantığı
    /// çoğaltılmaz, tahmin her zaman Bağlan'ın kararıyla aynıdır.
    /// </summary>
    internal static GpnSelectionPrediction DecideSelection(
        IReadOnlyList<GpnServerProfile> servers,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null,
        IReadOnlySet<string>? hairpinServerIds = null)
    {
        options ??= new GpnProbeOptions();
        hairpinServerIds ??= new HashSet<string>();
        var enabled = servers.Where(s => s.IsEnabled).ToArray();
        if (enabled.Length == 0)
        {
            return new GpnSelectionPrediction(null, ConnectionMode.V2rayTCP, null, "aktif sunucu yok");
        }

        // Yalnızca etkin sunucular aday olabilir (gerçek akışta ölçüm zaten yalnızca
        // etkin sunuculara atılır — eski/uzaylı ping girdilerine karşı savunma).
        var enabledIds = enabled.Select(s => s.ServerId).ToHashSet();

        // Adım 2 (SelectBestServerAsync ile aynı): en düşük gecikmeli ping adayı.
        var bestPing = pingResults
            .Where(r => r.IsSuccess && enabledIds.Contains(r.ServerId))
            .OrderBy(r => r.DelayMs)
            .FirstOrDefault();

        GpnServerProfile? candidate = null;
        UdpProbeResult? udpProbe = null;
        // Hairpin (öz-erişim) politikası: etkin bir NON-hairpin sunucu varken hairpin
        // adaylar HİÇBİR koşulda seçilmez (UDP ölçümü yanıltıcı "Open" verebilir; NAT
        // hairpin desteklenmeyen yönlendiricide el sıkışma asla almaz). Yalnızca TÜM
        // sunucular hairpin ise son çare olarak seçilebilir.
        var skipHairpin = HasNonHairpinAlternative(enabled, hairpinServerIds);

        if (bestPing is not null)
        {
            // Akıllı Düşüş (SelectBestServerAsync BUGFIX'iyle birebir): en düşük
            // ping'li adayın UDP'si ölüyse (bloklu / handshake-no-response) ikinci
            // en iyi aday denenir; hairpin adaylar ise OrderCandidates ile EN SONA
            // alınır. İlk WireGuardUDP-uygun aday kazanır; yalnızca HİÇBİR aday uygun
            // değilse V2rayTCP düşüşü.
            var ordered = OrderCandidates(enabled, pingResults, hairpinServerIds);

            foreach (var s in ordered)
            {
                if (skipHairpin && hairpinServerIds.Contains(s.ServerId))
                {
                    continue; // non-hairpin alternatif varken hairpin'i atla
                }
                if (!udpResults.TryGetValue(s.ServerId, out var udp))
                {
                    continue; // bu adayın UDP sonucu yok — atla
                }
                // Yüksek-gecikme toleransı: bu adayın ping'i SlowServerToleranceMs'e eşit/üst
                // ise HandshakeNoResponse ölü sayılmaz (uzak/yoğun sunucuda kısa probe penceresi
                // yetersiz olabilir) — seçim yanlış V2rayTCP'ye düşmesin.
                var candidatePingMs = pingResults.FirstOrDefault(r => r.ServerId == s.ServerId)?.DelayMs ?? -1;
                var check = options.UdpCheck;
                if (options.SlowServerToleranceMs > 0
                    && check.TreatHandshakeNoResponseAsBlocked
                    && candidatePingMs >= options.SlowServerToleranceMs)
                {
                    check = check with { TreatHandshakeNoResponseAsBlocked = false };
                }
                if (DecideMode(udp, check) == ConnectionMode.WireGuardUDP)
                {
                    candidate = s;
                    udpProbe = udp;
                    break;
                }
            }
        }
        else
        {
            // ICMP yanıtı yok — pes etmeden önce ilk UDP-açık sunucu (Adım 2b):
            // ping sinyali yokken kesin kanıt (Open) gerekir. Non-hairpin alternatif
            // varken hairpin Open sunucu seçilmez.
            udpProbe = udpResults.Values.FirstOrDefault(
                r => r.Status == UdpProbeStatus.Open
                     && enabledIds.Contains(r.ServerId)
                     && !(skipHairpin && hairpinServerIds.Contains(r.ServerId)));
            if (udpProbe is not null)
            {
                candidate = enabled.First(s => s.ServerId == udpProbe.ServerId);
            }
        }

        if (candidate is null || udpProbe is null)
        {
            return new GpnSelectionPrediction(null, ConnectionMode.V2rayTCP, udpProbe,
                "UDP yolu doğrulanamadı — V2rayTCP düşüşü");
        }

        // Adım 3: UDP durumundan mod kararı — seçilen adayın ping'iyle tolerans uygulanır
        // (döngüdeki seçimle BİREBİR tutarlı; yoksa yüksek-ping HandshakeNoResponse aday
        // seçilip yine de V2rayTCP gösterilerek çelişirdi).
        var selectedPing = pingResults.FirstOrDefault(r => r.ServerId == candidate.ServerId)?.DelayMs ?? -1;
        var modeCheck = options.UdpCheck;
        if (options.SlowServerToleranceMs > 0
            && modeCheck.TreatHandshakeNoResponseAsBlocked
            && selectedPing >= options.SlowServerToleranceMs)
        {
            modeCheck = modeCheck with { TreatHandshakeNoResponseAsBlocked = false };
        }
        var mode = DecideMode(udpProbe, modeCheck);
        var pingMs = pingResults.First(r => r.ServerId == candidate.ServerId).DelayMs;
        var reason = mode == ConnectionMode.WireGuardUDP
            ? $"{candidate.Name} — ping {pingMs}ms, UDP {udpProbe.Status}"
            : $"{candidate.Name} — UDP {udpProbe.Status} → V2rayTCP düşüşü";
        return new GpnSelectionPrediction(candidate, mode, udpProbe, reason);
    }

    /// <summary>
    /// Failover karar mantığı (saf — ağ yok, test edilebilir):
    ///
    ///  * Aktif tünel sağlıklıysa (UDP Open, veya katı olmayan politikada NoResponse):
    ///    yalnızca aktiften SwitchHysteresisMs kadar daha iyi ping'li ve UDP'si sağlıklı
    ///    bir aday varsa SwitchServer. Salınım bu marjla engellenir.
    ///  * Aktif tünel öldüyse (UDP Blocked, veya katı politikada NoResponse):
    ///    önce UDP'si sağlıklı bir aday aranır → SwitchServer.
    ///  * Hiçbir adayda sağlıklı UDP yoksa → FallbackToV2ray (Tier 3).
    ///
    /// UDP sağlık testi kapalıysa (EnableUdpHealth=false) tüm sunucular "sağlıklı"
    /// sayılır ve davranış eski ping-tabanlı failover'a döner.
    /// </summary>
    internal static FailoverDecision DecideFailover(
        GpnServerProfile active,
        IReadOnlyList<GpnServerProfile> candidates,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions options,
        IReadOnlySet<string>? hairpinServerIds = null)
    {
        hairpinServerIds ??= new HashSet<string>();
        var strict = options.UdpCheck.TreatNoResponseAsBlocked;
        var strictHandshake = options.UdpCheck.TreatHandshakeNoResponseAsBlocked;
        var udpEnabled = options.EnableUdpHealth;
        var tolerance = options.SlowServerToleranceMs;

        bool IsUdpHealthy(GpnServerProfile server, int pingMs)
        {
            if (!udpEnabled || !udpResults.TryGetValue(server.ServerId, out var udp))
            {
                return true; // UDP devre dışı veya sonuç yok → varsayılan sağlıklı
            }
            if (IsUdpHealthyStatus(udp.Status, strict, strictHandshake))
            {
                return true;
            }
            // Yüksek-gecikme toleransı: ping'i toleranstan büyük/yeşıt olan bir sunucunun
            // HandshakeNoResponse'u 'ölü' sayılmaz — kısa probe penceresi uzak/yoğun bir
            // WireGuard sunucusu için yetersiz kalabilir (false-dead → gereksiz failover).
            if (udp.Status == UdpProbeStatus.HandshakeNoResponse
                && tolerance > 0 && pingMs >= tolerance)
            {
                return true;
            }
            return false;
        }

        var activeUdp = udpEnabled ? udpResults.GetValueOrDefault(active.ServerId) : null;
        var activePing = pingResults
            .FirstOrDefault(r => r.ServerId == active.ServerId && r.IsSuccess)?.DelayMs ?? int.MaxValue;
        var activeUdpDead = activeUdp is not null
            && IsUdpDeadStatus(activeUdp.Status, strict, strictHandshake)
            // Tolerans: aktif tünel yüksek ping'deyse HandshakeNoResponse ölü sayılmaz.
            && !(activeUdp.Status == UdpProbeStatus.HandshakeNoResponse
                 && tolerance > 0 && activePing != int.MaxValue && activePing >= tolerance);

        // Hairpin (öz-erişim, kendi genel IP'si) politikası: etkin bir non-hairpin
        // sunucu varken hairpin'ler geçiş hedefi OLAMAZ — UDP ölçümü yanıltıcı "Open"
        // dönebilir ama NAT hairpin desteklenmeyen yönlendiricide el sıkışma almaz
        // (canlı gözlenen düşüş kaynağı). Yalnızca TÜM sunucular hairpin ise son çare.
        var skipHairpin = HasNonHairpinAlternative(
            candidates.Where(c => c.IsEnabled).ToArray(), hairpinServerIds);

        // Switch adayları: aktif hariç, UDP'si sağlıklı (+ tolerans bilinçli), non-hairpin
        // varken hairpin değil. Ping başarısı olanlar önce, sonra düşük gecikmeye göre
        // sıralanır (ICMP engelli ama UDP sağlıklı adaylar da geçerli hedeftir).
        var switchTargets = candidates
            .Where(c => c.ServerId != active.ServerId
                        && !(skipHairpin && hairpinServerIds.Contains(c.ServerId)))
            .Select(c => (Server: c, Ping: pingResults.FirstOrDefault(r => r.ServerId == c.ServerId)?.DelayMs ?? -1))
            .Where(x => IsUdpHealthy(x.Server, x.Ping)
                        && (x.Ping >= 0 || (udpEnabled && udpResults.ContainsKey(x.Server.ServerId))))
            .OrderByDescending(x => x.Ping >= 0)
            .ThenBy(x => x.Ping)
            .ToList();

        if (!activeUdpDead)
        {
            if (activePing == int.MaxValue)
            {
                // Aktif tünel UDP sağlıklı ama ICMP ölçülemiyor (ISP engeli olabilir) —
                // ping tabanı yok, hysteresis karşılaştırması yapılamaz; geçiş salınımı
                // yaratmamak için dokunma.
                return new FailoverDecision(FailoverActionType.None, null, "aktif tünel sağlıklı, ping tabanı yok");
            }

            // Aktif tünel sağlıklı — yalnızca belirgin şekilde daha iyi adaya geç.
            var better = switchTargets
                .FirstOrDefault(x => x.Ping >= 0 && x.Ping < activePing - options.SwitchHysteresisMs);
            if (better.Server is not null)
            {
                return new FailoverDecision(FailoverActionType.SwitchServer, better.Server,
                    $"ping {activePing}→{better.Ping}ms (hysteresis {options.SwitchHysteresisMs}ms)");
            }
            return new FailoverDecision(FailoverActionType.None, null, "aktif tünel sağlıklı, daha iyi aday yok");
        }

        // Aktif tünel öldü — önce sağlıklı UDP'li başka sunucu ara.
        if (switchTargets.Count > 0)
        {
            var target = switchTargets.First().Server;
            return new FailoverDecision(FailoverActionType.SwitchServer, target,
                $"aktif UDP ölü ({activeUdp!.Status}) → {target.Name} (UDP sağlıklı)");
        }

        return new FailoverDecision(FailoverActionType.FallbackToV2ray, null,
            "hiçbir sunucuda sağlıklı UDP yok — tünel öldü");
    }

    /// <summary>
    /// V2rayTCP düşüşü sonrası WireGuard kurtarma kararı (saf — ağ yok, test edilebilir):
    /// tüm adayların UDP sağlığına bakar, sağlıklı (Open; katı olmayan politikada
    /// NoResponse da sağlıklı) olanlar arasından DÜŞÜK-ping'li adayı döndürür.
    /// Sağlıklı aday yoksa null. Seçilen aday Tier-2'ye dönüşün hedefidir.
    /// </summary>
    internal static GpnServerProfile? DecideRecovery(
        IReadOnlyList<GpnServerProfile> candidates,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions options,
        IReadOnlySet<string>? hairpinServerIds = null)
    {
        hairpinServerIds ??= new HashSet<string>();
        var strict = options.UdpCheck.TreatNoResponseAsBlocked;
        var strictHandshake = options.UdpCheck.TreatHandshakeNoResponseAsBlocked;
        var udpEnabled = options.EnableUdpHealth;
        var tolerance = options.SlowServerToleranceMs;

        bool IsUdpHealthy(GpnServerProfile server, int pingMs)
        {
            if (!udpEnabled || !udpResults.TryGetValue(server.ServerId, out var udp))
            {
                return true; // UDP devre dışı veya sonuç yok → varsayılan sağlıklı
            }
            if (IsUdpHealthyStatus(udp.Status, strict, strictHandshake))
            {
                return true;
            }
            // Yüksek-gecikme toleransı (DecideFailover ile aynı): yüksek ping'li bir
            // sunucunun HandshakeNoResponse'u yanlış 'ölü' sayılıp kurtarma kaçırılmaz.
            if (udp.Status == UdpProbeStatus.HandshakeNoResponse
                && tolerance > 0 && pingMs >= tolerance)
            {
                return true;
            }
            return false;
        }

        // Hairpin (öz-erişim) politikası: non-hairpin alternatif varken hairpin'e
        // Tier-2 kurtarması yapılmaz — el sıkışma almaz (NAT hairpin yoksa).
        var skipHairpin = HasNonHairpinAlternative(
            candidates.Where(c => c.IsEnabled).ToArray(), hairpinServerIds);

        var target = candidates
            .Where(c => c.IsEnabled
                        && !(skipHairpin && hairpinServerIds.Contains(c.ServerId)))
            .Select(c => (Server: c, Ping: pingResults.FirstOrDefault(r => r.ServerId == c.ServerId)?.DelayMs ?? -1))
            .Where(x => IsUdpHealthy(x.Server, x.Ping))
            .OrderByDescending(x => x.Ping >= 0)
            .ThenBy(x => x.Ping)
            .FirstOrDefault().Server;

        return target;
    }

    /// <summary>
    /// Ping-pong sıçrama koruması (saf — test edilebilir): bir sunucu değişiminden
    /// <c>cooldownSeconds</c> içinde, AZ ÖNCE TERK EDİLEN sunucuya (target == abandoned)
    /// geri dönmeyi engeller. TUN altındaki yanıltıcı UDP probe sonuçları iki sunucu
    /// arasında sürekli gidip-gelme yaratır (her geçiş sing-box restart + TCP kesimi =
    /// "bağlantı kendi kendini koparıyor"); bu koruma probe bir kez yanılırsa tünelin
    /// yerinde kalmasını sağlar. cooldown &lt;= 0 ise koruma kapalı (eski davranış).
    /// </summary>
    internal static bool IsPingPongSwitchSuppressed(
        string? abandonedServerId,
        string? targetServerId,
        DateTime lastSwitchUtc,
        DateTime now,
        int cooldownSeconds)
        => abandonedServerId is not null
           && targetServerId is not null
           && string.Equals(abandonedServerId, targetServerId, StringComparison.OrdinalIgnoreCase)
           && cooldownSeconds > 0
           && (now - lastSwitchUtc).TotalSeconds < cooldownSeconds;

    /// <summary>
    /// UDP durumunun "sağlıklı" sayılıp sayılmayacağı (failover/kurtarma adayı).
    /// Katı bayraklar: NoResponse (junk) ve HandshakeNoResponse için ayrı politikalar
    /// — varsayılan katı handshake politikası, el sıkışma yanıtsızlığını "ölü" sayar
    /// (sağlıklı sunucu geçerli el sıkışmaya yanıt verir), NoResponse ise WireGuard
    /// junk-sessizliği olduğundan sağlıklı kalır.
    /// </summary>
    internal static bool IsUdpHealthyStatus(UdpProbeStatus status, bool strictNoResponse, bool strictHandshake)
        => status == UdpProbeStatus.Open
           || (status == UdpProbeStatus.NoResponse && !strictNoResponse)
           || (status == UdpProbeStatus.HandshakeNoResponse && !strictHandshake);

    /// <summary>UDP durumunun "ölü" (tünel çalışmaz) sayılıp sayılmayacağı — <see cref="IsUdpHealthyStatus"/> tersi.</summary>
    internal static bool IsUdpDeadStatus(UdpProbeStatus status, bool strictNoResponse, bool strictHandshake)
        => status == UdpProbeStatus.Blocked
           || (status == UdpProbeStatus.NoResponse && strictNoResponse)
           || (status == UdpProbeStatus.HandshakeNoResponse && strictHandshake);

    /// <summary>Failover karar matrisi politikası eşlemesi (dashboards görselleştirmesi için).</summary>
    internal static GpnFailoverMatrixPolicy ToPolicy(GpnProbeOptions options, FailoverDecision decision)
        => new(
            options.UdpCheck.TreatNoResponseAsBlocked,
            options.UdpCheck.TreatHandshakeNoResponseAsBlocked,
            options.EnableUdpHealth,
            decision.Action switch
            {
                FailoverActionType.SwitchServer => GpnFailoverAction.SwitchServer,
                FailoverActionType.FallbackToV2ray => GpnFailoverAction.FallbackToV2ray,
                _ => GpnFailoverAction.None,
            },
            decision.Target?.ServerId,
            decision.Reason);
}