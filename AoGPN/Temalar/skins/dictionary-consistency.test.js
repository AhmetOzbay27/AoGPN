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
    // Every skin-scoped key must exist in every language: no silent fallback
    // to the shared key for any skin (cyber/infra/nexus) in any language.
    const missingSkin = skinKeys(en).filter(key => dicts[lang][key] === undefined);
    if (missingSkin.length) failures.push('fallback-skin-gap:' + lang + ':' + missingSkin.slice(0, 5).join(','));
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
    ['fallback-skin-gap', () => { const d = corruptedCopy(); delete d['zh-Hans']['skin.nexus-gpn.about.description']; return d; }],
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

// ---------------------------------------------------------------------------
// Coverage guard — every key the UI actually uses must exist in EVERY language
// dictionary, and non-technical values must not silently stay English.
// ---------------------------------------------------------------------------
// The invariant checks above only compare dictionaries with each other; they
// can never notice a key the UI renders that no dictionary translates (the
// pipeline falls back to the embedded English defaults). This section extracts
// the keys the three surfaces really use (static data-i18n attributes, t()/nxT/
// cyT calls and the skins' embedded English default maps) and fails when a
// language lacks one of them or ships a value identical to English outside the
// allowlist of intentional technical/brand terms.

const USED_KEYS = (() => {
  const read = (p) => fs.readFileSync(path.join(ROOT, p), 'utf8');
  const keyRe = /(?:cyT|nxT|ifT|t|__aogpnT)\(\s*['"]([^'"]+)['"]/g;
  const attrRe = /data-(?:i18n|nx-i18n|cy-i18n|if-i18n)(?:-title)?="([^"]+)"/g;
  const keys = new Set();
  const usedBy = new Map();
  const addFrom = (src, re, sid = 'standard') => {
    for (const m of src.matchAll(re)) {
      keys.add(m[1]);
      usedBy.set(m[1], (usedBy.get(m[1]) || new Set()).add(sid));
    }
  };
  addFrom(read('vpn-gpn-dashboard.html'), attrRe);
  for (const f of ['app.js', 'tailwind.js', 'boost-view.js', 'monitor-view.js']) {
    addFrom(read(path.join('Temalar', f)), keyRe);
    addFrom(read(path.join('Temalar', f)), attrRe);
  }
  for (const dir of ['core', 'features']) {
    for (const f of fs.readdirSync(path.join(ROOT, 'Temalar', dir))) {
      addFrom(read(path.join('Temalar', dir, f)), keyRe);
    }
  }
  for (const [id, dir, mapName] of [['nexus-gpn', 'nexus', 'NX_TEXT'], ['cyber', 'cyber', 'CY_TEXT'], ['infra', 'infra', 'IF_TEXT']]) {
    const html = read(path.join('Temalar', 'skins', dir, 'skin.html'));
    const js = read(path.join('Temalar', 'skins', dir, 'skin.js'));
    const addFor = (src, re) => { for (const m of src.matchAll(re)) { keys.add(m[1]); usedBy.set(m[1], (usedBy.get(m[1]) || new Set()).add(id)); } };
    addFor(html, attrRe);
    addFor(js, keyRe);
    const m = js.match(new RegExp('const ' + mapName + ' = \\{([\\s\\S]*?)\\n  \\};?'));
    if (m) for (const k of m[1].matchAll(/'([^']+)'\s*:/g)) { keys.add(k[1]); usedBy.set(k[1], (usedBy.get(k[1]) || new Set()).add(id)); }
  }
  return { keys, usedBy };
})();

// Key usage per surface (standard uses shared keys; skins resolve
// 'skin.<id>.<key>' first, then the shared '<key>').
const USED_BY = USED_KEYS.usedBy;
const USED_KEYS_SET = USED_KEYS.keys;

