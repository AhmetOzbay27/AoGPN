// ============================================================================
// Cascade-consistency engine — binds jsdom's order-based cascade to the real
// CSS source order
// ----------------------------------------------------------------------------
// Real browsers cascade by specificity; jsdom only by source order. The two
// disagree exactly when a MORE SPECIFIC rule (an override) appears BEFORE the
// rule it extends: jsdom then silently applies the wrong (less specific) one.
// cascadeViolations(cssText) derives every (base, override) pair directly from
// a real stylesheet: the override's selector must extend the base's selector
// (token containment — any element matching the override also matches the
// base) and declare at least one shared property, with strictly higher
// specificity. It returns every pair where the override sits BEFORE its base —
// the exact jsdom-divergence condition. New CSS that breaks the invariant
// fails the consuming tests automatically, no pair list to maintain.
//
// Used by:
//   skins-style.test.js       — CYBER / NEXUS sheets
//   dashboard-style.test.js   — the embedded main-dashboard chain
// ============================================================================
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const CSSOM = require('rrweb-cssom');

const SKINS_DIR = __dirname;

const parseSheet = (cssText) => CSSOM.parse(cssText);

// ===========================================================================
// CSS Selectors Level 4 specificity — [ids, classes+attrs+pseudo-classes, types+pseudo-elements]
// ----------------------------------------------------------------------------
// Implements the modern rules the old regex version got wrong:
//   * `:is()`, `:not()`, `:has()` take the specificity of their MOST specific
//     argument — the pseudo itself adds nothing (`:not(.x)` is [0,1,0], not
//     [0,2,0]);
//   * `:where()` always contributes ZERO;
//   * `:nth-child(An+B of S)` / `:nth-last-child(...)` take S's specificity;
//   * attribute selectors (any form, incl. nested inside the functions above)
//     count like a class;
//   * pseudo-elements count like a type selector;
//   * escaped characters stay inside their token (`.md\:hidden` is one class).
// ============================================================================

const RE_IDENT = /[\w-]/;

// Consume a CSS identifier token (letters/digits/hyphens plus `\`-escapes),
// returning the index after it.
function skipToken(s, i) {
  let j = i;
  while (j < s.length && (RE_IDENT.test(s[j]) || s[j] === '\\')) j += s[j] === '\\' ? 2 : 1;
  return j;
}

// s[i] === '(' — return the index AFTER the matching ')', honouring quoted
// strings so `[data-x="a)b"]` does not confuse the paren depth.
function matchingParen(s, i) {
  let depth = 1;
  let j = i + 1;
  while (j < s.length && depth > 0) {
    const ch = s[j];
    if (ch === '"' || ch === "'") {
      const q = ch;
      j++;
      while (j < s.length && s[j] !== q) j++;
      j++;
      continue;
    }
    if (ch === '(') depth++;
    else if (ch === ')') depth--;
    j++;
  }
  return j;
}

// Split a comma-separated selector list at depth 0 (nested parens/brackets and
// quoted strings stay intact).
function splitList(s) {
  const out = [];
  let depth = 0, start = 0;
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if (ch === '(' || ch === '[') depth++;
    else if (ch === ')' || ch === ']') depth--;
    else if (ch === ',' && depth === 0) {
      out.push(s.slice(start, i));
      start = i + 1;
    }
  }
  out.push(s.slice(start));
  return out;
}

