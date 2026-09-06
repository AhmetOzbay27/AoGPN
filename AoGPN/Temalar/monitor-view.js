/* ==========================================================================
   AoGPN dashboard — Connection Monitor view module (Windows-style views)
   --------------------------------------------------------------------------
   Companion to Temalar/boost-view.js: adds the same Explorer-style display
   options to the Performance → Connection Monitor table, again as a fully
   standalone module loaded AFTER Temalar/app.js (see the <script> tags in
   vpn-gpn-dashboard.html):

     • View modes — Details (the classic connection table, rendered by
       app.js), Small / Medium / Large icons (grid tiles showing the REAL
       executable icon the host resolves through window.setAppIcons, plus
       remote address, route badge, protocol, state and country).
     • Grouping ("kategorileme") — group live connections by Route,
       Protocol, State, Country or Application with collapsible headers.

   The module talks to the dashboard only through the public window API it
   wraps (updateMonitorSnapshot, setAppIcons, applyLanguage) and reuses the
   exact filtering rules of the details table (monitorFilter text +
   "Hide listeners" checkbox). The tile route select posts the same
   set_app_route contract as the ASSIGN column. Preferences persist in
   localStorage under its own key.
   ========================================================================== */
(() => {
  'use strict';

  const filterInput = document.getElementById('monitorFilter');
  const hideListenersInput = document.getElementById('monitorHideListeners');
  const tbody = document.getElementById('monitorConnectionsBody');
  if (!filterInput || !tbody) return;

  const LS_KEY = 'aogpn.monitorView.v1';

  // ------------------------------------------------------------------
  // State
  // ------------------------------------------------------------------

  let snapshot = { connections: [], apps: [] };
  const iconCache = {}; // lowercased exe path -> data URI (mirror of app.js)
  let viewMode = 'details'; // details | small | medium | large
  let groupBy = 'none'; // none | route | protocol | state | country | app
  const collapsedGroups = new Set();

  try {
    const saved = JSON.parse(localStorage.getItem(LS_KEY) || '{}');
    if (['details', 'small', 'medium', 'large'].includes(saved.mode)) viewMode = saved.mode;
    if (['none', 'route', 'protocol', 'state', 'country', 'app'].includes(saved.groupBy)) groupBy = saved.groupBy;
  } catch (e) { /* no localStorage */ }

  function persistPrefs() {
    try { localStorage.setItem(LS_KEY, JSON.stringify({ mode: viewMode, groupBy })); } catch (e) { /* ignore */ }
  }

  // ------------------------------------------------------------------
  // i18n + shared helpers (same pipeline as boost-view.js)
  // ------------------------------------------------------------------

  const L = (key, fallback, params) => {
    let value = null;
    if (typeof window.__aogpnT === 'function') value = window.__aogpnT(key, params);
    return (value !== null && value !== undefined && value !== '') ? value : fallback;
  };

  const ROUTE_FALLBACKS = { vpn: 'VPN', proxy: 'Proxy', 'vpn+proxy': 'VPN + Proxy', direct: 'Direct', block: 'Block', warp: 'WARP', '': '—' };

  const esc = (v) => String(v == null ? '' : v)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

  function routeBadge(tag, label) {
    const normalized = ['proxy', 'direct', 'block', 'warp'].includes(tag) ? tag : 'unknown';
    return `<span class="route-badge ${normalized}">${esc(label)}</span>`;
  }

  function routeLabelFor(item) {
    return item.routeText || ROUTE_FALLBACKS[item.routeTag] || item.routeTag || '—';
  }

  // Same effective-route rule as the details table: the ASSIGN dropdown and the
  // badges both follow the blacklist inversion when it is active.
  function effectiveRoute(action) {
    const blacklist = snapshot.invertManualRouting === true && snapshot.mode === 'manual';
    if (!blacklist) return action;
    return action === 'direct' ? 'vpn' : (action === 'block' ? 'block' : 'direct');
  }

  function configuredActionFor(processName) {
    if (!processName) return '';
    return (snapshot.apps || []).find(item =>
      item.entryType === 'app' && String(item.processName).toLowerCase() === String(processName).toLowerCase())?.action || '';
  }

  function configuredWarpNodeFor(processName) {
    if (!processName) return '';
    return (snapshot.apps || []).find(item =>
      item.entryType === 'app' && String(item.processName).toLowerCase() === String(processName).toLowerCase())?.warpNodeIndexId || '';
  }

  // ------------------------------------------------------------------
  // Toolbar (injected into the monitor filter row, after the traffic note)
  // ------------------------------------------------------------------

  let modeButtons = [];
  let groupSelect = null;

  const VIEW_ICONS = {
    details: '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M4 5h16v2H4V5Zm0 6h16v2H4v-2Zm0 6h16v2H4v-2Z"/></svg>',
    small: '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M4 4h7v7H4V4Zm9 0h7v7h-7V4ZM4 13h7v7H4v-7Zm9 0h7v7h-7v-7Z"/></svg>',
    medium: '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M4 4h5v5H4V4Zm11 0h5v5h-5V4ZM4 15h5v5H4v-5Zm11 0h5v5h-5v-5Z"/></svg>',
    large: '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M3 3h8v8H3V3Zm10 0h8v8h-8V3ZM3 13h8v8H3v-8Zm10 0h8v8h-8v-8Z"/></svg>'
  };
  const MODE_LABEL_KEYS = {
    details: ['monitor.view.details', 'Details'],
    small: ['monitor.view.small', 'Small icons'],
    medium: ['monitor.view.medium', 'Medium icons'],
    large: ['monitor.view.large', 'Large icons']
  };

  function buildToolbar() {
    const row = filterInput.closest('.flex.flex-wrap');
    if (!row) return;

    const toolbar = document.createElement('div');
    toolbar.className = 'mon-view-toolbar';

    modeButtons = [];
    ['details', 'small', 'medium', 'large'].forEach(mode => {
      const [key, fallback] = MODE_LABEL_KEYS[mode];
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'mon-view-mode-btn';
      btn.dataset.viewMode = mode;
      btn.title = L(key, fallback);
      btn.setAttribute('aria-label', L(key, fallback));
      btn.setAttribute('aria-pressed', String(mode === viewMode));
      btn.innerHTML = VIEW_ICONS[mode];
      btn.addEventListener('click', () => setMode(mode));
      toolbar.appendChild(btn);
      modeButtons.push(btn);
    });

    const divider = document.createElement('span');
    divider.className = 'mon-view-divider';
    toolbar.appendChild(divider);

    const groupLabel = document.createElement('span');
    groupLabel.className = 'mon-view-toolbar-label';
    groupLabel.textContent = L('monitor.view.groupBy', 'Group by');
    toolbar.appendChild(groupLabel);

    groupSelect = document.createElement('select');
    groupSelect.setAttribute('aria-label', L('monitor.view.groupBy', 'Group by'));
    groupSelect.innerHTML = [
      ['none', L('monitor.view.groupNone', 'None')],
      ['route', L('monitor.view.groupRoute', 'Route')],
      ['protocol', L('monitor.view.groupProtocol', 'Protocol')],
      ['state', L('monitor.view.groupState', 'State')],
      ['country', L('monitor.view.groupCountry', 'Country')],
      ['app', L('monitor.view.groupApp', 'Application')]
    ].map(([value, label]) => `<option value="${value}"${value === groupBy ? ' selected' : ''}>${esc(label)}</option>`).join('');
    groupSelect.addEventListener('change', () => {
      groupBy = groupSelect.value;
      persistPrefs();
      renderGrid();
    });
    toolbar.appendChild(groupSelect);

    row.appendChild(toolbar);
    syncToolbar();
  }

  function syncToolbar() {
    modeButtons.forEach(btn => {
      const on = btn.dataset.viewMode === viewMode;
      btn.classList.toggle('active', on);
      btn.setAttribute('aria-pressed', String(on));
    });
    if (groupSelect) groupSelect.value = groupBy;
  }

  // ------------------------------------------------------------------
  // Grid host (replaces the table in icon modes)
  // ------------------------------------------------------------------

  const tableWrap = tbody.closest('.overflow-x-auto');
  const gridHost = document.createElement('div');
  gridHost.id = 'monitorGridView';
  gridHost.className = 'mon-view-grid-host hidden';
  if (tableWrap && tableWrap.parentNode) {
    tableWrap.parentNode.insertBefore(gridHost, tableWrap.nextSibling);
  }

  function setMode(mode) {
    viewMode = mode;
    persistPrefs();
    syncToolbar();
    applyVisibility();
    if (viewMode !== 'details') renderGrid();
  }

  function applyVisibility() {
    if (!tableWrap) return;
    const gridMode = viewMode !== 'details';
    tableWrap.classList.toggle('hidden', gridMode);
    if (gridHost.parentNode) {
      gridHost.classList.toggle('hidden', !gridMode);
      gridHost.classList.toggle('large', viewMode === 'large');
      gridHost.classList.toggle('medium', viewMode === 'medium');
      gridHost.classList.toggle('small', viewMode === 'small');
    }
  }

  // ------------------------------------------------------------------
  // Filtering — identical rules to the details table (app.js)
  // ------------------------------------------------------------------

  function connectionMatches(item, filter) {
    if (!filter) return true;
    const haystack = [item.processName, item.displayName, item.remoteAddress, item.countryText, item.asnText, item.routeText].join(' ');
    return haystack.toLowerCase().includes(filter.toLowerCase());
  }

  function visibleConnections() {
    const filter = (filterInput.value || '').trim();
    const hideListeners = hideListenersInput ? hideListenersInput.checked !== false : true;
    return (snapshot.connections || []).filter(item => {
      if (hideListeners && item.protocol === 'TCP' && item.state === 'Listen') return false;
      return connectionMatches(item, filter);
    });
  }

  // ------------------------------------------------------------------
  // Grouping
  // ------------------------------------------------------------------

  const GROUP_ORDER = {
    route: ['proxy', 'direct', 'block', 'warp', 'unknown'],
    protocol: ['TCP', 'UDP']
  };

  function groupOf(item) {
    switch (groupBy) {
      case 'route': {
        const tag = item.routeTag || 'unknown';
        return { key: tag, label: routeLabelFor(item) };
      }
      case 'protocol':
        return { key: item.protocol || '—', label: item.protocol || '—' };
      case 'state':
        return { key: item.state || '—', label: item.state || '—' };
      case 'country': {
        const label = [item.countryText, item.asnText].filter(Boolean).join(' · ') || '—';
        return { key: label, label };
      }
      case 'app': {
        const label = item.displayName || item.processName || 'Unknown';
        return { key: label, label };
      }
      default:
        return { key: '', label: '' };
    }
  }

  function groupItems(items) {
    if (groupBy === 'none') return [{ key: '', label: '', items }];

    const byKey = new Map();
    const order = GROUP_ORDER[groupBy] || [];
    items.forEach(item => {
      const g = groupOf(item);
      if (!byKey.has(g.key)) byKey.set(g.key, { key: g.key, label: g.label, items: [] });
      byKey.get(g.key).items.push(item);
    });

    return [...byKey.values()].sort((a, b) => {
      const ai = order.indexOf(a.key);
      const bi = order.indexOf(b.key);
      if (ai !== -1 && bi !== -1) return ai - bi;
      if (ai !== -1) return -1;
      if (bi !== -1) return 1;
      return String(a.label).localeCompare(String(b.label));
    });
  }

  // ------------------------------------------------------------------
  // Rendering
  // ------------------------------------------------------------------

  function iconMarkup(exePath, fallbackText, cls) {
    const key = typeof exePath === 'string' ? exePath.trim().toLowerCase() : '';
    const uri = key && iconCache[key];
    if (uri) return `<img src="${uri}" alt="" class="app-icon ${cls}" draggable="false" />`;
    const label = String(fallbackText || 'APP').slice(0, 2).toUpperCase() || '?';
    return `<span class="app-icon ${cls}">${esc(label)}</span>`;
  }

  function stateDot(state) {
    const s = String(state || '');
    // State values arrive localized (Kuruldu / Established / Dinleniyor…).
    if (/kurd|estab/i.test(s)) return 'on';
    if (/listen|dinl/i.test(s)) return 'warn';
    return '';
  }

  function assignSelect(item) {
    const processName = item.processName || '';
    const displayName = item.displayName || processName;
    if (!processName) {
      return '<span class="mon-view-noselect">—</span>';
    }
    const selected = configuredActionFor(processName);
    const normalized = ['proxy', 'vpn+proxy'].includes(selected) ? 'vpn' : selected;
    const value = ['vpn', 'direct', 'block', 'warp'].includes(normalized) ? normalized : '';
    const options = [
      ['', L('route.assign', 'Assign route…')],
      ['vpn', L('route.vpn', 'VPN')],
      ['direct', L('route.direct', 'Direct')],
      ['block', L('route.block', 'Block')],
      ['warp', L('route.warp', 'WARP')]
    ].map(([val, label]) => `<option value="${val}"${val === value ? ' selected' : ''}>${esc(label)}</option>`).join('');
    return `<select data-route-process="${esc(processName)}" data-route-display="${esc(displayName)}" title="${esc(L('route.assign', 'Assign route…'))}">${options}</select>`;
  }

  function tileMarkup(item, size) {
    const display = item.displayName || item.processName || 'Unknown';
    const processName = item.processName || '';
    const iconCls = size === 'large' ? 'w-14 h-14' : size === 'medium' ? 'w-10 h-10' : 'w-8 h-8';
    const route = item.routeTag || '';
    const remote = item.remoteAddress || '—';
    const protocol = item.protocol || '—';
    const state = item.state || '—';
    const country = [item.countryText, item.asnText].filter(Boolean).join(' · ') || '—';
    return `<div class="mon-view-tile">
      <div class="mon-view-tile-icon">${iconMarkup(item.exePath, display, iconCls)}<span class="mon-view-status ${stateDot(item.state)}"></span></div>
      <p class="mon-view-tile-name" title="${esc(display)}">${esc(display)}</p>
      <p class="mon-view-tile-exe" title="${esc(processName)}">${esc(processName)}${item.pid ? ' · PID ' + esc(String(item.pid)) : ''}</p>
      <p class="mon-view-tile-remote" title="${esc(remote)}">${esc(remote)}</p>
      <div class="mon-view-tile-route">${routeBadge(route, routeLabelFor(item))}</div>
      <div class="mon-view-tile-meta"><span class="mon-view-chip">${esc(protocol)}</span><span class="mon-view-chip">${esc(state)}</span></div>
      <p class="mon-view-tile-country" title="${esc(country)}">${esc(country)}</p>
      <div class="mon-view-tile-actions">${assignSelect(item)}</div>
    </div>`;
  }

  function renderGrid() {
    if (!gridHost.parentNode) return;
    const items = visibleConnections();
    if (items.length === 0) {
      const all = snapshot.connections || [];
      const note = all.length === 0
        ? L('monitor.view.empty', 'No visible connections.')
        : L('monitor.view.noMatch', 'No connections match the filter.');
      gridHost.innerHTML = `<p class="mon-view-empty">${esc(note)}</p>`;
      return;
    }

    gridHost.innerHTML = groupItems(items).map(group => {
      const groupKey = groupBy === 'none' ? '' : String(group.key);
      const collapsed = groupKey !== '' && collapsedGroups.has(groupKey);
      const tiles = group.items
        .slice()
        .sort((a, b) => String(a.displayName || a.processName || '').localeCompare(String(b.displayName || b.processName || '')))
        .map(item => tileMarkup(item, viewMode))
        .join('');
      const head = groupKey === ''
        ? ''
        : `<div class="mon-view-group-head" data-group="${esc(groupKey)}" role="button" tabindex="0" aria-expanded="${String(!collapsed)}">
            <span class="chev">▾</span><b>${esc(group.label)}</b><span class="count">${group.items.length}</span>
          </div>`;
      return `<div class="mon-view-group ${collapsed ? 'collapsed' : ''}" data-group="${esc(groupKey)}">${head}<div class="mon-view-tiles">${tiles}</div></div>`;
    }).join('');
  }

  // ------------------------------------------------------------------
  // Grid interactions (same set_app_route contract as the ASSIGN column)
  // ------------------------------------------------------------------

  gridHost.addEventListener('click', (event) => {
    const head = event.target.closest('.mon-view-group-head');
    if (!head) return;
    const groupKey = head.dataset.group;
    const groupEl = head.closest('.mon-view-group');
    if (groupEl) {
      const collapsed = groupEl.classList.toggle('collapsed');
      head.setAttribute('aria-expanded', String(!collapsed));
      if (collapsed) collapsedGroups.add(groupKey);
      else collapsedGroups.delete(groupKey);
    }
  });

  gridHost.addEventListener('change', (event) => {
    const select = event.target.closest('select[data-route-process]');
    if (!select) return;
    const route = select.value;
    if (!route) return;
    postToHost({
      action: 'set_app_route',
      processName: select.dataset.routeProcess,
      displayName: select.dataset.routeDisplay || select.dataset.routeProcess,
      route
    });
  });

  function postToHost(message) {
    try {
      if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
        window.chrome.webview.postMessage(JSON.stringify(message));
      }
    } catch (e) { /* host channel unavailable (plain browser) */ }
  }

  // ------------------------------------------------------------------
  // Live hooks: re-render when the snapshot / icons / language change
  // ------------------------------------------------------------------

  const origUpdate = window.updateMonitorSnapshot;
  if (typeof origUpdate === 'function') {
    window.updateMonitorSnapshot = function (data) {
      origUpdate(data);
      if (data && typeof data === 'object') {
        snapshot = {
          connections: Array.isArray(data.connections) ? data.connections : [],
          apps: Array.isArray(data.apps) ? data.apps : [],
          mode: data.mode || snapshot.mode,
          invertManualRouting: data.invertManualRouting === true
        };
      }
      if (viewMode !== 'details') renderGrid();
    };
  }

  const origSetIcons = window.setAppIcons;
  if (typeof origSetIcons === 'function') {
    window.setAppIcons = function (payload) {
      origSetIcons(payload);
      if (!payload || typeof payload !== 'object' || !payload.icons) return;
      let changed = false;
      Object.keys(payload.icons).forEach(path => {
        const key = String(path).trim().toLowerCase();
        const uri = payload.icons[path];
        if (key && typeof uri === 'string' && uri.startsWith('data:image/') && iconCache[key] !== uri) {
          iconCache[key] = uri;
          changed = true;
        }
      });
      if (changed && viewMode !== 'details') renderGrid();
    };
  }

  const origApplyLanguage = window.applyLanguage;
  if (typeof origApplyLanguage === 'function') {
    window.applyLanguage = function (lang) {
      origApplyLanguage(lang);
      syncToolbar();
      if (viewMode !== 'details') renderGrid();
    };
  }

  filterInput.addEventListener('input', () => {
    if (viewMode !== 'details') renderGrid();
  });
  if (hideListenersInput) {
    hideListenersInput.addEventListener('change', () => {
      if (viewMode !== 'details') renderGrid();
    });
  }

  // ------------------------------------------------------------------
  // Boot
  // ------------------------------------------------------------------

  // Module styles stay local to this file (injected once); shared look-and-feel
  // classes (.route-badge, .app-icon) come from the main stylesheet chain.
  const style = document.createElement('style');
  style.textContent = `
  .mon-view-toolbar { display: flex; align-items: center; gap: 4px; margin-left: auto; flex-shrink: 0; }
  .mon-view-toolbar-label { font-size: 9px; text-transform: uppercase; letter-spacing: .14em; color: #8A94A6; margin-right: 2px; }
  .mon-view-mode-btn { width: 26px; height: 26px; border-radius: 7px; display: inline-flex; align-items: center; justify-content: center;
    color: #8A94A6; background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.08); cursor: pointer; transition: all .15s; }
  .mon-view-mode-btn:hover { color: #E2E8F0; background: rgba(255,255,255,.08); }
  .mon-view-mode-btn.active { color: #fff; background: rgba(139,92,246,.16); border-color: rgba(139,92,246,.45); box-shadow: 0 0 10px rgba(139,92,246,.25); }
  .mon-view-divider { width: 1px; height: 16px; background: rgba(255,255,255,.1); margin: 0 6px; }
  .mon-view-toolbar select { background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.1); border-radius: 7px; padding: 4px 8px; font-size: 10px; color: #CBD5E1; outline: none; }
  .mon-view-toolbar select:focus { border-color: rgba(34,211,238,.5); }
  .mon-view-grid-host { padding-top: 2px; }
  .mon-view-group { margin-bottom: 12px; }
  .mon-view-group-head { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border-radius: 9px;
    background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.06); cursor: pointer; user-select: none; margin-bottom: 8px; }
  .mon-view-group-head:hover { background: rgba(255,255,255,.07); }
  .mon-view-group-head .chev { color: #64748B; font-size: 9px; transition: transform .15s; }
  .mon-view-group.collapsed .chev { transform: rotate(-90deg); }
  .mon-view-group-head b { font-size: 10px; font-weight: 700; letter-spacing: .08em; color: #E2E8F0; text-transform: uppercase; }
  .mon-view-group-head .count { margin-left: auto; font-size: 10px; color: #64748B; font-variant-numeric: tabular-nums; }
  .mon-view-group.collapsed .mon-view-tiles { display: none; }
  .mon-view-tiles { display: grid; gap: 10px; grid-template-columns: repeat(auto-fill, minmax(104px, 1fr)); }
  .mon-view-grid-host.large .mon-view-tiles { grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 14px; }
  .mon-view-grid-host.small .mon-view-tiles { grid-template-columns: repeat(auto-fill, minmax(92px, 1fr)); gap: 8px; }
  .mon-view-tile { display: flex; flex-direction: column; align-items: center; gap: 3px; padding: 10px 6px 8px; border-radius: 12px;
    background: rgba(255,255,255,.03); border: 1px solid rgba(255,255,255,.06); transition: all .15s; min-width: 0; }
  .mon-view-tile:hover { background: rgba(255,255,255,.06); border-color: rgba(255,255,255,.12); transform: translateY(-1px); }
  .mon-view-tile-icon { position: relative; }
  .mon-view-status { position: absolute; right: -3px; bottom: -3px; width: 10px; height: 10px; border-radius: 999px;
    border: 2px solid #0D111A; background: #5B6472; }
  .mon-view-status.on { background: #34D399; box-shadow: 0 0 8px rgba(52,211,153,.6); }
  .mon-view-status.warn { background: #FBBF24; box-shadow: 0 0 8px rgba(251,191,36,.5); }
  .mon-view-tile-name { max-width: 100%; font-size: 10.5px; font-weight: 600; color: #E2E8F0; text-align: center;
    line-height: 1.25; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .mon-view-tile-exe { max-width: 100%; font-size: 9px; color: #64748B; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .mon-view-tile-remote { max-width: 100%; font-size: 9px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    color: #CBD5E1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .mon-view-tile-route { margin-top: 2px; }
  .mon-view-tile-meta { display: flex; align-items: center; gap: 4px; }
  .mon-view-chip { font-size: 8.5px; font-weight: 600; letter-spacing: .04em; padding: 2px 6px; border-radius: 999px;
    color: #A7B0BF; background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.08); }
  .mon-view-tile-country { max-width: 100%; font-size: 8.5px; color: #64748B; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .mon-view-tile-actions { margin-top: 3px; }
  .mon-view-tile-actions select { width: 96px; background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.1);
    border-radius: 6px; padding: 3px 4px; font-size: 9.5px; color: #CBD5E1; outline: none; }
  .mon-view-tile-actions select:focus { border-color: rgba(34,211,238,.5); }
  .mon-view-noselect { font-size: 10px; color: #5B6472; }
  .mon-view-empty { text-align: center; font-size: 11px; color: #64748B; padding: 26px 0; }
  `;
  document.head.appendChild(style);

  buildToolbar();
  applyVisibility();
})();