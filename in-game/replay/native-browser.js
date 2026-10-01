/* Opens an actual main-view replay. The native replay HUD owns playback controls. */
(function (root) {
  'use strict';
  var active=false, generation=0, requests=[], timer=null, poller=null, tick=null, transferred=false, waiting=false;
  var POLL_MS=1000, HANDOFF_MS=5000;
  function endpoint(){return root.location.pathname.slice(0,root.location.pathname.lastIndexOf('/'))+'/native-replay';}
  function request(method, body, callback) {
    var ticket=generation, xhr=new root.XMLHttpRequest(); requests.push(xhr);
    xhr.open(method,endpoint(),true);xhr.timeout=5000;
    if(body){xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');}
    var done=false;
    function finish(){if(done)return;done=true;var i=requests.indexOf(xhr);if(i>=0)requests.splice(i,1);if(!active||ticket!==generation)return;var data=null;try{data=JSON.parse(xhr.responseText);}catch(_){}callback(xhr.status,data);}
    xhr.onreadystatechange=function(){if(xhr.readyState===4)finish();};xhr.onerror=xhr.ontimeout=finish;
    xhr.send(body?JSON.stringify(body):null);
  }
  function post(body){var xhr=new root.XMLHttpRequest();xhr.open('POST',endpoint(),true);xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');xhr.send(JSON.stringify(body));}
  function stopTimers(){if(timer!==null)root.clearTimeout(timer);timer=null;if(poller!==null)root.clearTimeout(poller);poller=null;if(tick!==null)root.clearTimeout(tick);tick=null;}
  function leave(){
    // A pending start keeps waiting while the player loads the scenario or opens
    // the pause menu, so hiding the workspace must not cancel it.
    var cancel=active&&!transferred&&!waiting;active=false;generation++;stopTimers();
    var old=requests;requests=[];old.forEach(function(xhr){xhr.abort();});
    // Closing the workspace is the successful handoff to the game, not a stop command.
    if(cancel)post({action:'close'});
  }
  function clock(seconds){var s=Math.max(0,Math.floor(seconds||0)),m=Math.floor(s/60);return m+':'+(s%60<10?'0':'')+(s%60);}
  // start is the service's pending-start state; older services omit it.
  function pendingFor(state,id){var start=state&&state.start;return start&&start.pending&&start.pending===id?start:null;}
  function enter(target,row){
    leave();active=true;transferred=false;waiting=false;while(target.firstChild)target.removeChild(target.firstChild);
    function node(tag,text,css){var n=root.document.createElement(tag);if(text!==undefined)n.textContent=text;if(css)n.className=css;return n;}
    var id=row.id||row.Id;
    var title=node('h3',row.scenario||row.Scenario||'Replay');
    var note=node('p','Preparing in-game playback…');note.style.cssText='color:#dcebe3;margin-top:12px';
    var retry=node('button','Try again');retry.className='button primary';retry.type='button';retry.style.marginTop='16px';retry.style.display='none';
    // Pending start: what to do in the game, how long it has waited, Retry/Cancel.
    var pending=node('div',undefined,'replay-pending');pending.style.display='none';
    var steps=node('p','','replay-pending-message'),elapsed=node('p','','replay-pending-timer'),row2=node('div',undefined,'actions replay-pending-actions');
    var retryNow=node('button','Retry now','button primary'),cancel=node('button','Cancel','button');retryNow.type=cancel.type='button';
    row2.appendChild(retryNow);row2.appendChild(cancel);pending.appendChild(steps);pending.appendChild(elapsed);pending.appendChild(row2);
    target.appendChild(title);target.appendChild(note);target.appendChild(retry);target.appendChild(pending);
    var seconds=0;
    function stopTick(){if(tick!==null)root.clearTimeout(tick);tick=null;}
    function countUp(){stopTick();elapsed.textContent='Waiting '+clock(seconds);tick=root.setTimeout(function(){tick=null;if(!active||!waiting)return;seconds++;countUp();},1000);}
    function fail(message){stopTick();waiting=false;pending.style.display='none';note.textContent=message;retry.style.display='';transferred=false;}
    function started(){stopTick();waiting=false;pending.style.display='none';transferred=true;note.textContent='Opening replay in the game…';
      // A successful native handoff hides this workspace and cancels the
      // timer through leave(). Visible transport alone is not a scene ack.
      timer=root.setTimeout(function(){timer=null;request('POST',{action:'close'},function(){fail('Replay did not open. Try again.');});},HANDOFF_MS);}
    function wait(start){waiting=true;retry.style.display='none';pending.style.display='';note.textContent='Waiting for the game to start this replay.';
      steps.textContent=start.message||'Load the replay’s scenario, then open the pause menu (Esc).';
      if(typeof start.waitingSeconds==='number'&&isFinite(start.waitingSeconds))seconds=start.waitingSeconds;if(tick===null)countUp();else elapsed.textContent='Waiting '+clock(seconds);
      if(poller!==null)root.clearTimeout(poller);poller=root.setTimeout(poll,POLL_MS);}
    // While waiting, the service starts the replay by itself; watch for the outcome.
    function poll(){poller=null;if(!active||!waiting)return;request('GET',null,function(code,state){
      if(!waiting)return;if(code!==200||!state){poller=root.setTimeout(poll,POLL_MS);return;}
      var start=pendingFor(state,id);if(start){wait(start);return;}
      // Pending cleared: with a reason it expired or failed, without one it started.
      var reason=state.start&&state.start.reason;
      if(reason){fail(reason==='timed-out'?'The replay did not start within 10 minutes. Try again when the scenario is loaded.':(state.start.message||'The replay did not start. Try again.'));return;}
      started();});}
    function load(){request('POST',{action:'load',id:id},function(status,state){
      if(status===202){var start=pendingFor(state,id)||(state&&state.start)||{};wait(start);return;}
      if(status===422){fail('This recording does not include the map data needed for in-game playback.');return;}
      if(status===404){fail('This replay file is missing or incomplete. Refresh the library and try another replay.');return;}
      if(status===409){fail('In-game playback is not ready yet. Wait a moment, then try again.');return;}
      if(status!==200){fail('This replay could not be opened.');return;}
      started();});}
    function open(){
      retry.style.display='none';pending.style.display='none';note.textContent='Preparing in-game playback…';
      request('GET',null,function(code,state){
        if(code!==200||!state){fail('Could not connect to in-game playback.');return;}
        var resumed=pendingFor(state,id);if(resumed){wait(resumed);return;}
        // Services with a start gate decide in load(); older ones need a ready renderer first.
        if(!state.start&&!state.rendererReady){fail(state.rendererReason==='challenge-active'||state.rendererReason==='scenario-active'?'Finish the current run before opening a replay.':'In-game replay playback is not available yet.');return;}
        load();
      });
    }
    retry.onclick=open;retryNow.onclick=function(){load();};
    cancel.onclick=function(){stopTick();waiting=false;if(poller!==null)root.clearTimeout(poller);poller=null;request('POST',{action:'cancel'},function(){});pending.style.display='none';note.textContent='Replay start cancelled.';retry.style.display='';};
    open();
  }
  // Reports a pending start (if any) so the library can reopen it after the
  // player returns from loading the scenario.
  function pendingStart(callback){var xhr=new root.XMLHttpRequest();xhr.open('GET',endpoint(),true);xhr.timeout=5000;xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;var state=null;try{state=JSON.parse(xhr.responseText);}catch(_){}callback(xhr.status===200&&state&&state.start&&state.start.pending?state.start:null);};xhr.onerror=xhr.ontimeout=function(){callback(null);};xhr.send(null);}
  root.AimModNativeReplayBrowser={enter:enter,leave:leave,pendingStart:pendingStart};
})(typeof window==='undefined'?globalThis:window);
