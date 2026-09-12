/* ==========================================================================
   core/theme.js — AoGPN dashboard module (tema motoru + efektler)
   --------------------------------------------------------------------------
   app.js ile aynı küresel pencere kapsamında, ancak app.js'den ÖNCE yüklenir
   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).
   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel
   takma adlarını alır. ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const escHtml = aogpn.util.escHtml;
  const { $ } = aogpn.util;
  const postToHost = aogpn.bridge.postToHost;
  const body = document.body;
  let reduceEffects = false;
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
    { id: 'matrix',    name: 'Matrix',    signal: 'PHOSPHOR / GRID', tagline: 'Phosphor green, dark terminal',   a: '#16A34A', b: '#15803D', rgbA: '22,163,74', rgbB: '21,128,61', rgbEmerald: '0,255,65' },
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
      renderThemeDeck();
      applyTheme(_currentThemeId, false, false);
    } catch (e) { /* keep built-in fallback */ }
  }

  // ---------- Canvas effects engine (confetti, matrix rain) ----------
  var _cursorCanvas, _ctx;
  var _matrixCanvas, _matrixCtx, _matrixCols = [];
  var _audioCtx;
  var _confettiParticles = [];
  var _dpr = 1;

  function _resizeCanvas() {
    if (!_cursorCanvas) return;
    _dpr = window.devicePixelRatio || 1;
    _cursorCanvas.width = Math.round(window.innerWidth * _dpr);
    _cursorCanvas.height = Math.round(window.innerHeight * _dpr);
    // Draw in CSS pixels: the DPR transform keeps trails/confetti crisp on
    // HiDPI displays without changing the effect's coordinate math.
    if (_ctx) _ctx.setTransform(_dpr, 0, 0, _dpr, 0, 0);
  }

  var _cursorFramePending = false;
  function _requestFxFrame() {
    // The rAF loop only lives while particles are on screen; once everything
    // fades it stops itself (_renderFxFrame stops re-scheduling) so the
    // compositor can go fully idle.
    if (effectsReduced() || _effectsHidden || _cursorFramePending || !_ctx || !_cursorCanvas) return;
    _cursorFramePending = true;
    requestAnimationFrame(_renderFxFrame);
  }

  function _renderFxFrame() {
    _cursorFramePending = false;
    if (!_ctx || !_cursorCanvas) return;
    _ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
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
    // Everything faded out: stop the loop until the next fireConfetti
    // restarts it. Previously this re-scheduled every frame forever, keeping
    // the WebView2 GPU process rendering at 60 fps.
    if (_confettiParticles.length === 0) return;
    _cursorFramePending = true;
    requestAnimationFrame(_renderFxFrame);
  }

  function initCanvasEffects() {
    _cursorCanvas = document.getElementById('cursorCanvas');
    if (_cursorCanvas) {
      _ctx = _cursorCanvas.getContext('2d');
      _resizeCanvas();
      window.addEventListener('resize', _resizeCanvas);
      // No initial frame: with no confetti there is nothing to paint.
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
    if (effectsReduced() || effectsTier === 'balanced' || _effectsHidden || _matrixFramePending || !_matrixCtx || !_matrixCanvas) return;
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
    if (document.body.getAttribute('data-theme') !== 'matrix' || effectsReduced() || effectsTier === 'balanced' || _effectsHidden) {
      // Theme switched away, the tier froze the rain, or the app lost focus
      // (Alt+Tab / minimize): stop the loop and drop any leftover pixels
      // instead of re-filling the canvas every frame.
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
    velocity:  function() { _playTone(280, 'square', 0.15, 0.05); setTimeout(function() { _playTone(560, 'sine', 0.1, 0.04); }, 60); },
    aurora:    function() { _playTone(260, 'triangle', 0.3, 0.04); setTimeout(function() { _playTone(390, 'sine', 0.24, 0.03); }, 90); },
    candy:     function() { _playTone(520, 'sine', 0.12, 0.05); setTimeout(function() { _playTone(660, 'sine', 0.12, 0.04); }, 50); },
    obsidian:  function() { _playTone(130, 'sine', 0.35, 0.05); setTimeout(function() { _playTone(196, 'sine', 0.28, 0.03); }, 120); },
    sandstorm: function() { _playTone(180, 'triangle', 0.25, 0.04); setTimeout(function() { _playTone(270, 'sine', 0.2, 0.03); }, 100); },
    'neon-cyber':      function() { _playTone(340, 'square', 0.12, 0.05); setTimeout(function() { _playTone(680, 'sine', 0.08, 0.03); }, 40); },
    'titanium-orange': function() { _playTone(200, 'sawtooth', 0.16, 0.05); _playNoise(0.06, 0.03); },
    'matrix-green':    function() { _playTone(150, 'triangle', 0.2, 0.05); setTimeout(function() { _playTone(300, 'sine', 0.12, 0.03); }, 70); },
    'ocean-blue':      function() { _playTone(240, 'sine', 0.32, 0.04); setTimeout(function() { _playTone(360, 'sine', 0.24, 0.03); }, 100); },
    'red-phantom':     function() { _playTone(110, 'sawtooth', 0.16, 0.05); _playNoise(0.07, 0.04); },
    'violet-nova':     function() { _playTone(300, 'sine', 0.2, 0.06); setTimeout(function() { _playTone(450, 'square', 0.12, 0.04); }, 60); },
    'arctic-ice':      function() { _playTone(520, 'sine', 0.28, 0.03); setTimeout(function() { _playTone(780, 'sine', 0.2, 0.02); }, 80); },
    'gold-elite':      function() { _playTone(330, 'triangle', 0.22, 0.05); setTimeout(function() { _playTone(495, 'sine', 0.16, 0.04); }, 70); },
    'stealth-camo':    function() { _playTone(140, 'triangle', 0.2, 0.04); setTimeout(function() { _playTone(210, 'sine', 0.16, 0.03); }, 90); },
    'crimson-core':    function() { _playTone(90, 'sawtooth', 0.2, 0.06); _playNoise(0.09, 0.04); }
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
    if (effectsReduced() || _effectsHidden) return;
    _spawnConfetti(window.innerWidth / 2, window.innerHeight / 2, 50);
    // Wake the canvas loop: confetti must render even with a still mouse.
    _requestFxFrame();
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
      const on = card.dataset.themeId === theme.id;
      // theme-active is the legacy card class; the Appearance deck styles .active
      // (dash-theme-btn), so keep both in sync with the applied theme.
      card.classList.toggle('theme-active', on);
      card.classList.toggle('active', on);
      card.setAttribute('aria-pressed', String(on));
    });
    _currentThemeId = theme.id;
    // Start/stop the matrix rain loop with the theme: it renders only while
    // data-theme is "matrix" (see _requestMatrixFrame / _renderMatrixFrame).
    if (theme.id === 'matrix') _requestMatrixFrame();
    // Effects are initialized once during startup; switching palettes must not
    // add duplicate resize/mouse listeners or animation loops.
  }

  // The host calls this through CoreWebView2.ExecuteScriptAsync.
  window.applyTheme = applyTheme;

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

  // Appearance tab: the full theme deck (colour palette + signature effects).
  // The container id is #themePicker so applyTheme's active-card sync covers
  // host-driven theme pushes as well as clicks here. Same data-theme-id
  // contract as the (older) top-bar popover grid.
  function renderThemeDeck() {
    const grid = document.getElementById('themePicker');
    if (!grid) return;
    grid.innerHTML = THEMES.map(theme => {
      const active = theme.id === _currentThemeId;
      const name = themeName(theme);
      const tagline = themeTagline(theme);
      const tip = tagline ? name + ' — ' + tagline : name;
      return `<button type="button" data-theme-id="${theme.id}" aria-pressed="${active}"
        title="${escHtml(tip)}" aria-label="${escHtml('Theme: ' + name)}${active ? ' (active)' : ''}"
        class="dash-theme-btn${active ? ' active' : ''}">
        <span class="dash-theme-swatch" style="background:linear-gradient(90deg,${theme.a},${theme.b});color:${theme.a}"></span>
        <span class="dash-theme-name">${escHtml(name)}</span>
        <span class="dash-theme-signal">${escHtml(tagline)}</span>
      </button>`;
    }).join('');
    grid.querySelectorAll('[data-theme-id]').forEach(btn => btn.addEventListener('click', () => {
      applyTheme(btn.dataset.themeId);
      renderThemeDeck();
    }));
  }

  function renderTopThemePopover() {
    const grid = document.getElementById('topThemeGrid');
    if (!grid) return;
    // Keep the layout buttons in sync with the applied layout (e.g. host push).
    applyLayout(_currentLayout);
    // Same named palette cards as the Appearance deck (dash-theme-btn): every
    // theme shows its gradient swatch + name + tagline, so the popover exposes
    // the full theme registry instead of anonymous colour squares.
    grid.innerHTML = THEMES.map(theme => {
      const active = theme.id === _currentThemeId;
      const name = themeName(theme);
      const tagline = themeTagline(theme);
      const tip = tagline ? name + ' — ' + tagline : name;
      return `<button type="button" data-theme-id="${theme.id}" aria-pressed="${active}"
        title="${escHtml(tip)}" aria-label="${escHtml('Theme: ' + name)}${active ? ' (active)' : ''}"
        class="dash-theme-btn${active ? ' active' : ''}">
        <span class="dash-theme-swatch" style="background:linear-gradient(90deg,${theme.a},${theme.b});color:${theme.a}"></span>
        <span class="dash-theme-name">${escHtml(name)}</span>
        <span class="dash-theme-signal">${escHtml(tagline)}</span>
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
  // True while the app is not visible (Alt+Tab away, minimized, covered by
  // another window): the effects-freeze manager below pauses every CSS
  // animation at its current frame and stops the canvas loops. Set from the
  // visibilitychange/blur/focus handlers and exposed to skins as
  // skinBridge.frozen.
  let _effectsHidden = false;
  function effectsReduced() { return reduceEffects || _osReducedMotion; }
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
      _confettiParticles = [];
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

  // ---------- Effects freeze while the app is not visible ----------
  // Alt+Tab away, minimize, or a window covering the app: the user is not
  // looking, so the whole motion budget pauses. body.effects-hidden pauses
  // every CSS animation at its current frame (animation-play-state) — the
  // exact visual state is kept and everything resumes seamlessly on focus,
  // so no effect is ever lost. The canvas loops stop re-painting and drop
  // their pixels. A loaded skin iframe (same origin) is frozen the same way
  // via an injected pause rule in its own document.
  function _applyEffectsHidden() {
    body.classList.toggle('effects-hidden', _effectsHidden);
    if (_effectsHidden) {
      // Drop leftover pixels so nothing stale reappears on return; the loops
      // restart cleanly when the class is removed (see the gates above).
      if (_matrixCtx && _matrixCanvas) _matrixCtx.clearRect(0, 0, _matrixCanvas.width, _matrixCanvas.height);
      _matrixPaintHalf = false;
      _matrixFramePending = false;
      if (_ctx && _cursorCanvas) _ctx.clearRect(0, 0, _cursorCanvas.width, _cursorCanvas.height);
      _confettiParticles = [];
    } else {
      // Returning to the app: restart the self-stopped matrix rain (the
      // confetti engine restarts on demand from fireConfetti).
      _requestMatrixFrame();
    }
    // Freeze the skin iframe's own document the same way. Same origin
    // (aogpn.local virtual host), so the injected rule and body class reach
    // it directly; the rule is removed when unfrozen so the skin's normal
    // animations keep their exact cascade.
    try {
      const fdoc = skinFrame && skinFrame.contentDocument;
      if (fdoc && fdoc.body) {
        let st = fdoc.getElementById('aogpn-effects-hidden-style');
        if (!st) {
          st = fdoc.createElement('style');
          st.id = 'aogpn-effects-hidden-style';
          (fdoc.head || fdoc.documentElement).appendChild(st);
        }
        st.textContent = _effectsHidden
          ? 'body.aogpn-effects-hidden *, body.aogpn-effects-hidden *::before, body.aogpn-effects-hidden *::after { animation-play-state: paused !important; transition: none !important; }'
          : '';
        fdoc.body.classList.toggle('aogpn-effects-hidden', _effectsHidden);
      }
    } catch (e) { /* iframe not loaded or inaccessible yet — handled on load */ }
    // The boot path can reach this BEFORE window.aogpn.theme is registered
    // (the "started hidden" state below calls setEffectsHidden(true) while
    // the module is still initializing — the packaged app boots the WebView2
    // Visibility=Hidden, so document.hidden is true from the very first
    // frame). Guard the self-reference so a hidden boot cannot crash the
    // whole module chain: previously this threw "Cannot read properties of
    // undefined (reading 'onHiddenChange')", aogpn.theme never registered,
    // and every later module (selects, tooltips, settings, connection,
    // app.js) died on destructuring — the dashboard rendered but nothing
    // was clickable except the early-bound title-bar controls.
    if (window.aogpn && window.aogpn.theme && window.aogpn.theme.onHiddenChange)
    {
      try { window.aogpn.theme.onHiddenChange(_effectsHidden); } catch (e) { /* ignore */ }
    }
    aogpn.events.emit('skin-changed');
  }
  function setEffectsHidden(next) {
    next = !!next;
    if (next === _effectsHidden) return;
    _effectsHidden = next;
    _applyEffectsHidden();
  }
  window.setEffectsHidden = setEffectsHidden;
  // Alt+Tab and any focus move away freeze the dashboard; returning resumes
  // it. visibilitychange additionally covers minimize/restore and full
  // occlusion (when Chromium reports it).
  document.addEventListener('visibilitychange', () => setEffectsHidden(document.hidden));
  window.addEventListener('blur', () => setEffectsHidden(true));
  window.addEventListener('focus', () => setEffectsHidden(false));
  // Boot state (e.g. started minimized or behind other windows): honor it
  // from the first frame instead of animating unseen.
  if (document.hidden) setEffectsHidden(true);
  // A skin swapped in while frozen must freeze its new document too.
  if (skinFrame) skinFrame.addEventListener('load', () => { if (_effectsHidden) _applyEffectsHidden(); });

  window.aogpn = window.aogpn || {};
  window.aogpn.theme = {
    applyTheme, fireConfetti, applyLayout, renderThemeDeck, renderTopThemePopover,
    loadThemesFromDisk, initCanvasEffects, playThemeSound,
    setEffectsTier, setReduceEffects, setEffectsHidden,
    applyEffectsHiddenNow: _applyEffectsHidden,
    isEffectsHidden: () => _effectsHidden,
    getEffectsTier: () => effectsTier,
    getCurrentThemeId: () => _currentThemeId,
    getPerfState: () => ({
      matrixFramePending: _matrixFramePending,
      confettiN: _confettiParticles.length,
      osReducedMotion: _osReducedMotion
    }),
    THEMES, THEME_STORAGE_KEY, LAYOUT_STORAGE_KEY,
    onHiddenChange: null
  };
  initReduceEffects();
})();
