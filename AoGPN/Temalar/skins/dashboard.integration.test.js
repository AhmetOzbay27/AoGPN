// ============================================================================
// Dashboard integration test: boost table wiring in the REAL main dashboard
// ----------------------------------------------------------------------------
// Loads vpn-gpn-dashboard.html + Temalar/app.js into jsdom via the shared
// sandbox (loadDashboard) and drives the actual boost table: app rows render
// from the monitor snapshot, the per-row route select posts set_app_route with
// the picked route, the delete button posts remove_app (subject to confirm),
// and the "Add EXE" button posts add_app. Every message is captured through the
// stubbed host channel (window.chrome.webview.postMessage).
//
// Run with:
//   node --test Temalar/skins/dashboard.integration.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { loadDashboard } = require('./skin-sandbox.js');

const APPS = [
  { id: 1, processName: 'cs2', displayName: 'Counter-Strike 2', action: 'vpn', isRunning: true },
  { id: 2, processName: 'VALORANT', displayName: 'Valorant', action: 'direct', isRunning: true },
  { id: 3, processName: 'fortnite', displayName: 'Fortnite', action: 'block', isRunning: false }
];

// ---------------------------------------------------------------------------
// Boot timing summary — printed once after the last test of this file so a
// regression in loadDashboard cost (or a boot step that got slow) is visible
// right in the test output.
// ---------------------------------------------------------------------------

const _started = process.hrtime.bigint();
const _boots = []; // ms per loadDashboard call

async function bootDashboard() {
  const t = process.hrtime.bigint();
  const dash = await loadDashboard();
  _boots.push(Number(process.hrtime.bigint() - t) / 1e6);
  return dash;
}

test.after(() => {
  const total = Number(process.hrtime.bigint() - _started) / 1e6;
  const n = _boots.length;
  const avg = n ? _boots.reduce((a, b) => a + b, 0) / n : 0;
  const max = n ? Math.max(..._boots) : 0;
  console.log(`\n⏱ dashboard integration: ${total.toFixed(0)} ms total · ${n} dashboard boots · avg ${avg.toFixed(0)} ms/boot · max ${max.toFixed(0)} ms`);
});

// Boot a fresh dashboard with the boost view open and the snapshot applied.
async function boostWithApps(apps) {
  const dash = await bootDashboard();
  assert.ok(dash.openView('boost'), 'boost nav item must exist and be clickable');
  dash.updateMonitorSnapshot({ apps, mode: 'manual', connected: false });
  return dash;
}

// ---------------------------------------------------------------------------
// Boost table rendering
// ---------------------------------------------------------------------------

test('dashboard: boost table renders one row per app with route select + remove button', async () => {
  const dash = await boostWithApps(APPS);
  const rows = dash.rows();
  assert.equal(rows.length, 3, 'one row per boost app');

  rows.forEach((row, i) => {
    const pname = APPS[i].processName;
    assert.equal(row.dataset.processName, pname, 'row carries the process name');
    const sel = dash.getRouteSelect(pname);
    assert.ok(sel, pname + ' has a route select');
    assert.equal(sel.dataset.routeProcess, pname);
    assert.equal(sel.dataset.routeDisplay, APPS[i].displayName);
    assert.ok(dash.getRemoveBtn(pname), pname + ' has a remove button');
  });

  // The select is pre-selected to the app's current action.
  assert.equal(dash.getRouteSelect('cs2').value, 'vpn');
  assert.equal(dash.getRouteSelect('VALORANT').value, 'direct');
  assert.equal(dash.getRouteSelect('fortnite').value, 'block');
  dash.close();
});

test('dashboard: legacy vpn+proxy / proxy actions render as the merged vpn option', async () => {
  const dash = await boostWithApps([
    { id: 1, processName: 'eft', displayName: 'EFT', action: 'vpn+proxy', isRunning: true },
    { id: 2, processName: 'wow', displayName: 'WoW', action: 'proxy', isRunning: true }
  ]);
  assert.equal(dash.rows().length, 2);
  assert.equal(dash.getRouteSelect('eft').value, 'vpn', 'vpn+proxy normalizes to vpn');
  assert.equal(dash.getRouteSelect('wow').value, 'vpn', 'proxy normalizes to vpn');
  dash.close();
});

// ---------------------------------------------------------------------------
// Real-ping before/after on the dashboard boost cards
// ---------------------------------------------------------------------------

test('dashboard: boost cards show real server ping before/after with delta', async () => {
  const dash = await bootDashboard();
  dash.updateMonitorSnapshot({
    apps: [{
      id: 1,
      processName: 'eft',
      displayName: 'EFT',
      action: 'vpn',
      isRunning: true,
      beforePingMs: 42,
      beforePingText: '42 ms',
      afterPingMs: 18,
      afterPingText: '18 ms',
      pingDeltaText: '-24 ms'
    }],
    mode: 'manual',
    connected: false
  });
  const cards = dash.document.getElementById('dashboardBoostCards');
  assert.ok(cards, 'boost cards container exists');
  const text = cards.textContent;
  assert.ok(text.includes('18 ms'), 'primary latency shows the after (tunnel) ping');
  assert.ok(text.includes('42 ms'), 'before ping is shown on the real-ping line');
  assert.ok(text.includes('-24 ms'), 'before → after delta is shown');
  dash.close();
});

test('dashboard: boost card falls back to the before ping when no after measurement exists', async () => {
  const dash = await bootDashboard();
  dash.updateMonitorSnapshot({
    apps: [{
      id: 1,
      processName: 'wow',
      displayName: 'WoW',
      action: 'vpn',
      isRunning: true,
      beforePingMs: 55,
      beforePingText: '55 ms'
    }],
    mode: 'manual',
    connected: false
  });
  const text = dash.document.getElementById('dashboardBoostCards').textContent;
  assert.ok(text.includes('55 ms'), 'before ping is the primary latency before a connection');
  dash.close();
});

// ---------------------------------------------------------------------------
// Real-ping before/after in the Game Boost table + selected-node attribution
// ---------------------------------------------------------------------------

test('dashboard: boost table shows real server ping before/after with delta', async () => {
  const dash = await boostWithApps([{
    id: 1,
    processName: 'eft',
    displayName: 'EFT',
    action: 'vpn',
    isRunning: true,
    beforePingMs: 42,
    beforePingText: '42 ms',
    afterPingMs: 18,
    afterPingText: '18 ms',
    pingDeltaText: '-24 ms'
  }]);
  const body = dash.document.getElementById('splitAppsBody');
  assert.ok(body, 'split apps table body exists');
  const text = body.textContent;
  assert.ok(text.includes('42 ms'), 'before ping is shown in the boost table');
  assert.ok(text.includes('18 ms'), 'after ping is shown in the boost table');
  assert.ok(text.includes('-24 ms'), 'before → after delta is shown in the boost table');
  assert.ok(text.includes('→'), 'before → after arrow is shown');
  dash.close();
});

test('dashboard: real-ping delta names the selected node via the via label', async () => {
  const dash = await bootDashboard();
  assert.ok(dash.openView('boost'), 'boost nav item must exist and be clickable');
  // The selected/active node drives the "via" attribution for the after (tunnel)
  // measurement — the delta then reads as this node's improvement.
  dash.window.setGpnConnectionInfo({ server: 'Almanya', delayMs: 12, mode: 'WireGuard' });
  dash.updateMonitorSnapshot({
    apps: [{
      id: 1,
      processName: 'eft',
      displayName: 'EFT',
      action: 'vpn',
      isRunning: true,
      beforePingMs: 42,
      beforePingText: '42 ms',
      afterPingMs: 18,
      afterPingText: '18 ms',
      pingDeltaText: '-24 ms'
    }],
    mode: 'manual',
    connected: true
  });
  const cardsText = dash.document.getElementById('dashboardBoostCards').textContent;
  assert.ok(cardsText.includes('via Almanya'), 'boost card attributes the delta to the selected node');
  const bodyText = dash.document.getElementById('splitAppsBody').textContent;
  assert.ok(bodyText.includes('via Almanya'), 'boost table attributes the delta to the selected node');
  dash.close();
});

// ---------------------------------------------------------------------------
// Route select -> set_app_route
// ---------------------------------------------------------------------------

test('dashboard: route select change posts set_app_route with the picked route', async () => {
  const dash = await boostWithApps(APPS);

  dash.selectRoute('cs2', 'block');
  dash.selectRoute('VALORANT', 'vpn');
  dash.selectRoute('fortnite', 'direct');

  const posts = dash.postsWith('set_app_route');
  assert.equal(posts.length, 3);
  assert.deepEqual(posts[0], { action: 'set_app_route', processName: 'cs2', displayName: 'Counter-Strike 2', route: 'block' });
  assert.deepEqual(posts[1], { action: 'set_app_route', processName: 'VALORANT', displayName: 'Valorant', route: 'vpn' });
  assert.deepEqual(posts[2], { action: 'set_app_route', processName: 'fortnite', displayName: 'Fortnite', route: 'direct' });
  dash.close();
});

test('dashboard: WARP route option is offered and posts set_app_route warp', async () => {
  const dash = await boostWithApps(APPS);

  const sel = dash.getRouteSelect('cs2');
  const warpOption = [...sel.options].find(o => o.value === 'warp');
  assert.ok(warpOption, 'route select offers the WARP (clean egress) option');
  assert.equal(warpOption.textContent, 'WARP');

  dash.selectRoute('cs2', 'warp');
  const posts = dash.postsWith('set_app_route');
  assert.deepEqual(posts[0], { action: 'set_app_route', processName: 'cs2', displayName: 'Counter-Strike 2', route: 'warp' });
  dash.close();
});

test('dashboard: WARP fallback warning shows when the active connection is not WireGuard', async () => {
  const dash = await boostWithApps([
    { id: 1, processName: 'BsGLauncher.exe', displayName: 'BSG Launcher', action: 'warp', isRunning: true }
  ]);
  const warn = dash.document.getElementById('warpFallbackNotice');
  assert.ok(warn, 'warp fallback notice element exists');

  // V2ray TCP (fallback) modunda WARP uyarısı görünür — kurallar VPN'e düşer.
  dash.window.setGpnConnectionInfo({ server: 'Almanya', delayMs: 42, mode: 'V2rayTCP' });
  assert.ok(!warn.classList.contains('hidden'), 'V2ray TCP modunda WARP uyarısı görünmeli');

  // WireGuard modunda uyarı gizlenir — WARP çalışır.
  dash.window.setGpnConnectionInfo({ server: 'Almanya', delayMs: 12, mode: 'WireGuard' });
  assert.ok(warn.classList.contains('hidden'), 'WireGuard modunda WARP uyarısı gizli kalmalı');

  // Mod bilgisi yokken (bağlantısız) uyarı gizli kalır.
  dash.window.setGpnConnectionInfo({ server: 'Almanya', delayMs: -1, mode: '' });
  assert.ok(warn.classList.contains('hidden'), 'mod bilinmiyorsa uyarı gizli kalmalı');

  // WARP girişi yokken mod ne olursa olsun uyarı görünmez.
  dash.window.setGpnConnectionInfo({ server: 'Almanya', delayMs: 42, mode: 'V2rayTCP' });
  dash.updateMonitorSnapshot({ apps: [{ id: 2, processName: 'cs2', displayName: 'CS2', action: 'vpn' }], mode: 'manual', connected: true });
  assert.ok(warn.classList.contains('hidden'), 'WARP girişi yokken uyarı gizli kalmalı');
  dash.close();
});

test('dashboard: empty route selection posts nothing (placeholder option)', async () => {
  const dash = await boostWithApps(APPS);
  dash.selectRoute('cs2', '');
  assert.equal(dash.postsWith('set_app_route').length, 0, 'placeholder selection is not a route change');
  dash.close();
});

// ---------------------------------------------------------------------------
// Add / remove app
// ---------------------------------------------------------------------------

test('dashboard: remove button posts remove_app when the user confirms', async () => {
  const dash = await boostWithApps(APPS);
  assert.ok(dash.removeApp('fortnite'));
  const posts = dash.postsWith('remove_app');
  assert.equal(posts.length, 1);
  assert.deepEqual(posts[0], { action: 'remove_app', processName: 'fortnite' });
  dash.close();
});

test('dashboard: remove button posts nothing when the user cancels the confirm', async () => {
  const dash = await boostWithApps(APPS);
  dash.setConfirm(false);
  assert.ok(dash.removeApp('fortnite'));
  assert.equal(dash.postsWith('remove_app').length, 0, 'cancelled confirmation must not remove the app');
  dash.close();
});

test('dashboard: Add EXE button posts add_app', async () => {
  const dash = await boostWithApps(APPS);
  assert.ok(dash.addApp());
  const posts = dash.postsWith('add_app');
  assert.equal(posts.length, 1);
  assert.deepEqual(posts[0], { action: 'add_app' });
  dash.close();
});

// ---------------------------------------------------------------------------
// Row reordering -> move_route
// ---------------------------------------------------------------------------

test('dashboard: reorder buttons post move_route and disable at the list edges', async () => {
  const dash = await boostWithApps(APPS);
  const moveBtn = (value, dir) => dash.document.querySelector(`[data-move-entry="${dir}"][data-move-value="${value}"]`);

  // Every app row has up/down controls; first/last rows are locked at the edges.
  assert.ok(moveBtn('cs2', 'up'), 'row renders an up control');
  assert.ok(moveBtn('cs2', 'down'), 'row renders a down control');
  assert.equal(moveBtn('cs2', 'up').disabled, true, 'first row cannot move up');
  assert.equal(moveBtn('cs2', 'down').disabled, false, 'first row can move down');
  assert.equal(moveBtn('VALORANT', 'up').disabled, false, 'middle row can move up');
  assert.equal(moveBtn('VALORANT', 'down').disabled, false, 'middle row can move down');
  assert.equal(moveBtn('fortnite', 'up').disabled, false, 'last row can move up');
  assert.equal(moveBtn('fortnite', 'down').disabled, true, 'last row cannot move down');

  // Clicking moves the entry by type + value so domain/IP rows are addressable.
  moveBtn('VALORANT', 'up').click();
  moveBtn('cs2', 'down').click();
  const posts = dash.postsWith('move_route');
  assert.equal(posts.length, 2);
  assert.deepEqual(posts[0], { action: 'move_route', entryType: 'app', value: 'VALORANT', direction: 'up' });
  assert.deepEqual(posts[1], { action: 'move_route', entryType: 'app', value: 'cs2', direction: 'down' });
  dash.close();
});

test('dashboard: domain rows reorder by value and controls hide under a filter', async () => {
  const dash = await boostWithApps([
    { id: 1, entryType: 'domain', value: 'escapefromtarkov.com', displayName: 'BSG API', action: 'warp' },
    { id: 2, entryType: 'app', processName: 'cs2', displayName: 'Counter-Strike 2', action: 'vpn' }
  ]);
  const findMove = (etype, value, dir) => dash.document.querySelector(
    `[data-move-entry="${dir}"][data-move-etype="${etype}"][data-move-value="${value}"]`);

  // A domain rule listed above the process carries its own identity and edges.
  const domainUp = findMove('domain', 'escapefromtarkov.com', 'up');
  const domainDown = findMove('domain', 'escapefromtarkov.com', 'down');
  assert.ok(domainUp, 'domain row renders an up control');
  assert.ok(domainDown, 'domain row renders a down control');
  assert.equal(domainUp.disabled, true, 'first domain row cannot move up');
  assert.equal(domainDown.disabled, false, 'domain row can move below the process');
  assert.equal(findMove('app', 'cs2', 'down').disabled, true, 'last process row cannot move down');

  domainDown.click();
  const posts = dash.postsWith('move_route');
  assert.deepEqual(posts[0], {
    action: 'move_route', entryType: 'domain', value: 'escapefromtarkov.com', direction: 'down'
  });

  // While the filter is active the controls are hidden (the move targets the
  // real list position, not the filtered one).
  const filterEl = dash.document.getElementById('boostAppFilter');
  filterEl.value = 'cs2';
  filterEl.dispatchEvent(new dash.window.Event('input', { bubbles: true }));
  assert.equal(dash.document.querySelector('[data-move-entry]'), null,
    'reorder controls are hidden while the filter is active');
  dash.close();
});

