using ServiceLib.Services.CoreConfig.Mihomo;
using ServiceLib.Services.Gpn;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnCoreLauncher — IGpnConnectionLauncher'ın gerçek uygulaması
//
// Koordinatörün mod kararını gerçek çekirdek yaşam döngüsüne köprüler:
//
//   WireGuardUDP → GpnServerProfile'ı ProfileItem'a çevirir (EConfigType.WireGuard,
//                  ECoreType.mihomo). mihomo TUN + PROCESS-NAME süreç kuralları +
//                  WARP SOCKS zinciri (dialer-proxy), sing-box'ın bu makinede
//                  el sıkışamayan WG endpoint'ine karşı canlı doğrulandı (Ağu 2026,
//                  A/B/C/DNS fazları). Faz 1 pipeline'ı: CoreConfigContextBuilder
//                  BuildAll → CoreEngineHost.StartAsync (mihomo own TUN + auto-route).
//                  Not: yabancı tüneller (ör. resmi WG 'İtalya') artık kapatılmaz —
//                  yalnızca tespit edilip DiagLog'a raporlanır (karar kullanıcınındır;
//                  bkz. docs/foreign-vpn-kill-removal-roadmap.md). WinDivert yakalama
//                  köprüsü (GpnCaptureBridge) mihomo yolunda KULLANILMAZ: süreç
//                  ayırımını zaten mihomo yapar.
//
//   V2rayTCP     → ConfigHandler.GetDefaultServer ile uygulamanın mevcut
//                  varsayılan V2ray düğümünü seçer ve aynı pipeline'dan başlatır.
//
// Config ve CoreEngineHost, AppManager'ın mevcut örneklerinden alınır (uygulama
// tek CoreEngineHost kullanır); testlerde ikisi de dışarıdan verilebilir.
// GpnCaptureBridge isteğe bağlıdır — verilmezse yalnızca çekirdek yaşam döngüsü
// yürütülür (koordinatör testleri bu yolu kullanır).
// ─────────────────────────────────────────────────────────────────────────

public sealed class GpnCoreLauncher : IGpnConnectionLauncher
{
    private const string Tag = "GpnLaunch";
    private readonly Config _config;
    private readonly Func<bool, string, Task> _update;
    private readonly CoreEngineHost _engineHost;
    private readonly GpnCaptureBridge? _captureBridge;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GpnCoreLauncher(
        Config? config = null,
        Func<bool, string, Task>? updateFunc = null,
        CoreEngineHost? engineHost = null,
        GpnCaptureBridge? captureBridge = null)
    {
        _config = config ?? AppManager.Instance.Config;
        _update = updateFunc ?? DefaultUpdateAsync;
        // Uygulama zaten tek CoreEngineHost tutar; varsa onu yeniden kullan,
        // yoksa oluştur ve AppManager'a ata (MainWindowViewModel'in deseninin aynısı).
        var host = engineHost ?? AppManager.Instance.CoreEngineHost;
        if (host is null)
        {
            host = new CoreEngineHost(_config, _update);
            AppManager.Instance.CoreEngineHost = host;
        }
        _engineHost = host;
        _captureBridge = captureBridge;
    }

