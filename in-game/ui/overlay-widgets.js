// AimMod overlay widgets: one self-contained component per widget type, shared
// by the Overlays editor preview, the in-game HUD view and the OBS sources.
// Each component returns a small node description; patch() applies it to the
// DOM in place (text and styles change, nodes stay), so 10 Hz updates never
// rebuild the page. Display only: nothing here reads input or writes game state.
// Gameface-safe: DOM nodes and canvas only, ES5, solid canvas colours.
(function(root){
  'use strict';
  var M=root.AimModOverlayModel;
  // ---- formatting (standalone: no Intl, never "-0") ----
  function known(n){return typeof n==='number'&&isFinite(n);}
  function fmt(n,d){if(!known(n))return '—';d=d||0;var p=Math.pow(10,d),r=Math.round(Math.abs(n)*p)/p,parts=r.toFixed(d).split('.'),s=parts[0].replace(/\B(?=(\d{3})+(?!\d))/g,',')+(parts[1]?'.'+parts[1]:'');return (n<0&&/[1-9]/.test(s)?'-':'')+s;}
  function signed(n,d){if(!known(n))return '—';var s=fmt(n,d);return /[1-9]/.test(s)&&n>0?'+'+s:s;}
  // 1 -> 1.0, 1.5 -> 1.5, 1.25 -> 1.25: the decimals the value needs, at least one.
  function short(n){if(!known(n))return '—';return fmt(n,2).replace(/(\.\d)0$/,'$1');}
  function pct(n,d){return known(n)?fmt(n,d===undefined?1:d)+'%':'—';}
  function clock(sec){if(!known(sec)||sec<0)return '—';sec=Math.floor(sec);var m=Math.floor(sec/60),s=sec%60;return m+':'+(s<10?'0':'')+s;}
  function span(sec){if(!known(sec)||sec<0)return '—';sec=Math.round(sec);if(sec<60)return sec+'s';var h=Math.floor(sec/3600),m=Math.floor(sec%3600/60);return h>0?h+'h '+(m<10?'0':'')+m+'m':m+'m';}
  function millis(ms){if(!known(ms))return '—';return ms<1000?fmt(ms)+' ms':fmt(ms/1000,2)+' s';}
  function ago(ms,now){if(!known(ms))return '';var s=Math.max(0,(now-ms)/1000);if(s<60)return 'now';if(s<3600)return Math.floor(s/60)+'m ago';if(s<86400)return Math.floor(s/3600)+'h ago';return Math.floor(s/86400)+'d ago';}
  function hex(c){c=/^#[0-9a-f]{6}$/i.test(c||'')?c:'#000000';return [parseInt(c.slice(1,3),16),parseInt(c.slice(3,5),16),parseInt(c.slice(5,7),16)];}
  // CSS colours with alpha for DOM styles only; canvas drawing uses solid colours.
  var CSS_RGBA='rgb'+'a(';
  function rgba(c,a){var v=hex(c);return CSS_RGBA+v[0]+','+v[1]+','+v[2]+','+(Math.round(a*1000)/1000)+')';}
  function mix(a,b,t){var x=hex(a),y=hex(b),out='#';for(var i=0;i<3;i++){var v=Math.round(x[i]+(y[i]-x[i])*t).toString(16);out+=v.length<2?'0'+v:v;}return out;}
  function dark(c){var v=hex(c);return (v[0]*299+v[1]*587+v[2]*114)/1000<140;}
  // Control characters never reach the page.
  function strip(s){var out='';for(var i=0;i<s.length;i++){var c=s.charCodeAt(i);if(c>=32&&(c<127||c>159))out+=s.charAt(i);}return out;}
  function safe(s,max){s=typeof s==='string'?s:'';s=strip(s);return max&&s.length>max?s.slice(0,max-1)+'…':s;}
  // ---- node descriptions and in-place patching ----
  function h(tag,css,content,attrs){var v={tag:tag,css:css||'',attrs:attrs||null};if(typeof content==='string'||typeof content==='number')v.text=String(content);else v.kids=content||[];return v;}
  function styled(v,style){v.attrs=v.attrs||{};v.attrs.style=style;return v;}
  function create(doc,v){var n=doc.createElement(v.tag);n.amKey=v.key||'';return n;}
  function apply(doc,n,v){
    if(n.className!==v.css)n.className=v.css;
    var style=v.attrs&&v.attrs.style||null,prev=n.amStyle||{};
    if(style){for(var k in style)if(prev[k]!==style[k])n.style[k]=style[k];for(var q in prev)if(!(q in style))n.style[q]='';}
    else for(var p in prev)n.style[p]='';
    n.amStyle=style||{};
    if(v.attrs&&v.attrs.width!==undefined){if(n.width!==v.attrs.width)n.width=v.attrs.width;if(n.height!==v.attrs.height)n.height=v.attrs.height;}
    if(v.attrs&&v.attrs.label&&n.amLabel!==v.attrs.label){n.amLabel=v.attrs.label;if(n.setAttribute)n.setAttribute('aria-label',v.attrs.label);}
    if(v.text!==undefined){if(n.amText!==v.text){n.textContent=v.text;n.amText=v.text;}}
    // Opaque nodes keep the children their paint function draws (the standings component).
    else if(!(v.attrs&&v.attrs.opaque))patch(doc,n,v.kids);
    if(v.attrs&&v.attrs.paint)v.attrs.paint(n);
  }
  function patch(doc,parent,list){
    if(parent.amText!==undefined){parent.textContent='';parent.amText=undefined;}
    var kids=parent.childNodes||parent.children||[];
    for(var i=0;i<list.length;i++){
      var v=list[i],n=kids[i];
      if(!n||!n.tagName||n.tagName.toLowerCase()!==v.tag||n.amKey!==(v.key||'')){var m=create(doc,v);if(n)parent.replaceChild(m,n);else parent.appendChild(m);n=m;kids=parent.childNodes||parent.children;}
      apply(doc,n,v);
    }
    while(kids.length>list.length)parent.removeChild(kids[kids.length-1]);
  }
  // ---- shared pieces ----
  function T(env){return env.theme;}
  function accentOf(w,env){return w.accent||env.theme.accent;}
  function eyebrow(text,w,env,right){
    var kids=[styled(h('div','amw-eyebrow',text),{color:accentOf(w,env)})];
    if(right)kids.push(styled(h('div','amw-eyebrow-side',right),{color:env.theme.muted}));
    return h('div','amw-head',kids);
  }
  function label(text,env){return styled(h('div','amw-label',text),{color:env.theme.muted});}
  function tile(w,env,lab,value,unit,tone){
    var val=[h('span','amw-num',value)];if(unit)val.push(styled(h('span','amw-unit',unit),{color:env.theme.muted}));
    var kids=[];if(w.labels)kids.push(label(lab,env));kids.push(styled(h('div','amw-value',val),tone?{color:tone}:null));
    return h('div','amw-tile',kids);
  }
  function tiles(w,env,list){
    if(w.layout==='vertical'){var rows=[];for(var i=0;i<list.length;i++)rows.push(row(w,env,list[i][0],list[i][1]+(list[i][2]?' '+list[i][2]:''),list[i][3]));return h('div','amw-rows',rows);}
    var out=[];for(var j=0;j<list.length;j++){var t=tile(w,env,list[j][0],list[j][1],list[j][2],list[j][3]);if(j>0)t.attrs={style:{borderLeft:'1px solid '+rgba(env.theme.muted,0.22)}};out.push(t);}
    return h('div','amw-tiles'+(list.length>4?' many':''),out);
  }
  function row(w,env,lab,value,tone,extra){
    var kids=[];if(w.labels!==false)kids.push(styled(h('div','amw-row-label',lab),{color:env.theme.muted}));
    var vk=[];if(extra)vk.push(extra);vk.push(h('span','',value));
    kids.push(styled(h('div','amw-row-value',vk),tone?{color:tone}:null));
    return styled(h('div','amw-row',kids),{borderTop:'1px solid '+rgba(env.theme.muted,0.14)});
  }
  function bar(env,w,ratio,marker){
    ratio=known(ratio)?Math.max(0,Math.min(1,ratio)):0;var kids=[styled(h('div','amw-bar-fill',''),{width:(ratio*100).toFixed(1)+'%',background:accentOf(w,env)})];
    if(known(marker))kids.push(styled(h('div','amw-bar-mark',''),{left:(Math.max(0,Math.min(1,marker))*100).toFixed(1)+'%',background:env.theme.text}));
    return styled(h('div','amw-bar',kids),{background:rgba(env.theme.muted,0.2)});
  }
  function pill(text,tone,env){return styled(h('span','amw-pill',text),{color:dark(tone)?'#ffffff':'#06120d',background:tone});}
  function empty(env,text){return styled(h('div','amw-empty',text),{color:env.theme.muted});}
  function good(env){return env.theme.preset==='contrast'?'#5dff9c':'#27e4a1';}
  function warn(){return '#f0b45a';}
  // ---- widgets ----
  var R={};
  R['live-stats']=function(w,data,env){
    var L=data.live||{},o=w.opts,list=[],active=L.active;
    if(o.m_score)list.push(['Score',fmt(L.score),'']);
    if(o.m_spm)list.push(['Score / min',fmt(L.scorePerMinute),'']);
    if(o.m_accuracy)list.push(['Accuracy',known(L.accuracy)?fmt(L.accuracy,1):'—',known(L.accuracy)?'%':'']);
    if(o.m_kills)list.push(['Kills',fmt(L.kills),'']);
    if(o.m_ttk)list.push(['Last TTK',known(L.lastTimeToKillSeconds)?fmt(L.lastTimeToKillSeconds*1000):'—',known(L.lastTimeToKillSeconds)?'ms':'']);
    if(o.m_kps)list.push(['Kills / sec',fmt(L.killsPerSecond,2),'']);
    if(o.m_time)list.push(['Time left',clock(L.remainingSeconds),'']);
    if(o.m_hits)list.push(['Hits / shots',fmt(L.hits)+' / '+fmt(L.shots),'']);
    if(o.m_damage)list.push(['Damage',fmt(L.damage),'']);
    var kids=[];if(w.variant==='expanded'||o.scenario)kids.push(eyebrow(active?'Live':'Live · waiting',w,env,o.scenario?safe(L.scenario,60):''));
    kids.push(list.length?tiles(w,env,list):empty(env,'Choose metrics in the widget settings.'));
    return kids;
  };
  R['pb-pace']=function(w,data,env){
    var L=data.live||{},o=w.opts,name=safe(L.opponentName||'Personal best',40),opp=L.opponentScore,delta=L.projectedDelta,tone=known(delta)?(delta>=0?good(env):warn()):env.theme.muted;
    var kids=[eyebrow('VS '+name,w,env)];
    var big=[h('span','amw-num',o.projection?fmt(L.projectedScore):fmt(L.score))];
    var head=[styled(h('div','amw-value big',big),null)];
    if(known(delta))head.push(pill((delta>=0?'+':'')+fmt(delta)+(delta>=0?' ahead':' behind'),tone,env));
    var line=[];if(w.labels)line.push(label(o.projection?'Projected finish':'Current score',env));line.push(h('div','amw-pace-main',head));kids.push(h('div','amw-pace',line));
    if(o.bar)kids.push(bar(env,w,known(opp)&&opp>0?L.score/opp:0,known(opp)&&opp>0&&known(L.projectedScore)?L.projectedScore/opp:null));
    kids.push(h('div','amw-split',[h('div','amw-split-cell',[label('Current',env),h('div','amw-mid',fmt(L.score))]),h('div','amw-split-cell right',[label(name,env),h('div','amw-mid',fmt(opp))])]));
    if(o.note)kids.push(styled(h('div','amw-note',known(opp)?(known(L.projectedScore)?'Projected finish at your current pace.':'Pace starts as you play.'):'No score to compare for this scenario yet.'),{color:env.theme.muted}));
    return kids;
  };
  R.session=function(w,data,env){
    var S=data.session||{},o=w.opts,list=[];
    if(o.m_runs)list.push(['Runs',fmt(S.runs),'']);
    if(o.m_time)list.push(['Play time',span(S.seconds),'']);
    if(o.m_pbs)list.push(['New PBs',fmt(S.pbs),'',S.pbs>0?accentOf(w,env):'']);
    if(o.m_accuracy)list.push(['Accuracy',known(S.accuracy)?fmt(S.accuracy,1):'—',known(S.accuracy)?'%':'']);
    if(o.m_scenarios)list.push(['Scenarios',fmt(S.scenarios),'']);
    if(o.m_best)list.push(['Best vs PB',known(S.bestRatio)?fmt(S.bestRatio*100,1):'—',known(S.bestRatio)?'%':'']);
    var kids=[];if(w.variant==='expanded')kids.push(eyebrow('This session',w,env,S.runs?'':'No runs yet'));
    kids.push(list.length?tiles(w,env,list):empty(env,'Choose metrics in the widget settings.'));return kids;
  };
  function graphPaint(w,data,env){
    return function(canvas){
      var ctx=canvas.getContext&&canvas.getContext('2d');if(!ctx)return;var W=canvas.width,H=canvas.height,pts=((data.session||{}).graph||[]).slice(-w.opts.runs),acc=accentOf(w,env),muted=env.theme.muted,bg=env.theme.surface;
      ctx.clearRect(0,0,W,H);var pad=8*env.dpr,top=pad,bottom=H-pad,left=pad,right=W-pad;
      if(!pts.length){ctx.fillStyle=muted;ctx.font=Math.round(13*env.dpr*w.font)+'px Roboto';ctx.fillText('Runs this session appear here.',left,H/2);return;}
      var lo=1,hi=1;for(var i=0;i<pts.length;i++){lo=Math.min(lo,pts[i].ratio);hi=Math.max(hi,pts[i].ratio);}lo=Math.max(0,lo-0.05);hi=hi+0.03;
      function y(r){return bottom-(r-lo)/(hi-lo)*(bottom-top);}function x(i){return pts.length<2?(left+right)/2:left+i*(right-left)/(pts.length-1);}
      if(w.opts.pbLine){ctx.strokeStyle=mix(muted,bg,0.4);ctx.lineWidth=1*env.dpr;ctx.beginPath();var py=Math.round(y(1))+0.5;for(var d=left;d<right;d+=8*env.dpr){ctx.moveTo(d,py);ctx.lineTo(Math.min(right,d+4*env.dpr),py);}ctx.stroke();
        ctx.fillStyle=muted;ctx.font=Math.round(11*env.dpr*w.font)+'px Roboto';ctx.fillText('PB',right-18*env.dpr*w.font,py-4*env.dpr);}
      if(w.opts.fill&&pts.length>1){ctx.globalAlpha=0.18;ctx.fillStyle=acc;ctx.beginPath();ctx.moveTo(x(0),bottom);for(var f=0;f<pts.length;f++)ctx.lineTo(x(f),y(pts[f].ratio));ctx.lineTo(x(pts.length-1),bottom);ctx.closePath();ctx.fill();ctx.globalAlpha=1;}
      ctx.strokeStyle=acc;ctx.lineWidth=2.5*env.dpr;ctx.lineJoin='round';ctx.beginPath();for(var j=0;j<pts.length;j++){if(j)ctx.lineTo(x(j),y(pts[j].ratio));else ctx.moveTo(x(j),y(pts[j].ratio));}ctx.stroke();
      for(var k=0;k<pts.length;k++){var r=(pts[k].pb?4.5:2.5)*env.dpr;ctx.fillStyle=pts[k].pb?env.theme.text:acc;ctx.beginPath();ctx.arc(x(k),y(pts[k].ratio),r,0,Math.PI*2);ctx.fill();}
    };
  }
  function canvasNode(w,env,paint,key,height){
    var cw=Math.max(10,Math.round(env.width*env.dpr)),ch=Math.max(10,Math.round(height*env.dpr));
    var v=h('canvas','amw-canvas',[],{width:cw,height:ch,paint:paint,style:{width:Math.round(env.width)+'px',height:Math.round(height)+'px'}});v.key=key;return v;
  }
  R['session-graph']=function(w,data,env){
    var S=data.session||{},n=(S.graph||[]).length,kids=[];
    if(w.variant==='expanded')kids.push(eyebrow('Session',w,env,n?n+(n===1?' run':' runs')+' · % of PB':''));
    var head=w.variant==='expanded'?30*w.font:0;env.width=Math.max(20,w.w-env.padX*2);
    kids.push(canvasNode(w,env,graphPaint(w,data,env),'graph',Math.max(30,w.h-env.padY*2-head)));return kids;
  };
  R.scenario=function(w,data,env){
    var S=data.scenario||{},o=w.opts,list=[];
    if(o.pb)list.push(['Personal best',fmt(S.best),'']);if(o.attempts)list.push(['Attempts',fmt(S.attempts),'']);
    if(o.average)list.push(['Avg of last 10',fmt(S.average),'']);if(o.last)list.push(['Last score',fmt(S.last),'']);
    var kids=[eyebrow('Scenario',w,env)];kids.push(h('div','amw-title',S.name?safe(S.name,70):'No scenario yet'));
    if(list.length)kids.push(tiles(w,env,list));return kids;
  };
  R.rank=function(w,data,env){
    var list=data.benchmarks||[],kids=[eyebrow('Benchmark rank',w,env)],want=(w.opts.benchmark||'').toLowerCase(),shown=[];
    for(var i=0;i<list.length;i++)if(!want||list[i].name.toLowerCase().indexOf(want)>=0)shown.push(list[i]);
    if(!shown.length){kids.push(empty(env,data.profile&&data.profile.linked?'No benchmark ranks yet.':'Link AimMod Hub to show your ranks.'));return kids;}
    if(!w.opts.all)shown=shown.slice(0,1);
    if(shown.length===1){var b=shown[0];kids.push(styled(h('div','amw-value big',[h('span','amw-num',safe(b.rank||'Unranked',32))]),{color:accentOf(w,env)}));if(w.labels)kids.push(label(safe(b.name,60),env));
      if(known(b.progress))kids.push(bar(env,w,b.progress));if(b.next)kids.push(styled(h('div','amw-note','Next: '+safe(b.next,32)),{color:env.theme.muted}));return kids;}
    var rows=[];for(var j=0;j<shown.length&&j<8;j++)rows.push(row(w,env,safe(shown[j].name,44),safe(shown[j].rank||'Unranked',24),accentOf(w,env)));kids.push(h('div','amw-rows',rows));return kids;
  };
  function crosshairPaint(w,data,env,size,backdrop){
    return function(canvas){
      var ctx=canvas.getContext&&canvas.getContext('2d');if(!ctx)return;var K=data.kovaaks||{},W=canvas.width,H=canvas.height,col=K.crosshairColor||'#ffffff',scale=known(K.crosshairScale)?K.crosshairScale:1;
      ctx.clearRect(0,0,W,H);
      if(backdrop!=='none'){ctx.fillStyle=backdrop==='light'?'#d9dedb':'#1b2320';ctx.fillRect(0,0,W,H);ctx.strokeStyle=backdrop==='light'?'#c4cbc7':'#26302c';ctx.lineWidth=1;for(var g=0;g<=W;g+=Math.round(W/8)){ctx.beginPath();ctx.moveTo(g+0.5,0);ctx.lineTo(g+0.5,H);ctx.stroke();ctx.beginPath();ctx.moveTo(0,g+0.5);ctx.lineTo(W,g+0.5);ctx.stroke();}}
      var img=env.image&&env.image(K.crosshairImage),cx=W/2,cy=H/2;
      if(img&&img.complete&&img.width>0){
        var s=Math.min(W,H)*0.9/Math.max(img.width,img.height),dw=img.width*Math.min(s,scale*2*env.dpr),dh=img.height*Math.min(s,scale*2*env.dpr);
        var off=env.doc&&env.doc.createElement?env.doc.createElement('canvas'):null,octx=off&&off.getContext?off.getContext('2d'):null;
        if(octx){off.width=Math.max(1,Math.round(dw));off.height=Math.max(1,Math.round(dh));octx.drawImage(img,0,0,off.width,off.height);octx.globalCompositeOperation='source-in';octx.fillStyle=col;octx.fillRect(0,0,off.width,off.height);ctx.drawImage(off,cx-dw/2,cy-dh/2,dw,dh);}
        else ctx.drawImage(img,cx-dw/2,cy-dh/2,dw,dh);
        return;
      }
      var arm=Math.max(4,size*0.12*scale),gap=Math.max(2,size*0.04*scale),th=Math.max(1.5,size*0.018*scale);
      ctx.fillStyle='#000000';ctx.globalAlpha=0.6;
      ctx.fillRect(cx-th/2-1,cy-gap-arm-1,th+2,arm+2);ctx.fillRect(cx-th/2-1,cy+gap-1,th+2,arm+2);ctx.fillRect(cx-gap-arm-1,cy-th/2-1,arm+2,th+2);ctx.fillRect(cx+gap-1,cy-th/2-1,arm+2,th+2);
      ctx.globalAlpha=1;ctx.fillStyle=col;
      ctx.fillRect(cx-th/2,cy-gap-arm,th,arm);ctx.fillRect(cx-th/2,cy+gap,th,arm);ctx.fillRect(cx-gap-arm,cy-th/2,arm,th);ctx.fillRect(cx+gap,cy-th/2,arm,th);
      ctx.fillRect(cx-th/2,cy-th/2,th,th);
    };
  }
  function crossName(K){return K&&K.crosshair?safe(K.crosshair.replace(/\.png$/i,''),40):'';}
  R.settings=function(w,data,env){
    var K=data.kovaaks||{},o=w.opts,kids=[eyebrow('Setup',w,env,w.variant==='expanded'?'KovaaK’s':'')];
    if(!K.available){kids.push(empty(env,'KovaaK’s settings appear here once the game has saved them.'));return kids;}
    var top=[];
    if(o.dpi)top.push(['DPI',fmt(K.dpi),'']);
    if(o.sens){if(known(K.cm360))top.push(['Sensitivity',fmt(K.cm360,2),'cm/360']);else top.push(['Sensitivity',fmt(K.sens,2),safe(K.sensScale,18)]);}
    if(o.fov)top.push(['FOV'+(K.fovScale?' · '+safe(K.fovScale,16):''),fmt(K.fov,known(K.fov)&&K.fov%1?1:0)+(known(K.fov)?'°':''),'']);
    if(top.length)kids.push(tiles({labels:w.labels,layout:w.layout==='vertical'?'vertical':'horizontal'},env,top));
    var rows=[];
    if(o.sens&&known(K.cm360)&&K.sensScale&&K.sensScale!=='cm/360'&&w.variant==='expanded')rows.push(row(w,env,'In-game sens',fmt(K.sens,2)+' '+safe(K.sensScale,18)));
    if(o.crosshair&&K.crosshair){var dot=styled(h('span','amw-swatch',''),{background:K.crosshairColor||'#ffffff'});rows.push(row(w,env,'Crosshair',crossName(K)+(known(K.crosshairScale)?' · '+short(K.crosshairScale)+'×':''),'',dot));}
    if(o.theme&&K.theme)rows.push(row(w,env,'Theme',safe(K.theme,40)));
    if(o.sounds&&K.hitSounds&&K.hitSounds.length)rows.push(row(w,env,K.hitSounds.length>1?'Hit sounds':'Hit sound',safe(K.hitSounds.join(' / '),60)));
    if(rows.length)kids.push(h('div','amw-rows',rows));return kids;
  };
  R.peripherals=function(w,data,env){
    var list=data.peripherals||[],kids=[eyebrow('Peripherals',w,env)],rows=[];
    for(var i=0;i<list.length;i++)if(list[i].value||list[i].label)rows.push(row(w,env,safe(list[i].label,32),safe(list[i].value,60)));
    if(!rows.length){kids.push(empty(env,'Add your gear on the Overlays page, under Profile.'));return kids;}
    if(w.variant==='compact'&&w.layout==='horizontal'){var t=[];for(var j=0;j<list.length&&j<4;j++)t.push([safe(list[j].label,20),safe(list[j].value,28),'']);kids.push(h('div','amw-tiles gear',tileList(w,env,t)));return kids;}
    kids.push(h('div','amw-rows',rows));return kids;
  };
  function tileList(w,env,list){var out=[];for(var i=0;i<list.length;i++){var kids=[];if(w.labels)kids.push(label(list[i][0],env));kids.push(h('div','amw-gear',list[i][1]));var t=h('div','amw-tile',kids);if(i>0)t.attrs={style:{borderLeft:'1px solid '+rgba(env.theme.muted,0.22)}};out.push(t);}return out;}
  R.crosshair=function(w,data,env){
    var K=data.kovaaks||{},kids=[],nameH=w.opts.name?26*w.font:0;env.width=Math.max(20,w.w-env.padX*2);var size=Math.max(20,Math.min(env.width,w.h-env.padY*2-nameH));
    env.width=size;kids.push(styled(h('div','amw-center',[canvasNode(w,env,crosshairPaint(w,data,env,size*env.dpr,w.opts.backdrop),'cross',size)]),null));
    if(w.opts.name)kids.push(styled(h('div','amw-caption',crossName(K)||'Crosshair'),{color:env.theme.muted}));return kids;
  };
  // Mouse path: yaw/pitch samples (degrees) drawn around the newest point, older segments fading out.
  function pathPoints(path,opts){
    if(!path||path.length<2)return [];var newest=path[path.length-1][0],from=newest-opts.length*1000,pts=[],yaw=0,last=null;
    for(var i=0;i<path.length;i++){var p=path[i];if(last!==null){var d=p[1]-last;d=((d+540)%360+360)%360-180;yaw+=d;}last=p[1];if(p[0]>=from)pts.push([p[0],yaw,p[2]]);}
    var k=Math.round(opts.smoothing);if(k>0&&pts.length>2){var sm=[];for(var a=0;a<pts.length;a++){var sx=0,sy=0,n=0;for(var b=Math.max(0,a-k);b<=Math.min(pts.length-1,a+k);b++){sx+=pts[b][1];sy+=pts[b][2];n++;}sm.push([pts[a][0],sx/n,sy/n]);}sm[sm.length-1]=pts[pts.length-1];pts=sm;}
    return pts;
  }
  function pathPaint(w,env,motion){
    return function(canvas){
      var ctx=canvas.getContext&&canvas.getContext('2d');if(!ctx)return;var W=canvas.width,H=canvas.height,o=w.opts,col=o.color||accentOf(w,env),pts=pathPoints(motion&&motion.path,o);
      ctx.clearRect(0,0,W,H);ctx.strokeStyle=mix(env.theme.muted,env.theme.surface,0.65);ctx.lineWidth=1;ctx.beginPath();ctx.moveTo(W/2+0.5,H*0.42);ctx.lineTo(W/2+0.5,H*0.58);ctx.moveTo(W*0.42,H/2+0.5);ctx.lineTo(W*0.58,H/2+0.5);ctx.stroke();
      if(pts.length<2)return;var end=pts[pts.length-1],z=o.zoom*env.dpr,first=pts[0][0],spanT=Math.max(1,end[0]-first);
      if(o.fit){var mx=0.5,my=0.5;for(var f=0;f<pts.length;f++){mx=Math.max(mx,Math.abs(pts[f][1]-end[1]));my=Math.max(my,Math.abs(pts[f][2]-end[2]));}
        var target=Math.min(z*4,W*0.44/mx,H*0.44/my),prev=canvas.amZoom||target;canvas.amZoom=prev+(target-prev)*(target<prev?0.5:0.12);z=canvas.amZoom;}
      ctx.strokeStyle=col;ctx.lineWidth=o.thickness*env.dpr;ctx.lineCap='round';ctx.lineJoin='round';
      for(var i=1;i<pts.length;i++){var a=pts[i-1],b=pts[i],age=(b[0]-first)/spanT;ctx.globalAlpha=Math.max(0.04,Math.pow(age,1.6));
        ctx.beginPath();ctx.moveTo(W/2+(a[1]-end[1])*z,H/2-(a[2]-end[2])*z);ctx.lineTo(W/2+(b[1]-end[1])*z,H/2-(b[2]-end[2])*z);ctx.stroke();}
      ctx.globalAlpha=1;if(o.dot){ctx.fillStyle=env.theme.text;ctx.beginPath();ctx.arc(W/2,H/2,Math.max(2.5,o.thickness*0.9)*env.dpr,0,Math.PI*2);ctx.fill();}
    };
  }
  R['mouse-path']=function(w,data,env){
    var kids=[];if(w.variant==='expanded')kids.push(eyebrow('Mouse path',w,env,fmt(w.opts.length,1)+' s'));var head=w.variant==='expanded'?30*w.font:0;
    env.width=Math.max(20,w.w-env.padX*2);var v=canvasNode(w,env,pathPaint(w,env,env.motion),'path',Math.max(20,w.h-env.padY*2-head));kids.push(v);return kids;
  };
  function key(env,w,text,down,wide){var acc=accentOf(w,env);return styled(h('div','amw-key'+(wide?' wide':''),text),down?{background:acc,color:dark(acc)?'#ffffff':'#06120d',borderColor:acc}:{background:rgba(env.theme.surface,0.6),color:env.theme.text,borderColor:rgba(env.theme.muted,0.35)});}
  R.input=function(w,data,env){
    var I=(env.motion&&env.motion.input)||{},mode=w.opts.keys,rows=[];
    if(mode!=='mouse'){rows.push(h('div','amw-keyrow',[key(env,w,'W',I.w)]));rows.push(h('div','amw-keyrow',[key(env,w,'A',I.a),key(env,w,'S',I.s),key(env,w,'D',I.d)]));rows.push(h('div','amw-keyrow',[key(env,w,'Shift',I.shift,true),key(env,w,'Space',I.space,true),key(env,w,'Ctrl',I.ctrl,true)]));}
    if(mode!=='wasd')rows.push(h('div','amw-keyrow',[key(env,w,'LMB',I.lmb,true),key(env,w,'RMB',I.rmb,true)]));
    if(mode==='wasd')rows[2].kids.push(key(env,w,'LMB',I.lmb,true));
    var kids=[];if(w.variant==='expanded')kids.push(eyebrow('Input',w,env,I.available===false?'Game not focused':''));kids.push(h('div','amw-keys',rows));return kids;
  };
  R.recent=function(w,data,env){
    var list=(data.recent||[]).slice(0,w.opts.count),kids=[eyebrow('Recent runs',w,env)];
    if(!list.length){kids.push(empty(env,'Your latest runs appear here.'));return kids;}
    if(w.opts.ticker){var i=Math.floor(env.now/4000)%list.length,r=list[i];kids.push(h('div','amw-ticker',[h('div','amw-ticker-name',safe(r.scenario,48)),h('div','amw-ticker-score',fmt(r.score)),runBadge(r,env,w)]));return kids;}
    var rows=[];for(var j=0;j<list.length;j++){var x=list[j];rows.push(styled(h('div','amw-run',[h('div','amw-run-name',safe(x.scenario,56)),styled(h('div','amw-run-time',ago(x.at,env.now)),{color:env.theme.muted}),h('div','amw-run-score',fmt(x.score)),runBadge(x,env,w)]),{borderTop:'1px solid '+rgba(env.theme.muted,0.14)}));}
    kids.push(h('div','amw-rows',rows));return kids;
  };
  function runBadge(r,env,w){if(r.pb)return pill('PB',accentOf(w,env),env);if(known(r.delta))return styled(h('span','amw-delta',signed(r.delta,1)+'%'),{color:r.delta>=0?good(env):env.theme.muted});return h('span','amw-delta','');}
  R.standings=function(w,data,env){
    var b=data.board,kids=[];if(!b){kids.push(eyebrow('Match',w,env));kids.push(empty(env,'Standings show during a multiplayer match.'));return kids;}
    var key=JSON.stringify(b);kids.push(h('div','amw-standings',[],{opaque:true,paint:function(n){if(n.amBoard===key)return;n.amBoard=key;if(root.AimModStandings){root.AimModStandings.render(n,b,'stream');}}}));return kids;
  };
  function bracketPaint(w,data,env){
    return function(c){
      var x=c.getContext&&c.getContext('2d'),t=data.tournament&&data.tournament.tournament;if(!x)return;x.clearRect(0,0,c.width,c.height);if(!t||!t.matches||!t.matches.length)return;
      var d=env.dpr,names={},rounds={},max=0;for(var i=0;i<t.entrants.length;i++)names[t.entrants[i].id]=t.entrants[i].name;
      for(var j=0;j<t.matches.length;j++){var m=t.matches[j];if(m.side!=='winners'&&m.side!=='round_robin'&&m.side!=='swiss')continue;(rounds[m.round]=rounds[m.round]||[]).push(m);max=Math.max(max,m.round);}
      var cw=Math.floor(c.width/Math.max(1,max)),ch=Math.round(38*d*w.font),acc=accentOf(w,env),cell=mix(env.theme.surface,env.theme.text,0.07);
      for(var r=1;r<=max;r++){var list=(rounds[r]||[]).sort(function(a,b){return a.position-b.position;});
        for(var q=0;q<list.length;q++){var mm=list[q],y=Math.round(c.height*(q+0.5)/list.length-ch/2),left=(r-1)*cw+4*d;
          x.fillStyle=cell;x.fillRect(left,y,cw-12*d,ch);x.strokeStyle=mm.state==='live'||mm.state==='veto'?acc:mix(env.theme.surface,env.theme.muted,0.3);x.lineWidth=d;x.strokeRect(left+0.5,y+0.5,cw-12*d-1,ch-1);
          x.font=Math.round(12*d*w.font)+'px Roboto';var sides=[[mm.a,mm.winsA],[mm.b,mm.winsB]];
          for(var z=0;z<2;z++){var id=sides[z][0],won=mm.winner&&mm.winner===id;x.fillStyle=won?acc:id?env.theme.text:env.theme.muted;var lab=id?safe(names[id]||'Player',16):'TBD';x.fillText(lab,left+8*d,y+(15+z*16)*d*w.font);if(id&&(mm.winsA||mm.winsB))x.fillText(String(sides[z][1]),left+cw-30*d,y+(15+z*16)*d*w.font);}
        }}
    };
  }
  R.bracket=function(w,data,env){
    var st=data.tournament||{},m=st.match,live=st.live,kids=[eyebrow('Tournament',w,env,m?safe(m.label,40)+' · best of '+m.bestOf:'')];
    if(!m&&!(st.tournament&&st.tournament.matches&&st.tournament.matches.length)){kids.push(empty(env,'The bracket shows when you play or cast a tournament.'));return kids;}
    var used=30*w.font;
    if(m&&w.opts.match){var pa=live&&live.players&&live.players[0],pb=live&&live.players&&live.players[1];if(pa&&pb&&pa.name!==m.a){var tmp=pa;pa=pb;pb=tmp;}
      kids.push(h('div','amw-versus',[h('div','amw-versus-name',safe(m.a,24)),styled(h('div','amw-versus-score',m.winsA+' – '+m.winsB),{color:accentOf(w,env)}),h('div','amw-versus-name right',safe(m.b,24))]));
      if(pa&&pb)kids.push(h('div','amw-split',[h('div','amw-split-cell',[label('Live',env),h('div','amw-mid',fmt(pa.score))]),h('div','amw-split-cell right',[label('Live',env),h('div','amw-mid',fmt(pb.score))])]));used+=90*w.font;}
    if(w.opts.tree){env.width=Math.max(20,w.w-env.padX*2);kids.push(canvasNode(w,env,bracketPaint(w,data,env),'bracket',Math.max(40,w.h-env.padY*2-used)));}
    return kids;
  };
  R.profile=function(w,data,env){
    var P=data.profile||{},name=safe(P.name||P.handle||'AimMod player',32),acc=accentOf(w,env),initial=name.charAt(0).toUpperCase()||'A',sub=[];
    if(w.opts.tagline)sub.push(safe(w.opts.tagline,60));else if(P.handle)sub.push('@'+safe(P.handle,32));
    if(w.opts.rank&&data.benchmarks&&data.benchmarks.length&&data.benchmarks[0].rank)sub.push(safe(data.benchmarks[0].rank,24)+' · '+safe(data.benchmarks[0].name,30));
    return [h('div','amw-profile',[styled(h('div','amw-avatar',initial),{background:acc,color:dark(acc)?'#ffffff':'#06120d'}),h('div','amw-profile-text',[h('div','amw-profile-name',name),styled(h('div','amw-profile-sub',sub.join('  ·  ')||'AimMod for KovaaK’s'),{color:env.theme.muted})])])];
  };
  R['now-playing']=function(w,data,env){
    var L=data.live||{},S=data.scenario||{},name=L.active&&L.scenario?L.scenario:S.name,kids=[eyebrow(L.active?'Now playing':'Last played',w,env)];
    kids.push(h('div','amw-title big',name?safe(name,60):'Nothing yet'));
    if(w.opts.pb&&known(S.best)&&w.labels)kids.push(styled(h('div','amw-note','Personal best '+fmt(S.best)),{color:env.theme.muted}));return kids;
  };
  R.clock=function(w,data,env){
    var d=new Date(env.now),hh=d.getHours(),mm=d.getMinutes(),ss=d.getSeconds(),suffix='';
    if(!w.opts.h24){suffix=hh<12?' AM':' PM';hh=hh%12||12;}
    var text=(w.opts.h24&&hh<10?'0':'')+hh+':'+(mm<10?'0':'')+mm+(w.opts.seconds?':'+(ss<10?'0':'')+ss:''),kids=[];
    if(w.variant==='expanded')kids.push(eyebrow('Local time',w,env));
    kids.push(h('div','amw-value big',[h('span','amw-num',text),styled(h('span','amw-unit',suffix),{color:env.theme.muted})]));
    if(w.opts.session&&data.session&&known(data.session.startedAt))kids.push(styled(h('div','amw-note','Session '+span((env.now-data.session.startedAt)/1000)),{color:env.theme.muted}));
    return kids;
  };
  R.text=function(w,data,env){
    var o=w.opts,align=o.align==='center'?'center':o.align==='right'?'right':'left',kids=[styled(h('div','amw-text',safe(o.text,160)||' '),{textAlign:align})];
    if(o.caption)kids.push(styled(h('div','amw-note',safe(o.caption,160)),{color:env.theme.muted,textAlign:align}));return kids;
  };
  R.image=function(w,data,env){
    var url=w.opts.url&&/^https:\/\/[^\s"'<>\\()]+$/.test(w.opts.url)?w.opts.url:'';
    if(!url)return [empty(env,'Set an https:// image address in the widget settings.')];
    return [styled(h('div','amw-image',''),{backgroundImage:'url('+url+')',backgroundSize:w.opts.fit==='cover'?'cover':'contain',height:Math.max(10,w.h-env.padY*2)+'px'})];
  };
  // ---- frame ----
  var PAD={x:18,y:14};
  function frame(w,data,env){
    var theme=env.theme,renderer=R[w.type];if(!renderer)return null;
    var bareTypes={image:1,text:1},alpha=w.panel>=0?w.panel:theme.alpha,bg=w.background==='none'?'transparent':rgba(w.background||theme.surface,w.background?(w.panel>=0?w.panel:1):alpha);
    if(w.background===''&&bareTypes[w.type]&&w.panel<0)bg=rgba(theme.surface,alpha);
    var compact=w.variant==='compact',padX=Math.round((compact?13:PAD.x)*w.font),padY=Math.round((compact?10:PAD.y)*w.font);
    env.padX=padX;env.padY=padY;var entry=M.entry(w.type);
    var style={left:w.x+'px',top:w.y+'px',width:w.w+'px',opacity:String(w.opacity),fontSize:(16*w.font).toFixed(2)+'px',color:theme.text,background:bg,
      borderRadius:theme.radius+'px',padding:padY+'px '+padX+'px',border:theme.borders&&w.background!=='none'?'1px solid '+rgba(theme.border,theme.preset==='contrast'?0.9:0.75):'1px solid transparent'};
    if(!entry.auto)style.height=w.h+'px';
    // The standings component draws its own card (the same one as the in-game corner panel).
    if(w.type==='standings'&&data.board){style.background='transparent';style.border='1px solid transparent';style.padding='0px';}
    // Minimal panels are faint: a soft shadow keeps their text readable on bright scenes.
    if(theme.preset==='minimal'||w.background==='none')style.textShadow='0 1px 3px '+rgba('#000000',0.85);
    var v=h('div','amw amw-'+w.type+(compact?' compact':'')+(w.layout==='vertical'?' vertical':''),renderer(w,data,env),{style:style,label:entry.name});v.key=w.type;return v;
  }
  // Renders the widgets of a scene (or a subset) into container, in place.
  function render(container,scene,data,options){
    options=options||{};var doc=options.document||root.document,theme=M.resolveTheme(scene),ctx=options.context||M.context(data),list=[];
    var env={theme:theme,now:options.now||new Date().getTime(),dpr:options.dpr||1,motion:options.motion||null,image:options.image||null,doc:doc};
    for(var i=0;i<scene.widgets.length;i++){var w=scene.widgets[i];if(options.only&&w.id!==options.only)continue;if(!options.all&&!M.shownIn(w,ctx))continue;if(options.all&&!w.visible&&!options.hidden)continue;
      var copyEnv={theme:env.theme,now:env.now,dpr:env.dpr,motion:env.motion,image:env.image,doc:doc,width:0,padX:0,padY:0},f=frame(w,data,copyEnv);if(!f)continue;
      f.key='w:'+w.id+':'+w.type;if(options.only){f.attrs.style.left='0px';f.attrs.style.top='0px';}if(options.decorate)options.decorate(f,w);list.push(f);}
    patch(doc,container,list);return list.length;
  }
  function needsMotion(scene,ctx){var out={path:false,input:false};if(!scene)return out;for(var i=0;i<scene.widgets.length;i++){var w=scene.widgets[i];if(ctx&&!M.shownIn(w,ctx))continue;if(!w.visible)continue;if(w.type==='mouse-path')out.path=true;if(w.type==='input')out.input=true;}return out;}
  // ---- sample data (synthetic: no real players, gear or scores) ----
  function sample(now){
    now=now||new Date().getTime();var t=now/1000,elapsed=38+(t%22),score=812+Math.round(elapsed*21.4),graph=[],recent=[],names=['Synthetic Flick Wall','Sample Tracking Orb','Example Microshot','Demo Strafe Track','Synthetic Flick Wall','Sample Target Switch'];
    for(var i=0;i<18;i++){var r=0.84+0.11*Math.sin(i*1.7)+i*0.006;graph.push({scenario:names[i%names.length],score:Math.round(1000*r),ratio:i===13?1.024:Math.min(0.995,r),pb:i===13});}
    for(var j=0;j<6;j++)recent.push({scenario:names[j],score:[1342,2210,918,3104,1288,1730][j],accuracy:[61,88,72,90,59,77][j],at:now-[2,6,11,17,25,31][j]*60000,pb:j===1,delta:[-3.2,0,1.4,-8.1,-0.6,2.3][j]});
    return {
      live:{available:true,active:true,paused:false,scenario:'Synthetic Flick Wall',score:score,seconds:elapsed,shots:142,hits:101,kills:58,accuracy:71.1,personalBest:2410,opponentScore:2410,opponentName:'Personal best',projectedScore:2463,projectedDelta:53,scorePerMinute:score/elapsed*60,killsPerSecond:1.42,remainingSeconds:60-elapsed,durationSeconds:60,lastTimeToKillSeconds:0.284},
      session:{runs:23,seconds:3720,pbs:2,accuracy:76.4,scenarios:5,bestRatio:1.024,startedAt:now-3720000,graph:graph},
      scenario:{name:'Synthetic Flick Wall',best:2410,attempts:214,average:1318,last:1342},
      recent:recent,
      kovaaks:{available:true,dpi:1600,sens:50,sensScale:'cm/360',cm360:50,fov:103,fovScale:'Overwatch',theme:'Sample Night',crosshair:'sample_dot.png',crosshairScale:1,crosshairColor:'#5dfcc4',crosshairImage:'',hitSounds:['sample-hit-2']},
      peripherals:[{label:'Mouse',value:'Example Mouse V2 Wireless'},{label:'Mousepad',value:'Sample Control Pad XL'},{label:'Keyboard',value:'Demo 65% Hall Effect'},{label:'IEMs',value:'Example Monitors 3'},{label:'Monitor',value:'Sample 27" 360 Hz'}],
      profile:{linked:true,name:'SamplePlayer',handle:'sampleplayer'},
      benchmarks:[{name:'Sample Benchmarks S5',rank:'Diamond',rankIndex:6,progress:0.62,next:'Jade'},{name:'Example Intermediate',rank:'Platinum',rankIndex:4}],
      board:null,tournament:null
    };
  }
  function sampleMotion(now){
    var path=[],t0=now-4000;for(var i=0;i<=240;i++){var tt=t0+i*16.7,s=tt/1000;var yaw=Math.sin(s*1.9)*9+Math.sin(s*5.3)*2.2+(Math.floor(s/1.4)%2?6:-6)*Math.min(1,(s%1.4)*7),pitch=Math.cos(s*1.3)*7+Math.sin(s*3.1)*3.5;path.push([Math.round(tt),yaw,pitch]);}
    var phase=Math.floor(now/450)%6;return {path:path,input:{available:true,w:phase<3,a:phase===1||phase===2,d:phase===4,s:false,shift:false,space:phase===5,ctrl:false,lmb:Math.floor(now/180)%3===0,rmb:false}};
  }
  // A synthetic multiplayer match and tournament for previews in the Match context.
  function sampleMatch(){
    var entrants=[{id:'p1',name:'SampleAlpha'},{id:'p2',name:'DemoBravo'},{id:'p3',name:'ExampleCharlie'},{id:'p4',name:'SampleDelta'}];
    return {board:{kind:'combat',mode:'ffa',title:'Free for all',phase:'live',round:1,rounds:1,fragLimit:20,left:212,rows:[{rank:1,name:'SampleAlpha',frags:14,deaths:6,kd:2.33},{rank:2,name:'You',frags:12,deaths:7,kd:1.71,self:true},{rank:3,name:'DemoBravo',frags:9,deaths:11,kd:0.82},{rank:4,name:'ExampleCharlie',frags:5,deaths:12,kd:0.42}]},
      tournament:{match:{label:'Semifinal',bestOf:3,a:'SampleAlpha',b:'DemoBravo',winsA:1,winsB:0},live:{game:1,scenario:'Synthetic Flick Wall',phase:'live',players:[{name:'SampleAlpha',score:1840},{name:'DemoBravo',score:1712}]},
        tournament:{entrants:entrants,matches:[{side:'winners',round:1,position:0,a:'p1',b:'p2',winsA:1,winsB:0,state:'live'},{side:'winners',round:1,position:1,a:'p3',b:'p4',winsA:2,winsB:1,winner:'p3',state:'done'},{side:'winners',round:2,position:0,a:null,b:'p3',state:'pending'}]}}};
  }
  root.AimModOverlayWidgets={render:render,patch:patch,h:h,frame:frame,renderers:R,needsMotion:needsMotion,sample:sample,sampleMotion:sampleMotion,sampleMatch:sampleMatch,pathPoints:pathPoints,
    format:{short:short,number:fmt,signed:signed,percent:pct,clock:clock,span:span,millis:millis,ago:ago,safe:safe},color:{rgba:rgba,mix:mix,dark:dark}};
})(window);
