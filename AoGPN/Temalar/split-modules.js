/* ==========================================================================
   split-modules.js — app.js'i modüler dosyalara bölme aracı (Dalga 1)
   --------------------------------------------------------------------------
   app.js'deki bölümleri BİREBİR (verbatim) kesip Temalar/core + Temalar/features
   altındaki modül dosyalarına taşır ve bağımlılıkları (postToHost, t, escHtml,
   skinNotify → aogpn.* ad alanı) yeniden bağlar. Her anchor'ın TEK olduğunu
   doğrular; bir anchor iki kez eşleşirse veya hiç eşleşmezse betik durur.

   Kullanım:  node split-modules.js
   Girdi:     app.js (mevcut)
   Çıktı:     core/*.js + features/*.js (yeni), app.js (kesilmiş + kablolanmış)
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

// Cut [startAnchor, endAnchorExclusive); both must be unique.
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

// Cut [startAnchor, endAnchor] inclusive; both unique.
function cutInclusive(startAnchor, endAnchor) {
  const s = src.indexOf(startAnchor);
  const e = src.indexOf(endAnchor);
  if (s < 0 || e < 0 || e < s) throw new Error('cutInclusive anchors not found/ordered: ' + startAnchor.slice(0, 60));
  if (src.indexOf(startAnchor, s + 1) !== -1) throw new Error('start anchor not unique: ' + startAnchor.slice(0, 60));
  if (src.indexOf(endAnchor, e + 1) !== -1) throw new Error('end anchor not unique: ' + endAnchor.slice(0, 60));
  const block = src.slice(s, e + endAnchor.length);
  src = src.slice(0, s) + src.slice(e + endAnchor.length);
  return block.replace(/\s+$/, '');
}

// Remove one whole line (unique substring on that line) including its newline.
function cutLine(anchor) {
  const i = src.indexOf(anchor);
  if (i < 0) throw new Error('cutLine anchor not found: ' + anchor.slice(0, 60));
  if (src.indexOf(anchor, i + 1) !== -1) throw new Error('cutLine anchor not unique: ' + anchor.slice(0, 60));
  const lineStart = src.lastIndexOf('\n', i) + 1;
  const lineEnd = src.indexOf('\n', i);
  const end = lineEnd < 0 ? src.length : lineEnd + 1;
  src = src.slice(0, lineStart) + src.slice(end);
}

// Insert text immediately after a unique anchor line (keeps the anchor line).
function insertAfter(anchor, text) {
  const i = src.indexOf(anchor);
  if (i < 0) throw new Error('insert anchor not found: ' + anchor.slice(0, 60));
  if (src.indexOf(anchor, i + 1) !== -1) throw new Error('insert anchor not unique: ' + anchor.slice(0, 60));
  const end = i + anchor.length;
  src = src.slice(0, end) + '\n' + text + src.slice(end);
}

// Insert text immediately before a unique anchor line.
function insertBefore(anchor, text) {
  const i = src.indexOf(anchor);
  if (i < 0) throw new Error('insert anchor not found: ' + anchor.slice(0, 60));
  if (src.indexOf(anchor, i + 1) !== -1) throw new Error('insert anchor not unique: ' + anchor.slice(0, 60));
  src = src.slice(0, i) + text + src.slice(i);
}

// Replace text; assert exactly `expected` occurrences.
function replaceAll(text, from, to, expected) {
  assertCount(text, from, expected);
  return text.split(from).join(to);
}

// ---------------------------------------------------------------------------
// Extraction order (top-down; every anchor must be unique in app.js)
// ---------------------------------------------------------------------------
const order = [
  ['dollar', 'cutLine', "  const $ = (id) => document.getElementById(id);"],
  ['bridge', 'cut', "  function hasWebViewBridge() {",
    "  const btnIcon = $('btnIcon'), btnText = $('btnText'), btnSub = $('btnSub'),"],
  ['dom', 'cut', "  const btnIcon = $('btnIcon'), btnText = $('btnText'), btnSub = $('btnSub'),",
    "        connectionStatusLine = $('connectionStatusLine');"],
  ['domEndLine', 'cutLine', "        connectionStatusLine = $('connectionStatusLine');"],
  ['reduceEffects', 'cutLine', "  let reduceEffects = false;"],
  ['releaseNotes', 'cut', "  // The raw object is also captured so standalone skins can read it through the",
    "  // ---------- Gaming theme engine ----------"],
  ['themeEngine', 'cut', "  // ---------- Gaming theme engine ----------",
    "  window.applyTheme = applyTheme;"],
  ['themeEngineEnd', 'cutInclusive', "  window.applyTheme = applyTheme;",
    "  window.applyTheme = applyTheme;"],
  ['i18n', 'cut', "  // ---------- i18n (Dil folder) ----------",
    "  // Generic native <select> → themed dropdown upgrade."],
  ['selects', 'cut', "  // Generic native <select> → themed dropdown upgrade.",
    "  // Compact theme popover in the title bar."],
  ['strayComment', 'cutLine', "  // Compact theme popover in the title bar."],
  ['layoutDeck', 'cut', "  const LAYOUT_STORAGE_KEY = 'aogpn.layout';",
    "  function collectSettings() {"],
  ['effects', 'cut', "  // ---------- Reduce-effects (motion budget) ----------",
    "  if (!window.__aogpnPerfDisabled && hasWebViewBridge()) startPerformanceProbe();"],
  ['initReduceEffectsCall', 'cutLine', "  initReduceEffects();"],
  ['windowControls', 'cut', "  $('minimizeButton').addEventListener('click', () => {",
    "  const topNodeSelectEl = $('topNodeSelect');"],
  ['tooltips', 'cut', "  // ---------- Custom tooltips ----------",
    "  document.querySelectorAll('[title]').forEach(adoptTooltipEl);"],
  ['tooltipsEnd', 'cutInclusive', "  document.querySelectorAll('[title]').forEach(adoptTooltipEl);",
    "  document.querySelectorAll('[title]').forEach(adoptTooltipEl);"],
  ['effectsFreeze', 'cut', "  // ---------- Effects freeze while the app is not visible ----------",
    "  if (skinFrame) skinFrame.addEventListener('load', () => { if (_effectsHidden) _applyEffectsHidden(); });"],
  ['effectsFreezeEnd', 'cutInclusive', "  if (skinFrame) skinFrame.addEventListener('load', () => { if (_effectsHidden) _applyEffectsHidden(); });",
    "  if (skinFrame) skinFrame.addEventListener('load', () => { if (_effectsHidden) _applyEffectsHidden(); });"]
];

const blocks = {};
src = fs.readFileSync(APP, 'utf8');
for (const step of order) {
  const [name, kind, a, b] = step;
  if (kind === 'cutLine') {
    cutLine(a);
    blocks[name] = null;
  } else if (kind === 'cut') {
    blocks[name] = cut(a, b);
  } else {
    blocks[name] = cutInclusive(a, b);
  }
}

// cut()/cutInclusive() end anchors that ARE the block's final line: append them
// back so the moved code stays byte-identical to the original.
blocks.dom += "\n        connectionStatusLine = $('connectionStatusLine');";
blocks.themeEngine += "\n  window.applyTheme = applyTheme;";
blocks.tooltips += "\n  document.querySelectorAll('[title]').forEach(adoptTooltipEl);";
blocks.effectsFreeze += "\n  if (skinFrame) skinFrame.addEventListener('load', () => { if (_effectsHidden) _applyEffectsHidden(); });";

// ---------------------------------------------------------------------------
// app.js inserts
// ---------------------------------------------------------------------------
insertAfter("  const body = document.body;", `  // ---- Core module bindings (loaded before app.js via <script> tags) ----
  const { $ } = aogpn.util;
  const { escHtml } = aogpn.util;
  const { postToHost, hasWebViewBridge } = aogpn.bridge;
  const { btnIcon, btnText, btnSub, statusDot, statusLabel, nodeName, nodeAddr,
          ipDisplay, ipCountry, ipVerifyText, ipVerifyDetail, ipVerifyCard,
          ipLeakBanner, ipLeakText, ipOkBanner, ipOkText, ipOkTunnelIp, ipOkIspIp,
          pingVal, pingSub, lossVal, lossSub, downVal, upVal,
          downGauge, upGauge, systemProxyToggleBtn, systemProxyDot,
          systemProxyLabel, systemProxyAddress, systemProxyModeSelect,
          proxyTestBtn, proxyTestIcon, proxyTestLabel,
          quickProtocolSelect, quickAutoReconnect, protocolHint,
          routeHint, transportHint, connectionStatusLine } = aogpn.dom;
  const { t, resolveKey, applyTexts, LANGUAGES, langName, applyLanguage, countryDisplayName } = aogpn.i18n;
  const { upgradeSelect, refreshAllCustomSelects } = aogpn.selects;
  const { THEMES, THEME_STORAGE_KEY, LAYOUT_STORAGE_KEY, applyTheme, applyLayout,
          renderThemeDeck, renderTopThemePopover, loadThemesFromDisk,
          initCanvasEffects, playThemeSound, fireConfetti, getCurrentThemeId } = aogpn.theme;
  const { loadReleaseNotesFromDisk } = aogpn.releaseNotes;
`);

// Shared session state that used to live inside the i18n block but is owned by
// app.js (telemetry / availability / GPN connection info all write it).
insertBefore("  function collectSettings() {", `  // ---------- Shared session state (consumed by core modules) ----------
  let _lastIpState = null;
  let _lastIpMeasuredAt = null; // son ölçümün ISO zaman damgası (host measuredAt)
  let _lastTelemetry = null;
  let _activeGpnServer = ''; // "sonra" ölçümünün yapıldığı seçili/aktif GPN düğümü (via etiketi)
  let _activeGpnMode = '';  // aktif GPN bağlantı modu ('WireGuard' | 'V2rayTCP' | '') — WARP uyarısı buna göre verilir

`);

insertBefore("  // Keep legacy aliases (used by the old inline fragment and debug consoles).",
  "  if (skinFrame) skinFrame.addEventListener('load', () => { if (aogpn.theme.isEffectsHidden()) aogpn.theme.applyEffectsHiddenNow(); });\n\n");

insertBefore("  // Load the registry and apply the persisted skin.", `  // ---- App services consumed by the core modules (i18n re-render hooks) ----
  window.aogpn = window.aogpn || {};
  window.aogpn.app = {
    applyMode, renderTopNodeSelect, updateTransportLock, applyProtocolPreference,
    applyRealIpState, updateTelemetry, renderDashboardBoostCards, updateStatusLine,
    getProtocolPreference: () => protocolPreference,
    getLastIpState: () => _lastIpState,
    getLastTelemetry: () => _lastTelemetry
  };

`);

// ---------------------------------------------------------------------------
// app.js text replaces
// ---------------------------------------------------------------------------
// skinNotify() → events bus (the definition itself is replaced below).
src = replaceAll(src, 'skinNotify();', "aogpn.events.emit('skin-changed');", 9);

src = replaceAll(src,
  `  function skinNotify() {
    _skinSubscribers.forEach(cb => { try { cb(); } catch (e) { /* ignore */ } });
  }`,
  `  // Core modules emit 'skin-changed' through the aogpn.events bus; forward it to
  // the skin subscribers exactly like the old direct skinNotify() calls did.
  aogpn.events.on('skin-changed', () => {
    _skinSubscribers.forEach(cb => { try { cb(); } catch (e) { /* ignore */ } });
  });`,
  1);

src = replaceAll(src,
  "    get effectsTier() { return effectsTier; },",
  "    get effectsTier() { return aogpn.theme.getEffectsTier(); },", 1);
src = replaceAll(src,
  "    get frozen() { return _effectsHidden; },",
  "    get frozen() { return aogpn.theme.isEffectsHidden(); },", 1);
src = replaceAll(src,
  "    get language() { return _currentLang; },",
  "    get language() { return aogpn.i18n.getLang(); },", 1);
src = replaceAll(src,
  "    get appInfo() { return _appInfo || null; },",
  "    get appInfo() { return aogpn.releaseNotes.getAppInfo(); },", 1);

src = replaceAll(src,
  `    var matrixState = _matrixFramePending ? 'running' : 'idle';
    var confettiN = _confettiParticles.length;`,
  `    var fx = aogpn.theme.getPerfState();
    var matrixState = fx.matrixFramePending ? 'running' : 'idle';
    var confettiN = fx.confettiN;`, 1);

src = replaceAll(src,
  `    line += '<br><b style="color:#8A94A6">Theme:</b> ' + (_currentThemeId || '?')
      + ' &nbsp; <b style="color:#8A94A6">FX:</b> ' + (effectsTier || 'full')
      + ' &nbsp; <b style="color:#8A94A6">OS-motion:</b> ' + (_osReducedMotion ? '<span style="color:#F59E0B">yes</span>' : 'no') + ')' ;`,
  `    line += '<br><b style="color:#8A94A6">Theme:</b> ' + (aogpn.theme.getCurrentThemeId() || '?')
      + ' &nbsp; <b style="color:#8A94A6">FX:</b> ' + (aogpn.theme.getEffectsTier() || 'full')
      + ' &nbsp; <b style="color:#8A94A6">OS-motion:</b> ' + (fx.osReducedMotion ? '<span style="color:#F59E0B">yes</span>' : 'no') + ')' ;`, 1);

src = replaceAll(src,
  "        visibleView: currentView, theme: _currentThemeId || ''",
  "        visibleView: currentView, theme: aogpn.theme.getCurrentThemeId() || ''", 1);

src = replaceAll(src,
  "      playThemeSound(_currentThemeId); if (typeof fireConfetti === 'function') fireConfetti();",
  "      playThemeSound(getCurrentThemeId()); if (typeof fireConfetti === 'function') fireConfetti();", 1);

// Header: replace the stale section map (everything before the IIFE opener).
{
  const iife = src.indexOf("(() => {");
  if (iife < 0) throw new Error('IIFE opener not found');
  src = `/* ==========================================================================
   AoGPN dashboard — application script (koordinatör)
   --------------------------------------------------------------------------
   Bu dosya artık uygulamanın TEK mantık deposu değil: başlatma akışını, host
   köprüsünün (window.*) kayıtlarını ve görünüm/özellik kablolamasını yürüten
   koordinatördür. Özellik mantığı modüllere taşındı — Temalar/core + Temalar/
   features klasörleri, index.html'deki <script> sırasıyla yüklenir:

   core/events.js              olay veriyolu (skin-changed vb. çapraz bildirim)
   core/util.js                $, escHtml gibi ortak yardımcılar
   core/dom.js                 dashboard element referansları
   core/bridge.js              WebView2 host köprüsü (postToHost)
   core/i18n.js                t(), dil yükleme, dil popover'ı
   core/selects.js             native <select> → temalı açılır menü yükseltmesi
   core/theme.js               tema motoru, canvas efektleri, ses/konfeti,
                               efekt katmanı (tier/reduce/hidden), layout + tema
                               destesi, popover
   core/tooltips.js            özel tooltip motoru + MutationObserver
   features/window-controls.js küçült/büyüt/kapat + başlık çubuğu sürükleme
   features/release-notes.js   Sürüm Notları + setAppInfo

   Geriye kalan bölümler (bağlantı durumu, telemetri, düğümler, görünümler,
   GPN sunucu/panel, ayarlar, skin motoru) kademeli taşıma tamamlanana dek
   burada kalır.
   ========================================================================== */
