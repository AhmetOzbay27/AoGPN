// ============================================================================
// Accessibility regression test: focus-visible styles + role/aria structure
// ----------------------------------------------------------------------------
// Two layers across all three surfaces (CYBER, NEXUS, main dashboard):
//
//   1. cssom (rrweb-cssom): keyboard-focus styles — every interactive element
//      that is keyboard-operable must expose a visible :focus-visible rule so
//      keyboard users can see where they are.
//
//   2. DOM (jsdom + the real skin sandbox): the static markup and the runtime-
//      rendered switches must carry the right role/aria contract — role=switch,
//      aria-checked, tabindex and aria-labels — and the NEXUS kill switch must
//      actually toggle from the keyboard (Enter/Space).
//
// Run with:
//   node --test Temalar/skins/a11y.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const CSSOM = require('rrweb-cssom');
const { JSDOM } = require('jsdom');

const { loadSkin } = require('./skin-sandbox.js');
const SKINS = __dirname;
const DASHBOARD_HTML = path.join(__dirname, '..', '..', 'vpn-gpn-dashboard.html');

// ---------------------------------------------------------------------------
// cssom helpers
// ---------------------------------------------------------------------------

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

const sheetOf = (skin) => parseSheet(fs.readFileSync(path.join(SKINS, skin, 'skin.css'), 'utf8'));

function dashboardCss() {
  const html = fs.readFileSync(DASHBOARD_HTML, 'utf8');
  const m = html.match(/<style id="embedded-dashboard-css">([\s\S]*?)<\/style>/);
  assert.ok(m, 'embedded-dashboard-css block must exist');
  return m[1];
}

const APPS = [
  { id: 1, processName: 'cs2', displayName: 'CS2', action: 'vpn', isRunning: true },
  { id: 2, processName: 'valorant', displayName: 'Valorant', action: 'direct', isRunning: true }
];

// ---------------------------------------------------------------------------
// focus-visible styles (cssom)
// ---------------------------------------------------------------------------

test('cssom: CYBER keyboard-operable elements have visible focus styles', () => {
  const sheet = sheetOf('cyber');
  // App route switches (keyboard toggled with ←/→/Enter/Space).
  assert.equal(decl(sheet, '.cy-switch:focus-visible', 'box-shadow'), '0 0 0 2px var(--cy-primary)');
  // The JACK IN button (role=button, tabindex=0).
  assert.equal(decl(sheet, '.cy-jackin:focus-visible', 'outline'), '2px solid var(--cy-primary)');
  assert.equal(decl(sheet, '.cy-jackin:focus-visible', 'outline-offset'), '4px');
});

test('cssom: NEXUS switches have a visible focus style', () => {
  const sheet = sheetOf('nexus');
  assert.equal(decl(sheet, '.nx-switch:focus-visible', 'outline'), '2px solid var(--nx-cyan)');
  assert.equal(decl(sheet, '.nx-switch:focus-visible', 'outline-offset'), '2px');
});

test('cssom: dashboard custom selects have visible focus styles', () => {
  const sheet = parseSheet(dashboardCss());
  assert.ok(decl(sheet, '.cs-trigger:focus-visible', 'border-color'), 'custom select trigger highlights on keyboard focus');
  assert.ok(decl(sheet, '.cs-option:focus-visible', 'background'), 'custom select options highlight on keyboard focus');
});

// ---------------------------------------------------------------------------
// role / aria structure (static markup)
// ---------------------------------------------------------------------------

test('cyber static: the JACK IN button exposes role, tabindex and aria-label', () => {
  const d = new JSDOM(fs.readFileSync(path.join(SKINS, 'cyber', 'skin.html'), 'utf8')).window.document;
  const power = d.getElementById('cyPower');
  assert.ok(power, 'jack-in element exists');
  assert.equal(power.getAttribute('role'), 'button');
  assert.equal(power.getAttribute('tabindex'), '0');
  assert.equal(power.getAttribute('aria-label'), 'Connect');
});

