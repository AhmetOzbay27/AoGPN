# L2a — SOCKS Session Relay ayrıntılı teknik tasarımı

Tarih: 2026-09-07
Durum: Tasarım (onay bekliyor)
Üst doküman: `docs/session-continuity-design.md` (Faz 2 = L2a)
Kapsam: GPN (Proxy capture) modunda, çekirdek (mihomo) kurtarma/soft-reload
pencerelerinde oyun istemcisi TCP oturumlarının kesilmesini önleyen oturum
taşıyıcı katmanın ayrıntılı mimarisi.

---

## 1. Amaç ve dürüst sınırlar

**Amaç:** İstemciye bakan TCP soketlerini çekirdekten bağımsız bir süreçte tutmak.
Çekirdek öldüğünde/restart olduğunda istemci bacağı hiç kopmaz; yalnızca
yukarı akış (upstream) bacakları yeni çekirdeğe yeniden kurulur.

**Dürüst sınırlar (tasarımın dayandığı gerçekler):**

1. **Sunucu tarafı TCP oturumu her durumda "yeni bağlantı" görür.** TCP oturum
   durumu (seq/ack) sunucu + istemci OS + tüm ara NAT'ların ortak malıdır; tek
   taraflı "taşıma" mümkün değildir. Relay, istemci tarafının hiçbir şey fark
   etmemesini sağlar; sunucu aynı çıkış IP'sinden (node sabit) yeni bir bağlantı
   görür. Oturum kimliği hesap/token tabanlı oyunlarda bu şeffaftır; 4-tuple
   tabanlı oyunlarda oyun protokolünün yeniden senkronizasyonuna bağlıdır.
2. **In-flight veri kaybı kaçınılmazdır.** Çekirdek restart penceresinde
   istemciden gelip sunucuya ulaşmamış byte'lar kaybolur (aşağıda §5.4 replay
   politikası ile sınırlanır). Relay'in görevi bunu **görünmez** kılmaktır:
   oyun/launcher "bağlantı kesildi" algılamaz; protokol kendi yeniden
   senkronizasyonunu yaparsa oturum sürer.
3. **Bu katman GPN (proxy capture) modu içindir.** TUN/Global modu L2b'nin
   kapsamıdır (Wintun köprüsü — ayrı tasarım).

## 2. Konum ve topoloji

Kod tabanı doğrulaması: mihomo `mixed-port` = `AppManager.Instance.GetLocalPort(EInboundProtocol.socks)`
(`ServiceLib/Handler/CoreConfigHandler.cs:115`). Sistem proxy'si, sağlık yoklamaları
(`CoreHealthProbe.WaitForSocks5Async`), trafik motoru, oturum logu — hepsi bu
portu kullanır. Relay bu portun **dış** tarafını devralır:

```
[Oyun istemcisi] --SOCKS5/HTTP--> 127.0.0.1:10808  (DIŞ port — relay dinler; sistem proxy ayarı DEĞİŞMEZ)
        │
        ▼
[ AoGPN.Relay süreci ]  ──SOCKS5 CONNECT (no-auth)──▶ 127.0.0.1:10809  (İÇ port — mihomo mixed-port)
        │                                                    │
   istemci bacakları                              upstream bacakları ──WG──▶ oyun sunucusu
   (çekirdekten bağımsız,                          (çekirdeğe ait — restart'ta
    relay'e ait)                                   yeniden kurulur)
```

- **DIŞ port = bugünkü socks portu.** Sistem proxy ayarı, uygulama ayarları ve
  istemci tarafı değişmez. Relay bu portu dinler ve mihomo'nun portuna köprüler.
- **İÇ port = mihomo `mixed-port`** (yeni değer; örn. socks+1). Config üreticiye
  (`CoreConfigHandler` → `GpnMihomoConfigService.GenerateYaml`) tek değişiklik:
  `MixedPort = socks + 1` gibi deterministik iç port.
- **HTTP istekleri:** relay dış tarafta mixed protokol dinler (SOCKS5 + HTTP
  CONNECT). HTTP `CONNECT host:port` → iç tarafta SOCKS5 `CONNECT`'e çevrilir;
  düz HTTP (proxy GET) istekleri de aynı yola normalize edilir.
- **UDP:** dış tarafta SOCKS5 UDP ASSOCIATE alınırsa iç tarafta da ASSOCIATE
  açılıp çerçeveler köprülenir (Faz 2.2 — §7).

