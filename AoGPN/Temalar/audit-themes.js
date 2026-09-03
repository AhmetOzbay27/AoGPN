#!/usr/bin/env node
/* ==========================================================================
   AoGPN dashboard — theme animation audit (dev tool)
   --------------------------------------------------------------------------
   Parses the full stylesheet chain (dashboard.css -> base/components/themes/
   effects) and prints every rule that declares an animation, grouped per
   file, with duration / iteration count so theme GPU cost can be compared.

   Run:  node Temalar/audit-themes.js
   ========================================================================== */
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname);
const manifest = fs.readFileSync(path.join(ROOT, 'dashboard.css'), 'utf8');
const imports = [...manifest.matchAll(/^\s*@import url\("([^"]+)"\)/gm)].map(m => m[1]);

function stripComments(css) {
  return css.replace(/\/\*[\s\S]*?\*\//g, '');
}

/** Tolerant brace-scanner: returns [{ selector, decls: [{prop,val}] }] + keyframes. */
function parseFile(css) {
  const text = stripComments(css);
  const rules = [];
  const keyframes = [];
  let i = 0;
  const len = text.length;
  while (i < len) {
    const open = text.indexOf('{', i);
    if (open < 0) break;
    let depth = 0;
    let close = -1;
    for (let j = open; j < len; j++) {
      if (text[j] === '{') depth++;
      else if (text[j] === '}') { depth--; if (depth === 0) { close = j; break; } }
    }
    if (close < 0) break;
    const head = text.slice(i, open).trim();
    const body = text.slice(open + 1, close).trim();
    if (head.startsWith('@keyframes')) {
      const name = head.replace(/^@keyframes\s+/, '').trim().split(/\s+/)[0];
      // count frames + property steps as a cheap complexity gauge
      const frames = (body.match(/\d+(?:\.\d+)?%/g) || []).length;
      const props = (body.match(/[a-z-]+\s*:/g) || []).length;
      keyframes.push({ name, frames, props });
    } else if (head && !head.startsWith('@')) {
      const decls = [];
      for (const raw of body.split(';')) {
        const d = raw.trim();
        if (!d) continue;
        const m = d.match(/^([a-z-]+)\s*:\s*(.*)$/i);
        if (m) decls.push({ prop: m[1].toLowerCase(), val: m[2].trim().replace(/!important\s*$/i, '').trim() });
      }
      const anims = decls.filter(d => d.prop === 'animation' || d.prop.startsWith('animation-'));
      if (anims.length) rules.push({ selector: head, anims });
    }
    i = close + 1;
  }
  return { rules, keyframes };
}

const timeRe = /((?:\d+(?:\.\d+)?|\.\d+)(?:ms|s))/;

function firstTime(value) {
  const m = value.match(timeRe);
  return m ? m[1] : null;
}

function parseShorthand(value) {
  // animation: name duration timing delay iteration direction fill
  const parts = value.trim().split(/\s+/);
  const name = parts[0] || '';
  let duration = null;
  let timing = 'ease';
  let delay = null;
  let iteration = null;
  for (const p of parts.slice(1)) {
    if (timeRe.test(p)) {
      if (duration === null) duration = p;
      else delay = p;
    } else if (/^\d+(?:\.\d+)?$/.test(p)) iteration = p;
    else if (/^infinite$/.test(p)) iteration = 'infinite';
    else if (/^ease|linear|step|cubic-bezier/i.test(p)) timing = p;
  }
  return { name, duration, timing, delay, iteration };
}

/** Turn an anim decl into { name, duration, iteration, source } */
function describe(anim) {
  if (anim.prop === 'animation') {
    const s = parseShorthand(anim.val);
    return { name: s.name, duration: s.duration, iteration: s.iteration || '1', source: anim.val };
  }
  if (anim.prop === 'animation-name') return { name: anim.val, duration: null, iteration: null, source: anim.val };
  if (anim.prop === 'animation-duration') return { name: '(duration)', duration: anim.val, iteration: null, source: anim.val };
  if (anim.prop === 'animation-iteration-count') return { name: '(iteration)', duration: null, iteration: anim.val, source: anim.val };
  return { name: '(' + anim.prop + ')', duration: null, iteration: null, source: anim.val };
}

const seconds = t => (t ? (t.endsWith('ms') ? parseFloat(t) / 1000 : parseFloat(t)) : null);

const files = imports.map(rel => ({ rel, parsed: parseFile(fs.readFileSync(path.join(ROOT, rel), 'utf8')) }));

// Cross-reference: collect the animation each selector declares so a
// duration-only override (e.g. body[data-theme=x]::before { animation-duration:
// 45s }) can be reported as the base animation it actually drives (aurora-drift).
const animBySelector = new Map();
for (const { parsed } of files) {
  for (const rule of parsed.rules) {
    for (const anim of rule.anims) {
      if (anim.prop === 'animation-name' || anim.prop === 'animation') {
        const d = describe(anim);
        if (d.name && d.name !== 'none') {
          animBySelector.set(rule.selector, { name: d.name, iteration: d.iteration });
        }
      }
    }
  }
}

const stripThemePrefix = sel => sel.replace(/^body\[data-theme="[^"]+"\]\s*/, '').trim();

/** Find the base animation a themed selector inherits (duration-only overrides). */
function resolveBase(selector) {
  if (animBySelector.has(selector)) return animBySelector.get(selector);
  const stripped = stripThemePrefix(selector);
  if (animBySelector.has(stripped)) return animBySelector.get(stripped);
  if (stripped === '::before' || stripped === '::after') {
    const p = animBySelector.get('body' + stripped);
    if (p) return p;
  }
  return null;
}


// ---- render ----
const pad = (s, n) => String(s).padEnd(n);
const padL = (s, n) => String(s).padStart(n);

console.log('Theme animation audit — ' + files.length + ' files in chain\n');
let totalAlwaysOn = 0;
let totalFinite = 0;

for (const { rel, parsed } of files) {
  if (!parsed.rules.length && !parsed.keyframes.length) continue;
  console.log('=== ' + rel + ' ===');
  if (parsed.keyframes.length) {
    console.log(pad('  keyframes:', 14) + parsed.keyframes.map(k => k.name + ' (' + k.frames + ' frames/' + k.props + ' props)').join(', '));
  }
  for (const rule of parsed.rules) {
    for (const anim of rule.anims) {
      const d = describe(anim);
      // animation: none = the disabled/idle state; not an active animation.
      if ((anim.prop === 'animation' || anim.prop === 'animation-name') && d.name === 'none') continue;
      // Duration-only override: resolve the base animation it drives.
      if (!d.name || d.name.startsWith('(')) {
        const base = resolveBase(rule.selector);
        if (base) {
          d.name = base.name + ' ⤾(dur override)';
          if (d.iteration === null) d.iteration = base.iteration;
        }
      }
      const dur = seconds(d.duration);
      const alwaysOn = d.iteration === 'infinite';
      if (alwaysOn) totalAlwaysOn++; else totalFinite++;
      console.log('  ' + pad(rule.selector.slice(0, 58), 60)
        + pad(d.name || '(name?)', 26)
        + padL(d.duration || (d.duration === null ? '—' : ''), 10)
        + padL(alwaysOn ? '∞' : (d.iteration || '1'), 6)
        + (alwaysOn ? '  ⚠ always-on' : ''));
    }
  }
  console.log('');
}

console.log('Totals: ' + totalAlwaysOn + ' always-on animation rules, ' + totalFinite + ' finite animation rules.');
console.log('(Always-on rules keep the compositor busy every frame while their theme is active;');
console.log('  finite ones cost one run. "Reduce effects" disables all of them.)');
