const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
function setup(){
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.className='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(k,v){this.attrs[k]=String(v);}getAttribute(k){return this.attrs[k];}getContext(){return null;}}
  const window={document:{createElement:t=>new El(t)}};
  vm.runInContext(fs.readFileSync(path.join(__dirname,'cshud.js'),'utf8'),vm.createContext({window}));
  const walk=e=>[e,...e.children.flatMap(walk)];
  return {hud:window.AimModCsHud,root:new El('section'),walk,text:r=>walk(r).map(e=>e.textContent||'').join('|')};
}
const base={phase:'freeze',left:12,round:3,rounds:24,tScore:1,ctScore:1,side:'T',team:1,money:2350,moneyDelta:1900,alive:true,health:100,armor:0,helmet:false,kit:false,bomb:'carried',site:null,bombIn:null,
  plantProgress:null,defuseProgress:null,useHint:null,buyOpen:true,buyWindow:true,buyLeft:32,banner:null,notice:null,feed:[],primary:null,secondary:'Glock-18',buyKey:'B',useKey:'E',keyClashes:[],
  buy:[{key:4,id:'ak47',label:'AK-47',category:'rifle',price:2700,owned:false,affordable:false,disabled:'$350 short',profile:'AimMod CS AK-47'},
       {key:3,id:'mac10',label:'MAC-10',category:'smg',price:1050,owned:false,affordable:true,disabled:null,profile:'AimMod CS MAC-10'},
       {key:null,id:'m4a1s',label:'M4A1-S',category:'rifle',price:2900,owned:false,affordable:false,disabled:'Counter-Terrorists only',profile:'AimMod CS M4A1-S'},
       {key:1,id:'glock',label:'Glock-18',category:'pistol',price:200,owned:true,affordable:true,disabled:'Already yours',profile:null}]};
