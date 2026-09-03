// ============================================================================
// @media coverage — responsive overrides are source-ordered too
// ----------------------------------------------------------------------------
// jsdom IGNORES @media queries entirely (verified empirically: a matching
// `@media (min-width: 768px)` rule never applies to getComputedStyle even at a
// 1024px viewport), so media-inner rules can never beat a base in jsdom. But
// the REAL browser applies them whenever the query matches, so the source-order
// contract still holds — and matters MORE here:
//
//   * higher specificity — the engine catches a media override placed before
//     its base exactly like a normal override;
//   * EQUAL specificity — neither jsdom (ignores media) nor the engine (the
//     two engines agree) can see it, yet a media override placed BEFORE its
//     base is silently dead in real browsers: the later base wins whenever the
//     query matches. This file adds a dedicated check for exactly that case.
//
// The tests:
//   1. the engine's walk really recurses into @media (ruleCount includes the
//      media children — cross-checked against an independent manual walk),
//   2. every media-inner rule that extends an earlier rule comes AFTER it, in
//      every real sheet (responsive overrides after their bases),
//   3. negative self-tests prove both checks can actually fail.
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const CSSOM = require('rrweb-cssom');
const { cascadeViolations, tokenContains, sharedProps, sheetStats } = require('./cascade-consistency.js');

const SKINS = __dirname;
const DASHBOARD = path.join(SKINS, '..', '..', 'vpn-gpn-dashboard.html');

// All five embedded / on-disk sheets the engine scans, with the real content.
function sheets() {
  const html = fs.readFileSync(DASHBOARD, 'utf8');
  const block = (id) => {
    const m = html.match(new RegExp('<style id="' + id + '">([\\s\\S]*?)</style>'));
    assert.ok(m, id + ' block must exist in the dashboard html');
    return m[1];
  };
  return [
    ['cyber', fs.readFileSync(path.join(SKINS, 'cyber', 'skin.css'), 'utf8')],
    ['nexus', fs.readFileSync(path.join(SKINS, 'nexus', 'skin.css'), 'utf8')],
    ['dashboard chain', block('embedded-dashboard-css')],
    ['tailwind utils', block('embedded-tailwind-utils')],
    ['skin host', block('skin-host-css')]
  ];
}

// Independent manual walk: style rules (comma parts expanded, pseudo-element
// parts skipped — the same counting semantics as the engine), flagged when
// nested under an @media rule (a parent rule with a conditionText). Written
// separately from the engine so the cross-check is meaningful.
function manualMediaStats(cssText) {
  const sheet = CSSOM.parse(cssText);
  let rules = 0, inMedia = 0;
  const walk = (rs, inM) => {
    for (const r of rs || []) {
      if (r.selectorText) {
        const sel = String(r.selectorText).replace(/\s+/g, ' ').trim();
        for (const part of sel.split(',').map(s => s.trim())) {
          if (part.includes('::')) continue;
          rules++;
          if (inM) inMedia++;
        }
      }
      if (r.cssRules) walk(r.cssRules, inM || !!r.conditionText);
    }
  };
  walk(sheet.cssRules, false);
  return { rules, inMedia };
}

// ---------------------------------------------------------------------------
// 1. Traversal — the engine's scan includes @media children
// ---------------------------------------------------------------------------

test('engine: the walk recurses into @media in every sheet', () => {
  const mediaSheets = ['nexus', 'dashboard chain', 'tailwind utils'];
  for (const [name, css] of sheets()) {
    const manual = manualMediaStats(css);
    const { ruleCount, mediaRuleCount } = sheetStats(css);
    // The engine's media count must match an independent manual walk of the
    // same sheet — if the engine ever stopped recursing into @media, the
    // media-having sheets below would go stale and this fails.
    assert.equal(mediaRuleCount, manual.inMedia,
      name + ': engine counts every @media child (manual walk found ' + manual.inMedia + ')');
    // The engine's own scan agrees with its stats (walk implementations cannot drift).
    assert.equal(cascadeViolations(css).ruleCount, ruleCount, name + ': engine stats agree with its scan');
    if (mediaSheets.includes(name)) {
      assert.ok(mediaRuleCount > 0, name + ' contributes @media rules (non-vacuous)');
    } else {
      assert.equal(mediaRuleCount, 0, name + ' has no @media rules');
    }
  }
});

// ---------------------------------------------------------------------------
// 2. Responsive ordering — every media override comes after its base
// ---------------------------------------------------------------------------
// Dedicated check (the engine cannot see the equal-specificity case): for each
// media-inner rule, every OTHER rule it extends (shared property + token
// containment, ANY specificity) must come BEFORE it — otherwise the override
// is silently dead in real browsers whenever the query matches.

function responsiveViolations(cssText) {
  const sheet = CSSOM.parse(cssText);
  const rules = [];
  let idx = 0;
  const walk = (rs, inMedia) => {
    for (const r of rs || []) {
      if (r.selectorText) {
        const sel = String(r.selectorText).replace(/\s+/g, ' ').trim();
        for (const part of sel.split(',').map(s => s.trim())) {
          if (part.includes('::')) continue;
          rules.push({ sel: part, idx, inMedia, style: r.style });
        }
        idx++;
      }
      if (r.cssRules) walk(r.cssRules, inMedia || !!r.conditionText);
    }
  };
  walk(sheet.cssRules, false);

  const violations = [];
  for (const m of rules.filter(r => r.inMedia)) {
    for (const e of rules) {
      if (e.idx === m.idx) continue; // comma parts of the same rule
      if (!sharedProps(e.style, m.style).length) continue;
      // The media rule EXTENDS e (any element matching m also matches e) — so
      // m is the override and must come AFTER e. If it comes before, the
      // override is silently dead in real browsers whenever the query matches.
      if (tokenContains(m.sel, e.sel) && m.idx < e.idx) {
        violations.push({ base: e, override: m });
      }
    }
  }
  return violations;
}

