// ============================================================================
// Integration test: NEXUS sub-views (analytics / settings) are class-based
// ----------------------------------------------------------------------------
// After the inline->CSS migration, the analytics and settings views must render
// with NO static inline styling — sizes live in classes (.nx-anaVal.sm/.lg,
// .nx-unit/.xs, .nx-empty) and sub-view visibility is the .nx-view-hidden
// class, not an inline style.display toggle. Dynamic data (meter/bar fills,
// widths/heights) legitimately stays inline.
//
// This file boots the REAL nexus skin (real skin.html + real skin.js) through
// the shared sandbox, injects the REAL skin.css so computed styles resolve, and
// drives the real sidebar buttons to switch views.
//
// Run with:
//   node --test Temalar/skins/nexus-views.integration.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { loadSkin } = require('./skin-sandbox.js');

const NEXUS_CSS = fs.readFileSync(path.join(__dirname, 'nexus', 'skin.css'), 'utf8');

// Boot nexus and inject the real stylesheet so getComputedStyle resolves the
// class sizes (the <link rel="stylesheet"> in skin.html is not fetched by
// jsdom).
function bootNexus(bridgeData) {
  const skin = loadSkin('nexus', bridgeData || {}, { now: 1000 });
  const style = skin.document.createElement('style');
  style.textContent = NEXUS_CSS;
  skin.document.head.appendChild(style);
  return skin;
}

function clickNav(skin, view) {
  const btn = skin.document.querySelector('.nx-nav button[data-nx-view="' + view + '"]');
  assert.ok(btn, 'nav button for ' + view + ' exists');
  btn.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
}

function css(skin, el) {
  return skin.window.getComputedStyle(el);
}

const CONNECTED = {
  connected: true,
  telemetry: [42, 0.5, 8.2, 3.1],
  sessionTime: '00:01:23',
  exitIp: '1.2.3.4',
  mode: 'gpn',
  monitorSnapshot: { apps: [], autoConnectOnGameStart: true, invertManualRouting: false },
  gpnRecoveryWatch: true,
  gpnFailover: false,
  tunStack: 'mixed',
  effectsTier: 'full',
  appInfo: { version: '7.26.54', appName: 'AO GPN' }
};

test('NEXUS: analytics view renders class-based sizes with no inline font styling', () => {
  const skin = bootNexus(CONNECTED);
  const d = skin.document;

  clickNav(skin, 'analytics');
  const root = d.getElementById('nxAnalytics');
  assert.ok(root && root.querySelectorAll('.nx-anaCard').length === 3, 'three analytics cards render');

  // Migration contract: no STATIC inline styling (font-size) anywhere in the
  // analytics DOM — only the dynamic meter fills keep inline width values.
  assert.equal(root.querySelectorAll('[style*="font-size"]').length, 0,
    'analytics view has zero inline font-size styles');

  // The dynamic meter fills stay inline (data, not styling).
  const meters = root.querySelectorAll('.nx-meter i');
  assert.ok(meters.length >= 2, 'meter fills render');
  meters.forEach((m) => {
    assert.match(m.getAttribute('style') || '', /^width:\d+%$/, 'meter fill width stays a dynamic inline value');
  });

  // Class sizes resolve through the real stylesheet.
  const valSm = root.querySelector('.nx-anaVal.sm');
  const valLg = root.querySelector('.nx-anaVal.lg');
  assert.ok(valSm && valLg, 'sm/lg value variants render');
  assert.equal(css(skin, valSm).fontSize, '14px', 'exit IP value is 14px via .sm');
  assert.equal(css(skin, valLg).fontSize, '16px', 'download/upload values are 16px via .lg');

  const unit = root.querySelector('.nx-unit');
  const unitXs = root.querySelector('.nx-unit.xs');
  assert.ok(unit && unitXs, 'unit variants render');
  assert.equal(css(skin, unit).fontSize, '11px', 'ms unit is 11px');
  assert.equal(css(skin, unitXs).fontSize, '10px', 'Mbps unit is 10px');

  // Telemetry reached the view (connected sample).
  assert.ok(root.textContent.includes('1.2.3.4'), 'exit IP shows');
  assert.ok(root.textContent.includes('00:01:23'), 'session time shows');
  skin.close();
});

