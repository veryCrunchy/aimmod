(function (global) {
  'use strict';
  var runFilter = 'all', periodKey = '30', chartMetric = 'Score', detailKey = '', scenarioQuery = '', scenarioLimit = 200, root, report, signature = '', selectScenario;
  var SERIES = { value: '#27e4a1', average: '#c8f3e0', trend: '#e8b667' };
  var measures = [
    ['Overshoot','Overshoot','%',100],['SpeedVariation','Speed variation',' CV',1],['AverageSpeed','Average speed',' px/s at 800 DPI',1],
    ['ClickTimingVariation','Click timing variation',' CV',1],['DirectionalBias','Directional bias','%',100],
    ['KillsPerSecond','Kills per second',' KPS',1],['AverageTimeToKillMs','Average time to kill',' ms',1],['BestTimeToKillMs','Best time to kill',' ms',1],
    ['TimeToKillSpreadMs','Time-to-kill spread',' ms',1],['AccuracyTrend','Accuracy trend','',1],
    ['AverageFireToHitMs','Average shot-to-hit interval',' ms',1],['P90FireToHitMs','P90 shot-to-hit interval',' ms',1],
    ['AverageShotsToHit','Shots per hit','',1],['CorrectiveShotRatio','Corrective shots','%',100]
  ];
  function F() { return global.AimModFormat; }
  function measure(key) { return measures.filter(function(m){return m[0]===key;})[0]; }
  function pointValue(p,key) { var m=measure(key);var v=m ? p.Measurements && p.Measurements[key] : p[key];return typeof v==='number' && isFinite(v) ? v*(m?m[3]:1) : null; }
  function widthOf(e) { return Math.max(0,e.clientWidth||e.offsetWidth||0); }
  function measurementText(m,value) {
    if(m[2]===' ms')return F().millis(value);
    if(m[0]==='AverageSpeed')return number(value,0)+' px/s';
    return number(value*m[3],m[2]==='%'?1:2)+m[2];
  }
  // Values already scaled by pointValue (percent measures multiplied by 100).
  function chartValueText(key,value) {
    var m=measure(key);
    if(m){if(m[2]===' ms')return F().millis(value);if(m[2]==='%')return number(value,1)+'%';if(m[0]==='AverageSpeed')return number(value,0)+' px/s';return number(value,2)+m[2];}
    if(key==='Accuracy')return number(value,1)+'%';
    if(key==='Score')return number(value);
    return number(value,1);
  }
  function renderMeasurements(container,summaries) {
    var grids=[],count=0;
    [['Movement',measures.slice(0,5)],['Target completion',measures.slice(5,10)],['Shot timing',measures.slice(10)]].forEach(function(group){
      var available=group[1].filter(function(m){return (summaries||[]).some(function(s){return s.Key===m[0]&&s.Samples>0&&typeof s.Average==='number'&&isFinite(s.Average);});});
      if(!available.length)return;
      var section=append(container,node('section',undefined,'stats-measure-section'));append(section,node('h3',group[0]));
      var cards=append(section,node('div',undefined,'stats-measure-grid'));grids.push(cards);
      available.forEach(function(m){var value=summaries.filter(function(s){return s.Key===m[0];})[0];count++;
        metric(cards,m[1],measurementText(m,value.Average),number(value.Samples,0)+(value.Samples===1?' measured run':' measured runs')+(m[0]==='AverageSpeed'?' · 800 DPI baseline':''));});
    });
    if(!count)empty(container,'No movement or timing measurements in this selection yet. Try another scenario or a longer period.');
    else empty(container,'Averages per run. Time to kill measures how long targets take to clear. Shot-to-hit intervals can include earlier misses, so they are not input latency.');
    setTimeout(function(){var width=widthOf(container)-((container.className||'').indexOf('stats-panel')>=0?44:0);if(width<=0)return;var cols=width>=1050?4:width>=720?3:width>=430?2:1;
      grids.forEach(function(cards){var columns=width>=1400&&cards.children.length===5?5:cols;cards.style.width=width+'px';for(var i=0;i<cards.children.length;i++){var card=cards.children[i];card.style.width=Math.floor((width-(columns-1)*12)/columns)+'px';card.style.flex='none';card.style.marginRight=(i%columns===columns-1?'0':'12px');}});
    },0);
  }
  function node(tag, text, cls) { var e = document.createElement(tag); if (text !== undefined) e.textContent = text; if (cls) e.className = cls; return e; }
  function number(value, digits) { return F().number(value, digits); }
  function append(parent, child) { parent.appendChild(child); return child; }
  function button(label, action, active) { var b = node('button', label, 'button' + (active ? ' primary' : '')); b.type = 'button'; b.onclick = action; return b; }
  function metric(parent, title, value, note) { var m = append(parent, node('div', undefined, 'metric')); append(m, node('div', title, 'metric-label')); append(m, node('div', value, 'metric-value')); append(m, node('div', note || '', 'metric-note')); }
  function panel(title) { var p = node('div', undefined, 'panel stats-panel'); append(p, node('h2', title)); return p; }
  function empty(parent, title) { append(parent, node('p', title, 'stats-muted')); }
  function changeText(c) { if (!c || typeof c.ChangePercent !== 'number') return 'Needs 3+ runs in this and the previous period'; return F().signed(c.ChangePercent) + '% vs previous period'; }
  function legend(parent, items) { var row = append(parent, node('div', undefined, 'stats-legend')); items.forEach(function (item) { var entry = append(row, node('span', undefined, 'stats-legend-item')); var swatch = append(entry, node('span', undefined, 'stats-legend-swatch')); swatch.style.background = item[1]; append(entry, node('span', item[0])); }); }
  function fraction(points, p, index) { return typeof p.RunNumber === 'number' && typeof points[0].RunNumber === 'number' ? (p.RunNumber - points[0].RunNumber) / Math.max(points[points.length - 1].RunNumber - points[0].RunNumber, 1) : index / Math.max(points.length - 1, 1); }
  function chart(canvas, points, histogram) {
    var w = parseFloat(canvas.style.width)||widthOf(canvas), h = 280; canvas.chartHits = []; if (!w) return;
    var ratio = global.devicePixelRatio || 1; canvas.width = w * ratio; canvas.height = h * ratio;
    var c = canvas.getContext('2d'); c.scale(ratio, ratio); c.clearRect(0, 0, w, h);
    if (!points.length) { c.fillStyle = '#a4b3b8'; c.font = '14px Arial'; c.fillText('No scores in this period yet', 20, 100); return; }
    var values = points.map(function (p) { return histogram ? p.Count : pointValue(p,chartMetric); }).filter(function (v) { return typeof v === 'number' && isFinite(v); });
    if (!values.length) { c.fillStyle = '#a4b3b8'; c.font = '14px Arial'; c.fillText('No measurements for this metric yet', 20, 100); return; }
    var lo = histogram ? 0 : Math.min.apply(null, values), hi = Math.max.apply(null, values);
    if (!histogram) { var padding = Math.max((hi - lo) * 0.12, Math.abs(hi) * 0.02, hi - lo > 0 ? 0 : 1); lo -= padding; hi += padding; if (lo < 0 && Math.min.apply(null, values) >= 0) lo = 0; }
    var axis = F().ticks(lo, hi, 4); lo = axis.min; hi = axis.max;
    var left = 64, right = w - 18, top = 16, bottom = h - 30;
    function y(v) { return bottom - (v - lo) / (hi - lo) * (bottom - top); }
    c.font = '12px Arial'; c.textAlign = 'right';
    axis.values.forEach(function (n) { c.strokeStyle = '#2a3a3f'; c.beginPath(); c.moveTo(left, y(n)); c.lineTo(right, y(n)); c.stroke(); c.fillStyle = '#9aacb3'; c.fillText(histogram ? number(n, 0) : F().tick(n, axis), left - 8, y(n) + 4); });
    if (histogram) {
      var width = (right - left) / points.length;
      points.forEach(function (p, index) { c.fillStyle = SERIES.value; c.fillRect(left + index * width + 2, y(p.Count), Math.max(1, width - 4), bottom - y(p.Count)); canvas.chartHits.push({ x: left + (index + .5) * width, y: y(p.Count), text: number(p.From) + '–' + number(p.To) + ' · ' + number(p.Count, 0) + (p.Count === 1 ? ' run' : ' runs') }); });
      c.fillStyle = '#9aacb3'; c.textAlign = 'left'; c.fillText(number(points[0].From), left, h - 8); c.textAlign = 'right'; c.fillText(number(points[points.length - 1].To), right, h - 8);
    } else {
      var line = function (key, color, weight) {
        c.beginPath(); var connected = false;
        points.forEach(function (p, index) { var v = pointValue(p,key); if (typeof v !== 'number') { connected = false; return; } var x = left + fraction(points, p, index) * (right - left); if (connected) c.lineTo(x, y(v)); else c.moveTo(x, y(v)); connected = true; });
        c.strokeStyle = color; c.lineWidth = weight; c.stroke();
      };
      line(chartMetric, SERIES.value, 1.5); if (chartMetric === 'Score') { line('RollingAverage', SERIES.average, 2); line('TrendLine', SERIES.trend, 1.5); }
      // An isolated measured run must remain visible even when neighboring
      // runs are missing this metric and the line correctly breaks there.
      c.fillStyle=SERIES.value;points.forEach(function(p,index){var v=pointValue(p,chartMetric);if(v===null)return;var x=left+fraction(points,p,index)*(right-left);c.beginPath();c.arc(x,y(v),2.5,0,Math.PI*2);c.fill();canvas.chartHits.push({x:x,y:y(v),text:(p.Date?F().dateTime(p.Date)+' · ':'')+chartValueText(chartMetric,v)});});
      var first = points[0].Date, last = points[points.length - 1].Date;
      c.fillStyle = '#9aacb3'; c.textAlign = 'left'; c.fillText(first ? F().date(first) : 'Earlier runs', left, h - 8); c.textAlign = 'right'; c.fillText(last ? F().date(last) : 'Latest runs', right, h - 8);
    }
  }
  // Nearest-point tooltip; the chart stores hit positions while drawing.
  function hover(canvas, wrap) {
    var tip = append(wrap, node('div', undefined, 'graph-tooltip'));
    canvas.onmousemove = function (e) { var hits = canvas.chartHits || []; if (!hits.length || !canvas.getBoundingClientRect) return; var rect = canvas.getBoundingClientRect(), x = e.clientX - rect.left, best = hits[0]; hits.forEach(function (p) { if (Math.abs(p.x - x) < Math.abs(best.x - x)) best = p; }); tip.textContent = best.text; tip.style.left = Math.max(8, Math.min((rect.width || 400) - 240, best.x + 12)) + 'px'; tip.style.top = Math.max(4, best.y - 34) + 'px'; tip.style.display = 'block'; };
    canvas.onmouseleave = function () { tip.style.display = 'none'; };
  }
  function draw() {
    if (!root || !report || !report.Periods) return; root.textContent = '';
    var period = report.Periods.filter(function (p) { return p.Key === periodKey; })[0] || report.Periods[0];
    if (!period) { var none = append(root, node('div', undefined, 'empty')); append(none, node('h3', 'No practice to show yet')); append(none, node('p', 'Complete a few runs and your trends will appear here.')); return; }
    var toolbar = append(root, node('div', undefined, 'toolbar'));
    append(toolbar, node('h2', 'Practice trends'));
    report.Periods.forEach(function (p) { var b = append(toolbar, button(p.Days ? p.Days + ' days' : 'All time', function () { periodKey = p.Key; draw(); }, p.Key === period.Key)); b.setAttribute('aria-pressed', String(p.Key === period.Key)); });
    var totals = append(root, node('div', undefined, 'metrics'));
    metric(totals, 'Runs', number(period.Runs, 0), number(period.Scenarios, 0) + (period.Scenarios === 1 ? ' scenario' : ' scenarios'));
    metric(totals, 'Practice time', number(period.Hours) + 'h', period.Days ? changeText(period.PracticeChange) : 'All available history');
    metric(totals, 'Active days', number(period.ActiveDays, 0), 'Days with at least one run');
    var selected = runFilter==='warmup'&&period.Warmup?period.Warmup:runFilter==='settled'&&period.Settled?period.Settled:period.Selected;
    if (!selected) { empty(root, 'Choose a scenario to see its score trend.'); return; }
    var availableMetrics=[['Score','Score'],['Accuracy','Accuracy'],['Smoothness','Control'],['Efficiency','Path'],['Jitter','Jitter'],['Correction','Correction']].concat(measures).filter(function(item){return (selected.Points||[]).some(function(p){return pointValue(p,item[0])!==null;});});
    if(!availableMetrics.some(function(item){return item[0]===chartMetric;}))chartMetric=availableMetrics.length?availableMetrics[0][0]:'Score';
    var summary = append(root, panel(selected.Name || 'Choose a scenario'));
    if(period.Warmup&&period.Settled){var filters=append(summary,node('div',undefined,'stats-chart-controls'));[['all','All runs'],['warmup','Warm-up ('+period.Warmup.Runs+')'],['settled','Other runs ('+period.Settled.Runs+')']].forEach(function(f){var b=append(filters,button(f[1],function(){runFilter=f[0];draw();},runFilter===f[0]));b.setAttribute('aria-pressed',String(runFilter===f[0]));});if(runFilter!=='all')empty(summary,'Warm-up runs are early, lower scores that recover later in the same session. This filter applies to this scenario’s analysis.');}
    var stats = append(summary, node('div', undefined, 'metrics'));
    metric(stats, 'Best score', number(selected.Best), 'Best in this period');
    metric(stats, 'Average score', number(selected.Average), period.Days ? changeText(selected.ScoreChange) : number(selected.Runs, 0) + (selected.Runs === 1 ? ' run' : ' runs'));
    metric(stats, 'Median score', number(selected.Median), 'Middle score, less affected by outliers');
    metric(stats, 'Consistency', F().percent(selected.VariationPercent), 'Score spread vs average · lower is steadier');
    var charts = append(root, node('div', undefined, 'stats-columns'));
    var trend = append(charts, panel('Progression'));
    var chartButtons = append(trend, node('div', undefined, 'stats-chart-controls'));
    availableMetrics.forEach(function(item){var b=append(chartButtons,button(item[1],function(){chartMetric=item[0];draw();},chartMetric===item[0]));b.setAttribute('aria-pressed',String(chartMetric===item[0]));});
    var selectedMeasure=measure(chartMetric);
    if (chartMetric === 'Score') legend(trend, [['Score', SERIES.value], ['5-run average', SERIES.average], ['Trend', SERIES.trend]]);
    else empty(trend, selectedMeasure ? selectedMeasure[1]+' ('+(selectedMeasure[2]||'recorded value').trim()+'). Runs without this measurement leave a gap.' : chartMetric === 'Smoothness' ? 'Movement control, out of 100.' : chartMetric === 'Accuracy' ? 'Accuracy (%) per run.' : chartMetric + ' per run. Runs without this measurement leave a gap.');
    var trendWrap = append(trend, node('div', undefined, 'stats-chart-wrap'));
    var trendCanvas = append(trendWrap, node('canvas', undefined, 'stats-chart')); hover(trendCanvas, trendWrap);
    var details=append(root,node('div',undefined,'stats-details'));
    var detailButtons=append(details,node('div',undefined,'stats-detail-controls'));
    append(detailButtons,node('span','Explore more','stats-detail-label'));
    [['distribution','Score spread'],['movement','Movement & timing'],['practice','Practice pattern'],['scenarios','Compare scenarios']].forEach(function(item){
      var control=button(item[1],function(){detailKey=detailKey===item[0]?'':item[0];draw();},detailKey===item[0]);
      control.setAttribute('aria-expanded',detailKey===item[0]?'true':'false');append(detailButtons,control);
    });
    var distribution=null,distributionCanvas=null;
    if(detailKey==='distribution'){
      distribution=append(details,panel('Score distribution'));empty(distribution,'How often you reached each score range.');
      var distributionWrap=append(distribution,node('div',undefined,'stats-chart-wrap'));
      distributionCanvas=append(distributionWrap,node('canvas',undefined,'stats-chart'));hover(distributionCanvas,distributionWrap);
    }
    if(detailKey==='movement')renderMeasurements(append(details,panel('Movement and timing')),selected.Measurements);
    if(detailKey==='practice'){
    var calendar = period.Calendar || [], blockList = period.Blocks || [];
    var practice = append(details, panel('Practice days'));
    if (!calendar.length) empty(practice, 'Your practice days will appear here after your first run.');
    else {
      empty(practice, 'Each bar is one day you practised; taller bars mean more minutes. Shows up to your latest 366 active days.');
      var days = append(practice, node('div', undefined, 'stats-days')); var max = Math.max.apply(null, calendar.map(function (d) { return d.Minutes; })) || 1;
      calendar.forEach(function (d) { var bar = append(days, node('div', undefined, 'stats-day')); bar.style.height = Math.max(4, d.Minutes / max * 76) + 'px'; bar.title = F().date(d.Date) + ' · ' + number(d.Minutes, 0) + ' min · ' + number(d.Runs, 0) + (d.Runs === 1 ? ' run' : ' runs'); });
      var range = append(practice, node('div', undefined, 'stats-day-range')); append(range, node('span', F().date(calendar[0].Date))); append(range, node('span', F().date(calendar[calendar.length - 1].Date)));
    }
    var blocks = append(details, panel('Practice blocks')); empty(blocks, 'A break of more than 30 minutes starts a new block. Showing your latest 100 blocks.');
    if (!blockList.length) empty(blocks, 'No practice blocks in this period.');
    blockList.forEach(function (b) { var item = append(blocks, node('div', undefined, 'stats-block')); append(item, node('strong', F().dateTime(b.Start))); append(item, node('span', number(b.Runs, 0) + (b.Runs === 1 ? ' run' : ' runs') + ' · ' + number(b.Minutes, 0) + ' min')); append(item, node('small', (b.Scenarios || []).join(' · '))); });
    }
    if(detailKey==='scenarios'){
    var scenarios = append(details, panel('Scenario comparison')); empty(scenarios, 'Change compares each scenario’s average with its own previous period.');
    var search = append(scenarios, node('input', undefined, 'stats-search')); search.type = 'search'; search.placeholder = 'Find a scenario'; search.setAttribute('aria-label', 'Find a scenario'); search.value = scenarioQuery;
    var table = append(scenarios, node('div', undefined, 'stats-table')); table.setAttribute('role','table'); var row = append(table, node('div', undefined, 'stats-row stats-head')); row.setAttribute('role','row');
    ['Scenario', 'Runs', 'Hours', 'Best', 'Average', 'Change', 'Accuracy'].forEach(function (v, i) { var cell=append(row,node('div',v,'stats-cell stats-col-'+i)); cell.setAttribute('role','columnheader'); });
    var body=append(table,node('div',undefined,'stats-table-body')); body.setAttribute('role','rowgroup');
    var footer=append(scenarios,node('div',undefined,'stats-table-footer'));
    // Bounded rendering keeps very large libraries responsive in Gameface.
    var rows = function () { body.textContent = ''; footer.textContent = ''; var query = scenarioQuery.toLowerCase(); var matches = (period.ScenarioTable || []).filter(function (s) { return typeof s.Name === 'string' && s.Name.toLowerCase().indexOf(query) >= 0; });
      matches.slice(0, scenarioLimit).forEach(function (s) {
        var r=append(body,node('div',undefined,'stats-row'));r.setAttribute('role','row');var name=append(r,node('div',undefined,'stats-cell stats-col-0'));name.setAttribute('role','cell'); var open=append(name, button(s.Name, function () { if (selectScenario) selectScenario(s.Name); })); open.title='Show '+s.Name;
        var change = s.ScoreChange && typeof s.ScoreChange.ChangePercent === 'number' ? F().signed(s.ScoreChange.ChangePercent) + '%' : '—';
        [number(s.Runs, 0), number(s.Hours), number(s.Best), number(s.Average), change, F().percent(s.Accuracy)].forEach(function (v, i) { var cell=append(r,node('div',v,'stats-cell stats-col-'+(i+1)+(i===4&&change!=='—'?(change.charAt(0)==='-'?' is-down':' is-up'):'')));cell.setAttribute('role','cell'); });
      });
      if (!matches.length) empty(footer, (period.ScenarioTable || []).length ? 'No scenarios match “' + scenarioQuery + '”.' : 'No scenarios in this period.');
      else if (matches.length > scenarioLimit) { empty(footer, 'Showing ' + number(scenarioLimit, 0) + ' of ' + number(matches.length, 0) + ' scenarios.'); append(footer, button('Show more', function () { scenarioLimit += 200; rows(); })); }
    };
    var searchTimer = null;
    search.oninput = function () { scenarioQuery = search.value; scenarioLimit = 200; if (searchTimer) clearTimeout(searchTimer); searchTimer = setTimeout(function () { searchTimer = null; rows(); }, 120); }; rows();
    }
    setTimeout(function () { var width=widthOf(root);if(width){charts.style.width=width+'px';[trend,distribution].forEach(function(p){if(p){p.style.width=width+'px';p.style.marginRight='0px';}});[trendCanvas,distributionCanvas].forEach(function(c){if(c){c.style.width=Math.max(120,width-44)+'px';c.style.height='280px';}});}
      chart(trendCanvas, selected.Points || [], false); if(distributionCanvas)chart(distributionCanvas, selected.Distribution || [], true); }, 0);
  }
  function focusedSearch() { var active = global.document && global.document.activeElement; return !!(active && active.className === 'stats-search'); }
  global.AimModStatistics = {
    render: function (container, data, onScenario) {
      selectScenario = onScenario;
      // Polls re-send the whole workspace state; unchanged trends are not rebuilt,
      // which also keeps the scenario search focused while typing.
      var next = ''; try { next = JSON.stringify(data); } catch (e) { next = ''; }
      if (container === root && next && next === signature && root.children && root.children.length) return;
      var refocus = container === root && focusedSearch();
      root = container; report = data; signature = next; draw();
      if (refocus && root.querySelector) { var input = root.querySelector('.stats-search'); if (input && input.focus) input.focus(); }
    },
    resize: draw, renderMeasurements: renderMeasurements
  };
})(window);
