// ============================================================================
// Game Boost view module integration test
// ----------------------------------------------------------------------------
// Boots the REAL main dashboard (vpn-gpn-dashboard.html + Temalar/app.js) in
// jsdom via the shared sandbox, then evals the standalone boost-view.js module
// exactly as the WebView2 loads it (script tag after app.js). Verifies:
//   • the Windows-style view-mode toolbar appears next to the boost filter,
//   • icon modes swap the route table for a tile grid (small/medium/large),
//   • tiles render the real shell icon when the host pushes setAppIcons and a
//     letter placeholder before that,
//   • grouping ("kategorileme") by route/status/type renders collapsible
//     headers with correct counts,
//   • tile route select and delete post the SAME host contracts as the table,
//   • the app posts get_app_icons once per new exe path,
//   • the Connection Monitor rows show the icon too.
//
// Run with:
//   node --test Temalar/skins/boost-view.integration.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { loadDashboard } = require('./skin-sandbox.js');

const MODULE_SOURCE = fs.readFileSync(path.join(__dirname, '..', 'boost-view.js'), 'utf8');

function tableWrapHidden(dash) {
  return dash.document.getElementById('splitAppsBody').closest('.overflow-x-auto').classList.contains('hidden');
}

const APPS = [
  { id: 1, processName: 'cs2', displayName: 'Counter-Strike 2', action: 'vpn', isRunning: true, exePath: 'C:\\Games\\cs2\\cs2.exe' },
  { id: 2, processName: 'chrome', displayName: 'Google Chrome', action: 'direct', isRunning: true, exePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' },
  { id: 3, processName: 'fortnite', displayName: 'Fortnite', action: 'block', isRunning: false, exePath: 'C:\\Games\\Fortnite\\FortniteGame.exe' },
  { id: 4, value: 'escapefromtarkov.com', displayName: 'Escape from Tarkov API', action: 'warp', entryType: 'domain' }
];

async function boostWithModule(apps = APPS) {
  const dash = await loadDashboard();
  assert.ok(dash.openView('boost'), 'boost nav item must exist and be clickable');
  dash.window.eval(MODULE_SOURCE);
  dash.updateMonitorSnapshot({ apps, mode: 'manual', connected: false });
  return dash;
}

test('boost-view: toolbar appears next to the filter with details active by default', async () => {
  const dash = await boostWithModule();
  const toolbar = dash.document.querySelector('.boost-view-toolbar');
  assert.ok(toolbar, 'view-mode toolbar must be injected');
  assert.ok(toolbar.parentElement.contains(dash.document.getElementById('boostAppFilter')),
    'toolbar lives in the same row as the filter input');
  const buttons = dash.document.querySelectorAll('.boost-view-mode-btn');
  assert.equal(buttons.length, 4, 'details + 3 icon sizes');
  const active = dash.document.querySelector('.boost-view-mode-btn.active');
  assert.equal(active.dataset.viewMode, 'details', 'details is the default view mode');
  const groupSelect = dash.document.querySelector('.boost-view-toolbar select');
  assert.ok(groupSelect, 'group-by select must exist');
  assert.equal(groupSelect.value, 'none', 'no grouping by default');
  assert.ok(dash.document.getElementById('boostGridView').classList.contains('hidden'), 'grid hidden in details mode');
  assert.ok(!tableWrapHidden(dash), 'route table visible in details mode');
  dash.close();
});

test('boost-view: large mode swaps the table for a tile grid with icons', async () => {
  const dash = await boostWithModule();
  const largeBtn = dash.document.querySelector('.boost-view-mode-btn[data-view-mode="large"]');
  largeBtn.dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  const grid = dash.document.getElementById('boostGridView');
  assert.ok(grid.classList.contains('large'), 'grid host carries the large size class');
  assert.ok(!grid.classList.contains('hidden'), 'grid visible in icon mode');
  const tableWrap = dash.document.getElementById('splitAppsBody').closest('.overflow-x-auto');
  assert.ok(tableWrap.classList.contains('hidden'), 'route table hidden in icon mode');

  const tiles = grid.querySelectorAll('.boost-view-tile');
  assert.equal(tiles.length, 4, 'every app gets a tile');
  const tileText = grid.textContent;
  assert.ok(tileText.includes('Counter-Strike 2'), 'tile shows the display name');
  assert.ok(grid.querySelector('.boost-view-tile-exe')?.textContent.includes('cs2'), 'tile shows the exe name');
  assert.ok(tileText.includes('VPN'), 'tile shows the route badge');
  assert.ok(grid.querySelector('.boost-view-status.on'), 'running app carries the live status dot');
  assert.ok(grid.querySelector('.app-icon'), 'tile starts with the letter placeholder');

  // The host resolves icons -> setAppIcons lands in the module cache and the
  // grid re-renders with the REAL executable icon.
  dash.window.setAppIcons({
    icons: { 'c:\\games\\cs2\\cs2.exe': 'data:image/png;base64,AAAA' }
  });
  const iconImg = grid.querySelector('.boost-view-tile[data-process-name="cs2"] img.app-icon');
  assert.ok(iconImg, 'tile swapped the letter placeholder for the shell icon');
  assert.equal(iconImg.getAttribute('src'), 'data:image/png;base64,AAAA');
  dash.close();
});

test('boost-view: small/medium modes use smaller icon tiles', async () => {
  const dash = await boostWithModule();
  dash.document.querySelector('.boost-view-mode-btn[data-view-mode="small"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const grid = dash.document.getElementById('boostGridView');
  assert.ok(grid.classList.contains('small'), 'small class applied');
  assert.ok(grid.querySelector('.boost-view-tile .app-icon'), 'small tiles carry icon markup');
  assert.equal(grid.querySelectorAll('.boost-view-tile-exe').length, 0, 'small tiles hide the exe line');

  dash.document.querySelector('.boost-view-mode-btn[data-view-mode="medium"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.ok(grid.classList.contains('medium'), 'medium class applied');
  assert.equal(grid.querySelectorAll('.boost-view-tile-exe').length, 4, 'medium tiles show the exe line again');
  assert.ok([...grid.querySelectorAll('.boost-view-tile-exe')].some(el => el.textContent.includes('chrome')), 'exe line carries the process name');
  dash.close();
});

test('boost-view: grouping by route/status/type renders collapsible headers', async () => {
  const dash = await boostWithModule();
  dash.document.querySelector('.boost-view-mode-btn[data-view-mode="medium"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const groupSelect = dash.document.querySelector('.boost-view-toolbar select');

  groupSelect.value = 'route';
  groupSelect.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  let heads = dash.document.querySelectorAll('#boostGridView .boost-view-group-head');
  assert.equal(heads.length, 4, 'one group per distinct route');
  const headTexts = [...heads].map(h => h.textContent);
  assert.ok(headTexts.some(t => t.includes('VPN') && t.includes('1')), 'VPN group counts its entry');
  assert.ok(headTexts.some(t => t.includes('Direct')), 'Direct group present');
  assert.ok(headTexts.some(t => t.includes('WARP')), 'WARP group present');

  // Collapse the VPN group.
  const vpnHead = [...heads].find(h => h.textContent.includes('VPN'));
  vpnHead.dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const vpnGroup = vpnHead.closest('.boost-view-group');
  assert.ok(vpnGroup.classList.contains('collapsed'), 'clicking the header collapses the group');
  assert.equal(vpnHead.getAttribute('aria-expanded'), 'false');

  groupSelect.value = 'status';
  groupSelect.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  heads = dash.document.querySelectorAll('#boostGridView .boost-view-group-head');
  assert.equal(heads.length, 2, 'running + idle groups');
  assert.ok([...heads].some(h => h.textContent.includes('Running')), 'running group label');

  groupSelect.value = 'type';
  groupSelect.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  heads = dash.document.querySelectorAll('#boostGridView .boost-view-group-head');
  assert.equal(heads.length, 2, 'applications + domain groups');
  assert.ok([...heads].some(h => h.textContent.includes('Domain')), 'domain rules grouped separately');
  dash.close();
});

test('boost-view: tile route select and delete post the same contracts as the table', async () => {
  const dash = await boostWithModule();
  dash.document.querySelector('.boost-view-mode-btn[data-view-mode="large"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  const sel = dash.document.querySelector('#boostGridView select[data-route-process="cs2"]');
  assert.ok(sel, 'tile carries a route select');
  sel.value = 'block';
  sel.dispatchEvent(new dash.window.Event('change', { bubbles: true }));

  const del = dash.document.querySelector('#boostGridView button[data-remove-process="cs2"]');
  assert.ok(del, 'tile carries a delete button');
  del.dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  const routePosts = dash.postsWith('set_app_route');
  assert.equal(routePosts.length, 1);
  assert.deepEqual(routePosts[0], { action: 'set_app_route', processName: 'cs2', displayName: 'Counter-Strike 2', route: 'block' });
  assert.deepEqual(dash.postsWith('remove_app'), [{ action: 'remove_app', processName: 'cs2' }]);
  dash.close();
});

test('boost-view: grid honours the boost filter and shows an empty note', async () => {
  const dash = await boostWithModule();
  dash.document.querySelector('.boost-view-mode-btn[data-view-mode="medium"]')
    .dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const filter = dash.document.getElementById('boostAppFilter');
  filter.value = 'fortnite';
  filter.dispatchEvent(new dash.window.Event('input', { bubbles: true }));
  let tiles = dash.document.querySelectorAll('#boostGridView .boost-view-tile');
  assert.equal(tiles.length, 1, 'filter applies to the grid');
  assert.ok(tiles[0].textContent.includes('Fortnite'));

  filter.value = 'zzz-no-match';
  filter.dispatchEvent(new dash.window.Event('input', { bubbles: true }));
  assert.ok(dash.document.querySelector('#boostGridView .boost-view-empty'), 'empty note when nothing matches');

  filter.value = '';
  filter.dispatchEvent(new dash.window.Event('input', { bubbles: true }));
  tiles = dash.document.querySelectorAll('#boostGridView .boost-view-tile');
  assert.equal(tiles.length, 4, 'clearing the filter restores all tiles');
  dash.close();
});

test('boost-view: app.js asks the host for icons and renders them in both tables', async () => {
  const dash = await boostWithModule();

  // The snapshot push must ask the host for the shell icons of every exe path
  // (apps + connections + traffic) exactly once.
  const iconRequests = dash.postsWith('get_app_icons');
  assert.equal(iconRequests.length, 1, 'one icon request per snapshot');
  const requested = iconRequests[0].paths.slice().sort();
  assert.deepEqual(requested, [
    'c:\\games\\cs2\\cs2.exe',
    'c:\\games\\fortnite\\fortnitegame.exe',
    'c:\\program files\\google\\chrome\\application\\chrome.exe'
  ].sort(), 'every displayed exe path is requested (domain entries have no exe)');

  // Connection Monitor rows render the icon once the host answers.
  dash.openView('perf');
  dash.updateMonitorSnapshot({
    apps: APPS,
    connections: [{ processName: 'chrome', displayName: 'Google Chrome', exePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', pid: 42 }],
    mode: 'manual',
    connected: false
  });
  const connIcon = dash.document.querySelector('#monitorConnectionsBody .app-icon');
  assert.ok(connIcon, 'connection row carries the icon slot (letter placeholder first)');
  assert.equal(connIcon.tagName, 'SPAN', 'placeholder before the host answers');
  dash.window.setAppIcons({
    icons: { 'c:\\program files\\google\\chrome\\application\\chrome.exe': 'data:image/png;base64,BBBB' }
  });
  const connImg = dash.document.querySelector('#monitorConnectionsBody img.app-icon');
  assert.ok(connImg, 'connection row swapped to the real icon after setAppIcons');
  assert.equal(connImg.getAttribute('src'), 'data:image/png;base64,BBBB');
  assert.ok(dash.document.querySelector('#splitAppsBody img.app-icon'), 'boost table also re-rendered with icons');
  dash.close();
});

test('boost-view: repeated snapshots do not re-request already cached icons', async () => {
  const dash = await boostWithModule();
  // The host answered every requested path: the cache is now warm, so the next
  // snapshot with the same apps must not ask for any icon again.
  dash.window.setAppIcons({
    icons: {
      'c:\\games\\cs2\\cs2.exe': 'data:image/png;base64,AAAA',
      'c:\\program files\\google\\chrome\\application\\chrome.exe': 'data:image/png;base64,BBBB',
      'c:\\games\\fortnite\\fortnitegame.exe': 'data:image/png;base64,CCCC'
    }
  });
  dash.updateMonitorSnapshot({ apps: APPS, mode: 'manual', connected: false });
  const requests = dash.postsWith('get_app_icons');
  assert.equal(requests.length, 1, 'warm cache -> no second request');
  dash.close();
});