// ---------------------------------------------------------------------------
// Manual domain/IP rules (BSG API quick add + domain form)
// ---------------------------------------------------------------------------

test('dashboard: BSG API quick button posts add_domain_route warp', async () => {
  const dash = await boostWithApps(APPS);
  const btn = dash.document.getElementById('boostBszApiBtn');
  assert.ok(btn, 'BSG API quick-add button exists');
  btn.click();
  const posts = dash.postsWith('add_domain_route');
  assert.equal(posts.length, 1);
  assert.deepEqual(posts[0], {
    action: 'add_domain_route',
    value: 'escapefromtarkov.com',
    route: 'warp',
    displayName: 'BSG API (escapefromtarkov.com)'
  });
  dash.close();
});

test('dashboard: domain form adds a domain/IP rule with the picked route', async () => {
  const dash = await boostWithApps(APPS);
  const panel = dash.document.getElementById('boostDomainPanel');
  assert.ok(panel, 'domain panel exists');
  assert.ok(panel.classList.contains('hidden'), 'domain panel starts hidden');

  dash.document.getElementById('boostAddDomainBtn').click();
  assert.ok(!panel.classList.contains('hidden'), 'toggling the button opens the panel');
  assert.equal(dash.document.getElementById('boostAddDomainBtn').getAttribute('aria-expanded'), 'true');

  const valueEl = dash.document.getElementById('boostDomainValue');
  const actionEl = dash.document.getElementById('boostDomainAction');
  assert.equal(actionEl.options.length, 4, 'route options are populated');
  valueEl.value = 'escapefromtarkov.com';
  actionEl.value = 'warp';
  dash.document.getElementById('boostDomainSubmit').click();

  const posts = dash.postsWith('add_domain_route');
  assert.equal(posts.length, 1);
  assert.deepEqual(posts[0], {
    action: 'add_domain_route', value: 'escapefromtarkov.com', route: 'warp', displayName: 'escapefromtarkov.com'
  });
  assert.ok(panel.classList.contains('hidden'), 'panel closes after submit');
  dash.close();
});

test('dashboard: domain form ignores an empty value', async () => {
  const dash = await boostWithApps(APPS);
  dash.document.getElementById('boostAddDomainBtn').click();
  dash.document.getElementById('boostDomainSubmit').click();
  assert.equal(dash.postsWith('add_domain_route').length, 0, 'empty submit posts nothing');
  dash.close();
});

// ---------------------------------------------------------------------------
// View wiring (the boost table only renders when the boost view is active)
// ---------------------------------------------------------------------------

test('dashboard: opening the boost view requests a fresh monitor snapshot', async () => {
  const dash = await bootDashboard();
  const before = dash.postsWith('request_monitor_snapshot').length;
  dash.openView('boost');
  const views = dash.postsWith('set_active_view');
  assert.equal(views.length, 1);
  assert.equal(views[0].view, 'boost');
  assert.equal(dash.postsWith('request_monitor_snapshot').length, before + 1,
    'opening boost asks the host for a fresh snapshot so the table is not stale');
  dash.close();
});

// ---------------------------------------------------------------------------
// Monitor (perf) view — connections table rendering and filtering
// ---------------------------------------------------------------------------

const CONNECTIONS = [
  { pid: 1042, processName: 'cs2.exe', displayName: 'Counter-Strike 2', protocol: 'UDP', state: 'Established', remoteAddress: '145.239.14.77:443', countryText: 'Turkey', asnText: 'OVH', routeText: 'Tunneled', routeTag: 'proxy' },
  { pid: 501, processName: 'chrome.exe', displayName: 'Chrome', protocol: 'TCP', state: 'Established', remoteAddress: '142.250.185.14:443', countryText: 'United States', asnText: 'Google', routeText: 'Direct', routeTag: 'direct' },
  { pid: 0, processName: 'sshd.exe', displayName: 'SSH Daemon', protocol: 'TCP', state: 'Listen', remoteAddress: '0.0.0.0:22', countryText: '', asnText: '', routeText: 'Direct', routeTag: 'direct' }
];

function monitorRows(dash) {
  return Array.from(dash.document.querySelectorAll('#monitorConnectionsBody tr'));
}
function setMonitorFilter(dash, text) {
  const el = dash.document.getElementById('monitorFilter');
  el.value = text;
  el.dispatchEvent(new dash.window.Event('input', { bubbles: true }));
}
function setHideListeners(dash, checked) {
  const el = dash.document.getElementById('monitorHideListeners');
  el.checked = checked;
  el.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
}

// Open the monitor view and push a snapshot so renderMonitorConnections runs.
async function perfWithSnapshot(connections, apps) {
  const dash = await bootDashboard();
  assert.ok(dash.openView('perf'), 'perf nav item must exist and be clickable');
  dash.updateMonitorSnapshot({ connections, apps: apps || [], mode: 'manual', connected: false });
  return dash;
}

test('dashboard: monitor view renders the connections table (listeners hidden by default)', async () => {
  const dash = await perfWithSnapshot(CONNECTIONS, [
    { id: 1, entryType: 'app', processName: 'cs2.exe', displayName: 'Counter-Strike 2', action: 'vpn', isRunning: true }
  ]);

  // The TCP/Listen row is filtered out by the (default-on) hide-listeners box.
  const rows = monitorRows(dash);
  assert.equal(rows.length, 2, 'UDP + TCP Established render, TCP Listen is hidden');

  const first = rows[0];
  assert.ok(first.textContent.includes('Counter-Strike 2'), 'display name rendered');
  assert.ok(first.textContent.includes('cs2.exe'), 'process name rendered');
  assert.ok(first.textContent.includes('PID 1042'), 'pid rendered');
  assert.ok(first.textContent.includes('145.239.14.77:443'), 'remote address rendered');
  assert.ok(first.textContent.includes('Turkey'), 'country text rendered');

  // Every row carries a route select, pre-selected from the app snapshot.
  const sel = first.querySelector('select[data-route-process="cs2.exe"]');
  assert.ok(sel, 'connection row has a route select');
  assert.equal(sel.value, 'vpn', 'select pre-set from the app entry via configuredActionFor');

  // Total count reflects the full connection list, not the filtered table.
  assert.equal(dash.document.getElementById('monitorConnectionsCount').textContent, '3');
  dash.close();
});

test('dashboard: monitor filter narrows rows by name, country and address', async () => {
  const dash = await perfWithSnapshot(CONNECTIONS);
  assert.equal(monitorRows(dash).length, 2);

  setMonitorFilter(dash, 'chrome');
  let rows = monitorRows(dash);
  assert.equal(rows.length, 1, 'process-name filter narrows');
  assert.ok(rows[0].textContent.includes('Chrome'));

  setMonitorFilter(dash, 'turkey');
  rows = monitorRows(dash);
  assert.equal(rows.length, 1, 'country filter narrows');
  assert.ok(rows[0].textContent.includes('Counter-Strike 2'));

  setMonitorFilter(dash, '142.250.185.14');
  rows = monitorRows(dash);
  assert.equal(rows.length, 1, 'remote-address filter narrows');
  assert.ok(rows[0].textContent.includes('Chrome'));

  // No match -> the empty-state hint becomes visible.
  setMonitorFilter(dash, 'zzz-no-such-app');
  assert.equal(monitorRows(dash).length, 0);
  assert.ok(!dash.document.getElementById('monitorEmpty').classList.contains('hidden'), 'empty hint shown when nothing matches');

  // Clearing the filter restores the full (listener-filtered) table.
  setMonitorFilter(dash, '');
  assert.equal(monitorRows(dash).length, 2);
  assert.ok(dash.document.getElementById('monitorEmpty').classList.contains('hidden'), 'empty hint hidden again');
  dash.close();
});

test('dashboard: hide-listeners toggle reveals TCP Listen rows', async () => {
  const dash = await perfWithSnapshot(CONNECTIONS);
  assert.equal(monitorRows(dash).length, 2, 'Listen row hidden by default');

  setHideListeners(dash, false);
  const rows = monitorRows(dash);
  assert.equal(rows.length, 3, 'Listen row appears once the filter is off');
  assert.ok(rows.some(r => r.textContent.includes('SSH Daemon')));

  setHideListeners(dash, true);
  assert.equal(monitorRows(dash).length, 2, 'toggling back on hides it again');
  dash.close();
});

// ---------------------------------------------------------------------------
// Connection controls — mode pills, split-mode pills (kill-switch family) and
// the quick-connect buttons
// ---------------------------------------------------------------------------

function click(dash, el) {
  el.dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
}

const TOGGLE_DEFAULTS = { action: 'toggle_connection', mode: 'gpn', transport: 'proxy', protocol: 'auto' };

test('dashboard: connect button posts toggle_connection with the current mode/transport/protocol', async () => {
  const dash = await bootDashboard();

  click(dash, dash.document.getElementById('connectBtn'));
  assert.deepEqual(dash.postsWith('toggle_connection').at(-1), TOGGLE_DEFAULTS,
    'main CONNECT button posts the current mode/transport/protocol');

  // Mobile quick connect is the same toggle.
  click(dash, dash.document.getElementById('mobileConnectBtn'));
  assert.deepEqual(dash.postsWith('toggle_connection').at(-1), TOGGLE_DEFAULTS,
    'mobile quick-connect posts the same toggle_connection');
  dash.close();
});

test('dashboard: setNexusConnected flips the CONNECT ring, label and status end-to-end', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const btn = document.getElementById('connectBtn');
  const btnText = document.getElementById('btnText');
  const btnSub = document.getElementById('btnSub');
  const ringArc = document.getElementById('ringArc');
  const statusLabel = document.getElementById('statusLabel');
  const statusDot = document.getElementById('statusDot');
  const sessionTime = document.getElementById('sessionTime');
  const mobile = document.getElementById('mobileConnectBtn');
  const orbitDot = document.querySelector('.ring-orbit i');

  // ---- idle baseline ----
  assert.ok(btnText.textContent.includes('CONNECT'), 'label reads CONNECT while idle');
  assert.ok(!document.body.classList.contains('connected'), 'body starts disconnected');
  assert.equal(ringArc.getAttribute('stroke'), 'var(--cyan)', 'idle ring arc is cyan');
  assert.equal(mobile.getAttribute('aria-label'), 'Connect', 'mobile quick-connect says Connect');

  // ---- connect: visual state must flip everywhere at once ----
  dash.window.setNexusConnected(true);
  assert.ok(document.body.classList.contains('connected'),
    'body.connected class drives the ring CSS (emerald orbits, core border)');
  assert.equal(btnText.textContent, 'CONNECTED', 'ring label switches to CONNECTED');
  assert.ok(btnSub.textContent.includes('ACTIVE'), 'sub-label gains the ACTIVE suffix');
  assert.equal(ringArc.getAttribute('stroke'), 'var(--emerald)', 'ring arc turns emerald');
  assert.ok(statusLabel.classList.contains('text-emerald-300'), 'status label goes emerald');
  assert.equal(statusDot.style.background, 'var(--emerald)', 'status dot turns emerald');
  assert.equal(sessionTime.textContent, '00:00:00', 'session timer resets');
  assert.ok(mobile.classList.contains('connected'), 'mobile quick-connect mirrors the ring');
  assert.equal(mobile.getAttribute('aria-label'), 'Disconnect', 'mobile quick-connect says Disconnect');
  assert.ok(orbitDot, 'ring orbit dot element exists for the CSS cascade');

  // ---- disconnect: everything reverts ----
  dash.window.setNexusConnected(false);
  assert.ok(!document.body.classList.contains('connected'), 'body.connected removed again');
  assert.ok(btnText.textContent.includes('CONNECT'), 'label reverts to CONNECT');
  assert.ok(!btnSub.textContent.includes('ACTIVE'), 'ACTIVE suffix removed');
  assert.equal(ringArc.getAttribute('stroke'), 'var(--cyan)', 'ring arc back to cyan');
  assert.ok(statusLabel.classList.contains('text-cyan-300'), 'status label back to cyan');
  assert.equal(statusDot.style.background, 'var(--cyan)', 'status dot back to cyan');
  assert.equal(mobile.getAttribute('aria-label'), 'Connect', 'mobile quick-connect reverts');
  dash.close();
});

test('dashboard: mode pills post set_connection_mode only while connected', async () => {
  const dash = await bootDashboard();
  const vpnPill = dash.document.querySelector('.mode-pill[data-mode="vpn"]');
  const gpnPill = dash.document.querySelector('.mode-pill[data-mode="gpn"]');
  assert.ok(vpnPill && gpnPill, 'mode pills exist');

  // Disconnected: the pill updates local state but must not bother the host.
  click(dash, vpnPill);
  assert.equal(dash.postsWith('set_connection_mode').length, 0, 'no host call while disconnected');

  // Connect (via the exposed setter), then the same click reaches the host.
  dash.window.setNexusConnected(true);
  click(dash, vpnPill);
  assert.deepEqual(dash.postsWith('set_connection_mode').at(-1), { action: 'set_connection_mode', mode: 'vpn' });

  click(dash, gpnPill);
  assert.deepEqual(dash.postsWith('set_connection_mode').at(-1), { action: 'set_connection_mode', mode: 'gpn' });
  dash.close();
});

test('dashboard: split-mode pills post set_split_mode and sync the active pill', async () => {
  const dash = await bootDashboard();
  const pill = (m) => dash.document.querySelector('.split-mode-pill[data-split-mode="' + m + '"]');
  ['off', 'vpn', 'manual'].forEach(m => assert.ok(pill(m), m + ' split-mode pill exists'));

  click(dash, pill('vpn'));
  assert.deepEqual(dash.postsWith('set_split_mode').at(-1), { action: 'set_split_mode', mode: 'vpn' });
  assert.ok(pill('vpn').classList.contains('active'), 'vpn pill becomes active');

  // manual is the GPN Game Tunnel (kill-switch) mode — only assigned apps are
  // tunneled, everything else stays direct.
  click(dash, pill('manual'));
  assert.deepEqual(dash.postsWith('set_split_mode').at(-1), { action: 'set_split_mode', mode: 'manual' });
  assert.ok(pill('manual').classList.contains('active'), 'manual pill becomes active');
  assert.ok(!pill('vpn').classList.contains('active'), 'previous pill loses active');

  click(dash, pill('off'));
  assert.deepEqual(dash.postsWith('set_split_mode').at(-1), { action: 'set_split_mode', mode: 'off' });
  assert.ok(pill('off').classList.contains('active'));
  dash.close();
});

