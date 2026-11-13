const {test}=require('node:test'),assert=require('node:assert/strict');
const {makeWindow,load}=require('./overlay-test-dom.cjs');
test('searchable choices support keyboard movement, selection and Escape',()=>{
 const w=makeWindow();load(w.window,[]);let chosen;
 const box=w.window.AimModFormat.dropdown('Scenario',Array.from({length:20},(_,i)=>({value:String(i),label:'Practice '+i})),'0',v=>chosen=v);
 const trigger=box.children[0],panel=box.children[1];assert.equal(panel.style.display,'none');trigger.onclick();
 const input=box.find(n=>n.tagName==='INPUT');input.value='Practice 19';input.oninput();
 const options=box.findAll(n=>n.getAttribute('role')==='option');assert.equal(options.length,1);
 input.onkeydown({keyCode:40,preventDefault(){}});assert.equal(w.document.activeElement,options[0]);options[0].onclick();
 assert.equal(chosen,'19');assert.equal(trigger.textContent,'Practice 19');assert.equal(w.document.activeElement,trigger);
 trigger.onkeydown({keyCode:40,preventDefault(){}});box.onkeydown({keyCode:27,preventDefault(){},stopPropagation(){}});assert.equal(panel.style.display,'none');
 box.setDisabled(true);trigger.onclick();assert.equal(panel.style.display,'none');
});
