# Oyun Odaklı Bağlanma Performansı — Denetim ve Düzeltme Planı

Tarih: 2026-09-11 · Kapsam: AoGPN GPN (WireGuard + mihomo TUN) bağlanma yolu
Amaç: (1) açılışta yapılabilecek her işi açılışa taşımak, (2) bağlanma yolunu
yalnızca bağlantı için gerekli işlerden oluşan saf bir hat haline getirmek,
(3) "oyunda hızlı bağlanma + düşük ping" hedefine muhalefet eden her şeyi
saptayıp önceliklendirilmiş bir düzeltme planına dökmek.

Bu belge **analiz + plandır**; burada kod değişikliği yapılmamıştır.

---

## 0. Ölçülen temel gerçek

Canlı tanılama loglarından (2026-09-11) iki bağımsız bağlanma ölçümü:

| Ölçüm | Başlangıç | Bağlandı | Süre |
|---|---|---|---|
| Oturum 1 | 09:55:53.867 | 09:56:04.751 | **10.88 s** |
| Oturum 2 | 09:56:44.012 | 09:56:53.873 | **9.86 s** |

Yani "Bağlan" niyetinden tünelin ayakta olmasına kadar **~10 saniye** geçiyor.
Oyun senaryosunda bu, maç ortasında kabul edilemez bir kör pencere demektir.

> **Bilgi boşluğu (ilk iş):** Bu 10 saniyenin ne kadarı sunucu ölçümüne, ne
> kadarı çekirdek başlatma/el sıkışmaya gidiyor, şu an ölçülmüyor. Aşağıdaki
> P0-1 maddesi bu ayrımı görünür kılar; diğer her şeyin önceliği buna bağlıdır.

---

## 1. Bağlanma yolundaki darboğazlar

### A1 — Bağlanma isteği her seferinde tüneli yıkıp yeniden kuruyor (P0)

`GpnConnectionCoordinator.ConnectAsync` (`ServiceLib/Services/GpnConnectionCoordinator.cs:~150-210`)
şu sırayı **koşulsuz** izler:

```
CancelDrain();
GpnSessionLog.BeginSession(...);
StopMonitor();
await _launcher.StopAsync(ct);     // ← çalışan tüneli yıkar
await _selector.SelectBestServerAsync(...);  // ← 10 sn'lik ölçüm
await _launcher.LaunchAsync(...);  // ← yeniden başlat
```

Tek istisna "yumuşak geçiş" bloğudur ve o blok **yalnızca
`preferred.ServerId != Snapshot.Server.ServerId` olduğunda** çalışır. Yani:

- Zaten **aynı** sunucuya bağlıyken gelen bir bağlanma isteği → tünel yıkılır,
  aynı sunucu yeniden ölçülür, yeniden kurulur.
- `preferred` **null** ise (seçili profil WireGuard değilse; ör. VLESS/SS seçiliyken
  otomatik GPN akışı) yumuşak geçiş bloğu tamamen atlanır → bağlı olsa bile sıfırdan kurulum.

Bunun önemi: `MainWindowViewModel.Reload()` her çalıştığında
`TryRunGpnConnectAsync()` çağrılır (`MainWindowViewModel.cs:~1037`). `Reload()`
ise şu olaylarla tetiklenir:

- `ProfilesViewModel` / `StatusBarViewModel` / `CheckUpdateViewModel`
  `ReloadRequested` akışları (`MainWindowViewModel.cs:~284-299`)
- Abonelik otomatik güncellemesi tamamlandığında (`UpdateTaskHandler`, satır ~370+)
- Mod / TUN değişiklikleri, bölgesel ön ayar, `F5`
- **Her dakika koşan `TaskManager.UpdateTaskRunSubscription`** zinciri
  (`ServiceLib/Manager/TaskManager.cs:35-75` → `SubscriptionHandler.UpdateProcess`
  → `UpdateTaskHandler`)

**Sonuç:** maç ortasında abonelik güncellenirse veya herhangi bir reload
tetiklenirse çalışan tünel sıfırdan kurulur. Kullanıcının "bağlanıyor / kopuyor /
tekrar bağlanıyor" algısının en güçlü kaynağı budur.

**Yapılacak:** `ConnectAsync` başına idempotentlik kapısı:

