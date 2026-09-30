-- Lua 5.3+. Discord handoff with synthetic UE4SS globals and in-memory files.
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
local files,now,loop={},1000,nil
local base='synthetic/AimMod/KovaaksNative/'
os.getenv=function()return'synthetic'end;os.time=function()return now end
os.remove=function(p)files[p]=nil end
os.rename=function(a,b)if files[a]==nil then return nil end;files[b]=files[a];files[a]=nil;return true end
io.open=function(p,mode)
    if mode=='rb' then
        if files[p]==nil then return nil end
        return {read=function(_,n)return files[p]:sub(1,n)end,close=function()end}
    end
    return {write=function(_,s)files[p]=s;return true end,close=function()end}
end
local calls={}
local function valid(t)t.IsValid=function()return true end;return t end
local game=valid({enabled=true,GetFullName=function()return'MetaGameUserSettings /Engine/Transient.MetaGameUserSettings_0'end})
function game:GetDiscordRichPresence()return self.enabled end
function game:SetDiscordRichPresence(v)calls[#calls+1]=v;self.enabled=v end
local cdo=valid({GetFullName=function()return'MetaGameUserSettings /Script/GameSkillsTrainer.Default__MetaGameUserSettings'end})
local functionsPresent,instances=true,{cdo,game}
StaticFindObject=function(path)
    assert(path:find('/Script/GameSkillsTrainer.MetaGameUserSettings:',1,true),'only the presence accessors are resolved')
    return functionsPresent and valid({}) or {IsValid=function()return false end}
end
FindAllOf=function(name)assert(name=='MetaGameUserSettings');return instances end
LoopInGameThreadWithDelay=function(ms,fn)assert(ms==500);loop=fn end
local function ack()local text=files[base..'discord-game.tsv'];return text and text:match('^AIMMOD_DISCORD_GAME_1\t(%a+)\t%d+\n$')end
local function request()files[base..'discord-takeover.tsv']='AIMMOD_DISCORD_TAKEOVER_1\t'..now..'\n'end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/DiscordPresence.lua')
M.start();M.start()
for _=1,10 do loop() end
check(ack()==nil and #calls==0,'waits for the engine before touching settings')
loop();check(ack()=='game' and #calls==0,'without a request the game keeps its presence')
request();now=now+1;loop()
check(calls[1]==false and game.enabled==false,'request turns KovaaK presence off')
check(files[base..'discord-restore.tsv']~=nil and ack()=='released','hold is recorded and acknowledged')
game.enabled=true;now=now+1;request();loop()
check(calls[2]==false and ack()=='released','player re-enabling while held is re-asserted')
now=now+7;loop()
check(calls[3]==true and game.enabled==true,'stale request hands the presence back')
check(files[base..'discord-restore.tsv']==nil and ack()=='game','hold released and acknowledged')
-- Player keeps KovaaK's presence off: nobody publishes and it stays off.
game.enabled=false;request();now=now+1;loop()
check(#calls==3 and ack()=='off' and files[base..'discord-restore.tsv']==nil,'player choice to disable is respected')
files[base..'discord-takeover.tsv']=nil;now=now+1;loop()
check(#calls==3 and ack()=='off','no restore without a hold')
-- Crash while held: the next launch restores from the marker.
game.enabled=false;files[base..'discord-restore.tsv']='AIMMOD_DISCORD_RESTORE_1\n'
package.loaded.DiscordPresence=nil
M=dofile('../ue4ss/AimModNativeUI/Scripts/DiscordPresence.lua');M.start()
for _=1,11 do loop() end
check(calls[4]==true and game.enabled and files[base..'discord-restore.tsv']==nil,'leftover hold is restored on the next launch')
-- Malformed or future-dated requests are ignored.
files[base..'discord-takeover.tsv']='AIMMOD_DISCORD_TAKEOVER_1\tabc\n';now=now+1;loop()
check(#calls==4 and ack()=='game','malformed request ignored')
files[base..'discord-takeover.tsv']='AIMMOD_DISCORD_TAKEOVER_1\t'..(now+10)..'\n';now=now+1;loop()
check(#calls==4,'future-dated request ignored')
-- Missing accessors or settings object: report unavailable, change nothing.
functionsPresent=false;package.loaded.DiscordPresence=nil
M=dofile('../ue4ss/AimModNativeUI/Scripts/DiscordPresence.lua');M.start();request()
for _=1,11 do loop() end
check(#calls==4 and ack()=='unavailable','unsupported build reports unavailable')
functionsPresent=true;instances={cdo};package.loaded.DiscordPresence=nil
M=dofile('../ue4ss/AimModNativeUI/Scripts/DiscordPresence.lua');M.start();now=now+1;request()
for _=1,11 do loop() end
check(#calls==4 and ack()=='unavailable','default object alone is never called')
print('PASS '..checks..' Discord handoff checks')
