// Developer page: test multiplayer and the in-game notices on your own.
// Hidden unless Developer mode is on (Settings). Talks to the service's
// /developer endpoint; lobby details stay on the Multiplayer page.
// Gameface: XHR only, no promises or arrow functions, DOM nodes only,
// stand-alone buttons sit in .actions rows.
(function(root){
  'use strict';
  var container=null,generation=0,state=null,timer=null,toastNode=null,toastTimer=null,members=3,mode='score-race',settingsCard=null;
  var MODES=[{id:'score-race',label:'Score race'},{id:'duel',label:'Duel'},{id:'ffa-rounds',label:'Free-for-all'},{id:'practice',label:'Practice'}];
  var NOTICES={invite:'Invite received',request:'Join request',ready:'Ready check',countdown:'Countdown',round:'Round start',friend:'Friend online',watching:'Someone watching',ask:'Spectate request',update:'Update ready',repair:'Repair needed'};
  var OPS=[['add','Add a player'],['add-missing','Add one without the map'],['chat','Someone chats'],['unready','Someone unreadies'],['away','Someone goes away or comes back'],['suggest','Suggest and vote'],['drop','Drop a player'],['reconnect','They reconnect'],['remove','A player leaves'],['host-leave','The simulated host leaves']];
  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=text;return el;}
  function add(parent){for(var i=1;i<arguments.length;i++)if(arguments[i])parent.appendChild(arguments[i]);return parent;}
  function button(label,action,css){var b=node('button','button'+(css?' '+css:''),label);b.type='button';b.onclick=action;return b;}
  function actions(list){var row=node('div','actions');for(var i=0;i<list.length;i++)if(list[i])row.appendChild(list[i]);return row;}
  function base(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  function request(body,done){
    var ticket=generation,x=new root.XMLHttpRequest();x.open(body?'POST':'GET',base()+'/developer',true);x.timeout=8000;
    if(body){x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');}
    var finished=false;function finish(ok,data){if(finished)return;finished=true;done(ok,data,ticket);}
    x.onreadystatechange=function(){if(x.readyState!==4)return;var data=null;try{data=JSON.parse(x.responseText);}catch(e){data=null;}finish(x.status>=200&&x.status<300&&!!data&&typeof data.enabled==='boolean',data);};
    x.onerror=x.ontimeout=function(){finish(false,null);};x.send(body?JSON.stringify(body):null);
  }
  function toast(text){if(!toastNode)return;toastNode.textContent=text;toastNode.style.display='block';clearTimeout(toastTimer);toastTimer=setTimeout(function(){if(toastNode)toastNode.style.display='none';},3000);}
  function act(body,done){request(body,function(ok,data,ticket){if(ok){state=data;nav();if(container&&ticket===generation)render();if(done)done(true);}else{toast(data&&data.error?data.error:'That didn’t work.');if(done)done(false);}});}

  // The nav entry exists only while developer mode is on.
  function nav(){var b=root.document.getElementById('nav-developer');if(b)b.style.display=state&&state.enabled?'':'none';}
  function refreshNav(){request(null,function(ok,data){if(ok){state=data;nav();}});}

  function toggleSwitch(on,title,action){var c=node('button','switch'+(on?' on':''),on?'On':'Off');c.type='button';c.setAttribute('role','switch');c.setAttribute('aria-checked',String(on));c.setAttribute('aria-label',title);c.appendChild(node('span','knob'));c.onclick=action;return c;}
  // Settings card: the one switch that turns everything here on.
  function renderSettings(parent){
    settingsCard=node('div','panel settings-card');parent.appendChild(settingsCard);drawSettings();
    request(null,function(ok,data){if(ok){state=data;nav();}drawSettings();});
  }
  function drawSettings(){
    var p=settingsCard;if(!p)return;while(p.firstChild)p.removeChild(p.firstChild);
    p.appendChild(node('h2','','Developer mode'));
    var row=node('div','settings-row'),info=node('div','settings-info');
    add(info,node('h3','','Developer tools'),node('p','subtle','Adds a Developer page to test multiplayer and in-game notices on your own, with simulated players. Never touches ranked runs.'));row.appendChild(info);
    var on=!!(state&&state.enabled);row.appendChild(node('span','switch-state',on?'On':'Off'));
    row.appendChild(toggleSwitch(on,'Developer tools',function(){act({action:'enable',on:!on},function(){drawSettings();});}));
    p.appendChild(row);
  }

  function segmented(options,value,pick,label){
    var box=node('div','segmented dev-seg');box.setAttribute('role','radiogroup');box.setAttribute('aria-label',label);
    options.forEach(function(o){var b=button(o.label,function(){pick(o.id);},o.id===value?'primary':'');b.setAttribute('role','radio');b.setAttribute('aria-checked',String(o.id===value));box.appendChild(b);});
    return box;
  }
  function section(title,note){var p=node('div','panel dev-card');var head=node('div','panel-head');var t=node('div','head-text');add(t,node('h2','',title),note?node('p','',note):null);head.appendChild(t);p.appendChild(head);var body=node('div','dev-body');p.appendChild(body);return {panel:p,body:body};}
  function openMultiplayer(){var b=root.document.getElementById('nav-multiplayer');if(b&&b.click)b.click();}

  function render(){
    if(!container)return;while(container.firstChild)container.removeChild(container.firstChild);
    var page=node('div','dev-page');container.appendChild(page);
    if(!state){page.appendChild(add(node('div','panel dev-card'),node('p','subtle','Loading…')));return;}
    if(!state.enabled){page.appendChild(add(node('div','panel dev-card'),node('h2','','Developer mode is off'),node('p','subtle','Turn it on under Settings to use these tools.')));return;}
    var st=state.status||{};var lobby=st.lobby;
    var row=node('div','dev-row');page.appendChild(row);var left=node('div','dev-col dev-main'),right=node('div','dev-col dev-side');row.appendChild(left);row.appendChild(right);
    // Simulated lobby
    var sim=section('Simulated lobby','Play every lobby and match screen with simulated players. They ready up, chat, post scores and vote by themselves.');left.appendChild(sim.panel);
    var counts=[];for(var i=1;i<=7;i++)counts.push({id:i,label:String(i)});
    add(sim.body,add(node('div','dev-line'),node('span','dev-label','Simulated players'),segmented(counts,members,function(n){members=n;render();},'simulated players')));
    add(sim.body,add(node('div','dev-line'),node('span','dev-label','Mode'),segmented(MODES,mode,function(id){mode=id;render();},'mode')));
    sim.body.appendChild(actions([button('Host it here',function(){act({action:'lobby',members:members,mode:mode},function(ok){if(ok)openMultiplayer();});},'primary'),button('Join a simulated host',function(){act({action:'lobby',members:members,simulatedHost:true},function(ok){if(ok)openMultiplayer();});})]));
    if(lobby){
      sim.body.appendChild(node('p','dev-note','Room '+lobby.code+' · '+lobby.members+' members ('+lobby.simulated+' simulated) · '+(lobby.isHost?'you host':'a simulated player hosts')+' · '+(lobby.phase==='lobby'?'in the lobby':lobby.phase)));
      sim.body.appendChild(node('div','dev-label','Make them…'));
      sim.body.appendChild(actions(OPS.map(function(o){return button(o[1],function(){act({action:'sim',op:o[0]},function(ok){if(ok)toast('Done: '+o[1].toLowerCase()+'.');});},'compact');})));
      sim.body.appendChild(actions([button('Open the lobby',openMultiplayer,'compact primary'),button('Leave the lobby',function(){act({action:'leave'});},'compact quiet danger')]));
    }
    // Notifications
    var nt=section('In-game notices','Shows each notice in game, outside the AimMod panel, exactly like the real one.');left.appendChild(nt.panel);
    nt.body.appendChild(actions((state.notices||[]).map(function(k){return button(NOTICES[k]||k,function(){act({action:'notice',kind:k},function(ok){if(ok)toast('Sent. Close the AimMod panel to see it.');});},'compact');})));
    // Status
    var sp=section('Status','What AimMod can reach right now.');right.appendChild(sp.panel);
    function line(label,value,good){add(sp.body,add(node('div','dev-status'),node('span','dev-label',label),node('span','dev-value'+(good===true?' ok':good===false?' warn':''),value)));}
    line('Steam bridge',st.online?'Connected'+(st.bridge?' · '+st.bridge:''):'Not connected',!!st.online);
    line('Transport',st.transport||'none');
    line('AimModCore',(st.capabilities||[]).length?st.capabilities.join(', '):'No game commands',(st.capabilities||[]).length>0);
    line('Simulation',st.simulation?(st.simulationForced?'On (started with --multiplayer-sim)':'On'):'Off',!!st.simulation);
    toastNode=node('div','dev-toast');toastNode.setAttribute('role','status');container.appendChild(toastNode);
  }
  function poll(){clearTimeout(timer);if(!container)return;request(null,function(ok,data,ticket){if(ticket!==generation||!container)return;if(ok){var changed=JSON.stringify(data)!==JSON.stringify(state);state=data;nav();if(changed)render();}timer=setTimeout(poll,2000);});}
  function enter(element){leave();container=element;generation++;if(!container)return;render();poll();}
  function leave(){generation++;clearTimeout(timer);timer=null;if(container)while(container.firstChild)container.removeChild(container.firstChild);container=null;toastNode=null;}
  root.AimModDeveloper={enter:enter,leave:leave,renderSettings:renderSettings,refreshNav:refreshNav,_state:function(){return state;}};
})(window);