```
if (Snapshot.State == Connected
    && Snapshot.Mode == WireGuardUDP
    && (preferred is null || preferred.ServerId == Snapshot.Server.ServerId))
    → hedef karşılanmış; hiçbir şey yapma, mevcut snapshot'ı döndür
    → DiagLog: "GPN_LOG connect no-op (already connected, server=…)"
```

`ConnectionTogglePolicy` ile aynı felsefe: **niyet zaten karşılanmışsa dokunma.**

---

### A2 — Ölçüm fazları sıralı; paralelleştirilebilir (P0)

`GpnServerSelectionService.SelectBestServerAsync`
(`ServiceLib/Services/GpnServerSelectionService.cs:~350-500`):

1. `await coordinator.RunAsync(requests, timeout)` → **tüm adaylara ICMP**
   (`Samples` × (`PerSampleTimeoutMs` + 50 ms ara))
2. `await _prober.ProbeUdpAllAsync(enabled, options, ct)` → **tüm adaylara
   WireGuard el sıkışma probe'u** (`WaitTimeoutMs`, `MaxAttempts: 2`)

İki faz birbirinden bağımsızdır (ayrı soketler, ayrı sonuçlar) ve **art arda**
beklenir. Toplam gecikme ≈ ICMP fazı + UDP fazı.

**Yapılacak:** `Task.WhenAll` ile iki fazı birleştir; karar zaten ikisinin
birleşiminden üretiliyor (`GpnDecision.OrderCandidates` + `DecideMode`). Beklenen
kazanç: en yavaş fazın süresi kadar (teorik olarak ~%40-50), ölçümle doğrulanmalı.

Ek: `HandshakeProbe.MaxAttempts = 2` bağlanma yolunda 1'e indirilebilir
(failover izleyicisinde 2 kalabilir) — ilk bağlanma "kesin kanıt" için tek
denemeyle de karar üretebiliyor; yanıtsızsa zaten akıllı düşüş devreye giriyor.

---

### A3 — Ölçüm sonuçları önbelleğe alınmıyor; her bağlanmada sıfırdan ölçülür (P0)

`GpnServerProber` ölçer, saklamaz. Aynı sunucu kümesi için dakikalar içinde
tekrar bağlanıldığında aynı ICMP/UDP ölçümleri yeniden yapılır.

**Yapılacak:** `GpnServerProber` içine Kısa-Ömürlü (TTL ~60 sn) ölçüm önbelleği:

- Anahtar: `(ServerId, probe türü, EscapeTunnelForProbes)`
- TTL içinde istek → önbellekten dön (DiagLog: `GPN_PROBE cache-hit`)
- Tünel durumu değiştiğinde (`EscapeTunnelForProbes` sınırında) önbellek geçersiz
- Açılışta ısıtılır (bkz. C1) → ilk "Bağlan"da ölçüm büyük ölçüde önbellekten gelir

Bu, gözlenen ~10 sn'yi ilk bağlanmada da, sonraki bağlanmalarda da aşağı çeker.

---

### A4 — Bağlantı kurulur kurulmaz ağır işler aynı tünele biniyor (P1)

Tünelin "Connected" olduğu ana bağlı tetiklenen üç ayrı iş var:

| Tetik | Kaynak | İş |
|---|---|---|
| `GpnConnectionState.Connected` snapshot | `MainWindow.xaml.cs:~2803` | `MeasureRealPingAsync(isBefore:false, delay:3s)` — oyun uç noktalarına gerçek ping |
| `Reload()` sonu | `MainWindowViewModel.cs:~1126` | `RunAvailabilityCheckAfterConnectAsync` → Ready bekle + 2 sn + `TestServerAvailability()` |
| `ConnectionLifecycleSupervisor` | `ServiceLib/Services/ConnectionLifecycleSupervisor.cs:110+` | 2 sn'de bir UI senkronu, ~30 sn'de bir kural kayması, IP yeniden ölçümü |

Üçü de **taze ve kırılgan** olan tünelin ilk saniyelerine yüklenir. Ayrıca
`MeasureRealPingAsync` yalnızca ilk bağlanmada değil, **her yumuşak düğüm
geçişinde (`soft-switch`) de** çalışır — yani düğüm değiştirdiğiniz her seferde.