// Technical/brand terms that are intentionally identical in every language
// (protocol names, acronyms, same-spelling loanwords like Ping/Mode/Menu).
const TECHNICAL_IDENTICAL = new Set([
  'brand', 'brandSub', 'stat.jitter', 'stat.ping', 'stat.session', 'stat.stable', 'stat.excellent',
  'mode.globalVpn', 'ip.isp', 'about.platform', 'about.version', 'sidebar.menu', 'quick.mode',
  'quick.capture', 'gpn.captureSet.direction', 'monitor.apps', 'ip.tunnel', 'gpn.pidpool.offlineShort',
  'gpn.pidpool.fatal', 'gpn.servers.edit', 'topbar.node', 'split.vpn', 'split.off', 'proxy.off',
  'proxy.on', 'console.session', 'log.direct', 'log.on', 'tele.throughput', 'tele.ping', 'node',
  'metric.session', 'sub.globalVpn', 'sub.gpn', 'sub.tun', 'sub.proxy', 'status.online',
  'status.offline', 'modeGlobal', 'transportProxy', 'transportTun', 'route.global', 'kind.vpn',
  'ana.ping', 'ana.session', 'proxy.ok', 'opt.pac', 'opt.wireguard', 'opt.mimic', 'opt.hysteria2',
  'opt.openvpn', 'opt.gpnSplit', 'route.prefix', 'local', 'protection', 'capture', 'transport.tun',
  'transport.proxy', 'route.vpn', 'route.warp', 'route.vpn+proxy', 'route.direct', 'route.proxy',
  'status.connected', 'status.disconnected', 'status.connecting',
  // French words that are spelled identically to English (legitimate translations):
  'gpn.matrix.strict', 'telemetry.excellent', 'telemetry.stable', 'boost.quickRoute',
  'boost.viaNode', 'global.transport',
  // Skin-scoped keys whose resolved values are intentional technical/brand
  // terms spelled identically in those languages (matched against the bare
  // key after stripping the skin.<id>. prefix):
  'boost.colProgram', 'boost.colRoute', 'boost.colStatus', 'gpn.cluster.egressLabel',
  'global.title', 'ana.session', 'ana.jitter', 'ana.route', 'opt.gpnSplit',
  'proxy.timeout', 'setting.capture', 'setting.split', 'gpns.edit', 'mode.vpn',
  'ctl.capture', 'node.colRoute', 'log.node', 'log.mode', 'log.split', 'ctl.failover',
  'log.failover', 'about.session', 'ctl.mode', 'ctl.transport', 'ctl.split', 'tabs.gpn',
  'opt.gvisor', 'opt.system', 'opt.mixed', 'proxy.test', 'gpns.udp.unknown', 'split.gpn',
  'node.colProgram', 'tabs.terminal', 'tabs.monitor', 'about.skin', 'panel.proxy', 'split.global',
  'boost.globalVpn', 'monitor.view.groupRoute', 'monitor.view.groupApp', 'ana.gpn', 'ana.global',
  'gpn.reslog.action.modeFallback', 'log.off', 'gpn.ev.fallback',
  // Brand name, protocol/technical loanwords, and same-spelling UI terms
  // (TCP/UDP ping types, Domain button, online/Edit/Remote loanwords):
  'footer.appName', 'nodes.online', 'nodes.pingTypeTcp', 'nodes.pingTypeUdp',
  'nodes.pingTypeBoth', 'nodes.edit', 'monitor.colRemote', 'boost.addDomain',
  // Settings view: brand/acronym/technical literals that legitimately stay
  // spelled identically in every language (tab label, MTU, IP placeholder,
  // protocol/stack option names).
  'settings.tab.gpn', 'settings.mtu', 'settings.sendThroughPh',
  'quick.protocolWireguard', 'quick.protocolMimic', 'quick.protocolHysteria2',
  'quick.protocolOpenvpn', 'opt.stack.gvisor'
]);

function isRealKey(key, en, usedBy) {
  if (!key || key.includes('{')) return false;
  if (/^[a-zA-Z][\w-]*(\.[a-zA-Z][\w-]*)+$/.test(key)) return true; // dotted path
  // Single-token keys are real only when a dictionary entry exists for them
  // (shared or under any skin scope) — everything else is regex noise.
  if (en[key] !== undefined) return true;
  for (const sid of usedBy.get(key) || []) {
    if (en['skin.' + sid + '.' + key] !== undefined) return true;
  }
  return false;
}

function coverageFailures(dicts = DICTS, usedKeys = USED_KEYS_SET, usedBy = USED_BY) {
  const en = dicts.en;
  const failures = [];
  for (const key of usedKeys) {
    const surfaces = usedBy.get(key) || new Set(['standard']);
    for (const sid of surfaces) {
      const chain = sid === 'standard' ? [key] : ['skin.' + sid + '.' + key, key];
      if (!isRealKey(key, en, usedBy)) continue;
      const enKey = chain.find(k => en[k] !== undefined);
      if (enKey === undefined) {
        failures.push('no-en:' + sid + ':' + key);
        continue;
      }
      const enVal = en[enKey];
      for (const lang of Object.keys(dicts)) {
        if (lang === 'en') continue;
        const dict = dicts[lang];
        const valKey = chain.find(k => dict[k] !== undefined);
        if (valKey === undefined) {
          failures.push('missing:' + lang + ':' + sid + ':' + key);
        } else if (String(dict[valKey]) === String(enVal) && !TECHNICAL_IDENTICAL.has(enKey) && !TECHNICAL_IDENTICAL.has(enKey.replace(/^skin\.[^.]+\./, ''))) {
          failures.push('untranslated:' + lang + ':' + sid + ':' + key + ' → "' + String(dict[valKey]).slice(0, 30) + '"');
        }
      }
    }
  }
  return failures;
}

test('dictionary consistency: every used key resolves in every language (no English fallback gaps)', () => {
  const failures = coverageFailures();
  assert.deepEqual(failures.filter(f => f.startsWith('missing:') || f.startsWith('no-en:')), [], 'gaps: ' + failures.filter(f => f.startsWith('missing:') || f.startsWith('no-en:')).slice(0, 30).join(' | '));
});

test('dictionary consistency: used keys never silently stay English outside the technical allowlist', () => {
  const failures = coverageFailures();
  assert.deepEqual(failures.filter(f => f.startsWith('untranslated:')), [], 'untranslated: ' + failures.filter(f => f.startsWith('untranslated:')).slice(0, 30).join(' | '));
});
