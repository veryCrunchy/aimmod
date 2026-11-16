(function(root){
'use strict';
function node(tag,cls,text){var e=document.createElement(tag);e.className=cls||'';if(text!==undefined)e.textContent=text;return e;}
function known(n){return typeof n==='number'&&isFinite(n);}
function number(n){return known(n)?(root.AimModFormat?root.AimModFormat.number(n,1):String(Math.round(n*10)/10)):'Unknown';}
function signed(n){return (n>0?'+':'')+number(n);}
function renderScenario(container,data){
 if(!container)return;container.textContent='';var s=data&&data.insights?data.insights.scenario:data;
 var panel=node('div','panel insight-panel');container.appendChild(panel);panel.appendChild(node('h2','','Where you stand'));
 if(!s||!s.Typical||!known(s.Typical.Value)){panel.appendChild(node('p','subtle','Complete 10 scored runs on this scenario to see your range.'));return;}
 panel.appendChild(node('p','subtle',s.Name+' | Last '+s.Samples+' scored runs'));
 var values=[s.Floor.Value,s.Typical.Value,s.Peak.Value,s.PersonalBest,s.Latest].filter(known),lo=Math.min.apply(null,values),hi=Math.max.apply(null,values),span=hi-lo;
 function x(v){return span>0?5+90*(v-lo)/span:50;}
 var bar=node('div','insight-bar');bar.setAttribute('role','img');bar.setAttribute('aria-label','Floor '+number(s.Floor.Value)+', typical '+number(s.Typical.Value)+', peak '+number(s.Peak.Value)+', personal best '+number(s.PersonalBest)+', latest '+number(s.Latest)+' points');
 var range=node('div','insight-range');range.style.left=x(s.Floor.Value)+'%';range.style.width=(x(s.Peak.Value)-x(s.Floor.Value))+'%';bar.appendChild(range);
 function mark(v,cls,label){if(!known(v))return;var m=node('div','insight-marker '+cls);m.style.left=x(v)+'%';m.title=label+' '+number(v)+' points';bar.appendChild(m);}
 mark(s.Typical.Value,'typical','Typical');mark(s.PersonalBest,'best','Personal best');mark(s.Latest,'latest','Latest');panel.appendChild(bar);
 var labels=node('div','insight-labels');['Floor','Typical','Peak'].forEach(function(key){var b=s[key],item=node('div','');item.appendChild(node('strong','',key+' '+number(b.Value)+' pts'));item.appendChild(node('p','subtle',known(b.ChangePoints)?(b.ChangePoints>0?'\u2191 Up ':b.ChangePoints<0?'\u2193 Down ':'Unchanged ')+number(Math.abs(b.ChangePoints))+' pts vs previous '+s.PreviousSamples+' runs':'Needs 10 previous runs'));labels.appendChild(item);});panel.appendChild(labels);
 panel.appendChild(node('p','subtle','PB '+number(s.PersonalBest)+' pts (amber) | Latest '+number(s.Latest)+' pts (white) | Range spread '+number(s.SpreadPercent)+'% of typical'));
 var learning=s.Learning||{},sentence=learning.Statement||'Not enough runs for a learning rate.';
 if(known(learning.PointsPer10Runs))sentence+=' '+signed(learning.PointsPer10Runs)+' pts per 10 runs.';
 if(known(learning.PointsPerWeek))sentence+=' '+signed(learning.PointsPerWeek)+' pts per week.';
 if(s.Plateau&&s.Plateau.Detected)sentence+=' Typical has not improved by 1% for '+s.Plateau.Runs+' runs ('+number(s.Plateau.Days)+' days).';
 panel.appendChild(node('p','',sentence));
 panel.appendChild(node('p','subtle',number(s.GapToBestPercent)+'% below PB | Within 2%: '+s.Within2Percent+'/'+s.NearBestSamples+' runs | Within 5%: '+s.Within5Percent+'/'+s.NearBestSamples+' runs'));
}
function renderPractice(container,data){
 if(!container)return;container.textContent='';var tiles=data&&data.insights?data.insights.practice:data;
 container.appendChild(node('h2','','How you practice'));container.appendChild(node('p','subtle','Scores are compared with each scenario\'s own typical. Patterns describe your history; try small changes and compare.'));
 var row=node('div','insight-tiles');container.appendChild(row);
 (tiles||[]).forEach(function(t){var low=t.Confidence==='low'||!known(t.Value),tile=node('div','panel insight-tile'+(low?' insight-low':''));tile.appendChild(node('h3','',t.Label));tile.appendChild(node('strong','',low?'Needs more runs':t.Unit==='local hour'?number(t.Value)+':00':signed(t.Value)+' '+t.Unit));tile.appendChild(node('p','subtle',t.Samples+' samples'+(t.ComparisonSamples?' / '+t.ComparisonSamples+' comparison runs':'')));tile.appendChild(node('p','',t.Action));row.appendChild(tile);});
}
root.AimModInsights={renderScenario:renderScenario,renderPractice:renderPractice};
})(window);
