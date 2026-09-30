(function(root){
 'use strict';
 var period='all';
 function F(){return root.AimModFormat;}
 function node(tag,text,css){var n=root.document.createElement(tag);if(text!==undefined)n.textContent=text;if(css)n.className=css;return n;}
 function render(container,state,onRun,onHistory){
  container.textContent='';var toolbar=node('div',undefined,'toolbar');var heading=node('div',undefined,'stats-heading');heading.appendChild(node('h2','Movement & timing'));heading.appendChild(node('p',(state.selectedScenario?state.selectedScenario:'All scenarios')+' · '+({'7':'last 7 days','30':'last 30 days','90':'last 90 days'}[period]||'all time'),'stats-muted'));toolbar.appendChild(heading);var periods=node('div',undefined,'segmented');toolbar.appendChild(periods);
  [['7','7 days'],['30','30 days'],['90','90 days'],['all','All time']].forEach(function(pair){var b=node('button',pair[1],'button'+(pair[0]===period?' primary':''));b.type='button';b.setAttribute('aria-pressed',String(pair[0]===period));b.onclick=function(){period=pair[0];render(container,state,onRun,onHistory);};periods.appendChild(b);});container.appendChild(toolbar);
  var periods=state.statistics&&state.statistics.Periods||[],selected=periods.filter(function(p){return p.Key===period;})[0];
  var summaries=selected&&selected.Selected?selected.Selected.Measurements||[]:[];
  var section=node('div');container.appendChild(section);if(summaries.some(function(s){return s&&(s.Key==='AverageTimeToKillMs'||s.Key==='AverageFireToHitMs')&&s.Average>=5000;}))section.appendChild(node('p','Long times to kill and shot-to-hit intervals are normal on tracking and high-health scenarios.','notice mechanics-note'));if(root.AimModStatistics)root.AimModStatistics.renderMeasurements(section,summaries);
  var now=Date.now();
  var runs=(state.mechanics||[]).filter(function(r){if(!r)return false;if(period==='all')return true;var stamp=F().parse(r.Timestamp);return !!stamp&&now-stamp.getTime()<=Number(period)*86400000;});
  var panel=node('div',undefined,'panel mechanics-runs');panel.appendChild(node('h2','Explore a recorded run'));
  if(runs.length){panel.appendChild(node('p','Open a run to see its timeline, shots and target responses.','subtle'));runs.slice(0,8).forEach(function(r){var b=node('button',undefined,'mechanics-run');b.type='button';b.appendChild(node('span',F().dateTime(r.Timestamp)));b.appendChild(node('strong',F().known(r.Smoothness)?'Control '+F().number(r.Smoothness,1):'Control —'));b.appendChild(node('span',(F().known(r.Score)?F().number(r.Score)+' score · ':'')+'Open run'));b.setAttribute('aria-label','Open run from '+F().dateTime(r.Timestamp));b.onclick=function(){onRun(r);};panel.appendChild(b);});}
  else {panel.appendChild(node('p',summaries.some(function(s){return s.Samples>0;})?'These runs have summary measurements. Browse history to inspect the data available for an individual run.':'Choose another period or browse history for a run with movement measurements.','subtle'));var row=node('div',undefined,'actions');var history=node('button','Browse scenario history','button');history.type='button';history.onclick=onHistory;row.appendChild(history);panel.appendChild(row);}
  container.appendChild(panel);
 }
 root.AimModMechanics={render:render};
})(window);
