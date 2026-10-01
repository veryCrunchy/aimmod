-- Owned, read-only Gameface surface. Call only from the native game thread.
-- No input mode, focus, key callbacks, gameplay state or score writes.
local M={}
local owner,host,view,renderer,loadedUrl
local visible=false
local enabled=false
local nextRead=0
local nextDelivery=0
local deliveries=0
local nextCreate=0
local lastGoodTime,lastGoodId
local realClock
local base=(os.getenv('LOCALAPPDATA') or '')..'/AimMod/KovaaksNative/'
local function valid(value)return value and value:IsValid()end
local function realTime()
    local ok,value=pcall(function()
        if not valid(realClock)then realClock=StaticFindObject('/Script/Engine.Default__GameplayStatics')end
        return realClock:GetRealTimeSeconds(owner)
    end)
    if ok and type(value)=='number' and value==value and value>=0 and value<1e12 then return value end
end
local function read(name,limit)
    local file=io.open(base..name,'rb');if not file then return nil end
    local ok,value=pcall(function()return file:read(limit)end);file:close()
    return ok and value or nil
end
local function settingEnabled(text)
    if not text or #text>4096 then return false end
    -- The owned settings store always writes this complete canonical shape.
    -- Anchoring the full record rejects truncation, duplicate keys and strings
    -- containing a misleading gameEnabled field without needing a JSON runtime.
    local n='([-+%deE.]+)'
    local placement='{"visible":(%a+),"x":'..n..',"y":'..n..',"width":'..n..'}'
    local game,obs,opacity,sv,sx,sy,sw,vv,vx,vy,vw=text:match(
        '^{"gameEnabled":(%a+),"obsEnabled":(%a+),"opacity":'..n..',"stats":'..placement..',"versus":'..placement..'}$')
    local function boolean(v)return v=='true' or v=='false'end
    local function range(v,lo,hi)
        if not v then return false end
        local mantissa=v:match('^(.+)[eE][+-]?%d+$') or v
        if not (mantissa:match('^%-?%d+$') or mantissa:match('^%-?%d+%.%d+$'))
            or mantissa:match('^%-?0%d') then return false end
        local x=tonumber(v);return x and x==x and x>=lo and x<=hi
    end
    return game=='true' and boolean(obs) and boolean(sv) and boolean(vv)
        and range(opacity,0,1) and range(sx,0,100) and range(sy,0,100) and range(sw,140,600)
        and range(vx,0,100) and range(vy,0,100) and range(vw,140,600) or false
end
local function notify(now)
    if not valid(renderer) or deliveries>=4 or now<nextDelivery then return end
    if renderer:IsReadyForBindings() then
        local event=renderer:CreateJSEvent();event:AddBool(visible)
        renderer:TriggerJSEvent('AimModVisibility',event)
        deliveries=deliveries+1;nextDelivery=now+1
    end
end
function M.hide()
    lastGoodTime=nil;lastGoodId=nil
    if valid(host) then host:SetVisibility(1) end
    if visible then visible=false;deliveries=0;nextDelivery=0 end
    notify(os.time())
end
function M.close()
    M.hide()
    if valid(host) then host:RemoveFromParent() end
    host=nil;view=nil;renderer=nil;loadedUrl=nil;visible=false
end
function M.attach(value)
    if owner==value and valid(owner) then return end
    M.close();owner=value;nextRead=0;nextCreate=0;enabled=false
