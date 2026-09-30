-- Lua 5.3+. Capture bounds and failure recovery with synthetic objects only.
local completion
local index=0
local function obj(t)index=index+1;local address=index;t=t or {};t.IsValid=function()return true end;t.GetAddress=function()return address end;return t end
local gameTime,elapsed=0,0
local intent={active=false,id='idle',scenario='Synthetic scenario'}
local instance=obj();local character=obj()
character.PlaybackComponent=obj({GetOwner=function()return character end})
local player=obj({MyCharacter=character,GetFullName=function()return 'Synthetic controller' end})
local scenario=obj({GetName=function()return intent.scenario end,IsActive=function()return true end,IsInChallenge=function()return true end})
local manager=obj({GetOuter=function()return instance end,GetCurrentScenario=function()return scenario end,IsInChallenge=function()return true end,
    IsScenarioLoading=function()return false end,GetChallengeQueueTimeRemaining=function()return -1 end,GetChallengeTimeElapsed=function()return elapsed end})
local characters=function()return {} end
local worldValid=true
local world=obj({IsA=function()return true end,GetCurrentMapName=function()return 'Synthetic map' end,GetMapScale=function()return 1 end,GetCharacters=function()return characters() end})
world.IsValid=function()return worldValid end
local camera=obj({GetCameraLocation=function()return {X=0,Y=0,Z=0}end,GetCameraRotation=function()return {Pitch=0,Yaw=0,Roll=0}end,GetFOVAngle=function()return 90 end})
local api=obj({IsGamePaused=function()return false end,GetGameInstance=function()return instance end,GetGameState=function()return world end,GetPlayerCameraManager=function()return camera end,GetTimeSeconds=function()return gameTime end})
local hooks,tick,files,opens={},nil,{},0
StaticFindObject=function(path)if path:find('GameplayStatics',1,true)then return api end;return obj()end
FindAllOf=function(name)return name=='ScenarioManager' and {manager} or {player}end
RegisterHook=function(path,_,post)hooks[path]=post end
LoopInGameThreadWithDelay=function(_,fn)tick=fn end
ExecuteInGameThreadWithDelay=function(_,fn)fn()end
local realPrint=print;print=function()end
io.open=function(path)
    if path:sub(-8)=='.partial'then opens=opens+1 end;files[path]=''
    return {write=function(_,...)files[path]=files[path]..table.concat({...});return true end,flush=function()end,close=function()end}
end
os.getenv=function()return 'test-private-root'end
os.remove=function(path)files[path]=nil;return true end
os.rename=function(a,b)files[b]=files[a];files[a]=nil;return true end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayCapture.lua');M.start({state=function()return intent end,onCompleted=function(fn)completion=fn end})
local checks=0;local function check(v,msg)assert(v,msg);checks=checks+1 end
local root='test-private-root/AimMod/KovaaksNative/replays/'
local function step(value)elapsed=value;gameTime=gameTime+.016;tick()end
local function attempt(id)intent={active=true,id=id,scenario='Synthetic scenario'};elapsed=0;tick();step(.016)end
local function complete()
    completion({scenario=intent.scenario,id=intent.id,score=10})
    intent.active=false;tick()
end

-- A completed attempt without motion is not left behind as a temporary file.
attempt('still');check(M.status().state=='recording' and M.status().frames==1,'writer opened')
complete()
check(files[root..'still.partial']==nil and files[root..'still.amreplay']==nil,'single-frame completion leaves no orphaned temporary file')

-- A transiently missing local character skips frames without failing.
attempt('respawn');step(.032)
local frames=M.status().frames
character.IsValid=function()return false end;step(.048);step(.064)
check(M.status().state=='recording' and M.status().frames==frames,'missing local character skips frames without an error')
character.IsValid=function()return true end;step(.08)
check(M.status().frames==frames+1,'sampling resumes once the character is valid')
complete();check(files[root..'respawn.amreplay']~=nil,'attempt with a skipped gap still completes')

-- World teardown mid-run ends only that attempt and never reads a stale clock.
attempt('teardown');step(.032)
worldValid=false;step(.048)
check(M.status().state=='ready' and M.status().reason=='world-changed','world teardown is a clean boundary, not a capture error')
check(files[root..'teardown.partial']==nil and files[root..'teardown.amreplay']==nil,'torn-down attempt discarded')
worldValid=true;intent.active=false;tick()

-- An unexpected read failure stops the attempt but not future recording.
local before=opens
characters=function()return nil end
attempt('broken-1')
check(M.status().state=='error' and M.status().reason=='capture-error','capture failure is visible')
check(files[root..'broken-1.partial']==nil,'failed attempt temporary file removed')
tick();tick();check(opens==before+1,'the failed attempt is not reopened every tick')
characters=function()return {} end;intent.active=false;tick()
attempt('recovered');check(opens==before+2 and M.status().state=='recording','next attempt records after a single failure')
step(.032);complete();check(files[root..'recovered.amreplay']~=nil,'recovered attempt published')

-- Input observer failures follow the same bounded policy.
attempt('input-fault')
hooks['/Script/GameSkillsTrainer.MetaInputRecordingComponent:RecordFirePressed']({get=function()error('synthetic input failure')end})
check(M.status().state=='error' and M.status().reason=='input-error','input observer failure is visible')
intent.active=false;tick()
attempt('after-input');check(M.status().state=='recording','input failure does not disable later recording')
complete()

-- Consecutive failures on every attempt disable capture until reload.
characters=function()return nil end
for n=1,3 do intent.active=false;tick();attempt('repeat-'..n) end
local disabledOpens=opens
characters=function()return {} end;intent.active=false;tick();attempt('after-disable');step(.032)
check(opens==disabledOpens and M.status().state=='error','three consecutive failures disable capture')

-- Size budget ends the attempt cleanly as 'size-limit' (tested in a fresh module).
index=0;opens=0;files={};hooks={}
M=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayCapture.lua');M.start({state=function()return intent end,onCompleted=function(fn)completion=fn end})
M.lowerLimits(4096+700)
intent.active=false;tick()
attempt('large');for n=2,40 do step(n*.016) end
check(M.status().reason=='size-limit' and M.status().state~='error','size budget ends the attempt without a capture error')
check(M.status().frames>1 and M.status().frames<40,'frames stop at the byte budget')
check(files[root..'large.partial']==nil and files[root..'large.amreplay']==nil,'over-budget attempt is not published')
local afterLimit=opens
intent.active=false;tick();attempt('next');check(opens==afterLimit+1 and M.status().state=='recording','size limit does not disable later recording')
print=realPrint
print('PASS '..checks..' capture bounds and recovery checks')
