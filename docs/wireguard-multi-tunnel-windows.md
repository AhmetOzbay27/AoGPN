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

İki config'in tünel adları farklı olmalıdır (ör. `Italya.conf` / `Almanya.conf`).
İsim çakışırsa import edilmez veya üzerine yazar.

---

## 2) Dosyaları hazırlama (adlandırma) — KRİTİK

`D:\Programlar\VPN\` altında iki klasör var:

```
D:\Programlar\VPN\Italya\Italya.conf        → tünel adı "Italya"
D:\Programlar\VPN\Almanya\Almanya.conf      → tünel adı "Almanya"
```

**WireGuard for Windows tünel adını `.conf` DOSYA ADINDAN alır** — conf içindeki
`# Name = ...` yorumu yalnızca **wg-quick (Linux)** kuralıdır ve Windows
istemcisince **okunmaz**. Ayrıca tünel adları yalnızca ASCII karakter
kabul eder (`^[a-zA-Z0-9_=+.-]{1,32}$`, upstream `conf/name.go`):

> ⚠️ **"İtalya" (noktalı İ, U+0130) geçersiz bir tünel adıdır.** Güncel
> WireGuard for Windows bu adla tüneli içe **aktarmaz** ("Tunnel name is not
> valid"); eski/başka bir yolla bu adla oluşmuş bir tünelin yapılandırması
> kaybolursa tünel "The system cannot find the file specified" hatası verir
> (bkz. §6.1). Windows'ta her zaman ASCII ad kullanın: **`Italya`** (tek "i").

Örn. İtalya için (`Italya.conf`):

```ini
[Interface]
# Name = Italya
Address = 10.66.66.2/24
PrivateKey = <İtalya istemci özel anahtarı>
MTU = 1360
DNS = 1.1.1.1

[Peer]
PublicKey = <İtalya sunucu genel anahtarı>
Endpoint = 92.4.220.236:51820
AllowedIPs = 0.0.0.0/0, ::/0
PersistentKeepalive = 25
```

Almanya için aynısını **dosya adı** `Almanya.conf` ve
`Endpoint = 130.61.223.36:51820` ile yapın. `# Name =` satırı Windows'ta
görmezden gelinir; Linux wg-quick tarafında yararına dosyada tutulabilir.

> Dosyanın kendisini `Italya.conf` olarak adlandırın — dosya adı tünel adıdır.
> Farklı ad verirseniz (ör. `aogpn-client-it.conf`) tünel de o adla görünür.

---

## 3) İçe aktarma (import)

1. [wireguard.com/install](https://www.wireguard.com/install/) → **WireGuard · Windows** kur.
2. Uygulamayı açın → sol üst **Import tunnel(s) from file**.
3. Önce `D:\Programlar\VPN\Italya\Italya.conf`'u seçin → **Italya** tüneli görünür.
4. Tekrar **Import tunnel(s) from file** → `D:\Programlar\VPN\Almanya\Almanya.conf`
   → **Almanya** tüneli de görünür.

İki tünel de listeye girmiş olur:

```
Tüneller
  Italya    [Inactive]
  Almanya   [Inactive]
```

> Not: İtalya ve Almanya'ya **aynı VPN ağı** (10.66.66.2) atadık. Sorun
> değil — aynı anda tek tünel açık kaldığı sürece çakışmaz.

---

## 4) Hızlı geçiş (Activate / Deactivate)

- **İtalya'ya geçmek:** Italya satırına tıkla → **Activate**. Almanya otomatik
  kapanmaz; **Almanya'yı önce kapatmalısın** (kural §1).
- **Almanya'ya geçmek:** Almanya → **Activate**, Italya → Deactivate.

> İpucu: sistem tepsisindeki WireGuard simgesine sağ tık → **Tüneller**
> alt menüsünde Activate/Deactivate kısayolları listelenir; iki tıklamayla
> geçiş yaparsın.

**Kritik sıralama — çakışmayı önleme:**

1. Aktif olanı **Deactivate** (örn. Italya).
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
| LoL/EFT'te yüksek jitter | MTU 1420 uygun değil | Her iki tarafta MTU 1360 → 1384 → 1280 (önerilen: 1360) |
| Hostaficalarda sıradışı DNS | DNS leak | `AllowedIPs = 0.0.0.0/0, ::/0` ve `DNS = 1.1.1.1` ile zaten DNS tünel içine gider; TUN dışında başka DNS ayarı yoksa sorun yok |
| **"The system cannot find the file specified"** (Activate'te) | Tünelin `%ProgramFiles%\WireGuard\Data\Configurations\*.conf.dpapi` yapılandırma dosyası **kayıp veya bozuk** (güncelleme/antivirüs/yarım kurulum); ya da tünel adı geçersiz (Unicode) | **§6.1** — bozuk tüneli kaldır, ASCII adla yeniden içe aktar |

---

## 6.1 "The system cannot find the file specified" — kayıp/bozuk tünel yapılandırması

**Teşhis.** WireGuard for Windows her tüneli `C:\Program Files\WireGuard\Data\Configurations\<ad>.conf.dpapi`
olarak (DPAPI şifreli) saklar ve `WireGuardTunnel$<ad>` hizmeti bu dosyayı okur.
Dosya silinmiş/bozulmuşsa (yarım kalan WireGuard güncellemesi, antivirüs
karantinası, elle temizlik) veya adı istemcinin kuralına uymuyorsa
(`^[a-zA-Z0-9_=+.-]{1,32}$` — Unicode "İtalya" gibi), tünel açılamaz ve
**Windows hata 2 = "The system cannot find the file specified"** gösterilir.
Bu senaryo canlı gözlenmiştir: eski/bozuk "İtalya" tüneli bu hatayla açılamaz
ve aynı adla yeniden içe aktarılamaz (ad geçersiz).

**Kontrol:**
```powershell
Get-ChildItem "$env:ProgramFiles\WireGuard\Data\Configurations" | Select-Object Name,Length
# bozuk adaylar: Unicode içeren veya 0 bayt dosyalar; sağlıklı: Italya.conf.dpapi ~200+ bayt
```

**Onarım (manuel):**
1. WireGuard penceresinde bozuk tüneli seç → **Delete** (kaldırılmıyorsa
   yönetici PowerShell'de `sc.exe delete "WireGuardTunnel$İtalya"`).
2. `Data\Configurations` altındaki Unicode adlı / 0 bayt `*.conf.dpapi`
   dosyalarını sil.
3. **`Italya.conf`** adıyla kaydedilmiş geçerli bir conf'u **Import tunnel(s)
   from file** ile içe aktar → tünel "Italya" adıyla yeniden oluşur (§2).
4. **Activate** → §5 ile doğrula.

**Onarım (script):** `repair-wireguard-client-tunnel.ps1` (repo kökü) tüm bu
adımları yapar — bozuk/Unicode kalıntıları temizler, `WireGuardTunnel$<ad>`
hizmetini yeniden kurar:
```powershell
# Yönetici PowerShell — önce dosyayı Italya.conf adıyla kaydetmiş ol
powershell -ExecutionPolicy Bypass -File .\repair-wireguard-client-tunnel.ps1 `
    -ConfPath .\.freebuff\aogpn-client-it.conf -TunnelName Italya
```

> Geçersiz (Unicode) adla tünel **asla** yeniden oluşturulamaz — istemci
> "Tunnel name is not valid" der. Çözüm her zaman ASCII addır (`Italya`).

---

## 7) Özet

- İki `.conf` tek WireGuard programında tutulur; **tek seferde tek tünel aktif**.
- Tünel adı **dosya adından** gelir ve **yalnızca ASCII** olabilir:
  `Italya.conf` / `Almanya.conf` (Unicode "İtalya" geçersiz — §6.1).
- Deactivate → (1 sn) → Activate sırasıyla geçiş; çakışma yaşarsan hepsini
  kapatıp birini aç.
- Doğrulama: sunucuda `latest handshake`, istemcide `ping 10.66.66.1`.
- Hatasız kalıcılık için her sunucunun VCN kuralı ve MTU değeri doğru olmalı.