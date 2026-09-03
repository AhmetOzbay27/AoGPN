// ============================================================================
// Dashboard style regression test: critical main-dashboard CSS (jsdom + cssom)
// ----------------------------------------------------------------------------
// Same two-layer pattern as skins-style.test.js, applied to the MAIN dashboard
// (vpn-gpn-dashboard.html) instead of the standalone skins:
//
//   1. cssom (rrweb-cssom): the embedded stylesheet chain is extracted from the
//      real dashboard html (base + components + themes, the exact rules the
//      packaged app renders) and the critical declarations asserted at the rule
//      level — route badge colours, the CONNECT ring connected state, active /
//      locked pills and the :root palette.
//
//   2. jsdom getComputedStyle: the real html already carries its stylesheet
//      (the #embedded-dashboard-css block), so computed styles resolve exactly
//      as in the WebView2 host — literal colours (jsdom cannot resolve var() /
//      color-mix()) and state-class cascades are asserted on real elements.
//
// Run with:
//   node --test Temalar/skins/dashboard-style.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const CSSOM = require('rrweb-cssom');
const { JSDOM } = require('jsdom');

const DASHBOARD_HTML = path.join(__dirname, '..', '..', 'vpn-gpn-dashboard.html');

// ---------------------------------------------------------------------------
// cssom layer — rule-level assertions against the embedded stylesheet chain
// ---------------------------------------------------------------------------

// The rules the app actually renders: base + components + themes embedded in
// the dashboard html (identical to Temalar/dashboard.css per the header note).
function dashboardCss() {
  const html = fs.readFileSync(DASHBOARD_HTML, 'utf8');
  const m = html.match(/<style id="embedded-dashboard-css">([\s\S]*?)<\/style>/);
  assert.ok(m, 'embedded-dashboard-css block must exist in the dashboard html');
  return m[1];
}

function parseSheet(cssText) { return CSSOM.parse(cssText); }

function ruleFor(sheet, selector) {
  const walk = (rules) => {
    for (const r of rules || []) {
      if (r.selectorText === selector) return r;
      if (r.cssRules) {
        const hit = walk(r.cssRules);
        if (hit) return hit;
      }
    }
    return null;
  };
  return walk(sheet.cssRules);
}

const decl = (sheet, selector, prop) => {
  const r = ruleFor(sheet, selector);
  return r ? r.style.getPropertyValue(prop) : null;
};

// ---------------------------------------------------------------------------
// jsdom layer — computed styles against the real dashboard document
// ---------------------------------------------------------------------------
// CAVEAT: jsdom's computed-style cascade is order-based (it does not implement
// CSS specificity or @layer), while the real page relies on both — e.g. Tailwind's
// preflight (* { border-width:0; border-color:#e5e7eb }) lives in a @layer base so
// it loses to component rules in the browser, but wins in jsdom. The jsdom
// assertions below therefore stick to properties with no competing later rule
// (colours, opacity) and to elements the preflight does not reset (divs over
// buttons); specificity-dependent values are asserted in the cssom layer above.

function styledDashboard() {
  const dom = new JSDOM(fs.readFileSync(DASHBOARD_HTML, 'utf8'), {
    url: 'http://localhost/vpn-gpn-dashboard.html'
  });
  return dom;
}

const computed = (dom, el) => dom.window.getComputedStyle(el);

// ---------------------------------------------------------------------------
// Route badges — one distinct colour per route state
// ---------------------------------------------------------------------------

test('cssom: dashboard route badges carry distinct colours per route', () => {
  const sheet = parseSheet(dashboardCss());
  assert.equal(decl(sheet, '.route-badge.proxy', 'color'), '#A5F3FC', 'proxy = cyan-tinted');
  assert.equal(decl(sheet, '.route-badge.direct', 'color'), '#A7F3D0', 'direct = emerald-tinted');
  assert.equal(decl(sheet, '.route-badge.block', 'color'), '#FCA5A5', 'block = red');
  assert.equal(decl(sheet, '.route-badge.block', 'background'), 'rgba(239,68,68,.12)');
  assert.equal(decl(sheet, '.route-badge.block', 'border-color'), 'rgba(239,68,68,.24)');
  assert.equal(decl(sheet, '.route-badge.unknown', 'color'), '#CBD5E1');
  assert.equal(decl(sheet, '.route-badge.unknown', 'background'), 'rgba(148,163,184,.1)');
});

