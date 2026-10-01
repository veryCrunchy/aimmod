(function(root){
  'use strict';
  var container=null,request=null,generation=0,items=[],linked=false,query='',rankedOnly=true,onScenario=null;
  function el(tag,css,text){var n=root.document.createElement(tag);n.className=css||'';if(text!==undefined)n.textContent=text;return n;}
  function button(text,fn,css){var n=el('button',css||'button',text);n.type='button';n.onclick=fn;return n;}
  function clear(){while(container&&container.firstChild)container.removeChild(container.firstChild);}
  function cancel(){generation++;if(request){request.abort();request=null;}}
  function get(path,done){cancel();var ticket=generation,xhr=new root.XMLHttpRequest();request=xhr;var pathName=root.location.pathname;var prefix=pathName.slice(0,pathName.lastIndexOf('/'));xhr.open('GET',prefix+'/'+path,true);xhr.timeout=20000;var finished=false;
    function finish(ok,data){if(finished)return;finished=true;if(ticket!==generation||!container)return;request=null;done(ok,data);}
    xhr.onreadystatechange=function(){if(xhr.readyState!==4)return;if(xhr.status!==200){finish(false);return;}try{finish(true,JSON.parse(xhr.responseText));}catch(e){finish(false);}};
    xhr.onerror=xhr.ontimeout=function(){finish(false);};xhr.send();
  }
  function number(value){return root.AimModFormat.number(value,2);}
  var TIERS=['#a7bab0','#c9905c','#c3ccd2','#e8c15a','#5fd6c8','#7fb2ff','#c79bff','#ff8ab0','#ff6b6b'];
  // Colour a rank by its position in the benchmark's ladder (index 0 = first rank).
  function tier(rank){return rank&&typeof rank.index==='number'&&isFinite(rank.index)?TIERS[Math.max(0,Math.min(TIERS.length-1,Math.round(rank.index)))]:'#a7bab0';}
  function rankLabel(parent,rank,css){var label=el('span',css,rank&&rank.name?root.AimModFormat.safeText(rank.name,'Rank '+((rank.index||0)+1)):'Unranked');if(rank&&rank.name)label.style.color=tier(rank);parent.appendChild(label);return label;}
  function title(item){return root.AimModFormat.safeText(item.name,'Benchmark '+item.id);}
  function byline(item){return [root.AimModFormat.safeText(item.type,''),root.AimModFormat.safeText(item.author,'')].filter(Boolean).join(' · ');}
  function actionsRow(){var row=el('div','actions');container.appendChild(row);return row;}
  function notice(text){container.appendChild(el('p','benchmark-empty',text));}
  // Same error pattern as the other pages: a panel with the problem, what to do, and Try again.
  function failure(title,text,retry){var box=el('div','panel empty');box.appendChild(el('h3','',title));box.appendChild(el('p','',text));var row=el('div','actions');row.appendChild(button('Try again',retry,'button primary'));box.appendChild(row);container.appendChild(box);}
  function widthOf(node){return node.offsetWidth||node.clientWidth||0;}
  function list(){detailPage=null;cancel();clear();var card=el('div','benchmark-panel');container.appendChild(card);card.appendChild(el('h2','','Your benchmarks'));
    card.appendChild(el('p','subtle','Open one to see the score for your next rank.'));
    if(!linked){var empty=el('div','benchmark-empty');empty.appendChild(el('p','','Link your AimMod account to see your benchmark ranks.'));if(root.AimModWorkspace){var link=el('div','actions center');link.appendChild(button('Link account',function(){root.AimModWorkspace.open('account');},'button primary'));empty.appendChild(link);}card.appendChild(empty);return;}
    var filters=el('div','benchmark-filters'),search=el('input','benchmark-search');search.type='search';search.setAttribute('aria-label','Find a benchmark');search.value=query;filters.appendChild(root.AimModFormat.field(search,'Find a benchmark','benchmark-search-field'));
    var scope=el('div','segmented benchmark-scope');var rankedButton=button('Ranked',function(){setRanked(true);},'button'+(rankedOnly?' primary':'')),allButton=button('All',function(){setRanked(false);},'button'+(rankedOnly?'':' primary'));function setRanked(value){rankedOnly=value;rankedButton.className='button'+(rankedOnly?' primary':'');allButton.className='button'+(rankedOnly?'':' primary');rankedButton.setAttribute('aria-pressed',String(rankedOnly));allButton.setAttribute('aria-pressed',String(!rankedOnly));rows();}rankedButton.setAttribute('aria-pressed',String(rankedOnly));allButton.setAttribute('aria-pressed',String(!rankedOnly));scope.appendChild(rankedButton);scope.appendChild(allButton);filters.appendChild(scope);filters.appendChild(button('Refresh',load));card.appendChild(filters);
    var results=el('div','benchmark-results');card.appendChild(results);
    // Only the result rows are rebuilt, so the search field keeps focus while typing.
    function rows(){while(results.firstChild)results.removeChild(results.firstChild);
      var q=query.toLowerCase(),shown=items.filter(function(x){return (!rankedOnly||x.rank)&&(!q||(x.name+' '+(x.author||'')+' '+(x.type||'')).toLowerCase().indexOf(q)>=0);});
      if(!shown.length){var none=el('div','benchmark-empty');none.appendChild(el('p','',items.length?(rankedOnly&&!q?'You have no ranked benchmarks yet.':'No benchmarks match your filters.'):'No benchmarks yet. Refresh your Hub history in Account to check again.'));if(items.length&&rankedOnly){var row=el('div','actions center');row.appendChild(button('Show all benchmarks',function(){setRanked(false);}));none.appendChild(row);}results.appendChild(none);return;}
      var heading=el('div','benchmark-row benchmark-heading');heading.appendChild(el('span','benchmark-name','Benchmark'));heading.appendChild(el('span','benchmark-rank','Your rank'));heading.appendChild(el('span','benchmark-open',''));results.appendChild(heading);
      shown.forEach(function(item){var row=button('',function(){detail(item);},'benchmark-row benchmark-item'),name=el('span','benchmark-name');name.appendChild(el('strong','',title(item)));var meta=byline(item);if(meta)name.appendChild(el('span','benchmark-meta',meta));row.appendChild(name);rankLabel(row,item.rank,'benchmark-rank'+(item.rank?' is-ranked':''));row.appendChild(el('span','benchmark-open','View'));results.appendChild(row);});
    }
    var timer=null;search.oninput=function(){if(timer)root.clearTimeout(timer);timer=root.setTimeout(function(){timer=null;if(query!==search.value){query=search.value;rows();}},150);};
    search.onchange=function(){if(timer){root.clearTimeout(timer);timer=null;}query=search.value;rows();};rows();
  }
  var detailPage=null,categoryIndex=0,scenarioQuery='',openScenario=-1;
  function thresholdsFor(scenario){return (scenario.thresholds||[]).filter(function(t){return typeof t.score==='number'&&isFinite(t.score)&&t.score>=0;}).sort(function(a,b){return a.score-b.score;});}
  // Rank ladder: each threshold gets an equal step so low ranks stay readable;
  // the fill moves linearly between the thresholds around your score.
  function ladderPosition(score,thresholds){var n=thresholds.length;if(!n||typeof score!=='number'||!isFinite(score)||score<=0)return 0;for(var i=0;i<n;i++){if(score<thresholds[i].score){var from=i?thresholds[i-1].score:0,span=thresholds[i].score-from;return (i+(span>0?(score-from)/span:0))/n;}}return 1;}
  function ladder(scenario,thresholds){var box=el('div','benchmark-ladder'),track=el('div','benchmark-ladder-track'),fill=el('div','benchmark-ladder-fill');var position=ladderPosition(scenario.score,thresholds);fill.style.width=Math.round(position*1000)/10+'%';track.appendChild(fill);
    thresholds.forEach(function(t,i){var tick=el('span','benchmark-ladder-tick'+(scenario.score>=t.score?' reached':''));tick.style.left=Math.round((i+1)/thresholds.length*1000)/10+'%';track.appendChild(tick);});box.appendChild(track);
    var labels=el('div','benchmark-ladder-labels');thresholds.forEach(function(t){var label=el('span','benchmark-ladder-label'+(scenario.score>=t.score?' reached':''),t.rank);label.style.width=Math.floor(1000/thresholds.length)/10+'%';labels.appendChild(label);});box.appendChild(labels);
    track.setAttribute('role','img');track.setAttribute('aria-label','Rank ladder: '+(scenario.rank?scenario.rank.name:'Unranked')+', '+number(scenario.score)+' points');return box;}
  function nextFor(scenario,thresholds){for(var i=0;i<thresholds.length;i++)if(thresholds[i].score>scenario.score)return thresholds[i];return null;}
  function drawDetail(){
    if(!container||!detailPage)return;clear();var page=detailPage;
    var back=button('Back to benchmarks',function(){detailPage=null;list();},'button quiet benchmark-back');actionsRow().appendChild(back);
    var header=el('div','benchmark-detail-header'),identity=el('div','benchmark-identity');identity.appendChild(el('h2','',title(page)));identity.appendChild(el('p','subtle',byline(page)));var total=0;page.categories.forEach(function(c){total+=(c.scenarios||[]).length;});identity.appendChild(el('span','benchmark-count',page.categories.length+' categories · '+total+' scenarios'));header.appendChild(identity);
    var overall=el('div','benchmark-rank-summary');overall.appendChild(el('span','benchmark-count','Overall rank'));rankLabel(overall,page.rank,'benchmark-overall-rank');header.appendChild(overall);container.appendChild(header);
    if(!page.categories.length){notice('No scenario thresholds are available for this benchmark yet.');return;}
    var search=el('input','benchmark-search benchmark-scenario-search');search.type='search';search.setAttribute('aria-label','Find a scenario in this benchmark');search.value=scenarioQuery;container.appendChild(root.AimModFormat.field(search,'Find a scenario in this benchmark','benchmark-scenario-field'));
    var body=el('div','benchmark-detail-body');container.appendChild(body);
    function drawBody(){
      while(body.firstChild)body.removeChild(body.firstChild);
      var q=scenarioQuery.toLowerCase();function matching(c){return (c.scenarios||[]).filter(function(s){return !q||s.name.toLowerCase().indexOf(q)>=0;});}
      var category=page.categories[categoryIndex];if(!category){categoryIndex=0;category=page.categories[0];}
      var nav=el('div','benchmark-categories');nav.setAttribute('aria-label','Benchmark categories');nav.appendChild(el('div','benchmark-category-label','Categories'));body.appendChild(nav);
      page.categories.forEach(function(c,index){var entries=matching(c),choice=button('',function(){categoryIndex=index;openScenario=-1;drawBody();},'benchmark-category'+(index===categoryIndex?' active':''));choice.setAttribute('aria-pressed',String(index===categoryIndex));choice.appendChild(el('span','',c.name||'Scenarios'));choice.appendChild(el('small','',String(entries.length)));nav.appendChild(choice);});
      var panel=el('section','benchmark-category-content');body.appendChild(panel);
      // Explicit widths avoid unsupported calc/flex basis behaviour in Gameface.
      var width=widthOf(container)||900,side=width>=820?190:150;nav.style.width=side+'px';nav.style.flexShrink='0';panel.style.width=Math.max(200,width-side-18)+'px';panel.style.flexShrink='0';if(width<560){body.style.display='block';nav.style.width=width+'px';nav.style.maxHeight='160px';nav.style.marginBottom='16px';panel.style.width=width+'px';}else body.style.display='flex';
      var categoryHeading=el('div','benchmark-category-title');categoryHeading.appendChild(el('h3','',category.name||'Scenarios'));categoryHeading.appendChild(el('span','benchmark-count',matching(category).length+' scenarios'));panel.appendChild(categoryHeading);
      var labels=el('div','benchmark-compact-row benchmark-compact-head');['Scenario','Score','Rank','Next rank',''].forEach(function(label,i){labels.appendChild(el('span','benchmark-col-'+i,label));});panel.appendChild(labels);
      var matches=matching(category);if(!matches.length)panel.appendChild(el('p','benchmark-empty','No scenarios match.'));
      matches.forEach(function(scenario,index){
        var thresholds=thresholdsFor(scenario),next=nextFor(scenario,thresholds),isOpen=openScenario===index;
        var row=button('',function(){openScenario=isOpen?-1:index;drawBody();},'benchmark-compact-row benchmark-toggle'+(isOpen?' expanded':''));row.setAttribute('aria-expanded',String(isOpen));row.setAttribute('aria-label',scenario.name+' — show rank thresholds');
        var name=el('span','benchmark-col-0',scenario.name);row.appendChild(name);row.appendChild(el('strong','benchmark-col-1',number(scenario.score)));rankLabel(row,scenario.rank,'benchmark-col-2');
        var progress=el('span','benchmark-col-3');progress.appendChild(el('span','',next?number(next.score-scenario.score)+' to '+next.rank:thresholds.length?'Top target reached':'No thresholds'));
        if(next){var track=el('span','benchmark-progress'),fill=el('span','benchmark-progress-fill');fill.style.width=Math.max(0,Math.min(100,scenario.score/next.score*100))+'%';track.setAttribute('role','progressbar');track.setAttribute('aria-label',scenario.name+' progress to '+next.rank);track.setAttribute('aria-valuemin','0');track.setAttribute('aria-valuemax',String(next.score));track.setAttribute('aria-valuenow',String(Math.max(0,scenario.score)));track.appendChild(fill);progress.appendChild(track);}row.appendChild(progress);row.appendChild(el('span','benchmark-col-4',isOpen?'-':'+'));panel.appendChild(row);
        if(!isOpen)return;
        var details=el('div','benchmark-expanded');details.appendChild(el('h4','',scenario.name));var actions=el('div','benchmark-detail-actions');actions.appendChild(el('span','subtle',next?number(next.score-scenario.score)+' to '+next.rank+' · '+number(next.score)+' target':thresholds.length?'Highest listed target reached':'Thresholds unavailable'));if(onScenario)actions.appendChild(button('View stats',function(){onScenario(scenario.name);}));details.appendChild(actions);
        if(thresholds.length)details.appendChild(ladder(scenario,thresholds));
        var ranks=el('div','benchmark-rank-table'),rankHeader=el('div','benchmark-rank-line benchmark-compact-head');['Rank','Target score','Your progress'].forEach(function(label){rankHeader.appendChild(el('span','',label));});ranks.appendChild(rankHeader);
        thresholds.forEach(function(t){var achieved=scenario.score>=t.score,line=el('div','benchmark-rank-line'+(achieved?' achieved':''));line.appendChild(el('span','',t.rank));line.appendChild(el('strong','',number(t.score)));line.appendChild(el('span','',achieved?'Reached':number(t.score-scenario.score)+' remaining'));ranks.appendChild(line);});if(thresholds.length)details.appendChild(ranks);panel.appendChild(details);
      });
    }
    search.oninput=function(){scenarioQuery=search.value;openScenario=-1;if(scenarioQuery){var q=scenarioQuery.toLowerCase();var current=page.categories[categoryIndex];if(!(current.scenarios||[]).some(function(s){return s.name.toLowerCase().indexOf(q)>=0;})){for(var i=0;i<page.categories.length;i++)if((page.categories[i].scenarios||[]).some(function(s){return s.name.toLowerCase().indexOf(q)>=0;})){categoryIndex=i;break;}}}drawBody();};drawBody();
  }
  function detail(item){cancel();clear();actionsRow().appendChild(button('Back to benchmarks',list,'button quiet'));notice('Loading benchmark…');get('benchmark?id='+encodeURIComponent(item.id),function(ok,page){if(!ok||!page||!Array.isArray(page.categories)){clear();actionsRow().appendChild(button('Back to benchmarks',list,'button quiet'));notice('Couldn’t load this benchmark. Try again.');actionsRow().appendChild(button('Try again',function(){detail(item);},'button primary'));return;}detailPage=page;categoryIndex=0;scenarioQuery='';openScenario=-1;drawDetail();});}
  function load(){clear();notice('Loading benchmarks…');get('benchmarks',function(ok,data){if(!ok||!data||!Array.isArray(data.items)){clear();failure('Could not load benchmarks','AimMod couldn’t reach AimMod Hub or its local service. Check your connection and try again.',load);return;}linked=!!data.linked;items=data.items.filter(function(x){return x&&typeof x.id==='number'&&x.id>0&&typeof x.name==='string';});list();});}
  root.AimModBenchmarks={enter:function(target,selectScenario){cancel();onScenario=typeof selectScenario==='function'?selectScenario:null;container=target;if(container)load();},resize:function(){if(container&&detailPage)drawDetail();},back:function(){if(container&&detailPage)list();},leave:function(){cancel();container=null;detailPage=null;}};
})(window);
