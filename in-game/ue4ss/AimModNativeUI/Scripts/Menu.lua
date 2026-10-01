local M = {}
local Workspace = require('Workspace')
local ReplayMainBridge = require('ReplayMainBridge')
local LiveHUD = require('LiveHUD')
local Notify = require('Notify')
local Telemetry = require('Telemetry')
local menu, header, frame, entry, texture
local opened = false
local attachControls
local source = debug.getinfo(1, 'S').source:sub(2):gsub('\\','/')
local assets = source:match('^(.*)/Scripts/[^/]+$') .. '/Assets/'
local function valid(o) return o ~= nil and o:IsValid() end
local function log(s) print('[AimModMenu] ' .. tostring(s) .. '\n') end
local function make(name, outer, objectName)
    local object = StaticConstructObject(StaticFindObject('/Script/UMG.' .. name),outer,objectName and FName(objectName) or 0)
    assert(valid(object), 'Could not create ' .. name)
    return object
end
local function live(class)
    for _, object in ipairs(FindAllOf(class) or {}) do
        if valid(object) and valid(object.WidgetTree) and valid(object.WidgetTree.RootWidget)
            and object:GetFullName():find('/Engine/Transient.',1,true) then return object end
    end
end
local function removeOwned(parent, marker)
    for i=parent:GetChildrenCount()-1,0,-1 do
        local child=parent:GetChildAt(i)
        local ok, tip=pcall(function() return child.ToolTipText:ToString() end)
        if (ok and tip==marker) or (marker=='AimMod workspace' and child:GetFName():ToString():find('AimModWorkspace',1,true)==1) then child:RemoveFromParent() end
    end
