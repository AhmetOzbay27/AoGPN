/* ==========================================================================
   features/telemetry.js — AoGPN dashboard feature module (canlı telemetri)
   --------------------------------------------------------------------------
   Gauge kalibrasyonu (uyarlanabilir ölçek), canlı ping/kayıp/indirme/yükleme
   kartları, oturum sayacı ve GPN failover/kurtarma flaşı. app.js'den ÖNCE
   yüklenir; bağlantı durumuna aogpn.app üzerinden lazily erişir.
   ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const { $ } = aogpn.util;
  const { pingVal, pingSub, lossVal, lossSub, downVal, upVal,
          downGauge, upGauge } = aogpn.dom;
  let sessionSec = 0;
  let sessionTimer = null;
  const CIRC = 276.46;
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
    const app = aogpn.app;
    const connected = !!(app && typeof app.getConnected === 'function' && app.getConnected());
    const mode = app && typeof app.getMode === 'function' ? app.getMode() : 'gpn';
    const sample = [pingFromHost, lossFromHost, downFromHost, upFromHost];
    window.__nxTelemetry = sample;
    if (app && typeof app.setLastTelemetry === 'function') app.setLastTelemetry(sample);
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
    if (app && typeof app.updateGlobalPanel === 'function') app.updateGlobalPanel();
    aogpn.events.emit('skin-changed'); // refresh standalone skins with the latest telemetry sample
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

  // Shared session-time formatter (also used by skinBridge.sessionTime).
  function formatSessionTime() {
    const h = String(Math.floor(sessionSec / 3600)).padStart(2, '0');
    const m = String(Math.floor((sessionSec % 3600) / 60)).padStart(2, '0');
    const s = String(sessionSec % 60).padStart(2, '0');
    return `${h}:${m}:${s}`;
  }

  function tickSession() {
    sessionSec++;
    $('sessionTime').textContent = formatSessionTime();
    // IP ölçüm tazeliğini saniyede bir yenile (yaş artar, bayat sızıntı amber'e düşer).
    const app = aogpn.app;
    if (app && app.getLastIpState()) {
      if (typeof app.applyIpFreshness === 'function') app.applyIpFreshness();
    }
  }

  // Oturum zamanlayıcısı (setConnected tarafından çağrılır): sayaç her
  // bağlantıda sıfırlanır, her saniye tickSession tetiklenir.
  function startSession() {
    sessionSec = 0;
    $('sessionTime').textContent = '00:00:00';
    if (!sessionTimer) sessionTimer = setInterval(tickSession, 1000);
  }

  function stopSession() {
    if (sessionTimer) { clearInterval(sessionTimer); sessionTimer = null; }
  }

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

  window.aogpn = window.aogpn || {};
  window.aogpn.telemetry = {
    updateTelemetry, resetTelemetry, formatSessionTime, flashGpnTelemetry,
    startSession, stopSession,
    clearAvailabilityHold: () => { _availabilityHoldUntil = 0; },
    holdAvailability: () => { _availabilityHoldUntil = Date.now() + AVAILABILITY_HOLD_MS; }
  };
})();
