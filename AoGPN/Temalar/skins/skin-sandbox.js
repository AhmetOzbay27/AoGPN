// ============================================================================
// Shared test sandbox for the standalone skins (CYBER, NEXUS) — jsdom edition.
// ----------------------------------------------------------------------------
// Loads the REAL skin.html document into jsdom and boots the REAL skin.js in
// that document (as the iframe would), with a bridge stub and a controllable
// clock. Tests therefore exercise actual DOM behavior: real innerHTML parsing,
// real querySelector/classList/dataset, real event listeners, and real event
// propagation through the tree — not a hand-rolled stub of it.
//
// Used by:
//   route-keys.integration.test.js  — keydown listener contract
//   route-echo.integration.test.js  — host-echo override cleanup thresholds
// ============================================================================
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');

// ---------------------------------------------------------------------------
// Bridge stub — mirrors the parent dashboard's skinBridge surface the skins use
// ---------------------------------------------------------------------------

function makeBridge(data) {
  const bridge = {
    _posts: [],
    _subscribers: [],
    connected: false,
    connecting: false,
    mode: 'gpn',
    transport: 'proxy',
    splitMode: 'off',
    autoReconnect: true,
    systemProxyMode: 0,
    protocolPreference: 'auto',
    useRealNodes: false,
    activeRealNodeId: null,
    selectedNode: null,
    monitorSnapshot: { apps: [] },
    routeLabels: { vpn: 'VPN', 'vpn+proxy': 'VPN + Proxy', direct: 'Direct', block: 'Block' },
    nodes: [{ key: 'istanbul', code: 'TR', name: 'Istanbul · TR-01', addr: '145.239.14.77', ping: 18 }],
    telemetry: [null, null, null, null],
    sessionTime: '00:00:00',
    exitIp: 'Not verified',
    exitCountry: '',
    language: 'en',
    isAdmin: true,
    LANGUAGES: [{ code: 'en', name: 'English' }],
    postToHost(msg) { bridge._posts.push(msg); return true; },
    subscribe(cb) { bridge._subscribers.push(cb); },
    applySkin() {},
    setConnected() {},
    requestNodeSwitch() {},
    applyNode() {},
    setLanguage() {},
    t(key, params) {
      let v = data && data.t && data.t[key];
      if (v == null) v = key;
      if (params) Object.keys(params).forEach(k => { v = String(v).split('{' + k + '}').join(String(params[k])); });
      return String(v);
    },
    langName() { return 'English'; }
  };
  Object.assign(bridge, data || {});
  // Clone apps so tests mutating bridge.monitorSnapshot.apps never leak between cases.
  if (bridge.monitorSnapshot && Array.isArray(bridge.monitorSnapshot.apps)) {
    bridge.monitorSnapshot = {
      ...bridge.monitorSnapshot,
      apps: bridge.monitorSnapshot.apps.map(a => ({ ...a }))
    };
  }
  return bridge;
}

// ---------------------------------------------------------------------------
// Sandbox loader
// ---------------------------------------------------------------------------

