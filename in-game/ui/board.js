// OBS browser source: the live match standings, larger for the stream. Read-only:
// polls the OBS host's board-state (the same model the in-game panel uses).
(function(root){
  'use strict';
  var box=root.document.getElementById('board'),last='',timer=null;
  function base(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  function poll(){
    var x=new root.XMLHttpRequest();x.open('GET',base()+'/board-state',true);x.timeout=2000;
    x.onreadystatechange=function(){if(x.readyState!==4)return;var data=null;if(x.status===200){try{data=JSON.parse(x.responseText);}catch(e){data=null;}}show(data&&data.board);};
    x.onerror=x.ontimeout=function(){show(null);};
    x.send(null);clearTimeout(timer);timer=setTimeout(poll,500);
  }
  function show(b){var key=b?JSON.stringify(b):'';if(key===last)return;last=key;box.className=b?'':'empty';if(root.AimModStandings)root.AimModStandings.render(box,b,'stream');}
  poll();
})(window);