test('dashboard: mode pill drives splitMode through applySplitMode (vpn -> vpn, gpn -> manual)', async () => {
  const dash = await bootDashboard();
  const modePill = (m) => dash.document.querySelector('.mode-pill[data-mode="' + m + '"]');
  const splitPill = (m) => dash.document.querySelector('.split-mode-pill[data-split-mode="' + m + '"]');
  const hint = dash.document.getElementById('boostModeHint');

  // The mode-pill click handler (app.js) runs applyMode() and then mirrors the
  // mode onto splitMode (gpn -> manual, vpn -> vpn) via applySplitMode(). The
  // split-mode pills, hint text and quick controls all re-sync from there.
  click(dash, modePill('vpn'));
  assert.ok(modePill('vpn').classList.contains('active'), 'vpn mode pill becomes active');
  assert.ok(splitPill('vpn').classList.contains('active'),
    'splitMode follows mode: Global VPN (vpn) mode activates the vpn split pill');
  assert.ok(!splitPill('off').classList.contains('active'), 'off split pill loses active');
  assert.ok(!splitPill('manual').classList.contains('active'), 'manual split pill stays inactive');
  assert.ok(hint.textContent.includes('Global VPN'), 'hint describes Global VPN routing');
  // A mode pill never posts set_split_mode itself — the split state is a local mirror.
  assert.equal(dash.postsWith('set_split_mode').length, 0, 'no set_split_mode posted by a mode pill');

  // gpn (GPN Game Tunnel) mode maps splitMode to manual — only assigned apps tunnel.
  click(dash, modePill('gpn'));
  assert.ok(modePill('gpn').classList.contains('active'), 'gpn mode pill becomes active');
  assert.ok(splitPill('manual').classList.contains('active'),
    'gpn mode activates the manual (GPN Game Tunnel) split pill');
  assert.ok(!splitPill('vpn').classList.contains('active'), 'vpn split pill loses active');
  assert.ok(hint.textContent.includes('GPN Game Tunnel'), 'hint describes GPN Game Tunnel routing');

  // Reverse direction: clicking a split-mode pill back-propagates into mode.
  click(dash, splitPill('vpn'));
  assert.ok(modePill('vpn').classList.contains('active'),
    'split pill vpn back-propagates mode to vpn via applySplitMode');
  assert.ok(splitPill('vpn').classList.contains('active'));
  dash.close();
});

// ---------------------------------------------------------------------------
// Skin host engine — applySkin() flips body[data-skin] / #skinHost / #appFrame
// ---------------------------------------------------------------------------

test('dashboard: applySkin swaps the host to the skin and back (body[data-skin] transition)', async () => {
  const dash = await bootDashboard();
  const { document, getComputedStyle } = dash.window;
  const body = document.body;
  const skinHost = document.getElementById('skinHost');
  const skinFrame = document.getElementById('skinFrame');
  const appFrame = document.getElementById('appFrame');
  assert.ok(skinHost && skinFrame && appFrame, 'skin host elements exist');

  // Boot: registry loaded from the real Temalar/skins.json -> standard.
  assert.equal(body.getAttribute('data-skin'), 'standard');
  assert.equal(skinHost.hidden, true, 'host hidden at boot');
  assert.equal(getComputedStyle(appFrame).display, 'flex', 'classic UI visible');

  // Activate CYBER through the real engine.
  dash.window.applySkin('cyber');
  assert.equal(body.getAttribute('data-skin'), 'cyber', 'body[data-skin] mirrors the engine state');
  assert.equal(skinHost.hidden, false, 'host container revealed');
  assert.ok(skinFrame.getAttribute('src').includes('skins/cyber/skin.html'), 'iframe loads the skin file');
  assert.equal(getComputedStyle(skinHost).display, 'block', 'host covers the window (CSS rule active)');
  assert.equal(getComputedStyle(appFrame).display, 'none', 'app frame hidden while the skin is active');
  assert.equal(dash.window.localStorage.getItem('aogpn.skin'), 'cyber', 'active skin persisted');
  assert.ok(document.querySelector('#topSkinRow [data-skin="cyber"]').classList.contains('active'), 'picker button active');
  assert.equal(document.getElementById('skinBadgeName').textContent, 'CYBER', 'title-bar badge follows');

  // Back to the classic dashboard.
  dash.window.applySkin('standard');
  assert.equal(body.getAttribute('data-skin'), 'standard');
  assert.equal(skinHost.hidden, true, 'host hidden again');
  assert.equal(getComputedStyle(appFrame).display, 'flex', 'classic UI back');
  assert.equal(getComputedStyle(skinHost).display, 'none', 'host hidden via CSS');
  assert.equal(dash.window.localStorage.getItem('aogpn.skin'), 'standard', 'standard persisted');
  assert.equal(document.getElementById('skinBadgeName').textContent, 'Standard');
  dash.close();
});

// ---------------------------------------------------------------------------
// Kill-switch flow — manual split mode + blacklist direction + boost table
// ---------------------------------------------------------------------------

test('dashboard: kill-switch flow — manual split mode + blacklist inverts the boost routes', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  dash.openView('boost');
  dash.updateMonitorSnapshot({
    connections: [],
    apps: [
      { id: 1, processName: 'cs2.exe', displayName: 'Counter-Strike 2', action: 'direct', isRunning: true },
      { id: 2, processName: 'eft.exe', displayName: 'Escape from Tarkov', action: 'vpn', isRunning: true },
      { id: 3, processName: 'chrome.exe', displayName: 'Chrome', action: 'block', isRunning: true }
    ]
  });
  const splitPill = (m) => document.querySelector('.split-mode-pill[data-split-mode="' + m + '"]');
  const dirPill = (d2) => document.querySelector('.dir-pill[data-dir="' + d2 + '"]');
  const modePill = (m) => document.querySelector('.mode-pill[data-mode="' + m + '"]');
  const hint = document.getElementById('boostModeHint');
  const dirNotice = document.getElementById('boostDirNotice');
  const btnSub = document.getElementById('btnSub');
  const rowOf = (pname) => dash.rows().find(r => r.dataset.processName === pname);

  // --- GPN Game Tunnel (manual) split mode: the kill switch ---
  click(dash, splitPill('manual'));
  assert.ok(splitPill('manual').classList.contains('active'), 'manual split pill active');
  assert.ok(modePill('gpn').classList.contains('active'), 'splitMode back-propagates mode to gpn');
  assert.ok(hint.textContent.includes('GPN Game Tunnel'), 'hint describes the kill switch');
  assert.deepEqual(dash.postsWith('set_split_mode').at(-1), { action: 'set_split_mode', mode: 'manual' });
  // Whitelist (default direction): no blacklist notice yet.
  assert.ok(dirNotice.classList.contains('hidden'), 'whitelist: no blacklist notice');

  // --- Blacklist direction: assigned apps stay direct, everything else tunnels ---
  click(dash, dirPill('blacklist'));
  assert.ok(dirPill('blacklist').classList.contains('active'), 'blacklist pill active');
  assert.deepEqual(dash.postsWith('set_split_direction').at(-1),
    { action: 'set_split_direction', invert: 'true' });
  assert.ok(!dirNotice.classList.contains('hidden'), 'blacklist notice visible in the boost table');

  // The route column shows the EFFECTIVE route (inverted display):
  //   direct-assigned app -> tunneled (VPN), vpn-assigned app -> direct,
  //   block stays block. The amber ⇄ marker tags the inversion.
  const cs2 = rowOf('cs2.exe');
  const eft = rowOf('eft.exe');
  const chrome = rowOf('chrome.exe');
  assert.ok(cs2 && eft && chrome, 'all three boost rows render');
  assert.ok(cs2.textContent.includes('VPN'), 'cs2 (direct) shows effective VPN');
  assert.ok(eft.textContent.includes('Direct'), 'eft (vpn) shows effective Direct');
  assert.ok(chrome.textContent.includes('Block'), 'chrome (block) stays Block');
  assert.ok(cs2.querySelector('.route-badge + span'), 'inverted row carries the ⇄ BLACKLIST marker');
  assert.ok(cs2.textContent.includes('⇄'), 'marker glyph present');
  // The route SELECT keeps the ORIGINAL assignment — the flip is display-only.
  assert.equal(dash.getRouteSelect('cs2.exe').value, 'direct', 'select keeps the real assignment');
  assert.equal(dash.getRouteSelect('eft.exe').value, 'vpn');

  // --- Connection ring reflects the kill-switch mode once connected ---
  dash.window.setNexusConnected(true);
  assert.ok(btnSub.textContent.includes('GPN Game Tunnel'), 'ring sub-label shows the kill-switch mode');
  assert.ok(btnSub.textContent.includes('ACTIVE'), 'connected suffix present');
  dash.window.setNexusConnected(false);

  // --- Back to whitelist: routes and notice revert ---
  click(dash, dirPill('whitelist'));
  assert.ok(dirPill('whitelist').classList.contains('active'), 'whitelist pill active again');
  assert.ok(dirNotice.classList.contains('hidden'), 'blacklist notice gone');
  assert.ok(rowOf('cs2.exe').textContent.includes('Direct'), 'cs2 effective route reverts to Direct');
  assert.ok(rowOf('eft.exe').textContent.includes('VPN'), 'eft reverts to VPN');
  dash.close();
});

test('dashboard: blacklist rows carry inversion chips + direction-aware route labels', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  dash.openView('boost');
  dash.updateMonitorSnapshot({
    connections: [],
    apps: [
      { id: 1, processName: 'cs2.exe', displayName: 'Counter-Strike 2', action: 'direct', isRunning: true },
      { id: 2, processName: 'eft.exe', displayName: 'Escape from Tarkov', action: 'vpn', isRunning: true },
      { id: 3, processName: 'chrome.exe', displayName: 'Chrome', action: 'block', isRunning: true }
    ]
  });
  const splitPill = (m) => document.querySelector('.split-mode-pill[data-split-mode="' + m + '"]');
  const dirPill = (d2) => document.querySelector('.dir-pill[data-dir="' + d2 + '"]');
  const rowOf = (pname) => dash.rows().find(r => r.dataset.processName === pname);

  // Whitelist baseline: no inversion chips, plain route labels.
  assert.ok(!rowOf('eft.exe').textContent.includes('⇄'), 'whitelist: no inversion chip');
  const whitelistLabels = () => Array.from(dash.getRouteSelect('eft.exe').options).map(o => o.textContent);
  assert.ok(whitelistLabels().includes('VPN'), 'whitelist: plain VPN label');
  assert.ok(!whitelistLabels().some(l => l.includes('tunnel')), 'whitelist: no direction suffix on labels');

  // Blacklist direction: the route column must spell out the inversion inline.
  click(dash, splitPill('manual'));
  click(dash, dirPill('blacklist'));

  // eft (vpn assignment) is EXCLUDED — the chip says it stays outside the tunnel.
  const eft = rowOf('eft.exe');
  const eftChip = eft.querySelector('.route-badge + span');
  assert.ok(eftChip, 'excluded row carries an inline chip right after the route badge');
  assert.ok(eftChip.textContent.includes('Outside tunnel'),
    'chip says the app stays OUTSIDE the tunnel (the ' + eftChip.textContent + ' label)');
  assert.ok(eftChip.textContent.includes('⇄'), 'chip carries the inversion glyph');

  // cs2 (direct assignment) is tunneled — the chip says so.
  const cs2Chip = rowOf('cs2.exe').querySelector('.route-badge + span');
  assert.ok(cs2Chip && cs2Chip.textContent.includes('Tunneled'),
    'direct-assigned row is marked as tunneled (got: ' + (cs2Chip && cs2Chip.textContent) + ')');

  // block is not inverted — no chip.
  assert.ok(!rowOf('chrome.exe').textContent.includes('⇄'), 'block row carries no inversion chip');

  // The route dropdown labels explain the effective meaning; the VALUES keep the
  // real assignment (the flip is display-only).
  const eftLabels = Array.from(dash.getRouteSelect('eft.exe').options).map(o => o.textContent);
  assert.ok(eftLabels.includes('VPN · outside tunnel'), 'VPN option explains the exclusion');
  assert.equal(dash.getRouteSelect('eft.exe').value, 'vpn', 'select keeps the real assignment');
  const cs2Labels = Array.from(dash.getRouteSelect('cs2.exe').options).map(o => o.textContent);
  assert.ok(cs2Labels.includes('Direct · tunneled'), 'Direct option explains the tunneling');
  assert.equal(dash.getRouteSelect('cs2.exe').value, 'direct');

  // The table banner explains the same VPN-assignment inversion.
  assert.ok(document.getElementById('boostDirNoticeText').textContent.includes('OUTSIDE the tunnel'),
    'banner explains that a VPN assignment keeps the app outside the tunnel');

  // Back to whitelist: chips and direction-aware labels revert.
  click(dash, dirPill('whitelist'));
  assert.ok(!rowOf('eft.exe').textContent.includes('⇄'), 'whitelist: chip gone again');
  assert.ok(whitelistLabels().includes('VPN'), 'whitelist: plain label restored');
  assert.ok(!whitelistLabels().some(l => l.includes('tunnel')), 'whitelist: direction suffix gone');
  dash.close();
});

// ---------------------------------------------------------------------------
// Host echo — updateMonitorSnapshot drives the mode/split/direction pills
// ---------------------------------------------------------------------------

test('dashboard: updateMonitorSnapshot host echo drives the mode/split pills live', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const splitPill = (m) => document.querySelector('.split-mode-pill[data-split-mode="' + m + '"]');
  const modePill = (m) => document.querySelector('.mode-pill[data-mode="' + m + '"]');
  const dirPill = (d2) => document.querySelector('.dir-pill[data-dir="' + d2 + '"]');
  const hint = document.getElementById('boostModeHint');
  const dirHint = document.getElementById('dirHint');

  // Boot: off split mode, gpn connection mode, whitelist direction.
  assert.ok(splitPill('off').classList.contains('active'), 'boot split mode off');
  assert.ok(modePill('gpn').classList.contains('active'), 'boot connection mode gpn');
  assert.ok(dirPill('whitelist').classList.contains('active'), 'boot direction whitelist');

  // Host echoes manual (GPN Game Tunnel): the split pill AND the connection
  // mode pill follow live (applySplitMode back-propagates manual -> gpn).
  dash.updateMonitorSnapshot({ mode: 'manual' });
  assert.ok(splitPill('manual').classList.contains('active'), 'manual split pill activates from the echo');
  assert.ok(!splitPill('off').classList.contains('active'), 'off split pill deactivates');
  assert.ok(modePill('gpn').classList.contains('active'), 'manual back-propagates connection mode to gpn');
  assert.ok(hint.textContent.includes('GPN Game Tunnel'), 'boost hint follows the echo');
  // An inbound echo never re-posts anything back to the host.
  assert.equal(dash.postsWith('set_split_mode').length, 0, 'echo does not re-post set_split_mode');
  assert.equal(dash.postsWith('set_connection_mode').length, 0, 'echo does not re-post set_connection_mode');

  // Host echoes vpn: both pills go vpn (applySplitMode -> applyMode).
  dash.updateMonitorSnapshot({ mode: 'vpn' });
  assert.ok(splitPill('vpn').classList.contains('active'), 'vpn split pill activates from the echo');
  assert.ok(modePill('vpn').classList.contains('active'), 'vpn back-propagates connection mode to vpn');

  // Host echoes off: split pill returns, the connection mode is untouched.
  dash.updateMonitorSnapshot({ mode: 'off' });
  assert.ok(splitPill('off').classList.contains('active'), 'off split pill returns');
  assert.ok(modePill('vpn').classList.contains('active'), 'off does not touch the connection mode');

  // Invalid split value: the current state is preserved, not corrupted.
  dash.updateMonitorSnapshot({ mode: 'bogus' });
  assert.ok(splitPill('off').classList.contains('active'), 'invalid echo keeps the current split mode');
  assert.equal(dash.postsWith('set_split_mode').length, 0);

  // invertManualRouting echo drives the direction pills live.
  dash.updateMonitorSnapshot({ mode: 'manual', invertManualRouting: true });
  assert.ok(dirPill('blacklist').classList.contains('active'), 'blacklist direction activates from the echo');
  assert.ok(!dirPill('whitelist').classList.contains('active'), 'whitelist deactivates');
  assert.ok(dirHint.textContent.includes('Blacklist'), 'direction hint follows the echo');
  dash.close();
});

// ---------------------------------------------------------------------------
// Connected telemetry + session timer (live host updates in jsdom)
// ---------------------------------------------------------------------------