test('jsdom: route badge block computes the red state on a real element', () => {
  const dom = styledDashboard();
  const d = dom.window.document;
  const badge = d.createElement('span');
  badge.className = 'route-badge block';
  d.body.appendChild(badge);
  assert.equal(computed(dom, badge).color, 'rgb(252, 165, 165)');
  assert.equal(computed(dom, badge).backgroundColor, 'rgba(239, 68, 68, 0.12)');
  // border-color is asserted at the cssom level: jsdom lets the Tailwind
  // preflight (@layer base in the browser) override it via source order.
  dom.window.close();
});

// ---------------------------------------------------------------------------
// Connected state — the CONNECT ring re-tints when body.connected is set
// ---------------------------------------------------------------------------

test('cssom: dashboard connected state re-tints the CONNECT ring', () => {
  const sheet = parseSheet(dashboardCss());
  assert.equal(decl(sheet, '.ring-orbit i', 'background'), 'var(--cyan)', 'idle orbit dot cyan');
  assert.equal(decl(sheet, 'body.connected .ring-orbit i', 'background'), 'var(--emerald)', 'connected orbit dot emerald');
  assert.equal(decl(sheet, 'body.connected .ring-core', 'border-color'), 'rgba(var(--emerald-rgb),.20)');
  assert.equal(decl(sheet, 'body.connected .ring-halo', 'background'),
    'radial-gradient(circle, color-mix(in srgb, var(--emerald) 20%, transparent), transparent 62%)');
  assert.equal(decl(sheet, 'body.connected .ring-orbit.r2 i', 'background'), '#A7F3D0', 'second orbit dot light mint');
});

test('jsdom: adding body.connected flips the orbit dot to the literal mint colour', () => {
  const dom = styledDashboard();
  const d = dom.window.document;
  const orbit = d.createElement('div');
  orbit.className = 'ring-orbit r2';
  const dot = d.createElement('i');
  orbit.appendChild(dot);
  d.body.appendChild(orbit);
  assert.equal(computed(dom, dot).backgroundColor, 'rgba(0, 0, 0, 0)', 'idle dot is var(--violet) — unresolvable, so transparent');

  d.body.classList.add('connected');
  assert.equal(computed(dom, dot).backgroundColor, 'rgb(167, 243, 208)',
    'connected dot resolves the literal #A7F3D0 from the state rule');
  dom.window.close();
});

// ---------------------------------------------------------------------------
// Active / locked pills and the disabled connect button
// ---------------------------------------------------------------------------

test('cssom: active and locked pill states are visually distinct', () => {
  const sheet = parseSheet(dashboardCss());
  // Active pills: violet→cyan gradient + white text.
  assert.equal(decl(sheet, '.mode-pill.active', 'color'), '#fff');
  assert.equal(decl(sheet, '.split-mode-pill.active', 'color'), '#fff');
  assert.ok(decl(sheet, '.mode-pill.active', 'background').includes('linear-gradient'), 'active pill is a gradient');
  assert.ok(decl(sheet, '.split-mode-pill.active', 'background').includes('linear-gradient'));
  // Locked transport pill (TUN without elevation): amber, forced via !important.
  assert.equal(decl(sheet, '.transport-pill.locked', 'color'), '#FBBF24');
  assert.equal(decl(sheet, '.transport-pill.locked', 'background'), 'rgba(245, 158, 11, .10)');
  assert.equal(decl(sheet, '.transport-pill.locked', 'border-color'), 'rgba(245, 158, 11, .45)');
  // Disabled connect button.
  assert.equal(decl(sheet, '#connectBtn:disabled', 'opacity'), '.45');
  assert.equal(decl(sheet, '#connectBtn:disabled', 'cursor'), 'not-allowed');
});

