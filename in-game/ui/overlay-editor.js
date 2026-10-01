// Overlays page: scene editor (drag, resize and snap widgets on a 16:9 canvas
// with a live preview), per-widget and theme settings, share codes, OBS and
// in-game outputs, the profile (peripherals) and the VS opponent. The scene
// store is the service's (overlay-scenes); legacy overlay-settings keeps the
// two master switches the in-game HUD view reads. Gameface-safe ES5 DOM.
(function(root){
  'use strict';
  var M=root.AimModOverlayModel,W=root.AimModOverlayWidgets;
  var container=null,generation=0,pending=[],st=null,saveTimer=null,feedTimer=null,tickTimer=null,drag=null,listening=false;
  var TABS=[['editor','Editor'],['outputs','OBS and in-game'],['profile','Profile'],['vs','VS opponent']];
  var CONTEXTS=[['menu','Menu'],['scenario','Scenario'],['match','Match']];
  function blank(){return {store:null,settings:null,setup:null,opponents:null,feed:null,tab:'editor',scene:'',selected:'',data:'sample',ctx:'scenario',status:'',warn:false,busy:false,panel:'',importText:'',importError:'',rename:false,resolution:'1920x1080',failed:false};}
  function el(tag,css,text){var n=root.document.createElement(tag);if(css)n.className=css;if(text!==undefined&&text!==null)n.textContent=String(text);return n;}
  function add(parent,child){parent.appendChild(child);return child;}
  function base(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  function request(path,body,done){
    var ticket=generation,x=new root.XMLHttpRequest();pending.push(x);x.open(body?'POST':'GET',base()+'/'+path,true);x.timeout=12000;
    if(body){x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');}
    var finished=false;function finish(ok,data){if(finished)return;finished=true;pending=pending.filter(function(y){return y!==x;});if(ticket!==generation||!container)return;done(ok,data);}
    x.onreadystatechange=function(){if(x.readyState!==4)return;var data=null;try{data=JSON.parse(x.responseText);}catch(e){data=null;}finish(x.status===200&&data!==null,data);};
    x.onerror=x.ontimeout=function(){finish(false,null);};x.send(body?JSON.stringify(body):null);
  }
  function button(label,fn,css,id){var b=el('button','button'+(css?' '+css:''),label);b.type='button';if(id)b.id=id;b.onclick=function(e){if(st&&st.busy)return;fn(e);};return b;}
  function toggle(on,label,fn,id){var b=el('button','switch'+(on?' on':''));b.type='button';if(id)b.id=id;b.appendChild(el('span','knob'));b.setAttribute('role','switch');b.setAttribute('aria-checked',String(!!on));b.setAttribute('aria-label',label);b.onclick=function(){fn(!on);};return b;}
  function segmented(options,value,fn,label,id){var box=el('div','segmented ove-seg');box.setAttribute('role','group');box.setAttribute('aria-label',label);
    for(var i=0;i<options.length;i++)(function(opt,index){var b=button(opt[1],function(){fn(opt[0]);},opt[0]===value?'primary':'',id?id+'-'+opt[0]:'');b.setAttribute('aria-pressed',String(opt[0]===value));box.appendChild(b);})(options[i],i);return box;}
  function field(input,hint){return root.AimModFormat&&root.AimModFormat.field?root.AimModFormat.field(input,hint):input;}
  function scene(){return st&&st.store?M.find(st.store,st.scene):null;}
  function selectedWidget(){return M.findWidget(scene(),st.selected);}
  function obsBase(){return st.setup&&st.setup.obsBase?st.setup.obsBase:'';}
  function sceneUrl(id,widget){var b=obsBase();if(!b)return '';var r=st.resolution.split('x');return b+'/scene?id='+encodeURIComponent(id)+(widget?'&widget='+encodeURIComponent(widget):'')+'&w='+r[0]+'&h='+r[1];}
  // ---- persistence ----
  function setStatus(text,warn){st.status=text;st.warn=!!warn;var n=root.document.getElementById('ove-status');if(n){n.textContent=text;n.className='ove-status'+(warn?' warn':'')+(text?'':' idle');}}
  function changed(rebuild){setStatus('Saving…');if(saveTimer!==null)root.clearTimeout(saveTimer);saveTimer=root.setTimeout(save,450);if(rebuild!==false)render();else preview();}
  function save(){saveTimer=null;if(!st||!st.store)return;var body=M.normalize(st.store);request('overlay-scenes',body,function(ok,data){if(ok&&data&&data.scenes){setStatus(saveTimer===null?'All changes saved.':'Saving…');}else setStatus('Could not save. Your last change will retry when you edit again.',true);});}
  function saveLegacy(patch){st.busy=true;request('overlay-settings',patch,function(ok,data){st.busy=false;if(ok)st.settings=data;render();setStatus(ok?'Saved.':'Could not save. Please try again.',!ok);});}
  // ---- data for the preview ----
  function data(){var now=new Date().getTime();if(st.data==='live'&&st.feed){var d=st.feed;d.peripherals=st.store.profile.peripherals;return d;}var s=W.sample(now);if(st.store.profile.peripherals.length)s.peripherals=st.store.profile.peripherals;if(st.ctx==='match'){var m=W.sampleMatch();s.board=m.board;s.tournament=m.tournament;}if(st.ctx==='menu')s.live={available:true,active:false};return s;}
  function pollFeed(){if(feedTimer!==null){root.clearTimeout(feedTimer);feedTimer=null;}if(!container||!st||st.data!=='live')return;request('overlay-feed',null,function(ok,d){if(ok)st.feed=d;preview();feedTimer=root.setTimeout(pollFeed,1000);});}
  var motionBusy=false;
  // Live data: the mouse path and input display follow the real motion feed while the scene shows them.
  function pollMotion(){var sc=scene();if(motionBusy||!sc||st.data!=='live')return;var n=W.needsMotion(sc,null);if(!n.path&&!n.input){st.motion=null;return;}motionBusy=true;request('overlay-motion?'+(n.path?'path=1':'')+(n.path&&n.input?'&':'')+(n.input?'input=1':''),null,function(ok,d){motionBusy=false;st.motion=ok?d:null;});}
  function tick(){if(tickTimer!==null)root.clearTimeout(tickTimer);tickTimer=null;if(!container||st.tab!=='editor')return;if(!drag)preview();pollMotion();tickTimer=root.setTimeout(tick,st.data==='sample'?100:120);}
  // ---- preview canvas ----
  var view={stage:null,layer:null,handles:null,scale:0.5,width:960,guides:null};
  function preview(){
    var sc=scene();if(!view.layer||!sc)return;var now=new Date().getTime(),d=data();
    W.render(view.layer,sc,d,{all:true,hidden:true,now:now,context:st.ctx,motion:st.data==='sample'?W.sampleMotion(now):st.motion||null,
      decorate:function(v,w){if(!w.visible||!M.shownIn(w,st.ctx))v.attrs.style.opacity=String(Math.min(w.opacity,0.28));}});
    placeHandles();
  }
  function heightOf(w){var nodes=view.layer?(view.layer.childNodes||view.layer.children):[];for(var i=0;i<nodes.length;i++)if(nodes[i].amKey==='w:'+w.id+':'+w.type)return nodes[i].offsetHeight||w.h;return w.h;}
  function placeHandles(){
    var sc=scene();if(!view.handles||!sc)return;var kids=view.handles.childNodes||view.handles.children;
    for(var i=0;i<kids.length;i++){var hnd=kids[i],w=M.findWidget(sc,hnd.amId);if(!w)continue;var hh=M.entry(w.type).auto?heightOf(w):w.h;
      hnd.style.left=w.x+'px';hnd.style.top=w.y+'px';hnd.style.width=w.w+'px';hnd.style.height=hh+'px';hnd.className='ove-handle'+(w.id===st.selected?' selected':'')+(w.visible?'':' off');}
  }
  function stagePoint(e){var r=view.stage.getBoundingClientRect?view.stage.getBoundingClientRect():{left:0,top:0};return {x:(e.clientX-r.left)/view.scale,y:(e.clientY-r.top)/view.scale};}
  function startDrag(e,w,mode){
    if(e.button!==undefined&&e.button!==0)return;if(e.preventDefault)e.preventDefault();if(e.stopPropagation)e.stopPropagation();
    if(st.selected!==w.id){st.selected=w.id;render();}
    var p=stagePoint(e);drag={id:w.id,mode:mode,start:p,x:w.x,y:w.y,w:w.w,h:M.entry(w.type).auto?heightOf(w):w.h,moved:false};listen();
  }
  function onMove(e){
    if(!drag)return;var sc=scene(),w=M.findWidget(sc,drag.id);if(!w){drag=null;return;}var p=stagePoint(e),dx=p.x-drag.start.x,dy=p.y-drag.start.y;if(Math.abs(dx)+Math.abs(dy)>2)drag.moved=true;
    var others=[];for(var i=0;i<sc.widgets.length;i++){var o=sc.widgets[i];if(o.id!==w.id&&o.visible)others.push({x:o.x,y:o.y,w:o.w,h:M.entry(o.type).auto?heightOf(o):o.h});}
    var free=e.altKey,guides=[];
    if(drag.mode==='move'){var r={x:drag.x+dx,y:drag.y+dy,w:drag.w,h:drag.h};if(!free){var s=M.snap(r,others,10/Math.max(0.3,view.scale*2));r=s;guides=s.guides;}w.x=Math.round(M.clamp(r.x,0,M.BASE_W-20));w.y=Math.round(M.clamp(r.y,0,M.BASE_H-20));}
    else{var nw=Math.max(80,drag.w+dx),nh=Math.max(48,drag.h+dy);if(!free){nw=Math.round(nw/8)*8;nh=Math.round(nh/8)*8;}w.w=Math.round(M.clamp(nw,60,M.BASE_W-w.x));if(!M.entry(w.type).auto)w.h=Math.round(M.clamp(nh,40,M.BASE_H-w.y));}
    showGuides(guides);preview();syncFields(w);
  }
  function onUp(){if(!drag)return;var moved=drag.moved;drag=null;showGuides([]);if(moved)changed(false);}
  function listen(){if(listening||!root.document.addEventListener)return;listening=true;root.document.addEventListener('mousemove',onMove);root.document.addEventListener('mouseup',onUp);}
  function unlisten(){if(!listening||!root.document.removeEventListener)return;listening=false;root.document.removeEventListener('mousemove',onMove);root.document.removeEventListener('mouseup',onUp);}
  function showGuides(list){if(!view.guides)return;view.guides.textContent='';for(var i=0;i<list.length;i++){var g=el('div','ove-guide '+list[i].axis);if(list[i].axis==='x'){g.style.left=list[i].at+'px';}else g.style.top=list[i].at+'px';view.guides.appendChild(g);}}
  function syncFields(w){var keys=['x','y','w','h'];for(var i=0;i<keys.length;i++){var n=root.document.getElementById('ove-field-'+keys[i]);if(n&&n!==root.document.activeElement)n.value=String(w[keys[i]]);}}
  function nudge(e){
    var w=selectedWidget();if(!w||st.tab!=='editor')return;var code=e.keyCode,step=e.shiftKey?10:1,tag=e.target&&e.target.tagName?e.target.tagName.toLowerCase():'';if(tag==='input'||tag==='select'||tag==='textarea')return;
    if(code===37)w.x=Math.max(0,w.x-step);else if(code===39)w.x=Math.min(M.BASE_W-20,w.x+step);else if(code===38)w.y=Math.max(0,w.y-step);else if(code===40)w.y=Math.min(M.BASE_H-20,w.y+step);
    else if(code===46){removeWidget(w.id);if(e.preventDefault)e.preventDefault();return;}else return;
    if(e.preventDefault)e.preventDefault();syncFields(w);changed(false);
  }
  // ---- scene and widget actions ----
  function addWidget(type){
    var sc=scene();if(!sc)return;if(sc.widgets.length>=M.MAX_WIDGETS){setStatus('A scene holds up to '+M.MAX_WIDGETS+' widgets.',true);return;}
    var entry=M.entry(type),n=sc.widgets.length,x=Math.round(M.clamp(160+n*24,0,M.BASE_W-entry.w)),y=Math.round(M.clamp(120+n*24,0,M.BASE_H-entry.h));
    var w=M.widget(type,M.freshId(sc.widgets,'w'),x,y);sc.widgets.push(w);st.selected=w.id;changed();
  }
  function removeWidget(id){var sc=scene();sc.widgets=sc.widgets.filter(function(w){return w.id!==id;});if(st.selected===id)st.selected='';changed();}
  function duplicateWidget(w){var sc=scene();if(sc.widgets.length>=M.MAX_WIDGETS)return;var c=M.copy(w);c.id=M.freshId(sc.widgets,'w');c.x=Math.min(M.BASE_W-40,c.x+24);c.y=Math.min(M.BASE_H-40,c.y+24);sc.widgets.push(M.cleanWidget(c));st.selected=c.id;changed();}
  function reorder(w,front){var sc=scene(),rest=sc.widgets.filter(function(x){return x!==w;});sc.widgets=front?rest.concat([w]):[w].concat(rest);changed();}
  function addScene(template){
    if(st.store.scenes.length>=M.MAX_SCENES){setStatus('You can keep up to '+M.MAX_SCENES+' scenes.',true);return;}
    var id=M.freshId(st.store.scenes,'scene'),s=template==='blank'?M.cleanScene({id:id,name:'New scene',theme:{preset:'mint'},widgets:[]}):M.fromTemplate(template,id);
    st.store.scenes.push(s);st.scene=id;st.selected='';st.panel='';changed();
  }
  function duplicateScene(){var sc=scene();if(!sc||st.store.scenes.length>=M.MAX_SCENES)return;var c=M.copy(sc);c.id=M.freshId(st.store.scenes,'scene');c.name=(sc.name+' copy').slice(0,48);st.store.scenes.push(M.cleanScene(c));st.scene=c.id;changed();}
  function deleteScene(){var sc=scene();if(!sc||st.store.scenes.length<2)return;st.store.scenes=st.store.scenes.filter(function(s){return s!==sc;});var first=st.store.scenes[0].id;if(st.store.obsScene===sc.id)st.store.obsScene=first;if(st.store.gameScene===sc.id)st.store.gameScene=first;st.scene=first;st.selected='';st.panel='';changed();}
  // ---- page ----
  function focusedId(){var a=root.document.activeElement;return a&&a.id&&a.id.indexOf('ove-')===0?a.id:'';}
  function render(){if(!container)return;var focus=focusedId();draw();if(focus&&root.document.getElementById){var n=root.document.getElementById(focus);if(n&&n.focus&&!n.disabled)n.focus();}}
  function draw(){
    container.textContent='';view.layer=null;view.handles=null;view.stage=null;view.guides=null;
    if(!st.store){var p=add(container,el('div','panel ove-loading'));if(st.failed){var f=add(p,el('div','empty'));add(f,el('h3','','Could not load your overlays'));add(f,el('p','','AimMod couldn’t reach its local service. Your overlays keep their last saved layout.'));var a=add(f,el('div','actions center'));a.appendChild(button('Try again',function(){st.failed=false;render();load();},'primary'));}else add(p,el('p','subtle','Loading overlays…'));return;}
    var head=add(container,el('div','ove-top'));var tabs=add(head,el('div','ove-tabs'));tabs.setAttribute('role','tablist');tabs.setAttribute('aria-label','Overlay pages');
    for(var i=0;i<TABS.length;i++)(function(t){var b=button(t[1],function(){st.tab=t[0];st.panel='';render();tick();},'ove-tab'+(st.tab===t[0]?' active':''),'ove-tab-'+t[0]);b.setAttribute('role','tab');b.setAttribute('aria-selected',String(st.tab===t[0]));tabs.appendChild(b);})(TABS[i]);
    var status=add(head,el('div','ove-status'+(st.warn?' warn':'')+(st.status?'':' idle'),st.status));status.id='ove-status';status.setAttribute('role','status');
    if(st.tab==='editor')editor();else if(st.tab==='outputs')outputs();else if(st.tab==='profile')profile();else opponent();
  }
  function editor(){
    var sc=scene();
    var bar=add(container,el('div','ove-bar'));var picker=add(bar,el('div','ove-scenes'));
    for(var i=0;i<st.store.scenes.length;i++)(function(s){var b=button(s.name,function(){st.scene=s.id;st.selected='';st.panel='';st.rename=false;render();},'ove-scene'+(s.id===st.scene?' active':''),'ove-scene-'+s.id);
      var tags=[];if(st.store.obsScene===s.id)tags.push('OBS');if(st.store.gameScene===s.id)tags.push('Game');if(tags.length)b.appendChild(el('span','ove-scene-tag',tags.join(' · ')));picker.appendChild(b);})(st.store.scenes[i]);
    picker.appendChild(button('New scene',function(){st.panel=st.panel==='new'?'':'new';render();},'quiet','ove-new-scene'));
    var right=add(bar,el('div','ove-bar-right'));
    right.appendChild(segmented([['sample','Sample data'],['live','Live data']],st.data,function(v){st.data=v;render();pollFeed();},'Preview data','ove-data'));
    right.appendChild(segmented(CONTEXTS,st.ctx,function(v){st.ctx=v;render();},'Preview context','ove-ctx'));
    right.appendChild(button('Share',function(){st.panel=st.panel==='share'?'':'share';st.importError='';render();},st.panel==='share'?'primary compact':'compact','ove-share'));
    if(st.panel==='new')templates();if(st.panel==='share')share();
    var body=add(container,el('div','ove-body'));
    library(add(body,el('div','ove-library')));
    var center=add(body,el('div','ove-center'));stage(center);
    var side=add(body,el('div','ove-inspector'));if(selectedWidget())inspector(side,selectedWidget());else sceneInspector(side,sc);
  }
  function templates(){
    var box=add(container,el('div','panel ove-drawer'));add(box,el('h3','','Start a new scene'));add(box,el('p','subtle','Pick a starting point. Every widget stays editable.'));
    var row=add(box,el('div','ove-templates'));
    var list=M.TEMPLATES.concat([{key:'blank',name:'Blank',desc:'An empty canvas.'}]);
    for(var i=0;i<list.length;i++)(function(t){var b=button('',function(){addScene(t.key);},'ove-template','ove-template-'+t.key);b.textContent='';add(b,el('span','ove-template-name',t.name));add(b,el('span','ove-template-desc',t.desc));row.appendChild(b);})(list[i]);
  }
  function share(){
    var sc=scene(),box=add(container,el('div','panel ove-drawer'));add(box,el('h3','','Share this scene'));
    add(box,el('p','subtle','The code holds the layout, widgets and theme of “'+sc.name+'”. Your peripherals and stats are not included.'));
    var code=el('input','ove-code');code.type='text';code.readOnly=true;code.value=M.encodeShare(sc);code.id='ove-export';code.setAttribute('aria-label','Share code');add(box,code);
    var a=add(box,el('div','actions'));var fb=el('span','subtle ove-feedback','');a.appendChild(button('Copy code',function(){code.focus();code.select();var ok=false;try{ok=root.document.execCommand('copy');}catch(e){}fb.textContent=ok?'Copied.':'Code selected. Press Ctrl+C to copy.';},'primary compact'));a.appendChild(fb);
    add(box,el('h3','ove-sub','Import a scene'));var input=el('input','ove-code');input.type='text';input.id='ove-import';input.value=st.importText;input.oninput=function(){st.importText=input.value;};add(box,field(input,'Paste a share code (AIMMOD-OVERLAY-1:…)'));
    var b=add(box,el('div','actions'));b.appendChild(button('Import as a new scene',function(){var s=M.decodeShare(st.importText);if(!s){st.importError='That code isn’t a valid AimMod overlay scene.';render();return;}if(st.store.scenes.length>=M.MAX_SCENES){st.importError='You can keep up to '+M.MAX_SCENES+' scenes. Delete one first.';render();return;}s.id=M.freshId(st.store.scenes,'scene');st.store.scenes.push(s);st.scene=s.id;st.importText='';st.panel='';st.importError='';changed();},'compact'));
    if(st.importError)add(box,el('p','ove-error',st.importError));
  }
  function library(box){
    add(box,el('div','ove-col-title','Add a widget'));var groups=[],seen={};for(var i=0;i<M.CATALOG.length;i++){var g=M.CATALOG[i].group;if(!seen[g]){seen[g]=[];groups.push(g);}seen[g].push(M.CATALOG[i]);}
    for(var j=0;j<groups.length;j++){add(box,el('div','ove-group',groups[j]));for(var k=0;k<seen[groups[j]].length;k++)(function(entry){var b=button('',function(){addWidget(entry.type);},'ove-lib','ove-add-'+entry.type);add(b,el('span','ove-lib-name',entry.name));add(b,el('span','ove-lib-add','+'));b.setAttribute('aria-label','Add '+entry.name);b.title=entry.desc;box.appendChild(b);})(seen[groups[j]][k]);}
  }
  function stage(center){
    var sc=scene(),avail=(container.offsetWidth||1500)-560-40;var width=Math.max(480,Math.min(1280,avail));view.scale=width/M.BASE_W;view.width=width;
    var frame=add(center,el('div','ove-stage-frame'));frame.style.width=width+'px';frame.style.height=Math.round(width*9/16)+'px';
    var stg=add(frame,el('div','ove-stage'));stg.style.width=width+'px';stg.style.height=Math.round(width*9/16)+'px';view.stage=stg;
    var world=add(stg,el('div','ove-world'));world.style.transform='scale('+view.scale.toFixed(5)+')';
    add(world,el('div','ove-sky'));add(world,el('div','ove-floor'));
    view.layer=add(world,el('div','ove-layer'));view.guides=add(world,el('div','ove-guides'));view.handles=add(world,el('div','ove-handles'));
    stg.onmousedown=function(e){if(e.target===stg||e.target===world||(e.target&&e.target.className&&/ove-(sky|floor|layer)/.test(e.target.className))){if(st.selected){st.selected='';render();}}};
    for(var i=0;i<sc.widgets.length;i++)(function(w){var hnd=el('div','ove-handle');hnd.amId=w.id;hnd.setAttribute('aria-label',M.entry(w.type).name);hnd.onmousedown=function(e){startDrag(e,w,'move');};
      var grip=add(hnd,el('div','ove-grip'+(M.entry(w.type).auto?' wide':'')));grip.onmousedown=function(e){startDrag(e,w,'resize');};
      add(hnd,el('div','ove-handle-label',M.entry(w.type).name));view.handles.appendChild(hnd);})(sc.widgets[i]);
    var foot=add(center,el('div','ove-stage-foot'));
    add(foot,el('span','',sc.widgets.length?'Drag to move, drag the corner to resize. Arrow keys nudge (Shift: 10 px). Hold Alt to place freely.':'Add widgets from the list on the left.'));
    add(foot,el('span','ove-stage-size','1920 × 1080'));
    preview();
  }
  // ---- inspector ----
  function section(parent,title){var s=add(parent,el('div','ove-section'));if(title)add(s,el('div','ove-section-title',title));return s;}
  function line(parent,label,control){var r=add(parent,el('div','ove-line'));add(r,el('span','ove-line-label',label));var c=add(r,el('div','ove-line-control'));c.appendChild(control);return r;}
  function stepper(value,min,max,step,digits,unit,fn,id){
    var box=el('div','ove-stepper'),out=el('span','ove-step-value',(digits?value.toFixed(digits):String(Math.round(value)))+(unit||''));if(id)out.id=id;
    function set(v){v=Math.round(M.clamp(v,min,max)/step)*step;v=Math.round(v*1000)/1000;if(v!==value)fn(v);}
    box.appendChild(button('–',function(){set(value-step);},'compact ove-step',id?id+'-down':''));box.appendChild(out);box.appendChild(button('+',function(){set(value+step);},'compact ove-step',id?id+'-up':''));return box;
  }
  function numberField(key,value,min,max,fn){var i=el('input','ove-num');i.type='number';i.id='ove-field-'+key;i.min=String(min);i.max=String(max);i.step='1';i.value=String(value);i.setAttribute('aria-label',key.toUpperCase());i.onchange=function(){var v=Number(i.value);if(!isFinite(v)){i.value=String(value);return;}fn(Math.round(M.clamp(v,min,max)));};return i;}
  function swatches(current,list,fn,allowTheme,label,id){
    var box=el('div','ove-swatches');box.setAttribute('aria-label',label);
    if(allowTheme){var t=button('Theme',function(){fn('');},'compact ove-swatch-theme'+(current===''?' primary':''),id?id+'-theme':'');box.appendChild(t);}
    for(var i=0;i<list.length;i++)(function(c){var b=button('',function(){fn(c);},'ove-swatch'+(current===c?' on':''),id?id+'-'+c.slice(1):'');b.style.background=c;b.setAttribute('aria-label',c);box.appendChild(b);})(list[i]);
    var hex=el('input','ove-hex');hex.type='text';hex.maxLength=7;hex.value=current||'';if(id)hex.id=id+'-hex';hex.setAttribute('aria-label',label+' hex');hex.onchange=function(){var v=hex.value.replace(/^\s+|\s+$/g,'');if(v&&v.charAt(0)!=='#')v='#'+v;if(/^#[0-9a-fA-F]{6}$/.test(v))fn(v.toLowerCase());else hex.value=current||'';};box.appendChild(field(hex,'#hex'));
    return box;
  }
  function inspector(side,w){
    var entry=M.entry(w.type),sc=scene();
    var top=add(side,el('div','ove-inspect-head'));var titles=add(top,el('div','ove-inspect-titles'));add(titles,el('div','ove-col-title',entry.name));add(titles,el('div','ove-inspect-desc',entry.desc));
    top.appendChild(button('Done',function(){st.selected='';render();},'compact','ove-done'));
    var acts=add(side,el('div','actions ove-inspect-actions'));acts.appendChild(button('Duplicate',function(){duplicateWidget(w);},'compact'));acts.appendChild(button('To front',function(){reorder(w,true);},'compact'));acts.appendChild(button('To back',function(){reorder(w,false);},'compact'));acts.appendChild(button('Remove',function(){removeWidget(w.id);},'compact danger','ove-remove'));
    var show=section(side,'Visibility');line(show,'Visible',toggle(w.visible,'Visible',function(v){w.visible=v;changed();},'ove-visible'));
    var ctx=el('div','ove-contexts');for(var i=0;i<CONTEXTS.length;i++)(function(c){var on=w.show[c[0]];var b=button(c[1],function(){w.show[c[0]]=!on;changed();},'compact'+(on?' primary':''),'ove-show-'+c[0]);b.setAttribute('aria-pressed',String(on));ctx.appendChild(b);})(CONTEXTS[i]);
    line(show,'Show in',ctx);
    var size=section(side,'Position and size');var grid=add(size,el('div','ove-xywh'));
    var dims=[['x',w.x,0,M.BASE_W-20],['y',w.y,0,M.BASE_H-20],['w',w.w,60,M.BASE_W]];if(!entry.auto)dims.push(['h',w.h,40,M.BASE_H]);
    for(var d=0;d<dims.length;d++)(function(f){var cell=add(grid,el('label','ove-xy'));add(cell,el('span','',f[0].toUpperCase()));cell.appendChild(numberField(f[0],f[1],f[2],f[3],function(v){w[f[0]]=v;changed();}));})(dims[d]);
    line(size,'Text size',stepper(w.font,0.6,2.5,0.1,1,'×',function(v){w.font=v;changed();},'ove-font'));
    line(size,'Opacity',stepper(Math.round(w.opacity*100),10,100,10,0,'%',function(v){w.opacity=v/100;changed();},'ove-opacity'));
    var style=section(side,'Style');
    line(style,'Density',segmented([['compact','Compact'],['expanded','Expanded']],w.variant,function(v){w.variant=v;changed();},'Density','ove-variant'));
    if(entry.layout)line(style,'Layout',segmented([['horizontal','Row'],['vertical','List']],w.layout,function(v){w.layout=v;changed();},'Layout','ove-layout'));
    line(style,'Labels',toggle(w.labels,'Labels',function(v){w.labels=v;changed();},'ove-labels'));
    var accent=add(style,el('div','ove-block'));add(accent,el('div','ove-line-label','Accent colour'));accent.appendChild(swatches(w.accent,M.ACCENTS,function(v){w.accent=v;changed();},true,'Accent colour','ove-accent'));
    var bg=add(style,el('div','ove-block'));add(bg,el('div','ove-line-label','Background'));
    bg.appendChild(segmented([['','Theme'],['none','None'],['custom','Colour']],w.background===''||w.background==='none'?w.background:'custom',function(v){w.background=v==='custom'?(w.background&&w.background!=='none'?w.background:'#000000'):v;changed();},'Background','ove-bg'));
    if(w.background&&w.background!=='none')bg.appendChild(swatches(w.background,['#000000','#0b1110','#101820','#1d1d1d','#ffffff'],function(v){w.background=v||'';changed();},false,'Background colour','ove-bgc'));
    if(w.background!=='none')line(style,'Panel opacity',stepper(w.panel<0?Math.round(M.resolveTheme(sc).alpha*100):Math.round(w.panel*100),0,100,5,0,'%',function(v){w.panel=v/100;changed();},'ove-panel'));
    if(entry.opts.length){var content=section(side,w.type==='settings'?'Show on the card':'Content');var group='';
      for(var k=0;k<entry.opts.length;k++)(function(o){
        if(o.group&&o.group!==group){group=o.group;add(content,el('div','ove-subtitle',o.group));}
        var v=w.opts[o.key];
        if(o.kind==='bool')line(content,o.label,toggle(v,o.label,function(x){w.opts[o.key]=x;changed();},'ove-opt-'+o.key));
        else if(o.kind==='number')line(content,o.label,stepper(v,o.min,o.max,o.step,o.step<1?1:0,'',function(x){w.opts[o.key]=x;changed();},'ove-opt-'+o.key));
        else if(o.kind==='choice')line(content,o.label,segmented(o.choices,v,function(x){w.opts[o.key]=x;changed();},o.label,'ove-opt-'+o.key));
        else if(o.kind==='color'){var b=add(content,el('div','ove-block'));add(b,el('div','ove-line-label',o.label));b.appendChild(swatches(v,M.ACCENTS,function(x){w.opts[o.key]=x;changed();},true,o.label,'ove-opt-'+o.key));}
        else{var b2=add(content,el('div','ove-block'));add(b2,el('div','ove-line-label',o.label));var t=el('input','ove-text');t.type='text';t.id='ove-opt-'+o.key;t.maxLength=o.key==='url'?512:160;t.value=v||'';t.setAttribute('aria-label',o.label);
          t.onchange=function(){w.opts[o.key]=t.value;var c=M.cleanWidget(w);w.opts[o.key]=c.opts[o.key];if(o.key==='url'&&t.value&&!c.opts.url)setStatus('Use an https:// image address.',true);changed();};b2.appendChild(t);}
      })(entry.opts[k]);}
    if(w.type==='peripherals'){var gear=section(side,'Your gear');add(gear,el('p','subtle ove-hint','Edit your peripherals on the Profile tab.'));gear.appendChild(button('Open Profile',function(){st.tab='profile';render();},'compact'));}
    var out=section(side,'This widget alone in OBS');var url=sceneUrl(sc.id,w.id);
    if(url){add(out,el('p','subtle ove-hint','A browser source with just this widget, sized '+w.w+' × '+(entry.auto?heightOf(w):w.h)+'.'));copyRow(out,url,'ove-widget-url');}else add(out,el('p','subtle ove-hint','The OBS source is unavailable. Restart AimMod to retry.'));
  }
  function copyRow(parent,url,id){var row=add(parent,el('div','ove-copy'));var i=el('input','ove-url');i.type='text';i.readOnly=true;i.value=url;i.id=id;i.setAttribute('aria-label','Browser source URL');row.appendChild(i);
    var fb=el('span','ove-copied','');row.appendChild(button('Copy URL',function(){i.focus();i.select();var ok=false;try{ok=root.document.execCommand('copy');}catch(e){}fb.textContent=ok?'Copied':'Press Ctrl+C';},'compact primary',id+'-copy'));row.appendChild(fb);return row;}
  function sceneInspector(side,sc){
    add(side,el('div','ove-col-title','Scene'));
    var name=section(side,'');var input=el('input','ove-text');input.type='text';input.maxLength=48;input.value=sc.name;input.id='ove-scene-name';input.setAttribute('aria-label','Scene name');input.onchange=function(){var v=input.value.replace(/^\s+|\s+$/g,'');if(v){sc.name=v.slice(0,48);changed();}else input.value=sc.name;};
    var nb=add(name,el('div','ove-block'));add(nb,el('div','ove-line-label','Name'));nb.appendChild(input);
    var use=section(side,'Use this scene for');line(use,'OBS browser source',toggle(st.store.obsScene===sc.id,'Use for OBS',function(){st.store.obsScene=sc.id;changed();},'ove-use-obs'));
    line(use,'In-game HUD',toggle(st.store.gameScene===sc.id,'Use in game',function(){st.store.gameScene=sc.id;changed();},'ove-use-game'));
    var th=section(side,'Theme');var list=add(th,el('div','ove-themes'));
    for(var i=0;i<M.THEMES.length;i++)(function(t){var b=button('',function(){sc.theme=M.cleanTheme({preset:t.key});changed();},'ove-theme'+(sc.theme.preset===t.key?' on':''),'ove-theme-'+t.key);var chip=add(b,el('span','ove-theme-chip'));chip.style.background=t.surface;var dot=add(chip,el('span','ove-theme-dot'));dot.style.background=t.accent;add(b,el('span','ove-theme-name',t.name));list.appendChild(b);})(M.THEMES[i]);
    var ac=add(th,el('div','ove-block'));add(ac,el('div','ove-line-label','Accent colour'));ac.appendChild(swatches(sc.theme.accent,M.ACCENTS,function(v){sc.theme.accent=v||M.theme(sc.theme.preset).accent;changed();},false,'Theme accent','ove-theme-accent'));
    line(th,'Panel opacity',stepper(Math.round(sc.theme.alpha*100),0,100,5,0,'%',function(v){sc.theme.alpha=v/100;changed();},'ove-theme-alpha'));
    line(th,'Corner radius',stepper(sc.theme.radius,0,24,2,0,' px',function(v){sc.theme.radius=v;changed();},'ove-theme-radius'));
    line(th,'Borders',toggle(sc.theme.borders,'Borders',function(v){sc.theme.borders=v;changed();},'ove-theme-borders'));
    var layers=section(side,'Widgets in this scene');if(!sc.widgets.length)add(layers,el('p','subtle ove-hint','None yet.'));
    for(var j=sc.widgets.length-1;j>=0;j--)(function(w){var r=add(layers,el('div','ove-layer-row'));var b=button(M.entry(w.type).name,function(){st.selected=w.id;render();},'ove-layer-name','ove-layer-'+w.id);r.appendChild(b);r.appendChild(toggle(w.visible,'Show '+M.entry(w.type).name,function(v){w.visible=v;changed();}));})(sc.widgets[j]);
    var manage=section(side,'Manage');var a=add(manage,el('div','actions'));a.appendChild(button('Duplicate scene',function(){duplicateScene();},'compact'));
    if(st.store.scenes.length>1){var del=button('Delete scene',function(){del.textContent='Confirm delete';del.onclick=function(){deleteScene();};},'compact danger','ove-delete-scene');a.appendChild(del);}
  }
  // ---- outputs ----
  function outputs(){
    var wrap=add(container,el('div','ove-cols'));var left=add(wrap,el('div','ove-col-main')),right=add(wrap,el('div','ove-col-side'));
    var obs=add(left,el('div','panel ove-card'));var head=add(obs,el('div','ove-card-head'));var ht=add(head,el('div',''));add(ht,el('h2','','OBS browser sources'));add(ht,el('p','subtle','Every scene and every widget has its own transparent browser source.'));
    head.appendChild(toggle(!!st.settings.obsEnabled,'OBS browser sources',function(v){saveLegacy({obsEnabled:v});},'ove-obs-enabled'));
    if(!st.settings.obsEnabled)add(obs,el('p','ove-note','Browser sources are off. Turn them on to show your overlays in OBS.'));
    var steps=add(obs,el('ol','ove-steps'));var r=st.resolution.split('x');
    ['In OBS, click + under Sources and choose Browser.','Paste a URL from below into URL. Leave Local file unticked.','Set Width to '+r[0]+' and Height to '+r[1]+' (the size of your canvas).','Clear Custom CSS. The page background is already transparent.','Optional: tick Shutdown source when not visible to save CPU while hidden.'].forEach(function(t){add(steps,el('li','',t));});
    line(obs,'Canvas size',segmented([['1920x1080','1080p'],['2560x1440','1440p'],['1280x720','720p']],st.resolution,function(v){st.resolution=v;render();},'Canvas size','ove-res'));
    if(!obsBase())add(obs,el('p','ove-note warn','The browser source is unavailable. Restart AimMod to retry.'));
    else{
      for(var i=0;i<st.store.scenes.length;i++)(function(s){var row=add(obs,el('div','ove-source'));var t=add(row,el('div','ove-source-head'));add(t,el('h3','',s.name));if(st.store.obsScene===s.id)add(t,el('span','ove-scene-tag on','Default'));
        copyRow(row,sceneUrl(s.id),'ove-url-'+s.id);
        var det=add(row,el('div','ove-widget-urls'));for(var j=0;j<s.widgets.length;j++)(function(w){var e=M.entry(w.type),wr=add(det,el('div','ove-widget-url'));add(wr,el('span','ove-widget-name',e.name));add(wr,el('span','subtle',w.w+' × '+(e.auto?'auto':w.h)));var u=sceneUrl(s.id,w.id);var fb=el('span','ove-copied','');var hidden=el('input','ove-url small');hidden.type='text';hidden.readOnly=true;hidden.value=u;hidden.setAttribute('aria-label',e.name+' URL');wr.appendChild(hidden);wr.appendChild(button('Copy',function(){hidden.focus();hidden.select();var ok=false;try{ok=root.document.execCommand('copy');}catch(x){}fb.textContent=ok?'Copied':'Ctrl+C';},'compact'));wr.appendChild(fb);})(s.widgets[j]);
      })(st.store.scenes[i]);
      var legacy=add(obs,el('div','ove-source'));add(legacy,el('h3','','Earlier links'));add(legacy,el('p','subtle ove-hint','These keep working. The live HUD link now shows your default OBS scene.'));
      if(st.setup.obsUrl)copyRow(legacy,st.setup.obsUrl,'ove-url-legacy');if(st.setup.boardUrl)copyRow(legacy,st.setup.boardUrl,'ove-url-board');if(st.setup.tournamentUrl)copyRow(legacy,st.setup.tournamentUrl,'ove-url-tournament');
      add(obs,el('p','subtle ove-hint','Keep these URLs private. They show your live stats from this PC while AimMod runs.'));
    }
    var game=add(right,el('div','panel ove-card'));var gh=add(game,el('div','ove-card-head'));var gt=add(gh,el('div',''));add(gt,el('h2','','In game'));add(gt,el('p','subtle','Draws a scene over KovaaK’s. It never takes your mouse or keyboard.'));
    gh.appendChild(toggle(!!st.settings.gameEnabled,'In-game overlay',function(v){saveLegacy({gameEnabled:v});},'ove-game-enabled'));
    var pick=el('select','ove-select');pick.id='ove-game-scene';pick.setAttribute('aria-label','In-game scene');for(var k=0;k<st.store.scenes.length;k++){var o=el('option','',st.store.scenes[k].name);o.value=st.store.scenes[k].id;pick.appendChild(o);}pick.value=st.store.gameScene;pick.onchange=function(){st.store.gameScene=pick.value;changed();};
    var pb=add(game,el('div','ove-block'));add(pb,el('div','ove-line-label','Scene shown in game'));pb.appendChild(pick);
    add(game,el('p','subtle ove-hint','Each widget chooses where it shows: Menu, Scenario or Match (Editor, Visibility). In game, AimMod draws the overlay while a scenario or match runs; widgets set to Menu show on OBS.'));
    add(game,el('p','subtle ove-hint','Overlays are display only. They never change your game, inputs or ranked scores.'));
  }
  // ---- profile ----
  var GEAR=['Mouse','Mousepad','Keyboard','IEMs','Headset','Monitor','Mouse skates','Grips','Chair'];
  function profile(){
    var wrap=add(container,el('div','ove-cols'));var left=add(wrap,el('div','ove-col-main')),right=add(wrap,el('div','ove-col-side'));
    var card=add(left,el('div','panel ove-card'));add(card,el('h2','','Peripherals'));add(card,el('p','subtle','Shown on the Peripherals widget. Only what you enter here, nothing is detected.'));
    var list=st.store.profile.peripherals;
    for(var i=0;i<list.length;i++)(function(p,index){var row=add(card,el('div','ove-gear-row'));
      var l=el('input','ove-text ove-gear-label');l.type='text';l.maxLength=32;l.value=p.label;l.id='ove-gear-label-'+index;l.setAttribute('aria-label','Item');l.onchange=function(){p.label=l.value.replace(/^\s+|\s+$/g,'').slice(0,32);changed(false);};
      var v=el('input','ove-text ove-gear-value');v.type='text';v.maxLength=80;v.value=p.value;v.id='ove-gear-value-'+index;v.setAttribute('aria-label',(p.label||'Item')+' model');v.onchange=function(){p.value=v.value.replace(/^\s+|\s+$/g,'').slice(0,80);changed(false);};
      row.appendChild(field(l,'Item'));row.appendChild(field(v,'Model'));
      row.appendChild(button('Up',function(){if(index>0){list.splice(index,1);list.splice(index-1,0,p);changed();}},'compact'));
      row.appendChild(button('Remove',function(){list.splice(index,1);changed();},'compact danger','ove-gear-remove-'+index));})(list[i],i);
    if(list.length<M.MAX_PERIPHERALS){var add1=add(card,el('div','ove-gear-add'));add(add1,el('span','ove-line-label','Add'));
      for(var g=0;g<GEAR.length;g++)(function(name){var used=list.some(function(p){return p.label===name;});if(used&&name!=='Monitor')return;add1.appendChild(button(name,function(){list.push({label:name,value:''});changed();},'compact','ove-gear-add-'+name.replace(/\s+/g,'-').toLowerCase()));})(GEAR[g]);
      add1.appendChild(button('Other',function(){list.push({label:'Other',value:''});changed();},'compact quiet'));}
    var k=add(right,el('div','panel ove-card'));add(k,el('h2','','KovaaK’s settings'));add(k,el('p','subtle','Read from KovaaK’s and shown on the Settings card. Change them in the game.'));
    var K=st.feed&&st.feed.kovaaks;if(!K||!K.available){add(k,el('p','ove-note','KovaaK’s settings aren’t available yet. They appear once the game has saved its settings.'));}
    else{var rows=[['DPI',W.format.number(K.dpi)],['Sensitivity',K.cm360!==null&&K.cm360!==undefined?W.format.number(K.cm360,2)+' cm/360':W.format.number(K.sens,2)+' '+(K.sensScale||'')],['FOV',W.format.number(K.fov)+'°'+(K.fovScale?' · '+K.fovScale:'')],['Crosshair',(K.crosshair?K.crosshair.replace(/\.png$/i,''):'—')+(K.crosshairScale?' · '+W.format.short(K.crosshairScale)+'×':'')],['Theme',K.theme||'—'],['Hit sounds',(K.hitSounds||[]).join(' / ')||'—']];
      for(var r=0;r<rows.length;r++){var kr=add(k,el('div','ove-kv'));add(kr,el('span','ove-line-label',rows[r][0]));add(kr,el('span','ove-kv-value',rows[r][1]));}}
  }
  // ---- VS opponent (unchanged behaviour) ----
  function opponent(){
    var card=add(container,el('div','panel ove-card'));add(card,el('h2','','VS opponent'));add(card,el('p','subtle','PB pace compares your live pace with this score. Personal best is the default.'));
    var chooser=el('select','ove-select');chooser.id='ove-opponent';chooser.setAttribute('aria-label','VS opponent');var own=el('option','','Personal best');own.value='';chooser.appendChild(own);
    var o=st.opponents,selected=o&&o.selectedKey||'',found=!selected;(o&&o.rows||[]).forEach(function(row){var opt=el('option','',(row.name||'Unknown player')+' · '+W.format.number(row.score)+' · '+(row.scenario||'Unknown scenario'));opt.value=row.key;chooser.appendChild(opt);if(row.key===selected)found=true;});
    if(!found){var missing=el('option','','Selected opponent unavailable');missing.value=selected;chooser.appendChild(missing);}chooser.value=selected;
    chooser.onchange=function(){st.busy=true;chooser.disabled=true;request('overlay-opponents',{key:chooser.value},function(ok,d){st.busy=false;if(ok)st.opponents=d;render();setStatus(ok?'Opponent saved.':'Could not select this opponent. Refresh and try again.',!ok);});};
    card.appendChild(chooser);add(card,el('p','subtle ove-hint','Open a scenario’s leaderboard in game to pick its players here.'));
    var a=add(card,el('div','actions'));a.appendChild(button('Refresh opponents',function(){request('overlay-opponents',null,function(ok,d){if(ok)st.opponents=d;render();if(!ok)setStatus('Could not refresh opponents.',true);});}));
  }
  function load(){
    request('overlay-scenes',null,function(ok,d){if(ok&&d&&d.scenes){st.store=M.normalize(d);if(!M.find(st.store,st.scene))st.scene=st.store.gameScene||st.store.scenes[0].id;}else st.failed=true;render();tick();});
    request('overlay-settings',null,function(ok,d){st.settings=ok?d:{gameEnabled:false,obsEnabled:false};if(st.store)render();});
    request('overlay-setup',null,function(ok,d){if(ok)st.setup=d;if(st.store)render();});
    request('overlay-opponents',null,function(ok,d){if(ok)st.opponents=d;});
    request('overlay-feed',null,function(ok,d){if(ok)st.feed=d;});
  }
  function leave(){generation++;motionBusy=false;pending.forEach(function(x){x.abort();});pending=[];if(saveTimer!==null){root.clearTimeout(saveTimer);saveTimer=null;if(st&&st.store)save();}
    if(feedTimer!==null)root.clearTimeout(feedTimer);if(tickTimer!==null)root.clearTimeout(tickTimer);feedTimer=tickTimer=null;drag=null;unlisten();if(root.document.removeEventListener)root.document.removeEventListener('keydown',nudge);container=null;}
  root.AimModOverlayEditor={
    enter:function(target){leave();generation++;container=target;st=blank();st.settings={gameEnabled:false,obsEnabled:false};if(container){if(root.document.addEventListener)root.document.addEventListener('keydown',nudge);render();load();}},
    leave:leave,resize:function(){if(container&&st&&st.store&&!drag)render();},
    state:function(){return st;}
  };
})(window);
