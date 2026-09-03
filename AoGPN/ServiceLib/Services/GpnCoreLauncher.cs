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
            // kayıplı hatta çift parça kaybı yaşanıyor. TUN ve WireGuard MTU'sunu
            // Global.GpnRecommendedMtu (1360) ile hizala — mevcut kayıtlı değerler
            // eski varsayılan (1420) olsa bile uygulanır. Yalnızca oturum içi;
            // kalıcı ayarlara dokunulmaz.
            if (mode == ConnectionMode.WireGuardUDP && _config.TunModeItem.Mtu != Global.GpnRecommendedMtu)
            {
                DiagLog.Write($"GPN_MTU TUN {_config.TunModeItem.Mtu} → {Global.GpnRecommendedMtu} (yol sınırı ~1400; parçalanmayı önle)");
                _config.TunModeItem.Mtu = Global.GpnRecommendedMtu;
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

            await _engineHost.StartAsync(mainContext, allResult.PreSocksResult?.Context, ct).ConfigureAwait(false);

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