test('jsdom: locked transport pill computes the amber warning', () => {
  const dom = styledDashboard();
  const d = dom.window.document;
  // A div, not a button: the Tailwind preflight resets button backgrounds and
  // jsdom has no @layer to demote it, so a real <button> would win the cascade.
  const pill = d.createElement('div');
  pill.className = 'transport-pill locked';
  d.body.appendChild(pill);
  assert.equal(computed(dom, pill).color, 'rgb(251, 191, 36)', '#FBBF24');
  assert.equal(computed(dom, pill).backgroundColor, 'rgba(245, 158, 11, 0.1)');
  dom.window.close();
});

test('jsdom: the real CONNECT button dims when disabled', () => {
  const dom = styledDashboard();
  const d = dom.window.document;
  const btn = d.getElementById('connectBtn');
  assert.ok(btn, 'connect button exists in the real html');
  assert.notEqual(computed(dom, btn).opacity, '0.45', 'enabled button is fully visible');
  btn.disabled = true;
  assert.equal(computed(dom, btn).opacity, '0.45');
  // cursor: not-allowed is asserted at the cssom level — the later .ring-core
  // rule (cursor: pointer) wins in jsdom's order-based cascade.
  dom.window.close();
});

// ---------------------------------------------------------------------------
// Skin host — body[data-skin] swaps the standard dashboard for a skin iframe
// ---------------------------------------------------------------------------
// While a non-standard skin is active the host hides the classic UI and its
// full-screen FX and shows the #skinHost iframe. The rules live in the
// dedicated #skin-host-css block of the real dashboard html (same block the
// packaged app renders).

function skinHostCss() {
  const html = fs.readFileSync(DASHBOARD_HTML, 'utf8');
  const m = html.match(/<style id="skin-host-css">([\s\S]*?)<\/style>/);
  assert.ok(m, 'skin-host-css block must exist in the dashboard html');
  return m[1];
}

// The Tailwind layer: the frozen utility CSS (preflight + utilities) embedded
// in the dashboard html. The local Temalar/tailwind.js runtime generates the
// same rules for dynamically-added classes — the embedded block is its
// offline-identical snapshot, so scanning it scans the Tailwind layer.
function tailwindUtilsCss() {
  const html = fs.readFileSync(DASHBOARD_HTML, 'utf8');
  const m = html.match(/<style id="embedded-tailwind-utils">([\s\S]*?)<\/style>/);
  assert.ok(m, 'embedded-tailwind-utils block must exist in the dashboard html');
  return m[1];
}

// ---------------------------------------------------------------------------
// Layout axis — 'compact' shows the hero strip, and the CONNECT ring stays
// top-aligned with the (much taller) GPN panel instead of floating in the
// empty space left by vertical centering
// ---------------------------------------------------------------------------

test('cssom: compact layout hides the sidebar controls and forces the hero strip', () => {
  const sheet = parseSheet(dashboardCss());
  // Sidebar Connection panel disappears in compact mode.
  assert.equal(decl(sheet, 'body[data-layout="compact"] .layout-sidebar-conn', 'display'), 'none');
  // The hero strip is forced visible with !important so it wins over the
  // md:hidden utility (display:none at >=768px) at desktop width.
  assert.equal(decl(sheet, 'body[data-layout="compact"] .layout-hero-strip', 'display'), 'flex');
  const heroRule = ruleFor(sheet, 'body[data-layout="compact"] .layout-hero-strip');
  assert.equal(heroRule.style.getPropertyPriority('display'), 'important',
    'hero strip must use !important so it overrides md:hidden on desktop');
});

