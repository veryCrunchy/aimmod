-- Owned multiplayer notice layer: invites, "host is starting" and countdowns,
-- shown even while the AimMod panel is closed. A small Gameface view at the top
-- centre, normally click-through. It only takes clicks for an incoming invite
-- while the game already shows the mouse cursor (menus); during play the
-- service's hotkey (F7) answers instead. No input mode, focus or key callbacks.
-- Call only from the native game thread.
local M={}
local owner,host,view,renderer,loadedUrl
local shown=false
local text
local lastId,lastCount
local interactive=false
local base=(os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/'
local Width,Height=620,230
local function valid(value)return value and value:IsValid()end
local function read(name,limit)
    local file=io.open(base..name,'rb');if not file then return nil end
    local ok,value=pcall(function()return file:read(limit)end);file:close()
    return ok and value or nil
end
local function noticeUrl()
    local url=read('live-overlay-url.txt',513)
    if not url then return nil end
    url=url:gsub('/overlay%?surface=game$','/notify')
    if url:match('^http://127%.0%.0%.1:%d+/%x+/notify$') then return url end
end
-- The game's own UI sounds; missing assets are simply skipped.
local sounds={}
local volume=0.8
local function play(name)
    if volume<=0 then return true end
    local ok=pcall(function()
        local path='/Game/Audio/UI/SFX/'..name..'.'..name
        local sound=sounds[name]
        if not valid(sound) then
            sound=StaticFindObject(path)
            if not valid(sound) and LoadAsset then sound=LoadAsset(path) end
            sounds[name]=sound
        end
        if valid(sound) and valid(owner) then
            StaticFindObject('/Script/Engine.Default__GameplayStatics'):PlaySound2D(owner,sound,volume,1.0,0.0,nil,nil,true)
        end
    end)
    return ok
end
local function setInteractive(on)
    if not valid(host) or not valid(view) or not valid(renderer) then return end
    interactive=on
    -- 4: self hit-test invisible (children can be clicked); 3: nothing can be clicked.
    host:SetVisibility(on and 4 or 3);view:SetVisibility(on and 0 or 3);renderer:SetVisibility(on and 0 or 3)
    renderer.bReceiveInput=on
end
function M.hide()
    if valid(host) then host:SetVisibility(1) end
    if valid(renderer) then renderer.bReceiveInput=false end
    shown=false;interactive=false
end
function M.close()
    M.hide()
    if valid(host) then host:RemoveFromParent() end
    host=nil;view=nil;renderer=nil;loadedUrl=nil
end
function M.attach(value)
    if owner==value and valid(owner) then return end
    M.close();owner=value
end
local function create(url)
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    host=lib:Create(owner,StaticFindObject('/Game/FirstPersonBP/Blueprints/UI/Palette/PalettedBorderWidget.PalettedBorderWidget_C'),owner:GetOwningPlayer())
    assert(valid(host) and valid(host.WidgetTree),'notice host unavailable')
    local canvas=StaticConstructObject(StaticFindObject('/Script/UMG.CanvasPanel'),host.WidgetTree)
    assert(valid(canvas),'notice canvas unavailable')
    host.WidgetTree.RootWidget=canvas;canvas:SetVisibility(4)
    host.bIsFocusable=false;host:SetVisibility(1)
    view=lib:Create(owner,StaticFindObject('/Game/UI/MetaGraphWidget.MetaGraphWidget_C'),owner:GetOwningPlayer())
    assert(valid(view),'notice view unavailable')
    view.bIsFocusable=false;view:SetVisibility(3)
    renderer=view:GetCohtmlWidget();assert(valid(renderer),'notice renderer unavailable')
    renderer.bReceiveInput=false;renderer:SetVisibility(3)
    -- Only the toast area: top centre, fixed size, so the rest of the screen is untouched.
    local slot=canvas:AddChildToCanvas(view)
    slot:SetAnchors({Minimum={X=0.5,Y=0},Maximum={X=0.5,Y=0}})
    slot:SetAlignment({X=0.5,Y=0});slot:SetAutoSize(false)
    slot:SetOffsets({Left=0,Top=0,Right=Width,Bottom=Height})
    host:AddToViewport(5100)
    host.bIsFocusable=false;view.bIsFocusable=false
    host:SetVisibility(1);renderer.bReceiveInput=false
    renderer:Load(url);loadedUrl=url
end
-- panelOpen: the AimMod panel is on screen (it shows the same things itself).
function M.update(panelOpen,replayActive)
    if not valid(owner) then M.close();owner=nil;return end
    -- A few hundred bytes, read on every 100 ms tick so countdowns stay in step.
    text=read('multiplayer-notify.json',4097)
    local active=text~=nil and #text<=4096 and text:find('"active":true',1,true)~=nil
    if not active or panelOpen or replayActive then M.hide();lastId=nil;lastCount=nil;return end
    local id=text:match('"id":"([^"]+)"')
    local sound=text:match('"sound":"(%a+)"')
    local count=tonumber(text:match('"countdown":(%d+)') or '')
    volume=math.max(0,math.min(1,tonumber(text:match('"volume":([%d%.]+)') or '') or 0.8))
    local url=noticeUrl()
    if not url then M.hide();return end
    if not valid(host) or not valid(view) or not valid(renderer) then
        M.close()
        local ok=pcall(create,url)
        if not ok then M.close();return end
    elseif url~=loadedUrl then renderer:Load(url);loadedUrl=url end
    if not shown then shown=true;host:SetVisibility(3) end
    local cursor=false
    pcall(function()cursor=owner:GetOwningPlayer().bShowMouseCursor==true end)
    local wantInput=text:find('"interactive":true',1,true)~=nil and cursor
    if wantInput~=interactive then setInteractive(wantInput) end
    if id~=lastId then
        lastId=id;lastCount=nil
        if sound=='popup' then play('popupCue') elseif sound=='click' then play('clickCue') end
    end
    if count and count~=lastCount then
        lastCount=count
        if count>=1 and count<=3 then play('countdown'..count..'Cue') end
    end
end
return M
