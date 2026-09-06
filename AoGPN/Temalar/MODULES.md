# MODULES.md — AoGPN Dashboard Modül Mimarisi

Dashboard, WebView2 içinde çalışan tek sayfalık bir web uygulamasıdır. Eskiden
tüm mantık tek dosyada (`app.js`, ~6.800 satır) toplanıyordu; artık her özellik
kendi modülünde yaşar ve `app.js` yalnızca **koordinatör**dür (durum, host
köprüsü kayıtları, skin motoru, başlatma akışı).

Modüller klasik `<script>` etiketleriyle (IIFE) yüklenir — **ES modül yok**.
Her modül `window.aogpn` ad alanına kendi API'sini kaydeder; modüller
birbirine doğrudan değil, yalnızca bu ad alanı üzerinden bağlanır.

---

## 1. Yükleme Sırası

Sıra **kritiktir**: her modül yalnızca kendinden ÖNCE yüklenen modüllerin
API'sine modül-toplamda erişebilir. (`aogpn.app` sonradan yüklenir — onu
kullanan modüller erişimi **lazily** yapar, bkz. Bölüm 5.)

`vpn-gpn-dashboard.html` ve test sandbox'ı (`skins/skin-sandbox.js`
`MODULE_FILES`) bu sırayı birebir paylaşır — ikisine de aynı anda ekleme
yapılır:

```
core/events.js               (1) olay veriyolu
core/util.js                 (2) $, escHtml
core/dom.js                  (3) dashboard element referansları
core/bridge.js               (4) postToHost / hasWebViewBridge
core/i18n.js                 (5) t(), dil yükleme, dil popover'ı
core/selects.js              (6) temalı <select> yükseltmesi
core/theme.js                (7) tema motoru, canvas efektleri, konfeti
core/tooltips.js             (8) özel tooltip motoru
features/window-controls.js  (9)  küçült/büyüt/kapat + başlık sürükleme
features/release-notes.js    (10) Sürüm Notları + setAppInfo
features/telemetry.js        (11) gauge kalibrasyonu, canlı kartlar, oturum sayacı
features/settings.js         (12) Ayarlar formu
features/game-boost.js       (13) domain kural paneli + EXE sürükle-bırak
features/gpn.js              (14) GPN paneli (sunucu yönetimi, failover, diyagnoz)
features/views.js            (15) görünümler, monitor, split routing, boost kartları
features/nodes.js            (16) VPN Rotaları (kartlar, test, havuz)
features/connection.js       (17) bağlantı durumu, IP doğrulama, host köprüsü
app.js                       (18) koordinatör (durum + skinBridge + init)
```

`features/*` modülleri `aogpn.gpn` gibi **kendi aralarında** bağımsızdır;
birbirlerine ihtiyaç duyduklarında bile sıra önemsizdir, çünkü erişim her
zaman fonksiyon çağrısı anında (lazily) yapılır.

---

## 2. Modül Sorumlulukları

### core/ — çekirdek altyapı

| Modül | Sorumluluk | API |
|---|---|---|
| `events.js` | Çapraz modül bildirimleri. Modüller birbirini hiç çağırmaz; `skin-changed` gibi olayları buradan yayınlar/dinler. | `aogpn.events = { on, emit }` |
| `util.js` | Ortak saf yardımcılar. | `aogpn.util = { $, escHtml }` |
| `dom.js` | Sık kullanılan dashboard element referansları (`statusDot`, `ipDisplay`, `connectionStatusLine` …). Yalnızca tek seferlik `getElementById` sonuçları — animasyonlu/değişken elemanlar için `$()` kullanılır. | `aogpn.dom = { btnIcon, btnText, btnSub, statusDot, statusLabel, nodeName, nodeAddr, ipDisplay, ipCountry, ipVerifyText, ipVerifyDetail, ipVerifyCard, ipLeakBanner, ipLeakText, ipOkBanner, ipOkText, ipOkTunnelIp, ipOkIspIp, pingVal, pingSub, lossVal, lossSub, downVal, upVal, downGauge, upGauge, systemProxyToggleBtn, systemProxyDot, systemProxyLabel, systemProxyAddress, systemProxyModeSelect, proxyTestBtn, proxyTestIcon, proxyTestLabel, quickProtocolSelect, quickAutoReconnect, protocolHint, routeHint, transportHint, connectionStatusLine }` |
| `bridge.js` | WebView2 host köprüsü. `postToHost` mesaj gönderir; mesaj sözleşmesi `DashboardMessageDispatcher.cs` ile birebir eşleşir. `hasWebViewBridge()` tarayıcı önizlemesini ayırt eder. | `aogpn.bridge = { hasWebViewBridge, postToHost }` |
| `i18n.js` | Çeviri motoru: `t()`, sözlük yükleme (`Dil/*.json`), dil popover'ı, ülke adları, `resolveKey`. | `aogpn.i18n = { resolveKey, t, applyTexts, loadLanguage, refreshDynamicTexts, refreshConnectLabels, LANGUAGES, langName, countryDisplayName, renderLangPopover, applyLanguage, getLang, getDict }` |
| `selects.js` | Native `<select>` → temalı açılır menü yükseltmesi. | `aogpn.selects = { upgradeSelect, refreshCustomSelect, refreshAllCustomSelects }` |
| `theme.js` | Tema motoru: tema destesi, canvas efektleri (matrix/konfeti), ses, efekt katmanı (tier/reduce/hidden), layout, perf durumu. | `aogpn.theme = { applyTheme, fireConfetti, applyLayout, renderThemeDeck, renderTopThemePopover, loadThemesFromDisk, initCanvasEffects, playThemeSound, setEffectsTier, setReduceEffects, setEffectsHidden, applyEffectsHiddenNow, isEffectsHidden, getEffectsTier, getCurrentThemeId, getPerfState, THEMES, THEME_STORAGE_KEY, LAYOUT_STORAGE_KEY, onHiddenChange }` |
| `tooltips.js` | Özel tooltip motoru + MutationObserver. Kayıt defteri yok — kurulumu yüklenince kendisi yapar. | — (yan etki) |

