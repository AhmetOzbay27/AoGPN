/* ==========================================================================
   core/events.js — AoGPN dashboard core module (olay veriyolu)
   --------------------------------------------------------------------------
   Modüller birbirini doğrudan çağırmaz; 'skin-changed' gibi çapraz bildirimleri
   bu veriyolu üzerinden yayınlar. app.js'nin skin motoru olaya abone olur ve
   skinBridge.subscribe abonelerini besler (eski doğrudan skinNotify() çağrıları).
   ========================================================================== */
(() => {
  'use strict';
  const _subs = {};

  function on(event, cb) {
    (_subs[event] = _subs[event] || []).push(cb);
  }

  function emit(event, ...args) {
    (_subs[event] || []).forEach(cb => { try { cb(...args); } catch (e) { /* ignore */ } });
  }

  window.aogpn = window.aogpn || {};
  window.aogpn.events = { on, emit };
})();
