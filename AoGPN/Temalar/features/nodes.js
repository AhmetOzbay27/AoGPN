/* ==========================================================================
   features/nodes.js — AoGPN dashboard feature module (VPN Rotaları)
   --------------------------------------------------------------------------
   Düğüm durumu ve çizimi: hostNode/NODES/realNodes, seçim + çoklu seçim,
   sürükle-sırala, ping testi, düğüm havuzu (URL abonelikleri), bağlam menüsü
   ve toplu işlemler (kopyala/yapıştır/dedup/cleanup). Host köprüsü
   window.updateNodeInfo / updateNodeList* / updateDisabledNodes / updateNodePool
   / updateNodeTest / setNodeTestRunning / setNodeSwitchResult sözleşmesi bu
   modülde yaşar. app.js ve skinBridge aogpn.nodes erişimcileriyle bağlanır.
   ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const applyTexts = aogpn.i18n.applyTexts;
  const escHtml = aogpn.util.escHtml;
  const $ = aogpn.util.$;
  const postToHost = aogpn.bridge.postToHost;
  const refreshAllCustomSelects = aogpn.selects.refreshAllCustomSelects;
  const { nodeName, nodeAddr } = aogpn.dom;

  let hostNode = null;

  // ---------- VPN Nodes (standalone preview fallback) ----------
  // These are only shown when no AoGPN host is connected (browser preview).
  // When the WebView2 host is active, real nodes are pushed via setNodeList.
  const NODES = [
    { key: 'istanbul',  code: 'TR', name: 'Istanbul · TR-01',   addr: '145.239.14.77',  ping: 18,  load: 34 },
    { key: 'frankfurt', code: 'DE', name: 'Frankfurt · DE-11', addr: '5.161.91.19',   ping: 148, load: 22 },
    { key: 'amsterdam', code: 'NL', name: 'Amsterdam · NL-02', addr: '51.15.118.35',  ping: 171, load: 41 },
    { key: 'london',    code: 'GB', name: 'London · UK-07',    addr: '51.36.10.188',  ping: 167, load: 29 },
    { key: 'newyork',   code: 'US', name: 'New York · US-03',  addr: '104.26.8.40',   ping: 201, load: 18 }
  ];
  let selectedNode = NODES[0] || null;

  // Real AoGPN profile list published by the host. The static NODES array above
  // remains the fallback for standalone browser previews without the WPF host.
  const realNodes = new Map();
  // Ids pushed by the host in the current round, so updateNodeListDone can drop
  // entries that no longer exist (e.g. deleted nodes) instead of leaving ghosts.
  const pushedNodeIds = new Set();
  // IndexId order of the current push round (the host streams the whole list in
  // Sort order); updateNodeListDone finalizes it into nodeOrder. Map insertion
  // order cannot serve as "host order": Map.set on an existing key keeps its old
  // position, so reordered nodes would never move without this explicit order.
  const pushedNodeOrder = [];
  let nodeOrder = [];
  let useRealNodes = false;
  let activeRealNodeId = null;
  // Server switch confirmation: the node being switched ("switching…" state), the
  // node that was active before the attempt (rollback target), and a safety timer
  // in case the host never acknowledges.
  let pendingSwitchId = null;
  let previousActiveRealNodeId = null;
  let pendingSwitchTimer = null;
  // Multi-select state for copy/delete, mirroring the native servers list. The
  // active node (what the tunnel uses) and the selection (what the user marked)
  // are independent; a plain click both selects and switches.
  const selectedIds = new Set();
  let selectionAnchorId = null;
  // Node ordering for the grid: 'default' (host order), 'country' (A-Z),
  // 'fav' (favorites first), 'recent' (last used first).
  let nodeSortMode = 'default';
  // Id of the node currently being dragged (drag-to-reorder in Default order).
  let dragNodeId = null;
  const NODE_GROUPS_STORAGE_KEY = 'aogpn.routeGroups';
  let collapsedNodeGroups = new Set(JSON.parse(localStorage.getItem(NODE_GROUPS_STORAGE_KEY) || '[]'));

  // Node pool: links (GitHub raw .txt / subscription endpoints) published by the
  // host; the user pulls shared nodes from them with one click.
  let nodePoolLinks = [];

  let pendingConfirmAction = null;
  // Edit-mode toggle: reveals the bulk-cleanup toolbar (dedupe, delete/disable
  // failed). Per-node add/edit/delete/disable stay available without it.
  let editMode = false;
  // Disabled section: nodes hidden from the main list that can be restored or
  // permanently deleted. Pushed by the host separately from the main list.
  const disabledNodes = new Map();
  let showingDisabled = false;
  // Real ping-test progress: indexId -> { testing: bool, fail: bool }.
  const nodeTestState = new Map();
  let nodeTestRunning = false;
  let nodeTestType = 'tcp';
  let nodeTestRunToken = 0;
  // Prevent repeated clicks and stale host callbacks from changing a newer run.
  const NODE_TEST_COOLDOWN_MS = 1200;
  let nodeTestRequestAt = 0;
  let nodeTestRunId = 0;
  let activeNodeTestRunId = 0;

  function canStartNodeTest() {
    const now = Date.now();
    if (nodeTestRunning || now - nodeTestRequestAt < NODE_TEST_COOLDOWN_MS) {
      return false;
    }
    nodeTestRequestAt = now;
    activeNodeTestRunId = ++nodeTestRunId;
    return true;
  }

  function markNodesTesting(ids) {
    ids.forEach(id => nodeTestState.set(id, { testing: true, runId: activeNodeTestRunId }));
    nodeTestRunning = true;
    updateNodesHeader();
    renderNodes();
  }

  function clearNodesTesting(failed = false) {
    [...nodeTestState.entries()].forEach(([id, state]) => {
      if (state?.testing) nodeTestState.set(id, { testing: false, fail: failed });
    });
  }


  function applyHostNode() {
    if (!hostNode) {
      return;
    }
    if (hostNode.name && nodeName) nodeName.textContent = hostNode.name;
    if ((hostNode.address || hostNode.protocol) && nodeAddr) {
      nodeAddr.textContent = [hostNode.address, hostNode.protocol].filter(Boolean).join(' · ');
    }
    refreshSessionNode();
  }

  function refreshSessionNode() {
    const el = document.getElementById('sessionNode');
    if (!el) {
      return;
    }
    const name = hostNode?.name || selectedNode?.name || '';
    const addr = hostNode?.address || selectedNode?.addr || '';
    const proto = hostNode?.protocol || (['istanbul', 'frankfurt'].includes(selectedNode?.key) ? 'VLESS' : 'VMess');
    const parts = [name, addr, proto].filter(Boolean);
    el.textContent = parts.length ? parts.join(' · ') : '—';
    el.title = parts.join(' · ') || '';
  }

  function applyNode() {
    if (!selectedNode) return;
    // In WebView2 the real profile overwrites the concept node once the host
    // publishes it; the static list stays as the standalone-preview fallback.
    if (!hostNode) {
      if (nodeName) nodeName.textContent = selectedNode.name;
      if (nodeAddr) nodeAddr.textContent = selectedNode.addr + ' · ' + (['istanbul', 'frankfurt'].includes(selectedNode.key) ? 'VLESS' : 'VMess');
    }
    $('selNodeFlag').innerHTML = aogpn.flags.tileMarkup(selectedNode.code, selectedNode.code);
    $('selNodeSummary').textContent = selectedNode.name;
    document.querySelectorAll('.node-card').forEach(c => c.classList.toggle('selected', c.dataset.node === selectedNode.key));
    refreshSessionNode();
    aogpn.events.emit('skin-changed'); // refresh standalone skins when the active node changes
  }

  function realNodeCard(n) {
    const sel = n.indexId === activeRealNodeId;
    const switching = n.indexId === pendingSwitchId;
    const checked = selectedIds.has(n.indexId);
    const code = (n.country || n.sub || n.protocol || 'VPN').slice(0, 2).toUpperCase();
    const addr = (n.address || '') + (n.port > 0 ? ':' + n.port : '');
    const test = nodeTestState.get(n.indexId);
    const testing = !!(test && test.testing);
    const failed = !!(test && test.fail && !testing);
    const delayText = testing ? 'Test ediliyor…' : failed ? '✗ başarısız' : n.delay > 0 ? n.delay + ' ms' : '—';
    const delayClass = testing ? 'bg-cyan-400/10 text-cyan-300 border-cyan-400/20 animate-pulse'
      : failed ? 'bg-red-500/10 text-red-300 border-red-400/25'
      : n.delay > 0 ? 'bg-emerald-500/10 text-emerald-300 border-emerald-400/20'
      : 'bg-cyan-400/10 text-cyan-300 border-cyan-400/20';
    const dotColor = switching ? '#22D3EE' : sel ? '#22D3EE' : '#3A4152';
    const dotClass = switching ? 'bg-cyan-400 animate-pulse' : sel ? 'bg-cyan-400' : 'bg-[#3A4152]';
    const footerText = switching ? 'Değiştiriliyor…' : sel ? 'Aktif' : 'Değiştirmek için dokunun';
    const footerClass = switching ? 'text-cyan-300' : sel ? 'text-cyan-300' : 'text-[#5B6472]';
    return `
      <div class="node-card relative glass-soft rounded-xl p-4 min-w-0 ${sel ? 'selected' : ''} ${checked ? 'ring-1 ring-cyan-400/60' : ''} ${switching ? 'opacity-90' : ''}" data-index="${n.indexId}">
        <button type="button" data-check="${n.indexId}" title="${checked ? 'Deselect node' : 'Select node'}" aria-pressed="${checked}" class="node-check absolute top-2.5 left-2.5 w-5 h-5 rounded-md flex items-center justify-center text-[11px] font-bold transition-all duration-200 cursor-pointer ${checked ? 'bg-cyan-400 text-[#0B0F19] shadow-[0_0_10px_rgba(34,211,238,.6)] scale-100' : 'bg-white/5 text-transparent border border-white/10 hover:border-cyan-400/50 hover:text-cyan-300/70 scale-[.3]'}">✓</button>
        <div class="flex items-center gap-3 min-w-0">
          ${aogpn.flags.tileMarkup(n.country, code)}
          <div class="min-w-0">
            <p class="text-sm font-semibold truncate">${escHtml(n.name)}</p>
            <p class="text-[11px] text-[#8A94A6] truncate">${escHtml(addr)}</p>
          </div>
          <div class="ml-auto flex items-center gap-1.5 shrink-0">
            <button type="button" data-fav="${n.indexId}" title="${n.fav ? 'Remove from favorites' : 'Add to favorites'}" class="node-fav ${n.fav ? 'text-amber-300' : 'text-[#5B6472] hover:text-amber-300/70'} w-6 h-6 rounded-md flex items-center justify-center bg-white/5 border border-white/10 hover:bg-amber-400/10 hover:border-amber-400/25 transition-colors">
              <svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="m12 17.27 6.18 3.73-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21 12 17.27Z"/></svg>
            </button>
            <button type="button" data-ping="${n.indexId}" title="Ping this node" class="node-ping ${nodeTestRunning ? 'opacity-40 pointer-events-none' : ''} w-6 h-6 rounded-md flex items-center justify-center text-emerald-300 bg-emerald-500/10 border border-emerald-400/20 hover:bg-emerald-500/25 transition-colors">
              <svg class="w-3 h-3" fill="currentColor" viewBox="0 0 24 24"><path d="M13 2 3 14h7l-1 8 10-12h-7l1-8Z"/></svg>
            </button>
            <span class="text-[11px] font-semibold px-2 py-0.5 rounded-md border num-tabular ${delayClass}">${delayText}</span>
          </div>
        </div>
        <div class="mt-3 min-w-0">
          <div class="flex justify-between gap-2 text-[10px] text-[#8A94A6]">
            <span class="uppercase tracking-[0.18em] truncate">${escHtml(n.protocol || '')}</span>
            <span class="num-tabular truncate">${escHtml(n.sub || '')}</span>
          </div>
        </div>
        <div class="mt-3 flex items-center gap-2">
          <span class="w-2 h-2 rounded-full status-dot ${dotClass}" style="color:${dotColor}"></span>
          <span class="text-[10px] uppercase tracking-[0.16em] ${footerClass}">${footerText}</span>
        </div>
      </div>`;
  }

  /** Card shown while browsing the Disabled section: restore or permanently delete. */
  function disabledNodeCard(n) {
    const code = (n.sub || n.protocol || 'VPN').slice(0, 2).toUpperCase();
    const addr = (n.address || '') + (n.port > 0 ? ':' + n.port : '');
    const test = nodeTestState.get(n.indexId);
    const delayText = test && test.fail ? '✗ fail' : n.delay > 0 ? n.delay + ' ms' : '—';
    const delayClass = test && test.fail ? 'bg-red-500/10 text-red-300 border-red-400/25'
      : n.delay > 0 ? 'bg-emerald-500/10 text-emerald-300 border-emerald-400/20'
      : 'bg-amber-500/10 text-amber-300 border-amber-400/20';
    return `
      <div class="node-card relative glass-soft rounded-xl p-4 min-w-0 opacity-90" data-index="${n.indexId}">
        <span class="absolute top-2.5 right-2.5 text-[10px] uppercase tracking-[0.14em] px-1.5 py-0.5 rounded-md bg-amber-500/15 text-amber-300 border border-amber-400/25">Disabled</span>
        <div class="flex items-center gap-3 min-w-0">
          ${aogpn.flags.tileMarkup(n.country, code)}
          <div class="min-w-0">
            <p class="text-sm font-semibold truncate">${escHtml(n.name)}</p>
            <p class="text-[11px] text-[#8A94A6] truncate">${escHtml(addr)}</p>
          </div>
          <span class="ml-auto shrink-0 text-[11px] font-semibold px-2 py-0.5 rounded-md border num-tabular ${delayClass}">${delayText}</span>
        </div>
        <div class="mt-3 flex items-center gap-2">
          <button type="button" data-restore="${n.indexId}" class="text-[11px] font-semibold px-2.5 py-1 rounded-md bg-emerald-500/10 text-emerald-300 border border-emerald-400/25 hover:bg-emerald-500/20 transition-colors">Restore</button>
          <button type="button" data-disabled-delete="${n.indexId}" class="text-[11px] font-semibold px-2.5 py-1 rounded-md bg-red-500/10 text-red-300 border border-red-400/25 hover:bg-red-500/20 transition-colors">Delete</button>
        </div>
      </div>`;
  }

  function updateRealNodeFooter() {
    const node = realNodes.get(activeRealNodeId);
    if (!node) {
      return;
    }
    $('selNodeFlag').innerHTML = aogpn.flags.tileMarkup(node.country, (node.country || node.sub || node.protocol || 'VPN').slice(0, 2).toUpperCase());
    $('selNodeSummary').textContent = node.name;
  }

  /** Reflects a real profile on the dashboard node card immediately; the host confirms it. */
  function applyRealNode(node) {
    const addr = (node.address || '') + (node.port > 0 ? ':' + node.port : '');
    if (node.name && nodeName) nodeName.textContent = node.name;
    if (addr && nodeAddr) nodeAddr.textContent = addr + (node.protocol ? ' · ' + node.protocol : '');
  }

  /** Rebuilds the top-bar node dropdown from the pushed real-node list. */
  function renderTopNodeSelect() {
    const sel = $('topNodeSelect');
    if (!sel) return;
    sel.innerHTML = '';
    const nodes = useRealNodes ? [...realNodes.values()] : [];
    if (nodes.length > 0) {
      nodes.forEach(n => {
        const opt = document.createElement('option');
        opt.value = n.indexId;
        opt.textContent = n.name || n.address || n.indexId;
        sel.appendChild(opt);
      });
      if (realNodes.has(activeRealNodeId)) {
        sel.value = activeRealNodeId;
      }
    } else {
      const opt = document.createElement('option');
      opt.value = '';
      opt.textContent = useRealNodes ? t('topbar.noNodes') : t('topbar.loadingNodes');
      sel.appendChild(opt);
    }
    refreshAllCustomSelects();
  }

  /** Optimistic node switch shared by the Nodes view and the top-bar dropdown. */
  function requestNodeSwitch(indexId) {
    if (!indexId || !realNodes.has(indexId) || indexId === activeRealNodeId) {
      return;
    }
    const node = realNodes.get(indexId);
    // Optimistic switch with a "switching…" state; the host confirms with
    // setNodeSwitchResult. A timeout guards against a dead host.
    previousActiveRealNodeId = activeRealNodeId;
    pendingSwitchId = indexId;
    renderNodes();
    applyRealNode(node);
    postToHost({ action: 'select_node', indexId });
    if (pendingSwitchTimer) clearTimeout(pendingSwitchTimer);
    pendingSwitchTimer = setTimeout(() => {
      if (pendingSwitchId !== indexId) return;
      pendingSwitchId = null;
      activeRealNodeId = previousActiveRealNodeId;
      renderNodes();
      const activeNode = realNodes.get(activeRealNodeId);
      if (activeNode) applyRealNode(activeNode);
      renderTopNodeSelect();
    }, 8000);
  }

  /** Returns the real node list in the current sort order. */
  function sortedRealNodes() {
    const arr = [...realNodes.values()];
    if (nodeSortMode === 'country') {
      arr.sort((a, b) =>
        (a.country || 'ZZ').localeCompare(b.country || 'ZZ')
        || (a.name || '').localeCompare(b.name || ''));
    } else if (nodeSortMode === 'default') {
      // Default order = the host's persisted Sort order. Country grouping still
      // needs countries adjacent, so sort by country only (stable — host order
      // survives inside each group) and tie-break with the pushed order.
      const orderIndex = new Map();
      nodeOrder.forEach((id, idx) => orderIndex.set(id, idx));
      arr.sort((a, b) => {
        const c = (a.country || 'ZZ').localeCompare(b.country || 'ZZ');
        if (c !== 0) return c;
        const ia = orderIndex.has(a.indexId) ? orderIndex.get(a.indexId) : Number.MAX_SAFE_INTEGER;
        const ib = orderIndex.has(b.indexId) ? orderIndex.get(b.indexId) : Number.MAX_SAFE_INTEGER;
        return ia - ib;
      });
    } else if (nodeSortMode === 'fav') {
      arr.sort((a, b) =>
        (b.fav ? 1 : 0) - (a.fav ? 1 : 0)
        || (a.country || 'ZZ').localeCompare(b.country || 'ZZ')
        || (a.name || '').localeCompare(b.name || ''));
    } else if (nodeSortMode === 'recent') {
      arr.sort((a, b) => (b.lastUsed || 0) - (a.lastUsed || 0));
    } else if (nodeSortMode === 'ping' || nodeSortMode === 'pingDesc') {
      // Ping sorting: measured delays order the list; nodes with no measured
      // delay (never tested, failed, or still untested) always sink to the
      // bottom so a "0 ms" node is never presented as the fastest.
      const dir = nodeSortMode === 'ping' ? 1 : -1;
      const measured = a => a.delay > 0;
      arr.sort((a, b) =>
        (measured(a) ? 0 : 1) - (measured(b) ? 0 : 1)
        || dir * (a.delay - b.delay)
        || (a.name || '').localeCompare(b.name || ''));
    }
    return arr;
  }

  function renderNodes() {
    const grid = $('nodeGrid');
    // With no nodes yet there is nothing to be "selected" — hide the selected
    // node row so the view does not claim a connection that cannot exist.
    const selNodeRow = $('selNodeRow');
    if (selNodeRow) {
      selNodeRow.classList.toggle('hidden', !(useRealNodes && (realNodes.size > 0 || showingDisabled)));
    }

    if (useRealNodes && (realNodes.size > 0 || showingDisabled)) {
      if (showingDisabled) {
        grid.innerHTML = [...disabledNodes.values()].map(disabledNodeCard).join('');
        grid.querySelectorAll('.node-card').forEach(card => {
          const node = disabledNodes.get(card.dataset.index);
          if (!node) {
            return;
          }
          const restoreBtn = card.querySelector('[data-restore]');
          if (restoreBtn) {
            restoreBtn.addEventListener('click', () => {
              postToHost({ action: 'restore_nodes', indexIds: [node.indexId] });
            });
          }
          const delBtn = card.querySelector('[data-disabled-delete]');
          if (delBtn) {
            delBtn.addEventListener('click', () => {
              selectedIds.clear();
              selectedIds.add(node.indexId);
              requestNodeDelete();
            });
          }
        });
        $('nodesCount').textContent = disabledNodes.size;
        return;
      }
      const grouped = new Map();
      sortedRealNodes().forEach(node => {
        const country = node.address && node.country ? node.country : t('nodes.countryUnknown');
        if (!grouped.has(country)) grouped.set(country, []);
        grouped.get(country).push(node);
      });
      grid.innerHTML = [...grouped.entries()].map(([country, nodes]) => {
        const groupKey = country;
        const collapsed = collapsedNodeGroups.has(groupKey);
        // Ülke kodu gruplarında bayrak + yerelleştirilmiş ülke adı göster;
        // bilinmeyen gruplar düz etiket kalır (anahtar her zaman ham koddur).
        const isCode = /^[A-Za-z]{2}$/.test(country);
        const flag = isCode ? aogpn.flags.flagMarkup(country, 'w-5 h-4') : null;
        const displayName = isCode ? (aogpn.i18n.countryDisplayName(country) || country) : country;
        return `
        <div class="col-span-full flex items-center gap-2 mt-2 first:mt-0 route-group-header" data-group="${escHtml(groupKey)}">
          <button type="button" class="route-group-toggle flex items-center gap-2 min-w-0" data-group-toggle="${escHtml(groupKey)}" aria-expanded="${!collapsed}">
            <span class="text-cyan-300 text-xs">${collapsed ? '▶' : '▼'}</span>
            ${flag || ''}
            <span class="text-[10px] uppercase tracking-[0.18em] text-cyan-300 font-semibold">${escHtml(displayName)}</span>
            <span class="text-[10px] text-[#64748B]">${nodes.length}</span>
          </button>
          <span class="h-px flex-1 bg-white/10"></span>
        </div>
        <div class="col-span-full route-group-items ${collapsed ? 'hidden' : ''}" data-group-items="${escHtml(groupKey)}">
          <div class="grid sm:grid-cols-2 xl:grid-cols-3 gap-3 md:gap-4">${nodes.map(realNodeCard).join('')}</div>
        </div>`;
      }).join('');
      grid.querySelectorAll('[data-group-toggle]').forEach(toggle => toggle.addEventListener('click', () => {
        const group = toggle.dataset.groupToggle;
        collapsedNodeGroups.has(group) ? collapsedNodeGroups.delete(group) : collapsedNodeGroups.add(group);
        localStorage.setItem(NODE_GROUPS_STORAGE_KEY, JSON.stringify([...collapsedNodeGroups]));
        renderNodes();
      }));
      grid.querySelectorAll('.node-card').forEach(card => {
        const node = realNodes.get(card.dataset.index);
        const favBtn = card.querySelector('[data-fav]');
        if (favBtn) {
          favBtn.addEventListener('click', (event) => {
            event.stopPropagation();
            event.preventDefault();
            postToHost({ action: 'toggle_node_fav', indexId: card.dataset.index });
          });
        }
        const pingBtn = card.querySelector('[data-ping]');
        if (pingBtn) {
          pingBtn.addEventListener('click', (event) => {
            event.stopPropagation();
            event.preventDefault();
            if (!canStartNodeTest()) return;
            const id = card.dataset.index;
            postToHost({ action: 'test_nodes', indexIds: [id], testType: 'tcp', runId: activeNodeTestRunId });
            markNodesTesting([id]);
          });
        }
        // The checkmark is a real checkbox: clicking it toggles the multi-select
        // without switching the tunnel (a plain card click still switches).
        const checkBtn = card.querySelector('[data-check]');
        if (checkBtn) {
          checkBtn.addEventListener('click', (event) => {
            event.stopPropagation();
            event.preventDefault();
            const node = realNodes.get(card.dataset.index);
            if (!node) {
              return;
            }
            if (selectedIds.has(node.indexId)) {
              selectedIds.delete(node.indexId);
            } else {
              selectedIds.add(node.indexId);
            }
            selectionAnchorId = node.indexId;
            updateNodeSelectionUI();
          });
        }
        card.addEventListener('click', (event) => {
          const node = realNodes.get(card.dataset.index);
          if (!node || pendingSwitchId) {
            return;
          }

          // Ctrl/Cmd+click toggles the multi-select without switching the tunnel.
          if (event.ctrlKey || event.metaKey) {
            if (selectedIds.has(node.indexId)) {
              selectedIds.delete(node.indexId);
            } else {
              selectedIds.add(node.indexId);
            }
            selectionAnchorId = node.indexId;
            updateNodeSelectionUI();
            return;
          }

          // Shift+click range-selects from the anchor node.
          if (event.shiftKey && selectionAnchorId) {
            const ids = [...realNodes.keys()];
            const start = ids.indexOf(selectionAnchorId);
            const end = ids.indexOf(node.indexId);
            if (start >= 0 && end >= 0) {
              const lo = Math.min(start, end);
              const hi = Math.max(start, end);
              for (let i = lo; i <= hi; i++) {
                selectedIds.add(ids[i]);
              }
            }
            updateNodeSelectionUI();
            return;
          }

          // Plain click: select this node and switch the tunnel to it.
          // When the card is already the only selected node, clear the
          // selection instead so one-click-dismiss works naturally.
          if (selectedIds.size === 1 && selectedIds.has(node.indexId)) {
            selectedIds.clear();
            selectionAnchorId = null;
            updateNodeSelectionUI();
            return;
          }

          selectedIds.clear();
          selectedIds.add(node.indexId);
          selectionAnchorId = node.indexId;
          updateNodeSelectionUI();
          requestNodeSwitch(node.indexId);
        });

        // Drag-to-reorder, active only in Default order (other sort modes derive
        // their order and would fight the drag). Drops are accepted on cards of
        // the same country group so the grouped layout stays coherent; the host
        // persists the new Sort through the native MoveServer path.
        const canReorder = nodeSortMode === 'default' && !showingDisabled;
        card.draggable = canReorder;
        if (canReorder) {
          card.addEventListener('dragstart', (event) => {
            if (event.target.closest && event.target.closest('[data-fav],[data-ping],[data-check]')) {
              event.preventDefault();
              return;
            }
            dragNodeId = node.indexId;
            event.dataTransfer.effectAllowed = 'move';
            event.dataTransfer.setData('text/plain', node.indexId);
            card.classList.add('dragging');
          });
          card.addEventListener('dragover', (event) => {
            const target = realNodes.get(card.dataset.index);
            const from = dragNodeId ? realNodes.get(dragNodeId) : null;
            if (!target || !from || from.indexId === target.indexId || from.country !== target.country) {
              return;
            }
            event.preventDefault();
            event.dataTransfer.dropEffect = 'move';
            card.classList.add('drop-target');
          });
          card.addEventListener('dragleave', () => card.classList.remove('drop-target'));
          card.addEventListener('drop', (event) => {
            event.preventDefault();
            card.classList.remove('drop-target');
            const fromId = dragNodeId;
            const target = realNodes.get(card.dataset.index);
            if (!fromId || !target || fromId === target.indexId) {
              return;
            }
            postToHost({ action: 'move_node', indexId: fromId, targetIndexId: target.indexId });
          });
          card.addEventListener('dragend', () => {
            dragNodeId = null;
            card.classList.remove('dragging', 'drop-target');
          });
        }
      });
      $('nodesCount').textContent = realNodes.size;
      updateRealNodeFooter();
      return;
    }

    grid.innerHTML = NODES.map(n => {
      const sel = n.key === selectedNode.key;
      return `
      <div class="node-card glass-soft rounded-xl p-4 min-w-0 ${sel ? 'selected' : ''}" data-node="${n.key}">
        <div class="flex items-center gap-3 min-w-0">
          <span class="w-10 h-10 rounded-lg bg-gradient-to-br from-[var(--violet-30)] to-[var(--cyan-20)] flex items-center justify-center font-display font-bold text-sm text-cyan-300 shrink-0 shadow-[0_0_10px_var(--violet-30)]">${n.code}</span>
          <div class="min-w-0">
            <p class="text-sm font-semibold truncate">${n.name}</p>
            <p class="text-[11px] text-[#8A94A6] truncate">${n.addr}</p>
          </div>
          <span class="ml-auto shrink-0 text-[11px] font-semibold px-2 py-0.5 rounded-md bg-cyan-400/10 text-cyan-300 border border-cyan-400/20 num-tabular">${n.ping} ms</span>
        </div>
        <div class="mt-3 min-w-0">
          <div class="flex justify-between text-[10px] text-[#8A94A6]">
            <span class="uppercase tracking-[0.18em]">Load</span>
            <span class="num-tabular">${n.load}%</span>
          </div>
          <div class="h-1.5 mt-1.5 bg-white/5 rounded-full overflow-hidden">
            <div class="load-bar h-full rounded-full" style="width:${n.load}%"></div>
          </div>
        </div>
        <div class="mt-3 flex items-center gap-2">
          <span class="w-2 h-2 rounded-full status-dot ${sel ? 'bg-cyan-400' : 'bg-[#3A4152]'}" style="color:${sel ? '#22D3EE' : '#3A4152'}"></span>
          <span class="text-[10px] uppercase tracking-[0.16em] ${sel ? 'text-cyan-300' : 'text-[#5B6472]'}">${sel ? 'Selected' : 'Tap to select'}</span>
        </div>
      </div>`;
    }).join('');
    grid.querySelectorAll('.node-card').forEach(card => card.addEventListener('click', () => {
      selectedNode = NODES.find(n => n.key === card.dataset.node) || NODES[0];
      applyNode();
    }));
    $('nodesCount').textContent = NODES.length;
  }

  function selectionIds() {
    return [...selectedIds];
  }

  /** Re-renders the checkmarks and toggles the multi-select toolbar. */
  function updateNodeSelectionUI() {
    const bar = $('nodeSelBar');
    const count = selectedIds.size;
    if (count > 0 && !showingDisabled) {
      bar.classList.remove('hidden');
      bar.classList.add('flex');
      $('nodeSelCount').textContent = t('nodes.selectedCount', { n: count });
    } else {
      bar.classList.add('hidden');
      bar.classList.remove('flex');
    }
    renderNodes();
  }

  /** Syncs the header buttons, edit toolbar and disabled banner visibility. */
  function updateNodesHeader() {
    const showAll = $('nodeGroupsShowAllBtn');
    const hideAll = $('nodeGroupsHideAllBtn');
    if (showAll) showAll.onclick = () => { collapsedNodeGroups.clear(); localStorage.setItem(NODE_GROUPS_STORAGE_KEY, '[]'); renderNodes(); };
    if (hideAll) hideAll.onclick = () => {
      const groups = new Set([...realNodes.values()].map(n => n.address && n.country ? n.country : t('nodes.countryUnknown')));
      collapsedNodeGroups = groups;
      localStorage.setItem(NODE_GROUPS_STORAGE_KEY, JSON.stringify([...groups]));
      renderNodes();
    };

    const hasList = realNodes.size > 0 || disabledNodes.size > 0;
    const nodeTestControls = $('nodeTestControls');
    if (nodeTestControls) {
      nodeTestControls.classList.toggle('hidden', !useRealNodes || !hasList);
      nodeTestControls.classList.toggle('flex', useRealNodes && hasList);
    }
    $('nodeTestAllBtn').classList.toggle('hidden', false);
    $('nodeTestAllBtn').classList.toggle('flex', true);
    $('nodeTestAllLabel').textContent = nodeTestRunning ? t('nodes.stopPing') : t('nodes.pingAll');
    const nodeTestAllBtn = $('nodeTestAllBtn');
    if (nodeTestAllBtn) {
      nodeTestAllBtn.setAttribute('aria-busy', nodeTestRunning ? 'true' : 'false');
      nodeTestAllBtn.title = nodeTestRunning ? t('nodes.stopPingTip') : t('nodes.pingAllTip');
    }
    $('nodeDisabledBtn').classList.toggle('hidden', disabledNodes.size === 0 && !showingDisabled);
    $('nodeDisabledBtn').classList.toggle('flex', disabledNodes.size > 0 || showingDisabled);
    $('nodeDisabledLabel').textContent = t('nodes.disabledLabel', { n: disabledNodes.size });
    $('nodeDisabledBarText').textContent = t('nodes.disabledBar', { n: disabledNodes.size });
    $('nodeDisabledBar').classList.toggle('hidden', !showingDisabled);
    $('nodeDisabledBar').classList.toggle('flex', showingDisabled);
    // The Edit toggle only reveals the bulk-cleanup toolbar (dedupe, delete
    // failed). Per-node actions (delete/disable/edit) are always available —
    // destructive ones still go through their own confirmation dialog.
    $('nodeEditBar').classList.toggle('hidden', !editMode || showingDisabled);
    $('nodeEditBar').classList.toggle('flex', editMode && !showingDisabled);
  }

  function syncEditMode() {
    const btn = $('nodeEditBtn');
    btn.classList.toggle('text-violet-300', editMode);
    btn.classList.toggle('bg-violet-500/10', editMode);
    btn.classList.toggle('border-violet-400/30', editMode);
    updateNodesHeader();
    renderNodes();
  }

  const nodeCtxMenu = $('nodeCtxMenu');

  // Right-click on any node card opens the action menu (copy / edit / disable /
  // delete). Event delegation on the grid — the grid element survives re-renders,
  // so the menu can never be dropped by a re-created card losing its listener.
  $('nodeGrid').addEventListener('contextmenu', (event) => {
    const card = event.target && event.target.closest
      ? event.target.closest('.node-card')
      : null;
    if (!card) {
      return;
    }
    const node = realNodes.get(card.dataset.index);
    if (!node || pendingSwitchId) {
      return;
    }
    event.preventDefault();
    if (!selectedIds.has(node.indexId)) {
      selectedIds.clear();
      selectedIds.add(node.indexId);
      selectionAnchorId = node.indexId;
      updateNodeSelectionUI();
    }
    showNodeCtxMenu(event.clientX, event.clientY);
  });

  function showNodeCtxMenu(x, y) {
    nodeCtxMenu.classList.remove('hidden');
    const rect = nodeCtxMenu.getBoundingClientRect();
    nodeCtxMenu.style.left = Math.max(8, Math.min(x, window.innerWidth - rect.width - 8)) + 'px';
    nodeCtxMenu.style.top = Math.max(8, Math.min(y, window.innerHeight - rect.height - 8)) + 'px';
  }
  function hideNodeCtxMenu() {
    nodeCtxMenu.classList.add('hidden');
  }
  document.addEventListener('click', (e) => {
    if (!nodeCtxMenu.classList.contains('hidden') && !nodeCtxMenu.contains(e.target)) {
      hideNodeCtxMenu();
    }
  });
  document.addEventListener('scroll', (e) => {
    const t = e.target;
    if (t && t.nodeType === 1 && (t === nodeCtxMenu || nodeCtxMenu.contains(t))) return;
    hideNodeCtxMenu();
  }, true);

  function showNodeConfirm() {
    $('nodeConfirm').classList.remove('hidden');
    $('nodeConfirm').classList.add('flex');
  }
  function requestConfirm(title, text, action) {
    $('nodeConfirmTitle').textContent = title;
    $('nodeConfirmOkLabel').textContent = t('nodes.confirmApprove');
    $('nodeConfirmText').textContent = text;
    pendingConfirmAction = action;
    showNodeConfirm();
  }
  function requestNodeDelete() {
    const ids = selectionIds();
    if (ids.length === 0) {
      return;
    }
    $('nodeConfirmTitle').textContent = t('nodes.confirmDeleteTitle');
    $('nodeConfirmOkLabel').textContent = t('nodes.delete');
    $('nodeConfirmText').textContent = t('nodes.confirmDeleteText', { n: ids.length });
    pendingConfirmAction = () => postToHost({ action: 'delete_nodes', indexIds: ids });
    showNodeConfirm();
  }
  function requestNodeDisable() {
    const ids = selectionIds();
    if (ids.length === 0) {
      return;
    }
    $('nodeConfirmTitle').textContent = t('nodes.confirmDisableTitle');
    $('nodeConfirmOkLabel').textContent = t('nodes.disable');
    $('nodeConfirmText').textContent = t('nodes.confirmDisableText', { n: ids.length });
    pendingConfirmAction = () => postToHost({ action: 'disable_nodes', indexIds: ids });
    showNodeConfirm();
  }
  function closeNodeConfirm() {
    pendingConfirmAction = null;
    $('nodeConfirm').classList.add('hidden');
    $('nodeConfirm').classList.remove('flex');
  }
  $('nodeConfirmOk').addEventListener('click', () => {
    const action = pendingConfirmAction;
    closeNodeConfirm();
    if (action) {
      action();
    }
  });
  $('nodeConfirmCancel').addEventListener('click', closeNodeConfirm);
  $('nodeConfirm').addEventListener('click', (e) => {
    if (e.target === $('nodeConfirm')) {
      closeNodeConfirm();
    }
  });

  let toastTimer = null;
  window.notifyNodes = (message) => {
    const toast = $('nodeToast');
    toast.textContent = message;
    toast.classList.remove('hidden');
    if (toastTimer) {
      clearTimeout(toastTimer);
    }
    toastTimer = setTimeout(() => toast.classList.add('hidden'), 2600);
  };

  $('nodeCopyBtn').addEventListener('click', () => {
    postToHost({ action: 'copy_nodes', indexIds: selectionIds() });
  });
  const nodeTestTypeSelect = $('nodeTestType');
  const nodeTestSelTypeSelect = $('nodeTestSelType');
  const syncNodeTestType = value => {
    nodeTestType = value === 'udp' || value === 'both' ? value : 'tcp';
    if (nodeTestTypeSelect) nodeTestTypeSelect.value = nodeTestType;
    if (nodeTestSelTypeSelect) nodeTestSelTypeSelect.value = nodeTestType;
  };
  nodeTestTypeSelect?.addEventListener('change', () => syncNodeTestType(nodeTestTypeSelect.value));
  nodeTestSelTypeSelect?.addEventListener('change', () => syncNodeTestType(nodeTestSelTypeSelect.value));

  $('nodeTestSelBtn').addEventListener('click', () => {
    if (!canStartNodeTest()) return;
    const ids = selectionIds();
    if (ids.length === 0) {
      return;
    }
    postToHost({ action: 'test_nodes', indexIds: ids, testType: nodeTestType, runId: activeNodeTestRunId });
    markNodesTesting(ids);
  });
  $('nodeDeleteBtn').addEventListener('click', requestNodeDelete);
  $('nodeDisableSelBtn').addEventListener('click', requestNodeDisable);
  $('nodeClearBtn').addEventListener('click', () => {
    selectedIds.clear();
    selectionAnchorId = null;
    updateNodeSelectionUI();
  });
  $('nodeEditBtn').addEventListener('click', () => {
    editMode = !editMode;
    syncEditMode();
  });
  // Header "Düğüm ekle" button: imports share links from the clipboard, the
  // same path as Ctrl+V (paste_nodes) so adding is discoverable without a key.
  $('nodePasteBtn').addEventListener('click', () => {
    postToHost({ action: 'paste_nodes' });
  });
  $('nodeDisabledBtn').addEventListener('click', () => {
    showingDisabled = !showingDisabled;
    selectedIds.clear();
    selectionAnchorId = null;
    updateNodesHeader();
    renderNodes();
  });
  $('nodeDisabledBackBtn').addEventListener('click', () => {
    showingDisabled = false;
    updateNodesHeader();
    renderNodes();
  });
  $('nodeDeleteAllDisabledBtn').addEventListener('click', () => {
    const ids = [...disabledNodes.keys()];
    if (ids.length === 0) {
      return;
    }
    $('nodeConfirmTitle').textContent = t('nodes.confirmDeleteAllTitle');
    $('nodeConfirmOkLabel').textContent = t('nodes.delete');
    $('nodeConfirmText').textContent = t('nodes.confirmDeleteAllText', { n: ids.length });
    pendingConfirmAction = () => postToHost({ action: 'delete_nodes', indexIds: ids });
    showNodeConfirm();
  });
  $('nodeTestAllBtn').addEventListener('click', () => {
    if (nodeTestRunning) {
      // An accidental double-click right after the run started would hit this
      // stop branch and cancel a test that just began. Ignore clicks inside
      // the same cooldown window canStartNodeTest() enforces for starts; a
      // deliberate stop is never blocked because it happens later.
      if (Date.now() - nodeTestRequestAt < NODE_TEST_COOLDOWN_MS) {
        return;
      }
      postToHost({ action: 'stop_test', runId: activeNodeTestRunId });
      nodeTestRunId++;
      activeNodeTestRunId = nodeTestRunId;
      nodeTestRequestAt = Date.now();
      setNodeTestRunning(false, activeNodeTestRunId);
      nodeTestRunToken++;
      clearNodesTesting(false);
      renderNodes();
      return;
    }
    const ids = [...(showingDisabled ? disabledNodes : realNodes).keys()];
    if (ids.length === 0) {
      return;
    }
    if (!canStartNodeTest()) return;
    postToHost({ action: 'test_nodes', indexIds: ids, testType: nodeTestType, runId: activeNodeTestRunId });
    markNodesTesting(ids);
  });
  $('nodeDedupBtn').addEventListener('click', () => {
    requestConfirm('Yinelenenleri kaldır', 'Liste taranıp her özdeş düğümden yalnızca bir tane mi kalsın? Fazlalıklar silinir.', () => {
      postToHost({ action: 'dedup_nodes' });
    });
  });
  $('nodeCleanupDeleteBtn').addEventListener('click', () => {
    requestConfirm('Başarısız düğümleri sil', 'Son ping testinde başarısız olan tüm düğümler kalıcı olarak silinsin mi?', () => {
      postToHost({ action: 'cleanup_failed', target: 'delete' });
    });
  });
  $('nodeCleanupDisableBtn').addEventListener('click', () => {
    requestConfirm('Başarısız düğümleri devre dışı bırak', 'Son ping testinde başarısız olan tüm düğümler Devre Dışı bölümüne taşınsın mı?', () => {
      postToHost({ action: 'cleanup_failed', target: 'disable' });
    });
  });
  $('nodeCtxMenu').querySelector('[data-act="edit"]').addEventListener('click', () => {
    hideNodeCtxMenu();
    const ids = selectionIds();
    if (ids.length === 0) {
      return;
    }
    // Opens the native server-edit dialog for the right-clicked node (the
    // context menu already made it the sole selection).
    postToHost({ action: 'edit_node', indexId: ids[0] });
  });
  $('nodeCtxMenu').querySelector('[data-act="copy"]').addEventListener('click', () => {
    hideNodeCtxMenu();
    postToHost({ action: 'copy_nodes', indexIds: selectionIds() });
  });
  $('nodeCtxMenu').querySelector('[data-act="ping"]').addEventListener('click', () => {
    hideNodeCtxMenu();
    const ids = selectionIds();
    if (ids.length === 0) {
      return;
    }
    if (!canStartNodeTest()) return;
    postToHost({ action: 'test_nodes', indexIds: ids, testType: nodeTestType, runId: activeNodeTestRunId });
    markNodesTesting(ids);
  });
  $('nodeCtxMenu').querySelector('[data-act="disable"]').addEventListener('click', () => {
    hideNodeCtxMenu();
    requestNodeDisable();
  });
  $('nodeCtxMenu').querySelector('[data-act="delete"]').addEventListener('click', () => {
    hideNodeCtxMenu();
    requestNodeDelete();
  });

  // Node ordering: country A-Z / favorites / recently used / default.
  const nodeSortSel = $('nodeSortSel');
  if (nodeSortSel) {
    nodeSortSel.addEventListener('change', () => {
      nodeSortMode = nodeSortSel.value || 'default';
      renderNodes();
    });
  }
  // Node pool: add a link, fetch nodes from every pooled link, remove a link.
  const nodePoolAddBtn = $('nodePoolAddBtn');
  if (nodePoolAddBtn) {
    nodePoolAddBtn.addEventListener('click', () => {
      const input = $('nodePoolUrl');
      const url = (input?.value || '').trim();
      if (!url) {
        return;
      }
      postToHost({ action: 'add_node_pool_link', url });
      input.value = '';
    });
  }
  const nodePoolUrlInput = $('nodePoolUrl');
  if (nodePoolUrlInput) {
    nodePoolUrlInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        nodePoolAddBtn?.click();
      }
    });
  }
  const nodePoolFetchBtn = $('nodePoolFetchBtn');
  if (nodePoolFetchBtn) {
    nodePoolFetchBtn.addEventListener('click', () => {
      postToHost({ action: 'fetch_node_pool' });
    });
  }

  // Node clipboard/delete shortcuts, active only while the Nodes view is shown.
  document.addEventListener('keydown', (event) => {
    if (aogpn.app.getCurrentView() !== 'nodes' || !useRealNodes) {
      return;
    }
    const mod = event.ctrlKey || event.metaKey;
    const key = event.key.toLowerCase();
    if (mod && key === 'a') {
      event.preventDefault();
      selectedIds.clear();
      (showingDisabled ? disabledNodes : realNodes).forEach((_, id) => selectedIds.add(id));
      selectionAnchorId = null;
      updateNodeSelectionUI();
    } else if (mod && key === 'c') {
      event.preventDefault();
      postToHost({ action: 'copy_nodes', indexIds: selectionIds() });
    } else if (mod && key === 'v') {
      event.preventDefault();
      postToHost({ action: 'paste_nodes' });
    } else if (event.key === 'Delete' || event.key === 'Backspace') {
      // With no explicit selection, fall back to deleting the active node.
      let ids = selectionIds();
      if (ids.length === 0 && !showingDisabled && activeRealNodeId) {
        ids = [activeRealNodeId];
      }
      if (ids.length > 0) {
        event.preventDefault();
        selectedIds.clear();
        ids.forEach(id => selectedIds.add(id));
        updateNodeSelectionUI();
        requestNodeDelete();
      }
    } else if (event.key === 'Escape') {
      if (showingDisabled) {
        showingDisabled = false;
        updateNodesHeader();
      } else if (selectedIds.size > 0) {
        selectedIds.clear();
        selectionAnchorId = null;
        updateNodeSelectionUI();
      }
      hideNodeCtxMenu();
      closeNodeConfirm();
    }
  });

  // View titles come from the i18n dictionary so they switch language instantly,
  // like every other label. Each view splits into two parts so the second word
  // keeps the gradient; the subtitle is a plain sentence.


  window.updateNodeInfo = (name, address, protocol) => {
    hostNode = {
      name: typeof name === 'string' ? name : '',
      address: typeof address === 'string' ? address : '',
      protocol: typeof protocol === 'string' ? protocol : ''
    };
    applyHostNode();
  };
  // The host streams the real profile list in chunks and finalizes with the id
  // of the active profile. Until the finalize arrives the concept list stands.
  window.updateNodeListAppend = (chunk) => {
    if (!Array.isArray(chunk)) {
      return;
    }
    chunk.forEach(n => {
      if (!n || typeof n.indexId !== 'string' || !n.indexId) {
        return;
      }
      pushedNodeIds.add(n.indexId);
      pushedNodeOrder.push(n.indexId);
      realNodes.set(n.indexId, {
        indexId: n.indexId,
        name: n.name || '',
        address: n.address || '',
        port: Number.isFinite(n.port) ? n.port : 0,
        protocol: n.protocol || '',
        sub: n.sub || '',
        delay: Number.isFinite(n.delay) ? n.delay : 0,
        active: n.active === true,
        country: typeof n.country === 'string' ? n.country : '',
        fav: n.fav === true,
        lastUsed: Number.isFinite(n.lastUsed) ? n.lastUsed : 0
      });
    });
  };
  window.updateNodeListDone = (activeIndexId) => {
    if (typeof activeIndexId === 'string' && activeIndexId) {
      activeRealNodeId = activeIndexId;
    }
    useRealNodes = true;
    // The host always pushes the complete current group; entries from earlier
    // rounds (deleted nodes, switched groups) must not linger in the map.
    if (pushedNodeIds.size > 0) {
      [...realNodes.keys()].forEach(id => {
        if (!pushedNodeIds.has(id)) {
          realNodes.delete(id);
        }
      });
      pushedNodeIds.clear();
    }
    // Finalize the round's order (the host pushed the whole list) and keep only
    // ids that still exist, so deleted nodes cannot linger in the order.
    nodeOrder = pushedNodeOrder.filter(id => realNodes.has(id));
    pushedNodeOrder.length = 0;
    // Drop selection ids that no longer exist (e.g. right after a delete), so a
    // stale selection never lingers on the toolbar or in the copy payload.
    [...selectedIds].forEach(id => {
      if (!realNodes.has(id)) {
        selectedIds.delete(id);
      }
    });
    updateNodesHeader();
    updateNodeSelectionUI();
    const activeNode = realNodes.get(activeRealNodeId);
    if (activeNode) {
      applyRealNode(activeNode);
    }
    renderTopNodeSelect();
  };
  // The host publishes the Disabled section (nodes hidden from the main list).
  window.updateDisabledNodes = (nodes) => {
    if (!Array.isArray(nodes)) {
      return;
    }
    disabledNodes.clear();
    nodes.forEach(n => {
      if (!n || typeof n.indexId !== 'string' || !n.indexId) {
        return;
      }
      disabledNodes.set(n.indexId, {
        indexId: n.indexId,
        name: n.name || '',
        address: n.address || '',
        port: Number.isFinite(n.port) ? n.port : 0,
        protocol: n.protocol || '',
        sub: n.sub || '',
        delay: Number.isFinite(n.delay) ? n.delay : 0,
        active: n.active === true
      });
    });
    // Disabled ids are filtered out of the main list by the host as well, so
    // drop any stale entries (and their selection) from the active map.
    disabledNodes.forEach((_, id) => {
      realNodes.delete(id);
      selectedIds.delete(id);
    });
    updateNodesHeader();
    if (showingDisabled) {
      renderNodes();
    }
  };
  // Drag-and-drop WireGuard .conf import: while any file drag is over the
  // window a full-screen hint shows; on drop, every .conf file is read here
  // (File.text, no host round-trip needed) and handed to the host, which
  // imports it as a server named after the file. Non-file drags (e.g. the
  // node-card reorder) pass through untouched.
  const fileDropOverlay = $('fileDropOverlay');
  let fileDragDepth = 0;
  const isFileDrag = (event) => event.dataTransfer
    && [...event.dataTransfer.types].includes('Files');
  const hideFileDropOverlay = () => {
    fileDragDepth = 0;
    if (fileDropOverlay) {
      fileDropOverlay.classList.add('hidden');
      fileDropOverlay.classList.remove('flex');
    }
  };
  window.addEventListener('dragover', (event) => {
    if (!isFileDrag(event)) {
      return;
    }
    event.preventDefault();
    fileDragDepth++;
    if (fileDropOverlay) {
      fileDropOverlay.classList.remove('hidden');
      fileDropOverlay.classList.add('flex');
    }
  });
  window.addEventListener('dragleave', (event) => {
    if (!isFileDrag(event)) {
      return;
    }
    fileDragDepth = Math.max(0, fileDragDepth - 1);
    if (fileDragDepth === 0) {
      hideFileDropOverlay();
    }
  });
  window.addEventListener('drop', (event) => {
    hideFileDropOverlay();
    if (!event.dataTransfer || !event.dataTransfer.files || event.dataTransfer.files.length === 0) {
      return;
    }
    event.preventDefault();
    const confs = [...event.dataTransfer.files].filter(f => /\.conf$/i.test(f.name));
    if (confs.length === 0) {
      return;
    }
    Promise.all(confs.map(async (f) => ({ name: f.name, content: await f.text() })))
      .then(files => postToHost({ action: 'import_wireguard_conf', files }))
      .catch(() => notifyNodes('WireGuard .conf could not be read'));
  });

  // Node pool links published by the host (GitHub raw .txt / subscription URLs).
  window.updateNodePool = (links) => {
    nodePoolLinks = Array.isArray(links) ? links.filter(l => typeof l === 'string') : [];
    renderNodePool();
  };
  // The pool URL currently being edited inline (null when no row is in edit mode).
  let editingPoolUrl = null;
  function renderNodePool() {
    const list = $('nodePoolList');
    if (!list) return;
    if (nodePoolLinks.length === 0) {
      list.innerHTML = '<li class="text-[10px] text-[#5B6472] py-1" data-i18n="nodes.poolEmpty">No links in the pool yet.</li>';
      applyTexts();
      return;
    }
    list.innerHTML = nodePoolLinks.map(link => {
      if (link === editingPoolUrl) {
        // Inline edit row: pre-filled input + Save / Cancel (Enter saves, Escape cancels).
        return `<li class="flex items-center gap-2 min-w-0">
          <input type="text" data-pool-edit-input value="${escHtml(link)}" spellcheck="false" class="flex-1 min-w-0 bg-white/5 border border-cyan-400/40 rounded-md px-2 py-1 text-[11px] text-slate-200 outline-none focus:border-cyan-400/70" />
          <button type="button" data-pool-edit-save class="shrink-0 text-[10px] font-semibold px-2 py-1 rounded-md bg-emerald-500/10 text-emerald-300 border border-emerald-400/25 hover:bg-emerald-500/20 transition-colors" data-i18n="nodes.poolSave">Save</button>
          <button type="button" data-pool-edit-cancel class="shrink-0 text-[10px] font-semibold px-2 py-1 rounded-md bg-white/5 text-[#8A94A6] border border-white/10 hover:text-slate-200 transition-colors" data-i18n="nodes.poolCancel">Cancel</button>
        </li>`;
      }
      return `<li class="flex items-center gap-2 min-w-0 group">
        <span class="w-1.5 h-1.5 rounded-full bg-cyan-400/60 shrink-0"></span>
        <span class="text-[11px] text-slate-300 truncate min-w-0">${escHtml(link)}</span>
        <button type="button" data-pool-edit="${escHtml(link)}" class="shrink-0 text-[10px] font-semibold px-2 py-0.5 rounded-md bg-white/5 text-[#8A94A6] border border-white/10 hover:text-cyan-300 hover:bg-cyan-500/10 hover:border-cyan-400/25 transition-colors" data-i18n="nodes.poolEdit">Edit</button>
        <button type="button" data-pool-remove="${escHtml(link)}" class="shrink-0 text-[10px] font-semibold px-2 py-0.5 rounded-md bg-white/5 text-[#8A94A6] border border-white/10 hover:text-red-300 hover:bg-red-500/10 hover:border-red-400/25 transition-colors" data-i18n="nodes.poolRemove">Remove</button>
      </li>`;
    }).join('');

    // Normal rows: switch a row into inline-edit mode, or remove the link.
    list.querySelectorAll('[data-pool-edit]').forEach(btn => {
      btn.addEventListener('click', () => {
        editingPoolUrl = btn.dataset.poolEdit;
        renderNodePool();
        const input = list.querySelector('[data-pool-edit-input]');
        if (input) {
          input.focus();
          input.select();
        }
      });
    });
    list.querySelectorAll('[data-pool-remove]').forEach(btn => {
      btn.addEventListener('click', () => {
        postToHost({ action: 'remove_node_pool_link', url: btn.dataset.poolRemove });
      });
    });

    // Edit row: Save commits (the host validates, persists and republishes),
    // Cancel / Escape discards, Enter saves.
    const editInput = list.querySelector('[data-pool-edit-input]');
    if (editInput) {
      const finish = (save) => {
        const newUrl = editInput.value.trim();
        const oldUrl = editingPoolUrl;
        editingPoolUrl = null;
        if (save && newUrl !== oldUrl) {
          // Reflect the change locally right away; the host republishes to confirm.
          const idx = nodePoolLinks.findIndex(l => l === oldUrl);
          if (idx >= 0) {
            nodePoolLinks[idx] = newUrl;
          }
          postToHost({ action: 'edit_node_pool_link', url: oldUrl, newUrl });
        }
        renderNodePool();
      };
      editInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          finish(true);
        } else if (e.key === 'Escape') {
          finish(false);
        }
      });
      list.querySelector('[data-pool-edit-save]')?.addEventListener('click', () => finish(true));
      list.querySelector('[data-pool-edit-cancel]')?.addEventListener('click', () => finish(false));
    }
    applyTexts();
  }
  // Per-node real ping result from the host: a numeric delay (ms), "-1" for a
  // failed test, or a transient status string while the run is in progress.
  window.updateNodeTest = (indexId, delayStr, runId) => {
    if (runId !== undefined && Number(runId) !== activeNodeTestRunId) return;
    // The host always sends the delay as a string (the native SpeedtestService
    // path and the WireGuard probe chain both do), but tolerate a raw JSON
    // number too — a numeric delay must never be silently dropped.
    const status = typeof delayStr === 'string' ? delayStr
      : delayStr === null || delayStr === undefined ? ''
      : String(delayStr);
    // Ignore late callbacks from a cancelled/replaced run. A stale worker must
    // never resurrect the busy state or overwrite a newer ping result.
    if (indexId && !nodeTestRunning && !(nodeTestState.get(indexId) || {}).testing) return;
    if (!indexId) {
      // Global final status: clear only the active run's states. A late callback
      // from an older run must not finalize a newer one. A node that never
      // reported (still mid-test when the run ended) got no response, so mark
      // it failed unless the run finished cleanly — an unreachable node must
      // read as broken instead of silently keeping a stale value.
      clearNodesTesting(!status || !/completed|finished/i.test(status));
      setNodeTestRunning(false);
      const msg = status || 'Ping test finished';
      if (msg && !/completed|finished|stopped|cancel/i.test(msg)) {
        notifyNodes(msg);
      }
      renderNodes();
      return;
    }
    const trimmed = status.trim();
    const numeric = /^-?\d+$/.test(trimmed);
    if (numeric) {
      const ms = parseInt(trimmed, 10);
      const node = realNodes.get(indexId) || disabledNodes.get(indexId);
      if (node) {
        node.delay = ms > 0 ? ms : 0;
      }
      const currentState = nodeTestState.get(indexId);
      if (currentState?.runId !== undefined && currentState.runId !== activeNodeTestRunId) return;
      nodeTestState.set(indexId, { testing: false, fail: ms === -1 });
    } else if (!(nodeTestState.get(indexId) || {}).testing) {
      // A status string on a node that is not mid-test (e.g. "Skip") finalizes it.
      nodeTestState.set(indexId, { testing: false, fail: false });
    }
    if (!showingDisabled) {
      renderNodes();
    }
  };
  window.setNodeTestRunning = (running, runId) => {
    if (runId !== undefined && Number(runId) !== activeNodeTestRunId) return;
    nodeTestRunning = running === true;
    if (!nodeTestRunning) {
      clearNodesTesting(false);
    }
    if (nodeTestRunning) nodeTestRunToken++;
    else nodeTestRunToken = Math.abs(nodeTestRunToken);
    updateNodesHeader();
    renderNodes();
  };
  // Host acknowledgement for a select_node attempt. On success the authoritative
  // active id (re-pushed list) wins; on failure the optimistic switch is rolled
  // back to the node that was active before the attempt.
  window.setNodeSwitchResult = (success, indexId) => {
    if (pendingSwitchTimer) {
      clearTimeout(pendingSwitchTimer);
      pendingSwitchTimer = null;
    }
    pendingSwitchId = null;
    if (success !== true) {
      activeRealNodeId = previousActiveRealNodeId;
    } else if (typeof indexId === 'string' && indexId) {
      activeRealNodeId = indexId;
    }
    renderNodes();
    const activeNode = realNodes.get(activeRealNodeId);
    if (activeNode) {
      applyRealNode(activeNode);
    }
    renderTopNodeSelect();
  };



  window.aogpn = window.aogpn || {};
  window.aogpn.nodes = {
    applyHostNode, refreshSessionNode, applyNode, renderTopNodeSelect,
    requestNodeSwitch, renderNodes, renderNodePool,
    getUseRealNodes: () => useRealNodes,
    getRealNodes: () => realNodes,
    getNodes: () => NODES,
    getNodesList: () => useRealNodes ? [...realNodes.values()] : NODES,
    getActiveRealNodeId: () => activeRealNodeId,
    getSelectedNode: () => selectedNode,
    setSelectedNode: (v) => { selectedNode = v; },
    setUseRealNodes: (v) => { useRealNodes = !!v; },
    getNodePoolLinks: () => nodePoolLinks
  };
})();
