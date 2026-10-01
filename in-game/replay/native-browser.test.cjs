const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path'),test=require('node:test'),assert=require('node:assert/strict');
function fixture(){
 const requests=[],timers=new Map();let sequence=0;
 function element(tag){return {tag,children:[],style:{},appendChild(n){this.children.push(n)},removeChild(n){this.children.splice(this.children.indexOf(n),1)},get firstChild(){return this.children[0]}}}
 function XHR(){requests.push(this);this.headers={};this.open=(method,url)=>{this.method=method;this.url=url};this.setRequestHeader=(k,v)=>this.headers[k]=v;this.send=body=>this.body=body?JSON.parse(body):null;this.abort=()=>this.aborted=true;this.respond=(status,data)=>{this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange?.()}}
 const context={document:{createElement:element},XMLHttpRequest:XHR,location:{pathname:'/synthetic/ui'},setTimeout:callback=>{timers.set(++sequence,callback);return sequence},clearTimeout:id=>timers.delete(id)};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'native-browser.js'),'utf8'),context);
 const target=element('main');context.AimModNativeReplayBrowser.enter(target,{id:'synthetic',scenario:'Synthetic'});
 return {requests,timers,target,context,ready(){requests[0].respond(200,{rendererReady:true})}};
}
test('native replay opens through game command without embedded renderer or controls',()=>{const f=fixture();f.ready();assert.equal(f.requests.at(-1).body.action,'load');assert.equal(f.requests.at(-1).body.id,'synthetic');assert.equal(f.requests.at(-1).headers['X-AimMod-UI'],'1');assert.deepEqual(f.target.children.map(n=>n.tag),['h3','p','button','div']);assert.equal(f.target.children[3].style.display,'none');});
test('successful game handoff survives workspace hiding',()=>{const f=fixture();f.ready();f.requests.at(-1).respond(200,{});const n=f.requests.length;f.context.AimModNativeReplayBrowser.leave();assert.equal(f.requests.length,n);assert.equal(f.timers.size,0);});
test('leaving before handoff cancels and prevents late transfer',()=>{const f=fixture();f.ready();const load=f.requests.at(-1);f.context.AimModNativeReplayBrowser.leave();assert.equal(f.requests.at(-1).body.action,'close');load.respond(200,{});assert.equal(f.timers.size,0);});
test('unavailable renderer never blames menu state or requests a replay load',()=>{const f=fixture();f.requests[0].respond(200,{rendererReady:false});assert.equal(f.requests.length,1);assert.match(f.target.children[1].textContent,/not available yet/);});
test('old recording lacking map identity gives accurate recovery text',()=>{const f=fixture();f.ready();f.requests.at(-1).respond(422,{});assert.match(f.target.children[1].textContent,/map data/);assert.equal(f.target.children[2].style.display,'');});
test('missing replay files and renderer races get specific recovery text',()=>{for(const [code,text] of [[404,/missing or incomplete/],[409,/not ready yet/]]){const f=fixture();f.ready();f.requests.at(-1).respond(code,{});assert.match(f.target.children[1].textContent,text);assert.equal(f.target.children[2].style.display,'');}});
test('retry repeats readiness check rather than bypassing it',()=>{const f=fixture();f.requests[0].respond(500,{});f.target.children[2].onclick();assert.equal(f.requests.at(-1).method,'GET');});
test('a workspace that never hands off cancels the pending replay and offers retry',()=>{
 const f=fixture();f.ready();f.requests.at(-1).respond(200,{});
 const timeout=[...f.timers.values()][0];timeout();
 assert.equal(f.requests.at(-1).body.action,'close');f.requests.at(-1).respond(200,{});
 assert.match(f.target.children[1].textContent,/did not open/);
 assert.equal(f.target.children[2].style.display,'');f.target.children[2].onclick();
 assert.equal(f.requests.at(-1).method,'GET');
});
function gated(){const f=fixture();f.requests[0].respond(200,{rendererReady:false,start:{pending:null,reason:null,message:null,waitingSeconds:0}});return f;}
const pendingState=(extra={})=>({rendererReady:false,start:{pending:'synthetic',scenario:'Synthetic',reason:'scenario-loading',message:'Loading “Synthetic”…',waitingSeconds:42,...extra}});
const pendingBox=f=>f.target.children[3];
test('a start-gated service sends load even when the renderer is not ready yet',()=>{const f=gated();assert.equal(f.requests.at(-1).method,'POST');assert.equal(f.requests.at(-1).body.action,'load');});
test('202 shows the service message, a waiting timer, and Retry/Cancel',()=>{
 const f=gated();f.requests.at(-1).respond(202,pendingState());
 const box=pendingBox(f);assert.equal(box.style.display,'');assert.equal(box.children[0].textContent,'Loading “Synthetic”…');assert.equal(f.target.children[1].style.display,'none');assert.equal(box.children[1].textContent,'Waiting 0:42');
 assert.deepEqual(box.children[2].children.filter(b=>b.style.display!=='none').map(b=>b.textContent),['Retry now','Cancel']);assert.equal(f.target.children[2].style.display,'none');
 const tick=[...f.timers.entries()].find(([,fn])=>fn.toString().includes('countUp'));assert.ok(tick);
});
test('waiting survives the workspace hiding and polls until the replay starts',()=>{
 const f=gated();f.requests.at(-1).respond(202,pendingState());const count=f.requests.length;
 f.context.AimModNativeReplayBrowser.leave();assert.equal(f.requests.length,count);
});
test('poll updates the pending message and reports a start as success',()=>{
 const f=gated();f.requests.at(-1).respond(202,pendingState());
 const poll=()=>{for(const [id,fn] of [...f.timers.entries()])if(fn.toString().includes("request('GET'")){f.timers.delete(id);fn();return true;}return false;};
 assert.ok(poll());f.requests.at(-1).respond(200,pendingState({reason:'challenge-active',message:'Open the pause menu (Esc) to watch the replay; it starts there.',waitingSeconds:50}));
 assert.match(pendingBox(f).children[0].textContent,/pause menu \(Esc\)/);
 assert.ok(poll());f.requests.at(-1).respond(200,{rendererReady:true,playback:{visible:true},start:{pending:null,reason:null,message:null,waitingSeconds:0}});
 assert.match(f.target.children[1].textContent,/Opening replay in the game/);assert.equal(pendingBox(f).style.display,'none');
});
test('an expired start shows a clear error with Try again',()=>{
 const f=gated();f.requests.at(-1).respond(202,pendingState());
 const poll=[...f.timers.values()].find(fn=>fn.toString().includes("request('GET'"));poll();
 f.requests.at(-1).respond(200,{rendererReady:false,start:{pending:null,reason:'timed-out',message:'The replay did not start within 10 minutes. Press play to try again.',waitingSeconds:0}});
 assert.match(f.target.children[1].textContent,/within 10 minutes/);assert.equal(f.target.children[2].style.display,'');assert.equal(pendingBox(f).style.display,'none');
});
test('Retry re-sends load and Cancel sends cancel',()=>{
 const f=gated();f.requests.at(-1).respond(202,pendingState());const [,retryNow,cancel]=pendingBox(f).children[2].children;
 retryNow.onclick();assert.equal(f.requests.at(-1).body.action,'load');f.requests.at(-1).respond(202,pendingState({waitingSeconds:61}));assert.equal(pendingBox(f).children[1].textContent,'Waiting 1:01');
 cancel.onclick();assert.equal(f.requests.at(-1).body.action,'cancel');assert.match(f.target.children[1].textContent,/cancelled/);assert.equal(f.target.children[2].style.display,'');
});
test('reopening a replay that is already pending resumes waiting instead of loading again',()=>{
 const f=fixture();f.requests[0].respond(200,pendingState());assert.equal(f.requests.length,1);assert.equal(pendingBox(f).style.display,'');
});
test('a missing scenario a Workshop item provides offers Download, which asks the service',()=>{
 const f=gated();f.requests.at(-1).respond(202,pendingState({reason:'scenario-missing',message:'“Synthetic” isn’t installed. Download it from the Steam Workshop; the replay starts when it’s installed.',download:{workshop:true,state:null,percent:null}}));
 const [download]=pendingBox(f).children[2].children;assert.equal(download.style.display,'');assert.match(pendingBox(f).children[0].textContent,/Steam Workshop/);
 download.onclick();assert.equal(f.requests.at(-1).body.action,'download');assert.equal(download.style.display,'none');
 f.requests.at(-1).respond(409,{error:'workshop',message:'Installing maps needs Steam.'});assert.equal(pendingBox(f).children[0].textContent,'Installing maps needs Steam.');
});
test('a download under way hides the button; without an offer there is none',()=>{
 const f=gated();f.requests.at(-1).respond(202,pendingState({reason:'scenario-missing',message:'Downloading “Synthetic” from the Steam Workshop (40%)…',download:{workshop:true,state:'downloading',percent:40}}));
 assert.equal(pendingBox(f).children[2].children[0].style.display,'none');
 const g=gated();g.requests.at(-1).respond(202,pendingState());assert.equal(pendingBox(g).children[2].children[0].style.display,'none');
});
test('leaving the wait shows the note again',()=>{
 const f=gated();f.requests.at(-1).respond(202,pendingState());assert.equal(f.target.children[1].style.display,'none');
 pendingBox(f).children[2].children[2].onclick();assert.equal(f.target.children[1].style.display,'');assert.match(f.target.children[1].textContent,/cancelled/);
});
