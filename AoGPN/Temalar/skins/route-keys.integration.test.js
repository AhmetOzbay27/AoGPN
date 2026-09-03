// ============================================================================
// Integration test: the REAL keydown listeners of the CYBER and NEXUS skins
// ----------------------------------------------------------------------------
// route-keys.test.js covers the pure decision function (routeFromKey). This
// file goes one level deeper: it loads the actual skin.html document into jsdom
// and boots the real skin.js against it (as the iframe would), renders the game
// lists, and drives the REAL addEventListener('keydown') handlers with
// synthetic events. The DOM is a real jsdom tree — innerHTML parsing, classList,
// datasets and event propagation all behave like a browser — so the tests assert
// the host messages the listeners produce match the shared keyboard contract.
// A future edit that breaks the wiring (wrong key check, wrong route, missing
// listener) fails here even if the pure module stays green.
//
// Run with:
//   node --test Temalar/skins/route-keys.integration.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { loadSkin } = require('./skin-sandbox.js');
const { routeFromKey } = require('./route-keys.js');

const APPS = [
  { id: 1, processName: 'cs2', displayName: 'Counter-Strike 2', action: 'vpn', isRunning: true },
  { id: 2, processName: 'VALORANT', displayName: 'Valorant', action: 'vpn+proxy', isRunning: true },
  { id: 3, processName: 'fortnite', displayName: 'Fortnite', action: 'direct', isRunning: false }
];

function postsFor(skin, pname) {
  return skin.bridge._posts.filter(p => p.action === 'set_app_route' && p.processName === pname);
}

// ---------------------------------------------------------------------------
// CYBER skin — real listener behavior
// ---------------------------------------------------------------------------

test('CYBER: ArrowLeft on a tunneled app posts set_app_route direct', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('cs2');
  assert.ok(sw, 'cs2 switch must be rendered and wired');
  skin.keydown(sw, 'ArrowLeft');
  const posts = postsFor(skin, 'cs2');
  assert.equal(posts.length, 1);
  assert.equal(posts[0].route, 'direct');
});

test('CYBER: Enter flips a tunneled app to direct', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('cs2');
  skin.keydown(sw, 'Enter');
  const posts = postsFor(skin, 'cs2');
  assert.equal(posts.length, 1);
  assert.equal(posts[0].route, 'direct');
});

test('CYBER: ArrowRight on an already-tunneled app is a no-op (no host post)', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('cs2'); // action vpn
  skin.keydown(sw, 'ArrowRight');
  assert.equal(postsFor(skin, 'cs2').length, 0);
});

test('CYBER: ArrowRight on a non-tunneled app turns the tunnel on', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('fortnite'); // action direct
  skin.keydown(sw, 'ArrowRight');
  const posts = postsFor(skin, 'fortnite');
  assert.equal(posts.length, 1);
  assert.equal(posts[0].route, 'vpn');
});

test('CYBER: ArrowLeft on a non-tunneled app is a no-op', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('fortnite'); // action direct
  skin.keydown(sw, 'ArrowLeft');
  assert.equal(postsFor(skin, 'fortnite').length, 0);
});

test('CYBER: unrelated key never posts a route change', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('cs2');
  ['Tab', 'ArrowUp', 'x'].forEach(k => skin.keydown(sw, k));
  assert.equal(postsFor(skin, 'cs2').length, 0);
});

// ---------------------------------------------------------------------------
// NEXUS skin — real listener behavior
// ---------------------------------------------------------------------------

test('NEXUS: ArrowLeft on a tunneled app posts set_app_route direct', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('cs2');
  assert.ok(sw, 'cs2 switch must be rendered and wired');
  skin.keydown(sw, 'ArrowLeft');
  const posts = postsFor(skin, 'cs2');
  assert.equal(posts.length, 1);
  assert.equal(posts[0].route, 'direct');
});

test('NEXUS: Space flips a tunneled app to direct', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('cs2');
  skin.keydown(sw, ' ');
  const posts = postsFor(skin, 'cs2');
  assert.equal(posts.length, 1);
  assert.equal(posts[0].route, 'direct');
});

test('NEXUS: ArrowRight on an already-tunneled app is a no-op', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('VALORANT'); // vpn+proxy — never downgraded
  skin.keydown(sw, 'ArrowRight');
  assert.equal(postsFor(skin, 'VALORANT').length, 0);
});

test('NEXUS: ArrowRight on a non-tunneled app turns the tunnel on', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('fortnite'); // direct
  skin.keydown(sw, 'ArrowRight');
  const posts = postsFor(skin, 'fortnite');
  assert.equal(posts.length, 1);
  assert.equal(posts[0].route, 'vpn');
});

test('NEXUS: ArrowLeft on a non-tunneled app is a no-op', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('fortnite'); // direct
  skin.keydown(sw, 'ArrowLeft');
  assert.equal(postsFor(skin, 'fortnite').length, 0);
});

test('NEXUS: unrelated key never posts a route change', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('cs2');
  ['Tab', 'ArrowUp', 'x'].forEach(k => skin.keydown(sw, k));
  assert.equal(postsFor(skin, 'cs2').length, 0);
});

// ---------------------------------------------------------------------------
// Cross-check: the listeners agree with the pure module for every state
// ---------------------------------------------------------------------------

test('both skins: listener posts exactly what routeFromKey decides', () => {
  ['cyber', 'nexus'].forEach(skinDir => {
    const cases = [
      ['cs2', 'vpn', 'ArrowLeft', 'direct'],
      ['cs2', 'vpn', 'Enter', 'direct'],
      ['VALORANT', 'vpn+proxy', ' ', 'direct'],
      ['VALORANT', 'vpn+proxy', 'ArrowRight', null],
      ['fortnite', 'direct', 'ArrowRight', 'vpn'],
      ['fortnite', 'direct', 'Enter', 'vpn'],
      ['fortnite', 'direct', 'ArrowLeft', null]
    ];
    cases.forEach(([pname, action, key, expectedRoute]) => {
      const skin = loadSkin(skinDir, {
        monitorSnapshot: { apps: [{ id: 1, processName: pname, displayName: pname, action, isRunning: true }] }
      });
      const sw = skin.getSwitch(pname);
      assert.ok(sw, `${skinDir}: switch for ${pname} must exist`);
      skin.bridge._posts.length = 0;
      skin.keydown(sw, key);
      const posts = postsFor(skin, pname);
      // The pure contract decides whether ANY post should happen at all.
      const decided = routeFromKey(key, action);
      if (decided === null) {
        assert.equal(posts.length, 0, `${skinDir}/${pname} ${key} should be a no-op`);
      } else {
        assert.equal(posts.length, 1, `${skinDir}/${pname} ${key} should post once`);
        assert.equal(posts[0].route, decided, `${skinDir}/${pname} ${key} should post ${decided}`);
      }
    });
  });
});

// ---------------------------------------------------------------------------
// The DOM is real: events bubble through the tree like in a browser. A stub
// DOM (per-element addEventListener with no tree) could never satisfy this.
// ---------------------------------------------------------------------------

test('sandbox: keydown on a switch propagates through the real DOM tree', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: APPS } });
  const sw = skin.getSwitch('cs2');
  let seen = 0;
  let targetClass = null;
  skin.document.addEventListener('keydown', e => {
    seen++;
    targetClass = e.target && e.target.className;
  });
  skin.keydown(sw, 'ArrowLeft');
  assert.equal(seen, 1, 'bubbled keydown reached the document-level listener');
  assert.ok(String(targetClass).includes('cy-switch'), 'event target is the real switch element in the tree');
});