` + src.slice(iife);
}

// ---------------------------------------------------------------------------
// Module files
// ---------------------------------------------------------------------------
const CORE = path.join(__dirname, 'core');
const FEAT = path.join(__dirname, 'features');
fs.mkdirSync(CORE, { recursive: true });
fs.mkdirSync(FEAT, { recursive: true });

function moduleFile(header, depLines, body, footer) {
  return header + '\n(() => {\n  \'use strict\';\n' + depLines + body + '\n\n' + footer + '})();\n';
}

const headerCore = (name, desc) => '/* ==========================================================================\n'
  + '   ' + name + ' — AoGPN dashboard module (' + desc + ')\n'
  + '   --------------------------------------------------------------------------\n'
  + '   app.js ile aynı küresel pencere kapsamında, ancak app.js\'den ÖNCE yüklenir\n'
  + '   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).\n'
  + '   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel\n'
  + '   takma adlarını alır. ========================================================================== */';

// --- core/events.js — olay veriyolu ---
{
  const file = `/* ==========================================================================
   core/events.js — AoGPN dashboard core module (olay veriyolu)
   --------------------------------------------------------------------------
   Modüller birbirini doğrudan çağırmaz; 'skin-changed' gibi çapraz bildirimleri
   bu veriyolu üzerinden yayınlar. app.js'nin skin motoru olaya abone olur ve
   skinBridge.subscribe abonelerini besler (eski doğrudan skinNotify() çağrıları).
   ========================================================================== */
