/* ==========================================================================
   core/tooltips.js — AoGPN dashboard module (özel tooltip motoru)
   --------------------------------------------------------------------------
   app.js ile aynı küresel pencere kapsamında, ancak app.js'den ÖNCE yüklenir
   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).
   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel
   takma adlarını alır. ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
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

})();
