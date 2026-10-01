// Multiplayer page: home (create, join, friends, invites, recent matches),
// the lobby room with host settings, and the match screens (countdown, live
// scoreboard, round and final results). Everything renders from the native
// service's /multiplayer view; actions POST back and the reply is the new view.
// Gameface: XHR only, no promises or arrow functions, DOM nodes only, no
// placeholders (field() hints), stand-alone buttons sit in .actions rows.
(function(root){
  'use strict';
  var F=root.AimModFormat;
  var MODES=[
    {id:'score-race',label:'Score race',short:'Race',text:'Same scenario, best score wins.'},
    {id:'duel',label:'Duel',short:'Duel',text:'One on one, first to the set round wins.'},
    {id:'ffa-rounds',label:'Free-for-all',short:'FFA',text:'Several rounds, points for placing.'},
    {id:'practice',label:'Practice together',short:'Practice',text:'Side by side, live scores, no ranking.'},
    {id:'tracking-duel',label:'Tracking duel',short:'Tracking',text:'Take turns tracking. Most time on target wins.'},
    {id:'deathmatch',label:'Deathmatch',short:'DM',text:'Everyone against everyone, first to the frag limit.'},
    {id:'vampiric',label:'Vampiric 1v1',short:'Vampiric',text:'One on one. Damage heals you, health drains.'},
    {id:'instagib',label:'Instagib',short:'Instagib',text:'Every hit kills. First to the frag limit.'}
  ];
  function combat(m){return m==='deathmatch'||m==='vampiric'||m==='instagib';}
  var PRIVACY={friends:'Friends only',invite:'Invite only',public:'Public (room code)'};
  var PRESETS=[{id:'default',label:'Scenario default'},{id:'cs',label:'Counter-Strike-like'},{id:'valorant',label:'Valorant-like'},{id:'apex',label:'Apex-like'},{id:'quake',label:'Quake-like'},{id:'custom',label:'Custom'}];
  var AVATAR=['mint','cyan','amber','violet','rose'];
  var container=null,timer=null,ticker=null,generation=0,view=null,receivedAt=0,lastKey='',inflight=false,again=false;
  var editing=false,library=null,libraryAsked=false,picker=null,pickerQuery='',quickMode='score-race';
  var drafts={chat:'',code:''},focused=null,toastNode=null,toastTimer=null,countNodes=[];

  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=text;return el;}
  function add(parent){for(var i=1;i<arguments.length;i++)if(arguments[i])parent.appendChild(arguments[i]);return parent;}
  function button(label,action,css){var b=node('button','button'+(css?' '+css:''),label);b.type='button';b.onclick=action;return b;}
  // Stand-alone buttons go in an .actions row so Gameface sizes them to their label.
  function actions(){var row=node('div','actions');for(var i=0;i<arguments.length;i++)if(arguments[i])row.appendChild(arguments[i]);return row;}
  function mode(id){for(var i=0;i<MODES.length;i++)if(MODES[i].id===id)return MODES[i];return MODES[0];}
  function preset(id){for(var i=0;i<PRESETS.length;i++)if(PRESETS[i].id===id)return PRESETS[i];return PRESETS[0];}
  function safe(text,fallback){return F.safeText(typeof text==='string'?text:'',fallback||'Player');}
  function initials(name){var parts=safe(name,'?').replace(/[_.()\[\]-]+/g,' ').trim().split(/\s+/);var a=(parts[0]||'?').charAt(0),b=parts.length>1?parts[parts.length-1].charAt(0):(parts[0]||'').charAt(1);return (a+(b||'')).toUpperCase();}
  function tone(name){var h=0,s=String(name||'');for(var i=0;i<s.length;i++)h=(h*31+s.charCodeAt(i))%9973;return AVATAR[h%AVATAR.length];}
  function avatar(name,small){var a=node('div','mp-avatar '+tone(name)+(small?' small':''),initials(name));a.setAttribute('aria-hidden','true');return a;}
  function chip(text,kind){return node('span','mp-chip'+(kind?' '+kind:''),text);}
  // A small crown for the host, drawn on canvas with solid colours.
  function crown(){var c=node('canvas','mp-crown');c.width=28;c.height=22;var x=c.getContext&&c.getContext('2d');if(x){x.scale(2,2);x.fillStyle='#f0b45a';x.beginPath();x.moveTo(1,10);x.lineTo(1,3);x.lineTo(4.5,6);x.lineTo(7,1);x.lineTo(9.5,6);x.lineTo(13,3);x.lineTo(13,10);x.closePath();x.fill();}c.setAttribute('aria-label','Host');return c;}
  function member(id){var l=view&&view.lobby;if(!l)return null;for(var i=0;i<l.members.length;i++)if(l.members[i].id===id)return l.members[i];return null;}
  function nameOf(id){var m=member(id);if(m)return safe(m.name);var st=view&&view.lobby&&view.lobby.match?view.lobby.match.standings:[];for(var i=0;i<st.length;i++)if(st[i].memberId===id)return safe(st[i].name);return 'Player';}
  function now(){return view?view.now+(Date.now()-receivedAt):Date.now();}
  function seconds(ms){return Math.max(0,Math.ceil(ms/1000));}
  // Long names inside buttons: Gameface can't ellipsize a button's text, so shorten it here.
  function clip(text,n){text=String(text||'');return text.length>n?text.slice(0,n-1)+'…':text;}
  function ordinal(n){var s=['th','st','nd','rd'],v=n%100;return n+(s[(v-20)%10]||s[v]||s[0]);}

  // ---- requests -----------------------------------------------------------
  function path(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  function xhr(method,url,body,done){
    var ticket=generation,x=new root.XMLHttpRequest(),finished=false;
    x.open(method,path()+url,true);x.timeout=8000;
    if(method==='POST'){x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');}
    function finish(ok,data){if(finished)return;finished=true;if(ticket!==generation||!container)return;done(ok,data,x.status);}
    x.onreadystatechange=function(){if(x.readyState!==4)return;var data=null;try{data=JSON.parse(x.responseText);}catch(e){data=null;}finish(x.status>=200&&x.status<300&&data!==null,data);};
    x.onerror=x.ontimeout=function(){finish(false,null);};
    x.send(body?JSON.stringify(body):null);return x;
  }
  function poll(){
    if(!container)return;if(inflight){again=true;return;}inflight=true;
    // The library refreshes with the main view while it's open (installs and Workshop listings change).
    if(mapsOpen)loadMaps();
    xhr('GET','/multiplayer',null,function(ok,data){inflight=false;if(ok&&data&&data.v===1)accept(data);else if(!view)renderError();schedule();if(again){again=false;poll();}});
  }
  function fast(){var m=view&&view.lobby&&view.lobby.match;return !!(view&&(view.joining||view.watch||(m&&(m.phase==='countdown'||m.phase==='live'))));}
  function schedule(){clearTimeout(timer);if(container)timer=setTimeout(poll,fast()?250:1000);}
  function act(action,extra,done){
    var body={action:action};if(extra)for(var k in extra)if(Object.prototype.hasOwnProperty.call(extra,k))body[k]=extra[k];
    xhr('POST','/multiplayer',body,function(ok,data){if(ok&&data&&data.v===1){accept(data,true);if(done)done(true);}else{toast(data&&data.error?data.error:'That didn’t work. Try again.');if(done)done(false);}});
  }
  function setting(key,value){var s={};s[key]=value;act('settings',{settings:s});}
  function loadLibrary(){if(libraryAsked)return;libraryAsked=true;xhr('GET','/multiplayer?part=library',null,function(ok,data){if(ok&&data){library=data;render();}else libraryAsked=false;});}

  function accept(data,force){
    view=data;receivedAt=Date.now();
    if(!view.lobby)editing=false;
    if(view.lobby&&!view.lobby.isHost)editing=false;
    // Re-render only when something visible changed (the server clock always moves).
    var copy={};for(var k in data)if(k!=='now')copy[k]=data[k];
    var key=JSON.stringify(copy)+'|'+editing+'|'+(picker||'')+'|'+(library?1:0);
    // While the player types, fold view changes (pings, chat) into one re-render every few seconds.
    if(key!==lastKey){lastKey=key;var since=Date.now()-lastRender;if(focused&&!force&&since<3000){clearTimeout(deferred);deferred=setTimeout(function(){deferred=null;render();},3000-since);}else render();}
  }
  // The page scrolls inside the workspace, and absolute layers sit at the top of the
  // content. Toasts and the invite card move down by the scroll so they're always seen.
  function scrolled(){var el=container&&container.parentNode;while(el){if(el.scrollTop>0)return el.scrollTop;el=el.parentNode;}return 0;}
  function toast(text){if(!toastNode)return;toastNode.textContent=text;toastNode.style.top=scrolled()+'px';toastNode.style.display='block';clearTimeout(toastTimer);toastTimer=setTimeout(function(){if(toastNode)toastNode.style.display='none';},3500);}

  // ---- shared controls -------------------------------------------------
  function segmented(options,value,pick,disabled,label){
    var box=node('div','segmented mp-seg');if(label)box.setAttribute('aria-label',label);box.setAttribute('role','radiogroup');
    options.forEach(function(o){var b=button(o.label,function(){if(!disabled&&o.id!==value)pick(o.id);},o.id===value?'primary':'');b.setAttribute('role','radio');b.setAttribute('aria-checked',String(o.id===value));if(disabled)b.disabled=true;box.appendChild(b);});
    return box;
  }
  function toggleSwitch(on,title,action,disabled){var c=node('button','switch'+(on?' on':''),on?'On':'Off');c.type='button';c.setAttribute('role','switch');c.setAttribute('aria-checked',String(on));c.setAttribute('aria-label',title);c.appendChild(node('span','knob'));c.onclick=action;if(disabled)c.disabled=true;return c;}
  function stepper(value,min,max,step,format,change,disabled,label){
    var box=node('div','mp-stepper');box.setAttribute('aria-label',label||'');
    var minus=button('-',function(){change(Math.max(min,Math.round((value-step)*100)/100));},'compact');
    var plus=button('+',function(){change(Math.min(max,Math.round((value+step)*100)/100));},'compact');
    minus.setAttribute('aria-label','Decrease '+(label||''));plus.setAttribute('aria-label','Increase '+(label||''));
    minus.disabled=disabled||value<=min;plus.disabled=disabled||value>=max;
    return add(box,minus,node('span','mp-step-value',format(value)),plus);
  }
  function multiplier(v){return F.number(v,2)+'×';}
  function field(input,hint,css){var box=F.field(input,hint,css);return box;}
  // Every text input here is rebuilt when the view re-renders. Its text, focus and caret
  // are kept in drafts and put back afterwards; otherwise the next keys would go to the
  // game instead (WASD and other bound keys seemed to "drop" in the lobby search).
  var carets={},rendering=false,lastRender=0,deferred=null;
  function trackInput(input,key){
    input.value=drafts[key]||'';input.setAttribute('data-draft',key);
    function remember(){drafts[key]=input.value;if(typeof input.selectionStart==='number')carets[key]=input.selectionStart;}
    input.onfocus=function(){focused=key;};input.onblur=function(){if(focused===key&&!rendering)focused=null;};
    input.oninput=function(){remember();if(input.syncHint)input.syncHint();if(input.onchanged)input.onchanged();};
    input.onkeyup=function(){remember();};input.onclick=function(){remember();};
    return input;
  }
  function restoreFocus(){
    if(!focused||!container)return;var inputs=container.getElementsByTagName?container.getElementsByTagName('input'):[];
    for(var i=0;i<inputs.length;i++)if(inputs[i].getAttribute('data-draft')===focused){
      var input=inputs[i];try{input.focus();}catch(e){}
      var at=carets[focused];if(typeof at==='number'&&input.setSelectionRange){try{input.setSelectionRange(at,at);}catch(e){}}
      return;
    }
  }

  // ---- rendering -------------------------------------------------------
  function clear(){while(container.firstChild)container.removeChild(container.firstChild);countNodes=[];}
  function renderError(){if(!container)return;clear();var p=node('div','panel mp-card mp-joining mp-error');add(p,node('h2','','Can’t reach AimMod right now'),node('p','subtle','The AimMod service on this PC isn’t answering. AimMod keeps trying by itself. If this lasts more than a minute, restart KovaaK’s.'));p.appendChild(actions(button('Try again',function(){poll();},'primary')));container.appendChild(p);}
  function render(){
    if(!container||!view)return;
    rendering=true;lastRender=Date.now();clearTimeout(deferred);deferred=null;
    clear();
    var page=node('div','mp-page');container.appendChild(page);
    if(view.notice)page.appendChild(banner(view.notice.kind==='error'?'warn':'info',view.notice.text,true));
    var l=view.lobby;
    if(view.joining&&!l)page.appendChild(joining());
    else if(mapsOpen)mapLibrary(page);
    else if(historyOpen)historyPage(page);
    else if(!l)home(page);
    else if(l.match&&l.match.phase!=='final'&&!editing)matchScreen(page,l);
    else if(l.match&&l.match.phase==='final')finalScreen(page,l);
    else if(editing)settingsEditor(page,l);
    else lobbyRoom(page,l);
    if(view.invites&&view.invites.length)container.appendChild(inviteModal(view.invites[0]));
    else if(touring||(view.prefs&&view.prefs.onboarded===false&&!l&&!view.joining&&!tourSkipped))container.appendChild(onboarding());
    toastNode=node('div','mp-toast');toastNode.setAttribute('role','status');container.appendChild(toastNode);
    restoreFocus();rendering=false;tick();
  }
  function banner(kind,text,dismiss){
    var b=node('div','mp-banner '+kind);b.setAttribute('role','status');
    add(b,node('span','mp-banner-mark'),node('span','mp-banner-text',text));
    if(dismiss)b.appendChild(actions(button('Dismiss',function(){act('dismiss');},'compact quiet')));
    return b;
  }

  // Home ------------------------------------------------------------------
  var rejoinHidden='';
  function home(page){
    if(view.watch)page.appendChild(watchPanel());
    // The rejoin offer can be put away for this session (the service keeps it for 15 minutes).
    var rejoinKey=view.rejoin?safe(view.rejoin.hostName,'host'):'';
    if(view.rejoin&&rejoinHidden!==rejoinKey)page.appendChild(add(node('div','panel mp-rejoin'),add(node('div','mp-rejoin-text'),node('strong','','Rejoin '+safe(view.rejoin.hostName,'your host')+'’s lobby?'),node('span','','You left it '+(view.rejoin.minutes<1?'just now':view.rejoin.minutes+' min ago')+' when KovaaK’s closed.')),actions(button('Not now',function(){rejoinHidden=rejoinKey;render();},'quiet'),button('Rejoin',function(){act('rejoin');},'primary'))));
    var hero=node('div','panel mp-hero');page.appendChild(hero);
    var left=node('div','mp-hero-main');
    add(left,node('div','eyebrow','Multiplayer'),node('h2','','Play KovaaK’s together'),node('p','mp-lead','Race friends on the same scenario, duel first to three, or practise side by side with live scores. Results stay in AimMod and never touch KovaaK’s leaderboards.'));
    var pick=node('div','mp-mode-pick');
    MODES.forEach(function(m){var b=node('button','mp-mode'+(m.id===quickMode?' on':''));b.type='button';b.setAttribute('aria-pressed',String(m.id===quickMode));add(b,node('strong','',m.label),node('span','',m.text));b.onclick=function(){quickMode=m.id;render();};pick.appendChild(b);});
    left.appendChild(pick);
    var create=button('Create lobby',function(){act('create',{mode:quickMode});},'primary mp-big');
    left.appendChild(actions(create));
    if(!view.library.available)left.appendChild(node('p','mp-note','AimMod couldn’t find your KovaaK’s scenarios, so you can join lobbies but not pick content yet.'));
    var right=node('div','mp-hero-side');
    add(right,node('h3','','Join a friend'),node('p','subtle',view.transport.online?'Accept a Steam invite, use Join on a friend below, or enter a room code.':'Enter a room code from the host.'));
    var input=trackInput(node('input','mp-code'),'code');input.setAttribute('data-draft','code');input.setAttribute('maxlength','7');input.setAttribute('autocomplete','off');
    input.onkeydown=function(e){if((e||root.event).keyCode===13)joinCode();};
    var codeRow=node('div','mp-code-row');add(codeRow,field(input,'Room code','mp-code-field'),actions(button('Join',joinCode)));
    right.appendChild(codeRow);
    right.appendChild(steamState());
    right.appendChild(add(node('div','mp-hero-link'),node('span','','Counter-Strike maps, ported for KovaaK’s.'),actions(button('Map library',openMaps,'compact'))));
    hero.appendChild(left);hero.appendChild(right);
    var row=node('div','mp-row');page.appendChild(row);
    var main=node('div','mp-col mp-main'),side=node('div','mp-col mp-side');row.appendChild(main);row.appendChild(side);
    if((view.watchers&&view.watchers.length)||(view.watchAsks&&view.watchAsks.length))side.appendChild(watchersPanel());
    main.appendChild(friendsPanel(false));
    side.appendChild(recentPanel());
    side.appendChild(prefsPanel());
    if(view.simulation)page.appendChild(devPanel(false));
  }
  // Replays: watch a run (or a clip), or your run against another player's (same scenario).
  function watch(id,compareId){
    var body={action:'load',id:id};if(compareId)body.compareId=compareId;
    xhr('POST','/native-replay',body,function(ok,data,status){
      if(ok)toast(compareId?'Run vs run loaded. Open the pause menu to watch.':'Replay loaded. Open the pause menu to watch.');
      else toast(status===422?'Those runs are from different scenarios.':status===409?'Open the pause menu (not during a run) to watch replays.':'That replay isn’t here yet. Try again in a moment.');
    });
  }
  function runVsRun(lobby,round){
    var list=(lobby.replays||[]).filter(function(r){return (!round||r.round===round)&&r.mine&&r.others.length;});
    if(!list.length)return null;
    var p=node('div','panel mp-runs');var head=node('div','panel-head');var text=node('div','head-text');add(text,node('h2','','Watch run vs run'),node('p','','Your run against theirs, side by side in the replay viewer.'));head.appendChild(text);p.appendChild(head);
    list.forEach(function(r){r.others.forEach(function(o){var row=node('div','mp-friend');add(row,avatar(o.name,true),add(node('div','mp-friend-info'),node('strong','',safe(o.name)),node('span','','Round '+r.round)));row.appendChild(actions(button('You vs '+safe(o.name),function(){watch(r.mine,o.id);},'compact')));p.appendChild(row);});});
    return p;
  }
  // Share one of your clips (F8 marks) with the lobby.
  function clipPicker(panel){
    xhr('GET','/replays',null,function(ok,data){
      var clips=(ok&&data&&(data.items||data.replays||data)||[]);if(!clips.filter)clips=[];
      clips=clips.filter(function(r){return r&&typeof r.id==='string'&&/-clip[0-9]+$/.test(r.id);}).slice(0,8);
      var box=node('div','mp-clip-picker');
      if(!clips.length)box.appendChild(node('div','mp-muted','No clips yet. Press F8 during a recorded run to mark a moment.'));
      clips.forEach(function(c){var row=node('div','mp-clip-row');add(row,node('span','mp-clip-name',safe(c.scenario,'Clip')+' · '+F.relative(c.recordedAt)));row.appendChild(actions(button('Share',function(){act('share-clip',{id:c.id,label:safe(c.scenario,'')},function(done){if(done&&box.parentNode)box.parentNode.removeChild(box);});},'compact')));box.appendChild(row);});
      panel.appendChild(box);
    });
  }
  // Warm-up: everyone loads the scenario before the countdown.
  function loadingStage(page,lobby,match){
    var stage=node('div','mp-stage');page.appendChild(stage);
    var loaded=match.loaded||[];
    add(stage,node('div','eyebrow',mode(match.mode).label+' · getting ready'),node('div','mp-spinner'),node('h2','','Loading '+safe(match.scenario,'the scenario')),node('p','subtle',loaded.length+' of '+match.players.length+' ready. The countdown starts when everyone has loaded.'));
    var plan=planBox(lobby);if(plan)stage.appendChild(plan);
    var who=node('div','mp-stage-players');match.players.forEach(function(id){add(who,add(node('div','mp-stage-player'+(loaded.indexOf(id)>=0?' ok':'')),avatar(nameOf(id),true),node('span','',nameOf(id)),chip(loaded.indexOf(id)>=0?'Ready':'Loading…',loaded.indexOf(id)>=0?'mint':'')));});
    stage.appendChild(who);
    var left=node('p','mp-note','');countNodes.push({node:left,at:match.nextAt,format:function(ms){return 'Starting anyway in '+seconds(ms)+' s.';}});stage.appendChild(left);
    if(lobby.isHost)stage.appendChild(actions(button('Cancel match',function(){act('end');},'compact quiet danger')));
  }
  // Scenario suggestions and votes; the host picks.
  var suggesting=false;
  function suggestionsPanel(lobby){
    var list=lobby.suggestions||[];if(lobby.settings.voting===false&&!list.length)return null;
    var p=node('div','panel mp-suggest');var head=node('div','panel-head');var text=node('div','head-text');add(text,node('h2','','Suggestions'),node('p','',lobby.isHost?'Pick one to play it.':'Suggest a scenario and vote.'));head.appendChild(text);
    if(lobby.settings.voting!==false)head.appendChild(actions(button(suggesting?'Close':'Suggest',function(){suggesting=!suggesting;if(suggesting){picker='suggest';pickerQuery='';loadLibrary();}else picker=null;render();},'compact')));
    p.appendChild(head);
    if(suggesting)p.appendChild(pickerList('suggest',null));
    list.forEach(function(sg){var row=node('div','mp-friend');var mine=sg.votes.indexOf(lobby.self)>=0;
      add(row,add(node('div','mp-friend-info'),node('strong','',safe(sg.scenario,'Scenario')),node('span','','By '+safe(sg.by)+' · '+sg.votes.length+(sg.votes.length===1?' vote':' votes'))));
      row.appendChild(actions(mine?null:button('Vote',function(){act('vote',{scenario:sg.scenario});},'compact quiet'),lobby.isHost?button('Pick',function(){act('pick',{scenario:sg.scenario});},'compact primary'):null));p.appendChild(row);});
    if(!list.length&&!suggesting)p.appendChild(node('div','mp-empty','No suggestions yet.'));
    return p;
  }
  // Saved setups for the host (the last one is used for new lobbies).
  function setupsPanel(){
    var p=node('div','panel mp-setups');var head=node('div','panel-head');var text=node('div','head-text');add(text,node('h2','','Saved setups'),node('p','','Save these settings to reuse them. New lobbies start from your last setup.'));head.appendChild(text);p.appendChild(head);
    var body=node('div','mp-setups-body');p.appendChild(body);
    (view.presets||[]).forEach(function(n){var row=node('div','mp-clip-row');add(row,node('span','mp-clip-name',safe(n,'Setup')));row.appendChild(actions(button('Load',function(){act('preset-load',{name:n});},'compact'),button('Delete',function(){act('preset-delete',{name:n});},'compact quiet danger')));body.appendChild(row);});
    var input=trackInput(node('input','mp-chat-input'),'preset');input.setAttribute('maxlength','32');input.setAttribute('autocomplete','off');
    var saveRow=node('div','mp-chat-row');add(saveRow,field(input,'Name this setup','mp-chat-field'),actions(button('Save',function(){var n=(drafts.preset||'').trim();if(!n){toast('Name the setup first.');return;}act('preset-save',{name:n},function(ok){if(ok){drafts.preset='';toast('Setup saved.');}});},'compact')));
    body.appendChild(saveRow);return p;
  }
  // Spectating a friend without a lobby: status, their live stats, stop and switch.
  var watchTried=0,watchStartedFor='';
  function startWatchView(w){
    // Retried every few seconds until AimModCore's spectator view accepts (same scenario, pause menu).
    var key=w.peer+'|'+w.scenario+'|'+(w.stream||'');if(watchStartedFor===key||Date.now()-watchTried<3000||!w.scenario)return;watchTried=Date.now();
    var body={action:'spectate',scenario:w.scenario,mapName:w.mapName||'',mapScale:w.mapScale||1,label:w.name};if(w.stream)body.stream=w.stream;
    xhr('POST','/native-replay',body,function(ok,data){
      if(ok){watchStartedFor=key;act('watch-started');}
      else if(data&&data.message)lastWatchReason=String(data.message);
    });
  }
  var lastWatchReason='';
  function statLine(sc){
    if(!sc||sc.active===false)return 'Waiting for their run…';
    var parts=[];if(F.known(sc.score))parts.push('Score '+F.number(sc.score,0));if(F.known(sc.accuracy))parts.push(F.percent(sc.accuracy));if(F.known(sc.remaining))parts.push(F.duration(sc.remaining)+' left');
    return (sc.paused?'Paused · ':'')+(parts.join(' · ')||'Playing');
  }
  function watchPanel(){
    var w=view.watch;var p=node('div','panel mp-watch '+(w.state==='ended'||w.state==='missing'?'warn':''));
    var head=node('div','mp-watch-head');add(head,avatar(w.name),add(node('div','mp-watch-text'),node('div','eyebrow',w.state==='watching'?'Spectating':'Spectate'),node('h2','',safe(w.name,'Friend')),node('p','subtle',(w.scenario?safe(w.scenario,'')+' · ':'')+safe(w.message,''))));
    p.appendChild(head);
    if(w.download){var dp=downloadPanel({download:{view:w.download,conflicts:[]}},'watch-');if(dp)p.appendChild(dp);}
    if(w.state!=='ended'&&w.state!=='missing'&&w.state!=='downloading'){
      var hud=node('div','mp-watch-hud');hud.appendChild(node('span','',statLine(w.score)));p.appendChild(hud);
      if(w.state!=='watching')p.appendChild(node('p','mp-note',lastWatchReason?'Not yet: '+safe(lastWatchReason,'')+'.':'Their view starts in the pause menu once you’re in the same scenario.'));
      if(w.state==='loading'||w.state==='manual'||w.state==='watching')startWatchView(w);
    }
    var row=actions(w.state==='missing'&&!w.download?button(w.workshop?'Download from the Workshop':'Get it from '+clip(safe(w.name,'your friend'),20),function(){act('watch-download');},'primary compact'):null,button(w.state==='ended'?'Close':'Stop spectating',function(){watchStartedFor='';act('watch-stop');},w.state==='ended'?'compact':'compact quiet danger'));
    (w.others||[]).forEach(function(o){row.appendChild(button('Switch to '+clip(safe(o.name),20),function(){watchStartedFor='';act('watch',{friend:o.id});},'compact'));});
    p.appendChild(row);return p;
  }
  // Who is watching you, and requests to allow or deny (privacy "Ask me").
  function watchersPanel(){
    var p=node('div','panel mp-watchers');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','','Watching you'),node('p','','They see your view from their game.'));head.appendChild(text);p.appendChild(head);
    (view.watchAsks||[]).forEach(function(a){var row=node('div','mp-friend');add(row,avatar(a.name,true),add(node('div','mp-friend-info'),node('strong','',safe(a.name,'Friend')),node('span','','Wants to watch you')));row.appendChild(actions(button('Allow',function(){act('spectate-allow',{id:a.peer});},'compact primary'),button('Deny',function(){act('spectate-deny',{id:a.peer});},'compact')));p.appendChild(row);});
    (view.watchers||[]).forEach(function(w){var row=node('div','mp-friend');add(row,avatar(w.name,true),add(node('div','mp-friend-info'),node('strong','',safe(w.name,'Friend')),node('span','','Watching')));row.appendChild(actions(button('Remove',function(){act('spectator-remove',{id:w.peer});},'compact quiet danger')));p.appendChild(row);});
    return p;
  }
  // How you appear in the other players' games.
  function lookPanel(lobby){
    var me=member(lobby.self)||{};var p=node('div','panel mp-look');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','','Your look'),node('p','','How the others see you in their game.'));head.appendChild(text);p.appendChild(head);
    var body=node('div','mp-look-body');
    body.appendChild(segmented((view.avatars||[]).map(function(a){return {id:a.id,label:safe(a.label,a.id)};}),me.avatar||'meso-mccree',function(id){act('avatar',{avatar:id});},false,'look'));
    p.appendChild(body);return p;
  }
  // Follow a player's camera in AimModCore's spectator view (pause menu, same scenario).
  // AimModCore follows the bridge's pose stream (stream id from spectate.started). Switching
  // players resends the same call with only stream and label changed; the view cuts in place.
  var spectateShown='',spectateWanted=false,spectateLoud=false,spectateAskedAt=0;
  function want(loud){spectateWanted=true;spectateLoud=loud;spectateAskedAt=Date.now();if(view&&view.lobby)keepSpectateView(view.lobby);}
  function spectate(id){act('spectate',{member:id},function(ok){if(ok)want(true);});}
  function followLeader(on){act('spectate-follow',{on:on},function(ok){if(ok&&on)want(true);});}
  function stopSpectate(){spectateShown='';spectateWanted=false;act('spectate-stop');}
  function startSpectateView(loud){
    var s=view&&view.lobby&&view.lobby.spectate;if(!s)return;spectateShown=s.member+'|'+(s.stream||'');
    var body={action:'spectate',scenario:s.scenario,mapName:s.mapName,mapScale:s.mapScale,label:s.label};if(s.stream)body.stream=s.stream;
    xhr('POST','/native-replay',body,function(started,data,status){
      if(!loud)return;
      if(started)toast((s.follow?'Following the leader, now ':'Watching ')+safe(s.name)+'. Return to the pause menu to see their view.');
      else toast(data&&(data.reason||data.error)?'Can’t spectate yet: '+safe(String(data.reason||data.error),'')+'.':status===409?'Open the pause menu in the same scenario to spectate.':'Spectating isn’t available in this build.');
    });
  }
  // Follow the leader moves the camera in the service; the spectator view takes the new name quietly.
  function keepSpectateView(lobby){
    var s=lobby.spectate;if(!s){spectateShown='';spectateWanted=false;return;}
    if(s.follow&&spectateShown)spectateWanted=true;
    var key=s.member+'|'+(s.stream||'');if(!spectateWanted||key===spectateShown)return;
    // Wait for the bridge's stream id (a few seconds at most; older bridges don't send one).
    if(!s.stream&&!s.started&&Date.now()-spectateAskedAt<3000){clearTimeout(streamTimer);streamTimer=setTimeout(function(){if(view&&view.lobby)keepSpectateView(view.lobby);},3100);return;}
    var loud=spectateLoud;spectateLoud=false;startSpectateView(loud);
  }
  var streamTimer=null;
  function spectatePanel(lobby,match,others){
    var s=lobby.spectate,watching=!!s,me=match.players.indexOf(lobby.self)<0;
    var sp=node('div','panel mp-spectate');var sh=node('div','panel-head');var st=node('div','head-text');
    add(st,node('h2','',me?'You’re spectating':'Spectate'),node('p','',watching?(s.follow?'Following whoever leads, now ':'Watching ')+safe(s.name)+'. Their view is in the pause menu.':'Follow a player from the pause menu in the same scenario.'));sh.appendChild(st);sp.appendChild(sh);
    if(watching){var line=match.live.filter(function(l){return l.memberId===s.member;})[0];sp.appendChild(add(node('div','mp-watch-hud'),node('span','',line?statLine({active:true,score:line.score,accuracy:line.shots?line.hits*100/line.shots:null,remaining:line.remaining}):'Waiting for their run…')));}
    sp.appendChild(settingRow('Follow the leader','Switch to whoever has the top score.',toggleSwitch(!!(s&&s.follow),'Follow the leader',function(){followLeader(!(s&&s.follow));})));
    others.forEach(function(id){var on=watching&&s.member===id;var row=node('div','mp-friend'+(on?' on':''));add(row,avatar(nameOf(id),true),add(node('div','mp-friend-info'),node('strong','',nameOf(id)),node('span','',on?'Watching':'Player')));row.appendChild(actions(button(on?'Watching':'Spectate',function(){if(!on)spectate(id);},on?'compact primary':'compact')));sp.appendChild(row);});
    if(watching)sp.appendChild(actions(button('Stop spectating',stopSpectate,'compact quiet danger')));
    return sp;
  }
  // This player's own multiplayer preferences (saved on this PC).
  var prefsOpen=false;
  function prefsPanel(){
    var pr=view.prefs||{};var p=node('div','panel mp-prefs');var head=node('div','panel-head');var text=node('div','head-text');
    // Folded to a one-line summary until the player asks to change something.
    var spec={friends:'Friends can spectate you',ask:'Spectating asks you first',off:'Nobody can spectate you'};
    add(text,node('h2','','Your multiplayer settings'),node('p','',prefsOpen?'Saved on this PC.':'Hotkey '+(pr.hotkey||'F7')+' · Sounds '+(pr.sounds?'on':'off')+' · '+(spec[pr.spectatePrivacy]||spec.friends)));head.appendChild(text);
    head.appendChild(actions(button(prefsOpen?'Done':'Change',function(){prefsOpen=!prefsOpen;render();},'compact')));p.appendChild(head);
    if(!prefsOpen){p.className+=' folded';((view.keys&&view.keys.conflicts)||[]).forEach(function(c){p.appendChild(node('p','mp-warn-text mp-prefs-warn',safe(c,'')));});return p;}
    var body=node('div','mp-prefs-body');p.appendChild(body);
    function pref(key,value){var o={};o[key]=value;act('prefs',{prefs:o});}
    function flag(key,title,note){body.appendChild(settingRow(title,note,toggleSwitch(!!pr[key],title,function(){pref(key,!pr[key]);})));}
    flag('readyOnJoin','Ready when I join','Once you have the content.');
    flag('readyOnContent','Ready after downloading','When missing content finishes installing.');
    flag('readyAfterMatch','Ready again after a match','Back in the lobby after the results.');
    flag('quietDuringRanked','Quiet during ranked runs','No popups or hotkey while you play a scenario of your own.');
    flag('sounds','Sounds','Uses KovaaK’s own menu sounds.');
    flag('hideScenario','Hide my scenario from friends','Friends see you’re in AimMod, not what you play.');
    flag('friendToasts','Tell me when friends start AimMod','A short note in game with Join or Watch. Never during a run.');
    body.appendChild(settingRow('Who can spectate me','Friends watch from their own game, osu!-style.',segmented([{id:'friends',label:'Friends'},{id:'ask',label:'Ask me'},{id:'off',label:'Nobody'}],pr.spectatePrivacy||'friends',function(id){pref('spectatePrivacy',id);},false,'spectate privacy')));
    flag('showWatchers','Show who’s watching while I play','A small line at the top of the screen.');
    if(pr.sounds)body.appendChild(settingRow('Volume','',stepper(typeof pr.volume==='number'?pr.volume:0.8,0,1,0.1,function(v){return F.number(v*100,0)+'%';},function(v){pref('volume',v);},false,'volume')));
    var taken=(view.keys&&view.keys.taken)||[];function keyLabel(k){return k+(taken.indexOf(k)>=0?' (in use)':'');}
    var keys=[];for(var i=5;i<=10;i++)keys.push({id:'F'+i,label:keyLabel('F'+i)});
    body.appendChild(settingRow('Hotkey','Ready up or open the lobby from in game.',segmented(keys,pr.hotkey||'F7',function(id){pref('hotkey',id);},false,'hotkey')));
    var ks=view.keys||{};var clipOptions=['F6','F8','F9','F10','F11','Insert','Home','PageUp'].map(function(k){return {id:k,label:keyLabel(k)};});
    body.appendChild(settingRow('Clip key','Marks a moment of a recorded run as a clip.',segmented(clipOptions,ks.clip||'F8',function(id){pref('clipKey',id);},false,'clip key')));
    (ks.conflicts||[]).forEach(function(c){body.appendChild(node('p','mp-warn-text',safe(c,'')));});
    body.appendChild(actions(button('Show the tour again',function(){touring=true;tourStep=0;render();},'compact quiet')));
    return p;
  }
  // First-run tour: what multiplayer does, your keys, privacy, and the Hub and Discord links.
  var touring=false,tourStep=0,tourSkipped=false,discord=null,discordAsked=false;
  function loadDiscord(){
    if(discordAsked)return;discordAsked=true;
    // Present only in builds with Discord presence; otherwise the step leaves it out.
    xhr('GET','/discord-settings',null,function(ok,data){if(ok&&data&&data.settings&&typeof data.settings.discordPresenceEnabled==='boolean'){discord=data;render();}});
  }
  function setDiscord(on){
    var x=new root.XMLHttpRequest();x.open('POST',path()+'/discord-settings',true);x.timeout=8000;x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');
    x.onreadystatechange=function(){if(x.readyState!==4)return;var data=null;try{data=JSON.parse(x.responseText);}catch(e){data=null;}if(x.status===200&&data&&data.settings){discord=data;render();}else toast('Couldn’t change the Discord setting.');};
    x.send(JSON.stringify({discordPresenceEnabled:on}));
  }
  function finishTour(){touring=false;tourSkipped=true;tourStep=0;if(view.prefs&&!view.prefs.onboarded)act('prefs',{prefs:{onboarded:true}});render();}
  function openAccount(){finishTour();var nav=root.document.getElementById('nav-account');if(nav&&nav.click)nav.click();}
  var TOUR=[
    {title:'Play KovaaK’s together',text:'Race friends on the same scenario, duel first to three, or practise side by side with live scores. Results stay in AimMod and never touch KovaaK’s leaderboards.',points:[
      ['Lobbies','Create one, invite Steam friends or share the room code. AimMod checks everyone has the same scenario and sends what’s missing.'],
      ['Map library','Counter-Strike maps ported to KovaaK’s with CS movement, installed from the Steam Workshop.'],
      ['Spectate','Watch a friend play from your own game, with or without a lobby.'],
      ['History and rivals','Every match is kept on this PC, with replays to compare runs.']]},
    {title:'Your keys',text:'These work while KovaaK’s has focus. AimMod warns when a key clashes with your KovaaK’s binds.',keys:true},
    {title:'Privacy',text:'Choose what friends see. You can change this later under Your multiplayer settings.',privacy:true},
    {title:'Connect your accounts',text:'Both are optional.',connect:true}
  ];
  function onboarding(){
    var step=TOUR[Math.min(tourStep,TOUR.length-1)],last=tourStep>=TOUR.length-1,pr=view.prefs||{};
    var shade=node('div','mp-modal');var card=node('div','mp-modal-card mp-tour');card.setAttribute('role','dialog');card.setAttribute('aria-label','Multiplayer tour');shade.appendChild(card);
    var dots=node('div','mp-tour-dots');TOUR.forEach(function(x,i){dots.appendChild(node('span','mp-tour-dot'+(i===tourStep?' on':'')));});
    add(card,add(node('div','mp-tour-top'),node('div','eyebrow','Step '+(tourStep+1)+' of '+TOUR.length),dots),node('h2','mp-tour-title',step.title),node('p','subtle mp-tour-text',step.text));
    if(step.points){var list=node('div','mp-tour-points');step.points.forEach(function(x){list.appendChild(add(node('div','mp-tour-point'),node('strong','',x[0]),node('span','',x[1])));});card.appendChild(list);}
    if(step.keys){
      var taken=(view.keys&&view.keys.taken)||[];function label(k){return k+(taken.indexOf(k)>=0?' (in use)':'');}
      var keys=[];for(var i=5;i<=10;i++)keys.push({id:'F'+i,label:label('F'+i)});
      card.appendChild(settingRow('Lobby key','Ready up, answer invites or open the lobby.',segmented(keys,pr.hotkey||'F7',function(id){act('prefs',{prefs:{hotkey:id}});},false,'lobby key')));
      var ks=view.keys||{};var clips=['F6','F8','F9','F10','F11','Insert'].map(function(k){return {id:k,label:label(k)};});
      card.appendChild(settingRow('Clip key','Marks a moment of a recorded run as a clip.',segmented(clips,ks.clip||'F8',function(id){act('prefs',{prefs:{clipKey:id}});},false,'clip key')));
      (ks.conflicts||[]).forEach(function(c){card.appendChild(node('p','mp-warn-text',safe(c,'')));});
    }
    if(step.privacy){
      card.appendChild(settingRow('Who can spectate me','Friends watch from their own game.',segmented([{id:'friends',label:'Friends'},{id:'ask',label:'Ask me'},{id:'off',label:'Nobody'}],pr.spectatePrivacy||'friends',function(id){act('prefs',{prefs:{spectatePrivacy:id}});},false,'spectate privacy')));
      card.appendChild(settingRow('Hide my scenario from friends','They see you’re in AimMod, not what you play.',toggleSwitch(!!pr.hideScenario,'Hide my scenario',function(){act('prefs',{prefs:{hideScenario:!pr.hideScenario}});})));
    }
    if(step.connect){
      loadDiscord();
      card.appendChild(settingRow('AimMod Hub','Link your account to add Hub scores to your history and benchmarks.',actions(button('Open Account',openAccount,'compact'))));
      if(discord)card.appendChild(settingRow('Discord status','Show your AimMod session on your Discord profile.',toggleSwitch(!!discord.settings.discordPresenceEnabled,'Discord status',function(){setDiscord(!discord.settings.discordPresenceEnabled);})));
    }
    var nav=[];
    if(tourStep>0)nav.push(button('Back',function(){tourStep--;render();},'quiet'));
    else nav.push(button('Skip',finishTour,'quiet'));
    nav.push(button(last?'Done':'Next',function(){if(last)finishTour();else{tourStep++;render();}},'primary'));
    card.appendChild(actions.apply(null,nav));
    return shade;
  }
  function joinCode(){var code=(drafts.code||'').toUpperCase().replace(/[^A-Z0-9]/g,'');if(code.length!==6){toast('Room codes are six letters and numbers.');return;}act('join',{code:code},function(ok){if(ok)drafts.code='';});}
  function steamState(){
    var box=node('div','mp-steam '+(view.transport.online?'on':'off'));
    add(box,node('span','mp-dot'),node('span','',view.transport.online?'Connected to Steam':view.simulation?'Developer simulation, not connected to Steam':'Not connected to Steam, so invites and the friends list are off. Room codes still work.'));
    return box;
  }
  function friendStatus(f){return f.status==='aimmod-lobby'?chip('In a lobby','mint'):f.status==='aimmod'?chip('AimMod','mint'):f.status==='kovaaks'?chip('KovaaK’s','cyan'):chip(f.status==='away'?'Away':'Online','');}
  function friendsPanel(inLobby){
    var p=node('div','panel mp-friends');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','',inLobby?'Invite friends':'Friends playing'),node('p','',view.friends.source==='steam'?'Online Steam friends, AimMod players first.':view.friends.source==='simulation'?'Simulated friends for testing.':'Your Steam friends show here when AimMod is connected to Steam.'));
    head.appendChild(text);p.appendChild(head);
    var items=view.friends.items||[];
    if(!items.length){p.appendChild(node('div','mp-empty',view.friends.source==='unavailable'?'Until then, share your room code to play together.':'None of your friends are online right now.'));return p;}
    var list=node('div','mp-list');p.appendChild(list);
    items.slice(0,inLobby?8:12).forEach(function(f){
      var row=node('div','mp-friend');var info=node('div','mp-friend-info');
      add(info,node('strong','',safe(f.name,'Friend')),node('span','',safe(f.detail,'')));
      add(row,avatar(f.name,true),info,friendStatus(f));
      var b=null;
      if(inLobby&&f.status!=='aimmod-lobby')b=button('Invite',function(){act('invite-friend',{friend:f.id},function(ok){if(ok)toast('Invite sent to '+safe(f.name,'your friend')+'.');});},'compact');
      else if(!inLobby&&f.joinable)b=button('Join',function(){act('join-friend',{friend:f.id});},'compact primary');
      // Spectating would leave your own lobby, so the invite list only invites.
      if(!inLobby&&f.spectatable&&!(view.watch&&view.watch.peer===f.id)){var spec=button('Spectate',function(){act('watch',{friend:f.id},function(ok){if(ok)toast('Asking '+safe(f.name,'your friend')+' to let you watch…');});},'compact quiet');row.appendChild(actions(spec));}
      if(b)row.appendChild(actions(b));
      list.appendChild(row);
    });
    return p;
  }
  function recentPanel(){
    var p=node('div','panel mp-recent');var head=node('div','panel-head');add(head,node('h2','','Recent matches'));p.appendChild(head);
    var items=view.recent||[];
    if(items.length)head.appendChild(actions(button('All matches',openHistory,'compact quiet')));
    if(!items.length){p.appendChild(node('div','mp-empty','Your matches show up here. They stay in AimMod.'));return p;}
    var list=node('div','mp-list');p.appendChild(list);
    items.slice(0,6).forEach(function(r){
      var row=node('div','mp-recent-row');var info=node('div','mp-recent-info');
      var result=r.mode==='practice'?'Practice':r.won?'Won':r.place?ordinal(r.place)+' of '+r.players:r.winner?safe(r.winner)+' won':'Draw';
      add(info,node('strong','',safe(r.scenario,'Scenario')),node('span','',mode(r.mode).label+' · '+r.players+' players · '+F.relative(r.endedAt)));
      add(row,node('span','mp-result'+(r.won?' won':r.place===1?' won':''),result),info);
      if(r.simulated)row.appendChild(chip('Sim',''));
      list.appendChild(row);
    });
    return p;
  }
  // Match history and rivals --------------------------------------------------
  var historyOpen=false,history=null,openMatch=null,rivalFilter=null;
  function openHistory(){historyOpen=true;mapsOpen=false;rivalFilter=null;openMatch=null;history=null;render();xhr('GET','/multiplayer?part=history',null,function(ok,data){if(ok&&data){history=data;if(historyOpen)render();}});}
  function resultText(r){return r.mode==='practice'?'Practice':r.won?'Won':r.place?ordinal(r.place)+' of '+r.players:r.winner?safe(r.winner)+' won':'Draw';}
  function historyPage(page){
    var head=node('div','mp-editor-top');var t=node('div','mp-editor-title');
    add(t,node('div','eyebrow','Multiplayer'),node('h2','','Match history'),node('p','subtle','Every match you finished, kept on this PC. Results never touch KovaaK’s leaderboards.'));
    add(head,t,actions(button('Back',function(){historyOpen=false;render();},'primary')));page.appendChild(head);
    if(!history){page.appendChild(add(node('div','panel mp-card'),node('p','subtle','Loading your matches…')));return;}
    var row=node('div','mp-row');page.appendChild(row);
    var main=node('div','mp-col mp-main'),side=node('div','mp-col mp-side');row.appendChild(main);row.appendChild(side);
    var list=node('div','panel mp-history');main.appendChild(list);
    var lh=node('div','panel-head');var rival=rivalFilter&&(history.rivals||[]).filter(function(r){return r.key===rivalFilter;})[0];
    add(lh,node('h2','',rival?'Matches with '+safe(rival.name):'All matches'));
    if(rival)lh.appendChild(actions(button('Show all',function(){rivalFilter=null;render();},'compact quiet')));
    list.appendChild(lh);
    var shown=(history.matches||[]).filter(function(m){return !rivalFilter||(m.standings||[]).some(function(p){return p.key===rivalFilter;});});
    if(!shown.length)list.appendChild(node('div','mp-empty','No matches yet. Finished matches show up here.'));
    shown.slice(0,100).forEach(function(m){list.appendChild(historyRow(m));});
    side.appendChild(rivalsPanel());
  }
  function historyRow(m){
    var box=node('div','mp-history-item'+(openMatch===m.id?' open':''));
    var top=node('div','mp-recent-row');var info=node('div','mp-recent-info');
    var others=(m.standings||[]).filter(function(p){return !p.self;}).map(function(p){return safe(p.name);});
    add(info,node('strong','',safe(m.scenario,'Scenario')),node('span','',mode(m.mode).label+' · '+(others.length?'vs '+others.slice(0,3).join(', ')+(others.length>3?' and '+(others.length-3)+' more':''):m.players+' players')+' · '+F.relative(m.endedAt)));
    add(top,node('span','mp-result'+(m.won||m.place===1?' won':''),resultText(m)),info);
    if(m.simulated)top.appendChild(chip('Sim',''));
    top.appendChild(actions(button(openMatch===m.id?'Hide':'Details',function(){openMatch=openMatch===m.id?null:m.id;render();},'compact quiet')));
    box.appendChild(top);
    if(openMatch!==m.id)return box;
    var detail=node('div','mp-history-detail');box.appendChild(detail);
    var table=node('div','mp-table');
    add(table,add(node('div','mp-tr head'),node('span','mp-td place','#'),node('span','mp-td name','Player'),node('span','mp-td num','Best'),node('span','mp-td num',m.mode==='duel'?'Wins':m.mode==='ffa-rounds'?'Points':'Total')));
    (m.standings||[]).forEach(function(p){add(table,add(node('div','mp-tr'+(p.self?' self':'')),node('span','mp-td place',String(p.place)),node('span','mp-td name',safe(p.name)+(p.self&&safe(p.name)!=='You'?' (you)':'')),node('span','mp-td num',typeof p.best==='number'?F.number(p.best,0):'-'),node('span','mp-td num',m.mode==='duel'?String(p.wins):m.mode==='ffa-rounds'?String(p.points):F.number(p.total||0,0))));});
    detail.appendChild(table);
    var reps=m.replays||[];
    if(!reps.length){detail.appendChild(node('p','mp-note','No replays from this match on this PC.'));return box;}
    reps.forEach(function(r){
      var line=node('div','mp-history-replays');line.appendChild(node('span','mp-history-round',(m.rounds>1?'Round '+r.round:'Replays')));
      var row=[];
      if(r.mine)row.push(button('My run',function(){watch(r.mine);},'compact'));
      (r.others||[]).forEach(function(o){
        if(r.mine)row.push(button('Me vs '+safe(o.name),function(){watch(r.mine,o.id);},'compact'));
        else row.push(button('Watch '+safe(o.name),function(){watch(o.id);},'compact'));
      });
      line.appendChild(actions.apply(null,row));detail.appendChild(line);
    });
    return box;
  }
  function rivalsPanel(){
    var p=node('div','panel mp-rivals');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','','Rivals'),node('p','','Head to head with the players you meet most.'));head.appendChild(text);p.appendChild(head);
    var list=history.rivals||[];
    if(!list.length){p.appendChild(node('div','mp-empty','Play someone twice in a scored match and they show up here.'));return p;}
    list.forEach(function(r){
      var row=node('div','mp-friend'+(rivalFilter===r.key?' on':''));
      var info=node('div','mp-friend-info');add(info,node('strong','',safe(r.name)),node('span','',r.played+' matches · last '+F.relative(r.lastAt)));
      var score=node('div','mp-h2h');add(score,node('span','won',String(r.won)),node('span','sep','-'),node('span','lost',String(r.lost)));
      add(row,avatar(r.name,true),info,score,actions(button(rivalFilter===r.key?'All':'Matches',function(){rivalFilter=rivalFilter===r.key?null:r.key;openMatch=null;render();},'compact quiet')));
      p.appendChild(row);
    });
    return p;
  }
  // Map Library: AimMod map ports here and on the Steam Workshop --------------
  var mapsOpen=false,maps=null,mapsBusy=false,mapsFilter='all';
  function openMaps(){mapsOpen=true;historyOpen=false;picker=null;drafts.maps='';loadMaps();render();}
  function loadMaps(){if(mapsBusy)return;mapsBusy=true;xhr('GET','/multiplayer?part=maps',null,function(ok,data){mapsBusy=false;if(!ok||!data)return;var changed=JSON.stringify(data)!==JSON.stringify(maps);maps=data;if(mapsOpen&&changed&&!focused)render();});}
  function shiftText(p){return p.shift==='walk'?'Shift walks':p.shift==='sprint'?'Shift sprints':'No Shift ability';}
  function portState(p){
    if(p.download)return p.download.state==='queued'?'Queued…':'Downloading '+(p.download.total>0?Math.floor(p.download.done*100/p.download.total)+'%':'…');
    if(p.installed&&p.simulated)return 'Installed (simulation)';
    if(p.installed&&p.needsUpdate)return 'Update available';
    if(p.installed)return p.workshop?'Installed from the Workshop':'Installed';
    return 'On the Workshop';
  }
  function mapPreview(p){
    var box=node('div','mp-port-preview '+tone(p.display));
    if(p.preview){var img=node('img');img.src=path()+'/multiplayer?part=preview&key='+encodeURIComponent(p.key);img.setAttribute('alt','');box.appendChild(img);}
    else box.appendChild(node('span','mp-port-initials',initials(p.display)));
    return box;
  }
  function mapLibrary(page){
    var head=node('div','mp-editor-top');var t=node('div','mp-editor-title');
    add(t,node('div','eyebrow','Multiplayer'),node('h2','','Map library'),node('p','subtle','Counter-Strike and Garry’s Mod maps ported to KovaaK’s with matching movement. Install them from the Steam Workshop, then play or host a lobby on them.'));
    add(head,t,actions(button('Back',function(){mapsOpen=false;render();},'primary')));page.appendChild(head);
    if(!maps){page.appendChild(add(node('div','panel mp-card'),node('p','subtle','Loading the map library…')));loadMaps();return;}
    var bar=node('div','mp-maps-bar');
    var input=trackInput(node('input','mp-picker-search'),'maps');input.setAttribute('autocomplete','off');input.onchanged=function(){fill();};
    add(bar,field(input,'Find a map','mp-maps-search'),segmented([{id:'all',label:'All'},{id:'installed',label:'Installed'},{id:'workshop',label:'Not installed'}],mapsFilter,function(id){mapsFilter=id;render();},false,'show'));
    page.appendChild(bar);
    if(maps.source==='none')page.appendChild(banner('warn','Steam isn’t connected, so only the maps you already have are listed.'));
    else if(maps.source==='simulation')page.appendChild(banner('info','Simulation: Workshop maps are made up and installs write nothing.'));
    var grid=node('div','mp-ports');page.appendChild(grid);
    function fill(){
      while(grid.firstChild)grid.removeChild(grid.firstChild);
      var q=(drafts.maps||'').toLowerCase(),shown=0;
      (maps.ports||[]).forEach(function(p){
        if(mapsFilter==='installed'&&!p.installed)return;if(mapsFilter==='workshop'&&p.installed)return;
        if(q&&(p.scenario+' '+p.display).toLowerCase().indexOf(q)<0)return;
        shown++;grid.appendChild(portCard(p));
      });
      if(!shown)grid.appendChild(add(node('div','panel mp-empty'),node('span','',(maps.ports||[]).length?'No maps match.':'No map ports yet. Ported maps from the Workshop show up here.')));
    }
    fill();
  }
  function portCard(p){
    var c=node('div','mp-port-cell');var card=node('div','panel mp-port');c.appendChild(card);
    card.appendChild(mapPreview(p));
    var info=node('div','mp-port-info');
    var title=node('div','mp-port-title');add(title,node('strong','',safe(p.display,'Map')),p.game?chip(p.game,'cyan'):null);
    add(info,title,node('span','mp-port-variant',safe(p.variant||p.scenario,'')));
    var facts=[shiftText(p)];if(p.bytes>0)facts.push(p.bytes<1048576?Math.max(1,Math.round(p.bytes/1024))+' KB':mb(p.bytes));if(p.mapScale>0)facts.push('Scale '+F.number(p.mapScale,1));
    info.appendChild(node('span','mp-port-facts',facts.join(' · ')));
    info.appendChild(node('div','mp-port-state'+(p.needsUpdate?' warn':p.installed?' ok':''),portState(p)));
    if(p.download&&p.download.total>0){var bar=node('div','mp-progress');var fillBar=node('div','mp-progress-fill');fillBar.style.width=Math.min(100,Math.floor(p.download.done*100/p.download.total))+'%';bar.appendChild(fillBar);info.appendChild(bar);}
    card.appendChild(info);
    var row=[];
    if(!p.download&&p.workshop&&(!p.installed||p.needsUpdate)&&maps.canInstall)row.push(button(p.installed?'Update':'Install',function(){act('map-install',{key:p.key},function(){loadMaps();});},'primary compact'));
    if(p.installed&&!p.simulated){
      var l=maps.lobby;
      if(l&&l.isHost)row.push(button(l.scenario===p.scenario?'In your lobby':'Use in lobby',function(){mapsOpen=false;setting('scenario',p.scenario);},'compact'+(l.scenario===p.scenario?' quiet':'')));
      else if(!l)row.push(button('Host a lobby',function(){mapsOpen=false;act('create',{mode:'practice',scenario:p.scenario});},'compact'));
      if(maps.canLoad)row.push(button('Play',function(){act('map-load',{key:p.key},function(ok){if(ok)toast('Loading '+safe(p.display,'the map')+' in KovaaK’s…');});},'compact quiet'));
    }
    if(row.length)card.appendChild(actions.apply(null,row));
    return c;
  }
  function devPanel(inLobby){
    var p=node('div','mp-dev');add(p,node('span','mp-dev-label','Developer simulation'));
    var row=node('div','actions');
    function sim(label,op){return button(label,function(){act('sim',{op:op});},'compact quiet');}
    if(inLobby){if(view.lobby&&!view.lobby.isHost)row.appendChild(sim('Pretend I’m missing the map','self-missing'));add(row,sim('Add player','add'),sim('Add player without the map','add-missing'),sim('Drop a player','drop'),sim('Reconnect','reconnect'),sim('Remove a player','remove'));if(view.lobby&&!view.lobby.isHost)row.appendChild(sim('Host leaves','host-leave'));}
    else add(row,sim('Incoming invite','invite'),sim('Launched from an invite','launch'),sim('A friend starts AimMod','friend-online'));
    p.appendChild(row);return p;
  }
  function joining(){
    var p=node('div','panel mp-card mp-joining');
    add(p,node('div','mp-spinner'),node('h2','',view.joining.stage==='lobby'?'Joining the Steam lobby…':'Connecting to the host…'),node('p','subtle','Traffic goes through Steam’s relays, so nobody sees your IP. This can take a few seconds.'));
    p.appendChild(actions(button('Cancel',function(){act('cancel-join');})));
    return p;
  }
  function inviteModal(inv){
    var shade=node('div','mp-modal');var card=node('div','mp-modal-card');card.setAttribute('role','dialog');card.setAttribute('aria-label','Invite');shade.appendChild(card);
    shade.style.paddingTop=(scrolled()+70)+'px';
    var who=safe(inv.fromName,'A friend');
    var title=inv.kind==='launch'?'Join from Steam':inv.kind==='request'?who+' wants to join':who+' invited you';
    var line=inv.kind==='launch'?'KovaaK’s was started from a Steam invite. Join that lobby now?':inv.kind==='request'?'Let them into your lobby?':'Join their AimMod lobby?';
    add(card,node('div','eyebrow',inv.kind==='request'?'Join request':'Steam invite'),add(node('div','mp-modal-head'),avatar(who),add(node('div',''),node('h2','',title),node('p','subtle',line))));
    if(inv.summary){var s=inv.summary;var sum=node('div','mp-modal-summary');add(sum,chip(mode(s.mode).label,'mint'),node('span','',safe(s.scenario,'Scenario to be chosen')),node('span','mp-muted',s.players+' / '+s.maxPlayers+' players'));card.appendChild(sum);}
    // A different AimMod version can't join (the service refuses), so don't offer Accept.
    if(inv.compatible===false){card.appendChild(node('p','mp-warn-text',who+' has a different AimMod version, so you can’t play together yet. Both of you need the latest AimMod.'));card.appendChild(actions(button('Close',function(){act('decline-invite',{id:inv.id});},'primary')));return shade;}
    if(view.lobby&&inv.kind!=='request')card.appendChild(node('p','mp-note','Accepting leaves your current lobby.'));
    card.appendChild(actions(button(inv.kind==='request'?'Let them in':'Accept',function(){act('accept-invite',{id:inv.id});},'primary'),button('Decline',function(){act('decline-invite',{id:inv.id});})));
    return shade;
  }

  // Lobby room -----------------------------------------------------------
  function contentState(m,lobby){
    if(m.role==='spectator')return {text:'Watching',kind:''};
    if(!lobby.settings.scenario)return {text:'No scenario yet',kind:''};
    if(m.scenario==='missing')return {text:'Missing scenario',kind:'warn'};
    if(m.scenario==='mismatch')return {text:'Different scenario version',kind:'bad'};
    if(m.map==='missing')return {text:'Missing map',kind:'warn'};
    if(m.map==='mismatch')return {text:'Different map version',kind:'bad'};
    if(m.profiles==='missing'||m.profiles==='mismatch')return {text:'Missing custom profile',kind:'warn'};
    if(m.scenario==='unknown')return {text:'Checking content…',kind:''};
    return {text:'Has content',kind:'ok'};
  }
  function linkText(m,lobby){
    if(m.id===lobby.self&&lobby.isHost)return 'Host · this PC';
    if(m.connection==='reconnecting')return 'Reconnecting…';
    var route=m.link==='simulated'?'Simulated':m.link==='local'?'This PC':m.link==='direct'?'Direct':'Relay';
    return route+(m.ping!==null&&m.ping!==undefined?' · '+F.number(m.ping,0)+' ms':'');
  }
  function lobbyHead(lobby){
    var s=lobby.settings,host=member(lobby.hostId);
    var head=node('div','panel mp-lobby-head');var left=node('div','mp-lobby-title');
    var meta=[];if(s.scenario){meta.push('Map '+safe(s.mapOverride?s.mapOverride.name:s.scenario.map,'scenario map'));meta.push(F.duration(s.timeLimit||s.scenario.timeLimit));}meta.push(PRIVACY[s.privacy]||'Friends only');
    add(left,node('div','eyebrow',mode(s.mode).label),node('h2','',s.scenario?safe(s.scenario.name,'Scenario'):'Choose a scenario'),node('p','subtle',meta.join(' · ')));
    var hostLine=node('div','mp-host-line');add(hostLine,crown(),node('span','',lobby.isHost?'You’re the host':'Hosted by '+safe(host?host.name:'the host')),lobby.simulated?chip('Simulated members','violet'):null);
    left.appendChild(hostLine);
    var right=node('div','mp-room');
    add(right,node('div','mp-room-label','Room code'),node('div','mp-room-code',lobby.code));
    right.appendChild(actions(button('Copy code',function(){act('copy-code',null,function(ok){if(ok)toast('Room code copied.');});},'compact'),button('Invite friends',function(){act('invite',null,function(ok){if(ok)toast('Steam invite dialog opened.');});},'compact primary'),button('Leave',function(){act('leave');},'compact quiet danger')));
    add(head,left,right);
    return head;
  }
  function lobbyRoom(page,lobby){
    connectionBanners(page,lobby);
    var mine=member(lobby.self);
    if(lobby.autoStartAt){var auto=banner('info','Everyone’s ready. Starting in 3 s…');page.appendChild(auto);countNodes.push({node:auto.children[1],at:lobby.autoStartAt,format:function(ms){return 'Everyone’s ready. Starting in '+Math.max(1,seconds(ms))+' s…';}});}
    if(lobby.readyCheck&&!lobby.isHost&&mine&&mine.role==='player'&&!mine.ready)page.appendChild(banner('warn',safe(nameOf(lobby.hostId))+' is starting. Ready up below'+(view.hotkey?', or press '+view.hotkey+' in game':'')+'.'));
    page.appendChild(lobbyHead(lobby));
    var row=node('div','mp-row');page.appendChild(row);
    var main=node('div','mp-col mp-main'),side=node('div','mp-col mp-side');row.appendChild(main);row.appendChild(side);
    // Main column: what you act on (download, Ready / Start, players, chat). Ready and
    // Start sit right under the header so they stay above the fold at 720p.
    var download=downloadPanel(lobby);if(download)main.appendChild(download);
    main.appendChild(startBar(lobby));
    main.appendChild(playersPanel(lobby));
    main.appendChild(chatPanel(lobby));
    if(view.simulation)main.appendChild(devPanel(true));
    // Side column: who's watching, then the match, then extras. While the host is alone,
    // inviting comes first.
    if((view.watchers&&view.watchers.length)||(view.watchAsks&&view.watchAsks.length))side.appendChild(watchersPanel());
    var invite=view.friends.items&&view.friends.items.length?friendsPanel(true):null;
    var alone=lobby.members.length<2;
    if(alone&&invite)side.appendChild(invite);
    side.appendChild(summaryCard(lobby));
    var sug=suggestionsPanel(lobby);if(sug)side.appendChild(sug);
    if(!alone&&invite)side.appendChild(invite);
    side.appendChild(lookPanel(lobby));
  }
  function connectionBanners(page,lobby){
    lobby.members.forEach(function(m){if(m.connection!=='reconnecting')return;page.appendChild(banner('warn',m.id===lobby.hostId?safe(m.name)+' (host) lost connection. If they aren’t back in 10 seconds, the next player becomes host.':safe(m.name)+' lost connection. Waiting up to 30 seconds for them to come back.'));});
  }
  function playersPanel(lobby){
    var s=lobby.settings,players=0,spectators=0;lobby.members.forEach(function(m){if(m.role==='player')players++;else spectators++;});
    var p=node('div','panel mp-players');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','','Players '+players+' / '+s.maxPlayers),node('p','',s.spectators?spectators+' watching · up to 4 spectators':'Spectators off'));
    head.appendChild(text);p.appendChild(head);
    var list=node('div','mp-members');p.appendChild(list);
    var ordered=lobby.members.slice().sort(function(a,b){if(a.id===lobby.hostId)return -1;if(b.id===lobby.hostId)return 1;if(a.role!==b.role)return a.role==='player'?-1:1;return a.joinedAt-b.joinedAt;});
    ordered.forEach(function(m){list.appendChild(memberRow(m,lobby));});
    // One summary row for the free slots, with an invite shortcut.
    var free=s.maxPlayers-players;
    if(free>0){var open=node('div','mp-member open');add(open,node('div','mp-avatar empty','+'),node('div','mp-member-info',free===1?'1 open slot':free+' open slots'));
      open.appendChild(actions(button('Invite friends',function(){act('invite',null,function(ok){if(ok)toast('Steam invite dialog opened.');});},'compact quiet')));list.appendChild(open);}
    return p;
  }
  function memberRow(m,lobby){
    var row=node('div','mp-member'+(m.connection==='reconnecting'?' lost':'')+(m.id===lobby.self?' self':''));
    var info=node('div','mp-member-info');var name=node('div','mp-member-name');
    add(name,node('strong','',safe(m.name)),m.id===lobby.hostId?crown():null,m.id===lobby.self&&safe(m.name)!=='You'?chip('You',''):null,m.role==='spectator'?chip('Spectator','cyan'):null,m.simulated?chip('Sim','violet'):null);
    var c=contentState(m,lobby);var sub=node('div','mp-member-sub');
    add(sub,node('span','mp-content '+c.kind,c.text),node('span','mp-link'+(m.connection==='reconnecting'?' warn':''),linkText(m,lobby)));
    add(info,name,sub);
    add(row,avatar(m.name),info);
    var state=m.role==='spectator'?null:m.id===lobby.hostId?node('span','mp-ready host','Host'):m.away?node('span','mp-ready away','Away'):m.ready?node('span','mp-ready on','Ready'):node('span','mp-ready','Not ready');
    if(state)row.appendChild(state);
    if(m.id!==lobby.self&&!m.simulated&&lobby.match&&lobby.match.phase==='live'&&m.role==='player'){var watch=actions(button(lobby.spectate&&lobby.spectate.member===m.id?'Watching':'Spectate',function(){spectate(m.id);},'compact quiet'));watch.className='actions mp-member-tools';row.appendChild(watch);}
    if(lobby.isHost&&m.id!==lobby.self){
      var tools=actions(m.role==='player'&&!m.ready&&!m.away&&!lobby.match?button('Skip',function(){act('skip',{member:m.id});},'compact quiet'):null,m.connection==='connected'&&m.role==='player'?button('Make host',function(){act('transfer',{member:m.id});},'compact quiet'):null,button('Kick',function(){act('kick',{member:m.id},function(ok){if(ok)toast(safe(m.name)+' was removed.');});},'compact quiet danger'));
      tools.className='actions mp-member-tools';row.appendChild(tools);
    }
    return row;
  }
  function startBar(lobby){
    var bar=node('div','panel mp-start');var me=member(lobby.self)||{};var blockers=lobby.blockers||[];
    var text=node('div','mp-start-text');
    if(lobby.isHost){
      add(text,node('strong','',blockers.length?'Not ready to start':'Everyone’s ready'),blockers.length?blockerList(blockers):node('span','subtle','Starts a '+F.number(lobby.settings.countdown,0)+'-second countdown for everyone.'));
      bar.appendChild(text);
      var start=button('Start match',function(){act('start');},'primary mp-big');if(blockers.length)start.disabled=true;
      // Only readiness missing: ping everyone, in game too (they can press the hotkey).
      var onlyReady=blockers.length>0&&blockers.every(function(b){return b.code==='ready';});
      var ask=onlyReady?button(lobby.readyCheck?'Asked to ready up':'Ask everyone to ready up',function(){act('ready-check',null,function(ok){if(ok)toast('Everyone who isn’t ready got a notice.');});},'mp-big'):null;
      if(ask&&lobby.readyCheck)ask.disabled=true;
      bar.appendChild(actions(ask,start));
    }else if(me.role==='spectator'){
      add(text,node('strong','','You’re watching'),node('span','subtle','Spectators see live scores and results.'));bar.appendChild(text);
      if(lobby.settings.spectators)bar.appendChild(actions(button('Play instead',function(){act('role',{spectator:false});},'compact')));
    }else{
      var have=lobby.content&&lobby.content.scenario==='ok'&&lobby.content.map==='ok';
      add(text,node('strong','',me.ready?'You’re ready':'Ready up when you’re set'),node('span','subtle',!have&&lobby.settings.scenario?contentHelp(lobby):blockers.length?'Waiting: '+blockers[0].text:'Waiting for '+safe(nameOf(lobby.hostId))+' to start.'));
      bar.appendChild(text);
      var ready=button(me.ready?'Not ready':'Ready',function(){act('ready',{ready:!me.ready});},me.ready?'mp-big':'primary mp-big');if(!have&&lobby.settings.scenario&&!me.ready)ready.disabled=true;
      var awayBtn=button(me.away?'I’m back':'I’m away',function(){act('away',{away:!me.away});},'compact quiet');
      bar.appendChild(actions(awayBtn,lobby.settings.spectators?button('Watch',function(){act('role',{spectator:true});},'compact quiet'):null,ready));
    }
    return bar;
  }
  function mb(bytes){return F.number((bytes||0)/1048576,bytes<10485760?1:0)+' MB';}
  // File sizes: small files (scenarios, profiles) read as KB instead of "0 MB".
  function size(bytes){return bytes>0&&bytes<1048576?F.number(Math.max(1,bytes/1024),0)+' KB':mb(bytes);}
  // Download what this player is missing: Workshop first, otherwise from the host.
  function downloadPanel(lobby,prefix){
    var pre=prefix||'';
    var d=lobby.download;if(!d)return null;var v=d.view||{};var p=node('div','panel mp-download '+(v.state==='error'?'warn':v.state==='done'?'ok':''));
    var workshop=v.source==='workshop';var fromFriend=v.source==='friend';
    var head=node('div','mp-download-head');var text=node('div','mp-download-text');
    var title=v.state==='manifest'?'Checking what you need…':v.state==='done'?'Content installed and verified':v.state==='verifying'?'Verifying files…':v.state==='installing'?'Installing…':v.state==='downloading'?(workshop?'Downloading from the Steam Workshop':'Downloading from the host'):v.state==='error'?'Download stopped':v.state==='cancelled'?'Download paused':'Get the content for this lobby';
    add(text,node('strong','',title),node('span','',v.state==='error'?safe(v.error,'Something went wrong.'):v.state==='done'?'You can ready up now.':workshop?'The official Workshop copy, checked to match the lobby.':'Sent by the host through Steam and checked to match the lobby.'));
    add(head,text,chip(workshop?'Steam Workshop':fromFriend?'From your friend':'From the host',workshop?'cyan':'mint'));p.appendChild(head);
    if(d.conflicts&&d.conflicts.length)p.appendChild(node('p','mp-warn-text','You already have a different “'+safe(d.conflicts[0],'file')+'”. AimMod won’t replace your file. Rename or move it, then download.'));
    var files=node('div','mp-download-files');(v.files||[]).forEach(function(f){add(files,add(node('div','mp-download-file'),node('span','mp-download-kind',f.kind==='scenario'?'Scenario':f.kind==='map'?'Map':f.kind==='ability'?'Ability':f.kind==='weapon'?'Weapon':'Character'),node('span','mp-download-name',safe(f.name,'file')),node('span','mp-muted',size(f.size))));});
    if((v.files||[]).length&&v.state!=='done')p.appendChild(files);
    var total=workshop&&d.workshopProgress?d.workshopProgress.total:v.packed,done=workshop&&d.workshopProgress?d.workshopProgress.done:v.done;
    if(v.state==='downloading'||v.state==='cancelled'||v.state==='error'&&done>0){
      var bar=node('div','mp-progress');var fill=node('div','mp-progress-fill');fill.style.width=(total?Math.min(100,done/total*100):0)+'%';bar.appendChild(fill);p.appendChild(bar);
      var left=v.speed>0&&total>done?F.duration((total-done)/v.speed):null;
      p.appendChild(node('div','mp-progress-text',mb(done)+' of '+mb(total)+(v.speed>0?' · '+mb(v.speed)+'/s':'')+(left?' · '+left+' left':'')));
    }
    var row=null;
    if(v.state==='ready')row=actions(button('Download'+(v.total?' ('+mb(v.total)+')':''),function(){act(pre+'download');},'primary'));
    else if(v.state==='downloading')row=actions(button('Cancel',function(){act(pre+'download-cancel');},'compact quiet'));
    else if(v.state==='error'||v.state==='cancelled')row=actions(button(v.state==='cancelled'?'Resume':'Retry',function(){act(pre+'download-retry');},'primary compact'));
    if(row&&!(d.conflicts&&d.conflicts.length))p.appendChild(row);
    return p;
  }
  function contentHelp(lobby){var c=lobby.content||{};var s=lobby.settings;if(c.scenario==='missing')return 'You don’t have “'+safe(s.scenario.name,'this scenario')+'”. Get it from the host or the Workshop, then ready up.';if(c.scenario==='mismatch')return 'Your copy of this scenario is a different version than the host’s.';if(c.map==='missing')return 'You need the map “'+safe(s.mapOverride?s.mapOverride.name:s.scenario.map,'')+'” in your maps folder.';if(c.map==='mismatch')return 'Your copy of the map is a different version than the host’s.';return 'Checking your content…';}
  function blockerList(list){var box=node('div','mp-blockers');list.slice(0,4).forEach(function(b){add(box,add(node('div','mp-blocker'),node('span','mp-blocker-mark'),node('span','',b.text)));});if(list.length>4)box.appendChild(node('div','mp-muted','and '+(list.length-4)+' more'));return box;}
  function profileText(p,kind){if(!p||p.preset==='default')return kind==='weapon'?'Scenario weapon':kind==='movement'?'Scenario movement':'Scenario character';if(p.preset==='custom')return safe(p.custom,'Custom');return preset(p.preset).label;}
  function summaryCard(lobby){
    var s=lobby.settings;var p=node('div','panel mp-summary');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','','Match settings'),node('p','',lobby.isHost?'Only you can change these.':'Set by '+safe(nameOf(lobby.hostId))+' (host).'));
    head.appendChild(text);if(lobby.isHost)head.appendChild(actions(button('Edit',function(){editing=true;loadLibrary();render();},'compact')));
    p.appendChild(head);
    var rows=node('div','mp-kv');p.appendChild(rows);
    function kv(k,v,note){var r=node('div','mp-kv-row');add(r,node('span','mp-k',k),node('span','mp-v',v));if(note)r.appendChild(node('span','mp-kv-note',note));rows.appendChild(r);}
    kv('Mode',mode(s.mode).label);
    kv('Scenario',s.scenario?safe(s.scenario.name,'Scenario'):'Not chosen');
    kv('Map',s.mapOverride?safe(s.mapOverride.name,'Map')+(s.mapOverride.source==='ported'?' (ported)':''):'Scenario map');
    if(combat(s.mode)){kv('Frag limit',F.number(s.fragLimit||(s.mode==='vampiric'?10:s.mode==='instagib'?25:20),0)+' kills');if(s.mode==='vampiric')kv('Lifesteal',F.number(typeof s.lifesteal==='number'?s.lifesteal:50,0)+' %');}
    else kv(s.mode==='duel'?'First to':s.mode==='score-race'?'Attempts':s.mode==='tracking-duel'?'Rounds each':'Rounds',s.mode==='duel'?F.number(s.firstTo,0)+' wins':s.mode==='practice'?'As many as you like':F.number(s.rounds,0));
    kv(combat(s.mode)?'Match length':s.mode==='tracking-duel'?'Round length':'Time limit',s.timeLimit?F.duration(s.timeLimit):'Scenario ('+F.duration(s.scenario?s.scenario.timeLimit:60)+')');
    kv('Loadout',profileText(s.weapon,'weapon')+' · '+profileText(s.movement,'movement'));
    if(s.character&&s.character.preset!=='default')kv('Character',profileText(s.character,'character'));
    if(s.targetSpeed!==1||s.targetSize!==1)kv('Targets','Speed '+multiplier(s.targetSpeed)+' · size '+multiplier(s.targetSize));
    kv('Players','Up to '+s.maxPlayers+(s.spectators?' + spectators':''));
    kv('Countdown',F.number(s.countdown,0)+' s'+(s.lateJoin?' · late join on':'')+(s.autoStart?' · auto start':''));
    if(lobby.generated){var g=node('div','mp-generated');add(g,node('strong','',lobby.generated.problem?'Match scenario problem':lobby.generated.saved?'Match scenario saved to your scenarios':'A custom scenario will be generated'),lobby.generated.problem?node('span','mp-warn-line',safe(lobby.generated.problem,'')):null,node('span','',safe(lobby.generated.name,'Match scenario')),node('span','mp-muted','Played in freeplay and scored by AimMod, so KovaaK’s leaderboards stay untouched.'));p.appendChild(g);}
    else if(s.scenario)p.appendChild(node('div','mp-generated plain','Played as the published scenario. Each player’s run is a normal KovaaK’s run.'));
    return p;
  }
  function chatPanel(lobby){
    var p=node('div','panel mp-chat');var head=node('div','panel-head');add(head,node('h2','','Lobby chat'));p.appendChild(head);
    var log=node('div','mp-chat-log');p.appendChild(log);
    var lines=(lobby.chat||[]).slice(-40);
    if(!lines.length)log.appendChild(node('div','mp-muted','Say hi.'));
    lines.forEach(function(c){var line=node('div','mp-line'+(c.system?' system':'')+(c.clip?' clip':''));if(c.clip){var w=button('Watch',function(){watch(c.clip,null);},'compact quiet mp-clip-watch');line.appendChild(w);}if(c.system)line.textContent=safe(c.text,'');else add(line,node('span','mp-line-name',safe(c.name)),node('span','',F.safeText(c.text,'(message in an unsupported script)')));log.appendChild(line);});
    var input=trackInput(node('input','mp-chat-input'),'chat');input.setAttribute('data-draft','chat');input.setAttribute('maxlength','200');input.setAttribute('autocomplete','off');
    function send(){var text=(drafts.chat||'').trim();if(!text)return;act('chat',{text:text},function(ok){if(ok){drafts.chat='';render();}});}
    input.onkeydown=function(e){if((e||root.event).keyCode===13)send();};
    var row=node('div','mp-chat-row');add(row,field(input,'Message the lobby','mp-chat-field'),actions(button('Send',send,'compact'),button('Share a clip',function(){clipPicker(p);},'compact quiet')));
    var quick=node('div','mp-quick');['GG','Nice shot!','One more?','glhf','brb','Ready when you are'].forEach(function(q){quick.appendChild(button(q,function(){act('chat',{text:q});},'compact quiet'));});p.appendChild(quick);
    p.appendChild(row);
    setTimeout(function(){log.scrollTop=log.scrollHeight||0;},0);
    return p;
  }

  // Host settings editor ----------------------------------------------------
  function section(title,note){var s=node('div','mp-section');add(s,node('h3','',title));if(note)s.appendChild(node('p','mp-section-note',note));return s;}
  // The basics (mode, scenario, players, rounds, time, privacy) are always shown.
  // Everything else sits under "More options", which opens by itself when one of
  // those settings is already changed, so nothing that affects the match is hidden.
  var advancedOpen=null;
  function advancedChanged(s){
    function custom(p){return !!p&&p.preset&&p.preset!=='default';}
    return !!(s.mapOverride||custom(s.weapon)||custom(s.movement)||custom(s.character)||s.targetSpeed!==1||s.targetSize!==1||s.spectators||s.countdown!==5||s.lateJoin||s.autoStart||s.voting===false);
  }
  function advancedSummary(s){
    var out=[s.mapOverride?'Map '+safe(s.mapOverride.name,'custom'):'Scenario map'];
    var w=s.weapon&&s.weapon.preset!=='default'?profileText(s.weapon,'weapon'):null,mv=s.movement&&s.movement.preset!=='default'?profileText(s.movement,'movement'):null;
    out.push(w||mv?[w,mv].filter(Boolean).join(' · '):'Scenario loadout');
    if(s.targetSpeed!==1||s.targetSize!==1)out.push('Targets '+multiplier(s.targetSpeed)+' / '+multiplier(s.targetSize));
    out.push('Countdown '+F.number(s.countdown,0)+' s');
    out.push(s.spectators?'Spectators on':'No spectators');
    if(s.lateJoin)out.push('Late join');if(s.autoStart)out.push('Auto start');if(s.voting===false)out.push('No suggestions');
    return out.join(' · ');
  }
  function settingsEditor(page,lobby){
    var s=lobby.settings,overrides=s.mode!=='score-race',locked='Fixed in score race.';
    var top=node('div','mp-editor-top');var t=node('div','mp-editor-title');add(t,node('div','eyebrow','Lobby settings'),node('h2','','Set up the match'),node('p','subtle','Changes apply right away. Changing the match clears everyone’s ready.'));
    add(top,t,actions(button('Done',function(){editing=false;picker=null;advancedOpen=null;render();},'primary')));page.appendChild(top);
    var cols=node('div','mp-row');page.appendChild(cols);var a=node('div','mp-col mp-half'),b=node('div','mp-col mp-half');cols.appendChild(a);cols.appendChild(b);
    var left=node('div','panel mp-editor');a.appendChild(left);var right=node('div','panel mp-editor');b.appendChild(right);
    // Mode
    var m=section('Mode');var modes=node('div','mp-mode-pick compact');
    MODES.forEach(function(x){var btn=node('button','mp-mode'+(x.id===s.mode?' on':''));btn.type='button';btn.setAttribute('aria-pressed',String(x.id===s.mode));add(btn,node('strong','',x.label),node('span','',x.text));btn.onclick=function(){if(x.id!==s.mode)setting('mode',x.id);};modes.appendChild(btn);});
    m.appendChild(modes);left.appendChild(m);
    // Scenario
    var sc=section('Scenario','Everyone needs the same scenario. AimMod checks that each player’s copy matches yours.');
    var current=node('button','mp-pick');current.type='button';add(current,node('strong','',s.scenario?safe(s.scenario.name,'Scenario'):'Choose a scenario'),node('span','',s.scenario?'Map '+safe(s.scenario.map,'')+' · '+F.duration(s.scenario.timeLimit)+' · Change':'From your KovaaK’s library'));
    current.onclick=function(){picker=picker==='scenario'?null:'scenario';pickerQuery='';loadLibrary();render();};sc.appendChild(current);
    if(picker==='scenario')sc.appendChild(pickerList('scenario',s));
    sc.appendChild(contentTable(lobby));
    left.appendChild(sc);
    // Players, rounds and time. The settings that define the chosen mode (frag limit,
    // lifesteal, rounds each, match or round length) are basics; the rest is More options.
    var pl=section('Players and rounds');
    var oneOnOne=s.mode==='duel'||s.mode==='tracking-duel'||s.mode==='vampiric';
    pl.appendChild(settingRow('Max players',oneOnOne?'Always one against one in this mode.':'Including you.',stepper(s.maxPlayers,2,8,1,function(v){return F.number(v,0);},function(v){setting('maxPlayers',v);},oneOnOne,'max players')));
    if(s.mode==='duel')pl.appendChild(settingRow('First to','Round wins needed to take the duel.',stepper(s.firstTo,1,7,1,function(v){return F.number(v,0)+(v===1?' win':' wins');},function(v){setting('firstTo',v);},false,'first to')));
    else if(combat(s.mode)){
      var fragDefault=s.mode==='vampiric'?10:s.mode==='instagib'?25:20;
      pl.appendChild(settingRow('Frag limit','First to this many kills wins.',stepper(s.fragLimit||fragDefault,1,100,1,function(v){return F.number(v,0);},function(v){setting('fragLimit',v);},false,'frag limit')));
      if(s.mode==='vampiric')pl.appendChild(settingRow('Lifesteal','How much of the damage you deal heals you.',stepper(typeof s.lifesteal==='number'?s.lifesteal:50,0,200,5,function(v){return F.number(v,0)+' %';},function(v){setting('lifesteal',v);},false,'lifesteal')));
    }
    else if(s.mode==='tracking-duel')pl.appendChild(settingRow('Rounds each','How many times each player tracks. Roles swap every round.',stepper(s.rounds,1,5,1,function(v){return F.number(v,0);},function(v){setting('rounds',v);},false,'rounds')));
    else if(s.mode==='practice')pl.appendChild(settingRow('Rounds','Practice runs until you end it.',node('span','mp-muted','Unlimited')));
    else pl.appendChild(settingRow(s.mode==='score-race'?'Attempts':'Rounds',s.mode==='score-race'?'Each player’s best attempt counts.':'Each round gives points by placing.',stepper(s.rounds,1,s.mode==='score-race'?5:10,1,function(v){return F.number(v,0);},function(v){setting('rounds',v);},false,'rounds')));
    var limits=[{id:'default',label:'Scenario'},{id:'30',label:'30 s'},{id:'60',label:'60 s'},{id:'90',label:'90 s'},{id:'120',label:'2 min'}];
    if(combat(s.mode))pl.appendChild(settingRow('Match length','Ends here if nobody reaches the frag limit.',segmented([{id:'180',label:'3 min'},{id:'300',label:'5 min'},{id:'600',label:'10 min'}],String(s.timeLimit||300),function(id){setting('timeLimit',Number(id));},false,'match length')));
    else if(s.mode==='tracking-duel')pl.appendChild(settingRow('Round length','Seconds of tracking per round.',segmented([{id:'10',label:'10 s'},{id:'15',label:'15 s'},{id:'20',label:'20 s'},{id:'30',label:'30 s'}],String(s.timeLimit||10),function(id){setting('timeLimit',Number(id));},false,'round length')));
    else pl.appendChild(settingRow('Time limit',overrides?'The scenario’s own is '+F.duration(s.scenario?s.scenario.timeLimit:60)+'.':locked,segmented(limits,s.timeLimit?String(s.timeLimit):'default',function(id){setting('timeLimit',id==='default'?null:Number(id));},!overrides,'time limit')));
    right.appendChild(pl);
    // Privacy
    var pv=section('Who can join');
    pv.appendChild(segmented([{id:'friends',label:'Friends'},{id:'invite',label:'Invited only'},{id:'public',label:'Anyone with the code'}],s.privacy,function(id){setting('privacy',id);},false,'privacy'));
    pv.appendChild(node('p','mp-section-note',s.privacy==='friends'?'Steam friends can join from their friends list or with your invite.':s.privacy==='invite'?'Only people you invite can join.':'Anyone with the room code can join once AimMod Hub rooms are live. Until then it works like Friends.'));
    right.appendChild(pv);
    if(lobby.generated){var g=node('div','mp-generated');add(g,node('strong','','A custom scenario will be generated'),node('span','',safe(lobby.generated.name,'')),node('span','mp-muted','Played in freeplay and scored by AimMod. Ranked leaderboards are never involved.'));right.appendChild(g);}
    // More options
    var open=advancedOpen===null?advancedChanged(s):advancedOpen;
    var more=node('div','panel mp-more'+(open?' open':''));page.appendChild(more);
    var head=node('div','mp-more-head');var ht=node('div','mp-more-text');
    add(ht,node('strong','','More options'),node('span','',open?'Map, loadout, targets and lobby rules.':advancedSummary(s)));
    add(head,ht,actions(button(open?'Hide':'Show',function(){advancedOpen=!open;if(!advancedOpen&&picker&&picker!=='scenario')picker=null;render();},'compact')));
    more.appendChild(head);
    if(open){
      if(!overrides)more.appendChild(node('p','mp-lock','Score race plays the scenario exactly as published, so scores compare with everyone’s history. Pick another mode to change the map, time, loadout or targets.'));
      var mc=node('div','mp-row');more.appendChild(mc);var c1=node('div','mp-col mp-half'),c2=node('div','mp-col mp-half');mc.appendChild(c1);mc.appendChild(c2);
      // Map
      var mp=section('Map',overrides?'Play the scenario on another map, including ported maps.':locked);
      var mapBtn=node('button','mp-pick'+(overrides?'':' locked'));mapBtn.type='button';add(mapBtn,node('strong','',s.mapOverride?safe(s.mapOverride.name,'Map'):'Scenario map'),node('span','',s.mapOverride?(s.mapOverride.source==='ported'?'Ported map':'Custom map'):'The map the scenario was made for'));
      mapBtn.disabled=!overrides;mapBtn.onclick=function(){picker=picker==='map'?null:'map';pickerQuery='';loadLibrary();render();};mp.appendChild(mapBtn);
      if(overrides&&s.mapOverride)mp.appendChild(actions(button('Use the scenario map',function(){setting('mapOverride',null);},'compact quiet')));
      if(picker==='map'&&overrides)mp.appendChild(pickerList('map',s));
      mp.appendChild(actions(button('Browse the map library',openMaps,'compact quiet')));
      c1.appendChild(mp);
      // Loadout
      var lo=section('Loadout',overrides?'Presets give everyone the same weapon and movement feel.':locked);
      lo.appendChild(profileRow('Weapon','weapon',s.weapon,PRESETS,overrides));
      lo.appendChild(profileRow('Movement','movement',s.movement,PRESETS.filter(function(p){return p.id!=='custom';}),overrides));
      lo.appendChild(profileRow('Character','character',s.character,[PRESETS[0],PRESETS[5]],overrides));
      c1.appendChild(lo);
      // Targets
      var tg=section('Targets',overrides?'Bot speed and size, like KovaaK’s freeplay settings.':locked);
      tg.appendChild(settingRow('Target speed','',stepper(s.targetSpeed,0.25,3,0.05,multiplier,function(v){setting('targetSpeed',v);},!overrides,'target speed')));
      tg.appendChild(settingRow('Target size','',stepper(s.targetSize,0.25,2,0.05,multiplier,function(v){setting('targetSize',v);},!overrides,'target size')));
      if(overrides&&(s.targetSpeed!==1||s.targetSize!==1))tg.appendChild(actions(button('Reset targets',function(){act('settings',{settings:{targetSpeed:1,targetSize:1}});},'compact quiet')));
      c2.appendChild(tg);
      // Lobby rules
      var rl=section('Lobby rules');
      rl.appendChild(settingRow('Countdown','Seconds before everyone starts.',stepper(s.countdown,3,10,1,function(v){return F.number(v,0)+' s';},function(v){setting('countdown',v);},false,'countdown')));
      rl.appendChild(settingRow('Spectators','Up to 4 people can watch.',toggleSwitch(s.spectators,'Spectators',function(){setting('spectators',!s.spectators);})));
      var lateOk=s.mode==='ffa-rounds'||s.mode==='practice';
      rl.appendChild(settingRow('Late join',lateOk?'Players who join mid-match play from the next round.':'Only free-for-all and practice allow it.',toggleSwitch(s.lateJoin,'Late join',function(){setting('lateJoin',!s.lateJoin);},!lateOk)));
      rl.appendChild(settingRow('Auto start','Starts by itself a few seconds after everyone is ready.',toggleSwitch(!!s.autoStart,'Auto start',function(){setting('autoStart',!s.autoStart);})));
      rl.appendChild(settingRow('Scenario suggestions','Players suggest scenarios and vote. You pick.',toggleSwitch(s.voting!==false,'Scenario suggestions',function(){setting('voting',s.voting===false);})));
      c2.appendChild(rl);
    }
    page.appendChild(setupsPanel());
  }
  function settingRow(title,note,control){var r=node('div','mp-setting');var t=node('div','mp-setting-text');add(t,node('strong','',title));if(note)t.appendChild(node('span','',note));add(r,t,control);return r;}
  function profileRow(title,key,value,options,enabled){
    var v=value||{preset:'default'};var box=node('div','mp-profile');
    var t=node('div','mp-setting-text');add(t,node('strong','',title),node('span','',presetNote(key,v)));box.appendChild(t);
    box.appendChild(segmented(options.map(function(o){return {id:o.id,label:o.id==='default'?'Default':o.id==='custom'?'Custom':o.label.replace('-like','')};}),v.preset,function(id){if(id==='custom'){picker=key;pickerQuery='';loadLibrary();render();}else setting(key,id);},!enabled,title));
    if(enabled&&(picker===key||v.preset==='custom'&&picker===key))box.appendChild(pickerList(key,null));
    return box;
  }
  function presetNote(key,v){
    if(v.preset==='custom')return 'From your library: '+safe(v.custom,'custom');
    if(library&&library.presets)for(var i=0;i<library.presets.length;i++)if(library.presets[i].id===v.preset)return key==='weapon'?library.presets[i].weapon:key==='movement'?library.presets[i].movement:'The scenario’s own character';
    return v.preset==='default'?'As the scenario defines it':preset(v.preset).label;
  }
  function pickerList(kind,s){
    var box=node('div','mp-picker');
    if(!library){box.appendChild(node('div','mp-muted','Loading your library…'));return box;}
    if(!library.available){box.appendChild(node('div','mp-muted','AimMod couldn’t find your KovaaK’s folder.'));return box;}
    drafts.picker=pickerQuery;
    var input=trackInput(node('input','mp-picker-search'),'picker');input.setAttribute('autocomplete','off');
    input.onchanged=function(){pickerQuery=input.value;fill();};
    box.appendChild(field(input,kind==='scenario'?'Find a scenario':kind==='map'?'Find a map':'Find a profile'));
    var list=node('div','mp-picker-list');box.appendChild(list);
    function items(){var src=kind==='scenario'||kind==='suggest'?library.scenarios:kind==='map'?library.maps:kind==='weapon'?library.weapons:library.characters;return (src||[]).map(function(x){return typeof x==='string'?{name:x}:x;});}
    var scen=kind==='scenario'||kind==='suggest',picks=(view&&view.picks)||{favourites:[],recent:[]};
    function isFav(name){return (picks.favourites||[]).indexOf(name)>=0;}
    function fill(){
      while(list.firstChild)list.removeChild(list.firstChild);
      var q=(pickerQuery||'').toLowerCase(),shown=0,all=items(),byName={};
      all.forEach(function(x){byName[x.name]=x;});
      // Without a search, favourites and recent scenarios come first.
      if(scen&&!q){
        [['Favourites',picks.favourites||[]],['Recent',picks.recent||[]]].forEach(function(g){
          var found=g[1].filter(function(n){return !!byName[n];}).slice(0,g[0]==='Recent'?6:20);
          if(!found.length)return;list.appendChild(node('div','mp-pick-group',g[0]));
          found.forEach(function(n){list.appendChild(entry(byName[n]));});
        });
        if(list.firstChild)list.appendChild(node('div','mp-pick-group','All scenarios'));
      }
      all.forEach(function(x){if(shown>=80||(q&&String(x.name).toLowerCase().indexOf(q)<0))return;shown++;list.appendChild(entry(x));});
      if(!shown)list.appendChild(node('div','mp-muted',all.length?'Nothing matches. Try a shorter search.':'Nothing in your library yet.'));
      else if(all.length>shown&&!q)list.appendChild(node('div','mp-muted','Showing '+shown+' of '+F.number(all.length,0)+'. Type to narrow the list.'));
    }
    function entry(x){
        var b=node('button','mp-pick-item');b.type='button';
        var info=node('span','mp-pick-info');add(info,node('strong','',safe(x.name,'Untitled')));
        if(kind==='scenario'||kind==='suggest')info.appendChild(node('span','','Map '+safe(x.map,'')+' · '+F.duration(x.timeLimit)+(x.defaultWeapon?' · '+safe(x.defaultWeapon,''):'')));
        b.appendChild(info);
        if(kind==='map'||kind==='scenario'||kind==='suggest'){var src=kind==='map'?x.source:x.mapSource;b.appendChild(chip(src==='ported'?'Ported':src==='custom'?'Custom map':'Built-in',src==='ported'?'mint':''));}
        b.onclick=function(){picker=null;if(kind==='suggest'){suggesting=false;act('suggest',{scenario:x.name});return;}if(kind==='scenario'||kind==='map')setting(kind==='map'?'mapOverride':'scenario',x.name);else setting(kind,{preset:'custom',custom:x.name});};
        if(!scen)return b;
        var row=node('div','mp-pick-row');row.appendChild(b);
        var fav=isFav(x.name),star=button('',function(){act('favourite',{scenario:x.name,on:!fav},function(ok){if(ok){picks=view.picks||picks;fill();}});},'compact quiet mp-fav'+(fav?' on':''));
        star.appendChild(starIcon(fav));star.setAttribute('aria-label',fav?'Remove from favourites':'Add to favourites');star.setAttribute('aria-pressed',String(fav));
        row.appendChild(actions(star));
        return row;
    }
    fill();
    return box;
  }
  function starIcon(on){
    var c=node('canvas','mp-star');c.width=28;c.height=28;var x=c.getContext&&c.getContext('2d');
    if(x){x.scale(2,2);x.beginPath();for(var i=0;i<10;i++){var r=i%2?2.6:6.2,a=-Math.PI/2+i*Math.PI/5;x.lineTo(7+r*Math.cos(a),7.4+r*Math.sin(a));}x.closePath();
      if(on){x.fillStyle='#f0b45a';x.fill();}else{x.strokeStyle='#7f968a';x.lineWidth=1.2;x.stroke();}}
    return c;
  }
  function contentTable(lobby){
    var box=node('div','mp-content-table');
    lobby.members.filter(function(m){return m.role==='player';}).forEach(function(m){var c=contentState(m,lobby);var r=node('div','mp-content-row');add(r,avatar(m.name,true),node('span','mp-content-name',safe(m.name)),node('span','mp-content '+c.kind,c.text));box.appendChild(r);});
    return box;
  }

  // Match screens ------------------------------------------------------------
  function roundLabel(match){if(match.mode==='tracking-duel')return 'Round '+match.round+' of '+match.totalRounds+(match.attacker?' · '+(match.attacker===(view&&view.lobby&&view.lobby.self)?'you track':nameOf(match.attacker)+' tracks'):'');return match.mode==='duel'?'Round '+match.round+' · first to '+match.firstTo:match.totalRounds?'Round '+match.round+' of '+match.totalRounds:'Run '+match.round;}
  function matchScreen(page,lobby){
    if(lobby.spectate)page.appendChild(add(banner('info','Spectating '+safe(lobby.spectate.name)+' · '+statLine(lobby.spectate.score)+'. Their view plays in the pause menu.'),actions(button('Stop',function(){act('spectate-stop');},'compact quiet'))));
    var match=lobby.match;
    connectionBanners(page,lobby);
    if(match.phase==='loading')loadingStage(page,lobby,match);
    else if(match.phase==='countdown')countdown(page,lobby,match);
    else if(match.phase==='live')live(page,lobby,match);
    else roundResults(page,lobby,match);
  }
  function planBox(lobby){
    var r=lobby.round;if(!r)return null;
    var box=node('div','mp-plan '+(r.state==='error'?'warn':r.state==='manual'||r.state==='blocked'?'manual':'ok'));
    add(box,node('strong','',r.state==='blocked'?'Finish your current run':r.state==='manual'?'Start it yourself':r.state==='error'?'Start it yourself':r.mode==='freeplay'?'Match scenario, freeplay':'Normal KovaaK’s run'),node('span','',safe(r.message,'')));
    return box;
  }
  function countdown(page,lobby,match){
    var stage=node('div','mp-stage');page.appendChild(stage);
    var ring=node('div','mp-count');var digits=node('div','mp-count-num',String(seconds(match.startsAt-now())));ring.appendChild(digits);
    countNodes.push({node:digits,at:match.startsAt,format:function(ms){return String(Math.max(1,seconds(ms)));}});
    add(stage,node('div','eyebrow',mode(match.mode).label+' · '+roundLabel(match)),ring,node('h2','',safe(match.scenario,'Scenario')),node('p','subtle',match.players.indexOf(lobby.self)>=0?'Get your hand on the mouse. Everyone starts together.':'You’re spectating this round. Pick who to follow once it starts.'));
    var plan=planBox(lobby);if(plan)stage.appendChild(plan);
    var who=node('div','mp-stage-players');match.players.forEach(function(id){var m=member(id);add(who,add(node('div','mp-stage-player'),avatar(nameOf(id),true),node('span','',nameOf(id)),m&&m.connection==='reconnecting'?chip('Reconnecting','amber'):null));});
    stage.appendChild(who);
    if(lobby.isHost)stage.appendChild(actions(button('Cancel match',function(){act('end');},'compact quiet danger')));
  }
  function liveRows(lobby,match){
    var rows=match.live.slice().sort(function(a,b){return (b.score||0)-(a.score||0);});
    var leader=rows.length&&rows[0].score!==null?rows[0].score:null;
    return rows.map(function(l,i){return {line:l,rank:i+1,gap:leader!==null&&l.score!==null&&i>0?l.score-leader:null};});
  }
  // The compact scoreboard, styled like the in-game HUD.
  function hud(lobby,match){
    var card=node('div','mp-hud');var head=node('div','mp-hud-head');
    var left=node('span','mp-hud-time','');countNodes.push({node:left,at:(match.startsAt||0)+match.timeLimit*1000,format:function(ms){return F.duration(Math.max(0,ms/1000))+' left';}});
    add(head,node('span','mp-hud-brand','AIMMOD · '+mode(match.mode).short.toUpperCase()),node('span','mp-hud-round',roundLabel(match)),left);card.appendChild(head);
    var limit=match.timeLimit||60;
    liveRows(lobby,match).forEach(function(r){
      var l=r.line;var row=node('div','mp-hud-row'+(l.memberId===lobby.self?' self':'')+(r.rank===1&&l.score?' lead':'')+(lobby.spectate&&lobby.spectate.member===l.memberId?' watched':''));
      var status=l.status==='finished'?'Done':l.status==='left'?'Left':l.status==='dnf'?'DNF':l.status==='waiting'?'Starting':null;
      add(row,node('span','mp-hud-rank',String(r.rank)),node('span','mp-hud-name',nameOf(l.memberId)),status?node('span','mp-hud-status',status):null,node('span','mp-hud-score',l.score===null?'—':F.number(l.score,0)),node('span','mp-hud-gap',r.gap===null?'':F.signed(r.gap,0)));
      var track=node('div','mp-hud-track');var fill=node('div','mp-hud-fill');fill.style.width=Math.min(100,Math.max(0,(l.seconds||0)/limit*100))+'%';track.appendChild(fill);
      var wrap=node('div','mp-hud-line');add(wrap,row,track);card.appendChild(wrap);
    });
    return card;
  }
  function live(page,lobby,match){
    var row=node('div','mp-row');page.appendChild(row);var main=node('div','mp-col mp-main'),side=node('div','mp-col mp-side');row.appendChild(main);row.appendChild(side);
    var p=node('div','panel mp-live');var head=node('div','panel-head');var text=node('div','head-text');add(text,node('h2','','Live scores'),node('p','',safe(match.scenario,'Scenario')));head.appendChild(text);
    // The host's End match lives with the scores, not under a long spectate list.
    if(lobby.isHost)head.appendChild(actions(button('End match',function(){act('end');},'compact quiet danger')));p.appendChild(head);
    var body=node('div','mp-live-body');body.appendChild(hud(lobby,match));p.appendChild(body);main.appendChild(p);
    var playing=match.players.indexOf(lobby.self)>=0;
    if(playing){var you=node('div','panel mp-card');add(you,node('h2','','Your run'));var plan=planBox(lobby);if(plan)you.appendChild(plan);else you.appendChild(node('p','subtle','Play the round in KovaaK’s. Your score streams to the lobby as you play.'));
      you.appendChild(node('p','mp-note','Each score comes from that player’s own run in their game.'));
      side.appendChild(you);}
    var others=match.players.filter(function(id){var m=member(id);return id!==lobby.self&&m&&!m.simulated;});
    keepSpectateView(lobby);
    if(others.length)side.appendChild(spectatePanel(lobby,match,others));
    else if(!playing)side.appendChild(add(node('div','panel mp-card'),node('h2','','You’re spectating'),node('p','subtle','The players here are simulated, so there’s no camera to follow. Their scores update live.')));
    if(match.rounds.length&&match.mode!=='practice'){var st=node('div','panel');var sh=node('div','panel-head');add(sh,node('h2','','Standings so far'));st.appendChild(sh);var sb=node('div','panel-body');sb.appendChild(standingsTable(match));st.appendChild(sb);side.appendChild(st);}
  }
  function placementTable(results,mode,showPoints){
    var t=node('div','mp-table');var head=node('div','mp-tr head');add(head,node('span','mp-td place',''),node('span','mp-td name','Player'),node('span','mp-td num','Score'),node('span','mp-td num','Accuracy'),showPoints?node('span','mp-td num',mode==='duel'?'Win':'Points'):null);t.appendChild(head);
    results.forEach(function(r){var row=node('div','mp-tr'+(r.memberId===(view.lobby&&view.lobby.self)?' self':''));
      var place=node('span','mp-td place');if(r.place)place.appendChild(node('span','mp-medal p'+Math.min(r.place,4),String(r.place)));
      var name=node('span','mp-td name');add(name,avatar(r.name,true),node('span','',safe(r.name)),r.status==='dnf'?chip('Did not finish','amber'):r.status==='left'?chip('Left','amber'):null,r.disputed?chip('Disputed','rose'):null);
      add(row,place,name,node('span','mp-td num',r.score===null?'—':F.number(r.score,0)),node('span','mp-td num',F.percent(r.accuracy)),showPoints?node('span','mp-td num',mode==='duel'?(r.points?'+1':''):'+'+F.number(r.points,0)):null);t.appendChild(row);});
    return t;
  }
  function standingsTable(match){
    var t=node('div','mp-table');var duel=match.mode==='duel',ffa=match.mode==='ffa-rounds';
    var head=node('div','mp-tr head');add(head,node('span','mp-td place',''),node('span','mp-td name','Player'),node('span','mp-td num',duel?'Wins':ffa?'Points':'Best'),node('span','mp-td num',duel||ffa?'Best':'Total'));t.appendChild(head);
    match.standings.forEach(function(s){var row=node('div','mp-tr'+(s.memberId===view.lobby.self?' self':''));var place=node('span','mp-td place');if(s.place)place.appendChild(node('span','mp-medal p'+Math.min(s.place,4),String(s.place)));
      var name=node('span','mp-td name');add(name,avatar(s.name,true),node('span','',safe(s.name)));
      add(row,place,name,node('span','mp-td num strong',duel?F.number(s.wins,0):ffa?F.number(s.points,0):(s.best===null?'—':F.number(s.best,0))),node('span','mp-td num',duel||ffa?(s.best===null?'—':F.number(s.best,0)):F.number(s.total,0)));t.appendChild(row);});
    return t;
  }
  function roundResults(page,lobby,match){
    var last=match.rounds[match.rounds.length-1];if(!last)return;
    var hero=node('div','panel mp-result-hero');var winner=last.winnerId;
    var tracked=match.mode==='tracking-duel'&&last.results[0]?last.results[0]:null;
    add(hero,node('div','eyebrow',mode(match.mode).label+' · '+roundLabel(match)),node('h2','',tracked?(tracked.memberId===lobby.self?'You':nameOf(tracked.memberId))+' tracked '+(tracked.score===null?'—':F.number(tracked.score,1)+' %'):match.mode==='practice'?'Run '+match.round+' done':winner?(winner===lobby.self?'You take the round':nameOf(winner)+' takes the round'):'Round drawn'));
    var next=node('p','subtle','');countNodes.push({node:next,at:match.nextAt,format:function(ms){return 'Next round in '+seconds(ms)+' s';}});hero.appendChild(next);
    if(lobby.isHost)hero.appendChild(actions(button('Next round now',function(){act('next');},'compact primary'),button(match.mode==='practice'?'End session':'End match',function(){act('end');},'compact quiet danger')));
    page.appendChild(hero);
    var row=node('div','mp-row');page.appendChild(row);var main=node('div','mp-col mp-main'),side=node('div','mp-col mp-side');row.appendChild(main);row.appendChild(side);
    var p=node('div','panel');var h=node('div','panel-head');add(h,node('h2','','Round '+last.round));p.appendChild(h);var body=node('div','panel-body');body.appendChild(placementTable(last.results,match.mode,match.mode!=='practice'&&match.mode!=='score-race'));p.appendChild(body);main.appendChild(p);
    var rr=runVsRun(lobby,last.round);if(rr)main.appendChild(rr);
    var st=node('div','panel');var sh=node('div','panel-head');add(sh,node('h2','',match.mode==='practice'?'Best so far':'Standings'));st.appendChild(sh);var sb=node('div','panel-body');sb.appendChild(standingsTable(match));st.appendChild(sb);side.appendChild(st);
  }
  function finalScreen(page,lobby){
    var match=lobby.match,top=match.standings[0];
    connectionBanners(page,lobby);
    var hero=node('div','panel mp-final');var practice=match.mode==='practice';
    var title=practice?'Session complete':match.winnerId===lobby.self?'You win!':match.winnerId?nameOf(match.winnerId)+' wins':'It’s a draw';
    var crownBox=node('div','mp-final-mark');if(match.winnerId)crownBox.appendChild(crown());
    var me=null;match.standings.forEach(function(s){if(s.memberId===lobby.self)me=s;});
    add(hero,crownBox,node('div','eyebrow',mode(match.mode).label+' · '+safe(match.scenario,'Scenario')),node('h2','',title),node('p','subtle',practice?'Your best runs are below.':me&&me.place?'You finished '+ordinal(me.place)+' of '+match.standings.length+'.':'Final standings below.'));
    var votes=match.rematch.length,needed=match.players.length;
    var voted=match.rematch.indexOf(lobby.self)>=0,isPlayer=match.players.indexOf(lobby.self)>=0;
    var rematch=button(voted?'Waiting for others…':'Rematch',function(){act('rematch');},'primary mp-big');if(voted||!isPlayer)rematch.disabled=true;
    hero.appendChild(actions(rematch,lobby.isHost?button('Back to lobby',function(){act('end');}):null,button('Leave',function(){act('leave');},'quiet danger')));
    var closes=node('p','mp-note',votes?votes+' of '+needed+' want a rematch.':'Rematch starts when every player asks for one. Players who don’t answer in 20 seconds sit it out.');hero.appendChild(closes);
    if(match.rematchDeadline)countNodes.push({node:closes,at:match.rematchDeadline,format:function(ms){return votes+' of '+needed+' want a rematch · starts in '+seconds(ms)+' s with whoever confirmed.';}});
    page.appendChild(hero);
    var row=node('div','mp-row');page.appendChild(row);var main=node('div','mp-col mp-main'),side=node('div','mp-col mp-side');row.appendChild(main);row.appendChild(side);
    var st=node('div','panel');var sh=node('div','panel-head');var shText=node('div','head-text');add(shText,node('h2','','Final standings'),node('p','','Kept in AimMod only. KovaaK’s leaderboards are never changed.'));sh.appendChild(shText);st.appendChild(sh);var sb=node('div','panel-body');sb.appendChild(standingsTable(match));st.appendChild(sb);main.appendChild(st);
    var rounds=node('div','panel');var rh=node('div','panel-head');add(rh,node('h2','','Rounds'));rounds.appendChild(rh);var list=node('div','mp-list');
    match.rounds.forEach(function(r){var best=r.results[0];var line=node('div','mp-round-line');
      add(line,node('span','mp-round-no','R'+r.round),avatar(r.winnerId?nameOf(r.winnerId):best?best.name:'?',true),node('span','mp-round-win',r.winnerId?nameOf(r.winnerId):practice&&best?safe(best.name)+' (best run)':'Draw'),node('span','mp-round-score',best&&best.score!==null?F.number(best.score,0):'—'));list.appendChild(line);});
    rounds.appendChild(list);side.appendChild(rounds);
    var rv=runVsRun(lobby);if(rv)side.appendChild(rv);
    if(top&&!top.name)return;
  }

  // Countdowns move between polls without re-rendering.
  function tick(){
    clearTimeout(ticker);if(!container)return;
    var t=now();for(var i=0;i<countNodes.length;i++){var c=countNodes[i];if(c.at)c.node.textContent=c.format(c.at-t);}
    if(countNodes.length)ticker=setTimeout(tick,100);
  }

  function enter(element){leave();container=element;generation++;if(!container)return;clear();var p=node('div','panel mp-card mp-joining');add(p,node('div','mp-spinner'),node('p','subtle','Loading multiplayer…'));container.appendChild(p);lastKey='';poll();}
  function leave(){generation++;clearTimeout(deferred);deferred=null;clearTimeout(timer);clearTimeout(ticker);timer=null;ticker=null;inflight=false;again=false;if(container)clear();container=null;toastNode=null;}
  root.AimModMultiplayer={enter:enter,leave:leave,resize:function(){if(container&&view)render();},_state:function(){return {view:view,editing:editing,picker:picker};}};
})(window);