test('dashboard: connected telemetry gauges and the session timer update live', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const el = (id) => document.getElementById(id);
  const pingVal = el('pingVal'), pingSub = el('pingSub');
  const lossVal = el('lossVal'), lossSub = el('lossSub');
  const downVal = el('downVal'), upVal = el('upVal');
  const downGauge = el('downGauge'), upGauge = el('upGauge');
  const sessionTime = el('sessionTime');
  const FULL_CIRCLE = 276.46;

  // Disconnected: honest measuring state.
  assert.equal(pingVal.textContent, '--');
  assert.equal(lossVal.textContent, '--');

  // Connect: the session timer starts at zero; two intervals are registered
  // (session timer + the IP re-check loop that keeps the IP panel fresh).
  dash.window.setNexusConnected(true);
  assert.equal(sessionTime.textContent, '00:00:00');
  assert.equal(dash.tickIntervals(), 2, 'two intervals on connect (session timer + IP re-check)');
  assert.equal(sessionTime.textContent, '00:00:01', 'session clock advances one second per tick');
  assert.equal(dash.tickIntervals(), 2);
  assert.equal(sessionTime.textContent, '00:00:02');

  // First host telemetry sample: in GPN mode the live HTTP ping is NOT written
  // to the ping card (it measures CDN latency outside the tunnel, not the
  // tunnel delay — the authoritative value comes from setGpnConnectionInfo).
  // Loss/throughput still update live and the gauges fill.
  dash.window.updateTelemetry(42, 0.5, 8.2, 3.1);
  assert.equal(pingVal.textContent, '--', 'GPN mode keeps the ping card honest while live samples stream');
  assert.equal(lossVal.textContent, '0.5');
  assert.equal(lossSub.textContent, 'warning', 'loss > 0 -> warning');
  assert.equal(downVal.textContent, '8.2');
  assert.equal(upVal.textContent, '3.1');
  const downOffset = parseFloat(downGauge.style.strokeDashoffset);
  assert.ok(downOffset > 0 && downOffset < FULL_CIRCLE, 'down gauge fills (offset ' + downOffset + ')');
  assert.equal(downGauge.style.color, 'var(--cyan)');
  assert.equal(upGauge.style.color, 'var(--violet)');
  assert.ok(/^\d+$/.test(el('downGaugeMax').textContent), 'gauge scale label calibrates');

  // The GPN decision (server selection measurement) is what owns the ping card.
  dash.window.setGpnConnectionInfo({ server: 'Almanya', delayMs: 42, mode: 'WireGuard' });
  assert.equal(pingVal.textContent, '42', 'GPN decision writes the measured latency');
  assert.ok(pingSub.textContent.includes('Almanya'), 'ping sub shows the selected server');
  assert.ok(pingSub.textContent.includes('WireGuard'), 'ping sub shows the handshake protocol');

  // Second live sample: loss still updates live, gauges re-target, and the
  // GPN latency stays authoritative on the ping card.
  dash.window.updateTelemetry(15, 0, 12.5, 6.0);
  assert.equal(pingVal.textContent, '42', 'GPN latency survives later live telemetry samples');
  assert.equal(lossSub.textContent, 'stable', '0 loss -> stable');
  assert.equal(downVal.textContent, '12.5');

  // The session clock keeps running alongside telemetry (session + IP re-check).
  assert.equal(dash.tickIntervals(), 2);
  assert.equal(sessionTime.textContent, '00:00:03');

  // Disconnect: telemetry resets and the session timer is cleared.
  dash.window.setNexusConnected(false);
  assert.equal(pingVal.textContent, '--');
  assert.equal(lossVal.textContent, '--');
  assert.equal(downVal.textContent, '0');
  assert.equal(upVal.textContent, '0');
  assert.equal(parseFloat(downGauge.style.strokeDashoffset), FULL_CIRCLE, 'gauge back to empty');
  assert.equal(dash.tickIntervals(), 0, 'session timer cleared on disconnect');
  assert.equal(sessionTime.textContent, '00:00:03', 'session clock freezes');
  dash.close();
});

// ---------------------------------------------------------------------------
// Disconnect cleanup — telemetry reset + IP banner hiding
// ---------------------------------------------------------------------------

test('dashboard: disconnecting resets telemetry and hides the IP verification banners', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const el = (id) => document.getElementById(id);
  const pingVal = el('pingVal'), lossVal = el('lossVal');
  const downVal = el('downVal'), upVal = el('upVal');
  const downGauge = el('downGauge');
  const leakBanner = el('ipLeakBanner'), okBanner = el('ipOkBanner');
  const verifyText = el('ipVerifyText'), verifyDetail = el('ipVerifyDetail');
  const ipDisplay = el('ipDisplay');
  const FULL_CIRCLE = 276.46;

  // --- Connected + tunnel verified: success banner up, telemetry live ---
  dash.window.setNexusConnected(true);
  dash.window.setRealIpState({
    connected: true, tunnelVerified: true, transport: 'tun',
    tunnelIp: '5.6.7.8', tunnelCountry: 'DE',
    ispIp: '1.2.3.4', ispCountry: 'TR'
  });
  // In GPN mode the live sample does not own the ping card (the authoritative
  // latency comes from the server-selection decision), so feed it first.
  dash.window.setGpnConnectionInfo({ server: 'Almanya', delayMs: 42, mode: 'WireGuard' });
  dash.window.updateTelemetry(42, 0.5, 8.2, 3.1);
  assert.ok(!okBanner.classList.contains('hidden'), 'tunnel-verified: success banner visible');
  assert.ok(leakBanner.classList.contains('hidden'), 'no leak while tunnel verified');
  assert.ok(verifyText.textContent.includes('Tunneled'), 'verification reads Tunneled');
  assert.equal(ipDisplay.textContent, '5.6.7.8', 'exit IP shown');
  assert.equal(pingVal.textContent, '42');
  assert.equal(downVal.textContent, '8.2');

  // --- Leak state pushed: leak banner up, success banner down ---
  // Confirmed leak = tunnel IP matches the ISP IP with a cached baseline.
  dash.window.setRealIpState({
    connected: true, tunnelVerified: false,
    tunnelIp: '1.2.3.4', tunnelCountry: 'TR',
    ispIp: '1.2.3.4', ispCached: true
  });
  assert.ok(!leakBanner.classList.contains('hidden'), 'confirmed leak: leak banner visible');
  assert.ok(okBanner.classList.contains('hidden'), 'success banner down while leaking');
  assert.ok(verifyText.textContent.includes('Leaking'), 'verification reads Leaking');

  // --- Disconnect: everything honest again ---
  dash.window.setNexusConnected(false);
  // IP banners cleared (stale tunnel-verified state must not linger).
  assert.ok(leakBanner.classList.contains('hidden'), 'leak banner hidden on disconnect');
  assert.ok(okBanner.classList.contains('hidden'), 'success banner hidden on disconnect');
  assert.ok(verifyText.textContent.includes('Idle'), 'verification resets to Idle');
  assert.ok(verifyText.className.includes('text-slate-400'), 'idle text dimmed');
  assert.equal(verifyDetail.textContent, 'not connected', 'detail resets to not connected');
  // Telemetry reset to the measuring state.
  assert.equal(pingVal.textContent, '--');
  assert.equal(lossVal.textContent, '--');
  assert.equal(downVal.textContent, '0');
  assert.equal(upVal.textContent, '0');
  assert.equal(parseFloat(downGauge.style.strokeDashoffset), FULL_CIRCLE, 'gauges back to empty');
  // The IP card keeps the last-known value (documented: prevents flicker, the
  // next host probe overwrites it) — it is not wiped to '--'.
  assert.equal(ipDisplay.textContent, '1.2.3.4', 'IP display keeps the last-known value');
  dash.close();
});

// ---------------------------------------------------------------------------
// IP doğrulama paneli — ölçüm tazeliği (measuredAt) + bayat sızıntı düşüşü
// ---------------------------------------------------------------------------

test('IP verify shows measurement age and downgrades stale leak to re-checking', async (t) => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const el = (id) => document.getElementById(id);
  const verifyText = el('ipVerifyText'), verifyDetail = el('ipVerifyDetail');
  const leakBanner = el('ipLeakBanner');

  dash.window.setNexusConnected(true);

  // Sandbox saati (dash.clock.now) sabittir — measuredAt ONUNLA üretilir ki
  // yaş deterministik olsun (gerçek Node zamanı kullanılırsa yaş hep 0 kalır).
  const clockNow = dash.clock.now;

  // --- Fresh measurement while tunnel verified: age suffix shown, no leak ---
  dash.window.setRealIpState({
    connected: true, tunnelVerified: true, transport: 'tun',
    tunnelIp: '5.6.7.8', tunnelCountry: 'DE',
    ispIp: '1.2.3.4', ispCountry: 'TR',
    measuredAt: new Date(clockNow - 2000).toISOString()
  });
  assert.ok(verifyText.textContent.includes('Tunneled'), 'verified state kept');
  assert.ok(verifyDetail.textContent.includes('2s ago'), 'detail shows measurement age');
  assert.ok(leakBanner.classList.contains('hidden'), 'no leak while verified');

  // --- Fresh leak measurement (< 45 s): still red Leaking (current fact) ---
  dash.window.setRealIpState({
    connected: true, tunnelVerified: false,
    tunnelIp: '1.2.3.4', tunnelCountry: 'TR',
    ispIp: '1.2.3.4', ispCached: true,
    measuredAt: new Date(clockNow - 5000).toISOString()
  });
  assert.ok(verifyText.textContent.includes('Leaking'), 'fresh leak stays Leaking');
  assert.ok(verifyText.className.includes('text-red-300'), 'fresh leak is red');

  // --- Stale leak measurement (> 45 s): downgraded to amber re-checking ---
  // Bayat ölçüm (tünel hazır olmadan alınan ISP IP'si) asla kesin 'Leaking'
  // olarak sunulmaz — panel 'yeniden ölçülüyor' der, host yeniden ölçünce düzelir.
  dash.window.setRealIpState({
    connected: true, tunnelVerified: false,
    tunnelIp: '1.2.3.4', tunnelCountry: 'TR',
    ispIp: '1.2.3.4', ispCached: true,
    measuredAt: new Date(clockNow - 60000).toISOString()
  });
  assert.ok(verifyText.textContent.includes('Retrying'), 'stale leak shows retrying');
  assert.ok(verifyText.className.includes('text-amber-300'), 'stale leak is amber');
  assert.ok(verifyDetail.textContent.includes('Stale'), 'stale leak explains re-check');

  // --- Host re-measures: fresh state restores the red leak ---
  dash.window.setRealIpState({
    connected: true, tunnelVerified: false,
    tunnelIp: '1.2.3.4', tunnelCountry: 'TR',
    ispIp: '1.2.3.4', ispCached: true,
    measuredAt: new Date(clockNow - 1000).toISOString()
  });
  assert.ok(verifyText.textContent.includes('Leaking'), 'fresh re-measure restores Leaking');

  dash.window.setNexusConnected(false);
  dash.close();
});

// ---------------------------------------------------------------------------
// Connecting (intermediate) state — amber CONNECTING label + button lock
// ---------------------------------------------------------------------------

test('dashboard: connecting state locks CONNECT with an amber CONNECTING label', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const btn = document.getElementById('connectBtn');
  const btnText = document.getElementById('btnText');
  const btnSub = document.getElementById('btnSub');
  const btnIcon = document.getElementById('btnIcon');
  const statusLabel = document.getElementById('statusLabel');
  const statusDot = document.getElementById('statusDot');
  const ringArc = document.getElementById('ringArc');
  const mobile = document.getElementById('mobileConnectBtn');

  // Idle baseline.
  assert.ok(btnText.textContent.includes('CONNECT'), 'idle label CONNECT');
  assert.equal(btn.disabled, false, 'idle: CONNECT enabled');
  assert.equal(mobile.getAttribute('aria-label'), 'Connect');

  // Host signals an in-flight attempt.
  dash.window.setNexusConnecting(true);
  assert.equal(btnText.textContent, 'CONNECTING', 'ring label switches to CONNECTING');
  assert.ok(btnSub.textContent.includes('Connecting'), 'sub-label gains the Connecting suffix');
  assert.equal(btnIcon.style.color, 'var(--amber, #f59e0b)', 'ring icon turns amber');
  assert.ok(statusLabel.classList.contains('text-amber-300'), 'status label amber');
  assert.equal(statusLabel.textContent, 'Connecting');
  assert.equal(statusDot.style.background, 'var(--amber, #f59e0b)', 'status dot amber');
  assert.equal(ringArc.getAttribute('stroke'), 'var(--cyan)', 'ring arc stays cyan while connecting');
  assert.equal(mobile.getAttribute('aria-label'), 'Connecting', 'mobile quick-connect says Connecting');
  assert.equal(mobile.style.color, 'var(--amber, #f59e0b)');
  // The button is locked so a second press cannot double-toggle the tunnel.
  assert.equal(btn.disabled, true, 'CONNECT locked while connecting');
  assert.equal(btn.getAttribute('aria-disabled'), 'true');
  assert.equal(mobile.disabled, true, 'mobile quick-connect locked too');
  assert.ok(!document.body.classList.contains('connected'), 'not yet connected');
  // Inbound state change posts nothing back.
  assert.equal(dash.postsWith('toggle_connection').length, 0, 'connecting never posts toggle_connection');

  // The attempt resolves to connected: the lock lifts and the ring goes live.
  dash.window.setNexusConnected(true);
  assert.equal(btn.disabled, false, 'CONNECT unlocked once connected');
  assert.equal(btnText.textContent, 'CONNECTED');
  assert.equal(ringArc.getAttribute('stroke'), 'var(--emerald)');

  // And back through idle.
  dash.window.setNexusConnecting(true);
  assert.equal(btn.disabled, true, 're-locks during the next attempt');
  dash.window.setNexusConnecting(false);
  assert.ok(btnText.textContent.includes('CONNECT'), 'back to idle label');
  assert.equal(btn.disabled, false, 'unlocked at idle');
  assert.equal(btnIcon.style.color, 'var(--cyan)', 'icon back to cyan');
  assert.equal(mobile.getAttribute('aria-label'), 'Connect');
  dash.close();
});

// ---------------------------------------------------------------------------
// Transport pills — proxy/tun selection and the TUN elevation lock
// ---------------------------------------------------------------------------

const transportPill = (dash, t) => dash.document.querySelector('.transport-pill[data-transport="' + t + '"]');

test('dashboard: transport pills post set_transport with the picked transport', async () => {
  const dash = await bootDashboard();
  click(dash, transportPill(dash, 'tun'));
  assert.deepEqual(dash.postsWith('set_transport').at(-1), { action: 'set_transport', transport: 'tun' });
  click(dash, transportPill(dash, 'proxy'));
  assert.deepEqual(dash.postsWith('set_transport').at(-1), { action: 'set_transport', transport: 'proxy' });
  dash.close();
});

test('dashboard: TUN pill never posts set_transport without elevation, and locks CONNECT', async () => {
  const dash = await bootDashboard();
  dash.window.setAdminState(false);
  click(dash, transportPill(dash, 'tun'));
  assert.equal(dash.postsWith('set_transport').length, 0,
    'TUN is never sent to the host without elevation (it would be rejected anyway)');

  const tunPill = dash.document.getElementById('tunPill');
  const lockBadge = dash.document.getElementById('tunLockBadge');
  const btn = dash.document.getElementById('connectBtn');
  assert.ok(tunPill.classList.contains('locked'), 'tun pill shows the locked state');
  assert.ok(!lockBadge.classList.contains('hidden'), 'LOCKED badge is visible');
  assert.equal(btn.disabled, true, 'CONNECT is disabled while TUN lacks elevation');
  assert.equal(btn.getAttribute('aria-disabled'), 'true');

  // Elevation arrives -> the lock clears and the pill is live again.
  dash.window.setAdminState(true);
  assert.ok(!tunPill.classList.contains('locked'), 'lock clears once elevated');
  assert.ok(lockBadge.classList.contains('hidden'), 'LOCKED badge hides again');
  assert.equal(btn.disabled, false);
  click(dash, transportPill(dash, 'proxy'));
  assert.deepEqual(dash.postsWith('set_transport').at(-1), { action: 'set_transport', transport: 'proxy' });
  dash.close();
});

