# AoGPN — Gerçek GPN Geçiş Yol Haritası (V2ray Tabanı → WireGuard + WinDivert)

**Tarih:** 29 Ağustos 2026
**Kapsam:** Mevcut sing-box/Xray TUN (v2rayN tabanı) mimarisinden, Wintun + WinDivert tabanlı gerçek GPN (süreç bazlı oyun tünelleme) mimarisine 4 adımlı geçiş planı.
**Sunucular:**
- **İtalya** `92.4.220.236:51820` — Oracle Linux 9, wg0 `10.66.66.1/24`, BBR/fq, MTU 1420 (`.freebuff/aogpn-client.conf`)
- **Almanya** `130.61.223.36:51820` — Ubuntu, wg0 `10.66.66.1/24`, BBR/fq, MTU 1420 (`.freebuff/aogpn-client-alman.conf`)

> Bu doküman, kod tabanının doğrudan incelenmesiyle üretilmiştir (`AoGPN/ServiceLib`, `AoGPN/AoGPN`, `docs/`, `.freebuff/`). Eşlik eden kod: `ServiceLib/Services/GpnServerSelectionService.cs` (Faz 4 iskeleti).

---

## 1. Adım — Mevcut İskelet Analizi

### 1.1 Bileşen haritası

| Katman | Bileşen | Dosya | Görev |
|---|---|---|---|
| **UI** | WPF kabuk + WebView2 dashboard | `AoGPN/AoGPN/Views/MainWindow.xaml.cs`, `vpn-gpn-dashboard.html` | Ana pencere, tepsi ikonu, dashboard (Telemetri, Split Tunnel, Node Pool) |
| **UI** | Ayarlar / durum çubuğu | `OptionSettingWindow`, `StatusBarView` | Çekirdek, TUN, rota ayarları; bağlantı durumu |
| **UI/VM** | Bağlantı orkestrasyonu | `MainWindowViewModel.cs` (`Reload` → `LoadCore`) | "Bağlan" butonu akışı; `CoreEngineHost.StartAsync` |
| **VM** | Split tunnel yönetimi | `SplitTunnelViewModel.cs` | Manuel rota listesi (app/domain/IP), oyun tetikleyici, transport seçimi (tun/proxy) |
| **Core** | Config üretimi | `CoreConfigContextBuilder`, `CoreConfigHandler` | Profil → sing-box/Xray JSON config |
| **Core** | Çekirdek süreci | `CoreManager`, `CoreEngineHost`, `ProcessService` | sing-box.exe/xray.exe başlatma, sağlık (SOCKS5 probe), çökme kurtarma, orfan temizliği |
| **Core** | TUN routing | `SingboxRoutingService.GenRoutingGpn()` | GPN modu: `process_name` kuralları → proxy, gerisi direct; QUIC bloğu (UDP/443 reject) |
| **Core** | TUN yaşam döngüsü | `TunLifecycleManager`, `WindowsUtils` | Öncesi/sonrası socket flush, `singbox_tun` adaptör doğrulaması, `RemoveTunDevice` |
| **Core** | Socket temizliği | `NetworkConnectionFlusher` | iphlpapi `GetExtendedTcpTable`+`SetTcpEntry(DELETE_TCB)`, UDP tarama, DNS cache flush |
| **Core** | Süreç keşfi | `ProcessCatalogService` | Çalışan uygulama listesi (split-routing seçici için) |
| **Core** | Ölçüm altyapısı | `NodePingCoordinator`, `SpeedtestService` | Paralel ping/hız testi (HTTP proxy üzerinden gerçek ping) |
| **Core** | WireGuard profil desteği | `WireguardFmt` (`EConfigType.WireGuard`) | wireguard:// URI ve .conf ayrıştırma → `ProfileItem` |
| **Çekirdekler** | sing-box / Xray | `bin/sing_box/sing-box.exe`, `bin/xray/xray.exe`, `wintun.dll` | TUN taşıyıcı (sing-box `stack="system"` = WFP) + VLESS/REALITY outbound |

### 1.2 Kalacaklar / Atılacaklar / Değişecekler

**✅ KALIR (altyapı — dokunulmayacak):**
- WPF kabuk, WebView2 dashboard, tepsi ikonu, dil sistemi (`Dil/*.json`)
- Config/DB katmanı (`ConfigItems`, `SqliteHelper`, `ProfileItem` — WireGuard profili zaten modelde)
- `NodePingCoordinator` + `SpeedtestService` ölçüm altyapısı (Faz 4 bunu kullanır)
- `ProcessCatalogService` (exe seçimi), `NetworkConnectionFlusher` (bağlantı kesme hâlâ gerekli)
- `SplitTunnelViewModel`'in manuel rota listesi — **bu liste "seçilen oyun exe" kaynağının ta kendisi**
- `CoreEngineHost` yaşam döngüsü kapısı + `AppEvents` sağlık yayını
- `UpdateService`/`CoreInstaller` deseni (wintun.dll + WireGuard motoru için aynen yeniden kullanılır)
- `WireguardFmt` — İtalya/Almanya `.conf`'larını içe aktarma hâlâ buradan geçer

**❌ ATILIR (GPN modu için):**
- `GenRoutingGpn()` + `SingboxInboundService` TUN bloğu — yerine WireGuard motoru gelir
- `TunLifecycleManager`'ın `singbox_tun` doğrulaması — yerine kendi `AoGPN_tun` adaptörümüz
- QUIC bloğu hack'i (UDP/443 reject) — WinDivert UDP'yi zaten yakaladığı için gereksiz kalır
- "TUN yoksa → Global VPN proxy fallback" karmaşası — per-exe yakalama modunda fallback gerekmez

**🔄 DEĞİŞİR (kademeli):**
- `CoreManager.LoadCore`: sing-box süreci yerine `IGpnTransport` soyutlamasına geçer
- `TunLifecycleManager`: "aktif çekirdeğin TUN adı" doğrulaması (motor bağımsız)
- `ForeignTunnelDetector`: kendi adaptör adını (`AoGPN_tun`) yabancı saymaz

---

## 2. Adım — Hedef Mimari (Gerçek GPN: Wintun + WinDivert)

### 2.1 Neden "gerçek GPN" = Wintun + WinDivert?

- **TCP meltdown** yalnızca TCP'nin TCP içinde tünellenmesinde olur. WireGuard dış taşıyıcı olarak **saf UDP** kullanır → meltdown yapısal olarak imkânsız. Mevcut tek eksik, oyun trafiğinin sing-box'ın kullanıcı alanı yığını (gVisor/WFP) yerine **doğrudan WireGuard tüneline** binmesidir.
- **Wintun** (kernel adaptörü) yalnızca paket taşır; WireGuard şifreleme/el sıkışma kullanıcı alanında çalışır.
- **WinDivert** kullanıcı alanındaki paket **hook'u**dur: PID'e göre filtre kurar, paketi okur, karar verir. Per-exe yakalama için en yaygın araçtır.

### 2.2 Veri yolu (hedef akış)

```
[Oyun exe — PID 1234 (ör. EscapeFromTarkov.exe)]
        │  UDP/TCP paketi (src=LAN IP, dst=oyun sunucusu)
        ▼
[WinDivert — NETWORK katmanı, filtre: outbound && (processId=1234 || processId=5678)]
        │  WinDivertRecv → kullanıcı alanı
        ▼
[AoGPN WinDivertEngine (ServiceLib)]
        │  IP paketi → WireGuard oturumu (kripto kullanıcı alanında)
        ▼
[WireGuard encrypt → UDP(92.4.220.236:51820)]
        │  fiziksel arayüz üzerinden ham UDP soketi
        │  (kendi PID'imiz WinDivert filtresinden hariç tutulur)
        ▼
[Sunucu wg0 (BBR/fq/MTU 1420)] → İnternet → oyun sunucusu
```

Dönüş yolu simetriktir: sunucudan gelen şifreli UDP → WireGuard oturumu çözer → **`WinDivertSend`** ile ağ yığınına geri enjekte edilir → oyunun soketi 4-tuple eşleşmesiyle paketi alır. Oyunun TCP durum makinesi (congestion control) oyun ile sunucu arasında kalır; tünel araya girmez → **saf UDP iletimi, meltdown yok**.

### 2.3 Yeni katmanlar (ServiceLib/Services/Gpn/)