// Specificity of one COMPOUND selector (no combinators).
function compoundSpecificity(compound) {
  let a = 0, b = 0, c = 0;
  const s = compound, n = s.length;
  let i = 0;
  while (i < n) {
    const ch = s[i];
    if (ch === '#') {
      a++;
      i = skipToken(s, i + 1);
    } else if (ch === '.') {
      b++;
      i = skipToken(s, i + 1);
    } else if (ch === '[') {
      // attribute selector — scan to the matching ']' (honours quotes)
      let j = i + 1;
      while (j < n) {
        const q = s[j];
        if (q === '"' || q === "'") { j++; while (j < n && s[j] !== q) j++; j++; continue; }
        if (q === ']') break;
        j++;
      }
      b++;
      i = j + 1;
    } else if (ch === ':') {
      if (s[i + 1] === ':') {
        c++; // pseudo-element counts like a type selector
        i = skipToken(s, i + 2);
      } else {
        const nameEnd = skipToken(s, i + 1);
        const name = s.slice(i + 1, nameEnd);
        if (s[nameEnd] === '(') {
          const close = matchingParen(s, nameEnd);
          const inner = s.slice(nameEnd + 1, close - 1);
          if (name === 'where') {
            // :where() contributes zero specificity
          } else if (name === 'is' || name === 'not' || name === 'has') {
            const m = maxListSpecificity(inner);
            a += m[0]; b += m[1]; c += m[2];
          } else if (name === 'nth-child' || name === 'nth-last-child') {
            // `An+B of S` — the S part (if present) contributes
            const ofIdx = inner.search(/\bof\s/);
            if (ofIdx >= 0) {
              const m = maxListSpecificity(inner.slice(ofIdx + 2));
              a += m[0]; b += m[1]; c += m[2];
            } else {
              b++; // plain structural pseudo-class
            }
          } else {
            b++; // :lang(), :dir(), :host(), ... count as a class
          }
          i = close;
        } else {
          b++; // plain pseudo-class
          i = nameEnd;
        }
      }
    } else if (ch === '*' || ch === '&' || ch === '|') {
      i++; // universal / nesting / namespace separators add nothing
    } else {
      // type selector (or escaped char)
      const end = skipToken(s, i);
      if (end > i) { c++; i = end; }
      else i++;
    }
  }
  return [a, b, c];
}

// Split a COMPLEX selector into compounds at depth 0 — combinators (whitespace,
// >, +, ~) only count OUTSIDE parentheses/brackets, so `:is(.a, .b.c)` or
// `[data-x="a b"]` never split mid-argument.
function splitCompounds(sel) {
  const out = [];
  let depth = 0, start = 0;
  for (let i = 0; i < sel.length; i++) {
    const ch = sel[i];
    if (ch === '(' || ch === '[') { depth++; continue; }
    if (ch === ')' || ch === ']') { depth--; continue; }
    if (depth === 0 && /[\s>+~]/.test(ch)) {
      if (i > start) out.push(sel.slice(start, i));
      while (i < sel.length && /[\s>+~]/.test(sel[i])) i++; // skip the combinator run
      start = i; // i now points at the first non-combinator char
    }
  }
  if (start < sel.length) out.push(sel.slice(start));
  return out.filter(s => s.length > 0);
}

// Specificity of one COMPLEX selector (may contain combinators).
function complexSpecificity(sel) {
  const spec = [0, 0, 0];
  for (const comp of splitCompounds(sel)) {
    const s = compoundSpecificity(comp);
    spec[0] += s[0]; spec[1] += s[1]; spec[2] += s[2];
  }
  return spec;
}

// Maximum specificity over a comma-separated selector list.
function maxListSpecificity(list) {
  const max = [0, 0, 0];
  for (const p of splitList(list)) {
    if (!p.trim()) continue;
    const s = complexSpecificity(p.trim());
    if (s[0] > max[0] || (s[0] === max[0] && (s[1] > max[1] || (s[1] === max[1] && s[2] > max[2])))) {
      max[0] = s[0]; max[1] = s[1]; max[2] = s[2];
    }
  }
  return max;
}

function specificityOf(selector) {
  return complexSpecificity(selector);
}

function specGt(x, y) {
  return x[0] > y[0] || (x[0] === y[0] && (x[1] > y[1] || (x[1] === y[1] && x[2] > y[2])));
}

const escRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// Token containment: needle appears in hay as a whole selector token (so
// `.cy-target` does not match inside `.cy-targets`).
function tokenContains(hay, needle) {
  return new RegExp('(^|[^\\w-])' + escRe(needle) + '($|[^\\w-])').test(hay);
}

function sharedProps(styleA, styleB) {
  const out = [];
  for (let i = 0; i < styleA.length; i++) {
    const p = styleA[i];
    if (styleB.getPropertyValue(p) !== '') out.push(p);
  }
  return out;
}

