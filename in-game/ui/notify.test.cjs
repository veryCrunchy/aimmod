const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
function setup(){
  class El{constructor(tag){this.tag=tag;this.children=[];this.style={};this.className='';this.textContent='';}
    appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(){}all(){return this.children.flatMap(c=>[c,...c.all()]);}text(){return [this.textContent,...this.children.map(c=>c.text())].join(' ');}}
  const box=new El('main');
  class Xhr{open(){}setRequestHeader(){}send(){}}
  const window={document:{createElement:t=>new El(t),getElementById:()=>box},XMLHttpRequest:Xhr,location:{pathname:'/cap/notify'}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'notify.js'),'utf8'),{window,setTimeout:()=>1,clearTimeout:()=>{}});
  return {render:window.AimModNotify.render,box};
}
test('kill feed reads naturally when you are the victim and lines have a backing',()=>{
  const n=setup();n.render({version:1,active:false,combat:{alive:true,health:80,max:100,frags:1,fragLimit:10,left:65,feed:[{killer:'Kestrel',victim:'You',you:'victim'},{killer:'You',victim:'Nova',you:'killer',head:true}]}});
  const lines=n.box.all().filter(e=>e.className==='feed-text').map(e=>e.textContent);
  assert.deepEqual(lines,['Kestrel fragged you','You fragged Nova · headshot']);
});
test('respawn and team lines have units and plain wording',()=>{
  const n=setup();n.render({version:1,active:false,combat:{alive:false,respawnIn:3,frags:2,fragLimit:10,team:1,teamFrags:31,otherFrags:28,feed:[]}});
  const text=n.box.text();assert.match(text,/Back in 3 s/);assert.match(text,/Your team 31 · Other team 28/);
});
test('duel strip spells out the round and keeps a long opponent name in its own truncating cell',()=>{
  const n=setup();n.render({version:1,active:false,duel:{opponent:'A very long persona name that will not fit',you:41.2,them:38.14,youShare:41,themShare:38,wins:1,theirWins:0,round:2,rounds:4,disputed:true}});
  const who=n.box.all().filter(e=>e.className==='duel-who').map(e=>e.textContent);
  assert.deepEqual(who,['You','A very long persona name that will not fit']);
  assert.ok(n.box.all().some(e=>e.className==='duel-time'&&e.textContent==='Round 2/4'));
  assert.ok(n.box.all().some(e=>e.className==='duel-sub'&&/disputed/.test(e.textContent)));
  assert.match(fs.readFileSync(path.join(__dirname,'notify.css'),'utf8'),/\.duel-who\{[^}]*text-overflow:ellipsis/);
});
test('the notice label names where it comes from',()=>{
  const brand=id=>{const n=setup();n.render({version:1,active:true,id,kind:'info',title:'T',body:'B'});return n.box.all().find(e=>e.className==='brand').textContent;};
  assert.equal(brand('inv-1'),'AIMMOD · MULTIPLAYER');assert.equal(brand('tmr-1-2'),'AIMMOD · TOURNAMENT');assert.equal(brand('tci-9'),'AIMMOD · TOURNAMENT');
  assert.equal(brand('fr-a-1'),'AIMMOD · FRIENDS');assert.equal(brand('dev-up-1'),'AIMMOD · DEVELOPER TEST');
});
test('CS buy lines and key clashes share the kill feed backing',()=>{
  const n=setup();n.render({version:1,active:false,cs:{alive:true,health:100,armor:0,side:'CT',score:[1,2],round:4,phase:'freeze',money:800,buyKey:'B',useKey:'E',buy:[{key:'1',label:'Pistol',price:200,affordable:true},{key:'2',label:'Rifle',price:2700,affordable:false}],keyClashes:['B is also bound to something else']}});
  const lines=n.box.all().filter(e=>e.className==='feed-text').map(e=>e.textContent);
  assert.equal(lines.length,3);assert.ok(n.box.all().some(e=>e.className==='feed victim'));
});
test('standings show unknown values as a dash and spell out the frag limit',()=>{
  class El{constructor(t){this.children=[];this.className='';this.textContent='';}appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}all(){return this.children.flatMap(c=>[c,...c.all()]);}}
  const window={document:{createElement:t=>new El(t)}};vm.runInNewContext(fs.readFileSync(path.join(__dirname,'standings.js'),'utf8'),{window});
  const target=new El('div');window.AimModStandings.render(target,{kind:'combat',title:'Deathmatch',fragLimit:20,rows:[{rank:1,name:'Synthetic One',self:true,frags:3,deaths:null,kd:null}]},'full');
  const text=target.all().map(e=>e.textContent);
  assert.ok(text.includes('First to 20 frags'));assert.ok(text.includes('—'));assert.ok(!text.includes('-'));
});
