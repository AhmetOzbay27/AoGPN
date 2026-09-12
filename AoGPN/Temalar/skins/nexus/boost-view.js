/* ==========================================================================
   NEXUS GPN skin — Game Boost view module (Windows-style view modes)
   --------------------------------------------------------------------------
   Standalone module loaded AFTER skins/nexus/skin.js (see the <script> tag
   in skins/nexus/skin.html). Adds the same Explorer-style display options to
   the NEXUS Game Profiles (Game Boost) view as the main dashboard's
   boost-view.js, without touching the skin script:

     • View modes — Details (the route table the skin renders), Small /
       Medium / Large icons (grid tiles with the REAL executable icons the
       host resolves and shares through skinBridge.appIcons).
     • Grouping ("kategorileme") — by Route, Status or Type with collapsible
       headers.

   The module reads live state exclusively through the parent's skinBridge
   (B.monitorSnapshot.apps, B.appIcons, B.postToHost), mirrors the filter
   input the skin already renders, drives the same set_app_route / remove_app
   host contracts as the table, and keeps its choices in localStorage. The
   skin re-renders its whole view on every host push and on its own 2 s poll,
   so this module re-syncs idempotently on the same triggers instead of
   patching the skin's internals.

   The skin's Game Profiles table markup carries no styles in skin.css, so
   this module injects the complete styling for its own toolbar and tiles
   (nxv-* classes); the shared .nx-* look-and-feel stays untouched.
   ========================================================================== */
