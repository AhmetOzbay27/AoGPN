# Bağlantı/Tepsi Kümesi — Durum Makinesi Modeli + MainWindow'da Kalma Gerekçesi

**Tarih:** 2026-09-05 · **Kapsam:** `AoGPN/AoGPN/Views/MainWindow.xaml.cs` (3.999 satır, worktree)
**Karar:** P0 yol haritasının son açık maddesi (§5/§6 — "model as a state machine or keep,
document why") **belgeleme** dalıyla kapatıldı: alt akışlar durum makineleri olarak modellendi
ve her parçanın neden pencereye bağlı kaldığı belgelendi. Bu belge aynı zamanda ileride kod
tarafına geçilirse izlenecek blueprint'i (§6) içerir.

---

## 1. Yürütme özeti

Bağlantı/tepsi kümesi, P0 refactor'ünün (7.071 → 3.999 satır) **bilinçli olarak en sona**
bırakılmış bölgesidir. Kümenin incelenmesi kritik bir mimari gerçeği gösteriyor:

> **Kümenin "durum"u zaten ServiceLib'e çekilmiş ve testlidir.** MainWindow'da kalan
> ~2.400 satır metot gövdesi bir durum makinesi **değil**; durum makinelerini WPF kromuna,
> WebView2 DOM'una ve ViewModel'lere bağlayan **imperatif sunum/olay yapıştırıcısıdır**.

Zaten çekilmiş/testli karar çekirdekleri (detay §3):

| Çekirdek | Model | Test |
|---|---|---|
| `ServiceLib/Services/ConnectionCoordinator.cs` | `ConnectionState` (9 durum) + `RuntimeSnapshot`, serileştirme gate'i, iptal | `ConnectionCoordinatorTests` (3) |
| `ServiceLib/Services/GpnConnectionCoordinator.cs` | `GpnConnectionState` (4 durum) + `GpnConnectionSnapshot`, BehaviorSubject, soft-switch, drain | `GpnConnectionCoordinatorTests` (23) |
| `ServiceLib/Services/TrayWindowCoordinator.cs` | tepsi-hide vs gerçek-çıkış ayrımı, adım süreli çıkış yolu | `TrayWindowCoordinatorTests` (15) |
| `ServiceLib/Services/WindowLifecycleDecisionService.cs` | `WindowLifecycleAction` (None/AllowClose/HideToTray/Exit) saf kararları | `WindowLifecycleServiceTests` (5) |
| `ServiceLib/Services/RealityCoreFallbackAdvisor.cs` | `RealityFallbackAdvice` (None/SuggestXray/SwitchToXray) + `ApplyXraySwitch` | `RealityCoreFallbackAdvisorTests` (10) |
| `ServiceLib/Services/ForeignTunnelDetector.cs` | yabancı VPN/TUN çakışma tespiti | (entegrasyon) |

Geriye kalan parçanın **büyük kısmı WPF/WebView2 kromuna dokunduğu için taşınamaz**; taşınabilir
olan kısım ise tek-satır servis delegasyonlarıdır (zaten yapıldı). Gerekçe §5'te, istenirse hangi
parçaların taşınabileceği §6'da.

---

## 2. Küme envanteri (ölçüm: worktree, metot gövde satırları)

Aşağıdaki 75 üye, MainWindow'un bağlantı/tepsi kümesi olarak sınıflandırılan bölgesidir.
Toplam **2.432 satır** metot gövdesi; buna alanlar (27-103) ve ctor olay kablolaması (~105-542)
eklenince pencereye bağlı küme **~2.800 satır** eder (el kitabındaki "~1.600" tahmini, daha dar
bir üye tanımıyla ve kümeye yeni eklenen egress/capture ayar kollarından önce yapılmıştı).
Satır aralıkları §-başı yaklaşıktır (worktree'de oturum öncesi ±20 satır diff var).

### A) Bağlantı aç/kapat + mod/taşıma kolları — 862 satır
`ToggleConnectionAsync` (817), `RunGpnConnectAsync` (922), `MeasureRealPingAsync` (970),
`FormatRealPing` (1060), `ComputePingDelta` (1063), `ToggleConnectionFromTrayAsync` (1078), `SetConnectionModeAsync` (1095), `SetDashboardModeAsync`
(1132), `SetTransportAsync` (1190), `EnsureProtocolCompatibleAsync` (1249),
`SetProtocolPreferenceAsync` (1305), `SetTunStackAsync` (1325), `SetAutoReconnectAsync` (1342),
`SetGpnRecoveryWatchAsync` (1351), `SetGpnFailoverAsync` (1365), `SetSystemProxyModeAsync` (1655),
`ToggleSystemProxyAsync` (1700), `ApplyConnectionModeAsync` (1707), `IsGpnTunnelActive` (1767),
`ReadActualConnectionState` (1777), `UpdateTrayStatus` (1806), `WarnOnForeignTunnelBeforeConnect`
(1833), `HandleAppControl` (2068).

### B) Egress/capture ayar kolları — 273 satır
`SetVlessBypassNodeAsync` (1384), `SetVlessBypassFromUriAsync` (1434), `PushVlessBypassNodeAsync`
(1494), `PushGpnCaptureSettingsAsync` (1533), `SetGpnCaptureSettingsAsync` (1568),
`PushGpnWintunSettingsAsync` (1592), `SetGpnWintunSettingsAsync` (1622), `TryGetIntProperty` (1635),
`ToUintOrNull` (1653). *(Launcher-bypass genelleştirmesi bu bölgeyi büyüttü.)*

### C) Reality-core fallback — 117 satır
`SuggestRealityCoreFallbackAsync` (1875), `ApplyRealityXraySwitchAsync` (1943),
`RetryFallbackConnectionAsync` (1979).

### D) Durum senkron + DOM yayın + hata kartı — 286 satır
`SynchronizeConnectionStateAsync` (1992), `WaitForCoreLeavingStartingAsync` (2029),
`OnGpnConnectionSnapshotAsync` (2378), `SendConnectionStateAsync` (2413), `ReadTransport` (2447),
`NotifyConnectionErrorAsync` (2464), `TryPushConnectionFailureAsync` (2477),
`ShowConnectionFailureAsync` (2518), `NotifySystemProxyResultAsync` (2543),
`ComposeSystemProxyResultMessage` (2555).

### E) IP/ISP taban çizgisi + proxy testi — 275 satır
`TestProxyAsync` (2588), `CheckIpAsync` (2661), `CacheIspIpAsync` (2776), `PushIspBaselineAsync`
(2801), `GetIPInfoWithRetryAsync` (2825).

### F) Telemetri döngüsü + köprü — 219 satır
`StartTelemetryLoop` (3028), `ConnectionLifecycleLoopAsync` (3040 — 2 sn supervisory poll),
`StartTelemetryBridge` (3164), `TelemetryDashboard_SamplesChanged` (3177),
`PushRealTelemetryAsync` (3206), `PushTelemetryAsync` (3221), `StopTelemetryBridge` (3235).

### G) Pencere/tepsi yaşam döngüsü + olaylar — 355 satır
`MainWindow_Closed` (3292), `OnProgramStarted` (3414), `OnHotkeyHandler` (3454),
`MainWindow_Closing` (3471), `MainWindow_StateChanged` (3503), `SyncWebViewSuspensionAsync`
(3535), `ShowTrayMinimizeHint` (3565), `ExitApplicationSafelyAsync` (3586),
`Current_SessionEnding` (3598), `Shutdown` (3605), `MainWindow_PreviewKeyDown` (3615),
`MenuClose_Click` (3650), `ShowHideWindow` (3769), `OnLoaded` (3801), `TryCreateTrayIcon` (3832).

### H) DOM yürütücü (paylaşılan altyapı) — 45 satır
`ExecuteScriptSafelyAsync` (3247) — tüm servislerin enjekte aldığı `executeScript` delegesinin
gövdesi; DOM kanalı + WebView ömrü + closing-bayrağı üçlüsünü sarar.

Ayrıca ctor (105-542, ~440 satır): 20+ `AppEvents.*` kanal aboneliğini ilgili servislere/pencerelere
bağlar (örn. `GpnConnectionStateChanged → OnGpnConnectionSnapshotAsync`, `CoreHealthChanged →
_lastMainCoreFailure` beslemesi, `SysProxyChangeRequested → SetSystemProxyModeAsync`,
tray-toggle → `ToggleConnectionFromTrayAsync`, `ShutdownRequested → Shutdown`).

---

## 3. Zaten ServiceLib'de olan çekirdekler (durum makinesi envanteri)

P0 dalgaları boyunca kümenin **karar** katmanı, Wave-2b tarifiyle ServiceLib'e çekildi ve test
edildi. Her biri saf/gövde-testli olduğu için küme adına "yeniden durum makinesi kurmak" bunlarla
çakışır:

| Dosya | Durum/karar modeli | Test |
|---|---|---|
| `ServiceLib/Services/ConnectionCoordinator.cs` | `ConnectionState`: Disconnected → Preparing → StartingCore → ApplyingRoutes → ApplyingProxy → Verifying → Connected; Stopping; Failed. `RuntimeSnapshot` (Sequence/ActiveNodeId/Mode/Transport/SystemProxyEnabled/Error). `ExecuteAsync` (gate + iptal + monotonic yayın), `SetState`, `Cancel`, `SnapshotChanged`. | `ConnectionCoordinatorTests` — serileştirme, iptal, hata→Failed, monotonik snapshot |
| `ServiceLib/Services/GpnConnectionCoordinator.cs` + `IGpnConnectionCoordinator` | `GpnConnectionState`: Disconnected/Connecting/Connected/Failed. `GpnConnectionSnapshot` BehaviorSubject; `ConnectAsync` (seçim→mod→launcher), kesintisiz düğüm soft-switch, drain, `Snapshots` Rx kanalı. DI: `GpnServiceCollectionExtensions`. | `GpnConnectionCoordinatorTests` (23) |
| `ServiceLib/Services/TrayWindowCoordinator.cs` | Tepsi-hide vs gerçek-çıkış **tek koruyucu**; `ShouldHideOnMinimize/Close`, `ShouldShowOnToggle`, `HandleMinimize`, `CloseToTray`, `ExitApplicationAsync` (adım süreli 20 sn + 70 sn genel watchdog — takılan adımda kapanış). Yaşam döngüsü dikişleri (stop-core/clear-proxy/flush/shutdown) ctor-enjekte. | `TrayWindowCoordinatorTests` (15) — hide yollarının dikişlere hiç dokunmadığını kanıtlar |
| `ServiceLib/Services/WindowLifecycleDecisionService.cs` (+ `AoGPN/Services/WindowLifecycleService.cs` ince WPF sarmalayıcı) | `WindowLifecycleAction` (None/AllowClose/HideToTray/Exit); `DecideClose`, `DecideStateChange`, `ShouldShowOnToggle` — saf. | `WindowLifecycleServiceTests` (5) |
| `ServiceLib/Services/RealityCoreFallbackAdvisor.cs` | `RealityFallbackAdvice` (None/SuggestXray/SwitchToXray); `Evaluate(node, failedCoreType, tunEnabled, connectionUp, modeOff)` saf karar; `IsRealityNode`, `ApplyXraySwitch` | `RealityCoreFallbackAdvisorTests` (10) |
| `ServiceLib/Services/ForeignTunnelDetector.cs` | Yabancı TUN adaptörü / yerel proxy portunda yabancı dinleyici / bilinen VPN istemcisi tespiti | entegrasyon |

Ayrıca kümenin taşıdığı veri **kaynakları** da ServiceLib/ViewModel katmanında: `SplitTunnelViewModel`
(mod üçlüsü + `ApplyCmd`), `MainWindowViewModel` (`GpnConnectCmd`, `GpnCoordinatorSnapshot`),
`TelemetryDashboardViewModel` (örnekleyici), `StatusBarViewModel` (tepsi satırı), çekirdek sağlığı
(`CoreEngineHost.GetHealth(CoreHealthRole.Main)` — Starting/Ready/Degraded/Failed/Stopped),
`SystemProxyOnlyService`.

---

## 4. Alt akışların durum makinesi modelleri

### 4.1 Uygulama bağlantı yaşam döngüsü (VPN + GPN ortak kapısı)

**Durum kaynakları üç katmanlıdır** ve MainWindow üçünü de **okur/uzlaştırır** ama hiçbirini
tek başına sahiplenmez:

1. **Kalıcı mod:** `ConnectionItem.Mode` — `ModeOff` / `ModeManual` (gpn) / `ModeVpn` (vpn).
2. **Çekirdek sağlığı:** `CoreHealthState` Starting → Ready/Degraded/Failed/Stopped (yalnızca
   local SOCKS dinleyicisi yanıt verince Ready).
3. **GPN koordinatörü:** `GpnConnectionState` — Disconnected/Connecting/Connected/Failed
   (BehaviorSubject ilk değeri `GpnConnectionSnapshot.Idle()` üreteci).

MainWindow'un katkısı bir **uzlaştırma durumu** + üç kısıtlayıcıdır:

| MainWindow mekanizması | Rol |
|---|---|
| `_connectionToggleGate` (SemaphoreSlim) | aç/kapat + mod geçişlerini serileştirir (çakışan yazma/çekirdek yeniden yüklemesi yok) |
| `_connectionStarting` | "connecting" penceresi: çekirdek Starting'den çıkmadan yayınları "disconnected"a düşürmez (bağlan→iptal→bağlan titremesi) |
| `_connectionState` (Volatile) | yayınlanmış efektif durum önbelleği — değişmedikçe DOM'a tekrar basmaz |
| `WaitForCoreLeavingStartingAsync` | bağlanma sırasında çekirdeğin terminal duruma (Ready/Degraded/Failed) oturmasını bekler; GPN `Failed` anlık görüntüsünde erken çıkar |

**Geçiş tablosu (ToggleConnectionAsync / RunGpnConnectAsync):**

| Tetik | Koşul | Etki |
|---|---|---|
| Bağlan (gpn/vpn) | `!ReadActualConnectionState()` | `_connectionStarting=true` → transport belirle (vpn⇒tun) → `ApplyCmd` → `WaitForCoreLeavingStartingAsync` → `SynchronizeConnectionStateAsync(force)` → başarısızlık kartı |
| Bağlan (tepsi toggle) | aynı kapı | `ToggleConnectionFromTrayAsync` → yukarıdaki |
| Kes | `ReadActualConnectionState()` | transport temizle → `ApplyCmd(ModeOff)` → proxy-only uzlaştırma → oturum günlüğü kapat |
| Mod değişimi (bağlıyken) | `SetConnectionModeAsync/DashboardMode` | gate içinde `ApplyConnectionModeAsync` (yumuşak geçiş) |
| GPN snapshot olayı | `AppEvents.GpnConnectionStateChanged` | `OnGpnConnectionSnapshotAsync`: Connecting⇒eski hata temizle; Connected⇒`_connectionState=true` + "sonra" gerçek-ping; Disconnected⇒bayraklar temiz; Failed⇒hata kartı için sakla |

### 4.2 Reality-core fallback (durum: düğüm × çekirdek × TUN)

Karar **saf fonksiyon** (`RealityCoreFallbackAdvisor.Evaluate`, 10 testli); MainWindow yalnızca
**tetikleme ve onay sunumunu** yönetir:

```
[2 sn poll: çekirdek Failed?] ──► [düğüm REALITY?] ──► [daha önce önerilmedi mi?]
        │                                                    │
        └──► Advisor.Evaluate(node, failedCore, tun, up, off)
                 ├─ None        → sessiz
                 ├─ SuggestXray → NoticeManager.Enqueue (bilgi bildirimi)
                 └─ SwitchToXray→ EnqueueAction("Switch to Xray" butonu)
                                       └──► (kullanıcı onayı) ApplyRealityXraySwitchAsync
                                              ├─ düğümü Xray'e çevir + SQLite'e yaz
                                              └─ RetryFallbackConnectionAsync → ToggleConnectionAsync
```

Anahtar detaylar: öneri **düğüm başına bir kez** (`_realityFallbackAdvisedNodes`, anahtar
`"IndexId|tunEnabled"` — TUN değişince yeniden silahlanır); TUN açıkken otomatik geçiş bloke
olduğu için yalnızca **bilgi** bildirimi; düğüm geçişi asla sessizce yapılmaz (kullanıcı onayı
zorunlu). MainWindow'a bağlı kalan: NoticeManager eylem bildirimi (pencere snackbar'ına gider) ve
geçiş sonrası bağlantıyı yeniden çalıştırma (4.1 kapısı).

### 4.3 Tepsi/pencere yaşam döngüsü (durum: görünür ↔ gizli ↔ çıkıyor)

Karar katmanı `WindowLifecycleDecisionService` + `TrayWindowCoordinator`'da (testli); MainWindow
yalnızca WPF eylemlerini yürütür:

```
                 ┌─────────────── Görünür ───────────────┐
  tray sol-tık / │                                        │ minimize
  ShowForm tuşu  │                                        ▼
 (ShouldShowOnToggle)                              Minimize + Minimize2Tray?
                 ▲                                        │ evet (coordinator.HandleMinimize)
                 │                                        ▼
                 └──── Tepside gizli (ShowInTaskbar=false  │
                        + WindowState.Minimized;           │ AutoHideStartup
                        Window.Hide() ASLA — TaskbarIcon   │ OnLoaded: önce
                        kaydı bozulur)                     │ ForceCreate sonra gizle
                                                           │
  X / Alt+F4 / HTML kapat ──► DecideClose ──► HideToTray (e.Cancel=true, CloseToTray)
                                     │
                                     └─► Exit: _allowClose=true + ExitApplicationSafelyAsync
                                               (coordinator: bounded adımlar + watchdog)
  Oturum kapanışı / Shutdown / tepsi Exit ──► Shutdown(): _allowClose=true + App.Shutdown
```

MainWindow'a bağlı kalma nedenleri (krom): `MainWindow_StateChanged`/`_Closing`/`_Closed` WPF
olayları, `HwndSource`/`WindowProc` özel başlık kromu, `TaskbarIcon`'ın `StatusBarView` görsel
ağacında barınması (`tbNotify.ForceCreate/Dispose`), `ShowInTaskbar`/`WindowState` manipülasyonu,
`SyncWebViewSuspensionAsync` (gizliyken WebView2 GPU'yu dondur). **Karar** (gizle vs çık) pencere
dışında; **eylem** (WPF nesneleri) pencerede.

### 4.4 ISP taban çizgisi + sızıntı doğrulama (durum: önbellek + doğrulama)

```
[CacheIspIpAsync — açılışta/bağlı değilken]   (yalnızca BAĞLI DEĞİLKEN önbellekle —
   └─► _ispIpCached=false → true              TUN açıkken doğrudan istek tünelden çıkar,
                                              ISP IP'si olarak tünel IP'si kaydedilmez)
[CheckIpAsync — connect anında hızlı 3-tik, sonra 15-tik ≈30 sn]
   ├─ bağlı değil → doğrudan IP = ISP taban çizgisi (yeniden önbellekle)
   ├─ bağlı + proxy → SOCKS5'ten tünel IP'si; verified = tünel ≠ doğrudan IP
   ├─ bağlı + tun   → doğrudan IP zaten tünelden; verified = doğrudan IP ≠ önbellek ISP
   └─ native motor → GpnCaptureBridge çift yönlü paket sayacı (Sent>0 ∧ Received>0);
                     "IP değişmedi" karşılaştırması native modda GEÇERSİZ (yalnızca
                     hedef uygulamaların UDP'si tünellenir) — yanlış "Sızıntı" üretmez
   └─► window.setRealIpState(payload)  + GPN_IPVERIFY gpn-session.log satırı
```

MainWindow'a bağlı kalan: DOM push (WebView hazır olmadan yayın yok), transport okuma
(`ReadTransport`), çekirdek/önbellek okumaları. **Saf kısım** (retry'li IP çözümleme +
karşılaştırma + native doğrulama kuralı) §6'da taşınabilir adaydır.

### 4.5 Telemetri döngüsü (2 sn supervisory poll + köprü)

Örnekleyici `TelemetryDashboardViewModel`'de (ServiceLib); MainWindow iki görev üstlenir:

1. **`ConnectionLifecycleLoopAsync`** — 2 sn'lik süpervizör: her tikte (Dispatcher.Background)
   fan-out: `SynchronizeConnectionStateAsync` → (kural-drift ~30 sn / bağlanma anında hemen) →
   (bağlanma anında IP yeniden ölç) → `SuggestRealityCoreFallbackAsync` → `UpdateTrayStatus` →
   `PushSystemProxyStateAsync` → `PushMonitorSnapshotAsync` → (bağlıysa `PushNodeInfoAsync` +
   periyodik `CheckIpAsync`) → `SynchronizeWindowStateAsync`. Tekrarlayan çünkü WebView2 köprüsü
   dışındaki değişiklikleri (native liste, tepsi, oyun-tetik, hotkey) yakalar.
2. **`StartTelemetryBridge`** — örnekleyicinin `SamplesChanged` olayını `updateTelemetry(...)`
   DOM çağrısına köprüler; pencere kapanırken `StopTelemetryBridge`.

---

## 5. Neden MainWindow'da kaldığı (gerekçe)

Küme **"bilinçli olarak pencereye bağlı bırakıldı"** kategorisindedir; §6'daki Wave tarifi bu
küme için üç dalgada da denenmedi çünkü her üye aşağıdaki bağımlılıklardan en az birine dokunur:

1. **WPF krom olayları yalnızca pencerede var olur.** `WindowProc`/`HwndSource` (özel başlık
   çubuğu, DPI, border ölçümü), `StateChanged`/`Closing`/`Closed`/`Loaded`/`PreviewKeyDown`,
   `SessionEnding`, ikinci-örnek `OnProgramStarted` — bu olay gövdeleri doğası gereği
   `MainWindow` örneğine aittir. Taşınabilse bile pencereye birer çağrı bırakırlar; gövde başına
   kazanç birkaç satırdır.

2. **Tepsi simgesi WPF görsel ağacına gömülüdür.** `TaskbarIcon` (H.NotifyIcon.Wpf)
   `StatusBarView` içinde barınır; `ForceCreate` yalnızca görsel ağaç yüklüyken çalışır
   (`TryCreateTrayIcon`), gizleme `Window.Hide()` ile **yapılamaz** (kayıt bozulur — kod yorumu
   bunu açıkça belgeler: "minimizing makes the app vanish from the tray"). Kapanışta
   `MainWindow_Closed` simgeyi dispose eder (hayalet simge yığılması düzeltmesi). Bu yaşam
   döngüsü penceresiz anlamsızdır.

3. **DOM kanalı sahipliği pencerededir.** Tüm yayınlar `ExecuteScriptSafelyAsync` üzerinden
   `DashboardHost`'a (WebView2) gider ve `_webViewReady`/`_isClosing`/`_webViewLifetime`
   bayraklarına bağlıdır. Wave 2b/3 bunu **delege enjeksiyonuyla** çözdü: servisler `executeScript`
   alır, pencere gövdeyi verir. Aynı desen kalan üyelere de uygulanabilir (§6) — ama kalan
   üyelerin çoğu zaten servis delegasyonu ya da kromdur, aradaki "gövde" incedir.

4. **Efektif durum üç global kaynağı okur ve pencere onların kompozisyon köküdür.**
   `ReadActualConnectionState` → `AppManager.Config.ConnectionItem.Mode` +
   `CoreEngineHost.GetHealth(Main)` + `IsRunningCore(...)`; `IsGpnTunnelActive` →
   `ViewModel.GpnCoordinatorSnapshot`; `UpdateTrayStatus` → `StatusBarViewModel.Instance`.
   Bunlar uygulama geneli tek tonlardır; pencere (ve ctor'da kurulan servisler) zaten bu
   global'lerin tek tüketici/kompozisyon noktasıdır. Durumu "saf"laştırmak bu global'lere
   erişimi enjekte etmek demektir — Wave 2b bunu yaptı, kalan üyeler için marjinal fayda azdır.

5. **Küçük yapıştırıcı + yüksek dokunuş yüzeyi.** Kalan gövdelerin büyük kısmı 10-40 satırlık
   "oku → karar ver (zaten ServiceLib'de) → DOM'a bas / ViewModel'i değiştir / WPF nesnesini
   kurcala" desenidir. Taşıma maliyeti (byte-exact kesim + ctor-enjekte ~10-15 delege + olası
   yeniden kablolama) satır kazancına oranla yüksektir; ayrıca WPF tarafı için otomatik test
   yoktur, güvenlik ağı yalnızca derleme + byte-diff'tir.

6. **Yeni durum makinesi kurmak çoğaltma olur.** §3'teki çekirdekler durumu zaten modelliyor ve
   testliyor. MainWindow'a ikinci bir "durum makinesi" eklemek (ör. poll döngüsünü state-enum'a
   bağlamak) aynı gerçeğin iki modeli arasında senkronizasyon borcu yaratır — 4.1'deki titreme
   düzeltmeleri (`_connectionStarting`/`WaitForCoreLeavingStartingAsync`) tam da böyle bir
   ikinci modelin yarattığı yarışların yamasıdır.

**Sonuç:** Küme için "keep — document why" dalı seçildi. Kalan gövde bir durum makinesi değil;
durum makinelerinin (ServiceLib) WPF/WebView2 sunumuna bağlandığı, pencere ömrüne endeksli bir
**sunum/olay katmanıdır**. MainWindow'u saf UI kabuğuna indirme hedefi, bu kümede "satır
sayısını düşürme" yerine "karar katmanını dışarı çekme" olarak zaten gerçekleşti.

---

## 6. İleri taşınma blueprint'i (istenirse)

Kod tarafına geçilirse §5'teki gerekçeye rağmen **makul taşınabilir adaylar** (Wave-2b tarifi —
byte-exact kesim, ctor-enjekte delege, tek-satır pencere delegasyonu, build 0 + body-diff + tam
test):

