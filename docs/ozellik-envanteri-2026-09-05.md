# Özellik/Nitelik Envanteri + Program Mantığına Göre Sınıflandırma (2026-09-05)

**Kapsam:** Tüm kullanıcıya açık yüzey + ayar modeli + görünür miras kod.
**Program mantığı (kullanıcı tanımı):** (1) Split-bağlantıyla oyun ping'ini en aza indir,
en stabil tüneli kur; (2) launcher (BSG/WARP) engellerini bypass mantığıyla aş; (3) gerektiğinde
normal VPN gibi kullanılabilir ol. Bu üç amaca hizmet etmeyen, onlarla çelişen ya da kafa karışıklığı
yaratan yüzey = kaldırma adayı.

---

## 1. Modern yüzey (AoGPN kimliği — KORUNUR)

### 1.1 Dashboard görünümleri (WebView2, `vpn-gpn-dashboard.html`)
| Görünüm | İçerik | Mantık |
|---|---|---|
| **Dashboard** | Bağlan, mod (GPN/VPN/Off), transport (proxy/tun), gerçek IP/sızıntı doğrulama, telemetri (ping/kayıp/hız), capture-drift uyarısı | ✓ çekirdek |
| **Nodes** | Düğüm listesi, test, kopyala/sil/devre dışı, dedup, node pool linkleri | ✓ VPN ayağı |
| **GPN Servers** | İtalya/Almanya WG kataloğu, import/ekleme, varsayılanları geri yükle | ✓ çekirdek |
| **Boost** | Oyun ekle, gerçek-ping öncesi/sonrası, route testi | ✓ çekirdek |
| **Perf** | Canlı bağlantı tablosu / izleme | ✓ çekirdek |
| **Settings** | Genel + GPN + launcher-bypass (WARP/VLESS/DIRECT) + çekirdek/protokol + yakalama | ✓ çekirdek |
| **About** | Sürüm notları | ✓ |

### 1.2 GPN'e özgü nitelikler (Config GUIItem / ConnectionSettingsItem / Gpn*)
AutoRun, GpnEnableRecoveryWatch, GpnEnableWarpAutoRecover, GpnEnableFailover, GpnProbe (zaman
aşımları), GpnSeed{Italy,Germany}PrivateKey, LauncherBypassesJson + VlessBypassNodeJson, Mode
(Off/Manual/Vpn), Transport, InvertManualRouting, ProtocolPreference, AutoConnectOnGameStart,
AutoReconnect(+MaxAttempts), GpnCaptureItem (kuyruk), GpnWintunItem (adaptör) — hepsi çekirdek.

### 1.3 Sistem/proxy + görsel
SystemProxyItem (Set/PAC/Clear + istisnalar), TUN (EnableTun, stack, StrictRoute, MTU),
EffectsMode (full/balanced/reduced), HWA koruma, 9 dil, tema, tepsi davranışı — korunur.

---

## 2. Miras v2rayN yüzeyi (SOL RAY + ÜST MENÜ — hâlâ erişilebilir)

### 2.1 Sol ray sekmeleri (`tabMain2`, ctor nav butonları)
`btnNavServers` (Profiles) · `btnNavMsg` (günlük) · `btnNavProxies` (Clash proxy listesi) ·
`btnNavConnections` (Clash bağlantı listesi) · `btnNavConnection` (dashboard). Ayrıca
`btnNavAddServer`/`btnNavImport`/`btnNavScan` (v2rayN içe aktarma), `btnNavGameBoost`,
`btnNavSettings`, `btnNavRouting`, `btnNavDNS` (v2rayN Routing/DNS pencereleri).

### 2.2 Üst menü (v2rayN kopyası, `MainWindow.xaml`)
| Menü | Öğeler | Mantık değerlendirmesi |
|---|---|---|
| **Servers** | Pano/QR tarama/ekrandan görüntü + **17 tekil protokol ekleme penceresi** (Custom, PolicyGroup, ProxyChain, VMess, VLESS, Shadowsocks, Trojan, Hysteria2, WireGuard, SOCKS, HTTP, TUIC, AnyTLS, Naive) | v2rayN kalıntısı — dashboard Nodes görünümü içe aktarma yapıyor |
| **Subscription** | Ayar, güncelle, proxy üzerinden güncelle, grup güncelle (×2) | v2rayN kalıntısı — node-pool linkleri aynı işi görür |
| **Settings** | OptionSetting, RoutingSetting, DNSSetting, FullConfigTemplate, **GlobalHotkeySetting (kod karantinada — ÖLÜ menü)**, RebootAsAdmin, SetUWP (EnableLoopback.exe), ClearServerStatistics, **RegionalPresets (Default/Russia/Iran — v2rayN bölge)** | karışık |
| **Help** | CheckUpdate, OpenLogFolder, VerboseLogging, core web siteleri (dinamik) | kısmen |
| **Reload / Promotion / Close** | Yeniden yükle / **Promotion (reklam URL)** / Kapat | Promotion v2rayN kalıntısı |

### 2.3 Alt görünüm pencereleri (miras)
`AddServerWindow` (17 protokol formu + Reality vb.), Option/Routing/DNS ayar pencereleri,
GlobalHotkeySettingWindow, SubscriptionSettingWindow, BackupAndRestore, CheckUpdateView,
`MainWindowViewModel`'de 35 komut (çoğu bu pencerelere gider).

---

## 3. Ölü / çelişkili / karmaşa bulguları (kanıtlı)

