/* ==========================================================================
   features/game-boost.js — AoGPN dashboard feature module (Game Boost)
   --------------------------------------------------------------------------
   Manuel domain/IP kural paneli (beyaz/kara liste + WARP BSG API kısayolu) ve
   EXE sürükle-bırak akışı. Görünüm denetimi (yalnızca boost görünümünde aktif)
   koordinatördeki currentView'a aogpn.app.getCurrentView() ile bağlanır.
   ========================================================================== */
(() => {
  'use strict';
  const { $ } = aogpn.util;
  const postToHost = aogpn.bridge.postToHost;
  const t = aogpn.i18n.t;
  // ---------- Manual domain/IP rules (Game Boost) ----------
  function fillDomainActions() {
    const sel = $('boostDomainAction');
    if (!sel || sel.options.length) return;
    [['vpn', t('route.vpn')], ['direct', t('route.direct')], ['block', t('route.block')], ['warp', t('route.warp')]]
      .forEach(([key, label]) => {
        const opt = document.createElement('option');
        opt.value = key;
        opt.textContent = label;
        sel.appendChild(opt);
      });
    sel.value = 'vpn';
  }
  function setDomainPanel(open) {
    const panel = $('boostDomainPanel');
    if (!panel) return;
    panel.classList.toggle('hidden', !open);
    $('boostAddDomainBtn')?.setAttribute('aria-expanded', String(open));
    if (open) {
      fillDomainActions();
      const valueEl = $('boostDomainValue');
      if (valueEl) setTimeout(() => valueEl.focus(), 0);
    }
  }
  function submitDomainAdd() {
    const valueEl = $('boostDomainValue');
    const value = (valueEl?.value || '').trim();
    if (!value) return;
    const route = $('boostDomainAction')?.value || 'proxy';
    postToHost({ action: 'add_domain_route', value, route, displayName: value });
    if (valueEl) valueEl.value = '';
    setDomainPanel(false);
  }
  // One-click curated BSG API rule (escapefromtarkov.com → WARP).
  $('boostBszApiBtn')?.addEventListener('click', () => postToHost({
    action: 'add_domain_route',
    value: 'escapefromtarkov.com',
    route: 'warp',
    displayName: 'BSG API (escapefromtarkov.com)'
  }));
  $('boostAddDomainBtn')?.addEventListener('click', () => setDomainPanel($('boostDomainPanel')?.classList.contains('hidden') ?? false));
  $('boostDomainClose')?.addEventListener('click', () => setDomainPanel(false));
  $('boostDomainSubmit')?.addEventListener('click', submitDomainAdd);
  $('boostDomainValue')?.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
      e.preventDefault();
      submitDomainAdd();
    }
  });
  fillDomainActions();

  $('boostAutoGameConnect')?.addEventListener('change', event => postToHost({ action: 'set_auto_game_connect', enabled: event.target.checked }));
  $('boostAppFilter')?.addEventListener('input', () => {
    if (aogpn.app && typeof aogpn.app.renderSplitApps === 'function') aogpn.app.renderSplitApps();
  });

  // ---------- Drag & drop EXE files onto the Game Boost view ----------
  var _boostDropZone = $('viewBoost');
  var _boostOverlay = $('boostDropOverlay');
  var _dropFiles = [];
  var _dropCounter = 0;

  function _boostDropActive(active) {
    if (active) { _dropCounter++; } else { _dropCounter = Math.max(0, _dropCounter - 1); }
    _boostOverlay?.classList.toggle('hidden', _dropCounter === 0);
  }

  document.addEventListener('dragenter', function(e) {
    if (aogpn.app.getCurrentView() !== 'boost') return;
    e.preventDefault();
    var items = e.dataTransfer && e.dataTransfer.types;
    if (items && items.indexOf('Files') >= 0) _boostDropActive(true);
  });
  document.addEventListener('dragover', function(e) {
    if (aogpn.app.getCurrentView() !== 'boost' || _dropCounter === 0) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  });
  document.addEventListener('dragleave', function(e) {
    if (aogpn.app.getCurrentView() !== 'boost') return;
    _boostDropActive(false);
  });
  document.addEventListener('drop', function(e) {
    if (aogpn.app.getCurrentView() !== 'boost') return;
    e.preventDefault();
    _boostDropActive(false);
    var files = e.dataTransfer && e.dataTransfer.files;
    if (!files || !files.length) return;
    // Collect .exe names from the drop; the C# host resolves full paths.
    _dropFiles = [];
    for (var i = 0; i < files.length; i++) {
      var name = files[i].name || '';
      if (name.toLowerCase().endsWith('.exe')) _dropFiles.push(name);
    }
    if (_dropFiles.length > 0) {
      postToHost({ action: 'add_files', files: _dropFiles });
    }
  });
})();
