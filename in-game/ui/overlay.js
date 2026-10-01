// Overlay scene runtime: the in-game HUD view (/overlay?surface=game) and the
// OBS browser sources (/scene?id=<scene>[&widget=<id>], and the earlier
// /overlay?surface=obs link, which shows the default OBS scene). Read-only:
// polls the service's overlay feed and draws the scene with the shared widget
// library. It never takes input, and it hides itself when its surface is off.
(function(root){
  'use strict';
  var doc=root.document,M=root.AimModOverlayModel,W=root.AimModOverlayWidgets;
  var host=doc.getElementById('scene'),visible=true,timer=null,motionTimer=null,generation=0,requests=[];
  var search=root.location.search||'';
  function param(name){var m=new RegExp('(?:\\?|&)'+name+'=([^&]*)').exec(search);if(!m)return '';try{return decodeURIComponent(m[1].replace(/\+/g,' '));}catch(e){return '';}}
  var page=root.location.pathname.slice(root.location.pathname.lastIndexOf('/')+1);
  var surface=param('surface')==='game'?'game':param('surface')==='obs'||page==='scene'?'obs':'game';
  var sceneParam=param('id'),widgetParam=param('widget');
  var store=null,storeRevision=-1,feed=null,motion=null,images={},lastShown=false;
  function base(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  function get(path,timeout,done){
    var ticket=generation,x=new root.XMLHttpRequest(),finished=false;requests.push(x);x.open('GET',base()+'/'+path,true);x.timeout=timeout;
    function finish(ok,data){if(finished)return;finished=true;for(var i=0;i<requests.length;i++)if(requests[i]===x){requests.splice(i,1);break;}if(ticket!==generation||!visible)return;done(ok,data);}
    x.onreadystatechange=function(){if(x.readyState!==4)return;if(x.status!==200){finish(false,null);return;}try{finish(true,JSON.parse(x.responseText));}catch(e){finish(false,null);}};
    x.onerror=x.ontimeout=function(){finish(false,null);};x.send(null);
  }
  function scene(){if(!store)return null;var id=sceneParam||(surface==='game'?store.gameScene:store.obsScene);return M.find(store,id)||(sceneParam?null:store.scenes[0]);}
  function image(name){
    if(!name)return null;var key=String(name);if(images[key])return images[key];if(typeof root.Image!=='function')return null;
    var img=new root.Image();img.onload=function(){draw();};img.src=base()+'/overlay-crosshair?file='+encodeURIComponent(key);images[key]=img;return img;
  }
  function hide(){if(host.className!=='hidden')host.className='hidden';lastShown=false;}
  function fit(sc){
    var w=root.innerWidth||1920,hgt=root.innerHeight||1080,scale;
    if(widgetParam){var wd=M.findWidget(sc,widgetParam);scale=wd?w/wd.w:1;host.style.width=(wd?wd.w:1920)+'px';host.style.height=(wd?Math.max(wd.h,hgt/scale):1080)+'px';}
    else{scale=Math.min(w/M.BASE_W,hgt/M.BASE_H);host.style.width=M.BASE_W+'px';host.style.height=M.BASE_H+'px';}
    var t='scale('+scale.toFixed(5)+')';if(host.style.transform!==t)host.style.transform=t;
  }
  function draw(){
    var sc=scene();if(!visible||!sc||!feed||!feed.enabled){hide();return false;}
    var data=feed;data.peripherals=store.profile.peripherals;var ctx=M.context(data);
    fit(sc);var shown=W.render(host,sc,data,{context:ctx,only:widgetParam||'',motion:motion,image:image,now:new Date().getTime()});
    if(shown>0){if(!lastShown){host.className='';lastShown=true;}}else hide();
    return shown>0;
  }
  function needs(){var sc=scene();return sc&&feed?W.needsMotion(sc,M.context(feed)):{path:false,input:false};}
  function liveShown(){var sc=scene();if(!sc||!feed||!feed.live||!feed.live.active)return false;for(var i=0;i<sc.widgets.length;i++){var t=sc.widgets[i].type;if(sc.widgets[i].visible&&(t==='live-stats'||t==='pb-pace'||t==='standings'||t==='now-playing'))return true;}return false;}
  function poll(){
    timer=null;if(!visible)return;
    get('overlay-feed?surface='+surface,1500,function(ok,data){
      if(!ok){feed=null;hide();schedule(1000);return;}
      feed=data;
      if(data.storeRevision!==storeRevision||!store){get('overlay-scenes',3000,function(ok2,s){if(ok2&&s&&s.scenes){store=M.normalize(s);storeRevision=data.storeRevision;}draw();schedule(liveShown()?100:500);motionLoop();});return;}
      draw();schedule(liveShown()?100:500);motionLoop();
    });
  }
  function schedule(delay){if(visible&&timer===null)timer=root.setTimeout(poll,delay);}
  function motionLoop(){
    if(motionTimer!==null||!visible)return;var n=needs();if(!n.path&&!n.input){motion=null;return;}
    motionTimer=root.setTimeout(function(){motionTimer=null;get('overlay-motion?surface='+surface+(n.path?'&path=1':'')+(n.input?'&input=1':''),1000,function(ok,data){motion=ok?data:null;draw();motionLoop();});},33);
  }
  function stop(){generation++;if(timer!==null){root.clearTimeout(timer);timer=null;}if(motionTimer!==null){root.clearTimeout(motionTimer);motionTimer=null;}for(var i=0;i<requests.length;i++)requests[i].abort();requests=[];}
  function setVisible(value){visible=value!==false;stop();if(visible)poll();else hide();}
  if(root.engine&&root.engine.on)root.engine.on('AimModVisibility',setVisible);
  if(root.addEventListener){root.addEventListener('resize',function(){draw();});root.addEventListener('pagehide',function(){setVisible(false);});}
  root.AimModLiveOverlay={setVisible:setVisible,surface:surface,draw:draw};setVisible(true);
})(window);
