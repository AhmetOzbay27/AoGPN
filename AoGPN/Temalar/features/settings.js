/* ==========================================================================
   features/settings.js — AoGPN dashboard feature module (Ayarlar formu)
   --------------------------------------------------------------------------
   Ayarlar formunun mantığı: seçenek listeleri (FALLBACK_OPTIONS + host
   güncellemeleri), alan doldurma/okuma, kaydetme akışı ve pencere davranışı
   toggle'ları. window.applySettings KOORDİNATÖRDE kalır — bağlantı/GPN
   durumuna doğrudan dokunur ve bu modülün fonksiyonlarını aogpn.settings
   üzerinden çağırır.
   ========================================================================== */
(() => {
  'use strict';
  const postToHost = aogpn.bridge.postToHost;
  const refreshAllCustomSelects = aogpn.selects.refreshAllCustomSelects;
  const setEffectsTier = aogpn.theme.setEffectsTier;
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

  window.aogpn = window.aogpn || {};
  window.aogpn.settings = {
    fillSettingsOptions, setSettingsField, collectSettings, saveSettings,
    settingsState, FALLBACK_OPTIONS
  };
})();
