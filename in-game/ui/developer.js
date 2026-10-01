// Developer page: test multiplayer and the in-game notices on your own.
// Hidden unless Developer mode is on (Settings). Talks to the service's
// /developer endpoint; lobby details stay on the Multiplayer page.
// Gameface: XHR only, no promises or arrow functions, DOM nodes only,
// stand-alone buttons sit in .actions rows.
(function(root){
  'use strict';
  var container=null,generation=0,state=null,timer=null,toastNode=null,toastTimer=null,members=3,mode='score-race',settingsCard=null;
  var MODES=[{id:'score-race',label:'Score race'},{id:'duel',label:'Duel'},{id:'ffa-rounds',label:'Free-for-all'},{id:'practice',label:'Practice'},{id:'tracking-duel',label:'Tracking duel'}];
  var F=root.AimModFormat,drafts={scenario:'',contentScenario:'',workshop:'AimMod - ',importPath:''},typing=false,replayA=null,replayB=null,delay=2,overrides={timeScale:1,targetSize:1,targetSpeed:1},game=null,discord=null;
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
  // A click runs once until it's answered: no repeats from impatient double clicks.
  var busy={};
  function once(key,run){if(busy[key])return;busy[key]=true;run(function(){busy[key]=false;});}
  function spawnAvatar(mode){
    once('avatar-on',function(done){
      function on(){act({action:'avatar',on:true,mode:mode},function(ok){done();if(ok)toast('Test avatar on. It wears your look.');});}
      // Spawn on an AimMod map when AimModCore can load one; otherwise in whatever is loaded.
      var caps=(game&&game.capabilities)||[];
      if(drafts.scenario&&caps.indexOf('load')>=0)other('POST','/game-command',{action:'load-scenario',scenario:drafts.scenario},function(){on();});else on();
    });
  }
  function refreshSoon(){setTimeout(function(){if(container)poll();},300);}
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
    sim.body.appendChild(actions([button('Host it here',function(){act({action:'lobby',members:members,mode:mode},function(ok){if(ok)openMultiplayer();});},'primary'),button('Join a simulated host',function(){act({action:'lobby',members:members,simulatedHost:true},function(ok){if(ok)openMultiplayer();});}),
      button('Tracking duel solo',function(){act({action:'lobby',members:1,mode:'tracking-duel',scenario:drafts.scenario||((state.tools&&state.tools.ports)||[])[0]||null},function(ok){if(ok){toast('Duel lobby ready. Spawn the avatar on a run’s path to track it.');openMultiplayer();}});})]));
    if(lobby){
      sim.body.appendChild(node('p','dev-note','Room '+lobby.code+' · '+lobby.members+' members ('+lobby.simulated+' simulated) · '+(lobby.isHost?'you host':'a simulated player hosts')+' · '+(lobby.phase==='lobby'?'in the lobby':lobby.phase)));
      sim.body.appendChild(node('div','dev-label','Make them…'));
      sim.body.appendChild(actions(OPS.map(function(o){return button(o[1],function(){act({action:'sim',op:o[0]},function(ok){if(ok)toast('Done: '+o[1].toLowerCase()+'.');});},'compact');})));
      sim.body.appendChild(actions([button('Open the lobby',openMultiplayer,'compact primary'),button('Leave the lobby',function(){act({action:'leave'});},'compact quiet danger')]));
    }
    // Notifications
    var tr=section('Simulated tournament','A pretend AimMod Hub with an 8-player bracket and simulated opponents. Play it on the Tournaments page; nothing is sent to the Hub.');left.appendChild(tr.panel);
    var tsim=state.tournament&&state.tournament.simulating;
    tr.body.appendChild(actions(tsim?[button('Advance the bracket',function(){act({action:'tournament',op:'sim-advance'});},'compact'),button('Opponent reports',function(){act({action:'tournament',op:'sim-opponent-reports'});},'compact'),button('End simulation',function(){act({action:'tournament',op:'sim-stop'});},'compact')]
      :[button('Simulate a tournament',function(){act({action:'tournament',op:'simulate'},function(ok){if(ok)toast('Open Tournaments to play your first match.');});},'compact')]));
    var nt=section('In-game notices','Shows each notice in game, outside the AimMod panel, exactly like the real one.');left.appendChild(nt.panel);
    nt.body.appendChild(actions((state.notices||[]).map(function(k){return button(NOTICES[k]||k,function(){act({action:'notice',kind:k},function(ok){if(ok)toast('Sent. Close the AimMod panel to see it.');});},'compact');})));
    var tools=state.tools||{},replays=tools.replays||[],cam=state.camera;
    // Avatars
    var av=section('Avatars','The bridge’s test avatar: circling you, or walking a recorded run’s path. Shoot it to test hits.');left.appendChild(av.panel);
    // One request per click: the button waits for the answer, and the service drops repeats.
    var av0=st.avatar;
    av.body.appendChild(node('p','dev-note',av0&&av0.on?'Test avatar on, '+(av0.mode==='path'?'walking a run’s path':'circling you')+(av0.look?', wearing '+av0.look:'')+'.':'Test avatar off.'+(drafts.scenario?' It spawns in '+drafts.scenario+'.':'')));
    av.body.appendChild(actions([button('Spawn circling avatar',function(){spawnAvatar('circle');},'compact'+(av0&&av0.on&&av0.mode==='circle'?' primary':'')),button('Despawn',function(){once('avatar-off',function(done){act({action:'avatar',on:false,mode:'circle'},function(ok){done();if(ok)toast('Test avatar despawned.');});});},'compact quiet')]));
    if(tools.avatarPath)av.body.appendChild(node('p','dev-note','Path ready from a run of '+tools.avatarPath+'. Load that scenario in freeplay to see the avatar walk it.'));
    if(st.looks)add(av.body,add(node('div','dev-line'),node('span','dev-label','Your look'),segmented(st.looks.map(function(l){return {id:l.id,label:l.label};}),st.look,function(id){other('POST','/multiplayer',{action:'avatar',avatar:id},function(ok){if(ok)refreshSoon();});},'look')));
    // Replays: pick runs for the tools below.
    var rp=section('Replays','Pick runs for run vs run, the avatar path and spectating a replay as if live.');left.appendChild(rp.panel);
    if(!replays.length)rp.body.appendChild(node('p','subtle','No recorded runs yet. Play a scenario with replay recording on.'));
    replays.slice(0,8).forEach(function(r){var line=node('div','dev-replay'+(r.id===replayA||r.id===replayB?' on':''));
      add(line,add(node('div','dev-replay-text'),node('strong','',r.scenario),node('span','',(F?F.relative(r.recordedAt):r.recordedAt)+' · '+r.seconds+' s')),
        actions([button(r.id===replayA?'A':'Use as A',function(){replayA=r.id;render();},'compact'+(r.id===replayA?' primary':'')),button(r.id===replayB?'B':'Use as B',function(){replayB=r.id;render();},'compact'+(r.id===replayB?' primary':'')),
          button('Avatar walks this',function(){once('avatar-path',function(done){act({action:'avatar-path',replay:r.id},function(ok){if(!ok){done();return;}act({action:'avatar',on:true,mode:'path'},function(ok2){done();if(ok2)toast('The avatar walks that run in '+r.scenario+'. Load it in freeplay to see it.');});});});},'compact quiet')]));rp.body.appendChild(line);});
    var rr=[];
    if(replayA)rr.push(button('Watch A',function(){nativeReplay({action:'load',id:replayA},'Replay loaded. Open the pause menu to watch.');},'compact'));
    if(replayA&&replayB)rr.push(button('A vs B',function(){nativeReplay({action:'load',id:replayA,compareId:replayB},'Run vs run loaded. Open the pause menu to watch.');},'compact primary'));
    if(rr.length)rp.body.appendChild(actions(rr));
    add(rp.body,add(node('div','dev-line'),input('importPath','Full path of a .amreplay file','dev-field dev-wide'),actions([button('Import',function(){act({action:'import',path:drafts.importPath},function(ok){if(ok){toast('Imported.');drafts.importPath='';}});},'compact')])));
    // Spectate loopback
    var lp=section('Spectate yourself','Your own view, delayed, or a replay played as a live stream. Tests the spectator view, follow the leader and the HUD alone.');left.appendChild(lp.panel);
    add(lp.body,add(node('div','dev-line'),node('span','dev-label','Delay'),segmented([{id:1,label:'1 s'},{id:2,label:'2 s'},{id:3,label:'3 s'}],delay,function(d){delay=d;render();},'delay')));
    var lb=[button('Spectate yourself',function(){act({action:'loopback',source:'self',delay:delay});},'compact')];
    if(replayA)lb.push(button('Replay A as live',function(){act({action:'loopback',source:'replay',replay:replayA,delay:delay});},'compact'));
    if(tools.loopback){var l=tools.loopback;lb.push(button('Open the spectator view',function(){if(!l.scenario||!l.mapName){toast('Waiting for your view: load a scenario first.');return;}nativeReplay({action:'spectate',scenario:l.scenario,mapName:l.mapName,mapScale:l.mapScale,label:'Loopback'},'Spectating the loopback. Pause to see it.');},'compact primary'));lb.push(button('Stop',function(){act({action:'loopback',source:'off'});},'compact quiet danger'));
      lp.body.appendChild(node('p','dev-note','Streaming '+(l.source==='self'?'your view':'a replay')+(l.scenario?' in '+l.scenario:'')+(l.source==='self'?' · '+l.delay+' s behind':'')+'.'));}
    lp.body.appendChild(actions(lb));
    // Game commands
    var gc=section('Game commands','Through AimModCore. Freeplay only; refused during a challenge.');left.appendChild(gc.panel);
    var ports=tools.ports||[];if(!drafts.scenario&&ports.length)drafts.scenario=ports[0];if(!drafts.contentScenario&&ports.length)drafts.contentScenario=ports[0];
    add(gc.body,add(node('div','dev-line'),input('scenario','Scenario name','dev-field dev-wide')));
    if(ports.length)gc.body.appendChild(add(node('div','dev-ports'),node('span','dev-label','AimMod maps'),actions(ports.slice(0,6).map(function(n){return button(n.replace(/^AimMod - /,''),function(){drafts.scenario=n;drafts.contentScenario=n;render();},'compact'+(drafts.scenario===n?' primary':''));}))));
    [['timeScale','Time scale',[0.5,1,1.5]],['targetSize','Target size',[0.5,1,2]],['targetSpeed','Target speed',[0.5,1,2]]].forEach(function(o){add(gc.body,add(node('div','dev-line'),node('span','dev-label',o[1]),segmented(o[2].map(function(v){return {id:v,label:v+'×'};}),overrides[o[0]],function(v){overrides[o[0]]=v;render();},o[1])));});
    function startBody(){var b={action:'start-scenario',scenario:drafts.scenario,mode:'freeplay'};if(overrides.timeScale!==1)b.timeScale=overrides.timeScale;if(overrides.targetSize!==1)b.targetSize=overrides.targetSize;if(overrides.targetSpeed!==1)b.targetSpeed=overrides.targetSpeed;return b;}
    gc.body.appendChild(actions([button('Load',function(){gameCommand({action:'load-scenario',scenario:drafts.scenario},'Load');},'compact'),button('Start in freeplay',function(){gameCommand(startBody(),'Start');},'compact primary'),
      button('Refresh scenarios',function(){gameCommand({action:'refresh-scenarios'},'Refresh');},'compact'),button('Reset overrides',function(){gameCommand({action:'reset-overrides'},'Reset');},'compact'),
      button('Capture thumbnail',function(){if(!cam){toast('No camera yet: load a scenario first.');return;}gameCommand({action:'capture-thumbnail',scenario:drafts.scenario,width:1920,height:1080,out:'dev-capture.png',views:[{x:cam[0],y:cam[1],z:cam[2],pitch:cam[3],yaw:cam[4],fov:cam[6]||90}]},'Capture');},'compact quiet')]));
    if(game)gc.body.appendChild(node('p','dev-note','Capabilities: '+((game.capabilities||[]).join(', ')||'none')+(game.result?' · Last: '+game.result.state+' '+game.result.code+(game.result.message?' ('+String(game.result.message).slice(0,120)+')':''):'')));
    // Content and Workshop
    var ct=section('Content transfer','Receive a scenario from a pretend host through the real transfer, into a scratch folder, with hash checks.');right.appendChild(ct.panel);
    add(ct.body,add(node('div','dev-line'),input('contentScenario','Scenario from your library','dev-field dev-wide')));
    ct.body.appendChild(actions([button('Receive it',function(){act({action:'content',scenario:drafts.contentScenario});},'compact primary'),button('Receive and fail on purpose',function(){act({action:'content',scenario:drafts.contentScenario,fail:true});},'compact')]));
    if(tools.content){var c=tools.content;ct.body.appendChild(node('p','dev-note',c.state+(c.total?' · '+Math.round(c.done*100/c.total)+'%':'')+(c.error?' · '+c.error:'')+(c.files&&c.files.length?' · '+c.files.map(function(f){return f.name;}).join(', '):'')));}
    var ws=section('Workshop search','What the Map Library asks the bridge for.');right.appendChild(ws.panel);
    add(ws.body,add(node('div','dev-line'),input('workshop','Title text','dev-field dev-wide'),actions([button('Search',function(){act({action:'workshop',text:drafts.workshop},function(ok){if(ok)toast('Asked. Results show in a moment.');});},'compact')])));
    (state.workshop||[]).slice(0,10).forEach(function(w){ws.body.appendChild(add(node('div','dev-status'),node('span','dev-label',w.port?'Port':'Other'),node('span','dev-value',w.title+' · '+Math.round(w.bytes/1048576)+' MB'+(w.installed?' · installed':'')+(w.needsUpdate?' · update':''))));});
    // Status
    var sp=section('Status','What AimMod can reach right now.');right.appendChild(sp.panel);
    function line(label,value,good){add(sp.body,add(node('div','dev-status'),node('span','dev-label',label),node('span','dev-value'+(good===true?' ok':good===false?' warn':''),value)));}
    line('Steam bridge',st.online?'Connected'+(st.bridge?' · '+st.bridge:''):'Not connected',!!st.online);
    line('Transport',st.transport||'none');
    line('AimModCore',(st.capabilities||[]).length?st.capabilities.join(', '):'No game commands',(st.capabilities||[]).length>0);
    line('Simulation',st.simulation?(st.simulationForced?'On (started with --multiplayer-sim)':'On'):'Off',!!st.simulation);
    line('Discord',discord&&discord.settings?(discord.settings.discordPresenceEnabled?'On':'Off')+(discord.status&&discord.status.state?' · '+discord.status.state:''):'Not in this build');
    var logs=tools.logs||{};
    [['Service log',logs.service],['Game log (AimMod lines)',logs.game]].forEach(function(g){var lg=section(g[0],'Newest first. Ids and user names are hidden.');right.appendChild(lg.panel);var box=node('div','dev-log');(g[1]||[]).slice().reverse().forEach(function(t){box.appendChild(node('div','dev-log-line',t));});if(!(g[1]||[]).length)box.appendChild(node('div','dev-log-line','Nothing yet.'));lg.body.appendChild(box);});
    toastNode=node('div','dev-toast');toastNode.setAttribute('role','status');container.appendChild(toastNode);
  }
  function visible(d){var c={};for(var k in d)if(k!=='camera')c[k]=d[k];return JSON.stringify(c);}
  function poll(){clearTimeout(timer);if(!container)return;
    request(null,function(ok,data,ticket){if(ticket!==generation||!container)return;if(ok){var changed=visible(data)!==visible(state||{});state=data;nav();if(changed&&!typing)render();}timer=setTimeout(poll,2000);});
    other('GET','/game-command',null,function(ok,data){if(ok&&data){var changed=JSON.stringify(data)!==JSON.stringify(game);game=data;if(changed&&!typing&&container)render();}});
    if(discord===null)other('GET','/discord-settings',null,function(ok,data){discord=ok&&data&&data.settings?data:false;});
  }
  // Other local endpoints (game commands, replays, Discord), same capability prefix.
  function other(method,url,body,done){var x=new root.XMLHttpRequest();x.open(method,base()+url,true);x.timeout=8000;if(method==='POST'){x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');}
    x.onreadystatechange=function(){if(x.readyState!==4)return;var data=null;try{data=JSON.parse(x.responseText);}catch(e){data=null;}done(x.status>=200&&x.status<300,data,x.status);};x.onerror=x.ontimeout=function(){done(false,null,0);};x.send(body?JSON.stringify(body):null);}
  function input(key,hint,css){var i=node('input','dev-input');i.value=drafts[key]||'';i.setAttribute('autocomplete','off');
    i.oninput=function(){drafts[key]=i.value;if(i.syncHint)i.syncHint();};i.onfocus=function(){typing=true;};i.onblur=function(){typing=false;};
    return F&&F.field?F.field(i,hint,css||'dev-field'):i;}
  function replayName(id){var list=(state&&state.tools&&state.tools.replays)||[];for(var i=0;i<list.length;i++)if(list[i].id===id)return list[i].scenario+' · '+(F?F.relative(list[i].recordedAt):'');return id;}
  function gameCommand(body,label){other('POST','/game-command',body,function(ok,data,status){toast(ok?label+' sent.':status===409?'AimModCore can’t do that in this game build.':'Refused: '+(data&&data.error?data.error:'check the scenario name')+'.');});}
  function nativeReplay(body,label){other('POST','/native-replay',body,function(ok,data,status){toast(ok?label:status===409?'Open the pause menu (not during a run) first.':status===422?'Those runs are from different scenarios.':'Not available yet. Try again in a moment.');});}
  function enter(element){leave();container=element;generation++;if(!container)return;render();poll();}
  function leave(){generation++;clearTimeout(timer);timer=null;if(container)while(container.firstChild)container.removeChild(container.firstChild);container=null;toastNode=null;}
  root.AimModDeveloper={enter:enter,leave:leave,renderSettings:renderSettings,refreshNav:refreshNav,_state:function(){return state;}};
})(window);
