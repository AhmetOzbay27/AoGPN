// ============================================================================
// AoGPN skin switch keyboard logic (shared, pure)
// ----------------------------------------------------------------------------
// Both standalone skins (CYBER, NEXUS) wire their per-app route switches with
// the same keyboard contract:
//   Enter / Space      -> flip the route (tunneled -> direct, else -> vpn)
//   ArrowRight         -> only turn the tunnel ON  (non-tunneled -> vpn)
//   ArrowLeft          -> only turn the tunnel OFF (tunneled -> direct)
//   any other key      -> no-op
// This file holds ONLY the pure decision function so the exact same logic runs
// in the browser (each skin.html loads it via <script>) and in the automated
// node test (route-keys.test.js) — no DOM, no bridge, no side effects.
//
// UMD-ish export: `module.exports` under node, `window.AoGPNRouteKeys` in the
// browser iframe.
// ============================================================================
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.AoGPNRouteKeys = factory();
  }
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // Tunneled actions are the ones that send traffic through the tunnel.
  function isTunneled(action) {
    return action === 'vpn' || action === 'vpn+proxy' || action === 'proxy';
  }

  // Resolve the target route for a keypress given the app's CURRENT effective
  // action. Returns 'vpn' | 'direct' when the key should change the route, or
  // null when the key is a no-op in that state (ArrowRight on an already
  // tunneled app, ArrowLeft on a non-tunneled app, unrelated keys).
  function routeFromKey(key, currentAction) {
    if (key === 'Enter' || key === ' ') {
      return isTunneled(currentAction) ? 'direct' : 'vpn';
    }
    if (key === 'ArrowRight') {
      // Only ever turn the tunnel ON; never downgrade vpn+proxy to vpn.
      return isTunneled(currentAction) ? null : 'vpn';
    }
    if (key === 'ArrowLeft') {
      // Only ever turn the tunnel OFF; never re-toggle a direct app.
      return isTunneled(currentAction) ? 'direct' : null;
    }
    return null;
  }

  return { isTunneled, routeFromKey };
});
