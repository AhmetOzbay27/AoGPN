# GPN → mihomo (Clash.Meta) çekirdek entegrasyonu — Bağlama Haritası

> Durum: Adım 1 ✅ + **Adım 2 ✅ ("GPN Bağlan → mihomo TUN")** + **Adım 3 ✅ (eksik
> mihomo otomatik indirme)** + **Adım 4 ✅ (canlı telemetri)** + **DNS kullanıcı
> ayarlarından okunuyor** + **oturum kurtarma + WARP otomatik onarımı**.
> 2026-09-02: tüm testler yeşil (941/941 çalışan, ServiceLib.Tests). Kaynak: kod
> taraması + canlı A/B/C/DNS doğrulamaları (WARP zinciri `warp=on` dahil).

## Neden mihomo?

sing-box WireGuard **endpoint** dial'i, IPv6'sız Windows makinelerde dual-stack UDP
soketi açamıyor (`listen udp6 [::] invalid argument`) → el sıkışma hiç tamamlanmıyor
(7 konfigürasyon denendi). mihomo aynı anahtarlarla ilk denemede el sıkışıyor ve TUN +
`PROCESS-NAME` ayırımı + WARP SOCKS5 (`dialer-proxy`) zinciri canlı A/B ile kanıtlandı
**ancak yalnızca İtalya/WireGuard KAPALI ortamda** — uygulama GPN modunda zaten
`ForeignTunnelDetector` ile yabancı tünelleri tespit edip uyarır (otomatik kapatma
2026-09-03'te kaldırıldı; koşulu kullanıcı sağlar — bkz.
`docs/foreign-vpn-kill-removal-roadmap.md`).

## Doğrulanmış dikiş yerleri

| Konum | Satır | Ne yapıyor |
|---|---|---|
| `GpnCoreLauncher.BuildWireGuardProfile` | ~130 | GPN sunucusunu `ProfileItem` yapar: `CoreType=sing_box`, WG anahtarları protocol-extra'da (`WgPublicKey`, `WgInterfaceAddress`, `WgMtu`, `WgPersistentKeepalive`), client private key `Password`'ta, endpoint `Address:Port` |
| `GpnCoreLauncher.LaunchAsync` | ~75 | `CoreConfigContextBuilder.BuildAll` → `CoreEngineHost.StartAsync(main, preSocks)` |
| `CoreConfigHandler.GenerateClientConfig` | ~20 | Çekirdek tipine göre config üretim dağıtımı: openvpn / Custom(+mihomo→ClashService) / sing_box / v2ray |
| `CoreManager.LoadCore` | ~125 | `fileName = Utils.GetBinConfigPath(Global.CoreConfigFileName)` (`config.json`), içeriği `GenerateClientConfig`'ten yazar, ardından `TunLifecycleManager` + pre-socks koruma + `RunProcess` |
| `CoreManager.RunProcess` | 811, 920 | `string.Format(coreInfo.Arguments, absoluteConfigPath)` ile başlatır |
| `CoreInfoManager` mihomo bloğu | 189-205 | `CoreExes = GetMihomoCoreExes()` (win: `mihomo-windows-amd64-v1(.exe)` … `clash`/`mihomo`), `Arguments = "-f {0}"`, indirme URL'leri, `-v` |
| `CoreBinaryRegistry.Resolve` | ~80 | sing-box/xray dışı tiplerde `CoreInfoManager.GetCoreInfo(coreType).CoreExes` üzerinden exe arar; `CoreDirectoryName` → `bin/mihomo/` |
| `CoreManager.KillOrphanCoreProcesses` | ~440 | `mihomo` zaten listede (app mülkiyetindeki dizinlerdeki süreçleri öldürür) |
| `CoreConfigValidator` | ~18 | mihomo config doğrulama: `-t -f {path}` hazır |
| `ManualRoutingRules.BuildManagedRules` | | Beyaz liste/kara liste + yakalayıcı → `List<RulesItem>` (OutboundTag: direct/proxy/warp/block) — generator'ın girdisi |
| `CoreConfigContext.RoutingItem.RuleSet` | | JSON `List<RulesItem>` — GPN rota listesi buradan gelir (ConfigHandler.GetDefaultRouting) |

## ✅ Adım 2 — uygulanan değişiklikler (2026-09-02)

