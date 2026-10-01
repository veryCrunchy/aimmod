// Test helper: a small DOM for the overlay scripts (elements, styles, events, canvas)
// and a vm context that loads the overlay model, widgets and a page script.
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
class Ctx2d{constructor(){this.calls=[];this.globalAlpha=1;}}
for(const m of ['clearRect','fillRect','strokeRect','beginPath','moveTo','lineTo','stroke','fill','arc','closePath','fillText','drawImage'])Ctx2d.prototype[m]=function(...a){this.calls.push([m,...a]);};
class El{
  constructor(tag,doc){this.tagName=String(tag).toUpperCase();this.children=[];this.style={};this.attrs={};this.className='';this.text='';this.doc=doc;this.parentNode=null;this.listeners={};this.offsetWidth=0;this.offsetHeight=40;this.value='';}
  get childNodes(){return this.children;}
  appendChild(c){if(c.parentNode)c.parentNode.removeChild(c);c.parentNode=this;this.children.push(c);return c;}
  replaceChild(n,o){const i=this.children.indexOf(o);if(n.parentNode)n.parentNode.removeChild(n);n.parentNode=this;this.children[i]=n;o.parentNode=null;return o;}
  removeChild(c){const i=this.children.indexOf(c);if(i>=0)this.children.splice(i,1);c.parentNode=null;return c;}
  set textContent(v){this.text=String(v);this.children.forEach(c=>c.parentNode=null);this.children=[];}
  get textContent(){return this.text+this.children.map(c=>c.textContent).join('');}
  setAttribute(k,v){this.attrs[k]=String(v);}getAttribute(k){return this.attrs[k];}
  addEventListener(e,f){(this.listeners[e]=this.listeners[e]||[]).push(f);}removeEventListener(e,f){this.listeners[e]=(this.listeners[e]||[]).filter(x=>x!==f);}
  focus(){this.doc.activeElement=this;}select(){this.selected=true;}
  getContext(){return this.ctx||(this.ctx=new Ctx2d());}
  getBoundingClientRect(){return {left:0,top:0,width:this.offsetWidth,height:this.offsetHeight};}
  all(){return this.children.flatMap(c=>[c,...c.all()]);}
  find(fn){return this.all().find(fn);}
  findAll(fn){return this.all().filter(fn);}
}
function makeDocument(){
  const doc={activeElement:null,listeners:{},roots:[],createElement:t=>new El(t,doc),
    getElementById:id=>{for(const r of doc.roots){if(r.id===id)return r;const f=r.find(x=>x.id===id);if(f)return f;}return null;},
    addEventListener(e,f){(doc.listeners[e]=doc.listeners[e]||[]).push(f);},removeEventListener(e,f){doc.listeners[e]=(doc.listeners[e]||[]).filter(x=>x!==f);},
    dispatch(e,ev){(doc.listeners[e]||[]).slice().forEach(f=>f(ev));},execCommand:()=>true};
  return doc;
}
function makeXhr(log){
  return class Xhr{constructor(){log.push(this);this.headers={};}open(method,url){this.method=method;this.url=url;}setRequestHeader(k,v){this.headers[k]=v;}send(body){this.body=body;}abort(){this.aborted=true;}
    finish(status,data){this.status=status;this.readyState=4;this.responseText=typeof data==='string'?data:JSON.stringify(data);this.onreadystatechange();}};
}
const UI=__dirname;
function load(window,files){const context=vm.createContext({window,Date,Math,JSON});for(const f of files)vm.runInContext(fs.readFileSync(path.join(UI,f),'utf8'),context,{filename:f});return context;}
function makeWindow(extra){
  const document=makeDocument(),requests=[],timers=[];
  const window=Object.assign({document,XMLHttpRequest:makeXhr(requests),location:{pathname:'/cap/ui',search:''},innerWidth:1920,innerHeight:1080,
    setTimeout:(fn,delay)=>{timers.push({fn,delay,id:timers.length+1});return timers.length;},clearTimeout:id=>{const t=timers.find(x=>x.id===id);if(t)t.cleared=true;},addEventListener:()=>{}},extra||{});
  return {window,document,requests,timers,run(){const due=timers.filter(t=>!t.cleared&&!t.ran);due.forEach(t=>{t.ran=true;t.fn();});return due.length;}};
}
module.exports={El,makeDocument,makeWindow,load};