test('html: the connection center top-aligns the ring and keeps it sticky on desktop', () => {
  const html = fs.readFileSync(DASHBOARD_HTML, 'utf8');
  // The ring column must be top-aligned (lg:items-start) with the status panels
  // — items-center would strand the CONNECT button in the tall GPN panel.
  assert.ok(html.includes('lg:flex-row items-center lg:items-start gap-8 lg:gap-12'),
    'connection-center row must switch to items-start at the lg breakpoint');
  // The ring stays pinned to the top of the scrollport while the tall GPN
  // panel scrolls.
  assert.ok(html.includes('shrink-0 flex flex-col items-center lg:sticky lg:top-8'),
    'CONNECT ring column must be sticky at the lg breakpoint');
  // The decorative glow wrapper (not the section) clips the blobs, so no
  // overflow:hidden ancestor breaks position:sticky.
  const center = html.slice(html.indexOf('<!-- Connection center -->'));
  const section = center.slice(0, center.indexOf('<!-- Telemetry -->'));
  assert.ok(section.includes('absolute inset-0 overflow-hidden rounded-2xl'),
    'glow blobs are clipped by their own wrapper');
  assert.ok(!section.includes('rounded-2xl p-6 md:p-8 relative overflow-hidden'),
    'the section itself must not clip (overflow:hidden breaks position:sticky)');
});

test('jsdom: compact data-layout flips sidebar vs hero strip on the real document', () => {
  const dom = styledDashboard();
  const d = dom.window.document;
  const sidebarConn = d.querySelector('.layout-sidebar-conn');
  const heroStrip = d.querySelector('.layout-hero-strip');
  assert.ok(sidebarConn && heroStrip, 'layout elements exist in the real html');

  // Status layout (default): the sidebar Connection panel is visible.
  assert.notEqual(computed(dom, sidebarConn).display, 'none', 'sidebar controls visible in status layout');

  // Compact layout: sidebar controls hide, hero strip is forced on.
  d.body.setAttribute('data-layout', 'compact');
  assert.equal(computed(dom, sidebarConn).display, 'none', 'sidebar controls hidden in compact layout');
  assert.equal(computed(dom, heroStrip).display, 'flex', 'hero strip shown in compact layout');

  // Back to status: the sidebar panel returns.
  d.body.setAttribute('data-layout', 'status');
  assert.notEqual(computed(dom, sidebarConn).display, 'none', 'sidebar controls back in status layout');
  dom.window.close();
});

test('cssom: skin-host rules hide the app frame and reveal the host when a skin is active', () => {
  const sheet = parseSheet(skinHostCss());
  // Multi-selector rules keep their line breaks in selectorText — compare
  // whitespace-normalised so single-line expectations match.
  const ruleNorm = (selector) => {
    const target = selector.replace(/\s+/g, ' ').trim();
    const walk = (rules) => {
      for (const r of rules || []) {
        if (r.selectorText && r.selectorText.replace(/\s+/g, ' ').trim() === target) return r;
        if (r.cssRules) { const hit = walk(r.cssRules); if (hit) return hit; }
      }
      return null;
    };
    return walk(sheet.cssRules);
  };
  const declNorm = (selector, prop) => {
    const r = ruleNorm(selector);
    return r ? r.style.getPropertyValue(prop) : null;
  };

  // Host: hidden by default, and doubly hidden while [hidden] is set.
  assert.equal(decl(sheet, '#skinHost', 'display'), 'none');
  assert.equal(decl(sheet, '#skinHost[hidden]', 'display'), 'none');
  assert.equal(decl(sheet, '#skinFrame', 'display'), 'block');
  // Skin active -> host covers the window, standard UI is gone.
  assert.equal(decl(sheet, 'body[data-skin]:not([data-skin="standard"]) #skinHost', 'display'), 'block');
  assert.equal(decl(sheet, 'body[data-skin]:not([data-skin="standard"]) #appFrame', 'display'), 'none');
  // The full-screen FX (cursor trails, matrix rain, cyber overlay) must die too.
  assert.equal(declNorm('body[data-skin]:not([data-skin="standard"]) #cursorCanvas, body[data-skin]:not([data-skin="standard"]) #matrixRainCanvas, body[data-skin]:not([data-skin="standard"]) #cyberOverlay, body[data-skin]:not([data-skin="standard"]) .cursor-fx', 'display'), 'none');
  assert.equal(declNorm('body[data-skin]:not([data-skin="standard"])::before, body[data-skin]:not([data-skin="standard"])::after', 'display'), 'none');
  assert.equal(decl(sheet, 'body[data-skin]:not([data-skin="standard"])', 'overflow-y'), 'auto');

  // The app-frame / FX hides are forced with !important so nothing re-shows them.
  const frameRule = ruleFor(sheet, 'body[data-skin]:not([data-skin="standard"]) #appFrame');
  assert.equal(frameRule.style.getPropertyPriority('display'), 'important');
  const fxRule = ruleNorm('body[data-skin]:not([data-skin="standard"]) #cursorCanvas, body[data-skin]:not([data-skin="standard"]) #matrixRainCanvas, body[data-skin]:not([data-skin="standard"]) #cyberOverlay, body[data-skin]:not([data-skin="standard"]) .cursor-fx');
  assert.equal(fxRule.style.getPropertyPriority('display'), 'important');
});

