(function (global) {
  'use strict';
  var runFilter = 'all', periodKey = '30', chartMetric = 'Score', detailKey = '', root, report, selectScenario;
  var measures = [
    ['Overshoot','Overshoot','%',100],['SpeedVariation','Speed variation',' CV',1],['AverageSpeed','Average speed',' px/s at 800 DPI',1],
    ['ClickTimingVariation','Click timing variation',' CV',1],['DirectionalBias','Directional bias','%',100],
    ['KillsPerSecond','Kills per second',' KPS',1],['AverageTimeToKillMs','Average time to kill',' ms',1],['BestTimeToKillMs','Best time to kill',' ms',1],
    ['TimeToKillSpreadMs','Time-to-kill spread',' ms',1],['AccuracyTrend','Accuracy trend','',1],
    ['AverageFireToHitMs','Average shot-to-hit interval',' ms',1],['P90FireToHitMs','P90 shot-to-hit interval',' ms',1],
    ['AverageShotsToHit','Shots per hit','',1],['CorrectiveShotRatio','Corrective shots','%',100]
  ];
  function measure(key) { return measures.filter(function(m){return m[0]===key;})[0]; }
  function pointValue(p,key) { var m=measure(key);var v=m ? p.Measurements && p.Measurements[key] : p[key];return typeof v==='number' && isFinite(v) ? v*(m?m[3]:1) : null; }
  function widthOf(e) { return Math.max(0,e.clientWidth||e.offsetWidth||0); }
  function measurementText(m,value) {
    if(m[2]===' ms')return value>=1000 ? number(value/1000,1)+' s' : number(value,0)+' ms';
    if(m[0]==='AverageSpeed')return number(value,0)+' px/s';
    return number(value*m[3],m[2]==='%'?1:2).replace(/\.00$/,'')+m[2];
  }
  function renderMeasurements(container,summaries) {
    var grids=[],count=0;
    [['Movement',measures.slice(0,5)],['Target completion',measures.slice(5,10)],['Shot timing',measures.slice(10)]].forEach(function(group){
      var available=group[1].filter(function(m){return (summaries||[]).some(function(s){return s.Key===m[0]&&s.Samples>0&&typeof s.Average==='number'&&isFinite(s.Average);});});
      if(!available.length)return;
      var section=append(container,node('section',undefined,'stats-measure-section'));append(section,node('h3',group[0]));
      var cards=append(section,node('div',undefined,'stats-measure-grid'));grids.push(cards);
      available.forEach(function(m){var value=summaries.filter(function(s){return s.Key===m[0];})[0];count++;
        metric(cards,m[1],measurementText(m,value.Average),value.Samples+' measured runs'+(m[0]==='AverageSpeed'?' · 800 DPI baseline':''));});
    });
    if(!count)empty(container,'No measurements in this selection. Choose another scenario or a longer period.');
    else empty(container,'Run averages. Time to kill measures target completion; shot-to-hit intervals can include earlier missed shots and do not measure input latency.');
    setTimeout(function(){var width=widthOf(container)-((container.className||'').indexOf('stats-panel')>=0?44:0);if(width<=0)return;var cols=width>=1050?4:width>=720?3:width>=430?2:1;
      grids.forEach(function(cards){var columns=width>=1400&&cards.children.length===5?5:cols;cards.style.width=width+'px';for(var i=0;i<cards.children.length;i++){var card=cards.children[i];card.style.width=Math.floor((width-(columns-1)*12)/columns)+'px';card.style.flex='none';card.style.marginRight=(i%columns===columns-1?'0':'12px');}});
    },0);
  }
  function date(value) { var d = new Date(value); if (isNaN(d.getTime())) return '—'; return d.getDate() + ' ' + ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'][d.getMonth()] + ' ' + d.getFullYear() + ' ' + d.getHours() + ':' + ('0' + d.getMinutes()).slice(-2); }
  function node(tag, text, cls) { var e = document.createElement(tag); if (text !== undefined) e.textContent = text; if (cls) e.className = cls; return e; }
  function number(value, digits) { return typeof value === 'number' && isFinite(value) ? value.toFixed(digits === undefined ? 1 : digits).replace(/\.0$/, '') : '—'; }
  function append(parent, child) { parent.appendChild(child); return child; }
  function button(label, action, active) { var b = node('button', label, 'button' + (active ? ' primary' : '')); b.onclick = action; return b; }
  function metric(parent, title, value, note) { var m = append(parent, node('div', undefined, 'metric')); append(m, node('div', title, 'metric-label')); append(m, node('div', value, 'metric-value')); append(m, node('div', note || '', 'metric-note')); }
  function panel(title) { var p = node('div', undefined, 'panel stats-panel'); append(p, node('h2', title)); return p; }
  function empty(parent, title) { append(parent, node('p', title, 'stats-muted')); }
  function changeText(c) { if (!c || c.ChangePercent === null) return 'At least 3 runs in each period needed'; return (c.ChangePercent > 0 ? '+' : '') + number(c.ChangePercent) + '% vs previous period'; }
  function chart(canvas, points, histogram) {
    var w = parseFloat(canvas.style.width)||widthOf(canvas), h = 280; if (!w) return;
    var ratio = global.devicePixelRatio || 1; canvas.width = w * ratio; canvas.height = h * ratio;
    var c = canvas.getContext('2d'); c.scale(ratio, ratio); c.clearRect(0, 0, w, h);
    if (!points.length) { c.fillStyle = '#8daa97'; c.font = '14px Arial'; c.fillText('No scores in this period', 20, 100); return; }
    var values = points.map(function (p) { return histogram ? p.Count : pointValue(p,chartMetric); }).filter(function (v) { return typeof v === 'number' && isFinite(v); });
    if (!values.length) { c.fillStyle = '#8daa97'; c.font = '14px Arial'; c.fillText('No measurements for this metric', 20, 100); return; }
    var lo = histogram ? 0 : Math.min.apply(null, values), hi = Math.max.apply(null, values);
    var padding = Math.max((hi - lo) * 0.12, Math.abs(hi) * 0.02, 1); if (!histogram) lo -= padding; hi += padding;
    var left = 64, right = w - 18, top = 16, bottom = h - 30;
    function y(v) { return bottom - (v - lo) / (hi - lo) * (bottom - top); }
    c.font = '11px Arial'; c.textAlign = 'right';
    for (var i = 0; i <= 4; i++) { var n = lo + (hi - lo) * i / 4; c.strokeStyle = '#233d2f'; c.beginPath(); c.moveTo(left, y(n)); c.lineTo(right, y(n)); c.stroke(); c.fillStyle = '#8daa97'; c.fillText(number(n, 0), left - 8, y(n) + 4); }
    if (histogram) {
      var width = (right - left) / points.length;
      points.forEach(function (p, index) { c.fillStyle = '#27e4a1'; c.fillRect(left + index * width + 2, y(p.Count), Math.max(1, width - 4), bottom - y(p.Count)); });
      c.fillStyle = '#8daa97'; c.textAlign = 'left'; c.fillText(number(points[0].From), left, h - 8); c.textAlign = 'right'; c.fillText(number(points[points.length - 1].To), right, h - 8);
    } else {
      function line(key, color, weight) {
        c.beginPath(); var connected = false;
        points.forEach(function (p, index) { var v = pointValue(p,key); if (typeof v !== 'number') { connected = false; return; } var fraction = typeof p.RunNumber === 'number' ? (p.RunNumber - points[0].RunNumber) / Math.max(points[points.length - 1].RunNumber - points[0].RunNumber, 1) : index / Math.max(points.length - 1, 1); var x = left + fraction * (right - left); if (connected) c.lineTo(x, y(v)); else c.moveTo(x, y(v)); connected = true; });
        c.strokeStyle = color; c.lineWidth = weight; c.stroke();
      }
      line(chartMetric, '#27e4a1', 1.5); if (chartMetric === 'Score') { line('RollingAverage', '#c8f3e0', 2); line('TrendLine', '#e8b667', 1.5); }
      // An isolated measured run must remain visible even when neighboring
      // runs are missing this metric and the line correctly breaks there.
      c.fillStyle='#27e4a1';points.forEach(function(p,index){var v=pointValue(p,chartMetric);if(v===null)return;var fraction=typeof p.RunNumber==='number'?(p.RunNumber-points[0].RunNumber)/Math.max(points[points.length-1].RunNumber-points[0].RunNumber,1):index/Math.max(points.length-1,1);c.beginPath();c.arc(left+fraction*(right-left),y(v),2.5,0,Math.PI*2);c.fill();});
      c.fillStyle = '#8daa97'; c.textAlign = 'left'; c.fillText('Earlier runs', left, h - 8); c.textAlign = 'right'; c.fillText('Latest runs', right, h - 8);
    }
  }
  function draw() {
    if (!root || !report) return; root.textContent = '';
    var period = report.Periods.filter(function (p) { return p.Key === periodKey; })[0] || report.Periods[0]; if (!period) return;
    var toolbar = append(root, node('div', undefined, 'toolbar'));
    append(toolbar, node('h2', 'Practice trends'));
    report.Periods.forEach(function (p) { append(toolbar, button(p.Days ? p.Days + ' days' : 'All time', function () { periodKey = p.Key; draw(); }, p.Key === period.Key)); });
    var totals = append(root, node('div', undefined, 'metrics'));
    metric(totals, 'Runs', number(period.Runs, 0), period.Scenarios + ' scenarios');
    metric(totals, 'Practice', number(period.Hours) + 'h', period.Days ? changeText(period.PracticeChange) : 'Available history');
    metric(totals, 'Active days', number(period.ActiveDays, 0), 'Calendar dates in your local time zone');
    var selected = runFilter==='warmup'&&period.Warmup?period.Warmup:runFilter==='settled'&&period.Settled?period.Settled:period.Selected;
    var availableMetrics=[['Score','Score'],['Accuracy','Accuracy'],['Smoothness','Control'],['Efficiency','Path'],['Jitter','Jitter'],['Correction','Correction']].concat(measures).filter(function(item){return selected.Points.some(function(p){return pointValue(p,item[0])!==null;});});
    if(!availableMetrics.some(function(item){return item[0]===chartMetric;}))chartMetric=availableMetrics.length?availableMetrics[0][0]:'Score';
    var summary = append(root, panel(selected.Name || 'Choose a scenario'));
    if(period.Warmup&&period.Settled){var filters=append(summary,node('div',undefined,'stats-chart-controls'));[['all','All runs'],['warmup','Warm-up ('+period.Warmup.Runs+')'],['settled','Other runs ('+period.Settled.Runs+')']].forEach(function(f){append(filters,button(f[1],function(){runFilter=f[0];draw();},runFilter===f[0]));});if(runFilter!=='all')empty(summary,'Warm-up labels identify early lower scores that recover within a practice session. This filter applies to this scenario’s analysis.');}
    var stats = append(summary, node('div', undefined, 'metrics'));
    metric(stats, 'Best score', number(selected.Best), 'Best in this period');
    metric(stats, 'Average score', number(selected.Average), period.Days ? changeText(selected.ScoreChange) : selected.Runs + ' runs');
    metric(stats, 'Median score', number(selected.Median), 'Middle score, less affected by outliers');
    metric(stats, 'Score variation', selected.VariationPercent === null ? '—' : number(selected.VariationPercent) + '%', 'Standard deviation ÷ mean; lower is steadier');
    var charts = append(root, node('div', undefined, 'stats-columns'));
    var trend = append(charts, panel('Progression'));
    var chartButtons = append(trend, node('div', undefined, 'stats-chart-controls'));
    availableMetrics.forEach(function(item){append(chartButtons,button(item[1],function(){chartMetric=item[0];draw();},chartMetric===item[0]));});
    var selectedMeasure=measure(chartMetric);
    empty(trend, selectedMeasure ? selectedMeasure[1]+' ('+(selectedMeasure[2]||'recorded value').trim()+'). Missing measurements leave a gap.' : chartMetric === 'Score' ? 'Scores · 5-run average · trend line' : chartMetric === 'Smoothness' ? 'Movement control, out of 100.' : chartMetric + ' (%). Missing measurements leave a gap.');
    var trendCanvas = append(trend, node('canvas', undefined, 'stats-chart'));
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
      distributionCanvas=append(distribution,node('canvas',undefined,'stats-chart'));
    }
    if(detailKey==='movement')renderMeasurements(append(details,panel('Movement and timing')),selected.Measurements);
    if(detailKey==='practice'){
    var practice = append(details, panel('Practice days'));
    if (!period.Calendar.length) empty(practice, 'Your recorded practice will appear here.');
    else {
      empty(practice, 'Each bar shows a recorded practice day. Up to the latest 366 active days are shown.');
      var days = append(practice, node('div', undefined, 'stats-days')); var max = Math.max.apply(null, period.Calendar.map(function (d) { return d.Minutes; }));
      period.Calendar.forEach(function (d) { var bar = append(days, node('div', undefined, 'stats-day')); bar.style.height = Math.max(4, d.Minutes / max * 76) + 'px'; bar.title = d.Date + ' · ' + number(d.Minutes) + ' min · ' + d.Runs + ' runs'; });
      empty(practice, period.Calendar[0].Date + ' — ' + period.Calendar[period.Calendar.length - 1].Date);
    }
    var blocks = append(details, panel('Practice blocks')); empty(blocks, 'A break longer than 30 minutes starts a new block. Showing the latest 100 blocks.');
    if (!period.Blocks.length) empty(blocks, 'No practice blocks in this period.');
    period.Blocks.forEach(function (b) { var item = append(blocks, node('div', undefined, 'stats-block')); append(item, node('strong', date(b.Start))); append(item, node('span', b.Runs + ' runs · ' + number(b.Minutes) + ' min')); append(item, node('small', b.Scenarios.join(' · '))); });
    }
    if(detailKey==='scenarios'){
    var scenarios = append(details, panel('Scenario comparison')); empty(scenarios, 'Progress compares each scenario with its own previous period.');
    var search = append(scenarios, node('input', undefined, 'stats-search')); search.placeholder = 'Find a scenario'; search.setAttribute('aria-label', 'Find a scenario');
    var table = append(scenarios, node('div', undefined, 'stats-table')); table.setAttribute('role','table'); var row = append(table, node('div', undefined, 'stats-row stats-head')); row.setAttribute('role','row');
    ['Scenario', 'Runs', 'Hours', 'Best', 'Average', 'Change', 'Accuracy'].forEach(function (v, i) { var cell=append(row,node('div',v,'stats-cell stats-col-'+i)); cell.setAttribute('role','columnheader'); });
    var body=append(table,node('div',undefined,'stats-table-body')); body.setAttribute('role','rowgroup');
    function rows() { body.textContent = ''; var query = search.value.toLowerCase(); period.ScenarioTable.filter(function (s) { return s.Name.toLowerCase().indexOf(query) >= 0; }).forEach(function (s) {
      var r=append(body,node('div',undefined,'stats-row'));r.setAttribute('role','row');var name=append(r,node('div',undefined,'stats-cell stats-col-0'));name.setAttribute('role','cell'); append(name, button(s.Name, function () { if (selectScenario) selectScenario(s.Name); }));
      [number(s.Runs, 0), number(s.Hours), number(s.Best), number(s.Average), s.ScoreChange.ChangePercent === null ? '—' : (s.ScoreChange.ChangePercent > 0 ? '+' : '') + number(s.ScoreChange.ChangePercent) + '%', s.Accuracy === null ? '—' : number(s.Accuracy) + '%'].forEach(function (v, i) { var cell=append(r,node('div',v,'stats-cell stats-col-'+(i+1)));cell.setAttribute('role','cell'); });
    }); }
    search.oninput = rows; rows();
    }
    setTimeout(function () { var width=widthOf(root);if(width){charts.style.width=width+'px';[trend,distribution].forEach(function(p){if(p){p.style.width=width+'px';p.style.marginRight='0px';}});[trendCanvas,distributionCanvas].forEach(function(c){if(c){c.style.width=Math.max(120,width-44)+'px';c.style.height='280px';}});}
      chart(trendCanvas, selected.Points, false); if(distributionCanvas)chart(distributionCanvas, selected.Distribution, true); }, 0);
  }
  global.AimModStatistics = { render: function (container, data, onScenario) { root = container; report = data; selectScenario = onScenario; draw(); }, resize: draw,renderMeasurements:renderMeasurements };
})(window);