test('dashboard: Global VPN mode ignores transport pill clicks (locked to TUN)', async () => {
  const dash = await bootDashboard();
  click(dash, dash.document.querySelector('.mode-pill[data-mode="vpn"]'));
  click(dash, transportPill(dash, 'tun'));
  click(dash, transportPill(dash, 'proxy'));
  assert.equal(dash.postsWith('set_transport').length, 0,
    'Global VPN fixes transport to TUN — pill clicks are ignored');
  dash.close();
});

test('dashboard: Global VPN forces TUN transport; back to GPN frees it', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const modePill2 = (m) => document.querySelector('.mode-pill[data-mode="' + m + '"]');
  const tunPill = document.getElementById('tunPill');
  const proxyPill = document.querySelector('.transport-pill[data-transport="proxy"]');
  const proxyLock = document.getElementById('proxyLockBadge');
  const capLock = document.getElementById('vpnCaptureLockNotice');
  const capLockText = document.getElementById('vpnCaptureLockText');
  assert.ok(tunPill && proxyPill && proxyLock && capLock && capLockText, 'lock elements exist');

  // GPN boot state: transport free, proxy selected.
  assert.ok(proxyPill.classList.contains('active'), 'proxy is the active transport at boot');
  assert.equal(tunPill.disabled, false, 'transport pills enabled in GPN mode');
  assert.ok(proxyLock.classList.contains('hidden'), 'no proxy lock badge at boot');
  assert.ok(capLock.classList.contains('hidden'), 'no capture lock notice at boot');

  // Global VPN -> applyMode() forces transport to TUN and locks the pills.
  click(dash, modePill2('vpn'));
  assert.ok(tunPill.classList.contains('active'), 'TUN becomes the active transport (forced)');
  assert.ok(!proxyPill.classList.contains('active'), 'proxy pill loses active');
  assert.equal(tunPill.disabled, true, 'transport pills disabled under Global VPN');
  assert.equal(proxyPill.disabled, true);
  assert.ok(tunPill.classList.contains('opacity-50'), 'pills visually dimmed');
  assert.ok(!proxyLock.classList.contains('hidden'), 'proxy lock badge shown');
  assert.ok(!capLock.classList.contains('hidden'), 'capture lock notice shown');
  assert.ok(capLockText.textContent.includes('TUN'), 'notice explains the TUN lock');

  // The force is local: switching mode never posts set_transport, and CONNECT
  // in Global VPN carries the forced TUN.
  assert.equal(dash.postsWith('set_transport').length, 0,
    'the TUN force is a local applyMode effect, not a host call');
  dash.window.setNexusConnected(true);
  click(dash, modePill2('vpn'));
  assert.deepEqual(dash.postsWith('set_connection_mode').at(-1), { action: 'set_connection_mode', mode: 'vpn' });
  click(dash, document.getElementById('connectBtn'));
  assert.deepEqual(dash.postsWith('toggle_connection').at(-1),
    { action: 'toggle_connection', mode: 'vpn', transport: 'tun', protocol: 'auto' },
    'CONNECT in Global VPN always uses TUN');
  dash.window.setNexusConnected(false);

  // Back to GPN -> applyMode() frees the transport pills and hides the locks.
  click(dash, modePill2('gpn'));
  assert.equal(tunPill.disabled, false, 'transport pills re-enabled in GPN mode');
  assert.equal(proxyPill.disabled, false);
  assert.ok(!tunPill.classList.contains('opacity-50'), 'pills no longer dimmed');
  assert.ok(proxyLock.classList.contains('hidden'), 'proxy lock badge hidden again');
  assert.ok(capLock.classList.contains('hidden'), 'capture lock notice hidden again');
  assert.ok(tunPill.classList.contains('active'), 'TUN stays selected (forced value persists)');
  click(dash, proxyPill);
  assert.deepEqual(dash.postsWith('set_transport').at(-1),
    { action: 'set_transport', transport: 'proxy' }, 'proxy is selectable again');
  dash.close();
});

// ---------------------------------------------------------------------------
// Global VPN + locked TUN — system proxy pills stay independent, and CONNECT
// hands the proxy over to the tunnel (PROXY · CONNECTION ownership handoff)
// ---------------------------------------------------------------------------

test('dashboard: Global VPN locked TUN keeps proxy pills live; CONNECT hands proxy ownership to the tunnel', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const modePill = (m) => document.querySelector('.mode-pill[data-mode="' + m + '"]');
  const tunPill = document.getElementById('tunPill');
  const proxyPill = document.querySelector('.transport-pill[data-transport="proxy"]');
  const tunLockBadge = document.getElementById('tunLockBadge');
  const proxyLock = document.getElementById('proxyLockBadge');
  const capLock = document.getElementById('vpnCaptureLockNotice');
  const btn = document.getElementById('connectBtn');
  const proxyToggle = document.getElementById('systemProxyToggleBtn');
  const proxyLabel = document.getElementById('systemProxyLabel');
  const proxySelect = document.getElementById('systemProxyModeSelect');
  const tunProxyNotice = document.getElementById('tunProxyNotice');
  const toast = document.getElementById('nodeToast');
  assert.ok(tunPill && proxyPill && tunLockBadge && proxyLock && capLock && btn && proxyToggle && proxyLabel && proxySelect && tunProxyNotice && toast,
    'all lock / proxy elements exist in the real html');

  // ---- Phase 1: Global VPN + non-admin -> TUN elevation lock, proxy pills independent ----
  dash.window.setAdminState(false);
  click(dash, modePill('vpn'));

  // Mode lock: transport forced to TUN, both transport pills disabled + dimmed.
  assert.ok(tunPill.classList.contains('active'), 'TUN forced active in Global VPN');
  assert.equal(tunPill.disabled, true, 'transport pills disabled under Global VPN');
  assert.equal(proxyPill.disabled, true);
  assert.ok(!proxyLock.classList.contains('hidden'), 'proxy lock badge visible');
  assert.ok(!capLock.classList.contains('hidden'), 'capture lock notice visible');

  // Elevation lock: TUN without admin while disconnected -> LOCKED badge + dead CONNECT.
  assert.ok(tunPill.classList.contains('locked'), 'TUN pill shows the elevation lock');
  assert.ok(!tunLockBadge.classList.contains('hidden'), 'LOCKED badge visible');
  assert.equal(btn.disabled, true, 'CONNECT disabled while TUN lacks elevation');
  assert.equal(btn.getAttribute('aria-disabled'), 'true');

  // The system proxy pills are INDEPENDENT of the TUN lock: still enabled, and
  // their controls reach the host directly.
  assert.equal(proxyToggle.disabled, false, 'proxy toggle not disabled by the TUN lock');
  assert.equal(proxySelect.disabled, false, 'proxy mode select not disabled by the TUN lock');
  click(dash, proxyToggle);
  assert.deepEqual(dash.postsWith('toggle_system_proxy').at(-1),
    { action: 'toggle_system_proxy' }, 'toggle reaches the host while TUN is locked');
  proxySelect.value = '3';
  proxySelect.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  assert.deepEqual(dash.postsWith('set_system_proxy_mode').at(-1),
    { action: 'set_system_proxy_mode', mode: 3 }, 'mode select reaches the host while TUN is locked');

  // Independent PROXY ON + TUN selected -> the notice explains TUN won't touch it.
  dash.window.setSystemProxyState(3, 3, false);
  assert.ok(!tunProxyNotice.classList.contains('hidden'), 'TUN/proxy independence notice shown');
  assert.ok(!proxyToggle.classList.contains('owned'), 'not owned while disconnected');

  // The TUN force is local: no transport was ever sent to the host.
  assert.equal(dash.postsWith('set_transport').length, 0);

  // ---- Phase 2: elevation + CONNECT -> the tunnel takes over the system proxy ----
  dash.window.setAdminState(true);
  assert.ok(!tunPill.classList.contains('locked'), 'elevation clears the TUN lock');
  assert.ok(tunLockBadge.classList.contains('hidden'), 'LOCKED badge hides');
  assert.equal(btn.disabled, false, 'CONNECT enabled again');

  // Capture the toast channel so the handoff can be asserted to fire ONCE.
  const notifications = [];
  const origNotify = dash.window.notifyNodes;
  dash.window.notifyNodes = (m) => { notifications.push(m); origNotify(m); };

  dash.window.setNexusConnected(true);
  assert.equal(tunPill.disabled, true, 'pills stay mode-locked under Global VPN while connected');
  assert.equal(btn.disabled, false, 'CONNECT is a live disconnect toggle while connected');
  assert.ok(tunProxyNotice.classList.contains('hidden'), 'independence notice hides once connected');

  // The host hands the system proxy over to the active tunnel.
  dash.window.setSystemProxyState(3, 3, true);
  assert.ok(proxyToggle.classList.contains('owned'), 'toggle marked owned (PROXY · CONNECTION)');
  assert.equal(proxyLabel.textContent, 'PROXY · CONNECTION', 'badge reflects tunnel ownership');
  assert.ok(proxyToggle.title.includes('handed the system proxy to the active tunnel'),
    'tooltip explains the handoff');
  assert.equal(notifications.length, 1, 'handoff toast fires exactly once');
  assert.ok(notifications[0].includes('PROXY · CONNECTION'), 'toast names the new badge');
  assert.ok(!toast.classList.contains('hidden'), 'toast is visible');

  // Re-pushing the same owned state must NOT toast again (becameConnectionOwned guard).
  dash.window.setSystemProxyState(3, 3, true);
  assert.equal(notifications.length, 1, 'idempotent owned push does not re-toast');

  // Disconnect -> the tunnel releases the proxy; the independent preference returns.
  dash.window.setNexusConnected(false);
  dash.window.setSystemProxyState(3, 3, false);
  assert.ok(!proxyToggle.classList.contains('owned'), 'ownership released on disconnect');
  assert.equal(proxyLabel.textContent, 'PROXY · PAC', 'independent PROXY preference restored');
  assert.ok(proxyToggle.title.includes('until you disconnect'),
    'tooltip explains the preference returns after disconnect');
  dash.close();
});

// ---------------------------------------------------------------------------
// CONNECT message matrix — 2 modes × 2 transports × 3 split modes
// ---------------------------------------------------------------------------
// The requested combos are driven through the REAL controls (transport pill,
// split pill, mode pill) in a canonical order. Because the split and mode pills
// are ONE shared route mode (a split click back-propagates the mode, a mode
// click mirrors the split), some requested combos resolve differently — each
// row documents its resolution. What CONNECT sends must ALWAYS mirror the live
// UI: mode + transport in toggle_connection, splitMode delivered separately via
// set_split_mode (the host contract — toggle_connection carries no split key).

function clickSel(dash, sel) {
  const el = dash.document.querySelector(sel);
  assert.ok(el, 'click target exists: ' + sel);
  el.dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
}

// Attempt the requested (mode, transport, split) via real clicks and assert the
// resolution: active pills, the toggle_connection payload and the set_split_mode
// delivery all match.
async function assertConnectMatrixRow(R_mode, R_transport, R_split, rMode, rTransport, rSplit, why) {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const active = (sel) => document.querySelector(sel).classList.contains('active');

  // 1) Transport first — only meaningful while the boot mode (gpn) allows it.
  clickSel(dash, '.transport-pill[data-transport="' + R_transport + '"]');
  // 2) The split pill — vpn/manual back-propagate the shared route mode.
  clickSel(dash, '.split-mode-pill[data-split-mode="' + R_split + '"]');
  // 3) The mode pill — only when the split did not already settle the requested
  //    mode ('off' never settles a mode; the boot mode is already gpn).
  const modeFromSplit = R_split === 'vpn' ? 'vpn' : R_split === 'manual' ? 'gpn' : null;
  const needsModePill = R_split === 'off' ? R_mode !== 'gpn' : modeFromSplit !== R_mode;
  if (needsModePill) clickSel(dash, '.mode-pill[data-mode="' + R_mode + '"]');

  // The CONNECT payload must mirror the resolved UI state.
  assert.ok(active('.mode-pill[data-mode="' + rMode + '"]'),
    '[' + R_mode + '/' + R_transport + '/' + R_split + '] mode pill shows ' + rMode + ' — ' + why);
  assert.ok(active('.split-mode-pill[data-split-mode="' + rSplit + '"]'),
    '[' + R_mode + '/' + R_transport + '/' + R_split + '] split pill shows ' + rSplit + ' — ' + why);
  assert.ok(active('.transport-pill[data-transport="' + rTransport + '"]'),
    '[' + R_mode + '/' + R_transport + '/' + R_split + '] transport pill shows ' + rTransport + ' — ' + why);

  clickSel(dash, '#connectBtn');
  const toggle = dash.postsWith('toggle_connection').at(-1);
  assert.deepEqual(toggle,
    { action: 'toggle_connection', mode: rMode, transport: rTransport, protocol: 'auto' },
    '[' + R_mode + '/' + R_transport + '/' + R_split + '] CONNECT carries the resolved state — ' + why);
  assert.ok(!('splitMode' in toggle),
    'splitMode is delivered via set_split_mode, never inside toggle_connection (host contract)');

  // The split the user EXPLICITLY clicked is what reaches the host; the mode
  // pill's mirror (rSplit) is a UI affordance that does not re-post.
  assert.deepEqual(dash.postsWith('set_split_mode').at(-1),
    { action: 'set_split_mode', mode: R_split },
    '[' + R_mode + '/' + R_transport + '/' + R_split + '] split state delivered via set_split_mode');
  dash.close();
}

test('dashboard: toggle_connection matrix — every mode × transport × split combo reflects the resolved UI state', async () => {
  // requested (mode, transport, split) -> resolved (mode, transport, split)
  const matrix = [
    ['gpn', 'proxy', 'off',    'gpn', 'proxy', 'off',    'plain GPN boot state'],
    ['gpn', 'proxy', 'manual', 'gpn', 'proxy', 'manual', 'split manual == GPN route mode'],
    ['gpn', 'proxy', 'vpn',    'gpn', 'tun',   'manual', 'split vpn forces Global VPN + TUN; mode gpn (last click) keeps the forced TUN'],
    ['gpn', 'tun',   'off',    'gpn', 'tun',   'off',    'TUN selected under GPN'],
    ['gpn', 'tun',   'manual', 'gpn', 'tun',   'manual', 'TUN + GPN route mode'],
    ['gpn', 'tun',   'vpn',    'gpn', 'tun',   'manual', 'split vpn -> TUN; mode gpn (last click) keeps TUN'],
    ['vpn', 'proxy', 'off',    'vpn', 'tun',   'vpn',    'mode vpn forces TUN and mirrors split to vpn'],
    ['vpn', 'tun',   'off',    'vpn', 'tun',   'vpn',    'same — off cannot survive mode vpn'],
    ['vpn', 'proxy', 'vpn',    'vpn', 'tun',   'vpn',    'proxy is impossible under Global VPN (forced TUN)'],
    ['vpn', 'tun',   'vpn',    'vpn', 'tun',   'vpn',    'plain Global VPN state'],
    ['vpn', 'proxy', 'manual', 'vpn', 'tun',   'vpn',    'mode vpn (last click) wins over split manual'],
    ['vpn', 'tun',   'manual', 'vpn', 'tun',   'vpn',    'same — last click wins']
  ];
  assert.equal(matrix.length, 12, '2 modes × 2 transports × 3 split modes');
  for (const row of matrix) {
    await assertConnectMatrixRow(row[0], row[1], row[2], row[3], row[4], row[5], row[6]);
  }
});

// ---------------------------------------------------------------------------
// GPN connection info — the host ModeDecision payload drives the telemetry
// Ping card and the session node live (server + latency + mode).
// ---------------------------------------------------------------------------

