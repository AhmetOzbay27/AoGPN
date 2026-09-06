# MainWindow İnceltme — Wave 4 Çıkarım Adayları

**Tarih:** 2026-09-05 · **Kapsam:** `AoGPN/AoGPN/Views/MainWindow.xaml.cs` (3.999 satır, worktree)
**Amaç:** UI kabuğunu daha da inceltmek için kalan en büyük pencere içi blokları analiz etmek ve
yeni çıkarım adaylarını (kapsam/satır/mekanizma/risk/test) öncelik sırasıyla belirlemek.

Bu belge `docs/connection-tray-cluster.md`'nin devamıdır: oradaki §6 "ileri taşınma blueprint'i"
burada somut, ölçülü Wave-4 adaylarına genişletilir. Referanslar: Wave-2b/3 tarifi (byte-exact
kesim → yeni servis → tek-satır pencere delegasyonu → build 0 → body-diff → tam test süiti) ve
küme envanteri (bağlantı/tepsi belgesi §2, 75 üye ~2.432 satır).

> **Uygulama günlüğü:** **W4-B uygulandı** (2026-09-05) — `ConnectionFailureLedger`
> (`ServiceLib/Services/ConnectionFailureLedger.cs`, 185 satır) + 16 test
> (`ServiceLib.Tests/Services/ConnectionFailureLedgerTests.cs`) + MainWindow delegasyonu
> (3.999 → 3.752 satır). Başlıktaki satır sayıları yazım anındaki ölçümdür; aday
> kapsamları uygulama öncesi metin olarak korunur.

---

## 1. Yürütme özeti

MainWindow 7.071 → 3.999 satıra indi; kalan gövde **18 bölgede ~3.974 satır + kapanış** içerir
(152 metot/olay işleyicisi). En büyük tek bloklar:

| Sıra | Bölge | Satır | Doğa |
|---|---|---|---|
| 1 | Bağlantı/mod/egress kolları (817-2139) | 1.323 | imperatif orkestrasyon + ayar kolları |
| 2 | Test/IP/ISP taban çizgisi (2588-2873) | 286 | kural + DOM push karışık |
| 3 | Telemetri loop/bridge/ExecuteScript (3028-3291) | 264 | supervisory poll + köprü |
| 4 | GPN snapshot + hata kartı + sysproxy (2378-2587) | 210 | durum besleme + kart kuralları |
| 5 | Program/tepsi/hotkey/oturum olayları (3414-3614) | 201 | pencere kromu |
| 6 | Ctor (105-542) | 438 | kompozisyon kökü + bağ boilerplate |

Kalan gövdenin sınıflandırması (dokunulabilirlik):

- **Taşınabilir mantık** (~450-550 satır): poll döngüsü, hata kartı defteri, IP/ISP doğrulama
  kararı, Reality-fallback orkestrasyonu, proxy-test kompozisyonu. Wave tarifiyle taşınabilir;
  asıl değer satır değil, bu kuralların otomatik test kapsamına girmesidir.
- **Taşınabilir ama yüksek riskli** (~600-700 satır): `ToggleConnectionAsync` +
  `ApplyConnectionModeAsync` + senkron/uzlaştırma (bağlantı orkestratörü) — titreme/yarış
  düzeltmeleri Dispatcher/ViewModel etkileşimine gömülü; tek seansta değil, dikkatli dalga ister.
- **Pencere kromu / taşınamaz** (~1.500-1.700 satır): ctor kompozisyon kökü + XAML bağları,
  `WindowProc`/border/DPI, sürükle, tepsi simgesi, menü/nav/layout, WebView başlangıç olayları,
  delegasyon duvarı (IDashboardBridge yüzeyi — zaten tek satır).
- **İnce yapıştırıcı (dokunma)**: delegasyon duvarı (2874-3027, ~154 satır) ve kapanış bölgesi
  büyük kısmı tek-satır delegasyonlardır; taşıma onları daha da inceltmez.

**Gerçekçi tavan:** W4-A/B/C (+ istenirse E/F) uygulanırsa ~300-450 satır pencere gövdesi düşer
ve üç yeni test dosyası eklenir; W4-D (bağlantı orkestratörü) eklenirse toplam kazanç
~1.000-1.200 satırla MainWindow'u ~2.800-3.000'e indirir. Altına inmek krom/kompozisyon
gövdesinden dolayı anlamlı değildir.

---

## 2. Sınıflandırma anahtarı

