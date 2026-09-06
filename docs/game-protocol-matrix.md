# L3-3 — Hedef oyun protokol matrisi: hangi oyun hangi katmanla kesintisiz kalır

Tarih: 2026-09-07
Durum: Tasarım (saha doğrulaması bekliyor)
Üst doküman: `docs/session-continuity-design.md` (L3-3)
Hedef küme: `ServiceLib/Common/KnownAppCatalog.cs` (uygulamanın kuratlı oyun/launcher listesi)

---

## 1. Yöntem ve güvenilirlik

- Taşıma bilgileri (TCP/UDP) genel ağ bilgisi + kamu kaynaklarından derlendi;
  kesinlik işaretleri: ✔️ = yaygın/çoklu kaynak, ~ = tek kaynak/muhtemel,
  ⚠️ = oyun güncellemeleriyle değişebilir.
- **Saha doğrulaması zorunludur:** matris, uygulamanın kendi telemetrisiyle
  (GPN oturum logu / DashboardTrafficEngine per-flow sayacı) doğrulanmalı;
  aşağıda §6 plan var.
- "Kesintisiz" tanımı: oyun istemcisi/launcher "bağlantı kesildi" göstermez,
  oturum uygulama-katmanında devam eder. Sunucu tarafı her TCP'de yeni bağlantı
  görür (bkz. üst doküman §1) — bu tablo istemci gözünden kesintisizliği ve
  oturum devamının MÜMKÜN olduğu protokolleri sınıflandırır.

## 2. Katmanların garanti ettiği şey (özet)