test('NEXUS: sub-view visibility is the .nx-view-hidden class, not inline display', () => {
  const skin = bootNexus(CONNECTED);
  const d = skin.document;

  const dashboardGrid = d.querySelector('.nx-grid[data-nx-view="dashboard"]');
  const dashboardBottom = d.querySelector('.nx-bottom[data-nx-view="dashboard"]');
  const routeView = d.querySelector('.nx-view[data-nx-view="route"]');
  const analyticsView = d.querySelector('.nx-view[data-nx-view="analytics"]');
  const settingsView = d.querySelector('.nx-view[data-nx-view="settings"]');
  assert.ok(dashboardGrid && dashboardBottom && routeView && analyticsView && settingsView, 'all view sections exist');

  // Boot state: dashboard visible, everything else hidden — via the class.
  assert.ok(!dashboardGrid.classList.contains('nx-view-hidden'), 'dashboard grid visible at boot');
  assert.ok(routeView.classList.contains('nx-view-hidden'), 'route hidden at boot');
  assert.equal(dashboardGrid.getAttribute('style'), null, 'no inline display on the dashboard grid');
  assert.equal(routeView.getAttribute('style'), null, 'no inline display on sub-views');

  // Analytics: only the analytics section is visible; both dashboard sections
  // (grid AND flex bottom) hide through the same class.
  clickNav(skin, 'analytics');
  assert.ok(!analyticsView.classList.contains('nx-view-hidden'), 'analytics visible');
  assert.ok(dashboardGrid.classList.contains('nx-view-hidden'), 'dashboard grid hidden');
  assert.ok(dashboardBottom.classList.contains('nx-view-hidden'), 'dashboard bottom bar hidden');
  assert.ok(routeView.classList.contains('nx-view-hidden') && settingsView.classList.contains('nx-view-hidden'), 'other sub-views hidden');
  assert.equal(dashboardGrid.getAttribute('style'), null, 'hiding still never uses inline styles');
  assert.ok(d.querySelector('.nx-nav button[data-nx-view="analytics"]').classList.contains('active'), 'analytics nav is active');

  // Back to dashboard: grid + bottom reappear, analytics hides.
  clickNav(skin, 'dashboard');
  assert.ok(!dashboardGrid.classList.contains('nx-view-hidden'), 'dashboard grid back');
  assert.ok(!dashboardBottom.classList.contains('nx-view-hidden'), 'dashboard bottom back');
  assert.ok(analyticsView.classList.contains('nx-view-hidden'), 'analytics hidden again');
  assert.equal(dashboardGrid.getAttribute('style'), null);
  skin.close();
});

test('NEXUS: the active sub-view is actually visible (computed display, real CSS)', () => {
  const skin = bootNexus(CONNECTED);
  const d = skin.document;
  const css = el => skin.window.getComputedStyle(el);

  // Boot: dashboard sections render, sub-views are display:none.
  const grid = d.querySelector('.nx-grid[data-nx-view="dashboard"]');
  const settings = d.querySelector('.nx-view[data-nx-view="settings"]');
  const about = d.querySelector('.nx-view[data-nx-view="about"]');
  assert.notEqual(css(grid).display, 'none', 'dashboard visible at boot');
  assert.equal(css(settings).display, 'none', 'settings hidden at boot');

  // Switching to settings must make the section render (display != none) —
  // this guards the .nx-view:not(.nx-view-hidden) rule in skin.css.
  clickNav(skin, 'settings');
  assert.notEqual(css(settings).display, 'none', 'settings section visible after nav');
  assert.equal(css(grid).display, 'none', 'dashboard hidden on settings');
  assert.equal(css(about).display, 'none', 'about stays hidden');

  // About view also shows through the same rule.
  clickNav(skin, 'about');
  assert.notEqual(css(about).display, 'none', 'about visible after nav');
  assert.equal(css(settings).display, 'none', 'settings hidden on about');
  skin.close();
});

