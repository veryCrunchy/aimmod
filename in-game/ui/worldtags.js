// Name tags over other players, drawn by the notice layer from AimModCore's "AimModTags" event
// (native-mod/DESIGN.md "World tags"): teammates in their team colour (through walls, like CS),
// an enemy only while under the crosshair. Positions are 0..1 of the viewport and arrive every
// frame, so the nodes are kept and only moved. CS teammates also show their gear under the name,
// as in CS: the bomb, the weapon in hand, armour and helmet, the defuse kit and a health bar.
// Gameface: DOM only, no grid, gap or var().
(function(root){
  'use strict';
  var layer=null,nodes=[];
  function ensure(){
    if(layer)return layer;
    layer=root.document.createElement('div');layer.id='world-tags';layer.className='wt-layer';
    root.document.body.appendChild(layer);return layer;
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
  function onTags(json){var data=null;try{data=typeof json==='string'?JSON.parse(json):json;}catch(e){data=null;}render(data);}
  if(root.engine&&root.engine.on)root.engine.on('AimModTags',onTags);
  root.AimModWorldTags={render:render,onTags:onTags,gearOf:gearOf};
})(window);
