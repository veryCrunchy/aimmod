const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
function surface(){
  let drawing=0;
  const ctx=new Proxy({}, {get(_,key){return function(...args){drawing++;for(const value of args)if(typeof value==='number')assert.ok(Number.isFinite(value),`finite canvas ${key}`)}},set(){return true}});
  function element(tag){let text='';return {tag,children:[],style:{},value:'',offsetWidth:600,appendChild(child){this.children.push(child);return child},setAttribute(){},getContext(){return ctx},get textContent(){return text},set textContent(value){text=String(value);this.children=[]}}}
  const context=vm.createContext({window:{devicePixelRatio:1},document:{createElement:element},setTimeout:callback=>callback()});
  require('./test-format.cjs').loadFormat(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname,'statistics.js'),'utf8'),context);
  const root=element('div');
  function all(e=root){return [e,...e.children.flatMap(c=>all(c))]}
  return {api:context.window.AimModStatistics,root,all,drawing:()=>drawing};
}
function fixture(){
  const change={Current:10,Previous:null,ChangePercent:null,CurrentSamples:1,PreviousSamples:0};
  const selected={Name:'<img onerror=alert(1)>',Runs:1,Best:0,Average:0,Median:0,VariationPercent:null,Accuracy:null,ScoreChange:change,
    Points:[{RunNumber:1,Score:0,RollingAverage:null,TrendLine:null,Accuracy:50}],Distribution:[{From:0,To:0,Count:1}]};
  const period={Key:'30',Days:30,Runs:1,Scenarios:1,Hours:1,ActiveDays:1,Selected:selected,PracticeChange:change,
    Calendar:[{Date:'2026-01-01',Minutes:60,Runs:1}],ScenarioTable:[selected],Blocks:[{Start:'2026-01-01T12:00:00Z',Runs:1,Minutes:60,Scenarios:['A']}]};
  return {Periods:[period,{...period,Key:'7',Days:7,Runs:0}],AvailableRuns:1};
}
test('statistics renders literal scenario names, legitimate zero and unknown metrics without Intl',()=>{
  const s=surface();let selected;
  s.api.render(s.root,fixture(),value=>selected=value);
  assert.ok(s.all().some(e=>e.textContent==='<img onerror=alert(1)>'));
  assert.ok(!s.all().some(e=>e.tag==='img'));
  assert.ok(s.all().some(e=>e.textContent==='0'));
  assert.ok(s.all().some(e=>e.textContent==='—'));
  s.all().find(e=>e.tag==='button'&&e.textContent==='Compare scenarios').onclick();
  const target=s.all().find(e=>e.tag==='button'&&e.textContent==='<img onerror=alert(1)>');target.onclick();
  assert.equal(selected,'<img onerror=alert(1)>');assert.ok(s.drawing()>0);
});
test('period and metric controls redraw real supplied series and search filters the scenario table',()=>{
  const s=surface();s.api.render(s.root,fixture());
  s.all().find(e=>e.tag==='button'&&e.textContent==='7 days').onclick();
  assert.ok(s.all().some(e=>e.textContent==='7 days'&&e.className.includes('primary')));
  s.all().find(e=>e.tag==='button'&&e.textContent==='Accuracy').onclick();
  assert.ok(s.all().some(e=>e.textContent==='Accuracy'&&e.className.includes('primary')));
  s.all().find(e=>e.tag==='button'&&e.textContent==='Compare scenarios').onclick();
  const input=s.all().find(e=>e.tag==='input');input.value='absent';input.oninput();
  assert.equal(s.all().find(e=>e.className==='stats-table-body').children.length,0);
});
test('empty period stays usable without plotting invalid coordinates',()=>{
  const data=fixture();const p=data.Periods[0];p.Selected.Points=[];p.Selected.Distribution=[];p.Calendar=[];p.Blocks=[];p.ScenarioTable=[];
  const s=surface();s.api.render(s.root,data);s.all().find(e=>e.tag==='button'&&e.textContent==='Practice pattern').onclick();assert.ok(s.all().some(e=>e.textContent==='No practice blocks in this period.'));
});
test('supplementary timing and movement preserve units, zero, missing samples, and independent series',()=>{
 const s=surface();const data=fixture();const selected=data.Periods[0].Selected;
 selected.Measurements=[{Key:'AverageTimeToKillMs',Average:800,Samples:1},{Key:'AverageFireToHitMs',Average:45,Samples:1},{Key:'Overshoot',Average:0,Samples:1},{Key:'DirectionalBias',Average:null,Samples:0}];
 selected.Points[0].Measurements={AverageTimeToKillMs:800,AverageFireToHitMs:45,Overshoot:0};
 s.api.render(s.root,data);
 s.all().find(e=>e.tag==='button'&&e.textContent==='Movement & timing').onclick();
 assert.ok(s.all().some(e=>e.textContent==='800 ms'));
 assert.ok(s.all().some(e=>e.textContent==='45 ms'));
 assert.ok(s.all().some(e=>e.textContent==='0%'));
 assert.ok(!s.all().some(e=>e.textContent==='Directional bias'));
 s.all().find(e=>e.tag==='button'&&e.textContent==='Average shot-to-hit interval').onclick();
 assert.ok(s.all().some(e=>e.textContent.includes('Average shot-to-hit interval (ms)')));
 assert.ok(s.drawing()>0);
});
test('Gameface layout assigns explicit pixel columns and full width chart surfaces',()=>{
 const s=surface(),data=fixture();s.api.render(s.root,data);
 s.all().find(e=>e.tag==='button'&&e.textContent==='Score spread').onclick();
 const canvases=s.all().filter(e=>e.tag==='canvas');assert.equal(canvases[0].style.width,'556px');assert.equal(canvases[1].style.width,'556px');
 const mount={children:[],style:{},clientWidth:1200,appendChild(e){this.children.push(e)}};
 s.api.renderMeasurements(mount,[{Key:'AverageTimeToKillMs',Average:20340,Samples:8},{Key:'AverageFireToHitMs',Average:18440,Samples:8}]);
 const nodes=s.all(mount);assert.ok(nodes.some(e=>e.textContent==='20.3 s'));assert.ok(nodes.some(e=>e.textContent==='18.4 s'));
 assert.ok(nodes.filter(e=>e.className==='metric').every(e=>e.style.width==='291px'&&e.style.flex==='none'));
});
test('unavailable movement charts are excluded and stale selected metric falls back to available score',()=>{
 const s=surface(),data=fixture();data.Periods[0].Selected.Points[0].Measurements={Overshoot:.2};data.Periods[0].Selected.Measurements=[{Key:'Overshoot',Average:.2,Samples:1}];
 s.api.render(s.root,data);assert.ok(!s.all().some(e=>e.tag==='button'&&e.textContent==='Path'));
 s.all().find(e=>e.tag==='button'&&e.textContent==='Overshoot').onclick();
 s.api.render(s.root,fixture());assert.ok(s.all().some(e=>e.tag==='button'&&e.textContent==='Score'&&e.className.includes('primary')));
});
test('secondary trend information is revealed one section at a time and resize preserves the selection',()=>{
 const s=surface();s.api.render(s.root,fixture());
 assert.equal(s.all().filter(e=>e.tag==='canvas').length,1);
 assert.ok(!s.all().some(e=>e.tag==='h2'&&['Score distribution','Movement and timing','Practice days','Scenario comparison','Practice blocks'].includes(e.textContent)));
 const click=text=>s.all().find(e=>e.tag==='button'&&e.textContent===text).onclick();
 click('Score spread');assert.equal(s.all().filter(e=>e.tag==='canvas').length,2);
 click('Practice pattern');assert.equal(s.all().filter(e=>e.tag==='canvas').length,1);assert.ok(s.all().some(e=>e.textContent==='Practice days'));assert.ok(s.all().some(e=>e.textContent==='Practice blocks'));
 s.api.resize();assert.ok(s.all().some(e=>e.textContent==='Practice blocks'));
 click('Compare scenarios');assert.ok(s.all().some(e=>e.className==='stats-table'));assert.ok(!s.all().some(e=>e.textContent==='Practice blocks'));
 click('Compare scenarios');assert.ok(!s.all().some(e=>e.className==='stats-table'));assert.ok(s.all().some(e=>e.textContent==='Progression'));
});
test('warm-up filter switches scenario analysis without changing total practice',()=>{const s=surface(),data=fixture();for(const p of data.Periods){p.Warmup={...p.Selected,Runs:2,Best:60,Average:50,Median:50,Points:[{Score:50,RunNumber:1}],Distribution:[{From:50,To:50,Count:2}]};p.Settled={...p.Selected,Runs:4,Best:100};}s.api.render(s.root,data,()=>{});s.all().find(e=>e.tag==='button'&&e.textContent==='Warm-up (2)').onclick();assert.ok(s.all().some(e=>e.textContent==='60'));assert.ok(s.all().some(e=>e.textContent.includes('This filter applies')));s.api.resize();assert.ok(s.all().find(e=>e.tag==='button'&&e.textContent==='Warm-up (2)').className.includes('primary'));s.all().find(e=>e.tag==='button'&&e.textContent==='All runs').onclick();assert.ok(!s.all().some(e=>e.textContent==='60'));});
