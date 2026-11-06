const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
function load(){const c=vm.createContext({window:{}});require('./test-format.cjs').loadFormat(c);vm.runInContext(fs.readFileSync(path.join(__dirname,'mechanics.js'),'utf8'),c);return c.window.AimModImprove;}
test('home stays independent of the history page and counts distinct current bests',()=>{
 const api=load(),now=new Date(2026,5,10,12).getTime();
 const state={scenarios:[{name:'Synthetic One',best:100},{name:'Synthetic Two',best:200}],history:[{Scenario:'Wrong page',Score:999}],coachingHistory:[
 {id:'first',normalizedScenario:'synthetic one',score:100,timestampMs:now-10000},
 {id:'tie',normalizedScenario:'synthetic one',score:100,timestampMs:now-20000},
 {id:'lower',normalizedScenario:'synthetic two',score:150,timestampMs:now-30000}],statistics:{Periods:[{Key:'all',Calendar:[{Date:'2026-06-08',Runs:1,Minutes:5},{Date:'2026-06-09',Runs:2,Minutes:10},{Date:'2026-06-10',Runs:1,Minutes:3}]},{Key:'7',Days:7,Runs:4,Hours:.3}]}};
 const s=api.summary(state,now);assert.equal(s.minutes,3);assert.equal(s.streak,3);assert.equal(s.weekPbs,1);assert.equal(s.recent[0].Scenario,'Synthetic One');assert.equal(s.recent[0].Id,'first');assert.equal(s.pbs.length,2);
 assert.equal(api.isBest({Scenario:'synthetic two',Score:150},state),false);assert.equal(api.isBest({Scenario:'unknown',Score:0},state),false);
});
test('streak includes yesterday before first practice today and breaks on a missed day',()=>{
 const api=load(),state={statistics:{Periods:[{Key:'all',Calendar:[{Date:'2026-06-09',Runs:1,Minutes:0}]}]}};
 assert.equal(api.summary(state,new Date(2026,5,10,12).getTime()).streak,1);
 assert.equal(api.summary(state,new Date(2026,5,11,12).getTime()).streak,0);
 assert.equal(api.summary({},new Date(2026,5,11,12).getTime()).weekPbs,0);
});
test('history sorting preserves zero, missing values and the original array',()=>{
 const api=load(),runs=[{Id:'missing',Score:null},{Id:'zero',Score:0},{Id:'high',Score:10}];
 assert.deepEqual(Array.from(api.sorted(runs,'Score',false),r=>r.Id),['zero','high','missing']);
 assert.deepEqual(Array.from(api.sorted(runs,'Score',true),r=>r.Id),['high','zero','missing']);assert.equal(runs[0].Id,'missing');
});
test('all inline page scripts parse after integrating page renderers',()=>{
 const source=fs.readFileSync(path.join(__dirname,'index.html'),'utf8');
 for(const match of source.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g))new vm.Script(match[1]);
});
