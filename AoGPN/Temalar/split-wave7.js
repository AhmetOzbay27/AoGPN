/* ==========================================================================
   split-wave7.js — app.js → features/connection.js (Dalga 7)
   --------------------------------------------------------------------------
   Bağlantı modülü: setConnected + bağlantı durumu UI'si, mod/transport/
   protokol/system-proxy uygulayıcıları, host köprüsü (setConnectionState ...
   setRealIpState / applyIpFreshness / ip recheck döngüsü) ve bağlantı hatası
   kartı. DURUM app.js koordinatöründe kalır; modül aogpn.app üzerinden st.
   canlı köprüsüyle erişir. _connectionError / _ipVerifyBaseDetail /
   _ipRecheckTimer kesimle birlikte modüle taşınır.
   ========================================================================== */
const fs = require('fs');
const path = require('path');

const APP = path.join(__dirname, 'app.js');
const OUT = path.join(__dirname, 'features', 'connection.js');

let src = fs.readFileSync(APP, 'utf8');

function countStr(hay, needle) {
  let n = 0, i = 0;
  while ((i = hay.indexOf(needle, i)) !== -1) { n++; i += needle.length; }
  return n;
}
function countRe(hay, re) {
  const m = hay.match(new RegExp(re.source, re.flags.includes('g') ? re.flags : re.flags + 'g'));
  return m ? m.length : 0;
}
function cutBlock(startAnchor, endAnchor, includeEnd) {
  if (countStr(src, startAnchor) !== 1) throw new Error('START beklenmiyor: ' + startAnchor.slice(0, 60));
  if (countStr(src, endAnchor) !== 1) throw new Error('END beklenmiyor: ' + endAnchor.slice(0, 60));
  const s = src.indexOf(startAnchor);
  let e = src.indexOf(endAnchor);
  if (includeEnd) e += endAnchor.length;
  const block = src.slice(s, e);
  src = src.slice(0, s) + src.slice(e);
  return block;
}

// --- 1) Kesimler (aşağıdan yukarıya) ---------------------------------------
let blockB = cutBlock('  window.setConnectionState = (next, backendMode, backendConnecting) => {',
  '  window.updateTelemetry = (ping, loss, download, upload) =>', false);
let blockA = cutBlock('  function setConnected(next, nextConnecting) {',
  '  window.applySettings = (data) => {', false);

