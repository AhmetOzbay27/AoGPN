// ============================================================================
// GPN settings removal + per-app WARP egress düğümü — gerçek dashboard üzerinde:
// Ayarlar → GPN paneli (VLESS bypass + launcher bypass editörü) tamamen
// kaldırıldı; bunun yerine her uygulama satırının "warp" rotası, düğüm
// listesinden seçilen bir düğümün egress'inden çıkar. Bu test, panelin
// yokluğunu, warp rotasındaki satırda düğüm seçicinin göründüğünü ve seçimin
// set_app_route + warpNodeIndexId ile host'a gittiğini doğrular.
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { loadDashboard } = require('./skin-sandbox.js');

function click(dash, el) {
  el.dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
}

test('dashboard: GPN settings tab and panel are removed (no VLESS bypass editor)', async () => {
  const dash = await loadDashboard();
  const { document } = dash;
  assert.ok(dash.openView('settings'), 'settings view opens');
  assert.equal(document.querySelector('[data-settings-tab="gpn"]'), null,
    'GPN settings tab button is gone');
  assert.equal(document.querySelector('[data-settings-panel="gpn"]'), null,
    'GPN settings panel is gone');
  assert.equal(document.getElementById('vlessBypassUri'), null, 'VLESS bypass textarea removed');
  assert.equal(document.getElementById('launcherBypassList'), null, 'launcher bypass editor removed');
  dash.close();
});

test('dashboard: WARP route shows the node picker and posts set_app_route with warpNodeIndexId', async () => {
  const dash = await loadDashboard();
  assert.ok(dash.openView('boost'), 'boost nav item must exist and be clickable');

  // Gerçek düğüm listesi host'tan gelir (İtalya / Almanya gibi).
  dash.window.updateNodeListAppend([
    { indexId: 'wg-it', name: 'İtalya', address: '92.4.220.236', port: 51820, protocol: 'WireGuard', country: 'IT' },
    { indexId: 'wg-de', name: 'Almanya', address: '130.61.223.36', port: 51820, protocol: 'WireGuard', country: 'DE' }
  ]);
  dash.window.updateNodeListDone('wg-it');

  dash.updateMonitorSnapshot({
    apps: [{ id: 1, processName: 'BsGLauncher.exe', displayName: 'BSG Launcher', action: 'warp', isRunning: true }],
    mode: 'manual',
    connected: false
  });

  // Warp rotasındaki satırın yanında düğüm seçici görünür.
  const warpSel = dash.document.querySelector('select[data-route-warp-node="BsGLauncher.exe"]');
  assert.ok(warpSel, 'warp route row offers the per-app node picker');
  const ids = [...warpSel.options].map(o => o.value);
  assert.ok(ids.includes('wg-it') && ids.includes('wg-de'), 'picker lists the existing nodes');

  // Düğüm seçimi set_app_route + warpNodeIndexId ile host'a gider.
  warpSel.value = 'wg-de';
  warpSel.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  const posts = dash.postsWith('set_app_route');
  const last = posts.at(-1);
  assert.ok(last, 'set_app_route posted');
  assert.equal(last.route, 'warp');
  assert.equal(last.warpNodeIndexId, 'wg-de');
  assert.equal(last.warpNodeName, 'Almanya');
  assert.equal(last.processName, 'BsGLauncher.exe');

  // Varsayılana dönüş düğümü temizler (route warp kalır).
  warpSel.value = '';
  warpSel.dispatchEvent(new dash.window.Event('change', { bubbles: true }));
  assert.equal(dash.postsWith('set_app_route').at(-1).warpNodeIndexId, '', 'clearing the picker clears the node');
  dash.close();
});

test('dashboard: warp entry with a node renders the node in the route badge', async () => {
  const dash = await loadDashboard();
  assert.ok(dash.openView('boost'), 'boost nav item must exist and be clickable');
  dash.window.updateNodeListAppend([
    { indexId: 'wg-za', name: 'Güney Afrika', address: '196.2.4.9', port: 51820, protocol: 'WireGuard', country: 'ZA' }
  ]);
  dash.window.updateNodeListDone('wg-za');
  dash.updateMonitorSnapshot({
    apps: [{ id: 1, processName: 'BsGLauncher.exe', displayName: 'BSG Launcher', action: 'warp', warpNodeIndexId: 'wg-za', isRunning: true }],
    mode: 'manual',
    connected: false
  });
  const row = dash.document.querySelector('tr[data-process-name="BsGLauncher.exe"]');
  assert.ok(row, 'row rendered');
  assert.ok(row.textContent.includes('WARP · Güney Afrika'), 'badge shows WARP · node name');
  dash.close();
});