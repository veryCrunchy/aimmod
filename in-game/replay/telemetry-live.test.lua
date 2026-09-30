local pulledScore=nil
package.preload.LiveScore=function()return {read=function(instance)assert(instance:GetAddress()==1);return {score=pulledScore}end}end
local callbacks,files,loop={},{},nil
local function obj(t)t=t or{};t.IsValid=function()return true end;return t end
local instance=obj({GetAddress=function()return 1 end})
local paused=false;local elapsed=20;local queued=0
local scenario=obj({IsActive=function()return true end,IsInChallenge=function()return true end})
local manager=obj({GetOuter=function()return instance end,GetCurrentScenario=function()return scenario end,IsInChallenge=function()return true end,IsScenarioLoading=function()return false end,GetChallengeQueueTimeRemaining=function()return queued end,GetChallengeTimeElapsed=function()return elapsed end,GetChallengeTimeRemaining=function()return 40 end})
local weapon=obj({GetAddress=function()return 3 end,ShotsFiredThisSession=10,ShotsHitThisSession=5})
local character=obj({WeaponHandler=obj({GetWeapons=function()return{weapon}end}),GetKillCount=function()return 2 end,DamageDone=.3,GetLastTTK=function()return 3 end})
local player=obj({MyCharacter=character,GetFullName=function()return 'Synthetic' end})
local helpers=obj({GetTimeSeconds=function()return 100 end,GetGameInstance=function()return instance end,IsGamePaused=function()return paused end})
StaticFindObject=function(path)if path:find('KismetMathLibrary',1,true)then return{GetTotalSeconds=function(_,v)return v end}end;return path:find('GameplayStatics',1,true)and helpers or obj()end
FindAllOf=function(name)return name=='ScenarioManager' and{manager}or{player}end
RegisterHook=function(path,_,post)callbacks[path]=post end
LoopInGameThreadWithDelay=function(ms,fn)assert(ms==100);loop=fn end
ExecuteInGameThreadWithDelay=function()end
os.getenv=function()return 'synthetic-root'end
os.remove=function(p)files[p]=nil end;os.rename=function(a,b)files[b]=files[a];files[a]=nil end
io.open=function(p,mode)if mode=='rb' and not files[p]then return nil end;return{read=function()return files[p]end,write=function(_,v)files[p]=v;return true end,close=function()end}end
package.preload.ReplayCapture=function()return{start=function()end}end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/Telemetry.lua');M.start()
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
loop();check(not M.liveSnapshot().active,'idle hidden')
callbacks['/Script/GameSkillsTrainer.AnalyticsManager:OnChallengeStarted']({}, {get=function()return{ToString=function()return'Synthetic'end}end})
loop();check(M.liveSnapshot().active and M.liveSnapshot().seconds==20,'native elapsed independent capture')
check(M.liveSnapshot().remainingSeconds==40 and M.liveSnapshot().lastTimeToKillSeconds==3,'native remaining and last kill timing captured');
check(M.liveSnapshot().score==nil and M.liveSnapshot().shots==10 and M.liveSnapshot().damage==.3,'unknown score and actual fractional counters')
pulledScore=125.5;loop();check(M.liveSnapshot().score==125.5 and M.state().score==125.5,'pull-only score feeds live HUD and shared replay metrics without events')
pulledScore=0;loop();check(M.liveSnapshot().score==0 and M.state().score==0,'authoritative zero replaces previous score')
pulledScore=-5;loop();check(M.liveSnapshot().score==-5,'native penalty score is preserved')
pulledScore=nil;loop();check(M.liveSnapshot().score==nil and M.state().score==nil,'unavailable getter cannot freeze a previous pulled score')
pulledScore=-5;loop()
queued=1;pulledScore=999;loop();check(not M.liveSnapshot().active and M.state().score==-5,'challenge countdown excluded from score reads');queued=0
paused=true;loop();check(M.liveSnapshot().paused,'pause explicit')
files['synthetic-root/AimMod/KovaaksNative/replay-frame.tsv']='AIMMOD_REPLAY_3\t1\t1';loop();check(not M.liveSnapshot().active,'replay suppresses nativeHUD');files['synthetic-root/AimMod/KovaaksNative/replay-frame.tsv']=nil
check(files['synthetic-root/AimMod/KovaaksNative/live-overlay.json']:find('"active":false',1,true),'file publishes suppression')
print('PASS '..checks..' live telemetry checks')
