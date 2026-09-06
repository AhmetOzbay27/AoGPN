/* ==========================================================================
   core/i18n.js — AoGPN dashboard module (çeviri + dil yönetimi)
   --------------------------------------------------------------------------
   app.js ile aynı küresel pencere kapsamında, ancak app.js'den ÖNCE yüklenir
   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).
   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel
   takma adlarını alır. ========================================================================== */
(() => {
  'use strict';
  const postToHost = aogpn.bridge.postToHost;
  const escHtml = aogpn.util.escHtml;
  const { statusLabel, btnText, btnSub } = aogpn.dom;
  // ---------- i18n (Dil folder) ----------
  // The full English dictionary is embedded as the last-resort fallback (it mirrors
  // Dil/en.json), so the UI never shows raw keys even if external files fail to load.
  // Per-language files override it at runtime. Switching language applies instantly, exactly
  // like the theme picker - no restart needed.
  const _fallbackDict = {
    "sidebar.menu": "Menu",
    "sidebar.subtitle": "Gaming Private Network",
    "nav.dashboard": "Dashboard",
    "nav.nodes": "VPN Routes",
    "nav.boost": "GPN Game Boost",
    "nav.perf": "Performance",
    "nav.settings": "Settings",
    "nav.about": "About & Help",
    "quick.title": "Quick Connection",
    "quick.sub": "One-click network preferences",
    "quick.hint": "Applied when you press CONNECT; mode &amp; routing live on the Dashboard.",
    "quick.autoReconnect": "Auto reconnect",
    "quick.capture": "Capture",
    "quick.mode": "Mode",
    "quick.protocol": "Protocol",
    "quick.settings": "Connection settings",
    "settings.stack": "Stack",
    "tun.stack": "TUN Stack",
    "quick.routeOff": "Route: Off",
    "quick.routeVpn": "Route: Global VPN",
    "quick.routeGpn": "Route: GPN Game Tunnel",
    "header.controlDeck": "Control Deck",
    "header.connectionCenter": "Connection Center",
    "view.dashboard.title1": "Connection ",
    "view.dashboard.title2": "Center",
    "view.dashboard.sub": "Realtime tunnel status · low-latency game routing",
    "view.nodes.title1": "VPN ",
    "view.nodes.title2": "Nodes",
    "view.nodes.sub": "Pick a server — the tunnel connects through your chosen node",
    "view.boost.title1": "GPN ",
    "view.boost.title2": "Game Boost",
    "view.boost.sub": "Low-latency routing for your games",
    "view.perf.title1": "Performance ",
    "view.perf.title2": "Monitor",
    "view.perf.sub": "Real-time metrics across your tunnel",
    "view.settings.title1": "Application ",
    "view.settings.title2": "Settings",
    "view.settings.sub": "Tune AO GPN to your liking",
    "view.about.title1": "About & ",
    "view.about.title2": "Help",
    "view.about.sub": "Program info, helpful links and feedback",
    "about.description": "Gaming Private Network — a modern VPN/GPN client with per-app game routing, 11 themes and a live connection monitor.",
    "about.thanks": "Thanks for using AO GPN!",
    "about.tabInfo": "Program Info",
    "about.tabHelp": "Help & Feedback",
    "about.version": "Version",
    "about.author": "Maintainer",
    "about.license": "License",
    "about.platform": "Platform",
    "about.cores": "Cores",
    "about.homepage": "Homepage / Wiki",
    "about.source": "Source code",
    "about.helpWiki": "Visit the Wiki",
    "about.helpWikiDesc": "Guides, configuration and usage documentation.",
    "about.helpBug": "Report a bug",
    "about.helpBugDesc": "Open a GitHub issue with a pre-filled template.",
    "about.helpFeature": "Request a feature",
    "about.helpFeatureDesc": "Suggest an idea for an upcoming version.",
    "about.helpTg": "Telegram group",
    "about.helpTgDesc": "Ask the community in real time.",
    "about.helpChannel": "Telegram channel",
    "about.helpChannelDesc": "Announcements and release notes.",
    "about.tabRelease": "Release Notes",
    "about.releaseIntro": "What's new in this version — every step that built it.",
    "about.releaseExpandAll": "Expand all",
    "about.releaseCollapseAll": "Collapse all",
    "about.releaseEmpty": "Could not load the release notes.",
    "connect.connect": "CONNECT",
    "connect.connected": "CONNECTED",
    "status.connected": "CONNECTED",
    "status.disconnected": "DISCONNECTED",
    "status.connecting": "CONNECTING…",
    "connect.connecting": "CONNECTING…",
    "mode.globalVpn": "Global VPN",
    "mode.gpn": "GPN Game Tunnel",
    "mode.active": "ACTIVE",
    "mode.primary": "PRIMARY",
    "dash.routeMode": "Route mode",
    "dash.capture": "Capture",
    "dash.protocolStrategy": "Protocol strategy",
    "dash.modeStrategy": "Connection mode",
    "dash.routeHintVpn": "Global VPN — all traffic is routed through the TUN tunnel. TUN captures every packet at the network layer.",
    "dash.routeHintGpn": "GPN Game Tunnel — only apps assigned in Game Boost are tunneled; all other traffic stays direct.",
    "transport.proxy": "Proxy",
    "transport.tun": "TUN",
    "transport.locked": "LOCKED",
    "transport.lockGlobalVpn": "Global VPN locks capture to TUN — every app is captured at the network layer so nothing can bypass the tunnel. Choose GPN Game Tunnel to pick Proxy capture.",
    "transport.hintProxy": "Proxy — sets the system proxy when connected; apps that honor it flow through the tunnel. No admin rights needed.",
    "transport.hintTun": "TUN — captures ALL network traffic at the network layer. Requires administrator privileges.",
    "transport.hintLocked": "TUN — captures ALL network traffic at the network layer, but requires administrator privileges. CONNECT is locked — relaunch as admin or switch to Proxy capture.",
    "protocol.auto": "Automatic",
    "protocol.recommended": "RECOMMENDED",
    "protocol.wireguard": "WireGuard",
    "protocol.mimic": "Mimic",
    "protocol.reality": "Reality/TLS",
    "protocol.hysteria2": "Hysteria2 / TUIC",
    "protocol.openvpn": "OpenVPN",
    "protocol.hint.auto": "Automatic keeps the selected profile and uses its native core; no credentials are converted.",
    "protocol.hint.wireguard": "WireGuard is fastest when a real WireGuard profile is available; it cannot be emulated from another node.",
    "protocol.hint.mimic": "Mimic uses Xray/sing-box Reality or TLS fingerprinting from a compatible profile.",
    "protocol.hint.hysteria2": "Hysteria2/TUIC uses QUIC and congestion control for high-loss or unstable paths.",
    "protocol.hint.openvpn": "OpenVPN uses the native provider profile and owns its TUN tunnel.",
    "stat.yourIp": "Your IP",
    "stat.node": "Node",
    "stat.session": "Session",
    "stat.verification": "Verification",
    "stat.ping": "Ping",
    "stat.packetLoss": "Packet Loss",
    "stat.download": "Download",
    "stat.upload": "Upload",
    "topbar.node": "Node",
    "topbar.loadingNodes": "Loading nodes…",
    "topbar.noNodes": "No nodes",
    "proxyOnly.noNode": "No node is selected for the system proxy — select a route in VPN Routes first",
    "proxyOnly.configFailed": "The system-proxy core configuration could not be created",
    "proxyOnly.unsupportedNode": "The selected route type does not support the system proxy",
    "proxyOnly.startFailed": "The system-proxy core could not be started",
    "proxyOnly.pending": "System proxy preference saved — it will be applied automatically when the core is ready",
    "proxyOnly.applied": "System proxy applied → {address}",
    "proxyOnly.appliedPac": "System proxy applied via PAC",
    "proxyOnly.unchanged": "System proxy left unchanged",
    "proxyOnly.cleared": "System proxy cleared",
    "proxyOnly.updateFailed": "System proxy update failed",
    "settings.language": "Language",
    "boost.title": "GPN Game Boost",
    "boost.full": "Full Game Boost",
    "gpn.connect": "GPN Connect",
    "gpn.connectSub": "Auto-select best Italy/Germany server",
    "gpn.servers.active": "Active",
    "gpn.servers.add": "Add",
    "gpn.servers.confirmDelete": "Delete this GPN server?",
    "gpn.servers.delete": "Delete",
    "gpn.servers.disable": "Disable",
    "gpn.servers.disabled": "Disabled",
    "gpn.servers.dpapiNote": "🔐 DPAPI protected",
    "gpn.servers.edit": "Edit",
    "gpn.servers.empty": "No GPN servers imported yet.",
    "gpn.servers.emptyConf": "Paste a WireGuard .conf first.",
    "gpn.servers.enable": "Enable",
    "gpn.servers.import": "Import",
    "gpn.servers.importDesc": "Paste an Italy/Germany WireGuard .conf below. The client private key is encrypted with Windows DPAPI before it touches disk.",
    "gpn.servers.importTitle": "Import WireGuard .conf",
    "gpn.servers.managed": "Managed servers",
    "gpn.servers.managedDesc": "Servers listed here are the Italy/Germany candidates auto-selected on GPN Connect. Disabled ones are skipped.",
    "gpn.servers.defaultsTitle": "Embedded defaults",
    "gpn.servers.restoreDefaults": "Restore defaults",
    "gpn.servers.defaultsEmpty": "No defaults status yet — open Server Management.",
    "gpn.servers.defaultsNotSeeded": "Not seeded",
    "gpn.servers.defaultsUpToDate": "Up to date",
    "gpn.servers.defaultsOutdated": "Outdated",
    "gpn.servers.defaultsNoKey": "no key",
    "gpn.servers.probe": "Measure",
    "gpn.servers.probeTitle": "Live latency · UDP path",
    "gpn.servers.refresh": "Refresh",
    "gpn.servers.title": "GPN Servers",
    "gpn.diag.title": "GPN diagnostics",
    "gpn.diag.empty": "No GPN diagnostics yet — connect to see the live pipeline.",
    "gpn.diag.clear": "Clear",
    "gpn.recovery.title": "Auto-recover after V2ray",
    "gpn.recovery.sub": "After a V2rayTCP fallback, watch for a healthy server and return to WireGuard automatically.",
    "gpn.failover.title": "Stable connection (no auto switch)",
    "gpn.failover.sub": "OFF = stick to the chosen server for the most stable, uninterrupted connection. ON = auto-switch/failover allowed.",
    "gpn.telemetry.title": "Failover telemetry",
    "gpn.telemetry.reset": "Reset",
    "gpn.telemetry.switchShort": "switch",
    "gpn.telemetry.deathShort": "death",
    "gpn.telemetry.fallbackShort": "fallback",
    "gpn.telemetry.recoverShort": "recover",
    "gpn.telemetry.selectShort": "select",
    "gpn.reslog.title": "Last 50 decisions",
    "gpn.reslog.empty": "No decisions recorded yet.",
    "gpn.reslog.refresh": "Refresh",
    "gpn.reslog.clear": "Clear",
    "gpn.reslog.path": "Mirrored to",
    "gpn.reslog.action.switch": "Server switch",
    "gpn.reslog.action.udpDeath": "UDP dead",
    "gpn.reslog.action.modeFallback": "Fallback",
    "gpn.reslog.action.recover": "Recovery",
    "gpn.reslog.action.select": "Selected",
    "gpn.cluster.title": "Server cluster",
    "gpn.cluster.measure": "Measure",
    "gpn.cluster.empty": "No active servers — GPN Connect will not auto-select.",
    "gpn.cluster.best": "best",
    "gpn.cluster.open": "open",
    "gpn.cluster.egressHintLabel": "physical NIC",
    "gpn.cluster.egressHint": "Tunnel active — ICMP is skipped and latency is measured over the physical NIC (UDP handshake RTT), so the values show real server reachability, not the tunnel path.",
    "gpn.pidpool.title": "PID pool",
    "gpn.pidpool.watching": "watching",
    "gpn.pidpool.idle": "idle",
    "gpn.pidpool.pids": "PIDs",
    "gpn.pidpool.offline": "Target offline — no PIDs yet",
    "gpn.pidpool.offlineShort": "offline",
    "gpn.pidpool.empty": "No vpn-routed game yet — assign a game in Game Boost to build the pool.",
    "gpn.pidpool.start": "Watch",
    "gpn.pidpool.stop": "Stop",
    "gpn.pidpool.refresh": "Refresh",
    "gpn.pidpool.fatal": "fatal",
    "gpn.pidpool.degraded": "degraded",
    "gpn.capture.title": "Captured traffic",
    "gpn.capture.empty": "No packets captured yet — GPN not connected or no game traffic.",
    "gpn.capture.flows": "Top flows",
    "gpn.capture.perPid": "Per-PID",
    "gpn.captureSet.title": "WinDivert queue",
    "gpn.captureSet.save": "Apply",
    "gpn.captureSet.queueLen": "Queue len",
    "gpn.captureSet.queueTime": "Queue time (ms)",
    "gpn.captureSet.queueSize": "Queue size (B)",
    "gpn.captureSet.enableLen": "len",
    "gpn.captureSet.enableTime": "time",
    "gpn.captureSet.enableSize": "size",
    "gpn.captureSet.layer": "Layer",
    "gpn.captureSet.direction": "Direction",
    "gpn.captureSet.hint": "Values apply to the next capture start; the running loop keeps its current parameters.",
    "gpn.wintunSet.title": "Wintun adapter",
    "gpn.wintunSet.save": "Apply",
    "gpn.wintunSet.adapter": "Adapter name prefix",
    "gpn.wintunSet.ring": "Ring capacity (B)",
    "gpn.wintunSet.hint": "Applies to the next WireGuard connect; the server id is appended to the adapter name (AoGPN-it). Ring capacity is clamped to 128 KiB–64 MiB and rounded to a power of two.",
    "gpn.capture.out": "out",
    "gpn.capture.in": "in",
    "gpn.capture.unknown": "unknown PID",
    "gpn.matrix.title": "Failover matrix",
    "gpn.matrix.empty": "No measurement yet — run a server probe.",
    "gpn.matrix.default": "Default",
    "gpn.matrix.strict": "Strict",
    "gpn.matrix.keep": "Keep",
    "gpn.matrix.switchTo": "Switch →",
    "gpn.matrix.fallback": "Fallback V2rayTCP",
    "gpn.matrix.legend": "✔ healthy · ✘ dead · first column default, second strict",
    "gpn.candidate.title": "Best candidate",
    "gpn.candidate.empty": "No measurement yet — run a server probe.",
    "gpn.candidate.wireguard": "WireGuard · Tier 2",
    "gpn.candidate.v2ray": "V2rayTCP · Tier 3",
    "gpn.candidate.selected": "will connect",
    "gpn.candidate.none": "No WireGuard server selectable",
    "gpn.candidate.hint": "What GPN Connect will pick right now — same decision logic, no tunnel started.",
    "nav.gpnServers": "GPN Servers",
    "view.gpnServers.sub": "Manage the Italy/Germany WireGuard servers auto-selected on GPN Connect",
    "view.gpnServers.title1": "GPN ",
    "view.gpnServers.title2": "Servers",
    "boost.loading": "Loading…",
    "boost.noGames": "No games assigned yet. Add EXE files in the {link} view to enable per-game tunnel routing.",
    "boost.noGamesShort": "No games assigned yet. Add apps in the Game Boost view.",
    "boost.running": "{n} games boosted",
    "boost.defined": "{n} apps defined",
    "boost.before": "Before",
    "boost.after": "After",
    "boost.viaNode": "via {node}",
    "boost.none": "No games defined",
    "boost.routeActive": "Route: {route} · active",
    "boost.idleNoBoost": "Idle · no boost",
    "global.allTraffic": "All Traffic Tunnelled",
    "global.transport": "Transport",
    "global.modeActive": "mode active",
    "global.ipVerification": "IP Verification",
    "global.coverage": "Coverage",
    "global.coverage100": "100% of traffic",
    "global.transportDesc": "All network traffic is captured at the network layer and routed through the VPN tunnel.",
    "global.coverageDesc": "All applications, services, and system processes route through the secure tunnel. No traffic bypasses the VPN.",
    "global.notConnected": "⚪ Not connected",
    "global.disconnectedDesc": "Disconnected — all traffic uses your direct internet connection.",
    "global.tunnelVerified": "✅ Tunnel verified",
    "global.tunnelVerifiedDesc": "IP changed to {ip}. Traffic is securely routed.",
    "global.leaking": "⚠️ Leaking",
    "global.leakingDesc": "IP unchanged — {ip}. Check transport settings.",
    "global.probing": "🔄 Probing…",
    "global.probingDesc": "Verifying tunnel exit IP.",
    "global.disconnected": "Disconnected",
    "dir.title": "Routing direction",
    "dir.whitelist": "Whitelist",
    "dir.blacklist": "Blacklist",
    "dir.hintWhitelist": "Whitelist — only assigned games/apps are tunneled; all other traffic stays direct.",
    "dir.hintBlacklist": "Blacklist — assigned games/apps stay direct; all other traffic is tunneled.",
    "dir.blacklistTableHint": "Blacklist active — assigned apps stay direct; everything else is tunneled. A 'VPN' assignment keeps an app OUTSIDE the tunnel; 'Direct' tunnels it. The route column shows the effective route.",
    "dir.rowExcluded": "Outside tunnel",
    "dir.rowTunneled": "Tunneled",
    "dir.tipRowExcluded": "Blacklist: a 'VPN' assignment keeps this app OUTSIDE the tunnel — it goes direct.",
    "dir.tipRowTunneled": "Blacklist: a 'Direct' assignment routes this app INTO the tunnel.",
    "dir.selectVpn": "VPN · outside tunnel",
    "dir.selectDirect": "Direct · tunneled",
    "dir.selectBlock": "Block",
    "dir.selectWarp": "WARP · outside tunnel",
    "tip.dirWhitelist": "Whitelist — only the games/apps assigned in Game Boost are routed through the tunnel; everything else stays direct.",
    "tip.dirBlacklist": "Blacklist — everything except the games/apps assigned in Game Boost is routed through the tunnel.",
    "route.assign": "Assign route…",
    "route.vpn": "VPN",
    "route.direct": "Direct",
    "route.block": "Block",
    "route.warp": "WARP",
    "warp.fallbackNotice": "WARP route needs a WireGuard node — the active connection is not WireGuard, so WARP apps are routed through VPN.",
    "nodes.title": "VPN Routes",
    "nodes.countryUnknown": "Other",
    "nodes.showAll": "Show all",
    "nodes.hideAll": "Hide all",
    "nodes.showAll": "Show all",
    "nodes.hideAll": "Hide all",

    "nodes.selectedNode": "Selected node:",
    "nodes.sortDefault": "Default order",
    "nodes.sortCountry": "Country A-Z",
    "nodes.sortFav": "Favorites first",
    "nodes.sortRecent": "Recently used",
    "nodes.pool": "Node pool",
    "nodes.poolDesc": "Add GitHub / .txt links that publish node lists — one click pulls them into your nodes.",
    "nodes.poolFetch": "Download new nodes",
    "nodes.poolAdd": "Add link",
    "nodes.poolRemove": "Remove",
    "nodes.poolEmpty": "No links in the pool yet.",
    "settings.title": "Application Settings",
    "settings.save": "Save Settings",
    "monitor.title": "Connection Monitor",
    "monitor.connections": "Connections",
    "monitor.activeSockets": "active sockets",
    "status.idle": "⚪ Idle",
    "status.notConnected": "not connected",
    "status.tunneled": "✅ Tunneled",
    "status.probing": "🔄 Probing…",
    "status.retrying": "🔄 Retrying…",
    "status.leaking": "⚠️ Leaking",
    "status.checking": "Checking",
    "ip.tunnel": "tunnel",
    "ip.isp": "ISP",
    "ip.leaking": "leaking!",
    "ip.probing": "probing tunnel…",
    "ip.pending": "IP check pending…",
    "ip.detail.tunActive": "TUN active",
    "ip.detail.socksVerified": "SOCKS5 verified",
    "ip.detail.fetchingBaseline": "fetching ISP baseline…",
    "ip.detail.willRetry": "tunnel probe pending, will retry",
    "ip.detail.possibleLeak": "IP unchanged — possible leak",
    "ip.measuredAgo": "measured {s}s ago",
    "ip.detail.staleMeasure": "Stale measurement — re-checking",
    "telemetry.measuring": "measuring…",
    "telemetry.stable": "stable",
    "status.line.live": "Live — Route {route} · Capture {capture}. The tunnel is active.",
    "status.line.locked": "TUN requires administrator privileges — CONNECT is locked. Relaunch as admin or switch to {capture} capture.",
    "status.line.idle": "Route {route} · Capture {capture} — press {connect} to start the tunnel.",
    "footer.brand": "AO GPN · Gaming Private Network",
    "telemetry.excellent": "excellent",
    "telemetry.good": "good",
    "telemetry.warning": "warning",
    "ip.leakBanner": "You are connected but your public IP has not changed ({ip}) — traffic may be leaking. Check your capture settings or switch to TUN.",
    "warp.healthBanner": "WARP outbound dial is failing — launcher/regional traffic may not pass. Check the diag log for WARP_DIAL lines.",
    "warp.degradedBanner": "WARP is down — launcher/API traffic is now going DIRECT until WARP recovers.",
    "warp.degradedTag": "Launcher direct",
    "tip.modeGpn": "GPN Game Tunnel — only assigned games and apps are tunneled; everything else stays direct.",
    "tip.modeVpn": "Global VPN — all traffic is routed through the tunnel using TUN capture.",
    "tip.transportProxy": "Proxy — apps that honor the system proxy flow through the tunnel. No admin rights needed.",
    "tip.transportTun": "TUN — captures all network traffic at the network layer. Requires administrator privileges.",
    "tip.protocolAuto": "Automatic — uses the selected profile’s native core; no credentials are converted.",
    "tip.protocolWireguard": "WireGuard — fastest with a real WireGuard profile; cannot be emulated from another node.",
    "tip.protocolMimic": "Mimic — Reality/TLS browser fingerprinting from a compatible Xray/sing-box profile.",
    "tip.protocolHysteria2": "Hysteria2 / TUIC — QUIC and congestion control for high-loss or unstable paths.",
    "tip.protocolOpenvpn": "OpenVPN — uses the native provider profile and owns its TUN tunnel.",
    "tip.proxyControl": "System proxy — toggles the OS proxy (127.0.0.1:port) on or off, independent of the VPN tunnel.",
    "tip.proxyMode": "Proxy mode — Clear (off) · Set (on) · Unchanged · PAC.",
    "tip.proxyTest": "Test — verifies the local SOCKS5 listener and shows its latency.",
    "tip.nodeSelect": "Node — the tunnel routes through the selected server. Manage nodes in the Nodes view.",
    "tip.connectIdle": "Connect — route {route} via {capture} capture through the selected node.",
    "tip.connectActive": "Connected — press to disconnect. The system proxy returns to your independent preference.",
    "tip.connectLocked": "TUN requires administrator privileges — CONNECT is locked. Relaunch as admin or switch to Proxy capture.",
    "tip.statusIdle": "No active tunnel — press CONNECT to route traffic through the selected node.",
    "tip.statusActive": "Tunnel active — {route} via {capture} capture.",
    "theme.arctic-ice": "Arctic Ice",
    "theme.arctic-ice.tagline": "Glacial arctic ice",
    "theme.aurora": "Aurora",
    "theme.aurora.tagline": "Warm copper over deep teal",
    "theme.candy": "Candy",
    "theme.candy.tagline": "Playful rose and mint candy",
    "theme.crimson": "Crimson",
    "theme.crimson-core": "Crimson Core",
    "theme.crimson-core.tagline": "Reactor crimson core",
    "theme.crimson.tagline": "Tactical redline, black ops",
    "theme.cryo": "Cryo",
    "theme.cryo.tagline": "Glacial blue, frost indigo",
    "theme.cyberpunk": "Cyberpunk",
    "theme.cyberpunk.tagline": "Amber neon, crimson haze",
    "theme.gold-elite": "Gold Elite",
    "theme.gold-elite.tagline": "Premium gold elite",
    "theme.inferno": "Inferno",
    "theme.inferno.tagline": "Ember red, molten amber",
    "theme.matrix": "Matrix",
    "theme.matrix-green": "Matrix Green",
    "theme.matrix-green.tagline": "Terminal phosphor green",
    "theme.matrix.tagline": "Phosphor green, dark terminal",
    "theme.nebula": "Nebula",
    "theme.nebula.tagline": "Deep space violet aurora",
    "theme.neon-cyber": "Neon Cyber",
    "theme.neon-cyber.tagline": "Neon cyan over electric violet",
    "theme.obsidian": "Obsidian",
    "theme.obsidian.tagline": "Monochrome slate and steel",
    "theme.ocean-blue": "Ocean Blue",
    "theme.ocean-blue.tagline": "Deep ocean azure",
    "theme.phantom": "Phantom",
    "theme.phantom.tagline": "Spectral indigo, misty white",
    "theme.plasma": "Plasma",
    "theme.plasma.tagline": "Electric violet, deep blue",
    "theme.red-phantom": "Red Phantom",
    "theme.red-phantom.tagline": "Aggressive competitive red",
    "theme.sandstorm": "Sandstorm",
    "theme.sandstorm.tagline": "Dune sand and oasis teal",
    "theme.stealth-camo": "Stealth Camo",
    "theme.stealth-camo.tagline": "Tactical stealth camo",
    "theme.synthwave": "Synthwave",
    "theme.synthwave.tagline": "Neon magenta, retro rose",
    "theme.titanium-orange": "Titanium Orange",
    "theme.titanium-orange.tagline": "Molten titanium orange",
    "theme.velocity": "Velocity",
    "theme.velocity.tagline": "High-speed redline telemetry",
    "theme.venom": "Venom",
    "theme.venom.tagline": "Toxic lime, bio emerald",
    "theme.violet-nova": "Violet Nova",
    "theme.violet-nova.tagline": "Luxury violet nova",
  };
  let _dict = {};
  let _currentLang = 'en';

  // Shared translation core for the dashboard t() and skinBridge.t(): resolve a
  // key in the loaded dictionary, then the fallback dictionary, apply {param}
  // interpolation and stringify. Returns null when nothing resolved so each
  // caller decides its own fallback (the dashboard returns the raw key; the
  // skin bridge walks skin-scoped keys first).
  function resolveKey(key, params) {
    let val = null;
    if (_dict && Object.prototype.hasOwnProperty.call(_dict, key)) val = _dict[key];
    if ((val === null || val === undefined || val === '') && _fallbackDict && Object.prototype.hasOwnProperty.call(_fallbackDict, key)) val = _fallbackDict[key];
    if (val === null || val === undefined || val === '') return null;
    if (params) {
      Object.keys(params).forEach(k => { val = String(val).split('{' + k + '}').join(String(params[k])); });
    }
    return String(val);
  }

  function t(key, params) {
    const val = resolveKey(key, params);
    return val === null ? key : val;
  }

  function applyTexts() {
    document.querySelectorAll('[data-i18n]').forEach(el => {
      const key = el.getAttribute('data-i18n');
      const val = t(key);
      if (!val || val === key) return;
      // Replace text nodes in the subtree but keep SVG icons and nested elements
      // (spans, badges) so structure and styling survive translation.
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
        acceptNode(n) {
          const p = n.parentElement;
          if (p && p.closest && p.closest('svg,script,style')) return NodeFilter.FILTER_REJECT;
          return NodeFilter.FILTER_ACCEPT;
        }
      });
      let replaced = false;
      while (walker.nextNode()) {
        const node = walker.currentNode;
        if (node.nodeValue && node.nodeValue.trim() !== '') {
          node.nodeValue = val;
          replaced = true;
        }
      }
      if (!replaced && el.children.length === 0) el.textContent = val;
    });
  }

  async function loadLanguage(lang) {
    _currentLang = (lang && String(lang)) || 'en';
    let enDict = null, langDict = null;
    try {
      const r1 = await fetch('Dil/en.json', { cache: 'no-store' });
      if (r1.ok) enDict = await r1.json();
    } catch (e) {}
    if (_currentLang !== 'en') {
      try {
        const r2 = await fetch('Dil/' + _currentLang + '.json', { cache: 'no-store' });
        if (r2.ok) langDict = await r2.json();
      } catch (e) {}
    }
    _dict = Object.assign({}, (enDict && typeof enDict === 'object') ? enDict : {}, (langDict && typeof langDict === 'object') ? langDict : {});
    applyTexts();
    refreshDynamicTexts();
    // Standalone skins resolve their own texts through skinBridge.t(); a
    // language switch must re-render them instantly, like the main dashboard.
    aogpn.events.emit('skin-changed');
    return _dict;
  }

  // Re-applies strings written by JS from the current state so a language switch
  // updates them instantly (no restart), like the theme picker.
  function refreshDynamicTexts() {
    if (typeof refreshConnectLabels === 'function') refreshConnectLabels();
    const app = aogpn.app;
    if (app && typeof app.applyMode === 'function') app.applyMode();
    if (app && typeof app.renderTopNodeSelect === 'function') app.renderTopNodeSelect();
    if (app && typeof app.updateTransportLock === 'function') app.updateTransportLock();
    if (app && typeof app.applyProtocolPreference === 'function') app.applyProtocolPreference(app.getProtocolPreference());
    const ipState = app && app.getLastIpState();
    if (ipState && typeof app.applyRealIpState === 'function') app.applyRealIpState(ipState);
    const lastTel = app && app.getLastTelemetry();
    if (lastTel && typeof app.updateTelemetry === 'function') app.updateTelemetry(lastTel[0], lastTel[1], lastTel[2], lastTel[3]);
    if (app && typeof app.renderDashboardBoostCards === 'function') app.renderDashboardBoostCards();
  }

  function refreshConnectLabels() {
    // Bağlantı durumu app.js'de yaşar (koordinatör kapsamı) — modül buraya
    // lazily erişir (aogpn.app, boot sırasında app.js tarafından kaydedilir).
    const app = aogpn.app;
    const mode = app && typeof app.getMode === 'function' ? app.getMode() : 'gpn';
    const transport = app && typeof app.getTransport === 'function' ? app.getTransport() : 'proxy';
    const connected = app && typeof app.getConnected === 'function' ? app.getConnected() : false;
    const routeLabel = t(mode === 'gpn' ? 'mode.gpn' : 'mode.globalVpn');
    const captureLabel = transport === 'tun' ? t('transport.tun') : t('transport.proxy');
    if (statusLabel) statusLabel.textContent = connected ? t('status.connected') : t('status.disconnected');
    if (btnText) btnText.textContent = connected ? t('connect.connected') : t('connect.connect');
    if (btnSub) btnSub.textContent = routeLabel + (connected ? ' · ' + t('mode.active') : '');
    if (app && typeof app.updateStatusLine === 'function') app.updateStatusLine();
  }

  // Available languages in the same order the old native select listed them.
  const LANGUAGES = [
    { code: 'zh-Hans', name: '简体中文' },
    { code: 'zh-Hant', name: '繁體中文' },
    { code: 'en', name: 'English' },
    { code: 'fa', name: 'فارسی' },
    { code: 'fr', name: 'Français' },
    { code: 'hu', name: 'Magyar' },
    { code: 'id', name: 'Indonesia' },
    { code: 'ru', name: 'Русский' },
    { code: 'tr', name: 'Türkçe' }
  ];

  function langName(code) {
    const found = LANGUAGES.find(l => l.code === code);
    return found ? found.name : code;
  }

  // ISO 3166-1 alpha-2 ülke kodunu (örn. "IT") kullanıcının dilinde ülke adına
  // çevirir (tr'de "İtalya", en'de "Italy"). Intl.DisplayNames tüm kodları ve
  // tüm dilleri kapsar — ayrı bir sözlük gerektirmez. Kod tanınmıyorsa veya API
  // yoksa boş döner; çağıran taraf koda düşer.
  function countryDisplayName(code) {
    if (!code) return '';
    try {
      const loc = (_currentLang && _currentLang !== 'en') ? _currentLang : 'en';
      const upper = String(code).toUpperCase();
      const name = new Intl.DisplayNames([loc], { type: 'region' }).of(upper);
      return (name && name !== upper) ? name : '';
    } catch (e) {
      return '';
    }
  }

  function renderLangPopover() {
    const pop = document.getElementById('dashboardLangPopover');
    if (!pop) {
      return;
    }
    const current = _currentLang || 'en';
    const label = document.getElementById('dashboardLangLabel');
    if (label) {
      label.textContent = langName(current);
    }
    pop.innerHTML = LANGUAGES.map(l => {
      const on = l.code === current;
      return `<button type="button" data-lang="${l.code}" role="option" aria-selected="${on}"
        class="w-full flex items-center justify-between gap-2 rounded-lg px-3 py-1.5 text-[11px] font-semibold transition-colors ${on ? 'bg-white/8 text-white' : 'text-[#A7B0BF] hover:bg-white/5 hover:text-slate-200'}">
        <span>${escHtml(l.name)}</span>
        ${on ? '<svg class="w-3.5 h-3.5 text-cyan-300 shrink-0" fill="currentColor" viewBox="0 0 24 24"><path d="M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41Z"/></svg>' : ''}
      </button>`;
    }).join('');
  }

  // The host calls this through CoreWebView2.ExecuteScriptAsync and the dropdown
  // uses it directly, so selecting a language applies it immediately.
  const applyLanguage = function(lang) {
    loadLanguage(lang);
    const label = document.getElementById('dashboardLangLabel');
    if (label) {
      label.textContent = langName(lang);
    }
    renderLangPopover();
    // Theme tooltips carry translated names/taglines, so re-render them so a
    // language switch updates the picker immediately.
    if (aogpn.theme && typeof aogpn.theme.renderTopThemePopover === 'function') aogpn.theme.renderTopThemePopover();
  };
  window.applyLanguage = applyLanguage;

  // Wire the custom language dropdown: apply instantly (like themes) and persist
  // to the host. The native <select> popup clashed with the dark theme, so this
  // is a themed popover identical in behaviour, plus full keyboard support:
  //   • ↓/↑ open (when closed) and move between options (when open)
  //   • Enter/Space select the focused option
  //   • Esc closes and returns focus to the trigger
  //   • Home/End jump to the first/last option
  const langBtn = document.getElementById('dashboardLangBtn');
  const langPop = document.getElementById('dashboardLangPopover');
  if (langBtn && langPop) {
    function openLangPop() {
      renderLangPopover();
      langPop.classList.remove('hidden');
      langBtn.setAttribute('aria-expanded', 'true');
      // Keyboard users start from the currently selected language so ↓/↑ move
      // relative to it and Enter re-picks it without a mouse.
      const sel = langPop.querySelector('[aria-selected="true"]') || langPop.querySelector('[data-lang]');
      if (sel) sel.focus();
    }
    function closeLangPop() {
      langPop.classList.add('hidden');
      langBtn.setAttribute('aria-expanded', 'false');
    }
    function langOptions() {
      return [...langPop.querySelectorAll('[data-lang]')];
    }
    function moveLangFocus(step) {
      const opts = langOptions();
      if (!opts.length) return;
      const i = opts.indexOf(document.activeElement);
      const next = step === 'down' ? (i + 1) % opts.length : (i - 1 + opts.length) % opts.length;
      opts[next].focus();
    }
    function selectLangFocus() {
      const active = document.activeElement && document.activeElement.closest('[data-lang]');
      const code = active ? active.dataset.lang : (langPop.querySelector('[aria-selected="true"]') || {}).dataset;
      if (code) {
        applyLanguage(code);
        postToHost({ action: 'set_language', lang: code });
        closeLangPop();
        langBtn.focus();
      }
    }
    // Shared key map for both the trigger button and the popover options:
    // ↓/↑ move, Home/End jump, Esc closes, Enter/Space selects.
    function handleLangKeys(e) {
      if (e.key === 'ArrowDown') { e.preventDefault(); moveLangFocus('down'); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); moveLangFocus('up'); }
      else if (e.key === 'Home') { e.preventDefault(); const o = langOptions(); if (o.length) o[0].focus(); }
      else if (e.key === 'End') { e.preventDefault(); const o = langOptions(); if (o.length) o[o.length - 1].focus(); }
      else if (e.key === 'Escape') { e.preventDefault(); closeLangPop(); langBtn.focus(); }
      else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectLangFocus(); }
    }
    langBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (langPop.classList.contains('hidden')) openLangPop(); else closeLangPop();
    });
    langBtn.addEventListener('keydown', (e) => {
      if (langPop.classList.contains('hidden')) {
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Enter' || e.key === ' ') {
          // preventDefault stops the synthetic click, so the popover stays open
          // after the keyboard opens it (no double-toggle).
          e.preventDefault();
          openLangPop();
        }
        return;
      }
      handleLangKeys(e);
    });
    // Key events on the options bubble up here; preventDefault stops the option's
    // synthetic click so selection happens exactly once.
    langPop.addEventListener('keydown', (e) => {
      handleLangKeys(e);
    });
    document.addEventListener('click', (e) => {
      if (!langPop.classList.contains('hidden') && !langPop.contains(e.target) && e.target !== langBtn && !langBtn.contains(e.target)) {
        closeLangPop();
      }
    });
    langPop.addEventListener('click', (e) => {
      const btn = e.target.closest('[data-lang]');
      if (!btn) {
        return;
      }
      const newLang = btn.dataset.lang;
      applyLanguage(newLang);
      postToHost({ action: 'set_language', lang: newLang });
      closeLangPop();
      langBtn.focus();
    });
  }

  // ---------------------------------------------------------------------------

  window.aogpn = window.aogpn || {};
  window.aogpn.i18n = {
    resolveKey, t, applyTexts, loadLanguage, refreshDynamicTexts, refreshConnectLabels,
    LANGUAGES, langName, countryDisplayName, renderLangPopover, applyLanguage,
    getLang: () => _currentLang,
    getDict: () => _dict
  };
})();
