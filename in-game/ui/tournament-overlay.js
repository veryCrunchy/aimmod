// OBS browser source for casters: the live tournament match (players, series
// score, live scores) and a small bracket. Read-only; polls tournament-state
// on the overlay host every second. No account data reaches this page.
(function(root){
  'use strict';
  var doc=root.document,base=root.location.pathname.slice(0,root.location.pathname.lastIndexOf('/'));
  function el(id){return doc.getElementById(id);}
  function text(id,value){var n=el(id);if(n)n.textContent=value;}
  function num(v){if(typeof v!=='number'||!isFinite(v))return '';var s=String(Math.round(v)),out='';for(var i=0;i<s.length;i++){if(i&&(s.length-i)%3===0&&s.charAt(i-1)!=='-')out+=',';out+=s.charAt(i);}return out;}
  function render(state){
    var bar=el('bar'),m=state&&state.match,live=state&&state.live;
    if(!m){bar.style.display='none';}
    else{
      bar.style.display='block';
      text('label',m.label+' · best of '+m.bestOf);
      text('game',live?'Game '+(live.game+1)+(live.scenario?' · '+live.scenario:'')+' · '+live.phase:'');
      text('a',m.a);text('b',m.b);text('series',m.winsA+' – '+m.winsB);
      var pa=live&&live.players[0],pb=live&&live.players[1];
      // Live players arrive in lobby order; match them to the sides by name.
      if(pa&&pb&&pa.name!==m.a){var t=pa;pa=pb;pb=t;}
      text('la',pa&&pa.score!==null?num(pa.score):'');text('lb',pb&&pb.score!==null?num(pb.score):'');
      el('la').className='live'+(pa&&pb&&pa.score>pb.score?' lead':'');el('lb').className='live'+(pa&&pb&&pb.score>pa.score?' lead':'');
    }
    drawBracket(state&&state.tournament);
  }
  function drawBracket(t){
    var c=el('bracket');if(!t||!t.matches||!t.matches.length){c.style.display='none';return;}
    c.style.display='block';var x=c.getContext('2d');if(!x)return;
    var names={};for(var i=0;i<t.entrants.length;i++)names[t.entrants[i].id]=t.entrants[i].name;
    var rounds={},max=0;for(var j=0;j<t.matches.length;j++){var m=t.matches[j];if(m.side!=='winners'&&m.side!=='round_robin'&&m.side!=='swiss')continue;(rounds[m.round]=rounds[m.round]||[]).push(m);max=Math.max(max,m.round);}
    var cw=Math.floor((c.width-10)/Math.max(1,max)),ch=30;
    x.clearRect(0,0,c.width,c.height);x.fillStyle='#0f1513';x.fillRect(0,0,c.width,c.height);
    for(var r=1;r<=max;r++){
      var list=(rounds[r]||[]).sort(function(a,b){return a.position-b.position;});
      for(var q=0;q<list.length;q++){
        var mm=list[q],y=(c.height-20)*(q+0.5)/list.length-ch/2+10,left=(r-1)*cw+5;
        x.fillStyle='#17201c';x.fillRect(left,y,cw-10,ch);x.strokeStyle=mm.state==='live'||mm.state==='veto'?'#27e4a1':'#2a3631';x.lineWidth=1;x.strokeRect(left+0.5,y+0.5,cw-11,ch-1);
        x.font='11px Roboto';
        var sides=[[mm.a,mm.winsA],[mm.b,mm.winsB]];
        for(var z=0;z<2;z++){var id=sides[z][0],won=mm.winner&&mm.winner===id;x.fillStyle=won?'#27e4a1':id?'#b9c8c1':'#5d6d66';var label=id?(names[id]||'Player'):'TBD';if(label.length>14)label=label.slice(0,13)+'…';x.fillText(label+(id&&(mm.winsA||mm.winsB)?'  '+sides[z][1]:''),left+6,y+12+z*13);}
      }
    }
  }
  function poll(){
    var xhr=new root.XMLHttpRequest(),finished=false;xhr.open('GET',base+'/tournament-state',true);xhr.timeout=1500;
    function done(state){if(finished)return;finished=true;render(state);root.setTimeout(poll,1000);}
    xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;var data=null;try{data=JSON.parse(xhr.responseText);}catch(e){data=null;}done(xhr.status===200?data:null);};
    xhr.onerror=xhr.ontimeout=function(){done(null);};xhr.send(null);
  }
  poll();
})(window);
