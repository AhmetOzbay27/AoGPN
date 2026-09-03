// ============================================================================
// Style regression test: critical skin CSS rules (jsdom + cssom)
// ----------------------------------------------------------------------------
// Two layers verify the visual contract of the CYBER and NEXUS skins:
//
//   1. cssom (rrweb-cssom, the cssom fork jsdom itself uses): the REAL
//      skin.css files are parsed and the critical declarations asserted at the
//      rule level — switch thumb positions (::after left), connected vs
//      disconnected color states, and per-theme palettes. Pseudo-elements like
//      ::after cannot be observed through getComputedStyle, so this layer is
//      the only way to pin them down.
//
//   2. jsdom getComputedStyle: the real skin.css is injected into a jsdom
//      document (the real skin.html is used as the base) and literal colors /
//      cascade behaviour are asserted on real elements — proving the selectors
//      actually match and specificity wins (e.g. the compact switch variant).
//
// Run with:
//   node --test Temalar/skins/skins-style.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const CSSOM = require('rrweb-cssom');
const { JSDOM } = require('jsdom');

const SKINS = path.join(__dirname);
const cssOf = (skin) => fs.readFileSync(path.join(SKINS, skin, 'skin.css'), 'utf8');

// ---------------------------------------------------------------------------
// cssom layer — rule-level assertions against the real stylesheets
// ---------------------------------------------------------------------------

function parseSheet(cssText) {
  return CSSOM.parse(cssText);
}

// Find the first rule with an exact selectorText match, recursing into
// @media blocks so the whole real sheet is searched.
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

// Declaration map of a rule (custom properties included).
function decls(rule) {
  const out = {};
  if (!rule || !rule.style) return out;
  for (let i = 0; i < rule.style.length; i++) {
    const prop = rule.style[i];
    out[prop] = rule.style.getPropertyValue(prop);
  }
  return out;
}

const decl = (sheet, selector, prop) => {
  const r = ruleFor(sheet, selector);
  return r ? r.style.getPropertyValue(prop) : null;
};

// ---------------------------------------------------------------------------
// jsdom layer — computed styles against the real html + real css
// ---------------------------------------------------------------------------

// Load a skin's real document and inject its real stylesheet so computed
// styles resolve exactly as in the iframe.
function styledSkin(skinDir) {
  const html = fs.readFileSync(path.join(SKINS, skinDir, 'skin.html'), 'utf8');
  const css = cssOf(skinDir);
  const dom = new JSDOM(html, { url: 'http://localhost/skins/' + skinDir + '/skin.html' });
  const style = dom.window.document.createElement('style');
  style.textContent = css;
  dom.window.document.head.appendChild(style);
  return dom;
}

function computed(dom, el) {
  return dom.window.getComputedStyle(el);
}

// ---------------------------------------------------------------------------
// CYBER — switch thumb positions
// ---------------------------------------------------------------------------

test('cssom: CYBER switch thumb sits left when off, right when on', () => {
  const sheet = parseSheet(cssOf('cyber'));
  assert.equal(decl(sheet, '.cy-switch::after', 'left'), '2px', 'off thumb pinned left');
  assert.equal(decl(sheet, '.cy-switch::after', 'top'), '2px');
  assert.equal(decl(sheet, '.cy-switch::after', 'width'), '10px');
  assert.equal(decl(sheet, '.cy-switch::after', 'height'), '6px');

  assert.equal(decl(sheet, '.cy-target .cy-switch.on::after', 'left'), '16px', 'on thumb travels right');
  assert.equal(decl(sheet, '.cy-target .cy-switch.on::after', 'background'), 'var(--cy-bg)', 'on thumb contrast against the fill');
});

