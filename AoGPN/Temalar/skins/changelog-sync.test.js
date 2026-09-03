// ============================================================================
// CHANGELOG.md ⇄ Temalar/release-notes.json sync guard
// ----------------------------------------------------------------------------
// Every milestone heading (`#### [7.26.x] — Title`) under the TOP release
// section of CHANGELOG.md must appear in Temalar/release-notes.json with the
// same number, the identical title and in the same (newest-first) order — and
// the JSON `version` must name that release. Descriptions in the JSON are
// deliberately abridged summaries, so they are only checked for presence, not
// content. Adding a milestone to the changelog without adding its
// `{n,title,desc}` entry to the JSON (or editing one side's heading without
// the other) fails right here instead of shipping a stale Release Notes tab.
//
// Run with:
//   node --test Temalar/skins/changelog-sync.test.js   (from ./AoGPN)
//   node --test *.test.js                              (from ./Temalar/skins)
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const CHANGELOG_PATH = path.join(__dirname, '..', '..', '..', 'CHANGELOG.md');
const RELEASE_NOTES_PATH = path.join(__dirname, '..', 'release-notes.json');

// Development milestones live only in the TOP (current) `## [x.y.z]` release
// section: the release guide nests unpublished milestones under the topmost
// `## [` heading and keeps released history below it. release-notes.json is a
// single file for the current release, so only the first `## [` section is
// compared against it.
function parseChangelog(md) {
  const lines = md.split('\n');
  const sectionStarts = lines
    .map((line, i) => (/^## /.test(line) ? i : -1))
    .filter(i => i !== -1);
  if (sectionStarts.length === 0) {
    throw new Error('CHANGELOG.md has no `## [` release section');
  }
  const sectionEnd = sectionStarts.length > 1 ? sectionStarts[1] : lines.length;

  const headingRe = /^####\s*\[([0-9]+\.[0-9]+\.[0-9]+)\]\s*[—–-]\s*(.+?)\s*$/;
  const sectionLines = lines.slice(sectionStarts[0], sectionEnd);

  const versionMatch = /^##\s*\[([^\]]+)\]/.exec(sectionLines[0]);
  const milestones = [];
  const problems = [];

  for (const line of sectionLines) {
    if (!/^#### /.test(line)) continue;
    const m = headingRe.exec(line);
    if (!m) {
      problems.push('malformed heading (expected `#### [n.n.n] — Title`): ' + line.trim());
      continue;
    }
    const n = m[1];
    const title = m[2].trim();
    if (!title) {
      problems.push('empty title in heading: ' + line.trim());
    }
    if (/#/.test(title)) {
      problems.push('title contains "#" (a section header glued onto the heading?): ' + line.trim());
    }
    milestones.push({ n, title });
  }

  return {
    version: versionMatch ? versionMatch[1] : null,
    milestones,
    problems,
  };
}

function readJson(file) {
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (err) {
    assert.fail('cannot read ' + path.relative(process.cwd(), file) + ': ' + err.message);
  }
  try {
    return JSON.parse(text);
  } catch (err) {
    assert.fail(path.basename(file) + ' must be valid JSON: ' + err.message);
  }
}

test('changelog: top-release milestone headings are well-formed', () => {
  const changelog = parseChangelog(fs.readFileSync(CHANGELOG_PATH, 'utf8'));
  assert.ok(changelog.version, 'top `## [` release section must carry a version');
  assert.deepEqual(
    changelog.problems,
    [],
    'milestone headings under [' + changelog.version + '] must be `#### [n.n.n] — Title` with no stray markdown'
  );
  assert.ok(
    changelog.milestones.length > 0,
    'expected at least one development milestone under [' + changelog.version + ']'
  );
});

test('release-notes.json: version names the top changelog release', () => {
  const changelog = parseChangelog(fs.readFileSync(CHANGELOG_PATH, 'utf8'));
  const notes = readJson(RELEASE_NOTES_PATH);

  assert.equal(
    notes.version,
    changelog.version,
    'release-notes.json "version" must match the top `## [` release in CHANGELOG.md'
  );
});

test('release-notes.json: milestone list matches CHANGELOG.md exactly (n + title + order)', () => {
  const changelog = parseChangelog(fs.readFileSync(CHANGELOG_PATH, 'utf8'));
  const notes = readJson(RELEASE_NOTES_PATH);

  assert.ok(
    Array.isArray(notes.milestones),
    'release-notes.json must carry a "milestones" array'
  );

  for (const m of notes.milestones) {
    assert.ok(
      m && typeof m.n === 'string' && typeof m.title === 'string' && typeof m.desc === 'string',
      'every release-notes.json milestone needs string n / title / desc fields; got: ' + JSON.stringify(m)
    );
  }

  const changelogKeys = changelog.milestones.map(m => m.n + '\u0000' + m.title);
  const notesKeys = notes.milestones.map(m => m.n + '\u0000' + m.title);

  const leftovers = notesKeys.slice();
  const missing = changelogKeys.filter(key => {
    const i = leftovers.indexOf(key);
    if (i === -1) return true;
    leftovers.splice(i, 1);
    return false;
  });
  const extra = leftovers;

  assert.deepEqual(
    missing,
    [],
    'milestones present in CHANGELOG.md but missing from release-notes.json (same n AND identical title)'
  );
  assert.deepEqual(
    extra,
    [],
    'milestones present in release-notes.json but not in CHANGELOG.md (same n AND identical title)'
  );

  const label = m => m.n + ' — ' + m.title;
  assert.deepEqual(
    notes.milestones.map(label),
    changelog.milestones.map(label),
    'milestones must be listed newest-first in the same order in both files'
  );
});
