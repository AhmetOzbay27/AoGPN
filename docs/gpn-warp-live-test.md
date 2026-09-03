# GPN WARP Egress Canlı Test Rehberi — Tarkov (auth → WARP, oyun → VPN)

> **Kapsam:** Tarkov için kurulan WARP egress zincirinin uçtan uca doğrulaması.
> Hedef iki şeyin **gerçekten** doğru yoldan gittiğini kanıtlamak:
>
> | Uygulama | Rota | Çıkış | Amaç |
> |---|---|---|---|
> | `BsGLauncher.exe` (auth/giriş) | **WARP** | Cloudflare WARP IP'si (temiz) | profile.tarkov.com'un Cloudflare WAF'ı Oracle datacenter IP'sini engelliyor → login WARP'tan çıkmalı |
> | `escapefromtarkov.com` (domain kuralı — tüm süreçler) | **WARP** | Cloudflare WARP IP'si (temiz) | Oyunun kendi sürüm kontrolü de aynı WAF'ın arkasında; Oracle çıkışından "Unable to check the client version" ile reddedilir → API hostname'leri WARP'tan çıkmalı (kural, oyunun process kuralından ÖNCE gelir) |
> | `EscapeFromTarkov.exe` + `EscapeFromTarkov_BE.exe` | **VPN** | Almanya düğümü (Oracle) | düşük ping — WARP gecikme eklemez (raid sunucu IP'leri domain kuralına takılmaz, VPN yolunda kalır) |
>
> **Kanıt hiyerarşisi (güçlüden zayıfa):**
> 1. **Fonksiyonel:** launcher girişi başarılı ("Error on POST" yok) — asıl kabul kriteri.
> 2. **Access log eşleştirmesi:** sing-box `outbound/warp` satırları launcher uç noktalarını, `outbound/proxy` satırları oyun sunucusu IP'lerini taşıyor.
> 3. **Dashboard canlı rota sütunu:** `LIVE` kolonu her uygulamanın **canlı** bağlantılarını hangi outbound üzerinden kurduğunu gösterir (`WARP · N` / `PROXY · N` / `DİREKT BAĞLANTI`).
> 4. **Sunucu tarafı egress kanıtı (opsiyonel):** `tcpdump` ile launcher trafiğinin WARP SOCKS dinleyicisine geldiğinin görülmesi.

---

## 0) Ön koşullar

1. **WARP özellikli build.** Uygulama çalışıyorsa kapat (bin klasörünü kilitler), yeniden derle ve aç.
2. **Almanya WireGuard düğümü + sunucuda WARP dinleyicisi.** Sunucu tarafı hazır:
   - wireproxy, `10.66.66.1:40000` (SOCKS5 — WG tünelinden erişilir) ve `127.0.0.1:40000/40001` (bat yolu) dinliyor.
   - Doğrulama (sunucuda): `curl -s --socks5-hostname 10.66.66.1:40000 https://api.ipify.org` → Cloudflare IP'si dönmeli (Oracle değil).
3. **Verbose log açık** — access log eşleştirmesi için şart:
   - Dashboard → **Ayarlar → Log level: `debug`** (veya `info`)
   - **Verbose logging (diagnostics)**: AÇIK
   - Ayarlar sonrası **bağlantıyı kapat/aç** (çekirdek yeni log seviyesiyle yeniden başlar).
   - Not: verbose kapalıyken log seviyesi `warn`'a kısılır ve access satırları hiç yazılmaz.
4. **Kuralları manuel ekle** (preset yok — Game Boost'ta sırayla):
   1. **Ekle** → **EscapeFromTarkov.exe** → rota **VPN**;
      aynı şekilde **EscapeFromTarkov_BE.exe** → **VPN** ve **BsGLauncher.exe** → **WARP**.
   2. **Domain kuralı:** Game Boost'ta **⚡ BSG API → WARP** tek tık düğmesi
      (`escapefromtarkov.com` → WARP girer) — veya **＋ Domain** ile elle ekle; kuralı
      ↑/↓ oklarıyla oyunun process kuralından ÖNCE taşı (ilk eşleşen kazanır).
      Domain kuralı yoksa oyun açılırken **"Unable to check the client version"** alırsın
      (bkz. §6).

---

## 1) Bağlan ve dashboard rota sütununu doğrula

1. **Almanya WireGuard düğümünü** seç → **Bağlan**.
2. Bağlantı modu **WireGuard** olmalı. (Fallback'e düşülürse Game Boost'ta kırmızı **WARP uyarı bandı** görünür — bkz. §6.)
3. **Launcher'ı aç** (giriş ekranı) → birkaç saniye bekle.
4. **Oyunu başlat** → birkaç saniye bekle.

Game Boost tablosunda beklenen (canlı trafik akarken):

| PROGRAM | ROUTE | LIVE (canlı bağlantılar) |
|---|---|---|
| BSG Launcher (`BsGLauncher.exe`) | WARP | **`WARP · N`** ← auth trafiği WARP outbound'undan |
| Escape from Tarkov (`EscapeFromTarkov.exe`) | VPN | **`PROXY · N`** ← oyun trafiği düğümden |
| Escape from Tarkov (BattlEye) | VPN | `PROXY · N` (BE trafiği varsa) |
| Microsoft Edge vb. (listede değil) | Doğrudan | **`DİREKT BAĞLANTI`** |

> `LIVE` kolonu yalnızca **canlı** bağlantısı olan satırlarda dolar; uygulama kapalıyken `—` görünür. Sütun, uygulamanın **canlı bağlantılarına** kuralın atadığı outbound'u işler — config'den türetilir, gerçek gözlemle birleştirilir.

---

## 2) Core access log eşleştirmesi

Log dosyası: `<uygulama klasörü>\guiLogs\ao_singbox_<tarih>.log` (günlük dosya; eski günler ayrı dosyada).

> **mihomo çekirdeğinde:** aynı dizinde `ao_mihomo_<tarih>.log` yazılır
> (üretici `log-file` ile; WARP dial hataları `warp-socks` outbound adıyla oraya
> düşer — `WarpDialHealthMonitor` her iki dosyayı da izler).

**WARP (launcher) satırları:**

```powershell
Select-String -Path "$env:LOCALAPPDATA\..\..\..\Programlar\VPN\AoGPN\guiLogs\ao_singbox_*.log" "outbound/warp" | Select-Object -Last 20
```

> Uygulama klasörünüz farklıysa `guiLogs` dizinine giden tam yolu kullanın (ör. `D:\Programlar\VPN\AoGPN\AoGPN\bin\Release\...\guiLogs`).

**VPN (oyun) satırları:**

```powershell
Select-String -Path "...\guiLogs\ao_singbox_*.log" "outbound/proxy" | Select-Object -Last 20
```

**Beklenen içerik:**

| İşaret | Beklenen hedefler | Anlamı |
|---|---|---|
| `outbound/warp: outbound connection to ...` | `profile.tarkov.com`, `prod.escapefromtarkov.com`, launcher'ın auth/API uç noktaları | Auth trafiği WARP outbound'undan geçiyor ✅ |
| `outbound/proxy: outbound connection to ...` | oyun sunucusu IP'leri (EscapeFromTarkov'un bağlandığı adresler), `*.escapefromtarkov.com` oyun uç noktaları | Oyun trafiği düğüm (VPN) yolundan ✅ |
| `outbound/direct: ...` | diğer uygulamalar | listede olmayanlar direkt ✅ |

**Eşleştirme yöntemi:** launcher'da giriş yaptığın anda dashboard'da `BsGLauncher` → `WARP · 3` gibi bir sayı görürsün; aynı zaman penceresinde logda `outbound/warp` satırları artmalı (dosyanın sonuna `Select-Object -Last 20` ile bak — saniye damgaları dashboard trafik artışıyla örtüşür).

> **Not:** sing-box, endpoint'e giden bağlantıları `outbound/proxy` ya da `endpoint/proxy` biçiminde yazabilir; ayırt edici olan tag adıdır (`warp` vs `proxy`). Satırın tamamına bakın; her iki biçim de "VPN yolu" demektir.

---

## 3) Egress kanıtı (opsiyonel, kesin)

En kesin kanıt: launcher trafiğinin gerçekten sunucudaki WARP SOCKS'a varması.

**Sunucuda (Almanya):**

```bash
ssh -i .freebuff/ssh/alman.key ubuntu@130.61.223.36
sudo tcpdump -i wg0 port 40000 -n   # launcher açıkken birkaç saniye dinle
```

Beklenen: istemci WG IP'sinden (`10.66.66.2` vb.) `10.66.66.1:40000`'a giden TCP bağlantıları — bu, launcher trafiğinin WARP dinleyicisine ulaştığının doğrudan gözlemidir.

**Sunucuda tek satırlık doğrulama (her an çalışır):**

```bash
curl -s --socks5-hostname 10.66.66.1:40000 https://api.ipify.org   # Cloudflare IP'si
curl -s -o /dev/null -w "%{http_code}\n" --socks5-hostname 10.66.66.1:40000 https://profile.tarkov.com/   # 200 (blok değil)
```

---

## 4) Fonksiyonel doğrulama

1. Launcher'da **giriş yap** → giriş açılmalı; "Error on POST" / Cloudflare engeli **olmamalı**.
2. Oyunu başlat → sunucu listesi açılsın, maça giriş çalışsın.
3. Ping kartı: `önce → sonra (−ms) · via Almanya` görünür — oyun yolunun düğümden geçtiğinin ölçüsü.

---

## 5) Kabul ölçütleri (test "yeşil" sayılır)

- [ ] Bağlantı modu **WireGuard**; Game Boost'ta WARP uyarı bandı **görünmüyor**.
- [ ] `BsGLauncher.exe` satırı: ROUTE = WARP, canlı trafik varken LIVE = **`WARP · N`**.
- [ ] `EscapeFromTarkov.exe` satırı: ROUTE = VPN, oyun açıkken LIVE = **`PROXY · N`**.
- [ ] Access log'da `outbound/warp` satırları launcher/auth uç noktalarını taşıyor.
- [ ] Access log'da `outbound/proxy` satırları oyun sunucusu IP'lerini taşıyor.
- [ ] Launcher girişi başarılı (hata yok); oyun başlıyor.
- [ ] (Opsiyonel) `tcpdump -i wg0 port 40000` launcher trafiğini gösteriyor.

---

## 6) Sorun giderme

| Belirti | Olası neden | Çözüm |
|---|---|---|
| `BsGLauncher` LIVE = `PROXY` (WARP değil) + kırmızı uyarı bandı | Bağlantı **V2ray TCP** fallback'ine düştü (WireGuard değil) | WG düğümünü yeniden dene; UDP yolunu doğrula (`AoGPN.GpnProbeTool`). WARP yalnızca WireGuard modunda çalışır |
| LIVE sütunu `Default` gösteriyor | Eski build | Yeniden derle (monitor RouteText'e warp eklendi) |
| Access log'da `outbound/warp` satırı yok | Verbose log kapalı / log seviyesi `warn` | Ayarlar → Log level `debug` + Verbose logging AÇIK → bağlantıyı kapat/aç |
| Giriş hâlâ "Error on POST" | WARP egress çalışmıyor (sunucu tarafı) | Sunucuda §3 curl doğrulaması; wireproxy ayakta mı (`pgrep -f bin/wireproxy`), `10.66.66.1:40000` dinliyor mu (`ss -tln \| grep 4000`) |
| Access log'da `outbound/socks[warp]: connect tcp 10.66.66.1:40000: no route to host` (sunucuda wireproxy + listener sağlamken) | outbound → endpoint **detour dial'i** canlıda bozuk (yalıtılmış sing-box'la aynı config çalışıyor, uygulama ortamında her oturumda düşüyor); /24→/32 host prefix düzeltmesi bununla ilgisizdi | Yeniden derle: TUN/GPN modunda warp socks **detour'suz** üretilir — dial OS'ten yapılır, auto_route onu kendi TUN'undan geri alır, `GenRoutingGpn`'deki `ip_cidr 10.66.66.0/24 → proxy` kuralı (core-process protect'tan önce) endpoint'e yönlendirir. Doğrulama: diag'de `ROUTE warpSubnet: 10.66.66.0/24 → proxy` satırı + `WARP outbound added: ... OS dial → TUN döngüsü` |
| `GPN_BRIDGE yakalama döngüsü fault: Unable to load DLL 'WinDivert.dll'` | WinDivert.dll uygulama klasöründe yok (P/Invoke çalışma zamanı bağımlılığı — önceden hiçbir paketleme onu kopyalamıyordu) | Yeniden derle: build artık **repo'ya gömülü resmî WinDivert 2.2.2 dağıtımını** (`Libs\WinDivert\WinDivert-2.2.2-A.zip`, SHA-256 `63cb41…f15`) exe yanına kopyalar (`PromoteWinDivertToOutputRoot`; zip yoksa indirme fallback'i TLS 1.2 ile). Sürücü ilk `WinDivertOpen`'ta otomatik kurulur (yönetici gerekir; 5/577/1275/1753 kodları net mesaja çevrilir). Doğrulama: diag'de `WINDIVERT env state=Ready` satırı; sorun varsa dashboard'da sarı bant (`WinDivertHealthMonitor` — açılışta + köprü fault'unda durumu `AppEvents.WinDivertHealthChanged` ile yayınlar) |
| `BsGLauncher` hiç canlı bağlantı göstermiyor | Launcher kapalı veya auth trafiği farklı süreçte | Launcher'ı açık tut; `list_running_processes` ile süreç adını doğrula (küçük/büyük harf fark etmez) |
| Oyun LIVE = `WARP` görünüyor | Yanlışlıkla oyuna da WARP atanmış | Oyun satırını **VPN**'e çevir (preset bunu otomatik yapar) |
| Oyun açılırken **"Unable to check the client version"** | Oyunun kendi sürüm kontrolü `escapefromtarkov.com` API'sine gidiyor; VPN (Oracle) çıkışı launcher girişindeki aynı Cloudflare WAF engeline takılıyor | **`escapefromtarkov.com` → WARP** domain kuralı listenin başında olmalı (oyun process kuralından önce — Game Boost'ta satırın ↑/↓ oklarıyla domain kuralını oyunun üstüne taşı, sonra bağlantıyı kapat/aç). Acil sıçrama testi: oyun satırını geçici **WARP**'a çevir — oyun açılırsa tanı doğrudur; sonra domain kuralıyla VPN yoluna (düşük ping) dön |

---

## Ek: rota/live değerleri nereden geliyor (doğrulama için)

- **Boost tablosu LIVE sütunu:** `SplitTunnelViewModel.UpdateLiveStatus` → canlı bağlantı listesindeki uygulamanın baskın `RouteTag`'i; tag, kuralın outbound'una göre çözülür (`warp` → "WARP", `proxy` → "PROXY", `direct` → "DİREKT BAĞLANTI").
- **Access log:** `guiLogs\ao_singbox_<tarih>.log` — sing-box `log.level` verbose ile `info/debug` olur ve her outbound bağlantısı `outbound/<tag>: outbound connection to <hedef>` olarak yazılır.
- **Yakalama köprüsü çakışmaz:** GPN WireGuard modundaki WinDivert yakalama köprüsü **yalnızca UDP** paketlerini yakalar (`outbound and udp and (processId == ...)`); launcher'ın TCP auth trafiği sing-box kurallarına kalır ve WARP outbound'una gider. Oyunun UDP trafiği köprüden düğüme gider (Oracle çıkışı — ping için istenen yol), TCP trafiği `proxy` kuralıyla aynı düğüme gider.
