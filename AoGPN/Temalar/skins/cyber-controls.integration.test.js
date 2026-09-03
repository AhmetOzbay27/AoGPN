// ============================================================================
// CYBER skin controls — bottom-left SYSTEM CONTROLS + bottom-right SETTINGS
// ----------------------------------------------------------------------------
// The cyberpunk skin carries the app's real routing/settings features below the
// side panels: ROUTE MODE + SPLIT DIRECTION pills, an AUTO RECONNECT switch,
// and a SETTINGS panel (system proxy toggle + proxy mode + protocol select).
// Every control must render from the real skin.html and post the exact host
// message the standard dashboard uses (set_split_mode, set_split_direction,
// set_auto_reconnect, toggle_system_proxy, set_system_proxy_mode,
// set_protocol_preference). Booted through the shared sandbox (loadSkin) with
// the real document, real css contract and the bridge stub.
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { loadSkin } = require('./skin-sandbox.js');

function bootCyber(bridgeData) {
  return loadSkin('cyber', bridgeData || {});
}

// The bridge stub captures messages created inside the jsdom realm, whose
// Object prototype differs from Node's — normalize via JSON round-trip.
function lastPost(skin, action) {
  return JSON.parse(JSON.stringify(skin.bridge._posts.filter(p => p.action === action).at(-1)));
}

test('cyber: bottom panels render with all system controls and settings', () => {
  const skin = bootCyber();
  const d = skin.dom.window.document;
  assert.ok(d.querySelector('.cy-left-bottom'), 'bottom-left SYSTEM CONTROLS panel exists');
  assert.ok(d.querySelector('.cy-right-bottom'), 'bottom-right SETTINGS panel exists');
  assert.equal(d.querySelectorAll('[data-cy-split]').length, 3, 'ROUTE MODE pills: off/vpn/manual');
  assert.equal(d.querySelectorAll('[data-cy-dir]').length, 2, 'SPLIT DIRECTION pills: whitelist/blacklist');
  assert.ok(d.getElementById('cyAutoReconnect'), 'AUTO RECONNECT switch exists');
  assert.ok(d.getElementById('cyProxyToggle'), 'SYSTEM PROXY toggle exists');
  assert.equal(d.getElementById('cyProxyMode').options.length, 4, 'proxy modes: clear/set/unchanged/pac');
  assert.equal(d.getElementById('cyProtocol').options.length, 5, 'protocols: auto/wireguard/mimic/hysteria2/openvpn');
  skin.dom.window.close();
});

test('cyber: ROUTE MODE pills post set_split_mode and mirror the active state', () => {
  const skin = bootCyber();
  const d = skin.dom.window.document;
  const click = (sel) => d.querySelector(sel).dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  click('[data-cy-split="manual"]');
  assert.deepEqual(lastPost(skin, 'set_split_mode'), { action: 'set_split_mode', mode: 'manual' });
  assert.ok(d.querySelector('[data-cy-split="manual"]').classList.contains('active'), 'picked pill active');
  assert.ok(!d.querySelector('[data-cy-split="off"]').classList.contains('active'), 'others inactive');

  click('[data-cy-split="vpn"]');
  assert.deepEqual(lastPost(skin, 'set_split_mode'), { action: 'set_split_mode', mode: 'vpn' });
  assert.ok(d.querySelector('[data-cy-split="vpn"]').classList.contains('active'));
  skin.dom.window.close();
});

test('cyber: SPLIT DIRECTION pills post set_split_direction with the invert string', () => {
  const skin = bootCyber();
  const d = skin.dom.window.document;
  const click = (sel) => d.querySelector(sel).dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  click('[data-cy-dir="blacklist"]');
  assert.deepEqual(lastPost(skin, 'set_split_direction'),
    { action: 'set_split_direction', invert: 'true' }, 'blacklist -> invert=true');
  assert.ok(d.querySelector('[data-cy-dir="blacklist"]').classList.contains('active'));

  click('[data-cy-dir="whitelist"]');
  assert.deepEqual(lastPost(skin, 'set_split_direction'),
    { action: 'set_split_direction', invert: 'false' }, 'whitelist -> invert=false');
  assert.ok(d.querySelector('[data-cy-dir="whitelist"]').classList.contains('active'));
  skin.dom.window.close();
});

test('cyber: AUTO RECONNECT switch posts set_auto_reconnect and flips the on state', () => {
  const skin = bootCyber({ autoReconnect: true });
  const d = skin.dom.window.document;
  const ar = d.getElementById('cyAutoReconnect');
  assert.ok(ar.classList.contains('on'), 'booted with auto-reconnect on');
  assert.equal(ar.getAttribute('aria-checked'), 'true');

  ar.dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'set_auto_reconnect'), { action: 'set_auto_reconnect', enabled: false });
  assert.ok(!ar.classList.contains('on'), 'switch flipped off');
  assert.equal(ar.getAttribute('aria-checked'), 'false');
  skin.dom.window.close();
});

test('cyber: SETTINGS proxy toggle + mode + protocol post the exact host messages', () => {
  const skin = bootCyber();
  const d = skin.dom.window.document;
  const click = (el) => el.dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  // System proxy toggle: OFF -> posts toggle_system_proxy, label flips to ON.
  const pt = d.getElementById('cyProxyToggle');
  assert.equal(pt.textContent, 'PROXY OFF', 'boot label');
  click(pt);
  assert.deepEqual(lastPost(skin, 'toggle_system_proxy'), { action: 'toggle_system_proxy' });
  assert.equal(pt.textContent, 'PROXY ON', 'label flips after the toggle');

  // Proxy mode select -> set_system_proxy_mode.
  const pm = d.getElementById('cyProxyMode');
  pm.value = '3';
  pm.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(lastPost(skin, 'set_system_proxy_mode'), { action: 'set_system_proxy_mode', mode: 3 });

  // Protocol select -> set_protocol_preference.
  const pp = d.getElementById('cyProtocol');
  pp.value = 'wireguard';
  pp.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(lastPost(skin, 'set_protocol_preference'), { action: 'set_protocol_preference', protocol: 'wireguard' });
  skin.dom.window.close();
});

