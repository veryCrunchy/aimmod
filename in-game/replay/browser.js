/* Replay library for the owned Gameface workspace. No gameplay commands. */
(function (root) {
  'use strict';
  var container = null, request = null, generation = 0, rows = [], active = false, nativeOpen=false, favoritesOnly=false, query='', message='', busy=false, total=0;
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
  function button(label, callback, primary) {
    var el = node('button', 'button' + (primary ? ' primary' : ''), label);
    el.type = 'button'; el.onclick = function(){if(!busy)callback();}; return el;
  }
  function status(title, message, action, label) {
    clear(); var card = node('div', 'empty');
    card.appendChild(node('h3', '', title)); card.appendChild(node('p', '', message));
    if (action) card.appendChild(button(label || 'Try again', action, true));
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
    container.style.cssText='padding:24px;max-width:1100px;border:1px solid #254237;background:#091c14;border-radius:14px';
    var toolbar = node('div', 'toolbar');
    toolbar.appendChild(node('h2', '', (total > rows.length ? rows.length + ' of ' + total : rows.length) + (rows.length === 1 ? ' in-game replay' : ' in-game replays')));
    toolbar.appendChild(button('Refresh', refresh)); container.appendChild(toolbar);
    if(message)container.appendChild(node('p','subtle',message));
    if (!rows.length) {
      var empty = node('div', 'empty'); empty.appendChild(node('h3', '', 'No replays saved yet'));
      empty.appendChild(node('p', '', 'Play a run with replay recording on and it will appear here. Older score history may not include a replay.'));
      container.appendChild(empty); return;
    }
    var filters=node('div','toolbar');
    var search=node('input','replay-search');search.type='search';search.placeholder='Find a scenario';search.value=query;search.setAttribute&&search.setAttribute('aria-label','Find a replay by scenario');
    filters.appendChild(search);
    var favorite=button(favoritesOnly?'Show all replays':'Favorites',function(){favoritesOnly=!favoritesOnly;favorite.textContent=favoritesOnly?'Show all replays':'Favorites';favorite.className='button'+(favoritesOnly?' primary':'');draw();},favoritesOnly);
    filters.appendChild(favorite);container.appendChild(filters);
    var panel = node('div', '');container.appendChild(panel);
    // Search redraws only the rows, so the field keeps focus while typing.
    search.oninput=function(){if(searchTimer)root.clearTimeout(searchTimer);searchTimer=root.setTimeout(function(){searchTimer=null;if(!busy&&query!==search.value){query=search.value;draw();}},150);};
    search.onchange=function(){if(busy)return;if(searchTimer){root.clearTimeout(searchTimer);searchTimer=null;}query=search.value;draw();};
    function draw(){
    while(panel.firstChild)panel.removeChild(panel.firstChild);
    var shown=rows.filter(function(row){return (!favoritesOnly||row.favorite)&&(!query||(row.scenario||'').toLowerCase().indexOf(query.toLowerCase())>=0);});
    if(!shown.length){var none=node('div','empty');none.appendChild(node('p','',favoritesOnly&&!query?'No favorite replays yet. Mark a replay as a favorite to keep it here.':'No replays match your filters.'));none.appendChild(button('Clear filters',function(){query='';favoritesOnly=false;list();}));panel.appendChild(none);}
    shown.forEach(function (row) {
      var group=node('div','replay-library-row');
      var item = node('button', '', ''); item.type = 'button';
      item.style.cssText = 'display:flex;align-items:center;text-align:left;width:100%;padding:24px;margin-top:12px;background:#10291f;border:1px solid #2b4a3b;border-radius:10px';
      var info = node('div', ''); info.style.cssText = 'flex:1;min-width:0';
      var title = node('div', '', row.scenario || 'Untitled scenario');
      title.style.cssText = 'font-size:18px;font-weight:600;color:#e1f4e9;overflow:hidden;text-overflow:ellipsis;white-space:nowrap';
      var details = date(row.recordedAt);

      if (row.reason !== 'completed') details += (details ? ' · ' : '') + 'Partial run';
      var sub = node('div', 'subtle', details); sub.style.cssText = 'font-size:12px;margin-top:6px';
      info.appendChild(title); info.appendChild(sub); item.appendChild(info);
      var play = node('span', '', 'Play replay'); play.style.cssText = 'font-size:14px;font-weight:600;margin-left:24px;padding:12px 20px;background:#16dca1;color:#04251b;border-radius:7px'; item.appendChild(play);
      item.onclick = function () { open(row); }; group.appendChild(item);
      var actions=node('div','replay-library-actions');actions.style.cssText='display:flex;padding:10px 4px;align-items:center';
      function act(action, favorite){if(busy)return;busy=true;disableControls(container);message='';get('replay-library',function(ok){busy=false;message=ok?(action==='export'?'Saved to Documents / AimMod / Replays.':''):'Could not update this replay. Please try again.';if(ok&&action==='delete'){rows=rows.filter(function(r){return r.id!==row.id;});total=Math.max(rows.length,total-1);}if(ok&&action==='favorite')row.favorite=favorite;list();},{action:action,id:row.id,favorite:favorite});}
      actions.appendChild(button(row.favorite?'Remove favorite':'Favorite',function(){act('favorite',!row.favorite);}));
      actions.appendChild(button('Export',function(){act('export');}));
      actions.appendChild(button('Delete',function(){
        while(actions.firstChild)actions.removeChild(actions.firstChild);
        actions.appendChild(node('span','subtle','Delete this replay? Your score history will be kept.'));
        actions.appendChild(button('Keep replay',list));actions.appendChild(button('Delete replay',function(){act('delete');}));
      }));group.appendChild(actions);panel.appendChild(group);
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
    var nativeTarget=node('div','');container.appendChild(nativeTarget);
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
