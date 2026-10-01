const {test}=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs'),vm=require('node:vm');
function setup(surface='game'){
 class El{constructor(){this.style={};this.children=[];this.offsetHeight=200;}appendChild(x){this.children.push(x);}removeChild(x){this.children.splice(this.children.indexOf(x),1);}get firstChild(){return this.children[0];}}
 const ids={};['hud','stats','versus','scenario','stats-values','vs-values','vs-progress','vs-note','vs-title'].forEach(id=>ids[id]=new El());const requests=[],timers=[],events={};
 class Xhr{constructor(){requests.push(this);}open(method,url){this.method=method;this.url=url;}send(){}abort(){this.aborted=true;}finish(status,data){this.status=status;this.readyState=4;this.responseText=JSON.stringify(data);this.onreadystatechange();}}
 const window={document:{getElementById:id=>ids[id],createElement:()=>new El()},location:{pathname:'/readonly/overlay',search:'?surface='+surface},innerWidth:1920,innerHeight:1080,XMLHttpRequest:Xhr,setTimeout:(fn,delay)=>{timers.push({fn,delay});return timers.length;},clearTimeout:()=>{},addEventListener:()=>{},engine:{on:(name,fn)=>events[name]=fn}};
 vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname,'overlay.js'),'utf8'),{window});return {ids,requests,timers,events};
}
const settings={gameEnabled:true,obsEnabled:false,opacity:0.6,stats:{visible:true,x:99,y:99,width:240},versus:{visible:true,x:70,y:10,width:300}};
const live={available:true,active:true,paused:false,scenario:'Synthetic run',score:100,seconds:20,accuracy:50,hits:10,shots:20,personalBest:500,opponentScore:500,opponentDuration:60,projectedScore:300,projectedDelta:-200};
test('active HUD uses real values and bounded layout with 10 Hz polling',()=>{const s=setup();s.requests[0].finish(200,{live,settings});assert.equal(s.ids.hud.style.display,'block');assert.equal(s.ids.stats.style.left,'1680px');assert.equal(s.ids.stats.style.top,'880px');assert.equal(s.ids.stats.style.opacity,'0.6');assert.equal(s.ids['vs-progress'].style.width,'20%');assert.equal(s.timers[0].delay,100);assert.equal(s.requests[0].url,'/readonly/overlay-state');});
test('OBS visibility is independent and inactive data backs off with no stale numbers',()=>{const s=setup('obs');s.requests[0].finish(200,{live,settings});assert.equal(s.ids.hud.style.display,'none');assert.equal(s.timers[0].delay,1000);s.timers[0].fn();s.requests[1].finish(200,{live:{...live,active:false},settings:{...settings,obsEnabled:true}});assert.equal(s.ids.hud.style.display,'none');});
test('pause, replay and missing freshness all suppress normal-run overlays',()=>{for(const patch of [{paused:true},{replay:true},{available:false}]){const s=setup();s.requests[0].finish(200,{live:{...live,...patch},settings});assert.equal(s.ids.hud.style.display,'none');}});
test('native visibility false aborts polling and ignores stale response',()=>{const s=setup();s.events.AimModVisibility(false);s.requests[0].finish(200,{live,settings});assert.equal(s.requests[0].aborted,true);assert.equal(s.ids.hud.style.display,'none');assert.equal(s.timers.length,0);s.events.AimModVisibility(true);assert.equal(s.requests.length,2);});
test('unknown measurements remain unknown and never become artificial zeroes',()=>{const s=setup();s.requests[0].finish(200,{live:{...live,score:null,accuracy:null,projectedScore:null,projectedDelta:null},settings});assert.equal(s.ids['stats-values'].children[0].children[1].textContent,'—');assert.equal(s.ids['stats-values'].children[1].children[1].textContent,'—');assert.equal(s.ids['vs-values'].children[2].children[1].textContent,'—');});
test('unavailable selected opponent never falls back to another personal best',()=>{const s=setup();s.requests[0].finish(200,{live:{...live,opponentName:'Synthetic friend',opponentSource:'unavailable',opponentScore:null,projectedDelta:null},settings});assert.equal(s.ids['vs-title'].textContent,'VS SYNTHETIC FRIEND');assert.equal(s.ids['vs-values'].children[1].children[1].textContent,'—');assert.equal(s.ids['vs-progress'].style.width,'0%');});
test('optional live rates and native timer preserve units and legitimate zero',()=>{const s=setup();s.requests[0].finish(200,{live:{...live,remainingSeconds:40,scorePerMinute:300,killsPerSecond:0,lastTimeToKillSeconds:.25},settings});const rows=Object.fromEntries(s.ids['stats-values'].children.map(row=>row.children.map(x=>x.textContent)));assert.equal(rows.Remaining,'40.0s');assert.equal(rows['Score / min'],'300');assert.equal(rows['Kills / sec'],'0.00');assert.equal(rows['Last target'],'250 ms');});

test('game and OBS render independently positioned layouts and opacity',()=>{
 const independent={...settings,obsEnabled:true,obs:{opacity:.25,stats:{visible:true,x:10,y:20,width:300},versus:{visible:false,x:0,y:0,width:300}}};
 const game=setup(),obs=setup('obs');game.requests[0].finish(200,{live,settings:independent});obs.requests[0].finish(200,{live,settings:independent});
 assert.equal(game.ids.stats.style.left,'1680px');assert.equal(game.ids.stats.style.opacity,'0.6');assert.equal(obs.ids.stats.style.left,'192px');assert.equal(obs.ids.stats.style.opacity,'0.25');assert.equal(obs.ids.versus.style.display,'none');
});
test('fractional damage remains visible instead of rounding to zero',()=>{const s=setup();s.requests[0].finish(200,{live:{...live,damage:.084400855},settings});const damage=s.ids['stats-values'].children.find(r=>r.children[0].textContent==='Damage');assert.equal(damage.children[1].textContent,'0.084');});

test('live metric updates reuse DOM rows and remove only unavailable optional metrics',()=>{
 const s=setup();s.requests[0].finish(200,{live:{...live,damage:1},settings});const first=s.ids['stats-values'].children[0],damage=s.ids['stats-values'].children.find(x=>x.children[0].textContent==='Damage');
 s.timers[0].fn();s.requests[1].finish(200,{live:{...live,score:101,damage:2},settings});assert.equal(s.ids['stats-values'].children[0],first);assert.equal(first.children[1].textContent,'101');assert.equal(s.ids['stats-values'].children.find(x=>x.children[0].textContent==='Damage'),damage);
 s.timers[1].fn();s.requests[2].finish(200,{live:{...live,damage:null},settings});assert.equal(s.ids['stats-values'].children[0],first);assert.ok(!s.ids['stats-values'].children.includes(damage));
});
test('large live values are grouped and an opponent named like a metric keeps its own row',()=>{
 const s=setup();s.requests[0].finish(200,{live:{...live,score:123456,opponentName:'Current',opponentScore:-0.2,projectedDelta:1500},settings});
 const vs=s.ids['vs-values'].children,captions=vs.map(x=>x.children[0].textContent),values=vs.map(x=>x.children[1].textContent);
 assert.equal(captions.filter(c=>c==='Current').length,2);assert.equal(values[0],'123,456');assert.equal(values[1],'0');assert.ok(values.includes('+1,500'));
 assert.equal(s.ids['stats-values'].children[0].children[1].textContent,'123,456');
});
