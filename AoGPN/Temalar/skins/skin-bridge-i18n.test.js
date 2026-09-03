// ============================================================================
// skinBridge.t contract — scoped skin.<id>.* keys beat shared keys and the
// embedded English fallback.
// ----------------------------------------------------------------------------
// The documented lookup order (app.js, skinBridge.t):
//   1. _dict['skin.<sid>.<key>']          (active language merged over en.json)
//   2. _fallbackDict['skin.<sid>.<key>']  (embedded English mirror)
//   3. _dict[key]                         (shared dashboard key)
//   4. _fallbackDict[key]
//   5. the raw key
//
// This test boots the REAL dashboard (loadDashboard → real app.js + real
// fetch of Dil/*.json) and proves the contract against the REAL tr.json data:
//   - skin.cyber.* wins over a shared key with a DIFFERENT value
//     (dir.whitelist: scoped "BEYAZ LİSTE" vs shared "Beyaz liste"),
//   - every skin.cyber.* key resolves to its scoped value (never raw),
//   - shared fallback when no scoped key exists (status.connected...),
//   - per-skin isolation (state.connected resolves differently per skin id),
//   - params substitution through the bridge,
//   - the real bridge agrees with a pure reimplementation of the contract.
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

const { loadDashboard } = require('./skin-sandbox.js');

const ROOT = path.join(__dirname, '..', '..'); // AoGPN/ (html, Temalar/, Dil/)
const EN = require(path.join(ROOT, 'Dil', 'en.json'));
const TR = require(path.join(ROOT, 'Dil', 'tr.json'));

// The documented contract reimplemented as a pure function over the real
// dictionaries (loadLanguage merges Object.assign({}, en, langDict), exactly
// like app.js). The embedded _fallbackDict mirrors Dil/en.json, so the real
// en.json is the faithful stand-in for it.
function contractT(key, params, skinId, langDict) {
  const empty = (v) => (v === null || v === undefined || v === '');
  const dict = Object.assign({}, EN, langDict || {});
  const sid = skinId || 'standard';
  let val = null;
  if (sid !== 'standard') {
    const scoped = 'skin.' + sid + '.' + key;
    if (Object.prototype.hasOwnProperty.call(dict, scoped)) val = dict[scoped];
    if (empty(val) && Object.prototype.hasOwnProperty.call(EN, scoped)) val = EN[scoped];
  }
  if (empty(val)) {
    if (Object.prototype.hasOwnProperty.call(dict, key)) val = dict[key];
    if (empty(val) && Object.prototype.hasOwnProperty.call(EN, key)) val = EN[key];
  }
  if (empty(val)) return key;
  if (params) {
    Object.keys(params).forEach(k => { val = String(val).split('{' + k + '}').join(String(params[k])); });
  }
  return String(val);
}

