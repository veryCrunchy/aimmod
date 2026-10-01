#!/usr/bin/env node
// Rasterises the mode icons (ui/art/modes/*.svg, 48 x 48 line icons) to PNG with headless
// Chrome: normal and selected, at 2x and 3x. Output: ui/art/modes/png/<id>[-on]@<n>x.png.
//
//   node tools/mode-icons/build.cjs            (Chrome from CHROME_PATH or the usual install)
//
// The SVGs draw with currentColor. Elements with class "soft" are the secondary tone and
// class "team" takes the mode's team colour (team modes only). Needs Node 22 (WebSocket).
'use strict';
const fs = require('fs'), os = require('os'), path = require('path'), cp = require('child_process');

const root = path.resolve(__dirname, '..', '..');
const src = path.join(root, 'ui', 'art', 'modes');
const out = path.join(src, 'png');
const SIZE = 48;
const SCALES = [2, 3];
// Muted green-grey normally, mint when selected. Team colours: the second team in TDM,
// the Terrorist timer in CS.
const STATES = {
  normal: { ink: '#7f968a', soft: 0.55, team: { 'team-deathmatch': '#9a8489', cs: '#a08a66' } },
  on: { ink: '#27e4a1', soft: 0.5, team: { 'team-deathmatch': '#ff8fa3', cs: '#f0b45a' } },
};

function chrome() {
  const pf = process.env.PROGRAMFILES, pf86 = process.env['PROGRAMFILES(X86)'], local = process.env.LOCALAPPDATA;
  const list = [process.env.CHROME_PATH,
    pf && path.join(pf, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    pf86 && path.join(pf86, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    local && path.join(local, 'Google', 'Chrome', 'Application', 'chrome.exe'),
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser'];
  const hit = list.find(p => p && fs.existsSync(p));
  if (!hit) throw new Error('Chrome not found; set CHROME_PATH');
  return hit;
}

// One icon on a transparent page exactly SIZE css px square.
function page(svg, ink, soft, team) {
  return '<!doctype html><html><head><meta charset="utf-8"><style>' +
    'html,body{margin:0;padding:0;background:transparent;overflow:hidden}' +
    'body{width:' + SIZE + 'px;height:' + SIZE + 'px;color:' + ink + '}' +
    'svg{display:block}.soft{opacity:' + soft + '}' + (team ? '.team{color:' + team + '}' : '') +
    '</style></head><body>' + svg + '</body></html>';
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

// One headless Chrome, driven over the DevTools protocol.
async function browser(exe) {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'mode-icon-'));
  const profile = path.join(tmp, 'profile');
  const proc = cp.spawn(exe, ['--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
    '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'], { stdio: 'ignore' });
  const portFile = path.join(profile, 'DevToolsActivePort');
  let port = '';
  for (let i = 0; i < 300 && !port; i++) {
    try { port = fs.readFileSync(portFile, 'utf8').split('\n')[0].trim(); } catch (e) { /* not yet */ }
    if (!port) await sleep(50);
  }
  if (!port) { proc.kill(); throw new Error('Chrome did not start'); }
  const targets = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
  const ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
  await new Promise((ok, bad) => { ws.onopen = ok; ws.onerror = bad; });
  let seq = 0;
  const wait = new Map();
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (!m.id || !wait.has(m.id)) return;
    const w = wait.get(m.id); wait.delete(m.id);
    if (m.error) w[1](new Error(m.error.message)); else w[0](m.result);
  };
  const send = (method, params) => new Promise((ok, bad) => {
    const id = ++seq; wait.set(id, [ok, bad]); ws.send(JSON.stringify({ id, method, params: params || {} }));
  });
  await send('Page.enable');
  await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } });
  const frameId = (await send('Page.getFrameTree')).frameTree.frame.id;
  return {
    async shoot(html, file, scale, width, height) {
      width = width || SIZE; height = height || SIZE;
      await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: scale, mobile: false });
      await send('Page.setDocumentContent', { frameId, html });
      await sleep(30);
      const shot = await send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 0, width, height, scale: 1 } });
      fs.writeFileSync(file, Buffer.from(shot.data, 'base64'));
    },
    async close() {
      const exited = new Promise(r => { proc.once('exit', r); setTimeout(r, 5000); });
      try { await send('Browser.close'); } catch (e) { proc.kill(); }
      await exited;
      try { ws.close(); } catch (e) { /* closed */ }
      // Chrome's helpers can hold the profile a moment longer; the temp folder isn't worth failing over.
      try { fs.rmSync(tmp, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 }); } catch (e) { /* left in temp */ }
    },
  };
}

async function main() {
  const b = await browser(chrome());
  try {
    fs.mkdirSync(out, { recursive: true });
    const ids = fs.readdirSync(src).filter(f => f.endsWith('.svg')).map(f => f.slice(0, -4)).sort();
    for (const id of ids) {
      const svg = fs.readFileSync(path.join(src, id + '.svg'), 'utf8');
      for (const [state, s] of Object.entries(STATES))
        for (const scale of SCALES) {
          const name = id + (state === 'on' ? '-on' : '') + '@' + scale + 'x.png';
          await b.shoot(page(svg, s.ink, s.soft, s.team[id]), path.join(out, name), scale);
        }
    }
    console.log(ids.length + ' icons, ' + ids.length * 4 + ' PNGs in ' + path.relative(root, out));
  } finally { await b.close(); }
}

if (require.main === module) main().catch(e => { console.error(e.message); process.exit(1); });
module.exports = { page, browser, chrome, STATES, SIZE };
