// Mode-aware match standings, drawn from the service's board model (Standings.cs).
// Used by the in-game notice layer (corner panel, hold-to-show scoreboard) and the
// OBS browser source. Gameface: DOM nodes only, no arrow functions or promises.
(function(root){
  'use strict';
  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=String(text);return el;}
  function num(v,d){if(typeof v!=='number'||!isFinite(v))return '—';var f=Math.pow(10,d||0);var r=Math.round(v*f)/f;var s=r.toFixed(d||0).split('.');s[0]=s[0].replace(/\B(?=(\d{3})+(?!\d))/g,',');return s.join('.');}
  function signed(v){return typeof v==='number'&&isFinite(v)?(v>0?'+':'')+num(v,0):'';}
  function clock(sec){if(typeof sec!=='number')return '';var m=Math.floor(sec/60),s=sec%60;return m+':'+(s<10?'0':'')+s;}
  function columns(b){
    var hp=b.mode==='vampiric'||b.kind==='team'&&b.rows.some(function(r){return typeof r.health==='number';});
    if(b.kind==='duel')return [['Wins',function(r){return num(r.wins);}],['Score',function(r){return num(r.score);}]];
    if(b.kind==='tracking')return [['Wins',function(r){return num(r.wins);}],['On target',function(r){return typeof r.percent==='number'?num(r.percent,1)+'%':'—';}]];
    if(b.kind==='combat'||b.kind==='team'){var c=[['K',function(r){return num(r.frags);}],['D',function(r){return num(r.deaths);}],['K/D',function(r){return num(r.kd,2);}]];if(hp)c.push(['HP',function(r){return num(r.health);}]);return c;}
    return [['Score',function(r){return num(r.score);}],['Gap',function(r){return signed(r.gap);}]];
  }
  function header(b){
    var h=node('div','sb-head');h.appendChild(node('span','sb-title',b.title));
    var parts=[];if(b.rounds&&b.rounds>1)parts.push('Round '+b.round+' of '+b.rounds);else if(b.firstTo)parts.push('First to '+b.firstTo);
    if(b.fragLimit&&!b.firstTo)parts.push('First to '+b.fragLimit+' frags');if(b.phase==='countdown')parts.push('Starting');if(b.phase==='round')parts.push('Round over');if(b.phase==='final')parts.push('Final');
    h.appendChild(node('span','sb-sub',parts.join(' · ')));if(typeof b.left==='number')h.appendChild(node('span','sb-time',clock(b.left)));
    return h;
  }
  function row(b,r,cols,showRank){
    var tr=node('div','sb-row'+(r.self?' self':'')+(r.status==='down'?' down':'')+(r.team?' team'+r.team:''));
    if(showRank)tr.appendChild(node('span','sb-rank',r.rank));
    // Bots: a BOT tag before the name (their names start with "BOT").
    var nm=node('span','sb-name');if(r.bot)nm.appendChild(node('span','sb-bot','BOT'));
    nm.appendChild(node('span','',(r.bot?String(r.name).replace(/^BOT /,''):r.name)+(r.self&&r.name!=='You'?' (you)':'')));tr.appendChild(nm);
    cols.forEach(function(c){tr.appendChild(node('span','sb-num',c[1](r)));});
    return tr;
  }
  // CS: each side as its own block (yours first), CS-style: the side's name, score and players
  // alive, then its players with money (your side only), kills, deaths and K/D; the down greyed.
  var csCols=[['$',function(r){return typeof r.money==='number'?'$'+num(r.money):'';}],['K',function(r){return num(r.frags);}],['D',function(r){return num(r.deaths);}],['K/D',function(r){return num(r.kd,2);}]];
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
  function render(target,b,mode){
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
  root.AimModStandings={render:render};
})(window);
