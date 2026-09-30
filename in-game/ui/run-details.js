(function(global){
  'use strict';
  var host,transport,runId,data,preview,tab='summary',page=0,generation=0,metric='ScorePerMinute';
  function node(tag,text,cls){var e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;return e}
  function add(parent,child){parent.appendChild(child);return child}
  function n(v,d){return typeof v==='number'&&isFinite(v)?v.toFixed(d===undefined?1:d).replace(/\.0$/,''):'—'}
  function seconds(ms){return n(ms/1000,2)+'s'}
  function button(label,fn,primary){var e=node('button',label,'button'+(primary?' primary':''));e.onclick=fn;return e}
  function card(parent,label,value,note){var e=add(parent,node('div',undefined,'metric'));add(e,node('div',label,'metric-label'));add(e,node('div',value,'metric-value'));if(note)add(e,node('div',note,'metric-note'))}
  function panel(title){var e=node('div',undefined,'panel run-panel');add(e,node('h2',title));return e}
  function text(parent,value){add(parent,node('p',value,'run-muted'))}
  function table(parent,columns,rows){var table=add(parent,node('div',undefined,'run-table'));table.setAttribute('role','table');
    function row(values,heading){var r=add(table,node('div',undefined,'run-row'+(heading?' run-head':'')));r.setAttribute('role','row');values.forEach(function(v,i){var cell=add(r,node('div',String(v),'run-cell'));cell.style.width=columns[i][1]+'%';cell.setAttribute('role',heading?'columnheader':'cell')})}
    row(columns.map(function(c){return c[0]}),true);rows.forEach(function(r){row(r,false)});return table;
  }
  function load(){var requestGeneration=++generation;host.textContent='';text(host,'Loading run analysis…');
    transport('run-details/'+encodeURIComponent(runId)+'?shotPage='+page,'GET',null,function(ok,value){
      if(requestGeneration!==generation)return;host.textContent='';
      if(!ok){
        if(preview){data={Run:preview,Details:{Summary:null,Timeline:[]},Response:null,Episodes:[],EpisodeCount:0,Windows:[],WindowCount:0,Shots:[],ShotCount:0,ShotPage:0,ShotPages:1,MissingTelemetry:true};render()}
        else text(host,'Detailed telemetry is unavailable for this run. Your history entry is kept.');return;
      }
      try{data=JSON.parse(value);if(!data.Run)throw Error();render()}catch(e){text(host,'Could not read this run. Try opening it again.')}
    });
  }
  function graph(canvas,points){var w=canvas.offsetWidth,h=230;if(!w)return;var ratio=global.devicePixelRatio||1;canvas.width=w*ratio;canvas.height=h*ratio;var c=canvas.getContext('2d');c.scale(ratio,ratio);
    var values=points.map(function(p){return p[metric]}).filter(function(v){return typeof v==='number'&&isFinite(v)});if(!values.length){c.fillStyle='#8daa97';c.font='14px Arial';c.fillText('No measured timeline for this metric',18,110);return}
    var hi=metric==='Accuracy'?100:Math.max(1,Math.max.apply(null,values)*1.1),end=Math.max(1,points[points.length-1].Time),left=60,right=w-20,bottom=h-28;
    c.font='11px Arial';c.textAlign='right';for(var i=0;i<=4;i++){var y=bottom-i/4*(bottom-15);c.strokeStyle='#284331';c.beginPath();c.moveTo(left,y);c.lineTo(right,y);c.stroke();c.fillStyle='#8daa97';c.fillText(n(hi*i/4,0),left-8,y+4)}
    c.beginPath();var connected=false;points.forEach(function(p){var v=p[metric];if(typeof v!=='number'){connected=false;return}var x=left+p.Time/end*(right-left),y=bottom-v/hi*(bottom-15);if(connected)c.lineTo(x,y);else c.moveTo(x,y);connected=true});c.strokeStyle='#27e4a1';c.lineWidth=2;c.stroke();c.fillStyle='#8daa97';c.textAlign='left';c.fillText('0s',left,h-7);c.textAlign='right';c.fillText(n(end,0)+'s',right,h-7);
  }
  function render(){host.textContent='';var run=data.Run;var head=add(host,node('div',undefined,'toolbar'));add(head,node('h2',run.Scenario));add(head,node('span',n(run.Score)+' points','run-muted'));
    if(data.MissingTelemetry)text(host,'This score is saved in your history. Detailed telemetry is not available locally.');
    var tabs=add(host,node('div',undefined,'run-tabs'));[['summary','Summary'],['shots','Shots'],['responses','Target responses'],['windows','Key moments']].forEach(function(item){add(tabs,button(item[1],function(){tab=item[0];render()},tab===item[0]))});
    if(tab==='summary'){
      var s=data.Details&&data.Details.Summary||{};var metrics=add(host,node('div',undefined,'run-metrics'));
      card(metrics,'Score',n(run.Score));card(metrics,'Accuracy',run.Accuracy===null?'—':n(run.Accuracy)+'%');card(metrics,'Duration',n(run.Duration)+'s');
      card(metrics,'Shots fired',n(s.ShotsFired,0));card(metrics,'Shots hit',n(s.ShotsHit,0));card(metrics,'Kill rate',n(s.KillsPerSecond,2),'Kills per second');
      card(metrics,'Fire to hit',n(s.AverageFireToHitMs)+'ms','Measured shot latency');card(metrics,'Shots per hit',n(s.AverageShotsToHit,2));card(metrics,'Corrective shots',s.CorrectiveShotRatio===null||s.CorrectiveShotRatio===undefined?'—':n(s.CorrectiveShotRatio*100)+'%');
      var p=add(host,panel('Run timeline'));var controls=add(p,node('div',undefined,'run-tabs'));
      [['ScorePerMinute','Pace'],['Accuracy','Accuracy'],['KillsPerSecond','Kills/sec'],['DamageEfficiency','Damage efficiency']].forEach(function(item){add(controls,button(item[1],function(){metric=item[0];render()},metric===item[0]))});
      var canvas=add(p,node('canvas',undefined,'run-chart'));setTimeout(function(){graph(canvas,data.Details&&data.Details.Timeline||[])},0);
    }else if(tab==='responses'){
      var response=data.Response;if(!response){text(host,'No target response analysis was recorded for this run.');return}
      var summary=add(host,node('div',undefined,'run-metrics'));card(summary,'Responses',n(response.Episodes,0));card(summary,'Reaction',n(response.ReactionMs)+'ms');card(summary,'Recovery',n(response.RecoveryMs)+'ms');card(summary,'Stable responses',response.StableRatio===null?'—':n(response.StableRatio*100)+'%');
      text(host,'Showing '+data.Episodes.length+' of '+data.EpisodeCount+' recorded responses. Reaction and recovery remain separate measurements.');
      table(host,[['Time',11],['Type',15],['Target',22],['Reaction',13],['Recovery',13],['Peak error',14],['Stable',12]],data.Episodes.map(function(e){return [seconds(e.StartMs),e.Kind.replace(/_/g,' '),e.Target,n(e.ReactionMs)+'ms',n(e.RecoveryMs)+'ms',n(e.PeakYawDegrees)+'°',e.Stable?'Yes':'No']}));
    }else if(tab==='shots'){
      text(host,data.ShotCount+' recorded shot events. Times are offsets from the first recorded shot event; nearby targets do not imply a hit.');
      if(!data.Shots.length){text(host,'No shot telemetry was recorded for this run.');return}
      table(host,[['Time',13],['Event',18],['Count',10],['Nearest target',25],['Distance',12],['Yaw error',11],['Pitch error',11]],data.Shots.map(function(s){var target=s.Targets.filter(function(t){return t.Nearest})[0]||{};return [seconds(s.TimestampMs-(typeof data.FirstShotTimestampMs==='number'?data.FirstShotTimestampMs:s.TimestampMs)),s.Kind.replace(/_/g,' '),n(s.Count,0),target.Label||'—',n(target.Distance),n(target.YawErrorDegrees)+'°',n(target.PitchErrorDegrees)+'°']}));
      var paging=add(host,node('div',undefined,'run-tabs'));var previous=add(paging,button('Previous',function(){page--;load()}));previous.disabled=data.ShotPage===0;add(paging,node('span',(data.ShotPage+1)+' / '+data.ShotPages,'run-muted'));var next=add(paging,button('Next',function(){page++;load()}));next.disabled=data.ShotPage+1>=data.ShotPages;
    }else{
      text(host,'Showing '+data.Windows.length+' of '+data.WindowCount+' recorded analysis windows.');
      if(!data.Windows.length){text(host,'No key moments were recorded for this run.');return}
      data.Windows.forEach(function(w){var p=add(host,panel(w.Label||w.Kind.replace(/_/g,' ')));text(p,seconds(w.StartMs)+' – '+seconds(w.EndMs)+(w.Target?' · '+w.Target:''));var m=add(p,node('div',undefined,'run-metrics'));card(m,'Accuracy',w.Accuracy===null?'—':n(w.Accuracy)+'%');card(m,'Shots',n(w.Fired,0));card(m,'Hits',n(w.Hits,0));card(m,'Pace',n(w.ScorePerMinute),'Score per minute')});
    }
  }
  global.AimModRunDetails={open:function(container,id,request,historyEntry){host=container;runId=id;transport=request;preview=historyEntry;data=null;page=0;tab='summary';load()},leave:function(){generation++},resize:function(){if(data&&host)render()}};
})(window);
