-- Lua 5.3+. Pause-menu integration lifecycle with permissive synthetic widgets.
local checks=0;local function check(v,m)assert(v,m);checks=checks+1 end
local realPrint=print;print=function()end
-- Any unknown member is a callable synthetic object; explicit fields override.
local perm
perm=function(fields)
    return setmetatable(fields or {},{__index=function()return perm()end,__call=function()return perm()end})
end
local removed,inputModes,bridgeEnter,bridgeLeave,hook,loops=0,{},nil,nil,nil,{}
local menuVisible,paused,entryFails=true,false,true
local function container()return perm({GetChildrenCount=function()return 0 end})end
local root=container();root.IsA=function()return true end
local headerButtons=container()
local player=perm()
local menu=perm({WidgetTree=perm({RootWidget=root}),IsVisible=function()return menuVisible end,GetOwningPlayer=function()return player end})
local header=perm({Stats=perm({GetParent=function()return headerButtons end})})
local frames={}
local lib=perm({
    Create=function()return entryFails and {IsValid=function()return false end} or perm() end,
    SetInputMode_UIOnlyEx=function()inputModes[#inputModes+1]='ui' end,
    SetInputMode_GameOnly=function()inputModes[#inputModes+1]='game' end})
local api=perm({IsGamePaused=function()return paused end})
StaticFindObject=function(path)
    if path:find('WidgetBlueprintLibrary',1,true)then return lib end
    if path:find('GameplayStatics',1,true)then return api end
    return perm()
end
StaticConstructObject=function()
    local f=perm({RemoveFromParent=function()removed=removed+1 end});frames[#frames+1]=f;return f
end
FindAllOf=function(name)return name=='PauseMenu_C' and {menu} or {header}end
FindFirstOf=function()return perm()end
FName=function()return perm()end;FText=function()return perm()end
RegisterHook=function(_,_,post)hook=post end
LoopInGameThreadWithDelay=function(ms,fn)loops[ms]=fn end
package.preload.Workspace=function()return {create=function()return perm()end,update=function()end,closeRequested=function()return false end}end
package.preload.LiveHUD=function()return {attach=function()end,hide=function()end,update=function()end}end
package.preload.Telemetry=function()return {liveSnapshot=function()return {}end}end
package.preload.ReplayMainBridge=function()return {attach=function(_,enter,leave)bridgeEnter=enter;bridgeLeave=leave end,active=function()return false end}end
local Menu=dofile('../ue4ss/AimModNativeUI/Scripts/Menu.lua')
Menu.start()
-- A header entry that cannot be created rolls back the workspace frame.
local ok=pcall(Menu.attach)
check(not ok and removed>=1,'partial attachment removes the owned workspace frame')
check(bridgeEnter==nil,'replay bridge is not bound to a partial attachment')
-- The retry loop rebuilds the attachment once the entry can be created.
entryFails=false
check(pcall(Menu.attach) and bridgeEnter~=nil,'complete attachment binds replay transitions')
-- Native click observer never raises for invalid or foreign contexts.
check(pcall(hook,{get=function()error('synthetic stale context')end}),'click observer contains stale context errors')
check(hook({get=function()return perm()end})==nil,'click observer never overrides the native result')
-- Replay exit restores the menu input mode, or game input when the menu is hidden.
bridgeEnter();menuVisible=true;bridgeLeave()
check(inputModes[#inputModes]=='ui','visible pause menu regains UI input after replay')
bridgeEnter();menuVisible=false;paused=false;bridgeLeave()
check(inputModes[#inputModes]=='game','hidden menu in a running game restores game input')
local count=#inputModes
bridgeEnter();menuVisible=false;paused=true;bridgeLeave()
check(#inputModes==count,'paused game with hidden menu keeps its current input mode')
print=realPrint
print('PASS '..checks..' pause menu lifecycle checks')
