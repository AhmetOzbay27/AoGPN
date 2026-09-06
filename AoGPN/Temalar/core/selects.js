/* ==========================================================================
   core/selects.js — AoGPN dashboard module (temalı <select> yükseltmesi)
   --------------------------------------------------------------------------
   app.js ile aynı küresel pencere kapsamında, ancak app.js'den ÖNCE yüklenir
   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).
   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel
   takma adlarını alır. ========================================================================== */
(() => {
  'use strict';
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

  window.aogpn = window.aogpn || {};
  window.aogpn.selects = { upgradeSelect, refreshCustomSelect, refreshAllCustomSelects };
})();
