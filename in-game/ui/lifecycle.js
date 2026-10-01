(function(root){
  'use strict';
  // Install lifecycle in the workspace: "update ready" toast and banner, the
  // repair prompt, and the Updates section in Settings. All changes to game
  // files happen in the service after KovaaK's closes; this only asks for them.
  var state=null,timer=null,options={},settingsBox=null,pending=null,announced={},busy=false,message='';
  var doc=root.document;
  function node(tag,css,text){var el=doc.createElement(tag);el.className=css||'';if(text!==undefined)el.textContent=text;return el;}
  function clear(el){while(el&&el.firstChild)el.removeChild(el.firstChild);}
  function base(){var p=root.location.pathname;return p.slice(0,p.lastIndexOf('/'));}
  function send(method,path,body,done){
    var xhr=new root.XMLHttpRequest();xhr.open(method,base()+path,true);xhr.timeout=10000;
    if(method==='POST'){xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');}
    var finished=false;function finish(ok,data){if(finished)return;finished=true;done(ok,data);}
    xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;if(xhr.status!==200){finish(false);return;}try{var data=JSON.parse(xhr.responseText);if(!data||!data.update||!data.repair){finish(false);return;}finish(true,data);}catch(e){finish(false);}};
    xhr.onerror=xhr.ontimeout=function(){finish(false);};xhr.send(body?JSON.stringify(body):null);return xhr;
  }
  // Release notes come from the release body (Markdown); show them as text.
  function plain(notes){
    return String(notes||'').replace(/\r/g,'').replace(/^#{1,6}\s*/gm,'').replace(/\*\*|__/g,'').replace(/\[([^\]]+)\]\([^)]*\)/g,'$1').replace(/^\s*[-*]\s+/gm,'• ').replace(/\n{3,}/g,'\n\n').trim();
  }
  function short(text,limit){return text.length>limit?text.slice(0,limit-1).replace(/\s+\S*$/,'')+'…':text;}
  function updateText(u){
    switch(u.state){
      case 'ready':return u.applyOnClose?'AimMod '+u.version+' is ready. It installs when you close KovaaK’s.':'AimMod '+u.version+' is downloaded. Choose “Install when I close KovaaK’s” to use it.';
      case 'checking':return 'Checking for updates…';
      case 'up-to-date':return 'AimMod is up to date.';
      case 'disabled':return 'Automatic updates are off.';
      case 'needs-newer-game':return u.message||'This update needs a newer KovaaK’s.';
      case 'failed':return u.message||'Could not check for updates. AimMod will try again later.';
      case 'unmanaged':return u.message||'Updates are not available for this install.';
      default:return 'AimMod checks for updates when the game starts and every few hours.';
    }
  }
  function act(action,done){if(busy)return;busy=true;render();send('POST','/lifecycle/action',{action:action},function(ok,data){busy=false;if(ok)state=data;message=ok?'':'Could not save that. Please try again.';render();if(done)done(ok);});}
  function save(patch){if(busy)return;busy=true;render();send('POST','/lifecycle/settings',patch,function(ok,data){busy=false;if(ok)state=data;message=ok?'Saved.':'Could not save your settings. Please try again.';render();});}
  function button(label,primary,handler){var b=node('button','button'+(primary?' primary':''),label);b.type='button';b.disabled=busy;b.onclick=handler;return b;}

  function showToast(s){
    var u=s.update,key=u.version;
    if(u.state!=='ready'||!key||announced[key])return;announced[key]=true;
    var box=node('div','lifecycle-toast');box.setAttribute('role','status');
    box.appendChild(node('strong','','Update ready'));
    box.appendChild(node('p','','AimMod '+u.version+(u.applyOnClose?' applies when you close KovaaK’s.':' is downloaded.')));
    var notes=plain(u.notes);if(notes)box.appendChild(node('p','lifecycle-notes',short(notes,320)));
    var row=node('div','lifecycle-actions');
    row.appendChild(button('Details',true,function(){close();if(options.openSettings)options.openSettings();}));
    row.appendChild(button('Dismiss',false,close));box.appendChild(row);
    function close(){if(box.parentNode)box.parentNode.removeChild(box);}
    doc.body.appendChild(box);root.setTimeout(close,15000);
  }
  function renderBanner(){
    var el=options.banner;if(!el)return;clear(el);el.style.display='none';if(!state)return;
    var s=state,line=null;
    function add(text,css){line=node('div','lifecycle-banner '+(css||''));line.appendChild(node('span','',text));el.appendChild(line);el.style.display='block';return line;}
    if(s.last){var r=add(s.last.message,s.last.ok?'':'warn');r.appendChild(button('OK',false,function(){act('dismiss');}));}
    // Developer installs (Install-AimModCore.ps1) change files on purpose: Settings only.
    if(s.repair.interrupted)add('An AimMod install was interrupted. It is undone when you close KovaaK’s.','warn');
    else if(s.repair.needed&&s.installed.managed){
      var p=add(s.repair.requested?'Repair scheduled: AimMod repairs itself when you close KovaaK’s.':'AimMod needs a repair. '+(s.repair.problems[0]||''),'warn');
      if(!s.repair.requested&&s.repair.available)p.appendChild(button('Repair when I close KovaaK’s',true,function(){act('repair');}));
    }
    if(s.update.state==='ready'){var b=add('Update ready: AimMod '+s.update.version+(s.update.applyOnClose?' applies when you close KovaaK’s.':' is downloaded.'));b.appendChild(button('What’s new',false,function(){if(options.openSettings)options.openSettings();}));}
    if(s.game.warning)add(s.game.warning,'warn');
  }
  function renderSettings(){
    var c=settingsBox;if(!c)return;
    var panel=c.querySelector?c.querySelector('.lifecycle-settings'):null;
    if(!panel){panel=node('div','panel settings-card lifecycle-settings');c.appendChild(panel);}clear(panel);
    panel.appendChild(node('h2','','Updates & repair'));
    if(!state){panel.appendChild(node('p','subtle','Loading update status…'));return;}
    var s=state,u=s.update;
    panel.appendChild(node('p','subtle',(s.installed.version?'Installed: AimMod '+s.installed.version+'. ':'')+updateText(u)));
    if(message)panel.appendChild(node('p','notice',message));
    var managed=u.state!=='unmanaged';
    function row(title,description,control){var r=node('div','settings-row'),info=node('div','settings-info');info.appendChild(node('h3','',title));info.appendChild(node('p','subtle',description));r.appendChild(info);if(control)r.appendChild(control);panel.appendChild(r);return r;}
    if(managed){
      var toggle=button(s.settings.autoUpdate?'On':'Off',s.settings.autoUpdate,function(){save({autoUpdate:!s.settings.autoUpdate});});
      toggle.setAttribute('role','switch');toggle.setAttribute('aria-checked',String(s.settings.autoUpdate));toggle.setAttribute('aria-label','Automatic updates');
      row('Automatic updates','Download updates in the background, check every file and install them when you close KovaaK’s. Your history, replays and settings are kept.',toggle);
      var channels=node('div','lifecycle-channels');
      ['stable','beta'].forEach(function(name){var b=button(name==='stable'?'Stable':'Beta',s.settings.channel===name,function(){if(s.settings.channel!==name)save({channel:name});});b.setAttribute('aria-pressed',String(s.settings.channel===name));channels.appendChild(b);});
      row('Update channel','Beta gets new features first and may be less stable.',channels);
      var actions=node('div','lifecycle-actions');
      actions.appendChild(button(u.state==='checking'?'Checking…':'Check for updates',false,function(){message='';act('check',function(){root.setTimeout(load,4000);});}));
      if(u.state==='ready'&&!u.applyOnClose)actions.appendChild(button('Install when I close KovaaK’s',true,function(){act('install');}));
      panel.appendChild(actions);
      if(u.state==='ready'&&u.notes){panel.appendChild(node('h3','lifecycle-heading','What’s new in '+u.version));panel.appendChild(node('p','lifecycle-notes',plain(u.notes)));}
    }
    var repairText=s.repair.requested?'A repair is scheduled for when you close KovaaK’s.':s.repair.needed?'Something is wrong with the install: '+s.repair.problems.join(' '):'Your AimMod install is complete.';
    var repairButton=s.repair.available&&!s.repair.requested?button('Repair when I close KovaaK’s',s.repair.needed,function(){act('repair');}):null;
    row('Repair',repairText+' If AimMod does not load at all after a KovaaK’s update, run Repair-AimMod.cmd from %LOCALAPPDATA%\\AimMod with the game closed.',repairButton);
    if(s.rollback.available)row('Previous version','AimMod '+s.rollback.version+' is kept. To go back, close KovaaK’s and run Repair-AimMod.cmd -Rollback.',null);
    if(s.game.steamBuildId)row('Game build',s.game.tested?'KovaaK’s '+s.game.testedVersion+' (Steam build '+s.game.steamBuildId+'), tested with this AimMod release.':(s.game.warning||'Steam build '+s.game.steamBuildId+'.'),null);
  }
  function render(){renderBanner();renderSettings();}
  function load(){
    if(pending)return;
    pending=send('GET','/lifecycle',null,function(ok,data){pending=null;if(!ok)return;state=data;render();showToast(state);});
  }
  function style(){
    if(doc.getElementById('lifecycle-style'))return;var css=node('style','');css.id='lifecycle-style';
    css.textContent='.lifecycle-banner{display:flex;align-items:center;padding:12px 16px;margin:0 0 12px;border:1px solid #3a7450;border-radius:9px;background:#183a26;color:#dff3e6;font-size:13px}.lifecycle-banner.warn{border-color:#8a6a2c;background:#3a2f17;color:#f5e6c4}.lifecycle-banner>span{flex:1;min-width:0;line-height:1.6}.lifecycle-banner>.button{margin-left:12px;flex-shrink:0}'+
      '.lifecycle-toast{position:absolute;right:30px;bottom:80px;z-index:30;max-width:420px;padding:18px 20px;border:1px solid #4c7b59;border-radius:10px;background:#1d3d29;color:#edf8f1;font-size:13px}.lifecycle-toast strong{display:block;font-size:15px;margin-bottom:6px}.lifecycle-toast p{margin:0 0 10px;line-height:1.6;color:#cfe6d7}'+
      '.lifecycle-notes{white-space:pre-wrap;color:#a9c7b3;font-size:12px;line-height:1.6;max-height:260px;overflow-y:auto}.lifecycle-actions{display:flex;flex-wrap:wrap;margin-top:14px}.lifecycle-actions>.button,.lifecycle-channels>.button{margin-right:8px}.lifecycle-heading{font-size:14px;font-weight:500;margin:20px 0 8px}';
    (doc.head||doc.body).appendChild(css);
  }
  root.AimModLifecycle={
    start:function(opts){options=opts||{};style();load();if(timer)root.clearInterval(timer);timer=root.setInterval(load,30000);},
    visible:function(on){if(timer){root.clearInterval(timer);timer=null;}if(on){load();timer=root.setInterval(load,30000);}else if(pending){pending.abort();pending=null;}},
    renderSettings:function(container){settingsBox=container;message='';renderSettings();load();},
    leaveSettings:function(){settingsBox=null;},
    _plain:plain
  };
})(window);
