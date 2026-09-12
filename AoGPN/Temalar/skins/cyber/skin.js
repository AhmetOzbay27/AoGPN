// ===== AoGPN CYBER skin — standalone netrunner console =====
// Same isolation contract as the other skins: lives fully inside its own
// iframe, reads live app state and fires host actions only through the
// parent's skinBridge. Left "source" list = real boost-tunneled apps (from
// monitorSnapshot), right "destination" list = real route nodes, the JACK IN
// button maps to toggle_connection, telemetry is mirrored from the bridge.
(function () {
  'use strict';

  const B = (window.parent && window.parent !== window && window.parent.skinBridge) ? window.parent.skinBridge : null;
  const CY_PREVIEW = /[?&]preview=1/.test(window.location.search || '');
  let _wired = false;
  let _src = 'src0';
  let _dst = 'dst0';
  // Smoothed traffic intensity (0..1) that drives the trace pulse glow, so
  // the spark doesn't flicker on every raw telemetry sample.
  let _flowSm = 0;

  const DEMO = {
    connected: false, connecting: false, mode: 'gpn', transport: 'proxy', splitMode: 'off',
    systemProxyMode: 0, protocolPreference: 'auto', autoReconnect: true,
    useRealNodes: false, activeRealNodeId: null, selectedNode: null,
    monitorSnapshot: { apps: [] }, routeLabels: {}, language: 'en', sessionTime: '00:00:00',
    telemetry: [null, null, null, null], exitIp: 'Not verified', exitCountry: '',
    nodes: [
      { key: 'istanbul', code: 'TR', name: 'Istanbul · TR-01', addr: '145.239.14.77', ping: 18 },
      { key: 'frankfurt', code: 'DE', name: 'Frankfurt · DE-11', addr: '5.161.91.19', ping: 148 },
      { key: 'amsterdam', code: 'NL', name: 'Amsterdam · NL-02', addr: '51.15.118.35', ping: 171 }
    ]
  };

  function G(name) { if (B) { try { return B[name]; } catch (e) { /* ignore */ } } return DEMO[name]; }
  function SET(name, value) { if (B) { try { B[name] = value; } catch (e) { /* read-only */ } } }
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  // Resolve a UI string through the parent bridge: skin.cyber.* keys in
  // Dil/*.json win, then the shared dictionary (e.g. route.direct), then the
  // skin's own English default, then the raw key. Same contract as NEXUS's
  // nxT, scoped to this skin so translators never touch the main dashboard.
  const CY_TEXT = {
    'route.vpn': 'VPN',
    'route.direct': 'Direct',
    'route.proxy': 'Proxy',
    'route.vpn+proxy': 'VPN + Proxy',
    'route.block': 'Block',
    'brandSub': 'AO GPN',
    'panel.source': 'SMART SPLIT // INJECTION',
    'panel.controls': 'SYSTEM CONTROLS',
    'panel.dest': 'DESTINATION // TELEMETRY',
    'panel.settings': 'SETTINGS',
    'ctl.routeMode': 'ROUTE MODE',
    'ctl.splitDir': 'SPLIT DIRECTION',
    'ctl.connectionMode': 'CONNECTION MODE',
    'ctl.capture': 'CAPTURE',
    'mode.gpn': 'GPN GAME TUNNEL',
    'mode.vpn': 'VPN',
    'transport.proxy': 'PROXY',
    'transport.tun': 'TUN',
    'ctl.autoReconnect': 'AUTO RECONNECT',
    'ctl.systemProxy': 'SYSTEM PROXY',
    'ctl.proxyMode': 'PROXY MODE',
    'ctl.protocol': 'PROTOCOL',
    'ctl.language': 'LANGUAGE',
    'split.off': 'OFF',
    'split.vpn': 'VPN',
    'split.manual': 'GPN GAME TUNNEL',
    'dir.whitelist': 'WHITELIST',
    'dir.blacklist': 'BLACKLIST',
    'proxy.on': 'PROXY ON',
    'proxy.off': 'PROXY OFF',
    'tele.latency': 'LATENCY (PING)',
    'tele.loss': 'PACKET LOSS',
    'tele.throughput': 'THROUGHPUT',
    'tele.session': 'SESSION UPTIME',
    'tele.exitIp': 'EXIT IP',
    'console.root': 'CONSOLE // ROOT ACCESS',
    'console.secure': 'CONSOLE // SECURE COMM-LINK',
    'console.session': 'SESSION',
    'console.bootOk': '> SYSTEM BOOT: OK',
    'console.bootLoaded': '> AO GPN HYBRID PROTOCOL LOADED.',
    'console.bootVuln': '> STATUS: VULNERABLE. SELECT ROUTE AND JACK IN.',
    'console.terminating': '> TERMINATING DATA STREAM...',
    'console.exposed': '> SYSTEM EXPOSED. IP ADDRESS VISIBLE.',
    'console.initiating': '> INITIATING AO GPN CORE...',
    'console.configuring': '> CONFIGURING SOCKS5 / WG TRACES...',
    'console.injecting': '> INJECTING PACKETS INTO NETWORK...',
    'console.granted': '> ACCESS GRANTED. ELECTRIC TRACES SECURED.',
    'console.restored': '> SESSION RESTORED: SECURE COMM-LINK ACTIVE.',
    'state.idle': 'JACK IN',
    'state.connected': 'LINKED',
    'state.breaching': 'BREACHING',
    'sub.idle': 'SYSTEM OFFLINE',
    'sub.connected': 'SECURE TUNNEL ACTIVE',
    'sub.breaching': 'BYPASSING FIREWALL...',
    'running': 'RUNNING',
    'node': 'NODE',
    'connectAria': 'Connect',
    'arTitle': 'Auto-reconnect on connection drop',
    'switchTitle': 'Toggle routing (Enter/Space or ←/→)',
    'empty.title': 'NO INJECTION TARGETS',
    'empty.sub': 'ADD GAMES VIA GAME BOOST // SPLIT TUNNEL',
    'log.routeMode': '> ROUTE MODE: {mode}',
    'log.connectionMode': '> CONNECTION MODE: {mode}',
    'log.transport': '> CAPTURE: {transport}',
    'log.splitDir': '> SPLIT DIRECTION: {dir}',
    'log.autoReconnect': '> AUTO RECONNECT: {state}',
    'log.systemProxy': '> SYSTEM PROXY: {state}',
    'log.proxyMode': '> PROXY MODE: {mode}',
    'log.protocol': '> PROTOCOL: {protocol}',
    'log.language': '> LANGUAGE: {lang}',
    'log.appControl': '> APP CONTROL: {command}',
    'log.theme': '> THEME OVERRIDE: {theme} PROTOCOL ENGAGED.',
    'log.sourceOverride': '> SOURCE OVERRIDE: {name}',
    'log.targetLocked': '> TARGET LOCKED: {name}',
    'log.nodeSync': '> NODE SYNC: {node}',
    'log.routeSet': '> ROUTE {route} FOR {app}',
    'log.tunneled': ' // TUNNELED',
    'log.direct': ' // DIRECT',
    'log.on': 'ON',
    'log.off': 'OFF',
    // Console tabs (bottom terminal).
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
    'log.tunStack': '> TUN STACK: {stack}',
    'log.effects': '> EFFECTS: {effects}',
    'log.autoGame': '> AUTO-GAME CONNECT: {state}',
    'log.recovery': '> RECOVERY WATCH: {state}',
    'log.failover': '> FAILOVER: {state}',
    'log.proxyTest': '> PROXY TEST INITIATED.',
    // Nodes tab.
    'node.select': 'SELECT',
    'node.active': 'ACTIVE',
    'node.none': 'NO ROUTE NODES.',
    'node.noneSub': 'ADD NODES VIA THE DASHBOARD NODES VIEW.',
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
    // Monitor tab (column heads stay brand literals like NEXUS).
    'mon.noMatch': 'NO CONNECTIONS MATCH THE FILTER.',
    'mon.none': 'NO VISIBLE CONNECTIONS.',
    // About tab.
    'about.version': 'VERSION',
    'about.skin': 'SKIN',
    'about.session': 'SESSION',
    'about.hint': 'ALL DASHBOARD MODULES ARE LIVE IN THIS CONSOLE: NODES, GPN SERVERS, CONNECTION MONITOR AND SETTINGS.',
    'about.hint2': 'SWITCH TO THE STANDARD DASHBOARD ANYTIME FOR THE FULL MODULE MATRIX.'
  };
  function cyT(key, params) {
    let val = null;
    if (B && typeof B.t === 'function') {
      try { val = B.t(key, params, 'cyber'); } catch (e) { /* fall through */ }
      if (typeof val === 'string' && val === key) val = null;
    }
    if ((val === null || val === undefined || val === '') && Object.prototype.hasOwnProperty.call(CY_TEXT, key)) {
      val = CY_TEXT[key];
    }
    if (val === null || val === undefined || val === '') return key;
    if (params) {
      Object.keys(params).forEach(k => { val = String(val).split('{' + k + '}').join(String(params[k])); });
    }
    return String(val);
  }

  // Language list: the bridge exposes the SAME LANGUAGES array the top-bar
  // picker uses (with translated names from the host), plus a small standalone
  // fallback so the design preview stays usable without the parent app.
  function cyLangs() {
    const langs = (B && B.LANGUAGES) ? B.LANGUAGES : [
      { code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }, { code: 'fa', name: 'فارسی' },
      { code: 'fr', name: 'Français' }, { code: 'ru', name: 'Русский' }
    ];
    return langs.map(l => [l.code, l.name]);
  }
  function cyOpts(list, current) {
    return list.map(o => {
      const eq = String(current) === String(o[0]);
      return '<option value="' + esc(o[0]) + '"' + (eq ? ' selected' : '') + '>' + esc(o[1]) + '</option>';
    }).join('');
  }

  // Apply [data-cy-i18n] on static markup (panel titles, control labels,
  // pills, telemetry labels, console boot lines) plus [data-cy-i18n-title]
  // tooltips, exactly like the main dashboard's applyTexts walker. Nested
  // elements (icons, badges) are preserved. Called from sync() so a language
  // switch re-applies instantly on the next state push.
  function applyStaticTexts() {
    document.querySelectorAll('[data-cy-i18n]').forEach(el => {
      const key = el.getAttribute('data-cy-i18n');
      const val = cyT(key);
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
    document.querySelectorAll('[data-cy-i18n-title]').forEach(el => {
      const val = cyT(el.getAttribute('data-cy-i18n-title'));
      if (val && val !== el.getAttribute('data-cy-i18n-title')) el.setAttribute('title', val);
    });
  }

  // Route badge label for an action: translated via skin.cyber.route.* (or the
  // shared route.* fallback), then the host's routeLabels, then the action name.
  function routeLabel(action) {
    const a = action || '';
    const tr = cyT('route.' + a);
    if (tr && tr !== 'route.' + a) return tr;
    const S = state();
    return (S.routeLabels || {})[a] || (a ? String(a).toUpperCase() : 'VPN');
  }

  function state() {
    return {
      connected: G('connected'), connecting: G('connecting'), mode: G('mode'), transport: G('transport'),
      splitMode: G('splitMode'), systemProxyMode: G('systemProxyMode'), protocolPreference: G('protocolPreference'),
      autoReconnect: G('autoReconnect'),
      useRealNodes: G('useRealNodes'), activeRealNodeId: G('activeRealNodeId'), selectedNode: G('selectedNode'),
      monitorSnapshot: G('monitorSnapshot') || { apps: [] }, routeLabels: G('routeLabels') || {},
      nodes: G('nodes') || [], telemetry: G('telemetry') || [null, null, null, null],
      sessionTime: G('sessionTime') || '00:00:00', exitIp: G('exitIp') || '—', exitCountry: G('exitCountry') || '',
      language: G('language') || 'en', isAdmin: G('isAdmin'),
      // Extended settings + dashboard modules (skinBridge parity).
      tunStack: G('tunStack') || 'mixed', effectsTier: G('effectsTier') || 'full',
      gpnRecoveryWatch: G('gpnRecoveryWatch'), gpnFailover: G('gpnFailover'),
      proxyTestResult: G('proxyTestResult') || null,
      gpnServers: G('gpnServers') || [], gpnServerProbes: G('gpnServerProbes') || [],
      activeGpnServer: G('activeGpnServer') || '', appInfo: G('appInfo') || null,
      gpnTelemetry: G('gpnTelemetry') || null, gpnResilienceLog: G('gpnResilienceLog') || null
    };
  }

  // A route always has an active endpoint. If the program hasn't pushed a
  // selection yet (fresh/standalone state), fall back to the first node so the
  // destination list and the header badge stay coherent instead of "LINK PENDING".
  function ensureActiveNode(S) {
    if (S.useRealNodes) {
      if (S.activeRealNodeId) return S.nodes.find(n => n.indexId === S.activeRealNodeId) || S.nodes[0] || null;
      return S.nodes[0] || null;
    }
    return S.selectedNode || S.nodes[0] || null;
  }

  function activeKey(S) {
    const active = ensureActiveNode(S);
    return active ? String(nodeKey(active, S.useRealNodes)) : '';
  }

  // ---- terminal log (operations console) ----
  // The console mirrors every meaningful action so the user can watch the whole
  // session from one place: connect/disconnect, route toggles, node switches,
  // theme changes, window controls. A soft cap keeps the log readable (oldest
  // lines drop instead of growing forever).
  const LOG_MAX = 40;
  let _typing = false;
  function logTerm(text, cls) {
    const log = $('cyTermLog');
    if (!log) return;
    while (log.children.length >= LOG_MAX) log.removeChild(log.firstChild);
    const line = document.createElement('div');
    if (cls) line.className = cls;
    log.appendChild(line);
    if (CY_PREVIEW) { line.textContent = text; log.scrollTop = log.scrollHeight; return; }
    let i = 0;
    const iv = setInterval(() => {
      line.textContent += text.charAt(i);
      i++;
      log.scrollTop = log.scrollHeight;
      if (i >= text.length) clearInterval(iv);
    }, 9);
  }

  // ---- theme switcher ----
  function setTheme(name) {
    document.body.setAttribute('data-theme', name === 'purple' ? 'purple' : 'yellow');
    logTerm(cyT('log.theme', { theme: name.toUpperCase() }));
  }

  // ---- node helpers ----
  function nodeKey(n, useReal) { return useReal ? n.indexId : n.key; }
  function nodeLabel(n, useReal) { return useReal ? (n.name || n.address || n.indexId) : (n.name + ' · ' + n.addr); }
  function nodeMs(n, useReal) { return useReal ? (n.delay > 0 ? n.delay + ' ms' : '— ms') : (n.ping + ' ms'); }


  // ---- render source (games) + dest (nodes) lists ----
  // Boost-tunneled apps, exactly like the main dashboard's boost cards:
  // action must be 'vpn' or 'vpn+proxy', the label comes from routeLabels
  // (falling back to 'VPN'), running apps carry the program's isRunning flag
  // and latencyText, and a live app shows the green "running" state.
  // Every boost app from the main dashboard's split table shows here — tunneled
  // (vpn/vpn+proxy), direct and block alike — so the skin mirrors the dashboard
  // route selector exactly. The switch simply reflects the effective action.
  function gameItems(S) { return (S.monitorSnapshot.apps || []); }

  // Local route overrides so the split switches work immediately even before
  // the host echoes a new snapshot: the switch posts set_app_route (real program
  // action) and remembers the choice locally so the card flips on screen. Once
  // the host pushes the next updateMonitorSnapshot the authoritative action
  // replaces the override.
  const _localRoutes = {}; // processName -> action override
  const _localRouteTs = {}; // processName -> ms timestamp of the override
  const ROUTE_CONFIRM_MS = 2500; // host echo window; past this the authoritative action wins

  function actionFor(a) {
    if (a && a.processName && Object.prototype.hasOwnProperty.call(_localRoutes, a.processName)) {
      return _localRoutes[a.processName];
    }
    return a ? (a.action || '') : '';
  }

  function isTunneled(action) { return action === 'vpn' || action === 'vpn+proxy' || action === 'proxy'; }

  // Shared keyboard decision logic loaded via <script src="../route-keys.js">.
  // Falls back to a local copy if the module ever fails to load, so the skin
  // keeps working standalone. The authoritative logic lives in route-keys.js
  // and is covered by route-keys.test.js.
  const routeFromKey = (typeof AoGPNRouteKeys !== 'undefined' && AoGPNRouteKeys.routeFromKey)
    ? AoGPNRouteKeys.routeFromKey
    : function (key, currentAction) {
        if (key === 'Enter' || key === ' ') return isTunneled(currentAction) ? 'direct' : 'vpn';
        if (key === 'ArrowRight') return isTunneled(currentAction) ? null : 'vpn';
        if (key === 'ArrowLeft') return isTunneled(currentAction) ? 'direct' : null;
        return null;
      };
  const cyRouteCycle = (typeof AoGPNRouteKeys !== 'undefined' && AoGPNRouteKeys.routeCycle)
    ? AoGPNRouteKeys.routeCycle
    : function (action) {
        const order = ['vpn', 'direct', 'block', 'warp'];
        const cur = isTunneled(action) ? 'vpn' : action;
        const idx = order.indexOf(cur);
        return order[(idx + 1) % order.length];
      };

  // Gerçek exe ikonu (host AppIconService -> skinBridge.appIcons); ikon yoksa
  // iki harfli yer tutucu kutu basılır.
  function cyAppIcon(a) {
    let uri = '';
    if (a && a.exePath && B) {
      try {
        const icons = B.appIcons;
        if (icons) uri = icons[String(a.exePath).trim().toLowerCase()] || '';
      } catch (e) { /* read-only bridge */ }
    }
    const label = String(a.displayName || a.processName || 'APP').slice(0, 2).toUpperCase() || '?';
    if (uri) return '<i class="cy-appIco"><img src="' + uri + '" alt="" draggable="false"></i>';
    return '<i class="cy-appIco">' + esc(label) + '</i>';
  }

  function sourceCard(a, i, S) {
    const name = a.displayName || a.processName || a.value || 'Target app';
    const action = actionFor(a);
    const route = routeLabel(action);
    const running = a.isRunning === true;
    const lat = (a.latencyText && a.latencyText !== '—') ? a.latencyText : null;
    const sel = _src === 'src' + i;
    const pname = a.processName || a.value || name;
    const on = isTunneled(action);
    const sub = (running ? '<span class="cy-live-tag">● ' + esc(cyT('running').toUpperCase()) + '</span> ' : '') + esc(String(route).toUpperCase()) + (lat ? ' <span class="cy-lat">' + esc(lat) + '</span>' : '');
    return '<div class="cy-target' + (sel ? ' active' : '') + (running ? ' live' : '') + '" data-cy-src="src' + i + '" data-cy-pname="' + esc(pname) + '">'
      + cyAppIcon(a)
      + '<div><h4>' + esc(name) + '</h4><span>' + sub + '</span></div>'
      + '<div class="cy-switch' + (on ? ' on' : '') + '" role="switch" aria-checked="' + on + '" tabindex="0" title="' + esc(cyT('switchTitle')) + '"></div></div>';
  }

  // Set one app's route explicitly through the host. Used by the split switch
  // toggle, Enter/Space, and the ←/→ arrow keys (right = tunnel on, left =
  // tunnel off). No-ops when the route is already the target so keyboard
  // repeats and direction presses don't spam the host or the console.
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
    logTerm(cyT('log.routeSet', { route: routeLabel(next).toUpperCase(), app: processName.toUpperCase() }) + (isTunneled(next) ? cyT('log.tunneled') : cyT('log.direct')));
    refreshTargets();
  }

  // Flip the app's route (vpn/proxy-family <-> direct) — what Enter/Space and
  // the switch click do.
  function toggleAppRoute(processName) {
    if (!processName) return;
    const S = state();
    const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === processName);
    const cur = actionFor(app || { processName });
    setAppRoute(processName, isTunneled(cur) ? 'direct' : 'vpn');
  }

  function renderSources(S) {
    const root = $('cySources');
    if (!root) return;
    const apps = gameItems(S);
    let html = '';
    if (apps.length === 0) {
      if (!B) {
        // Standalone design preview (no parent bridge): keep the PCB panel
        // populated with sample targets so the layout stays editable.
        html = [
          { displayName: 'Escape from Tarkov' },
          { displayName: 'League of Legends' },
          { displayName: 'Counter-Strike 2' }
        ].map((a, i) => sourceCard(a, i, S)).join('');
      } else {
        // Real app with no boost apps defined: honest empty state matching the
        // main dashboard's "No games assigned yet" card, not fake data.
        html = '<div class="cy-empty">' + esc(cyT('empty.title')) + '<br><small>' + esc(cyT('empty.sub')) + '</small></div>';
      }
    } else {
      // Every boost app gets its own card (multiple apps tunnel at once). When
      // there are many, the list enters compact mode so they all stay visible
      // without the whole panel overflowing.
      html = apps.map((a, i) => sourceCard(a, i, S)).join('');
    }
    root.innerHTML = html;
    root.classList.toggle('many', apps.length > 4);
    root.querySelectorAll('[data-cy-src]').forEach(el => {
      // Card click = select which app's trace is highlighted (works whether
      // connected or not — it only drives the visual trace focus).
      el.addEventListener('click', () => {
        // Capture the label before refreshTargets() re-renders and detaches it.
        const label = el.querySelector('h4') ? el.querySelector('h4').textContent : '';
        _src = el.dataset.cySrc;
        $('cyFrame').setAttribute('data-source', _src);
        refreshTargets();
        logTerm(cyT('log.sourceOverride', { name: label }));
      });
      // Switch click = toggle that app's real route (vpn <-> direct) through
      // the host; the card flips immediately via the local override.
      const sw = el.querySelector('.cy-switch');
      if (sw) {
        sw.addEventListener('click', (e) => { e.stopPropagation(); toggleAppRoute(el.dataset.cyPname); });
        sw.addEventListener('keydown', (e) => {
          // Keyboard contract lives in the shared pure module (route-keys.js,
          // tested by route-keys.test.js): Enter/Space flip, ←/→ one direction.
          const pname = el.dataset.cyPname;
          const S = state();
          const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === pname);
          const cur = actionFor(app || { processName: pname });
          const next = routeFromKey(e.key, cur);
          if (next) {
            e.preventDefault();
            setAppRoute(pname, next);
          }
        });
      }
    });
    layoutSourceTraces();
  }

  // ---- dynamic source traces ----
  // Each boost app gets its own PCB trace from its card into the core, so
  // multiple simultaneous connections are visible as a fan of live lines.
  // Paths are measured from the rendered cards (Y position) and rebuilt every
  // render, so the fan follows the actual compact/scroll layout.
  const SRC_CX = 700; // core entry x (viewBox units) — the JACK IN button's center
  const SRC_CY = 335; // core entry y — sources converge here, behind the JACK IN button

  function layoutSourceTraces() {
    const layer = $('cySourceTraces');
    const frame = $('cyFrame');
    const root = $('cySources');
    if (!layer || !frame || !root) return;
    const items = Array.from(root.querySelectorAll('[data-cy-src]'));
    const fr = frame.getBoundingClientRect();
    if (items.length === 0 || fr.width === 0) { layer.innerHTML = ''; return; }
    // SVG uses viewBox 0 0 1400 800 with preserveAspectRatio="none" and the
    // frame is fixed 1400x800 CSS px, so client rects map 1:1 to viewBox units.
    const sx = 1400 / fr.width, sy = 800 / fr.height;
    layer.innerHTML = items.map((it, i) => {
      const ir = it.getBoundingClientRect();
      const y = Math.round((ir.top + ir.height / 2 - fr.top) * sy);
      const x0 = Math.round((ir.right - fr.left) * sx);
      const key = it.dataset.cySrc;
      const live = it.classList.contains('live');
      const active = key === _src;
      // Route the trace from the card's right edge into the core, angling
      // toward the center line so stacked cards fan naturally.
      const mid = x0 + 64;
      const bend = (y < SRC_CY) ? Math.min(SRC_CY - 30, y + 46) : Math.max(SRC_CY + 30, y - 46);
      const d = 'M ' + x0 + ' ' + y + ' H ' + mid + ' L ' + (mid + 44) + ' ' + bend + ' L ' + SRC_CX + ' ' + SRC_CY;
      return '<path class="trace-src-idle" data-src="' + key + '" d="' + d + '"/>'
        + '<path class="trace-src-line' + (active ? ' active' : '') + (live ? ' live' : '') + '" data-src="' + key + '" d="' + d + '"/>'
        + '<path class="trace-src-pulse' + (live ? ' live' : '') + '" data-src="' + key + '" d="' + d + '"/>';
    }).join('');
  }

  function renderDests(S) {
    const root = $('cyDestinations');
    if (!root) return;
    const nodes = S.nodes || [];
    const list = nodes.length ? nodes : DEMO.nodes;
    const act = activeKey(S);
    // A destination card is highlighted when either the slot is the local
    // selection (_dst) or it holds the program's actual active endpoint — the
    // two stay in sync as soon as the user (or the host) picks a node.
    const activeIdx = Math.max(0, list.findIndex(n => String(nodeKey(n, S.useRealNodes)) === act));
    root.innerHTML = list.slice(0, 3).map((n, i) => {
      const key = nodeKey(n, S.useRealNodes);
      const cc = S.useRealNodes ? (n.country || n.sub || 'VPN').slice(0, 2) : (n.code || '??');
      const sel = i === activeIdx || _dst === 'dst' + i;
      if (i === activeIdx) _dst = 'dst' + i;
      const ms = nodeMs(n, S.useRealNodes);
      const proto = (S.useRealNodes && n.protocol) ? ' // ' + esc(n.protocol) : '';
      return '<div class="cy-target' + (sel ? ' active' : '') + '" data-cy-dst="dst' + i + '" data-cy-key="' + esc(String(key)) + '">'
        + '<div><h4>' + esc(nodeLabel(n, S.useRealNodes)) + '</h4><span>' + esc(cc) + ' // ' + esc(ms) + proto + '</span></div>'
        + '<div class="cy-switch"></div></div>';
    }).join('');
    root.querySelectorAll('[data-cy-dst]').forEach(el => {
      el.addEventListener('click', () => {
        // Capture the label BEFORE refreshTargets() re-renders the list — the
        // clicked card is detached by then. textContent (not innerText, which
        // jsdom leaves undefined) is the portable way to read it.
        const label = el.querySelector('h4') ? el.querySelector('h4').textContent : '';
        // Node switching stays available while connected: the real program
        // supports mid-session server changes (requestNodeSwitch/applyNode).
        _dst = el.dataset.cyDst;
        $('cyFrame').setAttribute('data-dest', _dst);
        const key = el.dataset.cyKey;
        const S2 = state();
    if (B) {
      if (S2.useRealNodes && typeof B.requestNodeSwitch === 'function') { B.requestNodeSwitch(key); }
      else if (!S2.useRealNodes) {
        const found = (S2.nodes || []).find(n => nodeKey(n, false) === key) || S2.nodes[0];
        SET('selectedNode', found);
        if (typeof B.applyNode === 'function') B.applyNode();
      }
    } else {
      DEMO.selectedNode = (DEMO.nodes || []).find(n => n.key === key) || DEMO.nodes[0];
    }
    refreshTargets();
    logTerm(cyT('log.targetLocked', { name: label }), 'sys-msg');
      });
    });
  }

  function refreshTargets() { const S = state(); renderSources(S); renderDests(S); }

  // ---- telemetry ----
  // Mirrors the program's full telemetry (same array the main dashboard uses):
  // [0]=ping ms, [1]=packet loss %, [2]=download Mbps, [3]=upload Mbps,
  // plus the session timer and the verified exit IP.
  //
  // The spark pulses streaming along the PCB traces are traffic-driven: the
  // destination fan (core -> exit node) runs at a rate set by the download
  // throughput, the source fan (apps -> core) at the upload rate. Durations
  // live in CSS custom properties on #cyFrame so the SVG paths (which are
  // regenerated on every render) inherit them, and --cy-glow scales the spark
  // glow with total flow. An idle but linked session keeps a slow crawl;
  // FLOW_SAT Mbps saturates a direction to full strobe speed.
  const FLOW_SAT = 20; // Mbps per direction at which the pulse runs at full speed
  const flowIntensity = (mbps) => (Number.isFinite(mbps) && mbps > 0) ? Math.min(1, mbps / FLOW_SAT) : 0;
  const flowDuration = (f) => (2.4 - Math.min(1, f) * 2.1).toFixed(3) + 's'; // 2.4s idle -> 0.3s full
  function renderTelemetry(S) {
    const tele = S.telemetry || [];
    const has = tele.some(v => v !== undefined && v !== null);
    const on = S.connected;
    const ping = (on && has && Number.isFinite(tele[0]) && tele[0] > 0) ? Math.round(tele[0]) : null;
    const loss = (on && has && Number.isFinite(tele[1]) && tele[1] >= 0) ? Number(tele[1]) : null;
    const down = (on && has && Number.isFinite(tele[2]) && tele[2] > 0) ? tele[2] : null;
    const up = (on && has && Number.isFinite(tele[3]) && tele[3] > 0) ? tele[3] : null;
    const pingEl = $('cyPing'), lossEl = $('cyLoss'), downEl = $('cyDown'), upEl = $('cyUp');
    // While connected, blank values mean the core hasn't pushed a sample yet —
    // show a neutral "awaiting" marker instead of an alarmed ERR so the panel
    // reads as linking, not broken. Once disconnected, ERR is honest. The same
    // marker applies to every metric so the panel never mixes ERR and -- (or
    // claims a 0.0 % loss while it is still waiting for the first sample).
    const idle = on ? '--' : 'ERR';
    if (pingEl) pingEl.innerHTML = (ping != null ? ping : idle) + ' <span class="cy-unit">ms</span>';
    if (lossEl) lossEl.innerHTML = (loss != null ? loss.toFixed(1) : idle) + ' <span class="cy-unit">%</span>';
    if (downEl) downEl.innerHTML = (down != null ? down.toFixed(1) : idle) + ' <i>Mbps ⇣</i>';
    if (upEl) upEl.innerHTML = (up != null ? up.toFixed(1) : idle) + ' <i>Mbps ⇡</i>';
    const bp = $('cyBarPing'), bl = $('cyBarLoss'), bd = $('cyBarDown');
    if (bp) bp.style.width = (ping != null ? Math.min(100, ping) : 0) + '%';
    if (bl) bl.style.width = (loss != null ? Math.min(100, loss * 2) : 0) + '%';
    if (bd) bd.style.width = (down != null ? Math.min(100, Math.max(4, down * 2)) : 0) + '%';
    // Traffic-driven trace flow (see FLOW_SAT / flowDuration above). While
    // linked but before the first sample arrives, keep a gentle crawl instead
    // of freezing the traces.
    const idleFlow = (on && down === null && up === null) ? 0.12 : 0;
    const destFlow = on ? Math.max(flowIntensity(down), idleFlow) : 0;
    const srcFlow = on ? Math.max(flowIntensity(up), idleFlow) : 0;
    _flowSm = _flowSm * 0.6 + Math.max(destFlow, srcFlow) * 0.4;
    const frame = $('cyFrame');
    if (frame) {
      frame.style.setProperty('--cy-dest-dur', flowDuration(destFlow));
      frame.style.setProperty('--cy-src-dur', flowDuration(srcFlow));
      frame.style.setProperty('--cy-glow', (4 + _flowSm * 18).toFixed(1) + 'px');
    }
    const sess = $('cySession'), exit = $('cyExitIp');
    if (sess) sess.textContent = S.sessionTime || '00:00:00';
    if (exit) exit.textContent = (on && S.exitIp && S.exitIp !== 'Not verified') ? S.exitIp : '—';
  }

  // ---- connect (JACK IN) ----
  function jackIn() {
    const S = state();
    if (S.connected) {
      if (B) {
        // `connected` = bastığı anda ekranda görünen durum → host yönü bu
        // niyetten türetir (bkz. features/gpn.js).
        const ok = B.postToHost({ action: 'toggle_connection', mode: S.mode, transport: S.transport, protocol: S.protocolPreference, connected: S.connected === true });
        if (!ok && typeof B.setConnected === 'function') B.setConnected(false, false);
      } else { DEMO.connected = false; }
      logTerm(cyT('console.terminating'), 'err-msg');
      logTerm(cyT('console.exposed'), 'err-msg');
      return;
    }
    if (S.connecting) return;
    // Breaching sequence
    const frame = $('cyFrame');
    frame.classList.add('cn');
    const main = $('cyMain'), sub = $('cySub');
    if (main) main.innerText = cyT('state.breaching');
    if (sub) sub.innerText = cyT('sub.breaching');
    logTerm(cyT('console.initiating'));
    setTimeout(() => logTerm(cyT('console.configuring')), 500);
    setTimeout(() => logTerm(cyT('console.injecting')), 1200);

    setTimeout(() => {
      if (B) {
        // `connected` = bastığı anda ekranda görünen durum → host yönü bu
        // niyetten türetir (bkz. features/gpn.js).
        const ok = B.postToHost({ action: 'toggle_connection', mode: S.mode, transport: S.transport, protocol: S.protocolPreference, connected: S.connected === true });
        if (!ok && typeof B.setConnected === 'function') B.setConnected(true, false);
      } else { DEMO.connected = true; }
      frame.classList.remove('cn');
      applyConnectedUI(true);
      logTerm(cyT('console.granted'), 'ok-msg');
    }, 2000);
  }

  function applyConnectedUI(on) {
    const frame = $('cyFrame');
    const body = document.body;
    const main = $('cyMain'), sub = $('cySub'), termH = $('cyTermHeader');
    frame.classList.toggle('cn', false);
    body.classList.toggle('is-breached', on);
    if (main) main.innerText = on ? cyT('state.connected') : cyT('state.idle');
    if (sub) sub.innerText = on ? cyT('sub.connected') : cyT('sub.idle');
    if (termH) termH.innerText = on ? cyT('console.secure') : cyT('console.root');
  }

  // ---- wiring ----
  function wire() {
    if (_wired) return;
    _wired = true;

    const power = $('cyPower');
    if (power) {
      power.addEventListener('click', () => jackIn());
      power.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); jackIn(); } });
    }

    document.querySelectorAll('[data-cy-theme]').forEach(b => b.addEventListener('click', () => setTheme(b.dataset.cyTheme)));
    const std = $('cyToStandardBtn');
    if (std) std.addEventListener('click', () => { if (B) B.applySkin('standard'); });
    const win = (cmd) => { if (B) B.postToHost({ action: 'app_control', command: cmd }); logTerm(cyT('log.appControl', { command: cmd.toUpperCase().replace('_', ' ') })); };
    const min = $('cyMinBtn'); if (min) min.addEventListener('click', () => win('minimize'));
    const max = $('cyMaxBtn'); if (max) max.addEventListener('click', () => win('maximize_toggle'));
    const close = $('cyCloseBtn'); if (close) close.addEventListener('click', () => win('close'));
    const drag = $('cyDragRegion');
    if (drag) {
      let last = 0;
      drag.addEventListener('pointerdown', (e) => {
        if (e.button) return; e.preventDefault();
        const now = Date.now(), dc = now - last < 400; last = now;
        win(dc ? 'maximize_toggle' : 'drag');
      });
    }

    // ---- connection mode (gpn / global vpn) -> set_connection_mode ----
    document.querySelectorAll('[data-cy-mode]').forEach(b => b.addEventListener('click', () => {
      const next = b.dataset.cyMode;
      SET('mode', next);
      if (B) B.postToHost({ action: 'set_connection_mode', mode: next });
      sync();
      logTerm(cyT('log.connectionMode', { mode: cyT(next === 'vpn' ? 'mode.vpn' : 'mode.gpn').toUpperCase() }), 'sys-msg');
    }));
    // ---- capture / transport (proxy / tun) -> set_transport ----
    document.querySelectorAll('[data-cy-transport]').forEach(b => b.addEventListener('click', () => {
      const next = b.dataset.cyTransport;
      SET('transport', next);
      if (B) B.postToHost({ action: 'set_transport', transport: next });
      sync();
      logTerm(cyT('log.transport', { transport: cyT(next === 'tun' ? 'transport.tun' : 'transport.proxy').toUpperCase() }), 'sys-msg');
    }));

    // ---- bottom-left: route mode / direction / auto-reconnect ----
    document.querySelectorAll('[data-cy-split]').forEach(b => b.addEventListener('click', () => {
      const next = b.dataset.cySplit;
      SET('splitMode', next);
      if (B) B.postToHost({ action: 'set_split_mode', mode: next });
      sync();
      logTerm(cyT('log.routeMode', { mode: cyT('split.' + next).toUpperCase() }), 'sys-msg');
    }));
    document.querySelectorAll('[data-cy-dir]').forEach(b => b.addEventListener('click', () => {
      const next = b.dataset.cyDir === 'blacklist';
      _invertManual = next;
      if (B) B.postToHost({ action: 'set_split_direction', invert: String(next) });
      sync();
      logTerm(cyT('log.splitDir', { dir: cyT(next ? 'dir.blacklist' : 'dir.whitelist').toUpperCase() }), 'sys-msg');
    }));
    const ar = $('cyAutoReconnect');
    if (ar) ar.addEventListener('click', () => {
      const next = !(G('autoReconnect') === true);
      SET('autoReconnect', next);
      if (B) B.postToHost({ action: 'set_auto_reconnect', enabled: next });
      sync();
      logTerm(cyT('log.autoReconnect', { state: cyT(next ? 'log.on' : 'log.off') }), 'sys-msg');
    });

    // ---- bottom-right: settings (system proxy + protocol) ----
    const proxyToggle = $('cyProxyToggle');
    if (proxyToggle) proxyToggle.addEventListener('click', () => {
      const cur = G('systemProxyMode');
      const next = isProxyOn(cur) ? 0 : 1;
      if (B) B.postToHost({ action: 'toggle_system_proxy' });
      SET('systemProxyMode', next);
      sync();
      logTerm(cyT('log.systemProxy', { state: cyT(isProxyOn(next) ? 'log.on' : 'log.off') }), 'sys-msg');
    });
    const proxyMode = $('cyProxyMode');
    if (proxyMode) proxyMode.addEventListener('change', () => {
      const next = Math.max(0, Math.min(3, Number(proxyMode.value) || 0));
      if (B) B.postToHost({ action: 'set_system_proxy_mode', mode: next });
      SET('systemProxyMode', next);
      sync();
      logTerm(cyT('log.proxyMode', { mode: String(next) }), 'sys-msg');
    });
    const proto = $('cyProtocol');
    if (proto) proto.addEventListener('change', () => {
      const next = String(proto.value || 'auto');
      if (B) B.postToHost({ action: 'set_protocol_preference', protocol: next });
      SET('protocolPreference', next);
      sync();
      logTerm(cyT('log.protocol', { protocol: String(next).toUpperCase() }), 'sys-msg');
    });
    // Language selects share one handler: the header one (cyHeaderLang) and
    // the SETTINGS one (cyLanguage) both switch the WHOLE app through the
    // same channel as the top-bar picker / NEXUS settings and persist through
    // the host (set_language).
    ['cyLanguage', 'cyHeaderLang'].forEach(id => {
      const lang = $(id);
      if (lang) lang.addEventListener('change', () => {
        const next = String(lang.value || 'en');
        if (B && typeof B.setLanguage === 'function') B.setLanguage(next);
        SET('language', next);
        sync();
        logTerm(cyT('log.language', { lang: String(next).toUpperCase() }), 'sys-msg');
      });
    });

    // ---- console tabs ----
    document.querySelectorAll('[data-cy-tab]').forEach(b => b.addEventListener('click', () => cySwitchTab(b.dataset.cyTab)));

    // ---- extended settings (dashboard parity) ----
    const tunStack = $('cyTunStack');
    if (tunStack) tunStack.addEventListener('change', () => {
      const next = String(tunStack.value || 'mixed');
      if (B) B.postToHost({ action: 'set_tun_stack', stack: next });
      SET('tunStack', next);
      sync();
      logTerm(cyT('log.tunStack', { stack: String(next).toUpperCase() }), 'sys-msg');
    });
    const effects = $('cyEffects');
    if (effects) effects.addEventListener('change', () => {
      const next = String(effects.value || 'full');
      if (B) B.postToHost({ action: 'set_effects_tier', tier: next });
      SET('effectsTier', next);
      sync();
      logTerm(cyT('log.effects', { effects: String(next).toUpperCase() }), 'sys-msg');
    });
    const autoGame = $('cyAutoGame');
    if (autoGame) autoGame.addEventListener('click', () => {
      const next = !(autoGame.classList.contains('on'));
      autoGame.classList.toggle('on', next); autoGame.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_auto_game_connect', enabled: next });
      logTerm(cyT('log.autoGame', { state: cyT(next ? 'log.on' : 'log.off') }), 'sys-msg');
    });
    const recovery = $('cyRecovery');
    if (recovery) recovery.addEventListener('click', () => {
      const next = !(recovery.classList.contains('on'));
      recovery.classList.toggle('on', next); recovery.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_gpn_recovery_watch', enabled: next });
      logTerm(cyT('log.recovery', { state: cyT(next ? 'log.on' : 'log.off') }), 'sys-msg');
    });
    const failover = $('cyFailover');
    if (failover) failover.addEventListener('click', () => {
      const next = !(failover.classList.contains('on'));
      failover.classList.toggle('on', next); failover.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_gpn_failover', enabled: next });
      logTerm(cyT('log.failover', { state: cyT(next ? 'log.on' : 'log.off') }), 'sys-msg');
    });
    const proxyTest = $('cyProxyTest');
    if (proxyTest) proxyTest.addEventListener('click', () => {
      if (proxyTest.classList.contains('running')) return;
      proxyTest.classList.add('running'); proxyTest.classList.remove('ok', 'fail');
      proxyTest.textContent = cyT('test.running');
      if (B) B.postToHost({ action: 'test_proxy' });
      logTerm(cyT('log.proxyTest'), 'sys-msg');
      // Safety: if the host never answers, drop back to idle after 6 s.
      setTimeout(() => {
        if (proxyTest.classList.contains('running')) {
          proxyTest.classList.remove('running'); proxyTest.classList.add('fail');
          proxyTest.textContent = cyT('test.fail');
        }
      }, 6000);
    });
  }

  // ---- bottom-left / bottom-right controls (split, direction, proxy, protocol) ----
  let _invertManual = false; // optimistic direction until the host echoes a snapshot
  const isProxyOn = (v) => Number(v) === 1 || Number(v) === 3;

  function renderControls(S) {
    // CONNECTION MODE pills mirror the connection mode (gpn / global vpn).
    document.querySelectorAll('[data-cy-mode]').forEach(b => b.classList.toggle('active', b.dataset.cyMode === (S.mode === 'vpn' ? 'vpn' : 'gpn')));
    // CAPTURE pills mirror the transport (proxy / tun).
    document.querySelectorAll('[data-cy-transport]').forEach(b => b.classList.toggle('active', b.dataset.cyTransport === S.transport));
    // ROUTE MODE pills mirror the shared splitMode (same concept as the mode pills).
    document.querySelectorAll('[data-cy-split]').forEach(b => b.classList.toggle('active', b.dataset.cySplit === S.splitMode));
    // SPLIT DIRECTION pills: host snapshot wins when present, else the optimistic local.
    const invert = (S.monitorSnapshot && S.monitorSnapshot.invertManualRouting === true) || _invertManual === true;
    document.querySelectorAll('[data-cy-dir]').forEach(b => b.classList.toggle('active', (b.dataset.cyDir === 'blacklist') === invert));
    // AUTO RECONNECT switch.
    const ar = $('cyAutoReconnect');
    if (ar) { ar.classList.toggle('on', S.autoReconnect === true); ar.setAttribute('aria-checked', String(S.autoReconnect === true)); }
    // SETTINGS: system proxy toggle + mode, protocol preference, language.
    const pt = $('cyProxyToggle');
    if (pt) { pt.textContent = isProxyOn(S.systemProxyMode) ? cyT('proxy.on') : cyT('proxy.off'); pt.classList.toggle('on', isProxyOn(S.systemProxyMode)); }
    const pm = $('cyProxyMode');
    if (pm) pm.value = String(Number(S.systemProxyMode) || 0);
    const pp = $('cyProtocol');
    if (pp) pp.value = S.protocolPreference || 'auto';
    ['cyLanguage', 'cyHeaderLang'].forEach(id => {
      const pl = $(id);
      if (pl) pl.value = S.language || 'en';
    });
    // Extended settings (dashboard parity): TUN stack, effects tier, auto-game
    // connect, GPN recovery watch / failover and the system-proxy test button.
    const ts = $('cyTunStack');
    if (ts) ts.value = S.tunStack || 'mixed';
    const ef = $('cyEffects');
    if (ef) ef.value = S.effectsTier || 'full';
    const autoGame = $('cyAutoGame');
    if (autoGame) {
      const on = !!(S.monitorSnapshot && S.monitorSnapshot.autoConnectOnGameStart === true);
      autoGame.classList.toggle('on', on); autoGame.setAttribute('aria-checked', String(on));
    }
    const rec = $('cyRecovery');
    if (rec) {
      const on = S.gpnRecoveryWatch === true;
      rec.classList.toggle('on', on); rec.setAttribute('aria-checked', String(on));
    }
    const fo = $('cyFailover');
    if (fo) {
      const on = S.gpnFailover === true;
      fo.classList.toggle('on', on); fo.setAttribute('aria-checked', String(on));
    }
    const ptest = $('cyProxyTest');
    if (ptest) {
      const r = S.proxyTestResult;
      const ok = !!(r && r.ok === true);
      const bad = !!(r && r.ok === false);
      ptest.classList.toggle('ok', ok); ptest.classList.toggle('fail', bad); ptest.classList.toggle('running', false);
      ptest.textContent = ok ? (Number.isFinite(r.ms) ? r.ms + ' ms' : cyT('test.ok')) : bad ? cyT('test.fail') : cyT('test.idle');
    }
  }

  // ---- console tabs (dashboard module parity) ----
  // The bottom terminal becomes a tabbed console: TERMINAL keeps the live log,
  // NODES lists route nodes (click to select), GPN shows the managed servers
  // with live probes, MONITOR mirrors the dashboard Connection Monitor (filter,
  // hide listeners, per-row route assignment) and ABOUT shows program info.
  let _cyTab = 'terminal';
  let _cyMonFilter = '';
  let _cyMonHideListeners = true;
  let _cyMonGroup = 'none';
  const _cyMonCollapsed = new Set();
  try {
    const saved = JSON.parse(localStorage.getItem('aogpn.cyberMon.v1') || '{}');
    if (typeof saved.hideListeners === 'boolean') _cyMonHideListeners = saved.hideListeners;
    if (['none', 'route', 'protocol', 'state', 'country', 'app'].includes(saved.group)) _cyMonGroup = saved.group;
  } catch (e) { /* no localStorage */ }
  function _cyMonPersist() {
    try { localStorage.setItem('aogpn.cyberMon.v1', JSON.stringify({ hideListeners: _cyMonHideListeners, group: _cyMonGroup })); } catch (e) { /* ignore */ }
  }
  const CY_MON_GROUP_KEYS = [
    ['none', 'monitor.view.groupNone'], ['route', 'monitor.view.groupRoute'], ['protocol', 'monitor.view.groupProtocol'],
    ['state', 'monitor.view.groupState'], ['country', 'monitor.view.groupCountry'], ['app', 'monitor.view.groupApp']
  ];

  function cySwitchTab(name) {
    _cyTab = name;
    document.querySelectorAll('[data-cy-tab]').forEach(b => b.classList.toggle('active', b.dataset.cyTab === name));
    document.querySelectorAll('[data-cy-panel]').forEach(p => {
      const on = p.dataset.cyPanel === name;
      p.hidden = !on;
      p.classList.toggle('active', on);
    });
    cyRenderTab();
  }

  function cyRenderTab() {
    const S = state();
    if (_cyTab === 'nodes') cyRenderTabNodes(S);
    else if (_cyTab === 'gpn') cyRenderTabGpn(S);
    else if (_cyTab === 'monitor') cyRenderTabMonitor(S);
    else if (_cyTab === 'about') cyRenderTabAbout(S);
  }

  function cyRenderTabNodes(S) {
    const root = $('cyTabNodes');
    if (!root) return;
    const nodes = (S.nodes && S.nodes.length) ? S.nodes : DEMO.nodes;
    const useReal = S.useRealNodes === true;
    const active = ensureActiveNode(S);
    const activeKeyId = active ? String(nodeKey(active, useReal)) : '';
    root.innerHTML =
      '<div class="cy-tabHead"><span>' + esc(cyT('tabs.nodes')) + '</span><em>' + nodes.length + '</em></div>'
      + (nodes.length === 0
        ? '<p class="cy-empty">' + esc(cyT('node.none')) + '<br><small>' + esc(cyT('node.noneSub')) + '</small></p>'
        : '<div class="cy-nodeList">' + nodes.map(n => {
            const key = nodeKey(n, useReal);
            const cc = useReal ? (n.country || n.sub || 'VPN').slice(0, 2) : (n.code || '??');
            const sel = String(key) === activeKeyId;
            const proto = (useReal && n.protocol) ? ' // ' + esc(n.protocol) : '';
            return '<button type="button" class="cy-nodeRow' + (sel ? ' active' : '') + '" data-cy-node="' + esc(String(key)) + '">'
              + '<span class="cy-flag">' + esc(cc) + '</span>'
              + '<span class="cy-nodeName">' + esc(nodeLabel(n, useReal)) + '</span>'
              + '<span class="cy-nodeMs">' + esc(nodeMs(n, useReal)) + proto + '</span>'
              + '<span class="cy-nodeAct">' + (sel ? esc(cyT('node.active')) : esc(cyT('node.select'))) + '</span>'
              + '</button>';
          }).join('') + '</div>');
    root.querySelectorAll('[data-cy-node]').forEach(btn => {
      btn.addEventListener('click', () => {
        const key = btn.dataset.cyNode;
        const S2 = state();
        if (B) {
          if (S2.useRealNodes && typeof B.requestNodeSwitch === 'function') B.requestNodeSwitch(key);
          else if (!S2.useRealNodes) {
            const found = (S2.nodes || []).find(n => nodeKey(n, false) === key) || S2.nodes[0];
            SET('selectedNode', found);
            if (typeof B.applyNode === 'function') B.applyNode();
          }
        } else {
          DEMO.selectedNode = (DEMO.nodes || []).find(n => n.key === key) || DEMO.nodes[0];
        }
        logTerm(cyT('log.targetLocked', { name: (btn.querySelector('.cy-nodeName') || {}).textContent || key }), 'sys-msg');
        cyRenderTabNodes(state());
        refreshTargets();
      });
    });
  }

  function cyRenderTabGpn(S) {
    const root = $('cyTabGpn');
    if (!root) return;
    const servers = S.gpnServers || [];
    const probes = {};
    (S.gpnServerProbes || []).forEach(p => { probes[p.serverId] = p; });
    const rows = servers.map(s => {
      const probe = probes[s.serverId];
      const badge = probe && s.isEnabled
        ? (probe.isSuccess
          ? '<span class="cy-gpnProbe ok">' + (Number.isFinite(probe.delayMs) && probe.delayMs >= 0 ? Math.round(probe.delayMs) + ' ms' : '—') + ' · ' + (Number.isFinite(probe.lossPercent) ? probe.lossPercent + '%' : '—') + ' · ' + esc(String(probe.udpStatus || '?').toUpperCase()) + '</span>'
          : '<span class="cy-gpnProbe fail">✕</span>')
        : '<span class="cy-gpnProbe">?</span>';
      const active = S.activeGpnServer === s.serverId;
      return '<div class="cy-gpnRow' + (active ? ' active' : '') + '">'
        + '<div class="cy-gpnMain"><b>' + esc(s.serverName || s.serverId || '—') + '</b>' + badge + '</div>'
        + '<div class="cy-gpnActs">'
        + '<button type="button" class="cy-gpnBtn' + (s.isEnabled ? ' on' : '') + '" data-cy-gpn-toggle="' + esc(s.serverId) + '">' + (s.isEnabled ? esc(cyT('gpn.servers.disable')) : esc(cyT('gpn.servers.enable'))) + '</button>'
        + (active ? '<span class="cy-gpnLive">● ' + esc(cyT('gpn.servers.active')) + '</span>' : '')
        + '</div></div>';
    }).join('');
    // ---- failover telemetry + resilience log (dashboard parity) ----
    // Same host contract as the dashboard GPN section: five live counters from
    // skinBridge.gpnTelemetry (reset via reset_gpn_telemetry) and the last-50
    // decisions ring buffer from skinBridge.gpnResilienceLog (refresh/clear via
    // get_gpn_resilience_log / clear_gpn_resilience_log).
    const snap = S.gpnTelemetry || {};
    const num = (v) => String(Number.isFinite(v) ? v : 0);
    const telItem = (label, cls, v) => '<span class="cy-gpnTelItem ' + cls + '"><b>' + num(v) + '</b><i>' + esc(label) + '</i></span>';
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
          const label = key ? cyT(key) : (e && e.action) || '';
          const mode = e && e.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WireGuard';
          const target = e && (e.targetServerName || e.targetServerId || '');
          const parts = [e && (e.serverName || e.serverId || ''), (target ? '→ ' + target : ''), e && e.reason ? e.reason : ''].filter(Boolean).join('  ·  ');
          return '<div class="cy-ev ' + cls + '"><i class="cy-evDot"></i><div class="cy-evMain"><div class="cy-evLine"><span class="cy-evBadge">' + esc(label) + '</span><em class="dim">' + esc(mode) + '</em><span class="cy-evTime">' + esc(time) + '</span></div>'
            + (parts ? '<p class="cy-evDetail">' + esc(parts) + '</p>' : '') + '</div></div>';
        }).join('')
      : '<p class="cy-empty slim">' + esc(cyT('gpn.resEmpty')) + '</p>';
    // One innerHTML pass — appending after wiring would destroy the listeners.
    root.innerHTML =
      '<div class="cy-tabHead"><span>' + esc(cyT('gpn.servers')) + '</span><em>' + servers.length + '</em></div>'
      + (servers.length === 0
        ? '<p class="cy-empty">' + esc(cyT('gpn.none')) + '<br><small>' + esc(cyT('gpn.noneSub')) + '</small></p>'
        : '<div class="cy-gpnList">' + rows + '</div>')
      + '<div class="cy-gpnFooter">'
      + '<button type="button" class="cy-gpnBtn" data-cy-gpn-measure>⟳ ' + esc(cyT('gpn.measure')) + '</button>'
      + '<button type="button" class="cy-gpnBtn primary" data-cy-gpn-connect>⚡ ' + esc(cyT('gpn.jackin')) + '</button>'
      + '</div>'
      + '<div class="cy-gpnTel">'
      + '<div class="cy-tabHead"><span>' + esc(cyT('gpn.telTitle')) + '</span><em>' + num(snap.totalEvents) + '</em>'
      + '<button type="button" class="cy-gpnBtn ml-auto" data-cy-telreset>' + esc(cyT('gpn.telemetry.reset')) + '</button></div>'
      + '<div class="cy-gpnTelWrap">'
      + telItem(cyT('gpn.telemetry.switchShort'), 'ok', snap.serverSwitches)
      + telItem(cyT('gpn.telemetry.deathShort'), 'bad', snap.udpDeaths)
      + telItem(cyT('gpn.telemetry.fallbackShort'), 'warn', snap.modeFallbacks)
      + telItem(cyT('gpn.telemetry.recoverShort'), 'ok', snap.recoveries)
      + telItem(cyT('gpn.telemetry.selectShort'), 'sel', snap.modeDecisions)
      + '</div></div>'
      + '<div class="cy-gpnRes">'
      + '<div class="cy-tabHead"><span>' + esc(cyT('gpn.resTitle')) + '</span><em>' + entries.length + '</em>'
      + '<span class="cy-gpnResActs"><button type="button" class="cy-gpnBtn" data-cy-resrefresh>⟳ ' + esc(cyT('gpn.resRefresh')) + '</button>'
      + '<button type="button" class="cy-gpnBtn" data-cy-resclear>' + esc(cyT('gpn.resClear')) + '</button></span></div>'
      + '<div class="cy-resFeed">' + resBody + '</div>'
      + (rlog && rlog.path ? '<p class="cy-resPath"><span>' + esc(cyT('gpn.resPath')) + '</span> <b>' + esc(rlog.path) + '</b></p>' : '')
      + '</div>';
    root.querySelector('[data-cy-gpn-measure]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'gpn_servers_probe' });
      logTerm(cyT('gpn.measure').toUpperCase() + ' → ' + cyT('gpn.servers'), 'sys-msg');
    });
    root.querySelector('[data-cy-gpn-connect]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'gpn_connect' });
      logTerm(cyT('gpn.jackin') + ' → GPN', 'ok-msg');
    });
    root.querySelectorAll('[data-cy-gpn-toggle]').forEach(btn => {
      btn.addEventListener('click', () => {
        if (B) B.postToHost({ action: 'gpn_server_toggle', serverId: btn.dataset.cyGpnToggle, enabled: !btn.classList.contains('on') });
      });
    });
    root.querySelector('[data-cy-telreset]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'reset_gpn_telemetry' });
      logTerm(cyT('gpn.telTitle') + ' → ' + cyT('gpn.telemetry.reset').toUpperCase(), 'sys-msg');
    });
    root.querySelector('[data-cy-resrefresh]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'get_gpn_resilience_log' });
      logTerm(cyT('gpn.resTitle') + ' → ' + cyT('gpn.resRefresh').toUpperCase(), 'sys-msg');
    });
    root.querySelector('[data-cy-resclear]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'clear_gpn_resilience_log' });
      logTerm(cyT('gpn.resTitle') + ' → ' + cyT('gpn.resClear').toUpperCase(), 'sys-msg');
    });
  }

  function cyRenderTabMonitor(S) {
    const root = $('cyTabMonitor');
    if (!root) return;
    const snap = S.monitorSnapshot || {};
    const conns = snap.connections || [];
    const filter = _cyMonFilter.trim().toLowerCase();
    const rows = conns.filter(it => {
      if (_cyMonHideListeners && it.protocol === 'TCP' && it.state === 'Listen') return false;
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
      const opts = [['', cyT('route.assign')], ['vpn', cyT('route.vpn')], ['direct', cyT('route.direct')], ['block', cyT('route.block')], ['warp', cyT('route.warp')]]
        .map(o => '<option value="' + o[0] + '"' + (o[0] === (it.action || '') ? ' selected' : '') + '>' + esc(o[1]) + '</option>').join('');
      const country = [it.countryText, it.asnText].filter(Boolean).join(' · ') || '—';
      return '<div class="cy-monRow' + (hidden ? ' hidden' : '') + '">'
        + '<div class="cy-monProg"><b>' + esc(display) + '</b><span>' + esc(pname) + (it.pid ? ' · ' + esc(String(it.pid)) : '') + '</span></div>'
        + '<span class="cy-routeBadge ' + cls + '">' + esc(label) + '</span>'
        + '<span>' + esc(it.protocol || '—') + '</span>'
        + '<span class="addr" title="' + esc(it.remoteAddress || '') + '">' + esc(it.remoteAddress || '—') + '</span>'
        + '<span class="addr" title="' + esc(country) + '">' + esc(country) + '</span>'
        + '<span>' + esc(it.state || '—') + '</span>'
        + '<select class="cy-routeSel" data-cy-monroute="' + esc(pname) + '" data-cy-mondisplay="' + esc(display) + '">' + opts + '</select>'
        + '</div>';
    };
    const groupHead = (k, count) => {
      const collapsed = _cyMonCollapsed.has(k);
      return '<div class="cy-mgroup' + (collapsed ? ' collapsed' : '') + '" data-cy-mongroup="' + esc(k) + '" role="button" tabindex="0" aria-expanded="' + !collapsed + '"><span class="cy-mchev">' + (collapsed ? '▸' : '▾') + '</span><b>' + esc(k) + '</b><em>' + count + '</em></div>';
    };
    // Grouping mirrors NEXUS/dashboard: route / protocol / state / country / app
    // with collapsible headers; groups are built from the FILTERED rows.
    const groupKey = (it) => {
      if (_cyMonGroup === 'route') return it.routeText || it.routeTag || '—';
      if (_cyMonGroup === 'protocol') return it.protocol || '—';
      if (_cyMonGroup === 'state') return it.state || '—';
      if (_cyMonGroup === 'country') return [it.countryText, it.asnText].filter(Boolean).join(' · ') || '—';
      return it.displayName || it.processName || 'Unknown';
    };
    const groupOpts = CY_MON_GROUP_KEYS.map(o => '<option value="' + o[0] + '"' + (o[0] === _cyMonGroup ? ' selected' : '') + '>' + esc(cyT(o[1])) + '</option>').join('');
    let body;
    if (rows.length === 0) {
      body = '<p class="cy-empty">' + esc(filter ? cyT('mon.noMatch') : cyT('mon.none')) + '</p>';
    } else if (_cyMonGroup === 'none') {
      body = '<div class="cy-monHead"><span>' + esc(cyT('node.colProgram')) + '</span><span>' + esc(cyT('node.colRoute')) + '</span><span>PROTOCOL</span><span>ADDRESS</span><span>COUNTRY</span><span>STATE</span><span></span></div>'
        + '<div class="cy-monRows">' + rows.map(it => monRow(it, false)).join('') + '</div>';
    } else {
      const order = [];
      const grouped = new Map();
      rows.forEach(it => {
        const k = groupKey(it);
        if (!grouped.has(k)) { order.push(k); grouped.set(k, []); }
        grouped.get(k).push(it);
      });
      body = '<div class="cy-monHead"><span>' + esc(cyT('node.colProgram')) + '</span><span>' + esc(cyT('node.colRoute')) + '</span><span>PROTOCOL</span><span>ADDRESS</span><span>COUNTRY</span><span>STATE</span><span></span></div>'
        + '<div class="cy-monRows">' + order.map(k => {
          const list = grouped.get(k);
          const collapsed = _cyMonCollapsed.has(k);
          return groupHead(k, list.length) + list.map(it => monRow(it, collapsed)).join('');
        }).join('') + '</div>';
    }
    root.innerHTML =
      '<div class="cy-tabHead"><span>' + esc(cyT('monitor.title')) + '</span><em>' + rows.length + ' / ' + conns.length + '</em></div>'
      + '<div class="cy-monTools">'
      + '<input type="search" class="cy-monFilter" data-cy-monfilter placeholder="' + esc(cyT('monitor.filter')) + '" value="' + esc(_cyMonFilter) + '">'
      + '<label class="cy-monHide"><input type="checkbox" data-cy-monhide' + (_cyMonHideListeners ? ' checked' : '') + '> ' + esc(cyT('monitor.hideListeners')) + '</label>'
      + '<select class="cy-routeSel cy-monGroup" data-cy-mongroup-sel aria-label="' + esc(cyT('monitor.view.groupBy')) + '">' + groupOpts + '</select>'
      + '</div>'
      + body;
    const f = root.querySelector('[data-cy-monfilter]');
    if (f) f.addEventListener('input', () => { _cyMonFilter = f.value; cyRenderTabMonitor(state()); });
    const hide = root.querySelector('[data-cy-monhide]');
    if (hide) hide.addEventListener('change', () => { _cyMonHideListeners = hide.checked; _cyMonPersist(); cyRenderTabMonitor(state()); });
    const gsel = root.querySelector('[data-cy-mongroup-sel]');
    if (gsel) gsel.addEventListener('change', () => { _cyMonGroup = gsel.value; _cyMonPersist(); cyRenderTabMonitor(state()); });
    root.querySelectorAll('[data-cy-mongroup]').forEach(h => {
      const toggle = () => {
        const k = h.dataset.cyMongroup;
        if (_cyMonCollapsed.has(k)) _cyMonCollapsed.delete(k); else _cyMonCollapsed.add(k);
        cyRenderTabMonitor(state());
      };
      h.addEventListener('click', toggle);
      h.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
    });
    root.querySelectorAll('[data-cy-monroute]').forEach(sel => {
      sel.addEventListener('change', () => {
        if (!sel.value) return;
        if (B) B.postToHost({ action: 'set_app_route', processName: sel.dataset.cyMonroute, displayName: sel.dataset.cyMondisplay, route: sel.value });
        logTerm(cyT('log.routeSet', { route: sel.value.toUpperCase(), app: sel.dataset.cyMondisplay.toUpperCase() }));
      });
    });
  }

  function cyRenderTabAbout(S) {
    const root = $('cyTabAbout');
    if (!root) return;
    const info = S.appInfo || {};
    root.innerHTML =
      '<div class="cy-tabHead"><span>' + esc(cyT('about.title')) + '</span></div>'
      + '<div class="cy-aboutLines">'
      + '<div class="sys-msg">> AO GPN HYBRID PROTOCOL LOADED.</div>'
      + '<div class="sys-msg">> ' + esc(cyT('about.version')) + ': ' + esc(info.version || '—') + '</div>'
      + '<div class="sys-msg">> ' + esc(cyT('about.skin')) + ': CYBER-VOLT // ' + esc((document.body.getAttribute('data-theme') || 'yellow').toUpperCase()) + '</div>'
      + '<div class="sys-msg">> ' + esc(cyT('about.session')) + ': ' + esc(S.sessionTime || '00:00:00') + '</div>'
      + '<div class="sys-msg">> ' + esc(cyT('node')) + ': ' + esc((ensureActiveNode(S) || {}).name || 'LINK PENDING') + '</div>'
      + '<div class="ok-msg">> ' + esc(cyT('about.hint')) + '</div>'
      + '<div class="ok-msg">> ' + esc(cyT('about.hint2')) + '</div>'
      + '</div>';
  }

  // ---- sync ----
  let _lastLogNode = '';

  // Host push handler (skinBridge.subscribe). The host is authoritative: when
  // a fresh monitor snapshot (or any state push) arrives, drop local route
  // overrides whose value the host has confirmed, so the dashboard's route
  // selector and this skin can never drift apart. Overrides for toggles the
  // host hasn't echoed yet survive, keeping the optimistic flip on screen.
  function onHostPush() {
    const S = state();
    const apps = S.monitorSnapshot.apps || [];
    const now = Date.now();
    Object.keys(_localRoutes).forEach(pname => {
      const app = apps.find(a => (a.processName || a.value) === pname);
      if (!app) {
        // App removed from the boost list — drop any stale override.
        delete _localRoutes[pname];
        delete _localRouteTs[pname];
        return;
      }
      const pendingMs = now - (_localRouteTs[pname] || 0);
      if (app.action === _localRoutes[pname]) {
        // Host confirmed the toggle — the authoritative action now matches the
        // override, so the override is redundant and must go.
        delete _localRoutes[pname];
        delete _localRouteTs[pname];
      } else if (pendingMs > ROUTE_CONFIRM_MS) {
        // The echo window expired and the host reports something else (e.g. the
        // route was changed from the dashboard, or the host rejected the
        // toggle). The authoritative snapshot wins — clear the override.
        delete _localRoutes[pname];
        delete _localRouteTs[pname];
      }
    });
    sync();
  }

  function sync() {
    const S = state();
    // Static labels carry translations too; re-apply first so a language
    // switch (via B.setLanguage) updates them on this very pass, then the
    // dynamic renderers fill in the state-dependent parts.
    applyStaticTexts();
    const on = S.connected;
    applyConnectedUI(on);
    renderTelemetry(S);
    renderSources(S);
    renderDests(S);
    renderControls(S);
    cyRenderTab();
    // Live session clock in the console header.
    const sessEl = $('cyTermSession');
    if (sessEl) sessEl.textContent = S.sessionTime || '00:00:00';
    // Log a node change once (host or user switched the active endpoint) so
    // the console mirrors what the destination list is doing.
    const n = ensureActiveNode(S);
    const nodeId = n ? String(nodeKey(n, S.useRealNodes)) : '';
    if (nodeId && nodeId !== _lastLogNode && _wired) {
      _lastLogNode = nodeId;
      logTerm(cyT('log.nodeSync', { node: String(nodeLabel(n, S.useRealNodes)).toUpperCase() }), 'sys-msg');
    }
    // keep selections valid
    const srcIdx = parseInt(String(_src).replace('src', ''), 10) || 0;
    const dstIdx = parseInt(String(_dst).replace('dst', ''), 10) || 0;
    $('cyFrame').setAttribute('data-source', srcIdx <= 2 ? _src : 'src0');
    $('cyFrame').setAttribute('data-dest', dstIdx <= 2 ? _dst : 'dst0');
    const nodeBadge = $('cyNodeLabel');
    if (nodeBadge) {
      // The header badge follows the program's real active endpoint (with the
      // same first-node fallback as the destination list), so it never reads
      // "LINK PENDING" once any route node exists.
      nodeBadge.textContent = '[ ' + cyT('node') + ': ' + (n ? (n.name || n.address || n.indexId || n.key || '').toUpperCase() : 'LINK PENDING') + ' ]';
    }
    layoutSourceTraces();
  }

  // ---- global keyboard shortcuts (shared across all skins) ----
  //   Ctrl+Enter -> JACK IN / disconnect (same toggle as the power button)
  //   Alt+1..5   -> console tabs (terminal / nodes / gpn / monitor / about)
  //   R          -> cycle the focused boost app's route (vpn/direct/block/warp)
  // The exact same set exists in NEXUS (Alt+1..8 for its views) and INFRA.
  const CY_TAB_KEYS = ['terminal', 'nodes', 'gpn', 'monitor', 'about'];
  let _cyShortcutsBound = false;
  function cyBindShortcuts() {
    if (_cyShortcutsBound) return;
    _cyShortcutsBound = true;
    document.addEventListener('keydown', (e) => {
      // Connect / disconnect — safe in every context, including inputs.
      if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'Enter' || e.key === 'NumpadEnter')) {
        e.preventDefault();
        const power = $('cyPower');
        if (power) power.click();
        return;
      }
      const typing = e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.target.tagName === 'TEXTAREA' || e.target.isContentEditable);
      // Tab switching.
      if (e.altKey && !e.ctrlKey && !e.metaKey && !typing && /^[1-5]$/.test(e.key)) {
        e.preventDefault();
        cySwitchTab(CY_TAB_KEYS[Number(e.key) - 1]);
        return;
      }
      // Route cycle on the focused boost app card (row or its switch).
      if (!typing && !e.altKey && !e.ctrlKey && !e.metaKey && (e.key === 'r' || e.key === 'R')) {
        const row = document.activeElement && document.activeElement.closest
          ? document.activeElement.closest('[data-cy-src]')
          : null;
        if (row && row.dataset && row.dataset.cyPname) {
          const pname = row.dataset.cyPname;
          const S = state();
          const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === pname);
          const cur = actionFor(app || { processName: pname });
          const next = cyRouteCycle(cur);
          e.preventDefault();
          setAppRoute(pname, next);
        }
      }
    });
  }

  // The CYBER stage is a fixed 1400x800 canvas; scale it down (never up) so
  // the whole interface is visible in any window: the app's 1200px-wide host,
  // the manager's thumbnail preview, small monitors. The body flex-centers the
  // frame, so a centered scale keeps everything on-screen.
  function cyFitCanvas() {
    const frame = document.querySelector('.cy-frame');
    if (!frame) return;
    const scale = Math.min(1, window.innerWidth / 1400, window.innerHeight / 800);
    frame.style.transform = scale < 1 ? 'scale(' + scale + ')' : '';
  }
  window.addEventListener('resize', cyFitCanvas);

  function boot() {
    cyFitCanvas();
    wire();
    cyBindShortcuts();
    ['cyLanguage', 'cyHeaderLang'].forEach(id => {
      const langSel = $(id);
      if (langSel) {
        const S0 = state();
        langSel.innerHTML = cyOpts(cyLangs(), S0.language);
      }
    });
    const power = $('cyPower');
    if (power) power.setAttribute('aria-label', cyT('connectAria'));
    if (CY_PREVIEW) {
      // Static connected snapshot for the manager thumbnail; no bridge sub, no timers.
      DEMO.connected = true;
      DEMO.telemetry = [22, 3, 0.5, 0.2];
      DEMO.selectedNode = (DEMO.nodes || [])[0] || null;
      DEMO.monitorSnapshot = { apps: [{ displayName: 'Escape from Tarkov' }, { displayName: 'League of Legends' }, { displayName: 'Counter-Strike 2' }] };
      document.body.classList.add('cy-preview');
      applyConnectedUI(true);
      sync();
      logTerm('> ACCESS GRANTED. ELECTRIC TRACES SECURED.', 'ok-msg');
      return;
    }
    sync();
    const S = state();
    // If the app is already connected when this skin opens (e.g. the user
    // switched to CYBER mid-session), reflect the real state instead of the
    // static "vulnerable" boot line.
    if (S.connected) {
      const vuln = $('cyBootVuln');
      if (vuln) vuln.remove();
      logTerm(cyT('console.restored'), 'ok-msg');
    }
    if (B && typeof B.subscribe === 'function') B.subscribe(onHostPush);
    setInterval(() => { if (document.visibilityState !== 'hidden') sync(); }, 2000);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();