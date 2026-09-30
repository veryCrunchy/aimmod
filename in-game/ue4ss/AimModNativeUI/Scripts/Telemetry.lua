-- Observe native challenge events. No calls into score or upload functions.
local M = {}
local LiveScore = require('LiveScore')
local state = { revision=0 }
local sequence = 0
local started=false
local resolveLiveManager
local live={version=1,active=false,paused=false}
function M.liveSnapshot()return live end
local function finite(n)return type(n)=='number' and n==n and math.abs(n)<1e12 end
-- Constant key lists: the live poll runs ten times per second on the game thread.
local STATE_KEYS={'score','shots','hits','kills','damage'}
local OUTPUT_KEYS={'score','seconds','shots','hits','kills','damage','remainingSeconds','lastTimeToKillSeconds'}
-- The worker treats live-overlay.json older than two seconds as stale. Rewrite
-- unchanged content at most once per wall-clock second instead of every poll.
local lastPublished,lastPublishedAt
local function pollLive()
    local next={version=1,active=false,paused=false}
    local ok=pcall(function()
        local replay=io.open((os.getenv('LOCALAPPDATA')or'')..'/AimMod/KovaaksNative/replay-frame.tsv','rb')
        if replay then local header=replay:read('*l');replay:close();if header and header:match('^AIMMOD_REPLAY_%d+\t%d+\t1$')then return end end
        if not state.active or not state.clock or not state.clockContext or not state.clockContext:IsValid() then return end
        next.id=state.id;next.scenario=state.scenario
        next.paused=state.clock:IsGamePaused(state.clockContext)
        if not next.paused then next.active=true;next.transient=true end
        local manager=state.manager
        if not manager or not manager:IsValid()then return end
        local scenario=manager:GetCurrentScenario()
        if not scenario:IsValid() then return end
        if not manager:IsInChallenge() or not scenario:IsActive() or not scenario:IsInChallenge() or manager:IsScenarioLoading() or manager:GetChallengeQueueTimeRemaining()>0 then next.active=false;next.transient=nil;return end
        next.active=true;next.transient=nil;next.scenario=state.scenario;next.paused=state.clock:IsGamePaused(state.clockContext)
        -- The game can expose a live score through its pull-only receiver path.
        -- Publish it to the shared observer state so recordings and both HUDs
        -- consume the same measured value without invoking score calculation.
        local pullOk,pulled=pcall(function()return LiveScore.read(state.clock:GetGameInstance(state.clockContext))end)
        if pullOk then next.scoreStatus=pulled.scoreStatus end
        if state.scorePulled and (not pullOk or not finite(pulled.score)) then
            state.score=nil;state.scorePulled=nil;state.revision=state.revision+1
        end
        if pullOk and finite(pulled.score) then
            state.scorePulled=true
        end
        if pullOk and finite(pulled.score) and state.score~=pulled.score then
            state.score=pulled.score;state.revision=state.revision+1
        end
        for _,key in ipairs(STATE_KEYS)do if finite(state[key])then next[key]=state[key]end end
        local elapsed=manager:GetChallengeTimeElapsed()
        if finite(elapsed) and elapsed>=0 then next.seconds=elapsed end
        local remainingOk,remaining=pcall(function()return manager:GetChallengeTimeRemaining()end)
        if remainingOk and finite(remaining) and remaining>=0 then next.remainingSeconds=remaining end
        pcall(function()
            local character=state.clockContext.MyCharacter
            local shots,hits,count,seen=0,0,0,{}
            local function weapon(w)
                if not pcall(function()return w:IsValid()end)then w=w:get()end
                if not w or not w:IsValid()then return end
                local id=w:GetAddress();if seen[id]then return end;seen[id]=true;count=count+1;assert(count<=32)
                local f,h=w.ShotsFiredThisSession,w.ShotsHitThisSession
                assert(finite(f)and finite(h)and f>=0 and h>=0);shots=shots+f;hits=hits+h
            end
            local weapons=character.WeaponHandler:GetWeapons()
            if type(weapons)=='table' and weapons.ForEach==nil then for _,w in ipairs(weapons)do weapon(w)end else weapons:ForEach(function(_,w)weapon(w)end)end
            if count>0 then next.shots=shots;next.hits=hits end
            local kills,damage=character:GetKillCount(),character.DamageDone
            if next.kills==nil and finite(kills)then next.kills=kills end
            if next.damage==nil and finite(damage)then next.damage=damage end
            if finite(kills)and kills>0 then
                local mathLibrary=StaticFindObject('/Script/Engine.Default__KismetMathLibrary')
                local ttk=mathLibrary:GetTotalSeconds(character:GetLastTTK())
                if finite(ttk)and ttk>0 and next.seconds and ttk<=next.seconds then next.lastTimeToKillSeconds=ttk end
            end
        end)
    end)
    live=(ok or next.transient==true) and next or {version=1,active=false,paused=false}
    local fields={'"version":1','"active":'..tostring(live.active),'"paused":'..tostring(live.paused)}
    if type(live.scoreStatus)=='string' and live.scoreStatus:match('^[a-z%-]+$') then fields[#fields+1]='"scoreStatus":"'..live.scoreStatus..'"' end
    if live.id and live.id:match('^[%w%-]+$') then fields[#fields+1]='"id":"'..live.id..'"' end
    if live.transient then fields[#fields+1]='"transient":true' end
    if live.scenario then fields[#fields+1]='"scenario":"'..live.scenario:gsub('[%z\1-\31\\"]',function(c)return string.format('\\u%04x',string.byte(c))end)..'"'end
    for _,key in ipairs(OUTPUT_KEYS)do if finite(live[key])then fields[#fields+1]='"'..key..'":'..string.format('%.9g',live[key])end end
    local body='{'..table.concat(fields,',')..'}'
    local second=os.time()
    if body==lastPublished and second==lastPublishedAt then return end
    local path=(os.getenv('LOCALAPPDATA')or'')..'/AimMod/KovaaksNative/live-overlay.json'
    local file=io.open(path..'.next','wb')
    if file then
        local written=file:write(body);file:close()
        if written then os.remove(path);if os.rename(path..'.next',path) then lastPublished=body;lastPublishedAt=second end end
    end
end
local sessionNonce=tostring(os.time()) .. '-' .. tostring(math.floor(os.clock()*1000000)) .. '-' .. tostring({}):gsub('[^%w]','')
local function escape(value)
    return tostring(value or ''):gsub('%%','%%25'):gsub('\t','%%09'):gsub('\r','%%0D'):gsub('\n','%%0A')
end
local unavailable={}
-- Each observer registers independently: a native event missing from another
-- game build must not prevent completion or metric observers from registering.
local function hook(path, callback)
    local found, f = pcall(StaticFindObject, path)
    if not found or not f or not f:IsValid() then unavailable[#unavailable+1]=path; return false end
    local failures=0
    local registered, reason = pcall(RegisterHook, path, function() end, function(...)
        local ok, err = pcall(callback, ...)
        if not ok then
            -- Bounded logging: a signature mismatch can fail on every native call.
            failures=failures+1
            if failures<=3 then print('[AimModTelemetry] ' .. tostring(err) .. '\n')
            elseif failures==4 then print('[AimModTelemetry] further errors suppressed for ' .. path .. '\n') end
        end
        -- Nil preserves the original return value and parameters.
    end)
    if not registered then
        unavailable[#unavailable+1]=path
        print('[AimModTelemetry] observer unavailable: ' .. path .. ': ' .. tostring(reason) .. '\n')
        return false
    end
    return true
end
-- Lifecycle sources, in preference order. KovaaK's 3.9.11 removed
-- AnalyticsManager; its challenge lifecycle is broadcast by the framework's
-- ScenarioBroadcastReceiver. Every available candidate is observed (post
-- hooks, read only) and handlers are idempotent, so overlapping notifications
-- from both generations collapse into one attempt.
local ANALYTICS='/Script/GameSkillsTrainer.AnalyticsManager:'
local BROADCAST='/Script/KovaaKFramework.ScenarioBroadcastReceiver:'
local lifecycle={start={},complete={},cancel={}}
local listeners={}
-- Completion listeners (replay capture) receive {scenario,id,score,source}.
function M.onCompleted(fn) listeners[#listeners+1]=fn end
local function text(value) if type(value)=='string' then return value end;return value:ToString() end
-- Read-only native context, cached and revalidated before every use.
local native={}
local function nativeManager()
    if native.manager and native.manager:IsValid() and native.player and native.player:IsValid() then return native.manager end
    native={}
    local helpers=StaticFindObject('/Script/Engine.Default__GameplayStatics')
    if not helpers or not helpers:IsValid() then return end
    for _,player in ipairs(FindAllOf('MetaPlayerController') or {}) do
        if player:IsValid() and not player:GetFullName():find('Default__',1,true) then
            local instance=helpers:GetGameInstance(player)
            for _,manager in ipairs(FindAllOf('ScenarioManager') or {}) do
                if manager:IsValid() and manager:GetOuter():GetAddress()==instance:GetAddress() then
                    native={player=player,helpers=helpers,manager=manager};return manager
                end
            end
        end
    end
end
-- Current scenario name as the capture gate reads it (Scenario:GetName).
local function nativeScenario()
    local manager=nativeManager();if not manager then return end
    local scenario=manager:GetCurrentScenario()
    if not scenario or not scenario:IsValid() then return end
    local name=text(scenario:GetName())
    if type(name)=='string' and #name>0 and #name<=512 then return name,manager,scenario end
end
local function beginIntent(source, name)
    assert(type(name)=='string' and #name>0,'scenario name unavailable')
    if state.active and state.scenario==name then
        state.lastStartEvent=source
        state.startNotifications=(state.startNotifications or 1)+1
        return
    end
    sequence = sequence + 1
    state = { active=true, scenario=name,startNotifications=1,
        id=sessionNonce .. '-' .. tostring(sequence), revision=state.revision+1, startEvent=source }
    -- Current builds do not always emit Send_Seconds. Measure Unreal game time
    -- between the real start/completion events; paused time is excluded.
    local helpers=StaticFindObject('/Script/Engine.Default__GameplayStatics')
    for _,player in ipairs(FindAllOf('MetaPlayerController') or {}) do
        if player:IsValid() and not player:GetFullName():find('Default__',1,true) then
            local ok,stamp=pcall(function() return helpers:GetTimeSeconds(player) end)
            if ok and type(stamp)=='number' then state.clockContext=player; state.clock=helpers; state.startedAt=stamp; break end
        end
    end
    pcall(resolveLiveManager)
end
local function quit()
    -- A completion in progress owns the attempt until it is finalized.
    if state.completing then return end
    state.active=false
end
local function complete(name, finalScore, source)
    if not state.active or name~=state.scenario then return end
    state.active=false;state.completing=nil
    local score=finite(finalScore) and finalScore or nil
    local event={scenario=name,id=state.id,score=score,source=source}
    for _,listener in ipairs(listeners) do
        local ok,err=pcall(listener,event)
        if not ok then print('[AimModTelemetry] completion listener failed: '..tostring(err)..'\n') end
    end
    if not score then print('[AimModTelemetry] completed run not saved: final score unavailable ('..tostring(source)..')\n');return end
    state.score=score
    local accuracy = state.shots and state.shots>0 and state.hits and state.hits>=0 and state.hits<=state.shots and state.hits/state.shots*100 or ''
    local duration=state.seconds
    if not duration and state.startedAt and state.clock and state.clockContext and state.clockContext:IsValid() then
        local ok,stamp=pcall(function() return state.clock:GetTimeSeconds(state.clockContext) end)
        if ok and type(stamp)=='number' and stamp>state.startedAt then duration=stamp-state.startedAt end
    end
    local values={'run',state.id,name,score,accuracy,duration or '',state.kills or '',state.damage or '',os.date('!%Y-%m-%dT%H:%M:%SZ')}
    for i,v in ipairs(values) do values[i]=escape(v) end
    local line=table.concat(values,'\t') .. '\n'
    -- One small append after completion, outside the scoring callback. The
    -- native worker owns directory creation, parsing, queries, and analysis.
    ExecuteInGameThreadWithDelay(250, function()
        local path=(os.getenv('LOCALAPPDATA') or '') .. '/AimMod/KovaaksNative/completed.tsv'
        local file, reason=io.open(path,'ab')
        if not file then print('[AimModTelemetry] Could not save run: '..tostring(reason)..'\n'); return end
        file:write(line); file:close()
        print('[AimModTelemetry] completed run saved; score source='..tostring(source)..'\n')
    end)
end
-- Broadcast completion carries no parameters. Hold the attempt briefly and
-- take the game's own final value: a complete score broadcast observed during
-- completion, else the indicator value read when completion was broadcast,
-- else the last indicator value observed during the run. Never computed here.
local COMPLETION_POLLS=5
local function finalize(source)
    local c=state.completing
    if not c or not state.active then return end
    if finite(c.broadcast) then return complete(state.scenario,c.broadcast,source..':score-broadcast') end
    if finite(c.pulled) then return complete(state.scenario,c.pulled,source..':indicator') end
    return complete(state.scenario,state.score,source..':last-indicator')
end
local function broadcastComplete()
    if not state.active or state.completing then return end
    local ok,name=pcall(nativeScenario)
    if ok and name and name~=state.scenario then return end
    local pulled
    pcall(function()
        local value=LiveScore.read(state.clock:GetGameInstance(state.clockContext))
        if finite(value.score) then pulled=value.score end
    end)
    state.completing={polls=0,pulled=pulled}
end
local function inChallenge()
    local name,manager,scenario=nativeScenario()
    if not name then return nil end
    return manager:IsInChallenge()==true or scenario:IsInChallenge()==true,name
end
local function broadcastStart(source)
    return function()
        local challenge,name=inChallenge()
        -- Freeplay also starts scenarios; only challenges are attempts.
        if challenge then beginIntent(source,name) end
    end
end
local function broadcastTransition()
    if not state.active or state.completing then return end
    local challenge=inChallenge()
    if challenge==false then quit() end
end
-- Fallback when this build exposes no start notification: an advancing
-- native challenge timer opens an intent. It re-arms only after the challenge
-- is observed not running, so a finished run never reopens itself.
local fallback={armed=true,idle=0}
local function nativeFallback()
    local name,manager,scenario=nativeScenario();if not name then return end
    local running=manager:IsInChallenge() and scenario:IsActive() and scenario:IsInChallenge()
        and not manager:IsScenarioLoading() and manager:GetChallengeQueueTimeRemaining()<=0
    local elapsed=manager:GetChallengeTimeElapsed()
    if state.active then
        fallback.elapsed=nil
        -- Without a cancel notification, a natively opened intent ends after
        -- two seconds of an unpaused, non-running challenge.
        local paused=native.helpers and native.helpers:IsGamePaused(native.player)
        if state.startEvent=='native' and not running and not paused and not state.completing then
            fallback.idle=fallback.idle+1;if fallback.idle>=20 then fallback.idle=0;quit() end
        else fallback.idle=0 end
        return
    end
    if not running then fallback.armed=true;fallback.elapsed=nil;return end
    if fallback.armed and finite(elapsed) and fallback.elapsed and elapsed>fallback.elapsed then
        fallback.armed=false;fallback.elapsed=nil;beginIntent('native',name);return
    end
    fallback.elapsed=elapsed
end
resolveLiveManager=function()
    if not state.clock or not state.clockContext then return end
    local instance=state.clock:GetGameInstance(state.clockContext)
    for _,manager in ipairs(FindAllOf('ScenarioManager')or{})do
        if manager:IsValid() and manager:GetOuter():GetAddress()==instance:GetAddress()then state.manager=manager;break end
    end
end
-- Called by the read-only capture gate only after a native timer/phase
-- transition is observed. Analytics notifications alone cannot reset history.
function M.confirmAttempt(restarted, elapsed)
    if not state.manager then pcall(resolveLiveManager)end
    if not state.active then return state end
    if restarted then
        sequence=sequence+1
        state={active=true,scenario=state.scenario,id=sessionNonce..'-'..sequence,
            revision=state.revision+1,startEvent=state.lastStartEvent or 'restarted',
            startNotifications=1,clock=state.clock,clockContext=state.clockContext,manager=state.manager}
    end
    if state.clock and state.clockContext and state.clockContext:IsValid() then
        local ok,stamp=pcall(function()return state.clock:GetTimeSeconds(state.clockContext)end)
        if ok and type(stamp)=='number' and type(elapsed)=='number' and elapsed>=0 then state.startedAt=stamp-elapsed end
    end
    return state
end
function M.state() return state end
local function short(path) return path:match('([^.]+:[^:]+)$') or path end
local function observe(kind, path, callback)
    if hook(path, callback) then lifecycle[kind][#lifecycle[kind]+1]=short(path) end
end
function M.start()
    if started then return end
    started=true
    pcall(function()require('LiveOpponents').start()end)
    -- Legacy (<= 3.9.8) analytics routes. Restart and result-screen replay do
    -- not necessarily emit OnChallengeStarted too. The source is kept privately.
    observe('start', ANALYTICS .. 'OnChallengeStarted', function(_,scenario)beginIntent('started',text(scenario:get()))end)
    observe('start', ANALYTICS .. 'OnChallengeRestarted', function(_,scenario)beginIntent('restarted',text(scenario:get()))end)
    observe('start', ANALYTICS .. 'OnChallengeReplayed', function(_,scenario)beginIntent('replayed',text(scenario:get()))end)
    observe('cancel', ANALYTICS .. 'OnChallengeQuit', function() quit() end)
    observe('complete', ANALYTICS .. 'OnChallengeCompleted', function(_, scenario, score)
        local name=text(scenario:get())
        if not state.active or name~=state.scenario then return end
        complete(name, score:get(), 'analytics')
    end)
    -- Framework broadcasts (3.9.11+). Parameters are not read.
    observe('start', BROADCAST .. 'Send_ChallengeQueued', broadcastStart('queued'))
    observe('start', BROADCAST .. 'Send_Start', broadcastStart('broadcast-start'))
    observe('start', BROADCAST .. 'Send_PostStart', broadcastStart('broadcast-start'))
    observe('complete', BROADCAST .. 'Send_ChallengeComplete', broadcastComplete)
    observe('complete', BROADCAST .. 'Send_PostChallengeComplete', function()
        if not state.completing then broadcastComplete() end
        finalize('broadcast')
    end)
    observe('cancel', BROADCAST .. 'Send_ChallengeCanceled', function() quit() end)
    observe('cancel', BROADCAST .. 'Send_ScenarioChanged', broadcastTransition)
    observe('cancel', BROADCAST .. 'Send_PlayTypeChanged', broadcastTransition)
    local useFallback=#lifecycle.start==0
    if type(LoopInGameThreadWithDelay)=='function'then LoopInGameThreadWithDelay(100,function()
        local c=state.completing
        if c then c.polls=c.polls+1;if c.polls>=COMPLETION_POLLS then pcall(finalize,'broadcast-timeout') end end
        if useFallback then pcall(nativeFallback) end
        local ok=pcall(pollLive);if not ok then live={version=1,active=false,paused=false}end
    end)end
    local metrics = {Send_Score='score',Send_ShotsFired='shots',Send_ShotsHit='hits',Send_Seconds='seconds',Send_Kills='kills',Send_DamageDone='damage'}
    for event, key in pairs(metrics) do
        local target=key
        hook('/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:' .. event, function(_, value)
            if not state.active then return end
            local n=value:get()
            if type(n)=='number' and n==n and math.abs(n)<1e12 and state[target]~=n then
                if target=='hits' and state.hits~=nil and n>state.hits and state.clock and state.clockContext then
                    local ok,stamp=pcall(function()return state.clock:GetTimeSeconds(state.clockContext)end)
                    if ok and type(stamp)=='number' then state.hitStamp=stamp;state.hitDelta=n-state.hits end
                end
                if target=='score' then state.scorePulled=nil end
                state[target]=n; state.revision=state.revision+1
            end
        end)
    end
    -- Observe the current framework's complete score broadcast, without
    -- invoking CalculateScore or writing back to any native game state.
    -- ChallengeName fences unrelated and stale scenario broadcasts.
    local function observeScore(score)
        if not state.active then return end
        local name=text(score.ChallengeName)
        if name~=state.scenario then return end
        for native,key in pairs({Score='score',ShotsHit='hits',ShotsFired='shots',KillCount='kills',DamageDone='damage'}) do
            local n=score[native]
            if type(n)=='number' and n==n and math.abs(n)<1e12 and state[key]~=n then
                if key=='score' then state.scorePulled=nil end
                state[key]=n;state.revision=state.revision+1
            end
        end
        if state.completing and finite(score.Score) then state.completing.broadcast=score.Score end
    end
    hook('/Script/KovaaKFramework.ScenarioStateReceiver:Send_ChallengeScore',function(_,value)
        observeScore(value:get())
    end)
    -- UE4SS native post callbacks receive the original return value second,
    -- before regular parameters. Observe computations the game already makes;
    -- do not call this function or override any input/return value.
    hook('/Script/GameSkillsTrainer.StatsManager:CalculateScore',function(_,result,worldContext)
        if not state.active or not state.clock or not state.clockContext then return end
        local expected=state.clock:GetGameState(state.clockContext)
        local actual=state.clock:GetGameState(worldContext:get())
        if not expected or not actual or not expected:IsValid() or not actual:IsValid() or expected:GetAddress()~=actual:GetAddress() then return end
        observeScore(result:get())
    end)
    local function list(kind) return #lifecycle[kind]>0 and table.concat(lifecycle[kind],',') or 'none' end
    print('[AimModTelemetry] lifecycle source: start='..(useFallback and 'native-timer' or list('start'))..'; complete='..list('complete')..'; cancel='..list('cancel')..'\n')
    if #lifecycle.complete==0 then print('[AimModTelemetry] no completion notification in this build: completed runs and replays are not saved\n') end
    if #unavailable>0 then print('[AimModTelemetry] native events unavailable in this build: ' .. table.concat(unavailable, ', ') .. '\n') end
    -- Start alongside telemetry so UE4SS reloads that retain the original main
    -- chunk still pick up recording. ReplayCapture.start is idempotent.
    local replayOk, replayError=pcall(function() require('ReplayCapture').start(M) end)
    if not replayOk then print('[AimModReplay] start failed: '..tostring(replayError)..'\n') end
end
return M