## 3. Süreç mimarisi

**Yeni exe: `AoGPN.Relay`** (konsol; ServiceLib'den ayrı, çekirdekten bağımsız).

| Özellik | Karar | Gerekçe |
|---|---|---|
| Süreç | Ayrı exe (ServiceLib'i referans almaz; sadece soket katmanı) | Uygulama UI/ServiceLib çökmesi relay'i etkilemesin; relay çökmesi çekirdeği etkilemesin (asimetri bilinçli: çekirdek restart'ları daha sık ve kritik) |
| Süpervizyon | Ana uygulama (CoreManager deseninde `WindowsJobService` + `ProcessService`) | CoreManager'ın süreç yönetim deseni hazır; job nesnesiyle ölümde temizlik |
| Başlatma sırası | Relay ÖNCE, mihomo SONRA | Dış port relay'e; iç port mihomo'ya; mihomo hazır değilken relay session'ları `Reconnecting` bekler |
| Kapanış sırası | mihomo önce, relay sonra | İstemci bağlantıları düzgün RST/FIN alır |
| Yapılandırma | Komut satırı: `--listen 127.0.0.1:10808 --upstream 127.0.0.1:10809 --epoch-port <ctl>` | Config dosyası bağımlılığı yok; ana uygulama her başlatmada parametreleri verir |
| Denetim kanalı | İsimli pipe (Windows) veya localhost TCP control portu | Relay'e "çekirdek epoch N+1" bildirimi + istatistik okuma (`GET /stats`) |

**Relay yalnızca loopback'ta dinler** (127.0.0.1) — mihomo bind-address ile aynı
güvenlik modeli. Auth yok (mevcut mixed-port gibi).

## 4. Oturum tablosu

```csharp
sealed class SessionEntry
{
    long Id;                          // kabul sırası (tablo anahtarı)
    DateTimeOffset CreatedAt;
    string TargetHost;                // DNS adı OLARAK saklanır (restart'ta yeniden çözülür)
    int TargetPort;

    // İstemci bacağı — relay'e ait, çekirdekten bağımsız.
    TcpClient Client;                 // dış taraftan kabul edilen
    NetworkStream ClientStream;

    // Yukarı akış bacağı — çekirdeğe ait; restart'ta null.
    TcpClient? Upstream;
    NetworkStream? UpstreamStream;

    // Kuyruklar (yalnızca Reconnecting/Establishing sırasında dolar — §5).
    Queue<byte[]>? PendingToUpstream;   // istemci→upstream, teslim edilememiş
    // Upstream→Client yönünde KUYRUK YOKTUR (gerekçe §5.3).

    SessionPhase Phase;               // EstablishingUpstream → Active → Reconnecting → Closed
    int ReconnectAttempts;
    DateTimeOffset LastActivityAt;
    long BytesIn, BytesOut;
}
```

- **Anahtar istemci bacağıdır**, upstream tuple değil — restart'ta upstream tuple
  değişir, tablo yaşar.
- `Dictionary<long, SessionEntry>` + ayrı `HashSet` (dışarıdan yeni kabul, epoch
  taraması için).
- Kapasite sınırı: maksimum eşzamanlı session (varsayılan 1024; aşılınca en eski
  idle session düşürülür — oyun senaryosunda session sayısı düşüktür).
- Zaman aşımı: `IdleTimeout` 60 sn (tamamen sessiz bağlantı); `HandshakeTimeout`
  10 sn; `ReconnectBudget` §6.

**State makinesi:**

```
Accept(CONNECT)
   │  hedefi kaydet (host:port), istemci bacağını kur
   ▼
EstablishingUpstream ──(iç SOCKS5 CONNECT başarılı)──▶ Active
   │                                                      │
   │ (upstream öldü: okuma EOF/RST, ya da epoch değişti)  │
   ▼                                                      ▼
Reconnecting ──(epoch değişti / yeni çekirdek Ready)──▶ EstablishingUpstream
   │
   (deneme bütçesi doldu) → istemciye RST + kapat → Closed → hata kartına düş
```

## 5. Tamponlama ve akış kontrolü

### 5.1 Normal akış (Active) — sıfır tampon

Veri kuyruğa alınmaz; iki yönlü doğrudan kopya (`CopyToAsync` deseni). Tamponlar
yalnızca `Reconnecting`/`EstablishingUpstream` sırasında dolar. Böylece normal
çalışmada ek gecikme ~0 ve bellek maliyeti ~0.

### 5.2 İstemci→Upstream kuyruğu (bounded)

- Kapasite: session başına varsayılan **256 KB** (config'le ayarlanır).
- Doluş kuralı: kuyruk limitine ulaşınca istemci akışı DURDURULUR (istemi
  okumayı bekle) — TCP doğal backpressure istemciye yansır; istemci OS'si kendi
  tamponlarını doldurur ve uygulama yazmaya devam ederse uygulama-katmanı
  blokajı oluşur. Oyun istemcisi "yazamıyorum" görür ama "bağlantı koptu"
  GÖRMEZ (socket yaşıyor).
- 256 KB seçimi: restart penceresi hedefi < 2 sn; oyun telemetrisi saniyede
  ~KBs (FPS/MMO durum senkronu) — 256 KB dakikalarca dayanır. Kayıpsızlık
  isteyen yüksek bantlı akışlar (indirme) için kuyruk dolunca eski veri
  DÜŞÜRÜLMEZ; akış bekletilir (kayıpsız ama gecikmeli — doğru davranış).

### 5.3 Upstream→İstemci yönü — kuyruk YOKTUR

Restart penceresinde upstream'ten veri gelmez (upstream ölü). Ölüm anında
upstream'ten okunup istemciye teslim edilememiş veri en fazla bir TCP
pencere büyüklüğündedir (64 KB) ve zaten istemciye YAZILMIŞTIR (relay doğrudan
köprülediği için arada tampon yoktur). Kural: **upstream ölümünde o yöndeki
okuma döngüsü sessizce biter; istemciye hiçbir şey gönderilmez** (RST/FIN
yok — socket açık kalır). Yeni upstream kurulunca akış sunucudan taze başlar.

### 5.4 Yeniden bağlantıda veri politikası (replay)

Durum: istemci, çekirdek ölmeden önce veri gönderdi; relay veriyi aldı ve OS
seviyesinde ACK'ladı (relay OS soketi kullandığı için ACK'ları kontrol edemez);
veri upstream'e ulaşamadı. İstemci retransmit ETMEZ (zaten ACK'landı) → veri
yok olur. Seçenekler:

| Yaklaşım | Sonuç |
|---|---|
| **A. Replay (önerilen):** `PendingToUpstream` kuyruğundaki veri, yeni upstream kurulunca baştan yazılır | Kayıpsız görünüm; DUPLICATE riski (veri upstream'e ulaşıp ACK relay'e gelmeden çekirdek öldüyse aynı byte iki kez gider). Sunucu TCP'si yeni bağlantıda bunu normal yeni veri gibi görür; oyun katmanı dup'ı kendi idempotansıyla çözer |
| B. Hiçbir şey yeniden gönderilmez | In-flight veri sessizce kaybolur; oyun state senkronu boşluğu tolere ederse yeterli, etmezse oturum kırılır |
| C. Relay kendi TCP yığınını kurar (ACK'ları erteleyerek tam TCP taşıma) | Teorik olarak mükemmel; pratikte .NET'te olgun yığın yok + mihomo tarafı seq'lerle uyum gerekir → reddedildi (L2b Go helper alternatifi bu sınıfa girer) |

**Karar: A (replay), sınırlı.** Yalnızca `PendingToUpstream` kuyruğunda DURAN
(teslim edilmemiş) veri yeniden gönderilir; upstream'e yazılmış ama ACK'ı
alınmamış veri relay'de olmadığı için zaten yeniden gönderilemez. Dup riski
kabul edilir ve diag'e işlenir: `RELAY replay session=… bytes=…`.

### 5.5 Graceful drain (bilinçli restart'larda)

Çekirdek restart'ı BİLİNÇLİ olduğunda (soft reload, node switch — yani relay
haberdar olduğunda, `--notify drain`):

1. Relay, istemci→upstream yönündeki okumayı DURDURUR (yeni veri kabul etmez),
2. Kuyruktaki veriyi upstream'e boşaltmayı dener (timeout 2 sn),
3. Upstream'i düzgün kapatır (FIN) — sunucu "graceful close" görür,
4. Yeni çekirdek Ready → §6 yeniden bağlantı (replay gereksiz: kuyruk boş).

Sert çökmede bu adımlar atlanır (upstream zaten ölü) — doğrudan §6'ya geçilir.

## 6. Yeniden bağlantı protokolü (epoch modeli)

```
Epoch N (çekirdek canlı) ──çekirdek öldü/reload──▶ Epoch N+1
   ▲                                                     │
   └───── yeni çekirdek Ready ──▶ ReconnectScheduler ─────┘
```

- **Epoch kaynağı:** `CoreManager.Health[Main]` (CoreHealthChanged) — relay'e
  denetim kanalından bildirilir. Alternatif: relay kendi upstream soketlerindeki
  EOF/RST'i epoch işareti sayar (çekirdeksiz bağımsızlık). **İkisi birlikte:**
  health sinyali birincil, soket ölümü yedek.
- **Scheduler davranışı** (tüm `Reconnecting` session'lar için):
  1. Yeni epoch başladı → her session `EstablishingUpstream`: iç SOCKS5 CONNECT
     (`TargetHost:TargetPort` — DNS adı olarak; mihomo kendi DNS'iyle çözer).
  2. Başarı → kuyruğu boşalt (replay, §5.4) → `Active`.
  3. Başarısız → `ReconnectAttempts++`; bekleme 1s → 2s → 4s (üst sınır 4s,
     toplam bütçe 3 deneme / 15 sn).
  4. Bütçe doldu → istemciye RST (dürüst kapanma), `Closed`, hata kartı
     (`ConnectionFailureLedger` deseni) + `RELAY session failed` diag.
- **Başarısızlık yarıçapı:** mihomo Ready olsa bile iç port açılmamışsa
  (başlatma yarıda) scheduler 500 ms aralıkla iç portu yoklar; Ready sinyali
  yanlış pozitifse socket dial hataları bütçeyi doldurur.
- **İstemci tarafı:** yeniden bağlantı süresince istemci bacağına TEK BYTE
  gitmez; OS ACK'ları devam eder (relay OS soketi); istemci uygulama-katmanı
  timeout'una kendi karar verir.

**Hedef gruplama:** aynı `TargetHost:TargetPort`'a giden session'lar tek
`ReconnectBatch` içinde sıralı dial edilir (port yarışını azaltır); her session
kendi CONNECT'ini alır (mihomo tarafında ayrı bağlantılar — oyun sunucuları
çoklu bağlantıya toleranslıdır).

## 7. mihomo mixed-port ile etkileşim

1. **Port sahipliği:** dış port relay'in; iç port mihomo'nun. `CoreConfigHandler`
   mihomo `MixedPort`'unu `socks + 1` olarak üretir; relay `--listen socks`
   alır. Sistem proxy ayarı (`SysProxyHandler`) `socks` portunda kalır — istemci
   tarafında sıfır değişiklik.
2. **Sağlık yoklaması ayrımı (kritik):** `CoreManager` hazırlık yoklaması
   (`WaitForSocks5Async`) DİŞ portta çalışırsa relay'i ölçer, mihomo'yu değil.
   Tasarım:
   - Relay başlatıldığında iç portu yoklar ve **ancak iç port cevap verirse**
     dış portta dinlemeye başlar (veya "upstream yok" modunda kabul edip
     session'ları beklemede tutar — tercih edilen, bkz. başlatma sırası).
   - CoreManager hazırlık yoklaması İÇ portta (mihomo gerçek sağlığı) + relay
     süreç canlılığı ayrı izlenir: `CoreHealthProbe.IsProcessReady(relay)` +
     iç port SOCKS5.
   - `GetClashProxiesAsync`/soft-switch gibi API çağrıları doğrudan
     `external-controller`'a gider (relay'den etkilenmez).
3. **Soft reload etkileşimi:** mihomo aynı süreçte reload olduğunda iç port
   açık kalır → relay HİÇBİR aksiyon almaz; `SURVIVAL relay=survived` (probe
   zaten bunu ölçer). Sert çökmede epoch artar, §6 devreye girer.
4. **Relay çökerse:** dış port düşer → istemci bağlantıları kopar (istemci
   tarafı etkilenir ama çekirdek/sunucu tarafı etkilenmez). Supervisor relay'i
   yeniden başlatır; yeni relay dış portu geri alır. Bu asimetri bilinçlidir:
   çekirdek restart'ları (sık/kritik) tam korumalıdır; relay restart'ı (nadir)
   istemciye görünür ama tüneli bozmaz.
5. **UDP (Faz 2.2):** dış SOCKS5 UDP ASSOCIATE → iç ASSOCIATE köprüsü. Oturum
   tablosu: `(istemci UDP tuple) → (iç ASSOCIATE relay adresi, hedef)`.
   Çekirdek restart'ta iç ASSOCIATE ölür; dış istemciye datagramlar DÜŞER
   (engellenmez); yeni ASSOCIATE kurulunca akış sürer — çıkış IP aynı olduğu
   için sunucu tarafı UDP oturumu 1-2 paket kaybıyla yaşar. UDP'de replay
   YOKTUR (connectionless — anlamsız). `udp-timeout` değerleri mihomo
   tarafında korunur.

## 8. Performans

- Normal akışta sıfır kopya köprü (doğrudan `CopyToAsync`); kuyruklar boş.
- Ek gecikme: loopback üzerinden bir SOCKS5 hop — mikrosaniyeler. GPN zaten
  proxy üzerinden gidiyordu (istemci → mihomo); yeni yol istemci → relay →
  mihomo: tek lokal hop eklenir.
- Bellek: session başına 2 × 64 KB `ArrayPool` tampon (yalnızca köprü sırasında
  ayrılır) + `PendingToUpstream` (yalnızca Reconnecting'de dolar).
- `nagle` kapalı (`NoDelay`), `UseNagleAlgorithm=false` — oyun akışları düşük
  gecikme ister.

## 9. Test stratejisi

Mevcut `SessionSurvivalProbeServiceTests`'teki `FakeMihomoCore` deseni
genişletilir (fake SOCKS5 sunucu + HTTP-204 yankısı + DNS yankısı hazır):

1. **Soft reload → survived:** relay çalışırken fake çekirdek canlı kalır;
   istemci simülasyonu bağlı kalır; veri bütünlüğü doğrulanır.
2. **Sert çökme → reconnect + replay:** fake çekirdek çöker/yeniden başlar;
   istemci yazmaya devam eder; yeni upstream kurulunca `PendingToUpstream`
   verisinin sunucu yankısında eksiksiz göründüğü doğrulanır (kayıp oranı
   ölçülür: hedef %0 in-flight hariç).
3. **Backpressure:** kuyruk limiti düşürülür; istemci yazmaya devam eder;
   istemci okumasının durduğu ve hiçbir byte'ın düşmediği doğrulanır.
4. **Bütçe tükenmesi:** fake çekirdek hiç gelmez; session'lar RST ile kapanır;
   hata kartı + `RELAY session failed` diag doğrulanır.
5. **Yük:** 100+ eşzamanlı session, 10 MB akış — kararlılık + bellek sınırı.
6. **HTTP CONNECT yolu:** dış taraftan HTTP proxy isteği → SOCKS5'e çeviri.

## 10. Uygulama yolu

| Adım | Kapsam | Çıktı |
|---|---|---|
| P2.0 | `AoGPN.Relay` iskeleti + supervisor (CoreManager deseni) + config (iç/dış port) + TCP CONNECT köprüsü (Active akış, sıfır tampon) + epoch izleme | Tünel çalışır; çekirdek restart'ında istemci bacakları yaşar, replay'siz |
| P2.1 | Reconnecting durumu + `PendingToUpstream` (256 KB) + replay + backpressure + graceful drain + bütçe/zaman aşımları | Tam L2a garantisi |
| P2.2 | UDP ASSOCIATE köprüsü + hedef gruplama + istatistikler (`RELAY` diag + `SURVIVAL` entegrasyonu) | UDP oyunları da korunur |
| P2.3 | Test matrisi (yukarıda §9) + oyun protokolü doğrulaması (L3-3 tablosu) | Saha onayı |

## 11. Açık kararlar ve riskler

- **Replay dup riski:** kabul edildi (§5.4); saha testinde oyun protokollerinin
  dup'a toleransı doğrulanacak. Toleranssız protokoller için seçenek B'ye
  config anahtarı: `relay.replay = on|off` (varsayılan on).
- **İç port çakışması:** `socks+1` çakışırsa (başka uygulama) supervisor
  yukarı doğru tarar (socks+1..+10).
- **Windows firewall:** relay loopback dinler — genellikle izin gerekmez; exe
  imzalı dağıtılır.
- **Health yoklaması kayması:** CoreManager hazırlık kontrolünün iç porta
  taşınması zorunlu (§7.2) — atlanırsa "relay açık = çekirdek hazır" yanlış
  pozitifi oluşur.
- **Kapsam dışı:** TUN/Global modu (L2b), TLS sonlandırma, kimlik doğrulama,
  çoklu relay ölçekleme.