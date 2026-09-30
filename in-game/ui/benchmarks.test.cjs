const {test}=require('node:test');

const assert=require('node:assert/strict');

const fs=require('node:fs'),vm=require('node:vm');

function setup(){

  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};}appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}setAttribute(k,v){this.attrs[k]=v;}querySelectorAll(tag){return this.children.flatMap(c=>[...(c.tag===tag?[c]:[]),...c.querySelectorAll(tag)]);}}

  const requests=[],container=new El('div');

  class Xhr{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}abort(){this.aborted=true;}finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}

  const window={document:{createElement:t=>new El(t)},XMLHttpRequest:Xhr,location:{pathname:'/private/ui'}};

  const context=vm.createContext({window});require('./test-format.cjs').loadFormat(context);
  vm.runInContext(fs.readFileSync(require('node:path').join(__dirname,'benchmarks.js'),'utf8'),context);

  return {window,api:window.AimModBenchmarks,container,requests,buttons:()=>container.querySelectorAll('button'),all:()=>{function walk(e){return [e,...e.children.flatMap(walk)];}return walk(container);}};

}

const item={id:7,name:'Synthetic benchmark',author:'Test',type:'Tracking',rank:{name:'Silver'}};

test('search and rank filter use structured benchmark summaries',()=>{

 const s=setup();s.api.enter(s.container);assert.equal(s.requests[0].url,'/private/benchmarks');

 s.requests[0].finish(200,{linked:true,items:[item,{...item,id:8,name:'Unranked',rank:null}]});

 assert.equal(s.buttons().filter(b=>b.className==='benchmark-row benchmark-item').length,1);s.buttons().find(b=>b.textContent==='All').onclick();assert.equal(s.buttons().filter(b=>b.className==='benchmark-row benchmark-item').length,2);s.buttons().find(b=>b.textContent==='Ranked').onclick();

 assert.equal(s.buttons().filter(b=>b.className==='benchmark-row benchmark-item').length,1);

 const input=s.all().find(e=>e.tag==='input');input.value='missing';input.onchange();

 assert.ok(s.all().some(e=>e.textContent==='No benchmarks match your filters.'));

});

test('detail renders actual threshold gap and bounded progress instead of fabricated ranks',()=>{

 const s=setup();s.api.enter(s.container);s.requests[0].finish(200,{linked:true,items:[item]});

 s.buttons().find(b=>b.className==='benchmark-row benchmark-item').onclick();

 assert.equal(s.requests[1].url,'/private/benchmark?id=7');

 s.requests[1].finish(200,{...item,categories:[{name:'Tracking',scenarios:[{name:'Synthetic scenario',score:150,rank:null,thresholds:[{rank:'Gold',score:200},{rank:'Bronze',score:100}]}]}]});

 assert.ok(s.all().some(e=>e.textContent==='50 to Gold'));
 assert.ok(!s.all().some(e=>e.className==='benchmark-expanded'));
 s.buttons().find(b=>b.className==='benchmark-compact-row benchmark-toggle').onclick();
 assert.ok(s.all().some(e=>e.textContent==='50 to Gold · 200 target'));

 assert.equal(s.all().find(e=>e.className==='benchmark-progress-fill').style.width,'75%');

 assert.ok(s.all().some(e=>e.className==='benchmark-rank-line achieved'&&e.children.some(c=>c.textContent==='Bronze')));

});

test('leaving prevents stale benchmark responses from rendering',()=>{

 const s=setup();s.api.enter(s.container);const before=s.container.children.length;s.api.leave();s.requests[0].finish(200,{linked:true,items:[item]});

 assert.equal(s.requests[0].aborted,true);assert.equal(s.container.children.length,before);

});


