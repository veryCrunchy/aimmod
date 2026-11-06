(function(root){
  'use strict';
  // Install lifecycle in the workspace: header inbox, the
  // repair prompt, and the Updates section in Settings. All changes to game
  // files happen in the service after KovaaK's closes; this only asks for them.
  var state=null,timer=null,options={},settingsBox=null,pending=null,inboxOpen=false,inboxSignature=null,busy=false,message='';
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
    return String(notes||'').replace(/\r/g,'').replace(/^#{1,6}\s*/gm,'').replace(/\*\*|__/g,'').replace(/\[([^\]]+)\]\([^)]*\)/g,'$1').replace(/^\s*[-*]\s+/gm,'- ').replace(/\n{3,}/g,'\n\n').trim();
  }
  function updateText(u){
    switch(u.state){
      case 'ready':return u.applyOnClose?'AimMod '+u.version+' is ready. It installs when you close KovaaK’s.':'AimMod '+u.version+' is downloaded. Choose “Install when I close KovaaK’s” to use it.';
      case 'checking':return 'Checking for updates…';
      case 'up-to-date':return 'AimMod is up to date.';
      case 'disabled':return 'Automatic updates are off.';
      case 'needs-newer-game':return u.message||'This update needs a newer KovaaK’s.';
      case 'failed':return u.message||'Couldn’t check for updates. AimMod tries again later.';
      case 'unmanaged':return u.message||'Updates are not available for this install.';
      default:return 'AimMod checks for updates when the game starts and every few hours.';
    }
  }
  function act(action,done){if(busy)return;busy=true;render();send('POST','/lifecycle/action',{action:action},function(ok,data){busy=false;if(ok)state=data;message=ok?'':'Could not save that. Please try again.';render();if(done)done(ok);});}
  function save(patch){if(busy)return;busy=true;render();send('POST','/lifecycle/settings',patch,function(ok,data){busy=false;if(ok)state=data;message=ok?'Saved.':'Could not save your settings. Please try again.';render();});}
  function button(label,primary,handler){var b=node('button','button'+(primary?' primary':''),label);b.type='button';b.disabled=busy;b.onclick=handler;return b;}

  function dismiss(key){
    if(key==='result'){act('dismiss');return;}
    if(busy)return;busy=true;
    send('POST','/lifecycle/dismiss',{key:key},function(ok,data){busy=false;if(ok){state=data;message='';}else message='Could not dismiss the notification. Please try again.';render();});
  }
  function openInbox(on){inboxOpen=on;if(options.inbox)options.inbox.style.display=on?'block':'none';if(options.trigger)options.trigger.setAttribute('aria-expanded',String(on));}
  function renderBanner(){
    var el=options.banner;if(!el)return;clear(el);el.style.display='none';if(!state)return;
    var s=state,r=s.repair,line;
    function add(text){line=node('div','lifecycle-banner warn');line.setAttribute('role','status');line.appendChild(node('span','',text));el.appendChild(line);el.style.display='block';}
    if(r.interrupted)add('An install was interrupted. Close KovaaK’s to restore AimMod.');
    else if(r.needed&&s.installed.managed){
      add(r.requested?'Repair scheduled: close KovaaK’s to finish.':'AimMod needs a repair. Close KovaaK’s after scheduling it.');
      if(r.requested)line.appendChild(button('Cancel repair',false,function(){act('cancel-repair');}));
      else if(r.available)line.appendChild(button('Repair when I close KovaaK’s',true,function(){act('repair');}));
      else line.appendChild(button('Repair details',false,function(){if(options.openSettings)options.openSettings();}));
    }
    if(message&&message!=='Saved.'){if(line)line.appendChild(node('span','',message));else add(message);}
  }
  function renderInbox(){
    var box=options.inbox;if(!box)return;var signature=JSON.stringify(state?[state.last,state.update.state,state.update.version,state.update.applyOnClose,state.game,state.dismissedNotices]:null);if(signature===inboxSignature)return;inboxSignature=signature;clear(box);
    box.appendChild(node('h2','inbox-title','Notifications'));
    var count=0;
    function item(key,text,details){
      if(state&&state.dismissedNotices&&state.dismissedNotices.indexOf(key)>=0)return;count++;
      var row=node('div','inbox-item');row.appendChild(node('p','',text));
      var actions=node('div','actions');
      if(details)actions.appendChild(button('What’s new',false,function(){openInbox(false);if(options.openSettings)options.openSettings();}));
      actions.appendChild(button('Dismiss',false,function(){dismiss(key);if(options.trigger&&options.trigger.focus)options.trigger.focus();}));
      row.appendChild(actions);box.appendChild(row);
    }
    if(state){
      var s=state,u=s.update;
      if(s.last)item('result',s.last.message,false);
      if(u.state==='ready')item('update:'+u.version,'Update ready: '+u.version+(u.applyOnClose?'. Restart KovaaK’s to apply.':'. Choose when to install in Settings.'),true);
      if(s.game.warning)item('game:'+s.game.steamBuildId,s.game.warning,false);
    }
    if(!count)box.appendChild(node('p','inbox-empty',state?'You’re all caught up.':'Checking notifications…'));
    if(options.trigger){options.trigger.textContent='Inbox'+(count?' ('+count+')':'');options.trigger.setAttribute('aria-label','Notifications, '+count+' unread');}
    openInbox(inboxOpen);
  }
  function renderSettings(){
    var c=settingsBox;if(!c)return;
    var panel=c.querySelector?c.querySelector('.lifecycle-settings'):null;
    if(!panel){panel=node('div','panel settings-card lifecycle-settings');c.appendChild(panel);}clear(panel);
    panel.appendChild(node('h2','','Updates & repair'));
    if(!state){panel.appendChild(node('p','subtle','Loading update status…'));return;}
    var s=state,u=s.update;
    panel.appendChild(node('p','subtle',(s.installed.version?'Installed: AimMod '+s.installed.version+'. ':'')+updateText(u)));
    if(message)panel.appendChild(node('p',message==='Saved.'?'saved-note':'notice warn',message));
    var managed=u.state!=='unmanaged';
    function row(title,description,control){var r=node('div','settings-row'),info=node('div','settings-info');info.appendChild(node('h3','',title));info.appendChild(node('p','subtle',description));r.appendChild(info);if(control)r.appendChild(control);panel.appendChild(r);return r;}
    if(managed){
      // Same switch and segmented control as the rest of Settings.
      var auto=!!s.settings.autoUpdate,toggle=node('div','lifecycle-switch');toggle.appendChild(node('span','switch-state',auto?'On':'Off'));
      var control=button(auto?'On':'Off',false,function(){save({autoUpdate:!auto});});control.className='switch'+(auto?' on':'');control.appendChild(node('span','knob'));
      control.setAttribute('role','switch');control.setAttribute('aria-checked',String(auto));control.setAttribute('aria-label','Automatic updates');toggle.appendChild(control);
      row('Automatic updates','Updates install when you close KovaaK’s.',toggle);
      var channels=node('div','segmented lifecycle-channels');channels.setAttribute('role','radiogroup');channels.setAttribute('aria-label','Update channel');
      ['stable','beta'].forEach(function(name){var b=button(name==='stable'?'Stable':'Beta',s.settings.channel===name,function(){if(s.settings.channel!==name)save({channel:name});});b.setAttribute('role','radio');b.setAttribute('aria-checked',String(s.settings.channel===name));channels.appendChild(b);});
      row('Update channel','Beta gets new features first and may be less stable.',channels);
      var actions=node('div','lifecycle-actions');
      actions.appendChild(button(u.state==='checking'?'Checking…':'Check for updates',false,function(){message='';act('check',function(){root.setTimeout(load,4000);});}));
      if(u.state==='ready'&&!u.applyOnClose)actions.appendChild(button('Install when I close KovaaK’s',true,function(){act('install');}));
      panel.appendChild(actions);
      if(u.state==='ready'&&u.notes){panel.appendChild(node('h3','lifecycle-heading','What’s new in '+u.version));panel.appendChild(node('p','lifecycle-notes',plain(u.notes)));}
    }
    var repairText=s.repair.requested?'A repair is scheduled for when you close KovaaK’s.':s.repair.needed?'Something is wrong with the install: '+s.repair.problems.join(' '):'No problems found.';
    var repairButton=s.repair.requested?button('Cancel repair',false,function(){act('cancel-repair');}):s.repair.available?button('Repair when I close KovaaK’s',s.repair.needed,function(){act('repair');}):null;
    row('Repair',repairText+' If AimMod doesn’t load after a KovaaK’s update, close the game and run Repair-AimMod.cmd from %LOCALAPPDATA%\\AimMod.',repairButton);
    if(s.rollback.available)row('Previous version','AimMod '+s.rollback.version+' is kept. To go back, close KovaaK’s and run Repair-AimMod.cmd -Rollback.',null);
    if(s.game.steamBuildId)row('Game build',s.game.tested?'KovaaK’s '+s.game.testedVersion+' (Steam build '+s.game.steamBuildId+'), tested with this AimMod release.':(s.game.warning||'Steam build '+s.game.steamBuildId+'.'),null);
  }
  function render(){renderBanner();renderInbox();renderSettings();}
  function load(){
    if(pending)return;
    pending=send('GET','/lifecycle',null,function(ok,data){pending=null;if(!ok)return;state=data;render();});
  }
  function style(){
    if(doc.getElementById('lifecycle-style'))return;var css=node('style','');css.id='lifecycle-style';
    css.textContent='.lifecycle-banner{display:flex;align-items:center;padding:12px 16px;margin:0 0 12px;border:1px solid #2c6b52;border-left:3px solid #27e4a1;border-radius:6px;background:#173b2f;color:#dcebe3;font-size:13px}.lifecycle-banner.warn{border-color:#6b5426;border-left-color:#f0b45a;background:#3a2f17;color:#f5e6c4}.lifecycle-banner>span{flex:1;min-width:0;line-height:1.6}.lifecycle-banner>.button{margin-left:12px;flex-shrink:0}'+
      '.lifecycle-notes{white-space:pre-wrap;color:#a7bab0;font-size:12px;line-height:1.6;max-height:260px;overflow-y:auto}.lifecycle-actions{display:flex;flex-wrap:wrap;margin-top:14px}.lifecycle-actions>.button{margin:0 8px 0 0}.lifecycle-actions>.button+.button{margin-left:0}.lifecycle-channels{margin:0}.lifecycle-channels>.button+.button{margin-left:0}.lifecycle-switch{display:flex;flex-direction:row;align-items:center;flex-shrink:0}.lifecycle-heading{font-size:14px;font-weight:500;margin:20px 0 8px}';
    (doc.head||doc.body).appendChild(css);
  }
  root.AimModLifecycle={
    start:function(opts){options=opts||{};if(options.trigger){options.trigger.onclick=function(){openInbox(!inboxOpen);};}if(options.inbox){options.inbox.onkeydown=function(e){if(e.key==='Escape'||e.keyCode===27){openInbox(false);if(options.trigger)options.trigger.focus();e.stopPropagation();}};}style();renderInbox();load();if(timer)root.clearInterval(timer);timer=root.setInterval(load,30000);},
    visible:function(on){if(timer){root.clearInterval(timer);timer=null;}if(on){load();timer=root.setInterval(load,30000);}else {openInbox(false);if(pending){pending.abort();pending=null;}}},
    renderSettings:function(container){settingsBox=container;message='';renderSettings();load();},
    leaveSettings:function(){settingsBox=null;},
    closeInbox:function(){openInbox(false);},
    _plain:plain
  };
})(window);
