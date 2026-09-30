// Test helper: installs the workspace's shared AimModFormat (defined inline in
// index.html) into a vm context, with Intl formatting disabled like Gameface.
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const html=fs.readFileSync(path.join(__dirname,'index.html'),'utf8');
const source=html.slice(html.indexOf('/*format:start*/'),html.indexOf('/*format:end*/'));
function loadFormat(context,target){
  vm.runInContext("Number.prototype.toLocaleString=function(){throw Error('Intl unavailable')};Date.prototype.toLocaleDateString=function(){throw Error('Intl unavailable')};Date.prototype.toLocaleString=function(){throw Error('Intl unavailable')};",context);
  const scope=target||vm.runInContext('typeof window==="undefined"?this:window',context);
  vm.runInContext('(function(window){'+source+'})',context)(scope);
  return scope.AimModFormat;
}
module.exports={loadFormat,source};
