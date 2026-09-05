/* ==========================================================================
   AoGPN dashboard — application script (bölüm haritası)
   --------------------------------------------------------------------------
   Tüm uygulama mantığı burada. Satır numaraları yaklaşıktır; bir bölümü
   bulmak için Ctrl+F ile aşağıdaki anahtar işlev adlarını ara.

   ~1-120    Durum & element referansları    $, bağlantı/mod/transport,
                                             düğüm listesi, seçim, görünüm
   ~121-235  Telemetri & göstergeler         setGauge, updateTelemetry,
                                             resetTelemetry, tickSession,
                                             setConnected
   ~235-265  Bağlantı etiketleri             updateConnectTooltip,
                                             updateStatusLine
   ~265-435  Mod / transport / protokol      applyMode, applyTransport,
                                             applyProtocolPreference,
                                             applySystemProxyState
   ~455-640  Düğüm kartları & seçim          realNodeCard, applyRealNode,
                                             renderTopNodeSelect,
                                             requestNodeSwitch, sortedRealNodes
   ~640-827  Nodes görünümü                 renderNodes, seçim UI,
                                             sıralama, edit modu
   ~837-918  Düğüm bağlam menüsü / onay     showNodeCtxMenu, requestConfirm,
                                             requestNodeDelete, notifyNodes
   ~918-1420 Görünümler / izleme / split    showView, routeBadge,
                                             renderMonitorConnections,
                                             renderDashboardBoostCards,
                                             renderSplitApps, applySplitMode
   ~1420-1528 Ayarlar formu                 fillSettingsOptions,
                                             setSettingsField, applySettings
   ~1528-1780 Tema motoru                   THEMES (yedek), themes.json
                                             yükleme, applyTheme,
                                             imleç/ses/konfeti efektleri
   ~1781-2039 i18n (Dil klasörü)            t(), loadLanguage,
                                             applyLanguage, applyTexts,
                                             renderTopThemePopover
   ~2072-2299 Host setter'ları              setConnectionState,
                                             setRealIpState, setTransport,
                                             setSystemProxyState
   ~2299-2568 Düğüm listesi / havuz / test  updateNodeList*,
                                             updateNodePool, renderNodePool,
                                             setNodeSwitchResult,
                                             setProxyTestResult
   ~2568-2663 Pencere kontrolleri           renderMaximizeIcon,
                                             setWindowMaximized
   ~2663-2706 Game Boost sürükle-bırak      exe ekleme (drop)
   ~2706-2871 Özel tooltip'ler + init       tooltip motoru, MutationObserver,
                                             ilk açılış akışı
   ========================================================================== */
