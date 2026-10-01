const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
// Synthetic identities only.
function setup(){
  const drawn=[];
  class Ctx{constructor(){this.ops=drawn;}fillRect(){}strokeRect(){}clearRect(){}fillText(t){drawn.push(t);}beginPath(){}moveTo(){}lineTo(){}stroke(){}}
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.listeners={};this.value='';this.className='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}get firstChild(){return this.children[0];}
    set textContent(v){this._text=v;if(v==='')this.children=[];}get textContent(){return this._text;}
    setAttribute(k,v){this.attrs[k]=String(v);}getAttribute(k){return this.attrs[k];}addEventListener(e,f){(this.listeners[e]=this.listeners[e]||[]).push(f);}
    getContext(){return new Ctx();}}
  function walk(e){return [e,...e.children.flatMap(walk)];}
  const requests=[],container=new El('section');
  class Xhr{constructor(){requests.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}
    finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}
  const window={document:{createElement:t=>new El(t),getElementById:()=>null},XMLHttpRequest:Xhr,location:{pathname:'/private/ui'}};
  const context=vm.createContext({window,setTimeout:()=>1,clearTimeout:()=>{},Date,JSON});
  require('./test-format.cjs').loadFormat(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname,'tournaments.js'),'utf8'),context);
  return {api:window.AimModTournaments,container,requests,drawn,
    text:()=>walk(container).map(e=>e.textContent||'').join('|'),button:label=>walk(container).find(e=>e.tag==='button'&&e.textContent===label),last:()=>requests[requests.length-1]};
}
function match(extra){return Object.assign({id:'W1-1',label:'Semi-final',side:'winners',round:1,position:1,state:'ready',bestOf:3,winsA:0,winsB:0,a:'e1',b:'e2',winner:'',host:'',ready:[],currentGame:-1,games:[],veto:[],vetoTurn:'',vetoAction:'',deadline:'',reportedBy:'',flags:[],resolution:''},extra||{});}
function view(m,extra){return Object.assign({v:1,linked:true,simulated:false,developer:false,status:'',canHost:true,checkIn:[],mine:[],open:null,lobby:null,
  matches:[{tournament:'t_synthetic',tournamentName:'Synthetic Cup',active:true,host:true,match:m,self:{id:'e1',name:'Synthetic One',seed:1},opponent:{id:'e2',name:'Synthetic Two',seed:4},ruleset:{bestOf:3,pool:['Synthetic A','Synthetic B','Synthetic C','Synthetic D','Synthetic E'],requireReplays:false}}]},extra||{});}

test('a ready match readies up from the page',()=>{
  const s=setup();s.api.enter(s.container);assert.equal(s.requests[0].url,'/private/tournaments');s.requests[0].finish(200,view(match()));
  assert.ok(s.text().includes('Semi-final: you vs Synthetic Two'));assert.ok(s.text().includes('you host the lobby'));
  const ready=s.button('I’m ready');assert.ok(ready&&ready.parentNode.className==='actions');ready.onclick();
  const post=s.last();assert.equal(post.method,'POST');assert.equal(post.headers['X-AimMod-UI'],'1');assert.deepEqual(JSON.parse(post.body),{action:'ready',tournament:'t_synthetic',match:'W1-1'});
  assert.ok(!s.text().includes('7656119'),'no Steam ids are shown');
});
test('veto turn offers the scenarios left, and the series shows games with seeds',()=>{
  const s=setup();s.api.enter(s.container);
  s.requests[0].finish(200,view(match({state:'veto',vetoTurn:'e1',vetoAction:'ban',veto:[{step:1,action:'ban',entrant:'e2',scenario:'Synthetic A'}]})));
  assert.ok(s.text().includes('Synthetic Two bans Synthetic A'));assert.ok(!s.button('Ban Synthetic A'));
  s.button('Ban Synthetic B').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'veto',tournament:'t_synthetic',match:'W1-1',scenario:'Synthetic B'});
  const s2=setup();s2.api.enter(s2.container);
  s2.requests[0].finish(200,view(match({state:'live',currentGame:1,winsA:1,games:[{index:0,scenario:'Synthetic C',seed:'123',hasScore:true,scoreA:1200,scoreB:1100,winner:0},{index:1,scenario:'Synthetic D',seed:'456',hasScore:false,winner:-1}]}),
    {lobby:{matchId:'W1-1',game:1,phase:'live',isHost:true,spectators:2,players:[{name:'Synthetic One',self:true,score:900,accuracy:81.5,remaining:30,ping:0,connection:'connected',status:'playing'},{name:'Synthetic Two',self:false,score:850,accuracy:79,remaining:31,ping:44,connection:'connected',status:'playing'}]}}));
  const t=s2.text();assert.ok(t.includes(' · seed 123'));assert.ok(t.includes('1,200 – 1,100'));assert.ok(t.includes('Playing'));assert.ok(t.includes('same targets'));
  assert.ok(t.includes('Your lobby · game 2 · live · 2 watching'),'host overview of both players');assert.ok(t.includes('81.5%'));assert.ok(t.includes('44 ms'));
});
test('the other player confirms or disputes the reported result',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,view(match({state:'awaiting_confirmation',winsA:2,winsB:1,reportedBy:'e2'})));
  assert.ok(s.text().includes('You won.'));s.button('Confirm result').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'confirm',tournament:'t_synthetic',match:'W1-1'});
  s.last().finish(409,{error:'Not now.'});
  const n=s.requests.length;s.button('Dispute').onclick();assert.equal(s.requests.length,n,'an empty reason is refused locally');
});
test('unlinked players are sent to the Account page and developers can simulate',()=>{
  const s=setup();s.api.enter(s.container);s.requests[0].finish(200,Object.assign(view(match()),{linked:false,matches:[],developer:true}));
  assert.ok(s.text().includes('Link your AimMod Hub account'));s.button('Simulate a tournament').onclick();assert.deepEqual(JSON.parse(s.last().body),{action:'simulate'});
});
test('the bracket is drawn on canvas with Roboto and the players in it',()=>{
  const s=setup();
  const t={name:'Synthetic Cup',selfEntrant:'e1',entrants:[{id:'e1',name:'Synthetic One',seed:1},{id:'e2',name:'Synthetic Two',seed:2}],
    matches:[match({state:'complete',winner:'e1',winsA:2,winsB:0}),match({id:'W1-2',position:2,a:'',b:'',state:'pending'}),match({id:'W2-1',round:2,label:'Final',a:'e1',b:'',state:'pending'})]};
  s.api._bracket(t);assert.ok(s.drawn.some(x=>x.indexOf('Synthetic One')>=0));assert.ok(s.drawn.includes('FINAL'));assert.ok(s.drawn.includes('TBD'));
});
