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
  function render(message){
    if(!container)return;while(container.firstChild)container.removeChild(container.firstChild);
    var panel=node('div','panel settings-card');panel.appendChild(node('h2','','Capture & privacy'));
    panel.appendChild(node('p','subtle','Choose what AimMod records and downloads.'));
    if(message)panel.appendChild(node('p','notice',message));
    if(!value){var retry=node('button','button','Try again');retry.onclick=load;panel.appendChild(retry);container.appendChild(panel);return;}
    function toggle(key,title,description){
      var row=node('div','settings-row'),info=node('div','settings-info');info.appendChild(node('h3','',title));info.appendChild(node('p','subtle',description));row.appendChild(info);
      var control=node('button','button'+(value[key]?' primary':''),value[key]?'On':'Off');control.type='button';control.setAttribute('role','switch');control.setAttribute('aria-checked',String(value[key]));control.setAttribute('aria-label',title);
      control.onclick=function(){if(request)return;var patch={};patch[key]=!value[key];var controls=container.querySelectorAll('button');for(var i=0;i<controls.length;i++)controls[i].disabled=true;send(patch,function(ok,data){if(ok)value=data;render(ok?'Saved.':'Could not save your settings. Please try again.');});};row.appendChild(control);panel.appendChild(row);
    }
    toggle('replayRecordingEnabled','Record replays','Save new runs for in-game playback. Turning this off discards an unfinished recording; saved replays are kept.');
    toggle('hubHistoryEnabled','Download Hub history','Keep your linked account’s history up to date. Turning this off pauses downloads; local runs and previously saved Hub history stay available.');
    container.appendChild(panel);
    if(root.AimModHistoryImport)root.AimModHistoryImport.render(container);
    var storage=node('div','panel settings-card');storage.appendChild(node('h2','','Your replay library'));storage.appendChild(node('p','subtle','Manage favorites, export a copy, or delete individual replays from Replays. Exports are saved in Documents / AimMod / Replays.'));if(root.AimModWorkspace){var open=node('button','button','Open replays');open.type='button';open.onclick=function(){root.AimModWorkspace.open('replays');};storage.appendChild(open);}container.appendChild(storage);
  }
  function load(){send(null,function(ok,data){if(ok)value=data;render(ok?'':'Could not load your settings. Please try again.');});}
  root.AimModSettings={enter:function(element){leave();container=element;value=null;if(container){container.textContent='Loading settings…';load();}},leave:leave};
})(window);
