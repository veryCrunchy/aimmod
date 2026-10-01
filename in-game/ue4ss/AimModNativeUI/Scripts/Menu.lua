local M = {}
local Workspace = require('Workspace')
local ReplayMainBridge = require('ReplayMainBridge')
local LiveHUD = require('LiveHUD')
local Notify = require('Notify')
local Telemetry = require('Telemetry')
local menu, header, frame, entry, texture
local opened = false
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
local function show(value)
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
    local slot=root:AddChildToCanvas(frame)
    slot:SetAnchors({Minimum={X=0,Y=0},Maximum={X=1,Y=1}})
    slot:SetOffsets({Left=35,Top=106,Right=35,Bottom=24})
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
        if valid(menu) and menu:IsVisible()then
            StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary'):SetInputMode_UIOnlyEx(menu:GetOwningPlayer(),menu,0)
        end
        savedMenuVisibility=nil;savedFrameVisibility=nil;savedOpened=nil
    end)
    log('AimMod Gameface workspace attached')
    return true
end
function M.start()
    RegisterHook('/Script/GameSkillsTrainer.PalettedButtonWidgetNative:Button_NotifyClicked',function() end,function(context)
        if valid(entry) and context:get():GetFullName()==entry:GetFullName() then
            local ok,reason=pcall(show,not opened)
            if not ok then log(reason) end
        end
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
    LoopInGameThreadWithDelay(100,function()
        local ok=pcall(function()
            LiveHUD.update(not valid(menu) or menu:IsVisible() or opened,
                ReplayMainBridge.active(),Telemetry.liveSnapshot())
        end)
        if not ok then pcall(LiveHUD.hide) end
        -- Multiplayer notices, shown while the AimMod panel itself is not on screen.
        local noticeOk=pcall(function()
            Notify.update(opened and valid(menu) and menu:IsVisible(),ReplayMainBridge.active())
        end)
        if not noticeOk then pcall(Notify.hide) end
    end)
end
return M