(() => {
  'use strict';
  const _subs = {};

  function on(event, cb) {
    (_subs[event] = _subs[event] || []).push(cb);
  }

  function emit(event, ...args) {
    (_subs[event] || []).forEach(cb => { try { cb(...args); } catch (e) { /* ignore */ } });
  }

  window.aogpn = window.aogpn || {};
  window.aogpn.events = { on, emit };
})();
`;
  fs.writeFileSync(path.join(CORE, 'events.js'), file);
}

// --- core/util.js — ortak yardımcılar ---
{
  const file = `/* ==========================================================================
   core/util.js — AoGPN dashboard core module (ortak yardımcılar)
   --------------------------------------------------------------------------
   Tüm modüllerin paylaştığı saf fonksiyonlar. app.js aynı fonksiyonları kendi
   kapsamına takma ad olarak alır (const { $, escHtml } = aogpn.util).
   ========================================================================== */
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);

  const escHtml = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  window.aogpn = window.aogpn || {};
  window.aogpn.util = { $, escHtml };
})();
`;
  fs.writeFileSync(path.join(CORE, 'util.js'), file);
}

// --- core/dom.js ---
{
  const body = blocks.dom;
  const footer = `  window.aogpn = window.aogpn || {};
  window.aogpn.dom = {
    btnIcon, btnText, btnSub, statusDot, statusLabel, nodeName, nodeAddr,
    ipDisplay, ipCountry, ipVerifyText, ipVerifyDetail, ipVerifyCard,
    ipLeakBanner, ipLeakText, ipOkBanner, ipOkText, ipOkTunnelIp, ipOkIspIp,
    pingVal, pingSub, lossVal, lossSub, downVal, upVal,
    downGauge, upGauge, systemProxyToggleBtn, systemProxyDot,
    systemProxyLabel, systemProxyAddress, systemProxyModeSelect,
    proxyTestBtn, proxyTestIcon, proxyTestLabel,
    quickProtocolSelect, quickAutoReconnect, protocolHint,
    routeHint, transportHint, connectionStatusLine
  };
