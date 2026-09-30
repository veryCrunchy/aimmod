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
  function widthOf(e) { return Math.max(0,e.offsetWidth||e.clientWidth||0); }
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
  function metric(parent, title, value, note, change) { var m = append(parent, node('div', undefined, 'metric')); append(m, node('div', title, 'metric-label')); append(m, node('div', value, 'metric-value')); var foot = append(m, node('div', undefined, 'metric-note')); var pct = change && typeof change.ChangePercent === 'number' ? change.ChangePercent : null; if (pct !== null) { var kind = F().trend(pct); append(foot, node('span', (kind === 'up' ? '▲ ' : kind === 'down' ? '▼ ' : '') + F().signed(pct) + '%', 'change ' + kind)); } append(foot, node('span', note || '')); }
  function changeNote(c) { return c && typeof c.ChangePercent === 'number' ? 'vs previous period' : 'Needs 3+ runs in this and the previous period'; }
  function mostCommon(bins) { var best = null; bins.forEach(function (b) { if (b && F().known(b.Count) && b.Count > 0 && (!best || b.Count > best.Count)) best = b; }); return best; }
  function panel(title) { var p = node('div', undefined, 'panel stats-panel'); append(p, node('h2', title)); return p; }
  function empty(parent, title) { append(parent, node('p', title, 'stats-muted')); }
  function legend(parent, items) { var row = append(parent, node('div', undefined, 'stats-legend')); items.forEach(function (item) { var entry = append(row, node('span', undefined, 'stats-legend-item')); var swatch = append(entry, node('span', undefined, 'stats-legend-swatch')); swatch.style.background = item[1]; append(entry, node('span', item[0])); }); }
  function fraction(points, p, index) { return typeof p.RunNumber === 'number' && typeof points[0].RunNumber === 'number' ? (p.RunNumber - points[0].RunNumber) / Math.max(points[points.length - 1].RunNumber - points[0].RunNumber, 1) : index / Math.max(points.length - 1, 1); }
  function message(c, w, h, text) { c.fillStyle = '#a7bab0'; c.font = '14px Arial'; c.textAlign = 'center'; c.fillText(text, w / 2, h / 2); }
  // One readable trend: runs (faint), 5-run average (bold) with a +/-1 SD band,
  // and the fitted trend. The spread chart marks the average and median.
  function chart(canvas, points, histogram, marks) {
    var w = parseFloat(canvas.style.width)||widthOf(canvas), h = parseFloat(canvas.style.height)||280; canvas.chartHits = []; if (!w) return;
    var ratio = global.devicePixelRatio || 1; canvas.width = w * ratio; canvas.height = h * ratio;
    var c = canvas.getContext('2d'); c.scale(ratio, ratio); c.clearRect(0, 0, w, h);
    if (!points.length) { message(c, w, h, 'No scores in this period yet'); return; }
    var values = points.map(function (p) { return histogram ? p.Count : pointValue(p,chartMetric); });
    var known = values.filter(function (v) { return typeof v === 'number' && isFinite(v); });
    if (!known.length) { message(c, w, h, 'No measurements for this metric yet'); return; }
    var band = histogram ? [] : F().rolling(values, 5), range = known.slice();
    band.forEach(function (b) { if (b) { range.push(b.mean + b.sd); range.push(b.mean - b.sd); } });
    var lo = histogram ? 0 : Math.min.apply(null, range), hi = Math.max.apply(null, range);
    if (!histogram) { var padding = Math.max((hi - lo) * 0.08, Math.abs(hi) * 0.01, hi - lo > 0 ? 0 : 1); lo -= padding; hi += padding; if (lo < 0 && Math.min.apply(null, known) >= 0) lo = 0; }
    var axis = F().ticks(lo, hi, 4); lo = axis.min; hi = axis.max;
    var left = histogram ? 44 : 60, right = w - 14, top = 14, bottom = h - 28;
    function y(v) { return bottom - (v - lo) / (hi - lo) * (bottom - top); }
    c.font = '12px Arial'; c.textAlign = 'right';
    axis.values.forEach(function (n) { var yy = Math.round(y(n)) + .5; c.strokeStyle = '#22302a'; c.lineWidth = 1; c.beginPath(); c.moveTo(left, yy); c.lineTo(right, yy); c.stroke(); c.fillStyle = '#a7bab0'; c.fillText(histogram ? number(n, 0) : F().tick(n, axis), left - 8, yy + 4); });
    if (histogram) {
      var width = (right - left) / points.length, from = points[0].From, to = points[points.length - 1].To;
      points.forEach(function (p, index) { c.fillStyle = SERIES.value; c.fillRect(left + index * width + 1.5, y(p.Count), Math.max(1, width - 3), bottom - y(p.Count)); canvas.chartHits.push({ x: left + (index + .5) * width, y: y(p.Count), text: number(p.From) + '–' + number(p.To) + ' · ' + number(p.Count, 0) + (p.Count === 1 ? ' run' : ' runs') }); });
      (marks || []).forEach(function (m) { if (!F().known(m.value) || !(to > from)) return; var x = left + (m.value - from) / (to - from) * (right - left); if (x < left || x > right) return; c.strokeStyle = m.color; c.lineWidth = 2; if (m.dash && c.setLineDash) c.setLineDash([4, 3]); c.beginPath(); c.moveTo(x, top); c.lineTo(x, bottom); c.stroke(); if (c.setLineDash) c.setLineDash([]); });
      c.fillStyle = '#a7bab0'; c.textAlign = 'left'; c.fillText(number(from, 0), left, h - 8); c.textAlign = 'right'; c.fillText(number(to, 0), right, h - 8);
      return;
    }
    function x(p, index) { return left + fraction(points, p, index) * (right - left); }
    var first = -1; band.forEach(function (b, i) { if (b && first < 0) first = i; });
    if (first >= 0) { c.beginPath(); var started = false; for (var i = first; i < band.length; i++) if (band[i]) { var px = x(points[i], i), py = y(band[i].mean + band[i].sd); if (started) c.lineTo(px, py); else { c.moveTo(px, py); started = true; } } for (var j = band.length - 1; j >= first; j--) if (band[j]) c.lineTo(x(points[j], j), y(band[j].mean - band[j].sd)); c.closePath(); c.fillStyle = 'rgba(39,228,161,0.14)'; c.fill(); }
    c.beginPath(); var connected = false;
    points.forEach(function (p, index) { var v = values[index]; if (typeof v !== 'number' || !isFinite(v)) { connected = false; return; } if (connected) c.lineTo(x(p, index), y(v)); else c.moveTo(x(p, index), y(v)); connected = true; });
    c.strokeStyle = 'rgba(39,228,161,0.45)'; c.lineWidth = 1.2; c.stroke();
    // An isolated measured run must remain visible even when neighboring
    // runs are missing this metric and the line correctly breaks there.
    c.fillStyle = SERIES.value; points.forEach(function (p, index) { var v = values[index]; if (typeof v !== 'number' || !isFinite(v)) return; c.beginPath(); c.arc(x(p, index), y(v), points.length > 120 ? 1.6 : 2.4, 0, Math.PI * 2); c.fill(); canvas.chartHits.push({ x: x(p, index), y: y(v), text: (p.Date ? F().dateTime(p.Date) + ' · ' : '') + chartValueText(chartMetric, v) + (band[index] ? ' · 5-run avg ' + chartValueText(chartMetric, band[index].mean) : '') }); });
    if (first >= 0) { c.beginPath(); var on = false; band.forEach(function (b, i) { if (!b) return; if (on) c.lineTo(x(points[i], i), y(b.mean)); else { c.moveTo(x(points[i], i), y(b.mean)); on = true; } }); c.strokeStyle = SERIES.average; c.lineWidth = 2.5; c.stroke(); }
    if (chartMetric === 'Score') { c.beginPath(); var t = false; points.forEach(function (p, index) { if (!F().known(p.TrendLine)) return; if (t) c.lineTo(x(p, index), y(p.TrendLine)); else { c.moveTo(x(p, index), y(p.TrendLine)); t = true; } }); if (c.setLineDash) c.setLineDash([6, 4]); c.strokeStyle = SERIES.trend; c.lineWidth = 1.5; c.stroke(); if (c.setLineDash) c.setLineDash([]); }
    var firstDate = points[0].Date, lastDate = points[points.length - 1].Date;
    c.fillStyle = '#a7bab0'; c.textAlign = 'left'; c.fillText(firstDate ? F().date(firstDate) : 'Earlier runs', left, h - 8); c.textAlign = 'right'; c.fillText(lastDate ? F().date(lastDate) : 'Latest runs', right, h - 8);
    if (firstDate && lastDate && points.length > 8) { var midIndex = Math.floor(points.length / 2), mid = points[midIndex]; if (mid.Date) { c.textAlign = 'center'; c.fillText(F().date(mid.Date), x(mid, midIndex), h - 8); } }
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
    var selected = runFilter==='warmup'&&period.Warmup?period.Warmup:runFilter==='settled'&&period.Settled?period.Settled:period.Selected;
    var toolbar = append(root, node('div', undefined, 'toolbar stats-toolbar'));
    var heading = append(toolbar, node('div', undefined, 'stats-heading')); append(heading, node('h2', selected && selected.Name || 'Choose a scenario'));
    append(heading, node('p', selected ? number(selected.Runs, 0) + (selected.Runs === 1 ? ' run' : ' runs') + (period.Days ? ' in the last ' + period.Days + ' days' : ' in your history') : 'Pick a scenario in the top bar.', 'stats-muted'));
    if(period.Warmup&&period.Settled){var filters=append(toolbar,node('div',undefined,'segmented stats-filter'));[['all','All runs'],['warmup','Warm-up ('+period.Warmup.Runs+')'],['settled','Other runs ('+period.Settled.Runs+')']].forEach(function(f){var b=append(filters,button(f[1],function(){runFilter=f[0];draw();},runFilter===f[0]));b.setAttribute('aria-pressed',String(runFilter===f[0]));});}
    var periods = append(toolbar, node('div', undefined, 'segmented'));
    report.Periods.forEach(function (p) { var b = append(periods, button(p.Days ? p.Days + ' days' : 'All time', function () { periodKey = p.Key; draw(); }, p.Key === period.Key)); b.setAttribute('aria-pressed', String(p.Key === period.Key)); });
    if(runFilter!=='all'&&period.Warmup)empty(root,'Warm-up runs are early, lower scores that recover later in the same session. This filter applies to this scenario’s analysis.');
    if (!selected) { empty(root, 'Choose a scenario to see its score trend.'); return; }
    var availableMetrics=[['Score','Score'],['Accuracy','Accuracy'],['Smoothness','Control'],['Efficiency','Path'],['Jitter','Jitter'],['Correction','Correction']].concat(measures).filter(function(item){return (selected.Points||[]).some(function(p){return pointValue(p,item[0])!==null;});});
    if(!availableMetrics.some(function(item){return item[0]===chartMetric;}))chartMetric=availableMetrics.length?availableMetrics[0][0]:'Score';
    var stats = append(root, node('div', undefined, 'metrics'));
    metric(stats, 'Best score', number(selected.Best), 'Best in this period');
    metric(stats, 'Average score', number(selected.Average), period.Days ? changeNote(selected.ScoreChange) : number(selected.Runs, 0) + (selected.Runs === 1 ? ' run' : ' runs'), period.Days ? selected.ScoreChange : null);
    metric(stats, 'Consistency', F().percent(selected.VariationPercent), 'Run-to-run spread · lower is steadier');
    metric(stats, 'Practice', F().hours(period.Hours), number(period.Runs, 0) + ' runs · ' + number(period.ActiveDays, 0) + ' active days', period.Days ? period.PracticeChange : null);
    var charts = append(root, node('div', undefined, 'stats-columns'));
    var trend = append(charts, panel(chartMetric === 'Score' ? 'Score trend' : ((availableMetrics.filter(function (m) { return m[0] === chartMetric; })[0] || [0, chartMetric])[1]) + ' trend'));
    var chartButtons = append(trend, node('div', undefined, 'stats-chart-controls'));
    availableMetrics.forEach(function(item){var b=append(chartButtons,button(item[1],function(){chartMetric=item[0];draw();},chartMetric===item[0]));b.className='button compact'+(chartMetric===item[0]?' primary':'');b.setAttribute('aria-pressed',String(chartMetric===item[0]));});
    var selectedMeasure=measure(chartMetric);
    legend(trend, [[chartMetric === 'Score' ? 'Each run' : 'Each measured run', 'rgba(39,228,161,0.6)'], ['5-run average', SERIES.average], ['±1 SD band', '#1c4a3a']].concat(chartMetric === 'Score' ? [['Trend', SERIES.trend]] : []));
    if (chartMetric !== 'Score') empty(trend, selectedMeasure ? selectedMeasure[1]+' ('+(selectedMeasure[2]||'recorded value').trim()+'). Runs without this measurement leave a gap.' : chartMetric === 'Smoothness' ? 'Movement control, out of 100.' : chartMetric === 'Accuracy' ? 'Accuracy (%) per run.' : chartMetric + ' per run. Runs without this measurement leave a gap.');
    var trendWrap = append(trend, node('div', undefined, 'stats-chart-wrap'));
    var trendCanvas = append(trendWrap, node('canvas', undefined, 'stats-chart')); hover(trendCanvas, trendWrap);
    var distribution = append(charts, panel('Score spread'));
    append(distribution, node('p', 'How often each score range came up.', 'stats-muted'));
    legend(distribution, [['Average', '#66ccff'], ['Median', '#f0b45a']]);
    var distributionWrap = append(distribution, node('div', undefined, 'stats-chart-wrap'));
    var distributionCanvas = append(distributionWrap, node('canvas', undefined, 'stats-chart')); hover(distributionCanvas, distributionWrap);
    var common = mostCommon(selected.Distribution || []);
    append(distribution, node('p', common ? 'Most runs landed between ' + number(common.From, 0) + ' and ' + number(common.To, 0) + '.' : 'Play a few more runs to see your spread.', 'stats-muted stats-foot'));
    var details=append(root,node('div',undefined,'stats-details'));
    var detailButtons=append(details,node('div',undefined,'stats-detail-controls'));
    append(detailButtons,node('span','Explore more','stats-detail-label'));
    [['movement','Movement & timing'],['practice','Practice pattern'],['scenarios','Compare scenarios']].forEach(function(item){
      var control=button(item[1],function(){detailKey=detailKey===item[0]?'':item[0];draw();},detailKey===item[0]);
      control.setAttribute('aria-expanded',detailKey===item[0]?'true':'false');append(detailButtons,control);
    });
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
    // Gameface: explicit pixel columns (no calc/flex-basis), side by side when wide.
    setTimeout(function () { var width=widthOf(root),wide=width>=1100,trendWidth=wide?Math.round((width-16)*0.68):width,spreadWidth=wide?width-16-trendWidth:width;
      if(width){charts.style.width=width+'px';trend.style.width=trendWidth+'px';distribution.style.width=spreadWidth+'px';trend.style.marginRight=wide?'16px':'0px';charts.style.display=wide?'flex':'block';
        trendCanvas.style.width=Math.max(120,trendWidth-40)+'px';trendCanvas.style.height='300px';distributionCanvas.style.width=Math.max(120,spreadWidth-40)+'px';distributionCanvas.style.height=wide?'254px':'220px';}
      chart(trendCanvas, selected.Points || [], false); chart(distributionCanvas, selected.Distribution || [], true, [{value:selected.Average,color:'#66ccff'},{value:selected.Median,color:'#f0b45a',dash:true}]); }, 0);
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
