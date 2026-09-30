const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm');
function setup(){
  class El{constructor(tag){this.tag=tag;this.children=[];this.attrs={};}appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}setAttribute(k,v){this.attrs[k]=v;}querySelectorAll(tag){return this.children.flatMap(c=>[...(c.tag===tag?[c]:[]),...c.querySelectorAll(tag)]);}}
  const requests=[],container=new El('div');
  class Xhr{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}abort(){this.aborted=true;}finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}
  const window={document:{createElement:t=>new El(t)},XMLHttpRequest:Xhr,location:{pathname:'/private/ui'}};
  vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname,'settings.js'),'utf8'),{window});
  return {api:window.AimModSettings,container,requests,buttons:()=>container.querySelectorAll('button')};
}
test('settings reflect persisted values and patch just the selected option',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,{replayRecordingEnabled:true,hubHistoryEnabled:false});
  const b=s.buttons().find(b=>b.attrs['aria-label']==='Record replays');assert.equal(b.attrs['aria-checked'],'true');b.onclick();
  assert.equal(s.requests[1].headers['X-AimMod-UI'],'1');assert.deepEqual(JSON.parse(s.requests[1].body),{replayRecordingEnabled:false});
  s.requests[1].finish(200,{replayRecordingEnabled:false,hubHistoryEnabled:false});assert.equal(s.buttons()[0].textContent,'Off');
});
test('failed save restores previous values and allows retry',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,{replayRecordingEnabled:true,hubHistoryEnabled:true});s.buttons()[0].onclick();s.requests[1].finish(503,{});assert.equal(s.buttons()[0].textContent,'On');assert.ok(!s.buttons()[0].disabled);
});
test('leaving aborts work and ignores late settings response',()=>{
  const s=setup();s.api.enter(s.container);s.api.leave();s.requests[0].finish(200,{replayRecordingEnabled:true,hubHistoryEnabled:true});assert.equal(s.requests[0].aborted,true);assert.equal(s.container.children.length,0);
});
