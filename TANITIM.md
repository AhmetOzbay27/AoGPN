# AO GPN — Tanıtım

> **AO GPN (Gaming Private Network)** — Ahmet Özbay tarafından geliştirilen,
> oyuncular için tasarlanmış modern bir VPN/GPN istemcisi. Windows, Linux ve
> macOS'ta çalışır; GPL-3.0 lisansıyla açık kaynaklıdır.

---

## AO GPN nedir, amacı ne?

AO GPN, oyun trafiğini tünelden geçirerek **gerçek oyun sunucusu gecikmesini
(ping) ölçülebilir şekilde düşürmek** için tasarlanmıştır. Sıradan bir VPN'den
farkı, "oyun tüneli" mantığıdır: listedeki oyunların trafiği tünellenir, geri
kalan her şey doğrudan (direkt) bağlantıda kalır.

Altyapıda **Xray** ve **sing-box** çekirdeklerini kullanır; arayüz, WebView2
üzerinde çalışan tamamen özel bir kontrol merkezi dashboard'dur.

---

## Özellik dökümü

### 🎮 Oyun odaklı yönlendirme
- **GPN Game Tunnel** — uygulama başına bölünmüş tünel: yalnızca atanan
  oyun/uygulamalar VPN'den geçer
- **Global VPN modu** — tüm trafik TUN üzerinden
- **Whitelist/Blacklist yönü** — sadece listedekiler mi tünellensin, yoksa
  liste dışındakiler mi? Tek tıkla tersine çevrilebilir
- Uygulama başına 4 rota: **VPN / Proxy / Direct / Block**
- **Oyun başlayınca otomatik bağlan** — listede bir oyun açılınca akıllı tünel
  devreye girer, son oyun kapanınca kendiliğinden kapanır
- **Gerçek oyun sunucusu ping'i** — bağlantı izleyicisinden toplanan gerçek
  sunucu IP:port'ları ölçülür: bağlantı öncesi (direkt) → sonrası (tünel)
  farkı kartlarda gösterilir, örn. `42 ms → 18 ms (−24 ms)`
- EXE sürükle-bırak ile oyun ekleme

### 🖥️ Canlı kontrol merkezi (WebView2 dashboard)
- Animasyonlu CONNECT halkası, canlı durum panelleri, IP doğrulama, ISP taban çizgisi
- **GlassWire tarzı Bağlantı İzleyici** — hangi program nereye, hangi rota
  üzerinden bağlanıyor (ülke/ASN bilgisi dahil)
- Uygulama başına canlı trafik (↓/↑), filtreleme
- **Düğüm (node) yönetimi** — ping testi (TCP/UDP), çoklu seçim,
  kopyala/yapıştır, sil/devre dışı bırak, yinelenenleri temizle,
  favori/ülke/ping sıralaması, gruplar
- **Düğüm havuzu** — GitHub raw `.txt` / abonelik linklerinden tek tıkla düğüm çekme
- **GPN Sunucuları** — WireGuard `.conf` içe aktarma, canlı ölçüm (measure)

### 🌐 Protokol ve bağlantı zekâsı
- Çoklu protokol: **Otomatik, WireGuard, Mimic Reality/TLS, Hysteria2/TUIC, OpenVPN**
- Katmanlı (tier) bağlantı: önce saf UDP (WireGuard), engelliyse TCP (V2ray)
  yedeğine düşüş
- İtalya/Almanya sunucuları arasında **otomatik en iyi sunucu seçimi + canlı
  failover** (ping + UDP sağlık + gerçek Noise_IKpsk2 el sıkışması ölçümüyle)
- REALITY düğümleri için otomatik çekirdek değişimi (sing-box → Xray), kullanıcı onaylı
- **Sistem proxy entegrasyonu** — tünel olmadan da çalışan v2rayN tarzı
  "yalnızca proxy" modu; Set/Clear/Unchanged/PAC; TUN açıkken güvenlik için
  otomatik devre dışı
- Otomatik yeniden bağlanma, yabancı VPN çakışma uyarısı, ağ gelince başlatma