| Sınıf | Anlam | Aksiyon |
|---|---|---|
| **M** (movable) | saf/gövde kural veya enjekte-delege desenine uyan mantık | Wave tarifiyle taşı |
| **O** (orchestrator-riskli) | taşınabilir ama Dispatcher/ViewModel/global etkileşimli, yarış hassas | ayrı dalga, ekstra body-diff |
| **C** (chrome) | WPF krom / görsel ağaç / Hwnd / TaskbarIcon / XAML adları | taşıma; yalnızca belgele |
| **D** (delegation) | tek-satır servis delegasyonu / IDashboardBridge yüzeyi | dokunma |
| **R** (composition) | ctor kompozisyon kökü + ReactiveUI bağ boilerplate | yalnızca belgele |

---

## 3. Adaylar (öncelik sıralı)

### W4-A — `ConnectionLifecycleSupervisor` (poll döngüsü) — sınıf M — ~150 satır
**Kapsam:** `ConnectionLifecycleLoopAsync` (3040-3163, 124 satır) + `StartTelemetryLoop` (3028)
+ `_connectionLifecycleTask` alanı. 2 sn'lik tick fan-out'unun tamamı: durum senkronu, kural-drift
(~30 sn), bağlanma anı IP yeniden ölçümü, Reality-fallback tetiği, tepsi durumu, sistem-proxy,
monitor snapshot, düğüm bilgisi + IP paneli, pencere durumu senkronu.
**Mekanizma:** `TrayWindowCoordinator` deseni — tek ctor, tüm çıkışlar delege:
`runOnUiThread(Func<Task>)`, `readConnected()`, `isWebViewReady()`, ve her fan-out hedefi
(syncConnectionState / pushRuleDrift / checkIp / suggestRealityFallback / updateTrayStatus /
pushSystemProxyState / pushMonitorSnapshot / pushNodeInfo / synchronizeWindowState). Hız
ayarları (2 sn tick, 15-tik drift, 3/15-tik IP) ctor parametresi → test edilebilir.
**Risk:** düşük-orta (loop gövdesi tek parça; tekrarlayan Dispatcher sarmalayıcıları delegeye
dönüşür). **Test:** tick sayacı/koşulları (drift kaç tick'te, bağlanınca IP hemen) — saf kural
kısmı ServiceLib'de testlenebilir.
**Kazanç:** ~124 satır gövde + pencere alanı; fan-out kuralları otomatik test kapsamına girer.

### W4-B — `ConnectionFailureLedger` (hata kartı defteri) — ✅ UYGULANDI (2026-09-05)
**Kapsam:** `_lastMainCoreFailure/_lastMainCoreDiagnostic/_lastGpnFailedSnapshot` alanları +
`TryPushConnectionFailureAsync` (2477, 41 satır) + `ShowConnectionFailureAsync` (2518, 25 satır)
+ `NotifyConnectionErrorAsync` (2464, 13 satır) + ctor'daki besleme abonelikleri (247-273'teki
`CoreHealthChanged`/`CoreStartupDiagnosticChanged` blokları) + `OnGpnConnectionSnapshotAsync`
(2378) içindeki defter yazımları.
**Mekanizma:** yeni servis (AoGPN/Services) — enjekte: `executeScript`, `isWebViewReady`,
`readActualConnectionState`. Kamu yüzeyi: `RecordMainCoreFailure(health)`, `RecordDiagnostic(diag)`,
`RecordGpnSnapshot(snapshot)`, `ResetOnConnecting()`, `TryPushAsync()`.
**Risk:** düşük. Ctor'daki iki abonelik bloğu `Record*` çağrısına iner; `OnGpnConnectionSnapshotAsync`
defter satırları servise taşınır. **Test:** 45 sn pencere, GPN önceliği, elevation/canRecover
türetimi, port taşıma — saf karar fonksiyonu (`Decide(message...)`) ServiceLib'de.
**Kazanç:** ~90-120 satır + hata kartı seçim kuralları testli.

**Uygulama sonucu:** `ServiceLib/Services/ConnectionFailureLedger.cs` (185 satır) + 16 test
(`ConnectionFailureLedgerTests`): `RecordCoreHealth/RecordDiagnostic/RecordGpnSnapshot`,
`ResetOnConnecting`, `TryPushAsync`, `PushErrorAsync`, `Decide` (45 sn tazelik penceresi,
GPN-öncelik sırası, elevation/canRecover/port-taşıma türetimi, script içeriği) — tümü enjekte
delege + `IClock` ile testli. MainWindow: 3 alan + 3 abonelik + 3 metot gövdesi tek-satır
delegasyona indi → 3.999 → **3.752 satır**.

