/* ==========================================================================
   core/util.js — AoGPN dashboard core module (ortak yardımcılar)
   --------------------------------------------------------------------------
   Tüm modüllerin paylaştığı saf fonksiyonlar. app.js aynı fonksiyonları kendi
   kapsamına takma ad olarak alır (const { $, escHtml } = aogpn.util).
   ========================================================================== */
(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);

  const escHtml = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  window.aogpn = window.aogpn || {};
  window.aogpn.util = { $, escHtml };
})();
