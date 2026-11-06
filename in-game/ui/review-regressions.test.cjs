const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const {makeWindow,load,El}=require('./overlay-test-dom.cjs');
El.prototype.insertBefore=function(child,before){if(child.parentNode)child.parentNode.removeChild(child);const at=before?this.children.indexOf(before):this.children.length;child.parentNode=this;this.children.splice(at,0,child);return child;};
test('shared reconciliation retains player rows through score and order changes',()=>{
 const w=makeWindow();load(w.window,['standings.js']);const target=w.document.createElement('div');const board={kind:'combat',title:'Match',rows:[{id:'p1',name:'One',rank:1,frags:2},{id:'p2',name:'Two',rank:2,frags:1}]};
 w.window.AimModStandings.render(target,board,'full');const one=target.find(e=>e.amKey==='player:p1');board.rows.reverse();board.rows[1].frags=3;w.window.AimModStandings.render(target,board,'full');assert.equal(target.find(e=>e.amKey==='player:p1'),one);assert.match(one.textContent,/3/);
});
test('notice polls serialize and late timeout does not complete twice',()=>{
 const w=makeWindow();for(const id of ['notice','board-corner','board-full','cs-hud','cs-sights','cs-flash','chat-hud']){const el=w.document.createElement('div');el.id=id;w.document.roots.push(el);}
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'notify.js'),'utf8'),{window:w.window,Date,setTimeout:w.window.setTimeout,clearTimeout:w.window.clearTimeout});
 assert.equal(w.requests.length,1);assert.equal(w.timers.length,0);const first=w.requests[0];first.finish(200,{active:false});const count=w.timers.length;first.ontimeout();assert.equal(w.timers.length,count);w.timers.find(t=>t.delay===250).fn();assert.equal(w.requests.length,2);
});
test('scope stack and grouped scaling retain 720p and 1440p rules',()=>{
 const css=fs.readFileSync(path.join(__dirname,'cshud.css'),'utf8');assert.match(css,/#cs-sights\{[^}]*z-index:60/);assert.match(css,/#cs-hud\{z-index:70/);assert.match(css,/#cs-flash\{[^}]*z-index:100/);assert.match(css,/@media\(max-height:760px\)/);assert.match(css,/cs-top-group\{transform:scale\(1.33\)/);
});

test('pointer polling rejects a closed session and never overlaps requests',()=>{
 const w=makeWindow();for(const id of ['notice','cs-hud','chat-hud']){const el=w.document.createElement('div');el.id=id;w.document.roots.push(el);}
 let typed=0;w.window.AimModChat={render(){},fileKeys(){typed++;}};w.window.AimModCsHud={render(){}};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'notify.js'),'utf8'),{window:w.window,Date,setTimeout:w.window.setTimeout,clearTimeout:w.window.clearTimeout});
 const api=w.window.AimModNotify,on={active:false,cs:{buyOpen:true},chat:{open:'all',session:1}};
 api.render(on);w.timers.find(t=>t.delay===50).fn();const pointer=w.requests.find(r=>r.url.endsWith('multiplayer-pointer'));
 const count=w.timers.filter(t=>t.delay===50).length;api.render(on);assert.equal(w.timers.filter(t=>t.delay===50).length,count);
 api.render(null);api.render(on);pointer.finish(200,'AIMMOD_POINTER_1\ton\t0\t0\t0\t1920\t1080\nkeys\t1\nkey\t1\t65');assert.equal(typed,0);
 w.timers.filter(t=>t.delay===50).at(-1).fn();w.requests.filter(r=>r.url.endsWith('multiplayer-pointer')).at(-1).finish(200,'AIMMOD_POINTER_1\ton\t0\t0\t0\t1920\t1080\nkeys\t1\nkey\t2\t66');assert.equal(typed,1);
});