**Yapılacak:**
- Bağlantı sonrası ağır ölçümleri tek bir "boşta çalış" zamanlayıcısına al:
  tünel ayakta + **oyun süreci çalışmıyor** + son 5 sn'de bağlantı olayı yok.
- `TestServerAvailability` yerine hafif/tek-sunucu doğrulaması; tam tarama
  yalnızca kullanıcı ⚡ Test'e bastığında veya oyun kapalıyken.
- `MeasureRealPing` sonrası ölçümünü yumuşak geçişte atla (yalnızca ilk kurulumda).

---

### A5 — MTU doğrulaması adaptörü 8 sn boyunca bulamıyor (P2, teşhis)

`GpnCoreLauncher.VerifyTunMtuAsync` (`ServiceLib/Services/GpnCoreLauncher.cs:230-288`)
çekirdek sonrası `AoGPN` adaptörünü 500 ms aralıkla 8 sn yoklar; logda
`GPN_MTU verify timeout` ve ardından `mtu=65535 expected=1350 MISMATCH` görülüyor.

Doğrulama **bağlantıyı engellemiyor** (iyi) ama iki şeyi gösteriyor:
- Adaptör adı/oluşma zamanlaması beklentisi tutmuyor (ad `AoGPN`, beklenen
  `Global.MihomoTunInterfaceName`).
- Adaptörün gerçek MTU'su istenen değil (65535 = Wintun varsayılanı) →
  **uygulanan MTU teyit edilmiyor**, yani yol MTU'su için güvence yok. Oyun
  paketleri için bu bir parçalanma/gecikme riskidir.

**Yapılacak:** MTU'yu mihomo config'ine bırakmakla kalmayıp, çekirdek sonrası
adaptör adını **bulunan ilk Wintun adaptörü** üzerinden eşleştir; MISMATCH
durumunda iyileştirici olarak adaptör MTU'sunu API ile ayarla (yönetici zaten var).

---

### A6 — Çıkış IP uyuşmazlığı failover döngüsü tetikliyor (P1, doğruluk)

Logda `10:08:05 exitMismatch=True` (Almanya seçili, çıkış İtalya). Failover
düğüm değişimi sonrası doğrulama uyuşmazlığı, gereksiz bir "sağlıksız" kararı ve
dolayısıyla ek düğüm geçişi/reconnect üretebilir. `GpnSoftSwitch` + drain
gözlemcisi devredeyken bu yarış ayrıca incelenmeli.

---

## 2. Canlı tüneli kirleten arka plan trafiği

### B1 — Dashboard her 15 saniyede ICMP + tam UDP el sıkışma taraması yapıyor (P0)

`Temalar/features/gpn.js:640-655` → `setInterval(requestGpnProbe, 15000)`.
Başlatan: `Temalar/features/views.js:115` (VPN Rotaları görünümü açıkken).
Karşı taraf: `DashboardMessageDispatcher` → `ProbeGpnServersAsync` →
`MainWindowViewModel.ProbeServersAsync` + `ProbeUdpAllAsync`
(`AoGPN/Services/DashboardGpnServerService.cs:100-140`).

Bu döngü **tüm etkin sunuculara** ICMP + geçerli WireGuard el sıkışma paketleri
gönderir — canlı oyun trafiğiyle aynı fiziksel NIC üzerinden. Döngü
`document.visibilityState` kontrolü **yapmıyor**, yani pencere tepsiye
küçültülmüşken de koşabilir.

**Yapılacak:**
- Oyun/süreç yakalama (capture) aktifken otomatik taramayı **duraklat**; manuel
  ⚡ Test her zaman çalışsın.
- Görünüm gizliyken (`visibilityState === 'hidden'`) döngüyü durdur.
- Tünel ayaktayken otomatik taramayı seyrelt (ör. 60 sn) ve tercihen yalnızca
  ICMP modunda çalıştır.

### B2 — Bağlıyken 25 sn'de bir IP yeniden ölçümü (P1)

`Temalar/features/connection.js:849-858` → `setInterval(check_ip, 25000)`; C#
tarafında ayrıca `ConnectionLifecycleSupervisor` IP yeniden ölçüm tikleri.
Her ölçüm `ip.sb` ailesine HTTP isteği demek — tünel üzerinden ek oturum açılışı.

