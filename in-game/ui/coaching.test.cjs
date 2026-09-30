const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
function load(){const ctx=vm.createContext({});vm.runInContext('this.window=this;Map=undefined;Set=undefined;Symbol=undefined;Number.isFinite=undefined;delete Array.prototype.flatMap;delete Array.prototype.includes;delete Array.prototype.find;Math.hypot=undefined;',ctx);vm.runInContext(fs.readFileSync(path.join(__dirname,'coaching.js'),'utf8'),ctx);return ctx;}
function runs(){return Array.from({length:40},(_,i)=>({id:'synthetic-'+i,normalizedScenario:i%2?'small':'large',timestampMs:Date.UTC(2026,8,1)+i*100000,duration_secs:60,score:(i%2?100:10000)*(1+i*.002),accuracy:1,kills:3,damage_done:2,stats_panel:{accuracy_pct:1,avg_ttk_ms:800},smoothness:{composite:80,jitter:.1,path_efficiency:.8,correction_ratio:.1},shot_timing:{avg_fire_to_hit_ms:45}}));}
test('desktop engine bundle runs with ES2015 collection/runtime helpers unavailable',()=>{const c=load();const a=c.AimModCoaching.analyze(runs());assert.ok(a.profile);assert.equal(a.samples,40);assert.ok(Array.isArray(a.cards));assert.ok(Number.isFinite(a.profile.sampleCount));});
test('scenario coaching scopes actual samples and rejects empty/outlier records',()=>{const c=load(),r=runs();r.push({...r[0],id:'empty',score:0,kills:0,damage_done:0});r.push({...r[0],id:'short',duration_secs:.1});assert.equal(c.AimModCoaching.analyze(r,'small').samples,20);assert.equal(c.AimModCoaching.analyze(r).samples,40);assert.equal(c.AimModCoaching.analyze([]).profile,null);});
function dom(c){
 function el(tag='div'){let text='';return {tag,children:[],style:{},className:'',attrs:{},appendChild(v){this.children.push(v)},setAttribute(k,v){this.attrs[k]=v},get textContent(){return text+this.children.map(c=>c.textContent).join('')},set textContent(v){text=v;this.children=[];}};}
 c.document={createElement:el};return el();
}
function all(root,predicate){const found=[];function walk(n){if(predicate(n))found.push(n);n.children.forEach(walk);}walk(root);return found;}
function css(root,name){return all(root,n=>n.className.split(' ').includes(name));}
function button(root,label){return all(root,n=>n.tag==='button'&&n.textContent===label)[0];}
test('one scope at a time, with collapsed evidence and persistent explicit expansion',()=>{
 const c=load(),root=dom(c),state={coachingHistory:runs(),selectedScenario:'small'};
 c.AimModCoaching.render(root,state);assert.equal(css(root,'coach-workspace').length,1);assert.equal(css(root,'coach-sample')[0].textContent,'40 runs');
 assert.equal(css(root,'coach-primary').length,1);assert.ok(css(root,'coach-details').every(n=>n.style.display==='none'));
 const show=button(root,'Show details');show.onclick();assert.equal(show.attrs['aria-expanded'],'true');assert.equal(css(root,'coach-details')[0].style.display,'block');
 c.AimModCoaching.render(root,state);assert.equal(css(root,'coach-details')[0].style.display,'block');
 button(root,'This scenario').onclick();assert.equal(css(root,'coach-sample')[0].textContent,'20 runs');assert.equal(css(root,'coach-scope-name')[0].textContent,'small');
 assert.ok(css(root,'coach-details').every(n=>n.style.display==='none'));button(root,'All practice').onclick();assert.equal(css(root,'coach-sample')[0].textContent,'40 runs');
});
test('missing metric axes are omitted rather than displaying engine fallback 50',()=>{
 const c=load();const result=c.AimModCoaching.measuredAxes({axes:[{key:'readiness',valuePct:50},{key:'control',valuePct:0},{key:'precision',valuePct:NaN}],metrics:{readinessPct:null,controlPct:0,precisionPct:10}});
 assert.equal(result.length,1);assert.equal(result[0].key,'control');assert.equal(result[0].valuePct,0);
});
test('unreliable oldest-pending-shot interval does not influence coaching',()=>{
 const c=load(),first=runs(),second=runs();first.forEach(r=>r.shot_timing.avg_fire_to_hit_ms=1);second.forEach(r=>r.shot_timing.avg_fire_to_hit_ms=18000);
 assert.deepEqual(JSON.parse(JSON.stringify(c.AimModCoaching.analyze(first).profile.metrics)),JSON.parse(JSON.stringify(c.AimModCoaching.analyze(second).profile.metrics)));
});
test('empty scenario text stays literal and unavailable scope is disabled',()=>{
 const c=load(),root=dom(c);c.AimModCoaching.render(root,{coachingHistory:[],selectedScenario:'<img>'});button(root,'This scenario').onclick();assert.equal(css(root,'coach-scope-name')[0].textContent,'<img>');assert.equal(css(root,'coach-empty').length,1);
 c.AimModCoaching.render(root,{coachingHistory:[]});assert.equal(button(root,'This scenario').disabled,true);
});
test('drill actions pass search queries without pretending to select exact scenarios',()=>{
 const c=load(),root=dom(c),queries=[];const input=Array.from({length:40},(_,i)=>({id:'drill-'+i,normalizedScenario:'synthetic static',timestampMs:Date.UTC(2026,8,1)+i*100000,duration_secs:60,score:100+i*.2,accuracy:90,kills:3,damage_done:2,stats_panel:{scenario_type:'StaticClicking'}}));
 c.AimModCoaching.render(root,{coachingHistory:input},()=>{throw Error('must use drill search')},query=>queries.push(query));
 const drills=css(root,'coach-drill');assert.ok(drills.length>0);drills[0].onclick();assert.equal(typeof queries[0],'string');assert.ok(queries[0].length>0);
});

