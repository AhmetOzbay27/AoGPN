/* ==========================================================================
   split-wave2.js — app.js → features/telemetry.js (Dalga 2)
   --------------------------------------------------------------------------
   split-modules.js ile aynı mekanik: app.js'deki telemetri bölümünü (gauge
   kalibrasyonu, canlı kartlar, oturum sayacı, GPN failover flaşı) birebir
   kesip features/telemetry.js'ye taşır. Oturum sayacı (sessionSec/sessionTimer)
   ve CIRC sabiti de bu modüle taşınır; app.js startSession()/stopSession()
   çağırır. Her anchor TEK olmalıdır.

   Kullanım:  node split-wave2.js
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

function cutLine(anchor) {
  const i = src.indexOf(anchor);
  if (i < 0) throw new Error('cutLine anchor not found: ' + anchor.slice(0, 60));
  if (src.indexOf(anchor, i + 1) !== -1) throw new Error('cutLine anchor not unique: ' + anchor.slice(0, 60));
  const lineStart = src.lastIndexOf('\n', i) + 1;
  const lineEnd = src.indexOf('\n', i);
  const end = lineEnd < 0 ? src.length : lineEnd + 1;
  src = src.slice(0, lineStart) + src.slice(end);
}

function replaceAll(text, from, to, expected) {
  assertCount(text, from, expected);
  return text.split(from).join(to);
}

// ---------------------------------------------------------------------------
// 1) Cuts
// ---------------------------------------------------------------------------
const gaugesBlock = cut(
  "  // Adaptive gauge calibration. The old code divided by hard-coded 25/12 Mbps,",
  "  function setConnected(next, nextConnecting) {");
const flashBlock = cut(
  "  // Failover anında Ping / Packet-Loss telemetri kartlarını çakıp karar bağlamını",
  '  // "GPN Bağlan" butonu durum rozeti: host (GpnResilience mode kararlarıyla)');
cutLine("  let sessionSec = 0;");
cutLine("  let sessionTimer = null;");
cutLine("  const CIRC = 276.46;");

// ---------------------------------------------------------------------------
// 2) app.js re-wiring
// ---------------------------------------------------------------------------
src = replaceAll(src,
  `      if (!sessionTimer) sessionTimer = setInterval(tickSession, 1000);
      sessionSec = 0; $('sessionTime').textContent = '00:00:00';`,
  `      aogpn.telemetry.startSession();`, 1);

src = replaceAll(src,
  `      if (sessionTimer) { clearInterval(sessionTimer); sessionTimer = null; }`,
  `      aogpn.telemetry.stopSession();`, 1);

src = replaceAll(src,
  `    _availabilityHoldUntil = 0;
    const server = info.server || info.Server || '';`,
  `    aogpn.telemetry.clearAvailabilityHold();
    const server = info.server || info.Server || '';`, 1);

src = replaceAll(src,
  `      _availabilityHoldUntil = Date.now() + AVAILABILITY_HOLD_MS;`,
  `      aogpn.telemetry.holdAvailability();`, 1);

src = replaceAll(src,
  "    get sessionTime() { return formatSessionTime(); },",
  "    get sessionTime() { return aogpn.telemetry.formatSessionTime(); },", 1);

src = replaceAll(src,
  `    applyRealIpState, updateTelemetry, renderDashboardBoostCards, updateStatusLine,
    getProtocolPreference: () => protocolPreference,
    getMode: () => mode,
    getTransport: () => transport,
    getConnected: () => connected,
    getLastIpState: () => _lastIpState,
    getLastTelemetry: () => _lastTelemetry
  };`,
  `    applyRealIpState, updateTelemetry, renderDashboardBoostCards, updateStatusLine,
    updateGlobalPanel, applyIpFreshness,
    getProtocolPreference: () => protocolPreference,
    getMode: () => mode,
    getTransport: () => transport,
    getConnected: () => connected,
    getLastIpState: () => _lastIpState,
    getLastTelemetry: () => _lastTelemetry,
    setLastTelemetry: (sample) => { _lastTelemetry = sample; }
  };`, 1);

// Alias for the module functions the coordinator still calls directly.
src = replaceAll(src,
  "  const { loadReleaseNotesFromDisk } = aogpn.releaseNotes;",
  `  const { loadReleaseNotesFromDisk } = aogpn.releaseNotes;
  const { resetTelemetry, updateTelemetry, flashGpnTelemetry } = aogpn.telemetry;`, 1);

// ---------------------------------------------------------------------------
// 3) features/telemetry.js assembly
// ---------------------------------------------------------------------------
let body = gaugesBlock + '\n\n' + flashBlock;

// updateTelemetry: session state + host sample bookkeeping go through aogpn.app.
body = replaceAll(body,
  `  function updateTelemetry(pingFromHost, lossFromHost, downFromHost, upFromHost) {
    _lastTelemetry = [pingFromHost, lossFromHost, downFromHost, upFromHost];
    window.__nxTelemetry = _lastTelemetry;
    if (!connected) {`,
  `  function updateTelemetry(pingFromHost, lossFromHost, downFromHost, upFromHost) {
    const app = aogpn.app;
    const connected = !!(app && typeof app.getConnected === 'function' && app.getConnected());
    const mode = app && typeof app.getMode === 'function' ? app.getMode() : 'gpn';
    const sample = [pingFromHost, lossFromHost, downFromHost, upFromHost];
    window.__nxTelemetry = sample;
    if (app && typeof app.setLastTelemetry === 'function') app.setLastTelemetry(sample);
    if (!connected) {`, 1);

body = replaceAll(body,
  `      const skipPingUpdate = availabilityHeld || mode === 'gpn';`,
  `      const skipPingUpdate = availabilityHeld || mode === 'gpn';`, 1);

body = replaceAll(body,
  `      if (!skipPingUpdate) {`,
  `      if (!skipPingUpdate) {`, 1);

body = replaceAll(body,
  `    updateGlobalPanel();
    aogpn.events.emit('skin-changed'); // refresh standalone skins with the latest telemetry sample`,
  `    if (app && typeof app.updateGlobalPanel === 'function') app.updateGlobalPanel();
    aogpn.events.emit('skin-changed'); // refresh standalone skins with the latest telemetry sample`, 1);

// tickSession + formatSessionTime: session counters now live in this module.
body = replaceAll(body,
  `  // Shared session-time formatter (also used by skinBridge.sessionTime).
  function formatSessionTime() {
    const h = String(Math.floor(sessionSec / 3600)).padStart(2, '0');
    const m = String(Math.floor((sessionSec % 3600) / 60)).padStart(2, '0');
    const s = String(sessionSec % 60).padStart(2, '0');
    return \`\${h}:\${m}:\${s}\`;
  }

  function tickSession() {
    sessionSec++;
    $('sessionTime').textContent = formatSessionTime();
    // IP ölçüm tazeliğini saniyede bir yenile (yaş artar, bayat sızıntı amber'e düşer).
    if (_lastIpState) applyIpFreshness();
  }`,
  `  // Shared session-time formatter (also used by skinBridge.sessionTime).
  function formatSessionTime() {
    const h = String(Math.floor(sessionSec / 3600)).padStart(2, '0');
    const m = String(Math.floor((sessionSec % 3600) / 60)).padStart(2, '0');
    const s = String(sessionSec % 60).padStart(2, '0');
    return \`\${h}:\${m}:\${s}\`;
  }

  function tickSession() {
    sessionSec++;
    $('sessionTime').textContent = formatSessionTime();
    // IP ölçüm tazeliğini saniyede bir yenile (yaş artar, bayat sızıntı amber'e düşer).
    const app = aogpn.app;
    if (app && app.getLastIpState()) {
      if (typeof app.applyIpFreshness === 'function') app.applyIpFreshness();
    }
  }

  // Oturum zamanlayıcısı (setConnected tarafından çağrılır): sayaç her
  // bağlantıda sıfırlanır, her saniye tickSession tetiklenir.
  function startSession() {
    sessionSec = 0;
    $('sessionTime').textContent = '00:00:00';
    if (!sessionTimer) sessionTimer = setInterval(tickSession, 1000);
  }

  function stopSession() {
    if (sessionTimer) { clearInterval(sessionTimer); sessionTimer = null; }
  }`, 1);

const depLines = `  const t = aogpn.i18n.t;
  const { $ } = aogpn.util;
  const { pingVal, pingSub, lossVal, lossSub, downVal, upVal,
          downGauge, upGauge } = aogpn.dom;
  let sessionSec = 0;
  let sessionTimer = null;
  const CIRC = 276.46;
`;

const footer = `  window.aogpn = window.aogpn || {};
  window.aogpn.telemetry = {
    updateTelemetry, resetTelemetry, formatSessionTime, flashGpnTelemetry,
    startSession, stopSession,
    clearAvailabilityHold: () => { _availabilityHoldUntil = 0; },
    holdAvailability: () => { _availabilityHoldUntil = Date.now() + AVAILABILITY_HOLD_MS; }
  };
`;

const header = `/* ==========================================================================
   features/telemetry.js — AoGPN dashboard feature module (canlı telemetri)
   --------------------------------------------------------------------------
   Gauge kalibrasyonu (uyarlanabilir ölçek), canlı ping/kayıp/indirme/yükleme
   kartları, oturum sayacı ve GPN failover/kurtarma flaşı. app.js'den ÖNCE
   yüklenir; bağlantı durumuna aogpn.app üzerinden lazily erişir.
   ========================================================================== */
`;

const moduleText = header + '(() => {\n  \'use strict\';\n' + depLines + body + '\n\n' + footer + '})();\n';
fs.writeFileSync(path.join(__dirname, 'features', 'telemetry.js'), moduleText);
fs.writeFileSync(APP, src);

console.log('OK — features/telemetry.js: ' + moduleText.split('\n').length + ' satır; app.js: ' + src.split('\n').length + ' satır.');