// --- 2) Block içi yeniden bağlama (sıra önemli!) -----------------------------
// Önce setGpnConnectionInfo yerel `mode` değişkenini connMode'a çevir (mode
// regex'i ondan sonra çalışır).
const renames = [
  // ---- setGpnConnectionInfo: yerel mode → connMode ----
  ["    const mode = info.mode || info.Mode || '';\n    _activeGpnMode = mode;",
   "    const connMode = info.mode || info.Mode || '';\n    st.activeGpnMode = connMode;", 1],
  ["      const modeTxt = mode ? (' · ' + mode) : '';",
   "      const modeTxt = connMode ? (' · ' + connMode) : '';", 1],
  ["      sessionNode.textContent = server + (mode ? ' · ' + mode : '');",
   "      sessionNode.textContent = server + (connMode ? ' · ' + connMode : '');", 1],
  // ---- setConnected: ilk durum yazımları ----
  ['    const wasConnected = connected;\n    connected = next;\n    connecting = nextConnecting === true && !connected;',
   '    const wasConnected = st.connected;\n    st.connected = next;\n    st.connecting = nextConnecting === true && !st.connected;', 1],
  ["    body.classList.toggle('connected', connected);",
   "    body.classList.toggle('connected', st.connected);", 1],
  ["    body.classList.toggle('connecting', connecting);",
   "    body.classList.toggle('connecting', st.connecting);", 1],
  ["      mobileConnectBtn.classList.toggle('connected', connected);",
   "      mobileConnectBtn.classList.toggle('connected', st.connected);", 1],
  // ---- setConnected: via etiketi + boost yenileme ----
  ["        _activeGpnServer = '';", "        st.activeGpnServer = '';", 1],
  ["    _activeGpnServer = server || '';", "    st.activeGpnServer = server || '';", 1],
  // ---- updateTransportLock: tam satır ----
  ["    tunLocked = transport === 'tun' && !isAdmin && !connected;",
   "    st.tunLocked = st.transport === 'tun' && !st.isAdmin && !st.connected;", 1],
  // ---- applyIpFreshness: yerel st → ipSt (modülün st köprüsüyle çakışmasın) ----
  ['    const st = _lastIpState;', '    const ipSt = st.lastIpState;', 1],
  ['    const isConnectedNow = st.connected === true;', '    const isConnectedNow = ipSt.connected === true;', 1],
  ['    const isLeaking = isConnectedNow && (st.tunnelVerified !== true)\n      && typeof st.tunnelIp === \'string\' && st.tunnelIp.length > 0\n      && st.ispCached === true;',
   '    const isLeaking = isConnectedNow && (ipSt.tunnelVerified !== true)\n      && typeof ipSt.tunnelIp === \'string\' && ipSt.tunnelIp.length > 0\n      && ipSt.ispCached === true;', 1],
  // ---- setSystemProxyResult: notifyNodes window üzerinden ----
  ['      notifyNodes(localizeProxyMessage(result && result.message ? result.message : \'proxyOnly.updateFailed\'));',
   '      window.notifyNodes(localizeProxyMessage(result && result.message ? result.message : \'proxyOnly.updateFailed\'));', 1],
  ['      notifyNodes(localizeProxyMessage(result.message));',
   '      window.notifyNodes(localizeProxyMessage(result.message));', 1],
  // ---- setGpnResilience: setGpnConnectState aogpn.gpn üzerinden ----
  ["        if (typeof setGpnConnectState === 'function') {\n          setGpnConnectState(evt.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WG');\n        }",
   "        if (typeof aogpn.gpn.setGpnConnectState === 'function') {\n          aogpn.gpn.setGpnConnectState(evt.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WG');\n        }", 1],
  // ---- applyRealIpState: _lastIpState / _lastIpMeasuredAt yazımları ----
  ['    _lastIpState = data;', '    st.lastIpState = data;', 1],
  ['    _lastIpMeasuredAt = (typeof data.measuredAt === \'string\' && data.measuredAt.length > 0)\n      ? data.measuredAt\n      : null;',
   '    st.lastIpMeasuredAt = (typeof data.measuredAt === \'string\' && data.measuredAt.length > 0)\n      ? data.measuredAt\n      : null;', 1],
  ['    const measured = _lastIpMeasuredAt ? new Date(_lastIpMeasuredAt).getTime() : null;',
   '    const measured = st.lastIpMeasuredAt ? new Date(st.lastIpMeasuredAt).getTime() : null;', 1],
  ['    if (!ipVerifyDetail || !_lastIpState) return;',
   '    if (!ipVerifyDetail || !st.lastIpState) return;', 1],
];

for (const [from, to, expected] of renames) {
  const all = blockA + '\n' + blockB;
  const n = countStr(all, from);
  if (n !== expected) throw new Error(`RENAME beklenmiyor (${expected} != ${n}): ${from.slice(0, 70)}`);
}
for (const [from, to] of renames) {
  blockA = blockA.split(from).join(to);
  blockB = blockB.split(from).join(to);
}

