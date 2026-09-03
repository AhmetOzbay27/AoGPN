# Xray Windows TUN Desteği — Uygulanabilirlik Raporu

**Tarih:** 26 Ağustos 2026
**Kapsam:** TUN modunda VLESS+REALITY düğümlerinde sing-box yerine Xray çekirdeğini kullanabilmek için Xray'in Windows TUN yeteneğinin araştırılması ve uygulama kapsamının belirlenmesi.
**İncelenen sürümler:** Xray-core **v26.7.28** (gömülü sürüm), sing-box 1.13.19, AoGPN mevcut kodu.

---

## 1. Yönetici Özeti

**Xray'in Windows TUN desteği mevcuttur, olgundur ve üretimde kanıtlanmıştır.** Xray-core 26.7.28 `proxy/tun` modülü wintun adaptörünü, gVisor IP yığınını, otomatik rota/loop önlemesini ve **Windows'ta çalışan süreç bazlı (per-app) yönlendirmeyi** destekler. Dahası, bu rapordaki en güçlü kanıt: **v2rayN'in bu makinede çalıştırdığı `xray_tun` adaptörü bizzat Xray'in TUN implementasyonudur** (açıklama: "Xray Tunnel") — yani aynı çekirdek aynı makinede şu an üretimde TUN çalıştırıyor.

**Uygulanabilirlik: Mümkün, ancak sıfırdan değil — kapsamı orta-büyük.** Xray, sing-box'ın uygulamada kullandığı TUN özelliklerinin çoğunu karşılar (per-app kuralları, DNS, QUIC bloğu). Asıl iş, uygulamanın **sing-box'a özel TUN config üreticilerinin Xray karşılıklarını yazmak** ve `CoreConfigContextBuilder`'daki zorlama kuralını kaldırmaktır. Teknik engel yok; tek gerçek fark, uygulamanın Windows'ta bilinçli olarak seçtiği WFP/system stack'inin Xray'de bulunmaması (Xray yalnızca gVisor kullanır).

**Kritik not:** Kullanıcının yaşadığı Almanya REALITY sorununun kök nedeni TUN çekirdeğinin sing-box olması değil, sing-box'ın REALITY el sıkışmasında **1.8.1** sürümü iddia etmesi ve sunucunun (muhtemelen) istemci sürümü kapısı açık olmasıdır. Xray'e TUN'da geçmek bu sorunu çözer, ama daha hızlı çözümler de vardır (bkz. §8).

---

## 2. Xray Windows TUN Desteği — Kaynakta Doğrulama (v26.7.28)

### 2.1 Çekirdek dosyalar

| Dosya | İçerik |
|---|---|
| `proxy/tun/tun_windows.go` | Tam wintun implementasyonu: `wintun.CreateAdapter` (adaptör adından deterministik GUID), `StartSession`, rota/IP/DNS kurulumu (`SetRoutes`, `SetIPAddresses`, `SetDNS`), MTU (`NLMTU`), interface metric 0, gVisor endpoint'i, **loop önleme** (`findOutboundInterface` + `setinterface`/`IP_UNICAST_IF`) |
| `proxy/tun/config.go` | TUN config: `name`, `desc`, `mtu`, `gateway[]`, `dns[]`, `userLevel`, `autoSystemRoutingTable[]`, `autoOutboundsInterface` |
| `proxy/tun/stack_gvisor.go` | gVisor IP yığını (TCP/UDP/ICMP tam işleme) |
| `proxy/tun/udp_fullcone.go` | UDP full-cone desteği |
| `infra/conf/xray.go` | `"tun"` protokolü inbound registry'de kayıtlı (satır 34) ve inbound özel işleme (satır 141) |
| `infra/conf/tun.go` | JSON → protobuf dönüşümü; `autoOutboundsInterface` boşsa ve `autoSystemRoutingTable` doluysa `"auto"` varsayılanı |

### 2.2 Windows'ta süreç bazlı yönlendirme — VAR

