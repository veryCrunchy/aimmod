(function(root){
  'use strict';
  var container=null,request=null,generation=0,value=null;
  function node(tag,css,text){var el=root.document.createElement(tag);el.className=css||'';if(text!==undefined)el.textContent=text;return el;}
  function leave(){if(root.AimModHistoryImport)root.AimModHistoryImport.leave();generation++;if(request){request.abort();request=null;}container=null;}
  function send(patch,done){
    var ticket=++generation,xhr=new root.XMLHttpRequest();request=xhr;
    var path=root.location.pathname;path=path.slice(0,path.lastIndexOf('/'));
    xhr.open(patch?'POST':'GET',path+'/settings',true);xhr.timeout=10000;
    if(patch){xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');}
    var finished=false;
    function finish(ok,data){if(finished)return;finished=true;if(ticket!==generation||!container)return;request=null;done(ok,data);}
    xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;if(xhr.status!==200){finish(false);return;}try{var data=JSON.parse(xhr.responseText);if(typeof data.replayRecordingEnabled!=='boolean'||typeof data.hubHistoryEnabled!=='boolean'){finish(false);return;}finish(true,data);}catch(e){finish(false);}};
    xhr.onerror=xhr.ontimeout=function(){finish(false);};xhr.send(patch?JSON.stringify(patch):null);
  }
  // A real on/off switch: the button text stays "On"/"Off" for assistive tech.
  function toggleSwitch(on,title,action){var control=node('button','switch'+(on?' on':''),on?'On':'Off');control.type='button';control.setAttribute('role','switch');control.setAttribute('aria-checked',String(on));control.setAttribute('aria-label',title);control.appendChild(node('span','knob'));control.onclick=action;return control;}
  function render(message,ok){
    if(!container)return;while(container.firstChild)container.removeChild(container.firstChild);
    var panel=node('div','panel settings-card');panel.appendChild(node('h2','','Capture & privacy'));
    panel.appendChild(node('p','subtle','Choose what AimMod records and downloads.'));
    if(!value){if(message)panel.appendChild(node('p','notice',message));var retryRow=node('div','actions');var retry=node('button','button primary','Try again');retry.type='button';retry.onclick=load;retryRow.appendChild(retry);panel.appendChild(retryRow);container.appendChild(panel);return;}
    function toggle(key,title,description){
      var row=node('div','settings-row'),info=node('div','settings-info');info.appendChild(node('h3','',title));info.appendChild(node('p','subtle',description));row.appendChild(info);
      row.appendChild(node('span','switch-state',value[key]?'On':'Off'));
      row.appendChild(toggleSwitch(value[key],title,function(){if(request)return;var patch={};patch[key]=!value[key];var controls=container.querySelectorAll('button');for(var i=0;i<controls.length;i++)controls[i].disabled=true;send(patch,function(ok,data){if(ok)value=data;render(ok?'Saved.':'Could not save your settings. Please try again.',ok);});}));panel.appendChild(row);
    }
    toggle('replayRecordingEnabled','Record replays','Save new runs for in-game playback. Turning this off discards an unfinished recording; saved replays are kept.');
    toggle('hubHistoryEnabled','Download Hub history','Keep your linked account’s history up to date. Local runs and saved Hub history stay available when this is off.');
    if(message)panel.appendChild(node('p',ok?'saved-note':'notice',message));
    // Two columns at wide sizes: preferences and library on the left, import on the right.
    var columns=node('div','settings-columns'),left=node('div','settings-col'),right=node('div','settings-col');columns.appendChild(left);columns.appendChild(right);container.appendChild(columns);
    left.appendChild(panel);
    if(root.AimModHistoryImport)root.AimModHistoryImport.render(right,typeof value.statsFolder==='string'?value.statsFolder:'');
    var storage=node('div','panel settings-card');storage.appendChild(node('h2','','Replay library'));storage.appendChild(node('p','subtle','Favorite, export or delete replays from Replays. Exports are saved in Documents / AimMod / Replays.'));if(root.AimModWorkspace){var openRow=node('div','actions');var open=node('button','button','Open replays');open.type='button';open.onclick=function(){root.AimModWorkspace.open('replays');};openRow.appendChild(open);storage.appendChild(openRow);}left.appendChild(storage);
    if(root.AimModDeveloper)root.AimModDeveloper.renderSettings(left);
  }
  function load(){send(null,function(ok,data){if(ok)value=data;render(ok?'':'Could not load your settings. Please try again.');});}
  root.AimModSettings={enter:function(element){leave();container=element;value=null;if(container){container.textContent='';var loading=node('div','panel settings-card');loading.appendChild(node('p','subtle','Loading settings…'));container.appendChild(loading);load();}},leave:leave};
})(window);
