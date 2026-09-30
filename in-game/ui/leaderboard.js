(function(root){'use strict';
 var doc=root.document;
 function F(){return root.AimModFormat;}
 function node(tag,text,cls){var e=doc.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;return e;}
 function button(label,cls){var b=node('button',label,cls||'button');b.type='button';return b;}
 function base(){var path=root.location&&root.location.pathname||'';return path.slice(0,path.lastIndexOf('/')+1);}
 function mount(container){if(!container)return;var opened=false,page=null,mode='records',shown=100,request=null,body,status,list;
  var shell=node('div',undefined,'community-panel'),toggle=button('Browse community scores');toggle.setAttribute('aria-expanded','false');var head=node('div',undefined,'community-head'),copy=node('div',undefined,'community-copy');copy.appendChild(node('h2','Community scores'));copy.appendChild(node('p','Scenario records and top scores from AimMod Hub.','subtle'));head.appendChild(copy);head.appendChild(toggle);shell.appendChild(head);container.appendChild(shell);
  function unavailable(){status.textContent='Community scores are unavailable right now. Select Refresh to try again.';}
  function rows(){if(!list)return;while(list.firstChild)list.removeChild(list.firstChild);var data=page?(mode==='records'?page.records:page.topScores)||[]:[];if(!data.length){list.appendChild(node('p',page?'No scores to show yet.':'','community-empty'));return;}
   for(var i=0;i<Math.min(data.length,shown);i++){var r=data[i]||{},row=node('div',undefined,'community-row'),left=node('div',undefined,'community-player'),right=node('div',undefined,'community-score');
    if(mode==='topScores')row.appendChild(node('span','#'+(i+1),'community-rank'));
    left.appendChild(node('strong',typeof r.scenario==='string'?r.scenario:'Unknown scenario'));left.appendChild(node('span',typeof r.player==='string'&&r.player?r.player:'Unknown player'));
    right.appendChild(node('strong',F().number(r.score,2)));right.appendChild(node('span',F().known(r.accuracy)?F().percent(r.accuracy,1)+' accuracy':''));row.appendChild(left);row.appendChild(right);list.appendChild(row);}
   if(data.length>shown){var more=button('Show more scores');more.onclick=function(){shown+=100;rows();};list.appendChild(more);}
  }
  function load(){if(request)return;status.textContent='Loading community scores…';var xhr=new root.XMLHttpRequest();request=xhr;xhr.open('GET',base()+'leaderboard',true);xhr.timeout=15000;
   xhr.onreadystatechange=function(){if(xhr.readyState!==4||request!==xhr)return;request=null;if(xhr.status!==200){unavailable();return;}try{var next=JSON.parse(xhr.responseText);if(!next||typeof next!=='object')throw Error();page=next;var stamp=F().parse(page.retrievedAt);status.textContent=(page.cached?'Saved scores from ':'Updated ')+(stamp?F().dateTime(stamp):'recently');rows();}catch(e){unavailable();}};
   xhr.onerror=xhr.ontimeout=function(){if(request===xhr){request=null;unavailable();}};xhr.send();}
  toggle.onclick=function(){opened=!opened;toggle.setAttribute('aria-expanded',String(opened));toggle.textContent=opened?'Hide community scores':'Browse community scores';if(body){body.style.display=opened?'block':'none';return;}body=node('div',undefined,'community-body');shell.appendChild(body);var toolbar=node('div',undefined,'community-toolbar'),records=button('Scenario records'),scores=button('Top scores'),refresh=button('Refresh');function choose(value){mode=value;shown=100;records.setAttribute('aria-pressed',String(mode==='records'));scores.setAttribute('aria-pressed',String(mode==='topScores'));rows();}records.onclick=function(){choose('records');};scores.onclick=function(){choose('topScores');};refresh.onclick=load;toolbar.appendChild(records);toolbar.appendChild(scores);toolbar.appendChild(refresh);body.appendChild(toolbar);status=node('p','','community-status');status.setAttribute('role','status');body.appendChild(status);list=node('div',undefined,'community-list');body.appendChild(list);choose('records');load();};
 }
 root.AimModLeaderboard={mount:mount};mount(doc.getElementById('account'));
})(window);