// Regex tabanlı durum köprüsü (sıra: belirli isimler önce, çakışmasın).
const regexes = [
  [/monitorSnapshot/g, 'st.monitorSnapshot', 2],
  [/currentView/g, 'st.currentView', 2],
  [/connected/g, 'st.connected', null],       // sayı doğrulanamaz — string'ler korunur (lookbehind yok; elle listelenen satırlar önceden çevrildi)
];
// Güvenli: yalnızca tam bağımsız belirteçler (nokta/kelime öneki ve son eki yok).
const safeRegexes = [
  [/(?<![\w.])connecting(?![\w])/g, 'st.connecting', null],
  [/(?<![\w.])tunLocked(?![\w])/g, 'st.tunLocked', null],
  [/(?<![\w.])isAdmin(?![\w])/g, 'st.isAdmin', null],
  [/(?<![\w.])transport(?![\w])/g, 'st.transport', null],
  [/(?<![\w.])mode(?![\w])/g, 'st.mode', null],
  [/(?<![\w.])protocolPreference(?![\w])/g, 'st.protocolPreference', null],
  [/(?<![\w.])autoReconnect(?![\w])/g, 'st.autoReconnect', null],
  [/(?<![\w.])systemProxyMode(?![\w])/g, 'st.systemProxyMode', null],
  [/(?<![\w.])effectiveSystemProxyMode(?![\w])/g, 'st.effectiveSystemProxyMode', null],
  [/(?<![\w.])systemProxyConnectionOwned(?![\w])/g, 'st.systemProxyConnectionOwned', null],
  [/(?<![\w.])systemProxyAppliedAddress(?![\w])/g, 'st.systemProxyAppliedAddress', null],
  [/(?<![\w.])prevSystemProxyConnectionOwned(?![\w])/g, 'st.prevSystemProxyConnectionOwned', null],
  [/(?<![\w.])connected(?![\w])/g, 'st.connected', null],
];

const applyBoth = (from, to) => {
  blockA = blockA.replace(from, to);
  blockB = blockB.replace(from, to);
};
for (const [re, to] of regexes) applyBoth(re, to);
for (const [re, to] of safeRegexes) applyBoth(re, to);

// Kalan ham durum referansı kalmadığını doğrula (yorum/string dışı).
const leftover = (blockA + '\n' + blockB).match(/(?<![\w.])(connected|connecting|tunLocked|isAdmin|transport|mode|protocolPreference|autoReconnect|systemProxyMode|effectiveSystemProxyMode|systemProxyConnectionOwned|systemProxyAppliedAddress|prevSystemProxyConnectionOwned|_lastIpState|_lastIpMeasuredAt|_activeGpnServer|_activeGpnMode|monitorSnapshot|currentView)(?![\w])/g);
const leftovers = leftover ? leftover.filter(w => !['modeTxt', 'backendMode', 'nextMode'].includes(w)) : [];
if (leftovers.length) throw new Error('Kalan durum referansı: ' + leftovers.slice(0, 10).join(', '));

