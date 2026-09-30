-- Lua 5.3+. KovaaK's 3.9.11-style build: AnalyticsManager is gone and the
-- challenge lifecycle is broadcast without parameters. Synthetic objects only.
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
local realPrint=print
local function load(options)
    local env={callbacks={},delayed={},lines={},printed={},listeners={},loop=nil}
    local pulled=options.pulled or {}
    for _,name in ipairs({'Telemetry','LiveScore','ReplayCapture','LiveOpponents'})do package.loaded[name]=nil end
    package.preload.LiveScore=function()return {read=function()return {score=pulled.score}end}end
    package.preload.ReplayCapture=function()return {start=function()end}end
    package.preload.LiveOpponents=function()return {start=function()end}end
    local function obj(t)t=t or {};t.IsValid=function()return true end;return t end
    local native=options.native
    local instance=obj({GetAddress=function()return 7 end})
    local scenario=obj({GetName=function()return native.name end,IsActive=function()return native.running end,IsInChallenge=function()return native.challenge end})
    local manager=obj({GetOuter=function()return instance end,GetCurrentScenario=function()return scenario end,IsInChallenge=function()return native.challenge end,
        IsScenarioLoading=function()return false end,GetChallengeQueueTimeRemaining=function()return 0 end,GetChallengeTimeElapsed=function()return native.elapsed end})
    local player=obj({GetFullName=function()return 'SyntheticController' end})
    local helpers=obj({GetTimeSeconds=function()return native.clock end,GetGameInstance=function()return instance end,IsGamePaused=function()return false end,GetGameState=function()return obj({GetAddress=function()return 1 end})end})
    StaticFindObject=function(path)
        if path:find('GameplayStatics',1,true)then return helpers end
        if path:find('AnalyticsManager',1,true) or (options.missing and options.missing(path)) then return {IsValid=function()return false end} end
        return obj()
    end
    FindAllOf=function(name)return name=='ScenarioManager' and {manager} or {player}end
    RegisterHook=function(path,_,post)env.callbacks[path]=post end
    LoopInGameThreadWithDelay=function(ms,fn)if ms==100 then env.loop=fn end end
    ExecuteInGameThreadWithDelay=function(_,fn)env.delayed[#env.delayed+1]=fn end
    print=function(s)env.printed[#env.printed+1]=tostring(s)end
    os.getenv=function()return 'synthetic-root'end
    os.remove=function()return true end;os.rename=function()return true end
    io.open=function(path,mode)
        if mode=='rb' then return nil end
        return {write=function(_,line)if path:find('completed.tsv',1,true)then env.lines[#env.lines+1]=line end;return true end,close=function()end}
    end
    env.M=dofile('../ue4ss/AimModNativeUI/Scripts/Telemetry.lua')
    env.M.onCompleted(function(event)env.listeners[#env.listeners+1]=event end)
    env.M.start()
    print=realPrint
    env.fire=function(name,...)local cb=env.callbacks['/Script/KovaaKFramework.ScenarioBroadcastReceiver:'..name];assert(cb,name..' not bound');return cb({},...)end
    env.flush=function()for _,fn in ipairs(env.delayed)do fn()end;env.delayed={}end
    return env
end
local native={name='Synthetic broadcast scenario',challenge=false,running=false,elapsed=0,clock=10}
local pulled={}
local t=load({native=native,pulled=pulled})
local summary
for _,line in ipairs(t.printed)do if line:find('lifecycle source',1,true)then summary=line end end
check(summary and summary:find('Send_ChallengeQueued',1,true) and summary:find('Send_ChallengeComplete',1,true),'bound lifecycle source is logged once')
check(not summary:find('native-timer',1,true),'broadcast start source disables the timer fallback')
-- Freeplay start is not an attempt.
t.fire('Send_Start');check(not t.M.state().active,'freeplay start does not open an attempt')
native.challenge=true;t.fire('Send_ChallengeQueued')
local first=t.M.state().id
check(t.M.state().active and t.M.state().scenario==native.name,'queued challenge opens an intent named by the native scenario')
t.fire('Send_Start');t.fire('Send_PostStart')
check(t.M.state().id==first and t.M.state().startNotifications==3,'repeated broadcasts do not split the attempt')
check(t.fire('Send_Start')==nil,'broadcast observers never override the native result')
-- Completion waits for the game's complete score broadcast.
t.fire('Send_ChallengeComplete')
check(t.M.state().active and #t.listeners==0,'completion is held until the final value is observed')
t.callbacks['/Script/KovaaKFramework.ScenarioStateReceiver:Send_ChallengeScore']({}, {get=function()return {ChallengeName=native.name,Score=77}end})
t.fire('Send_PostChallengeComplete');t.flush()
check(not t.M.state().active and #t.listeners==1 and t.listeners[1].score==77 and t.listeners[1].id==first,'post-complete publishes the observed final score to listeners')
check(#t.lines==1 and t.lines[1]:find('\t77\t',1,true),'completed run journaled with the observed score')
t.fire('Send_PostChallengeComplete');t.fire('Send_ChallengeComplete');t.flush()
check(#t.lines==1 and #t.listeners==1,'duplicate completion broadcasts are ignored')
-- Without a score broadcast, the indicator value read at completion is used.
t.fire('Send_ChallengeQueued');local second=t.M.state().id
check(second~=first,'next challenge receives a new attempt id')
pulled.score=55;t.fire('Send_ChallengeComplete');pulled.score=0
t.fire('Send_ChallengeCanceled');check(t.M.state().active,'cancel cannot discard a completion in progress')
for _=1,5 do t.loop() end;t.flush()
check(#t.lines==2 and t.lines[2]:find('\t55\t',1,true),'indicator value captured at completion is journaled after a bounded wait')
-- Cancel ends an ordinary attempt without history.
t.fire('Send_ChallengeQueued');t.fire('Send_ChallengeCanceled')
check(not t.M.state().active and #t.lines==2,'canceled attempt is not journaled')
-- A scenario/play-type change ends an attempt only when the native challenge ended.
t.fire('Send_ChallengeQueued');t.fire('Send_ScenarioChanged')
check(t.M.state().active,'transition during a live challenge keeps the attempt')
native.challenge=false;t.fire('Send_PlayTypeChanged')
check(not t.M.state().active,'transition out of the challenge ends the attempt')

-- Build with no start notification at all: native timer fallback.
native={name='Synthetic fallback scenario',challenge=true,running=true,elapsed=1,clock=10}
local f=load({native=native,pulled={},missing=function(path)return path:find('Send_ChallengeQueued',1,true) or path:find('Send_Start',1,true) or path:find('Send_PostStart',1,true)end})
local fallbackSummary
for _,line in ipairs(f.printed)do if line:find('lifecycle source',1,true)then fallbackSummary=line end end
check(fallbackSummary and fallbackSummary:find('start=native-timer',1,true),'missing start notifications select the native timer fallback')
f.loop();check(not f.M.state().active,'one timer observation is not advancement')
native.elapsed=1.1;f.loop();check(f.M.state().active and f.M.state().startEvent=='native','advancing native timer opens an intent')
f.callbacks['/Script/KovaaKFramework.ScenarioStateReceiver:Send_ChallengeScore']({}, {get=function()return {ChallengeName=native.name,Score=9}end})
f.fire('Send_ChallengeComplete');f.fire('Send_PostChallengeComplete');f.flush()
check(#f.lines==1,'fallback attempt completes through the broadcast')
native.elapsed=1.2;f.loop();native.elapsed=1.3;f.loop()
check(not f.M.state().active,'a finished run never reopens itself while still flagged running')
native.running=false;f.loop();native.running=true;native.elapsed=0;f.loop();native.elapsed=.1;f.loop()
check(f.M.state().active,'fallback re-arms after the challenge is observed not running')
native.running=false;for _=1,20 do f.loop() end
check(not f.M.state().active,'fallback intent ends after two seconds outside a running challenge')
print('PASS '..checks..' broadcast lifecycle checks')
