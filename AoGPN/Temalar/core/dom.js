/* ==========================================================================
   core/dom.js — AoGPN dashboard module (dashboard element referansları)
   --------------------------------------------------------------------------
   app.js ile aynı küresel pencere kapsamında, ancak app.js'den ÖNCE yüklenir
   (index.html <script> sırası ve skins/skin-sandbox.js MODULE_FILES listesi).
   window.aogpn ad alanına kaydolur; app.js bu ad alanından kendi yerel
   takma adlarını alır. ========================================================================== */
(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const btnIcon = $('btnIcon'), btnText = $('btnText'), btnSub = $('btnSub'),
        statusDot = $('statusDot'), statusLabel = $('statusLabel'),
        nodeName = $('nodeName'), nodeAddr = $('nodeAddr'),
        ipDisplay = $('ipDisplay'), ipCountry = $('ipCountry'),
        ipVerifyText = $('ipVerifyText'), ipVerifyDetail = $('ipVerifyDetail'),
        ipVerifyCard = $('ipVerifyCard'),
        ipLeakBanner = $('ipLeakBanner'), ipLeakText = $('ipLeakText'),
        ipOkBanner = $('ipOkBanner'), ipOkText = $('ipOkText'),
        ipOkTunnelIp = $('ipOkTunnelIp'), ipOkIspIp = $('ipOkIspIp'),
        pingVal = $('pingVal'), pingSub = $('pingSub'),
        lossVal = $('lossVal'), lossSub = $('lossSub'),
        downVal = $('downVal'), upVal = $('upVal'),
        downGauge = $('downGauge'), upGauge = $('upGauge'),
        systemProxyToggleBtn = $('systemProxyToggleBtn'), systemProxyDot = $('systemProxyDot'),
        systemProxyLabel = $('systemProxyLabel'), systemProxyAddress = $('systemProxyAddress'), systemProxyModeSelect = $('systemProxyModeSelect'),
        proxyTestBtn = $('proxyTestBtn'), proxyTestIcon = $('proxyTestIcon'), proxyTestLabel = $('proxyTestLabel'),
        quickProtocolSelect = $('quickProtocolSelect'),
        quickAutoReconnect = $('quickAutoReconnect'),
        protocolHint = $('protocolHint'),
        routeHint = $('routeHint'), transportHint = $('transportHint'),
        connectionStatusLine = $('connectionStatusLine');

  window.aogpn = window.aogpn || {};
  window.aogpn.dom = {
    btnIcon, btnText, btnSub, statusDot, statusLabel, nodeName, nodeAddr,
    ipDisplay, ipCountry, ipVerifyText, ipVerifyDetail, ipVerifyCard,
    ipLeakBanner, ipLeakText, ipOkBanner, ipOkText, ipOkTunnelIp, ipOkIspIp,
    pingVal, pingSub, lossVal, lossSub, downVal, upVal,
    downGauge, upGauge, systemProxyToggleBtn, systemProxyDot,
    systemProxyLabel, systemProxyAddress, systemProxyModeSelect,
    proxyTestBtn, proxyTestIcon, proxyTestLabel,
    quickProtocolSelect, quickAutoReconnect, protocolHint,
    routeHint, transportHint, connectionStatusLine
  };
})();