// --- 3) Modül dosyası --------------------------------------------------------
const moduleText = `/* ==========================================================================
   features/connection.js — AoGPN dashboard feature module (bağlantı)
   --------------------------------------------------------------------------
   Bağlantı durumu UI'si (setConnected, CONNECT halkası, durum satırı), mod/
   transport/protokol/system-proxy uygulayıcıları, IP doğrulama paneli ve host
   köprüsü (setConnectionState ... setRealIpState / setConnectionError / ip
   recheck döngüsü). Bağlantı DURUMU app.js koordinatöründe kalır; bu modül
   aogpn.app kayıt defterine bağlı st. canlı köprüsüyle erişir (durum tek
   kaynakta, skinBridge/init/applySettings doğrudan okumaya devam eder).
   ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const countryDisplayName = aogpn.i18n.countryDisplayName;
  const $ = aogpn.util.$;
  const postToHost = aogpn.bridge.postToHost;
  const refreshAllCustomSelects = aogpn.selects.refreshAllCustomSelects;
  const { btnIcon, btnText, btnSub, statusDot, statusLabel,
          ipDisplay, ipCountry, ipVerifyText, ipVerifyDetail, ipVerifyCard,
          ipLeakBanner, ipLeakText, ipOkBanner, ipOkText, ipOkTunnelIp, ipOkIspIp,
          systemProxyToggleBtn, systemProxyDot, systemProxyLabel, systemProxyAddress,
          systemProxyModeSelect, quickProtocolSelect, quickAutoReconnect,
          protocolHint, routeHint, transportHint, connectionStatusLine } = aogpn.dom;
  const { resetTelemetry, flashGpnTelemetry } = aogpn.telemetry;
  const { playThemeSound, getCurrentThemeId } = aogpn.theme;
  const body = document.body;
  // app.js durum kayıt defterine canlı köprü — durum koordinatörde kalır.
  const st = {
    get connected() { return aogpn.app.getConnected(); },
    set connected(v) { aogpn.app.setConnectedRaw(v); },
    get connecting() { return aogpn.app.getConnecting(); },
    set connecting(v) { aogpn.app.setConnectingRaw(v); },
    get mode() { return aogpn.app.getMode(); },
    set mode(v) { aogpn.app.setMode(v); },
    get transport() { return aogpn.app.getTransport(); },
    set transport(v) { aogpn.app.setTransportRaw(v); },
    get protocolPreference() { return aogpn.app.getProtocolPreference(); },
    set protocolPreference(v) { aogpn.app.setProtocolPreferenceRaw(v); },
    get autoReconnect() { return aogpn.app.getAutoReconnect(); },
    set autoReconnect(v) { aogpn.app.setAutoReconnectRaw(v); },
    get systemProxyMode() { return aogpn.app.getSystemProxyMode(); },
    set systemProxyMode(v) { aogpn.app.setSystemProxyModeRaw(v); },
    get effectiveSystemProxyMode() { return aogpn.app.getEffectiveSystemProxyMode(); },
    set effectiveSystemProxyMode(v) { aogpn.app.setEffectiveSystemProxyModeRaw(v); },
    get systemProxyConnectionOwned() { return aogpn.app.getSystemProxyConnectionOwned(); },
    set systemProxyConnectionOwned(v) { aogpn.app.setSystemProxyConnectionOwnedRaw(v); },
    get systemProxyAppliedAddress() { return aogpn.app.getSystemProxyAppliedAddress(); },
    set systemProxyAppliedAddress(v) { aogpn.app.setSystemProxyAppliedAddressRaw(v); },
    get prevSystemProxyConnectionOwned() { return aogpn.app.getPrevSystemProxyConnectionOwned(); },
    set prevSystemProxyConnectionOwned(v) { aogpn.app.setPrevSystemProxyConnectionOwnedRaw(v); },
    get isAdmin() { return aogpn.app.getIsAdmin(); },
    set isAdmin(v) { aogpn.app.setIsAdminRaw(v); },
    get tunLocked() { return aogpn.app.getTunLocked(); },
    set tunLocked(v) { aogpn.app.setTunLockedRaw(v); },
    get activeGpnServer() { return aogpn.app.getActiveGpnServer(); },
    set activeGpnServer(v) { aogpn.app.setActiveGpnServerRaw(v); },
    get activeGpnMode() { return aogpn.app.getActiveGpnMode(); },
    set activeGpnMode(v) { aogpn.app.setActiveGpnModeRaw(v); },
    get lastIpState() { return aogpn.app.getLastIpState(); },
    set lastIpState(v) { aogpn.app.setLastIpStateRaw(v); },
    get lastIpMeasuredAt() { return aogpn.app.getLastIpMeasuredAt(); },
    set lastIpMeasuredAt(v) { aogpn.app.setLastIpMeasuredAtRaw(v); },
    get monitorSnapshot() { return aogpn.app.getMonitorSnapshot(); },
    get currentView() { return aogpn.app.getCurrentView(); }
  };

${blockA}

${blockB}

  window.aogpn = window.aogpn || {};
  window.aogpn.connection = {
    setConnected, updateConnectTooltip, updateStatusLine, applyMode, applyTransport,
    updateTransportLock, applyProtocolPreference, isProxyModeEnabled, syncQuickControls,
    applySystemProxyState, showConnectionError, applyRealIpState, applyIpFreshness,
    getConnectionError: () => _connectionError || null
  };
})();
`;

fs.writeFileSync(OUT, moduleText);

