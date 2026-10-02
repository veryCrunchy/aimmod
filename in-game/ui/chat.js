// In-match chat on the notice layer (MultiplayerService.Chat.cs): the feed bottom left (above the
// money in CS), the input while it is open, and the CS-style radio menu. Each line reads
// "*DEAD* [team] Name @ A Site: text" with the name in its team's colour; lines fade 10 s after
// they arrive and all come back while the input is open.
// Typing: while the input is open AimModCore holds input for this layer and relays every character
// Windows makes of the keys (AimModKey(code, id, session), and the same in overlay-pointer.tsv for
// the polled fallback); Gameface's own key events count too when they arrive. Each character is
// taken once (by its id), from one source per input. Enter sends the line, Escape cancels,
// Backspace deletes; the service closes the input by itself too (Enter, Escape, alt-tab).
// Gameface: DOM only, solid colours.
(function(root){
  'use strict';
  var FADE_MS=10000,FADE_OUT_MS=1000,MAX_TEXT=200,EARLY_MS=1500;
  function node(tag,css,text){var el=root.document.createElement(tag);if(css)el.className=css;if(text!==undefined&&text!==null)el.textContent=String(text);return el;}
  function clear(el){while(el.firstChild)el.removeChild(el.firstChild);}
  function now(){return Date.now();}
  // One chat for the page: what the service says and what is being typed.
  var s={el:null,send:null,view:null,at:0,open:null,session:0,draft:'',source:null,lastId:0,done:{},early:[],sent:{},draftNode:null,core:null,doneCore:{}};
  function done(session){s.done[session]=true;}
  // The input the service has open, once the page knows it (and hasn't ended it itself).
  function opened(){return s.open&&!s.done[s.session];}
  function finish(kind){
    if(!opened())return;var session=s.session,text=s.draft.replace(/^\s+|\s+$/g,'');done(session);endCore();
    if(kind==='send'){s.sent[session]=true;if(s.send)s.send({action:'chat-send',session:session,text:text});}
    else if(s.send)s.send({action:'chat-close',session:session});
    s.open=null;s.draft='';paint();
  }
  // One UTF-16 unit from a source: 'core' (AimModCore's relay or its file) or 'native' (Gameface's keys).
  function unit(code,source){
    if(!opened())return false;
    if(s.source&&s.source!==source)return false;
    s.source=source;
    if(code===13){finish('send');return true;}
    if(code===27){finish('cancel');return true;}
    if(code===8){
      var t=s.draft;if(!t)return true;var last=t.charCodeAt(t.length-1);
      s.draft=t.slice(0,last>=0xDC00&&last<=0xDFFF&&t.length>1?-2:-1);draftPaint();return true;
    }
    if(code<32||code===127)return true;
    if(s.draft.length>=MAX_TEXT)return true;
    s.draft+=String.fromCharCode(code);draftPaint();return true;
  }
  // AimModCore's characters by id (relay and file carry the same ids: each is taken once), with
  // AimModCore's own input session: once an input ends here, the rest of its session is dropped
  // (keys still typed before AimModCore let go never reach the next input).
  function endCore(){if(s.core!==null)s.doneCore[s.core]=true;s.core=null;}
  function coreKey(code,id,session){
    code=+code;id=+id;session=+session||0;if(!(id>0)||!(code>=0))return;
    if(id<=s.lastId)return;s.lastId=id;
    if(s.doneCore[session])return;
    // Typed before this page saw the input open: kept a moment for it.
    if(!opened()){s.early.push({code:code,session:session,at:now()});if(s.early.length>64)s.early.shift();return;}
    if(s.source&&s.source!=='core')return;
    s.core=session;unit(code,'core');
  }
  function fileKeys(list,session){for(var i=0;i<list.length;i++)coreKey(list[i].code,list[i].id,session);}
  // Gameface's own keyboard events (when its view gets them): Enter, Escape and Backspace by key, text by keypress.
  function nativeDown(e){
    if(!opened())return;var k=e&&(e.keyCode||e.which);
    if(k===13||k===27||k===8){if(unit(k,'native')&&e.preventDefault)e.preventDefault();}
  }
  function nativePress(e){
    if(!opened())return;var c=e&&(e.charCode||e.which);
    if(c>=32&&c!==127){unit(c,'native');if(e.preventDefault)e.preventDefault();}
  }
  function scopeLabel(scope){return scope==='team'?'[team]':scope==='system'?'':'[all]';}
  function line(l,age,open){
    var el=node('div','chat-line'+(l.scope==='system'?' system':'')+(l.you?' you':'')+(l.radio?' radio':''));
    if(l.scope==='system'){el.appendChild(node('span','chat-text',l.text));}
    else{
      if(l.dead)el.appendChild(node('span','chat-dead','*DEAD*'));
      el.appendChild(node('span','chat-scope '+(l.scope==='team'?'team':'all'),scopeLabel(l.scope)));
      el.appendChild(node('span','chat-name'+(l.team===1||l.team===2?' team'+l.team:''),l.name));
      if(l.place)el.appendChild(node('span','chat-place','@ '+l.place));
      el.appendChild(node('span','chat-colon',':'));
      el.appendChild(node('span','chat-text',l.text));
    }
    // Fades out over the last second of its 10 s; all lines show while typing.
    var o=open?1:age>=FADE_MS?0:age>FADE_MS-FADE_OUT_MS?(FADE_MS-age)/FADE_OUT_MS:1;
    el.style.opacity=String(Math.round(o*100)/100);if(o<=0)el.style.display='none';
    return el;
  }
  function draftPaint(){if(s.draftNode)s.draftNode.textContent=s.draft;}
  // Three parts: the radio menu and the input are rebuilt only when what they show changes (a click
  // is never lost to a redraw between press and release, the draft is typed in place); the feed
  // redraws every time for its fades.
  function parts(el){
    if(el.chatParts&&el.chatParts.root===el)return el.chatParts;clear(el);
    var p={root:el,radio:node('div','chat-radio-slot'),feed:node('div','chat-feed'),input:node('div','chat-input-slot'),radioKey:null,inputKey:null};
    el.appendChild(p.radio);el.appendChild(p.feed);el.appendChild(p.input);el.chatParts=p;return p;
  }
  function paint(){
    var el=s.el,c=s.view;if(!el)return;var p=parts(el);
    var typing=!!(c&&opened());
    if(!c){el.className='';clear(p.feed);clear(p.radio);clear(p.input);p.radioKey=p.inputKey=null;s.draftNode=null;return;}
    el.className='show'+(c.cs?' cs':'')+(typing?' typing':'')+(c.radio?' radio-open':'');
    clear(p.feed);var since=now()-s.at,shown=0;
    (c.lines||[]).forEach(function(l){var row=line(l,(l.age||0)+since,typing);if(row.style.display!=='none')shown++;p.feed.appendChild(row);});
    var rk=c.radio?JSON.stringify(c.radio):null;
    if(rk!==p.radioKey){
      p.radioKey=rk;clear(p.radio);
      if(c.radio){
        var radio=c.radio,menu=node('div','chat-radio');menu.setAttribute('role','menu');
        menu.appendChild(node('div','chat-radio-head','Radio · '+radio.title+' ('+radio.key+')'));
        (radio.items||[]).forEach(function(item,i){var b=node('button','chat-radio-item');b.type='button';b.appendChild(node('span','chat-radio-key',String(i+1)));b.appendChild(node('span','',item));
          b.onclick=function(){if(s.send)s.send({action:'chat-radio',group:radio.group,item:i});};menu.appendChild(b);});
        menu.appendChild(node('div','chat-radio-foot','Number keys pick · Esc closes'));
        p.radio.appendChild(menu);
      }
    }
    var clashes=typing||!shown?(c.clashes||[]):[];
    var ik=JSON.stringify([typing?s.session:0,typing?s.open:null,c.teams,c.allKey,c.teamKey,clashes]);
    if(ik!==p.inputKey){
      p.inputKey=ik;clear(p.input);s.draftNode=null;
      if(typing){
        var box=node('div','chat-input '+(s.open==='team'?'team':'all'));
        box.appendChild(node('span','chat-input-label',s.open==='team'?'Say (team):':'Say (all):'));
        s.draftNode=box.appendChild(node('span','chat-draft',s.draft));
        box.appendChild(node('span','chat-caret','|'));
        p.input.appendChild(box);
        p.input.appendChild(node('div','chat-hint','Enter sends · Esc cancels'+(c.teams?' · '+c.allKey+' all chat · '+c.teamKey+' team chat':'')));
      }
      clashes.forEach(function(t){p.input.appendChild(node('div','chat-clash',t));});
    }
    draftPaint();
  }
  // The service's chat for this player (null outside a match), and how to post to it.
  function render(el,c,send){
    s.el=el;s.send=send||s.send;var was=opened(),wasSession=s.session,draft=s.draft;
    s.view=c||null;s.at=now();
    if(c&&c.open&&!s.done[c.session]&&c.session!==s.session){
      // A new input: start empty, then what was typed just before the page saw it open.
      s.session=c.session;s.open=c.open;s.draft='';s.source=null;
      var early=s.early;s.early=[];
      for(var i=0;i<early.length&&opened();i++)if(now()-early[i].at<=EARLY_MS&&!s.doneCore[early[i].session]){s.core=early[i].session;unit(early[i].code,'core');}
    }else if(c&&c.open&&c.session===s.session&&!s.done[c.session])s.open=c.open;
    else if(!c||!c.open){
      // The service closed it first (it saw Enter before the key reached the page): the line still goes.
      if(was&&c&&c.closed==='enter'&&c.session===wasSession&&!s.sent[wasSession]&&draft.replace(/\s+/g,'')){s.sent[wasSession]=true;done(wasSession);if(s.send)s.send({action:'chat-send',session:wasSession,text:draft});}
      else if(was)done(wasSession);
      if(was)endCore();
      s.open=null;s.draft='';
    }
    paint();
  }
  if(root.document&&root.document.addEventListener){root.document.addEventListener('keydown',nativeDown,true);root.document.addEventListener('keypress',nativePress,true);}
  function key(code,id,session){coreKey(code,id,session);}
  if(root.AimModListen)root.AimModListen('AimModKey',key);else if(root.engine&&root.engine.on)root.engine.on('AimModKey',key);
  root.AimModChat={render:render,key:key,fileKeys:fileKeys,nativeDown:nativeDown,nativePress:nativePress,typing:opened,state:function(){return {open:s.open,session:s.session,draft:s.draft,source:s.source};},
    // Tests: forget everything.
    reset:function(){s={el:null,send:null,view:null,at:0,open:null,session:0,draft:'',source:null,lastId:0,done:{},early:[],sent:{},draftNode:null,core:null,doneCore:{}};}};
})(window);
