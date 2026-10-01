-- Owned Gameface view. The worker supplies AimMod's UI; game stats views and
-- their user settings are never changed to select a chart tab.
local M = {}
local view, cohtml, loadedUrl, nextRead, lastVisible, deliveries
local base=(os.getenv('LOCALAPPDATA') or '') .. '/AimMod/KovaaksNative/'
local function url()
    local file=io.open(base .. 'workspace-url.txt','rb')
    if not file then return nil end
    local value=file:read(512); file:close()
    if value and value:match('^http://127%.0%.0%.1:%d+/%x+/ui$') then return value end
end
function M.create(owner)
    -- Check the worker before constructing widgets: attach retries while the
    -- worker is stopped must not create an orphaned view every retry.
    assert(url(), 'Start the AimMod native worker to load the workspace')
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    local class=StaticFindObject('/Game/UI/MetaGraphWidget.MetaGraphWidget_C')
    assert(class:IsValid(), 'KovaaK stats view unavailable')
    view=lib:Create(owner,class,owner:GetOwningPlayer())
    assert(view:IsValid(), 'Workspace creation failed')
    cohtml=view:GetCohtmlWidget()
    assert(cohtml:IsValid(), 'Workspace renderer unavailable')
    loadedUrl=url()
    assert(loadedUrl, 'Start the AimMod native worker to load the workspace')
    cohtml:Load(loadedUrl)
    nextRead=0; lastVisible=nil; deliveries=0
    return view
end
function M.update(visible)
    if not cohtml or not cohtml:IsValid() then return end
    local now=os.time()
    if lastVisible~=visible then lastVisible=visible; deliveries=0 end
    if now<(nextRead or 0) then return end
    nextRead=now+1
    local current=url()
    if current and current~=loadedUrl then
        loadedUrl=current; cohtml:Load(current); deliveries=0
    end
    if deliveries<4 and cohtml:IsReadyForBindings() then
        local event=cohtml:CreateJSEvent()
        event:AddBool(visible)
        cohtml:TriggerJSEvent('AimModVisibility',event)
        deliveries=deliveries+1
    end
end
-- The service asks to show a page (e.g. Multiplayer after a Steam join).
-- Returns the page name; the request stays until the caller consumes it.
function M.openRequest()
    local file=io.open(base .. 'open-workspace.request','rb')
    if not file then return nil end
    local value=file:read(64); file:close()
    if value and value:match('^%a+$') then return value end
    os.remove(base .. 'open-workspace.request')
end
function M.consumeOpenRequest() os.remove(base .. 'open-workspace.request') end
local pendingPage, pageDeliveries
function M.openPage(page) pendingPage=page; pageDeliveries=0 end
-- Delivered a few times, like visibility, until the page is ready for bindings.
local function deliverPage()
    if not pendingPage or not cohtml or not cohtml:IsValid() or not cohtml:IsReadyForBindings() then return end
    local event=cohtml:CreateJSEvent()
    event:AddString(pendingPage)
    cohtml:TriggerJSEvent('AimModOpenPage',event)
    pageDeliveries=pageDeliveries+1
    if pageDeliveries>=3 then pendingPage=nil end
end
M.deliverPage=deliverPage
function M.closeRequested()
    local path=base .. 'close.request'
    local file=io.open(path,'rb')
    if not file then return false end
    file:close(); os.remove(path); return true
end
return M
