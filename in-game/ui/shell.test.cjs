const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
test('navigation arrows wrap, skip hidden pages and leave activation to Enter',()=>{
  const html=fs.readFileSync(path.join(__dirname,'index.html'),'utf8');
  let focused=null,activated=null;
  function node(){return {style:{},children:[],appendChild(c){this.children.push(c);},focus(){focused=this;}};}
  const nav=node();nav.getElementsByTagName=()=>nav.children;
  const context={el:()=>nav,node,navIcon:node,titles:{home:['Home'],history:['History'],settings:['Settings'],developer:['Developer']},groups:{},change:k=>activated=k};
  vm.runInNewContext(html.slice(html.indexOf('function buildNav()'),html.indexOf('function change(page)')),context);
  context.buildNav();const [home,history,settings,hidden]=nav.children;hidden.style.display='none';
  function key(button,name){let prevented=false;button.onkeydown({key:name,preventDefault(){prevented=true;}});return prevented;}
  assert.equal(key(home,'ArrowUp'),true);assert.equal(focused,settings);
  key(settings,'ArrowDown');assert.equal(focused,home);
  key(home,'End');assert.equal(focused,settings);
  key(settings,'Home');assert.equal(focused,home);
  key(home,'ArrowDown');assert.equal(focused,history);assert.equal(activated,null);
  assert.equal(key(history,'Tab'),false);history.onclick();assert.equal(activated,'history');
});
test('shared stylesheet is embedded and routed by the service',()=>{
  const read=f=>fs.readFileSync(path.join(__dirname,'..','native-service',f),'utf8');
  assert.match(read('AimMod.InGame.csproj'),/shared-components\.css" LogicalName="AimMod.SharedComponents"/);
  assert.match(read('WorkspaceHost.cs'),/"\/shared-components\.css".*GetManifestResourceStream\("AimMod.SharedComponents"\)/);
});