test('NEXUS: settings view renders class-based controls with no inline styles', () => {
  const skin = bootNexus(CONNECTED);
  const d = skin.document;

  clickNav(skin, 'settings');
  const root = d.getElementById('nxSettings');
  assert.ok(root, 'settings root exists');
  assert.equal(root.querySelectorAll('[style]').length, 0, 'settings view has zero inline styles');

  const selects = root.querySelectorAll('select.nx-settingSel');
  assert.equal(selects.length, 9, 'mode/capture/split/direction/sysproxy/protocol/tunstack/effects/language selects render');
  assert.ok(Array.from(selects).every((s) => s.value), 'every select carries a value');

  // New feature-parity controls: TUN stack + effects tier selects.
  const tunStackSel = root.querySelector('select.nx-settingSel[data-nx-set="tunstack"]');
  assert.ok(tunStackSel, 'TUN stack select renders');
  assert.deepEqual(Array.from(tunStackSel.options).map(o => o.value), ['gvisor', 'system', 'mixed'], 'TUN stack options');
  const effectsSel = root.querySelector('select.nx-settingSel[data-nx-set="effects"]');
  assert.ok(effectsSel, 'effects tier select renders');
  assert.deepEqual(Array.from(effectsSel.options).map(o => o.value), ['full', 'balanced', 'reduced'], 'effects options');

  // System-proxy test button with the idle label.
  const pt = root.querySelector('.nx-proxyTest[data-nx-set="proxytest"]');
  assert.ok(pt, 'proxy test button renders');
  assert.equal(pt.textContent, 'TEST', 'proxy test idle label');
  assert.equal(pt.getAttribute('style'), null, 'proxy test state is class-based');

  // New switches: auto game connect / recovery watch / failover.
  const autoGame = root.querySelector('.nx-switch[data-nx-set="autogame"]');
  assert.ok(autoGame, 'auto-game-connect switch renders');
  assert.equal(autoGame.getAttribute('aria-checked'), 'true', 'autoConnectOnGameStart defaults to on');
  const recovery = root.querySelector('.nx-switch[data-nx-set="recovery"]');
  const failover = root.querySelector('.nx-switch[data-nx-set="failover"]');
  assert.ok(recovery && failover, 'recovery watch + failover switches render');
  assert.ok(!failover.classList.contains('on'), 'failover defaults off');

  // Auto-reconnect is a class-based switch with the a11y contract.
  const sw = root.querySelector('.nx-switch[data-nx-set="autoreconnect"]');
  assert.ok(sw, 'auto-reconnect switch renders');
  assert.ok(sw.classList.contains('on'), 'default autoReconnect is on');
  assert.equal(sw.getAttribute('aria-checked'), 'true');
  assert.equal(sw.getAttribute('style'), null, 'switch state is class-based (on), not inline');
  assert.equal(css(skin, sw).display !== 'none', true, 'switch is visible');

  // Toggling flips the class (real listener), not a style.
  sw.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.ok(!sw.classList.contains('on'), 'switch flips off via class');
  assert.equal(sw.getAttribute('aria-checked'), 'false');
  // JSON-round-trip: the post is created in the jsdom realm, the expected
  // literal in Node's — deepStrictEqual needs matching prototypes.
  assert.deepEqual(JSON.parse(JSON.stringify(skin.bridge._posts.at(-1))),
    { action: 'set_auto_reconnect', enabled: false }, 'host receives the toggle');
  skin.close();
});

test('NEXUS: empty states render through the .nx-empty class, no inline styles', () => {
  const skin = bootNexus({ nodes: [], connected: false });
  const d = skin.document;

  clickNav(skin, 'servers');
  const srv = d.getElementById('nxServers');
  const empty = srv && srv.querySelector('.nx-empty');
  assert.ok(empty, 'servers empty state renders');
  assert.equal(empty.getAttribute('style'), null, 'empty state has no inline styles');
  assert.equal(css(skin, empty).fontSize, '10px', 'empty state size from CSS');

  clickNav(skin, 'games');
  const games = d.getElementById('nxGameList');
  const gEmpty = games && games.querySelector('.nx-empty');
  assert.ok(gEmpty, 'games empty state renders');
  assert.ok(gEmpty.classList.contains('center'), 'games empty state centered via class');
  assert.ok(gEmpty.classList.contains('slim'), 'games empty state slim padding via class');
  assert.equal(gEmpty.getAttribute('style'), null);
  skin.close();
});

