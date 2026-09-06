/* ==========================================================================
   features/gpn.js — AoGPN dashboard feature module (GPN paneli)
   --------------------------------------------------------------------------
   Sunucu Yönetimi (import/edit/toggle/delete + canlı ölçüm), sunucu kümesi
   kartı, GPN diyagnoz akışı, WARP/WinDivert sağlık banner'ları, PID havuzu,
   WinDivert kuyruk + Wintun ayarları, yakalanan trafik, failover matrisi,
   en iyi aday tahmini, kurtarma/failover toggle'ları, failover telemetrisi,
   karar günlüğü ve GPN Bağlan rozeti. Host köprüsü window.setGpn* sözleşmesi
   bu modülde yaşar; app.js/skinBridge aogpn.gpn erişimcileriyle bağlanır.
   ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const escHtml = aogpn.util.escHtml;
  const $ = aogpn.util.$;
  const postToHost = aogpn.bridge.postToHost;
  let _gpnConnectStateLabel = '—';
  let _gpnConnectPending = false;
  function setGpnConnectState(label, pending = false) {
    _gpnConnectStateLabel = label;
    _gpnConnectPending = pending;
    const badge = document.getElementById('gpnConnectState');
    if (!badge) return;
    const btn = document.getElementById('gpnConnectBtn');
    badge.textContent = label;
    badge.className = 'ml-auto shrink-0 text-[10px] px-2.5 py-1 rounded-full border ' +
      (pending
        ? 'bg-amber-400/10 text-amber-300 border-amber-400/25'
        : 'bg-cyan-400/10 text-cyan-300 border-cyan-400/25');
    if (btn) btn.disabled = pending;
  }
  // Bağlan sırasında seçilen sunucu + gecikme + mod bilgisini telemetri paneline
  // ve oturum düğümüne canlı taşır (host ModeDecision olayından beslenir). Ping
  // kartı seçilen GPN sunucusunun ölçülen gecikmesini gösterir.

  // GPN diyagnoz akışı: host, DiagLog "GPN_*" satırlarını (GPN_LOG /
  // GPN_RECOVER / GPN_SELECT / GPN_FAILOVER / GPN_LAUNCH ...) canlı yayınlar.
  // Besleme son 50 satırı tutar; zaman damgası hosttan gelir.
  let gpnDiagCount = 0;
  // GPN diyagnoz beslemesinin son 50 satırı (skinBridge.gpnDiagLines) — ana
  // dashboard'daki setGpnDiag beslemesiyle aynı kaynaktan beslenir.
  let _gpnDiagLines = [];
  window.setGpnDiag = (evt) => {
    if (evt && evt.message) {
      _gpnDiagLines.push({ timestampMs: evt.timestampMs, kind: evt.kind || '', message: String(evt.message) });
      while (_gpnDiagLines.length > 50) _gpnDiagLines.shift();
    }
    const feed = document.getElementById('gpnDiagFeed');
    if (!feed) return;
    if (gpnDiagCount === 0) feed.innerHTML = '';
    const p = document.createElement('p');
    const time = evt && Number.isFinite(evt.timestampMs)
      ? '[' + new Date(evt.timestampMs).toLocaleTimeString() + '] '
      : '';
    const kind = evt && evt.kind ? '[' + evt.kind + '] ' : '';
    p.textContent = time + kind + (evt ? evt.message : '');
    p.className = 'truncate';
    p.title = p.textContent;
    feed.appendChild(p);
    gpnDiagCount++;
    while (feed.children.length > 50) {
      feed.removeChild(feed.firstChild);
      gpnDiagCount--;
    }
    feed.scrollTop = feed.scrollHeight;
    const countBadge = document.getElementById('gpnDiagCount');
    if (countBadge) countBadge.textContent = String(gpnDiagCount);
  };
  // Sunucu Yönetimi ekranı: host'un gpn_servers meta verisi (özel anahtar
  // asla renderer'a gelmez) + listeyi render eden köprü.
  let gpnServers = [];
  // Canlı ölçüm sonuçları (host ProbeGpnServersAsync → ICMP ping + UDP).
  const gpnServerProbes = new Map();
  let gpnProbeTimer = null;
  let gpnProbePending = false;
  window.setGpnServers = (items) => {
    gpnServers = Array.isArray(items) ? items : [];
    renderGpnServers();
    renderGpnCluster();
    aogpn.events.emit('skin-changed'); // refresh standalone skins (GPN Servers view)
  };
  window.setGpnServerProbes = (results) => {
    gpnServerProbes.clear();
    (Array.isArray(results) ? results : []).forEach(r => gpnServerProbes.set(r.serverId, r));
    renderGpnServers();
    renderGpnCluster();
    aogpn.events.emit('skin-changed'); // refresh standalone skins (GPN Servers view)
  };
  window.gpnServersNotify = (message) => {
    const notice = document.getElementById('gpnServerNotice');
    if (!notice) return;
    notice.textContent = message;
    notice.className = 'mb-3 rounded-xl border px-3.5 py-2.5 text-xs font-medium flex items-center gap-2.5 min-w-0 border-cyan-400/25 bg-cyan-400/5 text-cyan-200';
    clearTimeout(notice._t);
    notice._t = setTimeout(() => notice.classList.add('hidden'), 6000);
  };

  // Gömülü varsayılan şablonların durumu (WireGuardServerCatalog.GetDefaultsStatusAsync
  // → setGpnDefaultsStatus): her şablon için tohumlandı mı / anahtar var mı / güncel mi +
  // farklı alan adları. "Restore defaults" butonu gpn_defaults_restore ile şablon alanlarını
  // yeniden uygular (DPAPI anahtarı korunur).
  window.setGpnDefaultsStatus = (data) => {
    const body = document.getElementById('gpnDefaultsStatus');
    if (!body) return;

    const servers = (data && data.servers) ? data.servers : [];
    if (!servers.length) {
      body.innerHTML = '<p class="text-[#64748B]">' + t('gpn.servers.defaultsEmpty') + '</p>';
      return;
    }

    body.innerHTML = servers.map(s => {
      const addr = (s.endpointHost || '') + ':' + (s.endpointPort || '');
      let badge, cls;
      if (!s.seeded) {
        cls = 'bg-amber-400/10 text-amber-300 border-amber-400/25';
        badge = t('gpn.servers.defaultsNotSeeded');
      } else if (s.upToDate) {
        cls = 'bg-emerald-400/10 text-emerald-300 border-emerald-400/25';
        badge = t('gpn.servers.defaultsUpToDate');
      } else {
        cls = 'bg-red-400/10 text-red-300 border-red-400/25';
        badge = t('gpn.servers.defaultsOutdated');
      }
      const key = s.keyPresent ? '' : ' · <span class="text-[#64748B]">' + t('gpn.servers.defaultsNoKey') + '</span>';
      const diffs = (s.differences && s.differences.length)
        ? ' <span class="text-[#8A94A6]" title="' + escHtml(s.differences.join(', ')) + '">(' + escHtml(s.differences.join(', ')) + ')</span>'
        : '';
      return '<div class="flex items-center justify-between gap-2 py-0.5 border-b border-white/5 last:border-0">' +
        '<span class="truncate text-[#8A94A6]">' + escHtml(addr) + key + '</span>' +
        '<span class="flex items-center gap-1.5 shrink-0">' +
        diffs +
        '<span class="shrink-0 px-1.5 py-0.5 rounded-md border ' + cls + '">' + badge + '</span>' +
        '</span></div>';
    }).join('');
  };

  function renderGpnServers() {
    const list = document.getElementById('gpnServerList');
    const count = document.getElementById('gpnServerCount');
    if (!list) return;
    if (count) count.textContent = String(gpnServers.length);
    if (!gpnServers.length) {
      list.innerHTML = '<p class="text-xs text-[#64748B]">' + t('gpn.servers.empty') + '</p>';
      return;
    }
    list.innerHTML = gpnServers.map(s => {
      const addr = (s.endpointHost || '') + ':' + (s.endpointPort || '');
      const status = s.isEnabled
        ? '<span class="shrink-0 text-[10px] px-2 py-0.5 rounded-full bg-emerald-400/10 text-emerald-300 border border-emerald-400/20">' + t('gpn.servers.active') + '</span>'
        : '<span class="shrink-0 text-[10px] px-2 py-0.5 rounded-full bg-amber-400/10 text-amber-300 border border-amber-400/25">' + t('gpn.servers.disabled') + '</span>';
      // Canlı ölçüm rozeti: gecikme + kayıp + UDP yolu (host probe sonucundan).
      const probe = gpnServerProbes.get(s.serverId);
      let probeBadge = '';
      if (probe && s.isEnabled) {
        const delay = probe.isSuccess && Number.isFinite(probe.delayMs) && probe.delayMs >= 0
          ? Math.round(probe.delayMs) + ' ms'
          : '—';
        const loss = probe.isSuccess ? probe.lossPercent : null;
        const udp = probe.udpStatus || 'unknown';
        const udpColor = udp === 'Open' ? 'emerald'
          : (udp === 'Blocked' || udp === 'HandshakeNoResponse') ? 'red' : 'amber';
        const lossTxt = loss == null ? '' : (loss > 0 ? ' · %' + loss : '');
        probeBadge = '<span class="shrink-0 text-[10px] px-2 py-0.5 rounded-full bg-cyan-400/10 text-cyan-300 border border-cyan-400/25 font-mono" title="' + t('gpn.servers.probeTitle') + '">' +
          delay + lossTxt + ' · <span class="text-[' + udpColor + ']">' + udp + '</span></span>';
      }
      const keyState = s.keyProtected
        ? '<span class="text-[10px] text-[#5B6472]" title="' + t('gpn.servers.dpapiNote') + '">🔐</span>'
        : '';
      return '<div class="glass-soft rounded-xl p-3 flex items-center gap-3 min-w-0">' +
        '<div class="min-w-0 flex-1">' +
          '<div class="flex items-center gap-2 min-w-0">' +
            '<p class="text-sm font-semibold text-slate-100 truncate">' + escHtml(s.name || s.serverId) + '</p>' +
            keyState +
          '</div>' +
          '<p class="text-[11px] text-[#8A94A6] font-mono truncate mt-0.5">' + escHtml(addr) + '</p>' +
          '<p class="text-[10px] text-[#64748B] truncate mt-0.5">' + escHtml(s.clientAddress || '') + ' · MTU ' + (s.mtu || 1420) + ' · DNS ' + escHtml(s.dns || '1.1.1.1') + '</p>' +
        '</div>' +
        status +
        probeBadge +
        '<div class="flex items-center gap-1.5 shrink-0">' +
          '<button type="button" data-gpn-edit="' + escHtml(s.serverId) + '" class="text-[11px] font-semibold px-2 py-1 rounded-md bg-white/5 text-slate-200 border border-white/10 hover:bg-white/10 transition-colors">' + t('gpn.servers.edit') + '</button>' +
          '<button type="button" data-gpn-toggle="' + escHtml(s.serverId) + '" data-enabled="' + (s.isEnabled ? '0' : '1') + '" class="text-[11px] font-semibold px-2 py-1 rounded-md bg-white/5 text-slate-200 border border-white/10 hover:bg-white/10 transition-colors">' +
            (s.isEnabled ? t('gpn.servers.disable') : t('gpn.servers.enable')) +
          '</button>' +
          '<button type="button" data-gpn-delete="' + escHtml(s.serverId) + '" class="text-[11px] font-semibold px-2 py-1 rounded-md bg-red-500/10 text-red-300 border border-red-400/25 hover:bg-red-500/20 transition-colors">' + t('gpn.servers.delete') + '</button>' +
        '</div>' +
      '</div>';
    }).join('');
  }

  // GPN panel "sunucu kümesi" kartı — İtalya/Almanya canlı gecikmesi (host
  // ProbeGpnServersAsync → aynı gpnServerProbes verisi) + ölçüm döngüsü.
  let gpnClusterPending = false;
  let gpnClusterTimer = null;
  function renderGpnCluster() {
    const list = document.getElementById('gpnClusterList');
    if (!list) return;
    const active = gpnServers.filter(s => s.isEnabled);
    const ping = document.getElementById('gpnClusterPing');
    if (!active.length) {
      list.innerHTML = '<p class="text-[#64748B]">' + t('gpn.cluster.empty') + '</p>';
      if (ping) ping.textContent = '';
      return;
    }

    // İlk sunucu listesi geldiğinde ve GPN paneli görünürken canlı değerler için
    // tek seferlik ilk ölçümü tetikle (sonraki ölçümler 15 sn'lik döngüden gelir).
    if (!gpnClusterInitialProbed && document.getElementById('panelGPN')
        && !document.getElementById('panelGPN').classList.contains('hidden-panel')) {
      gpnClusterInitialProbed = true;
      requestGpnClusterProbe();
      // WinDivert kuyruk + Wintun adapter kartlarını doldur (config değerleri host'tan gelir).
      postToHost({ action: 'get_gpn_capture_settings' });
      postToHost({ action: 'get_gpn_wintun_settings' });
    }

    // En düşük gecikmeli + UDP'si açık aday → "best" vurgusu (WaveGuard seçimiyle aynı amaç).
    const rows = active.map((s, idx) => {
      const p = gpnServerProbes.get(s.serverId);
      const r = { id: s.serverId, name: s.name || s.serverId, addr: (s.endpointHost || '') + ':' + (s.endpointPort || ''), order: idx };
      if (!p) { r.delayMs = null; r.loss = null; r.udp = null; r.physicalNic = false; return r; }
      const ok = p.isSuccess && Number.isFinite(p.delayMs) && p.delayMs >= 0;
      r.delayMs = ok ? p.delayMs : null;
      r.loss = ok ? p.lossPercent : null;
      r.udp = p.udpStatus || (ok ? 'Open' : 'unknown');
      // Tünel aktifken ICMP atlanır ve ping (el sıkışma fallback'i) bazen zaman
      // aşımına düşebilir; UDP probe'u Open döndüyse onun RTT'sini gecikme olarak
      // göster — kart boş kalmaz, değer gerçek fiziksel yoldan ölçülmüştür.
      if (r.delayMs == null && p.udpStatus === 'Open'
          && Number.isFinite(p.udpRoundTripMs) && p.udpRoundTripMs >= 0) {
        r.delayMs = p.udpRoundTripMs;
        r.loss = 0; // kayıp oranı ping ölçümüne aittir — UDP RTT'siyle gösterilmez
      }
      // Host, tünel etkinken ölçümün fiziksel NIC üzerinden yapıldığını bildirir
      // (ICMP atlandı, gecikme UDP el sıkışma RTT'si) — başlıkta ipucu gösterilir.
      r.physicalNic = p.measuredOverPhysicalNic === true;
      return r;
    });

    // Tünel etkinken ölçüm fiziksel NIC üzerinden yapılır: kısa rozet + açıklayıcı
    // tooltip. Tünel yokken gizlidir (ölçüm normal ICMP yoludur).
    const egressHint = document.getElementById('gpnClusterEgressHint');
    if (egressHint) {
      if (rows.some(r => r.physicalNic)) {
        egressHint.textContent = t('gpn.cluster.egressHintLabel');
        egressHint.title = t('gpn.cluster.egressHint');
        egressHint.classList.remove('hidden');
      } else {
        egressHint.classList.add('hidden');
      }
    }
    const best = rows.filter(r => r.delayMs != null && r.udp === 'Open')
      .sort((a, b) => a.delayMs - b.delayMs)[0];

    list.innerHTML = rows.sort((a, b) => a.order - b.order).map(r => {
      const bestBadge = best && best.id === r.id
        ? '<span class="shrink-0 text-[9px] px-1.5 py-0.5 rounded-full bg-emerald-400/10 text-emerald-300 border border-emerald-400/25">' + t('gpn.cluster.best') + '</span>'
        : '';
      const delay = r.delayMs != null ? Math.round(r.delayMs) + ' ms' : '—';
      const delayCls = r.delayMs != null ? 'text-cyan-300 num-tabular' : 'text-[#5B6472]';
      const udpDot = r.udp === 'Open' ? 'bg-emerald-400'
        : (r.udp === 'Blocked' || r.udp === 'HandshakeNoResponse') ? 'bg-red-400' : 'bg-amber-400';
      const udpTxt = r.udp ? escHtml(r.udp) : '—';
      const loss = r.loss != null && r.loss > 0 ? ' · ' + Math.round(r.loss) + '%' : '';
      return '<div class="flex items-center gap-2.5 min-w-0 rounded-lg bg-white/[.03] border border-white/5 px-2.5 py-2">' +
        '<span class="w-2 h-2 rounded-full ' + udpDot + ' shrink-0"></span>' +
        '<div class="min-w-0 flex-1">' +
          '<div class="flex items-center gap-1.5 min-w-0">' +
            '<p class="text-xs font-semibold text-slate-100 truncate">' + escHtml(r.name) + '</p>' +
            bestBadge +
          '</div>' +
          '<p class="text-[9px] text-[#5B6472] font-mono truncate">' + escHtml(r.addr) + ' · ' + udpTxt + loss + '</p>' +
        '</div>' +
        '<span class="shrink-0 text-sm font-semibold num-tabular ' + delayCls + '">' + escHtml(delay) + '</span>' +
      '</div>';
    }).join('');

    if (ping) {
      const openCount = rows.filter(r => r.udp === 'Open').length;
      const bestMs = best ? Math.round(best.delayMs) : null;
      ping.textContent = openCount + ' ' + t('gpn.cluster.open') + (bestMs != null ? ' · ' + bestMs + ' ms' : '');
    }
  }

  let gpnClusterInitialProbed = false;
  function requestGpnClusterProbe() {
    if (gpnClusterPending) return;
    gpnClusterPending = true;
    postToHost({ action: 'gpn_cluster_probe' });
    setTimeout(() => { gpnClusterPending = false; }, 12000);
  }
  function scheduleGpnClusterTick() {
    const p = document.getElementById('panelGPN');
    // Sadece GPN paneli görünürken ölç — Sunucu Yönetimi görünümü açıkken
    // kendi 15 sn döngüsü zaten çalışır, burada tekrar ölçülmez. Sandbox'ta
    // setTimeout inert olduğundan bu döngü testte setInterval sayacını etkilemez.
    if (p && !p.classList.contains('hidden-panel') && !gpnProbePending) {
      requestGpnClusterProbe();
    }
    gpnClusterTimer = setTimeout(scheduleGpnClusterTick, 15000);
  }
  function startGpnClusterLoop() {
    stopGpnClusterLoop();
    gpnClusterTimer = setTimeout(scheduleGpnClusterTick, 15000);
  }
  function stopGpnClusterLoop() {
    if (gpnClusterTimer) { clearTimeout(gpnClusterTimer); gpnClusterTimer = null; }
    gpnClusterPending = false;
  }

  const gpnClusterProbeBtn = document.getElementById('gpnClusterProbe');
  if (gpnClusterProbeBtn) {
    gpnClusterProbeBtn.addEventListener('click', () => {
      gpnClusterPending = false;
      requestGpnClusterProbe();
    });
  }

  // WARP dial sağlığı — host (WarpDialHealthMonitor) sing-box log kuyruğunu
  // izler, warp outbound hatalarını (no route to host vb.) sayar ve faulted
  // olunca bu banner'ı gösterir. Veri yalnızca faulted bayrağı/hata sayısı/
  // son hata metnidir; ağ adresi veya anahtar içermez.
  window.setWarpHealth = (health) => {
    const banner = document.getElementById('warpHealthBanner');
    const errbox = document.getElementById('warpHealthError');
    const count = document.getElementById('warpHealthCount');
    const text = document.getElementById('warpHealthText');
    if (!banner || !text) return;
    const faulted = !!(health && health.faulted);
    const errorCount = (health && typeof health.errorCount === 'number') ? health.errorCount : 0;
    const latestError = (health && health.latestError) || '';
    // Degrade (GpnBypassEgressController): WARP faulted iken launcher/BSG egress'i
    // canlı (restart'sız) DIRECT'e çekildi — rozet bunu söyler, banner metni
    // degrade açıklamasına döner.
    const degraded = !!(health && health.degraded);
    const badge = document.getElementById('warpHealthDegraded');
    if (faulted) {
      text.textContent = degraded ? t('warp.degradedBanner') : t('warp.healthBanner');
      if (count) {
        count.textContent = errorCount > 0 ? String(errorCount) : '';
        count.classList.toggle('hidden', errorCount <= 0);
      }
      if (errbox) {
        errbox.textContent = latestError;
        errbox.classList.toggle('hidden', !latestError);
      }
      if (badge) {
        badge.textContent = t('warp.degradedTag');
        badge.classList.toggle('hidden', !degraded);
      }
      banner.classList.remove('hidden');
    } else {
      banner.classList.add('hidden');
      if (badge) badge.classList.add('hidden');
      if (errbox) errbox.classList.add('hidden');
    }
  };

  // WinDivert yakalama köprüsü sağlığı — host (WinDivertHealthMonitor) DLL/sürücü
  // dosyalarını ve sürücüyü kontrol eder (cihaz probe + SCM kurulumu); eksikse
  // bu banner'ı gösterir. Mesaj host'tan gelir (yerelleştirilmiş diyagnostik);
  // ağ adresi veya anahtar içermez — yalnızca durum adı + hata kodu + metin.
  window.setWinDivertHealth = (health) => {
    const banner = document.getElementById('winDivertHealthBanner');
    const text = document.getElementById('winDivertHealthText');
    const errbox = document.getElementById('winDivertHealthError');
    if (!banner || !text) return;
    const state = (health && health.state) || 'Unknown';
    const message = (health && health.message) || '';
    if (state === 'Ready' || state === 'Unknown') {
      banner.classList.add('hidden');
      if (errbox) errbox.classList.add('hidden');
      return;
    }
    text.textContent = t('gpn.capture.winDivertBanner');
    if (errbox) {
      errbox.textContent = message;
      errbox.classList.toggle('hidden', !message);
    }
    banner.classList.remove('hidden');
  };

  // PID havuzu kartı — SplitTunnelViewModel vpn oyunlarından GpnTargetResolver'
  // ın canlı PID seti (host GpnTargetResolverBridge → setGpnPidPool).
  // Son PID havuzu anlık görüntüsü (skinBridge.gpnPidPool).
  let _gpnPidPool = null;
  window.setGpnPidPool = (data) => {
    _gpnPidPool = data || null;
    const targets = document.getElementById('gpnPidPoolTargets');
    const list = document.getElementById('gpnPidPoolList');
    const state = document.getElementById('gpnPidPoolState');
    if (!targets || !list || !state) return;
    const names = (data && Array.isArray(data.targetNames)) ? data.targetNames : [];
    const pids = (data && Array.isArray(data.pids)) ? data.pids : [];
    const watching = !!(data && data.watching);
    const running = !!(data && data.targetRunning);
    const sourceStatus = (data && data.sourceStatus) || 'Healthy';

    if (!names.length) {
      targets.innerHTML = '';
      list.innerHTML = '<p class="text-[#64748B]">' + t('gpn.pidpool.empty') + '</p>';
    } else {
      targets.innerHTML = names.map(n =>
        '<span class="shrink-0 text-[9px] px-2 py-0.5 rounded-full bg-violet-400/10 text-violet-300 border border-violet-400/25 font-mono">' + escHtml(n) + '</span>'
      ).join('');
      if (pids.length) {
        list.innerHTML = '<span class="text-cyan-300 num-tabular">' + pids.join(' · ') + '</span>';
      } else {
        list.innerHTML = '<p class="text-[#64748B]">' + t('gpn.pidpool.offline') + '</p>';
      }
    }

    if (watching) {
      // FATAL = süreç ağacına erişilemedi (Toolhelp32 + yedek başarısız) — karar güvenilmez.
      if (sourceStatus === 'Fatal') {
        state.textContent = t('gpn.pidpool.fatal') + ' · ' + t('gpn.pidpool.offlineShort');
        state.className = 'shrink-0 text-[10px] px-2 py-0.5 rounded-full bg-red-400/10 text-red-300 border border-red-400/25 font-mono';
      } else if (sourceStatus === 'Degraded') {
        state.textContent = t('gpn.pidpool.degraded') + (running ? ' · ' + pids.length + ' ' + t('gpn.pidpool.pids') : ' · ' + t('gpn.pidpool.offlineShort'));
        state.className = 'shrink-0 text-[10px] px-2 py-0.5 rounded-full bg-amber-400/10 text-amber-300 border border-amber-400/25 font-mono';
      } else {
        state.textContent = t('gpn.pidpool.watching') + (running ? ' · ' + pids.length + ' ' + t('gpn.pidpool.pids') : ' · ' + t('gpn.pidpool.offlineShort'));
        state.className = 'shrink-0 text-[10px] px-2 py-0.5 rounded-full bg-emerald-400/10 text-emerald-300 border border-emerald-400/25 font-mono';
      }
    } else {
      state.textContent = t('gpn.pidpool.idle');
      state.className = 'shrink-0 text-[10px] px-2 py-0.5 rounded-full bg-white/5 text-[#64748B] border border-white/10 font-mono';
    }
  };

  // Yakalanan trafik kartı — GpnCaptureLoop'un paket başına telemetrisi
  // (AppEvents.GpnCaptureStatsChanged → setGpnCaptureStats). Per-PID satırları
  // UDP port→PID köprüsüyle atfedilir; havuz dışı (kaçan) trafik kırmızı rozetle
  // işaretlenir — PID havuzu küçükken hangi süreçlerin trafiği yakalanmıyor görülür.
  function fmtCount(n) {
    if (!n) return '0';
    if (n >= 1000000) return (n / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
    if (n >= 1000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
    return String(n);
  }
  function fmtBytes(n) {
    if (!n) return '0 B';
    if (n >= 1048576) return (n / 1048576).toFixed(1).replace(/\.0$/, '') + ' MB';
    if (n >= 1024) return (n / 1024).toFixed(1).replace(/\.0$/, '') + ' KB';
    return n + ' B';
  }
  // WinDivert kuyruk ayarları (GpnCaptureItem → setGpnCaptureSettings): kartı
  // mevcut config değerleriyle doldurur. Apply butonu set_gpn_capture_settings ile
  // config'e yazar — değerler bir sonraki yakalama başlangıcında uygulanır.
  // Son WinDivert kuyruk ayarları (skinBridge.gpnCaptureSettings).
  let _gpnCaptureSettings = null;
  window.setGpnCaptureSettings = (settings) => {
    _gpnCaptureSettings = settings || null;
    const qLen = document.getElementById('gpnCaptureSetQueueLen');
    const qTime = document.getElementById('gpnCaptureSetQueueTime');
    const qSize = document.getElementById('gpnCaptureSetQueueSize');
    const eLen = document.getElementById('gpnCaptureSetEnableLen');
    const eTime = document.getElementById('gpnCaptureSetEnableTime');
    const eSize = document.getElementById('gpnCaptureSetEnableSize');
    const layer = document.getElementById('gpnCaptureSetLayer');
    const direction = document.getElementById('gpnCaptureSetDirection');
    const slot = document.getElementById('gpnCaptureSetSlot');
    if (!qLen || !qTime || !qSize || !eLen || !eTime || !eSize || !layer || !direction || !slot) return;
    if (!settings) return;

    qLen.value = settings.queueLen == null ? '' : settings.queueLen;
    qTime.value = settings.queueTime == null ? '' : settings.queueTime;
    qSize.value = settings.queueSize == null ? '' : settings.queueSize;
    eLen.checked = settings.enableQueueLen === true;
    eTime.checked = settings.enableQueueTime === true;
    eSize.checked = settings.enableQueueSize === true;
    layer.value = String(settings.layer == null ? 0 : settings.layer);
    direction.value = String(settings.direction == null ? 1 : settings.direction);
    const layerLabel = layer.selectedOptions[0] ? layer.selectedOptions[0].textContent : String(layer.value);
    const dirLabel = direction.selectedOptions[0] ? direction.selectedOptions[0].textContent : String(direction.value);
    slot.textContent = layerLabel + ' · ' + dirLabel;
  };

  // Apply: karttaki geçerli değerleri host'a gönder (config'e yazılır).
  function bindGpnCaptureSetControls() {
    const save = document.getElementById('gpnCaptureSetSave');
    if (!save) return;
    save.addEventListener('click', () => {
      const readUint = (id, min) => {
        const el = document.getElementById(id);
        const v = el ? parseInt(el.value, 10) : NaN;
        return Number.isFinite(v) ? Math.max(min, v) : null;
      };
      postToHost({
        action: 'set_gpn_capture_settings',
        queueLen: readUint('gpnCaptureSetQueueLen', 1),
        queueTime: readUint('gpnCaptureSetQueueTime', 1),
        queueSize: readUint('gpnCaptureSetQueueSize', 0),
        enableQueueLen: document.getElementById('gpnCaptureSetEnableLen').checked,
        enableQueueTime: document.getElementById('gpnCaptureSetEnableTime').checked,
        enableQueueSize: document.getElementById('gpnCaptureSetEnableSize').checked,
        layer: parseInt(document.getElementById('gpnCaptureSetLayer').value, 10),
        direction: parseInt(document.getElementById('gpnCaptureSetDirection').value, 10)
      });
    });
  }
  bindGpnCaptureSetControls();

  // Wintun adapter ayarları (GpnWintunItem → setGpnWintunSettings): kartı mevcut
  // config değerleriyle doldurur. Apply butonu set_gpn_wintun_settings ile config'e
  // yazar — değerler bir sonraki WireGuard bağlantısında (köprü açılışı) uygulanır.
  // Son Wintun adapter ayarları (skinBridge.gpnWintunSettings).
  let _gpnWintunSettings = null;
  window.setGpnWintunSettings = (settings) => {
    _gpnWintunSettings = settings || null;
    const name = document.getElementById('gpnWintunSetAdapterName');
    const ring = document.getElementById('gpnWintunSetRingCapacity');
    const slot = document.getElementById('gpnWintunSetSlot');
    if (!name || !ring || !slot) return;
    if (!settings) return;

    name.value = settings.adapterName || 'AoGPN';
    ring.value = settings.ringCapacity == null ? '' : settings.ringCapacity;
    slot.textContent = settings.adapterName || 'AoGPN';
  };

  // Apply: karttaki geçerli değerleri host'a gönder (config'e yazılır).
  function bindGpnWintunSetControls() {
    const save = document.getElementById('gpnWintunSetSave');
    if (!save) return;
    save.addEventListener('click', () => {
      const nameEl = document.getElementById('gpnWintunSetAdapterName');
      const ringEl = document.getElementById('gpnWintunSetRingCapacity');
      const ringValue = ringEl ? parseInt(ringEl.value, 10) : NaN;
      postToHost({
        action: 'set_gpn_wintun_settings',
        adapterName: nameEl ? (nameEl.value.trim() || null) : null,
        ringCapacity: Number.isFinite(ringValue) ? Math.max(131072, ringValue) : null
      });
    });
  }
  bindGpnWintunSetControls();

  // Son yakalanan trafik anlık görüntüsü (skinBridge.gpnCaptureStats).
  let _gpnCaptureStats = null;
  window.setGpnCaptureStats = (data) => {
    _gpnCaptureStats = data || null;
    const total = document.getElementById('gpnCaptureTotal');
    const agg = document.getElementById('gpnCaptureAgg');
    const flows = document.getElementById('gpnCaptureFlows');
    const pids = document.getElementById('gpnCapturePids');
    if (!total || !agg || !flows || !pids) return;

    if (!data || !data.totalPackets) {
      total.textContent = '0';
      agg.innerHTML = '<p class="text-[#64748B]">' + t('gpn.capture.empty') + '</p>';
      flows.innerHTML = '';
      pids.innerHTML = '';
      return;
    }

    total.textContent = fmtCount(data.totalPackets);
    const chips = [
      '<span class="px-2 py-0.5 rounded-md bg-cyan-400/10 text-cyan-300 border border-cyan-400/20">' + fmtCount(data.udpPackets || 0) + ' UDP</span>',
    ];
    if (data.tcpPackets) {
      chips.push('<span class="px-2 py-0.5 rounded-md bg-white/5 text-slate-300 border border-white/10">' + fmtCount(data.tcpPackets) + ' TCP</span>');
    }
    if (data.icmpPackets) {
      chips.push('<span class="px-2 py-0.5 rounded-md bg-white/5 text-slate-300 border border-white/10">' + fmtCount(data.icmpPackets) + ' ICMP</span>');
    }
    chips.push('<span class="px-2 py-0.5 rounded-md bg-white/5 text-slate-300 border border-white/10">' + fmtBytes(data.totalBytes || 0) + '</span>');
    chips.push('<span class="px-2 py-0.5 rounded-md bg-white/5 text-slate-300 border border-white/10">' + t('gpn.capture.out') + ' ' + fmtCount(data.outboundPackets || 0) + '</span>');
    chips.push('<span class="px-2 py-0.5 rounded-md bg-white/5 text-slate-300 border border-white/10">' + t('gpn.capture.in') + ' ' + fmtCount(data.inboundPackets || 0) + '</span>');
    agg.innerHTML = chips.join('');

    flows.innerHTML = (data.topFlows && data.topFlows.length)
      ? data.topFlows.map(f =>
        '<div class="flex items-center justify-between gap-2"><span class="truncate">' + escHtml(f.peerEndpoint) + ' <span class="text-[#64748B]">' + escHtml(f.protocol) + '</span></span><span class="shrink-0 text-cyan-300 num-tabular">' + fmtCount(f.packets) + '</span></div>'
      ).join('')
      : '';

    pids.innerHTML = (data.byPid && data.byPid.length)
      ? data.byPid.map(p => {
        const label = p.pid ? String(p.pid) : t('gpn.capture.unknown');
        const cls = p.inPool
          ? 'bg-emerald-400/10 text-emerald-300 border-emerald-400/25'
          : 'bg-red-400/10 text-red-300 border-red-400/25';
        return '<div class="flex items-center justify-between gap-2"><span class="shrink-0 px-1.5 py-0.5 rounded-md border font-mono ' + cls + '">' + escHtml(label) + (p.inPool ? '' : ' ⚠') + '</span><span class="shrink-0 text-cyan-300 num-tabular">' + fmtCount(p.packets) + '</span></div>';
      }).join('')
      : '';
  };

  // Failover matrisi — aynı ölçümün varsayılan + katı politika altındaki kararı
  // (GpnServerSelectionService.EvaluateFailoverMatrix → setGpnFailoverMatrix).
  // Dashboard yalnızca görüntüler; karar mantığı C# tarafında tek kaynaktır.
  // Son failover matrisi (skinBridge.gpnFailoverMatrix).
  let _gpnFailoverMatrix = null;
  window.setGpnFailoverMatrix = (data) => {
    _gpnFailoverMatrix = data || null;
    const body = document.getElementById('gpnMatrixBody');
    const ts = document.getElementById('gpnMatrixTs');
    if (!body || !ts) return;

    if (!data || !data.rows || !data.rows.length || !data.defaultPolicy) {
      ts.textContent = '';
      body.innerHTML = '<p class="text-[#64748B]">' + t('gpn.matrix.empty') + '</p>';
      return;
    }

    ts.textContent = new Date(data.measuredAt).toLocaleTimeString();

    const policyBox = (p, label) => {
      const badge = p.action === 'SwitchServer'
        ? '<span class="px-2 py-0.5 rounded-md bg-cyan-400/10 text-cyan-300 border border-cyan-400/25">' + t('gpn.matrix.switchTo') + ' ' + escHtml(p.targetServerId || '?') + '</span>'
        : p.action === 'FallbackToV2ray'
          ? '<span class="px-2 py-0.5 rounded-md bg-red-400/10 text-red-300 border border-red-400/25">' + t('gpn.matrix.fallback') + '</span>'
          : '<span class="px-2 py-0.5 rounded-md bg-emerald-400/10 text-emerald-300 border border-emerald-400/25">' + t('gpn.matrix.keep') + '</span>';
      return '<div class="flex-1 min-w-0 glass-soft rounded-lg p-2">' +
        '<p class="text-[9px] uppercase tracking-[0.18em] text-[#8A94A6] mb-1">' + label + '</p>' +
        badge +
        '<p class="text-[9px] text-[#64748B] mt-1 truncate" title="' + escHtml(p.reason || '') + '">' + escHtml(p.reason || '') + '</p>' +
        '</div>';
    };

    const rows = data.rows.map(r => {
      const udp = r.udpStatus || '—';
      const udpCls = udp === 'Open' ? 'bg-emerald-400/10 text-emerald-300 border-emerald-400/25'
        : (udp === 'Blocked' || udp === 'HandshakeNoResponse') ? 'bg-red-400/10 text-red-300 border-red-400/25'
          : 'bg-amber-400/10 text-amber-300 border-amber-400/25';
      return '<div class="flex items-center justify-between gap-2 py-0.5 border-b border-white/5 last:border-0">' +
        '<span class="truncate">' + (r.isActive ? '<span class="text-cyan-300">●</span> ' : '') + escHtml(r.name) +
        ' <span class="text-[#64748B]">' + (r.pingMs >= 0 ? r.pingMs + ' ms' : '—') + '</span></span>' +
        '<span class="flex items-center gap-1.5 shrink-0">' +
        '<span class="px-1.5 py-0.5 rounded-md border font-mono text-[9px] ' + udpCls + '">' + escHtml(udp) + '</span>' +
        '<span class="' + (r.healthyDefault ? 'text-emerald-300' : 'text-red-300') + '" title="' + t('gpn.matrix.default') + '">' + (r.healthyDefault ? '✔' : '✘') + '</span>' +
        '<span class="' + (r.healthyStrict ? 'text-emerald-300' : 'text-red-300') + '" title="' + t('gpn.matrix.strict') + '">' + (r.healthyStrict ? '✔' : '✘') + '</span>' +
        '</span></div>';
    }).join('');

    body.innerHTML =
      '<div class="flex gap-2 mb-2 min-w-0">' +
      policyBox(data.defaultPolicy, t('gpn.matrix.default')) +
      policyBox(data.strictPolicy, t('gpn.matrix.strict')) +
      '</div>' +
      '<div class="font-mono text-[10px]">' + rows + '</div>' +
      '<p class="text-[9px] text-[#64748B] mt-1.5">' + t('gpn.matrix.legend') + '</p>';
  };

  // En iyi aday — GPN Bağlan'ın yapacağı otomatik seçim kararını ÖNCEDEN gösterir.
  // (GpnServerSelectionService.EvaluateSelection → setGpnSelectionPrediction; saf
  // DecideSelection, SelectBestServerAsync ile birebir aynı mantık — dashboard
  // yalnızca görüntüler, karar mantığı C# tarafında tek kaynaktır.)
  // Son seçim tahmini (skinBridge.gpnSelectionPrediction).
  let _gpnSelectionPrediction = null;
  window.setGpnSelectionPrediction = (data) => {
    _gpnSelectionPrediction = data || null;
    const body = document.getElementById('gpnCandidateBody');
    const ts = document.getElementById('gpnCandidateTs');
    if (!body || !ts) return;

    if (!data || !data.mode) {
      ts.textContent = '';
      body.innerHTML = '<p class="text-[#64748B]">' + t('gpn.candidate.empty') + '</p>';
      return;
    }

    ts.textContent = new Date(data.measuredAt).toLocaleTimeString();

    const wg = data.mode === 'WireGuardUDP';
    const udpCls = (s) => s === 'Open' ? 'bg-emerald-400/10 text-emerald-300 border-emerald-400/25'
      : (s === 'Blocked' || s === 'HandshakeNoResponse') ? 'bg-red-400/10 text-red-300 border-red-400/25'
        : (s === 'NoResponse') ? 'bg-amber-400/10 text-amber-300 border-amber-400/25'
          : 'bg-white/5 text-[#64748B] border-white/10';

    // Karar rozeti: WireGuardUDP (Tier 2) veya V2rayTCP (Tier 3 düşüşü).
    const modeBadge = wg
      ? '<span class="px-2 py-0.5 rounded-md bg-emerald-400/10 text-emerald-300 border border-emerald-400/25">' + t('gpn.candidate.wireguard') + '</span>'
      : '<span class="px-2 py-0.5 rounded-md bg-red-400/10 text-red-300 border border-red-400/25">' + t('gpn.candidate.v2ray') + '</span>';

    const rows = (data.servers || []).map(s => {
      const sel = s.selected ? '<span class="text-cyan-300">●</span> ' : '';
      return '<div class="flex items-center justify-between gap-2 py-0.5 border-b border-white/5 last:border-0">' +
        '<span class="truncate">' + sel + escHtml(s.name) +
        ' <span class="text-[#64748B]">' + (s.delayMs >= 0 ? s.delayMs + ' ms' : '—') + '</span></span>' +
        '<span class="flex items-center gap-1.5 shrink-0">' +
        '<span class="px-1.5 py-0.5 rounded-md border font-mono text-[9px] ' + udpCls(s.udpStatus) + '">' + escHtml(s.udpStatus || '—') + '</span>' +
        (s.selected ? '<span class="text-cyan-300 text-[10px]" title="' + t('gpn.candidate.selected') + '">' + t('gpn.candidate.selected') + '</span>' : '') +
        '</span></div>';
    }).join('');

    body.innerHTML =
      '<div class="flex items-center gap-2 mb-2 min-w-0">' +
      (data.bestName
        ? '<span class="truncate text-sm font-semibold text-slate-100">' + escHtml(data.bestName) +
          ' <span class="text-[#8A94A6] font-normal">' + (data.pingMs >= 0 ? data.pingMs + ' ms' : '—') + '</span></span>'
        : '<span class="truncate text-sm text-slate-300">' + t('gpn.candidate.none') + '</span>') +
      '<div class="ml-auto flex items-center gap-1.5 shrink-0">' + modeBadge + '</div>' +
      '</div>' +
      '<p class="text-[9px] text-[#64748B] mb-1.5 truncate" title="' + escHtml(data.reason || '') + '">' + escHtml(data.reason || '') + '</p>' +
      '<div class="font-mono text-[10px]">' + rows + '</div>' +
      '<p class="text-[9px] text-[#64748B] mt-1.5">' + t('gpn.candidate.hint') + '</p>';
  };
  const gpnPidPoolStart = document.getElementById('gpnPidPoolStart');
  const gpnPidPoolStop = document.getElementById('gpnPidPoolStop');
  const gpnPidPoolRefresh = document.getElementById('gpnPidPoolRefresh');
  if (gpnPidPoolStart) {
    gpnPidPoolStart.addEventListener('click', () => postToHost({ action: 'gpn_pid_pool_start' }));
  }
  if (gpnPidPoolStop) {
    gpnPidPoolStop.addEventListener('click', () => postToHost({ action: 'gpn_pid_pool_stop' }));
  }
  if (gpnPidPoolRefresh) {
    gpnPidPoolRefresh.addEventListener('click', () => postToHost({ action: 'gpn_pid_pool_refresh' }));
  }

  function bindGpnServersControls() {
    const list = document.getElementById('gpnServerList');
    const importBtn = document.getElementById('gpnImportBtn');
    const refreshBtn = document.getElementById('gpnServersRefreshBtn');
    const addBtn = document.getElementById('gpnServerAddBtn');
    if (list) {
      list.addEventListener('click', (e) => {
        const edit = e.target.closest('[data-gpn-edit]');
        if (edit) {
          postToHost({ action: 'gpn_server_edit_dialog', serverId: edit.dataset.gpnEdit });
          return;
        }
        const toggle = e.target.closest('[data-gpn-toggle]');
        if (toggle) {
          postToHost({ action: 'gpn_server_toggle', serverId: toggle.dataset.gpnToggle, enabled: toggle.dataset.enabled === '1' });
          return;
        }
        const del = e.target.closest('[data-gpn-delete]');
        if (del && confirm(t('gpn.servers.confirmDelete'))) {
          postToHost({ action: 'gpn_server_delete', serverId: del.dataset.gpnDelete });
        }
      });
    }
    if (addBtn) {
      addBtn.addEventListener('click', () => postToHost({ action: 'gpn_server_add_dialog' }));
    }
    if (importBtn) {
      importBtn.addEventListener('click', () => {
        const text = document.getElementById('gpnConfText');
        if (!text || !text.value.trim()) {
          window.gpnServersNotify && window.gpnServersNotify(t('gpn.servers.emptyConf'));
          return;
        }
        importBtn.disabled = true;
        postToHost({ action: 'gpn_server_add', confText: text.value });
        setTimeout(() => { importBtn.disabled = false; text.value = ''; }, 800);
      });
    }
    if (refreshBtn) {
      refreshBtn.addEventListener('click', () => postToHost({ action: 'gpn_servers_list' }));
    }
    const defaultsRestoreBtn = document.getElementById('gpnDefaultsRestoreBtn');
    if (defaultsRestoreBtn) {
      defaultsRestoreBtn.addEventListener('click', () => postToHost({ action: 'gpn_defaults_restore' }));
    }
    const probeBtn = document.getElementById('gpnProbeBtn');
    if (probeBtn) {
      probeBtn.addEventListener('click', () => {
        // Manuel ölçüm her zaman çalışır — otomatik döngünün pending kilidi
        // kullanıcının tıklamasını yutmamalı.
        gpnProbePending = false;
        requestGpnProbe();
      });
    }
  }

  // Canlı ölçüm: view açıkken her 15 saniyede bir host'tan taze ping+UDP sonucu iste.
  function requestGpnProbe() {
    if (gpnProbePending) return;
    gpnProbePending = true;
    postToHost({ action: 'gpn_servers_probe' });
    setTimeout(() => { gpnProbePending = false; }, 12000);
  }
  function startGpnProbeLoop() {
    stopGpnProbeLoop();
    requestGpnProbe();
    gpnProbeTimer = setInterval(requestGpnProbe, 15000);
  }
  function stopGpnProbeLoop() {
    if (gpnProbeTimer) { clearInterval(gpnProbeTimer); gpnProbeTimer = null; }
    gpnProbePending = false;
  }

  window.setGpnConnectState = setGpnConnectState;

  let gpnRecoveryWatch = true;
  function setGpnRecoveryWatchUI(enabled) {
    gpnRecoveryWatch = enabled === true;
    const tgl = document.getElementById('gpnRecoveryWatchToggle');
    if (!tgl) return;
    tgl.setAttribute('aria-checked', String(gpnRecoveryWatch));
    tgl.classList.toggle('bg-cyan-400/70', gpnRecoveryWatch);
    tgl.classList.toggle('bg-white/15', !gpnRecoveryWatch);
    const knob = tgl.querySelector('span');
    if (knob) knob.classList.toggle('translate-x-4', gpnRecoveryWatch);
  }
  const gpnRecoveryToggle = document.getElementById('gpnRecoveryWatchToggle');
  if (gpnRecoveryToggle) {
    gpnRecoveryToggle.addEventListener('click', () => {
      const next = !gpnRecoveryWatch;
      setGpnRecoveryWatchUI(next);
      postToHost({ action: 'set_gpn_recovery_watch', enabled: next });
    });
  }
  // GPN stabil mod: failover KAPALIYSA (varsayılan) bağlantı seçilen sunucuya takılı
  // kalır — otomatik sunucu değişimi, V2rayTCP düşüşü ve kurtarma yok (en stabil/kessintisiz).
  let gpnFailover = false;
  function setGpnFailoverUI(enabled) {
    gpnFailover = enabled === true;
    const tgl = document.getElementById('gpnFailoverToggle');
    if (!tgl) return;
    tgl.setAttribute('aria-checked', String(gpnFailover));
    tgl.classList.toggle('bg-cyan-400/70', gpnFailover);
    tgl.classList.toggle('bg-white/15', !gpnFailover);
    const knob = tgl.querySelector('span');
    if (knob) knob.classList.toggle('translate-x-4', gpnFailover);
  }
  const gpnFailoverToggleEl = document.getElementById('gpnFailoverToggle');
  if (gpnFailoverToggleEl) {
    gpnFailoverToggleEl.addEventListener('click', () => {
      const next = !gpnFailover;
      setGpnFailoverUI(next);
      postToHost({ action: 'set_gpn_failover', enabled: next });
    });
  }
  // GPN failover/kurtarma telemetri sayaçları (host GpnTelemetryService'ten).
  // Son failover telemetri anlık görüntüsü (skinBridge.gpnTelemetry).
  let _gpnTelemetrySnap = null;
  window.setGpnTelemetry = (snap) => {
    if (!snap || typeof snap !== 'object') return;
    _gpnTelemetrySnap = snap;
    const text = (v) => String(Number.isFinite(v) ? v : 0);
    const set = (id) => {
      const el = document.getElementById(id);
      if (el) el.textContent = text(id === 'gpnTelSwitches' ? snap.serverSwitches
        : id === 'gpnTelDeaths' ? snap.udpDeaths
        : id === 'gpnTelFallbacks' ? snap.modeFallbacks
        : id === 'gpnTelRecovers' ? snap.recoveries
        : snap.modeDecisions);
    };
    ['gpnTelSwitches', 'gpnTelDeaths', 'gpnTelFallbacks', 'gpnTelRecovers', 'gpnTelDecisions'].forEach(set);
    const total = document.getElementById('gpnTelemetryTotal');
    if (total) total.textContent = text(snap.totalEvents);
  };
  const gpnTelemetryReset = document.getElementById('gpnTelemetryReset');
  if (gpnTelemetryReset) {
    gpnTelemetryReset.addEventListener('click', () => postToHost({ action: 'reset_gpn_telemetry' }));
  }
  // Son olaylar geçmişi (host GpnResilienceLog döngüsel tamponu) — en son server
  // switch / UDP ölümü / Tier-3 düşüşü / Tier-2 kurtarma / otomatik seçim kararlarını
  // biriktirip eylem türüne göre renk kodlu satırlar halinde gösterir.
  const gpnEventMeta = (action) => {
    switch (action) {
      case 'ServerSwitch': return { cls: 'gpn-ev-switch', key: 'gpn.reslog.action.switch' };
      case 'UdpDeath':     return { cls: 'gpn-ev-death',   key: 'gpn.reslog.action.udpDeath' };
      case 'ModeFallback': return { cls: 'gpn-ev-fallback', key: 'gpn.reslog.action.modeFallback' };
      case 'Recover':      return { cls: 'gpn-ev-recover',  key: 'gpn.reslog.action.recover' };
      case 'ModeDecision': return { cls: 'gpn-ev-select',   key: 'gpn.reslog.action.select' };
      default:             return { cls: 'gpn-ev-other', key: null, raw: (action || '') };
    }
  };
  // Son dayanıklılık günlüğü (skinBridge.gpnResilienceLog).
  let _gpnResilienceLog = null;
  window.setGpnResilienceLog = (data) => {
    _gpnResilienceLog = data || null;
    const list = document.getElementById('gpnResilienceLogList');
    if (!list) return;
    const entries = (data && Array.isArray(data.entries)) ? data.entries : [];
    if (!entries.length) {
      list.innerHTML = '<p class="text-[#64748B]">' + t('gpn.reslog.empty') + '</p>';
    } else {
      list.innerHTML = entries.map((e, i) => {
        const meta = gpnEventMeta(e && e.action);
        const time = (e && Number.isFinite(e.timestampMs))
          ? new Date(e.timestampMs).toLocaleTimeString()
          : '';
        const label = meta.key ? t(meta.key) : meta.raw;
        const mode = e && e.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WireGuard';
        const server = e && (e.serverName || e.serverId || '');
        const target = e && (e.targetServerName || e.targetServerId || '');
        const reason = e && e.reason ? e.reason : '';
        const parts = [];
        if (server) parts.push(server);
        if (target) parts.push('→ ' + target);
        if (reason) parts.push(reason);
        const first = (i === entries.length - 1) ? ' gpn-event-pop' : '';
        return '<div class="gpn-event ' + meta.cls + first + '">'
          + '<span class="gpn-ev-dot"></span>'
          + '<div class="min-w-0">'
          + '<div class="flex flex-wrap items-center gap-x-1.5 gap-y-0.5">'
          + '<span class="gpn-ev-badge">' + escHtml(label) + '</span>'
          + '<span class="gpn-ev-mode">' + escHtml(mode) + '</span>'
          + '<span class="gpn-ev-time">' + escHtml(time) + '</span>'
          + '</div>'
          + (parts.length ? '<div class="gpn-ev-detail">' + escHtml(parts.join('  ·  ')) + '</div>' : '')
          + '</div>'
          + '</div>';
      }).join('');
    }
    list.scrollTop = list.scrollHeight;
    const count = document.getElementById('gpnResLogCount');
    if (count) count.textContent = String(entries.length);
    const path = document.getElementById('gpnResLogPath');
    if (path && data && data.path) path.textContent = data.path;
  };
  const gpnResLogRefresh = document.getElementById('gpnResLogRefresh');
  if (gpnResLogRefresh) {
    gpnResLogRefresh.addEventListener('click', () => postToHost({ action: 'get_gpn_resilience_log' }));
  }
  const gpnResLogClear = document.getElementById('gpnResLogClear');
  if (gpnResLogClear) {
    gpnResLogClear.addEventListener('click', () => postToHost({ action: 'clear_gpn_resilience_log' }));
  }
  const gpnDiagClear = document.getElementById('gpnDiagClear');
  if (gpnDiagClear) {
    gpnDiagClear.addEventListener('click', () => {
      const feed = document.getElementById('gpnDiagFeed');
      if (feed) {
        feed.innerHTML = '<p class="text-[#64748B]">' + t('gpn.diag.empty') + '</p>';
      }
      gpnDiagCount = 0;
      const countBadge = document.getElementById('gpnDiagCount');
      if (countBadge) countBadge.textContent = '0';
    });
  }
  const gpnConnectBtn = $('gpnConnectBtn');
  if (gpnConnectBtn) {
    gpnConnectBtn.addEventListener('click', () => {
      if (aogpn.app.getTunLocked()) {
        return;
      }
      setGpnConnectState('…', true);
      postToHost({ action: 'gpn_connect' });
    });
  }

  $('connectBtn').addEventListener('click', () => {
    // TUN without elevation would be rejected by the host; block the press up front.
    if (aogpn.app.getTunLocked()) {
      return;
    }
    // The real app waits for C# to acknowledge the applied routing transition. A
    // local fallback keeps the same file interactive when opened outside WebView2.
    if (!postToHost({ action: 'toggle_connection', mode: aogpn.app.getMode(), transport: aogpn.app.getTransport(), protocol: aogpn.app.getProtocolPreference() })) {
      aogpn.app.setConnected(!aogpn.app.getConnected(), false);
    }
  });
  // Mobile nav quick connect/disconnect: same toggle, same TUN lock.
  const mobileConnectBtn = $('mobileConnectBtn');
  if (mobileConnectBtn) {
    mobileConnectBtn.addEventListener('click', () => {
      if (aogpn.app.getTunLocked()) {
        return;
      }
    if (!postToHost({ action: 'toggle_connection', mode: aogpn.app.getMode(), transport: aogpn.app.getTransport(), protocol: aogpn.app.getProtocolPreference() })) {
      aogpn.app.setConnected(!aogpn.app.getConnected(), false);
    }
    });
  }

  const captureTunStack = document.getElementById('captureTunStack');
  if (captureTunStack) {
    captureTunStack.addEventListener('change', () => {
      const next = captureTunStack.value;
      if (!['gvisor', 'system', 'mixed'].includes(next)) {
        return;
      }
      postToHost({ action: 'set_tun_stack', stack: next });
    });
  }
  $('relaunchAdminBtn').addEventListener('click', () => {
    postToHost({ action: 'app_control', command: 'reboot_as_admin' });
  });
  $('tunAdminRelaunchBtn')?.addEventListener('click', () => {
    postToHost({ action: 'app_control', command: 'reboot_as_admin' });
  });

  (function initGpnAdvanced() {
    const wrap = document.getElementById('gpnAdvanced');
    const btn = document.getElementById('gpnAdvancedToggle');
    const body = document.getElementById('gpnAdvancedBody');
    const chev = document.getElementById('gpnAdvancedChevron');
    if (!wrap || !btn || !body) return;
    const KEY = 'aogpn.gpnAdvancedOpen';
    let open = false;
    try { open = localStorage.getItem(KEY) === '1'; } catch (e) {}
    btn.addEventListener('click', () => {
      open = !open;
      body.classList.toggle('hidden', !open);
      btn.setAttribute('aria-expanded', String(open));
      if (chev) chev.classList.toggle('-rotate-90', !open);
      try { localStorage.setItem(KEY, open ? '1' : '0'); } catch (e) {}
    });
    body.classList.toggle('hidden', !open);
    if (chev) chev.classList.toggle('-rotate-90', !open);
    btn.setAttribute('aria-expanded', String(open));
  })();

  window.aogpn = window.aogpn || {};
  window.aogpn.gpn = {
    startGpnClusterLoop, startGpnProbeLoop, stopGpnProbeLoop,
    setGpnConnectState, setGpnRecoveryWatchUI, setGpnFailoverUI,
    bindGpnServersControls,
    getServers: () => gpnServers,
    getServerProbes: () => Array.from(gpnServerProbes.values()),
    getRecoveryWatch: () => gpnRecoveryWatch,
    getFailover: () => gpnFailover,
    getConnectState: () => ({ label: _gpnConnectStateLabel || '—', pending: !!_gpnConnectPending }),
    getPidPool: () => _gpnPidPool || null,
    getCaptureSettings: () => _gpnCaptureSettings || null,
    getWintunSettings: () => _gpnWintunSettings || null,
    getCaptureStats: () => _gpnCaptureStats || null,
    getFailoverMatrix: () => _gpnFailoverMatrix || null,
    getSelectionPrediction: () => _gpnSelectionPrediction || null,
    getTelemetrySnap: () => _gpnTelemetrySnap || null,
    getResilienceLog: () => _gpnResilienceLog || null,
    getDiagLines: () => _gpnDiagLines.slice()
  };
})();