`;
  fs.writeFileSync(path.join(CORE, 'dom.js'), moduleFile(headerCore('core/dom.js', 'dashboard element referansları'), '  const $ = (id) => document.getElementById(id);\n', body, footer));
}

// --- core/bridge.js ---
{
  const body = blocks.bridge;
  const footer = `  window.aogpn = window.aogpn || {};
  window.aogpn.bridge = { hasWebViewBridge, postToHost };
`;
  fs.writeFileSync(path.join(CORE, 'bridge.js'), moduleFile(headerCore('core/bridge.js', 'WebView2 host köprüsü'), '', body, footer));
}

// --- core/i18n.js ---
{
  let body = blocks.i18n;
  // The cross-cutting session state stays in app.js — remove it from the block.
  body = replaceAll(body,
    `  let _lastIpState = null;
  let _lastIpMeasuredAt = null; // son ölçümün ISO zaman damgası (host measuredAt)
  let _lastTelemetry = null;
  let _activeGpnServer = ''; // "sonra" ölçümünün yapıldığı seçili/aktif GPN düğümü (via etiketi)
  let _activeGpnMode = '';  // aktif GPN bağlantı modu ('WireGuard' | 'V2rayTCP' | '') — WARP uyarısı buna göre verilir
`,
    '', 1);
  // skinNotify → events bus (loadLanguage tail).
  body = replaceAll(body, `    skinNotify();
    return _dict;`, `    aogpn.events.emit('skin-changed');
    return _dict;`, 1);
  // refreshDynamicTexts: app.js functions are reached lazily through aogpn.app.
  body = replaceAll(body,
    `  function refreshDynamicTexts() {
    if (typeof refreshConnectLabels === 'function') refreshConnectLabels();
    if (typeof applyMode === 'function') applyMode();
    if (typeof renderTopNodeSelect === 'function') renderTopNodeSelect();
    if (typeof updateTransportLock === 'function') updateTransportLock();
    if (typeof applyProtocolPreference === 'function') applyProtocolPreference(protocolPreference);
    if (_lastIpState) applyRealIpState(_lastIpState);
    if (_lastTelemetry) updateTelemetry(_lastTelemetry[0], _lastTelemetry[1], _lastTelemetry[2], _lastTelemetry[3]);
    if (typeof renderDashboardBoostCards === 'function') renderDashboardBoostCards();
  }`,
    `  function refreshDynamicTexts() {
    if (typeof refreshConnectLabels === 'function') refreshConnectLabels();
    const app = aogpn.app;
    if (app && typeof app.applyMode === 'function') app.applyMode();
    if (app && typeof app.renderTopNodeSelect === 'function') app.renderTopNodeSelect();
    if (app && typeof app.updateTransportLock === 'function') app.updateTransportLock();
    if (app && typeof app.applyProtocolPreference === 'function') app.applyProtocolPreference(app.getProtocolPreference());
    const ipState = app && app.getLastIpState();
    if (ipState && typeof app.applyRealIpState === 'function') app.applyRealIpState(ipState);
    const lastTel = app && app.getLastTelemetry();
    if (lastTel && typeof app.updateTelemetry === 'function') app.updateTelemetry(lastTel[0], lastTel[1], lastTel[2], lastTel[3]);
    if (app && typeof app.renderDashboardBoostCards === 'function') app.renderDashboardBoostCards();
  }`,
    1);
  // refreshConnectLabels: the status line lives in app.js.
  body = replaceAll(body,
    "    if (typeof updateStatusLine === 'function') updateStatusLine();",
    "    if (aogpn.app && typeof aogpn.app.updateStatusLine === 'function') aogpn.app.updateStatusLine();", 1);
  // window.applyLanguage wrapper: theme popover re-render goes through aogpn.theme.
  body = replaceAll(body,
    "  window.applyLanguage = function(lang) {",
    "  const applyLanguage = function(lang) {", 1);
  body = replaceAll(body,
    `    if (typeof renderTopThemePopover === 'function') renderTopThemePopover();
  };`,
    `    if (aogpn.theme && typeof aogpn.theme.renderTopThemePopover === 'function') aogpn.theme.renderTopThemePopover();
  };
  window.applyLanguage = applyLanguage;`, 1);
  const depLines = `  const postToHost = aogpn.bridge.postToHost;
  const escHtml = aogpn.util.escHtml;
  const { statusLabel, btnText, btnSub } = aogpn.dom;
