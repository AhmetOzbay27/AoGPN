// ===== AoGPN INFRA skin — infrastructure terminal =====
// Same isolation contract as CYBER/NEXUS: lives fully inside its own iframe,
// reads live app state and fires host actions only through the parent's
// skinBridge. The CONNECT toggle maps to toggle_connection, the app list comes
// from monitorSnapshot (with per-app route switches -> set_app_route), the node
// list is the real route endpoints (requestNodeSwitch/applyNode), and the
// connection controls mirror the standard dashboard's mode/transport/split/
// proxy/protocol/auto-reconnect setters. Telemetry/session/node come from the
// bridge.
(function () {
  'use strict';

  const B = (window.parent && window.parent !== window && window.parent.skinBridge) ? window.parent.skinBridge : null;
  const PREVIEW = /[?&]preview=1/.test(window.location.search || '');

  const DEMO = {
    connected: false, connecting: false, mode: 'gpn', transport: 'proxy', splitMode: 'off',
    systemProxyMode: 0, protocolPreference: 'auto', autoReconnect: true,
    sessionTime: '00:00:00', exitIp: 'Not verified', exitCountry: '',
    telemetry: [null, null, null, null],
    useRealNodes: false, activeRealNodeId: null, selectedNode: null,
    nodes: [
      { key: 'istanbul', code: 'TR', name: 'Istanbul · TR-01', addr: '145.239.14.77', ping: 18 },
      { key: 'frankfurt', code: 'DE', name: 'Frankfurt · DE-11', addr: '5.161.91.19', ping: 148 },
      { key: 'amsterdam', code: 'NL', name: 'Amsterdam · NL-02', addr: '51.15.118.35', ping: 171 }
    ],
    monitorSnapshot: { apps: [] }, routeLabels: {}, language: 'en', isAdmin: true
  };

  function G(name) { if (B) { try { return B[name]; } catch (e) { /* ignore */ } } return DEMO[name]; }
  function SET(name, value) { if (B) { try { B[name] = value; } catch (e) { /* read-only */ } } }
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  // Resolve a UI string through the parent bridge: skin.infra.* keys in
  // Dil/*.json win, then the shared dictionary (e.g. mode.gpn), then the
  // skin's own English default, then the raw key — same contract as CYBER's
  // cyT / NEXUS's nxT, scoped to this skin.
  const IF_TEXT = {
    'route.vpn': 'VPN',
    'route.direct': 'Direct',
    'route.proxy': 'Proxy',
    'route.vpn+proxy': 'VPN + Proxy',
    'route.block': 'Block',
    'node': 'NODE',
    'panel.apps': 'BOOST APPS',
    'panel.nodes': 'ROUTE NODES',
    'panel.telemetry': 'TELEMETRY',
    'panel.proxy': 'PROXY / PROTOCOL',
    'status.offline': 'OFFLINE',
    'status.online': 'ONLINE',
    'status.connecting': 'CONNECTING',
    'connect': 'CONNECT',
    'disconnect': 'DISCONNECT',
    'connecting': 'CONNECTING',
    'sub.gpn': 'GPN',
    'sub.globalVpn': 'GLOBAL VPN',
    'sub.tun': 'TUN',
    'sub.proxy': 'PROXY',
    'mode.gpn': 'GPN',
    'mode.globalVpn': 'VPN',
    'transport.proxy': 'PROXY',
    'transport.tun': 'TUN',
    'split.off': 'OFF',
    'split.global': 'VPN',
    'split.gpn': 'GPN',
    'dir.whitelist': 'WHITELIST',
    'dir.blacklist': 'BLACKLIST',
    'ctl.mode': 'MODE',
    'ctl.transport': 'CAPTURE',
    'ctl.split': 'SPLIT',
    'ctl.dir': 'DIRECTION',
    'ctl.autoReconnect': 'AUTO RECONNECT',
    'ctl.systemProxy': 'SYSTEM PROXY',
    'ctl.proxyMode': 'PROXY MODE',
    'ctl.protocol': 'PROTOCOL',
    'proxy.on': 'PROXY ON',
    'proxy.off': 'PROXY OFF',
    'tele.ping': 'PING',
    'tele.loss': 'LOSS',
    'tele.down': 'DOWN',
    'tele.up': 'UP',
    'metric.session': 'SESSION',
    'metric.exitIp': 'EXIT IP',
    'empty': 'NO BOOST APPS',
    'noNodes': 'NO NODES',
    'switchTitle': 'Toggle routing (Enter/Space or ←/→)',
    'sysProxyTitle': 'Toggle system proxy',
    'console.ready': '> INFRA SHELL READY. WAITING FOR TUNNEL.',
    'console.tunnelUp': 'TUNNEL UP',
    'console.tunnelDown': 'TUNNEL DOWN',
    'log.toggle': 'TOGGLE SENT: {state}',
    'log.mode': 'MODE: {mode}',
    'log.transport': 'CAPTURE: {transport}',
    'log.split': 'SPLIT MODE: {label}',
    'log.dir': 'DIRECTION: {dir}',
    'log.autoReconnect': 'AUTO RECONNECT: {state}',
    'log.systemProxy': 'SYSTEM PROXY: {state}',
    'log.proxyMode': 'PROXY MODE: {mode}',
    'log.protocol': 'PROTOCOL: {protocol}',
    'log.node': 'NODE: {node}',
    'log.routeSet': 'ROUTE {route} FOR {app}',
    'log.language': 'LANG: {lang}',
    'log.on': 'ON',
    'log.off': 'OFF',
    'lang': 'LANG',
    'connectAria': 'Connect',
    'stdTitle': 'Standard dashboard',
    // Console tabs (dashboard module parity).
    'tabs.terminal': 'TERMINAL',
    'tabs.nodes': 'NODES',
    'tabs.gpn': 'GPN',
    'tabs.monitor': 'MONITOR',
    'tabs.about': 'ABOUT',
    // Extended settings (dashboard parity).
    'ctl.tunStack': 'TUN STACK',
    'ctl.effects': 'EFFECTS',
    'ctl.autoGame': 'AUTO-GAME',
    'ctl.recovery': 'RECOVERY',
    'ctl.failover': 'FAILOVER',
    'ctl.proxyTest': 'PROXY TEST',
    'test.idle': 'TEST',
    'test.ok': 'OK',
    'test.fail': 'FAIL',
    'test.running': 'TESTING…',
    'autoGameTitle': 'Auto-connect when a listed game starts',
    'recoveryTitle': 'Auto-return to WireGuard after a V2ray fallback',
    'failoverTitle': 'Allow automatic server switching on failure',
    'log.tunStack': 'TUN STACK: {stack}',
    'log.effects': 'EFFECTS: {effects}',
    'log.autoGame': 'AUTO-GAME CONNECT: {state}',
    'log.recovery': 'RECOVERY WATCH: {state}',
    'log.failover': 'FAILOVER: {state}',
    'log.proxyTest': 'PROXY TEST INITIATED.',
    // GPN tab.
    'gpn.servers': 'GPN SERVERS',
    'gpn.measure': 'MEASURE',
    'gpn.jackin': 'JACK IN',
    'gpn.none': 'NO GPN SERVERS IMPORTED.',
    'gpn.noneSub': 'IMPORT ITALY / GERMANY WIREGUARD SERVERS IN THE DASHBOARD.',
    'gpn.telTitle': 'FAILOVER TELEMETRY',
    'gpn.resTitle': 'LAST 50 DECISIONS',
    'gpn.resRefresh': 'REFRESH',
    'gpn.resClear': 'CLEAR',
    'gpn.resEmpty': 'NO DECISIONS RECORDED YET.',
    'gpn.resPath': 'MIRRORED TO',
    'gpn.ev.switch': 'SERVER SWITCH',
    'gpn.ev.udpDeath': 'UDP DEATH',
    'gpn.ev.fallback': 'MODE FALLBACK',
    'gpn.ev.recover': 'RECOVER',
    'gpn.ev.select': 'SELECTION',
    // Monitor tab.
    'node.colProgram': 'PROGRAM',
    'mon.noMatch': 'NO CONNECTIONS MATCH THE FILTER.',
    'mon.none': 'NO VISIBLE CONNECTIONS.',
    // About tab.
    'about.title': 'ABOUT // INFRA SHELL',
    'about.version': 'VERSION',
    'about.skin': 'SKIN',
    'about.session': 'SESSION',
    'about.hint': 'ALL DASHBOARD MODULES ARE LIVE IN THIS CONSOLE: NODES, GPN SERVERS, CONNECTION MONITOR AND SETTINGS.',
    'about.hint2': 'SWITCH TO THE STANDARD DASHBOARD ANYTIME FOR THE FULL MODULE MATRIX.'
  };
  function ifT(key, params) {
    let val = null;
    if (B && typeof B.t === 'function') {
      try { val = B.t(key, params, 'infra'); } catch (e) { /* fall through */ }
      if (typeof val === 'string' && val === key) val = null;
    }
    if ((val === null || val === undefined || val === '') && Object.prototype.hasOwnProperty.call(IF_TEXT, key)) {
      val = IF_TEXT[key];
    }
    if (val === null || val === undefined || val === '') return key;
    if (params) {
      Object.keys(params).forEach(k => { val = String(val).split('{' + k + '}').join(String(params[k])); });
    }
    return String(val);
  }

  // Language list from the bridge (same LANGUAGES the top-bar picker uses),
  // with a small standalone fallback for the design preview.
  function ifLangs() {
    const langs = (B && B.LANGUAGES) ? B.LANGUAGES : [
      { code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }, { code: 'fa', name: 'فارسی' },
      { code: 'fr', name: 'Français' }, { code: 'ru', name: 'Русский' }
    ];
    return langs.map(l => [l.code, l.name]);
  }
  function ifOpts(list, current) {
    return list.map(o => {
      const eq = String(current) === String(o[0]);
      return '<option value="' + esc(o[0]) + '"' + (eq ? ' selected' : '') + '>' + esc(o[1]) + '</option>';
    }).join('');
  }

  // Apply [data-if-i18n] on static markup and [data-if-i18n-title] tooltips,
  // preserving nested elements — mirrors the main dashboard's applyTexts.
  function applyStaticTexts() {
    document.querySelectorAll('[data-if-i18n]').forEach(el => {
      const key = el.getAttribute('data-if-i18n');
      if (!key) return;
      const val = ifT(key);
      if (!val || val === key) return;
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
        acceptNode(n) {
          const p = n.parentElement;
          if (p && p.closest && p.closest('svg,script,style')) return NodeFilter.FILTER_REJECT;
          return NodeFilter.FILTER_ACCEPT;
        }
      });
      let replaced = false;
      while (walker.nextNode()) {
        const node = walker.currentNode;
        if (node.nodeValue && node.nodeValue.trim() !== '') {
          node.nodeValue = val;
          replaced = true;
        }
      }
      if (!replaced && el.children.length === 0) el.textContent = val;
    });
    document.querySelectorAll('[data-if-i18n-title]').forEach(el => {
      const val = ifT(el.getAttribute('data-if-i18n-title'));
      if (val && val !== el.getAttribute('data-if-i18n-title')) el.setAttribute('title', val);
    });
  }

  function state() {
    return {
      connected: G('connected') === true, connecting: G('connecting') === true,
      mode: G('mode') || 'gpn', transport: G('transport') || 'proxy',
      splitMode: G('splitMode') || 'off',
      systemProxyMode: G('systemProxyMode'), protocolPreference: G('protocolPreference') || 'auto',
      autoReconnect: G('autoReconnect') === true,
      useRealNodes: G('useRealNodes') === true, activeRealNodeId: G('activeRealNodeId'), selectedNode: G('selectedNode'),
      sessionTime: G('sessionTime') || '00:00:00', exitIp: G('exitIp') || '—',
      telemetry: G('telemetry') || [null, null, null, null],
      nodes: G('nodes') || [], monitorSnapshot: G('monitorSnapshot') || { apps: [] },
      routeLabels: G('routeLabels') || {}, language: G('language') || 'en',
      // Extended settings + dashboard modules (skinBridge parity).
      tunStack: G('tunStack') || 'mixed', effectsTier: G('effectsTier') || 'full',
      gpnRecoveryWatch: G('gpnRecoveryWatch'), gpnFailover: G('gpnFailover'),
      proxyTestResult: G('proxyTestResult') || null,
      gpnServers: G('gpnServers') || [], gpnServerProbes: G('gpnServerProbes') || [],
      activeGpnServer: G('activeGpnServer') || '', appInfo: G('appInfo') || null,
      gpnTelemetry: G('gpnTelemetry') || null, gpnResilienceLog: G('gpnResilienceLog') || null
    };
  }

  function routeLabel(action) {
    const a = action || '';
    const tr = ifT('route.' + a);
    if (tr && tr !== 'route.' + a) return tr;
    const labels = state().routeLabels || {};
    return labels[a] || (a ? String(a).toUpperCase() : 'VPN');
  }

  // ---- route handling for boost-app switches (shared keyboard contract via
  // route-keys.js, same local-override model as CYBER/NEXUS) ----
  const _localRoutes = {};   // processName -> action override
  const _localRouteTs = {};  // processName -> ms timestamp of the override
  const ROUTE_CONFIRM_MS = 2500;

  const routeFromKey = (typeof AoGPNRouteKeys !== 'undefined' && AoGPNRouteKeys.routeFromKey)
    ? AoGPNRouteKeys.routeFromKey
    : function (key, currentAction) {
        if (key === 'Enter' || key === ' ') return isTunneled(currentAction) ? 'direct' : 'vpn';
        if (key === 'ArrowRight') return isTunneled(currentAction) ? null : 'vpn';
        if (key === 'ArrowLeft') return isTunneled(currentAction) ? 'direct' : null;
        return null;
      };
  const ifRouteCycle = (typeof AoGPNRouteKeys !== 'undefined' && AoGPNRouteKeys.routeCycle)
    ? AoGPNRouteKeys.routeCycle
    : function (action) {
        const order = ['vpn', 'direct', 'block', 'warp'];
        const cur = isTunneled(action) ? 'vpn' : action;
        const idx = order.indexOf(cur);
        return order[(idx + 1) % order.length];
      };

  function isTunneled(action) { return action === 'vpn' || action === 'vpn+proxy' || action === 'proxy'; }

  function actionFor(a) {
    if (a && a.processName && Object.prototype.hasOwnProperty.call(_localRoutes, a.processName)) {
      return _localRoutes[a.processName];
    }
    return a ? (a.action || '') : '';
  }

  function setAppRoute(processName, next) {
    if (!processName) return;
    const S = state();
    const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === processName);
    const cur = actionFor(app || { processName });
    if (cur === next) return;
    _localRoutes[processName] = next;
    _localRouteTs[processName] = Date.now();
    const displayName = app ? (app.displayName || processName) : processName;
    if (B) B.postToHost({ action: 'set_app_route', processName, displayName, route: next });
    logLine(ifT('log.routeSet', { route: routeLabel(next).toUpperCase(), app: processName.toUpperCase() }), 'sys');
    renderApps();
  }

  function toggleAppRoute(processName) {
    if (!processName) return;
    const S = state();
    const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === processName);
    const cur = actionFor(app || { processName });
    setAppRoute(processName, isTunneled(cur) ? 'direct' : 'vpn');
  }

  // Gerçek exe ikonu (host AppIconService -> skinBridge.appIcons); ikon yoksa
  // iki harfli yer tutucu kutu basılır.
  function ifAppIcon(app) {
    let uri = '';
    if (app && app.exePath && B) {
      try {
        const icons = B.appIcons;
        if (icons) uri = icons[String(app.exePath).trim().toLowerCase()] || '';
      } catch (e) { /* read-only bridge */ }
    }
    const label = String(app.displayName || app.processName || 'APP').slice(0, 2).toUpperCase() || '?';
    if (uri) return '<i class="if-appIco"><img src="' + uri + '" alt="" draggable="false"></i>';
    return '<i class="if-appIco">' + esc(label) + '</i>';
  }

  function renderApps() {
    const list = $('ifApps');
    if (!list) return;
    const apps = (state().monitorSnapshot.apps || []).slice(0, 8);
    list.innerHTML = apps.map(app => {
      const live = app.isRunning === true;
      const label = routeLabel(actionFor(app));
      const pname = app.processName || app.value || app.name || '';
      const on = isTunneled(actionFor(app));
      return '<div class="if-row' + (live ? ' live' : '') + '" data-app="' + esc(pname) + '" data-if-pname="' + esc(pname) + '">' +
        ifAppIcon(app) +
        '<span>' + esc(app.displayName || app.processName) + '</span>' +
        '<i><em style="width:' + (live ? 100 : 0) + '%"></em></i>' +
        '<b>' + esc(label) + '</b>' +
        '<button type="button" class="if-switch' + (on ? ' on' : '') + '" role="switch" aria-checked="' + on + '" tabindex="0" aria-label="' + esc(ifT('switchTitle')) + '" data-if-app-toggle="' + esc(pname) + '"></button>' +
        '</div>';
    }).join('') || '<div class="if-row">' + esc(ifT('empty')) + '</div>';
    list.querySelectorAll('[data-if-app-toggle]').forEach(sw => {
      const pname = sw.getAttribute('data-if-app-toggle');
      sw.addEventListener('click', (e) => { e.stopPropagation(); toggleAppRoute(pname); });
      sw.addEventListener('keydown', (e) => {
        const S = state();
        const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === pname);
        const next = routeFromKey(e.key, actionFor(app || { processName: pname }));
        if (next) { e.preventDefault(); setAppRoute(pname, next); }
      });
    });
  }

  // ---- route node selection ----
  function nodeKey(n, useReal) { return useReal ? n.indexId : n.key; }
  function nodeLabel(n, useReal) { return useReal ? (n.name || n.address || n.indexId) : (n.name + ' · ' + n.addr); }
  function nodeMs(n, useReal) { return useReal ? (n.delay > 0 ? n.delay + ' ms' : '— ms') : (n.ping + ' ms'); }
  function nodeCode(n, useReal) { return useReal ? (n.country || n.sub || 'VPN').slice(0, 2) : (n.code || '??'); }

  function activeNode(S) {
    if (S.useRealNodes) {
      if (S.activeRealNodeId) return S.nodes.find(n => n.indexId === S.activeRealNodeId) || S.nodes[0] || null;
      return S.nodes[0] || null;
    }
    return S.selectedNode || S.nodes[0] || null;
  }

  function switchNode(key) {
    const S = state();
    if (B) {
      if (S.useRealNodes && typeof B.requestNodeSwitch === 'function') { B.requestNodeSwitch(key); }
      else if (!S.useRealNodes) {
        const found = (S.nodes || []).find(n => nodeKey(n, false) === key) || S.nodes[0];
        SET('selectedNode', found);
        if (typeof B.applyNode === 'function') B.applyNode();
      }
      render();
      return;
    }
    DEMO.selectedNode = (DEMO.nodes || []).find(n => n.key === key) || DEMO.nodes[0];
    render();
  }

  function renderNodes() {
    const root = $('ifNodes');
    if (!root) return;
    const S = state();
    const nodes = S.nodes || [];
    if (nodes.length === 0) { root.innerHTML = '<div class="if-row">' + esc(ifT('noNodes')) + '</div>'; return; }
    const act = activeNode(S);
    const actKey = act ? String(nodeKey(act, S.useRealNodes)) : '';
    root.innerHTML = nodes.map(n => {
      const key = String(nodeKey(n, S.useRealNodes));
      const active = key === actKey;
      return '<div class="if-node-row' + (active ? ' active' : '') + '" data-if-node="' + esc(key) + '">' +
        '<code>' + esc(nodeCode(n, S.useRealNodes)) + '</code>' +
        '<b>' + esc(nodeLabel(n, S.useRealNodes)) + '</b>' +
        '<em>' + esc(nodeMs(n, S.useRealNodes)) + '</em>' +
        '</div>';
    }).join('');
    root.querySelectorAll('[data-if-node]').forEach(row => {
      row.addEventListener('click', () => {
        const key = row.getAttribute('data-if-node');
        const S = state();
        const n = S.nodes.find(node => String(nodeKey(node, S.useRealNodes)) === key);
        const name = n ? String(nodeLabel(n, S.useRealNodes)).toUpperCase() : key;
        switchNode(key);
        logLine(ifT('log.node', { node: name }), 'sys');
      });
    });
  }

  function renderTelemetry() {
    const S = state();
    const on = S.connected;
    const t = S.telemetry || [null, null, null, null];
    const has = t.some(v => v !== undefined && v !== null);
    const set = (id, text) => { const el = $(id); if (el) el.textContent = text; };
    const bar = (id, pct) => { const el = $(id); if (el) el.style.width = pct + '%'; };
    const idle = on ? '—' : '—';
    const ping = (on && has && Number.isFinite(t[0]) && t[0] > 0) ? Math.round(t[0]) : null;
    const loss = (on && has && Number.isFinite(t[1]) && t[1] >= 0) ? Number(t[1]) : null;
    const down = (on && has && Number.isFinite(t[2]) && t[2] > 0) ? t[2] : null;
    const up = (on && has && Number.isFinite(t[3]) && t[3] > 0) ? t[3] : null;
    set('ifPing', (ping != null ? ping : idle) + ' ms');
    set('ifLoss', (loss != null ? loss.toFixed(1) : (on ? '0.0' : idle)) + ' %');
    set('ifDown', (down != null ? down.toFixed(1) : '—') + ' Mbps');
    set('ifUp', (up != null ? up.toFixed(1) : '—') + ' Mbps');
    bar('ifPingBar', ping != null ? Math.min(100, ping) : 0);
    bar('ifLossBar', loss != null ? Math.min(100, loss * 2) : 0);
    bar('ifDownBar', Math.min(100, (Number(down) || 0) * 4));
    bar('ifUpBar', Math.min(100, (Number(up) || 0) * 4));
  }

  const isProxyOn = (v) => Number(v) === 1 || Number(v) === 3;
  let _invertManual = false; // optimistic direction until the host echoes a snapshot

  function renderControls() {
    const S = state();
    // MODE pills -> connection mode.
    document.querySelectorAll('[data-if-mode]').forEach(b => b.classList.toggle('active', b.getAttribute('data-if-mode') === (S.mode === 'vpn' ? 'vpn' : 'gpn')));
    // CAPTURE pills -> transport.
    document.querySelectorAll('[data-if-transport]').forEach(b => b.classList.toggle('active', b.getAttribute('data-if-transport') === S.transport));
    // SPLIT pills -> splitMode.
    document.querySelectorAll('[data-if-split]').forEach(b => b.classList.toggle('active', b.getAttribute('data-if-split') === S.splitMode));
    // DIRECTION pills -> invert (host snapshot wins over the optimistic local).
    const invert = (S.monitorSnapshot && S.monitorSnapshot.invertManualRouting === true) || _invertManual === true;
    document.querySelectorAll('[data-if-dir]').forEach(b => b.classList.toggle('active', (b.getAttribute('data-if-dir') === 'blacklist') === invert));
    // AUTO RECONNECT switch.
    const ar = $('ifAutoReconnect');
    if (ar) { ar.classList.toggle('on', S.autoReconnect === true); ar.setAttribute('aria-checked', String(S.autoReconnect === true)); }
    // SYSTEM PROXY toggle + mode.
    const pt = $('ifProxyToggle');
    if (pt) { pt.textContent = isProxyOn(S.systemProxyMode) ? ifT('proxy.on') : ifT('proxy.off'); pt.classList.toggle('on', isProxyOn(S.systemProxyMode)); }
    const pm = $('ifProxyMode');
    if (pm) pm.value = String(Number(S.systemProxyMode) || 0);
    const pp = $('ifProtocol');
    if (pp) pp.value = S.protocolPreference || 'auto';
    // Extended settings (dashboard parity): TUN stack, effects tier, auto-game
    // connect, GPN recovery watch / failover and the system-proxy test button.
    const ts = $('ifTunStack');
    if (ts) ts.value = S.tunStack || 'mixed';
    const ef = $('ifEffects');
    if (ef) ef.value = S.effectsTier || 'full';
    const autoGame = $('ifAutoGame');
    if (autoGame) {
      const on = !!(S.monitorSnapshot && S.monitorSnapshot.autoConnectOnGameStart === true);
      autoGame.classList.toggle('on', on); autoGame.setAttribute('aria-checked', String(on));
    }
    const rec = $('ifRecovery');
    if (rec) {
      const on = S.gpnRecoveryWatch === true;
      rec.classList.toggle('on', on); rec.setAttribute('aria-checked', String(on));
    }
    const fo = $('ifFailover');
    if (fo) {
      const on = S.gpnFailover === true;
      fo.classList.toggle('on', on); fo.setAttribute('aria-checked', String(on));
    }
    const ptest = $('ifProxyTest');
    if (ptest) {
      const r = S.proxyTestResult;
      const ok = !!(r && r.ok === true);
      const bad = !!(r && r.ok === false);
      ptest.classList.toggle('ok', ok); ptest.classList.toggle('fail', bad); ptest.classList.toggle('running', false);
      ptest.textContent = ok ? (Number.isFinite(r.ms) ? r.ms + ' ms' : ifT('test.ok')) : bad ? ifT('test.fail') : ifT('test.idle');
    }
  }

  // ---- console tabs (dashboard module parity) ----
  // The bottom terminal becomes a tabbed console: TERMINAL keeps the live log,
  // NODES lists route nodes (click to select), GPN shows the managed servers
  // with live probes, MONITOR mirrors the dashboard Connection Monitor and
  // ABOUT shows program info — same module set as CYBER/NEXUS.
  let _ifTab = 'terminal';
  let _ifMonFilter = '';
  let _ifMonHideListeners = true;
  let _ifMonGroup = 'none';
  const _ifMonCollapsed = new Set();
  try {
    const saved = JSON.parse(localStorage.getItem('aogpn.infraMon.v1') || '{}');
    if (typeof saved.hideListeners === 'boolean') _ifMonHideListeners = saved.hideListeners;
    if (['none', 'route', 'protocol', 'state', 'country', 'app'].includes(saved.group)) _ifMonGroup = saved.group;
  } catch (e) { /* no localStorage */ }
  function _ifMonPersist() {
    try { localStorage.setItem('aogpn.infraMon.v1', JSON.stringify({ hideListeners: _ifMonHideListeners, group: _ifMonGroup })); } catch (e) { /* ignore */ }
  }
  const IF_MON_GROUP_KEYS = [
    ['none', 'monitor.view.groupNone'], ['route', 'monitor.view.groupRoute'], ['protocol', 'monitor.view.groupProtocol'],
    ['state', 'monitor.view.groupState'], ['country', 'monitor.view.groupCountry'], ['app', 'monitor.view.groupApp']
  ];

  function ifSwitchTab(name) {
    _ifTab = name;
    document.querySelectorAll('[data-if-tab]').forEach(b => b.classList.toggle('active', b.getAttribute('data-if-tab') === name));
    document.querySelectorAll('[data-if-panel]').forEach(p => {
      const on = p.getAttribute('data-if-panel') === name;
      p.hidden = !on;
      p.classList.toggle('active', on);
    });
    ifRenderTab();
  }

  function ifRenderTab() {
    const S = state();
    if (_ifTab === 'nodes') ifRenderTabNodes(S);
    else if (_ifTab === 'gpn') ifRenderTabGpn(S);
    else if (_ifTab === 'monitor') ifRenderTabMonitor(S);
    else if (_ifTab === 'about') ifRenderTabAbout(S);
  }

  function ifRenderTabNodes(S) {
    const root = $('ifTabNodes');
    if (!root) return;
    const nodes = S.nodes || [];
    if (nodes.length === 0) {
      root.innerHTML = '<div class="if-tabHead"><span>' + esc(ifT('tabs.nodes')) + '</span></div><div class="if-tabMsg">' + esc(ifT('noNodes')) + '</div>';
      return;
    }
    const act = activeNode(S);
    const actKey = act ? String(nodeKey(act, S.useRealNodes)) : '';
    root.innerHTML =
      '<div class="if-tabHead"><span>' + esc(ifT('tabs.nodes')) + '</span><em>' + nodes.length + '</em></div>'
      + nodes.map(n => {
        const key = String(nodeKey(n, S.useRealNodes));
        const active = key === actKey;
        return '<div class="if-node-row' + (active ? ' active' : '') + '" data-if-tabnode="' + esc(key) + '">'
          + '<code>' + esc(nodeCode(n, S.useRealNodes)) + '</code>'
          + '<b>' + esc(nodeLabel(n, S.useRealNodes)) + '</b>'
          + '<em>' + esc(nodeMs(n, S.useRealNodes)) + '</em>'
          + '</div>';
      }).join('');
    root.querySelectorAll('[data-if-tabnode]').forEach(row => {
      row.addEventListener('click', () => {
        const key = row.getAttribute('data-if-tabnode');
        const S2 = state();
        const n = S2.nodes.find(node => String(nodeKey(node, S2.useRealNodes)) === key);
        const name = n ? String(nodeLabel(n, S2.useRealNodes)).toUpperCase() : key;
        switchNode(key);
        logLine(ifT('log.node', { node: name }), 'sys');
      });
    });
  }

  function ifRenderTabGpn(S) {
    const root = $('ifTabGpn');
    if (!root) return;
    const servers = S.gpnServers || [];
    const probes = {};
    (S.gpnServerProbes || []).forEach(p => { probes[p.serverId] = p; });
    const rows = servers.map(s => {
      const probe = probes[s.serverId];
      const badge = probe && s.isEnabled
        ? (probe.isSuccess
          ? '<span class="if-gpnProbe ok">' + (Number.isFinite(probe.delayMs) && probe.delayMs >= 0 ? Math.round(probe.delayMs) + ' ms' : '—') + ' · ' + (Number.isFinite(probe.lossPercent) ? probe.lossPercent + '%' : '—') + ' · ' + esc(String(probe.udpStatus || '?').toUpperCase()) + '</span>'
          : '<span class="if-gpnProbe fail">✕</span>')
        : '<span class="if-gpnProbe">?</span>';
      const active = S.activeGpnServer === s.serverId;
      return '<div class="if-gpnRow' + (active ? ' active' : '') + '">'
        + '<div class="if-gpnMain"><b>' + esc(s.serverName || s.serverId || '—') + '</b>' + badge + '</div>'
        + '<div class="if-gpnActs">'
        + '<button type="button" class="if-gpnBtn' + (s.isEnabled ? ' on' : '') + '" data-if-gpn-toggle="' + esc(s.serverId) + '">' + (s.isEnabled ? esc(ifT('gpn.servers.disable')) : esc(ifT('gpn.servers.enable'))) + '</button>'
        + (active ? '<span class="if-gpnLive">● ' + esc(ifT('gpn.servers.active')) + '</span>' : '')
        + '</div></div>';
    }).join('');
    // ---- failover telemetry + resilience log (dashboard parity) ----
    // Same host contract as the dashboard GPN section: five live counters from
    // skinBridge.gpnTelemetry (reset via reset_gpn_telemetry) and the last-50
    // decisions ring buffer from skinBridge.gpnResilienceLog (refresh/clear via
    // get_gpn_resilience_log / clear_gpn_resilience_log).
    const snap = S.gpnTelemetry || {};
    const num = (v) => String(Number.isFinite(v) ? v : 0);
    const telItem = (label, cls, v) => '<span class="if-gpnTelItem ' + cls + '"><b>' + num(v) + '</b><i>' + esc(label) + '</i></span>';
    const rlog = S.gpnResilienceLog || {};
    const entries = (rlog && Array.isArray(rlog.entries)) ? rlog.entries : [];
    const evMeta = (action) => ({
      ServerSwitch: ['ok', 'gpn.ev.switch'],
      UdpDeath: ['bad', 'gpn.ev.udpDeath'],
      ModeFallback: ['warn', 'gpn.ev.fallback'],
      Recover: ['ok', 'gpn.ev.recover'],
      ModeDecision: ['sel', 'gpn.ev.select']
    }[action] || ['dim', null]);
    const resBody = entries.length
      ? entries.map(e => {
          const [cls, key] = evMeta(e && e.action);
          const time = (e && Number.isFinite(e.timestampMs)) ? new Date(e.timestampMs).toLocaleTimeString() : '';
          const label = key ? ifT(key) : (e && e.action) || '';
          const mode = e && e.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WireGuard';
          const target = e && (e.targetServerName || e.targetServerId || '');
          const parts = [e && (e.serverName || e.serverId || ''), (target ? '→ ' + target : ''), e && e.reason ? e.reason : ''].filter(Boolean).join('  ·  ');
          return '<div class="if-ev ' + cls + '"><i class="if-evDot"></i><div class="if-evMain"><div class="if-evLine"><span class="if-evBadge">' + esc(label) + '</span><em class="dim">' + esc(mode) + '</em><span class="if-evTime">' + esc(time) + '</span></div>'
            + (parts ? '<p class="if-evDetail">' + esc(parts) + '</p>' : '') + '</div></div>';
        }).join('')
      : '<div class="if-tabMsg">' + esc(ifT('gpn.resEmpty')) + '</div>';
    // One innerHTML pass — appending after wiring would destroy the listeners.
    root.innerHTML =
      '<div class="if-tabHead"><span>' + esc(ifT('gpn.servers')) + '</span><em>' + servers.length + '</em></div>'
      + (servers.length === 0
        ? '<div class="if-tabMsg">' + esc(ifT('gpn.none')) + '<br><small>' + esc(ifT('gpn.noneSub')) + '</small></div>'
        : rows)
      + '<div class="if-gpnFooter">'
      + '<button type="button" class="if-gpnBtn" data-if-gpn-measure>⟳ ' + esc(ifT('gpn.measure')) + '</button>'
      + '<button type="button" class="if-gpnBtn primary" data-if-gpn-connect>⚡ ' + esc(ifT('gpn.jackin')) + '</button>'
      + '</div>'
      + '<div class="if-gpnTel">'
      + '<div class="if-tabHead"><span>' + esc(ifT('gpn.telTitle')) + '</span><em>' + num(snap.totalEvents) + '</em>'
      + '<button type="button" class="if-gpnBtn ml-auto" data-if-telreset>' + esc(ifT('gpn.telemetry.reset')) + '</button></div>'
      + '<div class="if-gpnTelWrap">'
      + telItem(ifT('gpn.telemetry.switchShort'), 'ok', snap.serverSwitches)
      + telItem(ifT('gpn.telemetry.deathShort'), 'bad', snap.udpDeaths)
      + telItem(ifT('gpn.telemetry.fallbackShort'), 'warn', snap.modeFallbacks)
      + telItem(ifT('gpn.telemetry.recoverShort'), 'ok', snap.recoveries)
      + telItem(ifT('gpn.telemetry.selectShort'), 'sel', snap.modeDecisions)
      + '</div></div>'
      + '<div class="if-gpnRes">'
      + '<div class="if-tabHead"><span>' + esc(ifT('gpn.resTitle')) + '</span><em>' + entries.length + '</em>'
      + '<span class="if-gpnResActs"><button type="button" class="if-gpnBtn" data-if-resrefresh>⟳ ' + esc(ifT('gpn.resRefresh')) + '</button>'
      + '<button type="button" class="if-gpnBtn" data-if-resclear>' + esc(ifT('gpn.resClear')) + '</button></span></div>'
      + '<div class="if-resFeed">' + resBody + '</div>'
      + (rlog && rlog.path ? '<p class="if-resPath"><span>' + esc(ifT('gpn.resPath')) + '</span> <b>' + esc(rlog.path) + '</b></p>' : '')
      + '</div>';
    root.querySelector('[data-if-gpn-measure]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'gpn_servers_probe' });
      logLine(ifT('gpn.measure').toUpperCase() + ' → ' + ifT('gpn.servers'), 'sys');
    });
    root.querySelector('[data-if-gpn-connect]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'gpn_connect' });
      logLine(ifT('gpn.jackin') + ' → GPN', 'ok');
    });
    root.querySelectorAll('[data-if-gpn-toggle]').forEach(btn => {
      btn.addEventListener('click', () => {
        if (B) B.postToHost({ action: 'gpn_server_toggle', serverId: btn.getAttribute('data-if-gpn-toggle'), enabled: !btn.classList.contains('on') });
      });
    });
    root.querySelector('[data-if-telreset]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'reset_gpn_telemetry' });
      logLine(ifT('gpn.telTitle') + ' → ' + ifT('gpn.telemetry.reset').toUpperCase(), 'sys');
    });
    root.querySelector('[data-if-resrefresh]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'get_gpn_resilience_log' });
      logLine(ifT('gpn.resTitle') + ' → ' + ifT('gpn.resRefresh').toUpperCase(), 'sys');
    });
    root.querySelector('[data-if-resclear]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'clear_gpn_resilience_log' });
      logLine(ifT('gpn.resTitle') + ' → ' + ifT('gpn.resClear').toUpperCase(), 'sys');
    });
  }

  function ifRenderTabMonitor(S) {
    const root = $('ifTabMonitor');
    if (!root) return;
    const snap = S.monitorSnapshot || {};
    const conns = snap.connections || [];
    const filter = _ifMonFilter.trim().toLowerCase();
    const rows = conns.filter(it => {
      if (_ifMonHideListeners && it.protocol === 'TCP' && it.state === 'Listen') return false;
      if (!filter) return true;
      return [it.displayName, it.processName, it.remoteAddress, it.countryText, it.asnText, it.protocol, it.state]
        .filter(Boolean).join(' ').toLowerCase().includes(filter);
    });
    // One row div; the hidden class is applied when its group is collapsed.
    const monRow = (it, hidden) => {
      const route = it.routeTag || '';
      const label = it.routeText || (S.routeLabels && S.routeLabels[route]) || route || '—';
      const cls = ['proxy', 'direct', 'block', 'warp'].includes(route) ? route : 'unknown';
      const pname = it.processName || '';
      const display = it.displayName || pname || 'Unknown';
      const opts = [['', ifT('route.assign')], ['vpn', ifT('route.vpn')], ['direct', ifT('route.direct')], ['block', ifT('route.block')], ['warp', ifT('route.warp')]]
        .map(o => '<option value="' + o[0] + '"' + (o[0] === (it.action || '') ? ' selected' : '') + '>' + esc(o[1]) + '</option>').join('');
      const country = [it.countryText, it.asnText].filter(Boolean).join(' · ') || '—';
      return '<div class="if-monRow' + (hidden ? ' hidden' : '') + '">'
        + '<div class="if-monProg"><b>' + esc(display) + '</b><span>' + esc(pname) + (it.pid ? ' · ' + esc(String(it.pid)) : '') + '</span></div>'
        + '<span class="if-routeBadge ' + cls + '">' + esc(label) + '</span>'
        + '<span>' + esc(it.protocol || '—') + '</span>'
        + '<span class="addr" title="' + esc(it.remoteAddress || '') + '">' + esc(it.remoteAddress || '—') + '</span>'
        + '<span class="addr" title="' + esc(country) + '">' + esc(country) + '</span>'
        + '<span>' + esc(it.state || '—') + '</span>'
        + '<select class="if-routeSel" data-if-monroute="' + esc(pname) + '" data-if-mondisplay="' + esc(display) + '">' + opts + '</select>'
        + '</div>';
    };
    const groupHead = (k, count) => {
      const collapsed = _ifMonCollapsed.has(k);
      return '<div class="if-mgroup' + (collapsed ? ' collapsed' : '') + '" data-if-mongroup="' + esc(k) + '" role="button" tabindex="0" aria-expanded="' + !collapsed + '"><span class="if-mchev">' + (collapsed ? '▸' : '▾') + '</span><b>' + esc(k) + '</b><em>' + count + '</em></div>';
    };
    // Grouping mirrors NEXUS/dashboard: route / protocol / state / country / app
    // with collapsible headers; groups are built from the FILTERED rows.
    const groupKey = (it) => {
      if (_ifMonGroup === 'route') return it.routeText || it.routeTag || '—';
      if (_ifMonGroup === 'protocol') return it.protocol || '—';
      if (_ifMonGroup === 'state') return it.state || '—';
      if (_ifMonGroup === 'country') return [it.countryText, it.asnText].filter(Boolean).join(' · ') || '—';
      return it.displayName || it.processName || 'Unknown';
    };
    const groupOpts = IF_MON_GROUP_KEYS.map(o => '<option value="' + o[0] + '"' + (o[0] === _ifMonGroup ? ' selected' : '') + '>' + esc(ifT(o[1])) + '</option>').join('');
    const headRow = '<div class="if-monHead"><span>' + esc(ifT('node.colProgram')) + '</span><span>ROUTE</span><span>PROTOCOL</span><span>ADDRESS</span><span>COUNTRY</span><span>STATE</span><span></span></div>';
    let body;
    if (rows.length === 0) {
      body = '<div class="if-tabMsg">' + esc(filter ? ifT('mon.noMatch') : ifT('mon.none')) + '</div>';
    } else if (_ifMonGroup === 'none') {
      body = headRow + rows.map(it => monRow(it, false)).join('');
    } else {
      const order = [];
      const grouped = new Map();
      rows.forEach(it => {
        const k = groupKey(it);
        if (!grouped.has(k)) { order.push(k); grouped.set(k, []); }
        grouped.get(k).push(it);
      });
      body = headRow + order.map(k => {
        const list = grouped.get(k);
        const collapsed = _ifMonCollapsed.has(k);
        return groupHead(k, list.length) + list.map(it => monRow(it, collapsed)).join('');
      }).join('');
    }
    root.innerHTML =
      '<div class="if-tabHead"><span>' + esc(ifT('monitor.title')) + '</span><em>' + rows.length + ' / ' + conns.length + '</em></div>'
      + '<div class="if-monTools">'
      + '<input type="search" class="if-monFilter" data-if-monfilter placeholder="' + esc(ifT('monitor.filter')) + '" value="' + esc(_ifMonFilter) + '">'
      + '<label class="if-monHide"><input type="checkbox" data-if-monhide' + (_ifMonHideListeners ? ' checked' : '') + '> ' + esc(ifT('monitor.hideListeners')) + '</label>'
      + '<select class="if-routeSel if-monGroup" data-if-mongroup-sel aria-label="' + esc(ifT('monitor.view.groupBy')) + '">' + groupOpts + '</select>'
      + '</div>'
      + body;
    const f = root.querySelector('[data-if-monfilter]');
    if (f) f.addEventListener('input', () => { _ifMonFilter = f.value; ifRenderTabMonitor(state()); });
    const hide = root.querySelector('[data-if-monhide]');
    if (hide) hide.addEventListener('change', () => { _ifMonHideListeners = hide.checked; _ifMonPersist(); ifRenderTabMonitor(state()); });
    const gsel = root.querySelector('[data-if-mongroup-sel]');
    if (gsel) gsel.addEventListener('change', () => { _ifMonGroup = gsel.value; _ifMonPersist(); ifRenderTabMonitor(state()); });
    root.querySelectorAll('[data-if-mongroup]').forEach(h => {
      const toggle = () => {
        const k = h.getAttribute('data-if-mongroup');
        if (_ifMonCollapsed.has(k)) _ifMonCollapsed.delete(k); else _ifMonCollapsed.add(k);
        ifRenderTabMonitor(state());
      };
      h.addEventListener('click', toggle);
      h.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
    });
    root.querySelectorAll('[data-if-monroute]').forEach(sel => {
      sel.addEventListener('change', () => {
        if (!sel.value) return;
        if (B) B.postToHost({ action: 'set_app_route', processName: sel.getAttribute('data-if-monroute'), displayName: sel.getAttribute('data-if-mondisplay'), route: sel.value });
        logLine(ifT('log.routeSet', { route: sel.value.toUpperCase(), app: sel.getAttribute('data-if-mondisplay').toUpperCase() }), 'sys');
      });
    });
  }

  function ifRenderTabAbout(S) {
    const root = $('ifTabAbout');
    if (!root) return;
    const info = S.appInfo || {};
    root.innerHTML =
      '<div class="if-tabHead"><span>' + esc(ifT('about.title')) + '</span></div>'
      + '<div class="if-aboutLines">'
      + '<div class="if-console-line sys">> INFRA SHELL ACTIVE.</div>'
      + '<div class="if-console-line">> ' + esc(ifT('about.version')) + ': ' + esc(info.version || '—') + '</div>'
      + '<div class="if-console-line">> ' + esc(ifT('about.skin')) + ': INFRA TERMINAL</div>'
      + '<div class="if-console-line">> ' + esc(ifT('about.session')) + ': ' + esc(S.sessionTime || '00:00:00') + '</div>'
      + '<div class="if-console-line">> ' + esc(ifT('node')) + ': ' + esc((activeNode(S) || {}).name || '—') + '</div>'
      + '<div class="if-console-line ok">> ' + esc(ifT('about.hint')) + '</div>'
      + '<div class="if-console-line ok">> ' + esc(ifT('about.hint2')) + '</div>'
      + '</div>';
  }

  function logLine(text, cls) {
    const consoleEl = $('ifConsole');
    if (!consoleEl) return;
    const line = document.createElement('div');
    line.className = 'if-console-line' + (cls ? ' ' + cls : '');
    line.textContent = '> ' + text;
    consoleEl.appendChild(line);
    consoleEl.scrollTop = consoleEl.scrollHeight;
    while (consoleEl.children.length > 40) consoleEl.removeChild(consoleEl.firstChild);
  }

  function render() {
    // Static labels carry translations too; re-apply first so a language
    // switch (via B.setLanguage) updates them on this very pass.
    applyStaticTexts();
    const S = state();
    document.body.classList.toggle('is-on', S.connected);
    document.body.classList.toggle('is-connecting', S.connecting && !S.connected);

    const main = $('ifConnectMain');
    const sub = $('ifConnectSub');
    const status = $('ifStatus');
    if (main) main.textContent = S.connected ? ifT('disconnect') : S.connecting ? ifT('connecting') : ifT('connect');
    if (sub) sub.textContent = (S.mode === 'vpn' ? ifT('sub.globalVpn') : ifT('sub.gpn')) + ' · ' + (S.transport === 'tun' ? ifT('sub.tun') : ifT('sub.proxy'));
    if (status) status.textContent = S.connected ? ifT('status.online') : S.connecting ? ifT('status.connecting') : ifT('status.offline');

    const node = $('ifNodeLabel');
    const act = activeNode(S);
    if (node) node.textContent = ifT('node') + ': ' + (act ? esc(act.name || act.address || act.key) : '—');

    const session = $('ifSession');
    if (session) session.textContent = S.sessionTime || '00:00:00';
    const ip = $('ifExitIp');
    if (ip) ip.textContent = (S.connected && S.exitIp && S.exitIp !== 'Not verified') ? S.exitIp : '—';
    const lang = $('ifLang');
    if (lang) lang.value = S.language || 'en';

    renderApps();
    renderNodes();
    renderTelemetry();
    renderControls();
    ifRenderTab();
    const sessEl = $('ifSessionConsole');
    if (sessEl) sessEl.textContent = S.sessionTime || '00:00:00';
  }

  // ---- Host push handler: the authoritative snapshot wins once it echoes a
  // route override (mirrors CYBER/NEXUS so the skin never drifts from the
  // dashboard's route selector). ----
  function onHostPush() {
    const S = state();
    const apps = S.monitorSnapshot.apps || [];
    const now = Date.now();
    Object.keys(_localRoutes).forEach(pname => {
      const app = apps.find(a => (a.processName || a.value) === pname);
      if (!app) { delete _localRoutes[pname]; delete _localRouteTs[pname]; return; }
      const pendingMs = now - (_localRouteTs[pname] || 0);
      if (app.action === _localRoutes[pname] || pendingMs > ROUTE_CONFIRM_MS) {
        delete _localRoutes[pname];
        delete _localRouteTs[pname];
      }
    });
    render();
  }

  // ---- wiring ----
  function wire() {
    const connectBtn = $('ifConnect');
    if (connectBtn) {
      connectBtn.addEventListener('click', () => {
        const S = state();
        if (S.connecting) return;
        const ok = B && typeof B.postToHost === 'function'
          // `connected` = bastığı anda ekranda görünen durum → host yönü bu
          // niyetten türetir (bkz. features/gpn.js).
          ? B.postToHost({ action: 'toggle_connection', mode: S.mode, transport: S.transport, protocol: S.protocolPreference, connected: S.connected === true })
          : false;
        if (!ok && B && typeof B.setConnected === 'function') B.setConnected(!S.connected, false);
        if (!B) { DEMO.connected = !DEMO.connected; logLine(DEMO.connected ? ifT('console.tunnelUp') : ifT('console.tunnelDown'), DEMO.connected ? 'ok' : 'sys'); }
        else logLine(ifT('log.toggle', { state: (S.connected ? ifT('disconnect') : ifT('connect')) }), 'sys');
        render();
      });
    }

    const win = (cmd) => { if (B && typeof B.postToHost === 'function') B.postToHost({ action: 'app_control', command: cmd }); };
    const stdBtn = $('ifToStandardBtn'); if (stdBtn) stdBtn.addEventListener('click', () => { if (B && typeof B.applySkin === 'function') B.applySkin('standard'); });
    const minBtn = $('ifMinBtn'); if (minBtn) minBtn.addEventListener('click', () => win('minimize'));
    const maxBtn = $('ifMaxBtn'); if (maxBtn) maxBtn.addEventListener('click', () => win('maximize_toggle'));
    const closeBtn = $('ifCloseBtn'); if (closeBtn) closeBtn.addEventListener('click', () => win('close'));
    const drag = $('ifDragRegion');
    if (drag) {
      let last = 0;
      drag.addEventListener('pointerdown', (e) => {
        if (e.button) return; e.preventDefault();
        const now = Date.now(), dc = now - last < 400; last = now;
        win(dc ? 'maximize_toggle' : 'drag');
      });
    }

    // MODE -> set_connection_mode
    document.querySelectorAll('[data-if-mode]').forEach(b => b.addEventListener('click', () => {
      const next = b.getAttribute('data-if-mode');
      SET('mode', next);
      if (B) B.postToHost({ action: 'set_connection_mode', mode: next });
      logLine(ifT('log.mode', { mode: ifT(next === 'vpn' ? 'mode.globalVpn' : 'mode.gpn').toUpperCase() }), 'sys');
      render();
    }));
    // CAPTURE -> set_transport
    document.querySelectorAll('[data-if-transport]').forEach(b => b.addEventListener('click', () => {
      const next = b.getAttribute('data-if-transport');
      SET('transport', next);
      if (B) B.postToHost({ action: 'set_transport', transport: next });
      logLine(ifT('log.transport', { transport: ifT(next === 'tun' ? 'transport.tun' : 'transport.proxy').toUpperCase() }), 'sys');
      render();
    }));
    // SPLIT -> set_split_mode (the kill switch / split tunneling)
    document.querySelectorAll('[data-if-split]').forEach(b => b.addEventListener('click', () => {
      const next = b.getAttribute('data-if-split');
      SET('splitMode', next);
      if (B) B.postToHost({ action: 'set_split_mode', mode: next });
      logLine(ifT('log.split', { label: ifT('split.' + next).toUpperCase() }), 'sys');
      render();
    }));
    // DIRECTION -> set_split_direction (invert string)
    document.querySelectorAll('[data-if-dir]').forEach(b => b.addEventListener('click', () => {
      const next = b.getAttribute('data-if-dir') === 'blacklist';
      _invertManual = next;
      if (B) B.postToHost({ action: 'set_split_direction', invert: String(next) });
      logLine(ifT('log.dir', { dir: ifT(next ? 'dir.blacklist' : 'dir.whitelist').toUpperCase() }), 'sys');
      render();
    }));
    // AUTO RECONNECT -> set_auto_reconnect
    const ar = $('ifAutoReconnect');
    if (ar) ar.addEventListener('click', () => {
      const next = !(G('autoReconnect') === true);
      SET('autoReconnect', next);
      if (B) B.postToHost({ action: 'set_auto_reconnect', enabled: next });
      logLine(ifT('log.autoReconnect', { state: ifT(next ? 'log.on' : 'log.off') }), 'sys');
      render();
    });
    ar.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); ar.click(); } });

    // SYSTEM PROXY toggle -> toggle_system_proxy
    const pt = $('ifProxyToggle');
    if (pt) pt.addEventListener('click', () => {
      const cur = G('systemProxyMode');
      const next = isProxyOn(cur) ? 0 : 1;
      if (B) B.postToHost({ action: 'toggle_system_proxy' });
      SET('systemProxyMode', next);
      logLine(ifT('log.systemProxy', { state: ifT(isProxyOn(next) ? 'log.on' : 'log.off') }), 'sys');
      render();
    });
    // PROXY MODE -> set_system_proxy_mode
    const pm = $('ifProxyMode');
    if (pm) pm.addEventListener('change', () => {
      const next = Math.max(0, Math.min(3, Number(pm.value) || 0));
      if (B) B.postToHost({ action: 'set_system_proxy_mode', mode: next });
      SET('systemProxyMode', next);
      logLine(ifT('log.proxyMode', { mode: String(next) }), 'sys');
      render();
    });
    // PROTOCOL -> set_protocol_preference
    const pp = $('ifProtocol');
    if (pp) pp.addEventListener('change', () => {
      const next = String(pp.value || 'auto');
      if (B) B.postToHost({ action: 'set_protocol_preference', protocol: next });
      SET('protocolPreference', next);
      logLine(ifT('log.protocol', { protocol: String(next).toUpperCase() }), 'sys');
      render();
    });

    const langSel = $('ifLang');
    if (langSel) {
      langSel.innerHTML = ifOpts(ifLangs(), G('language') || 'en');
      langSel.addEventListener('change', () => {
        const next = String(langSel.value || 'en');
        // Same channel as the top-bar picker / CYBER / NEXUS settings: switch the
        // whole app instantly and persist through the host (set_language).
        if (B && typeof B.setLanguage === 'function') B.setLanguage(next);
        if (B) { try { B.language = next; } catch (e) { /* read-only */ } } else DEMO.language = next;
        render();
        logLine(ifT('log.language', { lang: String(next).toUpperCase() }), 'sys');
      });
    }

    // ---- console tabs ----
    document.querySelectorAll('[data-if-tab]').forEach(b => b.addEventListener('click', () => ifSwitchTab(b.getAttribute('data-if-tab'))));

    // ---- extended settings (dashboard parity) ----
    const tunStack = $('ifTunStack');
    if (tunStack) tunStack.addEventListener('change', () => {
      const next = String(tunStack.value || 'mixed');
      if (B) B.postToHost({ action: 'set_tun_stack', stack: next });
      SET('tunStack', next);
      logLine(ifT('log.tunStack', { stack: String(next).toUpperCase() }), 'sys');
      render();
    });
    const effects = $('ifEffects');
    if (effects) effects.addEventListener('change', () => {
      const next = String(effects.value || 'full');
      if (B) B.postToHost({ action: 'set_effects_tier', tier: next });
      SET('effectsTier', next);
      logLine(ifT('log.effects', { effects: String(next).toUpperCase() }), 'sys');
      render();
    });
    const autoGame = $('ifAutoGame');
    if (autoGame) autoGame.addEventListener('click', () => {
      const next = !(autoGame.classList.contains('on'));
      autoGame.classList.toggle('on', next); autoGame.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_auto_game_connect', enabled: next });
      logLine(ifT('log.autoGame', { state: ifT(next ? 'log.on' : 'log.off') }), 'sys');
    });
    const recovery = $('ifRecovery');
    if (recovery) recovery.addEventListener('click', () => {
      const next = !(recovery.classList.contains('on'));
      recovery.classList.toggle('on', next); recovery.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_gpn_recovery_watch', enabled: next });
      logLine(ifT('log.recovery', { state: ifT(next ? 'log.on' : 'log.off') }), 'sys');
    });
    const failover = $('ifFailover');
    if (failover) failover.addEventListener('click', () => {
      const next = !(failover.classList.contains('on'));
      failover.classList.toggle('on', next); failover.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_gpn_failover', enabled: next });
      logLine(ifT('log.failover', { state: ifT(next ? 'log.on' : 'log.off') }), 'sys');
    });
    const proxyTest = $('ifProxyTest');
    if (proxyTest) proxyTest.addEventListener('click', () => {
      if (proxyTest.classList.contains('running')) return;
      proxyTest.classList.add('running'); proxyTest.classList.remove('ok', 'fail');
      proxyTest.textContent = ifT('test.running');
      if (B) B.postToHost({ action: 'test_proxy' });
      logLine(ifT('log.proxyTest'), 'sys');
      // Safety: if the host never answers, drop back to idle after 6 s.
      setTimeout(() => {
        if (proxyTest.classList.contains('running')) {
          proxyTest.classList.remove('running'); proxyTest.classList.add('fail');
          proxyTest.textContent = ifT('test.fail');
        }
      }, 6000);
    });

    const connectBtn2 = $('ifConnect');
    if (connectBtn2) connectBtn2.setAttribute('aria-label', ifT('connectAria'));
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => { wire(); render(); start(); });
  } else {
    wire(); render(); start();
  }

  // ---- global keyboard shortcuts (shared across all skins) ----
  //   Ctrl+Enter -> CONNECT / disconnect (same toggle as the CONNECT button)
  //   Alt+1..5   -> console tabs (terminal / nodes / gpn / monitor / about)
  //   R          -> cycle the focused boost app's route (vpn/direct/block/warp)
  // The exact same set exists in NEXUS (Alt+1..8 for its views) and CYBER.
  const IF_TAB_KEYS = ['terminal', 'nodes', 'gpn', 'monitor', 'about'];
  let _ifShortcutsBound = false;
  function ifBindShortcuts() {
    if (_ifShortcutsBound) return;
    _ifShortcutsBound = true;
    document.addEventListener('keydown', (e) => {
      // Connect / disconnect — safe in every context, including inputs.
      if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'Enter' || e.key === 'NumpadEnter')) {
        e.preventDefault();
        const connectBtn = $('ifConnect');
        if (connectBtn) connectBtn.click();
        return;
      }
      const typing = e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.target.tagName === 'TEXTAREA' || e.target.isContentEditable);
      // Tab switching.
      if (e.altKey && !e.ctrlKey && !e.metaKey && !typing && /^[1-5]$/.test(e.key)) {
        e.preventDefault();
        ifSwitchTab(IF_TAB_KEYS[Number(e.key) - 1]);
        return;
      }
      // Route cycle on the focused boost app row (row or its switch).
      if (!typing && !e.altKey && !e.ctrlKey && !e.metaKey && (e.key === 'r' || e.key === 'R')) {
        const row = document.activeElement && document.activeElement.closest
          ? document.activeElement.closest('[data-if-pname]')
          : null;
        if (row && row.getAttribute && row.getAttribute('data-if-pname')) {
          const pname = row.getAttribute('data-if-pname');
          const S = state();
          const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === pname);
          const cur = actionFor(app || { processName: pname });
          const next = ifRouteCycle(cur);
          e.preventDefault();
          setAppRoute(pname, next);
        }
      }
    });
  }

  function start() {
    ifBindShortcuts();
    if (B && typeof B.subscribe === 'function') B.subscribe(onHostPush);
    if (!PREVIEW) setInterval(() => { if (document.visibilityState !== 'hidden') render(); }, 1000); // session-clock tick
  }
})();