# Libs/WinDivert — resmî WinDivert 2.2.2 dağıtımı (build'e gömülü)

GPN yakalama köprüsü (`WinDivertNative` P/Invoke) çalışma zamanında
`WinDivert.dll` + `WinDivert64.sys`'i exe'nin yanında bekler. Bu ikili dosyalar
çekirdek paketlerinin (sing-box / xray / wintun) HİÇBİRİNDE bulunmaz; build'in
internete bağımlı kalmasın diye resmî dağıtım bu klasöre gömülüdür.

| Dosya | Boyut (zip) | SHA-256 |
|---|---|---|
| `WinDivert-2.2.2-A.zip` | 405.137 bayt | `63cb41763bb4b20f600b6de04e991a9c2be73279e317d4d82f237b150c5f3f15` |

* **Kaynak:** https://github.com/basil00/WinDivert/releases/tag/v2.2.2
  (asset `WinDivert-2.2.2-A.zip`, 2022-09-20)
* **Lisans:** LGPL-3.0 — lisans metni zip kökündeki `LICENSE`; build onu
  `WinDivert-LICENSE.txt` olarak exe yanına kopyalar.
* **Kullanılan parçalar:** `x64/WinDivert.dll`, `x64/WinDivert64.sys`
  (x86 build: `x86/WinDivert.dll`, `x86/WinDivert32.sys` + `x86/WinDivert64.sys`).
  zip'in geri kalanı (windivertctl, örnekler, header) build'de kullanılmaz.

## Build akışı (`AoGPN.csproj` → `PromoteWinDivertToOutputRoot`)

1. Bu zip önce kullanılır (çevrimdışı, deterministik — indirme yok).
2. Zip yoksa resmî GitHub release'inden indirilir (TLS 1.2 açıkça set edilir;
   PowerShell 5.1 aksi halde eski TLS ile teklif eder ve GitHub reddeder).
3. `_WinDivertPresent` (exe yanında WinDivert.dll + WinDivert64.sys zaten var)
   ise hiçbir şey yapılmaz. Kopyalama başarısız olursa build kırılmaz — sonraki
   build dener; köprü eksik dosyayı net mesajla raporlar.

## Sürücü (WinDivert64.sys) nasıl yüklenir?

WinDivert 2.2.2 (`dll/windivert.c` — `WinDivertDriverInstall`) ilk
`WinDivertOpen()` çağrısında sürücüyü **kendisi kurar**: SCM üzerinden
`WinDivert` adında geçici kernel servisi oluşturur, `WinDivert64.sys` yolunu
**WinDivert.dll'nin yanından** (DLL'nin kendi dizini) çözer ve servisi başlattıktan
sonra siler (transient — son handle kapanınca sürücü boşalır). Yönetici yetkisi
gerekir; GPN TUN yolu zaten yükseltmeyle çalıştığı için pratikte sağlanır.

Uygulama tarafındaki durum makinesi (`WinDivertHealthMonitor`) aynı kuralları
izler: dosya varlığı → cihaz probe (`\\.\WinDivert`) → yöneticiyse SCM kurulumu →
dashboard uyarısı.