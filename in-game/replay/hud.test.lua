-- Synthetic ownership, layout and interaction contract; live Slate rendering is
-- verified separately in the game. No private recordings or game paths.
local checks=0
local function check(value,message)assert(value,message);checks=checks+1 end
local function obj(t)t=t or {};t.IsValid=function()return true end;return t end
local owner=obj({WidgetTree=obj()});local player=obj();owner.GetOwningPlayer=function()return player end
local created,constructed,commands={}, {}, {}
local clicked,sliderChanged,hookCount,focused
local pointerX,geometryWidth,realTime=496,992,0
RegisterKeyBind=function()error('input-thread Lua callbacks are unsafe in the current runtime')end
local function slot()
    local s={}
    for _,name in ipairs({'Padding','VerticalAlignment','Anchors','Alignment','AutoSize','Offsets'})do
        s['Set'..name]=function(self,value)self[name]=value end
    end
    return s
end
local function base(t)
    t=obj(t);t.SetVisibility=function(self,value)self.visibility=value end
    return t
end
local lib=obj({Create=function(_,context,class,controller)
    check(context==owner and controller==player,'native widgets use owning player')
    local item=base({class=class,WidgetTree=obj(),children={}})
    item.ContentsSlot={SetContent=function(_,value)item.content=value end}
    item.ButtonContentsSlot=item.ContentsSlot
    item.Trim=base({})
    item.Binding_Button=obj({
        SetStyle=function()error('native Slate styles must never be reconstructed from partial Lua tables')end,
        GetIsEnabled=function(self)return self.enabled~=false end,
        IsPressed=function(self)return self.pressed==true end,
        HasMouseCapture=function(self)return self.pressed==true and self.captured~=false end,
        IsHovered=function(self)return self.hovered==true end,
        GetCachedGeometry=function()return {width=geometryWidth}end,
        SetBackgroundColor=function(self,value)self.background=value end,
        SetColorAndOpacity=function(self,value)self.color=value end
    })
    item.Binding_Slider=obj({
        HasMouseCapture=function(self)return self.captured==true end,
        SetValue=function(self,value)self.value=value;if sliderChanged then sliderChanged({get=function()return item end},{get=function()return value end})end end
    })
    for _,field in ipairs({'MinValue','MaxValue','StepSize','Locked','IndentHandle','SliderBarColor','SliderHandleColor'})do item.Binding_Slider['Set'..field]=function(self,value)self[field]=value end end
    local name='SyntheticControl'..(#created+1)
    item.GetFullName=function()return name end
    item.AddToViewport=function(self,z)
        check(self.WidgetTree.RootWidget.class=='CanvasPanel','canvas installed before viewport attachment')
        check(z==10000,'replay controls above game UI');self.attached=true;self.bIsFocusable=false -- Simulate Blueprint Construct resetting focus.
    end
    item.RemoveFromParent=function(self)self.removed=(self.removed or 0)+1 end
    item.IsInViewport=function(self)return self.attached and not self.removed end
    item.HasAnyUserFocus=function(self)return focused==self end
    item.HasUserFocusedDescendants=function(self,controller)return self.focusedDescendant==true end
    item.SetUserFocus=function(self,controller)check(controller==player,'focus assigned to owning replay player');focused=self end
    item.GetVisibility=function(self)return self.visibility end
    -- Deliberately no viewport position/anchor/desired-size APIs in this mock.
    created[#created+1]=item;return item
end,SetInputMode_UIOnlyEx=function(_,controller,frame,lock)
    check(controller==player and frame.attached and lock==0,'native controls get UI-only focus');focused=frame
end})
StaticFindObject=function(path)
    if path:find('Default__WidgetBlueprintLibrary',1,true)then return lib end
    if path:find('Default__WidgetLayoutLibrary',1,true)then return {GetMousePositionOnPlatform=function()return {X=pointerX,Y=0}end}end
    if path:find('Default__SlateBlueprintLibrary',1,true)then return {
        GetLocalSize=function(_,g)return {X=g.width,Y=25}end,
        AbsoluteToLocal=function(_,g,p)return p end}end
    if path:find('Default__GameplayStatics',1,true)then return {GetRealTimeSeconds=function()return realTime end}end
    return path:match('%.([^%.]+)$')
end
StaticConstructObject=function(class,outer)
    local item=base({class=class,outer=outer,Font={},writes=0})
    item.SetFont=function(self,v)self.Font=v end
    item.SetBrushFromTexture=function(self,v,matchSize)self.texture=v;self.matchSize=matchSize end
    item.SetText=function(self,v)self.text=v;self.writes=self.writes+1 end
    item.SetJustification=function(self,v)self.justification=v end
    item.SetColorAndOpacity=function(self,v)self.color=v end
    item.SetBrushColor=function(self,v)self.color=v;self.paintWrites=(self.paintWrites or 0)+1 end
    item.SetPadding=function(self,v)self.padding=v end
    item.SetContent=function(self,v)self.content=v end
    item.SetPercent=function(self,v)self.percent=v;self.percentWrites=(self.percentWrites or 0)+1 end
    item.SetFillColorAndOpacity=function(self,v)self.fill=v end
    item.AddChildToCanvas=function(self,child)self.children=self.children or {};self.children[#self.children+1]=child;child.slot=slot();return child.slot end
    item.AddChildToHorizontalBox=function(self,child)return slot()end
    item.AddChildToVerticalBox=function(self,child)self.children=self.children or {};self.children[#self.children+1]=child;return slot()end
    constructed[#constructed+1]=item;return item
end
FindFirstOf=function()return nil end
FName=function(s)return s end;FText=function(s)return s end
RegisterHook=function(path,pre,post)hookCount=(hookCount or 0)+1;if path:find('Slider_NotifyValueChanged',1,true)then sliderChanged=post else clicked=post end;check(pre()==nil,'hook never overrides game return')end
local M=dofile('../ue4ss/AimModNativeUI/Scripts/ReplayHUD.lua')
local hud=M.create(owner,function(action,value)commands[#commands+1]={action,value}end)
local frame=created[1];local canvas=frame.WidgetTree.RootWidget;local panel=canvas.children[1];local body=panel.content
check(hud.view==frame and focused==frame,'returned view is fullscreen host')
check(panel.class=='Border' and body.class=='CanvasPanel','custom native panel has explicit canvas layout')
check(canvas.visibility==4 and frame.visibility==4 and panel.visibility==0,'fullscreen containers preserve child hit testing')
check(panel.slot.Anchors.Minimum.X==.5 and panel.slot.Anchors.Maximum.Y==1,'panel anchored bottom center')
check(panel.slot.Offsets.Left==-520 and panel.slot.Offsets.Top==-184 and panel.slot.Offsets.Right==1040 and panel.slot.Offsets.Bottom==160,'panel has explicit size and bottom margin')
check(panel.slot.Alignment.X==0 and panel.slot.AutoSize==false,'offsets use unambiguous fixed-size slot')
local marker=canvas.children[2]
check(marker.visibility==2 and marker.slot.Anchors.Minimum.X==.5 and marker.slot.Anchors.Minimum.Y==.5,'confirmed-hit marker starts hidden at screen center')
for _,item in ipairs(constructed)do check(item.outer==frame.WidgetTree and item.outer~=owner.WidgetTree,'controls owned by replay tree')end
local function surfaceFor(item)
    for _,child in ipairs(body.children)do
        if child.class=='Border' and child.slot.Offsets.Top==96 and child.slot.Offsets.Left==item.slot.Offsets.Left then return child end
    end
end
for i=2,#created-1 do
    local item=created[i];local surface=surfaceFor(item)
    check(item.HideTrim and item.HoverEffect==false and item.Trim.visibility==1,'palette trim and palette hover animation removed')
    check(item.Binding_Button.background.A==0 and item.Binding_Button.color.A==1,'native background transparent while content remains visible')
    check(surface and surface.visibility==3,'owned paint surface never intercepts native click target')
end
local native=created[2].Binding_Button;local primary=surfaceFor(created[2]);local normalGreen=primary.color.G
check(normalGreen>surfaceFor(created[3]).color.G,'primary play control has mint emphasis')
local dummy={playing=false,speed=1,time=0,duration=60}
native.hovered=true;hud.refreshControls();check(primary.color.G>normalGreen,'hover has distinct custom feedback')
native.pressed=true;hud.update(dummy);check(primary.color.G<normalGreen,'press takes precedence over hover')
native.enabled=false;hud.update(dummy);check(primary.color.A<1,'disabled takes precedence over pressed')
native.enabled=true;native.hovered=false;native.pressed=false;hud.update(dummy)
check(primary.color.G==normalGreen,'normal appearance restored on pointer exit')
local paintWrites=primary.paintWrites;hud.update(dummy);check(primary.paintWrites==paintWrites,'unchanged button state avoids repaint writes')
local expected={{'toggle'},{'seek-relative',-5},{'seek-relative',5},{'speed-next'},{'close'}}
for i=2,#created-1 do
    local item=created[i];check(item.visibility==0 and item.slot.Offsets.Bottom>=42,'native buttons have usable hit targets')
    check(clicked({get=function()return item end})==nil,'button hook returns no override')
end
for i,command in ipairs(expected)do check(commands[i][1]==command[1] and commands[i][2]==command[2],'native button sends expected command')end
local function findText(value)for _,item in ipairs(constructed)do if item.text==value then return item end end end
local function field(label)
    local title=findText(label)
    for _,item in ipairs(body.children)do if item.class=='TextBlock' and item.slot.Offsets.Left==title.slot.Offsets.Left and item.slot.Offsets.Top==34 then return item end end
end
local progress;for _,item in ipairs(constructed)do if item.class=='ProgressBar' then progress=item end end
check(progress and progress.visibility==3,'progress paint does not intercept timeline capture')
hud.update({playing=true,speed=.5,time=65,duration=120})
check(created[2].content.text=='Pause' and created[5].content.text=='0.5x speed','playback state updates labels')
local timer=findText('1:05 / 2:00');local remaining=findText('0:55 remaining')
check(timer and remaining,'timer has readable elapsed duration and remaining')
check(findText('REPLAY PLAYING')~=nil,'playback status clearly visible')
check(math.abs(progress.percent-65/120)<.001,'progress follows replay clock')
local writes=timer.writes;local percentWrites=progress.percentWrites
hud.update({playing=true,speed=.5,time=65,duration=120})
check(timer.writes==writes and progress.percentWrites==percentWrites,'unchanged timer and progress avoid Slate invalidation')
check(field('SCORE').text=='—' and field('HITS').text=='—','missing historical metrics never become invented zeros')
local state={playing=true,speed=1,time=65,duration=120}
hud.update(state,{score=123.5,shots=8,hits=6,kills=2,damage=99.2,seconds=999},true)
check(field('SCORE').text=='123.5' and field('ACCURACY').text=='75.0%' and field('HITS').text=='6' and field('SHOTS').text=='8' and field('KILLS').text=='2' and field('DAMAGE').text=='99.2','known recorded counters render directly')
check(marker.visibility==3,'only confirmed hit enables non-interactive marker')
check(timer.text=='1:05 / 2:00' and remaining.text=='0:55 remaining','timer comes from replay clock not stats seconds')
local scoreWrites=field('SCORE').writes
hud.update(state,{score=123.5,shots=8,hits=6,kills=2,damage=99.2},false)
check(field('SCORE').writes==scoreWrites and marker.visibility==2,'unchanged counters avoid invalidation and marker clears')
hud.update(state,{score=0,shots=0,hits=0,kills=0,damage=0},{confirmedHit=true})
check(field('SCORE').text=='0' and field('ACCURACY').text=='—' and field('HITS').text=='0','known zeros retained with undefined zero-shot accuracy')
check(marker.visibility==2,'truthy non-boolean feedback never fabricates hit confirmation')
hud.update(state,{score=0/0,shots=5,hits=nil,damage=math.huge})
check(field('SCORE').text=='—' and field('DAMAGE').text=='—' and field('SHOTS').text=='5','invalid or missing counters remain unknown')
hud.update(state,{score=-42,hits=-1})
check(field('SCORE').text=='-42' and field('HITS').text=='\226\128\148','negative recorded score retained while negative counters stay unknown')
state.time=121;state.playing=false;hud.update(state)
check(timer.text=='2:01 / 2:00' and remaining.text=='0:00 remaining' and progress.percent==1,'progress and remaining clamp at end')
check(findText('REPLAY FINISHED')~=nil,'finished state distinct from pause')
state.time=0;state.duration=0;hud.update(state)
check(progress.percent==0 and remaining.text=='0:00 remaining','zero duration is safe')
local track=created[#created];local nativeSlider=track.Binding_Slider
check(track.class=='PalettedSlider_C' and nativeSlider.Locked==false and nativeSlider.MinValue==0 and nativeSlider.MaxValue==1,'actual native slider has normalized unlocked range')
check(nativeSlider.SliderBarColor.A==0 and nativeSlider.SliderHandleColor.G>0,'native thumb custom tint retains owned progress track')
state.duration=120;state.time=0;local syncCount=#commands;hud.update(state)
check(#commands==syncCount,'programmatic slider refresh cannot send seek')
local function slide(value)sliderChanged({get=function()return track end},{get=function()return value end})end
realTime=1;slide(.5);check(commands[#commands][1]=='seek' and commands[#commands][2]==60,'native value callback sends midpoint absolute seek without mouse polling')
local dragCount=#commands;realTime=1.04;slide(.25);check(#commands==dragCount,'native drag events throttled')
realTime=1.11;hud.refreshControls();check(commands[#commands][2]==30,'pending drag position flushes even when paused')
realTime=1.14;slide(.75);realTime=1.22;hud.refreshControls();check(commands[#commands][2]==90,'final native slider value is flushed after release')
realTime=2;slide(-.2);check(commands[#commands][2]==0,'native values clamp below timeline start')
realTime=3;slide(1.5);check(commands[#commands][2]==120,'native values clamp above timeline end')
local validCount=#commands;slide(0/0);slide(math.huge);check(#commands==validCount,'invalid native values rejected')
state.playing=true;hud.update(state);realTime=4;slide(.5);check(commands[#commands][1]=='seek' and commands[#commands][2]==60,'playing seek does not toggle playback')
state.duration=0;hud.update(state);validCount=#commands;slide(.7);realTime=5;hud.refreshControls();check(#commands==validCount,'unknown duration cannot seek')
hud.update(state,{damage=.00002136});check(tonumber(field('DAMAGE').text)==.00002136,'tiny nonzero damage retains significant digits')
hud.update(state,{damage=.3309929});check(field('DAMAGE').text=='0.331','fractional damage retains four significant digits without unit conversion')
local scoreValue=field('SCORE')
hud.update(state,{},false,1,-42);check(scoreValue.text=='-42' and findText('FINAL SCORE')~=nil,'known negative result is explicitly labeled final score')
hud.update(state,{score=0},false,1,100);check(scoreValue.text=='0' and findText('SCORE')~=nil,'measured zero score takes precedence over final result')
hud.update(state,{},false,1,0/0);check(scoreValue.text=='\226\128\148' and findText('SCORE')~=nil,'invalid result never fabricates a score')
hud.close();hud.close();local closedPaint=primary.paintWrites;native.hovered=true;hud.refreshControls();check(primary.paintWrites==closedPaint,'closed HUD cannot repaint');check(frame.removed==1,'close removes host exactly once')
local count=#commands;clicked({get=function()return created[2] end});slide(.4);check(#commands==count,'closed controls cannot send commands')
local nextHud=M.create(owner,function()end);check(hookCount==2,'button and slider hooks each registered once across HUD instances');nextHud.close()
local texture=obj({});local crosshair={bHitmarkers=true,HitmarkerTime=.2,HitmarkerScale=99,HitmarkerColor={X=.1,Y=.7,Z=.3}}
local weapon=obj({WeaponSettingsNative={Crosshair=crosshair},CachedMainHitmarkers={CrosshairTexture=texture,Width=48,Height=24}})
player.MyCharacter=obj({GetCurrentWeapon=function()return weapon end})
local skinned=M.create(owner,function()end);local skinMarker=skinned.view.WidgetTree.RootWidget.children[2]
check(skinMarker.class=='Image' and skinMarker.texture==texture and skinMarker.matchSize==false,'cached local hitmarker texture is used directly')
check(skinMarker.slot.Offsets.Right==48 and skinMarker.slot.Offsets.Bottom==24 and skinMarker.slot.Offsets.Left==-24,'cached dimensions used without guessed second scaling')
check(skinMarker.color.R==.1 and skinMarker.color.G==.7 and skinMarker.color.B==.3,'local linear hitmarker color preserved')
local skinState={playing=true,speed=1,time=10,duration=60}
skinned.update(skinState,nil,true,3);check(skinMarker.visibility==3,'confirmed event displays cached skin')
skinState.time=10.1;skinned.update(skinState,nil,false,3);check(skinMarker.visibility==3,'configured marker duration persists across frames')
skinState.time=10.21;skinned.update(skinState,nil,false,3);check(skinMarker.visibility==2,'marker hides after configured duration')
skinned.update(skinState,nil,true,3);skinState.time=10.22;skinned.update(skinState,nil,false,4);check(skinMarker.visibility==2,'seek transport revision clears pending marker')
skinned.update(skinState,nil,true,4);skinState.playing=false;skinned.update(skinState,nil,false,4);check(skinMarker.visibility==2,'pause clears pending marker')
skinState.playing=true;skinned.update(skinState,nil,true,4);skinState.time=9;skinned.update(skinState,nil,false,4);check(skinMarker.visibility==2,'backward time clears pending marker')
skinned.close()
crosshair.bHitmarkers=false;weapon.CachedMainHitmarkers=nil
local disabled=M.create(owner,function()end);local disabledMarker=disabled.view.WidgetTree.RootWidget.children[2]
disabled.update(skinState,nil,true,5);check(disabledMarker.visibility==2,'disabled local markers also suppress fallback X');disabled.close()
local regularTexture=obj({})
weapon.CachedMainXHair={CrosshairTexture=regularTexture,Width=30,Height=18}
crosshair.Color={X=.8,Y=.2,Z=.6};crosshair.Scale=4
local regularHud=M.create(owner,function()end);local regularImage=regularHud.view.WidgetTree.RootWidget.children[2]
check(regularImage.class=='Image' and regularImage.texture==regularTexture and regularImage.visibility==3,'local regular crosshair is visible without a hit')
check(regularImage.slot.Offsets.Right==30 and regularImage.slot.Offsets.Bottom==18,'regular crosshair uses cached dimensions without multiplying configured scale')
check(regularImage.color.R==.8 and regularImage.color.B==.6,'regular crosshair preserves local color')
check(regularHud.view.WidgetTree.RootWidget.children[3].visibility==2,'hitmarker remains separately controlled above regular crosshair');regularHud.close()
crosshair.Scale=0
local hiddenCross=M.create(owner,function()end);check(#hiddenCross.view.WidgetTree.RootWidget.children==2,'zero crosshair scale creates no replacement');hiddenCross.close()
crosshair.Scale=1;weapon.CachedMainXHair=nil
local missingCross=M.create(owner,function()end);check(#missingCross.view.WidgetTree.RootWidget.children==2,'missing crosshair cache creates no fake replacement');missingCross.close()
print('Replay HUD: '..checks..' checks passed')