test('jsdom: body[data-skin] flips the real #appFrame / #skinHost visibility', () => {
  const dom = styledDashboard();
  const d = dom.window.document;
  const appFrame = d.getElementById('appFrame');
  const skinHost = d.getElementById('skinHost');
  const skinFrame = d.getElementById('skinFrame');
  const cursorCanvas = d.getElementById('cursorCanvas');
  assert.ok(appFrame && skinHost && skinFrame, 'host elements exist in the real html');

  // Standard (boot state): classic UI visible (appFrame is a flex column via
  // its Tailwind classes), host hidden.
  assert.equal(computed(dom, appFrame).display, 'flex', 'app frame visible by default');
  assert.equal(computed(dom, skinHost).display, 'none', 'skin host hidden by default');

  // Skin active: exactly what applySkin() does — data-skin attribute plus the
  // hidden property cleared on the host container.
  d.body.setAttribute('data-skin', 'cyber');
  skinHost.hidden = false;
  assert.equal(computed(dom, skinHost).display, 'block', 'host covers the window while a skin is active');
  assert.equal(computed(dom, appFrame).display, 'none', 'app frame hidden (forced !important)');
  assert.equal(computed(dom, skinFrame).display, 'block', 'iframe fills the host');
  if (cursorCanvas) assert.equal(computed(dom, cursorCanvas).display, 'none', 'cursor FX hidden');
  assert.equal(computed(dom, d.body).overflowY, 'auto', 'body scrolls while a skin is active');

  // Back to standard: the classic UI returns.
  d.body.setAttribute('data-skin', 'standard');
  skinHost.hidden = true;
  assert.equal(computed(dom, appFrame).display, 'flex', 'app frame back');
  assert.equal(computed(dom, skinHost).display, 'none', 'host hidden again');
  dom.window.close();
});

// ---------------------------------------------------------------------------
// Theme palettes — every registered theme owns a distinct body[data-theme]
// ---------------------------------------------------------------------------
// The 25 registered themes each define their own body[data-theme] palette
// block (the exact rules the packaged app renders, embedded in the dashboard
// html). These tests pin three contracts:
//   1. registry <-> palette completeness (no orphan ids in either direction),
//   2. palettes are pairwise UNIQUE (two identical palettes would be visually
//      indistinguishable bugs), and the core accent tuple stays distinct,
//   3. the on-disk Temalar/themes/theme-*.css sources are faithful to the
//      embedded chain (what ships is what renders).

function embeddedThemePalettes() {
  const sheet = parseSheet(dashboardCss());
  const map = {};
  const walk = (rules) => {
    for (const r of rules || []) {
      const m = r.selectorText && r.selectorText.match(/^body\[data-theme="([\w-]+)"\]$/);
      if (m) {
        const decls = {};
        for (let i = 0; i < r.style.length; i++) decls[r.style[i]] = r.style.getPropertyValue(r.style[i]);
        map[m[1]] = { rule: r, decls };
      }
      if (r.cssRules) walk(r.cssRules);
    }
  };
  walk(sheet.cssRules);
  return map;
}

