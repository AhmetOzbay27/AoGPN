// ============================================================================
// Launcher bypass düzenleyicisi (Ayarlar → GPN) — gerçek dashboard üzerinde:
// host push'u (applySettings → launcherBypasses) satırları doldurur, "Add
// Launcher" önayarı satır ekler, satır silme listeyi günceller ve kaydetme
// akışı (save_settings) launcherBypasses JSON'unu payload'a taşır — host bu
// değeri GuiItem.LauncherBypassesJson'a yazar (sonraki bağlantıda uygulanır).
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { loadDashboard } = require('./skin-sandbox.js');

function click(dash, el) {
  el.dispatchEvent(new dash.window.MouseEvent('click', { bubbles: true, cancelable: true }));
}

test('dashboard: launcher bypass editor renders rows, adds presets and saves JSON', async () => {
  const dash = await loadDashboard();
  const { document } = dash;
  const list = document.getElementById('launcherBypassList');
  assert.ok(list, 'launcher bypass list container exists in the GPN settings panel');

  // Host push: varsayılan BSG girişi satır olarak çizilir.
  dash.window.applySettings({
    launcherBypasses: JSON.stringify([
      { name: 'BSG', egress: 'warp', enabled: true, domains: ['escapefromtarkov.com', 'profile.tarkov.com'] }
    ])
  });
  assert.equal(list.querySelectorAll('.launcher-bypass-row').length, 1, 'one row rendered from the push');
  assert.equal(list.querySelector('[data-lb-name]').value, 'BSG', 'row carries the launcher name');

  // "Add Launcher" → seçili önayar (Epic Games) satır olarak eklenir.
  const preset = document.getElementById('launcherBypassPreset');
  preset.value = 'Epic Games';
  click(dash, document.getElementById('launcherBypassAddBtn'));
  assert.equal(list.querySelectorAll('.launcher-bypass-row').length, 2, 'preset added a second row');
  const epicDomains = [...list.querySelectorAll('[data-lb-domains]')].at(-1).value;
  assert.ok(epicDomains.includes('epicgames.com'), 'preset domains filled in');

  // Kaydetme akışı: save_settings payload'ı launcherBypasses JSON'unu taşır.
  click(dash, document.getElementById('settingsSaveBtn'));
  const post = dash.postsWith('save_settings').at(-1);
  assert.ok(post, 'save_settings posted');
  assert.ok(post.settings && typeof post.settings.launcherBypasses === 'string', 'settings carry launcherBypasses JSON');
  const saved = JSON.parse(post.settings.launcherBypasses);
  assert.equal(saved.length, 2, 'both rows serialized');
  assert.deepEqual(saved.map(s => s.name), ['BSG', 'Epic Games'], 'names preserved in order');
  assert.equal(saved[1].egress, 'warp', 'preset egress serialized');
  assert.equal(saved[1].enabled, true, 'preset enabled flag serialized');

  // Satır silme → liste güncellenir ve kayıt tek satıra düşer. (jsdom'da host
  // yanıtı gelmediği için kaydetme düğmesi kilitli kalır — gerçek akıştaki ack
  // setSettingsSaveResult ile simüle edilir, böylece ikinci kayıt da işlenir.)
  click(dash, list.querySelector('[data-lb-remove]'));
  assert.equal(list.querySelectorAll('.launcher-bypass-row').length, 1, 'row removed');
  dash.window.setSettingsSaveResult({ ok: true, needReboot: false });
  click(dash, document.getElementById('settingsSaveBtn'));
  const post2 = dash.postsWith('save_settings').at(-1);
  assert.equal(JSON.parse(post2.settings.launcherBypasses).length, 1, 'saved list drops removed launcher');

  dash.close();
});