test('cssom: CYBER connected state flips the state palette to secondary', () => {
  const sheet = parseSheet(cssOf('cyber'));
  // Disconnected: state = danger (red).
  assert.equal(decl(sheet, '.skin-cyber', '--cy-state'), 'var(--cy-danger)');
  assert.equal(decl(sheet, '.skin-cyber', '--cy-glow'), 'var(--cy-danger)');
  // Connected (is-breached): state = secondary (cyan) — the whole skin re-tints.
  assert.equal(decl(sheet, '.skin-cyber.is-breached', '--cy-state'), 'var(--cy-secondary)');
  assert.equal(decl(sheet, '.skin-cyber.is-breached', '--cy-glow'), 'var(--cy-secondary)');
});

test('cssom: CYBER JACK IN button changes colour when connected', () => {
  const sheet = parseSheet(cssOf('cyber'));
  assert.equal(decl(sheet, '.cy-jackin', 'background'), 'var(--cy-primary)', 'idle button uses primary');
  assert.equal(
    decl(sheet, 'body.skin-cyber.is-breached .cy-jackin', 'background'),
    'var(--cy-secondary)',
    'connected button switches to secondary'
  );
});

test('cssom: CYBER themes are distinct palettes (yellow vs violet)', () => {
  const sheet = parseSheet(cssOf('cyber'));
  const yellow = decls(ruleFor(sheet, 'body.skin-cyber[data-theme="yellow"]'));
  const purple = decls(ruleFor(sheet, 'body.skin-cyber[data-theme="purple"]'));
  assert.equal(yellow['--cy-primary'], '#fcee0a');
  assert.equal(yellow['--cy-secondary'], '#00f0ff');
  assert.equal(purple['--cy-primary'], '#b026ff');
  assert.equal(purple['--cy-secondary'], '#0066ff');
  assert.notEqual(yellow['--cy-primary'], purple['--cy-primary'], 'themes must be visually distinct');
  assert.notEqual(yellow['--cy-secondary'], purple['--cy-secondary']);
});

// ---------------------------------------------------------------------------
// NEXUS — switch thumb positions and fills
// ---------------------------------------------------------------------------

test('cssom: NEXUS switch thumb sits left when off, right when on', () => {
  const sheet = parseSheet(cssOf('nexus'));
  assert.equal(decl(sheet, '.nx-switch::after', 'left'), '3px', 'off thumb pinned left');
  assert.equal(decl(sheet, '.nx-switch::after', 'top'), '3px');
  assert.equal(decl(sheet, '.nx-switch::after', 'width'), '12px');
  assert.equal(decl(sheet, '.nx-switch::after', 'height'), '12px');
  assert.equal(decl(sheet, '.nx-switch::after', 'background'), '#657184', 'off thumb muted');

  assert.equal(decl(sheet, '.nx-switch.on::after', 'left'), '18px', 'on thumb travels right');
  assert.equal(decl(sheet, '.nx-switch.on::after', 'background'), 'var(--nx-cyan)', 'on thumb glows with the accent');
  assert.equal(decl(sheet, '.nx-switch.on::after', 'box-shadow'), '0 0 10px var(--nx-cyan)');
});

test('cssom: NEXUS switch fill toggles between muted and accent', () => {
  const sheet = parseSheet(cssOf('nexus'));
  assert.equal(decl(sheet, '.nx-switch', 'background'), '#18202d', 'off fill dark');
  assert.equal(decl(sheet, '.nx-switch.on', 'background'), 'rgba(32,232,255,.2)', 'on fill accent-tinted');
  // The accent driving the on state is defined on body.
  assert.equal(decl(sheet, 'body', '--nx-cyan'), '#20e8ff');
});

test('cssom: NEXUS live state re-tints the orb glow and the status dot', () => {
  const sheet = parseSheet(cssOf('nexus'));
  // Orb: idle cyan glow on the base rule, green outer glow once live.
  assert.equal(decl(sheet, '.nx-orb', 'box-shadow'), '0 0 80px rgba(32,232,255,.13), inset 0 0 45px rgba(32,232,255,.08)', 'idle orb glow');
  assert.equal(decl(sheet, '.nx-live .nx-orb', 'box-shadow'), '0 0 100px rgba(92,255,157,.22), inset 0 0 55px rgba(32,232,255,.12)', 'live orb glow');
  // Status dot: idle cyan, live green, connecting amber — all driven by the
  // body state class, not inline styles.
  assert.equal(decl(sheet, '.nx-dot', 'background'), '#20e8ff', 'idle dot cyan');
  assert.equal(decl(sheet, '.nx-live .nx-dot', 'background'), '#5cff9d', 'live dot green');
  assert.equal(decl(sheet, '.nx-connecting .nx-dot', 'background'), '#f59e0b', 'connecting dot amber');
});

