/* ==========================================================================
   split-wave5.js — app.js → features/views.js (Dalga 5)
   --------------------------------------------------------------------------
   Taşınan bölüm: viewTitleParts .. window.updateMonitorSnapshot
   (showView, monitor, split-routing, boost kartları, app ikonları).
   window.applySettings koordinatör olarak app.js'de kalır.
   Kesim anchor'ları doğrulanır; app.js içi çağrılar aogpn.views.* üzerinden
   yeniden bağlanır; modül durum erişimini aogpn.app getter/setter'larına çevirir.
   ========================================================================== */
const fs = require('fs');
const path = require('path');

const APP = path.join(__dirname, 'app.js');
const OUT = path.join(__dirname, 'features', 'views.js');

let src = fs.readFileSync(APP, 'utf8');

// --- 1) Anchor doğrulama ---------------------------------------------------
const START = '  function viewTitleParts(view) {';
const END = '  window.applySettings = (data) => {';

function countOcc(hay, needle) {
  let n = 0, i = 0;
  while ((i = hay.indexOf(needle, i)) !== -1) { n++; i += needle.length; }
  return n;
}

if (countOcc(src, START) !== 1) throw new Error('START anchor beklenmiyor: ' + countOcc(src, START));
if (countOcc(src, END) !== 1) throw new Error('END anchor beklenmiyor: ' + countOcc(src, END));

const startIdx = src.indexOf(START);
const endIdx = src.indexOf(END);
let block = src.slice(startIdx, endIdx);
src = src.slice(0, startIdx) + src.slice(endIdx);

