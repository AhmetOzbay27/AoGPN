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
    'mode.vpn': 'GLOBAL VPN',
    'transport.proxy': 'PROXY',
    'transport.tun': 'TUN',
    'ctl.autoReconnect': 'AUTO RECONNECT',
    'ctl.systemProxy': 'SYSTEM PROXY',
    'ctl.proxyMode': 'PROXY MODE',
    'ctl.protocol': 'PROTOCOL',
    'ctl.language': 'LANGUAGE',
    'split.off': 'OFF',
    'split.vpn': 'GLOBAL VPN',
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
    'log.off': 'OFF'
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
      language: G('language') || 'en', isAdmin: G('isAdmin')
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
  const SRC_CX = 645; // core entry x (viewBox units)
  const SRC_CY = 400; // core entry y

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
    // reads as linking, not broken. Once disconnected, ERR is honest.
    const idle = on ? '--' : 'ERR';
    if (pingEl) pingEl.innerHTML = (ping != null ? ping : idle) + ' <span class="cy-unit">ms</span>';
    if (lossEl) lossEl.innerHTML = (loss != null ? loss.toFixed(1) : (on ? '0.0' : idle)) + ' <span class="cy-unit">%</span>';
    if (downEl) downEl.innerHTML = (down != null ? down.toFixed(1) : '--') + ' <i>Mbps ⇣</i>';
    if (upEl) upEl.innerHTML = (up != null ? up.toFixed(1) : '--') + ' <i>Mbps ⇡</i>';
    const bp = $('cyBarPing'), bl = $('cyBarLoss'), bd = $('cyBarDown');
    if (bp) bp.style.width = (ping != null ? Math.min(100, ping) : 0) + '%';
    if (bl) bl.style.width = (loss != null ? Math.min(100, loss * 2) : 0) + '%';
    if (bd) bd.style.width = (down != null ? Math.min(100, Math.max(4, down * 2)) : 0) + '%';
    const sess = $('cySession'), exit = $('cyExitIp');
    if (sess) sess.textContent = S.sessionTime || '00:00:00';
    if (exit) exit.textContent = (on && S.exitIp && S.exitIp !== 'Not verified') ? S.exitIp : '—';
  }

  // ---- connect (JACK IN) ----
  function jackIn() {
    const S = state();
    if (S.connected) {
      if (B) {
        const ok = B.postToHost({ action: 'toggle_connection', mode: S.mode, transport: S.transport, protocol: S.protocolPreference });
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
        const ok = B.postToHost({ action: 'toggle_connection', mode: S.mode, transport: S.transport, protocol: S.protocolPreference });
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
    const lang = $('cyLanguage');
    if (lang) lang.addEventListener('change', () => {
      const next = String(lang.value || 'en');
      // Same channel as the top-bar picker / NEXUS settings: switch the whole
      // app (main dashboard AND every loaded skin) instantly and persist
      // through the host (set_language).
      if (B && typeof B.setLanguage === 'function') B.setLanguage(next);
      SET('language', next);
      sync();
      logTerm(cyT('log.language', { lang: String(next).toUpperCase() }), 'sys-msg');
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
    const pl = $('cyLanguage');
    if (pl) pl.value = S.language || 'en';
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

  function boot() {
    wire();
    const langSel = $('cyLanguage');
    if (langSel) {
      const S0 = state();
      langSel.innerHTML = cyOpts(cyLangs(), S0.language);
    }
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