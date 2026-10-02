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