// --- 2) Block içi referans yeniden bağlama (her biri sayım doğrulamalı) ----
const rewrites = [
  // showView: currentView yazımı registry'ye
  ['  function showView(view) {\n    currentView = view;',
   '  function showView(view) {\n    aogpn.app.setCurrentView(view);', 1],
  // blacklistActive
  ["  const blacklistActive = () => invertManual && (mode === 'gpn' || splitMode === 'manual');",
   "  const blacklistActive = () => aogpn.app.getInvertManual() && (aogpn.app.getMode() === 'gpn' || aogpn.app.getSplitMode() === 'manual');", 1],
  // warpNodeNameFor
  ['    if (useRealNodes && realNodes.has(indexId)) {\n      const n = realNodes.get(indexId);',
   '    if (aogpn.app.getUseRealNodes() && aogpn.app.getRealNodes().has(indexId)) {\n      const n = aogpn.app.getRealNodes().get(indexId);', 1],
  ['    const fallback = NODES.find(x => x.key === indexId);',
   '    const fallback = aogpn.app.getNodes().find(x => x.key === indexId);', 1],
  // warpNodeOptions
  ['    const nodes = useRealNodes && realNodes.size > 0\n      ? [...realNodes.values()]\n      : NODES.map(n => ({ indexId: n.key, name: n.name }));',
   '    const nodes = aogpn.app.getUseRealNodes() && aogpn.app.getRealNodes().size > 0\n      ? [...aogpn.app.getRealNodes().values()]\n      : aogpn.app.getNodes().map(n => ({ indexId: n.key, name: n.name }));', 1],
  // configuredActionFor + configuredWarpNodeFor (2 özdeş önek)
  ['    return (monitorSnapshot.apps || []).find(item =>',
   '    return (aogpn.app.getMonitorSnapshot().apps || []).find(item =>', 2],
  // renderMonitorConnections
  ['    const rows = (monitorSnapshot.connections || []).filter(item => {',
   '    const rows = (aogpn.app.getMonitorSnapshot().connections || []).filter(item => {', 1],
  // renderDashboardBoostCards
  ['    const apps = (monitorSnapshot.apps || [])\n      .filter(a => a.action === \'vpn\' || a.action === \'vpn+proxy\')',
   '    const apps = (aogpn.app.getMonitorSnapshot().apps || [])\n      .filter(a => a.action === \'vpn\' || a.action === \'vpn+proxy\')', 1],
  ['    const totalCount = (monitorSnapshot.apps || []).length;',
   '    const totalCount = (aogpn.app.getMonitorSnapshot().apps || []).length;', 1],
  // realPingMarkup — _activeGpnServer
  ['    const via = (realAfter && _activeGpnServer)',
   '    const via = (realAfter && aogpn.app.getActiveGpnServer())', 1],
  ['      ? ` · ${escHtml(t(\'boost.viaNode\', { node: _activeGpnServer }))}`',
   '      ? ` · ${escHtml(t(\'boost.viaNode\', { node: aogpn.app.getActiveGpnServer() }))}`', 1],
  // renderSplitApps
  ['    const all = monitorSnapshot.apps || [];',
   '    const all = aogpn.app.getMonitorSnapshot().apps || [];', 1],
  ["      const warpBlocked = warpEntries > 0 && _activeGpnMode && _activeGpnMode !== 'WireGuard';",
   "      const warpBlocked = warpEntries > 0 && aogpn.app.getActiveGpnMode() && aogpn.app.getActiveGpnMode() !== 'WireGuard';", 1],
  ['    if (currentView === \'perf\') {\n      renderMonitorConnections();\n    }',
   '    if (aogpn.app.getCurrentView() === \'perf\') {\n      renderMonitorConnections();\n    }', 1],
  ['    if (currentView === \'boost\') {\n      renderSplitApps();\n    }',
   '    if (aogpn.app.getCurrentView() === \'boost\') {\n      renderSplitApps();\n    }', 1],
  ['    if (currentView === \'boost\') renderSplitApps();',
   '    if (aogpn.app.getCurrentView() === \'boost\') renderSplitApps();', 1],
  // applySplitMode
  ["      button.classList.toggle('active', button.dataset.splitMode === splitMode);",
   "      button.classList.toggle('active', button.dataset.splitMode === aogpn.app.getSplitMode());", 1],
  ["    if ($('boostModeHint')) $('boostModeHint').textContent = hints[splitMode] || hints.off;",
   "    if ($('boostModeHint')) $('boostModeHint').textContent = hints[aogpn.app.getSplitMode()] || hints.off;", 1],
  ["    if (splitMode === 'vpn' || splitMode === 'manual') {\n      const nextMode = splitMode === 'manual' ? 'gpn' : 'vpn';\n      if (mode !== nextMode) {\n        mode = nextMode;\n        applyMode();\n      }\n    }\n    syncQuickControls();",
   "    if (aogpn.app.getSplitMode() === 'vpn' || aogpn.app.getSplitMode() === 'manual') {\n      const nextMode = aogpn.app.getSplitMode() === 'manual' ? 'gpn' : 'vpn';\n      if (aogpn.app.getMode() !== nextMode) {\n        aogpn.app.setMode(nextMode);\n        aogpn.app.applyMode();\n      }\n    }\n    aogpn.app.syncQuickControls();", 1],
  // updateTunProxyNotice
  ['      const show = transport === \'tun\' && !connected && isProxyModeEnabled(systemProxyMode);',
   '      const show = aogpn.app.getTransport() === \'tun\' && !aogpn.app.getConnected() && aogpn.app.isProxyModeEnabled(aogpn.app.getSystemProxyMode());', 1],
  // updateGlobalPanel
  ["    const vpnActive = mode === 'vpn' && connected;",
   "    const vpnActive = aogpn.app.getMode() === 'vpn' && aogpn.app.getConnected();", 1],
  // renderProcessCatalog
  ['    const items = processCatalog.filter(item => {',
   '    const items = aogpn.app.getProcessCatalog().filter(item => {', 1],
  // window.updateProcessList
  ['    processCatalog = Array.isArray(items)\n      ? items.filter(item => item && Number.isInteger(item.pid) && item.pid > 0).slice(0, 300)\n      : [];',
   '    aogpn.app.setProcessCatalog(Array.isArray(items)\n      ? items.filter(item => item && Number.isInteger(item.pid) && item.pid > 0).slice(0, 300)\n      : []);', 1],
  ['    requestAppIcons(processCatalog.map(item => item.exePath));',
   '    requestAppIcons(aogpn.app.getProcessCatalog().map(item => item.exePath));', 1],
  // window.updateMonitorSnapshot — durum yazımları
  ['    monitorSnapshot = {\n      ...data,\n      connections: Array.isArray(data.connections) ? data.connections : [],\n      apps: Array.isArray(data.apps) ? data.apps : [],\n      traffic: Array.isArray(data.traffic) ? data.traffic : []\n    };',
   '    aogpn.app.setMonitorSnapshot({\n      ...data,\n      connections: Array.isArray(data.connections) ? data.connections : [],\n      apps: Array.isArray(data.apps) ? data.apps : [],\n      traffic: Array.isArray(data.traffic) ? data.traffic : []\n    });', 1],
  ['    $(\'monitorConnectionsCount\').textContent = String(data.activeConnectionCount ?? monitorSnapshot.connections.length);',
   '    $(\'monitorConnectionsCount\').textContent = String(data.activeConnectionCount ?? aogpn.app.getMonitorSnapshot().connections.length);', 1],
  ['    $(\'monitorAppsCount\').textContent = String(data.activeAppCount ?? monitorSnapshot.apps.length);',
   '    $(\'monitorAppsCount\').textContent = String(data.activeAppCount ?? aogpn.app.getMonitorSnapshot().apps.length);', 1],
  ["    splitMode = ['off', 'vpn', 'manual'].includes(data.mode) ? data.mode : splitMode;",
   "    aogpn.app.setSplitMode(['off', 'vpn', 'manual'].includes(data.mode) ? data.mode : aogpn.app.getSplitMode());", 1],
  ['      invertManual = data.invertManualRouting;\n      applyDirection();',
   '      aogpn.app.setInvertManual(data.invertManualRouting);\n      applyDirection();', 1],
];