test('dashboard: setGpnConnectionInfo drives the Ping card and session node live', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const pingVal = document.getElementById('pingVal');
  const pingSub = document.getElementById('pingSub');
  const sessionNode = document.getElementById('sessionNode');

  dash.window.setGpnConnectionInfo({ server: 'İtalya', delayMs: 42, mode: 'WireGuard' });
  assert.equal(pingVal.textContent, '42', 'Ping card shows the selected server latency');
  assert.match(pingSub.textContent, /İtalya/);
  assert.match(pingSub.textContent, /WireGuard/);
  assert.match(sessionNode.textContent, /İtalya/);
  assert.match(sessionNode.textContent, /WireGuard/);

  // V2rayTCP fallback has no measured delay — Ping shows '--', node still named.
  dash.window.setGpnConnectionInfo({ server: 'İtalya', delayMs: -1, mode: 'V2rayTCP' });
  assert.equal(pingVal.textContent, '--');
  assert.match(sessionNode.textContent, /V2rayTCP/);

  dash.close();
});

test('dashboard: setAvailabilityInfo shows measured latency and IP in the Ping card', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const pingVal = document.getElementById('pingVal');
  const pingSub = document.getElementById('pingSub');
  const ipDisplay = document.getElementById('ipDisplay');
  const ipCountry = document.getElementById('ipCountry');

  dash.window.setAvailabilityInfo({ server: 'İtalya', delayMs: 36, ip: '92.4.220.236', country: 'IT' });
  assert.equal(pingVal.textContent, '36', 'Ping card shows the measured latency');
  assert.match(pingSub.textContent, /İtalya/);
  assert.match(pingSub.textContent, /36 ms/);
  assert.match(pingSub.textContent, /92\.4\.220\.236/);
  // IP verification card reflects the measured country/server + exit IP and
  // shows BOTH the country name and its ISO code: "İtalya (IT) · 92.4.220.236".
  assert.equal(ipDisplay.textContent, '92.4.220.236', 'IP card shows the measured exit IP');
  assert.match(ipCountry.textContent, /İtalya/);
  assert.match(ipCountry.textContent, /\(IT\)/);
  assert.match(ipCountry.textContent, /92\.4\.220\.236/);

  // Failed measurement (delay < 0) — Ping shows '--' but the server/IP stay visible.
  dash.window.setAvailabilityInfo({ server: 'İtalya', delayMs: -1, ip: '92.4.220.236', country: 'IT' });
  assert.equal(pingVal.textContent, '--');
  assert.match(pingSub.textContent, /İtalya/);
  assert.match(pingSub.textContent, /92\.4\.220\.236/);

  // Country code alone (no server name) is translated to the current UI
  // language — switch to Turkish: IT → İtalya, code still visible.
  dash.window.skinBridge.setLanguage('tr');
  await new Promise(res => setImmediate(res));
  dash.window.setAvailabilityInfo({ delayMs: 36, ip: '92.4.220.236', country: 'IT' });
  assert.equal(pingVal.textContent, '36');
  assert.match(pingSub.textContent, /\(İtalya · IT\)/, 'ping sub shows translated name + code');
  assert.match(pingSub.textContent, /92\.4\.220\.236/);
  assert.match(ipCountry.textContent, /^İtalya \(IT\) · 92\.4\.220\.236$/, 'IP card: name (code) · IP');

  dash.close();
});

test('dashboard: availability result is not overwritten by a fresh telemetry sample within the hold window', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const pingVal = document.getElementById('pingVal');
  const pingSub = document.getElementById('pingSub');
  const lossVal = document.getElementById('lossVal');

  // The hold-window contract is about live telemetry taking over the ping card,
  // which only happens outside GPN mode (in GPN the ping card belongs to the
  // server-selection decision, not live samples). Switch to Global VPN so the
  // hold/expiry behaviour is observable.
  dash.window.skinBridge.mode = 'vpn';
  dash.window.setNexusConnected(true);

  // Availability check result lands on the Ping card.
  dash.window.setAvailabilityInfo({ server: 'İtalya', delayMs: 36, ip: '92.4.220.236', country: 'IT' });
  assert.equal(pingVal.textContent, '36');

  // The very next live telemetry sample must NOT overwrite it — the user can read
  // the measured value; loss still updates live during the hold.
  dash.window.updateTelemetry(50, 0.5, 8.2, 3.1);
  assert.equal(pingVal.textContent, '36', 'availability latency survives the next telemetry sample');
  assert.match(pingSub.textContent, /92\.4\.220\.236/);
  assert.equal(lossVal.textContent, '0.5', 'loss still updates live during the hold');

  // A new authoritative GPN decision clears the hold → telemetry takes over again.
  dash.window.setGpnConnectionInfo({ server: 'İtalya', delayMs: -1, mode: 'WireGuard' });
  dash.window.updateTelemetry(50, 0.5, 8.2, 3.1);
  assert.equal(pingVal.textContent, '50', 'live telemetry resumes after a new GPN decision');

  dash.close();
});

test('dashboard: connection failure card shows why the connect failed', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const banner = document.getElementById('connectionError');
  const text = document.getElementById('connectionErrorText');
  const details = document.getElementById('connectionErrorDetails');
  const relaunch = document.getElementById('relaunchAdminBtn');

  // Structured failure payload: message + technical details, no elevation.
  dash.window.setConnectionError({
    message: 'Core configuration validation failed.',
    details: 'FATAL: unknown field "foo"',
    elevation: false
  });
  assert.ok(!banner.classList.contains('hidden'), 'failure card visible');
  assert.equal(text.textContent, 'Core configuration validation failed.');
  assert.ok(!details.classList.contains('hidden'), 'technical details shown');
  assert.equal(details.textContent, 'FATAL: unknown field "foo"');
  assert.ok(relaunch.classList.contains('hidden'), 'no relaunch button for non-elevation errors');

  // Elevation error → relaunch-as-admin button appears.
  dash.window.setConnectionError({ message: 'TUN requires admin.', elevation: true });
  assert.ok(!relaunch.classList.contains('hidden'), 'relaunch button for elevation errors');

  // Legacy string input keeps the old banner behaviour (button visible).
  dash.window.setConnectionError('TUN mode requires administrator privileges.');
  assert.ok(!banner.classList.contains('hidden'));
  assert.ok(!relaunch.classList.contains('hidden'));
  assert.ok(details.classList.contains('hidden'), 'no details line for plain strings');

  // Empty clears the card.
  dash.window.setConnectionError('');
  assert.ok(banner.classList.contains('hidden'), 'card hidden after clear');

  dash.close();
});

test('dashboard: GPN connect button posts gpn_connect and locks while pending', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const btn = document.getElementById('gpnConnectBtn');
  assert.ok(btn, 'distinct GPN Connect button exists');

  click(dash, btn);
  assert.ok(dash.postsWith('gpn_connect').length >= 1, 'button posts gpn_connect to the host');
  assert.equal(document.getElementById('gpnConnectState').textContent, '…', 'badge enters pending state');
  assert.equal(btn.disabled, true, 'button locks while pending');

  dash.close();
});

// ---------------------------------------------------------------------------
// GPN Server Management (Faz 3) — gpn_servers list render + CRUD posts
// ---------------------------------------------------------------------------

test('dashboard: server management renders gpn_servers and posts CRUD actions', async () => {
  const dash = await bootDashboard();
  const { document } = dash;

  // Open the GPN Servers view: the host list request must be posted.
  assert.ok(dash.openView('gpnServers'), 'gpnServers nav item must exist');
  assert.ok(dash.postsWith('gpn_servers_list').length >= 1, 'opening the view requests the catalog');

  // Push catalog metadata (private key never leaves the host).
  dash.window.setGpnServers([
    { serverId: '92.4.220.236:51820', name: 'İtalya', endpointHost: '92.4.220.236', endpointPort: 51820, clientAddress: '10.66.66.2/24', mtu: 1420, dns: '1.1.1.1', keepalive: 25, isEnabled: true, keyProtected: true, updatedAt: 1 },
    { serverId: '130.61.223.36:51820', name: 'Almanya', endpointHost: '130.61.223.36', endpointPort: 51820, clientAddress: '10.66.66.3/24', mtu: 1420, dns: '1.1.1.1', keepalive: 25, isEnabled: false, keyProtected: true, updatedAt: 2 }
  ]);

  const list = document.getElementById('gpnServerList');
  assert.ok(list.textContent.includes('İtalya'), 'server name renders');
  assert.ok(list.textContent.includes('130.61.223.36:51820'), 'endpoint renders');
  assert.equal(document.getElementById('gpnServerCount').textContent, '2', 'count reflects the catalog');

  // Toggle the disabled server on.
  const toggleBtn = list.querySelector('[data-gpn-toggle="130.61.223.36:51820"]');
  assert.ok(toggleBtn, 'toggle button exists per server');
  click(dash, toggleBtn);
  assert.deepEqual(dash.postsWith('gpn_server_toggle').at(-1),
    { action: 'gpn_server_toggle', serverId: '130.61.223.36:51820', enabled: true },
    'toggle posts the target id and the desired state');

  // Edit opens the WPF dialog via the host; Add opens a blank one.
  click(dash, list.querySelector('[data-gpn-edit="92.4.220.236:51820"]'));
  assert.deepEqual(dash.postsWith('gpn_server_edit_dialog').at(-1),
    { action: 'gpn_server_edit_dialog', serverId: '92.4.220.236:51820' },
    'edit button opens the WPF dialog for that server');
  click(dash, document.getElementById('gpnServerAddBtn'));
  assert.deepEqual(dash.postsWith('gpn_server_add_dialog').at(-1),
    { action: 'gpn_server_add_dialog' },
    'add button opens a blank WPF dialog');

  // Import posts the .conf text (long payload, not a short key).
  const conf = document.getElementById('gpnConfText');
  conf.value = '[Interface]\nPrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\nAddress = 10.66.66.2/24\n\n[Peer]\nPublicKey = 5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=\nEndpoint = 92.4.220.236:51820\nAllowedIPs = 0.0.0.0/0, ::/0';
  click(dash, document.getElementById('gpnImportBtn'));
  const addPost = dash.postsWith('gpn_server_add').at(-1);
  assert.equal(addPost.action, 'gpn_server_add', 'import posts gpn_server_add');
  assert.ok(addPost.confText.includes('Endpoint = 92.4.220.236:51820'), 'full .conf text is sent');

  dash.close();
});

test('dashboard: server management shows live latency + UDP probes from the host', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  assert.ok(dash.openView('gpnServers'), 'opens the GPN Servers view');
  assert.ok(dash.postsWith('gpn_servers_probe').length >= 1,
    'opening the view triggers a live probe');

  dash.window.setGpnServers([
    { serverId: '92.4.220.236:51820', name: 'İtalya', endpointHost: '92.4.220.236', endpointPort: 51820, isEnabled: true, keyProtected: true }
  ]);
  dash.window.setGpnServerProbes([
    { serverId: '92.4.220.236:51820', delayMs: 42, avgDelayMs: 44, maxDelayMs: 50, lossPercent: 0, isSuccess: true, udpStatus: 'Open', udpRoundTripMs: 5 }
  ]);

  const list = document.getElementById('gpnServerList');
  assert.ok(list.textContent.includes('42 ms'), 'latency badge renders the measured delay');
  assert.ok(list.textContent.includes('Open'), 'UDP path status renders');

  // Manual measure button posts the probe request.
  click(dash, document.getElementById('gpnProbeBtn'));
  assert.ok(dash.postsWith('gpn_servers_probe').length >= 2, 'Measure button re-probes');

  // Leaving the view stops the periodic probe loop.
  assert.ok(dash.openView('nodes'), 'switches to another view');
  const before = dash.postsWith('gpn_servers_probe').length;
  await new Promise(r => setTimeout(r, 400));
  assert.equal(dash.postsWith('gpn_servers_probe').length, before,
    'no further probes fire after leaving the view');

  dash.close();
});

test('dashboard: server management delete requires confirmation and posts gpn_server_delete', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  dash.window.setGpnServers([
    { serverId: '92.4.220.236:51820', name: 'İtalya', endpointHost: '92.4.220.236', endpointPort: 51820, isEnabled: true, keyProtected: true }
  ]);
  const list = document.getElementById('gpnServerList');

  // Cancel keeps the server; confirm deletes it.
  dash.window.confirm = () => false;
  click(dash, list.querySelector('[data-gpn-delete]'));
  assert.equal(dash.postsWith('gpn_server_delete').length, 0, 'cancel does not delete');

  dash.window.confirm = () => true;
  click(dash, list.querySelector('[data-gpn-delete]'));
  assert.deepEqual(dash.postsWith('gpn_server_delete').at(-1),
    { action: 'gpn_server_delete', serverId: '92.4.220.236:51820' },
    'confirmed delete posts the server id');

  dash.close();
});

test('dashboard: embedded defaults status renders and restore posts gpn_defaults_restore', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  assert.ok(dash.openView('gpnServers'), 'opens the GPN Servers view');

  const body = document.getElementById('gpnDefaultsStatus');
  const restoreBtn = document.getElementById('gpnDefaultsRestoreBtn');
  assert.ok(body && restoreBtn, 'embedded defaults status area + restore button exist');

  // Italy up to date, Germany outdated (fields drifted), a not-seeded third scenario.
  dash.window.setGpnDefaultsStatus({
    servers: [
      { serverId: '92.4.220.236:51820', endpointHost: '92.4.220.236', endpointPort: 51820, seeded: true, keyPresent: true, upToDate: true, differences: [] },
      { serverId: '130.61.223.36:51820', endpointHost: '130.61.223.36', endpointPort: 51820, seeded: true, keyPresent: true, upToDate: false, differences: ['Mtu', 'Dns'] },
      { serverId: '203.0.113.9:51820', endpointHost: '203.0.113.9', endpointPort: 51820, seeded: false, keyPresent: false, upToDate: false, differences: [] }
    ],
    restoredCount: null,
    measuredAt: Date.now()
  });

  let text = body.textContent;
  assert.ok(text.includes('Up to date'), 'up-to-date badge rendered');
  assert.ok(text.includes('Outdated'), 'outdated badge rendered');
  assert.ok(text.includes('Mtu') && text.includes('Dns'), 'field differences shown');
  assert.ok(text.includes('Not seeded'), 'not-seeded badge rendered');

  // Restore button posts the host action.
  click(dash, restoreBtn);
  assert.ok(dash.postsWith('gpn_defaults_restore').length >= 1, 'restore posts gpn_defaults_restore');

  // Empty state placeholder.
  dash.window.setGpnDefaultsStatus(null);
  assert.ok(body.textContent.includes('No defaults status yet'), 'empty placeholder restored');

  dash.close();
});

test('dashboard: GPN diagnostics feed streams host GPN_* lines', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const feed = document.getElementById('gpnDiagFeed');
  assert.ok(feed, 'diagnostics feed exists in the GPN panel');
  assert.ok(feed.textContent.length > 0, 'shows the empty placeholder initially');

  dash.window.setGpnDiag({ kind: 'RECOVER', message: 'GPN_RECOVER wireguard → de', timestampMs: Date.UTC(2026, 7, 29, 12, 0, 0) });
  assert.ok(feed.textContent.includes('GPN_RECOVER wireguard → de'), 'GPN_* line appended to the feed');
  assert.ok(feed.textContent.includes('RECOVER'), 'kind badge rendered');
  assert.equal(document.getElementById('gpnDiagCount').textContent, '1', 'count badge increments');

  // Feed caps at 50 lines (oldest dropped).
  for (let i = 0; i < 60; i++) {
    dash.window.setGpnDiag({ kind: 'LOG', message: 'GPN_LOG test line ' + i, timestampMs: Date.now() });
  }
  assert.equal(feed.children.length, 50, 'feed caps at 50 lines');
  assert.equal(document.getElementById('gpnDiagCount').textContent, '50', 'count badge caps at 50');

  // Clear button restores the empty placeholder.
  click(dash, document.getElementById('gpnDiagClear'));
  assert.ok(feed.textContent.includes('No GPN diagnostics'), 'clear restores the empty placeholder');
  assert.equal(document.getElementById('gpnDiagCount').textContent, '0', 'count badge resets');

  dash.close();
});

