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

## Keyboard shortcuts (shared across all skins)

Every skin binds the same global shortcut set (document-level keydown; typing in
an input/select/textarea never triggers them):

| Shortcut | Action |
|---|---|
| `Ctrl+Enter` | Connect / disconnect — same `toggle_connection` contract as the skin's main power button |
| `Alt+1..n` | Switch view / console tab (NEXUS: 8 sidebar views in order; CYBER/INFRA: 5 console tabs) |
| `R` | Cycle the **focused** boost app's route: vpn → direct → block → warp → vpn (same `set_app_route` contract as the switch) |

`R` reuses the shared pure decision logic in `route-keys.js` (`routeCycle`, unit
tested in `route-keys.test.js`) so all three skins cycle identically.

## Rules

- Never touch the main document from a skin — use the bridge. `document` in
  `skin.js` is the skin's own document.
- Keep every skin self-contained: all colors/fonts/effects must live in the
  skin's own CSS (or be loaded by `skin.html`), never in the dashboard's theme.
- `body[data-skin]` is owned by the engine; only `applySkin` should change it.
- Build artifacts: the `Temalar\**` Content glob in `AoGPN/AoGPN.csproj`
  already ships `skins.json` and the whole `skins/` folder, so a new skin is
  picked up by the WPF app with no csproj change.

## Feature parity matrix

Every skin covers the same dashboard modules — each in its own design
language. The integration tests (`skins/*.integration.test.js`) lock the host
contracts below, so a skin can never drift from the dashboard:

| Module | Standard | NEXUS | CYBER | INFRA |
|---|---|---|---|---|
| Connect / toggle_connection | ✅ | ✅ GPN panel | ✅ JACK IN | ✅ CONNECT |
| Mode / transport / split / direction pills | ✅ | ✅ settings | ✅ | ✅ |
| Auto-reconnect | ✅ | ✅ | ✅ | ✅ |
| System proxy toggle + mode + protocol | ✅ | ✅ settings | ✅ | ✅ |
| TUN stack select (`set_tun_stack`) | ✅ | ✅ settings | ✅ | ✅ |
| Effects tier select (`set_effects_tier`) | ✅ | ✅ settings | ✅ | ✅ |
| Auto-game connect (`set_auto_game_connect`) | ✅ | ✅ | ✅ | ✅ |
| GPN recovery watch (`set_gpn_recovery_watch`) | ✅ | ✅ | ✅ | ✅ |
| GPN failover (`set_gpn_failover`) | ✅ | ✅ | ✅ | ✅ |
| System-proxy test (`test_proxy` + result) | ✅ | ✅ | ✅ | ✅ |
| Boost apps with per-app route switches | ✅ | ✅ | ✅ | ✅ |
| Route nodes (select / switch) | ✅ | ✅ servers | ✅ destinations + NODES tab | ✅ left panel + NODES tab |
| Node pool subscription links | ✅ | ✅ servers | — | — |
| Server sort (ping/name/… ) | ✅ | ✅ servers | — | — |
| GPN servers + live probes | ✅ | ✅ servers view | ✅ GPN tab | ✅ GPN tab |
| GPN failover telemetry (switch/death/fallback/recover/select + reset) | ✅ | ✅ Advanced & Diagnostics | ✅ GPN tab | ✅ GPN tab |
| GPN resilience log (last-50 decisions, refresh/clear, mirror path) | ✅ | ✅ Advanced & Diagnostics | ✅ GPN tab | ✅ GPN tab |
| Connection Monitor (filter, hide listeners, per-row route) | ✅ | ✅ analytics | ✅ MONITOR tab | ✅ MONITOR tab |
| Connection Monitor group-by (route/protocol/state/country/app, collapsible) | ✅ | ✅ analytics | ✅ MONITOR tab | ✅ MONITOR tab |
| Undo toast (`undo_last_app_op`) | ✅ | ✅ | — | — |
| Game profiles / domains (`add_domain_route`) | ✅ | ✅ games | — | — |
| About / version info | ✅ | ✅ about | ✅ ABOUT tab | ✅ ABOUT tab |
