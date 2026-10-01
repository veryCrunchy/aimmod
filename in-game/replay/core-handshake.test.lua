-- Lua 5.3+. AimModCore handshake: a fresh native heartbeat takes over the
-- journal, live file and replay capture; a stale one hands them back.
package.preload.LiveScore=function()return {read=function()return {score=50}end}end
package.preload.LiveOpponents=function()return {start=function()end}end
local callbacks,files,loop,delayed={},{},nil,{}
local now=1000
local function obj(t)t=t or{};t.IsValid=function()return true end;return t end
local instance=obj({GetAddress=function()return 1 end})
local scenario=obj({IsActive=function()return true end,IsInChallenge=function()return true end,GetName=function()return 'Synthetic' end})
local manager=obj({GetOuter=function()return instance end,GetCurrentScenario=function()return scenario end,IsInChallenge=function()return true end,IsScenarioLoading=function()return false end,GetChallengeQueueTimeRemaining=function()return 0 end,GetChallengeTimeElapsed=function()return 5 end,GetChallengeTimeRemaining=function()return 55 end})
local player=obj({GetFullName=function()return 'Synthetic' end})
local helpers=obj({GetTimeSeconds=function()return 100 end,GetGameInstance=function()return instance end,IsGamePaused=function()return false end})
StaticFindObject=function(path)return path:find('GameplayStatics',1,true)and helpers or obj()end
FindAllOf=function(name)return name=='ScenarioManager' and{manager}or{player}end
RegisterHook=function(path,_,post)callbacks[path]=post end
LoopInGameThreadWithDelay=function(ms,fn)if ms==100 then loop=fn end end
ExecuteInGameThreadWithDelay=function(_,fn)delayed[#delayed+1]=fn end
os.getenv=function()return 'root'end
os.time=function()return now end
os.remove=function(p)files[p]=nil end;os.rename=function(a,b)files[b]=files[a];files[a]=nil;return true end
io.open=function(p,mode)
    if mode=='rb' and not files[p]then return nil end
    return{read=function()return files[p]end,write=function(_,v)files[p]=(mode=='ab' and (files[p] or '') or '')..v;return true end,close=function()end}
end
package.preload.ReplayCapture=function()return{start=function()end}end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/Telemetry.lua');M.start()
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
local base='root/AimMod/KovaaksNative/'
check(not M.coreActive('telemetry'),'no heartbeat: Lua owns telemetry')
files[base..'core-active.tsv']='AIMMOD_CORE_1\t0.1.0\t'..now..'\ttelemetry,replay'
now=now+1
check(M.coreActive('telemetry') and M.coreActive('replay') and not M.coreActive('tele'),'fresh heartbeat grants listed capabilities only')
files[base..'live-overlay.json']='{"version":1,"active":true,"paused":false,"id":"1790000000-7-1","scenario":"Synthetic","score":12}'
files[base..'live-overlay.json.next']=nil
loop()
local snap=M.liveSnapshot()
check(snap.active==true and snap.paused==false and snap.id=='1790000000-7-1','HUD mirrors the native live snapshot')
check(files[base..'live-overlay.json']:find('"score":12',1,true),'Lua does not rewrite the native live file')
-- Lifecycle notifications are ignored while the native mod owns telemetry.
callbacks['/Script/GameSkillsTrainer.AnalyticsManager:OnChallengeStarted']({}, {get=function()return{ToString=function()return'Synthetic'end}end})
check(not M.state().active,'no Lua attempt while the native mod is active')
callbacks['/Script/GameSkillsTrainer.AnalyticsManager:OnChallengeCompleted']({}, {get=function()return{ToString=function()return'Synthetic'end}end},{get=function()return 99 end})
for _,fn in ipairs(delayed)do fn()end
check(files[base..'completed.tsv']==nil,'no duplicate journal line')
-- Stale heartbeat: Lua resumes on its own.
now=now+10
check(not M.coreActive('telemetry'),'stale heartbeat hands telemetry back')
callbacks['/Script/GameSkillsTrainer.AnalyticsManager:OnChallengeStarted']({}, {get=function()return{ToString=function()return'Synthetic'end}end})
check(M.state().active,'Lua attempts resume')
files[base..'core-active.tsv']='AIMMOD_CORE_1\t0.1.0\t'..now..'\treplay'
now=now+1
check(M.coreActive('replay') and not M.coreActive('telemetry'),'capabilities are independent')
files[base..'core-active.tsv']='garbage'
now=now+1
check(not M.coreActive('replay'),'malformed heartbeat is ignored')
print('PASS '..checks..' native core handshake checks')