test('the buy menu groups items, buys on click and explains disabled items',()=>{
  const s=setup();const calls=[];s.hud.render(s.root,base,(a,id)=>calls.push([a,id]));
  const t=s.text(s.root);assert.ok(t.includes('Pistols')&&t.includes('SMGs')&&t.includes('Rifles')&&t.includes('Heavy')&&t.includes('Gear'));
  assert.ok(t.includes('$350 short')&&t.includes('Counter-Terrorists only')&&t.includes('Owned'));
  const items=s.walk(s.root).filter(e=>e.tag==='button'&&/cs-item/.test(e.className));
  items.find(b=>b.children[1].children[0].textContent==='MAC-10').onclick();
  items.find(b=>b.children[1].children[0].textContent==='AK-47').onclick();
  assert.deepEqual(calls,[['cs-buy','mac10']],'only buyable items buy');
  assert.equal(items.find(b=>b.children[1].children[0].textContent==='AK-47').title,'AimMod CS AK-47','the mapped KovaaK’s profile');
});
test('the HUD shows score, clocks, money change, banners, feed and plant progress',()=>{
  const s=setup();
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'end',banner:{title:'Terrorists win',reason:'All enemies eliminated',won:true,team:1},notice:'Halftime · Switching sides',
    feed:[{id:1,killer:'You',victim:'Nova',weapon:'AK-47',head:true,you:'killer',killerTeam:1}]}),()=>{});
  const t=s.text(s.root);
  assert.ok(t.includes('Terrorists win')&&t.includes('All enemies eliminated')&&t.includes('Switching sides')&&t.includes('AK-47 · headshot')&&t.includes('+1,900')&&t.includes('Round 3 of 24'));
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'planted',bombIn:28,side:'CT',defuseProgress:0.4}),()=>{});
  assert.ok(s.text(s.root).includes('Bomb planted')&&s.text(s.root).includes('0:28')&&s.text(s.root).includes('Defusing'));
  s.hud.render(s.root,null,()=>{});assert.equal(s.root.className,'');
});
test('the open buy menu stays the same element while the clock ticks, so clicks are never lost to a redraw',()=>{
  const s=setup();const calls=[];
  s.hud.render(s.root,base,(a,id)=>calls.push([a,id]));
  const find=()=>s.walk(s.root).find(e=>e.tag==='button'&&/cs-item/.test(e.className)&&e.children[1].children[0].textContent==='MAC-10');
  const before=find();
  s.hud.render(s.root,Object.assign({},base,{buyLeft:31,left:11}),(a,id)=>calls.push([a,id]));
  assert.equal(find(),before,'same button after a clock-only change');
  assert.ok(s.text(s.root).includes('Buy time 0:31'),'the buy clock still counts down');
  before.onclick();assert.deepEqual(calls,[['cs-buy','mac10']]);
  s.hud.render(s.root,Object.assign({},base,{money:200}),()=>{});
  assert.notEqual(find(),before,'a money change rebuilds the menu');
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false}),()=>{});
  assert.ok(!find(),'closing removes it');
});
test('sites on the compass, the site letter, the bomb carrier and why an action was refused',()=>{
  const s=setup();
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',inSite:'A',callout:'Long A',hasBomb:true,dropKey:'G',refused:'Not in a bomb site.',
    sites:[{name:'A',bearing:-30,meters:12},{name:'B',bearing:150,meters:60},{name:'Bomb',bearing:0,meters:2}]}),()=>{});
  const t=s.text(s.root);
  assert.ok(t.includes('Bomb site A')&&t.includes('Long A')&&t.includes('You have the bomb · G drops it')&&t.includes('Not in a bomb site.'));
  const marks=s.walk(s.root).filter(e=>/cs-mark( |$)/.test(e.className));
  assert.equal(marks.length,3);
  assert.ok(/behind/.test(marks[1].className)&&marks[1].style.left==='100%','a site behind you clamps to the edge');
  assert.ok(/here/.test(marks[0].className),'the site you stand in is marked');
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',bombCarrier:'Nova'}),()=>{});
  assert.ok(s.text(s.root).includes('Bomb: Nova'),'teammates see who has the bomb');
});
test('the buy menu is centred on both axes, never under the strip, and scaled to fit a short view',()=>{
  const s=setup();const place=s.hud.place;
  const menu=(w,h)=>({offsetWidth:w,offsetHeight:h,style:{}});
  const strip=bottom=>({getBoundingClientRect:()=>({bottom})});
  // 1920x1080: centred.
  let m=menu(640,520),r=place(m,strip(76),{innerWidth:1920,innerHeight:1080});
  assert.deepEqual([r.left,r.top,r.scale],[640,280,1]);assert.equal(m.style.top,'280px');assert.equal(m.style.transform,'scale(1.000)');
  // 1280x720: the strip pushes it down; it still fits at full size.
  r=place(menu(600,520),strip(76),{innerWidth:1280,innerHeight:720});
  assert.equal(r.scale,1);assert.ok(r.top>=88&&r.top+520<=720,'below the strip and on screen');
  // A menu taller than the room left: scaled down to fit between the strip and the bottom.
  r=place(menu(640,700),strip(76),{innerWidth:1280,innerHeight:720});
  assert.equal(r.top,88);assert.ok(r.scale<1&&88+700*r.scale<=708.5,'scaled to fit');
  // 2560x1440: larger, centred, below the scaled strip.
  r=place(menu(640,520),strip(101),{innerWidth:2560,innerHeight:1440});
  assert.equal(r.scale,1.33);assert.equal(r.left,Math.round((2560-640*1.33)/2));assert.ok(r.top>=113&&r.top+520*1.33<=1440);
  // Narrow view: never wider than the screen.
  r=place(menu(640,400),strip(70),{innerWidth:600,innerHeight:720});
  assert.ok(640*r.scale<=576.5&&r.left>=12);
  // Not on the page yet (no size): left alone.
  assert.equal(place({offsetWidth:0,offsetHeight:0,style:{}},strip(76),{innerWidth:1920,innerHeight:1080}),null);
});
test('down in a round: the HUD names who the camera follows and how to switch',()=>{
  const s=setup();
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',alive:false,watching:'BOT Echo',watchHint:'Click or Space: next player · Right click: previous'}),()=>{});
  const t=s.text(s.root);
  assert.ok(t.includes('Spectating')&&t.includes('BOT Echo')&&t.includes('Click or Space: next player')&&t.includes('Down'));
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live'}),()=>{});
  assert.ok(!s.walk(s.root).some(e=>e.className==='cs-watch'),'alive: no spectator line');
});
test('the buy menu stays hidden until it has a size, then is centred again on resize (never shown at the top left)',()=>{
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.className='';this.offsetWidth=0;this.offsetHeight=0;}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(k,v){this.attrs[k]=String(v);}getAttribute(k){return this.attrs[k];}getContext(){return null;}}
  const timers=[],listeners={};
  const window={document:{createElement:t=>new El(t)},innerWidth:1920,innerHeight:1080,setTimeout:f=>timers.push(f),addEventListener:(n,f)=>{listeners[n]=f;}};
  vm.runInContext(fs.readFileSync(path.join(__dirname,'cshud.js'),'utf8'),vm.createContext({window}));
  const root=new El('section');window.AimModCsHud.render(root,base,()=>{});
  const walk=e=>[e,...e.children.flatMap(walk)];
  const menu=walk(root).find(e=>/cs-buy\b/.test(e.className)&&e.attrs.role==='dialog');
  assert.ok(menu&&!/placed/.test(menu.className),'hidden while Gameface has not laid it out');
  menu.offsetWidth=640;menu.offsetHeight=520;timers.shift()();
  assert.match(menu.className,/placed/);assert.equal(menu.style.left,'640px');assert.equal(menu.style.top,'280px','centred vertically');
  window.innerWidth=2560;window.innerHeight=1440;listeners.resize();
  assert.equal(menu.style.left,String(Math.round((2560-640*1.33)/2))+'px','re-centred when the view resizes');
});
test('grenades: a Grenades buy category, what you carry with the one in hand, and the flash and smoke overlays',()=>{
  const s=setup();const calls=[];
  const buy=base.buy.concat([{key:null,id:'flash',label:'Flashbang (1/2)',category:'grenade',price:200,owned:false,affordable:true,disabled:null,profile:null},
    {key:null,id:'molotov',label:'Molotov',category:'grenade',price:400,owned:false,affordable:true,disabled:'You carry 4 grenades',profile:null}]);
  s.hud.render(s.root,Object.assign({},base,{buy}),(a,id)=>calls.push([a,id]));
  const t=s.text(s.root);assert.ok(t.includes('Grenades')&&t.includes('Flashbang (1/2)')&&t.includes('You carry 4 grenades'));
  const items=s.walk(s.root).filter(e=>e.tag==='button'&&/cs-item/.test(e.className));
  items.find(b=>b.children[1].children[0].textContent==='Flashbang (1/2)').onclick();
  items.find(b=>b.children[1].children[0].textContent==='Molotov').onclick();
  assert.deepEqual(calls,[['cs-buy','flash']],'a grenade buys on click; one over the carry limit does not');
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',grenades:[{id:'he',label:'HE',count:1,inHand:false},{id:'flash',label:'FL',count:2,inHand:true}],grenadeKey:'4',flash:0.8,smoke:0.5}),()=>{});
  const nades=s.walk(s.root).filter(e=>/cs-nade( |$)/.test(e.className));
  assert.deepEqual(nades.map(n=>n.textContent),['HE','FL ×2']);assert.match(nades[1].className,/hand/);
  const flash=s.walk(s.root).find(e=>e.className==='cs-flash'),fog=s.walk(s.root).find(e=>e.className==='cs-smoke');
  assert.equal(flash.style.opacity,'0.8');assert.equal(fog.style.opacity,String(0.5*0.97));
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',grenades:[{id:'flash',label:'FL',count:1,inHand:true}],grenadeHint:'Fire: throw · Right: underhand · Both: medium'}),()=>{});
  let hint=s.walk(s.root).find(e=>/cs-nade-hint/.test(e.className));
  assert.equal(hint.textContent,'Fire: throw · Right: underhand · Both: medium');assert.doesNotMatch(hint.className,/armed/);
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',grenades:[{id:'flash',label:'FL',count:1,inHand:true}],grenadeHint:'Underhand · let go to throw'}),()=>{});
  hint=s.walk(s.root).find(e=>/cs-nade-hint/.test(e.className));
  assert.equal(hint.textContent,'Underhand · let go to throw');assert.match(hint.className,/armed/,'the throw you hold stands out');
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',grenades:[{id:'he',label:'HE',count:1,inHand:false}]}),()=>{});
  assert.ok(!s.walk(s.root).some(e=>/cs-nade-hint/.test(e.className)),'no hint without a grenade in hand');
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',flash:0,smoke:0}),()=>{});
  assert.ok(!s.walk(s.root).some(e=>e.className==='cs-flash'||e.className==='cs-smoke'),'no overlay once it clears');
});
test('a flash plays on the top layer from the service\'s timing: full white while it holds, clearing after, then gone',()=>{
  const s=setup();const layer={style:{}};
  const fx={id:5,age:0,hold:2500,fade:2800,peak:1};
  assert.equal(s.hud.flash(layer,fx,10000),1);assert.equal(layer.style.display,'block');assert.equal(layer.style.opacity,'1.000');
  assert.equal(s.hud.flash(layer,Object.assign({},fx,{age:1800}),12000),1,'the same flash keeps its own start (no step back with a late poll)');
  assert.equal(s.hud.flash(layer,null,10000+2500+1400),0.25,'half way through the fade a quarter of the white is left');
  s.hud.flash(layer,null,10000+5400);assert.equal(layer.style.display,'none');
  assert.equal(s.hud.flash(layer,null,20000),null,'nothing playing');
  assert.ok(Math.abs(s.hud.flashAlpha({hold:100,fade:1000,peak:0.5},600)-0.125)<1e-9);
  s.hud.render(s.root,Object.assign({},base,{buyOpen:false,phase:'live',flash:0.8,flashFx:fx}),()=>{});
  assert.ok(!s.walk(s.root).some(e=>e.className==='cs-flash'),'with the timing the HUD draws no second white');
});
