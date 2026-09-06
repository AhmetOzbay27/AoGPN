/* ==========================================================================
   AoGPN dashboard — application script (koordinatör)
   --------------------------------------------------------------------------
   Bu dosya artık uygulamanın TEK mantık deposu değil: başlatma akışını, host
   köprüsünün (window.*) kayıtlarını ve görünüm/özellik kablolamasını yürüten
   koordinatördür. Özellik mantığı modüllere taşındı — Temalar/core + Temalar/
   features klasörleri, index.html'deki <script> sırasıyla yüklenir:

   core/events.js              olay veriyolu (skin-changed vb. çapraz bildirim)
   core/util.js                $, escHtml gibi ortak yardımcılar
   core/dom.js                 dashboard element referansları
   core/bridge.js              WebView2 host köprüsü (postToHost)
   core/i18n.js                t(), dil yükleme, dil popover'ı
   core/selects.js             native <select> → temalı açılır menü yükseltmesi
   core/theme.js               tema motoru, canvas efektleri, ses/konfeti,
                               efekt katmanı (tier/reduce/hidden), layout + tema
                               destesi, popover
   core/tooltips.js            özel tooltip motoru + MutationObserver
   features/window-controls.js küçült/büyüt/kapat + başlık çubuğu sürükleme
   features/release-notes.js   Sürüm Notları + setAppInfo
   features/telemetry.js       gauge kalibrasyonu, canlı kartlar, oturum sayacı
   features/settings.js        Ayarlar formu (options, doldurma, kaydetme)
   features/game-boost.js      domain kural paneli + EXE sürükle-bırak
   features/gpn.js             GPN paneli (sunucu yönetimi, küme, diyagnoz,
                               failover, WARP/WinDivert kartları, GPN Bağlan)
   features/views.js           görünümler (showView), Connection Monitor, split
                               routing tablosu, boost kartları, uygulama ikonları
   features/nodes.js           VPN Rotaları (düğüm kartları, seçim, sürükle,
                               ping testi, düğüm havuzu, bağlam menüsü)
   features/connection.js      bağlantı durumu UI'si, mod/transport/protokol/
                               system-proxy uygulayıcıları, IP doğrulama, host
                               köprüsü (setConnectionState ... setRealIpState)

   Koordinatör burada: durum değişkenleri (connected, mode, transport, ...)
   + skinBridge + skin motoru + performans HUD + window.* dışa aktarımları.
   Modüller aogpn.* kayıt defterleriyle bağlanır; C# sözleşmesi (window.*)
   isimleri birebir korunur. split-*.js betikleri aynı desenle devam etmek
   için hazır durur.
   ========================================================================== */
