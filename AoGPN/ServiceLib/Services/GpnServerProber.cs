namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnServerProber — ağ test motoru (P0 Faz 2: Karar Mekanizmasının Katmanlanması)
//
// GpnServerSelectionService'in ölçüm katmanı: ICMP ping, TCP fallback ve
// UdpHealthChecker / WireGuard el sıkışma probe'larını koordine eder. Saf karar
// üretmez (GpnDecision'a bak), hairpin teşhisi yapmaz (GpnHairpinDetector'a bak);
// yalnızca ölçer ve GpnServerProbeResult / UdpProbeResult döndürür. Davranış
// GpnServerSelectionService'ten BİREBİR taşınmıştır — aynı örnekleme, aynı
// min/avg/max/kayıp hesabı, aynı ICMP→TCP→UDP-el-sıkışma fallback zinciri.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Tek sunucu ve tüm sunucular için ölçüm motoru (ICMP/TCP/UDP). DI uyumlu:
/// checker'lar dışarıdan enjekte edilir; testlerde sahte (fake) uygulamalar
/// verilebilir. Belirtilmezse gerçek uygulamalar kullanılır.
/// </summary>
internal sealed class GpnServerProber
{
    private const string Tag = "GpnSelect";

    private readonly IUdpHealthChecker _udpHealthChecker;
    private readonly IWireGuardHandshakeProbe _wireGuardProbe;

    /// <summary>
    /// Kısa ömürlü ölçüm önbelleği (Faz 2). Yalnızca
    /// <see cref="GpnProbeOptions.UseCache"/> açıkken okunur/yazılır — varsayılan
    /// yollarda davranış birebir eskisi gibidir.
    /// </summary>
    private readonly GpnProbeCache _cache;

    public GpnServerProber(
        IUdpHealthChecker? udpHealthChecker = null,
        IWireGuardHandshakeProbe? wireGuardProbe = null,
        GpnProbeCache? cache = null)
    {
        _udpHealthChecker = udpHealthChecker ?? new UdpHealthChecker();
        _wireGuardProbe = wireGuardProbe ?? new WireGuardHandshakeProbe();
        _cache = cache ?? new GpnProbeCache();
    }

    /// <summary>Önbellekteki geçerli ölçüm sayısı (tanı/dashboard).</summary>
    internal int CachedProbeCount => _cache.Count;

    /// <summary>Testlerin önbellek durumunu doğrudan incelemesi için.</summary>
    internal GpnProbeCache Cache => _cache;

    /// <summary>
    /// Tüm sunucuları tam istatistikle (min/avg/max/kayıp) ölç. Dashboard
    /// telemetrisi ve manuel "test et" butonu için.
    /// </summary>
    internal async Task<IReadOnlyList<GpnServerProbeResult>> ProbeAllAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GpnProbeOptions();

        // EscapeTunnelForProbes açıkken her ölçüm grubu taze tünel durumuyla başlar:
        // ⚡ Test manuel basıldığında veya failover çevriminde tünel yeni kurulmuş
        // olabilir (15 sn önbellek bayat kalır → tünel algılanmaz → ICMP atlanmaz,
        // UDP fiziksel NIC'e bağlanmaz). Tünel yoksa Refresh no-op'tur (normal ölçüm).
        if (options.EscapeTunnelForProbes)
        {
            ProbeEgressNic.Refresh();
        }

        var results = new GpnServerProbeResult[servers.Count];

        using var semaphore = new SemaphoreSlim(options.MaxConcurrency);
        async Task ProbeOneAsync(GpnServerProfile server, int index)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await ProbeServerAsync(server, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        await Task.WhenAll(servers.Select(ProbeOneAsync)).ConfigureAwait(false);
        options.Timeline?.Mark("icmp-end");
        return results;
    }

    /// <summary>
    /// Tüm sunucularda paralel UDP sağlık testi (el sıkışma + junk teşhis zinciri).
    /// Seçim/failover içinde kullanılır; dashboard ⚡ Test paneli de (MainWindow
    /// ProbeGpnServersAsync) aynı kanıtı göstermek için buradan beslenir.
    /// </summary>
    internal async Task<IReadOnlyList<UdpProbeResult>> ProbeUdpAllAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        var results = new UdpProbeResult[servers.Count];
        using var semaphore = new SemaphoreSlim(options.MaxConcurrency);
        async Task ProbeOneAsync(GpnServerProfile server, int index)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await ProbeUdpAsync(server, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        await Task.WhenAll(servers.Select(ProbeOneAsync)).ConfigureAwait(false);
        options.Timeline?.Mark("udp-end");
        return results;
    }

