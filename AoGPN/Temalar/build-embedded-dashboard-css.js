#!/usr/bin/env node
'use strict';
/* ==========================================================================
   AoGPN dashboard — embedded stylesheet regenerator
   --------------------------------------------------------------------------
   The dashboard ships as a single offline-capable HTML document
   (vpn-gpn-dashboard.html). It contains <style id="embedded-dashboard-css">
   — a concatenated copy of the split stylesheet chain that
   Temalar/dashboard.css imports (base.css → components.css → themes/*.css
   → effects.css) — so the dashboard renders identically even when the
   @import targets cannot be resolved.

   This script is the SINGLE source of truth for that embedded copy:
     1. Parses the @import chain out of Temalar/dashboard.css (line-anchored,
        so the manifest's own header documentation is never mistaken for an
        import).
     2. Concatenates every imported file in manifest order, normalizing
        line endings to LF (the HTML is LF throughout).
     3. Replaces the <style id="embedded-dashboard-css"> block content in
        vpn-gpn-dashboard.html. The block is rewritten only when the
        concatenated output actually differs (idempotent; mtimes stay
        untouched when the copy is already current).

   Usage:
     node build-embedded-dashboard-css.js           # regenerate in place
     node build-embedded-dashboard-css.js --check   # exit 0 when in sync,
                                                    # exit 1 + diff hint when not
   The AoGPN.csproj build target RegenerateEmbeddedDashboardCss runs this
   script before every build, and DashboardAssetTests invokes it in --check
   mode so a stale embedded copy fails the test suite.
   ========================================================================== */

const fs = require('fs');
const path = require('path');

const TEMALAR_DIR = __dirname;                                   // .../Temalar
const HTML_PATH = path.join(TEMALAR_DIR, '..', 'vpn-gpn-dashboard.html');
const MANIFEST_PATH = path.join(TEMALAR_DIR, 'dashboard.css');
const BLOCK_TAG = '<style id="embedded-dashboard-css">';
const CLOSE_TAG = '</style>';

const checkMode = process.argv.includes('--check');

function fail(message) {
  process.stderr.write(`[build-embedded-dashboard-css] ${message}\n`);
  process.exit(1);
}

// 1. Parse the import chain (line-anchored: the header comment documents
//    files with "1) base.css" lines that must not be treated as imports).
const manifest = fs.readFileSync(MANIFEST_PATH, 'utf8');
const imported = [];
for (const line of manifest.split(/\r?\n/)) {
  const match = line.match(/^\s*@import url\("([^"]+)"\)\s*;?\s*$/);
  if (match) imported.push(match[1]);
}
if (imported.length < 18) {
  fail(`expected at least 18 imports in the manifest, found ${imported.length}`);
}

// 2. Concatenate in manifest order, normalizing to LF.
let css = '';
for (const rel of imported) {
  const filePath = path.join(TEMALAR_DIR, rel);
  if (!fs.existsSync(filePath)) {
    fail(`imported stylesheet missing: ${rel}`);
  }
  css += fs.readFileSync(filePath, 'utf8').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
}

// 3. Locate the embedded block and compare.
const html = fs.readFileSync(HTML_PATH, 'utf8');
const blockStart = html.indexOf(BLOCK_TAG);
if (blockStart < 0) {
  fail(`${BLOCK_TAG} is missing from ${path.basename(HTML_PATH)}`);
}
const contentStart = html.indexOf('>', blockStart) + 1;
const contentEnd = html.indexOf(CLOSE_TAG, contentStart);
if (contentEnd < 0) {
  fail(`closing ${CLOSE_TAG} for the embedded block is missing`);
}
const current = html.slice(contentStart, contentEnd);

if (current === css) {
  process.stdout.write(
    `embedded dashboard CSS is up to date (${imported.length} files, ${css.length} bytes)\n`
  );
  process.exit(0);
}

if (checkMode) {
  let hint = '';
  let i = 0;
  while (i < Math.min(current.length, css.length) && current[i] === css[i]) i++;
  hint = `first divergence at byte ${i} (embedded=${current.length}, chain=${css.length})`;
  fail(
    `embedded dashboard CSS is OUT OF SYNC with the import chain (${imported.length} files; ${hint}).\n` +
    `  Run: node ${path.relative(process.cwd(), __filename)}`
  );
}

// 4. Rewrite only when the content changed (idempotent).
const updated = html.slice(0, contentStart) + css + html.slice(contentEnd);
fs.writeFileSync(HTML_PATH, updated, 'utf8');
process.stdout.write(
  `regenerated embedded dashboard CSS from ${imported.length} imported files ` +
  `(${current.length} -> ${css.length} bytes)\n`
);
process.exit(0);