const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
// Synthetic identities only.
function setup(){
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.listeners={};this.value='';this.className='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(k,v){this.attrs[k]=String(v);}getAttribute(k){return this.attrs[k];}addEventListener(e,f){(this.listeners[e]=this.listeners[e]||[]).push(f);}
    getElementsByTagName(t){return walk(this).filter(e=>e!==this&&e.tag===t);}getContext(){return null;}focus(){El.focused=this;}setSelectionRange(a){this.caret=a;}}
  function walk(e){return [e,...e.children.flatMap(walk)];}
  const requests=[],container=new El('section'),timers=[];
  class Xhr{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}abort(){this.aborted=true;}
    finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}
  const window={document:{createElement:t=>new El(t)},XMLHttpRequest:Xhr,location:{pathname:'/private/ui'}};
  const context=vm.createContext({window,setTimeout:(f,ms)=>{timers.push(f);return timers.length;},clearTimeout:()=>{},Date});
  require('./test-format.cjs').loadFormat(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname,'multiplayer.js'),'utf8'),context);
  const api=window.AimModMultiplayer;
  return {api,container,requests,all:()=>walk(container),buttons:()=>walk(container).filter(e=>e.tag==='button'),text:()=>walk(container).map(e=>e.textContent||'').join('|'),
    button:label=>walk(container).find(e=>e.tag==='button'&&e.textContent===label),last:()=>requests[requests.length-1],focused:()=>El.focused};
}
const base={v:1,now:1000,transport:{kind:'steam',online:true},simulation:false,capabilities:{invite:true,friends:true,gameLoad:true,gameStart:true},self:{id:'p1',name:'Synthetic One'},joining:null,
  friends:{source:'steam',items:[{id:'f1',name:'Synthetic Friend',status:'aimmod',detail:'Playing KovaaK’s with AimMod',joinable:false},{id:'f2',name:'Lobby Friend',status:'aimmod-lobby',detail:'In an AimMod lobby',joinable:true}]},
  invites:[],recent:[],library:{available:true,scenarios:3},notice:null,lobby:null};
const settings={mode:'score-race',scenario:{name:'Synthetic Scenario',hash:'0123456789abcdef',map:'synthetic_map',mapHash:'fedcba9876543210',timeLimit:60},mapOverride:null,maxPlayers:4,spectators:false,rounds:1,firstTo:3,timeLimit:null,
  weapon:{preset:'default'},movement:{preset:'default'},character:{preset:'default'},targetSpeed:1,targetSize:1,privacy:'friends',countdown:5,lateJoin:false};
function lobby(extra){return Object.assign({id:'l1',code:'ABCDEF',revision:3,hostId:'p1',settings,members:[
  {id:'p1',name:'Synthetic One',role:'player',ready:false,ping:null,scenario:'ok',map:'ok',profiles:'none',connection:'connected',link:'local',joinedAt:1,simulated:false},
  {id:'p2',name:'Synthetic Two',role:'player',ready:false,ping:42,scenario:'ok',map:'missing',profiles:'none',connection:'connected',link:'relay',joinedAt:2,simulated:false}],
  match:null,chat:[{id:1,from:null,name:'',text:'Synthetic One created the lobby.',at:1,system:true}],self:'p1',isHost:true,authority:'local',
  blockers:[{code:'ready',text:'Synthetic Two isn’t ready.'}],content:{scenario:'ok',map:'ok',profiles:'none'},simulated:false,generated:null,round:null},extra||{});}
function view(extra){return Object.assign({},base,extra||{});}

