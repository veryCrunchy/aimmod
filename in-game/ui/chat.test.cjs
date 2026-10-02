const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
function setup(){
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.className='';this.textContent='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}
    setAttribute(k,v){this.attrs[k]=String(v);}}
  const listeners={},engine={};
  const window={document:{createElement:t=>new El(t),addEventListener:(k,f)=>{listeners[k]=f;}},engine:{on:(k,f)=>{engine[k]=f;}}};
  const ctx=vm.createContext({window,Date});
  vm.runInContext(fs.readFileSync(path.join(__dirname,'chat.js'),'utf8'),ctx);
  const walk=e=>[e,...e.children.flatMap(walk)];
  const sent=[];
  const chat=window.AimModChat,root=new El('section');
  return {chat,root,sent,walk,listeners,engine,text:()=>walk(root).filter(e=>e.style.display!=='none').map(e=>e.textContent||'').join('|'),
    render:c=>chat.render(root,c,b=>sent.push(JSON.parse(JSON.stringify(b)))),type:(s,from,core=1)=>{let id=from;for(const ch of s){engine.AimModKey(ch.charCodeAt(0),++id,core);}return id;}};
}
const line=(o)=>Object.assign({id:1,scope:'all',name:'Nova',team:1,dead:false,place:null,text:'gl hf',radio:false,you:false,age:0},o);
const view=(o)=>Object.assign({open:null,session:1,teams:true,allKey:'Y',teamKey:'U',radioKeys:['Z','X','C'],cs:true,lines:[],radio:null,clashes:[],closed:null},o);

test('the feed shows scope, name in team colour, the dead marker and the place, and fades after 10 s',()=>{
  const s=setup();
  s.render(view({lines:[line({id:1}),line({id:2,scope:'team',name:'Ace',team:2,dead:true,place:'A Site',text:'Enemy spotted',radio:true}),line({id:3,scope:'system',name:'',team:0,text:'Moss left.'})]}));
  const t=s.text();
  assert.ok(t.includes('[all]|Nova|:|gl hf'),'all line: prefix, name, text');
  assert.ok(t.includes('*DEAD*|[team]|Ace|@ A Site|:|Enemy spotted'),'team line: dead, prefix, name, place');
  assert.ok(t.includes('Moss left.'),'system lines');
  const names=s.walk(s.root).filter(e=>/chat-name/.test(e.className));
  assert.deepEqual(names.map(e=>e.className),['chat-name team1','chat-name team2'],'names in their team colour');
  assert.ok(/ cs/.test(s.root.className),'CS: above the money');
  s.render(view({lines:[line({id:1,age:9500}),line({id:2,age:12000,text:'old'})]}));
  const rows=s.walk(s.root).filter(e=>/chat-line/.test(e.className));
  assert.equal(rows[0].style.opacity,'0.5','fading over its last second');
  assert.equal(rows[1].style.display,'none','gone after 10 s');
  s.render(view({open:'all',session:1,lines:[line({id:1,age:9500}),line({id:2,age:12000,text:'old'})]}));
  assert.ok(s.text().includes('old')&&s.walk(s.root).filter(e=>/chat-line/.test(e.className)).every(e=>e.style.opacity==='1'),'all lines come back while typing');
  s.render(null);assert.equal(s.root.className,'');
});

test('typing: relayed characters, Backspace (surrogate pairs too), Enter sends once with the session',()=>{
  const s=setup();
  s.render(view({open:'team',session:4}));
  assert.ok(s.text().includes('Say (team):'),'the input says where the line goes');
  let id=s.type('Rotate B!',0);
  s.engine.AimModKey(8,++id,1);
  s.engine.AimModKey(0xD83D,++id,1);s.engine.AimModKey(0xDE00,++id,1);
  assert.equal(s.chat.state().draft,'Rotate B😀','Unicode: both halves of the pair');
  s.engine.AimModKey(8,++id,1);
  assert.equal(s.chat.state().draft,'Rotate B','Backspace removes the whole pair');
  assert.ok(s.text().includes('Rotate B'),'the draft shows as it is typed');
  s.engine.AimModKey(7,++id,1);
  assert.equal(s.chat.state().draft,'Rotate B','other control characters are ignored');
  s.engine.AimModKey(13,++id,1);
  assert.deepEqual(s.sent,[{action:'chat-send',session:4,text:'Rotate B'}],'Enter sends the line');
  s.engine.AimModKey(13,++id,1);s.type('x',id);
  assert.equal(s.sent.length,1,'nothing more until a new input opens');
  s.render(view({open:'team',session:4}));
  assert.ok(!s.chat.typing(),'the service still showing it open does not reopen it');
  s.render(view({open:'all',session:5}));
  assert.ok(s.chat.typing()&&s.chat.state().draft==='','a new input starts empty');
  s.engine.AimModKey(27,id+5,2);
  assert.deepEqual(s.sent[1],{action:'chat-close',session:5},'Escape cancels');
});

