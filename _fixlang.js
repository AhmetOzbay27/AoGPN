const fs = require('fs');
const f = 'AoGPN/vpn-gpn-dashboard.html';
let h = fs.readFileSync(f, 'utf8');
// Load the English dictionary at startup so t() never returns raw keys, even in
// standalone previews or before the host pushes the saved language.
const anchor = '  renderTopThemePopover();\n  loadThemesFromDisk();';
if (!h.includes(anchor)) { console.error('ANCHOR NOT FOUND'); process.exit(1); }
h = h.replace(anchor, '  applyLanguage(\'en\'); // populate the dictionary before the host pushes the saved language\n  renderTopThemePopover();\n  loadThemesFromDisk();');
fs.writeFileSync(f, h);
console.log('startup language load added');
