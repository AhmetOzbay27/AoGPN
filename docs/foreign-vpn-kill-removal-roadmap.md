# Yabancı VPN Otomatik Kapatma Özelliğini Kaldırma Yol Haritası

> **Durum:** Faz 1–3 **uygulandı** (2026-09-03), Faz 4 (CHANGELOG + dokümantasyon)
> **tamamlandı**; Faz 5 (manuel doğrulama) **bekliyor**.
> **Kapsam kararı:** **Öldürme (kill) kaldırılır, tespit + uyarı KORUNUR.**
> Yani AoGPN hiçbir üçüncü taraf süreci asla sonlandırmayacak; bağlanmadan önce
> çakışma olursa yalnızca kullanıcıyı bilgilendirecek.

---

## 1. Özellik ne yapıyor?

Bağlanma akışının başında AoGPN, `ForeignTunnelDetector` servisiyle şunları tarar:

1. **TUN adaptör çakışması** — başka bir VPN istemcisine ait aktif TUN adaptörü
   (`xray_tun`, `WireGuard Tunnel`, wintun tabanlı adaptörler...).
2. **Port çakışması** — AoGPN'nin yerel SOCKS portunda yabancı bir dinleyici
   (AoGPN'nin kendi çekirdeği dinliyorsa sayılmaz).
3. **Süreç çakışması** — `KnownForeignVpnProcessNames` listesindeki tanınmış
   üçüncü taraf VPN istemcileri:
   `wireguard.exe, wg.exe, v2rayn.exe, nekoray.exe, nekobox.exe, hiddify.exe,
   mullvad.exe, mullvadvpn.exe, openvpn.exe, openvpn-gui.exe, protonvpn.exe,
   nordvpn.exe, surfshark.exe, windscribe.exe, tailscale.exe, outlinevpn.exe`

Tespit edilen **16 istemci adından herhangi biri çalışıyorsa süreç ağacıyla birlikte
`Process.Kill(entireProcessTree: true)` ile zorla kapatılır** ve ~1,2 sn parçalanma
beklenir. TUN/port çakışmaları öldürülmez, yalnızca uyarılır.

### Neden var?

İki TUN yığını aynı anda çalışırsa paketleri birbirinden çalar ve interneti kırar;
dolu bir proxy portu çekirdeğin bind olmasını engeller. Özellik 7.26.39'da
**yalnızca uyarı** olarak eklendi (CHANGELOG satır 413–421). Otomatik öldürme daha
sonra eklendi ve `AoGPN/docs/gpn-mihomo-integration.md` satır 38'de
**"İtalya kill — mihomo TUN'un kanıtlanmış çalışma koşulu"** olarak belgelendi
(mihomo TUN'lu GPN akışı bu kill ile test edilmişti).

---

## 2. Araştırma envanteri — dokunulacak her nokta

### 2.1 Öldürme mantığı (kaldırılacak)

| Dosya | Konum | Ne yapıyor |
|---|---|---|
| `AoGPN/ServiceLib/Services/ForeignTunnelDetector.cs` | `KnownForeignVpnProcessNames` (~79) | 16 istemciden oluşan öldürme listesi |
| `.../ForeignTunnelDetector.cs` | `KillForeignClients` (168) | Asıl `Process.Kill(entireProcessTree: true)` |
| `.../ForeignTunnelDetector.cs` | `ResolveForeignClientsAsync` (122) | Tespit + öldürme + 1,2 sn bekleme |
| `.../ForeignTunnelDetector.cs` | `_killForeignProcesses` alanı + ctor parametresi (105) | Test edilebilirlik için kill delege'si |

### 2.2 Öldürme çağrı noktaları (kaldırılacak/küçültülecek)

| Dosya | Konum | Akış |
|---|---|---|
| `AoGPN/AoGPN/Views/MainWindow.xaml.cs` | `WarnOnForeignTunnelBeforeConnectAsync` (2482; çağrı 2375) | Normal bağlanma — kill + `ForeignTunnelAutoKilled` bildirimi + kalıntı uyarısı |
| `AoGPN/ServiceLib/Services/GpnCoreLauncher.cs` | `CloseForeignTunnelsBeforeStartAsync` (~205; çağrı 212) | GPN mihomo TUN akışı — best-effort kill |
| `AoGPN/ServiceLib/Services/Gpn/GpnCaptureBridge.cs` | `ResolveForeignTunnelBeforeConnectAsync` (~353; çağrı 368) | GPN WinDivert yakalama köprüsü — best-effort kill |

### 2.3 Testler

| Dosya | Konum | Not |
|---|---|---|
| `AoGPN/ServiceLib.Tests/Services/ForeignTunnelDetectorTests.cs` | 116, 172, 196, 217 | Kill davranışını test eden 4 test; 6 tespit testi korunacak |
| `AoGPN/ServiceLib.Tests/Services/GpnCaptureBridgeTests.cs` | 139, 182, 222, 271 | Ctor'a `ForeignTunnelDetector` veriyor — imza değişirse güncellenir |

### 2.4 Kaynak dizeleri

| Dosya | Konum |
|---|---|
| `AoGPN/ServiceLib/Resx/ResUI.resx` | 2536–2542 (`ForeignTunnelDetailTun`, `ForeignTunnelDetailPort`, `ForeignTunnelAutoKilled`) |
| `AoGPN/ServiceLib/Resx/ResUI.tr.resx` | 2485–2491 (aynıları) |
| `AoGPN/ServiceLib/Resx/ResUI.Designer.cs` | 6781–6795 (üretilmiş erişimci) |

### 2.5 Dokümantasyon

| Dosya | Konum |
|---|---|
| `CHANGELOG.md` | 413–421 (özellik geçmişi) — yeni sürüm notu eklenecek |
| `AoGPN/docs/gpn-mihomo-integration.md` | 16, 38 ("yabancı tünelleri kapatır", "İtalya kill") |
| `docs/gpn-migration-roadmap.md` | 54, 862, 909, 953 |
| `docs/xray-tun-windows.md` | 91, 105 |
| `.github/workflows/wintun-driver-smoke.yml` | 28 (`ForeignTunnelDetector.cs` path filtresi — değişiklik CI'ı tetikler, sorun değil) |

### 2.6 DOKUNULMAYACAKLAR (özellik değil, app kendi süreçlerini yönetiyor)

- `CoreManager.KillOrphanCoreProcesses` (~460) — yalnızca **AoGPN'nin kendi
  dizinlerindeki** orphan `xray`/`sing-box`/`mihomo` süreçlerini öldürür;
  exe yolu doğrulanır. 7.26.42'de tam da yabancı istemcileri öldürmemek için
  düzeltildi. **Kalır.**
- `ProcessService.StopAsync`/`Dispose` — AoGPN'nin kendi çocuk süreçleri.
- `AoGPN.Updater/Program.cs` — güncelleme sırasında AoGPN'nin kendisi (AmazTool/UpgradeApp.cs bu yola birleştirilip silindi).
- `MainWindow` 2955, `SplitTunnelViewModel` 559, `ConnectionMonitorViewModel` 546,
  `ProcessCatalogService` 154, `WindowsJobService` 60 — salt-okunur süreç aramaları.
- `AoGPN/tmp-mihomo/*.ps1` — derlemeye girmeyen geliştirme kabuk scriptleri.

---

## 3. Fazlar

### Faz 0 — Temel (önce bunu yap)

1. `AoGPN/ServiceLib.Tests` projesini mevcut hâliyle çalıştır → yeşil taban çizgisi.
   (Windows'ta WireGuard açıkken bazı testler çakışabilir — kapatıp çalıştırın,
   bkz. `GpnProbeTool` 625'teki not.)
2. `dotnet build` ile çözümün derlendiğini doğrula.

### Faz 1 — Öldürme kodunu çıkar (ServiceLib) ✅ uygulandı

`ForeignTunnelDetector.cs`:
- **Sil:** `_killForeignProcesses` delege alanı + ctor parametresi.
- **Sil:** `KillForeignClients` (statik öldürücü).
- **Sil:** `ResolveForeignClientsAsync` — çağıranlar artık `Detect`'i çağırıp kendi
  DiagLog/SaveLog satırlarını yazar (ayrı `DetectAndLogAsync` eklenmedi — API yüzeyi
  küçük tutuldu).
- **Kor:** `KnownForeignVpnProcessNames` (artık "uyarı/rapor listesi"),
  `EnumerateForeignVpnProcesses`, `Detect`, `ForeignTunnelConflictResult`.
- `ForeignTunnelConflictResult.ForeignProcessNames` anlamı değişir: "öldürülecek
  aday" değil, "yan yana çalışan istemci" — açıklama yorumlarını güncelle.

Çağrı noktaları:
- `MainWindow.WarnOnForeignTunnelBeforeConnectAsync`: kill bloğunu, `Task.Delay(1200)`
  yeniden-tarama bloğunu ve `ForeignTunnelAutoKilled` bildirimini **sil**. TUN/port
  uyarı yolu aynen kalır. Süreç çakışması artık yalnızca loga düşer (istersen
  uyarı metnine "X yan yana çalışıyor" bilgisi eklenebilir — isteğe bağlı).
- `GpnCoreLauncher.CloseForeignTunnelsBeforeStartAsync` → tespit + `GPN_LAUNCH`
  DiagLog uyarısı; kill yok, bağlantı akışı yine asla engellenmez (best-effort korunur).
- `GpnCaptureBridge.ResolveForeignTunnelBeforeConnectAsync` → tespit + DiagLog;
  kill çağrısı yok.

### Faz 2 — Kaynak dizeleri ve UI temizliği ✅ uygulandı

- `ResUI.resx`, `ResUI.tr.resx`, `ResUI.Designer.cs` içinden
  `ForeignTunnelAutoKilled` anahtarını üç dosyada birden sil (tutarlılık şart).
- `MainWindow` içinde kullanılmayan `killed`/yeniden-detect kodunu kaldır.
- `ForeignTunnelDetailTun` / `ForeignTunnelDetailPort` KALIR.

### Faz 3 — Testleri güncelle ✅ uygulandı

`ForeignTunnelDetectorTests.cs`:
- **Sil:** `KillForeignClients_UnknownName_ReturnsEmptyWithoutThrowing` (116),
  `ResolveForeignClientsAsync_ForeignVpnProcess_KillsIt` (172).
- **Güncelle:** `ResolveForeignClientsAsync_Clean_ReturnsEmpty_NoKill` (196),
  `ResolveForeignClientsAsync_ForeignTunOnly_DoesNotKill` (217) → yeni imzaya.
- **Ekle:** "Süreç tespit edilir ama asla öldürülmez" regresyon testi —
  sahte `foreignProcessNames` dönen detector'da `Detect` süreci raporlar ve
  `Process.GetProcessesByName` ile gerçek bir kill olmadığını doğrular.
- `GpnCaptureBridgeTests` ctor çağrılarını yeni imzaya uyarla (detector artık
  kill delege'si almıyor).

### Faz 4 — Dokümantasyon ✅ tamamlandı

CHANGELOG'a `[7.26.44] — Foreign VPN clients are no longer auto-killed` eklendi;
`AoGPN/docs/gpn-mihomo-integration.md` (16, 38, 150) ve `docs/gpn-migration-roadmap.md`
(861-868) yeni davranışa göre güncellendi. `docs/xray-tun-windows.md` (91, 105)
detektörün beyaz liste/uyarı davranışından bahseder — bunlar değişmedi, dokunulmadı.

- `CHANGELOG.md`'ye yeni sürüm notu: "Yabancı VPN istemcileri artık otomatik
  kapatılmaz; çakışma durumunda yalnızca uyarı gösterilir."
- `AoGPN/docs/gpn-mihomo-integration.md` satır 16/38: "kapatır" ifadelerini
  "uyarır/raporlar" ile değiştir; "İtalya kill" notunu geçmiş kaydı olarak işaretle.
- `docs/gpn-migration-roadmap.md` (54, 862, 909, 953) ve `docs/xray-tun-windows.md`
  (91, 105) ilgili cümleleri güncelle.

### Faz 5 — Doğrulama ⏳ bekliyor (manuel senaryolar)

1. `dotnet build` + `ServiceLib.Tests` tam koşu (hedef: 0 kill testi, tüm tespit
   testleri yeşil).
2. Manuel senaryolar:
   - WireGuard for Windows açıkken AoGPN'ye bağlan → **süreç ölmemeli**, TUN
     çakışma uyarısı görünmeli, istenirse bağlantı devam etmeli.
   - v2rayN TUN açıkken bağlan → aynı davranış.
   - Temiz sistemde bağlan → uyarı yok, davranış değişmedi.
   - GPN (mihomo) bağlantısı, WireGuard açıkken → DiagLog'da
     `GPN_LAUNCH foreign scan ...` uyarısı, kill yok.
   - Port çakışması (başka uygulama SOCKS portunu tutuyor) → uyarı görünür.

---

## 4. Riskler ve notlar

| Risk | Etki | Karşı önlem |
|---|---|---|
| İki TUN aynı anda → internet kesilir | Kullanıcı v2rayN/WireGuard'ı manuel kapatmazsa GPN/bağlanma bozuk görünebilir | Faz 1'deki korunan uyarı bunu önceden bildirir; kullanıcı seçimini yapar |
| Dolu proxy portu → core bind olamaz | Bağlanma hata verir | Korunan port uyarısı açıklar |
| mihomo GPN akışı kill ile test edilmişti | Kill kalkınca o akış yabancı istemci varken eskisi gibi "garantili temiz" olmaz | DiagLog uyarısı + dokümantasyon; davranış kullanıcı kontrolüne geçer |

**Kapsam dışı (bu yol haritasının):** kill'i ayar/toggle'a çevirmek, onay
diyaloğu eklemek, tespit/uyarıyı da kaldırmak. Karar verilirse ayrı iş olarak
değerlendirilir.

---

## 5. Uygulama sırası (özet)

```
Faz 0: test taban çizgisi          → Faz 1: kill kodunu çıkar (detector + 3 çağrı noktası)
→ Faz 2: ResUI temizliği           → Faz 3: testleri güncelle (regresyon testi ekle)
→ Faz 4: CHANGELOG + docs          → Faz 5: build + test + manuel doğrulama
```