### 🎨 Görsel kimlik
- **25 tema** (Nebula, Inferno, Synthwave, Cyberpunk, Matrix, Crimson,
  Velocity…), animasyonlu efektler ve bağlanınca konfeti kutlaması
- **3 tam bağımsız skin** (NEXUS GPN, CYBER, INFRA) — izole iframe içinde
  tamamen farklı tasarımlar, canlı uygulama durumu köprüsüyle beslenir
- Splash ekranı (yüksek çözünürlüklü logo, yükleme çubuğu, sürüm),
  sistem tepsisi temalı menü

### 🌍 Dil ve platform
- **9 dil:** Türkçe, İngilizce, Basitleştirilmiş/Geleneksel Çince, Farsça,
  Fransızca, Macarca, Endonezce, Rusça
- Windows (x64/x86/arm64), Linux (x64/arm64/riscv64/loong64),
  macOS (x64/arm64)
- GPG imzalı sürümler, SHA-256 doğrulamalı çekirdek/güncelleme indirmeleri

### 🔧 Geliştirici/operasyon araçları (depodaki CLI araçları)
- **GpnProbeTool** — canlı sunucu teşhisi: ICMP, UDP sağlığı, gerçek WG el
  sıkışması, failover karar matrisi, uzun süreli izleme, NDJSON çıktısı,
  webhook bildirimi
- **GpnJsonlConsumer** — prob çıktısını Prometheus metriklerine çevirir
  (textfile collector + HTTP `/metrics`)
- **GpnSessionTool** — oturum denetim günlüğü okuyucu
- **CoreTool** — Linux/macOS paketleme için headless çekirdek indirici
- CI: tüm platformlar için build, test, GPG imzalama, winget yayını,
  wintun smoke testi

---

## Tanıtım metni

> ### AO GPN — Oyunun İçin Özel Ağ
>
> **Gecikme, oyunda kaybettiren tek şeydir.** AO GPN, oyun trafiğini akıllı bir
> tünelden geçirerek pingini ölçülebilir şekilde düşüren, oyuncular için
> sıfırdan yazılmış modern bir GPN/VPN istemcisi.
>
> **Sadece oyunun tünellenir, gerisi karışmaz.** Global VPN'in aksine AO GPN,
> oyunların trafiğini yönlendirirken diğer uygulamalarını doğrudan bağlantıda
> bırakır. Oyunu listene ekle, tüneli aç — gerisini o yönetir. Hatta listede
> bir oyun açıldığında bağlantı kendiliğinden kurulur, son oyun kapanınca kapanır.
>
> **Övünerek söylemiyoruz, ölçüyoruz.** Her oyun kartında bağlantıdan önceki
> ve sonraki *gerçek* oyun sunucusu gecikmesini görürsün: `42 ms → 18 ms (−24 ms)`.
> İyileşme yeşil, kötüleşme kırmızı — rakamlar değil, sonuçlar konuşur.
>
> **Akıllı bağlantı.** WireGuard, Reality/TLS, Hysteria2/TUIC ve OpenVPN
> protokollerini tek tıkla yönet; otomatik mod, sana en düşük gecikmeyi veren
> sunucuyu canlı ölçümlerle seçsin. Sunucu düşerse saniyeler içinde yedeğine
> geçer — sen maçtan kopmazsın.
>
> **Kontrol merkezin avucunda.** Camdan bir dashboard: hangi programın nereye,
> hangi rota üzerinden bağlandığını canlı izle. 25 tema ve 3 tamamen farklı
> arayüz tasarımı (skin) ile görünüm senin.
>
> **Herkesin dili, her platform.** Türkçe dahil 9 dil; Windows, Linux ve
> macOS'ta çalışır. Çekirdek ve güncelleme indirmeleri SHA-256 ile doğrulanır,
> sürümler GPG ile imzalanır — güven pazarlıksız.
>
> **AO GPN.** Sadece kazanmak için yapıldı. 🎮
>
> 🔗 github.com/AhmetOzbay27/AoGPN · Topluluk: t.me/AoGPN
