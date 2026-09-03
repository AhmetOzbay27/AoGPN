# Faz 1 Canlı Test Rehberi — İtalya/Almanya WireGuard Outbound

> **Kapsam:** Faz 1 ("sing-box + WireGuard outbound") üretim senaryosunun
> canlı sunuculara karşı uçtan uca doğrulaması. Amaç: İtalya/Almanya `.conf`
> dosyalarını uygulamaya içe aktarmak, **Bağlan**'ı çalıştırmak ve **sunucuda
> gerçek WireGuard el sıkışmasının (handshake) görünmesini** doğrulamak.
>
> **Hedef sunucular:**
> - **İtalya** `92.4.220.236:51820` — Oracle Linux 9, `opc@92.4.220.236` (`.freebuff/ssh/aogpn.key`)
> - **Almanya** `130.61.223.36:51820` — Ubuntu, `ubuntu@130.61.223.36` (`.freebuff/ssh/alman.key`)
>
> Faz 1'de veri yolu `sing-box TUN (stack=system / WFP)` üzerinden
> **gerçek WireGuard outbound**'dur (`SingboxOutboundService.BuildWireGuardEndpoint`,
> userspace endpoint, MTU 1420, keepalive). TCP meltdown, dış taşıyıcı saf UDP
> olduğu için yapısal olarak imkânsızdır. GPN `process_name` kuralları aynen çalışır.

---

## 0) Ön koşullar

1. **Uygulama** kurulu, TUN modu açık.
2. **İki `.conf`** hazır ve içlerindeki anahtarlar sunucularla birebir eşleşiyor:
   - `.freebuff/aogpn-client.conf` → İtalya (`92.4.220.236`)
   - `.freebuff/aogpn-client-alman.conf` → Almanya (`130.61.223.36`)
3. **SSH erişimi** her iki sunucuya (anahtarlar `.freebuff/ssh/` altında).
4. Sunucular kurulu ve `wg0` aktif (bkz. `.freebuff/aogpn-server-setup.sh`,
   `.freebuff/aogpn-alman-setup.sh`). Self-test zaten geçmiş olmalı
   ("SELF-TEST: BASARILI").

