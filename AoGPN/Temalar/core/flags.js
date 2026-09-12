/* ==========================================================================
   core/flags.js — AoGPN dashboard module (ülke bayrakları)
   --------------------------------------------------------------------------
   Windows/Chromium emoji bayraklarını ÇİZEMEZ (🇹🇷 gibi kodlar harf çiftine
   düşer: "TR"), bu yüzden bayraklar küçük gömülü SVG'ler olarak çizilir
   (4:3). Kodlar ISO 3166-1 alpha-2'dir (host ResolveNodeCountry'tan gelir).
   Bilinmeyen kodlar harf karosuna düşer — arayüz asla boş kalmaz.

   app.js'ten ÖNCE yüklenir (index.html <script> sırası ve
   skins/skin-sandbox.js MODULE_FILES listesi); nodes.js ve skin'ler
   aogpn.flags üzerinden kullanır.
   ========================================================================== */
(() => {
  'use strict';

  // ── çizim yardımcıları ────────────────────────────────────────────────
  function starPoints(cx, cy, outer, inner, spikes) {
    const pts = [];
    for (let i = 0; i < spikes * 2; i++) {
      const r = i % 2 === 0 ? outer : inner;
      const a = -Math.PI / 2 + (i * Math.PI) / spikes;
      pts.push((cx + r * Math.cos(a)).toFixed(2) + ',' + (cy + r * Math.sin(a)).toFixed(2));
    }
    return pts.join(' ');
  }
  const star = (cx, cy, r, spikes, fill) =>
    `<polygon points="${starPoints(cx, cy, r, r * 0.382, spikes || 5)}" fill="${fill || '#fff'}"/>`;
  const rect = (x, y, w, h, f) => `<rect x="${x}" y="${y}" width="${w}" height="${h}" fill="${f}"/>`;
  const circle = (cx, cy, r, f) => `<circle cx="${cx}" cy="${cy}" r="${r}" fill="${f}"/>`;
  // Hilal: beyaz daire + arka plan renginde kesme dairesi (TR/SG/MY/PK).
  const crescent = (cut, cx, cy, r) => circle(cx, cy, r, '#fff') + circle(cx + r * 0.3, cy, r * 0.82, cut);
  const svg = (inner) =>
    '<svg viewBox="0 0 4 3" preserveAspectRatio="xMidYMid slice" class="w-full h-full block">' + inner + '</svg>';
  // Yatay şeritler (üstten alta).
  const hstripes = (heights, fills) =>
    (function () {
      let out = '';
      let y = 0;
      for (let i = 0; i < heights.length; i++) {
        out += rect(0, y, 4, heights[i], fills[i]);
        y += heights[i];
      }
      return out;
    })();
  // Dikey şeritler (soldan sağa).
  const vstripes = (widths, fills) =>
    (function () {
      let out = '';
      let x = 0;
      for (let i = 0; i < widths.length; i++) {
        out += rect(x, 0, widths[i], 3, fills[i]);
        x += widths[i];
      }
      return out;
    })();

  // ── bayrak sözlüğü (ISO 3166-1 alpha-2 → gömülü SVG) ─────────────────
  const F = {
    // Türkiye
    TR: svg(rect(0, 0, 4, 3, '#E30A17') + crescent('#E30A17', 1.45, 1.5, 0.9) + star(2.15, 1.5, 0.34, 5, '#fff')),
    // Almanya
    DE: svg(hstripes([1, 1, 1], ['#000', '#DD0000', '#FFCE00'])),
    // Fransa
    FR: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#0055A4', '#fff', '#EF4135'])),
    // Birleşik Krallık
    GB: svg(rect(0, 0, 4, 3, '#012169')
      + '<path d="M0,0 4,3 M4,0 0,3" stroke="#fff" stroke-width="0.7"/>'
      + '<path d="M0,0 4,3 M4,0 0,3" stroke="#C8102E" stroke-width="0.4"/>'
      + '<path d="M2,0V3 M0,1.5H4" stroke="#fff" stroke-width="1.05"/>'
      + '<path d="M2,0V3 M0,1.5H4" stroke="#C8102E" stroke-width="0.58"/>'),
    // Hollanda
    NL: svg(hstripes([1, 1, 1], ['#AE1C28', '#fff', '#21468B'])),
    // Japonya
    JP: svg(rect(0, 0, 4, 3, '#fff') + circle(2, 1.5, 0.85, '#BC002D')),
    // Güney Kore
    KR: svg(rect(0, 0, 4, 3, '#fff')
      + '<path d="M0.75,1.5 a0.95,0.95 0 0 1 1.9,0 z" fill="#0047A0"/>'
      + '<path d="M2.65,1.5 a0.95,0.95 0 0 1 -1.9,0 z" fill="#CD2E3A"/>'
      + '<g fill="#000">' + rect(0.35, 0.28, 0.42, 0.11) + rect(0.35, 0.5, 0.42, 0.11) + rect(0.35, 0.72, 0.42, 0.11)
      + rect(3.23, 0.28, 0.42, 0.11) + rect(3.23, 0.5, 0.42, 0.11) + rect(3.23, 0.72, 0.42, 0.11)
      + rect(0.35, 2.17, 0.42, 0.11) + rect(0.35, 2.39, 0.42, 0.11) + rect(0.35, 2.61, 0.42, 0.11)
      + rect(3.23, 2.17, 0.42, 0.11) + rect(3.23, 2.39, 0.42, 0.11) + rect(3.23, 2.61, 0.42, 0.11) + '</g>'),
    // Singapur
    SG: svg(rect(0, 0, 4, 1.5, '#EF3340') + rect(0, 1.5, 4, 1.5, '#fff')
      + crescent('#EF3340', 1.25, 0.75, 0.6)
      + [0, 1, 2, 3, 4].map(k => {
        const a = -Math.PI / 2 + (k * 2 * Math.PI) / 5;
        return circle(2.3 + 0.52 * Math.cos(a), 0.75 + 0.52 * Math.sin(a), 0.1, '#fff');
      }).join('')),
    // Hong Kong
    HK: svg(rect(0, 0, 4, 3, '#DE2910')
      + [0, 1, 2, 3, 4].map(k => {
        const a = -Math.PI / 2 + (k * 2 * Math.PI) / 5;
        return circle(2 + 0.5 * Math.cos(a), 1.5 + 0.5 * Math.sin(a), 0.4, '#fff');
      }).join('')
      + circle(2, 1.5, 0.3, '#fff')),
    // Kanada
    CA: svg(rect(0, 0, 4, 3, '#fff') + rect(0, 0, 0.9, 3, '#D52B1E') + rect(3.1, 0, 0.9, 3, '#D52B1E')
      + '<polygon points="2,0.85 2.16,1.3 2.62,1.28 2.34,1.58 2.5,2.12 2,1.86 1.5,2.12 1.66,1.58 1.38,1.28 1.84,1.3" fill="#D52B1E"/>'),
    // Avustralya
    AU: svg(rect(0, 0, 4, 3, '#00247D')
      + '<path d="M0,0 1.6,1.2 M1.6,0 0,1.2" stroke="#fff" stroke-width="0.42"/>'
      + '<path d="M0,0 1.6,1.2 M1.6,0 0,1.2" stroke="#C8102E" stroke-width="0.22"/>'
      + '<path d="M0.8,0V1.2 M0,0.6H1.6" stroke="#fff" stroke-width="0.52"/>'
      + '<path d="M0.8,0V1.2 M0,0.6H1.6" stroke="#C8102E" stroke-width="0.28"/>'
      + star(2.25, 0.75, 0.3, 7, '#fff')
      + '<g fill="#fff">' + circle(2.6, 2.1, 0.08, '#fff') + circle(2.95, 1.62, 0.08, '#fff')
      + circle(3.32, 1.98, 0.08, '#fff') + circle(3.14, 2.55, 0.08, '#fff') + '</g>'),
    // Rusya
    RU: svg(hstripes([1, 1, 1], ['#fff', '#0039A6', '#D52B1E'])),
    // İran
    IR: svg(hstripes([1, 1, 1], ['#239F40', '#fff', '#DA0000'])),
    // Birleşik Arap Emirlikleri
    AE: svg(rect(0, 0, 1, 3, '#EF3340') + hstripes([1, 1, 1], ['#00732F', '#fff', '#000'])),
    // Brezilya
    BR: svg(rect(0, 0, 4, 3, '#009739')
      + '<polygon points="2,0.55 3.3,1.5 2,2.45 0.7,1.5" fill="#FEDD00"'
      + ' stroke="#fff" stroke-width="0.12"/>' + circle(2, 1.5, 0.62, '#012169')),
    // Hindistan
    IN: svg(hstripes([1, 1, 1], ['#FF9933', '#fff', '#138808'])
      + '<circle cx="2" cy="1.5" r="0.42" fill="none" stroke="#000080" stroke-width="0.16"/>'),
    // Çin
    CN: svg(rect(0, 0, 4, 3, '#DE2910')
      + star(1.0, 0.85, 0.55, 5, '#FFDE00')
      + star(2.0, 0.5, 0.18, 5, '#FFDE00') + star(2.32, 0.82, 0.18, 5, '#FFDE00')
      + star(2.32, 1.22, 0.18, 5, '#FFDE00') + star(2.0, 1.55, 0.18, 5, '#FFDE00')),
    // Tayvan
    TW: svg(rect(0, 0, 4, 3, '#FE0000') + rect(0, 0, 2, 1.5, '#000095')
      + '<g fill="#fff">' + circle(1, 0.75, 0.4, '#fff')
      + [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11].map(k =>
        '<rect x="0.955" y="0.06" width="0.09" height="0.52" transform="rotate(' + (k * 30) + ' 1 0.75)"/>').join('')
      + '</g>'),
    // İtalya
    IT: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#009246', '#fff', '#CE2B37'])),
    // İspanya
    ES: svg(hstripes([1, 1, 1], ['#AA151B', '#F1BF00', '#AA151B'])),
    // İsveç
    SE: svg(rect(0, 0, 4, 3, '#006AA7')
      + '<path d="M1.25,0V3 M0,1.5H4" stroke="#FECC00" stroke-width="0.55"/>'),
    // İsviçre
    CH: svg(rect(0, 0, 4, 3, '#DA291C') + rect(1.68, 0.95, 0.64, 1.1, '#fff') + rect(1.43, 1.2, 1.14, 0.6, '#fff')),
    // Polonya
    PL: svg(hstripes([1.5, 1.5], ['#fff', '#DC143C'])),
    // Çekya
    CZ: svg(rect(0, 0, 4, 1.5, '#fff') + rect(0, 1.5, 4, 1.5, '#D7141A')
      + '<polygon points="0,0 1.5,1.5 0,3" fill="#11457E"/>'),
    // Ukrayna
    UA: svg(hstripes([1.5, 1.5], ['#005BBB', '#FFD500'])),
    // Kazakistan
    KZ: svg(rect(0, 0, 4, 3, '#00AFCA') + circle(2, 1.5, 0.8, '#FED100')),
    // Vietnam
    VN: svg(rect(0, 0, 4, 3, '#DA251D') + star(2, 1.5, 0.85, 5, '#FFFF00')),
    // Tayland
    TH: svg(hstripes([0.5, 0.4, 1.2, 0.4, 0.5], ['#A51931', '#F4F5F8', '#2D2A4A', '#F4F5F8', '#A51931'])),
    // Endonezya
    ID: svg(hstripes([1.5, 1.5], ['#fff', '#CE1126'])),
    // Malezya
    MY: svg([0, 1, 2, 3, 4, 5, 6].map(i => rect(0, (i * 3) / 13, 4, 3 / 26, i % 2 === 0 ? '#CC0001' : '#fff')).join('')
      + rect(0, 0, 1.7, 21 / 13, '#010066')
      + crescent('#010066', 0.75, 0.6, 0.38) + star(1.22, 0.78, 0.2, 5, '#FFCC00')),
    // Filipinler
    PH: svg(rect(0, 0, 4, 1.5, '#0038A8') + rect(0, 1.5, 4, 1.5, '#CE1126')
      + '<polygon points="0,0 2.1,1.5 0,3" fill="#fff"/>'
      + circle(0.7, 1.5, 0.38, '#FCD116')
      + star(1.55, 0.55, 0.15, 5, '#FCD116') + star(1.55, 2.45, 0.15, 5, '#FCD116')),
    // Arjantin
    AR: svg(hstripes([1, 1, 1], ['#74ACDF', '#fff', '#74ACDF']) + circle(2, 1.5, 0.7, '#F6B40E')),
    // Şili
    CL: svg(rect(0, 0, 4, 1.5, '#fff') + rect(0, 1.5, 4, 1.5, '#D52B1E')
      + rect(0, 0, 1.2, 1.5, '#0039A6') + star(0.6, 0.75, 0.35, 5, '#fff')),
    // Kolombiya
    CO: svg(hstripes([1.5, 0.75, 0.75], ['#FCD116', '#003893', '#CE1126'])),
    // Meksika
    MX: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#006847', '#fff', '#CE1126'])),
    // Güney Afrika
    ZA: svg(rect(0, 0, 4, 3, '#fff')
      + '<path d="M0,-0.1 2.42,1.5 0,3.1 1.02,3.1 2.88,1.5 1.02,-0.1 Z" fill="#FFB612"/>'
      + '<path d="M0,0.28 2.16,1.5 0,2.72 0.72,2.72 2.42,1.5 0.72,0.28 Z" fill="#007A4D"/>'
      + '<polygon points="4,0 2.45,1.5 4,1.5" fill="#DE3831"/>'
      + '<polygon points="2.6,1.5 4,1.5 4,3 2.95,3" fill="#002395"/>'),
    // Nijerya
    NG: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#008751', '#fff', '#008751'])),
    // Mısır
    EG: svg(hstripes([1, 1, 1], ['#CE1126', '#fff', '#000'])),
    // Finlandiya
    FI: svg(rect(0, 0, 4, 3, '#fff') + '<path d="M1.5,0V3 M0,1.5H4" stroke="#002F6C" stroke-width="0.7"/>'),
    // Norveç
    NO: svg(rect(0, 0, 4, 3, '#EF2B2D')
      + '<path d="M1.35,0V3 M0,1.5H4" stroke="#fff" stroke-width="0.8"/>'
      + '<path d="M1.35,0V3 M0,1.5H4" stroke="#00205B" stroke-width="0.42"/>'),
    // Danimarka
    DK: svg(rect(0, 0, 4, 3, '#C8102E') + '<path d="M1.3,0V3 M0,1.5H4" stroke="#fff" stroke-width="0.8"/>'),
    // Avusturya
    AT: svg(hstripes([1, 1, 1], ['#ED2939', '#fff', '#ED2939'])),
    // Belçika
    BE: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#000', '#FDDA24', '#EF3340'])),
    // Portekiz
    PT: svg(rect(0, 0, 1.5, 3, '#046A38') + rect(1.5, 0, 2.5, 3, '#DA291C') + circle(1.62, 1.5, 0.55, '#FFE900')),
    // Yunanistan
    GR: svg([0, 1, 2, 3, 4].map(i => rect(0, (i * 3) / 9, 4, 3 / 18, i % 2 === 0 ? '#0D5EAF' : '#fff')).join('')
      + rect(0, 0, 1.6, 15 / 18, '#0D5EAF')
      + rect(0.62, 0, 0.36, 15 / 18, '#fff') + rect(0, 0.62, 1.6, 0.36, '#fff')),
    // İsrail
    IL: svg(rect(0, 0, 4, 3, '#fff')
      + rect(0, 0, 4, 0.55, '#0038B8') + rect(0, 2.45, 4, 0.55, '#0038B8')
      + star(2, 1.5, 0.5, 5, '#0038B8')),
    // Suudi Arabistan
    SA: svg(rect(0, 0, 4, 3, '#006C35') + rect(0, 1.15, 4, 0.7, '#fff')),
    // Yeni Zelanda
    NZ: svg(rect(0, 0, 4, 3, '#00247D')
      + '<path d="M0,0 1.6,1.2 M1.6,0 0,1.2" stroke="#fff" stroke-width="0.42"/>'
      + '<path d="M0,0 1.6,1.2 M1.6,0 0,1.2" stroke="#C8102E" stroke-width="0.22"/>'
      + '<path d="M0.8,0V1.2 M0,0.6H1.6" stroke="#fff" stroke-width="0.52"/>'
      + '<path d="M0.8,0V1.2 M0,0.6H1.6" stroke="#C8102E" stroke-width="0.28"/>'
      + '<g fill="#C8102E">' + circle(2.7, 2.05, 0.09, '#C8102E') + circle(3.05, 1.6, 0.09, '#C8102E')
      + circle(3.42, 1.96, 0.09, '#C8102E') + circle(3.24, 2.53, 0.09, '#C8102E') + '</g>'
      + star(2.3, 1.35, 0.2, 5, '#fff')),
    // Romanya
    RO: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#002B7F', '#FCD116', '#CE1126'])),
    // Bulgaristan
    BG: svg(hstripes([1, 1, 1], ['#fff', '#00966E', '#D62612'])),
    // Macaristan
    HU: svg(hstripes([1, 1, 1], ['#CE2939', '#fff', '#477050'])),
    // İrlanda
    IE: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#169B62', '#fff', '#FF883E'])),
    // İzlanda
    IS: svg(rect(0, 0, 4, 3, '#003897')
      + '<path d="M1.35,0V3 M0,1.5H4" stroke="#fff" stroke-width="0.9"/>'
      + '<path d="M1.35,0V3 M0,1.5H4" stroke="#D72828" stroke-width="0.5"/>'),
    // Litvanya
    LT: svg(hstripes([1, 1, 1], ['#FDB913', '#006A44', '#C1272D'])),
    // Letonya
    LV: svg(hstripes([1.2, 0.6, 1.2], ['#9E3039', '#fff', '#9E3039'])),
    // Estonya
    EE: svg(hstripes([1, 1, 1], ['#0072CE', '#000', '#fff'])),
    // Lüksemburg
    LU: svg(hstripes([1, 1, 1], ['#ED2939', '#fff', '#00A1DE'])),
    // Slovakya
    SK: svg(hstripes([1, 1, 1], ['#fff', '#0B4EA2', '#EE1C25'])),
    // Slovenya
    SI: svg(hstripes([1, 1, 1], ['#fff', '#005DA4', '#EE1C25'])),
    // Hırvatistan
    HR: svg(hstripes([1, 1, 1], ['#FF0000', '#fff', '#171796'])),
    // Kıbrıs
    CY: svg(rect(0, 0, 4, 3, '#fff') + circle(2, 1.5, 0.8, '#D57800')),
    // Malta
    MT: svg(rect(0, 0, 2, 3, '#fff') + rect(2, 0, 2, 3, '#CE1126')),
    // Sırbistan
    RS: svg(hstripes([1, 1, 1], ['#C6363C', '#0C4076', '#fff'])),
    // Bosna-Hersek
    BA: svg(hstripes([1, 1, 1], ['#002395', '#FECB00', '#CE1126'])),
    // Karadağ
    ME: svg(rect(0, 0, 4, 3, '#C4030D') + rect(0, 0, 4, 0.4, '#FFD700') + rect(0, 2.6, 4, 0.4, '#FFD700')),
    // Arnavutluk
    AL: svg(rect(0, 0, 4, 3, '#E41E20') + circle(2, 1.5, 0.5, '#000')),
    // Kuzey Makedonya
    MK: svg(rect(0, 0, 4, 3, '#D20000') + circle(2, 1.5, 0.75, '#FFE600')),
    // Moldova
    MD: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#0046E0', '#FFD200', '#CC0000'])),
    // Beyaz Rusya
    BY: svg(rect(0, 0, 4, 2, '#D22730') + rect(0, 2, 4, 1, '#007C30') + rect(0, 0, 0.6, 3, '#fff')),
    // Gürcistan
    GE: svg(rect(0, 0, 4, 3, '#fff')
      + '<path d="M1.75,0V3 M0,1.5H4" stroke="#FF0000" stroke-width="0.5"/>'),
    // Ermenistan
    AM: svg(hstripes([1, 1, 1], ['#D90012', '#0033A0', '#F2A800'])),
    // Azerbaycan
    AZ: svg(hstripes([1, 1, 1], ['#00B5E2', '#EF3340', '#509E2F'])),
    // Özbekistan
    UZ: svg(hstripes([1.2, 0.6, 1.2], ['#0099B5', '#fff', '#0097A2'])
      + rect(0, 1.1, 4, 0.1, '#CE1126') + rect(0, 1.8, 4, 0.1, '#CE1126')),
    // Kırgızistan
    KG: svg(rect(0, 0, 4, 3, '#E8112D') + circle(2, 1.5, 0.7, '#FECB00')),
    // Tacikistan
    TJ: svg(hstripes([1, 1, 1], ['#CC0000', '#fff', '#006600']) + circle(2, 1.5, 0.35, '#FFD700')),
    // Bangladeş
    BD: svg(rect(0, 0, 4, 3, '#006A4E') + circle(1.7, 1.5, 0.75, '#F42A41')),
    // Pakistan
    PK: svg(rect(0, 0, 4, 3, '#01411C') + rect(0, 0, 1, 3, '#fff')
      + crescent('#01411C', 2.55, 1.5, 0.7) + star(3.1, 1.5, 0.28, 5, '#fff')),
    // Myanmar
    MM: svg(hstripes([1, 1, 1], ['#FECB00', '#34B233', '#EA2839']) + star(2, 1.5, 0.4, 5, '#fff')),
    // Laos
    LA: svg(hstripes([1, 1, 1], ['#CE1126', '#002868', '#CE1126']) + circle(2, 1.5, 0.6, '#fff')),
    // Moğolistan
    MN: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#DA2032', '#0066B3', '#DA2032'])),
    // Venezuela
    VE: svg(hstripes([2, 0.5, 0.5], ['#FCD116', '#00247D', '#CE1126'])),
    // Peru
    PE: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#D91023', '#fff', '#D91023'])),
    // Uruguay
    UY: svg([0, 1, 2, 3, 4].map(i => rect(0, (i * 3) / 9, 4, 3 / 18, i % 2 === 0 ? '#fff' : '#0038A8')).join('')
      + circle(2, 1.5, 0.75, '#FCD116')),
    // Paraguay
    PY: svg(hstripes([1, 1, 1], ['#D52B1E', '#fff', '#0038A8'])),
    // Ekvador
    EC: svg(hstripes([2, 0.5, 0.5], ['#FFD100', '#034EA2', '#ED1C24'])),
    // Bolivya
    BO: svg(hstripes([1, 1, 1], ['#D52B1E', '#FFD100', '#007934'])),
    // Dominik Cumhuriyeti
    DO: svg(rect(0, 0, 2, 3, '#002D62') + rect(2, 0, 2, 3, '#CE1126')
      + '<path d="M1.8,0V3 M0,1.5H4" stroke="#fff" stroke-width="0.42"/>'),
    // Küba
    CU: svg(hstripes([0.6, 0.6, 0.6, 0.6, 0.6], ['#002A8F', '#fff', '#002A8F', '#fff', '#002A8F'])
      + '<polygon points="0,0 1.7,1.5 0,3" fill="#CE1126"/>' + star(0.65, 1.5, 0.4, 5, '#fff')),
    // Guatemala
    GT: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#4997D0', '#fff', '#4997D0'])),
    // Honduras
    HN: svg(vstripes([4 / 3, 4 / 3, 4 / 3], ['#18C3DF', '#fff', '#18C3DF'])),
    // Kosta Rika
    CR: svg(hstripes([0.45, 0.45, 1.2, 0.45, 0.45], ['#002B7F', '#fff', '#CE1126', '#fff', '#002B7F'])),
    // Panama
    PA: svg(rect(0, 0, 2, 1.5, '#fff') + rect(2, 0, 2, 1.5, '#DA121A')
      + rect(0, 1.5, 2, 1.5, '#DA121A') + rect(2, 1.5, 2, 1.5, '#fff')
      + star(1, 0.75, 0.3, 5, '#005293') + star(3, 2.25, 0.3, 5, '#DA121A')),
    // Jamaika
    JM: svg(rect(0, 0, 4, 3, '#009B3A')
      + '<path d="M0,0 4,3 M4,0 0,3" stroke="#FED100" stroke-width="0.55"/>'
      + '<path d="M0,0 4,3 M4,0 0,3" stroke="#000" stroke-width="0.25"/>'),
    // Trinidad ve Tobago
    TT: svg(rect(0, 0, 4, 3, '#CE1126')
      + '<path d="M0,0 4,3" stroke="#000" stroke-width="0.7"/>'
      + '<path d="M0,0 4,3" stroke="#fff" stroke-width="0.35"/>'),
    // Haiti
    HT: svg(rect(0, 0, 4, 1.5, '#00209F') + rect(0, 1.5, 4, 1.5, '#D21034') + rect(1.6, 1.1, 0.8, 0.8, '#fff')),
    // Lübnan
    LB: svg(hstripes([1, 1, 1], ['#EE161F', '#fff', '#EE161F'])
      + '<polygon points="2,1.15 2.3,1.85 1.7,1.85" fill="#00A651"/>'),
    // Suriye
    SY: svg(hstripes([1, 1, 1], ['#CE1126', '#fff', '#000'])
      + star(1.45, 1.5, 0.25, 5, '#007A3D') + star(2.55, 1.5, 0.25, 5, '#007A3D')),
    // Ürdün
    JO: svg(hstripes([1, 1, 1], ['#000', '#fff', '#007A3D'])
      + '<polygon points="0,0 1.5,1.5 0,3" fill="#CE1126"/>' + star(0.55, 1.5, 0.28, 5, '#fff')),
    // Kuveyt
    KW: svg(rect(0, 0, 3, 1, '#007A3D') + rect(0, 1, 3, 1, '#fff') + rect(0, 2, 3, 1, '#CE1126')
      + '<polygon points="0,0 1.1,1.5 0,3" fill="#000"/>'),
    // Katar
    QA: svg(rect(0, 0, 4, 3, '#fff')
      + '<polygon points="1.2,0 1.7,0.3 1.2,0.6 1.7,0.9 1.2,1.2 1.7,1.5 1.2,1.8 1.7,2.1 1.2,2.4 1.7,2.7 1.2,3 4,3 4,0" fill="#8A1538"/>'),
    // Bahreyn
    BH: svg(rect(0, 0, 4, 3, '#CE1126')
      + '<polygon points="0,0 1.7,0 1.1,0.3 1.7,0.6 1.1,0.9 1.7,1.2 1.1,1.5 1.7,1.8 1.1,2.1 1.7,2.4 1.1,2.7 1.7,3 0,3" fill="#fff"/>'),
    // Yemen
    YE: svg(hstripes([1, 1, 1], ['#CE1126', '#fff', '#000'])),
    // ABD
    US: svg([0, 1, 2, 3, 4, 5, 6].map(i => rect(0, (i * 3) / 13, 4, 3 / 26, i % 2 === 0 ? '#B22234' : '#fff')).join('')
      + rect(0, 0, 1.62, 21 / 13, '#3C3B6E')
      + '<g fill="#fff">' + [0, 1, 2].map(r =>
        [0, 1, 2, 3].map(c => circle(0.22 + c * 0.38, 0.28 + r * 0.5, 0.055, '#fff')).join('')).join('')
      + '</g>')
  };

  // ── dış yüzey ─────────────────────────────────────────────────────────
  function flagFor(code) {
    if (!code || typeof code !== 'string') return null;
    const c = code.trim().toUpperCase();
    return Object.prototype.hasOwnProperty.call(F, c) ? F[c] : null;
  }

  /// Bayrağı boyutlandırılmış, köşeleri yuvarlatılmış bir span içinde döndürür;
  /// bilinmeyen kod için null (çağıran kendi harf karosuna düşer).
  function flagMarkup(code, cls) {
    const svgContent = flagFor(code);
    if (!svgContent) return null;
    return '<span class="' + (cls || 'w-7 h-5') + ' rounded-[3px] overflow-hidden ring-1 ring-black/25 shrink-0 inline-flex">'
      + svgContent + '</span>';
  }

  /// Kart/karolar için tam karo: bilinen kod → bayrak, değilse harf karosu.
  function tileMarkup(code, fallbackText) {
    const flag = flagMarkup(code, 'w-7 h-5');
    if (flag) {
      return '<span class="w-10 h-10 rounded-lg shrink-0 flex items-center justify-center bg-white/5 border border-white/10">' + flag + '</span>';
    }
    return '<span class="w-10 h-10 rounded-lg bg-gradient-to-br from-[var(--violet-30)] to-[var(--cyan-20)] flex items-center justify-center font-display font-bold text-sm text-cyan-300 shrink-0 shadow-[0_0_10px_var(--violet-30)]">' + (fallbackText || '?') + '</span>';
  }

  window.aogpn = window.aogpn || {};
  window.aogpn.flags = { flagFor, flagMarkup, tileMarkup };
})();