(() => {
  'use strict';
  const body = document.body;
  // ---- Core module bindings (loaded before app.js via <script> tags) ----
  const { $ } = aogpn.util;
  const { escHtml } = aogpn.util;
  const { postToHost, hasWebViewBridge } = aogpn.bridge;
  const { btnIcon, btnText, btnSub, statusDot, statusLabel, nodeName, nodeAddr,
          ipDisplay, ipCountry, ipVerifyText, ipVerifyDetail, ipVerifyCard,
          ipLeakBanner, ipLeakText, ipOkBanner, ipOkText, ipOkTunnelIp, ipOkIspIp,
          pingVal, pingSub, lossVal, lossSub, downVal, upVal,
          downGauge, upGauge, systemProxyToggleBtn, systemProxyDot,
          systemProxyLabel, systemProxyAddress, systemProxyModeSelect,
          proxyTestBtn, proxyTestIcon, proxyTestLabel,
          quickProtocolSelect, quickAutoReconnect, protocolHint,
          routeHint, transportHint, connectionStatusLine } = aogpn.dom;
  const { t, resolveKey, applyTexts, LANGUAGES, langName, applyLanguage, countryDisplayName } = aogpn.i18n;
  const { upgradeSelect, refreshAllCustomSelects } = aogpn.selects;
  const { THEMES, THEME_STORAGE_KEY, LAYOUT_STORAGE_KEY, applyTheme, applyLayout,
          renderThemeDeck, renderTopThemePopover, loadThemesFromDisk,
          initCanvasEffects, playThemeSound, fireConfetti, getCurrentThemeId } = aogpn.theme;
  const { loadReleaseNotesFromDisk } = aogpn.releaseNotes;
  const { resetTelemetry, updateTelemetry, flashGpnTelemetry } = aogpn.telemetry;
  const { startGpnClusterLoop, setGpnConnectState, setGpnRecoveryWatchUI,
          setGpnFailoverUI, bindGpnServersControls } = aogpn.gpn;
  const { fillSettingsOptions, setSettingsField, FALLBACK_OPTIONS } = aogpn.settings;

  let connected = false;
  let connecting = false;
  let mode = 'gpn'; // GPN Game Tunnel is the primary mode; Global VPN stays secondary.
  let transport = 'proxy';
  let protocolPreference = 'auto';
  let autoReconnect = true;
  let systemProxyMode = 0; // persisted independent preference: Clear / Set / Unchanged / PAC
  let effectiveSystemProxyMode = 0;
  let systemProxyConnectionOwned = false;
  let systemProxyAppliedAddress = '';
  // Tracks the last owned state so we can toast exactly when CONNECT hands the
  // system proxy over to the active tunnel (PROXY · CONNECTION transition).
  let prevSystemProxyConnectionOwned = false;
  // Whether the session is elevated. The host pushes this at startup and on every
  // connection-state sync; it locks CONNECT when TUN is chosen without elevation.
  let isAdmin = true;
  let tunLocked = false;
  const perfState = { started: performance.now(), frames: 0, lastFrame: performance.now(), frameSamples: [], longTasks: 0, renders: 0 };

  function startPerformanceProbe() {
    if (window.__aogpnPerfProbe) return;
    window.__aogpnPerfProbe = true;
    // Sampling bursts: the probe measures frame pacing by running
    // requestAnimationFrame, but a permanent rAF loop forces the compositor to
    // produce a frame every vsync even while the dashboard is static — that
    // alone pins the WebView2 GPU process near 100%. Frames are therefore only
    // requested for a short burst every 10 s; between bursts the page never
    // schedules a frame and the compositor can go fully idle.
    // Shortened burst: 800 ms still yields ~45 frame samples, which is plenty
    // for the avg/peak readout, at a quarter of the old duty cycle.
    const PROBE_BURST_MS = 800;
    let burstUntil = 0;
    const report = () => {
      const samples = perfState.frameSamples;
      const avg = samples.length ? samples.reduce((a, b) => a + b, 0) / samples.length : 0;
      const worst = samples.length ? Math.max(...samples) : 0;
      var _probeAnimations = document.getAnimations ? document.getAnimations().length : -1;
      var _probeAvg = Number(avg.toFixed(2));
      var _probeWorst = Number(worst.toFixed(2));
      _lastProbeSample = {
        uptimeMs: Math.round(performance.now() - perfState.started),
        frames: perfState.frames, avgFrameMs: _probeAvg, worstFrameMs: _probeWorst,
        longTasks: perfState.longTasks, animations: _probeAnimations,
        visibleView: currentView, theme: aogpn.theme.getCurrentThemeId() || ''
      };
      postToHost({ action: 'performance_sample', payload: _lastProbeSample });
      perfState.frames = 0; perfState.frameSamples = [];
    };
    const frame = now => {
      perfState.frames++;
      if (perfState.lastFrame) perfState.frameSamples.push(now - perfState.lastFrame);
      perfState.lastFrame = now;
      if (perfState.frameSamples.length > 120) perfState.frameSamples.shift();
      if (now < burstUntil) {
        requestAnimationFrame(frame);
      } else {
        // Burst finished: report once and stop requesting frames until the
        // next interval tick.
        setTimeout(report, 0);
      }
    };
    if (window.PerformanceObserver) {
      try {
        new PerformanceObserver(list => { perfState.longTasks += list.getEntries().length; })
          .observe({ type: 'longtask', buffered: true });
      } catch { /* unsupported in this WebView2 build */ }
    }
    window.__aogpnKickPerfProbe = () => {
      burstUntil = performance.now() + PROBE_BURST_MS;
      perfState.lastFrame = 0;
      requestAnimationFrame(frame);
    };
    window.__aogpnPerfTimer = typeof window.setInterval === 'function' ? window.setInterval(() => {
      // A burst forces the compositor to produce frames every vsync, so it
      // must never run while nothing consumes the samples: the Performance
      // HUD is the only consumer (the sample posts land in the host's diag
      // log). With the HUD closed — or the page hidden — skip the burst so
      // the GPU process can go fully idle; opening the HUD kicks one
      // immediately instead of waiting for the next tick.
      if (typeof window.__aogpnPerfHudVisible === 'function'
          && (!window.__aogpnPerfHudVisible() || document.hidden)) return;
      window.__aogpnKickPerfProbe();
    }, 10000) : null;
  }




  // The real AoGPN profile published by the WPF host; null keeps the static
  // concept node list as the standalone-preview fallback.

  let currentView = 'dashboard';
  let splitMode = 'off';
  // "Reduce effects" (motion budget): user switch from Settings, OR-ed with the
  // OS prefers-reduced-motion flag; the host config is pushed via setReduceEffects.
  // GPN routing direction: false = whitelist (only listed apps are tunneled),
  // true = blacklist (listed apps stay direct, everything else tunnels).
  let invertManual = false;
  let monitorSnapshot = { connections: [], apps: [], traffic: [] };
  let processCatalog = [];

  window.applySettings = (data) => {
    if (!data || typeof data !== 'object') {
      return;
    }
    fillSettingsOptions(data.options || FALLBACK_OPTIONS);
    ['core', 'general', 'connection', 'systemProxy', 'tun', 'coreType'].forEach(group => {
      const g = data[group];
      if (!g || typeof g !== 'object') {
        return;
      }
      Object.keys(g).forEach(k => setSettingsField(k, g[k]));
        if (group === 'systemProxy') {
        aogpn.connection.applySystemProxyState(g.sysProxyType, g.effectiveSysProxyType, g.connectionOwnsProxy);
      }
      if (group === 'connection') {
        aogpn.connection.applyProtocolPreference(g.protocolPreference);
        if (typeof g.invertManualRouting === 'boolean') {
          invertManual = g.invertManualRouting;
          aogpn.views.applyDirection();
        }
        if (typeof g.autoReconnectEnabled === 'boolean') {
          autoReconnect = g.autoReconnectEnabled;
          aogpn.connection.syncQuickControls();
        }
        if (typeof g.gpnEnableRecoveryWatch === 'boolean') {
          setGpnRecoveryWatchUI(g.gpnEnableRecoveryWatch);
        }
        if (typeof g.gpnEnableFailover === 'boolean') {
          setGpnFailoverUI(g.gpnEnableFailover);
        }
      }
      if (group === 'tun') {
        const stackSelect = document.getElementById('captureTunStack');
        if (stackSelect && typeof g.tunStack === 'string' && g.tunStack) {
          stackSelect.value = ['gvisor', 'system', 'mixed'].includes(g.tunStack) ? g.tunStack : 'mixed';
        }
      }
    });
    refreshAllCustomSelects();
    if (data.platform && data.platform.isWindows && data.platform.isAdmin === false) {
      const hint = document.getElementById('st_tunAdminHint');
      if (hint) {
        hint.textContent = 'TUN, trafiği ağ katmanında yakalar ve yönetici ayrıcalıkları gerektirir. Mevcut oturum yükseltilmemiş, bu yüzden TUN etkinleştirilemez.';
      }
    }
  };

  document.querySelectorAll('[data-settings-tab]').forEach(btn => btn.addEventListener('click', () => {
    document.querySelectorAll('[data-settings-tab]').forEach(b => b.classList.toggle('active', b === btn));
    document.querySelectorAll('[data-settings-panel]').forEach(p => {
      p.classList.toggle('hidden', p.dataset.settingsPanel !== btn.dataset.settingsTab);
    });
  }));

  // About & Help page tabs (Program info / Help & Feedback).
  document.querySelectorAll('[data-about-tab]').forEach(btn => btn.addEventListener('click', () => {
    document.querySelectorAll('[data-about-tab]').forEach(b => b.classList.toggle('active', b === btn));
    document.querySelectorAll('[data-about-panel]').forEach(p => {
      p.classList.toggle('hidden', p.dataset.aboutPanel !== btn.dataset.aboutTab);
    });
  }));

  // App version/info pushed from the native host (MainWindow.PushAppInfoAsync).
  // Rendered live on the About & Help page so it always matches the running build.



  // ---------- Shared session state (consumed by core modules) ----------
  let _lastIpState = null;
  let _lastIpMeasuredAt = null; // son ölçümün ISO zaman damgası (host measuredAt)
  let _lastTelemetry = null;
  let _activeGpnServer = ''; // "sonra" ölçümünün yapıldığı seçili/aktif GPN düğümü (via etiketi)
  let _activeGpnMode = '';  // aktif GPN bağlantı modu ('WireGuard' | 'V2rayTCP' | '') — WARP uyarısı buna göre verilir

  // These globals are the only JavaScript entry points used by the WPF backend.
  window.updateTelemetry = (ping, loss, download, upload) =>
    updateTelemetry(ping, loss, download, upload);
  if (!window.__aogpnPerfDisabled && hasWebViewBridge()) startPerformanceProbe();

  // ---------- Performance HUD (diagnostics overlay) ----------
  // A lightweight overlay that shows live GPU/rendering state: CSS animation
  // count, canvas loop status, probe frame timings, theme and reduce-effects
  // mode. Toggle with Ctrl+Shift+H or the dashboard Settings switch.
  var _lastProbeSample = null;
  var _perfHudEl = null;
  var _perfHudVisible = false;
  window.__aogpnPerfHudVisible = function() { return _perfHudVisible; };
  var _perfHudTimer = null;

  function createPerfHudEl() {
    if (_perfHudEl) return;
    _perfHudEl = document.createElement('div');
    _perfHudEl.id = 'perfHudOverlay';
    _perfHudEl.style.cssText = 'position:fixed;top:8px;right:8px;z-index:99999;background:rgba(10,13,24,.92);border:1px solid rgba(34,211,238,.25);border-radius:8px;padding:10px 14px;font-family:monospace;font-size:11px;color:#94A3B8;line-height:1.7;min-width:360px;pointer-events:none;backdrop-filter:blur(6px);-webkit-backdrop-filter:blur(6px);box-shadow:0 0 24px rgba(0,0,0,.5);';
    document.body.appendChild(_perfHudEl);
  }

  function updatePerfHud() {
    if (!_perfHudVisible || !_perfHudEl) return;
    var anims = typeof document.getAnimations === 'function' ? document.getAnimations().length : '?';
    var ringState = body.classList.contains('connecting') ? 'spinning' : 'static';
    var fx = aogpn.theme.getPerfState();
    var matrixState = fx.matrixFramePending ? 'running' : 'idle';
    var confettiN = fx.confettiN;
    var p = _lastProbeSample;
    var probeLine = p
      ? p.frames + ' frames / ' + p.avgFrameMs + 'ms avg / ' + p.worstFrameMs + 'ms peak / ' + p.animations + ' anims'
      : 'waiting for burst…';
    var line = '<span style="color:#22D3EE;font-weight:600">Performance HUD</span> — <span style="color:#5B6472">Ctrl+Shift+H</span>';
    line += '<br><b style="color:#8A94A6">Theme:</b> ' + (aogpn.theme.getCurrentThemeId() || '?')
      + ' &nbsp; <b style="color:#8A94A6">FX:</b> ' + (aogpn.theme.getEffectsTier() || 'full')
      + ' &nbsp; <b style="color:#8A94A6">OS-motion:</b> ' + (fx.osReducedMotion ? '<span style="color:#F59E0B">yes</span>' : 'no') + ')' ;
    line += '<br><b style="color:#8A94A6">CSS anims:</b> ' + anims
      + ' &nbsp; <b style="color:#8A94A6">Ring:</b> ' + ringState;
    line += '<br><b style="color:#8A94A6">Confetti:</b> ' + confettiN
      + ' &nbsp; <b style="color:#8A94A6">Matrix:</b> ' + matrixState;
    line += '<br><b style="color:#8A94A6">Probe:</b> ' + probeLine;
    line += '<br><b style="color:#8A94A6">Connected:</b> ' + connected
      + ' &nbsp; <b style="color:#8A94A6">Body:</b> <span style="color:#5B6472">' + body.className + '</span>';
    _perfHudEl.innerHTML = line;
  }

  function togglePerfHud(show) {
    _perfHudVisible = typeof show === 'boolean' ? show : !_perfHudVisible;
    if (_perfHudVisible) {
      createPerfHudEl();
      _perfHudEl.style.display = 'block';
      updatePerfHud();
      if (!_perfHudTimer) _perfHudTimer = setInterval(updatePerfHud, 500);
      // Frame pacing data is only collected while the HUD is open; start a
      // sample burst immediately so the readout is fresh instead of waiting
      // up to 10 s for the next scheduled tick.
      if (typeof window.__aogpnKickPerfProbe === 'function') window.__aogpnKickPerfProbe();
    } else {
      if (_perfHudEl) _perfHudEl.style.display = 'none';
      if (_perfHudTimer) { clearInterval(_perfHudTimer); _perfHudTimer = null; }
    }
    // Persist standalone preview choice; host wins on startup push.
    try { localStorage.setItem('aogpn.perfHud', _perfHudVisible ? '1' : '0'); } catch {}
  }
  window.togglePerfHud = togglePerfHud;

  // Restore standalone preview state + keyboard toggle (Ctrl+Shift+H).
  try {
    if (localStorage.getItem('aogpn.perfHud') === '1') togglePerfHud(true);
  } catch {}
  document.addEventListener('keydown', function(e) {
    if (e.ctrlKey && e.shiftKey && (e.key === 'H' || e.key === 'h')) {
      e.preventDefault();
      togglePerfHud();
    }
  });

  // Sunucu Yönetimi ekranı kontrolleri (import / refresh / toggle / delete).
  bindGpnServersControls();

  // Belirgin "GPN Bağlan" butonu: GPN modu açıkça seçildiğinde, seçili profil
  // WireGuard olmasa bile İtalya/Almanya otomatik seçimini zorla tetikler.
  // GPN kurtarma izleyici toggle durumu (config'den; V2rayTCP düşüşünden sonra
  // sağlıklı sunucu bulununca otomatik WireGuard'a dön).
  systemProxyToggleBtn.addEventListener('click', () => {
    if (!postToHost({ action: 'toggle_system_proxy' })) {
      const next = aogpn.connection.isProxyModeEnabled(systemProxyMode) ? 0 : 1;
      aogpn.connection.applySystemProxyState(next, next, false);
    }
  });
  systemProxyModeSelect.addEventListener('change', () => {
    const next = Math.max(0, Math.min(3, Number(systemProxyModeSelect.value) || 0));
    if (!postToHost({ action: 'set_system_proxy_mode', mode: next })) {
      aogpn.connection.applySystemProxyState(next, next, false);
    }
  });
  let proxyTestTimer = null;
  // Last host answer for the system-proxy test; exposed to standalone skins
  // through the skinBridge so their proxy-test button can show the result too.
  let _proxyTestResult = null;
  proxyTestBtn.addEventListener('click', () => {
    if (proxyTestBtn.classList.contains('testing')) { return; }
    proxyTestBtn.className = 'proxy-test-btn testing';
    proxyTestIcon.textContent = '⏳';
    proxyTestLabel.textContent = 'Test ediliyor…';
    if (proxyTestTimer) { clearTimeout(proxyTestTimer); }
    // Auto-clear the result after 6 s if the host never answers.
    proxyTestTimer = setTimeout(() => {
      proxyTestBtn.className = 'proxy-test-btn failed';
      proxyTestIcon.textContent = '⚠️';
      proxyTestLabel.textContent = 'Zaman aşımı';
      proxyTestTimer = setTimeout(() => resetProxyTestBtn(), 4000);
    }, 6000);
    if (!postToHost({ action: 'test_proxy' })) {
      clearTimeout(proxyTestTimer);
      proxyTestBtn.className = 'proxy-test-btn failed';
      proxyTestIcon.textContent = '⚠️';
      proxyTestLabel.textContent = 'Sunucu yok';
      proxyTestTimer = setTimeout(() => resetProxyTestBtn(), 4000);
    }
  });
  function resetProxyTestBtn() {
    proxyTestBtn.className = 'proxy-test-btn';
    proxyTestIcon.textContent = '⚡';
    proxyTestLabel.textContent = 'Test';
    if (proxyTestTimer) { clearTimeout(proxyTestTimer); proxyTestTimer = null; }
  }
  window.setProxyTestResult = (result) => {
    _proxyTestResult = result || null;
    const ok = result && result.ok === true;
    if (proxyTestTimer) { clearTimeout(proxyTestTimer); proxyTestTimer = null; }
    proxyTestBtn.className = ok ? 'proxy-test-btn success' : 'proxy-test-btn failed';
    proxyTestIcon.textContent = ok ? '✅' : '❌';
    proxyTestLabel.textContent = ok
      ? (Number.isFinite(result.ms) ? result.ms + ' ms' : 'OK')
      : (result && result.message ? result.message.slice(0, 20) : 'Unreachable');
    proxyTestTimer = setTimeout(() => resetProxyTestBtn(), ok ? 5000 : 6000);
    aogpn.events.emit('skin-changed'); // refresh standalone skins (system-proxy test result)
  };
  function bindProtocolSelect(el) {
    if (!el) {
      return;
    }
    el.addEventListener('change', () => {
      const next = el.value;
      aogpn.connection.applyProtocolPreference(next);
      if (!postToHost({ action: 'set_protocol_preference', protocol: next })) {
        aogpn.connection.applyProtocolPreference(next);
      }
      aogpn.connection.syncQuickControls();
    });
  }
  bindProtocolSelect(quickProtocolSelect);
  bindProtocolSelect(document.getElementById('mobileProtocolSelect'));
  function bindAutoReconnectToggle(el) {
    if (!el) {
      return;
    }
    el.addEventListener('change', () => {
      autoReconnect = el.checked === true;
      if (!postToHost({ action: 'set_auto_reconnect', enabled: autoReconnect })) {
        aogpn.connection.syncQuickControls();
      }
      aogpn.connection.syncQuickControls();
    });
  }
  bindAutoReconnectToggle(quickAutoReconnect);
  bindAutoReconnectToggle(document.getElementById('mobileAutoReconnect'));
  const topNodeSelectEl = $('topNodeSelect');
  if (topNodeSelectEl) {
    topNodeSelectEl.addEventListener('change', () => {
      if (topNodeSelectEl.value) {
        aogpn.nodes.requestNodeSwitch(topNodeSelectEl.value);
      }
    });
  }

  document.querySelectorAll('.mode-pill').forEach(p => p.addEventListener('click', () => {
    mode = p.dataset.mode === 'gpn' ? 'gpn' : 'vpn';
    aogpn.connection.applyMode();
    splitMode = mode === 'gpn' ? 'manual' : 'vpn';
    aogpn.views.applySplitMode();
    if (connected) {
      postToHost({ action: 'set_connection_mode', mode });
    }
  }));
  document.querySelectorAll('.transport-pill').forEach(p => p.addEventListener('click', () => {
    // Global VPN mode locks transport to TUN — ignore clicks.
    if (mode === 'vpn') {
      return;
    }
    const next = p.dataset.transport === 'tun' ? 'tun' : 'proxy';
    transport = next;
    aogpn.connection.applyTransport();
    // Never send TUN to the host without elevation: it would be rejected anyway,
    // and keeping the pill selected shows the locked CONNECT + explanation.
    if (next === 'tun' && !isAdmin) {
      return;
    }
    postToHost({ action: 'set_transport', transport: next });
  }));
  document.querySelectorAll('.protocol-pill').forEach(p => p.addEventListener('click', () => {
    aogpn.connection.applyProtocolPreference(p.dataset.protocol);
    postToHost({ action: 'set_protocol_preference', protocol: protocolPreference });
  }));
  document.querySelectorAll('[data-view]').forEach(item => item.addEventListener('click', (e) => {
    e.preventDefault();
    aogpn.views.showView(item.dataset.view);
  }));
  document.querySelectorAll('.split-mode-pill').forEach(button => button.addEventListener('click', () => {
    splitMode = button.dataset.splitMode;
    aogpn.views.applySplitMode();
    postToHost({ action: 'set_split_mode', mode: splitMode });
  }));
  document.querySelectorAll('.dir-pill').forEach(button => button.addEventListener('click', () => {
    const next = button.dataset.dir === 'blacklist';
    invertManual = next;
    aogpn.views.applyDirection();
    if (!postToHost({ action: 'set_split_direction', invert: String(next) })) {
      aogpn.views.applyDirection();
    }
  }));
  $('monitorFilter')?.addEventListener('input', aogpn.views.renderMonitorConnections);
  $('monitorHideListeners')?.addEventListener('change', aogpn.views.renderMonitorConnections);
  $('monitorRefreshBtn')?.addEventListener('click', () => postToHost({ action: 'refresh_monitor' }));
  $('boostRefreshBtn')?.addEventListener('click', () => postToHost({ action: 'refresh_monitor' }));
  $('boostRunningAppsBtn')?.addEventListener('click', () => {
    const picker = $('boostProcessPicker');
    aogpn.views.setProcessPickerOpen(Boolean(picker?.classList.contains('hidden')));
  });
  $('boostProcessPickerClose')?.addEventListener('click', () => aogpn.views.setProcessPickerOpen(false));
  $('boostProcessFilter')?.addEventListener('input', aogpn.views.renderProcessCatalog);
  $('boostAddAppBtn')?.addEventListener('click', () => postToHost({ action: 'add_app' }));

  // Pre-fill the settings selects/datalists so the form is usable standalone too.
  fillSettingsOptions(FALLBACK_OPTIONS);

  // Populate the i18n dictionary (default English) before the host pushes the
  // saved language, so dynamic strings never show raw keys.
  applyLanguage('en');
  loadThemesFromDisk();
  loadReleaseNotesFromDisk();
  renderTopThemePopover();
  renderThemeDeck();
  // Wire the canvas effects exactly once (matrix rain, confetti). Missing
  // canvases or a reduced-motion tier make it a no-op, so this is safe on
  // every boot; the loops self-stop when idle.
  initCanvasEffects();
  const savedTheme = (() => { try { return localStorage.getItem(THEME_STORAGE_KEY); } catch { return null; } })();
  applyTheme(THEMES.some(t => t.id === savedTheme) ? savedTheme : 'nebula', false);
  // Apply the saved layout (the pre-paint script already set data-layout to
  // avoid a flash; this syncs the popover buttons and the JS state).
  const savedLayout = (() => { try { return localStorage.getItem(LAYOUT_STORAGE_KEY); } catch { return null; } })();
  applyLayout(savedLayout === 'compact' ? 'compact' : 'status');

  // ================= Standalone skin engine =================
  // Skins are fully independent dashboards (own markup, CSS, fonts, script)
  // loaded into an isolated <iframe id="skinFrame"> inside #skinHost. The
  // registry lives in Temalar/skins.json; the active skin id is persisted in
  // localStorage ('aogpn.skin') and mirrored on <body data-skin>. A skin
  // never touches the main document: it reads live state and fires host
  // actions through window.skinBridge (getters return the current value,
  // setters update the same locals the real dashboard uses).
  const SKIN_STORAGE_KEY = 'aogpn.skin';
  // Cache-busting token for skin iframe URLs (see applySkin). Timestamp-based
  // so every app.js load guarantees fresh skin assets in the preview browser.
  const SKIN_ENGINE_VERSION = Date.now();
  let _skinRegistry = [];
  let _skinSubscribers = [];
  const skinFrame = document.getElementById('skinFrame');
  const skinHost = document.getElementById('skinHost');

  // Core modules emit 'skin-changed' through the aogpn.events bus; forward it to
  // the skin subscribers exactly like the old direct skinNotify() calls did.
  aogpn.events.on('skin-changed', () => {
    _skinSubscribers.forEach(cb => { try { cb(); } catch (e) { /* ignore */ } });
  });

  async function loadSkinRegistry() {
    try {
      const res = await fetch('Temalar/skins.json', { cache: 'no-store' });
      if (res.ok) _skinRegistry = await res.json();
    } catch (e) { /* standalone preview without the registry */ }
    if (!Array.isArray(_skinRegistry)) _skinRegistry = [];
    renderSkinPicker();
    const saved = (() => { try { return localStorage.getItem(SKIN_STORAGE_KEY); } catch { return null; } })();
    const valid = _skinRegistry.some(s => s.id === saved);
    // Repair a stale/removed skin id so the persisted value always matches a
    // real, registered skin (a removed skin would otherwise keep falling back
    // to standard on every launch).
    if (saved && saved !== 'standard' && !valid) {
      try { localStorage.setItem(SKIN_STORAGE_KEY, 'standard'); } catch (e) { /* ignore */ }
    }
    applySkin(valid ? saved : 'standard', false);
  }

  function renderSkinPicker() {
    const row = document.getElementById('topSkinRow');
    if (!row) return;
    row.querySelectorAll('[data-skin]:not([data-skin="standard"])').forEach(b => b.remove());
    _skinRegistry.forEach(skin => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.dataset.skin = skin.id;
      btn.className = 'skin-opt flex-1 rounded-lg px-2 py-1.5 text-[10px] font-semibold border transition-colors';
      btn.textContent = skin.name;
      btn.addEventListener('click', () => applySkin(skin.id));
      row.appendChild(btn);
    });
  }

  function applySkin(id, persist = true) {
    const entry = _skinRegistry.find(s => s.id === id);
    const next = entry ? entry.id : 'standard';
    document.body.setAttribute('data-skin', next);
    if (persist) { try { localStorage.setItem(SKIN_STORAGE_KEY, next); } catch (e) { /* ignore */ } }
    if (skinHost) skinHost.hidden = next === 'standard';
    if (skinFrame && entry) {
      // Cache-bust the iframe URL so a newly edited skin (html/css/js) is always
      // reloaded instead of served from the static server's HTTP cache.
      const v = (typeof SKIN_ENGINE_VERSION !== 'undefined') ? SKIN_ENGINE_VERSION : Date.now();
      const src = 'Temalar/' + entry.file + '?v=' + v;
      if (skinFrame.getAttribute('src') !== src) skinFrame.setAttribute('src', src);
    }
    document.querySelectorAll('#topSkinRow [data-skin]').forEach(btn => {
      const active = btn.dataset.skin === next;
      btn.classList.toggle('active', active);
      btn.style.borderColor = active ? 'var(--cyan)' : 'rgba(255,255,255,.1)';
      btn.style.color = active ? 'var(--cyan)' : '#A7B0BF';
      btn.style.background = active ? 'rgba(var(--cyan-rgb),.12)' : 'transparent';
    });
    aogpn.events.emit('skin-changed');
    // Keep the active-skin badge in the title bar in sync whenever the skin changes.
    if (typeof renderSkinBadge === 'function') renderSkinBadge();
  }
  window.applySkin = applySkin;

  // ---- Skin manager modal ----
  // A gallery of every registered skin (plus the built-in Standard dashboard)
  // with live thumbnail previews, opened from #skinManagerBtn in the title bar.
  const STANDARD_SKIN = { id: 'standard', name: 'Standard', tagline: 'The classic AO GPN dashboard', accent: 'var(--cyan, #22d3ee)' };
  const skinManagerEl = document.getElementById('skinManager');
  const skinManagerGrid = document.getElementById('skinManagerGrid');
  const skinManagerBtn = document.getElementById('skinManagerBtn');

  function currentSkinId() { return document.body.getAttribute('data-skin') || 'standard'; }

  // Active-skin badge in the title bar: shows the current skin name with an
  // accent-colored dot; clicking it opens the skin manager.
  function renderSkinBadge() {
    const nameEl = document.getElementById('skinBadgeName');
    const dotEl = document.getElementById('skinBadgeDot');
    const current = currentSkinId();
    const entry = STANDARD_SKIN.id === current ? STANDARD_SKIN : (_skinRegistry || []).find(s => s.id === current);
    if (nameEl) nameEl.textContent = entry ? entry.name : (current === 'standard' ? 'Standard' : current);
    if (dotEl) {
      const acc = (entry && entry.accent && !String(entry.accent).startsWith('var(')) ? entry.accent : 'var(--cyan)';
      dotEl.style.background = acc;
      dotEl.style.boxShadow = '0 0 6px ' + acc;
    }
    const badge = document.getElementById('skinBadge');
    if (badge) badge.title = (entry ? entry.name : current) + ' — open skin manager (Alt+Shift+D)';
  }

  // A lightweight, always-fast static preview for a skin card. Uses the skin's
  // accent color to draw a mini schematic (title bar, sidebar, hero ring, stat
  // bars) — no <iframe>, so the gallery stays smooth even for heavy skins.
  function skinManagerMock(entry) {
    const acc = (entry.accent && !String(entry.accent).startsWith('var(')) ? entry.accent : '#22d3ee';
    const dark = '#0d111a';
    const bars = [46, 70, 55, 82, 60, 38, 74, 52].map((h, i) =>
      '<i style="height:' + h + '%;animation-delay:' + (i * .08) + 's"></i>').join('');
    return ''
      + '<div class="sm-mock" style="background:' + dark + '">'
      + '<div class="sm-mockBar"><b style="background:' + acc + ';box-shadow:0 0 8px ' + acc + '"></b><i></i><span></span><span></span><span></span></div>'
      + '<div class="sm-mockBody">'
      + '<div class="sm-mockSide"><i style="background:' + acc + '"></i><i></i><i></i><i></i></div>'
      + '<div class="sm-mockMain">'
      + '<div class="sm-mockRing" style="border-color:' + acc + ';box-shadow:0 0 22px ' + acc + '"></div>'
      + '<div class="sm-mockBars">' + bars + '</div>'
      + '</div>'
      + '</div>'
      + '</div>';
  }

  function skinManagerCard(entry, active) {
    const accent = (entry.accent && !String(entry.accent).startsWith('var(')) ? entry.accent : 'var(--cyan)';
    const useBtn = active
      ? '<button type="button" class="sm-btn used" disabled>✓ In use</button>'
      : '<button type="button" class="sm-btn use" data-sm-use="' + escHtml(entry.id) + '">Use skin</button>';
    return ''
      + '<div class="sm-card' + (active ? ' active' : '') + '" data-sm-card="' + escHtml(entry.id) + '">'
      + '<div class="sm-thumb">' + skinManagerMock(entry) + '</div>'
      + '<div class="sm-body">'
      + '<div class="sm-name"><span class="sm-dot" style="background:' + accent + ';box-shadow:0 0 8px ' + accent + '"></span>' + escHtml(entry.name) + '</div>'
      + '<div class="sm-tagline">' + escHtml(entry.tagline || '') + '</div>'
      + '<div class="sm-actions">' + useBtn + '</div>'
      + '</div>'
      + '</div>';
  }

  function renderSkinManager() {
    if (!skinManagerGrid) return;
    const current = currentSkinId();
    const list = [STANDARD_SKIN].concat(_skinRegistry || []);
    skinManagerGrid.innerHTML = list.map(e => skinManagerCard(e, e.id === current)).join('');
    // Card click = switch skin (and stay open so the user sees it applied).
    skinManagerGrid.querySelectorAll('[data-sm-card]').forEach(card => {
      card.addEventListener('click', () => { applySkin(card.dataset.smCard); renderSkinManager(); });
    });
    skinManagerGrid.querySelectorAll('[data-sm-use]').forEach(btn => {
      btn.addEventListener('click', (e) => { e.stopPropagation(); applySkin(btn.dataset.smUse); renderSkinManager(); });
    });
  }

  function openSkinManager() {
    renderSkinManager();
    if (skinManagerEl) skinManagerEl.hidden = false;
  }
  function closeSkinManager() {
    if (skinManagerEl) skinManagerEl.hidden = true;
  }
  function toggleSkinManager() {
    if (skinManagerEl && skinManagerEl.hidden) openSkinManager(); else closeSkinManager();
  }

  if (skinManagerBtn) skinManagerBtn.addEventListener('click', toggleSkinManager);
  const smClose = document.getElementById('skinManagerClose');
  if (smClose) smClose.addEventListener('click', closeSkinManager);
  const smBackdrop = document.getElementById('skinManagerBackdrop');
  if (smBackdrop) smBackdrop.addEventListener('click', closeSkinManager);
  // Clicking the active-skin badge also opens the manager.
  const skinBadgeEl = document.getElementById('skinBadge');
  if (skinBadgeEl) {
    skinBadgeEl.addEventListener('click', toggleSkinManager);
    skinBadgeEl.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleSkinManager(); } });
  }
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && skinManagerEl && !skinManagerEl.hidden) closeSkinManager();
    // Alt+Shift+D toggles the skin manager from anywhere in the shell.
    if (e.altKey && e.shiftKey && (e.key === 'D' || e.key === 'd') && skinManagerEl) {
      e.preventDefault();
      toggleSkinManager();
    }
  });
  renderSkinBadge();
  window.openSkinManager = openSkinManager;
  window.closeSkinManager = closeSkinManager;
  window.toggleSkinManager = toggleSkinManager;
  window.renderSkinManager = renderSkinManager;

  // ---- skinBridge: the API a skin iframe uses to reach the app ----
  // Properties are live getters (always current); writable ones also have
  // setters that write back to the same locals. Methods forward to the real
  // handlers, so a skin click behaves exactly like the standard dashboard's.
  const skinBridge = {
    get connected() { return connected; },
    get connecting() { return connecting; },
    get mode() { return mode; },
    get transport() { return transport; },
    get splitMode() { return splitMode; },
    get systemProxyMode() { return systemProxyMode; },
    get protocolPreference() { return protocolPreference; },
    get autoReconnect() { return autoReconnect; },
    get useRealNodes() { return aogpn.nodes.getUseRealNodes(); },
    get nodes() { return aogpn.nodes.getNodesList(); },
    get realNodes() { return aogpn.nodes.getRealNodes(); },
    get NODES() { return aogpn.nodes.getNodes(); },
    get activeRealNodeId() { return aogpn.nodes.getActiveRealNodeId(); },
    get selectedNode() { return aogpn.nodes.getSelectedNode(); },
    get monitorSnapshot() { return monitorSnapshot; },
    get processCatalog() { return processCatalog || []; },
    // Live shell-icon cache (exe path -> data URI): the skins render the same
    // real app icons as the main tables. The object is mutated in place, so
    // the getter always returns the current map; skins read it at render time.
    get appIcons() { return appIconCache; },
    get routeLabels() { return aogpn.views.getRouteLabels(); },
    get language() { return aogpn.i18n.getLang(); },
    get gpnServers() { return aogpn.gpn.getServers(); },
    get gpnServerProbes() { return aogpn.gpn.getServerProbes(); },
    get tunStack() {
      const s = document.getElementById('captureTunStack');
      return (s && s.value) || 'mixed';
    },
    get appInfo() { return aogpn.releaseNotes.getAppInfo(); },
    get effectsTier() { return aogpn.theme.getEffectsTier(); },
    // True while the app is not visible; skins can pause their own loops too.
    get frozen() { return aogpn.theme.isEffectsHidden(); },
    get gpnRecoveryWatch() { return aogpn.gpn.getRecoveryWatch(); },
    get gpnFailover() { return aogpn.gpn.getFailover(); },
    get proxyTestResult() { return _proxyTestResult || null; },
    // ---- GPN panel / Advanced & Diagnostics (live host state) ----
    get gpnConnectState() { return aogpn.gpn.getConnectState(); },
    get gpnPidPool() { return aogpn.gpn.getPidPool(); },
    get gpnCaptureSettings() { return aogpn.gpn.getCaptureSettings(); },
    get gpnWintunSettings() { return aogpn.gpn.getWintunSettings(); },
    get gpnCaptureStats() { return aogpn.gpn.getCaptureStats(); },
    get gpnFailoverMatrix() { return aogpn.gpn.getFailoverMatrix(); },
    get gpnSelectionPrediction() { return aogpn.gpn.getSelectionPrediction(); },
    get gpnTelemetry() { return aogpn.gpn.getTelemetrySnap(); },
    get gpnResilienceLog() { return aogpn.gpn.getResilienceLog(); },
    get gpnDiagLines() { return aogpn.gpn.getDiagLines(); },
    get connectionError() { return aogpn.connection.getConnectionError(); },
    get realIpState() { return _lastIpState || null; },
    get systemProxyState() { return { desired: systemProxyMode, effective: effectiveSystemProxyMode, owned: systemProxyConnectionOwned }; },
    get activeGpnServer() { return _activeGpnServer || ''; },
    get sessionTime() { return aogpn.telemetry.formatSessionTime(); },
    get exitIp() { return (ipDisplay && connected) ? ipDisplay.textContent : undefined; },
    get exitCountry() { return (ipCountry && connected) ? ipCountry.textContent : undefined; },
    get telemetry() { return _lastTelemetry || [null, null, null, null]; },
    get isAdmin() { return isAdmin; },
    set connected(v) { connected = !!v; },
    set connecting(v) { connecting = !!v; },
    set mode(v) { mode = v; },
    set transport(v) { transport = v; },
    set selectedNode(v) { aogpn.nodes.setSelectedNode(v); },
    set splitMode(v) { splitMode = v; },
    set autoReconnect(v) { autoReconnect = !!v; },
    set systemProxyMode(v) { systemProxyMode = v; },
    set protocolPreference(v) { protocolPreference = v; },
    set useRealNodes(v) { aogpn.nodes.setUseRealNodes(!!v); },
    postToHost,
    requestNodeSwitch: (indexId) => aogpn.nodes.requestNodeSwitch(indexId),
    applyNode: () => aogpn.nodes.applyNode(),
    setConnected: (next) => aogpn.connection.setConnected(!!next, false),
    applySkin,
    subscribe(cb) { if (typeof cb === 'function') _skinSubscribers.push(cb); },
    // ---- language support ----
    // Resolve a UI string for the active skin. Skin-specific keys live under
    // 'skin.<skinId>.<key>' in Dil/*.json (so each skin owns its texts and
    // translators never touch the main dashboard keys); shared keys such as
    // 'mode.gpn' or 'status.connected' still resolve as a fallback. Unknown
    // keys return the key itself so the skin can fall back to its own default.
    t(key, params, skinId) {
      const sid = skinId || document.body.getAttribute('data-skin') || 'standard';
      const empty = (v) => (v === null || v === undefined || v === '');
      // Skin-scoped keys (skin.<sid>.<key>) win; everything else falls back to
      // the shared resolveKey() chain exactly as the dashboard t() does.
      let val = sid !== 'standard' ? resolveKey('skin.' + sid + '.' + key, params) : null;
      if (empty(val)) val = resolveKey(key, params);
      return empty(val) ? key : val;
    },
    // Switch the whole app (and every loaded skin) to another language, exactly
    // like the top-bar picker: apply instantly and persist through the host.
    setLanguage(lang) {
      if (!lang || !LANGUAGES.some(l => l.code === lang)) return;
      applyLanguage(lang);
      postToHost({ action: 'set_language', lang });
    },
    get LANGUAGES() { return LANGUAGES; },
    langName(code) { return langName(code); }
  };
  window.skinBridge = skinBridge;



  if (skinFrame) skinFrame.addEventListener('load', () => { if (aogpn.theme.isEffectsHidden()) aogpn.theme.applyEffectsHiddenNow(); });

  // Keep legacy aliases (used by the old inline fragment and debug consoles).
  window.escHtml = escHtml;
  window.postToHost = postToHost;
  window.requestNodeSwitch = (indexId) => aogpn.nodes.requestNodeSwitch(indexId);
  window.applyNode = () => aogpn.nodes.applyNode();
  window.setNexusConnected = (next) => aogpn.connection.setConnected(!!next, false);
  // Host signals an in-flight connection attempt: the button locks (amber
  // CONNECTING state) until the attempt resolves to connected or idle.
  window.setNexusConnecting = (next) => aogpn.connection.setConnected(false, !!next);

  // ---- App services consumed by the core modules (i18n re-render hooks) ----
  window.aogpn = window.aogpn || {};
  window.aogpn.app = {
    applyMode: () => aogpn.connection.applyMode(),
    updateTransportLock: () => aogpn.connection.updateTransportLock(),
    applyProtocolPreference: (next) => aogpn.connection.applyProtocolPreference(next),
    renderTopNodeSelect: () => aogpn.nodes.renderTopNodeSelect(),
    updateTelemetry,
    applyRealIpState: (data) => aogpn.connection.applyRealIpState(data),
    updateStatusLine: () => aogpn.connection.updateStatusLine(),
    applyIpFreshness: () => aogpn.connection.applyIpFreshness(),
    renderDashboardBoostCards: () => aogpn.views.renderDashboardBoostCards(),
    updateGlobalPanel: () => aogpn.views.updateGlobalPanel(),
    renderSplitApps: () => aogpn.views.renderSplitApps(),
    getProtocolPreference: () => protocolPreference,
    getMode: () => mode,
    getTransport: () => transport,
    getConnected: () => connected,
    getTunLocked: () => tunLocked,
    setTunLockedRaw: (v) => { tunLocked = v; },
    setConnected: (next, nextConnecting) => aogpn.connection.setConnected(next, nextConnecting),
    setConnectedRaw: (v) => { connected = v; },
    getLastIpState: () => _lastIpState,
    getLastTelemetry: () => _lastTelemetry,
    setLastTelemetry: (sample) => { _lastTelemetry = sample; },
    getCurrentView: () => currentView,
    setCurrentView: (v) => { currentView = v; },
    getInvertManual: () => invertManual,
    setInvertManual: (v) => { invertManual = v; },
    getSplitMode: () => splitMode,
    setSplitMode: (v) => { splitMode = v; },
    setMode: (v) => { mode = v; },
    getMonitorSnapshot: () => monitorSnapshot,
    setMonitorSnapshot: (m) => { monitorSnapshot = m; },
    getProcessCatalog: () => processCatalog,
    setProcessCatalog: (p) => { processCatalog = p; },
    getActiveGpnServer: () => _activeGpnServer,
    setActiveGpnServerRaw: (v) => { _activeGpnServer = v; },
    getActiveGpnMode: () => _activeGpnMode,
    setActiveGpnModeRaw: (v) => { _activeGpnMode = v; },
    getUseRealNodes: () => aogpn.nodes.getUseRealNodes(),
    getRealNodes: () => aogpn.nodes.getRealNodes(),
    getNodes: () => aogpn.nodes.getNodes(),
    getSystemProxyMode: () => systemProxyMode,
    getConnecting: () => connecting,
    getAutoReconnect: () => autoReconnect,
    getEffectiveSystemProxyMode: () => effectiveSystemProxyMode,
    getSystemProxyConnectionOwned: () => systemProxyConnectionOwned,
    getSystemProxyAppliedAddress: () => systemProxyAppliedAddress,
    getPrevSystemProxyConnectionOwned: () => prevSystemProxyConnectionOwned,
    getIsAdmin: () => isAdmin,
    getLastIpMeasuredAt: () => _lastIpMeasuredAt,
    setConnectingRaw: (v) => { connecting = v; },
    setAutoReconnectRaw: (v) => { autoReconnect = v; },
    setSystemProxyModeRaw: (v) => { systemProxyMode = v; },
    setEffectiveSystemProxyModeRaw: (v) => { effectiveSystemProxyMode = v; },
    setSystemProxyConnectionOwnedRaw: (v) => { systemProxyConnectionOwned = v; },
    setSystemProxyAppliedAddressRaw: (v) => { systemProxyAppliedAddress = v; },
    setPrevSystemProxyConnectionOwnedRaw: (v) => { prevSystemProxyConnectionOwned = v; },
    setIsAdminRaw: (v) => { isAdmin = v; },
    setProtocolPreferenceRaw: (v) => { protocolPreference = v; },
    setTransportRaw: (v) => { transport = v; },
    setLastIpStateRaw: (v) => { _lastIpState = v; },
    setLastIpMeasuredAtRaw: (v) => { _lastIpMeasuredAt = v; },
    syncQuickControls: () => aogpn.connection.syncQuickControls(),
    isProxyModeEnabled: (v) => aogpn.connection.isProxyModeEnabled(v)
  };

  // Load the registry and apply the persisted skin.
  loadSkinRegistry();

  // Upgrade every native <select> to the themed dropdown (top-bar node picker,
  // system proxy mode, protocol selects, TUN stack, node sort and every
  // settings select). The original elements stay in the DOM hidden, so all the
  // change listeners bound above keep working through the dispatched events.
  [
    'topNodeSelect', 'systemProxyModeSelect', 'quickProtocolSelect',
    'mobileProtocolSelect', 'captureTunStack', 'nodeSortSel'
  ].forEach(id => { const el = document.getElementById(id); if (el) upgradeSelect(el); });
  document.querySelectorAll('.settings-select').forEach(el => upgradeSelect(el));
  refreshAllCustomSelects();

  aogpn.nodes.renderNodes();
  aogpn.nodes.applyNode();
  aogpn.nodes.renderTopNodeSelect();
  aogpn.connection.applyMode();
  aogpn.connection.applyTransport();
  aogpn.connection.applyProtocolPreference(protocolPreference);
  aogpn.connection.applySystemProxyState(systemProxyMode, effectiveSystemProxyMode, false);
  aogpn.views.applySplitMode();
  aogpn.views.renderMonitorConnections();
  aogpn.views.renderSplitApps();
  aogpn.views.renderDashboardBoostCards();
  resetTelemetry();
  aogpn.views.applyDirection();

  // GPN panel "Gelişmiş & Teşhis" (Advanced & Diagnostics): the rarely-used
  // server tuning, failover, capture/Wintun internals, telemetry and diagnostics
  // cards live in a collapsible group so the main GPN flow stays clean. The host
  // keeps feeding the cards inside; state is remembered between launches.
  // Prime the node pool panel and its links as soon as the page is ready; the
  // host re-pushes after every add/remove/fetch.
  postToHost({ action: 'get_node_pool' });
  aogpn.nodes.renderNodePool();
  // GPN panel "sunucu kümesi" kartını besle: sunucu listesini iste ve canlı
  // gecikme ölçümü döngüsünü başlat (panel görünürken 15 sn'de bir ölçer).
  postToHost({ action: 'gpn_servers_list' });
  startGpnClusterLoop();
})();
