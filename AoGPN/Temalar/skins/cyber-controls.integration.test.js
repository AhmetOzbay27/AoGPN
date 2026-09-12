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
  const headSel = d.getElementById('cyHeaderLang');
  assert.ok(headSel, 'LANGUAGE select exists in the header (visible on every view)');
  for (const s of [sel, headSel]) {
    assert.equal(s.options.length, 3, 'options come from the bridge LANGUAGES array');
    assert.equal(s.options[0].value, 'en');
    assert.equal(s.options[1].value, 'tr');
    assert.equal(s.options[2].value, 'ru');
    assert.equal(s.value, 'tr', 'current app language is selected');
  }
  skin.dom.window.close();
});

test('cyber: LANGUAGE change calls bridge setLanguage (whole-app switch) and mirrors the selection', () => {
  const calls = [];
  const skin = bootCyber({
    LANGUAGES: [{ code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }, { code: 'ru', name: 'Русский' }],
    setLanguage(lang) { calls.push(lang); }
  });
  const d = skin.dom.window.document;
  const sel = d.getElementById('cyLanguage');
  const headSel = d.getElementById('cyHeaderLang');
  assert.equal(sel.value, 'en', 'boots with the bridge language');
  assert.equal(headSel.value, 'en', 'header select boots with the bridge language');

  sel.value = 'tr';
  sel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(calls, ['tr'], 'SETTINGS select switches the WHOLE app through the same channel as the top-bar picker');
  assert.equal(skin.bridge.language, 'tr', 'skin state mirrored (SET language)');
  assert.equal(sel.value, 'tr', 'select reflects the new language after sync');
  assert.equal(headSel.value, 'tr', 'header select mirrors the new language after sync');

  // The header select is not a decoration — it drives the same whole-app switch.
  calls.length = 0;
  headSel.value = 'ru';
  headSel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(calls, ['ru'], 'header select switches the WHOLE app through the same channel');
  assert.equal(skin.bridge.language, 'ru', 'skin state mirrored from the header select');
  assert.equal(sel.value, 'ru', 'SETTINGS select mirrors the header selection');
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

// ---- extended settings: dashboard parity controls ----
test('cyber: SETTINGS TUN STACK + EFFECTS selects post set_tun_stack / set_effects_tier and mirror bridge state', () => {
  const skin = bootCyber({ tunStack: 'system', effectsTier: 'reduced' });
  const d = skin.dom.window.document;
  const ts = d.getElementById('cyTunStack');
  const ef = d.getElementById('cyEffects');
  assert.ok(ts && ef, 'TUN STACK + EFFECTS selects render in SETTINGS');
  assert.deepEqual(Array.from(ts.options).map(o => o.value), ['gvisor', 'system', 'mixed'], 'TUN stack options match the dashboard');
  assert.deepEqual(Array.from(ef.options).map(o => o.value), ['full', 'balanced', 'reduced'], 'effects tier options match the dashboard');
  assert.equal(ts.value, 'system', 'TUN stack mirrors bridge state');
  assert.equal(ef.value, 'reduced', 'effects tier mirrors bridge state');
  ts.value = 'gvisor';
  ts.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(lastPost(skin, 'set_tun_stack'), { action: 'set_tun_stack', stack: 'gvisor' }, 'TUN stack change posts the shared contract');
  ef.value = 'balanced';
  ef.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(lastPost(skin, 'set_effects_tier'), { action: 'set_effects_tier', tier: 'balanced' }, 'effects tier change posts the shared contract');
  skin.dom.window.close();
});

test('cyber: SETTINGS auto-game / recovery / failover switches post the shared host actions', () => {
  const skin = bootCyber({
    monitorSnapshot: { apps: [], autoConnectOnGameStart: true },
    gpnRecoveryWatch: true,
    gpnFailover: false
  });
  const d = skin.dom.window.document;
  const click = (sel) => d.querySelector(sel).dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const auto = d.getElementById('cyAutoGame');
  const rec = d.getElementById('cyRecovery');
  const fo = d.getElementById('cyFailover');
  assert.ok(auto && rec && fo, 'auto-game / recovery / failover switches render');
  assert.equal(auto.getAttribute('aria-checked'), 'true', 'autoConnectOnGameStart defaults to on');
  assert.equal(rec.getAttribute('aria-checked'), 'true', 'recovery watch mirrors bridge state');
  assert.equal(fo.getAttribute('aria-checked'), 'false', 'failover mirrors bridge state');
  click('#cyAutoGame');
  assert.deepEqual(lastPost(skin, 'set_auto_game_connect'), { action: 'set_auto_game_connect', enabled: false }, 'auto-game toggle posts set_auto_game_connect');
  click('#cyRecovery');
  assert.deepEqual(lastPost(skin, 'set_gpn_recovery_watch'), { action: 'set_gpn_recovery_watch', enabled: false }, 'recovery toggle posts set_gpn_recovery_watch');
  click('#cyFailover');
  assert.deepEqual(lastPost(skin, 'set_gpn_failover'), { action: 'set_gpn_failover', enabled: true }, 'failover toggle posts set_gpn_failover');
  assert.equal(fo.getAttribute('aria-checked'), 'true', 'failover flips optimistically');
  skin.dom.window.close();
});

test('cyber: SETTINGS proxy test button posts test_proxy and mirrors the host result', () => {
  const skin = bootCyber();
  const d = skin.dom.window.document;
  const btn = d.getElementById('cyProxyTest');
  assert.ok(btn, 'PROXY TEST button renders');
  assert.equal(btn.textContent, 'TEST', 'idle label');
  btn.dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'test_proxy'), { action: 'test_proxy' }, 'click posts the shared test_proxy contract');
  assert.ok(btn.classList.contains('running'), 'button enters the testing state');
  // Host answers through the bridge snapshot (setProxyTestResult -> skin push).
  skin.bridge.proxyTestResult = { ok: true, ms: 12 };
  skin.push();
  assert.ok(btn.classList.contains('ok'), 'button turns OK');
  assert.equal(btn.textContent, '12 ms', 'latency shown like the dashboard');
  skin.bridge.proxyTestResult = { ok: false, message: 'Unreachable' };
  skin.push();
  assert.ok(btn.classList.contains('fail'), 'button turns FAIL on an unreachable proxy');
  skin.dom.window.close();
});