test('NEXUS: GPN Servers view renders managed servers, probes and actions', () => {
  const servers = [
    { serverId: 'srv-1', name: 'Italy · IT-01', endpointHost: '92.4.220.236', endpointPort: 51820, isEnabled: true, keyProtected: true, clientAddress: '10.66.66.2/24', mtu: 1420, dns: '1.1.1.1' },
    { serverId: 'srv-2', name: 'Germany · DE-02', endpointHost: '5.161.91.19', endpointPort: 51820, isEnabled: false, keyProtected: true }
  ];
  const probes = [
    { serverId: 'srv-1', isSuccess: true, delayMs: 18.4, lossPercent: 0, udpStatus: 'Open' }
  ];
  const skin = bootNexus({ gpnServers: servers, gpnServerProbes: probes });
  const d = skin.document;

  clickNav(skin, 'gpnsrv');
  const root = d.getElementById('nxGpnServers');
  assert.ok(root, 'GPN servers root exists');

  // Toolbar actions render.
  assert.equal(root.querySelectorAll('[data-nx-gpn]').length >= 4, true, 'toolbar + import actions render');

  // Managed server rows render with the probe badge on enabled servers.
  const rows = root.querySelectorAll('.nx-gpnSrv');
  assert.equal(rows.length, 2, 'both managed servers render');
  assert.ok(rows[0].textContent.includes('Italy · IT-01'), 'server name shows');
  assert.ok(rows[0].textContent.includes('ACTIVE'), 'enabled badge shows');
  assert.ok(rows[0].textContent.includes('18 ms'), 'live probe delay shows');
  assert.ok(rows[0].textContent.includes('OPEN'), 'UDP probe status shows');
  assert.ok(rows[1].textContent.includes('DISABLED'), 'disabled badge shows');
  assert.ok(!rows[1].textContent.includes('ms'), 'disabled servers skip the probe badge');

  // The import textarea + DPAPI note render.
  const conf = d.getElementById('nxGpnConf');
  assert.ok(conf, 'conf import textarea renders');
  assert.ok(root.textContent.includes('DPAPI'), 'DPAPI note shows');

  // Empty state renders when the host has no servers.
  const emptySkin = bootNexus({ gpnServers: [] });
  clickNav(emptySkin, 'gpnsrv');
  const emptyRoot = emptySkin.document.getElementById('nxGpnServers');
  assert.ok(emptyRoot.querySelector('.nx-empty'), 'no-servers empty state renders');
  emptySkin.close();
  skin.close();
});

test('NEXUS: About view renders program info, help and tabs', () => {
  const skin = bootNexus(CONNECTED);
  const d = skin.document;

  clickNav(skin, 'about');
  const root = d.getElementById('nxAbout');
  assert.ok(root, 'about root exists');
  assert.ok(root.textContent.includes('V7.26.54'), 'app version from the bridge shows');
  assert.ok(root.textContent.includes('Program Info'), 'info tab shows');
  assert.ok(root.textContent.includes('GPL-3.0'), 'license row shows');
  assert.ok(root.textContent.includes('Xray'), 'cores row shows');

  // Help tab cards render on switch and persist across a host push re-render.
  const helpTab = root.querySelector('[data-nx-about="help"]');
  helpTab.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.equal(root.querySelectorAll('.nx-aboutCard').length, 5, 'five help cards render');
  assert.ok(root.querySelector('.nx-aboutCard[href*="github.com/AhmetOzbay27/AoGPN/wiki"]'), 'wiki link renders');

  skin.push(); // host push must not reset the active about tab
  assert.ok(skin.document.querySelector('.nx-aboutCard'), 'cards still visible after re-render');
  assert.ok(root.querySelector('[data-nx-about="help"]').classList.contains('active'), 'help tab stays active');
  skin.close();
});

