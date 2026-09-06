/* ==========================================================================
   features/release-notes.js — AoGPN dashboard module (sürüm notları + uygulama bilgisi)
   --------------------------------------------------------------------------
   app.js ile aynı küresel pencere kapsamında, ancak app.js'den ÖNCE yüklenir
   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).
   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel
   takma adlarını alır. ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  // The raw object is also captured so standalone skins can read it through the
  // skinBridge (skin About views show the real version, not a hard-coded one).
  let _appInfo = null;
  window.setAppInfo = function (info) {
    _appInfo = (info && typeof info === 'object') ? info : null;
    const v = (info && info.version) ? String(info.version) : '';
    const appName = (info && info.appName) ? String(info.appName) : 'AO GPN';
    const vEl = document.getElementById('aboutVersion');
    if (vEl) vEl.textContent = v ? 'V' + v : 'V? — ' + appName;
    aogpn.events.emit('skin-changed');
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
    const version = (_releaseNotes && _releaseNotes.version) || '1.1.1';
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

  window.aogpn = window.aogpn || {};
  window.aogpn.releaseNotes = {
    loadReleaseNotesFromDisk, renderReleaseNotes,
    getAppInfo: () => _appInfo
  };
})();