// ---- console tabs: dashboard module parity ----
const CY_NODES = [
  { key: 'istanbul', code: 'TR', name: 'Istanbul · TR-01', addr: '145.239.14.77', ping: 18 },
  { key: 'frankfurt', code: 'DE', name: 'Frankfurt · DE-11', addr: '5.161.91.19', ping: 148 }
];
const CY_CONNS = [
  { processName: 'chrome.exe', displayName: 'Google Chrome', pid: 42, protocol: 'TCP', state: 'Established', remoteAddress: '142.250.74.14:443', countryText: 'United States', asnText: 'AS15169', routeTag: 'vpn', routeText: 'VPN', action: 'vpn' },
  { processName: 'sshd.exe', displayName: 'OpenSSH', pid: 77, protocol: 'TCP', state: 'Listen', remoteAddress: '0.0.0.0:22', routeTag: '', action: '' },
  { processName: 'discord.exe', displayName: 'Discord', pid: 88, protocol: 'UDP', state: 'Established', remoteAddress: '162.159.128.233:443', countryText: 'United States', routeTag: 'direct', routeText: 'Direct', action: 'direct' }
];

test('cyber: console tabs switch panels and render NODES / GPN / MONITOR / ABOUT content', () => {
  const skin = bootCyber({
    nodes: CY_NODES,
    gpnServers: [{ serverId: 'it-01', serverName: 'Italy · IT-01', isEnabled: true }],
    gpnServerProbes: [{ serverId: 'it-01', isSuccess: true, delayMs: 9, lossPercent: 0, udpStatus: 'open' }],
    monitorSnapshot: { apps: [{ processName: 'chrome.exe' }], connections: CY_CONNS },
    appInfo: { version: '7.26.54', appName: 'AO GPN' }
  });
  const d = skin.dom.window.document;
  const clickTab = (name) => d.querySelector('[data-cy-tab="' + name + '"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const panel = (name) => d.querySelector('[data-cy-panel="' + name + '"]');

  assert.equal(d.querySelectorAll('[data-cy-tab]').length, 5, 'five console tabs render');
  assert.equal(panel('terminal').hidden, false, 'terminal visible at boot');
  clickTab('nodes');
  assert.equal(panel('nodes').hidden, false, 'NODES panel opens');
  assert.equal(panel('terminal').hidden, true, 'terminal hides');
  const nodesRoot = d.getElementById('cyTabNodes');
  assert.equal(nodesRoot.querySelectorAll('.cy-nodeRow').length, 2, 'NODES tab lists every route node');
  assert.ok(nodesRoot.querySelector('.cy-nodeRow.active'), 'active node is highlighted');

  clickTab('gpn');
  const gpnRoot = d.getElementById('cyTabGpn');
  assert.equal(gpnRoot.querySelectorAll('.cy-gpnRow').length, 1, 'GPN tab lists managed servers');
  assert.ok(gpnRoot.querySelector('.cy-gpnProbe.ok'), 'probe badge renders the live measurement');

  clickTab('monitor');
  const monRoot = d.getElementById('cyTabMonitor');
  assert.equal(monRoot.querySelectorAll('.cy-monRow').length, 2, 'MONITOR tab renders connections (listener filtered, dashboard contract)');

  clickTab('about');
  const aboutRoot = d.getElementById('cyTabAbout');
  assert.ok(aboutRoot.textContent.includes('7.26.54'), 'ABOUT tab shows the program version');
  skin.dom.window.close();
});

test('cyber: console NODES tab selects a node through the same bridge contract as the destination list', () => {
  const skin = bootCyber({ nodes: CY_NODES });
  const d = skin.dom.window.document;
  d.querySelector('[data-cy-tab="nodes"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const rows = d.getElementById('cyTabNodes').querySelectorAll('.cy-nodeRow');
  rows[1].dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.equal(skin.bridge.selectedNode.key, 'frankfurt', 'clicking a row selects that node (demo mode applyNode)');
  // The list re-renders on selection — re-query the fresh DOM.
  const fresh = d.getElementById('cyTabNodes').querySelectorAll('.cy-nodeRow');
  assert.ok(fresh[1].classList.contains('active'), 'selected row becomes active');
  assert.ok(!fresh[0].classList.contains('active'), 'previously active row deactivates');
  skin.dom.window.close();
});

test('cyber: console MONITOR tab filters, hides listeners and assigns routes like the dashboard', () => {
  const skin = bootCyber({ connected: true, monitorSnapshot: { apps: [], connections: CY_CONNS } });
  const d = skin.dom.window.document;
  d.querySelector('[data-cy-tab="monitor"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('cyTabMonitor');
  // Filter narrows the visible rows.
  const filter = root.querySelector('[data-cy-monfilter]');
  filter.value = 'discord';
  filter.dispatchEvent(new skin.dom.window.Event('input', { bubbles: true }));
  assert.equal(root.querySelectorAll('.cy-monRow').length, 1, 'filter keeps only the match');
  assert.ok(root.querySelector('.cy-monRow').textContent.includes('Discord'), 'matching row is Discord');
  // Un-checking hide-listeners brings the TCP/Listen row back.
  filter.value = '';
  filter.dispatchEvent(new skin.dom.window.Event('input', { bubbles: true }));
  const hide = root.querySelector('[data-cy-monhide]');
  hide.checked = false;
  hide.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.equal(root.querySelectorAll('.cy-monRow').length, 3, 'hide-listeners off shows the listener row');
  // Route select posts the same set_app_route contract as the dashboard.
  const sel = root.querySelector('[data-cy-monroute]');
  sel.value = 'block';
  sel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(lastPost(skin, 'set_app_route'),
    { action: 'set_app_route', processName: 'chrome.exe', displayName: 'Google Chrome', route: 'block' },
    'monitor route select posts set_app_route');
  skin.dom.window.close();
});

test('cyber: console GPN tab measures, connects and toggles servers through the host', () => {
  const skin = bootCyber({
    gpnServers: [{ serverId: 'it-01', serverName: 'Italy · IT-01', isEnabled: true }],
    gpnServerProbes: [],
    activeGpnServer: 'it-01'
  });
  const d = skin.dom.window.document;
  d.querySelector('[data-cy-tab="gpn"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('cyTabGpn');
  assert.ok(root.querySelector('.cy-gpnRow.active'), 'active GPN server is highlighted');
  root.querySelector('[data-cy-gpn-measure]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'gpn_servers_probe'), { action: 'gpn_servers_probe' }, 'MEASURE posts gpn_servers_probe');
  root.querySelector('[data-cy-gpn-connect]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'gpn_connect'), { action: 'gpn_connect' }, 'JACK IN posts gpn_connect');
  root.querySelector('[data-cy-gpn-toggle]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'gpn_server_toggle'), { action: 'gpn_server_toggle', serverId: 'it-01', enabled: false }, 'row toggle posts gpn_server_toggle');
  skin.dom.window.close();
});

test('cyber: console GPN tab renders failover telemetry counters and resets through the host', () => {
  const skin = bootCyber({
    gpnTelemetry: { serverSwitches: 3, udpDeaths: 1, modeFallbacks: 2, recoveries: 4, modeDecisions: 7, totalEvents: 17 },
    gpnServers: [], gpnServerProbes: []
  });
  const d = skin.dom.window.document;
  d.querySelector('[data-cy-tab="gpn"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('cyTabGpn');
  const items = root.querySelectorAll('.cy-gpnTelItem');
  assert.equal(items.length, 5, 'five failover counters render (switch/death/fallback/recover/select)');
  assert.deepEqual(Array.from(items).map(i => i.querySelector('b').textContent), ['3', '1', '2', '4', '7'], 'counters mirror the host snapshot');
  assert.ok(root.querySelector('.cy-gpnTel .cy-tabHead em').textContent.includes('17'), 'total events badge renders');
  root.querySelector('[data-cy-telreset]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'reset_gpn_telemetry'), { action: 'reset_gpn_telemetry' }, 'RESET posts the shared reset_gpn_telemetry contract');
  skin.dom.window.close();
});

test('cyber: console GPN tab renders the resilience log with refresh/clear host actions', () => {
  const skin = bootCyber({
    gpnResilienceLog: {
      path: 'C:\\ProgramData\\AoGPN\\gpn-resilience.log',
      entries: [
        { action: 'ServerSwitch', timestampMs: 1000, toMode: 'V2rayTCP', serverName: 'IT-01', targetServerName: 'DE-11', reason: 'latency' },
        { action: 'UdpDeath', timestampMs: 2000, toMode: 'WireGuard', serverName: 'DE-11' },
        { action: 'Recover', timestampMs: 3000, toMode: 'WireGuard', serverName: 'IT-01' }
      ]
    },
    gpnServers: [], gpnServerProbes: []
  });
  const d = skin.dom.window.document;
  d.querySelector('[data-cy-tab="gpn"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('cyTabGpn');
  const evs = root.querySelectorAll('.cy-ev');
  assert.equal(evs.length, 3, 'every decision renders as a color-coded event row');
  assert.ok(evs[0].classList.contains('ok'), 'server switch is tinted ok');
  assert.ok(evs[1].classList.contains('bad'), 'udp death is tinted bad');
  assert.ok(evs[0].querySelector('.cy-evBadge').textContent.includes('SERVER SWITCH'), 'action badge shows the localized action label');
  assert.ok(evs[0].textContent.includes('IT-01') && evs[0].textContent.includes('DE-11'), 'event details show server → target');
  assert.ok(root.textContent.includes('gpn-resilience.log'), 'mirror path renders');
  root.querySelector('[data-cy-resrefresh]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'get_gpn_resilience_log'), { action: 'get_gpn_resilience_log' }, 'REFRESH posts get_gpn_resilience_log');
  root.querySelector('[data-cy-resclear]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'clear_gpn_resilience_log'), { action: 'clear_gpn_resilience_log' }, 'CLEAR posts clear_gpn_resilience_log');
  skin.dom.window.close();
});

// ---- global keyboard shortcuts (shared across all skins) ----
const CY_APPS = [
  { processName: 'chrome.exe', displayName: 'Google Chrome', action: 'vpn' }
];

function cyKey(skin, key, opts) {
  const target = opts && opts.target ? opts.target : skin.dom.window.document.body;
  target.dispatchEvent(new skin.dom.window.KeyboardEvent('keydown', Object.assign({ key, bubbles: true, cancelable: true }, opts || {})));
}

test('cyber: Ctrl+Enter toggles the connection through the shared toggle_connection contract', () => {
  // Connected: the toggle posts immediately (the disconnect branch).
  const skin = bootCyber({ connected: true });
  const d = skin.dom.window.document;
  cyKey(skin, 'Enter', { ctrlKey: true });
  assert.deepEqual(lastPost(skin, 'toggle_connection'),
    { action: 'toggle_connection', mode: 'gpn', transport: 'proxy', protocol: 'auto', connected: true },
    'Ctrl+Enter posts the same toggle_connection contract as the JACK IN button');
  skin.dom.window.close();
  // Disconnected: the JACK IN breaching sequence starts (the 2 s host post runs
  // on the real clock; the sandbox freezes timers, so assert the UI transition).
  const skin2 = bootCyber();
  const d2 = skin2.dom.window.document;
  cyKey(skin2, 'Enter', { ctrlKey: true });
  assert.ok(d2.getElementById('cyFrame').classList.contains('cn'), 'Ctrl+Enter starts the breaching sequence');
  // jsdom exposes innerText as the property the skin writes (no layout pass).
  assert.equal(d2.getElementById('cyMain').innerText, 'BREACHING', 'power button enters BREACHING state');
  skin2.dom.window.close();
});

test('cyber: Alt+digit switches console tabs', () => {
  const skin = bootCyber();
  const d = skin.dom.window.document;
  const panel = (name) => d.querySelector('[data-cy-panel="' + name + '"]');
  cyKey(skin, '2', { altKey: true });
  assert.equal(panel('nodes').hidden, false, 'Alt+2 opens the NODES tab');
  assert.equal(panel('terminal').hidden, true, 'terminal hides');
  cyKey(skin, '4', { altKey: true });
  assert.equal(panel('monitor').hidden, false, 'Alt+4 opens the MONITOR tab');
  cyKey(skin, '1', { altKey: true });
  assert.equal(panel('terminal').hidden, false, 'Alt+1 returns to TERMINAL');
  skin.dom.window.close();
});

test('cyber: R on a focused boost app cycles its route through the host', () => {
  const skin = bootCyber({ monitorSnapshot: { apps: CY_APPS } });
  const d = skin.dom.window.document;
  const sw = d.querySelector('[data-cy-src] .cy-switch');
  assert.ok(sw, 'boost app switch renders');
  sw.focus();
  cyKey(skin, 'r', { target: sw });
  assert.deepEqual(lastPost(skin, 'set_app_route'),
    { action: 'set_app_route', processName: 'chrome.exe', displayName: 'Google Chrome', route: 'direct' },
    'R cycles vpn -> direct');
  // The list re-renders after each change — re-query the fresh switch.
  const sw2 = d.querySelector('[data-cy-src] .cy-switch');
  sw2.focus();
  cyKey(skin, 'r', { target: sw2 });
  assert.deepEqual(lastPost(skin, 'set_app_route'),
    { action: 'set_app_route', processName: 'chrome.exe', displayName: 'Google Chrome', route: 'block' },
    'R cycles direct -> block');
  // R is ignored while typing in an input (open MONITOR, then focus its filter).
  d.querySelector('[data-cy-tab="monitor"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const filter = d.querySelector('[data-cy-monfilter]');
  assert.ok(filter, 'monitor filter input renders after opening the tab');
  filter.focus();
  const before = skin.bridge._posts.length;
  cyKey(skin, 'r', { target: filter });
  assert.equal(skin.bridge._posts.length, before, 'typing in an input never triggers the route shortcut');
  skin.dom.window.close();
});

test('cyber: MONITOR tab groups connections by country / protocol with collapsible headers', () => {
  const skin = bootCyber({ connected: true, monitorSnapshot: { apps: [], connections: CY_CONNS } });
  const d = skin.dom.window.document;
  d.querySelector('[data-cy-tab="monitor"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('cyTabMonitor');
  const gsel = root.querySelector('[data-cy-mongroup-sel]');
  assert.ok(gsel, 'GROUP BY select renders');
  assert.deepEqual(Array.from(gsel.options).map(o => o.value),
    ['none', 'route', 'protocol', 'state', 'country', 'app'],
    'group options match the dashboard (none/route/protocol/state/country/app)');
  // Group by country: chrome (US · AS15169) and discord (US) form two groups.
  gsel.value = 'country';
  gsel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  const heads = root.querySelectorAll('.cy-mgroup');
  assert.equal(heads.length, 2, 'country groups render (listener row stays filtered out)');
  assert.ok(heads[0].textContent.includes('United States'), 'group header names the country');
  assert.equal(root.querySelectorAll('.cy-monRow').length, 2, 'all filtered connections still render as rows');
  // Collapse the first group: its rows hide via the class, header flips.
  heads[0].dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const fresh = root.querySelectorAll('.cy-mgroup');
  assert.equal(fresh[0].getAttribute('aria-expanded'), 'false', 'collapsed header reports aria-expanded=false');
  assert.equal(root.querySelectorAll('.cy-monRow.hidden').length, 1, 'collapsed group rows carry the hidden class');
  // Group by protocol: TCP + UDP headers.
  const gsel2 = root.querySelector('[data-cy-mongroup-sel]');
  gsel2.value = 'protocol';
  gsel2.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.equal(root.querySelectorAll('.cy-mgroup').length, 2, 'protocol groups render');
  // Grouping respects the live filter (discord only → one country group).
  const filter = root.querySelector('[data-cy-monfilter]');
  filter.value = 'discord';
  filter.dispatchEvent(new skin.dom.window.Event('input', { bubbles: true }));
  assert.equal(root.querySelectorAll('.cy-mgroup').length, 1, 'groups rebuild from the filtered rows');
  skin.dom.window.close();
});

test('cyber: trace pulse flow follows real telemetry (dest=download, src=upload, glow=total)', () => {
  // Heavy download, light upload: the destination fan must strobe fast while
  // the source fan crawls — the CSS custom properties on #cyFrame carry the
  // durations so the regenerated SVG paths inherit them.
  const skin = bootCyber({ connected: true, telemetry: [22, 3, 40, 0.5] });
  const d = skin.dom.window.document;
  const frame = d.getElementById('cyFrame');
  const destDur = () => parseFloat(frame.style.getPropertyValue('--cy-dest-dur'));
  const srcDur = () => parseFloat(frame.style.getPropertyValue('--cy-src-dur'));
  const glow = () => parseFloat(frame.style.getPropertyValue('--cy-glow'));
  assert.ok(destDur() < 0.5, '40 Mbps download saturates the destination pulse (~0.3s)');
  assert.ok(srcDur() > 1.5, '0.5 Mbps upload keeps the source fan slow');
  const firstGlow = glow();
  assert.ok(firstGlow > 4, 'glow scales up with total flow');
  assert.ok(d.body.classList.contains('is-breached'), 'pulses only play while connected');
  // Traffic drops: both fans slow down and the glow fades toward idle.
  skin.bridge.telemetry = [22, 3, 0.4, 0.2];
  skin.push();
  const slowedDest = destDur(), slowedGlow = glow();
  assert.ok(slowedDest > 1.5, 'pulse slows when download falls');
  assert.ok(slowedGlow < firstGlow, 'glow fades as traffic drops');
  // Disconnect: flow resets so the next session starts from a calm baseline.
  skin.bridge.connected = false;
  skin.push();
  assert.ok(destDur() >= 2.3, 'disconnected session drops back to the idle crawl duration');
  skin.dom.window.close();
});
