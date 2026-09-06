/* ==========================================================================
   split-wave6.js — app.js → features/nodes.js (Dalga 6)
   --------------------------------------------------------------------------
   VPN Rotaları modülü: düğüm durumu (realNodes, seçim, sürükle, test, havuz),
   renderNodes + kartlar, bağlam menüsü/onay, node test/seçici/pool bağları ve
   host köprüsü window.updateNode* / setNodeSwitchResult. Durum modülle birlikte
   taşınır; app.js ve skinBridge aogpn.nodes erişimcilerine bağlanır.
   ========================================================================== */
const fs = require('fs');
const path = require('path');

const APP = path.join(__dirname, 'app.js');
const OUT = path.join(__dirname, 'features', 'nodes.js');

let src = fs.readFileSync(APP, 'utf8');

function countOcc(hay, needle) {
  let n = 0, i = 0;
  while ((i = hay.indexOf(needle, i)) !== -1) { n++; i += needle.length; }
  return n;
}
function cutBlock(startAnchor, endAnchor, includeEnd) {
  if (countOcc(src, startAnchor) !== 1) throw new Error('START beklenmiyor: ' + startAnchor.slice(0, 60));
  if (countOcc(src, endAnchor) !== 1) throw new Error('END beklenmiyor: ' + endAnchor.slice(0, 60));
  const s = src.indexOf(startAnchor);
  let e = src.indexOf(endAnchor);
  if (includeEnd) e += endAnchor.length;
  const block = src.slice(s, e);
  src = src.slice(0, s) + src.slice(e);
  return block;
}

// --- 1) Kesimler (aşağıdan yukarıya) ---------------------------------------
const blockC = cutBlock('  window.updateNodeInfo = (name, address, protocol) => {',
  '  if (!window.__aogpnPerfDisabled && hasWebViewBridge()) startPerformanceProbe();', false);
const blockB = cutBlock('  function applyHostNode() {',
  '  window.applySettings = (data) => {', false);
const blockA2 = cutBlock('  let pendingConfirmAction = null;',
  '  let processCatalog = [];', false);
const blockA1 = cutBlock('  let hostNode = null;',
  '  let nodePoolLinks = [];', true);

// --- 2) Block içi yeniden bağlama ------------------------------------------
const rewrites = [
  // renderNodePool / pool fetch: currentView app.js durumuna registry üzerinden
  ['    if (currentView !== \'nodes\' || !useRealNodes) {',
   '    if (aogpn.app.getCurrentView() !== \'nodes\' || !useRealNodes) {', 1],
];
for (const [from, to, expected] of rewrites) {
  const all = [blockA1, blockA2, blockB, blockC].join('\n');
  const n = countOcc(all, from);
  if (n !== expected) throw new Error(`BLOCK rewrite beklenmiyor (${expected} != ${n}): ${from.slice(0, 70)}`);
}
let blockB2 = blockB.split(rewrites[0][0]).join(rewrites[0][1]);

// --- 3) Modül dosyası --------------------------------------------------------
const moduleText = `/* ==========================================================================
   features/nodes.js — AoGPN dashboard feature module (VPN Rotaları)
   --------------------------------------------------------------------------
   Düğüm durumu ve çizimi: hostNode/NODES/realNodes, seçim + çoklu seçim,
   sürükle-sırala, ping testi, düğüm havuzu (URL abonelikleri), bağlam menüsü
   ve toplu işlemler (kopyala/yapıştır/dedup/cleanup). Host köprüsü
   window.updateNodeInfo / updateNodeList* / updateDisabledNodes / updateNodePool
   / updateNodeTest / setNodeTestRunning / setNodeSwitchResult sözleşmesi bu
   modülde yaşar. app.js ve skinBridge aogpn.nodes erişimcileriyle bağlanır.
   ========================================================================== */
(() => {
  'use strict';
  const t = aogpn.i18n.t;
  const escHtml = aogpn.util.escHtml;
  const $ = aogpn.util.$;
  const postToHost = aogpn.bridge.postToHost;
  const refreshAllCustomSelects = aogpn.selects.refreshAllCustomSelects;

${blockA1}

${blockA2}

${blockB2}

${blockC}

  window.aogpn = window.aogpn || {};
  window.aogpn.nodes = {
    applyHostNode, refreshSessionNode, applyNode, renderTopNodeSelect,
    requestNodeSwitch, renderNodes,
    getUseRealNodes: () => useRealNodes,
    getRealNodes: () => realNodes,
    getNodes: () => NODES,
    getNodesList: () => useRealNodes ? [...realNodes.values()] : NODES,
    getActiveRealNodeId: () => activeRealNodeId,
    getSelectedNode: () => selectedNode,
    setSelectedNode: (v) => { selectedNode = v; },
    setUseRealNodes: (v) => { useRealNodes = !!v; }
  };
})();
`;

fs.writeFileSync(OUT, moduleText);

