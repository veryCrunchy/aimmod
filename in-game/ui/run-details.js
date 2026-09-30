(function(global){
  'use strict';
  var host,transport,runId,data,preview,tab='summary',page=0,generation=0,metric='ScorePerMinute';
  var METRICS=[['ScorePerMinute','Pace','Score per minute'],['Accuracy','Accuracy','Accuracy (%)'],['KillsPerSecond','Kills/sec','Kills per second'],['DamageEfficiency','Damage efficiency','Damage efficiency']];
  var COLORS={line:'#27e4a1',avg:'#f0b45a',good:'rgba(39,228,161,0.16)',weak:'rgba(240,180,90,0.18)',grid:'#22302a',text:'#a7bab0',amber:'#f0b45a',cyan:'#66ccff'};
  function F(){return global.AimModFormat}
  function node(tag,text,cls){var e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;return e}
  function add(parent,child){parent.appendChild(child);return child}
  function n(v,d){return F().number(v,d)}
  function unit(v,suffix,d){return F().unit(v,suffix,d)}
  function seconds(ms){return F().known(ms)?F().fixed(ms/1000,2)+'s':'—'}
  function label(kind){return typeof kind==='string'&&kind?kind.replace(/_/g,' '):'—'}
  function button(text,fn,primary){var e=node('button',text,'button'+(primary?' primary':''));e.type='button';e.onclick=fn;return e}
  function card(parent,title,value,note){var e=add(parent,node('div',undefined,'metric'));add(e,node('div',title,'metric-label'));add(e,node('div',value,'metric-value'));if(note)add(e,node('div',note,'metric-note'))}
  function panel(title){var e=node('div',undefined,'panel run-panel');add(e,node('h2',title));return e}
  function text(parent,value){return add(parent,node('p',value,'run-muted'))}
  function legend(parent,items){var row=add(parent,node('div',undefined,'run-legend'));items.forEach(function(item){var entry=add(row,node('span',undefined,'run-legend-item'));var swatch=add(entry,node('span',undefined,'run-legend-swatch'+(item[2]?' '+item[2]:'')));swatch.style.background=item[1];add(entry,node('span',item[0]))})}
  function table(parent,columns,rows){var table=add(parent,node('div',undefined,'run-table'));table.setAttribute('role','table');
    function row(values,heading){var r=add(table,node('div',undefined,'run-row'+(heading?' run-head':'')));r.setAttribute('role','row');values.forEach(function(v,i){var cell=add(r,node('div',String(v),'run-cell'+(columns[i][2]?' run-number':'')));cell.style.width=columns[i][1]+'%';cell.setAttribute('role',heading?'columnheader':'cell')})}
    row(columns.map(function(c){return c[0]}),true);rows.forEach(function(r){row(r,false)});return table;
  }
  function problem(title,body){host.textContent='';var box=add(host,node('div',undefined,'empty'));add(box,node('h3',title));add(box,node('p',body));var actions=add(box,node('div',undefined,'run-tabs run-actions'));add(actions,button('Try again',load,true));if(global.AimModWorkspace)add(actions,button('Back to history',function(){global.AimModWorkspace.open('history')}))}
  function load(){var requestGeneration=++generation;host.textContent='';var loading=add(host,node('div',undefined,'empty'));add(loading,node('p','Loading run analysis…'));
    transport('run-details/'+encodeURIComponent(runId)+'?shotPage='+page,'GET',null,function(ok,value){
      if(requestGeneration!==generation)return;host.textContent='';
      if(!ok){
        if(preview){data={Run:preview,Details:{Summary:null,Timeline:[]},Response:null,Episodes:[],EpisodeCount:0,Windows:[],WindowCount:0,Shots:[],ShotCount:0,ShotPage:0,ShotPages:1,MissingTelemetry:true};render()}
        else problem('Run analysis unavailable','Detailed telemetry is unavailable for this run. Your history entry is kept.');return;
      }
      try{data=JSON.parse(value);if(!data||!data.Run)throw Error();render()}catch(e){problem('Could not read this run','The run data could not be read. Try opening it again.')}
    });
  }
  function surface(canvas,height){var w=canvas.offsetWidth;if(!w)return null;var ratio=global.devicePixelRatio||1;canvas.width=w*ratio;canvas.height=height*ratio;canvas.style.height=height+'px';var c=canvas.getContext('2d');c.scale(ratio,ratio);c.clearRect(0,0,w,height);return {c:c,w:w,h:height}}
  function grid(c,axis,y,left,right,format){c.font='12px Arial';c.textAlign='right';axis.values.forEach(function(v){var yy=Math.round(y(v))+.5;c.strokeStyle=COLORS.grid;c.lineWidth=1;c.beginPath();c.moveTo(left,yy);c.lineTo(right,yy);c.stroke();c.fillStyle=COLORS.text;c.fillText(format?format(v):F().tick(v,axis),left-8,yy+4)})}
  // Whether a key moment went better or worse than the run as a whole.
  function strong(w,run){var name=(w.Kind||'')+' '+(w.Label||'')+' '+(w.Phase||'');if(/strong|best|peak|fast/i.test(name))return true;if(/weak|slow|worst|drop|fade/i.test(name))return false;if(F().known(w.ScorePerMinute)&&F().known(run.Score)&&F().known(run.Duration)&&run.Duration>0)return w.ScorePerMinute>=run.Score/run.Duration*60;if(F().known(w.Accuracy)&&F().known(run.Accuracy))return w.Accuracy>=run.Accuracy;return true}
  // Run timeline with key moments shaded behind the line and the run average dashed.
  function graph(canvas,points,windows,run){var s=surface(canvas,240);if(!s)return;var c=s.c,w=s.w,h=s.h;
    points=(points||[]).filter(function(p){return p&&F().known(p.Time)});
    var values=points.map(function(p){return p[metric]}).filter(function(v){return typeof v==='number'&&isFinite(v)});if(!values.length){c.fillStyle=COLORS.text;c.font='14px Arial';c.textAlign='center';c.fillText('No measured timeline for this metric',w/2,h/2);return}
    var axis=metric==='Accuracy'?F().ticks(0,100,4):F().ticks(Math.max(Math.min(0,Math.min.apply(null,values)),Math.min.apply(null,values)-(Math.max.apply(null,values)-Math.min.apply(null,values))*0.6),Math.max(1e-6,Math.max.apply(null,values)+(Math.max.apply(null,values)-Math.min.apply(null,values))*0.15),4),lo=axis.min,hi=axis.max,end=Math.max(1,points[points.length-1].Time),left=60,right=w-16,top=22,bottom=h-26;
    function y(v){return bottom-(v-lo)/(hi-lo)*(bottom-top)}function x(t){return left+Math.max(0,Math.min(1,t/end))*(right-left)}
    (windows||[]).forEach(function(win){if(!F().known(win.StartMs)||!F().known(win.EndMs))return;var x0=x(win.StartMs/1000),x1=Math.max(x0+2,x(win.EndMs/1000));c.fillStyle=strong(win,run)?COLORS.good:COLORS.weak;c.fillRect(x0,top,x1-x0,bottom-top);c.fillStyle=strong(win,run)?'#27e4a1':COLORS.amber;c.font='11px Arial';c.textAlign='left';c.fillText((win.Label||label(win.Kind)).slice(0,28),x0+4,top-7)});
    grid(c,axis,y,left,right);
    c.beginPath();var connected=false;points.forEach(function(p){var v=p[metric];if(typeof v!=='number'||!isFinite(v)){connected=false;return}if(connected)c.lineTo(x(p.Time),y(v));else c.moveTo(x(p.Time),y(v));connected=true});c.strokeStyle=COLORS.line;c.lineWidth=2;c.stroke();
    var average=values.reduce(function(a,b){return a+b},0)/values.length;c.strokeStyle=COLORS.avg;c.lineWidth=1;if(c.setLineDash)c.setLineDash([4,4]);c.beginPath();c.moveTo(left,y(average));c.lineTo(right,y(average));c.stroke();if(c.setLineDash)c.setLineDash([]);
    c.fillStyle=COLORS.text;c.font='12px Arial';c.textAlign='left';c.fillText('0s',left,h-7);c.textAlign='center';c.fillText(F().duration(end/2),(left+right)/2,h-7);c.textAlign='right';c.fillText(F().duration(end),right,h-7);
    return average;
  }
  // Reaction time per target response; unstable responses in amber.
  function responseChart(canvas,episodes){var s=surface(canvas,170);if(!s)return;var c=s.c,w=s.w,h=s.h;var values=episodes.map(function(e){return e.ReactionMs}).filter(F().known);if(!values.length){c.fillStyle=COLORS.text;c.font='14px Arial';c.textAlign='center';c.fillText('No reaction times recorded',w/2,h/2);return}
    var axis=F().ticks(0,Math.max.apply(null,values)*1.1,3),left=56,right=w-12,top=10,bottom=h-24;function y(v){return bottom-v/axis.max*(bottom-top)}
    grid(c,axis,y,left,right,function(v){return n(v,0)+' ms'});var width=(right-left)/episodes.length;
    episodes.forEach(function(e,i){if(!F().known(e.ReactionMs))return;c.fillStyle=e.Stable?'#27e4a1':COLORS.amber;c.fillRect(left+i*width+1,y(e.ReactionMs),Math.max(1,width-2),bottom-y(e.ReactionMs))});
    c.fillStyle=COLORS.text;c.font='12px Arial';c.textAlign='left';c.fillText('First response',left,h-6);c.textAlign='right';c.fillText('Latest',right,h-6)}
  // Aim error at each shot on this page: distance from zero is how far off the nearest target was.
  function shotChart(canvas,shots){var s=surface(canvas,170);if(!s)return;var c=s.c,w=s.w,h=s.h;var pts=[];shots.forEach(function(shot){var t=(shot.Targets||[]).filter(function(x){return x.Nearest})[0];if(t&&F().known(t.YawErrorDegrees)&&F().known(shot.TimestampMs))pts.push({t:shot.TimestampMs,v:t.YawErrorDegrees,hit:/hit/i.test(shot.Kind||'')})});
    if(!pts.length){c.fillStyle=COLORS.text;c.font='14px Arial';c.textAlign='center';c.fillText('No aim error recorded on this page',w/2,h/2);return}
    var span=Math.max(0.5,Math.max.apply(null,pts.map(function(p){return Math.abs(p.v)}))*1.15),axis=F().ticks(-span,span,4),left=56,right=w-12,top=10,bottom=h-24,t0=pts[0].t,t1=Math.max(t0+1,pts[pts.length-1].t);
    function y(v){return bottom-(v-axis.min)/(axis.max-axis.min)*(bottom-top)}function x(t){return left+(t-t0)/(t1-t0)*(right-left)}
    grid(c,axis,y,left,right,function(v){return F().tick(v,axis)+'°'});c.strokeStyle='#3f5a4d';c.beginPath();c.moveTo(left,y(0));c.lineTo(right,y(0));c.stroke();
    pts.forEach(function(p){c.beginPath();c.arc(x(p.t),y(p.v),2.6,0,Math.PI*2);c.fillStyle=p.hit?'#27e4a1':COLORS.cyan;c.fill()});
    c.fillStyle=COLORS.text;c.font='12px Arial';c.textAlign='left';c.fillText(seconds(t0-(F().known(data.FirstShotTimestampMs)?data.FirstShotTimestampMs:t0)),left,h-6);c.textAlign='right';c.fillText(seconds(t1-(F().known(data.FirstShotTimestampMs)?data.FirstShotTimestampMs:t0)),right,h-6)}
  function moments(parent,windows,run){if(!windows.length)return;var row=add(parent,node('div',undefined,'run-moments'));windows.forEach(function(w){var good=strong(w,run),item=add(row,node('div',undefined,'run-moment'+(good?' good':' weak')));add(item,node('span',good?'Went well':'Room to improve','run-moment-tag'));add(item,node('h3',w.Label||label(w.Kind)));add(item,node('p',seconds(w.StartMs)+' – '+seconds(w.EndMs)+(w.Target?' · '+w.Target:''),'run-muted'));
      var bar=add(item,node('div',undefined,'run-bar'));var fill=add(bar,node('div',undefined,'run-bar-fill'));fill.style.width=(F().known(w.Accuracy)?Math.max(0,Math.min(100,w.Accuracy)):0)+'%';
      add(item,node('p',F().percent(w.Accuracy)+' accuracy · '+n(w.Hits,0)+' of '+n(w.Fired,0)+' shots · '+n(w.ScorePerMinute,0)+' pace','run-moment-stats'))})}
  function render(){host.textContent='';var run=data.Run;var head=add(host,node('div',undefined,'run-header'));var identity=add(head,node('div',undefined,'run-identity'));add(identity,node('h2',run.Scenario||'Unknown scenario'));
    add(identity,node('p',[F().dateTime(run.Timestamp),F().known(run.Duration)?F().duration(run.Duration)+' run':''].filter(function(v){return v&&v!=='—'}).join(' · '),'run-muted'));
    var score=add(head,node('div',undefined,'run-score'));add(score,node('span','Score','metric-label'));add(score,node('strong',n(run.Score)));
    if(data.MissingTelemetry)add(host,node('p','This score is saved in your history. Detailed telemetry was not recorded for this run.','notice run-notice'));
    var tabs=add(host,node('div',undefined,'segmented run-tabbar'));tabs.setAttribute('role','tablist');[['summary','Summary'],['responses','Target responses'],['shots','Shots']].forEach(function(item){var b=add(tabs,button(item[1],function(){tab=item[0];render()},tab===item[0]));b.setAttribute('role','tab');b.setAttribute('aria-selected',String(tab===item[0]))});
    if(tab==='windows')tab='summary';
    if(tab==='summary'){
      var s=data.Details&&data.Details.Summary||{};var metrics=add(host,node('div',undefined,'metrics run-kpis'));
      card(metrics,'Accuracy',F().percent(run.Accuracy),F().known(s.ShotsHit)&&F().known(s.ShotsFired)?n(s.ShotsHit,0)+' hits of '+n(s.ShotsFired,0)+' shots':undefined);card(metrics,'Kill rate',n(s.KillsPerSecond,2),'Kills per second');card(metrics,'Shots per hit',n(s.AverageShotsToHit,2),'Lower means fewer extra shots');
      card(metrics,'Shot-to-hit interval',F().millis(s.AverageFireToHitMs),'Shot to registered hit · not input latency');card(metrics,'Corrective shots',F().known(s.CorrectiveShotRatio)?F().percent(s.CorrectiveShotRatio*100):'—','Follow-up shots after a miss');if(F().known(s.PeakScorePerMinute))card(metrics,'Peak pace',n(s.PeakScorePerMinute,0),'Best score-per-minute moment');else card(metrics,'Duration',F().duration(run.Duration));
      var p=add(host,panel('Run timeline'));var controls=add(p,node('div',undefined,'run-tabs'));
      METRICS.forEach(function(item){var b=add(controls,button(item[1],function(){metric=item[0];render()},metric===item[0]));b.className='button compact'+(metric===item[0]?' primary':'');b.setAttribute('aria-pressed',String(metric===item[0]))});
      var windows=data.Windows||[];var selected=METRICS.filter(function(m){return m[0]===metric})[0]||METRICS[0];
      legend(p,[[selected[1],COLORS.line],['Run average',COLORS.avg,'dashed']].concat(windows.length?[['Went well','#1c4a3a','block'],['Room to improve','#4a3a1f','block']]:[]));
      var caption=text(p,selected[2]+' through the run.');
      var canvas=add(p,node('canvas',undefined,'run-chart'));setTimeout(function(){var average=graph(canvas,data.Details&&data.Details.Timeline||[],windows,run);if(F().known(average))caption.textContent=selected[2]+' through the run. Run average '+n(average,metric==='KillsPerSecond'?2:1)+(metric==='Accuracy'?'%':'')+'.'},0);
      if(windows.length){var km=add(host,panel('Key moments'));text(km,'Showing '+n(windows.length,0)+' of '+n(data.WindowCount,0)+' key moments, compared with the run as a whole.');moments(km,windows,run)}
      else text(host,'No key moments were recorded for this run.');
    }else if(tab==='responses'){
      var response=data.Response;if(!response){add(host,node('div','No target response analysis was recorded for this run.','empty'));return}
      var summary=add(host,node('div',undefined,'metrics run-kpis'));card(summary,'Responses',n(response.Episodes,0),'Target switches and path changes');card(summary,'Reaction',F().millis(response.ReactionMs),'Time to start correcting');card(summary,'Recovery',F().millis(response.RecoveryMs),'Time to settle back on target');card(summary,'Stable responses',F().known(response.StableRatio)?F().percent(response.StableRatio*100):'—','Settled without overshooting');
      var episodes=data.Episodes||[];var chartPanel=add(host,panel('Reaction per response'));legend(chartPanel,[['Stable','#27e4a1'],['Unstable',COLORS.amber]]);var rc=add(chartPanel,node('canvas',undefined,'run-chart'));setTimeout(function(){responseChart(rc,episodes)},0);
      text(host,'Showing '+n(episodes.length,0)+' of '+n(data.EpisodeCount,0)+' recorded responses.');
      if(episodes.length)table(host,[['Time',11,1],['Type',15],['Target',22],['Reaction',13,1],['Recovery',13,1],['Peak error',14,1],['Stable',12]],episodes.map(function(e){return [seconds(e.StartMs),label(e.Kind),e.Target||'—',F().millis(e.ReactionMs),F().millis(e.RecoveryMs),unit(e.PeakYawDegrees,'°'),e.Stable?'Yes':'No']}));
    }else if(tab==='shots'){
      var shots=data.Shots||[];text(host,n(data.ShotCount,0)+' recorded shot events. Times count from the first shot. A nearby target does not mean a hit.');
      if(!shots.length){add(host,node('div','No shot telemetry was recorded for this run.','empty'));return}
      var sp=add(host,panel('Aim error at each shot'));legend(sp,[['Hit','#27e4a1'],['Other shot',COLORS.cyan]]);text(sp,'Horizontal distance from the nearest target in degrees. Closer to the middle line is closer to the target.');var sc=add(sp,node('canvas',undefined,'run-chart'));setTimeout(function(){shotChart(sc,shots)},0);
      table(host,[['Time',13,1],['Event',18],['Count',10,1],['Nearest target',25],['Distance',12,1],['Yaw error',11,1],['Pitch error',11,1]],shots.map(function(s){var target=(s.Targets||[]).filter(function(t){return t.Nearest})[0]||{};return [seconds(s.TimestampMs-(typeof data.FirstShotTimestampMs==='number'?data.FirstShotTimestampMs:s.TimestampMs)),label(s.Kind),n(s.Count,0),target.Label||'—',n(target.Distance),unit(target.YawErrorDegrees,'°'),unit(target.PitchErrorDegrees,'°')]}));
      var pages=Math.max(1,data.ShotPages||1);if(pages>1){var paging=add(host,node('div',undefined,'run-tabs run-pager'));var previous=add(paging,button('Previous',function(){page--;load()}));previous.disabled=data.ShotPage<=0;add(paging,node('span','Page '+((data.ShotPage||0)+1)+' of '+pages,'run-muted'));var next=add(paging,button('Next',function(){page++;load()}));next.disabled=(data.ShotPage||0)+1>=pages}
    }
  }
  global.AimModRunDetails={open:function(container,id,request,historyEntry){host=container;runId=id;transport=request;preview=historyEntry;data=null;page=0;tab='summary';load()},leave:function(){generation++},resize:function(){if(data&&host)render()}};
})(window);
