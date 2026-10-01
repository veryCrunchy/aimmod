local index=0
local completion
local function obj(t)index=index+1;local address=index;t=t or {};t.IsValid=function()return true end;t.GetAddress=function()return address end;return t end
local gameTime,elapsed,queue,nativeActive=0,0,2,false
local intent={active=false,id='prelude',scenario='Synthetic scenario'}
local instance=obj();local character=obj()
character.PlaybackComponent=obj({GetOwner=function()return character end})
local player=obj({MyCharacter=character,GetFullName=function()return 'Synthetic controller' end})
local scenario=obj({GetName=function()return intent.scenario end,IsActive=function()return nativeActive end,IsInChallenge=function()return nativeActive end})
local manager=obj({GetOuter=function()return instance end,GetCurrentScenario=function()return scenario end,IsInChallenge=function()return nativeActive end,
    IsScenarioLoading=function()return false end,GetChallengeQueueTimeRemaining=function()return queue end,GetChallengeTimeElapsed=function()return elapsed end})
local world=obj({IsA=function()return true end,GetCurrentMapName=function()return 'Synthetic map' end,GetMapScale=function()return 1 end,GetCharacters=function()return {} end})
local camera=obj({GetCameraLocation=function()return {X=0,Y=0,Z=0}end,GetCameraRotation=function()return {Pitch=0,Yaw=0,Roll=0}end,GetFOVAngle=function()return 90 end})
local api=obj({IsGamePaused=function()return false end,GetGameInstance=function()return instance end,GetGameState=function()return world end,GetPlayerCameraManager=function()return camera end,GetTimeSeconds=function()return gameTime end})
local hooks,tick,files,opens,scans={},{},{},0,0
StaticFindObject=function(path)if path:find('GameplayStatics',1,true)then return api end;return obj()end
FindAllOf=function(name)scans=scans+1;return name=='ScenarioManager' and {manager} or {player}end
RegisterHook=function(path,_,post)hooks[path]=post end
LoopInGameThreadWithDelay=function(_,fn)tick=fn end
ExecuteInGameThreadWithDelay=function(_,fn)fn()end
io.open=function(path)
    if path:sub(-8)=='.partial'then opens=opens+1 end;files[path]=''
    return {write=function(_,...)files[path]=files[path]..table.concat({...});return true end,flush=function()end,close=function()end}
end
os.getenv=function()return 'test-private-root'end
os.remove=function(path)files[path]=nil;return true end
os.rename=function(a,b)files[b]=files[a];files[a]=nil;return true end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayCapture.lua');M.start({state=function()return intent end,onCompleted=function(fn)completion=fn end})
local checks=0;local function check(v,msg)assert(v,msg);checks=checks+1 end
intent.active=true;tick();gameTime=1.477;queue=.523;tick()
check(opens==0 and M.status().state=='awaiting','UI prelude never creates partial or completed replay')
intent.id='actual';nativeActive=true;queue=-1;elapsed=0;tick()
check(opens==0,'replacement intent requires observed timer advancement')
elapsed=.016;gameTime=1.493;tick();check(opens==1 and M.status().state=='recording','one file starts for real advancing challenge')
gameTime=1.51;elapsed=.033;tick();completion({scenario=intent.scenario,id=intent.id});intent.active=false;tick()
check(files['test-private-root/AimMod/KovaaksNative/replays/actual.amreplay']~=nil,'real completed replay published')
check(files['test-private-root/AimMod/KovaaksNative/replays/prelude.partial']==nil and files['test-private-root/AimMod/KovaaksNative/replays/prelude.amreplay']==nil,'no prelude archive to hide or merge')
intent={active=true,id='short',scenario='Synthetic scenario'};elapsed=0;tick();elapsed=.016;gameTime=2;tick();elapsed=.04;gameTime=2.024;tick();intent.active=false;tick()
check(opens==2 and files['test-private-root/AimMod/KovaaksNative/replays/short.amreplay']==nil and files['test-private-root/AimMod/KovaaksNative/replays/short.partial']==nil,'interrupted attempt temp closed and removed without publishing')
intent={active=true,id='paused',scenario='Synthetic scenario'};elapsed=0;tick();tick();tick()
check(opens==2 and M.status().state=='awaiting','frozen paused timer never opens replay')
queue=1;elapsed=.2;tick();queue=-1;elapsed=.21;tick()
check(opens==2,'queue resets advancement seed even when active flag true')
elapsed=.22;gameTime=3;tick();check(opens==3,'starts only after queue has ended and timer advances')
local function step(value)elapsed=value;gameTime=gameTime+.016;tick()end
step(.21);step(.215);step(.23)
check(opens==3 and M.status().state=='recording','millisecond timer jitter never splits the writer')
local frameCount=M.status().frames
step(0)
check(opens==3 and M.status().frames==frameCount,'unconfirmed zero read suspends sampling without discarding writer')
step(.24)
check(opens==3 and M.status().frames>frameCount,'stale zero recovery resumes same writer')
nativeActive=false;step(.25);step(.26);nativeActive=true;step(.27);step(.28)
check(opens==3 and M.status().state=='recording','brief inactive phase resumes same attempt rather than creating partial runs')
step(8);step(7.5);step(7.52);step(8.02)
check(opens==3,'mid-run timer correction does not masquerade as return to challenge start')
nativeActive=false;step(8.03);nativeActive=true;step(0)
check(opens==3 and M.status().state=='recording','restart after inactive phase waits for confirmation')
step(.016)
check(opens==3 and M.status().state=='awaiting','advancing timer below previous attempt confirms true restart')
step(.032)
check(opens==4 and M.status().state=='recording','true restart opens exactly one fresh writer')
check(files['test-private-root/AimMod/KovaaksNative/replays/paused.partial']==nil and files['test-private-root/AimMod/KovaaksNative/replays/paused.amreplay']==nil,'interrupted original never published as partial history')
step(.048);intent.active=false;tick()
intent={active=true,id='unsupported',scenario='Synthetic scenario'};manager.GetChallengeTimeElapsed=nil;tick()
check(M.status().state=='unsupported' and opens==4,'missing native API fails closed')
local beforeScans=scans;for i=1,100 do tick()end
check(scans==beforeScans and opens==4,'unsupported gate neither rescans nor opens repeatedly')
print('PASS '..checks..' authoritative capture gate checks')
