// ============================================================================
// Shared rule-order pair table — source-order guard for every skin
// ----------------------------------------------------------------------------
// jsdom does NOT compute CSS specificity: its cascade is "the last matching
// rule wins". A state/override rule placed BEFORE its base rule therefore
// silently loses in jsdom, even though real browsers (which honour
// specificity) would apply it. To make the skins render identically in jsdom
// and in the WebView2 host, every (base, override) pair must keep the override
// AFTER the base in the real stylesheet.
//
// The auto cascade-consistency engine (cascade-consistency.js) derives pairs
// by token containment, but it cannot derive overrides whose selector uses
// DIFFERENT token names (e.g. `.trace-active` -> `#trace-dst0`) or whose
// selector text is a comma-joined list. This module is the hand-curated layer
// that pins those exact pairs.
//
// Contract: when a new skin is registered in skins.json, it MUST have an
// entry here (the source-order test fails otherwise) — so a new skin is
// automatically covered by this guard from day one.
// ============================================================================
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const CSSOM = require('rrweb-cssom');

const SKINS_DIR = __dirname;

// Hand-curated (base, override) selector pairs per skin folder (the folder
// name from the skins.json `file` field, e.g. "cyber", "nexus").
// Each entry: [baseSelector, overrideSelector] — the override must come after
// the base in the sheet.
const SKIN_PAIRS = {
  cyber: [
    // state palette: breached must come after the base skin rule
    ['.skin-cyber', '.skin-cyber.is-breached'],
    // switch: on-fill and on-thumb after the base switch rules
    ['.cy-switch', '.cy-target .cy-switch.on'],
    ['.cy-switch::after', '.cy-target .cy-switch.on::after'],
    // compact variant: beats base + on-thumb
    ['.cy-switch', '.cy-targets.many .cy-switch'],
    ['.cy-switch::after', '.cy-targets.many .cy-switch::after'],
    ['.cy-target .cy-switch.on::after', '.cy-targets.many .cy-target .cy-switch.on::after'],
    // selected / running card overrides
    ['.cy-target', '.cy-target.active'],
    ['.cy-target h4', '.cy-target.active h4'],
    ['.cy-target span', '.cy-target.active span'],
    ['.cy-target', '.cy-target.live'],
    // JACK IN button + sub-label states
    ['.cy-jackin', 'body.skin-cyber.is-breached .cy-jackin'],
    ['.cy-jackin', '.cy-jackin.cn'],
    ['.cy-sub-txt', 'body.skin-cyber.is-breached .cy-sub-txt'],
    // telemetry value size modifier (this order was once reversed and jsdom
    // silently rendered 24px instead of 17px)
    ['.cy-data-value', '.cy-data-value.cy-sm'],
    // trace highlighting: selected / live / breached pulses after the bases
    ['.trace-active', '.cy-frame[data-dest="dst0"] #trace-dst0, .cy-frame[data-dest="dst1"] #trace-dst1, .cy-frame[data-dest="dst2"] #trace-dst2'],
    ['.cy-traces .trace-src-line', '.cy-traces .trace-src-line.active'],
    ['.cy-traces .trace-src-line', '.cy-traces .trace-src-line.live'],
    ['.cy-traces .trace-src-line.live', '.skin-cyber.is-breached .cy-traces .trace-src-line.live'],
    // toast visibility
    ['.cy-toast', '.cy-toast.show'],
    // bottom-left control pills (route mode / direction)
    ['.cy-pill', '.cy-pill.active']
  ],
  nexus: [
    // switch: on-fill and on-thumb after the base switch rules
    ['.nx-switch', '.nx-switch.on'],
    ['.nx-switch::after', '.nx-switch.on::after'],
    // live state: orb glow and status dot re-tint after the base rules
    ['.nx-orb', '.nx-live .nx-orb'],
    ['.nx-dot', '.nx-live .nx-dot'],
    ['.nx-dot', '.nx-connecting .nx-dot'],
    // analytics value size modifiers after the base value rule
    ['.nx-anaVal', '.nx-anaVal.sm'],
    ['.nx-anaVal', '.nx-anaVal.lg'],
    // unit suffix after its base unit rule
    ['.nx-unit', '.nx-unit.xs'],
    // view-hiding utility pinned after the base view rule (it wins via
    // !important regardless, but the source order stays consistent)
    ['.nx-view', '.nx-view-hidden'],
    // empty-state variants after the base empty rule
    ['.nx-empty', '.nx-empty.center'],
    ['.nx-empty', '.nx-empty.slim']
  ],
  infra: [
    // state palette: is-on / is-connecting must come after the base skin rule
    ['.skin-infra', '.skin-infra.is-on'],
    ['.skin-infra', '.skin-infra.is-connecting'],
    // switch-less skin: the status dot + connect button re-tint via body state
    ['.if-dot', '.skin-infra.is-on .if-dot'],
    ['.if-dot', '.skin-infra.is-connecting .if-dot'],
    ['.if-connect', '.skin-infra.is-on .if-connect'],
    // live (tunneled) app rows after the base row
    ['.if-row', '.if-row.live'],
    // compact telemetry value after the base metric
    ['.if-metric', '.if-metric.sm']
  ]
};

// ---------------------------------------------------------------------------
// Registry discovery — the source-order test iterates the REAL skins.json so
// every newly registered skin is automatically covered (and required to have
// pairs).
// ---------------------------------------------------------------------------
function registrySkins() {
  const reg = JSON.parse(fs.readFileSync(path.join(SKINS_DIR, '..', 'skins.json'), 'utf8'));
  return reg.map((s) => {
    // "skins/cyber/skin.html" -> "cyber" (robust against / and \ separators)
    const folder = s.file.split(/[\\/]/).slice(-2)[0];
    return { id: s.id, name: s.name, folder };
  });
}

// Source-order index of every style rule in a sheet (depth-first, @media
// recursed, whitespace-normalized selectorText). Rules without a selectorText
// (@keyframes, @font-face) are skipped.
function sheetIndexes(cssText) {
  const sheet = CSSOM.parse(cssText);
  const order = [];
  let idx = 0;
  const walk = (rules) => {
    for (const r of rules || []) {
      if (r.selectorText) order.push([String(r.selectorText).replace(/\s+/g, ' ').trim(), idx++]);
      if (r.cssRules) walk(r.cssRules);
    }
  };
  walk(sheet.cssRules);
  return new Map(order);
}

// Check every pair against a stylesheet's source order. Returns a list of
// violations; empty means the sheet honours the pair contract. Exported
// separately so the checker itself can be self-tested with synthetic CSS.
function checkPairOrder(cssText, pairs) {
  const indexOf = sheetIndexes(cssText);
  const violations = [];
  for (const [base, override] of pairs) {
    const b = indexOf.get(base);
    const o = indexOf.get(override);
    if (b === undefined) {
      violations.push({ type: 'missing-base', base, override });
    } else if (o === undefined) {
      violations.push({ type: 'missing-override', base, override });
    } else if (o <= b) {
      violations.push({ type: 'reversed', base, override, baseIdx: b, overrideIdx: o });
    }
  }
  return violations;
}

// Check every pair of a skin against its real stylesheet.
function sourceOrderViolations(skinDir) {
  const css = fs.readFileSync(path.join(SKINS_DIR, skinDir, 'skin.css'), 'utf8');
  return checkPairOrder(css, SKIN_PAIRS[skinDir] || []);
}

module.exports = { SKIN_PAIRS, registrySkins, checkPairOrder, sourceOrderViolations };
