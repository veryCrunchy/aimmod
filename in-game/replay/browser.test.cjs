const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
function setup(){
  class Element {
    constructor(tag){this.tag=tag;this.tagName=tag.toUpperCase();this.children=[];this.style={};this.textContent='';}
    appendChild(child){this.children.push(child);return child;}
    removeChild(child){this.children.splice(this.children.indexOf(child),1);}
    get firstChild(){return this.children[0];}
  }
  const container=new Element('div'),requests=[],renders=[],native=[];let destroyed=0,nativeClosed=0;
  class Xhr {
    constructor(){requests.push(this);}
    open(method,url){this.method=method;this.url=url;}
    setRequestHeader(){}
    send(body){this.body=body;}
    abort(){this.aborted=true;}
    finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}
  }
  const window={location:{pathname:'/capability/ui'},XMLHttpRequest:Xhr,document:{createElement:t=>new Element(t),getElementById:()=>container},
    AimModReplay:{create:(element,data)=>{renders.push(data);return {destroy:()=>destroyed++};}},
    AimModNativeReplayBrowser:{enter:(element,row)=>native.push(row),leave:()=>nativeClosed++}};
  vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname,'browser.js'),'utf8'),{window});
  function all(el=container){return [el,...el.children.flatMap(c=>all(c))];}
  return {window,browser:window.AimModReplayBrowser,container,requests,renders,native,all,destroyed:()=>destroyed,nativeClosed:()=>nativeClosed};
}
const row={id:'synthetic',scenario:'Synthetic run',recordedAt:'2026-01-01T12:00:00Z',reason:'completed'};
test('enter requests capability-prefixed replay library and renders empty state',()=>{
  const s=setup();s.browser.enter();assert.equal(s.requests[0].url,'/capability/replays');
  s.requests[0].finish(200,[]);assert.ok(s.all().some(n=>n.textContent==='No replays saved yet'));
});
test('new spatial replay uses Unreal renderer instead of canvas and closes on leave',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,[row]);
  s.all().find(n=>n.tag==='button'&&n.children.some(c=>c.textContent==='Play replay')).onclick();
  assert.equal(s.native.length,1);assert.equal(s.native[0].id,row.id);assert.equal(s.renders.length,0);
  assert.equal(s.requests.length,1);s.browser.leave();assert.equal(s.nativeClosed(),1);
});
test('leaving aborts fetch and stale responses cannot mutate the page',()=>{
  const s=setup();s.browser.enter();const before=s.all().length;s.browser.leave();
  assert.equal(s.requests[0].aborted,true);s.requests[0].finish(200,[row]);assert.equal(s.all().length,before);
});
test('malformed replay ids are excluded instead of requested',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,[{...row,id:'../private'},null]);
  assert.ok(s.all().some(n=>n.textContent==='No replays saved yet'));
});
test('request failure gives retry and does not pretend there are no replays',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(500,{});
  assert.ok(s.all().some(n=>n.textContent==='Could not load replays'));
  s.all().find(n=>n.textContent==='Try again').onclick();assert.equal(s.requests.length,2);
});

test('favorite uses authenticated local mutation and updates the row',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,[row]);
  s.all().find(n=>n.textContent==='Favorite').onclick();
  assert.equal(s.requests[1].method,'POST');assert.deepEqual(JSON.parse(s.requests[1].body),{action:'favorite',id:'synthetic',favorite:true});
  s.requests[1].finish(200,{ok:true});assert.ok(s.all().some(n=>n.textContent==='Remove favorite'));
});

test('delete requires explicit inline choice and only removes row after success',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,[row]);
  s.all().find(n=>n.textContent==='Delete').onclick();assert.equal(s.requests.length,1);
  s.all().find(n=>n.textContent==='Delete replay').onclick();assert.equal(JSON.parse(s.requests[1].body).action,'delete');
  s.requests[1].finish(503,{});assert.ok(s.all().some(n=>n.textContent===row.scenario));
});

