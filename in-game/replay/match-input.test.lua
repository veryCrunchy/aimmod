-- Lua 5.3+. A starting match gives input back to the game (Menu.lua's play request hand-back):
-- the AimMod panel and KovaaK's menu close, input goes game-only with no cursor, once per
-- request, and a KovaaK's menu the player opened during loading stays. Synthetic widgets only.
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
local realPrint=print;print=function()end
local perm
perm=function(fields)
    return setmetatable(fields or {},{__index=function()return perm()end,__call=function()return perm()end})
end
local now=1000
os.time=function()return now end
local inputModes,hook,loops={}, nil, {}
local menuVisible,paused=true,false
local function container()return perm({GetChildrenCount=function()return 0 end})end
local root=container();root.IsA=function()return true end
local headerButtons=container()
local player=perm({bShowMouseCursor=true})
local menu=perm({WidgetTree=perm({RootWidget=root}),IsVisible=function()return menuVisible end,GetOwningPlayer=function()return player end,
    SetVisibility=function(_,v)menuVisible=v~=1 and v~=2 end})
local header=perm({Stats=perm({GetParent=function()return headerButtons end})})
local frames={}
local lib=perm({
    Create=function()return perm({GetFullName=function()return 'AimModEntry' end})end,
    SetInputMode_UIOnlyEx=function()inputModes[#inputModes+1]='ui' end,
    SetInputMode_GameOnly=function(_,p)check(p==player,'game input for the owning player');inputModes[#inputModes+1]='game' end})
local unpaused=0
local api=perm({IsGamePaused=function()return paused end,SetGamePaused=function(_,_,v)if v==false then paused=false;unpaused=unpaused+1 end end})
StaticFindObject=function(path)
    if path:find('WidgetBlueprintLibrary',1,true)then return lib end
    if path:find('GameplayStatics',1,true)then return api end
    return perm()
end
StaticConstructObject=function()
    local f=perm({SetVisibility=function(self,v)self.visibility=v end,GetFullName=function()return 'AimModEntry' end});frames[#frames+1]=f;return f
end
FindAllOf=function(name)return name=='PauseMenu_C' and {menu} or {header}end
FindFirstOf=function()return perm()end
FName=function()return perm()end;FText=function()return perm()end
RegisterHook=function(_,_,post)hook=post end
LoopInGameThreadWithDelay=function(ms,fn)loops[ms]=fn end
local request={}
package.preload.Workspace=function()return {create=function()return perm()end,update=function()end,closeRequested=function()return false end,openRequest=function()return nil end,consumeOpenRequest=function()end,openPage=function()end,deliverPage=function()end}end
package.preload.Notify=function()return {attach=function()end,update=function()end,hide=function()end,playRequest=function()return request.id,request.since end}end
package.preload.LiveHUD=function()return {attach=function()end,hide=function()end,update=function()end}end
package.preload.Telemetry=function()return {liveSnapshot=function()return {}end}end
package.preload.ReplayMainBridge=function()return {attach=function()end,active=function()return false end}end
local Menu=dofile('../ue4ss/AimModNativeUI/Scripts/Menu.lua')
Menu.start()
check(pcall(Menu.attach),'menu attaches')
local frame
frame=frames[1]
local function tick()loops[100]()end
local function openPanel()hook({get=function()return perm({GetFullName=function()return 'AimModEntry' end})end})end
tick()
-- 1. Started from the AimMod panel: the panel and KovaaK's menu close, game input, no cursor.
openPanel();check(frame.visibility==0,'the AimMod panel is open')
request={id='play-m-1-0',since=now*1000};now=now+8;tick()
check(frame.visibility==1,'the AimMod panel closes when the match starts')
check(not menuVisible,'KovaaK\'s menu closes with it')
check(inputModes[#inputModes]=='game' and player.bShowMouseCursor==false,'game-only input and no cursor: move and look without clicking in')
-- 2. Once per request: the same id never hands back again (the countdown and play keep sending it).
local count=#inputModes
menuVisible=true;tick();now=now+1;tick()
check(#inputModes==count and menuVisible,'the same request is handled once')
-- 3. Escape while the next attempt loads: that menu is the player's and stays.
menuVisible=false;tick()
request={id='play-m-1-1',since=now*1000};now=now+3;menuVisible=true;tick() -- the menu opened 3 s after the attempt began
check(#inputModes==count and menuVisible,'a KovaaK\'s menu opened during loading is left alone')
-- 4. Retry (or an invite join) from a notice while KovaaK's menu was already up: input goes back to the game.
now=now+20;request={id='play-m-1-2',since=now*1000};now=now+6;player.bShowMouseCursor=true;tick()
check(#inputModes==count+1 and inputModes[#inputModes]=='game' and not menuVisible and player.bShowMouseCursor==false,'after Retry from the notice the game gets input back')
-- 5. Already in the scenario with the cursor shown (a notice was answered): cursor hidden, game input; a paused game resumes.
player.bShowMouseCursor=true;paused=true
request={id='play-m-2-0',since=now*1000};now=now+5;tick()
check(inputModes[#inputModes]=='game' and player.bShowMouseCursor==false and unpaused==1,'in game the cursor goes away and the game is unpaused')
-- 6. No request, no input changes.
count=#inputModes;request={};for _=1,5 do now=now+1;tick() end
check(#inputModes==count,'without a play request input is untouched')
print=realPrint
print('PASS '..checks..' match input checks')
