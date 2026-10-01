// Name tags over other players, drawn by the notice layer from AimModCore's "AimModTags" event
// (native-mod/DESIGN.md "World tags"): teammates in their team colour (through walls, like CS),
// an enemy only while under the crosshair. Positions are 0..1 of the viewport and arrive every
// frame, so the nodes are kept and only moved. Gameface: DOM only, no grid, gap or var().
(function(root){
  'use strict';
  var layer=null,nodes=[];
  function ensure(){
    if(layer)return layer;
    layer=root.document.createElement('div');layer.id='world-tags';layer.className='wt-layer';
    root.document.body.appendChild(layer);return layer;
  }
  function tagNode(i){
    if(nodes[i])return nodes[i];
    var el=root.document.createElement('div');el.className='wt-tag';
    var name=root.document.createElement('span');name.className='wt-name';el.appendChild(name);
    var dist=root.document.createElement('span');dist.className='wt-dist';el.appendChild(dist);
    el.nameNode=name;el.distNode=dist;ensure().appendChild(el);nodes[i]=el;return el;
  }
  function team(t){return t==='T'||t==='1'?'t':t==='CT'||t==='2'?'ct':'none';}
  function render(data){
    var tags=data&&data.tags&&data.tags.length?data.tags:[];
    for(var i=0;i<tags.length&&i<32;i++){
      var t=tags[i],el=tagNode(i);
      var cls='wt-tag '+team(t.t)+(t.f?' friend':' enemy')+(t.a?'':' down')+(t.c?' aimed':'');
      if(el.className!==cls)el.className=cls;
      var name=String(t.n||'').slice(0,32);if(el.nameNode.textContent!==name)el.nameNode.textContent=name;
      var d=t.f&&typeof t.d==='number'?Math.round(t.d)+' m':'';if(el.distNode.textContent!==d)el.distNode.textContent=d;
      el.style.left=(Math.max(0,Math.min(1,t.x))*100).toFixed(2)+'%';
      el.style.top=(Math.max(0,Math.min(1,t.y))*100).toFixed(2)+'%';
      el.style.display='block';
    }
    for(var j=tags.length;j<nodes.length;j++)if(nodes[j].style.display!=='none')nodes[j].style.display='none';
  }
  function onTags(json){var data=null;try{data=typeof json==='string'?JSON.parse(json):json;}catch(e){data=null;}render(data);}
  if(root.engine&&root.engine.on)root.engine.on('AimModTags',onTags);
  root.AimModWorldTags={render:render,onTags:onTags};
})(window);