// --- 4) app.js çağrı yerleri --------------------------------------------------
const callerRewrites = [
  // skinBridge
  ['    setConnected: (next) => { setConnected(!!next, false); },',
   '    setConnected: (next) => aogpn.connection.setConnected(!!next, false),', 1],
  ['    get connectionError() { return _connectionError || null; },',
   '    get connectionError() { return aogpn.connection.getConnectionError(); },', 1],
  // window nexus exports
  ['  window.setNexusConnected = (next) => { setConnected(!!next, false); };',
   '  window.setNexusConnected = (next) => aogpn.connection.setConnected(!!next, false);', 1],
  ['  window.setNexusConnecting = (next) => { setConnected(false, !!next); };',
   '  window.setNexusConnecting = (next) => aogpn.connection.setConnected(false, !!next);', 1],
  // aogpn.app registry: delegasyonlar
  ['    applyMode, updateTransportLock, applyProtocolPreference,\n    renderTopNodeSelect: () => aogpn.nodes.renderTopNodeSelect(),\n    applyRealIpState, updateTelemetry, updateStatusLine, applyIpFreshness,',
   '    applyMode: () => aogpn.connection.applyMode(),\n    updateTransportLock: () => aogpn.connection.updateTransportLock(),\n    applyProtocolPreference: (next) => aogpn.connection.applyProtocolPreference(next),\n    renderTopNodeSelect: () => aogpn.nodes.renderTopNodeSelect(),\n    updateTelemetry,\n    applyRealIpState: (data) => aogpn.connection.applyRealIpState(data),\n    updateStatusLine: () => aogpn.connection.updateStatusLine(),\n    applyIpFreshness: () => aogpn.connection.applyIpFreshness(),', 1],
  ['    getTunLocked: () => tunLocked,\n    setConnected: (next, nextConnecting) => { setConnected(next, nextConnecting); },',
   '    getTunLocked: () => tunLocked,\n    setTunLockedRaw: (v) => { tunLocked = v; },\n    setConnected: (next, nextConnecting) => aogpn.connection.setConnected(next, nextConnecting),\n    setConnectedRaw: (v) => { connected = v; },', 1],
  // registry kuyruğu: yeni erişimciler + delegasyonlar
  ['    getSystemProxyMode: () => systemProxyMode,\n    syncQuickControls,\n    isProxyModeEnabled\n  };',
   '    getSystemProxyMode: () => systemProxyMode,\n    getConnecting: () => connecting,\n    getAutoReconnect: () => autoReconnect,\n    getEffectiveSystemProxyMode: () => effectiveSystemProxyMode,\n    getSystemProxyConnectionOwned: () => systemProxyConnectionOwned,\n    getSystemProxyAppliedAddress: () => systemProxyAppliedAddress,\n    getPrevSystemProxyConnectionOwned: () => prevSystemProxyConnectionOwned,\n    getIsAdmin: () => isAdmin,\n    getLastIpMeasuredAt: () => _lastIpMeasuredAt,\n    setConnectingRaw: (v) => { connecting = v; },\n    setAutoReconnectRaw: (v) => { autoReconnect = v; },\n    setSystemProxyModeRaw: (v) => { systemProxyMode = v; },\n    setEffectiveSystemProxyModeRaw: (v) => { effectiveSystemProxyMode = v; },\n    setSystemProxyConnectionOwnedRaw: (v) => { systemProxyConnectionOwned = v; },\n    setSystemProxyAppliedAddressRaw: (v) => { systemProxyAppliedAddress = v; },\n    setPrevSystemProxyConnectionOwnedRaw: (v) => { prevSystemProxyConnectionOwned = v; },\n    setIsAdminRaw: (v) => { isAdmin = v; },\n    setProtocolPreferenceRaw: (v) => { protocolPreference = v; },\n    setTransportRaw: (v) => { transport = v; },\n    setLastIpStateRaw: (v) => { _lastIpState = v; },\n    setLastIpMeasuredAtRaw: (v) => { _lastIpMeasuredAt = v; },\n    syncQuickControls: () => aogpn.connection.syncQuickControls(),\n    isProxyModeEnabled: (v) => aogpn.connection.isProxyModeEnabled(v)\n  };', 1],
  // init
  ['  applyMode();\n  applyTransport();\n  applyProtocolPreference(protocolPreference);\n  applySystemProxyState(systemProxyMode, effectiveSystemProxyMode, false);',
   '  aogpn.connection.applyMode();\n  aogpn.connection.applyTransport();\n  aogpn.connection.applyProtocolPreference(protocolPreference);\n  aogpn.connection.applySystemProxyState(systemProxyMode, effectiveSystemProxyMode, false);', 1],
  // applySettings
  ['        applySystemProxyState(g.sysProxyType, g.effectiveSysProxyType, g.connectionOwnsProxy);',
   '        aogpn.connection.applySystemProxyState(g.sysProxyType, g.effectiveSysProxyType, g.connectionOwnsProxy);', 1],
  ['        applyProtocolPreference(g.protocolPreference);',
   '        aogpn.connection.applyProtocolPreference(g.protocolPreference);', 1],
  ['          syncQuickControls();',
   '          aogpn.connection.syncQuickControls();', 1],
  // system proxy bağları
  ['      const next = isProxyModeEnabled(systemProxyMode) ? 0 : 1;',
   '      const next = aogpn.connection.isProxyModeEnabled(systemProxyMode) ? 0 : 1;', 1],
  ['      applySystemProxyState(next, next, false);',
   '      aogpn.connection.applySystemProxyState(next, next, false);', 2],
  // bindProtocolSelect / bindAutoReconnectToggle (bağlam-anchor'lı — 8-boşluk
  // satırı 6-boşluk dizgenin alt dizesi olduğu için saf sayım yanıltır)
  ['      const next = el.value;\n      applyProtocolPreference(next);',
   '      const next = el.value;\n      aogpn.connection.applyProtocolPreference(next);', 1],
  ['      if (!postToHost({ action: \'set_protocol_preference\', protocol: next })) {\n        applyProtocolPreference(next);\n      }',
   '      if (!postToHost({ action: \'set_protocol_preference\', protocol: next })) {\n        aogpn.connection.applyProtocolPreference(next);\n      }', 1],
  ['      syncQuickControls();',
   '      aogpn.connection.syncQuickControls();', 3],
];

