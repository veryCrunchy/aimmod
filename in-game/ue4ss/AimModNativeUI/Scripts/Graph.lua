-- Reuse the shipped KovaaK's score-history graph in an owned, visible view.
-- GraphData updates this view only; it does not change a game performance model.
local M = {}
local graph, cohtml, lastData
local attempts=0
local nextRead=0
local dataPath=(os.getenv('LOCALAPPDATA') or '') .. '/AimMod/KovaaksNative/graph.json'
function M.create(owner)
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    local class=StaticFindObject('/Game/UI/MetaGraphWidget.MetaGraphWidget_C')
    assert(class:IsValid(), 'KovaaK graph widget is not loaded')
    graph=lib:Create(owner,class,owner:GetOwningPlayer())
    assert(graph:IsValid(), 'KovaaK graph creation failed')
    cohtml=graph:GetCohtmlWidget()
    assert(cohtml:IsValid(), 'KovaaK graph view missing')
    graph:LoadView('sandbox-graph', false)
    lastData=nil; attempts=0; nextRead=0
    print('[AimModGraph] graph created; URL=' .. tostring(cohtml.URL:ToString()) .. '\n')
    return graph
end
function M.update()
    if not cohtml or not cohtml:IsValid() or not cohtml:IsReadyForBindings() then return end
    local now=os.time()
    if now<nextRead then return end
    nextRead=now+1
    local file=io.open(dataPath,'rb')
    if not file then return end
    local data=file:read(65537); file:close()
    if not data or #data>65536 then return end
    if data~=lastData then lastData=data; attempts=0 end
    if attempts>=5 then nextRead=now+5; return end
    -- Readiness for bindings can precede React mounting its event listener.
    -- Retry the initial delivery a bounded number of times after view creation.
    local event=cohtml:CreateJSEvent()
    event:AddString(data)
    cohtml:TriggerJSEvent('GraphData',event)
    attempts=attempts+1
end
return M