### features/ — özellik modülleri

| Modül | Sorumluluk | API |
|---|---|---|
| `window-controls.js` | Pencere kontrolleri (küçült/büyüt/kapat), başlık çubuğu sürükleme, `setWindowMaximized` host köprüsü. | `window.setWindowMaximized` (host) |
| `release-notes.js` | Sürüm Notları sayfası + host'tan gelen uygulama bilgisi (`setAppInfo`). | `aogpn.releaseNotes = { loadReleaseNotesFromDisk, renderReleaseNotes, getAppInfo }` |
| `telemetry.js` | Gauge kalibrasyonu, canlı ping/loss/download/upload kartları, oturum sayacı, failover flaşı. Duruma `aogpn.app` üzerinden lazily erişir. | `aogpn.telemetry = { updateTelemetry, resetTelemetry, formatSessionTime, flashGpnTelemetry, startSession, stopSession, clearAvailabilityHold, holdAvailability }` |
| `settings.js` | Ayarlar formu: seçenek listeleri (`FALLBACK_OPTIONS`), form doldurma, toplama, kaydetme, pencere davranış bağları. | `aogpn.settings = { fillSettingsOptions, setSettingsField, collectSettings, saveSettings, settingsState, FALLBACK_OPTIONS }` |
| `game-boost.js` | Game Boost: domain kural paneli, EXE sürükle-bırak, filtre. | — (event listener'lar; `aogpn.app` lazily) |
| `gpn.js` | GPN paneli: sunucu yönetimi (CRUD + canlı ölçüm), sunucu kümesi kartı, diyagnoz akışı, WARP/WinDivert/Wintun kartları, PID havuzu, failover matrisi, karar günlüğü, GPN Bağlan rozeti. `window.setGpn*` host sözleşmesi burada yaşar. | `aogpn.gpn = { startGpnClusterLoop, startGpnProbeLoop, stopGpnProbeLoop, setGpnConnectState, setGpnRecoveryWatchUI, setGpnFailoverUI, bindGpnServersControls, getServers, getServerProbes, getRecoveryWatch, getFailover, getConnectState, getPidPool, getCaptureSettings, getWintunSettings, getCaptureStats, getFailoverMatrix, getSelectionPrediction, getTelemetrySnap, getResilienceLog, getDiagLines }` |
| `views.js` | Görünüm geçişleri (`showView`), Connection Monitor tablosu, split-routing tablosu (route select / remove / reorder), dashboard boost kartları, process kataloğu, host-çözümlü uygulama ikonları, `updateMonitorSnapshot` köprüsü. | `aogpn.views = { showView, viewTitleParts, renderSplitApps, renderMonitorConnections, renderDashboardBoostCards, renderProcessCatalog, setProcessPickerOpen, applySplitMode, applyDirection, updateGlobalPanel, updateTunProxyNotice, getRouteLabels }` |
| `nodes.js` | VPN Rotaları: düğüm durumu (realNodes, seçim, sürükle-sırala), kart çizimi, ping testi, düğüm havuzu (URL abonelikleri), bağlam menüsü, toplu işlemler. `window.updateNode*` host sözleşmesi burada yaşar. | `aogpn.nodes = { applyHostNode, refreshSessionNode, applyNode, renderTopNodeSelect, requestNodeSwitch, renderNodes, renderNodePool, getUseRealNodes, getRealNodes, getNodes, getNodesList, getActiveRealNodeId, getSelectedNode, setSelectedNode, setUseRealNodes }` |
| `connection.js` | Bağlantı durumu UI'si (`setConnected`, CONNECT halkası, durum satırı), mod/transport/protokol/system-proxy uygulayıcıları, IP doğrulama paneli + yeniden ölçüm döngüsü, bağlantı hatası kartı. `window.setConnectionState … setRealIpState` host sözleşmesi burada yaşar. | `aogpn.connection = { setConnected, updateConnectTooltip, updateStatusLine, applyMode, applyTransport, updateTransportLock, applyProtocolPreference, isProxyModeEnabled, syncQuickControls, applySystemProxyState, showConnectionError, applyRealIpState, applyIpFreshness, getConnectionError }` |

### app.js — koordinatör

- **Durum değişkenleri** (tek kaynak): `connected`, `connecting`, `mode`,
  `transport`, `protocolPreference`, `autoReconnect`, `systemProxy*`,
  `isAdmin`, `tunLocked`, `currentView`, `splitMode`, `invertManual`,
  `monitorSnapshot`, `processCatalog`, `_lastIpState`, `_lastTelemetry`,
  `_activeGpnServer`, `_activeGpnMode` …
- **`window.aogpn.app`**: durum erişimcileri (getter'lar + `setXxxRaw`
  yazıcıları) ve modül fonksiyonlarına delegasyonlar.
- **skinBridge**: skin iframe'lerinin dashboard'a ulaştığı API
  (`window.skinBridge`).
- **Skin motoru**: skin seçici, `applySkin`, skin manager.
- **Performans HUD**, proxy-test bağları, `topNodeSelect` bağlama.
- **`window.*` dışa aktarımları** ve başlatma akışı (init).

---

## 3. `window.aogpn.app` — Durum Kayıt Defteri

Modüllerin çoğu durumu app.js'den okur/yazar. Doğrudan app.js değişkenine
dokunmak **yasak**; her erişim kayıt defterinden yapılır:

```js
// okuma
aogpn.app.getConnected(); aogpn.app.getMode(); aogpn.app.getMonitorSnapshot();
// yazma (ham durum — UI tetiklemez)
aogpn.app.setMode('vpn'); aogpn.app.setConnectedRaw(true);
// fonksiyon delegasyonu (UI'yi de günceller)
aogpn.app.setConnected(true, false); aogpn.app.applyMode();
```

Kural: **ham durum yazımı** `setXxxRaw` ile, **davranışlı çağrı** (UI
güncelleyen) `aogpn.xxx` modül fonksiyonuyla yapılır. `aogpn.app` yalnızca
app.js yüklendikten sonra vardır — modüller ona **yalnızca fonksiyon
gövdesinde** (lazily) erişebilir, modül-toplamda asla.

---

## 4. Host Sözleşmesi (`window.*`)

C# tarafı (`DashboardMessageDispatcher.cs`) fonksiyonları `ExecuteScriptAsync`
ile çağırır. Bu isimler **uygulama sözleşmesidir ve değişmez** — fonksiyonların
*yaşadığı dosya* değişebilir, isim değişmez. Ana gruplar:

- **Bağlantı**: `setConnectionState`, `setTransport`, `setProtocolPreference`,
  `setAdminState`, `setSystemProxyState`, `setSystemProxyResult`,
  `setConnectionError`, `setRealIpState`, `setNexusConnected`,
  `setNexusConnecting`
- **GPN**: `setGpnResilience`, `setGpnNodeSwitch`, `setGpnConnectionInfo`,
  `setAvailabilityInfo`, `setGpnServers`, `setGpnServerProbes`,
  `setGpnDefaultsStatus`, `setGpnDiag`, `setGpnPidPool`,
  `setGpnCaptureSettings`, `setGpnWintunSettings`, `setGpnCaptureStats`,
  `setGpnFailoverMatrix`, `setGpnSelectionPrediction`, `setGpnTelemetry`,
  `setGpnResilienceLog`
- **Düğümler**: `updateNodeInfo`, `updateNodeListAppend`, `updateNodeListDone`,
  `updateDisabledNodes`, `updateNodePool`, `updateNodeTest`,
  `setNodeTestRunning`, `setNodeSwitchResult`, `notifyNodes`,
  `requestNodeSwitch`, `applyNode`
- **İzleme/ayarlar**: `updateMonitorSnapshot`, `updateProcessList`,
  `setAppIcons`, `applySettings`, `updateTelemetry`, `__aogpnT`,
  `setProxyTestResult`
- **UI**: `applyTheme`, `applySkin`, `setEffectsTier`, `setReduceEffects`,
  `setEffectsHidden`, `togglePerfHud`, `setAppInfo`, `setWindowMaximized`

Ayrıca `window.skinBridge` skin iframe API'si ve `window.postToHost` /
`window.escHtml` (skin'ler için) app.js'de kayıtlıdır.

---

## 5. Yeni Modül Ekleme Adımları

1. **Dosyayı oluştur**: `Temalar/features/<ad>.js` (veya çekirdekse
   `Temalar/core/<ad>.js`).
   ```js
   (() => {
     'use strict';
     const t = aogpn.i18n.t;
     const $ = aogpn.util.$;
     const postToHost = aogpn.bridge.postToHost;
     // ... modül mantığı ...
     window.aogpn = window.aogpn || {};
     window.aogpn.<ad> = { /* dışa açılan fonksiyonlar */ };
   })();
   ```
2. **Yükleme listelerine ekle** (ikisi birlikte, aynı sırayla):
   - `vpn-gpn-dashboard.html` → `<script src="Temalar/..."></script>`
   - `skins/skin-sandbox.js` → `MODULE_FILES` dizisi
3. **Bağımlılıkları `aogpn.*` üzerinden bağla**:
   - Önce yüklenen modüllerin API'sine modül-toplamda erişebilirsin.
   - `aogpn.app`'e yalnızca fonksiyon içinde (lazily) eriş.
   - Yeni durum gerekiyorsa app.js'e getter/`setXxxRaw` erişimcisi ekle
     (durum tek kaynakta kalır).
   - app.js'in modül fonksiyonuna ihtiyacı varsa `aogpn.app`'e delegasyon
     ekle: `modülFonksiyonu: () => aogpn.<ad>.modülFonksiyonu()`.
4. **Host sözleşmesi**: C# tarafının çağırdığı fonksiyonları
   `window.<Aynıİsim> = ...` olarak kaydet — isimleri değiştirme.
5. **Test**: `cd Temalar/skins && npm test` — sandbox modülleri aynı sırayla
   `eval` eder ve gerçek dashboard'u boot eder; 250 testin tamamı yeşil
   kalmalı. Yeni davranış için `dashboard.integration.test.js`'e test ekle.
6. **app.js başlığındaki** modül listesini ve bu dosyayı güncelle.

### Kurallar

- **IIFE + `'use strict'`** — ES module yok (test sandbox'ı `eval` ile yükler).
- **Doğrudan çapraz referans yok**: modüller yalnızca `aogpn.*` ve
  `window.*` üzerinden haberleşir.
- **`aogpn.app`'e lazy erişim**: modül-toplamda `aogpn.app` kullanma.
- **Ham durum** `setXxxRaw` ile, **davranış** modül fonksiyonuyla.
- Taşıma/kesim işlemleri için `Temalar/split-*.js` betikleri hazırdır
  (anchor doğrulamalı; her dalga sonrası testler yeşil tutulur).

---

## 5b. Paketlenmiş Fontlar

Dashboard ve skin'ler (`vpn-gpn-dashboard.html`, `skins/nexus`, `skins/cyber`)
uzak Google Fonts yerine **paketli** fontları kullanır: `fonts/fonts.css` +
woff2 dosyaları (`fonts/` klasörü, ~630 KB). Bu sayede offline kurulumda da
birebir aynı görünüm elde edilir ve açılışta ağ isteği olmaz.

- `fonts/fonts.css` **elle düzenlenmez** — `fetch-google-fonts.js` tarafından
  üretilir (Google css2 API'den indirir, @font-face bloklarını yerel dosyalara
  çevirir, aynı içerikli dosyaları tekilleştirir). Font seti değişince:
  `node Temalar/fetch-google-fonts.js` çalıştırıp çıktıyı commit'le.
- Font aileleri/ağırlıkları (Inter 400;500;600, Orbitron 500;600;700,
  Rajdhani 500;600;700, VT323) Google'ın o an servis ettiği kümeyle birebir
  aynıdır — uzak stil sayfasıyla aynı görünüm garantisi.

## 6. Test Altyapısı

`Temalar/skins/skin-sandbox.js` gerçek dashboard'u birebir yükler:
`MODULE_FILES` listesindeki modülleri ve ardından `app.js`'i `window.eval`
ile çalıştırır, async başlangıç zincirlerini (themes.json, Dil/*.json)
bekler ve rAF kuyruğunu boşaltır. Bu yüzden:

- Yükleme sırası değişiklikleri hem HTML'e hem `MODULE_FILES`'a yansımalı.
- Modüller klasik IIFE olarak kalmalı (eval uyumluluğu).
- Her test `bootDashboard()` ile taze bir dashboard boot eder — bir testin
  bıraktığı global durum diğerini etkilemez.