// Boot a skin inside a jsdom window hosting the skin's REAL skin.html, with a
// stubbed bridge and a controllable clock (Date.now is faked and advanced
// through sandbox.advance(ms)).
function loadSkin(skinDir, bridgeData, opts) {
  const clock = { now: (opts && typeof opts.now === 'number') ? opts.now : 1_000_000 };
  const bridge = makeBridge(bridgeData);

  // The real static document: containers, static buttons and panels are all
  // present, exactly as the iframe renders them.
  const html = fs.readFileSync(path.join(__dirname, skinDir, 'skin.html'), 'utf8');

  // The URL deliberately has no ?preview=1 query so the skins boot in their
  // LIVE (bridge-connected) mode, not the thumbnail snapshot.
  const dom = new JSDOM(html, {
    runScripts: 'outside-only',
    pretendToBeVisual: true,
    url: 'http://localhost/skins/' + skinDir + '/skin.html'
  });
  const w = dom.window;
  const d = w.document;

  // The skins read window.parent.skinBridge and only use it when
  // window.parent !== window; jsdom's own parent === window, so override it
  // with a fake parent hosting the bridge stub.
  Object.defineProperty(w, 'parent', {
    value: { skinBridge: bridge },
    configurable: true,
    writable: true
  });

  // Fake Date: Date.now() reads clock.now; constructor falls back to the real
  // clock so any incidental `new Date(...)` still behaves.
  const RealDate = Date;
  function FakeDate(...args) {
    if (new.target) return args.length ? new RealDate(...args) : new RealDate(clock.now);
    return new RealDate(...args).toString();
  }
  FakeDate.now = () => clock.now;
  FakeDate.parse = RealDate.parse;
  FakeDate.UTC = RealDate.UTC;
  w.Date = FakeDate;

  // No real timers in tests: the 2 s safety poll and log typewriters must not
  // keep the process alive or run concurrently with the test steps. Intervals
  // are RECORDED instead of run (like loadDashboard's tickIntervals), so tests
  // can drive the CYBER console typewriter deterministically via tickTimers().
  const _intervals = {};
  let _intervalSeq = 0;
  w.setInterval = (fn) => { _intervals[++_intervalSeq] = fn; return _intervalSeq; };
  w.clearInterval = (id) => { delete _intervals[id]; };
  w.setTimeout = () => 1;
  w.clearTimeout = () => {};
  w.requestAnimationFrame = () => 1;

  // Capture the boot handler instead of relying on jsdom's own
  // DOMContentLoaded timing, so boot runs exactly once, deterministically.
  let bootHandler = null;
  const origAdd = d.addEventListener.bind(d);
  d.addEventListener = (type, fn, ...rest) => {
    if (type === 'DOMContentLoaded') bootHandler = fn;
    return origAdd(type, fn, ...rest);
  };

  // Load the shared pure module first so the skins use the real routeFromKey.
  w.eval(fs.readFileSync(path.join(__dirname, 'route-keys.js'), 'utf8'));
  if (!w.AoGPNRouteKeys || typeof w.AoGPNRouteKeys.routeFromKey !== 'function') {
    throw new Error('route-keys.js must expose AoGPNRouteKeys.routeFromKey in the sandbox');
  }

  // Boot the real skin against the real document.
  w.eval(fs.readFileSync(path.join(__dirname, skinDir, 'skin.js'), 'utf8'));
  if (bootHandler) bootHandler();
  else d.dispatchEvent(new w.Event('DOMContentLoaded', { bubbles: true }));

  const isCyber = () => skinDir === 'cyber';
  const root = () => d.getElementById(isCyber() ? 'cySources' : 'nxGameList');

  // CYBER: card rows carry data-cy-pname; the switch is the .cy-switch child.
  // NEXUS: switches carry data-nx-pname directly (row = .nx-game ancestor).
  function findRow(pname) {
    const r = root();
    if (!r) return null;
    if (isCyber()) {
      return Array.from(r.querySelectorAll('[data-cy-src]')).find(c => c.dataset.cyPname === pname) || null;
    }
    const sw = r.querySelector('.nx-switch[data-nx-pname="' + pname.replace(/"/g, '\\"') + '"]');
    return sw ? sw.closest('.nx-game') : null;
  }
  function findSwitch(pname) {
    const row = findRow(pname);
    if (!row) return null;
    return isCyber() ? row.querySelector('.cy-switch') : row.querySelector('.nx-switch[data-nx-pname]');
  }
  // Route label of a row. CYBER renders "<span class=cy-live-tag>● RUNNING</span>
  // VPN <span class=cy-lat>24 ms</span>" inside the sub span, so the label is the
  // concatenated TEXT nodes (excluding nested element content). NEXUS renders
  // "VPN · RUNNING" in a single span, so the label is the part before " · ".
  function labelOf(row) {
    if (!row) return null;
    if (isCyber()) {
      const sub = row.querySelector('h4 + span');
      if (!sub) return null;
      const text = Array.from(sub.childNodes)
        .filter(n => n.nodeType === 3) // TEXT_NODE
        .map(n => n.textContent)
        .join('')
        .trim();
      return text || null;
    }
    const sub = row.querySelector('span');
    return sub ? sub.textContent.split(' · ')[0].trim() : null;
  }

  return {
    dom,
    window: w,
    document: d,
    bridge,
    clock,
    // Advance the fake clock (used by the route-override echo window).
    advance(ms) { clock.now += ms; },
    // Simulate a host push: invokes the real onHostPush / nxOnHostPush the skin
    // registered through bridge.subscribe at boot.
    push() { bridge._subscribers.forEach(cb => { cb(); }); },
    getSwitch(pname) { return findSwitch(pname); },
    getLabel(pname) { return labelOf(findRow(pname)); },
    getSwitchOn(pname) {
      const sw = findSwitch(pname);
      return sw ? sw.classList.contains('on') : null;
    },
    keydown(sw, key) {
      sw.dispatchEvent(new w.KeyboardEvent('keydown', {
        key,
        bubbles: true,
        cancelable: true
      }));
      return { prevented: false };
    },
    // Fire every recorded interval callback once (the CYBER console typewriter
    // and the 2 s safety poll). Returns how many callbacks ran.
    tickTimers() {
      const fns = Object.values(_intervals);
      fns.forEach(fn => fn());
      return fns.length;
    },
    close() { dom.window.close(); }
  };
}

// ---------------------------------------------------------------------------
// Dashboard sandbox
// ---------------------------------------------------------------------------
// Boots the REAL main dashboard (vpn-gpn-dashboard.html + Temalar/app.js) in a
// jsdom window, exactly as the WebView2 host renders it. The host channel is
// stubbed: window.chrome.webview.postMessage captures every message the app
// posts, window.confirm is controllable, and window.fetch serves the real
// Temalar/themes.json, Dil/*.json and Temalar/skins.json from disk so the app
// boots with real language dictionaries and the skin registry.
//
// Used by:
//   dashboard.integration.test.js — boost table, add_app/remove_app, route select
// ---------------------------------------------------------------------------

const DASHBOARD_ROOT = path.join(__dirname, '..', '..'); // AoGPN/ (html, Temalar/, Dil/)

// The dashboard markup is large (~200 KB) and jsdom re-parses it on every
// boot, so pre-shrink it once and reuse the cached string across tests:
//   - the cursor-trail / matrix-rain canvases are dropped (initCursorEffects
//     short-circuits when they are absent, so the whole animation machinery
//     never runs in tests),
//   - comments and whitespace between tags are stripped (no layout impact;
//     jsdom tokenizes every boot).
let _dashboardHtmlCache = null;
function dashboardHtml() {
  if (_dashboardHtmlCache) return _dashboardHtmlCache;
  let html = fs.readFileSync(path.join(DASHBOARD_ROOT, 'vpn-gpn-dashboard.html'), 'utf8');
  html = html
    .replace(/<canvas[^>]*id="cursorCanvas"[^>]*>\s*<\/canvas>/gi, '')
    .replace(/<canvas[^>]*id="matrixRainCanvas"[^>]*>\s*<\/canvas>/gi, '')
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/>\s+</g, '><');
  _dashboardHtmlCache = html;
  return _dashboardHtmlCache;
}

async function loadDashboard(opts) {
  const clock = { now: (opts && typeof opts.now === 'number') ? opts.now : 1_000_000 };
  const posts = [];

  const dom = new JSDOM(dashboardHtml(), {
    runScripts: 'outside-only',
    url: 'http://localhost/vpn-gpn-dashboard.html'
  });
  const w = dom.window;
  const d = w.document;

  // ---- host channel stub: capture every message postToHost sends ----
  w.chrome = {
    webview: {
      postMessage(json) { posts.push(JSON.parse(json)); return true; }
    }
  };

  // ---- confirm() is controllable so the delete-button path can be tested ----
  w.confirm = () => true;

  // ---- fake Date (controllable clock) ----
  const RealDate = Date;
  function FakeDate(...args) {
    if (new.target) return args.length ? new RealDate(...args) : new RealDate(clock.now);
    return new RealDate(...args).toString();
  }
  FakeDate.now = () => clock.now;
  FakeDate.parse = RealDate.parse;
  FakeDate.UTC = RealDate.UTC;
  w.Date = FakeDate;

  // ---- no real timers in tests ----
  // Intervals are RECORDED instead of run: the only app.js interval is the
  // session timer (tickSession every 1 s), so tests can fire it deterministically
  // through dash.tickIntervals() and assert the session clock advances.
  // The app's performance probe registers an extra 10 s interval (and needs a
  // real rAF) — it is irrelevant to the dashboard contract, so keep it off here.
  w.__aogpnPerfDisabled = true;
  const _intervals = {};
  let _intervalSeq = 0;
  w.setInterval = (fn) => { _intervals[++_intervalSeq] = fn; return _intervalSeq; };
  w.clearInterval = (id) => { delete _intervals[id]; };
  w.setTimeout = () => 1;
  w.clearTimeout = () => {};
  w.requestAnimationFrame = () => 1;

  // ---- fetch stub: serve the real config/lang/registry files from disk ----
  w.fetch = (url) => {
    let rel;
    try { rel = new URL(String(url), w.location.href).pathname.replace(/^\//, ''); }
    catch { rel = String(url); }
    const file = path.join(DASHBOARD_ROOT, rel);
    try {
      const body = fs.readFileSync(file, 'utf8');
      return Promise.resolve({
        ok: true,
        status: 200,
        json: async () => JSON.parse(body),
        text: async () => body
      });
    } catch {
      return Promise.resolve({ ok: false, status: 404, json: async () => { throw new Error('not found: ' + rel); }, text: async () => '' });
    }
  };

  if (typeof w.matchMedia !== 'function') {
    w.matchMedia = (query) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} });
  }

  // Canvas 2D stub: the theme engine's cursor-trail and matrix-rain effects
  // call getContext('2d') at boot; jsdom needs the optional canvas package for
  // that, so provide a minimal context that satisfies the drawing calls.
  const Canvas2D = () => ({
    canvas: null,
    fillRect() {}, strokeRect() {}, clearRect() {},
    fillText() {}, strokeText() {}, measureText() { return { width: 0 }; },
    beginPath() {}, closePath() {}, moveTo() {}, lineTo() {}, bezierCurveTo() {}, quadraticCurveTo() {},
    arc() {}, arcTo() {}, rect() {}, fill() {}, stroke() {}, clip() {}, save() {}, restore() {},
    translate() {}, scale() {}, rotate() {}, transform() {}, setTransform() {}, resetTransform() {},
    createLinearGradient() { return { addColorStop() {} }; }, createRadialGradient() { return { addColorStop() {} }; },
    createPattern() { return null; }, drawImage() {},
    getImageData() { return { data: new Uint8ClampedArray(4), width: 1, height: 1 }; },
    putImageData() {}, createImageData() { return { data: new Uint8ClampedArray(4), width: 1, height: 1 }; },
    get fillStyle() { return ''; }, set fillStyle(v) {},
    get strokeStyle() { return ''; }, set strokeStyle(v) {},
    get font() { return ''; }, set font(v) {},
    get textAlign() { return 'start'; }, set textAlign(v) {},
    get textBaseline() { return 'alphabetic'; }, set textBaseline(v) {},
    get globalAlpha() { return 1; }, set globalAlpha(v) {},
    get globalCompositeOperation() { return 'source-over'; }, set globalCompositeOperation(v) {},
    get lineWidth() { return 1; }, set lineWidth(v) {},
    get lineCap() { return 'butt'; }, set lineCap(v) {},
    get lineJoin() { return 'miter'; }, set lineJoin(v) {},
    get shadowBlur() { return 0; }, set shadowBlur(v) {},
    get shadowColor() { return ''; }, set shadowColor(v) {},
    get shadowOffsetX() { return 0; }, set shadowOffsetX(v) {},
    get shadowOffsetY() { return 0; }, set shadowOffsetY(v) {}
  });
  if (w.HTMLCanvasElement && w.HTMLCanvasElement.prototype) {
    w.HTMLCanvasElement.prototype.getContext = () => Canvas2D();
  }

  // Boot the real dashboard (IIFE runs immediately on eval).
  w.eval(fs.readFileSync(path.join(__dirname, '..', 'app.js'), 'utf8'));

  // Let the async startup chains (themes.json, Dil/*.json, skins.json) settle.
  // A single macrotask flush is enough: every fetch resolves immediately and
  // the dependent microtasks finish within the same event-loop turn.
  await new Promise(res => setImmediate(res));

  const boostBody = () => d.getElementById('splitAppsBody');
  const postsWith = (action) => posts.filter(p => p && p.action === action);

  return {
    dom,
    window: w,
    document: d,
    posts,
    clock,
    advance(ms) { clock.now += ms; },
    postsWith,
    openView(view) {
      const el = d.querySelector('[data-view="' + view + '"]');
      if (el) el.dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
      return !!el;
    },
    updateMonitorSnapshot(data) { w.updateMonitorSnapshot(data); },
    setConfirm(v) { w.confirm = () => !!v; },
    rows() { return Array.from((boostBody() || { querySelectorAll: () => [] }).querySelectorAll('tr[data-process-name]')); },
    getRouteSelect(pname) { return boostBody().querySelector('select[data-route-process="' + pname + '"]'); },
    getRemoveBtn(pname) { return boostBody().querySelector('button[data-remove-process="' + pname + '"]'); },
    selectRoute(pname, route) {
      const sel = this.getRouteSelect(pname);
      if (!sel) return false;
      sel.value = route;
      sel.dispatchEvent(new w.Event('change', { bubbles: true }));
      return true;
    },
    removeApp(pname) {
      const btn = this.getRemoveBtn(pname);
      if (!btn) return false;
      btn.dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
      return true;
    },
    addApp() {
      const btn = d.getElementById('boostAddAppBtn');
      if (!btn) return false;
      btn.dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
      return true;
    },
    // Fire every recorded interval callback once (the session timer). Returns
    // how many callbacks ran — 0 once the timer is cleared (disconnect).
    tickIntervals() {
      const fns = Object.values(_intervals);
      fns.forEach(fn => fn());
      return fns.length;
    },
    close() { dom.window.close(); }
  };
}

module.exports = { loadSkin, loadDashboard };