- `app/router/condition.go` → `ProcessNameMatcher` routing kuralı (`process` alanı).
- `common/net/find_process_windows.go` → `FindProcess` **`GetExtendedTcpTable`/`GetExtendedUdpTable`** (iphlpapi) ile bağlantının sahip olduğu PID'i bulur, `.exe` soneki kırpılır, isim eşleşmesi yapılır.
- Yani uygulamanın GPN modundaki `process_name` kuralları (msedge → vpn, LoL → direct, ...) Xray routing'inde birebir karşılanır.

### 2.3 Donanım/binary gereksinimi — karşılanıyor

- wintun.dll, Xray'in resmî Windows paketinde **gömülü gelir** (26.7.28 zip'inde `wintun.dll` 427.552 bayt doğrulandı) — ekstra indirme gerekmez.
- Yönetici ayrıcalığı gerekir (wintun + rota değişikliği) — sing-box TUN ile aynı; uygulamanın mevcut yükseltme akışı (NeedAdmin + yeniden başlatma) aynen kullanılır.

### 2.4 Üretim kanıtı (bu makinede)

- v2rayN'nin TUN modu = **Xray'in TUN'u**. `Get-NetAdapter`: adaptör adı `xray_tun`, açıklama **"Xray Tunnel"**; rota `0.0.0.0/0 → xray_tun` (ifIndex 48) — şu an aktif ve tüm trafiği yakalıyor.
- Yani aynı binary (26.7.28) bu makinede TUN çalıştırıyor; uygulamanın "Xray Windows'ta TUN desteklemiyor" yorumu **güncel değil**.

---

## 3. Uygulamanın sing-box TUN'da Kullandığı Özellikler (Taşınacaklar)

Kaynak: `ServiceLib/Services/CoreConfig/Singbox/*` ve `TunLifecycleManager`.

| # | Özellik | Konum |
|---|---|---|
| 1 | TUN inbound: `interface_name=singbox_tun`, `mtu=1408`, `auto_route`, `strict_route`, Windows'ta **`stack="system"` (WFP)** | `SingboxInboundService.cs:75-82` |
| 2 | Per-app yönlendirme kuralları (`process_name` → vpn/direct) + catch-all | `SingboxRoutingService.cs`, `ManualRoutingRules` |
| 3 | DNS hijack + sniff eylemi | `SingboxRoutingService.cs:118-125` |
| 4 | **QUIC bloğu**: proxy'ye yönlendirilen süreçlerde UDP/443 engelleme (beyaz liste muafiyetiyle) | `SingboxRoutingService.cs:537-553` |
| 5 | DNS: `remote_dns` (cloudflare DoH, `detour=proxy`), `direct_dns`, hosts, fakeip, AAAA filtreleme | `SingboxDnsService.cs` |
| 6 | IPv6 yönetimi (TUN IPv6 kapalıyken proxy süreçlerine AAAA reddi) | `SingboxDnsService.cs:169-180`, `SingboxRoutingService.cs:98` |
| 7 | TUN yaşam döngüsü: yükseltme, `singbox_tun` doğrulaması, temizlik işlemi, `RemoveTunDevice` | `TunLifecycleManager.cs`, `WindowsUtils.cs` |
| 8 | Sağlık: SOCKS5 dinleyici + bağlantı sondası | `CoreManager.cs` (çekirdekten bağımsız — Xray ile aynen çalışır) |

---

## 4. Xray Karşılanabilirlik Haritası