test('skinBridge.t contract against the real Dil dictionaries', async (t) => {
  const dash = await loadDashboard();
  t.after(() => dash.close());
  const w = dash.window;
  const tBridge = (key, params, skinId) => w.skinBridge.t(key, params, skinId);

  // Real skin scoping only shows its teeth when a non-English language is
  // active: _dict = merge(en.json, tr.json) with tr winning. Switch through
  // the same public channel the top-bar picker uses and let the async
  // loadLanguage fetches (stubbed with the real files) settle.
  w.skinBridge.setLanguage('tr');
  await new Promise(res => setImmediate(res));

  // Persistence channel: the host must have received set_language, exactly as
  // the top-bar picker persists the choice.
  const langPosts = dash.posts.filter(p => p && p.action === 'set_language');
  assert.equal(langPosts.at(-1).lang, 'tr', 'set_language posted to the host');

  await t.test('scoped skin.cyber.* wins over a shared key with a different value', () => {
    // Real data disambiguates: scoped-tr "BEYAZ LİSTE" ≠ shared-tr "Beyaz liste".
    assert.equal(TR['skin.cyber.dir.whitelist'], 'BEYAZ LİSTE');
    assert.equal(TR['dir.whitelist'], 'Beyaz liste', 'shared value differs — the contrast is real');
    assert.equal(tBridge('dir.whitelist', undefined, 'cyber'), 'BEYAZ LİSTE', 'scoped wins, NOT the shared value');
    assert.equal(tBridge('dir.blacklist', undefined, 'cyber'), 'KARA LİSTE');
  });

  await t.test('every skin.cyber.* key resolves through the bridge to its scoped value', () => {
    const cyberKeys = Object.keys(TR).filter(k => k.startsWith('skin.cyber.'));
    assert.ok(cyberKeys.length >= 50, 'cyber scoped block exists in tr.json (' + cyberKeys.length + ' keys)');
    for (const fullKey of cyberKeys) {
      const bare = fullKey.slice('skin.cyber.'.length);
      assert.equal(tBridge(bare, undefined, 'cyber'), TR[fullKey], bare);
      assert.notEqual(tBridge(bare, undefined, 'cyber'), bare, bare + ' must never fall through to the raw key');
    }
  });

  await t.test('shared dictionary is the fallback when no scoped key exists', () => {
    // These have no skin.cyber.* entry — the shared tr value must surface.
    assert.equal(TR['skin.cyber.status.connected'], undefined, 'no scoped entry');
    assert.equal(tBridge('status.connected', undefined, 'cyber'), TR['status.connected']); // 'BAĞLANDI'
    assert.equal(tBridge('status.idle', undefined, 'cyber'), TR['status.idle']);           // '⚪ Boşta'
    assert.equal(tBridge('connect.connecting', undefined, 'cyber'), TR['connect.connecting']); // 'BAĞLANIYOR'
  });

  await t.test('unknown keys return the raw key', () => {
    assert.equal(tBridge('no.such.key.anywhere', undefined, 'cyber'), 'no.such.key.anywhere');
    assert.equal(tBridge('no.such.key.anywhere'), 'no.such.key.anywhere');
  });

  await t.test('per-skin isolation: the same bare key resolves per skin id', () => {
    // state.connected exists in BOTH scoped blocks with different values.
    assert.equal(TR['skin.cyber.state.connected'], 'BAĞLI');
    assert.equal(TR['skin.nexus-gpn.state.connected'], 'BAĞLANDI');
    assert.equal(tBridge('state.connected', undefined, 'cyber'), 'BAĞLI');
    assert.equal(tBridge('state.connected', undefined, 'nexus-gpn'), 'BAĞLANDI');
    // brand: only nexus scopes it — cyber must fall through to the raw key,
    // never leak the nexus scoped value.
    assert.equal(TR['skin.cyber.brand'], undefined);
    assert.equal(TR['skin.nexus-gpn.brand'], 'AO GPN');
    assert.equal(tBridge('brand', undefined, 'cyber'), 'brand');
    assert.equal(tBridge('brand', undefined, 'nexus-gpn'), 'AO GPN');
    // panel.source: only cyber scopes it — nexus has neither scoped nor shared.
    assert.equal(tBridge('panel.source', undefined, 'nexus-gpn'), 'panel.source');
  });

  await t.test('params substitution runs after lookup', () => {
    const hint = TR['skin.nexus-gpn.routeHint']; // 'Rota modu: {mode} · Yakalama: {capture} · {state}'
    assert.equal(
      tBridge('routeHint', { mode: 'GPN', capture: 'TUN', state: 'OK' }, 'nexus-gpn'),
      hint.split('{mode}').join('GPN').split('{capture}').join('TUN').split('{state}').join('OK')
    );
    // Shared key, standard skin id.
    assert.equal(tBridge('boost.defined', { n: 3 }), TR['boost.defined'].split('{n}').join('3'));
  });

  await t.test('real CYBER and INFRA log keys substitute parameters through skinBridge.t', () => {
    const probes = [
      ['cyber', 'log.routeMode', { mode: 'GPN GAME TUNNEL' }],
      ['cyber', 'log.splitDir', { dir: 'WHITELIST' }],
      ['cyber', 'log.routeSet', { route: 'VPN', app: 'GAME.EXE' }],
      ['cyber', 'log.theme', { theme: 'CYBER YELLOW' }],
      ['infra', 'log.toggle', { state: 'CONNECT' }],
      ['infra', 'log.language', { lang: 'TR' }]
    ];
    for (const [skinId, key, params] of probes) {
      const fullKey = 'skin.' + skinId + '.' + key;
      assert.ok(Object.prototype.hasOwnProperty.call(TR, fullKey), fullKey + ' exists in tr.json');
      const expected = Object.keys(params).reduce(
        (value, name) => String(value).split('{' + name + '}').join(String(params[name])),
        TR[fullKey]
      );
      const actual = tBridge(key, params, skinId);
      assert.equal(actual, expected, skinId + '.' + key);
      for (const name of Object.keys(params)) assert.ok(!actual.includes('{' + name + '}'), skinId + '.' + key + ' replaced {' + name + '}');
    }
  });

  await t.test('the real bridge agrees with the pure contract implementation', () => {
    const probes = [
      ['panel.source', 'cyber'], ['route.direct', 'cyber'], ['route.proxy', 'cyber'],
      ['dir.whitelist', 'cyber'], ['dir.blacklist', 'cyber'], ['state.connected', 'cyber'],
      ['state.connected', 'nexus-gpn'], ['brand', 'nexus-gpn'], ['brand', 'cyber'],
      ['status.connected', 'cyber'], ['status.idle', 'cyber'], ['no.such.key', 'cyber']
    ];
    for (const [key, sid] of probes) {
      assert.equal(tBridge(key, undefined, sid), contractT(key, undefined, sid, TR), key + ' (' + sid + ')');
    }
    assert.equal(tBridge('boost.defined', { n: 2 }), contractT('boost.defined', { n: 2 }, undefined, TR));
    assert.equal(
      tBridge('routeHint', { mode: 'X', capture: 'Y', state: 'Z' }, 'nexus-gpn'),
      contractT('routeHint', { mode: 'X', capture: 'Y', state: 'Z' }, 'nexus-gpn', TR)
    );
  });
});
