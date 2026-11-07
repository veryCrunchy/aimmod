const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const source=fs.readFileSync(__dirname+'/index.html','utf8');
test('unscored runs cannot acquire a PB label while penalty scores remain numeric',()=>{
 const code=source.slice(source.indexOf('function scored('),source.indexOf('function recentRuns('));
 const ctx={F:{known:n=>typeof n==='number'&&Number.isFinite(n)},number:String,AimModImprove:{isBest:()=>true}};
 vm.createContext(ctx);vm.runInContext(code,ctx);
 for(const Score of [0,null,undefined,NaN])assert.equal(ctx.runScore({Score,Duration:60},{}),'No score');
 assert.equal(ctx.runScore({Score:123,Duration:0},{}),'No score');
 assert.equal(ctx.runScore({Score:123,Duration:60},{}),'123 PB');
 assert.equal(ctx.runScore({Score:-5,Duration:60},{}),'-5 PB');
});
test('scheduled shell work stops when hidden and releases completed handles',()=>{
 const code=source.slice(source.indexOf('function defer('),source.indexOf("var state=null,active="));
 let callback,calls=0;const ctx={visible:true,deferred:[],window:{setTimeout:fn=>{callback=fn;return 7}}};
 vm.createContext(ctx);vm.runInContext(code,ctx);ctx.defer(()=>calls++,20);assert.equal(ctx.deferred.length,1);
 ctx.visible=false;callback();assert.equal(calls,0);assert.equal(ctx.deferred.length,0);assert.equal(ctx.defer(()=>calls++,20),null);
 ctx.visible=true;ctx.defer(()=>calls++,20);callback();assert.equal(calls,1);assert.equal(ctx.deferred.length,0);
});
