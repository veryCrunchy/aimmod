-- Synthetic checks for the multiplayer notice layer (Notify.lua): when it takes clicks,
-- where it sits, and that it never takes focus or input mode. No game is launched.
local checks=0
local function check(value,message)assert(value,message);checks=checks+1 end
local function obj(t)t=t or {};t.IsValid=function(self)return not self.invalid end;return t end
local notice
local files={['live-overlay-url.txt']='http://127.0.0.1:12345/abc123/overlay?surface=game'}
io.open=function(path,mode)
    check(mode=='rb','only reads local files')
    local name=path:match('[^/]+$')
    local text=name=='multiplayer-notify.json' and notice or files[name];if not text then return nil end
    return {read=function(_,limit)return text:sub(1,limit)end,close=function()end}
end
RegisterKeyBind=function()error('no input-thread callbacks')end
RegisterHook=function()error('no input hooks')end
local player=obj({bShowMouseCursor=false});local owner=obj({GetOwningPlayer=function()return player end})
local widgets,renderers,slots={}, {}, {}
local function widget()
    local w=obj({WidgetTree=obj()})
    w.SetVisibility=function(self,value)self.visibility=value end
    w.RemoveFromParent=function(self)self.removed=true end
    w.SetUserFocus=function()error('notices must not take focus')end
    w.AddToViewport=function(self,z)self.z=z;self.attached=true end
    widgets[#widgets+1]=w;return w
end
local lib=obj({SetInputMode_UIOnlyEx=function()error('must keep the game input mode')end,SetInputMode_GameOnly=function()error('must keep the game input mode')end})
lib.Create=function(_,context,class,controller)
    check(context==owner and controller==player,'owned player context')
    local w=widget()
    if class.path:find('MetaGraphWidget',1,true)then
        local r=obj({loads={}})
        r.SetVisibility=function(self,value)self.visibility=value end
        r.Load=function(self,url)self.loads[#self.loads+1]=url end
        w.GetCohtmlWidget=function()return r end;renderers[#renderers+1]=r
    end
    return w
end
StaticFindObject=function(path)
    if path:find('Default__WidgetBlueprintLibrary',1,true)then return lib end
    if path:find('Default__GameplayStatics',1,true)then return obj({PlaySound2D=function()end})end
    return obj({path=path})
end
LoadAsset=function(path)return obj({path=path})end
StaticConstructObject=function(class,outer)
    check(class.path=='/Script/UMG.CanvasPanel','only a native canvas is constructed')
    local canvas=obj({kind='CanvasPanel'})
    canvas.SetVisibility=function(self,value)self.visibility=value end
    canvas.AddChildToCanvas=function(_,child)
        local slot={child=child}
        for _,name in ipairs({'Anchors','Alignment','AutoSize','Offsets'})do slot['Set'..name]=function(self,value)self[name]=value end end
        slots[#slots+1]=slot;return slot
    end
    return canvas
end
local Notify=dofile('../ue4ss/AimModNativeUI/Scripts/Notify.lua')
Notify.attach(owner)
local function clickable()
    local host,view,renderer=widgets[1],widgets[2],renderers[1]
    return host.visibility==4 and view.visibility==0 and renderer.visibility==0 and renderer.bReceiveInput==true
end
local function clickThrough()
    local host,renderer=widgets[1],renderers[1]
    return host.visibility==3 and renderer.bReceiveInput==false
end
local failed='{"version":1,"active":true,"id":"lf-m1-1","kind":"invite","eyebrow":"AimMod · Match","title":"Couldn’t load the match (1/2)","layout":"toast","actions":[{"label":"Retry","action":"retry-load","id":"m1"},{"label":"Abort","action":"end","id":"m1"}],"interactive":true,"volume":0}'
-- KovaaK's scenario browser: the menu is up, the controller doesn't report a cursor.
notice=failed;player.bShowMouseCursor=false
Notify.update(false,false,true)
check(widgets[1].attached and widgets[1].z==5100,'notice layer mounted above the live HUD')
check(clickable(),'a load failure can be clicked in KovaaK\'s menus even without bShowMouseCursor')
check(slots[1].Offsets.Right==620 and slots[1].Offsets.Bottom>=320,'the toast slot leaves room for a card with buttons')
check(slots[1].Anchors.Minimum.X==0.5 and slots[1].Anchors.Maximum.X==0.5,'toast anchored top centre')
-- In a scenario with no cursor: click-through, the hotkey answers instead.
Notify.update(false,false,false)
check(clickThrough(),'no cursor during play: the layer stays click-through')
-- In game with the cursor shown (the game's own cursor).
player.bShowMouseCursor=true
Notify.update(false,false,false)
check(clickable(),'cursor shown in game: the notice takes clicks')
-- A notice without buttons never takes clicks, menus or not.
notice='{"version":1,"active":true,"id":"cd-m1-1","kind":"countdown","title":"Match starting in 3","countdown":3,"layout":"toast","interactive":false,"volume":0}'
Notify.update(false,false,true)
check(clickThrough(),'countdowns are click-through even in menus')
-- An invite in menus is clickable again; the AimMod panel being open hides the layer.
notice='{"version":1,"active":true,"id":"inv-1","kind":"invite","title":"Synthetic One invited you","layout":"toast","actions":[{"label":"Join","action":"accept-invite","id":"1"}],"interactive":true,"volume":0}'
player.bShowMouseCursor=false
Notify.update(false,false,true)
check(clickable(),'an invite can be clicked in KovaaK\'s menus')
Notify.update(true,false,true)
check(widgets[1].visibility==1 and renderers[1].bReceiveInput==false,'hidden and inputless while the AimMod panel is open')
-- The CS HUD switches to the full-screen layout; the buy menu takes clicks with the cursor shown.
notice='{"version":1,"active":false,"layout":"full","interactive":true,"cs":{"phase":"freeze","buyOpen":true},"volume":0}'
player.bShowMouseCursor=true
Notify.update(false,false,false)
check(slots[1].Anchors.Maximum.X==1 and slots[1].Anchors.Maximum.Y==1 and slots[1].Offsets.Right==0,'full-screen layout for the CS HUD')
check(clickable(),'the buy menu takes clicks')
notice=failed
Notify.update(false,false,true)
check(slots[1].Anchors.Maximum.X==0.5 and slots[1].Offsets.Bottom>=320,'back to the toast for a plain notice')
check(#widgets==2,'one host and one view, reused')
-- The CS buy menu in game: the layer shows the cursor (game and UI input) and gives input back after.
local modes={}
lib.SetInputMode_GameAndUIEx=function(_,p,widget,lock,hide)check(p==player and widget==nil and hide==false,'game and UI input, cursor kept during capture');modes[#modes+1]='gameui' end
lib.SetInputMode_GameOnly=function(_,p)check(p==player,'owning player');modes[#modes+1]='game' end
local buying='{"version":1,"active":false,"layout":"full","interactive":true,"cursor":true,"cs":{"phase":"freeze","buyOpen":true},"volume":0}'
local closed='{"version":1,"active":false,"layout":"full","interactive":false,"cs":{"phase":"freeze","buyOpen":false},"volume":0}'
player.bShowMouseCursor=false
notice=buying;Notify.update(false,false,false)
check(modes[1]=='gameui' and player.bShowMouseCursor==true,'buy menu open: the cursor shows')
check(clickable(),'and the buy menu takes clicks with it')
Notify.update(false,false,false);check(#modes==1,'the cursor is taken once')
notice=closed;Notify.update(false,false,false)
check(modes[2]=='game' and player.bShowMouseCursor==false,'buy menu closed: game-only input and no cursor again')
Notify.update(false,false,false);check(#modes==2,'input is handed back once')
-- KovaaK's menus own input: the layer neither takes nor gives back the cursor there.
notice=buying;Notify.update(false,false,true)
check(#modes==2,'no input changes while KovaaK\'s menus are up')
notice=closed;Notify.update(false,false,false);check(#modes==2,'nothing to give back that the layer did not take')
-- The play request is read from the notice file.
notice='{"version":1,"active":false,"play":{"id":"play-m-abc-0","since":1700000000123},"volume":0}'
Notify.update(false,false,false)
local playId,since=Notify.playRequest()
check(playId=='play-m-abc-0' and since==1700000000123,'play request parsed')
notice='{"version":1,"active":false}';Notify.update(false,false,false)
check(Notify.playRequest()==nil,'no request once the service stops sending it')
print(('notify: %d checks passed'):format(checks))
