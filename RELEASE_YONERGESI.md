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
- [8. Güncelleme hattı ön koşulu: depo görünürlüğü](#8-güncelleme-hattı-ön-koşulu-depo-görünürlüğü)
- [9. Uçtan uca örnek](#9-uçtan-uca-örnek)

---

## 1. Tek sürüm kaynağı

Sürümün tek kaynağı **git etiketidir** (`v<major>.<minor>.<patch>`):

```bash
git tag v1.1.1
```

`AoGPN/Directory.Build.props` içinde elle yazılmış bir `<Version>` **yoktur**;
derleme sürümü oradaki MSBuild hedefiyle etiketten türetilir:

| Durum | Damgalanan sürüm |
| --- | --- |
| Yayın hattı (`release.yml`) | Etiketten gelen `-p:Version=1.1.1` |
| Yerel / etiket dışı CI derlemesi | HEAD'den erişilebilen en yakın `v<sayı>` etiketi |
| Etiket yok · git yok · etiket biçimi bozuk | `0.0.1` (yedek) |

Bu değer derlenince assembly sürümü olur (`1.1.1.0`). Aşağıdaki her yer aynı
kaynaktan beslenir, ayrıca elle güncellenmesi **gerekmez**:

| Nerede görünür | Kaynak |
| --- | --- |
| Dashboard About & Help sayfası (`#aboutVersion`) | `MainWindow.PushAppInfoAsync()` → `window.setAppInfo` → `Utils.GetVersionInfo()` |
| Pencere başlığı / sürüm damgası | `Utils.GetVersionInfo()` |
| Güncelleme kontrolü | `Utils.GetVersionInfo()` (assembly) |

> Yani sürümü yalnızca **etiket** belirler; About sayfası otomatik olarak doğru
> değeri gösterir ve `Directory.Build.props` içinde elle güncellenecek bir
> sürüm satırı kalmamıştır. Yedek `0.0.1` bilerek sıfırdan farklıdır:
> `AppUpdateChecker` yerel sürümü `0.0.0` okuduğunda bunu "sürüm bilinmiyor"
kabul eder ve güncelleme **önermez** — yedek `0.0.0` olsaydı etiketsiz her
> derlemede güncelleme denetimi tümden susardı.

---

## 2. Numaralandırma şeması

- **Assembly / güncelleme-kontrol sürümü:** AoGPN-yerli **`1.x.x`** şeması
  kullanılır ve sürüm **git etiketinden** gelir. Etiket biçimi
  `v<major>.<minor>.<patch>` olmalıdır; `v` öneki isteğe bağlıdır (`1.1.1` de
  kabul edilir), ancak sonekli etiket (`v1.1.1-rc1`) **reddedilir** — çünkü
  `AssemblyVersion` sayısal olmak zorundadır. Örn. şu anki sürüm: **1.1.1**.
- **CHANGELOG:** Beslenen değer aynı sürüm numarasıyla `[1.1.1]` başlığı olarak
  yazılır. Yayınlanmamış iç geliştirme işleri, `1.1.1` altında
  **"Development milestones folded into this release"** bölümünde `####`
  alt başlıkları olarak toplanır (`7.26.x` gibi miras alınan numaralar tek tek
  sürüm **değildir** — bunlar tek bir yayını oluşturan adımlardır).
- İki şema eşgüdümlüdür: bir release çıkarken hem assembly sürümü artar hem
  CHANGELOG'ta karşılık gelen başlık eklenir.

---

## 3. Yeni sürüm çıkarma: adım adım

1. **Sürüm numarasını seç** — `Directory.Build.props` içinde güncellenecek bir
   değer YOK; numarayı yalnızca etiket belirler (ör. `v1.1.1` → `v1.2.0`).
   Yerel derlemenin yeni numarayı göstermesi için etiketi erkenden atabilirsin;
   atmazsan yerel derleme bir önceki etiketi bildirir.
2. **CHANGELOG'a giriş ekle** — en üste yeni `## [x.y.z]` başlığını yaz ve
   değişiklikleri `### Added` / `### Changed` / `### Fixed` / `### Technical`
   bölümlerine ayır. (Yapı detayı: [4. CHANGELOG yapısı](#4-changelog-yapısı).)
3. **Etiketi at ve yayınla** — sürüm ile etiket artık ayrı iki şey değil;
   etiket **sürümün kendisidir**:

   ```bash
git tag v1.1.1
git push origin v1.1.1
   ```

   `.github/workflows/release.yml` tetiklenir: Release derler,
   `AoGPN-windows-64.zip` üretir, sürümü etiketten `-p:Version=<etiket>` ile
   damgalar ve **aynı** etiketle GitHub Releases'e yayımlar. Etiket biçimi
   `v<major>.<minor>.<patch>` olmalıdır; sonekli bir etiket (`v1.1.1-beta`)
   yayın adımında reddedilir ve `releases/latest` ucu ön sürümleri zaten
   göstermez.

   Güncelleme denetimi (`AppUpdateChecker`) assembly sürümünü GitHub etiketiyle
   karşılaştırır: etiket assembly sürümünden küçükse program sürekli "güncelleme
   var" der; büyükse yeni yayını görmez. Etiketten damgalama sayesinde bu
   sapma yayınlanmış derlemelerde yapısal olarak imkânsızdır.
4. **Yeni i18n anahtarı eklendiyse** → [5. Dil dosyaları](#5-dil-dosyalarını-güncelleme).
5. **Dashboard değiştiyse** → [6. Dashboard'ı güncelleme](#6-dashboardı-güncelleme).
6. **Doğrulamayı çalıştır** → [7. Doğrulama](#7-doğrulama--smoke-testler).

---

## 4. CHANGELOG yapısı

İstenen düzen (yeni en üstte):

```
## [1.1.1] — Kısa başlık

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

Yayın öncesi şu dördünü çalıştır:

```bash
# 1) Dashboard/skin JS testleri
cd AoGPN/Temalar/skins && npm test

# 2) C# derleme kontrolü (çıktı dosyası kilitliyse -t:Compile kullan)
cd AoGPN && dotnet build AoGPN/AoGPN.csproj -c Release -t:Compile

# 3) Yerelleştirme tutarlılık testi
cd AoGPN && dotnet test ServiceLib.Tests/ServiceLib.Tests.csproj \
  --filter "FullyQualifiedName~LocalizationConsistency"

# 4) Yayın hattı sözleşmesi: varlık adları uygulamanın istediği adlarla aynı mı,
#    sürüm ön sürüm olarak işaretlenmiyor mu, paket kökünde AoGPN.Updater.exe
#    var mı, v* etiketini yalnızca tek hat mı yazıyor
cd AoGPN && dotnet test ServiceLib.Tests/ServiceLib.Tests.csproj \
  --filter "FullyQualifiedName~ReleasePipelineContract"

# 5) Güncelleme denetimi sözleşmesi: 404 beklenen bir sonuç (istisna değil),
#    "depo görünmüyor" ile "kararlı yayın yok" ayrı teşhis edilir, başarısız
#    denetim 6 saat önbelleğe alınmaz
cd AoGPN && dotnet test ServiceLib.Tests/ServiceLib.Tests.csproj \
  --filter "FullyQualifiedName~AppUpdateCheckerTests"
```

> 4. adım `release.yml`'i YAML olarak ayrıştırır; `.github/workflows/` altındaki
> bir değişiklik bu kurallardan birini bozarsa test kırılır. `ReleasePipelineContractTests`
> bu yüzden `test.yml`'in yol filtrelerine de eklenmiştir — sözleşmeyi koruyan
> test, sözleşme değiştiğinde çalışmazsa hiçbir işe yaramaz.

**Smoke (uygulama açıkken):**

- Kenar çubuğundan **About & Help** → Program Info sekmesinde sürümün
  `V1.1.1` olduğunu gör (canlı push ile assembly'den gelir).
- Dil değiştirince About sayfası ve yeni eklenen görünümün Türkçe dahil 9 dilde
  doğru çevrildiğini doğrula.

---

## 8. Güncelleme hattı ön koşulu: depo görünürlüğü

Uygulama içi güncelleme denetimi (`AppUpdateChecker`) ve paket indirme
(`AppUpdateInstaller`) **kimlik doğrulaması taşımayan** HTTPS istekleri yapar:

```
GET https://api.github.com/repos/<owner>/<repo>/releases/latest
GET <asset browser_download_url>
```

Bu yolun çalışması için depo **herkese açık olmak zorundadır**. GitHub, özel
(private) depolara yapılan anonim isteklere **403 değil 404** döndürür. Belirti
yalnızca bir `404 NotFound`tur ve "henüz kararlı yayın yok" sanılabilir;
görünürlük yanlışsa güncelleme hattı hiçbir sürümde çalışmaz.

Uygulama günlüğünde teşhis tek satırda ve süreç başına bir kez yazılır:

```
[AppUpdate] Güncelleme denetimi yapılamadı (RepositoryNotVisible). '<owner>/<repo>'
deposu anonim isteklere görünmüyor (404). GitHub özel depolara 403 değil 404 döner;
depo özel kaldıkça sürüm denetimi de paket indirme de çalışmaz. Çözüm: depoyu herkese
açık yapın ya da yayın paketlerini herkese açık bir adresten sunun. → <url>
```

Aynı 404 iki farklı anlama geldiği için ayrım liste ucuna düşülerek yapılır:

| `/releases/latest` | `/releases` | Teşhis | Anlamı |
| --- | --- | --- | --- |
| 404 | 404 | `RepositoryNotVisible` | Depo özel ya da silinmiş — **yapılandırma hatası** |
| 404 | 200 | `NoPublishedRelease` | Depo var, kararlı yayın yok — normal |
| 403/429 | — | `RateLimited` | Kimliksiz API limiti (60 istek/saat) |
| 5xx | — | `HttpError` | Sunucu hatası |

Sözleşmenin geri kalanı:

- **İstisna yok:** 404/403/5xx beklenen sonuçlardır; bunlar için istisna atılmaz.
  (Aksi halde Visual Studio her biri için "ilk şans istisnası" satırı yazar ve
  hata ayıklama çıktısı kullanılamaz hale gelir.)
- **Ön sürüme yükseltme yok:** otomatik denetim asla ön sürüm sunmaz; depoda
  yalnızca ön sürüm varsa bu `NoPublishedRelease` olarak bildirilir.
- **Başarısız denetim önbelleğe alınmaz:** başarı `MinCheckInterval` (6 saat)
  boyunca geçerlidir, başarısızlık yalnızca `FailureRetryInterval` (1 dk) fren
  uygular — geçici bir kesinti güncellemeleri saatlerce kapatmaz.
- **Tek kaynak:** depo adı yalnızca `Global.CoreUrls` içindeki
  `ECoreType.AoGPN` girdisinde tanımlıdır.

> Kapalı depo bilinçli bir tercihse güncelleme paketleri herkese açık bir
> adresten (ayrı bir yayın deposu, CDN ya da sürüm dosyası) sunulmalıdır:
> uygulamaya token gömmek, dağıtılan her kopyada gizli anahtar taşımak demektir.

---

## 9. Uçtan uca örnek (1.2.0 yayınlamak)

1. **Etiketi at:** `git tag v1.2.0 && git push origin v1.2.0`. Sürüm numarası
   başka hiçbir yerde yazılmaz — yayın hattı bu etiketi `-p:Version=1.2.0`
   olarak derlemeye verir.
2. CHANGELOG en üste `## [1.2.0] — …` ve içerik bölümlerini yaz; yayınlanmamış
   geliştirme adımları varsa bu başlık altında `####` alt başlıklarına al.
3. Yayının bittiğini doğrula: Actions'ta `release` işi `AoGPN-windows-64.zip`
   varlığıyla ve normal (ön sürüm OLMAYAN) bir release oluşturmuş olmalı; aksi
   hâlde `releases/latest` ucu onu göstermez ve güncelleme önerilmez. İmzalama
   işi aynı sürüme `.sig` dosyalarının yanında **`AoGPN-public-key.asc`** de
   yükler; böylece indirilen paket doğrulanabilir. Ortak anahtarın üretildiği tek
   yer `.github/actions/publish-public-key` bileşenidir (`release.yml` imzalama
   işi ve `pub-key.yml` yalnızca onu çağırır), yani aynı varlık adını yazan
   ikinci bir kod yolu yoktur.
4. Dokunulan her yeni UI metni için 9 dil + fallback dict güncellendi.
5. Bölüm 7'deki üç testi koş, About sayfasını smoke'la.
