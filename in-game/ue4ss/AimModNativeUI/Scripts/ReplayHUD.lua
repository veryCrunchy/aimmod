-- Native main-viewport replay controls. Owns no gameplay or score state.
local M={}
local routes={}
local hooked=false
local sliderRoutes={}
local sliderHooked=false
local function valid(o)return o and o:IsValid()end
local build
function M.create(owner,send)
    -- A partially built HUD must not leave click routes (and the widgets their
    -- closures reference) registered, or an orphaned frame in the viewport.
    local registered={routes={},sliders={}}
    local ok,result=pcall(build,owner,send,registered)
    if not ok then
        for _,key in ipairs(registered.routes)do routes[key]=nil end
        for _,key in ipairs(registered.sliders)do sliderRoutes[key]=nil end
        pcall(function()if valid(registered.frame)then registered.frame:RemoveFromParent()end end)
        error(result,0)
    end
    return result
end
build=function(owner,send,registered)
    local lib=StaticFindObject('/Script/UMG.Default__WidgetBlueprintLibrary')
    local function widget(name)
        local class=StaticFindObject('/Game/FirstPersonBP/Blueprints/UI/Palette/'..name..'.'..name..'_C')
        local value=lib:Create(owner,class,owner:GetOwningPlayer());assert(valid(value),'replay control unavailable');return value
    end
    local frame=widget('PalettedBorderWidget');registered.frame=frame
    assert(valid(frame.WidgetTree),'replay control tree unavailable')
    local function make(name)
        local value=StaticConstructObject(StaticFindObject('/Script/UMG.'..name),frame.WidgetTree)
        assert(valid(value),'replay control unavailable');return value
    end
    local canvas=make('CanvasPanel');frame.WidgetTree.RootWidget=canvas
    frame.bIsFocusable=true
    frame:SetVisibility(4);canvas:SetVisibility(4)
    local function place(parent,child,x,y,w,h,anchorX,anchorY)
        local slot=parent:AddChildToCanvas(child)
        slot:SetAnchors({Minimum={X=anchorX or 0,Y=anchorY or 0},Maximum={X=anchorX or 0,Y=anchorY or 0}})
        slot:SetAlignment({X=0,Y=0});slot:SetAutoSize(false)
        slot:SetOffsets({Left=x,Top=y,Right=w,Bottom=h});return slot
    end
    local colors={mint={R=.04,G=.82,B=.49,A=1},paper={R=.85,G=.94,B=.90,A=1},muted={R=.42,G=.61,B=.53,A=1},panel={R=.009,G=.024,B=.018,A=.97},line={R=.035,G=.13,B=.08,A=1}}
    local panel=make('Border');panel:SetVisibility(0)
    panel:SetPadding({Left=0,Top=0,Right=0,Bottom=0});panel:SetBrushColor(colors.panel)
    place(canvas,panel,-520,-184,1040,160,.5,1)
    local body=make('CanvasPanel');body:SetVisibility(4);panel:SetContent(body)
    local palette=FindFirstOf('PaletteManager')
    local fontObject=valid(palette) and valid(palette.PaletteManagerData) and palette.PaletteManagerData.BoldFont or nil
    local function text(value,size,color)
        local label=make('TextBlock');local font=label.Font;font.Size=size or 16
        if fontObject then font.FontObject=fontObject end
        font.TypefaceFontName=FName('Bold');label:SetFont(font);label:SetText(FText(value))
        label:SetColorAndOpacity({SpecifiedColor=color or colors.paper,ColorUseRule=0});label:SetVisibility(3)
        return label
    end
    local function line(x,y,w,h,color)
        local b=make('Border');b:SetBrushColor(color);b:SetVisibility(3);place(body,b,x,y,w,h);return b
    end
    line(0,0,1040,3,colors.mint)
    place(body,text('AIMMOD',17,colors.mint),24,19,120,24)
    local status=text('REPLAY PAUSED',11,colors.muted);place(body,status,24,44,140,20)
    local scoreTitle
    local fields={};local keys={'score','accuracy','hits','shots','kills','damage'}
    local labels={'SCORE','ACCURACY','HITS','SHOTS','KILLS','DAMAGE'}
    for i,key in ipairs(keys)do
        local x=192+(i-1)*136
        local title=text(labels[i],10,colors.muted);place(body,title,x,17,126,16)
        if key=='score' then scoreTitle=title end
        fields[key]=text('—',21);place(body,fields[key],x,34,126,30)
    end
    -- Native slider handles pointer capture and DPI; this bar supplies the progress fill.
    local progress=make('ProgressBar');progress:SetVisibility(3);progress:SetPercent(0);progress:SetFillColorAndOpacity(colors.mint)
    place(body,progress,24,75,992,5)
    local buttons={}
    local surfaces={}
    local function updateSurface(surface)
        local native=surface.item.Binding_Button
        local state=not native:GetIsEnabled() and 'disabled' or (native:IsPressed() and 'pressed' or (native:IsHovered() and 'hover' or 'normal'))
        if state~=surface.state then surface.border:SetBrushColor(surface.colors[state]);surface.state=state end
    end
    local function button(value,x,w,callback,primary)
        local item=widget('PalettedBoxButtonWidget');item:SetVisibility(0)
        item.HideTrim=true;item.HoverEffect=false
        assert(valid(item.Binding_Button),'replay button unavailable')
        item.Binding_Button.IsFocusable=false
        -- Own the paint surface; preserve the game's complete native button style,
        -- including its nontrivial Slate sounds and brush resource ownership.
        local surface=make('Border');surface:SetVisibility(3)
        place(body,surface,x,96,w,42)
        local entry={item=item,border=surface,colors={
            normal=primary and colors.mint or {R=.035,G=.095,B=.065,A=1},
            hover=primary and {R=.09,G=1,B=.65,A=1} or {R=.06,G=.18,B=.12,A=1},
            pressed=primary and {R=.02,G=.53,B=.30,A=1} or {R=.02,G=.065,B=.04,A=1},
            disabled={R=.025,G=.04,B=.032,A=.55}}}
        surfaces[#surfaces+1]=entry
        local label=text(value,16,primary and colors.panel or colors.paper);label:SetJustification(1)
        item.ButtonContentsSlot:SetContent(label)
        place(body,item,x,96,w,42)
        local key=item:GetFullName();registered.routes[#registered.routes+1]=key
        routes[key]=function()frame:SetUserFocus(owner:GetOwningPlayer());callback()end;buttons[#buttons+1]=item
        return label
    end
    local play=button('Play',24,112,function()send('toggle')end,true)
    button('-5 sec',148,88,function()send('seek-relative',-5)end)
    button('+5 sec',244,88,function()send('seek-relative',5)end)
    local speedLabel=button('1x speed',348,112,function()send('speed-next')end)
    local timer=text('0:00 / 0:00',20);place(body,timer,492,94,240,28)
    local remaining=text('0:00 remaining',11,colors.muted);place(body,remaining,492,122,240,20)
    button('Exit replay',864,152,function()send('close')end)
    local seek=widget('PalettedSlider');seek:SetVisibility(0)
    local slider=seek.Binding_Slider;assert(valid(slider),'replay timeline unavailable')
    slider.IsFocusable=false
    place(body,seek,24,64,992,27)
    local clock=StaticFindObject('/Script/Engine.Default__GameplayStatics')
    local seekDuration=0;local dragFraction;local syncingSlider=false
    local lastSeekTime=-math.huge;local pendingSeekValue;local lastSliderValue
    local function flushSeek()
        if not pendingSeekValue or seekDuration<=0 then return end
        local now=clock:GetRealTimeSeconds(owner)
        if type(now)~='number' or now~=now or now==math.huge then return end
        if now-lastSeekTime>=.1 or now<lastSeekTime then
            local value=pendingSeekValue;pendingSeekValue=nil
            send('seek',value);lastSeekTime=now
        end
    end
    local seekKey=seek:GetFullName();registered.sliders[#registered.sliders+1]=seekKey
    sliderRoutes[seekKey]=function(value)
        if syncingSlider or seekDuration<=0 or type(value)~='number' or value~=value or math.abs(value)==math.huge then return end
        dragFraction=math.max(0,math.min(1,value))
        pendingSeekValue=dragFraction*seekDuration
        flushSeek()
    end
    if not sliderHooked then
        sliderHooked=true
        -- This is the shipped slider's already-bound native notification, not a
        -- reconstructed delegate or a guessed Blueprint callback.
        RegisterHook('/Script/GameSkillsTrainer.PalettedSliderNative:Slider_NotifyValueChanged',function()return nil end,function(context,value)
            pcall(function()
                local callback=sliderRoutes[context:get():GetFullName()]
                if callback then callback(type(value)=='number' and value or value:get())end
            end)
            return nil
        end)
    end
    local markerEnabled=true;local markerDuration=0;local markerTexture,markerColor
    local markerWidth,markerHeight=32,36
    local regularTexture,regularWidth,regularHeight,regularColor
    pcall(function()
        local character=owner:GetOwningPlayer().MyCharacter;assert(valid(character))
        local weapon=character:GetCurrentWeapon();assert(valid(weapon))
        local crosshair=weapon.WeaponSettingsNative.Crosshair
        if crosshair.bHitmarkers==false then markerEnabled=false end
        local duration=crosshair.HitmarkerTime
        if type(duration)=='number' and duration==duration and duration>=0 and duration<=10 then markerDuration=duration end
        local color=crosshair.HitmarkerColor
        local function finite(v)return type(v)=='number' and v==v and math.abs(v)<1e12 end
        if color and finite(color.X) and finite(color.Y) and finite(color.Z)then markerColor={R=color.X,G=color.Y,B=color.Z,A=1}end
        local regular=weapon.CachedMainXHair
        local tint=crosshair.Color
        -- A zero scale or missing cache represents no visible local crosshair.
        -- Otherwise consume cached dimensions without guessing a second scale.
        if regular and valid(regular.CrosshairTexture) and finite(regular.Width) and finite(regular.Height) and
            regular.Width>0 and regular.Height>0 and regular.Width<=2048 and regular.Height<=2048 and
            (crosshair.Scale==nil or (finite(crosshair.Scale) and crosshair.Scale>0)) and
            tint and finite(tint.X) and finite(tint.Y) and finite(tint.Z)then
            regularTexture=regular.CrosshairTexture;regularWidth=regular.Width;regularHeight=regular.Height
            regularColor={R=tint.X,G=tint.Y,B=tint.Z,A=1}
        end
        local cache=weapon.CachedMainHitmarkers
        if cache and valid(cache.CrosshairTexture) and finite(cache.Width) and finite(cache.Height) and
            cache.Width>0 and cache.Height>0 and cache.Width<=2048 and cache.Height<=2048 then
            markerTexture=cache.CrosshairTexture;markerWidth=cache.Width;markerHeight=cache.Height
        end
    end)
    if regularTexture then
        local crosshairImage=make('Image');crosshairImage:SetBrushFromTexture(regularTexture,false)
        crosshairImage:SetColorAndOpacity(regularColor);crosshairImage:SetVisibility(3)
        place(canvas,crosshairImage,-regularWidth/2,-regularHeight/2,regularWidth,regularHeight,.5,.5)
    end
    local hitMarker
    if markerTexture then
        hitMarker=make('Image');hitMarker:SetBrushFromTexture(markerTexture,false)
        hitMarker:SetColorAndOpacity(markerColor or {R=1,G=1,B=1,A=1})
    else
        hitMarker=text('X',26,markerColor or colors.mint);hitMarker:SetJustification(1)
    end
    hitMarker:SetVisibility(2)
    -- Cached dimensions are consumed directly; applying guessed scale again can double-size the skin.
    place(canvas,hitMarker,-markerWidth/2,-markerHeight/2,markerWidth,markerHeight,.5,.5)
    if not hooked then
        hooked=true
        RegisterHook('/Script/GameSkillsTrainer.PalettedButtonWidgetNative:Button_NotifyClicked',function()return nil end,function(context)
            pcall(function()local callback=routes[context:get():GetFullName()];if callback then callback()end end)
            return nil
        end)
    end
    frame:AddToViewport(10000)
    frame.bIsFocusable=true -- Reapply after Blueprint Construct before requesting Slate focus.
    slider.IsFocusable=false
    -- Blueprint construction runs during attachment; apply our states afterward.
    for _,surface in ipairs(surfaces)do
        surface.item.Binding_Button.IsFocusable=false
        surface.item.Binding_Button:SetBackgroundColor({R=1,G=1,B=1,A=0})
        surface.item.Binding_Button:SetColorAndOpacity({R=1,G=1,B=1,A=1})
        if valid(surface.item.Trim)then surface.item.Trim:SetVisibility(1)end
        updateSurface(surface)
    end
    slider:SetMinValue(0);slider:SetMaxValue(1);slider:SetStepSize(.001)
    slider:SetLocked(false);slider:SetIndentHandle(false)
    slider:SetSliderBarColor({R=0,G=0,B=0,A=0});slider:SetSliderHandleColor(colors.mint)
    lib:SetInputMode_UIOnlyEx(owner:GetOwningPlayer(),frame,0)
    local closed=false;local cached={};local lastHit,lastProgress
    local markerUntil,previousMarkerTime,previousTransport
    -- Do not enter this shared Lua VM through RegisterKeyBind on this UE4SS build.
    -- Its input-thread lock is not held by the engine-tick Lua callback executor.
    -- Native button/slider callbacks stay on the game thread.
    local function write(label,value)if cached[label]~=value then label:SetText(FText(value));cached[label]=value end end
    local function stamp(n)return string.format('%d:%02d',math.floor(n/60),math.floor(n%60))end
    local function known(n)return type(n)=='number' and n==n and n>=0 and n<1e12 end
    local function metric(n,allowNegative,precise)if not (known(n) or (allowNegative and type(n)=='number' and n==n and n<0 and n>-1e12))then return '—'end;if precise and n>0 and n<1 then return string.format('%.4g',n)end;return string.format('%.1f',n):gsub('%.0$','')end
    local function refreshControls()
        if closed then return end
        for _,surface in ipairs(surfaces)do updateSurface(surface)end
        flushSeek()
        if not slider:HasMouseCapture() and not pendingSeekValue then dragFraction=nil end
    end
    return {view=frame,refreshControls=refreshControls,update=function(state,stats,feedback,transport,resultScore)
        if closed then return end
        local time=known(state.time) and state.time or 0;local duration=known(state.duration) and state.duration or 0
        seekDuration=duration
        refreshControls()
        write(play,state.playing and 'Pause' or 'Play')
        write(status,state.playing and 'REPLAY PLAYING' or (duration>0 and time>=duration and 'REPLAY FINISHED' or 'REPLAY PAUSED'))
        write(speedLabel,tostring(state.speed)..'x speed')
        write(timer,stamp(time)..' / '..stamp(duration));write(remaining,stamp(math.max(0,duration-time))..' remaining')
        -- Quantize to track width, avoiding redundant Slate updates.
        local percent=dragFraction or (duration>0 and math.min(1,time/duration) or 0)
        percent=math.floor(percent*992+.5)/992
        if percent~=lastProgress then progress:SetPercent(percent);lastProgress=percent end
        if not slider:HasMouseCapture() and pendingSeekValue==nil and percent~=lastSliderValue then
            syncingSlider=true;slider:SetValue(percent);syncingSlider=false;lastSliderValue=percent
        end
        stats=stats or {};local accuracy='—'
        if known(stats.hits) and known(stats.shots) and stats.shots>0 then accuracy=string.format('%.1f%%',stats.hits/stats.shots*100)end
        local score=stats.score
        local function scoreKnown(n)return type(n)=='number' and n==n and math.abs(n)<1e12 end
        local useResult=not scoreKnown(score) and scoreKnown(resultScore)
        if useResult then score=resultScore end
        write(scoreTitle,useResult and 'FINAL SCORE' or 'SCORE')
        for _,key in ipairs(keys)do write(fields[key],key=='accuracy' and accuracy or metric(key=='score' and score or stats[key],key=='score',key=='damage'))end
        local changed=previousTransport~=nil and transport~=previousTransport
        local backwards=previousMarkerTime~=nil and time<previousMarkerTime
        local continuous=type(transport)=='number' and transport==transport and transport>=0 and transport==math.floor(transport)
        if changed or backwards or not state.playing then markerUntil=nil end
        if markerEnabled and feedback==true and state.playing and not changed and not backwards then
            markerUntil=time+(continuous and markerDuration or 0)
        end
        local hit=markerEnabled and state.playing and markerUntil~=nil and time<=markerUntil and
            (feedback==true or (continuous and markerDuration>0 and time<markerUntil))
        if hit~=lastHit then hitMarker:SetVisibility(hit and 3 or 2);lastHit=hit end
        previousMarkerTime=time;previousTransport=continuous and transport or nil
    end,close=function()
        if closed then return end;closed=true;dragFraction=nil;seekDuration=0;pendingSeekValue=nil
        sliderRoutes[seek:GetFullName()]=nil
        for _,item in ipairs(buttons)do if valid(item)then routes[item:GetFullName()]=nil end end
        if valid(frame)then frame:RemoveFromParent()end
    end}
end
return M