test('dashboard: GPN recovery watch toggle posts set_gpn_recovery_watch and reflects settings', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const toggle = document.getElementById('gpnRecoveryWatchToggle');
  assert.ok(toggle, 'recovery-watch toggle exists in the GPN panel');
  assert.equal(toggle.getAttribute('aria-checked'), 'true', 'defaults to on');

  // Toggle off → posts the disabled state and updates the visual state.
  click(dash, toggle);
  assert.deepEqual(dash.postsWith('set_gpn_recovery_watch').at(-1),
    { action: 'set_gpn_recovery_watch', enabled: false },
    'toggle off posts enabled:false');
  assert.equal(toggle.getAttribute('aria-checked'), 'false', 'visual state reflects off');

  // Host pushes the persisted value back → toggle reflects it.
  dash.window.applySettings({ connection: { gpnEnableRecoveryWatch: true } });
  assert.equal(toggle.getAttribute('aria-checked'), 'true', 'applySettings restores the toggle');

  // Now on this updated state, two clicks round-trip off → on.
  click(dash, toggle);
  assert.equal(toggle.getAttribute('aria-checked'), 'false', 'click toggles off after applySettings');
  click(dash, toggle);
  assert.deepEqual(dash.postsWith('set_gpn_recovery_watch').at(-1),
    { action: 'set_gpn_recovery_watch', enabled: true },
    'toggle on posts enabled:true');
  assert.equal(toggle.getAttribute('aria-checked'), 'true', 'visual state reflects on');

  dash.close();
});

test('dashboard: failover telemetry counters render live snapshots and reset', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const card = document.getElementById('gpnTelemetryCard');
  assert.ok(card, 'telemetry card exists in the GPN panel');
  assert.equal(document.getElementById('gpnTelSwitches').textContent, '0', 'starts at zero');

  dash.window.setGpnTelemetry({
    serverSwitches: 2, udpDeaths: 1, modeFallbacks: 3, recoveries: 4, modeDecisions: 5, totalEvents: 15
  });
  assert.equal(document.getElementById('gpnTelSwitches').textContent, '2', 'switch counter renders');
  assert.equal(document.getElementById('gpnTelDeaths').textContent, '1', 'death counter renders');
  assert.equal(document.getElementById('gpnTelFallbacks').textContent, '3', 'fallback counter renders');
  assert.equal(document.getElementById('gpnTelRecovers').textContent, '4', 'recover counter renders');
  assert.equal(document.getElementById('gpnTelDecisions').textContent, '5', 'select counter renders');
  assert.equal(document.getElementById('gpnTelemetryTotal').textContent, '15', 'total badge renders');

  // Reset button posts the host reset action.
  click(dash, document.getElementById('gpnTelemetryReset'));
  assert.deepEqual(dash.postsWith('reset_gpn_telemetry').at(-1),
    { action: 'reset_gpn_telemetry' },
    'reset button posts reset_gpn_telemetry');

  dash.close();
});

test('dashboard: last-50 decision ring log renders entries and clear/refresh post', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const list = document.getElementById('gpnResilienceLogList');
  assert.ok(list, 'last-50 decision list exists in the GPN panel');

  dash.window.setGpnResilienceLog({
    path: 'C:\\exe\\gpn_resilience.log',
    entries: [
      { timestampMs: Date.UTC(2026, 7, 29, 12, 0, 1), action: 'ServerSwitch', toMode: 'WireGuard', serverName: 'İtalya', targetServerName: 'Almanya', line: 'x' },
      { timestampMs: Date.UTC(2026, 7, 29, 12, 0, 2), action: 'Recover', toMode: 'WireGuard', serverName: 'Almanya', line: 'x' }
    ]
  });
  // Accumulated, color-coded recent-events history: localized badge + mode + servers.
  assert.ok(list.textContent.includes('Server switch'), 'switch badge rendered (localized)');
  assert.ok(list.textContent.includes('Recovery'), 'recover badge rendered (localized)');
  assert.ok(list.textContent.includes('WireGuard'), 'WireGuard mode badge rendered');
  assert.ok(list.textContent.includes('İtalya') && list.textContent.includes('→ Almanya'), 'switch detail shows source → target server');
  assert.ok(list.querySelector('.gpn-ev-switch'), 'server-switch row is color-coded (amber)');
  assert.ok(list.querySelector('.gpn-ev-recover'), 'recover row is color-coded (green)');
  assert.ok(list.querySelector('.gpn-event-pop'), 'newest event carries pop animation');
  assert.equal(document.getElementById('gpnResLogCount').textContent, '2', 'count badge shows entry count');
  assert.ok(document.getElementById('gpnResLogPath').textContent.includes('gpn_resilience.log'), 'log path shown');

  click(dash, document.getElementById('gpnResLogRefresh'));
  assert.deepEqual(dash.postsWith('get_gpn_resilience_log').at(-1),
    { action: 'get_gpn_resilience_log' }, 'refresh posts the fetch action');
  click(dash, document.getElementById('gpnResLogClear'));
  assert.deepEqual(dash.postsWith('clear_gpn_resilience_log').at(-1),
    { action: 'clear_gpn_resilience_log' }, 'clear posts the clear action');

  dash.close();
});

test('dashboard: GPN failover flashes the ping/loss telemetry cards and annotates lossSub', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const pingCard = document.getElementById('pingVal').closest('.glass');
  const lossCard = document.getElementById('lossVal').closest('.glass');
  assert.ok(pingCard && lossCard, 'ping & packet-loss telemetry cards exist');

  // Failover (server switch) → amber flash on both cards + decision context on lossSub.
  dash.window.setGpnResilience({ action: 'ServerSwitch', serverName: 'İtalya', targetServerName: 'Almanya', reason: 'de daha iyi', toMode: 'WireGuardUDP' });
  assert.ok(pingCard.classList.contains('gpn-failover-flash'), 'ping card flashes on failover');
  assert.ok(lossCard.classList.contains('gpn-failover-flash'), 'loss card flashes on failover');
  assert.ok(!pingCard.classList.contains('gpn-recover-flash'), 'failover is not a recovery flash');
  assert.ok(document.getElementById('lossSub').textContent.includes('GPN failover'), 'lossSub shows the failover context');

  // Recovery → emerald flash variant.
  dash.window.setGpnResilience({ action: 'Recover', serverName: 'Almanya', toMode: 'WireGuardUDP' });
  assert.ok(pingCard.classList.contains('gpn-recover-flash'), 'recover flashes the emerald variant');
  assert.ok(document.getElementById('lossSub').textContent.includes('kurtarma'), 'lossSub shows the recovery context');

  // ModeDecision (initial auto-selection) does not flash.
  dash.window.setGpnResilience({ action: 'ModeDecision', serverName: 'İtalya', toMode: 'WireGuardUDP' });
  assert.ok(pingCard.classList.contains('gpn-recover-flash'), 'ModeDecision leaves the current flash state untouched');

  dash.close();
});

test('dashboard: soft node-switch drain progress renders on the status line and clears on finish', async () => {
  const dash = await bootDashboard();
  const { document } = dash;
  const line = document.getElementById('connectionStatusLine');
  assert.ok(line, 'status line exists');

  // Boşalma sürüyor: eski düğüm → yeni düğüm + kalan oturum sayısı.
  dash.window.setGpnNodeSwitch({ isDraining: true, fromServerName: 'İtalya', toServerName: 'Almanya', remainingConnections: 3 });
  assert.ok(line.textContent.includes('eski düğüm boşalıyor'), 'drain progress is visible on the status line');
  assert.ok(line.textContent.includes('İtalya') && line.textContent.includes('→ Almanya'), 'source → target node is visible');
  assert.ok(line.textContent.includes('3 bağlantı'), 'remaining connection count is visible');

  // Tamamlandı: eski düğüm boşaldı, yeni düğüm aktif.
  dash.window.setGpnNodeSwitch({ isDraining: false, fromServerName: 'İtalya', toServerName: 'Almanya', remainingConnections: 0, timedOut: false });
  assert.ok(line.textContent.includes('eski düğüm boşaldı'), 'finished state is rendered');

  // Zaman aşımı: kalan oturumlar zorla kapatılmaz, doğal bitişi bekler.
  dash.window.setGpnNodeSwitch({ isDraining: false, fromServerName: 'İtalya', toServerName: 'Almanya', remainingConnections: 2, timedOut: true });
  assert.ok(line.textContent.includes('2 bağlantı eski düğümde doğal bitişi bekliyor'), 'leftover connections are surfaced');

  dash.close();
});

test('dashboard: server cluster card renders live Italy/Germany latency, best badge, and measure post', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const list = document.getElementById('gpnClusterList');
  const ping = document.getElementById('gpnClusterPing');
  assert.ok(list && ping, 'server cluster card exists in the GPN panel');
  assert.ok(list.textContent.includes('No active servers'), 'empty placeholder shown before servers arrive');

  const panel = document.getElementById('panelGPN');
  panel.classList.remove('hidden-panel');
  dash.window.setGpnServers([
    { serverId: '92.4.220.236:51820', name: 'İtalya', endpointHost: '92.4.220.236', endpointPort: '51820', isEnabled: true },
    { serverId: '130.61.223.36:51820', name: 'Almanya', endpointHost: '130.61.223.36', endpointPort: '51820', isEnabled: true },
    { serverId: '69.69.69.69:51820', name: 'Kapalı', endpointHost: '69.69.69.69', endpointPort: '51820', isEnabled: false }
  ]);
  // Populating enabled servers on a visible GPN panel fires the one-time auto-probe.
  assert.ok(dash.postsWith('gpn_cluster_probe').length >= 1, 'initial auto-probe posted on populate');

  dash.window.setGpnServerProbes([
    { serverId: '92.4.220.236:51820', isSuccess: true, delayMs: 42, lossPercent: 0, udpStatus: 'Open' },
    { serverId: '130.61.223.36:51820', isSuccess: true, delayMs: 21, lossPercent: 2, udpStatus: 'Open' }
  ]);

  const rows = list.querySelectorAll('.rounded-lg');
  assert.equal(rows.length, 2, 'two enabled server rows render (disabled excluded)');
  const texts = list.textContent;
  assert.ok(texts.includes('İtalya') && texts.includes('Almanya'), 'both servers named');
  assert.ok(texts.includes('42 ms') && texts.includes('21 ms'), 'live latency values shown');
  assert.ok(texts.includes('best'), 'lowest-latency healthy server highlighted as best');
  assert.ok(texts.includes('2%'), 'loss shown for the lossy server');
  assert.ok(ping.textContent.includes('2 open'), 'summary counts open servers');
  assert.ok(ping.textContent.includes('21 ms'), 'summary shows best latency');

  // Manual measure button posts the cluster probe action even while auto-pending.
  click(dash, document.getElementById('gpnClusterProbe'));
  assert.deepEqual(dash.postsWith('gpn_cluster_probe').at(-1),
    { action: 'gpn_cluster_probe' }, 'measure posts gpn_cluster_probe');

  dash.close();
});

test('dashboard: cluster card shows a physical-NIC hint while the tunnel is active', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const hint = document.getElementById('gpnClusterEgressHint');
  assert.ok(hint, 'egress hint element exists in the cluster card header');
  assert.ok(hint.classList.contains('hidden'), 'hint hidden before a tunnel-active measurement');

  dash.window.setGpnServers([
    { serverId: '92.4.220.236:51820', name: 'İtalya', endpointHost: '92.4.220.236', endpointPort: '51820', isEnabled: true }
  ]);

  // Tunnel active: host reports the measurement ran over the physical NIC.
  dash.window.setGpnServerProbes([
    { serverId: '92.4.220.236:51820', isSuccess: true, delayMs: 42, lossPercent: 0, udpStatus: 'NoResponse', measuredOverPhysicalNic: true }
  ]);
  assert.ok(!hint.classList.contains('hidden'), 'hint visible when measured over the physical NIC');
  assert.ok(hint.textContent.length > 0, 'hint carries a short label');
  assert.ok(hint.title.length > 0, 'hint carries the explanatory tooltip');

  // No tunnel: the hint disappears.
  dash.window.setGpnServerProbes([
    { serverId: '92.4.220.236:51820', isSuccess: true, delayMs: 42, lossPercent: 0, udpStatus: 'Open', measuredOverPhysicalNic: false }
  ]);
  assert.ok(hint.classList.contains('hidden'), 'hint hidden again when measured normally (ICMP)');

  dash.close();
});

test('dashboard: cluster card falls back to the Open UDP probe RTT when ping is unavailable', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const list = document.getElementById('gpnClusterList');

  dash.window.setGpnServers([
    { serverId: '92.4.220.236:51820', name: 'İtalya', endpointHost: '92.4.220.236', endpointPort: '51820', isEnabled: true }
  ]);

  // Ping failed (tunnel active, ICMP skipped, handshake timeout) but the UDP probe
  // is Open with a measured round-trip — the card shows the UDP RTT as the latency.
  dash.window.setGpnServerProbes([{
    serverId: '92.4.220.236:51820', isSuccess: false, delayMs: -1, lossPercent: 100,
    udpStatus: 'Open', udpRoundTripMs: 42, measuredOverPhysicalNic: true
  }]);
  assert.ok(list.textContent.includes('42 ms'), 'UDP RTT shown as latency when ping unavailable');
  assert.ok(!list.textContent.includes('100%'), 'ping loss not shown with the UDP-RTT fallback');

  // UDP not Open — no fallback, latency stays em-dash.
  dash.window.setGpnServerProbes([{
    serverId: '92.4.220.236:51820', isSuccess: false, delayMs: -1, lossPercent: 100,
    udpStatus: 'NoResponse', udpRoundTripMs: -1, measuredOverPhysicalNic: true
  }]);
  assert.ok(list.textContent.includes('—'), 'no latency when the UDP probe is not Open');

  dash.close();
});

test('dashboard: PID pool card renders live target PIDs and start/stop/refresh post', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const targets = document.getElementById('gpnPidPoolTargets');
  const list = document.getElementById('gpnPidPoolList');
  const state = document.getElementById('gpnPidPoolState');
  assert.ok(targets && list && state, 'PID pool card exists in the GPN panel');
  assert.ok(list.textContent.includes('No vpn-routed game yet'), 'empty placeholder shown initially');

  // Watching with live PIDs (host GpnTargetResolverBridge → setGpnPidPool).
  dash.window.setGpnPidPool({
    targetNames: ['EscapeFromTarkov.exe', 'lol.exe'],
    pids: [100, 200, 300],
    version: 1,
    resolvedAt: Date.now(),
    watching: true,
    targetRunning: true
  });
  assert.ok(targets.textContent.includes('EscapeFromTarkov.exe'), 'target chip rendered');
  assert.ok(targets.textContent.includes('lol.exe'), 'second target chip rendered');
  assert.ok(list.textContent.includes('100'), 'live PID list rendered');
  assert.ok(list.textContent.includes('300'), 'child-process PID rendered');
  assert.ok(state.textContent.includes('watching'), 'watching badge shown');

  // Target offline → honest placeholder instead of stale PIDs.
  dash.window.setGpnPidPool({ targetNames: ['EscapeFromTarkov.exe'], pids: [], watching: true, targetRunning: false });
  assert.ok(list.textContent.includes('Target offline'), 'offline placeholder shown');
  assert.ok(state.textContent.includes('offline'), 'offline state reflected in badge');

  // Stopped (idle) state.
  dash.window.setGpnPidPool({ targetNames: ['EscapeFromTarkov.exe'], pids: [100], watching: false, targetRunning: true });
  assert.ok(state.textContent.includes('idle'), 'idle badge shown when not watching');

  // FATAL source (process tree inaccessible) → red fatal badge, never fake "offline".
  dash.window.setGpnPidPool({
    targetNames: ['EscapeFromTarkov.exe'], pids: [], watching: true, targetRunning: false, sourceStatus: 'Fatal'
  });
  assert.ok(state.textContent.includes('fatal'), 'fatal badge shown when the process tree is inaccessible');
  assert.ok(state.classList.contains('bg-red-400/10'), 'fatal badge is red');

  // Degraded source (Toolhelp32 failed, fallback used) → amber badge with live pids.
  dash.window.setGpnPidPool({
    targetNames: ['EscapeFromTarkov.exe'], pids: [100], watching: true, targetRunning: true, sourceStatus: 'Degraded'
  });
  assert.ok(state.textContent.includes('degraded'), 'degraded badge shown for fallback mode');
  assert.ok(state.classList.contains('bg-amber-400/10'), 'degraded badge is amber');

  // Watch / Stop / Refresh buttons post the host actions.
  click(dash, document.getElementById('gpnPidPoolStart'));
  assert.deepEqual(dash.postsWith('gpn_pid_pool_start').at(-1),
    { action: 'gpn_pid_pool_start' }, 'watch posts gpn_pid_pool_start');
  click(dash, document.getElementById('gpnPidPoolStop'));
  assert.deepEqual(dash.postsWith('gpn_pid_pool_stop').at(-1),
    { action: 'gpn_pid_pool_stop' }, 'stop posts gpn_pid_pool_stop');
  click(dash, document.getElementById('gpnPidPoolRefresh'));
  assert.deepEqual(dash.postsWith('gpn_pid_pool_refresh').at(-1),
    { action: 'gpn_pid_pool_refresh' }, 'refresh posts gpn_pid_pool_refresh');

  dash.close();
});

