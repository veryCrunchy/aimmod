// Layout and flow regressions found in the multiplayer UX pass. Synthetic identities only.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
function setup(){
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.value='';this.className='';this.scrollTop=0;}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(k,v){this.attrs[k]=String(v);}getAttribute(k){return this.attrs[k];}addEventListener(){}
    getElementsByTagName(t){return walk(this).filter(e=>e!==this&&e.tag===t);}getContext(){return null;}focus(){}setSelectionRange(){}}
  function walk(e){return [e,...e.children.flatMap(walk)];}
  const requests=[],scroller=new El('div'),container=scroller.appendChild(new El('section'));
  class Xhr{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}
    finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}
  const window={document:{createElement:t=>new El(t)},XMLHttpRequest:Xhr,location:{pathname:'/private/ui'}};
  const context=vm.createContext({window,setTimeout:()=>1,clearTimeout:()=>{},Date});
  require('./test-format.cjs').loadFormat(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname,'multiplayer.js'),'utf8'),context);
  const all=()=>walk(container);
  return {api:window.AimModMultiplayer,container,scroller,requests,all,text:()=>all().map(e=>e.textContent||'').join('|'),
    button:label=>all().find(e=>e.tag==='button'&&e.textContent===label),find:css=>all().find(e=>(' '+e.className+' ').indexOf(' '+css+' ')>=0),
    last:()=>requests[requests.length-1],open(v){this.api.enter(this.container);this.requests[this.requests.length-1].finish(200,v);return this;}};
}
const base={v:1,now:1000,transport:{kind:'steam',online:true},simulation:false,self:{id:'p1',name:'Synthetic One'},joining:null,prefs:{hotkey:'F7',sounds:false},
  friends:{source:'steam',items:[{id:'f1',name:'Synthetic Friend',status:'aimmod',detail:'Playing',joinable:false,spectatable:true}]},invites:[],recent:[],library:{available:true,scenarios:3},notice:null,lobby:null};
const settings={mode:'score-race',scenario:{name:'Synthetic Scenario',hash:'0123456789abcdef',map:'synthetic_map',timeLimit:60},mapOverride:null,maxPlayers:4,spectators:false,rounds:1,firstTo:3,timeLimit:null,
  weapon:{preset:'default'},movement:{preset:'default'},character:{preset:'default'},targetSpeed:1,targetSize:1,privacy:'friends',countdown:5,lateJoin:false,autoStart:false,voting:true};
function member(id,name,extra){return Object.assign({id,name,role:'player',ready:false,ping:40,scenario:'ok',map:'ok',profiles:'none',connection:'connected',link:'relay',joinedAt:1,simulated:false},extra||{});}
function lobby(extra){return Object.assign({id:'l1',code:'ABCDEF',hostId:'p1',settings,members:[member('p1','Synthetic One',{link:'local'}),member('p2','Synthetic Two')],match:null,chat:[],
  self:'p1',isHost:true,blockers:[{code:'ready',text:'Synthetic Two isn’t ready.'}],content:{scenario:'ok',map:'ok',profiles:'none'},simulated:false,generated:null,round:null},extra||{});}
function view(extra){return Object.assign({},base,extra||{});}
function order(s,classes){const all=s.all();return classes.map(c=>all.findIndex(e=>(' '+e.className+' ').indexOf(' '+c+' ')>=0));}