test('NEXUS: dashboard GPN panel renders connect, tunnel info, boost cards and cluster', () => {
  const data = {
    connected: true,
    mode: 'gpn',
    transport: 'tun',
    sessionTime: '00:12:34',
    activeGpnServer: 'Italy · IT-01',
    gpnConnectState: { label: 'GPN · IT-01', pending: false },
    realIpState: {
      connected: true, tunnelVerified: true, transport: 'tun',
      tunnelIp: '92.4.220.236', tunnelCountry: 'IT', ispIp: '88.11.22.33', ispCached: true
    },
    monitorSnapshot: {
      apps: [
        { displayName: 'Counter-Strike 2', processName: 'cs2.exe', action: 'vpn', isRunning: true, beforePingText: '45 ms', afterPingText: '22 ms', afterPingMs: 22, pingDeltaText: '-23' },
        { displayName: 'Valorant', processName: 'valorant.exe', action: 'vpn+proxy', isRunning: false }
      ]
    },
    gpnServers: [
      { serverId: 'srv-1', name: 'Italy · IT-01', endpointHost: '92.4.220.236', endpointPort: 51820, isEnabled: true },
      { serverId: 'srv-2', name: 'Germany · DE-02', endpointHost: '5.161.91.19', endpointPort: 51820, isEnabled: true },
      { serverId: 'srv-3', name: 'Austria · AT-01', endpointHost: '10.0.0.1', endpointPort: 51820, isEnabled: false }
    ],
    gpnServerProbes: [
      { serverId: 'srv-1', isSuccess: true, delayMs: 18.4, lossPercent: 0, udpStatus: 'Open' },
      { serverId: 'srv-2', isSuccess: true, delayMs: 31.2, lossPercent: 0, udpStatus: 'Blocked' }
    ]
  };
  const skin = bootNexus(data);
  const d = skin.document;

  const panel = d.getElementById('nxGpnPanel');
  assert.ok(panel, 'GPN panel container exists');

  // GPN Connect button + live state badge.
  const gpnBtn = panel.querySelector('.nx-gpnConnect');
  assert.ok(gpnBtn, 'GPN Connect button renders');
  assert.ok(panel.textContent.includes('GPN · IT-01'), 'connect state badge shows');
  gpnBtn.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(JSON.parse(JSON.stringify(skin.bridge._posts.at(-1))), { action: 'gpn_connect' }, 'GPN Connect posts to host');

  // Live tunnel info: Your IP / Session / Verification cards.
  const infoCards = panel.querySelectorAll('.nx-infoGrid .nx-infoCard');
  assert.equal(infoCards.length, 3, 'IP / session / verification cards render');
  assert.ok(panel.textContent.includes('92.4.220.236'), 'tunnel IP shows');
  assert.ok(panel.textContent.includes('00:12:34'), 'session time shows');
  assert.ok(panel.textContent.includes('TUNNELED'), 'verification shows tunneled state');

  // Boost cards: running game shows the before → after real ping delta.
  const boost = panel.querySelectorAll('.nx-boost');
  assert.equal(boost.length, 2, 'both vpn-routed games render as boost cards');
  assert.ok(boost[0].textContent.includes('Counter-Strike 2'), 'running game name shows');
  assert.ok(boost[0].textContent.includes('45 ms → 22 ms'), 'real ping before→after shows');
  assert.ok(boost[1].classList.contains('dim'), 'idle game dims');

  // Server cluster: enabled servers only, BEST pill on the fastest open-UDP one.
  const clusterRows = panel.querySelectorAll('.nx-clusterRow');
  assert.equal(clusterRows.length, 2, 'only enabled servers show in the cluster');
  assert.ok(panel.querySelector('.nx-bestPill'), 'best server gets the BEST pill');
  assert.ok(panel.textContent.includes('1 open · 18 ms'), 'cluster ping badge counts open servers');
  assert.ok(clusterRows[1].textContent.includes('Blocked'), 'blocked UDP shows its status');
  skin.close();
});

