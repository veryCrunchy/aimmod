const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
test('CS standings render damage, headshot percentage and MVPs including zero',()=>{
  class El {
    constructor(){this.children=[];}
    appendChild(c){this.children.push(c);return c;}
    removeChild(c){this.children.splice(this.children.indexOf(c),1);}
    get firstChild(){return this.children[0];}
  }
  const window={document:{createElement:()=>new El()}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'standings.js'),'utf8'),{window});
  const root=new El();
  window.AimModStandings.render(root,{kind:'cs',title:'Match',teams:[{team:1,name:'Terrorists',total:1}],rows:[
    {team:1,name:'Player',frags:2,deaths:1,kd:2,money:800,adr:75.5,hsPercent:50,mvps:0}
  ]},'full');
  function text(el){return [el.textContent||'',...el.children.map(text)].join('|');}
  for(const value of ['ADR','HS%','MVP','75.5','50.0%','0'])assert.ok(text(root).includes(value));
});
