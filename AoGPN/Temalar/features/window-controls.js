/* ==========================================================================
   features/window-controls.js — AoGPN dashboard module (pencere kontrolleri)
   --------------------------------------------------------------------------
   app.js ile aynı küresel pencere kapsamında, ancak app.js'den ÖNCE yüklenir
   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).
   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel
   takma adlarını alır. ========================================================================== */
(() => {
  'use strict';
  const { $ } = aogpn.util;
  const postToHost = aogpn.bridge.postToHost;
  const body = document.body;
  $('minimizeButton').addEventListener('click', () => {
    postToHost({ action: 'app_control', command: 'minimize' });
  });

  // Maximize/restore toggle. The icon flips optimistically; the host confirms the
  // real window state via window.setWindowMaximized so external transitions
  // (Win+Up/Down, snap) stay truthful too.
  const maximizeButton = $('maximizeButton');
  let isMaximized = false;
  const MAXIMIZE_ICON = '<svg class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8" viewBox="0 0 24 24" aria-hidden="true"><rect x="4" y="4" width="16" height="16" rx="1"/></svg>';
  const RESTORE_ICON = '<svg class="h-3.5 w-3.5" fill="none" stroke="currentColor" stroke-width="1.8" viewBox="0 0 24 24" aria-hidden="true"><path d="M8 8h10a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2Z"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></svg>';
  function renderMaximizeIcon() {
    maximizeButton.innerHTML = isMaximized ? RESTORE_ICON : MAXIMIZE_ICON;
    maximizeButton.setAttribute('aria-label', isMaximized ? 'Restore window' : 'Maximize window');
    maximizeButton.setAttribute('title', isMaximized ? 'Restore' : 'Maximize');
  }
  window.setWindowMaximized = (maximized) => {
    isMaximized = maximized === true;
    renderMaximizeIcon();
    body.classList.toggle('window-maximized', isMaximized);
  };
  maximizeButton.addEventListener('click', () => {
    isMaximized = !isMaximized;
    renderMaximizeIcon();
    postToHost({ action: 'app_control', command: 'maximize_toggle' });
  });
  $('closeButton').addEventListener('click', () => {
    postToHost({ action: 'app_control', command: 'close' });
  });
  // Dragging the title bar moves the window; a second press within the Windows
  // double-click window instead toggles maximize/restore.
  let lastTitleBarPress = 0;
  const DOUBLE_CLICK_MS = 400;
  $('windowDragRegion').addEventListener('pointerdown', (event) => {
    if (event.button !== 0) {
      return;
    }
    event.preventDefault();
    const now = Date.now();
    const isDoubleClick = now - lastTitleBarPress < DOUBLE_CLICK_MS;
    lastTitleBarPress = now;
    postToHost(isDoubleClick
      ? { action: 'app_control', command: 'maximize_toggle' }
      : { action: 'app_control', command: 'drag' });
  });

})();