const fingerprint = (decls) => Object.keys(decls).sort().map(k => k + ':' + decls[k]).join(';');

function themePalettesFromFile(file) {
  const sheet = parseSheet(fs.readFileSync(file, 'utf8'));
  const out = {};
  const walk = (rules) => {
    for (const r of rules || []) {
      const m = r.selectorText && r.selectorText.match(/^body\[data-theme="([\w-]+)"\]$/);
      if (m) {
        const decls = {};
        for (let i = 0; i < r.style.length; i++) decls[r.style[i]] = r.style.getPropertyValue(r.style[i]);
        out[m[1]] = decls;
      }
      if (r.cssRules) walk(r.cssRules);
    }
  };
  walk(sheet.cssRules);
  return out;
}

test('cssom: every registered theme owns a body[data-theme] palette with the core vars', () => {
  const palettes = embeddedThemePalettes();
  const registered = require(path.join(__dirname, '..', 'themes.json')).map(t => t.id);
  assert.equal(Object.keys(palettes).length, registered.length,
    'embedded palette count matches the registry (' + registered.length + ')');
  for (const id of registered) {
    assert.ok(palettes[id], 'theme "' + id + '" has a body[data-theme] palette');
    // Common core: the accent trio drives the whole UI on every theme.
    for (const core of ['--violet', '--cyan', '--emerald']) {
      assert.ok(palettes[id].decls[core], 'theme "' + id + '" defines ' + core);
    }
    // Background contract: the primary family tints via --bg, the secondary
    // family (aurora/candy/neon-cyber/...) paints the body directly.
    const hasBg = palettes[id].decls['--bg'];
    const hasBgProp = palettes[id].decls['background'];
    assert.ok(hasBg || hasBgProp, 'theme "' + id + '" sets a background (--bg or background)');
  }
  // No stray palette for an unregistered id.
  const orphan = Object.keys(palettes).filter(p => !registered.includes(p));
  assert.deepEqual(orphan, [], 'every palette belongs to a registered theme');
});

test('cssom: the 25 theme palettes are pairwise unique (full fingerprint + core accents)', () => {
  const palettes = embeddedThemePalettes();
  const ids = Object.keys(palettes);
  assert.equal(ids.length, 25, 'exactly 25 distinct palettes render');

  // Full declaration maps must differ between any two themes.
  const seen = new Map(); // fingerprint -> theme id
  const dupes = [];
  const coreSeen = new Map(); // bg+violet+cyan+emerald tuple -> theme id
  const coreDupes = [];
  for (const id of ids) {
    const fp = fingerprint(palettes[id].decls);
    if (seen.has(fp)) dupes.push(id + ' == ' + seen.get(fp));
    else seen.set(fp, id);
    const core = ['--bg', '--violet', '--cyan', '--emerald'].map(k => palettes[id].decls[k]).join('|');
    if (coreSeen.has(core)) coreDupes.push(id + ' shares the core accents with ' + coreSeen.get(core));
    else coreSeen.set(core, id);
  }
  assert.deepEqual(dupes, [], 'no two themes share an identical palette');
  assert.deepEqual(coreDupes, [], 'no two themes share the same core accent tuple');
});

test('cssom: on-disk theme files match the embedded chain they render', () => {
  const palettes = embeddedThemePalettes();
  const themeDir = path.join(__dirname, '..', 'themes');
  const files = fs.readdirSync(themeDir).filter(f => /^theme-.+\.css$/.test(f));
  assert.ok(files.length >= 16, 'theme files exist on disk');
  for (const file of files) {
    const fromFile = themePalettesFromFile(path.join(themeDir, file));
    const ids = Object.keys(fromFile);
    assert.ok(ids.length > 0, file + ' defines at least one palette');
    for (const id of ids) {
      assert.ok(palettes[id], file + ' palette "' + id + '" exists in the embedded chain');
      assert.equal(fingerprint(fromFile[id]), fingerprint(palettes[id].decls),
        file + ' palette "' + id + '" is identical to the embedded chain');
    }
  }
});