> **IP yerine etki alanı mı?** `.conf` içindeki `Endpoint` bir IP olmalıdır
> (sing-box WireGuard DNS'e çözülemez). Sunucu el sıkışması için DNS yerine
> IP sabitlenmiş olmalı; aksi hâlde `wg show` "no endpoint" gösterir.

---

## 1) Adımlar: içe aktarma

### 1.1 İtalya'yı içe aktar (`aogpn-client.conf`)

Uygulamada **Ayarlar → Abonelikler / Düğüm ekle** → **Dosyadan içe aktar**
veya listede sağ tuş → **İçe aktar** üzerinden `aogpn-client.conf`'u seçin.

Import akışı şunları yapar (kod: `ConfigHandler.AddBatchServers` →
`AddBatchServers4Wireguard` → `WireguardFmt.ResolveConfig`):

- `.conf`'taki `[Interface]`/`[Peer]` bloklarını tek bir
  `ProfileItem` (EConfigType.WireGuard) olarak çözümler:
  `Address=10.66.66.2/24`, `PrivateKey=<istemci>`, `MTU=1420`, `DNS=1.1.1.1`,
  `PublicKey=<sunucu>`, `Endpoint=92.4.220.236:51820`, `PersistentKeepalive=25`.
- Profili `ProfileItem` tablosuna ekler ve **gerçek zamanlı el sıkışma/UDP
  sağlık ölçümü için** `gpn_servers` tablosuna yazar (`WireGuardServerCatalog.UpsertFromProfileAsync`);
  istemci özel anahtarı **DPAPI ile şifrelenerek** saklanır.

### 1.2 Almanya'yı içe aktar (`aogpn-client-alman.conf`)

Aynı işlemi ikinci dosya için tekrarlayın. İki profil de listede görünür
iste (İtalya / Almanya). Katalog bunları iki aday olarak tanır.

> **Doğrulama:** içe aktarım sonrası profilin düğüm satırı WireGuard tipi
> olmalı, `EndPoint` doğru ve özel anahtar alanı dolu olmalıdır.

---

## 2) Adımlar: Bağlan (otomatik ölçüm + seçim)

1. **Varsayılan profili WireGuard profillerinden birine** ayarlayın
   (İtalya **veya** Almanya — hangisi olduğu önemli değil; GPN akışı
   her ikisini de ölçüp en düşük gecikmeliye bağlanır).
2. TUN modu **açık** olsun.
3. **Bağlan**'a basın.

`MainWindowViewModel.Reload()` akışı (`ServiceLib/Services/GpnServerSelectionService` +
`GpnConnectionCoordinator`):

```
Bağlan
  → WireGuardServerCatalog.LoadAsync()            // gpn_servers → adaylar (DPAPI çözülür)
  → İtalya + Almanya'ya PARALEL ICMP ping         // en düşük ms
  → seçili adaya gerçek WireGuard el sıkışması    // Noise_IKpsk2 initiation + MAC1 doğrula
      → UDP açık   → ConnectionMode.WireGuardUDP   // İtalya veya Almanya
      → UDP bloklu → ConnectionMode.V2rayTCP (fallback) → mevcut V2ray düğümü
  → GpnCoreLauncher: GpnServerProfile → WireGuard ProfileItem → BuildAll → CoreEngineHost.StartAsync
  → failover izleyicisi (15 sn), sunucu ölürse diğerine / V2rayTCP'ye düşer
```

Bağlan akışı yalnızca seçili profil **WireGuard** ve **TUN açık**ken tetiklenir;
VLESS/SS seçiliyse mevcut akış birebir korunur.

---

## 3) Adımlar: sunucuda el sıkışma doğrulama

Bağlantı kurulunca sunucu tarafında `latest handshake` görünmelidir.
**Doğrulama penceresi:** el sıkışma ilk bağlantıdan sonra ±2-3 sn içinde oluşur;
`PersistentKeepalive=25` sayesinde 25 sn'de bir yenilenir (NAT deliği korunur).

### 3.1 Genel `wg show` (iki sunucu da)

```bash
# İtalya (Oracle Linux, opc)
ssh -i .freebuff/ssh/aogpn.key opc@92.4.220.236 "sudo wg show"

# Almanya (Ubuntu)
ssh -i .freebuff/ssh/alman.key ubuntu@130.61.223.36 "sudo wg show"
```

Beklenen çıktı:

```
interface: wg0
  public key: <sunucu pubkey>
  private key: (hidden)
  listening port: 51820

peer: <İstemci pubkey — .conf'taki istemcinin karşılığı>
  endpoint: <istekçinin dış IP>:<kaynak port>
  allowed ips: 10.66.66.2/32
  latest handshake: 7 seconds ago       ← BURASI GÜNCEL OLMALI
  transfer: 1.4 KiB received, 42.4 KiB sent
```

> Bağımsız uygulama (sing-box) kullanıldığında `endpoint` satırı sunucunun
> istemciyi gördüğü geçerli dış IP/port olur. `latest handshake` **son
> 1 dakika** içindeyse el sıkışma başarılı demektir.

### 3.2 Daraltılmış kontrol (sadece handshake)

```bash
# İtalya
ssh -i .freebuff/ssh/aogpn.key opc@92.4.220.236 "sudo wg show | grep -E 'handshake|endpoint'"

# Almanya
ssh -i .freebuff/ssh/alman.key ubuntu@130.61.223.36 "sudo wg show | grep -E 'handshake|endpoint'"
```

### 3.3 İlgili hangi sunucu el sıkıştı?

Bağlantı en düşük pingli sunucuya kurulur. Sorun ararken **her iki sunucuda**
`wg show` çalıştırın — aktif oturumun bulunduğu tarafta `latest handshake`
günceldir, diğerinde zaman aşımına uğramış ya da hiç oturum yoktur.

---

## 4) İstemci tarafı doğrulama (uygulama)

Bağlantı kurulduktan sonra istemci:

```powershell
# Tünel içi ağ geçidine ping (wg0 sunucu tarafı)
ping 10.66.66.1

# Egress IP/ülke — seçilen sunucunun bölgesini yansıtmalı
(Invoke-RestMethod https://ipapi.co/json).country
```

- Uygulama durum çubuğu/dashboard bağlı sunucuyu (İtalya/Almanya) gösterir.
- `GpnCoreLauncher` DiagLog'a yazar: `GPN_LAUNCH mode=WireGuardUDP node=… core=sing_box`.
- Failover etkin ise 15 sn'de bir sağlık ölçümü yapılır; ölü sunucuya geçilmez,
  sağlıklı alternatif yoksa `V2rayTCP`'ye düşülür.

---

## 5) Sorun giderme

| Belirti | Olası neden | Çözüm |
|---|---|---|
| Sunucuda hiç `latest handshake` yok | 51820/udp erişilemiyor (VCN/güvenlik duvarı) | Oracle VCN Ingress `UDP 51820` + firewalld `--add-port=51820/udp`; `.conf`'ta `Endpoint` IP doğru mu |
| Uygulama `V2rayTCP`'ye düştü | UDP probu engel/NoResponse | `GpnProbeOptions.TreatNoResponseAsBlocked=false` (sağlıklı yol NoResponse verir); handshake probe kullandığınızdan emin olun |
| El sıkışma görünüyor ama internet yok | MTU 1420 uyumsuz / NAT masquerade yok | Her iki tarafta MTU; `firewall-cmd --add-masquerade`, `ip_forward=1`, trusted zone |
| Veri akışı yok, bağlantı "bağlı" | `AllowedIPs`/rota çakışması (iki tünel) | aynı anda tek aktif tünel; seçilen profil doğru |
| `wg show` "no endpoint" | İstemci henüz bir paket göndermedi | `PersistentKeepalive` etkin; uygulamayı kapat/aç, yeniden Bağlan |

---

## 6) Hızlı ölçüm araçları

`AoGPN.GpnProbeTool` (Faz 4 teşhis CLI) Bağlan akışının her aşamasını canlı
sunuculara karşı test eder:

```bash
cd AoGPN && dotnet AoGPN.GpnProbeTool/bin/Debug/net10.0/AoGPN.GpnProbeTool.dll \
    ../.freebuff/aogpn-client.conf ../.freebuff/aogpn-client-alman.conf
```

- Satır başına: ICMP gecikme (min/avg/max/kayıp), junk-UDP sağlık, **gerçek
  WireGuard el sıkışma**, otomatik seçim kararı.
- `--no-handshake`, `--no-select`, `--timeout-ms`, `--samples`, `--icmp auto|icmp|tcp`.
- Dönüş kodu `0` = en az bir sunucuda açık UDP yolu var.

---

## 7) Kabul ölçütleri (test "yeşil" sayılır)

- [ ] İtalya ve Almanya `.conf`'ları içe aktarıldı; iki WireGuard profili listede.
- [ ] `Bağlan` sonrası uygulama durumu "bağlı" ve istemci tarafı `ping 10.66.66.1` yanıtı.
- [ ] **Seçilen sunucuda `latest handshake` < 1 dk** (hem `wg show` birebir).
- [ ] `transfer` sayaçları artıyor (veri akıyor) — salt el sıkışma değil.
- [ ] İkinci sunucuya da ayrı ayrı Bağlan doğrulandı (her iki uç nokta el sıkışıyor).
- [ ] (Opsiyonel) EFT/LoL canlı oturumda gecikme/jitter kararlı, meltdown yok.