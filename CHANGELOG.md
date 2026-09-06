# Changelog

All notable changes to AoGPN will be documented in this file.

> Version headings carry ISO dates derived from the corresponding git commit.
> Note: this repository holds a shallow, re-implemented history (all work dated
> 2026-07/08), so the `7.24.4` line resolves to `2026-07-30` and the `7.25.x`
> releases all resolve to `2026-08-24` — these are snapshot dates, not the
> original upstream release dates.

---

## [1.1.1-dev] — 2026-09-05 — WARP degrade-egress, capture-gap hardening, launcher bypass editor, shell declutter

> Development notes for this session (9 commits: `d3cdeb5` … `19ba8c0`, plus the
> uncommitted working tree). No version tag has been attached yet — the next
> release tag must be `1.1.1`.

### Added
- **WARP degrade-egress** (`GpnBypassEgressController`): while the WARP egress is
  faulted, launcher requests automatically fall back to **DIRECT** (clean exit
  instead of hitting the WAF); when WARP health returns they switch back
  automatically. Dashboard Degraded badge + i18n keys in all 9 languages.
- **Capture-gap hardening (A2):**
  - First-tick PID capture — a game spawning while the connection is being
    established (launcher auto-start) enters the WinDivert filter in ~750 ms
    instead of up to 5 s; silent while the PID set is unchanged.
  - Capture-drift warning — "live connections but 0 captured packets" rose
    banner (`GpnCaptureDriftChecker`; gpn mode + tunneled apps only, keys in
    all 9 languages).
- **Launcher bypass editor** (`LauncherBypassItem` + `GpnLauncherBypass`): the
  fixed `BsgLauncherDomains` list is replaced by a user-editable, per-launcher
  **domain + egress (WARP / VLESS / DIRECT)** list, managed in Dashboard
  Settings → GPN (add/remove/enable rows). An empty list disables launcher rows;
  `null` keeps the legacy BSG defaults. Works together with
  `VlessBypassNodeJson` (dual connection).

### Fixed
- `SpeedtestService`: `OverflowException` on empty selection — early exit
  (`Count == 0 || pageSize <= 0`) now covers Tcping/Realping/UDP alike; an empty
  run reported "completed" instead of "stopped".

### Changed
- **Splash-free boot (uncommitted working tree)**: the main window is shown
  invisible at startup (`Opacity 0` in `App.OnStartup`) and revealed by
  `MainWindow.RevealStartupWindow()` only once the WebView2 dashboard has loaded
  and received its initial state (theme/settings/language/…), so launching never
  flashes an empty black frame while WebView2 boots. The reveal is a short
  (~120 ms) fade-in so the first composited frame — including a standalone skin
  iframe that is still painting — can never appear as a raw dark frame. Reveal
  also fires on navigation failure / startup-script errors, on restore from a
  tray/minimized (AutoHideStartup) startup, and via a 15 s fallback timer — the
  window can never stay invisible.
- **Mid-session restart deferral**: rule edits that change the structure
  (`IsStructuralEntryChange`) no longer restart the core mid-match — they are
  deferred to the next natural reconnect, preventing drops.
- **P0 Wave 3 (refactor):** `DashboardNodeService` (969 lines) and
  `DashboardPushService` (659) extracted from MainWindow — the window went from
  7,071 to ~3,981 lines (3,999 in the working tree).
- **Shell declutter** (uncommitted working tree): legacy left-rail surface
  removed — the old "Servers" tab (`tabProfiles2` + `btnNavServers`) and the
  More Tools protocol add menu (`btnNavAddServer`, 9 protocols). Node management
  now lives solely in the dashboard **Nodes** view; Import (clipboard) and Scan
  (QR) are kept. Inventory/analysis:
  `docs/ozellik-envanteri-2026-09-05.md`.

### Removed
- **Startup splash screen (uncommitted working tree)**: `Views/SplashWindow.xaml` /
  `.xaml.cs` and `Resources/Splash.png` deleted (csproj `<Resource>` entry
  dropped); the boot logo window, progress stages and fade-out path removed from
  `App.OnStartup`. `TANITIM.md` / `RELEASE_YONERGESI.md` no longer describe the
  splash.
- V2rayN leftovers and scratch scripts quarantined (commit `361218d`; files under
  `Silinecekler_Yedek/V2rayN-Kalintilari-2026-09-05`).
- **Hidden legacy toolbar** (`legacyToolbar`, already `Visibility="Collapsed"`) deleted
  from `MainWindow.xaml` (−278 lines): the 17 single-protocol "Add server" items
  (VMess/VLESS/Shadowsocks/SOCKS/HTTP/Trojan/Hysteria2/TUIC/WireGuard/AnyTLS/Naive/
  Custom/Policy-group/Proxy-chain), the five Subscription items, the option/routing/DNS/
  full-config/hotkey/reboot/SetUWP/clear-stats/regional-preset menu items, and the
  Help/Reload/Promotion/Close/update/verbose-log entries. Rail buttons that already
  duplicate these commands (Settings/Routing/DNS/Import/Scan) stay; update-check and
  backup/restore dialogs are no longer reachable from any menu (can be re-exposed later).

### Technical
- **P0 Wave 3 recipe**: byte-exact cuts, ctor-injected delegates and one-line
  window delegations; every moved member body was verified byte-identical to the
  pre-move text.
- Connection/tray cluster closed via the **"keep — document why"** branch:
  `docs/connection-tray-cluster.md` (75-member inventory, state-machine models
  4.1–4.5, stay rationale, migration blueprint). Next extraction candidates:
  `docs/mainwindow-wave4-candidates.md` (W4-A … W4-F).
- **W4-B implemented** (`ConnectionFailureLedger`): failure-card record/priority
  and the 45 s freshness window moved out of MainWindow into a testable
  ServiceLib service (injected delegates + clock). Failure-card selection rules
  (45 s window, GPN priority, elevation/canRecover/port-carry derivation, script
  content) now covered by 16 new tests; MainWindow 3,999 → 3,752 lines.
- New tests: `GpnBypassEgressControllerTests` (9), `GpnCaptureDriftCheckerTests`
  (8), `GpnTargetResolverRefreshTests` (2), `FmtUriRoundTripTests` (15 cases),
  Speedtest regression tests (7), launcher-bypass rule tests +
  `lb-editor.integration.test.js`, `ConnectionFailureLedgerTests` (16).
- Verification at write time: `dotnet build` 0 errors; ServiceLib.Tests
  **1,178 passed / 0 failed** (incl. 16 new `ConnectionFailureLedgerTests`);
  dashboard JS **212/212**.

---

## [1.1.0] — About & Help page, AoGPN-native version