1. **GlobalHotkeySetting menüsü — K1 ADAYI GERİ ÇEKİLDİ (yanlış öncül).** İnceleme, uygulamanın
   kendi `WM_HOTKEY` motorunun (`AoGPN/AoGPN/Manager/HotkeyManager.cs`, ctor'da
   `WindowsManager.RegisterGlobalHotkey` → MainWindow.xaml.cs:530) **çalıştığını** gösterdi;
   `Silinecekler_Yedek/GlobalHotKeys` üçüncü taraf kopyasıdır, uygulama motoru değil. Özellik
   fonksiyonel → kaldırılmadı.
2. **17 tekil "Sunucu Ekle" penceresi** — dashboard Nodes görünümü bağlantı/içe aktarma
   akışını sahipleniyor; tekil protokol formları yalnızca "normal VPN" için VLESS/WG dışında
   kullanılmıyor → geniş yüzey karmaşası.
3. **Subscription bloğu** — AoGPN GPN mantığında yeri yok; düğüm kaynağı node-pool
   linkleri + GPN kataloğu. İki paralel "kaynak getir" yolu kafa karıştırır.
4. **Clash UI panelleri** (Proxies/Connections sekmeleri + ClashUIItem) — mihomo artık iç
   motor; canlı izleme Perf görünümünde. Legacy panel çifti gereksiz yüzey.
5. **RegionalPresets (Default/Russia/Iran)** — v2rayN bölgesel ön ayarları; AoGPN sunucu
   hedefi Almanya/İtalya. RU/IR ön ayarı mantıkla çelişmez ama anlamsız + olası karışıklık.
6. **Promotion menüsü** — base64 reklam URL'si (v2rayN kalıntısı).
7. **SetUWP (EnableLoopback.exe)** — v2rayN UWP loopback aracı; oyun/launcher akışıyla ilgisiz.
8. **`MsgUIItem`, `ColumnItem`, `WindowSizeItem`(legacy grid), `ClashUIItem`** vb. model
   özellikleri — çoğu yalnızca test fikstürlerinde; kullanıcı yüzeyi yok. (Model temizliği
   ayrı görev — serileştirme geriye dönük uyumluluk ister.)

---

## 3.5 Uygulama günlüğü (2026-09-05, onay sonrası)

Kullanıcı onayı: **K1–K5 tümü, karantina yöntemi** → sonra kanıta dayalı daraltma: **görünür sol-ray
miras öğeleri** kapsamı (anket sonucu). Üst menünün `legacyToolbar` içinde `Collapsed` olduğu ve
GlobalHotkey motorunun çalıştığı tespit edilince kapsam netleştirildi ve uygulandı:

**Uygulanan (build 0 hata + ServiceLib.Tests 1.162/5/0 yeşil):**
- `btnNavServers` sol-ray butonu kaldırıldı (legacy profil ızgarasına erişim kapandı — düğüm
  yönetimi dashboard **Nodes** görünümünde).
- `tabProfiles2` sekmesi XAML'den + `ProfilesViewModel` ViewHost aboneliği koddan kaldırıldı;
  sekme indeksleri yeniden numaralandı (Msg 0, ClashProxies 1, ClashConnections 2, Dashboard 3;
  `SyncActiveNav`, ctor nav kablolaması, `TabMainSelectedIndex=3` güncellendi).
- **More Tools** genişleticisindeki `btnNavAddServer` (9 protokol içeren bağlam menüsü:
  Custom/VMess/VLESS/Shadowsocks/Trojan/Hysteria2/WireGuard/SOCKS/HTTP) + nav* bağları kaldırıldı;
  **Import (pano) ve Scan (QR) korundu.**
- `SetActiveNav` dizisi temizlendi.

**Yapılmayan (gerekçeli):**
- **K1 GlobalHotkeys** — çalışan özellik, öncül yanlıştı (yukarı).
- **Gizli `legacyToolbar`** (17'li ekleme + Subscription + Settings kalıntı menüleri) — görünmez,
  görünür yüzeyi etkilemez; kod temizliği ayrı iş.
- Sol-ray **Proxies / Msg / Routing / DNS** öğeleri — bu turda kapsam dışı bırakıldı.
- ViewModel komutları (Add*/Sub* vb.) ve AddServerWindow dosyaları — kullanıcı yüzeyi kapalı ama
  dosyalar yerinde; derin karantina ayrı dalga gerektirir.

---

## 4. Önerilen kaldırma kümeleri (onaya açık)

| Küme | Kapsam | Etki | Risk |
|---|---|---|---|
| **K1 — Ölü GlobalHotkey yüzeyi** | XAML menü + MVVM komutu + `OnHotkeyHandler`/`KeyEventItem` + EGlobalHotkey kalıntısı | 5-8 dosya | Düşük (motor zaten yok) |
| **K2 — Tekil protokol ekleme menüleri** | 17 Add* menü öğesi + WhenActivated bağları; formlar karantinaya | 3-4 dosya (menü gizlenir/kaldırılır) | Orta — AddServerWindow durur (QR/import için) |
| **K3 — Subscription menü bloğu** | Üst menü 5 öğe + MVVM komutları | 3-4 dosya | Orta |
| **K4 — Clash UI sol-ray panelleri** | Proxies/Connections sekmeleri + nav butonları | 2-3 dosya | Orta — Profiler/messages değerlendirilir |
| **K5 — Küçük kalıntılar** | Promotion, RegionalPresets, SetUWP, CheckUpdate(menü) | 3-5 dosya | Düşük-orta |

**Yöntem önerisi:** her küme önce `Silinecekler_Yedek/...` karantinasına taşınır (geri alınabilir,
önceki desenle tutarlı), build 0 + tam test süiti + dashboard JS yeşil tutularak. Onay sonrası
karantinaya alınan parçalar bir sonraki temizlikte kalıcı silinir.
