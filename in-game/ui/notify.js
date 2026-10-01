// Always-on, non-interactive multiplayer notice shown by AimModNativeUI
// (Notify.lua) outside the AimMod panel: invites, "host is starting" and
// countdowns. It only reads the service's notice; keys are handled by the service.
(function(root){
  'use strict';
  var box=root.document.getElementById('notice'),last='',timer=null;
  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=text;return el;}
  function base(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  // Standings: the compact corner panel during a match, the full scoreboard while its key is held.
  var corner=root.document.getElementById('board-corner'),full=root.document.getElementById('board-full'),lastBoards='';
  function boards(n){
    var key=n?JSON.stringify([n.board,n.boardFull]):'';if(key===lastBoards||!root.AimModStandings||!corner||!full)return;lastBoards=key;
    root.AimModStandings.render(corner,n&&!n.boardFull?n.board:null,'corner');corner.className=n&&n.board&&!n.boardFull?'show':'';
    root.AimModStandings.render(full,n&&n.boardFull,'full');full.className=n&&n.boardFull?'show':'';
  }
  // CS: its own full-screen HUD layer (edges only), drawn whenever its state changes.
  var csRoot=root.document.getElementById('cs-hud'),lastCs='';
  function cs(n){var c=n&&n.cs||null;var key=c?JSON.stringify(c):'';if(key===lastCs||!root.AimModCsHud||!csRoot)return;lastCs=key;root.AimModCsHud.render(csRoot,c,answer);}
  function render(n){
    boards(n);cs(n);
    // The standings change every frame; the toast only re-renders for its own fields.
    var key=n?JSON.stringify(n,function(k,v){return k==='board'||k==='boardFull'?undefined:v;}):'';if(key===last)return;last=key;
    while(box.firstChild)box.removeChild(box.firstChild);
    if(!n||(!n.active&&!n.badge&&!n.duel&&!n.combat&&!n.cs)){box.className='';return;}
    // CS draws its own strip at the top, so notices move below it.
    var extra=n.cs&&root.AimModCsHud?' cs-on':n.duel||n.combat||n.cs?' duel-on':'';
    box.className='show'+extra;
    if(n.duel)box.appendChild(duel(n.duel));
    if(n.combat)box.appendChild(combat(n.combat));
    if(n.cs&&!root.AimModCsHud)box.appendChild(csHud(n.cs));
    if(n.badge){var b=node('div','badge');b.appendChild(node('span','eye'));b.appendChild(node('span','',n.badge));box.appendChild(b);}
    if(!n.active)return;
    box.className='show '+(n.kind||'info')+extra;
    var card=node('div','toast');
    var top=node('div','brand','AIMMOD · MULTIPLAYER');card.appendChild(top);
    var row=node('div','row');
    if(typeof n.countdown==='number')row.appendChild(node('div','count',String(n.countdown)));
    var text=node('div','text');text.appendChild(node('div','title',n.title||''));text.appendChild(node('div','body',n.body||''));row.appendChild(text);
    if(n.key)row.appendChild(node('div','key',n.key));
    card.appendChild(row);
    // An incoming invite can be answered right here (the layer takes clicks only for these).
    if(n.actions&&n.actions.length){var row2=node('div','actions');n.actions.forEach(function(a,i){row2.appendChild(button(a.label,i===0?'primary':'',function(){answer(a.action,a.id);}));});card.appendChild(row2);}
    box.appendChild(card);
  }
  // Tracking duel strip at the top edge: your bar and theirs (time on target), round wins and
  // time left. Both players track and dodge at once. Never near the crosshair.
  function pct(v){return typeof v==='number'?v.toFixed(1)+' %':'—';}
  function bar(css,share){var b=node('div','duel-bar');var f=node('div','duel-fill'+(css?' '+css:''));f.style.width=(typeof share==='number'?Math.max(0,Math.min(100,share)):0)+'%';b.appendChild(f);return b;}
  function duel(d){
    var strip=node('div','duel two');
    var mid=node('div','duel-mid');
    var you=node('div','duel-row');you.appendChild(node('div','duel-who','You'));you.appendChild(bar('',d.youShare));you.appendChild(node('div','duel-pct',pct(d.you)));mid.appendChild(you);
    var them=node('div','duel-row');them.appendChild(node('div','duel-who',d.opponent));them.appendChild(bar('them',d.themShare));them.appendChild(node('div','duel-pct',pct(d.them)));mid.appendChild(them);
    strip.appendChild(mid);
    var side=node('div','duel-side');
    side.appendChild(node('div','duel-time',typeof d.left==='number'?d.left+' s':'R'+d.round+'/'+d.rounds));
    side.appendChild(node('div','duel-sub',d.wins+' – '+d.theirWins+(d.requireFire?' · fire':'')+(d.disputed?' · disputed':'')));
    strip.appendChild(side);
    return strip;
  }
  // Combat strip on the top edge: health (or the respawn wait), frags against the limit with
  // the leader or the team score, time left, and up to three recent kills underneath.
  function combat(c){
    var wrap=node('div','combat');
    var strip=node('div','duel combat-strip'+(c.alive?'':' down'));
    var hp=node('div','hp');
    if(c.alive){
      hp.appendChild(node('div','hp-num',String(Math.max(0,Math.round(c.health)))));
      var bar=node('div','hp-bar');var fill=node('div','hp-fill'+(c.health<30?' low':''));fill.style.width=Math.max(0,Math.min(100,c.health*100/(c.max||100)))+'%';bar.appendChild(fill);hp.appendChild(bar);
    }else hp.appendChild(node('div','hp-num down',typeof c.respawnIn==='number'?'Back in '+c.respawnIn:'Down'));
    strip.appendChild(hp);
    var mid=node('div','duel-mid');
    mid.appendChild(node('div','duel-line',c.frags+' / '+c.fragLimit+' frags'+(c.protected?' · protected':'')));
    var other=typeof c.team==='number'?'Team '+c.team+' '+c.teamFrags+' · '+c.otherFrags+' them':(c.leader?'Best: '+c.leader+' '+c.leaderFrags:'');
    if(other)mid.appendChild(node('div','duel-sub',other));
    strip.appendChild(mid);
    strip.appendChild(node('div','duel-time',typeof c.left==='number'?Math.floor(c.left/60)+':'+(c.left%60<10?'0':'')+(c.left%60):''));
    wrap.appendChild(strip);
    (c.feed||[]).forEach(function(f){var line=node('div','feed'+(f.you?' '+f.you:''),f.killer+' fragged '+f.victim+(f.head?' · headshot':''));wrap.appendChild(line);});
    return wrap;
  }
  // CS strip: health and armour (or down), money, score, round clock and phase, bomb state
  // with plant/defuse progress, and the buy menu (digits buy) while it's open.
  function csHud(c){
    var wrap=node('div','combat');
    var strip=node('div','duel combat-strip'+(c.alive?'':' down'));
    var hp=node('div','hp');
    if(c.alive){hp.appendChild(node('div','hp-num',Math.round(c.health)+(c.armor>0?' · '+Math.round(c.armor)+(c.helmet?'H':'A'):'')));var bar=node('div','hp-bar');var fill=node('div','hp-fill'+(c.health<30?' low':''));fill.style.width=Math.max(0,Math.min(100,c.health))+'%';bar.appendChild(fill);hp.appendChild(bar);}
    else hp.appendChild(node('div','hp-num down','Down'));
    strip.appendChild(hp);
    var mid=node('div','duel-mid');
    var phase=c.phase==='freeze'?'FREEZE':c.phase==='planted'?'BOMB '+(c.site||''):c.phase==='end'?(c.lastRound||'Round over'):'LIVE';
    mid.appendChild(node('div','duel-line',c.side+' '+c.score[0]+' – '+c.score[1]+' · Round '+c.round+' · '+phase));
    var sub='$'+c.money+(c.kit?' · kit':'');
    if(typeof c.plantProgress==='number')sub+=' · planting '+Math.round(c.plantProgress*100)+'%';
    if(typeof c.defuseProgress==='number')sub+=' · defusing '+Math.round(c.defuseProgress*100)+'%';
    if(c.phase==='freeze'||c.buyOpen)sub+=' · '+c.buyKey+' buy';
    if(c.side==='T'&&c.phase==='live'||c.phase==='planted')sub+=' · hold '+c.useKey;
    mid.appendChild(node('div','duel-sub',sub));
    strip.appendChild(mid);
    strip.appendChild(node('div','duel-time',typeof c.bombIn==='number'?c.bombIn+' s':typeof c.left==='number'?Math.floor(c.left/60)+':'+(c.left%60<10?'0':'')+(c.left%60):''));
    wrap.appendChild(strip);
    if(c.buy)c.buy.forEach(function(b){wrap.appendChild(node('div','feed'+(b.affordable?'':' victim'),b.key+'  '+b.label+'  $'+b.price));});
    (c.keyClashes||[]).forEach(function(t){wrap.appendChild(node('div','feed victim',t));});
    return wrap;
  }
  function button(label,css,action){var b=node('button','button'+(css?' '+css:''),label);b.type='button';b.onclick=action;return b;}
  function answer(action,id){
    var x=new root.XMLHttpRequest();x.open('POST',base()+'/multiplayer',true);x.timeout=5000;
    x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');
    x.onreadystatechange=function(){if(x.readyState===4){last='';poll();}};
    x.send(JSON.stringify({action:action,id:id}));
  }
  function poll(){
    var x=new root.XMLHttpRequest();x.open('GET',base()+'/multiplayer-notify',true);x.timeout=2000;
    x.onreadystatechange=function(){if(x.readyState!==4)return;if(x.status===200){try{render(JSON.parse(x.responseText));}catch(e){render(null);}}else render(null);};
    x.onerror=x.ontimeout=function(){render(null);};
    x.send(null);
    clearTimeout(timer);timer=setTimeout(poll,250);
  }
  poll();
  root.AimModNotify={render:render};
})(window);