**Yapılacak:** Bağlıyken ve oyun çalışırken IP doğrulamasını 25 sn → 120 sn'ye
çek; yalnızca bir uyuşmazlık şüphesi (durum değişimi) olduğunda hızlandır.

### B3 — Abonelik otomatik güncellemesi maç ortasında tüneli yıkabiliyor (P0)

`ServiceLib/Manager/TaskManager.cs:35-100`:
- `UpdateTaskRunSubscription()` **her dakika** çalışır; süresi gelen abonelikleri
  indirir → `UpdateTaskHandler` → gerekirse `Reload()` → A1 ile birleşince
  **tünel sıfırdan kurulur**.
- `UpdateTaskRunCheckUpdate()` `numOfExecuted % 1440 == 1` koşulu yüzünden
  **açılıştan ~1 dakika sonra** çalışır (sayaç 1'den başlıyor) — ilk bağlanma
  penceresiyle çakışır.

**Yapılacak:**
- Abonelik güncellemesini ve çekirdek güncelleme kontrolünü **tünel ayaktayken
  veya oyun çalışırken ertele** (kuyruğa al, uygun ilk boşlukta çalıştır).
- Abonelik güncellemesi sonrası `Reload()` yerine, yalnızca abonelik listesini
  tazeleyip **çalışan tüneli koru**; tam reload yalnızca aktif profil değiştiyse.
- İlk güncelleme kontrolünü açılış +1 dk yerine açılışta **ön yükleme (preflight)**
  fazına taşı (bkz. C5) veya 10. dakikaya ertele.

### B4 — 2 saniyelik izleme döngüleri (P2)

- `ConnectionLifecycleSupervisor` 2 sn tik (`ConnectionLifecycleSupervisor.cs:110`):
  her tikte UI thread'e marshal edilen senkron. Oyunda ölçülebilir bir kazanç
  değil ama ucuz bir iyileştirme: tik 2 sn → 5 sn, bağlantı olayı anında zaten
  tetikleniyor.
- `DashboardConnectionEngine.RunLoopAsync` (`DashboardConnectionEngine.cs:92`)
  2 sn'de bir `/connections` okur. `ClashApiBackoffPolicy` sayesinde denetleyici
  kapalıyken artık boşa istek gitmiyor (önceki düzeltme) — görünüm kapalıyken
  döngüyü tamamen durdurmak kalan maliyeti de siler.

### B5 — Açılışta ölçülen "önce" ping'i (P2)

`MainWindow.xaml.cs:~890` `MeasureRealPingAsync(isBefore:true, delay:4s)` açılışta
oyun uç noktalarına ping atar. Doğru yerde (açılış) ama yine de açılış/bağlanma
penceresine biner; preflight fazına alınmalı ve bağlantı kurulmadan önce
tamamlanmalı.

---

## 3. Açılışa taşınacak işler (Preflight / Warm-up)

`App.OnStartup` şu an gerçek bir "hazırlık" hattı değil; splash ilerlemesi
(10 → 30 → 45 → 60 → 80 → 100) sabit adımlarla ilerliyor ve gerçek işin büyük
kısmı **dashboard göründükten sonra** arka planda başlıyor. Önerilen: splash
altında gerçek bir `StartupPreflight` fazı.

| # | Ön yükleme işi | Şu an | Kazanç |
|---|---|---|---|
| C1 | GPN katalog yükle + **ICMP/UDP ölçümlerini ısıt** (A3 önbelleğini doldur) | Her bağlanmada | "Bağlan" tıklamasında ölçüm neredeyse bedava |
| C2 | Kendi genel IP'sini çöz (hairpin teşhisi) | Bağlanma yolunda | Hairpin kontrolü bloklamaz |
| C3 | Wintun sürücüsü/adaptörü + **MTU'yu bir kez doğrula ve uygula** | Her bağlanmada (A5) | MISMATCH/timeout ortadan kalkar |
| C4 | Çekirdek ikilileri (mihomo/xray) var mı + sürüm | Bağlanmada fark edilir | "Çekirdek eksik" hatası erken görünür |
| C5 | Geo dosyaları + çekirdek güncelleme kontrolü | Açılış +1 dk (B3) | İlk dakika ağ trafiği temizlenir |
| C6 | DNS sunucularına erişilebilirlik doğrulaması | İlk DNS sorgusunda | Yavaş/ölü DNS ile bağlanmama önlenir |
| C7 | ISP IP önbelleği | Zaten var (`CacheIspIpAsync`) | — |
| C8 | Süreç/oyun kataloğu + ikonları | Tembel yükleniyor | İlk oyun algılama gecikmesi azalır |

**Tasarım kuralları:** tümü paralel, iptal edilebilir (`_webViewLifetime`),
UI thread'ini bloklamadan, splash ilerlemesine **gerçek** aşama olarak bağlı;
biri başarısız olsa da açılış devam eder (best-effort, loglanır).

---

## 4. Çekirdek (mihomo) gecikme ayarları

`GpnMihomoConfigService.GenerateYaml` incelendi. Tespitler:

| # | Bulgu | Konum | Öneri |
|---|---|---|---|
| D1 | `"find-process-mode": "always"` — mihomo **her bağlantı için** sahibi süreci çözer | `GpnMihomoConfigService.cs:328` | `"strict"` yap: `PROCESS-NAME` kuralı olduğunda zaten çözülür; kural yoksa bedel sıfırlanır. Oyun/launcher kuralları bozulmaz. |
| D2 | DNS sunucuları `tcp://` (DoT) olarak yazılıyor | `GpnMihomoConfigService.cs:414`, `AsTcpNameserver:1003` | Tünelsiz (DIRECT) çözümlemelerde her sorgu için TCP/DoT el sıkışması ödenir. Saf IP'ler için **düz UDP** birincil, DoT yedek olacak şekilde sırala. |
| D3 | `tcp-concurrent` yok | kök blok | `tcp-concurrent: true` ekle — çift yol denemesi ilk paket gecikmesini düşürür. |
| D4 | `unified-delay` yok | kök blok | `unified-delay: true` — arayüzde tutarlı gecikme gösterimi (ölçüm, gerçek RTT değil). |
| D5 | `sniffing.override-destination: true` zorunlu açık | `:382` | Launcher/game domain kuralları buna bağlı; **kapatma**. Ama gecikme maliyetini ölçüp gerekirse launcher egress'i için ayrı sniff kapsamı düşün. |
| D6 | TUN `stack` sabit (gvisor) | `:354` | Windows'ta `system` yığını paket başına daha az CPU/gecikme verir; yönetici zaten var. Ayar olarak sunulabilir (varsayılan değiştirilmeden). |
| D7 | TUN MTU 1360'a kırpılıyor, adaptöre uygulandığı doğrulanmıyor | `GpnCoreLauncher.cs:77-86`, A5 | Uygulandığını doğrula + gerekirse API ile yaz. |

**Uyarı:** D1 ve D3/D4 değişiklikleri mihomo config sözleşmesini değiştirir;
`ServiceLib.Tests/Services/CoreConfig/Mihomo/GpnMihomoConfigServiceTests.cs`
beklentilerinin güncellenmesi ve gerçek çekirdekle A/B doğrulaması gerekir.

---

## 5. Önceliklendirilmiş uygulama planı

Her madde bağımsız gönderilebilir; P0'lar sırayla, P1/P2 paralel yürütülebilir.

### Faz 0 — Görünürlük (önce bu, ½ gün)
- [ ] **P0-1** Bağlanma yoluna aşama zamanlayıcıları ekle: `Gpn_PROBE icmp=…ms udp=…ms`, `Gpn_LAUNCH core-start=…ms handshake=…ms ready=…ms`, toplam `Gpn_CONNECT total=…ms`. DiagLog'a yaz, dashboard diyagnozuna bas.
- [ ] **P1-2** A1/A2/A3/A4'ün gerçek kazancını bu satırlarla ölç (öncesi/sonrası).

### Faz 1 — Tüneli koru (P0, en yüksek etki)
- [ ] **P0-3 (A1)** `ConnectAsync` idempotentlik kapısı: aynı sunucu + Connected → no-op.
- [ ] **P0-4 (B3)** Abonelik güncellemesi ve çekirdek güncelleme kontrolünü tünel/oyun aktifken ertele; abonelik sonrası gereksiz `Reload()`'u kaldır.
- [ ] **P0-5 (B1)** Dashboard 15 sn'lik GPN taramasını oyun/görünürlük kapısına bağla.

### Faz 2 — Bağlanmayı hızlandır (P0-P1)
- [ ] **P0-6 (A2)** ICMP ve UDP ölçüm fazlarını `Task.WhenAll` ile paralelleştir.
- [ ] **P0-7 (A3+C1)** Ölçüm TTL önbelleği + açılışta ısıtma.
- [ ] **P1-8 (A4)** Bağlantı sonrası ağır ölçümleri "boşta" zamanlayıcısına al; soft-switch'te ping sonrası ölçümünü atla.

### Faz 3 — Preflight (P1)
- [ ] **P1-9** `StartupPreflight` servisi: C1-C6 işleri paralel, splash ilerlemesine gerçek aşama olarak bağlı, iptal edilebilir.
- [ ] **P1-10** Splash ilerleme eşiklerini (10/30/45/60/80/100) gerçek aşamalara bağla.

### Faz 4 — Çekirdek ince ayarı (P1-P2)
- [ ] **P1-11 (D1)** `find-process-mode: strict` + testlerin güncellenmesi + gerçek çekirdekle doğrulama.
- [ ] **P2-12 (D3/D4)** `tcp-concurrent` / `unified-delay`.
- [ ] **P2-13 (D2)** DNS nameserver sıralaması (UDP birincil, DoT yedek).
- [ ] **P2-14 (D6)** TUN `stack` seçeneği (varsayılan değişmeden).

### Faz 5 — Doğruluk ve sertleştirme (P1-P2)
- [ ] **P1-15 (A6)** Çıkış IP uyuşmazlığı (`exitMismatch`) ile failover döngüsü yarışını incele; doğrulamayı karar vermeden önce stabil hale getir.
- [ ] **P2-16 (A5)** Adaptör MTU'sunu gerçekten uygula ve doğrula.
- [ ] **P2-17 (B2/B4)** IP yeniden ölçüm aralığı ve 2 sn'lik izleme tikleri.

---

## 6. Doğrulama stratejisi

| Seviye | Ne |
|---|---|
| Birim | TTL önbelleği (taze/bayat/tünel değişimi), `ConnectAsync` idempotentliği (aynı/farklı/null preferred), ölçüm fazı paralelliği (sahte prober ile çağrı sırası), erteleme politikası (Saf `…Policy` sınıfları — mevcut `ClashApiBackoffPolicy` / `ConnectionTogglePolicy` deseni) |
| YAML sözleşmesi | `GpnMihomoConfigServiceTests` — `find-process-mode`, `tcp-concurrent`, `unified-delay`, DNS sırası |
| JS | `Temalar/skins/*.integration.test.js` — prob döngüsünün oyun/görünürlük kapısı; mevcut 322 test yeşil kalmalı |
| Uçtan uca | `ao_diag.txt` + `gpn-session.log`: `Gpn_CONNECT total` hedefi **ilk bağlanmada < 4 sn, sıcak önbellekte < 1.5 sn**; "bağlan → kop → yeniden bağlan" dizisi logda hiç görünmemeli |
| Regresyon | Maç senaryosu: bağlıyken abonelik güncellemesi + F5 + dashboard Rotaları görünümünü aç → tünel kimliği (handshake sayacı / `wg` düğümü) **değişmemeli** |

---

## 7. Hedef mimari (özet)

```
AÇILIŞ (splash, kullanıcı bekliyor)          BAĞLANMA (kullanıcı bekliyor)
├─ config/DB yükle                          ├─ niyet kapısı (zaten bağlı? → no-op)
├─ Wintun sürücü + MTU doğrula/uygula (C3)  ├─ önbellek taze mi? → karar anında
├─ çekirdek ikilileri doğrula (C4)          ├─ değilse: ICMP ∥ UDP (paralel, TTL'e yaz)
├─ geo + güncelleme kontrolü (C5)           ├─ çekirdek başlat + el sıkışma
├─ DNS erişilebilirliği (C6)                ├─ Ready → "Bağlandı" yayınla (CONNECTING görünür)
├─ GPN ölçüm ısıtma (C1) + public IP (C2)   └─ ağır ölçüm YOK (boşta zamanlayıcıya)
├─ ISP IP önbelleği (C7)
└─ süreç/oyun kataloğu (C8)

OYUN OTURUMU: otomatik prob/abonelik/IP-taraması/çekirdek-kontrolü → DURUR
              manuel ⚡ Test → her zaman çalışır
```
