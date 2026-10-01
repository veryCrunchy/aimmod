const {test}=require('node:test');const assert=require('node:assert/strict');
const deq=(a,b,m)=>assert.deepStrictEqual(JSON.parse(JSON.stringify(a)),JSON.parse(JSON.stringify(b)),m);
const {makeWindow,load}=require('./overlay-test-dom.cjs');
const settings={gameEnabled:false,obsEnabled:true,opacity:1,stats:{visible:true,x:1,y:100,width:560},versus:{visible:true,x:100,y:100,width:300}};
const setupInfo={obsAvailable:true,obsBase:'http://127.0.0.1:1234/obs',obsUrl:'http://127.0.0.1:1234/obs/overlay?surface=obs',boardUrl:'http://127.0.0.1:1234/obs/board',tournamentUrl:'http://127.0.0.1:1234/obs/tournament'};
function setup(){
  const w=makeWindow();const container=w.document.createElement('section');container.offsetWidth=1500;w.document.roots.push(container);
  load(w.window,['overlay-model.js','overlay-widgets.js','overlay-editor.js']);const M=w.window.AimModOverlayModel,api=w.window.AimModOverlayEditor;api.enter(container);
  const s={...w,M,api,container,
    byId:id=>w.document.getElementById(id),button:t=>container.find(n=>n.tagName==='BUTTON'&&n.textContent===t),
    route:p=>w.requests.filter(r=>r.url==='/cap/'+p),last:p=>w.requests.filter(r=>r.url==='/cap/'+p).at(-1),
    posts:p=>w.requests.filter(r=>r.url==='/cap/'+p&&r.method==='POST'),
    saveNow(){const t=w.timers.filter(x=>x.delay===450&&!x.cleared&&!x.ran).at(-1);assert.ok(t,'a save is pending');t.ran=true;t.fn();return JSON.parse(s.posts('overlay-scenes').at(-1).body);}};
  s.ready=(store=M.defaultStore(settings))=>{s.route('overlay-scenes')[0].finish(200,store);s.route('overlay-settings')[0].finish(200,settings);s.route('overlay-setup')[0].finish(200,setupInfo);s.route('overlay-opponents')[0].finish(200,{selectedKey:'',rows:[{key:'r1',name:'Example',score:120,scenario:'Practice'}]});s.route('overlay-feed')[0].finish(200,{enabled:true,kovaaks:{available:true,dpi:800,sens:30,sensScale:'cm/360',cm360:30,fov:103,fovScale:'Overwatch'}});};
  return s;
}
test('the editor opens on the in-game scene with every scene, the library, the canvas and the scene settings',()=>{
  const s=setup();assert.match(s.container.textContent,/Loading overlays/);s.ready();
  for(const id of ['ove-scene-game','ove-scene-stream','ove-scene-setup','ove-add-mouse-path','ove-add-settings','ove-scene-name','ove-use-obs','ove-theme-contrast'])assert.ok(s.byId(id),id);
  assert.match(s.byId('ove-scene-game').textContent,/Game/);assert.match(s.byId('ove-scene-stream').textContent,/OBS/);
  const handles=s.container.findAll(n=>/^ove-handle( |$)/.test(n.className));assert.equal(handles.length,2);
  const cards=s.container.find(n=>n.className==='ove-layer').children;assert.equal(cards.length,2,'the canvas previews the real widgets');
});
test('adding, editing and removing widgets saves the whole normalized store once per burst',()=>{
  const s=setup();s.ready();s.byId('ove-add-mouse-path').onclick();s.byId('ove-opt-thickness-up').onclick();s.byId('ove-opt-color-ff6680').onclick();
  const store=s.saveNow();assert.equal(s.posts('overlay-scenes').length,1);const game=store.scenes.find(x=>x.id==='game');const path=game.widgets.find(w=>w.type==='mouse-path');
  assert.ok(path);assert.equal(path.opts.thickness,3.5);assert.equal(path.opts.color,'#ff6680');deq(path.show,{menu:false,scenario:true,match:true});
  assert.equal(s.posts('overlay-scenes')[0].headers['X-AimMod-UI'],'1');
  s.posts('overlay-scenes')[0].finish(200,store);assert.match(s.byId('ove-status').textContent,/saved/);
  s.byId('ove-show-menu').onclick();s.byId('ove-variant-compact').onclick();s.byId('ove-labels').onclick();
  let next=s.saveNow().scenes.find(x=>x.id==='game').widgets.find(w=>w.type==='mouse-path');deq([next.show.menu,next.variant,next.labels],[true,'compact',false]);
  s.byId('ove-remove').onclick();next=s.saveNow().scenes.find(x=>x.id==='game');assert.ok(!next.widgets.some(w=>w.type==='mouse-path'));
});
test('position fields, text size and opacity are bounded',()=>{
  const s=setup();s.ready();s.byId('ove-layer-stats').onclick();const x=s.byId('ove-field-x');x.value='99999';x.onchange();
  for(let i=0;i<30;i++){const up=s.byId('ove-font-up');up.onclick();}
  const w=s.saveNow().scenes.find(x=>x.id==='game').widgets.find(w=>w.id==='stats');assert.equal(w.x,1900);assert.equal(w.font,2.5);
});
test('dragging moves the selected widget with snapping and a guide, then saves',()=>{
  const s=setup();s.ready();s.byId('ove-layer-versus').onclick();
  const handle=s.container.find(n=>n.amId==='versus');const scale=900/1920;const v=s.M.findWidget(s.M.find(s.api.state().store,'game'),'versus');
  handle.onmousedown({button:0,clientX:1000*scale,clientY:100*scale,preventDefault(){},stopPropagation(){}});
  s.document.dispatch('mousemove',{clientX:(1000-v.x+3)*scale,clientY:(100-v.y+2)*scale});
  const guides=s.container.find(n=>n.className==='ove-guides');assert.ok(guides.children.length>=1,'a snap guide shows');
  s.document.dispatch('mouseup',{});assert.equal(guides.children.length,0);
  const w=s.saveNow().scenes.find(x=>x.id==='game').widgets.find(w=>w.id==='versus');assert.equal(w.x,0);assert.equal(w.y,0);
});
test('arrow keys nudge and Delete removes the selected widget, but not while typing',()=>{
  const s=setup();s.ready();s.byId('ove-layer-stats').onclick();const before=s.M.find(s.api.state().store,'game').widgets[0].x;
  s.document.dispatch('keydown',{keyCode:39,shiftKey:true,target:{tagName:'DIV'},preventDefault(){}});
  s.document.dispatch('keydown',{keyCode:46,target:{tagName:'INPUT'},preventDefault(){}});
  let game=s.saveNow().scenes.find(x=>x.id==='game');assert.equal(game.widgets[0].x,before+10);assert.equal(game.widgets.length,2);
  s.document.dispatch('keydown',{keyCode:46,target:{tagName:'DIV'},preventDefault(){}});game=s.saveNow().scenes.find(x=>x.id==='game');assert.equal(game.widgets.length,1);
});
test('scenes: templates, theme, output assignment, rename, duplicate and confirmed delete',()=>{
  const s=setup();s.ready();s.byId('ove-new-scene').onclick();s.byId('ove-template-practice').onclick();
  let store=s.saveNow();assert.equal(store.scenes.length,4);const added=store.scenes[3];assert.ok(added.widgets.some(w=>w.type==='mouse-path'));assert.equal(s.api.state().scene,added.id);
  s.byId('ove-theme-contrast').onclick();s.byId('ove-use-obs').onclick();const name=s.byId('ove-scene-name');name.value='  Practice stream  ';name.onchange();
  store=s.saveNow();const sc=store.scenes.find(x=>x.id===added.id);assert.equal(sc.theme.preset,'contrast');assert.equal(sc.name,'Practice stream');assert.equal(store.obsScene,added.id);
  s.button('Duplicate scene').onclick();assert.equal(s.saveNow().scenes.length,5);
  s.byId('ove-delete-scene').onclick();assert.ok(!s.timers.some(t=>t.delay===450&&!t.ran&&!t.cleared),'delete waits for the confirmation');
  s.button('Confirm delete').onclick();store=s.saveNow();assert.equal(store.scenes.length,4);
});
test('share codes export the current scene and import as a new one; bad codes explain why',()=>{
  const s=setup();s.ready();s.byId('ove-share').onclick();const code=s.byId('ove-export').value;assert.match(code,/^AIMMOD-OVERLAY-1:/);
  const input=s.byId('ove-import');input.value='nonsense';input.oninput();s.button('Import as a new scene').onclick();assert.match(s.container.textContent,/isn.t a valid AimMod overlay scene/);
  const again=s.byId('ove-import');again.value=code;again.oninput();s.button('Import as a new scene').onclick();
  const store=s.saveNow();assert.equal(store.scenes.length,4);deq(store.scenes[3].widgets,store.scenes[0].widgets);assert.notEqual(store.scenes[3].id,'game');
});
test('OBS and in-game outputs: master switches keep the earlier settings API, every scene and widget has a URL',()=>{
  const s=setup();s.ready();s.byId('ove-tab-outputs').onclick();
  assert.equal(s.byId('ove-url-stream').value,'http://127.0.0.1:1234/obs/scene?id=stream&w=1920&h=1080');
  s.byId('ove-res-2560x1440').onclick();assert.equal(s.byId('ove-url-setup').value,'http://127.0.0.1:1234/obs/scene?id=setup&w=2560&h=1440');
  assert.match(s.container.textContent,/Set Width to 2560 and Height to 1440/);
  const widgetUrls=s.container.findAll(n=>n.className==='ove-url small').map(n=>n.value);assert.ok(widgetUrls.includes('http://127.0.0.1:1234/obs/scene?id=game&widget=stats&w=2560&h=1440'));
  assert.equal(s.byId('ove-url-legacy').value,setupInfo.obsUrl);assert.equal(s.byId('ove-url-board').value,setupInfo.boardUrl);
  s.byId('ove-game-enabled').onclick();deq(JSON.parse(s.posts('overlay-settings')[0].body),{gameEnabled:true});
  s.posts('overlay-settings')[0].finish(200,{...settings,gameEnabled:true});assert.equal(s.byId('ove-game-enabled').attrs['aria-checked'],'true');
  const pick=s.byId('ove-game-scene');pick.value='setup';pick.onchange();assert.equal(s.saveNow().gameScene,'setup');
  s.byId('ove-url-stream-copy').onclick();assert.ok(s.byId('ove-url-stream').selected);
});
test('profile peripherals are added, edited, reordered and removed; KovaaK’s settings are listed read-only',()=>{
  const s=setup();s.ready();s.byId('ove-tab-profile').onclick();s.byId('ove-gear-add-mouse').onclick();s.byId('ove-gear-add-iems').onclick();
  const v=s.byId('ove-gear-value-0');v.value='  Example Mouse  ';v.onchange();
  let store=s.saveNow();deq(store.profile.peripherals,[{label:'Mouse',value:'Example Mouse'},{label:'IEMs',value:''}]);
  s.container.findAll(n=>n.textContent==='Up')[1].onclick();s.byId('ove-gear-remove-1').onclick();store=s.saveNow();deq(store.profile.peripherals,[{label:'IEMs',value:''}]);
  assert.match(s.container.textContent,/30\.00 cm\/360/);assert.match(s.container.textContent,/103° · Overwatch/);
});
test('VS opponent selection still saves through overlay-opponents',()=>{
  const s=setup();s.ready();s.byId('ove-tab-vs').onclick();const sel=s.byId('ove-opponent');sel.value='r1';sel.onchange();deq(JSON.parse(s.posts('overlay-opponents')[0].body),{key:'r1'});
});
test('a failed load offers a retry, and leaving aborts requests so late responses cannot render',()=>{
  const s=setup();s.route('overlay-scenes')[0].finish(500,'');assert.match(s.container.textContent,/Could not load your overlays/);s.button('Try again').onclick();assert.equal(s.route('overlay-scenes').length,2);
  const t=setup();t.api.leave();const before=t.container.textContent;t.route('overlay-scenes')[0].finish(200,t.M.defaultStore());assert.equal(t.container.textContent,before);assert.ok(t.requests.every(r=>r.aborted));
});
test('a pending change is saved when the page is left',()=>{
  const s=setup();s.ready();s.byId('ove-add-clock').onclick();s.api.leave();const post=s.posts('overlay-scenes').at(-1);assert.ok(post);assert.ok(JSON.parse(post.body).scenes[0].widgets.some(w=>w.type==='clock'));
});
test('live data previews the real feed and polls motion only when the scene shows a path or input widget',()=>{
  const s=setup();s.ready();s.byId('ove-data-live').onclick();const feed=s.last('overlay-feed');feed.finish(200,{enabled:true,live:{active:true,score:4321,scenario:'Synthetic live'}});
  assert.match(s.container.find(n=>n.className==='ove-layer').textContent,/4,321/);
  s.run();assert.ok(!s.requests.some(r=>r.url.indexOf('overlay-motion')>=0),'the in-game scene has no motion widget');
  s.byId('ove-add-input').onclick();s.run();const m=s.requests.find(r=>r.url.indexOf('overlay-motion')>=0);assert.ok(m);assert.equal(m.url,'/cap/overlay-motion?input=1');
  m.finish(200,{input:{available:true,w:true}});s.run();const key=s.container.findAll(n=>/^amw-key( |$)/.test(n.className)).find(k=>k.textContent==='W');assert.equal(key.style.background,'#27e4a1');
});