test('responsive ordering: every @media override comes after its base in all real sheets', () => {
  const problems = [];
  for (const [name, css] of sheets()) {
    const violations = responsiveViolations(css);
    if (violations.length) {
      const detail = violations.slice(0, 8).map(v =>
        '  base     ' + v.base.sel + '  (index ' + v.base.idx + ')\n' +
        '  override ' + v.override.sel + '  (index ' + v.override.idx + ') — inside @media'
      ).join('\n');
      problems.push('[' + name + ']\n' + detail + (violations.length > 8 ? '\n  ... ' + (violations.length - 8) + ' more' : ''));
    }
  }
  assert.equal(problems.length, 0,
    'responsive overrides must come after the rules they extend (real browsers last-win when the query matches):\n\n' +
    problems.join('\n\n'));
});

test('responsive ordering: known responsive pairs of the real sheets are ordered', () => {
  // Pin the concrete responsive relationships the sheet must keep:
  //  - NEXUS collapses .nx-app to a single column under 900px
  //  - the dashboard theme deck reflows under 560px
  //  - Tailwind's md: variants extend their base utilities
  const pairs = [
    ['nexus', '.nx-app', '.nx-app'],                       // base (grid) -> media (1fr)
    ['dashboard chain', '.theme-deck-grid', '.theme-deck-grid'],   // base -> media (2 cols)
    ['dashboard chain', '.theme-deck-current', '.theme-deck-current'], // base -> media (display none)
    ['tailwind utils', '.hidden', '.md\\:hidden'],
    ['tailwind utils', '.flex', '.md\\:flex'],
    ['tailwind utils', '.grid-cols-2', '.sm\\:grid-cols-2']
  ];
  for (const [name, base, override] of pairs) {
    const css = sheets().find(s => s[0] === name)[1];
    const violations = responsiveViolations(css);
    const hit = violations.find(v => v.base.sel === base && v.override.sel === override);
    assert.ok(!hit,
      name + ': responsive override ' + override + ' must come after its base ' + base);
    // Also prove the pair is actually formed (the base exists before the media
    // rule) — otherwise the assertion above is vacuous.
    const indexes = [];
    let idx = 0;
    const sheet = CSSOM.parse(css);
    const walk = (rs) => {
      for (const r of rs || []) {
        if (r.selectorText) {
          const sel = String(r.selectorText).replace(/\s+/g, ' ').trim();
          for (const part of sel.split(',').map(s => s.trim())) {
            if (part === base || part === override) indexes.push({ sel: part, idx });
          }
          idx++;
        }
        if (r.cssRules) walk(r.cssRules);
      }
    };
    walk(sheet.cssRules);
    const b = indexes.find(i => i.sel === base);
    const o = indexes.find(i => i.sel === override && i.idx > (b ? b.idx : -1));
    assert.ok(b, name + ': base ' + base + ' exists in the sheet');
    assert.ok(o, name + ': responsive override ' + override + ' exists in the sheet');
    assert.ok(o.idx > b.idx, name + ': ' + override + ' (' + o.idx + ') after ' + base + ' (' + b.idx + ')');
  }
});

// ---------------------------------------------------------------------------
// 3. Negative self-tests — the checks can actually fail
// ---------------------------------------------------------------------------

test('responsive ordering: checker detects an equal-specificity media override before its base', () => {
  // Equal specificity: the ENGINE does not flag it (jsdom ignores media, and
  // real browsers last-win — both engines agree), but the responsive checker
  // must, because the media override is silently dead in real browsers.
  const broken = '@media (min-width: 768px) { .x { color: red; } } .x { color: blue; }';
  assert.equal(cascadeViolations(broken).violations.length, 0, 'engine intentionally stays quiet (no divergence)');
  const bad = responsiveViolations(broken);
  assert.equal(bad.length, 1, 'responsive checker flags the dead media override');
  assert.equal(bad[0].override.sel, '.x');
  assert.equal(bad[0].override.inMedia, true);
  assert.ok(bad[0].override.idx < bad[0].base.idx,
    'reversed pair: the media override sits BEFORE its base (that is the bug)');

  // Correct order (base first) is clean.
  const ok = '.x { color: blue; } @media (min-width: 768px) { .x { color: red; } }';
  assert.equal(responsiveViolations(ok).length, 0);
});

test('responsive ordering: engine also catches a higher-specificity media override before its base', () => {
  // Higher specificity: the engine's normal check applies to media rules too.
  const broken = '@media (min-width: 768px) { .x.on { background: red; } } .x { background: blue; }';
  const engine = cascadeViolations(broken);
  assert.equal(engine.violations.length, 1, 'engine flags the reversed responsive pair');
  assert.equal(engine.violations[0].override.sel, '.x.on');
  const check = responsiveViolations(broken);
  assert.equal(check.length, 1, 'responsive checker agrees');

  // Correct order is clean in both.
  const ok = '.x { background: blue; } @media (min-width: 768px) { .x.on { background: red; } }';
  assert.equal(cascadeViolations(ok).violations.length, 0);
  assert.equal(responsiveViolations(ok).length, 0);
});
