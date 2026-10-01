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
  vm.runInContext(fs.readFileSync(path.join(__dirname,'developer.js'),'utf8'),context);
  const text=root=>walk(root).map(e=>e.textContent||'').join('|');
  return {api:window.AimModDeveloper,container,settings,navButton,requests,last:()=>requests[requests.length-1],
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
test('with developer mode off the page explains where to turn it on',()=>{
  const s=setup();s.api.enter(s.container);s.last().finish(200,off);assert.ok(s.text(s.container).includes('Turn it on under Settings'));
});
