(function(global){
  'use strict';
  var host,transport,runId,data,preview,tab='summary',page=0,generation=0,metric='ScorePerMinute';
  var METRICS=[['ScorePerMinute','Pace','Score per minute'],['Accuracy','Accuracy','Accuracy (%)'],['KillsPerSecond','Kills/sec','Kills per second'],['DamageEfficiency','Damage efficiency','Damage efficiency']];
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
  function table(parent,columns,rows){var table=add(parent,node('div',undefined,'run-table'));table.setAttribute('role','table');
    function row(values,heading){var r=add(table,node('div',undefined,'run-row'+(heading?' run-head':'')));r.setAttribute('role','row');values.forEach(function(v,i){var cell=add(r,node('div',String(v),'run-cell'+(columns[i][2]?' run-number':'')));cell.style.width=columns[i][1]+'%';cell.setAttribute('role',heading?'columnheader':'cell')})}
    row(columns.map(function(c){return c[0]}),true);rows.forEach(function(r){row(r,false)});return table;
  }
  function problem(title,body){host.textContent='';var box=add(host,node('div',undefined,'empty'));add(box,node('h3',title));add(box,node('p',body));var actions=add(box,node('div',undefined,'run-tabs run-actions'));add(actions,button('Try again',load,true));if(global.AimModWorkspace)add(actions,button('Back to history',function(){global.AimModWorkspace.open('history')}))}
  function load(){var requestGeneration=++generation;host.textContent='';text(host,'Loading run analysis…');
    transport('run-details/'+encodeURIComponent(runId)+'?shotPage='+page,'GET',null,function(ok,value){
      if(requestGeneration!==generation)return;host.textContent='';
      if(!ok){
        if(preview){data={Run:preview,Details:{Summary:null,Timeline:[]},Response:null,Episodes:[],EpisodeCount:0,Windows:[],WindowCount:0,Shots:[],ShotCount:0,ShotPage:0,ShotPages:1,MissingTelemetry:true};render()}
        else problem('Run analysis unavailable','Detailed telemetry is unavailable for this run. Your history entry is kept.');return;
      }
      try{data=JSON.parse(value);if(!data||!data.Run)throw Error();render()}catch(e){problem('Could not read this run','The run data could not be read. Try opening it again.')}
    });
  }
  function graph(canvas,points){var w=canvas.offsetWidth,h=230;if(!w)return;var ratio=global.devicePixelRatio||1;canvas.width=w*ratio;canvas.height=h*ratio;var c=canvas.getContext('2d');c.scale(ratio,ratio);
    points=(points||[]).filter(function(p){return p&&F().known(p.Time)});
    var values=points.map(function(p){return p[metric]}).filter(function(v){return typeof v==='number'&&isFinite(v)});if(!values.length){c.fillStyle='#a4b3b8';c.font='14px Arial';c.fillText('No measured timeline for this metric',18,110);return}
    var axis=metric==='Accuracy'?F().ticks(0,100,4):F().ticks(Math.min(0,Math.min.apply(null,values)),Math.max(1e-6,Math.max.apply(null,values)*1.1),4),lo=axis.min,hi=axis.max,end=Math.max(1,points[points.length-1].Time),left=60,right=w-20,top=15,bottom=h-28;
    function y(v){return bottom-(v-lo)/(hi-lo)*(bottom-top)}
    c.font='12px Arial';c.textAlign='right';axis.values.forEach(function(v){c.strokeStyle='#2a3a3f';c.beginPath();c.moveTo(left,y(v));c.lineTo(right,y(v));c.stroke();c.fillStyle='#9aacb3';c.fillText(F().tick(v,axis),left-8,y(v)+4)});
    c.beginPath();var connected=false;points.forEach(function(p){var v=p[metric];if(typeof v!=='number'||!isFinite(v)){connected=false;return}var x=left+p.Time/end*(right-left);if(connected)c.lineTo(x,y(v));else c.moveTo(x,y(v));connected=true});c.strokeStyle='#27e4a1';c.lineWidth=2;c.stroke();
    // Mark the average so the line has a reference point.
    var average=values.reduce(function(a,b){return a+b},0)/values.length;c.strokeStyle='#e8b667';c.lineWidth=1;if(c.setLineDash)c.setLineDash([4,4]);c.beginPath();c.moveTo(left,y(average));c.lineTo(right,y(average));c.stroke();if(c.setLineDash)c.setLineDash([]);
    c.fillStyle='#9aacb3';c.textAlign='left';c.fillText('0s',left,h-7);c.textAlign='right';c.fillText(F().duration(end),right,h-7);
    return average;
  }
  function render(){host.textContent='';var run=data.Run;var head=add(host,node('div',undefined,'run-header'));var identity=add(head,node('div',undefined,'run-identity'));add(identity,node('h2',run.Scenario||'Unknown scenario'));
    add(identity,node('p',[F().dateTime(run.Timestamp),F().known(run.Duration)?F().duration(run.Duration)+' run':''].filter(function(v){return v&&v!=='—'}).join(' · '),'run-muted'));
    var score=add(head,node('div',undefined,'run-score'));add(score,node('span','Score','metric-label'));add(score,node('strong',n(run.Score)));
    if(data.MissingTelemetry)add(host,node('p','This score is saved in your history. Detailed telemetry was not recorded for this run.','notice run-notice'));
    var tabs=add(host,node('div',undefined,'run-tabs'));tabs.setAttribute('role','tablist');[['summary','Summary'],['shots','Shots'],['responses','Target responses'],['windows','Key moments']].forEach(function(item){var b=add(tabs,button(item[1],function(){tab=item[0];render()},tab===item[0]));b.setAttribute('role','tab');b.setAttribute('aria-selected',String(tab===item[0]))});
    if(tab==='summary'){
      var s=data.Details&&data.Details.Summary||{};var metrics=add(host,node('div',undefined,'run-metrics'));
      card(metrics,'Accuracy',F().percent(run.Accuracy),F().known(s.ShotsHit)&&F().known(s.ShotsFired)?n(s.ShotsHit,0)+' hits of '+n(s.ShotsFired,0)+' shots':undefined);card(metrics,'Kill rate',n(s.KillsPerSecond,2),'Kills per second');card(metrics,'Shots per hit',n(s.AverageShotsToHit,2),'Lower means fewer extra shots');
      card(metrics,'Shot-to-hit interval',F().millis(s.AverageFireToHitMs),'Shot to registered hit · not input latency');card(metrics,'Corrective shots',F().known(s.CorrectiveShotRatio)?F().percent(s.CorrectiveShotRatio*100):'—','Follow-up shots after a miss');card(metrics,'Duration',F().duration(run.Duration));
      var p=add(host,panel('Run timeline'));var controls=add(p,node('div',undefined,'run-tabs'));
      METRICS.forEach(function(item){var b=add(controls,button(item[1],function(){metric=item[0];render()},metric===item[0]));b.setAttribute('aria-pressed',String(metric===item[0]))});
      var selected=METRICS.filter(function(m){return m[0]===metric})[0]||METRICS[0];var caption=text(p,selected[2]+' through the run. Dashed line: run average.');
      var canvas=add(p,node('canvas',undefined,'run-chart'));setTimeout(function(){var average=graph(canvas,data.Details&&data.Details.Timeline||[]);if(F().known(average))caption.textContent=selected[2]+' through the run. Dashed line: run average ('+n(average,metric==='KillsPerSecond'?2:1)+(metric==='Accuracy'?'%':'')+').';else caption.textContent=selected[2]+' through the run.'},0);
    }else if(tab==='responses'){
      var response=data.Response;if(!response){text(host,'No target response analysis was recorded for this run.');return}
      var summary=add(host,node('div',undefined,'run-metrics'));card(summary,'Responses',n(response.Episodes,0));card(summary,'Reaction',F().millis(response.ReactionMs),'Time to start correcting');card(summary,'Recovery',F().millis(response.RecoveryMs),'Time to settle back on target');card(summary,'Stable responses',F().known(response.StableRatio)?F().percent(response.StableRatio*100):'—');
      var episodes=data.Episodes||[];text(host,'Showing '+n(episodes.length,0)+' of '+n(data.EpisodeCount,0)+' recorded responses.');
      if(episodes.length)table(host,[['Time',11,1],['Type',15],['Target',22],['Reaction',13,1],['Recovery',13,1],['Peak error',14,1],['Stable',12]],episodes.map(function(e){return [seconds(e.StartMs),label(e.Kind),e.Target||'—',F().millis(e.ReactionMs),F().millis(e.RecoveryMs),unit(e.PeakYawDegrees,'°'),e.Stable?'Yes':'No']}));
    }else if(tab==='shots'){
      var shots=data.Shots||[];text(host,n(data.ShotCount,0)+' recorded shot events. Times count from the first shot. A nearby target does not mean a hit.');
      if(!shots.length){text(host,'No shot telemetry was recorded for this run.');return}
      table(host,[['Time',13,1],['Event',18],['Count',10,1],['Nearest target',25],['Distance',12,1],['Yaw error',11,1],['Pitch error',11,1]],shots.map(function(s){var target=(s.Targets||[]).filter(function(t){return t.Nearest})[0]||{};return [seconds(s.TimestampMs-(typeof data.FirstShotTimestampMs==='number'?data.FirstShotTimestampMs:s.TimestampMs)),label(s.Kind),n(s.Count,0),target.Label||'—',n(target.Distance),unit(target.YawErrorDegrees,'°'),unit(target.PitchErrorDegrees,'°')]}));
      var pages=Math.max(1,data.ShotPages||1);if(pages>1){var paging=add(host,node('div',undefined,'run-tabs run-pager'));var previous=add(paging,button('Previous',function(){page--;load()}));previous.disabled=data.ShotPage<=0;add(paging,node('span','Page '+((data.ShotPage||0)+1)+' of '+pages,'run-muted'));var next=add(paging,button('Next',function(){page++;load()}));next.disabled=(data.ShotPage||0)+1>=pages}
    }else{
      var windows=data.Windows||[];text(host,'Showing '+n(windows.length,0)+' of '+n(data.WindowCount,0)+' key moments.');
      if(!windows.length){text(host,'No key moments were recorded for this run.');return}
      windows.forEach(function(w){var p=add(host,panel(w.Label||label(w.Kind)));text(p,seconds(w.StartMs)+' – '+seconds(w.EndMs)+(w.Target?' · '+w.Target:''));var m=add(p,node('div',undefined,'run-metrics'));card(m,'Accuracy',F().percent(w.Accuracy));card(m,'Shots',n(w.Fired,0));card(m,'Hits',n(w.Hits,0));card(m,'Pace',n(w.ScorePerMinute),'Score per minute')});
    }
  }
  global.AimModRunDetails={open:function(container,id,request,historyEntry){host=container;runId=id;transport=request;preview=historyEntry;data=null;page=0;tab='summary';load()},leave:function(){generation++},resize:function(){if(data&&host)render()}};
})(window);