test('cyber: SETTINGS LANGUAGE select renders the bridge language list and current value', () => {
  const skin = bootCyber({
    LANGUAGES: [
      { code: 'en', name: 'English' },
      { code: 'tr', name: 'Türkçe' },
      { code: 'ru', name: 'Русский' }
    ],
    language: 'tr'
  });
  const d = skin.dom.window.document;
  const sel = d.getElementById('cyLanguage');
  assert.ok(sel, 'LANGUAGE select exists in SETTINGS');
  assert.equal(sel.options.length, 3, 'options come from the bridge LANGUAGES array');
  assert.equal(sel.options[0].value, 'en');
  assert.equal(sel.options[1].value, 'tr');
  assert.equal(sel.options[2].value, 'ru');
  assert.equal(sel.value, 'tr', 'current app language is selected');
  skin.dom.window.close();
});

test('cyber: LANGUAGE change calls bridge setLanguage (whole-app switch) and mirrors the selection', () => {
  const calls = [];
  const skin = bootCyber({
    LANGUAGES: [{ code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }],
    setLanguage(lang) { calls.push(lang); }
  });
  const d = skin.dom.window.document;
  const sel = d.getElementById('cyLanguage');
  assert.equal(sel.value, 'en', 'boots with the bridge language');

  sel.value = 'tr';
  sel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(calls, ['tr'], 'switches the WHOLE app through the same channel as the top-bar picker');
  assert.equal(skin.bridge.language, 'tr', 'skin state mirrored (SET language)');
  assert.equal(sel.value, 'tr', 'select reflects the new language after sync');
  skin.dom.window.close();
});

test('cyber: static texts resolve through skin.cyber.* scoped translations', () => {
  // The real bridge t() resolves skin.cyber.<key> -> shared <key> -> raw key;
  // the stub replaces t with a function that applies the same scoping (the
  // sandbox's default t is unscoped, so a bare key would never hit the dict).
  const dict = {
    'skin.cyber.panel.source': 'AKILLI SPLIT // ENJEKSİYON',
    'skin.cyber.split.vpn': 'GLOBAL VPN (TR)',
    'skin.cyber.ctl.language': 'DİL',
    'skin.cyber.proxy.off': 'PROXY KAPALI'
  };
  const skin = bootCyber({ t: (key) => dict['skin.cyber.' + key] || key });
  const d = skin.dom.window.document;
  assert.equal(d.querySelector('.cy-left .cy-panel-title').textContent, 'AKILLI SPLIT // ENJEKSİYON', 'panel title from scoped key');
  assert.equal(d.querySelector('[data-cy-split="vpn"]').textContent, 'GLOBAL VPN (TR)', 'pill label from scoped key');
  assert.equal(d.querySelector('[data-cy-i18n="ctl.language"]').textContent, 'DİL', 'control label from scoped key');
  assert.equal(d.getElementById('cyProxyToggle').textContent, 'PROXY KAPALI', 'proxy toggle label from scoped key');
  skin.dom.window.close();
});

test('cyber: console operation logs resolve through skin.cyber.log.* translations', () => {
  const dict = {
    'skin.cyber.log.routeMode': '> ROTA MODU: {mode}',
    'skin.cyber.log.routeSet': '> ROTA {route} → {app}',
    'skin.cyber.log.tunneled': ' // TÜNELLENDİ',
    'skin.cyber.log.targetLocked': '> HEDEF KİLİTLENDİ: {name}',
    'skin.cyber.split.manual': 'GPN OYUN TÜNELİ'
  };
  const skin = bootCyber({
    t: (key) => dict['skin.cyber.' + key] || key,
    monitorSnapshot: { apps: [{ processName: 'cs2.exe', displayName: 'CS2', action: 'direct' }] }
  });
  const d = skin.dom.window.document;
  const click = (el) => el.dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  // The console typewriter appends one char per interval tick — advance it
  // until the newest line is fully typed, then assert the translated text.
  const typeUntil = (expected) => {
    for (let i = 0; i < 300 && (() => {
      const log = d.getElementById('cyTermLog');
      return log.lastElementChild ? log.lastElementChild.textContent : '';
    })() !== expected; i++) skin.tickTimers();
    const log = d.getElementById('cyTermLog');
    assert.equal(log.lastElementChild.textContent, expected);
  };

  // ROUTE MODE pill -> translated label + translated mode value.
  click(d.querySelector('[data-cy-split="manual"]'));
  typeUntil('> ROTA MODU: GPN OYUN TÜNELİ');

  // App route toggle -> ROUTE {route} → {app} + tunneled suffix.
  const sw = skin.getSwitch('cs2.exe');
  click(sw);
  typeUntil('> ROTA VPN → CS2.EXE // TÜNELLENDİ');

  // Node card -> TARGET LOCKED with the node name.
  click(d.querySelector('[data-cy-dst="dst0"]'));
  typeUntil('> HEDEF KİLİTLENDİ: Istanbul · TR-01 · 145.239.14.77');
  skin.dom.window.close();
});
