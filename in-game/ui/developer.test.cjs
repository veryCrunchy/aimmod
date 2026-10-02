const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
// Synthetic identities only.
function setup(){
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.className='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(k,v){this.attrs[k]=String(v);}getAttribute(k){return this.attrs[k];}}
  function walk(e){return [e,...e.children.flatMap(walk)];}
  const requests=[],container=new El('section'),navButton=new El('button'),settings=new El('div');
  class Xhr{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}
    finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}
  const window={document:{createElement:t=>new El(t),getElementById:id=>id==='nav-developer'?navButton:null},XMLHttpRequest:Xhr,location:{pathname:'/private/ui'}};
  const context=vm.createContext({window,setTimeout:()=>1,clearTimeout:()=>{},JSON});
  require('./test-format.cjs').loadFormat(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname,'developer.js'),'utf8'),context);
  const text=root=>walk(root).map(e=>e.textContent||'').join('|');
  return {api:window.AimModDeveloper,container,settings,navButton,requests,last:()=>requests.filter(r=>/\/developer$/.test(r.url)).pop(),
    button:(root,label)=>walk(root).find(e=>e.tag==='button'&&e.textContent===label),all:root=>walk(root),text};
}
const off={enabled:false,notices:['invite','ready'],status:null};
const on={enabled:true,notices:['invite','ready','countdown'],status:{transport:'offline',online:false,bridge:null,capabilities:[],simulation:true,simulationForced:false,lobby:null}};
test('developer mode is a Settings switch, and the nav entry shows only while it is on',()=>{
  const s=setup();s.api.renderSettings(s.settings);s.last().finish(200,off);
  assert.equal(s.navButton.style.display,'none','hidden while off');
  const toggle=s.all(s.settings).find(e=>e.attrs['aria-label']==='Developer tools');assert.equal(toggle.attrs['aria-checked'],'false');
  toggle.onclick();const post=s.last();assert.equal(post.method,'POST');assert.equal(post.headers['X-AimMod-UI'],'1');assert.deepEqual(JSON.parse(post.body),{action:'enable',on:true});
  post.finish(200,on);assert.equal(s.navButton.style.display,'','shown once on');
});
test('the developer page creates simulated lobbies, drives them and fires notices',()=>{
  const s=setup();s.api.enter(s.container);s.last().finish(200,on);
  s.button(s.container,'5').onclick();s.button(s.container,'Host it here').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'lobby',members:5,mode:'score-race'});
  const inLobby=JSON.parse(JSON.stringify(on));inLobby.status.lobby={code:'ABCDEF',members:6,simulated:5,isHost:true,phase:'lobby'};s.last().finish(200,inLobby);
  assert.ok(s.text(s.container).includes('Room ABCDEF · 6 members (5 simulated)'));
  s.button(s.container,'Someone chats').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'sim',op:'chat'});
  s.button(s.container,'Ready check').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'notice',kind:'ready'});
  assert.ok(s.all(s.container).filter(e=>e.tag==='button'&&/\bbutton\b/.test(e.className)).every(b=>b.parentNode.className==='actions'||/segmented/.test(b.parentNode.className)),'stand-alone buttons sit in actions rows');
});
test('the developer page offers avatars, loopback, game commands, content and Workshop tools',()=>{
  const s=setup();s.api.enter(s.container);
  const full=JSON.parse(JSON.stringify(on));full.status.looks=[{id:'meso-tracer',label:'Tracer'}];full.status.look='meso-tracer';
  full.tools={replays:[{id:'run-1',scenario:'Synthetic Scenario',recordedAt:'2026-01-01T00:00:00Z',seconds:60},{id:'run-2',scenario:'Synthetic Scenario',recordedAt:'2026-01-01T00:01:00Z',seconds:60}],avatarPath:null,loopback:{source:'self',delay:2,scenario:'Synthetic Scenario',mapName:'m',mapScale:1},content:{state:'done',done:10,total:10,files:[{name:'Synthetic Scenario.sce'}]},logs:{service:['started'],game:['[AimModCore] ok']}};
  full.camera=[1,2,3,-5,90,0,100];full.workshop=[{item:'1',title:'AimMod - Dust2 (CSGO) - CS Movement',bytes:71000000,installed:true,needsUpdate:false,port:true}];
  s.last().finish(200,full);
  s.button(s.container,'Spawn circling avatar').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'avatar',on:true,mode:'circle'});
  s.all(s.container).filter(e=>e.tag==='button'&&e.textContent==='Use as A')[0].onclick();s.all(s.container).filter(e=>e.tag==='button'&&e.textContent==='Use as B')[1].onclick();
  s.button(s.container,'A vs B').onclick();const vs=s.requests.filter(r=>/native-replay$/.test(r.url)).pop();assert.deepEqual(JSON.parse(vs.body),{action:'load',id:'run-1',compareId:'run-2'});
  s.button(s.container,'Spectate yourself').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'loopback',source:'self',delay:2});
  s.button(s.container,'Open the spectator view').onclick();assert.equal(JSON.parse(s.requests.filter(r=>/native-replay$/.test(r.url)).pop().body).action,'spectate');
  s.button(s.container,'Start in freeplay').onclick();const start=JSON.parse(s.requests.filter(r=>/game-command$/.test(r.url)&&r.method==='POST').pop().body);assert.equal(start.mode,'freeplay','developer starts are freeplay only');
  s.button(s.container,'Capture thumbnail').onclick();const cap=JSON.parse(s.requests.filter(r=>/game-command$/.test(r.url)&&r.method==='POST').pop().body);assert.deepEqual(cap.views,[{x:1,y:2,z:3,pitch:-5,yaw:90,fov:100}],'the current camera is the view');
  s.button(s.container,'Receive and fail on purpose').onclick();assert.equal(JSON.parse(s.last().body).fail,true);
  assert.ok(s.text(s.container).includes('AimMod - Dust2 (CSGO) - CS Movement')&&s.text(s.container).includes('[AimModCore] ok'));
});
test('with developer mode off the page explains where to turn it on',()=>{
  const s=setup();s.api.enter(s.container);s.last().finish(200,off);assert.ok(s.text(s.container).includes('Turn it on under Settings'));
});
test('with bot debug on the page lists each bot\'s senses: what it heard, the decoys it called, its smoke call and blindness',()=>{
  const s=setup();s.api.enter(s.container);
  const dbg=JSON.parse(JSON.stringify(on));dbg.status.botDebug=true;
  dbg.status.bots=[{name:'BOT Ace',role:'anchor',move:'walk/stand',heard:['gunfire (rifle) 23 m through a wall'],sources:['#2 rifle: decoy (never moves, no hits)'],smoke:'HoldEdge (smoke 9 m, 8 s left)',blind:0.7,flash:'looking away (own flash)'},
    {name:'BOT Kit',role:'roam',move:'run/stand',heard:[],sources:[],smoke:null,blind:0,flash:null}];
  s.last().finish(200,dbg);
  const text=s.text(s.container);
  assert.ok(text.includes('BOT Ace · anchor')&&text.includes('heard gunfire (rifle) 23 m through a wall')&&text.includes('gunfire #2 rifle: decoy (never moves, no hits)')&&text.includes('smoke: HoldEdge')&&text.includes('blind 70%')&&text.includes('looking away (own flash)'));
  assert.ok(text.includes('BOT Kit · roam')&&!text.includes('blind 0%'),'a calm bot shows just its movement');
  const held=JSON.parse(JSON.stringify(on));held.status.botDebug=true;
  held.status.bots=[{name:'BOT Ace',role:'post-plant (entrance 0)',move:'run/crouch',heard:[],sources:[],smoke:null,blind:0,flash:null,spot:'entrance 0 at (-3900, 1500), sees the bomb, cover 3',look:'(-3000, 1300, 160)'}];
  s.api.enter(s.container);s.last().finish(200,held);
  const heldText=s.text(s.container);
  assert.ok(heldText.includes('BOT Ace · post-plant (entrance 0)')&&heldText.includes('entrance 0 at (-3900, 1500), sees the bomb, cover 3')&&heldText.includes('looking at (-3000, 1300, 160)'),'a holding bot shows its spot and where it looks');
  s.button(s.container,'Off').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'bot-debug',on:false});
});
