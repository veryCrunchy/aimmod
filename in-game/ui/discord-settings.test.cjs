const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'discord-settings.js'),'utf8');
function setup(){
  class El{constructor(tag){this.tag=tag;this.children=[];this.attrs={};}appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}setAttribute(k,v){this.attrs[k]=v;}querySelectorAll(tag){return this.children.flatMap(c=>[...(c.tag===tag?[c]:[]),...c.querySelectorAll(tag)]);}text(){return [this.textContent||'',...this.children.map(c=>c.text())].join(' ');}}
  const requests=[],container=new El('div');
  class Xhr{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}abort(){this.aborted=true;}finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}
  const window={document:{createElement:t=>new El(t)},XMLHttpRequest:Xhr,location:{pathname:'/private/ui'}};
  vm.runInNewContext(source,{window});
  return {api:window.AimModDiscordSettings,container,requests,buttons:()=>container.querySelectorAll('button')};
}
const all={discordPresenceEnabled:true,discordShowScore:true,discordShowPersonalBest:true,discordShowHubButton:false};
test('shows each option and the handoff status',()=>{
  const s=setup();s.api.render(s.container);
  assert.equal(s.requests[0].method,'GET');assert.equal(s.requests[0].url,'/private/discord-settings');
  s.requests[0].finish(200,{settings:all,status:{state:'showing'}});
  const labels=s.buttons().map(b=>b.attrs['aria-label']);
  assert.deepEqual(labels,['Show AimMod on Discord','Score and accuracy','Personal best','Hub profile button']);
  assert.equal(s.buttons()[3].attrs['aria-checked'],'false');
  assert.match(s.container.text(),/Showing on Discord/);
});
test('toggling patches one option with the UI header',()=>{
  const s=setup();s.api.render(s.container);s.requests[0].finish(200,{settings:all,status:{state:'waiting'}});
  s.buttons()[1].onclick();assert.ok(s.buttons().every(b=>b.disabled));
  assert.equal(s.requests[1].method,'POST');assert.equal(s.requests[1].headers['X-AimMod-UI'],'1');
  assert.deepEqual(JSON.parse(s.requests[1].body),{discordShowScore:false});
  s.requests[1].finish(200,{settings:{...all,discordShowScore:false},status:{state:'waiting'}});
  assert.equal(s.buttons()[1].textContent,'Off');assert.match(s.container.text(),/Saved\./);
});
test('failed save keeps previous values and allows retry',()=>{
  const s=setup();s.api.render(s.container);s.requests[0].finish(200,{settings:all,status:null});
  s.buttons()[0].onclick();s.requests[1].finish(500,{});
  assert.equal(s.buttons()[0].textContent,'On');assert.ok(!s.buttons()[0].disabled);assert.match(s.container.text(),/Could not save/);
});
test('malformed response and late responses are ignored',()=>{
  const s=setup();s.api.render(s.container);s.requests[0].finish(200,{settings:{discordPresenceEnabled:'yes'}});
  assert.deepEqual(s.buttons().map(b=>b.textContent),['Try again']);
  const t=setup();t.api.render(t.container);t.api.leave();assert.equal(t.requests[0].aborted,true);
  t.requests[0].finish(200,{settings:all});assert.equal(t.buttons().length,0);
});
test('reports the shown workspace page',()=>{
  const s=setup();
  s.api.view('trends',true);
  assert.equal(s.requests[0].url,'/private/workspace-view');assert.equal(s.requests[0].headers['X-AimMod-UI'],'1');
  assert.deepEqual(JSON.parse(s.requests[0].body),{page:'trends',visible:true});
  s.api.view('trends',true);assert.equal(s.requests.length,1,'unchanged view is not resent immediately');
  s.api.view('coaching',false);assert.deepEqual(JSON.parse(s.requests[1].body),{page:'coaching',visible:false});
  s.api.view('../bad',true);assert.equal(s.requests.length,2,'invalid page keys are never sent');
});
test('Gameface-safe source',()=>{
  for(const banned of ['grid','gap:','var(--','calc(','inline-block','placeholder'])assert.ok(!source.includes(banned),banned);
});