| Uygulama özelliği | Xray 26.7.28 | Not |
|---|---|---|
| wintun TUN adaptörü | ✅ | `proxy/tun/tun_windows.go` |
| Otomatik rota (0.0.0.0/0 + özel) | ✅ | `autoSystemRoutingTable` |
| Loop önleme (çıkış arayüzü bağlama) | ✅ | `autoOutboundsInterface:"auto"` + `findOutboundInterface` (v2rayN'de aktif) |
| MTU, DNS, gateway atama | ✅ | `TunConfig` |
| **Per-app routing (process kuralı)** | ✅ | `ProcessNameMatcher` + `FindProcess` (Windows iphlpapi) |
| Sniffing (http/tls/quic) | ✅ | inbound `sniffing` ayarı |
| DNS routing (proxy üzerinden DoH, fakeip, AAAA filtre) | ✅ (farklı şema) | Xray `dns` modülü: `queryStrategy:"UseIP"`, server `tag`+`detour`; yeniden yazım gerekir |
| UDP/QUIC blok kuralı | ✅ | routing rule: `network:"udp"`, `port:443`, `outboundTag:"block"` |
| **Stack seçimi (system/WFP/mixed)** | ❌ **yalnızca gVisor** | Uygulama Windows'ta WFP/system kullanıyor (VM uyumluluğu için bilinçli seçim). gVisor farklı bir performans/uyumluluk profili sunar. |
| IPv6 | ⚠️ | Destek var; uygulamanın "TUN IPv6 kapalıyken AAAA reddi" mantığı Xray DNS kurallarıyla yeniden yazılmalı |

**Sonuç: Kapsam daraltıcı tek fark stack seçimidir; geri kalan her şey karşılanabilir veya yeniden yazılabilir.**

---

## 5. Yapılması Gereken Kod Değişiklikleri (Kapsam Tahmini)

1. **`CoreConfigContextBuilder`** (`ServiceLib/Handler/Builder/`): TUN açıkken Xray'i zorla sing-box'a çeviren kuralı kaldır/değiştir — düğüm veya varsayılan `CoreType=Xray` ise Xray kalsın. **Riskli alan:** yorum, "Xray Windows TUN'u desteklemez" varsayımına dayanıyor; §2'de çürütüldü.
2. **Yeni Xray TUN config üreticisi** (`ServiceLib/Services/CoreConfig/V2ray/`): `"protocol":"tun"` inbound + `routing` kuralları (process, QUIC blok, sniff) + `dns` modülü (DoH detour=proxy, fakeip, AAAA filtre). Mevcut sing-box üreticilerinin birebir karşılığı.
3. **`TunLifecycleManager`** genelleştirme: `singbox_tun` doğrulaması yerine "aktif çekirdeğin TUN adı" (Xray için örn. `aogpn_tun`); yükseltme + temizlik akışları çekirdekten bağımsız hale getirilmeli.
4. **`ForeignTunnelDetector`**: uygulamanın kendi TUN ad listesine Xray adı eklenmeli (örn. `aogpn_tun`) — v2rayN'nin `xray_tun`'u zaten yabancı sayılıyor.
5. **Varsayılan çekirdek stratejisi**: `GetCoreType`'ın VLESS varsayılanı zaten Xray (ConfigType 5 → CoreType 2); TUN zorlaması kalkınca varsayılan TUN çekirdeği de Xray olur — mevcut kullanıcı yapılandırması otomatik Xray'e geçer.
6. **Testler**: Xray TUN config'inin gerçek binary ile `xray run -test` doğrulaması (mevcut `XrayBinaryConfigValidationTests` deseni genişletilir).

**Tahmini iş:** 2-4 gün (config üreticileri) + 1 gün (yaşam döngüsü) + 1 gün (testler) ≈ **1 hafta**, gerçek düğümde doğrulama dahil.

---

## 6. Riskler ve Sınırlamalar

| Risk | Değerlendirme |
|---|---|
| **gVisor-only stack** | Uygulama Windows'ta WFP/system stack'i bilinçli seçmiş (yorum: "works everywhere, including VMs"). gVisor user-space yığını çoğu uygulamada çalışır ancak oyun/anti-cheat ve yoğun UDP yüklerinde farklı davranabilir; QUIC/HTTP3 tam destek için ayrıca test gerekir. |
| **DNS davranışı** | Xray DNS modülü ile sing-box DNS yapılandırması farklı; fakeip + sniff kombinasyonunda kaçak (leak) riski olmaması için dikkatli test. |
| **İki TUN aynı anda** | v2rayN + AoGPN birlikte çalışırsa aynı çakışma (internet kesintisi) Xray TUN'da da geçerli — mevcut `ForeignTunnelDetector` uyarısı bu durumu zaten kapsar. |
| **Bakım yükü** | İki TUN implementasyonunu (sing-box + Xray) aynı uygulamada tutmak config üretim yüzeyini iki katına çıkarır; hata düzeltmeleri iki tarafta da yapılır. |
| **Yönetici gereksinimi** | Değişmez; sing-box ile aynı akış. |
| **REALITY sürüm kapısı** | Xray'e geçiş bu sorunu çözer (Xray kendi sürümünü iddia eder) ama sorunun asıl kaynağı sunucudaki kapıysa en temiz çözüm kapıyı kapatmaktır (§8). |

---

## 7. Önerilen Uygulama Yolu (Fazlı)

1. **Faz 0 — Hızlı kazanım (kod yok):** TUN modundaki Almanya sorunu için kullanıcı zaten şunlardan biriyle çözer: (a) sunucuda `minClientVer`'i boşaltmak, (b) TUN kapalıyken düğümü Xray ile kullanmak. Uygulamadaki `RealityCoreFallbackAdvisor` da TUN kapalıyken otomatik Xray'e geçişi zaten yapıyor.
2. **Faz 1 — Deneysel Xray TUN:** `CoreConfigContextBuilder` kuralını kaldır, VLESS+REALITY düğümlerinde Xray TUN config üreticisini yaz, gerçek binary ile `run -test` + canlı düğüm doğrulaması. Varsayılan stack gVisor ile başlanır, sorun çıkarsa uygulama tarafında (ör. UDP soket ayarları) ince ayar yapılır.
3. **Faz 2 — Özellik paritesi:** DNS/fakeip/AAAA, QUIC bloğu, sniff kurallarının Xray karşılıkları; `TunLifecycleManager` genelleştirme; `ForeignTunnelDetector` güncelleme.
4. **Faz 3 — Kararlılık:** İki çekirdekte de tam test matrisi (GPN per-app, Global VPN, proxy-only, yeniden bağlanma, düğüm değişimi); `CoreHealthProbe` UDP sondaları; regresyon testleri.

---

## 8. Alternatif / Daha Hızlı Çözümler

1. **Sunucu tarafı (en hızlı):** 3x-ui → Inbound 443 → Reality → "Min client version" boşalt + Xray yeniden başlat. Çalışan config kontrolü:
   ```bash
   cat /usr/local/x-ui/bin/config.json | grep -A4 realitySettings
   ```
2. **TUN kapalı + Xray:** Bu makinede zaten varsayılan VLESS çekirdeği Xray; TUN kapalıyken Almanya düğümü yeni gömülü 26.7.28 Xray ile çalışır.
3. **sing-box güncellemesi:** 1.14.0-rc bile REALITY'de 1.8.1 iddia etmeye devam ediyor — sürüm kapısı varsa çözmez.

---

## 9. Sonuç

Xray Windows TUN desteği **teknik olarak yeterli ve üretimde kanıtlıdır** (v2rayN). Uygulamanın "Xray TUN desteklemez" varsayımı güncel değildir ve `CoreConfigContextBuilder`'daki zorlama kaldırılarak VLESS+REALITY düğümlerinde TUN modunda Xray kullanılabilir. Yapılacak iş, sing-box'a özel TUN config üreticilerinin Xray karşılıklarını yazmak (DNS, per-app kuralları, QUIC bloğu) ve TUN yaşam döngüsünü çekirdekten bağımsızlaştırmaktır. Tek gerçek farklılık stack seçimidir (Xray yalnızca gVisor; uygulama Windows'ta WFP/system kullanıyor). Önerilen yol, önce sunucu tarafı kapıyı kapatmak (5 dk), ardından istenirse Faz 1-3 ile Xray TUN'u aşamalı devreye almak.
