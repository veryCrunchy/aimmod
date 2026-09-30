import 'core-js/es/symbol';
import 'core-js/es/map';
import 'core-js/es/set';
import 'core-js/es/array/iterator';
import 'core-js/es/array/find';
import 'core-js/es/array/flat-map';
import 'core-js/es/array/includes';
import 'core-js/es/string/includes';
import 'core-js/es/number/is-finite';
import 'core-js/es/math/hypot';
import 'core-js/es/object/assign';
import {buildGlobalCoachingCards,buildPlayerLearningProfile,CoachingAnalyticsRecord} from '../../src/coaching/engine';
declare global {interface Window {AimModCoaching:any}}
function median(values:number[]){const a=values.slice().sort((a,b)=>a-b);return a.length?(a[Math.floor((a.length-1)/2)]+a[Math.floor(a.length/2)])/2:0;}
function records(input:any[]):CoachingAnalyticsRecord[]{
 const durations=new Map<string,number[]>();
 input.forEach(r=>{if(Number.isFinite(r.duration_secs)&&r.duration_secs>0){const key=r.normalizedScenario;const a=durations.get(key)||[];a.push(r.duration_secs);durations.set(key,a);}});
 return input.map(r=>{const values=durations.get(r.normalizedScenario)||[];const m=median(values),mad=median(values.map(v=>Math.abs(v-m))),spread=Math.max(8,m*.3,mad*10);
 return {...r,shot_timing:{...r.shot_timing,avg_fire_to_hit_ms:null},accuracy:typeof r.accuracy==="number"?r.accuracy/100:null,stats_panel:{...r.stats_panel,accuracy_pct:typeof r.stats_panel?.accuracy_pct==="number"?r.stats_panel.accuracy_pct/100:null},isReliableForAnalysis:Number.isFinite(r.timestampMs)&&r.timestampMs>0&&Number.isFinite(r.score)&&Number.isFinite(r.duration_secs)&&r.duration_secs>=Math.max(5,m-spread)&&r.duration_secs<=Math.max(m*2.25,m+spread)&&!(r.score<=0&&r.kills===0&&(r.damage_done||0)<=0)};});
}
function analyze(input:any[],scenario?:string){const all=records(input),selected=scenario?all.filter(r=>r.normalizedScenario===scenario):all;const warmup=new Set<string>();return {profile:buildPlayerLearningProfile(selected,null,warmup),cards:buildGlobalCoachingCards(selected,null,warmup),samples:selected.filter(r=>r.isReliableForAnalysis).length};}
function node(parent:HTMLElement,tag:string,text?:string,cls?:string){const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;parent.appendChild(e);return e;}
const axisMetric:Record<string,string>={readiness:'readinessPct',adaptation:'adaptationPct',endurance:'endurancePct',transfer:'transferPct',precision:'precisionPct',control:'controlPct',consistency:'consistencyPct',learning:'learningEfficiencyPct'};
const focusCopy:Record<string,string>={readiness:'Start with a short warm-up before your score attempts.',adaptation:'Give yourself a few runs to settle into each task.',endurance:'Keep your block short enough to finish with control.',transfer:'Add one contrasting scenario to your practice.',precision:'Slow down enough to make each hit deliberate.',control:'Keep your movement smooth as you add difficulty.',consistency:'Repeat the same setup and focus cue across your next block.',learning:'Keep one clear focus and compare your next session.'};
// Deliberate short labels, not cut-off engine paragraphs. Full evidence remains available.
const cardCopy:Record<string,[string,string]>={
 'global-warmup-tax':['Warm up before scoring','Start with 2-3 easier runs before serious attempts.'],
 'global-quick-ramp':['Keep your warm-up routine','Use the time you save for one focused practice block.'],
 'global-correction-load':['Make the first shot count','Use slower confirmation drills and focus on cleaner first hits.'],
 'global-clean-conversion':['Build on clean hits','Look for progress through pace and target planning.'],
 'global-hesitation-load':['Commit to the next target','Try short decision drills without double-checking each aim.'],
 'global-precision-ahead-of-tempo':['Add a little speed','Pair a reliable accuracy scenario with a faster contrast.'],
 'global-tempo-ahead-of-precision':['Keep speed under control','Reduce pace until first-shot quality holds steady.'],
 'global-control-foundation':['Challenge your control','Increase one demand: target size, speed, or switching.'],
 'global-learning-efficiency-low':['Simplify your practice','Keep fewer scenarios, stable blocks, and one main focus.'],
 'global-learning-efficiency-strong':['Raise one challenge','Keep the same routine while increasing one demand.'],
 'global-practice-density':['Split up long sessions','Use shorter sessions to keep the quality of your practice.'],
 'global-practice-cadence':['Keep your practice rhythm','Adjust one demand at a time while keeping your routine.'],
 'global-family-narrow':['Add some contrast','Keep your main task and add a different type of scenario.'],
 'global-family-balanced':['Keep a balanced mix','Choose one main focus while preserving your contrast work.'],
 'global-block-fade':['Stop before quality fades','Use the middle of your block for scoring, then ease off.'],
 'global-block-stability':['Make later runs count','Use your endurance for deliberate practice, not extra volume.'],
 'global-switch-cost':['Stay for a few runs','Try 2-3 runs on each task before switching scenarios.'],
 'global-switch-strength':['Use contrasting tasks','Judge your variety by the quality you keep next session.'],
 'global-retention-weak':['Repeat a familiar routine','Keep your main scenarios and block structure consistent.'],
 'global-retention-strong':['Try a harder variation','Keep your routine and rotate a little more difficulty.'],
 'global-momentum-up':['Build on recent progress','Raise difficulty slightly or choose one technical focus.'],
 'global-momentum-down':['Reset your practice','Simplify your scenario rotation and return to one focus.'],
 'global-variance-high':['Make your setup repeatable','Keep the same warm-up, setup, and first-block focus.'],
 'global-stable':['Change one thing at a time','Keep your routine and add a small, deliberate challenge.']
};
function measuredAxes(profile:any){return (profile.axes||[]).filter((axis:any)=>{const measured=profile.metrics&&profile.metrics[axisMetric[axis.key]];return typeof measured==='number'&&Number.isFinite(measured)&&typeof axis.valuePct==='number'&&Number.isFinite(axis.valuePct)&&axis.valuePct>=0&&axis.valuePct<=100;});}
function render(container:HTMLElement,state:any,onScenario?:(name:string)=>void,onDrillSearch?:(query:string)=>void){
 const host=container as any;const view=host.aimmodCoachingView||{scope:'all',expanded:{},input:null,results:{}};host.aimmodCoachingView=view;
 view.redraw=null;view.observed=view.observed||{};view.feedback=view.feedback||{feedback:[],history:[]};
 function send(body:any,done:()=>void){if(view.busy||!window.XMLHttpRequest)return;view.busy=true;const xhr=new XMLHttpRequest();const path=window.location.pathname;const base=path.slice(0,path.lastIndexOf('/'));xhr.open(body?'POST':'GET',base+'/coaching-feedback',true);xhr.timeout=12000;if(body){xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');}let finished=false;function finish(ok:boolean){if(finished)return;finished=true;view.busy=false;if(ok){try{const data=JSON.parse(xhr.responseText);if(!Array.isArray(data.feedback)||!Array.isArray(data.history))throw new Error();view.feedback=data;view.feedbackLoaded=true;view.feedbackError=false;}catch(e){ok=false;}}if(!ok)view.feedbackError=true;done();if(view.redraw)view.redraw();}xhr.onreadystatechange=()=>{if(xhr.readyState===4)finish(xhr.status===200);};xhr.onerror=xhr.ontimeout=()=>finish(false);xhr.send(body?JSON.stringify(body):null);}
 function mark(scope:string,id:string,feedback:string){send({action:'feedback',scope,id,feedback},()=>{});}
 function feedbackFor(scope:string,id:string){return view.feedback.feedback.filter((x:any)=>x.scope===scope&&x.id===id)[0]?.feedback;}
 function history(parent:HTMLElement,scope:string){const group=node(parent,'div',undefined,'coach-history');const toggle=button(group,view.historyOpen?'Close advice history':'Advice history',()=>{view.historyOpen=!view.historyOpen;draw();});toggle.setAttribute('aria-expanded',String(!!view.historyOpen));if(!view.historyOpen)return;const previous=view.feedback.history.filter((x:any)=>x.scope===scope).slice(0,20);if(!previous.length)node(group,'p','Previous advice will appear here after you review a recommendation.');previous.forEach((entry:any)=>{const row=node(group,'div',undefined,'coach-history-entry');node(row,'span',String(entry.seenAt).slice(0,10),'coach-axis-caption');node(row,'h4',entry.title);node(row,'p',entry.tip);if(feedbackFor(scope,entry.id)==='not_for_me'){const restore=button(row,'Restore advice',()=>mark(scope,entry.id,'none'));restore.disabled=!!view.busy;}});}
 const input=state.coachingHistory||[];if(view.input!==input){view.input=input;view.results={};}
 if(!state.selectedScenario)view.scope='all';
 function button(parent:HTMLElement,label:string,action:()=>void,cls='button'){const b=node(parent,'button',label,cls) as HTMLButtonElement;b.type='button';b.onclick=action;return b;}
 function draw(){container.textContent='';const shell=node(container,'div',undefined,'coach-workspace');const toolbar=node(shell,'div',undefined,'coach-toolbar');const scopes=node(toolbar,'div',undefined,'coach-scopes');
  for(const item of [{key:'all',label:'All practice'},{key:'scenario',label:'This scenario'}]){const b=button(scopes,item.label,()=>{view.scope=item.key;draw();},'button'+(view.scope===item.key?' primary':''));b.setAttribute('aria-pressed',String(view.scope===item.key));b.disabled=item.key==='scenario'&&!state.selectedScenario;}
  const scenario=view.scope==='scenario'?String(state.selectedScenario).trim().toLowerCase():undefined;
  const cacheKey=scenario?'scenario:'+scenario:'all';const result=view.results[cacheKey]||(view.results[cacheKey]=analyze(input,scenario));
  node(toolbar,'span',result.samples+' runs','coach-sample');
  if(scenario)node(shell,'p',state.selectedScenario,'coach-scope-name');
  if(!result.profile){const empty=node(shell,'div',undefined,'coach-empty');node(empty,'h3','Build your practice profile');node(empty,'p','Complete a few more runs to reveal a useful next focus.');history(shell,cacheKey);return;}
  const profile=result.profile,axes=measuredAxes(profile);const validKeys=axes.map((a:any)=>a.key);
  const strength=profile.strengths.filter(s=>validKeys.indexOf(s.key)>=0)[0];const focus=profile.constraints.filter(s=>validKeys.indexOf(s.key)>=0)[0];
  const lead=node(shell,'div',undefined,'coach-summary');node(lead,'span','NEXT BLOCK','coach-eyebrow');
  node(lead,'h2',focus?'Focus on '+focus.label.toLowerCase():'Keep building your baseline');node(lead,'p',focus?(focusCopy[focus.key]||'Choose one clear focus for your next block.'):'Keep your routine steady and change one demand at a time.');
  const columns=node(shell,'div',undefined,'coach-columns');const overview=node(columns,'div',undefined,'coach-profile');overview.style.width='43%';overview.style.marginRight='3%';const advice=node(columns,'div',undefined,'coach-advice');advice.style.width='54%';
  const signalRow=node(overview,'div',undefined,'coach-signal-row');
  for(const item of [{title:'Strength',signal:strength,cls:'coach-strength'},{title:'Focus next',signal:focus,cls:'coach-focus'}]){const tile=node(signalRow,'div',undefined,'coach-signal '+item.cls);tile.style.width='48%';node(tile,'span',item.title,'coach-eyebrow');node(tile,'strong',item.signal?item.signal.label:'Still developing');}
  const indicators=node(overview,'div',undefined,'coach-axes');node(indicators,'h3','Practice signals');node(indicators,'p','Relative indicators from your recorded practice.','coach-axis-caption');
  axes.forEach((axis:any)=>{const row=node(indicators,'div',undefined,'coach-axis');const heading=node(row,'div',undefined,'coach-axis-heading');node(heading,'span',axis.label);node(heading,'span',Math.round(axis.valuePct)+' / 100','coach-axis-value');const track=node(row,'div',undefined,'coach-axis-track');track.setAttribute('role','img');track.setAttribute('aria-label',axis.label+': '+Math.round(axis.valuePct)+' of 100');const fill=node(track,'div',undefined,'coach-axis-fill');fill.style.width=axis.valuePct+'%';});
  if(!axes.length)node(indicators,'p','Practice signals will appear as more measurements become available.','coach-axis-caption');
  function recommendation(card:any,index:number){const copy=cardCopy[card.id]||[card.title,card.tip];const item=node(advice,'div',undefined,index===0?'coach-primary':'coach-more');if(index===0)node(item,'span','TRY THIS NEXT','coach-eyebrow');node(item,'h3',copy[0]);if(index===0)node(item,'p',copy[1],'coach-action');
   const details=node(item,'div',undefined,'coach-details');const detailKey=cacheKey+':'+card.id;details.style.display=view.expanded[detailKey]?'block':'none';node(details,'p',card.body);node(details,'p',card.tip,'coach-tip');
   const drills=card.drills||[];if(drills.length){const drillGroup=node(details,'div',undefined,'coach-drills');node(drillGroup,'span','Practice ideas','coach-eyebrow');drills.forEach((d:any)=>{if(onDrillSearch)button(drillGroup,d.label,()=>onDrillSearch(d.query),'button coach-drill');else node(drillGroup,'p',d.label+' - '+d.query);});}
   const actions=node(item,'div',undefined,'coach-feedback');const useful=button(actions,feedbackFor(cacheKey,card.id)==='helpful'?'Marked useful':'Useful',()=>mark(cacheKey,card.id,feedbackFor(cacheKey,card.id)==='helpful'?'none':'helpful'));useful.setAttribute('aria-pressed',String(feedbackFor(cacheKey,card.id)==='helpful'));const hide=button(actions,'Hide this advice',()=>mark(cacheKey,card.id,'not_for_me'));useful.disabled=hide.disabled=!!view.busy;
   const toggle=button(item,view.expanded[detailKey]?'Hide details':'Show details',()=>{view.expanded[detailKey]=!view.expanded[detailKey];details.style.display=view.expanded[detailKey]?'block':'none';toggle.textContent=view.expanded[detailKey]?'Hide details':'Show details';toggle.setAttribute('aria-expanded',String(!!view.expanded[detailKey]));},'button coach-details-toggle');toggle.setAttribute('aria-expanded',String(!!view.expanded[detailKey]));
  }
  const cards=result.cards.slice(0,16);const observed=JSON.stringify([cacheKey,cards.map((card:any)=>[card.id,card.title,card.body,card.tip])]);
  if(window.XMLHttpRequest&&!view.observed[observed]&&!view.busy&&!view.feedbackError){if(Object.keys(view.observed).length>=32)view.observed={};view.observed[observed]=true;send({action:'observe',scope:cacheKey,cards:cards.map((card:any)=>({id:card.id,title:(cardCopy[card.id]||[card.title])[0],body:card.body||'',tip:card.tip||''}))},()=>{});}
  if(view.feedbackError){node(advice,'p','Advice preferences could not be saved.');button(advice,'Retry preferences',()=>{view.feedbackError=false;view.observed={};send(null,()=>{});});}
  if(!window.XMLHttpRequest||view.feedbackLoaded){const visibleCards=cards.filter((card:any)=>feedbackFor(cacheKey,card.id)!=='not_for_me');visibleCards.forEach(recommendation);if(cards.length&&!visibleCards.length)node(advice,'p','Your current advice is hidden. Restore it from Advice history.');}else node(advice,'p','Loading advice…');
  history(shell,cacheKey);
  if(!result.cards.length){node(advice,'h3','Keep your next block deliberate');node(advice,'p','Choose one focus, then compare your runs.');}
 }
 view.redraw=draw;draw();if(window.XMLHttpRequest&&!view.feedbackLoaded&&!view.busy&&!view.feedbackError)send(null,()=>{});
}
window.AimModCoaching={render,analyze,measuredAxes};
