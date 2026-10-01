const deq=(a,b,m)=>require('node:assert/strict').deepStrictEqual(JSON.parse(JSON.stringify(a)),JSON.parse(JSON.stringify(b)),m);
const {test}=require('node:test');const assert=require('node:assert/strict');
const {makeWindow,load}=require('./overlay-test-dom.cjs');
function model(){const w=makeWindow();load(w.window,['overlay-model.js','overlay-widgets.js']);return {M:w.window.AimModOverlayModel,W:w.window.AimModOverlayWidgets};}

test('every catalog widget has a renderer, a size and valid default options',()=>{
  const {M,W}=model();const types=new Set();
  for(const e of M.CATALOG){assert.ok(!types.has(e.type),e.type);types.add(e.type);assert.equal(typeof W.renderers[e.type],'function',e.type);assert.ok(e.w>=60&&e.h>=40,e.type);
    const w=M.widget(e.type,'w1',10,10);assert.ok(w,e.type);deq(w.opts,M.cleanWidget(w).opts,e.type);deq(w.show,e.show,e.type);}
  for(const t of ['live-stats','pb-pace','session','session-graph','scenario','rank','settings','peripherals','mouse-path','input','crosshair','recent','standings','bracket','profile','now-playing','clock','text','image'])assert.ok(types.has(t),t);
});
test('normalize clamps values, drops unknown widgets and fields, and keeps ids unique',()=>{
  const {M}=model();
  const raw={v:1,obsScene:'nope',gameScene:'b',evil:true,scenes:[
    {id:'a',name:'  A scene with a very long name that keeps going and going on  ',theme:{preset:'neon',accent:'red',alpha:4},widgets:[
      {id:'w1',type:'live-stats',x:-50,y:99999,w:5,h:5,opacity:9,font:0.1,variant:'tiny',layout:'diagonal',labels:'yes',accent:'#ABCDEF',background:'url(x)',panel:3,show:{menu:'x'},opts:{m_score:false,m_ttk:'no',bogus:1},extra:1},
      {id:'w1',type:'clock'},{id:'BAD ID',type:'clock'},{id:'w3',type:'flamethrower'},{id:'w4',type:'image',opts:{url:'javascript:alert(1)'}}]},
    {id:'a',name:'dupe'},{id:'b',name:'',widgets:'x'}],profile:{peripherals:[{label:'Mouse',value:'Example'},{label:'',value:''},{label:'x'.repeat(90),value:'y'.repeat(200)}],secret:'z'}};
  const s=M.normalize(raw);
  deq(Object.keys(s),['v','obsScene','gameScene','scenes','profile']);
  assert.equal(s.scenes.length,2);assert.equal(s.obsScene,'a');assert.equal(s.gameScene,'b');assert.equal(s.scenes[1].name,'Scene');assert.ok(s.scenes[0].name.length<=48&&s.scenes[0].name.charAt(0)==='A');
  deq(s.scenes[0].theme,{preset:'mint',accent:'#27e4a1',text:'#eef5f1',surface:'#0b1110',alpha:1,radius:10,borders:true});
  const w=s.scenes[0].widgets;deq(w.map(x=>x.id),['w1','w4']);
  deq({x:w[0].x,y:w[0].y,w:w[0].w,h:w[0].h,opacity:w[0].opacity,font:w[0].font},{x:0,y:1060,w:60,h:40,opacity:1,font:0.6});
  assert.equal(w[0].variant,'expanded');assert.equal(w[0].layout,'horizontal');assert.equal(w[0].labels,true);assert.equal(w[0].accent,'#abcdef');assert.equal(w[0].background,'');assert.equal(w[0].panel,1);
  deq(w[0].show,{menu:false,scenario:true,match:true});assert.equal(w[0].opts.m_score,false);assert.equal(w[0].opts.m_ttk,false);assert.ok(!('bogus' in w[0].opts));assert.ok(!('extra' in w[0]));
  assert.equal(w[1].opts.url,'','only https images');
  deq(s.profile.peripherals.map(p=>[p.label.length,p.value.length]),[[5,7],[32,80]]);
  deq(M.normalize(s),s,'normalize is idempotent');
  deq(M.normalize(null).scenes.map(x=>x.id),['game','stream','setup'],'nothing usable falls back to the default store');
});
test('scene and widget counts are bounded',()=>{
  const {M}=model();const widgets=[];for(let i=0;i<50;i++)widgets.push({id:'w'+i,type:'text'});const scenes=[];for(let i=0;i<20;i++)scenes.push({id:'s'+i,widgets});
  const s=M.normalize({scenes});assert.equal(s.scenes.length,M.MAX_SCENES);assert.equal(s.scenes[0].widgets.length,M.MAX_WIDGETS);
});
test('the default store keeps the earlier live stats and VS cards where they were',()=>{
  const {M}=model();
  const legacy={opacity:0.5,stats:{visible:true,x:1,y:100,width:560},versus:{visible:false,x:50,y:10,width:300},obs:{opacity:1,stats:{visible:true,x:0,y:0,width:200},versus:{visible:true,x:100,y:100,width:300}}};
  const s=M.defaultStore(legacy);assert.equal(s.gameScene,'game');assert.equal(s.obsScene,'stream');
  const game=M.find(s,'game');deq(game.widgets.map(w=>[w.type,w.x,w.y,w.w,w.visible]),[['live-stats',19,960,616,true],['pb-pace',960,108,375,false]]);
  assert.equal(game.theme.alpha,0.44);
  const obs=M.find(s,'stream');deq(obs.widgets.map(w=>[w.x,w.y,w.w]),[[0,0,220],[1545,930,375]]);
  assert.ok(M.find(s,'setup').widgets.some(w=>w.type==='settings'),'a settings card scene replaces the old settings overlay');
});
test('share codes round-trip a scene, keep unicode names and leave peripherals out',()=>{
  const {M}=model();const scene=M.fromTemplate('stream','s1','Ström · Café');scene.widgets[0].opts.scenario=false;
  const code=M.encodeShare(scene);assert.match(code,/^AIMMOD-OVERLAY-1:[A-Za-z0-9+/=]+$/);assert.doesNotMatch(code,/peripherals/);
  deq(M.decodeShare(code),M.cleanScene(scene));deq(M.decodeShare('  '+code+'\n'),M.cleanScene(scene));
  for(const bad of ['', 'hello', 'AIMMOD-OVERLAY-1:!!!!', 'AIMMOD-OVERLAY-1:'+Buffer.from('{"v":2,"scene":{}}').toString('base64'), 'AIMMOD-OVERLAY-1:'+Buffer.from('{"v":1,"scene":{"id":"Bad Id"}}').toString('base64'), null, 42])assert.equal(M.decodeShare(bad),null,String(bad));
  const shared=M.decodeShare('AIMMOD-OVERLAY-1:'+Buffer.from(JSON.stringify({v:1,scene:{id:'x',name:'N',widgets:[{id:'a',type:'image',opts:{url:'http://insecure.example/x.png'}}]}})).toString('base64'));
  assert.equal(shared.widgets[0].opts.url,'','imports are cleaned like everything else');
});
test('snapping uses the screen, other widgets and the 8 px grid, and stays on screen',()=>{
  const {M}=model();
  let r=M.snap({x:955,y:3,w:10,h:10},[]);assert.equal(r.x,955);assert.equal(r.y,0);assert.ok(r.guides.some(g=>g.axis==='y'&&g.at===0));
  r=M.snap({x:953,y:300,w:20,h:10},[]);assert.equal(r.x,950,'centre line');
  r=M.snap({x:507,y:203,w:100,h:50},[{x:100,y:100,w:400,h:100}]);assert.equal(r.x,500);assert.equal(r.y,200);
  r=M.snap({x:301,y:405,w:100,h:50},[]);assert.equal(r.x,304);assert.equal(r.y,408);
  r=M.snap({x:1915,y:1075,w:300,h:200},[]);assert.ok(r.x+300<=1920&&r.y+200<=1080);
});
test('context follows the match, then the live run, else the menu',()=>{
  const {M}=model();assert.equal(M.context({}),'menu');assert.equal(M.context({live:{active:true}}),'scenario');assert.equal(M.context({live:{active:true,paused:true}}),'menu');
  assert.equal(M.context({live:{active:true},board:{phase:'live'}}),'match');assert.equal(M.context({board:{phase:'lobby'}}),'menu');
  const w=M.widget('live-stats','a',0,0);assert.ok(!M.shownIn(w,'menu'));assert.ok(M.shownIn(w,'scenario'));w.visible=false;assert.ok(!M.shownIn(w,'scenario'));
});
test('templates are valid scenes inside the canvas',()=>{
  const {M}=model();for(const t of M.TEMPLATES){const s=M.fromTemplate(t.key,'x');deq(M.cleanScene(s),s,t.key);assert.ok(s.widgets.length>0,t.key);
    for(const w of s.widgets)assert.ok(w.x+w.w<=1920&&w.y<1080,t.key+':'+w.type);}
  assert.equal(M.freshId([{id:'w1'},{id:'w2'}],'w'),'w3');
});
