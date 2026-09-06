# Kısa çekirdek kurtarma penceresinde oturum sürekliliği — tasarım

Tarih: 2026-09-07
Durum: Tasarım onaylandı — **Faz 1 uygulandı** (2026-09-07, testler yeşil: 1153/1153)
Kapsam: GPN (Proxy capture) ve Global VPN (TUN) modlarında mihomo çekirdeği öldüğünde /
yeniden başlatıldığında oyun istemcisi TCP/UDP oturumlarının kesilmesini önleme stratejisi.

---

## 1. Problem ve temel kısıt

mihomo TUN akışı, oturum durumunu **çekirdek işleminin belleğinde** tutar:

- `stack: gvisor` (uygulama varsayılanı), `system` ve `mixed` — üçü de TCP'yi çekirdek
  işlemi içinde sonlandırır (gvisor = kullanıcı alanı yığın; system = işlem içi OS soketleri).
  İstemciye bakan seq/ack numaraları ve NAT eşlemeleri çekirdeğe aittir.
- Çekirdek öldüğü anda bu durum geri dönüşsüz kaybolur. mihomo, çalışan bağlantılarını
  dışa aktaracak bir mekanizma sunmaz; işlem öldükten sonra zaten okunamaz.

**Sonuç:** "Mevcut TCP oturumlarını yeni çekirdeğe kopyala" diye doğrudan bir taşıma
mümkün değildir. Strateji üç katmanlı olmalıdır:

- **L0 — Çökme olmasın:** çökme nedenlerini kapat, uygulama kaynaklı restart'ları ortadan kaldır.
- **L1 — Çökse de pencere görünmez olsun:** kurtarmayı TCP zamanlayıcıları tetiklenmeden bitir.
- **L2 — Oturum sahipliğini çekirdek dışına taşı:** istemciye bakan soketleri çekirdekten
  bağımsız bir süreç tutsun; çekirdek değişince yalnızca yukarı akış bacakları yeniden kurulsun.

## 2. Kod tabanında doğrulanan mimari gerçekler