(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const body = document.body;
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
  let sessionSec = 0;
  let sessionTimer = null;
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
        visibleView: currentView, theme: _currentThemeId || ''
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


  function hasWebViewBridge() {
    return Boolean(
      window.chrome
      && window.chrome.webview
      && typeof window.chrome.webview.postMessage === 'function'
    );
  }

  function postToHost(message) {
    if (!hasWebViewBridge()) {
      return false;
    }

    try {
      // Send a JSON string so the WPF side can reject non-string/oversized payloads.
      window.chrome.webview.postMessage(JSON.stringify(message));
      return true;
    } catch {
      return false;
    }
  }

  const btnIcon = $('btnIcon'), btnText = $('btnText'), btnSub = $('btnSub'),
        statusDot = $('statusDot'), statusLabel = $('statusLabel'),
        nodeName = $('nodeName'), nodeAddr = $('nodeAddr'),
        ipDisplay = $('ipDisplay'), ipCountry = $('ipCountry'),
        ipVerifyText = $('ipVerifyText'), ipVerifyDetail = $('ipVerifyDetail'),
        ipVerifyCard = $('ipVerifyCard'),
        ipLeakBanner = $('ipLeakBanner'), ipLeakText = $('ipLeakText'),
        ipOkBanner = $('ipOkBanner'), ipOkText = $('ipOkText'),
        ipOkTunnelIp = $('ipOkTunnelIp'), ipOkIspIp = $('ipOkIspIp'),
        pingVal = $('pingVal'), pingSub = $('pingSub'),
        lossVal = $('lossVal'), lossSub = $('lossSub'),
        downVal = $('downVal'), upVal = $('upVal'),
        downGauge = $('downGauge'), upGauge = $('upGauge'),
        systemProxyToggleBtn = $('systemProxyToggleBtn'), systemProxyDot = $('systemProxyDot'),
        systemProxyLabel = $('systemProxyLabel'), systemProxyAddress = $('systemProxyAddress'), systemProxyModeSelect = $('systemProxyModeSelect'),
        proxyTestBtn = $('proxyTestBtn'), proxyTestIcon = $('proxyTestIcon'), proxyTestLabel = $('proxyTestLabel'),
        quickProtocolSelect = $('quickProtocolSelect'),
        quickAutoReconnect = $('quickAutoReconnect'),
        protocolHint = $('protocolHint'),
        routeHint = $('routeHint'), transportHint = $('transportHint'),
        connectionStatusLine = $('connectionStatusLine');

  const CIRC = 276.46;

  // The real AoGPN profile published by the WPF host; null keeps the static
  // concept node list as the standalone-preview fallback.
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
  const NODE_GROUPS_STORAGE_KEY = 'aogpn.routeGroups';
  let collapsedNodeGroups = new Set(JSON.parse(localStorage.getItem(NODE_GROUPS_STORAGE_KEY) || '[]'));

  // Node pool: links (GitHub raw .txt / subscription endpoints) published by the
  // host; the user pulls shared nodes from them with one click.
  let nodePoolLinks = [];
  let currentView = 'dashboard';
  let splitMode = 'off';
  // "Reduce effects" (motion budget): user switch from Settings, OR-ed with the
  // OS prefers-reduced-motion flag; the host config is pushed via setReduceEffects.
  let reduceEffects = false;
  // GPN routing direction: false = whitelist (only listed apps are tunneled),
  // true = blacklist (listed apps stay direct, everything else tunnels).
  let invertManual = false;
  let monitorSnapshot = { connections: [], apps: [], traffic: [] };
  let pendingConfirmAction = null;
  // Edit-mode gate: destructive list edits (delete, disable, dedupe, cleanup)
  // only run while edit mode is on, mirroring AoGPN's edit concept.
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
  let processCatalog = [];

  // Adaptive gauge calibration. The old code divided by hard-coded 25/12 Mbps,
  // so a 200 Mbps fibre link pegged the dial at full while an 8 Mbps line barely
  // moved it. Instead the scale follows the observed peak: it grows instantly to
  // a "nice" round value above the current sample, then decays slowly back toward
  // the floor while the link is idle. The floor keeps low-speed links legible.
  const NICE_SCALES = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];
  let downScale = 25; // Mbps, current download gauge full-scale
  let upScale = 12;   // Mbps, current upload gauge full-scale

  function nextNiceScale(mbps, floor) {
    for (const s of NICE_SCALES) {
      if (s >= mbps * 1.15 && s >= floor) {
        return s;
      }
    }
    return NICE_SCALES[NICE_SCALES.length - 1];
  }

  function calibrateGauge(currentScale, mbps, floor) {
    // Grow fast: a sample near/over the current full-scale picks the next
    // nice scale above it (with 15% headroom so the dial never pegs).
    if (mbps >= currentScale * 0.85) {
      return nextNiceScale(mbps, floor);
    }
    // Shrink in nice steps, only when the link is meaningfully below the
    // current full-scale — steady traffic keeps a stable scale instead of
    // decaying every sample.
    if (mbps < currentScale * 0.5 && currentScale > floor) {
      let candidate = floor;
      for (const s of NICE_SCALES) {
        if (s < currentScale && s >= floor) {
          candidate = s;
        }
      }
      // Only step down if the value still reads well on the smaller scale.
      return mbps < candidate * 0.85 ? candidate : currentScale;
    }
    return currentScale;
  }

  function setGauge(g, frac, color) {
    g.style.strokeDashoffset = CIRC * (1 - Math.min(1, Math.max(0, frac)));
    g.style.color = color;
  }

  function applyGaugeScale(el, scale) {
    if (el) el.textContent = String(Math.round(scale));
  }

  /**
   * Updates the live cards. In WebView2, all four telemetry values are supplied by C#;
   * standalone browser previews remain in an explicit measuring/zero state.
   */
  // Hız testi (sunucu kullanılabilirlik ölçümü) sonucu yakın zamanda geldiyse
  // (setAvailabilityInfo) canlı telemetri örneği (updateTelemetry) ping kartını
  // EZMEZ — kullanıcı ölçümü okuyabilsin. Pencere dolunca canlı değerler devreye
  // girer. Yalnızca gerçek gecikme ölçülen sonuçlar tutulur (başarısız ölçüm "--"
  // canlı değerlerden daha az bilgilidir).
  const AVAILABILITY_HOLD_MS = 10000;
  let _availabilityHoldUntil = 0;

  function updateTelemetry(pingFromHost, lossFromHost, downFromHost, upFromHost) {
    _lastTelemetry = [pingFromHost, lossFromHost, downFromHost, upFromHost];
    window.__nxTelemetry = _lastTelemetry;
    if (!connected) {
      setGauge(downGauge, 0, 'var(--cyan)');
      setGauge(upGauge, 0, 'var(--violet)');
      return;
    }

    // The desktop host supplies explicit values (or null while a sample is pending).
    // Undefined arguments mean this is a standalone browser preview.
    const hasHostSample = [pingFromHost, lossFromHost, downFromHost, upFromHost]
      .some(value => value !== undefined);
    const availabilityHeld = Date.now() < _availabilityHoldUntil;
    let dl;
    let ul;

    if (hasHostSample) {
      const hasPing = Number.isFinite(pingFromHost) && pingFromHost > 0;
      const hasLoss = Number.isFinite(lossFromHost) && lossFromHost >= 0;
      const hasDownload = Number.isFinite(downFromHost) && downFromHost >= 0;
      const hasUpload = Number.isFinite(upFromHost) && upFromHost >= 0;

      // Hız testi sonucu bekleme penceresindeyken VEYA GPN modunda ping kartını
      // canlı örnekle ezme — GPN'de HTTP ping'i tünel gecikmesini değil, doğrudan
      // CDN gecikmesini ölçer (split tunnel isteği tünel dışına gider). Doğru
      // gecikme değeri setGpnConnectionInfo tarafından sunucu seçim ölçümünden gelir.
      // kayıp/indirme/yükleme yine de canlı güncellenir.
      const skipPingUpdate = availabilityHeld || mode === 'gpn';
      if (!skipPingUpdate) {
        pingVal.textContent = hasPing ? String(Math.round(pingFromHost)) : '--';
        pingSub.textContent = hasPing
          ? (pingFromHost <= 10 ? t('telemetry.excellent') : pingFromHost <= 30 ? t('telemetry.good') : pingFromHost <= 60 ? t('telemetry.medium') : t('telemetry.slow'))
          : t('telemetry.measuring');
      }
      lossVal.textContent = hasLoss ? Number(lossFromHost).toFixed(1) : '--';
      lossSub.textContent = hasLoss
        ? (Number(lossFromHost) === 0 ? t('telemetry.stable') : t('telemetry.warning'))
        : t('telemetry.measuring');

      dl = hasDownload ? Number(downFromHost) : 0;
      ul = hasUpload ? Number(upFromHost) : 0;
    } else {
      // Without the WPF host there is no trustworthy sample. Keep the cards in an
      // explicit measuring state rather than inventing throughput or latency values.
      dl = 0;
      ul = 0;
      if (!availabilityHeld && mode !== 'gpn') {
        pingVal.textContent = '--';
        pingSub.textContent = t('telemetry.measuring');
      }
      lossVal.textContent = '--';
      lossSub.textContent = t('telemetry.measuring');
    }

    downVal.textContent = dl.toFixed(1);
    upVal.textContent = ul.toFixed(1);
    // Adaptive full-scale: grows with the observed peak, decays while idle.
    downScale = calibrateGauge(downScale, dl, 25);
    upScale = calibrateGauge(upScale, ul, 12);
    setGauge(downGauge, dl / downScale, 'var(--cyan)');
    setGauge(upGauge, ul / upScale, 'var(--violet)');
    applyGaugeScale(document.getElementById('downGaugeMax'), downScale);
    applyGaugeScale(document.getElementById('upGaugeMax'), upScale);
    updateGlobalPanel();
    skinNotify(); // refresh standalone skins with the latest telemetry sample
  }

  function resetTelemetry() {
    // Bağlantı kapandı — hız testi sonucunun bekleme penceresi de biter (eski
    // ölçüm yeni oturuma taşınmaz).
    _availabilityHoldUntil = 0;
    pingVal.textContent = '--'; lossVal.textContent = '--';
    downVal.textContent = '0'; upVal.textContent = '0';
    pingSub.textContent = t('telemetry.measuring'); lossSub.textContent = t('telemetry.stable');
    downScale = 25; upScale = 12;
    setGauge(downGauge, 0, 'var(--cyan)');
    setGauge(upGauge, 0, 'var(--violet)');
    applyGaugeScale(document.getElementById('downGaugeMax'), downScale);
    applyGaugeScale(document.getElementById('upGaugeMax'), upScale);
  }

  function tickSession() {
    sessionSec++;
    const h = String(Math.floor(sessionSec / 3600)).padStart(2, '0');
    const m = String(Math.floor((sessionSec % 3600) / 60)).padStart(2, '0');
    const s = String(sessionSec % 60).padStart(2, '0');
    $('sessionTime').textContent = `${h}:${m}:${s}`;
    // IP ölçüm tazeliğini saniyede bir yenile (yaş artar, bayat sızıntı amber'e düşer).
    if (_lastIpState) applyIpFreshness();
  }

  function setConnected(next, nextConnecting) {
    const wasConnected = connected;
    connected = next;
    connecting = nextConnecting === true && !connected;
    body.classList.toggle('connected', connected);
    // The CONNECT ring only animates during a connection attempt (see
    // components.css `body.connecting` gates); idle and connected are static so
    // the WebView2 compositor has nothing to re-render at 60 fps.
    body.classList.toggle('connecting', connecting);
    if (next !== wasConnected) {
      if (!next) {
        // Bağlantı kesilince düğüm etiketi bayat kalmasın (önce/sonra değerleri
        // belgeli olarak son ölçümde kalır, ancak "via" hangi düğüm olduğunu
        // yalnızca bağlantı sırasında anlamlıdır).
        _activeGpnServer = '';
        if (monitorSnapshot && (monitorSnapshot.apps || []).length) {
          renderDashboardBoostCards();
          if (currentView === 'boost') renderSplitApps();
        }
      }
    }
    const cyan = 'var(--cyan)', emerald = 'var(--emerald)';
    statusDot.style.background = connected ? emerald : connecting ? 'var(--amber, #f59e0b)' : cyan;
    statusDot.style.color = connected ? emerald : connecting ? 'var(--amber, #f59e0b)' : cyan;
    // The sidebar footer dot reflects the real connection state (was hardcoded green).
    const sidebarDot = document.getElementById('sidebarStatusDot');
    if (sidebarDot) {
      sidebarDot.style.background = connected ? emerald : cyan;
      sidebarDot.style.color = connected ? emerald : cyan;
      sidebarDot.title = connected ? t('status.connected') : t('status.disconnected');
    }
    statusLabel.textContent = connected ? t('status.connected') : connecting ? t('status.connecting') : t('status.disconnected');
    statusLabel.className = 'font-medium ' + (connected ? 'text-emerald-300' : connecting ? 'text-amber-300' : 'text-cyan-300');
    // The mobile quick-connect button mirrors the CONNECT ring state.
    const mobileConnectBtn = $('mobileConnectBtn');
    if (mobileConnectBtn) {
      mobileConnectBtn.classList.toggle('connected', connected);
      mobileConnectBtn.style.color = connected ? emerald : connecting ? 'var(--amber, #f59e0b)' : cyan;
      mobileConnectBtn.setAttribute('aria-label', connected ? 'Disconnect' : connecting ? 'Connecting' : 'Connect');
      // Locked while connecting (no double-toggle) or TUN lacks elevation.
      mobileConnectBtn.disabled = tunLocked || connecting;
    }
    // CONNECT is locked while connecting: the attempt is in flight, so a second
    // press must not fire another toggle_connection before the host resolves it.
    if (connectBtn) {
      const locked = tunLocked || connecting;
      connectBtn.disabled = locked;
      connectBtn.setAttribute('aria-disabled', String(locked));
      connectBtn.classList.toggle('cursor-not-allowed', locked);
    }
    btnText.textContent = connected ? t('connect.connected') : connecting ? t('connect.connecting') : t('connect.connect');
    btnIcon.style.color = connected ? emerald : connecting ? 'var(--amber, #f59e0b)' : cyan;
    btnIcon.style.filter = connected ? 'drop-shadow(0 0 16px rgba(var(--emerald-rgb),.7))' : 'drop-shadow(0 0 16px rgba(var(--cyan-rgb),.7))';
    // Swap the SVG ring arc colour
    var arc = document.getElementById('ringArc');
    if (arc) arc.setAttribute('stroke', connected ? emerald : cyan);
    btnSub.textContent = (mode === 'gpn' ? t('mode.gpn') : t('mode.globalVpn')) + (connected ? ' · ' + t('mode.active') : connecting ? ' · ' + t('status.connecting') : '');

    if (connected) {
      showConnectionError('');
      if (!sessionTimer) sessionTimer = setInterval(tickSession, 1000);
      sessionSec = 0; $('sessionTime').textContent = '00:00:00';
      // Bağlıyken IP paneli kendini periyodik yeniden ölçer (bayat sızıntı uyarısı
      // en geç 25 sn'de düzeltilir — tarayıcı yenilemesi gerekmez).
      startIpRecheckLoop();
      playThemeSound(_currentThemeId); if (typeof fireConfetti === 'function') fireConfetti();
    } else {
      if (sessionTimer) { clearInterval(sessionTimer); sessionTimer = null; }
      stopIpRecheckLoop();
      resetTelemetry();
      // Clear IP verification banners and reset the IP display so stale
      // tunnel-verified state doesn't linger after disconnecting.
      if (ipLeakBanner) ipLeakBanner.classList.add('hidden');
      if (ipOkBanner) ipOkBanner.classList.add('hidden');
      if (ipVerifyText) { ipVerifyText.textContent = t('status.idle'); ipVerifyText.className = 'text-sm font-semibold truncate mt-1 text-slate-400'; }
      if (ipVerifyDetail) ipVerifyDetail.textContent = t('status.notConnected');
      // Don't clear ipDisplay here — the next CheckIpAsync run will overwrite
      // it with the real ISP IP. Keeping the last-known value prevents flicker.
    }
    updateConnectTooltip();
    updateStatusLine();
    updateGlobalPanel();
    updateTunProxyNotice();
    refreshSessionNode();
    // Push the new state to every loaded standalone skin (skinBridge.subscribe).
    skinNotify();
  }

  function updateConnectTooltip() {
    const routeLabel = mode === 'gpn' ? t('mode.gpn') : t('mode.globalVpn');
    const captureLabel = transport === 'tun' ? t('transport.tun') : t('transport.proxy');
    connectBtn.title = connected
      ? t('tip.connectActive')
      : connecting
        ? t('status.connecting')
        : t('tip.connectIdle', { route: routeLabel, capture: captureLabel });
    statusLabel.title = connected
      ? t('tip.statusActive', { route: routeLabel, capture: captureLabel })
      : connecting
        ? t('status.connecting')
        : t('tip.statusIdle');
    const mobileConnectBtn = $('mobileConnectBtn');
    if (mobileConnectBtn) {
      mobileConnectBtn.title = connected
        ? t('tip.connectActive')
        : t('tip.connectIdle', { route: routeLabel, capture: captureLabel });
    }
  }

  /** Live readout of what the connection is doing: route + capture + state. */
  function updateStatusLine() {
    if (!connectionStatusLine) {
      return;
    }
    const routeLabel = mode === 'gpn' ? 'GPN Game Tunnel' : 'Global VPN';
    const captureLabel = transport === 'tun' ? 'TUN' : 'Proxy';
    if (connected) {
      connectionStatusLine.className = 'text-xs text-emerald-300 leading-relaxed mt-4';
      connectionStatusLine.innerHTML = t('status.line.live', { route: '<span class="font-medium">' + routeLabel + '</span>', capture: '<span class="font-medium">' + captureLabel + '</span>' });
    } else if (connecting) {
      connectionStatusLine.className = 'text-xs text-amber-300 leading-relaxed mt-4';
      connectionStatusLine.textContent = t('status.connecting');
    } else if (connecting) {
      connectionStatusLine.className = 'text-xs text-amber-300 leading-relaxed mt-4';
      connectionStatusLine.textContent = t('status.connecting');
    } else if (tunLocked) {
      connectionStatusLine.className = 'text-xs text-amber-300 leading-relaxed mt-4';
      connectionStatusLine.innerHTML = t('status.line.locked', { capture: '<span class="font-medium">' + t('transport.proxy') + '</span>' });
    } else {
      connectionStatusLine.className = 'text-xs text-[#8A94A6] leading-relaxed mt-4';
      connectionStatusLine.innerHTML = t('status.line.idle', { route: '<span class="text-cyan-300 font-medium">' + routeLabel + '</span>', capture: '<span class="text-cyan-300 font-medium">' + captureLabel + '</span>', connect: '<span class="text-cyan-300 font-medium">' + t('connect.connect') + '</span>' });
    }
  }

  function applyMode() {
    document.querySelectorAll('.mode-pill[data-mode]').forEach(p => {
      const on = p.dataset.mode === mode;
      p.classList.toggle('active', on);
      p.classList.toggle('text-cyan-300', on);
      p.classList.toggle('text-[#A7B0BF]', !on);
    });
    if (connected) btnSub.textContent = (mode === 'gpn' ? t('mode.gpn') : t('mode.globalVpn')) + ' · ' + t('mode.active');
    else btnSub.textContent = mode === 'gpn' ? t('mode.gpn') : t('mode.globalVpn');
    // Global VPN forces TUN transport to capture ALL traffic.
    if (mode === 'vpn') {
      transport = 'tun';
      applyTransport();
      // Disable transport pills when in Global VPN mode and explain why.
      document.querySelectorAll('.transport-pill').forEach(p => {
        p.disabled = true;
        p.classList.add('opacity-50', 'cursor-not-allowed');
      });
      const proxyLock = document.getElementById('proxyLockBadge');
      if (proxyLock) proxyLock.classList.remove('hidden');
      const capLock = document.getElementById('vpnCaptureLockNotice');
      if (capLock) capLock.classList.remove('hidden');
      const capLockText = document.getElementById('vpnCaptureLockText');
      if (capLockText) capLockText.textContent = t('transport.lockGlobalVpn');
      if (routeHint) routeHint.textContent = t('dash.routeHintVpn');
    } else {
      // Re-enable transport pills in GPN mode.
      document.querySelectorAll('.transport-pill').forEach(p => {
        p.disabled = false;
        p.classList.remove('opacity-50', 'cursor-not-allowed');
      });
      const proxyLock = document.getElementById('proxyLockBadge');
      if (proxyLock) proxyLock.classList.add('hidden');
      const capLock = document.getElementById('vpnCaptureLockNotice');
      if (capLock) capLock.classList.add('hidden');
      if (routeHint) routeHint.textContent = t('dash.routeHintGpn');
    }
    // Toggle the mode-swap panels below.
    var pG=document.getElementById('panelGPN');
    var pV=document.getElementById('panelGlobal');
    if(pG&&pV){
      if(mode==='gpn'){
        pV.classList.add('hidden-panel');pV.classList.remove('active-panel');
        pG.classList.remove('hidden-panel');pG.classList.add('active-panel');
      }else{
        pG.classList.add('hidden-panel');pG.classList.remove('active-panel');
        pV.classList.remove('hidden-panel');pV.classList.add('active-panel');
      }
    }
    updateConnectTooltip();
    updateStatusLine();
    updateGlobalPanel();
  }

  function applyTransport() {
    document.querySelectorAll('.transport-pill').forEach(p => {
      const on = p.dataset.transport === transport;
      p.classList.toggle('active', on);
      p.classList.toggle('text-cyan-300', on);
      p.classList.toggle('text-[#A7B0BF]', !on);
    });
    updateTransportLock();
    syncQuickControls();
  }

  /**
   * Locks CONNECT when TUN is selected without elevation: the host would reject
   * the tunnel anyway, so the pill, hint and CONNECT button show the requirement
   * up front instead of failing only after the press.
   */
  function updateTransportLock() {
    tunLocked = transport === 'tun' && !isAdmin && !connected;
    const tunPill = document.getElementById('tunPill');
    const lockBadge = document.getElementById('tunLockBadge');
    const notice = document.getElementById('tunAdminNotice');
    if (tunPill) tunPill.classList.toggle('locked', tunLocked);
    if (lockBadge) lockBadge.classList.toggle('hidden', !tunLocked);
    if (notice) notice.classList.toggle('hidden', !tunLocked);
    const btnLocked = tunLocked || connecting;
    connectBtn.disabled = btnLocked;
    connectBtn.setAttribute('aria-disabled', String(btnLocked));
    connectBtn.classList.toggle('cursor-not-allowed', btnLocked);
    // The mobile quick-connect button obeys the same lock (TUN elevation OR
    // an in-flight connecting attempt).
    const mobileConnectBtn = $('mobileConnectBtn');
    if (mobileConnectBtn) {
      mobileConnectBtn.disabled = btnLocked;
      mobileConnectBtn.classList.toggle('cursor-not-allowed', btnLocked);
      mobileConnectBtn.classList.toggle('opacity-50', btnLocked);
    }
    if (tunLocked) {
      connectBtn.title = t('tip.connectLocked');
      if (transportHint) transportHint.textContent = t('transport.hintLocked');
      const mobileConnectBtn = $('mobileConnectBtn');
      if (mobileConnectBtn) mobileConnectBtn.title = t('tip.connectLocked');
    } else {
      updateConnectTooltip();
      if (transportHint) transportHint.textContent = transport === 'tun'
        ? t('transport.hintTun')
        : t('transport.hintProxy');
    }
    updateTunProxyNotice();
    // TUN stack selector is only relevant while TUN capture is selected.
    const stackRow = document.getElementById('tunStackRow');
    if (stackRow) stackRow.classList.toggle('hidden', transport !== 'tun');
    updateStatusLine();
  }

  function applyProtocolPreference(next) {
    const known = ['auto', 'wireguard', 'mimic', 'hysteria2', 'openvpn'];
    protocolPreference = known.includes(String(next)) ? String(next) : 'auto';
    document.querySelectorAll('.protocol-pill').forEach(p => {
      const on = p.dataset.protocol === protocolPreference;
      p.classList.toggle('active', on);
      p.classList.toggle('text-cyan-300', on);
      p.classList.toggle('text-[#A7B0BF]', !on);
    });
    syncQuickControls();
    const hints = {
      auto: t('protocol.hint.auto'),
      wireguard: t('protocol.hint.wireguard'),
      mimic: t('protocol.hint.mimic'),
      hysteria2: t('protocol.hint.hysteria2'),
      openvpn: t('protocol.hint.openvpn')
    };
    if (protocolHint) protocolHint.textContent = hints[protocolPreference] || hints.auto;
  }

  function isProxyModeEnabled(value) {
    return value === 1 || value === 3;
  }

  function syncQuickControls() {
    if (quickProtocolSelect) {
      quickProtocolSelect.value = protocolPreference;
    }
    // The mobile-only protocol select mirrors the sidebar one.
    const mobileProtocolSelect = document.getElementById('mobileProtocolSelect');
    if (mobileProtocolSelect) {
      mobileProtocolSelect.value = protocolPreference;
    }
    if (quickAutoReconnect) {
      quickAutoReconnect.checked = autoReconnect === true;
    }
    // The hero-strip (mobile/compact) auto-reconnect toggle mirrors the sidebar one.
    const mobileAutoReconnect = document.getElementById('mobileAutoReconnect');
    if (mobileAutoReconnect) {
      mobileAutoReconnect.checked = autoReconnect === true;
    }
    // Keep the upgraded themed dropdown labels in sync with programmatic sets.
    refreshAllCustomSelects();
  }

  function applySystemProxyState(desired, effective, connectionOwned) {
    const normalize = value => Number.isInteger(Number(value)) && Number(value) >= 0 && Number(value) <= 3 ? Number(value) : 0;
    systemProxyMode = normalize(desired);
    effectiveSystemProxyMode = normalize(effective);
    systemProxyConnectionOwned = connectionOwned === true;

    if (systemProxyModeSelect) {
      systemProxyModeSelect.value = String(systemProxyMode);
    }
    refreshAllCustomSelects();

    const enabled = isProxyModeEnabled(effectiveSystemProxyMode);
    const desiredEnabled = isProxyModeEnabled(systemProxyMode);
    systemProxyToggleBtn.classList.toggle('enabled', enabled);
    systemProxyToggleBtn.classList.toggle('owned', systemProxyConnectionOwned);
    systemProxyDot.classList.toggle('bg-emerald-400', enabled);
    systemProxyDot.classList.toggle('bg-[#5B6472]', !enabled);
    systemProxyDot.style.color = enabled ? '#34D399' : '#5B6472';
    systemProxyLabel.textContent = systemProxyConnectionOwned
      ? 'PROXY · CONNECTION'
      : enabled ? (effectiveSystemProxyMode === 3 ? 'PROXY · PAC' : 'PROXY ON')
      : 'PROXY OFF';
    if (systemProxyAddress) {
      const address = systemProxyAppliedAddress || (enabled ? '127.0.0.1' : '');
      systemProxyAddress.textContent = address ? `(${address})` : '';
      systemProxyAddress.classList.toggle('hidden', !address);
    }
    const becameConnectionOwned = systemProxyConnectionOwned && !prevSystemProxyConnectionOwned;
    prevSystemProxyConnectionOwned = systemProxyConnectionOwned;
    systemProxyToggleBtn.title = systemProxyConnectionOwned
      ? 'PROXY ON was enabled, so CONNECT handed the system proxy to the active tunnel — the badge now reads PROXY · CONNECTION. Your independent PROXY ON preference is preserved and restored when you disconnect.'
      : desiredEnabled
        ? (systemProxyMode === 3
          ? 'System proxy is on via PAC (PROXY · PAC). Click to disable. Press CONNECT and the active tunnel takes it over, switching the badge to PROXY · CONNECTION until you disconnect.'
          : 'System proxy is on (PROXY ON). Click to disable. Press CONNECT and the active tunnel takes it over, switching the badge to PROXY · CONNECTION until you disconnect.')
        : 'System proxy is off — the tunnel can still connect without it. Click to enable (PROXY ON).';
    // Exactly when CONNECT hands the system proxy over to the active tunnel, tell
    // the user why the badge changed from PROXY ON to PROXY · CONNECTION.
    if (becameConnectionOwned && typeof window.notifyNodes === 'function') {
      window.notifyNodes('PROXY ON + CONNECT: the active tunnel now manages the system proxy — badge shows PROXY · CONNECTION. Your PROXY ON preference returns when you disconnect.');
    }
    updateTunProxyNotice();
    syncQuickControls();
  }

  function showConnectionError(info) {
    const banner = $('connectionError');
    if (!banner) {
      return;
    }
    // Structured failure card: { message, details, elevation, canRecover, port }.
    // Plain strings (legacy TUN/elevation warnings) keep the old single-line
    // banner with the relaunch button visible.
    const message = typeof info === 'string'
      ? info
      : (info && (info.message || info.Message)) || '';
    if (!message) {
      banner.classList.add('hidden');
      return;
    }
    const text = $('connectionErrorText');
    if (text) {
      text.textContent = message;
    }
    const details = $('connectionErrorDetails');
    if (details) {
      const detailText = (typeof info === 'object' && info && (info.details || info.Details)) || '';
      if (detailText) {
        details.textContent = String(detailText);
        details.classList.remove('hidden');
      } else {
        details.classList.add('hidden');
      }
    }
    const relaunch = $('relaunchAdminBtn');
    if (relaunch) {
      const showRelaunch = typeof info === 'string' || (info && info.elevation === true);
      relaunch.classList.toggle('hidden', !showRelaunch);
    }
    banner.classList.remove('hidden');
  }

  /**
   * Applies the real AoGPN profile pushed by the host. Calling it with no
   * arguments keeps the current concept node in standalone browser previews.
   */
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
    $('selNodeFlag').textContent = selectedNode.code;
    $('selNodeSummary').textContent = selectedNode.name;
    document.querySelectorAll('.node-card').forEach(c => c.classList.toggle('selected', c.dataset.node === selectedNode.key));
    refreshSessionNode();
    skinNotify(); // refresh standalone skins when the active node changes
  }

  const escHtml = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

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
          <span class="w-10 h-10 rounded-lg bg-gradient-to-br from-[var(--violet-30)] to-[var(--cyan-20)] flex items-center justify-center font-display font-bold text-sm text-cyan-300 shrink-0 shadow-[0_0_10px_var(--violet-30)]">${code}</span>
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
          <span class="w-10 h-10 rounded-lg bg-gradient-to-br from-amber-500/20 to-[var(--cyan-10)] flex items-center justify-center font-display font-bold text-sm text-amber-300 shrink-0">${code}</span>
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
    $('selNodeFlag').textContent = (node.sub || node.protocol || 'VPN').slice(0, 2).toUpperCase();
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
    if (nodeSortMode === 'country' || nodeSortMode === 'default') {
      arr.sort((a, b) => (a.country || 'ZZZ').localeCompare(b.country || 'ZZZ') || (a.name || '').localeCompare(b.name || ''));
      if (nodeSortMode === 'country') return arr;
    }
    if (nodeSortMode === 'country') {
      arr.sort((a, b) =>
        (a.country || 'ZZ').localeCompare(b.country || 'ZZ')
        || (a.name || '').localeCompare(b.name || ''));
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
              if (!editMode) {
                notifyNodes('Düzenleme modu kapalı — düğümleri değiştirmek için Düzenle\'yi açın');
                return;
              }
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
        return `
        <div class="col-span-full flex items-center gap-2 mt-2 first:mt-0 route-group-header" data-group="${escHtml(groupKey)}">
          <button type="button" class="route-group-toggle flex items-center gap-2 min-w-0" data-group-toggle="${escHtml(groupKey)}" aria-expanded="${!collapsed}">
            <span class="text-cyan-300 text-xs">${collapsed ? '▶' : '▼'}</span>
            <span class="text-[10px] uppercase tracking-[0.18em] text-cyan-300 font-semibold">${escHtml(country)}</span>
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

        // Right-click opens the node action menu (copy / delete).
        card.addEventListener('contextmenu', (event) => {
          event.preventDefault();
          const node = realNodes.get(card.dataset.index);
          if (!node || pendingSwitchId) {
            return;
          }
          if (!selectedIds.has(node.indexId)) {
            selectedIds.clear();
            selectedIds.add(node.indexId);
            selectionAnchorId = node.indexId;
            updateNodeSelectionUI();
          }
          showNodeCtxMenu(event.clientX, event.clientY);
        });
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
      $('nodeSelCount').textContent = count + (count === 1 ? ' düğüm seçildi' : ' düğüm seçildi');
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
    $('nodeTestAllLabel').textContent = nodeTestRunning ? 'Durdur' : 'Tümünü test et';
    const nodeTestAllBtn = $('nodeTestAllBtn');
    if (nodeTestAllBtn) {
      nodeTestAllBtn.setAttribute('aria-busy', nodeTestRunning ? 'true' : 'false');
      nodeTestAllBtn.title = nodeTestRunning ? 'Ping testini durdur' : 'Tüm düğümleri sırayla ping ile test et';
    }
    $('nodeDisabledBtn').classList.toggle('hidden', disabledNodes.size === 0 && !showingDisabled);
    $('nodeDisabledBtn').classList.toggle('flex', disabledNodes.size > 0 || showingDisabled);
    $('nodeDisabledLabel').textContent = `Disabled (${disabledNodes.size})`;
    $('nodeDisabledBarText').textContent = `${disabledNodes.size} devre dışı düğüm gösteriliyor — geri yükle veya kalıcı olarak sil`;
    $('nodeDisabledBar').classList.toggle('hidden', !showingDisabled);
    $('nodeDisabledBar').classList.toggle('flex', showingDisabled);
    $('nodeEditBar').classList.toggle('hidden', !editMode || showingDisabled);
    $('nodeEditBar').classList.toggle('flex', editMode && !showingDisabled);
    document.querySelectorAll('.ctx-edit').forEach(el => el.classList.toggle('hidden', !editMode));
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
    if (!editMode) {
      notifyNodes('Düzenleme modu kapalı — düğümleri değiştirmek için Düzenle\'yi açın');
      return;
    }
    $('nodeConfirmTitle').textContent = title;
    $('nodeConfirmOkLabel').textContent = 'Onayla';
    $('nodeConfirmText').textContent = text;
    pendingConfirmAction = action;
    showNodeConfirm();
  }
  function requestNodeDelete() {
    if (!editMode) {
      notifyNodes('Düzenleme modu kapalı — düğümleri değiştirmek için Düzenle\'yi açın');
      return;
    }
    const ids = selectionIds();
    if (ids.length === 0) {
      return;
    }
    $('nodeConfirmTitle').textContent = 'Düğüm(ler)i sil';
    $('nodeConfirmOkLabel').textContent = 'Sil';
    $('nodeConfirmText').textContent = `Bu işlem aboneliğinizden ${ids.length} düğümü kaldırır. Geri alınamaz.`;
    pendingConfirmAction = () => postToHost({ action: 'delete_nodes', indexIds: ids });
    showNodeConfirm();
  }
  function requestNodeDisable() {
    if (!editMode) {
      notifyNodes('Düzenleme modu kapalı — düğümleri değiştirmek için Düzenle\'yi açın');
      return;
    }
    const ids = selectionIds();
    if (ids.length === 0) {
      return;
    }
    $('nodeConfirmTitle').textContent = 'Düğüm(ler)i devre dışı bırak';
    $('nodeConfirmOkLabel').textContent = 'Devre dışı bırak';
    $('nodeConfirmText').textContent = `${ids.length} düğüm Devre Dışı bölümüne taşınsın mı? Daha sonra geri yükleyebilirsiniz.`;
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
    if (!editMode) {
      notifyNodes('Düzenleme modu kapalı — düğümleri değiştirmek için Düzenle\'yi açın');
      return;
    }
    const ids = [...disabledNodes.keys()];
    if (ids.length === 0) {
      return;
    }
    $('nodeConfirmTitle').textContent = 'Tüm devre dışı olanları sil';
    $('nodeConfirmOkLabel').textContent = 'Sil';
    $('nodeConfirmText').textContent = `Devre dışı bırakılmış ${ids.length} düğüm kalıcı olarak silinsin mi? Bu işlem geri alınamaz.`;
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
    if (currentView !== 'nodes' || !useRealNodes) {
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
      if (!editMode) {
        event.preventDefault();
        notifyNodes('Düzenleme modu kapalı — düğümleri değiştirmek için Düzenle\'yi açın');
        return;
      }
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
  function viewTitleParts(view) {
    return [
      t('view.' + view + '.title1'),
      t('view.' + view + '.title2'),
      t('view.' + view + '.sub')
    ];
  }

  function showView(view) {
    currentView = view;
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
      startGpnProbeLoop();
    } else {
      stopGpnProbeLoop();
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
  const blacklistActive = () => invertManual && (mode === 'gpn' || splitMode === 'manual');
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

  function routeSelect(processName, displayName, selected) {
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
    return `<select data-route-process="${escHtml(processName)}" data-route-display="${escHtml(displayName || processName)}" class="bg-white/5 border border-white/10 rounded-md px-2 py-1 text-[10px] text-slate-200 outline-none focus:border-cyan-400/50">${options}</select>`;
  }

  function monitorFilterMatches(item, filter) {
    if (!filter) return true;
    const haystack = [item.processName, item.displayName, item.remoteAddress, item.countryText, item.asnText, item.routeText].join(' ');
    return haystack.toLowerCase().includes(filter.toLowerCase());
  }

  function configuredActionFor(processName) {
    return (monitorSnapshot.apps || []).find(item =>
      item.entryType === 'app' && String(item.processName).toLowerCase() === String(processName).toLowerCase())?.action || '';
  }

  function renderMonitorConnections() {
    const bodyEl = $('monitorConnectionsBody');
    if (!bodyEl) return;
    const filter = ($('monitorFilter')?.value || '').trim();
    const hideListeners = $('monitorHideListeners')?.checked !== false;
    const rows = (monitorSnapshot.connections || []).filter(item => {
      if (hideListeners && item.protocol === 'TCP' && item.state === 'Listen') return false;
      return monitorFilterMatches(item, filter);
    });
    $('monitorEmpty')?.classList.toggle('hidden', rows.length > 0);
    bodyEl.innerHTML = rows.length > 0 ? rows.map(item => {
      const route = item.routeTag || '';
      const country = [item.countryText, item.asnText].filter(Boolean).join(' · ') || '—';
      return `<tr class="hover:bg-white/[.035] transition-colors">
        <td class="px-3 py-2.5"><p class="font-medium text-slate-100 truncate max-w-[190px]">${escHtml(item.displayName || item.processName || 'Unknown')}</p><p class="text-[10px] text-[#64748B] truncate max-w-[190px]">${escHtml(item.processName || '')}${item.pid ? ' · PID ' + escHtml(item.pid) : ''}</p></td>
        <td class="px-3 py-2.5">${routeBadge(route, item.routeText || routeLabels[route])}</td>
        <td class="px-3 py-2.5 text-[#A7B0BF]">${escHtml(item.protocol || '—')}</td>
        <td class="px-3 py-2.5 text-[#CBD5E1] font-mono text-[10px] max-w-[180px] truncate" title="${escHtml(item.remoteAddress || '')}">${escHtml(item.remoteAddress || '—')}</td>
        <td class="px-3 py-2.5 text-[#8A94A6] max-w-[150px] truncate" title="${escHtml(country)}">${escHtml(country)}</td>
        <td class="px-3 py-2.5 text-[#8A94A6]">${escHtml(item.state || '—')}</td>
        <td class="px-3 py-2.5">${routeSelect(item.processName, item.displayName, configuredActionFor(item.processName))}</td>
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
    const apps = (monitorSnapshot.apps || [])
      .filter(a => a.action === 'vpn' || a.action === 'vpn+proxy')
      .slice(0, 4);
    const runningCount = apps.filter(a => a.isRunning).length;
    const totalCount = (monitorSnapshot.apps || []).length;
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
        <div class="w-10 h-10 rounded-lg bg-gradient-to-br from-[var(--violet-30)] to-[var(--cyan-20)] flex items-center justify-center font-display font-bold text-cyan-300 shrink-0 shadow-[0_0_12px_var(--violet-25)]">${escHtml(label)}</div>
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
    const items = processCatalog.filter(item => {
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
        <span class="w-7 h-7 rounded-md bg-cyan-400/10 text-cyan-300 flex items-center justify-center text-[10px] font-bold shrink-0">EXE</span>
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
    const via = (realAfter && _activeGpnServer)
      ? ` · ${escHtml(t('boost.viaNode', { node: _activeGpnServer }))}`
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
    const all = monitorSnapshot.apps || [];
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
      const warpBlocked = warpEntries > 0 && _activeGpnMode && _activeGpnMode !== 'WireGuard';
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
      const effLabel = blacklistActive() && eff !== item.action
        ? (routeLabels[eff] || eff)
        : (routeLabels[item.action] || item.routeText);
      const dirMark = blacklistActive() && item.action === 'vpn'
        ? `<span class="ml-1 text-[9px] px-1 py-px rounded bg-amber-500/15 text-amber-300 border border-amber-400/25" title="${escHtml(t('dir.tipRowExcluded'))}">⇄ ${escHtml(t('dir.rowExcluded'))}</span>`
        : blacklistActive() && item.action === 'direct'
          ? `<span class="ml-1 text-[9px] px-1 py-px rounded bg-cyan-400/10 text-cyan-300 border border-cyan-400/30" title="${escHtml(t('dir.tipRowTunneled'))}">⇄ ${escHtml(t('dir.rowTunneled'))}</span>`
          : '';
      return `<tr class="hover:bg-white/[.035] transition-colors" data-process-name="${escHtml(processName)}">
        <td class="px-3 py-2.5"><p class="font-medium text-slate-100 truncate max-w-[180px]">${escHtml(item.displayName || item.processName || item.value)}</p><p class="text-[10px] text-[#64748B] truncate max-w-[180px]">${escHtml(processName)}</p></td>
        <td class="px-3 py-2.5">${routeBadge(tagForRoute(eff), effLabel)}${dirMark}</td>
        <td class="px-3 py-2.5">${live ? routeBadge(live, item.liveRouteText) : '<span class="text-[#5B6472]">Idle</span>'}</td>
        <td class="px-3 py-2.5"><span class="inline-flex items-center gap-1.5 text-[10px] ${item.isRunning ? 'text-emerald-300' : 'text-[#8A94A6]'}"><span class="w-1.5 h-1.5 rounded-full ${item.isRunning ? 'bg-emerald-400 animate-pulse' : 'bg-[#5B6472]'}"></span>${escHtml(status)}</span></td>
        <td class="px-3 py-2.5 text-[10px] font-mono">${realPingMarkup(item) || '<span class="text-[#5B6472]">—</span>'}</td>
        <td class="px-3 py-2.5 text-[10px] font-mono text-[#8A94A6] max-w-[170px] truncate" title="${escHtml(item.activeIps || '')}">${targetIpsMarkup(item)}</td>
        <td class="px-3 py-2.5 text-[10px] text-cyan-200 font-mono num-tabular">${escHtml(down)}</td>
        <td class="px-3 py-2.5 text-[10px] text-purple-200 font-mono num-tabular">${escHtml(up)}</td>
        <td class="px-3 py-2.5">${routeSelect(processName, item.displayName || item.value, item.action)}</td>
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
      button.classList.toggle('active', button.dataset.splitMode === splitMode);
    });
    const hints = {
      off: 'Off — disconnected. No traffic is captured; your independent system proxy preference is untouched.',
      vpn: 'Global VPN — all traffic follows the selected VPN profile and TUN/proxy transport.',
      manual: 'GPN Game Tunnel — only assigned games/apps are tunneled; unlisted traffic stays direct.'
    };
    if ($('boostModeHint')) $('boostModeHint').textContent = hints[splitMode] || hints.off;
    // Route mode is one shared concept: the Game Boost pills, the quick selector
    // and the Dashboard pills all drive the same backend routing mode.
    if (splitMode === 'vpn' || splitMode === 'manual') {
      const nextMode = splitMode === 'manual' ? 'gpn' : 'vpn';
      if (mode !== nextMode) {
        mode = nextMode;
        applyMode();
      }
    }
    syncQuickControls();
  }

  /** Toggles the GPN routing-direction pills and their hint text. */
  function applyDirection() {
    document.querySelectorAll('.dir-pill').forEach(p => {
      const on = (p.dataset.dir === 'blacklist') === invertManual;
      p.classList.toggle('active', on);
      p.classList.toggle('text-cyan-300', on);
      p.classList.toggle('border-cyan-400/40', on);
      p.classList.toggle('bg-cyan-400/10', on);
      p.classList.toggle('text-[#A7B0BF]', !on);
      p.classList.toggle('border-white/10', !on);
    });
    const hint = document.getElementById('dirHint');
    if (hint) hint.textContent = invertManual ? t('dir.hintBlacklist') : t('dir.hintWhitelist');
    updateGlobalPanel();
    // Direction changes flip the meaning of the boost-table route column.
    if (currentView === 'boost') renderSplitApps();
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
      const show = transport === 'tun' && !connected && isProxyModeEnabled(systemProxyMode);
      proxyNotice.classList.toggle('hidden', !show);
    }
  }

  function updateGlobalPanel() {
    const vpnActive = mode === 'vpn' && connected;
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
    processCatalog = Array.isArray(items)
      ? items.filter(item => item && Number.isInteger(item.pid) && item.pid > 0).slice(0, 300)
      : [];
    renderProcessCatalog();
  };

  window.updateMonitorSnapshot = (data) => {
    if (!data || typeof data !== 'object') return;
    monitorSnapshot = {
      ...data,
      connections: Array.isArray(data.connections) ? data.connections : [],
      apps: Array.isArray(data.apps) ? data.apps : [],
      traffic: Array.isArray(data.traffic) ? data.traffic : []
    };
    splitMode = ['off', 'vpn', 'manual'].includes(data.mode) ? data.mode : splitMode;
    if (typeof data.invertManualRouting === 'boolean') {
      invertManual = data.invertManualRouting;
      applyDirection();
    }
    $('monitorConnectionsCount').textContent = String(data.activeConnectionCount ?? monitorSnapshot.connections.length);
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
    $('monitorAppsCount').textContent = String(data.activeAppCount ?? monitorSnapshot.apps.length);
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
    if (currentView === 'perf') {
      renderMonitorConnections();
    }
    if (currentView === 'boost') {
      renderSplitApps();
    }
    // The host pushes a fresh monitor snapshot every ~2 s — notify the loaded
    // skins immediately so boost apps, routes and telemetry stay live without
    // waiting on the skins' own safety poll (which may be throttled when the
    // app is backgrounded).
    skinNotify();
  };

  // ---------- Settings view ----------
  const settingsState = { saving: false };

  // Fallback option lists so the form still renders when opened outside WebView2;
  // the WPF host replaces them with the real Global.* lists via applySettings.
  const FALLBACK_OPTIONS = {
    logLevels: ['debug', 'info', 'warning', 'error', 'none'],
    fingerprints: ['chrome', 'firefox', 'safari', 'ios', 'android', 'edge', '360', 'qq', 'random', 'randomized', ''],
    userAgents: ['chrome', 'firefox', 'edge', 'curl', 'golang'],
    singboxMuxs: ['h2mux', 'smux', 'yamux', ''],
    tunStacks: ['gvisor', 'system', 'mixed'],
    tunIcmpRoutingPolicies: ['rule', 'direct', 'unreachable', 'drop', 'reply'],
    tunIPv4Addresses: ['172.18.0.1/30', '172.31.0.1/30', '172.20.0.1/30', '172.16.0.1/30', '192.168.100.1/30', '10.10.14.1/30', '10.1.0.1/30', '10.0.0.1/30'],
    tunIPv6Addresses: ['fc00::172:18:0:1/126', 'fc00::172:31:0:1/126', 'fc00::172:20:0:1/126', 'fc00::172:16:0:1/126', 'fc00::192:168:100:1/126', 'fc00::10:10:14:1/126', 'fc00::10:1:0:1/126', 'fc00::10:0:0:1/126'],
    fragmentPacketsOptions: ['tlshello', '1-1', '1-2', '1-3', '1-4', '1-5'],
    coreTypes: ['Xray', 'sing_box'],
    rootCertProviders: ['system', 'chrome', 'mozilla'],
    mixedConcurrencyCounts: ['2', '3', '4', '5', '6', '7', '8'],
    speedTestTimeouts: ['10', '15', '20', '25', '30'],
    speedTestUrls: ['https://cachefly.cachefly.net/50mb.test', 'https://speed.cloudflare.com/__down?bytes=10000000'],
    speedPingTestUrls: ['https://www.google.com/generate_204', 'https://www.gstatic.com/generate_204'],
    udpTestTargets: ['ntp:pool.ntp.org', 'dns:1.1.1.1', 'stun:stun.cloudflare.com'],
    ipapiUrls: ['https://api.ip.sb/geoip', ''],
    subConvertUrls: ['https://sub.xeton.dev/sub?url={0}', ''],
    geoFilesSources: ['', 'https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/{0}.dat'],
    singboxRulesetSources: ['', 'https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/sing-box/rule-set-{0}/{1}.srs'],
    routingRulesSources: ['', 'https://raw.githubusercontent.com/runetfreedom/russia-v2ray-custom-routing-list/main/AoGPN/template.json'],
    ieProxyProtocols: ['{ip}:{http_port}', 'socks={ip}:{socks_port}', 'http={ip}:{http_port};https={ip}:{http_port};ftp={ip}:{http_port};socks={ip}:{socks_port}', 'http=http://{ip}:{http_port};https=http://{ip}:{http_port}', ''],
    tunMtus: ['1280', '1408', '1500', '4064', '9000', '65535'],
  };

  const settingsOptions = {};
  // Keys match the plural option-list names pushed by the WPF host (and the
  // fallback lists below); the datalist element ids are the singular field names.
  const datalistMap = {
    speedTestUrls: 'dl_speedTestUrl',
    speedPingTestUrls: 'dl_speedPingTestUrl',
    udpTestTargets: 'dl_udpTestTarget',
    ipapiUrls: 'dl_ipapiUrl',
    subConvertUrls: 'dl_subConvertUrl',
    geoFilesSources: 'dl_geoFileSourceUrl',
    singboxRulesetSources: 'dl_srsFileSourceUrl',
    routingRulesSources: 'dl_routingRulesSourceUrl',
    tunMtus: 'dl_tunMtu',
  };

  function fillSettingsOptions(options) {
    if (!options || typeof options !== 'object') {
      return;
    }
    Object.assign(settingsOptions, options);
    document.querySelectorAll('[data-settings-options]').forEach(sel => {
      const list = settingsOptions[sel.dataset.settingsOptions];
      if (!Array.isArray(list)) {
        return;
      }
      const current = sel.value;
      sel.innerHTML = '';
      list.forEach(opt => {
        const o = document.createElement('option');
        o.value = opt;
        o.textContent = opt;
        sel.appendChild(o);
      });
      if (current) {
        sel.value = current;
      }
    });
    refreshAllCustomSelects();
    Object.keys(datalistMap).forEach(key => {
      const list = settingsOptions[key];
      const dl = document.getElementById(datalistMap[key]);
      if (!Array.isArray(list) || !dl) {
        return;
      }
      dl.innerHTML = '';
      list.forEach(opt => {
        const o = document.createElement('option');
        o.value = opt;
        dl.appendChild(o);
      });
    });
  }

  function setSettingsField(name, value) {
    if (name === 'sysProxyType') {
      document.querySelectorAll('input[data-settings="sysProxyType"]').forEach(r => {
        r.checked = String(r.value) === String(value);
      });
      return;
    }
    if (name === 'destOverride') {
      const set = new Set(Array.isArray(value) ? value : []);
      document.querySelectorAll('input[data-settings="destOverride"]').forEach(c => {
        c.checked = set.has(c.value);
      });
      return;
    }
    if (name === 'effectsMode') {
      // The tier is a segmented control, not a form input: apply it through
      // the effects engine (which reflects the state on the buttons).
      setEffectsTier(value === 'balanced' || value === 'reduced' ? value : 'full', false);
      return;
    }
    const el = document.querySelector('[data-settings="' + name + '"]');
    if (!el) {
      return;
    }
    if (el.type === 'checkbox') {
      el.checked = value === true || value === 'true';
      return;
    }
    const text = value == null ? '' : String(value);
    if (el.tagName === 'SELECT' && text && ![...el.options].some(o => o.value === text)) {
      // Editable native combos can hold values outside the preset list (custom
      // fingerprint/user-agent/URL); surface the real value instead of a blank box.
      const o = document.createElement('option');
      o.value = text;
      o.textContent = text;
      el.appendChild(o);
    }
    el.value = text;
    refreshAllCustomSelects();
  }

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
        applySystemProxyState(g.sysProxyType, g.effectiveSysProxyType, g.connectionOwnsProxy);
      }
      if (group === 'connection') {
        applyProtocolPreference(g.protocolPreference);
        if (typeof g.invertManualRouting === 'boolean') {
          invertManual = g.invertManualRouting;
          applyDirection();
        }
        if (typeof g.autoReconnectEnabled === 'boolean') {
          autoReconnect = g.autoReconnectEnabled;
          syncQuickControls();
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
    // GPN tabı açıldığında kayıtlı launcher-bypass düğümünü host'tan iste
    // (window.setVlessBypassNode ile gelir).
    if (btn.dataset.settingsTab === 'gpn') {
      postToHost({ action: 'get_vless_bypass_node' });
    }
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
  // The raw object is also captured so standalone skins can read it through the
  // skinBridge (skin About views show the real version, not a hard-coded one).
  let _appInfo = null;
  window.setAppInfo = function (info) {
    _appInfo = (info && typeof info === 'object') ? info : null;
    const v = (info && info.version) ? String(info.version) : '';
    const appName = (info && info.appName) ? String(info.appName) : 'AO GPN';
    const vEl = document.getElementById('aboutVersion');
    if (vEl) vEl.textContent = v ? 'V' + v : 'V? — ' + appName;
    skinNotify();
  };

  // ---------- About & Help: Release Notes (Temalar/release-notes.json) ----------
  // Loads the current release's milestone list from the Temalar folder at
  // startup (same pattern as themes.json), so new releases only add one JSON
  // file. Renders an expandable roadmap on the About & Help → Release Notes
  // tab. The JSON ships beside the dashboard, so the fallback is only a safety
  // net when the file cannot be fetched.
  var _releaseNotes = null;
  const RELEASE_NOTES_PATH = 'Temalar/release-notes.json';

  async function loadReleaseNotesFromDisk() {
    try {
      const res = await fetch(RELEASE_NOTES_PATH, { cache: 'no-store' });
      if (!res.ok) return;
      const data = await res.json();
      if (!data || !Array.isArray(data.milestones)) return;
      _releaseNotes = data;
      renderReleaseNotes();
    } catch (e) { /* keep fallback / empty state */ }
  }

  function _releaseCleanText(s) {
    return String(s || '').replace(/\*\*/g, '').replace(/[`_]/g, '').trim();
  }

  function renderReleaseNotes() {
    const host = document.getElementById('releaseNotes');
    if (!host) return;
    const meta = document.getElementById('releaseMeta');
    const version = (_releaseNotes && _releaseNotes.version) || '1.1.0';
    const list = (_releaseNotes && Array.isArray(_releaseNotes.milestones))
      ? _releaseNotes.milestones
      : [];
    host.textContent = '';

    if (meta) meta.textContent = 'V' + version + (list.length ? ' · ' + list.length : '');

    if (list.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'text-[12px] text-[#8A94A6]';
      empty.textContent = _releaseCleanText(t('about.releaseEmpty'));
      host.appendChild(empty);
      return;
    }

    list.forEach(m => {
      const title = _releaseCleanText(m.title);
      const desc = _releaseCleanText(m.desc);
      const hasDesc = !!desc;

      const row = document.createElement('button');
      row.type = 'button';
      row.className = 'release-row glass-soft rounded-xl w-full text-left p-3 flex items-center gap-3 hover:bg-white/5 transition-colors';

      const badge = document.createElement('span');
      badge.className = 'shrink-0 text-[10px] font-semibold px-2 py-0.5 rounded-md bg-cyan-400/10 text-cyan-300 border border-cyan-400/20 num-tabular';
      badge.textContent = _releaseCleanText(m.n);

      const label = document.createElement('span');
      label.className = 'min-w-0 flex-1 text-sm text-slate-200';
      label.textContent = title;

      const chev = document.createElement('span');
      chev.className = 'shrink-0 text-slate-400 transition-transform duration-200' + (hasDesc ? '' : ' opacity-40');
      chev.innerHTML = '<svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d="M19 9l-7 7-7-7"/></svg>';

      row.appendChild(badge);
      row.appendChild(label);
      row.appendChild(chev);
      host.appendChild(row);
      if (!hasDesc) return;

      const body = document.createElement('div');
      body.className = 'release-desc hidden rounded-xl bg-white/[0.03] border border-white/5 px-3.5 py-3 text-[12.5px] leading-relaxed text-[#A7B0BF]';
      body.textContent = desc;
      host.appendChild(body);

      row.addEventListener('click', () => {
        const opening = body.classList.contains('hidden');
        body.classList.toggle('hidden', !opening);
        chev.classList.toggle('rotate-180', opening);
      });
    });

    // Expand / collapse all scalings operate on every rendered description.
    const expandAll = document.getElementById('releaseExpandAll');
    const collapseAll = document.getElementById('releaseCollapseAll');
    if (expandAll) expandAll.addEventListener('click', () => {
      host.querySelectorAll('.release-desc').forEach(b => b.classList.remove('hidden'));
      host.querySelectorAll('.release-row').forEach(r => (r.lastElementChild) && r.lastElementChild.classList.add('rotate-180'));
    });
    if (collapseAll) collapseAll.addEventListener('click', () => {
      host.querySelectorAll('.release-desc').forEach(b => b.classList.add('hidden'));
      host.querySelectorAll('.release-row').forEach(r => (r.lastElementChild) && r.lastElementChild.classList.remove('rotate-180'));
    });
  }

  // ---------- Gaming theme engine ----------
  // Theme definitions are loaded from the Temalar folder (Temalar/themes.json)
  // so new themes can be added by editing that file without touching this
  // document. The array below is only the built-in fallback.
  let THEMES = [
    { id: 'nebula',    name: 'Nebula',    signal: 'VIOLET / CYAN',   tagline: 'Deep space violet aurora',      a: '#7C3AED', b: '#0891B2', rgbA: '124,58,237', rgbB: '8,145,178', rgbEmerald: '5,150,105' },
    { id: 'inferno',   name: 'Inferno',   signal: 'EMBER / AMBER',   tagline: 'Ember red, molten amber',        a: '#DC2626', b: '#D97706', rgbA: '220,38,38', rgbB: '217,119,6', rgbEmerald: '22,163,74' },
    { id: 'venom',     name: 'Venom',     signal: 'LIME / TOXIC',    tagline: 'Toxic lime, bio emerald',        a: '#65A30D', b: '#059669', rgbA: '101,163,13', rgbB: '5,150,105', rgbEmerald: '22,163,74' },
    { id: 'cryo',      name: 'Cryo',      signal: 'ICE / INDIGO',    tagline: 'Glacial blue, frost indigo',     a: '#2563EB', b: '#6366F1', rgbA: '37,99,235', rgbB: '99,102,241', rgbEmerald: '13,148,136' },
    { id: 'synthwave', name: 'Synthwave', signal: 'MAGENTA / ROSE',  tagline: 'Neon magenta, retro rose',        a: '#C026D3', b: '#DB2777', rgbA: '192,38,211', rgbB: '219,39,119', rgbEmerald: '13,148,136' },
    { id: 'cyberpunk', name: 'Cyberpunk', signal: 'GOLD / CRIMSON',  tagline: 'Amber neon, crimson haze',        a: '#EAB308', b: '#E11D48', rgbA: '234,179,8', rgbB: '225,29,72', rgbEmerald: '5,150,105' },
    { id: 'matrix',    name: 'Matrix',    signal: 'PHOSPHOR / GRID', tagline: 'Phosphor green, dark terminal',   a: '#16A34A', b: '#15803D', rgbA: '22,163,74', rgbB: '21,128,61', rgbEmerald: '34,197,94' },
    { id: 'plasma',    name: 'Plasma',    signal: 'VIOLET / BLUE',   tagline: 'Electric violet, deep blue',      a: '#7C3AED', b: '#0284C7', rgbA: '124,58,237', rgbB: '2,132,199', rgbEmerald: '5,150,105' },
    { id: 'phantom',   name: 'Phantom',   signal: 'SPECTRAL / MIST', tagline: 'Spectral indigo, misty white',    a: '#A5B4FC', b: '#6366F1', rgbA: '165,180,252', rgbB: '99,102,241', rgbEmerald: '129,140,248' },
    { id: 'crimson',   name: 'Crimson',   signal: 'RED / BLACK',     tagline: 'Tactical redline, black ops',     a: '#F43F5E', b: '#991B1B', rgbA: '244,63,94', rgbB: '153,27,27', rgbEmerald: '245,158,11' },
    { id: 'velocity',  name: 'Velocity',  signal: 'RED / BLUE',      tagline: 'High-speed redline telemetry',    a: '#EF4444', b: '#3B82F6', rgbA: '239,68,68', rgbB: '59,130,246', rgbEmerald: '34,197,94' },
    // Extra palettes (previously orphaned theme files, now part of the registry)
    { id: 'aurora',    name: 'Aurora',    signal: 'COPPER / TEAL',   tagline: 'Warm copper over deep teal',      a: '#B87333', b: '#2DD4BF', rgbA: '184,115,51', rgbB: '45,212,191', rgbEmerald: '163,230,53' },
    { id: 'candy',     name: 'Candy',     signal: 'ROSE / MINT',     tagline: 'Playful rose and mint candy',     a: '#FB7185', b: '#A7F3D0', rgbA: '251,113,133', rgbB: '167,243,208', rgbEmerald: '253,230,138' },
    { id: 'obsidian',  name: 'Obsidian',  signal: 'STEEL / WHITE',   tagline: 'Monochrome slate and steel',      a: '#CBD5E1', b: '#F8FAFC', rgbA: '203,213,225', rgbB: '248,250,252', rgbEmerald: '148,163,184' },
    { id: 'sandstorm', name: 'Sandstorm', signal: 'TEAL / SAND',     tagline: 'Dune sand and oasis teal',        a: '#14B8A6', b: '#F4E4BA', rgbA: '20,184,166', rgbB: '244,228,186', rgbEmerald: '132,204,22' },
    // NEXUS gamer palettes adapted from the supplied HTML concepts
    { id: 'neon-cyber',      name: 'Neon Cyber',      signal: 'CYAN / VIOLET',  tagline: 'Neon cyan over electric violet',  a: '#19E7FF', b: '#9B5CFF', rgbA: '25,231,255', rgbB: '155,92,255', rgbEmerald: '97,255,154' },
    { id: 'titanium-orange', name: 'Titanium Orange', signal: 'ORANGE / GOLD',  tagline: 'Molten titanium orange',          a: '#FF8A00', b: '#FFD166', rgbA: '255,138,0', rgbB: '255,209,102', rgbEmerald: '97,255,154' },
    { id: 'matrix-green',    name: 'Matrix Green',    signal: 'LIME / EMERALD', tagline: 'Terminal phosphor green',          a: '#58FF83', b: '#12C978', rgbA: '88,255,131', rgbB: '18,201,120', rgbEmerald: '97,255,154' },
    { id: 'ocean-blue',      name: 'Ocean Blue',      signal: 'AZURE / BLUE',   tagline: 'Deep ocean azure',                 a: '#42B9FF', b: '#557CFF', rgbA: '66,185,255', rgbB: '85,124,255', rgbEmerald: '97,255,154' },
    { id: 'red-phantom',     name: 'Red Phantom',     signal: 'RED / ORANGE',   tagline: 'Aggressive competitive red',       a: '#FF4058', b: '#FF8148', rgbA: '255,64,88', rgbB: '255,129,72', rgbEmerald: '97,255,154' },
    { id: 'violet-nova',     name: 'Violet Nova',     signal: 'VIOLET / PINK',  tagline: 'Luxury violet nova',               a: '#B66CFF', b: '#FF5EEA', rgbA: '182,108,255', rgbB: '255,94,234', rgbEmerald: '97,255,154' },
    { id: 'arctic-ice',      name: 'Arctic Ice',      signal: 'ICE / BLUE',     tagline: 'Glacial arctic ice',               a: '#BCECFF', b: '#70A8FF', rgbA: '188,236,255', rgbB: '112,168,255', rgbEmerald: '97,255,154' },
    { id: 'gold-elite',      name: 'Gold Elite',      signal: 'GOLD / AMBER',   tagline: 'Premium gold elite',               a: '#FFD45A', b: '#FF9F1C', rgbA: '255,212,90', rgbB: '255,159,28', rgbEmerald: '97,255,154' },
    { id: 'stealth-camo',    name: 'Stealth Camo',    signal: 'OLIVE / LIME',   tagline: 'Tactical stealth camo',            a: '#9AA66B', b: '#D0DF73', rgbA: '154,166,107', rgbB: '208,223,115', rgbEmerald: '97,255,154' },
    { id: 'crimson-core',    name: 'Crimson Core',    signal: 'CRIMSON / VIOLET', tagline: 'Reactor crimson core',           a: '#FF294F', b: '#A800FF', rgbA: '255,41,79', rgbB: '168,0,255', rgbEmerald: '97,255,154' }
  ];
  const THEME_STORAGE_KEY = 'aogpn.theme';
  var _currentThemeId = 'nebula';
  // Loads theme definitions from Temalar/themes.json at startup. On success the
  // live THEMES array is replaced (keeping the saved theme when it still exists)
  // and the popover re-renders; on failure the built-in fallback stays active.
  async function loadThemesFromDisk() {
    try {
      const res = await fetch('Temalar/themes.json', { cache: 'no-store' });
      if (!res.ok) return;
      const list = await res.json();
      if (!Array.isArray(list) || list.length === 0) return;
      const valid = list.filter(t => t && typeof t.id === 'string' && t.id && t.a && t.b);
      if (valid.length === 0) return;
      const saved = (() => { try { return localStorage.getItem(THEME_STORAGE_KEY); } catch { return null; } })();
      const keep = saved && valid.some(t => t.id === saved);
      THEMES = valid;
      _currentThemeId = keep ? saved : valid[0].id;
      renderTopThemePopover();
      applyTheme(_currentThemeId, false, false);
    } catch (e) { /* keep built-in fallback */ }
  }

  // ---------- Cursor & effects engine ----------
  var _cursorCanvas, _ctx, _trails = [], _cursorActive = false, _cursorX = 0, _cursorY = 0;
  var _matrixCanvas, _matrixCtx, _matrixCols = [];
  var _audioCtx;
  var _confettiParticles = [];

  function _resizeCanvas() {
    if (!_cursorCanvas) return;
    _cursorCanvas.width = window.innerWidth;
    _cursorCanvas.height = window.innerHeight;
  }

  var _cursorFramePending = false;
  function _requestCursorFrame() {
    // The rAF loop only lives while the pointer is moving or particles are
    // on screen; once everything fades it stops itself (_renderCursorFrame
    // stops re-scheduling) so the compositor can go fully idle.
    if (effectsReduced() || _cursorFramePending || !_ctx || !_cursorCanvas) return;
    _cursorFramePending = true;
    requestAnimationFrame(_renderCursorFrame);
  }

  function _onMouseMove(e) {
    _cursorX = e.clientX; _cursorY = e.clientY; _cursorActive = true;
    var fx = document.querySelectorAll('.cursor-fx');
    for (var i = 0; i < fx.length; i++) {
      fx[i].style.left = e.clientX + 'px';
      fx[i].style.top = e.clientY + 'px';
    }
    if (!_ctx) return;
    if (effectsReduced()) return;
    _trails.push({ x: e.clientX, y: e.clientY, life: 1, size: Math.random() * 3 + 1 });
    if (_trails.length > 40) _trails.shift();
    _requestCursorFrame();
  }

  function _renderCursorFrame() {
    _cursorFramePending = false;
    if (!_ctx || !_cursorCanvas) return;
    _ctx.clearRect(0, 0, _cursorCanvas.width, _cursorCanvas.height);
    for (var ci = _confettiParticles.length - 1; ci >= 0; ci--) {
      var p = _confettiParticles[ci];
      p.x += p.vx; p.y += p.vy;
      p.vy += 0.25; p.life -= 0.018;
      p.rotation += p.rotSpeed;
      if (p.life <= 0) { _confettiParticles.splice(ci, 1); continue; }
      _ctx.save();
      _ctx.translate(p.x, p.y);
      _ctx.rotate(p.rotation * Math.PI / 180);
      _ctx.fillStyle = p.color;
      _ctx.globalAlpha = p.life * 0.7;
      _ctx.fillRect(-p.size/2, -p.size/2, p.size, p.size);
      _ctx.restore();
    }
    for (var ti = _trails.length - 1; ti >= 0; ti--) {
      var t = _trails[ti];
      t.life -= 0.025;
      if (t.life <= 0) { _trails.splice(ti, 1); continue; }
      _ctx.beginPath();
      _ctx.arc(t.x, t.y, t.size * t.life, 0, Math.PI * 2);
      _ctx.fillStyle = 'rgba(var(--violet-rgb),' + (t.life * 0.4) + ')';
      _ctx.fill();
    }
    // Everything faded out: stop the loop until the next mousemove or
    // fireConfetti restarts it. Previously this re-scheduled every frame
    // forever, keeping the WebView2 GPU process rendering at 60 fps.
    if (_trails.length === 0 && _confettiParticles.length === 0) {
      _cursorActive = false;
      return;
    }
    _cursorFramePending = true;
    requestAnimationFrame(_renderCursorFrame);
  }

  function initCursorEffects(themeId) {
    _cursorCanvas = document.getElementById('cursorCanvas');
    if (_cursorCanvas) {
      _ctx = _cursorCanvas.getContext('2d');
      _resizeCanvas();
      window.addEventListener('resize', _resizeCanvas);
      document.addEventListener('mousemove', _onMouseMove);
      // No initial frame: with no trails/confetti there is nothing to paint.
    }
    var mc = document.getElementById('matrixRainCanvas');
    if (mc && !_matrixCtx) {
      _matrixCanvas = mc;
      _matrixCtx = mc.getContext('2d');
      window.addEventListener('resize', _resizeMatrix);
      _resizeMatrix();
      // Starts only when the matrix theme is active (see _requestMatrixFrame).
      _requestMatrixFrame();
    }
  }

  // --- Matrix rain ---
  var _matrixFontSize = 14;
  var _matrixFramePending = false;
  var _matrixPaintHalf = false; // alternate-vsync gate: repaint ~30 fps, not 60
  function _resizeMatrix() {
    if (!_matrixCanvas || !_matrixCtx) return;
    _matrixCanvas.width = window.innerWidth;
    _matrixCanvas.height = window.innerHeight;
    var cols = Math.floor(_matrixCanvas.width / _matrixFontSize);
    _matrixCols = [];
    for (var i = 0; i < cols; i++) {
      _matrixCols[i] = Math.floor(Math.random() * -_matrixCanvas.height / _matrixFontSize);
    }
  }
  function _requestMatrixFrame() {
    // The rain loop only runs while the matrix theme is active on the full
    // effects tier; balanced/reduced freeze it, otherwise a full-viewport
    // canvas redraw every other vsync keeps the GPU process busy forever.
    if (effectsReduced() || effectsTier === 'balanced' || _matrixFramePending || !_matrixCtx || !_matrixCanvas) return;
    if (document.body.getAttribute('data-theme') !== 'matrix') return;
    _matrixFramePending = true;
    requestAnimationFrame(_renderMatrixFrame);
  }
  function _renderMatrixFrame() {
    // Half-rate painting: the loop runs at vsync but only repaints on every
    // second frame (~30 fps). The falling rain looks identical to the naked
    // eye while the full-viewport canvas raster work is halved.
    if (_matrixPaintHalf) {
      _matrixPaintHalf = false;
      _matrixFramePending = true;
      requestAnimationFrame(_renderMatrixFrame);
      return;
    }
    _matrixPaintHalf = true;
    _matrixFramePending = false;
    if (!_matrixCtx || !_matrixCanvas) return;
    if (document.body.getAttribute('data-theme') !== 'matrix' || effectsReduced() || effectsTier === 'balanced') {
      // Theme switched away or the tier froze the rain: stop the loop and drop
      // any leftover pixels instead of re-filling the canvas every frame.
      _matrixCtx.clearRect(0, 0, _matrixCanvas.width, _matrixCanvas.height);
      return;
    }
    _matrixCtx.fillStyle = 'rgba(0,0,0,.05)';
    _matrixCtx.fillRect(0, 0, _matrixCanvas.width, _matrixCanvas.height);
    _matrixCtx.font = _matrixFontSize + 'px monospace';
    for (var i = 0; i < _matrixCols.length; i++) {
      var char = String.fromCharCode(0x30A0 + Math.random() * 96);
      _matrixCtx.fillStyle = 'rgba(34,197,94,' + (Math.random() * 0.4 + 0.1) + ')';
      _matrixCtx.fillText(char, i * _matrixFontSize, _matrixCols[i] * _matrixFontSize);
      if (_matrixCols[i] * _matrixFontSize > _matrixCanvas.height && Math.random() > 0.975) {
        _matrixCols[i] = 0;
      }
      _matrixCols[i]++;
    }
    _matrixFramePending = true;
    requestAnimationFrame(_renderMatrixFrame);
  }

  // --- Sound engine ---
  function _ensureAudioCtx() {
    if (!_audioCtx) { try { _audioCtx = new (window.AudioContext || window.webkitAudioContext)(); } catch(e) {} }
    return _audioCtx;
  }
  function _playTone(freq, type, duration, vol) {
    var ctx = _ensureAudioCtx();
    if (!ctx) return;
    var osc = ctx.createOscillator();
    var gain = ctx.createGain();
    osc.type = type || 'sine';
    osc.frequency.value = freq;
    gain.gain.setValueAtTime((vol || 0.08), ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + (duration || 0.15));
    osc.connect(gain); gain.connect(ctx.destination);
    osc.start(); osc.stop(ctx.currentTime + (duration || 0.15) + 0.05);
  }
  function _playNoise(duration, vol) {
    var ctx = _ensureAudioCtx();
    if (!ctx) return;
    var buf = ctx.createBuffer(1, ctx.sampleRate * (duration || 0.1), ctx.sampleRate);
    var data = buf.getChannelData(0);
    for (var i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
    var src = ctx.createBufferSource(); src.buffer = buf;
    var gain = ctx.createGain();
    gain.gain.setValueAtTime((vol || 0.04), ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + (duration || 0.1));
    src.connect(gain); gain.connect(ctx.destination);
    src.start(); src.stop(ctx.currentTime + (duration || 0.1) + 0.05);
  }
  var _themeSounds = {
    nebula:    function() { _playTone(220, 'triangle', 0.25, 0.06); setTimeout(function() { _playTone(330, 'sine', 0.2, 0.05); }, 100); },
    inferno:   function() { _playTone(180, 'sawtooth', 0.2, 0.05); _playNoise(0.08, 0.03); },
    venom:     function() { _playTone(160, 'square', 0.15, 0.04); setTimeout(function() { _playTone(320, 'sine', 0.12, 0.03); }, 60); },
    cryo:      function() { _playTone(440, 'sine', 0.3, 0.04); setTimeout(function() { _playTone(660, 'sine', 0.2, 0.03); }, 80); },
    synthwave: function() { _playTone(200, 'sawtooth', 0.18, 0.05); setTimeout(function() { _playTone(400, 'square', 0.1, 0.03); }, 50); },
    cyberpunk: function() { _playTone(150, 'square', 0.15, 0.04); _playNoise(0.06, 0.04); },
    matrix:    function() { _playTone(120, 'triangle', 0.22, 0.05); setTimeout(function() { _playTone(240, 'sine', 0.15, 0.04); }, 90); },
    plasma:    function() { _playTone(300, 'sine', 0.2, 0.06); setTimeout(function() { _playTone(450, 'triangle', 0.15, 0.04); }, 70); },
    phantom:   function() { _playTone(260, 'sine', 0.35, 0.03); setTimeout(function() { _playTone(390, 'sine', 0.25, 0.02); }, 120); },
    crimson:   function() { _playTone(100, 'sawtooth', 0.18, 0.06); _playNoise(0.1, 0.05); },
    velocity:  function() { _playTone(280, 'square', 0.15, 0.05); setTimeout(function() { _playTone(560, 'sine', 0.1, 0.04); }, 60); }
  };
  function playThemeSound(themeId) {
    var fn = _themeSounds[themeId || _currentThemeId || 'nebula'];
    if (fn) fn();
  }

  // --- Confetti engine ---
  var _confettiColors = ['#F43F5E','#EAB308','#22C55E','#3B82F6','#8B5CF6','#EC4899','#F97316','#06B6D4'];
  function _spawnConfetti(x, y, count) {
    for (var i = 0; i < (count || 40); i++) {
      _confettiParticles.push({
        x: x, y: y,
        vx: (Math.random() - 0.5) * 8,
        vy: Math.random() * -8 - 4,
        size: Math.random() * 6 + 3,
        color: _confettiColors[Math.floor(Math.random() * _confettiColors.length)],
        life: 1,
        rotation: Math.random() * 360,
        rotSpeed: (Math.random() - 0.5) * 12
      });
    }
    if (_confettiParticles.length > 200) _confettiParticles.splice(0, _confettiParticles.length - 200);
  }
  function fireConfetti() {
    if (effectsReduced()) return;
    _spawnConfetti(window.innerWidth / 2, window.innerHeight / 2, 50);
    // Wake the cursor canvas loop: confetti must render even with a still mouse.
    _requestCursorFrame();
  }
  window.fireConfetti = fireConfetti;


  function applyTheme(id, persist, notifyHost) {
    const theme = THEMES.find(t => t.id === id) || THEMES[0];
    // User selections are persisted and sent to WPF; host-driven updates pass
    // persist=false and must not echo back into the bridge.
    if (notifyHost !== false && persist !== false) {
      postToHost({ action: 'set_theme', theme: theme.id });
    }
    const prevTheme = document.body.getAttribute('data-theme');
    document.body.setAttribute('data-theme', theme.id);
    // Push RGB values for rgba() usage (avoid color-mix perf cost)
    document.body.style.setProperty('--violet-rgb', theme.rgbA);
    document.body.style.setProperty('--cyan-rgb', theme.rgbB);
    document.body.style.setProperty('--emerald-rgb', theme.rgbEmerald);
    if (persist !== false) {
      try { localStorage.setItem(THEME_STORAGE_KEY, theme.id); } catch {}
    }
    // Keep theme switching compositor-friendly: do not stack animated overlay
    // elements or repeatedly initialize global effects. The dashboard is one DOM;
    // only its data-theme attribute changes.
    if (prevTheme !== theme.id && prevTheme !== null) {
      document.body.classList.add('theme-switching');
      requestAnimationFrame(() => document.body.classList.remove('theme-switching'));
    }
    document.querySelectorAll('#themePicker [data-theme-id]').forEach(card => {
      card.classList.toggle('theme-active', card.dataset.themeId === theme.id);
    });
    _currentThemeId = theme.id;
    // Start/stop the matrix rain loop with the theme: it renders only while
    // data-theme is "matrix" (see _requestMatrixFrame / _renderMatrixFrame).
    if (theme.id === 'matrix') _requestMatrixFrame();
      // Effects are initialized once during startup; switching palettes must not
      // add duplicate resize/mouse listeners or animation loops.
      // Refresh the dashboard theme row
  }

  // The host calls this through CoreWebView2.ExecuteScriptAsync.
  window.applyTheme = applyTheme;


  // ---------- i18n (Dil folder) ----------
  // The full English dictionary is embedded as the last-resort fallback (it mirrors
  // Dil/en.json), so the UI never shows raw keys even if external files fail to load.
  // Per-language files override it at runtime. Switching language applies instantly, exactly
  // like the theme picker - no restart needed.
  const _fallbackDict = {
    "sidebar.menu": "Menu",
    "sidebar.subtitle": "Gaming Private Network",
    "nav.dashboard": "Dashboard",
    "nav.nodes": "VPN Routes",
    "nav.boost": "GPN Game Boost",
    "nav.perf": "Performance",
    "nav.settings": "Settings",
    "nav.about": "About & Help",
    "quick.title": "Quick Connection",
    "quick.sub": "One-click network preferences",
    "quick.hint": "Applied when you press CONNECT; mode &amp; routing live on the Dashboard.",
    "quick.autoReconnect": "Auto reconnect",
    "quick.capture": "Capture",
    "quick.mode": "Mode",
    "quick.protocol": "Protocol",
    "quick.settings": "Connection settings",
    "settings.stack": "Stack",
    "tun.stack": "TUN Stack",
    "quick.routeOff": "Route: Off",
    "quick.routeVpn": "Route: Global VPN",
    "quick.routeGpn": "Route: GPN Game Tunnel",
    "header.controlDeck": "Control Deck",
    "header.connectionCenter": "Connection Center",
    "view.dashboard.title1": "Connection ",
    "view.dashboard.title2": "Center",
    "view.dashboard.sub": "Realtime tunnel status · low-latency game routing",
    "view.nodes.title1": "VPN ",
    "view.nodes.title2": "Nodes",
    "view.nodes.sub": "Pick a server — the tunnel connects through your chosen node",
    "view.boost.title1": "GPN ",
    "view.boost.title2": "Game Boost",
    "view.boost.sub": "Low-latency routing for your games",
    "view.perf.title1": "Performance ",
    "view.perf.title2": "Monitor",
    "view.perf.sub": "Real-time metrics across your tunnel",
    "view.settings.title1": "Application ",
    "view.settings.title2": "Settings",
    "view.settings.sub": "Tune AO GPN to your liking",
    "view.about.title1": "About & ",
    "view.about.title2": "Help",
    "view.about.sub": "Program info, helpful links and feedback",
    "about.description": "Gaming Private Network — a modern VPN/GPN client with per-app game routing, 11 themes and a live connection monitor.",
    "about.thanks": "Thanks for using AO GPN!",
    "about.tabInfo": "Program Info",
    "about.tabHelp": "Help & Feedback",
    "about.version": "Version",
    "about.author": "Maintainer",
    "about.license": "License",
    "about.platform": "Platform",
    "about.cores": "Cores",
    "about.homepage": "Homepage / Wiki",
    "about.source": "Source code",
    "about.helpWiki": "Visit the Wiki",
    "about.helpWikiDesc": "Guides, configuration and usage documentation.",
    "about.helpBug": "Report a bug",
    "about.helpBugDesc": "Open a GitHub issue with a pre-filled template.",
    "about.helpFeature": "Request a feature",
    "about.helpFeatureDesc": "Suggest an idea for an upcoming version.",
    "about.helpTg": "Telegram group",
    "about.helpTgDesc": "Ask the community in real time.",
    "about.helpChannel": "Telegram channel",
    "about.helpChannelDesc": "Announcements and release notes.",
    "about.tabRelease": "Release Notes",
    "about.releaseIntro": "What's new in this version — every step that built it.",
    "about.releaseExpandAll": "Expand all",
    "about.releaseCollapseAll": "Collapse all",
    "about.releaseEmpty": "Could not load the release notes.",
    "connect.connect": "CONNECT",
    "connect.connected": "CONNECTED",
    "status.connected": "CONNECTED",
    "status.disconnected": "DISCONNECTED",
    "status.connecting": "CONNECTING…",
    "connect.connecting": "CONNECTING…",
    "mode.globalVpn": "Global VPN",
    "mode.gpn": "GPN Game Tunnel",
    "mode.active": "ACTIVE",
    "mode.primary": "PRIMARY",
    "dash.routeMode": "Route mode",
    "dash.capture": "Capture",
    "dash.protocolStrategy": "Protocol strategy",
    "dash.modeStrategy": "Connection mode",
    "dash.routeHintVpn": "Global VPN — all traffic is routed through the TUN tunnel. TUN captures every packet at the network layer.",
    "dash.routeHintGpn": "GPN Game Tunnel — only apps assigned in Game Boost are tunneled; all other traffic stays direct.",
    "transport.proxy": "Proxy",
    "transport.tun": "TUN",
    "transport.locked": "LOCKED",
    "transport.lockGlobalVpn": "Global VPN locks capture to TUN — every app is captured at the network layer so nothing can bypass the tunnel. Choose GPN Game Tunnel to pick Proxy capture.",
    "transport.hintProxy": "Proxy — sets the system proxy when connected; apps that honor it flow through the tunnel. No admin rights needed.",
    "transport.hintTun": "TUN — captures ALL network traffic at the network layer. Requires administrator privileges.",
    "transport.hintLocked": "TUN — captures ALL network traffic at the network layer, but requires administrator privileges. CONNECT is locked — relaunch as admin or switch to Proxy capture.",
    "protocol.auto": "Automatic",
    "protocol.recommended": "RECOMMENDED",
    "protocol.wireguard": "WireGuard",
    "protocol.mimic": "Mimic",
    "protocol.reality": "Reality/TLS",
    "protocol.hysteria2": "Hysteria2 / TUIC",
    "protocol.openvpn": "OpenVPN",
    "protocol.hint.auto": "Automatic keeps the selected profile and uses its native core; no credentials are converted.",
    "protocol.hint.wireguard": "WireGuard is fastest when a real WireGuard profile is available; it cannot be emulated from another node.",
    "protocol.hint.mimic": "Mimic uses Xray/sing-box Reality or TLS fingerprinting from a compatible profile.",
    "protocol.hint.hysteria2": "Hysteria2/TUIC uses QUIC and congestion control for high-loss or unstable paths.",
    "protocol.hint.openvpn": "OpenVPN uses the native provider profile and owns its TUN tunnel.",
    "stat.yourIp": "Your IP",
    "stat.node": "Node",
    "stat.session": "Session",
    "stat.verification": "Verification",
    "stat.ping": "Ping",
    "stat.packetLoss": "Packet Loss",
    "stat.download": "Download",
    "stat.upload": "Upload",
    "topbar.node": "Node",
    "topbar.loadingNodes": "Loading nodes…",
    "topbar.noNodes": "No nodes",
    "proxyOnly.noNode": "No node is selected for the system proxy — select a route in VPN Routes first",
    "proxyOnly.configFailed": "The system-proxy core configuration could not be created",
    "proxyOnly.unsupportedNode": "The selected route type does not support the system proxy",
    "proxyOnly.startFailed": "The system-proxy core could not be started",
    "proxyOnly.pending": "System proxy preference saved — it will be applied automatically when the core is ready",
    "proxyOnly.applied": "System proxy applied → {address}",
    "proxyOnly.appliedPac": "System proxy applied via PAC",
    "proxyOnly.unchanged": "System proxy left unchanged",
    "proxyOnly.cleared": "System proxy cleared",
    "proxyOnly.updateFailed": "System proxy update failed",
    "settings.language": "Language",
    "boost.title": "GPN Game Boost",
    "boost.full": "Full Game Boost",
    "gpn.connect": "GPN Connect",
    "gpn.connectSub": "Auto-select best Italy/Germany server",
    "gpn.servers.active": "Active",
    "gpn.servers.add": "Add",
    "gpn.servers.confirmDelete": "Delete this GPN server?",
    "gpn.servers.delete": "Delete",
    "gpn.servers.disable": "Disable",
    "gpn.servers.disabled": "Disabled",
    "gpn.servers.dpapiNote": "🔐 DPAPI protected",
    "gpn.servers.edit": "Edit",
    "gpn.servers.empty": "No GPN servers imported yet.",
    "gpn.servers.emptyConf": "Paste a WireGuard .conf first.",
    "gpn.servers.enable": "Enable",
    "gpn.servers.import": "Import",
    "gpn.servers.importDesc": "Paste an Italy/Germany WireGuard .conf below. The client private key is encrypted with Windows DPAPI before it touches disk.",
    "gpn.servers.importTitle": "Import WireGuard .conf",
    "gpn.servers.managed": "Managed servers",
    "gpn.servers.managedDesc": "Servers listed here are the Italy/Germany candidates auto-selected on GPN Connect. Disabled ones are skipped.",
    "gpn.servers.defaultsTitle": "Embedded defaults",
    "gpn.servers.restoreDefaults": "Restore defaults",
    "gpn.servers.defaultsEmpty": "No defaults status yet — open Server Management.",
    "gpn.servers.defaultsNotSeeded": "Not seeded",
    "gpn.servers.defaultsUpToDate": "Up to date",
    "gpn.servers.defaultsOutdated": "Outdated",
    "gpn.servers.defaultsNoKey": "no key",
    "gpn.servers.probe": "Measure",
    "gpn.servers.probeTitle": "Live latency · UDP path",
    "gpn.servers.refresh": "Refresh",
    "gpn.servers.title": "GPN Servers",
    "gpn.diag.title": "GPN diagnostics",
    "gpn.diag.empty": "No GPN diagnostics yet — connect to see the live pipeline.",
    "gpn.diag.clear": "Clear",
    "gpn.recovery.title": "Auto-recover after V2ray",
    "gpn.recovery.sub": "After a V2rayTCP fallback, watch for a healthy server and return to WireGuard automatically.",
    "gpn.failover.title": "Stable connection (no auto switch)",
    "gpn.failover.sub": "OFF = stick to the chosen server for the most stable, uninterrupted connection. ON = auto-switch/failover allowed.",
    "gpn.telemetry.title": "Failover telemetry",
    "gpn.telemetry.reset": "Reset",
    "gpn.telemetry.switchShort": "switch",
    "gpn.telemetry.deathShort": "death",
    "gpn.telemetry.fallbackShort": "fallback",
    "gpn.telemetry.recoverShort": "recover",
    "gpn.telemetry.selectShort": "select",
    "gpn.reslog.title": "Last 50 decisions",
    "gpn.reslog.empty": "No decisions recorded yet.",
    "gpn.reslog.refresh": "Refresh",
    "gpn.reslog.clear": "Clear",
    "gpn.reslog.path": "Mirrored to",
    "gpn.reslog.action.switch": "Server switch",
    "gpn.reslog.action.udpDeath": "UDP dead",
    "gpn.reslog.action.modeFallback": "Fallback",
    "gpn.reslog.action.recover": "Recovery",
    "gpn.reslog.action.select": "Selected",
    "gpn.cluster.title": "Server cluster",
    "gpn.cluster.measure": "Measure",
    "gpn.cluster.empty": "No active servers — GPN Connect will not auto-select.",
    "gpn.cluster.best": "best",
    "gpn.cluster.open": "open",
    "gpn.cluster.egressHintLabel": "physical NIC",
    "gpn.cluster.egressHint": "Tunnel active — ICMP is skipped and latency is measured over the physical NIC (UDP handshake RTT), so the values show real server reachability, not the tunnel path.",
    "gpn.pidpool.title": "PID pool",
    "gpn.pidpool.watching": "watching",
    "gpn.pidpool.idle": "idle",
    "gpn.pidpool.pids": "PIDs",
    "gpn.pidpool.offline": "Target offline — no PIDs yet",
    "gpn.pidpool.offlineShort": "offline",
    "gpn.pidpool.empty": "No vpn-routed game yet — assign a game in Game Boost to build the pool.",
    "gpn.pidpool.start": "Watch",
    "gpn.pidpool.stop": "Stop",
    "gpn.pidpool.refresh": "Refresh",
    "gpn.pidpool.fatal": "fatal",
    "gpn.pidpool.degraded": "degraded",
    "gpn.capture.title": "Captured traffic",
    "gpn.capture.empty": "No packets captured yet — GPN not connected or no game traffic.",
    "gpn.capture.flows": "Top flows",
    "gpn.capture.perPid": "Per-PID",
    "gpn.captureSet.title": "WinDivert queue",
    "gpn.captureSet.save": "Apply",
    "gpn.captureSet.queueLen": "Queue len",
    "gpn.captureSet.queueTime": "Queue time (ms)",
    "gpn.captureSet.queueSize": "Queue size (B)",
    "gpn.captureSet.enableLen": "len",
    "gpn.captureSet.enableTime": "time",
    "gpn.captureSet.enableSize": "size",
    "gpn.captureSet.layer": "Layer",
    "gpn.captureSet.direction": "Direction",
    "gpn.captureSet.hint": "Values apply to the next capture start; the running loop keeps its current parameters.",
    "gpn.wintunSet.title": "Wintun adapter",
    "gpn.wintunSet.save": "Apply",
    "gpn.wintunSet.adapter": "Adapter name prefix",
    "gpn.wintunSet.ring": "Ring capacity (B)",
    "gpn.wintunSet.hint": "Applies to the next WireGuard connect; the server id is appended to the adapter name (AoGPN-it). Ring capacity is clamped to 128 KiB–64 MiB and rounded to a power of two.",
    "gpn.capture.out": "out",
    "gpn.capture.in": "in",
    "gpn.capture.unknown": "unknown PID",
    "gpn.matrix.title": "Failover matrix",
    "gpn.matrix.empty": "No measurement yet — run a server probe.",
    "gpn.matrix.default": "Default",
    "gpn.matrix.strict": "Strict",
    "gpn.matrix.keep": "Keep",
    "gpn.matrix.switchTo": "Switch →",
    "gpn.matrix.fallback": "Fallback V2rayTCP",
    "gpn.matrix.legend": "✔ healthy · ✘ dead · first column default, second strict",
    "gpn.candidate.title": "Best candidate",
    "gpn.candidate.empty": "No measurement yet — run a server probe.",
    "gpn.candidate.wireguard": "WireGuard · Tier 2",
    "gpn.candidate.v2ray": "V2rayTCP · Tier 3",
    "gpn.candidate.selected": "will connect",
    "gpn.candidate.none": "No WireGuard server selectable",
    "gpn.candidate.hint": "What GPN Connect will pick right now — same decision logic, no tunnel started.",
    "nav.gpnServers": "GPN Servers",
    "view.gpnServers.sub": "Manage the Italy/Germany WireGuard servers auto-selected on GPN Connect",
    "view.gpnServers.title1": "GPN ",
    "view.gpnServers.title2": "Servers",
    "boost.loading": "Loading…",
    "boost.noGames": "No games assigned yet. Add EXE files in the {link} view to enable per-game tunnel routing.",
    "boost.noGamesShort": "No games assigned yet. Add apps in the Game Boost view.",
    "boost.running": "{n} games boosted",
    "boost.defined": "{n} apps defined",
    "boost.before": "Before",
    "boost.after": "After",
    "boost.viaNode": "via {node}",
    "boost.none": "No games defined",
    "boost.routeActive": "Route: {route} · active",
    "boost.idleNoBoost": "Idle · no boost",
    "global.allTraffic": "All Traffic Tunnelled",
    "global.transport": "Transport",
    "global.modeActive": "mode active",
    "global.ipVerification": "IP Verification",
    "global.coverage": "Coverage",
    "global.coverage100": "100% of traffic",
    "global.transportDesc": "All network traffic is captured at the network layer and routed through the VPN tunnel.",
    "global.coverageDesc": "All applications, services, and system processes route through the secure tunnel. No traffic bypasses the VPN.",
    "global.notConnected": "⚪ Not connected",
    "global.disconnectedDesc": "Disconnected — all traffic uses your direct internet connection.",
    "global.tunnelVerified": "✅ Tunnel verified",
    "global.tunnelVerifiedDesc": "IP changed to {ip}. Traffic is securely routed.",
    "global.leaking": "⚠️ Leaking",
    "global.leakingDesc": "IP unchanged — {ip}. Check transport settings.",
    "global.probing": "🔄 Probing…",
    "global.probingDesc": "Verifying tunnel exit IP.",
    "global.disconnected": "Disconnected",
    "dir.title": "Routing direction",
    "dir.whitelist": "Whitelist",
    "dir.blacklist": "Blacklist",
    "dir.hintWhitelist": "Whitelist — only assigned games/apps are tunneled; all other traffic stays direct.",
    "dir.hintBlacklist": "Blacklist — assigned games/apps stay direct; all other traffic is tunneled.",
    "dir.blacklistTableHint": "Blacklist active — assigned apps stay direct; everything else is tunneled. A 'VPN' assignment keeps an app OUTSIDE the tunnel; 'Direct' tunnels it. The route column shows the effective route.",
    "dir.rowExcluded": "Outside tunnel",
    "dir.rowTunneled": "Tunneled",
    "dir.tipRowExcluded": "Blacklist: a 'VPN' assignment keeps this app OUTSIDE the tunnel — it goes direct.",
    "dir.tipRowTunneled": "Blacklist: a 'Direct' assignment routes this app INTO the tunnel.",
    "dir.selectVpn": "VPN · outside tunnel",
    "dir.selectDirect": "Direct · tunneled",
    "dir.selectBlock": "Block",
    "dir.selectWarp": "WARP · outside tunnel",
    "tip.dirWhitelist": "Whitelist — only the games/apps assigned in Game Boost are routed through the tunnel; everything else stays direct.",
    "tip.dirBlacklist": "Blacklist — everything except the games/apps assigned in Game Boost is routed through the tunnel.",
    "route.assign": "Assign route…",
    "route.vpn": "VPN",
    "route.direct": "Direct",
    "route.block": "Block",
    "route.warp": "WARP",
    "warp.fallbackNotice": "WARP route needs a WireGuard node — the active connection is not WireGuard, so WARP apps are routed through VPN.",
    "nodes.title": "VPN Routes",
    "nodes.countryUnknown": "Other",
    "nodes.showAll": "Show all",
    "nodes.hideAll": "Hide all",
    "nodes.showAll": "Show all",
    "nodes.hideAll": "Hide all",

    "nodes.selectedNode": "Selected node:",
    "nodes.sortDefault": "Default order",
    "nodes.sortCountry": "Country A-Z",
    "nodes.sortFav": "Favorites first",
    "nodes.sortRecent": "Recently used",
    "nodes.pool": "Node pool",
    "nodes.poolDesc": "Add GitHub / .txt links that publish node lists — one click pulls them into your nodes.",
    "nodes.poolFetch": "Download new nodes",
    "nodes.poolAdd": "Add link",
    "nodes.poolRemove": "Remove",
    "nodes.poolEmpty": "No links in the pool yet.",
    "settings.title": "Application Settings",
    "settings.save": "Save Settings",
    "monitor.title": "Connection Monitor",
    "monitor.connections": "Connections",
    "monitor.activeSockets": "active sockets",
    "status.idle": "⚪ Idle",
    "status.notConnected": "not connected",
    "status.tunneled": "✅ Tunneled",
    "status.probing": "🔄 Probing…",
    "status.retrying": "🔄 Retrying…",
    "status.leaking": "⚠️ Leaking",
    "status.checking": "Checking",
    "ip.tunnel": "tunnel",
    "ip.isp": "ISP",
    "ip.leaking": "leaking!",
    "ip.probing": "probing tunnel…",
    "ip.pending": "IP check pending…",
    "ip.detail.tunActive": "TUN active",
    "ip.detail.socksVerified": "SOCKS5 verified",
    "ip.detail.fetchingBaseline": "fetching ISP baseline…",
    "ip.detail.willRetry": "tunnel probe pending, will retry",
    "ip.detail.possibleLeak": "IP unchanged — possible leak",
    "ip.measuredAgo": "measured {s}s ago",
    "ip.detail.staleMeasure": "Stale measurement — re-checking",
    "telemetry.measuring": "measuring…",
    "telemetry.stable": "stable",
    "status.line.live": "Live — Route {route} · Capture {capture}. The tunnel is active.",
    "status.line.locked": "TUN requires administrator privileges — CONNECT is locked. Relaunch as admin or switch to {capture} capture.",
    "status.line.idle": "Route {route} · Capture {capture} — press {connect} to start the tunnel.",
    "footer.brand": "AO GPN · Gaming Private Network",
    "telemetry.excellent": "excellent",
    "telemetry.good": "good",
    "telemetry.warning": "warning",
    "ip.leakBanner": "You are connected but your public IP has not changed ({ip}) — traffic may be leaking. Check your capture settings or switch to TUN.",
    "warp.healthBanner": "WARP outbound dial is failing — launcher/regional traffic may not pass. Check the diag log for WARP_DIAL lines.",
    "warp.degradedBanner": "WARP is down — launcher/API traffic is now going DIRECT until WARP recovers.",
    "warp.degradedTag": "Launcher direct",
    "tip.modeGpn": "GPN Game Tunnel — only assigned games and apps are tunneled; everything else stays direct.",
    "tip.modeVpn": "Global VPN — all traffic is routed through the tunnel using TUN capture.",
    "tip.transportProxy": "Proxy — apps that honor the system proxy flow through the tunnel. No admin rights needed.",
    "tip.transportTun": "TUN — captures all network traffic at the network layer. Requires administrator privileges.",
    "tip.protocolAuto": "Automatic — uses the selected profile’s native core; no credentials are converted.",
    "tip.protocolWireguard": "WireGuard — fastest with a real WireGuard profile; cannot be emulated from another node.",
    "tip.protocolMimic": "Mimic — Reality/TLS browser fingerprinting from a compatible Xray/sing-box profile.",
    "tip.protocolHysteria2": "Hysteria2 / TUIC — QUIC and congestion control for high-loss or unstable paths.",
    "tip.protocolOpenvpn": "OpenVPN — uses the native provider profile and owns its TUN tunnel.",
    "tip.proxyControl": "System proxy — toggles the OS proxy (127.0.0.1:port) on or off, independent of the VPN tunnel.",
    "tip.proxyMode": "Proxy mode — Clear (off) · Set (on) · Unchanged · PAC.",
    "tip.proxyTest": "Test — verifies the local SOCKS5 listener and shows its latency.",
    "tip.nodeSelect": "Node — the tunnel routes through the selected server. Manage nodes in the Nodes view.",
    "tip.connectIdle": "Connect — route {route} via {capture} capture through the selected node.",
    "tip.connectActive": "Connected — press to disconnect. The system proxy returns to your independent preference.",
    "tip.connectLocked": "TUN requires administrator privileges — CONNECT is locked. Relaunch as admin or switch to Proxy capture.",
    "tip.statusIdle": "No active tunnel — press CONNECT to route traffic through the selected node.",
    "tip.statusActive": "Tunnel active — {route} via {capture} capture.",
    "theme.arctic-ice": "Arctic Ice",
    "theme.arctic-ice.tagline": "Glacial arctic ice",
    "theme.aurora": "Aurora",
    "theme.aurora.tagline": "Warm copper over deep teal",
    "theme.candy": "Candy",
    "theme.candy.tagline": "Playful rose and mint candy",
    "theme.crimson": "Crimson",
    "theme.crimson-core": "Crimson Core",
    "theme.crimson-core.tagline": "Reactor crimson core",
    "theme.crimson.tagline": "Tactical redline, black ops",
    "theme.cryo": "Cryo",
    "theme.cryo.tagline": "Glacial blue, frost indigo",
    "theme.cyberpunk": "Cyberpunk",
    "theme.cyberpunk.tagline": "Amber neon, crimson haze",
    "theme.gold-elite": "Gold Elite",
    "theme.gold-elite.tagline": "Premium gold elite",
    "theme.inferno": "Inferno",
    "theme.inferno.tagline": "Ember red, molten amber",
    "theme.matrix": "Matrix",
    "theme.matrix-green": "Matrix Green",
    "theme.matrix-green.tagline": "Terminal phosphor green",
    "theme.matrix.tagline": "Phosphor green, dark terminal",
    "theme.nebula": "Nebula",
    "theme.nebula.tagline": "Deep space violet aurora",
    "theme.neon-cyber": "Neon Cyber",
    "theme.neon-cyber.tagline": "Neon cyan over electric violet",
    "theme.obsidian": "Obsidian",
    "theme.obsidian.tagline": "Monochrome slate and steel",
    "theme.ocean-blue": "Ocean Blue",
    "theme.ocean-blue.tagline": "Deep ocean azure",
    "theme.phantom": "Phantom",
    "theme.phantom.tagline": "Spectral indigo, misty white",
    "theme.plasma": "Plasma",
    "theme.plasma.tagline": "Electric violet, deep blue",
    "theme.red-phantom": "Red Phantom",
    "theme.red-phantom.tagline": "Aggressive competitive red",
    "theme.sandstorm": "Sandstorm",
    "theme.sandstorm.tagline": "Dune sand and oasis teal",
    "theme.stealth-camo": "Stealth Camo",
    "theme.stealth-camo.tagline": "Tactical stealth camo",
    "theme.synthwave": "Synthwave",
    "theme.synthwave.tagline": "Neon magenta, retro rose",
    "theme.titanium-orange": "Titanium Orange",
    "theme.titanium-orange.tagline": "Molten titanium orange",
    "theme.velocity": "Velocity",
    "theme.velocity.tagline": "High-speed redline telemetry",
    "theme.venom": "Venom",
    "theme.venom.tagline": "Toxic lime, bio emerald",
    "theme.violet-nova": "Violet Nova",
    "theme.violet-nova.tagline": "Luxury violet nova",
  };
  let _dict = {};
  let _currentLang = 'en';
  let _lastIpState = null;
  let _lastIpMeasuredAt = null; // son ölçümün ISO zaman damgası (host measuredAt)
  let _lastTelemetry = null;
  let _activeGpnServer = ''; // "sonra" ölçümünün yapıldığı seçili/aktif GPN düğümü (via etiketi)
  let _activeGpnMode = '';  // aktif GPN bağlantı modu ('WireGuard' | 'V2rayTCP' | '') — WARP uyarısı buna göre verilir

  function t(key, params) {
    let val = null;
    if (_dict && Object.prototype.hasOwnProperty.call(_dict, key)) val = _dict[key];
    if ((val === null || val === undefined || val === '') && _fallbackDict && Object.prototype.hasOwnProperty.call(_fallbackDict, key)) val = _fallbackDict[key];
    if (val === null || val === undefined || val === '') return key;
    if (params) {
      Object.keys(params).forEach(k => { val = String(val).split('{' + k + '}').join(String(params[k])); });
    }
    return String(val);
  }

  function applyTexts() {
    document.querySelectorAll('[data-i18n]').forEach(el => {
      const key = el.getAttribute('data-i18n');
      const val = t(key);
      if (!val || val === key) return;
      // Replace text nodes in the subtree but keep SVG icons and nested elements
      // (spans, badges) so structure and styling survive translation.
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
        acceptNode(n) {
          const p = n.parentElement;
          if (p && p.closest && p.closest('svg,script,style')) return NodeFilter.FILTER_REJECT;
          return NodeFilter.FILTER_ACCEPT;
        }
      });
      let replaced = false;
      while (walker.nextNode()) {
        const node = walker.currentNode;
        if (node.nodeValue && node.nodeValue.trim() !== '') {
          node.nodeValue = val;
          replaced = true;
        }
      }
      if (!replaced && el.children.length === 0) el.textContent = val;
    });
  }

  async function loadLanguage(lang) {
    _currentLang = (lang && String(lang)) || 'en';
    let enDict = null, langDict = null;
    try {
      const r1 = await fetch('Dil/en.json', { cache: 'no-store' });
      if (r1.ok) enDict = await r1.json();
    } catch (e) {}
    if (_currentLang !== 'en') {
      try {
        const r2 = await fetch('Dil/' + _currentLang + '.json', { cache: 'no-store' });
        if (r2.ok) langDict = await r2.json();
      } catch (e) {}
    }
    _dict = Object.assign({}, (enDict && typeof enDict === 'object') ? enDict : {}, (langDict && typeof langDict === 'object') ? langDict : {});
    applyTexts();
    refreshDynamicTexts();
    // Standalone skins resolve their own texts through skinBridge.t(); a
    // language switch must re-render them instantly, like the main dashboard.
    skinNotify();
    return _dict;
  }

  // Re-applies strings written by JS from the current state so a language switch
  // updates them instantly (no restart), like the theme picker.
  function refreshDynamicTexts() {
    if (typeof refreshConnectLabels === 'function') refreshConnectLabels();
    if (typeof applyMode === 'function') applyMode();
    if (typeof renderTopNodeSelect === 'function') renderTopNodeSelect();
    if (typeof updateTransportLock === 'function') updateTransportLock();
    if (typeof applyProtocolPreference === 'function') applyProtocolPreference(protocolPreference);
    if (_lastIpState) applyRealIpState(_lastIpState);
    if (_lastTelemetry) updateTelemetry(_lastTelemetry[0], _lastTelemetry[1], _lastTelemetry[2], _lastTelemetry[3]);
    if (typeof renderDashboardBoostCards === 'function') renderDashboardBoostCards();
  }

  function refreshConnectLabels() {
    const routeLabel = t(mode === 'gpn' ? 'mode.gpn' : 'mode.globalVpn');
    const captureLabel = transport === 'tun' ? t('transport.tun') : t('transport.proxy');
    if (statusLabel) statusLabel.textContent = connected ? t('status.connected') : t('status.disconnected');
    if (btnText) btnText.textContent = connected ? t('connect.connected') : t('connect.connect');
    if (btnSub) btnSub.textContent = routeLabel + (connected ? ' · ' + t('mode.active') : '');
    if (typeof updateStatusLine === 'function') updateStatusLine();
  }

  // Available languages in the same order the old native select listed them.
  const LANGUAGES = [
    { code: 'zh-Hans', name: '简体中文' },
    { code: 'zh-Hant', name: '繁體中文' },
    { code: 'en', name: 'English' },
    { code: 'fa', name: 'فارسی' },
    { code: 'fr', name: 'Français' },
    { code: 'hu', name: 'Magyar' },
    { code: 'id', name: 'Indonesia' },
    { code: 'ru', name: 'Русский' },
    { code: 'tr', name: 'Türkçe' }
  ];

  function langName(code) {
    const found = LANGUAGES.find(l => l.code === code);
    return found ? found.name : code;
  }

  // ISO 3166-1 alpha-2 ülke kodunu (örn. "IT") kullanıcının dilinde ülke adına
  // çevirir (tr'de "İtalya", en'de "Italy"). Intl.DisplayNames tüm kodları ve
  // tüm dilleri kapsar — ayrı bir sözlük gerektirmez. Kod tanınmıyorsa veya API
  // yoksa boş döner; çağıran taraf koda düşer.
  function countryDisplayName(code) {
    if (!code) return '';
    try {
      const loc = (_currentLang && _currentLang !== 'en') ? _currentLang : 'en';
      const upper = String(code).toUpperCase();
      const name = new Intl.DisplayNames([loc], { type: 'region' }).of(upper);
      return (name && name !== upper) ? name : '';
    } catch (e) {
      return '';
    }
  }

  function renderLangPopover() {
    const pop = document.getElementById('dashboardLangPopover');
    if (!pop) {
      return;
    }
    const current = _currentLang || 'en';
    const label = document.getElementById('dashboardLangLabel');
    if (label) {
      label.textContent = langName(current);
    }
    pop.innerHTML = LANGUAGES.map(l => {
      const on = l.code === current;
      return `<button type="button" data-lang="${l.code}" role="option" aria-selected="${on}"
        class="w-full flex items-center justify-between gap-2 rounded-lg px-3 py-1.5 text-[11px] font-semibold transition-colors ${on ? 'bg-white/8 text-white' : 'text-[#A7B0BF] hover:bg-white/5 hover:text-slate-200'}">
        <span>${escHtml(l.name)}</span>
        ${on ? '<svg class="w-3.5 h-3.5 text-cyan-300 shrink-0" fill="currentColor" viewBox="0 0 24 24"><path d="M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41Z"/></svg>' : ''}
      </button>`;
    }).join('');
  }

  // The host calls this through CoreWebView2.ExecuteScriptAsync and the dropdown
  // uses it directly, so selecting a language applies it immediately.
  window.applyLanguage = function(lang) {
    loadLanguage(lang);
    const label = document.getElementById('dashboardLangLabel');
    if (label) {
      label.textContent = langName(lang);
    }
    renderLangPopover();
    // Theme tooltips carry translated names/taglines, so re-render them so a
    // language switch updates the picker immediately.
    if (typeof renderTopThemePopover === 'function') renderTopThemePopover();
  };

  // Wire the custom language dropdown: apply instantly (like themes) and persist
  // to the host. The native <select> popup clashed with the dark theme, so this
  // is a themed popover identical in behaviour, plus full keyboard support:
  //   • ↓/↑ open (when closed) and move between options (when open)
  //   • Enter/Space select the focused option
  //   • Esc closes and returns focus to the trigger
  //   • Home/End jump to the first/last option
  const langBtn = document.getElementById('dashboardLangBtn');
  const langPop = document.getElementById('dashboardLangPopover');
  if (langBtn && langPop) {
    function openLangPop() {
      renderLangPopover();
      langPop.classList.remove('hidden');
      langBtn.setAttribute('aria-expanded', 'true');
      // Keyboard users start from the currently selected language so ↓/↑ move
      // relative to it and Enter re-picks it without a mouse.
      const sel = langPop.querySelector('[aria-selected="true"]') || langPop.querySelector('[data-lang]');
      if (sel) sel.focus();
    }
    function closeLangPop() {
      langPop.classList.add('hidden');
      langBtn.setAttribute('aria-expanded', 'false');
    }
    function langOptions() {
      return [...langPop.querySelectorAll('[data-lang]')];
    }
    function moveLangFocus(step) {
      const opts = langOptions();
      if (!opts.length) return;
      const i = opts.indexOf(document.activeElement);
      const next = step === 'down' ? (i + 1) % opts.length : (i - 1 + opts.length) % opts.length;
      opts[next].focus();
    }
    function selectLangFocus() {
      const active = document.activeElement && document.activeElement.closest('[data-lang]');
      const code = active ? active.dataset.lang : (langPop.querySelector('[aria-selected="true"]') || {}).dataset;
      if (code) {
        applyLanguage(code);
        postToHost({ action: 'set_language', lang: code });
        closeLangPop();
        langBtn.focus();
      }
    }
    langBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (langPop.classList.contains('hidden')) openLangPop(); else closeLangPop();
    });
    langBtn.addEventListener('keydown', (e) => {
      if (langPop.classList.contains('hidden')) {
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Enter' || e.key === ' ') {
          // preventDefault stops the synthetic click, so the popover stays open
          // after the keyboard opens it (no double-toggle).
          e.preventDefault();
          openLangPop();
        }
        return;
      }
      if (e.key === 'ArrowDown') { e.preventDefault(); moveLangFocus('down'); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); moveLangFocus('up'); }
      else if (e.key === 'Home') { e.preventDefault(); const o = langOptions(); if (o.length) o[0].focus(); }
      else if (e.key === 'End') { e.preventDefault(); const o = langOptions(); if (o.length) o[o.length - 1].focus(); }
      else if (e.key === 'Escape') { e.preventDefault(); closeLangPop(); langBtn.focus(); }
      else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectLangFocus(); }
    });
    // Key events on the options bubble up here; preventDefault stops the option's
    // synthetic click so selection happens exactly once.
    langPop.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowDown') { e.preventDefault(); moveLangFocus('down'); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); moveLangFocus('up'); }
      else if (e.key === 'Home') { e.preventDefault(); const o = langOptions(); if (o.length) o[0].focus(); }
      else if (e.key === 'End') { e.preventDefault(); const o = langOptions(); if (o.length) o[o.length - 1].focus(); }
      else if (e.key === 'Escape') { e.preventDefault(); closeLangPop(); langBtn.focus(); }
      else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectLangFocus(); }
    });
    document.addEventListener('click', (e) => {
      if (!langPop.classList.contains('hidden') && !langPop.contains(e.target) && e.target !== langBtn && !langBtn.contains(e.target)) {
        closeLangPop();
      }
    });
    langPop.addEventListener('click', (e) => {
      const btn = e.target.closest('[data-lang]');
      if (!btn) {
        return;
      }
      const newLang = btn.dataset.lang;
      applyLanguage(newLang);
      postToHost({ action: 'set_language', lang: newLang });
      closeLangPop();
      langBtn.focus();
    });
  }

  // ---------------------------------------------------------------------------
  // Generic native <select> → themed dropdown upgrade.
  // The OS-drawn <select> popup is a tall white box that clashes with the dark
  // theme (same problem the language selector had). This upgrades any native
  // <select> in place: the original element stays in the DOM (so `.value` reads,
  // `.options` and existing `change` listeners all keep working) but is visually
  // hidden, and a themed button + popover render its options instead. Choosing
  // an option sets `sel.value` and dispatches a bubbling `change` event, so the
  // existing listeners (bindProtocolSelect, node sort, proxy mode, node switch,
  // TUN stack, route selectors) fire untouched.
  // ---------------------------------------------------------------------------
  const _csRegistry = [];

  function refreshCustomSelect(sel) {
    const rec = _csRegistry.find(r => r.sel === sel);
    if (!rec) return;
    const opt = sel.options[sel.selectedIndex];
    rec.label.textContent = opt ? opt.textContent : (sel.value || '');
    rec.btn.classList.toggle('cs-empty', !opt);
  }

  function refreshAllCustomSelects() {
    _csRegistry.forEach(r => refreshCustomSelect(r.sel));
  }

  function upgradeSelect(sel) {
    if (!sel || sel.dataset.csUpgraded) return;
    sel.dataset.csUpgraded = '1';

    const btn = document.createElement('button');
    btn.type = 'button';
    // Keep the select's own classes so the button inherits its contextual look
    // (e.g. .quick-select box, .settings-select full width, transparent pill
    // styling in the top bar); .cs-trigger only supplies layout + popover.
    btn.className = 'cs-trigger ' + (sel.className || '');
    btn.setAttribute('aria-haspopup', 'listbox');
    btn.setAttribute('aria-expanded', 'false');
    // Carry over accessible name + tooltip hooks so the custom tooltip engine
    // and screen readers keep working on the upgraded control.
    const ariaLabel = sel.getAttribute('aria-label');
    if (ariaLabel) btn.setAttribute('aria-label', ariaLabel);
    const tipKey = sel.getAttribute('data-tip-key');
    if (tipKey) btn.setAttribute('data-tip-key', tipKey);
    const title = sel.getAttribute('title');
    if (title) btn.setAttribute('title', title);

    const label = document.createElement('span');
    label.className = 'cs-label';
    btn.appendChild(label);

    const chev = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    chev.setAttribute('class', 'cs-chev');
    chev.setAttribute('viewBox', '0 0 24 24');
    chev.setAttribute('fill', 'none');
    chev.setAttribute('stroke', 'currentColor');
    chev.setAttribute('stroke-width', '2');
    chev.innerHTML = '<path d="m6 9 6 6 6-6"/>';
    btn.appendChild(chev);

    const pop = document.createElement('div');
    pop.className = 'cs-pop hidden';
    pop.setAttribute('role', 'listbox');
    // The popover must escape the sidebar's overflow-hidden containers, so it
    // lives on <body> and is positioned (fixed) against the button on open.
    document.body.appendChild(pop);

    // Insert the button where the select sits, then hide the native element.
    sel.parentNode.insertBefore(btn, sel);
    sel.classList.add('cs-hidden');

    const rec = { sel, btn, pop, label };
    _csRegistry.push(rec);

    function renderOptions() {
      pop.innerHTML = '';
      [...sel.options].forEach(o => {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'cs-option' + (o.selected ? ' active' : '');
        b.setAttribute('role', 'option');
        b.setAttribute('aria-selected', String(o.selected));
        b.textContent = o.textContent;
        b.addEventListener('click', (e) => {
          e.stopPropagation();
          sel.value = o.value;
          sel.dispatchEvent(new Event('change', { bubbles: true }));
          refreshCustomSelect(sel);
          close();
        });
        pop.appendChild(b);
      });
    }

    function open() {
      renderOptions();
      // Position against the button, clamped to the viewport so long option
      // lists never push off-screen. body popover => not clipped by the sidebar.
      // At least as wide as the trigger so short options never shrink the box
      // below the button.
      pop.style.minWidth = btn.offsetWidth + 'px';
      const r = btn.getBoundingClientRect();
      const vw = document.documentElement.clientWidth;
      const vh = document.documentElement.clientHeight;
      pop.style.left = Math.max(6, Math.min(r.left, vw - pop.offsetWidth - 6)) + 'px';
      const spaceBelow = vh - r.bottom;
      if (spaceBelow < 160 && r.top > spaceBelow) {
        // Flip above when there is more room up top.
        pop.style.top = Math.max(6, r.top - pop.offsetHeight - 6) + 'px';
      } else {
        pop.style.top = Math.min(r.bottom + 4, vh - pop.offsetHeight - 6) + 'px';
      }
      pop.classList.remove('hidden');
      btn.setAttribute('aria-expanded', 'true');
      // Keyboard users start from the currently selected option so ↓/↑ move
      // relative to it (same pattern as the language dropdown).
      const selOpt = pop.querySelector('.cs-option.active') || pop.querySelector('.cs-option');
      if (selOpt) selOpt.focus();
    }

    function close() {
      pop.classList.add('hidden');
      btn.setAttribute('aria-expanded', 'false');
    }

    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (pop.classList.contains('hidden')) open(); else close();
    });

    // Close on outside click, Escape and scroll (the popover is fixed on body,
    // so it must not float in place while the user scrolls).
    document.addEventListener('click', (e) => {
      if (!pop.classList.contains('hidden') && !btn.contains(e.target) && !pop.contains(e.target)) close();
    });
    document.addEventListener('scroll', (e) => {
      if (pop.classList.contains('hidden')) return;
      // Scrolling INSIDE the popover's own scrollable list (mouse wheel or
      // middle-button drag over the options) must NOT close it — only a scroll
      // of the page behind does.
      const t = e.target;
      if (t && t.nodeType === 1 && (t === pop || pop.contains(t))) return;
      close();
    }, true);
    function focusOpt(dir) {
      const opts = [...pop.querySelectorAll('.cs-option')];
      if (!opts.length) return;
      const i = opts.indexOf(document.activeElement);
      const next = dir === 'down' ? (i + 1) % opts.length : (i - 1 + opts.length) % opts.length;
      opts[next].focus();
    }

    btn.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        if (pop.classList.contains('hidden')) open(); else close();
      } else if (e.key === 'Escape') {
        close();
      } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        if (pop.classList.contains('hidden')) open(); else focusOpt(e.key === 'ArrowDown' ? 'down' : 'up');
      }
    });
    // Arrow/Home/End navigation + Escape inside the open listbox. Enter/Space
    // re-trigger the focused option's own click (preventDefault stops the
    // synthetic one, so selection happens exactly once).
    pop.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowDown') { e.preventDefault(); focusOpt('down'); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); focusOpt('up'); }
      else if (e.key === 'Home') { e.preventDefault(); const o = pop.querySelector('.cs-option'); if (o) o.focus(); }
      else if (e.key === 'End') { e.preventDefault(); const o = [...pop.querySelectorAll('.cs-option')]; if (o.length) o[o.length - 1].focus(); }
      else if (e.key === 'Escape') { e.preventDefault(); close(); btn.focus(); }
      else if (e.key === 'Enter' || e.key === ' ') {
        const opt = e.target.closest('.cs-option');
        if (opt) { e.preventDefault(); opt.click(); }
      }
    });

    refreshCustomSelect(sel);
    return rec;
  }


  // Compact theme popover in the title bar.
  // ---------- Layout axis: 'status' (sidebar controls) ↔ 'compact' (hero strip) ----------
  // Independent from the color theme; persisted under its own key so switching
  // colors never resets the arrangement.
  const LAYOUT_STORAGE_KEY = 'aogpn.layout';
  let _currentLayout = 'status';

  function applyLayout(id) {
    const next = id === 'compact' ? 'compact' : 'status';
    _currentLayout = next;
    document.body.setAttribute('data-layout', next);
    try { localStorage.setItem(LAYOUT_STORAGE_KEY, next); } catch {}
    document.querySelectorAll('#topLayoutRow [data-layout]').forEach(btn => {
      const on = btn.dataset.layout === next;
      btn.classList.toggle('layout-active', on);
      btn.setAttribute('aria-pressed', String(on));
    });
  }

  // The host can also switch the layout (e.g. from the native settings window).
  window.applyLayout = applyLayout;

  // Wire the popover's layout buttons exactly once (the theme grid re-renders,
  // the layout row is static HTML).
  (function wireLayoutRow() {
    const row = document.getElementById('topLayoutRow');
    if (!row || row.dataset.layoutWired) {
      return;
    }
    row.dataset.layoutWired = '1';
    row.querySelectorAll('[data-layout]').forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        applyLayout(btn.dataset.layout);
      });
    });
  })();

  // Translated display helpers: fall back to themes.json values when a locale has
  // no entry, so theme names never render as raw keys (e.g. 'theme.nebula').
  function themeName(theme) {
    const key = 'theme.' + theme.id;
    const val = t(key);
    return val && val !== key ? val : (theme.name || key);
  }
  function themeTagline(theme) {
    const key = 'theme.' + theme.id + '.tagline';
    const val = t(key);
    return val && val !== key ? val : (theme.tagline || '');
  }

  function renderTopThemePopover() {
    const grid = document.getElementById('topThemeGrid');
    if (!grid) return;
    // Keep the layout buttons in sync with the applied layout (e.g. host push).
    applyLayout(_currentLayout);
    grid.innerHTML = THEMES.map(theme => {
      const active = theme.id === _currentThemeId;
      const name = themeName(theme);
      const tagline = themeTagline(theme);
      const tip = tagline ? name + ' — ' + tagline : name;
      return `<button type="button" data-theme-id="${theme.id}" title="${escHtml(tip)}" aria-label="${escHtml('Theme: ' + name)}${active ? ' (active)' : ''}" aria-pressed="${active}"
        class="w-9 h-9 rounded-lg border ${active ? 'border-cyan-400/80 ring-1 ring-cyan-400/40' : 'border-white/10 hover:border-cyan-400/50'} transition-colors flex items-center justify-center"
        style="background:linear-gradient(135deg, ${theme.a}, ${theme.b})">
        ${active ? '<span class="w-2 h-2 rounded-full bg-white/90 shadow-[0_0_6px_rgba(255,255,255,.9)]"></span>' : ''}
      </button>`;
    }).join('');
    grid.querySelectorAll('[data-theme-id]').forEach(btn => btn.addEventListener('click', () => {
      applyTheme(btn.dataset.themeId);
      renderTopThemePopover();
      const popover = document.getElementById('topThemePopover');
      if (popover) popover.classList.add('hidden');
    }));
    // Wire up the top-right theme button exactly once — this function also runs
    // again after themes load from Temalar/themes.json, and re-adding the listener
    // would make one click toggle the popover twice (open+close = never opens).
    const btn = document.getElementById('topThemeBtn');
    const popover = document.getElementById('topThemePopover');
    if (btn && popover && !btn.dataset.themePopoverWired) {
      btn.dataset.themePopoverWired = '1';
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        popover.classList.toggle('hidden');
      });
      // Close popover when clicking outside.
      document.addEventListener('click', (e) => {
        if (!popover.contains(e.target) && e.target !== btn) {
          popover.classList.add('hidden');
        }
      });
    }
  }

  function collectSettings() {
    const s = {};
    document.querySelectorAll('[data-settings]').forEach(el => {
      const name = el.dataset.settings;
      if (name === 'destOverride') {
        return;
      }
      if (el.type === 'checkbox') {
        s[name] = el.checked;
      } else if (el.type === 'radio') {
        if (el.checked) {
          s[name] = el.value;
        }
      } else {
        s[name] = el.value;
      }
    });
    s.destOverride = [...document.querySelectorAll('input[data-settings="destOverride"]:checked')].map(c => c.value);
    return s;
  }

  window.setSettingsSaveResult = (result) => {
    settingsState.saving = false;
    const saveBtn = document.getElementById('settingsSaveBtn');
    if (saveBtn) {
      saveBtn.disabled = false;
    }
    const notice = document.getElementById('settingsRestartNotice');
    if (notice) {
      notice.classList.toggle('hidden', !(result && result.needReboot));
    }
    const ok = Boolean(result && result.ok);
    if (ok && result.tunDenied) {
      setSettingsField('enableTun', false);
    }
    notifyNodes(ok ? (result.message || 'Settings saved') : (result.message || 'Failed to save settings'));
    if (ok && result.tunDenied) {
      notifyNodes('TUN mode requires administrator privileges — it was not enabled');
    }
  };

  function saveSettings() {
    if (settingsState.saving) {
      return;
    }
    settingsState.saving = true;
    const saveBtn = document.getElementById('settingsSaveBtn');
    if (saveBtn) {
      saveBtn.disabled = true;
    }
    if (!postToHost({ action: 'save_settings', settings: collectSettings() })) {
      // Standalone preview fallback: no host bridge, just confirm locally.
      settingsState.saving = false;
      if (saveBtn) {
        saveBtn.disabled = false;
      }
      notifyNodes('Settings saved (preview — no host bridge)');
    }
  }
  document.getElementById('settingsSaveBtn').addEventListener('click', saveSettings);

  // Window-behaviour toggles (minimize-to-tray / hide-to-tray-on-close) apply
  // IMMEDIATELY, no Save click needed: the host flips the persisted flags on the
  // same config the close/minimize paths read, then pushes the settings back so
  // the shown switches can never drift from the real X / minimize behaviour.
  function bindWindowBehavior(name) {
    const el = document.querySelector('[data-settings="' + name + '"]');
    if (!el) {
      return;
    }
    el.addEventListener('change', () => {
      const hide = document.querySelector('[data-settings="hide2TrayWhenClose"]');
      const minimize = document.querySelector('[data-settings="minimize2Tray"]');
      postToHost({
        action: 'set_window_behavior',
        hide2TrayWhenClose: hide ? hide.checked === true : false,
        minimize2Tray: minimize ? minimize.checked === true : false
      });
    });
  }
  bindWindowBehavior('hide2TrayWhenClose');
  bindWindowBehavior('minimize2Tray');

  // Çift Bağlantı (Bölünmüş Tünelleme): küresel launcher-bypass düğümü (VLESS/Reality).
  // Host, window.setVlessBypassNode ile kayıtlı düğümü yayınlar (boşsa null). GPN
  // ayarlar sekmesindeki durum satırını doldurur; kaydetme ham vless:// bağlantısını
  // set_vless_bypass_from_uri ile host'a gönderir (boşsa ayar temizlenir — legacy WARP).
  let vlessBypassNode = null;
  window.setVlessBypassNode = (node) => {
    vlessBypassNode = node || null;
    const status = document.getElementById('vlessBypassStatus');
    if (!status) return;
    status.textContent = vlessBypassNode
      ? 'Active: ' + (vlessBypassNode.name || 'Launcher Bypass') + ' · ' + vlessBypassNode.serverAddress + ':' + vlessBypassNode.serverPort + ' — BsGLauncher.exe exits through this node'
      : 'No bypass configured — launcher uses legacy WARP routing';
  };
  const vlessBypassSaveBtn = document.getElementById('vlessBypassSaveBtn');
  if (vlessBypassSaveBtn) {
    vlessBypassSaveBtn.addEventListener('click', () => {
      const input = document.getElementById('vlessBypassUri');
      const uri = input ? input.value.trim() : '';
      if (!postToHost({ action: 'set_vless_bypass_from_uri', uri })) {
        notifyNodes('No host bridge — cannot save the bypass node (preview)');
      }
    });
  }

  // These globals are the only JavaScript entry points used by the WPF backend.
  window.setConnectionState = (next, backendMode, backendConnecting) => {
    // Keep the mode pill truthful when a real AoGPN mode change completes.
    if (backendMode === 'vpn' || backendMode === 'gpn') {
      mode = backendMode;
      applyMode();
    }
    setConnected(next === true, backendConnecting === true);
  };
  window.setTransport = (next) => {
    if (next === 'tun' || next === 'proxy') {
      transport = next;
      applyTransport();
      if (next === 'proxy') {
        showConnectionError('');
      }
    }
  };
  // GPN resilience decisions (server switch / UDP death / mode fallback /
  // Tier-2 recovery) published by the WPF host. Surfaces the last decision on
  // the status line so the user sees why the tunnel switched or fell back.
  window.setGpnResilience = (evt) => {
    if (!evt || !connectionStatusLine) return;
    let text = '';
    switch (evt.action) {
      case 'ServerSwitch':
        text = 'GPN failover · ' + (evt.serverName || evt.serverId || '') + ' → ' + (evt.targetServerName || evt.targetServerId || '');
        break;
      case 'UdpDeath':
        text = 'GPN · tünel UDP \u00f6l\u00fc (t\u00fcnnel \u00f6ld\u00fc)';
        break;
      case 'ModeFallback':
        text = 'GPN · V2rayTCP\'ye d\u00fc\u015f\u00fcld\u00fc (fallback)';
        break;
      case 'Recover':
        text = 'GPN · kurtarma → WireGuard (' + (evt.serverName || evt.serverId || '') + ')';
        break;
      case 'ModeDecision':
        text = 'GPN · ' + (evt.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WireGuard') + ' se\u00e7ildi';
        // The distinctive GPN Connect button badge mirrors the chosen mode so the
        // user sees Italy/Germany auto-selection landed even when the selected
        // profile wasn't WireGuard.
        if (typeof setGpnConnectState === 'function') {
          setGpnConnectState(evt.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WG');
        }
        break;
      default:
        return;
    }
    // Failover/kurtarma kararı geldiğinde telemetri kartlarını görsel olarak çak.
    // (ModeDecision = otomatik seçim başlangıcı — flaşsız, durum satırıyla yetinir.)
    if (evt.action !== 'ModeDecision') {
      flashGpnTelemetry(evt.action === 'Recover', text);
    }
    if (evt.reason && evt.action !== 'ServerSwitch' && evt.action !== 'Recover') {
      text += ' · ' + evt.reason;
    }
    connectionStatusLine.className = 'text-xs text-amber-300 leading-relaxed mt-4';
    connectionStatusLine.textContent = text;
  };
  // Kesintisiz düğüm geçişi sırasında eski düğümün boşalma (drain) durumunu durum
  // satırına yazar. Host (GpnDrainWatcher) mihomo /connections yanıtındaki zincir
  // (chains) alanlarından eski düğüme (wg-<eskiId>) bağlı kalan oturum sayısını
  // yayınlar: geçişte bağlantılar KESİLMEZ, eski düğümde doğal olarak biter — bu
  // satır kullanıcıya boşalma ilerlemesini gösterir.
  window.setGpnNodeSwitch = (state) => {
    if (!state || !connectionStatusLine) return;
    const from = state.fromServerName || state.fromServerId || '';
    const to = state.toServerName || state.toServerId || '';
    const known = typeof state.remainingConnections === 'number' && state.remainingConnections >= 0;
    let text;
    let cls = 'text-xs text-amber-300 leading-relaxed mt-4';
    if (state.isDraining) {
      const left = known ? String(state.remainingConnections) : '…';
      text = known
        ? 'GPN · ' + from + ' → ' + to + ' · eski düğüm boşalıyor (' + left + ' bağlantı)'
        : 'GPN · ' + from + ' → ' + to + ' · eski düğüm boşalıyor…';
    } else if (state.timedOut) {
      text = known && state.remainingConnections > 0
        ? 'GPN · ' + to + ' aktif · ' + state.remainingConnections + ' bağlantı eski düğümde doğal bitişi bekliyor'
        : 'GPN · ' + to + ' aktif · eski düğümdeki bağlantılar doğal bitişi bekliyor';
    } else {
      text = 'GPN · ' + to + ' aktif · eski düğüm boşaldı';
      cls = 'text-xs text-emerald-300 leading-relaxed mt-4';
    }
    connectionStatusLine.className = cls;
    connectionStatusLine.textContent = text;
  };
  // Failover anında Ping / Packet-Loss telemetri kartlarını çakıp karar bağlamını
  // lossSub'a yazar (bir sonraki telemetri örneği üzerine yazana dek görünür).
  // Kurtarma (recover) yeşil, failover turuncu yanar.
  function flashGpnTelemetry(recovered, label) {
    const cards = [document.getElementById('pingVal'), document.getElementById('lossVal')]
      .filter(Boolean)
      .map(el => el.closest('.glass'))
      .filter(Boolean);
    cards.forEach(card => {
      card.classList.remove('gpn-failover-flash', 'gpn-recover-flash');
      void card.offsetWidth; // animasyonu yeniden başlat
      card.classList.add('gpn-failover-flash');
      if (recovered) card.classList.add('gpn-recover-flash');
      setTimeout(() => card.classList.remove('gpn-failover-flash', 'gpn-recover-flash'), 1200);
    });
    if (label) {
      const lossSub = document.getElementById('lossSub');
      if (lossSub) lossSub.textContent = label;
    }
  }
  // "GPN Bağlan" butonu durum rozeti: host (GpnResilience mode kararlarıyla)
  // veya yerel tıklama sonrası durumu günceller.
  // Son GPN Bağlan durumu (label + pending) — standalone skin'lerin GPN Connect
  // kartı skinBridge.gpnConnectState üzerinden aynı değeri görür.
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
  window.setGpnConnectionInfo = (info) => {
    if (!info) return;
    // GPN seçim kararı yeni, yetkili bir kaynaktır — hız testi bekleme penceresini
    // kapat (sunucu değişimi/bağlantı sırasında bayat ölçüm gösterilmesin).
    _availabilityHoldUntil = 0;
    const server = info.server || info.Server || '';
    // "sonra" (tünel yolu) gerçek ping ölçümü hangi düğüm üzerinden yapıldıysa
    // delta satırında o düğüm adı görünsün (boost kartları + tablo "via" etiketi).
    _activeGpnServer = server || '';
    const delay = Number.isFinite(info.delayMs) && info.delayMs >= 0 ? String(Math.round(info.delayMs)) : null;
    const mode = info.mode || info.Mode || '';
    _activeGpnMode = mode;

    const pingCard = document.getElementById('pingVal');
    const pingSub = document.getElementById('pingSub');
    if (pingCard) {
      pingCard.textContent = delay != null ? delay : '--';
    }
    if (pingSub && server) {
      const modeTxt = mode ? (' · ' + mode) : '';
      pingSub.textContent = server + ' · ' + (delay != null ? delay + ' ms' : 'ölçüm bekleniyor') + modeTxt;
    }

    const sessionNode = document.getElementById('sessionNode');
    if (sessionNode && server) {
      sessionNode.textContent = server + (mode ? ' · ' + mode : '');
      sessionNode.title = sessionNode.textContent;
    }
    // Düğüm adı değişti → "via" etiketini boost kartlarında/tabloda anında yansıt.
    if (monitorSnapshot && (monitorSnapshot.apps || []).length) {
      renderDashboardBoostCards();
      if (currentView === 'boost') renderSplitApps();
    }
  };
  // Bağlantı sonrası sunucu kullanılabilirlik ölçümü (hız testi) sonucu: host
  // StatusBarViewModel.TestServerAvailability → gecikme + IP + sunucu adı. Ping
  // kartına gecikmeyi, alt satıra sunucu · gecikme · (ülke) IP'yi yazar — ölçüm
  // yalnızca WPF durum çubuğunda kalmak yerine ana panelde de görünür.
  window.setAvailabilityInfo = (info) => {
    if (!info) return;
    const server = info.server || info.Server || '';
    const delay = Number.isFinite(info.delayMs) && info.delayMs >= 0 ? String(Math.round(info.delayMs)) : null;
    const ip = info.ip || info.Ip || '';
    const country = info.country || info.Country || '';
    const countryName = country ? countryDisplayName(country) : '';

    // Gerçek gecikme ölçüldüyse ping kartını kısa bir pencere boyunca canlı
    // telemetri örneklerinden koru (kullanıcı ölçümü okuyabilsin).
    if (delay != null) {
      _availabilityHoldUntil = Date.now() + AVAILABILITY_HOLD_MS;
    }
    const pingCard = document.getElementById('pingVal');
    if (pingCard) pingCard.textContent = delay != null ? delay : '--';

    const pingSub = document.getElementById('pingSub');
    if (pingSub) {
      const parts = [];
      if (server) parts.push(server);
      if (delay != null) parts.push(delay + ' ms');
      // Ülke kodu her zaman görünür; ad (yerelleştirilmiş ülke adı) yalnızca
      // sunucu adı zaten önde değilse eklenir: "(IT) ip" veya "(İtalya · IT) ip".
      let ipPart = '';
      if (ip) {
        const cLabel = (!server && countryName) ? countryName + ' · ' + country : country;
        ipPart = (country ? '(' + cLabel + ') ' : '') + ip;
      }
      if (ipPart) parts.push(ipPart);
      pingSub.textContent = parts.length ? parts.join(' · ') : t('telemetry.measuring');
    }

    // IP doğrulama kartına hız testinin ölçtüğü ülkeyi/sunucuyu + IP'yi yansıt
    // (ör. "İtalya · 92.4.220.236") — bağlantı sonrası anlık, canlı ölçümle aynı
    // kanıttan beslenir. Sunucu adı varsa o (node adı, örn. "İtalya"), yoksa ülke
    // kodu kullanılır.
    if (ip) {
      if (ipDisplay) ipDisplay.textContent = ip;
      if (ipCountry) {
        // Kart: ad (kod) · IP — ad sunucu adı, yoksa yerelleştirilmiş ülke adı;
        // kod her zaman görünür: "İtalya (IT) · 92.4.220.236".
        const label = server || countryName || country;
        const codePart = country && label !== country ? ' (' + country + ')' : '';
        ipCountry.textContent = (label + codePart) + ' · ' + ip;
      }
    }
  };
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
    skinNotify(); // refresh standalone skins (GPN Servers view)
  };
  window.setGpnServerProbes = (results) => {
    gpnServerProbes.clear();
    (Array.isArray(results) ? results : []).forEach(r => gpnServerProbes.set(r.serverId, r));
    renderGpnServers();
    renderGpnCluster();
    skinNotify(); // refresh standalone skins (GPN Servers view)
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
  window.setAdminState = (admin) => {
    isAdmin = admin === true;
    applyTransport();
  };
  window.setProtocolPreference = (next) => {
    applyProtocolPreference(next);
  };
  window.setSystemProxyState = (desired, effective, connectionOwned) => {
    applySystemProxyState(desired, effective, connectionOwned);
  };
  window.setSystemProxyResult = (result) => {
    const localizeProxyMessage = (message) => {
      if (typeof message !== 'string' || !message.startsWith('i18n:')) return message;
      const [key, ...pairs] = message.slice(5).split('|');
      const params = Object.fromEntries(pairs.map(pair => {
        const index = pair.indexOf('=');
        return index > 0 ? [pair.slice(0, index), pair.slice(index + 1)] : [pair, ''];
      }));
      return t(key, params);
    };
    if (!result || result.ok !== true) {
      systemProxyAppliedAddress = '';
      applySystemProxyState(systemProxyMode, 0, false);
      notifyNodes(localizeProxyMessage(result && result.message ? result.message : 'proxyOnly.updateFailed'));
      return;
    }
    const message = typeof result.message === 'string' ? result.message : '';
    const addressMatch = message.match(/127\.0\.0\.1(?::\d+)?/);
    systemProxyAppliedAddress = addressMatch ? addressMatch[0] : (Number(result.mode) === 0 ? '' : systemProxyAppliedAddress);
    if (Number.isInteger(result.mode)) {
      applySystemProxyState(result.mode, result.mode, systemProxyConnectionOwned);
    }
    if (result.message) {
      notifyNodes(localizeProxyMessage(result.message));
    }
  };
  // Son bağlantı hatası (skinBridge.connectionError) — standalone skin'lerin
  // hata kartı ana dashboard ile aynı mesajı görür.
  let _connectionError = null;
  window.setConnectionError = (message) => {
    const info = typeof message === 'string' ? message : (message || '');
    _connectionError = (typeof info === 'object' && info) ? { message: info.message || info.Message || '', details: info.details || info.Details || '', elevation: info.elevation === true } : info;
    showConnectionError(info);
  };

  /**
   * Receives real public IP data from the C# host. When disconnected the ISP
   * IP is shown; when connected the tunnel IP is compared against the ISP IP
   * to verify traffic is actually routing. A mismatch means the tunnel works;
   * a match means traffic is leaking past the tunnel.
   */
  window.setRealIpState = (data) => {
    if (!data) return;
    applyRealIpState(data);
  };
  function applyRealIpState(data) {
    _lastIpState = data;
    // Ölçüm tazeliği: host her ölçümde measuredAt (ISO) gönderir; panel ölçümün
    // yaşını gösterir ve bayat bir "sızıntı" uyarısını amber "yeniden ölçülüyor"a
    // düşürür — eski ölçüm asla güncel gerçek gibi sunulmaz.
    _lastIpMeasuredAt = (typeof data.measuredAt === 'string' && data.measuredAt.length > 0)
      ? data.measuredAt
      : null;
    const isTunneled = data.connected === true && data.tunnelVerified === true;
    const isConnected = data.connected === true;
    const hasTunnelIp = typeof data.tunnelIp === 'string' && data.tunnelIp.length > 0;
    const ispCached = data.ispCached === true;

    // Update the "Your IP" card.
    if (isConnected && isTunneled) {
      // Tunnel verified: show the tunnel exit IP.
      if (ipDisplay) ipDisplay.textContent = data.tunnelIp;
      if (ipCountry) ipCountry.textContent = (data.tunnelCountry || '') + ' · ' + t('ip.tunnel');
    } else if (isConnected && hasTunnelIp && ispCached && !isTunneled) {
      // Connected, baseline cached and tunnel IP matches ISP — actual leak.
      if (ipDisplay) ipDisplay.textContent = data.tunnelIp;
      if (ipCountry) ipCountry.textContent = (data.tunnelCountry || '') + ' (' + t('ip.leaking') + ')';
    } else if (isConnected && hasTunnelIp) {
      // Connected, tunnel IP known but no baseline yet — show the tunnel IP
      // without claiming a leak until the ISP IP can be compared.
      if (ipDisplay) ipDisplay.textContent = data.tunnelIp;
      if (ipCountry) ipCountry.textContent = (data.tunnelCountry || '') + ' · ' + t('ip.tunnel');
    } else if (isConnected && !hasTunnelIp) {
      // Connected but the proxy probe hasn't returned yet or failed.
      // Show the direct (ISP) IP with a probing indicator.
      const direct = (typeof data.directIp === 'string' && data.directIp.length > 0) ? data.directIp : '--';
      if (ipDisplay) ipDisplay.textContent = direct;
      if (ipCountry) ipCountry.textContent = t('ip.probing');
    } else {
      // Disconnected: show ISP IP.
      const ispIp = (typeof data.ispIp === 'string' && data.ispIp.length > 0) ? data.ispIp : (typeof data.directIp === 'string' ? data.directIp : '--');
      if (ipDisplay) ipDisplay.textContent = ispIp;
      if (ipCountry) ipCountry.textContent = (data.ispCountry || data.directCountry || '') + ' · ' + t('ip.isp');
    }

    // Update verification card.
    if (ipVerifyCard) {
      ipVerifyCard.classList.remove('hidden');
      if (!isConnected) {
        if (ipVerifyText) ipVerifyText.innerHTML = t('status.idle');
        if (ipVerifyText) ipVerifyText.className = 'text-sm font-semibold truncate mt-1 text-slate-400';
        if (ipVerifyDetail) ipVerifyDetail.textContent = t('status.notConnected');
      } else if (isTunneled) {
        if (ipVerifyText) ipVerifyText.innerHTML = t('status.tunneled');
        if (ipVerifyText) ipVerifyText.className = 'text-sm font-semibold truncate mt-1 text-emerald-300';
        if (ipVerifyDetail) ipVerifyDetail.textContent = data.transport === 'tun' ? t('ip.detail.tunActive') : t('ip.detail.socksVerified');
      } else if (isConnected && !hasTunnelIp && !ispCached) {
        // ISP baseline not yet cached — first probe still pending.
        if (ipVerifyText) ipVerifyText.innerHTML = '<span class="theme-spinner theme-spinner-sm"></span> ' + t('status.probing');
        if (ipVerifyText) ipVerifyText.className = 'text-sm font-semibold truncate mt-1 text-amber-300 flex items-center gap-2';
        if (ipVerifyDetail) ipVerifyDetail.textContent = t('ip.detail.fetchingBaseline');
      } else if (isConnected && !hasTunnelIp && ispCached) {
        // ISP baseline cached but tunnel probe failed (SOCKS5 timeout or core not ready).
        if (ipVerifyText) ipVerifyText.innerHTML = t('status.retrying');
        if (ipVerifyText) ipVerifyText.className = 'text-sm font-semibold truncate mt-1 text-amber-300';
        if (ipVerifyDetail) ipVerifyDetail.textContent = t('ip.detail.willRetry');
      } else if (isConnected && hasTunnelIp && !ispCached) {
        // Tunnel IP present but no ISP baseline to compare against yet.
        if (ipVerifyText) ipVerifyText.innerHTML = '<span class="theme-spinner theme-spinner-sm"></span> ' + t('status.probing');
        if (ipVerifyText) ipVerifyText.className = 'text-sm font-semibold truncate mt-1 text-amber-300 flex items-center gap-2';
        if (ipVerifyDetail) ipVerifyDetail.textContent = t('ip.detail.fetchingBaseline');
      } else {
        // Connected + tunnel IP exists but matches ISP — actual leak.
        if (ipVerifyText) ipVerifyText.innerHTML = t('status.leaking');
        if (ipVerifyText) ipVerifyText.className = 'text-sm font-semibold truncate mt-1 text-red-300';
        if (ipVerifyDetail) ipVerifyDetail.textContent = t('ip.detail.possibleLeak');
      }
    }

    // Show IP leak warning ONLY when there's a confirmed leak (tunnel IP matches ISP).
    if (ipLeakBanner) {
      const leaking = isConnected && hasTunnelIp && !isTunneled && ispCached;
      ipLeakBanner.classList.toggle('hidden', !leaking);
      if (leaking && ipLeakText) {
        ipLeakText.textContent = t('ip.leakBanner', { ip: (data.tunnelIp || 'unknown') });
      }
    }

    var gs=document.getElementById('globalIpStatus');
    if(gs){
      if(!isConnected){
        gs.innerHTML='<p class="text-sm font-semibold text-slate-400">' + t('global.notConnected') + '</p><p class="text-[11px] text-[#64748B] mt-1">' + t('global.disconnectedDesc') + '</p>';
      }else if(isTunneled){
        gs.innerHTML='<p class="text-sm font-semibold text-emerald-200">' + t('global.tunnelVerified') + '</p><p class="text-[11px] text-[#64748B] mt-1">' + t('global.tunnelVerifiedDesc', { ip: (data.tunnelIp||'—') }) + '</p>';
      }else if(isConnected&&hasTunnelIp&&ispCached){
        gs.innerHTML='<p class="text-sm font-semibold text-red-300">' + t('global.leaking') + '</p><p class="text-[11px] text-[#64748B] mt-1">' + t('global.leakingDesc', { ip: (data.tunnelIp||'—') }) + '</p>';
      }else{
        gs.innerHTML='<p class="text-sm font-semibold text-amber-300">' + t('global.probing') + '</p><p class="text-[11px] text-[#64748B] mt-1">' + t('global.probingDesc') + '</p>';
      }
    }
    // Show verification success banner.
    if (ipOkBanner) {
      ipOkBanner.classList.toggle('hidden', !isTunneled);
      if (isTunneled && ipOkTunnelIp) {
        ipOkTunnelIp.textContent = data.tunnelIp || '';
      }
      if (isTunneled && ipOkIspIp) {
        ipOkIspIp.textContent = data.ispIp || data.directIp || '';
      }
    }
    updateGlobalPanel();

    // Ölçüm tazeliği süslemesini uygula (saniyede bir tickSession yeniler).
    if (ipVerifyDetail) _ipVerifyBaseDetail = ipVerifyDetail.textContent;
    applyIpFreshness();
  }

  let _ipVerifyBaseDetail = '';

  /**
   * IP doğrulama panelini ölçümün YAŞIYLA günceller: bağlıyken ve ölçüm varsa
   * "ölçüldü {s}sn önce" ekini gösterir. Ayrıca bağlıyken "sızıntı" durumunda
   * ölçüm 45 sn'den bayatsa kırmızı "Leaking" yerine amber "yeniden ölçülüyor"
   * gösterir — bayat ölçüm (ör. bağlanma anında tünel henüz hazır değilken alınan
   * ISP IP'si) yanlış bir sızıntı uyarısı olarak asılı kalamaz. Yeni bir ölçüm
   * gelince (setRealIpState) state tazelenir ve kırmızı uyarı döner.
   */
  function applyIpFreshness() {
    if (!ipVerifyDetail || !_lastIpState) return;
    const st = _lastIpState;
    const isConnectedNow = st.connected === true;
    const measured = _lastIpMeasuredAt ? new Date(_lastIpMeasuredAt).getTime() : null;
    const ageS = measured ? Math.max(0, Math.round((Date.now() - measured) / 1000)) : null;

    // Bayat sızıntı ölçümü → amber "yeniden ölçülüyor" (kesin uyarı değil).
    const isLeaking = isConnectedNow && (st.tunnelVerified !== true)
      && typeof st.tunnelIp === 'string' && st.tunnelIp.length > 0
      && st.ispCached === true;
    if (isLeaking && ageS !== null && ageS > 45) {
      if (ipVerifyText) {
        ipVerifyText.innerHTML = '<span class="theme-spinner theme-spinner-sm"></span> ' + t('status.retrying');
        ipVerifyText.className = 'text-sm font-semibold truncate mt-1 text-amber-300 flex items-center gap-2';
      }
      ipVerifyDetail.textContent = t('ip.detail.staleMeasure');
      return;
    }

    // Taze ölçüm → ayrıntı satırına "ölçüldü {s}sn önce" ekini koy.
    const suffix = (ageS !== null && isConnectedNow) ? ' · ' + t('ip.measuredAgo', { s: ageS }) : '';
    ipVerifyDetail.textContent = _ipVerifyBaseDetail + suffix;
  }

  // Bağlıyken panel kendini yeniden ölçer: C# tarafının ~30 sn'lik döngüsüne
  // rağmen bayat ölçümden kaynaklı yanlış "sızıntı" uyarısı en geç 25 sn'de
  // düzeltilir (host `check_ip` ile ölçümü yeniler).
  let _ipRecheckTimer = null;
  function startIpRecheckLoop() {
    stopIpRecheckLoop();
    _ipRecheckTimer = setInterval(() => {
      if (connected) postToHost({ action: 'check_ip' });
    }, 25000);
  }
  function stopIpRecheckLoop() {
    if (_ipRecheckTimer) { clearInterval(_ipRecheckTimer); _ipRecheckTimer = null; }
  }

  window.updateTelemetry = (ping, loss, download, upload) =>
    updateTelemetry(ping, loss, download, upload);
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
  // Node pool links published by the host (GitHub raw .txt / subscription URLs).
  window.updateNodePool = (links) => {
    nodePoolLinks = Array.isArray(links) ? links.filter(l => typeof l === 'string') : [];
    renderNodePool();
  };
  function renderNodePool() {
    const list = $('nodePoolList');
    if (!list) return;
    if (nodePoolLinks.length === 0) {
      list.innerHTML = '<li class="text-[10px] text-[#5B6472] py-1" data-i18n="nodes.poolEmpty">No links in the pool yet.</li>';
      applyTexts();
      return;
    }
    list.innerHTML = nodePoolLinks.map(link =>
      `<li class="flex items-center gap-2 min-w-0 group">
        <span class="w-1.5 h-1.5 rounded-full bg-cyan-400/60 shrink-0"></span>
        <span class="text-[11px] text-slate-300 truncate min-w-0">${escHtml(link)}</span>
        <button type="button" data-pool-remove="${escHtml(link)}" class="ml-auto shrink-0 text-[10px] font-semibold px-2 py-0.5 rounded-md bg-white/5 text-[#8A94A6] border border-white/10 hover:text-red-300 hover:bg-red-500/10 hover:border-red-400/25 transition-colors" data-i18n="nodes.poolRemove">Remove</button>
      </li>`).join('');
    list.querySelectorAll('[data-pool-remove]').forEach(btn => {
      btn.addEventListener('click', () => {
        postToHost({ action: 'remove_node_pool_link', url: btn.dataset.poolRemove });
      });
    });
    applyTexts();
  }
  // Per-node real ping result from the host: a numeric delay (ms), "-1" for a
  // failed test, or a transient status string while the run is in progress.
  window.updateNodeTest = (indexId, delayStr, runId) => {
    if (runId !== undefined && Number(runId) !== activeNodeTestRunId) return;
    const status = typeof delayStr === 'string' ? delayStr : '';
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

  // ---------- Reduce-effects (motion budget) ----------
  // Heavy theme animations (aurora, matrix rain, cursor trails, CONNECT ring)
  // can be switched off from Settings (body.reduce-effects + canvas loop gates)
  // and are also disabled automatically when the OS asks for reduced motion.
  let _osReducedMotion = false;
  // Visual-effects tier pushed by the host: 'full' | 'balanced' | 'reduced'.
  // 'balanced' keeps the reactive effects (cursor trails, confetti, CONNECT
  // ring, spinners) but freezes the perpetual full-screen loops — aurora
  // drift, theme signature scans, matrix rain — the ones that keep the
  // compositor producing frames at vsync even while the dashboard is idle.
  let effectsTier = 'full';
  function effectsReduced() { return reduceEffects || _osReducedMotion; }
  function effectsBalanced() { return effectsTier === 'balanced' && !effectsReduced(); }
  function applyReduceEffectsClass() {
    try {
      _osReducedMotion = Boolean(
        window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
    } catch { _osReducedMotion = false; }
    body.classList.toggle('reduce-effects', effectsReduced());
    body.classList.toggle('effects-balanced', effectsTier === 'balanced' && !effectsReduced());
  }
  function setEffectsTier(next, persist) {
    effectsTier = next === 'balanced' || next === 'reduced' ? next : 'full';
    reduceEffects = effectsTier === 'reduced';
    if (persist !== false) {
      try { localStorage.setItem('aogpn.effectsTier', effectsTier); } catch {}
    }
    applyReduceEffectsClass();
    // Canvas effects the tier no longer allows stop immediately instead of
    // fading out (the matrix rain keeps repainting even at half rate, so it
    // must be cleared the moment balanced/reduced is picked).
    if (effectsTier !== 'full') {
      if (_matrixCtx && _matrixCanvas) _matrixCtx.clearRect(0, 0, _matrixCanvas.width, _matrixCanvas.height);
      _matrixPaintHalf = false;
      _matrixFramePending = false;
    }
    if (effectsReduced()) {
      if (_ctx && _cursorCanvas) _ctx.clearRect(0, 0, _cursorCanvas.width, _cursorCanvas.height);
      _trails = [];
      _confettiParticles = [];
      _cursorActive = false;
    }
    syncEffectsTierUI();
  }
  function syncEffectsTierUI() {
    document.querySelectorAll('[data-effects-tier]').forEach(btn => {
      const active = btn.dataset.effectsTier === effectsTier;
      btn.classList.toggle('active', active);
      btn.setAttribute('aria-pressed', String(active));
    });
  }
  window.setEffectsTier = setEffectsTier;
  // Tiny helper used by the Settings segmented control buttons.
  window.setEffectsTierFromSettings = (tier) => {
    setEffectsTier(tier, false);
    postToHost({ action: 'set_effects_tier', tier });
  };
  function setReduceEffects(next, persist) {
    // Legacy two-state bridge (older dashboard builds / host fallback path):
    // on kills everything, off restores the previous (or full) tier.
    setEffectsTier(next === true ? 'reduced' : (effectsTier === 'reduced' ? 'full' : effectsTier), persist);
  }
  window.setReduceEffects = setReduceEffects;
  function initReduceEffects() {
    // Standalone browser previews restore the choice locally; in the packaged
    // app the host pushes the authoritative config via window.setEffectsTier.
    try {
      const saved = localStorage.getItem('aogpn.effectsTier');
      if (saved === 'balanced' || saved === 'reduced') effectsTier = saved;
      // Legacy two-state pref from before the tiers existed.
      else if (localStorage.getItem('aogpn.reduceEffects') === '1') effectsTier = 'reduced';
    } catch {}
    applyReduceEffectsClass();
    syncEffectsTierUI();
    if (typeof window.matchMedia === 'function') {
      const mq = window.matchMedia('(prefers-reduced-motion: reduce)');
      const onOsChange = () => applyReduceEffectsClass();
      if (typeof mq.addEventListener === 'function') mq.addEventListener('change', onOsChange);
      else if (typeof mq.addListener === 'function') mq.addListener(onOsChange);
    }
  }

  if (!window.__aogpnPerfDisabled && hasWebViewBridge()) startPerformanceProbe();
  initReduceEffects();

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
    var cursorState = _cursorActive ? 'active' : 'idle';
    var matrixState = _matrixFramePending ? 'running' : 'idle';
    var confettiN = _confettiParticles.length;
    var trailN = _trails.length;
    var p = _lastProbeSample;
    var probeLine = p
      ? p.frames + ' frames / ' + p.avgFrameMs + 'ms avg / ' + p.worstFrameMs + 'ms peak / ' + p.animations + ' anims'
      : 'waiting for burst…';
    var line = '<span style="color:#22D3EE;font-weight:600">Performance HUD</span> — <span style="color:#5B6472">Ctrl+Shift+H</span>';
    line += '<br><b style="color:#8A94A6">Theme:</b> ' + (_currentThemeId || '?')
      + ' &nbsp; <b style="color:#8A94A6">FX:</b> ' + (effectsTier || 'full')
      + ' &nbsp; <b style="color:#8A94A6">OS-motion:</b> ' + (_osReducedMotion ? '<span style="color:#F59E0B">yes</span>' : 'no') + ')' ;
    line += '<br><b style="color:#8A94A6">CSS anims:</b> ' + anims
      + ' &nbsp; <b style="color:#8A94A6">Ring:</b> ' + ringState;
    line += '<br><b style="color:#8A94A6">Cursor:</b> ' + cursorState + (trailN ? ' (' + trailN + ' trails)' : '')
      + ' &nbsp; <b style="color:#8A94A6">Confetti:</b> ' + confettiN
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
      if (tunLocked) {
        return;
      }
      setGpnConnectState('…', true);
      postToHost({ action: 'gpn_connect' });
    });
  }

  $('connectBtn').addEventListener('click', () => {
    // TUN without elevation would be rejected by the host; block the press up front.
    if (tunLocked) {
      return;
    }
    // The real app waits for C# to acknowledge the applied routing transition. A
    // local fallback keeps the same file interactive when opened outside WebView2.
    if (!postToHost({ action: 'toggle_connection', mode, transport, protocol: protocolPreference })) {
      setConnected(!connected, false);
    }
  });
  // Mobile nav quick connect/disconnect: same toggle, same TUN lock.
  const mobileConnectBtn = $('mobileConnectBtn');
  if (mobileConnectBtn) {
    mobileConnectBtn.addEventListener('click', () => {
      if (tunLocked) {
        return;
      }
    if (!postToHost({ action: 'toggle_connection', mode, transport, protocol: protocolPreference })) {
      setConnected(!connected, false);
    }
    });
  }
  systemProxyToggleBtn.addEventListener('click', () => {
    if (!postToHost({ action: 'toggle_system_proxy' })) {
      const next = isProxyModeEnabled(systemProxyMode) ? 0 : 1;
      applySystemProxyState(next, next, false);
    }
  });
  systemProxyModeSelect.addEventListener('change', () => {
    const next = Math.max(0, Math.min(3, Number(systemProxyModeSelect.value) || 0));
    if (!postToHost({ action: 'set_system_proxy_mode', mode: next })) {
      applySystemProxyState(next, next, false);
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
    skinNotify(); // refresh standalone skins (system-proxy test result)
  };
  function bindProtocolSelect(el) {
    if (!el) {
      return;
    }
    el.addEventListener('change', () => {
      const next = el.value;
      applyProtocolPreference(next);
      if (!postToHost({ action: 'set_protocol_preference', protocol: next })) {
        applyProtocolPreference(next);
      }
      syncQuickControls();
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
        syncQuickControls();
      }
      syncQuickControls();
    });
  }
  bindAutoReconnectToggle(quickAutoReconnect);
  bindAutoReconnectToggle(document.getElementById('mobileAutoReconnect'));
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

  const topNodeSelectEl = $('topNodeSelect');
  if (topNodeSelectEl) {
    topNodeSelectEl.addEventListener('change', () => {
      if (topNodeSelectEl.value) {
        requestNodeSwitch(topNodeSelectEl.value);
      }
    });
  }

  document.querySelectorAll('.mode-pill').forEach(p => p.addEventListener('click', () => {
    mode = p.dataset.mode === 'gpn' ? 'gpn' : 'vpn';
    applyMode();
    splitMode = mode === 'gpn' ? 'manual' : 'vpn';
    applySplitMode();
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
    applyTransport();
    // Never send TUN to the host without elevation: it would be rejected anyway,
    // and keeping the pill selected shows the locked CONNECT + explanation.
    if (next === 'tun' && !isAdmin) {
      return;
    }
    postToHost({ action: 'set_transport', transport: next });
  }));
  document.querySelectorAll('.protocol-pill').forEach(p => p.addEventListener('click', () => {
    applyProtocolPreference(p.dataset.protocol);
    postToHost({ action: 'set_protocol_preference', protocol: protocolPreference });
  }));
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
  document.querySelectorAll('[data-view]').forEach(item => item.addEventListener('click', (e) => {
    e.preventDefault();
    showView(item.dataset.view);
  }));
  document.querySelectorAll('.split-mode-pill').forEach(button => button.addEventListener('click', () => {
    splitMode = button.dataset.splitMode;
    applySplitMode();
    postToHost({ action: 'set_split_mode', mode: splitMode });
  }));
  document.querySelectorAll('.dir-pill').forEach(button => button.addEventListener('click', () => {
    const next = button.dataset.dir === 'blacklist';
    invertManual = next;
    applyDirection();
    if (!postToHost({ action: 'set_split_direction', invert: String(next) })) {
      applyDirection();
    }
  }));
  $('monitorFilter')?.addEventListener('input', renderMonitorConnections);
  $('monitorHideListeners')?.addEventListener('change', renderMonitorConnections);
  $('monitorRefreshBtn')?.addEventListener('click', () => postToHost({ action: 'refresh_monitor' }));
  $('boostRefreshBtn')?.addEventListener('click', () => postToHost({ action: 'refresh_monitor' }));
  $('boostRunningAppsBtn')?.addEventListener('click', () => {
    const picker = $('boostProcessPicker');
    setProcessPickerOpen(Boolean(picker?.classList.contains('hidden')));
  });
  $('boostProcessPickerClose')?.addEventListener('click', () => setProcessPickerOpen(false));
  $('boostProcessFilter')?.addEventListener('input', renderProcessCatalog);
  $('boostAddAppBtn')?.addEventListener('click', () => postToHost({ action: 'add_app' }));

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
  $('boostAppFilter')?.addEventListener('input', () => renderSplitApps());

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
    if (currentView !== 'boost') return;
    e.preventDefault();
    var items = e.dataTransfer && e.dataTransfer.types;
    if (items && items.indexOf('Files') >= 0) _boostDropActive(true);
  });
  document.addEventListener('dragover', function(e) {
    if (currentView !== 'boost' || _dropCounter === 0) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  });
  document.addEventListener('dragleave', function(e) {
    if (currentView !== 'boost') return;
    _boostDropActive(false);
  });
  document.addEventListener('drop', function(e) {
    if (currentView !== 'boost') return;
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

  // ---------- Custom tooltips ----------
  // Replaces native title bubbles with a soft, theme-aware floating card.
  // Reads data-tip-key (translated via t()), data-tip (raw) or the title
  // attribute (lazy-adopted), so every existing tooltip gets the new look.
  let _tipEl = null;
  let _tipTarget = null;
  let _tipTimer = null;
  let _tipHideTimer = null;

  function ensureTipEl() {
    if (!_tipEl) {
      _tipEl = document.createElement('div');
      _tipEl.id = 'customTooltip';
      _tipEl.className = 'custom-tooltip';
      document.body.appendChild(_tipEl);
    }
    return _tipEl;
  }

  function adoptTooltipEl(el) {
    const ttl = el.getAttribute && el.getAttribute('title');
    if (ttl && ttl.length) {
      el.dataset.rawTitle = ttl;
      el.removeAttribute('title');
    }
  }

  function resolveTipText(el) {
    // A live title (set by JS, e.g. the connect button) already has its
    // placeholders substituted — prefer it over a static key template.
    if (el.dataset.rawTitle) return el.dataset.rawTitle;
    if (el.dataset.tipKey) {
      const v = t(el.dataset.tipKey);
      if (v && v !== el.dataset.tipKey) return v;
    }
    if (el.dataset.tip) return el.dataset.tip;
    const live = el.getAttribute && el.getAttribute('title');
    if (live && live.length) {
      el.dataset.rawTitle = live;
      el.removeAttribute('title');
      return live;
    }
    return '';
  }

  function positionTip(el) {
    const tip = ensureTipEl();
    const r = el.getBoundingClientRect();
    const tr = tip.getBoundingClientRect();
    const gap = 9;
    let top = r.top - tr.height - gap;
    let below = false;
    if (top < 8) {
      top = r.bottom + gap;
      below = true;
    }
    const left = Math.max(8, Math.min(r.left + r.width / 2 - tr.width / 2, window.innerWidth - tr.width - 8));
    tip.classList.toggle('below', below);
    tip.style.left = left + 'px';
    tip.style.top = top + 'px';
  }

  function showTip(el) {
    const text = resolveTipText(el);
    if (!text) return;
    const tip = ensureTipEl();
    tip.textContent = text;
    positionTip(el);
    requestAnimationFrame(() => tip.classList.add('visible'));
  }

  function hideTip() {
    if (_tipEl) _tipEl.classList.remove('visible');
  }

  function tipFromEvent(e) {
    // Hit-test so disabled buttons (which don't dispatch hover events) still
    // show their tooltip; pointer-events:none on the tooltip keeps it inert.
    const hit = document.elementFromPoint(e.clientX, e.clientY);
    return hit && hit.closest ? hit.closest('[title],[data-tip],[data-tip-key]') : null;
  }

  document.addEventListener('mouseover', (e) => {
    const el = tipFromEvent(e);
    if (!el) return;
    adoptTooltipEl(el);
    if (el === _tipTarget) return;
    clearTimeout(_tipTimer);
    clearTimeout(_tipHideTimer);
    _tipTarget = el;
    _tipTimer = setTimeout(() => showTip(el), 180);
  }, true);

  document.addEventListener('mouseout', (e) => {
    const el = e.target && e.target.closest ? e.target.closest('[title],[data-tip],[data-tip-key]') : null;
    if (!el || el !== _tipTarget) return;
    if (e.relatedTarget && e.relatedTarget.nodeType === 1 && el.contains(e.relatedTarget)) return;
    clearTimeout(_tipTimer);
    _tipTarget = null;
    _tipHideTimer = setTimeout(hideTip, 50);
  }, true);

  // Keyboard users get the tooltip on focus as well.
  document.addEventListener('focusin', (e) => {
    const el = e.target && e.target.closest ? e.target.closest('[title],[data-tip],[data-tip-key]') : null;
    if (!el) return;
    clearTimeout(_tipHideTimer);
    _tipTarget = el;
    _tipTimer = setTimeout(() => showTip(el), 120);
  }, true);
  document.addEventListener('focusout', () => {
    clearTimeout(_tipTimer);
    _tipTarget = null;
    _tipHideTimer = setTimeout(hideTip, 50);
  }, true);

  // Dynamically-set titles (connect button, status label, node rows) are adopted
  // as soon as they change so the native bubble never appears.
  const _tipObserver = new MutationObserver((muts) => {
    for (const m of muts) {
      if (m.type === 'attributes' && m.attributeName === 'title' && m.target.nodeType === 1) {
        adoptTooltipEl(m.target);
      } else if (m.type === 'childList') {
        m.addedNodes.forEach(n => {
          if (n.nodeType === 1 && n.matches && n.matches('[title],[data-tip],[data-tip-key]')) adoptTooltipEl(n);
        });
      }
    }
  });
  _tipObserver.observe(document.body, { attributes: true, attributeFilter: ['title'], subtree: true, childList: true });

  window.addEventListener('scroll', () => hideTip(), true);
  window.addEventListener('resize', () => hideTip());
  document.addEventListener('click', () => hideTip(), true);

  // Adopt titles that already exist in static HTML.
  document.querySelectorAll('[title]').forEach(adoptTooltipEl);

  // Pre-fill the settings selects/datalists so the form is usable standalone too.
  fillSettingsOptions(FALLBACK_OPTIONS);

  // Populate the i18n dictionary (default English) before the host pushes the
  // saved language, so dynamic strings never show raw keys.
  applyLanguage('en');
  loadThemesFromDisk();
  loadReleaseNotesFromDisk();
  renderTopThemePopover();
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

  function skinNotify() {
    _skinSubscribers.forEach(cb => { try { cb(); } catch (e) { /* ignore */ } });
  }

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
    skinNotify();
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
    get useRealNodes() { return useRealNodes; },
    get nodes() { return useRealNodes ? [...realNodes.values()] : NODES; },
    get realNodes() { return realNodes; },
    get NODES() { return NODES; },
    get activeRealNodeId() { return activeRealNodeId; },
    get selectedNode() { return selectedNode; },
    get monitorSnapshot() { return monitorSnapshot; },
    get processCatalog() { return processCatalog || []; },
    get routeLabels() { return routeLabels; },
    get language() { return _currentLang; },
    get gpnServers() { return gpnServers; },
    get gpnServerProbes() { return Array.from(gpnServerProbes.values()); },
    get tunStack() {
      const s = document.getElementById('captureTunStack');
      return (s && s.value) || 'mixed';
    },
    get appInfo() { return _appInfo || null; },
    get effectsTier() { return effectsTier; },
    get gpnRecoveryWatch() { return gpnRecoveryWatch; },
    get gpnFailover() { return gpnFailover; },
    get proxyTestResult() { return _proxyTestResult || null; },
    // ---- GPN panel / Advanced & Diagnostics (live host state) ----
    get gpnConnectState() { return { label: _gpnConnectStateLabel || '—', pending: !!_gpnConnectPending }; },
    get gpnPidPool() { return _gpnPidPool || null; },
    get gpnCaptureSettings() { return _gpnCaptureSettings || null; },
    get gpnWintunSettings() { return _gpnWintunSettings || null; },
    get gpnCaptureStats() { return _gpnCaptureStats || null; },
    get gpnFailoverMatrix() { return _gpnFailoverMatrix || null; },
    get gpnSelectionPrediction() { return _gpnSelectionPrediction || null; },
    get gpnTelemetry() { return _gpnTelemetrySnap || null; },
    get gpnResilienceLog() { return _gpnResilienceLog || null; },
    get gpnDiagLines() { return _gpnDiagLines.slice(); },
    get connectionError() { return _connectionError || null; },
    get realIpState() { return _lastIpState || null; },
    get systemProxyState() { return { desired: systemProxyMode, effective: effectiveSystemProxyMode, owned: systemProxyConnectionOwned }; },
    get activeGpnServer() { return _activeGpnServer || ''; },
    get sessionTime() {
      const h = String(Math.floor(sessionSec / 3600)).padStart(2, '0');
      const m = String(Math.floor((sessionSec % 3600) / 60)).padStart(2, '0');
      const s = String(sessionSec % 60).padStart(2, '0');
      return `${h}:${m}:${s}`;
    },
    get exitIp() { return (ipDisplay && connected) ? ipDisplay.textContent : undefined; },
    get exitCountry() { return (ipCountry && connected) ? ipCountry.textContent : undefined; },
    get telemetry() { return _lastTelemetry || [null, null, null, null]; },
    get isAdmin() { return isAdmin; },
    set connected(v) { connected = !!v; },
    set connecting(v) { connecting = !!v; },
    set mode(v) { mode = v; },
    set transport(v) { transport = v; },
    set selectedNode(v) { selectedNode = v; },
    set splitMode(v) { splitMode = v; },
    set autoReconnect(v) { autoReconnect = !!v; },
    set systemProxyMode(v) { systemProxyMode = v; },
    set protocolPreference(v) { protocolPreference = v; },
    set useRealNodes(v) { useRealNodes = !!v; },
    postToHost,
    requestNodeSwitch,
    applyNode,
    setConnected: (next) => { setConnected(!!next, false); },
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
      let val = null;
      if (sid !== 'standard') {
        const scoped = 'skin.' + sid + '.' + key;
        if (_dict && Object.prototype.hasOwnProperty.call(_dict, scoped)) val = _dict[scoped];
        if (empty(val) && _fallbackDict && Object.prototype.hasOwnProperty.call(_fallbackDict, scoped)) val = _fallbackDict[scoped];
      }
      if (empty(val)) {
        if (_dict && Object.prototype.hasOwnProperty.call(_dict, key)) val = _dict[key];
        if (empty(val) && _fallbackDict && Object.prototype.hasOwnProperty.call(_fallbackDict, key)) val = _fallbackDict[key];
      }
      if (empty(val)) return key;
      if (params) {
        Object.keys(params).forEach(k => { val = String(val).split('{' + k + '}').join(String(params[k])); });
      }
      return String(val);
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
  // Keep legacy aliases (used by the old inline fragment and debug consoles).
  window.escHtml = escHtml;
  window.postToHost = postToHost;
  window.requestNodeSwitch = requestNodeSwitch;
  window.applyNode = applyNode;
  window.setNexusConnected = (next) => { setConnected(!!next, false); };
  // Host signals an in-flight connection attempt: the button locks (amber
  // CONNECTING state) until the attempt resolves to connected or idle.
  window.setNexusConnecting = (next) => { setConnected(false, !!next); };

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

  renderNodes();
  applyNode();
  renderTopNodeSelect();
  applyMode();
  applyTransport();
  applyProtocolPreference(protocolPreference);
  applySystemProxyState(systemProxyMode, effectiveSystemProxyMode, false);
  applySplitMode();
  renderMonitorConnections();
  renderSplitApps();
  renderDashboardBoostCards();
  resetTelemetry();
  applyDirection();

  // GPN panel "Gelişmiş & Teşhis" (Advanced & Diagnostics): the rarely-used
  // server tuning, failover, capture/Wintun internals, telemetry and diagnostics
  // cards live in a collapsible group so the main GPN flow stays clean. The host
  // keeps feeding the cards inside; state is remembered between launches.
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

  // Prime the node pool panel and its links as soon as the page is ready; the
  // host re-pushes after every add/remove/fetch.
  postToHost({ action: 'get_node_pool' });
  renderNodePool();
  // GPN panel "sunucu kümesi" kartını besle: sunucu listesini iste ve canlı
  // gecikme ölçümü döngüsünü başlat (panel görünürken 15 sn'de bir ölçer).
  postToHost({ action: 'gpn_servers_list' });
  startGpnClusterLoop();
})();