| Modül | Görev |
|---|---|
| `GpnServerProfile` / `WireGuardServerCatalog` | Sunucu listesi (Faz 3) |
| `GpnServerSelectionService` | ICMP/TCP ölçüm + otomatik seçim + failover (Faz 4 — **kod hazır**) |
| `WireGuardTunnelService` | Wintun adaptörü + WireGuard oturumu (Faz 2b) |
| `WinDivertEngine` + `DivertWorker` | PID filtresi + paket yakalama → Channel kuyruğu (**iskelet ✅**) |
| `GpnTargetResolver` | exe adı → PID listesi (oyunlar alt süreç doğurur; 3-5 sn'de bir tazelenir) |
| `IGpnTransport` | Faz 1/2'yi aynı arayüz altında toplar |

### 2.4 WireGuard motoru seçimi (2 yol)

1. **`WireGuardTunnel.dll` (wireguard-windows, önerilen):** resmî WireGuard istemcisinin kullandığı yerel DLL. Config'i beslersin, Wintun adaptörünü kendisi kurar, kriptoyu DLL içinde çalıştırır. P/Invoke ile küçük bir sarıcı yeterli (`WireGuardTunnelSetConfig`, `WireGuardTunnelGetSessionStatus`...). `wintun.dll` de bu paketin içinden gelir.
2. **BoringTun (Cloudflare, saf C#):** tamamen yönetilen WireGuard implementasyonu; DLL bağımlılığı yok ama TCP paketleri için kendi IP yığınına ihtiyacın olur. Yedek yol olarak tut.

### 2.5 TCP stratejisi (meltdown'ı bitiren karar)

- **Önerilen:** TCP paketleri de WinDivert ile yakalanıp **olduğu gibi** tünelden geçirilir (transparent bridge). Tünel UDP olduğu için TCP-over-TCP sorunu yoktur; enjekte edilen yanıtlar 4-tuple ile sokete ulaşır. Kullanıcı alanı TCP yığını **gerekmez**.
- **UDP/ICMP:** aynı yol; UDP tam-çekirdek (full-cone) davranışı korunur — EFT/LoL için kritik.
- **Başlangıçta (Faz 1) TCP'yi atlamak istersen:** yalnızca UDP+ICMP yakala, TCP normal yoldan gitsin — tünel yalnızca oyun protokolünü taşır.

### 2.6 WinDivert filtresi ve istisnalar

```
outbound
&& (processId = 1234 or processId = 5678 or ...)   // hedef exe PID'leri
&& !(processId = <AoGPN kendi PID'i>)              // tünelin kendi UDP soketi
&& !(ip.DstAddr = 92.4.220.236 and ip.DstPort = 51820)  // sunucu endpoint'i (çift emniyet)
```

- Filtre her 3-5 sn'de bir yeniden derlenir (oyun alt süreç başlatır, PID'ler değişir).
- DNS: hedef exe'lerin 53/udp paketleri de yakalanır → tünel içi DNS (1.1.1.1) — mevcut `SingboxDnsService` mantığı taşınır.
- IPv6: sunucular IPv4; `::/0` engel kuralı mevcut `GenRoutingGpn`'den devralınır.

### 2.7 Uygulama yolu (2 fazlı — düşük riskli geçiş)

- **Faz 1 — "sing-box + WireGuard outbound" (hızlı kazanım):** `GenRoutingGpn`'i değiştirmeden, sing-box TUN `stack="system"` (WFP) kalırken **outbound'u VLESS/REALITY yerine gerçek WireGuard outbound'a** çevir (İtalya/Almanya sunucularına). Per-exe `process_name` kuralları **aynen çalışmaya devam eder** (mevcut GPN kuralları). Sonuç: bugünkü UI/rota kodu sıfır değişiklikle saf UDP WireGuard taşımacılığına kavuşur → **TCP meltdown hemen biter**.

  **✅ Faz 1 uygulandı (29 Ağu 2026):**
  - `SingboxOutboundService.BuildWireGuardEndpoint()` — şablon JSON'dan deserialize etmek yerine **programatik** sing-box 1.12+ `endpoints` şeması üreticisi: `system:false` (userspace — singbox_tun ile çakışmaz), MTU 1420, `persistent_keepalive_interval` desteği, güvenli `Reserved` çözümleme.
  - `WgPersistentKeepalive` alanı `ProtocolExtraItem`'e eklendi; `WireguardFmt.ResolveConfig` artık `.conf` içindeki `PersistentKeepalive`'ı parse eder (NAT traversal için kritik — önceden hiç okunmuyordu).
  - Xray paritesi: `WireguardPeer4Ray.keepAlive` eklendi ve `V2rayOutboundService`'te dolduruldu.
  - **Doğrulama:** sing-box 1.13.19 binary'siyle `sing-box check` — İtalya sunucusunun gerçek endpoint/pubkey verisi + GPN process_name kuralları + TUN içeren üretilen config **binary'den geçti** (`WireguardGpnConfig_WithAoGPNItalyServer_IsAcceptedByRealSingBox` testi). Runtime doğrulaması: `system` belirtilmezse endpoint userspace çalışır, sistem adaptörü oluşturmaz (TUN ile birlikte çalışır).
  - **Canlı test rehberi (29 Ağu 2026): `docs/gpn-faz1-live-test.md`** — İtalya/Almanya `.conf` içe aktarma, `Bağlan` (otomatik ölçüm + seçim), sunucuda `wg show` ile `latest handshake` doğrulama, kabul ölçütleri ve sorun giderme adımlarını belgeler.
- **Faz 2 — Native motor (hedef mimari, ~2-3 hafta):** sing-box'ı veri yolundan çıkar. `IGpnTransport` altında `WinDivertEngine` + `WireGuardTunnelService`; `CoreManager` bu iki bileşeni yönetir. Faz 1'deki config üreticileri legacy VLESS/REALITY düğümleri için korunur (uygulama hâlâ normal proxy de destekler).

---

## 3. Adım — Sunucu Entegrasyonu (client.conf'u dinamik gömme)

### 3.1 Veri modeli

Mevcut `ProfileItem` (EConfigType.WireGuard) İtalya/Almanya'yı taşıyabilir, ancak ayrı bir model önerilir — sunucu kümesi (cluster) olarak yönetim kolaylığı:

```
GpnServerProfile
├─ ServerId            "it" / "de"
├─ Name                "İtalya" / "Almanya"
├─ EndpointHost:Port   92.4.220.236:51820 / 130.61.223.36:51820
├─ ServerPublicKey     (wg0 pubkey — kurulum çıktısından)
├─ ClientPrivateKey    (sunucu başına FARKLI — .freebuff config'lerinde zaten böyle)
├─ ClientAddress       10.66.66.2/24
├─ Mtu 1420 · Dns 1.1.1.1 · PersistentKeepalive 25
```

### 3.2 Kaynak stratejisi

1. **Birincil: SQLite** (`ConfigItems` tarzı, yeni tablo `gpn_servers`) — yeniden kurulumda kaybolmaz, UI'da düzenlenebilir, mevcut `SqliteHelper` deseni.
2. **Yedek: gömülü kaynak** — ilk kurulumda DB'yi tohumlamak için iki `Sample/` kaynağı (`gpn_italy.conf`, `gpn_germany.conf`), `EmbeddedResource` olarak. `.freebuff/aogpn-client.conf` dosyaları buraya `Sample/` altına taşınır.
3. **Güncelleme: sunucu listesi URL'si** (opsiyonel Faz 3.5) — basit bir JSON endpoint'inden yeni sunucu/pubkey çekme; `DownloadService` zaten mevcut.

### 3.3 Anahtar güvenliği (kritik)

  **✅ gpn_servers + DPAPI uygulandı (29 Ağu 2026):**
  - `Models/Entities/GpnServerItem.cs` — `gpn_servers` tablosu (```ServerId (PK, uç noktadan türetilmiş "host:port"), Name, EndpointHost/Port, ServerPublicKey, ClientPrivateKeyEnc, ClientAddress, Mtu, Dns, Keepalive, IsEnabled, SourceProfileId, UpdatedAt```). `AppManager.InitApp` → `CreateTable<GpnServerItem>()`.
  - `Common/DpapiCryptor.cs` — Windows'ta crypt32 `CryptProtectData/CryptUnprotectData` (Geçerli Kullanıcı, UI istemi kapalı) P/Invoke; çıktı base64. Windows dışında (debug GUI) düz base64 geri düşüşü + günlük uyarısı. `Marshal` ile dengeli blok tahsisi/serbest bırakma.
  - İstemci özel anahtarı DB'ye **düz metin yazılmaz**: `ClientPrivateKeyEnc` DPAPI-şifreli blob taşır; yalnızca `LoadAsync`/bağlantı anında `DpapiCryptor.Decrypt` ile çözülür (bozuk/başka kullanıcı kaydı çözülemez → satır atlanır).
  - `WireGuardServerCatalog` artık **birincil kaynak olarak gpn_servers**'ı okur: `LoadAsync` → `UpsertAsync(GpnServerItem/GpnServerProfile)` → `UpsertFromProfileAsync(ProfileItem)` → `RemoveAsync` → `GetItemsAsync`. Tablo boşsa mevcut WireGuard ProfileItem'larından **tohumlanır** (geriye dönük uyum).
  - `.conf` içe aktarımı (`ConfigHandler.AddBatchServers4Wireguard`) her başarılı peer için `UpsertFromProfileAsync` çağırır; ServerId uç noktadan türetildiği için aynı .conf'i yeniden içe aktarmak yeni satır oluşturmaz (upsert idempotent).
  - **Testler:** `DpapiCryptorTests` (4: roundtrip, deterministik olmama, boş/gürültü) + `WireGuardServerCatalogTests` (11: DPAPI'li GpnServerItem eşlemesi, bozuk anahtarın reddi, UpsertFromProfile roundtrip — diske düz metin yazılmadığı, yeniden içe aktarmada tek satır, **conf-metni → katalog uçtan uca**).

  **✅ Sunucu Yönetimi ekranı (dashboard, 29 Ağu 2026):** Sidebar'a "GPN Sunucuları"
  görünümü + `viewGpnServers` section:
  - `.conf` içe aktarma kutusu — yapıştırılan metin `gpn_server_add` ile host'a gider,
    `WireguardFmt.ResolveConfig` → `UpsertFromProfileAsync` (DPAPI), sonuç bildirim çubuğu.
  - Yönetilen sunucular listesi: ad, uç nokta, adres/MTU/DNS, 🔐 rozeti (özel anahtar
    DPAPI'li — şifreli blob dahi renderer'a gönderilmez; yalnızca meta veri), etkin/devre
    dışı durumu + `Enable/Disable`/`Delete` (silme onaylı).
  - Host eylemleri: `gpn_servers_list` / `gpn_server_add` / `gpn_server_delete` /
    `gpn_server_toggle` (`DashboardMessagePolicy` izin listesine eklendi; uzun .conf
    payload'ı için `TryGetLongStringProperty`). `TryGetBooleanProperty` eklendi.
  - Devre dışı sunucular `SelectBestServerAsync` adaylığından çıkarılır (IsEnabled filtresi).
  - **Testler:** dashboard entegrasyonuna 2 test (list render + CRUD gönderimleri,
    silme onayı) + katalog conf-metni uçtan uca testi (11/11 katalog, 32/32 dashboard JS).

  **✅ WPF ekleme/düzenleme penceresi (29 Ağu 2026):** `GpnServerEditViewModel` +
  `GpnServerEditWindow` — gpn_servers'a manuel sunucu ekleme/düzenleme:
  - Alanlar: ad, uç nokta (host:port), sunucu genel anahtarı, istemci özel anahtarı
    (DPAPI notlu; "Generate" düğmesi `Utils.GenerateWireGuardPrivateKey` ile 32-bayt
    base64 üretir), adres, MTU, DNS, keepalive, etkin bayrağı.
  - Kaydet → `WireGuardServerCatalog.UpsertAsync` (yeni) veya mevcut satırı
    `GpnServerItem` üzerinden güncelle; özel anahtar her durumda DPAPI ile yazılır.
    **Düzenlemede alan boş bırakılırsa disk'teki DPAPI'li anahtar korunur.**
    ServerId uç noktadan türetildiği için aynı uç noktayı yeniden eklemek satırı
    günceller (çoğaltmaz).
  - Dashboard Sunucu Yönetimi'ne **Add** butonu + her kartta **Edit** butonu
    (`gpn_server_add_dialog` / `gpn_server_edit_dialog` → `SimpleViewLocator` kaydı).
  - `ResUI`'ye 14 yeni anahtar; `GpnServerEditViewModelTests` (5: DPAPI'li yazım,
    geçersiz uç nokta reddi, anahtarsız yeni sunucu reddi, düzenlemede anahtar
    korunması, anahtar üretimi) — SQLite singleton paylaşımı için
    `SqliteCatalogCollection` (paralellik kapalı). 499 C# + 41 dashboard JS testi.

  **✅ Canlı ölçüm paneli (29 Ağu 2026):** Sunucu Yönetimi kartlarında her sunucunun
  canlı gecikmesi + UDP durumu:
  - Host eylemi `gpn_servers_probe` → `ProbeGpnServersAsync`: katalogdaki **etkin**
    sunuculara `GpnServerSelectionService.ProbeAllAsync` (ICMP, Samples=3) + her
    sunucuya `UdpHealthChecker` (51820/udp) paralel; sonuçlar `setGpnServerProbes`
    ile gönderilir (ServerId, DelayMs/Avg/Max, LossPercent, UdpStatus, MeasuredAt).
  - JS: kart rozetleri — `it · 42ms · UDP ok` / `de · — · UDP bloklu`; görünüm açılınca
    otomatik ölçüm döngüsü (12 sn) + **Ölç** butonu (`gpnProbePending` bekleyen isteği
    engellemez, her tıklamada yeniden ölçer); başka görünüme geçince döngü durur.
  - **0 ms düzeltmesi:** `ProbeServerAsync`/`AutoDelayMsAsync` `delay > 0` kontrolü
    loopback/hızlı LAN'da ölçülen 0 ms'lik geçerli RTT'yi başarısız sayıyordu; `>= 0`
    yapıldı (-1 başarısızlık kalır). Failover/Recovery aday filtreleri de `>= 0`.
  - **Testler:** `ProbeAllAsync` sıra/sonuç testleri (loopback, 0ms dahil) +
    erişilemez host başarısızlık testi; dashboard JS probe köprüsü testi (otomatik
    ölçüm + Ölç butonu + bekleyen istek davranışı). 27/27 GPN, 161/161 JS.
  - **✅ Sunucu kümesi kartı (29 Ağu 2026):** ölçüm aynı `ProbeGpnServersAsync`
    verisini beslemeye devam eder, ancak aracı artık tek/önbellekli
    `GpnServerSelectionService` örneği üzerinden `MainWindowViewModel.ProbeServersAsync`
    (koordinatörle aynı örneği paylaşır). GPN panelinde İtalya/Almanya canlı
    gecikme kartı: `renderGpnCluster` (UDP dotu + gecikme + kayıp + en düşük
    gecikmeli sağlıklı adayda **best** rozeti + `N open · best` özeti); panel
    görünürken 15 sn'lik otomatik ölçüm (self-rescheduling `setTimeout` → testte
    setInterval sayacını bozmaz). `gpn_cluster_probe` aksiyonu `gpn_servers_probe`
    ile aynı host akışını çalıştırır. `gpn.cluster.*` 9 dilde + fallback.
    Dashboard JS testi (sunucu listesi+probe render, best vurgusu, ilk otomatik
    ölçüm, Ölç butonu POST).

  **✅ Gömülü ANAHTARSIZ şablonlar + harici anahtar tohumlaması (29 Ağu 2026):**
  - **Karar: istemci özel anahtarları SÜRÜM KONTROLÜNE YAZILMAZ.** `Sample/gpn_sample_italy_conf`
    + `gpn_sample_germany_conf` artık ANAHTARSIZ şablondur: uç nokta (`92.4.220.236` /
    `130.61.223.36:51820`), sunucu genel anahtarı, adres/MTU/DNS korunur; `PrivateKey`
    yönergesi bilinçli olarak yoktur (anahtarsız conf `ResolveConfig`'te reddedilir).
  - `WireGuardServerCatalog.SeedAsync` sırası: (1) saklı WireGuard profilleri,
    (2) env tam-conf `GPN_ITALY_CONF_B64`/`GPN_GERMANY_CONF_B64` (CI secret biçimi,
    anahtar conf içinde gelir), (3) gömülü şablonlar + harici anahtar — `CollectExternalClientKeys`
    (ayar `GuiItem.GpnSeedItalyPrivateKey/GpnSeedGermanyPrivateKey` &lt; env
    `GPN_ITALY_PRIVATE_KEY`/`GPN_GERMANY_PRIVATE_KEY`; env ayarı ezer) → `InjectPrivateKey`
    (satırı ekler/değiştirir) → `UpsertFromProfileAsync` (DPAPI'li yazım).
  - **Güvenlik duruşu:** anahtar dışarıdan gelmezse şablon ASLA tohumlanmaz (anahtarsız
    profil yararsızdır; boş katalog kalır, kullanıcı Sunucu Yönetimi'nden içe aktarır).
    `.freebuff/` (gerçek conf/SSH anahtarları) `.gitignore`'a eklendi. Gerçek anahtar
    sabitleri testlerden de çıkarıldı — yerine deterministik sahte anahtarlar.
  - **Testler:** 8 — anahtarsız şablon yapısı (theory), `InjectPrivateKey` ekle/değiştir/
    geçersiz, `CollectExternalClientKeys` ayar+env önceliği (config geri alınır),
    env bare-key → DPAPI roundtrip, anahtarsız → tohumlama yok, env tam-conf → doğrudan,
    `LoadAsync` boş tabloda harici anahtarla/anahtarsız. 24/24 katalog.
  - **Uçtan uca yeni kurulum testi (29 Ağu 2026):** `NewInstall_EmptyDb_AppStart_SeedsAndPopulatesConnectCandidates`
    — boş gpn_servers + harici anahtarlar (env) → ilk `LoadAsync` (uygulama başlangıcı/dashboard
    probe/GPN Bağlan'ın aday kaynağı) → tablo İtalya/Almanya ile dolar (DPAPI'li, düz metin yok)
    ve GPN Bağlan'ın tükettiği aday listesi her iki sunucuyu çözülmüş anahtarlarla döndürür;
    ikinci `LoadAsync` (yeniden başlatma) satır çoğaltmaz. Ayar yolunun eşdeğeri
    (`GpnSeed*PrivateKey`) ayrı testte. Katalog 26/26; tam süit 611 geçti / 4 bilinen temel.
  - **Varsayılanları geri yükle + güncellik durumu (29 Ağu 2026):** `WireGuardServerCatalog`'a
    `GetDefaultTemplates` (gömülü anahtarsız şablonları ayrıştırır: uç nokta, sunucu anahtarı,
    adres/MTU/DNS/keepalive), `GetDefaultsStatusAsync` (her şablon için tohumlandı mı / DPAPI
    anahtarı çözülebiliyor mu / şablonla güncel mi + farklı alan adları) ve `RestoreDefaultsAsync`
    (şablon alanlarını mevcut kayda yeniden uygular — **DPAPI anahtarı, ad ve etkinlik korunur**;
    kayıt yoksa atlanır, anahtarsız şablon tek başına sunucu üretemez; idempotent). Sunucu Yönetimi
    ekranındaki İçe aktar kartına "Gömülü varsayılanlar" bölümü: durum çipleri (Güncel / Güncel değil
    + alan farkları / Tohumlanmamış / anahtar yok) + **Varsayılanları geri yükle** butonu
    (`gpn_defaults_status` / `gpn_defaults_restore` host aksiyonları, DashboardMessagePolicy'e eklendi;
    `gpn_servers_list` artık durumu da basar). gpn.servers.defaults.* 9 dilde. Testler: katalog 7 yeni
    (şablon ayrıştırma, boş→tohumlanmamış, güncel, eskimiş→alan farkları, geri yükle anahtar/ad/etkinlik
    korunur, kayıt yok→0, idempotent) — 33/33; JS kart 1 (44/44 dashboard). Tam süit 618 geçti / 4 bilinen temel.

  **✅ Rozet rotasyonu (29 Ağu 2026):** aynı uç noktaya farklı sunucu genel /
  istemci özel anahtarı geldiğinde kayıt güncellenir ve eski kayıt silinir:
  - `PersistAsync` satırı her zaman uç noktadan türetilen **kanonik ServerId**
    altında yazar (eski içe aktarımlardan kalan IndexId tabanlı özel ServerId'ler
    de buraya çekilir).
  - `RemoveSameEndpointOtherIdsAsync`: aynı host:port'un farklı ServerId'li eski
    rozetleri silinir → uç nokta başına tek satır garantisi.
  - Anahtar değişimi **DPAPI çözülmüş** metinler karşılaştırılarak tespit edilir
    (şifreli blob'lar her çağrıda farklıdır — aynı anahtarın yeniden içe aktarımı
    yanlışlıkla "rotasyon" sayılmaz) ve `WgCatalog` loguna yazılır.
  - **Testler:** 3 yeni — pub+priv rotasyonu (tek satır, kanonik id), yalnızca
    priv rotasyonu, eski özel-ServerId satırın silinip diğer uç noktanın
    dokunulmaması. 18/18 katalog.

  **✅ GPN diyagnoz olay akışı → dashboard (29 Ağu 2026):** DiagLog'un "GPN_*"
  çıktısı WebView2 dashboard'a canlı akar:
  - `AppEvents.GpnDiagChanged` (`EventChannel<GpnDiagEvent>`): Kind (LOG /
    RECOVER / SELECT / FAILOVER / LAUNCH ...), Message (ham satır), TimestampMs.
  - `DiagLog.Write` kancası: yalnızca `GPN_` önekli satırları yayınlar; diğer
    kategoriler (FLUSH / TUN_* / ROUTE / CORE_* / QUIC ...) akışa karışmaz.
    Dosya yazımı başarısız olsa bile yayın yapılır (best-effort).
  - `GpnConnectionCoordinator` yaşam döngüsü GPN_LOG satırları yazar:
    connect start / connected (WireGuardUDP veya V2rayTCP fallback) / switch /
    disconnect / failed.
  - MainWindow: `GpnDiagChanged` aboneliği → `PushGpnDiagAsync` →
    `window.setGpnDiag` (camelCase, ExecuteScriptSafelyAsync UI thread'e taşır).
  - Dashboard GPN panelinde tanı akışı kartı: zaman damgalı + Kind rozetli son
    50 satır (otomatik kaydırma, sayı rozeti), Temizle butonu. 9 dilde
    `gpn.diag.*` anahtarları.
  - **Testler:** `DiagLogTests` (3: GPN satırı yayınlar + Kind ayrıştırma,
    GPN dışı satır yayınlamaz, boşluksuz GPN satırı) + dashboard JS feed testi
    (ekleme, 50 satır tavanı, Temizle). 510+ C# / 34 dashboard JS.

  **✅ EnableRecoveryWatch kullanıcı ayarı + UI (29 Ağu 2026):** V2rayTCP
  düşüşü sonrası otomatik Tier-2 (WireGuard) kurtarması artık kullanıcı
  tarafından açılıp kapatılabilir:
  - `GUIItem.GpnEnableRecoveryWatch` (varsayılan true) — JSON'da yoksa `= true`
    başlatıcı devreye girer, şema göçü gerekmez.
  - `MainWindowViewModel.TryRunGpnConnectAsync`, `GpnConnectionCoordinator.ConnectAsync`'e
    `GpnProbeOptions { EnableRecoveryWatch = _config.GuiItem.GpnEnableRecoveryWatch }`
    geçirir → izleyici gerçek runtime ayarıyla başlar.
  - Host aksiyonu `set_gpn_recovery_watch` → `SetGpnRecoveryWatchAsync`
    (config yaz + `PushSettingsAsync`); `DashboardMessagePolicy` iznine eklendi;
    `PushSettingsAsync` `connection.gpnEnableRecoveryWatch` taşır.
  - GPN panelinde toggle kartı (`gpnRecoveryWatchToggle`): `aria-checked` + knob
    durumu; `applySettings` config'den geri yansıtır. 9 dilde `gpn.recovery.*`.
  - **Testler:** `RunFailoverMonitor_RecoveryWatchDisabled_DoesNotRecover`
    (kapatılırsa düşüş terminal, onRecover çağrılmaz) + dashboard JS toggle testi
    (post + applySettings yansıması + off/on round-trip). 511+ C# / 35 dashboard JS.

  **✅ GpnTelemetryService — failover/kurtarma sayaç servisi (29 Ağu 2026):**
  `AppEvents.GpnResilienceChanged` akışını dinleyip olayları webview'a taşır:
  - Sayar: ServerSwitch, UdpDeath, ModeFallback (Tier-3 düşüşü), Recover
    (Tier-2 kurtarması) + ModeDecision (otomatik seçim); `Snapshot` thread-safe,
    `Reset()` ile oturum başına sıfırlanır. **Kurtarma döngüsü kapsanır**
    (RunFailoverMonitorAsync'in Recover/ModeFallback olayları bu akışa düşer).
  - DI singleton (`AddAoGpnGpnServices`). Testler için izole kanal ctor'u (`internal`).
  - MainWindow: `_gpnTelemetry` alanı; her resilience olayından sonra
    `PushGpnTelemetryAsync` ile `setGpnTelemetry` basılır; dashboard açılışında
    da tazelenir. Aksiyonlar: `get_gpn_telemetry` / `reset_gpn_telemetry`
    (policy iznine eklendi).
  - Dashboard GPN panelinde sayaç kartı: switch / death / fallback / recover /
    select rozetleri + toplam + `Sıfırla` butonu. 9 dilde `gpn.telemetry.*`.
  - **Testler:** `GpnTelemetryServiceTests` (4: tüm olay türleri, kurtarma
    döngüsü çifti, reset, bilinmeyen eylem) + dashboard JS sayaç/reset testi.
    517+ C# / 36 dashboard JS.

  **✅ GpnResilienceLog — son 50 karar döngüsel tamponu (29 Ağu 2026):**
  sorun giderme için GPN kararlarının döngüsel günlük tamponu:
  - `GpnResilienceLog`: `AppEvents.GpnResilienceChanged` dinler, kapasite 50
    (taşınca en eski düşer), her olayda tamponu `gpn_resilience.log` dosyasına
    yazar → dosya her an "son 50 karar" penceresi. `Recent`/`Count`/`Clear()`.
  - MainWindow: `_gpnResilienceLog` alanı + her olaydan sonra `setGpnResilienceLog`;
    aksiyonlar `get_gpn_resilience_log` / `clear_gpn_resilience_log` (policy).
  - Dashboard GPN panelinde "Son 50 karar" kartı (kaydırılabilir liste, sayı
    rozeti, dosya yolu, Yenile/Temizle). 9 dilde `gpn.reslog.*`.
  - **Testler:** `GpnResilienceLogTests` (4: Recent+dosya, 65 olayda 50-sınır +
    dosya yeniden yazımı, Clear, satır formatı) + JS reslog render testi.
  - **✅ “Son Olaylar” geçmiş paneli (29 Ağu 2026):** düz metin listesi, eylem
    türüne göre renk kodlu biriken olay satırlarına dönüştürüldü — `gpn-ev-switch`
    (amber), `gpn-ev-death` (kırmızı), `gpn-ev-fallback` (turuncu),
    `gpn-ev-recover` (yeşil), `gpn-ev-select` (camgöbeği) renkleri + düğme/dot;
    her satır yerelleştirilmiş rozet + WireGuard/V2rayTCP mod etiketi + zaman +
    sunucu→hedef·sebep detayı; en yeni olay `gpn-event-pop` animasyonuyla belirir.
    Eylem etiketleri `gpn.reslog.action.*` 9 dilde + fallback. Renk/meta mantığı
    `gpnEventMeta` (dashboard) aracılığıyla tek noktadan.

  **✅ Failover görsel geri bildirimi (29 Ağu 2026):** GpnResilienceEvent
  kararları telemetri kartlarına bağlandı:
  - `setGpnResilience` failover/kurtarma olaylarında Ping + Packet-Loss kartlarını
    çakar: `gpn-failover-flash` (turuncu, switch/UDP ölümü/düşüş) ve
    `gpn-recover-flash` (yeşil, kurtarma) CSS keyframe'leri ~1.1s; karar bağlamı
    `lossSub` alt satırına geçici yazılır. ModeDecision (başlangıç seçimi) flaşsızdır.
  - **Testler:** dashboard JS failover-flaş testi (kart sınıfları + lossSub bağlamı).
    166/166 JS, 38/38 dashboard.
- `.conf` diske geçici dosya olarak yazılırsa `File.SetAttributes(FileAttributes.Hidden)` + kullanım sonrası silme.
- `GpnServerProfile.ToConf()` iskelette hazırdır → hem `WireGuardTunnel.dll` (config dizesi) hem `wg-quick` (dosya) için kullanılır.

### 3.4 Çalışma zamanı akışı

```
Bağlan
  → WireGuardServerCatalog.Load() (DB + tohumlama)
  → GpnServerSelectionService.SelectBestServerAsync(adaylar)   // Faz 4
  → seçilen profil → WireGuardTunnelService.Start(profile)
      → conf dizesi → WireGuardTunnel.dll → Wintun adaptörü "AoGPN_tun" (MTU 1420)
```

---

## 4. Adım — Otomasyon + Ping Modülü (C# iskeleti hazır)

**Dosya:** `ServiceLib/Services/GpnServerSelectionService.cs` + `ServiceLib/Services/UdpHealthChecker.cs` + `ServiceLib/Enums/ConnectionMode.cs` + `ServiceLib/DI/GpnServiceCollectionExtensions.cs` (hepsi derlendi, testler ✓)

  **✅ Akıllı Düşüş (Smart Fallback) uygulandı (29 Ağu 2026):**
  - `ConnectionMode` enum: `WireGuardUDP` (Tier 2, öncelikli) / `V2rayTCP` (Tier 3, yedek).
  - `UdpHealthChecker` (IUdpHealthChecker): hedefin 51820/udp yoluna **bağlı UDP soketiyle** (UdpClient.Connect — ICMP hataları ConnectionReset olarak yüzeye çıkar) 1 baytlık probe; üçlü sonuç: `Open` / `Blocked` (ICMP Port Unreachable) / `NoResponse` (zaman aşımı).
  - `SelectBestServerAsync` 3 adım: (1) İtalya/Almanya'ya paralel ICMP → en düşük ping; (2) adayın UDP sağlık testi; (3) karar — UDP açıksa `WireGuardUDP`, bloklu/zaman aşımıysa `V2rayTCP` (fallback). ICMP tamamen engelliyse tüm sunucularda paralel UDP testi, ilk UDP-açık aday seçilir.
  - **WireGuard gerçeği:** 1 baytlık junk pakete sunucu yanıt vermez → NoResponse genellikle SAĞLIKLI yol demektir. `UdpHealthCheckOptions.TreatNoResponseAsBlocked` (varsayılan false) ile politika değiştirilebilir; katı davranış (zaman aşımı → V2rayTCP) istenirse true.
  - **DI:** `AddAoGpnGpnServices(IServiceCollection)` — `IUdpHealthChecker`, `IGpnServerSelectionService`, `NodePingCoordinator`, `IGpnConnectionLauncher`, `IGpnConnectionCoordinator` singleton kayıtları; ServiceProvider ile çözümleme testi mevcut (Microsoft.Extensions.DependencyInjection 10.0.8 eklendi).

  **✅ GpnConnectionCoordinator uygulandı (29 Ağu 2026):** "Bağlan" orkestratörü — `SelectBestServerAsync`'in mod kararına göre bağlantı kurar. `WireGuardUDP` → seçilen İtalya/Almanya sunucusunun tünelini `GpnCoreLauncher` ile başlatır (GpnServerProfile → WireGuard ProfileItem → BuildAll → CoreEngineHost) ve failover izleyicisini çalıştırır (onSwitch = sunucu taşıma, onModeFallback = V2rayTCP düşüşü). `V2rayTCP` → `ConfigHandler.GetDefaultServer` ile mevcut V2ray düğümünü başlatır. `GpnConnectionSnapshot`/`Snapshots` (BehaviorSubject) durumu UI/dashboard'a taşır; koordinatör AppManager'dan bağımsızdır (selector + launcher enjekte edilir, 7 test).
  - **Testler:** `GpnServerSelectionServiceTests` (17 test) — DecideMode + DecideFailover politikaları, yerel loopback UDP sunucularıyla Open/NoResponse/Blocked doğrulaması, DI çözümleme.

  **✅ Gerçek WireGuard Handshake Probe uygulandı (29 Ağu 2026):** `SelectBestServerAsync` (ve failover) artık 1 baytlık junk probe yerine **gerçek Noise_IKpsk2 el sıkışmasıyla kesin UDP kanıtı** alır:
  - `ServiceLib/Services/WireGuardNoise.cs` — wireguard-go'dan birebir port edilmiş istemci tarafı handshake initiation kriptosu (`CreateMessageInitiation` + `AddMacs`/MAC1). Tüm primitifler denetlenmiş kütüphanelerden: **X25519 + BLAKE2s** (BouncyCastle 2.5.0 eklendi), **ChaCha20-Poly1305** (.NET). El yazısı kripto yok.
  - `ServiceLib/Services/WireGuardHandshakeProbe.cs` — istemci özel anahtarıyla geçerli initiation paketi üretir, sunucuya gönderir, `Handshake Response`'u bekler. **Yanıtın MAC1'i, bu istemcinin statik genel anahtarına dayalı anahtarla doğrulanır** → sahte paketle taklit edilemez (`Open`). ICMP Port Unreachable → `Blocked`. Zaman aşımı → `NoResponse` (yük altındaki sunucu `cookie_reply` gönderir → o da `Open` sayılır).
  - `ProbeUdpAsync` routing: handshake `Open`/`Blocked` dönerse kesin sonuç olarak alınır; `NoResponse` ise bloklama sinyalini teyit etmek için junk probe'a düşülür. `GpnProbeOptions.UseWireGuardHandshakeProbe` (varsayılan true) / `HandshakeProbe` alt ayarlarıyla kontrol edilir; anahtar yoksa eski davranışa döner.
  - **Doğrulama (`WireGuardHandshakeProbeTests`, 12 test):** BLAKE2s RFC 7693 vektörleri, X25519 RFC 7748 §5.2 vektörü, ChaCha20-Poly1305 round-trip, KDF determinizmi, **iki taraflı el sıkışma round-trip** (responder, wireguard-go `ConsumeMessageInitiation`+`CreateMessageResponse`'u aynen yansıtır) ve loopback üzerinde gerçek UDP testleri (otoriter responder → `Open`, sessiz → `NoResponse`, yanlış sunucu anahtarı → `NoResponse` (MAC1 reddi), kapalı port → `Blocked`).

  **✅ Kapalı-port testleri sahte ICMP kanıtına taşındı (29 Ağu 2026):** `UdpHealthChecker` ve `WireGuardHandshakeProbe`'un kapalı-port testleri Windows ICMP rate-limit'ine (hedef IP başına Port Unreachable üretim hız sınırı; eski testler 127.0.0.2/3/4 arasında dolaşarak gerçek ICMP yakalamaya çalışıyordu) bağımlılıktan kurtarıldı. Yeni `ServiceLib/Services/UdpProbeSocket.cs`: `IUdpProbeSocket` soyutlaması + `UdpClientProbeSocket` (üretim varsayılanı) + `UdpProbeSocketFactory` delege — her iki probe sınıfına internal test enjeksiyon noktası (üretim kurucusu gerçek UdpClient'ı kullanmaya devam eder). Testler `FakeIcmpUnreachableSocket` ile **sahte ICMP kanıtı** (bağlı UDP soketinin ConnectionReset yüzeyi — ICMP Port Unreachable'ın Windows karşılığı) enjekte eder; `Blocked` artık ağ/IP/gerçek port kullanmadan deterministik üretilir. İlgili grup 15/15; tam süit 603 geçti / 4 bilinen temel.

  **✅ HandshakeNoResponse teşhisi — canlı gözlenen senaryo (29 Ağu 2026):** canlı testte geçerli el sıkışma gönderildiğinde sunucudan hiç yanıt alınmaması, `Blocked` (ICMP Port Unreachable kanıtı) ile karıştırılıp junk-probe `NoResponse`'uyla (beklenen WireGuard sessizliği) AYNI kovaya düşürülüyordu — yanlış teşhis: sessizlik "sağlıklı" sayılıp WireGuardUDP'ye bağlanılıyordu. Artık üç yol ayrı durumdur:
  - `Blocked` → **ICMP kanıtı VAR** (port kapalı / güvenlik listesi açıkça reddediyor) → V2rayTCP.
  - `HandshakeNoResponse` → geçerli el sıkışma yanıtsız, **ICMP kanıtı YOK**. Sağlıklı sunucu geçerli el sıkışmaya mutlaka yanıt verir; sessizlik junk-sessizliği DEĞİLDİR → güçlü bozukluk işareti (güvenlik listesi sessizce düşürüyor / wg0 kapalı / dönüş yolu bozuk / anahtar uyuşmazlığı). Varsayılan politika (`UdpHealthCheckOptions.TreatHandshakeNoResponseAsBlocked=true`) **V2rayTCP** düşüşü yapar; `false` ile WireGuardUDP korunabilir.
  - `NoResponse` → junk probe sessizliği (WireGuard junk pakete yanıt vermez) → varsayılan **sağlıklı** (WireGuardUDP), `TreatNoResponseAsBlocked=true` ise ölü.
  Teşhis zinciri (`ProbeUdpAsync`): el sıkışma yanıtsızsa junk probe ile ICMP teyidi — junk `Blocked` → kesin `Blocked` (detail: "el sıkışma da yanıtsızdı"), junk `Open` → `Open` (portta yanıtlayan hizmet var), junk `NoResponse` → `HandshakeNoResponse` (detail: "ICMP kanıtı yok"). Aynı politika failover/kurtarmaya taşındı (`IsUdpHealthyStatus`/`IsUdpDeadStatus` — katı handshake varsayılanı el sıkışma yanıtsız sunucuyu ölü sayar, junk NoResponse sağlıklı kalır). Dashboard'da `HandshakeNoResponse` kırmızı, GpnProbeTool'da "EL SIKIŞMA YANITSIZ" etiketi. Testler: probe (sessiz/yanlış anahtar → HandshakeNoResponse, anahtar eksik → NoResponse), DecideMode 3 yeni, failover 3 yeni, recovery 1, teşhis zinciri 4 (51/51 ilgili grup).

  **✅ Failover matrisi → dashboard (29 Ağu 2026):** `GpnServerSelectionService.EvaluateFailoverMatrix(active, candidates, ping, udp, options)` — AYNI canlı ölçümü İKİ politika altında yan yana değerlendirir (varsayılan: NoResponse sağlıklı/el sıkışma yanıtsız ölü; katı: ikisi de ölü). Kararlar saf `DecideFailover` ile üretilir (mantık çoğaltılmaz), satırlar `IsUdpHealthyStatus` bayraklarını taşır (aktif işaretli, ping/UDP durumu/iki sağlık sütunu). Yeni Models kayıtları: `GpnFailoverMatrix`/`Row`/`Policy` + `GpnFailoverAction` (kamu sözlüğü). Akış: `MainWindow.ProbeGpnServersAsync` ölçüm sonrası `ViewModel.EvaluateFailoverMatrix` (koordinatörle aynı önbellekli seçici) → `setGpnFailoverMatrix` → GPN panelinde "Failover matrisi" kartı: iki politika kutusu (Korunur / Geç→X / V2rayTCP düşüşü + neden) + sunucu satırları (ping, UDP rozeti, varsayılan/katı ✔/✘ sütunları). Aktif sunucu bağlıysa o, değilse en düşük ping'li aday. gpn.matrix.* 9 dilde. Testler: matris değerlendirme 4 (NoResponse default-Korunur/strict-düşüş, HandshakeNoResponse→Switch her iki politikada, aktif sıralama, UDP kapalı) + JS kart 1 (42/42 dashboard).

  **✅ En iyi aday kartı (29 Ağu 2026):** Ölçüm paneli (ping + UDP) ile GPN Bağlan'ın otomatik seçim kararı aynı görünümde birleştirildi. Seçim kararının "kuyruğu" saf `GpnServerSelectionService.DecideSelection(servers, ping, udp, options)` fonksiyonuna çıkarıldı — `SelectBestServerAsync` ile BİREBİR mantık: ping adayı varsa yalnızca onun UDP'si karar verir (başka sunucunun UDP'si denenmez), ping yoksa (ICMP engelli) ilk UDP-açık sunucu, o da yoksa V2rayTCP; etkin olmayan sunucuların ölçüm girdileri yok sayılır. Arayüze `EvaluateSelection` eklendi; `MainWindowViewModel.EvaluateSelection` geçidi; `MainWindow.ProbeGpnServersAsync` ölçüm sonrası `setGpnSelectionPrediction` basar (durum/mod adları string — renderer karşılaştırması için). GPN panelinde "En iyi aday" kartı: aday adı + ping + mod rozeti (WireGuard · Tier 2 / V2rayTCP · Tier 3) + neden satırı + sunucu satırları (ping, UDP rozeti, ● seçili işareti). gpn.candidate.* 9 dilde + fallback. Testler: DecideSelection 6 (ping+Open→Tier2, ping+Blocked→düşüş aday korunur, ping yok+UDP-açık→ilk açık sunucu, hiçbiri yok→null+V2ray, HandshakeNoResponse katı/gevşek, devre dışı asla aday) + JS kart 1 (43/43 dashboard); koordinatör FakeSelector arayüz değişikliğine uyarlandı. Tam süit 591 geçti / 4 bilinen temel.

  **✅ UDP destekli Failover uygulandı (29 Ağu 2026):** `RunFailoverMonitorAsync` artık her çevrimde (15 sn) ping yanında tüm adayların 51820/udp yolunu paralel `UdpHealthChecker` ile test eder. Saf `DecideFailover` karar fonksiyonu: aktif tünel ölüyse (Blocked / katı politikada NoResponse) önce UDP'si sağlıklı başka sunucuya geçer; hiçbirinde sağlıklı UDP yoksa `onModeFallback(ConnectionMode.V2rayTCP)` tetiklenir ve izleyici durur (Tier 3 düşüşü terminal). Aktif tünel sağlıklıyken yalnızca hysteresis marjını aşan ve UDP'si sağlıklı adaya geçilir; ölü UDP'li adaya asla geçilmez; ICMP tabanı yoksa (ISP engeli) geçiş kararı verilmez. `GpnProbeOptions.EnableUdpHealth=false` eski ping-tabanlı davranışa döner.

  **✅ Tier-2 Otomatik Kurtarma (onRecover) uygulandı (29 Ağu 2026):** V2rayTCP düşüşü artık terminal değil. `RunFailoverMonitorAsync`'e `onRecover` geri çağrısı eklendi; V2rayTCP'ye düşüldükten sonra `GpnProbeOptions.EnableRecoveryWatch` (varsayılan true) açıkken izleyici **kurtarma moduna** geçer ve her çevrimde adayların 51820/udp yolunu paralel `UdpHealthChecker` ile probe eder. Saf `DecideRecovery` sağlıklı (Open; varsayılan politikada NoResponse da sağlıklı) en düşük ping'li sunucuyu seçer ve `onRecover(targetServer, ct)` ile otomatik Tier-2 (WireGuard) dönüşü tetiklenir; izleyici normal failover'a devam eder. `EnableRecoveryWatch=false` veya `onRecover` null ise eski terminal davranış korunur. Koordinatör `onRecover`'ı `RecoverToWireGuardAsync`'a bağlar (durumu `Connected/WireGuardUDP`'ye çevirir). Testler: `DecideRecovery` (5 saf karar) + gerçek failover döngüsü (sahte `StagedUdpHealthChecker` ile V2rayTCP→kurtarma akışı) + koordinatör `onRecover`.

  **✅ Dashboard Karar Olayları (GpnResilienceChanged) uygulandı (29 Ağu 2026):** `ServiceLib/Models/GpnResilienceEvent.cs` (GpnResilienceAction: ServerSwitch / UdpDeath / ModeFallback / Recover / ModeDecision) — `AppEvents.GpnResilienceChanged` üzerinden yayınlanır. `GpnServerSelectionService` her karar anında yayınlar: sunucu değişimi (From→To sunucu + UDP durumu), UDP ölümü (Tier 3 düşüşünde aktife ait status), mod düşüşü, Tier-2 kurtarma ve `SelectBestServerAsync`'te başlangıç mod kararı. Dashboard köprüsü: `MainWindow` `GpnResilienceChanged`'a abone olur ve `window.setGpnResilience(json)` ile `Temalar/app.js`'e iletir (durum satırına karar gösterimi). Test: `RunFailoverMonitor_PublishesResilienceEvents_OnFallbackAndRecovery` — gerçek döngüde UdpDeath+ModeFallback+Recover üçünün de yayınlandığını doğrular.

### 4.1 Neler var

| Üye | Görev |
|---|---|
| `SelectBestServerAsync(servers, options, ct)` | "Bağlan" girişi: paralel ölç → en düşük ms adayı döndürür (`GpnSelectionResult` = en iyi + tüm ölçümler) |
| `ProbeAllAsync(...)` | min/avg/max/kayıp% — dashboard telemetrisi ve manuel test |
| `RunFailoverMonitorAsync(active, candidates, onSwitch, ...)` | 15 sn'de bir re-ölçüm; yeni aday ≥15 ms (hysteresis) iyi ise `onSwitch` |
| `GpnProbeMode` | `Auto` (önce ICMP, engellenirse TCP connect), `Icmp`, `Tcp` |
| `GpnServerProfile.ToConf()` | .conf üretimi (Faz 3 ile entegrasyon noktası) |

### 4.2 Tasarım kararları

- **ICMP:** `System.Net.NetworkInformation.Ping`, `DontFragment=true`, 32 bayt buffer, TTL 64 — fragmantasyon gecikmesi ölçüme karışmaz. MTU 1420 planını yansıtır.
- **Paralellik:** mevcut `NodePingCoordinator` (8 eşzamanlı) yeniden kullanıldı — yeni altyapı yok.
- **Failover salınımı:** `SwitchHysteresisMs` marjı olmadan iki sunucu birbirine yakınken sürekli geçiş olur; marj bunu önler.
- **Bağımsızlık:** sınıf `AppManager`'a bağlı değil → unit-test kolay (proje `xunit.v3` kullanıyor, `ServiceLib.Tests` deseni).
- **UDP handshake notu:** WireGuard öncesi handshake yok; gecikme metriği ICMP'dir. İleride sunuculara küçük bir HTTP health endpoint'i (`/ping`) eklersen `Tcp`/HTTP probe devreye sokulabilir — arayüz buna hazır.

### 4.3 MainWindowViewModel entegrasyonu (✅ tamamlandı, 29 Ağu 2026)

`MainWindowViewModel.Reload()` — uygulamanın tek "Bağlan" girişi (dashboard, tepsi hızlı bağlan ve profil değişimi buraya akar):

```
Reload():
  profileItem = GetDefaultServer(_config)
  if (profileItem.ConfigType == WireGuard && TUN açık)
      candidates = WireGuardServerCatalog.LoadAsync()   // tüm saklı WireGuard profilleri (İtalya/Almanya)
      snapshot = GetGpnCoordinator().ConnectAsync(candidates)
         // paralel ICMP → en düşük ping → gerçek WG el sıkışma (UDP kanıtı)
         // → WireGuardUDP/V2rayTCP kararı → seçilen sunucuya bağlan + failover izleyici
      gpnConnected = true
  if (!gpnConnected)
      // mevcut tek-profil (build all → LoadCore) akışı — WireGuard değilse hiç değişmiyor
```

Yeni parçalar:
- `ServiceLib/Services/WireGuardServerCatalog.cs` — kullanıcının içe aktardığı WireGuard
  `ProfileItem`'larını (`.conf`/URI) `GpnServerProfile` adaylarına eşler (Faz 3 katalogunun
  yer tutucusu). Eksik anahtar/uç nokta varsa profil atlanır; MTU 1420 / keepalive 25 varsayılan.
- Tek örnek `GpnConnectionCoordinator` (`GpnServerSelectionService` + `GpnCoreLauncher`) —
  failover izleyicisi ve `Snapshots` durumu süreç boyunca korunur.
- Kritik güvenlik kapısı: GPN akışı YALNIZCA seçili profil bir **WireGuard** profili ve **TUN
  açık**ken tetiklenir; VLESS/SS seçiliyken mevcut tek-profil akışı birebir korunur (davranış
  değişmez). Aday yoksa veya bağlantı başarısızsa otomatik olarak eski akışa düşülür.
- **Kritik teardown düzeltmesi (tam entegrasyon, 29 Ağu 2026):** GPN koordinatörü artık düzgün
  durduruluyor — kullanıcı WireGuard+GPN modundan VLESS/SS profiline ya da TUN kapalıya geçince
  eski WireGuard tüneli ve failover izleyicisi çalışmaya devam etmiyor. `Reload()` GPN dalını
  başlatmadan önce aktif koordinatörü temizler (izleyiciyi + başlatıcıyı durdurur); `ConnectAsync`
  zaten temiz başlangıç yaptığından tekrar bağlanma idempotent ve güvenli.
- **teardown unit testi (29 Ağu 2026):** karar, ağır UI/kurucu bağımlılıkları olmadan test
  edilebilmesi için `StopGpnCoordinatorWhenNotConnectedAsync(gpnConnected, coordinator)`
  (internal static) içine alındı; `Reload()` bunu `_gpnCoordinator` ile çağırır.
  `MainWindowViewModelGpnTeardownTests` (3): GPN→VLESS/SS geçişinde koordinatör durur
  (DisconnectAsync çağrılır), GPN aktifken durmaz, koordinatör null ise güvenli no-op —
  sahte `IGpnConnectionCoordinator` ile (524/528 geçer, kalan 4 bilinen temel).

**Belirgin "GPN Bağlan" butonu (✅ tamamlandı):** Seçili profil WireGuard dışı olsa bile (ör.
VLESS/SS seçiliyken) kullanıcı GPN modunu açıkça seçtiğinde İtalya/Almanya otomatik seçimini
zorla tetikler. Genel CONNECT butonunun profil-bağımlı `Reload()` dalından bağımsızdır:
- `MainWindowViewModel.GpnConnectAsync()` — GPN koordinatörünü seçilen profile bakmaksızın
  çalıştırır; zaten bağlıysa bağlantıyı keser (toggle) ve TUN kapalıysa önce TUN'u etkinleştirip
  kalıcılaştırır. `GpnConnectCmd` ReactiveCommand'a bağlı.
- `MainWindow` WebView köprüsüne `gpn_connect` eylemi eklendi (`DashboardMessagePolicy` izin
  listesine de) → `RunGpnConnectAsync()` (bağlantı kapısı + core-leaving-Starting eşitlemesi).
- Dashboard GPN paneline degrade, tam genişlikte bir "GPN Connect" butonu (`gpnConnectBtn`);
  tıklamada `{action:'gpn_connect'}` gönderilir, `window.setGpnConnectState` rozetini günceller ve
  `setGpnResilience` ModeDecision başarılı seçimi rozete (WG / V2rayTCP) yansıtır. 9 dilde yeni
  `gpn.connect`/`gpn.connectSub` anahtarları.

**Canlı sunucu + gecikme + mod gösterimi (✅ tamamlandı):** Bağlan sırasında
`ModeDecision` resilience olayı (sunucu adı + `DelayMs` + mod) artık yalnızca durum
satırını değil, StatusBar'ı ve dashboard telemetrisini de canlı günceller:
- `MainWindow.PushGpnResilienceAsync` — `ModeDecision` geldiğinde `StatusBarViewModel`
  `RunningServerDisplay`/`TrayStatusLine` değerlerini `Sunucu · gecikme · mod` formatında
  ayarlar (TrayStatusState=2) ve `window.setGpnConnectionInfo({server,delayMs,mode})`
  gönderir (yeni JS köprüsü, ağ çağrısı tekrarlanmaz).
- `Temalar/app.js setGpnConnectionInfo` — telemetri Ping kartını (ölçülen gecikme) ve
  oturum düğümünü (sunucu + mod) günceller; `V2rayTCP` düşüşünde gecikme ölçülemediği
  için Ping `--` kalır.
- Testler: dashboard entegrasyonuna `setGpnConnectionInfo` (Ping kartı + oturum düğümü)
  ve `gpn_connect` butonu (rozet + kilit) testleri eklendi (30/30 geçti).

Doğrulama: `WireGuardServerCatalogTests` (5 test — TryMap eşlemesi, eksik anahtar/public key
atlama, MTU/keepalive varsaylanı) + mevcut GPN/el sıkışma testleri (56). Uygulama projesi derlendi.

### 4.4 Canlı sunucu teşhis aracı (CLI, ✅ tamamlandı 29 Ağu 2026)

`AoGPN/AoGPN.GpnProbeTool` — platform-nötr (net10.0) konsol aracı; üretim koduna dokunmadan
canlı İtalya/Almanya sunucularını "Bağlan" akışının ölçtüğü her aşamayla test eder:
ICMP gecikme (`GpnServerSelectionService.ProbeAllAsync`), junk-UDP sağlık (`UdpHealthChecker`),
**gerçek WG el sıkışması** (`WireGuardHandshakeProbe`) ve otomatik seçim
(`SelectBestServerAsync`). Anahtarlar gömülü değildir — argüman olarak verilen `.conf` yolundan
`WireguardFmt.ResolveConfig` ile okunur; ortak eşleme `WireGuardServerCatalog.TryMap` üzerinden
paylaşılır (artık public). Dönüş kodu: en az bir sunucuda açık UDP yolu varsa 0.

**✅ Failover karar matrisi eklendi (29 Ağu 2026):** `--failover` bayrağı, canlı
ping+UDP sonuçlarını alır ve her aday sunucuyu sırayla "aktif tünel" varsayıp
`GpnServerSelectionService.DecideFailover`'ı çalıştırarak karar matrisini yazdırır
(UDP durumu + ping + eylem: SUNUCU DEĞİŞ → hedef / V2RAYTCP DÜŞÜŞÜ / DEĞİŞİM YOK + gerekçe).
`--strict-udp` ile NoResponse'un ölü sayıldığı katı politika senaryosu da çalıştırılabilir.
Bunun için `DecideFailover`/`FailoverActionType`'ın `internal` erişimi araç projesine
açıldı (`InternalsVisibleTo AoGPN.GpnProbeTool`).
Canlı test fix (29 Ağu 2026): `WireGuardServerCatalog.TryMap(ProfileItem)`, conf'tan gelen
profillerin `IndexId`'i boş kaldığında `ServerId`'yi uç noktadan (`host:port`) türetiyor — önceden
iki sunucunun da ServerId'si boştu ve `--failover` matrisi `ToDictionary` anahtar çakışmasıyla
çöküyordu. İndexId doluysa korunur (geriye dönük uyum).

```
AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf
AoGPN.GpnProbeTool .freebuff/aogpn-client.conf --no-handshake --no-select
AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf --failover
AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf --failover --strict-udp
AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf --monitor 10
AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf --jsonl
AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf --monitor 10 --jsonl
AoGPN.GpnProbeTool .freebuff/aogpn-client.conf .freebuff/aogpn-client-alman.conf --jsonl --webhook https://ntfy.sh/topic
```

**✅ Uzun süreli ön izleme + JSONL + CI notifier (29 Ağu 2026):**
- `--monitor [N]` — her N sn otomatik seçimi tekrar ölçer ve zaman damgalı satır basar;
  sunucu değişimi + mod düşüşü/kurtarma olaylarını ayrı zaman damgalı satır olarak gösterir
  (Ctrl+C ile durur).
- `--jsonl` — NDJSON çıktı: her olay tek satır JSON (camelCase) → `server_probe`,
  `selection`, `failover`, `monitor_sample`, `monitor_event`, `summary`. İnsan metni bastırılır,
  makine tüketimi için `jq -s` / CI uyumlu.
- `--webhook URL` — bitişte `summary` özetini URL'ye POST eden notifier; hata çalışmayı
  DÜŞÜRMEZ (exit kodu değişmez, stderr'e uyarı). `--strict-udp` artık hem karar matrisini hem
  `SelectBestServerAsync` mod kararını etkiler (`TreatNoResponseAsBlocked` probeOptions'a verildi).
- **CI:** `.github/workflows/gpn-servers-health.yml` — 30 dk'da bir canlı sunucuları `--jsonl`
  ile ölçer; UDP yolu doğrulanamazsa işi düşürür ve (secret `GPN_WEBHOOK_URL` verilirse)
  webhook üzerinden bildirir. WireGuard özel anahtarları repoya girmez — conf'lar `ITALY_CONF_B64` /
  `GERMANY_CONF_B64` secret'larından runner geçici dizinine açılır.

**✅ JSONL tüketici + Prometheus exporter (29 Ağu 2026):** `AoGPN.GpnJsonlConsumer`
  (platform-nötr konsol aracı) GpnProbeTool `--jsonl` akışını tüketir:
- `ServiceLib/Services/GpnJsonlConsumer.cs` — saf çekirdek: `TryParseServerProbe`
  (server_probe satırını türlü kayda çevirir, bozuk/ilgisiz satırlar sessizce atlanır),
  `EnrichLine` (herhangi bir JSON satırına `ingestedAt` ekler — selection/failover/
  monitor_event dahil TÜM olaylar arşivlenir), `GpnPrometheusProbeState` (sunucu başına
  son ölçüm, thread-safe).
- Kullanım: `GpnProbeTool --jsonl [--monitor N] <confs> | GpnJsonlConsumer [seçenekler]`:
  - `--log probes.jsonl` — BOM'suz UTF-8, `ingestedAt` zaman damgalı JSONL arşivi (jq -s uyumlu),
  - `--prometheus-out gpn.prom` — her güncellemede metrik dosyasını atomik yeniden yazar
    (node_exporter `--collector.textfile` deseni),
  - `--prometheus-listen 9101` — `GET /metrics` üzerinden exposition'ı HTTP ile sunar
    (bağımlılıksız mini HTTP ucu; `--file` ile log tekrar oynatma da var).
- Metrikler: `gpn_ping_ms{server,name}` (en iyi ICMP ms; -1 = başarısız) ve
  `gpn_udp_status{server,name,status}` (one-hot: gözlenen durum 1, diğerleri 0 — Open/Blocked/
  NoResponse/HandshakeNoResponse sabit sırayla). Yalnızca `server_probe` metrikleri besler;
  selection/failover satırları yalnızca loga gider. Etiket değerleri Prometheus kaçış kurallarına göre
  kaçışlanır. Testler: parse/zenginleştirme/durum/exposition 12 (603 geçti / 4 bilinen temel).
- **Test altyapısı düzeltmesi:** `GpnCaptureSettingsTests` artık `DisableParallelization`
  koleksiyonunda — `BindAppManagerConfig` (AppManager.Instance._config'i yansıma ile değiştirir)
  paralel koleksiyonlarla yarışıyordu; sıralı koleksiyon Bind→DI-çözüm penceresini güvenceye alır.

---

## 5. Numaralı Geliştirme Adımları (uygulama sırası)

1. **Faz 1 — WireGuard outbound (✅ tamamlandı):** `SingboxOutboundService.BuildWireGuardEndpoint` (userspace endpoint, keepalive), `WireguardFmt` PersistentKeepalive parse'ı, Xray `keepAlive` paritesi. İtalya/Almanya `.conf`'ları `WireguardFmt.ResolveConfig` ile içe aktarılır (UI import akışı zaten var). Mevcut GPN kuralları + `stack="system"` TUN ile binary doğrulaması yapıldı. **Kalan: canlı sunucuyla uçtan uca test (config'i seç, Bağlan, wg handshake'i sunucuda `sudo wg show` ile doğrula).**
2. **Faz 2a — Motor altyapısı:** `WireGuardTunnelService` (WireGuardTunnel.dll P/Invoke sarıcı) + `GpnTargetResolver` (PID tazeleme).
   > **Natif katman keşfi:** kalan 34 klasik `[DllImport]` (7 dosya) + planlanan Wintun/WireGuardTunnel
   > bağlayıcıları için tam envanter ve LibraryImport dönüşüm kontrol listesi
   > `docs/native-pinvoke-migration.md`'de — Faz 2b bağlayıcıları doğrudan source-generated yazılır.

   **✅ GpnTargetResolverBridge + PID havuzu kartı (29 Ağu 2026):** `GpnTargetResolverBridge`
   SplitTunnelViewModel'in Game Boost listesindeki **vpn eylemli** oyunlarından (app türünde,
   `Action=="vpn"` → `ProcessName`/`Value`) hedef exe adlarını toplar, `GpnTargetResolver`'ı canlı
   (5 sn) çalıştırır ve PID kümesi/her durum değişiminde `GpnPidPoolSnapshot` (TargetNames, Pids,
   Version, ResolvedAt, Watching, TargetRunning) yayınlar. Hedef adları her turda yeniden okunur —
   oyun değişince en geç bir sonraki turda yeni resolver kurulur; hedef kapalıysa boş küme + offline
   durumu (eski set korunmaz). `Start/Stop/RefreshNow` kontrolü; iç test kurucusu (apps sağlayıcısı)
   ile ağır VM kurucusu gerekmeden test edilir. MainWindow: köprü `ViewModel.ConnectionViewModel`'e
   bağlanır, `SnapshotChanged` → `setGpnPidPool`; host aksiyonları `gpn_pid_pool_start/stop/refresh`
   (policy) + kapanışta `Dispose`. Dashboard GPN panelinde **PID havuzu kartı**: hedef rozetleri,
   canlı PID listesi, watching/idle/offline rozetleri + İzle/Durdur/Yenile butonları. `gpn.pidpool.*`
   9 dilde + fallback. Testler: bridge 5 (vpn seçimi + alt süreç PID'leri, boş havuz, hedef değişince
   yeniden kurulum, start/stop + Stop son sözü, yalnızca değişimde yayın) + JS kart testi (40/40
   dashboard, 543/547 C#).
   **✅ Toolhelp32 FATAL durumu + geri dönüş (29 Ağu 2026):** `ProcessTreeStatus` (Healthy/Degraded/Fatal)
   + `IProcessTreeSource.Status`; `NativeProcessTreeSource` artık tanımlı bir FATAL durumu taşır —
   Toolhelp32 başarısız olursa yedek `Process.GetProcesses()` (Degraded: parent'sız ağaç), yedek de
   fırlatır/boş dönerse **FATAL** (süreç ağacı erişilemez, hiçbir karar güvenilir değil). Yedek
   fırlatmaları yakalanır (önceden resolver'a kaçıp sessizce null'a düşüyordu). Test seam'leri
   (`TrySnapshotNative`/`CollectFallbackNative` protected virtual + `NativeEntry` public) ile
   gerçek sürücü gerekmeden senaryolar üretilir. `GpnTargetResolver.LastSourceStatus` — null dönüşü
   "hedef kapalı" ile "Fatal"den ayırır; köprü `GpnPidPoolSnapshot.SourceStatus`'u taşır → dashboard
   PID havuzu kartında **kırmızı fatal** / **amber degraded** rozetleri (`gpn.pidpool.fatal/degraded`
   9 dilde). Testler: source 4 (Healthy tam ağaç, Degraded yedek, Fatal-fırlatma, Fatal-boş) + resolver
   3 (Fatal yüzeyleme, Healthy çözüm, Enumerate fırlatması → Fatal) + JS fatal/degraded rozet testi
   (550/554 C#).

   **✅ WinDivert motor iskeleti — `ServiceLib/Services/Gpn/` (29 Ağu 2026, 16 test):**
   - `WinDivertNative` — `WinDivertOpen/Recv/Send/Close/SetParam` P/Invoke; **`CallingConvention.Cdecl`** (WinDivert eksportları __cdecl — StdCall olursa çöker).

     **✅ Modern .NET LibraryImport (source-generated) + OpenEx (29 Ağu 2026):** eski `[DllImport]`/`Marshal.Copy`
     yolu, derleme zamanında üretilen `[LibraryImport]` (source-generated) P/Invoke'a taşındı — her çağrı için
     yükte sabit, marshaller spike'ı olmayan natif çağrı dizisi üretir. Detaylar:
     * `partial` sınıf/metodlar + `[UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]` (cdecl burada verilir);
       ANSI filtre için `StringMarshalling.Custom + AnsiStringMarshaller` (varsayılan UTF-8 WinDivert'i bozardı).
     * Hata okuma `Marshal.GetLastPInvokeError()`; `ServiceLib.csproj`'a `AllowUnsafeBlocks=true` eklendi (üretilen
       kod işaretçi kodu kullanır).
     * **`WinDivertOpenEx` (v2.2+)** desteği eklendi: `WINDIVERT_OPEN_PARAMS` eşdeğeri blittable
       `WinDivertOpenParams` struct'ı (katman/yön indeksli `QueueLen/Time/Size[4]`, slot = layer*2+direction),
       `WINDIVERT_FLAG_*` sabitleri (Sniff / RecvOnly / SendOnly / NoInstall / NoQueue / FragmentIgnore /
       UseSequenceNumbers / QueueLength/Time/Size) ve `IWinDivertApi.OpenEx`/`WinDivertEngine.OpenEx(filter, ps,
       layer, priority, flags)`. `OpenEx` kuyruk-MTU/tampon ince ayarı ve sniff/receive-only modları için; klasik
       `Open` davranışı korunur. Testler: OpenEx çağrı yolu + flags, sürücü yoksa hata, zaten açıkken red, struct
       natif boyutu 52 bayt ve varsayılan sıfırlama.
     **✅ Varsayılan sniff-önce akışı (29 Ağu 2026):** `GpnCaptureLoop` artık WinDivertEngine.OpenEx tabanlı
     **sniff moduyla başlar** — paketleri **yakala ve bırak** (WINDIVERT_FLAG_SNIFF: kopyalanır ama yığında
     ilerler, trafik bozulmaz, asla enjeksiyon yapılmaz). `VerifyPacketCount` paket görülünce (veya
     `SniffTimeout` dolunca) doğrulama biter ve döngü **recv-only'ye geçer** (WINDIVERT_FLAG_RECV_ONLY:
     paketler yığından çıkarılır, yalnızca okunur, WinDivertSend yok — tünel egress'i). Yeni:
     `WinDivertEngine.OpenSniff/OpenRecvOnly(filter, openParams, extraFlags)` (FlagSniff/FlagRecvOnly OR'lanır),
     `GpnCaptureMode` + `GpnCaptureOptions` (SniffFirst=true varsayılan, VerifyPacketCount=1, SniffTimeout=10 sn,
     kuyruk OpenParams, ExtraFlags), varsayılan recv-only inject = **tüketim** (consume — bump-reinject kaldırıldı;
     Faz 2b WireGuardTunnelService bu hattı şifreleyip tünel soketine bağlar). `SniffedPacketCount`/
     `ConsumedPacketCount` gözlem sayaçları. Testler: sniff önce gözlemleyip recv-only'ye geçiş, sniff'te
     enjeksiyon yok, sniff zaman aşımıyla da geçiş, recv-only pompası, PID yeniden derleme, OpenSniff/
     OpenRecvOnly flag'leri + ExtraFlags OR (GpnCaptureTests + WinDivertEngineTests 34/34).
     **✅ Kullanıcı ayarlarında WinDivertOpenParams (29 Ağu 2026):** yeni `Config.GpnCaptureItem`
     ayar bloğu (ConfigHandler.LoadConfig'te `??= new()` varsayılanı) — queue len/time/size değerleri +
     etkinleştirme bayrakları, WinDivert katmanı (0=NETWORK/1=NETWORK_FORWARD) ve yön (0=in/1=out),
     priority. `GpnCaptureSettingsMapper` (pure): ayarları `WinDivertOpenParams`'a çevirir
     (slot = layer*2+direction, CurrentLayer/CurrentDirection=1, `WinDivertOpenParams.SetQueue*`
     yardımcıları), `Enable*` bayraklarından `WINDIVERT_FLAG_QUEUE_LENGTH/TIME/SIZE` bitlerini üretir ve
     `GpnCaptureOptions` (OpenParams + ExtraFlags) kurar. DI: `IGpnCaptureSettingsProvider`
     (AppManager config'inden okur) + `GpnCaptureOptions` fabrika kaydı + `WinDivertEngine` singleton —
     `GpnCaptureLoop` bağlantı anında bu options'ı DI'dan çözer. Testler: varsayılan slot (0,1)→1
     8192/1000/0, özel katman/yön slotu, flag bitleri, provider config okuma + eksikte varsayılan,
     DI çözümleme (GpnCaptureSettingsTests 7 + DI 1, 542 süitte 538 geçer).
     **✅ Paket başına telemetri → dashboard (29 Ağu 2026):** `GpnCaptureLoop` artık her yakalanan
     paketi sayaçlıyor (`GpnCaptureOptions.EnableTelemetry=true` varsayılan, `TelemetryTickInterval` 1 sn).
     Yeni `GpnPacketStats` (saf yönetilen IPv4/IPv6 başlık çözücü: protokol/yön/eş uç noktası/yerel port),
     `GpnCaptureTelemetry` (Interlocked skalerler + kilitli top-akış ve per-PID haritaları) ve anlık görüntü
     `GpnCaptureStatsSnapshot` (TopFlows / ByPid / yön-protokol-byte sayaçları). Per-PID atfı ağ katmanında
     PID vermediği için **UDP port→PID köprüsü**: `WindowsNetworkTable.GetUdpPortOwners()` (GetExtendedUdpTable
     v4+v6) → `IGpnPortPidTable/GpnPortPidTable`; dışa giden UDP paketinin kaynak portu sahibine atfedilir ve
     havuz dışı (kaçan) trafik `InPool=false` olarak işaretlenir. Yayın: `AppEvents.GpnCaptureStatsChanged`
     (tik döngüsü ~1 sn'de port haritasını tazeler + snapshot yayınlar) → MainWindow `PushGpnCaptureStatsAsync`
     → `window.setGpnCaptureStats` → dashboard'da **"Yakalanan trafik" kartı** (toplam/out/in/udp/tcp/byte
     çipleri, top akışlar, per-PID satırları — havuz dışı kırmızı ⚠ rozeti; gpn.capture.* 9 dilde). Testler:
     GpnPacketStats 10 (v4/v6 UDP/TCP/ICMP, IHL seçenekleri, inbound flip, bozuk paketler), telemetri 7
     (toplamlar, akış sıralama+cap, per-PID havuz bayrağı, reset), port tablosu 2, döngü 3     (kayıt+tik yayını,
     havuz dışı atfı, telemetri kapalıyken yayın yok) — ilgili grup 30/30, tam süit 574'te 570 geçer;
     dashboard JS 41/41, sözlük/stil 44/44.
     **✅ WinDivert kuyruk kartı (29 Ağu 2026):** `GpnCaptureItem` ayarları GPN panelinde
     düzenlenebilir — saf `GpnCaptureSettingsPatch.Apply` (aralık sınırlama: queueLen ≥1, queueTime ≥1,
     queueSize ≥0; enable bayrakları/koruma; `GpnCaptureOptions` dönüşümü) + host aksiyonları
     `get_gpn_capture_settings` / `set_gpn_capture_settings` (config'e yazar, bir sonraki yakalama
     başlangıcında uygulanır) → dashboard **"WinDivert kuyruk" kartı** (len/time/size girişleri + enable
     onay kutuları + katman/yön seçicileri, Apply → set_*; gpn.captureSet.* 9 dilde). Testler: patch
     uygulama/sınırlama/koruma (GpnCaptureSettingsTests 12/12), JS kart 1 (render + Apply POST + null
     bozulma koruması) — dashboard 45/45, tam süit 622/626 (4 bilinen temel).
     **✅ Bağlantı-anı DI çözümü entegrasyon testi (29 Ağu 2026):** `GpnCaptureOptions` DI kaydı
     `AddSingleton` → `AddTransient` yapıldı — provider'ın `CaptureOptions`'ı her erişimde canlı
     config'den türetir; singleton olsaydı `set_gpn_capture_settings` patch'i ilk çözümden sonra
     bir sonraki açılışa asla yansımazdı (kuyruk değişiklikleri kaybolurdu). Entegrasyon testi:
     config A (layer 1 / yön 0 → slot 2, QueueSize açık) → DI'dan options → loop sniff+recv-only'yi
     slot 2 + FlagQueueSize ile açar; patch (layer 0 / yön 1, QueueSize kapalı) sonrası ikinci
     bağlantı TAZE options'la slot 1 + FlagQueueLength ile açılır (FlagQueueSize yok). `FakeDivertApi`
     artık her OpenEx'e verilen OpenParams'i de kaydeder. GpnCaptureTests sıralı koleksiyona alındı
     (AppManager config rebind yarışı). İlgili grup 26/26, tam süit 623/627 (4 bilinen temel).
   - `WinDivertFilterBuilder` (pure) — `outbound and udp and (ip) and (processId == … or …)`; IPv4/IPv6 ve tünel uç noktası dışlama (`!(ip.DstAddr == X …)`, canlı döngü önlemi).
   - `DivertedPacket` + `WinDivertAddress` (bağlı UDP/WFP adresini Send'e geri verir).
     **✅ Gerçek 2.x layout + sürücü smoke (29 Ağu 2026):** `WinDivertAddress` eski 32-baytlık
     (IfIdx/SubIfIdx başta, Timestamp YOK) düzenden resmi include/windivert.h layout'una taşındı —
     **80 bayt** (x86/x64 aynı): `INT64 Timestamp` (0) + `UINT32` bitfield depolama (8: Layer bit 0-7,
     Event 8-15, Sniffed 16, Outbound 17, Loopback 18, Impostor 19, IPv6 20, IP*Checksum 21-23,
     Reserved1 24-31 — MSVC LSB sırası) + `UINT32 Reserved2` (12) + 64-bayt union (16: Network
     IfIdx/SubIfIdx, Flow/Socket 5-tuple, Reflect, Reserved3). Eski layout Timestamp'i hiç okumuyordu
     — canlı paketlerde tüm alanlar 8 bayt kayık okunuyordu (yön/arayüz bozuk olurdu). Kolaylık
     özellikleri (Layer/Event/Direction/Sniffed/Loopback/…/IsOutbound) Flags üzerinden; `WinDivertNative`
     adres bit maskeleri. Layout testleri (sürücüsüz, 9/9): boyut 80, Marshal.OffsetOf tüm alanlar,
     union paylaşımı (IfIdx/SubIfIdx == FlowEndpointId), bit-packing round-trip (0x00FF0201 sabiti),
     byte-byte marshal golden (field offset'lerine birebir). **Sürücü smoke testi**
     (`WinDivertDriverSmokeTests`, `Category=DriverSmoke`): gerçek WinDivert.dll + sürücüye karşı
     loopback UDP üretip sniff yakalayıp Timestamp≠0 / Layer=0 / Outbound / Loopback / IfIdx≠0 doğrular;
     `AOGPN_SMOKE_DRIVER=1` ile açılır (yönetici + WinDivert.dll test bin'inde), yoksa xUnit v3
     `Assert.Skip` ile atlanır — CI etkilenmez. Tam süit 632/636 (4 bilinen temel) + 1 atlanan smoke.
     **✅ Yakala→enjekte round-trip testi (29 Ağu 2026):** `CaptureToInject_RoundTrips_Full2xAddress`
     — DivertWorker gerçek engine üzerinden Recv ile TAM 2.x adresi doldurur (Timestamp, Layer bitfield,
     Outbound/Loopback/IPChecksum, IfIdx=11, SubIfIdx=22), paket+adres kanaldan çıkar, `engine.Send`
     aynen geri enjekte eder. İddialar: IfIdx/SubIfIdx, IsOutbound (bit 17), Layer, Timestamp, Loopback,
     IpChecksum sentAddress'te korunur; ayrıca 80-bayt adres yakalamadan enjeksiyona BIT-BIT aynı
     (marshal round-trip) — eski 32-baytlık düzen IfIdx/yönü 8 bayt kayık okuduğu için bu, yeni
     layout'un kanal/enjeksiyon yolunda bozulmadığını kanıtlar. Tam süit 643/649 (4 bilinen temel
     + 2 atlanan smoke).
   - **Sürücülü yakala→enjekte round-trip smoke (29 Ağu 2026):** `CaptureToInject_LoopbackRoundTrip`
     — sahte API YOK, gerçek WinDivert.dll + sürücü. Adımlar: 127.0.0.1'e UDP dinleyici bağla →
     recv-only handle ile dar filtre (`loopback and udp and outbound and udp.DstPort == {port}` —
     makinedeki diğer loopback trafiği asla yakalanmaz) → istemci datagramı gönder → paket yığından
     ÇIKARILIR (dinleyici henüz almaz) → yakalanan adres doğrulanır (Layer=NETWORK, Outbound, Loopback,
     IfIdx≠0, Timestamp≠0) → yakalama handle'ı kapatılır (filtre kalkar — sonsuz yakala→enjekte
     döngüsü yok; recv-only handle'da Send zaten başarısız olurdu) → AYNI adresle (IfIdx/Outbound
     korunarak) ayrı "false" filtreli handle'dan `engine.Send` ile geri enjekte → **hedef UDP soketi
     payload'ı alır** (teslimat = enjeksiyon adresinin doğru olduğunun kesin kanıtı) + `RemoteEndPoint.Port`
     istemcinin kaynak portuyla eşleşir (ulaşan paket AYNI pakettir, yeni değil). Tam süit 669/676
     (4 bilinen temel + 3 atlanan smoke: sniff + wintun + round-trip).
   - `WinDivertEngine` (IDisposable/IAsyncDisposable) — Open/Send/Close; `Marshal.AllocHGlobal/FreeHGlobal` ile dengeli güvenli marshaling (unsafe blok yok); sürücü yoksa dostça `WinDivertException`; `DisposeAsync` → worker durdur → handle kapat → sürücüyü serbest bırak.
   - `DivertWorker` — ayrı arka plan iş parçacığında bloklayan `WinDivertRecv` döngüsü; yakalanan saf IP paketlerini **sınırlı (bounded) Channel** aracılığıyla sıraya koyar (geri basınç, tüketici tünel hızına ayak uydurur).
     **✅ Faz 2b — WireGuard tünel köprüsü + Wintun (29 Ağu 2026):** `WintunNative` (wintun.dll,
     LibraryImport/stdcall — resmi api/wintun.h ile birebir): adapter create/open/close, LUID,
     sürücü sürümü, session başlat/sonlandır, halka tampon alım/gönderim
     (AllocateSendPacket → kopyala → SendPacket; ReceivePacket/Release), okuma-bekleme olayı;
     `WintunException` (DLL/sürücü/yönetici dostça hatalar). `IWintunSession` + `WintunSession`
     sarmalayıcısı (ERROR_BUFFER_OVERFLOW/NO_MORE_ITEMS normal akış). **`WireGuardTunnelService`:**
     `Open` (adapter+session, ring 2'nin katına yuvarlanır) → `InjectPacket` (önce
     `IWireGuardTransport.Encrypt`, sonra adaptöre yaz) → `RunCaptureBridgeAsync` (DivertWorker
     kanalını tüketir: yakalanan oyun paketleri tünele/adaptöre akar; telemetri Captured/Injected/
     InjectFailed) → `RunReceiveLoopAsync` (adaptörden çözüp tüketiciye — ör. WinDivert Send ile
     oyuna dönüş) → `CreateInjectHandler` (GpnCaptureLoop'a inject hattı olarak verilebilir).
     Şifreleme dikişi `IWireGuardTransport` (varsayılan Noop — WireGuardNoise ilkelleriyle gerçek
     veri düzlemi Faz 2c'de aynı arayüze bağlanır; köprü/telemetri değişmez). DI:
     `AddSingleton<IWireGuardTransport, NoopWireGuardTransport>` + `AddSingleton<WireGuardTunnelService>`.
     Sürücüsüz testler 10/10 (köprü sırası, dolu-tampon atlama, iptal temiz kapanış, şifreleme
     öneki, decrypt hatası sayacı, Dispose). **Sürücü smoke** (`WintunDriverSmokeTests`, opt-in):
     benzersiz adapter + 10.88.0.2/24 (netsh) + listener → IPv4/UDP paketi `InjectPacket` ile →
     OS yığınına teslim (payload round-trip) — enjeksiyon köprüsü gerçek sürücüde uçtan uca
     doğrulanır; temizlik session/adapter + netsh. Tam süit 642/648 (4 bilinen temel + 2 atlanan
     smoke: WinDivert + Wintun).
   - **Bağlan akışı entegrasyonu — `GpnCaptureBridge` (29 Ağu 2026):** köprünün canlıya
     alınması. `GpnCaptureBridge` (ServiceLib/Services/Gpn) bağlantı anında `GpnCaptureLoop`'u
     `WireGuardTunnelService.CreateInjectHandler` ile kurar — yakalanan oyun paketleri varsayılan
     `ConsumeInjectAsync` yerine tünele şifrelenip Wintun adaptörüne enjekte edilir; ters yön
     (adaptörden dönen paketler) çözülüp tüketiciye verilir. Hedef oyun çalışmıyorsa / hedef ad
     listesi boşsa / sunucu yoksa (V2rayTCP) köprü BAŞLATILMAZ — çekirdek bağlantısı bozulmaz.
     `GpnCoreLauncher`'a 4. ctor parametresi olarak bağlandı: `LaunchAsync(WireGuardUDP, server)`
     çekirdek sonrası `StartAsync(server)` çağırır, `StopAsync` önce köprüyü durdurur — sunucu
     değişimi/düşüş/kurtarma akışları köprüyü otomatik takip eder. `MainWindowViewModel`
     `GpnTargetResolverBridge.ExtractTargetNames(ConnectionViewModel.Apps)` ile hedef adları verir
     (aynı kaynak kod — köprü ile 5 sn izleme asla ayrışmaz). DI: `GpnCaptureBridge` +
     `IGpnConnectionLauncher` factory kaydı. **Yakalanan gerçek kusur:** gerçek `GpnTargetResolver`
     `RefreshLoopAsync` İLK anlık görüntüyü de üretir; `GpnCaptureLoop` bunu gördüğünde az önce
     açtığı handle'ı kapatıp yeniden açıyor, ilk paketleri kanala yazılmış worker'ı süpürüyordu
     (bağlantı anında paket kaybı) — artık PID kümesi aynıysa yeniden açılış atlanır. Testler:
     `GpnCaptureBridgeTests` 8/8 (oyun çalışıyor → 2 paket tünele enjekte, Stop temiz kapanış;
     oyun yok / hedef yok / sunucu yok → başlamaz; idempotent Start/Stop; ters yön tüketici teslimi;
     **launcher entegrasyonu**: sahte runtime + geçici sing-box binary kökü ile `LaunchAsync` →
     köprü canlı, `StopAsync` → köprü durdu). Tam süit 651/657 (4 bilinen temel + 2 atlanan smoke).
   - **Wintun ayarları kullanıcıya taşındı — `GpnWintunItem` + GPN paneli "Wintun adapter" kartı
     (29 Ağu 2026):** adapter ad ön eki + halka tampon kapasitesi artık config'de
     (`Config.GpnWintunItem`; varsayılan "AoGPN" + 4 MiB). `GpnWintunSettingsMapper` (saf):
     adapter adı yalnızca [A-Za-z0-9_-] (32 karakter, boş → varsayılan); kapasite 128 KiB..64 MiB
     aralığına sınırlanır ve 2'nin katına yuvarlanır (Wintun halka tampon gereksinimi).
     `GpnWintunSettingsPatch.Apply` dashboard yükünü config'e uygular (gönderilmeyen alan korunur).
     `IGpnWintunSettingsProvider` (AppManager config'inden). `GpnCaptureBridge` köprü açılışında
     bu options'ı kullanır: adapter = "{ön ek}-{sunucu id}" (örn. FastTun-it), ring = kapasite;
     `WireGuardTunnelService.LastRingCapacity` açılışta kaydedilir (doğrulama/telemetri). Host:
     `get_gpn_wintun_settings` / `set_gpn_wintun_settings` aksiyonları (DashboardMessagePolicy'e
     eklendi) — WinDivert kuyruk kartının birebir deseni; uygulanan değerler round-trip ile geri
     basılır. Dashboard: WinDivert kuyruk kartının altına "Wintun adapter" kartı (ön ek + ring
     kapasite girişleri + slot çipi + Apply). `gpn.wintunSet.*` 9 dilde + fallback. Testler:
     `GpnWintunSettingsTests` 18/18 (varsayılanlar, özel değerler, sanitleştirme teorileri,
     kapasite sınırlama/yuvarlama teorileri, patch null/uygulama, **köprü açılışı**: FastTun + 8 MiB
     → adapter "FastTun-it" + LastRingCapacity 0x800000). JS dashboard 46/46 (+1: Wintun kartı
     render + Apply POST + round-trip + boş/geçersiz girdi null koruması). Tam süit 669/675 (4
     bilinen temel + 2 atlanan smoke); WPF 0 hata/uyarı.
   - **✅ Faz 2c — Gerçek WireGuard veri düzlemi (29 Ağu 2026):** `IWireGuardTransport`'a
     WireGuardNoise ilkelleriyle gerçek şifreleme bağlandı. **`WireGuardDataPlane`**
     (ServiceLib/Services/Gpn): `WireGuardSession` (tip-4 transport mesajları — type/receiver/
     counter başlığı, 16'ya padding, IP uzunluk alanına göre kırpma, yön bazlı anahtarlar +
     counter + RejectAfterMessages) + **`WireGuardReplayFilter`** — wireguard-go replay/replay.go
     (RFC 6479) portu: 128 blokluk halka tampon, `old != new` bit kontrolü (ilk counter 0 kabul;
     eski tasarımın `ulong.MaxValue` sentineli ilk paketi reddediyordu — bu kusur testte
     yakalandı). **`WireGuardHandshakeClient`** (initiator Noise_IKpsk2): initiation üretimi +
     response tüketimi (MAC1 + transkript/Empty doğrulama — gerçek sunucu kanıtı) +
     **cookie reply** (XChaCha20-Poly1305, MAC2'li yeniden gönderim). **`WireGuardNoiseTransport`**
     (IWireGuardTransport): `ConnectAsync` — sunucuya karşı el sıkışma (cookie retry dahil,
     max 3 deneme × 4 sn pencere), sonrası Encrypt/Decrypt oturum üzerinden tip-4 mesajlar.
     **Kritik düzeltme:** `WireGuardNoise.BuildInitiation` chainKey'i `InitialChainKey`'den
     başlatıyor (eski sürüm `InitialHash` kullanıyordu — canlı "handshake-no-response"
     belirtisinin olası nedeni: MAC1 geçer, sunucu tarafı static decrypt başarısız). BouncyCastle
     **2.5.0 → 2.7.0** (XChaCha20-Poly1305 — cookie şifrelemesi, el yazısı kripto yok). Köprü
     bağlama: `WireGuardTunnelService.UseTransport` (session kapalıyken taşıma değişimi) +
     `GpnCaptureBridge` — bağlantı anında profilin anahtarlarıyla gerçek taşımayı kurar
     (`transportFactory` dikişi; bozuk anahtar → köprü başlamaz, çekirdek etkilenmez), el
     sıkışma arka planda (`ObserveTransportConnectAsync` — Bağlan akışını bloklamaz, oturum
     gelene dek paketler injectFailed sayılır). DI varsayılanı Noop kalır. **Testler 9/9:**
     loopback UDP iki taraflı el sıkışma (gerçek responder host), oturum anahtarı eşleşmesi
     (cross-decrypt), transport wire-format round-trip (sunucu yankısı), tamper/replay/wrong-
     type-receiver reddi, cookie retry (MAC2'li ikinci initiation). Köprü +2 test (gerçek
     taşıma kurulumu, bozuk anahtar ret). Tam süit 680/687 (4 bilinen temel + 3 atlanan smoke);
     WPF 0 hata/uyarı.
   - **✅ Faz 2d — Kalıcı UDP veri yolu (29 Ağu 2026):** el sıkışma sonrası UDP soketi
     artık kapanmıyor. `IWireGuardTransport` veri yolu yüzeyi kazandı: `IsDataPathActive`,
     `SendAsync(byte[] wire)` (tip-4 → GERÇEK sunucuya), `RunReceiveLoopAsync(consumer,…)`
     (sunucudan gelen tip-4'ler UDP üzerinden alınır → çözülür → tüketiciye).
     `WireGuardNoiseTransport`: `ConnectAsync` başarılı olunca soket `_udp` alanında tutulur
     (başarısızlıkta kapatılır); `SendAsync` oturum/soket yokken false + `SendFailedCount`;
     `RunReceiveLoopAsync` oturum gelene dek bekler (köprü alım döngüsü el sıkışmadan önce
     başlayabilir), tip-4'leri çözer, keepalive (boş) dahil tüketiciye verir; `IDisposable`
     soketi kapatır (tünel `Close()`'ta çağırır). **Akış değişti:** giden yol artık Wintun'a
     değil gerçek sunucuya gider — `WireGuardTunnelService.InjectPacketAsync` = Encrypt →
     `transport.SendAsync` (UDP); alım yolu = `transport.RunReceiveLoopAsync` → Decrypt →
     `InjectIntoAdapter` (Wintun → OS → oyuna) + tüketici; 0-bayt keepalive adaptöre
     enjekte edilmez. Telemetri: `GpnTunnelTelemetrySnapshot` + `Sent`/`SendFailed`,
     `DecryptFailed` artık taşıma sayacından (DecryptFailedCount). Testler: tünel servisi
     9/9 yeni modelde (Encrypt→Send sırası, veri yolu kapalı ret, kanal sırası, reddedilen
     gönderim atlama, alım→çöz→consumer+Wintun, decrypt hatası sayacı, keepalive enjekte
     edilmez); köprü 11/11 (giden Sent, alım yolu queue-transport); `WireGuardNoiseTransport`
     +3 veri yolu (loopback SendAsync+ReceiveLoop round-trip — gerçek UDP yankısı; oturum
     yokken SendAsync false; döngü el sıkışmadan önce başlayıp oturum gelince canlı). Wintun
     smoke `InjectIntoAdapter` üzerinden (alım yolu köprüsü). Tam süit 686/693 (4 bilinen
     temel + 3 atlanan smoke); WPF 0 hata/uyarı. **Kalan:** gerçek sunucuya karşı canlı
     veri-yolu doğrulaması (köprü + Wintun sürücüsüyle uçtan uca) ve keepalive gönderimi
     (PersistentKeepalive=25 sn).
   - **✅ Akıllı Düşüş BUGFIX'i — seçici artık ikinci adayı deniyor (29 Ağu 2026, canlı):**
     Canlı İtalya/Almanya testinde gerçek kullanıcı belirtisi doğrulandı: "GPN modunda
     bağlanamıyorum". Kök neden: `SelectBestServerAsync` yalnızca EN DÜŞÜK ping'li TEK adayın
     UDP yolunu test ediyordu; o aday ölüyse (bloklu / handshake-no-response) ikinci aday HİÇ
     denenmeden doğrudan V2rayTCP'ye düşülüyordu. Canlı tanı (GpnProbeTool): makine İtalya
     tünelinin İÇİNDEYDİ (10.66.66.2 aktif, genel IP 92.4.220.236) → İtalya el sıkışması
     hairpin nedeniyle yanıtsız (sunucu tarafı: peer kayıtlı ✔, 51820 dinliyor ✔, firewalld
     açık ✔ — sunucu sağlıklı, sorun yalnızca tünel-içi hairpin) → Almanya el sıkışması AÇIK
     (MAC1 doğrulandı). Eski seçici İtalya'yı seçip V2rayTCP'ye düştü; Almanya hiç denenmedi.
     Düzeltme: tüm adayların UDP yolu paralel ölçülür (ProbeUdpAllAsync), ping sırasına göre
     İLK WireGuardUDP-uygun aday kazanır; yalnızca HİÇBİR aday uygun değilse V2rayTCP
     (Best=null). `DecideSelection` (dashboard "en iyi aday" kartı) birebir güncellendi —
     tahmin ile gerçek seçim asla çelişmez. Testler: +2 yeni (en düşük ping'li ölü → ikinci
     aday WireGuardUDP; hepsi ölü → Best=null V2rayTCP) + handshake-no-response senaryoları
     güncellendi (katı: ikinci adaya kay; gevşek: en düşük ping korunur). Canlı doğrulama
     (JSONL): `selection best=130.61.223.36:51820 mode=WireGuardUDP udp=Open` — Almanya
     seçildi, tünel kurulabilir. Tam süit 682/689 (4 bilinen temel + 3 atlanan smoke).
   - **✅ Failover Smart Fallback (29 Ağu 2026):** `RunFailoverMonitorAsync`'in aktif sunucu ölümünde
     artık doğrudan V2rayTCP'ye düşmek yerine diğer adayın UDP yolunu kontrol ediyor: aktif sunucu
     ölüyse → tüm adayları ping sırasına göre test et → ilk WireGuardUDP-uygun adaya geç (hiçbiri
     uygun değilse V2rayTCP). Testler: 4 yeni (aktif ölüm → diğer aday WireGuardUDP'de canlı → geçiş;
     aktif ölüm → ikinci de ölü → V2rayTCP; aktif canlı ama ikinci daha sağlıklı → geçiş; aktif
     canlı → değişiklik yok). Tam süit 690/697 (4 bilinen temel + 3 atlanan smoke); WPF 0 hata/uyarı.
   - **✅ Hairpin teşhisi — tünel-içi öz-erişim (29 Ağu 2026):** `SelectBestServerAsync` artık hedef
     sunucunun genel IP'si makinenin KENDİ genel IP'siyle eşleşiyorsa `HAIRPIN` uyarısı basar (log +
     `GPN_SELECT hairpin=...` diyagnozu) ve o sunucuyu aday sırasının SONUNA atar. Canlı gözlenen
     senaryonun kökten çözümü: makine İtalya tünelinin içindeyken İtalya sunucusunun yanıltıcı düşük
     ping'i (tünel-içi rota) artık Almanya'nın önüne geçemez. Kendi genel IP'si ölçümlerle PARALEL
     çözülür (constructor `ownPublicIpProvider` enjektabl — testler sabit verir; üretimde 60sn
     önbellekli HTTP çözücü api.ipify→ifconfig→icanhazip, 1.5sn zaman aşımı, ağ hatası seçimi
     düşürmez). Ortak saf sıralama `OrderCandidates` (hairpin sona → başarılı ping önce → düşük
     gecikme) hem `SelectBestServerAsync` hem dashboard `DecideSelection` tarafından kullanılır —
     tahmin ile gerçek seçim hairpin'de de çelişmez. Testler: +11 (IsHairpin eşleşme/hayır theorileri,
     OrderCandidates hairpin-sona-at + ping sırası, DecideSelection hairpin deprioritize, hairpin-tek-
     aday son umut). Tam süit 701/708 (4 bilinen temel + 3 atlanan smoke); WPF 0 hata/uyarı.
   - **✅ GPN akışına foreign-tunnel koruması (29 Ağu 2026, canlı teşhis):** Kullanıcı tipine
     göre — "bağlantı kuruldu görünüyor ama IP değişmiyor" — canlı teşhiste çalışan tünelin
     RESMİ WireGuard uygulamasına (adaptör `Almanya-client` 10.66.66.2 + `wireguard.exe` süreçleri)
     ait olduğu, AOGPN'nin kendi tünelinin hiç kurulmadığı doğrulandı; daha önceki "Almanya çıkışı"
     kanıtı da bu yabancı tünelden geçiyordu. Düzeltme: `GpnCaptureBridge.StartAsync` artık AOGPN
     kendi Wintun tünelini açmadan ÖNCE `ResolveForeignTunnelBeforeConnectAsync` çağırıyor —
     `ForeignTunnelDetector` ile dışarıdaki VPN istemcilerini (resmi WG uygulamasının wireguard.exe)
     tespit edip kapatıyor (kill) ve kapatılamayan kalıntıları (unknown TUN adaptörü, dolu proxy
     portu) diyagnoza düşürüyor; koruma best-effort, tünel açılışı asla engellenmez. Detektöre
     enjekte edilebilir kill delege'si + `ResolveForeignClientsAsync` eklendi (test edilebilir,
     gerçek süreç öldürülmez). AOGPN'nin kendi adaptörü (`{prefix}-{serverId}`, `tun` içermediği
     için) asla yabancı sanılmaz. Testler: +5 (Resolve kill/clean/tun-only, köprü: yabancı
     wireguard.exe → önce kill sonra tünel aç, temiz → kill yok). Tam süit 706/713 (4 bilinen
     temel + 3 atlanan smoke — 2'si wireguard.exe canlıyken ortam-bağımlı FD testi); WPF 0 hata/uyarı.
     ⚠️ **2026-09-03:** otomatik kill kaldırıldı — dedektör yalnızca tespit/uyarı yapar
     (`KillForeignClients`/`ResolveForeignClientsAsync` silindi; bkz.
     `docs/foreign-vpn-kill-removal-roadmap.md`).
   - **✅ Bayat bağlantı sıyırma — WireGuard köprüsü (29 Ağu 2026):** `GpnCaptureBridge.StartAsync`
     kendi Wintun tünelini canlıya aldıktan SONRA hedef süreçlerin (seçili oyun/"vpn" eylemli
     uygulamalar) tünel açılmadan ÖNCE kurulmuş mevcut TCP bağlantılarını keser
     (`NetworkConnectionFlusher.KillActiveConnectionsForProcesses` — iphlpapi DELETE_TCB) ki
     yeniden kendi tüneli üzerinden kurulsun; aksi halde bayat soketler eski yoldan sızar ve IP
     eski görünür (tarayıcı yenilemesiz). Enjekte edilebilir `connectionFlusher` delege'si
     (testler sahte verir), arka planda koşar, hata asla köprüyü düşürmez — best-effort; teardown
     `GPN_BRIDGE flush killed=... ms=...` diyagnozu. Testler: +2 (flush çağrılır doğru hedeflerle,
     flush hatası bağlantıyı bozmaz). Tam süit 708/715 (4 bilinen temel + 3 atlanan smoke); WPF
     0 hata/uyarı.
   - **✅ IP DOĞRULAMA paneli — bağlantı sonrası periyodik yeniden ölçüm (29 Ağu 2026):**
     Bağlanma anında tünel henüz el sıkışmayı bitirmeden alınan bayat ölçüm (ISP IP'si) artık
     yanlış "sızıntı" uyarısı olarak 30 sn asılı kalamaz. C#: (1) bağlantı geçişinde ANINDA
     `CheckIpAsync` + doğrulanana dek ~6 sn'de bir yeniden ölçüm (`_lastTunnelVerified` → hızlı
     3-tick / doğrulayınca 15-tick ≈ 30 sn), (2) her `setRealIpState` yüküne `measuredAt` (ISO)
     zaman damgası. JS: (3) bağlıyken 25 sn'de bir host'a `check_ip` isteyen kendi kendini
     yenileme döngüsü (`startIpRecheckLoop`), (4) ölçüm yaşını `ip.measuredAgo` ile ayrıntı
     satırında gösterir, (5) bağlıyken "sızıntı" ölçümü 45 sn'den bayatsa kırmızı `Leaking`
     yerine amber `Stale measurement — re-checking` gösterir — bayat ölçüm asla güncel gerçek
     gibi sunulmaz, host yeniden ölçünce kırmızı döner. Yeni i18n `ip.measuredAgo` +
     `ip.detail.staleMeasure` 9 dilde. Testler: JS +1 (tazelik eki, taze/bayat sızıntı düşüşü,
     re-measure ile dönüş — sandbox saatiyle deterministik) + interval beklentisi güncellendi
     (bağlantıda 2 interval); dashboard 47/47, tüm JS 175/175; tam süit 710/717 (4 bilinen temel
     + 3 atlanan smoke); WPF 0 hata/uyarı.
   - **✅ GPN oturum denetim günlüğü (29 Ağu 2026):** VPN bağlanınca/kopunca canlı iletişim
     kesildiğinden bağlantı/test sürecinin TAMAMI diske yazılır: `%LocalAppData%\AoGPN\gpn-session.log`.
     `GpnSessionLog`: oturum blokları (START trigger → env anlık görüntüsü → adımlar → END reason
     + elapsed), son 5 oturumu tutar (kırpma), best-effort I/O. DiagLog AYNASI: `ao_diag.txt`'e
     yazılan HER satır (GPN_SELECT / GPN_LAUNCH / GPN_BRIDGE / GPN_TUNNEL / GPN_FAILOVER /
     GPN_RECOVER / FLUSH / TUN_*) oturum dosyasına da düşer; ilk satır oturumu otomatik açar
     (first-log). Oturum sınırları: `GpnConnectionCoordinator.ConnectAsync` (Begin — ortam
     anlık görüntüsü: OS/arch/admin, socks-port, adaptörler+IPv4'ler, rotalar — yabancı tünel
     `Almanya-client` logdan tek başına görülür) + `DisconnectAsync`/app-exit (End). Yeni
     `GPN_IPVERIFY` satırı: CheckIpAsync ölçümünü (connected/transport/direct/tunnel/isp/verified)
     günlüğe yazar. Okuma aracı `AoGPN.GpnSessionTool`: `--path` / `--last` / `--steps` /
     `--sessions` (+ `--file`). Testler: +6 (oturum yapısı, auto-begin, idempotent begin,
     no-op end, 5-oturum kırpma, DiagLog aynası) — sıralı koleksiyon `GpnSessionLogSerial`
     (küresel statik yol yarışı) + ModuleInitializer test yönlendirmesi. Tam süit 716/723
     (4 bilinen temel + 3 atlanan smoke); WPF 0 hata/uyarı.
3. **Faz 2b — Entegrasyon:** `IGpnTransport` arayüzü; `CoreManager.LoadCore` → transport seçimi; `TunLifecycleManager` genelleştirme; `ForeignTunnelDetector` güncelleme; `NetworkConnectionFlusher`'ı motor başlangıcında yeniden kullan (eski soketler yeni tünele bağlansın).
4. **Faz 3 — Sunucu kataloğu:** `gpn_servers` tablosu, `Sample/` tohumlama, DPAPI anahtar koruması, `GpnServerProfile.ToConf()` bağlama.
5. **Faz 4 — Otomasyon:** `GpnServerSelectionService`'i `MainWindowViewModel` bağlan akışına bağla; dashboard'a iki sunucunun canlı gecikmesini göster; failover'ı etkinleştir.
6. **Testler:** `ServiceLib.Tests` içinde `GpnServerSelectionServiceTests` (sahte probe ile seçim/failover), `GpnServerProfileTests` (ToConf doğruluğu), motor için gerçek makinede manual doğrulama senaryoları (EFT/LoL canlı test).

   **Sunucu doğrulama betiği (29 Ağu 2026):** `.freebuff/gpn-server-verify.sh` — İtalya/Almanya sunucularını SSH üzerinden otomatik doğrular: `wg show wg0` peer listesinde istemci pubkey eşleşmesi (çalışma anı) + `wg0.conf [Peer]` eşleşmesi, latest-handshake yaşı (tünel hiç kuruldu mu), `ss` ile 51820/udp dinleme, host firewall (firewalld→ufw→iptables otomatik algılama) ve canlı UDP probe (nc + `--live` ile tcpdump kesin güvenlik listesi kanıtı). `--server italy|germany`, `--json` (JSONL), `--verbose` seçenekleri; sunucu adresleri/kullanıcılar/anahtarlar env ile ezilebilir; istemci pubkey'leri conf PrivateKey'inden türetilir (yerel `wg`) veya varsayılan gömülüdür. Sahte SSH ile uçtan uca test edildi (pubkey `=` ayrıştırması dahil).

   **Tünel uçtan uca doğrulama (29 Ağu 2026):** `.freebuff/tunnel-e2e-verify.sh` — Almanya tüneli AKTİF iken tek komutla 4+1 kontrol: (1) tünel adaptörü (ad + 10.66.66.x IPv4 — ipconfig satır tabanlı ayrıştırma), (2) rota tablosu (varsayılan rota tünel IP'sinden metric 0 mı — `route print`), (3) dış IP çıkışı (3 hizmetten genel IP == 130.61.223.36 mı), (4) el sıkışma yaşı (SSH ile `sudo wg show wg0 latest-handshakes` — sunucu tarafı; eşik 180 sn) + (5) `wg transfer` trafik bilgisi. `--json` (JSONL), `--verbose`, `--no-ssh` seçenekleri; GERMANY_* / EXPECTED_EXIT_IP / HANDSHAKE_MAX_AGE_S env ile ezilebilir. Canlı Almanya tünelinde doğrulandı: adaptör `Almanya-client` 10.66.66.2 ✔, rota metric 0 ✔, çıkış 130.61.223.36 ✔, el sıkışma 30-84s önce ✔, çıkış kodu 0.

## 5b. Canlı sızıntı teşhisi (30 Ağu 2026) — Global VPN'de IP değişmiyor

Kullanıcının ekran görüntüleri: "Global VPN · AKTİF · Tüm Trafik Tünelleniyor" ama IP hâlâ
Tanzanya (41.59.13.39). `gpn-session.log` teşhisi 3 kök neden ortaya çıkardı:

1. **Aktif routing profilindeki bayat catch-all:** `guiNDB.db` `RoutingItem` (IsActive=1,
   "V4-全局(Global)") kuralı "AoGPN Manuel varsayılan 0-65535 → **direct**" içeriyordu — bir GPN
   whitelist uygulamasından kalma. Global VPN'de bu, TÜM trafiği tünel dışına (direct) gönderiyordu
   (`CORE_OUT ... taking detour [direct] for [tcp:www.google.com:443]` logda birebir görüldü).
   **DB düzeltildi:** `0-65535 → proxy` (yedek `/tmp/guiNDB-backup.db`).
2. **`SplitTunnelViewModel.ApplyAsync` admin kontrolü routing yazımını atlıyordu:** TUN gerekliyken
   admin değilse `NeedAdmin → return` `SaveRulesAsync`'e hiç ulaşmadan dönüyordu — eski modun
   catch-all'i (direct) yerinde kalıyordu. **Düzeltme:** `SaveRulesAsync` admin kontrolünden ÖNCE
   çalışır (routing yazımı yükseltme gerektirmez; UI hâlâ TUN için admin ister).
3. **Xray 26 `allowInsecure` kaldırıldı:** `V2rayOutboundService` hâlâ `allowInsecure` yazıyordu
   (`CORE_CHECK Xray ... success=False` + "The feature allowInsecure has been removed"). **Düzeltme:**
   alan hiç yazılmıyor (null → JSON'dan atlanır); `pinnedPeerCertSha256` / `verifyPeerCertByName`
   zaten destekleniyor.
4. **`BuildFinalRule` balancer şartlıydı:** balancer yokken son kural (0-65535 → proxy) eklenmiyordu;
   eşleşmeyen trafik sızabiliyordu. **Düzeltme:** final kural her zaman eklenir (first-match-wins
   kullanıcı kurallarını korur).

**Testler (+3):** `Xray_TlsOutbound_DoesNotEmitAllowInsecure_Xray26Compatibility` (ham JSON'da
`allowInsecure` yok), `Xray_AlwaysAppendsFinalProxyRule_EvenWithoutBalancer` (son kural proxy),
`StaleDirectCatchAllInActiveProfile_IsManagedAndRewrittenToProxy_ForGlobalVpn` (bayat direct
catch-all Global VPN'de proxy'ye yazılır). Tam süit 719/726 (4 bilinen temel + 3 atlanan smoke).

## 6. Riskler

| Risk | Önlem |
|---|---|
| WinDivert + anti-cheat (EFT BattlEye) uyumluluğu | Motoru kullanıcı alanında tut, kernel callout yazma; WinDivert zaten köklü ve anti-cheat dostu kabul edilen araçlardandır; önce tek oyunda pilot test |
| ICMP engellenen ISP'ler | `GpnProbeMode.Auto` TCP connect düşüşü |
| Oyun alt süreçleri (PID drift) | `GpnTargetResolver` 3-5 sn tazeleme döngüsü |
| Wintun sürücü imzası / yönetici yetkisi | Mevcut yükseltme akışı (`RebootAsAdmin`), wintun.dll'yi `bin/` içinde dağıt (sing-box paketinde zaten var) |
| İki tünel çakışması (v2rayN + AoGPN) | `ForeignTunnelDetector` kendi adını (`AoGPN_tun`) beyaz listeye alır |

## 7. Referanslar

- Sunucu kurulumu: `docs/wireguard-oracle-routing.md`, `.freebuff/aogpn-server-setup.sh`
- İki sunucu geçiş rehberi: `docs/wireguard-multi-tunnel-windows.md`
- Xray TUN araştırması: `docs/xray-tun-windows.md`
- GPN routing üreticisi: `ServiceLib/Services/CoreConfig/Singbox/SingboxRoutingService.cs`
- Ping koordinatörü: `ServiceLib/Services/NodePingCoordinator.cs`
- Yeni modül: `ServiceLib/Services/GpnServerSelectionService.cs`
