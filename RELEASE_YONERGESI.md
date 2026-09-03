# Sürüm & Release Yönergesi — AoGPN

Bu belge, AoGPN için **sürümün tek bir yerden yönetilmesini**, **release
etiketinin sürümle eşleşmesini** ve her yayının **dil dosyaları** ile
**dashboard**'a eksiksiz yansıtılmasını düzenler. Yeni bir sürüm çıkarırken bu
sayfadaki adımları sırayla uygula.

- [1. Tek sürüm kaynağı](#1-tek-sürüm-kaynağı)
- [2. Numaralandırma şeması](#2-numaralandırma-seması)
- [3. Yeni sürüm çıkarma: adım adım](#3-yeni-sürüm-çıkarma-adım-adım)
- [4. CHANGELOG yapısı](#4-changelog-yapısı)
- [5. Dil dosyalarını güncelleme](#5-dil-dosyalarını-güncelleme)
- [6. Dashboard'ı güncelleme](#6-dashboardı-güncelleme)
- [7. Doğrulama & smoke testler](#7-doğrulama--smoke-testler)
- [8. Uçtan uca örnek](#8-uçtan-uca-örnek)

---

## 1. Tek sürüm kaynağı

Sürümün tek kaynağı **`AoGPN/Directory.Build.props`** içindeki `<Version>`
etiketidir:

```xml
<PropertyGroup>
    <Version>1.1.0</Version>
</PropertyGroup>
```

Bu değer derlenince assembly sürümü olur (`1.1.0.0`). Aşağıdaki her yer aynı
kaynaktan beslenir, ayrıca elle güncellenmesi **gerekmez**:

| Nerede görünür | Kaynak |
| --- | --- |
| Splash ekranı (`SplashWindow.xaml.cs`) | `$"V{Utils.GetVersionInfo()}"` |
| Dashboard About & Help sayfası (`#aboutVersion`) | `MainWindow.PushAppInfoAsync()` → `window.setAppInfo` → `Utils.GetVersionInfo()` |
| Pencere başlığı / sürüm damgası | `Utils.GetVersionInfo()` |
| Güncelleme kontrolü | `Utils.GetVersionInfo()` (assembly) |

> Yani sürümü yalnızca `Directory.Build.props` içinde değiştirirsin; splash ve
> About sayfası otomatik olarak doğru değeri gösterir.

---

## 2. Numaralandırma şeması

- **Assembly / güncelleme-kontrol sürümü:** AoGPN-yerli **`1.x.x`** şeması
  kullanılır (`Directory.Build.props`). Örn. şu anki sürüm: **1.1.0**.
- **CHANGELOG:** Beslenen değer aynı sürüm numarasıyla `[1.1.0]` başlığı olarak
  yazılır. Yayınlanmamış iç geliştirme işleri, `1.1.0` altında
  **"Development milestones folded into this release"** bölümünde `####`
  alt başlıkları olarak toplanır (`7.26.x` gibi miras alınan numaralar tek tek
  sürüm **değildir** — bunlar tek bir yayını oluşturan adımlardır).
- İki şema eşgüdümlüdür: bir release çıkarken hem assembly sürümü artar hem
  CHANGELOG'ta karşılık gelen başlık eklenir.

---

## 3. Yeni sürüm çıkarma: adım adım

1. **Assembly sürümünü artır** — `AoGPN/Directory.Build.props`
   `<Version>` değerini güncelle (ör. `1.1.0` → `1.2.0`).
2. **CHANGELOG'a giriş ekle** — en üste yeni `## [x.y.z]` başlığını yaz ve
   değişiklikleri `### Added` / `### Changed` / `### Fixed` / `### Technical`
   bölümlerine ayır. (Yapı detayı: [4. CHANGELOG yapısı](#4-changelog-yapısı).)
3. **Release etiketini sürümle eşle** — GitHub üzerinden yayın etiketi (tag),
   assembly sürümüyle **birebir** uyumlu olmalıdır (ör. `1.1.0`).
   Güncelleme kontrolü (`UpdateService`) assembly sürümünü, GitHub release
   etiketinden çözülen `SemanticVersion` ile karşılaştırır:
   - Etiket sürümden küçükse → program yanlışlıkla sürekli "güncelleme var"
     der.
   - Etiket sürümden büyükse → yeni yayını hiç görmez.
   Yani: **etiket = assembly sürümü** olmalı (örn. `1.1.0`). Mevcut etiket
   biçimi `v` öneksizdir (tek mevcut etiket: `7.24.4`), bu yüzden aynı şekilde
   `1.1.0` kullan.
4. **Yeni i18n anahtarı eklendiyse** → [5. Dil dosyaları](#5-dil-dosyalarını-güncelleme).
5. **Dashboard değiştiyse** → [6. Dashboard'ı güncelleme](#6-dashboardı-güncelleme).
6. **Doğrulamayı çalıştır** → [7. Doğrulama](#7-doğrulama--smoke-testler).

---

## 4. CHANGELOG yapısı

İstenen düzen (yeni en üstte):

```
## [1.1.0] — Kısa başlık

Giriş paragrafı (birkaç cümle; sürüm numarası, öne çıkanlar).

### Added
### Changed
### Fixed
### Technical

### Development milestones folded into this release

Bu sürümün geliştirme adımları, tekil sürüm numarası **için** alt başlık olarak:

#### [7.26.52] — ...
##### Added
##### Technical

...

## <önceki yayınlanmış sürüm>
```

Kurallar:
- Yeni sürüm her zaman en üste ve `## [` ile yazılır.
- Yayınlanmamış iç geliştirme adımları, en üst sürümün altında `#### [aynı seri]`
  ve `##### Added/Fixed/...` başlıklarıyla yuvalanır — **asla** ayrı `##`
  başlığı olmaz (aksi halde "yayınlanmış sürüm" gibi görünür).
- Alttaki gerçek yayınlanmış geçmiş (`7.25.x`, `7.24.4`, …) `##` seviyesinde
  kalır, dokunulmaz.
- Maddeler normal changelog düzeni gibi **yeni→eski** sıralanır.

---

## 5. Dil dosyalarını güncelleme

Dil dosyaları `AoGPN/Dil/` altında 9 yerelleştirmedir:

`en`, `tr`, `zh-Hans`, `zh-Hant`, `fa`, `fr`, `hu`, `id`, `ru`.

**Temel kurallar:**

1. **`en.json` referanstır (baseline).** `LocalizationConsistencyTests` tüm
   dilleri `en.json` anahtar setiyle karşılaştırır; eksik/ekstra anahtar veya
   `{placeholder}` uyumsuzluğu testi kırar.
2. Bir UI anahtarı eklersen onu **9 dosyanın tamamına** ve `Temalar/app.js`
   içindeki **gömülü fallback sözlüğe** ekle (harici dil dosyası yüklenemezse
   davranış düşmez).
3. Yer tutucular (örn. `{link}`, `{route}`, `{connect}`) çeviride korunmalı
   ve birebir aynı olmalı (test bunu doğrular; `%` gibi hiçbir karakter
   bozulmamalı).
4. Anahtar adını değiştirirsen tüm dilleri ve fallback sözlüğü birlikte
   güncelle.

**Hızlı kontrol (Node varken):**

```bash
cd AoGPN
node -e '
const fs=require("fs");
const langs=["en","tr","zh-Hans","zh-Hant","fa","fr","hu","id","ru"];
const b=JSON.parse(fs.readFileSync("Dil/en.json","utf8"));
for(const l of langs){
  const j=JSON.parse(fs.readFileSync("Dil/"+l+".json","utf8"));
  const miss=Object.keys(b).filter(k=>!(k in j));
  if(miss.length) console.log(l,"MISSING:",miss.join(", "));
}
console.log("en.json geçerli JSON ve 9 dil anahtarı tarandı.");
'
```

> Not: `en.json` referansında bulunmayan `theme.*` gibi anahtarlar (fon adı /
> tema adlandırması EN'de tema listesinden fallback alır) `LocalizationConsistencyTests`
> tarafından "extra key" olarak raporlanabilir — bu, yalnızca İngilizcede
> fallback kullanan tema/teknik etiketler için bilinçli bir durumdur.

---

## 6. Dashboard'ı güncelleme

Dashboard statik + çalışma zamanı dosyalarından oluşur:

- `AoGPN/vpn-gpn-dashboard.html` — görünümler (`<section id="view…">`, kenar
  çubuğu / mobil nav öğeleri).
- `AoGPN/Temalar/app.js` — mantık, i18n `t()`, **gömülü fallback sözlük** ve
  host köprüleri (`setAppInfo`, `applyLanguage`, …).
- `AoGPN/Temalar/skins/*.test.js` — `node --test` ile çalışan birim/integration
  testleri.

**Yeni bir görünüm eklerken:**

1. HTML'de `<section id="viewXyz">` + nav öğesi (`data-view`) ekle.
2. `app.js` içinde `showView()` içindeki **`real` görünüm listesine** görünüm
   adını ekle (`real = [..., 'settings', 'about'].includes(view)`), yoksa
   "coming soon" sayfasına düşer.
3. Görünüm başlığı anahtarlarını ekle: `view.X.title1 / title2 / sub`.
4. Yeni i18n anahtarlarını 9 dil + fallback dict'e ekle (bkz. bölüm 5).
5. Host'tan veri gerekiyorsa `MainWindow.xaml.cs` içinde bir push ile hizala
   (örn. `PushAppInfoAsync` → `window.setAppInfo`), spin'i `ExecuteScriptSafelyAsync`
   üzerinden koru.
6. `Temalar/skins/` altına veya mevcut test dosyasına bir test ekle.

---

## 7. Doğrulama & smoke testler

Yayın öncesi şu üçünü çalıştır:

```bash
# 1) Dashboard/skin JS testleri
cd AoGPN/Temalar/skins && npm test

# 2) C# derleme kontrolü (çıktı dosyası kilitliyse -t:Compile kullan)
cd AoGPN && dotnet build AoGPN/AoGPN.csproj -c Release -t:Compile

# 3) Yerelleştirme tutarlılık testi
cd AoGPN && dotnet test ServiceLib.Tests/ServiceLib.Tests.csproj \
  --filter "FullyQualifiedName~LocalizationConsistency"
```

**Smoke (uygulama açıkken):**

- Splash ekranında `V{assembly sürümü}` (ör. `V1.1.0`) yazdığını doğrula.
- Kenar çubuğundan **About & Help** → Program Info sekmesinde sürümün
  `V1.1.0` olduğunu gör (canlı push ile assembly'den gelir).
- Dil değiştirince About sayfası ve yeni eklenen görünümün Türkçe dahil 9 dilde
  doğru çevrildiğini doğrula.

---

## 8. Uçtan uca örnek (1.2.0 yayınlamak)

1. `AoGPN/Directory.Build.props`: `<Version>1.2.0</Version>`.
2. CHANGELOG en üste `## [1.2.0] — …` ve içerik bölümlerini yaz; yayınlanmamış
   geliştirme adımları varsa bu başlık altında `####` alt başlıklarına al.
3. GitHub'da veya CI'da release etiketini `1.2.0` yap (mevcut biçim `v` öneksiz;
   örn. `7.24.4`) — assembly sürümüyle birebir.
4. Dokunulan her yeni UI metni için 9 dil + fallback dict güncellendi.
5. Bölüm 7'deki üç testi koş, splash + About sayfasını smoke'la.