function feedbackClient(c){const requests=[];c.location={pathname:'/capability/ui'};c.XMLHttpRequest=class{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}finish(status,data){this.status=status;this.readyState=4;this.responseText=JSON.stringify(data);this.onreadystatechange();}};return requests;}
test('feedback stores generated advice once and supports useful, hide, prior advice and restore',()=>{
 const c=load(),root=dom(c),requests=feedbackClient(c),state={coachingHistory:runs()};c.AimModCoaching.render(root,state);
 const payload=JSON.parse(requests[0].body),card=payload.cards[0];assert.equal(payload.action,'observe');assert.equal(requests[0].url,'/capability/coaching-feedback');assert.equal(requests[0].headers['X-AimMod-UI'],'1');assert.ok(c.AimModCoaching.analyze(runs()).cards.some(x=>x.id===card.id));
 const history=[{...card,key:'synthetic-key',scope:'all',seenAt:'2026-01-01T00:00:00Z'}];requests[0].finish(200,{feedback:[],history});
 c.AimModCoaching.render(root,state);assert.equal(requests.length,1);button(root,'Useful').onclick();assert.equal(JSON.parse(requests[1].body).feedback,'helpful');requests[1].finish(200,{feedback:[{scope:'all',id:card.id,feedback:'helpful'}],history});assert.ok(button(root,'Marked useful'));
 button(root,'Hide this advice').onclick();requests[2].finish(200,{feedback:[{scope:'all',id:card.id,feedback:'not_for_me'}],history});assert.ok(!all(root,n=>n.tag==='h3'&&n.textContent===card.title).length);
 button(root,'Advice history').onclick();assert.ok(button(root,'Restore advice'));button(root,'Restore advice').onclick();assert.equal(JSON.parse(requests[3].body).feedback,'none');requests[3].finish(200,{feedback:[],history});assert.ok(all(root,n=>n.tag==='h3'&&n.textContent===card.title).length);
});
test('feedback failure exposes retry without pretending preferences were saved',()=>{const c=load(),root=dom(c),requests=feedbackClient(c);c.AimModCoaching.render(root,{coachingHistory:runs()});requests[0].finish(503,{});assert.ok(button(root,'Retry preferences'));assert.match(root.textContent,/could not be saved/);assert.equal(requests.length,1);});
