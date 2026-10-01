// AimMod overlay scenes: the widget catalog, themes, templates and the scene
// store model shared by the editor (Overlays page), the in-game HUD view and
// the OBS browser sources. Pure data and helpers, no DOM. Gameface-safe ES5.
// The service validates the same shape (OverlayScenes.cs) before saving.
(function(root){
  'use strict';
  var BASE_W=1920,BASE_H=1080,MAX_SCENES=12,MAX_WIDGETS=32,MAX_OPTS=24,MAX_PERIPHERALS=12;
  var SHARE_PREFIX='AIMMOD-OVERLAY-1:';
  var LIVE={menu:false,scenario:true,match:true},ALWAYS={menu:true,scenario:true,match:true},MATCH={menu:false,scenario:false,match:true},MENU={menu:true,scenario:false,match:false};
  function o(key,label,kind,def,extra){var x={key:key,label:label,kind:kind,def:def};if(extra)for(var k in extra)x[k]=extra[k];return x;}
  function metric(key,label,def){return o('m_'+key,label,'bool',def,{group:'Metrics'});}
  // auto: height follows the content (only width resizes); otherwise both resize.
  var CATALOG=[
    {type:'live-stats',name:'Live stats',group:'Live',desc:'Score, score per minute, accuracy, kills, time to kill and time left.',w:560,h:120,auto:true,show:LIVE,layout:'horizontal',
      opts:[metric('score','Score',true),metric('spm','Score / min',true),metric('accuracy','Accuracy',true),metric('kills','Kills',true),metric('ttk','Last time to kill',false),metric('kps','Kills / sec',false),metric('time','Time left',true),metric('hits','Hits / shots',false),metric('damage','Damage',false),o('scenario','Show scenario name','bool',true)]},
    {type:'pb-pace',name:'PB pace',group:'Live',desc:'Projected finish against your personal best (or the chosen VS opponent).',w:400,h:150,auto:true,show:LIVE,
      opts:[o('bar','Progress bar','bool',true),o('projection','Projected score','bool',true),o('note','Explanation line','bool',false)]},
    {type:'session',name:'Session stats',group:'Progress',desc:'Runs, play time, personal bests and accuracy this session.',w:420,h:130,auto:true,show:ALWAYS,layout:'horizontal',
      opts:[metric('runs','Runs',true),metric('time','Play time',true),metric('pbs','New PBs',true),metric('accuracy','Average accuracy',true),metric('scenarios','Scenarios',false),metric('best','Best vs PB',false)]},
    {type:'session-graph',name:'Session graph',group:'Progress',desc:'Every run this session as a share of its personal best.',w:520,h:220,auto:false,show:ALWAYS,
      opts:[o('runs','Runs shown','number',30,{min:5,max:80,step:1}),o('pbLine','PB line','bool',true),o('fill','Area fill','bool',true)]},
    {type:'scenario',name:'Scenario info',group:'Progress',desc:'The scenario you play: personal best, attempts and recent average.',w:460,h:120,auto:true,show:ALWAYS,layout:'horizontal',
      opts:[o('pb','Personal best','bool',true),o('attempts','Attempts','bool',true),o('average','Recent average','bool',true),o('last','Last score','bool',false)]},
    {type:'rank',name:'Rank progress',group:'Progress',desc:'Your benchmark rank from AimMod Hub.',w:420,h:110,auto:true,show:ALWAYS,
      opts:[o('benchmark','Benchmark','text',''),o('all','List every benchmark','bool',false)]},
    {type:'settings',name:'Settings card',group:'Setup',desc:'DPI, cm/360, FOV, crosshair, theme and hit sounds from KovaaK’s.',w:460,h:260,auto:true,show:ALWAYS,layout:'horizontal',
      opts:[o('dpi','DPI','bool',true),o('sens','Sensitivity','bool',true),o('fov','FOV','bool',true),o('crosshair','Crosshair','bool',true),o('theme','KovaaK’s theme','bool',true),o('sounds','Hit sounds','bool',true)]},
    {type:'peripherals',name:'Peripherals',group:'Setup',desc:'Your mouse, mousepad, keyboard, IEMs and monitor.',w:420,h:220,auto:true,show:ALWAYS,opts:[]},
    {type:'crosshair',name:'Crosshair preview',group:'Setup',desc:'Your current KovaaK’s crosshair, at its size and colour.',w:200,h:200,auto:false,show:ALWAYS,
      opts:[o('backdrop','Backdrop','choice','dark',{choices:[['dark','Dark'],['light','Light'],['none','None']]}),o('name','Name below','bool',true)]},
    {type:'mouse-path',name:'Mouse path',group:'Input',desc:'Your recent aim movement as a fading trail.',w:320,h:320,auto:false,show:LIVE,
      opts:[o('color','Trail colour','color',''),o('length','Trail length (s)','number',1.2,{min:0.2,max:4,step:0.1}),o('thickness','Thickness (px)','number',3,{min:1,max:12,step:0.5}),o('smoothing','Smoothing','number',2,{min:0,max:8,step:1}),o('fit','Fit the trail to the box','bool',true),o('zoom','Zoom (px per degree)','number',8,{min:1,max:40,step:0.5}),o('dot','Current point','bool',true)]},
    {type:'input',name:'Input display',group:'Input',desc:'W A S D, jump, crouch and mouse buttons as they are pressed.',w:300,h:190,auto:true,show:LIVE,
      opts:[o('keys','Keys','choice','wasd',{choices:[['wasd','WASD + extras'],['mouse','Mouse only'],['all','Keys and mouse']]})]},
    {type:'recent',name:'Recent runs',group:'Progress',desc:'Your latest runs with their change against your personal best.',w:460,h:200,auto:true,show:ALWAYS,
      opts:[o('count','Runs','number',5,{min:1,max:10,step:1}),o('ticker','Scrolling ticker','bool',false)]},
    {type:'standings',name:'Match standings',group:'Multiplayer',desc:'Live multiplayer standings during a match.',w:460,h:240,auto:true,show:MATCH,opts:[]},
    {type:'bracket',name:'Tournament bracket',group:'Multiplayer',desc:'The current tournament match and its bracket.',w:640,h:300,auto:false,show:ALWAYS,
      opts:[o('match','Current match','bool',true),o('tree','Bracket','bool',true)]},
    {type:'profile',name:'Profile badge',group:'About you',desc:'Your AimMod Hub name and top benchmark rank.',w:360,h:84,auto:true,show:ALWAYS,
      opts:[o('rank','Top rank','bool',true),o('tagline','Tagline','text','')]},
    {type:'now-playing',name:'Now playing',group:'About you',desc:'The scenario on screen, or the last one you played.',w:460,h:84,auto:true,show:ALWAYS,opts:[o('pb','Personal best','bool',true)]},
    {type:'clock',name:'Clock',group:'Basics',desc:'Local time, optionally with the session timer.',w:220,h:84,auto:true,show:ALWAYS,
      opts:[o('seconds','Seconds','bool',false),o('h24','24-hour','bool',true),o('session','Session timer','bool',false)]},
    {type:'text',name:'Custom text',group:'Basics',desc:'A title, a social handle or any short message.',w:420,h:70,auto:true,show:ALWAYS,
      opts:[o('text','Text','text','Your text here'),o('caption','Small caption','text',''),o('align','Alignment','choice','left',{choices:[['left','Left'],['center','Centre'],['right','Right']]})]},
    {type:'image',name:'Image',group:'Basics',desc:'A logo or picture from an https:// address.',w:240,h:240,auto:false,show:ALWAYS,
      opts:[o('url','Image address (https://)','text',''),o('fit','Fit','choice','contain',{choices:[['contain','Fit'],['cover','Fill']]})]}
  ];
  var BY_TYPE={};for(var c=0;c<CATALOG.length;c++)BY_TYPE[CATALOG[c].type]=CATALOG[c];
  var THEMES=[
    {key:'mint',name:'AimMod mint',accent:'#27e4a1',text:'#eef5f1',muted:'#a7bab0',surface:'#0b1110',alpha:0.88,border:'#30433a',radius:10,borders:true},
    {key:'minimal',name:'Minimal',accent:'#ffffff',text:'#ffffff',muted:'#c9d1cd',surface:'#000000',alpha:0.42,border:'#000000',radius:6,borders:false},
    {key:'contrast',name:'High contrast',accent:'#ffe14d',text:'#ffffff',muted:'#e6e6e6',surface:'#000000',alpha:1,border:'#ffffff',radius:4,borders:true},
    {key:'midnight',name:'Midnight',accent:'#66ccff',text:'#eef3f8',muted:'#9fb0c2',surface:'#0b1018',alpha:0.88,border:'#2b3a4d',radius:10,borders:true},
    {key:'ember',name:'Ember',accent:'#f0b45a',text:'#f7efe4',muted:'#c2b29d',surface:'#14100b',alpha:0.88,border:'#4a3a26',radius:10,borders:true}
  ];
  var THEME_BY_KEY={};for(var t=0;t<THEMES.length;t++)THEME_BY_KEY[THEMES[t].key]=THEMES[t];
  var ACCENTS=['#27e4a1','#66ccff','#f0b45a','#ff6680','#b18cff','#ffe14d','#ffffff'];
  function clamp(n,lo,hi){return n<lo?lo:n>hi?hi:n;}
  function num(v,lo,hi,def){return typeof v==='number'&&isFinite(v)?clamp(v,lo,hi):def;}
  function str(v,max,def){if(typeof v!=='string')return def;var s=v.replace(/[\u0000-\u001f\u007f]/g,'');return s.length>max?s.slice(0,max):s;}
  function color(v){return typeof v==='string'&&/^#[0-9a-fA-F]{6}$/.test(v)?v.toLowerCase():'';}
  function bool(v,def){return typeof v==='boolean'?v:def;}
  function ident(v){return typeof v==='string'&&/^[a-z0-9-]{1,24}$/.test(v)?v:'';}
  function copy(v){return JSON.parse(JSON.stringify(v));}
  function defaultsFor(type){var entry=BY_TYPE[type],out={};if(!entry)return out;for(var i=0;i<entry.opts.length;i++)out[entry.opts[i].key]=entry.opts[i].def;return out;}
  function cleanOpts(type,raw){
    var entry=BY_TYPE[type],out={};if(!entry)return out;raw=raw&&typeof raw==='object'?raw:{};
    for(var i=0;i<entry.opts.length;i++){var d=entry.opts[i],v=raw[d.key];
      if(d.kind==='bool')out[d.key]=bool(v,d.def);
      else if(d.kind==='number')out[d.key]=num(v,d.min,d.max,d.def);
      else if(d.kind==='color')out[d.key]=color(v);
      else if(d.kind==='choice'){var ok=false;for(var j=0;j<d.choices.length;j++)if(d.choices[j][0]===v)ok=true;out[d.key]=ok?v:d.def;}
      else out[d.key]=str(v,d.key==='url'?512:160,d.def);
    }
    if(type==='image'&&out.url&&!/^https:\/\/[^\s"'<>\\]+$/.test(out.url))out.url='';
    return out;
  }
  function cleanShow(v,def){v=v&&typeof v==='object'?v:{};return {menu:bool(v.menu,def.menu),scenario:bool(v.scenario,def.scenario),match:bool(v.match,def.match)};}
  function cleanWidget(raw){
    if(!raw||typeof raw!=='object'||!BY_TYPE[raw.type])return null;var entry=BY_TYPE[raw.type],id=ident(raw.id);if(!id)return null;
    var w=num(raw.w,60,BASE_W,entry.w),h=num(raw.h,40,BASE_H,entry.h);
    return {id:id,type:raw.type,x:Math.round(num(raw.x,0,BASE_W-20,0)),y:Math.round(num(raw.y,0,BASE_H-20,0)),w:Math.round(w),h:Math.round(h),
      visible:bool(raw.visible,true),opacity:num(raw.opacity,0.1,1,1),font:num(raw.font,0.6,2.5,1),
      variant:raw.variant==='compact'?'compact':'expanded',layout:raw.layout==='vertical'?'vertical':raw.layout==='horizontal'?'horizontal':(entry.layout||'vertical'),
      labels:bool(raw.labels,true),accent:color(raw.accent),background:raw.background==='none'?'none':color(raw.background),panel:num(raw.panel,-1,1,-1),
      show:cleanShow(raw.show,entry.show),opts:cleanOpts(raw.type,raw.opts)};
  }
  function cleanTheme(raw){
    raw=raw&&typeof raw==='object'?raw:{};var preset=THEME_BY_KEY[raw.preset]?raw.preset:'mint',base=THEME_BY_KEY[preset];
    return {preset:preset,accent:color(raw.accent)||base.accent,text:color(raw.text)||base.text,surface:color(raw.surface)||base.surface,alpha:num(raw.alpha,0,1,base.alpha),radius:Math.round(num(raw.radius,0,24,base.radius)),borders:bool(raw.borders,base.borders)};
  }
  function cleanScene(raw){
    if(!raw||typeof raw!=='object')return null;var id=ident(raw.id);if(!id)return null;
    var name=str(raw.name,48,'').replace(/^\s+|\s+$/g,'')||'Scene',widgets=[],seen={};
    var list=raw.widgets&&raw.widgets.length!==undefined?raw.widgets:[];
    for(var i=0;i<list.length&&widgets.length<MAX_WIDGETS;i++){var w=cleanWidget(list[i]);if(w&&!seen[w.id]){seen[w.id]=true;widgets.push(w);}}
    return {id:id,name:name,theme:cleanTheme(raw.theme),widgets:widgets};
  }
  function cleanProfile(raw){
    raw=raw&&typeof raw==='object'?raw:{};var list=raw.peripherals&&raw.peripherals.length!==undefined?raw.peripherals:[],out=[];
    for(var i=0;i<list.length&&out.length<MAX_PERIPHERALS;i++){var p=list[i];if(!p||typeof p!=='object')continue;var label=str(p.label,32,'').replace(/^\s+|\s+$/g,''),value=str(p.value,80,'').replace(/^\s+|\s+$/g,'');if(label||value)out.push({label:label,value:value});}
    return {peripherals:out};
  }
  // Same rules as the service: unknown fields drop, numbers clamp, ids stay unique.
  function normalize(raw){
    raw=raw&&typeof raw==='object'?raw:{};var scenes=[],seen={},list=raw.scenes&&raw.scenes.length!==undefined?raw.scenes:[];
    for(var i=0;i<list.length&&scenes.length<MAX_SCENES;i++){var s=cleanScene(list[i]);if(s&&!seen[s.id]){seen[s.id]=true;scenes.push(s);}}
    if(!scenes.length)return defaultStore();
    function pick(id,fallback){return seen[id]?id:fallback;}
    return {v:1,obsScene:pick(raw.obsScene,scenes[0].id),gameScene:pick(raw.gameScene,scenes[0].id),scenes:scenes,profile:cleanProfile(raw.profile)};
  }
  function widget(type,id,x,y,extra){
    var entry=BY_TYPE[type],w={id:id,type:type,x:x,y:y,w:entry.w,h:entry.h,opts:defaultsFor(type),show:copy(entry.show)};
    if(extra)for(var k in extra){if(k==='opts'){for(var q in extra.opts)w.opts[q]=extra.opts[q];}else w[k]=extra[k];}
    return cleanWidget(w);
  }
  var TEMPLATES=[
    {key:'stream',name:'Stream',desc:'Live stats, PB pace, session progress and your setup.',theme:'mint',widgets:[
      ['live-stats',40,940,{w:620}],['pb-pace',1480,880,{w:400}],['session',40,40,{w:480}],['session-graph',40,200,{w:460,h:200}],['settings',1440,40],['peripherals',1440,330,{w:440,variant:'compact'}],['recent',40,430,{variant:'compact',opts:{count:4}}]]},
    {key:'hud',name:'Minimal HUD',desc:'Only what matters while you play.',theme:'minimal',widgets:[['live-stats',660,960,{w:600,variant:'compact'}],['pb-pace',760,40,{w:400,variant:'compact'}]]},
    {key:'setup',name:'Setup showcase',desc:'Settings, peripherals and crosshair, cleanly laid out.',theme:'mint',widgets:[['settings',60,60],['peripherals',560,60],['crosshair',1020,60],['profile',60,960,{w:400}]]},
    {key:'match',name:'Match day',desc:'Standings, the bracket and a match clock.',theme:'contrast',widgets:[['standings',40,40],['bracket',1240,40],['live-stats',660,960,{w:600}],['clock',1660,980,{variant:'compact'}]]},
    {key:'practice',name:'Practice analysis',desc:'Mouse path, input display and scenario progress.',theme:'midnight',widgets:[['mouse-path',1560,680,{w:320,h:320}],['input',1240,780,{w:300}],['scenario',40,40],['rank',40,230],['live-stats',40,960,{w:600,variant:'compact'}]]}
  ];
  function fromTemplate(key,id,name){
    var tpl=null;for(var i=0;i<TEMPLATES.length;i++)if(TEMPLATES[i].key===key)tpl=TEMPLATES[i];if(!tpl)tpl=TEMPLATES[0];
    var widgets=[];for(var j=0;j<tpl.widgets.length;j++){var d=tpl.widgets[j];widgets.push(widget(d[0],'w'+(j+1),d[1],d[2],d[3]));}
    return cleanScene({id:id,name:name||tpl.name,theme:{preset:tpl.theme},widgets:widgets});
  }
  // Legacy overlay-settings.json placements: x/y are percentages, the card kept inside the screen.
  function legacyRect(p,w,h){return {x:Math.round(clamp(BASE_W*(p.x||0)/100,0,BASE_W-w)),y:Math.round(clamp(BASE_H*(p.y||0)/100,0,BASE_H-h)),w:w};}
  function legacyScene(id,name,layout){
    layout=layout||{};var stats=layout.stats||{visible:true,x:1,y:100,width:560},versus=layout.versus||{visible:true,x:100,y:100,width:300};
    var sw=clamp(stats.width||560,140,600)*1.1,vw=clamp(versus.width||300,140,600)*1.25,a=legacyRect(stats,sw,120),b=legacyRect(versus,vw,150);
    return cleanScene({id:id,name:name,theme:{preset:'mint',alpha:clamp(typeof layout.opacity==='number'?layout.opacity:1,0,1)*0.88},widgets:[
      widget('live-stats','stats',a.x,a.y,{w:Math.round(sw),visible:stats.visible!==false}),widget('pb-pace','versus',b.x,b.y,{w:Math.round(vw),visible:versus.visible!==false})]});
  }
  function defaultStore(legacy){
    legacy=legacy||null;
    var game=legacyScene('game','In-game HUD',legacy),obs=legacyScene('stream','OBS',legacy&&(legacy.obs||legacy)),setup=fromTemplate('setup','setup','Setup card');
    return {v:1,obsScene:'stream',gameScene:'game',scenes:[game,obs,setup],profile:{peripherals:[]}};
  }
  function find(store,id){if(!store)return null;for(var i=0;i<store.scenes.length;i++)if(store.scenes[i].id===id)return store.scenes[i];return null;}
  function findWidget(scene,id){if(!scene)return null;for(var i=0;i<scene.widgets.length;i++)if(scene.widgets[i].id===id)return scene.widgets[i];return null;}
  function freshId(list,prefix){var used={};for(var i=0;i<list.length;i++)used[list[i].id]=true;for(var n=1;n<1000;n++)if(!used[prefix+n])return prefix+n;return prefix+Math.floor(Math.random()*1e6);}
  function resolveTheme(scene){
    var t=scene&&scene.theme?scene.theme:cleanTheme(null),base=THEME_BY_KEY[t.preset]||THEMES[0];
    return {preset:t.preset,accent:t.accent,text:t.text,muted:base.muted,surface:t.surface,alpha:t.alpha,border:base.border,radius:t.radius,borders:t.borders};
  }
  // menu, scenario (solo practice) or match (a multiplayer match is on).
  function context(data){if(data&&data.board&&data.board.phase&&data.board.phase!=='lobby')return 'match';var live=data&&data.live;return live&&live.active&&!live.paused&&!live.replay?'scenario':'menu';}
  function shownIn(w,ctx){return !!(w&&w.visible&&w.show&&w.show[ctx]);}
  // Snapping: to an 8 px grid, the screen edges and centre, and other widgets' edges.
  function snap(rect,others,threshold){
    threshold=threshold||10;var gx=[0,BASE_W/2,BASE_W],gy=[0,BASE_H/2,BASE_H],i,best;
    for(i=0;i<others.length;i++){var r=others[i];gx.push(r.x,r.x+r.w,r.x+r.w/2);gy.push(r.y,r.y+r.h,r.y+r.h/2);}
    function axis(start,size,lines){best=null;var cand=[[start,0],[start+size/2,size/2],[start+size,size]];for(var a=0;a<cand.length;a++)for(var b=0;b<lines.length;b++){var d=Math.abs(cand[a][0]-lines[b]);if(d<=threshold&&(!best||d<best.d))best={d:d,pos:lines[b]-cand[a][1],line:lines[b]};}return best;}
    var sx=axis(rect.x,rect.w,gx),sy=axis(rect.y,rect.h,gy),out={x:rect.x,y:rect.y,w:rect.w,h:rect.h,guides:[]};
    if(sx){out.x=sx.pos;out.guides.push({axis:'x',at:sx.line});}else out.x=Math.round(rect.x/8)*8;
    if(sy){out.y=sy.pos;out.guides.push({axis:'y',at:sy.line});}else out.y=Math.round(rect.y/8)*8;
    out.x=Math.round(clamp(out.x,0,BASE_W-Math.min(rect.w,BASE_W)));out.y=Math.round(clamp(out.y,0,BASE_H-Math.min(rect.h,BASE_H)));return out;
  }
  // Share codes: a prefix and base64 of the UTF-8 JSON. Pure JS (no btoa/atob, no Intl).
  var B64='ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  function utf8(s){var out=[];for(var i=0;i<s.length;i++){var c=s.charCodeAt(i);if(c>=0xd800&&c<=0xdbff&&i+1<s.length){var d=s.charCodeAt(i+1);if(d>=0xdc00&&d<=0xdfff){c=0x10000+((c-0xd800)<<10)+(d-0xdc00);i++;}}
    if(c<0x80)out.push(c);else if(c<0x800)out.push(0xc0|c>>6,0x80|c&63);else if(c<0x10000)out.push(0xe0|c>>12,0x80|c>>6&63,0x80|c&63);else out.push(0xf0|c>>18,0x80|c>>12&63,0x80|c>>6&63,0x80|c&63);}return out;}
  function fromUtf8(b){var s='';for(var i=0;i<b.length;){var c=b[i++];if(c>=0xf0){c=(c&7)<<18|(b[i++]&63)<<12|(b[i++]&63)<<6|b[i++]&63;c-=0x10000;s+=String.fromCharCode(0xd800+(c>>10),0xdc00+(c&1023));}else if(c>=0xe0)s+=String.fromCharCode((c&15)<<12|(b[i++]&63)<<6|b[i++]&63);else if(c>=0xc0)s+=String.fromCharCode((c&31)<<6|b[i++]&63);else s+=String.fromCharCode(c);}return s;}
  function b64(bytes){var s='';for(var i=0;i<bytes.length;i+=3){var n=bytes[i]<<16|(i+1<bytes.length?bytes[i+1]<<8:0)|(i+2<bytes.length?bytes[i+2]:0);s+=B64.charAt(n>>18&63)+B64.charAt(n>>12&63)+(i+1<bytes.length?B64.charAt(n>>6&63):'=')+(i+2<bytes.length?B64.charAt(n&63):'=');}return s;}
  function unb64(s){s=s.replace(/[^A-Za-z0-9+\/]/g,'');var out=[];for(var i=0;i+1<s.length;i+=4){var n=B64.indexOf(s.charAt(i))<<18|B64.indexOf(s.charAt(i+1))<<12|(i+2<s.length?B64.indexOf(s.charAt(i+2)):0)<<6|(i+3<s.length?B64.indexOf(s.charAt(i+3)):0);out.push(n>>16&255);if(i+2<s.length)out.push(n>>8&255);if(i+3<s.length)out.push(n&255);}return out;}
  // A share code carries one scene (layout and theme). Peripherals stay private.
  function encodeShare(scene){var clean=cleanScene(scene);return SHARE_PREFIX+b64(utf8(JSON.stringify({v:1,scene:clean})));}
  function decodeShare(code){
    if(typeof code!=='string')return null;code=code.replace(/^\s+|\s+$/g,'');if(code.indexOf(SHARE_PREFIX)!==0||code.length>200000)return null;
    try{var data=JSON.parse(fromUtf8(unb64(code.slice(SHARE_PREFIX.length))));if(!data||data.v!==1)return null;return cleanScene(data.scene);}catch(e){return null;}
  }
  root.AimModOverlayModel={BASE_W:BASE_W,BASE_H:BASE_H,MAX_SCENES:MAX_SCENES,MAX_WIDGETS:MAX_WIDGETS,MAX_PERIPHERALS:MAX_PERIPHERALS,CATALOG:CATALOG,THEMES:THEMES,ACCENTS:ACCENTS,TEMPLATES:TEMPLATES,
    entry:function(type){return BY_TYPE[type]||null;},theme:function(key){return THEME_BY_KEY[key]||null;},defaultsFor:defaultsFor,widget:widget,cleanWidget:cleanWidget,cleanScene:cleanScene,cleanTheme:cleanTheme,normalize:normalize,
    defaultStore:defaultStore,legacyScene:legacyScene,fromTemplate:fromTemplate,find:find,findWidget:findWidget,freshId:freshId,resolveTheme:resolveTheme,context:context,shownIn:shownIn,snap:snap,
    encodeShare:encodeShare,decodeShare:decodeShare,copy:copy,clamp:clamp};
})(typeof window!=='undefined'?window:this);
