// ============================================================================
// Language dictionary consistency guard — reusable positive + negative checks
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.join(__dirname, '..', '..');
const LANGS = ['en', 'tr', 'fa', 'fr', 'hu', 'id', 'ru', 'zh-Hans', 'zh-Hant'];
const FALLBACK_LANGS = ['fa', 'fr', 'hu', 'id', 'ru', 'zh-Hans', 'zh-Hant'];
const DICTS = Object.fromEntries(LANGS.map(lang => [lang, JSON.parse(fs.readFileSync(path.join(ROOT, 'Dil', lang + '.json'), 'utf8'))]));
const EN = DICTS.en;
const REGISTRY = JSON.parse(fs.readFileSync(path.join(ROOT, 'Temalar', 'skins.json'), 'utf8'));

const allKeys = dict => Object.keys(dict).sort();
const skinKeys = dict => allKeys(dict).filter(key => key.startsWith('skin.'));
const skinPrefixes = dict => new Set(Object.keys(dict).filter(key => key.startsWith('skin.')).map(key => key.split('.')[1]).filter(Boolean));
const placeholders = value => [...new Set(String(value).match(/\{[a-zA-Z0-9_]+\}/g) || [])].sort();

function invariantFailures(dicts = DICTS, registry = REGISTRY) {
  const en = dicts.en;
  const failures = [];
  const enSkin = new Set(skinKeys(en));
  for (const lang of Object.keys(dicts)) {
    for (const key of skinKeys(dicts[lang])) {
      if (!enSkin.has(key)) failures.push('skin-orphan:' + lang + ':' + key);
    }
  }

  const enAll = allKeys(en);
  const trAll = allKeys(dicts.tr);
  const missingAll = enAll.filter(key => !trAll.includes(key));
  const extraAll = trAll.filter(key => !enAll.includes(key));
  if (missingAll.length || extraAll.length) failures.push('full-parity:' + JSON.stringify({ missing: missingAll, extra: extraAll }));

  const enSkinSorted = skinKeys(en);
  const trSkinSorted = skinKeys(dicts.tr);
  if (JSON.stringify(enSkinSorted) !== JSON.stringify(trSkinSorted)) failures.push('skin-parity');

  for (const lang of FALLBACK_LANGS.filter(lang => dicts[lang])) {
    const extra = skinKeys(dicts[lang]).filter(key => !enSkin.has(key));
    if (extra.length) failures.push('fallback-orphan:' + lang);
    const cyberCount = skinKeys(dicts[lang]).filter(key => key.startsWith('skin.cyber.')).length;
    if (cyberCount < 22) failures.push('fallback-cyber-block:' + lang);
  }

  const registered = new Set(registry.map(skin => skin.id));
  const prefixes = skinPrefixes(en);
  if ([...prefixes].some(id => !registered.has(id)) || [...registered].some(id => !prefixes.has(id))) failures.push('skin-prefix-registry');
  for (const skin of registry) {
    if (!skinKeys(en).some(key => key.startsWith('skin.' + skin.id + '.'))) failures.push('missing-skin-block:' + skin.id);
  }

  for (const lang of Object.keys(dicts)) {
    if (lang === 'en') continue;
    for (const key of skinKeys(dicts[lang])) {
      if (JSON.stringify(placeholders(en[key])) !== JSON.stringify(placeholders(dicts[lang][key]))) failures.push('placeholder:' + lang + ':' + key);
    }
  }
  return failures;
}

function corruptedCopy() {
  return Object.fromEntries(Object.entries(DICTS).map(([lang, dict]) => [lang, { ...dict }]));
}

test('dictionary consistency: all real dictionaries satisfy every invariant', () => {
  assert.deepEqual(invariantFailures(), []);
});

test('dictionary consistency: negative self-test proves every invariant detects corruption', () => {
  const cases = [
    ['skin-orphan', () => { const d = corruptedCopy(); d.fr['skin.ghost.only'] = 'ghost'; return d; }],
    ['full-parity', () => { const d = corruptedCopy(); delete d.tr['nav.dashboard']; return d; }],
    ['full-parity', () => { const d = corruptedCopy(); d.tr['dashboard.ghost'] = 'ghost'; return d; }],
    ['skin-parity', () => { const d = corruptedCopy(); delete d.tr['skin.cyber.brandSub']; return d; }],
    ['fallback-orphan', () => { const d = corruptedCopy(); d.ru['skin.ghost.only'] = 'ghost'; return d; }],
    ['fallback-cyber-block', () => { const d = corruptedCopy(); for (const key of Object.keys(d.fa)) if (key.startsWith('skin.cyber.')) delete d.fa[key]; return d; }],
    ['skin-prefix-registry', () => { const d = corruptedCopy(); d.en['skin.ghost.only'] = 'ghost'; return d; }],
    ['missing-skin-block', () => { const d = corruptedCopy(); for (const key of Object.keys(d.en)) if (key.startsWith('skin.infra.')) delete d.en[key]; return d; }],
    ['placeholder', () => { const d = corruptedCopy(); d.tr['skin.cyber.log.routeMode'] = '> ROTA MODU'; return d; }]
  ];
  for (const [expected, mutate] of cases) {
    const failures = invariantFailures(mutate());
    assert.ok(failures.some(failure => failure.startsWith(expected)), expected + ' corruption was not detected: ' + failures.join(', '));
  }
});

test('dictionary consistency: en.json is the canonical superset of skin.* keys', () => {
  assert.deepEqual(invariantFailures().filter(failure => failure.startsWith('skin-orphan:')), []);
});

test('dictionary consistency: en.json and tr.json have identical complete key sets', () => {
  assert.deepEqual(invariantFailures().filter(failure => failure.startsWith('full-parity:')), []);
});

test('dictionary consistency: tr.json skin.* key set matches en.json exactly', () => {
  assert.deepEqual(invariantFailures().filter(failure => failure === 'skin-parity'), []);
});

test('dictionary consistency: fallback languages carry only en-known skin.* keys (strict subsets)', () => {
  assert.deepEqual(invariantFailures().filter(failure => failure.startsWith('fallback-')), []);
});

test('dictionary consistency: skin.<id> prefixes match the registered skins exactly', () => {
  assert.deepEqual(invariantFailures().filter(failure => failure === 'skin-prefix-registry'), []);
});

test('dictionary consistency: every registered skin has a scoped block in en.json', () => {
  assert.deepEqual(invariantFailures().filter(failure => failure.startsWith('missing-skin-block:')), []);
});

test('dictionary consistency: translations keep the same {param} placeholders as en', () => {
  assert.deepEqual(invariantFailures().filter(failure => failure.startsWith('placeholder:')), []);
});