test('NEXUS: dashboard Advanced & Diagnostics renders all cards with live host data', () => {
  const data = {
    connected: true,
    mode: 'gpn',
    gpnSelectionPrediction: {
      mode: 'WireGuardUDP', measuredAt: '2026-09-01T10:00:00Z', bestName: 'Italy · IT-01', pingMs: 18.4, reason: 'fastest healthy WireGuard candidate',
      servers: [{ name: 'Italy · IT-01', delayMs: 18.4, udpStatus: 'Open', selected: true }, { name: 'Germany · DE-02', delayMs: 31.2, udpStatus: 'Blocked' }]
    },
    gpnPidPool: { watching: true, targetRunning: true, sourceStatus: 'Healthy', targetNames: ['cs2.exe'], pids: [5123, 5124] },
    gpnCaptureStats: { totalPackets: 12500, udpPackets: 12000, tcpPackets: 500, totalBytes: 10485760, outboundPackets: 8000, inboundPackets: 4500, topFlows: [{ peerEndpoint: '92.4.220.236:51820', protocol: 'UDP', packets: 9000 }], byPid: [{ pid: 5123, packets: 8800, inPool: true }] },
    gpnFailoverMatrix: {
      measuredAt: '2026-09-01T10:00:00Z',
      defaultPolicy: { action: 'Keep', reason: 'active server healthy' },
      strictPolicy: { action: 'SwitchServer', targetServerId: 'srv-2', reason: 'jitter above threshold' },
      rows: [{ name: 'Italy · IT-01', pingMs: 18, udpStatus: 'Open', isActive: true, healthyDefault: true, healthyStrict: true }, { name: 'Germany · DE-02', pingMs: 31, udpStatus: 'Blocked', isActive: false, healthyDefault: false, healthyStrict: false }]
    },
    gpnTelemetry: { totalEvents: 7, serverSwitches: 2, udpDeaths: 1, modeFallbacks: 2, recoveries: 1, modeDecisions: 1 },
    gpnCaptureSettings: { queueLen: 256, queueTime: 2000, queueSize: 0, enableQueueLen: true, enableQueueTime: true, enableQueueSize: false, layer: 0, direction: 1 },
    gpnWintunSettings: { adapterName: 'AoGPN', ringCapacity: 4194304 },
    gpnDiagLines: [{ timestampMs: 1000, kind: 'GPN_SELECT', message: 'selected Italy · IT-01' }],
    gpnResilienceLog: { path: 'C:/logs/gpn.log', entries: [{ action: 'ServerSwitch', timestampMs: 2000, toMode: 'WireGuard', serverName: 'Italy · IT-01', reason: 'latency improved' }] }
  };
  const skin = bootNexus(data);
  const d = skin.document;
  const panel = d.getElementById('nxGpnPanel');

  // Advanced group starts collapsed.
  const toggle = panel.querySelector('[data-nx-adv-toggle]');
  assert.ok(toggle, 'Advanced & Diagnostics toggle renders');
  assert.equal(panel.querySelector('.nx-advBody'), null, 'advanced body hidden until expanded');
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');

  // Expand: all eleven cards render with live data.
  toggle.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const body = panel.querySelector('.nx-advBody');
  assert.ok(body, 'advanced body expands');
  assert.equal(body.querySelectorAll('.nx-advCard').length, 11, 'eleven advanced cards render');
  assert.ok(body.textContent.includes('Italy · IT-01'), 'best candidate name shows');
  assert.ok(body.textContent.includes('WireGuard'), 'candidate mode badge shows');
  assert.ok(body.textContent.includes('cs2.exe'), 'PID pool target shows');
  assert.ok(body.textContent.includes('5123 · 5124'), 'PID pool PIDs show');
  assert.ok(body.textContent.includes('12.5k'), 'capture total packets show (formatted)');
  assert.ok(body.textContent.includes('92.4.220.236:51820'), 'top flow endpoint shows');
  assert.ok(body.textContent.includes('Switch to srv-2'), 'strict policy decision shows');
  assert.ok(body.textContent.includes('switch2') && body.textContent.includes('fallback2'), 'telemetry counters show');
  assert.ok(body.textContent.includes('AoGPN'), 'Wintun adapter name shows');
  assert.ok(body.textContent.includes('GPN_SELECT'), 'diagnostics feed line shows');
  assert.ok(body.textContent.includes('Server switch'), 'resilience log entry label shows');
  assert.ok(body.textContent.includes('C:/logs/gpn.log'), 'resilience log path shows');

  // Failover + recovery toggles post the same host actions as the dashboard.
  const fail = body.querySelector('[data-nx-advfailover]');
  fail.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(JSON.parse(JSON.stringify(skin.bridge._posts.at(-1))), { action: 'set_gpn_failover', enabled: true }, 'failover toggle posts');
  const rec = body.querySelector('[data-nx-advrecovery]');
  rec.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(JSON.parse(JSON.stringify(skin.bridge._posts.at(-1))), { action: 'set_gpn_recovery_watch', enabled: true }, 'recovery toggle posts');
  skin.close();
});

