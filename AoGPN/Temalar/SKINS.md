# Standalone skins (Temalar/skins/)

AoGPN's dashboard can be swapped for fully independent designs called **skins**.
Each skin is a complete, self-contained HTML document (its own markup, CSS,
fonts and logic) that runs inside an isolated `<iframe>`. It is *not* a color
theme and it does not touch the standard dashboard — the two never share a DOM
or stylesheet. The standard dashboard is simply the "standard" skin with no
iframe.

## How it works

- **Registry**: `Temalar/skins.json` lists every skin (`id`, `name`, `file`,
  `accent`, `tagline`). The picker buttons in the top-bar theme popover are
  rendered from this file.
- **Host**: `vpn-gpn-dashboard.html` contains a hidden `<div id="skinHost">`
  with `<iframe id="skinFrame">`. `app.js` (`applySkin`) shows/hides it and
  points the iframe at `Temalar/<file>` from the registry.
- **Active skin** is persisted in `localStorage['aogpn.skin']` and mirrored on
  `<body data-skin>` so the swap happens before the page finishes loading.
- **Bridge**: the skin talks to the app only through `window.parent.skinBridge`
  — live getters (`connected`, `mode`, `transport`, `splitMode`, `realNodes`,
  `telemetry`, `language`, …), setters that write back to the same state the
  real dashboard uses, and actions (`postToHost`, `requestNodeSwitch`,
  `applyNode`, `setConnected`, `applySkin`, `subscribe`). The skin never reads
  the main document.

## Adding a new skin

1. Create a folder `Temalar/skins/<id>/` with `skin.html` (required). You can
   add `skin.css` and `skin.js` next to it and reference them relatively —
   `skin.html` is served from that folder.
2. Build your skin like any independent web page. To show real app state, read
   it through the bridge at the bottom of your script:

   ```js
   const B = window.parent && window.parent.skinBridge;
   if (B) B.subscribe(render); // re-render whenever the app state changes
   ```

   When the page is opened directly in a browser (no parent bridge), the NEXUS
   skin falls back to a small demo state so the design stays editable — your
   skin should do the same (`if (B) … else …`).

3. Register it in `Temalar/skins.json`:

   ```json
   {
     "id": "my-skin",
     "name": "My Skin",
     "file": "skins/my-skin/skin.html",
     "accent": "#00ff88",
     "tagline": "Short description"
   }
   ```

   The picker button and the persisted-skin logic pick it up automatically; no
   HTML or `app.js` changes are needed.

## Rules

- Never touch the main document from a skin — use the bridge. `document` in
  `skin.js` is the skin's own document.
- Keep every skin self-contained: all colors/fonts/effects must live in the
  skin's own CSS (or be loaded by `skin.html`), never in the dashboard's theme.
- `body[data-skin]` is owned by the engine; only `applySkin` should change it.
- Build artifacts: the `Temalar\**` Content glob in `AoGPN/AoGPN.csproj`
  already ships `skins.json` and the whole `skins/` folder, so a new skin is
  picked up by the WPF app with no csproj change.
