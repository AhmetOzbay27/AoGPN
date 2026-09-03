// ============================================================================
// INFRA skin — language mechanism (LANGUAGE selector + skin.infra.* keys)
// ----------------------------------------------------------------------------
// Same contract as CYBER/NEXUS: the header LANGUAGE select uses the bridge's
// LANGUAGES array, changes go through B.setLanguage (whole-app switch,
// persisted via set_language), and every static/dynamic text resolves through
// skin.infra.* scoped keys with shared + English fallbacks. Also guards the
// real-app regression: skin.html must actually include skin.js (the iframe
// would otherwise render a static page and none of this would run).
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { loadSkin } = require('./skin-sandbox.js');

function bootInfra(bridgeData) {
  return loadSkin('infra', bridgeData || {});
}

test('infra: skin.html actually includes skin.js (real-app regression guard)', () => {
  const html = fs.readFileSync(path.join(__dirname, 'infra', 'skin.html'), 'utf8');
  assert.match(html, /<script[^>]*src=["']skin\.js["']/,
    'the skin iframe must load skin.js — otherwise the skin is static in the real app');
});

test('infra: LANGUAGE select renders the bridge language list and current value', () => {
  const skin = bootInfra({
    LANGUAGES: [
      { code: 'en', name: 'English' },
      { code: 'tr', name: 'Türkçe' },
      { code: 'ru', name: 'Русский' }
    ],
    language: 'tr'
  });
  const d = skin.dom.window.document;
  const sel = d.getElementById('ifLang');
  assert.ok(sel, 'LANGUAGE select exists in the header');
  assert.equal(sel.options.length, 3, 'options come from the bridge LANGUAGES array');
  assert.equal(sel.options[0].value, 'en');
  assert.equal(sel.options[1].value, 'tr');
  assert.equal(sel.value, 'tr', 'current app language is selected');
  skin.dom.window.close();
});

test('infra: LANGUAGE change calls bridge setLanguage (whole-app switch) and mirrors the selection', () => {
  const calls = [];
  const skin = bootInfra({
    LANGUAGES: [{ code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }],
    setLanguage(lang) { calls.push(lang); }
  });
  const d = skin.dom.window.document;
  const sel = d.getElementById('ifLang');
  assert.equal(sel.value, 'en', 'boots with the bridge language');

  sel.value = 'tr';
  sel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.deepEqual(calls, ['tr'], 'switches the WHOLE app through the same channel as the top-bar picker');
  assert.equal(skin.bridge.language, 'tr', 'skin state mirrored');
  assert.equal(sel.value, 'tr', 'select reflects the new language after render');
  skin.dom.window.close();
});

test('infra: static texts resolve through skin.infra.* scoped translations', () => {
  // The stub t applies the real bridge's scoping (skin.infra.<key>) so the
  // skin actually pulls its labels from the language layer.
  const dict = {
    'skin.infra.panel.apps': 'BOOST UYGULAMALARI',
    'skin.infra.tele.loss': 'KAYIP',
    'skin.infra.metric.session': 'OTURUM',
    'skin.infra.status.offline': 'ÇEVRİMDIŞI'
  };
  const skin = bootInfra({ t: (key) => dict['skin.infra.' + key] || key });
  const d = skin.dom.window.document;
  assert.equal(d.querySelector('.if-panel-title').textContent, 'BOOST UYGULAMALARI', 'panel title from scoped key');
  assert.equal(d.querySelector('[data-if-i18n="tele.loss"]').textContent, 'KAYIP', 'telemetry label from scoped key');
  assert.equal(d.querySelector('[data-if-i18n="metric.session"]').textContent, 'OTURUM', 'metric label from scoped key');
  assert.equal(d.getElementById('ifStatus').textContent, 'ÇEVRİMDIŞI', 'dynamic status from scoped key');
  skin.dom.window.close();
});

test('infra: app route labels resolve through skin.infra.route.* before host routeLabels', () => {
  const dict = { 'skin.infra.route.direct': 'Doğrudan' };
  const skin = bootInfra({
    t: (key) => dict['skin.infra.' + key] || key,
    monitorSnapshot: {
      apps: [{ processName: 'cs2.exe', displayName: 'CS2', action: 'direct' }]
    },
    routeLabels: { direct: 'Direct (host)' }
  });
  const d = skin.dom.window.document;
  const row = d.querySelector('[data-app="cs2.exe"]');
  assert.ok(row, 'app row rendered from the real monitorSnapshot');
  assert.equal(row.querySelector('b').textContent, 'Doğrudan', 'scoped route translation wins over host routeLabels');
  skin.dom.window.close();
});

test('infra: console operation logs resolve through skin.infra.log.* translations', () => {
  const dict = {
    'skin.infra.log.toggle': 'GEÇİŞ GÖNDERİLDİ: {state}',
    'skin.infra.log.language': 'DİL: {lang}',
    'skin.infra.connect': 'BAĞLAN',
    'skin.infra.disconnect': 'BAĞLANTIYI KES'
  };
  const skin = bootInfra({
    LANGUAGES: [{ code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }],
    t: (key) => dict['skin.infra.' + key] || key
  });
  const d = skin.dom.window.document;
  const lastLine = () => { const c = d.getElementById('ifConsole'); return c.lastElementChild.textContent; };
  const click = (el) => el.dispatchEvent(new skin.dom.window.MouseEvent('click', { bubbles: true, cancelable: true }));

  // CONNECT toggle -> translated "TOGGLE SENT: {state}" with the translated action.
  click(d.getElementById('ifConnect'));
  assert.equal(lastLine(), '> GEÇİŞ GÖNDERİLDİ: BAĞLAN', 'connect log with scoped translations');

  // Language switch -> translated "LANG: {lang}".
  const sel = d.getElementById('ifLang');
  sel.value = 'tr';
  sel.dispatchEvent(new skin.dom.window.Event('change', { bubbles: true }));
  assert.equal(lastLine(), '> DİL: TR', 'language log with scoped translations');
  skin.dom.window.close();
});