end
-- A match is starting (the service's play request): close the AimMod panel and KovaaK's menu and
-- give input to the game (game-only input, no cursor, viewport focus with mouse capture), so the
-- player can move and look without clicking in first. A KovaaK's menu the player opened after the
-- start, join or Retry (Escape while loading) is theirs and stays. One hand-back per request id.
local handled,menuShownAt,menuSeen,menuWasUp
local show -- defined below; handBack closes the AimMod panel through it
local function trackMenu()
    local up=valid(menu) and menu:IsVisible()
    -- A new menu object (level load) has no known opening time.
    if menuSeen~=menu then menuSeen=menu;menuWasUp=up;menuShownAt=nil;return false end
    local opening=up and not menuWasUp
    if opening then menuShownAt=os.time() end
    menuWasUp=up
    return opening
end
-- Escape with the CS buy menu open closes the buy menu (the service) and must do nothing else:
-- KovaaK's pause menu, opened by the same key, closes again at once and the game keeps its input.
local function swallowPauseMenu()
    if opened or not valid(menu) or ReplayMainBridge.active() then return false end
    if not trackMenu() or not Notify.swallowMenu() then return false end
    menu:SetVisibility(1);menuWasUp=false;menuShownAt=nil
    local player=menu:GetOwningPlayer()
    local api=StaticFindObject('/Script/Engine.Default__GameplayStatics')
    pcall(function()if valid(player) and valid(api) and api:IsGamePaused(player) then api:SetGamePaused(player,false) end end)
    Notify.restoreInput()
    return true
end
M.swallowPauseMenu=swallowPauseMenu
local function handBack(since)
    if not valid(menu) then return 'no-menu' end
    local player=menu:GetOwningPlayer()
    if not valid(player) then return 'no-player' end
    local up=menu:IsVisible()
    if up and not opened and menuShownAt and since and menuShownAt*1000>since+1000 then return 'kept' end
    if opened then show(false) end
    if up then menu:SetVisibility(1) end
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    local api=StaticFindObject('/Script/Engine.Default__GameplayStatics')
    pcall(function()if valid(api) and api:IsGamePaused(player) then api:SetGamePaused(player,false) end end)
    pcall(function()player.bShowMouseCursor=false end)
    lib:SetInputMode_GameOnly(player)
    return 'played'
end
M.handBack=handBack
function show(value)
    opened=value
    if opened then LiveHUD.hide() end
    if valid(frame) then frame:SetVisibility(opened and 0 or 1) end
    Workspace.update(opened and valid(menu) and menu:IsVisible())
end
function M.attach()
    menu=live('PauseMenu_C'); header=live('PauseBox_C')
    if not valid(menu) or not valid(header) then return false end
    LiveHUD.attach(menu)
    Notify.attach(menu)
    local root=menu.WidgetTree.RootWidget
    if not root:IsA('/Script/UMG.CanvasPanel') then return false end
    -- Build the replacement before removing any existing owned workspace.
    local view=Workspace.create(menu)
    local replacement=make('Border',menu.WidgetTree,'AimModWorkspace_' .. tostring(math.floor(os.clock()*1000)))
    replacement:SetPadding({Left=0,Top=0,Right=0,Bottom=0})
    replacement:SetBrushColor({R=0,G=0,B=0,A=1})
    replacement:SetContent(view)
    removeOwned(root,'AimMod workspace')
    frame=replacement
    -- Roll back a partial attachment so the retry loop rebuilds it instead of
    -- keeping a workspace without its header entry.
    local ok,reason=pcall(attachControls,root)
    if not ok then
        pcall(function() if valid(frame) then frame:RemoveFromParent() end end)
        pcall(function() if valid(entry) then entry:RemoveFromParent() end end)
        frame=nil;entry=nil
        error(reason,0)
    end
    log('AimMod Gameface workspace attached')
    return true
end
attachControls=function(root)
    local slot=root:AddChildToCanvas(frame)
    slot:SetAnchors({Minimum={X=0,Y=0},Maximum={X=1,Y=1}})
    slot:SetOffsets({Left=35,Top=60,Right=35,Bottom=24})
    slot:SetZOrder(9000)
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    local class=StaticFindObject('/Game/FirstPersonBP/Blueprints/UI/Palette/PalettedBoxButtonWidget.PalettedBoxButtonWidget_C')
    local nextEntry=lib:Create(menu,class,menu:GetOwningPlayer())
    assert(valid(nextEntry),'AimMod menu entry unavailable')
    nextEntry:SetPaletteColorType(header.Stats:GetPaletteColorType())
    nextEntry:SetToolTipText(FText('AimMod menu'))
    local label=make('TextBlock',menu.WidgetTree)
    local font=label.Font; font.Size=17
    local manager=FindFirstOf('PaletteManager')
    if valid(manager) and valid(manager.PaletteManagerData) then font.FontObject=manager.PaletteManagerData.BoldFont end
    font.TypefaceFontName=FName('Bold'); label:SetFont(font); label:SetText(FText('AIMMOD'))
    nextEntry.ButtonContentsSlot:SetContent(label)
    local headerButtons=header.Stats:GetParent()
    removeOwned(headerButtons,'AimMod menu'); entry=nextEntry
    local buttonSlot=headerButtons:AddChildToHorizontalBox(entry)
    buttonSlot:SetPadding({Left=8,Top=0,Right=8,Bottom=0}); buttonSlot:SetVerticalAlignment(2)
    texture=StaticFindObject('/Script/Engine.Default__KismetRenderingLibrary'):ImportFileAsTexture2D(menu,assets .. 'aimmod-horizontal.png')
    if valid(texture) then
        for _,name in ipairs({'KovaaKsLogo','kovaaks_logo'}) do
            local logo=header[name]
            if valid(logo) then logo:SetBrushFromTexture(texture,false); logo:SetBrushSize({X=220,Y=60}); logo:SetColorAndOpacity({R=1,G=1,B=1,A=1}) end
        end
    end
    show(false)
    local savedMenuVisibility,savedFrameVisibility,savedOpened
    ReplayMainBridge.attach(menu,function()
        savedMenuVisibility=menu:GetVisibility();savedFrameVisibility=frame:GetVisibility();savedOpened=opened
        LiveHUD.hide();show(false);menu:SetVisibility(1)
    end,function()
        if valid(menu) and savedMenuVisibility~=nil then menu:SetVisibility(savedMenuVisibility)end
        opened=savedOpened or false
        if valid(frame) and savedFrameVisibility~=nil then frame:SetVisibility(savedFrameVisibility)end
        Workspace.update(opened and valid(menu) and menu:IsVisible())
        local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
        if valid(menu) and menu:IsVisible()then
            lib:SetInputMode_UIOnlyEx(menu:GetOwningPlayer(),menu,0)
        elseif valid(menu) then
            -- The replay controls held UI-only input and are now removed. With
            -- the pause menu hidden and the game running, return the player's
            -- ordinary game input mode instead of focusing a removed widget.
            local player=menu:GetOwningPlayer()
            local api=StaticFindObject('/Script/Engine.Default__GameplayStatics')
            if valid(player) and valid(api) and not api:IsGamePaused(player) then lib:SetInputMode_GameOnly(player) end
        end
        savedMenuVisibility=nil;savedFrameVisibility=nil;savedOpened=nil
    end)
end
function M.start()
    RegisterHook('/Script/GameSkillsTrainer.PalettedButtonWidgetNative:Button_NotifyClicked',function() end,function(context)
        -- Game callback: never raise, never override the native result.
        local ok,reason=pcall(function()
            if valid(entry) and context:get():GetFullName()==entry:GetFullName() then show(not opened) end
        end)
        if not ok then log(reason) end
    end)
    -- This runtime dispatches key binds on an input thread that can overlap
    -- the game-thread Lua loop. The native header button remains the entry.
    local ticks,retry,lastError=0,0,nil
    LoopInGameThreadWithDelay(400,function()
        ticks=ticks+1
        if not valid(menu) or not valid(frame) then
            if ticks<retry then return end
            retry=ticks+25
            local ok,result=pcall(M.attach)
            if not ok and result~=lastError then lastError=result; log(result) end
        else
            local ok,reason=pcall(function()
                if Workspace.closeRequested() then show(false) end
                -- Open a page for the service (a Steam join): only in the menu, never
                -- over a running scenario or replay; the request waits until then.
                local page=Workspace.openRequest()
                if page and menu:IsVisible() and not ReplayMainBridge.active() then
                    local snapshot=Telemetry.liveSnapshot()
                    if not (type(snapshot)=='table' and snapshot.active==true) then
                        Workspace.consumeOpenRequest(); show(true); Workspace.openPage(page)
                    end
                end
                Workspace.update(opened and menu:IsVisible())
                Workspace.deliverPage()
            end)
            if not ok and reason~=lastError then lastError=reason; log(reason) end
        end
    end)
    -- Independent of the workspace and replay recording preference. Native
    -- visibility follows the read-only live snapshot on the game thread.
    -- Fast and cheap (flags only unless one applies): KovaaK's pause menu opened by Escape over the
    -- buy menu closes within a frame or two, and a held scoreboard key keeps the viewport focused.
    LoopInGameThreadWithDelay(33,function()
        pcall(function()
            if valid(menu) then swallowPauseMenu() end
            Notify.keepGameFocus()
        end)
    end)
    LoopInGameThreadWithDelay(100,function()
        local ok=pcall(function()
            LiveHUD.update(not valid(menu) or menu:IsVisible() or opened,
                ReplayMainBridge.active(),Telemetry.liveSnapshot())
        end)
        if not ok then pcall(LiveHUD.hide) end
        -- Multiplayer notices, shown while the AimMod panel itself is not on screen.
        local noticeOk=pcall(function()
            swallowPauseMenu();trackMenu()
            local menuUp=valid(menu) and menu:IsVisible()
            Notify.update(opened and menuUp,ReplayMainBridge.active(),menuUp)
            local id,since=Notify.playRequest()
            if id and id~=handled and not ReplayMainBridge.active() then
                handled=id;local okPlay,result=pcall(handBack,since)
                if not okPlay then log(result) end
            end
        end)
        if not noticeOk then pcall(Notify.hide) end
    end)
end
return M