test('each character is taken once: the relay and the polled file share ids; one source per input',()=>{
  const s=setup();
  s.render(view({open:'all',session:1}));
  s.engine.AimModKey(104,1,1);s.engine.AimModKey(105,2,1);
  s.chat.fileKeys([{id:1,code:104},{id:2,code:105},{id:3,code:33}],1);
  assert.equal(s.chat.state().draft,'hi!','the file fills in what the relay missed, never twice');
  s.listeners.keypress({charCode:120,preventDefault(){}});
  assert.equal(s.chat.state().draft,'hi!','Gameface keys are ignored once AimModCore relays');
  s.render(view({open:'all',session:2}));
  s.listeners.keypress({charCode:111,preventDefault(){}});s.listeners.keypress({charCode:107,preventDefault(){}});
  s.engine.AimModKey(122,4,1);
  assert.equal(s.chat.state().draft,'ok','without the relay Gameface keys type, and then the relay is ignored');
  s.listeners.keydown({keyCode:13,preventDefault(){}});
  assert.deepEqual(s.sent.at(-1),{action:'chat-send',session:2,text:'ok'});
});

test('keys typed just before the page saw the input open still count; Enter the service saw first still sends',()=>{
  const s=setup();
  s.render(view({}));
  s.engine.AimModKey(103,1,1);s.engine.AimModKey(103,2,1);
  s.render(view({open:'all',session:3}));
  assert.equal(s.chat.state().draft,'gg','early keys are applied to the new input');
  s.render(view({open:null,session:3,closed:'enter'}));
  assert.deepEqual(s.sent,[{action:'chat-send',session:3,text:'gg'}],'the service closed on Enter first: the line still goes');
  s.engine.AimModKey(13,3,1);
  assert.equal(s.sent.length,1,'the late Enter sends nothing more');
  s.render(view({open:'all',session:4}));s.type('no',10,2);assert.equal(s.chat.state().draft,'no','the next input types again');
  s.render(view({open:null,session:4,closed:'escape'}));
  assert.equal(s.sent.length,1,'closed by Escape or alt-tab: nothing is sent');
});

test('the radio menu: numbered callouts that click, kept in place between polls; clashes while typing',()=>{
  const s=setup();
  const radio={group:1,key:'X',title:'Reports',items:['Enemy spotted','Need backup']};
  s.render(view({radio}));
  const find=()=>s.walk(s.root).find(e=>e.tag==='button'&&e.children[1]&&e.children[1].textContent==='Need backup');
  const before=find();
  assert.ok(s.text().includes('Radio · Reports (X)')&&s.text().includes('1|Enemy spotted'),'title, key and numbered items');
  s.render(view({radio,lines:[line({})]}));
  assert.equal(find(),before,'same button after a feed change');
  before.onclick();
  assert.deepEqual(s.sent,[{action:'chat-radio',group:1,item:1}]);
  s.render(view({}));assert.ok(!find(),'closed');
  s.render(view({open:'all',session:9,clashes:['KovaaK’s also uses Y (all chat).']}));
  assert.ok(s.text().includes('KovaaK’s also uses Y (all chat).')&&s.text().includes('Enter sends · Esc cancels'),'KovaaK’s binds on the chat keys, as a note');
});

test('the notice layer: the chat takes input while open, and the polled pointer file types too',()=>{
  class El{constructor(tag){this.tag=tag;this.style={};this.children=[];this.attrs={};this.className='';this.textContent='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);}get firstChild(){return this.children[0];}setAttribute(k,v){this.attrs[k]=String(v);}}
  const els={},byId=id=>els[id]||(els[id]=new El('div'));
  const posts=[];class Xhr{open(m,u){this.m=m;this.u=u;}setRequestHeader(){}send(b){if(this.m==='POST')posts.push(JSON.parse(b));}}
  const body=new El('body'),page=new El('html');
  const window={document:{createElement:t=>new El(t),getElementById:byId,body,documentElement:page,addEventListener(){}},XMLHttpRequest:Xhr,location:{pathname:'/cap/notify'},engine:{on(){}}};
  const ctx=vm.createContext({window,Date,setTimeout:()=>1,clearTimeout:()=>{}});
  for(const f of ['chat.js','notify.js'])vm.runInContext(fs.readFileSync(path.join(__dirname,f),'utf8'),ctx);
  window.AimModNotify.render({version:1,active:false,layout:'full',interactive:true,typing:true,cursor:false,chat:view({open:'all',session:2})});
  assert.equal(body.className,'input','the full-screen layer takes input while the chat input is open');
  assert.ok(/show/.test(els['chat-hud'].className)&&/typing/.test(els['chat-hud'].className),'the chat layer shows the input');
  window.AimModNotify.filePointer('AIMMOD_POINTER_1\toff\t0\t0\t0\t0\t0\nkeys\t7\nkey\t1\t104\nkey\t2\t105\nkey\t3\t13\n');
  assert.deepEqual(posts.map(p=>JSON.stringify(p)),[JSON.stringify({action:'chat-send',session:2,text:'hi'})],'typed from the file, Enter sends');
  window.AimModNotify.render({version:1,active:false,layout:'full',chat:view({lines:[line({})]})});
  assert.equal(body.className,'','closed: click-through again');
});