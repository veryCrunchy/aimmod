const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
// Synthetic identities only.
function setup(){
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.listeners={};this.value='';this.className='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(k,v){this.attrs[k]=String(v);}getAttribute(k){return this.attrs[k];}addEventListener(e,f){(this.listeners[e]=this.listeners[e]||[]).push(f);}
    getElementsByTagName(t){return walk(this).filter(e=>e!==this&&e.tag===t);}getContext(){return null;}focus(){}}
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
    button:label=>walk(container).find(e=>e.tag==='button'&&e.textContent===label),last:()=>requests[requests.length-1]};
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
  s.button('Duel')||s.all().find(e=>e.className&&e.className.indexOf('mp-mode')===0&&e.children[0].textContent==='Duel').onclick();
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
  assert.ok(!s.button('Edit'),'only the host edits settings');assert.ok(s.text().includes('Set by Synthetic One (host).'));
  assert.ok(s.button('Ready').disabled,'cannot ready without the map');assert.ok(s.text().includes('You need the map'));
  const ok=lobby({self:'p2',isHost:false,content:{scenario:'ok',map:'ok',profiles:'none'}});ok.members[1].map='ok';
  s.api.leave();s.api.enter(s.container);s.requests[s.requests.length-1].finish(200,view({lobby:ok}));
  s.button('Ready').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'ready',ready:true});
});
test('incoming Steam invites ask before joining',()=>{
  const s=setup();s.api.enter(s.container);
  s.requests[0].finish(200,view({invites:[{id:'join-1',fromName:'Synthetic Host',kind:'invite',summary:{mode:'duel',scenario:'Synthetic Scenario',players:1,maxPlayers:2},at:900,compatible:true}]}));
  assert.ok(s.text().includes('Synthetic Host invited you'));assert.ok(s.text().includes('Duel'));
  s.button('Accept').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'accept-invite',id:'join-1'});
  s.button('Decline').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'decline-invite',id:'join-1'});
  const launch=setup();launch.api.enter(launch.container);launch.requests[0].finish(200,view({invites:[{id:'join-2',fromName:'A friend',kind:'launch',summary:null,at:900,compatible:true}]}));
  assert.ok(launch.text().includes('Join from Steam'));
});
test('host settings editor sends validated keys, and score race locks overrides',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view({lobby:lobby()}));
  s.button('Edit').onclick();const lib=s.requests.find(r=>r.url==='/private/multiplayer?part=library');assert.ok(lib,'the library loads for pickers');
  lib.finish(200,{available:true,scenarios:[{name:'Synthetic Scenario',map:'synthetic_map',mapSource:'game',timeLimit:60}],maps:[{name:'synthetic_port',source:'ported',hash:'abc'}],weapons:['Synthetic Rifle'],characters:[],presets:[]});
  assert.ok(s.all().some(e=>e.tag==='button'&&e.className.indexOf('mp-pick')===0&&e.disabled),'map override is locked in score race');
  s.button('60 s').onclick&&assert.ok(s.button('60 s').disabled,'time limit is locked in score race');
  s.all().find(e=>e.className&&e.className.indexOf('mp-mode')===0&&e.children[0]&&e.children[0].textContent==='Free-for-all').onclick();
  assert.deepEqual(JSON.parse(s.last().body),{action:'settings',settings:{mode:'ffa-rounds'}});
  s.button('Done').onclick();assert.ok(s.button('Edit'));
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
test('leaving stops polling and ignores late answers',()=>{
  const s=setup();s.api.enter(s.container);s.api.leave();s.requests[0].finish(200,view());assert.equal(s.buttons().length,0);
});
