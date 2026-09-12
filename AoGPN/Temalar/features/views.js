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

  // ---- Same-application grouping for the detail tables ----
  // Rows that belong to the same application (same display/app name) collapse
  // under one expandable header (default closed); clicking the header opens the
  // group so its rows can be edited. Single-entry applications stay as plain
  // rows. Open groups survive re-renders (snapshot pushes re-run the renderer),
  // so edits inside an expanded group are never lost.
  const expandedMonitorGroups = new Set();
  const expandedBoostGroups = new Set();

  function appNameOf(item) {
    return String(item.displayName || item.processName || item.value || 'Unknown').trim() || 'Unknown';
  }

  // Segments preserve the original list order: { single } plain rows and
  // { groupName, items } blocks (only names appearing more than once become
  // groups).
  function partitionAppSegments(items, nameOf) {
    const counts = new Map();
    for (const it of items) { const k = nameOf(it); counts.set(k, (counts.get(k) || 0) + 1); }
    const segments = [];
    const emitted = new Set();
    for (const it of items) {
      const k = nameOf(it);
      if (counts.get(k) === 1) { segments.push({ single: it }); continue; }
      if (emitted.has(k)) continue;
      emitted.add(k);
      segments.push({ groupName: k, items: items.filter(x => nameOf(x) === k) });
    }
    return segments;
  }

  // Route tables can also render a "remove group" action (routing list only):
  // one confirm removes every entry under the group (apps + domain/IP rules).
  const GROUP_REMOVE_SVG = '<svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M6 19a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V7H6v12ZM8 9h8v10H8V9Zm.5-5 1-1h5l1 1H19v2H5V4h3.5Z"/></svg>';

  function appGroupHeaderHtml(groupName, items, expanded, cols, removable) {
    const icon = appIconMarkup(items[0] && items[0].exePath, groupName, 'w-6 h-6');
    const removeBtn = removable
      ? '<button type="button" class="route-app-group-del shrink-0 w-6 h-6 mr-1.5 rounded-md flex items-center justify-center text-red-400/60 hover:text-red-300 hover:bg-red-500/10 transition-colors" data-app-group-remove="' + escHtml(groupName) + '" aria-label="' + escHtml(t('boost.view.del')) + '" title="' + escHtml(t('boost.removeGroupTip', { name: groupName })) + '">' + GROUP_REMOVE_SVG + '</button>'
      : '';
    return '<tr class="route-app-group bg-white/[.015]">'
      + '<td colspan="' + cols + '" class="px-0 py-0">'
      + '<div class="flex items-center gap-1 pr-0.5">'
      + '<button type="button" class="route-app-group-btn flex-1 min-w-0 flex items-center gap-2.5 px-3 py-2.5 text-left transition-colors hover:bg-white/[.03]" data-app-group-key="' + escHtml(groupName) + '" aria-expanded="' + (expanded ? 'true' : 'false') + '">'
      + '<span class="text-[9px] text-[#8A94A6] w-4 shrink-0 text-center">' + (expanded ? '▾' : '▸') + '</span>'
      + icon
      + '<span class="text-xs font-medium text-slate-100 truncate min-w-0">' + escHtml(groupName) + '</span>'
      + '<span class="ml-auto shrink-0 text-[9px] px-1.5 py-0.5 rounded-full bg-white/5 text-[#8A94A6] border border-white/10">' + items.length + '</span>'
      + '</button>'
      + removeBtn
      + '</div></td></tr>';
  }

  function bindAppGroupToggles(root, openSet, rerender) {
    root.querySelectorAll('[data-app-group-key]').forEach(btn => {
      btn.addEventListener('click', () => {
        const key = btn.dataset.appGroupKey;
        if (openSet.has(key)) openSet.delete(key); else openSet.add(key);
        if (typeof rerender === 'function') rerender();
      });
    });
  }

  function viewTitleParts(view) {
    return [
      t('view.' + view + '.title1'),
      t('view.' + view + '.title2'),
      t('view.' + view + '.sub')
    ];
  }

  function showView(view) {
    aogpn.app.setCurrentView(view);
    const real = ['dashboard', 'nodes', 'perf', 'boost', 'settings', 'about'].includes(view) ? view : 'coming';
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
      // GPN sunucu yönetimi artık bu görünümün içinde (gpnServersPanel) —
      // katalog ve canlı ölçüm döngüsü düğüm görünümü açıkken çalışır.
      postToHost({ action: 'get_node_pool' });
      postToHost({ action: 'gpn_servers_list' });
      aogpn.gpn.startGpnProbeLoop();
    } else {
      aogpn.gpn.stopGpnProbeLoop();
    }
    if (view === 'settings') {
      // Ayarlar → GPN sekmesi (Bağlantı Merkezi'nden taşınan Gelişmiş & Teşhis):
      // kuyruk/Wintun config değerlerini ve canlı ölçümü görünüm açılınca iste —
      // geri kalan kartlar host'un 2 sn'lik poll'uyla canlı beslenir.
      postToHost({ action: 'get_gpn_capture_settings' });
      postToHost({ action: 'get_gpn_wintun_settings' });
      postToHost({ action: 'gpn_servers_probe' });
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
    const rowHtml = (item, hidden) => {
      const route = item.routeTag || '';
      const country = [item.countryText, item.asnText].filter(Boolean).join(' · ') || '—';
      return `<tr class="hover:bg-white/[.035] transition-colors${hidden ? ' hidden' : ''}" data-app-group="${escHtml(appNameOf(item))}">
        <td class="px-3 py-2.5"><div class="flex items-center gap-2.5 min-w-0">${appIconMarkup(item.exePath, item.displayName || item.processName || 'APP', 'w-6 h-6')}<div class="min-w-0"><p class="font-medium text-slate-100 truncate max-w-[175px]">${escHtml(item.displayName || item.processName || 'Unknown')}</p><p class="text-[10px] text-[#64748B] truncate max-w-[175px]">${escHtml(item.processName || '')}${item.pid ? ' · PID ' + escHtml(item.pid) : ''}</p></div></div></td>
        <td class="px-3 py-2.5">${routeBadge(route, item.routeText || routeLabels[route])}</td>
        <td class="px-3 py-2.5 text-[#A7B0BF]">${escHtml(item.protocol || '—')}</td>
        <td class="px-3 py-2.5 text-[#CBD5E1] font-mono text-[10px] max-w-[180px] truncate" title="${escHtml(item.remoteAddress || '')}">${escHtml(item.remoteAddress || '—')}</td>
        <td class="px-3 py-2.5 text-[#8A94A6] max-w-[150px] truncate" title="${escHtml(country)}">${escHtml(country)}</td>
        <td class="px-3 py-2.5 text-[#8A94A6]">${escHtml(item.state || '—')}</td>
        <td class="px-3 py-2.5">${routeSelect(item.processName, item.displayName, configuredActionFor(item.processName), configuredWarpNodeFor(item.processName))}</td>
      </tr>`;
    };
    const htmlByIdx = new Map(rows.map(it => [it, rowHtml(it, false)]));
    const cols = 7;
    const forceOpen = filter.trim() !== '';
    bodyEl.innerHTML = partitionAppSegments(rows, appNameOf).map(seg => {
      if (seg.single) return htmlByIdx.get(seg.single);
      const open = forceOpen || expandedMonitorGroups.has(seg.groupName);
      const members = seg.items.map(it => {
        const row = htmlByIdx.get(it);
        return open ? row : row.replace('class="hover:bg-white/[.035] transition-colors"', 'class="hover:bg-white/[.035] transition-colors hidden"');
      }).join('');
      return appGroupHeaderHtml(seg.groupName, seg.items, open, cols) + members;
    }).join('');
    bindRouteSelectors(bodyEl);
    bindAppGroupToggles(bodyEl, expandedMonitorGroups, renderMonitorConnections);
  }

  // ============ Connection Center app rail (all routed apps) ============
  // Every managed APPLICATION in the Game Boost list renders as a fixed-width
  // card inside #dashboardBoostCards — a horizontal rail that scrolls BOTH ways
  // (‹ › buttons, wheel/trackpad or drag), so no app is hidden behind a 4-card
  // cap anymore. Domain/IP rules belong to the full management view and stay
  // out of the rail. Each card shows the live route, real server ping (before
  // → after) and a per-app GPN switch so an app can be routed through (or taken
  // off) the tunnel straight from the Connection Center.
  function isRuleEntry(item) {
    return !!item && (item.entryType === 'domain' || item.entryType === 'ip');
  }

  // Effective route semantics mirror the Game Boost table: whitelist tunnels
  // vpn/vpn+proxy/proxy/warp apps, blacklist tunnels apps assigned 'direct'.
  function isTunnelRoutedApp(item) {
    if (!item || isRuleEntry(item)) return false;
    const action = item.action;
    if (!action) return false;
    if (blacklistActive()) return action === 'direct';
    return action === 'vpn' || action === 'vpn+proxy' || action === 'proxy' || action === 'warp';
  }

  function boostCardMarkup(item) {
    const pname = item.processName || item.value || '';
    const display = item.displayName || item.processName || item.value || 'APP';
    const isRunning = item.isRunning === true;
    const tunneled = isTunnelRoutedApp(item);
    const eff = effectiveRoute(item.action);
    const effLabel = routeLabels[eff] || String(eff || '').toUpperCase() || '—';
    const statusText = tunneled
      ? (isRunning ? t('boost.routeActive', { route: effLabel }) : t('boost.idleNoBoost'))
      : (eff === 'block' ? routeLabels.block : t('dir.rowExcluded'));
    // Gerçek ping (oyun sunucusu) önce/sonra: kayıtlı uç noktalara program
    // açılışında doğrudan yoldan (beforePingText) ve GPN bağlantısından sonra
    // tünel yolundan (afterPingText) ölçülür; ikisi de varsa fark (delta) alt
    // satırda gösterilir. Büyük değer gerçek ping'i tercih eder (düğüm ping'i
    // değil) — önce yoksa sonra, o da yoksa eski düğüm gecikmesi kalır.
    const realBefore = (item.beforePingText && item.beforePingText !== '—') ? item.beforePingText : null;
    const realAfter = (item.afterPingText && item.afterPingText !== '—') ? item.afterPingText : null;
    const primaryLatency = realAfter || realBefore || item.latencyText || '—';
    const primaryMs = realAfter ? item.afterPingMs : (realBefore ? item.beforePingMs : item.latencyMs);
    const realLine = realPingMarkup(item);
    const hasPing = realLine !== '' || primaryLatency !== '—';
    const routeTag = tagForRoute(eff);
    return `<article class="boost-card relative group w-[210px] min-w-[210px] shrink-0 flex flex-col items-center pt-2 pb-1.5 px-2 min-w-0 ${isRunning ? '' : 'opacity-45'}" data-boost-pname="${escHtml(pname)}">
      <button type="button" data-remove-boost-pname="${escHtml(pname)}" aria-label="${escHtml(t('boost.removeCard'))}" title="${escHtml(t('boost.removeCard'))}" class="absolute top-0 right-0 w-5 h-5 rounded-md flex items-center justify-center text-slate-500/70 hover:text-red-300 transition-colors opacity-0 group-hover:opacity-100 focus:opacity-100"><svg class="w-3 h-3" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg></button>
      <!-- Gaming-style frameless card: the icon is the hero (neon glow spotlight,
           no box) AND the quick-settings trigger — clicking it opens the per-app
           panel (route pills, WARP egress node, remove); every text stays a
           micro detail around it. -->
      <button type="button" data-quick-settings-pname="${escHtml(pname)}" aria-label="${escHtml(t('boost.quickSettings'))}" title="${escHtml(t('boost.quickSettingsTip'))}" class="app-icon-btn" style="background:transparent;border:0;padding:0;cursor:pointer;border-radius:20px;">
        <div class="app-icon-glow ${isRunning ? 'app-icon-glow-alive' : ''}">${appIconMarkup(item.exePath, display, 'w-16 h-16')}</div>
      </button>
      <p class="text-[11px] font-semibold leading-tight text-slate-100 truncate w-full text-center mt-1.5" title="${escHtml(display)}">${escHtml(display)}</p>
      <p class="text-[8px] font-mono text-[#4B5563] truncate w-full text-center" title="${escHtml(pname)}">${escHtml(pname)}</p>
      <div class="flex items-center justify-center gap-1.5 min-w-0 mt-1">
        <span class="route-dot ${routeTag}"></span>
        <span class="text-[8.5px] uppercase tracking-[.16em] ${isRunning ? 'text-[#8A94A6]' : 'text-[#5B6472]'} truncate">${escHtml(statusText)}</span>
      </div>
      ${hasPing ? `<div class="min-w-0 flex flex-col items-center gap-1 mt-1.5">
        <span class="latency-ms text-[12px] font-mono font-semibold leading-none ${primaryLatency !== '—' ? (primaryMs < 80 ? 'text-emerald-300' : primaryMs < 180 ? 'text-amber-300' : 'text-red-300') : 'text-[#5B6472]'}">${escHtml(primaryLatency)}</span>
        ${realLine}
      </div>` : ''}
    </article>`;
  }

  // Change detection for the 2 s live snapshot poll: the host ticks every
  // 2 seconds, so a rebuild with identical data must be skipped — rebuilding
  // resets the rail's scroll position and churns DOM nodes for nothing. Only
  // the fields the cards display participate; icons arrive through setAppIcons
  // (which forces a rebuild) and a language switch re-renders via applyLanguage.
  let _lastBoostRailSignature = null;
  function boostRailSignature(apps) {
    return JSON.stringify(apps.map(a => [
      a.processName || a.value || '',
      a.displayName || '',
      a.exePath || '',
      a.action || '',
      a.isRunning === true,
      a.beforePingText || '',
      a.afterPingText || '',
      a.latencyMs ?? '',
      a.latencyText || '',
      a.pingDeltaText || ''
    ])) + '|' + (!!aogpn.app.getInvertManual()) + '|' + (aogpn.app.getActiveGpnServer() || '');
  }

  function renderDashboardBoostCards(force) {
    const container = document.getElementById('dashboardBoostCards');
    const badge = document.getElementById('boostSummaryBadge');
    if (!container) return;
    const apps = boostRailApps();
    const sig = boostRailSignature(apps);
    // Skip the rebuild when nothing the cards display changed. The empty state
    // always re-renders (it has no cards whose scroll could reset).
    if (!force && sig === _lastBoostRailSignature && container.querySelector('.boost-card')) {
      return;
    }
    _lastBoostRailSignature = sig;
    const boosted = apps.filter(isTunnelRoutedApp);
    const runningCount = boosted.filter(a => a.isRunning === true).length;
    if (badge) {
      badge.textContent = runningCount > 0
        ? t('boost.running', { n: runningCount })
        : (apps.length > 0 ? t('boost.defined', { n: apps.length }) : t('boost.none'));
    }
    if (apps.length === 0) {
      container.innerHTML = '<div class="glass-soft rounded-xl p-3.5 w-full flex items-center justify-center gap-3 min-w-0 opacity-60"><div class="min-w-0 text-center"><p class="text-sm text-[#8A94A6]">' + t('boost.noGames', { link: '<a href="#" data-view="boost" class="text-cyan-300 underline">' + t('nav.boost') + '</a>' }) + '</p></div></div>';
      updateBoostRailArrows();
      return;
    }
    // Preserve the rail's scroll position across a live-data rebuild, so a
    // latency tick never snaps a scrolled rail back to the start.
    const prevScrollLeft = container.scrollLeft;
    container.innerHTML = apps.map(boostCardMarkup).join('');
    container.scrollLeft = prevScrollLeft;
    updateBoostRailArrows();
    // Keep an open quick-settings popover in sync with the fresh snapshot (its
    // target card may have been re-rendered with new route/status data).
    refreshQuickSettings();
  }

  // ---------- Per-app quick settings popover ----------
  // Clicking a rail card's icon opens a small quick-settings panel for that app:
  // route pills (VPN / Direct / Block / WARP), the per-app WARP egress node
  // picker when the route is WARP, live status/ping, and a remove action — all
  // through the same set_app_route / remove_app contracts as the table.
  let _quickSettingsPname = null;

  function quickSettingsApp() {
    const pname = _quickSettingsPname;
    if (!pname) return null;
    return (aogpn.app.getMonitorSnapshot().apps || []).find(a =>
      String(a.processName || a.value).toLowerCase() === String(pname).toLowerCase()) || null;
  }

  function quickSettingsMarkup(item) {
    const pname = item.processName || item.value || '';
    const display = item.displayName || item.processName || item.value || 'APP';
    const isRunning = item.isRunning === true;
    const eff = effectiveRoute(item.action);
    const effLabel = routeLabels[eff] || String(eff || '').toUpperCase() || '—';
    const tunneled = isTunnelRoutedApp(item);
    const statusText = tunneled
      ? (isRunning ? t('boost.routeActive', { route: effLabel }) : t('boost.idleNoBoost'))
      : (eff === 'block' ? routeLabels.block : t('dir.rowExcluded'));
    const primaryLatency = (item.afterPingText && item.afterPingText !== '—') ? item.afterPingText
      : (item.beforePingText && item.beforePingText !== '—') ? item.beforePingText
      : item.latencyText || '—';
    const pill = (route) => {
      const on = eff === route;
      return `<button type="button" data-quick-route="${route}" class="flex-1 min-w-0 rounded-lg px-2 py-1.5 text-[10px] font-semibold transition-colors ${on ? 'route-pill-on route-pill-' + tagForRoute(route) : 'text-[#8A94A6] bg-white/5 hover:bg-white/10'}">${escHtml(routeLabels[route] || route)}</button>`;
    };
    // Per-app WARP egress node picker — shown only while the WARP route is set
    // (same warpNodeSelect the management table renders).
    const warpSel = eff === 'warp'
      ? `<div class="mt-2.5"><p class="text-[8px] uppercase tracking-[.16em] text-[#5B6472] mb-1">${escHtml(t('warp.nodePickerHint'))}</p>${warpNodeSelect(pname, display, item.warpNodeIndexId)}</div>`
      : '';
    return `<div class="flex items-start gap-2.5 min-w-0">
      ${appIconMarkup(item.exePath, display, 'w-9 h-9')}
      <div class="min-w-0 flex-1">
        <p class="text-[12px] font-semibold leading-tight text-slate-100 truncate" title="${escHtml(display)}">${escHtml(display)}</p>
        <p class="text-[9px] font-mono text-[#5B6472] truncate">${escHtml(pname)}</p>
      </div>
      <button type="button" id="appQuickSettingsClose" aria-label="${escHtml(t('undo.dismiss'))}" title="${escHtml(t('undo.dismiss'))}" class="shrink-0 w-5 h-5 rounded-md flex items-center justify-center text-slate-500/70 hover:text-slate-200 transition-colors">✕</button>
    </div>
    <div class="flex items-center gap-1.5 mt-2.5 min-w-0">
      <span class="route-dot ${tagForRoute(eff)}"></span>
      <span class="text-[9px] uppercase tracking-[.14em] ${isRunning ? 'text-[#8A94A6]' : 'text-[#5B6472]'} truncate">${escHtml(statusText)}</span>
      <span class="ml-auto shrink-0 text-[10px] font-mono ${primaryLatency !== '—' ? 'text-cyan-300' : 'text-[#5B6472]'}">${escHtml(primaryLatency)}</span>
    </div>
    <div class="mt-3">
      <p class="text-[8px] uppercase tracking-[.16em] text-[#5B6472] mb-1.5">${escHtml(t('boost.quickRoute'))}</p>
      <div class="flex gap-1.5">${['vpn', 'direct', 'block', 'warp'].map(pill).join('')}</div>
    </div>
    ${warpSel}
    <div class="mt-3 pt-2.5 border-t border-white/5 flex items-center justify-end">
      <button type="button" data-quick-remove="${escHtml(pname)}" class="px-2.5 py-1 rounded-md text-[10px] font-semibold text-red-300/80 hover:text-red-300 hover:bg-red-500/10 transition-colors">${escHtml(t('boost.view.del'))}</button>
    </div>`;
  }

  function positionQuickSettings() {
    const pop = $('appQuickSettings');
    if (!pop || !_quickSettingsPname) return;
    const icon = Array.from(document.querySelectorAll('[data-quick-settings-pname]'))
      .find(b => b.dataset.quickSettingsPname === _quickSettingsPname);
    if (!icon) return;
    const r = icon.getBoundingClientRect();
    const gap = 8;
    const est = pop.offsetHeight || 300;
    let top = r.bottom + gap;
    if (top + est > window.innerHeight - 8) top = Math.max(8, r.top - gap - est);
    const left = Math.min(Math.max(8, r.left + r.width / 2 - pop.offsetWidth / 2), window.innerWidth - pop.offsetWidth - 8);
    pop.style.top = top + 'px';
    pop.style.left = left + 'px';
  }

  function openQuickSettings(pname) {
    if (!pname) return;
    _quickSettingsPname = pname;
    refreshQuickSettings();
  }

  // Re-fills the popover from the CURRENT snapshot (open + every rail rebuild).
  function refreshQuickSettings() {
    const pop = $('appQuickSettings');
    if (!pop) return;
    const item = quickSettingsApp();
    if (!item) {
      closeQuickSettings();
      return;
    }
    pop.innerHTML = quickSettingsMarkup(item);
    pop.classList.remove('hidden');
    positionQuickSettings();
  }

  function closeQuickSettings() {
    _quickSettingsPname = null;
    const pop = $('appQuickSettings');
    if (pop) pop.classList.add('hidden');
  }

  // All routed application entries (not domain/IP rules) feed the rail.
  function boostRailApps() {
    return (aogpn.app.getMonitorSnapshot().apps || []).filter(item => !isRuleEntry(item));
  }

  // The rail scrolls by ~80% of its visible width per click.
  function boostRailScrollBy(dir) {
    const container = document.getElementById('dashboardBoostCards');
    if (!container) return;
    const step = Math.max(240, Math.round(container.clientWidth * 0.8));
    if (typeof container.scrollBy === 'function') {
      container.scrollBy({ left: dir * step, behavior: 'smooth' });
    } else {
      container.scrollLeft = Math.max(0, container.scrollLeft + dir * step);
    }
  }

  // Hide ‹ › while there is nothing to scroll; disable them at either edge.
  function updateBoostRailArrows() {
    const container = document.getElementById('dashboardBoostCards');
    const prev = document.getElementById('boostCardsPrevBtn');
    const next = document.getElementById('boostCardsNextBtn');
    if (!container) return;
    if (!container.querySelector('.boost-card')) {
      if (prev) { prev.classList.add('hidden'); prev.disabled = true; }
      if (next) { next.classList.add('hidden'); next.disabled = true; }
      return;
    }
    const overflow = container.scrollWidth > container.clientWidth + 4;
    const atStart = container.scrollLeft <= 2;
    const atEnd = container.scrollLeft + container.clientWidth >= container.scrollWidth - 2;
    if (prev) { prev.classList.toggle('hidden', !overflow); prev.disabled = !overflow || atStart; }
    if (next) { next.classList.toggle('hidden', !overflow); next.disabled = !overflow || atEnd; }
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

  // ---------- Quick app picker (Connection Center) ----------
  // The "＋ Uygulama Ekle" panel next to the Manage button offers three quick
  // sources for the app you want routed through GPN:
  //   1. Suggested apps — a curated list of popular games/launchers; added by
  //      name (pid 0), so the host's KnownAppCatalog picks the recommended
  //      route automatically (VPN for games, WARP for the BSG launcher).
  //   2. Recently added — apps the user has routed through GPN before
  //      (persisted in localStorage, one-click re-add even when not running).
  //   3. Running processes — the SAME list_running_processes → updateProcessList
  //      → add_running_process contract the Game Boost picker uses.
  // A "pick an EXE from disk" button covers anything not running.
  const QUICK_SUGGESTED_APPS = [
    { processName: 'cs2.exe', displayName: 'Counter-Strike 2' },
    { processName: 'Valorant.exe', displayName: 'Valorant' },
    { processName: 'League of Legends.exe', displayName: 'League of Legends' },
    { processName: 'EscapeFromTarkov.exe', displayName: 'Escape from Tarkov' },
    { processName: 'dota2.exe', displayName: 'Dota 2' },
    { processName: 'FortniteClient-Win64-Shipping.exe', displayName: 'Fortnite' },
    { processName: 'r5apex.exe', displayName: 'Apex Legends' },
    { processName: 'Minecraft.exe', displayName: 'Minecraft' }
  ];
  const QUICK_ADD_RECENT_KEY = 'aogpn.quickAddRecent.v1';

  function recentQuickApps() {
    try {
      const list = JSON.parse(localStorage.getItem(QUICK_ADD_RECENT_KEY) || '[]');
      return Array.isArray(list) ? list : [];
    } catch (e) {
      return [];
    }
  }

  function rememberQuickApp(processName, displayName, exePath) {
    if (!processName) return;
    const name = String(processName);
    const list = recentQuickApps()
      .filter(item => String(item.p || '').toLowerCase() !== name.toLowerCase());
    list.unshift({ p: name, d: displayName || '', e: exePath || '', t: Date.now() });
    try { localStorage.setItem(QUICK_ADD_RECENT_KEY, JSON.stringify(list.slice(0, 20))); } catch (e) { /* storage unavailable */ }
  }

  // Snapshot-driven history: apps that appear in the routing list are recorded
  // as "recently added" (deduped by process name). The FIRST snapshot only
  // seeds the known set, so a pre-existing list never floods the history.
  let _quickRecentSeeded = false;
  let _quickRecentSnapshotNames = new Set();
  function recordRecentFromSnapshot(apps) {
    const entries = (Array.isArray(apps) ? apps : []).filter(a => !isRuleEntry(a) && a.processName);
    const names = new Set(entries.map(a => String(a.processName).toLowerCase()));
    if (!_quickRecentSeeded) {
      _quickRecentSeeded = true;
      _quickRecentSnapshotNames = names;
      return;
    }
    entries.forEach(a => {
      if (!_quickRecentSnapshotNames.has(String(a.processName).toLowerCase())) {
        rememberQuickApp(a.processName, a.displayName, a.exePath);
      }
    });
    _quickRecentSnapshotNames = names;
  }

  function quickAddSectionHead(label) {
    return '<p class="text-[9px] uppercase tracking-[.16em] text-[#8A94A6] px-2 pt-2.5 pb-1">' + escHtml(label) + '</p>';
  }

  function quickAddDiskButton() {
    return '<button type="button" id="dashQuickAddDiskBtn" class="w-full flex items-center justify-center gap-1.5 text-[10px] font-semibold px-3 py-2 rounded-lg bg-white/5 text-slate-200 border border-white/10 hover:bg-white/10 transition-colors" data-i18n="boost.addAppDisk">' + t('boost.addAppDisk') + '</button>';
  }

  function quickAddRunningRow(item) {
    const label = item.displayName || item.processName || 'Application';
    const path = item.exePath || item.processName || '';
    return `<button type="button" data-process-pid="${escHtml(String(item.pid))}" data-process-name="${escHtml(item.processName || '')}" data-display-name="${escHtml(item.displayName || '')}" class="w-full flex items-center gap-2.5 py-2 text-left hover:bg-white/5 rounded-md px-2 transition-colors">
      ${appIconMarkup(item.exePath, 'EXE', 'w-7 h-7')}
      <span class="min-w-0"><span class="block text-[11px] font-semibold text-slate-100 truncate">${escHtml(label)}</span><span class="block text-[10px] text-[#64748B] truncate">${escHtml(path)} · PID ${escHtml(String(item.pid))}</span></span>
      <span class="ml-auto shrink-0 text-[9px] font-semibold px-1.5 py-0.5 rounded-full bg-cyan-400/10 text-cyan-300 border border-cyan-400/20">${escHtml(t('boost.addRow'))}</span>
    </button>`;
  }

  function quickAddRecentRow(item) {
    return `<button type="button" data-recent-pname="${escHtml(item.p || '')}" data-display-name="${escHtml(item.d || '')}" class="w-full flex items-center gap-2.5 py-2 text-left hover:bg-white/5 rounded-md px-2 transition-colors">
      ${appIconMarkup(item.e || '', item.d || item.p || 'APP', 'w-7 h-7')}
      <span class="min-w-0"><span class="block text-[11px] font-semibold text-slate-100 truncate">${escHtml(item.d || item.p)}</span><span class="block text-[10px] text-[#64748B] truncate">${escHtml(item.p || '')}</span></span>
      <span class="ml-auto shrink-0 text-[9px] font-semibold px-1.5 py-0.5 rounded-full bg-cyan-400/10 text-cyan-300 border border-cyan-400/20">${escHtml(t('boost.addRow'))}</span>
    </button>`;
  }

  function renderQuickAddList() {
    const root = $('dashQuickAddList');
    if (!root) return;
    const filter = ($('dashQuickAddFilter')?.value || '').trim().toLowerCase();
    const running = aogpn.app.getProcessCatalog().filter(item => {
      const haystack = [item.displayName, item.processName, item.exePath].join(' ').toLowerCase();
      return !filter || haystack.includes(filter);
    });
    const diskBtn = quickAddDiskButton();
    // An active search narrows ONLY the running list — suggestions/recent
    // would only add noise while typing.
    if (filter) {
      root.innerHTML = (running.length === 0
        ? '<p class="text-[11px] text-[#64748B] py-2 px-2">' + escHtml(t('boost.addAppEmpty')) + '</p>'
        : running.map(quickAddRunningRow).join('')) + diskBtn;
      return;
    }
    // Apps already in the routing list are hidden from both quick sections —
    // they are already managed (rail / management view).
    const currentNames = new Set((aogpn.app.getMonitorSnapshot().apps || [])
      .filter(a => !isRuleEntry(a) && a.processName)
      .map(a => String(a.processName).toLowerCase()));
    const suggestions = QUICK_SUGGESTED_APPS
      .filter(s => !currentNames.has(String(s.processName).toLowerCase()));
    const recent = recentQuickApps()
      .filter(item => !currentNames.has(String(item.p || '').toLowerCase()))
      .slice(0, 6);
    let html = '';
    if (suggestions.length > 0) {
      html += quickAddSectionHead(t('boost.suggested'))
        + '<div class="grid grid-cols-2 gap-1.5 px-2 pb-1">' + suggestions.map(s => `
          <button type="button" data-quick-suggest="${escHtml(s.processName)}" data-display-name="${escHtml(s.displayName)}" class="flex items-center gap-2 min-w-0 rounded-lg px-2 py-1.5 text-left hover:bg-white/5 border border-white/5 transition-colors">
            ${appIconMarkup('', s.displayName, 'w-6 h-6')}
            <span class="text-[10.5px] font-semibold text-slate-200 truncate">${escHtml(s.displayName)}</span>
            <span class="ml-auto text-[11px] text-cyan-300 shrink-0">＋</span>
          </button>`).join('') + '</div>';
    }
    if (recent.length > 0) {
      html += quickAddSectionHead(t('boost.recent')) + recent.map(quickAddRecentRow).join('');
    }
    html += quickAddSectionHead(t('boost.runningApps'));
    html += running.length > 0
      ? running.map(quickAddRunningRow).join('')
      : '<p class="text-[11px] text-[#64748B] py-2 px-2">' + escHtml(t('boost.addAppEmpty')) + '</p>';
    root.innerHTML = html + diskBtn;
  }

  // Add by process name (pid 0): the host's KnownAppCatalog suggests the
  // route, so this works for games that are NOT running right now.
  function quickAddByName(processName, displayName) {
    if (!processName) return;
    postToHost({
      action: 'add_running_process',
      pid: 0,
      processName,
      displayName: displayName || ''
    });
    rememberQuickApp(processName, displayName, '');
    setQuickAddOpen(false);
    const filter = $('dashQuickAddFilter');
    if (filter) filter.value = '';
  }

  function setQuickAddOpen(open) {
    const panel = $('dashQuickAddPanel');
    const button = $('dashAddAppBtn');
    if (!panel || !button) return;
    panel.classList.toggle('hidden', !open);
    button.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (open) {
      postToHost({ action: 'list_running_processes' });
      const filter = $('dashQuickAddFilter');
      if (filter) {
        filter.placeholder = t('boost.addAppFilter');
        filter.value = '';
        setTimeout(() => filter.focus(), 0);
      }
      renderQuickAddList();
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
      ? `${apps.length} ${t('boost.of')} ${all.length} ${t('boost.entries')}`
      : `${apps.length} ${t('boost.entries')}`;
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
    const flatRows = apps.map(item => {
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
      // Rule rows (domain/IP) must carry their type + value so the delete button
      // can address them exactly (remove_route) instead of the app-only remove_app
      // which silently ignored them.
      const removeEntryType = entryType;
      const removeEntryValue = entryValue;
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
        <td class="px-3 py-2.5 whitespace-nowrap">${reorderCells}<button data-remove-process="${escHtml(processName)}" data-remove-entry-type="${escHtml(removeEntryType)}" data-remove-entry-value="${escHtml(removeEntryValue)}" class="delete-app-btn w-6 h-6 rounded-md flex items-center justify-center text-red-400/60 hover:text-red-300 hover:bg-red-500/10 transition-colors" title="Remove ${escHtml(item.displayName || processName)}"><svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24"><path d="M6 19a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V7H6v12ZM8 9h8v10H8V9Zm.5-5 1-1h5l1 1H19v2H5V4h3.5Z"/></svg></button></td>
      </tr>`;
    });
    const cols = 10;
    const forceOpen = filter !== '';
    const segments = partitionAppSegments(apps, appNameOf);
    const htmlByIdx = new Map(apps.map((it, i) => [it, flatRows[i]]));
    bodyEl.innerHTML = segments.map(seg => {
      if (seg.single) return htmlByIdx.get(seg.single);
      const open = forceOpen || expandedBoostGroups.has(seg.groupName);
      const members = seg.items.map(it => {
        const row = htmlByIdx.get(it);
        return open ? row : row.replace('class="hover:bg-white/[.035] transition-colors"', 'class="hover:bg-white/[.035] transition-colors hidden"');
      }).join('');
      // removable: routing list rows can be deleted; monitor connections cannot.
      return appGroupHeaderHtml(seg.groupName, seg.items, open, cols, true) + members;
    }).join('');
    bindRouteSelectors(bodyEl);
    bindDeleteButtons(bodyEl);
    bindMoveButtons(bodyEl);
    bindAppGroupToggles(bodyEl, expandedBoostGroups, renderSplitApps);
    // Group "remove all" (routing list only): one confirm removes every entry
    // under the group — apps via remove_app, domain/IP rules via remove_route.
    bodyEl.querySelectorAll('[data-app-group-remove]').forEach(btn => {
      btn.addEventListener('click', () => {
        const key = btn.dataset.appGroupRemove;
        const group = segments.find(s => s.groupName === key);
        if (!group) return;
        if (!confirm(t('boost.removeGroupConfirm', { n: group.items.length, name: key }))) return;
        group.items.forEach(item => {
          const etype = item.entryType || 'app';
          const value = item.value || item.processName || '';
          if (!value) return;
          if (etype === 'app') {
            postToHost({ action: 'remove_app', processName: value });
          } else {
            postToHost({ action: 'remove_route', entryType: etype, value });
          }
        });
      });
    });
    // Proxy yakalaması aktifken VPN rotalı ÇALIŞAN bir uygulama varsa oyunun UDP
    // trafiği tünele giremez (SOCKS yalnızca sistem proxy'sini kullanan TCP'yi
    // taşır) — oyun sunucu/ping trafiği doğrudan çıkar. Bu, "CS2'de Almanya
    // sunucusu görünmüyor / pingler çok yüksek" senaryosunun birincil nedenidir.
    const udpNotice = $('boostUdpNotice');
    if (udpNotice) {
      const proxyCapture = aogpn.app.getConnected()
        && aogpn.app.getTransport() === 'proxy'
        && apps.some(a => isTunnelRoutedApp(a) && a.isRunning === true);
      udpNotice.classList.toggle('hidden', !proxyCapture);
      const udpNoticeText = $('boostUdpNoticeText');
      if (udpNoticeText) udpNoticeText.textContent = t('boost.udpProxyNotice');
    }
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
        const entryType = btn.dataset.removeEntryType || 'app';
        const entryValue = btn.dataset.removeEntryValue || processName;
        if (!confirm(t('boost.removeConfirm', { name: processName }))) return;
        if (entryType === 'app') {
          postToHost({ action: 'remove_app', processName });
        } else {
          // Domain/IP kuralları: tür + değer ile (remove_route) — remove_app
          // yalnızca app satırlarını bulurdu ve kural silme sessizce yok sayılırdı.
          postToHost({ action: 'remove_route', entryType, value: entryValue });
        }
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
      off: t('boost.modeHintOff'),
      vpn: t('boost.modeHintVpn'),
      manual: t('boost.modeHintGpn')
    };
    if ($('boostModeHint')) $('boostModeHint').textContent = hints[aogpn.app.getSplitMode()] || hints.off;
    // Route mode is one shared concept: the Game Boost pills, the quick selector
    // and the Dashboard pills all drive the same backend routing mode.
    // While a connection transition is in flight the monitor snapshot still
    // carries the OLD routing mode (the host persists the new choice only when
    // the transition starts); syncing the mode pill here would flip the user's
    // just-clicked choice back ("sürekli GPN'e geri dönüyor"). Skip the MODE
    // sync during transitions — the split pills themselves stay live, and the
    // host settles the mode pill via setConnectionState once the transition ends.
    if (!aogpn.app.getTransitioning()
        && (aogpn.app.getSplitMode() === 'vpn' || aogpn.app.getSplitMode() === 'manual')) {
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
      // The independence notice is static markup with nested accent spans;
      // re-render its text from the dictionary so it follows the active
      // language (and survives a language switch) without clobbering icons.
      proxyNotice.innerHTML = '💡 ' + t('banner.tunProxy');
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
    // The Connection Center quick-add panel renders from the same catalog.
    renderQuickAddList();
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
    // table, the running-apps picker and the dashboard boost cards. The cards
    // are forced because icons are not part of their change signature.
    renderSplitApps();
    renderMonitorConnections();
    renderProcessCatalog();
    renderDashboardBoostCards(true);
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
    // Recently-added history for the quick-add panel: every NEW app entry in
    // the routing list is remembered (deduped, localStorage-persisted).
    recordRecentFromSnapshot(data.apps);
    // Collect the exe paths the tables are about to display and ask the host
    // for their shell icons once per new path.
    requestAppIcons([
      ...(aogpn.app.getMonitorSnapshot().connections || []).map(item => item.exePath),
      ...(aogpn.app.getMonitorSnapshot().apps || []).map(item => item.exePath),
      ...(aogpn.app.getMonitorSnapshot().traffic || []).map(item => item.exePath)
    ].filter(Boolean));
    // A snapshot arriving while a transition is in flight still carries the OLD
    // routing mode — overwriting splitMode would flip the Game Boost pills back
    // to the previous choice mid-switch ("sürekli GPN'e geri dönüyor"). The
    // host settles both pills once the transition finishes.
    if (!aogpn.app.getTransitioning()) {
      aogpn.app.setSplitMode(['off', 'vpn', 'manual'].includes(data.mode) ? data.mode : aogpn.app.getSplitMode());
    }
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
    $('monitorTrafficStatus').textContent = data.trafficStatus || t('monitor.noSample');
    $('monitorLiveBadge').textContent = data.connected ? 'Live · ' + String(data.mode || 'connected').toUpperCase() : 'Monitor ready · disconnected';
    $('monitorLiveBadge').className = 'text-[10px] uppercase tracking-[.16em] px-2.5 py-1 rounded-full border ' + (data.connected ? 'bg-emerald-400/10 text-emerald-300 border-emerald-400/20' : 'bg-cyan-400/10 text-cyan-300 border-cyan-400/20');
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
    renderQuickAddList, setQuickAddOpen,
    applySplitMode, applyDirection, updateGlobalPanel, updateTunProxyNotice,
    getRouteLabels: () => routeLabels,
    getUndoAvailable: () => window._undoSlot || null,
    // Live shell-icon cache (exe path -> data URI). The skinBridge getter in
    // app.js reads through here: appIconCache is module-scoped, so exposing it
    // through the module registry is what lets the skins render real icons.
    getAppIconCache: () => appIconCache
  };

  // ---- Connection Center app-rail + quick-add wiring (static DOM, once) ----
  (function initConnectionCenter() {
    const rail = document.getElementById('dashboardBoostCards');
    const prev = document.getElementById('boostCardsPrevBtn');
    const next = document.getElementById('boostCardsNextBtn');
    if (rail) {
      rail.addEventListener('scroll', () => updateBoostRailArrows(), { passive: true });
      // A vertical wheel over the rail scrolls it sideways (standard carousel
      // feel); trackpad horizontal swipes keep their native horizontal scroll.
      rail.addEventListener('wheel', (e) => {
        if (Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;
        if (rail.scrollWidth <= rail.clientWidth + 4) return;
        e.preventDefault();
        rail.scrollLeft += e.deltaY;
      }, { passive: false });
      // Drag-to-scroll (mouse): press and hold on the cards, then slide left or
      // right — the rail follows the pointer like a carousel. Trackpad swipes
      // and touch keep their native panning (they never enter this handler).
      // Presses that START on an interactive control (GPN switch, ✕, links) are
      // left untouched so clicks keep working; moves/ups are tracked on window
      // so a drag that leaves the rail keeps scrolling.
      let dragState = null;
      rail.addEventListener('pointerdown', (e) => {
        if (e.button !== 0 || e.pointerType !== 'mouse') return;
        const t = e.target;
        if (t && t.closest && t.closest('button, input, select, a, label')) return;
        dragState = { pointerId: e.pointerId, startX: e.clientX, startScroll: rail.scrollLeft, moved: false };
        e.preventDefault(); // no text/image selection while grabbing
        rail.classList.add('rail-dragging');
      });
      window.addEventListener('pointermove', (e) => {
        if (!dragState || (e.pointerId !== undefined && e.pointerId !== dragState.pointerId)) return;
        const dx = e.clientX - dragState.startX;
        if (!dragState.moved && Math.abs(dx) > 4) dragState.moved = true;
        if (dragState.moved) rail.scrollLeft = dragState.startScroll - dx;
      });
      const endRailDrag = (e) => {
        if (!dragState || (e.pointerId !== undefined && e.pointerId !== dragState.pointerId)) return;
        dragState = null;
        rail.classList.remove('rail-dragging');
      };
      window.addEventListener('pointerup', endRailDrag);
      window.addEventListener('pointercancel', endRailDrag);
      // Clicking a card's icon opens its quick-settings popover (route pills,
      // per-app WARP egress node, remove) — the cards stay pure status tiles.
      rail.addEventListener('click', (e) => {
        const iconBtn = e.target && e.target.closest ? e.target.closest('[data-quick-settings-pname]') : null;
        if (iconBtn && iconBtn.dataset.quickSettingsPname) {
          openQuickSettings(iconBtn.dataset.quickSettingsPname);
          return;
        }
        // Per-card remove (✕): same contract as the Game Boost table delete button
        // (remove_app → host deletes the entry from the routing list), gated behind
        // a confirmation dialog so a mis-click cannot drop an app silently.
        const btn = e.target && e.target.closest ? e.target.closest('[data-remove-boost-pname]') : null;
        if (!btn || !btn.dataset.removeBoostPname) return;
        const pname = btn.dataset.removeBoostPname;
        if (!confirm(t('boost.removeConfirm', { name: pname }))) return;
        postToHost({ action: 'remove_app', processName: pname });
      });
      // A rail scroll (arrows, wheel, drag) moves the cards under the popover —
      // close it so it never floats over the wrong app.
      rail.addEventListener('scroll', () => closeQuickSettings(), { passive: true });
    }

    // ---- Quick-settings popover (static DOM, delegated events) ----
    const qsPop = document.getElementById('appQuickSettings');
    if (qsPop) {
      qsPop.addEventListener('click', (e) => {
        if (e.target.closest && e.target.closest('#appQuickSettingsClose')) {
          closeQuickSettings();
          return;
        }
        const pill = e.target.closest ? e.target.closest('[data-quick-route]') : null;
        if (pill && pill.dataset.quickRoute) {
          const item = quickSettingsApp();
          if (!item) return;
          const pname = item.processName || item.value || '';
          postToHost({
            action: 'set_app_route',
            processName: pname,
            displayName: item.displayName || pname,
            route: pill.dataset.quickRoute
          });
          return;
        }
        const rm = e.target.closest ? e.target.closest('[data-quick-remove]') : null;
        if (rm) {
          if (!confirm(t('boost.removeConfirm', { name: rm.dataset.quickRemove || '' }))) return;
          postToHost({ action: 'remove_app', processName: rm.dataset.quickRemove || '' });
          closeQuickSettings();
        }
      });
      qsPop.addEventListener('change', (e) => {
        const sel = e.target.closest ? e.target.closest('[data-route-warp-node]') : null;
        if (!sel) return;
        const nodeId = sel.value || '';
        postToHost({
          action: 'set_app_route',
          processName: sel.dataset.routeWarpNode || '',
          displayName: sel.dataset.routeDisplay || sel.dataset.routeWarpNode || '',
          route: 'warp',
          warpNodeIndexId: nodeId,
          warpNodeName: nodeId ? warpNodeNameFor(nodeId) : ''
        });
      });
    }
    // Clicking anywhere else closes the popover (an icon click re-targets it).
    document.addEventListener('click', (e) => {
      const pop = document.getElementById('appQuickSettings');
      if (!pop || pop.classList.contains('hidden')) return;
      if (pop.contains(e.target)) return;
      if (e.target.closest && e.target.closest('[data-quick-settings-pname]')) return;
      closeQuickSettings();
    });
    if (prev) prev.addEventListener('click', () => boostRailScrollBy(-1));
    if (next) next.addEventListener('click', () => boostRailScrollBy(1));
    window.addEventListener('resize', () => updateBoostRailArrows());

    const addBtn = document.getElementById('dashAddAppBtn');
    const addPanel = document.getElementById('dashQuickAddPanel');
    if (addBtn && addPanel) {
      addBtn.addEventListener('click', () => setQuickAddOpen(addPanel.classList.contains('hidden')));
      document.getElementById('dashQuickAddClose')?.addEventListener('click', () => setQuickAddOpen(false));
      document.getElementById('dashQuickAddFilter')?.addEventListener('input', renderQuickAddList);
    }

    // Undo toast: after every app removal / route change / quick add the host
    // pushes window.setUndoAvailable({ kind, processName, displayName }) — one
    // single-level undo slot. "Geri Al" posts undo_last_app_op; the host restores
    // the previous state (re-adds the app, reverts the route, or removes the
    // quick add) and echoes a fresh authoritative snapshot.
    const undoToast = document.getElementById('undoToast');
    const undoToastText = document.getElementById('undoToastText');
    const undoToastBtn = document.getElementById('undoToastBtn');
    let undoToastTimer = null;
    // Single-level undo slot shared with the skins: the host pushes
    // setUndoAvailable({ kind, processName, displayName }) after every app
    // mutation; skins read it through skinBridge.undoAvailable and post
    // undo_last_app_op the same way the standard toast does.
    window._undoSlot = null;
    window.setUndoAvailable = (payload) => {
      window._undoSlot = (payload && typeof payload === 'object' && payload.kind) ? payload : null;
      // Skins mirror the toast state (and clear it on dismissal) without
      // touching the standard dashboard's DOM.
      try { aogpn.events.emit('skin-changed'); } catch (e) { /* ignore */ }
      if (!undoToast || !undoToastText) return;
      if (!payload || typeof payload !== 'object') {
        undoToast.classList.add('hidden');
        return;
      }
      const name = payload.displayName || payload.processName || '';
      undoToastText.textContent = payload.kind === 'remove' ? t('undo.remove', { name })
        : payload.kind === 'route' ? t('undo.route', { name })
        : payload.kind === 'add' ? t('undo.add', { name })
        : name;
      if (undoToastBtn) undoToastBtn.textContent = t('undo.action');
      undoToast.classList.remove('hidden');
      if (undoToastTimer) clearTimeout(undoToastTimer);
      // Auto-hide after 10 s; the undo slot stays valid host-side (a later
      // mutation or an explicit undo push replaces/consumes it).
      undoToastTimer = setTimeout(() => undoToast.classList.add('hidden'), 10000);
    };
    if (undoToastBtn) {
      undoToastBtn.addEventListener('click', () => {
        postToHost({ action: 'undo_last_app_op' });
        if (undoToast) undoToast.classList.add('hidden');
      });
    }
    document.getElementById('undoToastClose')?.addEventListener('click', () => {
      if (undoToast) undoToast.classList.add('hidden');
    });
    // The list re-renders on every host push, so row clicks are delegated.
    document.getElementById('dashQuickAddList')?.addEventListener('click', (e) => {
      if (e.target.closest && e.target.closest('#dashQuickAddDiskBtn')) {
        // Disk picker (host native file dialog) — covers apps that are not running.
        postToHost({ action: 'add_app' });
        setQuickAddOpen(false);
        return;
      }
      const suggest = e.target.closest ? e.target.closest('[data-quick-suggest]') : null;
      if (suggest) {
        quickAddByName(suggest.dataset.quickSuggest || '', suggest.dataset.displayName || '');
        return;
      }
      const recentBtn = e.target.closest ? e.target.closest('[data-recent-pname]') : null;
      if (recentBtn) {
        quickAddByName(recentBtn.dataset.recentPname || '', recentBtn.dataset.displayName || '');
        return;
      }
      const btn = e.target.closest ? e.target.closest('[data-process-pid]') : null;
      if (!btn) return;
      const pid = Number.parseInt(btn.dataset.processPid || '', 10);
      if (!Number.isInteger(pid) || pid <= 0) return;
      const processName = btn.dataset.processName || '';
      const displayName = btn.dataset.displayName || '';
      postToHost({ action: 'add_running_process', pid, processName, displayName });
      // Remember the pick for the "recently added" quick section (keeps the
      // real exe path so the icon can be resolved later).
      const catalogItem = (aogpn.app.getProcessCatalog() || []).find(item => item.pid === pid);
      rememberQuickApp(processName, displayName, (catalogItem && catalogItem.exePath) || '');
      setQuickAddOpen(false);
      const filter = document.getElementById('dashQuickAddFilter');
      if (filter) filter.value = '';
    });
    document.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      const qs = document.getElementById('appQuickSettings');
      if (qs && !qs.classList.contains('hidden')) {
        closeQuickSettings();
        return;
      }
      if (addPanel && !addPanel.classList.contains('hidden')) {
        setQuickAddOpen(false);
        if (addBtn) addBtn.focus();
      }
    });
  })();
})();