test('cssom: NEXUS analytics/settings sizing is class-based, not inline', () => {
  const sheet = parseSheet(cssOf('nexus'));
  // Analytics value size modifiers (written as font shorthands — see the css
  // comment for why the longhand would break jsdom's naive cascade).
  assert.equal(decl(sheet, '.nx-anaVal.sm', 'font'), '600 14px Orbitron', 'exit IP value size');
  assert.equal(decl(sheet, '.nx-anaVal.lg', 'font'), '600 16px Orbitron', 'download/upload value size');
  // Unit suffixes.
  assert.equal(decl(sheet, '.nx-unit', 'font-size'), '11px', 'ms unit size');
  assert.equal(decl(sheet, '.nx-unit.xs', 'font-size'), '10px', 'Mbps unit size');
  // View visibility is a single class now (was an inline style.display toggle).
  assert.equal(decl(sheet, '.nx-view-hidden', 'display'), 'none');
  const hiddenRule = ruleFor(sheet, '.nx-view-hidden');
  assert.equal(hiddenRule.style.getPropertyPriority('display'), 'important',
    'the hidden class must beat any base display (grid/flex/block)');
  // Empty-state hints + the ACTIVE pill margin also moved out of inline styles.
  assert.equal(decl(sheet, '.nx-empty', 'font-size'), '10px');
  assert.equal(decl(sheet, '.nx-empty.center', 'text-align'), 'center');
  assert.equal(decl(sheet, '.nx-routeModePill.inline', 'margin-left'), '6px');
});

// ---------------------------------------------------------------------------
// Rule-order guard — jsdom's naive cascade only uses source order
// ---------------------------------------------------------------------------
// jsdom does NOT compute CSS specificity: its cascade is "the last matching
// rule wins". A state/override rule placed before its base rule therefore
// silently loses, even though real browsers (which honour specificity) would
// apply it. The pair table in skin-pairs.js holds every (base, override)
// pair per skin; the test below walks each skin's real stylesheet in source
// order (discovered from skins.json, so new skins are auto-covered) and
// asserts every override keeps coming AFTER its base — so the skin renders
// identically in jsdom and in the WebView2 host.

// The hand-curated (base, override) pair table and the registry discovery
// live in the shared skin-pairs.js module. The test below iterates the REAL
// skins.json registry, so a newly registered skin is automatically covered —
// and REQUIRED to have a pair table entry.
const { registrySkins, sourceOrderViolations } = require('./skin-pairs.js');

test('rule order: every registered skin has pairs and all are source-ordered', () => {
  const registry = registrySkins();
  assert.ok(registry.length >= 2, 'skins.json registry must contain at least the known skins');

  const problems = [];
  for (const skin of registry) {
    const violations = sourceOrderViolations(skin.folder);
    const missing = violations.filter((v) => v.type === 'missing-base' || v.type === 'missing-override');
    if (missing.length) {
      // A registered skin without pairs (or with stale selectors) must fail
      // loudly so new skins cannot slip past the guard.
      const names = missing.map((v) => '  ' + v.type + ': ' + v.base + ' -> ' + v.override).join('\n');
      problems.push('skin "' + skin.name + '" (' + skin.folder + ') has unresolved pair selectors:\n' + names);
      continue;
    }
    const reversed = violations.filter((v) => v.type === 'reversed');
    if (reversed.length) {
      const detail = reversed.map((v) =>
        '  base     ' + v.base + '  (index ' + v.baseIdx + ')\n' +
        '  override ' + v.override + '  (index ' + v.overrideIdx + ')'
      ).join('\n');
      problems.push('skin "' + skin.name + '" (' + skin.folder + ') has overrides before their bases:\n' + detail);
    }
  }

  assert.equal(problems.length, 0,
    'rule-order guard failed (override must come after its base for jsdom\'s naive cascade):\n\n' +
    problems.join('\n\n'));
});

