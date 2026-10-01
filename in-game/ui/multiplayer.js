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
    {id:'score-race',group:'score',label:'Score race',short:'Race',text:'Same scenario, best score wins.',detail:'2 to 8 players · one run each · best score wins'},
    {id:'duel',group:'score',label:'Score duel',short:'Duel',text:'One on one on the scenario’s targets. Win rounds by scoring higher.',detail:'2 players · rounds of the same scenario · first to the set wins'},
    {id:'ffa-rounds',group:'score',label:'Free-for-all',short:'FFA',text:'Several rounds, points for placing.',detail:'2 to 8 players · several rounds · placement points'},
    {id:'practice',group:'score',label:'Practice together',short:'Practice',text:'Side by side, live scores, no ranking.',detail:'Any number · live scores · nothing ranked'},
    {id:'tracking-duel',group:'pvp',label:'Tracking duel',short:'Tracking',text:'Both track each other at once. Most time on target wins.',detail:'2 players · track and dodge at the same time · time on target wins'},
    {id:'deathmatch',group:'pvp',label:'Deathmatch',short:'DM',text:'Everyone against everyone, first to the frag limit.',detail:'2 to 8 players · frag limit · respawns'},
    {id:'vampiric',group:'pvp',label:'Vampiric 1v1',short:'Vampiric',text:'One on one. Damage heals you, health drains.',detail:'2 players · health drains over time · hits heal you'},
    {id:'instagib',group:'pvp',label:'Instagib',short:'Instagib',text:'Every hit kills. First to the frag limit.',detail:'2 to 8 players · one hit, one kill · frag limit'},
    {id:'team-deathmatch',group:'pvp',label:'Team deathmatch',short:'TDM',text:'Two teams. First team to the frag limit. No friendly fire.',detail:'4 to 8 players · two teams · team frag limit'},
    {id:'cs',label:'CS competitive',short:'CS',text:'3v3 to 5v5. Buy, plant and defuse; halves and sides like CS2.',group:'pvp',detail:'3v3 to 5v5 · CS2 economy · plant and defuse'}
  ];
  var MODE_GROUPS=[{id:'score',label:'Score modes',text:'Same targets, compare scores.'},{id:'pvp',label:'PvP modes',text:'Fight each other. No targets.'}];
  // Card art: the mode's line icon (ui/art/modes, rasterised by tools/mode-icons) centred in a
  // subtle panel. Mint when selected, green-grey otherwise; 44 px on cards, 96 px on the hero.
  var MODE_ICONS=['score-race','duel','ffa-rounds','practice','tracking-duel','deathmatch','vampiric','instagib','team-deathmatch','cs'];
  function modeArt(id,on,big){
    var box=node('div','mp-mode-art');box.setAttribute('aria-hidden','true');
    if(MODE_ICONS.indexOf(id)<0)return box;
    var img=node('img','mp-mode-icon');img.setAttribute('alt','');img.draggable=false;
    img.src='art/modes/'+id+(on?'-on':'')+(big?'@3x':'@2x')+'.png';
    box.appendChild(img);return box;
  }
  // The mode picker: score modes and PvP modes under their own headings, with art on every card.
  function modePicker(selected,choose,compact){
    var box=node('div','mp-modes'+(compact?' compact':''));
    MODE_GROUPS.forEach(function(g){
      var head=node('div','mp-mode-group');add(head,node('strong','',g.label),node('span','',g.text));box.appendChild(head);
      var pick=node('div','mp-mode-pick '+g.id+(compact?' compact':''));
      MODES.filter(function(m){return m.group===g.id;}).forEach(function(m){var on=m.id===selected;var b=node('button','mp-mode'+(on?' on':''));b.type='button';b.setAttribute('aria-pressed',String(on));
        add(b,add(node('span','mp-mode-pic'),modeArt(m.id,on,false)),node('strong','',m.label),node('span','mp-mode-text',m.text));b.onclick=function(){choose(m.id);};pick.appendChild(b);});
      box.appendChild(pick);
    });
    return box;
  }
  // The selected mode, larger, with what the lobby starts with.
  function modeHero(id){var m=mode(id);var card=node('div','mp-mode-hero');add(card,add(node('span','mp-mode-pic'),modeArt(m.id,true,true)),add(node('div','mp-mode-hero-text'),node('div','eyebrow',m.group==='pvp'?'PvP mode':'Score mode'),node('strong','',m.label),node('span','',m.text),node('span','mp-mode-detail',m.detail)));return card;}
  function combat(m){return m==='deathmatch'||m==='vampiric'||m==='instagib'||m==='team-deathmatch';}
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
  // Names partly in a script the game font lacks (emoji, CJK) keep the part it can draw; only names with
  // nothing drawable fall back (a Steam name of only such characters showed as "Friend" / "?").
  function safe(text,fallback){var t=typeof text==='string'?text:'',out=F.safeText(t,'');if(out)return out;
    var part=t.replace(/[^\u0000-\u024F\u0370-\u03FF\u0400-\u04FF\u1E00-\u1EFF\u2010-\u2027\u2030-\u205E]/g,'').replace(/[\u0000-\u001F\u007F]/g,'').replace(/\s+/g,' ').trim();
    // Letters left: drop the decoration around them. Only punctuation left (like "-.-"): keep it as it is.
    if(/[0-9A-Za-z\u00C0-\u024F\u0370-\u03FF\u0400-\u04FF]/.test(part))return part.replace(/^[\s_.\-|()\[\]]+|[\s_.\-|()\[\]]+$/g,'');
    return part||fallback||'Player';}
  // Up to two initials; a name of only punctuation ("-.-") shows its first two characters instead of "?".
  function initials(name){var clean=safe(name,'?'),parts=clean.replace(/[_.()\[\]-]+/g,' ').trim().split(/\s+/);if(!parts[0])return clean.replace(/\s+/g,'').slice(0,2)||'?';var a=(parts[0]||'?').charAt(0),b=parts.length>1?parts[parts.length-1].charAt(0):(parts[0]||'').charAt(1);return (a+(b||'')).toUpperCase();}
  function tone(name){var h=0,s=String(name||'');for(var i=0;i<s.length;i++)h=(h*31+s.charCodeAt(i))%9973;return AVATAR[h%AVATAR.length];}
  // Initials in a coloured circle; with a Steam picture link (the service's /avatar/<id>.png) the picture
  // covers them once it loads, at the same size. Pictures seen before show at once, so re-renders don't flash.
  var pictures={};
  function avatar(name,small,url){var a=node('div','mp-avatar '+tone(name)+(small?' small':''),initials(name));a.setAttribute('aria-hidden','true');if(picture(url))photo(a,url);return a;}
  function picture(url){return typeof url==='string'&&/^\/avatar\/[0-9]{16,20}\.png(\?v=[0-9a-f]{8})?$/.test(url)&&pictures[url]!==false;}
  function photo(a,url){var img=node('img','mp-avatar-img');img.setAttribute('alt','');img.draggable=false;var base=a.className;if(pictures[url])a.className=base+' pic';
    img.onload=function(){pictures[url]=true;a.className=base+' pic';};img.onerror=function(){pictures[url]=false;a.className=base;if(img.parentNode)img.parentNode.removeChild(img);};
    img.src=path()+url;a.appendChild(img);}
  // A member's, player's or friend's picture link by id.
  function pic(id){var l=view&&view.lobby;if(l&&l.avatars&&l.avatars[id])return l.avatars[id];var f=view&&view.friends&&view.friends.items||[];for(var i=0;i<f.length;i++)if(f[i].id===id)return f[i].avatar;return null;}
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
  function renderError(){if(!container)return;clear();var p=node('div','panel mp-card mp-joining mp-error');add(p,node('h2','','Can’t reach AimMod right now'),node('p','subtle','AimMod keeps trying. If this lasts over a minute, restart KovaaK’s.'));p.appendChild(actions(button('Try again',function(){poll();},'primary')));container.appendChild(p);}
  function render(){
    if(!container||!view)return;
    rendering=true;lastRender=Date.now();clearTimeout(deferred);deferred=null;
    clear();
    var page=node('div','mp-page');container.appendChild(page);
    if(view.notice)page.appendChild(banner(view.notice.kind==='error'?'warn':'info',view.notice.text,true));
    var l=view.lobby;
    page.appendChild(tabStrip());
    if(tab==='look')lookPage(page);
    else if(view.joining&&!l)page.appendChild(joining());
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
    add(left,node('h2','','Play KovaaK’s together'),node('p','mp-lead','Results stay in AimMod and never touch KovaaK’s leaderboards.'));
    left.appendChild(modePicker(quickMode,function(id){quickMode=id;render();},false));
    left.appendChild(modeHero(quickMode));
    var create=button('Create lobby',function(){act('create',{mode:quickMode});},'primary mp-big');
    left.appendChild(actions(create));
    if(!view.library.available)left.appendChild(node('p','mp-note','Couldn’t find your KovaaK’s scenarios. You can still join lobbies.'));
    var right=node('div','mp-hero-side');
    add(right,node('h3','','Join a friend'),view.transport.online?node('p','subtle','Or use Join on a friend below.'):null);
    var input=trackInput(node('input','mp-code'),'code');input.setAttribute('data-draft','code');input.setAttribute('maxlength','7');input.setAttribute('autocomplete','off');
    input.onkeydown=function(e){if((e||root.event).keyCode===13)joinCode();};
    var codeRow=node('div','mp-code-row');add(codeRow,field(input,'Room code','mp-code-field'),actions(button('Join',joinCode)));
    right.appendChild(codeRow);
    right.appendChild(steamState());
    right.appendChild(add(node('div','mp-hero-link'),actions(button('Map library',openMaps,'compact'))));
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
    var p=node('div','panel mp-runs');var head=node('div','panel-head');var text=node('div','head-text');add(text,node('h2','','Watch run vs run'));head.appendChild(text);p.appendChild(head);
    list.forEach(function(r){r.others.forEach(function(o){var row=node('div','mp-friend');add(row,avatar(o.name,true,pic(o.id)),add(node('div','mp-friend-info'),node('strong','',safe(o.name)),node('span','','Round '+r.round)));row.appendChild(actions(button('You vs '+safe(o.name),function(){watch(r.mine,o.id);},'compact')));p.appendChild(row);});});
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
  // Warm-up: everyone's map must load (the service checks KovaaK's scene) before the countdown.
  // It never starts without someone: a failed load waits for the host to retry or end the match.
  function loadingStage(page,lobby,match){
    var stage=node('div','mp-stage');page.appendChild(stage);
    var loaded=match.loaded||[],issues=match.loadIssues||{},failed=match.loadFailed===true;
    var present=match.players.filter(function(id){return !!member(id);});
    var ready=present.filter(function(id){return loaded.indexOf(id)>=0;}).length;
    add(stage,node('div','eyebrow',mode(match.mode).label+' · getting ready'),failed?null:node('div','mp-spinner'),
      node('h2','',failed?'Couldn’t load the match ('+ready+'/'+present.length+')':'Waiting for everyone to load ('+ready+'/'+present.length+')'),
      node('p','subtle',failed?(lobby.isHost?'Retry the load, or end the match.':'Waiting for the host to retry or end the match.'):'Loading '+safe(match.scenario,'the scenario')+'…'));
    var plan=planBox(lobby);if(plan)stage.appendChild(plan);
    var keys=match.players.indexOf(lobby.self)>=0?bindsNote(lobby):null;if(keys)stage.appendChild(keys);
    var who=node('div','mp-stage-players');match.players.forEach(function(id){
      var ok=loaded.indexOf(id)>=0,issue=typeof issues[id]==='string'?issues[id]:null;
      var row=add(node('div','mp-stage-player'+(ok?' ok':'')),avatar(nameOf(id),true,pic(id)),node('span','',nameOf(id)),chip(ok?'Ready':issue?'Not loaded':'Loading…',ok?'mint':issue?'amber':''));
      if(!ok&&issue)row.title=issue;
      add(who,row);
      if(!ok&&issue)add(who,node('p','mp-note',nameOf(id)+': '+issue));
    });
    stage.appendChild(who);
    if(!failed){var left=node('p','mp-note','');countNodes.push({node:left,at:match.nextAt,format:function(ms){return 'If not everyone has loaded in '+seconds(ms)+' s, the host can retry or end the match.';}});stage.appendChild(left);}
    if(lobby.isHost)stage.appendChild(actions(failed?button('Retry',function(){act('retry-load');},'compact primary'):null,button(failed?'End match':'Cancel match',function(){act('end');},'compact quiet danger')));
  }
  // Scenario suggestions and votes; the host picks.
  var suggesting=false;
  function suggestionsPanel(lobby){
    var list=lobby.suggestions||[];if(lobby.settings.voting===false&&!list.length)return null;
    var p=node('div','panel mp-suggest');var head=node('div','panel-head');var text=node('div','head-text');add(text,node('h2','','Suggestions'));head.appendChild(text);
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
    var p=node('div','panel mp-setups');var head=node('div','panel-head');var text=node('div','head-text');add(text,node('h2','','Saved setups'),node('p','','New lobbies start from your last setup.'));head.appendChild(text);p.appendChild(head);
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
    var head=node('div','mp-watch-head');add(head,avatar(w.name,false,w.avatar),add(node('div','mp-watch-text'),node('div','eyebrow',w.state==='watching'?'Spectating':'Spectate'),node('h2','',safe(w.name,'Friend')),node('p','subtle',(w.scenario?safe(w.scenario,'')+' · ':'')+safe(w.message,''))));
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
    add(text,node('h2','','Watching you'));head.appendChild(text);p.appendChild(head);
    (view.watchAsks||[]).forEach(function(a){var row=node('div','mp-friend');add(row,avatar(a.name,true,a.avatar),add(node('div','mp-friend-info'),node('strong','',safe(a.name,'Friend')),node('span','','Wants to watch you')));row.appendChild(actions(button('Allow',function(){act('spectate-allow',{id:a.peer});},'compact primary'),button('Deny',function(){act('spectate-deny',{id:a.peer});},'compact')));p.appendChild(row);});
    (view.watchers||[]).forEach(function(w){var row=node('div','mp-friend');add(row,avatar(w.name,true,w.avatar),add(node('div','mp-friend-info'),node('strong','',safe(w.name,'Friend')),node('span','','Watching')));row.appendChild(actions(button('Remove',function(){act('spectator-remove',{id:w.peer});},'compact quiet danger')));p.appendChild(row);});
    return p;
  }
  // How you appear in the other players' games.
  function lookPanel(lobby){
    var me=member(lobby.self)||{};var p=node('div','panel mp-look');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','','Your look'));head.appendChild(text);
    head.appendChild(actions(button('Change look',function(){openCosmetics();},'compact quiet')));p.appendChild(head);
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
    add(st,node('h2','',me?'You’re spectating':'Spectate'),node('p','',watching?(s.follow?'Following whoever leads, now ':'Watching ')+safe(s.name)+'. Their view is in the pause menu.':'Watch from the pause menu in the same scenario.'));sh.appendChild(st);sp.appendChild(sh);
    if(watching){var line=match.live.filter(function(l){return l.memberId===s.member;})[0];sp.appendChild(add(node('div','mp-watch-hud'),node('span','',line?statLine({active:true,score:line.score,accuracy:line.shots?line.hits*100/line.shots:null,remaining:line.remaining}):'Waiting for their run…')));}
    sp.appendChild(settingRow('Follow the leader','',toggleSwitch(!!(s&&s.follow),'Follow the leader',function(){followLeader(!(s&&s.follow));})));
    others.forEach(function(id){var on=watching&&s.member===id;var row=node('div','mp-friend'+(on?' on':''));add(row,avatar(nameOf(id),true,pic(id)),add(node('div','mp-friend-info'),node('strong','',nameOf(id)),node('span','',on?'Watching':'Player')));row.appendChild(actions(button(on?'Watching':'Spectate',function(){if(!on)spectate(id);},on?'compact primary':'compact')));sp.appendChild(row);});
    if(watching)sp.appendChild(actions(button('Stop spectating',stopSpectate,'compact quiet danger')));
    return sp;
  }
  // This player's own multiplayer preferences (saved on this PC).
  var prefsOpen=false;
  function prefsPanel(){
    var pr=view.prefs||{};var p=node('div','panel mp-prefs');var head=node('div','panel-head');var text=node('div','head-text');
    // Folded to a one-line summary until the player asks to change something.
    var spec={friends:'Friends can spectate you',ask:'Spectating asks you first',off:'Nobody can spectate you'};
    add(text,node('h2','','Your multiplayer settings'),prefsOpen?null:node('p','','Hotkey '+(pr.hotkey||'F7')+' · Sounds '+(pr.sounds?'on':'off')+' · '+(spec[pr.spectatePrivacy]||spec.friends)));head.appendChild(text);
    head.appendChild(actions(button(prefsOpen?'Done':'Change',function(){prefsOpen=!prefsOpen;render();},'compact')));p.appendChild(head);
    if(!prefsOpen){p.className+=' folded';((view.keys&&view.keys.conflicts)||[]).forEach(function(c){p.appendChild(node('p','mp-warn-text mp-prefs-warn',safe(c,'')));});return p;}
    var body=node('div','mp-prefs-body');p.appendChild(body);
    function pref(key,value){var o={};o[key]=value;act('prefs',{prefs:o});}
    function flag(key,title,note){body.appendChild(settingRow(title,note,toggleSwitch(!!pr[key],title,function(){pref(key,!pr[key]);})));}
    flag('readyOnJoin','Ready when I join','Once you have the content.');
    flag('readyOnContent','Ready after downloading','');
    flag('readyAfterMatch','Ready again after a match','');
    flag('quietDuringRanked','Quiet during ranked runs','No popups or hotkey during your own runs.');
    flag('sounds','Sounds','');
    flag('hideScenario','Hide my scenario from friends','');
    flag('leaveRun','Leave my run when a match starts','After a 5 s notice you can cancel.');
    flag('friendToasts','Tell me when friends start AimMod','Never during a run.');
    body.appendChild(settingRow('Who can spectate me','',segmented([{id:'friends',label:'Friends'},{id:'ask',label:'Ask me'},{id:'off',label:'Nobody'}],pr.spectatePrivacy||'friends',function(id){pref('spectatePrivacy',id);},false,'spectate privacy')));
    flag('showWatchers','Show who’s watching while I play','');
    if(pr.sounds)body.appendChild(settingRow('Volume','',stepper(typeof pr.volume==='number'?pr.volume:0.8,0,1,0.1,function(v){return F.number(v*100,0)+'%';},function(v){pref('volume',v);},false,'volume')));
    var taken=(view.keys&&view.keys.taken)||[];function keyLabel(k){return k+(taken.indexOf(k)>=0?' (in use)':'');}
    var keys=[];for(var i=5;i<=10;i++)keys.push({id:'F'+i,label:keyLabel('F'+i)});
    body.appendChild(settingRow('Hotkey','Ready up or open the lobby in game.',segmented(keys,pr.hotkey||'F7',function(id){pref('hotkey',id);},false,'hotkey')));
    var ks=view.keys||{};var clipOptions=['F6','F8','F9','F10','F11','Insert','Home','PageUp'].map(function(k){return {id:k,label:keyLabel(k)};});
    body.appendChild(settingRow('Clip key','Marks a moment in a recorded run.',segmented(clipOptions,ks.clip||'F8',function(id){pref('clipKey',id);},false,'clip key')));
    flag('showBoard','Standings panel in matches','');
    body.appendChild(settingRow('Scoreboard key','Hold for the full standings.',segmented((ks.scoreboardKeys||['Tab','CapsLock','Tilde']).map(function(k){return {id:k,label:keyLabel(k)};}),ks.scoreboard||'Tab',function(id){pref('scoreboardKey',id);},false,'scoreboard key')));
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
    {title:'Play KovaaK’s together',text:'Compare scores or fight each other. Results never touch KovaaK’s leaderboards.',points:[
      ['Lobbies','Invite Steam friends or share the room code. Missing content is sent for you.'],
      ['Map library','Counter-Strike maps with CS movement, from the Steam Workshop.'],
      ['Spectate','Watch a friend play, with or without a lobby.'],
      ['History and rivals','Every match, with replays to compare runs.']]},
    {title:'Your keys',text:'They work while KovaaK’s has focus.',keys:true},
    {title:'Privacy',text:'Change this later in Your multiplayer settings.',privacy:true},
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
      card.appendChild(settingRow('Clip key','Marks a moment in a recorded run.',segmented(clips,ks.clip||'F8',function(id){act('prefs',{prefs:{clipKey:id}});},false,'clip key')));
      (ks.conflicts||[]).forEach(function(c){card.appendChild(node('p','mp-warn-text',safe(c,'')));});
    }
    if(step.privacy){
      card.appendChild(settingRow('Who can spectate me','',segmented([{id:'friends',label:'Friends'},{id:'ask',label:'Ask me'},{id:'off',label:'Nobody'}],pr.spectatePrivacy||'friends',function(id){act('prefs',{prefs:{spectatePrivacy:id}});},false,'spectate privacy')));
      card.appendChild(settingRow('Hide my scenario from friends','',toggleSwitch(!!pr.hideScenario,'Hide my scenario',function(){act('prefs',{prefs:{hideScenario:!pr.hideScenario}});})));
    }
    if(step.connect){
      loadDiscord();
      card.appendChild(settingRow('AimMod Hub','Adds your Hub scores to your history and benchmarks.',actions(button('Open Account',openAccount,'compact'))));
      if(discord)card.appendChild(settingRow('Discord status','Shows your AimMod session on your profile.',toggleSwitch(!!discord.settings.discordPresenceEnabled,'Discord status',function(){setDiscord(!discord.settings.discordPresenceEnabled);})));
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
    add(box,node('span','mp-dot'),node('span','',view.transport.online?'Connected to Steam':view.simulation?'Developer simulation, not connected to Steam':'Not connected to Steam. Invites and friends are off. Room codes still work.'));
    return box;
  }
  function friendStatus(f){return f.status==='aimmod-lobby'?chip('In a lobby','mint'):f.status==='aimmod'?chip('AimMod','mint'):f.status==='kovaaks'?chip('KovaaK’s','cyan'):chip(f.status==='away'?'Away':'Online','');}
  function friendsPanel(inLobby){
    var p=node('div','panel mp-friends');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','',inLobby?'Invite friends':'Friends playing'));
    head.appendChild(text);p.appendChild(head);
    var items=view.friends.items||[];
    if(!items.length){p.appendChild(node('div','mp-empty',view.friends.source==='unavailable'?'Steam isn’t connected.':'No friends online.'));return p;}
    var list=node('div','mp-list');p.appendChild(list);
    items.slice(0,inLobby?8:12).forEach(function(f){
      var row=node('div','mp-friend');var info=node('div','mp-friend-info');
      add(info,node('strong','',safe(f.name,'Friend')),node('span','',safe(f.detail,'')));
      add(row,avatar(f.name,true,f.avatar),info,friendStatus(f));
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
    if(!items.length){p.appendChild(node('div','mp-empty','No matches yet.'));return p;}
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
  // Cosmetics: curated catalog items, shown only in AimMod matches ------------
  var cosmeticsData=null;
  var COSMETIC_SLOTS={head:'Head',neck:'Neck',spine:'Back'};
  var COSMETIC_GROUPS=[['Tints and patterns',['avatar_tint','avatar_pattern','player_model']],['Weapon finishes',['weapon_finish','weapon_pattern','weapon_model','reload_animation']],['Accessories',['accessory']]];
  // The old Cosmetics page now lives in the Look tab; anything that opened it lands there.
  function openCosmetics(){openTab('look');}
  // Live character preview: while this page is open and visible, a heartbeat asks
  // AimModCore to render the game's own preview stage (never during challenges);
  // the newest PNG is shown and dragging turns the character. The heartbeat
  // stops when the page closes or the workspace hides, and the request expires.
  var preview={yaw:0,frame:0,img:null,note:null,timer:null,drag:null,item:null,sent:0,fastUntil:0,open:false,close:false};
  function previewUrl(){return path()+'/cosmetic-preview.png?f='+preview.frame;}
  function previewShow(){if(!preview.img)return;if(preview.frame>0){preview.img.src=previewUrl();preview.img.style.display='block';if(preview.note)preview.note.style.display='none';}else{preview.img.style.display='none';if(preview.note)preview.note.style.display='block';}}
  function previewSend(){
    preview.sent=Date.now();preview.open=true;var body={open:true,yaw:Math.round(preview.yaw*10)/10};if(preview.item)body.item=preview.item;
    xhr('POST','/cosmetic-preview',body,function(ok,data){if(ok&&data&&typeof data.frame==='number'&&data.frame!==preview.frame){preview.frame=data.frame;previewShow();}});
  }
  function previewTick(){
    clearTimeout(preview.timer);preview.timer=null;
    if(!container||tab!=='look'){previewStop();return;}
    previewSend();preview.timer=setTimeout(previewTick,Date.now()<preview.fastUntil?250:1000);
  }
  function previewStop(){
    clearTimeout(preview.timer);preview.timer=null;preview.drag=null;preview.item=null;preview.img=null;preview.note=null;
    if(!preview.open)return;preview.open=false;
    var x=new root.XMLHttpRequest();x.open('POST',path()+'/cosmetic-preview',true);x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');x.send(JSON.stringify({open:false}));
  }
  function previewTurn(clientX){
    if(!preview.drag)return;var yaw=preview.drag.yaw+(clientX-preview.drag.x)*0.5;yaw=((yaw+180)%360+360)%360-180;preview.yaw=yaw;preview.fastUntil=Date.now()+2000;
    if(Date.now()-preview.sent>=100)previewSend();
  }
  if(root.addEventListener){root.addEventListener('mousemove',function(e){previewTurn(e.clientX);});root.addEventListener('mouseup',function(e){if(preview.drag){previewTurn(e.clientX);preview.drag=null;previewSend();}});}
  function loadCosmetics(){xhr('GET','/multiplayer?part=cosmetics',null,function(ok,data){if(ok&&data){cosmeticsData=data;if(tab==='look')render();}});}
  function loadLooks(){xhr('GET','/multiplayer?part=looks',null,function(ok,data){if(ok&&data&&data.models){looksData=data;if(tab==='look')render();}});}
  // Every change saves at once; the stage says so ("Saving..." then "Saved").
  function saving(){lookSave={state:'saving',at:Date.now()};}
  function saved(ok){lookSave={state:ok?'saved':'failed',at:Date.now()};if(tab==='look')render();clearTimeout(lookSave.timer);lookSave.timer=setTimeout(function(){if(lookSave.state==='saved'){lookSave={state:'',at:0};if(tab==='look')render();}},4000);}
  function cosmeticAct(action,extra){saving();render();act(action,extra,function(ok){saved(ok);if(ok)loadCosmetics();});}
  function lookAct(avatar){saving();render();act('avatar',{avatar:avatar},function(ok){saved(ok);if(ok){loadLooks();preview.fastUntil=Date.now()+3000;previewSend();}});}
  // Card swatches: the item's own colours as a finish chip. The service sends
  // sRGB hex (main colour first); older services sent one linear colour.
  var HEX=/^#[0-9a-f]{6}$/;
  function hexOf(v){return '#'+v.slice(0,3).map(function(n){var h=Math.round(Math.max(0,Math.min(1,+n||0))*255).toString(16);return h.length<2?'0'+h:h;}).join('');}
  function swatchColours(item){
    var list=(item.swatch||[]).filter(function(h){return typeof h==='string'&&HEX.test(h);});
    if(!list.length&&item.color&&item.color.length>=3)list=[hexOf(item.color)];
    return list.length?list:['#27e4a1'];
  }
  // Mixes a hex colour with white (t>0) or black (t<0), for the sheen and the rim.
  function shade(hex,t){var out='#';for(var i=1;i<7;i+=2){var v=parseInt(hex.substr(i,2),16);v=t>0?v+(255-v)*t:v*(1+t);var h=Math.round(v).toString(16);out+=h.length<2?'0'+h:h;}return out;}
  function swatch(item){
    var c=node('canvas','mp-cos-preview');c.width=160;c.height=120;var x=c.getContext&&c.getContext('2d');
    if(x){x.scale(2,2);x.fillStyle='#121a17';x.fillRect(0,0,80,60);
      var cols=swatchColours(item),main=cols[0],second=cols[1]||shade(main,-0.35),metal=cols[2]||shade(main,0.25),shine=Math.max(0,Math.min(1,+item.shine||0));
      var cx=40,cy=30,r=21,tau=Math.PI*2;
      if(item.kind.indexOf('weapon')===0||item.kind==='reload_animation'){
        // Weapon finish: a gunmetal chip with the accent as a ring and its glow at the centre.
        x.fillStyle='#262f2b';x.beginPath();x.arc(cx,cy,r,0,tau);x.fill();
        x.strokeStyle=main;x.lineWidth=5;x.beginPath();x.arc(cx,cy,r-5,0,tau);x.stroke();
        x.fillStyle=second;x.beginPath();x.arc(cx,cy,6,0,tau);x.fill();
      }else if(item.kind==='accessory'){
        // Accessory: its colour as a ring on a dark chip, like the piece itself.
        x.fillStyle='#202a26';x.beginPath();x.arc(cx,cy,r,0,tau);x.fill();
        x.strokeStyle=main;x.lineWidth=6;x.beginPath();x.arc(cx,cy,r-7,0,tau);x.stroke();
        x.strokeStyle=shade(main,-0.4);x.lineWidth=1.5;x.beginPath();x.arc(cx,cy,r-3.5,0,tau);x.stroke();
      }else{
        // Body tint: paint chip split between body paint and panel colour, with a metal rim.
        x.fillStyle=main;x.beginPath();x.arc(cx,cy,r,0,tau);x.fill();
        x.fillStyle=second;x.beginPath();x.moveTo(cx,cy);x.arc(cx,cy,r,-Math.PI/4,Math.PI*3/4);x.closePath();x.fill();
        x.strokeStyle=metal;x.lineWidth=2;x.beginPath();x.arc(cx,cy,r,0,tau);x.stroke();
        x.strokeStyle='#121a17';x.lineWidth=1.5;x.beginPath();x.moveTo(cx-r*0.72,cy+r*0.72);x.lineTo(cx+r*0.72,cy-r*0.72);x.stroke();
      }
      // Sheen: larger and brighter on metallic finishes.
      x.fillStyle=shade(item.kind==='accessory'?main:cols[0],0.35+shine*0.45);x.beginPath();x.arc(cx-r*0.45,cy-r*0.45,2+shine*3,0,tau);x.fill();
    }
    c.setAttribute('aria-hidden','true');return c;
  }
  // Play / Look tabs ----------------------------------------------------------
  var tab='play';
  function openTab(id){
    if(id===tab)return;tab=id;
    if(tab==='look'){mapsOpen=false;historyOpen=false;loadCosmetics();loadLooks();render();previewTick();}
    else{previewStop();render();}
  }
  function tabStrip(){
    var bar=node('div','mp-tabs');bar.setAttribute('role','tablist');
    [{id:'play',label:'Play'},{id:'look',label:'Look'}].forEach(function(t){
      var b=node('button','mp-tab'+(tab===t.id?' on':''),t.label);b.type='button';b.setAttribute('role','tab');b.setAttribute('aria-selected',String(tab===t.id));
      b.onclick=function(){openTab(t.id);};bar.appendChild(b);
    });
    return bar;
  }

  // Look: a character customiser. The live preview on the left (drag or the arrows to turn,
  // full body or close-up), categories and their tiles on the right. A click saves at once.
  var looksData=null,lookCat='model',lookSave={state:'',at:0};
  var LOOK_CATS=[
    {id:'model',label:'Model',text:'The body other players see you as.'},
    {id:'skin',label:'Skin',text:'Free KovaaK’s skins for this model.'},
    {id:'tint',label:'Tint',kinds:['avatar_tint','avatar_pattern','player_model'],text:'Body paint and patterns.'},
    {id:'head',label:'Head',kinds:['accessory'],role:'head',text:'Worn on the head.'},
    {id:'neck',label:'Neck',kinds:['accessory'],role:'neck',text:'Worn around the neck.'},
    {id:'back',label:'Back',kinds:['accessory'],role:'spine',text:'Worn on the back.'},
    {id:'weapon',label:'Weapon',kinds:['weapon_finish','weapon_pattern','weapon_model','reload_animation'],text:'Weapon finishes show on your gun in AimMod matches. The preview shows your character.'},
    {id:'outfit',label:'Outfits',soon:true,text:'Full outfits arrive in a later AimMod update.'}];
  function lookCatOf(id){for(var i=0;i<LOOK_CATS.length;i++)if(LOOK_CATS[i].id===id)return LOOK_CATS[i];return LOOK_CATS[0];}
  function catItems(c){var d=cosmeticsData;if(!c.kinds||!d||!d.items)return [];return d.items.filter(function(i){return c.kinds.indexOf(i.kind)>=0&&(!c.role||i.role===c.role);});}
  function currentLook(){
    var id=(view.prefs&&view.prefs.avatar)||(looksData&&looksData.selected)||'meso-mccree',models=looksData&&looksData.models||[];
    for(var i=0;i<models.length;i++)for(var k=0;k<models[i].skins.length;k++)if(models[i].skins[k].id===id)return {id:id,model:models[i],skin:models[i].skins[k]};
    return {id:id,model:models[0]||null,skin:models[0]?models[0].skins[0]:null};
  }
  // Tile art, drawn (solid colours only): a figure for models and skins, the item's swatch otherwise.
  var SKIN_TONES={McCree:'#d08f4f',Tracer:'#f08a3c',Genji:'#7fd26a',Pharah:'#5f93dc',Default:'#a7bab0'};
  function figure(model,skin,on){
    var c=node('canvas','mp-look-art');c.width=176;c.height=176;c.setAttribute('aria-hidden','true');var x=c.getContext&&c.getContext('2d');if(!x)return c;
    // Drawn on an 88-unit square, a little larger than the tile so the figure fills it.
    x.translate(-17.6,-17.6);x.scale(2.4,2.4);
    var body=on?'#eef5f1':'#a7bab0',dark=on?'#173b2f':'#22302a',accent=SKIN_TONES[skin]||SKIN_TONES.Default,robot=model==='Endo';
    x.fillStyle=body;
    if(robot){x.fillRect(34,10,20,19);x.fillStyle=accent;x.fillRect(37,16,14,5);x.fillStyle=body;x.fillRect(29,33,30,25);x.fillRect(24,34,5,22);x.fillRect(59,34,5,22);x.fillRect(32,60,10,22);x.fillRect(46,60,10,22);}
    else{x.beginPath();x.arc(44,19,9,0,Math.PI*2);x.fill();x.fillStyle=accent;x.fillRect(38,16,12,4);x.fillStyle=body;
      x.beginPath();x.moveTo(30,32);x.lineTo(58,32);x.lineTo(54,58);x.lineTo(34,58);x.closePath();x.fill();
      x.fillRect(25,33,5,21);x.fillRect(58,33,5,21);x.fillRect(34,60,8,22);x.fillRect(46,60,8,22);}
    x.fillStyle=accent;x.fillRect(robot?29:32,44,robot?30:24,4);
    x.fillStyle=dark;x.fillRect(robot?42:42,60,4,22);
    return c;
  }
  function noneArt(){var c=node('canvas','mp-look-art');c.width=176;c.height=176;c.setAttribute('aria-hidden','true');var x=c.getContext&&c.getContext('2d');if(!x)return c;x.scale(2,2);
    x.strokeStyle='#5d7268';x.lineWidth=3;x.beginPath();x.arc(44,44,17,0,Math.PI*2);x.stroke();x.beginPath();x.moveTo(32,56);x.lineTo(56,32);x.stroke();return c;}
  function lockArt(){var c=node('canvas','mp-look-art');c.width=176;c.height=176;c.setAttribute('aria-hidden','true');var x=c.getContext&&c.getContext('2d');if(!x)return c;x.scale(2,2);
    x.strokeStyle='#5d7268';x.lineWidth=3;x.beginPath();x.arc(44,38,8,Math.PI,0);x.stroke();x.fillStyle='#5d7268';x.fillRect(32,38,24,18);x.fillStyle='#16211d';x.fillRect(42,43,4,7);return c;}
  // An arrow turning left or right, for the stage buttons (the game font has no arrow glyphs).
  function turnArt(dir){var c=node('canvas','mp-look-turn');c.width=36;c.height=36;c.setAttribute('aria-hidden','true');var x=c.getContext&&c.getContext('2d');if(!x)return c;x.scale(2,2);
    x.strokeStyle='#dcebe3';x.fillStyle='#dcebe3';x.lineWidth=2;x.beginPath();
    if(dir<0){x.arc(9,10,6,-0.2,Math.PI*1.25,false);x.stroke();x.beginPath();x.moveTo(1,6);x.lineTo(5,11);x.lineTo(8,5);x.closePath();x.fill();}
    else{x.arc(9,10,6,Math.PI+0.2,-Math.PI*0.25,true);x.stroke();x.beginPath();x.moveTo(17,6);x.lineTo(13,11);x.lineTo(10,5);x.closePath();x.fill();}
    return c;}
  function tile(name,art,state,click,tag){
    var b=node('button','mp-look-tile'+(state?' '+state:''));b.type='button';b.title=name+(tag?' · '+tag:'');
    if(state==='on')b.setAttribute('aria-pressed','true');
    add(b,add(node('span','mp-look-thumb'),art),node('span','mp-look-name',name),tag?node('span','mp-look-tag',tag):null,state==='on'?node('span','mp-look-check'):null);
    if(state==='locked')b.disabled=true;else b.onclick=click;
    return add(node('div','mp-look-cell'),b);
  }
  function lookTiles(c,look){
    var grid=node('div','mp-look-tiles');var d=cosmeticsData;
    if(c.id==='model'){(looksData&&looksData.models||[]).forEach(function(m){
      var on=look.model&&look.model.id===m.id;
      grid.appendChild(tile(safe(m.label,m.id),figure(m.id,on&&look.skin?look.skin.skin:m.skins[0].skin,on),on?'on':'',function(){
        if(on)return;var keep=null;for(var i=0;i<m.skins.length;i++)if(look.skin&&m.skins[i].skin===look.skin.skin)keep=m.skins[i];lookAct((keep||m.skins[0]).id);
      }));});}
    else if(c.id==='skin'){(look.model?look.model.skins:[]).forEach(function(k){
      var on=k.id===look.id;grid.appendChild(tile(safe(k.label,k.skin),figure(look.model.id,k.skin,on),on?'on':'',function(){if(!on)lookAct(k.id);}));});}
    else if(c.soon){for(var n=0;n<3;n++)grid.appendChild(tile('Coming soon',lockArt(),'locked',null,'Soon'));}
    else{
      var list=catItems(c),wearing=list.filter(function(i){return i.equipped;});
      grid.appendChild(tile('None',noneArt(),wearing.length?'':'on',function(){wearing.forEach(function(i){cosmeticAct('cosmetic-remove',{id:i.id});});}));
      list.forEach(function(i){
        var fits=c.id==='weapon'||!i.models||!i.models.length||!look.model||i.models.indexOf(look.model.id)>=0;
        grid.appendChild(tile(safe(i.name,i.id),swatch(i),!fits?'locked':i.equipped?'on':'',function(){if(!i.equipped)cosmeticAct('cosmetic-equip',{id:i.id});},!fits?'Not on '+look.model.label:null));
      });
      if(d&&d.unavailable&&c.id==='tint')grid.appendChild(tile(d.unavailable+' more',lockArt(),'locked',null,'Update AimMod'));
    }
    return grid;
  }
  function lookStage(look){
    var stage=node('div','panel mp-look-stage');
    var head=node('div','mp-look-head');
    var title=node('div','mp-look-title');add(title,node('strong','',look.model?safe(look.model.label,'Model')+(look.skin&&look.skin.skin!=='Default'?' · '+safe(look.skin.label,''):''):'Your look'),node('span','',wornLine()));
    var status=lookSave.state==='saving'?chip('Saving…'):lookSave.state==='saved'?chip('Saved','mint'):lookSave.state==='failed'?chip('Not saved','amber'):node('span','mp-look-auto','Changes save automatically');
    add(head,title,status);stage.appendChild(head);
    var shot=node('div','mp-look-view'+(preview.close?' zoomed':''));
    var img=node('img','mp-look-img');img.setAttribute('alt','Your character');img.draggable=false;img.title='Drag to turn';
    img.onmousedown=function(e){preview.drag={x:e.clientX,yaw:preview.yaw};if(e.preventDefault)e.preventDefault();};
    var note=node('div','mp-look-wait');add(note,node('strong','','Preview paused'),node('span','','It shows in KovaaK’s menus, not during challenges, benchmarks or the editor.'));
    preview.img=img;preview.note=note;previewShow();
    add(shot,img,note);stage.appendChild(shot);
    var turn=function(by){preview.yaw=((preview.yaw+by+180)%360+360)%360-180;preview.fastUntil=Date.now()+2000;previewSend();};
    var left=button('',function(){turn(-45);},'compact quiet icon');left.appendChild(turnArt(-1));left.setAttribute('aria-label','Turn left');
    var right=button('',function(){turn(45);},'compact quiet icon');right.appendChild(turnArt(1));right.setAttribute('aria-label','Turn right');
    var bar=node('div','mp-look-bar');
    add(bar,actions(left,right),node('span','mp-look-hint','Drag to turn'),segmented([{id:'body',label:'Full body'},{id:'close',label:'Close-up'}],preview.close?'close':'body',function(id){preview.close=id==='close';render();},false,'view'));
    stage.appendChild(bar);
    return stage;
  }
  function wornLine(){var d=cosmeticsData;var n=d&&d.items?d.items.filter(function(i){return i.equipped;}).length:0;return n?n+(n===1?' item':' items')+' on':'No items on';}
  function resetLook(){
    var d=cosmeticsData;var worn=d&&d.items?d.items.filter(function(i){return i.equipped;}):[];
    var def=looksData&&looksData.default||'meso-mccree';if(currentLook().id!==def)lookAct(def);
    worn.forEach(function(i){cosmeticAct('cosmetic-remove',{id:i.id});});
    toast('Back to the default look.');
  }
  function lookPage(page){
    var look=currentLook(),c=lookCatOf(lookCat),d=cosmeticsData;
    var wrap=node('div','mp-look');page.appendChild(wrap);
    wrap.appendChild(lookStage(look));
    var side=node('div','panel mp-look-side');wrap.appendChild(side);
    var rail=node('div','mp-look-rail');rail.setAttribute('role','tablist');
    LOOK_CATS.forEach(function(k){
      var worn=k.kinds?catItems(k).filter(function(i){return i.equipped;}).length:0;
      var b=node('button','mp-look-cat'+(k.id===c.id?' on':'')+(k.soon?' soon':''));b.type='button';b.setAttribute('role','tab');b.setAttribute('aria-selected',String(k.id===c.id));
      add(b,node('span','mp-look-cat-name',k.label),k.soon?node('span','mp-look-cat-note','Soon'):worn?node('span','mp-look-dot'):null);
      b.onclick=function(){lookCat=k.id;render();};rail.appendChild(b);
    });
    var body=node('div','mp-look-body');
    add(body,node('h3','mp-look-cat-title',c.label),node('p','mp-look-cat-text',c.text));
    if(!looksData&&(c.id==='model'||c.id==='skin'))body.appendChild(node('p','subtle','Loading looks…'));
    else if(c.kinds&&!d)body.appendChild(node('p','subtle','Loading the catalog…'));
    else if(c.kinds&&(!d.available||!catItems(c).length)&&!c.soon)body.appendChild(add(node('div','mp-look-empty'),node('strong','','Nothing here yet'),node('span','',d.available?'New items arrive with AimMod updates.':'Cosmetics arrive with the next AimMod update.')));
    else body.appendChild(lookTiles(c,look));
    add(side,add(node('div','mp-look-pick'),rail,body));
    var foot=node('div','mp-look-foot');
    add(foot,add(node('div','mp-look-others'),node('span','mp-look-others-label','Show others’ cosmetics'),segmented([{id:'all',label:'All'},{id:'friends',label:'Friends'},{id:'off',label:'Off'}],d&&d.show||'all',function(id){cosmeticAct('cosmetic-view',{show:id});},!d,'show others')),
      actions(button('Reset to default',resetLook,'compact quiet')));
    side.appendChild(foot);
    side.appendChild(node('p','mp-look-policy','Cosmetics show only in AimMod matches and while spectating them.'));
  }
  // Match history and rivals --------------------------------------------------
  var historyOpen=false,history=null,openMatch=null,rivalFilter=null;
  function openHistory(){historyOpen=true;mapsOpen=false;tab='play';rivalFilter=null;openMatch=null;history=null;render();xhr('GET','/multiplayer?part=history',null,function(ok,data){if(ok&&data){history=data;if(historyOpen)render();}});}
  function resultText(r){return r.mode==='practice'?'Practice':r.won?'Won':r.place?ordinal(r.place)+' of '+r.players:r.winner?safe(r.winner)+' won':'Draw';}
  function historyPage(page){
    var head=node('div','mp-editor-top');var t=node('div','mp-editor-title');
    add(t,node('div','eyebrow','Multiplayer'),node('h2','','Match history'),node('p','subtle','Kept on this PC. Results never touch KovaaK’s leaderboards.'));
    add(head,t,actions(button('Back',function(){historyOpen=false;render();},'quiet')));page.appendChild(head);
    if(!history){page.appendChild(add(node('div','panel mp-card'),node('p','subtle','Loading your matches…')));return;}
    var row=node('div','mp-row');page.appendChild(row);
    var main=node('div','mp-col mp-main'),side=node('div','mp-col mp-side');row.appendChild(main);row.appendChild(side);
    var list=node('div','panel mp-history');main.appendChild(list);
    var lh=node('div','panel-head');var rival=rivalFilter&&(history.rivals||[]).filter(function(r){return r.key===rivalFilter;})[0];
    add(lh,node('h2','',rival?'Matches with '+safe(rival.name):'All matches'));
    if(rival)lh.appendChild(actions(button('Show all',function(){rivalFilter=null;render();},'compact quiet')));
    list.appendChild(lh);
    var shown=(history.matches||[]).filter(function(m){return !rivalFilter||(m.standings||[]).some(function(p){return p.key===rivalFilter;});});
    if(!shown.length)list.appendChild(node('div','mp-empty','No matches yet.'));
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
    add(text,node('h2','','Rivals'));head.appendChild(text);p.appendChild(head);
    var list=history.rivals||[];
    if(!list.length){p.appendChild(node('div','mp-empty','Play someone twice in a scored match and they show up here.'));return p;}
    list.forEach(function(r){
      var row=node('div','mp-friend'+(rivalFilter===r.key?' on':''));
      var info=node('div','mp-friend-info');add(info,node('strong','',safe(r.name)),node('span','',r.played+' matches · '+F.relative(r.lastAt)));
      var score=node('div','mp-h2h');add(score,node('span','won',String(r.won)),node('span','sep','-'),node('span','lost',String(r.lost)));
      add(row,avatar(r.name,true),info,score,actions(button(rivalFilter===r.key?'All':'Matches',function(){rivalFilter=rivalFilter===r.key?null:r.key;openMatch=null;render();},'compact quiet')));
      p.appendChild(row);
    });
    return p;
  }
  // Map Library: AimMod map ports here and on the Steam Workshop --------------
  var mapsOpen=false,maps=null,mapsBusy=false,mapsFilter='all';
  function openMaps(){mapsOpen=true;historyOpen=false;tab='play';picker=null;drafts.maps='';loadMaps();render();}
  function loadMaps(){if(mapsBusy)return;mapsBusy=true;xhr('GET','/multiplayer?part=maps',null,function(ok,data){mapsBusy=false;if(!ok||!data)return;var changed=JSON.stringify(data)!==JSON.stringify(maps);maps=data;if(changed&&(mapsOpen&&!focused||picker==='scenario'))render();});}
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
    add(t,node('div','eyebrow','Multiplayer'),node('h2','','Map library'),node('p','subtle','Counter-Strike and Garry’s Mod maps with matching movement, from the Steam Workshop.'));
    add(head,t,actions(button('Back',function(){mapsOpen=false;render();},'quiet')));page.appendChild(head);
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
      if(!shown)grid.appendChild(add(node('div','panel mp-empty'),node('span','',(maps.ports||[]).length?'No maps match.':'No map ports yet.')));
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
    add(p,node('div','mp-spinner'),node('h2','',view.joining.stage==='lobby'?'Joining the Steam lobby…':'Connecting to the host…'));
    p.appendChild(actions(button('Cancel',function(){act('cancel-join');})));
    return p;
  }
  function inviteModal(inv){
    var shade=node('div','mp-modal');var card=node('div','mp-modal-card');card.setAttribute('role','dialog');card.setAttribute('aria-label','Invite');shade.appendChild(card);
    shade.style.paddingTop=(scrolled()+70)+'px';
    var who=safe(inv.fromName,'A friend');
    var title=inv.kind==='launch'?'Join from Steam':inv.kind==='request'?who+' wants to join':who+' invited you';
    var line=inv.kind==='launch'?'Join the lobby from your Steam invite?':inv.kind==='request'?'Let them into your lobby?':'Join their AimMod lobby?';
    add(card,node('div','eyebrow',inv.kind==='request'?'Join request':'Steam invite'),add(node('div','mp-modal-head'),avatar(who,false,inv.avatar),add(node('div',''),node('h2','',title),node('p','subtle',line))));
    if(inv.summary){var s=inv.summary;var sum=node('div','mp-modal-summary');add(sum,chip(mode(s.mode).label,'mint'),node('span','',safe(s.scenario,'Scenario to be chosen')),node('span','mp-muted',s.players+' / '+s.maxPlayers+' players'));card.appendChild(sum);}
    // A different AimMod version can't join (the service refuses), so don't offer Accept.
    if(inv.compatible===false){card.appendChild(node('p','mp-warn-text',who+' has a different AimMod version. Both of you need the latest AimMod.'));card.appendChild(actions(button('Close',function(){act('decline-invite',{id:inv.id});},'primary')));return shade;}
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
    if(lobby.settings.mode==='cs')main.appendChild(teamsPanel(lobby));
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
  // CS teams: T and CT columns plus "Either" (fills the smaller team at the start). Click a
  // player's T or CT; the host moves anyone, members move themselves. Balance alternates by join order.
  function teamsPanel(lobby){
    var players=lobby.members.filter(function(m){return m.role==='player';}),host=lobby.isHost;
    var p=node('div','panel mp-teams');var head=node('div','panel-head');var text=node('div','head-text');
    var t=players.filter(function(m){return m.team===1;}).length,ct=players.filter(function(m){return m.team===2;}).length,either=players.length-t-ct;
    add(text,node('h2','','Teams'),node('p','',t+' T · '+ct+' CT'+(either?' · '+either+' either (fills the smaller team)':'')));head.appendChild(text);
    if(host)head.appendChild(actions(button('Balance',function(){act('balance');},'compact')));
    p.appendChild(head);
    var cols=node('div','mp-team-cols');p.appendChild(cols);
    [[1,'Terrorists','t'],[0,'Either team','either'],[2,'Counter-Terrorists','ct']].forEach(function(col){
      var c=node('div','mp-team-col '+col[2]);c.appendChild(node('div','mp-team-title',col[1]));
      players.filter(function(m){return (m.team||0)===col[0];}).forEach(function(m){
        var row=node('div','mp-team-row'+(m.id===lobby.self?' self':''));row.appendChild(node('span','mp-team-name',safe(m.name)));row.title=safe(m.name);
        if(host||m.id===lobby.self){var moves=[];[[1,'T'],[0,'Either'],[2,'CT']].forEach(function(o){if(o[0]!==(m.team||0))moves.push(button(o[1],function(){act('team',{member:m.id,team:o[0]});},'compact quiet'));});row.appendChild(actions.apply(null,moves));}
        c.appendChild(row);
      });
      if(!players.some(function(m){return (m.team||0)===col[0];}))c.appendChild(node('div','mp-team-empty',col[0]===0?'Nobody':'No players yet'));
      cols.appendChild(c);
    });
    return p;
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
    add(row,avatar(m.name,false,pic(m.id)),info);
    var state=m.role==='spectator'?null:m.id===lobby.hostId?node('span','mp-ready host','Host'):m.away?node('span','mp-ready away','Away'):m.ready?node('span','mp-ready on','Ready'):node('span','mp-ready','Not ready');
    if(state)row.appendChild(state);
    if(m.id!==lobby.self&&!m.simulated&&lobby.match&&lobby.match.phase==='live'&&m.role==='player'){var watch=actions(button(lobby.spectate&&lobby.spectate.member===m.id?'Watching':'Spectate',function(){spectate(m.id);},'compact quiet'));watch.className='actions mp-member-tools';row.appendChild(watch);}
    if(lobby.isHost&&m.id!==lobby.self){
      var tools=actions(m.role==='player'&&!m.ready&&!m.away&&!lobby.match?button('Start without them',function(){act('skip',{member:m.id});},'compact quiet'):null,m.connection==='connected'&&m.role==='player'?button('Make host',function(){act('transfer',{member:m.id});},'compact quiet'):null,button('Kick',function(){act('kick',{member:m.id},function(ok){if(ok)toast(safe(m.name)+' was removed.');});},'compact quiet danger'));
      tools.className='actions mp-member-tools';row.appendChild(tools);
    }
    return row;
  }
  function startBar(lobby){
    var bar=node('div','panel mp-start');var me=member(lobby.self)||{};var blockers=lobby.blockers||[];
    var text=node('div','mp-start-text');
    if(lobby.isHost){
      add(text,node('strong','',blockers.length?'Not ready to start':'Everyone’s ready'),blockers.length?blockerList(blockers):null);
      bar.appendChild(text);
      var start=button('Start match',function(){act('start');},'primary mp-big');if(blockers.length)start.disabled=true;
      // Only readiness missing: ping everyone, in game too (they can press the hotkey).
      var onlyReady=blockers.length>0&&blockers.every(function(b){return b.code==='ready';});
      var ask=onlyReady?button(lobby.readyCheck?'Asked to ready up':'Ask everyone to ready up',function(){act('ready-check',null,function(ok){if(ok)toast('Everyone who isn’t ready got a notice.');});},'mp-big'):null;
      if(ask&&lobby.readyCheck)ask.disabled=true;
      bar.appendChild(actions(ask,start));
    }else if(me.role==='spectator'){
      add(text,node('strong','','You’re watching'));bar.appendChild(text);
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
    add(text,node('strong','',title),v.state==='error'?node('span','',safe(v.error,'Something went wrong.')):v.state==='done'?node('span','','You can ready up now.'):null);
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
  // KovaaK's keys the match uses (lobby.binds); movement is listed only when it differs.
  function bindsText(b){return (b&&b.rows||[]).filter(function(r){return !r.move||!r.standard;}).map(function(r){return safe(r.label,'')+' '+(r.keys?safe(r.keys,''):'none');}).join(' · ');}
  function bindsNote(lobby){var b=lobby.binds;if(!b||!b.issues||!b.issues.length)return null;return node('p','mp-warn-text mp-binds-warn',b.issues.slice(0,3).map(function(t){return safe(t,'');}).join(' · '));}
  function summaryCard(lobby){
    var s=lobby.settings;var p=node('div','panel mp-summary');var head=node('div','panel-head');var text=node('div','head-text');
    add(text,node('h2','','Match settings'));
    head.appendChild(text);if(lobby.isHost)head.appendChild(actions(button('Edit',function(){editing=true;loadLibrary();render();},'compact')));
    p.appendChild(head);
    var rows=node('div','mp-kv');p.appendChild(rows);
    function kv(k,v,note){var r=node('div','mp-kv-row');add(r,node('span','mp-k',k),node('span','mp-v',v));if(note)r.appendChild(node('span','mp-kv-note',note));rows.appendChild(r);return r;}
    kv('Mode',mode(s.mode).label);
    kv('Scenario',s.scenario?safe(s.scenario.name,'Scenario'):'Not chosen');
    kv('Map',s.mapOverride?safe(s.mapOverride.name,'Map')+(s.mapOverride.source==='ported'?' (ported)':''):'Scenario map');
    if(s.mode==='cs'){
      var half=s.halfRounds||12;
      kv('Rounds','First to '+(half+1)+' of '+(half*2)+' · sides switch after '+half);
      kv('Round','1:55 · freeze 15 s · buy 20 s · bomb 40 s');
      kv('Economy','$800 start · CS2 rewards and loss bonus');
      kv('Overtime',s.overtime===false?'Off':'On · halves of 3 with $12,500');
      if(s.scenario&&s.scenario.csProblem)kv('CS map','Not a CS map: '+safe(s.scenario.csProblem,'')).children[1].className+=' mp-warn-line';
    }
    else if(combat(s.mode)){kv('Frag limit',F.number(s.fragLimit||(s.mode==='vampiric'?10:s.mode==='instagib'?25:s.mode==='team-deathmatch'?50:20),0)+' kills');if(s.mode==='vampiric')kv('Lifesteal',F.number(typeof s.lifesteal==='number'?s.lifesteal:50,0)+' %');}
    else kv(s.mode==='duel'?'First to':'Rounds',s.mode==='duel'?F.number(s.firstTo,0)+' wins':s.mode==='practice'?'As many as you like':F.number(s.rounds,0));
    if(s.mode!=='cs')kv(combat(s.mode)?'Match length':s.mode==='tracking-duel'?'Round length':'Time limit',s.timeLimit?F.duration(s.timeLimit):'Scenario ('+F.duration(s.scenario?s.scenario.timeLimit:60)+')');
    kv('Loadout',profileText(s.weapon,'weapon')+' · '+profileText(s.movement,'movement'));
    if(s.character&&s.character.preset!=='default')kv('Character',profileText(s.character,'character'));
    if(s.targetSpeed!==1||s.targetSize!==1)kv('Targets','Speed '+multiplier(s.targetSpeed)+' · size '+multiplier(s.targetSize));
    kv('Players','Up to '+s.maxPlayers+(s.spectators?' + spectators':''));
    kv('Countdown',F.number(s.countdown,0)+' s'+(s.lateJoin?' · late join on':'')+(s.autoStart?' · auto start':''));
    if(lobby.binds&&lobby.binds.rows&&lobby.binds.rows.length)kv('Keybinds',bindsText(lobby.binds)).children[1].className+=' mp-wrap';
    var bn=bindsNote(lobby);if(bn)p.appendChild(bn);
    if(lobby.generated){var g=node('div','mp-generated');add(g,node('strong','',lobby.generated.problem?'Match scenario problem':lobby.generated.saved?'Match scenario saved to your scenarios':'A custom scenario will be generated'),lobby.generated.problem?node('span','mp-warn-line',safe(lobby.generated.problem,'')):null,node('span','',safe(lobby.generated.name,'Match scenario')),node('span','mp-muted','Played in freeplay. KovaaK’s leaderboards stay untouched.'));p.appendChild(g);}
    else if(s.scenario)p.appendChild(node('div','mp-generated plain','Played as published. Each run is a normal KovaaK’s run.'));
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
    var top=node('div','mp-editor-top');var t=node('div','mp-editor-title');add(t,node('div','eyebrow','Lobby settings'),node('h2','','Set up the match'),node('p','subtle','Changes apply right away and clear everyone’s ready.'));
    add(top,t,actions(button('Done',function(){editing=false;picker=null;advancedOpen=null;render();},'primary')));
    if(picker==='scenario'){mapSelect(page,lobby);return;}
    page.appendChild(top);
    var cols=node('div','mp-row');page.appendChild(cols);var a=node('div','mp-col mp-half'),b=node('div','mp-col mp-half');cols.appendChild(a);cols.appendChild(b);
    var left=node('div','panel mp-editor');a.appendChild(left);var right=node('div','panel mp-editor');b.appendChild(right);
    // Mode (right column first, so Scenario stays above the fold at 720p)
    var m=section('Mode');
    m.appendChild(modePicker(s.mode,function(id){if(id!==s.mode)setting('mode',id);},true));right.appendChild(m);
    // Scenario
    var sc=section('Scenario');
    var current=node('button','mp-pick');current.type='button';add(current,node('strong','',s.scenario?safe(s.scenario.name,'Scenario'):'Choose a scenario'),node('span','',s.scenario?'Map '+safe(s.scenario.map,'')+' · '+F.duration(s.scenario.timeLimit)+' · Change':'From your KovaaK’s library'));
    current.onclick=function(){openMapSelect();};sc.appendChild(current);
    sc.appendChild(contentTable(lobby));
    left.appendChild(sc);
    // Players, rounds and time. The settings that define the chosen mode (frag limit,
    // lifesteal, rounds each, match or round length) are basics; the rest is More options.
    var pl=section('Players and rounds');
    var oneOnOne=s.mode==='duel'||s.mode==='tracking-duel'||s.mode==='vampiric';
    if(s.mode==='cs')pl.appendChild(settingRow('Team size','',segmented([{id:'6',label:'3v3'},{id:'8',label:'4v4'},{id:'10',label:'5v5'}],String(s.maxPlayers),function(id){setting('maxPlayers',Number(id));},false,'team size')));
    else pl.appendChild(settingRow('Max players',oneOnOne?'Always one against one in this mode.':'Including you.',stepper(s.maxPlayers,2,8,1,function(v){return F.number(v,0);},function(v){setting('maxPlayers',v);},oneOnOne,'max players')));
    if(s.mode==='duel')pl.appendChild(settingRow('First to','',stepper(s.firstTo,1,7,1,function(v){return F.number(v,0)+(v===1?' win':' wins');},function(v){setting('firstTo',v);},false,'first to')));
    else if(s.mode==='cs'){
      pl.appendChild(settingRow('Rounds per half','Sides switch after this many (CS2: 12).',stepper(s.halfRounds||12,6,15,1,function(v){return F.number(v,0);},function(v){setting('halfRounds',v);},false,'rounds per half')));
      pl.appendChild(settingRow('Overtime','A tie goes to overtime halves of 3 rounds with $12,500.',toggleSwitch(s.overtime!==false,'Overtime',function(){setting('overtime',s.overtime===false);})));
    }
    else if(combat(s.mode)){
      var fragDefault=s.mode==='vampiric'?10:s.mode==='instagib'?25:s.mode==='team-deathmatch'?50:20;
      pl.appendChild(settingRow('Frag limit','',stepper(s.fragLimit||fragDefault,1,100,1,function(v){return F.number(v,0);},function(v){setting('fragLimit',v);},false,'frag limit')));
      if(s.mode==='vampiric')pl.appendChild(settingRow('Lifesteal','Share of damage dealt that heals you.',stepper(typeof s.lifesteal==='number'?s.lifesteal:50,0,200,5,function(v){return F.number(v,0)+' %';},function(v){setting('lifesteal',v);},false,'lifesteal')));
    }
    else if(s.mode==='tracking-duel'){
      pl.appendChild(settingRow('Rounds','',stepper(s.rounds,1,9,1,function(v){return F.number(v,0);},function(v){setting('rounds',v);},false,'rounds')));
      pl.appendChild(settingRow('Require fire','Count time on target only while the fire button is held.',toggleSwitch(!!s.requireFire,'Require fire',function(){setting('requireFire',!s.requireFire);})));
    }
    else if(s.mode==='practice')pl.appendChild(settingRow('Rounds','',node('span','mp-muted','Unlimited')));
    else pl.appendChild(settingRow('Rounds',s.mode==='score-race'?'Each player’s best round counts.':'Each round gives points by placing.',stepper(s.rounds,1,s.mode==='score-race'?5:10,1,function(v){return F.number(v,0);},function(v){setting('rounds',v);},false,'rounds')));
    var limits=[{id:'default',label:'Scenario'},{id:'30',label:'30 s'},{id:'60',label:'60 s'},{id:'90',label:'90 s'},{id:'120',label:'2 min'}];
    if(combat(s.mode))pl.appendChild(settingRow('Match length','Ends here if nobody reaches the frag limit.',segmented([{id:'180',label:'3 min'},{id:'300',label:'5 min'},{id:'600',label:'10 min'}],String(s.timeLimit||300),function(id){setting('timeLimit',Number(id));},false,'match length')));
    else if(s.mode==='tracking-duel')pl.appendChild(settingRow('Round length','',segmented([{id:'10',label:'10 s'},{id:'15',label:'15 s'},{id:'20',label:'20 s'},{id:'30',label:'30 s'}],String(s.timeLimit||10),function(id){setting('timeLimit',Number(id));},false,'round length')));
    else if(s.mode==='cs')pl.appendChild(settingRow('Round','CS2 rules: 1:55 rounds, 15 s freeze, 20 s buy time, 40 s bomb, $800 start.',node('span','mp-muted','Fixed')));
    else pl.appendChild(settingRow('Time limit',overrides?'The scenario’s own is '+F.duration(s.scenario?s.scenario.timeLimit:60)+'.':locked,segmented(limits,s.timeLimit?String(s.timeLimit):'default',function(id){setting('timeLimit',id==='default'?null:Number(id));},!overrides,'time limit')));
    right.appendChild(pl);
    // Privacy
    var pv=section('Who can join');
    pv.appendChild(segmented([{id:'friends',label:'Friends'},{id:'invite',label:'Invited only'},{id:'public',label:'Anyone with the code'}],s.privacy,function(id){setting('privacy',id);},false,'privacy'));
    if(s.privacy==='public')pv.appendChild(node('p','mp-section-note','Works like Friends until AimMod Hub rooms are live.'));
    right.appendChild(pv);
    if(lobby.generated){var g=node('div','mp-generated');add(g,node('strong','','A custom scenario will be generated'),node('span','',safe(lobby.generated.name,'')),node('span','mp-muted','Played in freeplay. KovaaK’s leaderboards stay untouched.'));right.appendChild(g);}
    // More options
    var open=advancedOpen===null?advancedChanged(s):advancedOpen;
    var more=node('div','panel mp-more'+(open?' open':''));page.appendChild(more);
    var head=node('div','mp-more-head');var ht=node('div','mp-more-text');
    add(ht,node('strong','','More options'),open?null:node('span','',advancedSummary(s)));
    add(head,ht,actions(button(open?'Hide':'Show',function(){advancedOpen=!open;if(!advancedOpen&&picker&&picker!=='scenario')picker=null;render();},'compact')));
    more.appendChild(head);
    if(open){
      if(!overrides)more.appendChild(node('p','mp-lock','Score race plays the scenario as published, so scores compare with your history. Pick another mode to change these.'));
      var mc=node('div','mp-row');more.appendChild(mc);var c1=node('div','mp-col mp-half'),c2=node('div','mp-col mp-half');mc.appendChild(c1);mc.appendChild(c2);
      // Map
      var mp=section('Map');
      var mapBtn=node('button','mp-pick'+(overrides?'':' locked'));mapBtn.type='button';add(mapBtn,node('strong','',s.mapOverride?safe(s.mapOverride.name,'Map'):'Scenario map'),node('span','',s.mapOverride?(s.mapOverride.source==='ported'?'Ported map':'Custom map'):'The map the scenario was made for'));
      mapBtn.disabled=!overrides;mapBtn.onclick=function(){picker=picker==='map'?null:'map';pickerQuery='';loadLibrary();render();};mp.appendChild(mapBtn);
      if(overrides&&s.mapOverride)mp.appendChild(actions(button('Use the scenario map',function(){setting('mapOverride',null);},'compact quiet')));
      if(picker==='map'&&overrides)mp.appendChild(pickerList('map',s));
      mp.appendChild(actions(button('Browse the map library',openMaps,'compact quiet')));
      c1.appendChild(mp);
      // Loadout
      var lo=section('Loadout');
      lo.appendChild(profileRow('Weapon','weapon',s.weapon,PRESETS,overrides));
      lo.appendChild(profileRow('Movement','movement',s.movement,PRESETS.filter(function(p){return p.id!=='custom';}),overrides));
      lo.appendChild(profileRow('Character','character',s.character,[PRESETS[0],PRESETS[5]],overrides));
      c1.appendChild(lo);
      // Targets
      var tg=section('Targets');
      tg.appendChild(settingRow('Target speed','',stepper(s.targetSpeed,0.25,3,0.05,multiplier,function(v){setting('targetSpeed',v);},!overrides,'target speed')));
      tg.appendChild(settingRow('Target size','',stepper(s.targetSize,0.25,2,0.05,multiplier,function(v){setting('targetSize',v);},!overrides,'target size')));
      if(overrides&&(s.targetSpeed!==1||s.targetSize!==1))tg.appendChild(actions(button('Reset targets',function(){act('settings',{settings:{targetSpeed:1,targetSize:1}});},'compact quiet')));
      c2.appendChild(tg);
      // Lobby rules
      var rl=section('Lobby rules');
      rl.appendChild(settingRow('Countdown','',stepper(s.countdown,3,10,1,function(v){return F.number(v,0)+' s';},function(v){setting('countdown',v);},false,'countdown')));
      rl.appendChild(settingRow('Spectators','Up to 4 people can watch.',toggleSwitch(s.spectators,'Spectators',function(){setting('spectators',!s.spectators);})));
      var lateOk=s.mode==='ffa-rounds'||s.mode==='practice';
      rl.appendChild(settingRow('Late join',lateOk?'Players who join mid-match play from the next round.':'Only free-for-all and practice allow it.',toggleSwitch(s.lateJoin,'Late join',function(){setting('lateJoin',!s.lateJoin);},!lateOk)));
      rl.appendChild(settingRow('Auto start','A few seconds after everyone is ready.',toggleSwitch(!!s.autoStart,'Auto start',function(){setting('autoStart',!s.autoStart);})));
      rl.appendChild(settingRow('Scenario suggestions','Players suggest and vote; you pick.',toggleSwitch(s.voting!==false,'Scenario suggestions',function(){setting('voting',s.voting===false);})));
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
  // Map select: the host picks the match's scenario from a searchable grid of map cards.
  // Filters (source, game, fits the mode), favourites and recent first, keyboard (type to
  // search, arrows, Enter, Escape) and a details panel. The grid redraws itself in place, so
  // the search box keeps focus and every key reaches it.
  var mapSel={source:'all',game:'all',fits:true,focus:0,key:null};
  var GAME_LABELS={CSGO:'CS:GO',CS2:'CS2',CSS:'CS:S',CS16:'CS 1.6','CS1.6':'CS 1.6',CSCZ:'CS:CZ',Q3:'Q3',QL:'Quake Live',UT:'UT',TF2:'TF2',HL2:'HL2'};
  function gameLabel(g){return GAME_LABELS[g]||safe(g,'');}
  function openMapSelect(){picker='scenario';pickerQuery='';drafts.mapsel='';focused='mapsel';mapSel.focus=0;mapSel.key=null;loadLibrary();loadMaps();render();}
  function closeMapSelect(){picker=null;focused=null;render();}
  // Per-mode fit for each scenario, from the lobby view (lobby.eligibility[name] = {ok, reason,
  // players}); until the service sends it every map fits.
  function mapFit(lobby,name){var e=lobby&&lobby.eligibility,r=e&&e[name];if(!r)return {ok:true,reason:null,players:null};return {ok:r.ok!==false,reason:r.ok===false?safe(r.reason,'Doesn’t fit this mode'):null,players:r.players?safe(r.players,''):null};}
  function mapEntries(){
    var ports=(maps&&maps.ports)||[],byScenario={},seen={},out=[];
    ports.forEach(function(p){byScenario[p.scenario]=p;});
    ((library&&library.scenarios)||[]).forEach(function(x){seen[x.name]=true;out.push({name:x.name,scen:x,port:byScenario[x.name]||null,installed:true});});
    ports.forEach(function(p){if(!seen[p.scenario])out.push({name:p.scenario,scen:null,port:p,installed:!!p.installed});});
    return out;
  }
  function mapTitle(x){if(x.port&&x.port.display)return safe(x.port.display,x.name);return safe(String(x.name).replace(/^AimMod - /,''),'Scenario');}
  function mapSource(x){return x.port?(x.port.workshop?'workshop':'ports'):'mine';}
  function mapSourceLabel(x){return x.port?(x.port.workshop?'Workshop port':'AimMod port'):x.scen&&x.scen.mapSource==='custom'?'Custom map':'KovaaK’s map';}
  function mapMovement(x){return x.port&&x.port.variant?safe(x.port.variant,''):'KovaaK’s movement';}
  function mapThumb(x,big){
    var box=node('div','mp-ms-thumb'+(big?' big':''));
    if(x.port&&x.port.preview){var img=node('img','mp-ms-img');img.setAttribute('alt','');img.draggable=false;img.src=path()+'/multiplayer?part=preview&key='+encodeURIComponent(x.port.key);box.appendChild(img);return box;}
    // No picture: a tile in a colour of its own, with the map's initials.
    var tones=[['#173b2f','#27e4a1'],['#11252f','#66ccff'],['#2a2114','#f0b45a'],['#2a1820','#ff8fa3'],['#1f1c33','#a99cff'],['#1c2a14','#b4e36a']];
    var h=0,t=mapTitle(x);for(var i=0;i<t.length;i++)h=(h*31+t.charCodeAt(i))%9973;var tone=tones[h%tones.length];
    var c=node('canvas','mp-ms-gen');c.width=320;c.height=180;c.setAttribute('aria-hidden','true');var g=c.getContext&&c.getContext('2d');
    if(g){g.fillStyle=tone[0];g.fillRect(0,0,320,180);g.strokeStyle=tone[1];g.lineWidth=2;
      for(var k=-180;k<320;k+=36){g.beginPath();g.moveTo(k,180);g.lineTo(k+180,0);g.stroke();}
      g.fillStyle=tone[0];g.fillRect(96,52,128,76);g.fillStyle=tone[1];g.font='bold 40px Roboto';g.textAlign='center';g.textBaseline='middle';
      var words=t.split(/[\s_-]+/).filter(function(w){return w;}),ini=(words[0]||'?').charAt(0)+(words[1]?words[1].charAt(0):(words[0]||'').charAt(1)||'');g.fillText(ini.toUpperCase(),160,92);}
    box.appendChild(c);return box;
  }
  function mapSelect(page,lobby){
    var s=lobby.settings,picks=(view&&view.picks)||{favourites:[],recent:[]},modeName=mode(s.mode).label;
    var top=node('div','mp-ms-top');var t=node('div','mp-editor-title');
    add(t,node('div','eyebrow','Match settings'),node('h2','','Choose a map'));
    var input=trackInput(node('input','mp-ms-search'),'mapsel');input.setAttribute('autocomplete','off');input.setAttribute('aria-label','Search maps');
    add(top,t,field(input,'Search maps and scenarios','mp-ms-field'),actions(button('Close',closeMapSelect,'quiet')));
    page.appendChild(top);
    if(!library||!maps&&!library){page.appendChild(add(node('div','panel mp-card'),node('p','subtle','Loading your library…')));return;}
    if(!library.available){page.appendChild(add(node('div','panel mp-card'),node('p','subtle','AimMod couldn’t find your KovaaK’s folder.')));return;}
    var all=mapEntries(),games={};all.forEach(function(x){if(x.port&&x.port.game)games[x.port.game]=true;});
    var bar=node('div','mp-ms-filters');page.appendChild(bar);
    var body=node('div','mp-ms');page.appendChild(body);
    var gridBox=node('div','mp-ms-grid-box');var details=node('div','panel mp-ms-details');add(body,gridBox,details);
    var visible=[];
    function fav(name){return (picks.favourites||[]).indexOf(name)>=0;}
    function filters(){
      while(bar.firstChild)bar.removeChild(bar.firstChild);
      bar.appendChild(segmented([{id:'all',label:'All'},{id:'ports',label:'AimMod ports'},{id:'mine',label:'My scenarios'},{id:'workshop',label:'Workshop'}],mapSel.source,function(id){mapSel.source=id;mapSel.focus=0;filters();fill();},false,'source'));
      var gl=[{id:'all',label:'All games'}];Object.keys(games).sort().forEach(function(g){gl.push({id:g,label:gameLabel(g)});});
      if(gl.length>1)bar.appendChild(segmented(gl,mapSel.game,function(id){mapSel.game=id;mapSel.focus=0;filters();fill();},false,'game'));
      var fit=node('button','mp-ms-fit'+(mapSel.fits?' on':''));fit.type='button';fit.setAttribute('role','switch');fit.setAttribute('aria-checked',String(mapSel.fits));
      add(fit,node('span','mp-ms-box'),node('span','','Fits '+modeName));fit.onclick=function(){mapSel.fits=!mapSel.fits;mapSel.focus=0;filters();fill();};
      bar.appendChild(fit);
    }
    function matches(x,q){
      if(mapSel.source!=='all'&&!(mapSel.source==='ports'?!!x.port:mapSel.source==='workshop'?!!(x.port&&x.port.workshop):!x.port))return false;
      if(mapSel.game!=='all'&&!(x.port&&x.port.game===mapSel.game))return false;
      if(mapSel.fits&&!mapFit(lobby,x.name).ok)return false;
      if(!q)return true;
      // Searched by its own words: the AimMod port prefix would match every port for "aim".
      var hay=(String(x.name).replace(/^AimMod - /,'')+' '+mapTitle(x)+' '+(x.port?gameLabel(x.port.game)+' '+(x.port.variant||''):'')+' '+(x.scen?String(x.scen.map).replace(/^aimmod_/i,''):'')).toLowerCase();
      return q.split(/\s+/).every(function(w){return !w||hay.indexOf(w)>=0;});
    }
    function card(x,index){
      var f=mapFit(lobby,x.name),selected=s.scenario&&s.scenario.name===x.name;
      var b=node('button','mp-ms-card'+(selected?' selected':'')+(index===mapSel.focus?' focus':'')+(f.ok?'':' unfit')+(x.installed?'':' missing'));b.type='button';b.title=x.name;
      b.appendChild(mapThumb(x,false));
      var info=node('div','mp-ms-info');
      var head=node('div','mp-ms-name');add(head,node('strong','',mapTitle(x)),fav(x.name)?node('span','mp-ms-star'):null);info.appendChild(head);
      var badges=node('div','mp-ms-badges');
      if(x.port&&x.port.game)badges.appendChild(chip(gameLabel(x.port.game),'mint'));
      badges.appendChild(chip(mapMovement(x)));
      if(!x.installed)badges.appendChild(chip('Not installed','amber'));
      info.appendChild(badges);
      info.appendChild(node('div','mp-ms-why',!f.ok?f.reason:f.players?f.players:mapSourceLabel(x)));
      b.appendChild(info);
      if(selected)b.appendChild(node('span','mp-ms-selected','Selected'));
      b.onclick=function(){mapSel.focus=index;mapSel.key=x.name;fill();};
      b.ondblclick=function(){choose(x);};
      return b;
    }
    function choose(x){if(!x.installed||!mapFit(lobby,x.name).ok)return;picker=null;focused=null;setting('scenario',x.name);}
    function fill(){
      while(gridBox.firstChild)gridBox.removeChild(gridBox.firstChild);
      var q=(drafts.mapsel||'').toLowerCase().replace(/^\s+|\s+$/g,''),byName={},hidden=0;visible=[];
      all.forEach(function(x){byName[x.name]=x;});
      function section(title,list,limit){
        var shown=list.filter(function(x){return matches(x,q);});if(!shown.length)return;
        gridBox.appendChild(node('div','mp-ms-group',title));var grid=node('div','mp-ms-grid');gridBox.appendChild(grid);
        shown.slice(0,limit).forEach(function(x){var cell=node('div','mp-ms-cell');cell.appendChild(card(x,visible.length));visible.push(x);grid.appendChild(cell);});
        if(shown.length>limit)gridBox.appendChild(node('div','mp-muted','Showing '+limit+' of '+F.number(shown.length,0)+'. Type to narrow the list.'));
      }
      if(!q){
        section('Favourites',(picks.favourites||[]).map(function(n){return byName[n];}).filter(Boolean),12);
        section('Recently played',(picks.recent||[]).map(function(n){return byName[n];}).filter(function(x){return x&&!fav(x.name);}).slice(0,8),8);
      }
      section(q?'Results':'All maps',all,q?60:48);
      if(mapSel.fits)hidden=all.filter(function(x){return !mapFit(lobby,x.name).ok;}).length;
      if(!visible.length)gridBox.appendChild(add(node('div','mp-ms-empty'),node('strong','','Nothing matches'),node('span','',q?'Try a shorter search or another filter.':'No maps in this filter yet.')));
      if(hidden)gridBox.appendChild(add(node('div','mp-ms-hidden'),node('span','',hidden+(hidden===1?' map doesn’t':' maps don’t')+' fit '+modeName+'. '),actions(button('Show them',function(){mapSel.fits=false;filters();fill();},'compact quiet'))));
      if(mapSel.key){for(var i=0;i<visible.length;i++)if(visible[i].name===mapSel.key){mapSel.focus=i;break;}}
      if(mapSel.focus>=visible.length)mapSel.focus=Math.max(0,visible.length-1);
      showDetails(visible[mapSel.focus]||null);
    }
    function showDetails(x){
      while(details.firstChild)details.removeChild(details.firstChild);
      if(!x){details.appendChild(node('p','subtle','Pick a map to see its details.'));return;}
      mapSel.key=x.name;
      var f=mapFit(lobby,x.name),selected=s.scenario&&s.scenario.name===x.name;
      details.appendChild(mapThumb(x,true));
      var d=node('div','mp-ms-dbody');details.appendChild(d);
      var head=node('div','mp-ms-dhead');add(head,add(node('div','mp-ms-dtitle'),node('strong','',mapTitle(x)),node('span','',safe(x.name,''))),actions((function(){
        var on=fav(x.name),star=button('',function(){act('favourite',{scenario:x.name,on:!on},function(ok){if(ok){picks=view.picks||picks;fill();}});},'compact quiet mp-fav'+(on?' on':''));
        star.appendChild(starIcon(on));star.setAttribute('aria-label',on?'Remove from favourites':'Add to favourites');star.setAttribute('aria-pressed',String(on));return star;})()));
      d.appendChild(head);
      var tags=node('div','mp-ms-badges');if(x.port&&x.port.game)tags.appendChild(chip(gameLabel(x.port.game),'mint'));tags.appendChild(chip(mapMovement(x)));tags.appendChild(chip(mapSourceLabel(x)));if(f.players)tags.appendChild(chip(f.players));d.appendChild(tags);
      if(x.port&&x.port.description||x.scen&&x.scen.description)d.appendChild(node('p','mp-ms-desc',safe((x.port&&x.port.description)||x.scen.description,'')));
      var facts=node('div','mp-ms-facts');
      function fact(k,v){if(!v)return;add(facts,add(node('div','mp-ms-fact'),node('span','',k),node('strong','',v)));}
      fact('Map',x.scen?safe(x.scen.map,''):x.port?safe(x.port.mapFile,''):'');
      fact('Time limit',x.scen&&x.scen.timeLimit?F.duration(x.scen.timeLimit):'');
      fact('Weapon',x.scen&&x.scen.defaultWeapon?safe(x.scen.defaultWeapon,''):'');
      fact('Map size',x.port&&x.port.mapScale?'Scale '+x.port.mapScale:'');
      fact('Download',x.port&&x.port.bytes?Math.max(1,Math.round(x.port.bytes/1e6))+' MB':'');
      fact('Shift',x.port?shiftText(x.port):'');
      d.appendChild(facts);
      if(!f.ok)d.appendChild(node('p','mp-ms-warn',f.reason+'. Pick another map, or change the mode.'));
      // Who still needs it: known for the lobby's current scenario; others get it from you once picked.
      if(selected){var missing=lobby.members.filter(function(m){return m.role==='player'&&contentState(m,lobby).kind;});
        d.appendChild(node('p',missing.length?'mp-ms-warn':'mp-ms-ok',missing.length?'Missing for '+missing.map(function(m){return safe(m.name);}).join(', ')+'. They get it from you before the match.':'Everyone has it.'));}
      else if(x.installed)d.appendChild(node('p','mp-ms-note','Players who don’t have it get it from you when the match loads.'));
      var row=[];
      if(!x.installed&&x.port&&x.port.workshop&&maps&&maps.canInstall)row.push(button(x.port.download?'Downloading…':'Download',function(){act('map-install',{key:x.port.key},function(){loadMaps();});},'primary'));
      else if(!x.installed)row.push(node('span','mp-ms-note','Install it from the map library first.'));
      if(x.installed)row.push(selected?button('Selected',function(){},'quiet'):button('Use this map',function(){choose(x);},'primary'));
      if(selected&&row.length)row[row.length-1].disabled=true;
      if(x.installed&&!f.ok)row[row.length-1].disabled=true;
      d.appendChild(actions.apply(null,row));
    }
    // Keys: letters type into the search; arrows move between cards; Enter picks; Escape closes.
    input.onchanged=function(){mapSel.focus=0;mapSel.key=null;fill();};
    input.onkeydown=function(e){
      e=e||root.event;var k=e.keyCode,cols=(root.innerWidth||1920)>=1500?4:3,n=visible.length;
      var move=k===37?-1:k===39?1:k===38?-cols:k===40?cols:0;
      if(move&&n){mapSel.focus=Math.max(0,Math.min(n-1,mapSel.focus+move));mapSel.key=visible[mapSel.focus].name;fill();if(e.preventDefault)e.preventDefault();return false;}
      if(k===13&&visible[mapSel.focus]){choose(visible[mapSel.focus]);if(e.preventDefault)e.preventDefault();return false;}
      if(k===27){closeMapSelect();if(e.preventDefault)e.preventDefault();return false;}
    };
    filters();fill();
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
    // CS competitive: only maps with the AimMod CS map spec; the others are listed after them, off, with why.
    var csMode=kind==='scenario'&&view&&view.lobby&&view.lobby.settings&&view.lobby.settings.mode==='cs';
    // Why a scenario's map can't host CS (null: it can): the library's modes.cs.
    function csWhy(x){var c=x&&x.modes&&x.modes.cs;return c?(c.ok?null:safe(c.reason,'Not a CS map')):'Not a CS map';}
    function items(){var src=kind==='scenario'||kind==='suggest'?library.scenarios:kind==='map'?library.maps:kind==='weapon'?library.weapons:library.characters;
      var list=(src||[]).map(function(x){return typeof x==='string'?{name:x}:x;});
      if(csMode)list=list.filter(function(x){return !csWhy(x);}).concat(list.filter(function(x){return !!csWhy(x);}));
      return list;}
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
        var why=csMode?csWhy(x):null;
        var b=node('button','mp-pick-item'+(why?' off':''));b.type='button';
        if(why){b.disabled=true;b.title='Not a CS map: '+why;}
        var info=node('span','mp-pick-info');add(info,node('strong','',safe(x.name,'Untitled')));
        if(kind==='scenario'||kind==='suggest')info.appendChild(node('span','','Map '+safe(x.map,'')+' · '+F.duration(x.timeLimit)+(x.defaultWeapon?' · '+safe(x.defaultWeapon,''):'')));
        b.appendChild(info);
        if(csMode)b.appendChild(chip(why||'CS map',why?'':'mint'));
        else if(kind==='map'||kind==='scenario'||kind==='suggest'){var src=kind==='map'?x.source:x.mapSource;b.appendChild(chip(src==='ported'?'Ported':src==='custom'?'Custom map':'Built-in',src==='ported'?'mint':''));}
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
    lobby.members.filter(function(m){return m.role==='player';}).forEach(function(m){var c=contentState(m,lobby);var r=node('div','mp-content-row');add(r,avatar(m.name,true,pic(m.id)),node('span','mp-content-name',safe(m.name)),node('span','mp-content '+c.kind,c.text));box.appendChild(r);});
    return box;
  }

  // Match screens ------------------------------------------------------------
  function roundLabel(match){if(match.mode==='tracking-duel')return 'Round '+match.round+' of '+match.totalRounds;return match.mode==='duel'?'Round '+match.round+' · first to '+match.firstTo:match.totalRounds?'Round '+match.round+' of '+match.totalRounds:'Run '+match.round;}
  function matchScreen(page,lobby){
    if(lobby.spectate)page.appendChild(add(banner('info','Spectating '+safe(lobby.spectate.name)+' · '+statLine(lobby.spectate.score)+'. Their view plays in the pause menu.'),actions(button('Stop',function(){act('spectate-stop');},'compact quiet'))));
    var match=lobby.match;
    connectionBanners(page,lobby);
    if(match.phase==='loading')loadingStage(page,lobby,match);
    else if(match.phase==='countdown')countdown(page,lobby,match);
    else if(match.phase==='live')live(page,lobby,match);
    else roundResults(page,lobby,match);
  }
  // The round on this machine. map is the load gate's check: checking, ok, wrong or failed.
  function planBox(lobby){
    var r=lobby.round;if(!r)return null;
    var bad=r.map==='wrong'||r.map==='failed',checking=r.map==='checking'||r.state==='loading';
    var box=node('div','mp-plan '+(r.state==='error'||bad?'warn':r.state==='manual'||r.state==='blocked'||checking?'manual':'ok'));
    add(box,node('strong','',bad?'Your map didn’t load':r.state==='blocked'?'Finish your current run':r.state==='manual'?'Start it yourself':r.state==='error'?'Start it yourself':checking?'Loading your map':r.mode==='freeplay'?'Match scenario, freeplay':'Normal KovaaK’s run'),node('span','',safe(r.message,'')));
    return box;
  }
  function countdown(page,lobby,match){
    var stage=node('div','mp-stage');page.appendChild(stage);
    var ring=node('div','mp-count');var digits=node('div','mp-count-num',String(seconds(match.startsAt-now())));ring.appendChild(digits);
    countNodes.push({node:digits,at:match.startsAt,format:function(ms){return String(Math.max(1,seconds(ms)));}});
    add(stage,node('div','eyebrow',mode(match.mode).label+' · '+roundLabel(match)),ring,node('h2','',safe(match.scenario,'Scenario')),match.players.indexOf(lobby.self)>=0?null:node('p','subtle','You’re spectating this round.'));
    var plan=planBox(lobby);if(plan)stage.appendChild(plan);
    var keys=match.players.indexOf(lobby.self)>=0?bindsNote(lobby):null;if(keys)stage.appendChild(keys);
    var who=node('div','mp-stage-players');match.players.forEach(function(id){var m=member(id);add(who,add(node('div','mp-stage-player'),avatar(nameOf(id),true,pic(id)),node('span','',nameOf(id)),m&&m.connection==='reconnecting'?chip('Reconnecting','amber'):null));});
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
    if(playing){var you=node('div','panel mp-card');add(you,node('h2','','Your run'));var plan=planBox(lobby);if(plan)you.appendChild(plan);else you.appendChild(node('p','subtle','Play the round in KovaaK’s.'));
      side.appendChild(you);}
    var others=match.players.filter(function(id){var m=member(id);return id!==lobby.self&&m&&!m.simulated;});
    keepSpectateView(lobby);
    if(others.length)side.appendChild(spectatePanel(lobby,match,others));
    else if(!playing)side.appendChild(add(node('div','panel mp-card'),node('h2','','You’re spectating'),node('p','subtle','Simulated players have no camera to follow.')));
    if(match.rounds.length&&match.mode!=='practice'){var st=node('div','panel');var sh=node('div','panel-head');add(sh,node('h2','','Standings so far'));st.appendChild(sh);var sb=node('div','panel-body');sb.appendChild(standingsTable(match));st.appendChild(sb);side.appendChild(st);}
  }
  function placementTable(results,mode,showPoints){
    var t=node('div','mp-table');var head=node('div','mp-tr head');add(head,node('span','mp-td place',''),node('span','mp-td name','Player'),node('span','mp-td num','Score'),node('span','mp-td num','Accuracy'),showPoints?node('span','mp-td num',mode==='duel'?'Win':'Points'):null);t.appendChild(head);
    results.forEach(function(r){var row=node('div','mp-tr'+(r.memberId===(view.lobby&&view.lobby.self)?' self':''));
      var place=node('span','mp-td place');if(r.place)place.appendChild(node('span','mp-medal p'+Math.min(r.place,4),String(r.place)));
      var name=node('span','mp-td name');add(name,avatar(r.name,true,pic(r.memberId)),node('span','',safe(r.name)),r.status==='dnf'?chip('Did not finish','amber'):r.status==='left'?chip('Left','amber'):null,r.disputed?chip('Disputed','rose'):null);
      add(row,place,name,node('span','mp-td num',r.score===null?'—':F.number(r.score,0)),node('span','mp-td num',F.percent(r.accuracy)),showPoints?node('span','mp-td num',mode==='duel'?(r.points?'+1':''):'+'+F.number(r.points,0)):null);t.appendChild(row);});
    return t;
  }
  function standingsTable(match){
    var t=node('div','mp-table');var duel=match.mode==='duel',ffa=match.mode==='ffa-rounds';
    var head=node('div','mp-tr head');add(head,node('span','mp-td place',''),node('span','mp-td name','Player'),node('span','mp-td num',duel?'Wins':ffa?'Points':'Best'),node('span','mp-td num',duel||ffa?'Best':'Total'));t.appendChild(head);
    match.standings.forEach(function(s){var row=node('div','mp-tr'+(s.memberId===view.lobby.self?' self':''));var place=node('span','mp-td place');if(s.place)place.appendChild(node('span','mp-medal p'+Math.min(s.place,4),String(s.place)));
      var name=node('span','mp-td name');name.title=safe(s.name);add(name,avatar(s.name,true,pic(s.memberId)),node('span','',safe(s.name)));
      add(row,place,name,node('span','mp-td num strong',duel?F.number(s.wins,0):ffa?F.number(s.points,0):(s.best===null?'—':F.number(s.best,0))),node('span','mp-td num',duel||ffa?(s.best===null?'—':F.number(s.best,0)):F.number(s.total,0)));t.appendChild(row);});
    return t;
  }
  function roundResults(page,lobby,match){
    var last=match.rounds[match.rounds.length-1];if(!last)return;
    var hero=node('div','panel mp-result-hero');var winner=last.winnerId;
    add(hero,node('div','eyebrow',mode(match.mode).label+' · '+roundLabel(match)),node('h2','',match.mode==='practice'?'Run '+match.round+' done':winner?(winner===lobby.self?'You take the round':nameOf(winner)+' takes the round'):'Round drawn'));
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
    var winTeam=match.mode==='team-deathmatch'&&match.combat?match.combat.winnerTeam||(match.combat.teamFrags&&match.combat.teamFrags[0]!==match.combat.teamFrags[1]?(match.combat.teamFrags[0]>match.combat.teamFrags[1]?1:2):null):null;
    var myTeam=null;if(match.combat)match.combat.players.forEach(function(p){if(p.member===lobby.self)myTeam=p.team;});
    var title=practice?'Session complete':winTeam?(winTeam===myTeam?'Your team wins!':'Team '+winTeam+' wins'):match.winnerId===lobby.self?'You win!':match.winnerId?nameOf(match.winnerId)+' wins':'It’s a draw';
    var crownBox=node('div','mp-final-mark');if(match.winnerId)crownBox.appendChild(crown());
    var me=null;match.standings.forEach(function(s){if(s.memberId===lobby.self)me=s;});
    add(hero,crownBox,node('div','eyebrow',mode(match.mode).label+' · '+safe(match.scenario,'Scenario')),node('h2','',title),!practice&&me&&me.place?node('p','subtle','You finished '+ordinal(me.place)+' of '+match.standings.length+'.'):null);
    var votes=match.rematch.length,needed=match.players.length;
    var voted=match.rematch.indexOf(lobby.self)>=0,isPlayer=match.players.indexOf(lobby.self)>=0;
    var rematch=button(voted?'Waiting for others…':'Rematch',function(){act('rematch');},'primary mp-big');if(voted||!isPlayer)rematch.disabled=true;
    hero.appendChild(actions(rematch,lobby.isHost?button('Back to lobby',function(){act('end');}):null,button('Leave',function(){act('leave');},'quiet danger')));
    var closes=node('p','mp-note',votes?votes+' of '+needed+' want a rematch.':'Starts when every player asks. No answer in 20 s sits it out.');hero.appendChild(closes);
    if(match.rematchDeadline)countNodes.push({node:closes,at:match.rematchDeadline,format:function(ms){return votes+' of '+needed+' want a rematch · starts in '+seconds(ms)+' s with whoever confirmed.';}});
    page.appendChild(hero);
    var row=node('div','mp-row');page.appendChild(row);var main=node('div','mp-col mp-main'),side=node('div','mp-col mp-side');row.appendChild(main);row.appendChild(side);
    var st=node('div','panel');var sh=node('div','panel-head');var shText=node('div','head-text');add(shText,node('h2','','Final standings'),node('p','','KovaaK’s leaderboards are never changed.'));sh.appendChild(shText);st.appendChild(sh);var sb=node('div','panel-body');sb.appendChild(standingsTable(match));st.appendChild(sb);main.appendChild(st);
    var rounds=node('div','panel');var rh=node('div','panel-head');add(rh,node('h2','','Rounds'));rounds.appendChild(rh);var list=node('div','mp-list');
    match.rounds.forEach(function(r){var best=r.results[0];var line=node('div','mp-round-line');
      add(line,node('span','mp-round-no','R'+r.round),avatar(r.winnerId?nameOf(r.winnerId):best?best.name:'?',true,pic(r.winnerId||best&&best.memberId)),node('span','mp-round-win',r.winnerId?nameOf(r.winnerId):practice&&best?safe(best.name)+' (best run)':'Draw'),node('span','mp-round-score',best&&best.score!==null?F.number(best.score,0):'—'));list.appendChild(line);});
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
  function leave(){previewStop();generation++;clearTimeout(deferred);deferred=null;clearTimeout(timer);clearTimeout(ticker);timer=null;ticker=null;inflight=false;again=false;if(container)clear();container=null;toastNode=null;}
  root.AimModMultiplayer={enter:enter,leave:leave,resize:function(){if(container&&view)render();},_state:function(){return {view:view,editing:editing,picker:picker};}};
})(window);
