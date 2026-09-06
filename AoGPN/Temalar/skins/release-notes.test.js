// ============================================================================
// Dashboard integration: About & Help → Release Notes tab
// ----------------------------------------------------------------------------
// Loads the REAL vpn-gpn-dashboard.html + Temalar/app.js into jsdom (shared
// sandbox), opens the About & Help view, switches to the Release Notes tab and
// asserts the milestone roadmap renders from Temalar/release-notes.json, that a
// row toggles its description, and that Expand/Collapse all work.
//
// Run with:
//   node --test Temalar/skins/release-notes.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { loadDashboard } = require('./skin-sandbox.js');

async function boot() {
  const dash = await loadDashboard();
  assert.ok(dash.openView('about'), 'about nav item must exist and be clickable');
  return dash;
}

function clickTab(d, w, name) {
  const tab = d.querySelector('[data-about-tab="' + name + '"]');
  assert.ok(tab, 'about tab exists: ' + name);
  tab.dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
}

function panelVisible(d, name) {
  const p = d.querySelector('[data-about-panel="' + name + '"]');
  return !!p && !p.classList.contains('hidden');
}

test('release notes: milestones load from release-notes.json and toggle', async () => {
  const { document: d, window: w } = await boot();

  // The tab bar offers the three About tabs.
  clickTab(d, w, 'release');
  assert.ok(panelVisible(d, 'release'), 'release panel shows after clicking its tab');

  // Version + count come from the shipped JSON (never hard-coded, so a
  // version bump in the JSON cannot silently break this assertion).
  const notes = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'release-notes.json'), 'utf8'));
  const expected = notes.milestones.length;

  const rows = Array.from(d.querySelectorAll('#releaseNotes .release-row'));
  assert.equal(rows.length, expected, 'one row per milestone');

  const meta = d.getElementById('releaseMeta');
  const versionLabel = 'V' + notes.version;
  assert.ok(meta && meta.textContent.includes(versionLabel), 'meta shows version ' + versionLabel + ': ' + (meta && meta.textContent));
  assert.match(meta.textContent, new RegExp(String(expected)), 'meta shows milestone count');
});

test('release notes: rows toggle their description on click', async () => {
  const { document: d, window: w } = await boot();
  clickTab(d, w, 'release');

  const rows = Array.from(d.querySelectorAll('#releaseNotes .release-row'));
  assert.ok(rows.length > 0, 'rows rendered');

  const first = rows[0];
  const desc = first.nextElementSibling; // body follows its row in the container
  assert.ok(desc && desc.classList.contains('release-desc'), 'description block follows a row with a desc');

  assert.ok(desc.classList.contains('hidden'), 'described closed by default');
  first.dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.ok(!desc.classList.contains('hidden'), 'description opens on click');
  first.dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.ok(desc.classList.contains('hidden'), 'description closes on second click');
});

test('release notes: expand all / collapse all scale every description', async () => {
  const { document: d, window: w } = await boot();
  clickTab(d, w, 'release');

  d.getElementById('releaseExpandAll').dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
  const open = Array.from(d.querySelectorAll('#releaseNotes .release-desc'));
  assert.ok(open.length >= 1, 'at least one description rendered');
  assert.ok(open.every(b => !b.classList.contains('hidden')), 'all descriptions opened');

  d.getElementById('releaseCollapseAll').dispatchEvent(new w.MouseEvent('click', { bubbles: true, cancelable: true }));
  assert.ok(open.every(b => b.classList.contains('hidden')), 'all descriptions collapsed');
});