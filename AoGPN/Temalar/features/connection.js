/* ==========================================================================
   features/connection.js — AoGPN dashboard feature module (bağlantı)
   --------------------------------------------------------------------------
   Bağlantı durumu UI'si (setConnected, CONNECT halkası, durum satırı), mod/
   transport/protokol/system-proxy uygulayıcıları, IP doğrulama paneli ve host
   köprüsü (setConnectionState ... setRealIpState / setConnectionError / ip
   recheck döngüsü). Bağlantı DURUMU app.js koordinatöründe kalır; bu modül
   aogpn.app kayıt defterine bağlı st. canlı köprüsüyle erişir (durum tek
   kaynakta, skinBridge/init/applySettings doğrudan okumaya devam eder).
   ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const countryDisplayName = aogpn.i18n.countryDisplayName;
  const $ = aogpn.util.$;
  const postToHost = aogpn.bridge.postToHost;
  const refreshAllCustomSelects = aogpn.selects.refreshAllCustomSelects;
  const { btnIcon, btnText, btnSub, statusDot, statusLabel,
          ipDisplay, ipCountry, ipVerifyText, ipVerifyDetail, ipVerifyCard,
          ipLeakBanner, ipLeakText, ipOkBanner, ipOkText, ipOkTunnelIp, ipOkIspIp,
          systemProxyToggleBtn, systemProxyDot, systemProxyLabel, systemProxyAddress,
          systemProxyModeSelect, quickProtocolSelect, quickAutoReconnect,
          protocolHint, routeHint, transportHint, connectionStatusLine } = aogpn.dom;
  const { resetTelemetry, flashGpnTelemetry } = aogpn.telemetry;
  const { playThemeSound, getCurrentThemeId } = aogpn.theme;
  const body = document.body;
  // app.js durum kayıt defterine canlı köprü — durum koordinatörde kalır.
  const st = {
    get connected() { return aogpn.app.getConnected(); },
    set connected(v) { aogpn.app.setConnectedRaw(v); },
    get connecting() { return aogpn.app.getConnecting(); },
    set connecting(v) { aogpn.app.setConnectingRaw(v); },
    get mode() { return aogpn.app.getMode(); },
    set mode(v) { aogpn.app.setMode(v); },
    get transport() { return aogpn.app.getTransport(); },
    set transport(v) { aogpn.app.setTransportRaw(v); },
    get protocolPreference() { return aogpn.app.getProtocolPreference(); },
    set protocolPreference(v) { aogpn.app.setProtocolPreferenceRaw(v); },
    get autoReconnect() { return aogpn.app.getAutoReconnect(); },
    set autoReconnect(v) { aogpn.app.setAutoReconnectRaw(v); },
    get systemProxyMode() { return aogpn.app.getSystemProxyMode(); },
    set systemProxyMode(v) { aogpn.app.setSystemProxyModeRaw(v); },
    get effectiveSystemProxyMode() { return aogpn.app.getEffectiveSystemProxyMode(); },
    set effectiveSystemProxyMode(v) { aogpn.app.setEffectiveSystemProxyModeRaw(v); },
    get systemProxyConnectionOwned() { return aogpn.app.getSystemProxyConnectionOwned(); },
    set systemProxyConnectionOwned(v) { aogpn.app.setSystemProxyConnectionOwnedRaw(v); },
    get systemProxyAppliedAddress() { return aogpn.app.getSystemProxyAppliedAddress(); },
    set systemProxyAppliedAddress(v) { aogpn.app.setSystemProxyAppliedAddressRaw(v); },
    get prevSystemProxyConnectionOwned() { return aogpn.app.getPrevSystemProxyConnectionOwned(); },
    set prevSystemProxyConnectionOwned(v) { aogpn.app.setPrevSystemProxyConnectionOwnedRaw(v); },
    get isAdmin() { return aogpn.app.getIsAdmin(); },
    set isAdmin(v) { aogpn.app.setIsAdminRaw(v); },
    get tunLocked() { return aogpn.app.getTunLocked(); },
    set tunLocked(v) { aogpn.app.setTunLockedRaw(v); },
    get activeGpnServer() { return aogpn.app.getActiveGpnServer(); },
    set activeGpnServer(v) { aogpn.app.setActiveGpnServerRaw(v); },
    get activeGpnMode() { return aogpn.app.getActiveGpnMode(); },
    set activeGpnMode(v) { aogpn.app.setActiveGpnModeRaw(v); },
    get lastIpState() { return aogpn.app.getLastIpState(); },
    set lastIpState(v) { aogpn.app.setLastIpStateRaw(v); },
    get lastIpMeasuredAt() { return aogpn.app.getLastIpMeasuredAt(); },
    set lastIpMeasuredAt(v) { aogpn.app.setLastIpMeasuredAtRaw(v); },
    get monitorSnapshot() { return aogpn.app.getMonitorSnapshot(); },
    get currentView() { return aogpn.app.getCurrentView(); }
  };

  function setConnected(next, nextConnecting) {
    const wasConnected = st.connected;
    st.connected = next;
    st.connecting = nextConnecting === true && !st.connected;
    body.classList.toggle('connected', st.connected);
    // The CONNECT ring only animates during a connection attempt (see
    // components.css `body.connecting` gates); idle and connected are static so
    // the WebView2 compositor has nothing to re-render at 60 fps.
    body.classList.toggle('connecting', st.connecting);
    if (next !== wasConnected) {
      if (!next) {
        // Bağlantı kesilince düğüm etiketi bayat kalmasın (önce/sonra değerleri
        // belgeli olarak son ölçümde kalır, ancak "via" hangi düğüm olduğunu
        // yalnızca bağlantı sırasında anlamlıdır).
        st.activeGpnServer = '';
        if (st.monitorSnapshot && (st.monitorSnapshot.apps || []).length) {
          aogpn.views.renderDashboardBoostCards();
          if (st.currentView === 'boost') aogpn.views.renderSplitApps();
        }
      }
    }
    const cyan = 'var(--cyan)', emerald = 'var(--emerald)';
    statusDot.style.background = st.connected ? emerald : st.connecting ? 'var(--amber, #f59e0b)' : cyan;
    statusDot.style.color = st.connected ? emerald : st.connecting ? 'var(--amber, #f59e0b)' : cyan;
    // The sidebar footer dot reflects the real connection state (was hardcoded green).
    const sidebarDot = document.getElementById('sidebarStatusDot');
    if (sidebarDot) {
      sidebarDot.style.background = st.connected ? emerald : cyan;
      sidebarDot.style.color = st.connected ? emerald : cyan;
      sidebarDot.title = st.connected ? t('status.connected') : t('status.disconnected');
    }
    statusLabel.textContent = st.connected ? t('status.connected') : st.connecting ? t('status.connecting') : t('status.disconnected');
    statusLabel.className = 'font-medium ' + (st.connected ? 'text-emerald-300' : st.connecting ? 'text-amber-300' : 'text-cyan-300');
    // The mobile quick-connect button mirrors the CONNECT ring state.
    const mobileConnectBtn = $('mobileConnectBtn');
    if (mobileConnectBtn) {
      mobileConnectBtn.classList.toggle('connected', st.connected);
      mobileConnectBtn.style.color = st.connected ? emerald : st.connecting ? 'var(--amber, #f59e0b)' : cyan;
      mobileConnectBtn.setAttribute('aria-label', st.connected ? 'Disconnect' : st.connecting ? 'Connecting' : 'Connect');
      // Locked while connecting (no double-toggle) or TUN lacks elevation.
      mobileConnectBtn.disabled = st.tunLocked || st.connecting;
    }
    // CONNECT is locked while connecting: the attempt is in flight, so a second
    // press must not fire another toggle_connection before the host resolves it.
    if (connectBtn) {
      const locked = st.tunLocked || st.connecting;
      connectBtn.disabled = locked;
      connectBtn.setAttribute('aria-disabled', String(locked));
      connectBtn.classList.toggle('cursor-not-allowed', locked);
    }
    btnText.textContent = st.connected ? t('connect.connected') : st.connecting ? t('connect.connecting') : t('connect.connect');
    btnIcon.style.color = st.connected ? emerald : st.connecting ? 'var(--amber, #f59e0b)' : cyan;
    btnIcon.style.filter = st.connected ? 'drop-shadow(0 0 16px rgba(var(--emerald-rgb),.7))' : 'drop-shadow(0 0 16px rgba(var(--cyan-rgb),.7))';
    // Swap the SVG ring arc colour
    var arc = document.getElementById('ringArc');
    if (arc) arc.setAttribute('stroke', st.connected ? emerald : cyan);
    btnSub.textContent = (st.mode === 'gpn' ? t('mode.gpn') : t('mode.globalVpn')) + (st.connected ? ' · ' + t('mode.active') : st.connecting ? ' · ' + t('status.connecting') : '');

    if (st.connected) {
      showConnectionError('');
      aogpn.telemetry.startSession();
      // Bağlıyken IP paneli kendini periyodik yeniden ölçer (bayat sızıntı uyarısı
      // en geç 25 sn'de düzeltilir — tarayıcı yenilemesi gerekmez).
      startIpRecheckLoop();
      playThemeSound(getCurrentThemeId()); if (typeof fireConfetti === 'function') fireConfetti();
    } else {
      aogpn.telemetry.stopSession();
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
    aogpn.views.updateGlobalPanel();
    aogpn.views.updateTunProxyNotice();
    aogpn.nodes.refreshSessionNode();
    // Push the new state to every loaded standalone skin (skinBridge.subscribe).
    aogpn.events.emit('skin-changed');
  }

  function updateConnectTooltip() {
    const routeLabel = st.mode === 'gpn' ? t('mode.gpn') : t('mode.globalVpn');
    const captureLabel = st.transport === 'tun' ? t('transport.tun') : t('transport.proxy');
    connectBtn.title = st.connected
      ? t('tip.connectActive')
      : st.connecting
        ? t('status.connecting')
        : t('tip.connectIdle', { route: routeLabel, capture: captureLabel });
    statusLabel.title = st.connected
      ? t('tip.statusActive', { route: routeLabel, capture: captureLabel })
      : st.connecting
        ? t('status.connecting')
        : t('tip.statusIdle');
    const mobileConnectBtn = $('mobileConnectBtn');
    if (mobileConnectBtn) {
      mobileConnectBtn.title = st.connected
        ? t('tip.connectActive')
        : t('tip.connectIdle', { route: routeLabel, capture: captureLabel });
    }
  }

  /** Live readout of what the connection is doing: route + capture + state. */
  function updateStatusLine() {
    if (!connectionStatusLine) {
      return;
    }
    const routeLabel = st.mode === 'gpn' ? 'GPN Game Tunnel' : 'Global VPN';
    const captureLabel = st.transport === 'tun' ? 'TUN' : 'Proxy';
    if (st.connected) {
      connectionStatusLine.className = 'text-xs text-emerald-300 leading-relaxed mt-4';
      connectionStatusLine.innerHTML = t('status.line.live', { route: '<span class="font-medium">' + routeLabel + '</span>', capture: '<span class="font-medium">' + captureLabel + '</span>' });
    } else if (st.connecting) {
      connectionStatusLine.className = 'text-xs text-amber-300 leading-relaxed mt-4';
      connectionStatusLine.textContent = t('status.connecting');
    } else if (st.connecting) {
      connectionStatusLine.className = 'text-xs text-amber-300 leading-relaxed mt-4';
      connectionStatusLine.textContent = t('status.connecting');
    } else if (st.tunLocked) {
      connectionStatusLine.className = 'text-xs text-amber-300 leading-relaxed mt-4';
      connectionStatusLine.innerHTML = t('status.line.locked', { capture: '<span class="font-medium">' + t('transport.proxy') + '</span>' });
    } else {
      connectionStatusLine.className = 'text-xs text-[#8A94A6] leading-relaxed mt-4';
      connectionStatusLine.innerHTML = t('status.line.idle', { route: '<span class="text-cyan-300 font-medium">' + routeLabel + '</span>', capture: '<span class="text-cyan-300 font-medium">' + captureLabel + '</span>', connect: '<span class="text-cyan-300 font-medium">' + t('connect.connect') + '</span>' });
    }
  }

  function applyMode() {
    document.querySelectorAll('.mode-pill[data-mode]').forEach(p => {
      const on = p.dataset.mode === st.mode;
      p.classList.toggle('active', on);
      p.classList.toggle('text-cyan-300', on);
      p.classList.toggle('text-[#A7B0BF]', !on);
    });
    if (st.connected) btnSub.textContent = (st.mode === 'gpn' ? t('mode.gpn') : t('mode.globalVpn')) + ' · ' + t('mode.active');
    else btnSub.textContent = st.mode === 'gpn' ? t('mode.gpn') : t('mode.globalVpn');
    // Global VPN forces TUN st.transport to capture ALL traffic.
    if (st.mode === 'vpn') {
      st.transport = 'tun';
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
      // Re-enable st.transport pills in GPN st.mode.
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
    // Toggle the st.mode-swap panels below.
    var pG=document.getElementById('panelGPN');
    var pV=document.getElementById('panelGlobal');
    if(pG&&pV){
      if(st.mode==='gpn'){
        pV.classList.add('hidden-panel');pV.classList.remove('active-panel');
        pG.classList.remove('hidden-panel');pG.classList.add('active-panel');
      }else{
        pG.classList.add('hidden-panel');pG.classList.remove('active-panel');
        pV.classList.remove('hidden-panel');pV.classList.add('active-panel');
      }
    }
    updateConnectTooltip();
    updateStatusLine();
    aogpn.views.updateGlobalPanel();
  }

  function applyTransport() {
    document.querySelectorAll('.transport-pill').forEach(p => {
      const on = p.dataset.transport === st.transport;
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
    st.tunLocked = st.transport === 'tun' && !st.isAdmin && !st.connected;
    const tunPill = document.getElementById('tunPill');
    const lockBadge = document.getElementById('tunLockBadge');
    const notice = document.getElementById('tunAdminNotice');
    if (tunPill) tunPill.classList.toggle('locked', st.tunLocked);
    if (lockBadge) lockBadge.classList.toggle('hidden', !st.tunLocked);
    if (notice) notice.classList.toggle('hidden', !st.tunLocked);
    const btnLocked = st.tunLocked || st.connecting;
    connectBtn.disabled = btnLocked;
    connectBtn.setAttribute('aria-disabled', String(btnLocked));
    connectBtn.classList.toggle('cursor-not-allowed', btnLocked);
    // The mobile quick-connect button obeys the same lock (TUN elevation OR
    // an in-flight st.connecting attempt).
    const mobileConnectBtn = $('mobileConnectBtn');
    if (mobileConnectBtn) {
      mobileConnectBtn.disabled = btnLocked;
      mobileConnectBtn.classList.toggle('cursor-not-allowed', btnLocked);
      mobileConnectBtn.classList.toggle('opacity-50', btnLocked);
    }
    if (st.tunLocked) {
      connectBtn.title = t('tip.connectLocked');
      if (transportHint) transportHint.textContent = t('transport.hintLocked');
      const mobileConnectBtn = $('mobileConnectBtn');
      if (mobileConnectBtn) mobileConnectBtn.title = t('tip.connectLocked');
    } else {
      updateConnectTooltip();
      if (transportHint) transportHint.textContent = st.transport === 'tun'
        ? t('transport.hintTun')
        : t('transport.hintProxy');
    }
    aogpn.views.updateTunProxyNotice();
    // TUN stack selector is only relevant while TUN capture is selected.
    const stackRow = document.getElementById('tunStackRow');
    if (stackRow) stackRow.classList.toggle('hidden', st.transport !== 'tun');
    updateStatusLine();
  }

  function applyProtocolPreference(next) {
    const known = ['auto', 'wireguard', 'mimic', 'hysteria2', 'openvpn'];
    st.protocolPreference = known.includes(String(next)) ? String(next) : 'auto';
    document.querySelectorAll('.protocol-pill').forEach(p => {
      const on = p.dataset.protocol === st.protocolPreference;
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
    if (protocolHint) protocolHint.textContent = hints[st.protocolPreference] || hints.auto;
  }

  function isProxyModeEnabled(value) {
    return value === 1 || value === 3;
  }

  function syncQuickControls() {
    if (quickProtocolSelect) {
      quickProtocolSelect.value = st.protocolPreference;
    }
    // The mobile-only protocol select mirrors the sidebar one.
    const mobileProtocolSelect = document.getElementById('mobileProtocolSelect');
    if (mobileProtocolSelect) {
      mobileProtocolSelect.value = st.protocolPreference;
    }
    if (quickAutoReconnect) {
      quickAutoReconnect.checked = st.autoReconnect === true;
    }
    // The hero-strip (mobile/compact) auto-reconnect toggle mirrors the sidebar one.
    const mobileAutoReconnect = document.getElementById('mobileAutoReconnect');
    if (mobileAutoReconnect) {
      mobileAutoReconnect.checked = st.autoReconnect === true;
    }
    // Keep the upgraded themed dropdown labels in sync with programmatic sets.
    refreshAllCustomSelects();
  }

  function applySystemProxyState(desired, effective, connectionOwned) {
    const normalize = value => Number.isInteger(Number(value)) && Number(value) >= 0 && Number(value) <= 3 ? Number(value) : 0;
    st.systemProxyMode = normalize(desired);
    st.effectiveSystemProxyMode = normalize(effective);
    st.systemProxyConnectionOwned = connectionOwned === true;

    if (systemProxyModeSelect) {
      systemProxyModeSelect.value = String(st.systemProxyMode);
    }
    refreshAllCustomSelects();

    const enabled = isProxyModeEnabled(st.effectiveSystemProxyMode);
    const desiredEnabled = isProxyModeEnabled(st.systemProxyMode);
    systemProxyToggleBtn.classList.toggle('enabled', enabled);
    systemProxyToggleBtn.classList.toggle('owned', st.systemProxyConnectionOwned);
    systemProxyDot.classList.toggle('bg-emerald-400', enabled);
    systemProxyDot.classList.toggle('bg-[#5B6472]', !enabled);
    systemProxyDot.style.color = enabled ? '#34D399' : '#5B6472';
    systemProxyLabel.textContent = st.systemProxyConnectionOwned
      ? 'PROXY · CONNECTION'
      : enabled ? (st.effectiveSystemProxyMode === 3 ? 'PROXY · PAC' : 'PROXY ON')
      : 'PROXY OFF';
    if (systemProxyAddress) {
      const address = st.systemProxyAppliedAddress || (enabled ? '127.0.0.1' : '');
      systemProxyAddress.textContent = address ? `(${address})` : '';
      systemProxyAddress.classList.toggle('hidden', !address);
    }
    const becameConnectionOwned = st.systemProxyConnectionOwned && !st.prevSystemProxyConnectionOwned;
    st.prevSystemProxyConnectionOwned = st.systemProxyConnectionOwned;
    systemProxyToggleBtn.title = st.systemProxyConnectionOwned
      ? 'PROXY ON was enabled, so CONNECT handed the system proxy to the active tunnel — the badge now reads PROXY · CONNECTION. Your independent PROXY ON preference is preserved and restored when you disconnect.'
      : desiredEnabled
        ? (st.systemProxyMode === 3
          ? 'System proxy is on via PAC (PROXY · PAC). Click to disable. Press CONNECT and the active tunnel takes it over, switching the badge to PROXY · CONNECTION until you disconnect.'
          : 'System proxy is on (PROXY ON). Click to disable. Press CONNECT and the active tunnel takes it over, switching the badge to PROXY · CONNECTION until you disconnect.')
        : 'System proxy is off — the tunnel can still connect without it. Click to enable (PROXY ON).';
    // Exactly when CONNECT hands the system proxy over to the active tunnel, tell
    // the user why the badge changed from PROXY ON to PROXY · CONNECTION.
    if (becameConnectionOwned && typeof window.notifyNodes === 'function') {
      window.notifyNodes('PROXY ON + CONNECT: the active tunnel now manages the system proxy — badge shows PROXY · CONNECTION. Your PROXY ON preference returns when you disconnect.');
    }
    aogpn.views.updateTunProxyNotice();
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


  window.setConnectionState = (next, backendMode, backendConnecting) => {
    // Keep the st.mode pill truthful when a real AoGPN st.mode change completes.
    if (backendMode === 'vpn' || backendMode === 'gpn') {
      st.mode = backendMode;
      applyMode();
    }
    setConnected(next === true, backendConnecting === true);
  };
  window.setTransport = (next) => {
    if (next === 'tun' || next === 'proxy') {
      st.transport = next;
      applyTransport();
      if (next === 'proxy') {
        showConnectionError('');
      }
    }
  };
  // GPN resilience decisions (server switch / UDP death / st.mode fallback /
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
        // The distinctive GPN Connect button badge mirrors the chosen st.mode so the
        // user sees Italy/Germany auto-selection landed even when the selected
        // profile wasn't WireGuard.
        if (typeof aogpn.gpn.setGpnConnectState === 'function') {
          aogpn.gpn.setGpnConnectState(evt.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WG');
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
  // "GPN Bağlan" butonu durum rozeti: host (GpnResilience st.mode kararlarıyla)
  // veya yerel tıklama sonrası durumu günceller.
  // Son GPN Bağlan durumu (label + pending) — standalone skin'lerin GPN Connect
  // kartı skinBridge.gpnConnectState üzerinden aynı değeri görür.
  window.setGpnConnectionInfo = (info) => {
    if (!info) return;
    // GPN seçim kararı yeni, yetkili bir kaynaktır — hız testi bekleme penceresini
    // kapat (sunucu değişimi/bağlantı sırasında bayat ölçüm gösterilmesin).
    aogpn.telemetry.clearAvailabilityHold();
    const server = info.server || info.Server || '';
    // "sonra" (tünel yolu) gerçek ping ölçümü hangi düğüm üzerinden yapıldıysa
    // delta satırında o düğüm adı görünsün (boost kartları + tablo "via" etiketi).
    st.activeGpnServer = server || '';
    const delay = Number.isFinite(info.delayMs) && info.delayMs >= 0 ? String(Math.round(info.delayMs)) : null;
    const connMode = info.mode || info.Mode || '';
    st.activeGpnMode = connMode;

    const pingCard = document.getElementById('pingVal');
    const pingSub = document.getElementById('pingSub');
    if (pingCard) {
      pingCard.textContent = delay != null ? delay : '--';
    }
    if (pingSub && server) {
      const modeTxt = connMode ? (' · ' + connMode) : '';
      pingSub.textContent = server + ' · ' + (delay != null ? delay + ' ms' : 'ölçüm bekleniyor') + modeTxt;
    }

    const sessionNode = document.getElementById('sessionNode');
    if (sessionNode && server) {
      sessionNode.textContent = server + (connMode ? ' · ' + connMode : '');
      sessionNode.title = sessionNode.textContent;
    }
    // Düğüm adı değişti → "via" etiketini boost kartlarında/tabloda anında yansıt.
    if (st.monitorSnapshot && (st.monitorSnapshot.apps || []).length) {
      aogpn.views.renderDashboardBoostCards();
      if (st.currentView === 'boost') aogpn.views.renderSplitApps();
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
      aogpn.telemetry.holdAvailability();
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
  window.setAdminState = (admin) => {
    st.isAdmin = admin === true;
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
      st.systemProxyAppliedAddress = '';
      applySystemProxyState(st.systemProxyMode, 0, false);
      window.notifyNodes(localizeProxyMessage(result && result.message ? result.message : 'proxyOnly.updateFailed'));
      return;
    }
    const message = typeof result.message === 'string' ? result.message : '';
    const addressMatch = message.match(/127\.0\.0\.1(?::\d+)?/);
    st.systemProxyAppliedAddress = addressMatch ? addressMatch[0] : (Number(result.mode) === 0 ? '' : st.systemProxyAppliedAddress);
    if (Number.isInteger(result.mode)) {
      applySystemProxyState(result.mode, result.mode, st.systemProxyConnectionOwned);
    }
    if (result.message) {
      window.notifyNodes(localizeProxyMessage(result.message));
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
   * IP is shown; when st.connected the tunnel IP is compared against the ISP IP
   * to verify traffic is actually routing. A mismatch means the tunnel works;
   * a match means traffic is leaking past the tunnel.
   */
  window.setRealIpState = (data) => {
    if (!data) return;
    applyRealIpState(data);
  };
  function applyRealIpState(data) {
    st.lastIpState = data;
    // Ölçüm tazeliği: host her ölçümde measuredAt (ISO) gönderir; panel ölçümün
    // yaşını gösterir ve bayat bir "sızıntı" uyarısını amber "yeniden ölçülüyor"a
    // düşürür — eski ölçüm asla güncel gerçek gibi sunulmaz.
    st.lastIpMeasuredAt = (typeof data.measuredAt === 'string' && data.measuredAt.length > 0)
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
      // Disst.connected: show ISP IP.
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
    aogpn.views.updateGlobalPanel();

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
    if (!ipVerifyDetail || !st.lastIpState) return;
    const ipSt = st.lastIpState;
    const isConnectedNow = ipSt.connected === true;
    const measured = st.lastIpMeasuredAt ? new Date(st.lastIpMeasuredAt).getTime() : null;
    const ageS = measured ? Math.max(0, Math.round((Date.now() - measured) / 1000)) : null;

    // Bayat sızıntı ölçümü → amber "yeniden ölçülüyor" (kesin uyarı değil).
    const isLeaking = isConnectedNow && (ipSt.tunnelVerified !== true)
      && typeof ipSt.tunnelIp === 'string' && ipSt.tunnelIp.length > 0
      && ipSt.ispCached === true;
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
      if (st.connected) postToHost({ action: 'check_ip' });
    }, 25000);
  }
  function stopIpRecheckLoop() {
    if (_ipRecheckTimer) { clearInterval(_ipRecheckTimer); _ipRecheckTimer = null; }
  }



  window.aogpn = window.aogpn || {};
  window.aogpn.connection = {
    setConnected, updateConnectTooltip, updateStatusLine, applyMode, applyTransport,
    updateTransportLock, applyProtocolPreference, isProxyModeEnabled, syncQuickControls,
    applySystemProxyState, showConnectionError, applyRealIpState, applyIpFreshness,
    getConnectionError: () => _connectionError || null
  };
})();
