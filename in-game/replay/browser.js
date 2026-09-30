/* Replay library for the owned Gameface workspace. No gameplay commands. */
(function (root) {
  'use strict';
  var container = null, request = null, generation = 0, rows = [], active = false, nativeOpen=false, favoritesOnly=false, query='', message='', busy=false, total=0, pendingStart=null;
  function node(tag, css, text) {
    var el = root.document.createElement(tag);
    if (css) el.className = css;
    if (text !== undefined) el.textContent = text;
    return el;
  }
  function clear() {
    if(nativeOpen&&root.AimModNativeReplayBrowser){root.AimModNativeReplayBrowser.leave();nativeOpen=false;}
    while (container && container.firstChild) container.removeChild(container.firstChild);
  }
  function cancel() {
    generation++;
    if (request) { var old = request; request = null; old.abort(); }
  }
  function prefix() {
    var pathname = root.location.pathname;
    return pathname.slice(0, pathname.lastIndexOf('/'));
  }
  function get(path, callback, body) {
    cancel();
    var ticket = generation, xhr = new root.XMLHttpRequest(); request = xhr;
    xhr.open(body ? 'POST' : 'GET', prefix() + '/' + path, true); xhr.timeout = 20000;
    if(body){xhr.setRequestHeader('X-AimMod-UI','1');xhr.setRequestHeader('Content-Type','application/json');}
    var done = false;
    function finish(ok, value) {
      if (done) return; done = true;
      if (ticket !== generation || !active) return;
      request = null; callback(ok, value);
    }
    xhr.onreadystatechange = function () {
      if (xhr.readyState !== 4) return;
      if (xhr.status !== 200) { finish(false, xhr.status); return; }
      try { finish(true, JSON.parse(xhr.responseText)); } catch (e) { finish(false, 0); }
    };
    xhr.onerror = xhr.ontimeout = function () { finish(false, 0); };
    xhr.send(body ? JSON.stringify(body) : null);
  }
  function known(v) { return typeof v === 'number' && isFinite(v); }
  function fmt(v) { return root.AimModFormat ? root.AimModFormat.number(v, 1) : String(Math.round(v * 10) / 10); }
  function button(label, callback, primary) {
    var el = node('button', 'button' + (primary ? ' primary' : ''), label);
    el.type = 'button'; el.onclick = function(){if(!busy)callback();}; return el;
  }
  function status(title, message, action, label) {
    clear(); var card = node('div', 'empty');
    card.appendChild(node('h3', '', title)); card.appendChild(node('p', '', message));
    if (action) { var row = node('div', 'actions center'); row.appendChild(button(label || 'Try again', action, true)); card.appendChild(row); }
    container.appendChild(card);
  }
  function date(value) {
    // Do not depend on Intl/date formatting options in the bundled renderer.
    if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(value)) return '';
    if (root.AimModFormat) { var parsed = root.AimModFormat.parse(value); if (parsed) return root.AimModFormat.dateTime(parsed); }
    return value.slice(0, 10) + ' · ' + value.slice(11, 16) + ' UTC';
  }
  var searchTimer = null, LIMIT = 250;
  function list() {
    if(busy)return;
    cancel(); clear();
    container.style.cssText='';container.className='replay-library';
    var toolbar = node('div', 'replay-toolbar');
    toolbar.appendChild(node('h2', '', (total > rows.length ? rows.length + ' of ' + total : rows.length) + (rows.length === 1 ? ' in-game replay' : ' in-game replays')));
    container.appendChild(toolbar);
    if(message)container.appendChild(node('p','notice replay-message',message));
    var waitingRow=pendingStart&&rows.filter(function(r){return r.id===pendingStart.pending;})[0];
    if(waitingRow){var banner=node('div','replay-waiting');var text=node('div','replay-waiting-text');text.appendChild(node('strong','','Waiting to start: '+(waitingRow.scenario||'replay')));text.appendChild(node('span','',pendingStart.message||'Load the scenario, then open the pause menu (Esc).'));banner.appendChild(text);banner.appendChild(button('Show',function(){pendingStart=null;open(waitingRow);},true));container.appendChild(banner);}
    if (!rows.length) {
      toolbar.appendChild(button('Refresh', refresh));
      var empty = node('div', 'empty'); empty.appendChild(node('h3', '', 'No replays saved yet'));
      empty.appendChild(node('p', '', 'Play a run with replay recording on and it will appear here. Older score history may not include a replay.'));
      container.appendChild(empty); return;
    }
    var search=node('input','replay-search');search.type='search';search.value=query;search.setAttribute&&search.setAttribute('aria-label','Find a replay by scenario');
    toolbar.appendChild(root.AimModFormat&&root.AimModFormat.field?root.AimModFormat.field(search,'Find a scenario','replay-search-field'):search);
    // A segmented toggle makes the favorites filter state visible.
    var scope=node('div','segmented replay-scope'),everything=button('All replays',function(){setFavorites(false);},!favoritesOnly),favorite=button('Favorites',function(){setFavorites(true);},favoritesOnly);
    function setFavorites(value){favoritesOnly=value;everything.className='button'+(favoritesOnly?'':' primary');favorite.className='button'+(favoritesOnly?' primary':'');everything.setAttribute&&everything.setAttribute('aria-pressed',String(!favoritesOnly));favorite.setAttribute&&favorite.setAttribute('aria-pressed',String(favoritesOnly));draw();}
    scope.appendChild(everything);scope.appendChild(favorite);toolbar.appendChild(scope);toolbar.appendChild(button('Refresh', refresh));
    var panel = node('div', 'replay-cards');container.appendChild(panel);
    // Search redraws only the cards, so the field keeps focus while typing.
    search.oninput=function(){if(searchTimer)root.clearTimeout(searchTimer);searchTimer=root.setTimeout(function(){searchTimer=null;if(!busy&&query!==search.value){query=search.value;draw();}},150);};
    search.onchange=function(){if(busy)return;if(searchTimer){root.clearTimeout(searchTimer);searchTimer=null;}query=search.value;draw();};
    function draw(){
    while(panel.firstChild)panel.removeChild(panel.firstChild);
    var shown=rows.filter(function(row){return (!favoritesOnly||row.favorite)&&(!query||(row.scenario||'').toLowerCase().indexOf(query.toLowerCase())>=0);});
    if(!shown.length){var none=node('div','empty');none.style.width='100%';none.appendChild(node('p','',favoritesOnly&&!query?'No favorite replays yet. Mark a replay as a favorite to keep it here.':'No replays match your filters.'));none.appendChild(button('Clear filters',function(){query='';favoritesOnly=false;list();}));panel.appendChild(none);}
    shown.forEach(function (row) {
      var card=node('div','replay-card'),group=node('div','replay-card-inner');card.appendChild(group);
      // One primary action: the whole card plays the replay in game.
      var item = node('button', 'replay-main', ''); item.type = 'button';
      var info = node('div', 'replay-info');
      var titleRow = node('div', 'replay-title-row'); if (row.favorite) titleRow.appendChild(node('span', 'replay-fav', 'Favorite')); titleRow.appendChild(node('div', 'replay-title', row.scenario || 'Untitled scenario'));
      var sub = node('div', 'replay-meta', date(row.recordedAt) || 'Date unknown');
      if (row.reason !== 'completed') sub.appendChild(node('span', 'replay-badge', 'Partial run'));
      info.appendChild(titleRow); info.appendChild(sub);
      // Score, length and accuracy appear when the library provides them.
      var facts = [['Score', known(row.score) ? fmt(row.score) : null], ['Accuracy', known(row.accuracy) ? fmt(row.accuracy) + '%' : null], ['Length', known(row.duration) ? Math.round(row.duration) + 's' : null]].filter(function (f) { return f[1] !== null; });
      if (facts.length) { var stats = node('div', 'replay-stats'); facts.forEach(function (f) { var cell = node('div', 'replay-stat'); cell.appendChild(node('span', '', f[0])); cell.appendChild(node('strong', '', f[1])); stats.appendChild(cell); }); info.appendChild(stats); }
      item.appendChild(info);
      item.appendChild(node('span', 'button primary', 'Play replay'));
      item.onclick = function () { open(row); }; group.appendChild(item);
      var actions=node('div','replay-actions');
      function act(action, favorite){if(busy)return;busy=true;disableControls(container);message='';get('replay-library',function(ok){busy=false;message=ok?(action==='export'?'Saved to Documents / AimMod / Replays.':''):'Could not update this replay. Please try again.';if(ok&&action==='delete'){rows=rows.filter(function(r){return r.id!==row.id;});total=Math.max(rows.length,total-1);}if(ok&&action==='favorite')row.favorite=favorite;list();},{action:action,id:row.id,favorite:favorite});}
      function quiet(label,fn){var b=button(label,fn);b.className='button quiet';return b;}
      actions.appendChild(quiet(row.favorite?'Remove favorite':'Favorite',function(){act('favorite',!row.favorite);}));
      actions.appendChild(node('span','spacer'));
      actions.appendChild(quiet('Export',function(){act('export');}));
      var del=quiet('Delete',function(){
        while(actions.firstChild)actions.removeChild(actions.firstChild);actions.className='replay-actions confirm';
        actions.appendChild(node('span','subtle','Delete this replay permanently? Your score history is kept.'));
        actions.appendChild(button('Keep replay',list));var remove=button('Delete replay',function(){act('delete');});remove.className='button danger';actions.appendChild(remove);
      });del.className='button quiet delete';actions.appendChild(del);group.appendChild(actions);panel.appendChild(card);
    });
    }
    draw();
  }
  function disableControls(element){
    if(element.tagName==='BUTTON'||element.tagName==='INPUT')element.disabled=true;
    for(var i=0;i<element.children.length;i++)disableControls(element.children[i]);
  }
  function open(row) {
    if (busy || !active || !/^[A-Za-z0-9_-]{1,100}$/.test(row.id)) return;
    cancel();clear();
    if(!root.AimModNativeReplayBrowser){status('Replay unavailable','The in-game replay view could not be loaded.',list,'Back to replays');return;}
    var bar=node('div','toolbar');bar.appendChild(button('Back to replays',list));container.appendChild(bar);
    var nativeTarget=node('div','native-replay');container.appendChild(nativeTarget);
    nativeOpen=true;root.AimModNativeReplayBrowser.enter(nativeTarget,row);
  }
  function refresh() {
    if (busy || !active) return;
    status('Loading replays', '');
    get('replays', function (ok, data) {
      if (!ok || !Array.isArray(data)) { status('Could not load replays', 'Please try again in a moment.', refresh); return; }
      var valid = data.filter(function (row) { return row && typeof row.id === 'string' && /^[A-Za-z0-9_-]{1,100}$/.test(row.id); });
      total = valid.length; rows = valid.slice(0, LIMIT);
      list();
      // A start may still be waiting from before the workspace was hidden.
      var native = root.AimModNativeReplayBrowser, ticket = generation;
      if (native && native.pendingStart) native.pendingStart(function (start) { if (!active || busy || ticket !== generation) return; var had = !!pendingStart; pendingStart = start; if (start || had) list(); });
    });
  }
  root.AimModReplayBrowser = {
    enter: function (element) {
      container = element || root.document.getElementById('replay-content');
      if (!container) return;
      busy=false; active = true; refresh();
    },
    leave: function () { active = false; busy=false; cancel(); if(nativeOpen&&root.AimModNativeReplayBrowser){root.AimModNativeReplayBrowser.leave();nativeOpen=false;} },
    refresh: refresh
  };
})(window);
