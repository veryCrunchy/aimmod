(function(root){
  'use strict';
  var doc=root.document,timer=null,request=null,generation=0,visible=true,last=null,lastSignature='';
  var surface=/(?:\?|&)surface=obs(?:&|$)/.test(root.location.search||'')?'obs':'game';
  var hud=doc.getElementById('hud'),stats=doc.getElementById('stats'),versus=doc.getElementById('versus');
  function known(n){return typeof n==='number'&&isFinite(n);}
  // Standalone page (OBS/game HUD): grouped digits without Intl, never "-0".
  function format(n,digits){if(!known(n))return '—';var parts=Math.abs(n).toFixed(digits||0).split('.'),text=parts[0].replace(/\B(?=(\d{3})+(?!\d))/g,',')+(parts[1]?'.'+parts[1]:'');return (n<0&&/[1-9]/.test(text)?'-':'')+text;}
  function hide(){hud.style.display='none';last=null;lastSignature='';}
  function begin(parent){parent.hudRows=parent.hudRows||{};parent.hudUsed={};}
  function finishRows(parent){Object.keys(parent.hudRows).forEach(function(key){if(!parent.hudUsed[key]){parent.removeChild(parent.hudRows[key].row);delete parent.hudRows[key];}});}
  // Rows are keyed separately from their caption so a player named like a
  // metric ("Current") cannot collide with it.
  function value(parent,label,text,css,key){key=key||label;var entry=parent.hudRows[key];if(!entry){var row=doc.createElement('div'),caption=doc.createElement('span'),number=doc.createElement('strong');row.appendChild(caption);row.appendChild(number);parent.appendChild(row);entry=parent.hudRows[key]={row:row,caption:caption,number:number};}parent.hudUsed[key]=true;if(entry.caption.textContent!==label)entry.caption.textContent=label;entry.row.className='hud-value'+(css?' '+css:'');if(entry.number.textContent!==text)entry.number.textContent=text;}
  function place(card,layout,opacity){
    if(!layout||!layout.visible){card.style.display='none';return;}
    card.style.display='block';card.style.opacity=String(opacity);
    var width=Math.min(Math.max(140,layout.width||240),Math.max(140,root.innerWidth-16));card.style.width=width+'px';if(card===stats)card.className='hud-card'+(width<420?' hud-narrow':'');
    var height=card.offsetHeight||200;
    card.style.left=Math.max(0,Math.min(root.innerWidth-width,root.innerWidth*(layout.x||0)/100))+'px';
    card.style.top=Math.max(0,Math.min(root.innerHeight-height,root.innerHeight*(layout.y||0)/100))+'px';
  }
  function render(data){
    var live=data&&data.live,settings=data&&data.settings;
    if(!visible||!live||!settings||!live.available||!live.active||live.paused||live.replay||!(surface==='obs'?settings.obsEnabled:settings.gameEnabled)){hide();return false;}
    var layout=surface==='obs'?(settings.obs||settings):settings;
    if(layout.opacity===0||!(layout.stats&&layout.stats.visible)&&!(layout.versus&&layout.versus.visible)){hide();return false;}
    var signature=JSON.stringify(data);if(signature===lastSignature)return true;lastSignature=signature;
    hud.style.display='block';last=data;doc.getElementById('scenario').textContent=live.scenario||'';
    var values=doc.getElementById('stats-values');begin(values);
    value(values,'Score',format(live.score), 'major');
    value(values,'Accuracy',known(live.accuracy)?format(live.accuracy,1)+'%':'—','major');
    value(values,'Elapsed',known(live.seconds)?format(live.seconds,1)+'s':'—','major');
    value(values,'Hits / shots',format(live.hits)+' / '+format(live.shots));
    if(known(live.kills))value(values,'Kills',format(live.kills));
    if(known(live.damage))value(values,'Damage',live.damage===0?'0':live.damage.toFixed(Math.abs(live.damage)<1?3:2).replace(/0+$/,'').replace(/\.$/,''));
    if(known(live.remainingSeconds))value(values,'Remaining',format(live.remainingSeconds,1)+'s');
    if(known(live.scorePerMinute))value(values,'Score / min',format(live.scorePerMinute));
    if(known(live.killsPerSecond))value(values,'Kills / sec',format(live.killsPerSecond,2));
    if(known(live.lastTimeToKillSeconds))value(values,'Last target',format(live.lastTimeToKillSeconds*1000)+' ms');
    finishRows(values);
    var opponent=live.opponentScore,opponentName=live.opponentName||'Personal best';
    doc.getElementById('vs-title').textContent='VS '+opponentName.toUpperCase();
    values=doc.getElementById('vs-values');begin(values);value(values,'Current',format(live.score),'major');value(values,opponentName,format(opponent),'','opponent');
    // Same desktop VS concept: compare measured live pace with an actual prior
    // scenario score. The worker provides its explicitly estimated final score.
    value(values,'Projected',format(live.projectedScore));var delta=live.projectedDelta;
    value(values,'Projected difference',known(delta)?(delta>0?'+':'')+format(delta):'—',known(delta)?(delta>=0?'ahead':'behind'):'');
    finishRows(values);
    doc.getElementById('vs-progress').style.width=(known(opponent)&&opponent>0&&known(live.score)?Math.max(0,Math.min(100,live.score/opponent*100)):0)+'%';
    doc.getElementById('vs-note').textContent=known(opponent)?(known(live.projectedScore)?'Projected finish at your current pace.':'Pace comparison starts as you play.'):(live.opponentSource==='unavailable'?'This opponent’s score is unavailable for this scenario.':'No personal best available for this scenario.');
    place(stats,layout.stats,layout.opacity);place(versus,layout.versus,layout.opacity);return true;
  }
  function stop(){generation++;if(timer!==null){root.clearTimeout(timer);timer=null;}if(request){request.abort();request=null;}}
  function schedule(delay){if(visible)timer=root.setTimeout(poll,delay);}
  function poll(){
    if(!visible)return;timer=null;var ticket=++generation,xhr=new root.XMLHttpRequest();request=xhr;var path=root.location.pathname;path=path.slice(0,path.lastIndexOf('/'));
    xhr.open('GET',path+'/overlay-state',true);xhr.timeout=1500;var finished=false;
    function finish(ok,data){if(finished)return;finished=true;if(ticket!==generation||!visible)return;request=null;var active=false;if(ok)active=render(data);else hide();schedule(active?100:1000);}
    xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;if(xhr.status!==200){finish(false);return;}try{finish(true,JSON.parse(xhr.responseText));}catch(e){finish(false);}};xhr.onerror=xhr.ontimeout=function(){finish(false);};xhr.send();
  }
  function setVisible(value){visible=value!==false;stop();if(visible)poll();else hide();}
  if(root.engine&&root.engine.on)root.engine.on('AimModVisibility',setVisible);
  if(root.addEventListener){root.addEventListener('resize',function(){lastSignature='';if(last)render(last);});root.addEventListener('pagehide',function(){setVisible(false);});}
  root.AimModLiveOverlay={setVisible:setVisible};setVisible(true);
})(window);
