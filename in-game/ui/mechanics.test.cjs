const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
test('mechanics uses selected period measurements and summary coverage does not claim no movement data',()=>{
 class El{constructor(t){this.tag=t;this.children=[];this.textContent='';}appendChild(c){this.children.push(c);return c;}}
 const measured=[],window={document:{createElement:t=>new El(t)},AimModStatistics:{renderMeasurements:(el,data)=>measured.push(data)}};
 vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname,'mechanics.js'),'utf8'),{window});
 const root=new El('div'),all=[{Key:'AverageSpeed',Samples:4,Average:800}],recent=[{Key:'KillsPerSecond',Samples:1,Average:.5}];let histories=0;
 window.AimModMechanics.render(root,{statistics:{Periods:[{Key:'all',Selected:{Measurements:all}},{Key:'7',Selected:{Measurements:recent}}]},mechanics:[]},()=>{},()=>histories++);
 assert.equal(measured[0],all);function flat(e){return [e,...e.children.flatMap(flat)];}
 assert.ok(!flat(root).some(e=>/No movement samples/.test(e.textContent)));
 flat(root).find(e=>e.textContent==='Browse scenario history').onclick();assert.equal(histories,1);
 flat(root).find(e=>e.textContent==='7 days').onclick();assert.equal(measured[1],recent);
});
