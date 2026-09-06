/* ==========================================================================
   split-wave4.js — app.js → features/gpn.js (Dalga 4)
   --------------------------------------------------------------------------
   GPN paneli: diyagnoz, sunucu yönetimi, sunucu kümesi, WARP/WinDivert
   sağlığı, PID havuzu, WinDivert kuyruk + Wintun ayarları, yakalanan trafik,
   failover matrisi, aday tahmini, kurtarma/failover toggle'ları, failover
   telemetrisi, karar günlüğü, GPN Bağlan rozeti + panel kablolaması.
   app.js yalnızca alias + skinBridge erişimcileriyle bağlanır.

   Kullanım:  node split-wave4.js
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
// 1) Cuts (5 parça, modül içinde aynı sırayla birleştirilir)
// ---------------------------------------------------------------------------
const gpnConnectStateBlock = cut(
  "  let _gpnConnectStateLabel = '—';",
  "  window.setGpnConnectionInfo = (info) => {");

const gpnMainBlock = cut(
  '  // GPN diyagnoz akışı: host, DiagLog "GPN_*" satırlarını (GPN_LOG /',
  "  window.setAdminState = (admin) => {");

const gpnTogglesBlock = cut(
  "  let gpnRecoveryWatch = true;",
  "  systemProxyToggleBtn.addEventListener('click', () => {");

const gpnTunStackBlock = cut(
  "  const captureTunStack = document.getElementById('captureTunStack');",
  "  document.querySelectorAll('[data-view]').forEach(item => item.addEventListener('click', (e) => {");

const gpnAdvancedBlock = cut(
  "  (function initGpnAdvanced() {",
  "  // Prime the node pool panel and its links as soon as the page is ready; the");

// ---------------------------------------------------------------------------
// 2) app.js re-wiring
// ---------------------------------------------------------------------------
// Aliases (setGpnResilience / applySettings / boot / bindGpnServersControls çağrısı).
src = replaceAll(src,
  "  const { resetTelemetry, updateTelemetry, flashGpnTelemetry } = aogpn.telemetry;",
  `  const { resetTelemetry, updateTelemetry, flashGpnTelemetry } = aogpn.telemetry;
  const { startGpnClusterLoop, setGpnConnectState, setGpnRecoveryWatchUI,
          setGpnFailoverUI, bindGpnServersControls } = aogpn.gpn;`, 1);

// skinBridge getters → aogpn.gpn erişimcileri (skin sözleşmesi aynı kalır).
src = replaceAll(src,
  "    get gpnServers() { return gpnServers; },",
  "    get gpnServers() { return aogpn.gpn.getServers(); },", 1);
src = replaceAll(src,
  "    get gpnServerProbes() { return Array.from(gpnServerProbes.values()); },",
  "    get gpnServerProbes() { return aogpn.gpn.getServerProbes(); },", 1);
src = replaceAll(src,
  "    get gpnRecoveryWatch() { return gpnRecoveryWatch; },",
  "    get gpnRecoveryWatch() { return aogpn.gpn.getRecoveryWatch(); },", 1);
src = replaceAll(src,
  "    get gpnFailover() { return gpnFailover; },",
  "    get gpnFailover() { return aogpn.gpn.getFailover(); },", 1);
src = replaceAll(src,
  "    get gpnConnectState() { return { label: _gpnConnectStateLabel || '—', pending: !!_gpnConnectPending }; },",
  "    get gpnConnectState() { return aogpn.gpn.getConnectState(); },", 1);
src = replaceAll(src,
  "    get gpnPidPool() { return _gpnPidPool || null; },",
  "    get gpnPidPool() { return aogpn.gpn.getPidPool(); },", 1);
src = replaceAll(src,
  "    get gpnCaptureSettings() { return _gpnCaptureSettings || null; },",
  "    get gpnCaptureSettings() { return aogpn.gpn.getCaptureSettings(); },", 1);
src = replaceAll(src,
  "    get gpnWintunSettings() { return _gpnWintunSettings || null; },",
  "    get gpnWintunSettings() { return aogpn.gpn.getWintunSettings(); },", 1);
src = replaceAll(src,
  "    get gpnCaptureStats() { return _gpnCaptureStats || null; },",
  "    get gpnCaptureStats() { return aogpn.gpn.getCaptureStats(); },", 1);
src = replaceAll(src,
  "    get gpnFailoverMatrix() { return _gpnFailoverMatrix || null; },",
  "    get gpnFailoverMatrix() { return aogpn.gpn.getFailoverMatrix(); },", 1);
src = replaceAll(src,
  "    get gpnSelectionPrediction() { return _gpnSelectionPrediction || null; },",
  "    get gpnSelectionPrediction() { return aogpn.gpn.getSelectionPrediction(); },", 1);
src = replaceAll(src,
  "    get gpnTelemetry() { return _gpnTelemetrySnap || null; },",
  "    get gpnTelemetry() { return aogpn.gpn.getTelemetrySnap(); },", 1);
src = replaceAll(src,
  "    get gpnResilienceLog() { return _gpnResilienceLog || null; },",
  "    get gpnResilienceLog() { return aogpn.gpn.getResilienceLog(); },", 1);
src = replaceAll(src,
  "    get gpnDiagLines() { return _gpnDiagLines.slice(); },",
  "    get gpnDiagLines() { return aogpn.gpn.getDiagLines(); },", 1);

// ---------------------------------------------------------------------------
// 3) features/gpn.js assembly
// ---------------------------------------------------------------------------
const body = gpnConnectStateBlock + '\n\n' + gpnMainBlock + '\n\n' + gpnTogglesBlock + '\n\n' + gpnTunStackBlock + '\n\n' + gpnAdvancedBlock;

const depLines = `  const t = aogpn.i18n.t;
  const escHtml = aogpn.util.escHtml;
  const postToHost = aogpn.bridge.postToHost;
`;

const footer = `  window.aogpn = window.aogpn || {};
  window.aogpn.gpn = {
    startGpnClusterLoop, setGpnConnectState, setGpnRecoveryWatchUI, setGpnFailoverUI,
    bindGpnServersControls,
    getServers: () => gpnServers,
    getServerProbes: () => Array.from(gpnServerProbes.values()),
    getRecoveryWatch: () => gpnRecoveryWatch,
    getFailover: () => gpnFailover,
    getConnectState: () => ({ label: _gpnConnectStateLabel || '—', pending: !!_gpnConnectPending }),
    getPidPool: () => _gpnPidPool || null,
    getCaptureSettings: () => _gpnCaptureSettings || null,
    getWintunSettings: () => _gpnWintunSettings || null,
    getCaptureStats: () => _gpnCaptureStats || null,
    getFailoverMatrix: () => _gpnFailoverMatrix || null,
    getSelectionPrediction: () => _gpnSelectionPrediction || null,
    getTelemetrySnap: () => _gpnTelemetrySnap || null,
    getResilienceLog: () => _gpnResilienceLog || null,
    getDiagLines: () => _gpnDiagLines.slice()
  };
`;

const header = `/* ==========================================================================
   features/gpn.js — AoGPN dashboard feature module (GPN paneli)
   --------------------------------------------------------------------------
   Sunucu Yönetimi (import/edit/toggle/delete + canlı ölçüm), sunucu kümesi
   kartı, GPN diyagnoz akışı, WARP/WinDivert sağlık banner'ları, PID havuzu,
   WinDivert kuyruk + Wintun ayarları, yakalanan trafik, failover matrisi,
   en iyi aday tahmini, kurtarma/failover toggle'ları, failover telemetrisi,
   karar günlüğü ve GPN Bağlan rozeti. Host köprüsü window.setGpn* sözleşmesi
   bu modülde yaşar; app.js/skinBridge aogpn.gpn erişimcileriyle bağlanır.
   ========================================================================== */
`;

const moduleText = header + '(() => {\n  \'use strict\';\n' + depLines + body + '\n\n' + footer + '})();\n';
fs.writeFileSync(path.join(__dirname, 'features', 'gpn.js'), moduleText);
fs.writeFileSync(APP, src);

console.log('OK — features/gpn.js: ' + moduleText.split('\n').length + ' satır; app.js: ' + src.split('\n').length + ' satır.');