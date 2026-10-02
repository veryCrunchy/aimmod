const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
function setup(){
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.className='';this.textContent='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}}
  const body=new El('body');let handler=null;
  const window={document:{createElement:t=>new El(t),body},engine:{on:(name,fn)=>{if(name==='AimModTags')handler=fn;}}};
  vm.runInContext(fs.readFileSync(path.join(__dirname,'worldtags.js'),'utf8'),vm.createContext({window,JSON,Math,String}));
  return {body,fire:j=>handler(j),api:window.AimModWorldTags};
}
test('AimModCore tags draw teammates in team colours and only an aimed enemy, reusing nodes',()=>{
  const s=setup();
  s.fire(JSON.stringify({tags:[{n:'Nova',t:'T',f:1,a:1,c:0,x:0.25,y:0.5,d:12},{n:'Kestrel',t:'CT',f:0,a:1,c:1,x:0.5,y:0.45,d:30}]}));
  const layer=s.body.children[0];
  assert.equal(layer.className,'wt-layer');
  const [mate,foe]=layer.children;
  assert.equal(mate.className,'wt-tag t friend');assert.equal(mate.nameNode.textContent,'Nova');assert.equal(mate.distNode.textContent,'12 m');
  assert.equal(mate.style.left,'25.00%');assert.equal(mate.style.top,'50.00%');
  assert.equal(foe.className,'wt-tag ct enemy aimed');assert.equal(foe.distNode.textContent,'','no distance for enemies');
  s.fire(JSON.stringify({tags:[{n:'Nova',t:'T',f:1,a:0,c:0,x:0.3,y:0.5,d:12}]}));
  assert.equal(layer.children[0],mate,'the same node moves');
  assert.equal(mate.className,'wt-tag t friend down');assert.equal(mate.style.left,'30.00%');
  assert.equal(foe.style.display,'none','tags that went away hide');
  s.fire('not json');
  assert.equal(mate.style.display,'none','bad data clears the layer');
});
test('CS teammates show their gear under the name: bomb, weapon, armour, kit and health; enemies never do',()=>{
  const s=setup();
  s.fire(JSON.stringify({tags:[{n:'Nova',t:'CT',f:1,a:1,c:0,x:0.5,y:0.5,d:8,g:'w=M4A1-S;hp=24;ar=1;hm=1;kit=1'},{n:'Ash',t:'T',f:0,a:1,c:1,x:0.4,y:0.5,d:20}]}));
  const [mate,foe]=s.body.children[0].children;
  assert.match(mate.className,/geared/);assert.equal(mate.weaponNode.textContent,'M4A1-S');
  assert.equal(mate.kitNode.style.display,'');assert.equal(mate.bombNode.style.display,'none');assert.equal(mate.armorNode.textContent,'A+H');
  assert.equal(mate.hpFill.style.width,'24%');assert.match(mate.hpFill.className,/low/);
  assert.doesNotMatch(foe.className,/geared/);assert.equal(foe.weaponNode.textContent,'');assert.equal(foe.hpBar.style.display,'none');
  s.fire(JSON.stringify({tags:[{n:'Nova',t:'T',f:1,a:1,c:0,x:0.5,y:0.5,d:8,g:'w=Glock-18;hp=100;c4=1'}]}));
  assert.equal(mate.bombNode.style.display,'','the bomb carrier is marked');assert.equal(mate.kitNode.style.display,'none');assert.equal(mate.armorNode.style.display,'none');
  assert.deepEqual(Object.assign({},s.api.gearOf('w=AK-47;hp=5')),{w:'AK-47',hp:'5'});
});
test('CS teammates show their grenades as small chips',()=>{
  const s=setup();
  s.fire(JSON.stringify({tags:[{n:'Nova',t:'T',f:1,a:1,c:0,x:0.5,y:0.5,d:8,g:'w=AK-47;hp=100;g=he,flash,flash,smoke'}]}));
  const mate=s.body.children[0].children[0];
  assert.deepEqual(Array.from(mate.nadeNodes,n=>n.style.display==='none'?'':n.textContent),['HE','FL','FL','SM']);
  assert.match(mate.nadeNodes[3].className,/wt-smoke/);
  s.fire(JSON.stringify({tags:[{n:'Nova',t:'T',f:1,a:1,c:0,x:0.5,y:0.5,d:8,g:'w=AK-47;hp=100;g=molotov'}]}));
  assert.deepEqual(Array.from(mate.nadeNodes,n=>n.style.display==='none'?'':n.textContent),['MO','','','']);
  s.fire(JSON.stringify({tags:[{n:'Nova',t:'T',f:1,a:1,c:0,x:0.5,y:0.5,d:8,g:'w=AK-47;hp=100'}]}));
  assert.ok(mate.nadeNodes.every(n=>n.style.display==='none'),'no grenades, no chips');
});
test('the notice page loads Gameface\'s cohtml.js before its scripts, so AimModCore\'s events (tags, pointer) arrive',()=>{
  for(const file of ['notify.html','overlay.html']){
    const html=fs.readFileSync(path.join(__dirname,file),'utf8');
    const engine=html.indexOf('<script src="coui://uiresources/javascript/cohtml.js"></script>'),first=html.search(/<script src="(?!coui:)/);
    assert.ok(engine>0&&first>engine,file+' loads cohtml.js first');
  }
  const notify=fs.readFileSync(path.join(__dirname,'notify.html'),'utf8');
  assert.ok(notify.indexOf('id="cs-flash"')>notify.indexOf('id="cs-hud"'),'the flash layer comes after the CS HUD');
});
test('without cohtml.js the page still takes the events (a minimal engine), and reports what arrived',()=>{
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.className='';this.textContent='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}insertBefore(c,before){c.parentNode=this;this.children.splice(this.children.indexOf(before),0,c);return c;}}
  const body=new El('body'),flash=body.appendChild(new El('div'));flash.id='cs-flash';
  const posts=[];class Xhr{open(m,u){this.u=u;}setRequestHeader(){}send(b){posts.push([this.u,JSON.parse(b)]);}}
  const window={document:{createElement:t=>new El(t),body,getElementById:id=>id==='cs-flash'?flash:null},XMLHttpRequest:Xhr,location:{pathname:'/cap/notify'}};
  vm.runInContext(fs.readFileSync(path.join(__dirname,'worldtags.js'),'utf8'),vm.createContext({window,JSON,Math,String,Array}));
  assert.equal(typeof window.engine._trigger,'function');
  window.engine._trigger('AimModTags',JSON.stringify({tags:[{n:'Nova',t:'T',f:1,a:1,c:0,x:0.5,y:0.5,d:4}]}));
  assert.equal(body.children[0].className,'wt-layer','the tags layer goes under the flash layer');
  assert.equal(body.children[0].children[0].nameNode.textContent,'Nova');
  window.AimModWorldTags.report();
  assert.equal(posts.length,1);assert.equal(posts[0][0],'/cap/multiplayer');assert.equal(posts[0][1].action,'tags-debug');
  assert.match(posts[0][1].counts,/minimal engine.*1 events in the last 10 s, 1 tags/);
  window.AimModWorldTags.report();window.AimModWorldTags.report();
  assert.equal(posts.length,2,'an idle page reports the silence once');
});