    public async Task LaunchAsync(ConnectionMode mode, GpnServerProfile? server, CancellationToken ct,
        IReadOnlyList<GpnServerProfile>? gpnCandidates = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var node = mode switch
            {
                ConnectionMode.WireGuardUDP when server is not null => BuildWireGuardProfile(server),
                ConnectionMode.V2rayTCP => await PickDefaultV2rayNodeAsync(ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Geçersiz bağlantı modu/parametre: {mode}, server={(server is null ? "null" : server.Name)}"),
            };

            // GPN tüneli için MTU iyileştirmesi (canlı ölçüm, Ağu 2026): yol ~1400
            // bayta kadar DF paketi taşıyor; 1420 MTU'lu dış paketler parçalanıyor ve
            // kayıplı hatta çift parça kaybı yaşanıyor. Kullanıcının KAYITLI TUN MTU
            // ayarına dokunulmaz (kalıcı tercih korunur); etkin değer YAML üretiminde
            // (CoreConfigHandler.ResolveGpnMtu) 1360'a kırpılır ve TUN + WG outbound
            // aynı değeri kullanır. Burada yalnızca teşhis: kayıtlı değer yol sınırını
            // aşıyorsa uygulanacak kırpma GPN_MTU satırıyla diyagnoz beslemesine düşer.
            if (mode == ConnectionMode.WireGuardUDP
                && _config.TunModeItem.Mtu > Global.GpnRecommendedMtu)
            {
                DiagLog.Write($"GPN_MTU TUN {_config.TunModeItem.Mtu} → {Global.GpnRecommendedMtu} (yol sınırı ~1400; parçalanmayı önle)");
            }

            var allResult = await CoreConfigContextBuilder.BuildAll(_config, node).ConfigureAwait(false);
            if (!allResult.Success)
            {
                var detail = allResult.CombinedValidatorResult.Errors.FirstOrDefault()
                    ?? allResult.CombinedValidatorResult.Warnings.FirstOrDefault()
                    ?? "config oluşturulamadı";
                throw new InvalidOperationException($"Düğüm doğrulaması başarısız: {detail}");
            }

            var runCore = allResult.MainResult.Context.RunCoreType;
            DiagLog.Write($"GPN_LAUNCH mode={mode} node={node.Remarks} ({node.ConfigType}) core={runCore}");

            // Çoklu-düğüm + kesintisiz rota (superset): WireGuard bağlantısında
            // koordinatör aday listesini ve GPN rota politikasını context'e taşır.
            // Politika doluysa config tüm girişleri sabit kural satırı + ao-<i> seçim
            // grubu, yakalayıcıyı GPN-MODE grubuna yazar → düğüm DEĞİŞİMİ (PUT
            // /proxies/GPN-Nodes) ve mod/rota/yön değişiklikleri (PUT GPN-MODE +
            // ao-<i>) çekirdek/TUN durdurulmadan yapılabilir (make-before-break).
            // Aday/politika yoksa config legacy biçiminde üretilir (davranış değişmez).
            var mainContext = allResult.MainResult.Context;
            if (mode == ConnectionMode.WireGuardUDP)
            {
                // Tier 1 — The Live Flip: native motor aktivasyon anahtarı. Açıkken
                // bağlam UseNativeGpnEngine taşır → CoreManager (strateji fabrikası)
                // yerel in-process motoru (WinDivert + WireGuard + Wintun) seçer;
                // kapalıyken (varsayılan — mihomo canlı doğrulanmıştır) mevcut yolu
                // birebir korur. Açılış tek satır: NativeGpnEnginePolicy.IsEnabled = true
                // (kompozisyon kökü; sürücülü ortamda doğrulandıktan sonra).
                if (NativeGpnEnginePolicy.IsEnabled)
                {
                    mainContext = mainContext with { UseNativeGpnEngine = true };
                    DiagLog.Write("GPN_LAUNCH native engine policy ENABLED — in-process engine selected");
                }

                var softContext = mainContext with
                {
                    GpnSoftPolicy = GpnSoftRouting.BuildPolicy(_config),
                };
                if (gpnCandidates is { Count: > 1 })
                {
                    softContext = softContext with { GpnCandidates = gpnCandidates };
                }
                // Çift Bağlantı (Bölünmüş Tünelleme): KÜRESEL launcher-bypass düğümü
                // (GuiItem.VlessBypassNodeJson — tek VLESS/Reality ayarı, hangi WG
                // düğümü seçilirse seçilsin aynıdır) context'e taşınır. Üretici
                // (CoreConfigHandler → GpnMihomoConfigService) YAML'e ikincil
                // "vless-launcher" outbound'unu ekler. (GpnSoftPolicy ile aynı desen:
                // config nesnesi çekirdeğe context üzerinden gider.)
                var bypassJson = _config.GuiItem.VlessBypassNodeJson;
                if (bypassJson.IsNotEmpty())
                {
                    var bypass = JsonUtils.Deserialize<VlessProfileItem>(bypassJson);
                    if (bypass is not null)
                    {
                        softContext = softContext with { GpnVlessBypass = bypass };
                    }
                }
                // Per-app WARP egress düğümleri: politika girişlerinin WarpNodeId
                // değerleri düğüm listesinden (ProfileItem) bağlantı anında çözülür
                // ve context'e taşınır — üretici bu düğümler için ayrı egress
                // outbound'ları üretir (Ayarlar → GPN VLESS bypass düğümünün yerini
                // alan yeni akış; çözülemeyen satırlar varsayılan WARP egress'e düşer).
                var warpNodes = await ResolveWarpNodesAsync(softContext.GpnSoftPolicy, ct).ConfigureAwait(false);
                if (warpNodes.Count > 0)
                {
                    softContext = softContext with { GpnWarpNodes = warpNodes };
                }
                mainContext = softContext;
            }

            // mihomo TUN açılmadan önce yabancı VPN durumunu yalnızca RAPORLA —
            // üçüncü taraf istemciler asla kapatılmaz (karar kullanıcınındır).
            // Resmi WireGuard istemcisinin (İtalya) WFP filtreleri + rekabet eden
            // default rotası mihomo TUN TCP taşımasını ezebilir; uyarı DiagLog'a
            // düşer (best-effort: tarama hatası bağlantıyı engellemez).
            if (runCore == ECoreType.mihomo)
            {
                DetectForeignTunnelsBeforeStart();
            }

            // Wintun hayalet adaptör süpürmesi — tünel açılmadan HEMEN ÖNCE:
            // önceki (ölü) oturumlardan kalan adaptörler aynı adla yeni adaptör
            // yaratmayı engelleyebilir ve "Yerel Ağ Bağlantısı N" birikmesine yol
            // açar. Tek örnekli uygulama + koordinatörün Stop→Launch sırası (bkz.
            // GpnConnectionCoordinator: her Launch'tan önce StopAsync koşar)
            // garantisi: burada ön ekimize uyan HER adaptör ölü bir oturumdan
            // kalmadır, güvenle silinebilir. Best-effort — başarısızlık bağlantıyı
            // engellemez (startup süpürmesiyle aynı sözleşme).
            if (mode == ConnectionMode.WireGuardUDP)
            {
                try
                {
                    var sweep = await WintunOrphanSweeper.SweepOrphanedAsync(_config.GpnWintunItem, ct)
                        .ConfigureAwait(false);
                    if (sweep.Removed > 0 || sweep.Skipped > 0 || sweep.AbortReason is not null)
                    {
                        DiagLog.Write($"GPN_LAUNCH pre-start wintun sweep removed={sweep.Removed} "
                            + $"skipped={sweep.Skipped} abort={sweep.AbortReason ?? "-"}");
                    }
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("AoGPN GPN pre-launch Wintun sweep failed", ex);
                }
            }

            await _engineHost.StartAsync(mainContext, allResult.PreSocksResult?.Context, ct).ConfigureAwait(false);

            // MTU doğrulaması (best-effort, bağlantıyı asla engellemez): çekirdek
            // başladıktan sonra mihomo TUN adaptörünün GERÇEK MTU'sunu oku ve
            // beklenen değerle karşılaştır. Sonuç GPN_MTU verify satırıyla diyagnoz
            // beslemesine düşer — ayarın gerçekten uygulandığı gözle doğrulanabilir
            // (Wintun adaptörü istenen değeri almazsa MISMATCH uyarısı görünür).
            if (mode == ConnectionMode.WireGuardUDP && runCore == ECoreType.mihomo)
            {
                var expectedMtu = CoreConfigHandler.ResolveGpnMtu(_config);
                _ = Task.Run(() => VerifyTunMtuAsync(expectedMtu, ct), ct);
            }

            // Faz 2b/2c — yalnızca eski sing-box yolunda: yakalama tünel köprüsünü
            // canlıya al (WinDivert recv-only → WireGuardTunnelService → Wintun
            // inject). mihomo yolunda köprü KULLANILMAZ — süreç bazlı ayırımı
            // (BsGLauncher.exe → warp-socks, oyun → wg-<id>) mihomo kendi TUN'unda
            // yapar; ikinci bir Wintun adaptörü + WinDivert filtresi çakışırdı.
            if (mode == ConnectionMode.WireGuardUDP && runCore != ECoreType.mihomo && _captureBridge is not null)
            {
                var bridgeStarted = await _captureBridge.StartAsync(server, ct).ConfigureAwait(false);
                DiagLog.Write($"GPN_LAUNCH capture-bridge={(bridgeStarted ? "live" : "skipped")}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// mihomo TUN adaptörünün (adı <see cref="Global.MihomoTunInterfaceName"/>) gerçek
    /// MTU'sunu okur ve beklenen değerle karşılaştırır. Adaptör çekirdek başladıktan
    /// sonra görünür — kısa bir süre yoklanır; sonuç DiagLog'a yazılır. Yalnızca
    /// teşhistir: uyuşmazlık bağlantıyı etkilemez, GPN_MTU verify MISMATCH satırıyla
    /// dashboard diyagnoz beslemesinde görünür.
    /// </summary>
    private static async Task VerifyTunMtuAsync(int expectedMtu, CancellationToken ct)
    {
        try
        {
            // Adaptör yeni yaratıldığında mihomo MTU'yu hemen uygulamayabilir;
            // ilk okuma bayat olabilir (canlı gözlenen: 65535 → 1280 yarışı).
            // Yanlış MISMATCH alarmı vermemek için karar, 500 ms arayla 2 KARARLI
            // okuma ister; uyuşmayan değer başlangıç penceresi (3 sn) boyunca
            // yeniden okunmaya devam eder (yoklama sürerken adaptör ayarlanabilir).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            var started = DateTime.UtcNow;
            var lastMtu = -1;
            var stableReads = 0;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var adapter = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(ni =>
                            string.Equals(ni.Name, Global.MihomoTunInterfaceName, StringComparison.OrdinalIgnoreCase)
                            && ni.OperationalStatus == OperationalStatus.Up);
                    if (adapter is not null)
                    {
                        var mtu = adapter.GetIPProperties().GetIPv4Properties()?.Mtu ?? 0;
                        if (mtu == lastMtu)
                        {
                            stableReads++;
                        }
                        else
                        {
                            lastMtu = mtu;
                            stableReads = 1;
                        }
                        var elapsed = DateTime.UtcNow - started;
                        var mismatchSettled = stableReads >= 2 && elapsed >= TimeSpan.FromSeconds(3);
                        if (stableReads >= 2 && (mtu == expectedMtu || mismatchSettled))
                        {
                            if (mtu == expectedMtu)
                            {
                                DiagLog.Write($"GPN_MTU verify adapter={adapter.Name} mtu={mtu} expected={expectedMtu} OK");
                            }
                            else
                            {
                                DiagLog.Write($"GPN_MTU verify adapter={adapter.Name} mtu={mtu} expected={expectedMtu} MISMATCH (2 stable reads)");
                                // İyileştirici adım (Faz 5): uyuşmazlık KARARLIYSA ve
                                // değer Wintun'un "hiç uygulanmadı" varsayılanıysa
                                // (65535) adaptöre beklenen MTU'yu biz yazıyoruz.
                                // Sebep: Wintun adaptörü istenen MTU'yu almazsa Windows
                                // tünele 65535 bayta kadar paket verir; WG dış paketleri
                                // parçalanır ve kayıplı hatta çift parça kaybı (oyunda
                                // jitter/kayıp) yaşanır. 65535 bir geçiş değeri değil,
                                // "uygulanmadı" demektir — bu yüzden mihomo'nun kendi
                                // değeriyle yarışmayız. Best-effort: başarısızlık
                                // bağlantıyı asla etkilemez.
                                if (mtu is 65535 or 0)
                                {
                                    await TryApplyTunMtuAsync(adapter.Name, expectedMtu, ct).ConfigureAwait(false);
                                }
                            }
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // GetIPv4Properties bazı platformlarda fırlatabilir — yoklama sürer.
                    DiagLog.Write($"GPN_MTU verify read failed (retry): {ex.Message}");
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            DiagLog.Write($"GPN_MTU verify timeout — {Global.MihomoTunInterfaceName} adaptörü 8 sn içinde bulunamadı");
        }
        catch (OperationCanceledException)
        {
            // Bağlantı kesildi — doğrulama yarıda bırakılır (normal).
        }
    }

    /// <summary>
    /// TUN adaptörüne beklenen MTU'yu yazar (yalnızca doğrulanmış uyuşmazlıkta, yani
    /// adaptör değeri "hiç uygulanmadı" varsayılanında kaldığında çağrılır).
    ///
    /// `netsh` kullanılır: Wintun adaptörünün MTU'su Win32 IP yardımcı API'siyle
    /// ayarlanır ve `netsh interface ipv4 set subinterface` tam olarak bunu yapar;
    /// kendi P/Invoke sarmalayıcımızı yazmak yüzeyi büyütürdü. Komut yönetici
    /// gerektirir — GPN TUN yolu zaten yönetici ister. Best-effort: hata yutulur ve
    /// günlüğe yazılır, bağlantı etkilenmez.
    /// </summary>
    private static async Task TryApplyTunMtuAsync(string adapterName, int mtu, CancellationToken ct)
    {
        try
        {
            var arguments = $"interface ipv4 set subinterface \"{adapterName}\" mtu={mtu} store=active";
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                DiagLog.Write($"GPN_MTU apply skipped — netsh başlatılamadı ({adapterName} → {mtu})");
                return;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                DiagLog.Write($"GPN_MTU apply adapter={adapterName} → {mtu} OK (netsh)");
            }
            else
            {
                var error = (await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                DiagLog.Write($"GPN_MTU apply adapter={adapterName} → {mtu} FAILED exit={process.ExitCode} {error}");
            }
        }
        catch (OperationCanceledException)
        {
            // Bağlantı kesildi — düzeltme yarıda bırakılır (normal).
        }
        catch (Exception ex)
        {
            DiagLog.Write($"GPN_MTU apply adapter={adapterName} → {mtu} hata: {ex.Message}");
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Önce yakalama tünel köprüsünü durdur (idempotent — çalışmıyorsa no-op),
            // ardından çekirdeği. Sıra önemli: köprü hâlâ paket çekerken core TUN'u
            // kapatılmaz.
            if (_captureBridge is not null)
            {
                await _captureBridge.StopAsync().ConfigureAwait(false);
            }
            await _engineHost.StopAsync(ct).ConfigureAwait(false);
            // Çekirdek durdu: superset oturum parmak izini temizle — bir sonraki
            // başlatma (varsa) config üretimi sırasında yeniden Begin ile dolacak.
            GpnSoftSession.End();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Politika girişlerinin per-app WARP düğümlerini (ProfileItem.IndexId) bağlantı
    /// anında çözer: WireGuard profilleri <see cref="GpnServerProfile"/>'a eşlenir
    /// (mihomo'da ayrı wg-&lt;id&gt; tüneli olur), desteklenen diğer protokoller
    /// (VLESS/VMess/SS/Trojan/Hysteria2/TUIC/SOCKS/HTTP — Global.MihomoSupportConfigType)
    /// ProfileItem olarak taşınır (üretici kendi adında proxy outbound'una çevirir).
    /// Silinmiş, devre dışı ya da desteklenmeyen tipler atlanır — ilgili satır
    /// varsayılan WARP egress'e düşer ve kural geçersiz outbound'a işaret etmez.
    /// internal: RoutingDriftHealthCheck aynı çözücüyü kullanır — beklenen superset
    /// config'in kural sırası canlı config'le birebir aynı üretilsin.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, WarpNodeProfile>> ResolveWarpNodesAsync(
        GpnSoftRoutingPolicy? policy,
        CancellationToken ct)
    {
        var result = new Dictionary<string, WarpNodeProfile>(StringComparer.Ordinal);
        if (policy is null)
        {
            return result;
        }
        foreach (var id in policy.Entries
                     .Select(e => e.WarpNodeId)
                     .Where(i => i.IsNotEmpty())
                     .Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            ProfileItem? profile;
            try
            {
                profile = await AppManager.Instance.GetProfileItem(id!).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN per-app warp node resolve failed", ex);
                continue;
            }
            if (profile is null)
            {
                continue;
            }
            if (profile.ConfigType == EConfigType.WireGuard)
            {
                if (WireGuardServerCatalog.TryMap(profile, out var wg))
                {
                    result[id!] = new WarpNodeProfile(
                        id!, profile.Remarks ?? profile.IndexId, null, wg);
                }
            }
            else if (Global.MihomoSupportConfigType.Contains(profile.ConfigType))
            {
                result[id!] = new WarpNodeProfile(
                    id!, profile.Remarks ?? profile.IndexId, profile, null);
            }
        }
        return result;
    }

    /// <summary>
    /// GpnServerProfile'ı uygulamanın WireGuard ProfileItem'ına çevirir.
    /// GPN routing (process_name kuralları) WireGuard'ı mihomo çekirdeğiyle üretir
    /// (mihomo TUN + PROCESS-NAME kuralları + WARP SOCKS zinciri — sing-box'ın WG
    /// endpoint'i IPv6'sız makinede el sıkışamıyor; mihomo ilk denemede kanıtlandı).
    /// CoreConfigContextBuilder bu CoreType'ı aynen RunCoreType'a taşır; mihomo
    /// config üretimi CoreConfigHandler'daki GPN mihomo dalında yapılır.
    /// </summary>
    internal static ProfileItem BuildWireGuardProfile(GpnServerProfile server)
    {
        var node = new ProfileItem
        {
            IndexId = $"gpn-{server.ServerId}",
            ConfigType = EConfigType.WireGuard,
            CoreType = ECoreType.mihomo,
            Remarks = $"AoGPN {server.Name}",
            Address = server.EndpointHost,
            Port = server.EndpointPort,
            Password = server.ClientPrivateKey,
            Network = nameof(ETransport.raw),
            StreamSecurity = string.Empty,
            Subid = string.Empty,
        };
        // MTU iyileştirmesi: kayıtlı MTU önerilen değerden (1360) büyükse (ör. eski
        // 1420 varsayılanı DB'de kalmış), yol sınırını aşan dış paketlerin
        // parçalanmasını önlemek için önerilen değere kırp — GPN kendi altyapısı
        // olduğundan güvenli tek değerdir; kullanıcı daha düşük bir MTU ayarlarsa
        // ona saygı duyulur.
        var effectiveMtu = server.Mtu > 0 && server.Mtu <= Global.GpnRecommendedMtu
            ? server.Mtu
            : Global.GpnRecommendedMtu;
        if (effectiveMtu != server.Mtu)
        {
            DiagLog.Write($"GPN_MTU WG {server.Mtu} → {effectiveMtu} (GpnRecommendedMtu={Global.GpnRecommendedMtu})");
        }

        node.SetProtocolExtra(node.GetProtocolExtra() with
        {
            WgPublicKey = server.ServerPublicKey,
            WgInterfaceAddress = server.ClientAddress,
            WgMtu = effectiveMtu,
            WgPersistentKeepalive = server.PersistentKeepalive > 0 ? server.PersistentKeepalive : null,
        });
        return node;
    }

    /// <summary>
    /// mihomo TUN açılmadan önce yabancı VPN durumunu (çalışan istemci süreçleri,
    /// yabancı TUN adaptörleri, dolu proxy portu) tespit edip DiagLog'a raporlar.
    /// Üçüncü taraf VPN istemcileri ASLA kapatılmaz. Best-effort: herhangi bir hata
    /// yalnızca loglanır, bağlantı akışı asla engellenmez.
    /// </summary>
    private void DetectForeignTunnelsBeforeStart()
    {
        try
        {
            var detector = new ForeignTunnelDetector();
            var localPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            var result = detector.Detect(localPort);
            if (!result.HasConflicts)
            {
                DiagLog.Write("GPN_LAUNCH foreign scan clean before core start");
                return;
            }
            DiagLog.Write("GPN_LAUNCH foreign state before core start: " +
                $"processes=[{string.Join(",", result.ForeignProcessNames)}] " +
                $"adapters=[{string.Join(",", result.TunAdapterNames)}] port={result.PortOccupiedByForeignProcess}");
        }
        catch (Exception ex)
        {
            // best-effort: tarama hatası çekirdek başlatmayı engellemesin
            DiagLog.Write($"GPN_LAUNCH foreign-scan error: {ex.Message}");
        }
    }

    private async Task<ProfileItem> PickDefaultV2rayNodeAsync(CancellationToken ct)
    {
        var node = await ConfigHandler.GetDefaultServer(_config).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (node is null)
        {
            throw new InvalidOperationException("V2ray düğümü yok — önce bir sunucu profili ekleyin.");
        }
        Logging.SaveLog($"[{Tag}] V2rayTCP fallback düğümü: {node.Remarks} ({node.ConfigType})");
        return node;
    }

    private static Task DefaultUpdateAsync(bool notify, string msg)
    {
        Logging.SaveLog(msg);
        return Task.CompletedTask;
    }
}