test('rule order: the third skin (INFRA) is auto-covered by the registry guards', () => {
  const { SKIN_PAIRS, registrySkins, sourceOrderViolations, checkPairOrder } = require('./skin-pairs.js');
  const { allSkinCascadeViolations } = require('./cascade-consistency.js');

  // 1. The new skin is registered and matched to its pair table entry — the
  //    registry-driven test above now iterates it without any test edit.
  const infra = registrySkins().find(s => s.folder === 'infra');
  assert.ok(infra, 'INFRA is registered in skins.json');
  assert.ok(Array.isArray(SKIN_PAIRS['infra']) && SKIN_PAIRS['infra'].length > 0,
    'INFRA has a pair table entry, so the source-order guard covers it');

  // 2. The source-order guard engages on its real stylesheet: every pair ordered.
  assert.deepEqual(sourceOrderViolations('infra'), [], 'INFRA pairs are all source-ordered');

  // 3. The cascade engine auto-covers the new skin too (registry-driven scan).
  const engine = allSkinCascadeViolations();
  assert.ok(engine.length >= 3, 'every registered skin is engine-scanned');
  const infraEngine = engine.find(r => r.folder === 'infra');
  assert.ok(infraEngine, 'the engine scans INFRA');
  assert.equal(infraEngine.violations.length, 0, 'INFRA has no cascade divergences');

  // 4. Negative — a registered skin WITHOUT pairs trips the enforcement path
  //    (the same check the registry-driven test runs).
  const orig = SKIN_PAIRS['infra'];
  try {
    delete SKIN_PAIRS['infra'];
    const missing = registrySkins().filter(s => !(SKIN_PAIRS[s.folder] && SKIN_PAIRS[s.folder].length));
    assert.ok(missing.some(s => s.folder === 'infra'),
      'a registered skin without a pair table entry is reported as missing');
  } finally {
    SKIN_PAIRS['infra'] = orig;
  }

  // 5. Negative — a reversed pair against the new skin's REAL stylesheet is
  //    caught, proving the guard is load-bearing for INFRA (not vacuously green).
  const realInfraCss = fs.readFileSync(path.join(SKINS, 'infra', 'skin.css'), 'utf8');
  const bad = checkPairOrder(
    '.if-row.synthetic-extra { color: red; }\n' + realInfraCss,
    [['.if-row', '.if-row.synthetic-extra']]);
  assert.equal(bad.length, 1, 'a reversed pair against the real INFRA sheet is detected');
  assert.equal(bad[0].type, 'reversed');
  assert.equal(bad[0].override, '.if-row.synthetic-extra');
  assert.equal(bad[0].base, '.if-row');
  assert.ok(bad[0].overrideIdx < bad[0].baseIdx, 'the override sits before its base — the bug');
});

// The hand-curated pair checker must prove it can actually DETECT violations
// (not be vacuously green because a selector lookup misses).
const { checkPairOrder } = require('./skin-pairs.js');

test('rule order: pair checker detects reversed and missing selectors', () => {
  // Deliberately broken sheet: the override comes before its base.
  const broken = '.x.on { background: red; }\n.x { background: blue; }';
  const bad = checkPairOrder(broken, [['.x', '.x.on']]);
  assert.equal(bad.length, 1);
  assert.equal(bad[0].type, 'reversed');
  assert.ok(bad[0].baseIdx > bad[0].overrideIdx, 'reversed pair reports the wrong indexes');

  // A selector that does not exist in the sheet is reported, not silently skipped.
  const missing = checkPairOrder('.x { color: red; }', [['.x', '.x.on']]);
  assert.equal(missing.length, 1);
  assert.equal(missing[0].type, 'missing-override');

  // Correct order (base first) is clean.
  const ok = '.x { background: blue; }\n.x.on { background: red; }';
  assert.equal(checkPairOrder(ok, [['.x', '.x.on']]).length, 0);
});