test('NEXUS: Global VPN panel + status banners follow mode and live state', () => {
  // Global VPN mode, connected, tunnel verified.
  const skin = bootNexus({
    connected: true,
    mode: 'vpn',
    transport: 'tun',
    realIpState: { connected: true, tunnelVerified: true, tunnelIp: '92.4.220.236', ispIp: '88.11.22.33', ispCached: true }
  });
  const d = skin.document;
  const global = d.getElementById('nxGlobalPanel');
  assert.ok(global, 'global panel exists');
  assert.ok(!global.classList.contains('nx-hidden'), 'global panel visible in vpn mode');
  assert.ok(global.textContent.includes('TUN'), 'transport shows TUN');
  assert.ok(global.textContent.includes('100%'), 'coverage shows 100%');
  assert.ok(global.textContent.includes('Tunnel verified'), 'verification shows verified');
  const gpn = d.getElementById('nxGpnPanel');
  assert.ok(gpn.textContent.length > 0, 'GPN panel still renders behind the mode');

  // GPN mode hides the global panel.
  const gpnSkin = bootNexus({ connected: true, mode: 'gpn', transport: 'proxy', realIpState: { connected: true, tunnelVerified: true, tunnelIp: '1.2.3.4' } });
  assert.ok(gpnSkin.document.getElementById('nxGlobalPanel').classList.contains('nx-hidden'), 'global panel hidden in gpn mode');
  gpnSkin.close();
  skin.close();
});

test('NEXUS: status banners surface connection error, IP leak and TUN admin', () => {
  // Connection error + IP leak (connected, tunnel IP matches ISP).
  const skin = bootNexus({
    connected: true,
    mode: 'gpn',
    transport: 'tun',
    isAdmin: true,
    connectionError: { message: 'TUN mode requires administrator privileges.', details: 'wintun open failed (5)', elevation: true },
    realIpState: { connected: true, tunnelVerified: false, tunnelIp: '88.11.22.33', ispIp: '88.11.22.33', ispCached: true }
  });
  const d = skin.document;
  const banners = d.getElementById('nxBanners');
  assert.ok(banners, 'banner container exists');
  assert.equal(banners.querySelectorAll('.nx-banner').length >= 2, true, 'error + leak banners render');
  assert.ok(banners.textContent.includes('TUN mode requires administrator privileges'), 'connection error message shows');
  assert.ok(banners.textContent.includes('wintun open failed (5)'), 'error details show');
  assert.ok(banners.textContent.includes('traffic may be leaking'), 'IP leak banner shows');
  assert.ok(banners.textContent.includes('Relaunch as admin'), 'relaunch button renders');

  const relaunch = banners.querySelector('[data-nx-relaunch]');
  relaunch.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(JSON.parse(JSON.stringify(skin.bridge._posts.at(-1))),
    { action: 'app_control', command: 'reboot_as_admin' }, 'relaunch posts reboot_as_admin');

  // TUN selected without elevation while DISCONNECTED locks CONNECT with a banner.
  const tunSkin = bootNexus({ connected: false, mode: 'gpn', transport: 'tun', isAdmin: false });
  const tunBanners = tunSkin.document.getElementById('nxBanners');
  assert.ok(tunBanners.textContent.includes('Relaunch as admin'), 'TUN admin banner renders while disconnected');
  assert.ok(tunBanners.textContent.includes('requires administrator privileges'), 'TUN admin message shows');
  tunSkin.close();

  // Clean connected state: verification banner shows instead, no error.
  const okSkin = bootNexus({
    connected: true, mode: 'gpn', transport: 'proxy', isAdmin: true,
    realIpState: { connected: true, tunnelVerified: true, tunnelIp: '92.4.220.236', ispIp: '88.11.22.33', ispCached: true }
  });
  const okBanners = okSkin.document.getElementById('nxBanners');
  assert.ok(okBanners.textContent.includes('Tunnel verified'), 'verification success banner shows');
  assert.ok(!okBanners.textContent.includes('leaking'), 'no false leak banner');
  okSkin.close();
  skin.close();
});