| Dosya | Değişiklik |
|---|---|
| `GpnCoreLauncher.cs` | `BuildWireGuardProfile` → `CoreType = ECoreType.mihomo`; LaunchAsync: çekirdek açılmadan ÖNCE yabancı VPN durumu `DetectForeignTunnelsBeforeStart` ile tespit edilip DiagLog'a raporlanır (otomatik kill 2026-09-03'te kaldırıldı — yalnızca uyarı, karar kullanıcınındır); mihomo yolunda `GpnCaptureBridge` KULLANILMAZ (süreç ayırımını mihomo yapar, ikinci Wintun çakışır) |
| `CoreConfigContextBuilder.cs` | `BuildPreSocksIfNeeded` → mihomo için `null` (legacy sing-box koruma yardımcısı mihomo TUN'uyla çakışır) |
| `CoreConfigHandler.cs` | Yeni dal: `RunCoreType==mihomo && ConfigType==WireGuard` → `GenerateGpnMihomoConfig`: `RulesItem` listesi (RuleSet) + `ToGpnServerProfile` (protocol-extra eşleyici) + seçenekler (mixed-port=`GetLocalPort(socks)`, **external-controller=`StatePort2`** — uygulamanın tüm Clash ailesi konvansiyonu: CoreConfigClashService/SingboxStatisticService aynı port; interface-name=fiziksel NIC, DNS fake-ip açık, keepalive) → `GpnMihomoConfigService.GenerateYaml` |
| `CoreManager.cs` | mihomo için: app `TunLifecycleManager` (Begin/Cleanup + flush + TUN-fallback) atlanır; çekirdek öncesi `MihomoTunSupport.EnsureHostRoute(node.Address)` (wg-quick /32 host rotası), kapanışta `RemoveHostRoute()` |
| **Yeni** `Services/CoreConfig/Mihomo/MihomoTunSupport.cs` | Fiziksel NIC tespiti (`GetBestInterface` iphlpapi + Ethernet/WiFi yedeği) ve host rota ekle/sil (`route.exe`) |
| **Yeni** `ServiceLib.Tests/.../GpnMihomoDispatchTests.cs` | Uçtan uca: BuildWireGuardProfile → BuildAll (RunCoreType=mihomo, pre-socks null) → GenerateClientConfig → YAML içerik doğrulaması |
| `GpnCaptureBridgeTests.cs` | `Launcher_WireGuardLaunch_StartsBridge...` → `Launcher_WireGuardLaunch_MihomoCore_DoesNotStartCaptureBridge` (yeni sözleşme: mihomo köprüyü devreye sokmaz) |
| `GpnConnectionCoordinatorTests.cs` | `BuildWireGuardProfile` beklentisi `sing_box` → `mihomo` |

Readiness: hazırlık yoklaması SOCKS5 portundan (mihomo mixed-port) geçer; config
`config.json` adıyla yazılır (`-f` uzantıdan bağımsızdır); `CoreConfigValidator`
`mihomo -t -f` ile config'i başlatmadan önce doğrular; `CoreBinaryRegistry.Resolve`
mihomo'yu `bin/mihomo/` altında arar; orphan-killer'da `mihomo` zaten listeli.

## Önerilen Adım 2 dilimi (dikey)

1. **Düğüm**: `BuildWireGuardProfile` → `CoreType = ECoreType.mihomo` (GPN WireGuard
   modu için). `CoreConfigContextBuilder.Build` TUN zorlaması yalnız Xray/v2fly'ı
   mihomo'ya çevirir — mihomo geçer.
2. **Dağıtım**: `CoreConfigHandler.GenerateClientConfig`'e dal ekle:
   `RunCoreType==mihomo && ConfigType==WireGuard` → ProfileItem protocol-extra →
   `GpnServerProfile` uyarlayıcısı; rules = `RoutingItem.RuleSet` (JSON →
   `List<RulesItem>`); options (mixed-port = `GetLocalPort(socks)`, controller =
   `StatePort2`, log seviyesi, MTU, interface-name) → `GpnMihomoConfigService.GenerateYaml`.
   Sonuç `RetResult.Data`'a yazılır (fileName sing-box için `config.json` — mihomo
   içerik YAML'ini uzantıdan bağımsız ayrıştırır, isim değişikliği şart değil).
3. **Başlatma**: `CoreManager` akışında mihomo dalları:
   - pre-socks koruma (ProtectCoreTypeList Xray/sing-box tabanlı) mihomo için atlanmalı;
   - `TunLifecycleManager.BeginAsync`'in neyi hazırladığı denetlenmeli (mihomo kendi
     TUN + auto-route'unu kendisi kurar; app tarafı rota/adaptör müdahalesi çakışır);
   - hazırlık yoklaması SOCKS5 portundan (mixed-port) zaten geçer.

## ✅ Adım 4 — canlı telemetri (2026-09-02, bu makinede kanıtlı)

`external-controller` StatePort2'ye taşındığı için mevcut tüketiciler mihomo'dan
**hiç kod değişikliği olmadan** beslenir (yumuşak geçiş, `AppManager.IsRunningCore`
mihomo'yu zaten sing_box uyumlu sayar):

| Tüketici | Uç nokta | Kanıt (canlı, `Mihomo Meta v1.19.30 with_gvisor`) |
|---|---|---|
| `ClashApiManager.GetClashProxiesAsync` | `GET /proxies` + `/providers/proxies` | `names=[COMPATIBLE, DIRECT, GLOBAL, PASS, PASS-RULE, REJECT, REJECT-DROP, wg-de]` |
| `ClashApiManager.GetClashConnectionsAsync` (dashboard trafik sekmeleri) | `GET /connections` | İndirme devam ederken `count=1 totals=11328150/1322` — canlı kayıt + kümülatif bayt |
| `StatisticsSingboxService` (hız göstergesi) | `ws://…/traffic` | `{"up":0,"down":1160188,"upTotal":674,"downTotal":3791196}` — sing-box ile aynı şema |
| Kural motoru (socks inbound) | — | `IP-CIDR,1.1.1.1/32,wg-de` eşleşti: trace `ip=130.61.223.36 loc=DE`; `MATCH,REJECT` bağlantıyı kesti (exit 35) |

⚠️ Test deneyimi (belgeye değer): **`curl --noproxy '*'` ile `-x` BİRLİKTE kullanılırsa
curl `-x`'i sessizce devre dışı bırakır** (“* effectively disables the proxy”) —
bu yüzden proxy doğrulamalarında `--noproxy` KULLANMAYIN. İlk api-proof serisinin
tamamı bu tuzağa düştü (traffic proxy'ye hiç girmemişti); düzeltilince tüm uçlar yeşil.

## ✅ Adım 3 — eksik mihomo otomatik indirme (2026-09-02)

| Dosya | Değişiklik |
|---|---|
| `CoreInstaller.cs` | `InstallMissingCoreAsync(coreType, updateFunc, ct, isInstalledCheck?, downloadInstall?)` — aynı pipeline'ı kullanır: `UpdateService.CheckUpdateCore` (CoreInfoManager URL şablonları + SHA-256 doğrulama) → arşiv açma → `bin\<core>\`; sonra `EnsureCoreWintunAsync` (mihomo zip'i wintun içermez — kök/xray/sing-box kopyası, yoksa resmi Wintun indirmesi). `UpdateCoresAsync` listesine mihomo eklendi (`--update-cores` modu taze build'lere mihomo kurar). `CoreExists` yardımcısı + test dikişleri |
| `CoreEngineHost.cs` | Yeni opsiyonel ctor parametresi `autoCoreInstaller` (varsayılan: `CoreInstaller.InstallMissingCoreAsync`). `StartAsync`: mihomo binary doğrulaması başarısızsa “mihomo çekirdeği bulunamadı — indiriliyor…” → oto-indir → başarılıysa **gerçek registry ile yeniden doğrula** → akış devam; başarısızsa orijinal hata korunur |
| **Yeni** `ServiceLib.Tests/Services/CoreAutoInstallTests.cs` | 5 test: eksik→indirici çağrılır, kurulu→no-op, indirme hatası→false; host akışı (eksik→indir→GERÇEK registry doğrulaması→başlat) ve hata yolu (indirme başarısız→orijinal hata) |

Canlı doğrulama (HEAD): `releases/latest` → `v1.19.30`; asset
`mihomo-windows-amd64-v1-v1.19.30.zip` → HTTP 200 — yani uygulamanın ürettiği
indirme adresi geçerli ve canlı testlerdeki binary ile AYNI sürümü dağıtır.

## ✅ DNS kullanıcı ayarlarından okunuyor (2026-09-02)

| Parça | Davranış |
|---|---|
| `GpnMihomoConfigService` | Yeni `DnsDefaultNameservers` seçeneği; `ParseDnsServers(csv, fallback)` — sing-box ile aynı ayırıcılar (`,`/`;`), mihomo biçimleri korunur (saf IP, udp/tcp/tls/quic/https/dhcp, system), `local`/`localhost` atlanır; boşsa fallback. `default-nameserver` boş gelirse nameserver listesindeki saf IP'ler, o da yoksa [1.1.1.1, 8.8.8.8] güvenlik ağı (mihomo DNS modülü default-nameserver'sız başlamaz) |
| `CoreConfigHandler` | `SimpleDNSItem.RemoteDNS` → `nameserver` (tünel-içi çözüm); `SimpleDNSItem.BootstrapDNS` → `default-nameserver`; RemoteDNS boşsa + custom per-core `DNSItem` aktifse `DomainDNSAddress` son çare; `SimpleDNSItem.FakeIPRange` yalnız geçerli CIDR ise uygulanır (kanıtlanmış 198.18.0.1/16 sonrasında); `enhanced-mode: fake-ip` KANITLANMIŞ değerde sabittir. DiagLog'a etkin DNS listesi yazılır |
| Testler | `ParseDnsServers` (ayrıştırma/biçim filtresi/fallback), `IsPlainIpAddress`, kullanıcı nameserver+bootstrap korunumu, DoH-yalnızca fallback; dispatch testi SimpleDNSItem → YAML aktarımını doğrular |

Canlı: mihomo `-t` kabul etti (ayrı `default-nameserver: [9.9.9.9, 1.1.1.1]` + `nameserver` içinde
DoH `https://cloudflare-dns.com/dns-query`) — gerçek binary, üreticinin yeni DNS şeklini onayladı.

## ✅ Oturum kurtarma + WARP otomatik onarımı (2026-09-02)

| Parça | Davranış |
|---|---|
| **Stale host rota** (`MihomoTunSupport.EnsureHostRoute`) | Çökme/force-kill sonrası OS'te kalan /32 rota, `route ADD`'i "already exists" ile düşürüyordu; artık ADD öncesi best-effort `route DELETE` (yoksa sessiz) → her başlangıçta temiz kurulum. Teardown `RemoveHostRoute` aynen korunur; orphan mihomo öldürme (`KillOrphanCoreProcesses`) zaten kapsıyordu; Wintun adaptörü süreçle birlikte ölür (kernel). |
| **WARP otomatik kurtarma** (`WarpAutoRecoverService` — yeni) | WARP egress **sunucu tarafı wireproxy** (10.66.66.1:40000, yalnızca WG tünelinden erişilir) olduğu için "auto-restart" = tüneli yeniden başlatmak: `WarpDialHealthMonitor` faulted'e ulaşınca `GpnConnectionCoordinator.ReconnectCurrentTunnelAsync` (yalnız WireGuardUDP + bağlıyken; V2rayTCP'de false) çağrılır. Cooldown 90 sn + oturum başına 3 deneme; bağlantı değişiminde sayaç sıfırlanır. Kullanıcı ayarı `GuiItem.GpnEnableWarpAutoRecover` (varsayılan true, settings payload'a eklendi; dashboard toggle yok — takip edilebilir). Diag: `GPN_WARP_RECOVER attempt=N/3 ...`; başarıda `WarpDialRecoverNotice` bildirimi. |
| **mihomo `-d` çalışma dizini** | ZATEN VARDI: `CoreInfoManager` mihomo `Arguments = "-f {0}" + PortableMode()` — `PortableMode()` `-d "<bin>"` ekler; cache.db `bin/` altına düşer. Yapılacak iş yoktu. |
| **mihomo log dosyası + WARP monitörü (Tarkov zinciri için kritik)** | mihomo stdout'a yazdığı için `WarpDialHealthMonitor` (sing-box `ao_singbox_*.log` izleyicisi) GPN mihomo oturumlarında KÖR kalıyordu → banner + otomatik kurtarma hiç tetiklenmezdi. Üretici artık `log-file: <guiLogs>/ao_mihomo_<tarih>.log` yazar (CoreConfigHandler), monitör iki dosyayı da izler (dosya başına konum takibi) ve mihomo satır formatını tanır (`warp-socks` + Go/WinSock hata metinleri: `no route to host`, `actively refused`, `did not properly respond`, `i/o timeout`, `unreachable ...`). `ExtractError` mihomo biçimini de ayrıştırır. |
| **BsGLauncher önerisi WARP** | `KnownAppCatalog.SuggestAction("BsGLauncher.exe")` `vpn` idi — çalışan uygulamalardan launcher ekleyen kullanıcı yanlışlıkla VPN yoluna (Oracle çıkışı → Cloudflare WAF engeli) düşüyordu; Tarkov preset'iyle tutarlı olarak artık `warp` önerilir. |
| Testler | `WarpAutoRecoverServiceTests` (8: tetikleme, healthy no-op, cooldown blok/süre, max deneme, sayaç sıfırlama, kapalı ayar, başarısız yeniden başlatma) + koordinatör `ReconnectCurrentTunnelAsync` (3: WG oturumunda Stop+Launch, V2rayTCP'de false, in-flight eşzamanlılık reddi) + monitör mihomo satırları (5) + YAML `log-file` (2) + katalog launcher→warp (1). 946/946 + 200/200 JS yeşil. |

## Açık riskler / kararlar

- ✅ **NodeValidator**: `WireGuard` + `mihomo` kombinasyonu serbest geçer (yalnız
  sing-box/Xray dalları özel denetim yapar) — ek dal gerekmedi, test kapsar.
- ✅ **Telemetri**: Adım 4 tamam — `external-controller` StatePort2'de; dashboard
  (`ConnectionMonitorViewModel`/`ClashProxiesViewModel`/`ClashConnectionsViewModel`)
  ve hız göstergesi (`StatisticsSingboxService` + `/traffic`) mihomo'dan canlı beslenir;
  tamamı bu makinede kanıtlandı.
- ✅ **DNS**: `DnsEnabled=true` + fake-ip generator'da uygulandı ve canlı DNS fazında
  doğrulandı (domain → warp-socks → `warp=on`); sızıntı yok (`[DNS] hijack` izleri).
- ✅ **interface-name**: `MihomoTunSupport.DetectPhysicalInterfaceName` (iphlpapi
  GetBestInterface) her GPN bağlantısında fiziksel NIC adını çözer; A/B doğrulaması
  `interface-name` + host rota kombinasyonuyla yapıldı.
- ✅ **Host rota**: `MihomoTunSupport.EnsureHostRoute` — /32 wg-quick deseni, çekirdek
  öncesi eklenir, kapanışta silinir (İtalya kapalı ortamda kanıtlandı).
- ✅ **mihomo çalışma dizini**: `-d` zaten sabit — `PortableMode()` mihomo
  `Arguments`'ına `-d "<bin>"` ekler (cache.db `bin/` altına düşer).
- ✅ **Stale host rota (çökme sonrası)**: `EnsureHostRoute` ADD öncesi best-effort
  DELETE ile her başlangıçta temiz kurulum yapar.
- ✅ **WARP egress düşüşü**: `WarpAutoRecoverService` faulted'de tüneli otomatik
  yeniden başlatır (cooldown + 3 deneme/session; ayar `GpnEnableWarpAutoRecover`).
- ✅ **Binary dağıtımı**: eksik mihomo artık Bağlan sırasında OTOMATİK indirilir
  (UpdateService + CoreInfoManager URL'leri, SHA-256 doğrulanmış, wintun.dll
  dahil); `--update-cores` başlangıç modu da mihomo'yu kurar; manuel betik
  (`tmp-mihomo/install-mihomo-core.ps1`) hâlâ yedek.

## Dosya kontrol listesi

- ✅ `GpnCoreLauncher.cs` — CoreType mihomo + mihomo öncesi foreign tespit/uyarı (kill kaldırıldı) + bridge atlaması
- ✅ `CoreConfigHandler.cs` — WireGuard+mihomo dağıtım dalı + ProfileItem→GpnServerProfile uyarlayıcı + StatePort2 controller
- ✅ `CoreManager.cs` — pre-socks/TUN müdahalesini atla + host rota yaşam döngüsü
- ✅ `CoreConfigContextBuilder.cs` — mihomo için pre-socks null
- ✅ `Services/CoreConfig/Mihomo/MihomoTunSupport.cs` — NIC tespiti + host rota (yeni)
- ✅ `GpnMihomoConfigService.cs` — Adım 1 (DNS fazı dahil)
- ✅ `ServiceLib.Tests/.../GpnMihomoDispatchTests.cs` — uçtan uca dağıtım testi (yeni; controller portu doğrular)
- ✅ Adım 3: eksik mihomo otomatik indirme (CoreInstaller + CoreEngineHost kancası)
- ✅ Adım 4: telemetri — dashboard + hız göstergesi mihomo'dan canlı (yukarıdaki tablo)