test('dashboard: captured traffic card renders per-packet telemetry (flows, per-PID, out-of-pool)', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const total = document.getElementById('gpnCaptureTotal');
  const agg = document.getElementById('gpnCaptureAgg');
  const flows = document.getElementById('gpnCaptureFlows');
  const pids = document.getElementById('gpnCapturePids');
  assert.ok(total && agg && flows && pids, 'captured traffic card exists in the GPN panel');

  // No data yet → empty placeholder, zero badge.
  dash.window.setGpnCaptureStats(null);
  assert.equal(total.textContent, '0', 'zero badge when no packets captured');
  assert.ok(agg.textContent.includes('No packets captured yet'), 'empty placeholder shown');

  // Live snapshot from GpnCaptureLoop (AppEvents.GpnCaptureStatsChanged).
  dash.window.setGpnCaptureStats({
    totalPackets: 1200,
    totalBytes: 204800,
    outboundPackets: 1150,
    inboundPackets: 50,
    udpPackets: 1100,
    tcpPackets: 100,
    icmpPackets: 0,
    otherPackets: 0,
    unknownPackets: 0,
    topFlows: [
      { peerEndpoint: '51.83.12.4:3074', protocol: 'Udp', packets: 1100, bytes: 180000, lastSeen: Date.now() },
      { peerEndpoint: '142.251.1.1:443', protocol: 'Tcp', packets: 100, bytes: 24800, lastSeen: Date.now() }
    ],
    byPid: [
      { pid: 100, packets: 1100, bytes: 180000, inPool: true },
      { pid: 8123, packets: 100, bytes: 24800, inPool: false }
    ],
    startedAt: Date.now(),
    lastActivityAt: Date.now()
  });

  assert.ok(total.textContent.includes('1.2k'), 'packet count formatted (1.2k)');
  assert.ok(agg.textContent.includes('1.1k UDP'), 'UDP chip rendered');
  assert.ok(agg.textContent.includes('100 TCP'), 'TCP chip rendered');
  assert.ok(agg.textContent.includes('200 KB'), 'bytes formatted');
  assert.ok(agg.textContent.includes('1.1k'), 'outbound count rendered');
  assert.ok(flows.textContent.includes('51.83.12.4:3074'), 'top flow endpoint rendered');
  assert.ok(flows.textContent.includes('1.1k'), 'top flow packet count rendered');
  assert.ok(pids.textContent.includes('100'), 'in-pool PID row rendered');
  assert.ok(pids.textContent.includes('8123'), 'out-of-pool PID row rendered');
  assert.ok(pids.textContent.includes('⚠'), 'out-of-pool PID flagged with warning');

  dash.close();
});

test('dashboard: failover matrix renders both policies side by side from one measurement', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const body = document.getElementById('gpnMatrixBody');
  const ts = document.getElementById('gpnMatrixTs');
  assert.ok(body && ts, 'failover matrix card exists in the GPN panel');

  // No data yet → empty placeholder.
  dash.window.setGpnFailoverMatrix(null);
  assert.ok(body.textContent.includes('No measurement yet'), 'empty placeholder shown initially');

  // Same measurement: both servers NoResponse → default keeps, strict falls back.
  dash.window.setGpnFailoverMatrix({
    rows: [
      { serverId: 'it', name: 'İtalya', pingMs: 30, lossPercent: 0, udpStatus: 'NoResponse', isActive: true, healthyDefault: true, healthyStrict: false },
      { serverId: 'de', name: 'Almanya', pingMs: 40, lossPercent: 0, udpStatus: 'NoResponse', isActive: false, healthyDefault: true, healthyStrict: false }
    ],
    defaultPolicy: { noResponseAsBlocked: false, handshakeNoResponseAsBlocked: true, udpHealthEnabled: true, action: 'None', targetServerId: null, reason: 'aktif tünel sağlıklı, daha iyi aday yok' },
    strictPolicy: { noResponseAsBlocked: true, handshakeNoResponseAsBlocked: true, udpHealthEnabled: true, action: 'FallbackToV2ray', targetServerId: null, reason: 'hiçbir sunucuda sağlıklı UDP yok — tünel öldü' },
    measuredAt: Date.now()
  });

  const text = body.textContent;
  assert.ok(text.includes('Default') && text.includes('Strict'), 'both policy columns labeled');
  assert.ok(text.includes('Keep'), 'default policy keeps the active tunnel (NoResponse healthy)');
  assert.ok(text.includes('Fallback V2rayTCP'), 'strict policy falls back (NoResponse dead)');
  assert.ok(text.includes('İtalya') && text.includes('30 ms'), 'server row with ping rendered');
  assert.ok(text.includes('NoResponse'), 'UDP status chip rendered');

  // Switch case: active HandshakeNoResponse (dead) + candidate Open → switch target.
  dash.window.setGpnFailoverMatrix({
    rows: [
      { serverId: 'it', name: 'İtalya', pingMs: 30, lossPercent: 0, udpStatus: 'HandshakeNoResponse', isActive: true, healthyDefault: false, healthyStrict: false },
      { serverId: 'de', name: 'Almanya', pingMs: 40, lossPercent: 0, udpStatus: 'Open', isActive: false, healthyDefault: true, healthyStrict: true }
    ],
    defaultPolicy: { noResponseAsBlocked: false, handshakeNoResponseAsBlocked: true, udpHealthEnabled: true, action: 'SwitchServer', targetServerId: 'de', reason: 'aktif UDP ölü (HandshakeNoResponse) → Almanya (UDP sağlıklı)' },
    strictPolicy: { noResponseAsBlocked: true, handshakeNoResponseAsBlocked: true, udpHealthEnabled: true, action: 'SwitchServer', targetServerId: 'de', reason: 'aktif UDP ölü (HandshakeNoResponse) → Almanya (UDP sağlıklı)' },
    measuredAt: Date.now()
  });

  const text2 = body.textContent;
  assert.ok(text2.includes('Switch → de'), 'switch decision shows the target server id');
  assert.ok(text2.includes('HandshakeNoResponse'), 'handshake no-response status rendered');

  dash.close();
});

test('dashboard: best candidate card previews what GPN Connect will pick (same decision logic)', async () => {
  const dash = await bootDashboard();
  const { document } = dash.window;
  const body = document.getElementById('gpnCandidateBody');
  const ts = document.getElementById('gpnCandidateTs');
  assert.ok(body && ts, 'best candidate card exists in the GPN panel');

  // No data yet → empty placeholder.
  dash.window.setGpnSelectionPrediction(null);
  assert.ok(body.textContent.includes('No measurement yet'), 'empty placeholder shown initially');

  // WireGuardUDP: best ping + Open UDP → Tier 2 badge, selected row marked.
  dash.window.setGpnSelectionPrediction({
    bestId: 'it',
    bestName: 'İtalya',
    pingMs: 25,
    lossPercent: 0,
    udpStatus: 'Open',
    mode: 'WireGuardUDP',
    reason: 'İtalya — ping 25ms, UDP Open',
    servers: [
      { serverId: 'it', name: 'İtalya', delayMs: 25, lossPercent: 0, udpStatus: 'Open', selected: true },
      { serverId: 'de', name: 'Almanya', delayMs: 45, lossPercent: 0, udpStatus: 'Open', selected: false }
    ],
    measuredAt: Date.now()
  });

  let text = body.textContent;
  assert.ok(text.includes('İtalya') && text.includes('25 ms'), 'best candidate name + ping shown');
  assert.ok(text.includes('WireGuard · Tier 2'), 'WireGuardUDP mode badge rendered');
  assert.ok(text.includes('will connect'), 'selected server row marked');
  assert.ok(text.includes('Almanya') && text.includes('45 ms'), 'candidate rows with ping rendered');
  assert.ok(text.includes('Open'), 'UDP status chips rendered');

  // V2rayTCP: no server selectable → fallback badge + none message.
  dash.window.setGpnSelectionPrediction({
    bestId: null,
    bestName: null,
    pingMs: -1,
    lossPercent: 100,
    udpStatus: 'Blocked',
    mode: 'V2rayTCP',
    reason: 'UDP yolu doğrulanamadı — V2rayTCP düşüşü',
    servers: [
      { serverId: 'it', name: 'İtalya', delayMs: -1, lossPercent: 100, udpStatus: 'Blocked', selected: false },
      { serverId: 'de', name: 'Almanya', delayMs: -1, lossPercent: 100, udpStatus: 'Blocked', selected: false }
    ],
    measuredAt: Date.now()
  });

  text = body.textContent;
  assert.ok(text.includes('V2rayTCP · Tier 3'), 'V2rayTCP fallback badge rendered');
  assert.ok(text.includes('No WireGuard server selectable'), 'none message when no candidate');
  assert.ok(text.includes('Blocked'), 'blocked status chips rendered');

  dash.close();
});

test('dashboard: WinDivert queue card renders capture settings and Apply posts set_gpn_capture_settings', async () => {
  const dash = await bootDashboard();
  const { document } = dash;

  const qLen = document.getElementById('gpnCaptureSetQueueLen');
  const qTime = document.getElementById('gpnCaptureSetQueueTime');
  const qSize = document.getElementById('gpnCaptureSetQueueSize');
  const eLen = document.getElementById('gpnCaptureSetEnableLen');
  const eTime = document.getElementById('gpnCaptureSetEnableTime');
  const eSize = document.getElementById('gpnCaptureSetEnableSize');
  const layer = document.getElementById('gpnCaptureSetLayer');
  const direction = document.getElementById('gpnCaptureSetDirection');
  const slot = document.getElementById('gpnCaptureSetSlot');
  const save = document.getElementById('gpnCaptureSetSave');
  assert.ok(qLen && qTime && qSize && eLen && eTime && eSize && layer && direction && slot && save,
    'WinDivert queue card exists in the GPN panel');

  // Host pushes current config → fields populate, layer/direction label rendered.
  dash.window.setGpnCaptureSettings({
    queueLen: 8192, queueTime: 2000, queueSize: 1048576,
    enableQueueLen: true, enableQueueTime: false, enableQueueSize: false,
    layer: 1, direction: 1
  });
  assert.equal(qLen.value, '8192', 'queue length populated');
  assert.equal(qTime.value, '2000', 'queue time populated');
  assert.equal(qSize.value, '1048576', 'queue size populated');
  assert.equal(eLen.checked, true, 'enable-len checkbox reflects config');
  assert.equal(eTime.checked, false, 'enable-time checkbox reflects config');
  assert.equal(eSize.checked, false, 'enable-size checkbox reflects config');
  assert.equal(layer.value, '1', 'layer select populated');
  assert.equal(direction.value, '1', 'direction select populated');
  assert.ok(slot.textContent.includes('·'), 'layer/direction slot label rendered');

  // Change a value and Apply → posts the full patch to the host.
  qLen.value = '16384';
  eTime.checked = true;
  layer.value = '0';
  click(dash, save);
  assert.deepEqual(dash.postsWith('set_gpn_capture_settings').at(-1), {
    action: 'set_gpn_capture_settings',
    queueLen: 16384,
    queueTime: 2000,
    queueSize: 1048576,
    enableQueueLen: true,
    enableQueueTime: true,
    enableQueueSize: false,
    layer: 0,
    direction: 1
  }, 'Apply posts current values with set_gpn_capture_settings');

  // Host pushes persisted values back → card reflects them (round-trip).
  dash.window.setGpnCaptureSettings({
    queueLen: 16384, queueTime: 2000, queueSize: 1048576,
    enableQueueLen: true, enableQueueTime: true, enableQueueSize: false,
    layer: 0, direction: 1
  });
  assert.equal(qLen.value, '16384', 'round-tripped queue length');
  assert.equal(layer.value, '0', 'round-tripped layer');

  // Malformed input → the value is dropped (null) instead of sending garbage.
  qLen.value = 'abc';
  click(dash, save);
  assert.equal(dash.postsWith('set_gpn_capture_settings').at(-1).queueLen, null,
    'non-numeric queue length sent as null');

  dash.close();
});

test('dashboard: Wintun adapter card renders settings and Apply posts set_gpn_wintun_settings', async () => {
  const dash = await bootDashboard();
  const { document } = dash;

  const name = document.getElementById('gpnWintunSetAdapterName');
  const ring = document.getElementById('gpnWintunSetRingCapacity');
  const slot = document.getElementById('gpnWintunSetSlot');
  const save = document.getElementById('gpnWintunSetSave');
  assert.ok(name && ring && slot && save, 'Wintun adapter card exists in the GPN panel');

  // Host pushes current config → fields populate, prefix shown in the slot chip.
  dash.window.setGpnWintunSettings({ adapterName: 'AoGPN', ringCapacity: 4194304 });
  assert.equal(name.value, 'AoGPN', 'adapter name prefix populated');
  assert.equal(ring.value, '4194304', 'ring capacity populated');
  assert.equal(slot.textContent, 'AoGPN', 'adapter prefix chip rendered');

  // Change values and Apply → posts the patch to the host.
  name.value = 'FastTun';
  ring.value = '8388608';
  click(dash, save);
  assert.deepEqual(dash.postsWith('set_gpn_wintun_settings').at(-1), {
    action: 'set_gpn_wintun_settings',
    adapterName: 'FastTun',
    ringCapacity: 8388608
  }, 'Apply posts current values with set_gpn_wintun_settings');

  // Host pushes persisted values back → card reflects them (round-trip).
  dash.window.setGpnWintunSettings({ adapterName: 'FastTun', ringCapacity: 8388608 });
  assert.equal(name.value, 'FastTun', 'round-tripped adapter prefix');
  assert.equal(ring.value, '8388608', 'round-tripped ring capacity');

  // Empty adapter name → sent as null (host falls back to the default).
  name.value = '   ';
  click(dash, save);
  assert.equal(dash.postsWith('set_gpn_wintun_settings').at(-1).adapterName, null,
    'blank adapter name sent as null');

  // Non-numeric ring capacity → dropped (null) instead of sending garbage.
  name.value = 'AoGPN';
  ring.value = 'abc';
  click(dash, save);
  const last = dash.postsWith('set_gpn_wintun_settings').at(-1);
  assert.equal(last.adapterName, 'AoGPN', 'adapter name still sent');
  assert.equal(last.ringCapacity, null, 'non-numeric ring capacity sent as null');

  dash.close();
});
