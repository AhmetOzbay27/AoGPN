// ============================================================================
// Skin icons + NEXUS Game Boost view module integration test
// ----------------------------------------------------------------------------
// Boots the REAL skins (real skin.html + real skin.js) through the shared
// sandbox with a bridge that carries shell icons (skinBridge.appIcons — the
// live cache the main dashboard populates from the host AppIconService) and
// verifies:
//   • NEXUS renders the real exe icon in the Game Profiles table rows, the
//     dashboard game list and the GPN panel boost cards (letter placeholder
//     when the host has not resolved the icon yet),
//   • the standalone NEXUS boost-view.js module adds the Windows-style view
//     modes: toolbar in the filter row, large-mode tile grid with real icons,
//     grouping by route/status/type with collapsible headers, and the same
//     set_app_route / remove_app host contracts as the table,
//   • CYBER source cards and INFRA app rows render the real exe icon.
//
// Run with:
//   node --test Temalar/skins/skin-icons.integration.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { loadSkin } = require('./skin-sandbox.js');

const NEXUS_MODULE_SOURCE = fs.readFileSync(path.join(__dirname, 'nexus', 'boost-view.js'), 'utf8');

const APPS = [
  { processName: 'cs2', displayName: 'Counter-Strike 2', action: 'vpn', isRunning: true, exePath: 'C:\\Games\\cs2\\cs2.exe', latencyText: '18 ms' },
  { processName: 'chrome', displayName: 'Google Chrome', action: 'direct', isRunning: true, exePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' },
  { processName: 'fortnite', displayName: 'Fortnite', action: 'block', isRunning: false, exePath: 'C:\\Games\\Fortnite\\FortniteGame.exe' }
];

const ICONS = {
  'c:\\games\\cs2\\cs2.exe': 'data:image/png;base64,AAAA',
  'c:\\program files\\google\\chrome\\application\\chrome.exe': 'data:image/png;base64,BBBB'
  // fortnite intentionally has NO resolved icon -> letter placeholder path
};

function bootNexus() {
  return loadSkin('nexus', {
    connected: true,
    mode: 'gpn',
    splitMode: 'off',
    appIcons: { ...ICONS },
    monitorSnapshot: { apps: APPS.map(a => ({ ...a })), invertManualRouting: false }
  }, { now: 1000 });
}

function clickNav(skin, view) {
  const btn = skin.document.querySelector('.nx-nav button[data-nx-view="' + view + '"]');
  assert.ok(btn, 'nav button for ' + view + ' exists');
  btn.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
}

function click(skin, el) {
  el.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
}

// ---------------------------------------------------------------------------
// NEXUS — real icons in the skin's own boost surfaces
// ---------------------------------------------------------------------------

test('NEXUS: Game Profiles table rows, game list and boost cards render the real exe icons', () => {
  const skin = bootNexus();
  const d = skin.document;

  // Dashboard GPN panel boost cards (vpn-routed apps).
  const cardImg = d.querySelector('.nx-boostIcon img');
  assert.ok(cardImg, 'boost card carries the real icon');
  assert.equal(cardImg.getAttribute('src'), ICONS['c:\\games\\cs2\\cs2.exe']);

  // Dashboard game list.
  const gameImg = d.querySelector('#nxGameList .nx-gameIcon img');
  assert.ok(gameImg, 'game list row carries the real icon');

  // Game Profiles table.
  clickNav(skin, 'games');
  const rows = d.querySelectorAll('#nxGameProfiles .nx-trow');
  assert.equal(rows.length, 3, 'boost table renders every app');
  const cs2Row = [...rows].find(r => r.dataset.nxPname === 'cs2');
  const cs2Icon = cs2Row.querySelector('.nx-progCell .nx-appIco img');
  assert.ok(cs2Icon, 'boost table row carries the real icon');
  assert.equal(cs2Icon.getAttribute('src'), ICONS['c:\\games\\cs2\\cs2.exe']);
  assert.ok(cs2Row.querySelector('.nx-progCell b'), 'app name still renders next to the icon');
  skin.close();
});

test('NEXUS: apps without a resolved icon fall back to the letter placeholder', () => {
  const skin = bootNexus();
  const d = skin.document;
  clickNav(skin, 'games');
  const fortniteRow = [...d.querySelectorAll('#nxGameProfiles .nx-trow')].find(r => r.dataset.nxPname === 'fortnite');
  const tile = fortniteRow.querySelector('.nx-appIco');
  assert.equal(tile.tagName, 'SPAN', 'placeholder is a span (not an img)');
  assert.ok(tile.textContent.includes('FO'), 'placeholder shows the two-letter fallback');
  skin.close();
});

// ---------------------------------------------------------------------------
// NEXUS — standalone boost-view.js module
// ---------------------------------------------------------------------------

function bootNexusWithModule() {
  const skin = bootNexus();
  skin.window.eval(NEXUS_MODULE_SOURCE);
  clickNav(skin, 'games');
  // The module subscribed to the bridge; a host push re-renders the view and
  // lets the module re-sync (inject toolbar + grid) exactly like the live app.
  skin.push();
  return skin;
}

test('NEXUS module: toolbar appears in the filter row with details active by default', () => {
  const skin = bootNexusWithModule();
  const d = skin.document;
  const toolbar = d.querySelector('#nxGameProfiles .nx-filterRow .nxv-toolbar');
  assert.ok(toolbar, 'view-mode toolbar injected into the filter row');
  assert.equal(d.querySelectorAll('.nxv-modeBtn').length, 4, 'details + 3 icon sizes');
  assert.equal(d.querySelector('.nxv-modeBtn.active').dataset.viewMode, 'details', 'details is the default');
  const groupSelect = d.querySelector('.nxv-toolbar select');
  assert.equal(groupSelect.options.length, 4, 'route/status/type grouping options');
  assert.equal(groupSelect.value, 'none', 'no grouping by default');
  const grid = d.getElementById('nxGameProfiles');
  assert.ok(grid.querySelector('.nxv-gridHost.hidden'), 'grid host exists hidden in details mode');
  assert.ok(!d.querySelector('#nxGameProfiles .nx-tbl.nxv-hidden'), 'table visible in details mode');
  skin.close();
});

test('NEXUS module: large mode swaps the table for an icon tile grid', () => {
  const skin = bootNexusWithModule();
  const d = skin.document;
  click(skin, d.querySelector('.nxv-modeBtn[data-view-mode="large"]'));

  const table = d.querySelector('#nxGameProfiles .nx-tbl');
  assert.ok(table.classList.contains('nxv-hidden'), 'route table hidden in icon mode');
  const grid = d.querySelector('#nxGameProfiles .nxv-gridHost');
  assert.ok(grid && grid.classList.contains('large'), 'grid host carries the large size class');
  assert.ok(!grid.classList.contains('hidden'), 'grid visible in icon mode');

  const tiles = grid.querySelectorAll('.nxv-tile');
  assert.equal(tiles.length, 3, 'every app gets a tile');
  const cs2Tile = [...tiles].find(t => t.dataset.processName === 'cs2');
  assert.ok(cs2Tile, 'tile for cs2 exists');
  const img = cs2Tile.querySelector('.nxv-ico[src="data:image/png;base64,AAAA"]');
  assert.ok(img, 'tile shows the REAL executable icon');
  assert.ok(cs2Tile.querySelector('.nxv-status.on'), 'running app carries the live status dot');
  assert.ok(cs2Tile.textContent.includes('Counter-Strike 2'), 'tile shows the display name');
  assert.ok(cs2Tile.textContent.includes('cs2'), 'tile shows the exe name');
  assert.ok(cs2Tile.querySelector('.nxv-badge.proxy'), 'vpn route renders the badge');

  // The fortnite tile has no resolved icon -> letter placeholder.
  const fortniteTile = [...tiles].find(t => t.dataset.processName === 'fortnite');
  assert.ok(fortniteTile.querySelector('.nxv-ico') && !fortniteTile.querySelector('.nxv-ico[src]'),
    'unresolved icon falls back to the letter tile');
  skin.close();
});

test('NEXUS module: grouping by route/status/type renders collapsible headers', () => {
  const skin = bootNexusWithModule();
  const d = skin.document;
  click(skin, d.querySelector('.nxv-modeBtn[data-view-mode="medium"]'));
  const groupSelect = d.querySelector('.nxv-toolbar select');

  groupSelect.value = 'route';
  groupSelect.dispatchEvent(new skin.window.Event('change', { bubbles: true }));
  let heads = d.querySelectorAll('.nxv-groupHead');
  assert.equal(heads.length, 3, 'one group per distinct route (VPN / Direct / Block)');
  assert.ok([...heads].some(h => h.textContent.includes('VPN') && h.textContent.includes('1')), 'VPN group counts its entry');

  const vpnHead = [...heads].find(h => h.textContent.includes('VPN'));
  click(skin, vpnHead);
  assert.ok(vpnHead.closest('.nxv-group').classList.contains('collapsed'), 'clicking the header collapses the group');
  assert.equal(vpnHead.getAttribute('aria-expanded'), 'false');

  groupSelect.value = 'status';
  groupSelect.dispatchEvent(new skin.window.Event('change', { bubbles: true }));
  heads = d.querySelectorAll('.nxv-groupHead');
  assert.equal(heads.length, 2, 'running + idle groups');
  assert.ok([...heads].some(h => h.textContent.includes('Running')), 'running group label');

  groupSelect.value = 'type';
  groupSelect.dispatchEvent(new skin.window.Event('change', { bubbles: true }));
  heads = d.querySelectorAll('.nxv-groupHead');
  assert.equal(heads.length, 1, 'all apps group under Applications');
  assert.ok(heads[0].textContent.includes('Applications'), 'type group label');
  skin.close();
});

test('NEXUS module: tile route select and delete post the same contracts as the table', () => {
  const skin = bootNexusWithModule();
  const d = skin.document;
  click(skin, d.querySelector('.nxv-modeBtn[data-view-mode="large"]'));

  const sel = d.querySelector('.nxv-tile select[data-route-process="cs2"]');
  assert.ok(sel, 'tile carries a route select');
  const del = d.querySelector('.nxv-tile button[data-remove-process="cs2"]');
  assert.ok(del, 'tile carries a delete button');
  skin.bridge._posts.length = 0;
  sel.value = 'block';
  sel.dispatchEvent(new skin.window.Event('change', { bubbles: true }));
  click(skin, del);

  const routePosts = skin.bridge._posts.filter(p => p.action === 'set_app_route');
  assert.equal(routePosts.length, 1);
  // Posts live in the sandbox (jsdom) realm — normalize before comparing.
  assert.deepEqual(JSON.parse(JSON.stringify(routePosts[0])), { action: 'set_app_route', processName: 'cs2', displayName: 'Counter-Strike 2', route: 'block' });
  const removePosts = skin.bridge._posts.filter(p => p.action === 'remove_app');
  assert.deepEqual(JSON.parse(JSON.stringify(removePosts)), [{ action: 'remove_app', processName: 'cs2' }]);
  skin.close();
});

test('NEXUS module: survives the skin re-rendering its view (toolbar re-injected, grid refreshed)', () => {
  const skin = bootNexusWithModule();
  const d = skin.document;
  click(skin, d.querySelector('.nxv-modeBtn[data-view-mode="medium"]'));
  assert.ok(d.querySelector('.nxv-gridHost'), 'grid rendered before the re-render');

  // Simulate the skin's 2 s safety poll re-rendering the whole games view.
  skin.tickTimers();
  skin.push();

  const toolbar = d.querySelector('#nxGameProfiles .nx-filterRow .nxv-toolbar');
  assert.ok(toolbar, 'toolbar re-injected after the skin re-render');
  assert.ok(d.querySelector('.nxv-modeBtn.active[data-view-mode="medium"]'), 'chosen mode survives the re-render');
  const grid = d.querySelector('#nxGameProfiles .nxv-gridHost');
  assert.ok(grid && grid.classList.contains('medium') && !grid.classList.contains('hidden'), 'grid re-rendered in the chosen mode');
  assert.equal(grid.querySelectorAll('.nxv-tile').length, 3, 'tiles refreshed from the live snapshot');
  skin.close();
});

// ---------------------------------------------------------------------------
// CYBER + INFRA — real icons in their app surfaces
// ---------------------------------------------------------------------------

test('CYBER: source cards render the real exe icons', () => {
  const skin = loadSkin('cyber', {
    connected: true,
    appIcons: { ...ICONS },
    monitorSnapshot: { apps: APPS.map(a => ({ ...a })), invertManualRouting: false }
  }, { now: 1000 });
  const d = skin.document;
  const cards = d.querySelectorAll('#cySources .cy-target');
  assert.ok(cards.length >= 3, 'source cards render');
  const cs2Card = [...cards].find(c => c.dataset.cyPname === 'cs2');
  const img = cs2Card.querySelector('.cy-appIco img');
  assert.ok(img, 'source card carries the real icon');
  assert.equal(img.getAttribute('src'), ICONS['c:\\games\\cs2\\cs2.exe']);
  const fortniteCard = [...cards].find(c => c.dataset.cyPname === 'fortnite');
  assert.ok(fortniteCard.querySelector('.cy-appIco') && !fortniteCard.querySelector('.cy-appIco img'),
    'unresolved icon falls back to the letter placeholder');
  skin.close();
});

test('INFRA: app rows render the real exe icons', () => {
  const skin = loadSkin('infra', {
    connected: true,
    appIcons: { ...ICONS },
    monitorSnapshot: { apps: APPS.map(a => ({ ...a })), invertManualRouting: false }
  }, { now: 1000 });
  const d = skin.document;
  const rows = d.querySelectorAll('#ifApps .if-row');
  assert.equal(rows.length, 3, 'app panel rows render');
  const cs2Row = [...rows].find(r => r.getAttribute('data-if-pname') === 'cs2');
  const img = cs2Row.querySelector('.if-appIco img');
  assert.ok(img, 'app row carries the real icon');
  assert.equal(img.getAttribute('src'), ICONS['c:\\games\\cs2\\cs2.exe']);
  assert.ok(cs2Row.querySelector('span'), 'app name still renders next to the icon');
  skin.close();
});

// ---------------------------------------------------------------------------
// Real host bridge — skinBridge.appIcons must expose the LIVE cache
// ---------------------------------------------------------------------------
// The tests above hand the skins a bridge stub, so they can never catch a
// broken REAL getter. Boot the real dashboard (real app.js + real views.js),
// push a monitor snapshot and icons through the same window.setAppIcons path
// the WPF host uses, and assert the getter the skins read at render time
// returns the resolved icons instead of throwing.

test('REAL bridge: skinBridge.appIcons returns the live icon cache after setAppIcons', async () => {
  const { loadDashboard } = require('./skin-sandbox.js');
  const dash = await loadDashboard({});
  const w = dash.window;

  w.updateMonitorSnapshot({
    connected: false,
    mode: 'manual',
    apps: APPS.map(a => ({ ...a })),
    connections: [],
    activeConnectionCount: 0
  });
  w.setAppIcons({ icons: { ...ICONS } });

  // The getter must not throw (it regressed to a ReferenceError when the icon
  // cache moved into the views module) and must expose every resolved icon.
  let icons;
  assert.doesNotThrow(() => { icons = w.skinBridge.appIcons; }, 'skinBridge.appIcons must not throw');
  assert.ok(icons, 'getter returns the cache');
  assert.equal(icons['c:\\games\\cs2\\cs2.exe'], ICONS['c:\\games\\cs2\\cs2.exe'], 'resolved icon is reachable');
  assert.equal(icons['c:\\program files\\google\\chrome\\application\\chrome.exe'], ICONS['c:\\program files\\google\\chrome\\application\\chrome.exe'], 'second resolved icon is reachable');
  dash.close();
});

test('REAL bridge: skins swap the letter placeholder for the real icon on the same cache', async () => {
  const { loadDashboard } = require('./skin-sandbox.js');
  const dash = await loadDashboard({});
  const w = dash.window;

  w.updateMonitorSnapshot({
    connected: false,
    mode: 'manual',
    apps: APPS.map(a => ({ ...a })),
    connections: [],
    activeConnectionCount: 0
  });
  w.setAppIcons({ icons: { ...ICONS } });

  // The main tables render the real icon for cs2 and keep the placeholder for
  // fortnite (unresolved) — same cache the skins read through the bridge.
  const cs2Row = [...dash.document.querySelectorAll('#splitAppsBody tr[data-process-name="cs2"]')][0];
  assert.ok(cs2Row && cs2Row.querySelector('img.app-icon'), 'boost table row uses the real icon');
  assert.equal(cs2Row.querySelector('img.app-icon').getAttribute('src'), ICONS['c:\\games\\cs2\\cs2.exe']);
  const fortniteRow = [...dash.document.querySelectorAll('#splitAppsBody tr[data-process-name="fortnite"]')][0];
  assert.ok(fortniteRow && fortniteRow.querySelector('span.app-icon'), 'unresolved app keeps the letter placeholder');
  dash.close();
});