test('NEXUS: Game Profiles rows reorder via move buttons and lock at the edges', () => {
  const skin = bootNexus({
    connected: true,
    mode: 'gpn',
    monitorSnapshot: {
      apps: [
        { id: 1, entryType: 'domain', value: 'escapefromtarkov.com', displayName: 'BSG API', action: 'warp' },
        { id: 2, entryType: 'app', processName: 'cs2.exe', displayName: 'Counter-Strike 2', action: 'vpn' },
        { id: 3, entryType: 'app', processName: 'valorant.exe', displayName: 'Valorant', action: 'direct' }
      ],
      autoConnectOnGameStart: true,
      invertManualRouting: false
    }
  });
  const d = skin.document;
  clickNav(skin, 'games');

  const move = (etype, value, dir) => d.querySelector(
    `[data-nx-move="${dir}"][data-nx-etype="${etype}"][data-nx-value="${value}"]`);
  assert.ok(move('domain', 'escapefromtarkov.com', 'down'), 'domain row renders a down control');
  assert.ok(move('domain', 'escapefromtarkov.com', 'up').classList.contains('off'), 'first row cannot move up');
  assert.ok(!move('app', 'valorant.exe', 'up').classList.contains('off'), 'last row can move up');
  assert.ok(move('app', 'valorant.exe', 'down').classList.contains('off'), 'last row cannot move down');

  // A middle app row moves both ways and posts the identity payload.
  const cs2Down = move('app', 'cs2.exe', 'down');
  cs2Down.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const cs2Up = move('app', 'cs2.exe', 'up');
  cs2Up.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const posts = skin.bridge._posts.filter(p => p.action === 'move_route');
  assert.equal(posts.length, 2);
  assert.deepEqual(JSON.parse(JSON.stringify(posts[0])),
    { action: 'move_route', entryType: 'app', value: 'cs2.exe', direction: 'down' });
  assert.deepEqual(JSON.parse(JSON.stringify(posts[1])),
    { action: 'move_route', entryType: 'app', value: 'cs2.exe', direction: 'up' });

  // Domain row click posts the domain identity (it must be movable above/below
  // the process rules).
  const domainDown = move('domain', 'escapefromtarkov.com', 'down');
  domainDown.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const last = skin.bridge._posts.filter(p => p.action === 'move_route').at(-1);
  assert.deepEqual(JSON.parse(JSON.stringify(last)),
    { action: 'move_route', entryType: 'domain', value: 'escapefromtarkov.com', direction: 'down' });
  skin.close();
});

test('NEXUS: BSG API quick button and manual domain row post add_domain_route', () => {
  const skin = bootNexus({ connected: true, mode: 'gpn', monitorSnapshot: { apps: [] } });
  const d = skin.document;
  clickNav(skin, 'games');

  // One-click BSG API rule.
  const bsz = d.querySelector('[data-nx-bszapi]');
  assert.ok(bsz, 'BSG API quick button exists');
  bsz.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(JSON.parse(JSON.stringify(skin.bridge._posts.filter(p => p.action === 'add_domain_route').at(-1))), {
    action: 'add_domain_route', value: 'escapefromtarkov.com', route: 'warp', displayName: 'BSG API (escapefromtarkov.com)'
  });

  // Manual domain form: toggle opens the row, typing + submit posts the payload.
  const toggle = d.querySelector('[data-nx-adddomain]');
  toggle.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  const valueInput = d.querySelector('[data-nx-domainvalue]');
  assert.ok(valueInput, 'domain input renders after toggling');
  valueInput.value = 'prod.example.com';
  valueInput.dispatchEvent(new skin.window.Event('input', { bubbles: true }));
  const actionSel = d.querySelector('[data-nx-domainaction]');
  assert.equal(actionSel.value, 'vpn', 'domain action defaults to vpn');
  actionSel.value = 'warp';
  const submit = d.querySelector('[data-nx-domainsubmit]');
  submit.dispatchEvent(new skin.window.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.deepEqual(JSON.parse(JSON.stringify(skin.bridge._posts.filter(p => p.action === 'add_domain_route').at(-1))), {
    action: 'add_domain_route', value: 'prod.example.com', route: 'warp', displayName: 'prod.example.com'
  });
  assert.equal(d.querySelector('[data-nx-domainvalue]'), null, 'domain row closes after submit');
  skin.close();
});