for (const [from, to, expected] of rewrites) {
  const n = countOcc(block, from);
  if (n !== expected) throw new Error(`BLOCK rewrite beklenmiyor (${expected} != ${n}): ${from.slice(0, 70)}`);
  block = block.split(from).join(to);
}

// --- 3) Modül dosyasını birleştir ------------------------------------------
const moduleText = `/* ==========================================================================
   features/views.js — AoGPN dashboard feature module (görünümler & rota tabloları)
   --------------------------------------------------------------------------
   Görünüm geçişleri (showView), GlassWire-style Connection Monitor, split
   routing tablosu (route select / remove / reorder), dashboard Game Boost
   kartları, process kataloğu + seçici, host-çözümlü uygulama ikonları ve
   window.updateMonitorSnapshot / setAppIcons / __aogpnT köprüleri. Durum
   (currentView, splitMode, monitorSnapshot, ...) app.js koordinatöründe
   aogpn.app getter/setter'ları üzerinden okunur/yazılır.
   ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const resolveKey = aogpn.i18n.resolveKey;
  const escHtml = aogpn.util.escHtml;
  const $ = aogpn.util.$;
  const postToHost = aogpn.bridge.postToHost;
  const upgradeSelect = aogpn.selects.upgradeSelect;

${block}
  window.aogpn = window.aogpn || {};
  window.aogpn.views = {
    showView, viewTitleParts, renderSplitApps, renderMonitorConnections,
    renderDashboardBoostCards, renderProcessCatalog, setProcessPickerOpen,
    applySplitMode, applyDirection, updateGlobalPanel, updateTunProxyNotice
  };
})();
`;

fs.writeFileSync(OUT, moduleText);

