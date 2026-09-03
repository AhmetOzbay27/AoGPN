// ============================================================================
// Integration test: host-echo route override cleanup (onHostPush / nxOnHostPush)
// ----------------------------------------------------------------------------
// When a skin toggles an app's route it stores a local override so the card
// flips optimistically, then posts set_app_route to the host. The host is
// authoritative: on the next bridge push the skin must drop the override —
// but only once the host either confirmed the same action or the echo window
// (2.5 s) expired with a different action. This file drives the REAL
// onHostPush / nxOnHostPush subscribers (registered via bridge.subscribe at
// skin boot) through a controllable clock and asserts the rendered label and
// switch state follow the expected cleanup rules.
//
// Run with:
//   node --test Temalar/skins/route-echo.integration.test.js
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { loadSkin } = require('./skin-sandbox.js');

const ECHO_WINDOW_MS = 2500; // must match ROUTE_CONFIRM_MS / NX_ROUTE_CONFIRM_MS in the skins

function apps(vpnName) {
  return [
    { id: 1, processName: vpnName, displayName: vpnName, action: 'vpn', isRunning: true }
  ];
}

function setAction(skin, pname, action) {
  const app = skin.bridge.monitorSnapshot.apps.find(a => a.processName === pname);
  assert.ok(app, `app ${pname} must exist in the bridge snapshot`);
  app.action = action;
}

// ---------------------------------------------------------------------------
// CYBER
// ---------------------------------------------------------------------------

test('CYBER: host confirming the toggle clears the local override', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft'); // vpn -> direct override
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'optimistic override shown');
  assert.equal(skin.getSwitchOn('cs2'), false);

  // Host echoes the exact action the skin toggled -> override is redundant.
  setAction(skin, 'cs2', 'direct');
  skin.push();
  // Override cleared: authoritative action is now direct, label unchanged but
  // coming from the snapshot, not the override.
  assert.equal(skin.getLabel('cs2'), 'DIRECT');

  // Now the host changes the route (dashboard-side): no stale override left to
  // fight it, the snapshot value wins immediately.
  setAction(skin, 'cs2', 'vpn');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'VPN', 'authoritative action wins after override cleared');
  assert.equal(skin.getSwitchOn('cs2'), true);
});

test('CYBER: different action inside the echo window keeps the optimistic override', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft'); // vpn -> direct override @t=1000
  assert.equal(skin.getLabel('cs2'), 'DIRECT');

  // A push arrives before the window expires, reporting something else
  // (e.g. dashboard set block) — the override must survive so the flip isn't
  // yanked away while the host may still confirm it.
  skin.advance(ECHO_WINDOW_MS - 100); // still inside the window
  setAction(skin, 'cs2', 'block');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'override survives inside the window');
});

test('CYBER: different action after the echo window expires -> authoritative wins', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft'); // override direct @t=1000
  assert.equal(skin.getLabel('cs2'), 'DIRECT');

  skin.advance(ECHO_WINDOW_MS + 1); // window expired
  setAction(skin, 'cs2', 'block');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'BLOCK', 'expired override gives way to the authoritative action');
});

test('CYBER: same (unconfirmed) action inside the window keeps the override', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft'); // override direct @t=1000
  skin.advance(1000); // inside the window
  // The snapshot still says vpn (host hasn't echoed the toggle yet).
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'unconfirmed toggle stays optimistic');
});

test('CYBER: host rejecting the toggle drops the override after the window expires', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft'); // vpn -> direct override @t=1000
  assert.equal(skin.getLabel('cs2'), 'DIRECT');

  // Host REJECTS the toggle: the snapshot keeps reporting vpn (the original
  // action never changed). Inside the window the optimistic flip must survive,
  // exactly like the unconfirmed case.
  skin.advance(ECHO_WINDOW_MS - 100);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'rejected toggle stays optimistic inside the window');

  // After the window the unchanged echo is authoritative: the stale override
  // must drop even though the action is the same as before the toggle.
  skin.advance(101);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'VPN', 'unchanged echo wins after the window — override dropped');
  assert.equal(skin.getSwitchOn('cs2'), true);
});