1. **`ConnectionLifecycleSupervisor`** (ServiceLib veya `AoGPN/Services`) — `ConnectionLifecycleLoopAsync`
   gövdesini taşır (124 satır). Enjekte: `dispatcher` (InvokeAsync), `executeScript`,
   `readActualConnectionState`, `isWebViewReady`, `push*` delegeleri (`SyncConnectionState`,
   `RuleDrift`, `CheckIp`, `RealityFallback`, `TrayStatus`, `SystemProxyState`, `MonitorSnapshot`,
   `NodeInfo`, `WindowState`) ve tik aralığı. Desen: `TrayWindowCoordinator` (tüm dikişler ctor'da).
   Kazanç ~124 satır + fan-out'un test edilebilmesi (tik/koşul mantığı saf).
2. **`ConnectionFailureLedger`** ✅ **UYGULANDI (2026-09-05)** —
   `_lastMainCoreFailure/_lastMainCoreDiagnostic/_lastGpnFailedSnapshot`
   + `TryPushConnectionFailureAsync`/`ShowConnectionFailureAsync` besleme mantığı (41+25 satır +
   3 alan) → `ServiceLib/Services/ConnectionFailureLedger.cs` + 16 test
   (`ConnectionFailureLedgerTests`): 45 sn tazelik penceresi, GPN önceliği,
   elevation/canRecover türetimi, port taşıma — saf `Decide` kararı enjekte delege + `IClock`
   ile testli. MainWindow 3.999 → 3.752.
3. **`IpLeakVerifier`** (ServiceLib, saf) — `CheckIpAsync`'in karşılaştırma/doğrulama kuralını
   (proxy/tun/native üçlüsü + önbellek koşulu) giriş-çıkış fonksiyonuna çevirir. MainWindow'a
   yalnızca DOM push kalır. Kazanç: 4.4'teki kural matrisinin testi (özellikle native mod
   yanlış-pozitif koruması).
