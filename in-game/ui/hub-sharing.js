(function(root){
  'use strict';
  // AimMod Hub sharing card on the Settings page: live activity and run
  // uploads for the linked account. Self-contained: settings.js only calls
  // render/leave. Uses the page's existing settings classes.
  var target=null,request=null,epoch=0,value=null,status=null;
  var keys=['hubLiveActivityEnabled','hubRunUploadsEnabled'];
  var liveStates={
    live:'You’re on the Live page.',
    waiting:'You’ll show on the Live page while KovaaK’s runs.',
    unavailable:'AimMod Hub can’t be reached. AimMod keeps trying.',
    rejected:'AimMod Hub refused this device. Link your account again.',
    preview:'This preview doesn’t share anything with AimMod Hub.',
    starting:'Starting…'
  };
  var uploadStates={
    idle:'New runs are uploaded after each run.',
    uploading:'Uploading a run…',
    unavailable:'AimMod Hub can’t be reached. Runs upload when it’s back.',
    rejected:'AimMod Hub refused this device. Link your account again.',
    preview:'This preview doesn’t share anything with AimMod Hub.',
    starting:'Starting…'
  };
  function node(tag,css,text){var el=root.document.createElement(tag);el.className=css||'';if(text!==undefined)el.textContent=text;return el;}
  function valid(data){if(!data||!data.settings)return false;for(var i=0;i<keys.length;i++)if(typeof data.settings[keys[i]]!=='boolean')return false;return true;}
  function leave(){epoch++;if(request){request.abort();request=null;}target=null;}
  function send(patch,done){
    var ticket=++epoch,xhr=new root.XMLHttpRequest();request=xhr;
    var path=root.location.pathname;path=path.slice(0,path.lastIndexOf('/'));
    xhr.open(patch?'POST':'GET',path+'/hub-sharing',true);xhr.timeout=10000;
    if(patch){xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');}
    var finished=false;
    function finish(ok,data){if(finished)return;finished=true;if(ticket!==epoch||!target)return;request=null;done(ok,data);}
    xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;if(xhr.status!==200){finish(false);return;}try{var data=JSON.parse(xhr.responseText);finish(valid(data),data);}catch(e){finish(false);}};
    xhr.onerror=xhr.ontimeout=function(){finish(false);};xhr.send(patch?JSON.stringify(patch):null);
  }
  function accept(data){value=data.settings;status=data.status&&typeof data.status==='object'?data.status:null;}
  function state(part){return status&&status[part]&&typeof status[part].state==='string'?status[part].state:null;}
  function draw(panel,message){
    while(panel.firstChild)panel.removeChild(panel.firstChild);
    panel.appendChild(node('h2','','AimMod Hub'));
    var linked=!!(status&&status.linked);
    panel.appendChild(node('p','subtle',linked?'What your linked account shares on aimmod.app.':'Link your account on the Account page to share your runs and live activity.'));
    if(message)panel.appendChild(node('p',message==='Saved.'?'saved-note':'notice warn',message));
    if(!value){var retryRow=node('div','actions'),retry=node('button','button primary','Try again');retry.type='button';retry.onclick=function(){load(panel);};retryRow.appendChild(retry);panel.appendChild(retryRow);return;}
    function toggle(key,title,description,part,states){
      var row=node('div','settings-row'),info=node('div','settings-info');info.appendChild(node('h3','',title));info.appendChild(node('p','subtle',description));
      var current=state(part);
      if(linked&&value[key]&&current&&states[current])info.appendChild(node('p','subtle',states[current]));
      row.appendChild(info);
      var on=value[key];row.appendChild(node('span','switch-state',on?'On':'Off'));
      var control=node('button','switch'+(on?' on':''),on?'On':'Off');control.type='button';control.setAttribute('role','switch');control.setAttribute('aria-checked',String(on));control.setAttribute('aria-label',title);control.appendChild(node('span','knob'));
      control.onclick=function(){if(request)return;var patch={};patch[key]=!value[key];var controls=panel.querySelectorAll('button');for(var i=0;i<controls.length;i++)controls[i].disabled=true;send(patch,function(ok,data){if(ok)accept(data);draw(panel,ok?'Saved.':'Couldn’t save. Try again.');});};
      row.appendChild(control);panel.appendChild(row);
    }
    toggle('hubLiveActivityEnabled','Show me on Live activity','Your scenario, live score and session progress while KovaaK’s runs. Turning this off removes you at once.','live',liveStates);
    toggle('hubRunUploadsEnabled','Upload my runs','Adds each run you finish to your Hub profile and leaderboards. Earlier runs aren’t uploaded.','uploads',uploadStates);
  }
  function load(panel){send(null,function(ok,data){if(ok)accept(data);draw(panel,ok?'':'Couldn’t load your Hub settings.');});}
  function render(container){
    leave();target=container;value=null;status=null;
    var panel=node('div','panel settings-card');panel.appendChild(node('p','subtle','Loading Hub settings…'));container.appendChild(panel);load(panel);
  }
  root.AimModHubSharing={render:render,leave:leave};
})(window);
