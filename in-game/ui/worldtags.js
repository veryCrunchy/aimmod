// Name tags over other players, drawn by the notice layer from AimModCore's "AimModTags" event
// (native-mod/DESIGN.md "World tags"): teammates in their team colour (through walls, like CS),
// an enemy only while under the crosshair. Positions are 0..1 of the viewport and arrive every
// frame, so the nodes are kept and only moved. CS teammates also show their gear under the name,
// as in CS: the bomb, the weapon in hand, armour and helmet, the defuse kit, their grenades and a
// health bar.
// Gameface: DOM only, no grid, gap or var().
(function(root){
  'use strict';
  var layer=null,nodes=[];
  function ensure(){
    if(layer)return layer;
    layer=root.document.createElement('div');layer.id='world-tags';layer.className='wt-layer';
    // Under the flash's white (#cs-flash), above everything else on the page.
    var body=root.document.body,top=root.document.getElementById?root.document.getElementById('cs-flash'):null;
    if(top&&top.parentNode===body&&body.insertBefore)body.insertBefore(layer,top);else body.appendChild(layer);return layer;
  }
  function span(cls,parent){var el=root.document.createElement('span');el.className=cls;parent.appendChild(el);return el;}
  function tagNode(i){
    if(nodes[i])return nodes[i];
    var el=root.document.createElement('div');el.className='wt-tag';
    el.nameNode=span('wt-name',el);
    var gear=span('wt-gear',el);
    el.bombNode=span('wt-chip wt-c4',gear);el.bombNode.textContent='C4';
    el.weaponNode=span('wt-weapon',gear);
    el.armorNode=span('wt-chip wt-armor',gear);
    el.kitNode=span('wt-chip wt-kit',gear);el.kitNode.textContent='KIT';
    // Up to four grenades (g=he,flash,flash,smoke), one small chip each.
    el.nadeNodes=[];for(var n=0;n<4;n++){el.nadeNodes.push(span('wt-chip wt-nade',gear));}
    var bar=span('wt-hp',el);el.hpBar=bar;el.hpFill=span('wt-hp-fill',bar);
    el.distNode=span('wt-dist',el);
    ensure().appendChild(el);nodes[i]=el;return el;
  }
  // "w=AK-47;hp=87;ar=1;hm=1;kit=1;c4=1" -> {w:'AK-47',hp:'87',...}
  function gearOf(text){
    var out={};if(typeof text!=='string'||!text)return out;
    var parts=text.split(';');
    for(var i=0;i<parts.length;i++){var at=parts[i].indexOf('=');if(at>0)out[parts[i].slice(0,at)]=parts[i].slice(at+1);}
    return out;
  }
  function show(el,on){var d=on?'':'none';if(el.style.display!==d)el.style.display=d;}
  function text(el,value){if(el.textContent!==value)el.textContent=value;}
  function team(t){return t==='T'||t==='1'?'t':t==='CT'||t==='2'?'ct':'none';}
  var NADES={he:'HE',flash:'FL',smoke:'SM',molotov:'MO',incendiary:'IN',decoy:'DC'};
  function render(data){
    var tags=data&&data.tags&&data.tags.length?data.tags:[];
    for(var i=0;i<tags.length&&i<32;i++){
      var t=tags[i],el=tagNode(i),g=gearOf(t.g),geared=!!t.g&&!!t.a;
      var cls='wt-tag '+team(t.t)+(t.f?' friend':' enemy')+(t.a?'':' down')+(t.c?' aimed':'')+(geared?' geared':'');
      if(el.className!==cls)el.className=cls;
      text(el.nameNode,String(t.n||'').slice(0,32));
      text(el.weaponNode,geared&&g.w?String(g.w).slice(0,24):'');
      show(el.bombNode,geared&&g.c4==='1');
      show(el.kitNode,geared&&g.kit==='1');
      show(el.armorNode,geared&&g.ar==='1');text(el.armorNode,g.hm==='1'?'A+H':'A');
      var nades=geared&&g.g?String(g.g).split(','):[];
      for(var k=0;k<el.nadeNodes.length;k++){var id=nades[k]||'';var on=!!NADES[id];show(el.nadeNodes[k],on);
        if(on){text(el.nadeNodes[k],NADES[id]);var nc='wt-chip wt-nade wt-'+id;if(el.nadeNodes[k].className!==nc)el.nadeNodes[k].className=nc;}}
      var hp=geared&&g.hp!==undefined?Math.max(0,Math.min(100,+g.hp||0)):null;
      show(el.hpBar,hp!==null);
      if(hp!==null){var w=hp+'%';if(el.hpFill.style.width!==w)el.hpFill.style.width=w;var hc='wt-hp-fill'+(hp<30?' low':hp<60?' mid':'');if(el.hpFill.className!==hc)el.hpFill.className=hc;}
      text(el.distNode,t.f&&typeof t.d==='number'?Math.round(t.d)+' m':'');
      el.style.left=(Math.max(0,Math.min(1,t.x))*100).toFixed(2)+'%';
      el.style.top=(Math.max(0,Math.min(1,t.y))*100).toFixed(2)+'%';
      el.style.display='block';
    }
    for(var j=tags.length;j<nodes.length;j++)if(nodes[j].style.display!=='none')nodes[j].style.display='none';
  }
  // What arrived, for the service log (AimModCore logs what it pushed): the events, the tags in the
  // last one, and how the page listens. Reported every 10 s while it changes.
  var stats={events:0,bad:0,last:0,via:'none'},reported='',reportTimer=null;
  function onTags(json){var data=null;try{data=typeof json==='string'?JSON.parse(json):json;}catch(e){data=null;}
    stats.events++;if(!data)stats.bad++;stats.last=data&&data.tags&&data.tags.length?data.tags.length:0;render(data);}
  function report(){
    reportTimer=null;
    var line='listening via '+stats.via+'; '+stats.events+' events in the last 10 s'+(stats.bad?' ('+stats.bad+' unreadable)':'')+', '+stats.last+' tags in the last one';
    var quiet=stats.events===0&&reported.indexOf(' 0 events')>=0;
    stats.events=0;stats.bad=0;
    if(line!==reported&&!quiet&&root.XMLHttpRequest&&root.location){reported=line;
      var p=root.location.pathname,x=new root.XMLHttpRequest();x.open('POST',p.slice(0,p.lastIndexOf('/'))+'/multiplayer',true);x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');
      x.send(JSON.stringify({action:'tags-debug',counts:line}));}
    if(root.setTimeout)reportTimer=root.setTimeout(report,10000);
  }
  // AimModCore's events come through Gameface's engine object (cohtml.js, which notify.html loads
  // first). Without it the page gets a minimal one: the native side delivers events by calling
  // engine._trigger(name, ...args).
  function listen(name,fn){
    var e=root.engine;
    if(e&&typeof e.on==='function'&&!e._aimmodHandlers){e.on(name,fn);return 'engine.on';}
    if(!e)e=root.engine={};
    if(!e._aimmodHandlers){
      var handlers=e._aimmodHandlers={},before=e._trigger;
      e._trigger=function(n){var list=handlers[n],args=Array.prototype.slice.call(arguments,1);if(list)for(var i=0;i<list.length;i++)list[i].apply(null,args);if(typeof before==='function')return before.apply(e,arguments);};
      e.on=function(n,f){(handlers[n]=handlers[n]||[]).push(f);};
    }
    e.on(name,fn);return 'a minimal engine (no cohtml.js)';
  }
  root.AimModListen=function(name,fn){return listen(name,fn);};
  stats.via=listen('AimModTags',onTags);
  if(root.setTimeout)reportTimer=root.setTimeout(report,10000);
  root.AimModWorldTags={render:render,onTags:onTags,gearOf:gearOf,stats:stats,report:report};
})(window);
