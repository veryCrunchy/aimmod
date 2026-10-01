-- Owned multiplayer notice layer: invites, "host is starting" and countdowns,
-- shown even while the AimMod panel is closed. A small Gameface view at the top
-- centre, normally click-through. It takes clicks only for a notice that asks
-- for them (invite, ready, load failure, the CS buy menu) while a cursor is on
-- screen: KovaaK's menus are up, or the game shows its cursor. During play the
-- service's hotkey (F7) answers instead. No input mode, focus or key callbacks.
-- Call only from the native game thread.
local M={}
local owner,host,view,renderer,loadedUrl,slot,layout
local shown=false
local text
local lastId,lastCount
local interactive=false
local playId,playSince
local tookCursor=false
local wantCursor,swallow,boardHeld,lastBlocked=false,false,false,false
local warned={}
local function warn(what,reason)
    if warned[what] then return end
    warned[what]=true;print('[AimModNotify] '..what..' failed: '..tostring(reason)..'\n')
end
local base=(os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/'
local Width,Height=620,340
-- Two sizes: the toast (top centre, 620 x 340, room for a card with buttons) for notices, and the whole screen for
-- the mode HUDs (CS, standings) that sit at the screen edges. The service says which.
local function place(full)
    if not slot then return end
    layout=full and 'full' or 'toast'
    if full then
        slot:SetAnchors({Minimum={X=0,Y=0},Maximum={X=1,Y=1}})
        slot:SetAlignment({X=0,Y=0})
        slot:SetOffsets({Left=0,Top=0,Right=0,Bottom=0})
    else
        slot:SetAnchors({Minimum={X=0.5,Y=0},Maximum={X=0.5,Y=0}})
        slot:SetAlignment({X=0.5,Y=0})
        slot:SetOffsets({Left=0,Top=0,Right=Width,Bottom=Height})
    end
end
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
-- The CS buy menu wants a cursor in game: show it (game and UI input, so movement keys still
-- work) and hand input back to the game once it closes. Only a cursor this layer took is given
-- back; while KovaaK's menus, the AimMod panel or a replay own input, it isn't touched.
-- KovaaK's controller keeps its own cursor state (AMetaPlayerController::K2_SetShowMouseCursor)
-- and can put bShowMouseCursor back, so both are set, and re-set while the layer holds the cursor.
local function showCursor(player,on)
    local ok,reason=pcall(function()player:K2_SetShowMouseCursor(on)end)
    if not ok then warn('K2_SetShowMouseCursor',reason) end
    pcall(function()player.bShowMouseCursor=on end)
end
local function applyInput(player,lib,cursor)
    showCursor(player,cursor)
    -- Game and UI: the view under the cursor gets clicks (mouse capture is released) while the
    -- movement keys still reach the game. No widget takes keyboard focus.
    local ok,reason
    if cursor then ok,reason=pcall(function()lib:SetInputMode_GameAndUIEx(player,nil,0,false)end)
    else ok,reason=pcall(function()lib:SetInputMode_GameOnly(player)end) end
    if not ok then warn(cursor and 'SetInputMode_GameAndUIEx' or 'SetInputMode_GameOnly',reason) end
end
local function cursorFor(want,blocked)
    lastBlocked=blocked
    if blocked then tookCursor=false;return end
    if not valid(owner) then return end
    local player=owner:GetOwningPlayer()
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    if not valid(player) or not valid(lib) then return end
    if want==tookCursor then
        -- Holding the cursor: put it back if the game hid it.
        if want then local shown=true;pcall(function()shown=player.bShowMouseCursor==true end);if not shown then showCursor(player,true) end end
        return
    end
    tookCursor=want
    applyInput(player,lib,want)
end
-- KovaaK's pause menu opened while the buy menu was up (or Escape just closed it): Menu.lua hides
-- that menu again; this puts back the input the layer had (cursor for the buy menu, or the game).
function M.swallowMenu()return wantCursor or swallow or tookCursor end
function M.restoreInput()
    if not valid(owner) then return end
    local player=owner:GetOwningPlayer()
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    if not valid(player) or not valid(lib) then return end
    tookCursor=wantCursor
    applyInput(player,lib,wantCursor)
end
-- The scoreboard is display-only. While its key is held, an unbound Tab can make Slate move
-- keyboard focus off the game viewport (focus navigation), and the game then drops its held keys
-- and its mouse capture. Focus goes straight back to the viewport; nothing else changes.
function M.keepGameFocus()
    if not boardHeld or tookCursor or lastBlocked then return false end
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    if not valid(lib) then return false end
    local ok,reason=pcall(function()lib:SetFocusToGameViewport()end)
    if not ok then warn('SetFocusToGameViewport',reason) end
    return ok
end
-- The service's "match is starting" request: an id per load attempt and when it began (ms).
function M.playRequest()return playId,playSince end
function M.hide()
    if valid(host) then host:SetVisibility(1) end
    if valid(renderer) then renderer.bReceiveInput=false end
    shown=false;interactive=false
end
function M.close()
    M.hide()
    if valid(host) then host:RemoveFromParent() end
    host=nil;view=nil;renderer=nil;loadedUrl=nil;slot=nil;layout=nil
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
    slot=canvas:AddChildToCanvas(view)
    slot:SetAutoSize(false)
    place(false)
    host:AddToViewport(5100)
    host.bIsFocusable=false;view.bIsFocusable=false
    host:SetVisibility(1);renderer.bReceiveInput=false
    renderer:Load(url);loadedUrl=url
end
-- panelOpen: the AimMod panel is on screen (it shows the same things itself).
-- menuVisible: KovaaK's own menus (scenario browser, settings) are on screen, so the
-- player has a cursor even when the controller's bShowMouseCursor doesn't say so.
function M.update(panelOpen,replayActive,menuVisible)
    if not valid(owner) then M.close();owner=nil;return end
    -- A few hundred bytes, read on every 100 ms tick so countdowns stay in step.
    text=read('multiplayer-notify.json',16385)
    playId,playSince=nil,nil
    if text and #text<=16384 then
        local id,since=text:match('"play":{"id":"([^"]+)","since":(%d+)')
        if id then playId=id;playSince=tonumber(since) end
    end
    wantCursor=text~=nil and text:find('"cursor":true',1,true)~=nil
    swallow=text~=nil and text:find('"swallowMenu":true',1,true)~=nil
    boardHeld=text~=nil and text:find('"boardFull":{',1,true)~=nil
    cursorFor(wantCursor,panelOpen or replayActive or menuVisible==true)
    -- A notice, only the watcher badge ("2 watching: ..."), a mode HUD (tracking duel, combat),
    -- or the match standings (corner panel, or the scoreboard while its key is held).
    local active=text~=nil and #text<=16384 and (text:find('"active":true',1,true)~=nil or text:find('"badge":"',1,true)~=nil or text:find('"duel":{',1,true)~=nil or text:find('"combat":{',1,true)~=nil or text:find('"cs":{',1,true)~=nil or text:find('"board":{',1,true)~=nil or text:find('"boardFull":{',1,true)~=nil)
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
    local full=text:find('"layout":"full"',1,true)~=nil
    if (full and layout~='full') or (not full and layout~='toast') then pcall(place,full) end
    -- A cursor is on screen: KovaaK's menus are up, this layer showed it (buy menu), or the game did.
    local cursor=menuVisible==true or tookCursor
    if not cursor then pcall(function()cursor=owner:GetOwningPlayer().bShowMouseCursor==true end) end
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
