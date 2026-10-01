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
test('duel strip keeps the share visible next to a long name and spells out the round',()=>{
  const n=setup();n.render({version:1,active:false,duel:{role:'dodge',opponent:'A very long persona name that will not fit',percent:38.14,onTarget:38,round:2,rounds:4,disputed:true}});
  const name=n.box.all().find(e=>e.className==='duel-name'),stat=n.box.all().find(e=>e.className==='duel-stat');
  assert.match(name.textContent,/on you$/);assert.equal(stat.textContent,' · 38.1% · disputed');
  assert.ok(n.box.all().some(e=>e.className==='duel-time'&&e.textContent==='Round 2/4'));
});
