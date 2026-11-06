const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const context={window:{}};
vm.runInNewContext(fs.readFileSync(__dirname+'/overlay-model.js','utf8'),context);
const M=context.window.AimModOverlayModel;
test('binding paths preserve zero and reject prototype traversal',()=>{
  const data={live:{score:0,active:false},rows:[{score:42}]};
  assert.equal(M.value(data,'live.score'),0);
  assert.equal(M.value(data,'rows.0.score'),42);
  for(const path of ['constructor','__proto__','live.constructor','missing'])assert.equal(M.value(data,path),null);
  assert.equal(M.condition(data,'live.active'),false);
  assert.equal(M.condition(data,''),true);
  assert.equal(M.condition(data,'live.score >= 0'),true);
  assert.equal(M.condition(data,'missing > 0'),false);
  assert.equal(M.condition(data,'live.active == false'),true);
});
test('numeric formats bound precision and preserve unknown values',()=>{
  assert.equal(M.formatValue(12500,{abbreviate:true,decimals:1,suffix:' pts'}),'12.5k pts');
  assert.equal(M.formatValue(0,{prefix:'Score '}),'Score 0');
  assert.equal(M.formatValue(null,{}),'Unavailable');
  assert.equal(M.formatValue(Infinity,{}),'Unavailable');
});
test('template substitution never evaluates expressions or parses markup',()=>{
  assert.equal(M.template('{{live.score}} <script>x</script>',{live:{score:42}}),'42 <script>x</script>');
  assert.equal(M.template('{{constructor}}',{}),'Unavailable');
  assert.equal(M.template('{{1+1}}',{}),'{{1+1}}');
});
test('binding scenes round trip through share codes with lock and automatic context',()=>{
  const scene=M.cleanScene({id:'sample',auto:'replay',widgets:[{id:'score',type:'stat',locked:true,opts:{path:'live.score',decimals:2,condition:'live.active'}}]});
  const result=M.decodeShare(M.encodeShare(scene));
  assert.equal(result.auto,'replay');assert.equal(result.widgets[0].locked,true);
  assert.equal(result.widgets[0].opts.path,'live.score');assert.equal(result.widgets[0].opts.decimals,2);
});
test('restricted markup strips network and executable attributes and styles',()=>{
  const result=M.markup('<div onclick="bad()" style="color:#ffffff;background-image:url(https://invalid.example)"><img src="https://invalid.example"><b>{{live.score}}</b><iframe src="https://invalid.example"></iframe></div>',{live:{score:'<img src=x>'}});
  const json=JSON.stringify(result);
  assert.doesNotMatch(json,/onclick|backgroundImage|https:|iframe/);
  assert.equal(result.kids[0].style.color,'#ffffff');
  assert.equal(result.kids[0].kids[0].kids[0].text,'<img src=x>');
});