### W4-C — `IpLeakVerifier` (saf doğrulama çekirdeği) — sınıf M — ~120 satır kural
**Kapsam:** `CheckIpAsync` (2661, 115 satır) içindeki **karar** kısmı: önbellek koşulu, üç
transport dalı (proxy/tun/native), karşılaştırma + `_lastTunnelVerified`; `CacheIspIpAsync`
(2776) önbellek kuralı (yalnızca bağlı değilken) + `GetIPInfoWithRetryAsync` (2825).
**Mekanizma:** ServiceLib'e saf fonksiyon — girişler `(connected, transport, ispIp, directInfo,
tunnelInfo, nativeSent/Received)` → çıktı `IpVerifyVerdict(tunnelIp, tunnelCountry, verified,
reason)`. MainWindow'da DOM push (`window.setRealIpState`) + `_webViewReady` gating + çağrı
zamanlaması kalır.
**Risk:** düşük — kural matrisi bugün zaten tek metotta; kesim noktası net. **Test:**
proxy/tun/native üçlüsünün tam matrisi (native mod yanlış-pozitif koruması dahil) — bugün hiç
testli değil, en değerli adaylardan biri.
**Kazanç:** ~60-80 satır pencere + kural matrisi testli (özellikle native motor
"çift yönlü paket" kuralı).

### W4-D — `ConnectionModeOrchestrator` (bağlantı aç/kapat + uzlaştırma) — sınıf O — ~600-700 satır
**Kapsam:** `ToggleConnectionAsync` (105), `RunGpnConnectAsync` (48), `ApplyConnectionModeAsync`
(60), `SynchronizeConnectionStateAsync` (37), `WaitForCoreLeavingStartingAsync` (39),
`ReadActualConnectionState` (29), `IsGpnTunnelActive` (10), `ReadTransport` (17),
`SendConnectionStateAsync` (34), `ToggleConnectionFromTrayAsync` (17), `OnGpnConnectionSnapshotAsync`
(35) + ilgili alanlar (`_connectionToggleGate`, `_connectionStarting`, `_connectionState`).
**Mekanizma:** Wave-2b tarifiyle `AoGPN/Services/ConnectionModeOrchestrator` — enjekte:
`getViewModel`, `executeScript`, `proxyOnlyService`, `notifyNodesOp` (VpnSessionLog bağı),
`pushSystemProxyState`, `pushNodeInfo`, `updateTrayStatus`, `readGpnSnapshot`, `runOnUiThread`,
`getHealth`/`isRunningCore` (AppManager sarmalayıcıları), `failures` (W4-B).
**Risk:** **yüksek** — `_connectionStarting`/`WaitForCoreLeavingStartingAsync` titreme-önleme
mantığı Dispatcher + ViewModel `ApplyCmd` + çekirdek zamanlamasına gömülü; WPF tarafında otomatik
test yok, güvenlik ağı derleme + byte-diff. Bu yüzden ayrı, kendi başına bir dalga (W4-D) olarak
planlanmalı — A/B/C ile aynı commit'e konmamalı.
**Test:** senkron/uzlaştırma kuralının saf kısmı (`ShouldPublish(connectionStarting, actual,
previous)`) ServiceLib'de.
**Kazanç:** ~450-550 satır net pencere düşüşü → MainWindow ~3.200-3.400.

### W4-E — `RealityCoreFallbackCoordinator` — sınıf M — ~120 satır
**Kapsam:** `SuggestRealityCoreFallbackAsync` (68) + `ApplyRealityXraySwitchAsync` (36) +
`RetryFallbackConnectionAsync` (13) + `_realityFallbackAdvisedNodes` (dedupe).
**Mekanizma:** karar zaten `RealityCoreFallbackAdvisor`'da (ServiceLib, 10 test). Kalan
orkestrasyon taşınır: enjekte `getConfig`, `getHealth`, `getProfileItem`, `noticeEnqueue`,
`noticeEnqueueAction`, `reconnectAsync` (W4-D kapısı). Dedupe seti servise; W4-A poll tetiklemesi
delege kalır.
**Risk:** düşük-orta (SQLite + düğüm yazma AppManager üzerinden). **Kazanç:** ~100 satır +
tetikleme/dedupe kuralları testli.

### W4-F — `ProxyTestOrchestrator` (proxy testi) — sınıf M — ~120 satır
**Kapsam:** `TestProxyAsync` (73) + `ComposeSystemProxyResultMessage` (33) +
`NotifySystemProxyResultAsync` (12).
**Mekanizma:** test yürütme + sonuç mesajı kompozisyonu servise; DOM push pencereye. Mesaj
kompozisyonu saf fonksiyon (9 dil için `ResUI` anahtarları) — ServiceLib'de testlenebilir.
**Risk:** düşük. **Kazanç:** ~90 satır + mesaj matrisi testli.

### Taşınamaz / dokunulmaz (yalnızca belgele)
- **Ctor (105-542, ~438):** R — XAML adlarına bağlı kompozisyon kökü; `WhenActivated` +
  `BindCommand` duvarı (~150 satır tek-satır bağ) ReactiveUI boilerplate'idir, taşınamaz.
  Ctor'daki AppEvents abonelik duvarı (187-274) W4-B ile hafifler (sağlık beslemesi servise).
- **Pencere kromu (543-658) + sürükle (2286-2377) + ShowHideWindow/OnLoaded/TryCreateTrayIcon
  (3769-3847):** C — HwndSource/WindowProc/StateChanged/TaskbarIcon ömrü.
- **Başlangıç (658-816):** C/D karışık — NavigationCompleted bir kez koşan başlangıç sıralaması;
  theme→web-id eşleme switch'i (~25 satır) tek `static ThemeWebIdMapper`'a çekilebilir (küçük
  kazanç, isteğe bağlı) ama sıralamanın kendisi pencerede kalmalı (WebView olayı).
- **Menüler/klavye/nav/layout (3615-3999, ~385):** C — XAML kontrol adları.
- **Delegasyon duvarı (2874-3027):** D — IDashboardBridge yüzeyi; 30+ tek-satır, taşıma
  inceltmez.
- **HandleAppControl (2068-2139, 72):** C/D — başlık çubuğu komutları (minimize/close/drag/
  maximize) krom; reboot/reload ViewModel/AppManager'a gider.

---

## 4. Önerilen dalga sırası ve doğrulama tarifi

Önerilen uygulama sırası (her biri bağımsız commit, aynı doğrulama kapısı):

1. **W4-B** ✅ (hata kartı defteri — uygulandı, 16 test) → sıradaki: **W4-A** (poll süpervizörü)
   → W4-C (IP doğrulama kuralı) → W4-E (Reality orkestrasyonu) → W4-F (proxy testi).
2. **W4-D** (bağlantı orkestratörü) tek başına, ayrı bir seansta — A/C/E/F'nin üstüne biner,
   `_connectionToggleGate`/`_connectionStarting` gibi alanları da taşır.

Her dalga için Wave-2b kapısı: `dotnet build AoGPN.sln` → 0 hata → taşınan her üyenin pre-move
metinle byte-diff'i (görünürlük/delege-rename normalleştirilmiş) → `dotnet test ServiceLib.Tests`
→ 0 failed / mevcut yeşil + yeni testler. WPF tarafının otomatik testi yoktur; byte-exact kesim +
derleyici güdümlü yeniden bağlama güvenlik ağıdır. Dashboard JS süiti (`Temalar/skins`,
node:test) 212/212 korunmalı (DOM sözleşmesi değişmez).

**Beklenen sonuç:** W4-B ✅ sonrası MainWindow **3.752**. W4-A/C/E/F → ~3.400-3.500; W4-D dahil
→ ~3.000-3.200. Altına inmek için krom/kompozisyon gövdesi (~1.500-1.700 satır) kalır — orada
durulur.

---

## 5. İlgili dosyalar

- `AoGPN/AoGPN/Views/MainWindow.xaml.cs` — bölge haritası §1/§3'teki satır aralıkları.
- `AoGPN/AoGPN/Services/` — mevcut çıkarımlar (Wave 2b/3 deseni örnekleri) +
  `WindowLifecycleService` (W4-A için TrayWindowCoordinator desen referansı
  `ServiceLib/Services/TrayWindowCoordinator.cs`).
- `docs/connection-tray-cluster.md` — küme envanteri + durum modelleri + §6 blueprint.
- Test desenleri: `ServiceLib.Tests/TrayWindowCoordinatorTests.cs`,
  `ServiceLib.Tests/Services/RealityCoreFallbackAdvisorTests.cs`.