// ---------------------------------------------------------------------------
// Race: the skin toggle and a dashboard-side route change fight over the same
// app. Deterministic because the sandbox clock and push channel let us pin
// down exactly when each event lands relative to the other.
// ---------------------------------------------------------------------------

test('CYBER: race — dashboard echo lands first, then the skin toggle starts from block', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  // Dashboard selects block; its echo reaches the skin before any toggle.
  setAction(skin, 'cs2', 'block');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'BLOCK', 'authoritative block rendered');

  // Now the user toggles: the current action is block, so the flip goes to vpn.
  skin.keydown(skin.getSwitch('cs2'), 'ArrowRight');
  assert.equal(skin.getLabel('cs2'), 'VPN', 'optimistic vpn override after the toggle');

  // The host has not confirmed vpn yet (snapshot still says block). Inside the
  // window the override must survive; after it, block wins again.
  skin.advance(ECHO_WINDOW_MS - 100);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'VPN', 'override survives inside the window');

  skin.advance(101);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'BLOCK', 'authoritative block wins after the window');
  assert.equal(skin.getSwitchOn('cs2'), false);
});

test('CYBER: race — toggle and dashboard echo at the exact same timestamp (zero latency)', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  // Both events land at t=1000: the skin posts vpn->direct, and the dashboard's
  // block echo arrives in the very same tick. pendingMs is 0, so the optimistic
  // override must win inside the window, block after it.
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft');
  setAction(skin, 'cs2', 'block');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'pendingMs=0 keeps the optimistic override');

  skin.advance(ECHO_WINDOW_MS + 1);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'BLOCK', 'authoritative block wins after the window');
  assert.equal(skin.getSwitchOn('cs2'), false);
});

test('CYBER: app removed from the boost list drops the stale override', () => {
  const skin = loadSkin('cyber', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft'); // override direct
  skin.bridge.monitorSnapshot.apps = [];
  skin.push(); // no app -> override cleared
  // App returns later with its authoritative action: no stale direct override.
  skin.bridge.monitorSnapshot.apps = apps('cs2');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'VPN', 're-added app shows authoritative action');
  assert.equal(skin.getSwitchOn('cs2'), true);
});

// ---------------------------------------------------------------------------
// NEXUS
// ---------------------------------------------------------------------------

test('NEXUS: host confirming the toggle clears the local override', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft'); // vpn -> direct override
  assert.equal(skin.getLabel('cs2'), 'DIRECT');

  setAction(skin, 'cs2', 'direct');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'confirmed direct stays');

  setAction(skin, 'cs2', 'vpn');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'VPN', 'authoritative action wins after override cleared');
  assert.equal(skin.getSwitchOn('cs2'), true);
});

test('NEXUS: different action inside the echo window keeps the optimistic override', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft');
  skin.advance(ECHO_WINDOW_MS - 100);
  setAction(skin, 'cs2', 'block');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'override survives inside the window');
});

test('NEXUS: different action after the echo window expires -> authoritative wins', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft');
  skin.advance(ECHO_WINDOW_MS + 1);
  setAction(skin, 'cs2', 'block');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'BLOCKED', 'expired override gives way to the authoritative action');
});

test('NEXUS: same (unconfirmed) action inside the window keeps the override', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft');
  skin.advance(1000);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'unconfirmed toggle stays optimistic');
});

test('NEXUS: host rejecting the toggle drops the override after the window expires', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft'); // vpn -> direct override @t=1000
  assert.equal(skin.getLabel('cs2'), 'DIRECT');

  // Host REJECTS the toggle: the snapshot keeps reporting vpn (the original
  // action never changed). Inside the window the optimistic flip must survive.
  skin.advance(ECHO_WINDOW_MS - 100);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'rejected toggle stays optimistic inside the window');

  // After the window the unchanged echo is authoritative: the stale override
  // must drop even though the action is the same as before the toggle.
  skin.advance(101);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'VPN', 'unchanged echo wins after the window — override dropped');
  assert.equal(skin.getSwitchOn('cs2'), true);
});

