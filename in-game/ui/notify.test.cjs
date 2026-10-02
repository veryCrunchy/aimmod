const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
function setup(){
  class El{constructor(tag){this.tag=tag;this.children=[];this.style={};this.className='';this.textContent='';}
    appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(){}all(){return this.children.flatMap(c=>[c,...c.all()]);}text(){return [this.textContent,...this.children.map(c=>c.text())].join(' ');}}
  const box=new El('main');
  class Xhr{open(){}setRequestHeader(){}send(){}}
  const window={document:{createElement:t=>new El(t),getElementById:()=>box},XMLHttpRequest:Xhr,location:{pathname:'/cap/notify'}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'notify.js'),'utf8'),{window,setTimeout:()=>1,clearTimeout:()=>{}});
  return {render:window.AimModNotify.render,box};
}
test('kill feed reads naturally when you are the victim and lines have a backing',()=>{
  const n=setup();n.render({version:1,active:false,combat:{alive:true,health:80,max:100,frags:1,fragLimit:10,left:65,feed:[{killer:'Kestrel',victim:'You',you:'victim'},{killer:'You',victim:'Nova',you:'killer',head:true}]}});
  const lines=n.box.all().filter(e=>e.className==='feed-text').map(e=>e.textContent);
  assert.deepEqual(lines,['Kestrel fragged you','You fragged Nova · headshot']);
});
test('respawn and team lines have units and plain wording',()=>{
  const n=setup();n.render({version:1,active:false,combat:{alive:false,respawnIn:3,frags:2,fragLimit:10,team:1,teamFrags:31,otherFrags:28,feed:[]}});
  const text=n.box.text();assert.match(text,/Back in 3 s/);assert.match(text,/Your team 31 · Other team 28/);
});
test('duel strip spells out the round and keeps a long opponent name in its own truncating cell',()=>{
  const n=setup();n.render({version:1,active:false,duel:{opponent:'A very long persona name that will not fit',you:41.2,them:38.14,youShare:41,themShare:38,wins:1,theirWins:0,round:2,rounds:4,disputed:true}});
  const who=n.box.all().filter(e=>e.className==='duel-who').map(e=>e.textContent);
  assert.deepEqual(who,['You','A very long persona name that will not fit']);
  assert.ok(n.box.all().some(e=>e.className==='duel-time'&&e.textContent==='Round 2/4'));
  assert.ok(n.box.all().some(e=>e.className==='duel-sub'&&/disputed/.test(e.textContent)));
  assert.match(fs.readFileSync(path.join(__dirname,'notify.css'),'utf8'),/\.duel-who\{[^}]*text-overflow:ellipsis/);
});
test('the notice label names where it comes from',()=>{
  const brand=id=>{const n=setup();n.render({version:1,active:true,id,kind:'info',title:'T',body:'B'});return n.box.all().find(e=>e.className==='brand').textContent;};
  assert.equal(brand('inv-1'),'AIMMOD · MULTIPLAYER');assert.equal(brand('tmr-1-2'),'AIMMOD · TOURNAMENT');assert.equal(brand('tci-9'),'AIMMOD · TOURNAMENT');
  assert.equal(brand('fr-a-1'),'AIMMOD · FRIENDS');assert.equal(brand('dev-up-1'),'AIMMOD · DEVELOPER TEST');
});
test('CS buy lines and key clashes share the kill feed backing',()=>{
  const n=setup();n.render({version:1,active:false,cs:{alive:true,health:100,armor:0,side:'CT',score:[1,2],round:4,phase:'freeze',money:800,buyKey:'B',useKey:'E',buy:[{key:'1',label:'Pistol',price:200,affordable:true},{key:'2',label:'Rifle',price:2700,affordable:false}],keyClashes:['B is also bound to something else']}});
  const lines=n.box.all().filter(e=>e.className==='feed-text').map(e=>e.textContent);
  assert.equal(lines.length,3);assert.ok(n.box.all().some(e=>e.className==='feed victim'));
});
test('standings show unknown values as a dash and spell out the frag limit',()=>{
  class El{constructor(t){this.children=[];this.className='';this.textContent='';}appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}all(){return this.children.flatMap(c=>[c,...c.all()]);}}
  const window={document:{createElement:t=>new El(t)}};vm.runInNewContext(fs.readFileSync(path.join(__dirname,'standings.js'),'utf8'),{window});
  const target=new El('div');window.AimModStandings.render(target,{kind:'combat',title:'Deathmatch',fragLimit:20,rows:[{rank:1,name:'Synthetic One',self:true,frags:3,deaths:null,kd:null}]},'full');
  const text=target.all().map(e=>e.textContent);
  assert.ok(text.includes('First to 20 frags'));assert.ok(text.includes('—'));assert.ok(!text.includes('-'));
});
test('the CS scoreboard lists both sides with players and bots, money for your side, K/D and who is down',()=>{
  class El{constructor(t){this.children=[];this.className='';this.textContent='';}appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}all(){return this.children.flatMap(c=>[c,...c.all()]);}}
  const window={document:{createElement:t=>new El(t)}};vm.runInNewContext(fs.readFileSync(path.join(__dirname,'standings.js'),'utf8'),{window});
  const target=new El('div');
  window.AimModStandings.render(target,{mode:'cs',kind:'cs',title:'CS competitive',phase:'live',round:1,rounds:24,left:95,
    teams:[{team:2,name:'Counter-Terrorists',total:1,self:true,side:'CT',alive:2,players:3},{team:1,name:'Terrorists',total:0,self:false,side:'T',alive:1,players:3}],
    rows:[{name:'Synthetic One',self:true,team:2,frags:2,deaths:0,kd:2,money:3250,status:'alive'},{name:'BOT Echo',bot:true,team:2,frags:0,deaths:1,kd:0,money:1400,status:'down'},{name:'BOT Ace',bot:true,team:2,frags:1,deaths:0,kd:1,money:900,status:'alive'},
      {name:'BOT Kilo',bot:true,team:1,frags:1,deaths:1,kd:1,status:'down'},{name:'BOT Nyx',bot:true,team:1,frags:0,deaths:1,kd:0,status:'down'},{name:'Synthetic Two',team:1,frags:0,deaths:1,kd:0,status:'alive'}]},'full');
  const all=target.all(),text=all.map(e=>e.textContent);
  const sides=all.filter(e=>/sb-cs-side/.test(e.className));
  assert.equal(sides.length,2);assert.match(sides[0].className,/ct self/);
  assert.equal(all.filter(e=>/(^| )sb-row( |$)/.test(e.className)&&!/sb-cols|sb-cs-title/.test(e.className)).length,6,'every player and bot has a row');
  assert.ok(text.includes('1:35')&&text.includes('$3,250')&&text.includes('2 of 3 alive')&&text.includes('DEAD')&&text.includes('Counter-Terrorists · your team'));
  assert.ok(!text.some(t=>/178:/.test(t)));
  assert.equal(all.filter(e=>e.className==='sb-bot').length,4);
});
test('standings tag bots once, before the name',()=>{
  class El{constructor(t){this.children=[];this.className='';this.textContent='';}appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}all(){return this.children.flatMap(c=>[c,...c.all()]);}}
  const window={document:{createElement:t=>new El(t)}};vm.runInNewContext(fs.readFileSync(path.join(__dirname,'standings.js'),'utf8'),{window});
  const target=new El('div');window.AimModStandings.render(target,{kind:'combat',title:'Deathmatch',fragLimit:20,rows:[{rank:1,name:'BOT Ace',bot:true,frags:3,deaths:1,kd:3},{rank:2,name:'Synthetic One',self:true,frags:1,deaths:3,kd:0.33}]},'full');
  const tags=target.all().filter(e=>e.className==='sb-bot').map(e=>e.textContent);
  assert.deepEqual(tags,['BOT']);
  const names=target.all().filter(e=>e.className==='sb-name').map(e=>e.children.map(c=>c.textContent).join(' '));
  assert.deepEqual(names.slice(-2),['BOT Ace','Synthetic One (you)']);
});
// Answerable notices: the buttons survive HUD ticks and missed polls, and the layer asks for clicks.
function live(){
  class El{constructor(tag){this.tag=tag;this.children=[];this.style={};this.className='';this.textContent='';}
    appendChild(c){this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(){}all(){return this.children.flatMap(c=>[c,...c.all()]);}get tagName(){return this.tag.toUpperCase();}
    getBoundingClientRect(){return this.rect||{left:-1,top:-1,right:-1,bottom:-1};}}
  const box=new El('main'),body=new El('body'),html=new El('html');const polls=[],posts=[];let next=null;const listeners={},engineOn={};
  class Xhr{open(m){this.m=m;}setRequestHeader(){}send(b){if(this.m==='GET')polls.push(this);else posts.push(JSON.parse(b));}}
  const window={document:{createElement:t=>new El(t),getElementById:id=>id==='notice'?box:null,body,documentElement:html,addEventListener:(t,f)=>{listeners[t]=f;}},XMLHttpRequest:Xhr,location:{pathname:'/cap/notify'},
    engine:{on:(name,f)=>{engineOn[name]=f;}}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'notify.js'),'utf8'),{window,setTimeout:f=>{next=f;return 1;},clearTimeout:()=>{}});
  // Answers the oldest poll, then lets the next one go out.
  const reply=(status,n)=>{const x=polls.shift();x.readyState=4;x.status=status;x.responseText=n?JSON.stringify(n):'';x.onreadystatechange();if(!polls.length&&next)next();};
  return {render:window.AimModNotify.render,filePointer:window.AimModNotify.filePointer,box,body,html,posts,reply,listeners,engineOn,buttons:()=>box.all().filter(e=>/\bbutton\b/.test(e.className))};
}
const failed={version:1,active:true,id:'lf-m1-1',kind:'invite',eyebrow:'AimMod · Match',title:'Couldn’t load the match (1/2)',body:'Synthetic Two: still loading.',layout:'toast',interactive:true,
  actions:[{label:'Retry',action:'retry-load',id:'m1'},{label:'Abort',action:'end',id:'m1'}]};