test('layout: the open-slot circle resets the shared .empty padding, mode text is never clipped, key pickers wrap',()=>{
  const css=fs.readFileSync(path.join(__dirname,'multiplayer.css'),'utf8');
  const rule=sel=>{const i=css.indexOf(sel+'{');assert.ok(i>=0,sel);return css.slice(i,css.indexOf('}',i));};
  assert.match(rule('.mp-avatar.empty'),/padding:0/,'index.html styles .empty with 40px padding');
  assert.doesNotMatch(css,/\.mp-mode span\{[^}]*(;|\{)height:/,'mode descriptions grow instead of being cut');
  assert.match(rule('.mp-setting'),/flex-wrap:wrap/);assert.match(rule('.mp-setting>.segmented'),/flex-wrap:wrap/);
});
test('lobby: Ready / Start come before the player list, chat sits in the main column and the look picker last',()=>{
  const s=setup().open(view({lobby:lobby()}));
  const [start,players,chat]=order(s,['mp-start','mp-players','mp-chat']);
  assert.ok(start>=0&&start<players&&players<chat,'start bar, then players, then chat');
  const main=s.find('mp-main'),side=s.find('mp-side');
  assert.ok(main.children.some(c=>/mp-chat/.test(c.className)),'chat is in the main column');
  assert.match(side.children[side.children.length-1].className,/mp-look/,'your look is the last side panel');
  assert.ok(!s.button('Spectate'),'the lobby invite list never offers Spectate');assert.ok(s.button('Invite'));
});
test('toasts and the invite card follow the scroll position, so they are never off screen',()=>{
  const s=setup();s.scroller.scrollTop=640;s.open(view());
  s.all().filter(e=>e.tag==='button'&&e.textContent==='Join')[0].onclick();
  const t=s.find('mp-toast');assert.equal(t.style.display,'block');assert.equal(t.textContent,'Room codes are six letters and numbers.');assert.equal(t.style.top,'640px');
  const s2=setup();s2.scroller.scrollTop=500;s2.open(view({invites:[{id:'i1',fromName:'Synthetic Host',kind:'invite',summary:null,at:900,compatible:true}]}));
  assert.equal(s2.find('mp-modal').style.paddingTop,'570px');
});
test('an invite from a different AimMod version explains why and offers no Accept',()=>{
  const s=setup().open(view({invites:[{id:'i2',fromName:'Synthetic Host',kind:'invite',summary:null,at:900,compatible:false}]}));
  assert.ok(!s.button('Accept'),'accepting would only fail');assert.ok(s.text().includes('Synthetic Host has a different AimMod version'));
  s.button('Close').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'decline-invite',id:'i2'});
  const r=setup().open(view({invites:[{id:'i3',fromName:'Synthetic Host',kind:'request',summary:null,at:900,compatible:true}]}));
  assert.ok(r.text().includes('Join request'));assert.ok(r.button('Let them in'));
});
test('settings editor: title and Done come first, advanced settings fold away unless one is changed',()=>{
  const lib={available:true,scenarios:[],maps:[],weapons:[],characters:[],presets:[]};
  const s=setup().open(view({lobby:lobby()}));s.button('Edit').onclick();s.requests.find(r=>/part=library/.test(r.url)).finish(200,lib);
  const [title,more,setups]=order(s,['mp-editor-top','mp-more','mp-setups']);assert.ok(title>=0&&title<more&&more<setups,'title, then More options, then saved setups');
  assert.ok(s.button('Done'));assert.ok(!s.text().includes('Target speed'),'targets are folded away by default');
  assert.ok(s.text().includes('Scenario map · Scenario loadout · Countdown 5 s · No spectators'),'the folded panel says what it holds');
  s.button('Show').onclick();assert.ok(s.text().includes('Target speed'));
  const locks=s.all().filter(e=>e.className==='mp-lock');assert.equal(locks.length,1,'the score race reason is said once');
  assert.equal(s.all().filter(e=>e.textContent==='Fixed in score race.').length,1,'only the basics row repeats the lock; the sections rely on the one reason');
  s.button('Hide').onclick();assert.ok(!s.text().includes('Target speed'));
  // A changed advanced setting is never hidden.
  const ffa=Object.assign({},settings,{mode:'ffa-rounds',targetSpeed:1.5});
  const t=setup().open(view({lobby:lobby({settings:ffa})}));t.button('Edit').onclick();
  assert.ok(t.text().includes('Target speed'),'opens by itself when targets are changed');assert.ok(!t.all().some(e=>e.className==='mp-lock'));
});
test('home: personal multiplayer settings fold to a summary, but key conflicts still show',()=>{
  const s=setup().open(view({prefs:{hotkey:'F6',sounds:true,spectatePrivacy:'ask'},keys:{clip:'F8',taken:[],conflicts:['F6 is also KovaaK’s reset key.']}}));
  assert.ok(s.text().includes('Hotkey F6 · Sounds on · Spectating asks you first'));assert.ok(!s.text().includes('Ready when I join'));
  assert.ok(s.text().includes('F6 is also KovaaK’s reset key.'),'a conflict is never folded away');
  s.button('Change').onclick();assert.ok(s.text().includes('Ready when I join'));s.button('Done').onclick();assert.ok(!s.text().includes('Ready when I join'));
});
test('wording: no bridge or hash jargon, and small files read in KB',()=>{
  const off=setup().open(view({transport:{kind:'none',online:false},friends:{source:'unavailable',items:[]}}));
  assert.doesNotMatch(off.text(),/bridge|AimModSteam/,'offline copy names Steam, not internals');assert.ok(off.text().includes('Room codes still work.'));
  const files=[{kind:'scenario',name:'Synthetic Scenario.sce',size:40000},{kind:'map',name:'synthetic_map.json',size:5000000}];
  const g=setup().open(view({lobby:lobby({self:'p2',isHost:false,content:{scenario:'missing',map:'missing',profiles:'none'},download:{view:{state:'ready',source:'host',total:5040000,packed:1200000,done:0,speed:0,files},conflicts:[]}})}));
  assert.ok(g.text().includes('39 KB'),'not 0 MB');assert.doesNotMatch(g.text(),/hashes|#0123456/,'no hashes in the lobby');
});
test('states: the service error says what to do and retries on demand; the rejoin offer can be put away',()=>{
  const s=setup();s.api.enter(s.container);assert.ok(s.find('mp-spinner'),'loading shows a spinner');
  s.requests[0].finish(500,null);assert.ok(s.text().includes('restart KovaaK’s'));const before=s.requests.length;s.button('Try again').onclick();assert.equal(s.requests.length,before+1);assert.equal(s.last().url,'/private/multiplayer');
  const r=setup().open(view({rejoin:{hostName:'Synthetic Host',minutes:3}}));assert.ok(r.text().includes('Rejoin Synthetic Host’s lobby?'));
  r.button('Not now').onclick();assert.ok(!r.text().includes('Rejoin Synthetic Host’s lobby?'));
});
test('match: ending a match looks destructive, and spectate buttons keep long names short',()=>{
  const ids=['p1','p2'],res=[{memberId:'p2',name:'Synthetic Two',place:1,score:900,accuracy:80,points:1,status:'finished',disputed:false}];
  const m={id:'m1',phase:'round',mode:'ffa-rounds',scenario:'Synthetic Scenario',timeLimit:60,round:1,totalRounds:3,startsAt:0,nextAt:5000,players:ids,live:[],rounds:[{round:1,results:res,winnerId:'p2'}],standings:[],winnerId:null,rematch:[]};
  const s=setup().open(view({lobby:lobby({match:m})}));assert.match(s.button('End match').className,/danger/);
  const w=setup().open(view({watch:{peer:'f9',name:'Synthetic Friend',scenario:'Synthetic A',state:'watching',message:'',score:null,others:[{id:'f1',name:'Synthetic Friend With A Very Long Name'}]}}));
  assert.ok(w.button('Switch to Synthetic Friend Wi…'),'clipped to 20 characters');
});
test('live: the round label shows once and the host ends the match from the scores panel',()=>{
  const m={id:'m1',phase:'live',mode:'ffa-rounds',scenario:'Synthetic Scenario',timeLimit:60,round:2,totalRounds:3,startsAt:0,players:['p1','p2'],
    live:[{memberId:'p1',score:900,seconds:20,status:'playing'},{memberId:'p2',score:800,seconds:20,status:'playing'}],rounds:[],standings:[],winnerId:null,rematch:[]};
  const s=setup().open(view({lobby:lobby({match:m})}));
  assert.equal(s.all().filter(e=>e.textContent==='Round 2 of 3').length,1);
  const end=s.button('End match');let el=end;while(el&&!/mp-live/.test(el.className))el=el.parentNode;assert.ok(el,'End match sits in the live panel');
});
test('settings editor: the settings that define a mode are basics, the rest stay under More options',()=>{
  function edit(extra){const s=setup().open(view({lobby:lobby({settings:Object.assign({},settings,extra)})}));s.button('Edit').onclick();return s;}
  const dm=edit({mode:'deathmatch',fragLimit:30,timeLimit:600});
  assert.ok(dm.text().includes('Frag limit')&&dm.text().includes('Match length'),'deathmatch basics');assert.ok(!dm.text().includes('Lifesteal'));assert.ok(!dm.text().includes('Target speed'),'targets stay folded');
  const frag=dm.all().find(e=>e.attrs&&e.attrs['aria-label']==='Increase frag limit');frag.onclick();assert.deepEqual(JSON.parse(dm.last().body),{action:'settings',settings:{fragLimit:31}});
  assert.equal(dm.button('10 min').attrs['aria-checked'],'true');
  const vamp=edit({mode:'vampiric',maxPlayers:2,lifesteal:75});assert.ok(vamp.text().includes('Lifesteal')&&vamp.text().includes('75 %'));
  const track=edit({mode:'tracking-duel',maxPlayers:2,rounds:2,timeLimit:15});assert.ok(track.text().includes('Rounds')&&track.text().includes('Require fire')&&track.text().includes('Round length'));assert.ok(!track.text().includes('Time limit'));
  assert.ok(track.text().includes('Always one against one in this mode.'));
  const sum=setup().open(view({lobby:lobby({settings:Object.assign({},settings,{mode:'vampiric',fragLimit:12,lifesteal:60,timeLimit:300})})}));
  assert.ok(sum.text().includes('12 kills')&&sum.text().includes('60 %')&&sum.text().includes('Match length'),'the summary card shows the mode settings');
});