This is the first AoGPN-native version **1.1.0** (assembly/update-check
version in `Directory.Build.props`). The full build-up — every Game Boost /
GPN / node-pool / connection-logic / UX change accumulated while developing
this release — is documented as milestones folded into this release, listed
chronologically beneath the version headings below (see "Development
milestones").

### Added
- **About & Help page** (`nav.about`), reachable from both the desktop sidebar
  and the mobile icon nav, with two tabs:
  - **Program Info**: maintainer, license (GPL-3.0), supported platforms and
    cores, plus links to the wiki and the source repository. It shows the exact
    running build — the version is pushed live from the native host
    (`MainWindow.PushAppInfoAsync`) lifting it straight out of the assembly, so
    it always matches the real revision, exactly like the window title and the
    splash screen.
  - **Help & Feedback**: quick links to the Wiki, bug reporting, feature
    requests, the Telegram group and the Telegram announcement channel — the
    user-feedback entry points requested.
  - **Release Notes**: a third tab that folds the 1.1.0 milestone roadmap in —
    68 milestone rows (7.26.0 → 7.26.66), each expanding to a short
    description, with **Expand all / Collapse all**. Loaded from
    `Temalar/release-notes.json` (the same pattern as `themes.json`), so new
    releases only add one JSON file.
- New i18n keys (`nav.about`, `view.about.*`, `about.*`) in all 9 language
  files (`en`, `tr`, `zh-Hans`, `zh-Hant`, `fa`, `fr`, `hu`, `id`, `ru`) plus
  the in-dashboard fallback dictionary in `Temalar/app.js`; tab switching
  follows the same pattern as the Settings page.

### Changed
- Application version bumped from `1.0.0` to `1.1.0` in
  `Directory.Build.props`. The update checker compares this assembly version
  against release tags, so the next tagged release must use `1.1.0`.

### Technical
- `AoGPN/Views/MainWindow.xaml.cs`: `PushAppInfoAsync()` pushed alongside
  `PushLanguageAsync()` at dashboard startup — sends `version`, `appName` and
  `arch` to `window.setAppInfo`.
- `Temalar/app.js`: `about` registered in the real-views list;
  `window.setAppInfo` renders the live version on the About page; `data-about-tab`
  panel wiring.
- `vpn-gpn-dashboard.html`: sidebar + mobile nav items and the About/Help
  `<section id="viewAbout">` with the Program Info, Help & Feedback and
  Release Notes panels.
- `Temalar/release-notes.json`: ships beside the dashboard (the csproj already
  copies `Temalar/**`);  holds `version` + the 68 milestone `{n,title,desc}`
  entries. `Temalar/app.js`: `loadReleaseNotesFromDisk()` fetches it (with a
  graceful empty state) and `renderReleaseNotes()` builds the accordion.
  New i18n keys `about.tabRelease/releaseIntro/releaseExpandAll/releaseCollapseAll/releaseEmpty`
  in all 9 languages + the fallback dict. Tests: `Temalar/skins/release-notes.test.js`
  (3 tests: render+count, per-row toggle, expand/collapse all).

---

### Development milestones folded into this release

The milestones below are the development build-up that shipped as this single **1.1.0** release. They were tracked as separate `7.26.x` changelog entries, but none shipped on its own — together they form the release above them. Listed newest-first (`7.26.66` → `7.26.0`), matching the changelog order.

#### [7.26.66] — Live TARGET IP column on the Game Boost rows

##### Added
- **Every Game Boost row now shows the live destination IPs of the app's connections.** A new **Target IP** column sits between *Real ping* and *↓ Down* in the standard dashboard route table and the NEXUS Game Profiles table. The values come straight from the running core's `GET /connections` metadata (`metadata.destinationIP`, falling back to `metadata.host`) — the same poll that already feeds the per-app DOWN/UP traffic — so the user can see which game servers (e.g. the actual Tarkov raid/matchmaking endpoints) an app is talking to right now, without leaving the dashboard.
- **Game-friendly aggregation and compact rendering.** A game's connections are almost always UDP, so UDP endpoints are listed before TCP ones and duplicates are collapsed into a single comma-separated cell; the first two endpoints are shown with a `+N` summary for the rest, and the full list stays available in the cell tooltip. Idle rows show `—`.

##### Technical
- `TrafficMonitorItem.ActiveIps` (new) + `ConnectionMonitorViewModel`: each `/connections` record now carries its destination (`DestinationOf`: real IP when parseable, else the SNI/hostname) and network type; `AggregateDestinations` builds the UDP-first, deduplicated list per app group.
- `SplitTunnelAppItem.ActiveIps` (new reactive field) + `SplitTunnelViewModel.ApplyTrafficToApps`: the IP list rides the same name/path traffic matching already used for DOWN/UP, so the row stays in sync on every 2 s monitor refresh.
- `MainWindow.PushMonitorSnapshotAsync`: `activeIps` added to both the `apps` and `traffic` payload arrays (`window.updateMonitorSnapshot`).
- `vpn-gpn-dashboard.html` header + `Temalar/app.js` `targetIpsMarkup`; NEXUS skin `boost.colTarget` i18n key + `nxTargetIpsCell`. Verified: ServiceLib builds clean and the full dashboard/skin JS suite stays green (208/208).

---

#### [7.26.65] — Game Boost dashboard buttons wired end-to-end

##### Fixed
- **The BSG API → WARP chip, the + Add domain form and the row reorder arrows silently did nothing.** Every dashboard message funnels through `DashboardMessageParser.TryParse`, which enforces `DashboardMessagePolicy.AllowedActions` — and the dispatcher had gained `add_domain_route` / `move_route` (plus `gpn_cluster_probe`, `set_effects_tier`, `set_window_behavior`) as implemented `case` handlers without those actions ever being allow-listed. The buttons posted perfectly valid JSON that was discarded at the policy gate before the switch ran; no toast, no row, no rule. The five missing actions are now on the allowlist — matching the dispatcher *and* the JS that actually emits them — and the window-chrome cases (close/drag/minimize) are unaffected in their separate `HandleAppControl` dispatcher.
- **The payload contract is now regression-locked.** New contract tests parse the exact wire payloads of the quick button (`{action:'add_domain_route', value:'escapefromtarkov.com', route:'warp', displayName}`) and the reorder arrows (`{action:'move_route', entryType, value, direction}`), so a future shape change fails in the suite instead of on the user.

##### Technical
- `DashboardMessagePolicy.AllowedActions` +5 entries; `CoreEngineHostTests.KnownActions_AreAllowed` theory extended; `DashboardMessageContractTests` +2 wire-contract tests. No JS/HTML changes were needed — the front end was already sending the right messages.

---

#### [7.26.64] — BSG launcher/API domains pinned to the launcher egress

##### Added
- **The hardcoded BSG domain bypass list now covers the full known launcher/auth/API host set.** `GpnMihomoConfigService.BsgLauncherDomains` carries the apex families `escapefromtarkov.com`, `battlestategames.com`, `tarkov.com` (the profile API lives on this *separate* registrable domain, behind the Cloudflare WAF) and `escapefromtarkov.ru` (the RU launcher mirror — found in a real session log), plus the explicit `prod.` / `launcher.` / `gw-pvp.` / `www.escapefromtarkov.com`, `launcher.escapefromtarkov.ru` and `profile.tarkov.com` rows. Every entry emits as a `DOMAIN-SUFFIX` rule at the **very top** of `rules:` — before any `PROCESS-NAME` rule and `MATCH`, first-match-wins — routing the Tarkov launcher's CefSharp API traffic to `vless-launcher` in dual-outbound mode (legacy mode keeps `warp-socks`). The Cloudflare "Fatal Error" caused by launcher API calls leaking into the WireGuard tunnel is closed.

##### Technical
- `GpnMihomoConfigService.cs` `BsgLauncherDomains` + updated doc comments (the audit used the app's own live session log: `profile.tarkov.com:443` / `launcher.escapefromtarkov.ru:443` previously matched `[socks -> proxy]` into the WG tunnel). `GpnMihomoConfigServiceTests.BsgDomains_AlwaysInjectAtTop_ToLauncherEgress` asserts presence *and* top-of-list ordering for every entry in dual and legacy modes (36/36).

---

#### [7.26.63] — One-click domain rules in Game Boost: ⚡ BSG API → WARP chip + manual domain form

##### Added
- **A destination rule can now be added from Game Boost in one click.** A new **⚡ BSG API → WARP** button (standard dashboard and the NEXUS Game Profiles skin) inserts `escapefromtarkov.com → WARP` at the top of the routing list — the BSG launcher/game API traffic then exits through the clean Cloudflare WARP egress while raid-server IP traffic keeps the low-ping VPN path. A general **+ Add domain** panel beside it accepts any `host[:port]` destination with a route picker (WARP/VPN/Direct/Block), so a destination rule no longer needs the EXE picker at all.
- Both entries run through the same `ManualRouteParser` validation as the native add flows (`AddDomainRoute` in `SplitTunnelViewModel`): invalid input is rejected, and an already-listed host is refused with the existing "already exists" notice instead of silently overwriting the row's action.

##### Fixed
- **The Game Boost domain-add message used the `action` key twice** — once for the host command and once for the route value — so the second `action` overwrote the first in JS and the host received `action: "warp"` (a route, not a command) and never dispatched it. The route now travels under its own **`route`** key in both the standard dashboard and the NEXUS skin, and the `add_domain_route` host case reads it from `route` (the same convention as `set_app_route`). The integration tests had duplicated the broken payload shape and could pass on it; they now assert the real contract.

##### Technical
- `MainWindow.xaml.cs`: `add_domain_route` host case → `AddDomainRoute`, allow-listed in `DashboardMessagePolicy`; snapshot push + toast on success/rejection.
- `vpn-gpn-dashboard.html` / `Temalar/app.js`: `#boostBszApiBtn` chip, the collapsible `boostAddDomainBtn` panel (`boostDomainValue`/`boostDomainAction`/`boostDomainSubmit`), Enter-to-submit and `fillDomainActions` route picker.
- `Temalar/skins/nexus/skin.js`: `[data-nx-bszapi]` chip and the `[data-nx-adddomain]` form, posting `{action:'add_domain_route', value, route, displayName}`.
- Tests: dashboard + NEXUS integration suites cover the chip payload (`{action:'add_domain_route', value:'escapefromtarkov.com', route:'warp', ...}`), the manual form and the empty-input guard; the payload-contract tests parse both `add_domain_route` and `move_route`.

---

#### [7.26.62] — Game Boost rows are reorderable: domain rules can take precedence

##### Added
- **Every Game Boost row now has ↑ / ↓ move buttons** (standard dashboard route table and the NEXUS Game Profiles table). Cores match routing rules top-down and rule order equals list order, so moving lets a domain rule sit **above** the process rule it must win over — e.g. `escapefromtarkov.com → WARP` before `EscapeFromTarkov.exe → VPN`, so the version-check domain exits via WARP while the game keeps the VPN. The move persists with the list (precedence survives restarts) and auto-applies through the normal collection-change path (Manuel mode).
- Edge rows disable the unusable button (first row ↑, last row ↓); while the row filter is being typed the move controls hide, so a move can never target the filtered position instead of the real row.

##### Technical
- `SplitTunnelViewModel.MoveManualRoute(entryType, value, up)`: locates the row by type + value (works for `app`, `domain` and `ip` rows alike) and swaps it with its neighbour (`CollectionChanged` → persist + debounced auto-apply); rejects unknown entries and edge moves.
- `MainWindow.xaml.cs`: `move_route` host case (`entryType`/`value`/`direction`), allow-listed in `DashboardMessagePolicy`; fresh snapshot + success/failure toast per move.
- `Temalar/app.js` `bindMoveButtons` (`data-move-entry`) and `Temalar/skins/nexus/skin.js` `[data-nx-move]` buttons post `{action:'move_route', entryType, value, direction}`.
- Tests: dashboard integration suite (edge disabling, domain-row move payload, controls hidden while filtering) + NEXUS suite.

---

#### [7.26.61] — ⚡ Tarkov preset removed — Game Boost is fully manual

##### Removed
- **The one-click Tarkov preset is gone** (per user request — Game Boost routing is configured by hand from now on): the **⚡ Tarkov preset** button in the standard dashboard and the NEXUS skin, its `add_app_preset` host action, `SplitTunnelViewModel.AddPreset`, the `KnownAppPresets` catalog, the `PresetBatchBuilder` and their test files. The sing-box / mihomo config tests that consumed the preset now build the launcher/game/BattlEye app entries inline, so the routing coverage (launcher → WARP, game → VPN, warp outbound generation) is preserved.
- `docs/gpn-warp-live-test.md` documents the manual three-process + domain setup instead of the preset.

##### Changed
- **Hand-add suggestions are untouched**: adding `BsGLauncher.exe` still pre-suggests WARP and `EscapeFromTarkov.exe` pre-suggests VPN, and the native Game Boost EFT tile stays — only the preset shortcut is gone. `KnownAppCatalog` comments were updated to point at the manual flow.

---

#### [7.26.60] — GPN route mode & per-app route changes switch without a core restart

##### Added
- **Off / Global VPN / Game Tunnel and per-app route changes now switch the live tunnel by group selection instead of restarting the core.** The whole journey since `7.26.58` is now restart-free: a connected mihomo session keeps its process, TUN adapter and listeners, and each mode/app-route assignment is expressed as one `PUT /proxies` call on a group whose members are the supported targets — no re-handshake, no connection drop.
- **Superset config with mode + per-app groups.** The mihomo YAML producer gained a superset mode: every app-managed routing entry becomes a fixed rule row that targets its own `ao-<i>` select group (members `DIRECT` / `GPN-Nodes`), the catch-all row targets the `GPN-MODE` group, and `warp-socks` is always present with `dialer-proxy: GPN-Nodes`. Off is then just selecting `DIRECT` on `GPN-MODE` — the tunnel stays alive with everything direct and flips back instantly without a new WireGuard handshake.
- **`GpnSoftRouting` (pure policy model)** mirrors `ManualRoutingRules` exactly — Off / Global VPN / Game Tunnel selection vectors for both whitelist and blacklist directions, plus the per-entry group mapping — so the superset rules are the app's real rules frozen at connect time.
- **`GpnSoftPolicyApplier` applies deltas live.** It fingerprints the app-managed entry list at config generation (`GpnSoftSession`) and, on a change request, applies only the group selections whose target actually changed, gated by: mihomo running, superset groups present, entry-list fingerprint match, target-is-a-member validation, and a post-PUT verification read-back. Any gate failure falls back to the previous reload path, so behaviour can never degrade.

##### Changed
- **`SplitTunnelViewModel.ApplyAsync` tries the soft path before `ReloadRequested`.** All dashboard/tray mode buttons (Off / Global VPN / Game Tunnel), the direction (whitelist/blacklist) toggle and every per-app route change funnel through `ApplyAsync`, so with a superset session live they now switch without a core restart.
- **`CoreConfigHandler`/`CoreConfigContext`**: the superset branch drops the managed routing-item rules (they are replaced by the fixed group rows) while preserving user-defined rules; `GpnCoreLauncher` stamps the soft policy on every WireGuard launch and clears the session on stop. `MainWindow` skips the proxy-only core reconciliation while the GPN tunnel is still up, so an "Off" selection can never double-start a second core on the same port.

##### Technical
- `GpnSoftRouting` + `GpnSoftPolicyApplier`/`GpnSoftSession` in `ServiceLib/Services/CoreConfig/Mihomo/` and `ServiceLib/Services/Gpn/`; superset `GenerateYaml` overload in `GpnMihomoConfigService` (legacy single-node output byte-identical, guarded by existing tests). Tests: 22 new unit tests across the pure policy model, superset YAML producer, dispatch and the applier (vector math, delta computation, fingerprint/validation gates, fallback); full suite green at **985 passed / 0 failed**, WPF app compiles clean.
- **Entries that change the rule set itself** (adding/removing/reordering an app) still restart the core — group selection cannot express a changed rule set, and the fingerprint gate catches exactly this and falls back to the reload path.

---

#### [7.26.59] — "Old node draining" status shown while a soft switch winds down

##### Added
- **After a restart-free node switch the dashboard shows the old node winding down.** While connections that were opened before the switch still live on the previous node, the status line reads **"eski düğüm boşalıyor (N bağlantı)"** (amber); when the last one ends it flips to **"yeni düğüm aktif · eski düğüm boşaldı"** (green); if some connections outlive the drain window it settles on **"N bağlantı eski düğümde doğal bitişi bekliyor"**.
- **`GpnDrainWatcher` tracks the drain from the live connection table.** After every successful soft switch (manual node change while connected and failover `SwitchServerAsync`) the coordinator starts a watcher that polls mihomo `GET /connections` roughly every 1.5 s and counts sessions whose `chains` still contain the old `wg-<id>`; reaching zero reports "finished", and the default 20 s drain window reports a timeout — the lingering sessions are **not** force-closed, they are left to end naturally on the old node. A newer switch, disconnect or restart cancels the watcher.

##### Technical
- New `GpnDrainSnapshot` model + `AppEvents.GpnDrainChanged` channel (`ServiceLib/Models`, `ServiceLib/Events`); `GpnDrainWatcher` under `ServiceLib/Services/Gpn/` with the project's delegate-seam test pattern; `GpnConnectionCoordinator` calls `BeginDrain(old, new)` / `CancelDrain()`. UI: `MainWindow` pushes `window.setGpnNodeSwitch(snapshot)`; `Temalar/app.js` renders the three status-line states with i18n keys in all 9 languages plus the fallback dict. Tests: `GpnDrainWatcherTests` (7 unit tests — 3→1→0 progression, timeout, cancel, retry after error, chains matching) + a dashboard jsdom integration test for the status-line texts; suite at 963/963 and dashboard JS 62/62.
- The indicator only appears on genuine mihomo soft switches (multi-node superset config with the `GPN-Nodes` group) while connected; the legacy stop→start paths keep their previous behaviour unchanged.

---

#### [7.26.58] — GPN node switching without disconnecting (make-before-break)

##### Added
- **Changing node (or a failover switch) while connected no longer tears the tunnel down.** Previously every switch path stopped the mihomo process — killing the Wintun/TUN adapter and the SOCKS listeners — and relaunched from scratch. Now, when the running config carries all candidate nodes, the switch is a single **make-before-break** step: the generated YAML lists every node as a `type: wireguard` outbound under a `GPN-Nodes` select group (the active node first, so it opens as the selection), routing rules and the `warp-socks` chain point at the group name (`dialer-proxy: GPN-Nodes`), and switching calls `PUT /proxies/GPN-Nodes` with the target node name. The process, TUN adapter and listeners stay up; new connections start on the new node immediately while connections already open remain on the old node until they end naturally ([mihomo API](https://wiki.metacubex.one/en/api/), `PUT /proxies/{name}`).
- **`GpnSoftSwitch`** verifies the group exists on the live controller and performs the API switch; it returns "unsupported" rather than throwing so callers fall back cleanly.

##### Changed
- **`GpnConnectionCoordinator` tries the soft switch first** on both paths that used to restart while connected: failover server changes (`SwitchServerAsync`, with a quick health probe of the target first when auto-switch is off) and a new connect with a different node over a live session. If the soft path is not available it falls back to the previous stop→launch behaviour, so nothing regresses for single-node or legacy configs.
- **`GpnMihomoConfigService`** produces the multi-node + `GPN-Nodes` layout whenever the coordinator passes a candidate list (single-node output unchanged); candidates are carried through `CoreConfigContext` → `CoreConfigHandler` → `GpnCoreLauncher`, which launches the multi-node config.

##### Technical
- `GpnSoftSwitch` (`ServiceLib/Services/`) reuses the existing `ClashApiManager.ClashSetActiveProxy`; external-controller (`StatePort2`) was already enabled in generated configs, so no port/protocol work was needed. Tests: 12 new unit tests (multi-node YAML with group targets, soft-switch and fallback scenarios in the coordinator); suite at 956/956, WPF app compiles clean.
- True "session migration" (carrying an open TCP session's state to another server) is impossible by design without shared server-side state — what the soft switch provides is connection-level continuity, which is what users perceive as "no disconnect".

---

#### [7.26.57] — Exit deadlock fixed: a config flush could strand the UI thread and leave AoGPN running headless

##### Fixed
- **Root cause of the lingering invisible process found and fixed.** Live trace (2026-09-03, 09:22 session): `OnExit` logged, yet the process kept running headless — its timers even fired again at +13 s and +17 s — until the 70 s exit watchdog force-killed it, with no exception and no timeout log. The cause was a sync-over-async deadlock inside the exit path: `App.OnExit` calls `HardwareAccelerationGuard.OnGracefulExit()`, which flushes the config with `.GetAwaiter().GetResult()` **on the UI thread**. `ConfigHandler.SaveConfig` writes the file with `await File.WriteAllTextAsync(...)` (no `ConfigureAwait(false)`), so when the in-flight write yielded, its continuation was posted back to the UI dispatcher — which was blocked in `GetResult` — and never ran. The write never completed, the flush never signaled, and even the 15 s flush timeout could not fire because its handler was queued on the same blocked thread. Intermittent by nature (only when the write actually yields), which is why earlier closes exited cleanly.
- Three layered fixes so the close can never deadlock again: (1) `ConfigHandler.SaveConfig` now writes with `ConfigureAwait(false)` — pure I/O that never needs the caller's SynchronizationContext; (2) the `ConfigSaveQueue` flush wait, its timeout handler and its write loop all use `ConfigureAwait(false)`, so a synchronous wait from a thread whose context never pumps always completes; (3) `HardwareAccelerationGuard.OnGracefulExit` runs the whole flush inside `Task.Run`, so the write chain executes on a context-free worker thread regardless of what `SaveConfig` does internally.

##### Technical
- New regression test `ConfigSaveQueueTests.SaveAndWaitAsync_SyncWaitFromNonPumpingContext_DoesNotDeadlock`: a synchronous `GetAwaiter().GetResult()` from a thread whose `SynchronizationContext.Post` never executes must complete — it fails cleanly (5 s join, never hangs the suite) if any await in the queue path regains `ConfigureAwait(true)`.

---

#### [7.26.56] — Dashboard window-behaviour toggles apply instantly + Minimize-to-tray control

##### Fixed
- **The dashboard title-bar X could leave the window open instead of closing the app.** The HTML close button only started the background exit steps and relied on `Application.Shutdown` (which runs after those steps) to close the window — so a slow or wedged step (each bounded at 20 s) left the window on screen as if the X did nothing. The exit branch now mirrors the native caption X: it calls `Close()` immediately (window, tray icon and WebView2 tear down at once) while the proxy/flush/core-stop steps finish in the background before shutdown, and it logs the `hide2TrayWhenClose` value at click time for diagnosis.
- **A "Hide to tray when closed" switch that looked off could still leave the app hiding on X.** The dashboard General tab only wrote its switches to the native config after the explicit **Save Settings** click, so a toggle flipped to OFF and then tested with X (without saving) still ran the previously saved behaviour — the page showed off, the app behaved as if hide-to-tray were on. The window-behaviour toggles (hide-to-tray-on-close and the new minimize-to-tray) now apply **immediately on change**: the renderer posts `set_window_behavior`, the host flips the same live config the close/minimize paths read and queues a save, then pushes the settings back so the form can never drift from the real behaviour. No Save click needed for these two.
- **The Settings form could drift from the persisted truth after a Save** (e.g. a TUN admin denial reverting `enableTun`, or a rejected value): a successful save now re-pushes the whole option set so every switch reflects the config the window behaviour actually uses.

##### Added
- **"Minimize to tray" is now visible on the dashboard General tab** (it was only present in the native options window). Pressing the minimize (—) hides the window to the tray with the connection/proxy still active; off keeps the window in the taskbar. Pushed by `applySettings`, collected by the settings form and saved by the host like every other General flag.

##### Technical
- `Temalar/app.js`: `bindWindowBehavior` posts `{ action: 'set_window_behavior', hide2TrayWhenClose, minimize2Tray }` on change of either toggle. `vpn-gpn-dashboard.html`: new `data-settings="minimize2Tray"` row with behaviour-explaining tooltips on both tray rows. `MainWindow.xaml.cs`: new `set_window_behavior` message handler (writes `UiItem.Hide2TrayWhenClose` / `UiItem.Minimize2Tray`, `ConfigSaveQueue.RequestSave`, `PushSettingsAsync`); `SaveSettingsCoreAsync` re-pushes the option set after a successful save.

---

#### [7.26.55] — Exit: X can no longer leave an invisible process behind

##### Fixed
- **A rare close (X) could leave AoGPN running invisibly in Task Manager.** The exit path closes the window first and then clears the proxy, flushes state and stops the core; a shutdown step — or the WebView2/COM teardown right after the window closes — that wedged the UI thread *synchronously* never reached its per-step timeout, so `Application.Shutdown` was never called and the process lingered with no window and no tray icon until it was killed manually. The whole exit is now guarded by a watchdog (worker thread, 70 s overall deadline): if the graceful path is still running when it fires, the watchdog sweeps app-owned core processes and force-terminates the process. A throwing shutdown escalates to the same force-exit instead of disappearing silently.
- **An `Application.Exit` handler throwing could skip the guaranteed termination.** Reproduced from a live log trace (2026-09-03): the X close ran every exit step in ~0.3 s (`proxy cleanup` 4 ms / `state flush` 168 ms / `core stop` 132 ms), `OnExit` logged — yet the process kept running headless and even fired its update check ~40 s later. An `Application.Exit` subscriber (tray library internal) had thrown, skipping the `Environment.Exit(0)` that follows `base.OnExit(e)`, and the coordinator watchdog had already been canceled after `Application.Shutdown` returned. Two fixes: `App.OnExit` now logs-and-continues when an Exit handler throws and **always** reaches `Environment.Exit(0)`; and the coordinator watchdog is **never canceled** once armed, so any process still alive `OverallExitTimeout` after the close request is force-terminated even when shutdown itself returned. New regression test `ExitApplication_WatchdogStaysArmed_WhenProcessSurvivesShutdown`.
- **Cores left by a force-killed run no longer survive into the next session.** The path-verified orphan sweep (`CoreManager.KillOrphanCoreProcesses` — only `xray`/`sing-box`/`mihomo` whose executable lives under AoGPN's own directories; foreign VPN clients are never touched) now also runs at normal GUI startup, so a previous run that crashed or was ended from Task Manager cannot keep holding the local ports/TUN from the dead session. It is skipped during reboot-as-admin, where the old elevated instance is still tearing down its own cores.

##### Changed
- **"Hide to tray when closing" stays an opt-in setting whose default is off.** Fresh configs always get `Hide2TrayWhenClose = false`, so X fully exits the app unless the user enables tray-hide in Settings. The option's description (now shown under the toggle) spells out the consequence: when enabled, X keeps AoGPN running in the background with the connection/proxy active, and only the tray menu's Exit fully quits it.

##### Technical
- `TrayWindowCoordinator`: `OverallExitTimeout` (70 s default), injectable `forceExit` (default: orphan core sweep + `Environment.Exit`), watchdog armed in `ExitApplicationAsync`; new regression tests `ExitApplication_ShutdownThrows_StillForceExits` and `ExitApplication_OverallDeadline_ForcesExitWhenExitStillRunning`.
- `CoreManager.KillOrphanCoreProcesses` made public — it now runs at core stop, at GUI startup and from the exit watchdog.
- `App.xaml.cs` (startup sweep), `ConfigHandler`/`ConfigItems` (default-off guarantee documented), `ResUI.resx` + `ResUI.tr.resx` (`TbSettingsHide2TrayWhenCloseTip` clarified and rendered under the toggle in `OptionSettingWindow.xaml`).

---

#### [7.26.54] — Theme library expanded to 25

##### Added
- **The dashboard now ships 25 themes** (up from the original 11): Nebula, Inferno, Venom, Cryo, Synthwave, Cyberpunk, Matrix, Plasma, Phantom, Crimson, Velocity, Aurora, Candy, Obsidian, Sandstorm, Neon Cyber, Titanium Orange, Matrix Green, Ocean Blue, Red Phantom, Violet Nova, Arctic Ice, Gold Elite, Stealth Camo and Crimson Core. Each theme keeps its own palette, signature effects and the animated cursor / sound / confetti engines, and is added by editing one entry in `Temalar/themes.json`.

##### Technical
- `Temalar/themes.json` is the single source for the theme list (id, name, signal, colors, rgb values, emerald accent); the dashboard renders the picker from it with a built-in fallback. No HTML or `app.js` changes are needed to add a theme.

---

#### [7.26.53] — Standalone skins: NEXUS GPN, CYBER, INFRA

##### Added
- **The dashboard can be swapped for fully independent designs called skins** — complete, self-contained HTML documents (their own markup, CSS, fonts and logic) that run inside an isolated `<iframe>`. Three ship out of the box: **NEXUS GPN**, **CYBER** and **INFRA**. A skin is *not* a color theme and never touches the main document — it reads live app state and posts actions exclusively through a safe `skinBridge` (live getters, setters, actions, subscribe), and falls back to a small demo state when opened directly in a browser.
- **Skin picker** in the top-bar theme popover, rendered from `Temalar/skins.json`; the active skin is persisted (`aogpn.skin`) and applied pre-paint so there is no flash.

##### Technical
- `Temalar/skins/<id>/skin.html` (+ optional `skin.css` / `skin.js`) per skin; `app.js` `applySkin()` shows/hides the hidden `#skinHost` iframe and mirrors `body[data-skin]`. Tests under `Temalar/skins/*.test.js` (skin pairs, bridge i18n, and NEXUS/CYBER/INFRA integration). New skins are registered by one JSON entry — the `Temalar/**` content glob ships them with no csproj change.

---

#### [7.26.52] — Real game-server ping: before/after on the boost cards

##### Added
- **The Game Boost cards now show the REAL game-server ping, measured before and after the GPN tunnel.** While a GPN-routed app is running, the public servers it connects to (real game-server IP:port pairs from the live connection monitor) are saved to a new `gpn_app_endpoints` database table (upserted per session, pruned after 45 days). At program startup those endpoints are pinged on the direct path — the **before** value — and again a few seconds after the GPN connection is established, through the tunnel — the **after** value. The boost card shows `before → after (delta)`, e.g. `42 ms → 18 ms (-24 ms)`, with the delta color-coded (green improvement / red regression). The big latency value now prefers the real server measurement instead of the active-node ping, so the number reflects the actual game server.
- **Measurement is best-effort and self-healing**: ICMP round-trip is used first (with DNS resolution), falling back to a TCP connect delay for TCP endpoints that do not answer ICMP; unmeasured endpoints stay "—". Endpoints with no data yet simply show nothing — the table fills in after the first GPN session with the game running.

##### Technical
- `ServiceLib/Models/Entities/GpnAppEndpointItem.cs`: new `gpn_app_endpoints` entity (AppName, Endpoint, Protocol, FirstSeenAt, LastSeenAt, HitCount; unique (AppName, Endpoint) index). Registered in `AppManager.InitApp`.
- `ServiceLib/Services/GpnAppEndpointStore.cs`: new store — `Observe` (in-memory dedupe), periodic `FlushAsync` (single upsert per endpoint, SQL-inlined literals), `LoadForAppsAsync` (case-insensitive `LOWER(IN ...)` lookup) and `MeasureRealPingAsync` (per-app best RTT across the top-N endpoints via `NodePingCoordinator`, ICMP → TCP-connect fallback; injectable probe for tests).
- `ServiceLib/ViewModels/SplitTunnelViewModel.cs`: the 3 s live-status loop now feeds public remote endpoints of effective-tunneled apps (same target rule as `GpnTargetResolverBridge.ExtractTargetNames`) into the store — recording happens only for apps added to GPN.
- `ServiceLib/Models/Dto/SplitTunnelAppItem.cs`: new `BeforePingMs/BeforePingText`, `AfterPingMs/AfterPingText`, `PingDeltaText` reactive fields.
- `AoGPN/Views/MainWindow.xaml.cs`: `MeasureRealPingAsync(isBefore)` runs at dashboard startup (after a short delay) and on `GpnConnectionState.Connected`; results are written onto the app rows and pushed in the monitor snapshot (`beforePingText` / `afterPingText` / `pingDeltaText`).
- `Temalar/app.js`: `renderDashboardBoostCards` renders the before/after line and delta; new i18n keys `boost.before` / `boost.after` in all 9 language files plus the fallback dict.
- Tests: `ServiceLib.Tests/Services/GpnAppEndpointStoreTests.cs` (9 tests: observe→flush upsert + hit-count accumulation, load filtering, probe aggregation, max-endpoints cap, endpoint parsing, SQL escaping); `Temalar/skins/dashboard.integration.test.js` +2 boost-card tests (before/after+delta render, before-only fallback).

---

#### [7.26.51] — Game Boost spells out the blacklist inversion inline

##### Added
- **In GPN blacklist direction, the Game Boost route column now names what each assignment actually does.** The old generic `⇄ BLACKLIST` marker on every row is replaced with a per-row chip: a `VPN` assignment (the common "exclude" case) shows an amber **⇄ Outside tunnel** chip with a tooltip explaining the app stays direct, and a `Direct` assignment (inverted to tunnel) shows a cyan **⇄ Tunneled** chip; `Block` rows carry no chip because they are not inverted. The route `<select>` labels spell out the same meaning in blacklist mode ("VPN · outside tunnel" / "Direct · tunneled") while the values sent to the host stay the real assignment — the flip remains display-only.
- **The blacklist banner above the table now states the inversion explicitly** ("A 'VPN' assignment keeps an app OUTSIDE the tunnel; 'Direct' tunnels it") instead of only the general "assigned apps stay direct" summary.

##### Technical
- `Temalar/app.js`: `renderSplitApps` emits the direction-specific chip (tooltip via new `dir.tipRowExcluded` / `dir.tipRowTunneled`); `routeSelect` picks direction-aware option labels through `blacklistActive()`. New i18n keys `dir.rowExcluded/rowTunneled/selectVpn/selectDirect/selectBlock/tipRowExcluded/tipRowTunneled` in all 9 language files plus the fallback dict; `dir.blacklistTableHint` updated everywhere. `Temalar/skins/dashboard.integration.test.js`: new kill-switch UI test asserting the chips, the direction-aware dropdown labels, the unchanged select values and the reverted whitelist state. All 184 dashboard/skin JS tests pass.

---

#### [7.26.50] — GPN blacklist: excluded apps no longer tunneled by the capture bridge

##### Fixed
- **In GPN blacklist (exclude) direction, excluded apps still connected through the VPN — the same connection as Global VPN.** The Phase-2 capture bridge (`GpnCaptureBridge` → WinDivert → WireGuard/Wintun inject) collected its target processes from `GpnTargetResolverBridge.ExtractTargetNames`, which unconditionally took every manual-list entry whose action was `"vpn"`. In blacklist direction a listed app with a VPN action is the *exception* that the routing rules invert to **direct** — but the capture bridge intercepted those very processes at the WinDivert layer and pushed their packets through the WireGuard tunnel anyway, bypassing the split rules entirely. The target set now follows the same effective-route logic as `ManualRoutingRules`: whitelist targets `vpn`-actioned entries, blacklist targets `direct`-actioned entries (the ones the rules invert to tunnel) and never captures the excluded `vpn` entries. When a blacklist contains only excluded apps, the bridge does not start at all and the sing-box TUN rules alone decide the routes.
- The direction is read live at every 5 s pool refresh and at connect time (`MainWindowViewModel.GetGpnTargetNames` passes `ConnectionViewModel.InvertManualRouting`), so flipping Whitelist/Blacklist while connected recomputes the captured PID pool on the next cycle.
- **When the TUN adapter cannot be created (VMware/Hyper-V guests etc.), the fallback no longer silently ignores the split rules.** The core used to restart in Global VPN mode keeping the app-managed process rules in the config — but without the TUN inbound there is no PID attribution through the SOCKS/proxy path, so those rules could never match: a GPN blacklist's excluded apps were silently tunneled by the proxy catch-all (Global-VPN behaviour), and a whitelist's direct catch-all silently leaked every unlisted connection. The fallback now strips the app-managed rules (new `ManualRoutingRules.StripManagedRules`, shared with `SystemProxyOnlyService.ToProxyOnlyContext`) so `route.final = proxy` governs an honest Global VPN, preserves user-defined rules, and shows a notice that GPN split tunneling is inactive because TUN is unavailable.

##### Technical
- `ServiceLib/Services/Gpn/GpnTargetResolverBridge.cs`: `ExtractTargetNames(apps, invertManual)` + a live `Func<bool>` direction provider threaded through both constructors; `ComputeTargetNames` passes the current direction. `ServiceLib/ViewModels/MainWindowViewModel.cs`: `GetGpnTargetNames` forwards `InvertManualRouting`. `ServiceLib/Common/ManualRoutingRules.cs`: new pure `StripManagedRules(RoutingItem?)` used by both the proxy-only service and the CoreManager TUN-missing fallback. `ServiceLib/Manager/CoreManager.cs`: the fallback builds its Global-VPN context through `SystemProxyOnlyService.ToProxyOnlyContext` (managed rules dropped, user rules kept) and notifies the user that GPN split is disabled. Tests: `GpnTargetResolverBridgeTests` +3 unit tests; `ManualRoutingRulesTests` +3 `StripManagedRules` tests; `GpnCaptureFlowIntegrationTests` (new) — 5 integration tests round-tripping `ManualRoutingRules` through the real sing-box config generator (capture-bridge target set equals the config's tunneled process set for **both** directions, and the TUN-fallback config for blacklist and whitelist has no dead process rules and `route.final = proxy`). All 316 GPN/routing/proxy tests pass.

---

#### [7.26.49] — Splash screen: fade-out, version and loading bar

##### Added
- **The splash screen now carries a version number and a loading bar.** The
  boot progress fills as start-up advances (config → fonts → components →
  reactive UI → main window) with matching status text, and the app version
  (e.g. `V1.0.0`) is shown at the bottom. `SplashWindow.SetStage` pumps a
  background-priority dispatcher frame after each update so the bar actually
  repaints during the synchronous start-up work.

##### Changed
- **The splash no longer auto-closes — it fades out smoothly.** The WPF
  `SplashScreen` build action was replaced by a dedicated frameless
  `SplashWindow` (kept on top, `Resources/Splash.png` embedded as a plain
  resource) whose `FadeOut()` animates opacity down over 450 ms with a
  cubic ease before closing, right as the main window comes up. Error paths
  (dispatcher unhandled exception before start-up completes) close it
  instantly so the error dialog is never covered.
- **The splash holds on screen at least ~2.2 s** so boot can never flash it
  past in milliseconds: even when the main window is ready almost
  immediately, `FadeOut()` waits out the remaining minimum-display time with
  the completed loading bar visible before starting the fade. Error paths
  still drop it instantly so the error dialog is never covered.
- **The splash shows just the transparent high-res logo png** — the window is
  now fully transparent (`AllowsTransparency`, no navy backdrop or bordered
  card); only the logo artwork renders, with the status text, gradient loading
  bar and version line floating beneath it over the desktop.

---

#### [7.26.48] — Splash screen with the high-resolution AO GPN logo

##### Added
- **A startup splash screen now shows the high-resolution AO GPN logo** while
  the app boots. `Resources/Splash.png` is a 640×440 image generated from the
  transparent high-res `Temalar/icon/AO-GPN-logo.png` (dark navy background
  matching the app theme, logo centered), registered as a WPF `SplashScreen`
  build resource so it appears immediately at launch and auto-closes when the
  main window first renders. Rebuilding embeds it into the assembly.

##### Changed
- All user-visible product branding now reads **"AO GPN"** (previously
  "AoGPN"): main window title, sidebar brand text, tray tooltip, WebView2
  error dialog, terminal view header, footer brand and the settings subtitle
  across all nine language dictionaries.

---

#### [7.26.47] — Real AoGPN logo inside the dashboard branding

##### Changed
- **The dashboard title bar and sidebar now render the actual AoGPN logo image
  instead of a generic inline shield glyph.** The small square next to the
  window's "AoGPN" title and the larger mark in the sidebar brand block each
  embed `Temalar/icon/convertico-AOGPN_128x128.png` (the new app logo),
  matching the icon used for the executable, window and tray. The NEXUS skin's
  sidebar logo mark is also replaced with the same logo image instead of its
  placeholder "N" letter. The logo ships with the dashboard via the existing
  `Temalar\**` content glob, so no new packaging is needed.

##### Fixed
- **The brand logos no longer sit on a gradient/coloured chip.** The previous
  placement wrapped the image in a violet→cyan gradient box, so the logo looked
  tiny and carried an unwanted blue-purple background. The chips are gone — the
  transparent PNG now renders standalone and larger (28 px in the title bar,
  64 px in the sidebar brand, 56 px in the NEXUS skin) with only a soft
  violet glow, matching the logo's own transparent edges.

---

#### [7.26.46] — New app logo across the icon set

##### Changed
- **The application icon, window icon, status-bar icon and all four tray state
  icons now use the new AoGPN logo** (`Temalar/icon`). The main icon ships the
  full multi-resolution set (16–256 px), and the tray keeps its state color
  coding by tinting the logo blue-violet (default), red, purple and yellow for
  the four system-proxy states, so the tray still communicates the current mode
  at a glance.

---

#### [7.26.45] — Stable node ping tests + sort by ping

##### Fixed
- **A second "Test all" no longer cancels itself the moment it starts.** The
  speed-test exit-loop registry was a single static bag shared by every
  SpeedtestService instance, and a finished run left its key behind forever. The
  next run's setup therefore saw a "run still active" and immediately pushed a
  stop event to the dashboard, which cleared the fresh run's state — so pressing
  the test button again after a first run appeared to cancel instantly and never
  showed results. The registry is now per-service-instance and each run removes
  its key when it finishes, so an idle service stays silent and one view's stop
  no longer cancels the other view's speedtest.
- **Accidental double-clicks no longer stop a test that just began.** Clicking
  "Test all" twice quickly hit the stop branch on the second click and cancelled
  the run; clicks inside the start-cooldown window are now ignored.
- **A node that never answers now shows as broken instead of silently keeping a
  stale value.** When a run ends (stopped, skipped or errored) any node still
  mid-test is marked failed, so unresponsive nodes read "✗ başarısız" rather than
  hiding behind their last known ping.
- **Ping values survive a stopped run.** Partial results are persisted to the
  profile-extension table when a run is stopped or errors, not only on clean
  completion, so previously measured delays are kept and re-testing refreshes
  them.
- **Huge "Test all" batches are chunked.** Tcping/UDP tests fired one socket per
  node for the whole list at once (page size defaults to 1000), exhausting
  ephemeral ports and DNS lookups so many nodes timed out and falsely reported as
  failed; parallelism is now capped at 64 per batch.

##### Added
- **Sort by ping.** The Nodes view sort dropdown gains "Ping: low to high" and
  "Ping: high to low"; nodes without a measured ping (untested/failed) always
  sort to the bottom.

---

#### [7.26.44] — Tray: left click always brings the window back

##### Fixed
- **Tray left click no longer hides the window when it is merely minimized.** The
  show/hide toggle decided from a cached "visible" flag, which desyncs when the
  window is minimized without hiding to the tray (e.g. minimize-to-tray disabled):
  a tray click then HID the already-minimized window again, so the window seemed
  to never come back. The toggle now derives from the window's live state — any
  minimized window (taskbar or tray) is restored, only a fully visible window is
  hidden.
- **Tray left click is delivered synchronously instead of through LeftClickCommand.**
  H.NotifyIcon raises `LeftClickCommand` from a `System.Threading.Timer` +
  `Dispatcher.Invoke` chain that silently died in this environment (elevated app,
  Windows Efficiency Mode), so every left click reached the icon but the window
  never came back. The left click is now wired to `TaskbarIcon.TrayLeftMouseDown`,
  which is raised once per shell callback on the same delivery path as the
  (previously working) right-click menu. This was confirmed by instrumenting the
  command chain in a real build: the click event arrived but the earlier
  double-click suppression discarded it as a "trailing action", so that logic was
  removed entirely.
- **No more ghost tray icons stacking on exit.** Only the tray menu's Exit item
  removed the shell icon; exits through the HTML close button, shutdown or session
  ending left it registered, so each restart added a dead icon and a click on an
  old icon went nowhere. Every close path now disposes the icon
  (`MainWindow_Closed`), and the icon uses a fixed `Id` instead of a random GUID
  so Windows can reconcile the old and new registrations.

##### Changed
- **The tray menu was rebuilt around the app's real concepts and theme system.**
  The old system-proxy jargon rows (clear/set/unchanged/PAC) were replaced with
  the app's actual routing controls, and the menu is now grouped into themed
  sections (Connection / Connection mode / Management):
  - **Quick connect / disconnect** (⚡): one click toggles the connection through
    the same gated path as the dashboard button — it can never bypass the
    connection gate, TUN elevation check or persisted routing rules. The label
    flips to "Disconnect" while connected.
  - **Test connection** (📶): runs the real server availability check.
  - **TUN (VPN)** (🛡): checkable toggle bound to the live TUN setting.
  - **Connection mode** radio group (Kapalı / VPN / Manuel): radio-style dots
    tinted with the theme accent, refreshed from the persisted mode on every menu
    open; switching routes through the same `SetDashboardModeAsync` path as the
    dashboard, so it works whether connected or disconnected.
  - Server and routing pickers, import-from-clipboard, QR scan and subscription
    updates are kept under a "Management" header; the copy action was relabeled
    to "Copy connection info" and the window controls are now explicit
    "Show window" / "Hide window" items. Every visual uses theme brushes
    (`DynamicResource`) so the menu matches the active dark/light palette.

##### Technical
- `TrayWindowCoordinator.ShouldShowOnToggle(isInTaskbar, isMinimized)`:
  state-derived toggle decision, used by `MainWindow.ShowHideWindow`.
- `StatusBarView`: `TrayLeftMouseDown` handler in the constructor (independent of
  ReactiveUI activation) calls `StatusBarViewModel.NotifyLeftClickCmd`, which
  toggles on every click without suppression; tray menu restyled; connection-mode
  dots driven by the persisted mode and re-resolved on `AppEvents.ThemeChanged`;
  quick-connect label follows the live `TrayStatusState`.
- `StatusBarViewModel`: new `ToggleConnectionRequested` and
  `SetConnectionModeRequested` event channels plus `QuickConnectCmd`,
  `TestServerCmd` and `ConnectionModeOff/Vpn/ManualCmd` commands.
- `MainWindow`: subscribes the new tray channels in the constructor and routes
  them through the existing `ToggleConnectionAsync` / `SetDashboardModeAsync`
  paths (`ToggleConnectionFromTrayAsync`); tray icon disposed on every exit path.
- `MainWindow.ShowHideWindow`: toggle decided from live window state.
- `TrayWindowCoordinatorTests`: regression tests for the toggle decision.

---

#### [7.26.44] — Foreign VPN clients are no longer auto-killed

##### Changed
- **AoGPN no longer terminates third-party VPN clients.** The auto-kill feature
  (`ForeignTunnelDetector.KillForeignClients` / `ResolveForeignClientsAsync`) that
  force-closed well-known VPN applications — WireGuard for Windows (`wireguard.exe`),
  v2rayN, Nekoray, OpenVPN, Tailscale, ... — before AoGPN opened its own tunnel has
  been removed entirely. `ForeignTunnelDetector` is now detection/reporting only: it
  still checks for a foreign TUN adapter, a foreign listener on the local proxy port
  and running third-party VPN clients, and the pre-connect warning
  (`ForeignTunnelWarning`) still tells the user to close the other VPN first (two TUN
  stacks at once break the internet). Closing another VPN is the user's decision,
  never the app's.
- **GPN flows report instead of killing.** `GpnCoreLauncher` (mihomo TUN) and
  `GpnCaptureBridge` (WinDivert bridge) now only detect foreign VPN state and write
  it to the diagnostic log before opening the tunnel; the connect flow is never
  blocked.

##### Removed
- `ForeignTunnelDetector.KillForeignClients`, `ResolveForeignClientsAsync` and the
  injectable `killForeignProcesses` delegate.
- Auto-kill + re-scan + "clients auto-closed" success notice
  (`ResUI.ForeignTunnelAutoKilled`) from `MainWindow.WarnOnForeignTunnelBeforeConnect`
  (previously `...Async`).
- `GpnCoreLauncher.CloseForeignTunnelsBeforeStartAsync` → replaced by
  `DetectForeignTunnelsBeforeStart` (reporting only).
- `GpnCaptureBridge.ResolveForeignTunnelBeforeConnectAsync` → replaced by
  `DetectForeignTunnelBeforeConnect` (reporting only).

##### Technical
- `ForeignTunnelDetectorTests`: kill tests removed; added
  `Detector_ExposesNoKillOrResolveApi` (structural regression guard: fails if any
  `Kill*`/`Resolve*` method is ever re-added) and
  `Detect_ForeignProcess_ReportsItWithoutKilling`.
- `GpnCaptureBridgeTests`: `Start_ForeignWireGuardRunning_KilledBeforeTunnelOpen` →
  `Start_ForeignWireGuardRunning_NotKilled_TunnelStillOpens`; clean-path test
  simplified.
- Design rationale + full inventory: `docs/foreign-vpn-kill-removal-roadmap.md`.

---

#### [7.26.43] — Tray: app can no longer vanish into a hidden, icon-less state

##### Fixed
- **"Auto hide on startup" + "Minimize to tray" no longer make the app unreachable.**
  With both toggles on, the window used to be hidden from the taskbar *before* the
  tray icon existed: H.NotifyIcon cannot register the shell icon while the window
  is minimized, so the result was a running process with no window, no taskbar
  button and no tray icon. Startup now defers the hide to the Loaded priority,
  force-creates the native tray icon (`TaskbarIcon.ForceCreate`), and only then
  hides the window. The startup minimize is guarded so it cannot race ahead of
  the icon creation.
- **Minimize-to-tray no longer depends on the tray notification.** The one-time
  tray hint used to run *before* the hide; when the native icon was not ready it
  threw "TrayIcon is not created", which propagated out of `HandleMinimize` and
  skipped the hide entirely (window stranded minimized in the taskbar). The window
  is now hidden first, the hint is best-effort (never throws), and it is retried
  on a later minimize if it failed — it is only marked "shown" after it succeeds.

##### Technical
- `MainWindow.xaml.cs`: `_startupTrayPending` guard + `OnLoaded` deferred
  transition + `TryCreateTrayIcon()`; `ShowTrayMinimizeHint` wrapped in try/catch.
- `TrayWindowCoordinator.HandleMinimize`: hide-first ordering, hint exceptions
  contained, hint marked shown only on success.
- `TrayWindowCoordinatorTests`: new regression test proving the hide runs even
  when the hint throws, and that a failed hint is retried later.

---

#### [7.26.42] — Core orphan guard, resilient exit chain, verified downloads

##### Fixed
- **Orphan core cleanup no longer kills other VPN clients.** `KillOrphanCoreProcesses`
  matched `xray`/`sing-box`/`mihomo` by process name alone, so stopping or reloading
  AoGPN killed every identically-named process on the system — including the cores of
  v2rayN, Nekoray or standalone sing-box running alongside. The cleanup now only
  touches processes whose executable lives under AoGPN's own directories (StartupPath
  or the base directory, with a directory-boundary match); a process whose executable
  path cannot be read is left untouched. Covered by 6 new unit tests.
- **Shutdown no longer skips steps silently.** `AppExitAsync` wrapped the whole exit
  sequence in one `try/catch { }`, so a failure in the system-proxy clear skipped the
  config/DB flush and the core stop, and the error was swallowed. Each exit step now
  runs in its own isolated best-effort step and failures are logged by name.

##### Changed
- **Core, app and geo downloads are now SHA-256 verified.** `UpdateService` fetches
  GitHub's auto-generated `<asset>.sha256sum` and refuses to apply an executable
  payload (cores, app update) whose hash does not match — the file is discarded and a
  clear message is shown. Geo/rule-set data files are verified whenever a checksum is
  published and accepted with a warning when the source is a raw URL without one.
  Verification runs before the unpack/install signal, so the installer can never
  receive an unverified archive. Checksum parsing covered by 10 new unit tests.
  (Note: this guards against corrupted/tampered downloads; it is not a substitute for
  GPG release signatures.)

---

#### [7.26.41] — Node list batch actions & toolbar de-duplication

##### Fixed
- **Batch delete now works from the multi-select bar.** Deleting/"Disabling"
  several nodes selected with Ctrl+click no longer gets swallowed by the
  "edit mode kapalı" guard — the selection toolbar's Delete/Disable (and the
  Delete key when a selection exists) run immediately, each behind its own
  confirmation dialog.
- The dashboard **shows one editing surface at a time**: the bulk-cleanup toolbar
  (Remove duplicates / Delete failed / Disable failed) is hidden while a
  selection is active, so the two toolbars no longer stack into confusing
  duplicate options. Bulk list cleanup still requires Edit mode; the explicit
  selection Delete/Disable always works.
- **Delete-key safety:** pressing Delete with *no* selection no longer drops the
  active node by accident — that fallback is restricted to Edit mode, and a hint
  tells you to select nodes first otherwise.

---

#### [7.26.40] — User-approved Xray core fallback for REALITY nodes

##### Changed
- The REALITY core fallback no longer switches a failed node to the Xray core
  silently. `RealityCoreFallbackAdvisor`'s `SwitchToXray` decision now surfaces a
  snackbar notification with an explicit **Switch to Xray** action button, and the
  switch (node core-type change + persist + reconnect) runs only when the user
  clicks it. Dismissing the notification leaves the node untouched.
- `NoticeManager` gained action-supporting notifications: a new `ActionNotice`
  payload (`Content`, `ActionContent`, `Handler`) delivered through the new
  `AppEvents.SendSnackActionRequested` channel; `EnqueueAction` publishes it to
  the snackbar with the action button wired to the handler.

##### Added
- `NoticeManagerActionTests`: verifies `EnqueueAction` publishes the content,
  action label and handler, and that empty content or a null handler publishes
  nothing.

---

#### [7.26.39] — Foreign VPN conflict warning before connecting

##### Added
- **`ForeignTunnelDetector`**: before AoGPN starts its own tunnel it checks for
  foreign VPN state that would collide with it — a TUN adapter owned by another
  client (v2rayN's `xray_tun`, wintun-based VPNs; AoGPN's own `singbox_tun` is
  excluded) and a foreign listener already bound to the local proxy port (skipped
  when AoGPN's own core is the listener). On conflict the user gets a warning
  telling them to close v2rayN first, since two TUN stacks at once kill the
  internet and a busy port prevents the core from binding. Fully unit-tested
  (7 tests).

---

#### [7.26.38] — Automatic core fallback for REALITY nodes

##### Added
- **`RealityCoreFallbackAdvisor`**: when the main core fails and the active node is a
  REALITY node that ran on the sing-box core (its hardcoded 1.8.1 REALITY handshake
  claim is rejected by modern 3x-ui/Xray servers), the app now automatically switches
  the node to the Xray core and retries the connection when TUN is off. With TUN
  enabled the switch is impossible (Xray has no TUN support on Windows here), so a
  notice explains the situation instead. The advice fires once per node per TUN state
  and is fully unit-tested (10 tests).

---

#### [7.26.37] — Bundled Xray upgraded to 26.7.28 + REALITY regression test

##### Changed
- **Bundled Xray core upgraded 26.3.27 → 26.7.28** (both Debug and Release build
  outputs). The REALITY handshake embeds the client's compiled-in version
  (`core.Version_x/y/z`); 26.7.28 claims a newer client version than v2rayN's
  known-working 26.6.1 build and matches the user's 3x-ui/Xray 26.7.28 server, so
  it passes every `minClientVer` gate v2rayN passes. Note: with TUN enabled the
  main core is still sing-box (its REALITY client hardcodes a 1.8.1 claim), so the
  Xray upgrade applies to non-TUN/proxy-only setups and per-node Xray selection.

##### Added
- **`XrayBinaryConfigValidationTests`**: generates the real Almanya VLESS+REALITY
  node config through `CoreConfigV2rayService` and validates it against the
  bundled Xray binary via `xray run -test`, asserting the REALITY identity fields
  (publicKey, shortId, serverName, fingerprint, spiderX) survive config
  generation unchanged. Skips when no binary is present in the build output.

---

#### [7.26.36] — Proxy-only egress verification script

##### Added
- **`verify-proxy-only-egress.ps1`**: end-to-end verification scenario for the
  proxy-only mode. Checks preconditions (proxy preference, OS system-proxy
  registry entry, running core under the app dir, listening local port), runs
  connection tests (direct vs HTTP vs SOCKS5 vs a real WinINET client that honours
  the system proxy) and verifies the core access log shows the test requests
  routed through the `proxy` outbound. Surfaces REALITY/handshake failures from
  the core error log with a fix hint.

---

#### [7.26.35] — Tray-hide invariant guarded by tests

##### Added
- **`TrayWindowCoordinator`**: MainWindow now routes every minimize/close-to-tray
  decision through a single guard whose hide paths only hide the window (plus the
  one-time tray hint) and can never stop the core or clear the OS proxy — only the
  exit path may touch the lifecycle. `AppManager.FlushStateAsync` extracted from
  `AppExitAsync` so the exit sequence (clear proxy → flush → stop core → shutdown)
  stays identical to before.
- **Focused unit tests** (`TrayWindowCoordinatorTests`, 9 tests) asserting
  minimize-to-tray and close-to-tray never invoke `CoreEngineHost.StopAsync` or
  `SysProxyHandler.UpdateSysProxy(clear)` (counter-based fakes), the hint is
  one-time per session, the disabled setting is a no-op, and the exit path is the
  only place the lifecycle is invoked.

---

#### [7.26.34] — Live tray status line

##### Added
- **Live status line at the top of the tray menu** that reflects the actual
  runtime state and updates every 2 s (plus immediately on proxy/connection
  transitions): a green dot with "Connection active", an amber dot with
  "Proxy-only active", or a grey dot with "Not connected". The status stays
  truthful even while the window is hidden to the tray.

---

#### [7.26.33] — Tray exit vs close distinction

##### Fixed
- **Tray "Exit" no longer leaves a hidden zombie process** when
  `Hide2TrayWhenClose` is on. `Application.Shutdown` marks the window as
  allow-close, so `MainWindow_Closing` can't swallow a real exit into a tray-hide
  (previously the core was stopped and the proxy cleared, but the app stayed alive
  hidden with the tray icon already disposed).

##### Added
- **Tray "Close" menu item** (minimize icon) that only hides the window to the
  tray while the core and system proxy keep running — visually and functionally
  distinct from tray "Exit" (power icon), which stops the core and clears the
  system proxy before quitting.

---

#### [7.26.32] — Proxy-only service testability

##### Changed
- **`SystemProxyOnlyService` moved from `AoGPN` into `ServiceLib`** and given an
  injectable seam (node resolver, context builder, core-host factory, OS-proxy
  applier) so its orchestration can be tested without SQLite, a live core process
  or OS registry writes.
- **`ToProxyOnlyContext` is now `internal static`** and covered by unit tests that
  assert managed routing rules (per-app entries + mode catch-alls + UDP QUIC block)
  are dropped while user rules and routing metadata are preserved, and that TUN and
  the core-protection list are cleared.
- `ServiceLib.csproj`: `InternalsVisibleTo ServiceLib.Tests`.

##### Added
- `ServiceLib.Tests/Services/SystemProxyOnlyServiceTests.cs`: 22 tests covering
  `ShouldRun` decisions (proxy preference × connection state), routing cleanup,
  reconcile idempotency, missing-node/validation/OpenVPN rejection, ownership
  release, and node-change restart behavior.

---

#### [7.26.31] — Minimize-to-tray and network-aware auto-start

##### Added
- **Minimize to tray.** A new "Minimize to tray" toggle (native settings and the
  dashboard General panel) hides the window to the tray instead of leaving a
  taskbar button when it is minimized.
- **Close-to-tray is now opt-in.** Closing the window only hides to the tray when
  "Hide to tray when closed" is on; otherwise it performs a real exit (the native
  settings window now also exposes the previously dashboard-only toggle).
- **Network-aware auto-start.** When "Auto run on startup" is on, the app waits for
  the OS to report a network before starting the proxy-only core, so the system
  proxy comes up on a live connection instead of an unreachable listener.

##### Technical
- `ConfigItems.cs`: `UIItem.Minimize2Tray`. `OptionSettingViewModel`: property +
  load/save. `OptionSettingWindow.xaml`/`.cs`: two new toggles (rows 11–12).
- `MainWindow.xaml.cs`: `StateChanged` minimize-to-tray, `MainWindow_Closing` honors
  `Hide2TrayWhenClose`, `minimize2Tray` in settings push/pull, `WaitForNetworkAsync`
  before proxy-only reconcile when auto-run is on.
- `Utils.cs`: `WaitForNetworkAsync` helper. `vpn-gpn-dashboard.html`: dashboard
  toggle. `ResUI.resx`/`tr.resx`/`Designer.cs`: `TbSettingsMinimize2Tray` key.

---

#### [7.26.30] — One edit control: remove the duplicate "Edit mode" label

##### Changed
- **The Nodes view has a single edit affordance again.** The header button now
  reads **Edit** (pencil) when off and **Done** (checkmark) when on, and the
  edit-mode toolbar dropped its redundant "Edit mode" label + icon — it now
  shows only the three destructive actions. Two competing "Edit"-looking
  controls caused the confusion; there is exactly one toggle now.
- **The node-pool row menu item is now "Edit link"** instead of "Edit", so it
  is unambiguous against the node-list edit mode.

##### Technical
- `AoGPN/vpn-gpn-dashboard.html`: `nodeEditBtn` gained a second (checkmark)
  icon and a labeled span; `nodeEditBar` lost the label block.
  `AoGPN/Temalar/app.js`: `syncEditMode` swaps icon + label (`nodes.edit` /
  `nodes.editDone`). `AoGPN/Dil/en.json` + `tr.json`: new keys, and
  `nodes.poolEdit` reworded to "Edit link" / "Bağlantıyı düzenle".

---

#### [7.26.29] — System proxy works without a tunnel (v2rayN-style proxy-only mode)

##### Added
- **The system proxy now applies even when no GPN/Global tunnel is active.**
  Setting PROXY to Set/PAC while disconnected starts a minimal proxy-only
  core: local SOCKS/HTTP inbounds with the selected node as the outbound and
  no TUN, no per-app routing. The OS proxy points at a live listener, so
  apps that honour the system proxy route through the node immediately —
  the v2rayN behaviour — and the dashboard still reports "disconnected".
- **Fully independent of the tunnel lifecycle.** The proxy-only core starts
  automatically when Set/PAC is chosen (and on app launch if the saved
  preference is Set/PAC), stops when Clear/Unchanged is chosen, and resumes
  after a disconnect. While it runs, picking a different node re-routes the
  core instead of leaving the old node active.
- **No conflicts with GPN/Global.** The existing safety rule is kept: when a
  TUN connection activates, the OS proxy is forced off (prevents leaks and
  broken process-name matching) and the Set/PAC preference is restored when
  the tunnel disconnects. A proxy-transport connection simply takes the
  listener over (badge flips to PROXY · CONNECTION). The dashboard badge
  now reads **PROXY ONLY** when the proxy is on without a tunnel.

##### Fixed
- **PROXY ON without a tunnel pointed at a dead port.** The OS proxy was
  written to 127.0.0.1:10808 while nothing listened (the listener only
  existed while the core ran), breaking apps that honour the proxy. The
  port-liveness guard in `SysProxyHandler` never fired because the port
  lookup is config-based, so the bad setting was applied. Proxy-only mode
  guarantees a listener before the OS proxy is applied.

##### Technical
- `AoGPN/AoGPN/Services/SystemProxyOnlyService.cs` (new): builds a
  TUN-free context (`CoreConfigContextBuilder.Build` + routing item stripped
  of app-managed rules so `route.final = proxy` is not shadowed), starts it
  through `CoreEngineHost`, and reconciles on proxy-mode changes, connection
  toggles, node switches and startup. `ReleaseOwnership()` is called before
  a real connection reload so the service never stops the connection's core.
- `AoGPN/AoGPN/Views/MainWindow.xaml.cs`: wired the service into
  `SetSystemProxyModeAsync`, `ApplyConnectionModeAsync`, `SelectNodeAsync`
  and the dashboard-ready startup path.
- `AoGPN/Temalar/app.js`: the system-proxy badge shows **PROXY ONLY** when
  the proxy is effective but no tunnel is connected.

---

#### [7.26.28] — Node pool: edit links behind the view's edit mode

##### Added
- **Pooled links can now be edited in place.** Clicking **Edit** in the row
  menu loads the URL into the pool input, selects it, highlights the row and
  switches the button to **Update link** (with a **Cancel** next to it).
  Enter or the button sends the renamed link to the host, which validates it
  like an add (http/https, no duplicates) and republishes the list. Escape or
  Cancel drops the edit and returns to add mode.
- **Pool rows are read-only until the view's edit mode is on.** Instead of
  always-visible per-row buttons, each row shows a single **⋯** manage option
  that only appears while edit mode is active (the same **Edit** button that
  gates node delete/disable). The menu holds both actions — **Edit link** and
  **Remove** — so the rows never carry double buttons, and editing/removal is
  impossible unless edit mode is explicitly opened.
- Long URLs expose their full text on hover (`title` tooltip) since the row
  truncates.

##### Technical
- `AoGPN/ServiceLib/Common/DashboardMessagePolicy.cs`: new `edit_node_pool_link`
  action. `AoGPN/AoGPN/Views/MainWindow.xaml.cs`: `EditNodePoolLinkAsync`
  (same validation path as add, case-insensitive replace, no duplicate
  new URLs). `AoGPN/Temalar/app.js`: `setPoolEditMode()` drives the
  input/button/cancel state, `syncEditMode()` re-renders the pool so the ⋯
  rows appear/disappear with edit mode, and a `poolCtxMenu` popover hosts
  the single manage option. New i18n keys `nodes.poolEdit/poolUpdate/poolCancel/
  poolManage` in the fallback dictionary, `Dil/en.json` and `Dil/tr.json`.
  `AoGPN/vpn-gpn-dashboard.html`: the pool row popover markup.

---

#### [7.26.27] — Nodes view: clipboard shortcuts stop fighting text inputs, selection fixes

##### Fixed
- **Ctrl+C / Ctrl+V / Ctrl+A / Delete / Backspace no longer hijack text
  inputs in the Nodes view.** The node shortcuts were bound on `document`
  without checking where focus was, so pasting a link into the Node pool URL
  field ran the node **paste** import (or copied nodes instead of the text),
  Backspace triggered the "Edit mode is off" toast (or deleted the active
  node with edit mode on), and Escape cleared the selection instead of
  editing the field. The handler now lets inputs, textareas and
  contenteditable elements own those keys; the shortcuts only act when
  nothing editable is focused.
- **A checkbox-selected node could not be clicked to connect.** The
  "one-click dismiss" logic cleared the selection on any plain click of the
  only selected card — even when that card was not the active node — so a
  node marked with the checkbox would never switch the tunnel. Dismiss now
  applies only to the already-active node; clicking any other card still
  selects and switches.
- **Shift+click range selection ignored the visible sort order.** The range
  was computed over the host's insertion order, so with Country A-Z (or
  favorites/recent) sorting the highlighted cards did not match what was on
  screen. The range now follows the sorted order.
- **Shortcuts could fire while a delete/disable confirm dialog was open and
  the switch lock blocked all selection.** Only Escape now acts while the
  confirm dialog is visible (no re-triggering the pending action), and a
  switch in flight only blocks further switches — Ctrl/Shift/checkbox
  selection keeps working.

##### Technical
- `AoGPN/Temalar/app.js`: the Nodes-view `keydown` handler restructured
  (editable-target guard first, Escape closes the top-most layer, confirm-
  dialog guard), the card click handler now checks `pendingSwitchId` only on
  the plain-click switch path, and the Shift+click range uses
  `sortedRealNodes()`. No C# changes; dashboard verified interactively.

---

#### [7.26.26] — Nodes view: real checkbox toggle, batched real-ping, sane selection

##### Fixed
- **Node card checkmark is now a real toggle button.** It used to be a bare
  `<span>` with no handler, so clicking it fell through to the card's
  "switch tunnel" action — checking a node while multi-selecting actually
  switched the active node. It now toggles the multi-select (ring + toolbar
  count) without touching the tunnel; a plain card click still switches.
- **"Test all" pings in small serial batches instead of the whole list at
  once.** Real ping loads ONE core instance per batch and fires the batch's
  requests through it in parallel, so a full-list batch (the configurable
  page size defaults to 1000) started a core with every node simultaneously,
  which errored and reported nothing. Batches of 10 keep each core load light
  and give every node a fair, isolated measurement; failed batches still
  retry with a halved page size and fall back to the mixed test.

---

#### [7.26.25] — GPN per-app capture: VPN routes now force TUN, honest running status

##### Fixed
- **VPN-routed apps (and Global VPN mode) silently did nothing when the dashboard transport was set to Proxy.** `ComputeRequiresTun()` returned `false` as soon as `Transport == "proxy"` — before the mode/action checks — so a Proxy preference defeated the TUN requirement. But per-process routing rules only work when sing-box can attribute connections to processes, which requires the TUN inbound (proxy capture only reaches apps that honour the OS proxy; most games bypass it entirely). The method now forces TUN for Global VPN mode and for GPN (Manuel) mode as soon as any listed app has a VPN route, regardless of the transport choice; Proxy capture is still honoured for lists with only proxy/direct/block entries. When TUN is forced but the app is not running as administrator, the existing NeedAdmin path explains exactly why.
- **The app table showed "Not running" next to live traffic.** The per-process scan is gated off in Global VPN / Off modes and when the window is hidden, so `UpdateLiveStatus` kept the last "Not running" value while the LIVE column showed active connections. A row with live connections is now reported as running when the scan hasn't run — the two columns can no longer contradict each other.

##### Technical
- `ServiceLib/ViewModels/SplitTunnelViewModel.cs`: `ComputeRequiresTun()` reordered (mode/action requirements first, explicit transport second) and `UpdateLiveStatus()` marks a row running when `running is null` but live connections exist. The pure `ManualRoutingRules` behaviour is untouched (all 19 unit tests pass); Debug build 0 warnings, 0 errors.

---

#### [7.26.24] — Node dropdown no longer closes when scrolling the list

##### Fixed
- **Scrolling the open node selector with the mouse wheel or the middle button closed the dropdown.** The themed popover's close-on-scroll handler (`document` capture listener) treated every scroll as "the user scrolled the page behind" — including scrolls that happened *inside* the popover's own scrollable list — so the moment the list moved it closed. The handler now ignores scroll events whose target is the popover (or inside it), so the list scrolls freely; scrolling the page behind still closes it, keeping the popover from floating out of place.
- The same guard was applied to the node right-click context menu, so it also won't vanish if it ever needs to scroll internally.

##### Technical
- `upgradeSelect` in `app.js`: the `document.addEventListener('scroll', …, true)` listener now checks `e.target` — if it is the `.cs-pop` element (or a descendant) the event is ignored, otherwise `close()` runs. Verified live: popover opens with `overflow-y:auto` (max-height 240px), a synthetic scroll dispatched from inside the popover keeps it open, a scroll dispatched on `document` closes it, and after reopening the same holds. Debug build 0 errors.

---

#### [7.26.23] — Translatable TUN Stack & Auto reconnect labels

##### Fixed
- **The sidebar Connection panel's "TUN Stack" label was hard-coded English.** It now reads from a new `tun.stack` key and translates with the rest of the UI (tr: TUN Yığını, ru: Стек TUN, zh: TUN 堆栈, …).
- **The Settings view's "Stack" label was also hard-coded** — it now uses `settings.stack` (tr: Yığın, ru: Стек, hu: Verem, …).
- Audited every label in the compact panel: the Auto reconnect toggles (sidebar + hero strip) already carried `data-i18n="quick.autoReconnect"` and the Mode/Capture/Protocol labels were already wired, so only the two stack labels were missing.

##### Technical
- Added `tun.stack` and `settings.stack` to all 9 language files plus the app.js fallback dict; added `data-i18n` to the two labels in `vpn-gpn-dashboard.html`. Verified live: with TUN capture selected (GPN mode) the sidebar row shows "TUN Yığını" in Türkçe and "TUN Stack" back in English, both Auto reconnect rows show "Otomatik yeniden bağlan", and the Settings view shows "Yığın". All JSON files valid, Debug build 0 errors.

---

#### [7.26.22] — Connection mode selector under the CONNECT button + ring effects

##### Added
- **Connection mode selector now sits directly under the CONNECT button.** A compact glass segmented control (GPN | Global) with a small label lives below the ring in the hero — the same pills the user asked for under the "BAĞLAN" button — and stays fully in sync with the sidebar Connection panel and the mobile hero strip (one source of truth via the shared `.mode-pill` wiring: switching here flips the live GPN/Global panel and updates every other selector instantly, and vice-versa).
- **CONNECT ring got a proper "live device" feel.** A soft breathing halo (radial cyan glow, `ring-breathe` 3.6s) now pulses behind the button, and two orbiting dots circle the ring on the existing spin animation (cyan main dot 7s + smaller violet counter-rotating dot 11s). When connected, the halo and both dots switch to the emerald connected palette, matching the arc/icon/glow that already flip on connect.

##### Technical
- HTML: the ring is wrapped in a centered column (`shrink-0 flex flex-col items-center`) with `.ring-halo`, two `.ring-orbit` dots and the `.ring-mode` selector beneath; the new pills carry `data-mode` + `data-tip-key` so `applyMode()`, the click handler and the tooltip engine pick them up automatically — no new JS. CSS: `components.css` gained the halo/orbit/breath keyframes and the `.seg` / `.seg-opt` styles (active segment uses the theme violet→cyan gradient + glow, `scale(1.03)`). Verified live at 838px (selector centered under ring, gap 20px) and 1200px (ring + selector left, panels right): switching mode from the under-button selector updates sidebar + panel, and switching from the sidebar updates the under-button selector. Console clean, Debug build 0 errors.

---

#### [7.26.21] — Fix broken sidebar Connection panel layout

##### Fixed
- **The sidebar Connection panel no longer overflows or clips.** The row labels used the long i18n keys (`dash.modeStrategy` = "Connection mode", `dash.protocolStrategy` = "Protocol strategy" in every language), which squeezed the Mode/Capture pills and the protocol dropdown past the card's right edge; the sidebar's `overflow-hidden` then clipped the pills mid-word. The compact panel now uses dedicated short keys (`quick.mode` / `quick.capture` / `quick.protocol`), so the rows always read **Mode · Capture · Protocol** regardless of language.
- **The protocol dropdown no longer stretches to fill the row.** `upgradeSelect` copies the native select's classes onto its replacement button, and `.quick-select` carries `width: 100%` — so the "Automatic" button grew to ~186px and pushed out of the card. A `.cs-trigger.quick-select { width: max-content }` rule keeps it content-sized (the same fix applies to the mobile hero-strip selector).
- Labels truncate and the pill groups / switch are `shrink-0`, so even the longest translations can never shove the controls out of the card again.

##### Changed
- The hero strip (mobile/Compact layout) uses the same short keys for its row labels, and its heading now reads from the new `quick.settings` key ("Connection settings") instead of reusing `dash.modeStrategy`.
- Added `quick.capture`, `quick.mode`, `quick.protocol`, `quick.settings` to all 9 language files plus the app.js fallback dict (tr: Mod/Yakalama/Protokol/Bağlantı ayarları, ru: Режим/Захват/Протокол, zh: 模式/捕获/协议, …).

##### Technical
- Measured live in the preview harness at 838px, 646px and 1200px: before the fix the Global pill overflowed the card by +18px and the protocol button by +20px (button width 186px); after, every pill and the button (88px) sit fully inside the card, the mobile strip shows short labels at 646px, and switching to Türkçe relabels the rows "Mod / Yakalama / Protokol" instantly. Console clean, Debug build 0 errors.

---

#### [7.26.20] — Keyboard accessibility for the themed dropdowns

##### Added
- **The language dropdown is now fully keyboard-operable.** ArrowDown/ArrowUp open the list (when closed) and move between the 9 languages with wrap-around; Home/End jump to the first/last entry; Enter/Space select the focused language (applied instantly and persisted to the host, exactly like a mouse click); Escape closes and returns focus to the trigger button. On open the currently selected language is focused first, and after a selection focus returns to the button so the keyboard flow never gets lost.
- **Every themed dropdown shares the same keyboard model.** The generic `upgradeSelect` helper (node selector, system proxy mode, quick + mobile protocol selectors, TUN stack, node sorting and the 20+ settings selects) gained the same arrow/Home/End/Escape handling and starts with the active option focused.
- **Visible keyboard focus.** Both the upgraded selects and the language options now have a theme-consistent `:focus-visible` state (subtle cyan outline + brighter tint) so keyboard users can see which option is highlighted — the global dark UI previously had no focus affordance for these controls.

##### Changed
- **All remaining native `<select>` popups are gone.** The OS-drawn white dropdown that clashed with the dark theme (same bug as the language selector in `[7.26.18]`) is replaced everywhere: top-bar node selector, system proxy mode, quick + mobile protocol, TUN stack, node sort order and the settings form selects. The original `<select>` stays in the DOM (value reads, `.options` and existing `change` listeners untouched — choosing an option dispatches a bubbling `change`), it is only hidden and rendered as a themed button + popover instead.

##### Technical
- `app.js`: the language wiring gained `openLangPop/closeLangPop/selectLangFocus/moveLangFocus` and keydown handlers on both the trigger and the listbox; `upgradeSelect` gained `focusOpt` and keydown handlers on the trigger and popover, plus focus-on-open. `components.css`: `#dashboardLangPopover [data-lang]:focus-visible` and `.cs-option:focus-visible` rules. Verified live in the preview harness: arrow open/navigate/Enter-select/Esc-close for both the language dropdown and the protocol select (values sync, `change` listeners fire, focus returns to the trigger), Home/End, console clean, Debug build 0 errors.

---

#### [7.26.19] — Compact sidebar Connection panel in Status layout

##### Changed
- **The sidebar Connection panel is now noticeably shorter.** Each setting row used to stack a label, the control and a full sentence of explanation — three rows of tall blocks that squeezed the hero. The panel now uses a compact two-column layout per row (label on the left, control on the right), drops the verbose inline hint lines (the same info lives in the pill/dropdown tooltips) and tightens spacing. Measured at 1200px: panel height dropped to ~183px from the previous taller card, with the same three controls (Mode, Capture, Protocol) plus Auto reconnect.
- The TUN Stack selector still only appears while TUN capture is selected, and lock badges on the transport pills are unchanged.

##### Technical
- HTML-only change in the sidebar card (`vpn-gpn-dashboard.html`): each row is now `flex items-center justify-between` with a small uppercase label and the control on the right; the hint paragraphs were removed. The hero strip (mobile/Compact) keeps its own controls untouched. Verified live in the 1200px harness: rows render compactly, TUN pill shows the stack row and hides it again on Proxy, pills stay synced with the hero strip, no duplicate ids, Debug build 0 errors.

---

#### [7.26.18] — Theme-consistent language dropdown

##### Fixed
- **The language selector's open list rendered as a tall white native popup** (the OS-drawn `<select>` dropdown) that clashed with the dark theme. The native select is replaced by a **custom themed popover** in the title bar — same dark glass, border and glow as the theme picker — listing all 9 languages with the active one highlighted (tint + checkmark). Selecting applies instantly and persists to the host exactly as before (`applyLanguage` + `set_language` bridge), and the button label shows the current language with a globe + chevron.
- The dropdown closes on outside click, keeps `aria-haspopup`/`aria-expanded`/`role="listbox"` semantics, and is rebuilt whenever the language changes so the active marker never goes stale.

##### Technical
- Replaced `#dashboardLangSelect` (native `<select>`) with `#dashboardLangBtn` + `#dashboardLangPopover` in the HTML; `window.applyLanguage` now updates the button label too, and a shared `LANGUAGES` array + `renderLangPopover()` drive the options. Verified live: opens with 9 options, selecting Türkçe relabels the whole UI instantly, outside click closes, console clean, Debug build 0 errors.

---

#### [7.26.17] — Layout as a theme axis: Status ↔ Compact

##### Added
- **The dashboard layout is now selectable like a theme.** The theme popover gained a layout row above the color swatches: **Status** (controls live in the sidebar Connection panel — the default) and **Compact** (the sidebar Connection panel disappears and the controls move to a strip at the bottom of the hero). The choice is independent of the color theme (persisted under its own `aogpn.layout` key, applied pre-paint so there is no flash) and can be switched from the popover or pushed by the host (`window.applyLayout`).
- **Hero strip is now always interactive**: in the Status layout it still appears only below 768px (where the sidebar is hidden); in Compact it is visible at every width and also gained an **Auto reconnect** toggle mirroring the sidebar one.

##### Technical
- `body[data-layout="compact"] .layout-sidebar-conn { display:none }` / `.layout-hero-strip { display:flex !important }` in `components.css`; the pre-paint inline script reads `aogpn.layout` alongside the theme. `applyLayout` syncs the popover buttons, and `bindProtocolSelect` / `bindAutoReconnectToggle` now drive both the sidebar and the hero-strip controls. Verified live at 646px and 1200px: switching layout moves the controls between sidebar and hero strip instantly, colors stay independent, console clean, Debug build 0 errors.

---

#### [7.26.16] — Status-first dashboard: connection controls move to the sidebar

##### Changed
- **The Dashboard hero is now status-first.** The CAPTURE (Proxy/TUN), PROTOCOL STRATEGY and Connection Mode sections no longer crowd the hero — they moved into the sidebar's Quick Connection card, which is now a compact **Connection** panel: Mode (GPN / Global), Capture (Proxy / TUN), Protocol (dropdown), Auto reconnect, with their hints inline.
- **Live GPN / Global panels moved up into the hero** next to the CONNECT ring — Your IP / Session / Verification cards, the game cards and the Global VPN transport/coverage panels now sit directly beside the ring, with the existing fade-slide mode swap. Routing direction (Whitelist/Blacklist) stays inside the GPN panel.
- The old standalone Connection Mode section and the separate dual-panel block below the hero are gone — one hero, live status first.
- **Mobile fallback:** below 768px the sidebar (and thus the Connection panel) is hidden, so a compact connection-settings strip appears at the bottom of the hero with Mode / Capture / Protocol controls. Nothing is unreachable on narrow windows.
- Status banners (TUN admin notice, capture lock notice, TUN/proxy note, connection error, IP leak/OK) stay in the hero under the panels.

##### Technical
- Mode/transport/protocol pills still use the same classes, so the existing `querySelectorAll` listeners bind to both the sidebar and the mobile strip automatically; `syncQuickControls` and the protocol change handler now also drive `mobileProtocolSelect`. `tunPill`, lock badges, `tunStackRow`, `routeHint`, `transportHint` and `protocolHint` keep their ids but moved into the sidebar card. Verified live at 1200px (panels in hero, sidebar Connection panel) and 646px (mobile strip visible, sidebar hidden); mode switch syncs both pill sets, protocol select syncs both dropdowns, console clean, Debug build 0 errors.

---

#### [7.26.15] — Quick connect/disconnect in the mobile icon nav

##### Added
- The narrow-window icon nav (below 768px) now has a **power button at the top** that mirrors the CONNECT ring: it posts the same `toggle_connection` bridge payload, so it works with the real host and also falls back locally in previews. It turns **emerald when connected** / cyan when idle, gets localized idle/active tooltips, and — like the big CONNECT button — is **disabled with the TUN-lock tooltip** when TUN is selected without admin rights while disconnected.

##### Technical
- New `#mobileConnectBtn` in the mobile nav + `.mobile-connect` styles in `components.css` (hover, emerald connected state, disabled dimming). `setConnected`, `updateConnectTooltip` and `updateTransportLock` all drive it, so the button stays truthful through host pushes (`setConnectionState`) and admin-state changes (`setAdminState`). Verified live at 720px and 400px: connect/disconnect toggles, lock/unlock follows the host, console clean, Debug build 0 errors.

---

#### [7.26.14] — Mobile nav: Performance & Settings reachable + active state

##### Fixed
- The **bottom nav on narrow windows (< 768px) only had 3 links** (Dashboard, Nodes, Game Boost) while the sidebar has 5 — Performance and Settings were simply unreachable on mobile, and the window can actually be dragged down to its 720px minimum. The bottom nav now carries all 5 entries.
- Mobile nav links had **no visual active state**: `showView` toggled an `active` class but only the sidebar's `.nav-item` had CSS for it. The links now share a `mobile-nav-link` class with a dedicated active style (soft tinted background + accent icon), mirroring the sidebar behaviour.

##### Technical
- Verified live by walking the dashboard at full width and via the running preview: all 5 bottom-nav links switch views, exactly one is active at a time, and the active tint + accent icon apply. Console clean, Debug build 0 errors.

---

#### [7.26.13] — UX polish from a real-user walkthrough

##### Fixed
- **Page titles no longer stick to English.** `showView` rebuilt the header via `innerHTML` with a hard-coded `VIEW_TITLES` table, which stripped the `data-i18n` hook — switching language changed every label except the big page heading. Titles now resolve fresh from the i18n dictionary on every view switch (new `view.<name>.title1/.title2/.sub` keys in all 9 languages + fallback), so headings, subtitles and the gradient split follow the active language instantly.

##### Removed
- The stale **NEW badge** on the GPN Game Boost nav item.

##### Improved
- **Nodes view:** the "Selected node" row is hidden while the node list is empty, so the view no longer claims a selection ("0 online" + "Istanbul TR-1") that cannot exist.
- **Theme popover:** swatches now carry `aria-label` / `aria-pressed`, and the active theme gets a white ring + center dot so the current choice is visible at a glance; the grid re-renders after a pick.
- **Quick Connection hint** shortened from 122 to ~80 characters and made translatable (`quick.hint` in 9 languages).

---

#### [7.26.12] — Adaptive gauge calibration for real bandwidth

##### Fixed
- The download/upload gauges divided by **hard-coded 25 / 12 Mbps** full scales, so a 200 Mbps fibre link pegged the dial at full while an 8 Mbps line barely moved it. The gauges now **auto-calibrate to the observed peak**: the full scale grows instantly to a "nice" round value (5 / 10 / 25 / 50 / 100 / 250 / 500 / 1000 / 2500 / 5000 / 10000 Mbps) above the current sample with 15% headroom, and steps back down in nice increments only when the link drops below half of the current scale — steady traffic keeps a stable dial.
- A small **max-scale label** under each dial shows the current full-scale value, so the reading is interpretable (e.g. 180 Mbps on a 250 Mbps scale).
- Applied identically on both surfaces: the dashboard (`Temalar/app.js`) and the native WPF connection gauges (`TelemetryDashboardViewModel` — new `DownGaugeMax` / `UpGaugeMax`). Scales reset to the 25/12 floors on disconnect.

---

#### [7.26.11] — Re-assert TUN fail-safe routes on every settings save

##### Fixed
- `SaveSettingsCoreAsync` wrote the TUN block (MTU / stack / auto-route / strict-route / legacy-protect) **raw** — it never re-applied `WindowsTunStabilityPolicy`. Saving a custom MTU (e.g. 9000) or toggling `tunAutoRoute` off in Settings could leave the TUN without its fail-safe route defaults until the next app restart, risking traffic escaping the tunnel while connected.
- The settings save path now re-runs the stability policy on Windows after writing the TUN block (MTU/stack normalisation + pinned `AutoRoute` / `StrictRoute` / `EnableLegacyProtect`), identical to what config load does. Non-Windows keeps MTU/stack normalisation only.

##### Verified (full TUN leak-protection inventory)
- `ConfigHandler.LoadConfig` applies `WindowsTunStabilityPolicy.Apply` on Windows at every startup (and MTU/stack normalisation elsewhere).
- Connection start: `ToggleConnectionAsync` → `SetTransportAsync` → `SplitTunnelViewModel.ApplyAsync` sets `EnableTun` and writes managed routing rules via `ManualRoutingRules.BuildManagedRules` (per-app whitelist/blacklist + catch-all to `proxy` or `direct`).
- sing-box TUN inbound: `auto_route` + `strict_route` from config, safe MTU, `auto_detect_interface`, loopback-exclude rules for protected cores, DNS hijack for protected processes, ICMP policy, multicast/broadcast reject rules from the `tun_singbox_rules` template.
- Xray TUN inbound: `autoSystemRoutingTable` to `0.0.0.0/0` + `::/0`, route-exclude subtraction, `sniffing.routeOnly`.
- `EnableLegacyProtect` drives the pre-SOCKS sing-box helper chain (`GetPreSocksItem` + `BuildPreSocksIfNeeded`) so non-sing-box cores cannot bypass TUN.
- `SystemProxyPolicy.ConnectionNeedsSystemProxy` returns false for TUN transport — no double capture with the OS proxy.
- All 216 ServiceLib tests pass.

---

#### [7.26.10] — Remove fake UI: honest status dot, live session node, dead subscription bridges

##### Fixed (was working but dishonest)
- The **sidebar footer status dot** was hardcoded green — it claimed "connected" even when disconnected. It now reflects the real connection state (green when connected, cyan when idle) with a tooltip, driven by `setConnected`.
- The **Session card's second line** was a static "uptime counter" label that nothing ever filled (`sessionAddr`). It now shows the **active node** (name · address · protocol) live from the host node info, with a hover tooltip; falls back to `—` when idle.

##### Removed (dead, could never run)
- The **subscription management bridge** in `MainWindow` was fully wired on the C# side (`add_subscription` / `sync_subscriptions` / `get_subscriptions` / `delete_subscription` cases + `AddSubscriptionAsync` / `SyncSubscriptionsAsync` / `PushSubscriptionsAsync` / `DeleteSubscriptionAsync`) but the dashboard has **no subscription UI** and no JS consumer — `window.updateSubscriptions` was called by the host yet never defined in the renderer. All of it is removed. Node acquisition still happens through the working **node pool** (GitHub raw .txt / subscription links) which has a real UI and bridge.
- Unused `stat.uptimeCounter` translation key removed from all 9 language files.

##### Audited
- Every `window.*` entry point the host calls is now defined in `app.js` (23/23).
- Every `postToHost` action from the renderer has a matching C# bridge case.
- All 209 dashboard element ids checked; remaining id-only nodes are container/CSS anchors or the intentionally restored theme cursor layers.

---

#### [7.26.9] — Remove unused WFP probe machinery

##### Removed
- The **WFP probe** (`WindowsWfpCompatibility.Probe()` / availability enum / capability record) is gone. It only opened and closed the WFP engine to answer "does WFP work?" — the app never installed a WFP filter, so the telemetry was dead weight. The WFP status badge that surfaced it in the dashboard is removed too.
- The misleadingly named **`WfpGuardEnabled`** config flag is removed. It never toggled WFP filtering (none exists); it merely gated the TUN stability policy, which is unrelated to WFP and now always applies on Windows.

##### Technical
- `WindowsTunStabilityPolicy` (safe MTU/stack defaults + fail-safe `AutoRoute`/`StrictRoute`/`EnableLegacyProtect`) is kept — it is genuine protection against fragmentation and game disconnects — and moved to its own file `WindowsTunStabilityPolicy.cs`. Non-Windows still gets MTU/stack normalization only, unchanged.
- Removed the WFP fields from the dashboard connection settings push and all dashboard consumers (`applyWfpState`, `wfpBadge`).

---

#### [7.26.8] — Surface WFP status + quick TUN stack selector

##### Added
- The dashboard CAPTURE section now shows a **WFP status badge** next to the transport pills. It surfaces the host's WFP probe result (Ready ✓ / needs admin / unavailable) with a tooltip explaining what it means. The badge is informative only — this app does not install WFP filters; a Ready state simply means the WFP engine could be opened, which is what enables the tuned TUN stability policy.
- A **TUN Stack selector** (gvisor / system / mixed) now sits in the CAPTURE section, visible only while TUN capture is selected. It mirrors the settings-form field and persists through the new `set_tun_stack` bridge (`SetTunStackAsync`).

##### Technical
- `applyWfpState()` in the dashboard consumes the connection settings group that the host already pushed (previously dead telemetry — `wfpAvailability`/`wfpMessage`/`wfpReady`/`wfpGuardEnabled` were sent but never rendered).
- New `set_tun_stack` bridge case in `MainWindow`; stack value is validated against `Global.TunStacks` before persisting and re-pushed to keep the quick selector and settings form in sync.

---

#### [7.26.7] — Show effective routes in the boost table for GPN blacklist

##### Added
- In GPN **blacklist** direction, the Game Boost table's route column now shows the **effective** route instead of just the configured choice, with an amber `⇄ Blacklist` marker: an app assigned VPN renders as `Direct ⇄ Blacklist` (it stays direct), an app assigned Direct renders as `VPN ⇄ Blacklist` (it is tunneled), Block stays Block.
- A table-level notice appears above the app list while blacklist is active: "Blacklist active — assigned apps stay direct; everything else is tunneled." It disappears in whitelist mode.
- The route selector keeps showing the user's intent; only the badge reflects the effective behaviour, so nothing is silently surprising.

##### Technical
- `effectiveRoute()` / `blacklistActive()` helpers in the dashboard mirror the backend inversion (`ManualRoutingRules.BuildEntryRule` with `invertManual`); direction changes re-render the table instantly. New `dir.blacklistTableHint` key in all 9 languages + fallback dictionary.

---

#### [7.26.6] — Explain the Global VPN capture lock

##### Added
- When Global VPN is selected, the capture area now shows **why** the transport pills are locked: a 🔒 badge on the Proxy pill and an inline notice explaining that Global VPN forces TUN so every app is captured at the network layer and nothing can bypass the tunnel (and that choosing GPN Game Tunnel restores the Proxy option).
- The notice is fully localised (`transport.lockGlobalVpn` added to all 9 languages + the embedded fallback dictionary) and follows language switches instantly.

---

#### [7.26.5] — Merge per-app connection options to 3 real choices

##### Changed
- The per-app connection type was 5 options that collapsed to 3 real behaviours (`vpn`/`proxy`/`vpn+proxy` all mapped to the same proxy outbound). Now the selector offers exactly three honest options: **VPN (tunnel)**, **Direct** and **Block** — in the Game Boost / Performance tables and the native edit dialog.
- Legacy saved values (`proxy`, `vpn+proxy`) are automatically normalised to `vpn` on load (`NormalizeAction`), so old configs keep working and are migrated on the next apply; the route mapping and the pre-Transport system-proxy fallback still understand them.
- Known-game suggestions still mark only real games with the "suggested" badge (generic apps keep the no-badge default).

##### Technical
- `ManualActions` reduced to vpn/direct/block (WPF dropdowns bind the same list). `SystemProxyPolicy` legacy fallback now also matches `vpn`. New `route.assign/vpn/direct/block` i18n keys in all 9 languages + fallback dictionary.
- Verified live: selectors show 3 options, a legacy `proxy` entry renders as VPN, Turkish labels apply, build clean.

---

#### [7.26.4] — Connection-logic overhaul: GPN direction (whitelist/blacklist), real protocol strategy, live Global VPN panel

##### Added
- **GPN routing direction** — the Game Tunnel now supports both directions with one toggle in the GPN panel:
  - **Whitelist** (default): only games/apps assigned in Game Boost are tunneled; everything else stays direct.
  - **Blacklist**: assigned games/apps stay direct/blocked and *everything else* is tunneled (catch-all inverts).
  - Backed by `ManualRoutingRules.BuildManagedRules(..., invertManual)` (catch-all + per-entry actions inverted), persisted in `ConnectionSettingsItem.InvertManualRouting`, bridged via `set_split_direction`, pushed in the settings and monitor snapshots.
- **Protocol strategy is now real** — the strategy pills (Automatic / WireGuard / Mimic / Hysteria2 / OpenVPN) previously did nothing. At connect time `EnsureProtocolCompatibleAsync` uses the (previously dead) `ConnectionProtocolPolicy`: if the active node can't speak the chosen protocol it auto-switches to the best matching node (notice shown); if none matches, a notice explains the fallback instead of silently connecting over a mismatched protocol. Preferences never rewrite credentials.

##### Changed
- **Global VPN panel is live** — the status badge, transport line and coverage bar now reflect the real connection state (previously hard-coded "All Traffic Tunnelled" / 100%); IP verification already streamed real data and still does.
- **Sidebar Route selector removed** — the same mode is chosen once on the Dashboard (GPN ↔ Global VPN) or in the Game Boost view; the sidebar Quick Connection keeps Protocol + Auto-reconnect.

##### Technical
- 8 new i18n keys (`dir.*`, `tip.dir*`, `global.disconnected`) added to all 9 languages + embedded fallback dictionary.
- Verified live: direction toggle flips pills/hints and posts `set_split_direction`; Global VPN badge/transport/coverage follow connect state; sidebar shows no Route selector; build clean.

---

#### [7.26.3] — Organise Temalar/: per-theme CSS files + section maps

##### Changed
- The `Temalar/` folder is now structured so editing never requires reading the whole dashboard:
  - `base.css` — `:root` variables, reset, body background/ambient aurora, shared helpers (accent-*, theme deck, spinner skeleton).
  - `components.css` — component styles (navigation, connect ring, tooltips, toast, badges, gauges, mode switch, window frame, scrollbar, node cards, settings, cursor effects, custom tooltips).
  - `themes/theme-<id>.css` — one file per theme: palette variables (`body[data-theme="…"]`), signature effects (`::before/::after`, `.glass`, `.font-display`, spinner, tooltip, badge, cursor) and that theme's `@keyframes`. The 11 original themes, 258 rules split with zero loss (integrity-checked).
  - `dashboard.css` — now a pure `@import` manifest (base → components → themes; order matters).
- **Section maps added**: `dashboard.css` (file map + how to add a theme), `app.js` (line-ranged section map: state, telemetry, connect labels, mode/transport, node cards, nodes view, context menu, views/monitor/split, settings, theme engine, i18n, host setters, node pool, window controls, drag & drop, tooltips), `themes.json` (ignored `_note` field per theme pointing at its CSS file — JSON cannot hold comments).

##### Technical
- Verified live: all 13 CSS files load through the `@import` chain (200s, no 404s), every theme's palette applies on switch, 11 swatches render, language applies instantly, no new console errors.

---

#### [7.26.2] — Split the dashboard into external CSS/JS under Temalar/

##### Changed
- The 4,984-line `vpn-gpn-dashboard.html` is now a slim ~1,000-line document (≈100 KB instead of ≈280 KB):
  - All styles moved to `Temalar/dashboard.css` (theme palettes, navigation, connect ring, tooltips, toasts, badges, gauges, mode switch, window frame, node cards, settings, cursor effects).
  - The main application script moved to `Temalar/app.js` (state, bridge, node pool, sorting, i18n, tooltips, themes).
- The document loads them via `<link rel="stylesheet" href="Temalar/dashboard.css">` and `<script src="Temalar/app.js"></script>` — same-origin, allowed by the existing CSP (`style-src 'self'`, `script-src 'self'`).
- Theme definitions were already external (`Temalar/themes.json`); now the whole `Temalar/` folder ships styles, logic and themes, and `Dil/` ships translations. Editing a theme, a style or a JS behaviour no longer touches the HTML at all.
- The pre-paint theme script stays inline so the saved theme applies before first paint (no flash).

##### Technical
- `Temalar/**` was already copied to the output by the csproj; no new build wiring was needed (comment updated).
- Verified: external CSS/JS load (200), all 11 original themes render, theme switching works, language applies instantly, node pool panel present, no 404s or new console errors.

---

#### [7.26.1] — Fix top-bar theme button + move language selector next to it

##### Fixed
- **Theme button in the title bar was unclickable** — the popover wiring ran twice (once at startup, again when `Temalar/themes.json` finished loading), so one click toggled the popover open *and* closed it in the same action. The button/document listeners are now registered exactly once (`data-theme-popover-wired` guard); the theme grid still re-renders after the disk themes load.
- **Language selector moved** from the Settings page into the title bar, side-by-side with the theme button (globe icon + dropdown). Same `dashboardLangSelect` id keeps the instant-apply wiring, the host `applyLanguage` sync and the WPF language push working unchanged; the duplicate Settings section was removed.

##### Technical
- `renderTopThemePopover()` is now idempotent for its listener wiring.

---

#### [7.26.0] — Node pool: pull nodes from your own link list + country/favorite/recent sorting

##### Added
- **Node pool** — a link pool in the VPN Nodes view for the raw `.txt` / GitHub / subscription URLs that publish node lists. Add links (or press Enter), see them listed with one-click remove, then hit **Download new nodes** to fetch every pooled link (direct first, proxy fallback), parse the content (plain text, base64, wireguard, …) and import the shared nodes into the current group without touching existing entries. Per-link progress and the final import count are streamed to the Nodes-view toast.
- **Node sorting** — a dropdown next to the node count: **Country A-Z** (remark code preferred, GeoIP fallback for plain-IP nodes), **Favorites first**, **Recently used** (last node you connected through first), and **Default order**. Sorting is client-side and instant.
- **Favorite star** — every node card has a star toggle (posts `toggle_node_fav`); favorites persist in the profile extension table and drive the Favorites sort.
- **Recently-used tracking** — selecting a node stamps `LastUsed` on the profile, so the Recent sort works automatically.

##### Technical
- `ProfileExItem` gains `IsFav` + `LastUsed` (sqlite-net auto-migrates existing tables); `ProfileExManager` gets `SetFav`/`GetFav`/`TouchLastUsed`.
- `Config.NodePoolLinks` persists the pool in the JSON config (survives restarts, no subscription model changes).
- New bridge actions: `get_node_pool`, `add_node_pool_link`, `remove_node_pool_link`, `fetch_node_pool`, `toggle_node_fav`.
- `PushNodeListAsync` now pushes `country` (remarks code → GeoIP fallback), `fav` and `lastUsed`; the dashboard stores them per node and re-renders in the chosen order.
- New i18n keys `nodes.sort*` + `nodes.pool*` in en/tr and the embedded fallback dictionary.

---

## [7.25.5] — 2026-08-24 — Fix app route changes silently ignored in GPN mode

### Fixed
- **Changing an app's route (VPN / Proxy / VPN + Proxy / Direct / Block) did nothing** in the Game Boost and Performance application tables. The route `<select>` posted `{"action":"direct",…}` — a duplicate `action` key in the JavaScript object literal meant the shorthand route value overwrote the `set_app_route` message type, so the host switch never matched and the change was dropped. The route is now sent under its own `route` key and the C# handler reads it from there, matching the rest of the message protocol.

### Technical
- `bindRouteSelectors` now posts `{ action: 'set_app_route', processName, displayName, route }`; `MainWindow.xaml.cs` reads the value from the `route` property. Verified live: change → host → snapshot → select and badge re-render with the new route.

---

## [7.25.4] — 2026-08-24 — Custom tooltips: soft, theme-aware floating cards

### Changed
- **Native title bubbles replaced by custom tooltips** — every `title` attribute (dashboard + settings, ~105 elements) is now rendered as a soft floating card: dark glass with backdrop blur, a theme-tinted border and glow, a small arrow, and a gentle fade + scale + slide animation with a short hover delay. Tooltips flip below the element when there is no room above and clamp to the viewport edges.
- **No more raw keys** — the full English dictionary is now embedded in the page as the last-resort fallback (mirrors `Dil/en.json`), so strings like `transport.tun`, `dash.routeHintVpn` or `transport.hintTun` can never surface, even when external language files fail to load. The connect button and status label tooltips are fully localized.
- **Main tooltips rewritten and translated** — the connect button, mode pills, capture pills, protocol pills, proxy badge/selector/test and node picker now use short, friendly tooltip copy routed through 18 new `tip.*` keys in all 9 languages (138 keys each). Tooltips follow the active language instantly.

### Technical
- Tooltip engine reads `data-tip-key` (translated via `t()`), `data-tip` (raw) or a live `title` (lazy-adopted via a `MutationObserver`, so dynamically-set titles such as the connect button's are picked up with their placeholders substituted). Hit-testing via `elementFromPoint` keeps tooltips working on disabled buttons; focus listeners cover keyboard users; scroll/resize/click dismiss.

---

## [7.25.3] — 2026-08-24 — Dashboard layout: node picker up top, GPN becomes primary mode

### Changed
- **Node selector moved to the top bar** — the server picker now lives right next to the system-proxy badge as a selectable dropdown (`select_node` flow, optimistic switch + host confirm). The old "Node" stat card in the connection center is gone.
- **Connection mode panel relocated** — the GPN ↔ Global VPN selector moved to where the IP stat cards used to be, styled like the protocol strategy segmented control. **GPN Game Tunnel is now the primary (first, default) mode** with a PRIMARY badge; Global VPN stays the secondary option.
- **GPN panel now leads with live connection info** — Your IP / Session / Verification cards moved into the GPN Game Boost panel above the per-game cards; the Global VPN panel (Transport / IP Verification / Coverage) still opens when Global VPN is selected, with the existing fade-slide transition.
- **Single mode selector** — the old Route Mode pill row next to CONNECT was removed to avoid duplicate selectors.

### Added
- New i18n keys `dash.modeStrategy`, `mode.primary`, `topbar.node`, `topbar.loadingNodes`, `topbar.noNodes` in all 9 language files (120 keys each) plus the embedded fallback dictionary.

### Technical
- Shared `requestNodeSwitch()` helper now backs both the Nodes-view cards and the top-bar dropdown; `renderTopNodeSelect()` rebuilds the dropdown on node-list pushes, switch confirmation and language changes. `nodeName`/`nodeAddr` updates are null-guarded since the dashboard node card was removed.

---

## [7.25.2] — 2026-08-24 — Full translations for all 9 languages

### Added
- **Complete translations** for the 7 secondary languages (`zh-Hans`, `zh-Hant`, `fa`, `fr`, `hu`, `id`, `ru`): all 115 keys now match English/Turkish. The 21 missing long-description keys (route/capture hints, protocol hints, Game Boost empty states, Global VPN descriptions, status lines, telemetry labels, leak banner) are now fully translated in every language.
- **Built-in English fallbacks** for those 21 keys added to the dashboard's embedded dictionary, so the UI never shows raw keys even if the external language files fail to load.

### Technical
- All 9 language files normalized to the canonical `en.json` key order with consistent formatting, and verified to parse and carry the correct `{link}`, `{route}`, `{capture}`, `{connect}` and `{ip}` placeholders.

---

## [7.25.1] — 2026-08-24 — Fix public IP never showing while connected

### Fixed
- **Public IP / ISP IP never displayed**: the IP check only queried the single configured endpoint (`api.ip.sb`), which is blocked for many users. `GetIPInfo` now falls back across the built-in list plus `ipinfo.io`, `ip-api.com` and `api.ipify.org` until one succeeds, and remembers the working endpoint so later checks skip blocked URLs. The dashboard IP card and verification status now populate reliably.
- **ISP baseline cached through the tunnel**: the baseline was re-cached while connected, so with TUN active the tunnel exit IP could be recorded as the "ISP IP". Caching now only happens while disconnected.
- **False "leaking" alarm when no baseline exists**: the dashboard only reports a leak when it actually has the cached ISP IP to compare against; without a baseline it shows the tunnel IP as "probing" instead of crying leak.
- **Theme loader not starting**: the startup call to `loadThemesFromDisk()` was misplaced inside the loader function (recursion) — moved to the init block.
- **Raw i18n keys in dynamic strings**: the dashboard now loads the English dictionary at startup, so `t()` calls never surface raw keys before the host pushes the saved language.

## [7.25.0] — 2026-08-24 — Themes & languages from folders

### Added
- **Temalar folder** — theme definitions moved from the dashboard HTML to `Temalar/themes.json`; the dashboard loads it at startup (with a built-in fallback), so new themes can be added by editing one JSON file. Loading a few KB of local JSON has no measurable startup impact.
- **Dil folder** — one JSON file per language (`Dil/en.json`, `Dil/tr.json`, `Dil/zh-Hans.json`, …); English is always merged as the base so missing keys fall back gracefully.
- **Instant language switching** — selecting a language in the dashboard applies immediately (sidebar, buttons, pills, hints, telemetry, IP verification, boost cards), exactly like the theme picker. No restart required.
- `AppEvents.LanguageChanged` channel so the native settings window also pushes language changes to the dashboard live.
- Added `telemetry.excellent/good/warning` and `ip.leakBanner` translation keys.

### Changed
- Removed the "restart required" confirmation dialog from the language dropdown.
- `set_language` handler now pushes the new language to the dashboard (`PushLanguageAsync`) after saving config.
- ThemeSettingViewModel no longer shows a reboot notice for language changes — the dashboard re-localises instantly.

### Technical
- Dashboard i18n engine: `t(key, params)`, `loadLanguage()`, `applyTexts()` (preserves SVG icons and nested spans), `refreshDynamicTexts()` for JS-driven strings.
- `Temalar/**` and `Dil/**` are shipped beside the dashboard via the csproj (`CopyToOutputDirectory`).
- Fixed a text-duplication bug where whitespace-only text nodes were being translated.

---

## [1.0.0] — 2026-08-24 — AoGPN-native build-up

The first AoGPN-native version (assembly `1.0.0`, the direct predecessor of
`1.1.0`). This build-up — the dual-panel mode swap, real per-game latency, ISP
baseline and connection-flow hardening — was folded into `1.1.0` as the 7.26.x
milestones.

### Added
- **Dual-panel mode swap** with fade-slide transition between GPN Game Boost and Global VPN status panels
- **Per-game latency display** in dashboard boost cards, color-coded by quality (green < 80ms, amber 80–180ms, red > 180ms)
- **ISP IP baseline display** — your real ISP IP is cached at startup and shown in the dashboard before connecting
- **Tray notification** when a second instance is blocked and the existing window is brought to front
- **Theme selector popover** in the top-right title bar for quick theme switching
- **Language restart confirmation** dialog when switching languages
- **Real-time protocol strategy selector** (Automatic, WireGuard, Mimic Reality/TLS, Hysteria2/TUIC, OpenVPN)

### Changed
- **Theme Loadout grid** removed from the main dashboard (redundant with top-right popover)
- **Game Boost cards** now render live data from SplitTunnelViewModel instead of hardcoded placeholders
- **IP verification** now uses retry logic (3 attempts with 2s delay), accurate leak detection with cached ISP baseline
- **Connection flow** improved: ISP baseline fetched before first connect, tunnel verification faster

### Fixed
- **Global VPN mode** now forces TUN transport automatically — user cannot accidentally select Proxy in VPN mode
- **Proxy preference** preserved alongside active TUN connection (no longer overridden by connection state)
- **IP verification** no longer shows false "leaking" or stuck "verification" states
- **Single instance** now brings the existing window to front when a second instance is launched
- **Admin privileges** enforced via app.manifest (requireAdministrator)
- **Cursor effects, matrix rain, sound engine, and confetti engine** preserved across theme changes

### Technical
- Added `LatencyMs` / `LatencyText` fields to `SplitTunnelAppItem` DTO
- Added `PushIspBaselineAsync` to stream ISP IP to dashboard at startup
- `PushMonitorSnapshotAsync` injects active-node ping into every running app
- `CacheIspIpAsync` with retry (3 attempts × 2s) for resilient ISP IP resolution
- `GetIPInfoWithRetryAsync` generic retry wrapper for IP API calls
- Removed ~54 lines of redundant Theme Loadout HTML + JS from dashboard

---

## [7.24.4] — 2026-07-30 — Original

- Initial release of AoGPN (forked from v2rayN)
- WebView2-based Connection Center dashboard
- 11 gaming themes with cursor effects
- Split-tunnel GPN game routing
- sing-box and Xray core support

---
