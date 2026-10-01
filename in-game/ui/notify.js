// Always-on, non-interactive multiplayer notice shown by AimModNativeUI
// (Notify.lua) outside the AimMod panel: invites, "host is starting" and
// countdowns. It only reads the service's notice; keys are handled by the service.
(function(root){
  'use strict';
  var box=root.document.getElementById('notice'),timer=null,misses=0;
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
  // The strips (duel, combat, badge) and the toast live in their own containers and are rebuilt
  // only when their own fields change: the HUD data ticks every poll, and rebuilding the toast
  // with it would swap its buttons between mouse down and mouse up, so clicks got lost.
  var strips=null,card=null,stripKey='',toastKey='';
  function clear(el){while(el.firstChild)el.removeChild(el.firstChild);}
  function parts(){
    if(strips)return;clear(box);
    strips=node('div','strips');card=node('div','toast-slot');box.appendChild(strips);box.appendChild(card);
  }
  function render(n){
    boards(n);cs(n);parts();
    var body=root.document.body,page=root.document.documentElement;
    function input(on){if(body)body.className=on?'input':'';if(page)page.className=on?'input':'';}
    if(!n||(!n.active&&!n.badge&&!n.duel&&!n.combat&&!n.cs)){box.className='';input(false);clear(strips);clear(card);stripKey=toastKey='';return;}
    // CS draws its own strip at the top, so notices move below it.
    var extra=n.cs&&root.AimModCsHud?' cs-on'+(n.cs.buyOpen?' cs-buying':''):n.duel||n.combat||n.cs?' duel-on':'';
    box.className=(n.active?'show '+(n.kind||'info'):'show')+extra;
    // The toast-sized layer takes clicks as a whole while it asks for them (see notify.css).
    input(!!n.interactive&&n.layout!=='full');
    var sk=JSON.stringify([n.duel,n.combat,root.AimModCsHud?null:n.cs,n.badge]);
    if(sk!==stripKey){
      stripKey=sk;clear(strips);
      if(n.duel)strips.appendChild(duel(n.duel));
      if(n.combat)strips.appendChild(combat(n.combat));
      if(n.cs&&!root.AimModCsHud)strips.appendChild(csHud(n.cs));
      if(n.badge){var b=node('div','badge');b.appendChild(node('span','eye'));b.appendChild(node('span','',n.badge));strips.appendChild(b);}
    }
    var tk=n.active?JSON.stringify([n.id,n.kind,n.eyebrow,n.title,n.body,n.key,n.countdown,n.actions]):'';
    if(tk===toastKey)return;
    toastKey=tk;clear(card);
    if(!n.active)return;
    card.appendChild(toast(n));
  }
  function toast(n){
    var card=node('div','toast'+(n.actions&&n.actions.length?' has-actions':''));
    // The eyebrow says what kind of notice this is (the service can name it; otherwise by kind).
    var brands={invite:'AIMMOD · INVITE',ready:'AIMMOD · LOBBY',countdown:'AIMMOD · MATCH',friend:'AIMMOD · FRIENDS'};
    var top=node('div','brand',n.eyebrow?String(n.eyebrow).toUpperCase():(/^(t(ci|m[a-z])|fr|dev)-/.test(String(n.id||''))?brand(n):brands[n.kind]||brand(n)));card.appendChild(top);
    var row=node('div','row');
    if(typeof n.countdown==='number')row.appendChild(node('div','count',String(n.countdown)));
    var text=node('div','text');text.appendChild(node('div','title',n.title||''));text.appendChild(node('div','body',n.body||''));row.appendChild(text);
    if(n.key)row.appendChild(node('div','key',n.key));
    card.appendChild(row);
    // Answerable notices (invite, ready, load failure) carry their buttons; the layer takes clicks only for these.
    if(n.actions&&n.actions.length){var row2=node('div','actions');n.actions.forEach(function(a,i){row2.appendChild(button(a.label,i===0?'primary':'',function(){answer(a.action,a.id);}));});card.appendChild(row2);}
    return card;
  }
  // The small label above the title says where the notice comes from.
  function brand(n){var id=String(n.id||'');return 'AIMMOD · '+(/^t(ci|m[a-z])-/.test(id)?'TOURNAMENT':/^fr-/.test(id)?'FRIENDS':/^dev-/.test(id)?'DEVELOPER TEST':'MULTIPLAYER');}
  function feedLine(css,text){var line=node('div','feed'+(css?' '+css:''));line.appendChild(node('span','feed-text',text));return line;}
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
    side.appendChild(node('div','duel-time',typeof d.left==='number'?d.left+' s':'Round '+d.round+'/'+d.rounds));
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
    }else hp.appendChild(node('div','hp-num down',typeof c.respawnIn==='number'?'Back in '+c.respawnIn+' s':'Down'));
    strip.appendChild(hp);
    var mid=node('div','duel-mid');
    mid.appendChild(node('div','duel-line',c.frags+' / '+c.fragLimit+' frags'+(c.protected?' · protected':'')));
    var other=typeof c.team==='number'?'Your team '+c.teamFrags+' · Other team '+c.otherFrags:(c.leader?'Best: '+c.leader+' '+c.leaderFrags:'');
    if(other)mid.appendChild(node('div','duel-sub',other));
    strip.appendChild(mid);
    strip.appendChild(node('div','duel-time',typeof c.left==='number'?Math.floor(c.left/60)+':'+(c.left%60<10?'0':'')+(c.left%60):''));
    wrap.appendChild(strip);
    // Kill feed lines sit on a dark backing so they stay readable over bright maps.
    (c.feed||[]).forEach(function(f){wrap.appendChild(feedLine(f.you,f.killer+' fragged '+(f.you==='victim'&&f.victim==='You'?'you':f.victim)+(f.head?' · headshot':'')));});
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
    if(c.buy)c.buy.forEach(function(b){wrap.appendChild(feedLine(b.affordable?'':'victim',b.key+'  '+b.label+'  $'+b.price));});
    (c.keyClashes||[]).forEach(function(t){wrap.appendChild(feedLine('victim',t));});
    return wrap;
  }
  function button(label,css,action){var b=node('button','button'+(css?' '+css:''),label);b.type='button';b.onclick=action;return b;}
  function answer(action,id){
    var x=new root.XMLHttpRequest();x.open('POST',base()+'/multiplayer',true);x.timeout=5000;
    x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');
    x.onreadystatechange=function(){if(x.readyState===4){toastKey='';poll();}};
    x.send(JSON.stringify({action:action,id:id}));
  }
  // A missed poll keeps what is on screen, so a button doesn't vanish under the cursor; eight in a row (2 s) clear it.
  function miss(){misses++;if(misses>=8)render(null);}
  function poll(){
    var x=new root.XMLHttpRequest();x.open('GET',base()+'/multiplayer-notify',true);x.timeout=2000;
    x.onreadystatechange=function(){if(x.readyState!==4)return;var n=null;if(x.status===200){try{n=JSON.parse(x.responseText);}catch(e){n=null;}}if(n){misses=0;render(n);}else miss();};
    x.ontimeout=miss;
    x.send(null);
    clearTimeout(timer);timer=setTimeout(poll,250);
  }
  poll();
  root.AimModNotify={render:render};
})(window);