    /// <summary>
    /// Bir sunucunun UDP yolunu test eder. Gerçek WireGuard el sıkışması (kesin
    /// kanıt) etkinse ve anahtarlar mevcutsa onu kullanır; el sıkışma yanıtsızsa
    /// bloklama sinyalini doğrulamak için 1 baytlık junk probe'a düşer:
    ///
    ///  * El sıkışma Open      → kesin kanıt (sunucu anahtarları tanıdı ve yanıtladı)
    ///  * El sıkışma Blocked   → ICMP kanıtı, kesin
    ///  * El sıkışma HandshakeNoResponse → junk probe ile teşhis zinciri:
    ///      - junk Blocked → Blocked (ICMP kanıtı, el sıkışma da yanıtsızdı)
    ///      - junk Open    → Open (portta yanıtlayan bir hizmet var)
    ///      - junk NoResponse → HandshakeNoResponse (daha güçlü sinyal — sağlıklı
    ///        sunucu geçerli el sıkışmaya yanıt verirdi; ICMP kanıtı yok)
    ///  * El sıkışma kapalı/anahtar yok → yalnızca junk probe
    /// </summary>
    internal async Task<UdpProbeResult> ProbeUdpAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.UseCache)
        {
            return await ProbeUdpUncachedAsync(server, options, cancellationToken).ConfigureAwait(false);
        }

        var key = GpnProbeCache.BuildKey("udp", server.ServerId, options, ProbeEgressNic.IsTunnelActive());
        if (_cache.TryGet(key, out UdpProbeResult? cached) && cached is not null)
        {
            DiagLog.Write($"GPN_PROBE cache-hit udp server={server.ServerId} status={cached.Status}");
            return cached;
        }

        var measured = await ProbeUdpUncachedAsync(server, options, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, measured);
        return measured;
    }

    /// <summary>
    /// Ölçüm önbelleğini ısıtır (açılış ön yüklemesi / Faz 3). Bağlanma yolundaki
    /// AYNI iş paralel koşar, ama kullanıcı beklemediği bir anda; sonraki "Bağlan"
    /// ölçümü büyük ölçüde önbellekten karşılanır.
    /// </summary>
    internal async Task WarmCacheAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        if (servers.Count == 0)
        {
            return;
        }

        var warm = options with { UseCache = true };
        await Task.WhenAll(
            ProbeAllAsync(servers, warm, cancellationToken),
            ProbeUdpAllAsync(servers, warm, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Önbelleği baypas eden gerçek UDP ölçümü. "Uncached" son eki BİLİNÇLİDİR:
    /// çağıranlar ölçümün her zaman taze olduğunu varsayabilmelidir.
    /// </summary>
    private async Task<UdpProbeResult> ProbeUdpUncachedAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        UdpProbeResult? handshake = null;
        if (options.UseWireGuardHandshakeProbe)
        {
            handshake = await _wireGuardProbe.ProbeAsync(
                server.ServerId, server.EndpointHost, server.EndpointPort,
                server.ServerPublicKey, server.ClientPrivateKey,
                options.HandshakeProbe, cancellationToken).ConfigureAwait(false);

            // Kesin sonuç (Open: geçerli el sıkışma; Blocked: ICMP) → hemen dön.
            if (handshake.Status is UdpProbeStatus.Open or UdpProbeStatus.Blocked)
            {
                return handshake;
            }
        }

        // El sıkışma yanıtsız (veya handshake kapalı/anahtar yok) → junk probe ile
        // ICMP-bloklu sinyalini teyit et; teşhis zinciri aşağıda birleştirilir.
        var junk = await _udpHealthChecker.ProbeAsync(
            server.ServerId, server.EndpointHost, server.EndpointPort,
            options.UdpCheck, cancellationToken).ConfigureAwait(false);

        // Junk: kesin sinyal (ICMP bloklu veya yanıtlayan hizmet) — el sıkışma
        // yanıtsızlığını da not düşerek döndür.
        if (junk.Status is UdpProbeStatus.Blocked or UdpProbeStatus.Open)
        {
            var enrich = handshake is { Status: UdpProbeStatus.HandshakeNoResponse };
            return enrich
                ? junk with { Detail = $"{junk.Detail}; el sıkışma da yanıtsızdı (geçerli initiation)" }
                : junk;
        }

        // İkisi de sessiz → el sıkışma teşhisi daha güçlü (sağlıklı sunucu yanıt
        // verirdi; ICMP kanıtı yok — Blocked'tan ayrı tutulur).
        if (handshake is not null)
        {
            return handshake with { Detail = $"{handshake.Detail}; junk probe de NoResponse (ICMP kanıtı yok)" };
        }

        return junk; // handshake kapalı — yalnızca junk sonucu
    }

    /// <summary>Seçili modda tek sunucunun gidiş-dönüş süresini döndürür (ms; -1 = hata).</summary>
    internal async Task<int> ProbeDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        var result = await ProbeServerAsync(server, options, cancellationToken).ConfigureAwait(false);
        return result.DelayMs;
    }

    /// <summary>
    /// Tek sunucu için tam ölçüm: N örnek, min/avg/max, kayıp oranı.
    /// <see cref="GpnProbeOptions.UseCache"/> açıkken kısa ömürlü önbellek kullanılır;
    /// kapalıyken (varsayılan) davranış birebir eskisidir.
    /// </summary>
    internal async Task<GpnServerProbeResult> ProbeServerAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.UseCache)
        {
            return await ProbeServerUncachedAsync(server, options, cancellationToken).ConfigureAwait(false);
        }

        var key = GpnProbeCache.BuildKey("ping", server.ServerId, options, ProbeEgressNic.IsTunnelActive());
        if (_cache.TryGet(key, out GpnServerProbeResult? cached) && cached is not null)
        {
            DiagLog.Write($"GPN_PROBE cache-hit ping server={server.ServerId} delay={cached.DelayMs}ms loss={cached.LossPercent}%");
            return cached;
        }

        var measured = await ProbeServerUncachedAsync(server, options, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, measured);
        return measured;
    }

    /// <summary>Önbelleği baypas eden gerçek gecikme ölçümü.</summary>
    private async Task<GpnServerProbeResult> ProbeServerUncachedAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        // TUN etkinken (EscapeTunnelForProbes) ölçüm fiziksel NIC üzerinden yapılır
        // (ICMP atlanır, gecikme UDP el sıkışma RTT'si) — dashboard bu bayrağı
        // kullanıcıya "tünel-içi değil, fiziksel ölçüm" ipucu olarak gösterir.
        var measuredOverPhysicalNic = options.EscapeTunnelForProbes && ProbeEgressNic.IsTunnelActive();
        try
        {
            var samples = new List<int>(options.Samples);
            for (var i = 0; i < options.Samples; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var delay = options.Mode switch
                {
                    GpnProbeMode.Icmp => await IcmpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false),
                    GpnProbeMode.Tcp => await TcpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false),
                    _ => await AutoDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false),
                };

                // 0 ms (loopback / çok hızlı LAN) geçerli bir ölçümdür; yalnızca
                // -1 (ölçüm başarısız) örnek dışı bırakılır.
                if (delay >= 0)
                {
                    samples.Add(delay);
                }

                // Örnekler arası kısa ara — ICMP rate-limit'ine takılmayı azaltır.
                if (i < options.Samples - 1)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }

            if (samples.Count == 0)
            {
                return new GpnServerProbeResult(server.ServerId, -1, -1, -1, 100, false, null, measuredOverPhysicalNic);
            }

            var loss = (int)Math.Round((options.Samples - samples.Count) * 100.0 / options.Samples);
            return new GpnServerProbeResult(
                server.ServerId,
                samples.Min(),
                (int)samples.Average(),
                samples.Max(),
                loss,
                true,
                null,
                measuredOverPhysicalNic);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Probe hatası: {server.Name}: {ex.Message}");
            return new GpnServerProbeResult(server.ServerId, -1, -1, -1, 100, false, ex, measuredOverPhysicalNic);
        }
    }

    private async Task<int> AutoDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        var icmp = await IcmpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false);
        // 0 ms geçerli bir ICMP ölçümüdür (loopback); yalnızca -1 (başarısız)
        // fallback'i tetikler.
        if (icmp >= 0)
        {
            return icmp;
        }

        // TUN etkinken ICMP atlandı (Ping arayüz bağlayamaz) ve TCP anlamsızdır
        // (WireGuard 51820 UDP-only) — ping yerine fiziksel NIC üzerinden UDP el
        // sıkışma round-trip'i ölçülür: geçerli bir handshake initiation'a yanıt
        // alan sunucu gerçek gecikme verir (dashboard "—" yerine değer görür).
        // Tünel yoksa eski TCP fallback'i sürer.
        if (options.EscapeTunnelForProbes && ProbeEgressNic.IsTunnelActive())
        {
            return await UdpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false);
        }

        return await TcpDelayMsAsync(server, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ICMP yokken (TUN etkin — Ping arayüz bağlayamaz) gecikmeyi FİZİKSEL NIC
    /// üzerinden UDP el sıkışmasıyla ölçer. WireGuard sunucusu junk pakete yanıt
    /// vermediği için RTT yalnızca geçerli handshake initiation'a verilen yanıttan
    /// (handshake response / cookie reply) elde edilir; soket ProbeEgressNic ile
    /// fiziksel uplink'e bağlıdır — tünelin içine yakalanmaz, gerçek sunucu
    /// gecikmesi ölçülür. Anahtar yoksa veya yanıt yoksa -1 (ölçülemedi).
    /// </summary>
    private async Task<int> UdpDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server.ServerPublicKey) || string.IsNullOrWhiteSpace(server.ClientPrivateKey))
        {
            return -1;
        }

        try
        {
            var result = await _wireGuardProbe.ProbeAsync(
                server.ServerId, server.EndpointHost, server.EndpointPort,
                server.ServerPublicKey, server.ClientPrivateKey,
                new WireGuardHandshakeProbeOptions(
                    WaitTimeoutMs: Math.Max(300, options.PerSampleTimeoutMs),
                    MaxAttempts: 1),
                cancellationToken).ConfigureAwait(false);
            return result.Status == UdpProbeStatus.Open && result.RoundTripMs >= 0 ? result.RoundTripMs : -1;
        }
        catch (OperationCanceledException)
        {
            throw; // dış iptal — sonuç değil, yukarı taşınır
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] {server.Name} UDP gecikme ölçümü hatası: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// ICMP Echo isteği ile gidiş-dönüş ölçümü. DontFragment=true: WireGuard
    /// MTU 1420 planını yansıtan küçük paketle ölçüm yapılır (fragmantasyon
    /// gecikmesi ölçüme karışmaz).
    /// </summary>
    private static async Task<int> IcmpDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        // TUN etkinken ICMP tünele yakalanır; System.Net.NetworkInformation.Ping
        // arayüz bağlayamaz — fiziksel ölçüm imkânsız. EscapeTunnelForProbes açıksa
        // ICMP'yi atla; failover kararı UDP sağlık kanıtına (fiziksel NIC'ten) kalır.
        if (options.EscapeTunnelForProbes && ProbeEgressNic.IsTunnelActive())
        {
            return -1;
        }

        if (!IPAddress.TryParse(server.EndpointHost, out var address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            // 0.0.0.0/:: gibi belirsiz adresler Ping hedefi olamaz — SendPingAsync
            // ArgumentException fırlatır; ölçülemedi olarak işaretle (fırlatma yok).
            return -1;
        }

        try
        {
            using var ping = new Ping();
            var buffer = Encoding.ASCII.GetBytes("aogpn-gpn-probe-0123456789ab");
            var pingOptions = new PingOptions(64, true); // TTL 64, DontFragment
            var timeout = options.PerSampleTimeoutMs;

            var reply = await ping.SendPingAsync(
                address, timeout, buffer, pingOptions).ConfigureAwait(false);

            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch (OperationCanceledException)
        {
            throw; // dış iptal — sonuç değil, yukarı taşınır
        }
        catch
        {
            // TUN etkinken Ping arayüz bağlayamaz (NetworkInformationException:
            // "protokol yapılandırılmamış"), hedef IPv6'sız sistemde IPv6 olabilir
            // veya ağ yığını geçici olarak bozuktur. Hepsi "ölçülemedi" demektir
            // (-1 örnek, kayıp sayılır) — probe döngüsünü ve logları spam'lemek
            // yerine sessizce işaretle. Beklenmedik hatalar yine de
            // ProbeServerAsync'in çevresindeki tek seferlik yakalayıcıya düşer.
            return -1;
        }
    }

    /// <summary>
    /// TCP connect sondası: ICMP engellendiğinde devreye girer. Sunucunun
    /// 51820/udp portuna TCP connect başarılı olmayabilir (udp-only) — bu
    /// durumda -1 döner ve sunucu "erişilemez" sayılır; bu bir yanlış
    /// negatiftir, bu yüzden Auto modda yalnızca ICMP başarısız olduğunda
    /// denenecek son çaredir.
    /// </summary>
    private static async Task<int> TcpDelayMsAsync(
        GpnServerProfile server,
        GpnProbeOptions options,
        CancellationToken cancellationToken)
    {
        // TUN etkinken TCP probe de tünele yakalanır; fiziksel NIC'e bağlansa bile
        // WireGuard 51820/udp portunda TCP dinleyici YOKTUR — hızlı vazgeç (ölçüm
        // UDP sağlık kanıtına kalır).
        if (options.EscapeTunnelForProbes && ProbeEgressNic.IsTunnelActive())
        {
            return -1;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.PerSampleTimeoutMs);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            // TUN etkinse soketi fiziksel NIC'e bağla — ICMP gibi tünele yakalanmasın
            // (tünel yoksa no-op).
            ProbeEgressNic.BindSocketEgress(client.Client, AddressFamily.InterNetwork);
            await client.ConnectAsync(server.EndpointHost, server.EndpointPort, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }
}