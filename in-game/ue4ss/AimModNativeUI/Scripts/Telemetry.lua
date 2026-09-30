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
local function start(source, scenario)
    local name=scenario:get():ToString()
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
function M.start()
    if started then return end
    started=true
    pcall(function()require('LiveOpponents').start()end)
    if type(LoopInGameThreadWithDelay)=='function'then LoopInGameThreadWithDelay(100,function()local ok=pcall(pollLive);if not ok then live={version=1,active=false,paused=false}end end)end
    local analytics = '/Script/GameSkillsTrainer.AnalyticsManager:'
    -- All three are independent UI routes in the current game. Restart and
    -- result-screen replay do not necessarily emit OnChallengeStarted too.
    -- Preserve the source privately so overlapping notifications can be
    -- investigated without guessing from attempt duration or scenario names.
    hook(analytics .. 'OnChallengeStarted', function(_,scenario)start('started',scenario)end)
    hook(analytics .. 'OnChallengeRestarted', function(_,scenario)start('restarted',scenario)end)
    hook(analytics .. 'OnChallengeReplayed', function(_,scenario)start('replayed',scenario)end)
    hook(analytics .. 'OnChallengeQuit', function() state.active=false end)
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
        local name=score.ChallengeName
        if type(name)~='string' then name=name:ToString() end
        if name~=state.scenario then return end
        for native,key in pairs({Score='score',ShotsHit='hits',ShotsFired='shots',KillCount='kills',DamageDone='damage'}) do
            local n=score[native]
            if type(n)=='number' and n==n and math.abs(n)<1e12 and state[key]~=n then
                if key=='score' then state.scorePulled=nil end
                state[key]=n;state.revision=state.revision+1
            end
        end
    end
    local scorePath='/Script/KovaaKFramework.ScenarioStateReceiver:Send_ChallengeScore'
    local scoreFunction=StaticFindObject(scorePath)
    if scoreFunction and scoreFunction:IsValid() then
        hook(scorePath,function(_,value)
            observeScore(value:get())
        end)
    end
    -- UE4SS native post callbacks receive the original return value second,
    -- before regular parameters. Observe computations the game already makes;
    -- do not call this function or override any input/return value.
    local calculationPath='/Script/GameSkillsTrainer.StatsManager:CalculateScore'
    local calculationFunction=StaticFindObject(calculationPath)
    if calculationFunction and calculationFunction:IsValid() then
        hook(calculationPath,function(_,result,worldContext)
            if not state.active or not state.clock or not state.clockContext then return end
            local expected=state.clock:GetGameState(state.clockContext)
            local actual=state.clock:GetGameState(worldContext:get())
            if not expected or not actual or not expected:IsValid() or not actual:IsValid() or expected:GetAddress()~=actual:GetAddress() then return end
            observeScore(result:get())
        end)
    end
    hook(analytics .. 'OnChallengeCompleted', function(_, scenario, score)
        if not state.active then return end
        local name=scenario:get():ToString()
        if name~=state.scenario then return end
        state.active=false
        local finalScore=score:get()
        if type(finalScore)~='number' or finalScore~=finalScore or math.abs(finalScore)>=1e12 then return end
        state.score=finalScore
        local accuracy = state.shots and state.shots>0 and state.hits and state.hits>=0 and state.hits<=state.shots and state.hits/state.shots*100 or ''
        local duration=state.seconds
        if not duration and state.startedAt and state.clock and state.clockContext and state.clockContext:IsValid() then
            local ok,stamp=pcall(function() return state.clock:GetTimeSeconds(state.clockContext) end)
            if ok and type(stamp)=='number' and stamp>state.startedAt then duration=stamp-state.startedAt end
        end
        local values={'run',state.id,name,finalScore,accuracy,duration or '',state.kills or '',state.damage or '',os.date('!%Y-%m-%dT%H:%M:%SZ')}
        for i,v in ipairs(values) do values[i]=escape(v) end
        local line=table.concat(values,'\t') .. '\n'
        -- One small append after completion, outside the scoring callback. The
        -- native worker owns directory creation, parsing, queries, and analysis.
        ExecuteInGameThreadWithDelay(250, function()
            local path=(os.getenv('LOCALAPPDATA') or '') .. '/AimMod/KovaaksNative/completed.tsv'
            local file, reason=io.open(path,'ab')
            if not file then print('[AimModTelemetry] Could not save run: '..tostring(reason)..'\n'); return end
            file:write(line); file:close()
            print('[AimModTelemetry] completed run saved\n')
        end)
    end)
    if #unavailable>0 then print('[AimModTelemetry] native events unavailable in this build: ' .. table.concat(unavailable, ', ') .. '\n') end
    -- Start alongside telemetry so UE4SS reloads that retain the original main
    -- chunk still pick up recording. ReplayCapture.start is idempotent.
    local replayOk, replayError=pcall(function() require('ReplayCapture').start(M) end)
    if not replayOk then print('[AimModReplay] start failed: '..tostring(replayError)..'\n') end
end
return M

