(function(root){
 'use strict';var target=null,pending=null,epoch=0;
 function node(tag,text,css){var n=root.document.createElement(tag);if(text!==undefined)n.textContent=text;if(css)n.className=css;return n;}
 function count(value){return typeof value==='number'&&isFinite(value)&&value>=0?Math.round(value):null;}
 function plural(value,one,many){return value+' '+(value===1?one:many);}
 // Summaries use only the counts the worker returns; nothing is inferred.
 function summary(data){var imported=count(data&&data.imported),skipped=count(data&&data.skipped),invalid=count(data&&data.invalid);if(imported===null)return null;var parts=[plural(imported,'run added','runs added')];if(skipped)parts.push(plural(skipped,'already saved','already saved'));if(invalid)parts.push(plural(invalid,'file could not be read','files could not be read'));return parts.join(' · ')+(imported===0&&!skipped?'. No KovaaK’s stats files were found in this folder.':'.');}
 function failure(status){return status===400?'Enter the full folder path, including the drive letter.':status===422?'This folder could not be opened. Check that it exists and try again.':status===403||status===415?'The import was refused. Restart AimMod and try again.':'Could not import this folder. Check its location and try again.';}
 function leave(){epoch++;if(pending)pending.abort();pending=null;target=null;}
 function render(container,defaultFolder){leave();target=container;var panel=node('div',undefined,'panel settings-card');panel.appendChild(node('h2','Import score history'));panel.appendChild(node('p','From your KovaaK’s stats folder (FPSAimTrainer / stats). Runs you already have are skipped.','subtle'));panel.appendChild(node('span','Stats folder','import-label'));var input=node('input');input.type='text';input.setAttribute('aria-label','Stats folder');input.value=typeof defaultFolder==='string'?defaultFolder:'';var fieldBox=root.AimModFormat&&root.AimModFormat.field?root.AimModFormat.field(input,'Full path to your stats folder','import-field'):input;panel.appendChild(fieldBox);var row=node('div',undefined,'actions import-actions');var button=node('button','Import runs','button primary');button.type='button';var status=node('p','','subtle');status.setAttribute('role','status');
  // Import stays disabled until there is a folder to read.
  function ready(){if(!pending)button.disabled=!input.value.trim();}input.oninput=ready;
  button.onclick=function(){if(pending)return;if(!input.value.trim()){status.textContent='Enter your stats folder first.';if(input.focus)input.focus();return;}var ticket=epoch,x=new root.XMLHttpRequest();pending=x;button.disabled=true;input.disabled=true;status.textContent='Importing runs… This can take a minute for large folders.';var base=root.location.pathname;base=base.slice(0,base.lastIndexOf('/'));x.open('POST',base+'/history-import',true);x.timeout=120000;x.setRequestHeader('X-AimMod-UI','1');x.setRequestHeader('Content-Type','application/json');var done=false;
   function finish(code,data){if(done)return;done=true;if(ticket!==epoch||!target)return;pending=null;input.disabled=false;ready();var text=code===200?summary(data):null;status.textContent=text||failure(code===200?0:code);}
   x.onreadystatechange=function(){if(x.readyState!==4)return;var data=null;try{data=JSON.parse(x.responseText);}catch(e){data=null;}finish(x.status,data);};x.onerror=function(){finish(0,null);};x.ontimeout=function(){finish(0,null);};x.send(JSON.stringify({directory:input.value.trim()}));};
  input.onkeydown=function(e){if(e&&e.keyCode===13){if(e.preventDefault)e.preventDefault();button.onclick();}};
  row.appendChild(button);panel.appendChild(row);panel.appendChild(status);ready();container.appendChild(panel);}
 root.AimModHistoryImport={render:render,leave:leave};
})(window);