| Bulgu | Yer | Önemi |
|---|---|---|
| `ClashConfigReload` (`PUT /configs?force=true`) tanımlı ama **hiçbir yerden çağrılmıyor** | `ServiceLib/Manager/ClashApiManager.cs:131` | Uygulama kaynaklı her config değişikliği (node değişimi, mod değişimi) süreci yeniden başlatıyor = oturumlar topluca ölüyor. **En büyük ve en ucuz kazanım.** |
| Çökme kurtarması üstel geri çekilme ile süreci yeniden başlatıyor (1s → 2s → 4s) | `ServiceLib/Manager/CoreManager.cs` (`RecoverMainCoreAsync`) | İlk deneme anında yapılabilir; pencere saniyelerden <500ms'e iner. |
| TUN: `stack: gvisor`, `auto-route`, sabit Wintun adaptör adı, MTU | `ServiceLib/Services/CoreConfig/Mihomo/GpnMihomoConfigService.cs:316` | Adaptör adı sabitse yeniden kurulum hızlıdır (L1-2 için temel hazır). |
| `profile: store-selected / store-fake-ip` config'te yok | aynı dosya | Restart sonrası fake-ip eşlemesi kaybolur; eklenmeli (L1-4). |
| Windows: arayüz kaybolunca mevcut TCP soketleri RST ile öldürülmez; retransmission zaman aşımına kadar sürer | OS davranışı | Pencere < ~1-2 sn ise istemci tarafı soket hayatta kalır; asıl kırılma NAT eşlemesinin kaybolmasıdır. |
| mihomo config reload mevcut bağlantıları korur (tünel katmanı yaşar; yeni kurallar yeni bağlantılara uygulanır) | mihomo davranışı (Faz 1'de Windows/TUN ile test edilecek) | L0-2'nin dayanağı. |

## 3. Katmanlı strateji

### L0 — Çökmenin kendisini önle

1. **Kök neden kapatma.** Eklenen `CORE_EXIT` diag çıktısıyla çökme nedenlerini sınıflandır
   (config hatası / Wintun hatası / rota çakışması / OOM / bilinmeyen) ve her sınıfa
   ayrı düzeltme uygula. Hedef: çekirdek çökme sıklığı ~0.
2. **Uygulama kaynaklı restart'ları API reload'a çevir.** `ClashConfigReload`'u node değişimi,
   mod değişimi ve config uygulaması yollarına bağla. Süreç hiç ölmez; mevcut bağlantılar
   yaşar. (TUN ayağında reload'un adaptörü/rotaları yeniden kurup kurmadığı Faz 1'de test edilir;
   gerekirse reload sonrası rota doğrulaması + `RoutingDriftHealthCheck` zaten devrede.)
3. **Ölümü önceden sez, kontrollü kapan.** API health yanıtı 2 sn boyunca yoksa sert çökme
   beklemek yerine API üzerinden kapanış + anında yeniden başlatma. Pencere "çökme + tespit +
   geri çekilme" yerine "tespit + ~200 ms" olur.
4. **Config dosyası atomik yazım + izleme.** Yarım yazılmış config çekirdeği öldürmesin.

### L1 — Çökme penceresini görünmez yap

Hedef: **kurtarma < 500 ms** (TCP retransmission zamanlayıcıları tetiklenmez), UDP'de
kesinti < 1-2 sn.

1. **İlk kurtarma denemesini anında yap.** `RecoverMainCoreAsync` üstel beklemesi yalnızca
   tekrarlarda işlesin (1s → 2s → 4s); ilk deneme gecikmesiz.
2. **Hızlı başlatma zinciri.** Geo dosyaları önceden doğrulanmış, config bellek önbelleğinde,
   süreç parametreleri hazır. Yeni çekirdek `Ready` olur olmaz rota + DNS kuralları senkron
   uygulanır; Wintun adaptör adı/IP aralığı/MTU sabit tutulur (aynı değerlerle yeniden kurulum
   en hızlı yoldur).
3. **Çıkış IP'si sabitlenir.** Kurtarma aynı node'a gider (mevcut davranış — doğrulanır).
   Sunucu tarafı UDP oturumları aynı IP'den gelmeye devam eder; TCP yeniden bağlantıları aynı
   sunucu IP'sine gider (DNS çalkantısı yok).
4. **fake-ip önbelleği.** `profile: { store-selected: true, store-fake-ip: true }` — restart
   sonrası oyun istemcisinin tuttuğu fake-ip'ler aynı gerçek IP'lere eşlenir; yeniden DNS
   çözümüne ve yeni IP'ye bağlanma adımına gerek kalmaz.
5. **UDP dayanıklılığı.** `udp-timeout` değerlendirmesi; `endpoint-independent-nat: true` ancak
   test edilip faydası kanıtlanırsa açılır (kaynak port sabitliği sağlamaz — o iş L2'dedir).
6. **TCP keepalive.** mihomo `keep-alive-interval` / `keep-alive-idle` ayarları L2 relay
   ayaklarıyla birlikte değerlendirilir.

> L1'in TCP garantisi: istemci tarafı soket OS'de yaşar, launcher "bağlantı kesildi" hatası
> göstermez; ancak sunucu tarafı yeni NAT eşlemesiyle **yeni bir bağlantı** görür. Oturumun
> sürmesi oyun protokolünün yeniden senkronizasyon toleransına bağlıdır. Gerçek TCP sürekliliği
> L2'dedir.

### L2 — Oturum sahipliğini çekirdek dışına taşı (gerçek "taşıma")

Temel fikir: istemciye bakan soketleri çekirdekten bağımsız bir **oturum taşıyıcı süreç**
tutar. Çekirdek değişince taşıyıcı yalnızca yukarı akış (upstream) bacaklarını yeniden kurar;
istemci bacağı hiç etkilenmez. Oturum durumu (seq/ack, NAT eşlemeleri) taşıyıcının belleğinde
olduğu için çekirdek değişimi oturumu taşıyamaz değil, **etkileyemez**.

**L2a — GPN modu (Proxy capture) için SOCKS/HTTP Session Relay**

- Yeni süreç `AoGPN.Relay` (ServiceLib'den supervise edilen küçük .NET süreci; çekirdekten
  bağımsız yaşar).
- Akış: oyun istemcisi → Relay (sabit local SOCKS/HTTP portu) → mihomo `mixed-port` → çıkış.
- Relay, istemci soketlerinin tam durumunu kendi belleğinde tutar; çekirdek öldüğünde istemci
  bacağı hiç kopmaz; yeni çekirdek `Ready` olur olmaz upstream bağlantıları yeniden açılır
  (aynı çıkış node'u).
- Uygulama: .NET `TcpListener` + SOCKS5 istemci ayağı; oturum tablosu
  `(istemci tuple) → (upstream tuple, kuyruk)`; her iki yönde sıra korunur.
- Etki: istemci tarafı %100 kesintisiz; sunucu tarafı "aynı IP'den yeni bağlantı" görür —
  oturum kimliği hesap/token tabanlı oyunlarda şeffaf, 4-tuple tabanlı oyunlarda protokolün
  yeniden senkronizasyonuna bağlı.

**L2b — Global VPN (TUN) modu için TUN köprü (tun2socks deseni)**

- Aynı relay, Wintun adaptörünü **kendisi** açar: istemci trafiği → Wintun (relay sahibi) →
  relay'in TCP/UDP işlemesi → mihomo SOCKS/mixed-port → çıkış.
- Çekirdek öldüğünde: adaptör, IP, rotalar ve istemci soketleri **yaşar**; yalnızca upstream
  bacakları yeniden kurulur (milisaniyeler).
- **UDP kazanımı büyüktür:** relay kendi NAT eşlemesini (istemci tuple → çıkış port) restart
  boyunca sakladığı için aynı çıkış portunu kullanmaya devam eder → sunucu gözünde UDP oturumu
  **hiç kesilmez** (FPS oyunları için tam süreklilik).
- Uygulama seçenekleri:
  - a. .NET içinde Wintun P/Invoke + kendi TCP yığını — .NET'te olgun yığın yok, **önerilmez**.
  - b. **Gömülü Go yardımcı (önerilen):** sing-box'ın `tun` + `socks` outbound'u (veya
    tun2socks) tek yardımcı binary; mihomo yalnızca "proxy/router" olarak kalır. TUN işleme
    kanıtlanmıştır (sing-tun), sürüm/imza yönetimi relay paketleme düzenine girer.
  - c. Uzun vadede mihomo'yu sing-box ile değiştirmek (tun + proxy tek süreç) — L2b'nin
    kazancını tek süreçte verir; ayrı bir migrasyon kararı.

> L2'de bile sunucu tarafı TCP "yeni bağlantı" görür (TCP oturumu sunucu + istemci OS + tüm
> ara NAT'ların ortak malıdır; tek taraflı taşınamaz). Ancak istemci tarafı hiçbir şey fark
> etmez ve UDP'de sunucu tarafı da süreklidir — ulaşılabilir en iyi sonuç budur.

### L3 — Doğrulama ve SLO

1. **Kurtarma süresi histogramı:** diag'e `RECOVERY windowMs=…` satırı; hedef p95 < 500 ms.
2. **Oturum hayatta kalma probu:** app, tünel üzerinden sürekli bir TCP + bir UDP test
   soketi tutar; her kurtarma olayında probun kopup kopmadığını raporlar →
   "görünmez kurtarma oranı" metriği.
3. **Oyun protokolü sınıflandırması:** hedef oyunlar için TCP/UDP + oturum kimliği tablosu;
   hangi katmanın (L1 mi L2a mı L2b mi) yeterli olduğu buna göre seçilir.
   → Ayrıntılı matris: [`docs/game-protocol-matrix.md`](game-protocol-matrix.md) (L3-3).

## 4. Uygulama yol haritası

| Faz | Kapsam | Çaba | Etki |
|---|---|---|---|
| 1 | L0-2 (`ClashConfigReload`'u node/mod değişimlerine bağla), L0-3 (canlılık sezimi + kontrollü kapanış), L0-4 (atomik config yazımı), L1-1 (ilk deneme gecikmesiz), L1-3/L1-4 (node sabitleme doğrulama + `store-fake-ip`), L3-2 (problar) | Küçük | **En büyük** — uygulama kaynaklı tüm oturum kopmaları biter; gerçek çökmelerde pencere <500ms olur |
| 2 | L2a SOCKS Session Relay (GPN modu) | Orta | GPN modunda istemci tarafı tam süreklilik |
| 3 | L2b TUN köprü (Go helper) | Büyük | Global modda istemci tam süreklilik + UDP sunucu tarafı süreklilik |
| 4 | sing-box tam migrasyon değerlendirmesi | Çok büyük | Tek süreç mimarisi; ayrı karar |

**Faz 1 uygulama notları (2026-09-07):**
- `ClashApiManager.ClashConfigReload` artık bağlantıları KAPATMIYOR ve HTTP 2xx
  doğruluyor; `CoreManager.LoadCore` içinde `CoreSoftReloadPolicy` kapısıyla
  soft-reload hızlı yolu devrede (mihomo→mihomo + aynı TUN durumu + süreç canlı +
  pre-SOCKS yok). Herhangi bir koşulda eski durdur/başlat yoluna düşülür.
- İlk kurtarma denemesi gecikmesiz (`CoreRestartPolicy.GetDelay(1) == 0`).
- `GpnMihomoConfigService` artık `profile: { store-selected, store-fake-ip }` üretiyor.
- `SessionSurvivalProbeService` (MainWindow'da başlar): çekirdek kurtarma/soft-reload
  pencerelerinde SOCKS5 üzerinden TCP + UDP bacaklarının hayatta kalmasını ölçer,
  `SURVIVAL tcp=… udp=… gapMs=…` diag satırı üretir; kullanıcı koparması (Stopped)
  değerlendirmeye girmez.

## 5. Riskler ve açık kararlar

- **Reload'un bağlantı koruması Faz 1'de kanıtlanmalı:** mihomo sürümüne ve TUN'a bağlı;
  reload sonrası rota/adaptör doğrulaması (`RoutingDriftHealthCheck`) devrede tutulur.
- **L2a'nın capture mimarisine oturması:** GPN modunda trafik sistem proxy/redir ile mi
  çekiliyor — relay'in o zincirin neresine girdiği netleştirilmeli.
- **L2b çift süreç yönetimi:** Go helper imza/sürüm güncelleme, Windows firewall izinleri,
  çekirdek + relay süpervizyon sıralaması.
- **`endpoint-independent-nat`:** faydası ölçülmeden açılmaz (performans maliyeti olabilir).
- **Hedef oyun listesi netleştirilmeli:** stratejinin hangi katmanının "yeterli" olduğu oyun
  protokolüne göre değişir; L3-3 tablosu bu kararı verir.