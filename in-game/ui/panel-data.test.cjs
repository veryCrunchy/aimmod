'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const source=fs.readFileSync(__dirname+'/index.html','utf8');
function between(from,to){const a=source.indexOf(from),b=source.indexOf(to,a);assert.ok(a>=0&&b>a,from);return source.slice(a,b);}
function poller(){
  const nodes={},sent=[],renders=[],toasts=[];let parses=0;
  const ctx={visible:true,pending:false,active:'overview',lastJson:'',state:null,encodeURIComponent,
    JSON:{parse(text){parses++;return JSON.parse(text);}},
    el(id){return nodes[id]||(nodes[id]={style:{},className:'',textContent:''});},
    request(path,method,body,callback){sent.push({path,callback});},
    render(next){renders.push(next);ctx.state=next;},toast(message){toasts.push(message);},defer(fn){fn();},refreshReplayStatus(){}};
  vm.createContext(ctx);vm.runInContext(between('var failures=0,again=false','function setVisible('),ctx);
  return {ctx,sent,renders,toasts,nodes,parses:()=>parses,
    poll(status,text){ctx.refresh();const call=sent[sent.length-1];call.callback(status>=200&&status<300,text,status);return call.path;}};
}
const snapshot=(tag,runs)=>JSON.stringify(Object.assign(tag?{snapshot:tag}:{},{totalRuns:runs,history:[]}));

test('the panel parses a snapshot once and then polls with its tag',()=>{
  const p=poller();
  assert.equal(p.poll(200,snapshot('aaaa000011112222',3)),'data');
  assert.equal(p.renders.length,1);assert.equal(p.ctx.lastJson,'','a tagged snapshot is not kept as text');
  for(let i=0;i<20;i++)assert.equal(p.poll(204,''),'data?have=aaaa000011112222');
  assert.equal(p.parses(),1);assert.equal(p.renders.length,1);assert.deepEqual(p.toasts,[]);
  assert.equal(p.nodes.notice.style.display,'none');assert.equal(p.nodes['online-dot'].className,'online-dot');
});
test('a new snapshot replaces the tag and renders once',()=>{
  const p=poller();
  p.poll(200,snapshot('aaaa000011112222',3));
  assert.equal(p.poll(200,snapshot('bbbb000011112222',4)),'data?have=aaaa000011112222');
  assert.equal(p.renders.length,2);assert.equal(p.renders[1].totalRuns,4);
  assert.equal(p.poll(204,''),'data?have=bbbb000011112222');
  assert.equal(p.parses(),2);
});
test('an untagged snapshot still skips identical text',()=>{
  const p=poller(),text=snapshot('',5);
  assert.equal(p.poll(200,text),'data');assert.equal(p.poll(200,text),'data');
  assert.equal(p.parses(),1);assert.equal(p.renders.length,1);assert.equal(p.ctx.lastJson,text);
});
test('a rejected or failed poll keeps the tag it holds',()=>{
  const p=poller();
  p.poll(200,snapshot('aaaa000011112222',3));
  p.poll(200,'{"partial":');assert.equal(p.toasts.length,1);
  p.poll(200,'{}');p.poll(0,'');
  assert.equal(p.nodes.notice.style.display,'block');
  assert.equal(p.poll(204,''),'data?have=aaaa000011112222');
  assert.equal(p.renders.length,1);assert.equal(p.nodes.notice.style.display,'none');
});
test('the Home signature is built once per snapshot and only on Home',()=>{
  let builds=0;
  const ctx={JSON:{stringify(value){builds++;return JSON.stringify(value);}}};
  vm.createContext(ctx);vm.runInContext(between('var homeKeyState=null','function render('),ctx);
  const first={scenarios:[{name:'Synthetic'}],best:1},second={scenarios:[{name:'Synthetic'}],best:2};
  const key=ctx.homeKeyOf(first);
  assert.equal(ctx.homeKeyOf(first),key);assert.equal(builds,1);
  assert.notEqual(ctx.homeKeyOf(second),key);assert.equal(builds,2);
  assert.equal(ctx.homeKeyOf(first),key,'same content gives the same signature');
  assert.match(between('function render(','function renderAccount('),/var homeKey=active==='overview'\?homeKeyOf\(s\):'';/);
});
test('the request helper reports the status and the service answers a held tag with 204',()=>{
  assert.match(source,/callback\(x\.status>=200&&x\.status<300,x\.responseText,x\.status\)/);
  const host=fs.readFileSync(__dirname+'/../native-service/WorkspaceHost.cs','utf8');
  assert.match(host,/have == snapshot\.Tag \? Results\.NoContent\(\)/);
});