// ---------------------------------------------------------------------------
// Race: the skin toggle and a dashboard-side route change fight over the same
// app. Deterministic because the sandbox clock and push channel let us pin
// down exactly when each event lands relative to the other.
// ---------------------------------------------------------------------------

test('NEXUS: race — dashboard echo lands first, then the skin toggle starts from block', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  // Dashboard selects block; its echo reaches the skin before any toggle.
  setAction(skin, 'cs2', 'block');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'BLOCKED', 'authoritative block rendered');

  // Now the user toggles: the current action is block, so the flip goes to vpn.
  skin.keydown(skin.getSwitch('cs2'), 'ArrowRight');
  assert.equal(skin.getLabel('cs2'), 'VPN', 'optimistic vpn override after the toggle');

  // The host has not confirmed vpn yet (snapshot still says block). Inside the
  // window the override must survive; after it, block wins again.
  skin.advance(ECHO_WINDOW_MS - 100);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'VPN', 'override survives inside the window');

  skin.advance(101);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'BLOCKED', 'authoritative block wins after the window');
  assert.equal(skin.getSwitchOn('cs2'), false);
});

test('NEXUS: race — toggle and dashboard echo at the exact same timestamp (zero latency)', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  // Both events land at t=1000: the skin posts vpn->direct, and the dashboard's
  // block echo arrives in the very same tick. pendingMs is 0, so the optimistic
  // override must win inside the window, block after it.
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft');
  setAction(skin, 'cs2', 'block');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'DIRECT', 'pendingMs=0 keeps the optimistic override');

  skin.advance(ECHO_WINDOW_MS + 1);
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'BLOCKED', 'authoritative block wins after the window');
  assert.equal(skin.getSwitchOn('cs2'), false);
});

test('NEXUS: app removed from the boost list drops the stale override', () => {
  const skin = loadSkin('nexus', { monitorSnapshot: { apps: apps('cs2') } }, { now: 1000 });
  skin.keydown(skin.getSwitch('cs2'), 'ArrowLeft');
  skin.bridge.monitorSnapshot.apps = [];
  skin.push();
  skin.bridge.monitorSnapshot.apps = apps('cs2');
  skin.push();
  assert.equal(skin.getLabel('cs2'), 'VPN', 're-added app shows authoritative action');
  assert.equal(skin.getSwitchOn('cs2'), true);
});

// ---------------------------------------------------------------------------
// Contract: echo window threshold is shared and symmetric
// ---------------------------------------------------------------------------

test('both skins: just inside the window keeps override, just past it drops it', () => {
  ['cyber', 'nexus'].forEach(skinDir => {
    // Inside the window.
    const inner = loadSkin(skinDir, { monitorSnapshot: { apps: apps('cs2') } }, { now: 500 });
    inner.keydown(inner.getSwitch('cs2'), 'ArrowLeft');
    inner.advance(ECHO_WINDOW_MS - 1);
    setAction(inner, 'cs2', 'block');
    inner.push();
    assert.equal(inner.getLabel('cs2'), 'DIRECT', `${skinDir}: inside window keeps override`);

    // Just past the window (the skins use `pendingMs > ECHO_WINDOW_MS`, so the
    // exact boundary still keeps the override — 1 ms more drops it).
    const outer = loadSkin(skinDir, { monitorSnapshot: { apps: apps('cs2') } }, { now: 500 });
    outer.keydown(outer.getSwitch('cs2'), 'ArrowLeft');
    outer.advance(ECHO_WINDOW_MS + 1);
    setAction(outer, 'cs2', 'block');
    outer.push();
    assert.notEqual(outer.getLabel('cs2'), 'DIRECT', `${skinDir}: past the window the override is dropped`);
  });
});
