const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),{loadFormat}=require('./test-format.cjs');
class El{constructor(t){this.tag=t;this.children=[];this.textContent='';}appendChild(c){this.children.push(c);return c;}setAttribute(){}}
function setup(){const measured=[],window={document:{createElement:t=>new El(t)},AimModStatistics:{renderMeasurements:(el,data)=>measured.push(data)}};
 const context=vm.createContext({window,Date});loadFormat(context);vm.runInContext(fs.readFileSync(require('node:path').join(__dirname,'mechanics.js'),'utf8'),context);return {window,measured};}
function flat(e){return [e,...e.children.flatMap(flat)];}
test('mechanics uses selected period measurements and summary coverage does not claim no movement data',()=>{
 const {window,measured}=setup();
 const root=new El('div'),all=[{Key:'AverageSpeed',Samples:4,Average:800}],recent=[{Key:'KillsPerSecond',Samples:1,Average:.5}];let histories=0;
 window.AimModMechanics.render(root,{statistics:{Periods:[{Key:'all',Selected:{Measurements:all}},{Key:'7',Selected:{Measurements:recent}}]},mechanics:[]},()=>{},()=>histories++);
 assert.equal(measured[0],all);
 assert.ok(!flat(root).some(e=>/No movement samples/.test(e.textContent)));
 flat(root).find(e=>e.textContent==='Browse scenario history').onclick();assert.equal(histories,1);
 flat(root).find(e=>e.textContent==='7 days').onclick();assert.equal(measured[1],recent);
});
test('recorded runs show readable dates and never print NaN for missing control',()=>{
 const {window}=setup();const root=new El('div');let opened;
 window.AimModMechanics.render(root,{statistics:{Periods:[]},mechanics:[{Id:'a',Timestamp:'2026.06.03-09.05.00',Smoothness:null,Score:1234.5},{Id:'b',Timestamp:null,Smoothness:71.26}]},r=>opened=r,()=>{});
 const text=flat(root).map(e=>e.textContent);
 assert.ok(text.includes('3 Jun 2026, 09:05'));assert.ok(text.includes('Control —'));assert.ok(text.includes('Control 71.3'));assert.ok(text.includes('1,234.5 score · Open →'));assert.ok(!text.some(t=>/NaN|undefined/.test(t)));
 flat(root).filter(e=>e.tag==='button'&&e.className==='mechanics-run')[0].onclick();assert.equal(opened.Id,'a');
});
