(function(root){
  'use strict';
  // Discord presence card on the Settings page. Self-contained: settings.js only
  // calls render/leave. Uses the page's existing settings classes.
  var target=null,request=null,epoch=0,value=null,status=null;
  var keys=['discordPresenceEnabled','discordShowScore','discordShowPersonalBest','discordShowHubButton','discordShowLobby','discordShowJoin'];
  var states={
    showing:'Showing on Discord.',
    waiting:'Waiting for KovaaK’s to hand over its Discord status.',
    'discord-unavailable':'Discord isn’t running. AimMod connects when it starts.',
    'game-off':'Discord status is turned off in KovaaK’s settings. Turn it on there to show AimMod on Discord.',
    unsupported:'This KovaaK’s version can’t hand over its Discord status, so KovaaK’s keeps its own.',
    off:'KovaaK’s shows its own Discord status.',
    starting:'Starting…'
  };
  function node(tag,css,text){var el=root.document.createElement(tag);el.className=css||'';if(text!==undefined)el.textContent=text;return el;}
  function valid(data){if(!data||!data.settings)return false;for(var i=0;i<keys.length;i++)if(typeof data.settings[keys[i]]!=='boolean')return false;return true;}
  function leave(){epoch++;if(request){request.abort();request=null;}target=null;}
  function send(patch,done){
    var ticket=++epoch,xhr=new root.XMLHttpRequest();request=xhr;
    var path=root.location.pathname;path=path.slice(0,path.lastIndexOf('/'));
    xhr.open(patch?'POST':'GET',path+'/discord-settings',true);xhr.timeout=10000;
    if(patch){xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');}
    var finished=false;
    function finish(ok,data){if(finished)return;finished=true;if(ticket!==epoch||!target)return;request=null;done(ok,data);}
    xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;if(xhr.status!==200){finish(false);return;}try{var data=JSON.parse(xhr.responseText);finish(valid(data),data);}catch(e){finish(false);}};
    xhr.onerror=xhr.ontimeout=function(){finish(false);};xhr.send(patch?JSON.stringify(patch):null);
  }
  function accept(data){value=data.settings;status=data.status&&typeof data.status.state==='string'?data.status.state:null;}
  function draw(panel,message){
    while(panel.firstChild)panel.removeChild(panel.firstChild);
    panel.appendChild(node('h2','','Discord'));
    panel.appendChild(node('p','subtle','Replaces KovaaK’s own Discord status.'));
    if(message)panel.appendChild(node('p',message==='Saved.'?'saved-note':'notice warn',message));
    if(!value){var retryRow=node('div','actions'),retry=node('button','button primary','Try again');retry.type='button';retry.onclick=function(){load(panel);};retryRow.appendChild(retry);panel.appendChild(retryRow);return;}
    if(status&&states[status])panel.appendChild(node('p','subtle',states[status]));
    // Same switch as the rest of Settings. The detail options only apply while
    // AimMod is shown on Discord, so they are disabled while it is off.
    function toggle(key,title,description){
      var inactive=key!=='discordPresenceEnabled'&&!value.discordPresenceEnabled;
      var row=node('div','settings-row'+(inactive?' disabled':'')),info=node('div','settings-info');info.appendChild(node('h3','',title));info.appendChild(node('p','subtle',description));row.appendChild(info);
      var on=value[key];row.appendChild(node('span','switch-state',on?'On':'Off'));
      var control=node('button','switch'+(on?' on':''),on?'On':'Off');control.type='button';control.setAttribute('role','switch');control.setAttribute('aria-checked',String(on));control.setAttribute('aria-label',title);control.appendChild(node('span','knob'));control.disabled=inactive;
      control.onclick=function(){if(request||inactive)return;var patch={};patch[key]=!value[key];var controls=panel.querySelectorAll('button');for(var i=0;i<controls.length;i++)controls[i].disabled=true;send(patch,function(ok,data){if(ok)accept(data);draw(panel,ok?'Saved.':'Couldn’t save. Try again.');});};
      row.appendChild(control);panel.appendChild(row);
    }
    toggle('discordPresenceEnabled','Show AimMod on Discord','Your scenario, run timer and session progress.');
    toggle('discordShowScore','Score and accuracy','Your live score, and your last one after each run.');
    toggle('discordShowPersonalBest','Personal best','Your PB, your pace against it, and when you beat it.');
    toggle('discordShowHubButton','Hub profile button','Shown while your account is linked.');
    toggle('discordShowLobby','Lobby and match','Lobby size and mode; in a match, the round and whether you lead.');
    toggle('discordShowJoin','Join from Discord','Steam friends can join while your lobby has room and isn’t invite only.');
  }
  function load(panel){send(null,function(ok,data){if(ok)accept(data);draw(panel,ok?'':'Couldn’t load your Discord settings.');});}
  function render(container){
    leave();target=container;value=null;status=null;
    var panel=node('div','panel settings-card');panel.appendChild(node('p','subtle','Loading Discord settings…'));container.appendChild(panel);load(panel);
  }
  // Tells the worker which AimMod page is shown, for the Discord status. Resent
  // every 4 s while shown; the worker treats 10 s of silence as closed.
  var viewPage=null,viewShown=false,viewTimer=null;
  function report(){
    if(!viewPage)return;
    var xhr=new root.XMLHttpRequest(),path=root.location.pathname;path=path.slice(0,path.lastIndexOf('/'));
    xhr.open('POST',path+'/workspace-view',true);xhr.timeout=5000;xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');
    xhr.send(JSON.stringify({page:viewPage,visible:viewShown}));
  }
  function view(page,shown){
    if(typeof page!=='string'||!/^[a-z][a-z-]{0,31}$/.test(page))return;
    var changed=page!==viewPage||!!shown!==viewShown;viewPage=page;viewShown=!!shown;
    if(viewShown&&!viewTimer&&root.setInterval)viewTimer=root.setInterval(report,4000);
    if(!viewShown&&viewTimer){root.clearInterval(viewTimer);viewTimer=null;}
    if(changed)report();
  }
  root.AimModDiscordSettings={render:render,leave:leave,view:view};
})(window);
