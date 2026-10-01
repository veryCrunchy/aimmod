const {test}=require('node:test');const assert=require('node:assert/strict');
const {makeWindow,load}=require('./overlay-test-dom.cjs');
function page(pathname,search,extra){
  const w=makeWindow(extra);w.window.location={pathname,search};const host=w.document.createElement('main');host.id='scene';w.document.roots.push(host);
  const events={};w.window.engine={on:(n,f)=>events[n]=f};load(w.window,['overlay-model.js','overlay-widgets.js','overlay.js']);
  const M=w.window.AimModOverlayModel;return {...w,host,events,M};
}
function store(M){
  const game=M.cleanScene({id:'game',name:'Game',widgets:[M.widget('live-stats','stats',10,20),M.widget('clock','clock',500,20,{show:{menu:true,scenario:false,match:false}})]});
  const obs=M.cleanScene({id:'stream',name:'Stream',widgets:[M.widget('settings','set',100,100),M.widget('live-stats','live',600,900),M.widget('mouse-path','path',1500,600)]});
  return M.normalize({obsScene:'stream',gameScene:'game',scenes:[game,obs],profile:{peripherals:[{label:'Mouse',value:'Example'}]}});
}
const live={available:true,active:true,paused:false,scenario:'Synthetic run',score:100,seconds:20,accuracy:50,hits:10,shots:20};
const feed=(extra)=>Object.assign({v:1,enabled:true,storeRevision:7,live,kovaaks:{available:true,dpi:800}},extra);
const kinds=host=>host.children.map(c=>c.className.split(' ')[1]);

test('the in-game view draws the game scene and polls the feed fast while a run is live',()=>{
  const s=page('/cap/overlay','?surface=game');assert.equal(s.requests[0].url,'/cap/overlay-feed?surface=game');
  s.requests[0].finish(200,feed());assert.equal(s.requests[1].url,'/cap/overlay-scenes');s.requests[1].finish(200,store(s.M));
  assert.equal(s.host.className,'');assert.deepEqual(kinds(s.host),['amw-live-stats'],'the menu-only clock is hidden during a run');
  assert.equal(s.host.children[0].style.left,'10px');assert.equal(s.timers.at(-1).delay,100);
  s.run();s.requests[2].finish(200,feed({live:{available:true,active:false}}));assert.equal(s.requests.length,3,'same revision: no scene reload');
  assert.deepEqual(kinds(s.host),['amw-clock'],'in the menu the clock shows instead');assert.equal(s.timers.at(-1).delay,500);
});
test('a switched-off surface draws nothing and keeps polling slowly',()=>{
  const s=page('/cap/overlay','?surface=game');s.requests[0].finish(200,{v:1,enabled:false,storeRevision:7});s.requests[1].finish(200,store(s.M));
  assert.equal(s.host.className,'hidden');s.run();s.requests[2].finish(500,'');assert.equal(s.host.className,'hidden');assert.equal(s.timers.at(-1).delay,1000);
});
test('OBS scene sources pick their scene, the earlier link shows the default OBS scene, and scenes reload on change',()=>{
  const a=page('/obs/scene','?id=game&w=1920&h=1080');assert.equal(a.requests[0].url,'/obs/overlay-feed?surface=obs');a.requests[0].finish(200,feed());a.requests[1].finish(200,store(a.M));assert.deepEqual(kinds(a.host),['amw-live-stats']);
  const b=page('/obs/overlay','?surface=obs');b.requests[0].finish(200,feed());b.requests[1].finish(200,store(b.M));assert.deepEqual(kinds(b.host),['amw-settings','amw-live-stats','amw-mouse-path']);
  assert.match(b.host.textContent,/800/);
  b.run();const next=b.requests.find(r=>!r.status&&r.url.includes('overlay-feed'));next.finish(200,feed({storeRevision:8}));
  const reload=b.requests.at(-1);assert.equal(reload.url,'/obs/overlay-scenes');const changed=store(b.M);changed.scenes[1].widgets=changed.scenes[1].widgets.slice(0,1);reload.finish(200,changed);assert.deepEqual(kinds(b.host),['amw-settings']);
  const missing=page('/obs/scene','?id=nope');missing.requests[0].finish(200,feed());missing.requests[1].finish(200,store(missing.M));assert.equal(missing.host.className,'hidden','an unknown scene id shows nothing');
});
test('a widget source renders one widget at the origin, scaled to the source width',()=>{
  const s=page('/obs/scene','?id=stream&widget=set',{innerWidth:920,innerHeight:600});s.requests[0].finish(200,feed());s.requests[1].finish(200,store(s.M));
  assert.deepEqual(kinds(s.host),['amw-settings']);assert.equal(s.host.children[0].style.left,'0px');assert.equal(s.host.style.width,'460px');assert.equal(s.host.style.transform,'scale(2.00000)');
});
test('motion is requested only while a path or input widget shows',()=>{
  const s=page('/obs/scene','?id=stream');s.requests[0].finish(200,feed());s.requests[1].finish(200,store(s.M));
  const timer=s.timers.find(t=>t.delay===33);assert.ok(timer);timer.fn();const m=s.requests.at(-1);assert.equal(m.url,'/obs/overlay-motion?surface=obs&path=1');
  m.finish(200,{path:[[0,0,0],[100,3,1],[200,6,2]],input:null});const canvas=s.host.find(n=>n.tagName==='CANVAS');assert.ok(canvas.ctx.calls.some(c=>c[0]==='stroke'));
  const g=page('/cap/overlay','?surface=game');g.requests[0].finish(200,feed());g.requests[1].finish(200,store(g.M));assert.ok(!g.timers.some(t=>t.delay===33),'no path widget, no motion polling');
});
test('native visibility false aborts polling and ignores late responses',()=>{
  const s=page('/cap/overlay','?surface=game');const first=s.requests[0];s.events.AimModVisibility(false);assert.ok(first.aborted);first.finish(200,feed());assert.equal(s.requests.length,1);assert.equal(s.host.className,'hidden');
  s.events.AimModVisibility(true);assert.equal(s.requests.length,2);
});
test('the page never posts, sends credentials or writes to the game',()=>{
  const src=require('node:fs').readFileSync(require('node:path').join(__dirname,'overlay.js'),'utf8');
  assert.doesNotMatch(src,/POST|setRequestHeader|withCredentials|localStorage|engine\.call|engine\.trigger/);
});