// The cascade-consistency engine lives in the shared cascade-consistency.js
// module (also used by dashboard-style.test.js).
const { cascadeViolations } = require('./cascade-consistency.js');

test('cascade consistency: engine detects an override placed before its base', () => {
  // Deliberately broken: the higher-specificity override comes first. Real
  // browsers apply it (specificity); jsdom applies the later, weaker base.
  const broken = '.x.on { background: red; }\n.x { background: blue; }';
  const bad = cascadeViolations(broken);
  assert.equal(bad.violations.length, 1, 'engine must flag the reversed pair');
  assert.equal(bad.violations[0].override.sel, '.x.on');
  assert.equal(bad.violations[0].base.sel, '.x');
  assert.ok(bad.violations[0].shared.includes('background'));

  // Correct order (base first) is clean.
  const ok = '.x { background: blue; }\n.x.on { background: red; }';
  assert.equal(cascadeViolations(ok).violations.length, 0);

  // Equal specificity is decided by source order in BOTH engines — no divergence.
  const equal = '.x.on { background: red; }\n.x.on { background: blue; }';
  assert.equal(cascadeViolations(equal).violations.length, 0);

  // Comma parts of the SAME rule share one declaration list and one cascade
  // position — the Tailwind preflight `[role=button],button { cursor:pointer }`
  // case. The engine must not treat them as a base/override pair (it did
  // before the same-rule skip: `[role=button]` contains the token `button`,
  // higher specificity, and both sit at the same source index).
  const sameRule = '[role=button],button { cursor: pointer; }';
  assert.equal(cascadeViolations(sameRule).violations.length, 0,
    'comma parts of one rule cannot diverge');
});

test('engine: specificityOf implements CSS Selectors 4 (is/not/has/where/nth-child/attributes)', () => {
  const { specificityOf } = require('./cascade-consistency.js');
  const eq = (sel, want) => {
    const got = specificityOf(sel);
    assert.deepEqual(got, want, sel + ' → [' + got + '], expected [' + want + ']');
  };
  // The classic spec table.
  eq('*', [0, 0, 0]);
  eq('LI', [0, 0, 1]);
  eq('UL OL LI.red', [0, 1, 3]);
  eq('#x34y', [1, 0, 0]);
  eq('a[href]', [0, 1, 1]);
  eq('div::before', [0, 0, 2]);
  eq('.x:hover', [0, 2, 0]);
  // :not() / :is() / :has() take the MAX of their arguments, pseudo adds nothing.
  eq(':not(.x)', [0, 1, 0]);
  eq(':not(EM)', [0, 0, 1]);
  eq('div:not(.x)', [0, 1, 1]);
  eq(':is(.a, .b.c)', [0, 2, 0]);
  eq(':has(> img)', [0, 0, 1]);
  eq(':has(.a, #b)', [1, 0, 0]); // max = #b
  // :where() is always zero — even with id/type arguments.
  eq(':where(#x)', [0, 0, 0]);
  eq('[hidden]:where(:not([hidden=until-found]))', [0, 1, 0]);
  // :nth-child(An+B of S) takes S's specificity.
  eq(':nth-child(2n of .x)', [0, 1, 0]);
  eq(':nth-child(2n)', [0, 1, 0]);
  // Attribute selectors: any value form counts like a class, incl. flags and
  // nested inside functional pseudos.
  eq('body[data-theme="yellow"]', [0, 1, 1]);
  eq('[type=search]', [0, 1, 0]);
  eq('[data-x="a]b"]', [0, 1, 0]);
  eq('[type="text" i]', [0, 1, 0]);
  eq(':not([hidden])', [0, 1, 0]);
  // Escaped characters stay inside their token: .md\:hidden is ONE class.
  eq('.md\\:hidden', [0, 1, 0]);
  // Real sheet selectors the engine must rank correctly.
  eq('body[data-skin]:not([data-skin="standard"]) #skinHost', [1, 2, 1]);
  eq('abbr:where([title])', [0, 0, 1]); // :where() zeroes its whole argument list
});

