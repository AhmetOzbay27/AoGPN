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
    'mode.globalVpn': 'GLOBAL VPN',
    'transport.proxy': 'PROXY',
    'transport.tun': 'TUN',
    'split.off': 'OFF',
    'split.global': 'GLOBAL',
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
    'stdTitle': 'Standard dashboard'
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
      routeLabels: G('routeLabels') || {}, language: G('language') || 'en'
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
          ? B.postToHost({ action: 'toggle_connection', mode: S.mode, transport: S.transport, protocol: S.protocolPreference })
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

    const connectBtn2 = $('ifConnect');
    if (connectBtn2) connectBtn2.setAttribute('aria-label', ifT('connectAria'));
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => { wire(); render(); start(); });
  } else {
    wire(); render(); start();
  }

  function start() {
    if (B && typeof B.subscribe === 'function') B.subscribe(onHostPush);
    if (!PREVIEW) setInterval(() => { if (document.visibilityState !== 'hidden') render(); }, 1000); // session-clock tick
  }
})();