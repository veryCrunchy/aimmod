const {test}=require('node:test');const assert=require('node:assert/strict');
const deq=(a,b,m)=>assert.deepStrictEqual(JSON.parse(JSON.stringify(a)),JSON.parse(JSON.stringify(b)),m);
const {makeWindow,load}=require('./overlay-test-dom.cjs');
const NOW=new Date(2026,9,1,20,41,7).getTime();
function setup(){const w=makeWindow();load(w.window,['overlay-model.js','overlay-widgets.js']);const host=w.document.createElement('div');w.document.roots.push(host);return {...w,M:w.window.AimModOverlayModel,W:w.window.AimModOverlayWidgets,host};}
function scene(M,widgets,theme){return M.cleanScene({id:'s',name:'S',theme:theme||{preset:'mint'},widgets});}
const text=n=>n.textContent;

test('every widget renders sample data and empty data without errors or placeholder leaks',()=>{
  const {M,W,host,document}=setup();
  for(const e of M.CATALOG){
    const sc=scene(M,[M.widget(e.type,'a',10,10)]);
    for(const data of [W.sample(NOW),{},{live:{available:false,active:false}}]){
      host.textContent='';const n=W.render(host,sc,data,{all:true,now:NOW,motion:W.sampleMotion(NOW),document});
      assert.equal(n,1,e.type);const t=text(host);assert.doesNotMatch(t,/NaN|undefined|null|Infinity|(^|[^0-9.,])-0(?![.0-9])/,e.type+': '+t);
    }
  }
});
test('updates patch the same nodes in place and only change text',()=>{
  const {M,W,host,document}=setup();const sc=scene(M,[M.widget('live-stats','a',0,0)]);const d=W.sample(NOW);
  W.render(host,sc,d,{all:true,now:NOW,document});const card=host.children[0];const nodes=card.all();
  d.live.score=123456;W.render(host,sc,d,{all:true,now:NOW,document});
  assert.equal(host.children[0],card);deq(card.all().length,nodes.length);assert.ok(card.all().every((n,i)=>n===nodes[i]),'no node was replaced');
  assert.match(text(card),/123,456/);
  sc.widgets[0].opts.m_kills=false;W.render(host,sc,d,{all:true,now:NOW,document});assert.doesNotMatch(text(host.children[0]),/Kills/);
});
test('the settings card shows DPI, cm/360, FOV and the crosshair, theme and hit sounds, each optional',()=>{
  const {M,W,host,document}=setup();const d=W.sample(NOW);d.kovaaks={available:true,dpi:1600,sens:3.2,sensScale:'Quake/Source',cm360:31.17,fov:103,fovScale:'Overwatch',theme:'Synthetic Theme',crosshair:'synthetic_cross.png',crosshairScale:1.5,crosshairColor:'#ff00ff',hitSounds:['hit-a','hit-b']};
  const sc=scene(M,[M.widget('settings','a',0,0)]);const w=sc.widgets[0];W.render(host,sc,d,{all:true,document});const t=text(host);
  for(const part of ['DPI','1,600','31.17','cm/360','FOV · Overwatch','103°','synthetic_cross · 1.5×','Synthetic Theme','Hit sounds','hit-a / hit-b','In-game sens','3.20 Quake/Source'])assert.ok(t.includes(part),part+' in '+t);
  w.opts.dpi=false;w.opts.sounds=false;w.opts.theme=false;W.render(host,sc,d,{all:true,document});const u=text(host);assert.ok(!u.includes('1,600')&&!u.includes('hit-a')&&!u.includes('Synthetic Theme'));
  d.kovaaks={available:false};W.render(host,sc,d,{all:true,document});assert.match(text(host),/settings appear here/);
  d.kovaaks={available:true,dpi:800,sens:50,sensScale:'cm/360',cm360:50,fov:90.5};w.opts.dpi=true;W.render(host,sc,d,{all:true,document});assert.match(text(host),/50\.00cm\/360/);assert.match(text(host),/90\.5°/);
});
test('live stats keep unknown values unknown and legitimate zero as zero',()=>{
  const {M,W,host,document}=setup();const sc=scene(M,[M.widget('live-stats','a',0,0,{opts:{m_ttk:true,m_damage:true}})]);
  W.render(host,sc,{live:{active:true,score:0,accuracy:null,kills:0,remainingSeconds:-0.0,lastTimeToKillSeconds:null,damage:-0.0001}},{all:true,document});
  const t=text(host);assert.match(t,/Score0/);assert.match(t,/Kills0/);assert.match(t,/Accuracy—/);assert.match(t,/Last TTK—/);assert.match(t,/Damage0/);assert.doesNotMatch(t,/-0/);
});
test('PB pace shows projection, ahead or behind, and the opponent name',()=>{
  const {M,W,host,document}=setup();const sc=scene(M,[M.widget('pb-pace','a',0,0)]);
  W.render(host,sc,{live:{active:true,score:500,opponentScore:1000,opponentName:'Example rival',projectedScore:900,projectedDelta:-100}},{all:true,document});
  const t=text(host);assert.match(t,/VS Example rival/);assert.match(t,/-100 behind/);assert.match(t,/900/);
  const fill=host.find(n=>n.className==='amw-bar-fill');assert.equal(fill.style.width,'50.0%');
});
test('theme, font scale, opacity and background reach the widget frame',()=>{
  const {M,W,host,document}=setup();
  const sc=scene(M,[M.widget('text','a',100,200,{w:300,font:1.5,opacity:0.5,accent:'#ff6680',opts:{text:'Hello'}})],{preset:'contrast'});const w=sc.widgets[0];
  W.render(host,sc,{},{all:true,document});const f=host.children[0];
  assert.equal(f.style.left,'100px');assert.equal(f.style.top,'200px');assert.equal(f.style.width,'300px');assert.equal(f.style.fontSize,'24.00px');assert.equal(f.style.opacity,'0.5');
  assert.equal(f.style.color,'#ffffff');assert.match(f.style.background,/^rgba\(0,0,0,1\)$/);assert.match(f.style.border,/255,255,255/);
  w.background='none';W.render(host,sc,{},{all:true,document});assert.equal(host.children[0].style.background,'transparent');
  w.background='#102030';w.panel=0.5;W.render(host,sc,{},{all:true,document});assert.equal(host.children[0].style.background,'rgba(16,32,48,0.5)');
});
test('only widgets shown in the current context render, and a single widget can render alone at the origin',()=>{
  const {M,W,host,document}=setup();const sc=scene(M,[M.widget('live-stats','a',300,300),M.widget('clock','b',500,500),M.widget('text','c',10,10,{visible:false})]);
  assert.equal(W.render(host,sc,{},{context:'menu',document}),1);assert.match(host.children[0].className,/amw-clock/);
  assert.equal(W.render(host,sc,{live:{active:true}},{context:'scenario',document}),2);
  assert.equal(W.render(host,sc,{},{context:'menu',only:'a',document}),0,'a hidden-in-context widget stays hidden alone');
  assert.equal(W.render(host,sc,{},{context:'menu',only:'b',document}),1);assert.equal(host.children[0].style.left,'0px');assert.equal(host.children[0].style.top,'0px');
  deq(W.needsMotion(M.cleanScene({id:'x',widgets:[M.widget('mouse-path','p',0,0),M.widget('input','i',0,0,{visible:false})]}),'scenario'),{path:true,input:false});
  deq(W.needsMotion(M.cleanScene({id:'x',widgets:[M.widget('mouse-path','p',0,0)]}),'menu'),{path:false,input:false});
});
test('mouse path unwraps yaw across 360, keeps the chosen length and smooths',()=>{
  const {W}=setup();
  const path=[[0,359,0],[100,1,1],[200,3,2],[300,5,3],[2000,7,4]];
  const pts=W.pathPoints(path,{length:1,smoothing:0});deq(pts.map(p=>p[0]),[2000]);
  const all=W.pathPoints(path,{length:3,smoothing:0});deq(all.map(p=>Math.round(p[1])),[0,2,4,6,8]);
  const sm=W.pathPoints([[0,0,0],[10,10,0],[20,0,0],[30,10,0],[40,0,0]],{length:3,smoothing:1});assert.ok(Math.abs(sm[2][1]-20/3)<1e-9);deq(sm[4],[40,0,0],'the newest point stays exact');
});
test('the path canvas fades older segments and the input display lights pressed keys',()=>{
  const {M,W,host,document}=setup();const sc=scene(M,[M.widget('mouse-path','p',0,0),M.widget('input','i',400,0,{opts:{keys:'all'}})]);
  const motion={path:[[0,0,0],[100,2,1],[200,4,2],[300,6,1]],input:{available:true,w:true,lmb:true}};
  W.render(host,sc,{},{all:true,motion,document});const canvas=host.find(n=>n.tagName==='CANVAS');const strokes=canvas.ctx.calls.filter(c=>c[0]==='stroke');assert.ok(strokes.length>=3);
  const keys=host.findAll(n=>/^amw-key( |$)/.test(n.className));const w=keys.find(k=>k.textContent==='W'),a=keys.find(k=>k.textContent==='A'),l=keys.find(k=>k.textContent==='LMB');
  assert.equal(w.style.background,'#27e4a1');assert.equal(l.style.background,'#27e4a1');assert.notEqual(a.style.background,'#27e4a1');
});
test('recent runs, session and scenario widgets show their numbers',()=>{
  const {M,W,host,document}=setup();const d=W.sample(NOW);
  W.render(host,scene(M,[M.widget('recent','r',0,0,{opts:{count:3}})]),d,{all:true,now:NOW,document});const r=text(host);assert.match(r,/Synthetic Flick Wall/);assert.match(r,/PB/);assert.match(r,/2m ago/);assert.equal(host.findAll(n=>n.className==='amw-run').length,3);
  W.render(host,scene(M,[M.widget('session','s',0,0)]),d,{all:true,now:NOW,document});assert.match(text(host),/Runs23/);assert.match(text(host),/1h 02m/);
  W.render(host,scene(M,[M.widget('scenario','s',0,0)]),d,{all:true,now:NOW,document});assert.match(text(host),/2,410/);assert.match(text(host),/214/);
  W.render(host,scene(M,[M.widget('peripherals','p',0,0)]),{peripherals:[{label:'Mouse',value:'Example Mouse'}]},{all:true,document});assert.match(text(host),/MouseExample Mouse/);
  W.render(host,scene(M,[M.widget('image','i',0,0,{opts:{url:'https://example.com/logo.png'}})]),{},{all:true,document});assert.equal(host.find(n=>n.className==='amw-image').style.backgroundImage,'url(https://example.com/logo.png)');
});
test('formatting groups digits without Intl and never shows negative zero',()=>{
  const {W}=setup();const F=W.format;assert.equal(F.number(1234567.891,2),'1,234,567.89');assert.equal(F.number(-0.0001,2),'0.00');assert.equal(F.number(null),'—');assert.equal(F.signed(2.5,1),'+2.5');assert.equal(F.signed(0,1),'0.0');
  assert.equal(F.clock(75.9),'1:15');assert.equal(F.span(3720),'1h 02m');assert.equal(F.span(59),'59s');assert.equal(F.span(600),'10m');assert.equal(F.millis(284),'284 ms');assert.equal(F.safe('a\u0001b\u0085c',10),'abc');assert.equal(F.safe('abcdefghijk',5),'abcd…');
});
