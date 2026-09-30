-- Lua 5.3+. Synthetic build without some native events; no game functions run.
package.preload.LiveScore=function()return {read=function()return {}end}end
package.preload.ReplayCapture=function()return {start=function()end}end
package.preload.LiveOpponents=function()return {start=function()end}end
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
local callbacks,delayed,printed,writes={},{},{},0
local loop
local now=100
local function valid(t)t.IsValid=function()return true end;return t end
local missing={['/Script/GameSkillsTrainer.AnalyticsManager:OnChallengeReplayed']=true,
    ['/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Send_Seconds']=true}
local helpers=valid({GetTimeSeconds=function()return 1 end,IsGamePaused=function()return false end})
StaticFindObject=function(path)
    if missing[path] then return {IsValid=function()return false end} end
    if path:find('GameplayStatics',1,true)then return helpers end
    return valid({})
end
FindAllOf=function()return {}end
RegisterHook=function(path,_,post)callbacks[path]=post end
LoopInGameThreadWithDelay=function(ms,fn)if ms==100 then loop=fn end end
ExecuteInGameThreadWithDelay=function(_,fn)delayed[#delayed+1]=fn end
local realPrint=print
print=function(s)printed[#printed+1]=tostring(s)end
os.getenv=function()return 'synthetic-root'end
os.time=function()return now end
os.remove=function()return true end
os.rename=function()return true end
io.open=function(_,mode)
    if mode=='rb' then return nil end
    return {write=function()writes=writes+1;return true end,close=function()end}
end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/Telemetry.lua');M.start()
local analytics='/Script/GameSkillsTrainer.AnalyticsManager:'
check(callbacks[analytics..'OnChallengeReplayed']==nil,'missing native event is not hooked')
check(callbacks[analytics..'OnChallengeCompleted']~=nil and callbacks[analytics..'OnChallengeQuit']~=nil,'observers after a missing event still register')
check(callbacks['/Script/KovaaKFramework.PerformanceIndicatorsStateReceiver:Send_Score']~=nil,'metric observers register independently')
local reported=false
for _,line in ipairs(printed)do if line:find('OnChallengeReplayed',1,true) and line:find('unavailable',1,true)then reported=true end end
check(reported,'unavailable native events are reported once')
-- A callback that fails on every native call logs a bounded number of lines.
local before=#printed
for _=1,50 do callbacks[analytics..'OnChallengeStarted']({},nil) end
check(#printed-before==4,'repeated observer failures are rate-limited: '..(#printed-before))
check(callbacks[analytics..'OnChallengeStarted']({},nil)==nil,'failing observer never overrides the native result')
-- Unchanged idle snapshots are rewritten at most once per wall-clock second.
writes=0
for _=1,10 do loop() end
check(writes==1,'unchanged live snapshot written once per second: '..writes)
now=101;loop();check(writes==2,'heartbeat rewrite keeps worker freshness')
print=realPrint
print('PASS '..checks..' telemetry compatibility checks')
