/* ==========================================================================
   split-wave3.js — app.js → features/settings.js + features/game-boost.js (Dalga 3)
   --------------------------------------------------------------------------
   Aynı mekanik (anchor doğrulamalı birebir kesim):
     • features/settings.js  — ayarlar FORM mantığı (options listeleri,
       fillSettingsOptions, setSettingsField, collectSettings, saveSettings,
       setSettingsSaveResult, pencere davranışı toggle'ları). window.applySettings
       KOORDİNATÖRDE kalır (bağlantı/GPN durumuna doğrudan dokunur).
     • features/game-boost.js — domain kural paneli + EXE sürükle-bırak + boost
       listener'ları. currentView'a aogpn.app.getCurrentView() ile erişir.

   Kullanım:  node split-wave3.js
   ========================================================================== */
'use strict';

const fs = require('fs');
const path = require('path');

const APP = path.join(__dirname, 'app.js');
let src = fs.readFileSync(APP, 'utf8');

function assertCount(text, anchor, expected) {
  const n = text.split(anchor).length - 1;
  if (n !== expected) {
    throw new Error('expected ' + expected + ' occurrence(s) of: ' + JSON.stringify(anchor.slice(0, 80)) + ' — got ' + n);
  }
}

function cut(startAnchor, endAnchorExclusive) {
  const s = src.indexOf(startAnchor);
  const e = src.indexOf(endAnchorExclusive);
  if (s < 0 || e < 0 || e <= s) throw new Error('cut anchors not found/ordered: ' + startAnchor.slice(0, 60));
  if (src.indexOf(startAnchor, s + 1) !== -1) throw new Error('start anchor not unique: ' + startAnchor.slice(0, 60));
  if (src.indexOf(endAnchorExclusive, e + 1) !== -1) throw new Error('end anchor not unique: ' + endAnchorExclusive.slice(0, 60));
  const block = src.slice(s, e);
  src = src.slice(0, s) + src.slice(e);
  return block.replace(/\s+$/, '');
}

function replaceAll(text, from, to, expected) {
  assertCount(text, from, expected);
  return text.split(from).join(to);
}

// ---------------------------------------------------------------------------
// 1) Cuts
// ---------------------------------------------------------------------------
// Settings: form core (part 1 — up to window.applySettings, which stays).
const settingsPart1 = cut(
  "  // ---------- Settings view ----------",
  "  window.applySettings = (data) => {");

// Settings: form core (part 2 — collectSettings … bindWindowBehavior calls).
const settingsPart2 = cut(
  "  function collectSettings() {",
  "  // These globals are the only JavaScript entry points used by the WPF backend.");

// Stale layout comment left over from the wave-1 theme.js extraction.
src = replaceAll(src,
  `  // ---------- Layout axis: 'status' (sidebar controls) ↔ 'compact' (hero strip) ----------
  // Independent from the color theme; persisted under its own key so switching
  // colors never resets the arrangement.
  // ---------- Shared session state (consumed by core modules) ----------`,
  `  // ---------- Shared session state (consumed by core modules) ----------`, 1);

// Game Boost: manual domain rules + drag & drop + boost listeners. The block
// ends right before the boot pre-fill (the tooltips block that used to sit
// between them moved to core/tooltips.js in wave 1).
const gameBoost = cut(
  "  // ---------- Manual domain/IP rules (Game Boost) ----------",
  "  // Pre-fill the settings selects/datalists so the form is usable standalone too.");

// ---------------------------------------------------------------------------
// 2) app.js re-wiring
// ---------------------------------------------------------------------------
// Aliases for the settings module (used by the coordinator's applySettings,
// the boot pre-fill and the C# host contract).
src = replaceAll(src,
  "  const { resetTelemetry, updateTelemetry, flashGpnTelemetry } = aogpn.telemetry;",
  `  const { resetTelemetry, updateTelemetry, flashGpnTelemetry } = aogpn.telemetry;
  const { fillSettingsOptions, setSettingsField, FALLBACK_OPTIONS } = aogpn.settings;`, 1);

// currentView lives in the coordinator — expose it to the game-boost module.
src = replaceAll(src,
  `    getLastIpState: () => _lastIpState,
    getLastTelemetry: () => _lastTelemetry,
    setLastTelemetry: (sample) => { _lastTelemetry = sample; }
  };`,
  `    getLastIpState: () => _lastIpState,
    getLastTelemetry: () => _lastTelemetry,
    setLastTelemetry: (sample) => { _lastTelemetry = sample; },
    getCurrentView: () => currentView
  };`, 1);

// ---------------------------------------------------------------------------
// 3) features/settings.js assembly
// ---------------------------------------------------------------------------
let settingsBody = settingsPart1 + '\n\n' + settingsPart2;
const settingsDeps = `  const postToHost = aogpn.bridge.postToHost;
  const refreshAllCustomSelects = aogpn.selects.refreshAllCustomSelects;
  const setEffectsTier = aogpn.theme.setEffectsTier;
`;
const settingsFooter = `  window.aogpn = window.aogpn || {};
  window.aogpn.settings = {
    fillSettingsOptions, setSettingsField, collectSettings, saveSettings,
    settingsState, FALLBACK_OPTIONS
  };
`;
const settingsHeader = `/* ==========================================================================
   features/settings.js — AoGPN dashboard feature module (Ayarlar formu)
   --------------------------------------------------------------------------
   Ayarlar formunun mantığı: seçenek listeleri (FALLBACK_OPTIONS + host
   güncellemeleri), alan doldurma/okuma, kaydetme akışı ve pencere davranışı
   toggle'ları. window.applySettings KOORDİNATÖRDE kalır — bağlantı/GPN
   durumuna doğrudan dokunur ve bu modülün fonksiyonlarını aogpn.settings
   üzerinden çağırır.
   ========================================================================== */
`;
fs.writeFileSync(path.join(__dirname, 'features', 'settings.js'),
  settingsHeader + '(() => {\n  \'use strict\';\n' + settingsDeps + settingsBody + '\n\n' + settingsFooter + '})();\n');

// ---------------------------------------------------------------------------
// 4) features/game-boost.js assembly
// ---------------------------------------------------------------------------
// currentView checks go through the coordinator (lazily).
let gbBody = replaceAll(gameBoost, "currentView !== 'boost'", "aogpn.app.getCurrentView() !== 'boost'", 4);
const gbDeps = `  const { $ } = aogpn.util;
  const postToHost = aogpn.bridge.postToHost;
  const t = aogpn.i18n.t;
`;
const gbHeader = `/* ==========================================================================
   features/game-boost.js — AoGPN dashboard feature module (Game Boost)
   --------------------------------------------------------------------------
   Manuel domain/IP kural paneli (beyaz/kara liste + WARP BSG API kısayolu) ve
   EXE sürükle-bırak akışı. Görünüm denetimi (yalnızca boost görünümünde aktif)
   koordinatördeki currentView'a aogpn.app.getCurrentView() ile bağlanır.
   ========================================================================== */
`;
fs.writeFileSync(path.join(__dirname, 'features', 'game-boost.js'),
  gbHeader + '(() => {\n  \'use strict\';\n' + gbDeps + gbBody + '\n})();\n');

// ---------------------------------------------------------------------------
// 5) Write the trimmed app.js
// ---------------------------------------------------------------------------
fs.writeFileSync(APP, src);
console.log('OK — features/settings.js: ' + settingsBody.split('\n').length + ' satır içerik; features/game-boost.js: ' + gbBody.split('\n').length + ' satır içerik; app.js: ' + src.split('\n').length + ' satır.');