test('a load failure keeps the same Retry and Abort buttons while the HUD ticks, and they answer',()=>{
  const n=live();n.render(failed);const [retry,abort]=n.buttons();
  assert.equal(retry.textContent,'Retry');assert.equal(abort.textContent,'Abort');
  for(let s=9;s>0;s--)n.render(Object.assign({},failed,{combat:{alive:true,health:100,max:100,frags:0,fragLimit:10,left:s,feed:[]}}));
  assert.equal(n.buttons()[0],retry,'the toast is not rebuilt for HUD data');
  retry.onclick();assert.deepEqual(n.posts[0],{action:'retry-load',id:'m1'});
  assert.equal(n.box.all().find(e=>e.className==='brand').textContent,'AIMMOD · MATCH');
});
test('the toast layer takes clicks only while a notice asks for them, never in the full-screen layout',()=>{
  const n=live();n.render(failed);
  assert.equal(n.body.className,'input');assert.equal(n.html.className,'input');
  assert.ok(n.box.all().some(e=>e.className==='toast has-actions'));
  n.render(Object.assign({},failed,{layout:'full'}));assert.equal(n.body.className,'');
  // The CS buy menu holds the cursor: the full-screen layer takes every click (none reach the game).
  n.render({version:1,active:false,layout:'full',interactive:true,cursor:true,cs:{phase:'freeze',buyOpen:true,alive:true,health:100,armor:0,side:'T',score:[0,0],round:1,money:800,buyKey:'B',useKey:'E'}});
  assert.equal(n.body.className,'input');assert.equal(n.html.className,'input');
  n.render({version:1,active:false,layout:'full',interactive:false,cs:{phase:'freeze',buyOpen:false,alive:true,health:100,armor:0,side:'T',score:[0,0],round:1,money:800,buyKey:'B',useKey:'E'}});assert.equal(n.body.className,'');
  n.render({version:1,active:true,id:'cd-1',kind:'countdown',title:'Match starting in 3',body:'',countdown:3,layout:'toast'});
  assert.equal(n.body.className,'');assert.equal(n.buttons().length,0);
  const css=fs.readFileSync(path.join(__dirname,'notify.css'),'utf8');
  assert.match(css,/html\.input,body\.input,body\.input #notice,\.toast\.has-actions\{pointer-events:auto\}/);
});
test('a missed poll leaves the buttons in place; a run of misses clears the layer',()=>{
  const n=live();n.reply(200,failed);const retry=n.buttons()[0];
  n.reply(0,null);assert.equal(n.buttons()[0],retry);
  for(let i=0;i<8;i++)n.reply(0,null);
  assert.equal(n.buttons().length,0);assert.equal(n.box.className,'');
});
test('invite notices show who it is from: initials first, their Steam picture once it loads, without rebuilding the buttons',()=>{
  // Synthetic names and ids only.
  const invite={version:1,active:true,id:'inv-inv-123456',kind:'invite',title:'Synthetic Friend invited you',body:'Click Join, or press F7.',key:'F7',layout:'toast',interactive:true,
    actions:[{label:'Join',action:'accept-invite',id:'inv-123456'},{label:'Dismiss',action:'decline-invite',id:'inv-123456'}],person:{name:'Synthetic Friend',avatar:null}};
  const n=live();n.render(invite);
  const who=n.box.all().find(e=>/^who /.test(e.className));assert.ok(who,'a circle for the sender');assert.equal(who.textContent,'SF');assert.equal(who.children.length,0);
  const join=n.buttons()[0];
  const url='/avatar/76561190000000105.png?v=33334444';
  n.render(Object.assign({},invite,{person:{name:'Synthetic Friend',avatar:url}}));
  assert.equal(n.buttons()[0],join,'the toast is not rebuilt when the picture arrives');
  const img=who.children[0];assert.ok(img&&img.className==='who-img'&&img.src==='/cap'+url,'the picture goes into the same circle');
  assert.ok(!/pic/.test(who.className));img.onload();assert.match(who.className,/ pic$/);
  n.render(Object.assign({},invite,{person:{name:'Synthetic Friend',avatar:'https://example.invalid/a.png'}}));
  assert.equal(who.children.length,0,'foreign links are never loaded');assert.ok(!/pic/.test(who.className));
  n.render(Object.assign({},invite,{person:{name:'Synthetic Friend',avatar:url}}));
  assert.match(who.className,/ pic$/,'a picture seen before shows at once');who.children[0].onerror();
  assert.equal(who.children.length,0);assert.ok(!/pic/.test(who.className),'a broken picture leaves the initials');
  n.render({version:1,active:true,id:'cd-1',kind:'countdown',title:'Match starting in 3',body:'',countdown:3,layout:'toast'});
  assert.ok(!n.box.all().some(e=>/^who /.test(e.className)),'countdowns keep their number, no circle');
  const css=fs.readFileSync(path.join(__dirname,'notify.css'),'utf8');
  assert.match(css,/\.who\{[^}]*width:44px;height:44px[^}]*border-radius:22px;overflow:hidden/);
  assert.match(css,/\.who-img\{position:absolute;left:0;top:0;width:44px;height:44px;border-radius:22px;border:1px solid/);
});
test('a notice can carry a short extra line, such as a keybind that differs',()=>{
  const n=setup();n.render({version:1,active:true,id:'ld-m1-0',kind:'countdown',title:'Waiting for everyone to load (1/2)',body:'Loading…',note:'Walk is on Q here (usually Shift)'});
  assert.ok(n.box.all().some(e=>e.className==='note'&&e.textContent==='Walk is on Q here (usually Shift)'));
  const k=setup();k.render({version:1,active:true,id:'keys-m1',kind:'info',eyebrow:'AimMod · Keybinds',title:'Walk is on Q here (usually Shift)',body:'',key:'F7'});
  assert.equal(k.box.all().find(e=>e.className==='brand').textContent,'AIMMOD · KEYBINDS');assert.ok(!k.box.all().some(e=>e.className==='note'));
});
test('a sender named only with punctuation keeps those characters in the circle',()=>{
  const n=live();n.render({version:1,active:true,id:'inv-inv-1',kind:'invite',title:'-.- invited you',body:'',layout:'toast',interactive:true,actions:[{label:'Join',action:'accept-invite',id:'inv-1'}],person:{name:'-.-',avatar:null}});
  assert.equal(n.box.all().find(e=>/^who /.test(e.className)).textContent,'-.');
});
test('AimModCore\'s pointer fallback hovers and presses the button under the cursor, once, and stands aside for Gameface clicks',()=>{
  const n=live();n.render(failed);
  const [retry,abort]=n.buttons();retry.rect={left:100,top:200,right:180,bottom:232};abort.rect={left:190,top:200,right:270,bottom:232};
  const pointer=n.engineOn.AimModPointer;assert.equal(typeof pointer,'function','listens for AimModPointer');
  pointer(120,210,false,1920,1080);assert.ok(/\bhover\b/.test(retry.className),'hover over Retry');
  pointer(120,210,true,1920,1080);pointer(121,211,false,1920,1080);
  assert.deepEqual(n.posts.filter(p=>p.action==='retry-load'),[{action:'retry-load',id:'m1'}],'released over Retry: pressed once');
  pointer(200,210,false,1920,1080);assert.ok(!/\bhover\b/.test(retry.className)&&/\bhover\b/.test(abort.className),'hover moves to Abort');
  pointer(200,210,true,1920,1080);pointer(20,20,false,1920,1080);
  assert.equal(n.posts.filter(p=>p.action==='end').length,0,'released elsewhere: nothing pressed');
  // Gameface's own click arrived: the fallback doesn't press a second time.
  n.listeners.mousedown();pointer(120,210,true,1920,1080);pointer(120,210,false,1920,1080);
  assert.equal(n.posts.filter(p=>p.action==='retry-load').length,1,'no double press');
});
test('the pointer relayed through the service hovers, replays new clicks once, and never replays clicks from before',()=>{
  const n=live();n.render(failed);
  const [retry]=n.buttons();retry.rect={left:100,top:200,right:180,bottom:232};
  const fp=n.filePointer;
  fp('AIMMOD_POINTER_1\ton\t120\t210\t0\t1920\t1080\nclick\t4\t120\t210\t120\t210\n');
  assert.ok(/\bhover\b/.test(retry.className),'hover from the relayed cursor');
  assert.equal(n.posts.filter(p=>p.action==='retry-load').length,0,'a click from before the menu opened is not replayed');
  fp('AIMMOD_POINTER_1\ton\t120\t210\t0\t1920\t1080\nclick\t4\t120\t210\t120\t210\nclick\t5\t110\t205\t150\t220\n');
  assert.equal(n.posts.filter(p=>p.action==='retry-load').length,1,'a new click down and up on Retry presses it');
  fp('AIMMOD_POINTER_1\ton\t120\t210\t0\t1920\t1080\nclick\t5\t110\t205\t150\t220\n');
  assert.equal(n.posts.filter(p=>p.action==='retry-load').length,1,'each click once');
  fp('AIMMOD_POINTER_1\ton\t10\t10\t0\t1920\t1080\nclick\t6\t110\t205\t10\t10\n');
  assert.equal(n.posts.filter(p=>p.action==='retry-load').length,1,'released elsewhere: nothing');
  fp('AIMMOD_POINTER_1\toff\t0\t0\t0\t0\t0\nclick\t7\t110\t205\t150\t220\n');
  assert.equal(n.posts.filter(p=>p.action==='retry-load').length,1,'off: ignored');
});