function openDetail(s){s.api.enter(s.container);s.requests[0].finish(200,{linked:true,items:[item]});s.buttons().find(b=>b.className==='benchmark-row benchmark-item').onclick();s.requests[1].finish(200,{...item,categories:[{name:'Tracking',scenarios:[{name:'Smooth A',score:150,rank:{name:'Bronze'},thresholds:[{rank:'Bronze',score:100},{rank:'Gold',score:200}]},{name:'Smooth B',score:99,rank:null,thresholds:[{rank:'Bronze',score:100}]}]},{name:'Switching',scenarios:[{name:'Precise switching',score:44,rank:null,thresholds:[{rank:'Gold',score:80}]}]}]});}
test('category navigation preserves all scenarios without displaying every category together',()=>{
 const s=setup();openDetail(s);assert.equal(s.buttons().filter(b=>b.className.indexOf('benchmark-toggle')>=0).length,2);
 assert.ok(!s.all().some(e=>e.textContent==='Precise switching'));
 s.buttons().find(b=>b.className==='benchmark-category'&&b.children[0].textContent==='Switching').onclick();
 assert.equal(s.buttons().filter(b=>b.className.indexOf('benchmark-toggle')>=0).length,1);assert.ok(s.all().some(e=>e.textContent==='Precise switching'));assert.equal(s.requests.length,2);
});
test('expanding one scenario shows every threshold and closes the previous breakdown',()=>{
 const s=setup();openDetail(s);s.buttons().filter(b=>b.className.indexOf('benchmark-toggle')>=0)[0].onclick();
 assert.ok(s.all().some(e=>e.textContent==='Gold'));assert.ok(s.all().some(e=>e.textContent==='Bronze'));
 s.buttons().filter(b=>b.className.indexOf('benchmark-toggle')>=0)[1].onclick();
 assert.equal(s.all().filter(e=>e.className==='benchmark-expanded').length,1);assert.ok(!s.all().some(e=>e.textContent==='Gold'));
});
test('scenario search jumps to a matching category and keeps unrelated categories accessible',()=>{
 const s=setup();openDetail(s);const search=s.all().find(e=>e.className==='benchmark-search benchmark-scenario-search');search.value='precise';search.oninput();
 assert.ok(s.all().some(e=>e.textContent==='Precise switching'));assert.equal(s.buttons().filter(b=>b.className.indexOf('benchmark-category')===0).length,2);
});
test('filtering keeps the same search field and large scores are grouped',()=>{
 const s=setup();s.api.enter(s.container);s.requests[0].finish(200,{linked:true,items:[item,{...item,id:9,name:'Other <b>'}]});
 const input=s.all().find(e=>e.tag==='input');input.value='other';input.onchange();
 assert.equal(s.all().find(e=>e.tag==='input'),input);assert.equal(s.buttons().filter(b=>b.className==='benchmark-row benchmark-item').length,1);assert.ok(s.all().some(e=>e.textContent==='Other <b>'));
 s.buttons().find(b=>b.className==='benchmark-row benchmark-item').onclick();s.requests[1].finish(200,{...item,categories:[{name:'Big',scenarios:[{name:'Large',score:12000,rank:null,thresholds:[{rank:'Gold',score:15500.5}]}]}]});
 assert.ok(s.all().some(e=>e.textContent==='12,000'));assert.ok(s.all().some(e=>e.textContent==='3,500.5 to Gold'));
 s.api.back();assert.ok(s.all().some(e=>e.textContent==='Your benchmarks'));
});
test('unlinked accounts get a direct link action instead of empty filters',()=>{
 const s=setup();let opened;s.window.AimModWorkspace={open:p=>opened=p};s.api.enter(s.container);s.requests[0].finish(200,{linked:false,items:[]});
 assert.ok(!s.all().some(e=>e.tag==='input'));s.buttons().find(b=>b.textContent==='Link account').onclick();assert.equal(opened,'account');
});
test('Gameface: stand-alone buttons sit in flex action rows and CJK authors fall back',()=>{
 const s=setup();s.api.enter(s.container);s.requests[0].finish(503,{});
 const retry=s.buttons().find(b=>b.textContent==='Try again');assert.ok(s.all().some(e=>e.className==='actions'&&e.children.includes(retry)));
 retry.onclick();s.requests[1].finish(200,{linked:true,items:[{...item,author:'小明',rank:{name:'Gold',index:3}}]});
 assert.ok(!s.all().some(e=>/小明/.test(e.textContent||'')));assert.ok(s.all().some(e=>e.textContent==='Tracking'));
 const rank=s.all().find(e=>e.className==='benchmark-rank is-ranked');assert.equal(rank.textContent,'Gold');assert.ok(rank.style.color);
});
