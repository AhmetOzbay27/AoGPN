/* ==========================================================================
   core/bridge.js — AoGPN dashboard module (WebView2 host köprüsü)
   --------------------------------------------------------------------------
   app.js ile aynı küresel pencere kapsamında, ancak app.js'den ÖNCE yüklenir
   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).
   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel
   takma adlarını alır. ========================================================================== */
(() => {
  'use strict';
  function hasWebViewBridge() {
    return Boolean(
      window.chrome
      && window.chrome.webview
      && typeof window.chrome.webview.postMessage === 'function'
    );
  }

  function postToHost(message) {
    if (!hasWebViewBridge()) {
      return false;
    }

    try {
      // Send a JSON string so the WPF side can reject non-string/oversized payloads.
      window.chrome.webview.postMessage(JSON.stringify(message));
      return true;
    } catch {
      return false;
    }
  }

  window.aogpn = window.aogpn || {};
  window.aogpn.bridge = { hasWebViewBridge, postToHost };
})();
