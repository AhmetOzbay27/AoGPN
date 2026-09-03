// ============================================================================
// Automated tests for the switch keyboard behavior shared by the CYBER and
// NEXUS standalone skins (Temalar/skins/route-keys.js).
// ----------------------------------------------------------------------------
// Run with:
//   node --test Temalar/skins/route-keys.test.js
//
// The module under test is the SAME pure module both skins load via
// <script src="../route-keys.js">, so these tests verify the exact logic the
// browser runs — Enter/Space flip, ArrowRight only turns the tunnel on,
// ArrowLeft only turns it off, anything else is a no-op.
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { isTunneled, routeFromKey } = require('./route-keys.js');

// ----------------------------------------------------------------------------
// isTunneled
// ----------------------------------------------------------------------------
test('isTunneled: tunneled actions are vpn / vpn+proxy / proxy', () => {
  assert.equal(isTunneled('vpn'), true);
  assert.equal(isTunneled('vpn+proxy'), true);
  assert.equal(isTunneled('proxy'), true);
});

test('isTunneled: direct / block / empty / unknown are not tunneled', () => {
  assert.equal(isTunneled('direct'), false);
  assert.equal(isTunneled('block'), false);
  assert.equal(isTunneled(''), false);
  assert.equal(isTunneled(undefined), false);
  assert.equal(isTunneled(null), false);
  assert.equal(isTunneled('whatever'), false);
});

// ----------------------------------------------------------------------------
// Enter / Space — flip the route
// ----------------------------------------------------------------------------
test('Enter: flips a tunneled app to direct', () => {
  assert.equal(routeFromKey('Enter', 'vpn'), 'direct');
  assert.equal(routeFromKey('Enter', 'vpn+proxy'), 'direct');
  assert.equal(routeFromKey('Enter', 'proxy'), 'direct');
});

test('Enter: flips a non-tunneled app to vpn', () => {
  assert.equal(routeFromKey('Enter', 'direct'), 'vpn');
  assert.equal(routeFromKey('Enter', 'block'), 'vpn');
  assert.equal(routeFromKey('Enter', ''), 'vpn');
  assert.equal(routeFromKey('Enter', undefined), 'vpn');
  assert.equal(routeFromKey('Enter', null), 'vpn');
});

test('Space: behaves exactly like Enter', () => {
  assert.equal(routeFromKey(' ', 'vpn'), 'direct');
  assert.equal(routeFromKey(' ', 'direct'), 'vpn');
  assert.equal(routeFromKey(' ', 'vpn+proxy'), 'direct');
  assert.equal(routeFromKey(' ', 'block'), 'vpn');
});

// ----------------------------------------------------------------------------
// ArrowRight — only turn the tunnel ON
// ----------------------------------------------------------------------------
test('ArrowRight: turns a non-tunneled app on', () => {
  assert.equal(routeFromKey('ArrowRight', 'direct'), 'vpn');
  assert.equal(routeFromKey('ArrowRight', 'block'), 'vpn');
  assert.equal(routeFromKey('ArrowRight', ''), 'vpn');
  assert.equal(routeFromKey('ArrowRight', undefined), 'vpn');
});

test('ArrowRight: no-op on tunneled apps (never downgrades vpn+proxy to vpn)', () => {
  assert.equal(routeFromKey('ArrowRight', 'vpn'), null);
  assert.equal(routeFromKey('ArrowRight', 'vpn+proxy'), null);
  assert.equal(routeFromKey('ArrowRight', 'proxy'), null);
});

// ----------------------------------------------------------------------------
// ArrowLeft — only turn the tunnel OFF
// ----------------------------------------------------------------------------
test('ArrowLeft: turns a tunneled app off', () => {
  assert.equal(routeFromKey('ArrowLeft', 'vpn'), 'direct');
  assert.equal(routeFromKey('ArrowLeft', 'vpn+proxy'), 'direct');
  assert.equal(routeFromKey('ArrowLeft', 'proxy'), 'direct');
});

test('ArrowLeft: no-op on non-tunneled apps (never re-toggles back on)', () => {
  assert.equal(routeFromKey('ArrowLeft', 'direct'), null);
  assert.equal(routeFromKey('ArrowLeft', 'block'), null);
  assert.equal(routeFromKey('ArrowLeft', ''), null);
  assert.equal(routeFromKey('ArrowLeft', undefined), null);
});

// ----------------------------------------------------------------------------
// Unrelated keys — always no-op regardless of the current route
// ----------------------------------------------------------------------------
test('other keys: no-op in every route state', () => {
  ['Tab', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'a', 'x', 'Shift', 'Enter2', ''].forEach(key => {
    assert.equal(routeFromKey(key, 'vpn'), null, key + ' on tunneled');
    assert.equal(routeFromKey(key, 'direct'), null, key + ' on direct');
    assert.equal(routeFromKey(key, undefined), null, key + ' on undefined');
  });
});

// ----------------------------------------------------------------------------
// Contract consistency: the skins' switch semantics as documented in both
// skin.js files must match what routeFromKey returns.
// ----------------------------------------------------------------------------
test('keyboard contract matrix (documented behavior)', () => {
  const rows = [
    // key, current action, expected result
    ['Enter', 'vpn', 'direct'],
    ['Enter', 'vpn+proxy', 'direct'],
    ['Enter', 'direct', 'vpn'],
    [' ', 'proxy', 'direct'],
    [' ', 'block', 'vpn'],
    ['ArrowRight', 'direct', 'vpn'],
    ['ArrowRight', 'block', 'vpn'],
    ['ArrowRight', 'vpn', null],
    ['ArrowRight', 'vpn+proxy', null],
    ['ArrowLeft', 'vpn', 'direct'],
    ['ArrowLeft', 'proxy', 'direct'],
    ['ArrowLeft', 'direct', null],
    ['ArrowLeft', '', null]
  ];
  rows.forEach(([key, action, expected]) => {
    assert.equal(routeFromKey(key, action), expected, `${key} on "${action}" should be ${expected}`);
  });
});