test('jsdom: switching body[data-theme] applies each palette at runtime', () => {
  const dom = styledDashboard();
  const d = dom.window.document;
  const palettes = embeddedThemePalettes();
  const g = (prop) => dom.window.getComputedStyle(d.body).getPropertyValue(prop).trim();

  for (const id of Object.keys(palettes)) {
    d.body.setAttribute('data-theme', id);
    assert.equal(g('--violet'), palettes[id].decls['--violet'], 'data-theme="' + id + '" applies its --violet');
    assert.equal(g('--cyan'), palettes[id].decls['--cyan'], 'data-theme="' + id + '" applies its --cyan');
    const bg = palettes[id].decls['--bg'];
    if (bg) {
      assert.equal(g('--bg'), bg, 'data-theme="' + id + '" applies its --bg');
    } else {
      // Secondary family paints the body background directly.
      const hex = palettes[id].decls['background'].replace('#', '');
      const rgb = [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16)).join(', ');
      assert.equal(dom.window.getComputedStyle(d.body).backgroundColor, 'rgb(' + rgb + ')',
        'data-theme="' + id + '" paints the body background');
    }
  }
  dom.window.close();
});

// ---------------------------------------------------------------------------
// Rule-order guard — every embedded block's overrides come after their bases
// ---------------------------------------------------------------------------
// Same engine as the skins: jsdom cascades by SOURCE ORDER only (no CSS
// specificity), so any override (higher specificity, extending selector, shared
// property) that sits BEFORE its base would silently lose in jsdom while real
// browsers apply it. This runs the engine over ALL THREE embedded blocks of
// the dashboard html — the hand-written chain (body.connected ring overrides,
// .active/.locked pill states, route badges, theme palettes), the Tailwind
// layer (preflight + utilities, the frozen snapshot of Temalar/tailwind.js),
// and the skin-host rules — and asserts every derived override comes after
// its base. Pseudo-element rules are skipped by the engine (invisible to
// jsdom), as are comma parts of the same rule (they share declarations).

test('cascade consistency: every embedded dashboard block has its overrides after their bases', () => {
  const { cascadeViolations } = require('./cascade-consistency.js');
  const blocks = [
    ['dashboard chain', dashboardCss()],
    ['tailwind utils', tailwindUtilsCss()],
    ['skin host', skinHostCss()]
  ];
  const problems = [];
  let totalRules = 0;
  for (const [name, css] of blocks) {
    const { violations, ruleCount } = cascadeViolations(css);
    totalRules += ruleCount;
    if (violations.length) {
      const detail = violations.map(v =>
        '  base     ' + v.base.sel + '  (index ' + v.base.idx + ')\n' +
        '  override ' + v.override.sel + '  (index ' + v.override.idx + ') — shared: ' + v.shared
      ).join('\n');
      problems.push('[' + name + '] ' + ruleCount + ' rules scanned:\n' + detail);
    }
  }
  assert.equal(problems.length, 0,
    'dashboard has no jsdom cascade divergences (' + totalRules + ' rules scanned across all embedded blocks):\n\n' +
    problems.join('\n\n'));
});

// ---------------------------------------------------------------------------
// Palette — the :root variables everything else derives from
// ---------------------------------------------------------------------------

test('cssom: dashboard :root palette defines the base accents', () => {
  const sheet = parseSheet(dashboardCss());
  const r = ruleFor(sheet, ':root');
  assert.ok(r, ':root rule exists');
  assert.equal(r.style.getPropertyValue('--bg'), '#0B0F19');
  assert.equal(r.style.getPropertyValue('--violet'), '#8B5CF6');
  assert.equal(r.style.getPropertyValue('--cyan'), '#06B6D4');
  assert.equal(r.style.getPropertyValue('--emerald'), '#10B981');
});