(() => {
  'use strict';

  const B = (window.parent && window.parent !== window && window.parent.skinBridge) ? window.parent.skinBridge : null;
  // No bridge (standalone preview / thumbnail): the module stays inert.
  if (!B) return;

  const LS_KEY = 'aogpn.nexusBoostView.v1';

  // ------------------------------------------------------------------
  // State
  // ------------------------------------------------------------------

  let viewMode = 'details'; // details | small | medium | large
  let groupBy = 'none'; // none | route | status | type
  const collapsedGroups = new Set();
  let lastTable = null; // last .nx-tbl element the grid host was attached after
  let boundFilterInput = null; // input the live-filter listener is attached to

  try {
    const saved = JSON.parse(localStorage.getItem(LS_KEY) || '{}');
    if (['details', 'small', 'medium', 'large'].includes(saved.mode)) viewMode = saved.mode;
    if (['none', 'route', 'status', 'type'].includes(saved.groupBy)) groupBy = saved.groupBy;
  } catch (e) { /* no localStorage */ }

  function persistPrefs() {
    try { localStorage.setItem(LS_KEY, JSON.stringify({ mode: viewMode, groupBy })); } catch (e) { /* ignore */ }
  }

  // ------------------------------------------------------------------
  // i18n + shared helpers (bridge t() = Dil/*.json via the main dashboard)
  // ------------------------------------------------------------------

  const T = (key, fallback, params) => {
    let value = null;
    try { value = B.t ? B.t(key, params) : null; } catch (e) { value = null; }
    return (value !== null && value !== undefined && value !== '' && value !== key) ? value : fallback;
  };

  const esc = (v) => String(v == null ? '' : v)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

  const ROUTE_FALLBACKS = { vpn: 'VPN', proxy: 'Proxy', 'vpn+proxy': 'VPN + Proxy', direct: 'Direct', block: 'Block', warp: 'WARP' };
  const routeLabel = (action) => T('route.' + action, ROUTE_FALLBACKS[action] || action);

  function routeBadge(action, label) {
    // Same visual mapping as the main dashboard's boost-view module: the VPN
    // route reuses the proxy (cyan) tag, everything else keeps its own color.
    const tag = action === 'vpn' ? 'proxy' : (['proxy', 'direct', 'block', 'warp'].includes(action) ? action : 'unknown');
    return `<span class="nxv-badge ${tag}">${esc(label)}</span>`;
  }

  function snapshot() {
    try { return B.monitorSnapshot || { apps: [] }; } catch (e) { return { apps: [] }; }
  }

  function boostApps() {
    const apps = snapshot().apps;
    return Array.isArray(apps) ? apps : [];
  }

  // Same effective-route rule as the skin's table (blacklist inversion).
  function effectiveRoute(action) {
    const S = snapshot();
    const blacklist = S.invertManualRouting === true && (B.mode === 'gpn' || B.splitMode === 'manual');
    if (!blacklist) return action;
    return action === 'direct' ? 'vpn' : (action === 'block' ? 'block' : 'direct');
  }

  function iconUri(exePath) {
    if (!exePath) return '';
    try {
      const icons = B.appIcons;
      if (!icons) return '';
      return icons[String(exePath).trim().toLowerCase()] || '';
    } catch (e) { return ''; }
  }

  function iconMarkup(exePath, fallbackText) {
    const uri = iconUri(exePath);
    const label = String(fallbackText || 'APP').slice(0, 2).toUpperCase() || '?';
    if (uri) return `<img src="${uri}" alt="" class="nxv-ico" draggable="false" />`;
    return `<span class="nxv-ico">${esc(label)}</span>`;
  }

  // ------------------------------------------------------------------
  // Toolbar (injected into the skin's filter row, idempotent per render)
  // ------------------------------------------------------------------

  let modeButtons = [];
  let groupSelect = null;

  const VIEW_ICONS = {
    details: '<svg class="nxv-svg" fill="currentColor" viewBox="0 0 24 24"><path d="M4 5h16v2H4V5Zm0 6h16v2H4v-2Zm0 6h16v2H4v-2Z"/></svg>',
    small: '<svg class="nxv-svg" fill="currentColor" viewBox="0 0 24 24"><path d="M4 4h7v7H4V4Zm9 0h7v7h-7V4ZM4 13h7v7H4v-7Zm9 0h7v7h-7v-7Z"/></svg>',
    medium: '<svg class="nxv-svg" fill="currentColor" viewBox="0 0 24 24"><path d="M4 4h5v5H4V4Zm11 0h5v5h-5V4ZM4 15h5v5H4v-5Zm11 0h5v5h-5v-5Z"/></svg>',
    large: '<svg class="nxv-svg" fill="currentColor" viewBox="0 0 24 24"><path d="M3 3h8v8H3V3Zm10 0h8v8h-8V3ZM3 13h8v8H3v-8Zm10 0h8v8h-8v-8Z"/></svg>'
  };
  const MODE_LABELS = {
    details: ['boost.view.details', 'Details'],
    small: ['boost.view.small', 'Small icons'],
    medium: ['boost.view.medium', 'Medium icons'],
    large: ['boost.view.large', 'Large icons']
  };

  function ensureToolbar(filterRow) {
    if (!filterRow || filterRow.querySelector('.nxv-toolbar')) return;
    const toolbar = document.createElement('div');
    toolbar.className = 'nxv-toolbar';

    modeButtons = [];
    ['details', 'small', 'medium', 'large'].forEach(mode => {
      const [key, fallback] = MODE_LABELS[mode];
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'nxv-modeBtn';
      btn.dataset.viewMode = mode;
      btn.title = T(key, fallback);
      btn.setAttribute('aria-label', T(key, fallback));
      btn.setAttribute('aria-pressed', String(mode === viewMode));
      btn.innerHTML = VIEW_ICONS[mode];
      btn.addEventListener('click', () => setMode(mode));
      toolbar.appendChild(btn);
      modeButtons.push(btn);
    });

    const divider = document.createElement('span');
    divider.className = 'nxv-divider';
    toolbar.appendChild(divider);

    const groupLabel = document.createElement('span');
    groupLabel.className = 'nxv-label';
    groupLabel.textContent = T('boost.view.groupBy', 'Group by');
    toolbar.appendChild(groupLabel);

    groupSelect = document.createElement('select');
    groupSelect.setAttribute('aria-label', T('boost.view.groupBy', 'Group by'));
    groupSelect.innerHTML = [
      ['none', T('boost.view.groupNone', 'None')],
      ['route', T('boost.view.groupRoute', 'Route')],
      ['status', T('boost.view.groupStatus', 'Status')],
      ['type', T('boost.view.groupType', 'Type')]
    ].map(([value, label]) => `<option value="${value}"${value === groupBy ? ' selected' : ''}>${esc(label)}</option>`).join('');
    groupSelect.addEventListener('change', () => {
      groupBy = groupSelect.value;
      persistPrefs();
      renderGrid();
    });
    toolbar.appendChild(groupSelect);

    filterRow.appendChild(toolbar);
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
  // Grid host + visibility
  // ------------------------------------------------------------------

  let gridHost = null;

  function ensureGridHost(table) {
    if (!table || !table.parentNode) return;
    if (gridHost && gridHost.parentNode === table.parentNode) return;
    gridHost = document.createElement('div');
    gridHost.className = 'nxv-gridHost hidden';
    table.parentNode.insertBefore(gridHost, table.nextSibling);
  }

  function setMode(mode) {
    viewMode = mode;
    persistPrefs();
    syncToolbar();
    applyVisibility();
    if (viewMode !== 'details') renderGrid();
  }

  function applyVisibility() {
    const root = document.getElementById('nxGameProfiles');
    const table = root && root.querySelector('.nx-tbl');
    if (!table || !gridHost) return;
    const gridMode = viewMode !== 'details';
    table.classList.toggle('nxv-hidden', gridMode);
    gridHost.classList.toggle('hidden', !gridMode);
    gridHost.classList.toggle('large', viewMode === 'large');
    gridHost.classList.toggle('medium', viewMode === 'medium');
    gridHost.classList.toggle('small', viewMode === 'small');
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
          label: running ? T('boost.view.statusRunning', 'Running') : T('boost.view.statusIdle', 'Idle')
        };
      }
      case 'type': {
        const isApp = item.entryType !== 'domain';
        return {
          key: isApp ? 'app' : 'domain',
          label: isApp ? T('boost.view.typeApps', 'Applications') : T('boost.view.typeDomain', 'Domain & IP rules')
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

  function tileMarkup(item, size) {
    const processName = item.processName || item.value || '';
    const display = item.displayName || item.processName || item.value || 'Unknown';
    const iconCls = size === 'large' ? 'lg' : size === 'medium' ? 'md' : 'sm';
    const eff = effectiveRoute(item.action);
    const options = [
      ['', T('route.assign', 'Assign route…')],
      ['vpn', T('route.vpn', 'VPN')],
      ['direct', T('route.direct', 'Direct')],
      ['block', T('route.block', 'Block')],
      ['warp', T('route.warp', 'WARP')]
    ].map(([value, label]) =>
      `<option value="${value}"${String(value) === String(item.action) ? ' selected' : ''}>${esc(label)}</option>`
    ).join('');
    return `<div class="nxv-tile" data-process-name="${esc(processName)}">
      <div class="nxv-tileIcon ${iconCls}">${iconMarkup(item.exePath, display)}<span class="nxv-status ${item.isRunning === true ? 'on' : ''}"></span></div>
      <p class="nxv-tileName" title="${esc(display)}">${esc(display)}</p>
      ${size !== 'small' ? `<p class="nxv-tileExe" title="${esc(processName)}">${esc(processName)}</p>` : ''}
      <div class="nxv-tileRoute">${routeBadge(eff, routeLabel(eff))}</div>
      <div class="nxv-tileActions">
        <select data-route-process="${esc(processName)}" data-route-display="${esc(display)}" title="${esc(T('route.assign', 'Assign route…'))}">${options}</select>
        <button type="button" class="nxv-tileDel" data-remove-process="${esc(processName)}" data-remove-entry-type="${esc(item.entryType || 'app')}" data-remove-entry-value="${esc(item.value || processName)}" title="${esc(T('boost.view.del', 'Remove'))}" aria-label="${esc(T('boost.view.del', 'Remove'))} ${esc(display)}">✕</button>
      </div>
    </div>`;
  }

  function currentFilter() {
    const root = document.getElementById('nxGameProfiles');
    const input = root && root.querySelector('[data-nx-appfilter]');
    return input ? (input.value || '').trim().toLowerCase() : '';
  }

  function renderGrid() {
    if (!gridHost || !gridHost.parentNode) return;
    const filter = currentFilter();
    const items = boostApps().filter(item => {
      if (!filter) return true;
      const name = String(item.displayName || item.processName || item.value || '').toLowerCase();
      const exe = String(item.processName || item.value || '').toLowerCase();
      const route = routeLabel(item.action).toLowerCase();
      return name.includes(filter) || exe.includes(filter) || route.includes(filter);
    });

    if (items.length === 0) {
      gridHost.innerHTML = `<p class="nxv-empty">${esc(T('boost.view.empty', 'No applications assigned yet.'))}</p>`;
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
        : `<div class="nxv-groupHead" data-group="${esc(groupKey)}" role="button" tabindex="0" aria-expanded="${String(!collapsed)}">
            <span class="chev">▾</span><b>${esc(group.label)}</b><span class="count">${group.items.length}</span>
          </div>`;
      return `<div class="nxv-group ${collapsed ? 'collapsed' : ''}" data-group="${esc(groupKey)}">${head}<div class="nxv-tiles">${tiles}</div></div>`;
    }).join('');
  }

  // ------------------------------------------------------------------
  // Grid interactions (same host contracts as the skin's table)
  // ------------------------------------------------------------------

  function wireGridEvents() {
    if (!gridHost) return;
    if (gridHost.dataset.nxvWired) return;
    gridHost.dataset.nxvWired = '1';

    gridHost.addEventListener('click', (event) => {
      const head = event.target.closest('.nxv-groupHead');
      if (head) {
        const groupKey = head.dataset.group;
        const groupEl = head.closest('.nxv-group');
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
        // Real browsers: cancel returns false and aborts. jsdom's confirm
        // returns undefined (not-implemented), so only an explicit false
        // cancels — keeps the sandbox tests honest about the contract.
        if (typeof confirm === 'function') {
          try { if (confirm(`Remove "${processName}" from the routing list?`) === false) return; }
          catch (e) { /* no confirm available */ }
        }
        const entryType = del.dataset.removeEntryType || 'app';
        if (entryType === 'app') {
          B.postToHost({ action: 'remove_app', processName });
        } else {
          B.postToHost({ action: 'remove_route', entryType, value: del.dataset.removeEntryValue || processName });
        }
      }
    });

    gridHost.addEventListener('change', (event) => {
      const select = event.target.closest('select[data-route-process]');
      if (!select) return;
      const route = select.value;
      if (!route) return;
      B.postToHost({
        action: 'set_app_route',
        processName: select.dataset.routeProcess,
        displayName: select.dataset.routeDisplay || select.dataset.routeProcess,
        route
      });
    });
  }

  // ------------------------------------------------------------------
  // Live sync — the skin re-renders its view on every host push and on its
  // own 2 s poll, replacing our toolbar/grid with fresh DOM. Re-sync is
  // idempotent: re-inject missing pieces and re-render the grid only when
  // the table was actually replaced or the prefs changed.
  // ------------------------------------------------------------------

  let gridDirty = true;

  function sync() {
    const root = document.getElementById('nxGameProfiles');
    if (!root) return;

    const filterRow = root.querySelector('.nx-filterRow');
    ensureToolbar(filterRow);

    // The skin's filter input is recreated on every render; keep exactly one
    // live listener bound so typing filters the grid immediately.
    const filterInput = filterRow && filterRow.querySelector('[data-nx-appfilter]');
    if (filterInput && filterInput !== boundFilterInput) {
      if (boundFilterInput) boundFilterInput.removeEventListener('input', onFilterInput);
      filterInput.addEventListener('input', onFilterInput);
      boundFilterInput = filterInput;
    }

    const table = root.querySelector('.nx-tbl');
    if (table !== lastTable) {
      lastTable = table;
      gridDirty = true;
    }
    if (!table) return;

    ensureGridHost(table);
    wireGridEvents();
    applyVisibility();
    if (viewMode !== 'details' && gridDirty) {
      gridDirty = false;
      renderGrid();
    }
  }

  function onFilterInput() {
    if (viewMode !== 'details') renderGrid();
  }

  // Mode/group changes always re-render immediately.
  // (setMode already calls renderGrid; group select too.)

  // ------------------------------------------------------------------
  // Boot
  // ------------------------------------------------------------------

  // Module styles stay local to this file (injected once). The games table
  // itself is unstyled in skin.css, so the module styles its own surface
  // completely — the shared .nx-* look-and-feel is untouched.
  const style = document.createElement('style');
  style.textContent = `
  .nxv-hidden { display: none !important; }
  .nxv-toolbar { display: flex; align-items: center; gap: 4px; margin-left: auto; }
  .nxv-label { font: 600 9px Orbitron, sans-serif; letter-spacing: .14em; color: rgba(138,148,166,.9); margin-right: 2px; text-transform: uppercase; }
  .nxv-modeBtn { width: 26px; height: 26px; border-radius: 7px; display: inline-flex; align-items: center; justify-content: center;
    color: rgba(148,163,184,.85); background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.09); cursor: pointer; transition: all .15s; }
  .nxv-modeBtn:hover { color: #E2E8F0; background: rgba(255,255,255,.09); }
  .nxv-modeBtn.active { color: #fff; background: rgba(32,232,255,.14); border-color: rgba(32,232,255,.5); box-shadow: 0 0 10px rgba(32,232,255,.22); }
  .nxv-svg { width: 14px; height: 14px; }
  .nxv-divider { width: 1px; height: 16px; background: rgba(255,255,255,.1); margin: 0 6px; }
  .nxv-toolbar select { background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.1); border-radius: 7px; padding: 4px 8px; font-size: 10px; color: #CBD5E1; outline: none; font-family: Inter, sans-serif; }
  .nxv-toolbar select:focus { border-color: rgba(32,232,255,.55); }
  .nxv-gridHost { padding-top: 4px; }
  .nxv-group { margin-bottom: 14px; }
  .nxv-groupHead { display: flex; align-items: center; gap: 8px; padding: 7px 12px; border-radius: 10px; cursor: pointer; user-select: none;
    margin-bottom: 10px; background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.07); }
  .nxv-groupHead:hover { background: rgba(255,255,255,.07); }
  .nxv-groupHead .chev { color: rgba(100,116,139,.9); font-size: 9px; transition: transform .15s; }
  .nxv-group.collapsed .chev { transform: rotate(-90deg); }
  .nxv-groupHead b { font: 700 10px Orbitron, sans-serif; letter-spacing: .1em; color: #E2E8F0; text-transform: uppercase; }
  .nxv-groupHead .count { margin-left: auto; font-size: 10px; color: rgba(100,116,139,.9); font-variant-numeric: tabular-nums; }
  .nxv-group.collapsed .nxv-tiles { display: none; }
  .nxv-tiles { display: grid; gap: 10px; grid-template-columns: repeat(auto-fill, minmax(104px, 1fr)); }
  .nxv-gridHost.large .nxv-tiles { grid-template-columns: repeat(auto-fill, minmax(136px, 1fr)); gap: 14px; }
  .nxv-gridHost.small .nxv-tiles { grid-template-columns: repeat(auto-fill, minmax(88px, 1fr)); gap: 8px; }
  .nxv-tile { display: flex; flex-direction: column; align-items: center; gap: 4px; padding: 12px 6px 9px; border-radius: 13px;
    background: rgba(255,255,255,.03); border: 1px solid rgba(255,255,255,.07); transition: all .15s; min-width: 0; }
  .nxv-tile:hover { background: rgba(255,255,255,.06); border-color: rgba(32,232,255,.25); transform: translateY(-1px); }
  .nxv-tileIcon { position: relative; }
  .nxv-ico { width: 40px; height: 40px; border-radius: 10px; display: inline-flex; align-items: center; justify-content: center;
    overflow: hidden; font: 700 11px Orbitron, sans-serif; color: var(--nx-cyan, #20e8ff);
    background: linear-gradient(135deg, #161e2c, #0a0e15); border: 1px solid var(--nx-line, rgba(255,255,255,.08)); object-fit: contain; }
  .nxv-tileIcon.lg .nxv-ico { width: 56px; height: 56px; border-radius: 13px; font-size: 14px; }
  .nxv-tileIcon.md .nxv-ico { width: 40px; height: 40px; }
  .nxv-tileIcon.sm .nxv-ico { width: 32px; height: 32px; border-radius: 8px; font-size: 9px; }
  .nxv-status { position: absolute; right: -3px; bottom: -3px; width: 11px; height: 11px; border-radius: 999px;
    border: 2px solid #0a0e15; background: rgba(100,116,139,.8); }
  .nxv-status.on { background: #34D399; box-shadow: 0 0 8px rgba(52,211,153,.6); }
  .nxv-tileName { max-width: 100%; font-size: 10.5px; font-weight: 600; color: #E2E8F0; text-align: center;
    line-height: 1.25; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .nxv-tileExe { max-width: 100%; font-size: 9px; color: rgba(100,116,139,.9); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .nxv-tileRoute { margin-top: 2px; }
  .nxv-badge { display: inline-flex; align-items: center; border-radius: 999px; padding: 3px 9px; font-size: 9px; font-weight: 700; border: 1px solid transparent; white-space: nowrap; }
  .nxv-badge.proxy { color: #A5F3FC; background: rgba(32,232,255,.12); border-color: rgba(32,232,255,.28); }
  .nxv-badge.direct { color: #A7F3D0; background: rgba(52,211,153,.12); border-color: rgba(52,211,153,.28); }
  .nxv-badge.block { color: #FCA5A5; background: rgba(239,68,68,.14); border-color: rgba(239,68,68,.3); }
  .nxv-badge.warp { color: #DDD6FE; background: rgba(139,92,246,.14); border-color: rgba(139,92,246,.32); }
  .nxv-badge.unknown { color: #CBD5E1; background: rgba(148,163,184,.1); border-color: rgba(148,163,184,.22); }
  .nxv-tileActions { display: flex; align-items: center; gap: 4px; margin-top: 3px; }
  .nxv-tileActions select { width: 88px; background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.1);
    border-radius: 6px; padding: 3px 4px; font-size: 9.5px; color: #CBD5E1; outline: none; font-family: Inter, sans-serif; }
  .nxv-tileActions select:focus { border-color: rgba(32,232,255,.55); }
  .nxv-tileDel { width: 21px; height: 21px; border-radius: 6px; display: inline-flex; align-items: center; justify-content: center;
    color: #F87171; background: rgba(239,68,68,.1); border: 1px solid rgba(239,68,68,.18); font-size: 11px; line-height: 1; cursor: pointer; }
  .nxv-tileDel:hover { background: rgba(239,68,68,.2); color: #FCA5A5; }
  .nxv-empty { text-align: center; font-size: 11px; color: rgba(138,148,166,.9); padding: 28px 0; }
  `;
  document.head.appendChild(style);

  if (typeof B.subscribe === 'function') B.subscribe(sync);
  sync();
  setInterval(sync, 2000);
})();