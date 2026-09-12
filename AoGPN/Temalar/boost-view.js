/* ==========================================================================
   AoGPN dashboard — Game Boost view module (Windows-style view modes)
   --------------------------------------------------------------------------
   Standalone module loaded AFTER Temalar/app.js (see the <script> tag in
   vpn-gpn-dashboard.html). It adds the Windows-Explorer-style display
   options to the GPN Game Boost section WITHOUT touching the main app.js:

     • View modes — Details (the classic route table, rendered by app.js),
       Small / Medium / Large icons (grid tiles with the REAL executable
       icons the host resolves through window.setAppIcons).
     • Grouping ("kategorileme") — group the entries by Route, Status or
       Type with collapsible headers, exactly like Explorer's "Group by".

   The module talks to the dashboard only through the public window API it
   wraps: updateMonitorSnapshot (app list + route mode), setAppIcons (shell
   icons) and applyLanguage (re-label). The host channel uses the same
   postMessage contracts as the main table (set_app_route / remove_app), so
   the tiles behave identically to the rows. Preferences persist in
   localStorage.

   The module is fully defensive: missing anchors or a missing app.js bridge
   simply leave the page untouched.
   ========================================================================== */
(() => {
  'use strict';

  const filterInput = document.getElementById('boostAppFilter');
  const tbody = document.getElementById('splitAppsBody');
  if (!filterInput || !tbody) return;

  const LS_KEY = 'aogpn.boostView.v1';

  // ------------------------------------------------------------------
  // State
  // ------------------------------------------------------------------

  let snapshot = { apps: [], mode: 'off', invertManualRouting: false };
  const iconCache = {}; // lowercased exe path -> data URI (mirror of app.js)
  let viewMode = 'details'; // details | small | medium | large
  let groupBy = 'none'; // none | route | status | type
  const collapsedGroups = new Set();

  try {
    const saved = JSON.parse(localStorage.getItem(LS_KEY) || '{}');
    if (['details', 'small', 'medium', 'large'].includes(saved.mode)) viewMode = saved.mode;
    if (['none', 'route', 'status', 'type'].includes(saved.groupBy)) groupBy = saved.groupBy;
  } catch (e) { /* no localStorage (privacy mode / jsdom without url) */ }

  function persistPrefs() {
    try { localStorage.setItem(LS_KEY, JSON.stringify({ mode: viewMode, groupBy })); } catch (e) { /* ignore */ }
  }

  // ------------------------------------------------------------------
  // i18n: same dictionary pipeline as the main UI, with embedded English
  // fallbacks so the module never renders raw keys offline.
  // ------------------------------------------------------------------

  const L = (key, fallback, params) => {
    let value = null;
    if (typeof window.__aogpnT === 'function') value = window.__aogpnT(key, params);
    return (value !== null && value !== undefined && value !== '') ? value : fallback;
  };

  const ROUTE_FALLBACKS = { vpn: 'VPN', proxy: 'Proxy', 'vpn+proxy': 'VPN + Proxy', direct: 'Direct', block: 'Block', warp: 'WARP' };
  const routeLabel = (action) => L('route.' + action, ROUTE_FALLBACKS[action] || action);

  const esc = (v) => String(v == null ? '' : v)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

  function routeBadge(action, label) {
    const tag = ['proxy', 'direct', 'block', 'warp'].includes(action) ? action : 'unknown';
    return `<span class="route-badge ${tag}">${esc(label)}</span>`;
  }

  // Same effective-route rule as the main table: in blacklist mode a VPN
  // assignment keeps the app OUTSIDE the tunnel, a Direct assignment routes
  // it IN, Block stays Block.
  function effectiveRoute(action) {
    const blacklist = snapshot.invertManualRouting === true && snapshot.mode === 'manual';
    if (!blacklist) return action;
    return action === 'direct' ? 'vpn' : (action === 'block' ? 'block' : 'direct');
  }

  // ------------------------------------------------------------------
  // Toolbar (injected into the existing filter row, right-aligned)
  // ------------------------------------------------------------------

  let modeButtons = [];
  let groupSelect = null;
  let groupLabelEl = null;

  const VIEW_ICONS = {
    details: '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M4 5h16v2H4V5Zm0 6h16v2H4v-2Zm0 6h16v2H4v-2Z"/></svg>',
    small: '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M4 4h7v7H4V4Zm9 0h7v7h-7V4ZM4 13h7v7H4v-7Zm9 0h7v7h-7v-7Z"/></svg>',
    medium: '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M4 4h5v5H4V4Zm11 0h5v5h-5V4ZM4 15h5v5H4v-5Zm11 0h5v5h-5v-5Z"/></svg>',
    large: '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M3 3h8v8H3V3Zm10 0h8v8h-8V3ZM3 13h8v8H3v-8Zm10 0h8v8h-8v-8Z"/></svg>'
  };
  const MODE_LABEL_KEYS = {
    details: ['boost.view.details', 'Details'],
    small: ['boost.view.small', 'Small icons'],
    medium: ['boost.view.medium', 'Medium icons'],
    large: ['boost.view.large', 'Large icons']
  };

  function buildToolbar() {
    // The filter row is the flex container that also holds the search input
    // (the input itself sits inside a nested relative wrapper).
    const row = filterInput.closest('.flex.items-center')
      || filterInput.parentElement?.parentElement;
    if (!row) return;

    const toolbar = document.createElement('div');
    toolbar.className = 'boost-view-toolbar';

    modeButtons = [];
    ['details', 'small', 'medium', 'large'].forEach(mode => {
      const [key, fallback] = MODE_LABEL_KEYS[mode];
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'boost-view-mode-btn';
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
    divider.className = 'boost-view-divider';
    toolbar.appendChild(divider);

    groupLabelEl = document.createElement('span');
    groupLabelEl.className = 'boost-view-toolbar-label';
    toolbar.appendChild(groupLabelEl);

    groupSelect = document.createElement('select');
    groupSelect.setAttribute('aria-label', L('boost.view.groupBy', 'Group by'));
    groupSelect.innerHTML = [
      ['none', L('boost.view.groupNone', 'None')],
      ['route', L('boost.view.groupRoute', 'Route')],
      ['status', L('boost.view.groupStatus', 'Status')],
      ['type', L('boost.view.groupType', 'Type')]
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
    if (groupLabelEl) groupLabelEl.textContent = L('boost.view.groupBy', 'Group by');
  }

  // ------------------------------------------------------------------
  // Grid host (replaces the table in icon modes)
  // ------------------------------------------------------------------

  const tableWrap = tbody.closest('.overflow-x-auto');
  const gridHost = document.createElement('div');
  gridHost.id = 'boostGridView';
  gridHost.className = 'boost-view-grid-host hidden';
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
  // Grouping
  // ------------------------------------------------------------------

  const GROUP_ORDER = {
    route: ['vpn', 'vpn+proxy', 'proxy', 'warp', 'direct', 'block'],
    status: ['running', 'idle'],
    type: ['app', 'domain']
  };

  function groupOf(item) {
    switch (groupBy) {
      case 'route': {
        const eff = effectiveRoute(item.action);
        return { key: eff, label: routeLabel(eff) };
      }
      case 'status': {
        const running = item.isRunning === true;
        return {
          key: running ? 'running' : 'idle',
          label: running
            ? (item.runStatusText || L('boost.view.statusRunning', 'Running'))
            : L('boost.view.statusIdle', 'Idle')
        };
      }
      case 'type': {
        const isApp = item.entryType !== 'domain';
        return {
          key: isApp ? 'app' : 'domain',
          label: isApp ? L('boost.view.typeApps', 'Applications') : L('boost.view.typeDomain', 'Domain & IP rules')
        };
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

  function tileMarkup(item, size) {
    const processName = item.processName || item.value || '';
    const display = item.displayName || item.processName || item.value || 'Unknown';
    const iconCls = size === 'large' ? 'w-14 h-14' : size === 'medium' ? 'w-10 h-10' : 'w-8 h-8';
    // Rule rows (domain/IP) need their type + value so the delete control can
    // address them exactly (remove_route) — remove_app silently ignored them.
    const entryType = item.entryType || 'app';
    const entryValue = item.value || processName;
    const eff = effectiveRoute(item.action);
    const options = [
      ['', L('route.assign', 'Assign route…')],
      ['vpn', L('route.vpn', 'VPN')],
      ['direct', L('route.direct', 'Direct')],
      ['block', L('route.block', 'Block')],
      ['warp', L('route.warp', 'WARP')]
    ].map(([value, label]) =>
      `<option value="${value}"${String(value) === String(item.action) ? ' selected' : ''}>${esc(label)}</option>`
    ).join('');
    return `<div class="boost-view-tile" data-process-name="${esc(processName)}">
      <div class="boost-view-tile-icon">${iconMarkup(item.exePath, display, iconCls)}<span class="boost-view-status ${item.isRunning === true ? 'on' : ''}"></span></div>
      <p class="boost-view-tile-name" title="${esc(display)}">${esc(display)}</p>
      ${size !== 'small' ? `<p class="boost-view-tile-exe" title="${esc(processName)}">${esc(processName)}</p>` : ''}
      <div class="boost-view-tile-route">${routeBadge(eff, routeLabel(eff))}</div>
      <div class="boost-view-tile-actions">
        <select data-route-process="${esc(processName)}" data-route-display="${esc(display)}" title="${esc(L('route.assign', 'Assign route…'))}">${options}</select>
        <button type="button" class="boost-view-tile-del" data-remove-process="${esc(processName)}" data-remove-entry-type="${esc(entryType)}" data-remove-entry-value="${esc(entryValue)}" title="${esc(L('boost.view.del', 'Remove') + ' ' + display)}" aria-label="${esc(L('boost.view.del', 'Remove'))} ${esc(display)}">×</button>
      </div>
    </div>`;
  }

  function renderGrid() {
    if (!gridHost.parentNode) return;
    const apps = Array.isArray(snapshot.apps) ? snapshot.apps : [];
    const filter = (filterInput.value || '').trim().toLowerCase();
    const items = apps.filter(item => {
      if (!filter) return true;
      const name = String(item.displayName || item.processName || item.value || '').toLowerCase();
      const exe = String(item.processName || item.value || '').toLowerCase();
      const route = routeLabel(item.action).toLowerCase();
      return name.includes(filter) || exe.includes(filter) || route.includes(filter);
    });

    if (items.length === 0) {
      gridHost.innerHTML = `<p class="boost-view-empty">${esc(filter ? L('boost.view.noMatch', 'No applications match the filter.') : L('boost.view.empty', 'No applications assigned yet.'))}</p>`;
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
        : `<div class="boost-view-group-head" data-group="${esc(groupKey)}" role="button" tabindex="0" aria-expanded="${String(!collapsed)}">
            <span class="chev">▾</span><b>${esc(group.label)}</b><span class="count">${group.items.length}</span>
          </div>`;
      return `<div class="boost-view-group ${collapsed ? 'collapsed' : ''}" data-group="${esc(groupKey)}">${head}<div class="boost-view-tiles">${tiles}</div></div>`;
    }).join('');
  }

  // ------------------------------------------------------------------
  // Grid interactions (delegated, same host contracts as the main table)
  // ------------------------------------------------------------------

  gridHost.addEventListener('click', (event) => {
    const head = event.target.closest('.boost-view-group-head');
    if (head) {
      const groupKey = head.dataset.group;
      const groupEl = head.closest('.boost-view-group');
      if (groupEl) {
        const collapsed = groupEl.classList.toggle('collapsed');
        head.setAttribute('aria-expanded', String(!collapsed));
        if (collapsed) collapsedGroups.add(groupKey);
        else collapsedGroups.delete(groupKey);
      }
      return;
    }
    const del = event.target.closest('button[data-remove-process]');
    if (del) {
      const processName = del.dataset.removeProcess;
      if (!processName) return;
      const entryType = del.dataset.removeEntryType || 'app';
      const entryValue = del.dataset.removeEntryValue || processName;
      if (!window.confirm(`Remove "${processName}" from the routing list?`)) return;
      if (entryType === 'app') {
        postToHost({ action: 'remove_app', processName });
      } else {
        postToHost({ action: 'remove_route', entryType, value: entryValue });
      }
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

  // ------------------------------------------------------------------
  // Boot
  // ------------------------------------------------------------------

  // Module styles stay local to this file (injected once) so the shared
  // stylesheet chain and its embedded copy are untouched.
  const style = document.createElement('style');
  style.textContent = `
  .boost-view-toolbar { display: flex; align-items: center; gap: 4px; margin-left: auto; flex-shrink: 0; }
  .boost-view-toolbar-label { font-size: 9px; text-transform: uppercase; letter-spacing: .14em; color: #8A94A6; margin-right: 2px; }
  .boost-view-mode-btn { width: 26px; height: 26px; border-radius: 7px; display: inline-flex; align-items: center; justify-content: center;
    color: #8A94A6; background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.08); cursor: pointer; transition: all .15s; }
  .boost-view-mode-btn:hover { color: #E2E8F0; background: rgba(255,255,255,.08); }
  .boost-view-mode-btn.active { color: #fff; background: rgba(139,92,246,.16); border-color: rgba(139,92,246,.45); box-shadow: 0 0 10px rgba(139,92,246,.25); }
  .boost-view-divider { width: 1px; height: 16px; background: rgba(255,255,255,.1); margin: 0 6px; }
  .boost-view-groupby { display: flex; align-items: center; gap: 6px; }
  .boost-view-groupby select { background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.1); border-radius: 7px; padding: 4px 8px; font-size: 10px; color: #CBD5E1; outline: none; }
  .boost-view-groupby select:focus { border-color: rgba(34,211,238,.5); }
  .boost-view-grid-host { padding-top: 2px; }
  .boost-view-group { margin-bottom: 12px; }
  .boost-view-group-head { display: flex; align-items: center; gap: 8px; padding: 6px 10px; border-radius: 9px;
    background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.06); cursor: pointer; user-select: none; margin-bottom: 8px; }
  .boost-view-group-head:hover { background: rgba(255,255,255,.07); }
  .boost-view-group-head .chev { color: #64748B; font-size: 9px; transition: transform .15s; }
  .boost-view-group.collapsed .chev { transform: rotate(-90deg); }
  .boost-view-group-head b { font-size: 10px; font-weight: 700; letter-spacing: .08em; color: #E2E8F0; text-transform: uppercase; }
  .boost-view-group-head .count { margin-left: auto; font-size: 10px; color: #64748B; font-variant-numeric: tabular-nums; }
  .boost-view-group.collapsed .boost-view-tiles { display: none; }
  .boost-view-tiles { display: grid; gap: 10px; grid-template-columns: repeat(auto-fill, minmax(96px, 1fr)); }
  .boost-view-grid-host.large .boost-view-tiles { grid-template-columns: repeat(auto-fill, minmax(132px, 1fr)); gap: 14px; }
  .boost-view-grid-host.small .boost-view-tiles { grid-template-columns: repeat(auto-fill, minmax(84px, 1fr)); gap: 8px; }
  .boost-view-tile { display: flex; flex-direction: column; align-items: center; gap: 4px; padding: 10px 6px 8px; border-radius: 12px;
    background: rgba(255,255,255,.03); border: 1px solid rgba(255,255,255,.06); transition: all .15s; min-width: 0; }
  .boost-view-tile:hover { background: rgba(255,255,255,.06); border-color: rgba(255,255,255,.12); transform: translateY(-1px); }
  .boost-view-tile-icon { position: relative; }
  .boost-view-status { position: absolute; right: -3px; bottom: -3px; width: 10px; height: 10px; border-radius: 999px;
    border: 2px solid #0D111A; background: #5B6472; }
  .boost-view-status.on { background: #34D399; box-shadow: 0 0 8px rgba(52,211,153,.6); }
  .boost-view-tile-name { max-width: 100%; font-size: 10.5px; font-weight: 600; color: #E2E8F0; text-align: center;
    line-height: 1.25; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .boost-view-tile-exe { max-width: 100%; font-size: 9px; color: #64748B; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .boost-view-tile-route { margin-top: 2px; }
  .boost-view-tile-actions { display: flex; align-items: center; gap: 4px; margin-top: 2px; }
  .boost-view-tile-actions select { width: 86px; background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.1);
    border-radius: 6px; padding: 3px 4px; font-size: 9.5px; color: #CBD5E1; outline: none; }
  .boost-view-tile-actions select:focus { border-color: rgba(34,211,238,.5); }
  .boost-view-tile-del { width: 20px; height: 20px; border-radius: 6px; display: inline-flex; align-items: center; justify-content: center;
    color: #F87171; background: rgba(239,68,68,.08); border: 1px solid rgba(239,68,68,.15); font-size: 12px; line-height: 1; cursor: pointer; }
  .boost-view-tile-del:hover { background: rgba(239,68,68,.18); color: #FCA5A5; }
  .boost-view-empty { text-align: center; font-size: 11px; color: #64748B; padding: 26px 0; }
  `;
  document.head.appendChild(style);

  buildToolbar();
  applyVisibility();
})();