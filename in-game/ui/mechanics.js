(function(root){
 'use strict';
 var period='all';
 function node(tag,text,css){var n=root.document.createElement(tag);if(text!==undefined)n.textContent=text;if(css)n.className=css;return n;}
 function render(container,state,onRun,onHistory){
  container.textContent='';var toolbar=node('div',undefined,'toolbar');toolbar.appendChild(node('h2','Movement & timing'));
  [['7','7 days'],['30','30 days'],['90','90 days'],['all','All time']].forEach(function(pair){var b=node('button',pair[1],'button'+(pair[0]===period?' primary':''));b.onclick=function(){period=pair[0];render(container,state,onRun,onHistory);};toolbar.appendChild(b);});container.appendChild(toolbar);
  var periods=state.statistics&&state.statistics.Periods||[],selected=periods.filter(function(p){return p.Key===period;})[0];
  var summaries=selected&&selected.Selected?selected.Selected.Measurements||[]:[];
  var section=node('div');container.appendChild(section);if(root.AimModStatistics)root.AimModStatistics.renderMeasurements(section,summaries);
  var runs=(state.mechanics||[]).filter(function(r){if(period==='all')return true;var stamp=Date.parse(r.Timestamp.replace(/^(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2})$/,'$1-$2-$3T$4:$5:$6'));return isFinite(stamp)&&Date.now()-stamp<=Number(period)*86400000;});
  var panel=node('div',undefined,'panel mechanics-runs');panel.appendChild(node('h2','Explore a recorded run'));
  if(runs.length){panel.appendChild(node('p','Open a run to inspect its timeline, shots and target responses.','subtle'));runs.slice(0,8).forEach(function(r){var b=node('button',undefined,'mechanics-run');b.appendChild(node('span',r.Timestamp.slice(0,10)));b.appendChild(node('strong','Control '+Number(r.Smoothness).toFixed(1)));b.appendChild(node('span','Open run →'));b.onclick=function(){onRun(r);};panel.appendChild(b);});}
  else {panel.appendChild(node('p',summaries.some(function(s){return s.Samples>0;})?'These runs have summary measurements. Browse history to inspect the data available for an individual run.':'Choose another period or browse history for a run with movement measurements.','subtle'));var history=node('button','Browse scenario history','button');history.onclick=onHistory;panel.appendChild(history);}
  container.appendChild(panel);
 }
 root.AimModMechanics={render:render};
})(window);