| Katman | Garanti | Kapsadığı trafik yolu |
|---|---|---|
| **L0** (çökme önleme) | Çekirdek çökme sıklığı ~0; app kaynaklı değişiklikler restart'sız | Her şeyin ön koşulu |
| **L1** (hızlı kurtarma) | Pencere < 500 ms, çıkış IP sabit, fake-ip korunur → UDP 1-2 paket kaybıyla sürer; token tabanlı TCP servisleri (launcher auth) görünmez | Tüm yollar |
| **L2a** (SOCKS relay) | İstemci TCP soketleri çekirdekten bağımsız — proxy yolundaki uygulamalar %100 istemci kesintisizliği | Sistem proxy'sini onurlandıran uygulamalar (GPN proxy yolu) |
| **L2b** (TUN köprü) | İstemci soketleri yaşar + UDP NAT portu köprüde sabitlenir → sunucu tarafı UDP oturumu bile sürer | TUN yolu (Global VPN; GPN'de process-kurallı oyun trafiği) |

**Kritik katalog notu (`KnownAppCatalog.cs`):** "Games (and game platforms)
ignore the system proxy, so they are suggested to use the VPN (TUN) option."
Yani **oyunların çoğu TUN yolundan geçer** → asıl koruma katmanı **L2b + L1'dir**;
L2a yalnızca proxy yolunu kullanan servisler (launcher web içeriği, tarayıcı
tabanlı auth) ve ileride proxy destekli oyunlar için geçerlidir.

## 3. Oturum kimliği modelleri (sunucu gözünden)

| Model | Davranış | Çekirdek restart'ında |
|---|---|---|
| **T1 — Token/hesap oturumu** | Sunucu oturumu hesap kimliğine bağlı; yeni bağlantı kabul edilir, oturum devam eder | TCP soket ölse bile yeniden bağlanınca oturum sürer (çoğu MMO'nun "yeniden bağlan" mekanizması) |
| **T2 — IP+port eşlemesi (NAT)** | Sunucu oturumu kaynak IP:port'a bağlı | Port değişti → sunucu yeni oturum sanır; IP sabit kalırsa çoğu sunucu kısa sessizliğe + yeni porta toleranslıdır (UDP FPS) |
| **T3 — P2P eşleşme + NAT deliği** | Oturum = iki istemci arası delik | Delik kaybolur; köprü (L2b) deliği korursa oturum sürer |
| **T4 — Kısa HTTP(S)/RPC** | Her istek yeni TLS bağlantısı; oturum cookie/token | Doğal olarak dayanıklı — L1 yeterli |

## 4. Matris (KnownAppCatalog hedef kümesi)

### A. UDP ağırlıklı — token/hesap oturumu (T2+T1) → **L1 yeterli; L2b ile %100**

Sunucu oturumu hesaba bağlı + periyodik durum senkronu; çıkış IP sabit kalırsa
yeni port 1-2 paket kaybıyla kabul edilir. L1 tek başına büyük çoğunluğu
kurtarır; L2b (port sabitleme) kopmayı tamamen sıfırlar.

| Oyun | Taşıma | Oturum modeli | Kopma algısı (L1'siz) | Yeterli katman |
|---|---|---|---|---|
| Valorant | UDP oynanış (5000-5500) + TCP sosyal ✔️ | T2+T1 | 1-5 sn freeze, nadir "reconnect" | **L1** → L2b tam |
| CS2 / CS:GO | UDP (Steam Datagram Relay) ✔️ | T2+T1 | 1-2 sn spike | **L1** → L2b tam |
| Fortnite | UDP (güvenilir UDP katmanı) ✔️ | T2+T1 | kısa freeze | **L1** → L2b tam |
| PUBG | UDP ✔️ | T2+T1 | spike + "network lag" | **L1** → L2b tam |
| Apex Legends | UDP ✔️ | T2+T1 | freeze + sunucu "reconnect" | **L1** → L2b tam |
| COD / Warzone | UDP ✔️ | T2+T1 | freeze | **L1** → L2b tam |
| Overwatch | UDP ✔️ | T2+T1 | "reconnecting" ekranı | **L1** → L2b tam |
| Rainbow Six Siege | UDP ✔️ | T2+T1 | kısa dondurma | **L1** → L2b tam |
| Destiny 2 | UDP ✔️ | T2+T1 | "contacting servers" | **L1** → L2b tam |
| Halo Infinite | UDP ~ | T2+T1 | freeze | **L1** → L2b tam |
| Rocket League | UDP ✔️ | T2+T1 | "reconnecting" | **L1** → L2b tam |
| Fall Guys | UDP ✔️ | T2+T1 | freeze | **L1** → L2b tam |

### B. UDP ağırlıklı — P2P/eşleşme (T3) → **L2b gerekli**

Oturum = NAT deliği; restart'ta delik kaybolur, eşleşme kopar. L1 tek başına
yetersiz; L2b köprüsü deliği ve kaynak portu korur.

| Oyun | Taşıma | Oturum modeli | Kopma algısı | Yeterli katman |
|---|---|---|---|---|
| GTA Online | UDP P2P + TCP eşleşme ✔️ | T3 | "session lost / kicked to lobby" | **L2b** |
| Elden Ring (co-op) | UDP P2P ✔️ | T3 | summon bağlantısı kopar | **L2b** |
| Rust | UDP oynanış + TCP liste ✔️ | T2+T3 | sunucudan düşer | **L2b** |
| DayZ | UDP ✔️ | T2+T3 | sunucudan düşer | **L2b** |
| ARK | UDP ✔️ | T2+T3 | sunucudan düşer | **L2b** |
| Valheim | UDP (P2P/eşleşme) ✔️ | T3 | eşleşme kopar | **L2b** |
| Palworld | UDP (P2P) ✔️ | T3 | eşleşme kopar | **L2b** |
| Among Us | UDP P2P ✔️ | T3 | lobiden düşer | **L2b** |
| Black Desert | UDP (9991-9993) ✔️ | T2+T1 | "connection to server lost" | **L1** → L2b tam |
| Dead by Daylight | UDP ~ | T2+T3 | eşleşme kopar | **L2b** |
| Sea of Thieves | UDP ~ | T2+T3 | oturumdan düşer | **L2b** |

### C. TCP tabanlı oynanış (T1) → **L2a/L2b istemci kesintisizliği; sunucu oturum devamı**

TCP soketi çekirdek restart'ında ölür; launcher/oyun "bağlantı kesildi" gösterir.
L2a (proxy yolu) veya L2b (TUN yolu) istemci soketini yaşatır; sunucu tarafı
"yeni bağlantı + aynı hesap oturumu" görür — T1 modelindeki oyunlar bunu
yeniden bağlanma mekanizmalarıyla tolere eder. **L1 tek başına YETMEZ** (TCP
soket ölür; yalnızca <500 ms pencerede OS soketi yaşar ama NAT eşlemesi kaybolur).

| Oyun | Taşıma | Oturum modeli | Kopma algısı (L1'siz) | Yeterli katman |
|---|---|---|---|---|
| League of Legends | TCP oynanış + TCP sosyal ✔️ | T1 | "reconnecting" ekranı | **L2a/L2b** (T1 devam eder) |
| World of Warcraft | TCP ✔️ | T1 | "world server down / disconnect" | **L2a/L2b** (oturum devam eder) |
| WoW Classic | TCP ✔️ | T1 | aynı | **L2a/L2b** |
| Diablo IV | TCP ~ | T1 | "disconnected" | **L2a/L2b** |
| Path of Exile | TCP ✔️ | T1 | "disconnected from server" | **L2a/L2b** |
| Lost Ark | TCP ✔️ | T1 | "server connection lost" | **L2a/L2b** |
| Guild Wars 2 | TCP ✔️ | T1 | "connection to server lost" | **L2a/L2b** |
| FFXIV (Final Fantasy) | TCP ✔️ | T1 | "connection with server lost (90002)" | **L2a/L2b** |
| EVE Online | TCP ✔️ | T1 | "connection lost" | **L2a/L2b** |
| Minecraft (Java) | TCP ✔️ | T1 | "connection lost" (yeniden giriş gerekir) | **L2a/L2b** (kısa pencere: oturum devam eder) |
| Hearthstone | TCP ✔️ | T1 | "reconnecting" | **L2a/L2b** |
| Genshin Impact | TCP ağırlıklı ~ | T1 | "connection timeout" | **L2a/L2b** |
| Honkai/Star Rail | TCP ağırlıklı ~ | T1 | aynı | **L2a/L2b** |

### D. Hibrit — TCP + UDP birlikte (T1+T2) → **L1 + L2a/L2b**

| Oyun | Taşıma | Oturum modeli | Yeterli katman | Not |
|---|---|---|---|---|
| Escape from Tarkov | TCP + UDP karışım ✔️ | T1 (raid hesaba bağlı) + T2 (UDP veri) | **L2b** (TUN yolu; hem TCP hem UDP bacaklar) + L1 | UDP ayakları L1'le sürer; TCP ayakları (eşleşme/telemetri) L2b'yle yaşar. BSG launcher auth ayrıca **warp egress** ister (Cloudflare WAF — kodda zaten kurallı) |
| RDR2 Online | TCP ağırlıklı ~ | T1+T3 | **L2b** | eşleşme oturumu |
| Star Citizen | UDP ✔️ | T2+T1 | **L1** → L2b tam | |

### E. Launcher / platform servisleri (T4) → **L1 yeterli**

Kısa TLS/RPC bağlantıları + token oturumu: çekirdek restart'ında istek yeniden
denenir, kullanıcı fark etmez.

| Platform | Taşıma | Oturum modeli | Yeterli katman | Not |
|---|---|---|---|---|
| BSG Launcher | HTTPS ✔️ | T4 | **L1** | warp egress (WAF) — zaten kuralda |
| Steam / SteamService | HTTPS + Steam protokol (TCP) + indirme ✔️ | T4 | **L1** | indirmeler HTTP range — idempotent, kesintisiz görünür |
| Epic Games Launcher | HTTPS ✔️ | T4 | **L1** | |
| Battle.net | HTTPS/TCP ✔️ | T4 | **L1** | |
| Riot Client | TCP (5222/5223 XMPP) + HTTPS ✔️ | T4 | **L1** | sosyal sohbet kısa bağlantılar |
| Ubisoft Connect | HTTPS ✔️ | T4 | **L1** | |
| GOG Galaxy / Xbox / Origin | HTTPS ✔️ | T4 | **L1** | |

## 5. Kategori sonuçları (yönetici özeti)

1. **UDP FPS/BR (~12 oyun):** Faz 1'deki L1 (hızlı kurtarma + sabit çıkış IP +
   fake-ip) büyük çoğunluğu görünmez kılar. Kalan %100 garantisi için L2b'nin
   UDP port sabitlemesi gerekir (Faz 3).
2. **P2P/UDP co-op (~11 oyun):** L1 yetersiz — NAT deliği kaybı; **L2b zorunlu**
   (köprü deliği korur). L2a bu grupta işe yaramaz (proxy yolu yok).
3. **TCP MMO (~12 oyun):** L1 yetersiz (soket ölür); **L2a veya L2b istemci
   tarafını %100 korur** ve T1 oturum devamı sayesinde oyunların çoğu sunucu
   tarafında da devam eder. WoW/FFXIV/PoE gibi "yeniden bağlan" mekanizmalı
   oyunlar en yüksek başarıyı gösterir.
4. **Launcher'lar:** zaten L1'le biter (T4).
5. **Öncelik sırası:** L2b (TUN köprü) hem B hem C grubunu çözdüğü için L2a'dan
   önce değerlendirilmelidir; L2a yalnızca proxy-yolu uygulamaları içindir.

## 6. Saha doğrulama planı (matrisi gerçek veriyle onaylama)

1. **Per-flow telemetri:** GPN oturum logu / DashboardTrafficEngine per-oyun
   akış sayacına `TCP/UDP + hedef IP:port` satırı eklenir; her kurtarma
   olayında (SURVIVAL probe ile eşleştirilerek) hangi oyunun hangi protokolde
   kopup kopmadığı ölçülür.
2. **Test senaryosu (oyun başına):** Bağlan → maç/raid/eşleşme başlat → çekirdek
   kurtarmasını tetikle (soft reload ve sert çökme) → oyunun algısı kaydedilir
   (fark etmedi / freeze / "reconnecting" / oturumdan düştü).
3. **Matris güncellemesi:** ✔️/~/⚠️ işaretleri saha sonucuna göre "doğrulandı /
   çelişkili" olarak güncellenir; her oyun için yeterli katman onaylanır.
4. **Anti-cheat notu:** BattlEye/Vanguard/EAC akışları UDP'dir ve taşıma
   sınıflandırmasına girer; ancak çekirdek restart'ı bazı anti-cheat
   istemcilerinde "bağlantı kesildi" algısına yol açabilir — test matrisine
   ayrı sütun olarak işlenir (EscapeFromTarkov_BE.exe, TslGame_BE.exe).