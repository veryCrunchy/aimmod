// Mode-aware match standings, drawn from the service's board model (Standings.cs).
// Used by the in-game notice layer (corner panel, hold-to-show scoreboard) and the
// OBS browser source. Gameface: DOM nodes only, no arrow functions or promises.
(function(root){
  'use strict';
  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=String(text);return el;}
  function num(v,d){if(typeof v!=='number'||!isFinite(v))return '—';var f=Math.pow(10,d||0);var r=Math.round(v*f)/f;var s=r.toFixed(d||0).split('.');s[0]=s[0].replace(/\B(?=(\d{3})+(?!\d))/g,',');return s.join('.');}
  function signed(v){return typeof v==='number'&&isFinite(v)?(v>0?'+':'')+num(v,0):'';}
  function clock(sec){if(typeof sec!=='number')return '';var m=Math.floor(sec/60),s=sec%60;return m+':'+(s<10?'0':'')+s;}
  // Shared ES5 primitives for the workspace and notice surfaces.
  function request(method,url,body,done){
    var x=new root.XMLHttpRequest(),finished=false;x.open(method,url,true);x.timeout=8000;
    if(body){x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');}
    function finish(){if(finished)return;finished=true;var data=null;try{data=JSON.parse(x.responseText);}catch(e){}done(x.status>=200&&x.status<300,data);}
    x.onreadystatechange=function(){if(x.readyState===4)finish();};x.onerror=x.ontimeout=finish;x.send(body?JSON.stringify(body):null);return x;
  }
  function sync(target,source){
    var old=Array.prototype.slice.call(target.children||[]),used=[],next=Array.prototype.slice.call(source.children||[]);
    for(var i=0;i<next.length;i++){
      var fresh=next[i],key=fresh.amKey||fresh.id||String(fresh.className).split(' ')[0],found=null;
      for(var j=0;j<old.length;j++)if(!used[j]&&(old[j].amKey||old[j].id||String(old[j].className).split(' ')[0])===key&&old[j].tagName===fresh.tagName){found=old[j];used[j]=true;break;}
      if(found){
        found.className=fresh.className;found.disabled=fresh.disabled;found.title=fresh.title;
        if(found.style&&fresh.style){if(typeof fresh.style.cssText==='string')found.style.cssText=fresh.style.cssText;else{for(var k in found.style)found.style[k]='';for(var k2 in fresh.style)found.style[k2]=fresh.style[k2];}}
        if(fresh.attributes)for(var a=0;a<fresh.attributes.length;a++)found.setAttribute(fresh.attributes[a].name,fresh.attributes[a].value);
        var handlers=['onclick','onchange','oninput','onkeydown'];for(var h=0;h<handlers.length;h++)found[handlers[h]]=fresh[handlers[h]];
        if(fresh.tagName==='CANVAS'){var ctx=found.getContext&&found.getContext('2d');if(ctx&&ctx.drawImage){ctx.clearRect(0,0,found.width,found.height);ctx.drawImage(fresh,0,0);}}
        else if(fresh.children&&fresh.children.length)sync(found,fresh);else if(found.textContent!==fresh.textContent)found.textContent=fresh.textContent;
      }else found=fresh;
      if(target.children[i]!==found)target.insertBefore(found,target.children[i]||null);
    }
    for(var n=target.children.length-1;n>=next.length;n--)target.removeChild(target.children[n]);
  }
  root.AimModUi={node:node,clock:clock,request:request,sync:sync};
  function columns(b){
    var hp=b.mode==='vampiric'||b.kind==='team'&&b.rows.some(function(r){return typeof r.health==='number';});
    if(b.kind==='duel')return [['Wins',function(r){return num(r.wins);}],['Score',function(r){return num(r.score);}]];
    if(b.kind==='tracking')return [['Wins',function(r){return num(r.wins);}],['On target',function(r){return typeof r.percent==='number'?num(r.percent,1)+'%':'—';}]];
    if(b.kind==='combat'||b.kind==='team'){var c=[['K',function(r){return num(r.frags);}],['D',function(r){return num(r.deaths);}],['K/D',function(r){return num(r.kd,2);}]];if(hp)c.push(['HP',function(r){return num(r.health);}]);return c;}
    return [['Score',function(r){return num(r.score);}],['Gap',function(r){return signed(r.gap);}]];
  }
  function header(b){
    var h=node('div','sb-head');h.appendChild(node('span','sb-title',b.mode==='ffa-rounds'?'Placement rounds':b.mode==='vampiric'?'Vampiric duel':b.title));
    var parts=[];if(b.rounds&&b.rounds>1)parts.push('Round '+b.round+' of '+b.rounds);else if(b.firstTo)parts.push('First to '+b.firstTo);
    if(b.fragLimit&&!b.firstTo)parts.push('First to '+b.fragLimit+' frags');if(b.phase==='countdown')parts.push('Starting');if(b.phase==='round')parts.push('Round over');if(b.phase==='final')parts.push('Final');
    h.appendChild(node('span','sb-sub',parts.join(' · ')));if(typeof b.left==='number')h.appendChild(node('span','sb-time',clock(b.left)));
    return h;
  }
  function row(b,r,cols,showRank){
    var tr=node('div','sb-row'+(r.self?' self':'')+(r.status==='down'?' down':'')+(r.team?' team'+r.team:''));
    tr.amKey='player:'+(r.rowKey||r.id||r.memberId||r.name);
    if(showRank)tr.appendChild(node('span','sb-rank',r.rank));
    // Bots: a BOT tag before the name (their names start with "BOT").
    var nm=node('span','sb-name');if(r.bot)nm.appendChild(node('span','sb-bot','BOT'));
    nm.appendChild(node('span','',(r.bot?String(r.name).replace(/^BOT /,''):r.name)+(r.self&&r.name!=='You'?' (you)':'')));tr.appendChild(nm);
    cols.forEach(function(c){tr.appendChild(node('span','sb-num',c[1](r)));});
    return tr;
  }
  // CS: each side as its own block (yours first), CS-style: the side's name, score and players
  // alive, then its players with money (your side only), kills, deaths and K/D; the down greyed.
  var csCols=[['$',function(r){return typeof r.money==='number'?'$'+num(r.money):'';}],['K',function(r){return num(r.frags);}],['D',function(r){return num(r.deaths);}],['K/D',function(r){return num(r.kd,2);}],['ADR',function(r){return num(r.adr,1);}],['HS%',function(r){return num(r.hsPercent,1)+'%';}],['MVP',function(r){return num(r.mvps);}]];
  function csSide(b,t){
    var side=node('div','sb-cs-side team'+t.team+(t.side==='T'?' t':' ct')+(t.self?' self':''));
    var title=node('div','sb-row sb-cs-title');
    title.appendChild(node('span','sb-cs-score',num(t.total)));
    title.appendChild(node('span','sb-name',t.name+(t.self?' · your team':'')));
    title.appendChild(node('span','sb-cs-alive',typeof t.alive==='number'?t.alive+' of '+t.players+' alive':''));
    side.appendChild(title);
    var head=node('div','sb-row sb-cols');head.appendChild(node('span','sb-name','Player'));csCols.forEach(function(c){head.appendChild(node('span','sb-num',c[0]));});head.appendChild(node('span','sb-cs-dead',''));side.appendChild(head);
    b.rows.forEach(function(r){
      if(r.team!==t.team)return;
      var tr=row(b,r,csCols,false);
      if(r.status==='down')tr.appendChild(node('span','sb-cs-dead','DEAD'));else tr.appendChild(node('span','sb-cs-dead',''));
      side.appendChild(tr);
    });
    return side;
  }
  // mode: corner (compact, in game), full (hold-to-show) or stream (OBS).
  function draw(target,b,mode){
    while(target.firstChild)target.removeChild(target.firstChild);
    if(!b||!b.rows)return;
    var box=node('div','sb sb-'+mode+' sb-'+b.kind);target.appendChild(box);
    box.appendChild(header(b));
    if(b.kind==='cs'){(b.teams||[]).forEach(function(t){box.appendChild(csSide(b,t));});return;}
    var cols=columns(b),rank=b.kind==='score'||b.kind==='combat';
    var head=node('div','sb-row sb-cols');if(rank)head.appendChild(node('span','sb-rank','#'));head.appendChild(node('span','sb-name','Player'));cols.forEach(function(c){head.appendChild(node('span','sb-num',c[0]));});
    if(mode!=='corner')box.appendChild(head);
    if(b.kind==='team'&&b.teams){
      var totals=node('div','sb-teams');b.teams.forEach(function(t){var tt=node('div','sb-team team'+t.team+(t.self?' self':''));tt.appendChild(node('span','sb-team-name',t.name));tt.appendChild(node('span','sb-team-total',num(t.total)));totals.appendChild(tt);});box.appendChild(totals);
      if(mode==='corner'){b.rows.filter(function(r){return r.self;}).forEach(function(r){box.appendChild(row(b,r,cols,false));});return;}
      b.teams.forEach(function(t){b.rows.filter(function(r){return r.team===t.team;}).forEach(function(r){box.appendChild(row(b,r,cols,false));});});
      return;
    }
    b.rows.forEach(function(r){box.appendChild(row(b,r,cols,rank));});
  }
  function render(target,b,mode){if(!target.insertBefore){draw(target,b,mode);return;}var draft=node('div','');draw(draft,b,mode);sync(target,draft);}
  root.AimModStandings={render:render};
})(window);
