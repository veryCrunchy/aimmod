const{test}=require('node:test');const assert=require('node:assert/strict');const vm=require('node:vm');const fs=require('node:fs');const path=require('node:path');
function setup(){
  const ctx=new Proxy({},{get(){return(...args)=>{for(const a of args)if(typeof a==='number')assert.ok(Number.isFinite(a))}},set(){return true}});
  function element(tag){let text='';return{tag,children:[],style:{},offsetWidth:600,appendChild(e){this.children.push(e);return e},setAttribute(){},getContext(){return ctx},get textContent(){return text},set textContent(v){text=String(v);this.children=[]}}}
  const c=vm.createContext({window:{},document:{createElement:element},setTimeout:fn=>fn()});vm.runInContext(fs.readFileSync(path.join(__dirname,'run-details.js'),'utf8'),c);
  const root=element('div');function all(e=root){return[e,...e.children.flatMap(all)]}return{root,all,api:c.window.AimModRunDetails};
}
function fixture(){return{Run:{Id:'a',Scenario:'<script>synthetic</script>',Score:0,Accuracy:0,Duration:60},Details:{Summary:null,Timeline:[]},Response:null,Episodes:[],EpisodeCount:0,Windows:[],WindowCount:0,
  Shots:[{Sequence:1,Kind:'shot_fired',TimestampMs:1000,Count:1,Targets:[{Label:'Target',Nearest:true,Distance:0,YawErrorDegrees:0,PitchErrorDegrees:null}]}],ShotCount:101,ShotPage:0,ShotPages:2,FirstShotTimestampMs:0};}
test('run detail fetch encodes selected ID and renders zero accuracy as measured',()=>{
  const s=setup();let requested;s.api.open(s.root,"a/b'",(url,method,body,done)=>{requested=url;done(true,JSON.stringify(fixture()))});
  assert.equal(requested,"run-details/a%2Fb'?shotPage=0");assert.ok(s.all().some(e=>e.textContent==='0%'));assert.ok(!s.all().some(e=>e.tag==='script'));
  s.all().find(e=>e.tag==='button'&&e.textContent==='Shots').onclick();assert.ok(s.all().some(e=>e.textContent==='1.00s'));
  assert.ok(!s.all().some(e=>['table','tr','td'].includes(e.tag)));
});
test('shot paging requests separate bounded pages and unavailable target analysis stays empty',()=>{
  const s=setup();let urls=[];s.api.open(s.root,'a',(url,method,body,done)=>{urls.push(url);done(true,JSON.stringify(fixture()))});
  s.all().find(e=>e.tag==='button'&&e.textContent==='Shots').onclick();s.all().find(e=>e.tag==='button'&&e.textContent==='Next').onclick();
  assert.equal(urls[1],'run-details/a?shotPage=1');s.all().find(e=>e.tag==='button'&&e.textContent==='Target responses').onclick();
  assert.ok(s.all().some(e=>e.textContent==='No target response analysis was recorded for this run.'));
});
test('leaving invalidates late requests and failures retain a useful explanation',()=>{
  const s=setup();let callback;s.api.open(s.root,'a',(u,m,b,done)=>callback=done);s.api.leave();callback(true,JSON.stringify(fixture()));
  assert.ok(!s.all().some(e=>e.textContent==='Summary'));
  s.api.open(s.root,'a',(u,m,b,done)=>done(false,''));assert.ok(s.all().some(e=>e.textContent.includes('Detailed telemetry is unavailable')));
});