`;
  const footer = `  window.aogpn = window.aogpn || {};
  window.aogpn.i18n = {
    resolveKey, t, applyTexts, loadLanguage, refreshDynamicTexts, refreshConnectLabels,
    LANGUAGES, langName, countryDisplayName, renderLangPopover, applyLanguage,
    getLang: () => _currentLang,
    getDict: () => _dict
  };
`;
  fs.writeFileSync(path.join(CORE, 'i18n.js'), moduleFile(headerCore('core/i18n.js', 'çeviri + dil yönetimi'), depLines, body, footer));
}

// --- core/selects.js ---
{
  const body = blocks.selects;
  const footer = `  window.aogpn = window.aogpn || {};
  window.aogpn.selects = { upgradeSelect, refreshCustomSelect, refreshAllCustomSelects };
`;
  fs.writeFileSync(path.join(CORE, 'selects.js'), moduleFile(headerCore('core/selects.js', 'temalı <select> yükseltmesi'), '', body, footer));
}

// --- core/theme.js ---
{
  let body = blocks.themeEngine + '\n\n' + blocks.layoutDeck + '\n\n' + blocks.effects + '\n\n' + blocks.effectsFreeze;
  // The effects-freeze manager no longer reaches into the skin iframe directly;
  // app.js registers aogpn.theme.onHiddenChange for that part.
  body = replaceAll(body,
    `    } catch (e) { /* iframe not loaded or inaccessible yet — handled on load */ }
    skinNotify();
  }`,
    `    } catch (e) { /* iframe not loaded or inaccessible yet — handled on load */ }
    if (window.aogpn && window.aogpn.theme && window.aogpn.theme.onHiddenChange)
      { try { window.aogpn.theme.onHiddenChange(_effectsHidden); } catch (e) { /* ignore */ } }
    aogpn.events.emit('skin-changed');
  }`, 1);
  const depLines = `  const t = aogpn.i18n.t;
  const escHtml = aogpn.util.escHtml;
  const { $ } = aogpn.util;
  const postToHost = aogpn.bridge.postToHost;
  const body = document.body;
  let reduceEffects = false;