test('nexus static: the kill switch exposes role, aria-label and tabindex', () => {
  const d = new JSDOM(fs.readFileSync(path.join(SKINS, 'nexus', 'skin.html'), 'utf8')).window.document;
  const ks = d.getElementById('nxKillSwitch');
  assert.ok(ks, 'kill switch exists');
  assert.equal(ks.getAttribute('role'), 'switch');
  assert.equal(ks.getAttribute('aria-label'), 'Kill switch');
  assert.equal(ks.getAttribute('tabindex'), '0', 'kill switch must be keyboard-focusable');
  assert.equal(ks.getAttribute('aria-checked'), 'true', 'static markup reflects the on state');
});

test('dashboard static: mode selectors are labelled tablists', () => {
  const d = new JSDOM(fs.readFileSync(DASHBOARD_HTML, 'utf8')).window.document;
  assert.ok(d.querySelectorAll('[role="tablist"]').length >= 1, 'at least one tablist');
  const modeSel = d.querySelector('[role="tablist"][aria-label="Connection mode"]');
  assert.ok(modeSel, 'connection mode selector is a labelled tablist');
});

// ---------------------------------------------------------------------------
// role / aria structure (runtime-rendered switches via the skin sandbox)
// ---------------------------------------------------------------------------

test('cyber rendered: every app switch carries role, aria-checked and tabindex', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: APPS } });
  const rows = skin.document.querySelectorAll('#cySources [data-cy-src]');
  assert.equal(rows.length, 2);
  rows.forEach(row => {
    const sw = row.querySelector('.cy-switch');
    assert.equal(sw.getAttribute('role'), 'switch');
    assert.equal(sw.getAttribute('tabindex'), '0');
    const on = sw.classList.contains('on');
    assert.equal(sw.getAttribute('aria-checked'), String(on), 'aria-checked mirrors the switch state');
  });
  // Toggling flips aria-checked with the state.
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft');
  assert.equal(skin.getSwitch('cs2').getAttribute('aria-checked'), 'false');
  skin.close();
});

test('nexus rendered: every app switch carries role, aria-checked and tabindex', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: APPS } });
  const switches = skin.document.querySelectorAll('#nxGameList .nx-switch[data-nx-pname]');
  assert.equal(switches.length, 2);
  switches.forEach(sw => {
    assert.equal(sw.getAttribute('role'), 'switch');
    assert.equal(sw.getAttribute('tabindex'), '0');
    assert.equal(sw.getAttribute('aria-checked'), String(sw.classList.contains('on')));
  });
  skin.close();
});

// ---------------------------------------------------------------------------
// Keyboard behaviour of the kill switch (Enter/Space activate it like a click)
// ---------------------------------------------------------------------------

test('nexus: kill switch toggles from the keyboard and mirrors aria-checked', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: [] } });
  const ks = skin.document.getElementById('nxKillSwitch');
  assert.ok(ks);

  // Idle split mode is off -> switch is on; Enter activates the toggle.
  skin.bridge._posts.length = 0;
  skin.keydown(ks, 'Enter');
  const posts = skin.bridge._posts.filter(p => p.action === 'set_split_mode');
  assert.equal(posts.length, 1);
  assert.equal(posts[0].mode, 'manual', 'Enter toggles the kill switch to manual');
  assert.equal(ks.getAttribute('aria-checked'), 'false', 'aria-checked flips with the state');

  // Space toggles back to off.
  skin.keydown(ks, ' ');
  const back = skin.bridge._posts.filter(p => p.action === 'set_split_mode');
  assert.equal(back.length, 2);
  assert.equal(back[1].mode, 'off');
  assert.equal(ks.getAttribute('aria-checked'), 'true');

  // Non-activating keys do nothing.
  skin.bridge._posts.length = 0;
  skin.keydown(ks, 'Tab');
  assert.equal(skin.bridge._posts.filter(p => p.action === 'set_split_mode').length, 0);
  skin.close();
});
