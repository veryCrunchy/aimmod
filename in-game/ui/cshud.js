// CS competitive HUD for the in-game notice layer, kept clear of the crosshair:
// score strip and clocks at the top, money/health/armour bottom left, the
// plant/defuse bar low in the middle, the kill feed top right, round-end and
// halftime banners, and the clickable buy menu on the left (B opens it in buy
// time; number keys still buy). Gameface: DOM and canvas only, solid colours.
(function(root){
  'use strict';
  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=String(text);return el;}
  function money(v){return '$'+String(Math.round(v)).replace(/\B(?=(\d{3})+(?!\d))/g,',');}
  function clock(s){if(typeof s!=='number')return '';var m=Math.floor(s/60),r=s%60;return m+':'+(r<10?'0':'')+r;}
  // Small solid icons: armour shield, helmet, defuse kit.
  function icon(kind,on){
    var c=node('canvas','cs-icon'+(on?' on':''));c.width=36;c.height=36;var x=c.getContext&&c.getContext('2d');if(!x)return c;x.scale(2,2);
    x.fillStyle=on?'#66ccff':'#3a4a44';x.strokeStyle=on?'#66ccff':'#3a4a44';x.lineWidth=1.6;
    if(kind==='armor'){x.beginPath();x.moveTo(9,2);x.lineTo(16,5);x.lineTo(15,11);x.lineTo(9,16);x.lineTo(3,11);x.lineTo(2,5);x.closePath();x.fill();}
    else if(kind==='helmet'){x.beginPath();x.arc(9,11,7,Math.PI,0);x.fill();x.fillRect(1,11,16,3);}
    else{x.fillRect(3,6,12,9);x.fillStyle=on?'#0f1714':'#16211d';x.fillRect(6,9,6,3);x.fillStyle=on?'#f0b45a':'#3a4a44';x.fillRect(7,3,4,3);}
    return c;
  }
  var CATS=[['pistol','Pistols'],['smg','SMGs'],['rifle','Rifles'],['heavy','Heavy'],['gear','Gear']];
  // The menu is built once per change of what it offers and kept in place while the clock or the
  // rest of the HUD redraws, so a click is never lost to a redraw between press and release.
  function buyMenu(c,act){
    var box=node('div','cs-buy');box.setAttribute('role','dialog');box.setAttribute('aria-label','Buy menu');
    var head=node('div','cs-buy-head');head.appendChild(node('strong','','Buy'));head.appendChild(node('span','cs-buy-money',money(c.money)));
    var left=node('span','cs-buy-left',typeof c.buyLeft==='number'?'Buy time '+clock(c.buyLeft):'Buy time over');head.appendChild(left);box.timeNode=left;
    var close=node('button','cs-buy-close',c.buyKey+' closes');close.type='button';close.onclick=function(){act('cs-buy-menu','');};head.appendChild(close);
    box.appendChild(head);
    // Two columns so the whole menu fits at 720p: pistols, SMGs and heavy; rifles and gear.
    var cols=node('div','cs-buy-cols'),left=node('div','cs-buy-col'),right=node('div','cs-buy-col');cols.appendChild(left);cols.appendChild(right);box.appendChild(cols);
    CATS.forEach(function(cat){
      var items=(c.buy||[]).filter(function(i){return i.category===cat[0];});
      var group=node('div','cs-buy-group');group.appendChild(node('div','cs-buy-cat',cat[1]));
      if(!items.length)group.appendChild(node('div','cs-buy-none','None in this mode yet'));
      items.forEach(function(i){
        var b=node('button','cs-item'+(i.owned?' owned':i.disabled?' off':i.affordable?' ok':''));b.type='button';
        if(i.disabled&&!i.owned)b.disabled=true;
        b.appendChild(node('div','cs-item-key',typeof i.key==='number'?String(i.key):''));
        var t=node('div','cs-item-text');t.appendChild(node('div','cs-item-name',i.label));var why=i.owned?'Owned':i.disabled||'';if(why)t.appendChild(node('div','cs-item-why',why));b.appendChild(t);
        // The KovaaK's weapon profile the item maps to, for players who know them.
        if(i.profile)b.title=i.profile;
        b.appendChild(node('div','cs-item-price',money(i.price)));
        b.onclick=function(){if(!b.disabled&&!i.owned)act('cs-buy',i.id);};
        group.appendChild(b);
      });
      (cat[0]==='rifle'||cat[0]==='gear'?right:left).appendChild(group);
    });
    return box;
  }
  function render(root2,c,act){
    // Two layers: the HUD (redrawn every time) and the buy menu (kept while it offers the same).
    var layers=root2.csLayers;
    if(!layers){while(root2.firstChild)root2.removeChild(root2.firstChild);layers=root2.csLayers={hud:node('div','cs-layer'),menu:node('div','cs-layer'),key:null};root2.appendChild(layers.hud);root2.appendChild(layers.menu);}
    var target=layers.hud;
    while(target.firstChild)target.removeChild(target.firstChild);
    var menuKey=c&&c.buyOpen?JSON.stringify([c.buy,c.money,c.buyKey]):null;
    if(menuKey!==layers.key){while(layers.menu.firstChild)layers.menu.removeChild(layers.menu.firstChild);layers.key=menuKey;if(menuKey)layers.menu.appendChild(buyMenu(c,act));}
    else if(menuKey&&layers.menu.firstChild&&layers.menu.firstChild.timeNode)layers.menu.firstChild.timeNode.textContent=typeof c.buyLeft==='number'?'Buy time '+clock(c.buyLeft):'Buy time over';
    if(!c){root2.className='';return;}
    root2.className='show';
    // Top: score strip and the clock for this phase.
    var top=node('div','cs-top');
    var t=node('div','cs-score t'+(c.side==='T'?' mine':''));t.appendChild(node('span','cs-team','T'));t.appendChild(node('span','cs-points',c.tScore));
    var mid=node('div','cs-clock'+(c.phase==='planted'?' bomb':''));
    // Freeze time here; the buy menu counts the buy window (freeze plus buy time) and says so.
    var label=c.phase==='freeze'?'Freeze time':c.phase==='planted'?'Bomb planted'+(c.site?' · '+c.site:''):c.phase==='end'?'Round over':'Round '+c.round;
    mid.appendChild(node('span','cs-clock-label',label));
    mid.appendChild(node('span','cs-clock-time',c.phase==='planted'&&typeof c.bombIn==='number'?clock(c.bombIn):clock(c.left)));
    mid.appendChild(node('span','cs-clock-round','Round '+c.round+' of '+c.rounds));
    var ct=node('div','cs-score ct'+(c.side==='CT'?' mine':''));ct.appendChild(node('span','cs-points',c.ctScore));ct.appendChild(node('span','cs-team','CT'));
    top.appendChild(t);top.appendChild(mid);top.appendChild(ct);target.appendChild(top);
    // Bomb sites on a compass under the strip (90 degrees either side; behind clamps to an edge), and
    // the site letter (and callout) while you stand in one.
    if(c.sites&&c.sites.length&&!c.buyOpen){
      var comp=node('div','cs-compass');
      c.sites.forEach(function(s){var b=Math.max(-90,Math.min(90,s.bearing));var m=node('div','cs-mark'+(Math.abs(s.bearing)>90?' behind':'')+(c.inSite===s.name?' here':''));
        m.style.left=Math.round((b+90)/180*100)+'%';m.appendChild(node('span','cs-mark-name',s.name));m.appendChild(node('span','cs-mark-dist',s.meters+' m'));comp.appendChild(m);});
      target.appendChild(comp);
    }
    if(c.inSite||c.callout){var here=node('div','cs-site');if(c.inSite)here.appendChild(node('strong','','Bomb site '+c.inSite));if(c.callout)here.appendChild(node('span','',c.callout));target.appendChild(here);}
    // Banners: round end with the reason, halftime side switch.
    if(c.banner){var bn=node('div','cs-banner team'+c.banner.team+(c.banner.won?' won':' lost'));bn.appendChild(node('strong','',c.banner.title));bn.appendChild(node('span','',c.banner.reason));target.appendChild(bn);}
    if(c.notice)target.appendChild(node('div','cs-notice',c.notice));
    // Kill feed, top right.
    if(c.feed&&c.feed.length){var feed=node('div','cs-feed');c.feed.forEach(function(f){var l=node('div','cs-kill'+(f.you?' '+f.you:''));l.appendChild(node('span','cs-k team'+f.killerTeam,f.killer));l.appendChild(node('span','cs-w',(f.weapon||'')+(f.head?' · headshot':'')));l.appendChild(node('span','cs-v',f.victim));feed.appendChild(l);});target.appendChild(feed);}
    // Plant or defuse: a hint and the progress bar, low in the middle.
    var progress=typeof c.plantProgress==='number'?c.plantProgress:typeof c.defuseProgress==='number'?c.defuseProgress:null;
    // Why the last plant, defuse or drop didn't happen ("Not in a bomb site", "You don't have the bomb").
    if(c.refused&&progress===null)target.appendChild(node('div','cs-refused',c.refused));
    if(c.useHint||progress!==null){var use=node('div','cs-use');use.appendChild(node('span','cs-use-text',progress!==null?(typeof c.plantProgress==='number'?'Planting…':'Defusing…'):c.useHint));
      if(progress!==null){var bar=node('div','cs-bar');var fill=node('div','cs-fill');fill.style.width=Math.round(progress*100)+'%';bar.appendChild(fill);use.appendChild(bar);}target.appendChild(use);}
    // Bottom left: money with the last round's change, health, armour, helmet and kit.
    var me=node('div','cs-me'+(c.alive?'':' down'));
    var cash=node('div','cs-money');cash.appendChild(node('span','',money(c.money)));if(typeof c.moneyDelta==='number'&&c.moneyDelta)cash.appendChild(node('span','cs-delta','+'+money(c.moneyDelta).slice(1)));me.appendChild(cash);
    var vit=node('div','cs-vitals');
    if(c.alive){vit.appendChild(node('span','cs-hp'+(c.health<30?' low':''),Math.round(c.health)));vit.appendChild(icon('armor',c.armor>0));if(c.armor>0)vit.appendChild(node('span','cs-armor',Math.round(c.armor)));vit.appendChild(icon('helmet',c.helmet));if(c.side==='CT')vit.appendChild(icon('kit',c.kit));}
    else vit.appendChild(node('span','cs-hp down','Down'));
    me.appendChild(vit);
    var gun=[c.primary,c.secondary].filter(function(x){return !!x;}).join(' · ');if(gun)me.appendChild(node('div','cs-guns',gun));
    // The bomb: yours (with the drop key), or which teammate has it.
    if(c.hasBomb){var bomb=node('div','cs-bomb');bomb.appendChild(node('span','cs-bomb-icon','C4'));bomb.appendChild(node('span','','You have the bomb · '+(c.dropKey||'G')+' drops it'));me.appendChild(bomb);}
    else if(c.bombCarrier)me.appendChild(node('div','cs-bomb mate','Bomb: '+c.bombCarrier));
    if(c.buyWindow&&!c.buyOpen)me.appendChild(node('div','cs-key-hint','Press '+c.buyKey+' to buy'));
    (c.keyClashes||[]).forEach(function(k){me.appendChild(node('div','cs-clash',k));});
    target.appendChild(me);
  }
  root.AimModCsHud={render:render};
})(window);