// --- 4) app.js çağrı yerlerini aogpn.views.* üzerinden bağla ----------------
const callerRewrites = [
  // init sonu (önce — tek satır yeniden yazımlarla çakışmaması için)
  ['  applySplitMode();\n  renderMonitorConnections();\n  renderSplitApps();\n  renderDashboardBoostCards();\n  resetTelemetry();\n  applyDirection();',
   '  aogpn.views.applySplitMode();\n  aogpn.views.renderMonitorConnections();\n  aogpn.views.renderSplitApps();\n  aogpn.views.renderDashboardBoostCards();\n  resetTelemetry();\n  aogpn.views.applyDirection();', 1],
  // setConnected (8-boşluk)
  ['        if (monitorSnapshot && (monitorSnapshot.apps || []).length) {\n          renderDashboardBoostCards();\n          if (currentView === \'boost\') renderSplitApps();\n        }',
   '        if (monitorSnapshot && (monitorSnapshot.apps || []).length) {\n          aogpn.views.renderDashboardBoostCards();\n          if (currentView === \'boost\') aogpn.views.renderSplitApps();\n        }', 1],
  // setGpnConnectionInfo (4-boşluk)
  ['    if (monitorSnapshot && (monitorSnapshot.apps || []).length) {\n      renderDashboardBoostCards();\n      if (currentView === \'boost\') renderSplitApps();\n    }',
   '    if (monitorSnapshot && (monitorSnapshot.apps || []).length) {\n      aogpn.views.renderDashboardBoostCards();\n      if (currentView === \'boost\') aogpn.views.renderSplitApps();\n    }', 1],
  // setConnected: panel + proxy bildirimi ikilisi
  ['    updateGlobalPanel();\n    updateTunProxyNotice();',
   '    aogpn.views.updateGlobalPanel();\n    aogpn.views.updateTunProxyNotice();', 1],
  // applyMode + applyRealIpState (2 özdeş)
  ['    updateGlobalPanel();',
   '    aogpn.views.updateGlobalPanel();', 2],
  // applyTransport + applySystemProxyState (2 özdeş)
  ['    updateTunProxyNotice();',
   '    aogpn.views.updateTunProxyNotice();', 2],
  // applySettings
  ['          invertManual = g.invertManualRouting;\n          applyDirection();',
   '          invertManual = g.invertManualRouting;\n          aogpn.views.applyDirection();', 1],
  // init: mode/split pills
  ['    applySplitMode();',
   '    aogpn.views.applySplitMode();', 2],
  ['    showView(item.dataset.view);',
   '    aogpn.views.showView(item.dataset.view);', 1],
  ['    applyDirection();',
   '    aogpn.views.applyDirection();', 2],
  ['  $(\'monitorFilter\')?.addEventListener(\'input\', renderMonitorConnections);',
   '  $(\'monitorFilter\')?.addEventListener(\'input\', aogpn.views.renderMonitorConnections);', 1],
  ['  $(\'monitorHideListeners\')?.addEventListener(\'change\', renderMonitorConnections);',
   '  $(\'monitorHideListeners\')?.addEventListener(\'change\', aogpn.views.renderMonitorConnections);', 1],
  ['    setProcessPickerOpen(Boolean(picker?.classList.contains(\'hidden\')));',
   '    aogpn.views.setProcessPickerOpen(Boolean(picker?.classList.contains(\'hidden\')));', 1],
  ['  $(\'boostProcessPickerClose\')?.addEventListener(\'click\', () => setProcessPickerOpen(false));',
   '  $(\'boostProcessPickerClose\')?.addEventListener(\'click\', () => aogpn.views.setProcessPickerOpen(false));', 1],
  ['  $(\'boostProcessFilter\')?.addEventListener(\'input\', renderProcessCatalog);',
   '  $(\'boostProcessFilter\')?.addEventListener(\'input\', aogpn.views.renderProcessCatalog);', 1],
  // aogpn.app registry: taşınan fonksiyonlar delegasyona döner
  ['    applyRealIpState, updateTelemetry, renderDashboardBoostCards, updateStatusLine,\n    updateGlobalPanel, applyIpFreshness, renderSplitApps,',
   '    applyRealIpState, updateTelemetry, updateStatusLine, applyIpFreshness,\n    renderDashboardBoostCards: () => aogpn.views.renderDashboardBoostCards(),\n    updateGlobalPanel: () => aogpn.views.updateGlobalPanel(),\n    renderSplitApps: () => aogpn.views.renderSplitApps(),', 1],
  // registry: yeni erişimciler
  ['    getCurrentView: () => currentView\n  };',
   '    getCurrentView: () => currentView,\n    setCurrentView: (v) => { currentView = v; },\n    getInvertManual: () => invertManual,\n    setInvertManual: (v) => { invertManual = v; },\n    getSplitMode: () => splitMode,\n    setSplitMode: (v) => { splitMode = v; },\n    setMode: (v) => { mode = v; },\n    getMonitorSnapshot: () => monitorSnapshot,\n    setMonitorSnapshot: (m) => { monitorSnapshot = m; },\n    getProcessCatalog: () => processCatalog,\n    setProcessCatalog: (p) => { processCatalog = p; },\n    getActiveGpnServer: () => _activeGpnServer,\n    getActiveGpnMode: () => _activeGpnMode,\n    getUseRealNodes: () => useRealNodes,\n    getRealNodes: () => realNodes,\n    getNodes: () => NODES,\n    getSystemProxyMode: () => systemProxyMode,\n    syncQuickControls,\n    isProxyModeEnabled\n  };', 1],
];

for (const [from, to, expected] of callerRewrites) {
  const n = countOcc(src, from);
  if (n !== expected) throw new Error(`CALLER rewrite beklenmiyor (${expected} != ${n}): ${from.slice(0, 70)}`);
  src = src.split(from).join(to);
}

fs.writeFileSync(APP, src);

// --- 5) Doğrulama -----------------------------------------------------------
const declCheck = ['viewTitleParts', 'showView', 'routeLabels', 'blacklistActive', 'effectiveRoute',
  'tagForRoute', 'routeBadge', 'warpNodeNameFor', 'warpNodeOptions', 'warpNodeSelect', 'routeSelect',
  'monitorFilterMatches', 'configuredActionFor', 'configuredWarpNodeFor', 'renderMonitorConnections',
  'renderDashboardBoostCards', 'renderProcessCatalog', 'setProcessPickerOpen', 'targetIpsMarkup',
  'realPingMarkup', 'renderSplitApps', 'bindRouteSelectors', 'bindDeleteButtons', 'bindMoveButtons',
  'applySplitMode', 'applyDirection', 'updateTunProxyNotice', 'updateGlobalPanel',
  'appIconCache', 'normalizedIconPath', 'requestAppIcons', 'appIconMarkup'];
for (const fn of declCheck) {
  if (!moduleText.includes(`function ${fn}(`) && !moduleText.includes(`const ${fn}`)) {
    throw new Error('Modülde eksik: ' + fn);
  }
}
for (const w of ['window.updateProcessList', 'window.setAppIcons', 'window.__aogpnT', 'window.updateMonitorSnapshot', 'window.aogpn.views']) {
  if (!moduleText.includes(w)) throw new Error('Modülde eksik window export: ' + w);
}

console.log('OK — features/views.js: ' + moduleText.split('\n').length + ' satır; app.js: ' + src.split('\n').length + ' satır.');