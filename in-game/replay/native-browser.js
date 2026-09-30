/* Opens an actual main-view replay. The native replay HUD owns playback controls. */
(function (root) {
  'use strict';
  var active=false, generation=0, requests=[], timer=null, transferred=false;
  function request(method, body, callback) {
    var ticket=generation, xhr=new root.XMLHttpRequest(); requests.push(xhr);
    xhr.open(method,root.location.pathname.slice(0,root.location.pathname.lastIndexOf('/'))+'/native-replay',true);xhr.timeout=5000;
    if(body){xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');}
    var done=false;
    function finish(){if(done)return;done=true;var i=requests.indexOf(xhr);if(i>=0)requests.splice(i,1);if(!active||ticket!==generation)return;var data=null;try{data=JSON.parse(xhr.responseText);}catch(_){}callback(xhr.status,data);}
    xhr.onreadystatechange=function(){if(xhr.readyState===4)finish();};xhr.onerror=xhr.ontimeout=finish;
    xhr.send(body?JSON.stringify(body):null);
  }
  function leave(){
    var cancel=active&&!transferred;active=false;generation++;if(timer!==null)root.clearTimeout(timer);timer=null;
    var old=requests;requests=[];old.forEach(function(xhr){xhr.abort();});
    // Closing the workspace is the successful handoff to the game, not a stop command.
    if(cancel){var xhr=new root.XMLHttpRequest();xhr.open('POST',root.location.pathname.slice(0,root.location.pathname.lastIndexOf('/'))+'/native-replay',true);xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');xhr.send('{"action":"close"}');}
  }
  function enter(target,row){
    leave();active=true;transferred=false;while(target.firstChild)target.removeChild(target.firstChild);
    function node(tag,text){var n=root.document.createElement(tag);n.textContent=text;return n;}
    var title=node('h3',row.scenario||row.Scenario||'Replay');
    var note=node('p','Preparing in-game playback…');note.style.cssText='color:#aac3b2;margin-top:16px';
    var retry=node('button','Try again');retry.className='button primary';retry.style.marginTop='18px';retry.style.display='none';
    target.appendChild(title);target.appendChild(note);target.appendChild(retry);
    function fail(message){note.textContent=message;retry.style.display='';transferred=false;}
    function open(){
      retry.style.display='none';note.textContent='Preparing in-game playback…';
      request('GET',null,function(code,state){
        if(code!==200||!state){fail('Could not connect to in-game playback.');return;}
        if(!state.rendererReady){fail(state.rendererReason==='challenge-active'||state.rendererReason==='scenario-active'?'Finish the current run before opening a replay.':'In-game replay playback is not available yet.');return;}
        request('POST',{action:'load',id:row.id||row.Id},function(status){
          if(status===422){fail('This recording does not include the map data needed for in-game playback.');return;}
          if(status===404){fail('This replay file is missing or incomplete. Refresh the library and try another replay.');return;}
          if(status===409){fail('In-game playback is not ready yet. Wait a moment, then try again.');return;}
          if(status!==200){fail('This replay could not be opened.');return;}
          transferred=true;note.textContent='Opening replay in the game…';
          // A successful native handoff hides this workspace and cancels the
          // timer through leave(). Visible transport alone is not a scene ack.
          timer=root.setTimeout(function(){
            timer=null;
            request('POST',{action:'close'},function(){fail('Replay did not open. Try again.');});
          },5000);
        });
      });
    }
    retry.onclick=open;open();
  }
  root.AimModNativeReplayBrowser={enter:enter,leave:leave};
})(typeof window==='undefined'?globalThis:window);