test('favorite filter reports no match without claiming library is empty',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,[row]);
  s.all().find(n=>n.textContent==='Favorites').onclick();assert.ok(s.all().some(n=>n.textContent==='No favorite replays yet. Mark a replay as a favorite to keep it here.'));
  assert.ok(!s.all().some(n=>n.textContent==='No replays saved yet'));
  s.all().find(n=>n.textContent==='Clear filters').onclick();assert.ok(s.all().some(n=>n.textContent==='Synthetic run'));
});
test('search keeps its field while filtering and names stay literal text',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,[row,{...row,id:'second',scenario:'<img src=x onerror=alert(1)>'}]);
  const input=s.all().find(n=>n.tag==='input');input.value='img';input.onchange();
  assert.equal(s.all().find(n=>n.tag==='input'),input);assert.ok(s.all().some(n=>n.textContent==='<img src=x onerror=alert(1)>'));assert.ok(!s.all().some(n=>n.tag==='img'));
  assert.ok(!s.all().some(n=>n.textContent==='Synthetic run'));
  input.value='nothing';input.onchange();assert.ok(s.all().some(n=>n.textContent==='No replays match your filters.'));
});
test('libraries above the display cap say how many are shown',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,Array.from({length:260},(_,i)=>({...row,id:'r'+i})));
  assert.ok(s.all().some(n=>n.textContent==='250 of 260 in-game replays'));
});


test('library mutation blocks duplicate actions, filters and playback until resolved',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,[row]);
  const exportButton=s.all().find(n=>n.textContent==='Export');
  exportButton.onclick();exportButton.onclick();
  s.all().find(n=>n.textContent==='Favorite').onclick();
  s.all().find(n=>n.textContent==='Favorites').onclick();
  s.browser.refresh();
  s.all().find(n=>n.tag==='button'&&n.children.some(c=>c.textContent==='Play replay')).onclick();
  assert.equal(s.requests.length,2);assert.equal(s.native.length,0);
  assert.ok(s.all().filter(n=>n.tag==='button'||n.tag==='input').every(n=>n.disabled));
  assert.equal(s.requests[1].aborted,undefined);
  s.requests[1].finish(200,{ok:true});
  assert.ok(s.all().some(n=>n.textContent==='Saved to Documents / AimMod / Replays.'));
  s.all().find(n=>n.textContent==='Favorite').onclick();assert.equal(s.requests.length,3);
});

test('leaving a mutation discards its response and reentry reads authoritative library',()=>{
  const s=setup();s.browser.enter();s.requests[0].finish(200,[row]);
  s.all().find(n=>n.textContent==='Favorite').onclick();const mutation=s.requests[1];
  s.browser.leave();s.browser.enter();assert.equal(s.requests[2].method,'GET');
  mutation.finish(200,{ok:true});
  s.requests[2].finish(200,[{...row,favorite:true}]);
  assert.ok(s.all().some(n=>n.textContent==='Remove favorite'));
  assert.equal(s.requests.filter(r=>r.method==='POST').length,1);
});
test('a replay still waiting to start is shown with its instructions and can be reopened',()=>{
  const s=setup();let answer;s.window.AimModNativeReplayBrowser.pendingStart=cb=>answer=cb;
  s.browser.enter();s.requests[0].finish(200,[row]);answer({pending:'synthetic',scenario:'Synthetic run',reason:'challenge-active',message:'Open the pause menu (Esc) to watch the replay; it starts there.',waitingSeconds:12});
  assert.ok(s.all().some(n=>n.textContent==='Waiting to start: Synthetic run'));assert.ok(s.all().some(n=>/pause menu \(Esc\)/.test(n.textContent)));
  s.all().find(n=>n.tag==='button'&&n.textContent==='Show').onclick();assert.equal(s.native.length,1);assert.equal(s.native[0].id,'synthetic');
});
