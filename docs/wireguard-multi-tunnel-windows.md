# Windows'ta Çoklu WireGuard Tüneli — İtalya / Almanya Hızlı Geçiş

> Hedef: Elinizde iki sunucu var —
> - **İtalya** `92.4.220.236` (sunucu ayağı hazır, `client.conf` mevcut)
> - **Almanya** `130.61.223.36` (sunucu ayağı hazır, `client.conf` mevcut)
>
> Bu rehber, iki `.conf` dosyasını WireGuard for Windows'a **aynı anda**
> kaydedip oyuna göre **hızlıca Activate/Deactivate** ile hangi bölgeden çıkacağını
> seçmeyi anlatır. Ayrıca birden fazla oyun sunucusunun iki config'i paralel
> tuşladığında yaşanabilecek çakışmayı (internet gitti) önler.

---

## 1) Neden "iki tünel" değil de "tek kümede iki config"?

WireGuard for Windows **tek seferde yalnızca bir tüneli Activate** edebilir.
"Tek tünel kümesi" derken kastedilen: iki `.conf` dosyasını aynı programda
saklamak ve ihtiyaca göre teker teker açıp kapatmaktır. Aynı anda ikisini
açar **AMAS** iki tünel de `0.0.0.0/0` rota koyar — rota çakışmasından internet
gider. Çözüm: **sadece birini Activate** tutmak.

Dosyaların donanım adı farklı olmalıdır (ör. `[Interface] # Name = İtalya` /
`# Name = Almanya`). İsim çakışırsa import edilmez veya üzerine yazar.

---

## 2) Dosyaları hazırlama (adlandırma)

`D:\Programlar\VPN\` altında iki klasör var:

```
D:\Programlar\VPN\Italya\aoGPN-client.conf      → tünel adı "İtalya"
D:\Programlar\VPN\Almanya\aoGPN-client.conf     → tünel adı "Almanya"
```

WireGuard for Windows, config'in `[Interface]` bölümünde `##` yorumuyla
`# Name = ...` satırını okuyarak tünele görünen ad verir. Örn. İtalya için:

```ini
[Interface]
# Name = İtalya
Address = 10.66.66.2/24
PrivateKey = <İtalya istemci özel anahtarı>
MTU = 1420
DNS = 1.1.1.1

[Peer]
PublicKey = <İtalya sunucu genel anahtarı>
Endpoint = 92.4.220.236:51820
AllowedIPs = 0.0.0.0/0, ::/0
PersistentKeepalive = 25
```

Almanya için aynısını `# Name = Almanya` ve `Endpoint = 130.61.223.36:51820`
ile yapın. Zaten farklı dosya adıyla iki ayrı dosya import ederseniz programa
iki ayrı tünel olarak görünür; ama isim satırı okunmazsa dosya adı kullanılır
(İtalya / Almanya dosya adlarından ayırt edebilirsiniz).

---

## 3) İçe aktarma (import)

1. [wireguard.com/install](https://www.wireguard.com/install/) → **WireGuard · Windows** kur.
2. Uygulamayı açın → sol üst **Import tunnel(s) from file**.
3. Önce `D:\Programlar\VPN\Italya\aoGPN-client.conf`'u seçin → **İtalya** tüneli görünür.
4. Tekrar **Import tunnel(s) from file** → `D:\Programlar\VPN\Almanya\aoGPN-client.conf`
   → **Almanya** tüneli de görünür.

İki tünel de listeye girmiş olur:

```
Tüneller
  İtalya    [Inactive]
  Almanya   [Inactive]
```

> Not: İtalyaya ve Almanyaya **aynı VPN ağı** (10.66.66.2) atadık. Sorun
> değil — aynı anda tek tünel açık kaldığı sürece çakışmaz.

---

## 4) Hızlı geçiş (Activate / Deactivate)

- **İtalya'ya geçmek:** İtalya satırına tıkla → **Activate**. Almanya otomatik
  kapanmaz; **Almanyayı önce kapatmalısın** (kural §1).
- **Almanya'ya geçmek:** Almanya → **Activate**, İtalya → Deactivate.

> İpucu: sistem tepsisindeki WireGuard simgesine sağ tık → **Tüneller**
> alt menüsünde Activate/Deactivate kısayolları listelenir; iki tıklamayla
> geçiş yaparsın.

**Kritik sıralama — çakışmayı önleme:**

1. Aktif olanı **Deactivate** (örn. İtalya).
2. Bir saniye bekle, tepsideki simge "Inactive" olana kadar.
3. İstediğini **Activate** (örn. Almanya).

İkisini üst üste Activate edersen iki tünel aynı `0.0.0.0/0` rotasını paylaşır
→ internet kopabilir. Bu durumda hepsini Deactivate edip yeniden tek tünel a.

---

## 5) Doğrulama

**Windows tarafı:**

```powershell
# Aktif tünelin IP'sine ping (istersen tünelin içine baktır)
ping 10.66.66.1
# İnternet çıkışının nereden göründüğü (IP lokasyonu <-> seçili sunucu)
(Invoke-RestMethod https://ipapi.co/json).country
```

İtalya aktifse `10.66.66.1` yanıtı + ülke `GB`/`IT` yakın; Almanya aktifse
ülke `DE`.

**Sunucu tarafında hangi tünel oturumu açıksa o görünür:**

```powershell
# İtalya
ssh -i "D:\Programlar\VPN\Italya\ServerKEY.key" opc@92.4.220.236 "sudo wg show | grep handshake"
# Almanya
ssh -i "D:\Programlar\VPN\Almanya\Almanya.key" ubuntu@130.61.223.36 "sudo wg show | grep handshake"
```

Active tüneldeki sunucuda `latest handshake` **son dakika** içinde olmalı.

---

## 6) Olağan sorunlar

| Belirti | Olası neden | Çözüm |
|---|---|---|
| Tıkladım ama bağlanmıyor | VCN'de UDP 51820 Ingress Rule yok | Oracle Console → VCN → subnet → Security List → Add Ingress Rule: UDP 51820 (`0.0.0.0/0`) |
| İki tüneli açtım, internet yok | İki `0.0.0.0/0` rotası çakışıyor | Hepsini Deactivate, sonra sadece birini Activate |
| `handshake` hiç görünmüyor | İstemciden 51820/udp dışa engellenmiş | Router/firewall'da UDP'yi engelleme; ISP'nin UDP'yi boğduğu nadir durumda MTU düşür |
| LoL/EFT'te yüksek jitter | MTU 1420 uygun değil | Her iki tarafta MTU 1420 → 1384 → 1280 |
| Hostaficalarda sıradışı DNS | DNS leak | `AllowedIPs = 0.0.0.0/0, ::/0` ve `DNS = 1.1.1.1` ile zaten DNS tünel içine gider; TUN dışında başka DNS ayarı yoksa sorun yok |

---

## 7) Özet

- İki `.conf` tek WireGuard programında tutulur; **tek seferde tek tünel aktif**.
- Deactivate → (1 sn) → Activate sırasıyla geçiş; çakışma yaşarsan hepsini
  kapatıp birini aç.
- Doğrulama: sunucuda `latest handshake`, istemcide `ping 10.66.66.1`.
- Hatasız kalıcılık için her sunucunun VCN kuralı ve MTU değeri doğru olmalı.