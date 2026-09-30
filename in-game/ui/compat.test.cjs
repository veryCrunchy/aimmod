const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const source=fs.readFileSync(require('node:path').join(__dirname,'index.html'),'utf8');
const context=vm.createContext({});
vm.runInContext("Number.prototype.toLocaleString=function(){throw Error('Intl unavailable')};Date.prototype.toLocaleDateString=function(){throw Error('Intl unavailable')};",context);
vm.runInContext(source.slice(source.indexOf('function number('),source.indexOf('function escape(')),context);
test('Gameface formatting works without Intl options',()=>{
  assert.equal(context.number(98.123456789),'98.1');
  assert.equal(context.percent(83.876543210),'83.9%');
  assert.equal(context.number(987.42600000000675,0),'987');
  assert.equal(context.number(12345),'12,345');
  assert.equal(context.number(-1234.567),'-1,234.6');
});
test('unknown metrics stay unknown and legitimate zero stays zero',()=>{
  for(const value of [null,undefined,NaN,Infinity])assert.equal(context.number(value),'—');
  assert.equal(context.number(0),'0');assert.equal(context.percent(0),'0%');
});
test('legacy local timestamps have predictable compact dates',()=>{
  assert.equal(context.date('2026.06.03-12.30.00'),'3 Jun 2026');
  assert.equal(context.date('invalid'),'invalid');
});
