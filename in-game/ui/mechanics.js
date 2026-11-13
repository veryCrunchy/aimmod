(function(root){
 'use strict';
 var period='all',lastContainer=null,lastSignature='';
 function F(){return root.AimModFormat;}
 function node(tag,text,css){var n=root.document.createElement(tag);if(text!==undefined)n.textContent=text;if(css)n.className=css;return n;}
 function render(container,state,onRun,onHistory){
  var signature=JSON.stringify([period,state&&state.selectedScenario,state&&state.statistics,state&&state.mechanics,container.offsetWidth]);if(container===lastContainer&&signature===lastSignature&&container.children.length&&(!root.AimModPractice||root.AimModPractice.active(container)))return;lastContainer=container;lastSignature=signature;
  if(root.AimModPractice)root.AimModPractice.dispose(container);container.textContent='';if(!state){container.appendChild(node('p','Loading movement measurements...','state loading'));return;}var toolbar=node('div',undefined,'toolbar');var heading=node('div',undefined,'stats-heading');heading.appendChild(node('h2','Movement & timing'));heading.appendChild(node('p',(state.selectedScenario?state.selectedScenario:'All scenarios')+' · '+({'7':'last 7 days','30':'last 30 days','90':'last 90 days'}[period]||'all time'),'stats-muted'));toolbar.appendChild(heading);var periods=node('div',undefined,'segmented');toolbar.appendChild(periods);
  [['7','7 days'],['30','30 days'],['90','90 days'],['all','All time']].forEach(function(pair){var b=node('button',pair[1],'button'+(pair[0]===period?' primary':''));b.type='button';b.setAttribute('aria-pressed',String(pair[0]===period));b.onclick=function(){period=pair[0];render(container,state,onRun,onHistory);};periods.appendChild(b);});container.appendChild(toolbar);
  var periods=state.statistics&&state.statistics.Periods||[],selected=periods.filter(function(p){return p.Key===period;})[0];
  var summaries=selected&&selected.Selected?selected.Selected.Measurements||[]:[];
  var section=node('div');container.appendChild(section);if(summaries.some(function(s){return s&&(s.Key==='AverageTimeToKillMs'||s.Key==='AverageFireToHitMs')&&s.Average>=5000;}))section.appendChild(node('p','Long times to kill and shot-to-hit intervals are normal on tracking and high-health scenarios.','notice mechanics-note'));if(root.AimModStatistics)root.AimModStatistics.renderMeasurements(section,summaries);
  var now=Date.now();
  var runs=(state.mechanics||[]).filter(function(r){if(!r)return false;if(r.Scenario&&state.selectedScenario&&r.Scenario.toLowerCase()!==state.selectedScenario.toLowerCase())return false;if(period==='all')return true;var stamp=F().parse(r.Timestamp);return !!stamp&&now-stamp.getTime()<=Number(period)*86400000;});
  var panel=node('div',undefined,'panel mechanics-runs');panel.appendChild(node('h2','Explore a recorded run'));
  if(runs.length){panel.appendChild(node('p','Open a run to see its timeline, shots and target responses.','subtle'));runs.slice(0,8).forEach(function(r){var b=node('button',undefined,'mechanics-run');b.type='button';b.appendChild(node('span',F().dateTime(r.Timestamp)));b.appendChild(node('strong',F().known(r.Smoothness)?'Control '+F().number(r.Smoothness,1):'Control —'));b.appendChild(node('span',(F().known(r.Score)?F().number(r.Score)+' score · ':'')+'Open run'));b.setAttribute('aria-label','Open run from '+F().dateTime(r.Timestamp));b.onclick=function(){onRun(r);};panel.appendChild(b);});}
  else {panel.appendChild(node('p',summaries.some(function(s){return s.Samples>0;})?'Browse history to inspect a single run.':'Choose another period or browse history for a run with movement measurements.','subtle'));var row=node('div',undefined,'actions');var history=node('button','Browse scenario history','button');history.type='button';history.onclick=onHistory;row.appendChild(history);panel.appendChild(row);}
  container.appendChild(panel);if(root.AimModPractice){var practice=node('div',undefined,'panel');container.appendChild(practice);root.AimModPractice.summary(practice,'map');}
 }
 root.AimModMechanics={render:render};
})(window);

(function(root){
 'use strict';
 function known(v){return typeof v==='number'&&isFinite(v);}
 var bestState=null,bestMap=null;
 function bests(state){if(bestState===state&&bestMap)return bestMap;var map={};(state.scenarios||[]).forEach(function(s){map['$'+s.name.trim().toLowerCase()]=s.best;});bestState=state;bestMap=map;return map;}
 function isBest(run,state){var best=bests(state)['$'+String(run.Scenario||run.normalizedScenario||'').trim().toLowerCase()];var score=run.Score===undefined?run.score:run.Score;return known(score)&&known(best)&&score===best;}
 function summary(state,now){
  var F=root.AimModFormat,periods=state.statistics&&state.statistics.Periods||[],all=periods.filter(function(p){return !p.Days;})[0],week=periods.filter(function(p){return p.Key==='7';})[0];
  var calendar=all&&all.Calendar||week&&week.Calendar||[],days={},today=new Date(now||Date.now());today.setHours(12,0,0,0);
  calendar.forEach(function(d){days[d.Date]=d;});var key=F.dayKey(today),minutes=days[key]?days[key].Minutes:0,streak=0,cursor=new Date(today.getTime());
  if(!days[key]||!days[key].Runs)cursor.setDate(cursor.getDate()-1);
  while(days[F.dayKey(cursor)]&&days[F.dayKey(cursor)].Runs){streak++;cursor.setDate(cursor.getDate()-1);}
  var names={};(state.scenarios||[]).forEach(function(s){names['$'+s.name.trim().toLowerCase()]=s.name;});
  var recent=(state.coachingHistory||[]).filter(function(r){return r&&known(r.timestampMs)&&r.timestampMs<=today.getTime()+43200000;}).slice().sort(function(a,b){return b.timestampMs-a.timestampMs;}).map(function(r){return {Id:r.id,Scenario:names['$'+r.normalizedScenario]||r.normalizedScenario,Timestamp:new Date(r.timestampMs).toISOString(),Score:r.score,Accuracy:r.accuracy,Duration:r.duration_secs};});
  var pbs=recent.filter(function(r){return isBest(r,state);}),seen={},weekPbs=0;
  pbs.forEach(function(r){var k='$'+r.Scenario;if(!seen[k]&&new Date(r.Timestamp).getTime()>=(now||Date.now())-7*86400000){seen[k]=true;weekPbs++;}});
  return {minutes:minutes,streak:streak,week:week,pbs:pbs,weekPbs:weekPbs,recent:recent};
 }
 function sorted(runs,key,descending){return (runs||[]).map(function(r,i){return {run:r,index:i};}).sort(function(a,b){var x=a.run[key],y=b.run[key];if(key==='Timestamp'){x=root.AimModFormat.parse(x);y=root.AimModFormat.parse(y);x=x?x.getTime():null;y=y?y.getTime():null;}if(x===null||x===undefined)return y===null||y===undefined?a.index-b.index:1;if(y===null||y===undefined)return -1;var result=typeof x==='string'?x.toLowerCase().localeCompare(String(y).toLowerCase()):x-y;return result?(descending?-result:result):a.index-b.index;}).map(function(x){return x.run;});}
 root.AimModImprove={summary:summary,isBest:isBest,sorted:sorted};
})(window);
