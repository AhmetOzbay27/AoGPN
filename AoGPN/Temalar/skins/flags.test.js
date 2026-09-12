// ============================================================================
// Flags module (core/flags.js) — SVG bayraklar + nodes görünümü entegrasyonu
// ----------------------------------------------------------------------------
// Windows emoji bayrakları çizemediği için bayraklar gömülü SVG'dir; bilinmeyen
// ülke kodları harf karosuna düşer. Bu test hem modülün sözleşmesini (flagFor /
// flagMarkup / tileMarkup) hem de gerçek dashboard entegrasyonunu doğrular:
// düğüm kartlarında bayrak, ülke grubu başlıklarında bayrak + yerelleştirilmiş
// ülke adı, bilinmeyen ülkede bayraksız harf karosu.
// ============================================================================
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { loadDashboard } = require('./skin-sandbox.js');

test('flags: known country codes return inline SVG, unknown return null', async () => {
  const dash = await loadDashboard();
  const f = dash.window.aogpn.flags;
  assert.ok(f.flagFor('TR').includes('<svg'), 'TR flag is inline SVG');
  assert.ok(f.flagFor('DE').includes('#DD0000'), 'DE flag carries its stripes');
  assert.equal(f.flagFor('tr'), f.flagFor('TR'), 'codes resolve case-insensitively');
  assert.equal(f.flagFor('XX'), null, 'unknown code returns null');
  assert.equal(f.flagFor(''), null, 'empty code returns null');
  assert.equal(f.flagFor(null), null, 'null returns null');
  dash.close();
});

test('flags: tileMarkup renders a flag for known codes and letters for unknown', async () => {
  const dash = await loadDashboard();
  const f = dash.window.aogpn.flags;
  const flagTile = f.tileMarkup('IT', 'IT');
  assert.ok(flagTile.includes('<svg'), 'known code renders a flag');
  assert.ok(!flagTile.includes('>IT<'), 'flag tile does not show the letter pair');
  const letterTile = f.tileMarkup('ZZ', 'ZZ');
  assert.ok(!letterTile.includes('<svg'), 'unknown code falls back to letters');
  assert.ok(letterTile.includes('ZZ'), 'fallback letters are shown');
  dash.close();
});

test('flags: node cards show real flags, group headers show flag + localized country', async () => {
  const dash = await loadDashboard();
  const w = dash.window;
  const { document } = dash;
  assert.ok(dash.openView('nodes'), 'nodes view opens');

  w.updateNodeListAppend([
    { indexId: 'it-1', name: 'WG Italy', address: '92.4.220.236', port: 51820,
      protocol: 'WireGuard', country: 'IT', delay: 0, fav: false },
    { indexId: 'de-1', name: 'WG Germany', address: '130.61.223.36', port: 51820,
      protocol: 'WireGuard', country: 'DE', delay: 0, fav: false },
    { indexId: 'xx-1', name: 'Mystery', address: '203.0.113.5', port: 443,
      protocol: 'VLESS', country: '', delay: 0, fav: false }
  ]);
  w.updateNodeListDone('it-1');

  // Bayrak karosu kartın ilk satırındaki ilk span'dir (favori/ping ikonları
  // ayrı SVG'lerdir — karoyu hedefleyen seçici şart).
  const tileOf = card => card && card.querySelector('.flex.items-center.gap-3 > span');
  const itCard = document.querySelector('.node-card[data-index="it-1"]');
  assert.ok(itCard && tileOf(itCard).querySelector('svg'), 'IT card renders a flag');
  const deCard = document.querySelector('.node-card[data-index="de-1"]');
  assert.ok(deCard && tileOf(deCard).querySelector('svg'), 'DE card renders a flag');
  const xxCard = document.querySelector('.node-card[data-index="xx-1"]');
  assert.ok(xxCard && !tileOf(xxCard).querySelector('svg'), 'unknown-country card keeps the letter tile');
  assert.equal(tileOf(xxCard).textContent.trim(), 'VL', 'letter tile shows the protocol fallback');

  // Group headers: flag + localized country name (default lang is English).
  const headers = [...document.querySelectorAll('.route-group-header')];
  const itHeader = headers.find(h => h.dataset.group === 'IT');
  assert.ok(itHeader && itHeader.querySelector('svg'), 'IT group header shows the flag');
  assert.ok(itHeader.textContent.includes('Italy'), 'group header shows the localized country name');
  const deHeader = headers.find(h => h.dataset.group === 'DE');
  assert.ok(deHeader && deHeader.textContent.includes('Germany'), 'DE group header localized');
  const otherHeader = headers.find(h => h.dataset.group === 'Other');
  assert.ok(otherHeader, 'unknown-country group header exists');
  assert.ok(!otherHeader.querySelector('svg'), 'unknown group has no flag');

  dash.close();
});