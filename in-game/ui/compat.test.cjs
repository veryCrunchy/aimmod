const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const {loadFormat}=require('./test-format.cjs');
const context=vm.createContext({window:{}});
const F=loadFormat(context);
test('Gameface formatting works without Intl options',()=>{
  assert.equal(F.number(98.123456789),'98.1');
  assert.equal(F.percent(83.876543210),'83.9%');
  assert.equal(F.number(987.42600000000675,0),'987');
  assert.equal(F.number(12345),'12,345');
  assert.equal(F.number(-1234.567),'-1,234.6');
  assert.equal(F.number(1234567.891,2),'1,234,567.89');
  assert.equal(F.fixed(1,2),'1.00');
});
test('unknown metrics stay unknown and legitimate zero stays zero',()=>{
  for(const value of [null,undefined,NaN,Infinity,'12'])assert.equal(F.number(value),'—');
  for(const value of [null,undefined,NaN])assert.equal(F.percent(value),'—');
  assert.equal(F.number(0),'0');assert.equal(F.percent(0),'0%');
  assert.equal(F.unit(null,' ms'),'—');assert.equal(F.unit(0,' ms'),'0 ms');
});
test('rounding never produces negative zero or a sign on zero change',()=>{
  assert.equal(F.number(-0.04),'0');assert.equal(F.number(-0.0001,2),'0');
  assert.equal(F.signed(0.01),'0');assert.equal(F.signed(2.5),'+2.5');assert.equal(F.signed(-2.5),'-2.5');assert.equal(F.signed(null),'—');
});
test('legacy local timestamps have predictable compact dates',()=>{
  assert.equal(F.date('2026.06.03-12.30.00'),'3 Jun 2026');
  assert.equal(F.dateTime('2026.06.03-09.05.00'),'3 Jun 2026, 09:05');
  assert.equal(F.date('2026-01-01'),'1 Jan 2026');
  assert.equal(F.date('invalid'),'invalid');
  assert.equal(F.date(undefined),'—');assert.equal(F.date(''),'—');assert.equal(F.shortDate(null),'—');
});
test('durations and intervals use readable units',()=>{
  assert.equal(F.duration(59.6),'60s');assert.equal(F.duration(45),'45s');assert.equal(F.duration(119),'119s');
  assert.equal(F.hours(0.5),'30 min');assert.equal(F.hours(5.73),'5.7h');assert.equal(F.hours(22.6),'23h');assert.equal(F.hours(null),'—');assert.equal(F.duration(125),'2m 05s');assert.equal(F.duration(3720),'1h 02m');assert.equal(F.duration(null),'—');
  assert.equal(F.millis(850),'850 ms');assert.equal(F.millis(1234),'1.23 s');assert.equal(F.millis(20340),'20.3 s');assert.equal(F.millis(undefined),'—');
});
test('axis ticks stay distinct for small ranges and round for large ones',()=>{
  const small=F.ticks(0.11,0.19,4);const labels=small.values.map(v=>F.tick(v,small));
  assert.equal(new Set(labels).size,labels.length);assert.ok(small.min<=0.11&&small.max>=0.19);
  const large=F.ticks(812,1377,4);assert.ok(large.values.every(v=>v%100===0||v%50===0));assert.ok(large.values.length>=3&&large.values.length<=8);
  const flat=F.ticks(5,5,4);assert.ok(flat.max>flat.min);
  const quarter=F.ticks(0,10,4);assert.deepEqual(Array.from(quarter.values,v=>F.tick(v,quarter)),['0','2.5','5','7.5','10']);
});
test('recent activity uses relative labels without Intl',()=>{
  const now=new Date(2026,8,30,18,0,0).getTime();
  assert.equal(F.relative(new Date(2026,8,30,17,59,40),now),'Just now');
  assert.equal(F.relative(new Date(2026,8,30,17,15,0),now),'45 min ago');
  assert.equal(F.relative(new Date(2026,8,30,9,0,0),now),'9h ago');
  assert.equal(F.relative(new Date(2026,8,29,21,5,0),now),'Yesterday, 21:05');
  assert.equal(F.relative(new Date(2026,8,26,12,0,0),now),'4 days ago');
  assert.equal(F.relative(new Date(2026,7,2,12,0,0),now),'2 Aug');
  assert.equal(F.relative(new Date(2025,7,2,12,0,0),now),'2 Aug 2025');
  assert.equal(F.relative('bad',now),'—');
  assert.equal(F.weekday(new Date(2026,8,30)),'Wed');assert.equal(F.dayKey(new Date(2026,0,5)),'2026-01-05');
});
test('rolling average band waits for a full window and ignores missing values',()=>{
  const out=F.rolling([1,2,3,4,5,null,7],3);
  assert.equal(out[0],null);assert.equal(out[1],null);assert.equal(out[2].mean,2);assert.ok(Math.abs(out[2].sd-Math.sqrt(2/3))<1e-9);
  assert.equal(out[4].mean,4);assert.equal(out[5],null);assert.equal(out[6],null);
  assert.equal(F.trend(3),'up');assert.equal(F.trend(-3),'down');assert.equal(F.trend(0.1),'flat');assert.equal(F.trend(null),'flat');
});
test('workspace sources never build markup from strings',()=>{
  const dir=__dirname,files=fs.readdirSync(dir).filter(f=>/\.(js|html|ts)$/.test(f)&&f!=='coaching.js');
  const replay=path.join(dir,'..','replay');files.push(...fs.readdirSync(replay).filter(f=>f.endsWith('.js')).map(f=>path.join('..','replay',f)),path.join('..','aimmod-panel.js'));
  for(const file of files){const text=fs.readFileSync(path.join(dir,file),'utf8');assert.doesNotMatch(text,/innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\(|new Function\(/,file);}
  assert.doesNotMatch(fs.readFileSync(path.join(dir,'coaching.js'),'utf8'),/\.innerHTML\s*=|insertAdjacentHTML/);
});
test('workspace code avoids Intl-dependent formatting',()=>{
  for(const file of ['statistics.js','run-details.js','benchmarks.js','leaderboard.js','mechanics.js','overlay-editor.js','overlay.js','overlay-model.js','overlay-widgets.js','history-import.js','settings.js','hub-sharing.js','../replay/browser.js']){
    assert.doesNotMatch(fs.readFileSync(path.join(__dirname,file),'utf8'),/toLocale(String|DateString|TimeString)\(|Intl\./,file);
  }
});
test('styles and scripts stay inside the Gameface feature set',()=>{
  const dir=__dirname,css=fs.readdirSync(dir).filter(f=>f.endsWith('.css'));
  const inline=fs.readFileSync(path.join(dir,'index.html'),'utf8').match(/<style>([\s\S]*?)<\/style>/)[1].replace(/\/\*[\s\S]*?\*\//g,'');
  for(const [name,text] of [...css.map(f=>[f,fs.readFileSync(path.join(dir,f),'utf8').replace(/\/\*[\s\S]*?\*\//g,'')]),['index.html <style>',inline]]){
    assert.doesNotMatch(text,/display:\s*(inline-)?grid|(^|[;{\s])(row-|column-)?gap\s*:|position:\s*sticky|var\(--|calc\(|@supports/,name);
  }
  const scripts=fs.readdirSync(dir).filter(f=>f.endsWith('.js')&&f!=='coaching.js').map(f=>path.join(dir,f)).concat(['browser.js','native-browser.js'].map(f=>path.join(dir,'..','replay',f)));
  for(const file of scripts){const text=fs.readFileSync(file,'utf8');
    assert.doesNotMatch(text,/\bfetch\(|AbortController|requestAnimationFrame|IntersectionObserver|ResizeObserver|\bPromise\b|=>/,file);
    // clientWidth/clientHeight are missing in Gameface; only allowed as a fallback after offsetWidth.
    for(const m of text.matchAll(/clientWidth|clientHeight/g)){const before=text.slice(Math.max(0,m.index-40),m.index);assert.match(before,/offsetWidth\|\|[\w.]*$/,file);}
  }
});
// Rules below come from live Gameface rendering in KovaaK's 3.9.11.
const uiDir=__dirname;
function uiCss(){const out=fs.readdirSync(uiDir).filter(f=>f.endsWith('.css')).map(f=>[f,fs.readFileSync(path.join(uiDir,f),'utf8')]);out.push(['index.html <style>',fs.readFileSync(path.join(uiDir,'index.html'),'utf8').match(/<style>([\s\S]*?)<\/style>/)[1]]);return out.map(([n,t])=>[n,t.replace(/\/\*[\s\S]*?\*\//g,'')]);}
function uiScripts(){return fs.readdirSync(uiDir).filter(f=>/\.(js|ts)$/.test(f)&&f!=='coaching.js').map(f=>[f,fs.readFileSync(path.join(uiDir,f),'utf8')]).concat(['browser.js','native-browser.js'].map(f=>['replay/'+f,fs.readFileSync(path.join(uiDir,'..','replay',f),'utf8')]),[['index.html',fs.readFileSync(path.join(uiDir,'index.html'),'utf8')]]);}
test('Gameface: no inline-block or inline-flex (they lay out as blocks and stack controls)',()=>{
  for(const [name,text] of uiCss())assert.doesNotMatch(text,/display:\s*inline/,name);
  for(const [name,text] of uiScripts())assert.doesNotMatch(text,/display\s*=\s*['"]inline|display:\s*inline/,name);
});
test('Gameface: no inherit keyword (buttons fell back to a dark default colour)',()=>{
  for(const [name,text] of uiCss())assert.doesNotMatch(text,/:\s*inherit\b|font:\s*inherit/,name);
  assert.match(uiCss().find(([n])=>n==='index.html <style>')[1],/button\{[^}]*color:#eef5f1/);
});
test('Gameface: canvas text uses the loaded Roboto face, never a fallback list',()=>{
  for(const [name,text] of uiScripts())for(const m of text.matchAll(/\.font\s*=\s*'([^']*)'/g))assert.match(m[1],/^(bold |600 )?\d+px Roboto$/,name+': '+m[1]);
});
test('Gameface: canvas colours are solid, not rgba',()=>{
  for(const [name,text] of uiScripts())assert.doesNotMatch(text,/(fillStyle|strokeStyle)\s*=\s*['"]rgba|['"]rgba\(/,name);
});
test('Gameface: only glyphs the game font covers (no ▲ ▼ ★ › ▾ arrows)',()=>{
  const ok=ch=>{const n=ch.codePointAt(0);return n<0x80||(n>=0xA0&&n<=0xFF)||[0x2013,0x2014,0x2018,0x2019,0x201C,0x201D,0x2026].includes(n);};
  for(const [name,text] of uiScripts().concat(uiCss())){const bad=[...new Set([...text].filter(ch=>!ok(ch)))];assert.deepEqual(bad,[],name);}
});
test('Gameface: inputs never rely on placeholder text, and inline b/i elements are not used',()=>{
  for(const [name,text] of uiScripts()){const code=text.replace(/\/\/.*$/gm,'').replace(/\/\*[\s\S]*?\*\//g,'');assert.doesNotMatch(code,/placeholder/,name);assert.doesNotMatch(code,/createElement\(['"](b|i|em)['"]\)|node\([^,]*,\s*['"](b|i|em)['"]|<(b|i|em)>|<i /,name);}
});
test('field() hint replaces the placeholder and hides once the input has a value',()=>{
  const c=vm.createContext({});class El{constructor(t){this.tag=t;this.children=[];this.style={};this.attrs={};this.value='';this.listeners={};}appendChild(x){this.children.push(x);return x;}setAttribute(k,v){this.attrs[k]=v;}getAttribute(k){return this.attrs[k];}addEventListener(e,f){(this.listeners[e]=this.listeners[e]||[]).push(f);}}
  c.window={document:{createElement:t=>new El(t)}};const G=loadFormat(c);const input=new El('input');const box=G.field(input,'Find a scenario');
  const hint=box.children[1];assert.equal(hint.textContent,'Find a scenario');assert.equal(hint.style.display,'block');assert.equal(input.attrs['aria-label'],'Find a scenario');
  input.value='abc';input.listeners.input.forEach(f=>f());assert.equal(hint.style.display,'none');input.value='';input.syncHint();assert.equal(hint.style.display,'block');
});
test('text in scripts the font lacks gets a readable fallback',()=>{
  assert.equal(F.safeText('Voltaic','x'),'Voltaic');assert.equal(F.safeText('Ренат','x'),'Ренат');assert.equal(F.safeText('Ώρα','x'),'Ώρα');assert.equal(F.safeText('Café – S5','x'),'Café – S5');
  assert.equal(F.safeText('小明的基准','Benchmark 3'),'Benchmark 3');assert.equal(F.safeText('   ','fallback'),'fallback');assert.equal(F.safeText(null),'');
});
test('count axes use whole-number ticks only',()=>{
  for(const max of [1,2,3,5,7,13,48]){const axis=F.countTicks(max,4);assert.ok(axis.values.every(Number.isInteger),String(max));assert.ok(axis.max>=max);assert.equal(new Set(axis.values).size,axis.values.length);}
});
test('pages never send the capability path as a referrer',()=>{
  for(const file of ['index.html','overlay.html'])assert.match(fs.readFileSync(path.join(__dirname,file),'utf8'),/<meta name="referrer" content="no-referrer">/,file);
});
test('workspace problems use the warning notice and the account needs confirming before unlink',()=>{
  const html=fs.readFileSync(path.join(__dirname,'index.html'),'utf8');
  assert.match(html,/\.notice\.warn\{/,'warning notice style exists');
  assert.match(html,/el\('notice'\)\.className='notice warn'/,'the offline notice uses the warning style');
  const unlinks=html.match(/command\('unlink'\)/g)||[];assert.equal(unlinks.length,1,'one unlink command');
  assert.match(html,/button\('Unlink',function\(\)\{confirmUnlink=false;clearTimeout\(confirmTimer\);command\('unlink'\)/,'unlink only runs from the confirmation');
  assert.match(html,/id="account-shortcut" type="button" style="display:none"/,'the account button waits for data');
});
