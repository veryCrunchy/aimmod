local count=0
local function check(v,label)assert(v,label);count=count+1 end
local id=0
local function obj(t)id=id+1;local address=id;t=t or {};t.IsValid=function(self)return not self.dead end;t.GetAddress=function()return address end;return t end
local world=obj();local activeWorld=world;local calls={};local body=obj({IsA=function()return true end})
local weapon=obj({CachedBodyHitSounds={body},WeaponSettingsNative={HitSoundCooldown=0}})
local character=obj({GetCurrentWeapon=function()return weapon end})
local controller=obj({MyCharacter=character,GetWorld=function()return world end})
local owner=obj({GetWorld=function()return activeWorld end})
local soundClass=obj()
local expectedVolume,expectedPitch=1,1;local masterVolume,hitVolume,hitPitch=1,1,1
local settings=obj({GetFloatUserSetting=function(_,id)if id==19 then return masterVolume elseif id==26 then return hitVolume elseif id==27 then return hitPitch end;error('unexpected setting')end})
local api=obj({GetPlayerController=function()return controller end,PlaySound2D=function(_,context,sound,volume,pitch,start,concurrency,actor,ui)
    assert(context==owner and sound==body and volume==expectedVolume and pitch==expectedPitch and start==0 and concurrency==nil and actor==controller and ui==true)
    calls[#calls+1]=sound
end})
StaticFindObject=function(path)if path=='/Script/GameSkillsTrainer.Default__MetaGameUserSettings'then return {Get=function()return settings end}end;if path=='/Script/Engine.Default__GameplayStatics'then return api end;if path=='/Script/Engine.SoundWave'then return soundClass end;error('unexpected asset lookup')end
local Effects=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayEffects.lua')
local function frame(time,hitTime,hits,transport,playing)
    return {transport=transport or 1,time={time=time,duration=60,playing=playing~=false,speed=1},hit=hitTime and {time=hitTime,hits=hits,delta=1}}
end
local s=Effects.create(owner)
check(not s.update(frame(0,0,1)),'initial frame silent')
check(s.update(frame(.1,.1,2)) and #calls==1,'confirmed forward hit uses cached UI audio')
check(not s.update(frame(.1,.1,2)) and #calls==1,'repeat frame deduplicated')
check(not s.update(frame(.2,.1,2)) and #calls==1,'same hit on later frame deduplicated')
check(s.update(frame(.21,.21,3)) and #calls==2,'next hit plays')
check(s.update(frame(.22,.22,4)) and #calls==2,'dense hit remains visual but audio bounded')
check(not s.update(frame(10,10,20,2)) and #calls==2,'seek generation silent')
check(s.update(frame(10.1,10.1,21,2)) and #calls==3,'post-seek new hit audible')
check(not s.update(frame(10.2,10.2,22,2,false)),'pause silent')
check(not s.update(frame(10.2,10.2,22,3,true)),'resume baseline silent')
check(not s.update(frame(5,5,5,3)),'backward clock silent even without revision')
check(not s.update(frame(7,7,6,3)),'large forward gap silent')
check(not s.update(frame(7.1,8,7,3)),'future hit ignored')
check(not s.update(frame(7.2,7,8,3)),'stale timestamp ignored')
check(not s.update(frame(7.3,7.3,7,3)),'regressed cumulative count ignored')
local before=#calls
s.close();check(not s.update(frame(7.4,7.4,9,3)) and #calls==before,'close prevents sound')
s=Effects.create(owner);check(not s.update(frame(0)),'legacy baseline');check(not s.update(frame(.1)) and #calls==before,'legacy has no fabricated audio')
local malformed=frame(.2,.2,1);malformed.hit.delta=2;check(not s.update(malformed),'invalid hit counts ignored')
s.update(frame(.3));activeWorld=obj();check(s.update(frame(.4,.4,2)) and #calls==before,'world mismatch silent')
activeWorld=world;body.dead=true;s=Effects.create(owner);s.update(frame(0));check(s.update(frame(.1,.1,1)) and #calls==before,'missing sound asset keeps visual hit')
body.dead=false
weapon.CachedBodyHitSounds={ForEach=function(_,fn)fn(0,{get=function()return body end})end}
s=Effects.create(owner);s.update(frame(0));check(s.update(frame(.1,.1,1)) and #calls==before+1,'remote sound array supported')
local missingTransport=frame(.2,.2,2);missingTransport.transport=nil;check(not s.update(missingTransport),'missing transport safely resets')
check(not s.update(frame(.3,.3,3)),'reset next frame is silent')
weapon.WeaponSettingsNative.HitSoundCooldown=.5;s=Effects.create(owner);s.update(frame(0));s.update(frame(.1,.1,1));before=#calls
check(s.update(frame(.2,.2,2)) and #calls==before,'configured cooldown respected')
check(s.update(frame(.6,.6,3)) and #calls==before+1,'configured cooldown expires')
weapon.WeaponSettingsNative.HitSoundCooldown=0
masterVolume=.6;hitVolume=.5;hitPitch=1.2;expectedVolume=.3;expectedPitch=1.2
s=Effects.create(owner);s.update(frame(0));before=#calls
check(s.update(frame(.1,.1,1)) and #calls==before+1,'native raw master times hit volume and pitch respected')
hitVolume=0;check(s.update(frame(.2,.2,2)) and #calls==before+1,'local hit mute suppresses audio without suppressing visual')
hitVolume=1;masterVolume=0;check(s.update(frame(.3,.3,3)) and #calls==before+1,'local master mute respected')
masterVolume=1;hitPitch=0/0;check(s.update(frame(.4,.4,4)) and #calls==before+1,'invalid setting fails silent')
settings.dead=true;s=Effects.create(owner);s.update(frame(0));before=#calls
check(s.update(frame(.1,.1,1)) and #calls==before,'unavailable settings preserve visual and fail silent');settings.dead=false
local instance=obj();api.GetGameInstance=function()return instance end
FindAllOf=function()return {obj({GetOuter=function()return instance end,GetCurrentScenario=function()return obj({GetChallengeProfile=function()return {BlockHitSounds=true}end})end})}end
hitPitch=1;s=Effects.create(owner);s.update(frame(0))
check(s.update(frame(.1,.1,1)) and #calls==before,'scenario sound block preserves visual but mutes audio')
print(count..' replay effects checks passed')
