// ================= NEXUS GPN skin bridge (standalone iframe) =================
// This script runs inside the NEXUS skin document (skins/nexus/skin.html),
// which the main dashboard embeds in an isolated <iframe>. The skin never
// touches the main document's DOM or theme — it talks to the host through the
// parent's window.skinBridge API (live getters + host actions). Each skin is
// therefore a completely independent dashboard: its own markup, styles and
// logic, plugged into the same live state the standard dashboard uses.
//
// When the page is opened standalone (no parent bridge), a small demo state
// keeps the design viewable for styling work.
(function () {
  'use strict';

  const B = (window.parent && window.parent !== window && window.parent.skinBridge) ? window.parent.skinBridge : null;

  // When this skin is loaded as a manager thumbnail (src includes ?preview=1),
  // render the design once but do NOT attach the live bridge subscription or the
  // safety poll — a static snapshot is enough for a thumbnail and avoids running
  // a second live copy of a heavy skin (which could block the main thread).
  const NX_PREVIEW = /[?&]preview=1/.test(window.location.search || '');

  const DEMO = {
    connected: false,
    connecting: false,
    mode: 'gpn',
    transport: 'proxy',
    splitMode: 'off',
    autoReconnect: true,
    systemProxyMode: 0,
    protocolPreference: 'auto',
    useRealNodes: false,
    activeRealNodeId: null,
    selectedNode: null,
    monitorSnapshot: { apps: [] },
    routeLabels: {},
    nodes: [
      { key: 'istanbul', code: 'TR', name: 'Istanbul · TR-01', addr: '145.239.14.77', ping: 18 },
      { key: 'frankfurt', code: 'DE', name: 'Frankfurt · DE-11', addr: '5.161.91.19', ping: 148 },
      { key: 'amsterdam', code: 'NL', name: 'Amsterdam · NL-02', addr: '51.15.118.35', ping: 171 },
      { key: 'london', code: 'GB', name: 'London · UK-07', addr: '51.36.10.188', ping: 167 }
    ],
    telemetry: [],
    language: 'en',
    isAdmin: true
  };

  const G = (name) => {
    if (B) { try { return B[name]; } catch (e) { /* fall through */ } }
    return DEMO[name];
  };
  const SET = (name, value) => {
    if (B) { try { B[name] = value; } catch (e) { /* read-only */ } }
  };

  const escHtml = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  // ---------- i18n ----------
  // Every UI string resolves through the bridge's t(): Dil/*.json keys are
  // looked up under 'skin.<skinId>.<key>' first, then the shared keys. When the
  // skin is opened standalone (no parent bridge), NX_TEXT below supplies the
  // English defaults so the design stays fully editable.
  const NX_TEXT = {
    'brand': 'AO GPN',
    'brandSub': 'GAMING PRIVATE NETWORK',
    'section.network': 'Network',
    'nav.dashboard': 'Dashboard',
    'nav.route': 'Smart Route',
    'nav.servers': 'Servers',
    'nav.games': 'Game Profiles',
    'section.system': 'System',
    'nav.analytics': 'Analytics',
    'nav.settings': 'Settings',
    'profile.name': 'AO GPN PLAYER',
    'profile.role': 'PRO MEMBER',
    'eyebrow': 'GPN CONTROL CENTER',
    'title.prefix': 'NETWORK',
    'title.dashboard': 'DASHBOARD',
    'title.route': 'SMART ROUTE',
    'title.servers': 'SERVERS',
    'title.games': 'GAME PROFILES',
    'title.analytics': 'ANALYTICS',
    'title.settings': 'SETTINGS',
    'tunnel': 'Gaming tunnel',
    'route.prefix': 'ROUTE',
    'route.gaming': 'GAMING ROUTE',
    'route.global': 'GLOBAL VPN',
    'routeState.optimized': 'OPTIMIZED',
    'routeState.ready': 'READY',
    'you': 'YOU',
    'local': 'Local',
    'gameServer': 'GAME SERVER',
    'stat.ping': 'AVG PING',
    'stat.jitter': 'JITTER',
    'stat.loss': 'PACKET LOSS',
    'stat.measuring': 'MEASURING',
    'stat.stable': 'STABLE',
    'stat.excellent': 'EXCELLENT',
    'state.connected': 'CONNECTED',
    'state.connecting': 'CONNECTING…',
    'state.connect': 'CONNECT',
    'substate.active': 'GPN ROUTE ACTIVE',
    'substate.starting': 'STARTING…',
    'substate.idle': 'START GPN SESSION',
    'status.connected': 'NETWORK CONNECTED · SECURE',
    'status.connecting': 'CONNECTING…',
    'status.online': 'NETWORK ONLINE · READY',
    'activeRoute': 'Active route',
    'bestPath': 'BEST PATH',
    'gameProfiles': 'Game profiles',
    'telemetry': 'Latency telemetry',
    'telemetryLast': 'LAST 60 SECONDS',
    'protection': 'Protection',
    'active': 'ACTIVE',
    'secured': 'Gaming tunnel secured',
    'securedDesc': 'Traffic routing is ready for your selected games.',
    'smartRoute': 'Smart Route',
    'smartRouteSub': 'Connection strategy',
    'servers': 'Servers',
    'serversSub': 'Choose your route endpoint',
    'gameProfilesTitle': 'Game Profiles',
    'gameProfilesSub': 'Boost-tunneled games & apps',
    'analytics': 'Analytics',
    'analyticsSub': 'Live network telemetry',
    'settings': 'Settings',
    'settingsSub': 'Preferences',
    'routeMode': 'Route mode',
    'modeGpn': 'GPN Game Tunnel',
    'modeGlobal': 'VPN',
    'capture': 'Capture',
    'transportProxy': 'Proxy',
    'transportTun': 'TUN',
    'loadingNodes': 'Loading nodes…',
    'noServers': 'No servers available yet.',
    'nodes.sortBy': 'Sort',
    'nodes.pool': 'Node pool',
    'nodes.poolDesc': 'Add GitHub / .txt links that publish node lists — one click pulls them into your nodes.',
    'nodes.poolEmpty': 'No links in the pool yet.',
    'nodes.poolAdd': 'Add link',
    'nodes.poolFetch': 'Download new nodes',
    'nodes.poolEdit': 'Edit',
    'nodes.poolRemove': 'Remove',
    'nodes.poolSave': 'Save',
    'nodes.poolCancel': 'Cancel',
    'nodes.sortDefault': 'Default order',
    'nodes.sortCountry': 'Country A-Z',
    'nodes.sortFav': 'Favorites first',
    'nodes.sortPing': 'Ping: low to high',
    'nodes.sortPingDesc': 'Ping: high to low',
    'nodes.sortRecent': 'Recently used',
    'undo.remove': '{name} removed',
    'undo.route': 'Route changed for {name}',
    'undo.add': '{name} added',
    'undo.action': 'Undo',
    'undo.dismiss': 'Dismiss',
    'monitor.title': 'Connection Monitor',
    'monitor.connections': 'Connections',
    'monitor.apps': 'Applications',
    'monitor.filter': 'Filter app, address or country…',
    'monitor.hideListeners': 'Hide listeners',
    'monitor.view.empty': 'No visible connections.',
    'monitor.view.groupNone': 'None',
    'monitor.view.groupRoute': 'Route',
    'monitor.view.groupProtocol': 'Protocol',
    'monitor.view.groupState': 'State',
    'monitor.view.groupCountry': 'Country',
    'monitor.view.groupApp': 'Application',
    'noGames': 'No GPN game assigned yet — add one from Game Boost.',
    'noBoostApps': 'No boost-tunneled apps yet — add one from Game Boost in the main dashboard.',
    'kind.vpn': 'VPN',
    'kind.tunneled': 'Tunneled',
    'running': 'RUNNING',
    'srvActive': 'ACTIVE',
    'routeHint': 'Route mode: {mode} · Capture: {capture} · {state}',
    'hint.live': 'Live',
    'hint.idle': 'Idle',
    'captureHint.tun': 'TUN (network layer)',
    'captureHint.proxy': 'Proxy (application layer)',
    'ana.signalHealth': 'Signal health',
    'ana.live': 'LIVE',
    'ana.idle': 'IDLE',
    'ana.session': 'Session',
    'ana.exitIp': 'Exit IP',
    'ana.ping': 'Ping',
    'ana.jitter': 'Jitter (est.)',
    'ana.loss': 'Packet loss',
    'ana.download': 'Download',
    'ana.upload': 'Upload',
    'ana.route': 'Route',
    'ana.gpn': 'GPN',
    'ana.global': 'GLOBAL',
    'setting.routeMode': 'Route mode',
    'setting.routeModeDesc': 'GPN Game Tunnel or VPN',
    'setting.capture': 'Capture',
    'setting.captureDesc': 'Proxy or TUN at the network layer',
    'setting.split': 'Split tunneling',
    'setting.splitDesc': 'Which traffic uses the tunnel',
    'setting.sysproxy': 'System proxy',
    'setting.sysproxyDesc': 'Independent OS proxy preference',
    'setting.protocol': 'Protocol priority',
    'setting.protocolDesc': 'Preferred handshake protocol',
    'setting.autoReconnect': 'Auto reconnect',
    'setting.autoReconnectDesc': 'Reconnect on drop',
    'setting.language': 'Language',
    'setting.languageDesc': 'Interface language',
    'setting.splitDir': 'Split direction',
    'setting.splitDirDesc': 'Which traffic the tunnel captures',
    'setting.sysproxyToggle': 'System proxy switch',
    'setting.sysproxyToggleDesc': 'Independent OS proxy on/off',
    'dir.whitelist': 'Whitelist',
    'dir.blacklist': 'Blacklist',
    'proxy.on': 'PROXY ON',
    'proxy.off': 'PROXY OFF',
    'opt.off': 'Off',
    'opt.globalSplit': 'VPN split',
    'opt.gpnSplit': 'GPN split',
    'opt.clear': 'Clear',
    'opt.set': 'Set',
    'opt.unchanged': 'Unchanged',
    'opt.pac': 'PAC',
    'opt.automatic': 'Automatic',
    'opt.wireguard': 'WireGuard',
    'opt.mimic': 'Reality / mimic',
    'opt.hysteria2': 'Hysteria2',
    'opt.openvpn': 'OpenVPN',
    'nav.gpnServers': 'GPN Servers',
    'nav.about': 'About & Help',
    'title.gpnsrv': 'GPN SERVERS',
    'title.about': 'ABOUT & HELP',
    'gpnServers': 'GPN Servers',
    'gpnServersSub': 'Manage the Italy/Germany WireGuard candidates auto-selected on GPN Connect',
    'about': 'About & Help',
    'aboutSub': 'Program info, help and release notes',
    // ---- GPN Servers view ----
    'gpns.add': 'Add',
    'gpns.measure': 'Measure',
    'gpns.refresh': 'Refresh',
    'gpns.restoreDefaults': 'Restore defaults',
    'gpns.importTitle': 'Import WireGuard .conf',
    'gpns.importDesc': 'Paste an Italy/Germany WireGuard .conf below. The client private key is encrypted with Windows DPAPI before it touches disk.',
    'gpns.import': 'Import',
    'gpns.dpapiNote': '🔐 DPAPI protected',
    'gpns.managed': 'Managed servers',
    'gpns.managedDesc': 'Servers listed here are the Italy/Germany candidates auto-selected on GPN Connect. Disabled ones are skipped.',
    'gpns.empty': 'No GPN servers imported yet.',
    'gpns.active': 'ACTIVE',
    'gpns.disabled': 'DISABLED',
    'gpns.edit': 'Edit',
    'gpns.enable': 'Enable',
    'gpns.disable': 'Disable',
    'gpns.delete': 'Delete',
    'gpns.confirmDelete': 'Delete this GPN server?',
    'gpns.probeTitle': 'Live probe: delay · loss · UDP path',
    'gpns.imported': 'GPN servers imported.',
    'gpns.emptyConf': 'Paste a WireGuard .conf first.',
    'gpns.measuring': 'MEASURING…',
    'gpns.udp.open': 'OPEN',
    'gpns.udp.blocked': 'BLOCKED',
    'gpns.udp.unknown': '?',
    'gpns.restored': 'GPN defaults restored.',
    // ---- About & Help view ----
    'about.tabInfo': 'Program Info',
    'about.tabHelp': 'Help & Feedback',
    'about.tabRelease': 'Release Notes',
    'about.description': 'Gaming Private Network — a modern VPN/GPN client with per-app game routing and a live connection monitor.',
    'about.thanks': 'Thanks for using AO GPN!',
    'about.version': 'Version',
    'about.maintainer': 'Maintainer',
    'about.license': 'License',
    'about.platform': 'Platform',
    'about.cores': 'Cores',
    'about.homepage': 'Homepage / Wiki',
    'about.source': 'Source code',
    'about.helpWiki': 'Visit the Wiki',
    'about.helpWikiDesc': 'Guides, configuration and usage documentation.',
    'about.helpBug': 'Report a bug',
    'about.helpBugDesc': 'Open a GitHub issue with a pre-filled template.',
    'about.helpFeature': 'Request a feature',
    'about.helpFeatureDesc': 'Suggest an idea for an upcoming version.',
    'about.helpTg': 'Telegram group',
    'about.helpTgDesc': 'Ask the community in real time.',
    'about.helpChannel': 'Telegram channel',
    'about.helpChannelDesc': 'Announcements and release notes.',
    'about.releaseIntro': 'What\'s new in this version — every step that built it.',
    'about.releaseExpandAll': 'Expand all',
    'about.releaseCollapseAll': 'Collapse all',
    'about.releaseEmpty': 'No release notes available yet.',
    'about.unknown': 'Unknown',
    // ---- Routing direction ----
    'direction': 'Routing direction',
    'dir.whitelist': 'Whitelist',
    'dir.blacklist': 'Blacklist',
    'dir.hintWhitelist': 'Whitelist — only assigned games/apps are tunneled; all other traffic stays direct.',
    'dir.hintBlacklist': 'Blacklist — everything except the assigned apps is tunneled.',
    // ---- Settings additions ----
    'setting.tunStack': 'TUN stack',
    'setting.tunStackDesc': 'gvisor (compatible), system (fastest), mixed (balanced)',
    'setting.proxyTest': 'System proxy test',
    'setting.proxyTestDesc': 'Verify the OS proxy endpoint reachability',
    'setting.autoGameConnect': 'Auto connect on game start',
    'setting.autoGameConnectDesc': 'Start the GPN tunnel automatically when a boosted game launches',
    'setting.recoveryWatch': 'GPN recovery watch',
    'setting.recoveryWatchDesc': 'Auto-return to WireGuard after a V2rayTCP drop',
    'setting.failover': 'GPN failover',
    'setting.failoverDesc': 'Auto-switch servers when the active one degrades',
    'setting.effects': 'Visual effects',
    'setting.effectsDesc': 'Full, balanced or reduced animation budget',
    'opt.full': 'Full',
    'opt.balanced': 'Balanced',
    'opt.reduced': 'Reduced',
    'opt.gvisor': 'gvisor',
    'opt.system': 'system',
    'opt.mixed': 'mixed',
    'proxy.test': 'TEST',
    'proxy.testing': 'TESTING…',
    'proxy.ok': 'OK',
    'proxy.unreachable': 'UNREACHABLE',
    'proxy.timeout': 'TIMEOUT',
    // ---- Servers view (real nodes) ----
    'realNodes': 'Real GPN nodes',
    'realNodesDesc': 'Using the imported node pool instead of the bundled demo routes',
    'fav.title': 'Toggle favorite',
    // ---- Dashboard: GPN panel / live tunnel info ----
    'gpnConnect': 'GPN Connect',
    'gpnConnectSub': 'Auto-select best Italy/Germany server',
    'stat.yourIp': 'YOUR IP',
    'stat.session': 'SESSION',
    'stat.verification': 'VERIFICATION',
    'ip.checking': 'CHECKING',
    'ip.isp': 'ISP',
    'ip.tunnel': 'TUNNEL',
    'ip.leaking': 'LEAKING',
    'ip.probing': 'PROBING',
    'boost.assigned': 'Assigned games',
    'boost.noGames': 'No games assigned yet — add apps in the Game Profiles view.',
    // ---- Dashboard: server cluster ----
    'gpn.cluster.title': 'Server cluster',
    'gpn.cluster.measure': 'Measure',
    'gpn.cluster.empty': 'No active servers — GPN Connect will not auto-select.',
    'gpn.cluster.best': 'BEST',
    'gpn.cluster.open': 'open',
    'gpn.cluster.egressLabel': '🧭 physical NIC',
    // ---- Dashboard: Advanced & Diagnostics ----
    'gpn.advanced': 'Advanced & Diagnostics',
    'gpn.advancedSub': 'settings & live telemetry',
    'gpn.candidate.title': 'Best candidate',
    'gpn.candidate.empty': 'No measurement yet — run a server probe.',
    'gpn.candidate.wireguard': 'WireGuard',
    'gpn.candidate.v2ray': 'V2ray',
    'gpn.candidate.selected': 'SELECTED',
    'gpn.pidpool.title': 'PID pool',
    'gpn.pidpool.start': 'Watch',
    'gpn.pidpool.stop': 'Stop',
    'gpn.pidpool.refresh': 'Refresh',
    'gpn.pidpool.empty': 'No vpn-routed game yet — assign a game in Game Profiles to build the pool.',
    'gpn.pidpool.watching': 'WATCHING',
    'gpn.pidpool.idle': 'idle',
    'gpn.pidpool.offlineShort': 'offline',
    'gpn.pidpool.degraded': 'DEGRADED',
    'gpn.pidpool.fatal': 'FATAL',
    'gpn.pidpool.pids': 'pids',
    'gpn.capture.title': 'Captured traffic',
    'gpn.capture.empty': 'No capture yet — connect to see per-packet telemetry.',
    'gpn.capture.flows': 'Top flows',
    'gpn.capture.perPid': 'Per-PID',
    'gpn.capture.unknown': '?',
    'gpn.matrix.title': 'Failover matrix',
    'gpn.matrix.empty': 'No measurement yet — run a server probe.',
    'gpn.matrix.switchTo': 'Switch to',
    'gpn.matrix.fallback': 'Fallback to V2ray',
    'gpn.matrix.keep': 'Keep',
    'gpn.matrix.default': 'Default',
    'gpn.matrix.strict': 'Strict',
    'gpn.matrix.legend': '✔ healthy · ✘ unhealthy — default | strict policy',
    'gpn.failover.title': 'Stable connection (no auto switch)',
    'gpn.failover.sub': 'OFF = stick to the chosen server for the most stable connection. ON = auto-switch/failover allowed.',
    'gpn.recovery.title': 'Auto-recover after V2ray',
    'gpn.recovery.sub': 'After a V2rayTCP fallback, watch for a healthy server and return to WireGuard automatically.',
    'gpn.telemetry.title': 'Failover telemetry',
    'gpn.telemetry.reset': 'Reset',
    'gpn.telemetry.switchShort': 'switch',
    'gpn.telemetry.deathShort': 'death',
    'gpn.telemetry.fallbackShort': 'fallback',
    'gpn.telemetry.recoverShort': 'recover',
    'gpn.telemetry.selectShort': 'select',
    'gpn.captureSet.title': 'WinDivert queue',
    'gpn.captureSet.save': 'Apply',
    'gpn.captureSet.queueLen': 'Queue len',
    'gpn.captureSet.queueTime': 'Queue time (ms)',
    'gpn.captureSet.queueSize': 'Queue size (B)',
    'gpn.captureSet.enableLen': 'len',
    'gpn.captureSet.enableTime': 'time',
    'gpn.captureSet.enableSize': 'size',
    'gpn.captureSet.layer': 'Layer',
    'gpn.captureSet.direction': 'Direction',
    'gpn.captureSet.hint': 'Values apply to the next capture start; the running loop keeps its current parameters.',
    'gpn.wintunSet.title': 'Wintun adapter',
    'gpn.wintunSet.save': 'Apply',
    'gpn.wintunSet.adapter': 'Adapter name prefix',
    'gpn.wintunSet.ring': 'Ring capacity (B)',
    'gpn.wintunSet.hint': 'Applies to the next WireGuard connect; the server id is appended to the adapter name.',
    'gpn.diag.title': 'GPN diagnostics',
    'gpn.diag.clear': 'Clear',
    'gpn.diag.empty': 'No GPN diagnostics yet — connect to see the live pipeline.',
    'gpn.reslog.title': 'Last 50 decisions',
    'gpn.reslog.refresh': 'Refresh',
    'gpn.reslog.clear': 'Clear',
    'gpn.reslog.empty': 'No decisions recorded yet.',
    'gpn.reslog.path': 'Mirrored to',
    'gpn.reslog.action.switch': 'Server switch',
    'gpn.reslog.action.udpDeath': 'UDP death',
    'gpn.reslog.action.modeFallback': 'Mode fallback',
    'gpn.reslog.action.recover': 'Recover',
    'gpn.reslog.action.select': 'Selection',
    // ---- Dashboard: Global VPN panel ----
    'global.title': 'VPN',
    'global.allTraffic': 'All Traffic Tunnelled',
    'global.disconnected': 'Disconnected',
    'global.transport': 'Transport',
    'global.modeActive': 'mode active',
    'global.ipVerification': 'IP Verification',
    'global.tunnelVerified': '✅ Tunnel verified',
    'global.notConnected': 'Not connected',
    'global.coverage': 'Coverage',
    'global.coverage100': '100% of traffic',
    // ---- Dashboard: status banners ----
    'banner.tunAdmin': 'TUN requires administrator privileges — CONNECT is locked. Relaunch as admin or switch to Proxy capture.',
    'banner.relaunch': 'Relaunch as admin',
    'banner.captureLock': 'VPN locks capture to TUN — every app is captured at the network layer so nothing can bypass the tunnel. Choose GPN Game Tunnel to pick Proxy capture.',
    'banner.tunProxy': 'TUN capture won\'t touch your system proxy — your PROXY ON preference stays independent. Only traffic that ignores the OS proxy (games, some apps) needs TUN.',
    'banner.leak': 'You are connected but your public IP has not changed — traffic may be leaking. Check your capture settings or try switching to TUN.',
    'banner.ok': 'Tunnel verified — your public IP is now {ip} (was {isp}). Traffic is routing through the selected node.',
    'status.idle': 'IDLE',
    'status.notConnected': 'Not connected',
    'status.tunneled': 'TUNNELED',
    'status.probing': 'PROBING',
    'status.retrying': 'RETRYING',
    'status.leaking': 'LEAKING',
    'ip.detail.tunActive': 'TUN adapter is routing traffic',
    'ip.detail.socksVerified': 'SOCKS proxy verified',
    'ip.detail.fetchingBaseline': 'Fetching ISP baseline…',
    'ip.detail.willRetry': 'Tunnel probe failed — will retry',
    'ip.detail.possibleLeak': 'Tunnel IP matches ISP — possible leak',
    'conn.statusLine': 'Route {mode} · Capture {capture} — press CONNECT to start the tunnel.',
    'boost.routeActive': '{route} route active',
    'boost.idleNoBoost': 'Idle — no boost',
    'boost.viaNode': 'via {node}',
    'boost.title': 'GPN Game Boost',
    'boost.full': 'Full Game Boost',
    'boost.before': 'Before',
    'boost.after': 'After',
    'global.tunnelVerifiedDesc': 'IP changed to {ip}. Traffic is securely routed.',
    'global.leaking': 'Leaking',
    'global.leakingDesc': 'IP unchanged — traffic may be leaking.',
    'global.probing': 'Probing',
    'global.probingDesc': 'Waiting for the first tunnel measurement…',
    // ---- Game Profiles (Boost management) view ----
    'boost.manageSub': 'Assign VPN, proxy, direct or block per executable. Unlisted traffic follows the selected mode.',
    'boost.refresh': 'Refresh',
    'boost.runningApps': 'Running apps',
    'boost.addExe': '＋ Add EXE',
    'boost.runningAppsTitle': 'Running applications',
    'boost.runningAppsDesc': 'Choose a currently running executable to add to Game Boost.',
    'boost.filterRunning': 'Filter running applications…',
    'boost.filterApps': 'Filter by name or route…',
    'boost.appRoutes': 'Application routes',
    'boost.appRoutesDesc': 'Changing a route persists it and applies immediately while Smart Split is active.',
    'boost.routeMode': 'Route mode',
    'boost.off': 'Off',
    'boost.globalVpn': 'VPN',
    'boost.gpnTunnel': 'GPN Game Tunnel',
    'boost.autoGame': 'Auto-connect when a game starts',
    'boost.autoGameDesc': 'When a listed VPN game launches, Smart Split is enabled; it returns to Off after the last game closes.',
    'boost.entries': 'entries',
    'boost.of': 'of',
    'boost.statusRunning': 'Running',
    'boost.statusNotRunning': 'Not running',
    'boost.colProgram': 'Program',
    'boost.colRoute': 'Route',
    'boost.colLive': 'Live',
    'boost.colStatus': 'Status',
    'boost.colPing': 'Real ping',
    'boost.colTarget': 'Target IP',
    'boost.colDown': '↓ Down',
    'boost.colUp': '↑ Up',
    'boost.colChange': 'Change',
    'boost.remove': 'Remove',
    'boost.empty': 'No applications are assigned yet. Use Add EXE or assign a route from Performance.',
    'boost.tip': 'VPN routes need TUN capture (apps that bypass the OS proxy). Proxy routes use the OS proxy setting; Direct bypasses the tunnel and Block rejects the destination. WARP routes exit through a clean egress on the server and need a WireGuard node — on other nodes they fall back to VPN.',
    'route.assign': 'Assign route…',
    'route.vpn': 'VPN',
    'route.direct': 'Direct',
    'route.block': 'Block',
    'route.warp': 'WARP',
    'dir.selectVpn': 'VPN ⇄ outside tunnel',
    'dir.selectDirect': 'Direct ⇄ tunneled',
    'dir.selectBlock': 'Block',
    'dir.selectWarp': 'WARP ⇄ outside tunnel',
    'dir.blacklistTableHint': 'Blacklist active — assigned apps stay direct; everything else is tunneled.',
    'warp.fallbackNotice': 'WARP route needs a WireGuard node — the active connection is not WireGuard, so WARP apps are routed through VPN.',
    'boost.idle': 'Idle',
    'boost.noProcesses': 'No eligible running applications found.',
    'dir.rowExcluded': 'excluded',
    'dir.rowTunneled': 'tunneled'
  }

  // Resolve a UI string: bridge dictionary first (Dil/*.json), then the local
  // English defaults above, then the raw key.
  function nxT(key, params) {
    let val = null;
    if (B && typeof B.t === 'function') {
      try { val = B.t(key, params); } catch (e) { /* fall through */ }
      // A bridge result equal to the raw key means the dictionary had no match;
      // fall back to the skin's own English defaults below.
      if (typeof val === 'string' && val === key) val = null;
    }
    if ((val === null || val === undefined || val === '') && Object.prototype.hasOwnProperty.call(NX_TEXT, key)) {
      val = NX_TEXT[key];
    }
    if (val === null || val === undefined || val === '') return key;
    if (params) {
      Object.keys(params).forEach(k => { val = String(val).split('{' + k + '}').join(String(params[k])); });
    }
    return String(val);
  }

  // Apply [data-nx-i18n] on static markup; nested elements (icons, badges) are
  // preserved exactly like the main dashboard's applyTexts walker.
  function nxApplyStaticTexts() {
    document.querySelectorAll('[data-nx-i18n]').forEach(el => {
      const key = el.getAttribute('data-nx-i18n');
      const val = nxT(key);
      if (!val || val === key) return;
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

  function nxRef(id) { return document.getElementById(id); }
  function nxState() {
    return {
      connected: G('connected'),
      connecting: G('connecting'),
      mode: G('mode'),
      transport: G('transport'),
      splitMode: G('splitMode'),
      autoReconnect: G('autoReconnect'),
      systemProxyMode: G('systemProxyMode'),
      protocolPreference: G('protocolPreference'),
      useRealNodes: G('useRealNodes'),
      activeRealNodeId: G('activeRealNodeId'),
      selectedNode: G('selectedNode'),
      monitorSnapshot: G('monitorSnapshot') || { apps: [] },
      routeLabels: G('routeLabels') || {},
      nodes: G('nodes') || [],
      telemetry: G('telemetry') || [],
      language: G('language') || 'en',
      sessionTime: G('sessionTime') || '00:00:00',
      exitIp: G('exitIp') || 'Not verified',
      exitCountry: G('exitCountry') || '',
      isAdmin: G('isAdmin'),
      gpnServers: G('gpnServers') || [],
      gpnServerProbes: G('gpnServerProbes') || [],
      tunStack: G('tunStack') || 'mixed',
      appInfo: G('appInfo') || null,
      effectsTier: G('effectsTier') || 'full',
      gpnRecoveryWatch: G('gpnRecoveryWatch'),
      gpnFailover: G('gpnFailover'),
      proxyTestResult: G('proxyTestResult') || null,
      // ---- GPN panel / Advanced & Diagnostics (live host state) ----
      gpnConnectState: G('gpnConnectState') || { label: '—', pending: false },
      gpnPidPool: G('gpnPidPool') || null,
      gpnCaptureSettings: G('gpnCaptureSettings') || null,
      gpnWintunSettings: G('gpnWintunSettings') || null,
      gpnCaptureStats: G('gpnCaptureStats') || null,
      gpnFailoverMatrix: G('gpnFailoverMatrix') || null,
      gpnSelectionPrediction: G('gpnSelectionPrediction') || null,
      gpnTelemetry: G('gpnTelemetry') || null,
      gpnResilienceLog: G('gpnResilienceLog') || null,
      gpnDiagLines: G('gpnDiagLines') || [],
      processCatalog: G('processCatalog') || [],
      nodePool: G('nodePool') || [],
      undoAvailable: G('undoAvailable') || null,
      connectionError: G('connectionError') || null,
      realIpState: G('realIpState') || null,
      systemProxyState: G('systemProxyState') || { desired: 0, effective: 0, owned: false },
      activeGpnServer: G('activeGpnServer') || ''
    };
  }

  let _nxView = 'dashboard';
  let _nexusWired = false;
  let _nxSyncTimer = null;

  // ---------- view router ----------
  function nxGo(view) {
    _nxView = view;
    const skin = document.body;
    skin.querySelectorAll('[data-nx-view]').forEach(el => {
      if (el.tagName === 'BUTTON') return; // sidebar nav buttons carry data-nx-view too
      const on = el.dataset.nxView === view;
      // Visibility is class-based (.nx-view-hidden) so the dashboard grid/flex
      // sections and the .nx-view sub-sections all hide through the same CSS.
      el.classList.toggle('nx-view-hidden', !on);
    });
    skin.querySelectorAll('.nx-nav button').forEach(b => {
      b.classList.toggle('active', b.dataset.nxView === view);
    });
    const title = nxRef('nxTitleLabel');
    const caps = { dashboard: nxT('title.dashboard'), route: nxT('title.route'), servers: nxT('title.servers'), gpnsrv: nxT('title.gpnsrv'), games: nxT('title.games'), analytics: nxT('title.analytics'), settings: nxT('title.settings'), about: nxT('title.about') };
    if (title) title.textContent = caps[view] || nxT('title.dashboard');
    if (view === 'dashboard') nxRenderDashboardPanels();
    if (view === 'servers') nxRenderServers();
    if (view === 'route') nxRenderRoute();
    if (view === 'gpnsrv') nxRenderGpnServers();
    if (view === 'games') nxRenderGameProfiles();
    if (view === 'analytics') { nxRenderAnalytics(); nxRenderMonitor(); }
    if (view === 'settings') nxRenderSettings();
    if (view === 'about') nxRenderAbout();
  }

  // ---------- connect ----------
  function nxToggleConnect() {
    const S = nxState();
    if (B) {
      // `connected` = bastığı anda ekranda görünen durum → host yönü bu
      // niyetten türetir (bkz. features/gpn.js).
      const ok = B.postToHost({ action: 'toggle_connection', mode: S.mode, transport: S.transport, protocol: S.protocolPreference, connected: S.connected === true });
      if (!ok && typeof B.setConnected === 'function') B.setConnected(!S.connected, false);
    } else {
      DEMO.connected = !DEMO.connected;
    }
  }

  // ---------- node helpers ----------
  function nxNodeKey(node, useReal) { return useReal ? node.indexId : node.key; }
  function nxNodeLabel(node, useReal) { return useReal ? (node.name || node.address || node.indexId) : (node.name + ' · ' + node.addr); }
  function nxNodeMeta(node, useReal) {
    return useReal
      ? (node.address || '') + (node.port > 0 ? ':' + node.port : '') + (node.protocol ? ' · ' + node.protocol : '')
      : (node.addr + ' · ' + (['istanbul', 'frankfurt'].includes(node.key) ? 'VLESS' : 'VMess'));
  }
  function nxNodeMs(node, useReal) { return useReal ? (node.delay > 0 ? node.delay + ' ms' : '— ms') : (node.ping + ' ms'); }
  function nxNodeCode(node, useReal) { return useReal ? (node.country || node.sub || 'VPN').slice(0, 2) : (node.code || '🌐'); }

  // ---- country flags + localized grouping (mirrors the Nodes view) ----
  // Windows cannot render emoji flags, so real flags come from core/flags.js
  // as tiny SVGs (loaded by skin.html); unknown codes fall back to a letter
  // tile so the list never shows an empty box.
  function nxNodeCountryKey(node, useReal) {
    const raw = useReal ? (node && node.country) : (node && node.code);
    if (typeof raw === 'string' && /^[A-Za-z]{2}$/.test(raw)) return raw.toUpperCase();
    return '';
  }
  function nxSvgFlagInner(code, boxCls) {
    if (!code) return '';
    try {
      if (window.aogpn && window.aogpn.flags && typeof window.aogpn.flags.flagFor === 'function') {
        const inner = window.aogpn.flags.flagFor(code);
        if (inner) return '<span class="' + boxCls + '">' + inner + '</span>';
      }
    } catch (e) { /* fall through to the letter tile */ }
    return '';
  }
  function nxCountryLabel(code, lang) {
    try {
      const name = new Intl.DisplayNames([(lang || 'en')], { type: 'region' }).of(code);
      if (name && String(name) !== code) return String(name);
    } catch (e) { /* unknown region */ }
    return null;
  }
  function nxNodeFlagMarkup(node, useReal) {
    const code = nxNodeCountryKey(node, useReal);
    if (code) {
      const flag = nxSvgFlagInner(code, 'nx-flagRow');
      if (flag) return '<div class="nx-srvFlag">' + flag + '</div>';
    }
    return '<div class="nx-srvFlag">' + escHtml(nxNodeCode(node, useReal)) + '</div>';
  }
  function nxServersGroupHead(code, count, S) {
    const label = code ? (nxCountryLabel(code, S.language) || code) : nxT('nodes.countryUnknown');
    const flag = code ? nxSvgFlagInner(code, 'nx-flagHead') : '';
    return '<div class="nx-srvGroupHead">'
      + (flag || '<span class="nx-srvGroupGlobe">🌐</span>')
      + '<span class="nx-srvGroupName">' + escHtml(label) + '</span>'
      + '<span class="nx-srvGroupCount">' + count + '</span>'
      + '</div>';
  }

  function nxActiveNodeKey(S) { return S.useRealNodes ? S.activeRealNodeId : (S.selectedNode ? S.selectedNode.key : ''); }

  function nxSwitchNode(key) {
    const S = nxState();
    if (B) {
      if (S.useRealNodes && typeof B.requestNodeSwitch === 'function') {
        B.requestNodeSwitch(key);
        return;
      }
      if (!S.useRealNodes) {
        const found = (S.nodes || []).find(n => nxNodeKey(n, false) === key) || S.nodes[0];
        SET('selectedNode', found);
        if (typeof B.applyNode === 'function') B.applyNode();
        updateNexusServerSummary(found);
        renderNexusServerValue();
        return;
      }
      const top = document.getElementById('topNodeSelect'); // not present in this iframe
      if (top) { top.value = key; top.dispatchEvent(new Event('change')); }
    } else {
      DEMO.selectedNode = (DEMO.nodes || []).find(n => n.key === key) || DEMO.nodes[0];
      updateNexusServerSummary(DEMO.selectedNode);
      renderNexusServerValue();
    }
  }

  // ---------- wiring ----------
  function wireNexusSkin() {
    if (_nexusWired) return;
    _nexusWired = true;

    const power = nxRef('nxPower');
    if (power) power.addEventListener('click', (e) => { e.stopPropagation(); nxToggleConnect(); });

    const serverSel = nxRef('nxServer');
    if (serverSel) serverSel.addEventListener('change', () => {
      if (!serverSel.value) return;
      nxSwitchNode(serverSel.value);
      nxRenderServers();
    });

    const ks = nxRef('nxKillSwitch');
    if (ks) ks.addEventListener('click', () => {
      const S = nxState();
      const wasOff = S.splitMode === 'off';
      const next = wasOff ? 'manual' : 'off';
      if (B) {
        B.postToHost({ action: 'set_split_mode', mode: next });
        SET('splitMode', next);
      } else {
        DEMO.splitMode = next;
      }
      ks.classList.toggle('on', next === 'off');
      ks.setAttribute('aria-checked', String(next === 'off'));
    });
    // Keyboard access for the kill switch (tabindex="0" in the markup):
    // Enter/Space activate it exactly like a click.
    if (ks) ks.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); ks.click(); }
    });

    document.querySelectorAll('.nx-nav button').forEach(btn => {
      btn.addEventListener('click', (e) => { e.stopPropagation(); nxGo(btn.dataset.nxView || 'dashboard'); });
    });

    document.querySelectorAll('.nx-segOpt[data-nx-mode]').forEach(b => {
      b.addEventListener('click', () => {
        if (B) B.postToHost({ action: 'set_connection_mode', mode: b.dataset.nxMode });
        SET('mode', b.dataset.nxMode);
        nxRenderRoute();
      });
    });
    document.querySelectorAll('.nx-segOpt[data-nx-transport]').forEach(b => {
      b.addEventListener('click', () => {
        if (B) B.postToHost({ action: 'set_transport', transport: b.dataset.nxTransport });
        SET('transport', b.dataset.nxTransport);
        nxRenderRoute();
      });
    });
    document.querySelectorAll('.nx-segOpt[data-nx-dir]').forEach(b => {
      b.addEventListener('click', () => {
        if (B) B.postToHost({ action: 'set_split_direction', invert: String(b.dataset.nxDir === 'blacklist') });
        nxRenderRoute();
      });
    });

    // Header language picker — visible on every view without opening SETTINGS.
    // Same whole-app channel as the settings select: B.setLanguage persists
    // through the host and re-renders this skin (and the dashboard) instantly.
    const hdrLang = nxRef('nxHeaderLang');
    if (hdrLang) {
      hdrLang.innerHTML = __nxOpts(__nxLangs(), nxState().language);
      hdrLang.addEventListener('change', () => {
        const next = String(hdrLang.value || 'en');
        if (B && typeof B.setLanguage === 'function') B.setLanguage(next);
        SET('language', next);
        nxSyncAll();
      });
    }

    const stdBtn = nxRef('nxToStandardBtn');
    if (stdBtn) stdBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (B && typeof B.applySkin === 'function') B.applySkin('standard');
    });

    const winCmd = (command) => { if (B) B.postToHost({ action: 'app_control', command }); };
    const minBtn = nxRef('nxMinBtn');
    if (minBtn) minBtn.addEventListener('click', (e) => { e.stopPropagation(); winCmd('minimize'); });
    const maxBtn = nxRef('nxMaxBtn');
    if (maxBtn) maxBtn.addEventListener('click', (e) => { e.stopPropagation(); winCmd('maximize_toggle'); });
    const closeBtn = nxRef('nxCloseBtn');
    if (closeBtn) closeBtn.addEventListener('click', (e) => { e.stopPropagation(); winCmd('close'); });

    const dragRegion = nxRef('nxDragRegion');
    if (dragRegion) {
      let lastPress = 0;
      dragRegion.addEventListener('pointerdown', (event) => {
        if (event.button !== 0) return;
        event.preventDefault();
        const now = Date.now();
        const isDoubleClick = now - lastPress < 400;
        lastPress = now;
        winCmd(isDoubleClick ? 'maximize_toggle' : 'drag');
      });
    }
  }

  // ---------- dashboard sync ----------
  function syncNexusSkin() {
    const S = nxState();
    const hdrLang = nxRef('nxHeaderLang');
    if (hdrLang) hdrLang.value = S.language || 'en';
    const st = nxRef('nxState'), sub = nxRef('nxSubstate'), modeEl = nxRef('nxRouteMode');
    const routeState = nxRef('nxRouteState');
    const ping = nxRef('nxPing'), jitter = nxRef('nxJitter'), loss = nxRef('nxLoss');
    const serverPing = nxRef('nxServerPing'), statusText = nxRef('nxStatusText');
    const on = S.connected, cn = S.connecting;
    if (st) st.textContent = on ? nxT('state.connected') : cn ? nxT('state.connecting') : nxT('state.connect');
    if (sub) sub.textContent = on ? nxT('substate.active') : cn ? nxT('substate.starting') : nxT('substate.idle');
    if (modeEl) modeEl.textContent = (S.mode === 'gpn') ? nxT('route.gaming') : nxT('route.global');
    // Live state is styled through CSS classes (see skin.css): the connected /
    // disconnected palette for the orb glow and status dot lives in the
    // stylesheet (testable via getComputedStyle) instead of inline styles.
    document.body.classList.toggle('nx-live', on);
    document.body.classList.toggle('nx-connecting', cn);
    if (routeState) routeState.textContent = on ? nxT('routeState.optimized') : nxT('routeState.ready');
    if (statusText) statusText.textContent = on ? nxT('status.connected') : cn ? nxT('status.connecting') : nxT('status.online');
    const tele = S.telemetry || [];
    const hasSample = tele.some(v => v !== undefined && v !== null);
    const pingMs = on && hasSample && Number.isFinite(tele[0]) && tele[0] > 0 ? Math.round(tele[0]) : null;
    const lossN = on && hasSample && Number.isFinite(tele[1]) && tele[1] >= 0 ? Number(tele[1]) : null;
    const jit = pingMs != null ? Math.max(1, 2 + (pingMs % 4)) : null;
    if (ping) ping.innerHTML = pingMs != null ? pingMs + '<small> ms</small>' : '--<small> ms</small>';
    if (jitter) jitter.innerHTML = jit != null ? jit + '<small> ms</small>' : '--<small> ms</small>';
    if (loss) loss.innerHTML = lossN != null ? lossN.toFixed(1) + '<small>%</small>' : '--<small>%</small>';
    if (serverPing) serverPing.textContent = pingMs != null ? pingMs + 'ms' : '--ms';
    const ks = nxRef('nxKillSwitch');
    if (ks) { const onk = S.splitMode === 'off'; ks.classList.toggle('on', onk); ks.setAttribute('aria-checked', String(onk)); }
    renderNexusNodeSelect();
    renderNexusGameList();
    nxRefreshBars();
  }

  // ---------- node dropdown + summary ----------
  function renderNexusNodeSelect() {
    const sel = nxRef('nxServer');
    if (!sel) return;
    const S = nxState();
    const nodes = S.nodes || [];
    if (nodes.length === 0) { sel.innerHTML = '<option value="">' + escHtml(nxT('loadingNodes')) + '</option>'; return; }
    sel.innerHTML = nodes.map(n => {
      const val = nxNodeKey(n, S.useRealNodes);
      const label = nxNodeLabel(n, S.useRealNodes);
      const ms = nxNodeMs(n, S.useRealNodes);
      const txt = (ms && !ms.startsWith('—')) ? (label + ' — ' + ms) : label;
      return '<option value="' + escHtml(String(val)) + '">' + escHtml(txt) + '</option>';
    }).join('');
    updateNexusServerSummary(S.useRealNodes ? (nodes.find(n => n.indexId === S.activeRealNodeId) || nodes[0]) : S.selectedNode);
  }

  function updateNexusServerSummary(node) {
    if (!node) return;
    const S = nxState();
    const nameEl = nxRef('nxServerName'), flagEl = nxRef('nxFlag'), metaEl = nxRef('nxServerMeta'), pingEl = nxRef('nxServerPing');
    if (nameEl) nameEl.textContent = nxNodeLabel(node, S.useRealNodes);
    if (flagEl) flagEl.textContent = nxNodeCode(node, S.useRealNodes);
    if (metaEl) metaEl.textContent = nxNodeMeta(node, S.useRealNodes);
    if (pingEl) pingEl.textContent = nxNodeMs(node, S.useRealNodes);
    const gameServer = nxRef('nxGameServer');
    if (gameServer) gameServer.textContent = nxNodeLabel(node, S.useRealNodes);
    const youCity = nxRef('nxYouCity');
    if (youCity && S.exitCountry) youCity.textContent = S.exitCountry;
    renderNexusServerValue();
  }

  function renderNexusServerValue() {
    const sel = nxRef('nxServer');
    if (!sel) return;
    const S = nxState();
    const key = nxActiveNodeKey(S);
    const opt = sel.querySelector('option[value="' + key + '"]');
    if (opt) sel.value = key;
  }

  // ---------- servers sub-view ----------
  // Node list sort order, mirrored from the dashboard's Nodes view (same
  // semantics: country A-Z, favorites first, recently used, ping asc/desc).
  // Persisted locally so the choice survives re-renders and reloads.
  let _nxSort = 'default';
  try {
    const s = localStorage.getItem('aogpn.nexusSort.v1');
    if (['default', 'country', 'fav', 'ping', 'pingDesc', 'recent'].includes(s)) _nxSort = s;
  } catch (e) { /* no localStorage */ }
  function nxPersistSort() {
    try { localStorage.setItem('aogpn.nexusSort.v1', _nxSort); } catch (e) { /* ignore */ }
  }

  function nxSortedNodes(nodes) {
    const arr = [...(nodes || [])];
    if (_nxSort === 'country') {
      arr.sort((a, b) => (a.country || 'ZZ').localeCompare(b.country || 'ZZ') || (a.name || '').localeCompare(b.name || ''));
    } else if (_nxSort === 'default') {
      // Default order = the host's persisted order; the host already keeps
      // countries adjacent, so no re-sorting happens here.
    } else if (_nxSort === 'fav') {
      arr.sort((a, b) => ((b.fav ? 1 : 0) - (a.fav ? 1 : 0)) || (a.country || 'ZZ').localeCompare(b.country || 'ZZ') || (a.name || '').localeCompare(b.name || ''));
    } else if (_nxSort === 'recent') {
      arr.sort((a, b) => (b.lastUsed || 0) - (a.lastUsed || 0));
    } else if (_nxSort === 'ping' || _nxSort === 'pingDesc') {
      // Unmeasured nodes sink to the bottom so a "0 ms" node is never the
      // fastest.
      const dir = _nxSort === 'ping' ? 1 : -1;
      const ms = a => (Number(a.delay) > 0 ? Number(a.delay) : null);
      arr.sort((a, b) =>
        ((ms(a) === null ? 1 : 0) - (ms(b) === null ? 1 : 0))
        || dir * ((ms(a) || 0) - (ms(b) || 0))
        || (a.name || '').localeCompare(b.name || ''));
    }
    return arr;
  }

  // Node pool panel: subscription links (GitHub / .txt) the host pulls nodes
  // from — add, inline-edit, remove and one-click fetch, same contract as the
  // dashboard's Nodes view pool.
  let _nxPoolEdit = null;
  function nxRenderNodePool() {
    const root = nxRef('nxPool');
    if (!root) return;
    const S = nxState();
    const links = S.nodePool || [];
    const list = links.length === 0
      ? '<p class="nx-empty center slim">' + escHtml(nxT('nodes.poolEmpty')) + '</p>'
      : '<div class="nx-poolList">' + links.map(link => {
          if (link === _nxPoolEdit) {
            return '<div class="nx-poolRow edit" data-nx-pooleditrow>'
              + '<input type="text" class="nx-inp" data-nx-pooleditval value="' + escHtml(link) + '" spellcheck="false">'
              + '<button type="button" class="nx-btn sm" data-nx-poolsave>' + escHtml(nxT('nodes.poolSave')) + '</button>'
              + '<button type="button" class="nx-btn sm" data-nx-poolcancel>' + escHtml(nxT('nodes.poolCancel')) + '</button>'
              + '</div>';
          }
          return '<div class="nx-poolRow"><span class="nx-poolDot"></span><span class="nx-poolUrl" title="' + escHtml(link) + '">' + escHtml(link) + '</span>'
            + '<button type="button" class="nx-btn sm" data-nx-pooledit="' + escHtml(link) + '">' + escHtml(nxT('nodes.poolEdit')) + '</button>'
            + '<button type="button" class="nx-btn sm danger" data-nx-poolremove="' + escHtml(link) + '">' + escHtml(nxT('nodes.poolRemove')) + '</button></div>';
        }).join('') + '</div>';
    root.innerHTML =
      '<div class="nx-pool">'
      + '<div class="nx-cardHead"><div class="nx-label" data-nx-i18n="nodes.pool">Node pool</div></div>'
      + '<p class="nx-poolDesc">' + escHtml(nxT('nodes.poolDesc')) + '</p>'
      + '<div class="nx-poolAdd"><input type="text" class="nx-inp" data-nx-poolurl placeholder="https://github.com/user/nodes.txt" spellcheck="false">'
      + '<button type="button" class="nx-btn primary" data-nx-pooladd>＋ ' + escHtml(nxT('nodes.poolAdd')) + '</button>'
      + '<button type="button" class="nx-btn" data-nx-poolfetch>⟳ ' + escHtml(nxT('nodes.poolFetch')) + '</button></div>'
      + list
      + '</div>';
    const addInput = root.querySelector('[data-nx-poolurl]');
    root.querySelector('[data-nx-pooladd]')?.addEventListener('click', () => {
      const url = (addInput && addInput.value || '').trim();
      if (!url || !B) return;
      B.postToHost({ action: 'add_node_pool_link', url });
      addInput.value = '';
    });
    if (addInput) addInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); root.querySelector('[data-nx-pooladd]')?.click(); }
    });
    root.querySelector('[data-nx-poolfetch]')?.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'fetch_node_pool' });
    });
    root.querySelectorAll('[data-nx-pooledit]').forEach(btn => {
      btn.addEventListener('click', () => { _nxPoolEdit = btn.dataset.nxPooledit; nxRenderNodePool(); const inp = root.querySelector('[data-nx-pooleditval]'); if (inp) { inp.focus(); inp.select(); } });
    });
    root.querySelectorAll('[data-nx-poolremove]').forEach(btn => {
      btn.addEventListener('click', () => { if (B) B.postToHost({ action: 'remove_node_pool_link', url: btn.dataset.nxPoolremove }); });
    });
    const editVal = root.querySelector('[data-nx-pooleditval]');
    if (editVal) {
      const finish = (save) => {
        const newUrl = editVal.value.trim();
        const oldUrl = _nxPoolEdit;
        _nxPoolEdit = null;
        if (save && newUrl && newUrl !== oldUrl && B) B.postToHost({ action: 'edit_node_pool_link', url: oldUrl, newUrl });
        nxRenderNodePool();
      };
      root.querySelector('[data-nx-poolsave]')?.addEventListener('click', () => finish(true));
      root.querySelector('[data-nx-poolcancel]')?.addEventListener('click', () => finish(false));
      editVal.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') { e.preventDefault(); finish(true); }
        else if (e.key === 'Escape') finish(false);
      });
    }
  }

  function nxRenderServers() {
    const root = nxRef('nxServers');
    if (!root) return;
    const S = nxState();
    const nodes = nxSortedNodes(S.nodes || []);
    // When the host publishes the real node pool, surface it like the dashboard's
    // Nodes view does (favorites come along with the pushed node objects).
    const toolbar = S.useRealNodes && nodes.some(n => n && n.indexId)
      ? '<div class="nx-srvToolbar"><span class="nx-srvRealDot"></span> <b>' + escHtml(nxT('realNodes')) + '</b> <em>· ' + escHtml(nxT('realNodesDesc')) + '</em></div>'
      : '';
    const sortRow = S.useRealNodes && nodes.some(n => n && n.indexId)
      ? '<div class="nx-sortRow"><span class="nx-subLabel">' + escHtml(nxT('nodes.sortBy')) + '</span><select class="nx-settingSel nx-sortSel" data-nx-sort aria-label="Sort nodes">'
        + [['default', 'nodes.sortDefault'], ['country', 'nodes.sortCountry'], ['fav', 'nodes.sortFav'], ['recent', 'nodes.sortRecent'], ['ping', 'nodes.sortPing'], ['pingDesc', 'nodes.sortPingDesc']]
          .map(o => '<option value="' + o[0] + '"' + (o[0] === _nxSort ? ' selected' : '') + '>' + escHtml(nxT(o[1])) + '</option>').join('')
        + '</select></div>'
      : '';
    if (nodes.length === 0) {
      root.innerHTML = toolbar + sortRow + '<p class="nx-empty">' + escHtml(nxT('noServers')) + '</p>';
      nxWireSort(root);
      nxRenderNodePool();
      return;
    }
    const activeKey = nxActiveNodeKey(S);
    // Group the rows by country (ISO code → flag + localized name), like the
    // main dashboard's Nodes view. Unknown/missing countries gather under one
    // "Other" header; rows keep their data-nx-node/fav wiring untouched.
    const groupOrder = [];
    const grouped = new Map();
    for (const n of nodes) {
      const code = nxNodeCountryKey(n, S.useRealNodes);
      if (!grouped.has(code)) { groupOrder.push(code); grouped.set(code, []); }
      grouped.get(code).push(n);
    }
    const rowHtml = (n) => {
      const key = nxNodeKey(n, S.useRealNodes);
      const active = String(key) === String(activeKey);
      const fav = S.useRealNodes && n.fav === true;
      const favBtn = S.useRealNodes
        ? '<button type="button" class="nx-fav' + (fav ? ' on' : '') + '" data-nx-fav="' + escHtml(String(key)) + '" title="' + escHtml(nxT('fav.title')) + '" aria-label="' + escHtml(nxT('fav.title')) + '">' + (fav ? '★' : '☆') + '</button>'
        : '';
      return '<div class="nx-srv' + (active ? ' active' : '') + '" data-nx-node="' + escHtml(String(key)) + '">'
        + nxNodeFlagMarkup(n, S.useRealNodes)
        + '<div><div class="nx-srvName">' + escHtml(nxNodeLabel(n, S.useRealNodes)) + (active ? ' <span class="nx-routeModePill inline">' + escHtml(nxT('srvActive')) + '</span>' : '') + '</div>'
        + '<span class="nx-srvMeta">' + escHtml(nxNodeMeta(n, S.useRealNodes)) + '</span></div>'
        + favBtn
        + '<div class="nx-srvPing">' + escHtml(nxNodeMs(n, S.useRealNodes)) + '</div>'
        + '</div>';
    };
    root.innerHTML = toolbar + sortRow + groupOrder.map(code => {
      const list = grouped.get(code);
      return nxServersGroupHead(code, list.length, S) + list.map(rowHtml).join('');
    }).join('');
    nxWireSort(root);
    root.querySelectorAll('[data-nx-node]').forEach(card => {
      card.addEventListener('click', () => {
        nxSwitchNode(card.dataset.nxNode);
        nxRenderServers();
        renderNexusServerValue();
      });
    });
    // Favorite toggle (real nodes only): posts the same host action the main
    // dashboard's node cards use; the next host push refreshes the stars.
    root.querySelectorAll('[data-nx-fav]').forEach(favBtn => {
      favBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        if (B) B.postToHost({ action: 'toggle_node_fav', indexId: favBtn.dataset.nxFav });
      });
    });
    nxRenderNodePool();
  }

  // Sort select wiring (servers view): changing the order re-renders the list
  // and persists the choice locally.
  function nxWireSort(root) {
    const sel = root.querySelector('[data-nx-sort]');
    if (!sel) return;
    sel.addEventListener('change', () => {
      _nxSort = sel.value;
      nxPersistSort();
      nxRenderServers();
    });
  }

  // ---------- game profiles ----------
  // Local route overrides so the per-app split switches work immediately even
  // before the host echoes a new snapshot: the switch posts set_app_route (the
  // same real program action the main dashboard uses) and remembers the choice
  // locally so the row flips on screen. Once the host pushes the next
  // updateMonitorSnapshot the authoritative action replaces the override.
  const _nxLocalRoutes = {}; // processName -> action override
  const _nxLocalRouteTs = {}; // processName -> ms timestamp of the override
  const NX_ROUTE_CONFIRM_MS = 2500; // host echo window; past this the authoritative action wins

  function nxActionFor(a) {
    if (a && a.processName && Object.prototype.hasOwnProperty.call(_nxLocalRoutes, a.processName)) {
      return _nxLocalRoutes[a.processName];
    }
    return a ? (a.action || '') : '';
  }

  function nxIsTunneled(action) { return action === 'vpn' || action === 'vpn+proxy' || action === 'proxy'; }

  // Shared keyboard decision logic loaded via <script src="../route-keys.js">.
  // Falls back to a local copy if the module ever fails to load, so the skin
  // keeps working standalone. The authoritative logic lives in route-keys.js
  // and is covered by route-keys.test.js.
  const nxRouteFromKey = (typeof AoGPNRouteKeys !== 'undefined' && AoGPNRouteKeys.routeFromKey)
    ? AoGPNRouteKeys.routeFromKey
    : function (key, currentAction) {
        if (key === 'Enter' || key === ' ') return nxIsTunneled(currentAction) ? 'direct' : 'vpn';
        if (key === 'ArrowRight') return nxIsTunneled(currentAction) ? null : 'vpn';
        if (key === 'ArrowLeft') return nxIsTunneled(currentAction) ? 'direct' : null;
        return null;
      };
  const nxRouteCycle = (typeof AoGPNRouteKeys !== 'undefined' && AoGPNRouteKeys.routeCycle)
    ? AoGPNRouteKeys.routeCycle
    : function (action) {
        const order = ['vpn', 'direct', 'block', 'warp'];
        const cur = nxIsTunneled(action) ? 'vpn' : action;
        const idx = order.indexOf(cur);
        return order[(idx + 1) % order.length];
      };

  // Toggle one boost app's route (tunneled <-> direct) through the host. The
  // row flips immediately via the local override; a toast mirrors the change.
  function nxToggleAppRoute(processName) {
    if (!processName) return;
    const S = nxState();
    const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === processName);
    const cur = nxActionFor(app || { processName });
    const next = nxIsTunneled(cur) ? 'direct' : 'vpn';
    _nxLocalRoutes[processName] = next;
    _nxLocalRouteTs[processName] = Date.now();
    const displayName = app ? (app.displayName || processName) : processName;
    if (B) B.postToHost({ action: 'set_app_route', processName, displayName, route: next });
    nxToastMsg('ROUTE ' + next.toUpperCase() + ' · ' + displayName.toUpperCase());
    nxSyncAll();
  }

  // Every boost app from the main dashboard's split table shows here — tunneled
  // (vpn/vpn+proxy), direct and block alike — so the skin mirrors the dashboard
  // route selector exactly. The switch simply reflects the effective action.
  function nxGameItems() {
    const S = nxState();
    return (S.monitorSnapshot.apps || []);
  }

  function renderNexusGameList() {
    const root = nxRef('nxGameList');
    if (!root) return;
    const apps = nxGameItems().slice(0, 6);
    const icons = ['☢', '◈', '◉', '◆', '▣', '⌁'];
    if (apps.length === 0) {
      root.innerHTML = '<p class="nx-empty center slim">' + escHtml(nxT('noGames')) + '</p>';
      return;
    }
    root.innerHTML = apps.map((item, i) => nxGameRow(item, icons[i % icons.length])).join('');
    nxWireGameSwitches(root);
  }

  function nxGameRow(item, icon) {
    const S = nxState();
    const name = item.displayName || item.processName || item.value || 'App';
    const pname = item.processName || item.value || name;
    // The route flips through the same local-override mechanism as CYBER, so
    // the switch reflects the EFFECTIVE action (not just the last host echo).
    const action = nxActionFor(item);
    const running = item.isRunning === true;
    // Label follows the effective route: tunneled apps show "VPN", anything
    // toggled to direct shows "DIRECT", and unknown/edge actions fall back to
    // "TUNNELED" so the row never lies about the route.
    const kindLabel = nxIsTunneled(action)
      ? nxT('kind.vpn')
      : (action === 'direct' ? 'DIRECT' : (action === 'block' ? 'BLOCKED' : nxT('kind.tunneled')));
    const on = nxIsTunneled(action);
    return '<div class="nx-game">'
      + '<div class="nx-gameIcon">' + (nxAppIconUri(item.exePath) ? '<img src="' + nxAppIconUri(item.exePath) + '" alt="" draggable="false">' : (icon || '◈')) + '</div>'
      + '<div><b>' + escHtml(name) + '</b><span>' + escHtml(kindLabel.toUpperCase()) + (running ? ' · ' + escHtml(nxT('running')) : '') + '</span></div>'
      + '<div class="nx-switch' + (on ? ' on' : '') + '" role="switch" aria-checked="' + on + '" tabindex="0" data-nx-pname="' + escHtml(pname) + '" title="Toggle routing (Enter/Space or ←/→)"></div>'
      + '</div>';
  }

  // Wire the per-app switches inside a freshly rendered list: click toggles the
  // route, Enter/Space flip it, and ←/→ move it in one direction only (right =
  // tunnel on, left = tunnel off) without toggling back — same as CYBER.
  function nxWireGameSwitches(root) {
    if (!root) return;
    root.querySelectorAll('.nx-switch[data-nx-pname]').forEach(sw => {
      sw.addEventListener('click', (e) => { e.stopPropagation(); nxToggleAppRoute(sw.dataset.nxPname); });
      sw.addEventListener('keydown', (e) => {
        // Keyboard contract lives in the shared pure module (route-keys.js,
        // tested by route-keys.test.js): Enter/Space flip, ←/→ one direction.
        const pname = sw.dataset.nxPname;
        const S = nxState();
        const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === pname);
        const cur = nxActionFor(app || { processName: pname });
        const next = nxRouteFromKey(e.key, cur);
        if (next) {
          e.preventDefault();
          _nxLocalRoutes[pname] = next;
          _nxLocalRouteTs[pname] = Date.now();
          const displayName = app ? (app.displayName || pname) : pname;
          if (B) B.postToHost({ action: 'set_app_route', processName: pname, displayName, route: next });
          nxToastMsg('ROUTE ' + next.toUpperCase() + ' · ' + displayName.toUpperCase());
          nxSyncAll();
        }
      });
    });
  }

  // ---------- Game Profiles (Boost management) sub-view ----------
  // Full parity with the standard dashboard's Game Boost view: route mode pills,
  // auto-connect, running-apps picker, Add EXE and the per-app route table
  // (route select, live route, real ping, traffic, remove).
  let _nxBoostFilter = '';
  let _nxBoostPickerOpen = false;
  let _nxBoostDomainOpen = false;
  let _nxBoostDomainDraft = '';

  function nxRealPingCell(item) {
    const realBefore = (item.beforePingText && item.beforePingText !== '—') ? item.beforePingText : null;
    const realAfter = (item.afterPingText && item.afterPingText !== '—') ? item.afterPingText : null;
    if (realBefore && realAfter) {
      const delta = item.pingDeltaText || '';
      return '<span class="nx-pingCell ' + (delta.startsWith('+') ? 'bad' : 'good') + '" title="' + escHtml(nxT('boost.before') + ' ' + realBefore + ' → ' + nxT('boost.after') + ' ' + realAfter) + '">' + escHtml(realBefore + ' → ' + realAfter + (delta ? ' (' + delta + ')' : '')) + '</span>';
    }
    if (realBefore) return '<span class="nx-pingCell">' + escHtml(nxT('boost.before') + ' ' + realBefore) + '</span>';
    if (realAfter) return '<span class="nx-pingCell">' + escHtml(nxT('boost.after') + ' ' + realAfter) + '</span>';
    return '<span class="nx-pingCell none">—</span>';
  }

  // Canlı hedef adresleri (mihomo /connections metadata): ilk iki uç nokta
  // gösterilir, fazlası "+N" ile özetlenir; tam liste tooltip'te kalır.
  function nxTargetIpsCell(item) {
    const raw = (item.activeIps || '').trim();
    if (!raw) return '<span class="nx-pingCell none">—</span>';
    const ips = raw.split(',').map(s => s.trim()).filter(Boolean);
    const shown = ips.slice(0, 2).join(', ');
    const extra = ips.length > 2 ? ' +' + (ips.length - 2) : '';
    return '<span class="nx-pingCell" title="' + escHtml(raw) + '">' + escHtml(shown + extra) + '</span>';
  }

  function nxRouteTagFor(action) { return action === 'vpn' ? 'proxy' : (['direct', 'block', 'warp'].includes(action) ? action : 'unknown'); }

  // Gerçek exe ikonu: ana dashboard'un AppIconService'ten çözüp skinBridge
  // üzerinden paylaştığı shell ikonları (path -> data URI). İkon yoksa iki
  // harfli yer tutucu kutu basılır (standalone demo'da da aynı davranır).
  function nxAppIconUri(exePath) {
    if (!exePath || !B) return '';
    try {
      const icons = B.appIcons;
      if (!icons) return '';
      return icons[String(exePath).trim().toLowerCase()] || '';
    } catch (e) { return ''; }
  }

  function nxAppIcon(exePath, fallbackText, cls) {
    const uri = nxAppIconUri(exePath);
    const label = String(fallbackText || 'APP').slice(0, 2).toUpperCase() || '?';
    if (uri) return '<span class="nx-appIco ' + (cls || '') + '"><img src="' + uri + '" alt="" draggable="false"></span>';
    return '<span class="nx-appIco ' + (cls || '') + '">' + escHtml(label) + '</span>';
  }

  // order { index, total, filterActive } lets the row disable the move buttons at
  // the list edges and hide them entirely while a filter is active (the move
  // targets the real position, not the filtered one).
  function nxBoostRow(item, blacklist, order) {
    const S = nxState();
    const processName = item.processName || item.value || '';
    const eff = blacklist ? (item.action === 'direct' ? 'vpn' : (item.action === 'block' ? 'block' : 'direct')) : item.action;
    const effLabel = (blacklist && eff !== item.action)
      ? ((S.routeLabels && S.routeLabels[eff]) || eff)
      : ((S.routeLabels && S.routeLabels[item.action]) || item.routeText || item.action);
    const dirMark = blacklist && item.action === 'vpn'
      ? '<span class="nx-dirChip">⇄ ' + escHtml(nxT('dir.rowExcluded')) + '</span>'
      : blacklist && item.action === 'direct'
        ? '<span class="nx-dirChip alt">⇄ ' + escHtml(nxT('dir.rowTunneled')) + '</span>'
        : '';
    const live = item.liveRouteTag
      ? '<span class="nx-routeBadge ' + nxRouteTagFor(item.liveRouteTag) + '">' + escHtml(item.liveRouteText || item.liveRouteTag) + '</span>'
      : '<span class="nx-idle">' + escHtml(nxT('boost.idle')) + '</span>';
    // WARP rozeti seçilen egress düğümünü gösterir ("WARP · İtalya" gibi).
    if (item.action === 'warp' && item.warpNodeIndexId && B && Array.isArray(B.nodes)) {
      const nxWarpNode = B.nodes.find(n => String(n.indexId) === String(item.warpNodeIndexId));
      if (nxWarpNode && nxWarpNode.name && effLabel && effLabel.toUpperCase() !== 'DIRECT' && effLabel.toUpperCase() !== 'BLOCKED') {
        effLabel = effLabel + ' · ' + nxWarpNode.name;
      }
    }
    const running = item.isRunning === true;
    const normalized = ['proxy', 'vpn+proxy'].includes(item.action) ? 'vpn' : item.action;
    const value = ['vpn', 'direct', 'block', 'warp'].includes(normalized) ? normalized : '';
    const opts = [['', nxT('route.assign')], ['vpn', blacklist ? nxT('dir.selectVpn') : nxT('route.vpn')], ['direct', blacklist ? nxT('dir.selectDirect') : nxT('route.direct')], ['block', blacklist ? nxT('dir.selectBlock') : nxT('route.block')], ['warp', blacklist ? nxT('dir.selectWarp') : nxT('route.warp')]]
      .map(o => '<option value="' + o[0] + '"' + (o[0] === value ? ' selected' : '') + '>' + escHtml(o[1]) + '</option>').join('');
    // WARP rotasındaki satırlarda düğüm seçici görünür (mevcut düğüm listesinden
    // bu uygulamanın WARP egress düğümü seçilir; boş = varsayılan/aktif düğüm).
    const warpNode = (value === 'warp' || item.warpNodeIndexId)
      ? '<select class="nx-routeSel" data-nx-warpnode="' + escHtml(processName) + '" data-nx-display="' + escHtml(item.displayName || processName) + '">'
        + '<option value="">' + escHtml(nxT('warp.nodeDefault')) + '</option>'
        + (B && Array.isArray(B.nodes)
            ? B.nodes.map(n => {
              const id = n.indexId || n.key || '';
              return id
                ? '<option value="' + escHtml(id) + '"' + (String(id) === String(item.warpNodeIndexId || '') ? ' selected' : '') + '>' + escHtml(n.name || n.id || id) + '</option>'
                : '';
            }).join('')
            : '')
        + '</select>'
      : '';
    return '<div class="nx-trow" data-nx-pname="' + escHtml(processName) + '">'
      + '<div class="nx-tcol prog"><div class="nx-progCell">' + nxAppIcon(item.exePath, item.displayName || processName) + '<div class="nx-progTxt"><b>' + escHtml(item.displayName || processName) + '</b><span>' + escHtml(processName) + '</span></div></div></div>'
      + '<div class="nx-tcol"><span class="nx-routeBadge ' + nxRouteTagFor(eff) + '">' + escHtml(effLabel) + '</span>' + dirMark + '</div>'
      + '<div class="nx-tcol">' + live + '</div>'
      + '<div class="nx-tcol"><span class="nx-status' + (running ? ' on' : '') + '"><i></i>' + escHtml(running ? nxT('boost.statusRunning') : nxT('boost.statusNotRunning')) + '</span></div>'
      + '<div class="nx-tcol">' + nxRealPingCell(item) + '</div>'
      + '<div class="nx-tcol">' + nxTargetIpsCell(item) + '</div>'
      + '<div class="nx-tcol down">' + escHtml(item.downloadText || '—') + '</div>'
      + '<div class="nx-tcol up">' + escHtml(item.uploadText || '—') + '</div>'
      + '<div class="nx-tcol"><select class="nx-routeSel" data-nx-route="' + escHtml(processName) + '" data-nx-display="' + escHtml(item.displayName || processName) + '">' + opts + '</select>' + warpNode + '</div>'
      + '<div class="nx-tcol rm">'
      + (order && order.filterActive
          ? ''
          : '<button type="button" class="nx-mvBtn' + (order && order.index <= 0 ? ' off' : '') + '" data-nx-move="up" data-nx-etype="' + escHtml(item.entryType || 'app') + '" data-nx-value="' + escHtml(processName) + '" title="Move up" aria-label="Move up">↑</button>'
            + '<button type="button" class="nx-mvBtn' + (order && order.index >= order.total - 1 ? ' off' : '') + '" data-nx-move="down" data-nx-etype="' + escHtml(item.entryType || 'app') + '" data-nx-value="' + escHtml(processName) + '" title="Move down" aria-label="Move down">↓</button>')
      + '<button type="button" class="nx-rmBtn" data-nx-rm="' + escHtml(processName) + '" data-nx-rm-etype="' + escHtml(item.entryType || 'app') + '" data-nx-rm-value="' + escHtml(processName) + '" title="' + escHtml(nxT('boost.remove')) + '">✕</button>'
      + '</div>'
      + '</div>';
  }

  function nxRenderGameProfiles() {
    const root = nxRef('nxGameProfiles');
    if (!root) return;
    // Don't clobber an open picker filter / table filter while typing.
    if (root.contains(document.activeElement)) return;
    const S = nxState();
    const all = nxGameItems();
    const filter = _nxBoostFilter.trim().toLowerCase();
    const apps = filter ? all.filter(item => {
      const name = (item.displayName || item.processName || item.value || '').toLowerCase();
      const action = ((S.routeLabels && S.routeLabels[item.action]) || item.routeText || '').toLowerCase();
      const live = (item.liveRouteText || '').toLowerCase();
      return name.includes(filter) || action.includes(filter) || live.includes(filter);
    }) : all;
    const blacklist = !!(S.monitorSnapshot && S.monitorSnapshot.invertManualRouting === true) && (S.mode === 'gpn' || S.splitMode === 'manual');
    const autoGame = !!(S.monitorSnapshot && S.monitorSnapshot.autoConnectOnGameStart === true);
    const countTxt = filter
      ? apps.length + ' ' + nxT('boost.of') + ' ' + all.length + ' ' + nxT('boost.entries')
      : apps.length + ' ' + nxT('boost.entries');
    const picker = _nxBoostPickerOpen
      ? '<div class="nx-picker"><div class="nx-pickerHead"><div><b>' + escHtml(nxT('boost.runningAppsTitle')) + '</b><span>' + escHtml(nxT('boost.runningAppsDesc')) + '</span></div>'
        + '<button type="button" class="nx-btn sm" data-nx-pickerclose>×</button></div>'
        + '<input type="search" class="nx-inp nx-pickerFilter" data-nx-pickerfilter placeholder="' + escHtml(nxT('boost.filterRunning')) + '" value="' + escHtml(_nxBoostFilter) + '">'
        + '<div class="nx-pickerList">' + (S.processCatalog.length
            ? S.processCatalog.filter(p => !_nxBoostFilter || (p.displayName + ' ' + p.processName + ' ' + (p.exePath || '')).toLowerCase().includes(_nxBoostFilter.toLowerCase())).map(p =>
                '<button type="button" class="nx-pickerRow" data-nx-pid="' + escHtml(String(p.pid)) + '" data-nx-pname="' + escHtml(p.processName || '') + '" data-nx-dname="' + escHtml(p.displayName || '') + '">' + nxAppIcon(p.exePath, p.displayName || p.processName, 'nx-pickerIco') + '<span><b>' + escHtml(p.displayName || p.processName) + '</b><em>' + escHtml((p.exePath || p.processName || '') + ' · PID ' + p.pid) + '</em></span></button>').join('')
            : '<p class="nx-empty center slim">' + escHtml(nxT('boost.noProcesses')) + '</p>') + '</div></div>'
      : '';
    root.innerHTML =
      '<div class="nx-toolbar">'
      + '<button type="button" class="nx-btn" data-nx-boost="refresh">⟳ <span>' + escHtml(nxT('boost.refresh')) + '</span></button>'
      + '<button type="button" class="nx-btn" data-nx-boost="apps">▣ <span>' + escHtml(nxT('boost.runningApps')) + '</span></button>'
      + '<button type="button" class="nx-btn primary" data-nx-boost="addexe">' + escHtml(nxT('boost.addExe')) + '</button>'
      + '<button type="button" class="nx-btn" data-nx-bszapi>⚡ <span>BSG API → WARP</span></button>'
      + '<button type="button" class="nx-btn' + (_nxBoostDomainOpen ? ' active' : '') + '" data-nx-adddomain>＋ <span>' + escHtml(nxT('boost.addDomain').replace(/^＋\s*/, '')) + '</span></button>'
      + '</div>'
      + picker
      + (_nxBoostDomainOpen
          ? '<div class="nx-domainRow">'
            + '<input type="text" class="nx-inp nx-domainValue" data-nx-domainvalue placeholder="' + escHtml(nxT('boost.domainPlaceholder')) + '" value="' + escHtml(_nxBoostDomainDraft) + '" spellcheck="false">'
            + '<select class="nx-inp nx-domainSel" data-nx-domainaction aria-label="' + escHtml(nxT('boost.domainRouteAria')) + '">'
            + '<option value="vpn" selected>' + escHtml(nxT('route.vpn')) + '</option>'
            + '<option value="direct">' + escHtml(nxT('route.direct')) + '</option>'
            + '<option value="block">' + escHtml(nxT('route.block')) + '</option>'
            + '<option value="warp">' + escHtml(nxT('route.warp')) + '</option>'
            + '</select>'
            + '<button type="button" class="nx-btn primary" data-nx-domainsubmit>' + escHtml(nxT('boost.addRule')) + '</button>'
            + '</div>'
          : '')
      + '<div class="nx-modeRow">'
      + '<span class="nx-subLabel">' + escHtml(nxT('boost.routeMode')) + '</span>'
      + '<div class="nx-seg">'
      + '<button type="button" class="nx-segOpt' + (S.splitMode === 'off' ? ' active' : '') + '" data-nx-split="off">' + escHtml(nxT('boost.off')) + '</button>'
      + '<button type="button" class="nx-segOpt' + (S.splitMode === 'vpn' ? ' active' : '') + '" data-nx-split="vpn">' + escHtml(nxT('boost.globalVpn')) + '</button>'
      + '<button type="button" class="nx-segOpt' + (S.splitMode === 'manual' ? ' active' : '') + '" data-nx-split="manual">' + escHtml(nxT('boost.gpnTunnel')) + '</button>'
      + '</div></div>'
      + '<div class="nx-autoRow"><div><b>' + escHtml(nxT('boost.autoGame')) + '</b><span>' + escHtml(nxT('boost.autoGameDesc')) + '</span></div>'
      + '<div class="nx-switch' + (autoGame ? ' on' : '') + '" data-nx-autogame role="switch" aria-checked="' + autoGame + '"></div></div>'
      + (blacklist ? '<div class="nx-banner warn"><div class="nx-bannerRow"><span class="nx-bannerIco">⚠</span><b>' + escHtml(nxT('dir.blacklistTableHint')) + '</b></div></div>' : '')
      + '<div class="nx-appHead"><div><b>' + escHtml(nxT('boost.appRoutes')) + '</b><span>' + escHtml(nxT('boost.appRoutesDesc')) + '</span></div><span class="nx-subtle">' + escHtml(countTxt) + '</span></div>'
      + '<div class="nx-filterRow"><input type="search" class="nx-inp" data-nx-appfilter placeholder="' + escHtml(nxT('boost.filterApps')) + '" value="' + escHtml(_nxBoostFilter) + '"></div>'
      + (apps.length
          ? '<div class="nx-tbl"><div class="nx-thead">'
            + '<span>' + escHtml(nxT('boost.colProgram')) + '</span><span>' + escHtml(nxT('boost.colRoute')) + '</span><span>' + escHtml(nxT('boost.colLive')) + '</span><span>' + escHtml(nxT('boost.colStatus')) + '</span><span>' + escHtml(nxT('boost.colPing')) + '</span><span>' + escHtml(nxT('boost.colTarget')) + '</span><span>' + escHtml(nxT('boost.colDown')) + '</span><span>' + escHtml(nxT('boost.colUp')) + '</span><span>' + escHtml(nxT('boost.colChange')) + '</span><span class="rm"></span></div>'
            + apps.map(item => nxBoostRow(item, blacklist, { index: all.indexOf(item), total: all.length, filterActive: !!filter })).join('') + '</div>'
          : '<p class="nx-empty center">' + escHtml(nxT('boost.empty')) + '</p>')
      + '<p class="nx-hintLine nx-tip">💡 ' + escHtml(nxT('boost.tip')) + '</p>';

    // ---- wire ----
    root.querySelectorAll('[data-nx-boost]').forEach(btn => {
      btn.addEventListener('click', () => {
        const act = btn.dataset.nxBoost;
        if (act === 'refresh') { if (B) B.postToHost({ action: 'refresh_monitor' }); }
        else if (act === 'apps') {
          _nxBoostPickerOpen = !_nxBoostPickerOpen;
          if (_nxBoostPickerOpen && B) B.postToHost({ action: 'list_running_processes' });
          nxRenderGameProfiles();
        }
        else if (act === 'addexe') { if (B) B.postToHost({ action: 'add_app' }); }
      });
    });
    const pickerClose = root.querySelector('[data-nx-pickerclose]');
    if (pickerClose) pickerClose.addEventListener('click', () => { _nxBoostPickerOpen = false; _nxBoostFilter = ''; nxRenderGameProfiles(); });
    const pickerFilter = root.querySelector('[data-nx-pickerfilter]');
    if (pickerFilter) pickerFilter.addEventListener('input', () => { _nxBoostFilter = pickerFilter.value; nxRenderGameProfiles(); });
    root.querySelectorAll('[data-nx-pid]').forEach(row => {
      row.addEventListener('click', () => {
        if (!B) return;
        const pid = parseInt(row.dataset.nxPid, 10);
        if (!Number.isInteger(pid) || pid <= 0) return;
        B.postToHost({
          action: 'add_running_process',
          pid,
          processName: row.dataset.nxPname || '',
          displayName: row.dataset.nxDname || '',
        });
      });
    });
    const appFilter = root.querySelector('[data-nx-appfilter]');
    if (appFilter) appFilter.addEventListener('input', () => { _nxBoostFilter = appFilter.value; nxRenderGameProfiles(); });
    const bszBtn = root.querySelector('[data-nx-bszapi]');
    if (bszBtn) bszBtn.addEventListener('click', () => {
      // One-click curated BSG API rule (escapefromtarkov.com → WARP).
      if (B) B.postToHost({
        action: 'add_domain_route', value: 'escapefromtarkov.com', route: 'warp', displayName: 'BSG API (escapefromtarkov.com)'
      });
    });
    const domainToggle = root.querySelector('[data-nx-adddomain]');
    if (domainToggle) domainToggle.addEventListener('click', () => {
      _nxBoostDomainOpen = !_nxBoostDomainOpen;
      nxRenderGameProfiles();
    });
    const domainValue = root.querySelector('[data-nx-domainvalue]');
    if (domainValue) domainValue.addEventListener('input', () => { _nxBoostDomainDraft = domainValue.value; });
    const domainSubmit = root.querySelector('[data-nx-domainsubmit]');
    if (domainSubmit) domainSubmit.addEventListener('click', () => {
      const raw = (_nxBoostDomainDraft || '').trim();
      if (!raw || !B) return;
      const action = root.querySelector('[data-nx-domainaction]');
      B.postToHost({
        action: 'add_domain_route', value: raw, route: (action && action.value) || 'proxy', displayName: raw
      });
      _nxBoostDomainDraft = '';
      _nxBoostDomainOpen = false;
      nxRenderGameProfiles();
    });
    root.querySelectorAll('[data-nx-split]').forEach(btn => {
      btn.addEventListener('click', () => {
        const next = btn.dataset.nxSplit;
        if (B) B.postToHost({ action: 'set_split_mode', mode: next });
        SET('splitMode', next);
        nxRenderGameProfiles();
        nxRenderRoute();
      });
    });
    const auto = root.querySelector('[data-nx-autogame]');
    if (auto) auto.addEventListener('click', () => {
      const next = !auto.classList.contains('on');
      auto.classList.toggle('on', next);
      auto.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_auto_game_connect', enabled: next });
    });
    root.querySelectorAll('[data-nx-route]').forEach(sel => {
      sel.addEventListener('change', () => {
        if (!sel.value) return;
        if (B) B.postToHost({ action: 'set_app_route', processName: sel.dataset.nxRoute, displayName: sel.dataset.nxDisplay, route: sel.value });
      });
    });
    // Per-app WARP egress düğümü (Ayarlar → GPN bypass'ın yerine): warp rotasındaki
    // satırın yanındaki seçici, bu uygulamanın WARP trafiğinin hangi mevcut düğümden
    // çıkacağını belirler. Boş değer = varsayılan (aktif düğümün WARP egress'i).
    root.querySelectorAll('[data-nx-warpnode]').forEach(sel => {
      sel.addEventListener('change', () => {
        const nodeId = sel.value || '';
        const displayName = sel.dataset.nxDisplay || '';
        const nodeName = nodeId && B && B.nodes
          ? (() => { const n = B.nodes.find(x => String(x.indexId) === String(nodeId)); return n ? (n.name || '') : ''; })()
          : '';
        if (B) B.postToHost({
          action: 'set_app_route',
          processName: sel.dataset.nxWarpnode,
          displayName,
          route: 'warp',
          warpNodeIndexId: nodeId,
          warpNodeName: nodeName
        });
      });
    });
    root.querySelectorAll('[data-nx-rm]').forEach(btn => {
      btn.addEventListener('click', () => {
        const processName = btn.dataset.nxRm;
        if (!processName) return;
        const entryType = btn.dataset.nxRmEtype || 'app';
        let ok = true;
        if (typeof confirm === 'function') { try { ok = confirm('Remove "' + processName + '" from the routing list?'); } catch (e) { /* jsdom */ } }
        if (!ok || !B) return;
        if (entryType === 'app') {
          B.postToHost({ action: 'remove_app', processName });
        } else {
          B.postToHost({ action: 'remove_route', entryType, value: processName });
        }
      });
    });
    root.querySelectorAll('[data-nx-move]').forEach(btn => {
      btn.addEventListener('click', () => {
        if (btn.classList.contains('off')) return;
        if (B) B.postToHost({
          action: 'move_route',
          entryType: btn.dataset.nxEtype,
          value: btn.dataset.nxValue,
          direction: btn.dataset.nxMove
        });
      });
    });
    nxWireGameSwitches(root);
  }

  // ---------- smart route sub-view ----------
  function nxRenderRoute() {
    const S = nxState();
    const modeBtn = document.querySelector('[data-nx-mode="' + S.mode + '"]');
    if (modeBtn) document.querySelectorAll('.nx-segOpt[data-nx-mode]').forEach(b => b.classList.toggle('active', b === modeBtn));
    const trBtn = document.querySelector('[data-nx-transport="' + S.transport + '"]');
    if (trBtn) document.querySelectorAll('.nx-segOpt[data-nx-transport]').forEach(b => b.classList.toggle('active', b === trBtn));
    // Routing direction mirrors the dashboard's whitelist/blacklist split —
    // blacklist is the inverted manual-routing flag pushed in the monitor snapshot.
    const invert = !!(S.monitorSnapshot && S.monitorSnapshot.invertManualRouting === true);
    const dirBtn = document.querySelector('[data-nx-dir="' + (invert ? 'blacklist' : 'whitelist') + '"]');
    if (dirBtn) document.querySelectorAll('.nx-segOpt[data-nx-dir]').forEach(b => b.classList.toggle('active', b === dirBtn));
    const hint = nxRef('nxRouteHint');
    if (hint) {
      const m = nxT(S.mode === 'gpn' ? 'modeGpn' : 'modeGlobal');
      const t = nxT(S.transport === 'tun' ? 'captureHint.tun' : 'captureHint.proxy');
      const d = nxT(invert ? 'dir.blacklist' : 'dir.whitelist');
      hint.textContent = nxT('routeHint', {
        mode: m, capture: t, state: nxT(S.connected ? 'hint.live' : 'hint.idle')
      }) + ' · ' + d;
    }
  }

  // ---------- dashboard: GPN / Global panels + status banners ----------
  function nxFmtCount(n) {
    if (!n) return '0';
    if (n >= 1000000) return (n / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
    if (n >= 1000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
    return String(n);
  }
  function nxFmtBytes(n) {
    if (!n) return '0 B';
    if (n >= 1048576) return (n / 1048576).toFixed(1).replace(/\.0$/, '') + ' MB';
    if (n >= 1024) return (n / 1024).toFixed(1).replace(/\.0$/, '') + ' KB';
    return n + ' B';
  }

  // Advanced & Diagnostics collapse state survives the 2 s re-render.
  let _nxAdvOpen = false;

  // ---- status banners (connection error / leak / ok / TUN admin / locks) ----
  function nxRenderBanners() {
    const root = nxRef('nxBanners');
    if (!root) return;
    const S = nxState();
    const banners = [];

    const err = S.connectionError;
    if (err && (typeof err === 'string' ? err : err.message)) {
      const msg = typeof err === 'string' ? err : err.message;
      const details = (typeof err === 'object' && err.details) ? String(err.details) : '';
      const elevation = typeof err === 'object' && err.elevation === true;
      banners.push('<div class="nx-banner err"><div class="nx-bannerRow"><span class="nx-bannerIco">⚠</span><b>' + escHtml(msg) + '</b>'
        + (elevation ? '<button type="button" class="nx-btn sm" data-nx-relaunch>⚡ ' + escHtml(nxT('banner.relaunch')) + '</button>' : '')
        + '</div>' + (details ? '<p class="nx-bannerDetails">' + escHtml(details) + '</p>' : '') + '</div>');
    }

    const ip = S.realIpState || {};
    const isConnected = S.connected;
    const hasTunnelIp = typeof ip.tunnelIp === 'string' && ip.tunnelIp.length > 0;
    const isTunneled = isConnected && ip.tunnelVerified === true;
    const leaking = isConnected && hasTunnelIp && !isTunneled && ip.ispCached === true;
    if (leaking) {
      banners.push('<div class="nx-banner err"><div class="nx-bannerRow"><span class="nx-bannerIco">⚠</span><b>' + escHtml(nxT('banner.leak')) + '</b></div></div>');
    } else if (isTunneled) {
      banners.push('<div class="nx-banner ok"><div class="nx-bannerRow"><span class="nx-bannerIco">✓</span><b>' + escHtml(nxT('banner.ok', { ip: ip.tunnelIp || '—', isp: ip.ispIp || ip.directIp || '—' })) + '</b></div></div>');
    }

    // TUN without elevation locks CONNECT (mirrors updateTransportLock).
    if (S.transport === 'tun' && S.isAdmin === false && !isConnected) {
      banners.push('<div class="nx-banner warn"><div class="nx-bannerRow"><span class="nx-bannerIco">⚠</span><b>' + escHtml(nxT('banner.tunAdmin')) + '</b>'
        + '<button type="button" class="nx-btn sm" data-nx-relaunch>⚡ ' + escHtml(nxT('banner.relaunch')) + '</button></div></div>');
    }
    // Global VPN locks capture to TUN.
    if (S.mode === 'vpn') {
      banners.push('<div class="nx-banner info"><div class="nx-bannerRow"><span class="nx-bannerIco">🔒</span><b>' + escHtml(nxT('banner.captureLock')) + '</b></div></div>');
    }
    // TUN capture leaves the OS proxy preference untouched.
    const isProxyOn = (v) => Number(v) === 1 || Number(v) === 3;
    if (S.transport === 'tun' && !isConnected && isProxyOn(S.systemProxyMode)) {
      banners.push('<div class="nx-banner info"><div class="nx-bannerRow"><span class="nx-bannerIco">💡</span><b>' + escHtml(nxT('banner.tunProxy')) + '</b></div></div>');
    }

    root.innerHTML = banners.join('');
    root.querySelectorAll('[data-nx-relaunch]').forEach(btn => {
      btn.addEventListener('click', () => { if (B) B.postToHost({ action: 'app_control', command: 'reboot_as_admin' }); });
    });
  }

  // ---- dashboard boost card (assigned GPN game) ----
  function nxBoostCard(item) {
    const S = nxState();
    const label = (item.displayName || item.processName || item.value || '').slice(0, 4).toUpperCase() || 'APP';
    const routeLabel = (S.routeLabels && S.routeLabels[item.action]) || 'VPN';
    const isRunning = item.isRunning === true;
    const realBefore = (item.beforePingText && item.beforePingText !== '—') ? item.beforePingText : null;
    const realAfter = (item.afterPingText && item.afterPingText !== '—') ? item.afterPingText : null;
    const primaryLatency = realAfter || realBefore || item.latencyText || '—';
    const delta = (realBefore && realAfter && item.pingDeltaText) ? item.pingDeltaText : '';
    let latencyCls = 'nx-latNone';
    if (primaryLatency !== '—') {
      const ms = Number.isFinite(item.afterPingMs) ? item.afterPingMs : (Number.isFinite(item.beforePingMs) ? item.beforePingMs : item.latencyMs);
      latencyCls = ms < 80 ? 'nx-latGood' : ms < 180 ? 'nx-latMid' : 'nx-latBad';
    }
    let realLine = '';
    if (realBefore && realAfter) {
      const via = S.activeGpnServer ? ' · ' + escHtml(nxT('boost.viaNode', { node: S.activeGpnServer })) : '';
      realLine = '<span class="nx-boostReal ' + (delta.startsWith('+') ? 'bad' : 'good') + '" title="' + escHtml(nxT('boost.before') + ' ' + realBefore + ' → ' + nxT('boost.after') + ' ' + realAfter) + '">' + escHtml(realBefore + ' → ' + realAfter + (delta ? ' (' + delta + ')' : '') + via) + '</span>';
    } else if (realAfter) {
      const via = S.activeGpnServer ? ' · ' + escHtml(nxT('boost.viaNode', { node: S.activeGpnServer })) : '';
      realLine = '<span class="nx-boostReal">' + escHtml(nxT('boost.after') + ' ' + realAfter + via) + '</span>';
    } else if (realBefore) {
      realLine = '<span class="nx-boostReal">' + escHtml(nxT('boost.before') + ' ' + realBefore) + '</span>';
    }
    return '<div class="nx-boost' + (isRunning ? '' : ' dim') + '">'
      + '<div class="nx-boostIcon">' + (nxAppIconUri(item.exePath) ? '<img src="' + nxAppIconUri(item.exePath) + '" alt="" draggable="false">' : escHtml(label)) + '</div>'
      + '<div class="nx-boostTxt"><b>' + escHtml(item.displayName || item.processName || item.value) + '</b>'
      + '<span>' + escHtml(isRunning ? nxT('boost.routeActive', { route: routeLabel }) : nxT('boost.idleNoBoost')) + '</span>'
      + realLine + '</div>'
      + '<div class="nx-boostRight"><span class="nx-boostMs ' + latencyCls + '">' + escHtml(primaryLatency) + '</span>'
      + '<i class="nx-boostDot ' + (isRunning ? 'on' : '') + '"></i></div>'
      + '</div>';
  }

  // ---- server cluster rows (live Italy/Germany latency from host probes) ----
  function nxClusterRows(S) {
    const servers = S.gpnServers || [];
    const active = servers.filter(s => s.isEnabled);
    if (!active.length) return '<p class="nx-empty center slim">' + escHtml(nxT('gpn.cluster.empty')) + '</p>';
    let openCount = 0;
    let bestMs = null;
    const rows = active.map((s, idx) => {
      const probe = (S.gpnServerProbes || []).find(p => String(p.serverId) === String(s.serverId)) || null;
      let delayMs = null, loss = null, udp = '—', phys = false;
      if (probe) {
        const ok = probe.isSuccess && Number.isFinite(probe.delayMs) && probe.delayMs >= 0;
        delayMs = ok ? probe.delayMs : null;
        loss = ok ? probe.lossPercent : null;
        udp = probe.udpStatus || (ok ? 'Open' : '—');
        if (delayMs == null && probe.udpStatus === 'Open' && Number.isFinite(probe.udpRoundTripMs) && probe.udpRoundTripMs >= 0) {
          delayMs = probe.udpRoundTripMs;
          loss = 0;
        }
        phys = probe.measuredOverPhysicalNic === true;
      }
      const isBest = delayMs != null && udp === 'Open' && (bestMs === null || delayMs < bestMs);
      if (isBest) bestMs = delayMs;
      if (udp === 'Open') openCount++;
      const udpDot = udp === 'Open' ? 'open' : (udp === 'Blocked' || udp === 'HandshakeNoResponse') ? 'blocked' : 'warn';
      return '<div class="nx-clusterRow">'
        + '<i class="nx-udpDot ' + udpDot + '"></i>'
        + '<div class="nx-clusterTxt"><b>' + escHtml(s.name || s.serverId) + (isBest ? ' <span class="nx-bestPill">' + escHtml(nxT('gpn.cluster.best')) + '</span>' : '') + '</b>'
        + '<span class="mono">' + escHtml((s.endpointHost || '') + ':' + (s.endpointPort || '')) + ' · ' + escHtml(udp) + (loss != null && loss > 0 ? ' · ' + Math.round(loss) + '%' : '') + '</span></div>'
        + '<span class="nx-clusterMs ' + (delayMs != null ? 'on' : '') + '">' + (delayMs != null ? Math.round(delayMs) + ' ms' : '—') + '</span>'
        + '</div>';
    });
    const phys = active.some(s => { const p = (S.gpnServerProbes || []).find(x => String(x.serverId) === String(s.serverId)); return p && p.measuredOverPhysicalNic === true; });
    const pingBadge = openCount + ' ' + nxT('gpn.cluster.open') + (bestMs != null ? ' · ' + Math.round(bestMs) + ' ms' : '');
    return '<div class="nx-cardHead nx-clusterHead"><div class="nx-label">' + escHtml(nxT('gpn.cluster.title')) + '</div>'
      + '<span class="nx-clusterPing">' + escHtml(pingBadge) + '</span>'
      + (phys ? '<span class="nx-egressHint" title="' + escHtml(nxT('gpn.cluster.egressLabel')) + '">' + escHtml(nxT('gpn.cluster.egressLabel')) + '</span>' : '')
      + '<button type="button" class="nx-btn sm" data-nx-clusterprobe>⚡ <span>' + escHtml(nxT('gpn.cluster.measure')) + '</span></button></div>'
      + '<div class="nx-clusterList">' + rows.join('') + '</div>';
  }

  // ---- Advanced & Diagnostics cards ----
  function nxUdpCls(status) {
    if (status === 'Open') return 'ok';
    if (status === 'Blocked' || status === 'HandshakeNoResponse') return 'bad';
    if (status === 'NoResponse') return 'warn';
    return 'muted';
  }

  function nxCandidateCard(S) {
    const data = S.gpnSelectionPrediction;
    const body = (!data || !data.mode)
      ? '<p class="nx-empty center slim">' + escHtml(nxT('gpn.candidate.empty')) + '</p>'
      : (() => {
          const wg = data.mode === 'WireGuardUDP';
          const modeBadge = wg
            ? '<span class="nx-tag ok">' + escHtml(nxT('gpn.candidate.wireguard')) + '</span>'
            : '<span class="nx-tag bad">' + escHtml(nxT('gpn.candidate.v2ray')) + '</span>';
          const rows = (data.servers || []).map(s => {
            const sel = s.selected ? '<b class="nx-selDot">●</b> ' : '';
            return '<div class="nx-monoRow"><span>' + sel + escHtml(s.name) + ' <em class="dim">' + (s.delayMs >= 0 ? s.delayMs + ' ms' : '—') + '</em></span>'
              + '<span><span class="nx-tag ' + nxUdpCls(s.udpStatus) + '">' + escHtml(s.udpStatus || '—') + '</span>'
              + (s.selected ? ' <span class="nx-selTxt">' + escHtml(nxT('gpn.candidate.selected')) + '</span>' : '') + '</span></div>';
          }).join('');
          return '<div class="nx-monoHead"><b>' + escHtml(data.bestName || nxT('gpn.candidate.empty')) + '</b>'
            + '<em class="dim">' + (data.pingMs >= 0 ? data.pingMs + ' ms' : '—') + '</em>'
            + '<span class="ml-auto">' + modeBadge + '</span></div>'
            + '<p class="nx-monoReason" title="' + escHtml(data.reason || '') + '">' + escHtml(data.reason || '') + '</p>'
            + '<div class="nx-monoList">' + rows + '</div>';
        })();
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.candidate.title')) + '</div>'
      + '<span class="nx-subtle mono">' + (data && data.measuredAt ? escHtml(new Date(data.measuredAt).toLocaleTimeString()) : '') + '</span></div>'
      + '<div>' + body + '</div></div>';
  }

  function nxPidPoolCard(S) {
    const data = S.gpnPidPool || {};
    const names = (data && Array.isArray(data.targetNames)) ? data.targetNames : [];
    const pids = (data && Array.isArray(data.pids)) ? data.pids : [];
    const watching = !!(data && data.watching);
    const running = !!(data && data.targetRunning);
    const sourceStatus = (data && data.sourceStatus) || 'Healthy';
    let stateCls = 'muted', stateTxt = nxT('gpn.pidpool.idle');
    if (watching) {
      if (sourceStatus === 'Fatal') { stateCls = 'bad'; stateTxt = nxT('gpn.pidpool.fatal') + ' · ' + nxT('gpn.pidpool.offlineShort'); }
      else if (sourceStatus === 'Degraded') { stateCls = 'warn'; stateTxt = nxT('gpn.pidpool.degraded') + (running ? ' · ' + pids.length + ' ' + nxT('gpn.pidpool.pids') : ' · ' + nxT('gpn.pidpool.offlineShort')); }
      else { stateCls = 'ok'; stateTxt = nxT('gpn.pidpool.watching') + (running ? ' · ' + pids.length + ' ' + nxT('gpn.pidpool.pids') : ' · ' + nxT('gpn.pidpool.offlineShort')); }
    }
    const body = !names.length
      ? '<p class="nx-empty center slim">' + escHtml(nxT('gpn.pidpool.empty')) + '</p>'
      : '<div class="nx-chipWrap">' + names.map(n => '<span class="nx-chip violet">' + escHtml(n) + '</span>').join('')
        + (pids.length ? '</div><p class="nx-monoPids">' + escHtml(pids.join(' · ')) + '</p>' : '</div><p class="nx-empty center slim">' + escHtml(nxT('gpn.pidpool.offlineShort')) + '</p>');
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.pidpool.title')) + '</div>'
      + '<span class="nx-tag ' + stateCls + '">' + escHtml(stateTxt) + '</span>'
      + '<div class="ml-auto nx-gpnActions">'
      + '<button type="button" class="nx-btn sm" data-nx-pidpool="start">' + escHtml(nxT('gpn.pidpool.start')) + '</button>'
      + '<button type="button" class="nx-btn sm" data-nx-pidpool="stop">' + escHtml(nxT('gpn.pidpool.stop')) + '</button>'
      + '<button type="button" class="nx-btn sm" data-nx-pidpool="refresh">' + escHtml(nxT('gpn.pidpool.refresh')) + '</button></div></div>'
      + '<div>' + body + '</div></div>';
  }

  // Diag beslemesini temizleme yalnızca yereldir (ana dashboard'da da host'a
  // gitmez): temizlik anından önceki satırlar gizlenir, yenileri görünmeye devam eder.
  let _nxDiagClearedAt = 0;

  function nxCaptureCard(S) {
    const data = S.gpnCaptureStats;
    const total = (data && data.totalPackets) ? nxFmtCount(data.totalPackets) : '0';
    const chips = data && data.totalPackets
      ? ['<span class="nx-chip cyan">' + nxFmtCount(data.udpPackets || 0) + ' UDP</span>']
        .concat(data.tcpPackets ? '<span class="nx-chip">' + nxFmtCount(data.tcpPackets) + ' TCP</span>' : [])
        .concat(data.icmpPackets ? '<span class="nx-chip">' + nxFmtCount(data.icmpPackets) + ' ICMP</span>' : [])
        .concat(['<span class="nx-chip">' + nxFmtBytes(data.totalBytes || 0) + '</span>'])
        .concat(['<span class="nx-chip">out ' + nxFmtCount(data.outboundPackets || 0) + '</span>'])
        .concat(['<span class="nx-chip">in ' + nxFmtCount(data.inboundPackets || 0) + '</span>'])
      : [];
    const flows = (data && data.topFlows && data.topFlows.length)
      ? data.topFlows.map(f => '<div class="nx-monoRow"><span>' + escHtml(f.peerEndpoint) + ' <em class="dim">' + escHtml(f.protocol) + '</em></span><b class="cyan">' + nxFmtCount(f.packets) + '</b></div>').join('')
      : '';
    const pids = (data && data.byPid && data.byPid.length)
      ? data.byPid.map(p => '<div class="nx-monoRow"><span class="nx-tag ' + (p.inPool ? 'ok' : 'bad') + '">' + escHtml(p.pid ? String(p.pid) : nxT('gpn.capture.unknown')) + (p.inPool ? '' : ' ⚠') + '</span><b class="cyan">' + nxFmtCount(p.packets) + '</b></div>').join('')
      : '';
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.capture.title')) + '</div>'
      + '<span class="nx-subtle mono">' + escHtml(total) + '</span></div>'
      + '<div class="nx-chipWrap">' + (chips.length ? chips.join('') : '<p class="nx-empty center slim">' + escHtml(nxT('gpn.capture.empty')) + '</p>') + '</div>'
      + (flows ? '<p class="nx-subLabel">' + escHtml(nxT('gpn.capture.flows')) + '</p><div class="nx-monoList">' + flows + '</div>' : '')
      + (pids ? '<p class="nx-subLabel">' + escHtml(nxT('gpn.capture.perPid')) + '</p><div class="nx-monoList">' + pids + '</div>' : '')
      + '</div>';
  }

  function nxMatrixCard(S) {
    const data = S.gpnFailoverMatrix;
    if (!data || !data.rows || !data.rows.length || !data.defaultPolicy) {
      return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.matrix.title')) + '</div></div>'
        + '<p class="nx-empty center slim">' + escHtml(nxT('gpn.matrix.empty')) + '</p></div>';
    }
    const policyBox = (p, label) => {
      const badge = p.action === 'SwitchServer'
        ? '<span class="nx-tag cyan">' + escHtml(nxT('gpn.matrix.switchTo')) + ' ' + escHtml(p.targetServerId || '?') + '</span>'
        : p.action === 'FallbackToV2ray'
          ? '<span class="nx-tag bad">' + escHtml(nxT('gpn.matrix.fallback')) + '</span>'
          : '<span class="nx-tag ok">' + escHtml(nxT('gpn.matrix.keep')) + '</span>';
      return '<div class="nx-policyBox"><p class="nx-subLabel">' + escHtml(label) + '</p>' + badge
        + '<p class="nx-monoReason" title="' + escHtml(p.reason || '') + '">' + escHtml(p.reason || '') + '</p></div>';
    };
    const rows = data.rows.map(r => {
      const udp = r.udpStatus || '—';
      return '<div class="nx-monoRow"><span>' + (r.isActive ? '<b class="cyan">●</b> ' : '') + escHtml(r.name) + ' <em class="dim">' + (r.pingMs >= 0 ? r.pingMs + ' ms' : '—') + '</em></span>'
        + '<span><span class="nx-tag ' + nxUdpCls(udp) + '">' + escHtml(udp) + '</span>'
        + '<i class="' + (r.healthyDefault ? 'nx-hl ok' : 'nx-hl bad') + '" title="' + escHtml(nxT('gpn.matrix.default')) + '">' + (r.healthyDefault ? '✔' : '✘') + '</i>'
        + '<i class="' + (r.healthyStrict ? 'nx-hl ok' : 'nx-hl bad') + '" title="' + escHtml(nxT('gpn.matrix.strict')) + '">' + (r.healthyStrict ? '✔' : '✘') + '</i></span></div>';
    }).join('');
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.matrix.title')) + '</div>'
      + '<span class="nx-subtle mono">' + escHtml(new Date(data.measuredAt).toLocaleTimeString()) + '</span></div>'
      + '<div class="nx-policyWrap">' + policyBox(data.defaultPolicy, nxT('gpn.matrix.default')) + policyBox(data.strictPolicy, nxT('gpn.matrix.strict')) + '</div>'
      + '<div class="nx-monoList">' + rows + '</div>'
      + '<p class="nx-hintLine">' + escHtml(nxT('gpn.matrix.legend')) + '</p></div>';
  }

  function nxTelemetryCard(S) {
    const snap = S.gpnTelemetry || {};
    const num = (v) => String(Number.isFinite(v) ? v : 0);
    const item = (label, cls, v) => '<span class="nx-tag ' + cls + '">' + escHtml(label) + '</span><b class="nx-telNum">' + num(v) + '</b>';
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.telemetry.title')) + '</div>'
      + '<span class="nx-subtle mono">' + num(snap.totalEvents) + '</span>'
      + '<button type="button" class="nx-btn sm ml-auto" data-nx-telreset>' + escHtml(nxT('gpn.telemetry.reset')) + '</button></div>'
      + '<div class="nx-telWrap">'
      + item(nxT('gpn.telemetry.switchShort'), 'cyan', snap.serverSwitches)
      + item(nxT('gpn.telemetry.deathShort'), 'bad', snap.udpDeaths)
      + item(nxT('gpn.telemetry.fallbackShort'), 'warn', snap.modeFallbacks)
      + item(nxT('gpn.telemetry.recoverShort'), 'ok', snap.recoveries)
      + item(nxT('gpn.telemetry.selectShort'), 'violet', snap.modeDecisions)
      + '</div></div>';
  }

  function nxCaptureSetCard(S) {
    const set = S.gpnCaptureSettings || {};
    const opt = (v, label) => '<option value="' + v + '"' + (String(set.direction) === String(v) ? ' selected' : '') + '>' + escHtml(label) + '</option>';
    const layerSel = '<select class="nx-inp" data-nx-capset="layer"><option value="0">Network</option><option value="1">Forward</option></select>';
    const dirSel = '<select class="nx-inp" data-nx-capset="direction">' + opt(0, 'Inbound') + opt(1, 'Outbound') + '</select>';
    const setVal = (v, d) => { const n = parseInt(v, 10); return Number.isFinite(n) ? n : d; };
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.captureSet.title')) + '</div>'
      + '<span class="nx-subtle mono">' + escHtml((Number(set.layer) === 1 ? 'Forward' : 'Network') + ' · ' + (Number(set.direction) === 1 ? 'Outbound' : 'Inbound')) + '</span>'
      + '<button type="button" class="nx-btn sm primary ml-auto" data-nx-capset-save>' + escHtml(nxT('gpn.captureSet.save')) + '</button></div>'
      + '<div class="nx-inpGrid3">'
      + '<label class="nx-inpLbl"><span>' + escHtml(nxT('gpn.captureSet.queueLen')) + '</span><input class="nx-inp" type="number" min="1" step="64" data-nx-capset="queueLen" value="' + escHtml(String(setVal(set.queueLen, 256))) + '"></label>'
      + '<label class="nx-inpLbl"><span>' + escHtml(nxT('gpn.captureSet.queueTime')) + '</span><input class="nx-inp" type="number" min="1" step="100" data-nx-capset="queueTime" value="' + escHtml(String(setVal(set.queueTime, 2000))) + '"></label>'
      + '<label class="nx-inpLbl"><span>' + escHtml(nxT('gpn.captureSet.queueSize')) + '</span><input class="nx-inp" type="number" min="0" step="1024" data-nx-capset="queueSize" value="' + escHtml(String(setVal(set.queueSize, 0))) + '"></label>'
      + '</div>'
      + '<div class="nx-inpRow">'
      + '<label class="nx-check"><input type="checkbox" data-nx-capset="enableQueueLen"' + (set.enableQueueLen ? ' checked' : '') + '><span>' + escHtml(nxT('gpn.captureSet.enableLen')) + '</span></label>'
      + '<label class="nx-check"><input type="checkbox" data-nx-capset="enableQueueTime"' + (set.enableQueueTime ? ' checked' : '') + '><span>' + escHtml(nxT('gpn.captureSet.enableTime')) + '</span></label>'
      + '<label class="nx-check"><input type="checkbox" data-nx-capset="enableQueueSize"' + (set.enableQueueSize ? ' checked' : '') + '><span>' + escHtml(nxT('gpn.captureSet.enableSize')) + '</span></label>'
      + '<span class="ml-auto nx-inpPair"><span class="nx-subLabel">' + escHtml(nxT('gpn.captureSet.layer')) + '</span>' + layerSel + '</span>'
      + '<span class="nx-inpPair"><span class="nx-subLabel">' + escHtml(nxT('gpn.captureSet.direction')) + '</span>' + dirSel + '</span>'
      + '</div>'
      + '<p class="nx-hintLine">' + escHtml(nxT('gpn.captureSet.hint')) + '</p></div>';
  }

  function nxWintunCard(S) {
    const set = S.gpnWintunSettings || {};
    const ringVal = Number.isFinite(parseInt(set.ringCapacity, 10)) ? set.ringCapacity : 4194304;
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.wintunSet.title')) + '</div>'
      + '<span class="nx-subtle mono">' + escHtml(set.adapterName || 'AoGPN') + '</span>'
      + '<button type="button" class="nx-btn sm primary ml-auto" data-nx-wintun-save>' + escHtml(nxT('gpn.wintunSet.save')) + '</button></div>'
      + '<div class="nx-inpGrid2">'
      + '<label class="nx-inpLbl"><span>' + escHtml(nxT('gpn.wintunSet.adapter')) + '</span><input class="nx-inp" type="text" maxlength="32" spellcheck="false" data-nx-wintun="adapterName" value="' + escHtml(set.adapterName || 'AoGPN') + '"></label>'
      + '<label class="nx-inpLbl"><span>' + escHtml(nxT('gpn.wintunSet.ring')) + '</span><input class="nx-inp" type="number" min="131072" step="65536" data-nx-wintun="ringCapacity" value="' + escHtml(String(ringVal)) + '"></label>'
      + '</div>'
      + '<p class="nx-hintLine">' + escHtml(nxT('gpn.wintunSet.hint')) + '</p></div>';
  }

  function nxDiagCard(S) {
    const lines = (S.gpnDiagLines || []).filter(l => !(l && l.timestampMs && l.timestampMs <= _nxDiagClearedAt));
    const body = lines.length
      ? lines.map(l => '<p class="nx-diagLine" title="' + escHtml(String(l.message)) + '">' + escHtml((l.timestampMs ? '[' + new Date(l.timestampMs).toLocaleTimeString() + '] ' : '') + (l.kind ? '[' + l.kind + '] ' : '') + l.message) + '</p>').join('')
      : '<p class="nx-empty center slim">' + escHtml(nxT('gpn.diag.empty')) + '</p>';
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.diag.title')) + '</div>'
      + '<span class="nx-subtle mono">' + lines.length + '</span>'
      + '<button type="button" class="nx-btn sm ml-auto" data-nx-diagclear>' + escHtml(nxT('gpn.diag.clear')) + '</button></div>'
      + '<div class="nx-diagFeed">' + body + '</div></div>';
  }

  function nxResLogCard(S) {
    const data = S.gpnResilienceLog || {};
    const entries = (data && Array.isArray(data.entries)) ? data.entries : [];
    const meta = (action) => {
      switch (action) {
        case 'ServerSwitch': return ['nx-ev-switch', 'gpn.reslog.action.switch'];
        case 'UdpDeath': return ['nx-ev-death', 'gpn.reslog.action.udpDeath'];
        case 'ModeFallback': return ['nx-ev-fallback', 'gpn.reslog.action.modeFallback'];
        case 'Recover': return ['nx-ev-recover', 'gpn.reslog.action.recover'];
        case 'ModeDecision': return ['nx-ev-select', 'gpn.reslog.action.select'];
        default: return ['nx-ev-other', null];
      }
    };
    const body = entries.length
      ? entries.map(e => {
          const [cls, key] = meta(e && e.action);
          const time = (e && Number.isFinite(e.timestampMs)) ? new Date(e.timestampMs).toLocaleTimeString() : '';
          const label = key ? nxT(key) : (e && e.action) || '';
          const mode = e && e.toMode === 'V2rayTCP' ? 'V2rayTCP' : 'WireGuard';
          const target = e && (e.targetServerName || e.targetServerId || '');
          const parts = [e && (e.serverName || e.serverId || ''), (target ? '→ ' + target : ''), e && e.reason ? e.reason : ''].filter(Boolean).join('  ·  ');
          return '<div class="nx-event ' + cls + '"><i class="nx-evDot"></i><div class="min-w-0"><div class="nx-evLine"><span class="nx-evBadge">' + escHtml(label) + '</span><em class="dim">' + escHtml(mode) + '</em><span class="nx-evTime">' + escHtml(time) + '</span></div>'
            + (parts ? '<p class="nx-evDetail">' + escHtml(parts) + '</p>' : '') + '</div></div>';
        }).join('')
      : '<p class="nx-empty center slim">' + escHtml(nxT('gpn.reslog.empty')) + '</p>';
    return '<div class="nx-advCard"><div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('gpn.reslog.title')) + '</div>'
      + '<span class="nx-subtle mono">' + entries.length + '</span>'
      + '<div class="ml-auto nx-gpnActions"><button type="button" class="nx-btn sm" data-nx-reslog="refresh">' + escHtml(nxT('gpn.reslog.refresh')) + '</button>'
      + '<button type="button" class="nx-btn sm" data-nx-reslog="clear">' + escHtml(nxT('gpn.reslog.clear')) + '</button></div></div>'
      + '<div class="nx-resFeed">' + body + '</div>'
      + (data && data.path ? '<p class="nx-hintLine"><span>' + escHtml(nxT('gpn.reslog.path')) + '</span> <span class="mono">' + escHtml(data.path) + '</span></p>' : '') + '</div>';
  }

  function nxAdvancedBody(S) {
    const toggle = '<button type="button" class="nx-advToggle" data-nx-adv-toggle aria-expanded="' + (_nxAdvOpen ? 'true' : 'false') + '">'
      + '<i class="nx-advChev' + (_nxAdvOpen ? ' open' : '') + '">▾</i>'
      + '<b>' + escHtml(nxT('gpn.advanced')) + '</b><span class="nx-subtle">' + escHtml(nxT('gpn.advancedSub')) + '</span></button>';
    const body = _nxAdvOpen
      ? '<div class="nx-advBody">'
        + nxCandidateCard(S) + nxPidPoolCard(S) + nxCaptureCard(S) + nxMatrixCard(S)
        + nxFailoverToggleCard(S) + nxRecoveryToggleCard(S) + nxTelemetryCard(S)
        + nxCaptureSetCard(S) + nxWintunCard(S) + nxDiagCard(S) + nxResLogCard(S)
        + '</div>'
      : '';
    return toggle + body;
  }

  function nxFailoverToggleCard(S) {
    const on = S.gpnFailover === true;
    return '<div class="nx-advCard row"><div><b>' + escHtml(nxT('gpn.failover.title')) + '</b><p class="nx-hintLine">' + escHtml(nxT('gpn.failover.sub')) + '</p></div>'
      + '<div class="nx-switch' + (on ? ' on' : '') + '" data-nx-advfailover role="switch" aria-checked="' + on + '"></div></div>';
  }
  function nxRecoveryToggleCard(S) {
    const on = S.gpnRecoveryWatch === true;
    return '<div class="nx-advCard row"><div><b>' + escHtml(nxT('gpn.recovery.title')) + '</b><p class="nx-hintLine">' + escHtml(nxT('gpn.recovery.sub')) + '</p></div>'
      + '<div class="nx-switch' + (on ? ' on' : '') + '" data-nx-advrecovery role="switch" aria-checked="' + on + '"></div></div>';
  }

  // ---- GPN panel (mode=gpn) ----
  function nxRenderGpnPanel() {
    const root = nxRef('nxGpnPanel');
    if (!root) return;
    const S = nxState();
    // Don't clobber open controls (WinDivert/Wintun inputs or toggles).
    if (root.contains(document.activeElement)) return;

    const gcs = S.gpnConnectState || {};
    const boostApps = (S.monitorSnapshot.apps || []).filter(a => a.action === 'vpn' || a.action === 'vpn+proxy').slice(0, 4);
    const cluster = nxClusterRows(S);
    const adv = nxAdvancedBody(S);

    // Live tunnel info (Your IP / Session / Verification) — mirrors applyRealIpState.
    const ip = S.realIpState || {};
    const isTunneled = S.connected && ip.tunnelVerified === true;
    const hasTunnelIp = typeof ip.tunnelIp === 'string' && ip.tunnelIp.length > 0;
    let ipVal = ip.ispIp || ip.directIp || '--';
    let ipSub = (ip.ispCountry || ip.directCountry || '') + ' · ' + nxT('ip.isp');
    let vTxt = nxT('status.idle'), vCls = 'muted', vDetail = nxT('status.notConnected');
    if (S.connected && isTunneled) {
      ipVal = ip.tunnelIp; ipSub = (ip.tunnelCountry || '') + ' · ' + nxT('ip.tunnel');
      vTxt = nxT('status.tunneled'); vCls = 'ok';
      vDetail = ip.transport === 'tun' ? nxT('ip.detail.tunActive') : nxT('ip.detail.socksVerified');
    } else if (S.connected && hasTunnelIp) {
      ipVal = ip.tunnelIp;
      ipSub = (ip.tunnelCountry || '') + (ip.ispCached === true ? ' (' + nxT('ip.leaking') + ')' : ' · ' + nxT('ip.tunnel'));
      vTxt = ip.ispCached === true ? nxT('status.leaking') : nxT('status.probing');
      vCls = ip.ispCached === true ? 'bad' : 'warn';
      vDetail = ip.ispCached === true ? nxT('ip.detail.possibleLeak') : nxT('ip.detail.fetchingBaseline');
    } else if (S.connected) {
      ipVal = ip.directIp || ip.ispIp || '--';
      ipSub = nxT('ip.probing');
      vTxt = nxT('status.probing'); vCls = 'warn'; vDetail = nxT('ip.detail.fetchingBaseline');
    }

    root.innerHTML =
      '<div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('boost.title')) + '</div>'
      + '<a class="nx-link" data-nx-view-go="games">' + escHtml(nxT('boost.full')) + ' →</a></div>'
      + '<button type="button" class="nx-gpnConnect" data-nx-gpnconnect>' + escHtml(nxT('gpnConnect'))
      + '<span class="nx-gpnCSub">' + escHtml(nxT('gpnConnectSub')) + '</span>'
      + '<span class="nx-gpnCState' + (gcs.pending ? ' pending' : '') + '">' + escHtml(gcs.label || '—') + '</span></button>'
      + '<div class="nx-infoGrid">'
      + '<div class="nx-infoCard"><span class="nx-subLabel">' + escHtml(nxT('stat.yourIp')) + '</span><b>' + escHtml(ipVal) + '</b><em>' + escHtml(ipSub) + '</em></div>'
      + '<div class="nx-infoCard"><span class="nx-subLabel">' + escHtml(nxT('stat.session')) + '</span><b>' + escHtml(S.sessionTime) + '</b><em>' + escHtml(S.activeGpnServer || '—') + '</em></div>'
      + '<div class="nx-infoCard"><span class="nx-subLabel">' + escHtml(nxT('stat.verification')) + '</span><b class="' + vCls + '">' + escHtml(vTxt) + '</b><em>' + escHtml(vDetail) + '</em></div>'
      + '</div>'
      + '<div class="nx-cardHead nx-clusterHead"><div class="nx-label">' + escHtml(nxT('boost.assigned')) + '</div></div>'
      + '<div class="nx-boostGrid">' + (boostApps.length ? boostApps.map(nxBoostCard).join('') : '<p class="nx-empty center slim">' + escHtml(nxT('boost.noGames')) + '</p>') + '</div>'
      + '<div class="nx-clusterWrap">' + cluster + '</div>'
      + '<div class="nx-advWrap">' + adv + '</div>';

    // ---- wire ----
    const goBtn = root.querySelector('[data-nx-view-go]');
    if (goBtn) goBtn.addEventListener('click', () => { nxGo('games'); });
    const gpnBtn = root.querySelector('[data-nx-gpnconnect]');
    if (gpnBtn) gpnBtn.addEventListener('click', () => { if (B) B.postToHost({ action: 'gpn_connect' }); });
    const probe = root.querySelector('[data-nx-clusterprobe]');
    if (probe) probe.addEventListener('click', () => { if (B) B.postToHost({ action: 'gpn_cluster_probe' }); });
    const advT = root.querySelector('[data-nx-adv-toggle]');
    if (advT) advT.addEventListener('click', () => {
      _nxAdvOpen = !_nxAdvOpen;
      nxRenderGpnPanel();
    });
    const advFail = root.querySelector('[data-nx-advfailover]');
    if (advFail) advFail.addEventListener('click', () => {
      const next = !advFail.classList.contains('on');
      advFail.classList.toggle('on', next);
      advFail.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_gpn_failover', enabled: next });
    });
    const advRec = root.querySelector('[data-nx-advrecovery]');
    if (advRec) advRec.addEventListener('click', () => {
      const next = !advRec.classList.contains('on');
      advRec.classList.toggle('on', next);
      advRec.setAttribute('aria-checked', String(next));
      if (B) B.postToHost({ action: 'set_gpn_recovery_watch', enabled: next });
    });
    root.querySelectorAll('[data-nx-pidpool]').forEach(btn => {
      btn.addEventListener('click', () => {
        const a = btn.dataset.nxPidpool;
        if (B) B.postToHost({ action: 'gpn_pid_pool_' + a });
      });
    });
    const telReset = root.querySelector('[data-nx-telreset]');
    if (telReset) telReset.addEventListener('click', () => { if (B) B.postToHost({ action: 'reset_gpn_telemetry' }); });
    const capSave = root.querySelector('[data-nx-capset-save]');
    if (capSave) capSave.addEventListener('click', () => {
      if (!B) return;
      const q = (k, d) => { const el = root.querySelector('[data-nx-capset="' + k + '"]'); const v = el ? parseInt(el.value, 10) : NaN; return Number.isFinite(v) ? Math.max(d, v) : null; };
      B.postToHost({
        action: 'set_gpn_capture_settings',
        queueLen: q('queueLen', 1), queueTime: q('queueTime', 1), queueSize: q('queueSize', 0),
        enableQueueLen: !!(root.querySelector('[data-nx-capset="enableQueueLen"]') || {}).checked,
        enableQueueTime: !!(root.querySelector('[data-nx-capset="enableQueueTime"]') || {}).checked,
        enableQueueSize: !!(root.querySelector('[data-nx-capset="enableQueueSize"]') || {}).checked,
        layer: parseInt((root.querySelector('[data-nx-capset="layer"]') || {}).value || '0', 10),
        direction: parseInt((root.querySelector('[data-nx-capset="direction"]') || {}).value || '1', 10)
      });
    });
    const winSave = root.querySelector('[data-nx-wintun-save]');
    if (winSave) winSave.addEventListener('click', () => {
      if (!B) return;
      const nameEl = root.querySelector('[data-nx-wintun="adapterName"]');
      const ringEl = root.querySelector('[data-nx-wintun="ringCapacity"]');
      const ringValue = ringEl ? parseInt(ringEl.value, 10) : NaN;
      B.postToHost({
        action: 'set_gpn_wintun_settings',
        adapterName: nameEl ? (nameEl.value.trim() || null) : null,
        ringCapacity: Number.isFinite(ringValue) ? Math.max(131072, ringValue) : null
      });
    });
    const diagClear = root.querySelector('[data-nx-diagclear]');
    if (diagClear) diagClear.addEventListener('click', () => { _nxDiagClearedAt = Date.now(); nxRenderGpnPanel(); });
    root.querySelectorAll('[data-nx-reslog]').forEach(btn => {
      btn.addEventListener('click', () => {
        const a = btn.dataset.nxReslog;
        if (B) B.postToHost({ action: (a === 'refresh' ? 'get_gpn_resilience_log' : 'clear_gpn_resilience_log') });
      });
    });
  }

  // ---- Global VPN panel (mode=vpn) ----
  function nxRenderGlobalPanel() {
    const root = nxRef('nxGlobalPanel');
    if (!root) return;
    const S = nxState();
    const vpnActive = S.mode === 'vpn' && S.connected;
    root.classList.toggle('nx-hidden', S.mode !== 'vpn');
    const ip = S.realIpState || {};
    const isTunneled = vpnActive && ip.tunnelVerified === true;
    const hasTunnelIp = typeof ip.tunnelIp === 'string' && ip.tunnelIp.length > 0;
    let vStatus = '<b class="warn">' + escHtml(nxT('status.probing')) + '</b><em>' + escHtml(nxT('ip.detail.fetchingBaseline')) + '</em>';
    if (!vpnActive) vStatus = '<b class="muted">' + escHtml(nxT('global.notConnected')) + '</b><em>' + escHtml(nxT('status.notConnected')) + '</em>';
    else if (isTunneled) vStatus = '<b class="ok">' + escHtml(nxT('global.tunnelVerified')) + '</b><em>' + escHtml(nxT('global.tunnelVerifiedDesc', { ip: ip.tunnelIp || '—' })) + '</em>';
    else if (hasTunnelIp && ip.ispCached === true) vStatus = '<b class="bad">' + escHtml(nxT('global.leaking')) + '</b><em>' + escHtml(nxT('global.leakingDesc', { ip: ip.tunnelIp || '—' })) + '</em>';
    root.innerHTML =
      '<div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('global.title')) + '</div>'
      + '<span class="nx-tag ' + (vpnActive ? 'ok' : 'muted') + '">' + escHtml(vpnActive ? nxT('global.allTraffic') : nxT('global.disconnected')) + '</span></div>'
      + '<div class="nx-globalGrid">'
      + '<div class="nx-infoCard"><span class="nx-subLabel">' + escHtml(nxT('global.transport')) + '</span><b>' + escHtml(vpnActive ? 'TUN' : '—') + '</b><em>' + escHtml(nxT('global.modeActive')) + '</em></div>'
      + '<div class="nx-infoCard"><span class="nx-subLabel">' + escHtml(nxT('global.ipVerification')) + '</span>' + vStatus + '</div>'
      + '</div>'
      + '<div class="nx-infoCard"><span class="nx-subLabel">' + escHtml(nxT('global.coverage')) + '</span>'
      + '<div class="nx-coverage"><i style="width:' + (vpnActive ? '100%' : '0%') + '"></i></div>'
      + '<em>' + escHtml(nxT('global.coverage100')) + '</em></div>';
  }

  function nxRenderDashboardPanels() {
    nxRenderBanners();
    nxRenderGpnPanel();
    nxRenderGlobalPanel();
  }

  // ---------- analytics sub-view ----------
  function nxRenderAnalytics() {
    const root = nxRef('nxAnalytics');
    if (!root) return;
    const S = nxState();
    const tele = S.telemetry || [];
    const has = tele.some(v => v !== undefined && v !== null);
    const ping = (S.connected && has && Number.isFinite(tele[0]) && tele[0] > 0) ? Math.round(tele[0]) : null;
    const loss = (S.connected && has && Number.isFinite(tele[1]) && tele[1] >= 0) ? Number(tele[1]) : null;
    const down = (has && Number.isFinite(tele[2]) && tele[2] > 0) ? tele[2] : null;
    const up = (has && Number.isFinite(tele[3]) && tele[3] > 0) ? tele[3] : null;
    const pingPct = ping != null ? Math.min(100, Math.round((ping / 120) * 100)) : 0;
    const lossPct = loss != null ? Math.min(100, Math.round(loss * 2)) : 0;
    const health = ping == null ? 0 : ping < 40 ? 92 : ping < 90 ? 70 : ping < 150 ? 45 : 20;
    root.innerHTML =
      '<div class="nx-anaCard">'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.signalHealth')) + '</span><span class="nx-anaPill">' + escHtml(nxT(S.connected ? 'ana.live' : 'ana.idle')) + '</span></div><div class="nx-meter"><i style="width:' + health + '%"></i></div></div>'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.session')) + '</span><span class="nx-anaVal">' + escHtml(S.sessionTime) + '</span></div></div>'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.exitIp')) + '</span><span class="nx-anaVal sm">' + escHtml(S.exitIp) + '</span></div></div>'
      + '</div>'
      + '<div class="nx-anaCard">'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.ping')) + '</span><span class="nx-anaVal">' + (ping != null ? ping : '--') + '<small class="nx-unit"> ms</small></span></div><div class="nx-meter"><i style="width:' + pingPct + '%"></i></div></div>'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.jitter')) + '</span><span class="nx-anaVal">' + (ping != null ? Math.max(1, 2 + (ping % 4)) : '--') + '<small class="nx-unit"> ms</small></span></div></div>'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.loss')) + '</span><span class="nx-anaVal">' + (loss != null ? loss.toFixed(1) : '--') + '<small class="nx-unit">%</small></span></div><div class="nx-meter"><i style="width:' + lossPct + '%"></i></div></div>'
      + '</div>'
      + '<div class="nx-anaCard">'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.download')) + '</span><span class="nx-anaVal lg">' + (down != null ? down.toFixed(1) : '--') + '<small class="nx-unit xs"> Mbps</small></span></div></div>'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.upload')) + '</span><span class="nx-anaVal lg">' + (up != null ? up.toFixed(1) : '--') + '<small class="nx-unit xs"> Mbps</small></span></div></div>'
        + '<div class="nx-anaRow"><div class="nx-anaTop"><span class="nx-anaLabel">' + escHtml(nxT('ana.route')) + '</span><span class="nx-anaPill">' + escHtml(nxT(S.mode === 'gpn' ? 'ana.gpn' : 'ana.global')) + (S.connected ? ' · ' + escHtml(nxT('ana.live')) : '') + '</span></div></div>'
      + '</div>';
  }

  // ---------- Connection Monitor (Perf parity) ----------
  // Live per-connection table fed by the same monitorSnapshot the dashboard's
  // Performance → Connection Monitor renders: filter, "hide listeners" and
  // group-by (route/protocol/state/country/application) with collapsible
  // headers, plus connection/application counters. Preferences persist locally.
  let _nxMonFilter = '';
  let _nxMonGroup = 'none';
  let _nxMonHideListeners = true;
  const _nxMonCollapsed = new Set();
  try {
    const saved = JSON.parse(localStorage.getItem('aogpn.nexusMonitor.v1') || '{}');
    if (['none', 'route', 'protocol', 'state', 'country', 'app'].includes(saved.group)) _nxMonGroup = saved.group;
    if (typeof saved.hideListeners === 'boolean') _nxMonHideListeners = saved.hideListeners;
  } catch (e) { /* no localStorage */ }
  function _nxMonPersist() {
    try { localStorage.setItem('aogpn.nexusMonitor.v1', JSON.stringify({ group: _nxMonGroup, hideListeners: _nxMonHideListeners })); } catch (e) { /* ignore */ }
  }

  const NX_MON_GROUP_KEYS = [
    ['none', 'monitor.view.groupNone'], ['route', 'monitor.view.groupRoute'], ['protocol', 'monitor.view.groupProtocol'],
    ['state', 'monitor.view.groupState'], ['country', 'monitor.view.groupCountry'], ['app', 'monitor.view.groupApp']
  ];

  function _nxMonRow(item, hidden) {
    const S = nxState();
    const country = [item.countryText, item.asnText].filter(Boolean).join(' · ') || '—';
    const pname = item.processName || '';
    const display = item.displayName || pname || 'Unknown';
    const route = item.routeTag || '';
    const label = item.routeText || (S.routeLabels && S.routeLabels[route]) || route || '—';
    const cls = ['proxy', 'direct', 'block', 'warp'].includes(route) ? route : 'unknown';
    const opts = [['', nxT('route.assign')], ['vpn', nxT('route.vpn')], ['direct', nxT('route.direct')], ['block', nxT('route.block')], ['warp', nxT('route.warp')]]
      .map(o => '<option value="' + o[0] + '"' + (o[0] === (item.action || '') ? ' selected' : '') + '>' + escHtml(o[1]) + '</option>').join('');
    return '<div class="nx-mrow' + (hidden ? ' hidden' : '') + '" data-app-group="' + escHtml(display) + '">'
      + '<div class="nx-mcol prog"><div class="nx-progCell">' + nxAppIcon(item.exePath, display) + '<div class="nx-progTxt"><b>' + escHtml(display) + '</b><span>' + escHtml(pname) + (item.pid ? ' · PID ' + escHtml(String(item.pid)) : '') + '</span></div></div></div>'
      + '<div class="nx-mcol"><span class="nx-routeBadge ' + cls + '">' + escHtml(label) + '</span></div>'
      + '<div class="nx-mcol">' + escHtml(item.protocol || '—') + '</div>'
      + '<div class="nx-mcol addr" title="' + escHtml(item.remoteAddress || '') + '">' + escHtml(item.remoteAddress || '—') + '</div>'
      + '<div class="nx-mcol addr" title="' + escHtml(country) + '">' + escHtml(country) + '</div>'
      + '<div class="nx-mcol">' + escHtml(item.state || '—') + '</div>'
      + '<div class="nx-mcol"><select class="nx-routeSel" data-nx-monroute="' + escHtml(pname) + '" data-nx-mondisplay="' + escHtml(display) + '">' + opts + '</select></div>'
      + '</div>';
  }

  function _nxMonGroupHead(name, count) {
    const collapsed = _nxMonCollapsed.has(name);
    return '<div class="nx-mgroup" data-nx-mongroup="' + escHtml(name) + '" role="button" tabindex="0" aria-expanded="' + !collapsed + '"><span class="nx-mchev">' + (collapsed ? '▸' : '▾') + '</span><b>' + escHtml(name) + '</b><em>' + count + '</em></div>';
  }

  function nxRenderMonitor() {
    const root = nxRef('nxMonitor');
    if (!root) return;
    // Don't clobber the filter while the user is typing.
    const filterEl = root.querySelector('[data-nx-monfilter]');
    if (filterEl && root.contains(document.activeElement)) return;
    const S = nxState();
    const snap = S.monitorSnapshot || {};
    const conns = snap.connections || [];
    const filter = _nxMonFilter.trim().toLowerCase();
    const rows = conns.filter(item => {
      if (_nxMonHideListeners && item.protocol === 'TCP' && item.state === 'Listen') return false;
      if (!filter) return true;
      const hay = [item.displayName, item.processName, item.remoteAddress, item.countryText, item.asnText, item.protocol, item.state]
        .filter(Boolean).join(' ').toLowerCase();
      return hay.includes(filter);
    });
    const appsCount = (snap.apps || []).length;
    const liveBadge = S.connected
      ? '<span class="nx-liveBadge">● ' + escHtml(nxT('hint.live')) + '</span>'
      : '<span class="nx-liveBadge off">○ ' + escHtml(nxT('hint.idle')) + '</span>';
    const groupOpts = NX_MON_GROUP_KEYS.map(o => '<option value="' + o[0] + '"' + (o[0] === _nxMonGroup ? ' selected' : '') + '>' + escHtml(nxT(o[1])) + '</option>').join('');
    let body;
    if (rows.length === 0) {
      body = '<p class="nx-empty center">' + escHtml(nxT('monitor.view.empty')) + '</p>';
    } else if (_nxMonGroup === 'none') {
      body = rows.map(it => _nxMonRow(it, false)).join('');
    } else {
      const keyOf = (it) => {
        if (_nxMonGroup === 'route') return it.routeText || it.routeTag || '—';
        if (_nxMonGroup === 'protocol') return it.protocol || '—';
        if (_nxMonGroup === 'state') return it.state || '—';
        if (_nxMonGroup === 'country') return [it.countryText, it.asnText].filter(Boolean).join(' · ') || '—';
        return it.displayName || it.processName || 'Unknown';
      };
      const order = [];
      const grouped = new Map();
      for (const it of rows) {
        const k = keyOf(it);
        if (!grouped.has(k)) { order.push(k); grouped.set(k, []); }
        grouped.get(k).push(it);
      }
      body = order.map(k => {
        const list = grouped.get(k);
        const collapsed = _nxMonCollapsed.has(k);
        return _nxMonGroupHead(k, list.length) + list.map(it => _nxMonRow(it, collapsed)).join('');
      }).join('');
    }
    root.innerHTML =
      '<div class="nx-cardHead"><div class="nx-label">' + escHtml(nxT('monitor.title')) + '</div>' + liveBadge + '</div>'
      + '<div class="nx-monStats">'
      + '<div class="nx-monStat"><b>' + rows.length + '</b><span>' + escHtml(nxT('monitor.connections')) + '</span></div>'
      + '<div class="nx-monStat"><b>' + appsCount + '</b><span>' + escHtml(nxT('monitor.apps')) + '</span></div>'
      + '</div>'
      + '<div class="nx-monTools">'
      + '<input type="search" class="nx-inp" data-nx-monfilter placeholder="' + escHtml(nxT('monitor.filter')) + '" value="' + escHtml(_nxMonFilter) + '">'
      + '<label class="nx-monHide"><input type="checkbox" data-nx-monhide' + (_nxMonHideListeners ? ' checked' : '') + '> <span>' + escHtml(nxT('monitor.hideListeners')) + '</span></label>'
      + '<select class="nx-settingSel nx-monGroup" data-nx-mongroup-sel aria-label="Group by">' + groupOpts + '</select>'
      + '</div>'
      + (rows.length ? '<div class="nx-mhead"><span>' + escHtml(nxT('boost.colProgram')) + '</span><span>' + escHtml(nxT('boost.colRoute')) + '</span><span>PROTOCOL</span><span>ADDRESS</span><span>COUNTRY</span><span>STATE</span><span>' + escHtml(nxT('route.assign')) + '</span></div>' + body : body);
    const f = root.querySelector('[data-nx-monfilter]');
    if (f) f.addEventListener('input', () => { _nxMonFilter = f.value; nxRenderMonitor(); });
    const hide = root.querySelector('[data-nx-monhide]');
    if (hide) hide.addEventListener('change', () => { _nxMonHideListeners = hide.checked; _nxMonPersist(); nxRenderMonitor(); });
    const gsel = root.querySelector('[data-nx-mongroup-sel]');
    if (gsel) gsel.addEventListener('change', () => { _nxMonGroup = gsel.value; _nxMonPersist(); nxRenderMonitor(); });
    root.querySelectorAll('[data-nx-mongroup]').forEach(h => {
      const toggle = () => {
        const name = h.dataset.nxMongroup;
        if (_nxMonCollapsed.has(name)) _nxMonCollapsed.delete(name); else _nxMonCollapsed.add(name);
        nxRenderMonitor();
      };
      h.addEventListener('click', toggle);
      h.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
    });
    root.querySelectorAll('[data-nx-monroute]').forEach(sel => {
      sel.addEventListener('change', () => {
        if (!sel.value) return;
        if (B) B.postToHost({ action: 'set_app_route', processName: sel.dataset.nxMonroute, displayName: sel.dataset.nxMondisplay, route: sel.value });
      });
    });
  }

  // ---------- settings sub-view ----------
  // Option labels are translated through nxT() so the settings panel follows
  // the app language; the language list itself comes from the bridge (the same
  // LANGUAGES the top-bar picker uses), with a small standalone fallback.
  function __nxLangs() {
    const langs = (B && B.LANGUAGES) ? B.LANGUAGES : [{ code: 'en', name: 'English' }, { code: 'tr', name: 'Türkçe' }, { code: 'fa', name: 'فارسی' }, { code: 'fr', name: 'Français' }, { code: 'ru', name: 'Русский' }];
    return langs.map(l => [l.code, l.name]);
  }

  function __nxOpts(list, current) {
    return list.map(o => { const eq = String(current) === String(o[0]); return '<option value="' + escHtml(o[0]) + '"' + (eq ? ' selected' : '') + '>' + escHtml(o[1]) + '</option>'; }).join('');
  }

  // System-proxy test state is kept locally so the button survives the 2 s
  // settings re-render; the host answer arrives through skinBridge.proxyTestResult
  // on the next push and is adopted only while the button is in 'testing'.
  const _nxProxyTest = { mode: 'idle', label: '', timer: null };

  function _nxProxyTestClick() {
    if (_nxProxyTest.mode === 'testing') return;
    _nxProxyTest.mode = 'testing';
    _nxProxyTest.label = '';
    if (B) B.postToHost({ action: 'test_proxy' });
    if (_nxProxyTest.timer) clearTimeout(_nxProxyTest.timer);
    _nxProxyTest.timer = setTimeout(() => {
      if (_nxProxyTest.mode === 'testing') {
        _nxProxyTest.mode = 'timeout';
        nxSyncAll();
      }
    }, 6000);
    nxSyncAll();
  }

  function _nxProxyTestAdopt(S) {
    if (_nxProxyTest.mode !== 'testing') return;
    const r = S.proxyTestResult;
    if (!r || typeof r !== 'object') return;
    const ok = r.ok === true;
    _nxProxyTest.mode = ok ? 'ok' : 'fail';
    _nxProxyTest.label = ok
      ? (Number.isFinite(r.ms) ? r.ms + ' ms' : nxT('proxy.ok'))
      : (r.message ? String(r.message) : nxT('proxy.unreachable'));
    if (_nxProxyTest.timer) clearTimeout(_nxProxyTest.timer);
    _nxProxyTest.timer = setTimeout(() => { _nxProxyTest.mode = 'idle'; nxSyncAll(); }, 5000);
  }

  function nxRenderSettings() {
    const root = nxRef('nxSettings');
    if (!root) return;
    if (root.contains(document.activeElement)) return; // don't clobber an open control
    const S = nxState();
    _nxProxyTestAdopt(S);
    const modes = [['gpn', nxT('modeGpn')], ['vpn', nxT('modeGlobal')]];
    const transports = [['proxy', nxT('transportProxy')], ['tun', nxT('transportTun')]];
    const splits = [['off', nxT('opt.off')], ['vpn', nxT('opt.globalSplit')], ['manual', nxT('opt.gpnSplit')]];
    const dirs = [['whitelist', nxT('dir.whitelist')], ['blacklist', nxT('dir.blacklist')]];
    const invert = !!(S.monitorSnapshot && S.monitorSnapshot.invertManualRouting === true);
    const isProxyOn = (v) => Number(v) === 1 || Number(v) === 3;
    const proxies = [['0', nxT('opt.clear')], ['1', nxT('opt.set')], ['2', nxT('opt.unchanged')], ['3', nxT('opt.pac')]];
    const protos = [['auto', nxT('opt.automatic')], ['wireguard', nxT('opt.wireguard')], ['mimic', nxT('opt.mimic')], ['hysteria2', nxT('opt.hysteria2')], ['openvpn', nxT('opt.openvpn')]];
    const stacks = [['gvisor', nxT('opt.gvisor')], ['system', nxT('opt.system')], ['mixed', nxT('opt.mixed')]];
    const effects = [['full', nxT('opt.full')], ['balanced', nxT('opt.balanced')], ['reduced', nxT('opt.reduced')]];
    const autoGame = !!(S.monitorSnapshot && S.monitorSnapshot.autoConnectOnGameStart === true);
    const pt = _nxProxyTest;
    const ptLabel = pt.mode === 'testing' ? nxT('proxy.testing')
      : pt.mode === 'ok' ? (pt.label || nxT('proxy.ok'))
      : pt.mode === 'fail' ? (pt.label || nxT('proxy.unreachable'))
      : pt.mode === 'timeout' ? nxT('proxy.timeout')
      : nxT('proxy.test');
    const ptClass = pt.mode === 'testing' ? ' testing' : (pt.mode === 'ok' ? ' ok' : (pt.mode === 'fail' || pt.mode === 'timeout' ? ' fail' : ''));
    root.innerHTML =
        '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.routeMode')) + '</b><span>' + escHtml(nxT('setting.routeModeDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="mode">' + __nxOpts(modes, S.mode) + '</select></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.capture')) + '</b><span>' + escHtml(nxT('setting.captureDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="transport">' + __nxOpts(transports, S.transport) + '</select></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.split')) + '</b><span>' + escHtml(nxT('setting.splitDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="split">' + __nxOpts(splits, S.splitMode) + '</select></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.splitDir')) + '</b><span>' + escHtml(nxT('setting.splitDirDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="splitdir">' + __nxOpts(dirs, invert ? 'blacklist' : 'whitelist') + '</select></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.sysproxy')) + '</b><span>' + escHtml(nxT('setting.sysproxyDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="sysproxy">' + __nxOpts(proxies, S.systemProxyMode) + '</select></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.sysproxyToggle')) + '</b><span>' + escHtml(nxT('setting.sysproxyToggleDesc')) + '</span></div><button type="button" class="nx-sysproxyToggle' + (isProxyOn(S.systemProxyMode) ? ' on' : '') + '" data-nx-set="sysproxy-toggle">' + escHtml(isProxyOn(S.systemProxyMode) ? nxT('proxy.on') : nxT('proxy.off')) + '</button></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.proxyTest')) + '</b><span>' + escHtml(nxT('setting.proxyTestDesc')) + '</span></div><button type="button" class="nx-proxyTest' + ptClass + '" data-nx-set="proxytest">' + escHtml(ptLabel) + '</button></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.protocol')) + '</b><span>' + escHtml(nxT('setting.protocolDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="protocol">' + __nxOpts(protos, S.protocolPreference) + '</select></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.tunStack')) + '</b><span>' + escHtml(nxT('setting.tunStackDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="tunstack">' + __nxOpts(stacks, S.tunStack) + '</select></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.autoReconnect')) + '</b><span>' + escHtml(nxT('setting.autoReconnectDesc')) + '</span></div><div class="nx-switch ' + (S.autoReconnect ? 'on' : '') + '" data-nx-set="autoreconnect" role="switch" aria-checked="' + S.autoReconnect + '"></div></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.autoGameConnect')) + '</b><span>' + escHtml(nxT('setting.autoGameConnectDesc')) + '</span></div><div class="nx-switch ' + (autoGame ? 'on' : '') + '" data-nx-set="autogame" role="switch" aria-checked="' + autoGame + '"></div></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.recoveryWatch')) + '</b><span>' + escHtml(nxT('setting.recoveryWatchDesc')) + '</span></div><div class="nx-switch ' + (S.gpnRecoveryWatch ? 'on' : '') + '" data-nx-set="recovery" role="switch" aria-checked="' + S.gpnRecoveryWatch + '"></div></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.failover')) + '</b><span>' + escHtml(nxT('setting.failoverDesc')) + '</span></div><div class="nx-switch ' + (S.gpnFailover ? 'on' : '') + '" data-nx-set="failover" role="switch" aria-checked="' + S.gpnFailover + '"></div></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.effects')) + '</b><span>' + escHtml(nxT('setting.effectsDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="effects">' + __nxOpts(effects, S.effectsTier) + '</select></div>'
      + '<div class="nx-setting"><div><b>' + escHtml(nxT('setting.language')) + '</b><span>' + escHtml(nxT('setting.languageDesc')) + '</span></div><select class="nx-settingSel" data-nx-set="language">' + __nxOpts(__nxLangs(), S.language) + '</select></div>';
    root.querySelectorAll('[data-nx-set]').forEach(ctl => {
      if (ctl.classList.contains('nx-switch')) {
        ctl.addEventListener('click', () => {
          const set = ctl.dataset.nxSet;
          const next = !ctl.classList.contains('on');
          ctl.classList.toggle('on', next);
          ctl.setAttribute('aria-checked', String(next));
          if (B) {
            if (set === 'autoreconnect') { B.postToHost({ action: 'set_auto_reconnect', enabled: next }); SET('autoReconnect', next); }
            else if (set === 'autogame') B.postToHost({ action: 'set_auto_game_connect', enabled: next });
            else if (set === 'recovery') B.postToHost({ action: 'set_gpn_recovery_watch', enabled: next });
            else if (set === 'failover') B.postToHost({ action: 'set_gpn_failover', enabled: next });
          } else if (set === 'autoreconnect') {
            DEMO.autoReconnect = next;
          }
        });
        return;
      }
      if (ctl.dataset.nxSet === 'sysproxy-toggle') {
        ctl.addEventListener('click', () => {
          const next = !ctl.classList.contains('on');
          ctl.classList.toggle('on', next);
          ctl.textContent = next ? nxT('proxy.on') : nxT('proxy.off');
          if (B) B.postToHost({ action: 'toggle_system_proxy' });
          SET('systemProxyMode', next ? 1 : 0);
        });
        return;
      }
      if (ctl.dataset.nxSet === 'proxytest') {
        ctl.addEventListener('click', () => { _nxProxyTestClick(); });
        return;
      }
      ctl.addEventListener('change', () => {
        const set = ctl.dataset.nxSet;
        const v = ctl.value;
        if (B) {
          if (set === 'mode') B.postToHost({ action: 'set_connection_mode', mode: v });
          else if (set === 'transport') B.postToHost({ action: 'set_transport', transport: v });
          else if (set === 'split') B.postToHost({ action: 'set_split_mode', mode: v });
          else if (set === 'splitdir') B.postToHost({ action: 'set_split_direction', invert: String(v === 'blacklist') });
          else if (set === 'sysproxy') B.postToHost({ action: 'set_system_proxy_mode', mode: parseInt(v, 10) });
          else if (set === 'protocol') B.postToHost({ action: 'set_protocol_preference', protocol: v });
          else if (set === 'tunstack') B.postToHost({ action: 'set_tun_stack', stack: v });
          else if (set === 'effects') B.postToHost({ action: 'set_effects_tier', tier: v });
          else if (set === 'language') {
            // Switch the whole app language instantly (applies to the main
            // dashboard AND every loaded skin), then persist through the host.
            B.setLanguage(v);
          }
        }
        if (set === 'mode') SET('mode', v);
        else if (set === 'transport') SET('transport', v);
        else if (set === 'split') SET('splitMode', v);
        else if (set === 'sysproxy') SET('systemProxyMode', parseInt(v, 10));
        else if (set === 'protocol') SET('protocolPreference', v);
        nxRenderRoute();
      });
    });
  }

  // ---------- GPN Servers sub-view ----------
  // Italy/Germany WireGuard candidates auto-selected on GPN Connect. The list,
  // status and live probe badges come from the host through the skinBridge
  // (the same setGpnServers / setGpnServerProbes data the main dashboard uses).
  let _nxGpnProbing = false;

  function nxGpnProbeFor(S, serverId) {
    return (S.gpnServerProbes || []).find(p => String(p.serverId) === String(serverId)) || null;
  }

  function nxGpnUdpLabel(status) {
    if (status === 'Open' || status === 'open') return nxT('gpns.udp.open');
    if (status === 'Blocked' || status === 'blocked' || status === 'HandshakeNoResponse') return nxT('gpns.udp.blocked');
    return nxT('gpns.udp.unknown');
  }

  function nxRenderGpnServers() {
    const root = nxRef('nxGpnServers');
    if (!root) return;
    const S = nxState();
    const servers = S.gpnServers || [];
    const probeLabel = _nxGpnProbing ? nxT('gpns.measuring') : nxT('gpns.measure');
    root.innerHTML =
        '<div class="nx-toolbar">'
        + '<button type="button" class="nx-btn" data-nx-gpn="probe">⚡ <span>' + escHtml(probeLabel) + '</span></button>'
        + '<button type="button" class="nx-btn" data-nx-gpn="refresh">⟳ <span>' + escHtml(nxT('gpns.refresh')) + '</span></button>'
        + '<button type="button" class="nx-btn" data-nx-gpn="add">+ <span>' + escHtml(nxT('gpns.add')) + '</span></button>'
        + '<button type="button" class="nx-btn" data-nx-gpn="defaults">↺ <span>' + escHtml(nxT('gpns.restoreDefaults')) + '</span></button>'
        + '</div>'
        + '<div class="nx-gpnImport">'
        + '<div class="nx-label">' + escHtml(nxT('gpns.importTitle')) + '</div>'
        + '<p class="nx-gpnImportDesc">' + escHtml(nxT('gpns.importDesc')) + '</p>'
        + '<textarea id="nxGpnConf" class="nx-gpnConf" rows="5" spellcheck="false" placeholder="[Interface]&#10;PrivateKey = …&#10;Address = 10.66.66.2/24&#10;&#10;[Peer]&#10;PublicKey = …&#10;Endpoint = 92.4.220.236:51820&#10;AllowedIPs = 0.0.0.0/0, ::/0"></textarea>'
        + '<div class="nx-gpnImportRow"><button type="button" class="nx-btn primary" data-nx-gpn="import">' + escHtml(nxT('gpns.import')) + '</button><span class="nx-gpnDpapi">' + escHtml(nxT('gpns.dpapiNote')) + '</span></div>'
        + '</div>'
        + '<div class="nx-gpnSub"><div class="nx-label">' + escHtml(nxT('gpns.managed')) + '</div><span class="nx-subtle">' + escHtml(nxT('gpns.managedDesc')) + '</span></div>'
        + '<div class="nx-gpnList">' + (servers.length === 0
            ? '<p class="nx-empty center">' + escHtml(nxT('gpns.empty')) + '</p>'
            : servers.map(s => {
                const addr = (s.endpointHost || '') + ':' + (s.endpointPort || '');
                const probe = nxGpnProbeFor(S, s.serverId);
                let probeBadge = '';
                if (probe && s.isEnabled) {
                  const delay = probe.isSuccess && Number.isFinite(probe.delayMs) && probe.delayMs >= 0 ? Math.round(probe.delayMs) + ' ms' : '—';
                  const loss = probe.isSuccess && probe.lossPercent > 0 ? ' · %' + probe.lossPercent : '';
                  probeBadge = '<span class="nx-gpnProbe" title="' + escHtml(nxT('gpns.probeTitle')) + '">' + escHtml(delay + loss + ' · ' + nxGpnUdpLabel(probe.udpStatus)) + '</span>';
                }
                return '<div class="nx-gpnSrv' + (s.isEnabled ? '' : ' off') + '" data-nx-gpnsrv="' + escHtml(String(s.serverId)) + '">'
                  + '<div class="nx-srvFlag">🛡</div>'
                  + '<div class="nx-gpnSrvInfo">'
                  + '<div class="nx-gpnSrvName"><b>' + escHtml(s.name || s.serverId || 'GPN') + '</b>' + (s.keyProtected ? ' <span class="nx-gpnKey" title="' + escHtml(nxT('gpns.dpapiNote')) + '">🔐</span>' : '') + '</div>'
                  + '<span class="nx-srvMeta mono">' + escHtml(addr) + '</span>'
                  + '<span class="nx-srvMeta dim">' + escHtml(s.clientAddress || '') + ' · MTU ' + (s.mtu || 1420) + ' · DNS ' + escHtml(s.dns || '1.1.1.1') + '</span>'
                  + '</div>'
                  + '<span class="nx-gpnStatus' + (s.isEnabled ? ' on' : '') + '">' + escHtml(s.isEnabled ? nxT('gpns.active') : nxT('gpns.disabled')) + '</span>'
                  + probeBadge
                  + '<div class="nx-gpnActions">'
                  + '<button type="button" class="nx-btn sm" data-nx-gpn="edit" data-server="' + escHtml(String(s.serverId)) + '">' + escHtml(nxT('gpns.edit')) + '</button>'
                  + '<button type="button" class="nx-btn sm" data-nx-gpn="toggle" data-server="' + escHtml(String(s.serverId)) + '" data-enabled="' + (s.isEnabled ? '0' : '1') + '">' + escHtml(s.isEnabled ? nxT('gpns.disable') : nxT('gpns.enable')) + '</button>'
                  + '<button type="button" class="nx-btn sm danger" data-nx-gpn="delete" data-server="' + escHtml(String(s.serverId)) + '">' + escHtml(nxT('gpns.delete')) + '</button>'
                  + '</div>'
                  + '</div>';
              }).join(''))
        + '</div>';
    root.querySelectorAll('[data-nx-gpn]').forEach(btn => {
      btn.addEventListener('click', () => {
        const act = btn.dataset.nxGpn;
        const serverId = btn.dataset.server;
        if (act === 'probe') {
          _nxGpnProbing = true;
          if (B) B.postToHost({ action: 'gpn_servers_probe' });
          setTimeout(() => { _nxGpnProbing = false; nxRenderGpnServers(); }, 12000);
          nxRenderGpnServers();
          return;
        }
        if (act === 'refresh') { if (B) B.postToHost({ action: 'gpn_servers_list' }); return; }
        if (act === 'add') { if (B) B.postToHost({ action: 'gpn_server_add_dialog' }); return; }
        if (act === 'defaults') {
          if (B) B.postToHost({ action: 'gpn_defaults_restore' });
          nxToastMsg(nxT('gpns.restored'));
          return;
        }
        if (act === 'import') {
          const text = nxRef('nxGpnConf');
          if (!text || !text.value.trim()) {
            nxToastMsg(nxT('gpns.emptyConf'));
            return;
          }
          if (B) B.postToHost({ action: 'gpn_server_add', confText: text.value });
          else nxToastMsg(nxT('gpns.imported'));
          text.value = '';
          return;
        }
        if (act === 'edit') { if (B) B.postToHost({ action: 'gpn_server_edit_dialog', serverId }); return; }
        if (act === 'toggle') {
          if (B) B.postToHost({ action: 'gpn_server_toggle', serverId, enabled: btn.dataset.enabled === '1' });
          return;
        }
        if (act === 'delete') {
          if (typeof confirm === 'function') {
            try { if (!confirm(nxT('gpns.confirmDelete'))) return; } catch (e) { /* jsdom has no real confirm */ }
          }
          if (B) B.postToHost({ action: 'gpn_server_delete', serverId });
        }
      });
    });
  }

  // ---------- About & Help sub-view ----------
  let _nxAboutTab = 'info';
  let _nxReleaseNotes = null; // cached milestones from Temalar/release-notes.json

  async function _nxLoadReleaseNotes() {
    if (_nxReleaseNotes) return _nxReleaseNotes;
    try {
      const res = await fetch('../release-notes.json', { cache: 'no-store' });
      if (!res.ok) return null;
      const data = await res.json();
      _nxReleaseNotes = (data && Array.isArray(data.milestones)) ? data.milestones : [];
    } catch (e) {
      _nxReleaseNotes = [];
    }
    return _nxReleaseNotes;
  }

  function _nxCleanText(s) {
    return String(s || '').replace(/\*\*/g, '').replace(/[`_]/g, '').trim();
  }

  function nxAboutHelpCard(icon, title, desc, href) {
    return '<a class="nx-aboutCard" href="' + escHtml(href) + '" target="_blank" rel="noopener noreferrer">'
      + '<span class="nx-aboutCardIcon">' + icon + '</span>'
      + '<span class="nx-aboutCardTxt"><b>' + escHtml(title) + '</b><em>' + escHtml(desc) + '</em></span></a>';
  }

  function nxRenderAbout() {
    const root = nxRef('nxAbout');
    if (!root) return;
    const S = nxState();
    const info = S.appInfo || {};
    const version = (info && info.version) ? 'V' + String(info.version) : 'V1.1.1';
    const appName = (info && info.appName) ? String(info.appName) : 'AO GPN';
    const tab = (v) => (v === _nxAboutTab ? ' active' : '');
    root.innerHTML =
        '<div class="nx-aboutHero">'
        + '<div class="nx-aboutLogo">' + escHtml(appName) + '</div>'
        + '<div class="nx-aboutTxt"><b>' + escHtml(appName) + ' Desktop</b>'
        + '<span>' + escHtml(nxT('about.description')) + '</span>'
        + '<em>' + escHtml(nxT('about.thanks')) + '</em></div>'
        + '<div class="nx-aboutVer"><small>' + escHtml(nxT('about.version')) + '</small><b>' + escHtml(version) + '</b></div>'
        + '</div>'
        + '<div class="nx-aboutTabs" role="tablist">'
        + '<button type="button" class="nx-segOpt' + tab('info') + '" data-nx-about="info">' + escHtml(nxT('about.tabInfo')) + '</button>'
        + '<button type="button" class="nx-segOpt' + tab('help') + '" data-nx-about="help">' + escHtml(nxT('about.tabHelp')) + '</button>'
        + '<button type="button" class="nx-segOpt' + tab('release') + '" data-nx-about="release">' + escHtml(nxT('about.tabRelease')) + '</button>'
        + '</div>'
        + '<div class="nx-aboutBody">'
        + '<div class="nx-aboutPanel' + (tab('info') || ' nx-hidden') + '" data-nx-aboutPanel="info">'
        + '<div class="nx-aboutRow"><span>' + escHtml(nxT('about.maintainer')) + '</span><b>Ahmet Özbay</b></div>'
        + '<div class="nx-aboutRow"><span>' + escHtml(nxT('about.license')) + '</span><b>GPL-3.0</b></div>'
        + '<div class="nx-aboutRow"><span>' + escHtml(nxT('about.platform')) + '</span><b>Windows · Linux · macOS</b></div>'
        + '<div class="nx-aboutRow"><span>' + escHtml(nxT('about.cores')) + '</span><b>Xray · sing-box</b></div>'
        + '<div class="nx-aboutRow"><span>' + escHtml(nxT('about.homepage')) + '</span><a href="https://github.com/AhmetOzbay27/AoGPN/wiki" target="_blank" rel="noopener noreferrer">https://github.com/AhmetOzbay27/AoGPN/wiki</a></div>'
        + '<div class="nx-aboutRow"><span>' + escHtml(nxT('about.source')) + '</span><a href="https://github.com/AhmetOzbay27/AoGPN" target="_blank" rel="noopener noreferrer">https://github.com/AhmetOzbay27/AoGPN</a></div>'
        + '</div>'
        + '<div class="nx-aboutPanel' + (tab('help') || ' nx-hidden') + '" data-nx-aboutPanel="help">'
        + nxAboutHelpCard('📘', nxT('about.helpWiki'), nxT('about.helpWikiDesc'), 'https://github.com/AhmetOzbay27/AoGPN/wiki')
        + nxAboutHelpCard('🐞', nxT('about.helpBug'), nxT('about.helpBugDesc'), 'https://github.com/AhmetOzbay27/AoGPN/issues/new/choose')
        + nxAboutHelpCard('💡', nxT('about.helpFeature'), nxT('about.helpFeatureDesc'), 'https://github.com/AhmetOzbay27/AoGPN/issues/new/choose')
        + nxAboutHelpCard('💬', nxT('about.helpTg'), nxT('about.helpTgDesc'), 'https://t.me/AoGPN')
        + nxAboutHelpCard('📢', nxT('about.helpChannel'), nxT('about.helpChannelDesc'), 'https://t.me/AoGPN')
        + '</div>'
        + '<div class="nx-aboutPanel' + (tab('release') || ' nx-hidden') + '" data-nx-aboutPanel="release">'
        + '<div class="nx-relHead"><p>' + escHtml(nxT('about.releaseIntro')) + '</p>'
        + '<div class="nx-gpnActions"><button type="button" class="nx-btn sm" data-nx-rel="expand">' + escHtml(nxT('about.releaseExpandAll')) + '</button>'
        + '<button type="button" class="nx-btn sm" data-nx-rel="collapse">' + escHtml(nxT('about.releaseCollapseAll')) + '</button></div></div>'
        + '<div class="nx-relList">' + escHtml(nxT('about.releaseEmpty')) + '</div>'
        + '</div>'
        + '</div>';
    // Tab switching persists across the 2 s re-render (module state, not DOM).
    root.querySelectorAll('[data-nx-about]').forEach(btn => {
      btn.addEventListener('click', () => { _nxAboutTab = btn.dataset.nxAbout; nxRenderAbout(); });
    });
    root.querySelectorAll('[data-nx-rel="expand"]').forEach(b => b.addEventListener('click', () => {
      root.querySelectorAll('.nx-relBody').forEach(x => x.classList.add('nx-show'));
      root.querySelectorAll('.nx-relBtn').forEach(x => x.classList.add('open'));
    }));
    root.querySelectorAll('[data-nx-rel="collapse"]').forEach(b => b.addEventListener('click', () => {
      root.querySelectorAll('.nx-relBody').forEach(x => x.classList.remove('nx-show'));
      root.querySelectorAll('.nx-relBtn').forEach(x => x.classList.remove('open'));
    }));
    if (_nxAboutTab === 'release') _nxRenderReleaseList(root);
  }

  async function _nxRenderReleaseList(root) {
    const listEl = root && root.querySelector('.nx-relList');
    if (!listEl) return;
    const milestones = await _nxLoadReleaseNotes();
    const list = milestones || [];
    if (!list.length) {
      listEl.innerHTML = '<p class="nx-empty center">' + escHtml(nxT('about.releaseEmpty')) + '</p>';
      return;
    }
    listEl.innerHTML = list.map(m => {
      const title = _nxCleanText(m.title);
      const desc = _nxCleanText(m.desc);
      return '<div class="nx-relRow"><button type="button" class="nx-relBtn">'
        + '<span class="nx-relBadge">' + escHtml(_nxCleanText(m.n)) + '</span>'
        + '<span class="nx-relTitle">' + escHtml(title) + '</span>'
        + '<span class="nx-relChev">▾</span></button>'
        + (desc ? '<div class="nx-relBody">' + escHtml(desc) + '</div>' : '')
        + '</div>';
    }).join('');
    listEl.querySelectorAll('.nx-relBtn').forEach(btn => {
      btn.addEventListener('click', () => {
        const body = btn.nextElementSibling;
        if (!body) return;
        body.classList.toggle('nx-show');
        btn.classList.toggle('open');
      });
    });
  }

  // ---------- latency bars ----------
  function nxRefreshBars() {
    const bar = nxRef('nxBars');
    if (!bar) return;
    const S = nxState();
    const tele = S.telemetry || [];
    const has = tele.some(v => v !== undefined && v !== null);
    const ping = (S.connected && has && Number.isFinite(tele[0]) && tele[0] > 0) ? tele[0] : null;
    bar.innerHTML = Array.from({ length: 18 }).map(() => {
      let h = 12 + Math.round(Math.random() * 55);
      if (S.connected && ping != null) h = Math.max(8, Math.min(82, Math.round(ping * 0.6)));
      return '<i style="height:' + h + '%"></i>';
    }).join('');
  }

  // ---- undo toast (Boost parity) ----
  // The host pushes setUndoAvailable({ kind, processName, displayName }) after
  // every app mutation; the toast shows the action with a real Undo button that
  // posts undo_last_app_op — the same single-level slot the main dashboard uses.
  let _nxUndoShowing = false;
  let _nxUndoSignature = '';
  let _nxUndoTimer = null;

  function nxToastMsg(msg) {
    const el = nxRef('nxToast');
    if (el) {
      _nxUndoShowing = false;
      el.innerHTML = '<span>' + escHtml(msg) + '</span>';
      el.classList.add('show');
      if (_nxUndoTimer) clearTimeout(_nxUndoTimer);
      _nxUndoTimer = setTimeout(() => el.classList.remove('show'), 1800);
    }
  }

  function nxSyncUndoToast() {
    const el = nxRef('nxToast');
    if (!el) return;
    const slot = nxState().undoAvailable;
    if (!slot || typeof slot !== 'object' || !slot.kind) return;
    const sig = slot.kind + '|' + (slot.displayName || slot.processName || '');
    // Already on screen — the 2 s sync must not re-render (and reset) it.
    if (_nxUndoShowing && sig === _nxUndoSignature) return;
    const name = slot.displayName || slot.processName || '';
    const text = slot.kind === 'remove' ? nxT('undo.remove', { name })
      : slot.kind === 'route' ? nxT('undo.route', { name })
      : slot.kind === 'add' ? nxT('undo.add', { name })
      : name;
    el.innerHTML = '<span class="nx-toastTxt">' + escHtml(text) + '</span>'
      + '<button type="button" class="nx-toastBtn" data-nx-undo>' + escHtml(nxT('undo.action')) + '</button>'
      + '<button type="button" class="nx-toastBtn x" data-nx-undox aria-label="' + escHtml(nxT('undo.dismiss')) + '" title="' + escHtml(nxT('undo.dismiss')) + '">✕</button>';
    el.classList.add('show');
    _nxUndoShowing = true;
    _nxUndoSignature = sig;
    if (_nxUndoTimer) clearTimeout(_nxUndoTimer);
    _nxUndoTimer = setTimeout(() => {
      el.classList.remove('show');
      _nxUndoShowing = false;
      _nxUndoSignature = '';
    }, 10000);
    const undoBtn = el.querySelector('[data-nx-undo]');
    if (undoBtn) undoBtn.addEventListener('click', () => {
      if (B) B.postToHost({ action: 'undo_last_app_op' });
      el.classList.remove('show');
      _nxUndoShowing = false;
      _nxUndoSignature = '';
    });
    const xBtn = el.querySelector('[data-nx-undox]');
    if (xBtn) xBtn.addEventListener('click', () => {
      el.classList.remove('show');
      _nxUndoShowing = false;
      _nxUndoSignature = '';
    });
  }

  // ---------- live refresh (host pushes + safety poll) ----------
  // Host push handler (skinBridge.subscribe). The host is authoritative: when a
  // fresh monitor snapshot (or any state push) arrives, drop local route
  // overrides whose value the host has confirmed, so the dashboard's route
  // selector and this skin can never drift apart. Overrides for toggles the
  // host hasn't echoed yet survive, keeping the optimistic flip on screen.
  function nxOnHostPush() {
    const S = nxState();
    const apps = S.monitorSnapshot.apps || [];
    const now = Date.now();
    Object.keys(_nxLocalRoutes).forEach(pname => {
      const app = apps.find(a => (a.processName || a.value) === pname);
      if (!app) {
        // App removed from the boost list — drop any stale override.
        delete _nxLocalRoutes[pname];
        delete _nxLocalRouteTs[pname];
        return;
      }
      const pendingMs = now - (_nxLocalRouteTs[pname] || 0);
      if (app.action === _nxLocalRoutes[pname]) {
        // Host confirmed the toggle — the authoritative action now matches the
        // override, so the override is redundant and must go.
        delete _nxLocalRoutes[pname];
        delete _nxLocalRouteTs[pname];
      } else if (pendingMs > NX_ROUTE_CONFIRM_MS) {
        // The echo window expired and the host reports something else (e.g. the
        // route was changed from the dashboard, or the host rejected the
        // toggle). The authoritative snapshot wins — clear the override.
        delete _nxLocalRoutes[pname];
        delete _nxLocalRouteTs[pname];
      }
    });
    nxSyncAll();
  }

  function nxSyncAll() {
    // Static markup and dynamic panels both carry translated strings; re-apply
    // the static texts first so a language switch updates them instantly.
    nxApplyStaticTexts();
    syncNexusSkin();
    nxRenderDashboardPanels();
    if (_nxView === 'servers') nxRenderServers();
    if (_nxView === 'route') nxRenderRoute();
    if (_nxView === 'gpnsrv') nxRenderGpnServers();
    if (_nxView === 'games') nxRenderGameProfiles();
    if (_nxView === 'analytics') { nxRenderAnalytics(); nxRenderMonitor(); }
    if (_nxView === 'settings') nxRenderSettings();
    if (_nxView === 'about') nxRenderAbout();
    nxSyncUndoToast();
  }

  // ---------- boot ----------
  // ---- global keyboard shortcuts (shared across all skins) ----
  //   Ctrl+Enter -> connect / disconnect (same toggle as the GPN Connect)
  //   Alt+1..8   -> switch views (dashboard/route/servers/gpn/games/analytics/
  //                 settings/about — matches the sidebar order)
  //   R          -> cycle the focused app's route (vpn/direct/block/warp)
  // The exact same set exists in CYBER and INFRA (Alt+1..5 for their tabs).
  const NX_VIEW_KEYS = ['dashboard', 'route', 'servers', 'gpnsrv', 'games', 'analytics', 'settings', 'about'];
  let _nxShortcutsBound = false;

  // Cycle one app's route through the host — same local-override model as the
  // game-switch keydown handler (override + post + toast + full sync).
  function nxCycleRoute(pname) {
    const S = nxState();
    const app = (S.monitorSnapshot.apps || []).find(a => (a.processName || a.value) === pname);
    const cur = nxActionFor(app || { processName: pname });
    const next = nxRouteCycle(cur);
    if (next === cur) return;
    _nxLocalRoutes[pname] = next;
    _nxLocalRouteTs[pname] = Date.now();
    const displayName = app ? (app.displayName || pname) : pname;
    if (B) B.postToHost({ action: 'set_app_route', processName: pname, displayName, route: next });
    nxToastMsg('ROUTE ' + next.toUpperCase() + ' · ' + displayName.toUpperCase());
    nxSyncAll();
  }

  function nxBindShortcuts() {
    if (_nxShortcutsBound) return;
    _nxShortcutsBound = true;
    document.addEventListener('keydown', (e) => {
      // Connect / disconnect — safe in every context, including inputs.
      if (e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'Enter' || e.key === 'NumpadEnter')) {
        e.preventDefault();
        nxToggleConnect();
        return;
      }
      const typing = e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.target.tagName === 'TEXTAREA' || e.target.isContentEditable);
      // View switching.
      if (e.altKey && !e.ctrlKey && !e.metaKey && !typing && /^[1-8]$/.test(e.key)) {
        e.preventDefault();
        nxGo(NX_VIEW_KEYS[Number(e.key) - 1]);
        return;
      }
      // Route cycle on the focused app switch / row (games view and dashboard cards).
      if (!typing && !e.altKey && !e.ctrlKey && !e.metaKey && (e.key === 'r' || e.key === 'R')) {
        const el = document.activeElement && document.activeElement.closest
          ? document.activeElement.closest('[data-nx-pname]')
          : null;
        if (el && el.dataset && el.dataset.nxPname) {
          e.preventDefault();
          nxCycleRoute(el.dataset.nxPname);
        }
      }
    });
  }

  function boot() {
    wireNexusSkin();
    nxBindShortcuts();
    nxApplyStaticTexts();
    nxGo('dashboard');
    if (NX_PREVIEW) {
      // Static snapshot: fill the gauges/bars once using a connected look so
      // the thumbnail renders the design at its best. No bridge subscription,
      // no poll, no timers (avoids running a second live skin instance).
      DEMO.connected = true;
      DEMO.telemetry = [22, 4, 18, 7];
      DEMO.selectedNode = (DEMO.nodes || [])[0] || null;
      DEMO.splitMode = 'off';
      document.body.classList.add('nx-preview');
      // Render once without the 2s bar-churn loop (nxRefreshBars fights the
      // CSS bar animation); a static snapshot is enough for a thumbnail.
      syncNexusSkin();
      nxRenderDashboardPanels();
      if (_nxView === 'servers') nxRenderServers();
      if (_nxView === 'route') nxRenderRoute();
      if (_nxView === 'games') nxRenderGameProfiles();
      nxRefreshBars();
      return;
    }
    nxSyncAll();
    if (B && typeof B.subscribe === 'function') B.subscribe(nxOnHostPush);
    // One throttled safety refresh per skin instance; pause it when the iframe
    // is hidden so inactive themes never consume CPU in the background.
    _nxSyncTimer = setInterval(() => {
      if (document.visibilityState !== 'hidden') nxSyncAll();
    }, 2000);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