test('cascade consistency: every registered skin has its overrides after their bases', () => {
  // Registry-driven: reads skins.json, applies the engine to every registered
  // skin (new skins are covered automatically — the same single source the
  // pair-table guard uses).
  const { allSkinCascadeViolations } = require('./cascade-consistency.js');
  const results = allSkinCascadeViolations();
  assert.ok(results.length >= 2, 'registry must contain at least the known skins');
  const problems = [];
  for (const r of results) {
    if (r.violations.length) {
      const detail = r.violations.map(v =>
        '  base     ' + v.base.sel + '  (index ' + v.base.idx + ')\n' +
        '  override ' + v.override.sel + '  (index ' + v.override.idx + ') — shared: ' + v.shared
      ).join('\n');
      problems.push('skin "' + r.name + '" (' + r.folder + ') — ' + r.ruleCount + ' rules scanned:\n' + detail);
    }
  }
  assert.equal(problems.length, 0,
    'no skin has jsdom cascade divergences (override before its base):\n\n' +
    problems.join('\n\n'));
});

test('jsdom: CYBER telemetry small value computes 17px, not the 24px base (real html + real css)', () => {
  const dom = styledSkin('cyber');
  const d = dom.window.document;
  const session = d.getElementById('cySession');
  const exitIp = d.getElementById('cyExitIp');
  assert.ok(session && exitIp, 'static telemetry values exist');
  assert.equal(computed(dom, session).fontSize, '17px', 'session value uses the .cy-sm size');
  assert.equal(computed(dom, exitIp).fontSize, '17px', 'exit IP value uses the .cy-sm size');
  // A plain value without the modifier keeps the 24px base.
  const plain = d.createElement('div');
  plain.className = 'cy-data-value';
  d.body.appendChild(plain);
  assert.equal(computed(dom, plain).fontSize, '24px');
  dom.window.close();
});

// ---------------------------------------------------------------------------
// jsdom layer — computed styles prove the rules actually apply
// ---------------------------------------------------------------------------

test('jsdom: NEXUS switch computed fill flips on .on (real html + real css)', () => {
  const dom = styledSkin('nexus');
  const d = dom.window.document;

  const off = d.createElement('div');
  off.className = 'nx-switch';
  d.body.appendChild(off);
  assert.equal(computed(dom, off).backgroundColor, 'rgb(24, 32, 45)', 'off fill computed from #18202d');

  const on = d.createElement('div');
  on.className = 'nx-switch on';
  d.body.appendChild(on);
  assert.equal(computed(dom, on).backgroundColor, 'rgba(32, 232, 255, 0.2)', 'on fill computed from the accent tint');

  // The static kill-switch in the real html already carries class "on".
  const kill = d.getElementById('nxKillSwitch');
  assert.ok(kill, 'static kill switch exists');
  assert.equal(computed(dom, kill).backgroundColor, 'rgba(32, 232, 255, 0.2)', 'static element picks up the on-state');
  dom.window.close();
});

test('jsdom: CYBER switch dimensions cascade to the compact variant (real html + real css)', () => {
  const dom = styledSkin('cyber');
  const d = dom.window.document;

  const plain = d.createElement('div');
  plain.className = 'cy-switch';
  d.body.appendChild(plain);
  assert.equal(computed(dom, plain).width, '30px');
  assert.equal(computed(dom, plain).height, '12px');

  // With many apps the container gets .cy-targets.many and the switch shrinks —
  // higher specificity must win in the cascade.
  const wrap = d.createElement('div');
  wrap.className = 'cy-targets many';
  const compact = d.createElement('div');
  compact.className = 'cy-switch';
  wrap.appendChild(compact);
  d.body.appendChild(wrap);
  assert.equal(computed(dom, compact).width, '24px', 'compact variant wins over the base rule');
  assert.equal(computed(dom, compact).height, '10px');
  dom.window.close();
});