// --- 4) app.js çağrı yerleri -------------------------------------------------
const callerRewrites = [
  // setConnected → refreshSessionNode
  ['    refreshSessionNode();',
   '    aogpn.nodes.refreshSessionNode();', 1],
  // topNodeSelectEl bağlama
  ['        requestNodeSwitch(topNodeSelectEl.value);',
   '        aogpn.nodes.requestNodeSwitch(topNodeSelectEl.value);', 1],
  // window dışa aktarımları
  ['  window.requestNodeSwitch = requestNodeSwitch;\n  window.applyNode = applyNode;',
   '  window.requestNodeSwitch = (indexId) => aogpn.nodes.requestNodeSwitch(indexId);\n  window.applyNode = () => aogpn.nodes.applyNode();', 1],
  // skinBridge: düğüm erişimcileri
  ['    get useRealNodes() { return useRealNodes; },',
   '    get useRealNodes() { return aogpn.nodes.getUseRealNodes(); },', 1],
  ['    get nodes() { return useRealNodes ? [...realNodes.values()] : NODES; },',
   '    get nodes() { return aogpn.nodes.getNodesList(); },', 1],
  ['    get realNodes() { return realNodes; },',
   '    get realNodes() { return aogpn.nodes.getRealNodes(); },', 1],
  ['    get NODES() { return NODES; },',
   '    get NODES() { return aogpn.nodes.getNodes(); },', 1],
  ['    get activeRealNodeId() { return activeRealNodeId; },',
   '    get activeRealNodeId() { return aogpn.nodes.getActiveRealNodeId(); },', 1],
  ['    get selectedNode() { return selectedNode; },',
   '    get selectedNode() { return aogpn.nodes.getSelectedNode(); },', 1],
  ['    set selectedNode(v) { selectedNode = v; },',
   '    set selectedNode(v) { aogpn.nodes.setSelectedNode(v); },', 1],
  ['    set useRealNodes(v) { useRealNodes = !!v; },',
   '    set useRealNodes(v) { aogpn.nodes.setUseRealNodes(!!v); },', 1],
  ['    requestNodeSwitch,\n    applyNode,',
   '    requestNodeSwitch: (indexId) => aogpn.nodes.requestNodeSwitch(indexId),\n    applyNode: () => aogpn.nodes.applyNode(),', 1],
  // skinBridge: routeLabels artık views modülünde (Dalga 5 kalıntısı düzeltmesi)
  ['    get routeLabels() { return routeLabels; },',
   '    get routeLabels() { return aogpn.views.getRouteLabels(); },', 1],
  // aogpn.app registry: renderTopNodeSelect delegasyonu
  ['    applyMode, renderTopNodeSelect, updateTransportLock, applyProtocolPreference,',
   '    applyMode, updateTransportLock, applyProtocolPreference,\n    renderTopNodeSelect: () => aogpn.nodes.renderTopNodeSelect(),', 1],
  // aogpn.app registry: düğüm durum erişimcileri delegasyonu
  ['    getUseRealNodes: () => useRealNodes,\n    getRealNodes: () => realNodes,\n    getNodes: () => NODES,',
   '    getUseRealNodes: () => aogpn.nodes.getUseRealNodes(),\n    getRealNodes: () => aogpn.nodes.getRealNodes(),\n    getNodes: () => aogpn.nodes.getNodes(),', 1],
  // init
  ['  renderTopNodeSelect();',
   '  aogpn.nodes.renderTopNodeSelect();', 1],
];

for (const [from, to, expected] of callerRewrites) {
  const n = countOcc(src, from);
  if (n !== expected) throw new Error(`CALLER rewrite beklenmiyor (${expected} != ${n}): ${from.slice(0, 70)}`);
  src = src.split(from).join(to);
}

fs.writeFileSync(APP, src);

// --- 5) Doğrulama ------------------------------------------------------------
const declCheck = ['hostNode', 'NODES', 'realNodes', 'canStartNodeTest', 'applyHostNode',
  'refreshSessionNode', 'applyNode', 'realNodeCard', 'renderTopNodeSelect', 'requestNodeSwitch',
  'sortedRealNodes', 'renderNodes', 'updateNodeSelectionUI', 'updateNodesHeader', 'syncEditMode',
  'nodeCtxMenu', 'showNodeCtxMenu', 'requestConfirm', 'requestNodeDelete', 'closeNodeConfirm',
  'notifyNodes', 'syncNodeTestType', 'nodeSortSel', 'nodePoolAddBtn', 'editingPoolUrl',
  'renderNodePool', 'fileDropOverlay'];
for (const fn of declCheck) {
  if (!moduleText.includes(fn)) throw new Error('Modülde eksik: ' + fn);
}
for (const w of ['window.updateNodeInfo', 'window.updateNodeListAppend', 'window.updateNodeListDone',
  'window.updateDisabledNodes', 'window.updateNodePool', 'window.updateNodeTest',
  'window.setNodeTestRunning', 'window.setNodeSwitchResult', 'window.notifyNodes', 'window.aogpn.nodes']) {
  if (!moduleText.includes(w)) throw new Error('Modülde eksik window export: ' + w);
}

console.log('OK — features/nodes.js: ' + moduleText.split('\n').length + ' satır; app.js: ' + src.split('\n').length + ' satır.');