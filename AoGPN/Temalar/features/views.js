/* ==========================================================================
   features/views.js — AoGPN dashboard feature module (görünümler & rota tabloları)
   --------------------------------------------------------------------------
   Görünüm geçişleri (showView), GlassWire-style Connection Monitor, split
   routing tablosu (route select / remove / reorder), dashboard Game Boost
   kartları, process kataloğu + seçici, host-çözümlü uygulama ikonları ve
   window.updateMonitorSnapshot / setAppIcons / __aogpnT köprüleri. Durum
   (currentView, splitMode, monitorSnapshot, ...) app.js koordinatöründe
   aogpn.app getter/setter'ları üzerinden okunur/yazılır.
   ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const resolveKey = aogpn.i18n.resolveKey;
  const escHtml = aogpn.util.escHtml;
  const $ = aogpn.util.$;
  const postToHost = aogpn.bridge.postToHost;
  const upgradeSelect = aogpn.selects.upgradeSelect;

  function viewTitleParts(view) {
    return [
      t('view.' + view + '.title1'),
      t('view.' + view + '.title2'),
      t('view.' + view + '.sub')
    ];
  }

  function showView(view) {
    aogpn.app.setCurrentView(view);
    const real = ['dashboard', 'nodes', 'gpnServers', 'perf', 'boost', 'settings', 'about'].includes(view) ? view : 'coming';
    document.querySelectorAll('section[id^="view"]').forEach(s => s.classList.add('hidden'));
    const target = real === 'coming' ? 'viewComingSoon' : 'view' + real[0].toUpperCase() + real.slice(1);
    $(target).classList.remove('hidden');
    // Titles resolve fresh from the active dictionary on every view switch, so
    // switching language while sitting in a view also re-renders correctly.
    const parts = viewTitleParts(real);
    if (real === 'coming') {
      $('soonTitle').textContent = (parts[0] + parts[1]).trim();
    }
    $('viewTitle').innerHTML = parts[0] + '<span class="bg-gradient-to-r from-[var(--violet)] to-[var(--cyan)] bg-clip-text text-transparent">' + parts[1] + '</span>';
    $('viewSub').textContent = parts[2];
    document.querySelectorAll('[data-view]').forEach(item => item.classList.toggle('active', item.dataset.view === view));
    postToHost({ action: 'set_active_view', view: real });
    if (view === 'perf' || view === 'boost') {
      postToHost({ action: 'request_monitor_snapshot' });
    }
    if (view === 'nodes') {
      postToHost({ action: 'get_node_pool' });
    }
    if (view === 'gpnServers') {
      postToHost({ action: 'gpn_servers_list' });
      aogpn.gpn.startGpnProbeLoop();
    } else {
      aogpn.gpn.stopGpnProbeLoop();
    }
  }

  // ---------- GlassWire-style monitor and split-routing views ----------
  const routeLabels = {
    vpn: 'VPN',
    proxy: 'Proxy',
    'vpn+proxy': 'VPN + Proxy',
    direct: 'Direct',
    block: 'Block',
    warp: 'WARP',
    '': 'Assign route…'
  };
  // True while GPN blacklist direction is active for the boost table.
  const blacklistActive = () => aogpn.app.getInvertManual() && (aogpn.app.getMode() === 'gpn' || aogpn.app.getSplitMode() === 'manual');
  // Effective per-app route in blacklist mode: the listed apps are the exceptions,
  // so a VPN assignment keeps them direct and a direct assignment tunnels them.
  function effectiveRoute(action) {
    if (!blacklistActive()) return action;
    return action === 'direct' ? 'vpn' : (action === 'block' ? 'block' : 'direct');
  }
  // Route badge colour tag: vpn action renders with the proxy colour.
  const tagForRoute = (a) => a === 'vpn' ? 'proxy' : a;

  function routeBadge(tag, text) {
    const normalized = ['proxy', 'direct', 'block', 'warp'].includes(tag) ? tag : 'unknown';
    return `<span class="route-badge ${normalized}">${escHtml(text || routeLabels[tag] || tag || 'Unknown')}</span>`;
  }

  // Per-app WARP egress düğümü — Ayarlar → GPN'deki eski VLESS bypass düğümünün
  // yerine: "warp" rotasındaki satır, düğüm listesinden seçilen bir düğümün
  // egress'inden çıkar (İtalya / Almanya / Güney Afrika gibi mevcut düğümlerden
  // hangisi isteniyorsa). Boş değer = varsayılan (aktif düğümün WARP egress'i).
  function warpNodeNameFor(indexId) {
    if (!indexId) return '';
    if (aogpn.app.getUseRealNodes() && aogpn.app.getRealNodes().has(indexId)) {
      const n = aogpn.app.getRealNodes().get(indexId);
      return (n && n.name) || '';
    }
    const fallback = aogpn.app.getNodes().find(x => x.key === indexId);
    return fallback ? fallback.name : '';
  }

  function warpNodeOptions(selectedId) {
    const nodes = aogpn.app.getUseRealNodes() && aogpn.app.getRealNodes().size > 0
      ? [...aogpn.app.getRealNodes().values()]
      : aogpn.app.getNodes().map(n => ({ indexId: n.key, name: n.name }));
    const opts = ['<option value="">' + escHtml(t('warp.nodeDefault')) + '</option>'];
    nodes.forEach(n => {
      const id = n.indexId;
      const label = n.name || id;
      opts.push(`<option value="${escHtml(id)}"${String(id) === String(selectedId || '') ? ' selected' : ''}>${escHtml(label)}</option>`);
    });
    return opts.join('');
  }

  function warpNodeSelect(processName, displayName, selectedId) {
    return `<select data-route-warp-node="${escHtml(processName)}" data-route-display="${escHtml(displayName || processName)}" title="${escHtml(t('warp.nodePickerHint'))}" class="bg-white/5 border border-white/10 rounded-md px-2 py-1 text-[10px] text-slate-200 outline-none focus:border-cyan-400/50">${warpNodeOptions(selectedId)}</select>`;
  }

  function routeSelect(processName, displayName, selected, warpNodeIndexId) {
    if (!processName) {
      return '<span class="text-[#5B6472]">—</span>';
    }
    // Legacy "proxy" / "vpn+proxy" values are displayed as the merged "vpn" option.
    const normalized = ['proxy', 'vpn+proxy'].includes(selected) ? 'vpn' : selected;
    const value = ['vpn', 'direct', 'block', 'warp'].includes(normalized) ? normalized : '';
    // In blacklist direction the dropdown spells out the EFFECTIVE meaning of each
    // assignment (the value sent to the host is unchanged): a VPN pick keeps the
    // app outside the tunnel, a Direct pick routes it into the tunnel.
    const blacklist = blacklistActive();
    const options = [
      ['', t('route.assign')],
      ['vpn', blacklist ? t('dir.selectVpn') : t('route.vpn')],
      ['direct', blacklist ? t('dir.selectDirect') : t('route.direct')],
      ['block', blacklist ? t('dir.selectBlock') : t('route.block')],
      ['warp', blacklist ? t('dir.selectWarp') : t('route.warp')]
    ].map(([key, label]) => `<option value="${key}"${key === value ? ' selected' : ''}>${label}</option>`).join('');
    const sel = `<select data-route-process="${escHtml(processName)}" data-route-display="${escHtml(displayName || processName)}" class="bg-white/5 border border-white/10 rounded-md px-2 py-1 text-[10px] text-slate-200 outline-none focus:border-cyan-400/50">${options}</select>`;
    // WARP rotasındaki satırların yanında düğüm seçici görünür: kullanıcı bu
    // uygulamanın WARP trafiğinin hangi mevcut düğümden çıkacağını seçer.
    if (value === 'warp' || warpNodeIndexId) {
      return `<span class="inline-flex items-center gap-1 min-w-0">${sel}${warpNodeSelect(processName, displayName, warpNodeIndexId)}</span>`;
    }
    return sel;
  }

  function monitorFilterMatches(item, filter) {
    if (!filter) return true;
    const haystack = [item.processName, item.displayName, item.remoteAddress, item.countryText, item.asnText, item.routeText].join(' ');
    return haystack.toLowerCase().includes(filter.toLowerCase());
  }

  function configuredActionFor(processName) {
    return (aogpn.app.getMonitorSnapshot().apps || []).find(item =>
      item.entryType === 'app' && String(item.processName).toLowerCase() === String(processName).toLowerCase())?.action || '';
  }

  function configuredWarpNodeFor(processName) {
    return (aogpn.app.getMonitorSnapshot().apps || []).find(item =>
      item.entryType === 'app' && String(item.processName).toLowerCase() === String(processName).toLowerCase())?.warpNodeIndexId || '';
  }

  function renderMonitorConnections() {
    const bodyEl = $('monitorConnectionsBody');
    if (!bodyEl) return;
    const filter = ($('monitorFilter')?.value || '').trim();
    const hideListeners = $('monitorHideListeners')?.checked !== false;
    const rows = (aogpn.app.getMonitorSnapshot().connections || []).filter(item => {
      if (hideListeners && item.protocol === 'TCP' && item.state === 'Listen') return false;
      return monitorFilterMatches(item, filter);
    });
    $('monitorEmpty')?.classList.toggle('hidden', rows.length > 0);
    bodyEl.innerHTML = rows.length > 0 ? rows.map(item => {
      const route = item.routeTag || '';
      const country = [item.countryText, item.asnText].filter(Boolean).join(' · ') || '—';
      return `<tr class="hover:bg-white/[.035] transition-colors">
        <td class="px-3 py-2.5"><div class="flex items-center gap-2.5 min-w-0">${appIconMarkup(item.exePath, item.displayName || item.processName || 'APP', 'w-6 h-6')}<div class="min-w-0"><p class="font-medium text-slate-100 truncate max-w-[175px]">${escHtml(item.displayName || item.processName || 'Unknown')}</p><p class="text-[10px] text-[#64748B] truncate max-w-[175px]">${escHtml(item.processName || '')}${item.pid ? ' · PID ' + escHtml(item.pid) : ''}</p></div></div></td>
        <td class="px-3 py-2.5">${routeBadge(route, item.routeText || routeLabels[route])}</td>
        <td class="px-3 py-2.5 text-[#A7B0BF]">${escHtml(item.protocol || '—')}</td>
        <td class="px-3 py-2.5 text-[#CBD5E1] font-mono text-[10px] max-w-[180px] truncate" title="${escHtml(item.remoteAddress || '')}">${escHtml(item.remoteAddress || '—')}</td>
        <td class="px-3 py-2.5 text-[#8A94A6] max-w-[150px] truncate" title="${escHtml(country)}">${escHtml(country)}</td>
        <td class="px-3 py-2.5 text-[#8A94A6]">${escHtml(item.state || '—')}</td>
        <td class="px-3 py-2.5">${routeSelect(item.processName, item.displayName, configuredActionFor(item.processName), configuredWarpNodeFor(item.processName))}</td>
      </tr>`;
    }).join('') : '';
    bindRouteSelectors(bodyEl);
  }

  /**
   * Renders the dashboard's GPN Game Boost card strip from real app data.
   * Shows up to 4 VPN-routed apps; click-through to the full Game Boost view.
   */
  function renderDashboardBoostCards() {
    const container = document.getElementById('dashboardBoostCards');
    const badge = document.getElementById('boostSummaryBadge');
    if (!container) return;
    const apps = (aogpn.app.getMonitorSnapshot().apps || [])
      .filter(a => a.action === 'vpn' || a.action === 'vpn+proxy')
      .slice(0, 4);
    const runningCount = apps.filter(a => a.isRunning).length;
    const totalCount = (aogpn.app.getMonitorSnapshot().apps || []).length;
    if (badge) {
      badge.textContent = runningCount > 0
        ? t('boost.running', { n: runningCount })
        : (totalCount > 0 ? t('boost.defined', { n: totalCount }) : t('boost.none'));
    }
    if (apps.length === 0) {
      container.innerHTML = '<div class="glass-soft rounded-xl p-3.5 flex items-center gap-3 min-w-0 opacity-60 col-span-full"><div class="min-w-0"><p class="text-sm text-[#8A94A6]">' + t('boost.noGames', { link: '<a href="#" data-view="boost" class="text-cyan-300 underline">' + t('nav.boost') + '</a>' }) + '</p></div></div>';
      return;
    }
    container.innerHTML = apps.map((item, i) => {
      const label = (item.displayName || item.processName || item.value || '').slice(0, 4).toUpperCase() || 'APP';
      const routeLabel = routeLabels[item.action] || 'VPN';
      const isRunning = item.isRunning === true;
      // Gerçek ping (oyun sunucusu) önce/sonra: kayıtlı uç noktalara program
      // açılışında doğrudan yoldan (beforePingText) ve GPN bağlantısından sonra
      // tünel yolundan (afterPingText) ölçülür; ikisi de varsa fark (delta) alt
      // satırda gösterilir. Büyük değer gerçek ping'i tercih eder (düğüm ping'i
      // değil) — önce yoksa sonra, o da yoksa eski düğüm gecikmesi kalır.
      const realBefore = (item.beforePingText && item.beforePingText !== '—') ? item.beforePingText : null;
      const realAfter = (item.afterPingText && item.afterPingText !== '—') ? item.afterPingText : null;
      const delta = (realBefore && realAfter && item.pingDeltaText) ? item.pingDeltaText : '';
      const primaryLatency = realAfter || realBefore || item.latencyText || '—';
      const primaryMs = realAfter ? item.afterPingMs : (realBefore ? item.beforePingMs : item.latencyMs);
      const realLine = realPingMarkup(item);
      return `<div class="glass-soft rounded-xl p-3.5 flex items-center gap-3 min-w-0 ${isRunning ? '' : 'opacity-60'}">
        ${appIconMarkup(item.exePath, label, 'w-10 h-10')}
        <div class="min-w-0">
          <p class="text-sm font-semibold truncate ${isRunning ? '' : 'text-[#A7B0BF]'}">${escHtml(item.displayName || item.processName || item.value)}</p>
          <p class="text-[11px] ${isRunning ? 'text-[#8A94A6]' : 'text-[#5B6472]'} truncate">${isRunning ? t('boost.routeActive', { route: routeLabel }) : t('boost.idleNoBoost')}</p>
        </div>
        <span class="ml-auto shrink-0 flex flex-col items-end gap-0.5">
          <span class="latency-ms text-[10px] font-mono font-semibold ${primaryLatency !== '—' ? (primaryMs < 80 ? 'text-emerald-300' : primaryMs < 180 ? 'text-amber-300' : 'text-red-300') : 'text-[#5B6472]'}">${escHtml(primaryLatency)}</span>
          ${realLine}
          <span class="w-2 h-2 rounded-full ${isRunning ? 'bg-emerald-400 status-dot' : 'bg-[#5B6472]'}" style="${isRunning ? 'color:#34D399' : ''}"></span>
      </div>`;
    }).join('');
  }

  function renderProcessCatalog() {
    const root = $('boostProcessList');
    if (!root) return;
    const filter = ($('boostProcessFilter')?.value || '').trim().toLowerCase();
    const items = aogpn.app.getProcessCatalog().filter(item => {
      const haystack = [item.displayName, item.processName, item.exePath].join(' ').toLowerCase();
      return !filter || haystack.includes(filter);
    });
    if (items.length === 0) {
      root.innerHTML = '<p class="text-[11px] text-[#64748B] py-3">No eligible running applications found.</p>';
      return;
    }
    root.innerHTML = items.map(item => {
      const label = item.displayName || item.processName || 'Application';
      const path = item.exePath || item.processName || '';
      return `<button type="button" data-process-pid="${escHtml(String(item.pid))}" data-process-name="${escHtml(item.processName || '')}" data-display-name="${escHtml(item.displayName || '')}" class="w-full flex items-center gap-3 py-2 text-left hover:bg-white/5 rounded-md px-2 transition-colors">
        ${appIconMarkup(item.exePath, 'EXE', 'w-7 h-7')}
        <span class="min-w-0"><span class="block text-[11px] font-semibold text-slate-100 truncate">${escHtml(label)}</span><span class="block text-[10px] text-[#64748B] truncate">${escHtml(path)} · PID ${escHtml(String(item.pid))}</span></span>
        <span class="ml-auto text-[10px] text-cyan-300 shrink-0">Add</span>
      </button>`;
    }).join('');
    root.querySelectorAll('[data-process-pid]').forEach(button => {
      button.addEventListener('click', () => {
        const pid = Number.parseInt(button.dataset.processPid || '', 10);
        if (!Number.isInteger(pid) || pid <= 0) return;
        postToHost({
          action: 'add_running_process',
          pid,
          processName: button.dataset.processName || '',
          displayName: button.dataset.displayName || '',
        });
      });
    });
  }

  function setProcessPickerOpen(open) {
    const picker = $('boostProcessPicker');
    const button = $('boostRunningAppsBtn');
    if (!picker || !button) return;
    picker.classList.toggle('hidden', !open);
    button.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (open) {
      postToHost({ action: 'list_running_processes' });
    }
  }

  // Canlı hedef adreslerini üretir (mihomo /connections metadata): virgülle
  // ayrılmış listeden ilk iki uç nokta gösterilir, fazlası "+N" ile özetlenir;
  // tam liste hücre tooltip'inde kalır. Veri yoksa — basılır.
  function targetIpsMarkup(item) {
    const raw = (item.activeIps || '').trim();
    if (!raw) return '<span class="text-[#5B6472]">—</span>';
    const ips = raw.split(',').map(s => s.trim()).filter(Boolean);
    const shown = ips.slice(0, 2).join(', ');
    const extra = ips.length > 2 ? ` +${ips.length - 2}` : '';
    return escHtml(shown + extra);
  }

  // Gerçek sunucu ping'i (önce → sonra) satırını üretir: kayıtlı uç noktalara
  // program açılışında doğrudan yoldan (beforePingText) ve GPN bağlantısından
  // sonra seçili/aktif düğüm üzerinden (afterPingText) ölçülür. İkisi de varsa
  // fark (delta) renk kodlu gösterilir ve "sonra" ölçümünün yapıldığı düğüm
  // adı "via" etiketiyle eklenir — delta hangi düğümün iyileştirmesi olduğu
  // açıkça okunur. Veri yoksa boş döner (çağıran yer kendi boş göstergesini basar).
  function realPingMarkup(item) {
    const realBefore = (item.beforePingText && item.beforePingText !== '—') ? item.beforePingText : null;
    const realAfter = (item.afterPingText && item.afterPingText !== '—') ? item.afterPingText : null;
    const delta = (realBefore && realAfter && item.pingDeltaText) ? item.pingDeltaText : '';
    const via = (realAfter && aogpn.app.getActiveGpnServer())
      ? ` · ${escHtml(t('boost.viaNode', { node: aogpn.app.getActiveGpnServer() }))}`
      : '';
    if (realBefore && realAfter) {
      return `<span class="text-[9px] font-mono ${delta.startsWith('+') ? 'text-red-300' : 'text-emerald-300'}" title="${escHtml(t('boost.before') + ' ' + realBefore + ' → ' + t('boost.after') + ' ' + realAfter)}">${escHtml(realBefore)} → ${escHtml(realAfter)}${delta ? ' (' + escHtml(delta) + ')' : ''}${via}</span>`;
    }
    if (realBefore) {
      return `<span class="text-[9px] font-mono text-[#5B6472]">${escHtml(t('boost.before') + ' ' + realBefore)}</span>`;
    }
    if (realAfter) {
      return `<span class="text-[9px] font-mono text-[#8A94A6]">${escHtml(t('boost.after') + ' ' + realAfter)}${via}</span>`;
    }
    return '';
  }

  function renderSplitApps() {
    const bodyEl = $('splitAppsBody');
    if (!bodyEl) return;
    const all = aogpn.app.getMonitorSnapshot().apps || [];
    const filter = ($('boostAppFilter')?.value || '').trim().toLowerCase();
    const apps = filter ? all.filter(item => {
      const name = (item.displayName || item.processName || item.value || '').toLowerCase();
      const action = (routeLabels[item.action] || item.routeText || '').toLowerCase();
      const live = (item.liveRouteText || '').toLowerCase();
      return name.includes(filter) || action.includes(filter) || live.includes(filter);
    }) : all;
    $('boostAppCount').textContent = filter
      ? `${apps.length} of ${all.length} entr${all.length === 1 ? 'y' : 'ies'}`
      : `${apps.length} entr${apps.length === 1 ? 'y' : 'ies'}`;
    $('boostEmpty')?.classList.toggle('hidden', apps.length > 0);
    const dirNotice = $('boostDirNotice');
    if (dirNotice) dirNotice.classList.toggle('hidden', !blacklistActive());
    const dirNoticeText = $('boostDirNoticeText');
    if (dirNoticeText) dirNoticeText.textContent = t('dir.blacklistTableHint');
    // WARP rotası yalnızca WireGuard modunda çalışır: warp girişi varken aktif
    // bağlantı WireGuard değilse (V2ray TCP fallback) açık uyarı göster — kurallar
    // sessizce VPN'e düşmesin.
    const warpWarn = $('warpFallbackNotice');
    if (warpWarn) {
      const warpEntries = all.filter(a => a.action === 'warp').length;
      const warpBlocked = warpEntries > 0 && aogpn.app.getActiveGpnMode() && aogpn.app.getActiveGpnMode() !== 'WireGuard';
      warpWarn.classList.toggle('hidden', !warpBlocked);
      const warpWarnText = $('warpFallbackNoticeText');
      if (warpWarnText) warpWarnText.textContent = t('warp.fallbackNotice');
    }
    bodyEl.innerHTML = apps.length > 0 ? apps.map(item => {
      const live = item.liveRouteTag || '';
      const down = item.downloadText || '—';
      const up = item.uploadText || '—';
      const status = item.isRunning ? (item.runStatusText || 'Running') : (item.runStatusText || 'Not running');
      const processName = item.processName || item.value || '';
      // Rule order = list order (cores match top-down): rows can be moved up/down
      // so a domain rule can be placed above the process rule it takes precedence
      // over. Buttons are disabled at the edges of the FULL list; while a filter is
      // active the controls are hidden to avoid ambiguity (the move targets the
      // real position, not the filtered one).
      const rowIndex = all.indexOf(item);
      const first = rowIndex <= 0;
      const last = rowIndex >= all.length - 1;
      const entryType = item.entryType || 'app';
      const entryValue = item.value || processName;
      const reorderCells = filter
        ? ''
        : `<button type="button" data-move-entry="up" data-move-etype="${escHtml(entryType)}" data-move-value="${escHtml(entryValue)}" ${first ? 'disabled' : ''} aria-label="Move up" title="Move up" class="w-6 h-6 rounded-md flex items-center justify-center text-slate-400/80 hover:text-cyan-300 hover:bg-white/10 transition-colors ${first ? 'opacity-25' : ''}">↑</button>`
        + `<button type="button" data-move-entry="down" data-move-etype="${escHtml(entryType)}" data-move-value="${escHtml(entryValue)}" ${last ? 'disabled' : ''} aria-label="Move down" title="Move down" class="w-6 h-6 rounded-md flex items-center justify-center text-slate-400/80 hover:text-cyan-300 hover:bg-white/10 transition-colors ${last ? 'opacity-25' : ''}">↓</button>`;
      // In blacklist mode the route column shows the EFFECTIVE route (what the
      // app actually does), with a per-row chip that spells out the inversion:
      // a VPN assignment keeps this app OUTSIDE the tunnel (the common
      // "exclude" case), a Direct assignment routes it INTO the tunnel. Block
      // is not inverted, so it carries no chip.
      const eff = effectiveRoute(item.action);
      let effLabel = blacklistActive() && eff !== item.action
        ? (routeLabels[eff] || eff)
        : (routeLabels[item.action] || item.routeText);
      // WARP + per-app düğüm: rozet seçilen egress düğümünü gösterir
      // ("WARP · İtalya" gibi) — kara listede warp istisnası Direct görünürken
      // düğüm adı eklenmez (o satır tünel dışıdır).
      if (eff === 'warp' && item.warpNodeIndexId) {
        const warpName = warpNodeNameFor(item.warpNodeIndexId);
        if (warpName) effLabel = (effLabel || routeLabels.warp || 'WARP') + ' · ' + warpName;
      }
      const dirMark = blacklistActive() && item.action === 'vpn'
        ? `<span class="ml-1 text-[9px] px-1 py-px rounded bg-amber-500/15 text-amber-300 border border-amber-400/25" title="${escHtml(t('dir.tipRowExcluded'))}">⇄ ${escHtml(t('dir.rowExcluded'))}</span>`
        : blacklistActive() && item.action === 'direct'
          ? `<span class="ml-1 text-[9px] px-1 py-px rounded bg-cyan-400/10 text-cyan-300 border border-cyan-400/30" title="${escHtml(t('dir.tipRowTunneled'))}">⇄ ${escHtml(t('dir.rowTunneled'))}</span>`
          : '';
      return `<tr class="hover:bg-white/[.035] transition-colors" data-process-name="${escHtml(processName)}">
        <td class="px-3 py-2.5"><div class="flex items-center gap-2.5 min-w-0">${appIconMarkup(item.exePath, item.displayName || item.processName || item.value, 'w-6 h-6')}<div class="min-w-0"><p class="font-medium text-slate-100 truncate max-w-[160px]">${escHtml(item.displayName || item.processName || item.value)}</p><p class="text-[10px] text-[#64748B] truncate max-w-[160px]">${escHtml(processName)}</p></div></div></td>
        <td class="px-3 py-2.5">${routeBadge(tagForRoute(eff), effLabel)}${dirMark}</td>
        <td class="px-3 py-2.5">${live ? routeBadge(live, item.liveRouteText) : '<span class="text-[#5B6472]">Idle</span>'}</td>
        <td class="px-3 py-2.5"><span class="inline-flex items-center gap-1.5 text-[10px] ${item.isRunning ? 'text-emerald-300' : 'text-[#8A94A6]'}"><span class="w-1.5 h-1.5 rounded-full ${item.isRunning ? 'bg-emerald-400 animate-pulse' : 'bg-[#5B6472]'}"></span>${escHtml(status)}</span></td>
        <td class="px-3 py-2.5 text-[10px] font-mono">${realPingMarkup(item) || '<span class="text-[#5B6472]">—</span>'}</td>
        <td class="px-3 py-2.5 text-[10px] font-mono text-[#8A94A6] max-w-[170px] truncate" title="${escHtml(item.activeIps || '')}">${targetIpsMarkup(item)}</td>
        <td class="px-3 py-2.5 text-[10px] text-cyan-200 font-mono num-tabular">${escHtml(down)}</td>
        <td class="px-3 py-2.5 text-[10px] text-purple-200 font-mono num-tabular">${escHtml(up)}</td>
        <td class="px-3 py-2.5">${routeSelect(processName, item.displayName || item.value, item.action, item.warpNodeIndexId)}</td>
        <td class="px-3 py-2.5 whitespace-nowrap">${reorderCells}<button data-remove-process="${escHtml(processName)}" class="delete-app-btn w-6 h-6 rounded-md flex items-center justify-center text-red-400/60 hover:text-red-300 hover:bg-red-500/10 transition-colors" title="Remove ${escHtml(item.displayName || processName)}"><svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M6 19a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V7H6v12ZM8 9h8v10H8V9Zm.5-5 1-1h5l1 1H19v2H5V4h3.5Z"/></svg></button></td>
      </tr>`;
    }).join('') : '';
    bindRouteSelectors(bodyEl);
    bindDeleteButtons(bodyEl);
    bindMoveButtons(bodyEl);
  }

  function bindRouteSelectors(root) {
    root.querySelectorAll('[data-route-process]').forEach(select => {
      upgradeSelect(select);
      select.addEventListener('change', () => {
        const route = select.value;
        if (!route) return;
        postToHost({
          action: 'set_app_route',
          processName: select.dataset.routeProcess,
          displayName: select.dataset.routeDisplay,
          route
        });
      });
    });
    // Per-app WARP egress düğümü seçicisi: satırın warp rotası bu düğümden çıkar.
    root.querySelectorAll('[data-route-warp-node]').forEach(select => {
      upgradeSelect(select);
      select.addEventListener('change', () => {
        const nodeId = select.value || '';
        postToHost({
          action: 'set_app_route',
          processName: select.dataset.routeWarpNode,
          displayName: select.dataset.routeDisplay,
          route: 'warp',
          warpNodeIndexId: nodeId,
          warpNodeName: nodeId ? warpNodeNameFor(nodeId) : ''
        });
      });
    });
  }

  function bindDeleteButtons(root) {
    root.querySelectorAll('[data-remove-process]').forEach(btn => {
      btn.addEventListener('click', () => {
        const processName = btn.dataset.removeProcess;
        if (!processName) return;
        if (!confirm(`Remove "${processName}" from the routing list?`)) return;
        postToHost({
          action: 'remove_app',
          processName
        });
      });
    });
  }

  function bindMoveButtons(root) {
    root.querySelectorAll('[data-move-entry]').forEach(btn => {
      btn.addEventListener('click', () => {
        if (btn.disabled) return;
        postToHost({
          action: 'move_route',
          entryType: btn.dataset.moveEtype,
          value: btn.dataset.moveValue,
          direction: btn.dataset.moveEntry
        });
      });
    });
  }

  function applySplitMode() {
    document.querySelectorAll('.split-mode-pill').forEach(button => {
      button.classList.toggle('active', button.dataset.splitMode === aogpn.app.getSplitMode());
    });
    const hints = {
      off: 'Off — disconnected. No traffic is captured; your independent system proxy preference is untouched.',
      vpn: 'Global VPN — all traffic follows the selected VPN profile and TUN/proxy transport.',
      manual: 'GPN Game Tunnel — only assigned games/apps are tunneled; unlisted traffic stays direct.'
    };
    if ($('boostModeHint')) $('boostModeHint').textContent = hints[aogpn.app.getSplitMode()] || hints.off;
    // Route mode is one shared concept: the Game Boost pills, the quick selector
    // and the Dashboard pills all drive the same backend routing mode.
    if (aogpn.app.getSplitMode() === 'vpn' || aogpn.app.getSplitMode() === 'manual') {
      const nextMode = aogpn.app.getSplitMode() === 'manual' ? 'gpn' : 'vpn';
      if (aogpn.app.getMode() !== nextMode) {
        aogpn.app.setMode(nextMode);
        aogpn.app.applyMode();
      }
    }
    aogpn.app.syncQuickControls();
  }

  /** Toggles the GPN routing-direction pills and their hint text. */
  function applyDirection() {
    document.querySelectorAll('.dir-pill').forEach(p => {
      const on = (p.dataset.dir === 'blacklist') === aogpn.app.getInvertManual();
      p.classList.toggle('active', on);
      p.classList.toggle('text-cyan-300', on);
      p.classList.toggle('border-cyan-400/40', on);
      p.classList.toggle('bg-cyan-400/10', on);
      p.classList.toggle('text-[#A7B0BF]', !on);
      p.classList.toggle('border-white/10', !on);
    });
    const hint = document.getElementById('dirHint');
    if (hint) hint.textContent = aogpn.app.getInvertManual() ? t('dir.hintBlacklist') : t('dir.hintWhitelist');
    updateGlobalPanel();
    // Direction changes flip the meaning of the boost-table route column.
    if (aogpn.app.getCurrentView() === 'boost') renderSplitApps();
  }

  /** Keeps the Global VPN panel honest: only live data, no hard-coded badges. */
  /**
   * The "TUN does not touch the system proxy" independence notice is only
   * meaningful while DISCONNECTED: once CONNECT hands the proxy over to the
   * active tunnel (PROXY · CONNECTION), the tunnel OWNS it, so the notice must
   * hide. Called on every proxy-state / transport change AND on every
   * connect/disconnect transition (setConnected).
   */
  function updateTunProxyNotice() {
    const proxyNotice = document.getElementById('tunProxyNotice');
    if (proxyNotice) {
      const show = aogpn.app.getTransport() === 'tun' && !aogpn.app.getConnected() && aogpn.app.isProxyModeEnabled(aogpn.app.getSystemProxyMode());
      proxyNotice.classList.toggle('hidden', !show);
    }
  }

  function updateGlobalPanel() {
    const vpnActive = aogpn.app.getMode() === 'vpn' && aogpn.app.getConnected();
    const badge = document.getElementById('globalStatusBadge');
    if (badge) {
      badge.textContent = vpnActive ? t('global.allTraffic') : t('global.disconnected');
      badge.className = 'shrink-0 text-[10px] px-2 py-0.5 rounded-full border ' + (vpnActive ? 'bg-emerald-400/10 text-emerald-300 border-emerald-400/20' : 'bg-slate-500/10 text-slate-400 border-white/10');
    }
    const transport = document.getElementById('globalTransport');
    if (transport) transport.textContent = vpnActive ? 'TUN' : '—';
    const bar = document.getElementById('globalCoverageBar');
    if (bar) bar.style.width = vpnActive ? '100%' : '0%';
  }

  window.updateProcessList = (items) => {
    aogpn.app.setProcessCatalog(Array.isArray(items)
      ? items.filter(item => item && Number.isInteger(item.pid) && item.pid > 0).slice(0, 300)
      : []);
    renderProcessCatalog();
    requestAppIcons(aogpn.app.getProcessCatalog().map(item => item.exePath));
  };

  // ---------- App icons (host-resolved shell icons) ----------
  // The tables show the REAL icon of each executable (the same icon Explorer
  // displays). The renderer never extracts icons itself: it collects the exe
  // paths currently on screen and asks the host once per new path
  // (action: get_app_icons). The host answers with
  // window.setAppIcons({ icons: { '<path>': 'data:image/png;base64,…' } }),
  // which lands in appIconCache (keyed by the lowercased path) and triggers a
  // re-render so rows swap their letter placeholder for the real icon.
  const appIconCache = {};
  const APP_ICON_REQUEST_CAP = 48;

  function normalizedIconPath(value) {
    return (typeof value === 'string' ? value.trim() : '').toLowerCase();
  }

  function requestAppIcons(paths) {
    if (!Array.isArray(paths)) return;
    const missing = [];
    const seen = new Set();
    paths.forEach(path => {
      const key = normalizedIconPath(path);
      if (!key || key.length > 320 || seen.has(key) || appIconCache[key]) return;
      seen.add(key);
      missing.push(key);
    });
    if (missing.length === 0) return;
    postToHost({ action: 'get_app_icons', paths: missing.slice(0, APP_ICON_REQUEST_CAP) });
  }

  // Icon or letter-tile placeholder for one row/tile. The letter tile keeps the
  // layout stable while the host resolves the icon (or when extraction fails).
  function appIconMarkup(exePath, fallbackText, cls) {
    const key = normalizedIconPath(exePath);
    const uri = key && appIconCache[key];
    if (uri) {
      return `<img src="${uri}" alt="" class="app-icon ${cls || ''}" draggable="false" />`;
    }
    const label = String(fallbackText || 'APP').slice(0, 2).toUpperCase() || '?';
    return `<span class="app-icon ${cls || ''}">${escHtml(label)}</span>`;
  }

  window.setAppIcons = (payload) => {
    if (!payload || typeof payload !== 'object' || !payload.icons) return;
    let changed = false;
    Object.keys(payload.icons).forEach(path => {
      const key = normalizedIconPath(path);
      const uri = payload.icons[path];
      if (key && typeof uri === 'string' && uri.startsWith('data:image/') && appIconCache[key] !== uri) {
        appIconCache[key] = uri;
        changed = true;
      }
    });
    if (!changed) return;
    // Refresh every consumer: the Game Boost table, the Connection Monitor
    // table, the running-apps picker and the dashboard boost cards.
    renderSplitApps();
    renderMonitorConnections();
    renderProcessCatalog();
    renderDashboardBoostCards();
    // Standalone skins render the same exe icons through skinBridge.appIcons;
    // tell them an icon arrived so their rows swap the placeholder right away.
    aogpn.events.emit('skin-changed');
  };

  // Translation bridge for standalone dashboard modules (e.g. boost-view.js):
  // they resolve their labels through the SAME dictionary pipeline as the main
  // UI, so a language switch re-renders them instantly. Missing keys return
  // null so a module can fall back to its own embedded string.
  window.__aogpnT = (key, params) => {
    const val = resolveKey(key, params);
    return val === null ? null : val;
  };

  window.updateMonitorSnapshot = (data) => {
    if (!data || typeof data !== 'object') return;
    aogpn.app.setMonitorSnapshot({
      ...data,
      connections: Array.isArray(data.connections) ? data.connections : [],
      apps: Array.isArray(data.apps) ? data.apps : [],
      traffic: Array.isArray(data.traffic) ? data.traffic : []
    });
    // Collect the exe paths the tables are about to display and ask the host
    // for their shell icons once per new path.
    requestAppIcons([
      ...(aogpn.app.getMonitorSnapshot().connections || []).map(item => item.exePath),
      ...(aogpn.app.getMonitorSnapshot().apps || []).map(item => item.exePath),
      ...(aogpn.app.getMonitorSnapshot().traffic || []).map(item => item.exePath)
    ].filter(Boolean));
    aogpn.app.setSplitMode(['off', 'vpn', 'manual'].includes(data.mode) ? data.mode : aogpn.app.getSplitMode());
    if (typeof data.invertManualRouting === 'boolean') {
      aogpn.app.setInvertManual(data.invertManualRouting);
      applyDirection();
    }
    $('monitorConnectionsCount').textContent = String(data.activeConnectionCount ?? aogpn.app.getMonitorSnapshot().connections.length);
    // Transient socket-flush highlight: when the TUN starts and kills pre-existing
    // connections, surface the count in the Connections card sub-text for one tick.
    const subEl = $('monitorConnectionsSubtext');
    const flushed = data.flushedSocketCount;
    if (subEl && typeof flushed === 'number' && flushed > 0) {
      // Show the flush notification with an emerald highlight.
      subEl.textContent = flushed + ' socket' + (flushed !== 1 ? 's' : '') + ' reset on tunnel start';
      subEl.className = 'text-[10px] text-emerald-400 mt-1 font-semibold';
      subEl.dataset.flushShown = '1';
    } else if (subEl && subEl.dataset.flushShown === '1') {
      // Revert to the default sub-text one tick after the flush was shown.
      subEl.textContent = 'active sockets';
      subEl.className = 'text-[10px] text-[#64748B] mt-1';
      delete subEl.dataset.flushShown;
    }
    $('monitorAppsCount').textContent = String(data.activeAppCount ?? aogpn.app.getMonitorSnapshot().apps.length);
    $('monitorDownload').textContent = data.totalDownloadText || '0.0 B';
    $('monitorUpload').textContent = data.totalUploadText || '0.0 B';
    // Update dashboard boost cards with real app data.
    renderDashboardBoostCards();
    $('monitorTrafficStatus').textContent = data.trafficStatus || 'No core traffic sample yet.';
    $('monitorLiveBadge').textContent = data.connected ? 'Live · ' + String(data.mode || 'connected').toUpperCase() : 'Monitor ready · disconnected';
    $('monitorLiveBadge').className = 'text-[10px] uppercase tracking-[.16em] px-2.5 py-1 rounded-full border ' + (data.connected ? 'bg-emerald-400/10 text-emerald-300 border-emerald-400/20' : 'bg-cyan-400/10 text-cyan-300 border-cyan-400/20');
    // Routing-engine badge — shows whether the core is running GPN (stripped
    // v2rayN baggage), Global VPN (full legacy), Proxy, or None.
    var rmBadge = $('routingModeBadge');
    var rmLabel = $('routingModeLabel');
    if (rmBadge && rmLabel) {
      var rm = (typeof data.routingMode === 'string' && data.routingMode) ? data.routingMode : 'none';
      var rmText = rm === 'gpn' ? 'GPN Game Tunnel' : rm === 'global' ? 'Global VPN' : rm === 'proxy' ? 'Proxy Capture' : '';
      var rmColors = rm === 'gpn'
        ? 'bg-violet-400/10 text-violet-300 border-violet-400/30'
        : rm === 'global'
          ? 'bg-cyan-400/10 text-cyan-300 border-cyan-400/30'
          : rm === 'proxy'
            ? 'bg-emerald-400/10 text-emerald-300 border-emerald-400/30'
            : 'bg-slate-700/30 text-slate-500 border-slate-700/40';
      if (rm === 'none' || !rmText) {
        rmBadge.classList.add('hidden');
      } else {
        rmBadge.classList.remove('hidden');
        rmLabel.textContent = rmText;
        rmBadge.className = 'hidden sm:flex items-center gap-1.5 rounded-full px-3 py-1 text-[10px] font-semibold uppercase tracking-[.14em] border ' + rmColors;
      }
    }
    const auto = $('boostAutoGameConnect');
    if (auto && document.activeElement !== auto) auto.checked = data.autoConnectOnGameStart === true;
    // Capture-drift warning: GPN modunda tünellenmesi gereken bir oyunun canlı
    // bağlantısı var ama yakalama köprüsü ondan hiç paket saymadı — trafik
    // doğrudan gidiyor (host GpnCaptureDriftChecker → monitor snapshot captureDrift).
    const driftBanner = document.getElementById('captureDriftBanner');
    const driftText = document.getElementById('captureDriftText');
    if (driftBanner && driftText) {
      const drift = Array.isArray(data.captureDrift) ? data.captureDrift : [];
      if (drift.length > 0) {
        const names = drift.map(d => d.processName).filter(Boolean).join(', ');
        driftText.textContent = t('capture.driftBanner')
          .replace('{count}', String(drift.length))
          .replace('{names}', names);
        driftBanner.classList.remove('hidden');
      } else {
        driftBanner.classList.add('hidden');
      }
    }
    applySplitMode();
    // Rebuilding the connection/split tables is the expensive part of the 2 s poll;
    // skip it while those views are hidden and re-render on the explicit snapshot
    // requested when the user opens them.
    if (aogpn.app.getCurrentView() === 'perf') {
      renderMonitorConnections();
    }
    if (aogpn.app.getCurrentView() === 'boost') {
      renderSplitApps();
    }
    // The host pushes a fresh monitor snapshot every ~2 s — notify the loaded
    // skins immediately so boost apps, routes and telemetry stay live without
    // waiting on the skins' own safety poll (which may be throttled when the
    // app is backgrounded).
    aogpn.events.emit('skin-changed');
  };


  window.aogpn = window.aogpn || {};
  window.aogpn.views = {
    showView, viewTitleParts, renderSplitApps, renderMonitorConnections,
    renderDashboardBoostCards, renderProcessCatalog, setProcessPickerOpen,
    applySplitMode, applyDirection, updateGlobalPanel, updateTunProxyNotice,
    getRouteLabels: () => routeLabels
  };
})();