// Walk the sheet in source order and derive all (base, override) pairs where
// the override (higher specificity, extending selector, shared property) sits
// BEFORE its base — the exact jsdom-divergence condition.
function cascadeViolations(cssText) {
  const sheet = parseSheet(cssText);
  const rules = [];
  let idx = 0;
  const walk = (rs) => {
    for (const r of rs || []) {
      if (r.selectorText) {
        // Expand comma-separated selector lists: each selector in the list is
        // evaluated on its own (same specificity rules, same shared props), so
        // a list like `.a, .a::before` must not inflate specificity.
        const sel = String(r.selectorText).replace(/\s+/g, ' ').trim();
        for (const part of sel.split(',').map(s => s.trim())) {
          // Pseudo-ELEMENT selectors (::before/::after/...) are skipped: jsdom
          // never computes styles for pseudo-elements, so their cascade can
          // never diverge from a real browser (e.g. `body::before` before
          // `body` would otherwise be a false-positive pair).
          if (part.includes('::')) continue;
          rules.push({ sel: part, idx, style: r.style });
        }
        idx++;
      }
      if (r.cssRules) walk(r.cssRules);
    }
  };
  walk(sheet.cssRules);

  const violations = [];
  for (let i = 0; i < rules.length; i++) {
    for (let j = i + 1; j < rules.length; j++) {
      const earlier = rules[i], later = rules[j];
      // Comma parts of the SAME source rule share idx AND the same declaration
      // list (e.g. `[role=button],button { cursor: pointer }`) — they are one
      // rule in the cascade and can never diverge, so comparing them is a
      // false positive.
      if (earlier.idx === later.idx) continue;
      const shared = sharedProps(earlier.style, later.style);
      if (!shared.length) continue;
      const eSpec = specificityOf(earlier.sel);
      const lSpec = specificityOf(later.sel);
      // Normal: the later rule extends the earlier one (override after base).
      if (tokenContains(later.sel, earlier.sel) && specGt(lSpec, eSpec)) continue;
      // Broken: the earlier rule extends the later one with higher specificity
      // — jsdom applies the earlier rule, real browsers the later one.
      if (tokenContains(earlier.sel, later.sel) && specGt(eSpec, lSpec)) {
        violations.push({ base: later, override: earlier, shared: shared.join(', ') });
      }
    }
  }
  return { violations, ruleCount: rules.length };
}

// Count style rules the engine scans, split by whether they sit inside an
// @media block (directly or nested). Used by the responsive-ordering tests to
// prove the walk really recurses into media queries.
function sheetStats(cssText) {
  const sheet = parseSheet(cssText);
  let ruleCount = 0, mediaRuleCount = 0;
  const walk = (rs, inMedia) => {
    for (const r of rs || []) {
      if (r.selectorText) {
        const sel = String(r.selectorText).replace(/\s+/g, ' ').trim();
        const parts = sel.split(',').map(s => s.trim()).filter(p => !p.includes('::'));
        if (parts.length) {
          ruleCount += parts.length;
          if (inMedia) mediaRuleCount += parts.length;
        }
      }
      if (r.cssRules) walk(r.cssRules, inMedia || !!r.conditionText);
    }
  };
  walk(sheet.cssRules, false);
  return { ruleCount, mediaRuleCount };
}

// ---------------------------------------------------------------------------
// Skin helpers — apply the engine to every registered skin
// ---------------------------------------------------------------------------
// The registry lives in skins.json (discovered via skin-pairs.js, the same
// single source the pair-table guard uses), so a newly registered skin is
// automatically covered by the engine — no test edit needed.

function skinCascadeViolations(skinDir) {
  const css = fs.readFileSync(path.join(SKINS_DIR, skinDir, 'skin.css'), 'utf8');
  return cascadeViolations(css);
}

// [{ id, name, folder, violations, ruleCount }, ...] for every registered skin.
function allSkinCascadeViolations() {
  const { registrySkins } = require('./skin-pairs.js');
  return registrySkins().map((skin) => ({
    id: skin.id,
    name: skin.name,
    folder: skin.folder,
    ...skinCascadeViolations(skin.folder)
  }));
}

module.exports = {
  cascadeViolations,
  specificityOf,
  tokenContains,
  sharedProps,
  sheetStats,
  skinCascadeViolations,
  allSkinCascadeViolations
};
