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
test('native replay opens through game command without embedded renderer or controls',()=>{const f=fixture();f.ready();assert.equal(f.requests.at(-1).body.action,'load');assert.equal(f.requests.at(-1).body.id,'synthetic');assert.equal(f.requests.at(-1).headers['X-AimMod-UI'],'1');assert.deepEqual(f.target.children.map(n=>n.tag),['h3','p','button']);});
test('successful game handoff survives workspace hiding',()=>{const f=fixture();f.ready();f.requests.at(-1).respond(200,{});const n=f.requests.length;f.context.AimModNativeReplayBrowser.leave();assert.equal(f.requests.length,n);assert.equal(f.timers.size,0);});
test('leaving before handoff cancels and prevents late transfer',()=>{const f=fixture();f.ready();const load=f.requests.at(-1);f.context.AimModNativeReplayBrowser.leave();assert.equal(f.requests.at(-1).body.action,'close');load.respond(200,{});assert.equal(f.timers.size,0);});
test('unavailable renderer never blames menu state or requests a replay load',()=>{const f=fixture();f.requests[0].respond(200,{rendererReady:false});assert.equal(f.requests.length,1);assert.match(f.target.children[1].textContent,/not available yet/);});
test('old recording lacking map identity gives accurate recovery text',()=>{const f=fixture();f.ready();f.requests.at(-1).respond(422,{});assert.match(f.target.children[1].textContent,/map data/);assert.equal(f.target.children[2].style.display,'');});
test('retry repeats readiness check rather than bypassing it',()=>{const f=fixture();f.requests[0].respond(500,{});f.target.children[2].onclick();assert.equal(f.requests.at(-1).method,'GET');});
test('a workspace that never hands off cancels the pending replay and offers retry',()=>{
 const f=fixture();f.ready();f.requests.at(-1).respond(200,{});
 const timeout=[...f.timers.values()][0];timeout();
 assert.equal(f.requests.at(-1).body.action,'close');f.requests.at(-1).respond(200,{});
 assert.match(f.target.children[1].textContent,/did not open/);
 assert.equal(f.target.children[2].style.display,'');f.target.children[2].onclick();
 assert.equal(f.requests.at(-1).method,'GET');
});