`;
  const footer = `  window.aogpn = window.aogpn || {};
  window.aogpn.theme = {
    applyTheme, fireConfetti, applyLayout, renderThemeDeck, renderTopThemePopover,
    loadThemesFromDisk, initCanvasEffects, playThemeSound,
    setEffectsTier, setReduceEffects, setEffectsHidden,
    applyEffectsHiddenNow: _applyEffectsHidden,
    isEffectsHidden: () => _effectsHidden,
    getEffectsTier: () => effectsTier,
    getCurrentThemeId: () => _currentThemeId,
    getPerfState: () => ({
      matrixFramePending: _matrixFramePending,
      confettiN: _confettiParticles.length,
      osReducedMotion: _osReducedMotion
    }),
    THEMES, THEME_STORAGE_KEY, LAYOUT_STORAGE_KEY,
    onHiddenChange: null
  };
  initReduceEffects();
`;
  fs.writeFileSync(path.join(CORE, 'theme.js'), moduleFile(headerCore('core/theme.js', 'tema motoru + efektler'), depLines, body, footer));
}

// --- core/tooltips.js ---
{
  const body = blocks.tooltips;
  const depLines = `  const t = aogpn.i18n.t;
`;
  fs.writeFileSync(path.join(CORE, 'tooltips.js'), moduleFile(headerCore('core/tooltips.js', 'özel tooltip motoru'), depLines, body, ''));
}

// --- features/window-controls.js ---
{
  const body = blocks.windowControls;
  const depLines = `  const { $ } = aogpn.util;
  const postToHost = aogpn.bridge.postToHost;
  const body = document.body;