for (const [from, to, expected] of callerRewrites) {
  const n = countStr(src, from);
  if (n !== expected) throw new Error(`CALLER rewrite beklenmiyor (${expected} != ${n}): ${from.slice(0, 70)}`);
  src = src.split(from).join(to);
}

fs.writeFileSync(APP, src);

// --- 5) Doğrulama -------------------------------------------------------------
const declCheck = ['function setConnected', 'function updateConnectTooltip', 'function updateStatusLine',
  'function applyMode', 'function applyTransport', 'function updateTransportLock',
  'function applyProtocolPreference', 'function isProxyModeEnabled', 'function syncQuickControls',
  'function applySystemProxyState', 'function showConnectionError', 'function applyRealIpState',
  'function applyIpFreshness', 'function startIpRecheckLoop', 'function stopIpRecheckLoop',
  'let _connectionError', 'let _ipVerifyBaseDetail', 'let _ipRecheckTimer'];
for (const fn of declCheck) {
  if (!moduleText.includes(fn)) throw new Error('Modülde eksik: ' + fn);
}
for (const w of ['window.setConnectionState', 'window.setTransport', 'window.setGpnResilience',
  'window.setGpnNodeSwitch', 'window.setGpnConnectionInfo', 'window.setAvailabilityInfo',
  'window.setAdminState', 'window.setProtocolPreference', 'window.setSystemProxyState',
  'window.setSystemProxyResult', 'window.setConnectionError', 'window.setRealIpState',
  'window.aogpn.connection']) {
  if (!moduleText.includes(w)) throw new Error('Modülde eksik window export: ' + w);
}

console.log('OK — features/connection.js: ' + moduleText.split('\n').length + ' satır; app.js: ' + src.split('\n').length + ' satır.');