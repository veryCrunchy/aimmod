const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
function setup(){
  class El{constructor(tag){this.tag=tag;this.children=[];this.attrs={};this.style={};this.parentNode=null;this.className='';this.textContent='';}
    appendChild(c){c.parentNode=this;this.children.push(c);return c;}removeChild(c){this.children.splice(this.children.indexOf(c),1);c.parentNode=null;}
    get firstChild(){return this.children[0];}setAttribute(k,v){this.attrs[k]=v;}
    all(){return this.children.flatMap(c=>[c,...c.all()]);}
    querySelector(sel){return this.all().find(c=>sel.startsWith('.')&&c.className.split(' ').includes(sel.slice(1)))||null;}
    text(){return [this.textContent,...this.children.map(c=>c.text())].join(' ');}}
  const requests=[],timers=[],body=new El('body'),head=new El('head');
  class Xhr{constructor(){requests.push(this);this.headers={};}open(m,u){this.method=m;this.url=u;}setRequestHeader(k,v){this.headers[k]=v;}send(b){this.body=b;}abort(){this.aborted=true;}
    finish(status,data){this.status=status;this.responseText=JSON.stringify(data);this.readyState=4;this.onreadystatechange();}}
  const window={document:{createElement:t=>new El(t),getElementById:()=>null,body,head},XMLHttpRequest:Xhr,location:{pathname:'/cap/ui'},
    setInterval:()=>1,clearInterval:()=>{},setTimeout:(f)=>{timers.push(f);return 1;}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'lifecycle.js'),'utf8'),{window});
  return {api:window.AimModLifecycle,requests,body,El,buttons:(root)=>root.all().filter(c=>c.tag==='button')};
}
function snapshot(overrides){
  const s={installed:{found:true,managed:true,version:'1.0.0',channel:'stable'},settings:{autoUpdate:true,channel:'stable'},
    update:{state:'up-to-date',version:'1.0.0',notes:null,message:null,applyOnClose:false},
    repair:{needed:false,problems:[],requested:false,available:true,interrupted:false},
    game:{steamBuildId:25635011,tested:true,testedVersion:'3.9.11',warning:null},rollback:{available:false,version:null},last:null};
  for(const k of Object.keys(overrides||{}))s[k]=Object.assign({},s[k],overrides[k]);
  if(overrides&&overrides.last)s.last=overrides.last;
  return s;
}
test('a staged update shows one toast with plain-text release notes and a banner',()=>{
  const t=setup(),banner=new t.El('div');let opened=0;
  t.api.start({banner,openSettings:()=>opened++});
  assert.equal(t.requests[0].url,'/cap/lifecycle');
  t.requests[0].finish(200,snapshot({update:{state:'ready',version:'1.1.0',notes:'## Features\n- **Faster** replays ([#12](https://x))',applyOnClose:true}}));
  const toast=t.body.children.find(c=>c.className==='lifecycle-toast');
  assert.ok(toast,'toast shown');
  assert.match(toast.text(),/Update ready/);assert.match(toast.text(),/applies when you close KovaaK’s/);
  assert.match(toast.text(),/- Faster replays/);assert.doesNotMatch(toast.text(),/\*\*|##|\]\(/);
  assert.match(banner.text(),/Update ready: AimMod 1\.1\.0 applies when you close KovaaK’s/);
  t.buttons(toast).find(b=>b.textContent==='Details').onclick();assert.equal(opened,1);
  t.api.visible(true);t.requests.at(-1).finish(200,snapshot({update:{state:'ready',version:'1.1.0',notes:'x',applyOnClose:true}}));
  assert.equal(t.body.children.filter(c=>c.className==='lifecycle-toast').length,0,'same version is announced once');
});
test('repair prompt asks the service to repair after the game closes',()=>{
  const t=setup(),banner=new t.El('div');t.api.start({banner});
  t.requests[0].finish(200,snapshot({repair:{needed:true,problems:['The UE4SS loader (dwmapi.dll) is missing: dwmapi.dll']}}));
  assert.match(banner.text(),/AimMod needs a repair\. The UE4SS loader/);
  t.buttons(banner).find(b=>/Repair when I close/.test(b.textContent)).onclick();
  const post=t.requests.at(-1);assert.equal(post.method,'POST');assert.equal(post.url,'/cap/lifecycle/action');
  assert.equal(post.headers['X-AimMod-UI'],'1');assert.deepEqual(JSON.parse(post.body),{action:'repair'});
  post.finish(200,snapshot({repair:{needed:true,requested:true,problems:['x']}}));
  assert.match(banner.text(),/Repair scheduled/);
});
test('settings toggle auto-update and switch channel with strict patches',()=>{
  const t=setup(),box=new t.El('div');t.api.start({});t.requests[0].finish(200,snapshot());
  t.api.renderSettings(box);t.requests.at(-1).finish(200,snapshot());
  const toggle=t.buttons(box).find(b=>b.attrs['aria-label']==='Automatic updates');assert.equal(toggle.attrs['aria-checked'],'true');
  toggle.onclick();let post=t.requests.at(-1);assert.equal(post.url,'/cap/lifecycle/settings');assert.deepEqual(JSON.parse(post.body),{autoUpdate:false});
  post.finish(200,snapshot({settings:{autoUpdate:false}}));
  assert.equal(t.buttons(box).find(b=>b.attrs['aria-label']==='Automatic updates').textContent,'Off');
  t.buttons(box).find(b=>b.textContent==='Beta').onclick();post=t.requests.at(-1);assert.deepEqual(JSON.parse(post.body),{channel:'beta'});
  post.finish(503,{});assert.match(box.text(),/Could not save your settings/);
});
test('downloaded update without auto-install offers an explicit install and notes',()=>{
  const t=setup(),box=new t.El('div');t.api.start({});t.requests[0].finish(200,snapshot());
  t.api.renderSettings(box);t.requests.at(-1).finish(200,snapshot({settings:{autoUpdate:false},update:{state:'ready',version:'1.2.0',notes:'Fixes',applyOnClose:false}}));
  assert.match(box.text(),/What’s new in 1\.2\.0/);
  t.buttons(box).find(b=>/Install when I close/.test(b.textContent)).onclick();
  assert.deepEqual(JSON.parse(t.requests.at(-1).body),{action:'install'});
});
test('developer installs hide update controls and unknown builds warn',()=>{
  const t=setup(),box=new t.El('div'),banner=new t.El('div');t.api.start({banner});
  t.requests[0].finish(200,snapshot({installed:{managed:false,version:null},repair:{needed:true,problems:['An AimMod file was replaced or changed: main.dll']},update:{state:'unmanaged',message:'This install was made with the developer installer; updates are off.'},game:{tested:false,warning:'KovaaK’s was updated (Steam build 1).'}}));
  assert.doesNotMatch(banner.text(),/needs a repair/,'developer rebuilds do not raise the repair banner');
  t.api.renderSettings(box);t.requests.at(-1).finish(200,snapshot({update:{state:'unmanaged',message:'developer installer'},game:{tested:false,warning:'KovaaK’s was updated (Steam build 1).'}}));
  assert.equal(t.buttons(box).find(b=>b.attrs['aria-label']==='Automatic updates'),undefined);
  assert.match(banner.text(),/KovaaK’s was updated/);
});
test('malformed responses are ignored',()=>{
  const t=setup(),banner=new t.El('div');t.api.start({banner});t.requests[0].finish(200,{update:null});
  assert.equal(banner.children.length,0);
});