end
local function create(url)
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    host=lib:Create(owner,StaticFindObject('/Game/FirstPersonBP/Blueprints/UI/Palette/PalettedBorderWidget.PalettedBorderWidget_C'),owner:GetOwningPlayer())
    assert(valid(host) and valid(host.WidgetTree),'live HUD host unavailable')
    local canvas=StaticConstructObject(StaticFindObject('/Script/UMG.CanvasPanel'),host.WidgetTree)
    assert(valid(canvas),'live HUD canvas unavailable')
    -- Replace the palette root entirely: no inherited background or padding.
    host.WidgetTree.RootWidget=canvas;canvas:SetVisibility(3)
    host.bIsFocusable=false;host:SetVisibility(1)
    view=lib:Create(owner,StaticFindObject('/Game/UI/MetaGraphWidget.MetaGraphWidget_C'),owner:GetOwningPlayer())
    assert(valid(view),'live HUD view unavailable')
    view.bIsFocusable=false;view:SetVisibility(3)
    renderer=view:GetCohtmlWidget();assert(valid(renderer),'live HUD renderer unavailable')
    renderer.bReceiveInput=false;renderer:SetVisibility(3)
    local slot=canvas:AddChildToCanvas(view)
    slot:SetAnchors({Minimum={X=0,Y=0},Maximum={X=1,Y=1}})
    slot:SetAlignment({X=0,Y=0});slot:SetAutoSize(false)
    slot:SetOffsets({Left=0,Top=0,Right=0,Bottom=0})
    host:AddToViewport(5000)
    -- Blueprint Construct can reset properties; reassert noninteraction after it.
    host.bIsFocusable=false;view.bIsFocusable=false
    host:SetVisibility(1);view:SetVisibility(3);renderer:SetVisibility(3);renderer.bReceiveInput=false
    renderer:Load(url);loadedUrl=url;deliveries=0;nextDelivery=0
end
-- native: AimModCore hosts the in-game HUD view (core-active.tsv lists "hud"). This view then
-- stands down: removed from the screen, nothing created. Logged when the decision changes.
local hostDecision
function M.update(menuVisible,replayActive,snapshot,native)
    if not valid(owner) then M.close();owner=nil;return end
    if (native==true)~=hostDecision then
        hostDecision=native==true
        print('[AimModLiveHUD] '..(hostDecision and 'standing down: AimModCore hosts the in-game HUD (core-active.tsv lists hud)' or 'hosting the in-game HUD here (AimModCore does not list hud)')..'\n')
    end
    if native==true then
        if valid(host) then M.close() end
        return
    end
    local now=os.time()
    local url=loadedUrl
    if now>=nextRead then
        nextRead=now+1
        -- Missing or malformed settings fail closed; no capture setting dependency.
        enabled=settingEnabled(read('overlay-settings.json',4097))
        url=read('live-overlay-url.txt',513)
        if not url or not url:match('^http://127%.0%.0%.1:%d+/%x+/overlay%?surface=game$') then enabled=false;url=nil end
        if valid(renderer) and url and url~=loadedUrl then
            M.hide();renderer:Load(url);loadedUrl=url;deliveries=0;nextDelivery=0
        end
    end
    local show=enabled and menuVisible==false and replayActive==false
        and type(snapshot)=='table' and snapshot.active==true and snapshot.paused==false
    local observedTime
    if show then
        local stamp=realTime();observedTime=stamp
        if snapshot.transient==true then
            -- Preserve only an already displayed, independently active run.
            -- RealTimeSeconds is wall time, unlike Lua os.clock (CPU time) or
            -- the paused challenge clock. Repeated failures never renew grace.
            show=visible and stamp~=nil and lastGoodTime~=nil and stamp>=lastGoodTime
                and stamp-lastGoodTime<0.2 and type(snapshot.id)=='string' and snapshot.id==lastGoodId
        end
    end
    if not show then M.hide();return end
    if not valid(host) or not valid(view) or not valid(renderer) then
        if now<nextCreate then return end
        M.close()
        if not url then return end
        nextCreate=now+5
        local ok=pcall(create,url)
        if not ok then M.close();return end
        nextCreate=0
    end
    if snapshot.transient~=true then lastGoodTime=observedTime;lastGoodId=snapshot.id end
    if not visible then visible=true;deliveries=0;nextDelivery=0 end
    host:SetVisibility(3);notify(now)
end
return M