test('home offers create and join, and Steam friends with join for joinable lobbies',()=>{
  const s=setup();s.api.enter(s.container);assert.equal(s.requests[0].url,'/private/multiplayer');s.requests[0].finish(200,view());
  const create=s.button('Create lobby');assert.ok(create&&create.parentNode.className==='actions','stand-alone buttons sit in an actions row');
  s.all().find(e=>e.tag==='button'&&/^mp-mode( |$)/.test(e.className)&&e.children.some(c=>c.tag==='strong'&&c.textContent==='Score duel')).onclick();
  s.button('Create lobby').onclick();const post=s.last();assert.equal(post.method,'POST');assert.equal(post.headers['X-AimMod-UI'],'1');assert.deepEqual(JSON.parse(post.body),{action:'create',mode:'duel'});
  const join=s.buttons().filter(b=>b.textContent==='Join');assert.equal(join.length,2,'code join plus the joinable friend');join[1].onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'join-friend',friend:'f2'});
  assert.ok(!s.text().includes('765611'),'no Steam ids are shown');
});
test('lobby shows members with relay ping, content state, host crown and host tools',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view({lobby:lobby()}));
  const t=s.text();assert.ok(t.includes('Relay · 42 ms'));assert.ok(t.includes('Missing map'));assert.ok(t.includes('You’re the host'));assert.ok(t.includes('ABCDEF'));
  assert.ok(s.all().some(e=>e.tag==='canvas'&&e.className==='mp-crown'),'host crown');
  const start=s.button('Start match');assert.ok(start.disabled,'start is disabled with blockers');assert.ok(t.includes('Synthetic Two isn’t ready.'),'blocker reason shown');
  s.button('Invite friends').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'invite'});
  s.button('Make host').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'transfer',member:'p2'});
  s.button('Kick').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'kick',member:'p2'});
  s.button('Invite').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'invite-friend',friend:'f1'});
});
test('a member sees read-only settings and readies up; missing content blocks ready',()=>{
  const s=setup();s.api.enter(s.container);
  const guest=lobby({self:'p2',isHost:false,hostId:'p1',content:{scenario:'ok',map:'missing',profiles:'none'}});
  s.requests[0].finish(200,view({lobby:guest}));
  assert.ok(!s.button('Edit'),'only the host edits settings');assert.ok(s.text().includes('Hosted by Synthetic One'));
  assert.ok(s.button('Ready').disabled,'cannot ready without the map');assert.ok(s.text().includes('You need the map'));
  const ok=lobby({self:'p2',isHost:false,content:{scenario:'ok',map:'ok',profiles:'none'}});ok.members[1].map='ok';
  s.api.leave();s.api.enter(s.container);s.requests[s.requests.length-1].finish(200,view({lobby:ok}));
  s.button('Ready').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'ready',ready:true});
});
test('incoming Steam invites ask before joining',()=>{
  const s=setup();s.api.enter(s.container);
  s.requests[0].finish(200,view({invites:[{id:'join-1',fromName:'Synthetic Host',kind:'invite',summary:{mode:'duel',scenario:'Synthetic Scenario',players:1,maxPlayers:2},at:900,compatible:true}]}));
  assert.ok(s.text().includes('Synthetic Host invited you'));assert.ok(s.text().includes('Score duel'));
  s.button('Accept').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'accept-invite',id:'join-1'});
  s.button('Decline').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'decline-invite',id:'join-1'});
  const launch=setup();launch.api.enter(launch.container);launch.requests[0].finish(200,view({invites:[{id:'join-2',fromName:'A friend',kind:'launch',summary:null,at:900,compatible:true}]}));
  assert.ok(launch.text().includes('Join from Steam'));
});
test('host settings editor sends validated keys, and score race locks overrides',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view({lobby:lobby()}));
  s.button('Edit').onclick();const lib=s.requests.find(r=>r.url==='/private/multiplayer?part=library');assert.ok(lib,'the library loads for pickers');
  lib.finish(200,{available:true,scenarios:[{name:'Synthetic Scenario',map:'synthetic_map',mapSource:'game',timeLimit:60}],maps:[{name:'synthetic_port',source:'ported',hash:'abc'}],weapons:['Synthetic Rifle'],characters:[],presets:[]});
  s.button('Show').onclick();assert.ok(s.all().some(e=>e.tag==='button'&&e.className.indexOf('mp-pick')===0&&e.disabled),'map override is locked in score race');
  s.button('60 s').onclick&&assert.ok(s.button('60 s').disabled,'time limit is locked in score race');
  s.all().find(e=>e.tag==='button'&&/^mp-mode( |$)/.test(e.className)&&e.children.some(c=>c.tag==='strong'&&c.textContent==='Free-for-all')).onclick();
  assert.deepEqual(JSON.parse(s.last().body),{action:'settings',settings:{mode:'ffa-rounds'}});
  s.button('Done').onclick();assert.ok(s.button('Edit'));
});
test('loading waits for every map, shows why one failed and gives the host retry or end',()=>{
  const s=setup();s.api.enter(s.container);
  const loading={id:'m1',phase:'loading',mode:'deathmatch',scenario:'Synthetic Scenario',timeLimit:60,round:1,totalRounds:1,startsAt:null,nextAt:46000,players:['p1','p2'],loaded:['p2'],live:[],rounds:[],standings:[],winnerId:null,rematch:[],loadAttempt:0};
  const wrong={scenario:'AimMod Match - Synthetic',mode:'freeplay',generated:true,state:'ready',message:'KovaaK’s kept the map “old.map” instead of “synthetic_map.map”.',map:'wrong'};
  s.requests[0].finish(200,view({lobby:lobby({match:loading,round:wrong})}));
  let t=s.text();
  assert.ok(t.includes('Waiting for everyone to load (1/2)'));assert.ok(!t.includes('Starting anyway'),'the gate never starts without everyone');
  assert.ok(t.includes('Your map didn’t load')&&!t.includes('Loaded. Your run starts'),'the box shows the map check, not the scenario load');
  const failed=Object.assign({},loading,{loadFailed:true,loadIssues:{p1:'KovaaK’s kept the map “old.map” instead of “synthetic_map.map”.'}});
  s.api.leave();s.api.enter(s.container);s.requests[s.requests.length-1].finish(200,view({lobby:lobby({match:failed,round:Object.assign({},wrong,{map:'failed'})})}));
  t=s.text();assert.ok(t.includes('Couldn’t load the match (1/2)'));assert.ok(t.includes('Synthetic One: KovaaK’s kept the map “old.map”'));
  s.button('Retry').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'retry-load'});
  s.button('End match').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'end'});
});
test('countdown, live scoreboard and final results render from the match',()=>{
  const s=setup();s.api.enter(s.container);
  const live=lobby({match:{id:'m1',phase:'live',mode:'ffa-rounds',scenario:'Synthetic Scenario',timeLimit:60,round:1,totalRounds:3,firstTo:null,startsAt:0,endsAt:70000,nextAt:null,players:['p1','p2'],
    live:[{memberId:'p1',score:900,seconds:30,remaining:30,shots:10,hits:8,kills:5,status:'playing',disputed:false},{memberId:'p2',score:1000,seconds:31,remaining:29,shots:10,hits:9,kills:6,status:'playing',disputed:false}],rounds:[],standings:[],winnerId:null,rematch:[]}});
  s.requests[0].finish(200,view({lobby:live}));
  const t=s.text();assert.ok(t.includes('AIMMOD · FFA'));assert.ok(t.includes('Round 1 of 3'));assert.ok(t.includes('-100'),'gap to the leader');
  const fin=lobby({match:Object.assign({},live.match,{phase:'final',winnerId:'p2',rounds:[{round:1,results:[],winnerId:'p2'}],standings:[{memberId:'p2',name:'Synthetic Two',place:1,wins:1,points:1,best:1000,total:1000,played:1},{memberId:'p1',name:'Synthetic One',place:2,wins:0,points:0,best:900,total:900,played:1}],rematch:['p2']})});
  s.api.leave();s.api.enter(s.container);s.requests[s.requests.length-1].finish(200,view({lobby:fin}));
  assert.ok(s.text().includes('Synthetic Two wins'));assert.ok(s.text().includes('1 of 2 want a rematch.'));assert.ok(s.text().includes('You finished 2nd of 2.'));
  s.button('Rematch').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'rematch'});
});
test('a missing player can download the content with progress, cancel and retry',()=>{
  const s=setup();s.api.enter(s.container);
  const files=[{kind:'scenario',name:'Synthetic Scenario.sce',size:40000,done:0,state:'waiting'},{kind:'map',name:'synthetic_port.json',size:5000000,done:0,state:'waiting'}];
  const guest=d=>lobby({self:'p2',isHost:false,content:{scenario:'missing',map:'missing',profiles:'none'},download:d});
  s.requests[0].finish(200,view({lobby:guest({view:{state:'ready',source:'host',total:5040000,packed:1200000,done:0,speed:0,files},workshop:null,workshopProgress:null,conflicts:[]})}));
  assert.ok(s.text().includes('From the host'));assert.ok(s.text().includes('synthetic_port.json'));
  s.button('Download (4.8 MB)').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'download'});
  s.api.leave();s.api.enter(s.container);s.requests[s.requests.length-1].finish(200,view({lobby:guest({view:{state:'downloading',source:'host',total:5040000,packed:1200000,done:600000,speed:300000,files},conflicts:[]})}));
  assert.ok(s.text().includes('0.6 MB of 1.1 MB · 0.3 MB/s'));s.button('Cancel').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'download-cancel'});
  s.api.leave();s.api.enter(s.container);s.requests[s.requests.length-1].finish(200,view({lobby:guest({view:{state:'error',source:'host',error:'“synthetic_port.json” didn’t match the lobby’s copy, so it was discarded.',code:'hash',total:5040000,packed:1200000,done:0,speed:0,files},conflicts:[]})}));
  assert.ok(s.text().includes('didn’t match'));s.button('Retry').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'download-retry'});
  s.api.leave();s.api.enter(s.container);s.requests[s.requests.length-1].finish(200,view({lobby:guest({view:{state:'ready',source:'workshop',total:40000,packed:20000,done:0,speed:0,files},conflicts:['Synthetic Scenario.sce']})}));
  assert.ok(s.text().includes('Steam Workshop'));assert.ok(s.text().includes('won’t replace your file'));assert.ok(!s.buttons().some(b=>/^Download/.test(b.textContent)),'no download over a conflicting file');
});
test('typing in a lobby search keeps focus and text when the view updates (keys never fall through to the game)',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view({lobby:lobby()}));
  s.button('Edit').onclick();s.requests.find(r=>r.url==='/private/multiplayer?part=library').finish(200,{available:true,scenarios:[{name:'Synthetic Scenario',map:'synthetic_map',mapSource:'game',timeLimit:60},{name:'Other Scenario',map:'m',mapSource:'game',timeLimit:60}],maps:[],weapons:[],characters:[],presets:[]});
  s.all().find(e=>e.tag==='button'&&e.className==='mp-pick').onclick();
  let search=s.all().find(e=>e.tag==='input'&&e.attrs['data-draft']==='picker');assert.ok(search,'picker search is a tracked input');
  search.onfocus();search.value='wasd';search.selectionStart=4;search.oninput();

  // A ping update re-renders the page: deferred while typing, then focus and text come back.
  const l2=lobby();l2.members[1].ping=77;

  const before=s.requests.length;
  s.api.resize();
  const again=s.all().find(e=>e.tag==='input'&&e.attrs['data-draft']==='picker');
  assert.ok(again,'the search survives a re-render');assert.equal(again.value,'wasd','typed text is kept');assert.equal(s.focused(),again,'focus returns to the rebuilt input');assert.equal(again.caret,4,'and the caret too');
  assert.ok(s.text().includes('Other Scenario')===false,'the list stays filtered by the typed text');
  assert.equal(before,s.requests.length);
});
test('spectating a friend shows their stats with stop and switch; being watched shows who, with allow and deny',()=>{
  const s=setup();s.api.enter(s.container);
  s.requests[0].finish(200,view({friends:{source:'steam',items:[{id:'f1',name:'Synthetic Friend',status:'aimmod',detail:'Playing Synthetic A · 1 watching',joinable:false,spectatable:true,watchers:1}]},
    watch:{peer:'f9',name:'Watched Friend',scenario:'Synthetic A',state:'watching',message:'Watching Watched Friend.',mapName:'m',mapScale:1,score:{active:true,score:1234,accuracy:85,remaining:20},others:[{id:'f1',name:'Synthetic Friend'}]},
    watchers:[{peer:'w1',name:'Synthetic Viewer'}],watchAsks:[{peer:'w2',name:'Synthetic Asker'}]}));
  const t=s.text();assert.ok(t.includes('Watched Friend'));assert.ok(t.includes('Score 1,234 · 85% · 20s left'));assert.ok(t.includes('Synthetic Viewer'));assert.ok(t.includes('Wants to watch you'));
  s.button('Switch to Synthetic Friend').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'watch',friend:'f1'});
  s.button('Allow').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'spectate-allow',id:'w2'});
  s.button('Remove').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'spectator-remove',id:'w1'});
  s.button('Stop spectating').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'watch-stop'});
});
test('scenario pickers put favourites and recent scenarios first, with a favourite toggle',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view({lobby:lobby(),picks:{favourites:['Other Scenario'],recent:['Synthetic Scenario','Gone Scenario']}}));
  s.button('Edit').onclick();s.requests.find(r=>r.url==='/private/multiplayer?part=library').finish(200,{available:true,scenarios:[{name:'Synthetic Scenario',map:'synthetic_map',mapSource:'game',timeLimit:60},{name:'Other Scenario',map:'m',mapSource:'game',timeLimit:60}],maps:[],weapons:[],characters:[],presets:[]});
  s.all().find(e=>e.tag==='button'&&e.className==='mp-pick').onclick();
  const groups=s.all().filter(e=>e.className==='mp-pick-group').map(e=>e.textContent);assert.deepEqual(groups,['Favourites','Recent','All scenarios']);
  const names=s.all().filter(e=>e.className==='mp-pick-info').map(e=>e.children[0].textContent);assert.deepEqual(names.slice(0,2),['Other Scenario','Synthetic Scenario'],'favourite, then recent; missing names are skipped');
  const stars=s.all().filter(e=>e.tag==='button'&&e.className.indexOf('mp-fav')>=0);assert.ok(stars.every(b=>b.parentNode.className==='actions'));
  stars[0].onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'favourite',scenario:'Other Scenario',on:false});
});
test('match history shows opponents, scores and replay links, and rivals filter it',()=>{
  const st=(name,place,self,key)=>({name,place,best:900-place*100,wins:0,points:0,self,key,total:900-place*100});
  const m1={id:'m1',endedAt:900,mode:'score-race',scenario:'Synthetic Scenario',place:1,players:2,winner:'Synthetic One',won:true,simulated:false,rounds:1,standings:[st('Synthetic One',1,true,'k0'),st('Synthetic Rival',2,false,'k1')],replays:[{round:1,mine:'run-1',others:[{key:'k1',name:'Synthetic Rival',id:'run-2'}]}]};
  const m2={id:'m2',endedAt:800,mode:'score-race',scenario:'Other Scenario',place:2,players:2,winner:'Someone Else',won:false,simulated:false,rounds:1,standings:[st('Someone Else',1,false,'k2'),st('Synthetic One',2,true,'k0')],replays:null};
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view({recent:[m1,m2]}));
  s.button('All matches').onclick();s.requests.find(r=>r.url==='/private/multiplayer?part=history').finish(200,{matches:[m1,m2],rivals:[{key:'k1',name:'Synthetic Rival',played:3,won:2,lost:1,drawn:0,lastAt:900,lastScenario:'Synthetic Scenario'}]});
  assert.ok(s.text().includes('vs Synthetic Rival')&&s.text().includes('Rivals'));
  s.buttons().filter(b=>b.textContent==='Details')[0].onclick();
  assert.ok(s.text().includes('Synthetic One (you)'),'standings table');
  s.button('Me vs Synthetic Rival').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'load',id:'run-1',compareId:'run-2'});assert.equal(s.last().url,'/private/native-replay');
  s.button('Matches').onclick();assert.ok(s.text().includes('Matches with Synthetic Rival')&&!s.text().includes('Other Scenario'),'rival filter');
});
test('first run shows the tour once: keys, privacy, Hub and Discord, then saves onboarded',()=>{
  const s=setup();s.api.enter(s.container);
  s.requests[0].finish(200,view({prefs:{onboarded:false,hotkey:'F7',spectatePrivacy:'friends'},keys:{clip:'F8',taken:['F9'],conflicts:[]}}));
  assert.ok(s.text().includes('Play KovaaK’s together')&&s.text().includes('Step 1 of 4'));
  s.button('Next').onclick();assert.ok(s.text().includes('Your keys')&&s.text().includes('F9 (in use)'));
  s.button('F6').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'prefs',prefs:{hotkey:'F6'}});
  s.button('Next').onclick();s.button('Next').onclick();
  const ask=s.requests.find(r=>r.url==='/private/discord-settings');assert.ok(ask,'asks whether Discord presence exists');
  ask.finish(200,{settings:{discordPresenceEnabled:false,discordShowScore:true,discordShowPersonalBest:true,discordShowHubButton:true},status:{state:'off'}});
  assert.ok(s.text().includes('Discord status')&&s.text().includes('AimMod Hub'));
  s.all().find(e=>e.tag==='button'&&e.attrs['aria-label']==='Discord status').onclick();assert.deepEqual(JSON.parse(s.last().body),{discordPresenceEnabled:true});
  s.button('Done').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'prefs',prefs:{onboarded:true}});
  assert.ok(!s.text().includes('Step 4 of 4'),'the tour closes');
});
test('a spectator follows a player or the leader during a live match, and can stop',()=>{
  const live=[{memberId:'p2',score:5000,seconds:20,remaining:40,shots:10,hits:8,kills:8,status:'playing',disputed:false},{memberId:'p3',score:7000,seconds:20,remaining:40,shots:10,hits:9,kills:9,status:'playing',disputed:false}];
  const match={id:'m1',phase:'live',mode:'score-race',scenario:'Synthetic Scenario',timeLimit:60,round:1,totalRounds:1,startsAt:0,players:['p2','p3'],live,rounds:[],standings:[],rematch:[]};
  const l=lobby({match});l.members[0].role='spectator';l.members.push({id:'p3',name:'Synthetic Three',role:'player',ready:true,ping:30,scenario:'ok',map:'ok',profiles:'none',connection:'connected',link:'relay',joinedAt:3,simulated:false});
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view({lobby:l}));
  assert.ok(s.text().includes('You’re spectating')&&!s.text().includes('Your run'),'spectators get a spectator card, not Your run');
  s.all().find(e=>e.tag==='button'&&e.attrs['aria-label']==='Follow the leader').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'spectate-follow',on:true});
  const following=lobby({match,spectate:{member:'p3',name:'Synthetic Three',scenario:'Synthetic Scenario',mapName:'synthetic_map',mapScale:1,label:'Synthetic Three',score:null,follow:true,stream:'pose-p3',started:true}});following.members=l.members;
  s.last().finish(200,view({lobby:following}));
  const start=s.requests.filter(r=>r.url==='/private/native-replay').pop();assert.equal(JSON.parse(start.body).label,'Synthetic Three','the spectator view starts on the leader');assert.equal(JSON.parse(start.body).stream,'pose-p3','with the bridge stream id');
  assert.ok(s.text().includes('Following whoever leads, now Synthetic Three')&&s.text().includes('Score 7,000'));
  assert.ok(s.all().some(e=>e.className==='mp-hud-row watched'||/ watched$/.test(e.className||'')),'the followed player is marked on the scoreboard');
  // The leader changes: the view quietly takes the new player.
  const next=JSON.parse(JSON.stringify(following));next.spectate.member='p2';next.spectate.name=next.spectate.label='Synthetic Two';next.spectate.stream='pose-p2';next.revision=4;
  s.api.leave();s.api.enter(s.container);s.requests[s.requests.length-1].finish(200,view({lobby:next}));
  const sw=JSON.parse(s.requests.filter(r=>r.url==='/private/native-replay').pop().body);assert.equal(sw.label,'Synthetic Two');assert.equal(sw.stream,'pose-p2');assert.equal(sw.scenario,'Synthetic Scenario','same scenario, so the view switches in place');
  s.button('Stop spectating').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'spectate-stop'});
});
test('the cosmetics page lists catalog items only, equips by id and sets who to show',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view());
  s.button('Cosmetics').onclick();const ask=s.requests.find(r=>r.url==='/private/multiplayer?part=cosmetics');
  ask.finish(200,{available:true,problem:null,version:1,show:'all',unavailable:1,items:[{id:'meso-tint-ember',version:1,kind:'avatar_tint',name:'Ember',models:['Meso'],color:[0.85,0.22,0.05],equipped:false},{id:'weapon-finish-sand',version:2,kind:'weapon_finish',name:'Sand',models:[],color:[0.76,0.66,0.48],equipped:true}]});
  const t=s.text();assert.ok(t.includes('only show in AimMod matches')&&t.includes('Tints and patterns')&&t.includes('Weapon finishes')&&t.includes('1 more need a newer AimMod'));
  s.button('Equip').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'cosmetic-equip',id:'meso-tint-ember'});
  s.button('Friends').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'cosmetic-view',show:'friends'});
});
test('the cosmetics preview heartbeats only while the page is open, shows the newest frame and stops on leave',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view());
  const previews=()=>s.requests.filter(r=>r.url==='/private/cosmetic-preview');
  assert.equal(previews().length,0,'no preview request before the page opens');
  s.button('Cosmetics').onclick();
  const first=previews()[0];assert.ok(first,'opening the page starts the preview');
  assert.equal(first.method,'POST');assert.equal(first.headers['X-AimMod-UI'],'1');assert.deepEqual(JSON.parse(first.body),{open:true,yaw:0});
  s.requests.find(r=>r.url==='/private/multiplayer?part=cosmetics').finish(200,{available:true,problem:null,version:1,show:'all',unavailable:0,
    items:[{id:'meso-tint-ember',version:1,kind:'avatar_tint',name:'Ember',models:['Meso'],color:[0.85,0.22,0.05],equipped:false}]});
  const img=()=>s.all().find(e=>e.tag==='img'&&/mp-cos-live-img/.test(e.className));
  assert.equal(img().style.display,'none','no frame yet: the note shows instead');
  first.finish(200,{frame:3});
  assert.equal(img().src,'/private/cosmetic-preview.png?f=3');assert.equal(img().style.display,'block');
  s.button('Preview').onclick();assert.deepEqual(JSON.parse(previews().at(-1).body),{open:true,yaw:0,item:'meso-tint-ember'},'trying an item on sends its id only');
  assert.ok(!previews().some(r=>/file|path|png/i.test(r.body)),'the preview never sends files or paths');
  s.api.leave();
  assert.deepEqual(JSON.parse(previews().at(-1).body),{open:false},'leaving the page (or hiding the workspace) ends the preview');
});
test('the curated set shows as finish swatches in its own colours, with no coming-soon state',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view());
  // Record what the card canvases paint.
  const painted=[];const ctx={scale(){},fillRect(){},beginPath(){},arc(){},fill(){painted.push(this.fillStyle);},stroke(){painted.push(this.strokeStyle);},moveTo(){},lineTo(){},closePath(){}};
  s.all()[0].constructor.prototype.getContext=()=>ctx;
  s.button('Cosmetics').onclick();
  s.requests.find(r=>r.url==='/private/multiplayer?part=cosmetics').finish(200,{available:true,problem:null,version:2,show:'friends',unavailable:0,items:[
    {id:'tint-mint',version:1,kind:'avatar_tint',name:'AimMod Mint',models:['Meso','Endo'],color:[0.02,0.6,0.3],swatch:['#27cb95','#eff3f1','#959e99'],shine:0.1,equipped:false},
    {id:'tint-gold',version:1,kind:'avatar_tint',name:'Gold',models:['Meso','Endo'],swatch:['#f0c675','red;x','#f9e2aa'],shine:0.9,equipped:true},
    {id:'finish-ice',version:1,kind:'weapon_finish',name:'Ice',models:[],swatch:['#7ccfff','#6fbcee'],shine:0,equipped:false},
    {id:'accessory-halo',version:1,kind:'accessory',name:'Halo',models:['Meso','Endo'],role:'head',swatch:['#f9dc8a'],shine:0.9,equipped:false}]});
  const t=s.text();
  assert.ok(!t.includes('coming soon'),'items replace the coming-soon state');
  assert.ok(t.includes('AimMod Mint')&&t.includes('Gold')&&t.includes('Ice')&&t.includes('Tints and patterns')&&t.includes('Weapon finishes'));
  assert.ok(t.includes('Accessories')&&t.includes('Halo')&&t.includes('Head · Meso, Endo'),'accessories list with their slot');
  for(const hex of ['#27cb95','#eff3f1','#959e99','#f0c675','#7ccfff','#6fbcee','#f9dc8a'])assert.ok(painted.includes(hex),'swatch paints '+hex);
  assert.ok(!painted.some(p=>/red|;/.test(p)),'only hex colours reach the canvas');
  assert.equal(s.all().filter(e=>e.tag==='canvas'&&e.className==='mp-cos-preview').length,4,'one swatch per item');
  // Try-on is for what the preview shows (tints and accessories); weapon finishes are equipped directly.
  assert.equal(s.all().filter(e=>e.tag==='button'&&e.textContent==='Preview').length,2);
  // Show others' cosmetics stays: the current choice is selected, and changes post.
  s.button('Off').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'cosmetic-view',show:'off'});
  s.button('All').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'cosmetic-view',show:'all'});
  s.button('Remove').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'cosmetic-remove',id:'tint-gold'});
});
test('the cosmetics page says cosmetics are coming soon while the catalog has nothing to pick',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view());
  s.button('Cosmetics').onclick();s.requests.find(r=>r.url==='/private/multiplayer?part=cosmetics').finish(200,{available:true,problem:null,version:1,show:'all',unavailable:0,items:[]});
  assert.ok(s.text().includes('Cosmetics are coming soon')&&s.text().includes('next AimMod update')&&s.text().includes('Show others’ cosmetics'));
});
test('the map library lists ports with size, Shift and Workshop state, and installs or hosts them',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view());
  s.button('Map library').onclick();const ask=s.requests.find(r=>r.url==='/private/multiplayer?part=maps');assert.ok(ask,'the library asks for ports');
  ask.finish(200,{available:true,source:'steam',canInstall:true,canLoad:true,lobby:null,ports:[
    {key:'aaaaaaaaaaaa',scenario:'AimMod - Dust2 (CSGO) - CS Movement',display:'Dust2',game:'CSGO',variant:'CS Movement',bytes:71000000,shift:'walk',mapScale:4,workshop:true,installed:true,simulated:false,needsUpdate:true,preview:true,download:null},
    {key:'bbbbbbbbbbbb',scenario:'AimMod - Mirage (CSGO) - CS Movement',display:'Mirage',game:'CSGO',variant:'CS Movement',bytes:61000000,shift:'walk',mapScale:0,workshop:true,installed:false,simulated:false,needsUpdate:false,preview:false,download:{state:'downloading',done:30500000,total:61000000}}]});
  const t=s.text();assert.ok(t.includes('Dust2')&&t.includes('Shift walks')&&t.includes('Update available')&&t.includes('Downloading 50%'));
  assert.ok(s.all().some(e=>e.tag==='img'&&e.src==='/private/multiplayer?part=preview&key=aaaaaaaaaaaa'),'preview by key, never by path');
  assert.ok(!s.button('Install'),'no second install while one downloads');
  s.button('Update').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'map-install',key:'aaaaaaaaaaaa'});
  s.button('Host a lobby').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'create',mode:'practice',scenario:'AimMod - Dust2 (CSGO) - CS Movement'});
});
test('mode cards show the line icon in a panel, mint when selected, and every mode ships its icons',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view());
  const card=label=>s.all().find(e=>e.tag==='button'&&/^mp-mode( |$)/.test(e.className)&&e.children.some(c=>c.tag==='strong'&&c.textContent===label));
  card('Score duel').onclick();
  const icon=label=>card(label).getElementsByTagName('img').find(e=>e.className==='mp-mode-icon');
  assert.equal(icon('Score duel').src,'art/modes/duel-on@2x.png');assert.equal(icon('Free-for-all').src,'art/modes/ffa-rounds@2x.png');
  assert.equal(card('Score duel').getElementsByTagName('canvas').length,0,'no canvas art on the cards');
  const dir=path.join(__dirname,'art','modes');
  const ids=fs.readFileSync(path.join(__dirname,'multiplayer.js'),'utf8').match(/MODE_ICONS=\[([^\]]+)\]/)[1].match(/[a-z-]+/g);
  assert.equal(ids.length,10);
  for(const id of ids){
    assert.match(fs.readFileSync(path.join(dir,id+'.svg'),'utf8'),/viewBox="0 0 48 48"[^>]*stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/,id+' is a 48 px line icon');
    for(const f of [id+'@2x.png',id+'@3x.png',id+'-on@2x.png',id+'-on@3x.png'])assert.ok(fs.statSync(path.join(dir,'png',f)).size>200,f+' is built');
  }
  assert.match(fs.readFileSync(path.join(__dirname,'..','native-service','AimMod.InGame.csproj'),'utf8'),/ui\/art\/modes\/png\/\*\.png" LogicalName="AimMod\.ModeIcon\./,'the PNGs are embedded');
});
test('leaving stops polling and ignores late answers',()=>{
  const s=setup();s.api.enter(s.container);s.api.leave();s.requests[0].finish(200,view());assert.equal(s.buttons().length,0);
});
