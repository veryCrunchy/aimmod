const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'hub-sharing.js'),'utf8');
function setup(){
  class El{constructor(tag){this.tag=tag;this.children=[];this.attrs={};}appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}setAttribute(k,v){this.attrs[k]=v;}querySelectorAll(tag){return this.children.flatMap(c=>[...(c.tag===tag?[c]:[]),...c.querySelectorAll(tag)]);}text(){return [this.textContent||'',...this.children.map(c=>c.text())].join(' ');}}
  const requests=[],container=new El('div');
  class Xhr{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}abort(){this.aborted=true;}finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}
  const window={document:{createElement:t=>new El(t)},XMLHttpRequest:Xhr,location:{pathname:'/private/ui'}};
  vm.runInNewContext(source,{window});
  return {api:window.AimModHubSharing,container,requests,buttons:()=>container.querySelectorAll('button')};
}
const on={hubLiveActivityEnabled:true,hubRunUploadsEnabled:true};
const linked={linked:true,live:{state:'live'},uploads:{state:'idle',uploaded:2,skipped:0}};
test('shows both sharing options with their status',()=>{
  const s=setup();s.api.render(s.container);
  assert.equal(s.requests[0].method,'GET');assert.equal(s.requests[0].url,'/private/hub-sharing');
  s.requests[0].finish(200,{settings:on,status:linked});
  assert.deepEqual(s.buttons().map(b=>b.attrs['aria-label']),['Show me on Live activity','Upload my runs']);
  assert.ok(s.buttons().every(b=>b.attrs['aria-checked']==='true'&&/^switch/.test(b.className)));
  assert.match(s.container.text(),/on the Live page/);assert.match(s.container.text(),/uploaded after each run/);
});
test('toggling patches one option with the UI header',()=>{
  const s=setup();s.api.render(s.container);s.requests[0].finish(200,{settings:on,status:linked});
  s.buttons()[0].onclick();assert.ok(s.buttons().every(b=>b.disabled));
  assert.equal(s.requests[1].method,'POST');assert.equal(s.requests[1].headers['X-AimMod-UI'],'1');
  assert.deepEqual(JSON.parse(s.requests[1].body),{hubLiveActivityEnabled:false});
  s.requests[1].finish(200,{settings:{...on,hubLiveActivityEnabled:false},status:{...linked,live:{state:'off'}}});
  assert.equal(s.buttons()[0].textContent,'Off');assert.match(s.container.text(),/Saved\./);
});
test('without a linked account it says how to link and shows no live status',()=>{
  const s=setup();s.api.render(s.container);s.requests[0].finish(200,{settings:on,status:{linked:false,live:{state:'not-linked'},uploads:{state:'not-linked'}}});
  assert.match(s.container.text(),/Link your account/);assert.doesNotMatch(s.container.text(),/Live page\./);
});
test('a refused device asks to link again',()=>{
  const s=setup();s.api.render(s.container);s.requests[0].finish(200,{settings:on,status:{linked:true,live:{state:'rejected'},uploads:{state:'rejected'}}});
  assert.match(s.container.text(),/Link your account again/);
});
test('failed save keeps previous values; malformed and late responses are ignored',()=>{
  const s=setup();s.api.render(s.container);s.requests[0].finish(200,{settings:on,status:linked});
  s.buttons()[1].onclick();s.requests[1].finish(500,{});
  assert.equal(s.buttons()[1].textContent,'On');assert.ok(!s.buttons()[1].disabled);assert.match(s.container.text(),/Couldn’t save/);
  const m=setup();m.api.render(m.container);m.requests[0].finish(200,{settings:{hubLiveActivityEnabled:'yes'}});
  assert.deepEqual(m.buttons().map(b=>b.textContent),['Try again']);
  const t=setup();t.api.render(t.container);t.api.leave();assert.equal(t.requests[0].aborted,true);
  t.requests[0].finish(200,{settings:on,status:linked});assert.equal(t.buttons().length,0);
});
test('Gameface-safe source',()=>{
  for(const banned of ['grid','gap:','var(--','calc(','inline-block','placeholder','=>','`'])assert.ok(!source.includes(banned),banned);
});
