# 3x-ui REALITY "Min Client Ver" Kapısı — Kontrol ve Temizleme Rehberi

> Hedef: AoGPN'nin sing-box çekirdeğiyle bağlanamadığı REALITY düğümlerini
> (örn. Almanya, 92.5.108.102) sunucu tarafında çalışır hale getirmek.

## Neden gerekli? (kesin kanıt)

Xray-core **26.3.27'den sonraki** tüm sürümlerde (PR #6181, commit `af7eb68`,
2026-07-11) REALITY sunucusu `minClientVer` alanı **boş bırakılsa bile** yerleşik
minimum **26.3.27** sürümünü uygular:

```go
// Xray-core v26.7.28, infra/conf/transport_security.go (satır 103–118)
if c.MinClientVer != "" {
    config.MinClientVer = make([]byte, 3)   // "a.b.c" olarak ayrıştır
} else {
    config.MinClientVer = []byte{26, 3, 27} // boş = 26.3.27 (kendi riskinizde değiştirin)
}
```

Sunucu tarafı kontrol (XTLS/Reality, `tls.go`):

```go
if (config.MinClientVer == nil || istemciSürümü >= config.MinClientVer) { ... } else { reddet }
```

Bunu açıklayan tablo:

| İstemci | El sıkışmada bildirdiği sürüm | 26.3.27 kapısı | Sonuç |
|---|---|---|---|
| v2rayN (Xray 26.6.1) | 26.6.1 | geçer | ✅ çalışır |
| AoGPN Xray çekirdeği (26.7.28) | 26.7.28 | geçer | ✅ çalışır |
| AoGPN sing-box (1.13.19) | **1.8.1** (sabit kodlanmış) | **reddedilir** | ❌ "received real certificate" / 403-404 |
| Africa (eski Xray — kapı commit'i yok) | herhangi | kapı yok | ✅ çalışır |

3x-ui v3.7.0'un kendi yardım metni de aynı şeyi söyler:
> *"Empty does not mean unrestricted: Xray-core then enforces the built-in
> minimum of the core build you run (26.3.27) ... including third-party cores
> such as Mihomo and sing-box. Set `1.0.0` to accept them."*

**Önemli:** Alanı "temizlemek" (boşaltmak) kapıyı **açmaz**. Doğru değer
**`1.0.0`**'dır (bedeli: çok eski TLS parmak izlerine de izin verilir).

---

## Yöntem 1 — Panel (web UI) ile, ~5 dakika

Panel adresi: `http://92.5.108.102:7168/oyun/panel/`

1. Kullanıcı adı `ahmet` / şifre `ahmet` ile giriş yapın.
2. Sol menüden **Inbound List** (Giriş Listesi) açın.
3. **Port 443**'teki VLESS inbound'un yanındaki **Edit** (Düzenle) düğmesine tıklayın.
4. **Stream Settings** → **Reality** bölümüne inin.
5. **"Min Client Ver"** alanına bakın
   (Türkçe arayüzde: **"Min. Kullanıcı Sürümü"**).
   - Şu anki değeri not edin (boş veya `26.x` gibi bir değer olabilir).
6. Alanın içine **`1.0.0`** yazın.
7. **"Max Client Ver"** (Maks. Kullanıcı Sürümü) alanı **doluysa boşaltın** —
   üst sınır bırakmayın, yoksa gelecekteki yeni Xray sürümleri reddedilir.
8. **Save** (Kaydet) → 3x-ui Xray'i otomatik yeniden başlatır, birkaç saniye bekleyin.
9. AoGPN'de bağlanmayı yeniden deneyin.

---

## Yöntem 2 — SSH ile (doğrulama + acil düzeltme)

> **Önce gerçek yolları bulun.** 3x-ui sürümüne göre DB ve bin klasörü farklı
> olabilir. Varsayılanlar şunlardır:
> - İnstall dizini: `/usr/local/x-ui/` (çalışan config: `/usr/local/x-ui/bin/config.json`)
> - Veritabanı: `/etc/x-ui/x-ui.db` (varsayılan; eski kurulumlarda bazen `/usr/local/x-ui/x-ui.db`)
>
> Kesin yolları bulmak için aşağıdaki komutlardan sonuca göre yolu ayarlayın:
> ```bash
> # Çalışan sürecin working directory'si = install dizini
> ls -la /proc/$(pgrep -f 'x-ui$' | head -1)/cwd 2>/dev/null
> # DB dosyası nerede:
> find / -name 'x-ui.db' 2>/dev/null | head -5
> ```

### 2a. Çalışan config'i kontrol et

```bash
cat /usr/local/x-ui/bin/config.json | python3 -c "
import json, sys
d = json.load(sys.stdin)
for i in d['inbounds']:
    r = (i.get('streamSettings') or {}).get('realitySettings') or {}
    print(i.get('port'), '->',
          'minClientVer =', repr(r.get('minClientVer')),
          '| maxClientVer =', repr(r.get('maxClientVer')))
"
```

Çıktıda `minClientVer = ''` görürseniz **kapı açık DEĞİLDİR** — Xray bunu
26.3.27 olarak uygular. Kapının açık olduğunu gösteren değer: `'1.0.0'`.

### 2b. Panel erişimi yoksa acil düzeltme (DB üzerinden)

> Panel her zaman tercih edilir; DB'yi elle değiştirmek son çaredir.
> Tablo adı **`inbounds`** (çoğul) ve sütun **`stream_settings`**'dır.

```bash
DB=/etc/x-ui/x-ui.db                # gerçek yola göre değiştirin (bkz.: önceki not)
# 1) Yedek al
cp "$DB" /root/x-ui.db.bak

# 2) 443'teki inbound'un minClientVer değerini 1.0.0 yap (ve maxClientVer'ı boşalt)
sqlite3 "$DB" "UPDATE inbounds SET stream_settings =
  json_set(json_set(stream_settings, '$.realitySettings.minClientVer', '1.0.0'),
                              '$.realitySettings.maxClientVer', '')
  WHERE port = 443;"

# 3) Xray'i (3x-ui servisini) yeniden başlat
systemctl restart x-ui
```

### 2c. Xray'in gerçekten yeni config ile ayağa kalktığını doğrula

```bash
systemctl status x-ui          # active (running) olmalı
tail -50 /usr/local/x-ui/log/error.log | grep -iE "reality|handshake|version"
cat /usr/local/x-ui/bin/config.json | grep -o '"minClientVer"[^,]*' # '1.0.0' olmalı
```

---

## Kapı 1.0.0 yapıldıktan sonra

- **TUN modunda:** AoGPN sing-box kullanmaya devam eder ama artık kapıya takılmaz —
  Almanya düğümü çalışmalı.
- **Yine çalışmazsa:** başka bir çekirdek/TUN çakışması var demektir. v2rayN
  **tamamen** kapalı olmalı (tepsi ikonu → **Çıkış**; pencereyi kapatmak yetmez).
  Doğrulama:
  ```powershell
  Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Name -match 'tun' }
  Get-Process v2rayN,xray
  ```
  İkisi de boş dönmeli. AoGPN bağlanırken yabancı TUN/port çakışması algılarsa
  kendi uyarısını gösterir.

---

## Özet

| Durum | Yapılacak |
|---|---|
| Panel'de `Min Client Ver` boş | **`1.0.0`** yaz → Save |
| Panel'de `Min Client Ver` = `26.x` | `1.0.0` yap → Save |
| `Max Client Ver` dolu | Boşalt → Save |
| SSH ile doğrulama | `cat /usr/local/x-ui/bin/config.json` → `minClientVer = '1.0.0'` |
