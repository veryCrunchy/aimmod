// Always-on, non-interactive multiplayer notice shown by AimModNativeUI
// (Notify.lua) outside the AimMod panel: invites, "host is starting" and
// countdowns. It only reads the service's notice; keys are handled by the service.
(function(root){
  'use strict';
  var box=root.document.getElementById('notice'),last='',timer=null;
  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=text;return el;}
  function base(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  function render(n){
    var key=n?JSON.stringify(n):'';if(key===last)return;last=key;
    while(box.firstChild)box.removeChild(box.firstChild);
    if(!n||(!n.active&&!n.badge&&!n.duel)){box.className='';return;}
    var extra=n.duel?' duel-on':'';
    box.className='show'+extra;
    if(n.duel)box.appendChild(duel(n.duel));
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
  // Tracking duel strip at the top edge: role, live bar and time left. Never near the crosshair.
  function duel(d){
    var tracking=d.role==='track';
    var strip=node('div','duel'+(tracking?' track':' dodge'));
    strip.appendChild(node('div','role',tracking?'YOU TRACK':'YOU DODGE'));
    var mid=node('div','duel-mid');
    var line=node('div','duel-line',(tracking?'On '+d.opponent:d.opponent+' on you')+(typeof d.percent==='number'?' · '+d.percent.toFixed(1)+' %':'')+(d.disputed?' · disputed':''));
    mid.appendChild(line);
    var bar=node('div','duel-bar');var fill=node('div','duel-fill');
    var share=typeof d.onTarget==='number'?Math.max(0,Math.min(100,d.onTarget)):0;fill.style.width=share+'%';
    bar.appendChild(fill);mid.appendChild(bar);strip.appendChild(mid);
    strip.appendChild(node('div','duel-time',typeof d.left==='number'?d.left+' s':'R'+d.round+'/'+d.rounds));
    return strip;
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