4. **Taşınamazlar** (belgelensin, taşınmasın): §5/1-2'deki WPF krom + tepsi ömrü; `ReadTransport`/
   `ReadActualConnectionState` gibi global-oku-yapıştırıcıları; ayar kolları (B) zaten tek-satır
   servis delegasyonuna yakındır.

Tahmini net kazanç: ~250-350 satır pencere gövdesi + üç yeni test dosyası; MainWindow 3.999 →
~3.700. Asıl değer satır değil, fan-out/hata-kartı/IP-doğrulama kurallarının otomatik test
kapsamına girmesidir.

---

## 7. İlgili dosyalar

- `AoGPN/AoGPN/Views/MainWindow.xaml.cs` — bu belgenin envanteri (satır aralıkları §2).
- `AoGPN/AoGPN/Services/` — Wave 2b/3 çıkarımları (DashboardSettingsService, DashboardGpnServerService,
  DashboardNodeService, DashboardPushService, DashboardMessageDispatcher, DashboardPublisher,
  IDashboardBridge, WindowLifecycleService) + `DashboardHost` (WebView2).
- `AoGPN/ServiceLib/Services/` — §3 çekirdekleri (+ `SystemProxyOnlyService`, `ForeignTunnelDetector`).
- `AoGPN/ServiceLib/ViewModels/` — `MainWindowViewModel`, `TelemetryDashboardViewModel`,
  `SplitTunnelViewModel`, `StatusBarViewModel`.
- `AoGPN/ServiceLib/Models/RuntimeSnapshot.cs` — `ConnectionState` + `RuntimeSnapshot`.
- Testler: `ServiceLib.Tests/{ConnectionCoordinatorTests,TrayWindowCoordinatorTests,
  RealityCoreFallbackAdvisorTests,WindowLifecycleServiceTests}.cs` +
  `ServiceLib.Tests/Services/GpnConnectionCoordinatorTests.cs`.