test('jsdom: CYBER body.is-breached flips the state palette (real html + real css)', () => {
  const dom = styledSkin('cyber');
  const d = dom.window.document;
  const g = (el) => dom.window.getComputedStyle(el);

  // Idle: the skin's state palette is the danger red — the whole skin re-tints
  // through --cy-state/--cy-glow, so the class-to-palette cascade IS the
  // connected-state styling (no inline styles involved).
  assert.equal(g(d.body).getPropertyValue('--cy-state').trim(), 'var(--cy-danger)');
  assert.equal(g(d.body).getPropertyValue('--cy-glow').trim(), 'var(--cy-danger)');
  assert.ok(!d.body.classList.contains('is-breached'));

  // Connected: sync() toggles body.is-breached; the later source-order rule
  // (jsdom's naive cascade) re-points the palette at the cyan secondary.
  d.body.classList.add('is-breached');
  assert.equal(g(d.body).getPropertyValue('--cy-state').trim(), 'var(--cy-secondary)',
    'state palette flips to secondary when connected');
  assert.equal(g(d.body).getPropertyValue('--cy-glow').trim(), 'var(--cy-secondary)');
  assert.ok(d.body.matches('.skin-cyber.is-breached'), 'body matches the breached selector');
  assert.ok(d.querySelector('.cy-jackin'), 'JACK IN element exists for the palette rules');

  // Disconnect: back to danger.
  d.body.classList.remove('is-breached');
  assert.equal(g(d.body).getPropertyValue('--cy-state').trim(), 'var(--cy-danger)');
  dom.window.close();
});

test('jsdom: CYBER theme attribute swap re-palettes the skin (real html + real css)', () => {
  const dom = styledSkin('cyber');
  const d = dom.window.document;
  const g = (el) => dom.window.getComputedStyle(el);

  // Default static markup is CYBER YELLOW.
  assert.equal(d.body.getAttribute('data-theme'), 'yellow');
  assert.equal(g(d.body).getPropertyValue('--cy-primary').trim(), '#fcee0a');
  assert.equal(g(d.body).getPropertyValue('--cy-secondary').trim(), '#00f0ff');

  // setTheme() swaps the attribute; the palette must follow.
  d.body.setAttribute('data-theme', 'purple');
  assert.equal(g(d.body).getPropertyValue('--cy-primary').trim(), '#b026ff');
  assert.equal(g(d.body).getPropertyValue('--cy-secondary').trim(), '#0066ff');
  assert.notEqual(g(d.body).getPropertyValue('--cy-primary'), '#fcee0a', 'palette no longer yellow');
  dom.window.close();
});

test('jsdom: NEXUS body.nx-live flips the orb glow and the dot colour (real html + real css)', () => {
  const dom = styledSkin('nexus');
  const d = dom.window.document;
  const orb = d.getElementById('nxOrb');
  const dot = d.getElementById('nxNodeStatus');
  assert.ok(orb && dot, 'static orb and status dot exist in the real html');

  const idleShadow = computed(dom, orb).boxShadow;
  const idleDot = computed(dom, dot).backgroundColor;
  assert.equal(idleDot, 'rgb(32, 232, 255)', 'idle dot is cyan');

  // Connected: body.nx-live re-tints both elements through the cascade.
  d.body.classList.add('nx-live');
  const liveShadow = computed(dom, orb).boxShadow;
  assert.notEqual(liveShadow, idleShadow, 'orb glow changes when connected');
  assert.ok(liveShadow.includes('92,255,157'), 'live orb gains the green outer glow');
  assert.equal(computed(dom, dot).backgroundColor, 'rgb(92, 255, 157)', 'live dot turns green');

  // Connecting: the amber state.
  d.body.classList.remove('nx-live');
  d.body.classList.add('nx-connecting');
  assert.equal(computed(dom, dot).backgroundColor, 'rgb(245, 158, 11)', 'connecting dot turns amber');
  dom.window.close();
});
