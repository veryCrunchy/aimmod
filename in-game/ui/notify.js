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
    if(!n||!n.active){box.className='';return;}
    box.className='show '+(n.kind||'info');
    var card=node('div','toast');
    var top=node('div','brand','AIMMOD · MULTIPLAYER');card.appendChild(top);
    var row=node('div','row');
    if(typeof n.countdown==='number')row.appendChild(node('div','count',String(n.countdown)));
    var text=node('div','text');text.appendChild(node('div','title',n.title||''));text.appendChild(node('div','body',n.body||''));row.appendChild(text);
    if(n.key)row.appendChild(node('div','key',n.key));
    card.appendChild(row);
    // An incoming invite can be answered right here (the layer takes clicks only for these).
    if(n.invite){var row2=node('div','actions');row2.appendChild(button('Join','primary',function(){answer('accept-invite',n.invite);}));row2.appendChild(button('Dismiss','',function(){answer('decline-invite',n.invite);}));card.appendChild(row2);}
    box.appendChild(card);
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