`;
  fs.writeFileSync(path.join(FEAT, 'window-controls.js'), moduleFile(headerCore('features/window-controls.js', 'pencere kontrolleri'), depLines, body, ''));
}

// --- features/release-notes.js ---
{
  let body = blocks.releaseNotes;
  body = replaceAll(body, `    skinNotify();
  };`, `    aogpn.events.emit('skin-changed');
  };`, 1);
  const depLines = `  const t = aogpn.i18n.t;
`;
  const footer = `  window.aogpn = window.aogpn || {};
  window.aogpn.releaseNotes = {
    loadReleaseNotesFromDisk, renderReleaseNotes,
    getAppInfo: () => _appInfo
  };
`;
  fs.writeFileSync(path.join(FEAT, 'release-notes.js'), moduleFile(headerCore('features/release-notes.js', 'sürüm notları + uygulama bilgisi'), depLines, body, footer));
}

// ---------------------------------------------------------------------------
// Write the trimmed app.js
// ---------------------------------------------------------------------------
fs.writeFileSync(APP, src);

const newLines = src.split('\n').length;
console.log('OK — app.js: ' + newLines + ' satır (modüller ayrıldı).');
console.log('core/events.js             ' + fs.readFileSync(path.join(CORE, 'events.js'), 'utf8').split('\n').length + ' satır');
console.log('core/util.js               ' + fs.readFileSync(path.join(CORE, 'util.js'), 'utf8').split('\n').length + ' satır');
console.log('core/dom.js                ' + fs.readFileSync(path.join(CORE, 'dom.js'), 'utf8').split('\n').length + ' satır');
console.log('core/bridge.js             ' + fs.readFileSync(path.join(CORE, 'bridge.js'), 'utf8').split('\n').length + ' satır');
console.log('core/i18n.js               ' + fs.readFileSync(path.join(CORE, 'i18n.js'), 'utf8').split('\n').length + ' satır');
console.log('core/selects.js            ' + fs.readFileSync(path.join(CORE, 'selects.js'), 'utf8').split('\n').length + ' satır');
console.log('core/theme.js              ' + fs.readFileSync(path.join(CORE, 'theme.js'), 'utf8').split('\n').length + ' satır');
console.log('core/tooltips.js           ' + fs.readFileSync(path.join(CORE, 'tooltips.js'), 'utf8').split('\n').length + ' satır');
console.log('features/window-controls.js ' + fs.readFileSync(path.join(FEAT, 'window-controls.js'), 'utf8').split('\n').length + ' satır');
console.log('features/release-notes.js   ' + fs.readFileSync(path.join(FEAT, 'release-notes.js'), 'utf8').split('\n').length + ' satır');