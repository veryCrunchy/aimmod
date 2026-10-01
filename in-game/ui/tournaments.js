// Tournaments page: your matches and check-ins from AimMod Hub, the match
// you're playing (ready, picks and bans, games with their seeds, confirm or
// dispute), the host overview of both players, and a bracket drawing.
// Everything renders from the service's /tournaments view; actions POST back.
// Gameface: XHR only, no promises or arrow functions, DOM nodes only, field()
// hints instead of placeholders, stand-alone buttons sit in .actions rows,
// canvas text in Roboto with solid colours.
(function(root){
  'use strict';
  var F=root.AimModFormat;
  var container=null,timer=null,generation=0,view=null,lastKey='',inflight=false,busy=false,toastNode=null,toastTimer=null,reason='',bracketOpen=true;
  var STATE={pending:'Waiting for an earlier match',ready:'Ready to play',veto:'Picks and bans',live:'Live',awaiting_confirmation:'Awaiting confirmation',disputed:'Disputed',complete:'Complete',skipped:'Not needed'};
  var STATUS={draft:'Draft',registration:'Registration open',check_in:'Check-in',in_progress:'In progress',completed:'Completed',cancelled:'Cancelled'};
  var FORMAT={single_elimination:'Single elimination',double_elimination:'Double elimination',round_robin:'Round robin',swiss:'Swiss'};

  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=text;return el;}
  function add(parent){for(var i=1;i<arguments.length;i++)if(arguments[i])parent.appendChild(arguments[i]);return parent;}
  function button(label,action,css){var b=node('button','button'+(css?' '+css:''),label);b.type='button';b.onclick=action;if(busy)b.disabled=true;return b;}
  function actions(){var row=node('div','actions');for(var i=0;i<arguments.length;i++)if(arguments[i])row.appendChild(arguments[i]);return row;}
  function safe(text,fallback){return F.safeText(typeof text==='string'?text:'',fallback||'Player');}
  function chip(text,kind){return node('span','tn-chip'+(kind?' '+kind:''),text);}
  function card(title,eyebrow){var c=node('div','panel tn-card');if(eyebrow)c.appendChild(node('div','eyebrow',eyebrow));if(title)c.appendChild(node('h2','',title));return c;}

  function path(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  function xhr(method,body,done){
    var ticket=generation,x=new root.XMLHttpRequest(),finished=false;
    x.open(method,path()+'/tournaments',true);x.timeout=15000;
    if(method==='POST'){x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');}
    function finish(ok,data){if(finished)return;finished=true;if(ticket!==generation||!container)return;done(ok,data);}
    x.onreadystatechange=function(){if(x.readyState!==4)return;var data=null;try{data=JSON.parse(x.responseText);}catch(e){data=null;}finish(x.status>=200&&x.status<300&&data!==null,data);};
    x.onerror=x.ontimeout=function(){finish(false,null);};
    x.send(body?JSON.stringify(body):null);
  }
  function poll(){
    if(!container||inflight)return;inflight=true;
    xhr('GET',null,function(ok,data){inflight=false;if(ok&&data&&data.v===1)accept(data);else if(!view)renderError();schedule();});
  }
  function fast(){var a=active();return !!(a&&(a.match.state==='live'||a.match.state==='veto'||a.match.state==='ready'));}
  function schedule(){clearTimeout(timer);if(container)timer=setTimeout(poll,fast()?1000:3000);}
  function act(body,done){
    busy=true;render();
    xhr('POST',body,function(ok,data){busy=false;if(ok&&data&&data.v===1){accept(data,true);if(done)done(true);}else{render();toast(data&&data.error?data.error:'That didn’t work. Try again.');if(done)done(false);}});
  }
  function accept(data,force){view=data;var key=JSON.stringify(data)+'|'+bracketOpen;if(force||key!==lastKey){lastKey=key;render();}}
  function toast(text){if(!toastNode)return;toastNode.textContent=text;toastNode.style.display='block';clearTimeout(toastTimer);toastTimer=setTimeout(function(){if(toastNode)toastNode.style.display='none';},3500);}
  function active(){if(!view)return null;for(var i=0;i<view.matches.length;i++)if(view.matches[i].active)return view.matches[i];return null;}
  function nameIn(m,id){if(!id)return 'TBD';if(m.self&&m.self.id===id)return safe(m.self.name,'You');if(m.opponent&&m.opponent.id===id)return safe(m.opponent.name);return 'Player';}

  function renderError(){
    if(!container)return;container.textContent='';
    var c=card('Tournaments aren’t available','Tournaments');c.appendChild(node('p','subtle','The AimMod service didn’t answer. It retries in a moment.'));container.appendChild(c);
  }

  // ---- your match -----------------------------------------------------------
  function matchCard(a){
    var m=a.match,me=a.self.id,mine=m.a===me;
    var c=card(m.label+': you vs '+safe(a.opponent.name),safe(a.tournamentName,'Tournament'));
    var head=node('div','tn-score');
    add(head,node('span','tn-big',String(mine?m.winsA:m.winsB)),node('span','tn-dash','–'),node('span','tn-big',String(mine?m.winsB:m.winsA)),
      add(node('div','tn-meta'),chip(STATE[m.state]||m.state,m.state==='live'?'mint':m.state==='disputed'?'warn':''),node('span','subtle','Best of '+m.bestOf+(a.host?' · you host the lobby':' · '+safe(a.opponent.name)+' hosts'))));
    c.appendChild(head);
    if(m.state==='ready'){
      var ready=m.ready.indexOf(me)>=0;
      c.appendChild(node('p','subtle',ready?'Waiting for '+safe(a.opponent.name)+'. The lobby is created as soon as you’re both ready.':'Ready up when you can play. '+(a.host?'Your AimMod creates the lobby and invites your opponent.':'You join your opponent’s lobby automatically.')));
      if(!view.canHost&&a.host)c.appendChild(node('p','tn-warn','AimMod’s Steam connection isn’t ready on this PC, so your opponent may host instead.'));
      c.appendChild(actions(ready?button('Not ready',function(){act({action:'unready',tournament:a.tournament,match:m.id});}):button('I’m ready',function(){act({action:'ready',tournament:a.tournament,match:m.id});},'primary')));
    }
    if(m.state==='veto'||m.veto.length)c.appendChild(vetoBlock(a));
    if(m.games.length)c.appendChild(gamesBlock(a));
    if(m.state==='live'&&m.currentGame>=0){var g=m.games[m.currentGame];c.appendChild(node('p','subtle','Game '+(g.index+1)+' on '+safe(g.scenario,'the scenario')+'. You and your opponent get the same targets: the game’s seed is shared. It runs in freeplay, so nothing touches KovaaK’s ranked leaderboards.'));}
    if(view.lobby&&view.lobby.matchId===m.id)c.appendChild(overview(view.lobby));
    if(m.state==='awaiting_confirmation'){
      if(m.reportedBy===me)c.appendChild(node('p','subtle',safe(a.opponent.name)+' confirms the result, or it’s accepted when the confirmation window ends.'));
      else{c.appendChild(node('p','',(mine?m.winsA>m.winsB:m.winsB>m.winsA)?'You won. Confirm the result to move on.':'Confirm the result, or dispute it if something went wrong.'));c.appendChild(actions(button('Confirm result',function(){act({action:'confirm',tournament:a.tournament,match:m.id});},'primary')));}
    }
    if(m.state==='live'||m.state==='awaiting_confirmation')c.appendChild(disputeBlock(a));
    if(m.state==='disputed')c.appendChild(node('p','tn-warn','The match is disputed. An organiser is looking at the replays.'));
    for(var i=0;i<m.flags.length;i++)if(m.flags[i]==='needs-review')c.appendChild(node('p','tn-warn','An organiser reviews this result before it counts.'));
    return c;
  }
  function vetoBlock(a){
    var m=a.match,box=node('div','tn-block');box.appendChild(node('h3','','Picks and bans'));
    var list=node('ol','tn-veto');
    for(var i=0;i<m.veto.length;i++){var v=m.veto[i];list.appendChild(node('li','',(v.action==='decider'?'Decider: ':nameIn(a,v.entrant)+(v.action==='ban'?' bans ':' picks '))+safe(v.scenario,'scenario')));}
    box.appendChild(list);
    if(m.state==='veto'){
      if(m.vetoTurn===a.self.id){
        box.appendChild(node('p','','Your turn: '+(m.vetoAction==='ban'?'ban':'pick')+' a scenario.'));
        var used={};for(var j=0;j<m.veto.length;j++)used[m.veto[j].scenario.toLowerCase()]=true;
        var row=node('div','actions');var pool=a.ruleset.pool||[];
        for(var k=0;k<pool.length;k++)if(!used[pool[k].toLowerCase()])(function(s){row.appendChild(button((m.vetoAction==='ban'?'Ban ':'Pick ')+safe(s,'scenario'),function(){act({action:'veto',tournament:a.tournament,match:m.id,scenario:s});},m.vetoAction==='ban'?'':'primary'));})(pool[k]);
        box.appendChild(row);
      } else box.appendChild(node('p','subtle','Waiting for '+safe(a.opponent.name)+' to '+(m.vetoAction==='ban'?'ban':'pick')+'.'));
    }
    return box;
  }
  function gamesBlock(a){
    var m=a.match,mine=m.a===a.self.id,box=node('div','tn-block');box.appendChild(node('h3','','Games'));
    for(var i=0;i<m.games.length;i++){
      var g=m.games[i],row=node('div','tn-game'+(m.currentGame===i?' current':''));
      var you=mine?g.scoreA:g.scoreB,them=mine?g.scoreB:g.scoreA,won=g.hasScore&&g.winner>=0&&((g.winner===0)===mine);
      add(row,node('span','tn-game-n',String(i+1)),add(node('span','tn-game-name'),node('span','',safe(g.scenario,'Scenario')),g.seed?node('span','subtle',' · seed '+g.seed):null),
        node('span','tn-game-score'+(won?' won':''),g.hasScore?F.number(you,0)+' – '+F.number(them,0):m.currentGame===i?'Playing':''));
      box.appendChild(row);
    }
    return box;
  }
  function disputeBlock(a){
    var box=node('div','tn-block');var input=node('input','tn-input');input.type='text';input.value=reason;input.maxLength=500;
    input.oninput=function(){reason=input.value;};
    add(box,node('h3','','Something wrong?'),F.field(input,'What went wrong'),actions(button('Dispute',function(){if(reason.replace(/\s+/g,'').length<5){toast('Say what went wrong first.');return;}act({action:'dispute',tournament:a.tournament,match:a.match.id,reason:reason},function(ok){if(ok)reason='';});})));
    box.appendChild(node('p','subtle','The match stops and an organiser decides with both replays.'));
    return box;
  }
  // Host overview: every player of the lobby at once.
  function overview(l){
    var box=node('div','tn-block');box.appendChild(node('h3','',(l.isHost?'Your lobby':'The lobby')+' · game '+(l.game+1)+' · '+l.phase+(l.spectators?' · '+l.spectators+' watching':'')));
    var row=node('div','tn-players');
    for(var i=0;i<l.players.length;i++){
      var p=l.players[i],cell=node('div','tn-player'+(p.self?' self':''));
      add(cell,node('div','tn-player-name',safe(p.name)+(p.self?' (you)':'')),node('div','tn-player-score',p.score===null||p.score===undefined?'–':F.number(p.score,0)),
        node('div','subtle',(p.accuracy!==null&&p.accuracy!==undefined?F.percent(p.accuracy)+' · ':'')+(p.remaining!==null&&p.remaining!==undefined?Math.ceil(p.remaining)+' s left':p.status)),
        node('div',p.connection==='connected'?'tn-ok':'tn-warn',p.connection+(p.ping?' · '+p.ping+' ms':'')));
      row.appendChild(cell);
    }
    box.appendChild(row);
    return box;
  }

  // ---- bracket ---------------------------------------------------------------
  // Compact drawing: one column per round, each match centred between the
  // two it follows; the viewer's matches are outlined in mint.
  function bracket(t){
    var self=t.selfEntrant,names={};for(var i=0;i<t.entrants.length;i++)names[t.entrants[i].id]=t.entrants[i];
    var sides={},order=[];for(var j=0;j<t.matches.length;j++){var m=t.matches[j],k=m.side||'winners';if(!sides[k]){sides[k]={};order.push(k);}(sides[k][m.round]=sides[k][m.round]||[]).push(m);}
    var CW=170,CH=38,GAPX=26,GAPY=10,TOP=22,width=0,height=0,layout=[];
    var y0=0;
    for(var s=0;s<order.length;s++){
      var rounds=Object.keys(sides[order[s]]).map(Number).sort(function(a,b){return a-b;});
      var tallest=0;for(var r=0;r<rounds.length;r++)tallest=Math.max(tallest,sides[order[s]][rounds[r]].length);
      var h=tallest*CH+(tallest-1)*GAPY;
      for(var r2=0;r2<rounds.length;r2++){
        var list=sides[order[s]][rounds[r2]].slice().sort(function(a,b){return a.position-b.position;});
        for(var q=0;q<list.length;q++)layout.push({m:list[q],x:r2*(CW+GAPX),y:y0+TOP+h*(q+0.5)/list.length-CH/2});
        layout.push({title:list[0].label,x:r2*(CW+GAPX),y:y0+14});
        width=Math.max(width,(r2+1)*(CW+GAPX));
      }
      y0+=TOP+h+28;height=y0;
    }
    var c=node('canvas','tn-bracket');c.width=Math.max(320,width);c.height=Math.max(120,height);
    var x=c.getContext&&c.getContext('2d');if(!x)return c;
    x.fillStyle='#0f1513';x.fillRect(0,0,c.width,c.height);
    for(var n=0;n<layout.length;n++){
      var it=layout[n];
      if(it.title!==undefined){x.fillStyle='#8fa69b';x.font='600 11px Roboto';x.fillText(it.title.toUpperCase(),it.x,it.y);continue;}
      var mm=it.m,mineMatch=mm.a===self||mm.b===self;
      x.fillStyle='#17201c';x.fillRect(it.x,it.y,CW,CH);
      x.strokeStyle=mm.state==='live'||mm.state==='veto'?'#27e4a1':mineMatch?'#3fb7d9':'#2a3631';x.lineWidth=mineMatch||mm.state==='live'?2:1;x.strokeRect(it.x+0.5,it.y+0.5,CW-1,CH-1);
      var slots=[[mm.a,mm.winsA],[mm.b,mm.winsB]];
      for(var z=0;z<2;z++){
        var id=slots[z][0],won=mm.winner&&mm.winner===id,e=names[id];
        x.fillStyle=won?'#eef5f1':id?'#b9c8c1':'#5d6d66';x.font=(won?'bold ':'')+'12px Roboto';
        var label=id?(e?(e.seed?e.seed+'  ':'')+safe(e.name):'Player'):(mm.state==='pending'?'TBD':'Bye');
        if(label.length>20)label=label.slice(0,19)+'…';
        x.fillText(label,it.x+8,it.y+15+z*17);
        if(id&&(mm.state==='complete'||mm.winsA||mm.winsB)){x.fillStyle=won?'#27e4a1':'#8fa69b';x.fillText(String(slots[z][1]),it.x+CW-16,it.y+15+z*17);}
      }
    }
    c.setAttribute('aria-label','Bracket of '+safe(t.name,'the tournament'));
    return c;
  }
  function tournamentCard(t){
    var c=card(safe(t.name,'Tournament'),(FORMAT[t.format]||'Tournament')+' · '+(STATUS[t.status]||t.status));
    if(t.champion){var champ=null;for(var i=0;i<t.entrants.length;i++)if(t.entrants[i].id===t.champion)champ=t.entrants[i];c.appendChild(node('p','tn-ok','Champion: '+safe(champ?champ.name:'Player')));}
    if(t.canCheckIn)c.appendChild(actions(button('Check in',function(){act({action:'check-in',tournament:t.id});},'primary')));
    var wrap=node('div','tn-bracket-wrap');wrap.appendChild(bracket(t));c.appendChild(wrap);
    c.appendChild(actions(button('Close',function(){act({action:'close'});},'quiet')));
    return c;
  }

  function render(){
    if(!container||!view)return;
    container.textContent='';
    var top=node('div','tn-top');
    add(top,node('p','subtle','Tournament matches from AimMod Hub. Results are verified with replays and never count toward KovaaK’s ranked leaderboards.'),
      actions(button('Refresh',function(){act({action:'refresh'});},'compact quiet'),view.developer&&!view.simulated?button('Simulate a tournament',function(){act({action:'simulate'});},'compact quiet'):null,
        view.simulated?button('Advance the bracket',function(){act({action:'sim-advance'});},'compact quiet'):null,view.simulated?button('Opponent reports',function(){act({action:'sim-opponent-reports'});},'compact quiet'):null,
        view.simulated?button('End simulation',function(){act({action:'sim-stop'});},'compact quiet'):null));
    container.appendChild(top);
    if(view.simulated)container.appendChild(node('p','tn-warn','Developer simulation: a pretend Hub with simulated opponents. Nothing is sent to AimMod Hub.'));
    if(view.status)container.appendChild(node('p','tn-warn',view.status));
    if(!view.linked){var l=card('Link your AimMod Hub account','Tournaments');l.appendChild(node('p','subtle','Tournaments are run on AimMod Hub. Link this device on the Account page, then enter a tournament at aimmod.app/tournaments.'));l.appendChild(actions(button('Open Account',function(){if(root.AimModWorkspace)root.AimModWorkspace.open('account');})));container.appendChild(l);}
    for(var i=0;i<view.checkIn.length;i++)(function(ci){var c=card('Check in for '+safe(ci.name,'your tournament'),'Check-in');c.appendChild(node('p','subtle','Check in to keep your place in the bracket.'));c.appendChild(actions(button('Check in',function(){act({action:'check-in',tournament:ci.tournament});},'primary')));container.appendChild(c);})(view.checkIn[i]);
    var a=active();if(a)container.appendChild(matchCard(a));
    var others=view.matches.filter(function(m){return !m.active;});
    if(others.length){var o=card('Also open','Your matches');for(var j=0;j<others.length;j++)o.appendChild(node('p','',others[j].match.label+' vs '+safe(others[j].opponent.name)+' · '+safe(others[j].tournamentName,'Tournament')+' · '+(STATE[others[j].match.state]||'')));container.appendChild(o);}
    if(!a&&view.linked&&!view.checkIn.length){var e=card('No matches right now','Your matches');e.appendChild(node('p','subtle','When a match of yours is ready, AimMod tells you, even outside this panel.'));container.appendChild(e);}
    if(view.open)container.appendChild(tournamentCard(view.open));
    if(view.mine&&view.mine.length){
      var list=card('Your tournaments','');
      for(var k=0;k<view.mine.length;k++)(function(t){var row=node('div','tn-row');add(row,add(node('div',''),node('div','tn-row-name',safe(t.name,'Tournament')),node('div','subtle',(FORMAT[t.format]||'')+' · '+t.entrants+'/'+t.maxEntrants+' players · '+(STATUS[t.status]||''))),actions(button('Bracket',function(){act({action:'open',tournament:t.id});},'compact')));list.appendChild(row);})(view.mine[k]);
      container.appendChild(list);
    }
    toastNode=node('div','tn-toast');toastNode.setAttribute('role','status');toastNode.style.display='none';container.appendChild(toastNode);
  }

  root.AimModTournaments={
    enter:function(el){container=el;generation++;lastKey='';poll();},
    leave:function(){generation++;container=null;clearTimeout(timer);timer=null;inflight=false;},
    _bracket:bracket,_state:function(){return view;}
  };
})(window);
