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
