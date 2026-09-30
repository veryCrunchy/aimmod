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
  for(const file of ['statistics.js','run-details.js','benchmarks.js','leaderboard.js','mechanics.js','overlay-editor.js','overlay.js','history-import.js','settings.js','../replay/browser.js']){
    assert.doesNotMatch(fs.readFileSync(path.join(__dirname,file),'utf8'),/toLocale(String|DateString|TimeString)\(|Intl\./,file);
  }
});
test('pages never send the capability path as a referrer',()=>{
  for(const file of ['index.html','overlay.html'])assert.match(fs.readFileSync(path.join(__dirname,file),'utf8'),/<meta name="referrer" content="no-referrer">/,file);
});
