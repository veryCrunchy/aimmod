(function(root){
  'use strict';
  // Page owners can mount this beside a measured weakness using its replay id.
  function mount(parent,replayId){
    var button=root.document.createElement('button'),request=null,disposed=false;
    button.type='button';button.className='button';button.textContent='Train this';
    button.title='Generate and open a freeplay drill';parent.appendChild(button);
    button.onclick=function(){
      if(request||disposed)return;
      button.disabled=true;
      var xhr=new root.XMLHttpRequest();request=xhr;
      xhr.open('POST',root.location.pathname.replace(/\/[^/]*$/,'')+'/practice-drills/'+encodeURIComponent(replayId),true);
      xhr.timeout=60000;xhr.setRequestHeader('X-AimMod-UI','1');
      function finish(){if(disposed)return;request=null;button.disabled=false;button.textContent=xhr.status===202?'Opening freeplay drill':'Train this: retry';}
      xhr.onload=finish;xhr.onerror=finish;xhr.ontimeout=finish;xhr.send(null);
    };
    return function(){disposed=true;if(request)request.abort();button.onclick=null;if(button.parentNode)button.parentNode.removeChild(button);};
  }
  root.AimModPracticeDrills={mount:mount};
})(typeof window==='undefined'?globalThis:window);
