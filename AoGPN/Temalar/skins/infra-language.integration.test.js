// ============================================================================
// INFRA skin — language mechanism (LANGUAGE selector + skin.infra.* keys)
// ----------------------------------------------------------------------------
// Same contract as CYBER/NEXUS: the header LANGUAGE select uses the bridge's
// LANGUAGES array, changes go through B.setLanguage (whole-app switch,
// persisted via set_language), and every static/dynamic text resolves through
// skin.infra.* scoped keys with shared + English fallbacks. Also guards the
// real-app regression: skin.html must actually include skin.js (the iframe
// would otherwise render a static page and none of this would run).
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { loadSkin } = require('./skin-sandbox.js');

function bootInfra(bridgeData) {
  return loadSkin('infra', bridgeData || {});
}

test('infra: skin.html actually includes skin.js (real-app regression guard)', () => {
  const html = fs.readFileSync(path.join(__dirname, 'infra', 'skin.html'), 'utf8');
  assert.match(html, /<script[^>]*src=["']skin\.js["']/,
    'the skin iframe must load skin.js — otherwise the skin is static in the real app');
});

test('infra: LANGUAGE select renders the bridge language list and current value', () => {
  const skin = bootInfra({
    LANGUAGES: [
      { code: 'en', name: 'English' },
      { code: 'tr', name: 'Türkçe' },
      { code: 'ru', name: 'Русский' }
    ],
    language: 'tr'
  });
  const d = skin.dom.window.document;
  const sel = d.getElementById('ifLang');
  assert.ok(sel, 'LANGUAGE select exists in the header');
  assert.equal(sel.options.length, 3, 'options come from the bridge LANGUAGES array');
  assert.equal(sel.options[0].value, 'en');
  assert.equal(sel.options[1].value, 'tr');
  assert.equal(sel.value, 'tr', 'current app language is selected');
  skin.dom.window.close();
});

test('infra: LANGUAGE change calls bridge setLanguage (whole-app switch) and mirrors the selection', () => {
  const calls = [];
  const skin = bootInfra({
    LANGUAGES: [{ code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }],
    setLanguage(lang) { calls.push(lang); }
  });
  const d = skin.dom.window.document;
  const sel = d.getElementById('ifLang');
  assert.equal(sel.value, 'en', 'boots with the bridge language');

  sel.value = 'tr';
  sel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(calls, ['tr'], 'switches the WHOLE app through the same channel as the top-bar picker');
  assert.equal(skin.bridge.language, 'tr', 'skin state mirrored');
  assert.equal(sel.value, 'tr', 'select reflects the new language after render');
  skin.dom.window.close();
});

test('infra: static texts resolve through skin.infra.* scoped translations', () => {
  // The stub t applies the real bridge's scoping (skin.infra.<key>) so the
  // skin actually pulls its labels from the language layer.
  const dict = {
    'skin.infra.panel.apps': 'BOOST UYGULAMALARI',
    'skin.infra.tele.loss': 'KAYIP',
    'skin.infra.metric.session': 'OTURUM',
    'skin.infra.status.offline': 'ÇEVRİMDIŞI'
  };
  const skin = bootInfra({ t: (key) => dict['skin.infra.' + key] || key });
  const d = skin.dom.window.document;
  assert.equal(d.querySelector('.if-panel-title').textContent, 'BOOST UYGULAMALARI', 'panel title from scoped key');
  assert.equal(d.querySelector('[data-if-i18n="tele.loss"]').textContent, 'KAYIP', 'telemetry label from scoped key');
  assert.equal(d.querySelector('[data-if-i18n="metric.session"]').textContent, 'OTURUM', 'metric label from scoped key');
  assert.equal(d.getElementById('ifStatus').textContent, 'ÇEVRİMDIŞI', 'dynamic status from scoped key');
  skin.dom.window.close();
});

test('infra: app route labels resolve through skin.infra.route.* before host routeLabels', () => {
  const dict = { 'skin.infra.route.direct': 'Doğrudan' };
  const skin = bootInfra({
    t: (key) => dict['skin.infra.' + key] || key,
    monitorSnapshot: {
      apps: [{ processName: 'cs2.exe', displayName: 'CS2', action: 'direct' }]
    },
    routeLabels: { direct: 'Direct (host)' }
  });
  const d = skin.dom.window.document;
  const row = d.querySelector('[data-app="cs2.exe"]');
  assert.ok(row, 'app row rendered from the real monitorSnapshot');
  assert.equal(row.querySelector('b').textContent, 'Doğrudan', 'scoped route translation wins over host routeLabels');
  skin.dom.window.close();
});

test('infra: console operation logs resolve through skin.infra.log.* translations', () => {
  const dict = {
    'skin.infra.log.toggle': 'GEÇİŞ GÖNDERİLDİ: {state}',
    'skin.infra.log.language': 'DİL: {lang}',
    'skin.infra.connect': 'BAĞLAN',
    'skin.infra.disconnect': 'BAĞLANTIYI KES'
  };
  const skin = bootInfra({
    LANGUAGES: [{ code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }],
    t: (key) => dict['skin.infra.' + key] || key
  });
  const d = skin.dom.window.document;
  const lastLine = () => { const c = d.getElementById('ifConsole'); return c.lastElementChild.textContent; };
  const click = (el) => el.dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  // CONNECT toggle -> translated "TOGGLE SENT: {state}" with the translated action.
  click(d.getElementById('ifConnect'));
  assert.equal(lastLine(), '> GEÇİŞ GÖNDERİLDİ: BAĞLAN', 'connect log with scoped translations');

  // Language switch -> translated "LANG: {lang}".
  const sel = d.getElementById('ifLang');
  sel.value = 'tr';
  sel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.equal(lastLine(), '> DİL: TR', 'language log with scoped translations');
  skin.dom.window.close();
});

// The bridge stub captures messages created inside the jsdom realm, whose
// Object prototype differs from Node's — normalize via JSON round-trip.
function lastPost(skin, action) {
  return JSON.parse(JSON.stringify(skin.bridge._posts.filter(p => p.action === action).at(-1)));
}

// ---- extended settings: dashboard parity controls ----
test('infra: SETTINGS TUN STACK + EFFECTS selects post set_tun_stack / set_effects_tier and mirror bridge state', () => {
  const skin = bootInfra({ tunStack: 'system', effectsTier: 'reduced' });
  const d = skin.dom.window.document;
  const ts = d.getElementById('ifTunStack');
  const ef = d.getElementById('ifEffects');
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

test('infra: SETTINGS auto-game / recovery / failover switches post the shared host actions', () => {
  const skin = bootInfra({
    monitorSnapshot: { apps: [], autoConnectOnGameStart: true },
    gpnRecoveryWatch: true,
    gpnFailover: false
  });
  const d = skin.dom.window.document;
  const click = (sel) => d.querySelector(sel).dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const auto = d.getElementById('ifAutoGame');
  const rec = d.getElementById('ifRecovery');
  const fo = d.getElementById('ifFailover');
  assert.ok(auto && rec && fo, 'auto-game / recovery / failover switches render');
  assert.equal(auto.getAttribute('aria-checked'), 'true', 'autoConnectOnGameStart defaults to on');
  assert.equal(rec.getAttribute('aria-checked'), 'true', 'recovery watch mirrors bridge state');
  assert.equal(fo.getAttribute('aria-checked'), 'false', 'failover mirrors bridge state');
  click('#ifAutoGame');
  assert.deepEqual(lastPost(skin, 'set_auto_game_connect'), { action: 'set_auto_game_connect', enabled: false }, 'auto-game toggle posts set_auto_game_connect');
  click('#ifRecovery');
  assert.deepEqual(lastPost(skin, 'set_gpn_recovery_watch'), { action: 'set_gpn_recovery_watch', enabled: false }, 'recovery toggle posts set_gpn_recovery_watch');
  click('#ifFailover');
  assert.deepEqual(lastPost(skin, 'set_gpn_failover'), { action: 'set_gpn_failover', enabled: true }, 'failover toggle posts set_gpn_failover');
  assert.equal(fo.getAttribute('aria-checked'), 'true', 'failover flips optimistically');
  skin.dom.window.close();
});

test('infra: SETTINGS proxy test button posts test_proxy and mirrors the host result', () => {
  const skin = bootInfra();
  const d = skin.dom.window.document;
  const btn = d.getElementById('ifProxyTest');
  assert.ok(btn, 'PROXY TEST button renders');
  assert.equal(btn.textContent, 'TEST', 'idle label');
  btn.dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'test_proxy'), { action: 'test_proxy' }, 'click posts the shared test_proxy contract');
  assert.ok(btn.classList.contains('running'), 'button enters the testing state');
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
const IF_NODES = [
  { key: 'istanbul', code: 'TR', name: 'Istanbul · TR-01', addr: '145.239.14.77', ping: 18 },
  { key: 'frankfurt', code: 'DE', name: 'Frankfurt · DE-11', addr: '5.161.91.19', ping: 148 }
];
const IF_CONNS = [
  { processName: 'chrome.exe', displayName: 'Google Chrome', pid: 42, protocol: 'TCP', state: 'Established', remoteAddress: '142.250.74.14:443', countryText: 'United States', asnText: 'AS15169', routeTag: 'vpn', routeText: 'VPN', action: 'vpn' },
  { processName: 'sshd.exe', displayName: 'OpenSSH', pid: 77, protocol: 'TCP', state: 'Listen', remoteAddress: '0.0.0.0:22', routeTag: '', action: '' },
  { processName: 'discord.exe', displayName: 'Discord', pid: 88, protocol: 'UDP', state: 'Established', remoteAddress: '162.159.128.233:443', countryText: 'United States', routeTag: 'direct', routeText: 'Direct', action: 'direct' }
];

test('infra: console tabs switch panels and render NODES / GPN / MONITOR / ABOUT content', () => {
  const skin = bootInfra({
    nodes: IF_NODES,
    gpnServers: [{ serverId: 'it-01', serverName: 'Italy · IT-01', isEnabled: true }],
    gpnServerProbes: [{ serverId: 'it-01', isSuccess: true, delayMs: 9, lossPercent: 0, udpStatus: 'open' }],
    monitorSnapshot: { apps: [{ processName: 'chrome.exe' }], connections: IF_CONNS },
    appInfo: { version: '7.26.54', appName: 'AO GPN' }
  });
  const d = skin.dom.window.document;
  const clickTab = (name) => d.querySelector('[data-if-tab="' + name + '"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const panel = (name) => d.querySelector('[data-if-panel="' + name + '"]');

  assert.equal(d.querySelectorAll('[data-if-tab]').length, 5, 'five console tabs render');
  assert.equal(panel('terminal').hidden, false, 'terminal visible at boot');
  clickTab('nodes');
  assert.equal(panel('nodes').hidden, false, 'NODES panel opens');
  assert.equal(panel('terminal').hidden, true, 'terminal hides');
  const nodesRoot = d.getElementById('ifTabNodes');
  assert.equal(nodesRoot.querySelectorAll('.if-node-row').length, 2, 'NODES tab lists every route node');
  assert.ok(nodesRoot.querySelector('.if-node-row.active'), 'active node is highlighted');

  clickTab('gpn');
  const gpnRoot = d.getElementById('ifTabGpn');
  assert.equal(gpnRoot.querySelectorAll('.if-gpnRow').length, 1, 'GPN tab lists managed servers');
  assert.ok(gpnRoot.querySelector('.if-gpnProbe.ok'), 'probe badge renders the live measurement');

  clickTab('monitor');
  const monRoot = d.getElementById('ifTabMonitor');
  assert.equal(monRoot.querySelectorAll('.if-monRow').length, 2, 'MONITOR tab renders connections (listener filtered, dashboard contract)');

  clickTab('about');
  const aboutRoot = d.getElementById('ifTabAbout');
  assert.ok(aboutRoot.textContent.includes('7.26.54'), 'ABOUT tab shows the program version');
  skin.dom.window.close();
});

test('infra: console NODES tab selects a node through the same bridge contract as the left panel', () => {
  const skin = bootInfra({ nodes: IF_NODES });
  const d = skin.dom.window.document;
  d.querySelector('[data-if-tab="nodes"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const rows = d.getElementById('ifTabNodes').querySelectorAll('.if-node-row');
  rows[1].dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.equal(skin.bridge.selectedNode.key, 'frankfurt', 'clicking a row selects that node (demo mode applyNode)');
  const fresh = d.getElementById('ifTabNodes').querySelectorAll('.if-node-row');
  assert.ok(fresh[1].classList.contains('active'), 'selected row becomes active');
  assert.ok(!fresh[0].classList.contains('active'), 'previously active row deactivates');
  skin.dom.window.close();
});

test('infra: console MONITOR tab filters, hides listeners and assigns routes like the dashboard', () => {
  const skin = bootInfra({ connected: true, monitorSnapshot: { apps: [], connections: IF_CONNS } });
  const d = skin.dom.window.document;
  d.querySelector('[data-if-tab="monitor"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('ifTabMonitor');
  const filter = root.querySelector('[data-if-monfilter]');
  filter.value = 'discord';
  filter.dispatchEvent(new skin.dom.window.Event('input', { bubbles: true }));
  assert.equal(root.querySelectorAll('.if-monRow').length, 1, 'filter keeps only the match');
  assert.ok(root.querySelector('.if-monRow').textContent.includes('Discord'), 'matching row is Discord');
  filter.value = '';
  filter.dispatchEvent(new skin.dom.window.Event('input', { bubbles: true }));
  const hide = root.querySelector('[data-if-monhide]');
  hide.checked = false;
  hide.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.equal(root.querySelectorAll('.if-monRow').length, 3, 'hide-listeners off shows the listener row');
  const sel = root.querySelector('[data-if-monroute]');
  sel.value = 'block';
  sel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(lastPost(skin, 'set_app_route'),
    { action: 'set_app_route', processName: 'chrome.exe', displayName: 'Google Chrome', route: 'block' },
    'monitor route select posts set_app_route');
  skin.dom.window.close();
});

test('infra: console GPN tab measures, connects and toggles servers through the host', () => {
  const skin = bootInfra({
    gpnServers: [{ serverId: 'it-01', serverName: 'Italy · IT-01', isEnabled: true }],
    gpnServerProbes: [],
    activeGpnServer: 'it-01'
  });
  const d = skin.dom.window.document;
  d.querySelector('[data-if-tab="gpn"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('ifTabGpn');
  assert.ok(root.querySelector('.if-gpnRow.active'), 'active GPN server is highlighted');
  root.querySelector('[data-if-gpn-measure]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'gpn_servers_probe'), { action: 'gpn_servers_probe' }, 'MEASURE posts gpn_servers_probe');
  root.querySelector('[data-if-gpn-connect]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'gpn_connect'), { action: 'gpn_connect' }, 'JACK IN posts gpn_connect');
  root.querySelector('[data-if-gpn-toggle]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'gpn_server_toggle'), { action: 'gpn_server_toggle', serverId: 'it-01', enabled: false }, 'row toggle posts gpn_server_toggle');
  skin.dom.window.close();
});

test('infra: console GPN tab renders failover telemetry counters and resets through the host', () => {
  const skin = bootInfra({
    gpnTelemetry: { serverSwitches: 3, udpDeaths: 1, modeFallbacks: 2, recoveries: 4, modeDecisions: 7, totalEvents: 17 },
    gpnServers: [], gpnServerProbes: []
  });
  const d = skin.dom.window.document;
  d.querySelector('[data-if-tab="gpn"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('ifTabGpn');
  const items = root.querySelectorAll('.if-gpnTelItem');
  assert.equal(items.length, 5, 'five failover counters render (switch/death/fallback/recover/select)');
  assert.deepEqual(Array.from(items).map(i => i.querySelector('b').textContent), ['3', '1', '2', '4', '7'], 'counters mirror the host snapshot');
  assert.ok(root.querySelector('.if-gpnTel .if-tabHead em').textContent.includes('17'), 'total events badge renders');
  root.querySelector('[data-if-telreset]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'reset_gpn_telemetry'), { action: 'reset_gpn_telemetry' }, 'RESET posts the shared reset_gpn_telemetry contract');
  skin.dom.window.close();
});

test('infra: console GPN tab renders the resilience log with refresh/clear host actions', () => {
  const skin = bootInfra({
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
  d.querySelector('[data-if-tab="gpn"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('ifTabGpn');
  const evs = root.querySelectorAll('.if-ev');
  assert.equal(evs.length, 3, 'every decision renders as a color-coded event row');
  assert.ok(evs[0].classList.contains('ok'), 'server switch is tinted ok');
  assert.ok(evs[1].classList.contains('bad'), 'udp death is tinted bad');
  assert.ok(evs[0].querySelector('.if-evBadge').textContent.includes('SERVER SWITCH'), 'action badge shows the localized action label');
  assert.ok(evs[0].textContent.includes('IT-01') && evs[0].textContent.includes('DE-11'), 'event details show server → target');
  assert.ok(root.textContent.includes('gpn-resilience.log'), 'mirror path renders');
  root.querySelector('[data-if-resrefresh]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'get_gpn_resilience_log'), { action: 'get_gpn_resilience_log' }, 'REFRESH posts get_gpn_resilience_log');
  root.querySelector('[data-if-resclear]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(lastPost(skin, 'clear_gpn_resilience_log'), { action: 'clear_gpn_resilience_log' }, 'CLEAR posts clear_gpn_resilience_log');
  skin.dom.window.close();
});

// ---- global keyboard shortcuts (shared across all skins) ----
function ifKey(skin, key, opts) {
  const target = opts && opts.target ? opts.target : skin.dom.window.document.body;
  target.dispatchEvent(new skin.dom.window.KeyboardEvent('keydown', Object.assign({ key, bubbles: true, cancelable: true }, opts || {})));
}

test('infra: Ctrl+Enter toggles the connection through the shared toggle_connection contract', () => {
  const skin = bootInfra();
  ifKey(skin, 'Enter', { ctrlKey: true });
  assert.deepEqual(lastPost(skin, 'toggle_connection'),
    { action: 'toggle_connection', mode: 'gpn', transport: 'proxy', protocol: 'auto', connected: false },
    'Ctrl+Enter posts the same toggle_connection contract as the CONNECT button');
  skin.dom.window.close();
});

test('infra: Alt+digit switches console tabs', () => {
  const skin = bootInfra();
  const d = skin.dom.window.document;
  const panel = (name) => d.querySelector('[data-if-panel="' + name + '"]');
  ifKey(skin, '2', { altKey: true });
  assert.equal(panel('nodes').hidden, false, 'Alt+2 opens the NODES tab');
  assert.equal(panel('terminal').hidden, true, 'terminal hides');
  ifKey(skin, '4', { altKey: true });
  assert.equal(panel('monitor').hidden, false, 'Alt+4 opens the MONITOR tab');
  ifKey(skin, '1', { altKey: true });
  assert.equal(panel('terminal').hidden, false, 'Alt+1 returns to TERMINAL');
  skin.dom.window.close();
});

test('infra: R on a focused boost app cycles its route through the host', () => {
  const skin = bootInfra({ monitorSnapshot: { apps: [{ processName: 'chrome.exe', displayName: 'Google Chrome', action: 'vpn' }] } });
  const d = skin.dom.window.document;
  const sw = d.querySelector('[data-if-app-toggle]');
  assert.ok(sw, 'boost app switch renders');
  sw.focus();
  ifKey(skin, 'r', { target: sw });
  assert.deepEqual(lastPost(skin, 'set_app_route'),
    { action: 'set_app_route', processName: 'chrome.exe', displayName: 'Google Chrome', route: 'direct' },
    'R cycles vpn -> direct');
  // The list re-renders after each change — re-query the fresh switch.
  const sw2 = d.querySelector('[data-if-app-toggle]');
  assert.ok(sw2, 'switch re-renders after the route change');
  sw2.focus();
  ifKey(skin, 'r', { target: sw2 });
  assert.deepEqual(lastPost(skin, 'set_app_route'),
    { action: 'set_app_route', processName: 'chrome.exe', displayName: 'Google Chrome', route: 'block' },
    'R cycles direct -> block');
  // R is ignored while typing in an input (open MONITOR, then focus its filter).
  d.querySelector('[data-if-tab="monitor"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const filter = d.querySelector('[data-if-monfilter]');
  assert.ok(filter, 'monitor filter input renders after opening the tab');
  filter.focus();
  const before = skin.bridge._posts.length;
  ifKey(skin, 'r', { target: filter });
  assert.equal(skin.bridge._posts.length, before, 'typing in an input never triggers the route shortcut');
  skin.dom.window.close();
});

test('infra: MONITOR tab groups connections by country / protocol with collapsible headers', () => {
  const skin = bootInfra({ connected: true, monitorSnapshot: { apps: [], connections: IF_CONNS } });
  const d = skin.dom.window.document;
  d.querySelector('[data-if-tab="monitor"]').dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const root = d.getElementById('ifTabMonitor');
  const gsel = root.querySelector('[data-if-mongroup-sel]');
  assert.ok(gsel, 'GROUP BY select renders');
  assert.deepEqual(Array.from(gsel.options).map(o => o.value),
    ['none', 'route', 'protocol', 'state', 'country', 'app'],
    'group options match the dashboard (none/route/protocol/state/country/app)');
  // Group by country: chrome (US · AS15169) and discord (US) form two groups.
  gsel.value = 'country';
  gsel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  const heads = root.querySelectorAll('.if-mgroup');
  assert.equal(heads.length, 2, 'country groups render (listener row stays filtered out)');
  assert.ok(heads[0].textContent.includes('United States'), 'group header names the country');
  assert.equal(root.querySelectorAll('.if-monRow').length, 2, 'all filtered connections still render as rows');
  // Collapse the first group: its rows hide via the class, header flips.
  heads[0].dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const fresh = root.querySelectorAll('.if-mgroup');
  assert.equal(fresh[0].getAttribute('aria-expanded'), 'false', 'collapsed header reports aria-expanded=false');
  assert.equal(root.querySelectorAll('.if-monRow.hidden').length, 1, 'collapsed group rows carry the hidden class');
  // Group by protocol: TCP + UDP headers.
  const gsel2 = root.querySelector('[data-if-mongroup-sel]');
  gsel2.value = 'protocol';
  gsel2.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.equal(root.querySelectorAll('.if-mgroup').length, 2, 'protocol groups render');
  // Grouping respects the live filter (discord only → one country group).
  const filter = root.querySelector('[data-if-monfilter]');
  filter.value = 'discord';
  filter.dispatchEvent(new skin.dom.window.Event('input', { bubbles: true }));
  assert.equal(root.querySelectorAll('.if-mgroup').length, 1, 'groups rebuild from the filtered rows');
  skin.dom.window.close();
});
