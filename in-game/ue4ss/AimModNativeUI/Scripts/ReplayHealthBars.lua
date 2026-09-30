-- Actual shipped Healthbar_C hosted by a native WidgetComponent on the owned
-- replay target. Owner stays nil: the Blueprint otherwise binds live character
-- health delegates. Only its presentation receives recorded percent values.
local M={}
local function valid(o)local ok,v=pcall(function()return o:IsValid()end);return ok and v end
local function finite(n)return type(n)=='number' and n==n and math.abs(n)<1e9 end
function M.create(owner,scene)
    local s={closed=false};local bars={};local settings,api,lib,world,pc;local blocked=false;local reported=false;local traced=false;local firstUpdate=false
    local function report(err)if not reported then reported=true;print('[AimMod] Native replay healthbar unavailable: '..tostring(err))end end
    local initialized,initializeError=pcall(function()
        settings=StaticFindObject('/Script/GameSkillsTrainer.Default__MetaGameUserSettings'):Get()
        api=StaticFindObject('/Script/Engine.Default__GameplayStatics');lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
        world=owner:GetWorld();pc=api:GetPlayerController(owner,0)
    end)
    if not initialized then report(initializeError)end
    pcall(function()
        local instance=api:GetGameInstance(owner)
        local function inspect(manager)
            if not valid(manager)then manager=manager:get()end
            if valid(manager) and manager:GetOuter():GetAddress()==instance:GetAddress()then
                blocked=manager:GetCurrentScenario():GetChallengeProfile().BlockHealthbars==true
            end
        end
        local managers=FindAllOf('ScenarioManager') or {}
        if type(managers)=='table' and not managers.ForEach then for _,manager in ipairs(managers)do inspect(manager)end
        else managers:ForEach(function(_,manager)inspect(manager)end)end
    end)
    local function remove(b)
        if valid(b.actor) and valid(b.component)then pcall(function()b.actor:K2_DestroyComponent(b.component)end)end
        if valid(b.widget)then pcall(function()b.widget:RemoveFromParent()end)end
    end
    function s.close()
        if s.closed then return end;s.closed=true
        for _,b in pairs(bars)do remove(b)end;bars={}
    end
    local function create(p)
        assert(valid(p.actor) and p.health and valid(p.health.source),'native target healthbar unavailable')
        local trace=not traced;traced=true
        local function stage(value)if trace then print('[AimMod] Native replay healthbar stage: '..value..'\n')end end
        local source=p.health.source
        stage('create-widget')
        local widget=lib:Create(owner,StaticFindObject('/Game/FirstPersonBP/Blueprints/Bodies/Healthbar.Healthbar_C'),pc)
        assert(valid(widget),'native healthbar widget unavailable')
        local b={actor=p.actor,widget=widget}
        local ok,err=pcall(function()
            -- Never assign Owner/SetOwningCharacter or call HandleHealthChanged.
            assert(not valid(widget.Owner),'unexpected live native healthbar owner')
            stage('add-component')
            local component=p.actor:AddComponentByClass(StaticFindObject('/Script/UMG.WidgetComponent'),false,p.health.relative,true)
            b.component=component;assert(valid(component),'native healthbar component unavailable')
            component:SetComponentTickEnabled(false)
            component:SetCollisionEnabled(0);component:SetSimulatePhysics(false)
            stage('copy-component-style')
            -- Copy POD values instead of passing borrowed reflected return
            -- storage into a second native invocation.
            local size=source:GetDrawSize();local width,height=size.X,size.Y
            local pivot=source:GetPivot();local pivotX,pivotY=pivot.X,pivot.Y
            assert(finite(width) and finite(height) and width>0 and height>0 and width<=16384 and height<=16384,'invalid native healthbar draw size')
            assert(finite(pivotX) and finite(pivotY),'invalid native healthbar pivot')
            component:SetWidgetSpace(source:GetWidgetSpace());component:SetDrawSize({X=width,Y=height})
            component:SetPivot({X=pivotX,Y=pivotY});component:SetDrawAtDesiredSize(source:GetDrawAtDesiredSize())
            component:SetTwoSided(source:GetTwoSided());component:SetWindowFocusable(false)
            component:SetOwnerPlayer(source:GetOwnerPlayer());component:SetTickableWhenPaused(true)
            stage('attach-widget')
            component:SetWidget(widget)
            stage('finish-component')
            p.actor:FinishAddComponent(component,false,p.health.relative)
            stage('apply-native-palette')
            widget:SetPaletteColorType(5) -- game's HudEnemyHealthBar palette
            widget:SetProfileName(p.profile)
            assert(valid(widget.PalettedProgressBar),'native healthbar binding unavailable')
            component:SetComponentTickEnabled(true)
            stage('ready')
        end)
        if not ok then remove(b);error(err)end
        return b
    end
    function s.update(data)
        if s.closed then return end
        local safe=pcall(function()assert(valid(owner) and valid(world) and valid(settings) and valid(pc));assert(owner:GetWorld():GetAddress()==world:GetAddress() and pc:GetWorld():GetAddress()==world:GetAddress())end)
        if not safe then s.close();return end
        local seen={}
        local ok,err=pcall(function()
            local show=not blocked and settings:GetBooleanUserSetting(6)==true
            local autoHide=settings:GetBooleanUserSetting(32)==true
            local showProfile=settings:GetBooleanUserSetting(43)==true
            local count=0
            for _,h in ipairs(data.health or {})do
                count=count+1;if count>128 then break end
                local p=scene and scene.presentation(h.id)
                if show and p and p.health and finite(h.percent) and h.percent>=0 and h.percent<=1 then
                    local b=bars[h.id]
                    if b and (not valid(b.actor) or not valid(b.component) or not valid(b.widget)
                        or not valid(b.widget.PalettedProgressBar) or b.actor:GetAddress()~=p.actor:GetAddress())then
                        remove(b);bars[h.id]=nil;b=nil
                    end
                    if not b then b=create(p);bars[h.id]=b end
                    b.widget.bShowProfileName=showProfile
                    if valid(b.widget.ProfileName)then b.widget.ProfileName:SetVisibility(showProfile and 0 or 1)end
                    b.widget.PalettedProgressBar:SetPercent(h.percent)
                    b.widget:SetVisibility(autoHide and h.percent>=1 and 2 or 4)
                    b.component:RequestRedraw();seen[h.id]=true
                    if not firstUpdate then firstUpdate=true;print('[AimMod] Native replay healthbar stage: first-update\n')end
                end
            end
        end)
        if not ok then report(err)end
        for id,b in pairs(bars)do if not ok or not seen[id]then remove(b);bars[id]=nil end end
    end
    return s
end
return M
