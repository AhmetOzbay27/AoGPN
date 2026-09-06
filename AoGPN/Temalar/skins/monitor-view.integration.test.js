// ============================================================================
// Connection Monitor view module integration test
// ----------------------------------------------------------------------------
// Boots the REAL main dashboard (vpn-gpn-dashboard.html + Temalar/app.js) in
// jsdom via the shared sandbox, then evals the standalone monitor-view.js
// module exactly as the WebView2 loads it (script tag after app.js). Verifies:
//   • the view-mode toolbar appears in the monitor filter row,
//   • icon modes swap the connection table for a tile grid with the REAL
//     executable icons (letter placeholder before setAppIcons answers),
//   • tiles show remote address, route badge, protocol/state chips and the
//     state status dot,
//   • grouping by route/protocol/state/country/application with collapsible
//     headers,
//   • the tile ASSIGN select posts set_app_route with the same contract as
//     the details table,
//   • the grid honours the monitor filter and the Hide-listeners checkbox.
//
// Run with:
//   node --test Temalar/skins/monitor-view.integration.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { loadDashboard } = require('./skin-sandbox.js');

const MODULE_SOURCE = fs.readFileSync(path.join(__dirname, '..', 'monitor-view.js'), 'utf8');

const CONNECTIONS = [
  { processName: 'chrome', displayName: 'Google Chrome', exePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', pid: 42, protocol: 'TCP', remoteAddress: '142.250.74.46:443', state: 'Established', routeTag: 'direct', routeText: 'Direct', countryText: 'Italy', asnText: 'AS15169' },
  { processName: 'AnyDesk', displayName: 'AnyDesk', exePath: 'C:\\Program Files (x86)\\AnyDesk\\AnyDesk.exe', pid: 6428, protocol: 'UDP', remoteAddress: '*', state: 'Listen', routeTag: 'direct', routeText: 'Direct' },
  { processName: 'svchost', displayName: 'Windows Service Host', exePath: 'C:\\Windows\\System32\\svchost.exe', pid: 900, protocol: 'TCP', remoteAddress: '1.0.0.127:1042', state: 'Listen', routeTag: '', routeText: '' },
  { processName: 'cs2', displayName: 'Counter-Strike 2', exePath: 'C:\\Games\\cs2\\cs2.exe', pid: 99, protocol: 'UDP', remoteAddress: '1.2.3.4:27015', state: 'Established', routeTag: 'warp', routeText: 'WARP · İtalya' }
];

const APPS = [
  { processName: 'chrome', displayName: 'Google Chrome', entryType: 'app', action: 'vpn' }
];

async function monitorWithModule(connections = CONNECTIONS, apps = APPS) {
  const dash = await loadDashboard();
  assert.ok(dash.openView('perf'), 'perf nav item must exist and be clickable');
  dash.window.eval(MODULE_SOURCE);
  dash.updateMonitorSnapshot({ connections, apps, mode: 'off', connected: false });
  return dash;
}

test('monitor-view: toolbar appears in the filter row with details active by default', async () => {
  const dash = await monitorWithModule();
  const toolbar = dash.document.querySelector('.mon-view-toolbar');
  assert.ok(toolbar, 'view-mode toolbar must be injected');
  assert.ok(toolbar.parentElement.contains(dash.document.getElementById('monitorFilter')),
    'toolbar lives in the same row as the filter input');
  assert.equal(dash.document.querySelectorAll('.mon-view-mode-btn').length, 4, 'details + 3 icon sizes');
  assert.equal(dash.document.querySelector('.mon-view-mode-btn.active').dataset.viewMode, 'details', 'details is the default');
  const groupSelect = dash.document.querySelector('.mon-view-toolbar select');
  assert.equal(groupSelect.options.length, 6, 'route/protocol/state/country/application grouping options');
  assert.equal(groupSelect.value, 'none', 'no grouping by default');
  assert.ok(dash.document.getElementById('monitorGridView').classList.contains('hidden'), 'grid hidden in details mode');
  assert.ok(!dash.document.getElementById('monitorConnectionsBody').closest('.overflow-x-auto').classList.contains('hidden'),
    'table visible in details mode');
  dash.close();
});

test('monitor-view: large mode swaps the table for a tile grid with icons and state dots', async () => {
  const dash = await monitorWithModule();
  dash.document.querySelector('.mon-view-mode-btn[data-view-mode="large"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  const grid = dash.document.getElementById('monitorGridView');
  assert.ok(grid.classList.contains('large'), 'grid host carries the large size class');
  assert.ok(!grid.classList.contains('hidden'), 'grid visible in icon mode');
  assert.ok(dash.document.getElementById('monitorConnectionsBody').closest('.overflow-x-auto').classList.contains('hidden'),
    'connection table hidden in icon mode');

  // Default state hides TCP listeners -> 3 of the 4 connections stay.
  const tiles = grid.querySelectorAll('.mon-view-tile');
  assert.equal(tiles.length, 3, 'listener filter applies to the grid');
  const tileText = grid.textContent;
  assert.ok(tileText.includes('Counter-Strike 2'), 'tile shows the app name');
  assert.ok(tileText.includes('1.2.3.4:27015'), 'tile shows the remote address');
  assert.ok(tileText.includes('WARP · İtalya'), 'tile shows the route text badge');
  assert.ok(tileText.includes('UDP'), 'tile shows the protocol chip');
  assert.ok(tileText.includes('Established'), 'tile shows the state chip');
  assert.ok(grid.querySelector('.mon-view-status.on'), 'established connection carries the green dot');
  assert.ok(grid.querySelector('.mon-view-status.warn'), 'listening connection carries the amber dot');
  assert.ok(grid.querySelector('.mon-view-tile .app-icon'), 'tile starts with the letter placeholder');

  // The host resolves icons -> setAppIcons lands in the module cache and the
  // grid re-renders with the REAL executable icon.
  dash.window.setAppIcons({
    icons: { 'c:\\games\\cs2\\cs2.exe': 'data:image/png;base64,DDDD' }
  });
  const iconImg = grid.querySelector('.mon-view-tile img.app-icon[src="data:image/png;base64,DDDD"]');
  assert.ok(iconImg, 'tile swapped the letter placeholder for the shell icon');
  dash.close();
});

test('monitor-view: grouping by protocol/state/country/application renders collapsible headers', async () => {
  const dash = await monitorWithModule();
  dash.document.querySelector('.mon-view-mode-btn[data-view-mode="medium"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const groupSelect = dash.document.querySelector('.mon-view-toolbar select');

  groupSelect.value = 'protocol';
  groupSelect.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  let heads = dash.document.querySelectorAll('#monitorGridView .mon-view-group-head');
  assert.equal(heads.length, 2, 'TCP + UDP groups after the listener filter');
  assert.ok([...heads].some(h => h.textContent.includes('UDP') && h.textContent.includes('2')), 'UDP group counts its connections');
  assert.ok([...heads].some(h => h.textContent.includes('TCP')), 'TCP group present');

  // Show the TCP listener again -> the TCP group gains a third connection.
  const hide = dash.document.getElementById('monitorHideListeners');
  hide.checked = false;
  hide.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  heads = dash.document.querySelectorAll('#monitorGridView .mon-view-group-head');
  assert.equal(heads.length, 2, 'TCP + UDP groups still');
  const tcpHeadAfter = [...heads].find(h => h.textContent.includes('TCP'));
  assert.ok(tcpHeadAfter.textContent.includes('2'), 'TCP group now counts both TCP connections');

  // Collapse the TCP group.
  const tcpHead = [...heads].find(h => h.textContent.includes('TCP'));
  tcpHead.dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.ok(tcpHead.closest('.mon-view-group').classList.contains('collapsed'), 'clicking the header collapses the group');
  assert.equal(tcpHead.getAttribute('aria-expanded'), 'false');

  groupSelect.value = 'country';
  groupSelect.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  heads = dash.document.querySelectorAll('#monitorGridView .mon-view-group-head');
  assert.equal(heads.length, 2, 'Italy + unnamed group');
  assert.ok([...heads].some(h => h.textContent.includes('Italy')), 'country group present');

  groupSelect.value = 'app';
  groupSelect.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  heads = dash.document.querySelectorAll('#monitorGridView .mon-view-group-head');
  assert.equal(heads.length, 4, 'one group per application');
  assert.ok([...heads].some(h => h.textContent.includes('Google Chrome')), 'application group present');
  dash.close();
});

test('monitor-view: tile ASSIGN select posts set_app_route with the configured action preselected', async () => {
  const dash = await monitorWithModule();
  dash.document.querySelector('.mon-view-mode-btn[data-view-mode="medium"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  const sel = dash.document.querySelector('#monitorGridView select[data-route-process="chrome"]');
  assert.ok(sel, 'tile carries an ASSIGN select');
  assert.equal(sel.value, 'vpn', 'the configured route (from the apps snapshot) is preselected');
  sel.value = 'block';
  sel.dispatchEvent(new dash.window.Event('change', { bubbles: true }));

  const routePosts = dash.postsWith('set_app_route');
  assert.equal(routePosts.length, 1);
  assert.deepEqual(routePosts[0], { action: 'set_app_route', processName: 'chrome', displayName: 'Google Chrome', route: 'block' });
  dash.close();
});

test('monitor-view: grid honours the monitor filter and the hide-listeners checkbox', async () => {
  const dash = await monitorWithModule();
  dash.document.querySelector('.mon-view-mode-btn[data-view-mode="medium"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  const filter = dash.document.getElementById('monitorFilter');
  filter.value = 'anydesk';
  filter.dispatchEvent(new dash.window.Event('input', { bubbles: true }));
  let tiles = dash.document.querySelectorAll('#monitorGridView .mon-view-tile');
  assert.equal(tiles.length, 1, 'filter applies to the grid');
  assert.ok(tiles[0].textContent.includes('AnyDesk'));

  filter.value = 'zzz-no-match';
  filter.dispatchEvent(new dash.window.Event('input', { bubbles: true }));
  assert.ok(dash.document.querySelector('#monitorGridView .mon-view-empty'), 'empty note when nothing matches');

  filter.value = '';
  filter.dispatchEvent(new dash.window.Event('input', { bubbles: true }));
  tiles = dash.document.querySelectorAll('#monitorGridView .mon-view-tile');
  assert.equal(tiles.length, 3, 'clearing the filter restores the (listener-filtered) tiles');

  const hide = dash.document.getElementById('monitorHideListeners');
  hide.checked = false;
  hide.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  tiles = dash.document.querySelectorAll('#monitorGridView .mon-view-tile');
  assert.equal(tiles.length, 4, 'unchecking Hide listeners reveals the TCP listener tile');
  dash.close();
});

test('monitor-view: app.js requests icons for the displayed connections and rows render them', async () => {
  const dash = await monitorWithModule();

  const iconRequests = dash.postsWith('get_app_icons');
  assert.equal(iconRequests.length, 1, 'one icon request per snapshot');
  assert.deepEqual(iconRequests[0].paths.slice().sort(), [
    'c:\\games\\cs2\\cs2.exe',
    'c:\\program files (x86)\\anydesk\\anydesk.exe',
    'c:\\program files\\google\\chrome\\application\\chrome.exe',
    'c:\\windows\\system32\\svchost.exe'
  ].sort(), 'every displayed exe path is requested');

  const placeholder = dash.document.querySelector('#monitorConnectionsBody .app-icon');
  assert.equal(placeholder.tagName, 'SPAN', 'details-table rows start with the letter placeholder');
  dash.window.setAppIcons({
    icons: { 'c:\\games\\cs2\\cs2.exe': 'data:image/png;base64,DDDD' }
  });
  assert.ok(dash.document.querySelector('#monitorConnectionsBody img.app-icon'), 'details-table rows swap to the real icon');
  dash.close();
});